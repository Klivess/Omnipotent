namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Keeps a productive Commander working between wakes.
    ///
    /// A Commander that ends a wake is normally not woken again until something pushes it — a worker's
    /// report, Klives, a hook — or the 14-minute keepalive. That is right when it ended because the work
    /// is waiting on someone else. It is wrong when it simply stopped after making progress with open
    /// steps left and nobody else working on them: the project then sits idle for a quarter of an hour,
    /// and the wake that finally comes pays a full cold restart because the prompt prefix died long before.
    ///
    /// So a wake that made verified progress, while the step ledger still has open work, no worker is
    /// awake, nothing waits on Klives' approval and no hook was armed, queues one short-fuse follow-up.
    /// It runs inside the prefix lifetime, so it continues the warm session cheaply. It chains only while
    /// each wake keeps producing verified progress: an unproductive follow-up schedules nothing further
    /// and the project falls back to its ordinary triggers and keepalive.
    /// </summary>
    public static class ProjectMomentum
    {
        public const string TriggerPrefix = "Momentum:";

        /// <summary>Short enough to land inside the provider's prefix lifetime; long enough that a worker's
        /// report or a Klives message that is already on its way wins the next wake instead.</summary>
        public static readonly TimeSpan Delay = TimeSpan.FromSeconds(45);

        /// <summary>A follow-up that could not start within this window (capacity, backoff) is dropped;
        /// by then the keepalive is the better judge.</summary>
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

        public const string TriggerText = TriggerPrefix + " your last wake made verified progress and the step ledger still has open work " +
            "that nobody else is on. Pick up the next concrete step now. If everything open is genuinely waiting on someone else or on a " +
            "future event, say so in one line and end the wake.";

        public static bool ShouldContinue(bool enabled, ProjectStatus? status, bool hasOpenSteps, bool workersAwake,
            int pendingApprovals, bool armedHook) =>
            enabled
            && status == ProjectStatus.Active
            && hasOpenSteps
            && !workersAwake
            && pendingApprovals == 0
            && !armedHook;

        public static ProjectWakeTrigger CreateTrigger(DateTime nowUtc) => new()
        {
            Kind = ProjectWakeTriggerKind.Continuation,
            Payload = TriggerText,
            NotBefore = nowUtc + Delay,
            ExpiresAt = nowUtc + Window,
            Priority = 20,
            // One pending follow-up per project: a newer one replaces an unclaimed older one.
            CoalescingKey = "momentum",
            Durable = false,
            DiscardWhenInapplicable = true,
        };
    }

    /// <summary>Wording for the timeline notes that explain why an agent is not working.</summary>
    public static class ProjectWakeWaiting
    {
        /// <summary>How many minutes of silence a repeated note for the same reason is allowed to cover.</summary>
        public static readonly TimeSpan RepeatAfter = TimeSpan.FromMinutes(15);

        public static string Until(DateTime retryAtUtc, DateTime nowUtc)
        {
            var wait = retryAtUtc - nowUtc;
            if (wait <= TimeSpan.FromSeconds(30)) return "in a moment";
            if (wait < TimeSpan.FromMinutes(1.5)) return "in about a minute";
            if (wait < TimeSpan.FromHours(1.5)) return $"in about {Math.Round(wait.TotalMinutes):0} minutes";
            return $"in about {wait.TotalHours:0.#} hours";
        }

        public static string AgentLabel(string agentID) =>
            string.Equals(agentID, "commander", StringComparison.OrdinalIgnoreCase) ? "The Commander" : $"Agent {agentID}";
    }
}
