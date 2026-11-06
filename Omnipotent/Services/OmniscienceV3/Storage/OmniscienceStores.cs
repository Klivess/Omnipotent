using Omnipotent.Data_Handling;
using Omnipotent.Services.OmniscienceV3.Storage.Schemas;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Storage
{
    /// <summary>On-disk layout of v3 under <c>SavedData/Omniscience/v3/</c> (kept inside the legacy folder so
    /// cutover never moves data).</summary>
    public sealed class StorePaths
    {
        public StorePaths(string root) => Root = root;

        public string Root { get; }
        public string Corpus => Path.Combine(Root, "corpus.db");
        public string Intel => Path.Combine(Root, "intel.db");
        public string Operator => Path.Combine(Root, "operator.db");
        public string Vectors => Path.Combine(Root, "vectors.db");
        public string Analytics => Path.Combine(Root, "analytics.duckdb");
        /// <summary>Content-addressed media store (attachments, avatars, face crops, thumbnails).</summary>
        public string Media => Path.Combine(Root, "media");
        /// <summary>ONNX models and tokenizers managed by ModelHub.</summary>
        public string Models => Path.Combine(Root, "models");
        /// <summary>Encrypted snapshot backups (N20).</summary>
        public string Backups => Path.Combine(Root, "backups");
        /// <summary>Snapshots of the v2 database taken for the migration (deleted once imported).</summary>
        public string LegacySnapshots => Path.Combine(Root, "legacy-snapshots");
        /// <summary>The folder that contains v3: v2's data folder, where its database and media stay in place.</summary>
        public string LegacyRoot => Path.GetFullPath(Path.Combine(Root, ".."));

        public static StorePaths Default() =>
            new(Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniscienceDirectory), "v3"));
    }

    /// <summary>
    /// The four SQLite stores of v3, opened together. Ownership: corpus.db is written only by ingest
    /// (through <see cref="ChangeLog"/>), intel.db by kernel jobs and modules, operator.db by Klives's
    /// actions, vectors.db by embedding jobs. Each has exactly one writer.
    /// </summary>
    public sealed class OmniscienceStores : IAsyncDisposable
    {
        /// <summary>How long opening waits for a previous owner of the directory (an overlapping restart) to let go.</summary>
        public static TimeSpan DefaultLockWait { get; set; } = TimeSpan.FromSeconds(60);

        private readonly InstanceLock instanceLock;

        private OmniscienceStores(StorePaths paths, InstanceLock instanceLock, SqliteStore corpus, SqliteStore intel, SqliteStore op, SqliteStore vectors)
        {
            Paths = paths;
            this.instanceLock = instanceLock;
            Corpus = corpus;
            Intel = intel;
            Operator = op;
            Vectors = vectors;
        }

        public StorePaths Paths { get; }
        public SqliteStore Corpus { get; }
        public SqliteStore Intel { get; }
        public SqliteStore Operator { get; }
        public SqliteStore Vectors { get; }

        public IEnumerable<SqliteStore> All => new[] { Corpus, Intel, Operator, Vectors };

        public static async Task<OmniscienceStores> OpenAsync(StorePaths paths, IClock clock, CancellationToken ct = default, TimeSpan? lockWait = null)
        {
            Directory.CreateDirectory(paths.Root);
            Directory.CreateDirectory(paths.Media);
            Directory.CreateDirectory(paths.Models);

            // Ownership first: nothing below may touch the files while another kernel works them.
            var instanceLock = await InstanceLock.AcquireAsync(paths.Root, lockWait ?? DefaultLockWait, ct).ConfigureAwait(false);
            var corpus = new SqliteStore("corpus", paths.Corpus, clock);
            var intel = new SqliteStore("intel", paths.Intel, clock);
            var op = new SqliteStore("operator", paths.Operator, clock);
            var vectors = new SqliteStore("vectors", paths.Vectors, clock);
            var opened = new List<SqliteStore>();
            try
            {
                await corpus.InitializeAsync(CorpusSchema.Migrations, ct).ConfigureAwait(false); opened.Add(corpus);
                await intel.InitializeAsync(IntelSchema.Migrations, ct).ConfigureAwait(false); opened.Add(intel);
                await op.InitializeAsync(OperatorSchema.Migrations, ct).ConfigureAwait(false); opened.Add(op);
                await vectors.InitializeAsync(VectorSchema.Migrations, ct).ConfigureAwait(false); opened.Add(vectors);
                return new OmniscienceStores(paths, instanceLock, corpus, intel, op, vectors);
            }
            catch
            {
                foreach (var s in opened) await s.DisposeAsync().ConfigureAwait(false);
                instanceLock.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                // Reverse of the dependency order: derived stores first, the source of truth last.
                await Vectors.DisposeAsync().ConfigureAwait(false);
                await Operator.DisposeAsync().ConfigureAwait(false);
                await Intel.DisposeAsync().ConfigureAwait(false);
                await Corpus.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                // Released last, after every writer has drained.
                instanceLock.Dispose();
            }
        }
    }
}
