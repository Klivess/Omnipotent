using Microsoft.Data.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Storage.Sqlite
{
    /// <summary>
    /// The cancellation of the work the current flow of execution belongs to: a job's handler sets it once, and every read
    /// the handler makes, however deep, stops with it.
    /// <para>SQLite runs a statement to its end on the calling thread and knows nothing of cancellation tokens. A handler
    /// that was told to stop therefore went on reading for as long as its statement took: on the server, jobs that were
    /// stopped for making no progress "did not stop when asked" 64 times in sixteen hours (20 Nov 2026), their handlers
    /// went on running beside the retries, and each held a thread and the disk. A read connection opened inside such a
    /// flow is now interrupted when the token is cancelled, and no new statement starts after that.</para>
    /// </summary>
    public static class AmbientCancellation
    {
        private static readonly AsyncLocal<CancellationToken> current = new();

        /// <summary>The token of the surrounding work; <see cref="CancellationToken.None"/> outside any.</summary>
        public static CancellationToken Token => current.Value;

        /// <summary>Makes <paramref name="token"/> the cancellation of everything this flow goes on to do (its awaits and the
        /// tasks it starts included). Call it at the top of the task that runs the work; it does not reach the caller.</summary>
        public static void Set(CancellationToken token) => current.Value = token;

        /// <summary>Throws when the surrounding work has been cancelled.</summary>
        public static void ThrowIfCancelled() => current.Value.ThrowIfCancellationRequested();

        /// <summary>SQLITE_INTERRUPT: the statement was stopped by <c>sqlite3_interrupt</c>.</summary>
        private const int Interrupted = 9;

        /// <summary>Whether <paramref name="ex"/> is a statement that was stopped because the surrounding work was cancelled.</summary>
        public static bool IsInterrupt(Exception ex) => ex is SqliteException { SqliteErrorCode: Interrupted } && current.Value.IsCancellationRequested;

        /// <summary>Whether <paramref name="ex"/> means "stopped because <paramref name="token"/> was cancelled": a cancellation,
        /// or a statement interrupted on its account (also when wrapped by a caller).</summary>
        public static bool IsStop(Exception ex, CancellationToken token)
        {
            if (!token.IsCancellationRequested) return false;
            for (Exception? e = ex; e != null; e = e.InnerException)
                if (e is OperationCanceledException or SqliteException { SqliteErrorCode: Interrupted }) return true;
            return false;
        }
    }

    /// <summary>
    /// A read connection that stops its running statement when the work it was opened for is cancelled. The registration
    /// ends before the connection goes back to the pool, so a late cancellation can never interrupt whoever holds the
    /// pooled handle next.
    /// </summary>
    internal sealed class ReadConnection : SqliteConnection
    {
        private CancellationTokenRegistration stop;

        public ReadConnection(string connectionString) : base(connectionString) { }

        /// <summary>Interrupts this connection's running statement when <paramref name="token"/> is cancelled. Call once, after Open.</summary>
        public void StopWith(CancellationToken token)
        {
            if (!token.CanBeCanceled) return;
            stop = token.Register(static state =>
            {
                var conn = (ReadConnection)state!;
                try
                {
                    // Safe from another thread, and a no-op when nothing is running.
                    if (conn.Handle is { } handle) SQLitePCL.raw.sqlite3_interrupt(handle);
                }
                catch (ObjectDisposedException) { /* closed in the same instant: nothing left to stop */ return; }
            }, this);
        }

        public override void Close()
        {
            // Waits for an interrupt that is being delivered right now, so none arrives after the handle is handed back.
            stop.Dispose();
            stop = default;
            base.Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                stop.Dispose();
                stop = default;
            }
            base.Dispose(disposing);
        }
    }
}
