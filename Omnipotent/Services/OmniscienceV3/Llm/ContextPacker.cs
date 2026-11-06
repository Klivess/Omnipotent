using System.Text;
using Omnipotent.Services.KliveLLM;

namespace Omnipotent.Services.OmniscienceV3.Llm
{
    /// <summary>How a section is cut when the window is full.</summary>
    public enum PackStrategy
    {
        /// <summary>Everything or nothing; the pack fails if it cannot fit.</summary>
        Required,
        /// <summary>Keep the most recent items (items are given oldest first).</summary>
        KeepNewest,
        /// <summary>Keep the earliest items.</summary>
        KeepOldest,
    }

    /// <summary>One prompt section. Sections are emitted in the order given (stable prefix first); priority
    /// only decides who gets the budget.</summary>
    public sealed record PackSection(string Name, int Priority, IReadOnlyList<string> Items, PackStrategy Strategy, string? Header = null, string Separator = "\n");

    public sealed record SectionCoverage(int Included, int Total, int Tokens);

    /// <summary>Packed prompt text plus exactly what was left out (reported as coverage, never hidden).</summary>
    public sealed record PackResult(string Text, int EstimatedTokens, long EstimatedBytes, IReadOnlyDictionary<string, SectionCoverage> Coverage, bool RequiredDidNotFit);

    /// <summary>
    /// Fills a token budget (the model's real window minus output, margin and overhead) with prioritised
    /// sections, and a byte budget under the provider's request-body ceiling. Higher-priority sections get
    /// first claim; within a section the strategy decides which items survive. Output order is the given
    /// section order so a shared stable prefix stays byte-identical across calls.
    /// </summary>
    public static class ContextPacker
    {
        public const long DefaultByteBudget = 3_500_000;

        /// <summary>Prompt token budget for a model: window − reserved output − margin − fixed overhead.</summary>
        public static int Budget(ModelContextInfo info, int reservedOutputTokens, double margin = 0.08, int overheadTokens = 512)
        {
            int window = info.ContextWindowTokens;
            return Math.Max(256, window - Math.Min(reservedOutputTokens, info.MaxOutputTokens) - (int)(window * margin) - overheadTokens);
        }

        public static long ByteBudget(ModelContextInfo info) =>
            info.RequestByteLimit is long limit && limit > 0 ? (long)(Math.Min(limit, DefaultByteBudget) * 0.9) : DefaultByteBudget;

        public static PackResult Pack(IReadOnlyList<PackSection> sections, int tokenBudget, TokenEstimator estimator, long byteBudget = DefaultByteBudget)
        {
            var chosen = sections.ToDictionary(s => s.Name, _ => new List<int>(), StringComparer.Ordinal);
            int usedTokens = 0;
            long usedBytes = 0;
            bool requiredMissing = false;

            foreach (var s in sections.OrderByDescending(s => s.Priority))
            {
                int headerTokens = s.Header == null ? 0 : estimator.Estimate(s.Header);
                long headerBytes = s.Header == null ? 0 : Encoding.UTF8.GetByteCount(s.Header) + 1;
                var costs = s.Items.Select(i => (Tokens: estimator.Estimate(i), Bytes: (long)Encoding.UTF8.GetByteCount(i) + 1)).ToList();

                if (s.Strategy == PackStrategy.Required)
                {
                    int t = headerTokens + costs.Sum(c => c.Tokens);
                    long b = headerBytes + costs.Sum(c => c.Bytes);
                    if (usedTokens + t > tokenBudget || usedBytes + b > byteBudget) { requiredMissing = true; continue; }
                    usedTokens += t;
                    usedBytes += b;
                    chosen[s.Name].AddRange(Enumerable.Range(0, s.Items.Count));
                    continue;
                }

                if (s.Items.Count == 0) continue;
                if (usedTokens + headerTokens > tokenBudget || usedBytes + headerBytes > byteBudget) continue;
                int sectionTokens = headerTokens;
                long sectionBytes = headerBytes;
                var order = s.Strategy == PackStrategy.KeepNewest
                    ? Enumerable.Range(0, s.Items.Count).Reverse()
                    : Enumerable.Range(0, s.Items.Count);
                foreach (int i in order)
                {
                    if (usedTokens + sectionTokens + costs[i].Tokens > tokenBudget) break;
                    if (usedBytes + sectionBytes + costs[i].Bytes > byteBudget) break;
                    sectionTokens += costs[i].Tokens;
                    sectionBytes += costs[i].Bytes;
                    chosen[s.Name].Add(i);
                }
                if (chosen[s.Name].Count == 0) continue;
                usedTokens += sectionTokens;
                usedBytes += sectionBytes;
            }

            var sb = new StringBuilder();
            var coverage = new Dictionary<string, SectionCoverage>(StringComparer.Ordinal);
            foreach (var s in sections)
            {
                var idx = chosen[s.Name];
                idx.Sort(); // original (chronological) order within the section
                int tokens = 0;
                if (idx.Count > 0)
                {
                    if (s.Header != null) { sb.Append(s.Header).Append('\n'); tokens += estimator.Estimate(s.Header); }
                    for (int k = 0; k < idx.Count; k++)
                    {
                        if (k > 0) sb.Append(s.Separator);
                        sb.Append(s.Items[idx[k]]);
                        tokens += estimator.Estimate(s.Items[idx[k]]);
                    }
                    sb.Append("\n\n");
                }
                coverage[s.Name] = new SectionCoverage(idx.Count, s.Items.Count, tokens);
            }
            string text = sb.ToString().TrimEnd();
            return new PackResult(text, usedTokens, Encoding.UTF8.GetByteCount(text), coverage, requiredMissing);
        }
    }
}
