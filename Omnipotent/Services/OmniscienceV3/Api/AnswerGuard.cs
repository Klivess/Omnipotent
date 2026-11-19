using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

namespace Omnipotent.Services.OmniscienceV3.Api
{
    /// <summary>
    /// The answer of one request, and who gives it. The handler normally writes it itself. When the handler has not
    /// answered in time, <see cref="AnswerGuard"/> answers "pending" in its place; what the handler then produces is kept
    /// here for the next request that asks the same thing.
    /// </summary>
    public sealed class AnswerSlot
    {
        private const int Open = 0, Written = 1, Deferred = 2;
        private int state;

        private volatile string? json;

        /// <summary>What the handler produced after the guard had already answered (null until then).</summary>
        public string? Json => json;
        public HttpStatusCode Status { get; private set; }
        /// <summary>Monotonic time at which <see cref="Json"/> was stored.</summary>
        public long StoredAt { get; private set; }

        /// <summary>The handler takes the response for itself (a stream, a file). False: the guard has answered already,
        /// and the handler must write nothing.</summary>
        public bool TryWriteDirect() => Interlocked.CompareExchange(ref state, Written, Open) == Open;

        internal bool TryDefer() => Interlocked.CompareExchange(ref state, Deferred, Open) == Open;

        /// <summary>Gives the answer: written now through <paramref name="direct"/>, or kept when the guard got there first.</summary>
        public Task AnswerAsync(string json, HttpStatusCode status, Func<Task> direct)
        {
            if (TryWriteDirect()) return direct();
            // Only the first answer after a deferral counts (a handler answers once).
            if (this.json == null)
            {
                Status = status;
                StoredAt = Stopwatch.GetTimestamp();
                this.json = json; // last: whoever sees the answer sees its status too
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Every read answers, and soon (Klives, 19 Nov 2026: "Timeouts are not an acceptable solution. There must always be
    /// a response."). A handler gets <see cref="AnswerWithin"/>. If it has not answered by then, the request is answered
    /// at once with 202 <c>{"code":"pending"}</c>, and the handler carries on: it is not cancelled and not run twice.
    /// The next request for the same thing by the same profile waits on that same piece of work and is given its answer
    /// (or "pending" again, after the same short wait). So no request is ever left open until something gives up on it,
    /// and slow work is done once however often the page asks.
    /// <para>Applied to GET routes. A command (POST) is not answered early: it would have to be sent again to learn its
    /// outcome, and sending a command twice is not the same as asking twice.</para>
    /// </summary>
    public sealed class AnswerGuard
    {
        /// <summary>How long a read may take before the request is answered "pending".</summary>
        public TimeSpan AnswerWithin { get; init; } = TimeSpan.FromSeconds(4);

        /// <summary>How long a finished answer nobody came back for is kept.</summary>
        public TimeSpan KeepFor { get; init; } = TimeSpan.FromMinutes(2);

        /// <summary>What the page is told to wait before asking again.</summary>
        public const int RetryAfterMs = 1500;

        public const string PendingCode = "pending";

        public static readonly string PendingJson =
            "{\"code\":\"" + PendingCode + "\",\"message\":\"This is taking longer than usual. It is still being worked out on the server; ask again in a moment.\",\"retryAfterMs\":" +
            RetryAfterMs.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";

        private sealed record Late(AnswerSlot Slot, Task Work);

        private readonly ConcurrentDictionary<string, Late> late = new(StringComparer.Ordinal);
        private long deferred;

        /// <summary>Requests answered "pending" since the engine started.</summary>
        public long Deferred => Interlocked.Read(ref deferred);

        /// <summary>Work that was answered "pending" and is still running, or finished and not yet collected.</summary>
        public int Waiting => late.Count;

        /// <param name="key">What is asked and by whom: the same key is the same piece of work.</param>
        /// <param name="handle">Runs the handler. It answers through <see cref="ApiResponses"/> and never throws.</param>
        /// <param name="write">Writes a kept answer to this request.</param>
        /// <param name="pending">Writes the "pending" answer to this request.</param>
        public async Task RunAsync(string key, Func<Task> handle, Func<string, HttpStatusCode, Task> write, Func<Task> pending)
        {
            Forget();
            if (late.TryGetValue(key, out var earlier))
            {
                // Asked before, answered "pending" then: wait a little for that same work instead of starting it again.
                if (earlier.Slot.Json == null) await Task.WhenAny(earlier.Work, Task.Delay(AnswerWithin)).ConfigureAwait(false);
                // Read in this order: an answer is stored before its work completes, so work seen as finished here has
                // its answer visible below. The other way round, an answer arriving between the two reads looked like
                // "finished without one" and the work was started again.
                bool finished = earlier.Work.IsCompleted;
                if (earlier.Slot.Json is { } kept)
                {
                    late.TryRemove(new KeyValuePair<string, Late>(key, earlier));
                    await write(kept, earlier.Slot.Status).ConfigureAwait(false);
                    return;
                }
                if (!finished)
                {
                    Interlocked.Increment(ref deferred);
                    await pending().ConfigureAwait(false);
                    return;
                }
                // It ended without an answer to keep (it wrote a stream itself): ask afresh.
                late.TryRemove(new KeyValuePair<string, Late>(key, earlier));
            }

            var slot = new AnswerSlot();
            ApiResponses.Slot = slot;
            Task work;
            // On the pool, not called here: most handlers are plain synchronous code (read, then answer), and a call
            // made here would not come back until they were done, so the wait below would never begin. That is how
            // the first deployment of this guard still left two searches open for a minute. The slot, the response
            // cache's notes and the request's trace are all carried by the execution context, which Task.Run keeps.
            try { work = Task.Run(handle); }
            finally { ApiResponses.Slot = null; }

            if (await Task.WhenAny(work, Task.Delay(AnswerWithin)).ConfigureAwait(false) == work || !slot.TryDefer())
            {
                await work.ConfigureAwait(false); // answered in time, or answering right now
                return;
            }
            late[key] = new Late(slot, work);
            Interlocked.Increment(ref deferred);
            await pending().ConfigureAwait(false);
        }

        /// <summary>Drops answers nobody came back for, and work that ended without one.</summary>
        private void Forget()
        {
            if (late.IsEmpty) return;
            foreach (var entry in late)
            {
                var slot = entry.Value.Slot;
                bool finished = entry.Value.Work.IsCompleted; // before the answer is read, for the reason given in RunAsync
                bool stale = slot.Json != null ? Stopwatch.GetElapsedTime(slot.StoredAt) > KeepFor : finished;
                if (stale) late.TryRemove(entry);
            }
        }
    }
}
