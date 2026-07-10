using System.Text;

namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Prompt assembly for the Projects Commander: the wake seed (standing digest + recent
    /// events + retrieval hits + triggering stimulus) and the digest-rebuild prompt.
    /// The Commander's full system prompt / escalation doctrine is a Phase 3 artifact —
    /// this file owns everything that turns log/digest state into text.
    /// </summary>
    public static class ProjectCommanderPrompts
    {
        /// <summary>How many recent events are considered for the verbatim window of a wake seed.</summary>
        public const int RecentEventsConsidered = 120;

        // ── wake seed ──

        /// <summary>
        /// Builds the seed message for one Commander wake. Everything here is budget-fitted:
        /// the digest, the recent-events window and the retrieval hits each live inside their
        /// own ProjectsContextBudget bucket, so a wake's input size is bounded no matter how
        /// old the project is (§7 "no unbounded conversation growth").
        /// </summary>
        public static string BuildWakeSeed(
            Project project,
            ProjectDigest digest,
            List<ProjectEvent> recentEvents,
            List<ProjectRetrievalIndex.RetrievalHit> retrievalHits,
            string triggerDescription,
            List<Omnipotent.Services.KliveRAG.KnowledgeHit>? knowledgeHits = null,
            string? observablesBlock = null,
            string? grandPlanBlock = null,
            string? accountsBlock = null)
        {
            var sb = new StringBuilder();

            sb.AppendLine("── PROJECT ──");
            sb.AppendLine($"Name: {project.Name}");
            sb.AppendLine($"Goal: {project.Goal}");
            sb.AppendLine($"Status: {project.Status}");
            sb.AppendLine($"Budgets: tokens ${project.TokenBudgetUsd:0.##} · money ${project.MoneyBudgetUsd:0.##} (autonomous ≤ ${project.MoneyAutonomousThresholdUsd:0.##}/action) · agent cap {project.SubAgentCap}");

            // The approved Grand Plan is the standing north star — surfaced right under the header so
            // every wake anchors on it. Read it in full with get_grand_plan; revise via amend_grand_plan.
            if (!string.IsNullOrWhiteSpace(grandPlanBlock))
            {
                sb.AppendLine("── GRAND PLAN (approved north star — amend via amend_grand_plan) ──");
                sb.AppendLine(ProjectsContextBudget.TruncateToTokens(grandPlanBlock, ProjectsContextBudget.GrandPlanBudget));
            }

            sb.AppendLine("── STANDING DIGEST ──");
            string digestBlock = ComposeDigestBlock(digest);
            sb.AppendLine(ProjectsContextBudget.TruncateToTokens(digestBlock, ProjectsContextBudget.DigestBudget));

            // Live observable values — read from the store at seed time, never digested prose,
            // so the numbers the Commander sees are exactly the numbers Klives sees.
            if (!string.IsNullOrWhiteSpace(observablesBlock))
            {
                sb.AppendLine("── OBSERVABLES (live values you maintain for Klives via update_observable) ──");
                sb.AppendLine(ProjectsContextBudget.TruncateToTokens(observablesBlock, ProjectsContextBudget.ObservablesBudget));
            }

            // Shared account registry (global across every project + KliveAgent). Reuse before
            // creating a duplicate; account_list for details, account_register after any signup.
            if (!string.IsNullOrWhiteSpace(accountsBlock))
            {
                sb.AppendLine("── SHARED ACCOUNTS (global registry — reuse before creating; account_list for details) ──");
                sb.AppendLine(ProjectsContextBudget.TruncateToTokens(accountsBlock, ProjectsContextBudget.AccountsBudget));
            }

            // Cross-system knowledge (other projects, KliveAgent memory, Omniscience, repo docs). The
            // Commander's own log is deliberately NOT here — that's the RETRIEVED-FROM-LOG leg below.
            if (knowledgeHits is { Count: > 0 })
            {
                sb.AppendLine("── RELEVANT KNOWLEDGE (Klives' knowledge base: other projects, KliveAgent memory, Omniscience, docs) ──");
                var fitted = ProjectsContextBudget.FitItemsInBudget(
                    knowledgeHits,
                    ProjectsContextBudget.KnowledgeBudget,
                    h => h.Text,
                    h => h.Score);
                foreach (var h in fitted)
                    sb.AppendLine($"[{h.Source}{(string.IsNullOrEmpty(h.Title) ? "" : " · " + h.Title)}] {ProjectsContextBudget.TruncateToTokens(h.Text, 200)} (doc:{h.DocId})");
            }

            if (retrievalHits.Count > 0)
            {
                sb.AppendLine("── RETRIEVED FROM THE FULL LOG (relevant to this wake's trigger) ──");
                var fitted = ProjectsContextBudget.FitItemsInBudget(
                    retrievalHits,
                    ProjectsContextBudget.RetrievalBudget,
                    h => h.Snippet,
                    h => h.Score);
                foreach (var hit in fitted.OrderBy(h => h.Sequence))
                    sb.AppendLine($"[#{hit.Sequence} {hit.Timestamp:MM-dd HH:mm} {hit.Type}] {hit.Snippet}");
            }

            if (recentEvents.Count > 0)
            {
                sb.AppendLine("── RECENT EVENTS (newest last) ──");
                var fitted = ProjectsContextBudget.FitItemsInBudget(
                    recentEvents.Select((e, i) => (evt: e, idxFromEnd: recentEvents.Count - 1 - i)),
                    ProjectsContextBudget.RecentEventsBudget,
                    x => DescribeEvent(x.evt),
                    x => ProjectsContextBudget.ScoreEvent(x.evt.Text, triggerDescription, x.idxFromEnd));
                foreach (var x in fitted.OrderBy(x => x.evt.Sequence))
                    sb.AppendLine(DescribeEvent(x.evt));
            }

            sb.AppendLine("── THIS WAKE'S TRIGGER ──");
            sb.AppendLine(ProjectsContextBudget.TruncateToTokens(triggerDescription, ProjectsContextBudget.StimulusBudget));

            return sb.ToString();
        }

        /// <summary>One-line rendering of an event for the recent-events window.</summary>
        public static string DescribeEvent(ProjectEvent e)
        {
            string who = e.Author switch
            {
                "commander" => "COMMANDER",
                "klives" => "KLIVES",
                "stimulus" => "STIMULUS",
                "agent" => $"AGENT {e.AgentID ?? "?"}",
                _ => "SYSTEM",
            };
            string body = e.Type is ProjectEventTypes.ToolCall or ProjectEventTypes.ToolResult
                ? $"{e.ToolName}: {Truncate(e.Text, 200)}"
                : Truncate(e.Text, 400);
            return $"[#{e.Sequence} {e.Timestamp:MM-dd HH:mm} {e.Type}] {who}: {body}";
        }

        private static string ComposeDigestBlock(ProjectDigest d)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"CURRENT PLAN: {OrNone(d.CurrentPlan)}");
            sb.AppendLine($"ORG CHART: {OrNone(d.OrgChart)}");
            sb.AppendLine($"BUDGET STATE: {OrNone(d.BudgetState)}");
            sb.AppendLine($"OPEN THREADS: {OrNone(d.OpenThreads)}");
            sb.AppendLine($"EARLIER HISTORY (compacted): {OrNone(d.RollingSummary)}");
            return sb.ToString();
        }

        // ── digest rebuild ──

        private const string PlanHeader = "## PLAN";
        private const string OrgHeader = "## ORG";
        private const string BudgetHeader = "## BUDGET";
        private const string OpenHeader = "## OPEN";
        private const string SummaryHeader = "## SUMMARY";

        /// <summary>
        /// Prompt for the utility model to fold new events into the standing digest.
        /// Output format is five fixed markdown sections, parsed by <see cref="ParseDigestResponse"/>.
        /// </summary>
        public static string BuildDigestRebuildPrompt(Project project, ProjectDigest existing, List<ProjectEvent> newEvents)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You maintain the standing digest of a long-running autonomous project. Merge the existing digest with the new events below.");
            sb.AppendLine($"The project's goal: {project.Goal}");
            sb.AppendLine();
            sb.AppendLine($"Output EXACTLY five sections with these exact headers, nothing before the first header:");
            sb.AppendLine($"{PlanHeader} — the tactical plan: a 'Focus:' line (one sentence on what's being driven at now) then a 'Next:' list of concrete next steps as bullets (a handful). ≤120 words.");
            sb.AppendLine($"{OrgHeader} — which agents exist, their tier/role, what each is doing (≤80 words).");
            sb.AppendLine($"{BudgetHeader} — spend vs budget and burn trend, as stated in events (≤40 words).");
            sb.AppendLine($"{OpenHeader} — unresolved questions, pending approvals, blockers (≤80 words).");
            sb.AppendLine($"{SummaryHeader} — compact narrative of everything older than the recent window; merge, don't append (≤250 words).");
            sb.AppendLine("Keep decisions, requirements, verified outcomes and open issues. Drop tool mechanics and superseded states.");
            sb.AppendLine();
            sb.AppendLine("EXISTING DIGEST:");
            sb.AppendLine($"{PlanHeader}\n{OrNone(DescribeExistingPlan(existing))}");
            sb.AppendLine($"{OrgHeader}\n{OrNone(existing.OrgChart)}");
            sb.AppendLine($"{BudgetHeader}\n{OrNone(existing.BudgetState)}");
            sb.AppendLine($"{OpenHeader}\n{OrNone(existing.OpenThreads)}");
            sb.AppendLine($"{SummaryHeader}\n{OrNone(existing.RollingSummary)}");
            sb.AppendLine();
            sb.AppendLine("NEW EVENTS:");
            foreach (var e in newEvents)
                sb.AppendLine(DescribeEvent(e));
            return sb.ToString();
        }

        /// <summary>
        /// Parses the five-section digest response. If the model ignored the format entirely,
        /// the whole response is folded into RollingSummary and the structured fields are
        /// carried over unchanged — a degraded digest beats a lost one.
        /// </summary>
        public static ProjectDigest? ParseDigestResponse(string response, ProjectDigest existing)
        {
            if (string.IsNullOrWhiteSpace(response)) return null;
            var result = new ProjectDigest
            {
                ProjectID = existing.ProjectID,
                CurrentPlan = existing.CurrentPlan,
                CurrentFocus = existing.CurrentFocus,
                NextSteps = new List<string>(existing.NextSteps),
                OrgChart = existing.OrgChart,
                BudgetState = existing.BudgetState,
                OpenThreads = existing.OpenThreads,
                RollingSummary = existing.RollingSummary,
                RecentStuckLoopTrips = existing.RecentStuckLoopTrips,
            };

            var sections = SplitSections(response);
            if (sections.Count == 0)
            {
                result.RollingSummary = response.Trim();
                return result;
            }
            if (sections.TryGetValue(PlanHeader, out var plan)) ApplyPlanSection(result, plan);
            if (sections.TryGetValue(OrgHeader, out var org)) result.OrgChart = org;
            if (sections.TryGetValue(BudgetHeader, out var budget)) result.BudgetState = budget;
            if (sections.TryGetValue(OpenHeader, out var open)) result.OpenThreads = open;
            if (sections.TryGetValue(SummaryHeader, out var summary)) result.RollingSummary = summary;
            return result;
        }

        /// <summary>Renders the existing tactical plan (focus + next steps) for echoing back into the rebuild prompt.</summary>
        private static string DescribeExistingPlan(ProjectDigest d)
        {
            if (string.IsNullOrWhiteSpace(d.CurrentFocus) && d.NextSteps.Count == 0)
                return d.CurrentPlan;
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(d.CurrentFocus)) sb.AppendLine($"Focus: {d.CurrentFocus}");
            if (d.NextSteps.Count > 0)
            {
                sb.AppendLine("Next:");
                foreach (var s in d.NextSteps) sb.AppendLine($"- {s}");
            }
            return sb.ToString().Trim();
        }

        /// <summary>Parses the ## PLAN section into CurrentFocus + NextSteps, keeping the raw text in CurrentPlan.</summary>
        private static void ApplyPlanSection(ProjectDigest result, string plan)
        {
            result.CurrentPlan = plan.Trim();
            string focus = "";
            var steps = new List<string>();
            bool inNext = false;
            foreach (var raw in plan.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("Focus:", StringComparison.OrdinalIgnoreCase))
                {
                    focus = line[6..].Trim();
                    inNext = false;
                }
                else if (line.StartsWith("Next:", StringComparison.OrdinalIgnoreCase))
                {
                    inNext = true;
                    var rest = line[5..].Trim();
                    if (rest.Length > 0) steps.Add(rest);
                }
                else if (line.StartsWith('-') || line.StartsWith('*') || line.StartsWith('•'))
                {
                    steps.Add(line.TrimStart('-', '*', '•', ' ').Trim());
                }
                else if (inNext)
                {
                    steps.Add(line);
                }
            }
            if (focus.Length > 0) result.CurrentFocus = focus;
            steps = steps.Where(s => s.Length > 0).ToList();
            if (steps.Count > 0) result.NextSteps = steps;
        }

        private static Dictionary<string, string> SplitSections(string response)
        {
            var headers = new[] { PlanHeader, OrgHeader, BudgetHeader, OpenHeader, SummaryHeader };
            var found = new List<(string header, int index)>();
            foreach (var h in headers)
            {
                int i = response.IndexOf(h, StringComparison.OrdinalIgnoreCase);
                if (i >= 0) found.Add((h, i));
            }
            var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var ordered = found.OrderBy(f => f.index).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                int start = ordered[i].index + ordered[i].header.Length;
                int end = i + 1 < ordered.Count ? ordered[i + 1].index : response.Length;
                sections[ordered[i].header] = response[start..end].Trim();
            }
            return sections;
        }

        private static string OrNone(string? s) => string.IsNullOrWhiteSpace(s) ? "(none)" : s.Trim();
        private static string Truncate(string? s, int max) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
    }
}
