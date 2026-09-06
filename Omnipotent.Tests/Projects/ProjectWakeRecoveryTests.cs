using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects;

public sealed class ProjectWakeRecoveryTests
{
    private static RemoteLLMException Failure(RemoteLLMFailureKind kind, int? seconds = null) =>
        new(kind, "test failure", "router", "model", retryAfter: seconds.HasValue ? TimeSpan.FromSeconds(seconds.Value) : null);

    [Fact]
    public async Task TransientFailureRetainsTheWakeAndDispatchesToolsOnlyAfterAResponse()
    {
        var recovery = new ProjectWakeRecovery();
        int modelCalls = 0, toolDispatches = 0;
        var waits = new List<TimeSpan>();
        string result = await recovery.ExecuteAsync(() => ++modelCalls < 3
            ? Task.FromException<string>(Failure(RemoteLLMFailureKind.Network))
            : Task.FromResult("tool-call"), (_, _, delay) => waits.Add(delay), CancellationToken.None,
            (_, _) => Task.CompletedTask);
        if (result == "tool-call") toolDispatches++;
        Assert.Equal(3, modelCalls);
        Assert.Equal(1, toolDispatches);
        Assert.Equal(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15) }, waits);
        Assert.Equal(2, recovery.Retries);
    }

    [Fact]
    public async Task ExhaustionRemainsAFailureAndTheRetryBudgetIsSharedAcrossTheWake()
    {
        var recovery = new ProjectWakeRecovery();
        var failure = Failure(RemoteLLMFailureKind.RateLimited, 60);
        int calls = 0;
        var thrown = await Assert.ThrowsAsync<RemoteLLMException>(() => recovery.ExecuteAsync<int>(
            () => { calls++; return Task.FromException<int>(failure); }, (_, _, _) => { },
            CancellationToken.None, (_, _) => Task.CompletedTask));
        Assert.Same(failure, thrown);
        Assert.Equal(3, calls);
        await Assert.ThrowsAsync<RemoteLLMException>(() => recovery.ExecuteAsync<int>(
            () => { calls++; return Task.FromException<int>(failure); }, (_, _, _) => throw new Exception("Must not retry"),
            CancellationToken.None));
        Assert.Equal(4, calls);
    }

    [Theory]
    [InlineData(RemoteLLMFailureKind.Authentication)]
    [InlineData(RemoteLLMFailureKind.InvalidRequest)]
    [InlineData(RemoteLLMFailureKind.ModelUnavailable)]
    [InlineData(RemoteLLMFailureKind.InsufficientProviderCredit)]
    public void NonTransientFailuresDoNotRetryUnchangedRequests(RemoteLLMFailureKind kind) =>
        Assert.Null(ProjectWakeRecovery.RetryDelay(Failure(kind), 0, TimeSpan.Zero));

    [Fact]
    public void ExplicitRetryAfterIsNeverShortenedToFitTheWake()
    {
        Assert.Null(ProjectWakeRecovery.RetryDelay(Failure(RemoteLLMFailureKind.RateLimited, 121), 0, TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(90), ProjectWakeRecovery.RetryDelay(
            Failure(RemoteLLMFailureKind.RateLimited, 90), 0, TimeSpan.Zero));
        Assert.Null(ProjectWakeRecovery.RetryDelay(Failure(RemoteLLMFailureKind.RateLimited, 90), 1, TimeSpan.FromSeconds(90)));
    }

    [Fact]
    public async Task CancellationDuringRetryStopsBeforeAnotherModelCall()
    {
        using var cts = new CancellationTokenSource();
        int calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProjectWakeRecovery().ExecuteAsync<int>(
            () => { calls++; return Task.FromException<int>(Failure(RemoteLLMFailureKind.Network)); },
            (_, _, _) => cts.Cancel(), cts.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void WorkerRouteFailureDoesNotBlockTeammatesAndRecoversAtTheDeadline()
    {
        var now = DateTime.UtcNow;
        var runtime = new ProjectRuntimeState();
        runtime.Health.Dependencies[ProjectWakeRecovery.DependencyKey("worker")] = new()
            { Healthy = false, RetryAt = now.AddSeconds(30), Code = "ModelUnavailable" };
        Assert.NotNull(ProjectWakeRecovery.BlockedUntil(runtime, "worker", now));
        Assert.Null(ProjectWakeRecovery.BlockedUntil(runtime, "commander", now));
        Assert.False(ProjectWakeRecovery.RetryDue(runtime, "worker", now));
        Assert.True(ProjectWakeRecovery.RetryDue(runtime, "worker", now.AddSeconds(30)));
        Assert.False(ProjectWakeRecovery.IsSharedFailure(Failure(RemoteLLMFailureKind.ModelUnavailable)));
        Assert.True(ProjectWakeRecovery.IsSharedFailure(Failure(RemoteLLMFailureKind.RateLimited)));
    }

    [Fact]
    public void SuccessDoesNotEraseATeammatesNewSharedCircuitOrOtherRouteFailure()
    {
        string root = Path.Combine(Path.GetTempPath(), "wake-recovery-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var now = DateTime.UtcNow;
            var store = new ProjectRuntimeStateStore(_ => { }, root);
            store.RecordDependencyHealth("p", ProjectWakeRecovery.DependencyKey("worker"), false, "Network", "retry", now.AddMinutes(1));
            store.RecordDependencyHealth("p", ProjectWakeRecovery.DependencyKey("commander"), false, "Network", "retry", now);
            store.OpenCircuit("p", "RateLimited", "wait", now.AddMinutes(1));
            store.RecordExecutionSuccess("p", providerAgentID: "commander");
            var reloaded = new ProjectRuntimeStateStore(_ => { }, root).Get("p");
            Assert.Equal(ProjectCircuitStatus.Open, reloaded.Health.Circuit.Status);
            Assert.Contains(ProjectWakeRecovery.DependencyKey("worker"), reloaded.Health.Dependencies.Keys);
            Assert.DoesNotContain(ProjectWakeRecovery.DependencyKey("commander"), reloaded.Health.Dependencies.Keys);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void DeadlineRecoveryBypassesIdleBackoffOnlyOnce()
    {
        var now = DateTime.UtcNow;
        var resume = new ProjectResumeAction { Kind = "loop-recovery", NotBefore = now, RecordedAt = now.AddMinutes(-2) };
        var worker = new ProjectAgentRecord { AgentID = "w", Objective = "Deliver", WorkStatus = ProjectAgentWorkStatus.Assigned, LastWakeAt = now.AddMinutes(-2) };
        Assert.True(ProjectWorkerHeartbeat.ShouldWake(worker, false, now, 20, 240, 5, resume));
        worker.LastWakeAt = now;
        Assert.False(ProjectWorkerHeartbeat.ShouldWake(worker, false, now.AddSeconds(15), 20, 240, 5, resume));
        Assert.True(ProjectWorkerHeartbeat.ShouldWake(worker, false, now.AddSeconds(15), 20, 240, 5, providerRetryDue: true));
        worker.WorkStatus = ProjectAgentWorkStatus.Completed;
        Assert.False(ProjectWorkerHeartbeat.ShouldWake(worker, false, now, 20, 240, 5, providerRetryDue: true));
        Assert.True(ProjectWorkerHeartbeat.ShouldWake(worker, false, now, 20, 240, 5,
            providerRetryDue: true, hasNewInstruction: true));
        worker.Retired = true;
        Assert.False(ProjectWorkerHeartbeat.ShouldWake(worker, false, now, 20, 240, 5,
            providerRetryDue: true, hasNewInstruction: true));
    }

    [Fact]
    public void DeniedAdmissionSurvivesRestartAndSharedRecoveryWithoutShorteningRestrictions()
    {
        string basePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wake-admission-tests"));
        string root = Path.Combine(basePath, Guid.NewGuid().ToString("N"));
        try
        {
            var now = DateTime.UtcNow;
            var retry = now.AddMinutes(1);
            var store = new ProjectRuntimeStateStore(_ => { }, root);
            store.OpenCircuit("p", "RateLimited", "wait", retry, nowUtc: now);
            store.DeferProviderAdmission("p", "new-worker", retry, now);
            long revision = store.Get("p").Revision;
            store.DeferProviderAdmission("p", "new-worker", retry, now.AddSeconds(2));
            Assert.Equal(revision, store.Get("p").Revision); // repeated queue polls cause no writes
            store.RecordExecutionSuccess("p", providerAgentID: "commander", nowUtc: retry);
            var restarted = new ProjectRuntimeStateStore(_ => { }, root);
            Assert.Equal(ProjectCircuitStatus.Closed, restarted.Get("p").Health.Circuit.Status);
            Assert.True(ProjectWakeRecovery.RetryDue(restarted.Get("p"), "new-worker", retry));

            var longer = now.AddMinutes(15);
            restarted.RecordDependencyHealth("p", ProjectWakeRecovery.DependencyKey("new-worker"), false,
                "RateLimited", "longer route restriction", longer, nowUtc: now);
            restarted.OpenCircuit("p", "RateLimited", "shared window", retry, nowUtc: now);
            Assert.Equal(longer, ProjectWakeRecovery.BlockedUntil(restarted.Get("p"), "new-worker", now));
            restarted.DeferProviderAdmission("p", "new-worker", retry, now);
            Assert.Equal(longer, restarted.Get("p").Health.Dependencies[ProjectWakeRecovery.DependencyKey("new-worker")].RetryAt);

            restarted.DeferProviderAdmission("p", "manual-wait", DateTime.MaxValue, now);
            Assert.DoesNotContain(ProjectWakeRecovery.DependencyKey("manual-wait"), restarted.Get("p").Health.Dependencies.Keys);
        }
        finally
        {
            Assert.StartsWith(basePath + Path.DirectorySeparatorChar, Path.GetFullPath(root));
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
