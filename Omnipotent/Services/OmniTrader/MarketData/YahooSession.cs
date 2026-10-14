using System.Net;

namespace Omnipotent.Services.OmniTrader.MarketData
{
    /// <summary>
    /// Keyless Yahoo Finance session. The chart and search endpoints need nothing; quoteSummary,
    /// batch quotes, options and screeners need a consent cookie plus a "crumb" bound to it. Both are
    /// fetched on first use and refreshed once whenever Yahoo answers 401 "Invalid Crumb".
    /// </summary>
    public sealed class YahooSession
    {
        public const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

        private readonly CookieContainer cookies = new();
        private readonly HttpClient http;
        private readonly SemaphoreSlim crumbLock = new(1, 1);
        private string? crumb;
        private DateTime crumbUtc;

        public YahooSession()
        {
            var handler = new HttpClientHandler
            {
                CookieContainer = cookies,
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
            };
            http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json,text/plain,*/*");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        }

        public HttpClient Http => http;

        /// <summary>GET without a crumb (chart, search, RSS).</summary>
        public async Task<(HttpStatusCode Status, string Body)> GetAsync(string url, CancellationToken ct = default)
        {
            using var resp = await http.GetAsync(url, ct);
            return (resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
        }

        /// <summary>GET that appends the crumb, retrying once with a fresh one on 401.</summary>
        public async Task<(HttpStatusCode Status, string Body)> GetWithCrumbAsync(string url, CancellationToken ct = default)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                string token = await GetCrumbAsync(forceRefresh: attempt > 0, ct);
                string full = url + (url.Contains('?') ? "&" : "?") + "crumb=" + Uri.EscapeDataString(token);
                using var resp = await http.GetAsync(full, ct);
                string body = await resp.Content.ReadAsStringAsync(ct);
                if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
                return (resp.StatusCode, body);
            }
            return (HttpStatusCode.Unauthorized, "");
        }

        private async Task<string> GetCrumbAsync(bool forceRefresh, CancellationToken ct)
        {
            if (!forceRefresh && crumb != null && DateTime.UtcNow - crumbUtc < TimeSpan.FromHours(6)) return crumb;
            await crumbLock.WaitAsync(ct);
            try
            {
                if (!forceRefresh && crumb != null && DateTime.UtcNow - crumbUtc < TimeSpan.FromHours(6)) return crumb;
                // fc.yahoo.com answers 404 but sets the A1/A3 consent cookies the crumb is bound to.
                try { using var _ = await http.GetAsync("https://fc.yahoo.com/", ct); } catch { }
                string value = "";
                foreach (var host in new[] { "query1", "query2" })
                {
                    try
                    {
                        using var resp = await http.GetAsync($"https://{host}.finance.yahoo.com/v1/test/getcrumb", ct);
                        value = (await resp.Content.ReadAsStringAsync(ct)).Trim();
                        if (resp.IsSuccessStatusCode && value.Length is > 0 and < 64 && !value.Contains('<')) break;
                        value = "";
                    }
                    catch { value = ""; }
                }
                if (string.IsNullOrEmpty(value)) throw new HttpRequestException("Yahoo crumb unavailable");
                crumb = value;
                crumbUtc = DateTime.UtcNow;
                return crumb;
            }
            finally
            {
                crumbLock.Release();
            }
        }
    }
}
