using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics.Modules
{
    /// <summary>Lexical richness (TTR), avg msg length, top tokens (minus stopwords), top phrases (bigrams/trigrams).</summary>
    public class VocabularyModule : IPersonAnalyticModule
    {
        public string Name => "vocabulary";
        public int Version => 3;

        private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
        {
            "the","a","an","and","or","but","is","it","of","to","in","on","at","for","with","this","that","i","you","my","your",
            "im","ive","ill","its","be","was","are","were","do","does","did","have","has","had","not","no","yes","so","just","like",
            "if","then","than","also","really","very","get","got","go","gonna","wanna","u","ur","r","lol","lmao","yeah","ok",
            "we","they","them","he","she","his","her","me","us","our","their","what","when","where","who","why","how","there","here","one","two","some","any","all","can","will","would","could","should"
        };
        private static readonly Regex Tokeniser = new(@"[a-zA-Z']{2,}", RegexOptions.Compiled);

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var msgs = AnalyticHelpers.LoadMessages(conn, personId);
            return Task.FromResult(AnalyticSplits.Apply(msgs, ComputeFromMessages,
                AnalyticSplits.CompactWithArrays(8, "top_tokens")));
        }

        internal static JObject ComputeFromMessages(List<AnalyticMessage> msgs)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var bigrams = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var trigrams = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            long totalTokens = 0, totalChars = 0;
            int nonEmpty = 0;
            foreach (var m in msgs)
            {
                if (string.IsNullOrWhiteSpace(m.Content)) continue;
                nonEmpty++;
                totalChars += m.Content.Length;

                // Tokenise the message into a per-message word list to derive n-grams that
                // never cross message boundaries (which would produce nonsense phrases).
                var perMsg = new List<string>();
                foreach (Match mt in Tokeniser.Matches(m.Content))
                {
                    string w = mt.Value.ToLowerInvariant();
                    perMsg.Add(w);
                    totalTokens++;
                    if (Stop.Contains(w)) continue;
                    counts.TryGetValue(w, out int c);
                    counts[w] = c + 1;
                }
                // bigrams: drop if either token is a stopword *or* both are very short
                for (int i = 0; i + 1 < perMsg.Count; i++)
                {
                    string a = perMsg[i], b = perMsg[i + 1];
                    if (Stop.Contains(a) || Stop.Contains(b)) continue;
                    if (a.Length < 3 && b.Length < 3) continue;
                    string key = a + " " + b;
                    bigrams.TryGetValue(key, out int bc); bigrams[key] = bc + 1;
                }
                // trigrams: middle word may be a stopword (e.g. "out of pocket")
                for (int i = 0; i + 2 < perMsg.Count; i++)
                {
                    string a = perMsg[i], b = perMsg[i + 1], c = perMsg[i + 2];
                    if (Stop.Contains(a) || Stop.Contains(c)) continue;
                    string key = a + " " + b + " " + c;
                    trigrams.TryGetValue(key, out int tc); trigrams[key] = tc + 1;
                }
            }

            double ttr = totalTokens > 0 ? (double)counts.Count / totalTokens : 0;
            var top = counts.OrderByDescending(kv => kv.Value).Take(40).Select(kv => new JObject(
                new JProperty("token", kv.Key),
                new JProperty("count", kv.Value)));
            var topBi = bigrams.Where(kv => kv.Value >= 2)
                               .OrderByDescending(kv => kv.Value).Take(20)
                               .Select(kv => new JObject(
                                   new JProperty("phrase", kv.Key),
                                   new JProperty("count", kv.Value),
                                   new JProperty("size", 2)));
            var topTri = trigrams.Where(kv => kv.Value >= 2)
                                 .OrderByDescending(kv => kv.Value).Take(15)
                                 .Select(kv => new JObject(
                                     new JProperty("phrase", kv.Key),
                                     new JProperty("count", kv.Value),
                                     new JProperty("size", 3)));

            return new JObject(
                new JProperty("messages_analysed", nonEmpty),
                new JProperty("total_tokens", totalTokens),
                new JProperty("unique_tokens", counts.Count),
                new JProperty("type_token_ratio", ttr),
                new JProperty("avg_message_chars", nonEmpty == 0 ? 0 : (double)totalChars / nonEmpty),
                new JProperty("avg_message_tokens", nonEmpty == 0 ? 0 : (double)totalTokens / nonEmpty),
                new JProperty("top_tokens", new JArray(top)),
                new JProperty("top_phrases", new JArray(topBi.Concat(topTri)))
            );
        }
    }
}
