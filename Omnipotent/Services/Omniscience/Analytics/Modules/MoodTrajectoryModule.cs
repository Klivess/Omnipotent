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
    /// Mood trajectory: daily sentiment time series with anomaly detection. Flags
    /// periods where the 7-day rolling mood deviates sharply from the person's own
    /// 90-day baseline ("X seems down this week"). Powers briefing alerts.
    /// </summary>
    public class MoodTrajectoryModule : IPersonAnalyticModule
    {
        public string Name => "mood_trajectory";
        public int Version => 1;

        private static readonly Regex Word = new(@"[a-zA-Z]+", RegexOptions.Compiled);

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();
            var msgs = AnalyticHelpers.LoadMessages(conn, personId);

            // Daily sentiment means.
            var daily = new SortedDictionary<string, (double sum, int n)>();
            foreach (var m in msgs)
            {
                if (string.IsNullOrWhiteSpace(m.Content)) continue;
                double s = 0; int hits = 0;
                foreach (Match w in Word.Matches(m.Content))
                    if (SentimentModule.Lex.TryGetValue(w.Value, out double v)) { s += v; hits++; }
                if (hits == 0) continue;
                string day = m.SentAt.ToString("yyyy-MM-dd");
                daily.TryGetValue(day, out var cur);
                daily[day] = (cur.sum + s / hits, cur.n + 1);
            }

            var days = daily.Select(kv => (Day: kv.Key, Avg: kv.Value.sum / kv.Value.n, Count: kv.Value.n)).ToList();
            if (days.Count < 14)
                return Task.FromResult(new JObject(
                    new JProperty("days_with_data", days.Count),
                    new JProperty("anomalies", new JArray())));

            // Rolling stats: for each day, 7d window vs trailing 90d baseline.
            var anomalies = new JArray();
            double currentZ = 0;
            for (int i = 0; i < days.Count; i++)
            {
                var d = DateTime.Parse(days[i].Day);
                var win7 = days.Where(x => { var xd = DateTime.Parse(x.Day); return xd <= d && (d - xd).TotalDays < 7; }).ToList();
                var base90 = days.Where(x => { var xd = DateTime.Parse(x.Day); return xd < d && (d - xd).TotalDays is >= 7 and < 97; }).ToList();
                if (win7.Count < 3 || base90.Count < 14) continue;

                double winAvg = win7.Average(x => x.Avg);
                double baseAvg = base90.Average(x => x.Avg);
                double baseStd = Math.Sqrt(base90.Average(x => Math.Pow(x.Avg - baseAvg, 2)));
                if (baseStd < 0.05) baseStd = 0.05;
                double z = (winAvg - baseAvg) / baseStd;
                if (i == days.Count - 1) currentZ = z;

                if (Math.Abs(z) >= 1.5 && (anomalies.Count == 0 ||
                    (DateTime.Parse(days[i].Day) - DateTime.Parse(anomalies.Last!.Value<string>("date")!)).TotalDays > 7))
                {
                    anomalies.Add(new JObject(
                        new JProperty("date", days[i].Day),
                        new JProperty("direction", z > 0 ? "high" : "low"),
                        new JProperty("z_score", Math.Round(z, 2))));
                }
            }

            // Recent series for charting (last 120 days with data).
            var series = new JArray(days.TakeLast(120).Select(d => new JObject(
                new JProperty("date", d.Day),
                new JProperty("avg_sentiment", Math.Round(d.Avg, 3)),
                new JProperty("messages", d.Count))));

            return Task.FromResult(new JObject(
                new JProperty("days_with_data", days.Count),
                new JProperty("current_mood_z_score", Math.Round(currentZ, 2)),
                new JProperty("current_mood_flag", currentZ <= -1.5 ? "low" : currentZ >= 1.5 ? "high" : "normal"),
                new JProperty("anomalies", new JArray(anomalies.Reverse().Take(10).Reverse())),
                new JProperty("daily_series", series)
            ));
        }
    }
}
