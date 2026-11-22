using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Storage.Sqlite
{
    /// <summary>
    /// The single writer of one SQLite file (plan: "one writer per file"). Replaces the legacy process-wide
    /// <c>WriteLock</c> that serialised every write in Omniscience and blocked pool threads with
    /// <c>.Wait()</c> (defects F007/F371).
    ///
    /// Group commit: the loop drains up to <see cref="maxBatch"/> queued operations into one transaction,
    /// each inside its own SAVEPOINT, so one failing operation is rolled back and faulted alone while the
    /// rest commit. Callers' tasks complete only after COMMIT returns, so an awaited write is durable
    /// (synchronous=NORMAL in WAL: durable across process crashes; an OS crash may lose the last commits,
    /// which the change-log watermarks tolerate).
    /// </summary>
    internal sealed class WriterQueue : IAsyncDisposable
    {
        private interface IWriteOp
        {
            CancellationToken Token { get; }
            /// <summary>Who asked for the write ("ModuleHost.RunMaintenanceOnceAsync"), for the line a slow one leaves.</summary>
            string Origin { get; }
            void Execute(SqliteConnection conn, SqliteTransaction tx);
            void Complete();
            void Fail(Exception ex);
            void Cancel();
        }

        private sealed class WriteOp<T> : IWriteOp
        {
            private readonly Func<SqliteConnection, SqliteTransaction, T> work;
            private readonly TaskCompletionSource<T> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly string caller, file;
            private T? result;

            public WriteOp(Func<SqliteConnection, SqliteTransaction, T> work, CancellationToken token, string caller, string file)
            {
                this.work = work;
                Token = token;
                this.caller = caller;
                this.file = file;
            }

            public CancellationToken Token { get; }
            public string Origin => (file.Length == 0 ? "" : System.IO.Path.GetFileNameWithoutExtension(file) + ".") + (caller.Length == 0 ? "?" : caller);
            public Task<T> Task => tcs.Task;
            public void Execute(SqliteConnection conn, SqliteTransaction tx) => result = work(conn, tx);
            public void Complete() => tcs.TrySetResult(result!);
            public void Fail(Exception ex) => tcs.TrySetException(ex);
            public void Cancel() => tcs.TrySetCanceled(Token);
        }

        private readonly Channel<IWriteOp> channel = Channel.CreateUnbounded<IWriteOp>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private readonly SqliteConnection conn;
        private readonly int maxBatch;
        private readonly Action<string, TimeSpan, long>? onSlow;
        private readonly Task loop;
        private long pending;
        private long committedBatches;
        private long lastCommitMicros;

        /// <summary>
        /// One write that takes this long has kept every other write of its file waiting for as long, and is told to
        /// <c>onSlow</c> when it ends. A file has one writer: on the server a clean-up statement held intel.db's for
        /// twenty minutes after every start, and all that showed was that no job finished (22 Nov 2026).
        /// </summary>
        internal static TimeSpan SlowOperation { get; set; } = TimeSpan.FromSeconds(5);

        /// <param name="onSlow">Told (who asked, how long it took, how many writes were waiting) when one write took
        /// <see cref="SlowOperation"/> or longer.</param>
        public WriterQueue(SqliteConnection openWriteConnection, int maxBatch = 256, Action<string, TimeSpan, long>? onSlow = null)
        {
            conn = openWriteConnection;
            this.maxBatch = Math.Max(1, maxBatch);
            this.onSlow = onSlow;
            // Owned long-running loop, awaited in DisposeAsync: not fire-and-forget.
            loop = Task.Factory.StartNew(RunAsync, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        }

        public long PendingOperations => Interlocked.Read(ref pending);
        public long CommittedBatches => Interlocked.Read(ref committedBatches);
        public TimeSpan LastCommitDuration => TimeSpan.FromTicks(Interlocked.Read(ref lastCommitMicros) * 10);

        public Task<T> Enqueue<T>(Func<SqliteConnection, SqliteTransaction, T> work, CancellationToken token, string caller = "", string file = "")
        {
            ArgumentNullException.ThrowIfNull(work);
            var op = new WriteOp<T>(work, token, caller, file);
            Interlocked.Increment(ref pending);
            if (!channel.Writer.TryWrite(op))
            {
                Interlocked.Decrement(ref pending);
                return Task.FromException<T>(new ObjectDisposedException(nameof(WriterQueue), "The store is shutting down; the write was not accepted."));
            }
            return op.Task;
        }

        private async Task RunAsync()
        {
            var batch = new List<IWriteOp>(maxBatch);
            var succeeded = new List<IWriteOp>(maxBatch);
            while (await channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                batch.Clear();
                succeeded.Clear();
                while (batch.Count < maxBatch && channel.Reader.TryRead(out var op)) batch.Add(op);
                if (batch.Count == 0) continue;

                var sw = Stopwatch.StartNew();
                SqliteTransaction? tx = null;
                try
                {
                    tx = conn.BeginTransaction();
                    for (int i = 0; i < batch.Count; i++)
                    {
                        var op = batch[i];
                        if (op.Token.IsCancellationRequested) { op.Cancel(); continue; }
                        conn.Exec(tx, "SAVEPOINT op");
                        long began = Stopwatch.GetTimestamp();
                        try
                        {
                            op.Execute(conn, tx);
                            conn.Exec(tx, "RELEASE op");
                            succeeded.Add(op);
                        }
                        catch (Exception ex)
                        {
                            conn.Exec(tx, "ROLLBACK TO op");
                            conn.Exec(tx, "RELEASE op");
                            op.Fail(ex);
                        }
                        var took = Stopwatch.GetElapsedTime(began);
                        // Waiting behind it: the rest of this batch, and whatever was queued while it ran.
                        if (onSlow != null && took >= SlowOperation) ReportSlow(op.Origin, took, Interlocked.Read(ref pending) - 1 - i);
                    }
                    tx.Commit();
                    foreach (var op in succeeded) op.Complete();
                }
                catch (Exception ex)
                {
                    // BEGIN, a savepoint rollback, or COMMIT failed: nothing in this batch is durable. Fail every
                    // operation, including ones not reached yet, so no caller waits forever (TrySet* is idempotent,
                    // so operations that already faulted or were cancelled keep their original outcome).
                    SafeRollback(tx);
                    var failure = new StoreWriteException("Batch commit failed; the write was rolled back.", ex);
                    foreach (var op in batch) op.Fail(failure);
                }
                finally
                {
                    tx?.Dispose();
                    Interlocked.Add(ref pending, -batch.Count);
                    Interlocked.Increment(ref committedBatches);
                    Interlocked.Exchange(ref lastCommitMicros, (long)(sw.Elapsed.TotalMilliseconds * 1000));
                }
            }
        }

        /// <summary>Tells whoever listens about a slow write. Nothing the listener does may stop the writer.</summary>
        private void ReportSlow(string origin, TimeSpan took, long waiting)
        {
            try
            {
                onSlow!(origin, took, Math.Max(0, waiting));
            }
            catch (Exception)
            {
                // A log that could not be written is not a reason to fail the writes that follow.
                return;
            }
        }

        private static void SafeRollback(SqliteTransaction? tx)
        {
            if (tx == null) return;
            try { tx.Rollback(); }
            catch (InvalidOperationException) { /* already completed: nothing to roll back */ return; }
            catch (SqliteException) { /* connection-level failure: the transaction is gone */ return; }
        }

        public async ValueTask DisposeAsync()
        {
            channel.Writer.TryComplete();
            await loop.ConfigureAwait(false);
        }
    }

    /// <summary>A write that could not be made durable.</summary>
    public sealed class StoreWriteException : Exception
    {
        public StoreWriteException(string message, Exception inner) : base(message, inner) { }
    }
}
