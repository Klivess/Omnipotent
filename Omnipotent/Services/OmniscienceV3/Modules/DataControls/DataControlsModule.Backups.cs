using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Modules.DataControls
{
    /// <summary>One store inside a backup. <c>sha256</c> is of the database file before encryption.</summary>
    [Api.ApiContract]
    public sealed record BackupFileDto(string Store, string File, long PlainBytes, long EncryptedBytes, string Sha256);

    /// <summary>An encrypted snapshot of corpus.db and operator.db. <c>keyId</c> names the key that opens it.</summary>
    [Api.ApiContract]
    public sealed record BackupDto(string BackupId, long CreatedAtMs, string Trigger, string KeyId, IReadOnlyList<BackupFileDto> Files);

    [Api.ApiContract]
    public sealed record BackupListDto(IReadOnlyList<BackupDto> Backups, string? KeyId, int Keep, bool Enabled);

    /// <summary>Where a restore put the decrypted, verified database files. They are never swapped in automatically.</summary>
    [Api.ApiContract]
    public sealed record RestoreDto(string BackupId, string Folder, IReadOnlyList<string> Files, string Note);

    /// <summary>How long a stream of events is kept, and what retention has removed so far (disclosed, never silent: F519).</summary>
    [Api.ApiContract]
    public sealed record RetentionDto(string Stream, int KeptDays, long? CutoffMs, long DeletedTotal, long LastDeleted, long? LastRunMs);

    [Api.ApiContract]
    public sealed record RetentionListDto(IReadOnlyList<RetentionDto> Streams);

    public sealed partial class DataControlsModule
    {
        // ── retention ────────────────────────────────────────────────────────────

        private static readonly (string Stream, string Setting, string Table, string Time)[] RetainedStreams =
        {
            ("presence", "datacontrols.retention.presence_days", "presence_events", "at_ms"),
            ("typing", "datacontrols.retention.typing_days", "typing_events", "at_ms"),
            ("voice", "datacontrols.retention.voice_days", "voice_sessions", "COALESCE(left_at_ms, joined_at_ms)"),
        };

        /// <summary>
        /// Deletes events older than each stream's retention, by the time the event happened (not when it was ingested), and
        /// records what was removed so the pages can say how far back a stream reaches. Messages are never subject to it.
        /// </summary>
        private async Task<string?> RunRetentionAsync(JobContext job)
        {
            var ct = job.Cancellation;
            long now = Context.Clock.NowMs;
            var removed = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var (stream, setting, table, time) in RetainedStreams)
            {
                int days = Context.Settings.Get(setting, 0);
                long deleted = 0, cutoff = now - days * 86_400_000L;
                if (days > 0)
                {
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        // In slices, so a first run over years of events never holds the corpus writer for long.
                        int n = await Context.Stores.Corpus.WriteAsync((c, tx) => c.Exec(tx, $"DELETE FROM {table} WHERE rowid IN (SELECT rowid FROM {table} WHERE {time} < $c LIMIT 20000)", ("$c", cutoff)), ct)
                            .ConfigureAwait(false);
                        deleted += n;
                        if (n < 20_000) break;
                        await job.ReportProgressAsync(new { stream, deleted }).ConfigureAwait(false);
                    }
                }
                removed[stream] = deleted;
                await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                    INSERT INTO dc_retention(stream, kept_days, cutoff_ms, deleted_total, last_deleted, last_run_ms) VALUES($s,$d,$c,$n,$n,$t)
                    ON CONFLICT(stream) DO UPDATE SET kept_days=excluded.kept_days, cutoff_ms=CASE WHEN excluded.kept_days > 0 THEN excluded.cutoff_ms ELSE cutoff_ms END,
                        deleted_total=deleted_total + excluded.last_deleted, last_deleted=excluded.last_deleted, last_run_ms=excluded.last_run_ms
                    """, ("$s", stream), ("$d", days), ("$c", days > 0 ? cutoff : 0), ("$n", deleted), ("$t", now)), ct).ConfigureAwait(false);
            }
            if (removed.Values.Sum() > 0)
                await Kernel.OperatorAudit.WriteAsync(Context.Stores.Operator, Context.Clock, "datacontrols", "retention.run", null, removed, ct).ConfigureAwait(false);
            return Json(removed);
        }

        public RetentionListDto Retention()
        {
            Dictionary<string, (long Cutoff, long Total, long Last, long At)> log;
            using (var c = Context.Stores.Intel.OpenRead())
                log = c.Query(null, "SELECT stream, cutoff_ms, deleted_total, last_deleted, last_run_ms FROM dc_retention", r => (r.GetString(0), (r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4))))
                    .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
            return new RetentionListDto(RetainedStreams.Select(s =>
            {
                log.TryGetValue(s.Stream, out var l);
                return new RetentionDto(s.Stream, Context.Settings.Get(s.Setting, 0), l.Cutoff > 0 ? l.Cutoff : null, l.Total, l.Last, l.At > 0 ? l.At : null);
            }).ToList());
        }

        // ── backups ──────────────────────────────────────────────────────────────

        private const int BackupChunk = 1 << 20;
        private static readonly byte[] BackupMagic = Encoding.ASCII.GetBytes("OMNIBK01");
        private const string KeyBlobPrefix = "OMNIKEY1";
        private static readonly byte[] KeyEntropy = Encoding.ASCII.GetBytes("omniscience-v3-backup-key");

        private string KeyFile => Path.Combine(Context.Stores.Paths.Backups, "backup-key.dpapi");

        private static string KeyId(byte[] key) => Convert.ToHexString(SHA256.HashData(key))[..12].ToLowerInvariant();

        /// <summary>The backup key: 256 random bits, made once, kept on disk only under Windows DPAPI for the account the
        /// service runs as. Another account, or another machine, cannot read it; that is what the passphrase export is for.</summary>
        private byte[] LoadOrCreateKey()
        {
            if (!OperatingSystem.IsWindows()) throw new JobFailureException(ErrorClass.Permanent, "Backups protect their key with Windows DPAPI and can only be taken on Windows.");
            Directory.CreateDirectory(Context.Stores.Paths.Backups);
            if (File.Exists(KeyFile)) return ProtectedData.Unprotect(File.ReadAllBytes(KeyFile), KeyEntropy, DataProtectionScope.CurrentUser);
            byte[] key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(KeyFile, ProtectedData.Protect(key, KeyEntropy, DataProtectionScope.CurrentUser));
            return key;
        }

        /// <summary>AES-256-GCM over 1 MiB chunks. Each chunk is authenticated with its index and whether it is the last, so a
        /// truncated, reordered or altered file fails to open instead of restoring silently wrong.</summary>
        private static async Task<(long Plain, long Encrypted, string Sha256)> EncryptFileAsync(string source, string target, byte[] key, CancellationToken ct, Func<long, Task>? progress = null)
        {
            using var aes = new AesGcm(key, 16);
            byte[] baseNonce = RandomNumberGenerator.GetBytes(12);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BackupChunk, useAsync: true);
            await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, BackupChunk, useAsync: true);
            await output.WriteAsync(BackupMagic, ct).ConfigureAwait(false);
            await output.WriteAsync(baseNonce, ct).ConfigureAwait(false);
            byte[] plain = new byte[BackupChunk], next = new byte[BackupChunk], cipher = new byte[BackupChunk], tag = new byte[16], header = new byte[4], nonce = new byte[12], aad = new byte[9];
            long total = 0;
            uint index = 0;
            int read = await ReadFullAsync(input, plain, ct).ConfigureAwait(false);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                // Read one chunk ahead (into the other buffer) to know whether this one is the last.
                int nextRead = read == BackupChunk ? await ReadFullAsync(input, next, ct).ConfigureAwait(false) : 0;
                bool last = nextRead == 0;
                ChunkNonce(baseNonce, index, nonce);
                BinaryPrimitives.WriteUInt64BigEndian(aad, index);
                aad[8] = last ? (byte)1 : (byte)0;
                aes.Encrypt(nonce, plain.AsSpan(0, read), cipher.AsSpan(0, read), tag, aad);
                sha.AppendData(plain, 0, read);
                BinaryPrimitives.WriteInt32BigEndian(header, read);
                await output.WriteAsync(header, ct).ConfigureAwait(false);
                await output.WriteAsync(cipher.AsMemory(0, read), ct).ConfigureAwait(false);
                await output.WriteAsync(tag, ct).ConfigureAwait(false);
                total += read;
                index++;
                if (last) break;
                if (progress != null && index % 64 == 0) await progress(total).ConfigureAwait(false);
                (plain, next) = (next, plain);
                read = nextRead;
            }
            await output.FlushAsync(ct).ConfigureAwait(false);
            return (total, output.Length, Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant());
        }

        private static async Task<string> DecryptFileAsync(string source, string target, byte[] key, CancellationToken ct)
        {
            using var aes = new AesGcm(key, 16);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BackupChunk, useAsync: true);
            await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, BackupChunk, useAsync: true);
            byte[] magic = new byte[BackupMagic.Length], baseNonce = new byte[12], header = new byte[4], tag = new byte[16], nonce = new byte[12], aad = new byte[9];
            if (await ReadFullAsync(input, magic, ct).ConfigureAwait(false) != magic.Length || !magic.AsSpan().SequenceEqual(BackupMagic)) throw new InvalidDataException("This is not an Omniscience backup file.");
            if (await ReadFullAsync(input, baseNonce, ct).ConfigureAwait(false) != 12) throw new InvalidDataException("The backup file is cut off.");
            byte[] cipher = new byte[BackupChunk], plain = new byte[BackupChunk];
            uint index = 0;
            bool sawLast = false;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int h = await ReadFullAsync(input, header, ct).ConfigureAwait(false);
                if (h == 0) break;
                if (h != 4 || sawLast) throw new InvalidDataException("The backup file is damaged.");
                int length = BinaryPrimitives.ReadInt32BigEndian(header);
                if (length < 0 || length > BackupChunk) throw new InvalidDataException("The backup file is damaged.");
                if (await ReadFullAsync(input, cipher.AsMemory(0, length), ct).ConfigureAwait(false) != length || await ReadFullAsync(input, tag, ct).ConfigureAwait(false) != 16)
                    throw new InvalidDataException("The backup file is cut off.");
                ChunkNonce(baseNonce, index, nonce);
                BinaryPrimitives.WriteUInt64BigEndian(aad, index);
                // The chunk says whether it is the last one; try "last" when nothing follows.
                bool last = input.Position == input.Length;
                aad[8] = last ? (byte)1 : (byte)0;
                try { aes.Decrypt(nonce, cipher.AsSpan(0, length), tag, plain.AsSpan(0, length), aad); }
                catch (CryptographicException ex) { throw new InvalidDataException("The backup file is damaged, or this is not the key it was made with.", ex); }
                sha.AppendData(plain, 0, length);
                await output.WriteAsync(plain.AsMemory(0, length), ct).ConfigureAwait(false);
                sawLast = last;
                index++;
            }
            if (!sawLast) throw new InvalidDataException("The backup file is cut off.");
            await output.FlushAsync(ct).ConfigureAwait(false);
            return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }

        private static void ChunkNonce(byte[] baseNonce, uint index, byte[] nonce)
        {
            baseNonce.CopyTo(nonce, 0);
            Span<byte> counter = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(counter, index);
            for (int i = 0; i < 4; i++) nonce[8 + i] ^= counter[i];
        }

        private static Task<int> ReadFullAsync(Stream s, byte[] buffer, CancellationToken ct) => ReadFullAsync(s, buffer.AsMemory(), ct);

        private static async Task<int> ReadFullAsync(Stream s, Memory<byte> buffer, CancellationToken ct)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int n = await s.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
                if (n == 0) break;
                total += n;
            }
            return total;
        }

        private sealed record BackupManifest(string BackupId, long CreatedAtMs, string Trigger, string KeyId, List<BackupFileDto> Files);

        public const string BackupFolderSetting = "datacontrols.backup.folder";
        /// <summary>The standing problem a backup that could not be taken reports (see <see cref="Kernel.ModuleProblems"/>).</summary>
        public const string BackupProblem = "backup";

        /// <summary>Pages copied per step of the online backup: 32 MB with 4 KiB pages. Between steps the job says how far it
        /// is and can be stopped.</summary>
        internal static int CopyPagesPerStep { get; set; } = 8_192;

        /// <summary>Free bytes on the volume that holds a folder. Where that cannot be told (a network share has no drive to
        /// ask) the backup is attempted: running out of room then fails it with the system's own message. Replaced in tests.</summary>
        internal Func<string, long> FreeSpace { get; set; } = folder =>
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder)) ?? folder).AvailableFreeSpace; }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return long.MaxValue; }
        };

        /// <summary>Where new backups are written: the folder Klives set, or the backups folder beside the databases.</summary>
        private string BackupRoot
        {
            get
            {
                string custom = (Context.Settings.Get(BackupFolderSetting, "") ?? "").Trim();
                return custom.Length > 0 && Path.IsPathFullyQualified(custom) ? custom : Context.Stores.Paths.Backups;
            }
        }

        /// <summary>Every folder backups may be in: where they are written now, and beside the databases (where they were
        /// written before the folder was set).</summary>
        private IReadOnlyList<string> BackupRoots =>
            new[] { BackupRoot, Context.Stores.Paths.Backups }.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>There is not room for a backup where backups go. Nothing was written.</summary>
        public sealed class BackupSpaceException : Exception
        {
            public BackupSpaceException(string folder, long neededBytes, long freeBytes)
                : base($"There is not enough room for a backup in {folder}: it needs about {Gb(neededBytes)} GB free while it is taken and {Gb(freeBytes)} GB are free.")
            {
                Folder = folder;
                NeededBytes = neededBytes;
                FreeBytes = freeBytes;
            }

            public string Folder { get; }
            public long NeededBytes { get; }
            public long FreeBytes { get; }

            private static string Gb(long bytes) => (bytes / 1_073_741_824.0).ToString("0.#", CultureInfo.InvariantCulture);
        }

        private static long StoreBytes(SqliteStore store)
        {
            var health = store.GetHealth();
            return health.FileBytes + health.WalBytes;
        }

        /// <summary>
        /// The most room a backup takes while it is made. Each store is copied in the clear and then encrypted, and the clear
        /// copy is removed only after that: so at its largest it holds the stores already encrypted plus two copies of the
        /// one in hand, and a margin.
        /// </summary>
        internal static long BytesNeeded(IReadOnlyList<long> storeBytes)
        {
            long peak = 0, done = 0;
            foreach (long size in storeBytes)
            {
                peak = Math.Max(peak, done + 2 * size);
                done += size;
            }
            return peak + 256L * 1024 * 1024;
        }

        /// <summary>
        /// The backup job. A backup that cannot be taken for want of room is not attempted and not retried: copying the corpus
        /// is the heaviest thing this engine does to the disk, and the room does not come back by trying again. On the server
        /// the nightly backup needed 30 GB on a drive with 21 GB free; it was started ten times over two nights, each copy
        /// was left running when the job was stopped for making no progress, and it ended as a dead job each night
        /// (20 Nov 2026). It now says what it needs, as a standing problem on the health page, and ends.
        /// </summary>
        private async Task<string?> RunBackupAsync(JobContext job)
        {
            var ct = job.Cancellation;
            try
            {
                var backup = await BackupAsync(job.Job.SubjectId ?? "scheduled", ct, job.ReportProgressAsync).ConfigureAwait(false);
                await Kernel.ModuleProblems.ClearAsync(Context.Stores.Intel, ModuleId, BackupProblem, CancellationToken.None).ConfigureAwait(false);
                return Json(backup);
            }
            catch (BackupSpaceException ex)
            {
                long? last = Backups().Backups.Select(b => (long?)b.CreatedAtMs).FirstOrDefault();
                await Kernel.ModuleProblems.ReportAsync(Context.Stores.Intel, Context.Clock, ModuleId, BackupProblem, Kernel.Modules.NoticeSeverity.Attention, new
                {
                    reason = "no_space", message = ex.Message, folder = ex.Folder, neededBytes = ex.NeededBytes, freeBytes = ex.FreeBytes, lastBackupMs = last,
                    fix = $"Free space there, lower datacontrols.backup.keep, or set {BackupFolderSetting} to a folder on a drive with room.",
                }, CancellationToken.None).ConfigureAwait(false);
                return Json(new { skipped = ex.Message, neededBytes = ex.NeededBytes, freeBytes = ex.FreeBytes, folder = ex.Folder });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or CryptographicException)
            {
                long? last = Backups().Backups.Select(b => (long?)b.CreatedAtMs).FirstOrDefault();
                await Kernel.ModuleProblems.ReportAsync(Context.Stores.Intel, Context.Clock, ModuleId, BackupProblem, Kernel.Modules.NoticeSeverity.Attention,
                    new { reason = "failed", message = ex.Message, folder = BackupRoot, lastBackupMs = last }, CancellationToken.None).ConfigureAwait(false);
                throw new JobFailureException(ErrorClass.Transient, "The backup could not be taken: " + ex.Message, ex);
            }
        }

        /// <summary>
        /// Takes a consistent snapshot of corpus.db and operator.db while the service runs (SQLite's online backup), encrypts
        /// each, and removes the plain copies. The manifest is written last: a folder without one is an interrupted backup and
        /// is cleaned up by the next run.
        /// </summary>
        /// <param name="progress">Told how far the copy is, every few seconds of work (a job's progress report).</param>
        public async Task<BackupDto> BackupAsync(string trigger, CancellationToken ct, Func<object, Task>? progress = null)
        {
            var stores = new[] { ("corpus", Context.Stores.Corpus), ("operator", Context.Stores.Operator) };
            string root = BackupRoot;
            Directory.CreateDirectory(root);
            RemoveInterrupted();
            long needed = BytesNeeded(stores.Select(s => StoreBytes(s.Item2)).ToList()), free = FreeSpace(root);
            if (free < needed) throw new BackupSpaceException(root, needed, free);

            byte[] key = LoadOrCreateKey();
            long now = Context.Clock.NowMs;
            string id = "bk" + Instant.ToUtcDateTime(now).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant();
            string dir = Path.Combine(root, id);
            Directory.CreateDirectory(dir);
            var files = new List<BackupFileDto>();
            foreach (var (store, sqlite) in stores)
            {
                ct.ThrowIfCancellationRequested();
                string plain = Path.Combine(dir, store + ".db.tmp"), encrypted = Path.Combine(dir, store + ".db.enc");
                try
                {
                    await CopyDatabaseAsync(sqlite, plain, store, progress, ct).ConfigureAwait(false);
                    var (plainBytes, encryptedBytes, sha) = await EncryptFileAsync(plain, encrypted, key, ct,
                        progress == null ? null : done => progress(new { stage = "encrypt", store, bytes = done })).ConfigureAwait(false);
                    files.Add(new BackupFileDto(store, Path.GetFileName(encrypted), plainBytes, encryptedBytes, sha));
                }
                catch
                {
                    // An unfinished encrypted copy is of no use to anyone and is as large as the database.
                    if (File.Exists(encrypted)) File.Delete(encrypted);
                    throw;
                }
                finally
                {
                    foreach (string leftover in new[] { plain, plain + "-wal", plain + "-shm", plain + "-journal" })
                        if (File.Exists(leftover)) File.Delete(leftover);
                }
            }
            var manifest = new BackupManifest(id, now, trigger, KeyId(key), files);
            await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest, KernelJson.Options), ct).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(key);
            Prune();
            await Kernel.OperatorAudit.WriteAsync(Context.Stores.Operator, Context.Clock, "datacontrols", "backup.taken", id, new { trigger, bytes = files.Sum(f => f.EncryptedBytes) }, ct).ConfigureAwait(false);
            return new BackupDto(id, now, trigger, manifest.KeyId, files);
        }

        /// <summary>
        /// SQLite's online backup, a piece at a time: a consistent copy of the database as of one moment, taken while it is in
        /// use. The read transaction held on the source for the whole copy is what makes the pieces one snapshot (without it
        /// every write to the live database between two pieces starts the copy over, and on a database that is always being
        /// written it would never end). Done in one call, as it was, the copy of a 15 GB corpus could neither say how far it
        /// was nor be stopped.
        /// </summary>
        private static async Task CopyDatabaseAsync(SqliteStore store, string target, string name, Func<object, Task>? progress, CancellationToken ct)
        {
            using var source = store.OpenRead();
            using var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString());
            copy.Open();
            // The copy is thrown away unless it completes, so it needs no journal of its own.
            copy.Exec(null, "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF;");
            using var snapshot = source.BeginTransaction(deferred: true);
            source.Scalar<long?>(snapshot, "SELECT COUNT(*) FROM sqlite_schema"); // the first read is what fixes the snapshot
            var backup = SQLitePCL.raw.sqlite3_backup_init(copy.Handle, "main", source.Handle, "main")
                ?? throw new IOException($"The copy of {name}.db could not be started: {SQLitePCL.raw.sqlite3_errmsg(copy.Handle).utf8_to_string()}");
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int rc = SQLitePCL.raw.sqlite3_backup_step(backup, CopyPagesPerStep);
                    if (rc == SQLitePCL.raw.SQLITE_DONE) break;
                    if (rc is SQLitePCL.raw.SQLITE_BUSY or SQLitePCL.raw.SQLITE_LOCKED)
                    {
                        await Task.Delay(50, ct).ConfigureAwait(false);
                        continue;
                    }
                    if (rc != SQLitePCL.raw.SQLITE_OK)
                        throw new IOException($"The copy of {name}.db stopped (SQLite result {rc}): {SQLitePCL.raw.sqlite3_errmsg(copy.Handle).utf8_to_string()}");
                    if (progress != null)
                    {
                        int pages = SQLitePCL.raw.sqlite3_backup_pagecount(backup), left = SQLitePCL.raw.sqlite3_backup_remaining(backup);
                        await progress(new { stage = "copy", store = name, pages, copied = pages - left }).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                SQLitePCL.raw.sqlite3_backup_finish(backup);
            }
        }

        private IEnumerable<(string Dir, BackupManifest? Manifest)> BackupFolders()
        {
            foreach (string root in BackupRoots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (string dir in Directory.GetDirectories(root, "bk*"))
                {
                    string manifest = Path.Combine(dir, "manifest.json");
                    BackupManifest? m = null;
                    if (File.Exists(manifest))
                    {
                        try { m = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifest), KernelJson.Options); }
                        catch (JsonException ex) { Context.LogError($"[DataControls] the manifest of backup {Path.GetFileName(dir)} cannot be read.", ex); }
                    }
                    yield return (dir, m);
                }
            }
        }

        private void Remove(IEnumerable<string> folders)
        {
            foreach (string dir in folders.ToList())
            {
                try { Directory.Delete(dir, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Context.LogError($"[DataControls] old backup {Path.GetFileName(dir)} could not be removed.", ex); }
            }
        }

        /// <summary>Removes the folders an interrupted backup left behind (no manifest, older than an hour): each can hold a
        /// copy of the corpus. Run before a backup measures the room it has, and after one is taken.</summary>
        private void RemoveInterrupted()
        {
            long now = Context.Clock.NowMs;
            Remove(BackupFolders().Where(b => b.Manifest == null && now - new DateTimeOffset(Directory.GetCreationTimeUtc(b.Dir), TimeSpan.Zero).ToUnixTimeMilliseconds() > 3_600_000L).Select(b => b.Dir));
        }

        /// <summary>Keeps the newest backups (<c>datacontrols.backup.keep</c>) and removes folders an interrupted run left behind.</summary>
        private void Prune()
        {
            int keep = Context.Settings.Get("datacontrols.backup.keep", 7);
            Remove(BackupFolders().Where(b => b.Manifest != null).OrderByDescending(b => b.Manifest!.CreatedAtMs).Skip(keep).Select(b => b.Dir));
            RemoveInterrupted();
        }

        public BackupListDto Backups()
        {
            string? keyId = null;
            if (OperatingSystem.IsWindows() && File.Exists(KeyFile))
            {
                byte[] key = ProtectedData.Unprotect(File.ReadAllBytes(KeyFile), KeyEntropy, DataProtectionScope.CurrentUser);
                keyId = KeyId(key);
                CryptographicOperations.ZeroMemory(key);
            }
            return new BackupListDto(BackupFolders().Where(b => b.Manifest != null).Select(b => b.Manifest!).OrderByDescending(m => m.CreatedAtMs)
                .Select(m => new BackupDto(m.BackupId, m.CreatedAtMs, m.Trigger, m.KeyId, m.Files)).ToList(), keyId, Context.Settings.Get("datacontrols.backup.keep", 7),
                Context.Settings.Get("datacontrols.backup.enabled", true));
        }

        public Task<long> RequestBackupAsync(CancellationToken ct) => Context.Jobs.EnqueueAsync(Job(BackupKind, "all", "on_demand", priority: 300) with { MaxAttempts = 2 }, ct);

        /// <summary>
        /// Decrypts a backup into a folder beside it and checks every file: the hash recorded when it was taken, and SQLite's
        /// own integrity check. The live databases are not touched; putting the restored files in their place is done by hand
        /// with the service stopped.
        /// </summary>
        public async Task<RestoreDto> RestoreAsync(string backupId, CancellationToken ct)
        {
            var (dir, manifest) = BackupFolders().FirstOrDefault(b => b.Manifest?.BackupId == backupId);
            if (manifest == null) throw new KeyNotFoundException($"No backup '{backupId}'.");
            byte[] key = LoadOrCreateKey();
            if (KeyId(key) != manifest.KeyId) throw new ArgumentException($"This backup was made with key {manifest.KeyId}; the key on this machine is {KeyId(key)}. Import the right key first.");
            string target = Path.Combine(dir, "restored");
            Directory.CreateDirectory(target);
            var restored = new List<string>();
            foreach (var f in manifest.Files)
            {
                string path = Path.Combine(target, f.Store + ".db");
                string sha = await DecryptFileAsync(Path.Combine(dir, f.File), path, key, ct).ConfigureAwait(false);
                if (!string.Equals(sha, f.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"The restored {f.Store} database does not match the hash recorded when the backup was taken.");
                await using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
                {
                    await c.OpenAsync(ct).ConfigureAwait(false);
                    string? check = c.Scalar<string>(null, "PRAGMA integrity_check");
                    if (check != "ok") throw new InvalidDataException($"The restored {f.Store} database fails SQLite's integrity check: {check}");
                }
                restored.Add(path);
            }
            CryptographicOperations.ZeroMemory(key);
            await Kernel.OperatorAudit.WriteAsync(Context.Stores.Operator, Context.Clock, "datacontrols", "backup.restored", backupId, new { files = restored.Count }, ct).ConfigureAwait(false);
            return new RestoreDto(backupId, target, restored, "Verified copies. To use them, stop the service, move the current database files aside, and put these in their place.");
        }

        /// <summary>
        /// The backup key wrapped with a passphrase (PBKDF2-SHA256, 600,000 rounds, then AES-256-GCM), for restoring on another
        /// machine. Whoever has this text and the passphrase can open every backup made with the key.
        /// </summary>
        public string ExportKey(string passphrase)
        {
            if (string.IsNullOrEmpty(passphrase) || passphrase.Length < 12) throw new ArgumentException("The passphrase must be at least 12 characters.");
            byte[] key = LoadOrCreateKey();
            byte[] salt = RandomNumberGenerator.GetBytes(16), nonce = RandomNumberGenerator.GetBytes(12), cipher = new byte[32], tag = new byte[16];
            byte[] wrapping = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, 600_000, HashAlgorithmName.SHA256, 32);
            using (var aes = new AesGcm(wrapping, 16)) aes.Encrypt(nonce, key, cipher, tag, Encoding.ASCII.GetBytes(KeyBlobPrefix));
            string id = KeyId(key);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(wrapping);
            return string.Join(':', KeyBlobPrefix, id, Convert.ToBase64String(salt), Convert.ToBase64String(nonce), Convert.ToBase64String(cipher), Convert.ToBase64String(tag));
        }

        /// <summary>Installs an exported key on this machine (protected by DPAPI again). Refused when this machine already has a
        /// different key, because replacing it would make the backups made here unreadable.</summary>
        public string ImportKey(string exported, string passphrase)
        {
            if (!OperatingSystem.IsWindows()) throw new ArgumentException("Backup keys are protected with Windows DPAPI and can only be installed on Windows.");
            string[] parts = (exported ?? "").Trim().Split(':');
            if (parts.Length != 6 || parts[0] != KeyBlobPrefix) throw new ArgumentException("This is not an exported Omniscience backup key.");
            byte[] key = new byte[32];
            try
            {
                byte[] wrapping = Rfc2898DeriveBytes.Pbkdf2(passphrase ?? "", Convert.FromBase64String(parts[2]), 600_000, HashAlgorithmName.SHA256, 32);
                using var aes = new AesGcm(wrapping, 16);
                aes.Decrypt(Convert.FromBase64String(parts[3]), Convert.FromBase64String(parts[4]), Convert.FromBase64String(parts[5]), key, Encoding.ASCII.GetBytes(KeyBlobPrefix));
                CryptographicOperations.ZeroMemory(wrapping);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                throw new ArgumentException("The passphrase is wrong, or the exported key is damaged.");
            }
            string id = KeyId(key);
            if (id != parts[1]) throw new ArgumentException("The exported key is damaged.");
            Directory.CreateDirectory(Context.Stores.Paths.Backups);
            if (File.Exists(KeyFile))
            {
                byte[] current = ProtectedData.Unprotect(File.ReadAllBytes(KeyFile), KeyEntropy, DataProtectionScope.CurrentUser);
                bool same = CryptographicOperations.FixedTimeEquals(current, key);
                CryptographicOperations.ZeroMemory(current);
                if (!same) throw new ArgumentException("This machine already has a different backup key. Move its key file and its backups aside before installing another key.");
            }
            else File.WriteAllBytes(KeyFile, ProtectedData.Protect(key, KeyEntropy, DataProtectionScope.CurrentUser));
            CryptographicOperations.ZeroMemory(key);
            return id;
        }
    }
}
