using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects;

/// <summary>
/// The wake-boundary budget. These pin the three things it exists to guarantee: that the number of
/// live conversations is bounded by what three AIRouter slots can keep cache-warm, that a held-back
/// wake is retained and started later rather than dropped, and that no project can be starved by
/// another that keeps rolling into continuations.
/// </summary>
public class ProjectWakeAdmissionTests
{
    private static readonly DateTime T0 = new(2026, 9, 18, 9, 0, 0, DateTimeKind.Utc);

    private sealed class Clock
    {
        internal DateTime Now = T0;
        internal DateTime Get() => Now;
        internal void Advance(TimeSpan by) => Now += by;
    }

    private static (ProjectWakeAdmission Gate, Clock Clock) Build(int measured, int cap = 0, int fallback = 6)
    {
        var clock = new Clock();
        var gate = new ProjectWakeAdmission(() => measured, clock.Get);
        gate.Configure(isEnabled: true, cap: cap, fallbackCap: fallback);
        return (gate, clock);
    }

    [Fact]
    public void QueuedOrRunningModelTurnsDoNotReleaseCapacityAfterTheActivityWindow()
    {
        var (gate, clock) = Build(measured: 1);
        Assert.True(gate.TryAdmit("p1", "commander", "go", out _));
        using (gate.BeginModelTurn("p1", "commander"))
        {
            clock.Advance(ProjectWakeAdmission.ActivityWindow + TimeSpan.FromMinutes(5));
            Assert.Equal(1, gate.Describe().Active);
            Assert.False(gate.TryAdmit("p2", "commander", "wait", out _));
            Assert.Empty(gate.Promote());
        }

        // Completion starts the tool/idle window; it does not immediately abandon a warm prefix.
        Assert.Equal(1, gate.Describe().Active);
        clock.Advance(ProjectWakeAdmission.ActivityWindow + TimeSpan.FromSeconds(1));
        Assert.Equal("p2", Assert.Single(gate.Promote()).ProjectID);
    }

    [Fact]
    public void ModelTurnScopeReleasesOnFailureAndCannotChangeAReplacementWake()
    {
        var (gate, clock) = Build(measured: 1);
        Assert.True(gate.TryAdmit("p1", "commander", "go", out _));
        var oldTurn = gate.BeginModelTurn("p1", "commander");
        gate.Forget("p1");
        Assert.True(gate.TryAdmit("p1", "commander", "restart", out _));
        clock.Advance(ProjectWakeAdmission.ActivityWindow + TimeSpan.FromSeconds(1));
        oldTurn.Dispose();
        oldTurn.Dispose();
        Assert.Equal(0, gate.Describe().Active);

        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var turn = gate.BeginModelTurn("p1", "commander");
            throw new InvalidOperationException("provider failed");
        }));
        clock.Advance(ProjectWakeAdmission.ActivityWindow + TimeSpan.FromSeconds(1));
        Assert.Equal(0, gate.Describe().Active);
    }

    [Fact]
    public void Budget_PrefersTheMeasurement_AndAConfiguredCapOverridesIt()
    {
        // Past the opening ramp, so this is testing where the steady-state number comes from.
        var (measuredGate, measuredClock) = Build(measured: 5);
        measuredClock.Advance(TimeSpan.FromHours(1));
        Assert.Equal((5, "measured"), measuredGate.Budget());

        var (pinned, pinnedClock) = Build(measured: 5, cap: 2);
        pinnedClock.Advance(TimeSpan.FromHours(1));
        Assert.Equal((2, "configured"), pinned.Budget());
    }

    /// <summary>
    /// The ramp is a ceiling over the measurement, not a stand-in for a missing one. A fleet coming
    /// back from a long halt is entirely cold whatever the curve remembers — and the curve DOES
    /// remember, because its sample count never decays — so a pre-halt measurement would otherwise
    /// wave the whole fleet back in at once, which is exactly how the 2026-09-17 restore went.
    /// </summary>
    [Fact]
    public void AWarmRestartCapsEvenAConfidentMeasurement()
    {
        var (gate, clock) = Build(measured: 12);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal((12, "measured"), gate.Budget());

        gate.BeginWarmRestart();
        Assert.Equal((ProjectWakeAdmission.WarmRestartFloor, "ramp"), gate.Budget());
        clock.Advance(ProjectWakeAdmission.RampStep);
        Assert.Equal((ProjectWakeAdmission.WarmRestartFloor + 1, "ramp"), gate.Budget());
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal((12, "measured"), gate.Budget());
    }

    [Fact]
    public void WithNoMeasurement_TheFleetRampsFromTheFloorInsteadOfOpeningAtOnce()
    {
        // The unhalt stampede in one test: no curve yet means every conversation is cold, and the
        // cold service time is what collapses the fleet. Opening at the fallback would reproduce it.
        var (gate, clock) = Build(measured: 0, fallback: 6);
        Assert.Equal((ProjectWakeAdmission.WarmRestartFloor, "ramp"), gate.Budget());

        clock.Advance(ProjectWakeAdmission.RampStep);
        Assert.Equal((ProjectWakeAdmission.WarmRestartFloor + 1, "ramp"), gate.Budget());

        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal((6, "fallback"), gate.Budget());

        // Clearing a halt restarts the ramp: the fleet is cold again whatever it had learned before.
        gate.BeginWarmRestart();
        Assert.Equal((ProjectWakeAdmission.WarmRestartFloor, "ramp"), gate.Budget());
    }

    [Fact]
    public void AdmitsUpToTheBudget_ThenDefersAndRetainsTheTrigger()
    {
        var (gate, _) = Build(measured: 2);
        Assert.True(gate.TryAdmit("p1", "commander", "go", out _));
        Assert.True(gate.TryAdmit("p2", "commander", "go", out _));

        Assert.False(gate.TryAdmit("p3", "commander", "third project", out string reason));
        Assert.Contains("prompt-cache lifetime", reason);
        Assert.True(gate.IsDeferred("p3"));
        Assert.True(gate.IsDeferred("p3", "commander"));
        Assert.False(gate.IsDeferred("p1"));

        var promoted = gate.Release("p1", "commander");
        var resumed = Assert.Single(promoted);
        Assert.Equal("p3", resumed.ProjectID);
        Assert.Equal("third project", resumed.Trigger);   // the wake that was asked for, not a nudge
        Assert.False(gate.IsDeferred("p3"));
    }

    [Fact]
    public void AWakeAlreadyInFlightIsNeverRefused()
    {
        // The gate decides which conversations START. Refusing a turn inside a live wake is exactly
        // the thing that cannot help: its prompt is already assembled as a continuation.
        var (gate, _) = Build(measured: 1);
        Assert.True(gate.TryAdmit("p1", "commander", "go", out _));
        Assert.False(gate.TryAdmit("p2", "commander", "go", out _));
        Assert.True(gate.TryAdmit("p1", "commander", "next slice", out _));
    }

    [Fact]
    public void AConversationInALongToolCallStopsHoldingItsPermit()
    {
        var (gate, clock) = Build(measured: 1);
        Assert.True(gate.TryAdmit("p1", "commander", "go", out _));
        Assert.False(gate.TryAdmit("p2", "commander", "go", out _));

        // Not a model turn in sight: it is waiting on a browser, not competing for a slot — and its
        // own prefix is dead by now, so letting someone else in costs it nothing it had left.
        clock.Advance(ProjectWakeAdmission.ActivityWindow + TimeSpan.FromMinutes(1));
        var promoted = gate.Promote();
        Assert.Equal("p2", Assert.Single(promoted).ProjectID);

        // ...and a turn brings it back into the count.
        gate.NoteTurn("p1", "commander");
        Assert.Empty(gate.Promote());
    }

    [Fact]
    public void APromotionPrefersAProjectWithNothingLive_ThenTheLongestWait()
    {
        var (gate, clock) = Build(measured: 2);
        Assert.True(gate.TryAdmit("busy", "commander", "go", out _));
        Assert.True(gate.TryAdmit("other", "commander", "go", out _));

        Assert.False(gate.TryAdmit("busy", "worker-1", "busy project wants a second conversation", out _));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False(gate.TryAdmit("quiet", "commander", "quiet project has nothing running", out _));

        // "busy" has waited longer, but it already holds a conversation and "quiet" holds none.
        var promoted = gate.Release("other", "commander");
        Assert.Equal("quiet", Assert.Single(promoted).ProjectID);
    }

    [Fact]
    public void ARedeferralKeepsItsOriginalPlaceInTheQueue()
    {
        var (gate, clock) = Build(measured: 1);
        Assert.True(gate.TryAdmit("p1", "commander", "go", out _));
        Assert.False(gate.TryAdmit("early", "commander", "first refusal", out _));
        clock.Advance(TimeSpan.FromMinutes(5));
        gate.NoteTurn("p1", "commander");   // p1 is working, so it keeps its permit
        Assert.False(gate.TryAdmit("late", "commander", "later refusal", out _));

        // The keepalive retries constantly. If a retry reset the wait, a project whose keepalive
        // fires often would keep overtaking one whose does not.
        clock.Advance(TimeSpan.FromMinutes(5));
        gate.NoteTurn("p1", "commander");
        Assert.False(gate.TryAdmit("early", "commander", "retry", out _));

        var promoted = gate.Release("p1", "commander");
        Assert.Equal("early", Assert.Single(promoted).ProjectID);
    }

    [Fact]
    public void HaltingAProjectFreesItsPermitsAndDropsItsDeferrals()
    {
        var (gate, _) = Build(measured: 1);
        Assert.True(gate.TryAdmit("halted", "commander", "go", out _));
        Assert.False(gate.TryAdmit("live", "commander", "go", out _));

        gate.Forget("halted");
        Assert.Equal(0, gate.Describe().Live);
        var promoted = gate.Promote();
        Assert.Equal("live", Assert.Single(promoted).ProjectID);

        Assert.True(gate.TryAdmit("live", "commander", "go", out _));
        Assert.False(gate.TryAdmit("waiting", "commander", "go", out _));
        gate.Forget("waiting");
        Assert.False(gate.IsDeferred("waiting"));
    }

    [Fact]
    public void AKlivesMessageIsNeverMadeToWaitForABudget()
    {
        // A handful of turns against a fleet that runs for hours is noise in the budget; a human
        // sitting there waiting for a reply is not. One over is the cheaper mistake.
        var (gate, _) = Build(measured: 1);
        Assert.True(gate.TryAdmit("p1", "commander", "go", out _));
        Assert.False(gate.TryAdmit("p2", "commander", "ordinary trigger", out _));
        Assert.True(gate.TryAdmit("p2", "commander", "Message from Klives: are you there?", out _, mustAdmit: true));
        Assert.Equal(2, gate.Describe().Live);
        Assert.False(gate.IsDeferred("p2"));   // it ran: it must not still look like it is waiting
    }

    [Fact]
    public void Disabled_AdmitsEverything()
    {
        var (gate, _) = Build(measured: 1);
        gate.Configure(isEnabled: false);
        for (int i = 0; i < 20; i++)
            Assert.True(gate.TryAdmit($"p{i}", "commander", "go", out _));
        Assert.Equal(20, gate.Describe().Live);
    }

    [Fact]
    public void Describe_SaysWhereTheBudgetCameFromAndWhoIsWaiting()
    {
        var (gate, _) = Build(measured: 1);
        Assert.True(gate.TryAdmit("p1", "commander", "go", out _));
        Assert.False(gate.TryAdmit("p2", "worker-7", "waiting", out _));

        var snapshot = gate.Describe();
        Assert.True(snapshot.Enabled);
        Assert.Equal(1, snapshot.Live);
        Assert.Equal(1, snapshot.Active);
        Assert.Equal(1, snapshot.Budget);
        Assert.Equal("measured", snapshot.Source);
        Assert.Equal("p1/commander", Assert.Single(snapshot.LiveKeys));
        var waiting = Assert.Single(snapshot.DeferredWakes);
        Assert.Equal("p2", waiting.ProjectID);
        Assert.Equal("worker-7", waiting.AgentID);
        Assert.False(waiting.IsCommander);
    }
}
