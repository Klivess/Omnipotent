using System;
using System.Diagnostics;
using System.Threading;

namespace Omnipotent.Services.KliveAPI.Telemetry
{
    /// <summary>
    /// Samples process/runtime gauges once a second on a DEDICATED OS thread (never the
    /// thread pool — the pool's health is one of the things being measured, and a starved
    /// pool must not also starve its own diagnostics). Rates are derived from deltas
    /// between consecutive samples.
    /// </summary>
    internal sealed class RuntimeSampler
    {
        public sealed class Inputs
        {
            public Func<int> InFlight = () => 0;
            public Func<long> AcceptsTotal = () => 0;
            public Func<int> CacheEntries = () => 0;
            public Func<long> CacheBytes = () => 0;
        }

        private readonly ApiTelemetry _telemetry;
        private readonly Inputs _inputs;
        private Thread? _thread;
        private volatile bool _running;

        private long _prevTimestamp;
        private TimeSpan _prevCpu;
        private TimeSpan _prevPause;
        private long _prevAlloc;
        private int _prevGen0, _prevGen2;
        private long _prevContention;
        private long _prevAccepts;

        public RuntimeSampler(ApiTelemetry telemetry, Inputs inputs)
        {
            _telemetry = telemetry;
            _inputs = inputs;
        }

        public void Start()
        {
            if (_thread is { IsAlive: true }) return;
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "KliveAPI_RuntimeSampler" };
            _thread.Start();
        }

        public void Stop() => _running = false;

        private void Loop()
        {
            using var process = Process.GetCurrentProcess();
            Prime(process);
            while (_running)
            {
                try { Thread.Sleep(1000); } catch (ThreadInterruptedException) { return; }
                try { _telemetry.RecordRuntimeSample(Sample(process)); }
                catch { /* sampling must never take anything down */ }
            }
        }

        private void Prime(Process process)
        {
            _prevTimestamp = Stopwatch.GetTimestamp();
            try { process.Refresh(); _prevCpu = process.TotalProcessorTime; } catch { }
            _prevPause = GC.GetTotalPauseDuration();
            _prevAlloc = GC.GetTotalAllocatedBytes(false);
            _prevGen0 = GC.CollectionCount(0);
            _prevGen2 = GC.CollectionCount(2);
            _prevContention = Monitor.LockContentionCount;
            _prevAccepts = _inputs.AcceptsTotal();
        }

        internal double[] Sample(Process process)
        {
            long now = Stopwatch.GetTimestamp();
            double seconds = Math.Max(0.001, (now - _prevTimestamp) / (double)Stopwatch.Frequency);
            _prevTimestamp = now;

            var s = new double[RuntimeGauges.Count];
            s[(int)RuntimeGauge.InFlight] = _inputs.InFlight();

            ThreadPool.GetAvailableThreads(out int availWorker, out _);
            ThreadPool.GetMaxThreads(out int maxWorker, out _);
            s[(int)RuntimeGauge.WorkerThreadsBusy] = maxWorker - availWorker;
            s[(int)RuntimeGauge.PoolThreads] = ThreadPool.ThreadCount;
            s[(int)RuntimeGauge.PendingWorkItems] = ThreadPool.PendingWorkItemCount;
            s[(int)RuntimeGauge.HeapMB] = GC.GetTotalMemory(false) / (1024.0 * 1024.0);

            try
            {
                process.Refresh();
                s[(int)RuntimeGauge.WorkingSetMB] = process.WorkingSet64 / (1024.0 * 1024.0);
                TimeSpan cpu = process.TotalProcessorTime;
                s[(int)RuntimeGauge.CpuPct] = Math.Clamp((cpu - _prevCpu).TotalSeconds / seconds / Environment.ProcessorCount * 100, 0, 100);
                _prevCpu = cpu;
            }
            catch { }

            TimeSpan pause = GC.GetTotalPauseDuration();
            s[(int)RuntimeGauge.GcPauseMsPerSec] = (pause - _prevPause).TotalMilliseconds / seconds;
            _prevPause = pause;

            long alloc = GC.GetTotalAllocatedBytes(false);
            s[(int)RuntimeGauge.AllocMBPerSec] = Math.Max(0, alloc - _prevAlloc) / (1024.0 * 1024.0) / seconds;
            _prevAlloc = alloc;

            int gen0 = GC.CollectionCount(0), gen2 = GC.CollectionCount(2);
            s[(int)RuntimeGauge.Gen0PerMin] = (gen0 - _prevGen0) * 60 / seconds;
            s[(int)RuntimeGauge.Gen2PerMin] = (gen2 - _prevGen2) * 60 / seconds;
            _prevGen0 = gen0;
            _prevGen2 = gen2;

            long contention = Monitor.LockContentionCount;
            s[(int)RuntimeGauge.LockContentionPerSec] = Math.Max(0, contention - _prevContention) / seconds;
            _prevContention = contention;

            long accepts = _inputs.AcceptsTotal();
            s[(int)RuntimeGauge.AcceptsPerSec] = Math.Max(0, accepts - _prevAccepts) / seconds;
            _prevAccepts = accepts;

            s[(int)RuntimeGauge.CacheEntries] = _inputs.CacheEntries();
            s[(int)RuntimeGauge.CacheMB] = _inputs.CacheBytes() / (1024.0 * 1024.0);
            s[(int)RuntimeGauge.TelemetryBacklog] = _telemetry.Backlog;
            return s;
        }
    }
}
