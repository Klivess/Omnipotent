namespace Omnipotent.Services.OmniTrader.Venues
{
    /// <summary>
    /// Stops a venue whose credentials the broker has rejected from retrying on every single call.
    ///
    /// Without this, one wrong key produces a failure per request forever: the command centre polls
    /// the firm view every fifteen seconds, each poll asks every derivative venue for its account,
    /// and each of those attempts a fresh login. A single mis-scoped key reached fifty-seven failed
    /// logins in eight minutes — which is both a self-inflicted denial of service against the broker
    /// (they rate-limit, and some lock the account) and a wall of identical alerts that buries
    /// whatever else is wrong.
    ///
    /// Rejected credentials are not a transient fault, so retrying cannot fix them. The breaker
    /// opens after a few consecutive rejections and stays open until either the cooldown expires or
    /// an operator explicitly reconnects — which is the only action that can actually change the
    /// outcome, because it follows them changing the key.
    ///
    /// Only *authentication* failures open it. A timeout or a 500 is transient and must keep
    /// retrying, or a blip would take the venue offline until someone noticed.
    /// </summary>
    public sealed class AuthCircuitBreaker
    {
        private readonly object gate = new();
        private int consecutiveRejections;
        private DateTime? openedUtc;
        private string reason = "";

        public int RejectionsBeforeOpening { get; init; } = 3;
        public TimeSpan Cooldown { get; init; } = TimeSpan.FromMinutes(15);

        public bool IsOpen
        {
            get
            {
                lock (gate)
                {
                    if (openedUtc == null) return false;
                    if (DateTime.UtcNow - openedUtc.Value < Cooldown) return true;
                    // Cooldown served: allow exactly one probe rather than reopening the floodgates.
                    openedUtc = null;
                    consecutiveRejections = RejectionsBeforeOpening - 1;
                    return false;
                }
            }
        }

        /// <summary>Why the breaker is open, phrased for an operator rather than a log.</summary>
        public string Reason
        {
            get
            {
                lock (gate)
                {
                    if (openedUtc == null) return "";
                    var retryAt = openedUtc.Value + Cooldown;
                    return $"{reason} Retries are paused until {retryAt:HH:mm} UTC to avoid hammering the broker — "
                         + "fix the credential in Omni settings, then use Reconnect venues.";
                }
            }
        }

        public void RecordSuccess()
        {
            lock (gate)
            {
                consecutiveRejections = 0;
                openedUtc = null;
                reason = "";
            }
        }

        /// <summary>Record a rejection the broker will keep making until the credential changes.</summary>
        public void RecordRejection(string detail)
        {
            lock (gate)
            {
                consecutiveRejections++;
                reason = detail;
                if (consecutiveRejections >= RejectionsBeforeOpening && openedUtc == null)
                    openedUtc = DateTime.UtcNow;
            }
        }

        /// <summary>An operator has changed something and asked us to try again.</summary>
        public void Reset()
        {
            lock (gate)
            {
                consecutiveRejections = 0;
                openedUtc = null;
                reason = "";
            }
        }

        /// <summary>
        /// Is this status the broker saying "this credential is wrong"? Anything else — a timeout, a
        /// 500, a rate limit — is transient and must not open the breaker.
        /// </summary>
        public static bool IsRejection(System.Net.HttpStatusCode status)
            => status is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden;
    }
}
