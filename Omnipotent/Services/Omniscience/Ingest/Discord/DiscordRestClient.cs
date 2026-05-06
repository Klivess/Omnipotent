using Newtonsoft.Json.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Ingest.Discord
{
    /// <summary>
    /// Minimal user-token Discord REST wrapper (api/v9). Adds the "X-Super-Properties" /
    /// User-Agent fingerprint a real client sends so requests aren't immediately rejected.
    /// Handles 429 rate limits transparently.
    /// </summary>
    public class DiscordRestClient
    {
        public const string ApiBase = "https://discord.com/api/v9";

        // base64 of {"os":"Windows","browser":"Chrome","device":"","system_locale":"en-GB","browser_user_agent":"Mozilla/5.0 ...","browser_version":"124.0.0.0","os_version":"10","referrer":"","referring_domain":"","referrer_current":"","referring_domain_current":"","release_channel":"stable","client_build_number":290000,"client_event_source":null}
        public const string SuperProperties =
            "eyJvcyI6IldpbmRvd3MiLCJicm93c2VyIjoiQ2hyb21lIiwiZGV2aWNlIjoiIiwic3lzdGVtX2xvY2FsZSI6ImVuLUdCIiwiYnJvd3Nlcl91c2VyX2FnZW50IjoiTW96aWxsYS81LjAgKFdpbmRvd3MgTlQgMTAuMDsgV2luNjQ7IHg2NCkgQXBwbGVXZWJLaXQvNTM3LjM2IChLSFRNTCwgbGlrZSBHZWNrbykgQ2hyb21lLzEyNC4wLjAuMCBTYWZhcmkvNTM3LjM2IiwiYnJvd3Nlcl92ZXJzaW9uIjoiMTI0LjAuMC4wIiwib3NfdmVyc2lvbiI6IjEwIiwicmVmZXJyZXIiOiIiLCJyZWZlcnJpbmdfZG9tYWluIjoiIiwicmVmZXJyZXJfY3VycmVudCI6IiIsInJlZmVycmluZ19kb21haW5fY3VycmVudCI6IiIsInJlbGVhc2VfY2hhbm5lbCI6InN0YWJsZSIsImNsaWVudF9idWlsZF9udW1iZXIiOjI5MDAwMCwiY2xpZW50X2V2ZW50X3NvdXJjZSI6bnVsbH0=";

        public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

        private readonly HttpClient http;
        private readonly string token;

        public DiscordRestClient(HttpClient http, string token)
        {
            this.http = http;
            this.token = token;
        }

        private HttpRequestMessage Build(HttpMethod method, string path)
        {
            var req = new HttpRequestMessage(method, ApiBase + path);
            req.Headers.TryAddWithoutValidation("Authorization", token);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.TryAddWithoutValidation("X-Super-Properties", SuperProperties);
            req.Headers.TryAddWithoutValidation("X-Discord-Locale", "en-GB");
            req.Headers.TryAddWithoutValidation("Accept", "*/*");
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-GB,en;q=0.9");
            req.Headers.TryAddWithoutValidation("Origin", "https://discord.com");
            req.Headers.TryAddWithoutValidation("Referer", "https://discord.com/channels/@me");
            return req;
        }

        public async Task<JToken> GetAsync(string path, CancellationToken ct)
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                using var req = Build(HttpMethod.Get, path);
                using var resp = await http.SendAsync(req, ct);
                if (resp.StatusCode == (HttpStatusCode)429)
                {
                    string body = await resp.Content.ReadAsStringAsync(ct);
                    double retryAfter = 1.0;
                    try
                    {
                        var j = JObject.Parse(body);
                        retryAfter = j.Value<double?>("retry_after") ?? 1.0;
                    }
                    catch { }
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(retryAfter + 0.25, 30)), ct);
                    continue;
                }
                resp.EnsureSuccessStatusCode();
                string text = await resp.Content.ReadAsStringAsync(ct);
                return JToken.Parse(text);
            }
            throw new HttpRequestException($"Rate-limited too many times on {path}");
        }

        // ── High level helpers ──

        public Task<JToken> GetSelfAsync(CancellationToken ct) => GetAsync("/users/@me", ct);
        public Task<JToken> GetGuildsAsync(CancellationToken ct) => GetAsync("/users/@me/guilds", ct);
        public Task<JToken> GetDmChannelsAsync(CancellationToken ct) => GetAsync("/users/@me/channels", ct);
        public Task<JToken> GetGuildChannelsAsync(string guildId, CancellationToken ct) => GetAsync($"/guilds/{guildId}/channels", ct);
        public Task<JToken> GetUserAsync(string userId, CancellationToken ct) => GetAsync($"/users/{userId}", ct);

        public Task<JToken> GetMessagesBeforeAsync(string channelId, string? beforeId, int limit, CancellationToken ct)
        {
            string q = $"/channels/{channelId}/messages?limit={Math.Clamp(limit, 1, 100)}";
            if (!string.IsNullOrEmpty(beforeId)) q += "&before=" + beforeId;
            return GetAsync(q, ct);
        }

        public Task<JToken> GetMessagesAfterAsync(string channelId, string afterId, int limit, CancellationToken ct)
        {
            string q = $"/channels/{channelId}/messages?limit={Math.Clamp(limit, 1, 100)}&after={afterId}";
            return GetAsync(q, ct);
        }
    }
}
