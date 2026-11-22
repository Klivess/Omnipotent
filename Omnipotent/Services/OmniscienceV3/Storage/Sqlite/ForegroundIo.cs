using System.Collections.Concurrent;
using System.Diagnostics;

namespace Omnipotent.Services.OmniscienceV3.Storage.Sqlite
{
    /// <summary>
    /// Whoever is waiting for an answer goes first. While a request is being answered, the reads of background jobs wait.
    /// <para>The engine's stores sit on one slow drive (on the server a page that is not in memory took 25 to 150 ms to
    /// read while seven jobs were reading beside it; the online backup managed 218 pages a second), and SQLite empties a
    /// reader's page cache at every commit, so a request's reads are disk reads and stand in the same queue as every
    /// job's. A list of nine jobs took one to two seconds, the status the page asks for every ten seconds had a median of
    /// 1.2 s, and nearly every read of the engine reached the four-second limit at its 95th percentile (21 Nov 2026).
    /// Nothing a job does is so urgent that it should be read ahead of the page somebody is looking at.</para>
    /// <para>A request counts from the moment it arrives until it is answered, and for <see cref="MaxHold"/> at most, so
    /// one that runs long (an export, a stream, a slow search) does not hold the jobs back for its whole length. A job's
    /// wait is bounded too (<see cref="MaxYield"/>), and a job that has waited that long then reads for
    /// <see cref="MinRun"/> before it waits again: with requests arriving without a break the jobs still get about a
    /// fifth of the time, instead of a few thousand steps between one wait and the next.</para>
    /// </summary>
    public static class ForegroundIo
    {
        private static readonly ConcurrentDictionary<Scope, byte> answering = new();
        private static int waiting;
        private static int yielding;
        private static long yields;
        private static long yieldedTicks;

        /// <summary>Off: jobs never wait for requests (the tests that time jobs; a switch for the server).</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>The longest one request counts as being answered.</summary>
        public static TimeSpan MaxHold { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>The longest a job waits in one go before it reads anyway.</summary>
        public static TimeSpan MaxYield { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>How long a job that has waited <see cref="MaxYield"/> reads before it can be made to wait again.</summary>
        public static TimeSpan MinRun { get; set; } = TimeSpan.FromMilliseconds(500);

        /// <summary>How many times a job has waited for a request since the process started.</summary>
        public static long Yields => Interlocked.Read(ref yields);

        /// <summary>How long jobs have waited for requests in all since the process started.</summary>
        public static TimeSpan Yielded => Stopwatch.GetElapsedTime(0, Interlocked.Read(ref yieldedTicks));

        /// <summary>How many jobs are waiting for a request at this moment.</summary>
        public static int Yielding => Volatile.Read(ref yielding);

        /// <summary>A request is being answered right now. Asked by every job every few thousand steps of a read, so the
        /// common answer (nobody is) is one read of a counter.</summary>
        public static bool Busy
        {
            get
            {
                if (Volatile.Read(ref waiting) == 0) return false;
                long now = Stopwatch.GetTimestamp();
                foreach (var scope in answering.Keys)
                    if (now < scope.Until) return true;
                return false;
            }
        }

        /// <summary>Marks a request as being answered until the returned scope is disposed (or <see cref="MaxHold"/> has passed).</summary>
        public static Scope Enter()
        {
            var scope = new Scope(Stopwatch.GetTimestamp() + (long)(MaxHold.TotalSeconds * Stopwatch.Frequency));
            answering[scope] = 0;
            Interlocked.Increment(ref waiting);
            return scope;
        }

        /// <summary>One request being answered. Disposing it twice counts once.</summary>
        public sealed class Scope : IDisposable
        {
            private int left;

            internal Scope(long until) => Until = until;

            /// <summary>The monotonic time after which this request no longer holds the jobs back.</summary>
            internal long Until { get; }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref left, 1) != 0) return;
                answering.TryRemove(this, out _);
                Interlocked.Decrement(ref waiting);
            }
        }

        /// <summary>Whether the calling code runs inside a job nobody is waiting for (its reads are the ones that wait). A job
        /// of the interactive lane is somebody's request carried on in the queue, and counts as one.</summary>
        public static bool InBackground => AmbientCancellation.Token.CanBeCanceled && AmbientCancellation.Pace is not { Foreground: true };

        /// <summary>Called by a job between two reads, and from inside a long one: waits while a request is being answered.</summary>
        public static void YieldIfBackground()
        {
            if (!Enabled || !Busy) return;
            var token = AmbientCancellation.Token;
            if (!token.CanBeCanceled) return;
            var pace = AmbientCancellation.Pace;
            if (pace is { Foreground: true }) return;
            long started = Stopwatch.GetTimestamp();
            if (pace != null && started < Volatile.Read(ref pace.RunUntil)) return; // it has just waited its limit: it reads for a while first
            long limit = (long)(MaxYield.TotalSeconds * Stopwatch.Frequency);
            Interlocked.Increment(ref yielding);
            try
            {
                while (Busy && !token.IsCancellationRequested && Stopwatch.GetTimestamp() - started < limit) Thread.Sleep(2);
            }
            finally
            {
                Interlocked.Decrement(ref yielding);
                Note(started);
            }
            Rest(pace, started, limit);
        }

        /// <summary>A job that waited its whole limit reads for <see cref="MinRun"/> before it can be made to wait again.</summary>
        private static void Rest(WorkPace? pace, long started, long limit)
        {
            long ended = Stopwatch.GetTimestamp();
            if (pace != null && ended - started >= limit) Volatile.Write(ref pace.RunUntil, ended + (long)(MinRun.TotalSeconds * Stopwatch.Frequency));
        }

        /// <summary>The same wait for code that is between awaits (a job reporting its progress).</summary>
        public static async ValueTask YieldIfBackgroundAsync()
        {
            if (!Enabled || !Busy) return;
            var token = AmbientCancellation.Token;
            if (!token.CanBeCanceled) return;
            var pace = AmbientCancellation.Pace;
            if (pace is { Foreground: true }) return;
            long started = Stopwatch.GetTimestamp();
            if (pace != null && started < Volatile.Read(ref pace.RunUntil)) return;
            long limit = (long)(MaxYield.TotalSeconds * Stopwatch.Frequency);
            Interlocked.Increment(ref yielding);
            try
            {
                while (Busy && !token.IsCancellationRequested && Stopwatch.GetTimestamp() - started < limit)
                    await Task.Delay(5, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref yielding);
                Note(started);
            }
            Rest(pace, started, limit);
        }

        private static void Note(long started)
        {
            Interlocked.Increment(ref yields);
            Interlocked.Add(ref yieldedTicks, Stopwatch.GetTimestamp() - started);
        }
    }
}
