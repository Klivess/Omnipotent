using Omnipotent.Services.KliveAPI.Caching;

namespace Omnipotent.Tests.Projects
{
    /// <summary>
    /// Drives a store read the way KliveAPI's dispatcher does — inside a dependency scope, sealed
    /// afterwards — so a test can assert what a cached response would do rather than only what the
    /// store returns. Every /projects/* GET resolves its project through ProjectStore (which notes
    /// `projects:index`), so these fills are cacheable whether or not the store being read is
    /// instrumented; that is exactly what makes an uninstrumented store serve stale data.
    /// </summary>
    internal static class CacheFillProbe
    {
        /// <summary>Runs <paramref name="read"/> as a cache fill and returns the recorded scope.</summary>
        public static DependencyScope Fill(Action read)
        {
            var scope = CacheDeps.OpenScope();
            try { read(); }
            finally { CacheDeps.Seal(scope); }
            return scope;
        }

        /// <summary>Mirrors ResponseCache.IsValid: an entry survives only while every dataset it read
        /// is still at the version it read.</summary>
        public static bool StillValid(this DependencyScope scope) =>
            scope.SnapshotReads().All(dep => CacheDeps.CurrentVersion(dep.Key) == dep.Value);

        /// <summary>Mirrors ResponseCache's store-time rules for the parts a store controls: a fill
        /// that noted nothing is never cached, and one that declared itself uncacheable never is either.</summary>
        public static bool WouldBeCached(this DependencyScope scope) =>
            scope.ReadCount > 0 && scope.UncacheableReason == null && !scope.WroteDuringFill;
    }
}
