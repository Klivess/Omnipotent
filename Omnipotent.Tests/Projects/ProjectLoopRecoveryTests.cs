using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects;

public class ProjectLoopRecoveryTests
{
    [Fact]
    public void FirstStopCreatesADurableOneHourCooldown()
    {
        var now = new DateTime(2026, 8, 9, 4, 0, 0, DateTimeKind.Utc);

        var action = ProjectLoopRecovery.Create(null, "commander", "project_directive",
            "change the call shape", now);

        Assert.Equal("loop-recovery", action.Kind);
        Assert.Equal(now, action.RecordedAt);
        Assert.Equal(now + ProjectLoopRecovery.InitialRetryDelay, action.NotBefore);
        Assert.True(ProjectLoopRecovery.DefersAutomaticWake(action, now.AddMinutes(59)));
        Assert.False(ProjectLoopRecovery.DefersAutomaticWake(action, now.AddHours(1)));
    }

    [Fact]
    public void RepeatedStopsBackOffExponentiallyAndCapAtOneDay()
    {
        var now = new DateTime(2026, 8, 9, 4, 0, 0, DateTimeKind.Utc);
        ProjectResumeAction? previous = null;
        var expectedHours = new[] { 1, 2, 4, 8, 16, 24, 24 };

        foreach (int hours in expectedHours)
        {
            var next = ProjectLoopRecovery.Create(previous, "commander", "project_directive",
                "change strategy", now);
            Assert.Equal(now.AddHours(hours), next.NotBefore);
            previous = next;
            now = now.AddDays(2);
        }
    }

    [Fact]
    public void LegacyLoopRecoveryWithoutADeadlineStillDefersAutomaticWakes()
    {
        var now = new DateTime(2026, 8, 9, 4, 0, 0, DateTimeKind.Utc);
        var legacy = new ProjectResumeAction
        {
            Kind = "loop-recovery",
            RecordedAt = now,
        };

        Assert.True(ProjectLoopRecovery.DefersAutomaticWake(legacy, now.AddMinutes(30)));
        Assert.False(ProjectLoopRecovery.DefersAutomaticWake(legacy, now.AddHours(1)));
    }

    [Fact]
    public void ExplicitDeadlinesKeepTheirIntentionalSleepSemantics()
    {
        var now = new DateTime(2026, 8, 9, 4, 0, 0, DateTimeKind.Utc);
        var resume = new ProjectResumeAction
        {
            Kind = "external-wait",
            RecordedAt = now,
            NotBefore = now.AddHours(3),
        };

        Assert.True(ProjectLoopRecovery.DefersAutomaticWake(resume, now.AddHours(2)));
        Assert.False(ProjectLoopRecovery.DefersAutomaticWake(resume, now.AddHours(3)));
    }

    [Theory]
    [InlineData(true, 1, 0, true)]
    [InlineData(true, 0, 0, false)]
    [InlineData(true, 1, 1, false)]
    [InlineData(false, 1, 0, false)]
    public void OnlyProductiveLoopFreeRecoveryClearsTheStartingAction(
        bool completed, int productiveActions, int loopTrips, bool expected)
    {
        var action = new ProjectResumeAction
        {
            ActionID = "resume-1",
            Kind = "loop-recovery",
        };

        Assert.Equal(expected, ProjectLoopRecovery.ShouldClearAfterProgress(
            action, completed, productiveActions, loopTrips));
    }
}
