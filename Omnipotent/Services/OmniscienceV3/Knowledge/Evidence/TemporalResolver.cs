using System.Globalization;
using Microsoft.Recognizers.Text;
using Microsoft.Recognizers.Text.DateTime;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Evidence
{
    /// <summary>How a model's resolution of a temporal expression compares with the deterministic resolver's.</summary>
    public enum TemporalCheck
    {
        /// <summary>Both resolved and the intervals overlap (within tolerance).</summary>
        Agrees,
        /// <summary>Both resolved and they are disjoint: the claim's interval is replaced and its confidence lowered.</summary>
        Disagrees,
        /// <summary>The deterministic resolver could not resolve the expression (vague words, other languages): nothing to compare.</summary>
        Unresolved,
        /// <summary>The model gave no resolution: the deterministic one is used when there is one.</summary>
        ModelSilent,
    }

    /// <summary>One deterministic resolution: an interval, or a duration ("for two years").</summary>
    public sealed record TemporalResolution(TimeInterval? Interval, TimeSpan? Duration, string Kind, string Timex);

    public sealed record TemporalCheckResult(TemporalCheck Check, TimeInterval? Chosen, IReadOnlyList<TemporalResolution> Deterministic, string? Note);

    /// <summary>
    /// The deterministic cross-check of plan rule T3: every temporal expression the extractor resolves is re-resolved by
    /// Microsoft.Recognizers.Text.DateTime at the message's author-local reference time ("tomorrow" sent at 23:59 resolves
    /// against the author's local day), converted to UTC through the author's IANA zone (DST-safe). When the author's zone is
    /// unknown the interval is widened by ±14 h (every UTC offset). Ambiguous expressions ("Saturday") yield several
    /// candidates; the model's interval agrees when it overlaps any of them.
    /// </summary>
    public static class TemporalResolver
    {
        private static readonly string[] Cultures =
        {
            Culture.English, Culture.Spanish, Culture.French, Culture.German, Culture.Portuguese, Culture.Italian, Culture.Dutch,
            Culture.Turkish, Culture.Swedish, Culture.Chinese, Culture.Japanese, Culture.Korean, Culture.Hindi, Culture.Arabic,
        };

        private static readonly TimeSpan UnknownZoneSlack = TimeSpan.FromHours(14);

        public static IReadOnlyList<TemporalResolution> Resolve(string? expression, long referenceUtcMs, TimeZoneInfo? authorZone, string? preferredCulture = null)
        {
            if (string.IsNullOrWhiteSpace(expression) || expression.Length > 200) return Array.Empty<TemporalResolution>();
            var zone = authorZone ?? TimeZoneInfo.Utc;
            var reference = Zones.ToLocal(referenceUtcMs, zone);
            foreach (var culture in Order(preferredCulture))
            {
                List<ModelResult> results;
                try
                {
                    results = DateTimeRecognizer.RecognizeDateTime(expression, culture, DateTimeOptions.None, reference, fallbackToDefaultCulture: false);
                }
                catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or NullReferenceException)
                {
                    // A recogniser failure on odd input is "unresolved", never a crash of the verifier.
                    continue;
                }
                // The recogniser must have read the expression, not a fragment of it: in "back in the day" it finds "the day"
                // and calls it today. A reading that leaves most of the words unexplained is no reading at all.
                if (Coverage(expression, results) < MinimumCoverage) continue;
                var resolved = new List<TemporalResolution>();
                foreach (var r in results)
                    foreach (var value in Values(r))
                        if (ToResolution(value, zone, authorZone == null) is { } res) resolved.Add(res);
                if (resolved.Count > 0) return resolved;
            }
            return Array.Empty<TemporalResolution>();
        }

        /// <summary>The share of the expression's letters and digits a deterministic reading must account for.</summary>
        public const double MinimumCoverage = 0.6;

        /// <summary>The share of the expression's letters and digits that lie inside what the recogniser matched.</summary>
        private static double Coverage(string expression, IReadOnlyList<ModelResult> results)
        {
            var covered = new bool[expression.Length];
            foreach (var r in results)
                for (int i = Math.Max(0, r.Start); i <= r.End && i < expression.Length; i++) covered[i] = true;
            int total = 0, matched = 0;
            for (int i = 0; i < expression.Length; i++)
            {
                if (!char.IsLetterOrDigit(expression[i])) continue;
                total++;
                if (covered[i]) matched++;
            }
            return total == 0 ? 0 : (double)matched / total;
        }

        private static IEnumerable<string> Order(string? preferred)
        {
            if (!string.IsNullOrWhiteSpace(preferred))
            {
                var match = Cultures.FirstOrDefault(c => c.StartsWith(preferred.Trim().ToLowerInvariant(), StringComparison.Ordinal));
                if (match != null) yield return match;
            }
            foreach (var c in Cultures) yield return c;
        }

        private static IEnumerable<IReadOnlyDictionary<string, string>> Values(ModelResult r)
        {
            if (r.Resolution == null || !r.Resolution.TryGetValue("values", out var raw) || raw is not System.Collections.IEnumerable list) yield break;
            foreach (var item in list)
            {
                if (item is IDictionary<string, string> d) yield return new Dictionary<string, string>(d, StringComparer.Ordinal);
                else if (item is System.Collections.IDictionary g)
                {
                    var copy = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (System.Collections.DictionaryEntry e in g)
                        if (e.Key is string k && e.Value is string v) copy[k] = v;
                    yield return copy;
                }
            }
        }

        private static TemporalResolution? ToResolution(IReadOnlyDictionary<string, string> v, TimeZoneInfo zone, bool zoneUnknown)
        {
            v.TryGetValue("type", out var type);
            v.TryGetValue("timex", out var timex);
            timex ??= "";
            TimeInterval? interval = null;
            TimeSpan? duration = null;
            switch (type)
            {
                case "date" when v.TryGetValue("value", out var day) && TryDate(day, out var d):
                    interval = Day(d, zone);
                    break;
                case "daterange" when v.TryGetValue("start", out var s) && TryDate(s, out var ds):
                {
                    DateOnly de = v.TryGetValue("end", out var e) && TryDate(e, out var parsed) ? parsed : ds.AddDays(1);
                    if (de <= ds) de = ds.AddDays(1);
                    interval = Range(Zones.DayStartUtcMs(ds, zone), Zones.DayStartUtcMs(de, zone) - 1);
                    break;
                }
                case "datetime" when v.TryGetValue("value", out var dt) && TryDateTime(dt, out var local):
                {
                    long at = Zones.ResolveLocal(local, zone).UtcMs;
                    interval = Range(at, at + 59_999);
                    break;
                }
                case "datetimerange" or "timerange" when v.TryGetValue("start", out var s2) && TryDateTime(s2, out var ls):
                {
                    long start = Zones.ResolveLocal(ls, zone).UtcMs;
                    long end = v.TryGetValue("end", out var e2) && TryDateTime(e2, out var le) ? Zones.ResolveLocal(le, zone).UtcMs : start + 3_600_000;
                    interval = Range(start, Math.Max(start, end));
                    break;
                }
                case "duration" when v.TryGetValue("value", out var secs) && double.TryParse(secs, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds):
                    duration = TimeSpan.FromSeconds(seconds);
                    break;
                default:
                    return null; // sets ("every Sunday"), bare times without a date, unknown shapes
            }
            if (interval is { } iv && zoneUnknown) interval = iv.Widen(UnknownZoneSlack);
            return new TemporalResolution(interval, duration, type ?? "", timex);
        }

        private static TimeInterval Day(DateOnly d, TimeZoneInfo zone)
        {
            var (start, end) = Zones.LocalDayRange(d, zone);
            return Range(start, end - 1);
        }

        private static TimeInterval Range(long a, long b) => new(Math.Min(a, b), Math.Max(a, b));

        private static bool TryDate(string s, out DateOnly d) =>
            DateOnly.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);

        private static bool TryDateTime(string s, out DateTime local)
        {
            bool ok = DateTime.TryParseExact(s.Trim(), new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd", "HH:mm:ss" }, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out local);
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            return ok && local.Year > 1;
        }

        /// <summary>
        /// Compares the model's interval with the deterministic candidates. Agreement means overlap after widening the
        /// model's interval by <paramref name="tolerance"/> (default 12 h, covering zone slips at day granularity).
        /// </summary>
        public static TemporalCheckResult Check(TimeInterval? model, string? expression, long referenceUtcMs, TimeZoneInfo? authorZone, TimeSpan? tolerance = null)
        {
            var det = Resolve(expression, referenceUtcMs, authorZone).Where(r => r.Interval != null).ToList();
            if (model == null)
            {
                return det.Count == 1
                    ? new TemporalCheckResult(TemporalCheck.ModelSilent, det[0].Interval, det, "Resolved deterministically; the model gave no interval.")
                    : new TemporalCheckResult(TemporalCheck.ModelSilent, null, det, det.Count > 1 ? "Ambiguous expression; left unresolved." : null);
            }
            if (det.Count == 0) return new TemporalCheckResult(TemporalCheck.Unresolved, model, det, null);
            var widened = model.Value.Widen(tolerance ?? TimeSpan.FromHours(12));
            if (det.Any(d => d.Interval!.Value.Overlaps(widened))) return new TemporalCheckResult(TemporalCheck.Agrees, model, det, null);
            // Disjoint: prefer the deterministic reading when it is unambiguous; otherwise keep the model's and flag it.
            var chosen = det.Count == 1 ? det[0].Interval : model;
            return new TemporalCheckResult(TemporalCheck.Disagrees, chosen, det,
                $"The model resolved '{expression}' to {model} but the deterministic resolver read {string.Join(" or ", det.Select(d => d.Interval.ToString()))}.");
        }
    }
}
