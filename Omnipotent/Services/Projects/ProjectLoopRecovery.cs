namespace Omnipotent.Services.Projects;

/// <summary>
/// Durable pacing for a wake that exhausted its convergence budget. Automatic schedulers respect
/// the deadline, while direct human and external stimuli deliberately remain free to wake the
/// project: new information can make a previously repeating approach worth reconsidering.
/// Repeated convergence stops back off exponentially across wakes and process restarts.
/// </summary>
internal static class ProjectLoopRecovery
{
    internal static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromHours(1);

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
            Summary = summary + " Resume the current unfinished step using a different evidence-backed approach. "
                + "Keep completed work, account IDs and committed artifacts; do not restart the goal or replay the failed action unchanged.",
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

    /// <summary>Dispatch one wake at a durable deadline rather than waiting for the idle heartbeat.
    /// A previous dispatch after this deadline prevents a 15-second loop on an unchanged resume.</summary>
    public static bool RetryDue(ProjectResumeAction? action, DateTime nowUtc, DateTime? lastWakeAt)
    {
        DateTime? deadline = action?.NotBefore;
        if (!deadline.HasValue && action?.Kind == "loop-recovery" && action.RecordedAt != default)
            deadline = action.RecordedAt + InitialRetryDelay;
        return deadline.HasValue && deadline <= nowUtc && (!lastWakeAt.HasValue || lastWakeAt < deadline);
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
