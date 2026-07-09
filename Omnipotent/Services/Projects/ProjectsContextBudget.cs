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
        /// <summary>The standing digest (goal, plan, org chart, budget, open threads).</summary>
        public const int DigestBudget = 1000;

        /// <summary>Recent events replayed verbatim into a wake seed.</summary>
        public const int RecentEventsBudget = 3000;

        /// <summary>BM25 retrieval hits pulled from the deep log for the triggering stimulus.</summary>
        public const int RetrievalBudget = 1500;

        /// <summary>Cross-system knowledge (KliveRAG) injected into a Commander wake — other projects,
        /// KliveAgent memory, Omniscience facts, repo docs. Agents can search_knowledge for more.</summary>
        public const int KnowledgeBudget = 900;

        /// <summary>Thinner knowledge budget for sub-agent wakes (their seed is deliberately lean).</summary>
        public const int SubAgentKnowledgeBudget = 400;

        /// <summary>The triggering stimulus payload + verdict itself.</summary>
        public const int StimulusBudget = 800;

        /// <summary>Per-tool-result truncation inside a wake.</summary>
        public const int ToolResultBudget = 800;

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
            double recencyScore = 1.0 / (1.0 + indexFromEnd);

            double keywordScore = 0;
            if (!string.IsNullOrWhiteSpace(queryText) && !string.IsNullOrWhiteSpace(eventText))
            {
                var queryTerms = queryText.ToLowerInvariant()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(t => t.Length > 3)
                    .ToHashSet();

                var matchCount = queryTerms.Count(term =>
                    eventText.Contains(term, StringComparison.OrdinalIgnoreCase));

                keywordScore = queryTerms.Count > 0
                    ? (double)matchCount / queryTerms.Count
                    : 0;
            }

            return recencyScore * 0.6 + keywordScore * 0.4;
        }
    }
}
