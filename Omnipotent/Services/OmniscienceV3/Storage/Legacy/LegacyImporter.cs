using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Storage.Legacy
{
    /// <summary>Whether a run reads a one-off snapshot (the initial migration) or the live v2 file (shadow tail).</summary>
    public enum LegacyImportMode { Snapshot, Tail }

    public sealed record LegacyProgress(string Table, long Done, long Total);

    /// <summary>Counts for one v2 table in one run.</summary>
    public sealed class LegacyTableReport
    {
        /// <summary>Rows in the v2 table (counted on snapshot runs only).</summary>
        public long? SourceRows { get; set; }
        public long Read { get; set; }
        public long Imported { get; set; }
        public long Updated { get; set; }
        /// <summary>Already present in v3 (a re-run, or a row v3 already holds from another table).</summary>
        public long Skipped { get; set; }
        public long Quarantined { get; set; }
        public long ElapsedMs { get; set; }

        internal void Add(BatchCounts c, int read)
        {
            Read += read;
            Imported += c.Imported;
            Updated += c.Updated;
            Skipped += c.Skipped;
            Quarantined += c.Quarantined;
        }
    }

    /// <summary>The migration report (plan F-Storage): what was read, imported, skipped and quarantined per table,
    /// how every instant was derived, and what was found about media and secrets.</summary>
    public sealed class LegacyImportReport
    {
        public required string Mode { get; init; }
        public required string Source { get; init; }
        public int LegacyUserVersion { get; init; }
        public long StartedAtMs { get; init; }
        public long FinishedAtMs { get; set; }
        public string AssumedZone { get; init; } = "";
        public SortedDictionary<string, LegacyTableReport> Tables { get; } = new(StringComparer.Ordinal);
        /// <summary>Imported messages per <see cref="Temporal.TimeQuality"/>.</summary>
        public SortedDictionary<string, long> MessageTimeQuality { get; } = new(StringComparer.Ordinal);
        /// <summary>Discord messages whose v2 timestamp disagreed with their snowflake by more than two seconds.</summary>
        public long DiscordDriftOver2s { get; set; }
        public long MaxDiscordDriftMs { get; set; }
        /// <summary>Wall-clock rows that fell in a DST overlap (ambiguous) or gap (nonexistent) when re-resolved.</summary>
        public long WallClockAmbiguous { get; set; }
        public long WallClockInvalid { get; set; }
        /// <summary>Messages whose stored text differed from their last logged edit (an edit v2 never recorded).</summary>
        public long VersionHistoriesRepaired { get; set; }
        /// <summary>Forwards v2 had stored as replies (F361).</summary>
        public long ForwardsRecovered { get; set; }
        public long MediaFilesFound { get; set; }
        public long MediaFilesMissing { get; set; }
        public long MediaBytesReferenced { get; set; }
        public long? OrphanMediaFiles { get; set; }
        public long? OrphanMediaBytes { get; set; }
        public List<string> Notes { get; } = new();

        public LegacyTableReport Table(string name)
        {
            if (!Tables.TryGetValue(name, out var t)) Tables[name] = t = new LegacyTableReport();
            return t;
        }

        internal void CountQuality(TimeQuality q)
        {
            string key = q.ToString();
            MessageTimeQuality[key] = MessageTimeQuality.GetValueOrDefault(key) + 1;
        }
    }

    internal readonly record struct BatchCounts(long Imported, long Updated, long Skipped, long Quarantined)
    {
        public static BatchCounts operator +(BatchCounts a, BatchCounts b) =>
            new(a.Imported + b.Imported, a.Updated + b.Updated, a.Skipped + b.Skipped, a.Quarantined + b.Quarantined);
    }

    /// <summary>
    /// Migrates Omniscience v2's database into v3's stores (plan F-Storage / LegacyImporter, R8 shadow tail).
    /// <list type="bullet">
    /// <item><b>Read-only:</b> v2's file is opened read-only; the initial run reads a backup-API snapshot.</item>
    /// <item><b>Resumable:</b> append-only tables stream in rowid order in bounded batches, and each batch commits
    /// with its cursor, so a crash resumes exactly where it stopped. Small, mutable tables (people, identities,
    /// conversations, operator data) are re-read in full each run and upserted only where they changed.</item>
    /// <item><b>Idempotent:</b> every write is keyed by natural ids, so re-running is a no-op.</item>
    /// <item><b>Never aborts on a row:</b> a row that cannot be mapped is quarantined with its reason.</item>
    /// <item><b>Time:</b> see <see cref="LegacyTime"/>.</item>
    /// </list>
    /// Derived v2 data (facts, profiles, embeddings, statistics, replica dossiers) is not migrated: v3 re-derives it
    /// from the corpus. Everything Klives authored is (tiers, pins, notes, watchlists, saved queries, replica chats,
    /// review decisions, manual facts).
    /// </summary>
    public sealed partial class LegacyImporter
    {
        public const int BatchSize = 5_000;
        public const string SnapshotCompletedKey = "@snapshot_completed";

        private readonly OmniscienceStores stores;
        private readonly IClock clock;
        private readonly TimeZoneInfo assumedZone;
        private readonly Action<string> log;
        private readonly int batchSize;

        public LegacyImporter(OmniscienceStores stores, IClock clock, TimeZoneInfo assumedZone, Action<string> log, int batchSize = BatchSize)
        {
            this.stores = stores;
            this.clock = clock;
            this.assumedZone = assumedZone;
            this.log = log;
            this.batchSize = Math.Clamp(batchSize, 1, 50_000);
        }

        /// <summary>True once a snapshot import has finished; afterwards runs tail the live file.</summary>
        public bool SnapshotCompleted()
        {
            using var c = stores.Corpus.OpenRead();
            return c.Scalar<long?>(null, "SELECT last_rowid FROM legacy_import_state WHERE table_name=$t", ("$t", SnapshotCompletedKey)) is > 0;
        }

        /// <summary>Imports everything not yet imported from <paramref name="legacy"/>.</summary>
        public async Task<LegacyImportReport> RunAsync(LegacyDatabase legacy, LegacyImportMode mode, Func<LegacyProgress, Task>? progress, CancellationToken ct)
        {
            var report = new LegacyImportReport
            {
                Mode = mode.ToString(), Source = legacy.Path, LegacyUserVersion = legacy.UserVersion, StartedAtMs = clock.NowMs, AssumedZone = assumedZone.Id,
            };
            if (legacy.UserVersion is > 0 and < 10)
                report.Notes.Add($"v2 schema version {legacy.UserVersion} predates the versions this importer was tested against (10–12); columns are probed per table.");

            var ctx = new Context(mode);
            await ImportSourcesAsync(legacy, ctx, report, ct).ConfigureAwait(false);
            await ImportPersonsAsync(legacy, ctx, report, ct).ConfigureAwait(false);
            await ImportIdentitiesAsync(legacy, ctx, report, progress, ct).ConfigureAwait(false);
            await ImportConversationsAsync(legacy, ctx, report, ct).ConfigureAwait(false);
            await ImportMessagesAsync(legacy, ctx, report, progress, ct).ConfigureAwait(false);
            await ImportEditsAsync(legacy, ctx, report, progress, ct).ConfigureAwait(false);
            await ImportDeletesAsync(legacy, ctx, report, progress, ct).ConfigureAwait(false);
            await ImportAttachmentsAsync(legacy, ctx, report, progress, ct).ConfigureAwait(false);
            await ImportReactionsAsync(legacy, ctx, report, progress, ct).ConfigureAwait(false);
            await ImportPresenceAsync(legacy, ctx, report, progress, ct).ConfigureAwait(false);
            await ImportTypingAsync(legacy, ctx, report, progress, ct).ConfigureAwait(false);
            await ImportVoiceAsync(legacy, ctx, report, progress, ct).ConfigureAwait(false);
            await ImportIngestCursorsAsync(legacy, ctx, report, ct).ConfigureAwait(false);
            await ImportOperatorDataAsync(legacy, ctx, report, ct).ConfigureAwait(false);
            if (mode == LegacyImportMode.Snapshot) CountOrphanMedia(report, ct);

            report.FinishedAtMs = clock.NowMs;
            if (mode == LegacyImportMode.Snapshot)
            {
                long now = clock.NowMs;
                string json = JsonSerializer.Serialize(report, KernelJson.Options);
                await stores.Corpus.WriteAsync((c, tx) =>
                {
                    c.Exec(tx, """
                        INSERT INTO import_ledger(import_id, format, file_sha256, zone, state, counts_json, created_at_ms, committed_at_ms)
                        VALUES($id,'omniscience-v2',$key,$zone,'committed',$counts,$start,$now)
                        ON CONFLICT(import_id) DO UPDATE SET counts_json=excluded.counts_json, committed_at_ms=excluded.committed_at_ms
                        """, ("$id", $"legacy-snapshot-{report.StartedAtMs}"), ("$key", $"legacy-snapshot:{report.StartedAtMs}"),
                        ("$zone", assumedZone.Id), ("$counts", json), ("$start", report.StartedAtMs), ("$now", now));
                    SaveCursor(c, tx, SnapshotCompletedKey, now, 0, 0, now);
                }, ct).ConfigureAwait(false);
            }
            return report;
        }

        /// <summary>What a run needs across tables: id maps built from v2's small tables.</summary>
        private sealed class Context
        {
            public Context(LegacyImportMode mode) => Mode = mode;

            public LegacyImportMode Mode { get; }

            /// <summary>v2 identity id (a GUID) → v3 identity id, v2 person id, platform.</summary>
            public Dictionary<string, (string IdentityId, string PersonId, string Platform)> Identities { get; } = new(StringComparer.Ordinal);

            /// <summary>v2 person id → canonical person id (merge chains resolved).</summary>
            public Dictionary<string, string> Canonical { get; } = new(StringComparer.Ordinal);

            /// <summary>v2 conversation id → v3 conversation id, v3 guild id (or null) and platform.</summary>
            public Dictionary<string, (string ConversationId, string? GuildId, string Platform)> Conversations { get; } = new(StringComparer.Ordinal);

            /// <summary>v2 persons by id (for operator data: tiers, notes).</summary>
            public Dictionary<string, LegacyPerson> Persons { get; } = new(StringComparer.Ordinal);

            /// <summary>v3 identity id → v2 person id.</summary>
            public Dictionary<string, string> PersonByIdentity { get; } = new(StringComparer.Ordinal);

            /// <summary>"platform:userId" of every account a v2 harvest source logged in as.</summary>
            public HashSet<string> SelfIdentities { get; } = new(StringComparer.Ordinal);

            public string? CanonicalOf(string? personId) =>
                personId == null ? null : Canonical.TryGetValue(personId, out var c) ? c : personId;
        }

        // ── shared helpers ───────────────────────────────────────────────────────

        private long Cursor(string table)
        {
            using var c = stores.Corpus.OpenRead();
            return c.Scalar<long?>(null, "SELECT last_rowid FROM legacy_import_state WHERE table_name=$t", ("$t", table)) ?? 0;
        }

        private static void SaveCursor(SqliteConnection c, SqliteTransaction tx, string table, long lastRowid, long imported, long quarantined, long now) =>
            c.Exec(tx, """
                INSERT INTO legacy_import_state(table_name, last_rowid, rows_imported, rows_quarantined, updated_at_ms) VALUES($t,$r,$i,$q,$n)
                ON CONFLICT(table_name) DO UPDATE SET last_rowid=MAX(last_rowid, excluded.last_rowid),
                    rows_imported=rows_imported+excluded.rows_imported, rows_quarantined=rows_quarantined+excluded.rows_quarantined,
                    updated_at_ms=excluded.updated_at_ms
                """, ("$t", table), ("$r", lastRowid), ("$i", imported), ("$q", quarantined), ("$n", now));

        /// <summary>Records a row that could not be imported. Returns false when it was already quarantined for the same reason.</summary>
        private static bool Quarantine(SqliteConnection c, SqliteTransaction tx, string table, string rowKey, string reason, object? row, long now) =>
            c.Exec(tx, "INSERT OR IGNORE INTO quarantine(origin, reason, payload, at_ms) VALUES($o,$r,$p,$t)",
                ("$o", $"legacy:{table}:{rowKey}"), ("$r", reason), ("$p", row == null ? null : JsonSerializer.Serialize(row, KernelJson.Options)), ("$t", now)) == 1;

        private static long Change(SqliteConnection c, SqliteTransaction tx, long now, string kind, string entityId, string? personId, string? conversationId) =>
            ChangeLog.Append(c, tx, now, kind, entityId, personId, conversationId, SourceKinds.Legacy);

        /// <summary>
        /// Streams an append-only v2 table in rowid order: read a batch (outside the writer), prepare it, write it with
        /// its cursor in one transaction, repeat. <paramref name="prepare"/> runs on the importer's thread and may read
        /// v2 or the disk; <paramref name="write"/> runs inside the corpus writer and must only touch the database.
        /// </summary>
        private async Task StreamAsync<TRow, TPrepared>(LegacyDatabase legacy, string table, string columnsSql, Func<SqliteDataReader, TRow> read,
            Func<IReadOnlyList<(long Rowid, TRow Row)>, TPrepared> prepare,
            Func<SqliteConnection, SqliteTransaction, TPrepared, long, BatchCounts> write,
            LegacyImportReport report, Func<LegacyProgress, Task>? progress, CancellationToken ct, int batchMultiplier = 1)
        {
            if (!legacy.HasTable(table))
            {
                report.Notes.Add($"{table}: not in this v2 database (schema v{legacy.UserVersion}); nothing to import.");
                return;
            }
            int batchSize = Math.Min(this.batchSize * Math.Max(1, batchMultiplier), 50_000);
            var tr = report.Table(table);
            var timer = Stopwatch.StartNew();
            if (report.Mode == nameof(LegacyImportMode.Snapshot)) tr.SourceRows = legacy.Count(table);
            long cursor = Cursor(table);
            long total = legacy.MaxRowid(table);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var batch = legacy.Batch(table, columnsSql, cursor, batchSize, read);
                if (batch.Rows.Count == 0) break;
                var prepared = prepare(batch.Rows);
                long last = batch.LastRowid;
                long now = clock.NowMs;
                var counts = await stores.Corpus.WriteAsync((c, tx) =>
                {
                    var bc = write(c, tx, prepared, now);
                    SaveCursor(c, tx, table, last, bc.Imported + bc.Updated, bc.Quarantined, now);
                    return bc;
                }, ct).ConfigureAwait(false);
                tr.Add(counts, batch.Rows.Count);
                cursor = last;
                if (progress != null) await progress(new LegacyProgress(table, cursor, total)).ConfigureAwait(false);
                if (batch.Rows.Count < batchSize) break;
            }
            tr.ElapsedMs += timer.ElapsedMilliseconds;
            if (report.Mode == nameof(LegacyImportMode.Snapshot))
                log($"[OmniscienceV3] v2 import {table}: {tr.Imported} imported, {tr.Updated} updated, {tr.Skipped} already present, {tr.Quarantined} quarantined in {tr.ElapsedMs} ms.");
        }

        /// <summary>Reads a small v2 table in full (people, identities, conversations, operator data).</summary>
        private static List<(long Rowid, T Row)> ReadAll<T>(LegacyDatabase legacy, string table, string columnsSql, Func<SqliteDataReader, T> read) =>
            legacy.HasTable(table)
                ? legacy.Connection.Query(null, $"SELECT rowid, {columnsSql} FROM {table} ORDER BY rowid", r => (r.GetInt64(0), read(r)))
                : new List<(long, T)>();

        private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private static long Ms(object? raw, long fallback) =>
            LegacyTime.TryNormalize(raw, out long ms, out _) ? ms : fallback;

        private static long? MsOrNull(object? raw) =>
            LegacyTime.TryNormalize(raw, out long ms, out _) ? ms : null;
    }
}
