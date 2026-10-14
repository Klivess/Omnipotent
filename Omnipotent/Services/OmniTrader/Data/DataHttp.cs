using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Net;

namespace Omnipotent.Services.OmniTrader.Data
{
    /// <summary>
    /// Shared HTTP for every free data source. Two identities: a browser-shaped client for sites that
    /// refuse anything else, and a declared research client for SEC, BLS and Wikipedia, which ask
    /// automated callers to identify themselves. Every call is timed into <see cref="SourceHealth"/>.
    /// </summary>
    internal static class DataHttp
    {
        public const string ResearchAgent = "OmniTrader research admin@klive.dev";

        public static readonly HttpClient Browser = Create(MarketData.YahooSession.UserAgent);
        public static readonly HttpClient Research = Create(ResearchAgent);

        private static HttpClient Create(string userAgent)
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/csv, application/xml, text/xml, */*");
            return client;
        }

        public static async Task<string> GetStringAsync(string source, string category, string url, HttpClient? client = null,
            CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var resp = await (client ?? Browser).GetAsync(url, ct);
                string body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                    throw new HttpRequestException($"{source} {(int)resp.StatusCode}");
                SourceHealth.Success(source, sw.Elapsed.TotalMilliseconds, category);
                return body;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                SourceHealth.Failure(source, ex.Message, category);
                throw;
            }
        }

        public static async Task<JToken> GetJsonAsync(string source, string category, string url, HttpClient? client = null,
            CancellationToken ct = default)
            => JToken.Parse(await GetStringAsync(source, category, url, client, ct));

        public static async Task<string> PostJsonAsync(string source, string category, string url, string json, HttpClient? client = null,
            CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                using var resp = await (client ?? Research).PostAsync(url, content, ct);
                string body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{source} {(int)resp.StatusCode}");
                SourceHealth.Success(source, sw.Elapsed.TotalMilliseconds, category);
                return body;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                SourceHealth.Failure(source, ex.Message, category);
                throw;
            }
        }

        // ── JSON helpers tolerant of Yahoo's {raw, fmt} wrappers ─────────────────

        public static double? Num(JToken? t)
        {
            if (t == null || t.Type == JTokenType.Null || t.Type == JTokenType.Undefined) return null;
            if (t.Type == JTokenType.Object) return Num(t["raw"]);
            if (t.Type is JTokenType.Integer or JTokenType.Float) return t.Value<double>();
            if (t.Type == JTokenType.String && double.TryParse(t.Value<string>(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
            return null;
        }

        public static string? Str(JToken? t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.Object) return t["fmt"]?.Value<string>() ?? t["raw"]?.ToString();
            return t.ToString();
        }

        public static DateTime? Unix(JToken? t)
        {
            var v = Num(t);
            if (v == null || v <= 0) return null;
            try { return DateTimeOffset.FromUnixTimeSeconds((long)v.Value).UtcDateTime; } catch { return null; }
        }

        public static DateTime? Date(JToken? t)
        {
            var s = t?.Type == JTokenType.Object ? t["fmt"]?.Value<string>() : t?.ToString();
            return DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
        }
    }
}
