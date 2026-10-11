using System.Text.RegularExpressions;

namespace Omnipotent.Services.OmniGram.Api
{
    /// <summary>What went wrong, in terms the publisher and sync can act on.</summary>
    public enum IgErrorKind
    {
        Unknown,
        /// <summary>No response at all (DNS, TLS, connection reset).</summary>
        Network,
        Timeout,
        /// <summary>190 (and 102): the token expired, was revoked, or the password changed. Reconnect.</summary>
        TokenInvalid,
        /// <summary>10, 200–299: the token lacks a permission (scope). Reconnect granting it.</summary>
        PermissionDenied,
        /// <summary>4, 17, 32, 613: an API call-rate limit. Wait.</summary>
        RateLimited,
        /// <summary>4 / 2207051: Instagram flagged the action as spam. Back off for a long time.</summary>
        SpamRestricted,
        /// <summary>9 / 2207042: the rolling 24-hour publishing limit is used up.</summary>
        DailyPublishLimit,
        /// <summary>25 / 2207050: the account is inactive, checkpointed or restricted. Fix it in the app.</summary>
        AccountRestricted,
        /// <summary>9004 / 2207052: Instagram could not download the media from the public URL.</summary>
        MediaFetchFailed,
        /// <summary>-2 / 2207003: downloading the media timed out on Instagram's side.</summary>
        MediaFetchTimeout,
        /// <summary>2207020, 2207008, 2207006: the container expired or vanished. Create a new one.</summary>
        ContainerExpired,
        /// <summary>9007 / 2207027: the container is still processing.</summary>
        NotReady,
        /// <summary>Unsupported format, size, aspect ratio or item count: retrying the same file never works.</summary>
        InvalidMedia,
        /// <summary>1 / 2207057: the cover-frame offset is outside the video.</summary>
        InvalidCoverFrame,
        /// <summary>36004 / 2207010, 100 / 2207040: caption too long or too many tags.</summary>
        InvalidCaption,
        NotFound,
        BadRequest,
        /// <summary>-1 / 2207001, 2207032, 2207053, codes 1 and 2, HTTP 5xx: Instagram-side trouble. Retry.</summary>
        ServerError,
    }

    /// <summary>
    /// A failed Instagram API call with Meta's error code and subcode preserved, so callers can tell
    /// "reconnect" from "come back tomorrow" from "this file will never work". Codes follow Meta's
    /// Instagram Platform error table (developers.facebook.com → Instagram Platform → Error Codes).
    /// </summary>
    public sealed class InstagramApiException : Exception
    {
        public InstagramApiException(IgErrorKind kind, int status, int? code, int? subcode, string message,
            string? endpoint = null, Exception? inner = null)
            : base(message, inner)
        {
            Kind = kind;
            Status = status;
            ErrorCode = code;
            Subcode = subcode;
            Endpoint = endpoint;
        }

        public IgErrorKind Kind { get; }
        /// <summary>HTTP status, or 0 when no response was received.</summary>
        public int Status { get; }
        /// <summary>Meta's error.code (190, 9004, 352…).</summary>
        public int? ErrorCode { get; }
        /// <summary>Meta's error.error_subcode (2207052…).</summary>
        public int? Subcode { get; }
        public string? Endpoint { get; }
        public string? FbTraceId { get; init; }
        public DateTime? RetryAfterUtc { get; init; }
        /// <summary>True when the request may have reached Instagram (and taken effect) before the failure.</summary>
        public bool Ambiguous { get; init; }

        /// <summary>A compact code for logs and the UI: "9004.2207052", "190", "2207026" (a container error), "http 502", "timeout".</summary>
        public string Code => ErrorCode is int c
            ? Subcode is int s ? $"{c}.{s}" : c.ToString()
            : Subcode is int sub ? sub.ToString()
            : Status == 0 ? Kind.ToString().ToLowerInvariant() : $"http {Status}";

        public bool IsTransient => Kind is IgErrorKind.Network or IgErrorKind.Timeout or IgErrorKind.ServerError
            or IgErrorKind.RateLimited or IgErrorKind.Unknown or IgErrorKind.MediaFetchTimeout or IgErrorKind.NotReady;

        /// <summary>The token is not usable as it is: the account must be reconnected.</summary>
        public bool NeedsReconnect => Kind is IgErrorKind.TokenInvalid or IgErrorKind.PermissionDenied;

        /// <summary>Maps HTTP status + Meta's code/subcode (+ message text as a last resort) to an error kind.</summary>
        public static IgErrorKind Classify(int status, int? code, int? subcode, string? text)
        {
            string t = (text ?? string.Empty).ToLowerInvariant();
            switch (subcode)
            {
                case 2207051: return IgErrorKind.SpamRestricted;
                case 2207042: return IgErrorKind.DailyPublishLimit;
                case 2207050: return IgErrorKind.AccountRestricted;
                case 2207052: return IgErrorKind.MediaFetchFailed;
                case 2207003: return IgErrorKind.MediaFetchTimeout;
                case 2207020: case 2207008: case 2207006: return IgErrorKind.ContainerExpired;
                case 2207027: return IgErrorKind.NotReady;
                case 2207026: case 2207004: case 2207005: case 2207009: case 2207023: case 2207028: case 2207024:
                    return IgErrorKind.InvalidMedia;
                case 2207057: return IgErrorKind.InvalidCoverFrame;
                case 2207010: case 2207040: return IgErrorKind.InvalidCaption;
                case 2207001: case 2207032: case 2207053: return IgErrorKind.ServerError;
                case 463: case 467: case 460: case 458: case 459: case 464: case 492: return IgErrorKind.TokenInvalid;
            }
            switch (code)
            {
                case 190: case 102: return IgErrorKind.TokenInvalid;
                case 10: return IgErrorKind.PermissionDenied;
                case int c when c >= 200 && c <= 299: return IgErrorKind.PermissionDenied;
                case 4: case 17: case 32: case 613: case 80002: case 80006: return IgErrorKind.RateLimited;
                case 9: return IgErrorKind.DailyPublishLimit;
                case 25: return IgErrorKind.AccountRestricted;
                case 9004: return IgErrorKind.MediaFetchFailed;
                case 9007: return IgErrorKind.NotReady;
                case 352: case 36000: case 36001: case 36003: return IgErrorKind.InvalidMedia;
                case 36004: return IgErrorKind.InvalidCaption;
                case 803: return IgErrorKind.NotFound;
                case 1: case 2: case -1: return IgErrorKind.ServerError;
                case -2: return IgErrorKind.MediaFetchTimeout;
                case 24: return IgErrorKind.ContainerExpired;
                case 100:
                    if (t.Contains("does not exist") || t.Contains("cannot be loaded") || t.Contains("unsupported get request")) return IgErrorKind.NotFound;
                    if (t.Contains("caption")) return IgErrorKind.InvalidCaption;
                    return IgErrorKind.BadRequest;
            }
            if (t.Contains("access token") && (t.Contains("expired") || t.Contains("invalid") || t.Contains("session"))) return IgErrorKind.TokenInvalid;
            if (status == 401) return IgErrorKind.TokenInvalid;
            if (status == 403) return IgErrorKind.PermissionDenied;
            if (status == 404) return IgErrorKind.NotFound;
            if (status == 429) return IgErrorKind.RateLimited;
            if (status >= 500) return IgErrorKind.ServerError;
            if (status == 400) return IgErrorKind.BadRequest;
            return IgErrorKind.Unknown;
        }

        /// <summary>
        /// A container in ERROR state reports why only as text in its "status" field (e.g.
        /// "Error: … (2207026)"). Pulls a 22070xx subcode out of it when present.
        /// </summary>
        public static int? SubcodeFromStatusText(string? status)
        {
            if (string.IsNullOrEmpty(status)) return null;
            var m = Regex.Match(status, @"\b(2207\d{3})\b");
            return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : null;
        }

        /// <summary>Classifies a container that ended in ERROR from its status text.</summary>
        public static InstagramApiException FromContainerError(string containerId, string? statusText)
        {
            int? sub = SubcodeFromStatusText(statusText);
            string t = (statusText ?? "").ToLowerInvariant();
            IgErrorKind kind = sub != null
                ? Classify(400, null, sub, statusText)
                : t.Contains("download") || t.Contains("fetch") || t.Contains("uri") || t.Contains("url") ? IgErrorKind.MediaFetchFailed
                : t.Contains("format") || t.Contains("codec") || t.Contains("aspect") || t.Contains("resolution") || t.Contains("duration") || t.Contains("too long") || t.Contains("too short") || t.Contains("size") ? IgErrorKind.InvalidMedia
                : IgErrorKind.ServerError;
            string message = string.IsNullOrWhiteSpace(statusText) ? "Instagram could not process the media." : statusText.Trim();
            return new InstagramApiException(kind, 0, null, sub, message, "container:" + containerId);
        }
    }
}
