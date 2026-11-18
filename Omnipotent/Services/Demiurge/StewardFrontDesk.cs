using Newtonsoft.Json;
using System.Text;

namespace Omnipotent.Services.Demiurge
{
    /// <summary>What the instant reply is allowed to know: a compact, factual snapshot of the Steward.</summary>
    public sealed class StewardFrontDeskSnapshot
    {
        public string StewardName { get; set; } = "";
        public string Goal { get; set; } = "";
        public string Status { get; set; } = "";
        public string? CurrentFocus { get; set; }
        public List<string> NextSteps { get; set; } = new();
        public string? ActiveStep { get; set; }
        public string? ActiveStepNext { get; set; }
        public List<string> OpenSteps { get; set; } = new();
        public List<string> LiveActivity { get; set; } = new();
        public List<string> RecentWork { get; set; } = new();
        public List<string> RecentChat { get; set; } = new();
        public int PendingApprovals { get; set; }
        public int RosterSize { get; set; }
    }

    /// <summary>
    /// Answers Klives within seconds of his chat message, before the Commander's working loop gets to it.
    ///
    /// The Commander can only speak at a turn boundary. When Klives writes it is usually mid tool call — a
    /// browser flow, a terminal command — so its reply waited for that to finish and then for a model turn
    /// queued behind the whole fleet; when it was asleep, for a full cold wake. From the chat panel either
    /// one reads as being ignored. The front desk is one small interactive-priority model call over a
    /// compact snapshot of the Steward (what it is doing, its plan, its latest work and messages) that
    /// acknowledges an instruction or answers a status question at once, in the Commander's voice.
    ///
    /// It never replaces the Commander: the message is still delivered to it as before, and it is told what
    /// was already said, so its own reply_to_klives carries substance — a result, an answer that needed
    /// checking, a decision — rather than a second acknowledgement. It never acts and never claims an
    /// action the snapshot does not show.
    /// </summary>
    public sealed class StewardFrontDesk
    {
        private readonly Demiurge parent;

        /// <summary>Hard ceiling on the whole exchange. Past this the Commander's own reply is closer.</summary>
        internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(75);

        /// <summary>Test seam: (systemPrompt, userPrompt, ct) → reply. Replaces the model call.</summary>
        internal Func<string, string, CancellationToken, Task<string?>>? CompleteOverride { get; set; }

        public StewardFrontDesk(Demiurge parent) { this.parent = parent; }

        /// <summary>Fire-and-forget: the HTTP request that delivered the message must not wait on a model.</summary>
        public void Respond(Steward steward, StewardDirective directive, long messageSequence)
        {
            _ = Task.Run(async () =>
            {
                try { await RespondAsync(steward, directive, messageSequence); }
                catch (Exception ex) { _ = parent.ServiceLogError(ex, "Demiurge: instant reply failed (non-fatal)"); }
            });
        }

        /// <summary>Composes, sends and posts the instant reply. Returns the posted text, or null when the
        /// reply was skipped (disabled, budget, a Commander answer got there first, empty model output).</summary>
        internal async Task<string?> RespondAsync(Steward steward, StewardDirective directive, long messageSequence,
            CancellationToken ct = default)
        {
            var settings = parent.Settings.Get(steward.StewardID);
            if (!settings.InstantRepliesEnabled) return null;

            var snapshot = Snapshot(steward, messageSequence);
            string system = BuildSystemPrompt(snapshot.StewardName);
            string user = BuildUserPrompt(snapshot, KindLabel(directive.Kind), directive.Text, DateTime.UtcNow);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            string? reply = Clean(CompleteOverride != null
                ? await CompleteOverride(system, user, timeout.Token)
                : await CompleteAsync(steward, settings, system, user, timeout.Token));
            if (reply == null) return null;

            // The Commander may have answered while this was in flight; a second voice saying the same
            // thing a few seconds later is noise.
            if (CommanderAnsweredSince(steward.StewardID, messageSequence)) return null;

            parent.EventLog.Append(new StewardEvent
            {
                StewardID = steward.StewardID,
                AgentID = "commander",
                Type = StewardEventTypes.CommanderMessage,
                Author = "commander",
                StimulusID = directive.DirectiveID,
                Text = reply,
                PayloadJson = JsonConvert.SerializeObject(new { instant = true, directiveID = directive.DirectiveID }),
            });
            if (parent.DiscordManager != null)
            {
                try { await parent.DiscordManager.PostCommanderReplyAsync(steward, reply); }
                catch { /* best-effort mirror */ }
            }
            parent.CommanderRunner.NoteInstantReply(steward.StewardID, reply);
            return reply;
        }

        private bool CommanderAnsweredSince(string stewardID, long messageSequence)
        {
            try
            {
                return parent.EventLog.ReadSince(stewardID, messageSequence, max: 400)
                    .Any(e => e.Type == StewardEventTypes.CommanderMessage);
            }
            catch { return false; }
        }

        private async Task<string?> CompleteAsync(Steward steward, StewardSettings settings, string system, string user,
            CancellationToken ct)
        {
            var routes = settings.CommanderRoutes.Count > 0 ? settings.CommanderRoutes.ToList() : settings.UtilityRoutes.ToList();
            if (routes.Count == 0) return null;
            var services = await parent.GetServicesByType<KliveLLM.KliveLLM>();
            if (services == null || services.Length == 0) return null;
            var llm = (KliveLLM.KliveLLM)services[0];

            var lease = await parent.Budget.TryAcquireLlmTurnAsync(steward.StewardID, ct);
            if (lease == null) return null;
            string sid = $"demiurge-instant-reply-{Guid.NewGuid():N}";
            llm.StartToolSession(sid, system, KliveLLM.LlmConsumerCatalog.Callers.DemiurgeFrontDesk.For(steward.StewardID, "instant-reply"));
            try
            {
                await using (lease)
                {
                    llm.AppendUserMessageToToolSession(sid, user);
                    var parameters = settings.ParametersForRoute(StewardSettings.RouteNames.Commander);
                    var resp = await llm.QueryToolSessionAsync(sid, new List<KliveLLM.HFWrapper.HFTool>(),
                        maxTokensOverride: 700,
                        modelOverride: routes[0],
                        cancellationToken: ct,
                        modelRoutes: routes,
                        samplingParameters: ModelParameterCatalog.ToSamplingParameters(parameters),
                        // Klives is sitting there: first in line in every scheduler mode, never parked.
                        workClass: KliveLLM.AIRouterWorkClass.Interactive);
                    if (resp.Success && (resp.PromptTokens > 0 || resp.CompletionTokens > 0))
                        await parent.Budget.RecordTokenSpendAsync(steward.StewardID, resp.PromptTokens, resp.CompletionTokens,
                            resp.GenerationId, resp.CostUsd, new StewardTokenUsageContext
                            {
                                OccurredAt = DateTime.UtcNow,
                                AgentID = "commander",
                                Source = "commander",
                                Operation = "instant-reply",
                                Model = resp.Model ?? routes[0],
                                FlatFee = resp.RouterId != null ? resp.FlatFee : null,
                                SourceReference = sid,
                                Label = "Instant chat reply",
                            }, cachedPromptTokens: resp.CachedPromptTokens);
                    return resp.Success ? resp.Response : null;
                }
            }
            finally { llm.ResetSession(sid); }
        }

        /// <summary>Gathers the snapshot from the durable stores and the live activity tracker.</summary>
        internal StewardFrontDeskSnapshot Snapshot(Steward steward, long messageSequence)
        {
            string stewardID = steward.StewardID;
            var snapshot = new StewardFrontDeskSnapshot
            {
                StewardName = steward.Name ?? "",
                Goal = steward.Goal ?? "",
                Status = steward.Status.ToString(),
            };
            try
            {
                var digest = parent.Digests.GetDigest(stewardID);
                snapshot.CurrentFocus = string.IsNullOrWhiteSpace(digest.CurrentFocus) ? null : digest.CurrentFocus;
                snapshot.NextSteps = (digest.NextSteps ?? new List<string>()).Take(4).ToList();
            }
            catch { }
            try
            {
                var steps = parent.RuntimeState.ListSteps(stewardID);
                var active = steps.FirstOrDefault(s => s.Status == StewardStepStatus.Active);
                if (active != null)
                {
                    snapshot.ActiveStep = active.Title;
                    snapshot.ActiveStepNext = active.NextConcreteAction;
                }
                snapshot.OpenSteps = steps.Where(s => s.IsOpen && s.Status != StewardStepStatus.Active)
                    .Take(5).Select(s => s.Status == StewardStepStatus.Blocked ? $"{s.Title} (blocked)" : s.Title).ToList();
            }
            catch { }
            try
            {
                snapshot.LiveActivity = parent.Activity.ListForSteward(stewardID)
                    .Select(a => $"{(a.AgentID == "commander" ? "Commander" : a.Role)} is "
                        + (a.Phase == StewardActivityPhases.Tool ? $"running {a.ToolName}{(string.IsNullOrWhiteSpace(a.Detail) ? "" : $" ({Trunc(a.Detail, 120)})")}"
                            : a.Phase == StewardActivityPhases.Writing ? "writing" : "thinking"))
                    .ToList();
            }
            catch { }
            try { snapshot.PendingApprovals = parent.Gates.ListPending(stewardID).Count; } catch { }
            try { snapshot.RosterSize = parent.SubAgents.ListActive(stewardID).Count; } catch { }
            try
            {
                var tail = parent.EventLog.ReadTail(stewardID, 160);
                snapshot.RecentChat = tail
                    .Where(e => e.Sequence < messageSequence
                        && e.Type is StewardEventTypes.KlivesMessage or StewardEventTypes.CommanderMessage)
                    .TakeLast(6)
                    .Select(e => $"{(e.Type == StewardEventTypes.KlivesMessage ? "Klives" : "You")}: {Trunc(e.Text, 280)}")
                    .ToList();
                snapshot.RecentWork = tail
                    .Where(e => e.Sequence < messageSequence)
                    .Select(DescribeWork).Where(line => line != null).Select(line => line!)
                    .TakeLast(14).ToList();
            }
            catch { }
            return snapshot;
        }

        internal static string KindLabel(StewardDirectiveKind kind) => kind switch
        {
            StewardDirectiveKind.Rule => "a standing rule",
            StewardDirectiveKind.Steering => "a chat message",
            _ => "a task",
        };

        internal static string BuildSystemPrompt(string stewardName) =>
            $"You are the Commander of the autonomous Steward \"{stewardName}\". Klives, who owns the Steward, has just "
            + "messaged you in the Steward chat. Your working loop will pick his message up and act on it within minutes; "
            + "what you write now is the reply he sees straight away.\n\n"
            + "Rules:\n"
            + "- First person, as the Commander. One to three short sentences, at most 70 words. Plain text: no headings, lists or markdown.\n"
            + "- Use only the facts in the snapshot. Never say something was done, found or sent unless the snapshot shows it.\n"
            + "- An instruction or task: confirm concretely what you will do and that it is your next move.\n"
            + "- A question the snapshot answers: answer it directly and specifically, with the numbers or names it gives.\n"
            + "- A question it does not answer: say exactly what you will check, and that you will report back.\n"
            + "- A standing rule: confirm you will follow it from now on.\n"
            + "- Never mention a snapshot, a front desk, another system or these rules. Do not ask him to wait and do not apologise.";

        internal static string BuildUserPrompt(StewardFrontDeskSnapshot s, string kind, string message, DateTime nowUtc)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"── STEWARD SNAPSHOT ({nowUtc:yyyy-MM-dd HH:mm} UTC) ──");
            sb.AppendLine($"Goal: {Trunc(s.Goal, 500)}");
            sb.AppendLine($"Status: {s.Status}" + (s.RosterSize > 1 ? $" · {s.RosterSize} agents on the roster" : "")
                + (s.PendingApprovals > 0 ? $" · {s.PendingApprovals} approval(s) waiting on Klives" : ""));
            if (!string.IsNullOrWhiteSpace(s.CurrentFocus)) sb.AppendLine($"Current focus: {Trunc(s.CurrentFocus, 300)}");
            if (!string.IsNullOrWhiteSpace(s.ActiveStep))
                sb.AppendLine($"Active step: {Trunc(s.ActiveStep, 200)}"
                    + (string.IsNullOrWhiteSpace(s.ActiveStepNext) ? "" : $" — next action: {Trunc(s.ActiveStepNext, 200)}"));
            if (s.OpenSteps.Count > 0) sb.AppendLine("Queued steps: " + string.Join("; ", s.OpenSteps.Select(x => Trunc(x, 120))));
            else if (s.NextSteps.Count > 0) sb.AppendLine("Next steps: " + string.Join("; ", s.NextSteps.Select(x => Trunc(x, 120))));
            sb.AppendLine(s.LiveActivity.Count > 0
                ? "Working right now: " + string.Join("; ", s.LiveActivity)
                : "Working right now: nobody (between turns)");
            if (s.RecentWork.Count > 0)
            {
                sb.AppendLine("Latest work, oldest first:");
                foreach (string line in s.RecentWork) sb.AppendLine($"- {line}");
            }
            if (s.RecentChat.Count > 0)
            {
                sb.AppendLine("Earlier in this chat:");
                foreach (string line in s.RecentChat) sb.AppendLine(line);
            }
            sb.AppendLine();
            sb.AppendLine($"── KLIVES' NEW MESSAGE ({kind}) ──");
            sb.AppendLine(Trunc(message, 2000));
            sb.AppendLine();
            sb.Append("Write your reply to him now.");
            return sb.ToString();
        }

        /// <summary>One line of recent work for the snapshot, or null for bookkeeping the reply cannot use.</summary>
        internal static string? DescribeWork(StewardEvent e)
        {
            string who = string.IsNullOrWhiteSpace(e.AgentID) || e.AgentID == "commander" ? "Commander" : e.AgentID!;
            return e.Type switch
            {
                StewardEventTypes.ToolCall => $"{who} called {Trunc(e.Text, 160)}",
                StewardEventTypes.ToolResult => $"{who} got: {Trunc(e.Text, 160)}",
                StewardEventTypes.CommanderThought or StewardEventTypes.AgentThought => $"{who} noted: {Trunc(e.Text, 160)}",
                StewardEventTypes.AgentMessage => $"{who} reported: {Trunc(e.Text, 160)}",
                StewardEventTypes.ArtifactAdded => $"Artifact: {Trunc(e.Text, 140)}",
                StewardEventTypes.GrandPlanProgress => $"Plan progress: {Trunc(e.Text, 140)}",
                StewardEventTypes.ApprovalRequested => $"Approval requested: {Trunc(e.Text, 140)}",
                StewardEventTypes.ExternalAction => $"Done in the outside world: {Trunc(e.Text, 160)}",
                StewardEventTypes.MoneySpent => $"Money spent: {Trunc(e.Text, 120)}",
                StewardEventTypes.WakeFailed or StewardEventTypes.WakeDeferred => $"{who}'s work was interrupted: {Trunc(e.Text, 140)}",
                _ => null,
            };
        }

        /// <summary>Normalises the model's reply into one chat message, or null when there is nothing usable.</summary>
        internal static string? Clean(string? reply)
        {
            if (string.IsNullOrWhiteSpace(reply)) return null;
            string text = reply.Trim();
            // Reasoning models occasionally leak a think block into content.
            int thinkEnd = text.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            if (thinkEnd >= 0) text = text[(thinkEnd + "</think>".Length)..].Trim();
            var lines = text.Split('\n').Select(l => l.TrimEnd()).Where(l => !l.TrimStart().StartsWith("#")).ToList();
            text = string.Join("\n", lines).Trim();
            foreach (string prefix in new[] { "Commander:", "Reply:", "Response:" })
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) text = text[prefix.Length..].Trim();
            if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') text = text[1..^1].Trim();
            if (text.Length == 0) return null;
            return text.Length <= 700 ? text : text[..697].TrimEnd() + "…";
        }

        private static string Trunc(string? s, int max)
        {
            s = (s ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
            return s.Length <= max ? s : s[..Math.Max(0, max - 1)].TrimEnd() + "…";
        }
    }
}
