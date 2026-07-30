using System.Text.RegularExpressions;

namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Decides whether something Klives typed is a STANDING CONSTRAINT rather than a one-off request.
    ///
    /// The distinction matters because the two kinds have opposite lifecycles. A Task is finished when its
    /// deliverable exists. A Steering message is finished when the Commander answers it — and an answered
    /// steer used to vanish from every future wake seed. So "keep digging, don't complete yet", sent as
    /// ordinary chat, was acknowledged in prose and then forgotten, and the next wake re-proposed exactly
    /// the thing it had been told not to do. A constraint has no deliverable and is never "answered": it
    /// holds until Klives replaces or revokes it, which is what a Rule is.
    ///
    /// Deliberately narrow. A false positive creates a standing rule Klives has to go and revoke, so the
    /// patterns only fire on unambiguous constraint grammar, and anything that names a deliverable is
    /// treated as work instead.
    /// </summary>
    public static class ProjectDirectiveClassifier
    {
        /// <summary>
        /// Constraint grammar: prohibitions, standing obligations, and "carry on until I say" instructions.
        /// Anchored to a clause start so "I don't think the report is ready" is not read as a prohibition.
        /// </summary>
        private static readonly Regex[] ConstraintPatterns =
        {
            // Prohibitions.
            new(@"(^|[.;!?]\s+|\band\s+|\bbut\s+|,\s*)(please\s+)?(don'?t|do not|never|stop|no more|avoid|refrain from|must not|cannot|can'?t)\s+\w",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),
            // Standing obligations.
            new(@"(^|[.;!?]\s+|\band\s+|,\s*)(always|from now on|going forward|in future|every time|whenever)\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),
            // "keep going until I say" — an instruction to continue, with no deliverable of its own.
            new(@"\b(keep|carry on|continue)\s+(\w+ing\b|going\b|at it\b|on\b)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new(@"\buntil\s+(i|klive|klives)\s+(say|tell|ask|decide|confirm)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),
        };

        /// <summary>
        /// Requests for a concrete artefact. These are work with a definition of done, so they stay Tasks
        /// even when they also contain constraint-shaped words ("write the report, don't use LLM prose").
        /// </summary>
        private static readonly Regex DeliverablePattern = new(
            @"\b(send|give|show|write|make|build|create|produce|draft|compile|generate|deliver|publish|upload|email)\s+(me\b|us\b|a\b|an\b|the\b|it\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Returns <see cref="ProjectDirectiveKind.Rule"/> when the text is a standing constraint that must
        /// outlive being acknowledged, or null to leave the caller's classification alone.
        /// </summary>
        public static ProjectDirectiveKind? ClassifyStandingConstraint(string? text)
        {
            string value = (text ?? "").Trim();
            if (value.Length == 0) return null;
            // A rule has to fit the always-injected prompt block; long prose is a briefing, not a rule.
            if (value.Length > ProjectDirectiveStore.MaxRuleLength) return null;
            if (DeliverablePattern.IsMatch(value)) return null;
            return ConstraintPatterns.Any(p => p.IsMatch(value)) ? ProjectDirectiveKind.Rule : null;
        }
    }
}
