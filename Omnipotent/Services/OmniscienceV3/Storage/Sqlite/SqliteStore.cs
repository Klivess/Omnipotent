using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Storage.Sqlite
{
    /// <summary>Health of one store, surfaced on the Modules/Jobs pages.</summary>
    public sealed record StoreHealth(string Name, string Path, long PendingWrites, long CommittedBatches, TimeSpan LastCommit, long FileBytes, long WalBytes);

    /// <summary>
    /// One SQLite file with exactly one writer (a <see cref="WriterQueue"/>) and any number of pooled
    /// read-only readers. WAL mode, private cache (never Cache=Shared: it funnels every connection through
    /// one mutex and deadlocked the thread pool in v2), tuned pragmas applied once per connection
    /// (legacy defect F372).
    /// </summary>
    public sealed class SqliteStore : IAsyncDisposable
    {
        private readonly string readWriteConnectionString;
        private readonly string readOnlyConnectionString;
        private readonly IClock clock;
        private SqliteConnection? writerConnection;
        private WriterQueue? writer;
        private int disposed;

        public string Name { get; }
        public string Path { get; }

        public SqliteStore(string name, string path, IClock clock)
        {
            Name = name;
            Path = path;
            this.clock = clock;
            readWriteConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false, // the single writer connection lives for the process lifetime
                Cache = SqliteCacheMode.Private,
            }.ToString();
            readOnlyConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = true,
                Cache = SqliteCacheMode.Private,
                DefaultTimeout = 10,
            }.ToString();
        }

        /// <summary>Opens the writer, switches to WAL, starts the write loop and applies <paramref name="migrations"/>.</summary>
        public async Task InitializeAsync(IEnumerable<Migration> migrations, CancellationToken ct = default)
        {
            if (writer != null) throw new InvalidOperationException($"Store '{Name}' is already initialised.");
            string? dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var conn = new SqliteConnection(readWriteConnectionString);
            conn.Open();
            // journal_mode is persistent and cannot change inside a transaction: set it before the loop starts.
            conn.Exec(null, "PRAGMA journal_mode=WAL;");
            conn.Exec(null,
                "PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA temp_store=MEMORY; PRAGMA busy_timeout=10000;" +
                " PRAGMA cache_size=-65536; PRAGMA mmap_size=268435456; PRAGMA wal_autocheckpoint=2000;" +
                " PRAGMA journal_size_limit=134217728;");
            writerConnection = conn;
            writer = new WriterQueue(conn, onSlow: (origin, took, waiting) => SlowWriteLog?.Invoke(
                $"[OmniscienceV3] {Name}.db: one write ({origin}) held the writer for {took.TotalSeconds:0} s; {waiting} other write(s) waited behind it."));
            await ApplyMigrationsAsync(migrations, ct).ConfigureAwait(false);
        }

        /// <summary>Where a write that held this file's one writer for long is reported (see <see cref="WriterQueue.SlowOperation"/>).</summary>
        public Action<string>? SlowWriteLog { get; set; }

        /// <summary>Applies module migrations through the writer (safe while the store is live).</summary>
        public Task<int> ApplyMigrationsAsync(IEnumerable<Migration> migrations, CancellationToken ct = default)
        {
            var list = migrations.ToList();
            return WriteAsync((c, tx) => SchemaMigrator.Apply(c, tx, list, clock.NowMs), ct);
        }

        /// <summary>A pooled read-only connection. Dispose it promptly: long-lived readers starve WAL checkpoints (F373).
        /// Opened inside a job, its statements stop when the job is cancelled (<see cref="AmbientCancellation"/>).</summary>
        public SqliteConnection OpenRead()
        {
            EnsureLive();
            var stopWith = AmbientCancellation.Token;
            stopWith.ThrowIfCancellationRequested();
            for (int attempt = 0; ; attempt++)
            {
                var conn = new ReadConnection(readOnlyConnectionString);
                try
                {
                    conn.Open();
                    conn.Exec(null, "PRAGMA query_only=ON; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-32768; PRAGMA mmap_size=268435456; PRAGMA busy_timeout=10000;");
                    conn.StopWith(stopWith);
                    // A job's reads give way to a request that is being answered (ForegroundIo); a request's never wait.
                    if (ForegroundIo.Enabled && ForegroundIo.InBackground) conn.YieldToForeground();
                    return conn;
                }
                catch (ObjectDisposedException) when (attempt < 3)
                {
                    // Something else in the process emptied every connection pool (SqliteConnection.ClearAllPools) at the very
                    // moment this connection was handed out, and closed it under us. It is of no use: take another.
                    conn.Dispose();
                }
            }
        }

        /// <param name="caller">Filled in by the compiler: who asked, for the line a slow write leaves.</param>
        /// <param name="file">Filled in by the compiler.</param>
        public Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> work, CancellationToken ct = default,
            [System.Runtime.CompilerServices.CallerMemberName] string caller = "", [System.Runtime.CompilerServices.CallerFilePath] string file = "")
        {
            EnsureLive();
            return writer!.Enqueue(work, ct, caller, file);
        }

        public Task WriteAsync(Action<SqliteConnection, SqliteTransaction> work, CancellationToken ct = default,
            [System.Runtime.CompilerServices.CallerMemberName] string caller = "", [System.Runtime.CompilerServices.CallerFilePath] string file = "") =>
            WriteAsync<bool>((c, tx) => { work(c, tx); return true; }, ct, caller, file);

        public StoreHealth GetHealth()
        {
            long fileBytes = File.Exists(Path) ? new FileInfo(Path).Length : 0;
            string wal = Path + "-wal";
            long walBytes = File.Exists(wal) ? new FileInfo(wal).Length : 0;
            return new StoreHealth(Name, Path, writer?.PendingOperations ?? 0, writer?.CommittedBatches ?? 0,
                writer?.LastCommitDuration ?? TimeSpan.Zero, fileBytes, walBytes);
        }

        /// <summary>
        /// Passive WAL checkpoint, run by maintenance when readers are idle (F373). PASSIVE never blocks the
        /// writer or readers; it copies whatever frames no reader still needs. Runs on its own short-lived
        /// connection because the writer connection is always inside a batch transaction.
        /// Returns (walFrames, checkpointedFrames).
        /// </summary>
        public (long WalFrames, long Checkpointed) CheckpointPassive()
        {
            EnsureLive();
            using var conn = new SqliteConnection(readWriteConnectionString);
            conn.Open();
            using var cmd = conn.Cmd(null, "PRAGMA wal_checkpoint(PASSIVE);");
            using var r = cmd.ExecuteReader();
            return r.Read() ? (r.GetInt64(1), r.GetInt64(2)) : (0, 0);
        }

        private void EnsureLive()
        {
            if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(Name);
            if (writer == null) throw new InvalidOperationException($"Store '{Name}' is not initialised.");
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            if (writer != null) await writer.DisposeAsync().ConfigureAwait(false);
            if (writerConnection != null)
            {
                writerConnection.Exec(null, "PRAGMA optimize;");
                writerConnection.Dispose();
            }
            SqliteConnection.ClearPool(new SqliteConnection(readOnlyConnectionString));
        }
    }
}
