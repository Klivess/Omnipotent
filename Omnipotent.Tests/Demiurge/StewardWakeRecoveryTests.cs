using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.Demiurge;

namespace Omnipotent.Tests.Demiurge;

public sealed class StewardWakeRecoveryTests
{
    private static RemoteLLMException Failure(RemoteLLMFailureKind kind, int? seconds = null) =>
        new(kind, "test failure", "router", "model", retryAfter: seconds.HasValue ? TimeSpan.FromSeconds(seconds.Value) : null);

    [Fact]
    public async Task TransientFailureRetainsTheWakeAndDispatchesToolsOnlyAfterAResponse()
    {
        var recovery = new StewardWakeRecovery();
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
        var recovery = new StewardWakeRecovery();
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
        Assert.Null(StewardWakeRecovery.RetryDelay(Failure(kind), 0, TimeSpan.Zero));

    [Fact]
    public void ExplicitRetryAfterIsNeverShortenedToFitTheWake()
    {
        Assert.Null(StewardWakeRecovery.RetryDelay(Failure(RemoteLLMFailureKind.RateLimited, 121), 0, TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(90), StewardWakeRecovery.RetryDelay(
            Failure(RemoteLLMFailureKind.RateLimited, 90), 0, TimeSpan.Zero));
        Assert.Null(StewardWakeRecovery.RetryDelay(Failure(RemoteLLMFailureKind.RateLimited, 90), 1, TimeSpan.FromSeconds(90)));
    }

    [Fact]
    public async Task CancellationDuringRetryStopsBeforeAnotherModelCall()
    {
        using var cts = new CancellationTokenSource();
        int calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new StewardWakeRecovery().ExecuteAsync<int>(
            () => { calls++; return Task.FromException<int>(Failure(RemoteLLMFailureKind.Network)); },
            (_, _, _) => cts.Cancel(), cts.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task InternalTimeoutRetriesInTheSameWakeInsteadOfCancellingIt()
    {
        int calls = 0;
        var recovery = new StewardWakeRecovery();
        var result = await recovery.ExecuteAsync(() => ++calls == 1
            ? Task.FromException<string>(new TaskCanceledException("HTTP timeout"))
            : Task.FromResult("completed"), (failure, _, _) => Assert.Equal(RemoteLLMFailureKind.Timeout, failure.Kind),
            CancellationToken.None, (_, _) => Task.CompletedTask, model: "test-model");
        Assert.Equal("completed", result);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(StewardEventTypes.WakeCompleted)]
    [InlineData(StewardEventTypes.WakeDeferred)]
    [InlineData(StewardEventTypes.WakeFailed)]
    public void RestartAfterOutcomeCommitDoesNotOverwriteItWithCancelled(string outcome)
    {
        string pid = "test_" + Guid.NewGuid().ToString("N");
        var events = new StewardEventLogStore(_ => { });
        events.Append(new StewardEvent { StewardID = pid, WakeID = "wake", Type = outcome });
        Assert.Equal(outcome, StewardWakeRecovery.RecordInterruption(events, pid, "wake", "commander"));
        Assert.Single(events.ReadTail(pid, 10));
    }

    [Fact]
    public void InterruptedWorkerGetsOneTerminalOutcomeAndUnknownToolResult()
    {
        string pid = "test_" + Guid.NewGuid().ToString("N");
        var events = new StewardEventLogStore(_ => { });
        events.Append(new StewardEvent { StewardID = pid, WakeID = "wake", AgentID = "worker",
            Type = StewardEventTypes.ToolCall, ToolName = "publish", ToolCallId = "call" });
        StewardWakeRecovery.RecordInterruption(events, pid, "wake", "worker");
        StewardWakeRecovery.RecordInterruption(events, pid, "wake", "worker");
        var history = events.ReadTail(pid, 10);
        Assert.Single(history, e => e.Type == StewardEventTypes.WakeCancelled);
        Assert.Single(history, e => e.Type == StewardEventTypes.ToolResult
            && e.Text.StartsWith(StewardToolCallJournal.InterruptedResultPrefix));
        Assert.Equal(3, history.Count);
    }

    [Fact]
    public async Task PersistentInternalTimeoutReportsProviderFailureWithModel()
    {
        var error = await Assert.ThrowsAsync<RemoteLLMException>(() => new StewardWakeRecovery().ExecuteAsync<int>(
            () => Task.FromException<int>(new OperationCanceledException()), (_, _, _) => { },
            CancellationToken.None, (_, _) => Task.CompletedTask, model: "test-model"));
        Assert.Equal(RemoteLLMFailureKind.Timeout, error.Kind);
        Assert.Equal("test-model", error.Model);
    }

    [Fact]
    public async Task RequestedCancellationInsideInferenceDoesNotRetry()
    {
        using var cts = new CancellationTokenSource();
        int calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new StewardWakeRecovery().ExecuteAsync<int>(
            () => { calls++; cts.Cancel(); return Task.FromCanceled<int>(cts.Token); },
            (_, _, _) => throw new Exception("Must not retry"), cts.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void WorkerRouteFailureDoesNotBlockTeammatesAndRecoversAtTheDeadline()
    {
        var now = DateTime.UtcNow;
        var runtime = new StewardRuntimeState();
        runtime.Health.Dependencies[StewardWakeRecovery.DependencyKey("worker")] = new()
            { Healthy = false, RetryAt = now.AddSeconds(30), Code = "ModelUnavailable" };
        Assert.NotNull(StewardWakeRecovery.BlockedUntil(runtime, "worker", now));
        Assert.Null(StewardWakeRecovery.BlockedUntil(runtime, "commander", now));
        Assert.False(StewardWakeRecovery.RetryDue(runtime, "worker", now));
        Assert.True(StewardWakeRecovery.RetryDue(runtime, "worker", now.AddSeconds(30)));
        Assert.False(StewardWakeRecovery.IsSharedFailure(Failure(RemoteLLMFailureKind.ModelUnavailable)));
        Assert.True(StewardWakeRecovery.IsSharedFailure(Failure(RemoteLLMFailureKind.RateLimited)));
    }

    [Fact]
    public void SuccessDoesNotEraseATeammatesNewSharedCircuitOrOtherRouteFailure()
    {
        string root = Path.Combine(Path.GetTempPath(), "wake-recovery-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var now = DateTime.UtcNow;
            var store = new StewardRuntimeStateStore(_ => { }, root);
            store.RecordDependencyHealth("p", StewardWakeRecovery.DependencyKey("worker"), false, "Network", "retry", now.AddMinutes(1));
            store.RecordDependencyHealth("p", StewardWakeRecovery.DependencyKey("commander"), false, "Network", "retry", now);
            store.OpenCircuit("p", "RateLimited", "wait", now.AddMinutes(1));
            store.RecordExecutionSuccess("p", providerAgentID: "commander");
            var reloaded = new StewardRuntimeStateStore(_ => { }, root).Get("p");
            Assert.Equal(StewardCircuitStatus.Open, reloaded.Health.Circuit.Status);
            Assert.Contains(StewardWakeRecovery.DependencyKey("worker"), reloaded.Health.Dependencies.Keys);
            Assert.DoesNotContain(StewardWakeRecovery.DependencyKey("commander"), reloaded.Health.Dependencies.Keys);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void DeadlineRecoveryBypassesIdleBackoffOnlyOnce()
    {
        var now = DateTime.UtcNow;
        var resume = new StewardResumeAction { Kind = "loop-recovery", NotBefore = now, RecordedAt = now.AddMinutes(-2) };
        var worker = new StewardAgentRecord { AgentID = "w", Objective = "Deliver", WorkStatus = StewardAgentWorkStatus.Assigned, LastWakeAt = now.AddMinutes(-2) };
        Assert.True(StewardWorkerHeartbeat.ShouldWake(worker, false, now, 20, 240, 5, resume));
        worker.LastWakeAt = now;
        Assert.False(StewardWorkerHeartbeat.ShouldWake(worker, false, now.AddSeconds(15), 20, 240, 5, resume));
        Assert.True(StewardWorkerHeartbeat.ShouldWake(worker, false, now.AddSeconds(15), 20, 240, 5, providerRetryDue: true));
        worker.WorkStatus = StewardAgentWorkStatus.Completed;
        Assert.False(StewardWorkerHeartbeat.ShouldWake(worker, false, now, 20, 240, 5, providerRetryDue: true));
        Assert.True(StewardWorkerHeartbeat.ShouldWake(worker, false, now, 20, 240, 5,
            providerRetryDue: true, hasNewInstruction: true));
        worker.Retired = true;
        Assert.False(StewardWorkerHeartbeat.ShouldWake(worker, false, now, 20, 240, 5,
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
            var store = new StewardRuntimeStateStore(_ => { }, root);
            store.OpenCircuit("p", "RateLimited", "wait", retry, nowUtc: now);
            store.DeferProviderAdmission("p", "new-worker", retry, now);
            long revision = store.Get("p").Revision;
            store.DeferProviderAdmission("p", "new-worker", retry, now.AddSeconds(2));
            Assert.Equal(revision, store.Get("p").Revision); // repeated queue polls cause no writes
            store.RecordExecutionSuccess("p", providerAgentID: "commander", nowUtc: retry);
            var restarted = new StewardRuntimeStateStore(_ => { }, root);
            Assert.Equal(StewardCircuitStatus.Closed, restarted.Get("p").Health.Circuit.Status);
            Assert.True(StewardWakeRecovery.RetryDue(restarted.Get("p"), "new-worker", retry));

            var longer = now.AddMinutes(15);
            restarted.RecordDependencyHealth("p", StewardWakeRecovery.DependencyKey("new-worker"), false,
                "RateLimited", "longer route restriction", longer, nowUtc: now);
            restarted.OpenCircuit("p", "RateLimited", "shared window", retry, nowUtc: now);
            Assert.Equal(longer, StewardWakeRecovery.BlockedUntil(restarted.Get("p"), "new-worker", now));
            restarted.DeferProviderAdmission("p", "new-worker", retry, now);
            Assert.Equal(longer, restarted.Get("p").Health.Dependencies[StewardWakeRecovery.DependencyKey("new-worker")].RetryAt);

            restarted.DeferProviderAdmission("p", "manual-wait", DateTime.MaxValue, now);
            Assert.DoesNotContain(StewardWakeRecovery.DependencyKey("manual-wait"), restarted.Get("p").Health.Dependencies.Keys);
        }
        finally
        {
            Assert.StartsWith(basePath + Path.DirectorySeparatorChar, Path.GetFullPath(root));
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
