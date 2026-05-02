using DSharpPlus.Entities;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Profiles;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAPI;
using Omnipotent.Services.KliveBot_Discord;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Omnipotent.Services.OmniDefence
{
    /// <summary>
    /// Cyberdefence service for Omnipotent. Tracks every API request, every
    /// authentication event, every KM profile action, and every IP that talks
    /// to KliveAPI. Provides Klives-only routes to query/filter the data and
    /// to ban / watch / honeypot / scan attacker IPs.
    /// </summary>
    public class OmniDefence : OmniService
    {
        private OmniDefenceStore store = null!;
        private IpThreatTracker tracker = null!;
        private OmniDefenceScanner scanner = new();

        // Discord notification dedupe state (separate from DB so it survives intra-request scenarios fast)
        private readonly ConcurrentDictionary<string, byte> recentlyScannedIps = new();
        private readonly ConcurrentDictionary<string, DateTime> lastScanByIp = new();

        // Cached set of routes registered as honeypots (route -> response kind)
        private readonly ConcurrentDictionary<string, string> honeypotRoutes = new(StringComparer.OrdinalIgnoreCase);

        public OmniDefence()
        {
            name = "OmniDefence";
            threadAnteriority = ThreadAnteriority.High;
        }

        public OmniDefenceStore Store => store;
        public IpThreatTracker Tracker => tracker;
        public OmniDefenceScanner Scanner => scanner;

        protected override async void ServiceMain()
        {
            try
            {
                Directory.CreateDirectory(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniDefenceDirectory));
                store = new OmniDefenceStore(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniDefenceDatabaseFile));
                await store.InitializeAsync();

                tracker = new IpThreatTracker(store);
                await tracker.LoadAsync();

                foreach (var hp in await store.ListHoneypotRoutesAsync())
                {
                    honeypotRoutes[hp.Route] = hp.ResponseKind;
                }

                await ServiceLog($"OmniDefence active. Loaded {tracker.All().Count()} IP records, {honeypotRoutes.Count} honeypot routes.");

                // Periodic flush so threat scores survive restarts.
                _ = PeriodicFlushLoop();

                await OmniDefenceRoutes.RegisterAsync(this);
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "OmniDefence failed to start.");
            }
        }

        private async Task PeriodicFlushLoop()
        {
            while (cancellationToken == null || !cancellationToken.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken?.Token ?? CancellationToken.None); }
                catch (OperationCanceledException) { return; }
                try { await tracker.PersistAllDirtyAsync(); }
                catch (Exception ex) { await ServiceLogError(ex, "OmniDefence periodic flush failed."); }
            }
        }

        // ------------------------------------------------------------------
        //  Pre-dispatch hook from KliveAPI
        // ------------------------------------------------------------------

        public IpThreatTracker.GateDecision EvaluateRequestGate(string ip, string route)
        {
            if (string.IsNullOrEmpty(ip)) return IpThreatTracker.GateDecision.Allow;
            // Honeypot routes always honeypot regardless of IP status.
            if (honeypotRoutes.ContainsKey(route)) return IpThreatTracker.GateDecision.Honeypot;
            return tracker.Evaluate(ip);
        }

        public bool IsHoneypotRoute(string route) => honeypotRoutes.ContainsKey(route);

        // ------------------------------------------------------------------
        //  Recording API (called from KliveAPI ProcessRequestAsync / KMProfileManager etc.)
        // ------------------------------------------------------------------

        public async Task RecordRequestAsync(RequestRow row, RequestOutcome outcome)
        {
            try
            {
                if (!string.IsNullOrEmpty(row.Ip))
                {
                    var rec = tracker.RecordOutcome(row.Ip!, outcome);
                    _ = MaybeAlertAsync(rec, row);
                }
                await store.InsertRequestAsync(row);
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "OmniDefence RecordRequestAsync failed.");
            }
        }

        public async Task RecordAuthEventAsync(AuthEventRow row)
        {
            try { await store.InsertAuthEventAsync(row); }
            catch (Exception ex) { await ServiceLogError(ex, "OmniDefence RecordAuthEventAsync failed."); }
        }

        public async Task RecordProfileAction(KMProfileManager.KMProfile? profile, string category, string action, object? detail = null, string? ip = null)
        {
            try
            {
                var row = new ProfileActionRow
                {
                    UtcTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ProfileId = profile?.UserID,
                    ProfileName = profile?.Name,
                    Ip = ip,
                    Category = category ?? "",
                    Action = action ?? "",
                    DetailJson = detail == null ? null : JsonConvert.SerializeObject(detail)
                };
                await store.InsertProfileActionAsync(row);
            }
            catch (Exception ex) { await ServiceLogError(ex, "OmniDefence RecordProfileAction failed."); }
        }

        public async Task RecordIpEventAsync(IpEventRow row)
        {
            try { await store.InsertIpEventAsync(row); }
            catch (Exception ex) { await ServiceLogError(ex, "OmniDefence RecordIpEventAsync failed."); }
        }

        // ------------------------------------------------------------------
        //  Discord notifier (replaces the per-request spam)
        // ------------------------------------------------------------------

        private async Task MaybeAlertAsync(IpRecord rec, RequestRow row)
        {
            try
            {
                if (rec == null) return;
                if (row.DenyReason == null && row.StatusCode < 400) return; // only alert on suspicious activity

                bool first;
                int? escalation = null;
                lock (rec)
                {
                    first = rec.FirstAlertedUtc == null && rec.UnauthAttempts >= tracker.FirstAlertThreshold;
                    if (first)
                    {
                        rec.FirstAlertedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        rec.LastAlertedUtc = rec.FirstAlertedUtc;
                        rec.EscalationLevel = 1;
                    }
                    else if (rec.EscalationLevel < 2 && rec.UnauthAttempts >= tracker.Escalation2Threshold)
                    {
                        rec.EscalationLevel = 2;
                        escalation = 2;
                        rec.LastAlertedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    }
                    else if (rec.EscalationLevel < 3 && rec.UnauthAttempts >= tracker.Escalation3Threshold)
                    {
                        rec.EscalationLevel = 3;
                        escalation = 3;
                        rec.LastAlertedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    }
                }

                if (first)
                {
                    var builder = KliveBotDiscord.MakeSimpleEmbed(
                        $"OmniDefence: New attacker detected ({rec.Ip})",
                        BuildAlertDescription(rec, row, "First detection"),
                        DiscordColor.Yellow);
                    await SendDiscordBuilderSafeAsync(builder);
                }
                if (escalation.HasValue)
                {
                    var color = escalation == 3 ? DiscordColor.Red : DiscordColor.Orange;
                    var builder = KliveBotDiscord.MakeSimpleEmbed(
                        $"OmniDefence: Escalation L{escalation} ({rec.Ip})",
                        BuildAlertDescription(rec, row, $"Escalated to level {escalation}"),
                        color);
                    await SendDiscordBuilderSafeAsync(builder);
                }
            }
            catch (Exception ex) { await ServiceLogError(ex, "OmniDefence MaybeAlertAsync failed."); }
        }

        private static string BuildAlertDescription(IpRecord rec, RequestRow row, string reason)
        {
            return $"**Reason:** {reason}\n" +
                   $"**IP:** `{rec.Ip}`\n" +
                   $"**Threat score:** {rec.ThreatScore:F0}\n" +
                   $"**Unauth attempts:** {rec.UnauthAttempts}\n" +
                   $"**Total requests:** {rec.TotalRequests}\n" +
                   $"**Status:** {rec.Status}\n" +
                   $"**Last route:** `{row.Method} {row.Route}`\n" +
                   $"**User-agent:** {row.UserAgent ?? "(none)"}";
        }

        public async Task SendBlockNotificationAsync(IpRecord rec, string actor)
        {
            try
            {
                var builder = KliveBotDiscord.MakeSimpleEmbed(
                    $"OmniDefence: IP blocked ({rec.Ip})",
                    $"**Actor:** {actor}\n" +
                    $"**Reason:** {rec.LastBlockReason ?? "(none)"}\n" +
                    $"**Threat score:** {rec.ThreatScore:F0}\n" +
                    $"**Unauth attempts:** {rec.UnauthAttempts}",
                    DiscordColor.DarkRed);
                await SendDiscordBuilderSafeAsync(builder);
            }
            catch (Exception ex) { await ServiceLogError(ex, "OmniDefence SendBlockNotificationAsync failed."); }
        }

        private async Task SendDiscordBuilderSafeAsync(DiscordMessageBuilder builder)
        {
            try
            {
                await ExecuteServiceMethod<KliveBotDiscord>("SendMessageToKlives", builder);
            }
            catch { }
        }

        // ------------------------------------------------------------------
        //  Honeypot helpers (used by KliveAPI integration to pick a response)
        // ------------------------------------------------------------------

        public string GenerateHoneypotResponse(string ip)
        {
            return JunkPayloadGenerator.GenerateJunkJson(ip ?? "unknown");
        }

        public Task RegisterHoneypotRouteAsync(string route, string? note)
        {
            honeypotRoutes[route] = "JunkJson";
            return store.UpsertHoneypotRouteAsync(new HoneypotRouteRow
            {
                Route = route,
                CreatedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ResponseKind = "JunkJson",
                Note = note
            });
        }

        public Task RemoveHoneypotRouteAsync(string route)
        {
            honeypotRoutes.TryRemove(route, out _);
            return store.DeleteHoneypotRouteAsync(route);
        }

        public IReadOnlyDictionary<string, string> HoneypotRouteSnapshot() =>
            new Dictionary<string, string>(honeypotRoutes, StringComparer.OrdinalIgnoreCase);

        // ------------------------------------------------------------------
        //  Convenience helpers
        // ------------------------------------------------------------------

        public static string ExtractClientIp(HttpListenerRequest req)
        {
            if (req == null) return "";
            try
            {
                string? remote = req.RemoteEndPoint?.Address?.ToString();
                // Honour X-Forwarded-For only when the request hits us locally
                // (i.e. a known reverse proxy on this box). Anything else and we
                // trust the socket peer.
                if (remote == "127.0.0.1" || remote == "::1")
                {
                    string? xff = req.Headers["X-Forwarded-For"];
                    if (!string.IsNullOrWhiteSpace(xff))
                    {
                        var first = xff.Split(',')[0].Trim();
                        if (!string.IsNullOrEmpty(first)) return first;
                    }
                }
                return remote ?? "";
            }
            catch { return ""; }
        }

        public static string HashBody(byte[] bodyBytes)
        {
            if (bodyBytes == null || bodyBytes.Length == 0) return "";
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(bodyBytes));
        }

        /// <summary>
        /// Throttles port scans to one per IP per hour.
        /// </summary>
        public bool TryAcquireScanSlot(string ip)
        {
            var now = DateTime.UtcNow;
            if (lastScanByIp.TryGetValue(ip, out var last) && (now - last) < TimeSpan.FromHours(1))
            {
                return false;
            }
            lastScanByIp[ip] = now;
            return true;
        }
    }
}
