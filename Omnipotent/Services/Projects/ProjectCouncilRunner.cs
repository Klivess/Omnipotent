using Newtonsoft.Json;
using System.Security.Cryptography;
using System.Text;

namespace Omnipotent.Services.Projects
{
    /// <summary>One model round-trip's result, as the council runner needs it (text + spend).</summary>
    public record CouncilTurn(bool Success, string Text, long PromptTokens, long CompletionTokens, string? GenerationId, double? CostUsd);

    /// <summary>
    /// Orchestrates an adversarial council: a panel of role-played LLM seats plus a Chair that the
    /// Commander convenes for a high-stakes decision. Three rounds — parallel openings, parallel
    /// rebuttals (each seat attacks the others in its own continued session), then a single Chair
    /// synthesis. The Chair's output is the verdict returned to the Commander.
    ///
    /// Councils are NOT sub-agents: they don't count against SubAgentCap and hold no tools. The
    /// panelists see only the Commander's briefing — the Chair has an explicit
    /// "INSUFFICIENT INFORMATION — gather X and reconvene" escape hatch for when that isn't enough.
    ///
    /// The LLM/budget dependencies are injected as delegates (mirrors StimulusAgent) so the
    /// orchestration can be unit-tested with scripted turns.
    /// </summary>
    public class ProjectCouncilRunner
    {
        public const int OpeningMaxTokens = 900;
        public const int RebuttalMaxTokens = 700;
        public const int ChairMaxTokens = 1200;
        public const int BriefingTokenCap = 4000;

        public static readonly string[] DefaultRoles = { "Strategist", "Skeptic", "Pragmatist" };

        private readonly ProjectCouncilStore store;
        private readonly ProjectEventLogStore eventLog;
        private readonly Action<string> log;

        /// <summary>Fresh session: (sessionId, systemPrompt, userMessage, model, maxTokens, ct) → turn.</summary>
        public Func<string, string?, string, string, int, CancellationToken, Task<CouncilTurn?>>? QueryAsync { get; set; }
        /// <summary>Continue an existing session (rebuttal round): (sessionId, userMessage, model, maxTokens, ct) → turn.</summary>
        public Func<string, string, string, int, CancellationToken, Task<CouncilTurn?>>? ContinueAsync { get; set; }
        /// <summary>Reserves budget for a provider turn without serializing the provider call.</summary>
        public Func<string, CancellationToken, Task<IAsyncDisposable?>>? AcquireTurnAsync { get; set; }
        /// <summary>Books a turn's spend to the ledger: (projectID, prompt, completion, genId, cost).</summary>
        public Func<string, long, long, string?, double?, Task>? RecordSpendAsync { get; set; }
        /// <summary>True when the project auto-paused on budget exhaustion mid-council.</summary>
        public Func<string, bool>? IsBudgetPaused { get; set; }
        /// <summary>Budget snapshot line for the shared context.</summary>
        public Func<string, string>? DescribeBudget { get; set; }
        /// <summary>Current Grand Plan summary line for the shared context (empty when none).</summary>
        public Func<string, string>? DescribeGrandPlan { get; set; }
        public ProjectCouncilRunner(ProjectCouncilStore store, ProjectEventLogStore eventLog, Action<string> log)
        {
            this.store = store;
            this.eventLog = eventLog;
            this.log = log ?? (_ => { });
        }

        /// <summary>
        /// Runs a full council. Returns the completed (or failed/cancelled) session. Cap/budget
        /// refusals return a transient Failed session with <see cref="CouncilSession.Error"/> set and
        /// are NOT persisted. Cancellation persists the partial transcript and rethrows so the wake
        /// unwinds normally.
        /// </summary>
        public Task<CouncilSession> ConveneAsync(Project project, string? wakeID, string topic, string briefing,
            string[]? roles, string urgency, string purpose, string model, int maxPerWake, int maxPerDay, CancellationToken ct)
            => ConveneAsync(project, wakeID, topic, briefing, roles, urgency, purpose,
                (IReadOnlyList<string>)new[] { model }, maxPerWake, maxPerDay, ct);

        public async Task<CouncilSession> ConveneAsync(Project project, string? wakeID, string topic, string briefing,
            string[]? roles, string urgency, string purpose, IReadOnlyList<string> configuredRoutes,
            int maxPerWake, int maxPerDay, CancellationToken ct)
        {
            string pid = project.ProjectID;
            var modelRoutes = configuredRoutes.Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (modelRoutes.Count == 0) return Refused("No council model routes are configured.");
            string model = modelRoutes[0];

            // ── guardrails ──
            if (IsBudgetPaused?.Invoke(pid) == true)
                return Refused("The project is budget-paused; a council would overrun the budget. Resolve the budget first.");

            var panel = NormalizeRoles(roles);
            var session = new CouncilSession
            {
                ProjectID = pid,
                WakeID = wakeID,
                ConvenedBy = "commander",
                Purpose = string.IsNullOrWhiteSpace(purpose) ? "decision" : purpose.Trim().ToLowerInvariant(),
                Topic = topic.Trim(),
                Briefing = briefing,
                InputFingerprint = InputFingerprint(topic, briefing),
                Urgency = NormalizeUrgency(urgency),
                Roles = panel.ToList(),
                Model = model,
                Status = CouncilStatus.Running,
            };
            if (store.TryCreateWithGuards(session, maxPerWake, maxPerDay, out string? refusal) == null)
                return Refused(refusal ?? "Council guardrail refused this deliberation.");

            AppendEvent(session, ProjectEventTypes.CouncilConvened, "commander",
                $"Council convened: {Trunc(session.Topic, 140)} — panel: {string.Join(", ", panel)} + Chair.",
                new { councilID = session.CouncilID, topic = session.Topic, roles = panel, urgency = session.Urgency, purpose = session.Purpose, modelRoutes });

            try
            {
                if (QueryAsync == null || ContinueAsync == null)
                    throw new InvalidOperationException("Council runner is not wired to an LLM.");

                string sharedContext = BuildSharedContext(project, session);

                // ── Round 1: openings (parallel) ──
                var openingTasks = panel.Select(role => RunOpeningAsync(session, role, sharedContext, modelRoutes, ct)).ToArray();
                var openings = await Task.WhenAll(openingTasks);
                var openingByRole = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int i = 0; i < panel.Length; i++)
                    if (openings[i] != null) openingByRole[panel[i]] = openings[i]!;

                if (openingByRole.Count < 2)
                {
                    session.Status = CouncilStatus.Failed;
                    session.Error = "Too few panelists responded to form a council (need ≥2 openings).";
                    session.CompletedAt = DateTime.UtcNow;
                    store.Update(session);
                    return session;
                }

                if (IsBudgetPaused?.Invoke(pid) == true)
                    return FinishOnBudget(session, openingByRole);

                // ── Round 2: rebuttals (parallel) — only seats that opened ──
                var respondents = panel.Where(openingByRole.ContainsKey).ToArray();
                var rebuttalTasks = respondents
                    .Select(role => RunRebuttalAsync(session, role, openingByRole, modelRoutes, ct)).ToArray();
                await Task.WhenAll(rebuttalTasks);

                if (IsBudgetPaused?.Invoke(pid) == true)
                    return FinishOnBudget(session, openingByRole);

                // ── Round 3: Chair synthesis ──
                string chairPrompt = BuildChairPrompt(session);
                CouncilTurn? chair = null;
                await using (var lease = AcquireTurnAsync == null ? null : await AcquireTurnAsync(pid, ct))
                {
                    if (AcquireTurnAsync == null || lease != null)
                        chair = await QueryRoutesAsync(session, $"projects-council-{session.CouncilID}-chair",
                            ChairSystemPrompt(), chairPrompt, modelRoutes, ChairMaxTokens, ct);
                    if (chair is { Success: true } && !string.IsNullOrWhiteSpace(chair.Text))
                        await RecordStatementAsync(session, "Chair", 3, chair);
                }
                if (chair is { Success: true } && !string.IsNullOrWhiteSpace(chair.Text))
                {
                    session.VerdictText = chair.Text.Trim();
                    session.RecommendationClass = ParseRecommendationClass(session.VerdictText);
                }
                else
                {
                    // No Chair synthesis — fall back to a plain concatenation so the Commander still gets signal.
                    session.VerdictText = "Chair synthesis unavailable. Panel positions:\n\n" +
                        string.Join("\n\n", openingByRole.Select(kv => $"[{kv.Key}] {kv.Value}"));
                }

                session.Status = CouncilStatus.Completed;
                session.CompletedAt = DateTime.UtcNow;
                store.Update(session);

                AppendEvent(session, ProjectEventTypes.CouncilVerdict, "commander",
                    $"Council verdict — {Trunc(session.Topic, 100)}: {Trunc(session.VerdictText, 1500)}",
                    new { councilID = session.CouncilID, recommendationClass = session.RecommendationClass.ToString(), totalCostUsd = session.TotalCostUsd });
                return session;
            }
            catch (OperationCanceledException)
            {
                session.Status = CouncilStatus.Cancelled;
                session.Error = "Council cancelled (wake interrupted).";
                session.CompletedAt = DateTime.UtcNow;
                store.Update(session);
                throw;
            }
            catch (Exception ex)
            {
                log($"ProjectCouncilRunner: council {session.CouncilID} failed: {ex.Message}");
                session.Status = CouncilStatus.Failed;
                session.Error = ex.Message;
                session.CompletedAt = DateTime.UtcNow;
                store.Update(session);
                return session;
            }
        }

        /// <summary>Formats a finished council into the text handed back to the Commander as the tool result.</summary>
        public static string FormatForCommander(CouncilSession session)
        {
            if (session.Status != CouncilStatus.Completed || string.IsNullOrWhiteSpace(session.VerdictText))
                return session.Error ?? "The council did not reach a verdict.";
            return $"COUNCIL VERDICT [{session.RecommendationClass}] ({session.Roles.Count} seats + Chair, {session.Statements.Count} statements, " +
                   $"${session.TotalCostUsd:0.####} spent):\n\n{session.VerdictText}\n\n" +
                   "(This is advisory — you decide and remain accountable. The full transcript is on the timeline.)";
        }

        // ── rounds ──

        private async Task<string?> RunOpeningAsync(CouncilSession session, string role, string sharedContext,
            IReadOnlyList<string> modelRoutes, CancellationToken ct)
        {
            string prompt = sharedContext +
                $"\n\nYou are the {role} seat. Give your OPENING position:\n" +
                "1. Your recommendation.\n" +
                "2. The top 3 assumptions it rests on.\n" +
                "3. The top risks / failure modes.\n" +
                "4. What evidence would change your mind.\n" +
                "Be concrete. Play your seat's perspective hard. ≤400 words.";
            await using var lease = AcquireTurnAsync == null ? null : await AcquireTurnAsync(session.ProjectID, ct);
            if (AcquireTurnAsync != null && lease == null) return null;
            var turn = await QueryRoutesAsync(session, $"projects-council-{session.CouncilID}-{Slug(role)}",
                RoleSystemPrompt(role), prompt, modelRoutes, OpeningMaxTokens, ct);
            if (turn is not { Success: true } || string.IsNullOrWhiteSpace(turn.Text)) return null;
            await RecordStatementAsync(session, role, 1, turn);
            return turn.Text.Trim();
        }

        private async Task RunRebuttalAsync(CouncilSession session, string role,
            Dictionary<string, string> openingByRole, IReadOnlyList<string> modelRoutes, CancellationToken ct)
        {
            var others = openingByRole.Where(kv => kv.Key != role).ToList();
            string othersBlock = string.Join("\n\n", others.Select(kv => $"--- {kv.Key} ---\n{kv.Value}"));
            string prompt =
                "The other seats gave these opening positions:\n\n" + othersBlock +
                "\n\nAttack the weakest load-bearing claim in each. Concede the points that survive scrutiny. " +
                "Then restate your FINAL position in ≤250 words, explicitly noting anything you changed your mind about.";
            await using var lease = AcquireTurnAsync == null ? null : await AcquireTurnAsync(session.ProjectID, ct);
            if (AcquireTurnAsync != null && lease == null) return;
            CouncilTurn? turn = null;
            Exception? lastError = null;
            foreach (string model in modelRoutes)
            {
                try
                {
                    turn = await ContinueAsync!($"projects-council-{session.CouncilID}-{Slug(role)}", prompt, model, RebuttalMaxTokens, ct);
                    if (turn is { Success: true }) break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { lastError = ex; }
            }
            if (turn is not { Success: true } && lastError != null) log($"Council rebuttal routes failed: {lastError.Message}");
            if (turn is { Success: true } && !string.IsNullOrWhiteSpace(turn.Text))
                await RecordStatementAsync(session, role, 2, turn);
        }

        private async Task<CouncilTurn?> QueryRoutesAsync(CouncilSession session, string sessionID,
            string? systemPrompt, string prompt, IReadOnlyList<string> modelRoutes, int maxTokens, CancellationToken ct)
        {
            CouncilTurn? last = null;
            Exception? lastError = null;
            foreach (string model in modelRoutes)
            {
                try
                {
                    last = await QueryAsync!(sessionID, systemPrompt, prompt, model, maxTokens, ct);
                    if (last is { Success: true }) return last;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { lastError = ex; }
            }
            if (last == null && lastError != null) throw lastError;
            return last;
        }

        private CouncilSession FinishOnBudget(CouncilSession session, Dictionary<string, string> openingByRole)
        {
            session.Status = CouncilStatus.Failed;
            session.Error = "Council stopped early — the project ran out of token budget mid-deliberation.";
            session.VerdictText = "Council incomplete (budget exhausted). Panel openings so far:\n\n" +
                string.Join("\n\n", openingByRole.Select(kv => $"[{kv.Key}] {kv.Value}"));
            session.CompletedAt = DateTime.UtcNow;
            store.Update(session);
            return session;
        }

        // ── recording ──

        private async Task RecordStatementAsync(CouncilSession session, string role, int round, CouncilTurn turn)
        {
            var statement = new CouncilStatement
            {
                Role = role,
                Round = round,
                Text = turn.Text.Trim(),
                PromptTokens = turn.PromptTokens,
                CompletionTokens = turn.CompletionTokens,
                CostUsd = turn.CostUsd ?? 0,
            };
            // Round 1 and 2 run their panelists in parallel and all mutate this one session object.
            // Hold the lock across the persist too — store.Update serializes Statements, so another
            // panelist adding to the list mid-serialize would throw "collection modified".
            lock (session)
            {
                session.Statements.Add(statement);
                session.TotalCostUsd += statement.CostUsd;
                store.Update(session);
            }
            if ((turn.PromptTokens > 0 || turn.CompletionTokens > 0) && RecordSpendAsync != null)
                await RecordSpendAsync(session.ProjectID, turn.PromptTokens, turn.CompletionTokens, turn.GenerationId, turn.CostUsd);

            string roundName = round switch { 1 => "opening", 2 => "rebuttal", 3 => "synthesis", _ => "statement" };
            AppendEvent(session, ProjectEventTypes.CouncilStatement, role == "Chair" ? "commander" : "system",
                $"{role} ({roundName}): {Trunc(statement.Text, 300)}",
                new { councilID = session.CouncilID, role, round });
        }

        // ── prompts ──

        private string BuildSharedContext(Project project, CouncilSession session)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"COUNCIL BRIEFING — project \"{project.Name}\" (status: {project.Status}).");
            sb.AppendLine($"Project goal: {project.Goal}");
            string budget = DescribeBudget?.Invoke(project.ProjectID) ?? "";
            if (!string.IsNullOrWhiteSpace(budget)) sb.AppendLine($"Budget: {budget}");
            string plan = DescribeGrandPlan?.Invoke(project.ProjectID) ?? "";
            if (!string.IsNullOrWhiteSpace(plan)) sb.AppendLine(plan);
            sb.AppendLine();
            sb.AppendLine($"TOPIC: {session.Topic}");
            sb.AppendLine($"URGENCY: {session.Urgency}");
            sb.AppendLine();
            sb.AppendLine("BRIEFING (this is everything you know — the Commander compiled it for you; you have no other tools or sources):");
            sb.Append(ProjectsContextBudget.TruncateToTokens(session.Briefing, BriefingTokenCap));
            return sb.ToString();
        }

        private string BuildChairPrompt(CouncilSession session)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"TOPIC: {session.Topic}");
            sb.AppendLine();
            sb.AppendLine("BRIEFING:");
            sb.AppendLine(ProjectsContextBudget.TruncateToTokens(session.Briefing, BriefingTokenCap));
            sb.AppendLine();
            sb.AppendLine("FULL PANEL TRANSCRIPT:");
            foreach (var st in session.Statements.Where(s => s.Round <= 2).OrderBy(s => s.Round).ThenBy(s => s.Role))
            {
                string roundName = st.Round == 1 ? "Opening" : "Rebuttal";
                sb.AppendLine($"=== {st.Role} — {roundName} ===");
                sb.AppendLine(st.Text);
                sb.AppendLine();
            }
            sb.AppendLine("Synthesize the council's verdict. Do NOT vote-count — weigh the arguments. Output these sections:");
            sb.AppendLine("DECISION CLASS: exactly one of PROCEED | PROCEED_WITH_CONDITIONS | NEEDS_USER_DECISION | HARD_POLICY_BLOCK. HARD_POLICY_BLOCK is only for an actual governing policy constraint; feasibility or strategic disagreement is advisory, not a veto.");
            sb.AppendLine("RECOMMENDATION: the single course of action, stated plainly.");
            sb.AppendLine("KEY RISKS: the risks that survived debate.");
            sb.AppendLine("DISSENTS (preserved): quote any minority view the panel could not resolve, verbatim in spirit.");
            sb.AppendLine("CONDITIONS / TRIPWIRES: what must hold for this to work; what would reverse it.");
            sb.AppendLine("CONFIDENCE: low | medium | high.");
            sb.AppendLine("If the panel genuinely lacked the information to decide well, instead LEAD with: 'INSUFFICIENT INFORMATION — gather <what> and reconvene.'");
            return sb.ToString();
        }

        private static string RoleSystemPrompt(string role)
        {
            string common = "You are one seat on an adversarial decision council convened by the Commander of an autonomous " +
                "project. The council exists to pressure-test a decision before it is made. Reason only from the briefing " +
                "you are given — you have no tools and no other information. Be rigorous, concrete, and brief.";
            string persona = role switch
            {
                "Strategist" => "You hold the STRATEGIST seat: think in long horizons and second-order effects. Ask whether this " +
                    "advances the mission, what compounding advantages or path-dependencies it creates, and what the strongest " +
                    "version of the plan looks like.",
                "Skeptic" => "You hold the SKEPTIC / RED-TEAM seat: find the strongest case AGAINST the emerging consensus — hidden " +
                    "assumptions, failure modes, cheaper alternatives, and reasons to do nothing. You are rewarded for being right " +
                    "when the others are wrong, never for agreeing.",
                "Pragmatist" => "You hold the PRAGMATIST seat: focus on execution reality — cost, time, complexity, what can actually " +
                    "be done now with the resources at hand, and the simplest thing that could work.",
                _ => $"You hold the {role} seat: argue that perspective as forcefully and honestly as the evidence allows.",
            };
            return common + "\n\n" + persona;
        }

        private static string ChairSystemPrompt() =>
            "You are the CHAIR of an adversarial decision council. You did not argue a side; your job is to synthesize the panel's " +
            "deliberation into a clear, decision-ready verdict for the Commander. Do not vote-count — weigh the arguments on merit. " +
            "Preserve genuine dissent rather than papering over it. If the panel lacked the information to decide well, say exactly " +
            "what to gather before deciding. Be decisive where the evidence supports it and honest where it does not.";

        // ── helpers ──

        private static string[] NormalizeRoles(string[]? roles)
        {
            if (roles == null || roles.Length == 0) return DefaultRoles;
            var cleaned = roles
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => r.Trim())
                .Where(r => !string.Equals(r, "Chair", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToArray();
            if (cleaned.Length < 2) return DefaultRoles;
            return cleaned;
        }

        private static string NormalizeUrgency(string urgency)
        {
            string u = (urgency ?? "").Trim().ToLowerInvariant();
            return u is "routine" or "elevated" or "critical" ? u : "routine";
        }

        private static string Slug(string role)
        {
            var chars = role.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
            return new string(chars).Trim('-');
        }

        internal static string InputFingerprint(string topic, string briefing)
        {
            static string Normalize(string value) => string.Join(' ', (value ?? "")
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
            byte[] bytes = Encoding.UTF8.GetBytes(Normalize(topic) + "\n" + Normalize(briefing));
            return Convert.ToHexString(SHA256.HashData(bytes));
        }

        internal static CouncilRecommendationClass ParseRecommendationClass(string? verdict)
        {
            if (string.IsNullOrWhiteSpace(verdict)) return CouncilRecommendationClass.Unclassified;
            string first = verdict.Split('\n').FirstOrDefault(l => l.Contains("DECISION CLASS", StringComparison.OrdinalIgnoreCase)) ?? "";
            string normalized = first.Replace("-", "_", StringComparison.Ordinal).Replace(" ", "_", StringComparison.Ordinal).ToUpperInvariant();
            if (normalized.Contains("HARD_POLICY_BLOCK", StringComparison.Ordinal)) return CouncilRecommendationClass.HardPolicyBlock;
            if (normalized.Contains("NEEDS_USER_DECISION", StringComparison.Ordinal)) return CouncilRecommendationClass.NeedsUserDecision;
            if (normalized.Contains("PROCEED_WITH_CONDITIONS", StringComparison.Ordinal)) return CouncilRecommendationClass.ProceedWithConditions;
            if (normalized.Contains("PROCEED", StringComparison.Ordinal)) return CouncilRecommendationClass.Proceed;
            return CouncilRecommendationClass.Unclassified;
        }

        private CouncilSession Refused(string reason) =>
            new() { Status = CouncilStatus.Failed, Error = reason };

        private void AppendEvent(CouncilSession session, string type, string author, string text, object payload)
        {
            eventLog.Append(new ProjectEvent
            {
                ProjectID = session.ProjectID,
                WakeID = session.WakeID,
                AgentID = "commander",
                Type = type,
                Author = author,
                Text = text,
                PayloadJson = JsonConvert.SerializeObject(payload),
            });
        }

        private static string Trunc(string? s, int max) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
    }
}
