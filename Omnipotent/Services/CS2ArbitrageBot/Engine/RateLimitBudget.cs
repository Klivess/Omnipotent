using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

namespace Omnipotent.Services.CS2ArbitrageBot.Engine
{
    /// <summary>One server-side rate-limit window, as last reported by x-ratelimit-* headers.</summary>
    public sealed class RateLimitBucket
    {
        public string Name { get; init; } = "";
        public int Limit { get; set; }
        public int Remaining { get; set; }
        public DateTime ResetUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public int TooManyRequestsCount { get; set; }
        public DateTime? LastTooManyRequestsUtc { get; set; }
        public long RequestsSent { get; set; }
        /// <summary>Whether the headers were ever seen (an unseen bucket is not throttled).</summary>
        public bool Known => UpdatedUtc != default;
    }

    /// <summary>
    /// Tracks CSFloat's per-endpoint rate-limit windows from response headers. Measured live
    /// (Oct 2026): <c>/listings</c> search = 200 per hour per API key (fixed window), <c>/me/trades</c> = 100,
    /// <c>/history/*</c> = 500 per day, <c>/me</c> and the price list = 50k. The old bot paid no attention
    /// to these and stalled for up to 30 minutes on every 429.
    /// </summary>
    public sealed class RateLimitBudget
    {
        private readonly ConcurrentDictionary<string, RateLimitBucket> buckets = new(StringComparer.OrdinalIgnoreCase);
        private readonly Func<DateTime> utcNow;

        public RateLimitBudget(Func<DateTime>? utcNow = null)
        {
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public RateLimitBucket Get(string bucket) => buckets.GetOrAdd(bucket, name => new RateLimitBucket { Name = name });

        public IReadOnlyList<RateLimitBucket> Snapshot() => buckets.Values.OrderBy(b => b.Name).ToList();

        /// <summary>Records the window state carried by a response (any status code).</summary>
        public void Observe(string bucket, HttpResponseMessage response)
        {
            var state = Get(bucket);
            lock (state)
            {
                state.RequestsSent++;
                if (TryReadInt(response.Headers, "x-ratelimit-limit", out int limit)) state.Limit = limit;
                // A response without the headers (CDN-cached, or an endpoint that omits them) says nothing
                // about the window; it must not mark the bucket as known-and-empty.
                if (TryReadInt(response.Headers, "x-ratelimit-remaining", out int remaining))
                {
                    state.Remaining = remaining;
                    state.UpdatedUtc = utcNow();
                }
                if (TryReadLong(response.Headers, "x-ratelimit-reset", out long resetEpoch) && resetEpoch > 0)
                    state.ResetUtc = DateTimeOffset.FromUnixTimeSeconds(resetEpoch).UtcDateTime;

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    state.UpdatedUtc = utcNow();
                    state.TooManyRequestsCount++;
                    state.LastTooManyRequestsUtc = utcNow();
                    state.Remaining = 0;
                    TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
                    if (retryAfter == null && response.Headers.RetryAfter?.Date is DateTimeOffset at) retryAfter = at.UtcDateTime - utcNow();
                    DateTime candidate = utcNow() + (retryAfter ?? TimeSpan.FromMinutes(1));
                    // Trust the window reset when it is sane; otherwise fall back to Retry-After / 1 min.
                    if (state.ResetUtc <= utcNow() || state.ResetUtc > utcNow().AddDays(2)) state.ResetUtc = candidate;
                }
            }
        }

        /// <summary>True when a request can be sent while keeping <paramref name="reserve"/> requests back.</summary>
        public bool CanSpend(string bucket, int reserve = 0)
        {
            var state = Get(bucket);
            lock (state)
            {
                if (!state.Known) return true;
                if (state.ResetUtc != default && utcNow() >= state.ResetUtc) return true; // window rolled over
                return state.Remaining > reserve;
            }
        }

        /// <summary>When the bucket next has budget (now if it already does).</summary>
        public DateTime NextAvailableUtc(string bucket, int reserve = 0)
        {
            var state = Get(bucket);
            lock (state)
            {
                if (!state.Known || state.Remaining > reserve) return utcNow();
                return state.ResetUtc > utcNow() ? state.ResetUtc : utcNow();
            }
        }

        /// <summary>
        /// Spacing that spreads the remaining budget (minus <paramref name="reserve"/>) evenly over what is
        /// left of the window, clamped to [min, max]. This is what lets the listing feed run continuously
        /// at the fastest rate the 200/hour key allows instead of bursting and then going blind.
        /// </summary>
        public TimeSpan SuggestedInterval(string bucket, int reserve, TimeSpan min, TimeSpan max)
        {
            var state = Get(bucket);
            lock (state)
            {
                DateTime now = utcNow();
                if (!state.Known || state.ResetUtc <= now) return min;
                int spendable = state.Remaining - reserve;
                TimeSpan untilReset = state.ResetUtc - now;
                if (spendable <= 0) return untilReset < max ? untilReset : max;
                TimeSpan even = TimeSpan.FromTicks(untilReset.Ticks / spendable);
                if (even < min) return min;
                return even > max ? max : even;
            }
        }

        private static bool TryReadInt(HttpResponseHeaders headers, string name, out int value)
        {
            value = 0;
            return headers.TryGetValues(name, out var values) &&
                   int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryReadLong(HttpResponseHeaders headers, string name, out long value)
        {
            value = 0;
            return headers.TryGetValues(name, out var values) &&
                   long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
    }
}
