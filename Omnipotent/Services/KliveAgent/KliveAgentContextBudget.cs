namespace Omnipotent.Services.KliveAgent
{
    /// <summary>
    /// Token budget management for the KliveAgent context window.
    /// Estimates tokens via the standard ~4 chars/token heuristic and provides
    /// utilities for fitting multiple items within a budget.
    ///
    /// Spec reference: Chapter 10 — Context Window Management & Token Budgeting
    /// </summary>
    public class KliveAgentContextBudget
    {
        // ── Budget constants ──

        /// <summary>Max tokens reserved for the repo map in every system prompt.</summary>
        public const int RepoMapBudget = 2000;

        /// <summary>Max tokens reserved for memory entries in every system prompt.</summary>
        public const int MemoryBudget = 1000;

        /// <summary>Max tokens reserved for conversation history selection.</summary>
        public const int HistoryBudget = 3000;

        /// <summary>Max tokens to surface from a single script execution output before truncating.</summary>
        public const int ScriptOutputBudget = 2000;

        /// <summary>Hard cap on the total system prompt (personality + repo map + memories + tools).</summary>
        public const int TotalSystemPromptBudget = 8000;

        /// <summary>Chars-per-token ratio used for estimation.</summary>
        private const double CharsPerToken = 4.0;

        // ── Estimation ──

        /// <summary>Estimates the token count for the given text using the 4 chars/token heuristic.</summary>
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
        /// Scores a conversation message pair (user + agent) for budget selection.
        /// Higher score = more relevant and recent.
        /// </summary>
        public static double ScoreMessage(string messageContent, string queryText, int indexFromEnd)
        {
            // Recency weight: more recent messages score higher
            double recencyScore = 1.0 / (1.0 + indexFromEnd);

            // Keyword overlap score
            double keywordScore = 0;
            if (!string.IsNullOrWhiteSpace(queryText) && !string.IsNullOrWhiteSpace(messageContent))
            {
                var queryTerms = queryText.ToLowerInvariant()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(t => t.Length > 3)
                    .ToHashSet();

                var matchCount = queryTerms.Count(term =>
                    messageContent.Contains(term, StringComparison.OrdinalIgnoreCase));

                keywordScore = queryTerms.Count > 0
                    ? (double)matchCount / queryTerms.Count
                    : 0;
            }

            return recencyScore * 0.6 + keywordScore * 0.4;
        }
    }
}
