using Omnipotent.Services.KliveLLM;

namespace Omnipotent.Services.Projects;

/// <summary>Retry inference without replaying tools or discarding the current conversation.</summary>
internal sealed class ProjectWakeRecovery
{
    internal const int MaxRetries = 2;
    internal static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(2);
    public int Retries { get; private set; }
    public long WaitMs { get; private set; }
    private TimeSpan reservedWait;

    internal async Task<bool> WaitForAdmissionAsync(Func<DateTime?> blockedUntil,
        Action heartbeat, CancellationToken ct)
    {
        while (blockedUntil() is { } retryAt)
        {
            var remaining = retryAt - DateTime.UtcNow;
            if (remaining > MaxWait - reservedWait) return false;
            var delay = TimeSpan.FromMilliseconds(Math.Clamp(remaining.TotalMilliseconds, 1, 15_000));
            heartbeat();
            reservedWait += delay;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try { await Task.Delay(delay, ct); }
            finally { WaitMs += timer.ElapsedMilliseconds; }
        }
        return true;
    }

    internal static string DependencyKey(string agentID) => ProjectProviderFailure.DependencyKey + ":" + agentID;

    // Authentication, account credit and rate limits can affect every route. A bad model/request
    // or a transient failure on one worker's route must not take healthy teammates offline.
    internal static bool IsSharedFailure(RemoteLLMException ex) => ex.Kind is
        RemoteLLMFailureKind.Authentication or RemoteLLMFailureKind.InsufficientProviderCredit
        or RemoteLLMFailureKind.RateLimited;

    internal static DateTime AutomaticRetryAt(RemoteLLMException ex, bool flatFeeProvider) =>
        ex.IsRetryable && !(ex.RetryAfter > TimeSpan.Zero)
            ? DateTime.UtcNow.AddSeconds(ex.Kind == RemoteLLMFailureKind.RateLimited ? 60 : 30)
            : ProjectProviderFailure.AutomaticRetryAt(ex, flatFeeProvider: flatFeeProvider);

    internal static DateTime? BlockedUntil(ProjectRuntimeState runtime, string agentID, DateTime now)
    {
        var circuit = runtime.Health.Circuit;
        DateTime? until = null;
        if (circuit.Status == ProjectCircuitStatus.Open
            && (!circuit.RetryAt.HasValue || circuit.RetryAt > now))
            until = circuit.RetryAt ?? DateTime.MaxValue;
        var dependency = runtime.Health.Dependencies.GetValueOrDefault(DependencyKey(agentID));
        if (dependency is { Healthy: false, RetryAt: not null } && dependency.RetryAt > now
            && (!until.HasValue || dependency.RetryAt > until)) until = dependency.RetryAt;
        return until;
    }

    internal static bool RetryDue(ProjectRuntimeState runtime, string agentID, DateTime now)
    {
        if (BlockedUntil(runtime, agentID, now).HasValue) return false;
        var dependency = runtime.Health.Dependencies.GetValueOrDefault(DependencyKey(agentID));
        return dependency is { Healthy: false, RetryAt: not null } && dependency.RetryAt <= now
            || runtime.Health.Circuit.Status == ProjectCircuitStatus.Open
                && runtime.Health.Circuit.RetryAt <= now;
    }

    internal static TimeSpan? RetryDelay(RemoteLLMException ex, int retries, TimeSpan waited)
    {
        if (!ex.IsRetryable || retries >= MaxRetries) return null;
        var delay = ex.RetryAfter is { } requested && requested > TimeSpan.Zero
            ? requested
            : ex.Kind == RemoteLLMFailureKind.RateLimited
                ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(5 * Math.Pow(3, retries));
        return delay <= MaxWait - waited ? delay : null;
    }

    internal async Task<T> ExecuteAsync<T>(Func<Task<T>> query,
        Action<RemoteLLMException, int, TimeSpan> onRetry, CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        delayAsync ??= Task.Delay;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return await query(); }
            catch (RemoteLLMException ex)
            {
                var delay = RetryDelay(ex, Retries, reservedWait);
                if (!delay.HasValue) throw;
                onRetry(ex, ++Retries, delay.Value);
                reservedWait += delay.Value;
                var timer = System.Diagnostics.Stopwatch.StartNew();
                try { await delayAsync(delay.Value, ct); }
                finally { WaitMs += timer.ElapsedMilliseconds; }
            }
        }
    }
}
