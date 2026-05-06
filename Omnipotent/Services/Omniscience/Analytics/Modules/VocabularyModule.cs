using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics.Modules
{
    /// <summary>Lexical richness (TTR), avg msg length, top tokens (minus stopwords).</summary>
    public class VocabularyModule : IPersonAnalyticModule
    {
        public string Name => "vocabulary";
        public int Version => 1;

        private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
        {
            "the","a","an","and","or","but","is","it","of","to","in","on","at","for","with","this","that","i","you","my","your",
            "im","ive","ill","its","be","was","are","were","do","does","did","have","has","had","not","no","yes","so","just","like",
            "if","then","than","also","really","very","very","get","got","go","gonna","wanna","u","ur","r","lol","lmao","yeah","ok"
        };
        private static readonly Regex Tokeniser = new(@"[a-zA-Z']{2,}", RegexOptions.Compiled);

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var msgs = AnalyticHelpers.LoadMessages(conn, personId);
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            long totalTokens = 0, totalChars = 0;
            int nonEmpty = 0;
            foreach (var m in msgs)
            {
                if (string.IsNullOrWhiteSpace(m.Content)) continue;
                nonEmpty++;
                totalChars += m.Content.Length;
                foreach (Match mt in Tokeniser.Matches(m.Content))
                {
                    string w = mt.Value.ToLowerInvariant();
                    totalTokens++;
                    if (Stop.Contains(w)) continue;
                    counts.TryGetValue(w, out int c);
                    counts[w] = c + 1;
                }
            }

            double ttr = totalTokens > 0 ? (double)counts.Count / totalTokens : 0;
            var top = counts.OrderByDescending(kv => kv.Value).Take(40).Select(kv => new JObject(
                new JProperty("token", kv.Key),
                new JProperty("count", kv.Value)));

            var payload = new JObject(
                new JProperty("messages_analysed", nonEmpty),
                new JProperty("total_tokens", totalTokens),
                new JProperty("unique_tokens", counts.Count),
                new JProperty("type_token_ratio", ttr),
                new JProperty("avg_message_chars", nonEmpty == 0 ? 0 : (double)totalChars / nonEmpty),
                new JProperty("avg_message_tokens", nonEmpty == 0 ? 0 : (double)totalTokens / nonEmpty),
                new JProperty("top_tokens", new JArray(top))
            );
            return Task.FromResult(payload);
        }
    }
}
