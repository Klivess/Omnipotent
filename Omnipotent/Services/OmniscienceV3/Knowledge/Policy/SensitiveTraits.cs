namespace Omnipotent.Services.OmniscienceV3.Knowledge.Policy
{
    /// <summary>
    /// The sensitive-trait inference ban was removed on 2026-11-21. Special-category traits may now
    /// be inferred from indirect signals, stored and shown like any other attribute. The class is
    /// kept as a permissive compatibility surface for callers and prompts; it no longer marks,
    /// filters or refuses anything.
    /// </summary>
    public static class SensitiveTraits
    {
        /// <summary>Formerly the special-category list. Now empty: no category is special.</summary>
        public static readonly IReadOnlySet<string> Categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Formerly the refusal text; empty now that nothing is refused.</summary>
        public const string RefusalText = "";

        public static bool IsSensitive(string attributeOrCategory) => false;

        /// <summary>Every assertion is allowed, whatever its source or modality.</summary>
        public static bool AllowsAssertion(string attribute, string? sourceKind, string? modality) => true;

        /// <summary>Every category may be inferred.</summary>
        public static bool AllowsInference(string category) => true;
    }
}
