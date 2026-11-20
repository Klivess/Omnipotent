using System.Diagnostics;

namespace Omnipotent.Services.OmniscienceV3.Ingest.Media
{
    /// <summary>
    /// How long one media sweep may work. A sweep tops the queue up with what the change log missed; it walks the corpus
    /// from a cursor and wraps round, so what it does not reach now it reaches next time. Without a limit a sweep was as
    /// long as the corpus is large, and it ran again every ten to thirty minutes.
    /// </summary>
    internal sealed class MediaSweep
    {
        /// <summary>The time one sweep gets. Tests shorten it.</summary>
        internal static TimeSpan TimeLimit { get; set; } = TimeSpan.FromSeconds(45);

        private readonly long started = Stopwatch.GetTimestamp();
        private readonly TimeSpan limit;

        private MediaSweep(TimeSpan limit) => this.limit = limit;

        public static MediaSweep Start() => new(TimeLimit);

        /// <summary>A part of the sweep that may use this share of the time that is left.</summary>
        public MediaSweep Part(double share) => new(TimeSpan.FromTicks((long)(Math.Max(0, (limit - Elapsed).Ticks) * share)));

        public TimeSpan Elapsed => Stopwatch.GetElapsedTime(started);

        public bool Spent => Elapsed >= limit;
    }
}
