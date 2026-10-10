namespace Omnipotent.Services.OmniTumblr.Api
{
    public sealed class TumblrEdgeGateStatus
    {
        public bool Blocked { get; set; }
        public DateTime? BlockedSinceUtc { get; set; }
        /// <summary>When background work next tries Tumblr (one probe call).</summary>
        public DateTime? NextCheckUtc { get; set; }
        public int FailedChecks { get; set; }
        public DateTime? LastRefusedUtc { get; set; }
        public string? LastDetail { get; set; }
        public DateTime? LastClearedUtc { get; set; }
        public double? LastBlockMinutes { get; set; }
    }

    /// <summary>
    /// Tumblr's edge proxy sometimes refuses every request from this server's IP for hours: a bare nginx 403
    /// that never reaches the API (Oct 8–10 2026, while the same requests and app key from another network
    /// were answered). Retrying each post and sync job into it spent every post's attempts within the hour
    /// and kept ~90 doomed calls an hour going at the block. So while the edge refuses, background work
    /// waits here and a single probe call goes out on a backoff; the first real answer from Tumblr reopens it.
    /// The client reports every response; the publisher and sync loops ask <see cref="TryEnter"/> first.
    /// </summary>
    public sealed class TumblrEdgeGate
    {
        internal static readonly TimeSpan[] ProbeBackoff =
        {
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(30),
        };
        /// <summary>How long the other callers wait on a probe that never reports (it failed before a response).</summary>
        internal static readonly TimeSpan ProbeLease = TimeSpan.FromMinutes(2);

        private readonly object sync = new();
        private readonly Func<DateTime> clock;
        private DateTime? blockedSince;
        private DateTime? nextProbe;
        private int strikes;
        private DateTime? lastRefused;
        private string? lastDetail;
        private DateTime? lastCleared;
        private TimeSpan? lastBlockLength;

        public TumblrEdgeGate(Func<DateTime>? clock = null) => this.clock = clock ?? (() => DateTime.UtcNow);

        /// <summary>Raised (outside the lock) when the edge starts refusing and when it answers again.</summary>
        public Action<TumblrEdgeGateStatus>? Changed { get; set; }

        public bool IsBlocked { get { lock (sync) return blockedSince != null; } }

        /// <summary>When background work should try again, or null when Tumblr is answering.</summary>
        public DateTime? RetryAtUtc { get { lock (sync) return blockedSince == null ? null : nextProbe; } }

        /// <summary>
        /// Whether background work may call Tumblr now. While the edge refuses, one caller per probe window
        /// gets true — it is the probe — and everyone else gets the time to come back.
        /// </summary>
        public bool TryEnter(out DateTime retryAtUtc)
        {
            lock (sync)
            {
                DateTime now = clock();
                retryAtUtc = now;
                if (blockedSince == null) return true;
                if (nextProbe is DateTime at && now < at)
                {
                    retryAtUtc = at;
                    return false;
                }
                nextProbe = now + ProbeLease;
                return true;
            }
        }

        /// <summary>Lets the next caller through at once (someone pressed publish now, or the route changed).</summary>
        public void ProbeNow()
        {
            lock (sync)
                if (blockedSince != null) nextProbe = clock();
        }

        /// <summary>A response from Tumblr: an HTML 403 from its edge, or anything else (the API answered).</summary>
        public void Observe(int status, bool edgeRefusal, string detail)
        {
            if (edgeRefusal) RecordRefused(detail);
            else if (status > 0) RecordAnswered();
        }

        public void RecordRefused(string detail)
        {
            TumblrEdgeGateStatus? started = null;
            lock (sync)
            {
                DateTime now = clock();
                lastRefused = now;
                lastDetail = detail;
                if (blockedSince == null)
                {
                    blockedSince = now;
                    strikes = 1;
                    started = SnapshotLocked();
                }
                // A call already in flight when the block began is not a probe: it must not lengthen the wait.
                else if (nextProbe is DateTime at && at - now > ProbeLease) return;
                else strikes++;
                nextProbe = now + ProbeBackoff[Math.Min(strikes - 1, ProbeBackoff.Length - 1)];
                if (started != null) started.NextCheckUtc = nextProbe;
            }
            if (started != null) Raise(started);
        }

        public void RecordAnswered()
        {
            TumblrEdgeGateStatus? cleared = null;
            lock (sync)
            {
                if (blockedSince == null) return;
                DateTime now = clock();
                lastCleared = now;
                lastBlockLength = now - blockedSince.Value;
                blockedSince = null;
                nextProbe = null;
                strikes = 0;
                cleared = SnapshotLocked();
            }
            Raise(cleared);
        }

        public TumblrEdgeGateStatus Snapshot()
        {
            lock (sync) return SnapshotLocked();
        }

        private TumblrEdgeGateStatus SnapshotLocked() => new()
        {
            Blocked = blockedSince != null,
            BlockedSinceUtc = blockedSince,
            NextCheckUtc = blockedSince == null ? null : nextProbe,
            FailedChecks = strikes,
            LastRefusedUtc = lastRefused,
            LastDetail = lastDetail,
            LastClearedUtc = lastCleared,
            LastBlockMinutes = lastBlockLength is TimeSpan length ? Math.Round(length.TotalMinutes, 1) : null,
        };

        private void Raise(TumblrEdgeGateStatus status)
        {
            try { Changed?.Invoke(status); } catch { }
        }
    }
}
