using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics.Modules
{
    /// <summary>
    /// Quantifies how differently a person behaves per context (DM vs group DM vs each
    /// server): per-facet behaviour vectors, a code-switching (divergence) score, and the
    /// "most themselves" facet. Cross-facet divergence is a personality finding in its own
    /// right — high divergence = strong code-switcher / performative public persona.
    /// </summary>
    public class FacetDivergenceModule : IPersonAnalyticModule
    {
        public string Name => "facet_divergence";
        public int Version => 1;

        private static readonly Regex Laugh = new(@"\b(?:lo+l+|lmao+|rofl|(?:ha){2,}|(?:he){2,}|ke+k+|x+d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Word = new(@"[a-zA-Z]+", RegexOptions.Compiled);

        private class FacetVector
        {
            public int Messages;
            public double AvgChars;
            public double ProfanityRate;
            public double EmojiRate;
            public double LaughterRate;
            public double QuestionRate;
            public double LowercaseStartRate;
            public double AvgSentiment;

            public double[] AsArray() => new[] { AvgChars / 100.0, ProfanityRate, EmojiRate, LaughterRate, QuestionRate, LowercaseStartRate, (AvgSentiment + 2) / 4.0 };
        }

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var msgs = AnalyticHelpers.LoadMessages(conn, personId);

            var vectors = new Dictionary<string, FacetVector>();
            foreach (var group in msgs.GroupBy(AnalyticSplits.FacetKey)
                                      .Where(g => g.Count(m => !string.IsNullOrWhiteSpace(m.Content)) >= AnalyticSplits.MinFacetMessages)
                                      .OrderByDescending(g => g.Count())
                                      .Take(AnalyticSplits.MaxFacets))
            {
                vectors[group.Key] = ComputeVector(group.Where(m => !string.IsNullOrWhiteSpace(m.Content)).ToList());
            }

            // Divergence: mean pairwise distance between facet behaviour vectors.
            var keys = vectors.Keys.ToList();
            double divergence = 0; int pairs = 0;
            for (int i = 0; i < keys.Count; i++)
                for (int j = i + 1; j < keys.Count; j++)
                {
                    var a = vectors[keys[i]].AsArray();
                    var b = vectors[keys[j]].AsArray();
                    double d = Math.Sqrt(a.Zip(b, (x, y) => (x - y) * (x - y)).Sum() / a.Length);
                    divergence += d; pairs++;
                }
            divergence = pairs == 0 ? 0 : divergence / pairs;

            // "Most themselves" = the least guarded register: profanity + laughter + emoji
            // + lowercase starts, weighted. "Most performative" = the inverse.
            string? mostRelaxed = null, mostFormal = null;
            double bestRelax = double.MinValue, worstRelax = double.MaxValue;
            foreach (var kv in vectors)
            {
                double relax = kv.Value.ProfanityRate * 2 + kv.Value.LaughterRate + kv.Value.EmojiRate + kv.Value.LowercaseStartRate;
                if (relax > bestRelax) { bestRelax = relax; mostRelaxed = kv.Key; }
                if (relax < worstRelax) { worstRelax = relax; mostFormal = kv.Key; }
            }

            var facetJson = new JObject();
            foreach (var kv in vectors)
            {
                facetJson[kv.Key] = new JObject(
                    new JProperty("messages", kv.Value.Messages),
                    new JProperty("avg_message_chars", kv.Value.AvgChars),
                    new JProperty("profanity_rate", kv.Value.ProfanityRate),
                    new JProperty("emoji_rate", kv.Value.EmojiRate),
                    new JProperty("laughter_rate", kv.Value.LaughterRate),
                    new JProperty("question_rate", kv.Value.QuestionRate),
                    new JProperty("lowercase_start_rate", kv.Value.LowercaseStartRate),
                    new JProperty("avg_sentiment", kv.Value.AvgSentiment));
            }

            return Task.FromResult(new JObject(
                new JProperty("facets_compared", vectors.Count),
                new JProperty("divergence_score", divergence),
                new JProperty("code_switcher", divergence > 0.18),
                new JProperty("most_relaxed_facet", mostRelaxed),
                new JProperty("most_formal_facet", mostFormal),
                new JProperty("facet_vectors", facetJson)
            ));
        }

        private static FacetVector ComputeVector(List<AnalyticMessage> msgs)
        {
            long chars = 0;
            int profane = 0, withEmoji = 0, laughter = 0, questions = 0, lowercaseStart = 0;
            double sentTotal = 0; int sentScored = 0;
            foreach (var m in msgs)
            {
                string c = m.Content;
                chars += c.Length;
                if (c.Contains('?')) questions++;
                if (Laugh.IsMatch(c)) laughter++;
                if (char.IsLetter(c[0]) && char.IsLower(c[0])) lowercaseStart++;

                bool hasEmoji = c.EnumerateRunes().Any(r => (r.Value >= 0x1F300 && r.Value <= 0x1FAFF) || (r.Value >= 0x2600 && r.Value <= 0x27BF));
                if (hasEmoji || c.Contains("<:") || c.Contains("<a:")) withEmoji++;

                bool isProfane = false;
                double s = 0; int hits = 0;
                foreach (Match w in Word.Matches(c))
                {
                    if (ProfanityModule.Severity.ContainsKey(w.Value)) isProfane = true;
                    if (SentimentModule.Lex.TryGetValue(w.Value, out double v)) { s += v; hits++; }
                }
                if (isProfane) profane++;
                if (hits > 0) { sentTotal += s / hits; sentScored++; }
            }
            int n = msgs.Count;
            return new FacetVector
            {
                Messages = n,
                AvgChars = n == 0 ? 0 : (double)chars / n,
                ProfanityRate = n == 0 ? 0 : (double)profane / n,
                EmojiRate = n == 0 ? 0 : (double)withEmoji / n,
                LaughterRate = n == 0 ? 0 : (double)laughter / n,
                QuestionRate = n == 0 ? 0 : (double)questions / n,
                LowercaseStartRate = n == 0 ? 0 : (double)lowercaseStart / n,
                AvgSentiment = sentScored == 0 ? 0 : sentTotal / sentScored,
            };
        }
    }
}
