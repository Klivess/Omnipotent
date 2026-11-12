using System.Security.Cryptography;
using System.Text;

namespace Omnipotent.Services.OmniscienceV3.Modules.Links
{
    /// <summary>
    /// A link as Omniscience keeps it. <c>url</c> is normalised (the same thing shared twice is one link); a link that may
    /// carry a credential or a one-time token is <c>sensitive</c>: its query is not kept and it is never looked up anywhere.
    /// </summary>
    public sealed record ParsedLink(string UrlId, string Url, string Host, string Site, string? Provider, string Kind, bool Sensitive);

    /// <summary>
    /// Finds and normalises the links in a message. Everything here is structural (URL parsing and tables of hosts): no
    /// network, no guessing by keywords in the text.
    /// </summary>
    public static class LinkParser
    {
        public const int MaxPerMessage = 10;

        /// <summary>Query parameters that only track where a click came from. They are dropped, so a link is the same link whoever shared it.</summary>
        private static readonly HashSet<string> Tracking = new(StringComparer.OrdinalIgnoreCase)
        {
            "fbclid", "gclid", "dclid", "msclkid", "igshid", "igsh", "si", "feature", "ref", "ref_src", "ref_url", "referrer", "source", "src", "mc_cid", "mc_eid", "spm", "share_id",
            "share", "context", "pp", "s", "t", "cmpid", "ocid", "ncid", "yclid", "_hsenc", "_hsmi", "mkt_tok", "vero_id", "wt_mc", "trk", "app", "ab_channel",
        };

        /// <summary>Query parameters that carry a credential, a signature or a one-time code. A link with one is never looked up.</summary>
        private static readonly HashSet<string> Secret = new(StringComparer.OrdinalIgnoreCase)
        {
            "token", "access_token", "id_token", "refresh_token", "auth_token", "authtoken", "code", "sig", "signature", "key", "apikey", "api_key", "auth", "authkey", "password", "pass",
            "pwd", "secret", "session", "sessionid", "session_id", "sid", "otp", "magic", "verification", "verify", "reset", "resetkey", "confirm", "confirmation", "ticket", "jwt",
            "credential", "credentials", "x-amz-signature", "x-amz-credential", "x-amz-security-token", "x-goog-signature", "hmac", "expires", "invite", "invitation", "login_token",
            "state", "nonce", "oobcode", "resourcekey", "usp", "authuser", "ex", "is", "hm",
        };

        /// <summary>Path words of pages that sign someone in, change an account or act on an emailed link.</summary>
        private static readonly string[] SecretPaths =
        {
            "login", "signin", "sign-in", "sign_in", "logout", "signout", "oauth", "authorize", "authorise", "sso", "reset", "recover", "verify", "verification", "confirm", "activate",
            "unsubscribe", "password", "magic-link", "magiclink", "invite", "invitation", "join", "claim", "redeem", "checkout", "billing", "account",
        };

        /// <summary>Places where a link is itself the permission to read a private file.</summary>
        private static readonly string[] PrivateHosts =
        {
            "docs.google.com", "drive.google.com", "photos.google.com", "photos.app.goo.gl", "dropbox.com", "onedrive.live.com", "1drv.ms", "sharepoint.com", "icloud.com", "wetransfer.com",
            "we.tl", "mega.nz", "box.com", "notion.so", "notion.site", "pastebin.com", "discord.gg", "discord.com", "discordapp.com", "t.me", "chat.whatsapp.com", "zoom.us", "meet.google.com",
            "teams.microsoft.com", "calendly.com", "paypal.com", "paypal.me",
        };

        private static readonly string[] TwoLabelSuffixes =
        {
            "co.uk", "org.uk", "ac.uk", "gov.uk", "me.uk", "com.au", "net.au", "org.au", "co.nz", "co.jp", "ne.jp", "or.jp", "com.br", "com.mx", "com.ar", "co.in", "co.za", "com.tr", "com.cn",
            "com.hk", "com.sg", "com.tw", "co.kr", "co.il", "com.pl", "com.ua", "github.io", "blogspot.com", "wordpress.com", "substack.com", "tumblr.com", "itch.io", "bandcamp.com",
        };

        public static string IdOf(string url) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..20].ToLowerInvariant();

        /// <summary>The registrable part of a host, near enough without the full public-suffix list: the last two labels, or
        /// three under a known two-label suffix (so <c>news.bbc.co.uk</c> is <c>bbc.co.uk</c>).</summary>
        public static string SiteOf(string host)
        {
            string[] labels = host.Split('.');
            if (labels.Length <= 2 || System.Net.IPAddress.TryParse(host, out _)) return host;
            string lastTwo = labels[^2] + "." + labels[^1];
            return TwoLabelSuffixes.Contains(lastTwo, StringComparer.Ordinal) ? labels[^3] + "." + lastTwo : lastTwo;
        }

        /// <summary>The links written in a message, in order, each once, at most <see cref="MaxPerMessage"/>.</summary>
        public static IReadOnlyList<ParsedLink> Extract(string? text)
        {
            var result = new List<ParsedLink>();
            if (string.IsNullOrEmpty(text)) return result;
            int i = 0;
            while (result.Count < MaxPerMessage)
            {
                int http = text.IndexOf("http", i, StringComparison.OrdinalIgnoreCase);
                if (http < 0) break;
                int end = http;
                while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not ('<' or '>' or '"' or '`' or '|')) end++;
                i = Math.Max(end, http + 4);
                string raw = text[http..end];
                // Sentence punctuation and an unbalanced closing bracket are not part of the link.
                while (raw.Length > 0 && (raw[^1] is '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '*' or '_' or '~'
                       || (raw[^1] == ')' && raw.Count(c => c == ')') > raw.Count(c => c == '(')) || (raw[^1] == ']' && raw.Count(c => c == ']') > raw.Count(c => c == '['))))
                    raw = raw[..^1];
                if (Parse(raw) is { } link && result.All(r => r.UrlId != link.UrlId)) result.Add(link);
            }
            return result;
        }

        /// <summary>Normalises one URL, or null when it is not a web link (or is one of the platform's own media links).</summary>
        public static ParsedLink? Parse(string raw)
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return null;
            string host = uri.IdnHost.ToLowerInvariant().TrimEnd('.');
            if (host.Length == 0 || !host.Contains('.')) return null;
            foreach (string prefix in new[] { "www.", "m.", "mobile.", "amp." })
                if (host.StartsWith(prefix, StringComparison.Ordinal) && host.Count(c => c == '.') >= 2) { host = host[prefix.Length..]; break; }
            // The platform's own files (attachments, avatars, emoji) are media, not links.
            if (host is "cdn.discordapp.com" or "media.discordapp.net" or "images-ext-1.discordapp.net" or "images-ext-2.discordapp.net") return null;

            string path = uri.AbsolutePath;
            var query = ParseQuery(uri.Query);
            bool sensitive = uri.UserInfo.Length > 0 || IsPrivateHost(host) || query.Any(q => Secret.Contains(q.Key) || LooksLikeToken(q.Value))
                             || path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(seg => SecretPaths.Contains(seg.ToLowerInvariant(), StringComparer.Ordinal));
            var (provider, kind, canonical) = Canonical(host, path, query);
            string url;
            if (canonical != null && !sensitive)
            {
                // Every spelling of the thing is the same link, on the provider's own site.
                url = canonical;
                host = new Uri(canonical).IdnHost;
                if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
            }
            else
            {
                string cleanPath = path.Length > 1 ? path.TrimEnd('/') : "";
                var kept = sensitive ? new List<KeyValuePair<string, string>>()
                    : query.Where(q => !Tracking.Contains(q.Key) && !q.Key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase)).OrderBy(q => q.Key, StringComparer.Ordinal).ThenBy(q => q.Value, StringComparer.Ordinal).ToList();
                url = "https://" + host + (uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)) + cleanPath
                      + (kept.Count == 0 ? "" : "?" + string.Join("&", kept.Select(q => Uri.EscapeDataString(q.Key) + (q.Value.Length == 0 ? "" : "=" + Uri.EscapeDataString(q.Value)))));
            }
            if (sensitive && host is "discord.gg" or "discord.com" or "discordapp.com" or "t.me" or "chat.whatsapp.com") kind = "invite";
            return new ParsedLink(IdOf(url), url, host, SiteOf(host), provider, kind, sensitive);
        }

        private static bool IsPrivateHost(string host) => PrivateHosts.Any(p => host == p || host.EndsWith("." + p, StringComparison.Ordinal));

        /// <summary>A long run of letters and digits with no words in it: an opaque token, whatever its parameter is called.</summary>
        private static bool LooksLikeToken(string value) =>
            value.Length >= 32 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '=' or '+' or '/' or '%') && value.Count(char.IsAsciiDigit) >= 4;

        private static List<KeyValuePair<string, string>> ParseQuery(string query)
        {
            var result = new List<KeyValuePair<string, string>>();
            foreach (string part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                string key = Uri.UnescapeDataString((eq < 0 ? part : part[..eq]).Replace('+', ' ')), value = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
                if (key.Length > 0) result.Add(new(key, value));
            }
            return result;
        }

        /// <summary>The provider and kind of a host, and the one canonical URL of the thing where a provider has several spellings.</summary>
        private static (string? Provider, string Kind, string? Canonical) Canonical(string host, string path, List<KeyValuePair<string, string>> query)
        {
            string[] seg = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string? Q(string key) => query.FirstOrDefault(q => q.Key == key).Value is { Length: > 0 } v ? v : null;
            static bool Id(string s, int min = 3) => s.Length >= min && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

            switch (SiteOf(host))
            {
                case "youtube.com" or "youtu.be" or "youtube-nocookie.com":
                {
                    string? video = host == "youtu.be" ? seg.FirstOrDefault() : seg.Length >= 2 && seg[0] is "shorts" or "embed" or "live" or "v" ? seg[1] : seg.FirstOrDefault() == "watch" ? Q("v") : null;
                    if (video != null && Id(video, 6)) return ("youtube", "video", "https://www.youtube.com/watch?v=" + video);
                    if (seg.FirstOrDefault() == "playlist" && Q("list") is { } list && Id(list, 6)) return ("youtube", "video", "https://www.youtube.com/playlist?list=" + list);
                    return ("youtube", "video", null);
                }
                case "twitter.com" or "x.com" or "fxtwitter.com" or "vxtwitter.com" or "fixupx.com" or "nitter.net":
                    if (seg.Length >= 3 && seg[1] == "status" && seg[2].All(char.IsAsciiDigit)) return ("x", "post", $"https://x.com/{seg[0]}/status/{seg[2]}");
                    return ("x", "post", null);
                case "spotify.com":
                {
                    var s = seg.Where(x => !x.StartsWith("intl-", StringComparison.Ordinal)).ToArray();
                    if (s.Length >= 2 && s[0] is "track" or "album" or "playlist" or "artist" or "episode" or "show" && Id(s[1], 6)) return ("spotify", "music", $"https://open.spotify.com/{s[0]}/{s[1]}");
                    return ("spotify", "music", null);
                }
                case "steampowered.com" or "steamcommunity.com":
                    if (host == "store.steampowered.com" && seg.Length >= 2 && seg[0] == "app" && seg[1].All(char.IsAsciiDigit)) return ("steam", "game", "https://store.steampowered.com/app/" + seg[1]);
                    return ("steam", "game", null);
                case "reddit.com" or "redd.it":
                    if (seg.Length >= 4 && seg[0] == "r" && seg[2] == "comments" && Id(seg[3], 2)) return ("reddit", "post", $"https://www.reddit.com/r/{seg[1]}/comments/{seg[3]}");
                    return ("reddit", "post", null);
                case "tiktok.com": return ("tiktok", "video", null);
                case "twitch.tv": return ("twitch", "video", null);
                case "vimeo.com": return ("vimeo", "video", seg.Length == 1 && seg[0].All(char.IsAsciiDigit) ? "https://vimeo.com/" + seg[0] : null);
                case "soundcloud.com": return ("soundcloud", "music", null);
                case "bandcamp.com": return ("bandcamp", "music", null);
                case "apple.com" when host == "music.apple.com": return ("apple_music", "music", null);
                case "instagram.com": return ("instagram", "post", null);
                case "facebook.com" or "fb.watch": return ("facebook", "post", null);
                case "bsky.app": return ("bluesky", "post", null);
                case "github.com" or "gitlab.com": return (SiteOf(host)[..^4], "code", null);
                case "stackoverflow.com" or "stackexchange.com": return ("stackoverflow", "code", null);
                case "wikipedia.org": return ("wikipedia", "reference", null);
                case "tenor.com" or "giphy.com": return (SiteOf(host)[..^4], "gif", null);
                case "imgur.com": return ("imgur", "image", null);
                case "epicgames.com" or "itch.io" or "gog.com": return (SiteOf(host).Split('.')[0], "game", null);
                case "amazon.com" or "amazon.co.uk" or "ebay.com" or "ebay.co.uk" or "aliexpress.com": return (SiteOf(host).Split('.')[0], "shop", null);
                default: return (null, "page", null);
            }
        }

        /// <summary>
        /// The provider's own metadata endpoint for a link, when it has one Omniscience may ask: a fixed list of hosts, asked
        /// about a public link. The shared link itself is never requested, and a sensitive link is never sent anywhere.
        /// </summary>
        public static Uri? MetadataEndpoint(ParsedLink link)
        {
            if (link.Sensitive) return null;
            string encoded = Uri.EscapeDataString(link.Url);
            string? endpoint = link.Provider switch
            {
                "youtube" => "https://www.youtube.com/oembed?format=json&url=" + encoded,
                "spotify" => "https://open.spotify.com/oembed?url=" + encoded,
                "reddit" => "https://www.reddit.com/oembed?url=" + encoded,
                "vimeo" => "https://vimeo.com/api/oembed.json?url=" + encoded,
                "soundcloud" => "https://soundcloud.com/oembed?format=json&url=" + encoded,
                "tiktok" => "https://www.tiktok.com/oembed?url=" + encoded,
                "x" => "https://publish.twitter.com/oembed?omit_script=1&dnt=1&url=" + encoded,
                "steam" when link.Url.StartsWith("https://store.steampowered.com/app/", StringComparison.Ordinal) =>
                    "https://store.steampowered.com/api/appdetails?filters=basic&appids=" + link.Url["https://store.steampowered.com/app/".Length..],
                _ => null,
            };
            return endpoint == null ? null : new Uri(endpoint);
        }

        /// <summary>The only hosts a metadata request may go to.</summary>
        public static readonly IReadOnlySet<string> EndpointHosts = new HashSet<string>(StringComparer.Ordinal)
        {
            "www.youtube.com", "open.spotify.com", "www.reddit.com", "vimeo.com", "soundcloud.com", "www.tiktok.com", "publish.twitter.com", "store.steampowered.com",
        };
    }
}
