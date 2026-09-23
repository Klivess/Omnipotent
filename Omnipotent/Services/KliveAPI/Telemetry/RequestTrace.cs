using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace Omnipotent.Services.KliveAPI.Telemetry
{
    /// <summary>
    /// Per-request timing + dimensions. A reference type on purpose: <c>UserRequest</c>
    /// is a struct copied through the pipeline, and every copy must mark the same trace.
    ///
    /// Timing model: the trace is always "in" exactly one stage. <see cref="Enter"/>
    /// charges the ticks since the last transition to the current stage and switches,
    /// so the stage durations partition the request's lifetime with no gaps or overlap.
    /// A request is a single logical flow (batch items get their own child traces), so
    /// no synchronisation is needed.
    /// </summary>
    public sealed class RequestTrace
    {
        public const int MaxSpans = 8;

        private static long _sequence;
        private static readonly long Salt = Random.Shared.NextInt64();

        public readonly long TraceId;
        public readonly long StartUtcTicks;
        public readonly long StartTimestamp;

        /// <summary>Accumulated Stopwatch ticks per <see cref="TelemetryStage"/>.</summary>
        public readonly long[] StageTicks = new long[TelemetryStages.Count];

        private TelemetryStage _current;
        private long _lastTimestamp;
        private long _endTimestamp;

        // ── dimensions (filled in as the pipeline learns them) ──
        public string Route = "/";
        public string Method = "GET";
        public bool Matched;
        public int StatusCode;
        public TelemetryCacheStatus Cache;
        public bool NotModified;
        public string? Encoding;
        public long RequestBytes;
        public long ResponseBytes;      // on the wire (post-compression)
        public long ResponseRawBytes;   // before compression
        public string? Origin;
        public string? ProfileId;
        public string? ProfileName;
        public int ProfileRank = -1;
        public string? DenyReason;
        public bool Denied;             // OmniDefence block/tarpit/honeypot
        public bool ClientDisconnected;
        public bool Exception;
        public bool ViaBatch;
        public long ParentTraceId;
        /// <summary>Forces the aggregation series (e.g. the preflight pseudo-series).</summary>
        public string? SeriesOverride;
        public int InFlightAtStart;
        public long PendingWorkAtStart;

        // ── custom spans ──
        private string[]? _spanNames;
        private long[]? _spanStartTicks;
        private long[]? _spanTicks;
        private int _spanCount;

        public RequestTrace(long acceptTimestamp, TelemetryStage initialStage = TelemetryStage.DispatchQueue)
        {
            TraceId = NextId();
            StartTimestamp = acceptTimestamp;
            _lastTimestamp = acceptTimestamp;
            _current = initialStage;
            // Back-date the wall-clock start to the accept instant.
            long elapsed = Stopwatch.GetTimestamp() - acceptTimestamp;
            StartUtcTicks = DateTime.UtcNow.Ticks - TicksToTimeSpanTicks(Math.Max(0, elapsed));
        }

        public static RequestTrace StartNow(TelemetryStage initialStage = TelemetryStage.Prologue) =>
            new(Stopwatch.GetTimestamp(), initialStage);

        private static long NextId()
        {
            long seq = Interlocked.Increment(ref _sequence);
            // Mix so ids are non-guessable-ish and unique within the process lifetime.
            ulong x = (ulong)(seq ^ Salt);
            x ^= x >> 33; x *= 0xff51afd7ed558ccdUL; x ^= x >> 33; x *= 0xc4ceb9fe1a85ec53UL; x ^= x >> 33;
            return (long)(x & 0x7FFF_FFFF_FFFF_FFFFUL);
        }

        public string TraceIdHex => TraceId.ToString("x16", CultureInfo.InvariantCulture);

        public TelemetryStage CurrentStage => _current;
        public bool IsCompleted => _endTimestamp != 0;

        /// <summary>Charges time since the last transition to the current stage, then switches.</summary>
        public void Enter(TelemetryStage stage)
        {
            if (_endTimestamp != 0) return;
            long now = Stopwatch.GetTimestamp();
            StageTicks[(int)_current] += now - _lastTimestamp;
            _lastTimestamp = now;
            _current = stage;
        }

        /// <summary>Enter <paramref name="stage"/> and return the stage that was current, for scoped restores.</summary>
        public TelemetryStage Swap(TelemetryStage stage)
        {
            TelemetryStage prev = _current;
            Enter(stage);
            return prev;
        }

        /// <summary>Closes the trace. Idempotent.</summary>
        public void Complete()
        {
            if (_endTimestamp != 0) return;
            long now = Stopwatch.GetTimestamp();
            StageTicks[(int)_current] += now - _lastTimestamp;
            _lastTimestamp = now;
            _endTimestamp = now;
        }

        /// <summary>Total lifetime ticks (all stages).</summary>
        public long TotalTicks => (_endTimestamp != 0 ? _endTimestamp : Stopwatch.GetTimestamp()) - StartTimestamp;

        /// <summary>Client-visible latency ticks: every stage except DefenceDelay and Teardown.</summary>
        public long LatencyTicks
        {
            get
            {
                long sum = 0;
                for (int i = 0; i < StageTicks.Length; i++)
                {
                    if (TelemetryStages.CountsTowardLatency(i)) sum += StageTicks[i];
                }
                // Include the in-progress stage when read before Complete().
                if (_endTimestamp == 0 && TelemetryStages.CountsTowardLatency((int)_current))
                {
                    sum += Stopwatch.GetTimestamp() - _lastTimestamp;
                }
                return sum;
            }
        }

        /// <summary>
        /// Records a named custom span (e.g. "gate-wait"). Dispose the returned scope to
        /// close it. Spans overlay stages (they don't move time between stages).
        /// </summary>
        public SpanScope Span(string name) => new(this, name, Stopwatch.GetTimestamp());

        public void AddSpan(string name, long startTimestamp, long ticks)
        {
            if (string.IsNullOrEmpty(name) || ticks < 0) return;
            _spanNames ??= new string[MaxSpans];
            _spanStartTicks ??= new long[MaxSpans];
            _spanTicks ??= new long[MaxSpans];
            // Same-name spans accumulate (e.g. several gate waits in one handler).
            for (int i = 0; i < _spanCount; i++)
            {
                if (string.Equals(_spanNames[i], name, StringComparison.Ordinal))
                {
                    _spanTicks[i] += ticks;
                    return;
                }
            }
            if (_spanCount >= MaxSpans) return;
            _spanNames[_spanCount] = name.Length > 48 ? name[..48] : name;
            _spanStartTicks[_spanCount] = Math.Max(0, startTimestamp - StartTimestamp);
            _spanTicks[_spanCount] = ticks;
            _spanCount++;
        }

        public int SpanCount => _spanCount;
        public string SpanName(int i) => _spanNames![i];
        public long SpanTicks(int i) => _spanTicks![i];
        public long SpanStartTicks(int i) => _spanStartTicks![i];

        public readonly struct SpanScope : IDisposable
        {
            private readonly RequestTrace? _trace;
            private readonly string _name;
            private readonly long _start;

            internal SpanScope(RequestTrace trace, string name, long start)
            {
                _trace = trace;
                _name = name;
                _start = start;
            }

            public void Dispose() => _trace?.AddSpan(_name, _start, Stopwatch.GetTimestamp() - _start);
        }

        // ── ambient access for code that can't see the UserRequest (e.g. store gates) ──

        private static readonly AsyncLocal<RequestTrace?> _ambient = new();

        /// <summary>The trace of the request currently executing on this async flow, if any.</summary>
        public static RequestTrace? Current
        {
            get => _ambient.Value;
            internal set => _ambient.Value = value;
        }

        /// <summary>Span on the ambient trace, or a no-op scope when there is none.</summary>
        public static SpanScope AmbientSpan(string name)
        {
            RequestTrace? t = _ambient.Value;
            return t == null ? default : t.Span(name);
        }

        // ── Server-Timing ──

        /// <summary>
        /// Multi-entry Server-Timing value for the stages observed so far plus <c>app</c>
        /// (total server time before the first byte), e.g.
        /// <c>queue;dur=0.1, auth;dur=0.0, handler;dur=12.4, app;dur=13.0</c>.
        /// Zero-duration stages are omitted to keep the header small.
        /// </summary>
        public string BuildServerTimingHeader()
        {
            var sb = new StringBuilder(160);
            long now = Stopwatch.GetTimestamp();
            long app = 0;
            for (int i = 0; i < StageTicks.Length; i++)
            {
                long ticks = StageTicks[i];
                if (i == (int)_current && _endTimestamp == 0) ticks += now - _lastTimestamp;
                if (i == (int)TelemetryStage.Teardown) continue;
                if (i != (int)TelemetryStage.DefenceDelay) app += ticks;
                double ms = TicksToMs(ticks);
                if (ms < 0.05) continue;
                sb.Append(TelemetryStages.Keys[i]).Append(";dur=")
                  .Append(ms.ToString("F1", CultureInfo.InvariantCulture)).Append(", ");
            }
            sb.Append("app;dur=").Append(TicksToMs(app).ToString("F1", CultureInfo.InvariantCulture));
            // Resource Timing exposes Server-Timing (not custom headers) to page scripts, so the
            // trace id rides here too — it lets a client beacon join its server trace.
            sb.Append(", trace;desc=").Append(TraceIdHex);
            return sb.ToString();
        }

        // ── conversions ──

        public static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        public static long TicksToMicros(long ticks) => (long)(ticks * 1_000_000.0 / Stopwatch.Frequency);
        public static long TicksToTimeSpanTicks(long ticks) => (long)(ticks * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency);
    }

    public enum TelemetryCacheStatus : byte
    {
        None = 0,
        Hit = 1,
        Miss = 2,
        Bypass = 3,
    }
}
