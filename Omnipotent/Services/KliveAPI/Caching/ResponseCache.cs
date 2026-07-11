using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;

namespace Omnipotent.Services.KliveAPI.Caching
{
    /// <summary>
    /// In-memory, dependency-versioned response cache for KliveAPI GET routes.
    /// Entries are keyed by (route, canonical query, user) and stay valid only while
    /// every dataset they read is still at the version they read (see
    /// <see cref="CacheDeps"/>) — so a served hit is always identical to what the
    /// handler would produce right now. Memory-only: on restart the cache and the
    /// version registry reset together, which is coherent by construction.
    /// </summary>
    internal sealed class ResponseCache
    {
        private readonly ConcurrentDictionary<string, CacheEntry> _store = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, RouteStat> _stats = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _evictLock = new();

        private long _totalBytes;

        // Live-tunable bounds (set from OmniSettings; volatile for lock-free reads).
        private volatile int _maxEntries = 10_000;
        private long _maxBytes = 256L * 1024 * 1024;

        /// <summary>Per-entry body cap — matches the pipeline's 8 MB ETag ceiling.</summary>
        public const long PerEntryCapBytes = 8L * 1024 * 1024;

        public long MaxBytes => Interlocked.Read(ref _maxBytes);
        public int MaxEntries => _maxEntries;
        public long TotalBytes => Interlocked.Read(ref _totalBytes);
        public int EntryCount => _store.Count;

        public void Configure(long maxBytes, int maxEntries)
        {
            if (maxBytes > 0) Interlocked.Exchange(ref _maxBytes, maxBytes);
            if (maxEntries > 0) _maxEntries = maxEntries;
            EvictIfNeeded();
        }

        // ── key construction ──

        /// <summary>
        /// Canonical cache key: <c>GET|{route}|{sorted-query}|{user}</c>. Route is
        /// lowercased (the route table is case-insensitive); query params are ordinal
        /// sorted by name with values preserved verbatim; entries vary by user because
        /// handlers routinely read <c>req.user</c>.
        /// </summary>
        public static string BuildKey(string route, NameValueCollection? query, string? userId)
        {
            var sb = new System.Text.StringBuilder(64);
            sb.Append("GET|").Append((route ?? "/").ToLowerInvariant()).Append('|');
            if (query != null && query.Count > 0)
            {
                var ordered = query.AllKeys
                    .Select(k => k ?? string.Empty)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(k => k, StringComparer.Ordinal);
                bool first = true;
                foreach (string k in ordered)
                {
                    if (!first) sb.Append('&');
                    first = false;
                    sb.Append(k).Append('=');
                    string[]? vals = query.GetValues(k.Length == 0 ? null : k);
                    if (vals != null) sb.Append(string.Join(",", vals));
                }
            }
            sb.Append('|').Append(string.IsNullOrEmpty(userId) ? "anon" : userId);
            return sb.ToString();
        }

        // ── read path ──

        /// <summary>Returns a still-valid entry or null. Purges an invalidated entry.</summary>
        public CacheEntry? TryGetValid(string key)
        {
            if (!_store.TryGetValue(key, out CacheEntry? entry)) return null;
            if (IsValid(entry))
            {
                entry.LastAccessTicks = DateTime.UtcNow.Ticks;
                return entry;
            }
            Remove(key, entry);
            return null;
        }

        private static bool IsValid(CacheEntry entry)
        {
            foreach (KeyValuePair<string, long> dep in entry.Deps)
            {
                if (CacheDeps.CurrentVersion(dep.Key) != dep.Value) return false;
            }
            return true;
        }

        // ── write path ──

        /// <summary>
        /// Stores the completed fill if it passes every cacheability rule. Revalidates
        /// all noted dependency versions at store time so a write racing the fill can
        /// never leave a stale entry behind.
        /// </summary>
        public bool TryStoreFromRecording(string key, ResponseRecording rec, DependencyScope scope)
        {
            if (rec == null || scope == null) return false;
            if (!rec.Completed || rec.IsStreaming) return false;
            if (rec.StatusCode != 200) return false;
            if (scope.WroteDuringFill) return false;
            if (scope.UncacheableReason != null) return false;
            if (scope.ReadCount == 0) return false;                 // touched nothing tracked
            if (rec.Body.LongLength > PerEntryCapBytes) return false;

            IReadOnlyDictionary<string, long> deps = scope.SnapshotReads();
            foreach (KeyValuePair<string, long> dep in deps)
            {
                if (CacheDeps.CurrentVersion(dep.Key) != dep.Value) return false; // raced — discard
            }

            var entry = new CacheEntry(
                rec.Body, rec.StatusCode, rec.ContentType, rec.ExtraHeaders, rec.IsBinary,
                deps, rec.SeededEncoding, rec.SeededVariant);
            Insert(key, entry);
            return true;
        }

        /// <summary>
        /// Builds a cache entry from raw response parts (used by the /batch path,
        /// which captures into a buffer rather than teeing the socket).
        /// </summary>
        public bool TryStoreFromParts(string key, int statusCode, string contentType, NameValueCollection? headers,
            byte[] body, bool isBinary, DependencyScope scope)
        {
            if (scope == null || body == null) return false;
            if (statusCode != 200) return false;
            if (scope.WroteDuringFill || scope.UncacheableReason != null || scope.ReadCount == 0) return false;
            if (body.LongLength > PerEntryCapBytes) return false;

            IReadOnlyDictionary<string, long> deps = scope.SnapshotReads();
            foreach (KeyValuePair<string, long> dep in deps)
            {
                if (CacheDeps.CurrentVersion(dep.Key) != dep.Value) return false;
            }

            var entry = new CacheEntry(body, statusCode, contentType, headers, isBinary,
                deps, HttpResponseHelpers.ContentEncoding.None, null);
            Insert(key, entry);
            return true;
        }

        private void Insert(string key, CacheEntry entry)
        {
            long oldSize = _store.TryGetValue(key, out CacheEntry? existing) ? existing.SizeBytes : 0;
            _store[key] = entry;
            Interlocked.Add(ref _totalBytes, entry.SizeBytes - oldSize);
            if (Interlocked.Read(ref _totalBytes) > MaxBytes || _store.Count > _maxEntries)
            {
                EvictIfNeeded();
            }
        }

        private void Remove(string key, CacheEntry entry)
        {
            if (_store.TryRemove(new KeyValuePair<string, CacheEntry>(key, entry)))
            {
                Interlocked.Add(ref _totalBytes, -entry.SizeBytes);
            }
        }

        private void EvictIfNeeded()
        {
            lock (_evictLock)
            {
                if (Interlocked.Read(ref _totalBytes) <= MaxBytes && _store.Count <= _maxEntries) return;

                // 1) Drop version-invalidated entries first — they are dead weight.
                foreach (KeyValuePair<string, CacheEntry> kvp in _store.ToArray())
                {
                    if (!IsValid(kvp.Value)) Remove(kvp.Key, kvp.Value);
                }

                // 2) Then evict least-recently-used until back under budget.
                if (Interlocked.Read(ref _totalBytes) <= MaxBytes && _store.Count <= _maxEntries) return;
                foreach (KeyValuePair<string, CacheEntry> kvp in _store.ToArray()
                             .OrderBy(k => k.Value.LastAccessTicks))
                {
                    if (Interlocked.Read(ref _totalBytes) <= MaxBytes && _store.Count <= _maxEntries) return;
                    Remove(kvp.Key, kvp.Value);
                }
            }
        }

        public void Clear()
        {
            lock (_evictLock)
            {
                _store.Clear();
                Interlocked.Exchange(ref _totalBytes, 0);
            }
        }

        // ── stats ──

        public void RecordHit(string route) => StatFor(route).Hits++;
        public void RecordMiss(string route) => StatFor(route).Misses++;
        public void RecordBypass(string route) => StatFor(route).Bypasses++;

        private RouteStat StatFor(string route) => _stats.GetOrAdd(route ?? "/", static _ => new RouteStat());

        public object GetStatsSnapshot()
        {
            var routes = _stats
                .Select(kvp => new
                {
                    route = kvp.Key,
                    hits = Interlocked.Read(ref kvp.Value.Hits),
                    misses = Interlocked.Read(ref kvp.Value.Misses),
                    bypasses = Interlocked.Read(ref kvp.Value.Bypasses),
                    hitRatio = Ratio(kvp.Value.Hits, kvp.Value.Misses)
                })
                .OrderByDescending(r => r.hits + r.misses)
                .Take(100)
                .ToList();

            long totalHits = _stats.Values.Sum(s => Interlocked.Read(ref s.Hits));
            long totalMisses = _stats.Values.Sum(s => Interlocked.Read(ref s.Misses));
            long totalBypasses = _stats.Values.Sum(s => Interlocked.Read(ref s.Bypasses));

            return new
            {
                entryCount = EntryCount,
                totalBytes = TotalBytes,
                maxBytes = MaxBytes,
                maxEntries = MaxEntries,
                lifetime = new
                {
                    hits = totalHits,
                    misses = totalMisses,
                    bypasses = totalBypasses,
                    hitRatio = Ratio(totalHits, totalMisses)
                },
                routes
            };
        }

        private static double Ratio(long hits, long misses)
        {
            long total = hits + misses;
            return total == 0 ? 0 : Math.Round((double)hits / total, 4);
        }

        private sealed class RouteStat
        {
            public long Hits;
            public long Misses;
            public long Bypasses;
        }
    }

    /// <summary>
    /// A cached response: uncompressed body plus lazily materialized compressed
    /// variants, the precomputed weak ETag, and the dependency versions that keep it
    /// honest. Never mutated after construction except <see cref="LastAccessTicks"/>.
    /// </summary>
    internal sealed class CacheEntry
    {
        public byte[] RawBody { get; }
        public int StatusCode { get; }
        public string ContentType { get; }
        public NameValueCollection? ExtraHeaders { get; }
        public bool IsBinary { get; }
        public string? ETag { get; }
        public IReadOnlyDictionary<string, long> Deps { get; }
        public long SizeBytes { get; }
        public long LastAccessTicks;

        private const long MaxETagPayloadBytes = 8L * 1024 * 1024;

        private readonly object _variantLock = new();
        private byte[]? _brotli;
        private byte[]? _gzip;
        private bool _brotliComputed;
        private bool _gzipComputed;

        public CacheEntry(byte[] rawBody, int statusCode, string contentType, NameValueCollection? extraHeaders,
            bool isBinary, IReadOnlyDictionary<string, long> deps,
            HttpResponseHelpers.ContentEncoding seededEncoding, byte[]? seededVariant)
        {
            RawBody = rawBody ?? Array.Empty<byte>();
            StatusCode = statusCode;
            ContentType = string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType;
            ExtraHeaders = extraHeaders;
            IsBinary = isBinary;
            Deps = deps;
            LastAccessTicks = DateTime.UtcNow.Ticks;

            // Text responses carry a weak ETag identical to the live path's, so hit
            // and miss both honor If-None-Match with the same tag. Binary responses
            // don't (ReturnBinaryResponse doesn't set one).
            if (!isBinary && RawBody.Length > 0 && RawBody.Length <= MaxETagPayloadBytes)
            {
                ETag = HttpResponseHelpers.ComputeWeakETag(RawBody);
            }

            // Seed the negotiated variant so the first matching hit needn't compress.
            if (seededVariant != null && seededVariant.Length < RawBody.Length)
            {
                if (seededEncoding == HttpResponseHelpers.ContentEncoding.Brotli)
                {
                    _brotli = seededVariant;
                    _brotliComputed = true;
                }
                else if (seededEncoding == HttpResponseHelpers.ContentEncoding.Gzip)
                {
                    _gzip = seededVariant;
                    _gzipComputed = true;
                }
            }

            SizeBytes = RawBody.LongLength + (seededVariant?.LongLength ?? 0) + 256; // + rough overhead
        }

        /// <summary>
        /// Returns the compressed bytes for <paramref name="encoding"/>, or null when
        /// compression doesn't shrink the payload (caller then serves raw, matching
        /// the pipeline's <c>compressed.Length &lt; buffer.Length</c> guard).
        /// </summary>
        public byte[]? GetVariant(HttpResponseHelpers.ContentEncoding encoding)
        {
            if (encoding == HttpResponseHelpers.ContentEncoding.Brotli)
            {
                lock (_variantLock)
                {
                    if (!_brotliComputed)
                    {
                        byte[] c = HttpResponseHelpers.Compress(RawBody, encoding);
                        _brotli = c.Length < RawBody.Length ? c : null;
                        _brotliComputed = true;
                    }
                    return _brotli;
                }
            }
            if (encoding == HttpResponseHelpers.ContentEncoding.Gzip)
            {
                lock (_variantLock)
                {
                    if (!_gzipComputed)
                    {
                        byte[] c = HttpResponseHelpers.Compress(RawBody, encoding);
                        _gzip = c.Length < RawBody.Length ? c : null;
                        _gzipComputed = true;
                    }
                    return _gzip;
                }
            }
            return null;
        }
    }
}
