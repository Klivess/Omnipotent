namespace Omnipotent.Services.KliveGames.Runtime
{
    /// <summary>A window of vitals in columns, oldest first. A null is a bucket with no sample (the server
    /// was not running, or Omnipotent was not).</summary>
    public sealed class VitalsSeries
    {
        public long T0 { get; set; }          // unix ms of the first bucket
        public int StepSeconds { get; set; }
        public double?[] Cpu { get; set; } = Array.Empty<double?>();
        public int?[] RamMb { get; set; } = Array.Empty<int?>();
        public int?[] Players { get; set; } = Array.Empty<int?>();
    }

    /// <summary>
    /// What a running server used, kept in memory only: a fine ring (15 s buckets, one hour) for the live
    /// sparkline and a coarse one (5 min buckets, a day) for "what did it use today". Each bucket keeps the
    /// mean CPU, the peak RAM and the peak player count of the samples that fell in it. Fed by the monitor
    /// loop's existing 4-second sample; lost on restart, which is fine for a sparkline.
    /// </summary>
    public sealed class VitalsRing
    {
        private sealed class Ring
        {
            public readonly int Step;
            public readonly long[] Bucket;
            public readonly double[] CpuSum;
            public readonly int[] N;
            public readonly int[] RamMb;
            public readonly int[] Players;

            public Ring(int stepSeconds, int size)
            {
                Step = stepSeconds;
                Bucket = Enumerable.Repeat(long.MinValue, size).ToArray();
                CpuSum = new double[size];
                N = new int[size];
                RamMb = new int[size];
                Players = new int[size];
            }

            public void Add(long unixSeconds, double cpu, int ramMb, int players)
            {
                long b = unixSeconds / Step;
                int slot = (int)(b % Bucket.Length);
                if (Bucket[slot] != b)
                {
                    Bucket[slot] = b;
                    CpuSum[slot] = 0;
                    N[slot] = 0;
                    RamMb[slot] = 0;
                    Players[slot] = 0;
                }
                CpuSum[slot] += cpu;
                N[slot]++;
                RamMb[slot] = Math.Max(RamMb[slot], ramMb);
                Players[slot] = Math.Max(Players[slot], players);
            }

            public VitalsSeries Snapshot(long nowUnixSeconds, int buckets)
            {
                buckets = Math.Clamp(buckets, 1, Bucket.Length);
                long last = nowUnixSeconds / Step;
                long first = last - buckets + 1;
                var s = new VitalsSeries
                {
                    T0 = first * Step * 1000,
                    StepSeconds = Step,
                    Cpu = new double?[buckets],
                    RamMb = new int?[buckets],
                    Players = new int?[buckets],
                };
                for (int i = 0; i < buckets; i++)
                {
                    long b = first + i;
                    int slot = (int)(((b % Bucket.Length) + Bucket.Length) % Bucket.Length);
                    if (Bucket[slot] != b || N[slot] == 0) continue;
                    s.Cpu[i] = Math.Round(CpuSum[slot] / N[slot], 1);
                    s.RamMb[i] = RamMb[slot];
                    s.Players[i] = Players[slot];
                }
                return s;
            }
        }

        private readonly object _gate = new();
        private readonly Ring _hour = new(15, 240);
        private readonly Ring _day = new(300, 288);

        public DateTime? LastSampleUtc { get; private set; }

        public void Add(DateTime nowUtc, double cpuPercent, long ramBytes, int players)
        {
            long t = new DateTimeOffset(nowUtc).ToUnixTimeSeconds();
            int ramMb = (int)Math.Max(0, ramBytes / (1024 * 1024));
            lock (_gate)
            {
                _hour.Add(t, cpuPercent, ramMb, players);
                _day.Add(t, cpuPercent, ramMb, players);
                LastSampleUtc = nowUtc;
            }
        }

        /// <summary><paramref name="window"/>: "1h" (15 s buckets) or "24h" (5 min buckets);
        /// <paramref name="buckets"/> trims to the newest N.</summary>
        public VitalsSeries Snapshot(string window, DateTime nowUtc, int? buckets = null)
        {
            long t = new DateTimeOffset(nowUtc).ToUnixTimeSeconds();
            lock (_gate)
            {
                return window == "24h"
                    ? _day.Snapshot(t, buckets ?? 288)
                    : _hour.Snapshot(t, buckets ?? 240);
            }
        }

        /// <summary>Peak players across the day ring (0 when nothing ran).</summary>
        public int PeakPlayers(DateTime nowUtc)
        {
            var day = Snapshot("24h", nowUtc);
            int peak = 0;
            foreach (var p in day.Players) if (p is int v && v > peak) peak = v;
            return peak;
        }
    }
}
