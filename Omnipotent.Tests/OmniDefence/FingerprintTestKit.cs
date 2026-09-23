using Omnipotent.Services.OmniDefence;
using Omnipotent.Services.OmniDefence.Fingerprint;

namespace Omnipotent.Tests.OmniDefence
{
    /// <summary>Builders for synthetic request streams used by the fingerprint tests.</summary>
    internal static class FingerprintTestKit
    {
        public const string ChromeUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";
        public const string ChromeSecChUa = "\"Chromium\";v=\"128\", \"Not;A=Brand\";v=\"24\", \"Google Chrome\";v=\"128\"";

        public static RequestSignals Browser(string ip, long utcMs, string route = "/km/dashboard", string origin = "WebsitePublicNoProfile")
        {
            return new RequestSignals
            {
                Ip = ip,
                UtcMs = utcMs,
                Method = "GET",
                Route = route,
                StatusCode = 200,
                MatchedRoute = true,
                Outcome = RequestOutcome.Success,
                RequestOrigin = origin,
                HttpVersion = "1.1",
                Secure = true,
                UserAgent = ChromeUa,
                Host = "klive.dev",
                AcceptLanguage = "en-GB,en;q=0.9",
                SecChUa = ChromeSecChUa,
                SecFetchSite = "same-site",
                Flags = HeaderFlags.Accept | HeaderFlags.AcceptLanguage | HeaderFlags.AcceptEncoding | HeaderFlags.AcceptEncodingBr
                    | HeaderFlags.SecFetchSite | HeaderFlags.SecFetchMode | HeaderFlags.SecFetchDest | HeaderFlags.SecChUa
                    | HeaderFlags.SecChUaMobile | HeaderFlags.SecChUaPlatform | HeaderFlags.Origin | HeaderFlags.Referer | HeaderFlags.KliveClient,
                HeaderNames = new[] { "Accept", "Accept-Language", "Accept-Encoding", "Sec-Fetch-Site", "Sec-Fetch-Mode", "Sec-Fetch-Dest", "Sec-CH-UA", "Origin", "Referer", "User-Agent", "Host", "X-Klive-Client" },
            };
        }

        public static RequestSignals Script(string ip, long utcMs, string ua = "python-requests/2.31.0", string route = "/km/items", int status = 200, bool matched = true)
        {
            return new RequestSignals
            {
                Ip = ip,
                UtcMs = utcMs,
                Method = "GET",
                Route = route,
                StatusCode = status,
                MatchedRoute = matched,
                Outcome = RequestSignals.OutcomeFrom(status, matched, null),
                RequestOrigin = "DirectApi",
                HttpVersion = "1.1",
                Secure = true,
                UserAgent = ua,
                Host = "klive.dev",
                Flags = HeaderFlags.Accept | HeaderFlags.AcceptWildcardOnly | HeaderFlags.AcceptEncoding,
                HeaderNames = new[] { "Accept", "Accept-Encoding", "User-Agent", "Host", "Connection" },
            };
        }

        public static IpFingerprint Fold(IEnumerable<RequestSignals> signals, Func<string, bool>? honeypot = null, string? robotsTrap = null)
        {
            IpFingerprint? fp = null;
            foreach (var s in signals)
            {
                fp ??= new IpFingerprint { Ip = s.Ip };
                FingerprintFolder.Fold(fp, s, honeypot ?? (_ => false), robotsTrap);
            }
            return fp!;
        }

        public static IEnumerable<RequestSignals> Every(int count, long startMs, long stepMs, Func<string, long, RequestSignals> make, string ip)
        {
            for (int i = 0; i < count; i++) yield return make(ip, startMs + i * stepMs);
        }
    }
}
