using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics.Modules
{
    /// <summary>
    /// Chronotype classification from recency-weighted local-hour activity. Uses the
    /// timezone_inference module's stored offset (when available) to convert UTC send
    /// times to local. Also estimates the sleep window as the longest low-activity
    /// stretch — a v1 estimate that presence data (M2) later refines.
    /// </summary>
    public class ChronotypeModule : IPersonAnalyticModule
    {
        public string Name => "chronotype";
        public int Version => 1;

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();

            // Reuse the already-computed timezone offset rather than re-deriving it.
            int offsetMinutes = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT payload_json FROM person_statistics WHERE person_id=$p AND module_name='timezone_inference'";
                cmd.Parameters.AddWithValue("$p", personId);
                var raw = cmd.ExecuteScalar() as string;
                if (!string.IsNullOrEmpty(raw))
                {
                    try
                    {
                        var tz = JObject.Parse(raw);
                        offsetMinutes = tz.Value<int?>("inferred_utc_offset_minutes") ?? 0;
                    }
                    catch { }
                }
            }

            var msgs = AnalyticHelpers.LoadMessages(conn, personId);
            var now = DateTime.UtcNow;

            double[] localHours = new double[24];        // recency-weighted
            int[] localHoursRaw = new int[24];
            double weekend = 0, weekday = 0;
            foreach (var m in msgs)
            {
                if (string.IsNullOrWhiteSpace(m.Content)) continue;
                var local = m.SentAt.AddMinutes(offsetMinutes);
                double w = TemporalWeighting.Weight(m.SentAt, now);
                localHours[local.Hour] += w;
                localHoursRaw[local.Hour]++;
                if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) weekend += w; else weekday += w;
            }

            double total = localHours.Sum();
            double Share(int fromHour, int toHourExclusive)
            {
                if (total <= 0) return 0;
                double s = 0;
                for (int h = fromHour; h != toHourExclusive; h = (h + 1) % 24) s += localHours[h];
                return s / total;
            }
            double nightOwl = Share(22, 5);   // 22:00–04:59 local
            double earlyBird = Share(5, 10);  // 05:00–09:59 local

            string label = total <= 0 ? "unknown"
                : nightOwl > 0.30 ? "night_owl"
                : earlyBird > 0.20 ? "early_bird"
                : "typical";

            // Sleep window estimate: the quietest consecutive 7-hour stretch.
            int bestStart = 0; double bestSum = double.MaxValue;
            for (int start = 0; start < 24; start++)
            {
                double s = 0;
                for (int i = 0; i < 7; i++) s += localHours[(start + i) % 24];
                if (s < bestSum) { bestSum = s; bestStart = start; }
            }

            // Weekend share normalised against the 2/7 baseline (>1 = more active at weekends).
            double weekendShift = (weekend + weekday) <= 0 ? 0 : (weekend / (weekend + weekday)) / (2.0 / 7.0);

            return Task.FromResult(new JObject(
                new JProperty("utc_offset_minutes_used", offsetMinutes),
                new JProperty("chronotype", label),
                new JProperty("night_owl_score", nightOwl),
                new JProperty("early_bird_score", earlyBird),
                new JProperty("estimated_sleep_start_local_hour", bestStart),
                new JProperty("estimated_sleep_end_local_hour", (bestStart + 7) % 24),
                new JProperty("weekend_activity_shift", weekendShift),
                new JProperty("local_hour_histogram", new JArray(localHoursRaw.Select(h => (JToken)h)))
            ));
        }
    }
}
