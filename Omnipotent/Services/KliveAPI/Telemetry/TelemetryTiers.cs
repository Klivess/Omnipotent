using System;
using System.Collections.Generic;
using System.Linq;

namespace Omnipotent.Services.KliveAPI.Telemetry
{
    /// <summary>Aggregation resolutions. Buckets are UTC-aligned to their width.</summary>
    public enum TelemetryTier
    {
        S10 = 0,
        M1 = 1,
        H1 = 2,
        D1 = 3,
    }

    public static class TelemetryTiers
    {
        public static readonly TelemetryTier[] All = { TelemetryTier.S10, TelemetryTier.M1, TelemetryTier.H1, TelemetryTier.D1 };

        public static long WidthMs(TelemetryTier tier) => tier switch
        {
            TelemetryTier.S10 => 10_000,
            TelemetryTier.M1 => 60_000,
            TelemetryTier.H1 => 3_600_000,
            _ => 86_400_000,
        };

        public static string Name(TelemetryTier tier) => tier switch
        {
            TelemetryTier.S10 => "10s",
            TelemetryTier.M1 => "1m",
            TelemetryTier.H1 => "1h",
            _ => "1d",
        };

        public static long Align(long utcMs, TelemetryTier tier)
        {
            long w = WidthMs(tier);
            return utcMs - Mod(utcMs, w);
        }

        public static long AlignTo(long utcMs, long widthMs) => utcMs - Mod(utcMs, widthMs);

        private static long Mod(long a, long m) => ((a % m) + m) % m;

        /// <summary>Bucket widths the planner may choose (ms), ascending.</summary>
        public static readonly long[] BucketWidthsMs =
        {
            10_000, 30_000, 60_000, 120_000, 300_000, 600_000, 900_000, 1_800_000,
            3_600_000, 7_200_000, 10_800_000, 21_600_000, 43_200_000,
            86_400_000, 172_800_000, 604_800_000, 2_592_000_000,
        };

        public static string FormatWidth(long ms)
        {
            if (ms % 86_400_000 == 0) return (ms / 86_400_000) + "d";
            if (ms % 3_600_000 == 0) return (ms / 3_600_000) + "h";
            if (ms % 60_000 == 0) return (ms / 60_000) + "m";
            return (ms / 1000) + "s";
        }

        public static bool TryParseWidth(string? s, out long ms)
        {
            ms = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim().ToLowerInvariant();
            char unit = s[^1];
            if (!long.TryParse(s[..^1], out long n) || n <= 0) return false;
            ms = unit switch
            {
                's' => n * 1000,
                'm' => n * 60_000,
                'h' => n * 3_600_000,
                'd' => n * 86_400_000,
                'w' => n * 604_800_000,
                _ => 0,
            };
            return ms > 0;
        }

        /// <summary>The coarsest tier whose width divides <paramref name="bucketMs"/>.</summary>
        public static TelemetryTier TierFor(long bucketMs)
        {
            for (int i = All.Length - 1; i >= 0; i--)
            {
                if (bucketMs % WidthMs(All[i]) == 0) return All[i];
            }
            return TelemetryTier.S10;
        }
    }

    /// <summary>
    /// One resolution's buckets for one kind of aggregate, keyed by aligned bucket start
    /// (UTC ms) then series. Owned exclusively by the telemetry engine thread — no locks.
    /// </summary>
    public sealed class TierStore<T> where T : class
    {
        private readonly Func<T> _factory;
        private readonly Action<T, T> _mergeInto;
        private readonly Func<T, T> _freeze;
        private readonly SortedDictionary<long, Dictionary<string, T>> _closed = new();

        public TelemetryTier Tier { get; }
        public long WidthMs { get; }
        public long OpenStart { get; private set; } = long.MinValue;
        public Dictionary<string, T> Open { get; private set; } = new(StringComparer.Ordinal);

        public TierStore(TelemetryTier tier, Func<T> factory, Action<T, T> mergeInto, Func<T, T> freeze)
        {
            Tier = tier;
            WidthMs = TelemetryTiers.WidthMs(tier);
            _factory = factory;
            _mergeInto = mergeInto;
            _freeze = freeze;
        }

        public IEnumerable<long> ClosedStarts => _closed.Keys;
        public int ClosedCount => _closed.Count;
        public long OldestClosedStart => _closed.Count == 0 ? long.MaxValue : _closed.Keys.First();

        /// <summary>
        /// Rolls the open bucket forward to the bucket containing <paramref name="nowMs"/>.
        /// Returns the bucket that was closed (start, contents) or null when still inside it.
        /// </summary>
        public (long Start, Dictionary<string, T> Series)? Advance(long nowMs)
        {
            long aligned = TelemetryTiers.Align(nowMs, Tier);
            if (OpenStart == long.MinValue)
            {
                OpenStart = aligned;
                return null;
            }
            if (aligned <= OpenStart) return null;

            long closedStart = OpenStart;
            Dictionary<string, T> closed = Open;
            foreach (T v in closed.Values) _freeze(v);
            if (closed.Count > 0) _closed[closedStart] = closed;
            Open = new Dictionary<string, T>(StringComparer.Ordinal);
            OpenStart = aligned;
            return (closedStart, closed);
        }

        public T GetOrCreateOpen(string series)
        {
            if (!Open.TryGetValue(series, out T? b))
            {
                b = _factory();
                Open[series] = b;
            }
            return b;
        }

        /// <summary>Seeds the open bucket (after restart) from a persisted partial.</summary>
        public void SeedOpen(long start, string series, T partial)
        {
            if (OpenStart == long.MinValue) OpenStart = start;
            if (start != OpenStart) return;
            _mergeInto(GetOrCreateOpen(series), partial);
        }

        /// <summary>Inserts a historic closed bucket (loaded from disk).</summary>
        public void LoadClosed(long start, string series, T bucket)
        {
            if (start >= OpenStart && OpenStart != long.MinValue) return;
            if (!_closed.TryGetValue(start, out var set))
            {
                set = new Dictionary<string, T>(StringComparer.Ordinal);
                _closed[start] = set;
            }
            set[series] = _freeze(bucket);
        }

        /// <summary>Bucket for (start, series): closed, or the live open one.</summary>
        public T? Get(long start, string series)
        {
            if (start == OpenStart) return Open.TryGetValue(series, out T? o) ? o : null;
            return _closed.TryGetValue(start, out var set) && set.TryGetValue(series, out T? b) ? b : null;
        }

        public IReadOnlyDictionary<string, T>? GetSet(long start)
        {
            if (start == OpenStart) return Open;
            return _closed.TryGetValue(start, out var set) ? set : null;
        }

        /// <summary>Earliest start this store can answer for <paramref name="series"/> from memory.</summary>
        public bool Covers(long startMs) => _closed.Count > 0 ? startMs >= _closed.Keys.First() : startMs >= OpenStart;

        /// <summary>
        /// Drops series older than their retention. <paramref name="isLongLived"/> series
        /// (global pseudo-series) use <paramref name="longRetentionMs"/>.
        /// </summary>
        public void Prune(long nowMs, long routeRetentionMs, long longRetentionMs, Func<string, bool> isLongLived)
        {
            long routeCutoff = nowMs - routeRetentionMs;
            long longCutoff = nowMs - longRetentionMs;
            var emptied = new List<long>();
            foreach (var (start, set) in _closed)
            {
                if (start >= routeCutoff) break;
                if (start < longCutoff)
                {
                    emptied.Add(start);
                    continue;
                }
                foreach (string key in set.Keys.Where(k => !isLongLived(k)).ToList()) set.Remove(key);
                if (set.Count == 0) emptied.Add(start);
            }
            foreach (long s in emptied) _closed.Remove(s);
        }

        public long EstimateBytes(Func<T, int> sizeOf)
        {
            long n = 0;
            foreach (var set in _closed.Values) foreach (T v in set.Values) n += sizeOf(v) + 48;
            foreach (T v in Open.Values) n += sizeOf(v) + 48;
            return n;
        }

        /// <summary>
        /// Merges every stored bucket of <paramref name="series"/> in [fromMs, toMs) into a
        /// fresh aggregate. Includes the open bucket when it falls inside the window.
        /// </summary>
        public T MergeRange(string series, long fromMs, long toMs)
        {
            T acc = _factory();
            foreach (var (start, set) in _closed)
            {
                if (start < fromMs) continue;
                if (start >= toMs) break;
                if (set.TryGetValue(series, out T? b)) _mergeInto(acc, b);
            }
            if (OpenStart >= fromMs && OpenStart < toMs && Open.TryGetValue(series, out T? o)) _mergeInto(acc, o);
            return acc;
        }

        /// <summary>Every series present in [fromMs, toMs).</summary>
        public HashSet<string> SeriesIn(long fromMs, long toMs)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (start, set) in _closed)
            {
                if (start < fromMs) continue;
                if (start >= toMs) break;
                result.UnionWith(set.Keys);
            }
            if (OpenStart >= fromMs && OpenStart < toMs) result.UnionWith(Open.Keys);
            return result;
        }

        /// <summary>
        /// Merges every series in [fromMs, toMs) at once: one pass, one aggregate per series.
        /// </summary>
        public Dictionary<string, T> MergeAllSeries(long fromMs, long toMs, Func<string, bool>? include = null)
        {
            var result = new Dictionary<string, T>(StringComparer.Ordinal);
            void Add(Dictionary<string, T> set)
            {
                foreach (var (series, b) in set)
                {
                    if (include != null && !include(series)) continue;
                    if (!result.TryGetValue(series, out T? acc))
                    {
                        acc = _factory();
                        result[series] = acc;
                    }
                    _mergeInto(acc, b);
                }
            }
            foreach (var (start, set) in _closed)
            {
                if (start < fromMs) continue;
                if (start >= toMs) break;
                Add(set);
            }
            if (OpenStart >= fromMs && OpenStart < toMs) Add(Open);
            return result;
        }
    }
}
