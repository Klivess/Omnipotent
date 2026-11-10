using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Ingest.Discord
{
    public enum DiscordApiFailure
    {
        /// <summary>The token was refused (401): nothing will work until it is replaced.</summary>
        Unauthorised,
        /// <summary>The bot may not do this here (403): a channel it can see but not read back.</summary>
        Forbidden,
        /// <summary>The thing is gone (404): a deleted message or channel, or a server the bot left.</summary>
        NotFound,
        RateLimited,
        Server,
        Network,
        BadResponse,
    }

    public sealed class DiscordApiException : Exception
    {
        public DiscordApiException(DiscordApiFailure failure, string message, int? discordCode = null, TimeSpan? retryAfter = null, Exception? inner = null)
            : base(message, inner)
        {
            Failure = failure;
            DiscordCode = discordCode;
            RetryAfter = retryAfter;
        }

        public DiscordApiFailure Failure { get; }
        /// <summary>Discord's own error code from the response body (50001 "Missing Access", 10003 "Unknown Channel"…).</summary>
        public int? DiscordCode { get; }
        public TimeSpan? RetryAfter { get; }
        /// <summary>Trying again later can work (rate limits, Discord's own errors, the network).</summary>
        public bool IsTransient => Failure is DiscordApiFailure.RateLimited or DiscordApiFailure.Server or DiscordApiFailure.Network;
    }

    /// <summary>The reads Omniscience makes through Discord's bot API. Elements returned are detached copies.</summary>
    public interface IDiscordBotApi
    {
        /// <summary>Up to <paramref name="limit"/> messages, newest first: the latest ones, those just before
        /// <paramref name="before"/>, or those just after <paramref name="after"/>.</summary>
        Task<IReadOnlyList<JsonElement>> MessagesAsync(string channelId, string? before, string? after, int limit, CancellationToken ct);

        /// <summary>One message, or null when it no longer exists.</summary>
        Task<JsonElement?> MessageAsync(string channelId, string messageId, CancellationToken ct);

        Task<JsonElement> ChannelAsync(string channelId, CancellationToken ct);

        /// <summary>Every thread of a server that is not archived.</summary>
        Task<IReadOnlyList<JsonElement>> ActiveThreadsAsync(string guildId, CancellationToken ct);

        /// <summary>A page of a channel's archived public threads, most recently archived first.</summary>
        Task<(IReadOnlyList<JsonElement> Threads, bool HasMore)> ArchivedThreadsAsync(string channelId, string? beforeIso, CancellationToken ct);
    }

    /// <summary>
    /// Discord's documented bot API over HTTPS, authenticated as KliveBot. Read-only: it fetches history, single
    /// messages and channel lists, nothing else. It keeps to Discord's rate limits as Discord states them — the
    /// per-route allowance and reset in each response's headers, and the wait a 429 asks for — rather than a fixed
    /// pause (F416, F430), retries Discord's own errors and network faults a few times with jittered backoff, and
    /// tells the caller exactly what kind of refusal it got instead of treating every failure alike (F425, F434).
    /// </summary>
    public sealed class DiscordBotApi : IDiscordBotApi, IDisposable
    {
        public const string BaseUrl = "https://discord.com/api/v10/";
        private const int MaxAttempts = 4;
        /// <summary>A wait longer than this is handed back to the caller (a job retried later) instead of slept through.</summary>
        private static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(45);

        private readonly HttpClient http;
        private readonly Func<Task<string?>> token;
        private readonly IClock clock;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;
        private readonly ConcurrentDictionary<string, Bucket> buckets = new(StringComparer.Ordinal);
        private long globalUntilMs;

        private sealed class Bucket
        {
            public readonly SemaphoreSlim Gate = new(1, 1);
            public int Remaining = 1;
            public long ResetAtMs;
        }

        /// <param name="token">Reads KliveBot's token when a request is made. It is sent to Discord only and never logged.</param>
        /// <param name="handler">Replaces the network (tests).</param>
        /// <param name="delay">Replaces waiting (tests).</param>
        public DiscordBotApi(Func<Task<string?>> token, IClock clock, HttpMessageHandler? handler = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            this.token = token;
            this.clock = clock;
            this.delay = delay ?? ((span, ct) => Task.Delay(span, ct));
            http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
            {
                BaseAddress = new Uri(BaseUrl),
                Timeout = TimeSpan.FromSeconds(30),
            };
            // The form Discord asks bots to identify themselves with.
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "DiscordBot (omnipotent-klivebot, 3.0)");
        }

        public long Requests => Interlocked.Read(ref requests);
        private long requests;

        public async Task<IReadOnlyList<JsonElement>> MessagesAsync(string channelId, string? before, string? after, int limit, CancellationToken ct)
        {
            Snowflake(channelId, nameof(channelId));
            string query = "limit=" + Math.Clamp(limit, 1, 100).ToString(CultureInfo.InvariantCulture);
            if (before != null) query += "&before=" + Snowflake(before, nameof(before));
            if (after != null) query += "&after=" + Snowflake(after, nameof(after));
            var root = await GetAsync($"channels/{channelId}/messages?{query}", $"channels/{channelId}/messages", ct).ConfigureAwait(false);
            return Items(root);
        }

        public async Task<JsonElement?> MessageAsync(string channelId, string messageId, CancellationToken ct)
        {
            Snowflake(channelId, nameof(channelId));
            Snowflake(messageId, nameof(messageId));
            try
            {
                return await GetAsync($"channels/{channelId}/messages/{messageId}", $"channels/{channelId}/messages/:id", ct).ConfigureAwait(false);
            }
            // 10008 is "Unknown Message": deleted since. An unknown channel is a different answer and still raised.
            catch (DiscordApiException ex) when (ex.Failure == DiscordApiFailure.NotFound && ex.DiscordCode is 10008 or null)
            {
                return null;
            }
        }

        public Task<JsonElement> ChannelAsync(string channelId, CancellationToken ct) =>
            GetAsync($"channels/{Snowflake(channelId, nameof(channelId))}", $"channels/{channelId}", ct);

        public async Task<IReadOnlyList<JsonElement>> ActiveThreadsAsync(string guildId, CancellationToken ct)
        {
            var root = await GetAsync($"guilds/{Snowflake(guildId, nameof(guildId))}/threads/active", $"guilds/{guildId}/threads", ct).ConfigureAwait(false);
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("threads", out var threads) ? Items(threads) : Array.Empty<JsonElement>();
        }

        public async Task<(IReadOnlyList<JsonElement> Threads, bool HasMore)> ArchivedThreadsAsync(string channelId, string? beforeIso, CancellationToken ct)
        {
            Snowflake(channelId, nameof(channelId));
            string query = "limit=100" + (beforeIso == null ? "" : "&before=" + Uri.EscapeDataString(beforeIso));
            var root = await GetAsync($"channels/{channelId}/threads/archived/public?{query}", $"channels/{channelId}/threads/archived", ct).ConfigureAwait(false);
            if (root.ValueKind != JsonValueKind.Object) return (Array.Empty<JsonElement>(), false);
            var threads = root.TryGetProperty("threads", out var t) ? Items(t) : Array.Empty<JsonElement>();
            bool more = root.TryGetProperty("has_more", out var h) && h.ValueKind == JsonValueKind.True;
            return (threads, more);
        }

        private static IReadOnlyList<JsonElement> Items(JsonElement array) =>
            array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().ToList() : Array.Empty<JsonElement>();

        /// <summary>Ids go into the URL, so they must be what Discord ids are: digits.</summary>
        private static string Snowflake(string id, string what) =>
            DiscordPayload.IsSnowflake(id) ? id : throw new ArgumentException($"'{id}' is not a Discord id.", what);

        private async Task<JsonElement> GetAsync(string route, string bucketKey, CancellationToken ct)
        {
            var bucket = buckets.GetOrAdd(bucketKey, _ => new Bucket());
            // One request at a time per route and channel: Discord counts them per bucket, and so do we.
            await bucket.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                for (int attempt = 1; ; attempt++)
                {
                    long now = clock.NowMs;
                    long wait = Math.Max(Interlocked.Read(ref globalUntilMs) - now, bucket.Remaining <= 0 ? bucket.ResetAtMs - now : 0);
                    if (wait > LongestWait.TotalMilliseconds)
                        throw new DiscordApiException(DiscordApiFailure.RateLimited, $"Discord asks for a {wait / 1000}s pause on this route.", retryAfter: TimeSpan.FromMilliseconds(wait));
                    if (wait > 0) await delay(TimeSpan.FromMilliseconds(wait), ct).ConfigureAwait(false);

                    string? secret = await token().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(secret)) throw new DiscordApiException(DiscordApiFailure.Unauthorised, "KliveBot's token is not available.");

                    using var request = new HttpRequestMessage(HttpMethod.Get, route);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bot", secret.Trim());
                    HttpResponseMessage response;
                    try
                    {
                        Interlocked.Increment(ref requests);
                        response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
                    {
                        if (attempt >= MaxAttempts) throw new DiscordApiException(DiscordApiFailure.Network, "Discord could not be reached: " + ex.Message, inner: ex);
                        await delay(Backoff(attempt), ct).ConfigureAwait(false);
                        continue;
                    }

                    using (response)
                    {
                        ReadAllowance(response, bucket);
                        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        int status = (int)response.StatusCode;
                        if (status is >= 200 and < 300)
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(body);
                                return doc.RootElement.Clone();
                            }
                            catch (JsonException ex)
                            {
                                throw new DiscordApiException(DiscordApiFailure.BadResponse, "Discord answered with something that is not JSON.", inner: ex);
                            }
                        }

                        var (code, message, bodyRetry, bodyGlobal) = ReadError(body);
                        if (status == 429)
                        {
                            double seconds = HeaderSeconds(response, "Retry-After") ?? bodyRetry ?? 1;
                            long until = clock.NowMs + (long)Math.Ceiling(Math.Clamp(seconds, 0, 3600) * 1000) + 50;
                            if (bodyGlobal || Header(response, "X-RateLimit-Global") == "true") Interlocked.Exchange(ref globalUntilMs, until);
                            else { bucket.Remaining = 0; bucket.ResetAtMs = until; }
                            if (attempt >= MaxAttempts)
                                throw new DiscordApiException(DiscordApiFailure.RateLimited, "Discord is rate limiting this route.", code, TimeSpan.FromSeconds(seconds));
                            continue;
                        }
                        if (status >= 500)
                        {
                            if (attempt >= MaxAttempts) throw new DiscordApiException(DiscordApiFailure.Server, $"Discord answered {status}.", code);
                            await delay(Backoff(attempt), ct).ConfigureAwait(false);
                            continue;
                        }
                        string detail = message == null ? $"Discord answered {status}." : $"Discord answered {status}: {message}.";
                        throw new DiscordApiException(status switch
                        {
                            401 => DiscordApiFailure.Unauthorised,
                            403 => DiscordApiFailure.Forbidden,
                            404 => DiscordApiFailure.NotFound,
                            _ => DiscordApiFailure.BadResponse,
                        }, detail, code);
                    }
                }
            }
            finally
            {
                bucket.Gate.Release();
            }
        }

        private void ReadAllowance(HttpResponseMessage response, Bucket bucket)
        {
            if (Header(response, "X-RateLimit-Remaining") is { } remaining && int.TryParse(remaining, NumberStyles.Integer, CultureInfo.InvariantCulture, out int left)
                && HeaderSeconds(response, "X-RateLimit-Reset-After") is { } resetAfter)
            {
                bucket.Remaining = left;
                bucket.ResetAtMs = clock.NowMs + (long)Math.Ceiling(Math.Clamp(resetAfter, 0, 3600) * 1000) + 25;
            }
        }

        private static string? Header(HttpResponseMessage response, string name) =>
            response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

        private static double? HeaderSeconds(HttpResponseMessage response, string name) =>
            Header(response, name) is { } text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double s) && double.IsFinite(s) ? s : null;

        private static (int? Code, string? Message, double? RetryAfter, bool Global) ReadError(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return (null, null, null, false);
                int? code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out int ci) ? ci : null;
                string? message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                double? retry = root.TryGetProperty("retry_after", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetDouble(out double rd) ? rd : null;
                bool global = root.TryGetProperty("global", out var g) && g.ValueKind == JsonValueKind.True;
                return (code, message is { Length: > 200 } ? message[..200] : message, retry, global);
            }
            catch (JsonException)
            {
                // An error page that is not JSON (a proxy's HTML): the status alone classifies it.
                return (null, null, null, false);
            }
        }

        private static TimeSpan Backoff(int attempt) =>
            TimeSpan.FromMilliseconds(Math.Min(20_000, 400 * Math.Pow(2, attempt)) * (0.6 + Random.Shared.NextDouble() * 0.4));

        public void Dispose() => http.Dispose();
    }
}
