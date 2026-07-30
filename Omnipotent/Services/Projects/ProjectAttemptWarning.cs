namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Turns the durable attempt ledger into a message the acting agent reads at the moment it repeats
    /// itself, appended to the tool result.
    ///
    /// This deliberately never blocks a call. An intent that failed three times can still be the right
    /// thing to try when an external condition has changed, and a project that refuses its own tools is
    /// worse than one that wastes a turn. The hard stop stays where it was: the in-wake convergence guard,
    /// which trips on byte-identical calls and ends the wake after its trip budget. This layer's job is to
    /// make cross-wake repetition impossible to be unaware of.
    /// </summary>
    public static class ProjectAttemptWarning
    {
        /// <summary>Failure count at which the wording stops being informative and starts being blunt.</summary>
        private const int EscalateAt = 4;

        /// <summary>
        /// Whether the agent should be told about the prior attempts. Requires a prior record that failed
        /// repeatedly, this attempt to have failed too, and the tool not to be one whose whole purpose is
        /// repetition (polling a mailbox, re-reading a screen, waiting for a postcondition).
        /// </summary>
        public static bool ShouldWarn(ProjectAttempt? prior, bool succeeded, string toolName)
        {
            if (prior == null || succeeded) return false;
            if (ProjectAttemptKey.IsDeliberatelyRepeatable(toolName)) return false;
            return prior.IsRepeatedFailure(ProjectRuntimeStateStore.RepeatedFailureThreshold);
        }

        /// <summary>
        /// The warning text. <paramref name="prior"/> is the ledger row as it stood BEFORE the attempt that
        /// just failed, so the count is stated inclusive of this one.
        /// </summary>
        public static string Render(ProjectAttempt prior)
        {
            int failures = prior.FailureCount + 1;
            string opener = failures >= EscalateAt
                ? $"REPEAT ({failures}× FAILED) — stop attempting this."
                : $"REPEAT — you have attempted this same intent {failures} times now and it has failed every time.";
            return opener +
                $" First tried {Data_Handling.TemporalFormat.StampMinute(prior.FirstAt)}," +
                $" last {Data_Handling.TemporalFormat.StampWithAge(prior.LastAt)} → \"{prior.LastOutcome}\"." +
                " Identical inputs will keep producing this result. Either use a materially different" +
                " approach, or record it with checkpoint op:record_dead_end and move to the next step." +
                " Do not repeat it again without new information about what changed.";
        }
    }
}
