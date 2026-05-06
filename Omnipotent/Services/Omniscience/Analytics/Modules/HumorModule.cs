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
    /// Counts laugh-tokens, exclamation density, ironic punctuation, and surfaces
    /// per-family distribution + sample messages so the dossier shows specifics
    /// rather than a single opaque score.
    /// </summary>
    public class HumorModule : IPersonAnalyticModule
    {
        public string Name => "humor";
        public int Version => 2;

        private static readonly (string Family, Regex Re)[] Families =
        {
            ("lol",  new Regex(@"\blo+l+\b",      RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("lmao", new Regex(@"\blmao+\b",      RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("rofl", new Regex(@"\brofl\b",       RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("haha", new Regex(@"\b(?:ha){2,}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("hehe", new Regex(@"\b(?:he){2,}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("kek",  new Regex(@"\bke+k+\b",      RegexOptions.IgnoreCase | RegexOptions.Compiled)),
            ("xd",   new Regex(@"\bx+d+\b",       RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        };

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var msgs = AnalyticHelpers.LoadMessages(conn, personId);
            int total = 0, laughs = 0, multiExcl = 0, ironicQuotes = 0;
            int laughTokenTotal = 0;
            var byFamily = Families.ToDictionary(f => f.Family, _ => 0);
            var samples = new List<(int hits, string text)>();
            foreach (var m in msgs)
            {
                if (string.IsNullOrEmpty(m.Content)) continue;
                total++;
                int msgHits = 0;
                foreach (var f in Families)
                {
                    int c = f.Re.Matches(m.Content).Count;
                    if (c > 0) { byFamily[f.Family] += c; msgHits += c; }
                }
                if (msgHits > 0) { laughs++; laughTokenTotal += msgHits; }
                if (m.Content.Contains("!!")) multiExcl++;
                if (Regex.IsMatch(m.Content, "\"[^\" ]{2,}\"")) ironicQuotes++;
                if (msgHits >= 2 && m.Content.Length is > 0 and < 220) samples.Add((msgHits, m.Content));
            }
            string? dominant = byFamily.Values.All(v => v == 0)
                ? null
                : byFamily.OrderByDescending(kv => kv.Value).First().Key;
            return Task.FromResult(new JObject(
                new JProperty("messages_analysed", total),
                new JProperty("messages_with_laughter", laughs),
                new JProperty("laughter_token_total", laughTokenTotal),
                new JProperty("multi_exclamation_messages", multiExcl),
                new JProperty("ironic_quote_messages", ironicQuotes),
                new JProperty("dominant_laugh_family", dominant),
                new JProperty("laugh_family_distribution", new JArray(
                    byFamily.OrderByDescending(kv => kv.Value).Where(kv => kv.Value > 0)
                            .Select(kv => new JObject(new JProperty("family", kv.Key), new JProperty("count", kv.Value))))),
                new JProperty("sample_humorous_messages", new JArray(
                    samples.OrderByDescending(s => s.hits).Take(5)
                           .Select(s => new JObject(new JProperty("text", s.text), new JProperty("laugh_hits", s.hits))))),
                new JProperty("humor_score", total == 0 ? 0 :
                    (laughs + 0.5 * multiExcl + 0.5 * ironicQuotes) / (double)total)
            ));
        }
    }
}

