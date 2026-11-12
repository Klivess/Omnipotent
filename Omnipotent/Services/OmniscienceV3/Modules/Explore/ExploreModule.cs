using System.Globalization;
using System.Text.Json;
using DuckDB.NET.Data;
using Omnipotent.Services.OmniscienceV3.Kernel.Explore;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Modules.Classification;
using Omnipotent.Services.OmniscienceV3.Modules.Rollups;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Modules.Explore
{
    /// <summary>
    /// The Explore engine (plan D4): a columnar DuckDB mirror (<c>analytics.duckdb</c>) of the corpus, labels and engine
    /// datasets, maintained incrementally from the change log and classification's outbox, and the <see cref="IExploreEngine"/>
    /// that answers bounded aggregate queries over it (2 s timeout, 10K groups, its own memory limit, a result cache keyed
    /// by query and mirror version). Merges never rewrite the mirror: person dimensions resolve through a mirrored
    /// canonical map at query time. Every time bucket can be read on Klives's clock, the subject's clock (their zone for
    /// that month, UTC when unknown) or UTC; day-precision timestamps never enter hour statistics (R4).
    /// <para>
    /// Datasets come from two places: the ones this module mirrors itself (<see cref="ExploreDatasets"/>), and every
    /// dataset another module declares in its manifest and offers through <see cref="IExploreSourceProvider"/> — those are
    /// copied generically from their intel.db table, so a new module's data is filterable the day it ships.
    /// </para>
    /// </summary>
    public sealed partial class ExploreModule : IntelModuleBase, IExploreEngine, IAsyncDisposable
    {
        public const string ModuleId = "explore";
        private readonly SemaphoreSlim writeGate = new(1, 1);
        private readonly Func<IEnumerable<IIntelModule>>? otherModules;
        private DuckDBConnection? db;
        private string? path;
        private long version;
        private int disposed;

        /// <param name="otherModules">The engine's modules (asked once, at start): those that implement
        /// <see cref="IExploreSourceProvider"/> have their declared datasets mirrored. Null mirrors only this module's own.</param>
        public ExploreModule(Func<IEnumerable<IIntelModule>>? otherModules = null) => this.otherModules = otherModules;

        public override ModuleManifest Manifest { get; } = new()
        {
            Id = ModuleId,
            Version = 1,
            Title = "Explore engine",
            Category = "core",
            Description = "Mirrors messages, labels, events, relations, facts, concepts, engine activity and every dataset other modules declare into a columnar store, " +
                          "and answers filterable, drillable aggregate queries on any lens, with coverage and an explanation for every result.",
            DependsOn = new[] { RollupModule.ModuleId },
            SubscribedChangeKinds = new[]
            {
                ChangeKinds.MessageInsert, ChangeKinds.MessageVersion, ChangeKinds.MessageDelete, ChangeKinds.ReactionChange, ChangeKinds.AttachmentAdd,
                ChangeKinds.PersonMerge, ChangeKinds.PersonUnmerge, ChangeKinds.PersonErase, ChangeKinds.PresenceEvent, ChangeKinds.VoiceEvent, ChangeKinds.MemberEvent,
            },
            Schedules = new[]
            {
                new ScheduleTrigger("mirror", Every: TimeSpan.FromSeconds(30)),
                new ScheduleTrigger("engine", Every: TimeSpan.FromMinutes(5)),
            },
            DefaultLane = Lane.Cpu,
            Settings = new[]
            {
                new SettingDescriptor("explore.memory_mb", Kernel.SettingTypes.Int, "768", "DuckDB memory limit for the Explore mirror (MB).", 128, 16384),
                new SettingDescriptor("explore.threads", Kernel.SettingTypes.Int, "2", "DuckDB worker threads for Explore queries.", 1, 16),
                new SettingDescriptor("explore.timeout_ms", Kernel.SettingTypes.Int, "2000", "An Explore query that runs longer is cancelled (plan D4: 2 s).", 200, 60000),
                new SettingDescriptor("explore.whole_copy_max_rows", Kernel.SettingTypes.Int, "1000000",
                    "A module dataset without a change column is copied whole on every pass; above this many rows it is skipped and reported instead.", 1000, 50000000),
            },
            Datasets = ExploreDatasets.All,
        };

        public override async Task InitializeAsync(ModuleContext context, CancellationToken ct)
        {
            await base.InitializeAsync(context, ct).ConfigureAwait(false);
            path = context.Stores.Paths.Analytics;
            db = new DuckDBConnection($"Data Source={path}");
            db.Open();
            int memory = context.Settings.Get("explore.memory_mb", 768), threads = context.Settings.Get("explore.threads", 2);
            Exec($"SET memory_limit='{memory.ToString(CultureInfo.InvariantCulture)}MB'; SET threads={threads.ToString(CultureInfo.InvariantCulture)};");
            Exec(ExploreDatasets.Ddl[0]); // mirror_state first: it says which layout the file holds
            // A file written before layouts were numbered has cursors but no number: it is an older layout too.
            long? stored = ReadState("schema");
            if (stored == null ? ReadState("changes") != null || ReadState("version") != null : stored != ExploreDatasets.SchemaVersion) DropEverything();
            foreach (var ddl in ExploreDatasets.Ddl) Exec(ddl);
            WriteState("schema", ExploreDatasets.SchemaVersion);
            BindDatasets();
            version = ReadState("version") ?? 0;
        }

        /// <summary>Empties the mirror file (an older layout): every table goes and every cursor with it, so the next passes
        /// rebuild it from the stores. Nothing is lost: the mirror is derived data.</summary>
        private void DropEverything()
        {
            var tables = new List<string>();
            using (var cmd = Db.CreateCommand())
            {
                cmd.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_schema = 'main' AND table_type = 'BASE TABLE'";
                using var r = cmd.ExecuteReader();
                while (r.Read()) tables.Add(r.GetString(0));
            }
            // The result cache is keyed by version: it keeps counting up so an answer from the old layout is never reused.
            long keep = ReadState("version") ?? 0;
            foreach (var table in tables) Exec($"DROP TABLE \"{table.Replace("\"", "\"\"")}\"");
            Exec(ExploreDatasets.Ddl[0]);
            WriteState("version", keep + 1);
            Context.Log($"[OmniscienceV3] the Explore mirror had an older layout and is being rebuilt from the stores ({tables.Count} table(s) dropped).");
        }

        /// <summary>Closes the mirror once no job can be writing it. Safe to call more than once (the kernel and the runtime
        /// may both dispose modules that own resources).</summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            await writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                db?.Dispose();
                db = null;
            }
            finally
            {
                writeGate.Release();
                writeGate.Dispose();
            }
        }

        internal DuckDBConnection Db => db ?? throw new InvalidOperationException("The Explore mirror is not open.");

        /// <summary>
        /// A reader connection to the same database. Connections opened on one file in one process share a single database
        /// instance (the driver keeps it open while any connection is), so a reader sees every committed write of the mirror
        /// and never blocks it. <c>Duplicate()</c> is not used: the driver only supports it for in-memory databases.
        /// </summary>
        internal DuckDBConnection OpenReader()
        {
            _ = Db; // refuses once the mirror is closed
            var c = new DuckDBConnection($"Data Source={path}");
            c.Open();
            return c;
        }

        internal void Exec(string sql)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        internal long? ReadState(string key)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "SELECT value FROM mirror_state WHERE key = $k";
            cmd.Parameters.Add(new DuckDBParameter("k", key));
            var v = cmd.ExecuteScalar();
            return v == null || v is DBNull ? null : Convert.ToInt64(v, CultureInfo.InvariantCulture);
        }

        internal void WriteState(string key, long value)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO mirror_state(key, value) VALUES ($k, $v)";
            cmd.Parameters.Add(new DuckDBParameter("k", key));
            cmd.Parameters.Add(new DuckDBParameter("v", value));
            cmd.ExecuteNonQuery();
        }

        private void ClearState(string key)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = "DELETE FROM mirror_state WHERE key = $k";
            cmd.Parameters.Add(new DuckDBParameter("k", key));
            cmd.ExecuteNonQuery();
        }

        /// <summary>The mirror's version: moves on every write, keys the result cache.</summary>
        internal long Version => Interlocked.Read(ref version);

        private void Bump()
        {
            long v = Interlocked.Increment(ref version);
            WriteState("version", v);
            lock (cache) cache.Clear();
        }

        public override IEnumerable<DirtySubject> MapChanges(IReadOnlyList<ChangeRecord> changes) =>
            changes.Count == 0 ? Array.Empty<DirtySubject>() : new[] { new DirtySubject("stream", "mirror") };

        public override IEnumerable<JobRequest> Plan(PlanContext context)
        {
            var jobs = new List<JobRequest>();
            if (context.Dirty.Count > 0 || context.VersionChanged || context.DueSchedules.Any(s => s.Name == "mirror"))
                jobs.Add(Job("mirror", "stream", "mirror", priority: 100));
            if (context.VersionChanged || context.DueSchedules.Any(s => s.Name == "engine"))
                jobs.Add(Job("engine", "stream", "engine", priority: 60));
            return jobs;
        }

        public override async Task<string?> ExecuteAsync(JobContext job) => job.Job.Kind switch
        {
            "mirror" => await MirrorAsync(job).ConfigureAwait(false),
            "engine" => await MirrorEngineAsync(job).ConfigureAwait(false),
            _ => throw new JobFailureException(ErrorClass.Permanent, $"Unknown explore job kind '{job.Job.Kind}'."),
        };
    }
}
