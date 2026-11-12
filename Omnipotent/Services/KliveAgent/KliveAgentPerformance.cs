using System.Diagnostics;

namespace Omnipotent.Services.KliveAgent;

/// <summary>Content-free timings for one agent run. Stage durations may overlap (preparation
/// includes prompt and attachment work; speculative tools can overlap model time).</summary>
public sealed class KliveAgentPerformance
{
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly object _lock = new();
    private readonly Dictionary<string, PerformanceStage> _stages = new(StringComparer.Ordinal);
    public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
    public string Outcome { get; set; } = "failed";
    public long DurationMs => _elapsed.ElapsedMilliseconds;

    public IDisposable Measure(string stage) => new StageTimer(this, stage);

    public async Task<T> MeasureAsync<T>(string stage, Func<Task<T>> operation)
    {
        using (Measure(stage)) return await operation();
    }

    public void Add(string stage, long durationMs)
    {
        lock (_lock)
        {
            if (!_stages.TryGetValue(stage, out var value)) _stages[stage] = value = new PerformanceStage();
            value.Count++;
            value.TotalMs += Math.Max(0, durationMs);
            value.MaxMs = Math.Max(value.MaxMs, durationMs);
        }
    }

    public PerformanceRunSnapshot Snapshot(int iterations, int promptTokens, int completionTokens)
    {
        lock (_lock)
            return new PerformanceRunSnapshot
            {
                StartedAtUtc = StartedAtUtc,
                Outcome = Outcome,
                DurationMs = DurationMs,
                Iterations = iterations,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                Stages = _stages.ToDictionary(kv => kv.Key, kv => kv.Value.Copy(), StringComparer.Ordinal)
            };
    }

    private sealed class StageTimer : IDisposable
    {
        private readonly KliveAgentPerformance _owner;
        private readonly string _stage;
        private readonly Stopwatch _timer = Stopwatch.StartNew();
        private bool _disposed;
        public StageTimer(KliveAgentPerformance owner, string stage) { _owner = owner; _stage = stage; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.Add(_stage, _timer.ElapsedMilliseconds);
        }
    }
}

/// <summary>
/// Splits one model call into the states KliveLLM reports for it, as stages <c>model.queued</c>,
/// <c>model.awaiting</c>, <c>model.reasoning</c>, <c>model.streaming</c> and <c>model.retrying</c>, and
/// files the whole call under <c>model.alone</c>, <c>model.with1other</c> or <c>model.with2others</c> by how
/// many other KliveAgent calls were in flight when it was sent.
///
/// The single "model" stage was 66% of all run time and said nothing about why: a 195 s spoken "hello"
/// and a 3 s one looked the same. Disposing it closes the state that was open.
/// </summary>
internal sealed class ModelStateClock : IDisposable
{
    /// <summary>Shared count of calls in flight.</summary>
    internal sealed class InFlightCounter
    {
        private int count;
        public int Enter() => Interlocked.Increment(ref count);
        public void Leave() => Interlocked.Decrement(ref count);
        public int Current => Volatile.Read(ref count);
    }

    private readonly KliveAgentPerformance performance;
    private readonly InFlightCounter? inFlight;
    private readonly int concurrentAtStart;
    private readonly Stopwatch whole = Stopwatch.StartNew();
    private readonly Stopwatch inState = new();
    private readonly object gate = new();
    private Omnipotent.Services.KliveLLM.KliveLLMActivityKind? current;
    private bool disposed;
    private long awaitingMs;
    private bool sawOutput;

    public ModelStateClock(KliveAgentPerformance performance, InFlightCounter? inFlight = null)
    {
        this.performance = performance;
        this.inFlight = inFlight;
        concurrentAtStart = inFlight?.Enter() ?? 1;
    }

    /// <summary>
    /// How long the provider took to start answering, once the call has started producing output. Null
    /// for a call that never streamed: there "awaiting" covers the whole reply, so it is not a wait for
    /// the first byte and must not be read as one.
    /// </summary>
    public long? ProviderWaitMs
    {
        get { lock (gate) return sawOutput ? awaitingMs : null; }
    }

    public void Observe(Omnipotent.Services.KliveLLM.KliveLLMActivityKind kind)
    {
        lock (gate)
        {
            if (disposed || current == kind) return;
            CloseStateLocked();
            current = kind;
            if (kind is Omnipotent.Services.KliveLLM.KliveLLMActivityKind.Reasoning
                or Omnipotent.Services.KliveLLM.KliveLLMActivityKind.Streaming) sawOutput = true;
            inState.Restart();
        }
    }

    private void CloseStateLocked()
    {
        if (current is not { } kind) return;
        long elapsed = inState.ElapsedMilliseconds;
        if (kind == Omnipotent.Services.KliveLLM.KliveLLMActivityKind.AwaitingProvider) awaitingMs += elapsed;
        performance.Add(StageName(kind), elapsed);
        current = null;
    }

    internal static string StageName(Omnipotent.Services.KliveLLM.KliveLLMActivityKind kind) => kind switch
    {
        Omnipotent.Services.KliveLLM.KliveLLMActivityKind.Queued => "model.queued",
        Omnipotent.Services.KliveLLM.KliveLLMActivityKind.AwaitingProvider => "model.awaiting",
        Omnipotent.Services.KliveLLM.KliveLLMActivityKind.Reasoning => "model.reasoning",
        Omnipotent.Services.KliveLLM.KliveLLMActivityKind.Streaming => "model.streaming",
        Omnipotent.Services.KliveLLM.KliveLLMActivityKind.Retrying => "model.retrying",
        _ => "model.other",
    };

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            CloseStateLocked();
        }
        inFlight?.Leave();
        performance.Add(concurrentAtStart >= 3 ? "model.with2others" : concurrentAtStart == 2 ? "model.with1other" : "model.alone",
            whole.ElapsedMilliseconds);
    }
}

public sealed class PerformanceStage
{
    public long Count { get; set; }
    public long TotalMs { get; set; }
    public long MaxMs { get; set; }
    public PerformanceStage Copy() => new() { Count = Count, TotalMs = TotalMs, MaxMs = MaxMs };
}

public sealed class PerformanceRunSnapshot
{
    public DateTime StartedAtUtc { get; set; }
    public string Outcome { get; set; } = "failed";
    public long DurationMs { get; set; }
    public int Iterations { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public Dictionary<string, PerformanceStage> Stages { get; set; } = new(StringComparer.Ordinal);
}
