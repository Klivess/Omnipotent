using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Omnipotent.Services.OmniDefence.Fingerprint
{
    [Flags]
    public enum HeaderFlags : uint
    {
        None = 0,
        Accept = 1u << 0,
        AcceptLanguage = 1u << 1,
        AcceptEncoding = 1u << 2,
        AcceptEncodingBr = 1u << 3,
        AcceptEncodingZstd = 1u << 4,
        SecFetchSite = 1u << 5,
        SecFetchMode = 1u << 6,
        SecFetchDest = 1u << 7,
        SecChUa = 1u << 8,
        SecChUaMobile = 1u << 9,
        SecChUaPlatform = 1u << 10,
        Referer = 1u << 11,
        Origin = 1u << 12,
        Dnt = 1u << 13,
        UpgradeInsecure = 1u << 14,
        IfNoneMatch = 1u << 15,
        IfModifiedSince = 1u << 16,
        Cookie = 1u << 17,
        CacheControl = 1u << 18,
        XRequestedWith = 1u << 19,
        ForwardedFor = 1u << 20,
        Via = 1u << 21,
        KliveClient = 1u << 22,
        Authorization = 1u << 23,
        AcceptWildcardOnly = 1u << 24,
        SecGpc = 1u << 25,
        Priority = 1u << 26,
    }

    /// <summary>
    /// Everything the fingerprint engine learns from one request. Captured in two halves:
    /// <see cref="Capture"/> copies what it needs out of the request early (header values
    /// are only guaranteed readable while the request is live), and the KliveAPI finally
    /// block fills in the outcome. All hashing happens later on the engine thread.
    /// </summary>
    public sealed class RequestSignals
    {
        public long UtcMs;
        public string Ip = "";
        public string Method = "";
        public string Route = "";
        public string? Query;
        public int StatusCode;
        public double DurationMs;
        public RequestOutcome Outcome;
        public string? DenyReason;
        public bool MatchedRoute;
        public string? RequestOrigin;
        public string? ProfileId;
        public int? ProfileRank;
        public long ResponseBytes;
        public bool ViaBatch;

        // Protocol
        public string HttpVersion = "";
        public bool KeepAlive;
        public int LocalPort;
        public bool Secure;

        // Headers
        public string? UserAgent;
        public string? Host;
        public HeaderFlags Flags;
        public string? AcceptLanguage;
        public string? SecChUa;
        public string? SecChUaPlatform;
        public string? SecFetchSite;
        /// <summary>Header names as http.sys reports them. Order is not the wire order.</summary>
        public string[] HeaderNames = Array.Empty<string>();
        /// <summary>Cookie names only — values are never captured.</summary>
        public string[] CookieNames = Array.Empty<string>();

        public bool Has(HeaderFlags f) => (Flags & f) == f;

        public static RequestSignals Capture(HttpListenerRequest req, string ip)
        {
            var s = new RequestSignals { Ip = ip ?? "", UtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            try
            {
                s.Method = req.HttpMethod ?? "";
                s.HttpVersion = req.ProtocolVersion?.ToString() ?? "";
                s.KeepAlive = req.KeepAlive;
                s.Secure = req.IsSecureConnection;
                s.LocalPort = req.LocalEndPoint?.Port ?? 0;
                s.UserAgent = Cap(req.UserAgent, 512);

                var headers = req.Headers;
                if (headers != null)
                {
                    s.HeaderNames = headers.AllKeys.Where(k => !string.IsNullOrEmpty(k)).Select(k => k!).ToArray();
                    s.Host = Cap(headers["Host"], 128);
                    s.AcceptLanguage = Cap(headers["Accept-Language"], 64);
                    s.SecChUa = Cap(headers["Sec-CH-UA"], 160);
                    s.SecChUaPlatform = Cap(headers["Sec-CH-UA-Platform"], 32);
                    s.SecFetchSite = Cap(headers["Sec-Fetch-Site"], 24);

                    HeaderFlags f = HeaderFlags.None;
                    string? accept = headers["Accept"];
                    if (!string.IsNullOrEmpty(accept)) { f |= HeaderFlags.Accept; if (accept.Trim() == "*/*") f |= HeaderFlags.AcceptWildcardOnly; }
                    if (!string.IsNullOrEmpty(s.AcceptLanguage)) f |= HeaderFlags.AcceptLanguage;
                    string? enc = headers["Accept-Encoding"];
                    if (!string.IsNullOrEmpty(enc))
                    {
                        f |= HeaderFlags.AcceptEncoding;
                        if (enc.Contains("br", StringComparison.OrdinalIgnoreCase)) f |= HeaderFlags.AcceptEncodingBr;
                        if (enc.Contains("zstd", StringComparison.OrdinalIgnoreCase)) f |= HeaderFlags.AcceptEncodingZstd;
                    }
                    if (!string.IsNullOrEmpty(s.SecFetchSite)) f |= HeaderFlags.SecFetchSite;
                    if (!string.IsNullOrEmpty(headers["Sec-Fetch-Mode"])) f |= HeaderFlags.SecFetchMode;
                    if (!string.IsNullOrEmpty(headers["Sec-Fetch-Dest"])) f |= HeaderFlags.SecFetchDest;
                    if (!string.IsNullOrEmpty(s.SecChUa)) f |= HeaderFlags.SecChUa;
                    if (!string.IsNullOrEmpty(headers["Sec-CH-UA-Mobile"])) f |= HeaderFlags.SecChUaMobile;
                    if (!string.IsNullOrEmpty(s.SecChUaPlatform)) f |= HeaderFlags.SecChUaPlatform;
                    if (!string.IsNullOrEmpty(headers["Referer"])) f |= HeaderFlags.Referer;
                    if (!string.IsNullOrEmpty(headers["Origin"])) f |= HeaderFlags.Origin;
                    if (!string.IsNullOrEmpty(headers["DNT"])) f |= HeaderFlags.Dnt;
                    if (!string.IsNullOrEmpty(headers["Sec-GPC"])) f |= HeaderFlags.SecGpc;
                    if (!string.IsNullOrEmpty(headers["Upgrade-Insecure-Requests"])) f |= HeaderFlags.UpgradeInsecure;
                    if (!string.IsNullOrEmpty(headers["If-None-Match"])) f |= HeaderFlags.IfNoneMatch;
                    if (!string.IsNullOrEmpty(headers["If-Modified-Since"])) f |= HeaderFlags.IfModifiedSince;
                    if (!string.IsNullOrEmpty(headers["Cookie"])) f |= HeaderFlags.Cookie;
                    if (!string.IsNullOrEmpty(headers["Cache-Control"])) f |= HeaderFlags.CacheControl;
                    if (!string.IsNullOrEmpty(headers["X-Requested-With"])) f |= HeaderFlags.XRequestedWith;
                    if (!string.IsNullOrEmpty(headers["X-Forwarded-For"]) || !string.IsNullOrEmpty(headers["Forwarded"])) f |= HeaderFlags.ForwardedFor;
                    if (!string.IsNullOrEmpty(headers["Via"])) f |= HeaderFlags.Via;
                    if (!string.IsNullOrEmpty(headers["X-Klive-Client"])) f |= HeaderFlags.KliveClient;
                    if (!string.IsNullOrEmpty(headers["Authorization"])) f |= HeaderFlags.Authorization;
                    if (!string.IsNullOrEmpty(headers["Priority"])) f |= HeaderFlags.Priority;
                    s.Flags = f;
                }

                var cookies = req.Cookies;
                if (cookies != null && cookies.Count > 0)
                {
                    var names = new string[Math.Min(cookies.Count, 32)];
                    for (int i = 0; i < names.Length; i++) names[i] = Cap(cookies[i].Name, 64) ?? "";
                    s.CookieNames = names;
                }
            }
            catch { /* signals are best-effort; a partial capture is still useful */ }
            return s;
        }

        /// <summary>
        /// Rebuilds signals from a stored audit row (backfill). Headers come from the
        /// redacted headers_json, so cookie names and protocol version are unknown.
        /// </summary>
        public static RequestSignals FromAuditRow(long utcTs, string ip, string? method, string? route, string? query, int status,
            string? userAgent, string? origin, string? profileId, int? profileRank, bool matched, string? denyReason, string? headersJson)
        {
            var s = new RequestSignals
            {
                UtcMs = utcTs * 1000,
                Ip = ip,
                Method = method ?? "",
                Route = route ?? "/",
                Query = query,
                StatusCode = status,
                UserAgent = Cap(userAgent, 512),
                RequestOrigin = origin,
                ProfileId = profileId,
                ProfileRank = profileRank,
                MatchedRoute = matched,
                DenyReason = denyReason,
                HttpVersion = "1.1",
                Secure = true,
                Outcome = OutcomeFrom(status, matched, denyReason),
            };

            if (!string.IsNullOrEmpty(headersJson))
            {
                try
                {
                    var headers = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(headersJson);
                    if (headers != null)
                    {
                        var h = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
                        string? Get(string k) => h.TryGetValue(k, out var v) ? v : null;
                        s.HeaderNames = h.Keys.ToArray();
                        s.Host = Cap(Get("Host"), 128);
                        s.Secure = s.Host == null || !s.Host.EndsWith(":5000", StringComparison.Ordinal);
                        s.AcceptLanguage = Cap(Get("Accept-Language"), 64);
                        s.SecChUa = Cap(Get("Sec-CH-UA"), 160);
                        s.SecChUaPlatform = Cap(Get("Sec-CH-UA-Platform"), 32);
                        s.SecFetchSite = Cap(Get("Sec-Fetch-Site"), 24);
                        HeaderFlags f = HeaderFlags.None;
                        string? accept = Get("Accept");
                        if (!string.IsNullOrEmpty(accept)) { f |= HeaderFlags.Accept; if (accept.Trim() == "*/*") f |= HeaderFlags.AcceptWildcardOnly; }
                        if (!string.IsNullOrEmpty(s.AcceptLanguage)) f |= HeaderFlags.AcceptLanguage;
                        string? enc = Get("Accept-Encoding");
                        if (!string.IsNullOrEmpty(enc))
                        {
                            f |= HeaderFlags.AcceptEncoding;
                            if (enc.Contains("br", StringComparison.OrdinalIgnoreCase)) f |= HeaderFlags.AcceptEncodingBr;
                            if (enc.Contains("zstd", StringComparison.OrdinalIgnoreCase)) f |= HeaderFlags.AcceptEncodingZstd;
                        }
                        if (!string.IsNullOrEmpty(s.SecFetchSite)) f |= HeaderFlags.SecFetchSite;
                        if (h.ContainsKey("Sec-Fetch-Mode")) f |= HeaderFlags.SecFetchMode;
                        if (h.ContainsKey("Sec-Fetch-Dest")) f |= HeaderFlags.SecFetchDest;
                        if (!string.IsNullOrEmpty(s.SecChUa)) f |= HeaderFlags.SecChUa;
                        if (h.ContainsKey("Sec-CH-UA-Mobile")) f |= HeaderFlags.SecChUaMobile;
                        if (!string.IsNullOrEmpty(s.SecChUaPlatform)) f |= HeaderFlags.SecChUaPlatform;
                        if (h.ContainsKey("Referer")) f |= HeaderFlags.Referer;
                        if (h.ContainsKey("Origin")) f |= HeaderFlags.Origin;
                        if (h.ContainsKey("DNT")) f |= HeaderFlags.Dnt;
                        if (h.ContainsKey("Sec-GPC")) f |= HeaderFlags.SecGpc;
                        if (h.ContainsKey("Upgrade-Insecure-Requests")) f |= HeaderFlags.UpgradeInsecure;
                        if (h.ContainsKey("If-None-Match")) f |= HeaderFlags.IfNoneMatch;
                        if (h.ContainsKey("If-Modified-Since")) f |= HeaderFlags.IfModifiedSince;
                        if (h.ContainsKey("Cookie")) f |= HeaderFlags.Cookie;
                        if (h.ContainsKey("Cache-Control")) f |= HeaderFlags.CacheControl;
                        if (h.ContainsKey("X-Requested-With")) f |= HeaderFlags.XRequestedWith;
                        if (h.ContainsKey("X-Forwarded-For") || h.ContainsKey("Forwarded")) f |= HeaderFlags.ForwardedFor;
                        if (h.ContainsKey("Via")) f |= HeaderFlags.Via;
                        if (h.ContainsKey("X-Klive-Client")) f |= HeaderFlags.KliveClient;
                        if (h.ContainsKey("Authorization")) f |= HeaderFlags.Authorization;
                        if (h.ContainsKey("Priority")) f |= HeaderFlags.Priority;
                        s.Flags = f;
                    }
                }
                catch { /* malformed historic row: fold what we have */ }
            }
            return s;
        }

        /// <summary>Mirrors how KliveAPI maps a finished request onto a <see cref="RequestOutcome"/>.</summary>
        public static RequestOutcome OutcomeFrom(int status, bool matched, string? denyReason) => denyReason switch
        {
            "InvalidPassword" => RequestOutcome.InvalidPassword,
            "NoProfile" => RequestOutcome.UnauthRoute,
            "WebsiteNoProfile" or "WebsiteInvalidProfile" => RequestOutcome.WebsiteNoProfile,
            "TooLowClearance" or "ProfileDisabled" => RequestOutcome.InsufficientClearance,
            "IPBlocked" or "Tarpit" => RequestOutcome.PreBlocked,
            "IncorrectHTTPMethod" => RequestOutcome.IncorrectMethod,
            "RequestBodyTooLarge" => RequestOutcome.ClientError,
            _ => status == 404 && !matched ? RequestOutcome.NotFound
                : status == 401 ? RequestOutcome.UnauthRoute
                : status == 403 ? RequestOutcome.InsufficientClearance
                : status == 404 ? RequestOutcome.NotFound
                : status >= 500 ? RequestOutcome.ServerError
                : status >= 400 ? RequestOutcome.ClientError
                : RequestOutcome.Success
        };

        private static string? Cap(string? value, int max)
            => value == null ? null : value.Length <= max ? value : value.Substring(0, max);

        // ── derived values (engine thread) ──

        public bool HostIsIp => !string.IsNullOrEmpty(Host) && IPAddress.TryParse(StripPort(Host!), out _);

        private static string StripPort(string host)
        {
            if (host.StartsWith('[')) { int end = host.IndexOf(']'); return end > 0 ? host.Substring(1, end - 1) : host; }
            int colon = host.LastIndexOf(':');
            return colon > 0 && host.IndexOf(':') == colon ? host.Substring(0, colon) : host;
        }

        /// <summary>
        /// JA4H-style HTTP client fingerprint. Real JA4H hashes headers in wire order; http.sys
        /// loses that order, so this uses the sorted header set ("lite"). Stable per client
        /// stack, which is what matters for grouping. Cookie values are never hashed.
        /// Format: {method2}{ver2}{c|n}{r|n}{count2}{lang4}_{headers12}_{cookies12}
        /// </summary>
        public string ComputeJa4hLite()
        {
            string method = Method.Length >= 2 ? Method.Substring(0, 2).ToLowerInvariant() : (Method.ToLowerInvariant() + "xx").Substring(0, 2);
            string ver = HttpVersion switch { "2.0" => "20", "1.0" => "10", "3.0" => "30", _ => "11" };
            char cookie = Has(HeaderFlags.Cookie) ? 'c' : 'n';
            char referer = Has(HeaderFlags.Referer) ? 'r' : 'n';
            var names = HeaderNames
                .Where(n => !n.Equals("Cookie", StringComparison.OrdinalIgnoreCase) && !n.Equals("Referer", StringComparison.OrdinalIgnoreCase))
                .Select(n => n.ToLowerInvariant())
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            string count = Math.Min(names.Length, 99).ToString("00");
            string lang = "0000";
            if (!string.IsNullOrEmpty(AcceptLanguage))
            {
                string first = new string(AcceptLanguage.Split(',', ';')[0].Where(char.IsLetter).ToArray()).ToLowerInvariant();
                lang = (first + "0000").Substring(0, 4);
            }
            string headerHash = Hash12(string.Join(",", names));
            string cookieHash = CookieNames.Length == 0 ? "000000000000" : Hash12(string.Join(",", CookieNames.OrderBy(n => n, StringComparer.Ordinal)));
            return $"{method}{ver}{cookie}{referer}{count}{lang}_{headerHash}_{cookieHash}";
        }

        public static string Hash12(string value)
        {
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(Encoding.UTF8.GetBytes(value), hash);
            return Convert.ToHexString(hash.Slice(0, 6)).ToLowerInvariant();
        }
    }
}
