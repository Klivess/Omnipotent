using Newtonsoft.Json;
using Omnipotent.Services.KliveAPI.Caching;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Omnipotent.Services.KliveCloud
{
    /// <summary>
    /// How much each share link is used: opens, downloads, bytes, and when. Without it nobody could
    /// tell a link people rely on from one nobody has touched since it was made (9 of 33 live links
    /// could not even open, unnoticed, in Nov 2026).
    ///
    /// Counted in memory and written to its own file on a timer, never per request and never into
    /// share_links.json: a write there bumps the share-links cache key, which would invalidate every
    /// cached share response on each visit. Opens are counted once per visitor per
    /// <see cref="OpenWindow"/> (a share page re-reads its folder when the tab regains focus); the
    /// visitor key is a salted hash that lives only in memory, so no address is stored.
    /// </summary>
    public sealed class KliveCloudShareStats
    {
        public sealed class LinkUsage
        {
            public long Opens;
            public long Downloads;
            public long BytesServed;
            public DateTime? FirstOpenedUtc;
            public DateTime? LastOpenedUtc;
            public DateTime? LastDownloadUtc;
        }

        /// <summary>Response-cache key: the link list reads these counts, and every count bumps it.</summary>
        internal const string CacheKey = "klivecloud:sharestats";

        internal static readonly TimeSpan OpenWindow = TimeSpan.FromMinutes(30);
        internal static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(60);

        private readonly ConcurrentDictionary<string, LinkUsage> usage = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, DateTime> recentVisitors = new(StringComparer.Ordinal);
        private readonly object gate = new();
        private readonly byte[] salt = RandomNumberGenerator.GetBytes(16);
        private readonly string? filePath;
        private int dirty;
        private DateTime lastPruneUtc = DateTime.MinValue;

        public KliveCloudShareStats(string? filePath) => this.filePath = filePath;

        /// <summary>Total counted since the stats began (for the page's summary).</summary>
        public DateTime? CountingSinceUtc { get; private set; }

        public LinkUsage? Get(string shareCode)
        {
            CacheDeps.NoteRead(CacheKey);
            if (string.IsNullOrEmpty(shareCode) || !usage.TryGetValue(shareCode, out var found)) return null;
            lock (gate) return Copy(found);
        }

        public void NoteOpen(string shareCode, string? visitor, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(shareCode)) return;
            if (!string.IsNullOrEmpty(visitor))
            {
                string key = shareCode + "|" + Hash(visitor);
                if (recentVisitors.TryGetValue(key, out var seen) && nowUtc - seen < OpenWindow) return;
                recentVisitors[key] = nowUtc;
                PruneVisitors(nowUtc);
            }
            var entry = usage.GetOrAdd(shareCode, _ => new LinkUsage());
            lock (gate)
            {
                entry.Opens++;
                entry.FirstOpenedUtc ??= nowUtc;
                entry.LastOpenedUtc = nowUtc;
                CountingSinceUtc ??= nowUtc;
            }
            Changed();
        }

        /// <summary>A download starting from its first byte. A resumed or seeking range request isn't another download.</summary>
        public void NoteDownload(string shareCode, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(shareCode)) return;
            var entry = usage.GetOrAdd(shareCode, _ => new LinkUsage());
            lock (gate)
            {
                entry.Downloads++;
                entry.LastDownloadUtc = nowUtc;
                CountingSinceUtc ??= nowUtc;
            }
            Changed();
        }

        public void NoteBytes(string shareCode, long bytes)
        {
            if (string.IsNullOrEmpty(shareCode) || bytes <= 0) return;
            var entry = usage.GetOrAdd(shareCode, _ => new LinkUsage());
            lock (gate) entry.BytesServed += bytes;
            Changed();
        }

        /// <summary>A deleted link's history goes with it.</summary>
        public void Forget(string shareCode)
        {
            if (usage.TryRemove(shareCode, out _)) Changed();
        }

        private void Changed()
        {
            Interlocked.Exchange(ref dirty, 1);
            CacheDeps.Bump(CacheKey);
        }

        private void PruneVisitors(DateTime nowUtc)
        {
            if (nowUtc - lastPruneUtc < TimeSpan.FromMinutes(5) && recentVisitors.Count < 5_000) return;
            lastPruneUtc = nowUtc;
            foreach (var pair in recentVisitors)
            {
                if (nowUtc - pair.Value >= OpenWindow) recentVisitors.TryRemove(pair.Key, out _);
            }
        }

        private string Hash(string visitor)
        {
            byte[] digest = HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes(visitor));
            return Convert.ToHexString(digest, 0, 12);
        }

        private static LinkUsage Copy(LinkUsage u) => new()
        {
            Opens = u.Opens,
            Downloads = u.Downloads,
            BytesServed = u.BytesServed,
            FirstOpenedUtc = u.FirstOpenedUtc,
            LastOpenedUtc = u.LastOpenedUtc,
            LastDownloadUtc = u.LastDownloadUtc,
        };

        // ───────────────────────────── persistence ─────────────────────────────

        private sealed class Document
        {
            public DateTime? CountingSinceUtc;
            public Dictionary<string, LinkUsage> Links = new(StringComparer.Ordinal);
        }

        public void Load()
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
            var doc = JsonConvert.DeserializeObject<Document>(File.ReadAllText(filePath));
            if (doc == null) return;
            CountingSinceUtc = doc.CountingSinceUtc;
            foreach (var pair in doc.Links ?? new()) usage[pair.Key] = pair.Value ?? new LinkUsage();
        }

        /// <summary>Writes the counts if anything changed since the last write. Returns whether it wrote.</summary>
        public bool FlushIfDirty()
        {
            if (string.IsNullOrEmpty(filePath) || Interlocked.Exchange(ref dirty, 0) == 0) return false;
            Document doc;
            lock (gate)
            {
                doc = new Document { CountingSinceUtc = CountingSinceUtc };
                foreach (var pair in usage) doc.Links[pair.Key] = Copy(pair.Value);
            }
            try
            {
                string temp = filePath + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(doc, Formatting.Indented));
                File.Move(temp, filePath, overwrite: true);
                return true;
            }
            catch
            {
                Interlocked.Exchange(ref dirty, 1); // try again next time
                throw;
            }
        }
    }
}
