using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.OmniscienceV3.Modules.Links
{
    /// <summary>What a provider's metadata endpoint said about a link. It is content from the internet: data, never instructions.</summary>
    public sealed record LinkMetadata(string? Title, string? Author, string? Description);

    /// <summary>A metadata request that is not allowed (a host outside the list, a private address, a redirect out of the list).</summary>
    public sealed class LinkFetchRefusedException : Exception
    {
        public LinkFetchRefusedException(string message) : base(message) { }
    }

    /// <summary>Asks a provider's metadata endpoint about a public link. The only network access of the links module.</summary>
    public interface ILinkFetcher
    {
        /// <summary>The body the endpoint answered with, or null when it knows nothing about the link (404, 410, 401, 403).
        /// Throws <see cref="LinkFetchRefusedException"/> when the request is not allowed, and an
        /// <see cref="HttpRequestException"/> when it failed in a way worth trying again.</summary>
        Task<string?> GetAsync(Uri endpoint, CancellationToken ct);
    }

    /// <summary>
    /// The real fetcher (plan N15 "no arbitrary fetching"):
    /// <list type="bullet">
    /// <item>only <c>https</c>, only the hosts in <see cref="LinkParser.EndpointHosts"/>, default port, no credentials in the address;</item>
    /// <item>the host is resolved here and refused when any of its addresses is private, loopback or link-local, and the
    /// connection goes to exactly the address that was checked (so a name cannot be pointed elsewhere between check and use);</item>
    /// <item>redirects are followed by hand, at most two, and each target must pass the same checks;</item>
    /// <item>no cookies, no credentials, a generic client name, eight seconds, at most 256 KB.</item>
    /// </list>
    /// </summary>
    public sealed class HttpLinkFetcher : ILinkFetcher, IDisposable
    {
        public const int MaxBytes = 256 * 1024;
        private readonly HttpClient http;

        public HttpLinkFetcher()
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseProxy = false,
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectCallback = async (context, ct) =>
                {
                    var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
                    if (addresses.Length == 0 || addresses.Any(a => !IsPublic(a))) throw new LinkFetchRefusedException($"'{context.DnsEndPoint.Host}' does not resolve to public addresses only.");
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(addresses[0], context.DnsEndPoint.Port), ct).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            };
            http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = MaxBytes };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; link-preview)");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        }

        /// <summary>Whether an address is one a metadata request may go to: nothing private, local or special.</summary>
        public static bool IsPublic(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None)) return false;
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = address.GetAddressBytes();
                return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                         || (b[0] == 192 && b[1] == 168) || (b[0] == 192 && b[1] == 0 && b[2] == 0) || (b[0] == 198 && (b[1] == 18 || b[1] == 19)) || b[0] >= 224);
            }
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                byte[] b = address.GetAddressBytes();
                bool uniqueLocal = (b[0] & 0xFE) == 0xFC, linkLocal = b[0] == 0xFE && (b[1] & 0xC0) == 0x80, multicast = b[0] == 0xFF, documentation = b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8;
                return !(uniqueLocal || linkLocal || multicast || documentation || address.IsIPv6SiteLocal);
            }
            return false;
        }

        /// <summary>The checks every request and every redirect target must pass.</summary>
        public static void Guard(Uri endpoint)
        {
            if (endpoint.Scheme != Uri.UriSchemeHttps) throw new LinkFetchRefusedException("Only https metadata endpoints are asked.");
            if (!endpoint.IsDefaultPort || endpoint.UserInfo.Length > 0) throw new LinkFetchRefusedException("A metadata endpoint has no port and no credentials in its address.");
            if (!LinkParser.EndpointHosts.Contains(endpoint.IdnHost.ToLowerInvariant())) throw new LinkFetchRefusedException($"'{endpoint.IdnHost}' is not one of the metadata endpoints Omniscience asks.");
        }

        public async Task<string?> GetAsync(Uri endpoint, CancellationToken ct)
        {
            for (int hop = 0; hop < 3; hop++)
            {
                Guard(endpoint);
                using var response = await http.GetAsync(endpoint, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                int status = (int)response.StatusCode;
                if (status is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    endpoint = location.IsAbsoluteUri ? location : new Uri(endpoint, location);
                    continue;
                }
                if (status is 401 or 403 or 404 or 410) return null;
                if (status is 408 or 429 || status >= 500) throw new HttpRequestException($"The metadata endpoint answered {status.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
                if (!response.IsSuccessStatusCode) return null;
                return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            throw new LinkFetchRefusedException("The metadata endpoint redirected more than twice.");
        }

        public void Dispose() => http.Dispose();
    }

    /// <summary>Reads what the endpoints answer: oEmbed JSON, and the store's own format for a game.</summary>
    public static class LinkMetadataParser
    {
        private static readonly Regex Tags = new("<[^>]{0,2000}>", RegexOptions.Compiled);
        private static readonly Regex Space = new(@"\s+", RegexOptions.Compiled);

        /// <summary>Plain text of a bounded length: tags and control characters removed, white space collapsed.</summary>
        public static string? Clean(string? text, int max)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string plain = WebUtility.HtmlDecode(Tags.Replace(text, " "));
            var sb = new StringBuilder(plain.Length);
            foreach (char c in plain) sb.Append(char.IsControl(c) ? ' ' : c);
            plain = Space.Replace(sb.ToString(), " ").Trim();
            if (plain.Length == 0) return null;
            return plain.Length <= max ? plain : plain[..max].TrimEnd() + "…";
        }

        public static LinkMetadata? Parse(string? provider, string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (provider == "steam")
                {
                    foreach (var app in root.EnumerateObject())
                    {
                        if (app.Value.ValueKind != JsonValueKind.Object || !app.Value.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) continue;
                        string? developer = data.TryGetProperty("developers", out var devs) && devs.ValueKind == JsonValueKind.Array && devs.GetArrayLength() > 0 && devs[0].ValueKind == JsonValueKind.String
                            ? devs[0].GetString() : null;
                        return Result(Str(data, "name"), developer, Str(data, "short_description"));
                    }
                    return null;
                }
                // A post's oEmbed carries the text only inside its HTML.
                string? description = provider is "x" or "reddit" or "tiktok" ? Str(root, "html") : null;
                return Result(Str(root, "title"), Str(root, "author_name"), description);
            }
            catch (JsonException)
            {
                return null; // not JSON: the endpoint had nothing usable
            }
        }

        private static LinkMetadata? Result(string? title, string? author, string? description)
        {
            var result = new LinkMetadata(Clean(title, 300), Clean(author, 120), Clean(description, 500));
            return result.Title == null && result.Author == null && result.Description == null ? null : result;
        }
    }
}
