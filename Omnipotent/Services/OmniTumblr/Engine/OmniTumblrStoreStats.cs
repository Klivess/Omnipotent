namespace Omnipotent.Services.OmniTumblr.Engine
{
    /// <summary>What the store has been doing: how long routes waited for its lock and what background saves cost.</summary>
    public sealed class OmniTumblrStoreStats
    {
        public DateTime SinceUtc { get; set; }
        /// <summary>Lock waits longer than 2 ms since the start, the longest of them, and those in the last hour.</summary>
        public long Waits { get; set; }
        public double MaxWaitMs { get; set; }
        public DateTime? MaxWaitUtc { get; set; }
        public int WaitsLastHour { get; set; }
        public double MaxWaitLastHourMs { get; set; }
        public long Saves { get; set; }
        public DateTime? LastSaveUtc { get; set; }
        public double LastSaveMs { get; set; }
        /// <summary>How long the last save held the lock while it serialised the small documents.</summary>
        public double LastSaveLockMs { get; set; }
        public double MaxSaveLockMs { get; set; }
        public long LastSaveBytes { get; set; }
        public int? Fingerprints { get; set; }
        public long? FingerprintBytes { get; set; }
        public double? FingerprintSerialiseMs { get; set; }
        public DateTime? FingerprintsSavedUtc { get; set; }
    }

    internal sealed class OmniTumblrStoreStatsRecorder
    {
        private readonly object sync = new();
        private readonly Queue<(DateTime At, double Ms)> recentWaits = new();
        private readonly OmniTumblrStoreStats totals = new() { SinceUtc = DateTime.UtcNow };

        public void NoteWait(double ms)
        {
            DateTime now = DateTime.UtcNow;
            lock (sync)
            {
                totals.Waits++;
                if (ms > totals.MaxWaitMs)
                {
                    totals.MaxWaitMs = Math.Round(ms, 1);
                    totals.MaxWaitUtc = now;
                }
                recentWaits.Enqueue((now, ms));
                while (recentWaits.Count > 0 && (now - recentWaits.Peek().At > TimeSpan.FromHours(1) || recentWaits.Count > 5000)) recentWaits.Dequeue();
            }
        }

        public void NoteFlush(double ms, double lockMs, long bytes, int documents, int? fingerprints, long? fingerprintBytes, double fingerprintMs)
        {
            lock (sync)
            {
                totals.Saves++;
                totals.LastSaveUtc = DateTime.UtcNow;
                totals.LastSaveMs = Math.Round(ms, 1);
                totals.LastSaveLockMs = Math.Round(lockMs, 2);
                totals.MaxSaveLockMs = Math.Max(totals.MaxSaveLockMs, Math.Round(lockMs, 2));
                totals.LastSaveBytes = bytes;
                if (fingerprints != null)
                {
                    totals.Fingerprints = fingerprints;
                    totals.FingerprintBytes = fingerprintBytes;
                    totals.FingerprintSerialiseMs = Math.Round(fingerprintMs, 1);
                    totals.FingerprintsSavedUtc = totals.LastSaveUtc;
                }
            }
        }

        public OmniTumblrStoreStats Snapshot()
        {
            DateTime now = DateTime.UtcNow;
            lock (sync)
            {
                var hour = recentWaits.Where(w => now - w.At <= TimeSpan.FromHours(1)).ToList();
                return new OmniTumblrStoreStats
                {
                    SinceUtc = totals.SinceUtc,
                    Waits = totals.Waits,
                    MaxWaitMs = totals.MaxWaitMs,
                    MaxWaitUtc = totals.MaxWaitUtc,
                    WaitsLastHour = hour.Count,
                    MaxWaitLastHourMs = hour.Count == 0 ? 0 : Math.Round(hour.Max(w => w.Ms), 1),
                    Saves = totals.Saves,
                    LastSaveUtc = totals.LastSaveUtc,
                    LastSaveMs = totals.LastSaveMs,
                    LastSaveLockMs = totals.LastSaveLockMs,
                    MaxSaveLockMs = totals.MaxSaveLockMs,
                    LastSaveBytes = totals.LastSaveBytes,
                    Fingerprints = totals.Fingerprints,
                    FingerprintBytes = totals.FingerprintBytes,
                    FingerprintSerialiseMs = totals.FingerprintSerialiseMs,
                    FingerprintsSavedUtc = totals.FingerprintsSavedUtc,
                };
            }
        }
    }
}
