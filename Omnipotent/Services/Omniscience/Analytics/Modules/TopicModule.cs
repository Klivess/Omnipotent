using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics.Modules
{
    /// <summary>Cheap topic surface: bigrams + URL-domain frequency.</summary>
    public class TopicModule : IPersonAnalyticModule
    {
        public string Name => "topics";
        public int Version => 1;

        private static readonly Regex Tok = new(@"[a-zA-Z]{3,}", RegexOptions.Compiled);
        private static readonly Regex Url = new(@"https?://([\w.-]+)", RegexOptions.Compiled);

        private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
        {
            "the","and","but","you","your","yes","not","with","this","that","they","them","what","when","where","who",
            "are","was","were","have","has","had","its","ive","ill","like","just","really","very","get","got","one","all",
            "for","from","about","into","out","over","more","than","then","now","here","there","also","some","any","none"
        };

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var msgs = AnalyticHelpers.LoadMessages(conn, personId);
            var bigrams = new Dictionary<string, int>();
            var domains = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var m in msgs)
            {
                if (string.IsNullOrEmpty(m.Content)) continue;
                foreach (Match u in Url.Matches(m.Content))
                {
                    string d = u.Groups[1].Value.ToLowerInvariant();
                    domains.TryGetValue(d, out int c); domains[d] = c + 1;
                }
                var toks = Tok.Matches(m.Content).Select(x => x.Value.ToLowerInvariant())
                    .Where(t => !Stop.Contains(t)).ToList();
                for (int i = 0; i + 1 < toks.Count; i++)
                {
                    string bg = toks[i] + " " + toks[i + 1];
                    bigrams.TryGetValue(bg, out int c); bigrams[bg] = c + 1;
                }
            }

            var topBg = bigrams.Where(kv => kv.Value >= 2).OrderByDescending(kv => kv.Value).Take(30)
                .Select(kv => new JObject(new JProperty("bigram", kv.Key), new JProperty("count", kv.Value)));
            var topDom = domains.OrderByDescending(kv => kv.Value).Take(20)
                .Select(kv => new JObject(new JProperty("domain", kv.Key), new JProperty("count", kv.Value)));

            return Task.FromResult(new JObject(
                new JProperty("top_bigrams", new JArray(topBg)),
                new JProperty("top_domains", new JArray(topDom))
            ));
        }
    }
}
