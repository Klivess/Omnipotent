using System.Globalization;
using Omnipotent.Services.OmniscienceV3.Knowledge.Deduction;
using Omnipotent.Services.OmniscienceV3.Knowledge.Evidence;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Policy
{
    public static class AgeEvidenceKinds
    {
        public const string AgeNow = "age_now";
        public const string JustTurned = "just_turned";
        public const string Turning = "turning";
        public const string SchoolYear = "school_year";
        public const string Birthday = "birthday";
        public const string BirthYear = "birth_year";
        public const string BirthDate = "birth_date";
    }

    public static class AgeEvidenceStatus
    {
        public const string Active = "active";
        /// <summary>Outside the largest consistent set: a joke or misread candidate, raised for review (still counted for protection).</summary>
        public const string Inconsistent = "inconsistent";
        public const string Rejected = "rejected";
        /// <summary>The message was edited or erased: reactivated if the new version still says it.</summary>
        public const string Retracted = "retracted";
        /// <summary>A later message corrected it ("typo"): never counts again.</summary>
        public const string Corrected = "corrected";
    }

    /// <summary>One age statement as a birth-date constraint (policy evidence: never shown as a fact, only the band it implies).</summary>
    public sealed record AgeEvidenceRow(
        string EvidenceId,
        string PersonId,
        string Kind,
        TimeInterval? Birth,
        int? Month,
        int? Day,
        string SourceKind,
        long AssertedAtMs,
        string MessageId,
        int MessageVersion,
        string? ClaimId,
        double Probability,
        string Status,
        string NaturalKey);

    /// <summary>
    /// Age statements → birth intervals (plan T3 "Ages" and "School years"): "I'm 17" at t → birth ∈ (t−18y, t−17y];
    /// "just turned 17" → within ~5 weeks; "turning 18 next month" → the resolved window; "year 11" in England → the
    /// academic-year cohort; a birthday narrows the interval to its day in each candidate year. Statements are time-adjusted
    /// automatically because the anchor is the birth interval, never a stored age (F026).
    /// </summary>
    public static class AgeEvidenceBuilder
    {
        public static AgeEvidenceRow? FromClaim(VerifiedClaim c, EducationSystem defaultSystem)
        {
            if (c.Age is not { } age || c.Evidence.Count == 0) return null;
            var e = c.Evidence.OrderBy(x => x.SentAtMs).First();
            long t = e.SentAtMs;
            string form = (age.Form ?? "").Trim().ToLowerInvariant();
            TimeInterval? birth = null;
            string kind;
            int? month = null, day = null;
            switch (form)
            {
                case "now" when age.Years is int n and >= 0 and <= 120:
                    kind = AgeEvidenceKinds.AgeNow;
                    birth = LifeMath.BirthFromAge(n, t);
                    break;
                case "just_turned" when age.Years is int n2 and >= 0 and <= 120:
                    kind = AgeEvidenceKinds.JustTurned;
                    birth = LifeMath.BirthFromJustTurned(n2, t);
                    break;
                case "turning" when age.Years is int n3 and >= 1 and <= 121:
                {
                    kind = AgeEvidenceKinds.Turning;
                    var zone = e.AuthorZoneId != null && Zones.TryGet(e.AuthorZoneId, out var z) ? z : null;
                    var window = c.Occurred
                                 ?? TemporalResolver.Resolve(age.AheadSurface ?? c.TimeSurface, t, zone).Select(r => r.Interval).FirstOrDefault(i => i != null);
                    if (window is { } w && w.LatestMs > t)
                        birth = LifeMath.BirthFromTurning(n3, t, TimeSpan.FromMilliseconds(Math.Max(0, w.EarliestMs - t)), TimeSpan.FromMilliseconds(w.LatestMs - t));
                    else
                        birth = LifeMath.BirthFromTurning(n3, t, TimeSpan.Zero, TimeSpan.FromDays(366)); // "turning N" with no window: within a year
                    break;
                }
                case "school_year" when age.SchoolLevel is int level and >= 0 and <= 14:
                {
                    kind = AgeEvidenceKinds.SchoolYear;
                    var system = ParseSystem(age.SchoolSystem) ?? defaultSystem;
                    int lvl = level;
                    // Scottish secondary years are S1–S6, which LifeMath counts after P7.
                    if (system == EducationSystem.Scotland && (c.ValueText.Contains('S', StringComparison.Ordinal) || c.ValueText.Contains("secondary", StringComparison.OrdinalIgnoreCase))) lvl = level + 7;
                    birth = LifeMath.BirthFromSchoolLevel(lvl, system, t);
                    if (birth is { IsUnknown: true }) birth = null;
                    break;
                }
                case "birthday" when age.Month is int m and >= 1 and <= 12 && age.Day is int d and >= 1 and <= 31:
                    kind = AgeEvidenceKinds.Birthday;
                    month = m;
                    day = d;
                    if (age.Year is int y && y > 1890 && y <= Instant.ToUtcDateTime(t).Year && d <= DateTime.DaysInMonth(y, m))
                    {
                        kind = AgeEvidenceKinds.BirthDate;
                        var start = new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Utc);
                        birth = new TimeInterval(Instant.FromUtc(start), Instant.FromUtc(start.AddDays(1)) - 1);
                    }
                    break;
                case "birth_year" when age.Year is int by && by > 1890 && by <= Instant.ToUtcDateTime(t).Year:
                    kind = AgeEvidenceKinds.BirthYear;
                    birth = new TimeInterval(Instant.FromUtc(new DateTime(by, 1, 1, 0, 0, 0, DateTimeKind.Utc)), Instant.FromUtc(new DateTime(by + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc)) - 1);
                    break;
                default:
                    return null;
            }
            string key = NaturalKeys.Canonical(("k", kind), ("m", e.MessageId), ("s", "person:" + c.SubjectPersonId), ("v", form + ":" + (age.Years?.ToString(CultureInfo.InvariantCulture) ?? "") + ":" + (age.SchoolLevel?.ToString(CultureInfo.InvariantCulture) ?? "") + ":" + month + "-" + day));
            return new AgeEvidenceRow(NaturalKeys.Id("age", key), c.SubjectPersonId, kind, birth, month, day, c.SourceKind, t, e.MessageId, e.Version, c.ClaimId,
                c.Probability, AgeEvidenceStatus.Active, key);
        }

        public static EducationSystem? ParseSystem(string? s) => (s ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_') switch
        {
            "england_wales" or "england" or "wales" or "uk" or "england_and_wales" => EducationSystem.EnglandWales,
            "northern_ireland" => EducationSystem.NorthernIreland,
            "scotland" => EducationSystem.Scotland,
            "united_states" or "us" or "usa" => EducationSystem.UnitedStates,
            "unknown" or "" => null,
            _ => null,
        };
    }

    /// <summary>
    /// Combines age evidence into a band (plan Guardrails: stricter, never looser). The birth constraints intersect; when they
    /// are inconsistent the largest consistent set (by probability) decides the main interval and the rest are flagged as
    /// joke/misread candidates for review. For protection, any single unrejected statement that makes the person possibly
    /// under 18 at the instant keeps the band at least LikelyMinor until Klives rejects it in review.
    /// </summary>
    public static class AgeBands
    {
        public static (TimeInterval? Birth, IReadOnlyList<AgeEvidenceRow> Consistent, IReadOnlyList<AgeEvidenceRow> Inconsistent) Combine(IReadOnlyList<AgeEvidenceRow> rows)
        {
            var intervals = rows.Where(r => r.Birth != null && r.Status is AgeEvidenceStatus.Active or AgeEvidenceStatus.Inconsistent)
                .OrderByDescending(r => r.Probability * (r.SourceKind == ClaimSource.SelfDisclosure ? 1.0 : 0.6)).ThenBy(r => r.AssertedAtMs).ToList();
            TimeInterval acc = TimeInterval.Unknown;
            var consistent = new List<AgeEvidenceRow>();
            var inconsistent = new List<AgeEvidenceRow>();
            foreach (var r in intervals)
            {
                var next = acc.Intersect(r.Birth!.Value);
                if (next == null) { inconsistent.Add(r); continue; }
                acc = next.Value;
                consistent.Add(r);
            }
            TimeInterval? birth = consistent.Count == 0 ? null : acc;
            // A birthday narrows the interval to that calendar day in each candidate year.
            if (birth is { } b)
            {
                foreach (var bd in rows.Where(r => r.Month != null && r.Day != null && r.Status is AgeEvidenceStatus.Active))
                {
                    var candidates = LifeMath.BirthdayCandidates(bd.Month!.Value, bd.Day!.Value, b);
                    if (candidates.Count == 0) continue;
                    birth = new TimeInterval(candidates.Min(c => c.EarliestMs), candidates.Max(c => c.LatestMs));
                    break;
                }
            }
            return (birth, consistent, inconsistent);
        }

        public static AgeBand Evaluate(IReadOnlyList<AgeEvidenceRow> rows, long atMs)
        {
            var (birth, _, inconsistent) = Combine(rows);
            var band = MinorGuard.Evaluate(birth, false, atMs);
            foreach (var r in inconsistent)
            {
                var single = MinorGuard.Evaluate(r.Birth, false, atMs);
                if (MinorGuard.IsRestricted(single)) band = Stricter(band, AgeBand.LikelyMinor);
            }
            return band;
        }

        public static int Strictness(AgeBand band) => band switch
        {
            AgeBand.Minor => 3,
            AgeBand.LikelyMinor => 2,
            _ => 0,
        };

        /// <summary>The stricter of two bands; between two unrestricted bands, evidence of adulthood beats "unknown".</summary>
        public static AgeBand Stricter(AgeBand a, AgeBand b)
        {
            int sa = Strictness(a), sb = Strictness(b);
            if (sa != sb) return sa > sb ? a : b;
            if (sa > 0) return a;
            return a == AgeBand.Adult || b == AgeBand.Adult ? AgeBand.Adult : AgeBand.Unknown;
        }
    }
}
