using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Omnipotent.Services.KliveCloud
{
    /// <summary>
    /// Short-lived, single-item keys that let a signed-in profile's browser fetch a file without
    /// an Authorization header: what an &lt;img&gt;, &lt;video&gt;, &lt;audio&gt; or a plain download link
    /// needs, since none of them can send one (the API reads ?authorization= on WebSockets only).
    /// Without them the page had to fetch a whole file into a Blob before showing or saving it,
    /// with no progress, and could not play a video at all.
    ///
    /// A ticket names one item and the profile it was issued to, and dies after
    /// <see cref="Lifetime"/> or a restart (they live only in memory). It grants nothing by itself:
    /// every use re-checks that the profile still exists, may still download, and can still see the
    /// item, so revoking access ends a ticket at once.
    /// </summary>
    public sealed class KliveCloudTickets
    {
        /// <summary>Long enough to watch a film with seeking (each seek is a new range request).</summary>
        public static readonly TimeSpan Lifetime = TimeSpan.FromHours(3);

        /// <summary>A bound on memory for a client that asks for tickets in a loop.</summary>
        internal const int MaxLive = 5_000;

        public sealed record Ticket(string Token, string ItemId, string ProfileId, DateTime IssuedUtc, DateTime ExpiresUtc);

        private readonly ConcurrentDictionary<string, Ticket> tickets = new(StringComparer.Ordinal);

        public int Count => tickets.Count;

        public Ticket Issue(string itemId, string profileId, DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(itemId)) throw new ArgumentException("An item is required.", nameof(itemId));
            if (string.IsNullOrWhiteSpace(profileId)) throw new ArgumentException("A profile is required.", nameof(profileId));
            if (tickets.Count >= MaxLive) Prune(nowUtc);

            // 32 random bytes: as unguessable as a session token.
            string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var ticket = new Ticket(token, itemId, profileId, nowUtc, nowUtc + Lifetime);
            tickets[token] = ticket;
            return ticket;
        }

        /// <summary>The live ticket for <paramref name="token"/>, or null (unknown, malformed or expired).</summary>
        public Ticket? Resolve(string? token, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(token) || token.Length > 64 || !tickets.TryGetValue(token, out var ticket)) return null;
            if (ticket.ExpiresUtc <= nowUtc)
            {
                tickets.TryRemove(token, out _);
                return null;
            }
            return ticket;
        }

        /// <summary>Drops expired tickets, then the oldest ones while there are still too many.</summary>
        internal void Prune(DateTime nowUtc)
        {
            foreach (var pair in tickets)
            {
                if (pair.Value.ExpiresUtc <= nowUtc) tickets.TryRemove(pair.Key, out _);
            }
            int excess = tickets.Count - MaxLive + MaxLive / 10;
            if (excess <= 0) return;
            foreach (var old in tickets.Values.OrderBy(t => t.IssuedUtc).Take(excess).ToList())
            {
                tickets.TryRemove(old.Token, out _);
            }
        }
    }
}
