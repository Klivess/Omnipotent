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
        //
        // These are *compression* budgets, not capability caps. They only apply to content
        // that is regenerable on demand (the agent can call GetRepoMap, RecallMemories,
        // or re-run a script with different parameters to fetch more). They do NOT cap how
        // many iterations the agent may take or how complex a task may be.

        /// <summary>Soft cap on the repo-map slice injected into the system prompt.
        /// Agent can always call GetRepoMap(maxTokens: ...) inside a script for more.</summary>
        public const int RepoMapBudget = 800;

        /// <summary>Soft cap on memories surfaced into the system prompt up-front.
        /// Agent can always call RecallMemories(query) for deeper retrieval.</summary>
        public const int MemoryBudget = 600;

        /// <summary>Soft cap on conversation history selected into the per-turn prompt.
        /// Higher = more conversational persistence at the cost of input tokens.</summary>
        public const int HistoryBudget = 4000;

        /// <summary>Per-script output truncation. Output is regenerable: if a single script
        /// dumps more than this, the agent should narrow its query, not be given the full blob.</summary>
        public const int ScriptOutputBudget = 800;

        /// <summary>Per-turn cap on the replayed scripts+outputs injected under a past agent turn in
        /// the conversation history, so the agent can see what code it previously ran and what it
        /// returned. Only the most recent agent turns carry this (see HistoryScriptRecentTurns).</summary>
        public const int HistoryScriptBudget = 400;

        /// <summary>Budget for the compacted synopsis of turns that fall before the retained history
        /// window. Instead of silently dropping old turns, we summarise them into this many tokens so
        /// the agent keeps a thread of the earlier conversation.</summary>
        public const int HistorySummaryBudget = 500;

        /// <summary>Legacy constant. No longer applied as a hard truncation — kept for API
        /// stability in case external callers reference it.</summary>
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
