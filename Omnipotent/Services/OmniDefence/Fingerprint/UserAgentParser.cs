using System.Text.RegularExpressions;

namespace Omnipotent.Services.OmniDefence.Fingerprint
{
    /// <summary>What kind of software a User-Agent string claims to be.</summary>
    public enum UaKind
    {
        Empty,
        Browser,
        HeadlessBrowser,
        Script,
        Crawler,
        PreviewBot,
        Monitor,
        Scanner,
        OtherBot,
        Unknown
    }

    public sealed class ParsedUserAgent
    {
        public UaKind Kind;
        /// <summary>Browser family for browsers (Chrome/Edge/Firefox/Safari/Opera), tool/bot name otherwise.</summary>
        public string Family = "Unknown";
        public int MajorVersion;
        public string OperatingSystem = "Unknown";
        public string DeviceType = "Unknown";
        /// <summary>Name of the crawler this UA claims to be, used for rDNS verification (e.g. "Googlebot").</summary>
        public string? ClaimedCrawler;

        public bool IsChromium => Family is "Chrome" or "Edge" or "Opera" or "Brave" or "Samsung";
        public override string ToString() => MajorVersion > 0 ? $"{Family} {MajorVersion}" : Family;
    }

    /// <summary>
    /// User-Agent parsing shared by OmniDefence fingerprinting and Tripwires. The
    /// <see cref="LegacyIsBot"/>/<see cref="LegacyParse"/> pair is the original Tripwires
    /// behaviour, kept byte-for-byte so tripwire hit records don't change meaning.
    /// </summary>
    public static class UserAgentParser
    {
        // ── legacy (Tripwires) ──
        private static readonly Regex LegacyBotPattern = new(
            @"bot|crawler|spider|slurp|preview|facebookexternalhit|discordbot|telegrambot|whatsapp|skypeuripreview|curl|wget|python-requests|headless",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool LegacyIsBot(string? value) => !string.IsNullOrWhiteSpace(value) && LegacyBotPattern.IsMatch(value);

        public static (string Browser, string OperatingSystem, string DeviceType) LegacyParse(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return ("Unknown", "Unknown", "Unknown");
            string ua = value;
            string browser = ua.Contains("Edg/", StringComparison.OrdinalIgnoreCase) ? LegacyVersion("Edge", ua, @"Edg/([\d.]+)")
                : ua.Contains("OPR/", StringComparison.OrdinalIgnoreCase) ? LegacyVersion("Opera", ua, @"OPR/([\d.]+)")
                : ua.Contains("Chrome/", StringComparison.OrdinalIgnoreCase) ? LegacyVersion("Chrome", ua, @"Chrome/([\d.]+)")
                : ua.Contains("Firefox/", StringComparison.OrdinalIgnoreCase) ? LegacyVersion("Firefox", ua, @"Firefox/([\d.]+)")
                : ua.Contains("Safari/", StringComparison.OrdinalIgnoreCase) ? LegacyVersion("Safari", ua, @"Version/([\d.]+)")
                : "Other";

            string device = LegacyIsBot(ua) ? "Bot" : DeviceOf(ua);
            return (browser, OsOf(ua, includeChromeOs: false), device);
        }

        private static string LegacyVersion(string name, string ua, string pattern)
        {
            var match = Regex.Match(ua, pattern, RegexOptions.IgnoreCase);
            if (!match.Success) return name;
            string major = match.Groups[1].Value.Split('.')[0];
            return string.IsNullOrWhiteSpace(major) ? name : $"{name} {major}";
        }

        // ── fingerprinting parser ──

        // Ordered: the first matching entry wins, so specific names precede generic ones.
        private static readonly (Regex Pattern, UaKind Kind, string Name)[] Known = BuildKnown();

        private static (Regex, UaKind, string)[] BuildKnown()
        {
            var list = new List<(string, UaKind, string)>
            {
                // Vulnerability / port scanners and fuzzers.
                (@"zgrab", UaKind.Scanner, "zgrab"),
                (@"masscan", UaKind.Scanner, "masscan"),
                (@"nmap", UaKind.Scanner, "Nmap"),
                (@"nuclei", UaKind.Scanner, "Nuclei"),
                (@"sqlmap", UaKind.Scanner, "sqlmap"),
                (@"nikto", UaKind.Scanner, "Nikto"),
                (@"wpscan", UaKind.Scanner, "WPScan"),
                (@"gobuster", UaKind.Scanner, "gobuster"),
                (@"dirbuster|dirb\b", UaKind.Scanner, "DirBuster"),
                (@"\bffuf\b", UaKind.Scanner, "ffuf"),
                (@"feroxbuster", UaKind.Scanner, "feroxbuster"),
                (@"nessus", UaKind.Scanner, "Nessus"),
                (@"openvas", UaKind.Scanner, "OpenVAS"),
                (@"acunetix", UaKind.Scanner, "Acunetix"),
                (@"burp", UaKind.Scanner, "Burp"),
                (@"netsparker|invicti", UaKind.Scanner, "Invicti"),
                (@"hydra", UaKind.Scanner, "Hydra"),
                (@"l9explore|l9tcpid|leakix", UaKind.Scanner, "LeakIX"),
                (@"censysinspect|censys", UaKind.Scanner, "Censys"),
                (@"expanse", UaKind.Scanner, "Palo Alto Expanse"),
                (@"internet-?measurement|internetmeasurement", UaKind.Scanner, "InternetMeasurement"),
                (@"shodan", UaKind.Scanner, "Shodan"),
                (@"odin\.io|odin[- ]scanner", UaKind.Scanner, "ODIN"),
                (@"modatscanner", UaKind.Scanner, "Modat"),
                (@"keydrop|visionheight|xpanse", UaKind.Scanner, "Scanner"),

                // Headless / automation frameworks.
                (@"headlesschrome", UaKind.HeadlessBrowser, "HeadlessChrome"),
                (@"phantomjs", UaKind.HeadlessBrowser, "PhantomJS"),
                (@"puppeteer", UaKind.HeadlessBrowser, "Puppeteer"),
                (@"playwright", UaKind.HeadlessBrowser, "Playwright"),
                (@"selenium|webdriver", UaKind.HeadlessBrowser, "Selenium"),

                // Search / AI crawlers (verifiable by rDNS or published ranges).
                (@"googlebot|google-inspectiontool|googleother|storebot-google|adsbot-google|mediapartners-google|google-extended", UaKind.Crawler, "Googlebot"),
                (@"bingbot|bingpreview|adidxbot|msnbot", UaKind.Crawler, "Bingbot"),
                (@"applebot", UaKind.Crawler, "Applebot"),
                (@"duckduckbot|duckassistbot", UaKind.Crawler, "DuckDuckBot"),
                (@"yandex(bot|images|mobilebot|metrika)?", UaKind.Crawler, "YandexBot"),
                (@"baiduspider", UaKind.Crawler, "Baiduspider"),
                (@"gptbot|chatgpt-user|oai-searchbot", UaKind.Crawler, "GPTBot"),
                (@"claudebot|claude-web|anthropic-ai|claude-user|claude-searchbot", UaKind.Crawler, "ClaudeBot"),
                (@"perplexitybot|perplexity-user", UaKind.Crawler, "PerplexityBot"),
                (@"ccbot", UaKind.Crawler, "CCBot"),
                (@"amazonbot", UaKind.Crawler, "Amazonbot"),
                (@"bytespider", UaKind.Crawler, "Bytespider"),
                (@"petalbot", UaKind.Crawler, "PetalBot"),
                (@"ahrefsbot", UaKind.Crawler, "AhrefsBot"),
                (@"semrushbot", UaKind.Crawler, "SemrushBot"),
                (@"mj12bot", UaKind.Crawler, "MJ12bot"),
                (@"dotbot", UaKind.Crawler, "DotBot"),
                (@"seznambot", UaKind.Crawler, "SeznamBot"),
                (@"sogou", UaKind.Crawler, "Sogou"),
                (@"facebookbot|meta-externalagent", UaKind.Crawler, "Meta crawler"),

                // Link-preview unfurlers.
                (@"facebookexternalhit|facebookcatalog", UaKind.PreviewBot, "Facebook preview"),
                (@"discordbot", UaKind.PreviewBot, "Discordbot"),
                (@"slackbot|slack-imgproxy", UaKind.PreviewBot, "Slackbot"),
                (@"telegrambot", UaKind.PreviewBot, "TelegramBot"),
                (@"whatsapp", UaKind.PreviewBot, "WhatsApp"),
                (@"twitterbot", UaKind.PreviewBot, "Twitterbot"),
                (@"linkedinbot", UaKind.PreviewBot, "LinkedInBot"),
                (@"skypeuripreview", UaKind.PreviewBot, "Skype preview"),
                (@"redditbot", UaKind.PreviewBot, "Redditbot"),
                (@"embedly|iframely", UaKind.PreviewBot, "Embed preview"),
                (@"pinterestbot", UaKind.PreviewBot, "Pinterestbot"),

                // Uptime monitors.
                (@"uptimerobot", UaKind.Monitor, "UptimeRobot"),
                (@"pingdom", UaKind.Monitor, "Pingdom"),
                (@"statuscake", UaKind.Monitor, "StatusCake"),
                (@"betteruptime|better stack", UaKind.Monitor, "Better Uptime"),
                (@"site24x7", UaKind.Monitor, "Site24x7"),
                (@"freshping", UaKind.Monitor, "Freshping"),
                (@"hetrixtools", UaKind.Monitor, "HetrixTools"),
                (@"uptime-kuma", UaKind.Monitor, "Uptime Kuma"),
                (@"datadog", UaKind.Monitor, "Datadog"),

                // HTTP libraries and CLI tools.
                (@"^curl/", UaKind.Script, "curl"),
                (@"^wget/", UaKind.Script, "Wget"),
                (@"python-requests", UaKind.Script, "python-requests"),
                (@"python-urllib|urllib", UaKind.Script, "urllib"),
                (@"python-httpx|httpx", UaKind.Script, "httpx"),
                (@"aiohttp", UaKind.Script, "aiohttp"),
                (@"scrapy", UaKind.Script, "Scrapy"),
                (@"go-http-client", UaKind.Script, "Go http"),
                (@"java/|apache-httpclient|okhttp", UaKind.Script, "Java http"),
                (@"node-fetch|undici|axios|got \(|node\.js", UaKind.Script, "Node http"),
                (@"libwww-perl|lwp::", UaKind.Script, "Perl LWP"),
                (@"ruby|faraday", UaKind.Script, "Ruby http"),
                (@"php/|guzzlehttp", UaKind.Script, "PHP http"),
                (@"postmanruntime", UaKind.Script, "Postman"),
                (@"insomnia", UaKind.Script, "Insomnia"),
                (@"windowspowershell|powershell", UaKind.Script, "PowerShell"),
                (@"^dotnet|system\.net\.http|^\.net", UaKind.Script, ".NET http"),
                (@"reqwest|hyper/", UaKind.Script, "Rust http"),
                (@"httpie", UaKind.Script, "HTTPie"),
                (@"libcurl|pycurl", UaKind.Script, "libcurl"),
                (@"colly", UaKind.Script, "Colly"),
                (@"^mozilla/5\.0 \(compatible\)$|^mozilla/5\.0$|^mozilla/4\.0$", UaKind.Script, "Bare Mozilla"),

                // Anything else admitting to being a bot.
                (@"bot\b|crawler|spider|slurp|scraper|fetcher|checker|monitor", UaKind.OtherBot, "Bot"),
            };
            return list.Select(e => (new Regex(e.Item1, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant), e.Item2, e.Item3)).ToArray();
        }

        private static readonly Regex ChromeVer = new(@"Chrome/(\d+)", RegexOptions.Compiled);
        private static readonly Regex EdgeVer = new(@"Edg(?:e|A|iOS)?/(\d+)", RegexOptions.Compiled);
        private static readonly Regex OperaVer = new(@"OPR/(\d+)", RegexOptions.Compiled);
        private static readonly Regex FirefoxVer = new(@"Firefox/(\d+)", RegexOptions.Compiled);
        private static readonly Regex SafariVer = new(@"Version/(\d+)", RegexOptions.Compiled);
        private static readonly Regex SamsungVer = new(@"SamsungBrowser/(\d+)", RegexOptions.Compiled);
        private static readonly Regex CriOSVer = new(@"CriOS/(\d+)", RegexOptions.Compiled);
        private static readonly Regex FxiOSVer = new(@"FxiOS/(\d+)", RegexOptions.Compiled);

        public static ParsedUserAgent Parse(string? value)
        {
            var result = new ParsedUserAgent();
            if (string.IsNullOrWhiteSpace(value))
            {
                result.Kind = UaKind.Empty;
                result.Family = "(none)";
                return result;
            }

            string ua = value.Length > 512 ? value.Substring(0, 512) : value;
            result.OperatingSystem = OsOf(ua);
            result.DeviceType = DeviceOf(ua);

            foreach (var (pattern, kind, name) in Known)
            {
                if (!pattern.IsMatch(ua)) continue;
                result.Kind = kind;
                result.Family = name;
                if (kind == UaKind.Crawler) result.ClaimedCrawler = name;
                if (kind is UaKind.Crawler or UaKind.PreviewBot or UaKind.Monitor or UaKind.OtherBot or UaKind.Scanner or UaKind.Script)
                    result.DeviceType = "Bot";
                if (kind == UaKind.HeadlessBrowser) FillBrowserVersion(ua, result, keepFamily: true);
                return result;
            }

            if (ua.StartsWith("Mozilla/", StringComparison.OrdinalIgnoreCase) && FillBrowserVersion(ua, result, keepFamily: false))
            {
                result.Kind = UaKind.Browser;
                return result;
            }

            result.Kind = UaKind.Unknown;
            result.Family = ua.Split('/', ' ')[0];
            if (result.Family.Length > 32) result.Family = result.Family.Substring(0, 32);
            return result;
        }

        private static bool FillBrowserVersion(string ua, ParsedUserAgent r, bool keepFamily)
        {
            (string fam, Regex rx)? hit =
                  EdgeVer.IsMatch(ua) ? ("Edge", EdgeVer)
                : OperaVer.IsMatch(ua) ? ("Opera", OperaVer)
                : SamsungVer.IsMatch(ua) ? ("Samsung", SamsungVer)
                : CriOSVer.IsMatch(ua) ? ("Chrome", CriOSVer)
                : FxiOSVer.IsMatch(ua) ? ("Firefox", FxiOSVer)
                : ChromeVer.IsMatch(ua) ? ("Chrome", ChromeVer)
                : FirefoxVer.IsMatch(ua) ? ("Firefox", FirefoxVer)
                : ua.Contains("Safari/", StringComparison.Ordinal) && SafariVer.IsMatch(ua) ? ("Safari", SafariVer)
                : null;
            if (hit == null) return false;
            if (!keepFamily) r.Family = hit.Value.fam;
            var m = hit.Value.rx.Match(ua);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int major)) r.MajorVersion = major;
            return true;
        }

        private static string OsOf(string ua, bool includeChromeOs = true) =>
              ua.Contains("Windows NT", StringComparison.OrdinalIgnoreCase) ? "Windows"
            : ua.Contains("Android", StringComparison.OrdinalIgnoreCase) ? "Android"
            : ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) || ua.Contains("iPad", StringComparison.OrdinalIgnoreCase) ? "iOS"
            : ua.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase) ? "macOS"
            : includeChromeOs && ua.Contains("CrOS", StringComparison.Ordinal) ? "ChromeOS"
            : ua.Contains("Linux", StringComparison.OrdinalIgnoreCase) ? "Linux"
            : "Other";

        private static string DeviceOf(string ua) =>
              ua.Contains("iPad", StringComparison.OrdinalIgnoreCase) || ua.Contains("Tablet", StringComparison.OrdinalIgnoreCase) ? "Tablet"
            : ua.Contains("Mobile", StringComparison.OrdinalIgnoreCase) || ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ? "Mobile"
            : "Desktop";
    }
}
