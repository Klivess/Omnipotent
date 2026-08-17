using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Omnipotent.Services.KliveAPI.Caching
{
    /// <summary>
    /// Ambient, never-stale dependency tracker for the KliveAPI response cache.
    ///
    /// The model: every persisted dataset has a monotonic version counter. Stores
    /// call <see cref="NoteRead"/> at the top of their read methods (recording the
    /// version they are about to observe) and <see cref="Bump"/> after a write is
    /// durably visible. During a cache-miss fill the dispatcher opens a
    /// <see cref="DependencyScope"/> (flows through await and into Task.Run via
    /// ExecutionContext), so every store touched by the handler is captured
    /// automatically. A cached entry is valid only while every dataset it read is
    /// still at the version it read — a single dictionary lookup per dependency.
    ///
    /// Correctness rests on three rules that together guarantee a cached response
    /// can never be stale:
    ///   1. NoteRead records the version BEFORE reading the data.
    ///   2. Bump increments AFTER the write is visible.
    ///   3. The fill is only stored if every noted version is still current at
    ///      store time (see <see cref="ResponseCache.TryStoreFromRecording"/>).
    /// A write concurrent with a fill therefore either bumps before store-time
    /// revalidation (fill discarded) or after (entry invalidated on next hit).
    /// The worst case is a wasted fill, never a stale served response.
    ///
    /// Every public method is O(1) and swallows exceptions: instrumentation added
    /// to a store must never be able to break that store.
    /// </summary>
    public static class CacheDeps
    {
        private static readonly AsyncLocal<DependencyScope?> _current = new();
        private static readonly ConcurrentDictionary<string, long> _versions = new(StringComparer.Ordinal);

        /// <summary>
        /// Prefix marking a *virtual* dependency whose version comes from the clock
        /// rather than the registry. See <see cref="NoteTimeBucket"/>.
        /// </summary>
        private const string ClockKeyPrefix = "clock:";

        /// <summary>Clock source — overridable so tests can pin bucket boundaries.</summary>
        internal static Func<DateTime> UtcNowProvider = static () => DateTime.UtcNow;

        /// <summary>Current monotonic version of a dataset key (0 if never bumped).</summary>
        public static long CurrentVersion(string key)
        {
            if (key != null && key.StartsWith(ClockKeyPrefix, StringComparison.Ordinal))
            {
                return CurrentBucket(key);
            }
            return _versions.TryGetValue(key ?? string.Empty, out long v) ? v : 0;
        }

        /// <summary>
        /// Declares that the current fill's payload is anchored to wall-clock time,
        /// valid for <paramref name="bucket"/>.
        ///
        /// A sliding window ("last 30 days", "facts fresh as of now") cannot be
        /// expressed as a stored version: the data need not change for the answer to
        /// go wrong, because the *question* moves with the clock. Rather than give up
        /// and mark such a fill uncacheable, we quantize the clock and note the
        /// bucket as an ordinary dependency: version = floor(nowUtc / bucket). The
        /// entry then self-invalidates the moment the window advances, and until then
        /// it is exactly correct for the window it was built for.
        ///
        /// Pick the bucket to match how precise the answer must be, not how often the
        /// route is called — a 24-hour digest is honest at a 60s bucket; a countdown
        /// is not. Reach for <see cref="MarkUncacheable"/> only when no bucket is
        /// tolerable.
        /// </summary>
        public static void NoteTimeBucket(TimeSpan bucket)
        {
            try
            {
                long ticks = bucket.Ticks;
                if (ticks <= 0) { MarkUncacheable("non-positive time bucket"); return; }
                NoteRead(ClockKeyPrefix + ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch { }
        }

        private static long CurrentBucket(string key)
        {
            try
            {
                string span = key.Substring(ClockKeyPrefix.Length);
                if (!long.TryParse(span, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out long ticks) || ticks <= 0)
                {
                    // Unparseable bucket: return an always-changing version so the
                    // entry can never be served. Fail closed, never stale.
                    return UtcNowProvider().Ticks;
                }
                return UtcNowProvider().Ticks / ticks;
            }
            catch { return UtcNowProvider().Ticks; }
        }

        /// <summary>
        /// Record that the current fill depends on <paramref name="key"/>. No-op
        /// outside a fill or once the scope is sealed. First note wins per key, so
        /// the earliest (most conservative) observed version is retained.
        /// </summary>
        public static void NoteRead(string key)
        {
            try
            {
                DependencyScope? scope = _current.Value;
                if (scope == null || scope.IsSealed || string.IsNullOrEmpty(key)) return;
                scope.NoteReadInternal(key, CurrentVersion(key));
            }
            catch { /* instrumentation must never break a store */ }
        }

        /// <summary>
        /// Increment a dataset's version — call after the write is durably visible.
        /// Always bumps the global registry (invalidating cached readers); if a fill
        /// is in progress on this execution context it also marks that fill as having
        /// written, so a GET with a hidden side effect is never cached.
        /// </summary>
        public static void Bump(string key)
        {
            try
            {
                if (string.IsNullOrEmpty(key)) return;
                _versions.AddOrUpdate(key, 1, static (_, v) => v + 1);
                _current.Value?.MarkWrote();
            }
            catch { }
        }

        /// <summary>
        /// Mark the current fill as uncacheable (volatile data the version model
        /// can't track — uptime, live market prices, wall-clock content).
        /// </summary>
        public static void MarkUncacheable(string reason)
        {
            try { _current.Value?.MarkUncacheableInternal(string.IsNullOrEmpty(reason) ? "unspecified" : reason); }
            catch { }
        }

        /// <summary>Convenience marker used by the streaming response path.</summary>
        public static void MarkStreaming() => MarkUncacheable("streaming");

        /// <summary>Opens a fill scope on the current execution context.</summary>
        internal static DependencyScope OpenScope()
        {
            var scope = new DependencyScope();
            _current.Value = scope;
            return scope;
        }

        /// <summary>
        /// Seals a fill scope. Late reads from fire-and-forget children spawned by
        /// the handler are ignored (sealed), while late writes still bump the global
        /// registry and invalidate normally.
        /// </summary>
        internal static void Seal(DependencyScope scope)
        {
            try { scope?.Seal(); } catch { }
            _current.Value = null;
        }

        // Test-only hooks (InternalsVisibleTo Omnipotent.Tests).
        internal static void ResetForTests()
        {
            _versions.Clear();
            UtcNowProvider = static () => DateTime.UtcNow;
        }
        internal static DependencyScope? CurrentScopeForTests => _current.Value;
    }

    /// <summary>
    /// Per-fill record of which datasets a handler read (and at what version) plus
    /// whether it wrote or declared itself uncacheable. Thread-safe: a handler may
    /// fan out across Task.Run (e.g. /batch, or parallel prologues) and every child
    /// inherits this same scope instance through ExecutionContext.
    /// </summary>
    public sealed class DependencyScope
    {
        private readonly ConcurrentDictionary<string, long> _reads = new(StringComparer.Ordinal);
        private volatile bool _wrote;
        private volatile string? _uncacheableReason;
        private volatile bool _sealed;

        public bool WroteDuringFill => _wrote;
        public string? UncacheableReason => _uncacheableReason;
        public bool IsSealed => _sealed;
        public int ReadCount => _reads.Count;

        internal void NoteReadInternal(string key, long version)
        {
            if (_sealed) return;
            _reads.TryAdd(key, version);
        }

        internal void MarkWrote()
        {
            if (!_sealed) _wrote = true;
        }

        internal void MarkUncacheableInternal(string reason)
        {
            if (_sealed) return;
            _uncacheableReason ??= reason;
        }

        internal void Seal() => _sealed = true;

        /// <summary>Immutable snapshot of the reads captured during the fill.</summary>
        internal IReadOnlyDictionary<string, long> SnapshotReads()
            => new Dictionary<string, long>(_reads, StringComparer.Ordinal);
    }
}
