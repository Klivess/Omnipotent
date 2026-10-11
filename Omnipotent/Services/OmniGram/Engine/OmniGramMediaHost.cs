using Omnipotent.Services.OmniGram.Models;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Omnipotent.Services.OmniGram.Engine
{
    /// <summary>
    /// Instagram's API never accepts an upload from us: it downloads each photo/video from a public URL
    /// (resumable upload exists only for Facebook Login). This hands Instagram short-lived, HMAC-signed links
    /// — "{postId}.{index}.{expiry}.{signature}" — that resolve only to a post's prepared copy under
    /// SavedData/OmniGram/v2/prepared, so a link can neither be forged, outlive its window, nor point
    /// anywhere else on the server.
    /// </summary>
    internal sealed class OmniGramMediaHost
    {
        public const string RoutePath = "/omnigram/public/media";
        public const string ProbePostId = "probe";
        private const string ProbeFileName = "_probe.txt";

        private readonly OmniGramStore store;
        private readonly Func<DateTime> clock;
        private readonly object keyGate = new();
        private byte[]? key;

        public OmniGramMediaHost(OmniGramStore store, Func<DateTime> clock)
        {
            this.store = store;
            this.clock = clock;
        }

        /// <summary>Where klive.dev's KliveAPI is reachable from the internet (OmniSetting OmniGram_PublicBaseUrl).</summary>
        public string BaseUrl { get; set; } = "https://klive.dev";
        /// <summary>Instagram fetches within minutes; a few hours covers retries of the same container.</summary>
        public TimeSpan UrlLifetime { get; set; } = TimeSpan.FromHours(6);

        private byte[] Key()
        {
            if (key != null) return key;
            lock (keyGate) return key ??= store.Vault.DeriveKey("public-media-url");
        }

        // ─────────────────────────────── Links ───────────────────────────────

        public string CreateUrl(string postId, int index, string extension)
        {
            string token = CreateToken(postId, index, clock() + UrlLifetime);
            // The trailing name is cosmetic: some fetchers sniff the extension before the Content-Type.
            return $"{BaseUrl.TrimEnd('/')}{RoutePath}?t={token}&f=media{extension}";
        }

        public string CreateToken(string postId, int index, DateTime expiresUtc)
        {
            string payload = $"{postId}.{index.ToString(CultureInfo.InvariantCulture)}.{new DateTimeOffset(expiresUtc).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";
            return payload + "." + Sign(payload);
        }

        private string Sign(string payload)
        {
            using var hmac = new HMACSHA256(Key());
            byte[] mac = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            return Convert.ToBase64String(mac, 0, 16).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>Checks a link's signature and expiry. On success yields the post id and media index it grants.</summary>
        public bool TryValidate(string? token, out string postId, out int index, out string? error)
        {
            postId = "";
            index = -1;
            error = null;
            if (string.IsNullOrWhiteSpace(token) || token.Length > 200) { error = "missing token"; return false; }
            string[] parts = token.Split('.');
            if (parts.Length != 4) { error = "malformed token"; return false; }
            string payload = $"{parts[0]}.{parts[1]}.{parts[2]}";
            byte[] expected = Encoding.ASCII.GetBytes(Sign(payload));
            byte[] given = Encoding.ASCII.GetBytes(parts[3]);
            if (!CryptographicOperations.FixedTimeEquals(expected, given)) { error = "bad signature"; return false; }
            if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out index) || index < 0) { error = "bad index"; return false; }
            if (!long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long expiry)) { error = "bad expiry"; return false; }
            if (DateTimeOffset.FromUnixTimeSeconds(expiry).UtcDateTime < clock()) { error = "expired"; return false; }
            postId = parts[0];
            return true;
        }

        /// <summary>
        /// The prepared file a valid link grants, and its type — or null when the post or its prepared copy is
        /// gone. Anything resolving outside the prepared directory is refused.
        /// </summary>
        public (string Path, string MimeType)? Resolve(string postId, int index)
        {
            string root = Path.GetFullPath(store.PreparedDirectory) + Path.DirectorySeparatorChar;
            if (postId == ProbePostId)
            {
                string probe = Path.Combine(root, ProbeFileName);
                return File.Exists(probe) ? (probe, "text/plain") : null;
            }
            var media = store.Read(s =>
            {
                var post = s.FindPost(postId);
                return post != null && index < post.Media.Count
                    ? (PreparedPath: post.Media[index].PreparedPath, PreparedMimeType: post.Media[index].PreparedMimeType)
                    : (PreparedPath: (string?)null, PreparedMimeType: (string?)null);
            });
            if (string.IsNullOrEmpty(media.PreparedPath)) return null;
            string full = Path.GetFullPath(media.PreparedPath);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return null;
            return (full, media.PreparedMimeType ?? OmniGramMediaTools.MimeFor(full));
        }

        /// <summary>Notes a fetch of a post's media; a fetch by Meta's crawler proves the URL works from outside.</summary>
        public void RecordFetch(string postId, string? userAgent)
        {
            if (postId == ProbePostId) return;
            DateTime now = clock();
            bool meta = IsMetaFetcher(userAgent);
            store.Mutate(s =>
            {
                var post = s.FindPost(postId);
                if (post != null)
                {
                    post.Publish.MediaFetches++;
                    post.Publish.LastMediaFetchUtc = now;
                    post.Publish.LastMediaFetchAgent = Clip(userAgent, 120);
                    s.MarkPosts(post.AccountId);
                }
                if (meta)
                {
                    s.Engine.LastExternalMediaFetchUtc = now;
                    s.Engine.LastExternalMediaFetchAgent = Clip(userAgent, 120);
                    s.MarkEngine();
                }
            });
        }

        public static bool IsMetaFetcher(string? userAgent)
        {
            string ua = (userAgent ?? "").ToLowerInvariant();
            return ua.Contains("facebookexternal") || ua.Contains("meta-external") || ua.Contains("facebookcatalog") || ua.Contains("instagram") || ua.Contains("facebot");
        }

        private static string? Clip(string? s, int max) => s == null ? null : s.Length <= max ? s : s[..max];

        // ─────────────────────────────── Self-check ───────────────────────────────

        /// <summary>
        /// Fetches a probe file through the public URL, the way Instagram would. "Could not connect" from the
        /// server itself can be a router that does not loop back to its own public address (hairpin NAT), so
        /// the result says so instead of declaring the URL broken.
        /// </summary>
        public async Task<PublicUrlCheck> CheckAsync(HttpClient http, CancellationToken ct)
        {
            DateTime now = clock();
            var check = new PublicUrlCheck { Utc = now, BaseUrl = BaseUrl };
            if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
            {
                check.Detail = $"The public base URL \"{BaseUrl}\" is not an absolute https:// URL (OmniSetting OmniGram_PublicBaseUrl).";
                return Save(check);
            }
            string nonce = Guid.NewGuid().ToString("N");
            string probePath = Path.Combine(store.PreparedDirectory, ProbeFileName);
            Directory.CreateDirectory(store.PreparedDirectory);
            await File.WriteAllTextAsync(probePath, "omnigram-probe-" + nonce, ct);
            string url = $"{BaseUrl.TrimEnd('/')}{RoutePath}?t={CreateToken(ProbePostId, 0, now.AddMinutes(5))}&f=probe.txt";
            var sw = Stopwatch.StartNew();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                using var response = await http.GetAsync(url, cts.Token);
                string body = await response.Content.ReadAsStringAsync(cts.Token);
                check.Status = (int)response.StatusCode;
                check.LatencyMs = sw.ElapsedMilliseconds;
                check.Ok = response.IsSuccessStatusCode && body.Contains(nonce, StringComparison.Ordinal);
                check.Detail = check.Ok
                    ? $"{BaseUrl} served the probe in {sw.ElapsedMilliseconds} ms."
                    : response.IsSuccessStatusCode
                        ? $"{BaseUrl} answered, but not with this server's probe — the domain may point somewhere else."
                        : $"{BaseUrl} answered HTTP {(int)response.StatusCode}; Instagram would get the same and fail with error 9004.";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                check.LatencyMs = sw.ElapsedMilliseconds;
                check.Detail = $"Could not reach {BaseUrl} from this server ({ex.GetBaseException().Message}). If the site works from outside, the router may not loop back to its own address; " +
                    "a successful Instagram fetch (shown below) is the real proof.";
            }
            finally
            {
                try { File.Delete(probePath); } catch { }
            }
            return Save(check);
        }

        private PublicUrlCheck Save(PublicUrlCheck check)
        {
            store.Mutate(s =>
            {
                s.Engine.PublicUrlCheck = check;
                s.MarkEngine();
            });
            return check;
        }
    }
}
