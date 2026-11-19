using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using ComFileTime = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace Omnipotent.Services.OmniscienceV3.Ml.Encoders
{
    /// <summary>Host-wide CPU load (0..1), or null when the platform cannot tell.</summary>
    public interface IHostLoad
    {
        double? CpuLoad();
    }

    /// <summary>
    /// Whole-host CPU load from the operating system's cumulative busy/idle counters (Windows <c>GetSystemTimes</c>,
    /// Linux <c>/proc/stat</c>), sampled at most once per <see cref="MinInterval"/>. The interval is measured with a
    /// monotonic stopwatch, never the wall clock. Thread-safe.
    /// </summary>
    public sealed class HostCpuMonitor : IHostLoad
    {
        public static readonly HostCpuMonitor Shared = new();

        private readonly object gate = new();
        private readonly Stopwatch since = Stopwatch.StartNew();
        private (long Busy, long Total)? previous;
        private double? lastLoad;
        private long lastSampleTicks = long.MinValue;

        public TimeSpan MinInterval { get; init; } = TimeSpan.FromSeconds(1);

        public double? CpuLoad()
        {
            lock (gate)
            {
                long now = since.ElapsedTicks;
                if (lastSampleTicks != long.MinValue && now - lastSampleTicks < MinInterval.TotalSeconds * Stopwatch.Frequency)
                    return lastLoad;
                var current = ReadCounters();
                lastSampleTicks = now;
                if (current == null) return lastLoad = null;
                if (previous is { } p)
                {
                    long total = current.Value.Total - p.Total, busy = current.Value.Busy - p.Busy;
                    if (total > 0) lastLoad = Math.Clamp((double)busy / total, 0, 1);
                }
                previous = current;
                return lastLoad;
            }
        }

        private static (long Busy, long Total)? ReadCounters()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
                    long i = ToLong(idle), k = ToLong(kernel), u = ToLong(user);
                    long total = k + u; // kernel time includes idle time
                    return (total - i, total);
                }
                if (OperatingSystem.IsLinux() && File.Exists("/proc/stat"))
                {
                    string? line = File.ReadLines("/proc/stat").FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
                    if (line == null) return null;
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
                        .Select(s => long.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture)).ToArray();
                    if (parts.Length < 4) return null;
                    long idleAll = parts[3] + (parts.Length > 4 ? parts[4] : 0); // idle + iowait
                    long sum = parts.Sum();
                    return (sum - idleAll, sum);
                }
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or DllNotFoundException or EntryPointNotFoundException)
            {
                // Counters unavailable (sandbox, container without /proc): report "unknown" rather than guessing a load.
                return null;
            }
        }

        private static long ToLong(ComFileTime ft) => ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out ComFileTime idle, out ComFileTime kernel, out ComFileTime user);
    }

    /// <summary>A fixed load, for tests and for hosts where throttling is switched off.</summary>
    public sealed class FixedHostLoad : IHostLoad
    {
        public FixedHostLoad(double? load) => Load = load;
        public double? Load { get; set; }
        public double? CpuLoad() => Load;
    }

    /// <summary>
    /// Plan R1/R2: background CPU work yields when the host is busy. Before each unit of work (one encoder batch, one
    /// head-scoring chunk) the caller waits while the host load is above <see cref="PauseAbove"/>, up to
    /// <see cref="MaxPause"/>; then the unit runs anyway, so a host that is permanently busy still makes slow progress
    /// instead of none. Pauses are counted for the Modules page.
    /// <para>The longest pause was a minute. The server sits above the threshold nearly all day (its other services, and this
    /// engine's own work, which the measure includes), so every unit waited the full minute: 1,440 images a day against
    /// a backlog of 226,000. Fifteen seconds still gives way four times out of five and finishes in weeks, not months.</para>
    /// </summary>
    public sealed class CpuThrottle
    {
        private readonly IHostLoad load;
        private long pauses;
        private long pausedMs;

        public CpuThrottle(IHostLoad load, double pauseAbove = 0.85, TimeSpan? maxPause = null, TimeSpan? poll = null)
        {
            this.load = load;
            PauseAbove = pauseAbove;
            MaxPause = maxPause ?? TimeSpan.FromSeconds(15);
            Poll = poll ?? TimeSpan.FromMilliseconds(500);
        }

        public double PauseAbove { get; set; }
        public TimeSpan MaxPause { get; set; }
        public TimeSpan Poll { get; }
        public long Pauses => Interlocked.Read(ref pauses);
        public long PausedMs => Interlocked.Read(ref pausedMs);

        /// <summary>A throttle that never waits.</summary>
        public static CpuThrottle None => new(new FixedHostLoad(null));

        public async Task WaitAsync(CancellationToken ct)
        {
            if (load.CpuLoad() is not double l || l <= PauseAbove) return;
            Interlocked.Increment(ref pauses);
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < MaxPause)
            {
                await Task.Delay(Poll, ct).ConfigureAwait(false);
                if (load.CpuLoad() is not double now || now <= PauseAbove) break;
            }
            Interlocked.Add(ref pausedMs, (long)sw.Elapsed.TotalMilliseconds);
        }
    }
}
