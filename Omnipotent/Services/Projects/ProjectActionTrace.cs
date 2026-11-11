using System.Diagnostics;
using Omnipotent.Services.KliveAPI.Telemetry;

namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Times the steps of a lifecycle action (pause, resume, halt, unhalt, clearing the cache halt) and
    /// says which one is slow.
    ///
    /// Every fleet action Klives took in October held its request open for an hour or more and the
    /// page gave up waiting: halt-all 84 min on 31 Oct (it reached the nine projects at 19:25, 19:37,
    /// then 20:47 to 20:48), pause 82 min, unhalt-all 136 min on 30 Oct, clearing the cache halt 65 min
    /// on 29 Oct while no wake was running at all. The handlers do a handful of in-memory and
    /// small-file operations, and nothing recorded which of them was waiting. Now every step is timed:
    /// a step slower than <see cref="SlowStep"/> is logged with its duration when it ends, one still
    /// running after <see cref="StuckAfter"/> is logged while it is stuck (so a step that never ends
    /// is still named), and the timings become telemetry spans when the action finishes inside its
    /// request.
    /// </summary>
    public sealed class ProjectActionTrace : IDisposable
    {
        internal static readonly TimeSpan SlowStep = TimeSpan.FromSeconds(5);
        internal static readonly TimeSpan StuckAfter = TimeSpan.FromSeconds(30);

        private readonly string action;
        private readonly Action<string> log;
        private readonly TimeSpan slowStep;
        private readonly object sync = new();
        private readonly List<(string Name, long Start, long Ticks)> steps = new();
        private readonly Timer? watchdog;
        private string? currentStep;
        private string? currentProject;
        private long currentStart;
        private bool stuckReported;
        private bool disposed;

        public ProjectActionTrace(string action, Action<string> log, TimeSpan? stuckAfter = null, TimeSpan? slowStep = null)
        {
            this.action = action;
            this.log = log ?? (_ => { });
            this.slowStep = slowStep ?? SlowStep;
            TimeSpan period = stuckAfter ?? StuckAfter;
            // Polled at a fraction of the threshold so a stuck step is reported close to the threshold.
            TimeSpan tick = TimeSpan.FromMilliseconds(Math.Max(10, period.TotalMilliseconds / 4));
            watchdog = new Timer(_ => ReportIfStuck(period), null, tick, tick);
        }

        /// <summary>The step running now, for a response that has to say the action is still settling.</summary>
        public string? CurrentStep { get { lock (sync) return currentStep; } }

        public IReadOnlyList<(string Name, double Ms)> Steps
        {
            get
            {
                lock (sync)
                    return steps.Select(s => (s.Name, s.Ticks * 1000d / Stopwatch.Frequency)).ToList();
            }
        }

        public void Step(string projectID, string name, Action body) => Step<bool>(projectID, name, () => { body(); return true; });

        public T Step<T>(string projectID, string name, Func<T> body)
        {
            long start = Stopwatch.GetTimestamp();
            lock (sync)
            {
                currentStep = name;
                currentProject = projectID;
                currentStart = start;
                stuckReported = false;
            }
            try { return body(); }
            finally
            {
                long ticks = Stopwatch.GetTimestamp() - start;
                lock (sync)
                {
                    steps.Add((name, start, ticks));
                    currentStep = null;
                    currentProject = null;
                }
                double seconds = ticks / (double)Stopwatch.Frequency;
                if (seconds >= slowStep.TotalSeconds)
                    log($"[Projects] {action}: step '{name}' for project {projectID} took {seconds:0.0} s.");
            }
        }

        /// <summary>Copies the step timings onto the request's trace (as projects-&lt;step&gt; spans).</summary>
        public void AttachSpans(RequestTrace? trace)
        {
            if (trace == null) return;
            lock (sync)
                foreach (var (name, start, ticks) in steps)
                    trace.AddSpan("projects-" + name, start, ticks);
        }

        private void ReportIfStuck(TimeSpan threshold)
        {
            string? step, project;
            double seconds;
            lock (sync)
            {
                if (disposed || currentStep == null || stuckReported) return;
                seconds = (Stopwatch.GetTimestamp() - currentStart) / (double)Stopwatch.Frequency;
                if (seconds < threshold.TotalSeconds) return;
                stuckReported = true;
                step = currentStep;
                project = currentProject;
            }
            log($"[Projects] {action} has been in step '{step}' for project {project} for {seconds:0} s and is still waiting.");
        }

        public void Dispose()
        {
            lock (sync) disposed = true;
            watchdog?.Dispose();
        }
    }
}
