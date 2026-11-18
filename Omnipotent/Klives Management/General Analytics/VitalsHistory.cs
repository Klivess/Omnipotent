namespace Omnipotent.Klives_Management.General_Analytics
{
    /// <summary>
    /// The last hour of host and process vitals, one sample per statistics refresh. The admin page draws
    /// its trend lines from this, so they are complete the moment the page opens instead of filling in
    /// while it stays open. Fixed size: once full, the oldest sample is overwritten.
    /// </summary>
    public sealed class VitalsHistory
    {
        public readonly record struct Sample(
            DateTime Utc,
            double CpuPct,
            double RamPct,
            double ProcessMB,
            double HeapMB,
            int Threads,
            int Gen2Collections,
            long NetSentBytes,
            long NetReceivedBytes,
            long ErrorsLogged,
            long ApiRequests,
            long ApiServerErrors);

        /// <summary>
        /// Column-per-metric so an hour of samples stays small on the wire. Counters (collections, bytes,
        /// errors, requests) are running totals: the reader takes differences to get a rate.
        /// </summary>
        public sealed class Series
        {
            public int StepSeconds;
            public int Count;
            public long[] T = [];
            public double[] Cpu = [];
            public double[] RamPct = [];
            public double[] ProcessMB = [];
            public double[] HeapMB = [];
            public int[] Threads = [];
            public int[] Gen2 = [];
            public long[] NetSent = [];
            public long[] NetReceived = [];
            public long[] Errors = [];
            public long[] Requests = [];
            public long[] ServerErrors = [];
        }

        private readonly Sample[] ring;
        private readonly int stepSeconds;
        private readonly object gate = new();
        private int next;
        private int count;

        public VitalsHistory(int capacity = 720, int stepSeconds = 5)
        {
            ring = new Sample[Math.Max(1, capacity)];
            this.stepSeconds = Math.Max(1, stepSeconds);
        }

        public int Capacity => ring.Length;

        public void Append(Sample sample)
        {
            lock (gate)
            {
                ring[next] = sample;
                next = (next + 1) % ring.Length;
                if (count < ring.Length) count++;
            }
        }

        /// <summary>Samples no older than <paramref name="window"/>, oldest first.</summary>
        public Series Read(TimeSpan window, DateTime nowUtc)
        {
            Sample[] ordered;
            lock (gate)
            {
                ordered = new Sample[count];
                int start = (next - count + ring.Length) % ring.Length;
                for (int i = 0; i < count; i++) ordered[i] = ring[(start + i) % ring.Length];
            }

            DateTime cutoff = nowUtc - window;
            int first = 0;
            while (first < ordered.Length && ordered[first].Utc < cutoff) first++;
            int n = ordered.Length - first;

            var series = new Series
            {
                StepSeconds = stepSeconds,
                Count = n,
                T = new long[n],
                Cpu = new double[n],
                RamPct = new double[n],
                ProcessMB = new double[n],
                HeapMB = new double[n],
                Threads = new int[n],
                Gen2 = new int[n],
                NetSent = new long[n],
                NetReceived = new long[n],
                Errors = new long[n],
                Requests = new long[n],
                ServerErrors = new long[n],
            };
            for (int i = 0; i < n; i++)
            {
                Sample s = ordered[first + i];
                series.T[i] = new DateTimeOffset(DateTime.SpecifyKind(s.Utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
                series.Cpu[i] = Math.Round(s.CpuPct, 1);
                series.RamPct[i] = Math.Round(s.RamPct, 1);
                series.ProcessMB[i] = Math.Round(s.ProcessMB, 0);
                series.HeapMB[i] = Math.Round(s.HeapMB, 0);
                series.Threads[i] = s.Threads;
                series.Gen2[i] = s.Gen2Collections;
                series.NetSent[i] = s.NetSentBytes;
                series.NetReceived[i] = s.NetReceivedBytes;
                series.Errors[i] = s.ErrorsLogged;
                series.Requests[i] = s.ApiRequests;
                series.ServerErrors[i] = s.ApiServerErrors;
            }
            return series;
        }
    }
}
