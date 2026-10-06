namespace Omnipotent.Services.KliveLLM;

/// <summary>What a model call is doing right now, as seen from inside KliveLLM.</summary>
public enum KliveLLMActivityKind
{
    /// <summary>Waiting locally for permission to send: an AIRouter slot, a rate window or a cool-off.</summary>
    Queued,
    /// <summary>Dispatched; waiting for the provider to start answering (prefill, or a silent stream).</summary>
    AwaitingProvider,
    /// <summary>The provider is streaming visible output or tool calls.</summary>
    Streaming,
    /// <summary>The provider is streaming reasoning tokens that never reach the visible answer.</summary>
    Reasoning,
    /// <summary>A transient failure; waiting before the next attempt.</summary>
    Retrying,
}

/// <summary>One liveness observation. <see cref="Elapsed"/> is time spent in the current state.</summary>
public readonly record struct KliveLLMActivity(KliveLLMActivityKind Kind, string Detail, TimeSpan Elapsed);

/// <summary>
/// The caller's liveness contract for every model call made inside the scope.
///
/// A model call can legitimately produce nothing a caller can see for minutes: queued behind three
/// shared AIRouter slots, prefilling a large prompt, or reasoning without emitting a visible token.
/// A caller whose stall detector watches only visible output reads all of that as a hang and kills
/// a healthy request — which is exactly how an interactive KliveAgent run died at "step 1" with no
/// output. Inside a scope, KliveLLM reports what it is doing instead, and a stream that genuinely
/// stops producing bytes is bounded by <see cref="StreamIdleTimeout"/> so the heartbeat never keeps
/// a wedged request alive.
/// </summary>
public sealed class KliveLLMActivityScope
{
    public KliveLLMActivityScope(Action<KliveLLMActivity> observer, TimeSpan? streamIdleTimeout = null)
    {
        Observer = observer ?? throw new ArgumentNullException(nameof(observer));
        StreamIdleTimeout = streamIdleTimeout is { } t && t > TimeSpan.Zero ? t : null;
    }

    public Action<KliveLLMActivity> Observer { get; }

    /// <summary>Longest silence tolerated between two lines of a streamed response before the attempt
    /// is abandoned as stalled. Null leaves streams unbounded (the historical behaviour).</summary>
    public TimeSpan? StreamIdleTimeout { get; }
}

/// <summary>A streamed response that stopped producing bytes for longer than the caller allows.</summary>
public sealed class KliveLLMStreamStalledException : TimeoutException
{
    public KliveLLMStreamStalledException(TimeSpan idle, bool producedOutput)
        : base($"The model provider stopped sending data for {idle.TotalSeconds:0}s"
            + (producedOutput ? " after partial output." : " before producing any output."))
    {
        Idle = idle;
        ProducedOutput = producedOutput;
    }

    public TimeSpan Idle { get; }
    public bool ProducedOutput { get; }
}

public partial class KliveLLM
{
    private static readonly AsyncLocal<KliveLLMActivityScope?> activityScope = new();

    /// <summary>How often an in-flight state is re-reported while nothing else happens.</summary>
    internal static TimeSpan ActivityPulseInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Report this call chain's model activity to <paramref name="observer"/> until disposed. Flows
    /// through awaits (AsyncLocal), so every model call made inside the using-block is observed while
    /// unrelated callers on other flows are not. Must be entered from the awaiting method itself.
    /// </summary>
    public static IDisposable ObserveActivity(Action<KliveLLMActivity> observer, TimeSpan? streamIdleTimeout = null)
    {
        var previous = activityScope.Value;
        activityScope.Value = new KliveLLMActivityScope(observer, streamIdleTimeout);
        return new ScopeRestorer(previous);
    }

    internal static KliveLLMActivityScope? CurrentActivityScope => activityScope.Value;

    internal static void ReportActivity(KliveLLMActivityKind kind, string detail, TimeSpan elapsed = default)
    {
        var scope = activityScope.Value;
        if (scope == null) return;
        try { scope.Observer(new KliveLLMActivity(kind, detail ?? string.Empty, elapsed)); }
        catch { /* an observer must never break the request it is watching */ }
    }

    /// <summary>
    /// Starts a background pulse that re-reports the current state every
    /// <see cref="ActivityPulseInterval"/>. Returns null when nobody is observing, so an unobserved
    /// request pays nothing. Only bounded waits may be pulsed: the pulse is evidence of liveness.
    /// </summary>
    internal static ActivityPulse? StartActivityPulse(KliveLLMActivityKind kind, Func<string> describe)
        => activityScope.Value == null ? null : new ActivityPulse(kind, describe);

    private sealed class ScopeRestorer : IDisposable
    {
        private readonly KliveLLMActivityScope? previous;
        private int disposed;

        public ScopeRestorer(KliveLLMActivityScope? previous) => this.previous = previous;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) activityScope.Value = previous;
        }
    }

    /// <summary>A state that is reported immediately and then re-reported on a fixed cadence.</summary>
    internal sealed class ActivityPulse : IDisposable
    {
        private readonly CancellationTokenSource stop = new();
        private readonly object sync = new();
        private readonly System.Diagnostics.Stopwatch inState = System.Diagnostics.Stopwatch.StartNew();
        private KliveLLMActivityKind kind;
        private Func<string> describe;

        public ActivityPulse(KliveLLMActivityKind kind, Func<string> describe)
        {
            this.kind = kind;
            this.describe = describe;
            Report();
            // Started on the caller's flow so the observer captured by AsyncLocal is the caller's.
            _ = PulseAsync();
        }

        /// <summary>Moves to a new state, reporting it at once if it actually changed.</summary>
        public void Set(KliveLLMActivityKind next, Func<string> nextDescribe)
        {
            bool changed;
            lock (sync)
            {
                changed = next != kind;
                kind = next;
                describe = nextDescribe;
                if (changed) inState.Restart();
            }
            if (changed) Report();
        }

        private void Report()
        {
            KliveLLMActivityKind current;
            Func<string> text;
            TimeSpan elapsed;
            lock (sync) { current = kind; text = describe; elapsed = inState.Elapsed; }
            string detail;
            try { detail = text() ?? string.Empty; } catch { detail = string.Empty; }
            ReportActivity(current, detail, elapsed);
        }

        private async Task PulseAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(ActivityPulseInterval, stop.Token).ConfigureAwait(false);
                    Report();
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            try { stop.Cancel(); } catch (ObjectDisposedException) { }
            stop.Dispose();
        }
    }
}
