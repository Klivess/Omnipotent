using Newtonsoft.Json.Linq;
using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics.Modules
{
    /// <summary>Counts laugh-tokens, exclamation density, ironic punctuation.</summary>
    public class HumorModule : IPersonAnalyticModule
    {
        public string Name => "humor";
        public int Version => 1;

        private static readonly Regex Laugh = new(@"\b(lol+|lmao+|rofl|haha+|hehe+|kek)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var msgs = AnalyticHelpers.LoadMessages(conn, personId);
            int total = 0, laughs = 0, multiExcl = 0, ironicQuotes = 0;
            int laughTokenTotal = 0;
            foreach (var m in msgs)
            {
                if (string.IsNullOrEmpty(m.Content)) continue;
                total++;
                int hits = Laugh.Matches(m.Content).Count;
                if (hits > 0) { laughs++; laughTokenTotal += hits; }
                if (m.Content.Contains("!!")) multiExcl++;
                // air-quotes used ironically: "word"
                if (Regex.IsMatch(m.Content, "\"[^\" ]{2,}\"")) ironicQuotes++;
            }
            return Task.FromResult(new JObject(
                new JProperty("messages_analysed", total),
                new JProperty("messages_with_laughter", laughs),
                new JProperty("laughter_token_total", laughTokenTotal),
                new JProperty("multi_exclamation_messages", multiExcl),
                new JProperty("ironic_quote_messages", ironicQuotes),
                new JProperty("humor_score", total == 0 ? 0 :
                    (laughs + 0.5 * multiExcl + 0.5 * ironicQuotes) / (double)total)
            ));
        }
    }
}
