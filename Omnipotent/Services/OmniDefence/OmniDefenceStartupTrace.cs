namespace Omnipotent.Services.OmniDefence
{
    /// <summary>
    /// Where OmniDefence's start-up is and how long each step took.
    ///
    /// Until the routes are registered every /omnidefence route answers 404, so whatever runs before
    /// them is how long the service is missing. A slow start used to leave one line behind,
    /// "Started service.", and nothing to say which step it was in. Each step is now named and timed,
    /// a start that runs past <see cref="SlowAfter"/> is reported with the step it is in, and the
    /// timings are served by /omnidefence/overview/status.
    ///
    /// The start is ready once the routes exist. Steps that run after that (loading the fingerprint
    /// engine) are timed the same way and show as the step under way, but nothing waits for them.
    /// </summary>
    internal sealed class OmniDefenceStartupTrace
    {
        /// <summary>A start still running after this long is reported, once, with the step it is in.</summary>
        internal static readonly TimeSpan SlowAfter = TimeSpan.FromMinutes(2);

        private readonly Func<DateTimeOffset> utcNow;
        private readonly object gate = new();
        // Never removed from, so it enumerates (and serializes) in the order the steps ran.
        private readonly Dictionary<string, long> stepMs = new();
        /// <summary>What a step says about where its time went ("IP records": slot wait, open, pages read).</summary>
        private readonly Dictionary<string, string> stepDetail = new();
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
                stepDetail.Clear();
            }
        }

        /// <summary>Attaches a line to a step saying where its time went; served with the timings.</summary>
        public void Detail(string name, string detail)
        {
            lock (gate) stepDetail[name] = detail;
        }

        public async Task Step(string name, Func<Task> run)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            lock (gate) step = name;
            try { await run(); }
            finally
            {
                long ms = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                lock (gate)
                {
                    stepMs[name] = ms;
                    // A step after the routes is over when it ends; one before them hands straight
                    // on to the next, and a failed start keeps the name of the step it stopped in.
                    if (readyUtc != null && step == name) step = "";
                }
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

        /// <summary>One line for the log once the routes are up: the time to get there, slowest step first.</summary>
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
                    // How long the routes took to appear (so far, while they have not).
                    seconds = startedUtc == null
                        ? (double?)null
                        : Math.Round(((readyUtc ?? utcNow()) - startedUtc.Value).TotalSeconds, 1),
                    // The step under way, before the routes or after them, or the one a failed
                    // start stopped in.
                    step = startedUtc != null && step.Length > 0 ? step : null,
                    // Every step in the order it ran, in milliseconds; those after "routes" ran
                    // once the service was already answering.
                    steps = new Dictionary<string, long>(stepMs),
                    // Where a step's time went, for the steps that can say.
                    details = new Dictionary<string, string>(stepDetail),
                    error,
                };
            }
        }
    }
}
