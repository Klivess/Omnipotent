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

    /// <summary>Capabilities the minor-safety guard switches off for minors (plan Guardrails).</summary>
    public enum RestrictedCapability
    {
        FaceLabelling,
        Replica,
        PersonTargetedAlert,
        RoutineInference,
        IdentityFacts,
        TrackingSuggestion,
        ReplicaBasedTactic,
    }

    /// <summary>
    /// The minor-safety guard. When the evidence says a person is under 18 (time-adjusted age claims,
    /// school-year statements) or Klives marks them, Omniscience stops building an identity dossier on them:
    /// no face labels, no replica, no person-targeted alerts or tracking suggestions, no schedule/routine
    /// inference, and restricted identity facts are neither stored, inferred nor shown. Their messages and
    /// conversations stay browsable. A misread age is corrected by rejecting the age fact in review, which
    /// recomputes the band; there is deliberately no "mark as adult" override that bypasses the evidence.
    /// </summary>
    public static class MinorGuard
    {
        public const int AdultAge = 18;

        /// <summary>Fact attributes never stored, inferred or shown for a minor.</summary>
        public static readonly IReadOnlySet<string> RestrictedAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "real_name", "full_name", "surname", "legal_name",
            "school", "college", "school_year", "class",
            "home_location", "address", "city", "neighbourhood", "postcode",
            "contact_handle", "phone", "email", "social_handle",
            "family_member", "family_member_name", "parent", "sibling",
            "birth_date", "exact_age",
            "daily_routine", "sleep_schedule", "active_hours",
        };

        /// <summary>
        /// Band at <paramref name="atMs"/> from a birth interval (intersection of all age evidence; null when
        /// none) and Klives's explicit mark. A straddling interval counts as a likely minor when its midpoint
        /// age is under 18, so the guard errs towards protection.
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

        public static bool IsRestricted(AgeBand band) => band is AgeBand.Minor or AgeBand.LikelyMinor;

        public static bool Allows(RestrictedCapability capability, AgeBand band) => !IsRestricted(band);

        public static bool AllowsAttribute(string attribute, AgeBand band) =>
            !IsRestricted(band) || !RestrictedAttributes.Contains(attribute);
    }
}
