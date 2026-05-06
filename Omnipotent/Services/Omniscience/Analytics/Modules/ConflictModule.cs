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
    /// Heuristic conflict signal: profanity density + caps-lock + insult terms.
    /// Surfaces top profanity tokens, top insults, sample heated messages, and
    /// hourly conflict distribution so the dossier panel is concrete rather than vague.
    /// </summary>
    public class ConflictModule : IPersonAnalyticModule
    {
        public string Name => "conflict";
        public int Version => 2;

        private static readonly string[] Insults =
        {
            "idiot","stupid","moron","retard","dumb","shut up","fuck you","fuck off",
            "kys","stfu","bitch","asshole","loser","clown","cunt","pathetic","trash"
        };
        private static readonly string[] Profanity =
        {
            "fuck","shit","bitch","cunt","ass","damn","bastard","crap","prick","dick"
        };

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var msgs = AnalyticHelpers.LoadMessages(conn, personId);
            int total = 0, profane = 0, capsHeavy = 0, insulting = 0;
            var profanityCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var insultCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var samples = new List<(int score, string text)>();
            int[] hourly = new int[24];
            foreach (var m in msgs)
            {
                if (string.IsNullOrEmpty(m.Content)) continue;
                total++;
                string c = m.Content;
                string low = c.ToLowerInvariant();

                int localScore = 0;
                bool wasProfane = false;
                foreach (var p in Profanity)
                {
                    if (Regex.IsMatch(low, "\\b" + Regex.Escape(p) + "\\b"))
                    {
                        profanityCounts.TryGetValue(p, out int pc);
                        profanityCounts[p] = pc + 1;
                        wasProfane = true; localScore++;
                    }
                }
                if (wasProfane) profane++;

                int letters = c.Count(char.IsLetter);
                int upper = c.Count(char.IsUpper);
                if (letters >= 6 && upper >= 0.7 * letters) { capsHeavy++; localScore++; }

                bool wasInsult = false;
                foreach (var ins in Insults)
                {
                    if (low.Contains(ins))
                    {
                        insultCounts.TryGetValue(ins, out int ic);
                        insultCounts[ins] = ic + 1;
                        wasInsult = true; localScore += 2;
                    }
                }
                if (wasInsult) insulting++;

                if (localScore >= 2) hourly[m.SentAt.Hour]++;
                if (localScore >= 2 && c.Length is > 0 and < 240) samples.Add((localScore, c));
            }
            return Task.FromResult(new JObject(
                new JProperty("messages_analysed", total),
                new JProperty("profane_messages", profane),
                new JProperty("caps_heavy_messages", capsHeavy),
                new JProperty("insult_pattern_messages", insulting),
                new JProperty("top_profanity", new JArray(
                    profanityCounts.OrderByDescending(k => k.Value).Take(8)
                                   .Select(k => new JObject(new JProperty("token", k.Key), new JProperty("count", k.Value))))),
                new JProperty("top_insults", new JArray(
                    insultCounts.OrderByDescending(k => k.Value).Take(8)
                                .Select(k => new JObject(new JProperty("token", k.Key), new JProperty("count", k.Value))))),
                new JProperty("sample_heated_messages", new JArray(
                    samples.OrderByDescending(s => s.score).Take(5)
                           .Select(s => new JObject(new JProperty("text", s.text), new JProperty("score", s.score))))),
                new JProperty("hourly_conflict_distribution", new JArray(hourly.Select(h => (JToken)h))),
                new JProperty("conflict_score", total == 0 ? 0 :
                    (profane + capsHeavy + 2 * insulting) / (double)total)
            ));
        }
    }
}

