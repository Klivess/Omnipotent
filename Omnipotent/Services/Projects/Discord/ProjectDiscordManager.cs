using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveBot_Discord;
using System.Collections.Concurrent;

namespace Omnipotent.Services.Projects.Discord
{
    /// <summary>
    /// Per-project Discord integration (§10.2): one #project channel per project, button
    /// approvals (Approve/Deny/Discuss) racing the website's gate resolution, plain-reply routing
    /// into the Commander's context, and twice-daily reports. Reuses KliveBotDiscord's single
    /// DiscordClient rather than standing up its own.
    ///
    /// The approval flow uses a TaskCompletionSource wired straight into ProjectGateManager, so a
    /// Discord button press and a website click race — first responder wins — the same pattern
    /// HostControl's ApprovalBroker already uses for computer-use approvals.
    /// </summary>
    public class ProjectDiscordManager
    {
        private readonly Projects parent;
        private readonly KliveBotDiscord discord;
        private readonly Action<string> log;

        // channelID → projectID, for routing replies and button interactions.
        private readonly ConcurrentDictionary<ulong, string> channelToProject = new();
        // Discord button custom-id → (projectID, gateID, decision).
        private readonly ConcurrentDictionary<string, (string projectID, string gateID, GateDecision decision)> buttonMap = new(StringComparer.Ordinal);

        public ProjectDiscordManager(Projects parent, KliveBotDiscord discord, Action<string> log)
        {
            this.parent = parent;
            this.discord = discord;
            this.log = log ?? (_ => { });
        }

        /// <summary>Wires the shared client's events and indexes existing project channels.</summary>
        public void Initialise()
        {
            foreach (var p in parent.Store.ListProjects())
                if (p.DiscordChannelID != 0)
                    channelToProject[p.DiscordChannelID] = p.ProjectID;

            discord.Client.MessageCreated += OnMessageCreated;
            discord.Client.ComponentInteractionCreated += OnComponentInteraction;
        }

        // ── channel lifecycle ──

        public async Task<ulong> CreateProjectChannelAsync(Project project)
        {
            if (project.DiscordChannelID != 0) return project.DiscordChannelID;
            var guild = await discord.Client.GetGuildAsync(OmniPaths.DiscordServerContainingKlives);
            string name = "project-" + Sanitise(project.Name);
            var channel = await guild.CreateTextChannelAsync(name, topic: $"Project: {project.Goal}");
            project.DiscordChannelID = channel.Id;
            parent.Store.SaveProject(project);
            channelToProject[channel.Id] = project.ProjectID;

            await channel.SendMessageAsync(KliveBotDiscord.MakeSimpleEmbed(
                $"Project started: {project.Name}",
                $"Goal: {project.Goal}\nToken budget: ${project.TokenBudgetUsd:0.##} · Money budget: ${project.MoneyBudgetUsd:0.##}\n\nReply here to talk to the Commander. Approvals will appear as buttons.",
                DiscordColor.Teal));
            log($"Created Discord channel #{name} for project {project.ProjectID}.");
            return channel.Id;
        }

        /// <summary>Renames the project's Discord channel to match a project rename (best-effort).</summary>
        public async Task RenameProjectChannelAsync(Project project)
        {
            if (project.DiscordChannelID == 0) return;
            try
            {
                var channel = await discord.Client.GetChannelAsync(project.DiscordChannelID);
                string name = "project-" + Sanitise(project.Name);
                await channel.ModifyAsync(c => c.Name = name);
            }
            catch (Exception ex) { log($"Rename channel failed for {project.ProjectID}: {ex.Message}"); }
        }

        public async Task ArchiveChannelAsync(Project project)
        {
            if (project.DiscordChannelID == 0) return;
            try
            {
                var channel = await discord.Client.GetChannelAsync(project.DiscordChannelID);
                await channel.ModifyAsync(c => c.Name = "archived-" + channel.Name);
                await channel.SendMessageAsync("Project completed — channel archived.");
            }
            catch (Exception ex) { log($"Archive channel failed for {project.ProjectID}: {ex.Message}"); }
            channelToProject.TryRemove(project.DiscordChannelID, out _);
        }

        // ── approvals ──

        /// <summary>
        /// Posts an approval card to the project's channel and resolves the gate on the first
        /// button press (racing the website). Fire-and-forget: it doesn't block the agent, which
        /// awaits the gate's own TaskCompletionSource in ProjectGateManager.
        /// </summary>
        public async Task PostApprovalAsync(Project project, ProjectGate gate)
        {
            if (project.DiscordChannelID == 0) return;
            DiscordChannel channel;
            try { channel = await discord.Client.GetChannelAsync(project.DiscordChannelID); }
            catch { return; }

            var embed = KliveBotDiscord.MakeSimpleEmbed(
                $"Approval needed: {gate.Title}",
                $"{gate.Description}\n\n**Why:** {gate.Rationale}",
                DiscordColor.Orange);

            string baseId = "gate_" + gate.GateID;
            buttonMap[baseId + "_a"] = (project.ProjectID, gate.GateID, GateDecision.Approve);
            buttonMap[baseId + "_d"] = (project.ProjectID, gate.GateID, GateDecision.Deny);
            buttonMap[baseId + "_x"] = (project.ProjectID, gate.GateID, GateDecision.Discuss);

            embed.AddComponents(
                new DiscordButtonComponent(ButtonStyle.Success, baseId + "_a", "Approve"),
                new DiscordButtonComponent(ButtonStyle.Danger, baseId + "_d", "Deny"),
                new DiscordButtonComponent(ButtonStyle.Secondary, baseId + "_x", "Discuss"));

            await channel.SendMessageAsync(embed);
        }

        private async Task OnComponentInteraction(DiscordClient sender, ComponentInteractionCreateEventArgs e)
        {
            if (!buttonMap.TryGetValue(e.Id, out var m)) return;
            try
            {
                // Discuss keeps the gate open and just posts to the channel; Approve/Deny resolve it.
                if (m.decision == GateDecision.Discuss)
                {
                    await e.Interaction.CreateResponseAsync(InteractionResponseType.ChannelMessageWithSource,
                        new DiscordInteractionResponseBuilder().WithContent("Reply in this channel to discuss with the Commander; the gate stays open."));
                    return;
                }

                bool ok = parent.Gates.ResolveGate(m.projectID, m.gateID, new GateResolution(m.decision, $"(via Discord)", "klives"));
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
                if (!channelToProject.TryGetValue(e.Channel.Id, out var projectID)) return;
                if (e.Author.Id != OmniPaths.KlivesDiscordAccountID) return; // only Klives steers a project

                var project = parent.Store.GetProject(projectID);
                if (project == null) return;

                parent.EventLog.Append(new ProjectEvent
                {
                    ProjectID = projectID,
                    Type = ProjectEventTypes.KlivesMessage,
                    Author = "klives",
                    Text = e.Message.Content,
                });
                if (project.Status == ProjectStatus.Active)
                {
                    // Steer: injected into the live wake if one is running (fast steering, item 5).
                    parent.CommanderRunner.Steer(project, e.Message.Content);
                    // The reply can be a moment away — show life immediately.
                    try { await e.Channel.TriggerTypingAsync(); } catch { }
                }
                else
                {
                    // A message into a paused project used to be swallowed silently.
                    await e.Message.RespondAsync($"Project is **{project.Status}** — the Commander won't act until it's resumed (message logged for its next wake).");
                }
            }
            catch (Exception ex) { log($"Discord reply routing failed: {ex.Message}"); }
        }

        /// <summary>
        /// Posts the Commander's prose (a wake's closing reply to Klives) to the project channel
        /// as plain chat — the counterpart of reply routing, without which the Commander answers
        /// only into the event log and Discord reads as silence.
        /// </summary>
        public async Task PostCommanderReplyAsync(Project project, string text)
        {
            if (project.DiscordChannelID == 0 || string.IsNullOrWhiteSpace(text)) return;
            try
            {
                var channel = await discord.Client.GetChannelAsync(project.DiscordChannelID);
                for (int at = 0; at < text.Length; at += 1900)
                    await channel.SendMessageAsync(text.Substring(at, Math.Min(1900, text.Length - at)));
            }
            catch (Exception ex) { log($"Commander reply post failed for {project.ProjectID}: {ex.Message}"); }
        }

        // ── reports ──

        /// <summary>Posts a brief report (morning intentions / evening wrap) to the project channel.</summary>
        public async Task PostReportAsync(Project project, string title, string body)
        {
            if (project.DiscordChannelID == 0) return;
            try
            {
                var channel = await discord.Client.GetChannelAsync(project.DiscordChannelID);
                await channel.SendMessageAsync(KliveBotDiscord.MakeSimpleEmbed(title, body, DiscordColor.Blurple));
            }
            catch (Exception ex) { log($"Report post failed for {project.ProjectID}: {ex.Message}"); }
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
