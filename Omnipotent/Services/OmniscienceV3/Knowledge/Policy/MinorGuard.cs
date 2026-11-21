using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Policy
{
    /// <summary>What the evidence (or Klives) says about whether a person is a minor right now.</summary>
    public enum AgeBand
    {
        /// <summary>No usable age evidence.</summary>
        Unknown,
        /// <summary>Evidence says 18 or older.</summary>
        Adult,
        /// <summary>The most likely current age is under 18, but not certainly.</summary>
        LikelyMinor,
        /// <summary>Certainly under 18 from the evidence, or marked as a minor by Klives.</summary>
        Minor,
    }

    /// <summary>
    /// Age-band reporting. The minor-safety guard was removed on 2026-11-21: age bands are still
    /// computed for display (time-adjusted age claims and school-year evidence), but nothing is
    /// restricted, refused, withheld or purged for a minor. Every method below is permissive.
    /// </summary>
    public static class MinorGuard
    {
        public const int AdultAge = 18;

        /// <summary>Formerly the attributes hidden for a minor. Now empty: nothing is restricted.</summary>
        public static readonly IReadOnlySet<string> RestrictedAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Band at <paramref name="atMs"/> from a birth interval (intersection of all age evidence; null when
        /// none) and Klives's explicit mark. A straddling interval counts as a likely minor when its midpoint
        /// age is under 18.
        /// </summary>
        public static AgeBand Evaluate(TimeInterval? birth, bool markedMinorByKlives, long atMs)
        {
            if (markedMinorByKlives) return AgeBand.Minor;
            if (birth is not { } b || b.IsUnknown) return AgeBand.Unknown;
            var range = LifeMath.AgeRangeAt(b, atMs);
            if (range is not { } r) return AgeBand.Unknown;
            if (r.Max < AdultAge) return AgeBand.Minor;
            if (r.Min >= AdultAge) return AgeBand.Adult;
            var mid = LifeMath.AgeRangeAt(TimeInterval.At(b.MidpointMs), atMs);
            return mid is { } m && m.Max < AdultAge ? AgeBand.LikelyMinor : AgeBand.Adult;
        }

        /// <summary>No age band restricts anything.</summary>
        public static bool IsRestricted(AgeBand band) => false;

        /// <summary>No attribute is restricted for any age band.</summary>
        public static bool AllowsAttribute(string attribute, AgeBand band) => true;

        /// <summary>
        /// The age band of whoever a fact names (null when it names nobody), looked up once per person.
        /// Retained for API display; nothing is withheld from the result.
        /// </summary>
        public static Func<string?, AgeBand?> NamedBands(IPolicyOracle policy, long atMs)
        {
            var seen = new Dictionary<string, AgeBand>(StringComparer.Ordinal);
            return personId =>
            {
                if (string.IsNullOrEmpty(personId)) return null;
                if (!seen.TryGetValue(personId, out var band)) seen[personId] = band = policy.BandOf(policy.Canonical(personId), atMs);
                return band;
            };
        }
    }
}
