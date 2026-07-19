using System.Text.RegularExpressions;

namespace Omnipotent.Services.Projects
{
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

        /// <summary>Chars-per-token ratio used for estimation.</summary>
        private const double CharsPerToken = 4.0;

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
