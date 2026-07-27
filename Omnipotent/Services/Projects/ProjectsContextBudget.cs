using System.Text.RegularExpressions;

namespace Omnipotent.Services.Projects
{
    /// <summary>Derived context policy for one Projects request route set.</summary>
    public sealed record ProjectContextWindowPolicy(
        int ContextWindowTokens,
        int MaxOutputTokens,
        int CompactionTriggerTokens,
        int WorkSliceTokenBudget,
        bool CatalogComplete);

    /// <summary>
    /// Token budget management for Commander/sub-agent wake prompts, cloned from
    /// KliveAgentContextBudget's approach (design doc §7 names it explicitly) with its own
    /// bucket constants — deliberately a separate file so Projects and KliveAgent can tune
    /// independently. Everything budgeted here is regenerable: the full event log is lossless
    /// on disk and an agent can always retrieve more via search.
    /// </summary>
    public static class ProjectsContextBudget
    {
        private static readonly HashSet<string> QueryStopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "in", "is",
            "it", "of", "on", "or", "that", "the", "this", "to", "was", "were", "with"
        };
        /// <summary>The standing digest (goal, plan, org chart, budget, open threads).</summary>
        public const int DigestBudget = 6000;

        /// <summary>
        /// Klives' durable project directives. This is separate from the digest budget because
        /// rules are authoritative constraints, not expendable model-authored narrative.
        /// </summary>
        public const int DirectivesBudget = 3000;

        /// <summary>Recent events replayed verbatim into a wake seed.</summary>
        public const int RecentEventsBudget = 48000;

        /// <summary>BM25 retrieval hits pulled from the deep log for the triggering stimulus.</summary>
        public const int RetrievalBudget = 8000;

        /// <summary>Cross-system knowledge (KliveRAG) injected into a Commander wake — other projects,
        /// KliveAgent memory, Omniscience facts, repo docs. Agents can search_knowledge for more.</summary>
        public const int KnowledgeBudget = 4000;

        /// <summary>Thinner knowledge budget for sub-agent wakes (their seed is deliberately lean).</summary>
        public const int SubAgentKnowledgeBudget = 2000;

        /// <summary>Current observable values (the live dashboard agents maintain for Klives).</summary>
        public const int ObservablesBudget = 1000;

        /// <summary>The approved Grand Plan summary (the standing north star) rendered into each wake seed.</summary>
        public const int GrandPlanBudget = 4000;

        /// <summary>Known accounts from the global shared registry (reuse before creating duplicates).</summary>
        public const int AccountsBudget = 1000;

        /// <summary>
        /// Shared project-file summary (important inputs/assets, recent changes and a compact tree).
        /// The full filesystem remains available through list_files/stat_file and at /project.
        /// </summary>
        public const int SharedFilesBudget = 4000;

        /// <summary>The triggering stimulus payload + verdict itself.</summary>
        public const int StimulusBudget = 4000;

        /// <summary>Per-tool-result truncation inside a wake.</summary>
        public const int ToolResultBudget = 2400;

        /// <summary>
        /// Verbatim excerpt of how the PREVIOUS wake ended, replayed at the top of the next one. Each wake
        /// starts a fresh session, so this is the only fine-grained carry-over; everything else is
        /// rehydrated from disk by the seed. Kept small deliberately — it is re-sent on every turn.
        /// </summary>
        public const int WakeTailBudget = 8000;

        /// <summary>How many trailing action→result pairs the wake tail records.</summary>
        public const int WakeTailActions = 4;

        /// <summary>
        /// New events fed into the post-wake digest rebuild. The rebuild runs after every wake and its
        /// output is capped at ~570 words, so there is no value in shipping the entire event backlog to
        /// the utility model — the rolling summary already carries older ground.
        /// </summary>
        public const int DigestRebuildEventsBudget = 24000;

        /// <summary>Chars-per-token ratio used for estimation.</summary>
        private const double CharsPerToken = 4.0;

        /// <summary>
        /// Token cost of one inline screenshot. Providers bill images by area after resizing the long
        /// edge down to ~1568px, at roughly 750 pixels per token. A 1920x1080 frame therefore costs
        /// ~1.6k tokens, not the flat 1200 the wake loop used to assume — an under-count that let a
        /// vision-heavy wake sail past its context ceiling.
        /// </summary>
        public static long EstimateImageTokens(int width, int height)
        {
            if (width <= 0 || height <= 0) return 1600;
            double longest = Math.Max(width, height);
            double scale = longest > 1568 ? 1568.0 / longest : 1.0;
            double pixels = (width * scale) * (height * scale);
            return Math.Clamp((long)Math.Ceiling(pixels / 750.0), 256, 4096);
        }

        /// <summary>
        /// Builds the local context policy from OpenRouter's live route metadata. Ten percent of the
        /// model window is reserved for tokenizer/envelope variance, then the requested completion is
        /// reserved before calculating the message compaction point. Work slices roll at 80% of the
        /// real window so a completed tool batch has room to be committed before a fresh context starts.
        /// </summary>
        public static ProjectContextWindowPolicy CreateContextWindowPolicy(
            OpenRouterRouteContextLimits limits,
            int requestedMaxOutputTokens,
            int configuredWorkSliceTokenBudget = int.MaxValue)
        {
            ArgumentNullException.ThrowIfNull(limits);
            int contextWindow = Math.Max(2_048, limits.ContextWindowTokens);
            int safeTotal = Math.Max(1_024, (int)Math.Floor(contextWindow * 0.90));
            int routeOutputCap = Math.Max(1, limits.MaxCompletionTokens);
            int maxOutput = Math.Clamp(
                Math.Min(requestedMaxOutputTokens, routeOutputCap),
                1,
                Math.Max(1, safeTotal / 2));
            int promptCapacity = Math.Max(256, safeTotal - maxOutput);
            int compactionTrigger = Math.Max(256, (int)Math.Floor(promptCapacity * 0.85));
            int rollover = Math.Max(1, (int)Math.Floor(contextWindow * 0.80));
            if (configuredWorkSliceTokenBudget > 0)
                rollover = Math.Min(rollover, configuredWorkSliceTokenBudget);

            return new ProjectContextWindowPolicy(
                contextWindow,
                maxOutput,
                compactionTrigger,
                rollover,
                limits.AllRoutesResolved);
        }

        /// <summary>Estimates the token count for the given text using the ~4 chars/token heuristic.</summary>
        public static int EstimateTokens(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return (int)Math.Ceiling(text.Length / CharsPerToken);
        }

        /// <summary>Truncates text to fit within <paramref name="maxTokens"/> tokens, appending a truncation marker.</summary>
        public static string TruncateToTokens(string text, int maxTokens)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var maxChars = (int)(maxTokens * CharsPerToken);
            if (text.Length <= maxChars) return text;
            const string marker = "\n[...truncated]";
            return text[..Math.Max(0, maxChars - marker.Length)] + marker;
        }

        /// <summary>
        /// Greedily selects as many items as possible while staying within <paramref name="maxTokens"/>,
        /// ordered by descending <paramref name="scoreSelector"/>. Returns the selected subset in score order.
        /// </summary>
        public static List<T> FitItemsInBudget<T>(
            IEnumerable<T> items,
            int maxTokens,
            Func<T, string> textSelector,
            Func<T, double> scoreSelector)
        {
            var sorted = items.OrderByDescending(scoreSelector).ToList();
            var selected = new List<T>();
            var used = 0;

            foreach (var item in sorted)
            {
                var text = textSelector(item);
                var cost = EstimateTokens(text);
                if (used + cost > maxTokens) continue;
                selected.Add(item);
                used += cost;
            }

            return selected;
        }

        /// <summary>
        /// Scores an event for budget selection: recency-weighted with keyword overlap against
        /// the triggering stimulus. Higher = more relevant.
        /// </summary>
        public static double ScoreEvent(string eventText, string queryText, int indexFromEnd)
        {
            double recencyScore = Math.Max(0.1, 1.0 / (1.0 + Math.Max(0, indexFromEnd)));

            double keywordScore = 0;
            if (!string.IsNullOrWhiteSpace(queryText) && !string.IsNullOrWhiteSpace(eventText))
            {
                var queryTerms = Regex.Matches(queryText, @"[\p{L}\p{N}]+")
                    .Select(m => m.Value)
                    .Where(t => t.Length >= 2 && !QueryStopWords.Contains(t))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var eventTerms = Regex.Matches(eventText, @"[\p{L}\p{N}]+")
                    .Select(m => m.Value)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var matchCount = queryTerms.Count(eventTerms.Contains);

                keywordScore = queryTerms.Count > 0
                    ? (double)matchCount / queryTerms.Count
                    : 0;
            }

            return recencyScore * 0.6 + keywordScore * 0.4;
        }

        // ── Harness-leak guard (TEMP diagnostic, Jul 2026) ─────────────────────────────────────
        // Distinctive phrases that only appear in Claude Code / Agent-SDK harness scaffolding, never
        // in legitimate project context. A seed fragment carrying one is a captured coding-agent
        // transcript that leaked into a persistent store (KliveRAG, a /project file, the event log);
        // re-seeding it makes the wake model parrot it back, so such fragments are dropped here.
        private static readonly string[] HarnessLeakMarkers =
        {
            "deferred tools are now available via ToolSearch",
            "will fail with InputValidationError",
            "Available agent types for the Agent tool",
            "available for use with the Skill tool",
            "require authentication before their tools can be used",
        };

        /// <summary>True when the text carries Claude-Code/Agent-SDK harness scaffolding that must not
        /// be re-seeded into a wake prompt.</summary>
        public static bool LooksLikeHarnessLeak(string? text) =>
            !string.IsNullOrEmpty(text) && HarnessLeakMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));

        /// <summary>Returns <paramref name="text"/> unchanged, or <paramref name="ifContaminated"/> when
        /// it carries harness scaffolding (see <see cref="LooksLikeHarnessLeak"/>).</summary>
        public static string ScrubHarnessLeak(string? text, string ifContaminated) =>
            LooksLikeHarnessLeak(text) ? ifContaminated : (text ?? string.Empty);
    }
}
