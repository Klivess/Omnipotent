namespace Omnipotent.Services.Projects;

/// <summary>
/// Fleet-wide admission at the WAKE BOUNDARY: how many agent conversations may be live at once,
/// given that only three of them can be talking to AIRouter at any instant.
///
/// This is the admission point the AIRouter scheduler documents and cannot implement. By the time a
/// request reaches the shared queue its prompt has already been assembled as a continuation, so
/// refusing it there cannot make it cheap — it only makes it miss later, which simulation put at 37%
/// prefix efficiency against a 99% target. A wake that has not started yet has no prompt at all, so
/// holding it back costs nothing and is the only free admission control in the system.
///
/// WHY IT HAS TO EXIST. The fleet has two stable states, and the difference between them is not
/// small. Measured over the live journal: a turn served from cache occupies a slot for 3–17 seconds,
/// the same turn re-prefilled occupies it for 90–120. So three slots serve either ~30 warm turns a
/// minute or ~1.5 cold ones. Once enough conversations are live that the round trip between one
/// conversation's turns exceeds the provider's prefix lifetime, they all go cold, throughput falls by
/// an order of magnitude, the round trip gets longer still, and nothing recovers on its own — the
/// same positive feedback loop that got the router key suspended in August. On 2026-09-17 the
/// journal shows the collapsed state directly: queue waits at the 90th percentile of 447s against a
/// ~300s prefix lifetime, and 79% of all expired prefixes belonging to turns that were READY inside
/// the lifetime and spent it queueing.
///
/// Fewer live conversations therefore complete MORE work, which is the counter-intuitive part and
/// the whole point: the budget is not a throttle bolted on to protect a bill, it is what keeps the
/// fleet in the fast state.
/// </summary>
public sealed class ProjectWakeAdmission
{
    /// <summary>
    /// The cap to open with when there is no measurement to size one — a fresh process, or the first
    /// minutes after a fleet unhalt. Deliberately small: those are exactly the moments when every
    /// conversation is cold, service time is at its worst, and admitting the whole fleet at once
    /// reproduces the collapse the cap exists to prevent. <see cref="RampStep"/> grows it from here.
    /// </summary>
    internal const int WarmRestartFloor = 3;

    /// <summary>How long one extra conversation must prove itself before the next is admitted while
    /// the survival curve is still guessing. Long enough for a cold conversation to take a turn, miss,
    /// re-prefill and come back warm.</summary>
    internal static readonly TimeSpan RampStep = TimeSpan.FromMinutes(3);

    /// <summary>
    /// A wake stops counting against the budget once it has gone this long without a model turn.
    ///
    /// A conversation waiting on a 26-minute browser call is not competing for a slot, and holding a
    /// permit for it would freeze the fleet behind work that is not using the thing being rationed.
    /// Its own prefix is already dead by then, which the re-seed path now handles for free, so
    /// letting somebody else in costs it nothing it had left.
    /// </summary>
    internal static readonly TimeSpan ActivityWindow = TimeSpan.FromMinutes(6);

    /// <summary>Bound on retained deferrals. Keyed per agent, so this is only ever reached by a fleet
    /// far larger than anything three slots could serve.</summary>
    private const int MaxDeferred = 512;

    public const string CommanderAgentID = "commander";

    /// <summary>One deferred wake. The trigger is retained so the resume is the wake that was asked
    /// for, not a generic nudge that loses why it was woken.</summary>
    public readonly record struct DeferredWake(string ProjectID, string AgentID, string Trigger, DateTime QueuedAt)
    {
        public bool IsCommander => string.Equals(AgentID, CommanderAgentID, StringComparison.Ordinal);
    }

    public readonly record struct AdmissionSnapshot(
        bool Enabled,
        int Live,
        int Active,
        int Budget,
        int MeasuredBudget,
        int ConfiguredCap,
        int Fallback,
        int Deferred,
        string Source,
        DateTime? RampStartedAt,
        IReadOnlyList<string> LiveKeys,
        IReadOnlyList<DeferredWake> DeferredWakes);

    private sealed class LiveWake
    {
        internal string ProjectID = "";
        internal string AgentID = "";
        internal DateTime AdmittedAt;
        internal DateTime LastTurnAt;
        internal int PendingTurns;
    }

    private readonly object sync = new();
    private readonly Dictionary<string, LiveWake> live = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeferredWake> deferred = new(StringComparer.Ordinal);
    private readonly Func<DateTime> nowUtc;
    private readonly Func<int> measuredBudget;

    private bool enabled = true;
    private int configuredCap;      // 0 = derive from the measurement
    private int fallback = 6;
    private DateTime rampStartedAt;

    public ProjectWakeAdmission(Func<int>? measuredBudget = null, Func<DateTime>? nowUtc = null)
    {
        this.nowUtc = nowUtc ?? (() => DateTime.UtcNow);
        this.measuredBudget = measuredBudget ?? KliveLLM.KliveLLM.AIRouterConversationBudget;
        rampStartedAt = this.nowUtc();
    }

    public static string Key(string projectID, string agentID) => $"{projectID}/{agentID}";

    public void Configure(bool? isEnabled = null, int? cap = null, int? fallbackCap = null)
    {
        lock (sync)
        {
            if (isEnabled.HasValue) enabled = isEnabled.Value;
            if (cap.HasValue) configuredCap = Math.Clamp(cap.Value, 0, 128);
            if (fallbackCap.HasValue) fallback = Math.Clamp(fallbackCap.Value, 1, 128);
        }
    }

    /// <summary>
    /// Restart the ramp. Called when the fleet resumes en masse — a process start, or a cache-halt
    /// being cleared — because that is precisely when every conversation is cold and releasing all of
    /// them at once is what re-collapses the fleet. The 2026-09-17 journal shows the shape: the
    /// window after the halt was cleared reads 51.6% cached with a 74s median provider time against
    /// the previous day's 90.5% and 17s.
    /// </summary>
    public void BeginWarmRestart()
    {
        lock (sync) { rampStartedAt = nowUtc(); }
    }

    /// <summary>The number of conversations allowed to be live right now, and where that came from.</summary>
    public (int Budget, string Source) Budget()
    {
        lock (sync) { return BudgetLocked(nowUtc()); }
    }

    private (int Budget, string Source) BudgetLocked(DateTime now)
    {
        int target;
        string source;
        if (configuredCap > 0) { target = configuredCap; source = "configured"; }
        else
        {
            int measured = 0;
            try { measured = measuredBudget(); } catch { measured = 0; }
            if (measured > 0) { target = measured; source = "measured"; }
            else { target = fallback; source = "fallback"; }
        }

        // The ramp is a CEILING over whatever the steady-state answer is, not just a stand-in for a
        // missing measurement. A fleet coming back from a restart or an unhalt is entirely cold
        // whatever the curve remembers — and the curve does remember, because its sample count never
        // decays, so a measurement taken before an eleven-hour halt would otherwise wave the whole
        // fleet back in at once. Widening one conversation at a time lets them re-warm in sequence.
        long steps = (long)((now - rampStartedAt).TotalMilliseconds / RampStep.TotalMilliseconds);
        int ramped = (int)Math.Clamp(WarmRestartFloor + steps, WarmRestartFloor, Math.Max(WarmRestartFloor, target));
        return ramped < target ? (ramped, "ramp") : (target, source);
    }

    /// <summary>Live wakes that are still taking model turns. A wake parked in a long tool call is
    /// live but not competing for a slot, so it does not hold a permit against the budget.</summary>
    private int ActiveLocked(DateTime now)
    {
        int active = 0;
        foreach (LiveWake wake in live.Values)
            if (wake.PendingTurns > 0 || now - wake.LastTurnAt <= ActivityWindow) active++;
        return active;
    }

    /// <summary>
    /// May this agent start a wake now? A wake already in flight for the same agent always may —
    /// this gate is about STARTING conversations, never about interrupting one.
    /// </summary>
    /// <param name="mustAdmit">
    /// A human is blocked on this one. Klives sending a message is a handful of turns against a fleet
    /// that runs for hours, so its slot-seconds are noise — but making him wait behind the budget for
    /// a reply is not, and the AIRouter scheduler makes the same exemption one layer down for exactly
    /// the same reason. Going one over the budget for a message costs a fraction of what it protects.
    /// </param>
    public bool TryAdmit(string projectID, string agentID, string trigger, out string reason,
        bool mustAdmit = false)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(projectID) || string.IsNullOrWhiteSpace(agentID)) return true;
        string key = Key(projectID, agentID);
        lock (sync)
        {
            if (!enabled || mustAdmit)
            {
                deferred.Remove(key);
                if (!live.ContainsKey(key)) live[key] = NewWake(projectID, agentID);
                else live[key].LastTurnAt = nowUtc();
                return true;
            }
            DateTime now = nowUtc();
            if (live.ContainsKey(key)) { live[key].LastTurnAt = now; return true; }

            var (budget, source) = BudgetLocked(now);
            int active = ActiveLocked(now);
            if (active < budget)
            {
                deferred.Remove(key);
                live[key] = NewWake(projectID, agentID);
                return true;
            }

            reason = $"{active} of {budget} live conversations ({source}); "
                   + "starting another would push consecutive turns past the prompt-cache lifetime";
            Defer(key, projectID, agentID, trigger, now);
            return false;
        }
    }

    private LiveWake NewWake(string projectID, string agentID)
    {
        DateTime now = nowUtc();
        return new LiveWake { ProjectID = projectID, AgentID = agentID, AdmittedAt = now, LastTurnAt = now };
    }

    private void Defer(string key, string projectID, string agentID, string trigger, DateTime now)
    {
        // A re-deferral keeps its ORIGINAL queue time: the wait is what earns the next free permit,
        // and restarting the clock on every retry would let a busy project starve a quiet one forever.
        DateTime queuedAt = deferred.TryGetValue(key, out DeferredWake existing) ? existing.QueuedAt : now;
        if (!deferred.ContainsKey(key) && deferred.Count >= MaxDeferred) return;
        deferred[key] = new DeferredWake(projectID, agentID, trigger, queuedAt);
    }

    /// <summary>Record a model turn, so a conversation in a long tool call stops holding a permit it
    /// is not using. Cheap enough to call on every turn.</summary>
    public void NoteTurn(string projectID, string agentID)
    {
        string key = Key(projectID, agentID);
        lock (sync) { if (live.TryGetValue(key, out LiveWake? wake)) wake.LastTurnAt = nowUtc(); }
    }

    /// <summary>Keep admission occupied throughout queueing, inference and retries. A slow model
    /// request is still competing for capacity; only time spent outside the model call may idle out.</summary>
    public IDisposable BeginModelTurn(string projectID, string agentID)
    {
        lock (sync)
        {
            live.TryGetValue(Key(projectID, agentID), out LiveWake? wake);
            if (wake != null)
            {
                wake.PendingTurns++;
                wake.LastTurnAt = nowUtc();
            }
            return new ModelTurn(this, wake);
        }
    }

    private sealed class ModelTurn(ProjectWakeAdmission owner, LiveWake? wake) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0 || wake == null) return;
            lock (owner.sync)
            {
                wake.PendingTurns--;
                wake.LastTurnAt = owner.nowUtc();
            }
        }
    }

    /// <summary>Whether this agent is waiting for a permit. The watchdog needs it for the same reason
    /// it needs the AIRouter queue probe: a deferred wake is a healthy agent waiting its turn, and
    /// diagnosing it as a stall would manufacture a stall out of correct behaviour.</summary>
    public bool IsDeferred(string projectID, string? agentID = null)
    {
        lock (sync)
        {
            if (agentID != null) return deferred.ContainsKey(Key(projectID, agentID));
            string prefix = projectID + "/";
            foreach (string key in deferred.Keys)
                if (key.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }
    }

    /// <summary>
    /// Give up a permit and return the wakes that should start in its place, best first.
    ///
    /// Ordering is fairness, not priority for its own sake: a project with nothing live outranks one
    /// that already holds a conversation, so a single busy project cannot own the whole budget, and
    /// within that it is longest-waiting first so nothing can be overtaken indefinitely.
    /// </summary>
    public IReadOnlyList<DeferredWake> Release(string projectID, string agentID)
    {
        lock (sync)
        {
            live.Remove(Key(projectID, agentID));
            return PromoteLocked();
        }
    }

    /// <summary>Re-check the queue without releasing anything — for the periodic sweep that covers a
    /// permit freed by the activity window rather than by a wake ending.</summary>
    public IReadOnlyList<DeferredWake> Promote()
    {
        lock (sync) { return PromoteLocked(); }
    }

    private IReadOnlyList<DeferredWake> PromoteLocked()
    {
        if (!enabled || deferred.Count == 0) return Array.Empty<DeferredWake>();
        DateTime now = nowUtc();
        var (budget, _) = BudgetLocked(now);
        int room = budget - ActiveLocked(now);
        if (room <= 0) return Array.Empty<DeferredWake>();

        var busy = new HashSet<string>(live.Values.Select(wake => wake.ProjectID), StringComparer.Ordinal);
        var promoted = new List<DeferredWake>();
        foreach (DeferredWake candidate in deferred.Values
            .OrderBy(wake => busy.Contains(wake.ProjectID) ? 1 : 0)
            .ThenBy(wake => wake.IsCommander ? 0 : 1)
            .ThenBy(wake => wake.QueuedAt)
            .ToList())
        {
            if (room <= 0) break;
            // The permit is NOT taken here. The resume goes back through TryAdmit, which is the one
            // place a permit is granted — two paths into that would be two chances to get it wrong.
            deferred.Remove(Key(candidate.ProjectID, candidate.AgentID));
            busy.Add(candidate.ProjectID);
            promoted.Add(candidate);
            room--;
        }
        return promoted;
    }

    /// <summary>Forget a project entirely — halted, paused, archived or deleted. Its deferrals must
    /// not keep being resumed, and its live entries must not keep holding permits.</summary>
    public void Forget(string projectID)
    {
        string prefix = projectID + "/";
        lock (sync)
        {
            foreach (string key in live.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                live.Remove(key);
            foreach (string key in deferred.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                deferred.Remove(key);
        }
    }

    public AdmissionSnapshot Describe()
    {
        lock (sync)
        {
            DateTime now = nowUtc();
            var (budget, source) = BudgetLocked(now);
            int measured = 0;
            try { measured = measuredBudget(); } catch { measured = 0; }
            return new AdmissionSnapshot(
                Enabled: enabled,
                Live: live.Count,
                Active: ActiveLocked(now),
                Budget: budget,
                MeasuredBudget: measured,
                ConfiguredCap: configuredCap,
                Fallback: fallback,
                Deferred: deferred.Count,
                Source: source,
                RampStartedAt: source == "ramp" ? rampStartedAt : null,
                LiveKeys: live.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
                DeferredWakes: deferred.Values.OrderBy(wake => wake.QueuedAt).ToList());
        }
    }
}
