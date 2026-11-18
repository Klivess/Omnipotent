namespace Omnipotent.Services.KliveMail
{
    /// <summary>
    /// Whether KliveMail is doing its job right now, for the mail page and the next person who asks:
    /// is the SMTP listener up, is anyone connecting, did the last message store, when.
    ///
    /// Before this a listener that crashed logged one error and stayed down until the next process
    /// start, and nothing anywhere showed that mail had stopped arriving. Counters cover the current
    /// process; when mail last arrived comes from the store itself (MailSummary.LastReceivedUtc).
    /// </summary>
    public sealed class KliveMailHealth
    {
        private readonly object gate = new();
        private readonly Func<DateTime> clock;

        public KliveMailHealth(Func<DateTime>? clock = null) { this.clock = clock ?? (() => DateTime.UtcNow); }

        private string smtpState = "starting";
        private DateTime smtpSinceUtc;
        private bool? smtpTls;
        private int smtpRestarts;
        private string? smtpLastError;
        private DateTime? smtpLastErrorUtc;
        private long sessions;
        private long sessionFaults;
        private DateTime? lastSessionUtc;
        private long stored;
        private DateTime? lastStoredUtc;
        private long storeErrors;
        private string? lastStoreError;
        private DateTime? lastStoreErrorUtc;
        private int? digestsBuilt;
        private long? digestMs;
        private DateTime startedUtc = DateTime.UtcNow;

        public void Started() { lock (gate) { startedUtc = clock(); smtpSinceUtc = startedUtc; } }

        public void SmtpListening(bool tls)
        {
            lock (gate) { smtpState = "listening"; smtpSinceUtc = clock(); smtpTls = tls; }
        }

        public void SmtpRestarting()
        {
            lock (gate) { smtpState = "restarting"; smtpSinceUtc = clock(); smtpRestarts++; }
        }

        public void SmtpFailed(string error)
        {
            lock (gate) { smtpState = "failed"; smtpSinceUtc = clock(); smtpLastError = Clip(error); smtpLastErrorUtc = smtpSinceUtc; }
        }

        public void SmtpStopped() { lock (gate) { smtpState = "stopped"; smtpSinceUtc = clock(); } }

        public void SessionStarted() { lock (gate) { sessions++; lastSessionUtc = clock(); } }

        /// <summary>Port 25 is scanned constantly; a faulted session is normal noise, counted, never alarmed on.</summary>
        public void SessionFaulted() { lock (gate) { sessionFaults++; } }

        public void MessageStored() { lock (gate) { stored++; lastStoredUtc = clock(); } }

        public void StoreFailed(string error)
        {
            lock (gate) { storeErrors++; lastStoreError = Clip(error); lastStoreErrorUtc = clock(); }
        }

        public void DigestsBuilt(int count, long ms) { lock (gate) { digestsBuilt = count; digestMs = ms; } }

        public string SmtpState { get { lock (gate) return smtpState; } }

        /// <summary>The shape the page reads. Every field is optional on the page's side.</summary>
        public object View()
        {
            lock (gate)
            {
                return new
                {
                    startedUtc,
                    smtp = new
                    {
                        state = smtpState,
                        sinceUtc = smtpSinceUtc,
                        port = KliveMail.SmtpPort,
                        tls = smtpTls,
                        restarts = smtpRestarts,
                        lastError = smtpLastError,
                        lastErrorUtc = smtpLastErrorUtc,
                        sessions,
                        sessionFaults,
                        lastSessionUtc,
                    },
                    store = new
                    {
                        stored,
                        lastStoredUtc,
                        errors = storeErrors,
                        lastError = lastStoreError,
                        lastErrorUtc = lastStoreErrorUtc,
                    },
                    digests = digestsBuilt == null ? null : new { built = digestsBuilt, ms = digestMs },
                };
            }
        }

        private static string Clip(string text) => text.Length <= 300 ? text : text[..300] + "…";
    }
}
