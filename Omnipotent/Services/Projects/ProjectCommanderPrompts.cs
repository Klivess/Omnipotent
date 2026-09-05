using System.Text;
using Omnipotent.Services.KliveLLM;

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
            string? accountsBlock = null,
            string? filesBlock = null,
            string? runtimeStateBlock = null,
            string? kliveAgentContextBlock = null,
            string? directivesBlock = null,
            int? recentEventsBudget = null,
            bool chronologicalEvents = true,
            string? approvalsBlock = null,
            string? taskForceBlock = null,
            string? externalActionsBlock = null,
            bool tokensUnmetered = false,
            List<ToolSessionBriefSection>? briefSections = null)
        {
            var sb = new ToolSessionBriefBuilder();

            // PREFIX-CACHE ORDER (see PromptPrefixStability). This header used to open with the
            // wall-clock and a recomputed relative age, which made the seed's SECOND line differ on
            // every wake — so the project header, capability truth, directives and grand plan behind
            // it were re-prefilled from scratch every time even when their text had not changed.
            // Absolute stamps here (they mean the same thing and never churn); the live clock is
            // stated with the trigger at the end, where the genuinely per-wake content lives.
            sb.BeginSection("project","── PROJECT ──");
            sb.AppendLine($"Name: {project.Name}");
            sb.AppendLine($"Goal: {project.Goal}");
            sb.AppendLine($"Status: {project.Status} · project created {Data_Handling.TemporalFormat.StampMinute(project.CreatedAt)}");
            // Under a flat-fee router the token budget is not a live constraint, and stating one
            // invites the Commander to ration model calls it is not actually paying for. The MONEY
            // budget is unaffected: that governs real purchases, not inference.
            string tokenBudgetLine = tokensUnmetered
                ? "tokens unmetered (flat-fee router — do not ration model calls to save money)"
                : $"tokens ${project.TokenBudgetUsd:0.##}";
            sb.AppendLine($"Budgets: {tokenBudgetLine} · money ${project.MoneyBudgetUsd:0.##} (autonomous ≤ ${project.MoneyAutonomousThresholdUsd:0.##}/action) · agent cap {project.SubAgentCap}");
            sb.BeginSection("capabilities","── RUNTIME CAPABILITY TRUTH (authoritative) ──");
            sb.AppendLine(ProjectPromptHygiene.CapabilityTruth);

            // This is intentionally ahead of the grand plan/digest/retrieval legs. A rule from
            // Klives is authoritative and must not be summarized away, outranked by a stale plan,
            // or omitted because an unrelated trigger had better lexical retrieval score.
            if (!string.IsNullOrWhiteSpace(directivesBlock))
            {
                sb.BeginSection("directives","── NON-NEGOTIABLE KLIVES DIRECTIVES (durable project memory; obey before all plans) ──");
                sb.AppendLine(ProjectsContextBudget.TruncateToTokens(directivesBlock, ProjectsContextBudget.DirectivesBudget));
            }

            // Immediately after the directives, because an approval request and its answer are Klives
            // speaking too. Without this leg the Commander cannot see its own pending asks, so it re-opens
            // a card that is already waiting in front of him and re-proposes what he already refused.
            if (!string.IsNullOrWhiteSpace(approvalsBlock))
            {
                sb.BeginSection("approvals","── APPROVALS (your own outstanding requests and Klives' recent decisions) ──");
                sb.AppendLine(ProjectsContextBudget.TruncateToTokens(approvalsBlock, ProjectsContextBudget.ApprovalsBudget));
            }

            // The approved Grand Plan is the standing north star — surfaced right under the header so
            // every wake anchors on it. Read it in full with get_grand_plan; revise via amend_grand_plan.
            if (!string.IsNullOrWhiteSpace(grandPlanBlock))
            {
                sb.BeginSection("grand-plan","── GRAND PLAN (approved north star — read via grand_plan op:get, revise via op:amend) ──");
                sb.AppendLine(ProjectsContextBudget.TruncateToTokens(grandPlanBlock, ProjectsContextBudget.GrandPlanBudget));
            }

            // These two are REFERENCE, not news: the account registry only moves when someone signs up
            // somewhere, and the bridge is deliberately task-independent. Grouping them here with the
            // other rarely-changing blocks — rather than leaving them buried behind the roster, typed
            // state and digest — is what lets a provider's prefix cache carry them from one wake to the
            // next, since the match ends at the first block that churned. See PromptPrefixStability.
            if (!string.IsNullOrWhiteSpace(accountsBlock))
            {
                sb.BeginSection("accounts","── SHARED ACCOUNTS (global registry — reuse before creating; account op:list for details) ──");
                sb.AppendLine(ProjectsContextBudget.TruncateToTokens(accountsBlock, ProjectsContextBudget.AccountsBudget));
            }

            if (!string.IsNullOrWhiteSpace(kliveAgentContextBlock))
            {
                sb.BeginSection("kliveagent","── KLIVEAGENT LIVE BRIDGE (same service graph/capabilities/recipes available to this agent) ──");
                sb.AppendLine(ProjectsContextBudget.TruncateToTokens(kliveAgentContextBlock, ProjectsContextBudget.KnowledgeBudget));
            }

            // Directly under the plan, because staffing is decided before the Commander picks up any
            // work itself: the plan says what must happen, this says who is free to do it. The old
            // one-line org chart carried no last-report, no silence age and no slot arithmetic, so a
            // roster sitting at one worker with eleven slots free looked exactly like a full one.
            if (!string.IsNullOrWhiteSpace(taskForceBlock))
            {
                sb.BeginSection("task-force","── YOUR TASK FORCE (muster this FIRST: retire finished workers, re-task idle ones, staff free slots) ──");
                sb.AppendLine(ProjectsContextBudget.TruncateToTokens(taskForceBlock, ProjectsContextBudget.TaskForceBudget));
            }

            // Typed state is authoritative for blockers, verified facts, canonical artifacts and
            // resume actions. It precedes model-authored digest prose so a stale summary cannot win.
            if (!string.IsNullOrWhiteSpace(runtimeStateBlock))
            {
                sb.BeginSection("execution-state","── TYPED EXECUTION STATE (authoritative; update with checkpoint tools) ──");
                sb.AppendLine(ProjectsContextBudget.TruncateToTokens(
                    ProjectPromptHygiene.ScrubState(runtimeStateBlock), ProjectsContextBudget.DigestBudget));
            }

            // The ledger of real side effects sits above the digest for the same reason typed state
            // does: prose can claim an email was sent, this cannot. It is also the duplicate guard —
            // a wake that lost its context would otherwise sign up or apply a second time.
            if (!string.IsNullOrWhiteSpace(externalActionsBlock))
            {
                sb.BeginSection("external-actions","── EXTERNAL ACTION LEDGER (evidenced side effects in the real world; authoritative) ──");
                sb.AppendLine(ProjectsContextBudget.TruncateToTokens(
                    externalActionsBlock, ProjectsContextBudget.ObservablesBudget));
            }

            // Absolute, not StampWithAge: a relative age recomputes every wake, so an UNCHANGED digest
            // would still hand the provider a different byte here and cost a re-prefill of everything
            // after it. The clock line at the end of the seed is what the age was for.
            sb.BeginSection("digest",$"── STANDING DIGEST (last rebuilt {Data_Handling.TemporalFormat.StampMinute(digest.UpdatedAt)}) ──");
            string digestBlock = ComposeDigestBlock(digest);
            sb.AppendLine(ProjectsContextBudget.TruncateToTokens(digestBlock, ProjectsContextBudget.DigestBudget));

            // Live observable values — read from the store at seed time, never digested prose,
            // so the numbers the Commander sees are exactly the numbers Klives sees.
            if (!string.IsNullOrWhiteSpace(observablesBlock))
            {
                sb.BeginSection("observables","── OBSERVABLES (live values you maintain for Klives via observable op:set) ──");
                sb.AppendLine(ProjectsContextBudget.TruncateToTokens(observablesBlock, ProjectsContextBudget.ObservablesBudget));
            }

            // Persistent project volume shared by Klive, the Commander and every worker. The
            // summary is intentionally small; list_files/stat_file provide paged detail on demand.
            if (!string.IsNullOrWhiteSpace(filesBlock))
            {
                sb.BeginSection("files","── SHARED PROJECT FILES (/project — inspect before work; list_files / manage_files op:stat for more) ──");
                sb.AppendLine(ProjectsContextBudget.TruncateToTokens(filesBlock, ProjectsContextBudget.SharedFilesBudget));
            }

            // Cross-system knowledge (other projects, KliveAgent memory, Omniscience, repo docs). The
            // Commander's own log is deliberately NOT here — that's the RETRIEVED-FROM-LOG leg below.
            if (knowledgeHits is { Count: > 0 })
            {
                sb.BeginSection("knowledge","── RELEVANT KNOWLEDGE (Klives' knowledge base: other projects, KliveAgent memory, Omniscience, docs) ──");
                var fitted = ProjectsContextBudget.FitItemsInBudget(
                    knowledgeHits,
                    ProjectsContextBudget.KnowledgeBudget,
                    h => h.Text,
                    h => h.Score);
                foreach (var h in fitted)
                    sb.AppendLine($"[{h.Source}{(string.IsNullOrEmpty(h.Title) ? "" : " · " + h.Title)}] {ProjectsContextBudget.TruncateToTokens(h.Text, 200)} (doc:{h.DocId})");
            }

            var visibleRetrievalHits = retrievalHits
                .Where(ProjectPromptHygiene.IsAgentVisibleRetrievalHit)
                .ToList();
            if (visibleRetrievalHits.Count > 0)
            {
                sb.BeginSection("retrieved-events","── RETRIEVED FROM THE FULL LOG (relevant to this wake's trigger) ──");
                var fitted = ProjectsContextBudget.FitItemsInBudget(
                    visibleRetrievalHits,
                    ProjectsContextBudget.RetrievalBudget,
                    h => h.Snippet,
                    h => h.Score);
                foreach (var hit in fitted.OrderBy(h => h.Sequence))
                    sb.AppendLine($"[#{hit.Sequence} {hit.Timestamp:yyyy-MM-dd HH:mm} {hit.Type}] {hit.Snippet}");
            }

            // Chronological and contiguous, not score-selected. The events leg is a timeline: if it is
            // allowed to keep the highest-scoring events and quietly drop the ones between them, the
            // agent reads a complete-looking history that is missing the record of what it already did.
            var visibleRecentEvents = recentEvents
                .Where(ProjectPromptHygiene.IsAgentVisibleEvent)
                .ToList();
            int eventsBudget = recentEventsBudget ?? ProjectsContextBudget.RecentEventsBudget;
            if (chronologicalEvents)
            {
                var entries = new List<ToolSessionBriefEntry>();
                string history = RenderHistoryBlock("── RECENT EVENTS (newest last) ──", visibleRecentEvents, eventsBudget, entries);
                sb.AppendJournal("recent-events", history, entries);
            }
            else if (visibleRecentEvents.Count > 0)
            {
                // Legacy relevance-ranked window, kept behind the ChronologicalRecentEvents setting.
                sb.BeginSection("recent-events","── RECENT EVENTS (newest last) ──");
                var fitted = ProjectsContextBudget.FitItemsInBudget(
                    visibleRecentEvents.Select((e, i) =>
                        (evt: e, idxFromEnd: visibleRecentEvents.Count - 1 - i)),
                    eventsBudget,
                    x => DescribeEvent(x.evt),
                    x => ProjectsContextBudget.ScoreEvent(x.evt.Text, triggerDescription, x.idxFromEnd));
                foreach (var x in fitted.OrderBy(x => x.evt.Sequence))
                    sb.AppendLine(DescribeEvent(x.evt));
            }

            // The clock lives here, at the very end of the seed, rather than in the header. It is the
            // most volatile line in the whole prompt — second precision — so anything placed after it
            // can never be served from a provider's prefix cache on a later wake.
            sb.BeginSection("trigger","── THIS WAKE'S TRIGGER ──", sendEveryWake: true);
            sb.AppendLine($"Now: {Data_Handling.TemporalFormat.ClockLine()} — every timestamp in this seed and in your messages is UTC; measure staleness and elapsed time against this clock.");
            sb.AppendLine(ProjectsContextBudget.TruncateToTokens(triggerDescription, ProjectsContextBudget.StimulusBudget));

            briefSections?.AddRange(sb.Build());
            return sb.ToString();
        }

        /// <summary>One-line rendering of an event for the recent-events window.</summary>
        public static string DescribeEvent(ProjectEvent e)
        {
            string who = Who(e);
            string body = e.Type is ProjectEventTypes.ToolCall or ProjectEventTypes.ToolResult
                ? $"{e.ToolName}: {Truncate(e.Text, 1200)}"
                : Truncate(e.Text, 1600);
            return $"[#{e.Sequence} {e.Timestamp:yyyy-MM-dd HH:mm} {e.Type}] {who}: {body}";
        }

        private static string Who(ProjectEvent e) => e.Author switch
        {
            "commander" => "COMMANDER",
            "klives" => "KLIVES",
            "stimulus" => "STIMULUS",
            "agent" => $"AGENT {e.AgentID ?? "?"}",
            _ => "SYSTEM",
        };

        // ── history window ──

        /// <summary>
        /// One unit of history. A tool call and the result it produced are one thing that happened, so
        /// they travel together and can collapse into a single line once they are old enough.
        /// </summary>
        public sealed record HistoryItem(ProjectEvent Anchor, ProjectEvent? Result)
        {
            public long Sequence => Anchor.Sequence;
            public DateTime Timestamp => Anchor.Timestamp;
        }

        /// <summary>
        /// Pairs each tool call with its result, leaving every other event as its own unit. Input must be
        /// in ascending sequence order; output preserves that order.
        /// </summary>
        public static List<HistoryItem> GroupHistory(IReadOnlyList<ProjectEvent> ascending)
        {
            var items = new List<HistoryItem>(ascending.Count);
            var consumed = new HashSet<int>();
            for (int i = 0; i < ascending.Count; i++)
            {
                if (consumed.Contains(i)) continue;
                var e = ascending[i];
                if (e.Type == ProjectEventTypes.ToolCall && !string.IsNullOrEmpty(e.ToolCallId))
                {
                    // The result normally lands immediately after its call, but a batch of calls can
                    // interleave, so match on the id rather than on adjacency.
                    for (int j = i + 1; j < ascending.Count; j++)
                    {
                        if (consumed.Contains(j)) continue;
                        var candidate = ascending[j];
                        if (candidate.Type != ProjectEventTypes.ToolResult) continue;
                        if (!string.Equals(candidate.ToolCallId, e.ToolCallId, StringComparison.Ordinal)) continue;
                        consumed.Add(j);
                        items.Add(new HistoryItem(e, candidate));
                        goto next;
                    }
                }
                items.Add(new HistoryItem(e, null));
            next: ;
            }
            return items;
        }

        /// <summary>Full verbatim rendering — byte-identical to what the window produced before compaction.</summary>
        public static string DescribeHistoryItemFull(HistoryItem item) =>
            item.Result == null
                ? DescribeEvent(item.Anchor)
                : DescribeEvent(item.Anchor) + "\n" + DescribeEvent(item.Result);

        /// <summary>
        /// Compact rendering for the older part of the window: a tool call and its result become one
        /// line carrying the tool, a success mark and both texts trimmed hard. This is what buys the
        /// extra history depth — the same budget reaches several times further back.
        /// </summary>
        public static string DescribeHistoryItemCompact(HistoryItem item)
        {
            var e = item.Anchor;
            if (item.Result != null || e.Type is ProjectEventTypes.ToolCall or ProjectEventTypes.ToolResult)
            {
                var result = item.Result ?? (e.Type == ProjectEventTypes.ToolResult ? e : null);
                string span = item.Result == null ? $"#{e.Sequence}" : $"#{e.Sequence}→{item.Result.Sequence}";
                string mark = result == null ? "·" : (ToolSucceeded(result) ? "✓" : "✗");
                string call = item.Result == null && e.Type == ProjectEventTypes.ToolResult
                    ? ""
                    : Truncate(Collapse(e.Text), 90);
                string outcome = result == null ? "" : Truncate(Collapse(result.Text), 160);
                string body = call.Length > 0 && outcome.Length > 0 ? $"{call} → {outcome}"
                    : call.Length > 0 ? call : outcome;
                return $"[{span} {e.Timestamp:MM-dd HH:mm} {e.ToolName ?? result?.ToolName ?? "tool"} {mark}] {body}";
            }
            return $"[#{e.Sequence} {e.Timestamp:MM-dd HH:mm} {e.Type}] {Who(e)}: {Truncate(Collapse(e.Text), 240)}";
        }

        /// <summary>
        /// Whether an item carries policy rather than mechanics. Klives' own words and the approval
        /// exchange keep their full text no matter how far back in the window they sit — compacting
        /// "Deny: keep digging" down to a fragment is exactly how a standing instruction gets lost.
        /// </summary>
        public static bool IsPolicyBearing(HistoryItem item) =>
            string.Equals(item.Anchor.Author, "klives", StringComparison.OrdinalIgnoreCase)
            || item.Anchor.Type is ProjectEventTypes.KlivesMessage
                or ProjectEventTypes.ApprovalRequested
                or ProjectEventTypes.ApprovalResolved
                or ProjectEventTypes.HumanAssistanceRequested;

        /// <summary>
        /// Renders a complete history block: the header, an explicit notice when older events did not
        /// fit, then an unbroken chronological tail. The notice matters as much as the events — a window
        /// that silently drops its middle looks complete, so the agent stops reaching for query_events
        /// and re-derives (or re-attempts) whatever fell out.
        /// </summary>
        public static string RenderHistoryBlock(string header, IReadOnlyList<ProjectEvent> ascending, int budget,
            List<ToolSessionBriefEntry>? briefEntries = null)
        {
            if (ascending.Count == 0) return "";
            var items = GroupHistory(ascending);
            var window = ProjectsContextBudget.FitEventsChronologically(
                items, budget, DescribeHistoryItemFull, DescribeHistoryItemCompact, IsPolicyBearing);
            if (window.Lines.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendLine(header);
            if (window.Dropped > 0 && window.OldestKept != null)
            {
                sb.AppendLine($"[{window.Dropped} older event group(s) omitted to fit this budget. Everything below is " +
                    $"complete and unbroken back to {window.OldestKept.Timestamp:yyyy-MM-dd HH:mm} — for anything earlier " +
                    $"call query_events with to={window.OldestKept.Timestamp:O} rather than assuming it did not happen.]");
            }
            var keptItems = items.Skip(window.Dropped).ToArray();
            for (int i = 0; i < window.Lines.Count; i++)
            {
                string line = window.Lines[i];
                sb.AppendLine(line);
                var item = keptItems[i];
                // The result sequence distinguishes an unfinished call from its later completed pair.
                // Older entries becoming compact does not make them new evidence.
                briefEntries?.Add(new($"{item.Anchor.Sequence}:{item.Result?.Sequence}", line, IsPolicyBearing(item)));
            }
            return sb.ToString().TrimEnd();
        }

        private static bool ToolSucceeded(ProjectEvent result)
        {
            if (string.IsNullOrWhiteSpace(result.PayloadJson)) return !string.Equals(result.Author, "system", StringComparison.Ordinal);
            try
            {
                var jo = Newtonsoft.Json.Linq.JObject.Parse(result.PayloadJson);
                return (bool?)jo["succeeded"] ?? true;
            }
            catch { return true; }
        }

        private static string Collapse(string? s) =>
            string.IsNullOrEmpty(s) ? "" : System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();

        private static string ComposeDigestBlock(ProjectDigest d)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"CURRENT PLAN: {ProjectPromptHygiene.ScrubState(d.CurrentPlan)}");
            sb.AppendLine($"ORG CHART: {ProjectPromptHygiene.ScrubState(d.OrgChart)}");
            sb.AppendLine($"BUDGET STATE: {ProjectPromptHygiene.ScrubState(d.BudgetState)}");
            sb.AppendLine($"OPEN THREADS: {ProjectPromptHygiene.ScrubState(d.OpenThreads)}");
            sb.AppendLine($"EARLIER HISTORY (compacted): {ProjectPromptHygiene.ScrubState(d.RollingSummary)}");
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
            sb.AppendLine($"Current time: {Data_Handling.TemporalFormat.ClockLine()}. Event stamps are UTC. When the digest mentions time, use ABSOLUTE dates (e.g. '2026-07-12'), never 'today'/'yesterday' — the digest is read days later.");
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
            sb.AppendLine($"{PlanHeader}\n{ProjectPromptHygiene.ScrubState(DescribeExistingPlan(existing))}");
            sb.AppendLine($"{OrgHeader}\n{ProjectPromptHygiene.ScrubState(existing.OrgChart)}");
            sb.AppendLine($"{BudgetHeader}\n{ProjectPromptHygiene.ScrubState(existing.BudgetState)}");
            sb.AppendLine($"{OpenHeader}\n{ProjectPromptHygiene.ScrubState(existing.OpenThreads)}");
            sb.AppendLine($"{SummaryHeader}\n{ProjectPromptHygiene.ScrubState(existing.RollingSummary)}");
            sb.AppendLine();
            sb.AppendLine("NEW EVENTS:");
            // Budget-fit like every other prompt we build. This ran unbounded over up to 2,000 events at
            // 1,200-1,600 chars each, after EVERY wake — a busy wake could push a six-figure-token prompt
            // through the utility model purely to refresh a ≤570-word digest. Oldest-first order is
            // preserved so the narrative still reads chronologically.
            var visible = newEvents.Where(ProjectPromptHygiene.IsAgentVisibleEvent).ToList();
            var fitted = ProjectsContextBudget.FitItemsInBudget(
                visible.Select((e, i) => (evt: e, idxFromEnd: visible.Count - 1 - i)),
                ProjectsContextBudget.DigestRebuildEventsBudget,
                x => DescribeEvent(x.evt),
                x => ProjectsContextBudget.ScoreEvent(x.evt.Text, project.Goal, x.idxFromEnd));
            int dropped = visible.Count - fitted.Count;
            if (dropped > 0)
                sb.AppendLine($"[{dropped} lower-signal event(s) omitted to fit the digest budget; the existing ROLLING SUMMARY already covers older ground and the full log remains on disk.]");
            foreach (var x in fitted.OrderBy(x => x.evt.Sequence))
                sb.AppendLine(DescribeEvent(x.evt));
            return sb.ToString();
        }

        /// <summary>
        /// Parses the five-section digest response. If the model ignored the format entirely,
        /// the whole response is folded into RollingSummary and the structured fields are
        /// carried over unchanged — a degraded digest beats a lost one.
        /// </summary>
        /// <param name="preserveNextSteps">
        /// True when the step ledger holds open steps and therefore owns the plan. The digest rebuild runs a
        /// utility model over the wake's events after every wake, and it used to overwrite CurrentFocus and
        /// NextSteps from that model's paraphrase — so the plan the Commander deliberately set drifted on its
        /// own between wakes. Narrative fields still rebuild; the plan of record does not.
        /// </param>
        public static ProjectDigest? ParseDigestResponse(string response, ProjectDigest existing, bool preserveNextSteps = false)
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
                return SanitizeDigest(result);
            }
            if (sections.TryGetValue(PlanHeader, out var plan)) ApplyPlanSection(result, plan, preserveNextSteps);
            if (sections.TryGetValue(OrgHeader, out var org)) result.OrgChart = org;
            if (sections.TryGetValue(BudgetHeader, out var budget)) result.BudgetState = budget;
            if (sections.TryGetValue(OpenHeader, out var open)) result.OpenThreads = open;
            if (sections.TryGetValue(SummaryHeader, out var summary)) result.RollingSummary = summary;
            return SanitizeDigest(result);
        }

        private static ProjectDigest SanitizeDigest(ProjectDigest result)
        {
            result.CurrentPlan = ProjectPromptHygiene.ScrubState(result.CurrentPlan, "");
            result.CurrentFocus = ProjectPromptHygiene.ScrubState(result.CurrentFocus, "");
            result.NextSteps = result.NextSteps
                .Where(step => !ProjectPromptHygiene.ContainsContextBookkeeping(step))
                .ToList();
            result.OrgChart = ProjectPromptHygiene.ScrubState(result.OrgChart, "");
            result.BudgetState = ProjectPromptHygiene.ScrubState(result.BudgetState, "");
            result.OpenThreads = ProjectPromptHygiene.ScrubState(result.OpenThreads, "");
            result.RollingSummary = ProjectPromptHygiene.ScrubState(result.RollingSummary, "");
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
        private static void ApplyPlanSection(ProjectDigest result, string plan, bool preserveNextSteps = false)
        {
            result.CurrentPlan = plan.Trim();
            // The step ledger is authoritative when it holds open steps; the utility model's paraphrase of
            // the plan is narrative only and must not replace it.
            if (preserveNextSteps) return;
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
