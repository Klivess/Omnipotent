namespace Omnipotent.Services.OmniDefence
{
    /// <summary>
    /// Where OmniDefence's start-up is and how long each step took.
    ///
    /// The service registers its routes last, so until start-up finishes every /omnidefence route
    /// answers 404, and honeypots, blocked regions, the overview builder and fingerprinting are not
    /// running. A slow start used to leave one line behind, "Started service.", and nothing to say
    /// which step it was in. Each step is now named and timed, a start that runs past
    /// <see cref="SlowAfter"/> is reported with the step it is in, and the finished timings are
    /// served by /omnidefence/overview/status.
    /// </summary>
    internal sealed class OmniDefenceStartupTrace
    {
        /// <summary>A start still running after this long is reported, once, with the step it is in.</summary>
        internal static readonly TimeSpan SlowAfter = TimeSpan.FromMinutes(2);

        private readonly Func<DateTimeOffset> utcNow;
        private readonly object gate = new();
        // Never removed from, so it enumerates (and serializes) in the order the steps ran.
        private readonly Dictionary<string, long> stepMs = new();
        private DateTimeOffset? startedUtc;
        private DateTimeOffset? readyUtc;
        private string step = "";
        private string? error;

        public OmniDefenceStartupTrace(Func<DateTimeOffset>? utcNow = null)
            => this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

        /// <summary>A service that is restarted runs its start-up again: begin a clean trace.</summary>
        public void Begin()
        {
            lock (gate)
            {
                startedUtc = utcNow();
                readyUtc = null;
                error = null;
                step = "";
                stepMs.Clear();
            }
        }

        public async Task Step(string name, Func<Task> run)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            lock (gate) step = name;
            try { await run(); }
            finally
            {
                long ms = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                lock (gate) stepMs[name] = ms;
            }
        }

        public void Ready()
        {
            lock (gate)
            {
                readyUtc = utcNow();
                step = "";
            }
        }

        public void Failed(Exception ex)
        {
            lock (gate) error = ex.Message;
        }

        /// <summary>True while a start is under way: begun, not finished, not failed.</summary>
        public bool Running
        {
            get { lock (gate) return startedUtc != null && readyUtc == null && error == null; }
        }

        public string CurrentStep
        {
            get { lock (gate) return step; }
        }

        /// <summary>One line for the log once the service is up, slowest step first.</summary>
        public string Summary()
        {
            lock (gate)
            {
                double seconds = startedUtc == null ? 0 : ((readyUtc ?? utcNow()) - startedUtc.Value).TotalSeconds;
                string steps = string.Join(", ", stepMs.OrderByDescending(pair => pair.Value)
                    .Select(pair => $"{pair.Key} {pair.Value} ms"));
                return $"{seconds:0.0} s ({steps})";
            }
        }

        public object Status()
        {
            lock (gate)
            {
                return new
                {
                    ready = readyUtc != null,
                    startedUtc,
                    readyUtc,
                    seconds = startedUtc == null
                        ? (double?)null
                        : Math.Round(((readyUtc ?? utcNow()) - startedUtc.Value).TotalSeconds, 1),
                    // The step under way, or the one a failed start stopped in.
                    step = startedUtc != null && readyUtc == null ? step : null,
                    steps = new Dictionary<string, long>(stepMs),
                    error,
                };
            }
        }
    }
}
