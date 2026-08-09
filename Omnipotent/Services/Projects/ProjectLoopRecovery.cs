namespace Omnipotent.Services.Projects;

/// <summary>
/// Durable pacing for a wake that exhausted its convergence budget. Automatic schedulers respect
/// the deadline, while direct human and external stimuli deliberately remain free to wake the
/// project: new information can make a previously repeating approach worth reconsidering.
/// Repeated convergence stops back off exponentially across wakes and process restarts.
/// </summary>
internal static class ProjectLoopRecovery
{
    internal static readonly TimeSpan InitialRetryDelay = TimeSpan.FromHours(1);
    internal static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromHours(24);

    public static ProjectResumeAction Create(ProjectResumeAction? previous, string recordedBy,
        string toolName, string summary, DateTime? nowUtc = null)
    {
        DateTime now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        TimeSpan delay = InitialRetryDelay;
        if (previous?.Kind == "loop-recovery")
        {
            TimeSpan priorDelay = InitialRetryDelay;
            if (previous.NotBefore.HasValue && previous.RecordedAt != default)
            {
                priorDelay = previous.NotBefore.Value.ToUniversalTime()
                    - previous.RecordedAt.ToUniversalTime();
                if (priorDelay <= TimeSpan.Zero) priorDelay = InitialRetryDelay;
            }
            long priorTicks = Math.Clamp(priorDelay.Ticks,
                InitialRetryDelay.Ticks, MaximumRetryDelay.Ticks);
            delay = TimeSpan.FromTicks(priorTicks >= MaximumRetryDelay.Ticks / 2
                ? MaximumRetryDelay.Ticks
                : priorTicks * 2);
        }

        return new ProjectResumeAction
        {
            Kind = "loop-recovery",
            RecordedBy = recordedBy,
            ToolName = toolName,
            Summary = summary,
            RecordedAt = now,
            NotBefore = now + delay,
        };
    }

    /// <summary>
    /// Whether a scheduler-generated wake must wait. Older loop-recovery records predate the
    /// explicit NotBefore field; give those the initial delay from their recorded timestamp too.
    /// Explicit deadlines on other resume kinds keep their existing intentional-sleep semantics.
    /// </summary>
    public static bool DefersAutomaticWake(ProjectResumeAction? action, DateTime nowUtc)
    {
        if (action == null) return false;
        DateTime? retryAt = action.NotBefore?.ToUniversalTime();
        if (!retryAt.HasValue && action.Kind == "loop-recovery" && action.RecordedAt != default)
            retryAt = action.RecordedAt.ToUniversalTime() + InitialRetryDelay;
        return retryAt.HasValue && retryAt.Value > nowUtc.ToUniversalTime();
    }

    /// <summary>A productive, loop-free retry has recovered. Clear only the action captured when
    /// the wake began; the store's expected-id fence protects any newer model-authored resume.</summary>
    public static bool ShouldClearAfterProgress(ProjectResumeAction? startingAction,
        bool wakeCompleted, int productiveActions, int loopTrips) =>
        startingAction?.Kind == "loop-recovery"
        && !string.IsNullOrWhiteSpace(startingAction.ActionID)
        && wakeCompleted
        && productiveActions > 0
        && loopTrips == 0;
}
