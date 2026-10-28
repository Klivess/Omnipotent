using System.Net;
using System.Net.Http.Headers;

namespace Omnipotent.Services.OmniBlox.Roblox
{
    /// <summary>A Roblox API call failed in a way the caller should hear about (status + body).</summary>
    public sealed class RobloxApiException : Exception
    {
        public RobloxApiException(string endpoint, HttpStatusCode status, string body, string? hint = null)
            : base($"{endpoint} → {(int)status} {status}: {Trim(body)}{(hint == null ? "" : " — " + hint)}")
        {
            Endpoint = endpoint;
            Status = status;
            Body = body;
        }

        public string Endpoint { get; }
        public HttpStatusCode Status { get; }
        public string Body { get; }
        public bool IsAuth => Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
        public bool IsNotFound => Status == HttpStatusCode.NotFound;

        private static string Trim(string body) => body.Length <= 400 ? body : body[..400] + "…";
    }

    /// <summary>The key is not configured, so an Open Cloud call cannot even be attempted.</summary>
    public sealed class RobloxKeyMissingException : Exception
    {
        public RobloxKeyMissingException(string capability)
            : base($"No Roblox Open Cloud API key is configured (OmniSetting OmniBlox_RobloxApiKey), so {capability} is unavailable.") { }
    }

    public sealed record RobloxResponse(HttpStatusCode Status, string Body, byte[] Bytes, HttpResponseHeaders Headers, HttpContentHeaders? ContentHeaders)
    {
        public bool Ok => (int)Status is >= 200 and < 300;
    }

    /// <summary>
    /// A multipart body Roblox's edge firewall lets through. .NET's own MultipartFormDataContent quotes its
    /// boundary (<c>boundary="…"</c>): valid HTTP, but the firewall in front of developer products answers that
    /// with 403 9009 "WAF Blocked Request" before the key is even checked, so no product could be changed or taken
    /// off sale. The boundary here is a bare token that never needs quoting, and parts carry quoted names and a
    /// plain filename (no <c>filename*</c>, which RFC 7578 forbids in form data).
    /// </summary>
    public static class RobloxForm
    {
        public static MultipartFormDataContent Create()
        {
            string boundary = "omniblox" + Guid.NewGuid().ToString("N");
            var form = new MultipartFormDataContent(boundary);
            form.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=" + boundary);
            return form;
        }

        public static void AddText(MultipartFormDataContent form, string name, string value)
        {
            var part = new StringContent(value);
            part.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = Quote(name) };
            form.Add(part);
        }

        public static void AddJson(MultipartFormDataContent form, string name, string json)
        {
            var part = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            part.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = Quote(name) };
            form.Add(part);
        }

        public static void AddFile(MultipartFormDataContent form, string name, byte[] content, string contentType, string fileName)
        {
            var part = new ByteArrayContent(content);
            part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            part.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = Quote(name), FileName = Quote(fileName) };
            form.Add(part);
        }

        private static string Quote(string s) => "\"" + s.Replace("\"", "") + "\"";
    }

    /// <summary>
    /// Shared transport for every Roblox call: per-endpoint-family rate buckets (kept well under the
    /// published limits, since one key serves the whole studio), retries with exponential backoff on
    /// 429 / 5xx / transport errors, and Retry-After when Roblox sends it.
    /// </summary>
    public sealed class RobloxHttp
    {
        private readonly HttpClient http;
        private readonly Func<DateTime> clock;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;
        private readonly Dictionary<string, RateBucket> buckets = new(StringComparer.Ordinal);
        private readonly object sync = new();

        public const string UserAgent = "OmniBlox/1.0 (+https://klive.dev)";

        public RobloxHttp(HttpClient http, Func<DateTime>? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            this.http = http;
            this.clock = clock ?? (() => DateTime.UtcNow);
            this.delay = delay ?? Task.Delay;
        }

        public static HttpClient CreateDefaultClient()
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                // No shared cookie jar: the owner's Roblox session goes only on the calls that add it themselves
                // (RobloxSessionClient), never on public or Open Cloud requests.
                UseCookies = false,
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(100) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            return client;
        }

        /// <summary>Requests per minute allowed per bucket (a fraction of each published limit).</summary>
        public static readonly IReadOnlyDictionary<string, int> PerMinute = new Dictionary<string, int>
        {
            ["public"] = 120,
            ["toolbox"] = 300,
            ["thumbnails"] = 120,
            ["asset-delivery"] = 300,
            ["asset-create"] = 60,
            ["asset-operation"] = 120,
            ["publish"] = 20,
            ["universe"] = 60,
            ["game-pass"] = 120,
            ["developer-product"] = 90,
            ["luau-create"] = 4,
            ["luau-read"] = 120,
            ["luau-logs"] = 30,
            ["analytics"] = 60,
            ["thumbnail-personalization"] = 20,
            ["session"] = 30,
            ["session-upload"] = 6,
            ["session-create"] = 2,
            ["quick-login"] = 30,
            ["cdn"] = 600,
        };

        public async Task<RobloxResponse> SendAsync(string bucket, Func<HttpRequestMessage> build, CancellationToken ct, int maxAttempts = 4)
        {
            Exception? lastError = null;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                await AcquireAsync(bucket, ct);
                HttpResponseMessage? response = null;
                try
                {
                    using var request = build();
                    response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
                    byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct);
                    string body = LooksTextual(response.Content.Headers.ContentType) ? System.Text.Encoding.UTF8.GetString(bytes) : "";
                    var result = new RobloxResponse(response.StatusCode, body, bytes, response.Headers, response.Content.Headers);
                    if (IsRetryable(response.StatusCode) && attempt < maxAttempts)
                    {
                        await delay(Backoff(attempt, response.Headers.RetryAfter), ct);
                        continue;
                    }
                    return result;
                }
                catch (HttpRequestException ex) when (attempt < maxAttempts)
                {
                    lastError = ex;
                    await delay(Backoff(attempt, null), ct);
                }
                catch (TaskCanceledException ex) when (!ct.IsCancellationRequested && attempt < maxAttempts)
                {
                    lastError = ex; // the client timeout, not our caller
                    await delay(Backoff(attempt, null), ct);
                }
                finally
                {
                    response?.Dispose();
                }
            }
            throw lastError ?? new HttpRequestException("Roblox request failed.");
        }

        private static bool IsRetryable(HttpStatusCode status) =>
            status == HttpStatusCode.TooManyRequests || (int)status >= 500;

        private static bool LooksTextual(MediaTypeHeaderValue? type)
        {
            string media = type?.MediaType ?? "";
            return media.Length == 0 || media.Contains("json") || media.StartsWith("text/") || media.Contains("xml");
        }

        internal static TimeSpan Backoff(int attempt, RetryConditionHeaderValue? retryAfter)
        {
            if (retryAfter?.Delta is TimeSpan d && d > TimeSpan.Zero) return d < TimeSpan.FromMinutes(2) ? d : TimeSpan.FromMinutes(2);
            return TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)); // 1s, 2s, 4s — Roblox's own advice
        }

        private async Task AcquireAsync(string bucket, CancellationToken ct)
        {
            while (true)
            {
                TimeSpan wait;
                lock (sync)
                {
                    if (!buckets.TryGetValue(bucket, out var b))
                    {
                        b = new RateBucket(PerMinute.TryGetValue(bucket, out int perMinute) ? perMinute : 60);
                        buckets[bucket] = b;
                    }
                    wait = b.TryTake(clock());
                }
                if (wait <= TimeSpan.Zero) return;
                await delay(wait, ct);
            }
        }

        /// <summary>A sliding one-minute window of request timestamps.</summary>
        private sealed class RateBucket
        {
            private readonly int perMinute;
            private readonly Queue<DateTime> taken = new();

            public RateBucket(int perMinute) => this.perMinute = Math.Max(1, perMinute);

            public TimeSpan TryTake(DateTime now)
            {
                while (taken.Count > 0 && now - taken.Peek() >= TimeSpan.FromMinutes(1)) taken.Dequeue();
                if (taken.Count < perMinute)
                {
                    taken.Enqueue(now);
                    return TimeSpan.Zero;
                }
                var wait = taken.Peek().AddMinutes(1) - now;
                return wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(10);
            }
        }
    }
}
