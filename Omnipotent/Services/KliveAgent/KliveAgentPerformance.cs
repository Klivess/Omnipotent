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
