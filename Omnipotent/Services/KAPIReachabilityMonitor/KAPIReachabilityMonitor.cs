using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Entities;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAPI;
using Omnipotent.Services.KliveBot_Discord;
using Omnipotent.Services.PortForwardManager;

namespace Omnipotent.Services.KAPIReachabilityMonitor
{
    // Continuously watches two things that silently break public reachability of the KliveAPI:
    //  1. The server's public (WAN) IP changing -> DNS needs updating (only Klives can fix this).
    //  2. UPnP port mappings for 5000 (HTTP) / 443 (HTTPS) dropping -> auto re-forwarded + Klives notified.
    public class KAPIReachabilityMonitor : OmniService
    {
        private const string ResolvedButtonId = "KAPIReachabilityResolved";
        private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan ReminderInterval = TimeSpan.FromHours(6);

        private KliveBotDiscord kliveBotDiscord;
        private PortForwardManager.PortForwardManager portForwardManager;
        private ReachabilityState state;

        public KAPIReachabilityMonitor()
        {
            name = "KAPI Reachability Monitor";
            threadAnteriority = ThreadAnteriority.Standard;
        }

        protected override async void ServiceMain()
        {
            try
            {
                // Acquire dependencies (retry until available, as NotificationsService does).
                kliveBotDiscord = (KliveBotDiscord)(await GetServicesByType<KliveBotDiscord>())[0];
                portForwardManager = (PortForwardManager.PortForwardManager)(await GetServicesByType<PortForwardManager.PortForwardManager>())[0];

                await GetDataHandler().CreateDirectory(OmniPaths.GetPath(OmniPaths.GlobalPaths.KAPIReachabilityMonitorDirectory));
                state = await LoadState();

                // One persistent handler for the "Resolved" button. Matching by a stable prefix means
                // buttons keep working even on messages sent before a restart.
                kliveBotDiscord.Client.ComponentInteractionCreated += async (s, e) =>
                {
                    try
                    {
                        if (e.Id != null && e.Id.StartsWith(ResolvedButtonId))
                        {
                            DiscordInteractionResponseBuilder builder = new();
                            builder.WithContent("Resolved — thanks! KAPI reachability reminders for this IP change have been cleared.");
                            await e.Interaction.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource, builder);

                            state.PendingChange = null;
                            await SaveState();
                            await ServiceLog("Public IP change marked resolved by Klives.");
                        }
                    }
                    catch (Exception ex)
                    {
                        await ServiceLogError(ex, "Failed to handle KAPI reachability Resolved button.", false);
                    }
                };

                // "Every startup till resolved": if there's an unresolved change, re-notify immediately.
                if (state.PendingChange != null)
                {
                    await NotifyIpChange(state.PendingChange);
                    state.PendingChange.LastNotifiedAt = DateTime.Now;
                    await SaveState();
                }

                await ServiceLog("KAPI Reachability Monitor started.");

                while (IsServiceActive())
                {
                    try
                    {
                        await CheckPublicIp();
                        await CheckPortForwards();
                    }
                    catch (Exception ex)
                    {
                        await ServiceLogError(ex, "KAPI reachability check failed", false);
                    }
                    await Task.Delay(CheckInterval);
                }
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Error starting KAPI Reachability Monitor.");
            }
        }

        private async Task CheckPublicIp()
        {
            string current = await CertificateInstaller.GetPublicIpAddress();
            if (string.IsNullOrWhiteSpace(current))
            {
                // Network blip / lookup failed — don't treat as a change.
                return;
            }

            if (string.IsNullOrWhiteSpace(state.LastKnownPublicIP))
            {
                // First run: record baseline, no notification.
                state.LastKnownPublicIP = current;
                await SaveState();
                await ServiceLog($"Recorded baseline public IP: {current}");
                return;
            }

            if (current != state.LastKnownPublicIP)
            {
                if (state.PendingChange == null)
                {
                    state.PendingChange = new PendingIpChange
                    {
                        OldIP = state.LastKnownPublicIP,
                        NewIP = current,
                        DetectedAt = DateTime.Now
                    };
                }
                else
                {
                    // Changed again while still unresolved — keep the original OldIP, track the newest.
                    state.PendingChange.NewIP = current;
                }

                state.LastKnownPublicIP = current;
                await NotifyIpChange(state.PendingChange);
                state.PendingChange.LastNotifiedAt = DateTime.Now;
                await SaveState();
                await ServiceLog($"Public IP changed to {current}. Notified Klives that DNS needs updating.");
            }
            else if (state.PendingChange != null && (DateTime.Now - state.PendingChange.LastNotifiedAt) >= ReminderInterval)
            {
                // "Every 6 hours till resolved" reminder.
                await NotifyIpChange(state.PendingChange);
                state.PendingChange.LastNotifiedAt = DateTime.Now;
                await SaveState();
            }
        }

        private async Task NotifyIpChange(PendingIpChange change)
        {
            var embed = KliveBotDiscord.MakeSimpleEmbed("KAPI Reachability: Public IP changed!",
                $"The server's public IP has changed. **DNS needs updating** so the API stays reachable.\n\n" +
                $"Old IP: `{change.OldIP}`\nNew IP: `{change.NewIP}`\n\n" +
                $"Update the A record for {KliveAPI.KliveAPI.domainName} to the new IP, then click Resolved.",
                DiscordColor.Orange);
            embed.AddComponents(new DiscordButtonComponent(ButtonStyle.Success, ResolvedButtonId, "Resolved"));
            await kliveBotDiscord.SendMessageToKlives(embed);
        }

        private async Task CheckPortForwards()
        {
            // Only act if UPnP is enabled on the router.
            if (!await portForwardManager.IsUpnpAvailable())
            {
                return;
            }

            var added = new List<string>();
            if (await portForwardManager.EnsurePortForwarded(KliveAPI.KliveAPI.apiHTTPPORT, KliveAPI.KliveAPI.apiHTTPPORT, "TCP", "KliveAPIHTTP"))
            {
                added.Add($"{KliveAPI.KliveAPI.apiHTTPPORT} (KliveAPIHTTP)");
            }
            if (await portForwardManager.EnsurePortForwarded(KliveAPI.KliveAPI.apiPORT, KliveAPI.KliveAPI.apiPORT, "TCP", "KliveAPIHTTPS"))
            {
                added.Add($"{KliveAPI.KliveAPI.apiPORT} (KliveAPIHTTPS)");
            }

            if (added.Any())
            {
                var embed = KliveBotDiscord.MakeSimpleEmbed("KAPI Reachability: Re-added port forwards",
                    "UPnP mappings were missing and have been automatically re-forwarded:\n- " + string.Join("\n- ", added),
                    DiscordColor.Green);
                await kliveBotDiscord.SendMessageToKlives(embed);
                await ServiceLog("Re-added missing UPnP port forwards: " + string.Join(", ", added));
            }
        }

        private async Task<ReachabilityState> LoadState()
        {
            string path = OmniPaths.GetPath(OmniPaths.GlobalPaths.KAPIReachabilityMonitorStateFile);
            try
            {
                if (File.Exists(path))
                {
                    return await GetDataHandler().ReadAndDeserialiseDataFromFile<ReachabilityState>(path) ?? new ReachabilityState();
                }
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Failed to load KAPI reachability state, starting fresh.", false);
            }
            return new ReachabilityState();
        }

        private async Task SaveState()
        {
            string path = OmniPaths.GetPath(OmniPaths.GlobalPaths.KAPIReachabilityMonitorStateFile);
            await GetDataHandler().SerialiseObjectToFile(path, state);
        }
    }

    public class ReachabilityState
    {
        public string LastKnownPublicIP { get; set; }
        public PendingIpChange PendingChange { get; set; }
    }

    public class PendingIpChange
    {
        public string OldIP { get; set; }
        public string NewIP { get; set; }
        public DateTime DetectedAt { get; set; }
        public DateTime LastNotifiedAt { get; set; }
    }
}
