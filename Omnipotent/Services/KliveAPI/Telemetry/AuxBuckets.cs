using System;
using System.IO;

namespace Omnipotent.Services.KliveAPI.Telemetry
{
    /// <summary>Client-side (browser Resource Timing) phases reported by the website.</summary>
    public enum RumPhase
    {
        Blocked = 0,   // browser queueing before DNS (fetchStart → domainLookupStart)
        Dns = 1,
        Tcp = 2,
        Tls = 3,
        Network = 4,   // TTFB minus the server's own pre-write time: RTT + kernel queue
        Server = 5,    // Server-Timing app;dur as seen by the client
        Download = 6,  // responseStart → responseEnd
        Total = 7,     // fetch start → responseEnd
    }

    public static class RumPhases
    {
        public const int Count = 8;
        public static readonly string[] Keys = { "blocked", "dns", "tcp", "tls", "network", "server", "download", "total" };
        public static readonly string[] Labels = { "Browser queue", "DNS", "TCP connect", "TLS", "Network wait", "Server", "Download", "Total" };
    }

    /// <summary>One client-observed request, parsed + clamped from a RUM beacon.</summary>
    public sealed class RumSample
    {
        public string Route = "/";
        public string Method = "GET";
        public long TraceId;
        public long TimestampUtcMs;
        public bool Reused;
        public long TransferBytes;
        public readonly long[] PhaseMicros = new long[RumPhases.Count];
    }

    /// <summary>Additive aggregate of RUM samples for one series in one window.</summary>
    public sealed class RumBucket
    {
        public long Count;
        public long Reused;
        public long TransferBytes;
        public readonly LogHistogram[] Phases = new LogHistogram[RumPhases.Count];

        public RumBucket()
        {
            for (int i = 0; i < Phases.Length; i++) Phases[i] = new LogHistogram();
        }

        public void Fold(RumSample s)
        {
            Count++;
            if (s.Reused) Reused++;
            TransferBytes += Math.Max(0, s.TransferBytes);
            for (int i = 0; i < Phases.Length; i++) Phases[i].Add(s.PhaseMicros[i]);
        }

        public void Merge(RumBucket? o)
        {
            if (o == null || o.Count == 0) return;
            Count += o.Count;
            Reused += o.Reused;
            TransferBytes += o.TransferBytes;
            for (int i = 0; i < Phases.Length; i++) Phases[i].Merge(o.Phases[i]);
        }

        public RumBucket Freeze()
        {
            foreach (var h in Phases) h.Freeze();
            return this;
        }

        public byte[] Serialize()
        {
            using var ms = new MemoryStream(128);
            using (var w = new BinaryWriter(ms))
            {
                w.Write((byte)1);
                w.Write7BitEncodedInt64(Count);
                w.Write7BitEncodedInt64(Reused);
                w.Write7BitEncodedInt64(TransferBytes);
                w.Write((byte)Phases.Length);
                foreach (var h in Phases) h.Write(w);
            }
            return ms.ToArray();
        }

        public static RumBucket Deserialize(byte[] data)
        {
            using var r = new BinaryReader(new MemoryStream(data));
            var b = new RumBucket();
            if (r.ReadByte() != 1) throw new InvalidDataException("Unknown RUM bucket format.");
            b.Count = r.Read7BitEncodedInt64();
            b.Reused = r.Read7BitEncodedInt64();
            b.TransferBytes = r.Read7BitEncodedInt64();
            int n = r.ReadByte();
            for (int i = 0; i < n; i++)
            {
                var h = LogHistogram.Read(r);
                if (i < b.Phases.Length) b.Phases[i] = h;
            }
            return b;
        }
    }

    /// <summary>Process/runtime gauges sampled once a second by <see cref="RuntimeSampler"/>.</summary>
    public enum RuntimeGauge
    {
        InFlight = 0,
        WorkerThreadsBusy = 1,
        PoolThreads = 2,
        PendingWorkItems = 3,
        HeapMB = 4,
        WorkingSetMB = 5,
        GcPauseMsPerSec = 6,
        AllocMBPerSec = 7,
        Gen0PerMin = 8,
        Gen2PerMin = 9,
        LockContentionPerSec = 10,
        CpuPct = 11,
        AcceptsPerSec = 12,
        CacheEntries = 13,
        CacheMB = 14,
        TelemetryBacklog = 15,
    }

    public static class RuntimeGauges
    {
        public const int Count = 16;
        public static readonly string[] Keys =
        {
            "inFlight", "workerBusy", "poolThreads", "pendingWork", "heapMB", "workingSetMB",
            "gcPauseMsPerSec", "allocMBPerSec", "gen0PerMin", "gen2PerMin", "lockContentionPerSec",
            "cpuPct", "acceptsPerSec", "cacheEntries", "cacheMB", "telemetryBacklog"
        };
    }

    /// <summary>min/avg/max of every runtime gauge over one window.</summary>
    public sealed class RuntimeBucket
    {
        public long Samples;
        public readonly double[] Min = new double[RuntimeGauges.Count];
        public readonly double[] Max = new double[RuntimeGauges.Count];
        public readonly double[] Sum = new double[RuntimeGauges.Count];

        public void Fold(double[] sample)
        {
            for (int i = 0; i < RuntimeGauges.Count; i++)
            {
                double v = sample[i];
                if (Samples == 0 || v < Min[i]) Min[i] = v;
                if (Samples == 0 || v > Max[i]) Max[i] = v;
                Sum[i] += v;
            }
            Samples++;
        }

        public void Merge(RuntimeBucket? o)
        {
            if (o == null || o.Samples == 0) return;
            for (int i = 0; i < RuntimeGauges.Count; i++)
            {
                if (Samples == 0 || o.Min[i] < Min[i]) Min[i] = o.Min[i];
                if (Samples == 0 || o.Max[i] > Max[i]) Max[i] = o.Max[i];
                Sum[i] += o.Sum[i];
            }
            Samples += o.Samples;
        }

        public double Avg(int gauge) => Samples == 0 ? 0 : Sum[gauge] / Samples;

        public byte[] Serialize()
        {
            using var ms = new MemoryStream(RuntimeGauges.Count * 24 + 16);
            using (var w = new BinaryWriter(ms))
            {
                w.Write((byte)1);
                w.Write7BitEncodedInt64(Samples);
                w.Write((byte)RuntimeGauges.Count);
                for (int i = 0; i < RuntimeGauges.Count; i++)
                {
                    w.Write((float)Min[i]);
                    w.Write((float)Max[i]);
                    w.Write(Sum[i]);
                }
            }
            return ms.ToArray();
        }

        public static RuntimeBucket Deserialize(byte[] data)
        {
            using var r = new BinaryReader(new MemoryStream(data));
            var b = new RuntimeBucket();
            if (r.ReadByte() != 1) throw new InvalidDataException("Unknown runtime bucket format.");
            b.Samples = r.Read7BitEncodedInt64();
            int n = r.ReadByte();
            for (int i = 0; i < n; i++)
            {
                double min = r.ReadSingle(), max = r.ReadSingle(), sum = r.ReadDouble();
                if (i >= RuntimeGauges.Count) continue;
                b.Min[i] = min; b.Max[i] = max; b.Sum[i] = sum;
            }
            return b;
        }
    }
}
