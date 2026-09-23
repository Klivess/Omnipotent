namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// Memory policy for the desktop fleet. The fleet used to be admitted without any capacity
    /// check: every desktop got a 2 GB ceiling, nothing was ever stopped (the Incus migration had
    /// disabled idle suspension), and on a 16 GB host the Docker VM and Windows fought over the
    /// same RAM until dockerd stopped answering — the 2026-09-10 daemon flaps.
    ///
    /// The invariant here is deliberately about hard limits, not observed usage: the sum of the
    /// running desktops' memory ceilings never exceeds what Docker's VM has minus a daemon reserve.
    /// A desktop that outgrows its ceiling is OOM-killed inside its own cgroup; the VM itself can
    /// never reach global OOM, so dockerd cannot be the victim. Idle desktops are stopped in place
    /// (installed apps, home files and browser profiles survive) so they hold no RAM at all.
    /// Pure functions only — Docker-free and unit-tested.
    /// </summary>
    public static class DesktopCapacityPolicy
    {
        private const long MiB = 1024L * 1024;
        private const long GiB = 1024 * MiB;

        /// <summary>Hard memory ceiling per desktop. XFCE-lite + x11vnc + Chromium idle near
        /// 500–800 MB; the ceiling leaves room for real browsing without letting one desktop
        /// starve the rest.</summary>
        public const long DesktopMemoryLimitBytes = 1536 * MiB;
        /// <summary>Soft floor: under VM memory pressure the kernel reclaims from desktops above
        /// this first, instead of from dockerd or containerd.</summary>
        public const long DesktopMemoryReservationBytes = 512 * MiB;
        /// <summary>Swap a desktop may use on top of its ceiling. Small on purpose: swap thrash is
        /// what made the daemon unresponsive rather than a single desktop failing cleanly.</summary>
        public const long DesktopSwapAllowanceBytes = 512 * MiB;
        /// <summary>RAM kept for dockerd, containerd, the VM kernel and image builds.</summary>
        public const long DaemonReserveBytes = 1 * GiB;
        /// <summary>A desktop kernel OOM should pick a desktop process, never the daemon.</summary>
        public const long DesktopOomScoreAdj = 500;
        public const long DesktopPidsLimit = 4096;

        /// <summary>A desktop used more recently than this is never evicted to admit another.</summary>
        public static readonly TimeSpan MinIdleBeforeEviction = TimeSpan.FromMinutes(3);
        /// <summary>Default idle time after which a desktop is stopped in place to release its RAM.</summary>
        public static readonly TimeSpan DefaultIdleSuspendAfter = TimeSpan.FromMinutes(20);
        /// <summary>A desktop burning more than this share of one core is doing work (a download,
        /// a build, a video render) and is not idle whatever its last tool call says.</summary>
        public const double BusyCpuPercent = 25;

        /// <summary>How many desktops may run at once inside a Docker VM of this size.</summary>
        public static int MaxRunningDesktops(long daemonMemoryTotalBytes)
        {
            if (daemonMemoryTotalBytes <= 0) return 1; // unknown: admit one rather than none
            long usable = daemonMemoryTotalBytes - DaemonReserveBytes;
            return (int)Math.Max(1, usable / DesktopMemoryLimitBytes);
        }

        /// <summary>
        /// Chooses which running desktops to stop so that one more can start. Least recently used
        /// first; a desktop that is pinned (an action in flight, Klives watching or controlling it,
        /// busy CPU) or used within <see cref="MinIdleBeforeEviction"/> is never chosen. Returns
        /// null when the request cannot be admitted even after every eligible eviction.
        /// </summary>
        public static IReadOnlyList<DesktopContainerRecord>? ChooseEvictions(
            IEnumerable<DesktopContainerRecord> running, int slots, string? requesterContainerID,
            DateTime nowUtc, Func<DesktopContainerRecord, bool> isPinned)
        {
            var others = running
                .Where(r => !r.Lost && !r.Suspended
                    && !string.Equals(r.ContainerID, requesterContainerID, StringComparison.Ordinal))
                .ToList();
            int excess = others.Count + 1 - Math.Max(1, slots);
            if (excess <= 0) return Array.Empty<DesktopContainerRecord>();

            var eligible = others
                .Where(r => nowUtc - r.LastUsedAt >= MinIdleBeforeEviction && !isPinned(r))
                .OrderBy(r => r.LastUsedAt)
                .ThenBy(r => r.CreatedAt)
                .Take(excess)
                .ToList();
            return eligible.Count == excess ? eligible : null;
        }

        /// <summary>Whether a desktop has been unused long enough to stop in place.</summary>
        public static bool IsIdle(DesktopContainerRecord record, DateTime nowUtc, TimeSpan idleAfter) =>
            !record.Lost && !record.Suspended && idleAfter > TimeSpan.Zero && nowUtc - record.LastUsedAt >= idleAfter;

        /// <summary>
        /// Memory ceiling for Docker Desktop's WSL VM. Without one, an older WSL build lets the VM
        /// take up to 80% of host RAM, which on this 16 GB host left Windows and Omnipotent paging
        /// (host shells timing out at 120–180 s while the daemon "flapped"). The VM only grows
        /// to what containers use; this bounds how far.
        /// </summary>
        public static long WslMemoryCapBytes(long hostTotalBytes)
        {
            long cap = hostTotalBytes - 6 * GiB;
            cap = Math.Clamp(cap, 3 * GiB, 12 * GiB);
            return cap / GiB * GiB; // whole GiB, as .wslconfig expects
        }

        /// <summary>CPU use as a percentage of one core from a Docker stats sample pair.</summary>
        public static double CpuPercentOfOneCore(ulong totalUsage, ulong previousTotalUsage,
            ulong systemUsage, ulong previousSystemUsage, uint onlineCpus)
        {
            if (totalUsage <= previousTotalUsage || systemUsage <= previousSystemUsage) return 0;
            double cpuDelta = totalUsage - previousTotalUsage;
            double systemDelta = systemUsage - previousSystemUsage;
            return cpuDelta / systemDelta * Math.Max(1u, onlineCpus) * 100.0;
        }
    }
}
