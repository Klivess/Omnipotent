namespace Omnipotent.Services.OmniscienceV3.Knowledge.Policy
{
    /// <summary>
    /// Special-category traits are never inferred (plan Guardrails): sexual orientation, health conditions,
    /// religion, ethnicity, political affiliation and sex life. An inquiry or the detective pass may only
    /// report what the person explicitly and literally said about themselves, quoted with its date;
    /// otherwise the answer is "Omniscience doesn't infer this".
    /// </summary>
    public static class SensitiveTraits
    {
        public static readonly IReadOnlySet<string> Categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sexual_orientation", "gender_identity", "health_condition", "mental_health", "disability",
            "religion", "ethnicity", "race", "political_affiliation", "sex_life", "trade_union",
        };

        public const string RefusalText = "Omniscience doesn't infer this. It only reports what a person explicitly said about themselves.";

        public static bool IsSensitive(string attributeOrCategory) => Categories.Contains(attributeOrCategory);

        /// <summary>
        /// A sensitive assertion may be kept only when it is the person's own literal statement about
        /// themselves (source kind "self_disclosure", modality "literal"). Inferred, third-party, joking or
        /// hypothetical claims are rejected.
        /// </summary>
        public static bool AllowsAssertion(string attribute, string? sourceKind, string? modality) =>
            !IsSensitive(attribute)
            || (string.Equals(sourceKind, "self_disclosure", StringComparison.OrdinalIgnoreCase)
                && string.Equals(modality, "literal", StringComparison.OrdinalIgnoreCase));

        /// <summary>Inquiries into a sensitive category are refused unless the caller only asks for quotes.</summary>
        public static bool AllowsInference(string category) => !IsSensitive(category);
    }
}
