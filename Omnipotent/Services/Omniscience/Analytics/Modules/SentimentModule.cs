using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics.Modules
{
    /// <summary>Lexicon-based sentiment (VADER-flavoured but trimmed). Phase-1 deterministic.</summary>
    public class SentimentModule : IPersonAnalyticModule
    {
        public string Name => "sentiment";
        public int Version => 1;

        private static readonly Dictionary<string, double> Lex = new(StringComparer.OrdinalIgnoreCase)
        {
            // positive
            {"love",2},{"loved",2},{"loving",2},{"awesome",2},{"great",1.5},{"good",1},{"nice",1},{"happy",1.5},
            {"amazing",2},{"perfect",1.5},{"thanks",1},{"thank",1},{"lol",0.6},{"lmao",0.6},{"haha",0.6},{"haha",0.6},
            {"cool",1},{"yay",1.2},{"win",1},{"wins",1},{"won",1},{"best",1.5},
            // negative
            {"hate",-2},{"hated",-2},{"hating",-2},{"shit",-1.2},{"fuck",-1.2},{"fucked",-1.5},{"fucking",-0.5},
            {"bad",-1},{"awful",-2},{"terrible",-2},{"sad",-1.5},{"angry",-1.5},{"mad",-1},{"sucks",-1.2},{"trash",-1},
            {"worst",-1.8},{"annoying",-1.2},{"crap",-1},{"ugh",-1},{"stupid",-1.2},{"idiot",-1.5}
        };
        private static readonly Regex Word = new(@"[a-zA-Z]+", RegexOptions.Compiled);

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var msgs = AnalyticHelpers.LoadMessages(conn, personId);
            double total = 0;
            int scored = 0;
            int pos = 0, neg = 0, neu = 0;
            // monthly trend
            var monthly = new Dictionary<string, (double sum, int n)>();
            foreach (var m in msgs)
            {
                if (string.IsNullOrWhiteSpace(m.Content)) continue;
                double s = 0;
                int hits = 0;
                foreach (Match w in Word.Matches(m.Content))
                {
                    if (Lex.TryGetValue(w.Value, out double v)) { s += v; hits++; }
                }
                if (hits == 0) { neu++; continue; }
                double norm = s / Math.Max(1, hits);
                total += norm;
                scored++;
                if (norm > 0.1) pos++; else if (norm < -0.1) neg++; else neu++;
                string key = m.SentAt.ToString("yyyy-MM");
                if (!monthly.TryGetValue(key, out var cur)) cur = (0, 0);
                monthly[key] = (cur.sum + norm, cur.n + 1);
            }
            double avg = scored > 0 ? total / scored : 0;
            var trend = monthly.OrderBy(k => k.Key).Select(k => new JObject(
                new JProperty("month", k.Key),
                new JProperty("avg", k.Value.sum / Math.Max(1, k.Value.n)),
                new JProperty("count", k.Value.n)));
            var payload = new JObject(
                new JProperty("avg_sentiment", avg),
                new JProperty("scored_messages", scored),
                new JProperty("positive_count", pos),
                new JProperty("negative_count", neg),
                new JProperty("neutral_count", neu),
                new JProperty("monthly_trend", new JArray(trend))
            );
            return Task.FromResult(payload);
        }
    }
}
