using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Store
{
    /// <summary>
    /// Temporal semantics of an attribute (plan rule T2). The class decides how a newer value relates to an older one
    /// (a contradiction, or a change over time), whether confidence decays, and how "current" is computed.
    /// </summary>
    public enum AttributeClass
    {
        /// <summary>One true value for life (birthplace, first language). Conflicting literal self-claims are a contradiction.</summary>
        Immutable,
        /// <summary>Interval-valued (city, job, relationship status). A newer value closes the older interval: a timeline
        /// entry, not a contradiction. Confidence decays from the last corroboration.</summary>
        State,
        /// <summary>Computed at query time from a stored anchor (age from a birth interval). Never stored as a static value.</summary>
        TimeDerived,
        /// <summary>Has an occurrence instant or interval (trips, exams, parties). Status is recomputed against as-of.</summary>
        Event,
        /// <summary>Preferences and opinions: "as of last evidence", short half-life, can flip.</summary>
        Preference,
        /// <summary>Habits and rhythms: windowed statistics with eras.</summary>
        Habit,
        /// <summary>A relation to another person (friend, partner, sibling). Interval-valued like a state.</summary>
        Relation,
    }

    /// <summary>Legacy visibility metadata. The minor guard and sensitive-trait ban were removed, so every
    /// attribute is <see cref="Normal"/> in practice; the enum remains for stored-row compatibility.</summary>
    public enum AttributeSensitivity
    {
        Normal,
        RestrictedForMinors,
        SensitiveTrait,
    }

    /// <summary>One entry of the controlled attribute vocabulary.</summary>
    public sealed record AttributeDefinition(
        string Id,
        string Title,
        string Category,
        AttributeClass Class,
        double? HalfLifeDays,
        AttributeSensitivity Sensitivity,
        bool Private,
        string? LifeEventCategory = null,
        string? PlanKind = null)
    {
        /// <summary>Whether a newer value of this attribute closes the interval of the older one (state change, not contradiction).</summary>
        public bool IsIntervalValued => Class is AttributeClass.State or AttributeClass.Relation or AttributeClass.Preference or AttributeClass.Habit;

        /// <summary>Several values may hold at once (likes cats AND dogs; speaks English AND Spanish).</summary>
        public bool MultiValued { get; init; }
    }

    /// <summary>
    /// The controlled vocabulary the extractor must use (plus "other"). One definition per attribute: temporal class,
    /// confidence half-life, category and privacy (DM evidence outweighs public evidence for private facts). The
    /// former policy-sensitivity metadata is inert. Categories drive the Quality Lab's per-category precision and
    /// X16's coverage map.
    /// </summary>
    public static class Attributes
    {
        public const string Other = "other";

        // Categories.
        public const string Identity = "identity";
        public const string Location = "location";
        public const string Work = "work";
        public const string Education = "education";
        public const string Relationships = "relationships";
        public const string Family = "family";
        public const string Interests = "interests";
        public const string Preferences = "preferences";
        public const string Possessions = "possessions";
        public const string Skills = "skills";
        public const string Habits = "habits";
        public const string Contact = "contact";
        public const string Events = "events";
        public const string Plans = "plans";
        public const string Sensitive = "sensitive";
        public const string Misc = "misc";

        private static AttributeDefinition D(string id, string title, string category, AttributeClass cls, double? halfLife,
            AttributeSensitivity sensitivity = AttributeSensitivity.Normal, bool isPrivate = false, string? lifeEvent = null, string? planKind = null, bool multi = false) =>
            // Sensitivity is accepted for source compatibility but is always Normal: nothing is restricted now.
            new(id, title, category, cls, halfLife, AttributeSensitivity.Normal, isPrivate, lifeEvent, planKind) { MultiValued = multi };

        private const AttributeSensitivity R = AttributeSensitivity.RestrictedForMinors;
        private const AttributeSensitivity S = AttributeSensitivity.SensitiveTrait;

        public static readonly IReadOnlyList<AttributeDefinition> All = new[]
        {
            // Identity
            D("real_name", "Real name", Identity, AttributeClass.Immutable, null, R, isPrivate: true),
            D("full_name", "Full name", Identity, AttributeClass.Immutable, null, R, isPrivate: true),
            D("surname", "Surname", Identity, AttributeClass.Immutable, null, R, isPrivate: true),
            D("nickname", "Nickname", Identity, AttributeClass.State, 730, multi: true),
            D("pronouns", "Pronouns", Identity, AttributeClass.State, 730),
            D("age", "Age statement (policy evidence only)", Identity, AttributeClass.TimeDerived, null, R, isPrivate: true),
            D("birth_date", "Birth date", Identity, AttributeClass.Immutable, null, R, isPrivate: true),
            D("birthday", "Birthday (day and month)", Identity, AttributeClass.Immutable, null, R, isPrivate: true, planKind: "birthday"),
            D("birthplace", "Birthplace", Identity, AttributeClass.Immutable, null, R, isPrivate: true),
            D("nationality", "Nationality", Identity, AttributeClass.State, 3650, multi: true),
            D("first_language", "First language", Identity, AttributeClass.Immutable, null),
            // Location
            D("country", "Country of residence", Location, AttributeClass.State, 540, lifeEvent: "move"),
            D("city", "City", Location, AttributeClass.State, 365, R, isPrivate: true, lifeEvent: "move"),
            D("home_location", "Home location", Location, AttributeClass.State, 365, R, isPrivate: true, lifeEvent: "move"),
            D("neighbourhood", "Neighbourhood", Location, AttributeClass.State, 365, R, isPrivate: true),
            D("visited_place", "Visited a place", Location, AttributeClass.Event, null, lifeEvent: "trip", multi: true),
            // Work
            D("job", "Job", Work, AttributeClass.State, 365, lifeEvent: "job", multi: true),
            D("employer", "Employer", Work, AttributeClass.State, 365, isPrivate: true, lifeEvent: "job", multi: true),
            // Education
            D("school", "School", Education, AttributeClass.State, 365, R, isPrivate: true, lifeEvent: "education"),
            D("college", "College or university", Education, AttributeClass.State, 365, R, isPrivate: true, lifeEvent: "education"),
            D("school_year", "School year or grade", Education, AttributeClass.TimeDerived, null, R, isPrivate: true),
            D("field_of_study", "Field of study", Education, AttributeClass.State, 540, multi: true),
            // Relationships and family
            D("relationship_status", "Relationship status", Relationships, AttributeClass.State, 180, isPrivate: true, lifeEvent: "relationship"),
            D("partner", "Partner", Relationships, AttributeClass.Relation, 180, isPrivate: true, lifeEvent: "relationship"),
            D("ex_partner", "Former partner", Relationships, AttributeClass.Relation, null, isPrivate: true, multi: true),
            D("friend", "Friend", Relationships, AttributeClass.Relation, 365, multi: true),
            D("coworker", "Coworker", Relationships, AttributeClass.Relation, 365, multi: true),
            D("classmate", "Classmate", Relationships, AttributeClass.Relation, 365, R, multi: true),
            D("family_member", "Family member", Family, AttributeClass.Relation, null, R, isPrivate: true, multi: true),
            D("family_member_name", "Family member's name", Family, AttributeClass.Immutable, null, R, isPrivate: true, multi: true),
            D("parent", "Parent", Family, AttributeClass.Relation, null, R, isPrivate: true, multi: true),
            D("sibling", "Sibling", Family, AttributeClass.Relation, null, R, isPrivate: true, multi: true),
            D("child", "Child", Family, AttributeClass.Relation, null, isPrivate: true, multi: true),
            D("pet", "Pet", Possessions, AttributeClass.State, 730, lifeEvent: "pet", multi: true),
            // Interests and preferences
            D("likes", "Likes", Preferences, AttributeClass.Preference, 180, multi: true),
            D("dislikes", "Dislikes", Preferences, AttributeClass.Preference, 180, multi: true),
            D("favourite", "Favourite", Preferences, AttributeClass.Preference, 180, multi: true),
            D("opinion", "Opinion", Preferences, AttributeClass.Preference, 90, multi: true),
            D("wants", "Wants or wishes for", Preferences, AttributeClass.Preference, 120, multi: true),
            D("hobby", "Hobby", Interests, AttributeClass.State, 365, multi: true),
            D("plays_game", "Plays a game", Interests, AttributeClass.State, 180, multi: true),
            D("music_taste", "Music taste", Interests, AttributeClass.Preference, 365, multi: true),
            D("supports_team", "Supports a team", Interests, AttributeClass.State, 730, multi: true),
            D("diet", "Diet", Habits, AttributeClass.State, 365),
            // Possessions and skills
            D("owns", "Owns", Possessions, AttributeClass.State, 540, lifeEvent: "purchase", multi: true),
            D("skill", "Skill or expertise", Skills, AttributeClass.State, 1095, multi: true),
            D("speaks_language", "Speaks a language", Skills, AttributeClass.State, 1825, multi: true),
            // Habits and routine (restricted for minors: routine inference)
            D("daily_routine", "Daily routine", Habits, AttributeClass.Habit, 90, R, isPrivate: true),
            D("sleep_schedule", "Sleep schedule", Habits, AttributeClass.Habit, 90, R, isPrivate: true),
            D("active_hours", "Active hours", Habits, AttributeClass.Habit, 90, R),
            // Contact
            D("contact_handle", "Contact handle", Contact, AttributeClass.State, 730, R, isPrivate: true, multi: true),
            D("phone", "Phone number", Contact, AttributeClass.State, 730, R, isPrivate: true),
            D("email", "Email address", Contact, AttributeClass.State, 730, R, isPrivate: true, multi: true),
            D("social_handle", "Social media handle", Contact, AttributeClass.State, 730, R, multi: true),
            D("alt_account", "Other account", Contact, AttributeClass.State, 1825, multi: true),
            // Events, plans and dates
            D("life_event", "Life event", Events, AttributeClass.Event, null, lifeEvent: "other", multi: true),
            D("achievement", "Achievement", Events, AttributeClass.Event, null, lifeEvent: "achievement", multi: true),
            D("purchase", "Purchase", Events, AttributeClass.Event, null, lifeEvent: "purchase", multi: true),
            D("plan", "Plan", Plans, AttributeClass.Event, null, planKind: "plan", multi: true),
            D("promise", "Promise or commitment", Plans, AttributeClass.Event, null, planKind: "promise", multi: true),
            D("important_date", "Important date", Plans, AttributeClass.Event, null, planKind: "date", multi: true),
            // Special-category traits: only literal self-disclosures, quoted with their date.
            D("health_condition", "Health condition", Sensitive, AttributeClass.State, 365, S, isPrivate: true, multi: true),
            D("mental_health", "Mental health", Sensitive, AttributeClass.State, 365, S, isPrivate: true, multi: true),
            D("disability", "Disability", Sensitive, AttributeClass.State, 3650, S, isPrivate: true, multi: true),
            D("sexual_orientation", "Sexual orientation", Sensitive, AttributeClass.State, 3650, S, isPrivate: true),
            D("gender_identity", "Gender identity", Sensitive, AttributeClass.State, 3650, S, isPrivate: true),
            D("religion", "Religion", Sensitive, AttributeClass.State, 3650, S, isPrivate: true),
            D("ethnicity", "Ethnicity", Sensitive, AttributeClass.Immutable, null, S, isPrivate: true),
            D("race", "Race", Sensitive, AttributeClass.Immutable, null, S, isPrivate: true),
            D("political_affiliation", "Political affiliation", Sensitive, AttributeClass.State, 730, S, isPrivate: true),
            D("sex_life", "Sex life", Sensitive, AttributeClass.State, 180, S, isPrivate: true),
            D("trade_union", "Trade union membership", Sensitive, AttributeClass.State, 1095, S, isPrivate: true),
            // Derived-only anchors and catch-all
            D("birth_interval", "Birth interval (age anchor)", Identity, AttributeClass.TimeDerived, null, R, isPrivate: true),
            D("inquiry_answer", "Inquiry answer", Misc, AttributeClass.Preference, 180, multi: true),
            D("inference", "Inference", Misc, AttributeClass.State, 180, multi: true),
            D(Other, "Other", Misc, AttributeClass.State, 365, multi: true),
        };

        private static readonly Dictionary<string, AttributeDefinition> byId = Build();

        private static Dictionary<string, AttributeDefinition> Build() =>
            All.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);

        public static bool TryGet(string? id, out AttributeDefinition definition)
        {
            definition = byId[Other];
            if (string.IsNullOrWhiteSpace(id)) return false;
            if (!byId.TryGetValue(id.Trim(), out var found)) return false;
            definition = found;
            return true;
        }

        /// <summary>The definition, or "other" for an attribute outside the vocabulary.</summary>
        public static AttributeDefinition Get(string? id) => TryGet(id, out var d) ? d : byId[Other];

        public static IReadOnlyList<string> Ids { get; } = All.Select(a => a.Id).ToList();

        public static IReadOnlyList<string> Categories { get; } = All.Select(a => a.Category).Distinct(StringComparer.Ordinal).ToList();

        /// <summary>Nothing is restricted for any age band.</summary>
        public static bool IsBlockedFor(string attribute, AgeBand band) => false;

        /// <summary>Every stored assertion is shown, whatever its attribute, status or the bands of the people it names.</summary>
        public static bool IsShownFor(string attribute, string status, AgeBand subjectBand, AgeBand? namedBand = null) => true;

        /// <summary>No attribute is special-category any more.</summary>
        public static bool IsSensitive(string attribute) => false;
    }
}
