namespace Omnipotent.Services.OmniTumblr.Api
{
    /// <summary>What went wrong, in terms the publisher can act on.</summary>
    public enum TumblrErrorKind
    {
        Unknown,
        /// <summary>No response at all (DNS, TLS, connection reset).</summary>
        Network,
        Timeout,
        /// <summary>401: the token was revoked or expired. The connection needs re-authorizing.</summary>
        Unauthorized,
        Forbidden,
        NotFound,
        BadRequest,
        /// <summary>400.8005: Tumblr cannot accept this media. Retrying the same file will never work.</summary>
        InvalidMedia,
        /// <summary>429: an API rate limit (per consumer key, per IP).</summary>
        RateLimited,
        /// <summary>403.8023: the blog's daily posting limit (250 posts incl. reblogs per user).</summary>
        DailyPostLimit,
        /// <summary>403.8004: the daily media upload limit (250 images per user).</summary>
        DailyMediaLimit,
        /// <summary>403.8011: the daily video limit (20 videos / 60 minutes per user).</summary>
        DailyVideoLimit,
        /// <summary>403.8010: a previous video is still transcoding; another cannot be uploaded yet.</summary>
        VideoTranscoding,
        /// <summary>403.8022: the blog's queue is full (1,000 posts).</summary>
        QueueFull,
        ServerError,
        ServiceUnavailable,
    }

    /// <summary>
    /// A failed Tumblr API call with the HTTP status and Tumblr's error subcode (e.g. 403.8011) preserved,
    /// so callers can tell "come back tomorrow" from "this media is broken" from "re-authorize".
    /// </summary>
    public sealed class TumblrApiException : Exception
    {
        public TumblrApiException(TumblrErrorKind kind, int status, int? subcode, string message,
            string? endpoint = null, Exception? inner = null)
            : base(message, inner)
        {
            Kind = kind;
            Status = status;
            Subcode = subcode;
            Endpoint = endpoint;
        }

        public TumblrErrorKind Kind { get; }
        /// <summary>HTTP status, or 0 when no response was received.</summary>
        public int Status { get; }
        public int? Subcode { get; }
        public string? Endpoint { get; }
        public DateTime? RetryAfterUtc { get; init; }
        /// <summary>True when the request body may have reached Tumblr (and taken effect) before the failure.</summary>
        public bool Ambiguous { get; init; }

        /// <summary>A compact code for logs and the UI: "403.8011", "429", "timeout".</summary>
        public string Code => Status == 0
            ? Kind.ToString().ToLowerInvariant()
            : Subcode is int s ? $"{Status}.{s}" : Status.ToString();

        public bool IsTransient => Kind is TumblrErrorKind.Network or TumblrErrorKind.Timeout
            or TumblrErrorKind.ServerError or TumblrErrorKind.ServiceUnavailable or TumblrErrorKind.RateLimited
            or TumblrErrorKind.Unknown;

        public bool IsDailyLimit => Kind is TumblrErrorKind.DailyPostLimit or TumblrErrorKind.DailyMediaLimit
            or TumblrErrorKind.DailyVideoLimit;

        /// <summary>Maps an HTTP status + Tumblr subcode (+ message text as a last resort) to an error kind.</summary>
        public static TumblrErrorKind Classify(int status, int? subcode, string? text)
        {
            string t = (text ?? string.Empty).ToLowerInvariant();
            switch (status)
            {
                case 401:
                    return TumblrErrorKind.Unauthorized;
                case 429:
                    return TumblrErrorKind.RateLimited;
                case 403:
                    switch (subcode)
                    {
                        case 8004: return TumblrErrorKind.DailyMediaLimit;
                        case 8010: return TumblrErrorKind.VideoTranscoding;
                        case 8011: return TumblrErrorKind.DailyVideoLimit;
                        case 8022: return TumblrErrorKind.QueueFull;
                        case 8023: return TumblrErrorKind.DailyPostLimit;
                    }
                    if (t.Contains("transcod")) return TumblrErrorKind.VideoTranscoding;
                    if (t.Contains("limit"))
                    {
                        if (t.Contains("video")) return TumblrErrorKind.DailyVideoLimit;
                        if (t.Contains("upload") || t.Contains("photo") || t.Contains("image") || t.Contains("media"))
                            return TumblrErrorKind.DailyMediaLimit;
                        if (t.Contains("queue")) return TumblrErrorKind.QueueFull;
                        return TumblrErrorKind.DailyPostLimit;
                    }
                    return TumblrErrorKind.Forbidden;
                case 400:
                    if (subcode == 8005) return TumblrErrorKind.InvalidMedia;
                    if (t.Contains("limit") && t.Contains("post")) return TumblrErrorKind.DailyPostLimit;
                    return TumblrErrorKind.BadRequest;
                case 404:
                    return TumblrErrorKind.NotFound;
                case 503:
                    return TumblrErrorKind.ServiceUnavailable;
            }
            if (status >= 500) return TumblrErrorKind.ServerError;
            return TumblrErrorKind.Unknown;
        }
    }
}
