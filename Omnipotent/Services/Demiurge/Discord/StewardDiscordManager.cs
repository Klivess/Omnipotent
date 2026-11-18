using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveBot_Discord;
using System.Collections.Concurrent;

namespace Omnipotent.Services.Demiurge.Discord
{
    /// <summary>
    /// Per-Steward Discord integration (§10.2): one #steward channel per Steward, button
    /// approvals (Approve/Deny/Discuss) racing the website's gate resolution, plain-reply routing
    /// into the Commander's context, and twice-daily reports. Reuses KliveBotDiscord's single
    /// DiscordClient rather than standing up its own.
    ///
    /// The approval flow uses a TaskCompletionSource wired straight into StewardGateManager, so a
    /// Discord button press and a website click race — first responder wins — the same pattern
    /// HostControl's ApprovalBroker already uses for computer-use approvals.
    /// </summary>
    public class StewardDiscordManager
    {
        public const string ActiveCategoryName = "Active Stewards";
        public const string PausedCategoryName = "Paused Stewards";
        public const string ArchivedCategoryName = "Archived Stewards";

        private readonly Demiurge parent;
        private readonly KliveBotDiscord discord;
        private readonly Action<string> log;

        // channelID → stewardID, for routing replies and button interactions.
        private readonly ConcurrentDictionary<ulong, string> channelToSteward = new();
        // Discord button custom-id → (stewardID, gateID, decision).
        private readonly ConcurrentDictionary<string, (string stewardID, string gateID, GateDecision decision)> buttonMap = new(StringComparer.Ordinal);

        public StewardDiscordManager(Demiurge parent, KliveBotDiscord discord, Action<string> log)
        {
            this.parent = parent;
            this.discord = discord;
            this.log = log ?? (_ => { });
        }

        /// <summary>Wires the shared client's events and indexes existing Steward channels.</summary>
        public async Task InitialiseAsync()
        {
            foreach (var p in parent.Store.ListStewards())
                if (p.DiscordChannelID != 0)
                    channelToSteward[p.DiscordChannelID] = p.StewardID;

            discord.Client.MessageCreated += OnMessageCreated;
            discord.Client.ComponentInteractionCreated += OnComponentInteraction;

            // This is deliberately a startup reconciliation rather than a migration flag: it
            // repairs channels created by older versions, including channels whose names were
            // changed to archived-* when completion used to be implemented as a rename.
            await ReconcileStewardChannelsAsync();
        }

        // Keep the old fire-and-forget entry point for integrations compiled against the prior
        // manager API; Omnipotent itself uses InitialiseAsync so startup waits for repair.
        public void Initialise() => _ = InitialiseAsync();

        // ── channel lifecycle ──

        public async Task<ulong> CreateStewardChannelAsync(Steward steward)
        {
            if (steward.DiscordChannelID != 0) return steward.DiscordChannelID;
            var guild = await discord.Client.GetGuildAsync(OmniPaths.DiscordServerContainingKlives);
            var category = await GetOrCreateCategoryAsync(guild, CategoryNameFor(steward.Status));
            string name = ChannelPrefix + Sanitise(steward.Name);
            var channel = await guild.CreateTextChannelAsync(name, parent: category, topic: $"Steward: {steward.Goal}");
            steward.DiscordChannelID = channel.Id;
            parent.Store.SaveSteward(steward);
            channelToSteward[channel.Id] = steward.StewardID;

            await channel.SendMessageAsync(KliveBotDiscord.MakeSimpleEmbed(
                $"Steward started: {steward.Name}",
                $"Goal: {steward.Goal}\nToken budget: ${steward.TokenBudgetUsd:0.##} · Money budget: ${steward.MoneyBudgetUsd:0.##}\n\nReply here to talk to the Commander. Approvals will appear as buttons.",
                DiscordColor.Teal));
            log($"Created Discord channel #{name} for Steward {steward.StewardID}.");
            return channel.Id;
        }

        /// <summary>Renames the Steward's Discord channel to match a Steward rename (best-effort).</summary>
        public async Task RenameStewardChannelAsync(Steward steward)
        {
            if (steward.DiscordChannelID == 0) return;
            try
            {
                var channel = await discord.Client.GetChannelAsync(steward.DiscordChannelID);
                string name = ChannelPrefix + Sanitise(steward.Name);
                await channel.ModifyAsync(c => c.Name = name);
            }
            catch (Exception ex) { log($"Rename channel failed for {steward.StewardID}: {ex.Message}"); }
        }

        public async Task ArchiveChannelAsync(Steward steward)
        {
            await SyncStewardChannelAsync(steward, "Steward completed — channel archived.");
        }

        /// <summary>Moves an existing Steward channel to the category for its current status.</summary>
        public async Task SyncStewardChannelAsync(Steward steward, string? announcement = null)
        {
            if (steward.DiscordChannelID == 0) return;
            try
            {
                var guild = await discord.Client.GetGuildAsync(OmniPaths.DiscordServerContainingKlives);
                var category = await GetOrCreateCategoryAsync(guild, CategoryNameFor(steward.Status));
                var channel = await discord.Client.GetChannelAsync(steward.DiscordChannelID);
                if (channel.ParentId != category.Id)
                    await channel.ModifyAsync(c => c.Parent = category);
                // Channels made before Projects became Demiurge are #project-<name> with a "Project:"
                // topic; bring them to today's names in one edit (Discord rate-limits renames).
                if (channel.Name.StartsWith(LegacyChannelPrefix, StringComparison.Ordinal)
                    || (channel.Topic?.StartsWith("Project: ", StringComparison.Ordinal) ?? false))
                {
                    string name = ChannelPrefix + Sanitise(steward.Name);
                    string topic = "Steward: " + steward.Goal;
                    await channel.ModifyAsync(c => { c.Name = name; c.Topic = topic; });
                }
                if (!string.IsNullOrWhiteSpace(announcement))
                    await channel.SendMessageAsync(announcement);
            }
            catch (Exception ex) { log($"Sync channel failed for {steward.StewardID}: {ex.Message}"); }
        }

        /// <summary>Repairs every persisted Steward channel after a restart.</summary>
        public async Task ReconcileStewardChannelsAsync()
        {
            foreach (var steward in parent.Store.ListStewards())
            {
                if (steward.DiscordChannelID == 0) continue;
                await SyncStewardChannelAsync(steward);
            }
        }

        internal static string CategoryNameFor(StewardStatus status) => status switch
        {
            StewardStatus.Paused or StewardStatus.BudgetPaused or StewardStatus.Blocked
                => PausedCategoryName,
            StewardStatus.Completed or StewardStatus.Archived => ArchivedCategoryName,
            _ => ActiveCategoryName,
        };

        private static async Task<DiscordChannel> GetOrCreateCategoryAsync(DiscordGuild guild, string name)
        {
            var channels = await guild.GetChannelsAsync();
            DiscordChannel? Find(string categoryName) => channels.FirstOrDefault(c => c.Type == ChannelType.Category
                && string.Equals(c.Name, categoryName, StringComparison.OrdinalIgnoreCase));
            var category = Find(name);
            if (category != null) return category;
            // The category from before Projects became Demiurge ("Active Projects") is renamed in
            // place, so its channels, order and permissions carry over.
            string? legacy = LegacyCategoryNameFor(name);
            var old = legacy == null ? null : Find(legacy);
            if (old != null)
            {
                await old.ModifyAsync(c => c.Name = name);
                return old;
            }
            return await guild.CreateChannelCategoryAsync(name);
        }

        internal const string ChannelPrefix = "steward-";
        internal const string LegacyChannelPrefix = "project-";

        internal static string? LegacyCategoryNameFor(string name) => name switch
        {
            ActiveCategoryName => "Active Projects",
            PausedCategoryName => "Paused Projects",
            ArchivedCategoryName => "Archived Projects",
            _ => null,
        };

        // ── approvals ──

        /// <summary>
        /// Posts an approval card to the Steward's channel and resolves the gate on the first
        /// button press (racing the website). Fire-and-forget: it doesn't block the agent, which
        /// awaits the gate's own TaskCompletionSource in StewardGateManager.
        /// </summary>
        public async Task PostApprovalAsync(Steward steward, StewardGate gate)
        {
            if (steward.DiscordChannelID == 0) return;
            DiscordChannel channel;
            try { channel = await discord.Client.GetChannelAsync(steward.DiscordChannelID); }
            catch { return; }

            var embed = KliveBotDiscord.MakeSimpleEmbed(
                $"Approval needed: {gate.Title}",
                $"{gate.Description}\n\n**Why:** {gate.Rationale}",
                DiscordColor.Orange);

            string baseId = "gate_" + gate.GateID;
            buttonMap[baseId + "_a"] = (steward.StewardID, gate.GateID, GateDecision.Approve);
            buttonMap[baseId + "_d"] = (steward.StewardID, gate.GateID, GateDecision.Deny);
            buttonMap[baseId + "_x"] = (steward.StewardID, gate.GateID, GateDecision.Discuss);

            embed.AddComponents(
                new DiscordButtonComponent(ButtonStyle.Success, baseId + "_a", "Approve"),
                new DiscordButtonComponent(ButtonStyle.Danger, baseId + "_d", "Deny"),
                new DiscordButtonComponent(ButtonStyle.Secondary, baseId + "_x", "Discuss"));

            // @mention Klives so a blocking approval reaches him on his phone (mention in content;
            // embeds don't ping). The agent is suspended awaiting this — it matters that he sees it.
            embed.WithContent($"<@{OmniPaths.KlivesDiscordAccountID}>");
            embed.WithAllowedMention(new UserMention(OmniPaths.KlivesDiscordAccountID));

            await channel.SendMessageAsync(embed);
        }

        /// <summary>
        /// Posts an attention ping to the Steward channel with an @mention of Klives — for blocking
        /// events (interventions, budget pauses, stalls) so he's notified even away from the website.
        /// The mention lives in message content because embeds alone don't ping.
        /// </summary>
        public async Task PostAttentionAsync(Steward steward, string title, string body)
        {
            if (steward.DiscordChannelID == 0) return;
            try
            {
                var channel = await discord.Client.GetChannelAsync(steward.DiscordChannelID);
                var msg = KliveBotDiscord.MakeSimpleEmbed(title, body, DiscordColor.Gold);
                msg.WithContent($"<@{OmniPaths.KlivesDiscordAccountID}>");
                msg.WithAllowedMention(new UserMention(OmniPaths.KlivesDiscordAccountID));
                await channel.SendMessageAsync(msg);
            }
            catch (Exception ex) { log($"Attention ping failed for {steward.StewardID}: {ex.Message}"); }
        }

        private async Task OnComponentInteraction(DiscordClient sender, ComponentInteractionCreateEventArgs e)
        {
            if (!buttonMap.TryGetValue(e.Id, out var m)) return;
            try
            {
                // Discuss keeps the gate open and just posts to the channel; Approve/Deny resolve it.
                if (m.decision == GateDecision.Discuss)
                {
                    parent.Gates.BeginDiscussion(m.stewardID, m.gateID, "Discussion requested via Discord.");
                    await e.Interaction.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource,
                        new DiscordInteractionResponseBuilder().WithContent("Reply in this channel to discuss with the Commander; the gate stays open."));
                    return;
                }

                bool ok = parent.Gates.ResolveGate(m.stewardID, m.gateID, new GateResolution(m.decision, $"(via Discord)", "klives"));
                await e.Interaction.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource,
                    new DiscordInteractionResponseBuilder().WithContent(ok ? $"{m.decision} recorded." : "Already resolved."));

                // Retire the button ids for this gate.
                buttonMap.TryRemove("gate_" + m.gateID + "_a", out _);
                buttonMap.TryRemove("gate_" + m.gateID + "_d", out _);
                buttonMap.TryRemove("gate_" + m.gateID + "_x", out _);
            }
            catch (Exception ex) { log($"Discord approval interaction failed: {ex.Message}"); }
        }

        // ── reply routing ──

        private async Task OnMessageCreated(DiscordClient sender, MessageCreateEventArgs e)
        {
            try
            {
                if (e.Author.IsBot) return;
                if (!channelToSteward.TryGetValue(e.Channel.Id, out var stewardID)) return;
                if (e.Author.Id != OmniPaths.KlivesDiscordAccountID) return; // only Klives steers a Steward

                var steward = parent.Store.GetSteward(stewardID);
                if (steward == null) return;

                // Task is the right default for a request with a deliverable, but a constraint typed here
                // ("don't complete it yet") has no deliverable and would sit open forever as a Task.
                // CreateAndDeliverDirective classifies that case and saves it as a standing rule instead.
                var receipt = parent.MessageStewardWithReceipt(stewardID, e.Message.Content, StewardDirectiveKind.Task);
                if (!receipt.Accepted)
                {
                    await e.Message.RespondAsync($"I could not queue that instruction: {receipt.Reason ?? "steward unavailable"}");
                    return;
                }
                if (steward.Status is StewardStatus.Active or StewardStatus.Planning)
                {
                    // Steer: injected into the live wake if one is running (fast steering, item 5).
                    // The reply can be a moment away — show life immediately.
                    string state = receipt.Status == "delivered"
                        ? $"delivered to wake `{receipt.WakeID}`"
                        : "stored durably and queued for the next runnable wake";
                    string label = receipt.PromotedToRule
                        ? "Saved as a **standing rule** (it holds until you replace or revoke it)"
                        : "Accepted directive";
                    await e.Message.RespondAsync($"{label} `{receipt.DirectiveID}` — {state}.");
                    try { await e.Channel.TriggerTypingAsync(); } catch { }
                }
                else
                {
                    // A message into a paused Steward used to be swallowed silently.
                    await e.Message.RespondAsync($"Steward is **{steward.Status}** — the Commander won't act until it's resumed (message logged for its next wake).");
                }
            }
            catch (Exception ex) { log($"Discord reply routing failed: {ex.Message}"); }
        }

        /// <summary>
        /// Posts the Commander's prose (a wake's closing reply to Klives) to the Steward channel
        /// as plain chat — the counterpart of reply routing, without which the Commander answers
        /// only into the event log and Discord reads as silence.
        /// </summary>
        public async Task PostCommanderReplyAsync(Steward steward, string text)
        {
            if (steward.DiscordChannelID == 0 || string.IsNullOrWhiteSpace(text)) return;
            try
            {
                var channel = await discord.Client.GetChannelAsync(steward.DiscordChannelID);
                for (int at = 0; at < text.Length; at += 1900)
                    await channel.SendMessageAsync(text.Substring(at, Math.Min(1900, text.Length - at)));
            }
            catch (Exception ex) { log($"Commander reply post failed for {steward.StewardID}: {ex.Message}"); }
        }

        /// <summary>
        /// Posts a verified directive result to Klives and attaches small Steward files directly
        /// to the Steward channel. Larger files remain downloadable from Steward Files, with their
        /// exact /project paths surfaced in chat.
        /// </summary>
        public async Task PostDirectiveCompletionAsync(Steward steward, StewardDirective directive,
            IReadOnlyList<string> artifactPaths, string summary)
        {
            if (steward.DiscordChannelID == 0) return;
            try
            {
                var channel = await discord.Client.GetChannelAsync(steward.DiscordChannelID);
                string files = artifactPaths.Count == 0 ? "No file deliverables were recorded."
                    : "Verified deliverables: " + string.Join(", ", artifactPaths.Select(x => $"`/project/{x.TrimStart('/').Replace('\\', '/')}`"));
                await channel.SendMessageAsync($"[Directive complete] `{directive.DirectiveID}` completed by {directive.CompletedBy ?? "agent"}.\n{summary}\n{files}");

                foreach (string path in artifactPaths)
                {
                    try
                    {
                        var entry = parent.Files.Stat(steward.StewardID, path);
                        if (entry?.Kind != StewardFileKind.File) continue;
                        if (entry.Size > 8L * 1024 * 1024)
                        {
                            await channel.SendMessageAsync($"`/project/{path}` is {entry.Size:N0} bytes, so it is ready in Steward Files rather than attached here.");
                            continue;
                        }
                        await using var stream = parent.Files.OpenRead(steward.StewardID, path);
                        var builder = new DiscordMessageBuilder().WithContent($"Deliverable for directive `{directive.DirectiveID}`: `/project/{path}`");
                        builder.AddFile(Path.GetFileName(path), stream);
                        await channel.SendMessageAsync(builder);
                    }
                    catch (Exception ex) { log($"Directive artifact delivery failed for {steward.StewardID}/{path}: {ex.Message}"); }
                }
            }
            catch (Exception ex) { log($"Directive completion post failed for {steward.StewardID}: {ex.Message}"); }
        }

        // ── reports ──

        /// <summary>Posts a brief report (morning intentions / evening wrap) to the Steward channel.</summary>
        public async Task PostReportAsync(Steward steward, string title, string body)
        {
            if (steward.DiscordChannelID == 0) return;
            try
            {
                var channel = await discord.Client.GetChannelAsync(steward.DiscordChannelID);
                await channel.SendMessageAsync(KliveBotDiscord.MakeSimpleEmbed(title, body, DiscordColor.Blurple));
            }
            catch (Exception ex) { log($"Report post failed for {steward.StewardID}: {ex.Message}"); }
        }

        private static string Sanitise(string name)
        {
            var chars = name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
            string s = new string(chars).Trim('-');
            while (s.Contains("--")) s = s.Replace("--", "-");
            return string.IsNullOrWhiteSpace(s) ? "unnamed" : (s.Length > 90 ? s[..90] : s);
        }
    }
}
