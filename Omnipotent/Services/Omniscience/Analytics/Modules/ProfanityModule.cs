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
    /// Profanity profile: severity-tiered rates, top tokens, monthly trend. The facet
    /// split (via AnalyticSplits) is the headline feature — where a person swears freely
    /// vs where they self-censor is a strong intimacy/formality signal.
    /// </summary>
    public class ProfanityModule : IPersonAnalyticModule
    {
        public string Name => "profanity";
        public int Version => 1;

        internal static readonly Dictionary<string, int> Severity = new(StringComparer.OrdinalIgnoreCase)
        {
            // 1 = mild, 2 = moderate, 3 = strong
            {"damn",1},{"crap",1},{"hell",1},{"bloody",1},{"piss",1},{"pissed",1},
            {"shit",2},{"ass",2},{"arse",2},{"bastard",2},{"bitch",2},{"dick",2},{"prick",2},{"wanker",2},{"bollocks",2},
            {"fuck",3},{"fucking",3},{"fucked",3},{"fucker",3},{"motherfucker",3},{"cunt",3},
        };
        private static readonly Regex Word = new(@"[a-zA-Z]+", RegexOptions.Compiled);

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var msgs = AnalyticHelpers.LoadMessages(conn, personId);
            return Task.FromResult(AnalyticSplits.Apply(msgs, ComputeFromMessages,
                AnalyticSplits.CompactWithArrays(5, "top_tokens")));
        }

        internal static JObject ComputeFromMessages(List<AnalyticMessage> msgs)
        {
            int analysed = 0, profaneMessages = 0;
            long totalTokens = 0;
            int hitsMild = 0, hitsModerate = 0, hitsStrong = 0;
            var tokenCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var monthly = new Dictionary<string, (int profane, int total)>();

            foreach (var m in msgs)
            {
                if (string.IsNullOrWhiteSpace(m.Content)) continue;
                analysed++;
                bool profane = false;
                foreach (Match w in Word.Matches(m.Content))
                {
                    totalTokens++;
                    if (!Severity.TryGetValue(w.Value, out int sev)) continue;
                    profane = true;
                    tokenCounts.TryGetValue(w.Value.ToLowerInvariant(), out int c);
                    tokenCounts[w.Value.ToLowerInvariant()] = c + 1;
                    if (sev == 1) hitsMild++; else if (sev == 2) hitsModerate++; else hitsStrong++;
                }
                if (profane) profaneMessages++;
                string key = m.SentAt.ToString("yyyy-MM");
                monthly.TryGetValue(key, out var cur);
                monthly[key] = (cur.profane + (profane ? 1 : 0), cur.total + 1);
            }

            int totalHits = hitsMild + hitsModerate + hitsStrong;
            var trend = monthly.OrderBy(k => k.Key).Select(k => new JObject(
                new JProperty("month", k.Key),
                new JProperty("profane_rate", k.Value.total == 0 ? 0 : (double)k.Value.profane / k.Value.total),
                new JProperty("count", k.Value.total)));

            return new JObject(
                new JProperty("messages_analysed", analysed),
                new JProperty("profane_messages", profaneMessages),
                new JProperty("profanity_rate", analysed == 0 ? 0 : (double)profaneMessages / analysed),
                new JProperty("profanity_per_1k_tokens", totalTokens == 0 ? 0 : 1000.0 * totalHits / totalTokens),
                new JProperty("mild_hits", hitsMild),
                new JProperty("moderate_hits", hitsModerate),
                new JProperty("strong_hits", hitsStrong),
                new JProperty("strong_share", totalHits == 0 ? 0 : (double)hitsStrong / totalHits),
                new JProperty("top_tokens", new JArray(
                    tokenCounts.OrderByDescending(kv => kv.Value).Take(10)
                               .Select(kv => new JObject(new JProperty("token", kv.Key), new JProperty("count", kv.Value))))),
                new JProperty("monthly_trend", new JArray(trend))
            );
        }
    }
}
