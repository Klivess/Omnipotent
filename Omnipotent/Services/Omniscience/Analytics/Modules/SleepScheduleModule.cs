using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics.Modules
{
    /// <summary>
    /// Sleep schedule v2: per-day sleep windows inferred from activity gaps. Combines
    /// message timestamps with presence change-points when available (presence makes the
    /// "went offline at 02:14" edge visible even on silent days). Reports median bedtime,
    /// wake time, consistency, and likely all-nighters.
    /// </summary>
    public class SleepScheduleModule : IPersonAnalyticModule
    {
        public string Name => "sleep_schedule";
        public int Version => 1;

        public Task<JObject> ComputeAsync(string personId, OmniscienceDb db, CancellationToken ct)
        {
            using var conn = db.Open();

            int offsetMinutes = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT payload_json FROM person_statistics WHERE person_id=$p AND module_name='timezone_inference'";
                cmd.Parameters.AddWithValue("$p", personId);
                if (cmd.ExecuteScalar() is string raw)
                {
                    try { offsetMinutes = JObject.Parse(raw).Value<int?>("inferred_utc_offset_minutes") ?? 0; } catch { }
                }
            }

            // Activity timeline = message sends + presence transitions (last 180 days).
            long cutoff = DateTimeOffset.UtcNow.AddDays(-180).ToUnixTimeMilliseconds();
            var timestamps = new List<long>();
            var idents = AnalyticHelpers.GetPersonIdentityIds(conn, personId);
            if (idents.Count > 0)
            {
                using var cmd = conn.CreateCommand();
                string inC = AnalyticHelpers.BindInClause(cmd, "i", idents);
                cmd.CommandText = $"SELECT sent_at FROM messages WHERE author_identity_id IN ({inC}) AND sent_at >= $cut";
                cmd.Parameters.AddWithValue("$cut", cutoff);
                using var r = cmd.ExecuteReader();
                while (r.Read()) timestamps.Add(r.GetInt64(0));
            }
            int presenceSamples = 0;
            var userIds = AnalyticHelpers.GetPersonPlatformUserIds(conn, personId);
            if (userIds.Count > 0)
            {
                using var cmd = conn.CreateCommand();
                string inC = AnalyticHelpers.BindInClause(cmd, "u", userIds);
                cmd.CommandText = $@"SELECT captured_at FROM presence_events
                    WHERE platform_user_id IN ({inC}) AND captured_at >= $cut AND status IN ('online','idle','dnd')";
                cmd.Parameters.AddWithValue("$cut", cutoff);
                using var r = cmd.ExecuteReader();
                while (r.Read()) { timestamps.Add(r.GetInt64(0)); presenceSamples++; }
            }

            if (timestamps.Count < 30)
                return Task.FromResult(new JObject(
                    new JProperty("days_analysed", 0),
                    new JProperty("confidence", 0.0),
                    new JProperty("note", "insufficient activity data")));

            timestamps.Sort();

            // Walk gaps: any quiet stretch of 4-16h whose midpoint lands in local night
            // (21:00–11:00) is a candidate sleep window for that day.
            var bedtimes = new List<double>();   // local hour, may exceed 24 (e.g. 25.5 = 01:30)
            var wakes = new List<double>();
            var sleepDurations = new List<double>();
            var daysWithSleep = new HashSet<string>();
            for (int i = 1; i < timestamps.Count; i++)
            {
                double gapH = (timestamps[i] - timestamps[i - 1]) / 3_600_000.0;
                if (gapH < 4 || gapH > 16) continue;
                var start = DateTimeOffset.FromUnixTimeMilliseconds(timestamps[i - 1]).UtcDateTime.AddMinutes(offsetMinutes);
                var end = DateTimeOffset.FromUnixTimeMilliseconds(timestamps[i]).UtcDateTime.AddMinutes(offsetMinutes);
                var mid = start.AddHours(gapH / 2);
                bool nightish = mid.Hour >= 22 || mid.Hour < 11;
                if (!nightish) continue;

                double bed = start.Hour + start.Minute / 60.0;
                if (bed < 12) bed += 24; // normalise past-midnight bedtimes onto a 12–36 axis
                bedtimes.Add(bed);
                wakes.Add(end.Hour + end.Minute / 60.0);
                sleepDurations.Add(gapH);
                daysWithSleep.Add(end.ToString("yyyy-MM-dd"));
            }

            if (bedtimes.Count < 5)
                return Task.FromResult(new JObject(
                    new JProperty("days_analysed", daysWithSleep.Count),
                    new JProperty("confidence", 0.1),
                    new JProperty("note", "too few clear sleep windows")));

            double Median(List<double> xs) { var s = xs.OrderBy(x => x).ToList(); return s[s.Count / 2]; }
            double medianBed = Median(bedtimes);
            double medianWake = Median(wakes);
            double medianSleep = Median(sleepDurations);
            double bedStdDev = Math.Sqrt(bedtimes.Average(b => Math.Pow(b - bedtimes.Average(), 2)));

            // All-nighter heuristic: active days (any message) with no sleep window detected
            // before noon AND activity spanning 03:00–06:00 local.
            int allNighters = 0;
            var activeByDay = timestamps
                .Select(t => DateTimeOffset.FromUnixTimeMilliseconds(t).UtcDateTime.AddMinutes(offsetMinutes))
                .GroupBy(d => d.ToString("yyyy-MM-dd"));
            foreach (var day in activeByDay)
            {
                bool coveredSmallHours = day.Any(d => d.Hour == 3) && day.Any(d => d.Hour == 5);
                if (coveredSmallHours && !daysWithSleep.Contains(day.Key)) allNighters++;
            }

            string FormatHour(double h) { h %= 24; return $"{(int)h:00}:{(int)((h % 1) * 60):00}"; }
            return Task.FromResult(new JObject(
                new JProperty("days_analysed", daysWithSleep.Count),
                new JProperty("median_bedtime_local", FormatHour(medianBed)),
                new JProperty("median_wake_local", FormatHour(medianWake)),
                new JProperty("median_sleep_hours", Math.Round(medianSleep, 1)),
                new JProperty("bedtime_consistency_stddev_hours", Math.Round(bedStdDev, 2)),
                new JProperty("suspected_all_nighters_180d", allNighters),
                new JProperty("presence_samples_used", presenceSamples),
                new JProperty("utc_offset_minutes_used", offsetMinutes),
                new JProperty("confidence", Math.Min(1.0, bedtimes.Count / 60.0 + (presenceSamples > 200 ? 0.25 : 0)))
            ));
        }
    }
}
