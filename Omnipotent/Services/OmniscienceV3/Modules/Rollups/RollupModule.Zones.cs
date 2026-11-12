using System.Globalization;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Events;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Ml.Classification;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Modules.Rollups
{
    /// <summary>One month's zone reading for a person.</summary>
    public sealed record ZoneMonth(string Month, string? Zone, int? OffsetMinutes, double Confidence, string Method, IReadOnlyList<string> Alternatives);

    /// <summary>
    /// Time zones as time-varying facts (plan T4; replaces v2's location-lead regex F044 and fixed-offset chronotype F491):
    /// <list type="number">
    /// <item>Per month, the UTC offset (15-minute steps) that best aligns the person's quarter-hour activity profile with the
    /// human diurnal curve (night trough, evening peak) by circular correlation; confidence from how sharply it beats offsets
    /// three or more hours away, scaled by the data volume.</item>
    /// <item>Across months, the candidate IANA zone whose real offsets (DST included, per month) best match those readings:
    /// a person in Berlin and one in Lagos share winter offsets but not summer ones.</item>
    /// <item>A confident month that contradicts the main zone by ≥ 3 h is a period away (travel or a move) and gets its own zone.</item>
    /// <item>Thin months stay unknown (R4), except between confident months of the same zone, which they inherit at a reduced confidence.</item>
    /// <item>A zone the person stated (knowledge assertion <c>time_zone</c>, an IANA id) wins over rhythm.</item>
    /// <item>Minors are skipped entirely (MinorGuard: no routine inference).</item>
    /// </list>
    /// Zone-equivalent candidates (same offsets all year) are kept as alternatives; the representative is not a location claim.
    /// </summary>
    public static class ZoneInference
    {
        /// <summary>Relative chat activity by local hour (a population diurnal curve: trough before dawn, evening peak).</summary>
        public static readonly double[] DiurnalTemplate =
        {
            0.55, 0.35, 0.20, 0.12, 0.08, 0.08, 0.12, 0.25, 0.45, 0.60, 0.70, 0.75,
            0.80, 0.80, 0.80, 0.82, 0.85, 0.90, 0.95, 1.00, 1.00, 0.98, 0.90, 0.75,
        };

        public static readonly IReadOnlyList<string> Candidates = new[]
        {
            "Pacific/Pago_Pago", "Pacific/Honolulu", "America/Anchorage", "America/Los_Angeles", "America/Phoenix", "America/Denver", "America/Mexico_City",
            "America/Chicago", "America/Bogota", "America/New_York", "America/Caracas", "America/Halifax", "America/St_Johns", "America/Sao_Paulo",
            "America/Argentina/Buenos_Aires", "America/Noronha", "Atlantic/Azores", "Atlantic/Reykjavik", "Europe/London", "Africa/Lagos", "Europe/Berlin",
            "Africa/Johannesburg", "Africa/Cairo", "Europe/Helsinki", "Europe/Istanbul", "Europe/Moscow", "Asia/Tehran", "Asia/Dubai", "Asia/Kabul",
            "Asia/Karachi", "Asia/Kolkata", "Asia/Kathmandu", "Asia/Dhaka", "Asia/Yangon", "Asia/Bangkok", "Asia/Shanghai", "Asia/Singapore",
            "Australia/Perth", "Asia/Tokyo", "Asia/Seoul", "Australia/Darwin", "Australia/Adelaide", "Australia/Brisbane", "Australia/Sydney",
            "Pacific/Noumea", "Pacific/Auckland", "Pacific/Tongatapu", "Pacific/Kiritimati",
        };

        /// <summary>Best offset for one month's 96 UTC quarter-hour counts: (offset minutes, correlation, confidence 0..1).</summary>
        public static (int OffsetMinutes, double Score, double Confidence) BestOffset(IReadOnlyList<int> slots, int minMessages)
        {
            int total = slots.Sum();
            var template96 = Enumerable.Range(0, 96).Select(q => DiurnalTemplate[q / 4]).ToArray();
            double tMean = template96.Average();
            double Correlation(int offsetQuarters)
            {
                // Local quarter q corresponds to UTC quarter q - offset.
                var local = new double[96];
                for (int q = 0; q < 96; q++) local[q] = slots[((q - offsetQuarters) % 96 + 96) % 96];
                double lMean = local.Average(), num = 0, a = 0, b = 0;
                for (int q = 0; q < 96; q++)
                {
                    double x = local[q] - lMean, y = template96[q] - tMean;
                    num += x * y; a += x * x; b += y * y;
                }
                return a == 0 || b == 0 ? 0 : num / Math.Sqrt(a * b);
            }
            var scores = Enumerable.Range(-48, 105).Select(o => (Offset: o, Score: Correlation(o))).ToList(); // −12 h … +14 h
            var best = scores.OrderByDescending(s => s.Score).ThenBy(s => Math.Abs(s.Offset)).First();
            double farBest = scores.Where(s => Circular(s.Offset - best.Offset) >= 12).Max(s => s.Score);
            double sharpness = Math.Clamp((best.Score - farBest) / 0.6, 0, 1);
            double volume = Math.Clamp(total / (2.0 * minMessages), 0, 1);
            return (best.Offset * 15, best.Score, Math.Round(sharpness * volume * Math.Clamp(best.Score, 0, 1), 3));
        }

        /// <summary>Circular distance in quarter-hours on a 24 h dial.</summary>
        private static int Circular(int quarters)
        {
            int d = ((quarters % 96) + 96) % 96;
            return Math.Min(d, 96 - d);
        }

        private static int OffsetAtMidMonth(TimeZoneInfo zone, string month)
        {
            var first = DateOnly.ParseExact(month + "-15", "yyyy-MM-dd", CultureInfo.InvariantCulture);
            return (int)Zones.OffsetAt(Zones.DayStartUtcMs(first, TimeZoneInfo.Utc) + 12 * 3_600_000L, zone).TotalMinutes;
        }

        /// <summary>
        /// Zones for each month of a person's history from per-month readings (offset, confidence), the stated zone if any,
        /// and Klives's zone as the tie-break prior.
        /// </summary>
        public static IReadOnlyList<ZoneMonth> Assign(IReadOnlyList<(string Month, int Offset, double Confidence, bool Enough)> readings, string? statedZone, string klivesZone,
            double minConfidence = 0.3)
        {
            var months = readings.OrderBy(r => r.Month, StringComparer.Ordinal).ToList();
            if (statedZone != null && Zones.TryGet(statedZone, out _))
                return months.Select(m => new ZoneMonth(m.Month, statedZone, OffsetAtMidMonth(Zones.GetOrThrow(statedZone), m.Month), 0.9, "stated", Array.Empty<string>())).ToList();
            var confident = months.Where(m => m.Enough && m.Confidence >= minConfidence).ToList();
            if (confident.Count == 0)
                return months.Select(m => new ZoneMonth(m.Month, null, null, m.Confidence, "unknown", Array.Empty<string>())).ToList();

            var candidates = Candidates.Append(klivesZone).Distinct().Where(z => Zones.TryGet(z, out _)).Select(z => (Id: z, Zone: Zones.GetOrThrow(z))).ToList();
            double ErrorOf(TimeZoneInfo zone, IEnumerable<(string Month, int Offset, double Confidence, bool Enough)> set) =>
                set.Sum(m => m.Confidence * Math.Min(Math.Abs(OffsetAtMidMonth(zone, m.Month) - m.Offset), 1440 - Math.Abs(OffsetAtMidMonth(zone, m.Month) - m.Offset)) / 60.0);
            var ranked = candidates.Select(c => (c.Id, c.Zone, Error: ErrorOf(c.Zone, confident)))
                .OrderBy(c => Math.Round(c.Error, 6)).ThenBy(c => c.Id == klivesZone ? 0 : 1).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
            var main = ranked[0];
            var equivalents = ranked.Where(r => r.Id != main.Id && confident.All(m => OffsetAtMidMonth(r.Zone, m.Month) == OffsetAtMidMonth(main.Zone, m.Month)))
                .Select(r => r.Id).Take(5).ToList();

            var result = new List<ZoneMonth>();
            for (int i = 0; i < months.Count; i++)
            {
                var m = months[i];
                bool isConfident = m.Enough && m.Confidence >= minConfidence;
                if (isConfident)
                {
                    int mainOffset = OffsetAtMidMonth(main.Zone, m.Month);
                    int diff = Math.Min(Math.Abs(mainOffset - m.Offset), 1440 - Math.Abs(mainOffset - m.Offset));
                    if (diff >= 180)
                    {
                        // A period away: the candidate that fits this month alone.
                        var away = candidates.OrderBy(c => Math.Min(Math.Abs(OffsetAtMidMonth(c.Zone, m.Month) - m.Offset), 1440 - Math.Abs(OffsetAtMidMonth(c.Zone, m.Month) - m.Offset)))
                            .ThenBy(c => c.Id == klivesZone ? 0 : 1).ThenBy(c => c.Id, StringComparer.Ordinal).First();
                        result.Add(new ZoneMonth(m.Month, away.Id, OffsetAtMidMonth(away.Zone, m.Month), m.Confidence, "rhythm-away", Array.Empty<string>()));
                    }
                    else result.Add(new ZoneMonth(m.Month, main.Id, mainOffset, m.Confidence, "rhythm", equivalents));
                    continue;
                }
                // Thin month: inherit only when surrounded (or bounded) by confident months of the main zone within two months.
                bool Supports(int j) => j >= 0 && j < months.Count && months[j].Enough && months[j].Confidence >= minConfidence
                                        && Math.Min(Math.Abs(OffsetAtMidMonth(main.Zone, months[j].Month) - months[j].Offset), 1440 - Math.Abs(OffsetAtMidMonth(main.Zone, months[j].Month) - months[j].Offset)) < 180;
                // The nearest confident month on each side (within two) decides: it must agree with the main zone. A side with no
                // confident month within two counts only at the edge of the history; at least one side must have one.
                bool Confident(int j) => j >= 0 && j < months.Count && months[j].Enough && months[j].Confidence >= minConfidence;
                int? NearestBefore() => Enumerable.Range(1, 2).Select(k => i - k).Cast<int?>().FirstOrDefault(j => Confident(j!.Value));
                int? NearestAfter() => Enumerable.Range(1, 2).Select(k => i + k).Cast<int?>().FirstOrDefault(j => Confident(j!.Value));
                var nb = NearestBefore();
                var na = NearestAfter();
                bool before = nb is int b1 ? Supports(b1) : i < 2;
                bool after = na is int a1 ? Supports(a1) : i >= months.Count - 2;
                if (before && after && (nb != null || na != null))
                    result.Add(new ZoneMonth(m.Month, main.Id, OffsetAtMidMonth(main.Zone, m.Month), Math.Round(confident.Average(c => c.Confidence) * 0.5, 3), "inherited", equivalents));
                else
                    result.Add(new ZoneMonth(m.Month, null, null, m.Confidence, "unknown", Array.Empty<string>()));
            }
            return result;
        }
    }

    public sealed partial class RollupModule
    {
        /// <summary>
        /// Infers every person's zone per month from their slots and writes <c>rollup_zones</c>; months whose zone changed are
        /// rebuilt (their person-days move to the new local days).
        /// </summary>
        private async Task<string?> ZonesAsync(JobContext job)
        {
            var ct = job.Cancellation;
            int minMessages = Context.Settings.Get("rollups.zone_min_messages", 150);
            int minDays = Context.Settings.Get("rollups.zone_min_days", 8);
            string klives = Context.Settings.KlivesZone.Id;
            List<string> persons;
            using (var c = Context.Stores.Intel.OpenRead())
                persons = c.Query(null, "SELECT DISTINCT person_id FROM rollup_slots ORDER BY person_id", r => r.GetString(0));
            int changedPeople = 0, months = 0;
            long now = Context.Clock.NowMs;
            foreach (var person in persons)
            {
                ct.ThrowIfCancellationRequested();
                List<(string Month, int Msgs, int Days, int[] Slots)> rows;
                Dictionary<string, string?> previous;
                using (var c = Context.Stores.Intel.OpenRead())
                {
                    rows = c.Query(null, "SELECT month, msgs, days, slots FROM rollup_slots WHERE person_id=$p ORDER BY month",
                        r => (r.GetString(0), r.GetInt32(1), r.GetInt32(2), JsonSerializer.Deserialize<int[]>(r.GetString(3))!), ("$p", person));
                    previous = c.Query(null, "SELECT month, zone FROM rollup_zones WHERE person_id=$p", r => (r.GetString(0), r.GetStringOrNull(1)), ("$p", person))
                        .ToDictionary(x => x.Item1, x => x.Item2);
                }
                IReadOnlyList<ZoneMonth> assigned;
                if (MinorGuard.IsRestricted(Context.Policy.BandOf(person, now)))
                    assigned = rows.Select(r => new ZoneMonth(r.Month, null, null, 0, "minor-guard", Array.Empty<string>())).ToList();
                else
                {
                    var readings = rows.Select(r =>
                    {
                        var (offset, _, confidence) = ZoneInference.BestOffset(r.Slots, minMessages);
                        return (r.Month, offset, confidence, Enough: r.Msgs >= minMessages && r.Days >= minDays);
                    }).ToList();
                    assigned = ZoneInference.Assign(readings, StatedZone(person), klives);
                }
                var changed = assigned.Where(a => !previous.TryGetValue(a.Month, out var old) || old != a.Zone).Select(a => a.Month).ToList();
                await Context.Stores.Intel.WriteAsync((c, tx) =>
                {
                    using var put = new PreparedStatement(c, tx, """
                        INSERT INTO rollup_zones(person_id, month, zone, offset_minutes, confidence, method, alternatives, computed_at_ms) VALUES($p,$m,$z,$o,$c,$me,$a,$t)
                        ON CONFLICT(person_id, month) DO UPDATE SET zone=excluded.zone, offset_minutes=excluded.offset_minutes, confidence=excluded.confidence,
                            method=excluded.method, alternatives=excluded.alternatives, computed_at_ms=excluded.computed_at_ms
                        """, "$p", "$m", "$z", "$o", "$c", "$me", "$a", "$t");
                    foreach (var a in assigned)
                        put.Run(person, a.Month, a.Zone, a.OffsetMinutes, a.Confidence, a.Method, JsonSerializer.Serialize(a.Alternatives), now);
                }, ct).ConfigureAwait(false);
                months += assigned.Count;
                if (changed.Count > 0)
                {
                    changedPeople++;
                    ForgetZones(person);
                    // A month's days move to new local days: rebuild that month (and its neighbours' edge days via the month scope).
                    await AddDirtyAsync(changed.Select(m => (Cells.PersonAll, person, m)).ToList(), 0, ct).ConfigureAwait(false);
                }
            }
            await Context.Jobs.EnqueueAsync(Job("drain", "stream", "changes", priority: 115), ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { persons = persons.Count, months, changedPeople });
        }

        /// <summary>A zone the person stated, from the knowledge store (WS-B assertion <c>time_zone</c> with an IANA id), if any.</summary>
        private string? StatedZone(string person)
        {
            using var c = Context.Stores.Intel.OpenRead();
            string? value = c.Scalar<string>(null, """
                SELECT value_text FROM assertions WHERE subject_kind='person' AND subject_id=$p AND attribute IN ('time_zone','timezone')
                AND superseded_at_ms IS NULL AND retracted_at_ms IS NULL AND status IN ('confirmed','proposed') ORDER BY recorded_at_ms DESC LIMIT 1
                """, ("$p", person));
            return value != null && Zones.TryGet(value, out _) ? value : null;
        }

        // ── drift ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Weekly label-distribution drift per task (plan F-ML: "a drift alert fires when weekly label distributions shift beyond
        /// threshold"): the last complete Klives-week against the eight before it, from global day cells of the active label set
        /// only (a label-set change is not drift). Above the threshold, one alert per task and week.
        /// </summary>
        private async Task<string?> DriftAsync(JobContext job)
        {
            var ct = job.Cancellation;
            int? labelset = ActiveLabelset();
            if (labelset == null) return null;
            var tz = Context.Settings.KlivesZone;
            var today = Zones.LocalDate(Context.Clock.NowMs, tz);
            var thisWeekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)); // ISO weeks start on Monday
            var weekStart = thisWeekStart.AddDays(-7);
            var baseStart = weekStart.AddDays(-56);
            List<(string Day, string Labels, int? Ls)> cells;
            using (var c = Context.Stores.Intel.OpenRead())
                cells = c.Query(null, "SELECT day, labels, labelset FROM rollup_scope_day WHERE scope='global' AND key='all' AND day >= $a AND day < $b",
                    r => (r.GetString(0), r.GetString(1), (int?)r.GetInt64OrNull(2)), ("$a", Day(baseStart)), ("$b", Day(thisWeekStart)));
            cells = cells.Where(x => x.Ls == labelset).ToList();
            double threshold = Context.Settings.Get("rollups.drift_psi_alert", 0.2);
            string week = ISOWeek.GetYear(weekStart.ToDateTime(TimeOnly.MinValue)).ToString(CultureInfo.InvariantCulture) + "-W"
                          + ISOWeek.GetWeekOfYear(weekStart.ToDateTime(TimeOnly.MinValue)).ToString("00", CultureInfo.InvariantCulture);
            var results = new List<object>();
            foreach (var task in ClassificationTasks.All.Where(t => t.Id != ClassificationTasks.Distress.Id))
            {
                var current = new Dictionary<string, double>(StringComparer.Ordinal);
                var baseline = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var (day, labels, _) in cells)
                {
                    var target = string.CompareOrdinal(day, Day(weekStart)) >= 0 ? current : baseline;
                    var all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(labels);
                    if (all == null || !all.TryGetValue(task.Id, out var counts)) continue;
                    foreach (var (label, n) in counts) target[label] = target.GetValueOrDefault(label) + n;
                }
                int answered = (int)current.Values.Sum();
                if (answered < 200 || baseline.Values.Sum() < 400) continue;
                double psi = Calibration.Psi(baseline, current);
                bool alert = psi >= threshold;
                bool already;
                using (var c = Context.Stores.Intel.OpenRead())
                    already = c.Scalar<long?>(null, "SELECT alerted FROM rollup_drift WHERE task=$t AND week=$w", ("$t", task.Id), ("$w", week)) == 1;
                await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                    INSERT INTO rollup_drift(task, week, distribution, answered, psi, alerted, computed_at_ms) VALUES($t,$w,$d,$n,$p,$a,$now)
                    ON CONFLICT(task, week) DO UPDATE SET distribution=excluded.distribution, answered=excluded.answered, psi=excluded.psi,
                        alerted=MAX(alerted, excluded.alerted), computed_at_ms=excluded.computed_at_ms
                    """, ("$t", task.Id), ("$w", week), ("$d", JsonSerializer.Serialize(current)), ("$n", answered), ("$p", psi), ("$a", alert ? 1 : 0),
                    ("$now", Context.Clock.NowMs)), ct).ConfigureAwait(false);
                if (alert && !already)
                    await Context.Events.PublishAsync(Topics.Alert, new { kind = "classify.drift", task = task.Id, week, psi = Math.Round(psi, 3), answered, labelset }, ct).ConfigureAwait(false);
                results.Add(new { task = task.Id, psi = Math.Round(psi, 3), answered, alert });
            }
            return JsonSerializer.Serialize(new { week, results });
        }
    }
}
