using System.Diagnostics;

namespace Omnipotent.Services.KliveGames.Runtime
{
    /// <summary>
    /// Computes per-process CPU% and RAM, keeping the previous sample so CPU can be derived from the
    /// delta of <see cref="Process.TotalProcessorTime"/> over wall-clock time. Exit-safe: returns zeros
    /// (never throws) once the process has exited.
    /// </summary>
    public sealed class ResourceSampler
    {
        private TimeSpan _lastCpu;
        private DateTime _lastSampleUtc;
        private bool _initialized;

        public (double cpuPercent, long ramBytes) Sample(Process? process)
        {
            if (process == null) return (0, 0);
            try
            {
                process.Refresh();
                if (process.HasExited) return (0, 0);

                long ram = process.WorkingSet64;
                TimeSpan cpu = process.TotalProcessorTime;
                DateTime now = DateTime.UtcNow;

                double cpuPercent = 0;
                if (_initialized)
                {
                    double wallMs = (now - _lastSampleUtc).TotalMilliseconds;
                    double cpuMs = (cpu - _lastCpu).TotalMilliseconds;
                    if (wallMs > 0)
                    {
                        int cores = Math.Max(1, Environment.ProcessorCount);
                        cpuPercent = Math.Clamp(cpuMs / (wallMs * cores) * 100.0, 0, 100);
                    }
                }

                _lastCpu = cpu;
                _lastSampleUtc = now;
                _initialized = true;
                return (cpuPercent, ram);
            }
            catch
            {
                // Win32Exception / InvalidOperationException once the process is gone.
                return (0, 0);
            }
        }
    }
}
