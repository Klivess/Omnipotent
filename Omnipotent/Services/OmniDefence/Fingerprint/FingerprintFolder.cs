using System.Collections.Concurrent;

namespace Omnipotent.Services.OmniDefence.Fingerprint
{
    /// <summary>Folds one request's signals into an IP's aggregate. Engine thread only.</summary>
    public static class FingerprintFolder
    {
        public const string LoginRoute = "/KMProfiles/AttemptLogin";
        public const string RobotsRoute = "/robots.txt";

        private static readonly ConcurrentDictionary<string, ParsedUserAgent> UaCache = new(StringComparer.Ordinal);

        public static ParsedUserAgent ParseCached(string? ua)
        {
            string key = ua ?? "";
            if (UaCache.TryGetValue(key, out var hit)) return hit;
            var parsed = UserAgentParser.Parse(ua);
            if (UaCache.Count > 4096) UaCache.Clear();
            UaCache[key] = parsed;
            return parsed;
        }

        /// <param name="isHoneypotRoute">Whether the route is a registered honeypot route.</param>
        /// <param name="robotsTrapRoute">The robots.txt-disallowed trap path, if any.</param>
        public static void Fold(IpFingerprint fp, RequestSignals s, Func<string, bool> isHoneypotRoute, string? robotsTrapRoute)
        {
            fp.Requests++;
            if (fp.FirstSeenMs == 0 || s.UtcMs < fp.FirstSeenMs) fp.FirstSeenMs = s.UtcMs;
            FoldTiming(fp, s.UtcMs);

            // ── outcomes ──
            bool notFound = s.Outcome == RequestOutcome.NotFound || (s.StatusCode == 404 && !s.MatchedRoute);
            if (notFound) fp.NotFound++;
            if (s.Outcome == RequestOutcome.ClientError) fp.ClientErrors++;
            if (s.Outcome == RequestOutcome.ServerError) fp.ServerErrors++;
            if (s.Outcome == RequestOutcome.PreBlocked) fp.PreBlocked++;
            if (s.Outcome is RequestOutcome.UnauthRoute or RequestOutcome.InsufficientClearance or RequestOutcome.InvalidPassword or RequestOutcome.WebsiteNoProfile)
                fp.AuthFailures++;
            if (s.Outcome == RequestOutcome.InvalidPassword) fp.InvalidPasswords++;

            if (string.Equals(s.Route, LoginRoute, StringComparison.OrdinalIgnoreCase))
            {
                fp.LoginAttempts++;
                if (s.StatusCode >= 400) fp.LoginFailures++;
            }

            if (string.Equals(s.Route, RobotsRoute, StringComparison.OrdinalIgnoreCase))
            {
                fp.RobotsFetches++;
            }
            else if (!string.IsNullOrEmpty(robotsTrapRoute) && string.Equals(s.Route, robotsTrapRoute, StringComparison.OrdinalIgnoreCase))
            {
                if (fp.RobotsTrapHits == 0) fp.Urgent = true;
                fp.RobotsTrapHits++;
            }
            else if (isHoneypotRoute(s.Route))
            {
                if (fp.HoneypotHits == 0) fp.Urgent = true;
                fp.HoneypotHits++;
            }

            if (!s.MatchedRoute)
            {
                var probe = ProbePatterns.Classify(s.Route, s.Query);
                if (probe != ProbePatterns.ProbeKind.None)
                {
                    if (fp.ProbeHits == 0) fp.Urgent = true;
                    fp.ProbeHits++;
                    IpFingerprint.Bump(fp.ProbeKinds, probe.ToString(), cap: 16);
                    string sample = s.Route.Length > 96 ? s.Route.Substring(0, 96) : s.Route;
                    if (fp.SampleProbes.Count < 10 && !fp.SampleProbes.Contains(sample)) fp.SampleProbes.Add(sample);
                }
            }

            if (fp.Routes.Count < IpFingerprint.MaxRoutes)
            {
                fp.Routes.Add(s.Route.Length > 160 ? s.Route.Substring(0, 160) : s.Route);
            }
            else if (!fp.Routes.Contains(s.Route))
            {
                fp.RoutesCapped = true;
            }

            // ── identity / origin ──
            if (s.RequestOrigin != null && s.RequestOrigin.StartsWith("Website", StringComparison.Ordinal)) fp.WebsiteRequests++;
            else fp.DirectRequests++;
            if (!string.IsNullOrEmpty(s.ProfileId))
            {
                if (fp.ProfileRequests == 0) fp.Urgent = true;
                fp.ProfileRequests++;
                fp.LastProfileId = s.ProfileId;
                if (s.ProfileRank.HasValue && s.ProfileRank.Value > fp.MaxProfileRank) fp.MaxProfileRank = s.ProfileRank.Value;
            }
            if (s.ViaBatch) fp.ViaBatch++;

            // ── headers ──
            var ua = ParseCached(s.UserAgent);
            string uaKey = string.IsNullOrEmpty(s.UserAgent) ? "(none)" : (s.UserAgent!.Length > 200 ? s.UserAgent.Substring(0, 200) : s.UserAgent);
            IpFingerprint.Bump(fp.UserAgents, uaKey);
            IpFingerprint.Bump(fp.UaKinds, ua.Kind.ToString());
            IpFingerprint.Bump(fp.UaFamilies, ua.ToString());
            if (ua.ClaimedCrawler != null) fp.ClaimedCrawler = ua.ClaimedCrawler;
            fp.LastUserAgent = uaKey;

            if (s.Has(HeaderFlags.AcceptLanguage)) fp.WithAcceptLanguage++;
            if (s.Has(HeaderFlags.SecFetchSite) || s.Has(HeaderFlags.SecFetchMode)) fp.WithSecFetch++;
            if (s.Has(HeaderFlags.SecChUa)) fp.WithClientHints++;
            if (s.Has(HeaderFlags.AcceptEncodingBr)) fp.WithBrotli++;
            if (s.Has(HeaderFlags.Cookie)) fp.WithCookie++;
            if (s.Has(HeaderFlags.Referer)) fp.WithReferer++;
            if (s.Has(HeaderFlags.IfNoneMatch) || s.Has(HeaderFlags.IfModifiedSince)) fp.Conditional++;
            if (s.Has(HeaderFlags.ForwardedFor) || s.Has(HeaderFlags.Via)) fp.ForwardedHeader++;
            if (s.Has(HeaderFlags.AcceptWildcardOnly)) fp.AcceptWildcardOnly++;
            if (s.HostIsIp) fp.HostIsIp++;
            if (s.HttpVersion == "1.0") fp.Http10++;
            if (!s.Secure) fp.PlainHttp++;

            if (ua.Kind == UaKind.Browser) FoldBrowserConsistency(fp, s, ua);

            IpFingerprint.Bump(fp.Ja4h, s.ComputeJa4hLite());

            if (fp.Requests == 1) fp.Urgent = true;
            fp.Dirty = true;
            fp.PendingClassify = true;
        }

        private static void FoldTiming(IpFingerprint fp, long utcMs)
        {
            long hour = (utcMs / 3_600_000L) % 24;
            fp.HourHistogram[hour]++;

            long minute = utcMs / 60_000L;
            if (minute == fp.CurrentMinute) fp.CurrentMinuteCount++;
            else if (minute > fp.CurrentMinute) { fp.CurrentMinute = minute; fp.CurrentMinuteCount = 1; }
            if (fp.CurrentMinuteCount > fp.PeakPerMinute) fp.PeakPerMinute = fp.CurrentMinuteCount;

            if (fp.LastSeenMs == 0)
            {
                fp.Bursts = 1;
                fp.Sessions = 1;
                fp.LastBurstStartMs = utcMs;
                fp.LastSeenMs = utcMs;
                return;
            }
            // Out-of-order (backfill racing live traffic): count it, but don't distort gaps.
            if (utcMs < fp.LastSeenMs) return;

            double sinceLast = (utcMs - fp.LastSeenMs) / 1000.0;
            if (sinceLast >= IpFingerprint.SessionGapSeconds) fp.Sessions++;
            if (sinceLast >= IpFingerprint.BurstGapSeconds)
            {
                // Gaps are measured between burst starts: a page load fires many requests at
                // once, which says nothing about whether a machine or a person is pacing it.
                double interBurst = (utcMs - fp.LastBurstStartMs) / 1000.0;
                if (interBurst < IpFingerprint.SessionGapSeconds)
                {
                    fp.GapCount++;
                    double delta = interBurst - fp.GapMean;
                    fp.GapMean += delta / fp.GapCount;
                    fp.GapM2 += delta * (interBurst - fp.GapMean);
                }
                fp.Bursts++;
                fp.LastBurstStartMs = utcMs;
            }
            fp.LastSeenMs = utcMs;
        }

        private static void FoldBrowserConsistency(IpFingerprint fp, RequestSignals s, ParsedUserAgent ua)
        {
            fp.BrowserClaims++;
            bool ios = ua.OperatingSystem == "iOS";
            bool chromium = ua.IsChromium && !ios;
            int v = ua.MajorVersion;

            // Browsers only send Sec-Fetch-* and client hints to secure origins.
            bool expectsSecFetch = s.Secure && ((chromium && v >= 80) || (ua.Family == "Firefox" && !ios && v >= 90) || (ua.Family == "Safari" && v >= 17));
            bool expectsHints = s.Secure && chromium && v >= 90;

            if (!s.Has(HeaderFlags.AcceptLanguage)) fp.MissingAcceptLanguage++;
            if (expectsSecFetch && !s.Has(HeaderFlags.SecFetchSite) && !s.Has(HeaderFlags.SecFetchMode)) fp.MissingSecFetch++;
            if (expectsHints && !s.Has(HeaderFlags.SecChUa)) fp.MissingClientHints++;

            if (s.Has(HeaderFlags.SecChUa) && !string.IsNullOrEmpty(s.SecChUa))
            {
                bool claimsNonChromium = ua.Family is "Firefox" or "Safari";
                bool versionMissing = chromium && v > 0 && !s.SecChUa!.Contains($"v=\"{v}\"", StringComparison.Ordinal);
                if (claimsNonChromium || versionMissing) fp.SecChUaMismatch++;
            }
        }
    }
}
