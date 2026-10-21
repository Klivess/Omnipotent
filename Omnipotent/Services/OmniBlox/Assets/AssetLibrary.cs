using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Omnipotent.Services.OmniBlox.Rbx;
using Omnipotent.Services.OmniBlox.Roblox;

namespace Omnipotent.Services.OmniBlox.Assets
{
    public static class AssetStatus
    {
        /// <summary>Found by search; metadata only.</summary>
        public const string Discovered = "discovered";
        /// <summary>Official thumbnail fetched; still not downloaded (no API key yet, or queued).</summary>
        public const string Previewed = "previewed";
        /// <summary>Downloaded, cleaned, measured and rendered.</summary>
        public const string Analyzed = "analyzed";
        /// <summary>Described by the vision model: ready for the builder.</summary>
        public const string Labeled = "labeled";
        public const string Rejected = "rejected";
        public const string Failed = "failed";
    }

    /// <summary>What the vision model says about an asset.</summary>
    public sealed class AssetLabel
    {
        public string What { get; set; } = "";
        public string Category { get; set; } = "";
        public string Subcategory { get; set; } = "";
        public string Style { get; set; } = "";
        public List<string> Themes { get; set; } = new();
        public string Description { get; set; } = "";
        public List<string> GoodFor { get; set; } = new();
        public List<string> PairsWith { get; set; } = new();
        /// <summary>Degrees to add to a placement's yaw so the asset's real front faces −Z (0, 90, 180 or 270).</summary>
        public float FrontYaw { get; set; }
        public bool Walkable { get; set; }
        public double Quality { get; set; }
        public List<string> Issues { get; set; } = new();
        public string Model { get; set; } = "";
    }

    public sealed class AssetRecord
    {
        public long AssetId { get; set; }
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string CreatorName { get; set; } = "";
        public bool CreatorVerified { get; set; }
        public string CategoryPath { get; set; } = "";
        public long UpVotes { get; set; }
        public long DownVotes { get; set; }
        public int UpVotePercent { get; set; }
        public bool HasScripts { get; set; }
        public int ScriptCount { get; set; }
        public long Triangles { get; set; }
        public int MeshParts { get; set; }
        public string Status { get; set; } = AssetStatus.Discovered;
        public string? StatusReason { get; set; }
        public int Attempts { get; set; }
        public bool Placeable { get; set; }
        public AssetMeasurements? Measurements { get; set; }
        public List<string> ScriptFlags { get; set; } = new();
        public int ScriptsRemoved { get; set; }
        public AssetLabel? Label { get; set; }
        public bool HasThumbnail { get; set; }
        public List<string> Renders { get; set; } = new();
        /// <summary>The labeller's quality-scale version the label was made on (0: before calibrated anchors).</summary>
        public int LabelVersion { get; set; }
        public int TimesUsed { get; set; }
        public List<string> UsageNotes { get; set; } = new();
        public string DiscoveredQuery { get; set; } = "";
        public int Priority { get; set; }
        public DateTime DiscoveredUtc { get; set; }
        public DateTime? AnalyzedUtc { get; set; }
        public DateTime? LabeledUtc { get; set; }

        /// <summary>One line for a tool result: what it is, how big, where it came from.</summary>
        public string Summary()
        {
            var sb = new StringBuilder();
            sb.Append($"#{AssetId} \"{Name}\"");
            if (Label != null && Label.What.Length > 0) sb.Append($" — {Label.What}");
            if (Measurements is { } m) sb.Append(FormattableString.Invariant($" · {m.SizeX:0.#}×{m.SizeY:0.#}×{m.SizeZ:0.#} studs"));
            if (Label != null && Label.Style.Length > 0) sb.Append($" · {Label.Style}");
            if (Measurements?.Colors.Count > 0) sb.Append(" · " + string.Join("/", Measurements.Colors.Take(2).Select(c => c.Name)));
            if (Label != null && Label.Quality > 0) sb.Append(FormattableString.Invariant($" · quality {Label.Quality:0.#}/10"));
            if (Status != AssetStatus.Labeled) sb.Append($" · [{Status}]");
            if (TimesUsed > 0) sb.Append($" · used {TimesUsed}×");
            return sb.ToString();
        }
    }

    public sealed class AssetSearchQuery
    {
        public string Text { get; set; } = "";
        public string? Category { get; set; }
        public string? Style { get; set; }
        public float? MinHeight { get; set; }
        public float? MaxHeight { get; set; }
        public float? MaxFootprint { get; set; }
        public bool PlaceableOnly { get; set; } = true;
        public int Limit { get; set; } = 10;
    }

    public sealed record CategoryCount(string Category, long Count);

    public sealed class LibraryStats
    {
        public Dictionary<string, long> ByStatus { get; set; } = new();
        public long Total { get; set; }
        public long Placeable { get; set; }
        public List<CategoryCount> TopCategories { get; set; } = new();
    }

    /// <summary>
    /// The labelled shelf of free models: SQLite rows (metadata, measurements, label) with an FTS5 index
    /// over everything a builder might type, plus per-asset files (the original download, the cleaned
    /// model as XML, Roblox's thumbnail and the schematic renders).
    /// </summary>
    public sealed class AssetLibrary : IDisposable
    {
        private readonly string directory;
        private readonly object sync = new();
        private SqliteConnection? db;

        private static readonly JsonSerializerSettings Json = new() { NullValueHandling = NullValueHandling.Ignore };

        public AssetLibrary(string directory) => this.directory = Path.GetFullPath(directory);

        public string FilesDirectory(long assetId) => Path.Combine(directory, "files", assetId.ToString(CultureInfo.InvariantCulture));
        public string ThumbnailPath(long assetId) => Path.Combine(FilesDirectory(assetId), "thumb.png");
        public string RenderPath(long assetId, string view) => Path.Combine(FilesDirectory(assetId), $"render_{view}.png");
        public string SourcePath(long assetId) => Path.Combine(FilesDirectory(assetId), "source.rbxm");
        public string CleanModelPath(long assetId) => Path.Combine(FilesDirectory(assetId), "clean.rbxmx");

        public void Open()
        {
            Directory.CreateDirectory(directory);
            var conn = new SqliteConnection($"Data Source={Path.Combine(directory, "library.db")};Cache=Shared");
            conn.Open();
            Exec(conn, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;");
            Exec(conn, @"
CREATE TABLE IF NOT EXISTS assets (
  asset_id INTEGER PRIMARY KEY,
  name TEXT NOT NULL DEFAULT '',
  description TEXT NOT NULL DEFAULT '',
  creator_name TEXT NOT NULL DEFAULT '',
  creator_verified INTEGER NOT NULL DEFAULT 0,
  category_path TEXT NOT NULL DEFAULT '',
  up_votes INTEGER NOT NULL DEFAULT 0,
  down_votes INTEGER NOT NULL DEFAULT 0,
  up_percent INTEGER NOT NULL DEFAULT 0,
  has_scripts INTEGER NOT NULL DEFAULT 0,
  script_count INTEGER NOT NULL DEFAULT 0,
  triangles INTEGER NOT NULL DEFAULT 0,
  mesh_parts INTEGER NOT NULL DEFAULT 0,
  status TEXT NOT NULL DEFAULT 'discovered',
  status_reason TEXT,
  attempts INTEGER NOT NULL DEFAULT 0,
  placeable INTEGER NOT NULL DEFAULT 0,
  size_x REAL, size_y REAL, size_z REAL,
  measurements TEXT,
  script_flags TEXT,
  scripts_removed INTEGER NOT NULL DEFAULT 0,
  label TEXT,
  label_version INTEGER NOT NULL DEFAULT 0,
  category TEXT NOT NULL DEFAULT '',
  style TEXT NOT NULL DEFAULT '',
  quality REAL NOT NULL DEFAULT 0,
  has_thumbnail INTEGER NOT NULL DEFAULT 0,
  renders TEXT,
  times_used INTEGER NOT NULL DEFAULT 0,
  usage_notes TEXT,
  discovered_query TEXT NOT NULL DEFAULT '',
  priority INTEGER NOT NULL DEFAULT 0,
  discovered_utc TEXT NOT NULL,
  analyzed_utc TEXT,
  labeled_utc TEXT
);
CREATE INDEX IF NOT EXISTS ix_assets_status ON assets(status, priority DESC, up_votes DESC);
CREATE VIRTUAL TABLE IF NOT EXISTS assets_fts USING fts5(asset_id UNINDEXED, name, what, body, tokenize='porter unicode61');
");
            // Libraries created before labels were versioned: their labels are on the old, uncalibrated scale (0).
            if (!HasColumn(conn, "assets", "label_version"))
                Exec(conn, "ALTER TABLE assets ADD COLUMN label_version INTEGER NOT NULL DEFAULT 0");
            lock (sync) db = conn;
        }

        private static bool HasColumn(SqliteConnection conn, string table, string column)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table})";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public void Dispose()
        {
            lock (sync)
            {
                db?.Dispose();
                db = null;
            }
        }

        private SqliteConnection Db => db ?? throw new InvalidOperationException("The asset library is not open.");

        private static void Exec(SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        // ─────────────── Discovery ───────────────

        /// <summary>Adds newly found assets and refreshes votes on known ones. Returns how many were new.</summary>
        public int UpsertDiscovered(IEnumerable<CreatorStoreAsset> found, string query, int priority, DateTime nowUtc)
        {
            int added = 0;
            lock (sync)
            {
                using var tx = Db.BeginTransaction();
                foreach (var a in found)
                {
                    if (!a.IsFree || a.AssetTypeId != 10) continue; // free Models only
                    using var cmd = Db.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
INSERT INTO assets (asset_id, name, description, creator_name, creator_verified, category_path, up_votes, down_votes, up_percent,
  has_scripts, script_count, triangles, mesh_parts, discovered_query, priority, discovered_utc)
VALUES ($id, $name, $desc, $creator, $verified, $cat, $up, $down, $pct, $hs, $sc, $tri, $mp, $q, $prio, $now)
ON CONFLICT(asset_id) DO UPDATE SET up_votes = excluded.up_votes, down_votes = excluded.down_votes, up_percent = excluded.up_percent,
  priority = MAX(assets.priority, excluded.priority);";
                    cmd.Parameters.AddWithValue("$id", a.AssetId);
                    cmd.Parameters.AddWithValue("$name", Clip(a.Name, 200));
                    cmd.Parameters.AddWithValue("$desc", Clip(a.Description, 1000));
                    cmd.Parameters.AddWithValue("$creator", Clip(a.CreatorName, 100));
                    cmd.Parameters.AddWithValue("$verified", a.CreatorVerified ? 1 : 0);
                    cmd.Parameters.AddWithValue("$cat", a.CategoryPath);
                    cmd.Parameters.AddWithValue("$up", a.UpVotes);
                    cmd.Parameters.AddWithValue("$down", a.DownVotes);
                    cmd.Parameters.AddWithValue("$pct", a.UpVotePercent);
                    cmd.Parameters.AddWithValue("$hs", a.HasScripts ? 1 : 0);
                    cmd.Parameters.AddWithValue("$sc", a.ScriptCount);
                    cmd.Parameters.AddWithValue("$tri", a.Triangles);
                    cmd.Parameters.AddWithValue("$mp", a.MeshPartCount);
                    cmd.Parameters.AddWithValue("$q", Clip(query, 120));
                    cmd.Parameters.AddWithValue("$prio", priority);
                    cmd.Parameters.AddWithValue("$now", Iso(nowUtc));
                    bool isNew = !Exists(a.AssetId, tx);
                    cmd.ExecuteNonQuery();
                    if (isNew)
                    {
                        added++;
                        IndexText(a.AssetId, a.Name, "", a.Description + " " + a.CategoryPath.Replace("__", " ").Replace('-', ' '), tx);
                    }
                }
                tx.Commit();
            }
            return added;
        }

        private bool Exists(long assetId, SqliteTransaction? tx = null)
        {
            using var cmd = Db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT 1 FROM assets WHERE asset_id = $id";
            cmd.Parameters.AddWithValue("$id", assetId);
            return cmd.ExecuteScalar() != null;
        }

        /// <summary>Assets that still need Roblox's thumbnail (any status before analysis).</summary>
        public List<long> NeedingThumbnails(int limit)
        {
            lock (sync)
            {
                using var cmd = Db.CreateCommand();
                cmd.CommandText = "SELECT asset_id FROM assets WHERE has_thumbnail = 0 AND status IN ('discovered','previewed','analyzed','labeled') ORDER BY priority DESC, up_votes DESC LIMIT $n";
                cmd.Parameters.AddWithValue("$n", limit);
                return ReadIds(cmd);
            }
        }

        public void SaveThumbnail(long assetId, byte[] png)
        {
            string path = ThumbnailPath(assetId);
            Core.OmniBloxStore.WriteAtomic(path, png);
            lock (sync)
            {
                using var cmd = Db.CreateCommand();
                cmd.CommandText = "UPDATE assets SET has_thumbnail = 1, status = CASE WHEN status = 'discovered' THEN 'previewed' ELSE status END WHERE asset_id = $id";
                cmd.Parameters.AddWithValue("$id", assetId);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// The next downloads, best first: priority (what an upcoming game needs), then models that look
        /// cheap and clean (no scripts, verified creator, well liked, modest triangle count).
        /// </summary>
        public List<AssetRecord> NextForDownload(int limit)
        {
            lock (sync)
            {
                using var cmd = Db.CreateCommand();
                cmd.CommandText = @"
SELECT * FROM assets
WHERE status IN ('discovered','previewed') AND attempts < 3 AND triangles <= 120000 AND script_count <= 12
ORDER BY priority DESC,
  (CASE WHEN has_scripts = 0 THEN 2 ELSE 0 END) + creator_verified + (CASE WHEN up_percent >= 80 AND up_votes >= 5 THEN 1 ELSE 0 END) DESC,
  up_votes DESC
LIMIT $n";
                cmd.Parameters.AddWithValue("$n", limit);
                return ReadRecords(cmd);
            }
        }

        public List<AssetRecord> NextForLabel(int limit)
        {
            lock (sync)
            {
                using var cmd = Db.CreateCommand();
                cmd.CommandText = "SELECT * FROM assets WHERE status = 'analyzed' AND placeable = 1 ORDER BY priority DESC, up_votes DESC LIMIT $n";
                cmd.Parameters.AddWithValue("$n", limit);
                return ReadRecords(cmd);
            }
        }

        /// <summary>
        /// Labelled models whose label was made on an older quality scale, the most used first: the
        /// labeller re-rates them when it has nothing new to do.
        /// </summary>
        public List<AssetRecord> NextForRelabel(int limit, int scaleVersion)
        {
            if (limit <= 0) return new List<AssetRecord>();
            lock (sync)
            {
                using var cmd = Db.CreateCommand();
                cmd.CommandText = "SELECT * FROM assets WHERE status = 'labeled' AND label_version < $v AND has_thumbnail = 1 AND renders IS NOT NULL " +
                                  "ORDER BY times_used DESC, priority DESC, up_votes DESC LIMIT $n";
                cmd.Parameters.AddWithValue("$v", scaleVersion);
                cmd.Parameters.AddWithValue("$n", limit);
                return ReadRecords(cmd);
            }
        }

        /// <summary>
        /// Labelled models with a thumbnail and renders (a low label also survives rejection), from both ends
        /// of their stored quality AND of their popularity, since stored scores may all sit in the middle:
        /// what the labeller's calibration pool and reference examples are picked from.
        /// </summary>
        public List<AssetRecord> ReferenceCandidates(int each = 30)
        {
            lock (sync)
            {
                var found = new List<AssetRecord>();
                foreach (string order in new[] { "quality DESC", "quality ASC", "creator_verified DESC, up_percent DESC", "up_percent ASC" })
                {
                    using var cmd = Db.CreateCommand();
                    cmd.CommandText = "SELECT * FROM assets WHERE label IS NOT NULL AND has_thumbnail = 1 AND renders IS NOT NULL AND status IN ('labeled','rejected') " +
                                      $"ORDER BY {order}, up_votes DESC, asset_id LIMIT $n";
                    cmd.Parameters.AddWithValue("$n", each);
                    foreach (var record in ReadRecords(cmd))
                        if (!found.Any(r => r.AssetId == record.AssetId)) found.Add(record);
                }
                return found;
            }
        }

        // ─────────────── Analysis and labels ───────────────

        public void SaveAnalysis(long assetId, byte[] source, AssetAnalysis analysis, IReadOnlyList<(string View, byte[] Png)> renders, long triangles, DateTime nowUtc)
        {
            Directory.CreateDirectory(FilesDirectory(assetId));
            Core.OmniBloxStore.WriteAtomic(SourcePath(assetId), source);
            var cleanDoc = new RbxDocument();
            cleanDoc.AddRoot(analysis.Model);
            Core.OmniBloxStore.WriteAtomic(CleanModelPath(assetId), RbxXmlWriter.WriteBytes(cleanDoc));
            foreach (var (view, png) in renders) Core.OmniBloxStore.WriteAtomic(RenderPath(assetId, view), png);

            var m = analysis.Measurements;
            m.PerformanceCost = AssetAnalyzer.PerformanceCost(m.PartCount, triangles);
            string status = analysis.Placeable ? AssetStatus.Analyzed : AssetStatus.Rejected;
            string? reason = analysis.Placeable ? null : string.Join("; ", analysis.Problems);
            lock (sync)
            {
                using var cmd = Db.CreateCommand();
                cmd.CommandText = @"
UPDATE assets SET status = $status, status_reason = $reason, placeable = $placeable, size_x = $sx, size_y = $sy, size_z = $sz,
  measurements = $m, script_flags = $flags, scripts_removed = $removed, renders = $renders, analyzed_utc = $now
WHERE asset_id = $id";
                cmd.Parameters.AddWithValue("$status", status);
                cmd.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$placeable", analysis.Placeable ? 1 : 0);
                cmd.Parameters.AddWithValue("$sx", m.SizeX);
                cmd.Parameters.AddWithValue("$sy", m.SizeY);
                cmd.Parameters.AddWithValue("$sz", m.SizeZ);
                cmd.Parameters.AddWithValue("$m", JsonConvert.SerializeObject(m, Json));
                cmd.Parameters.AddWithValue("$flags", JsonConvert.SerializeObject(analysis.RemovedScripts.SelectMany(s => s.Flags).Distinct().ToList()));
                cmd.Parameters.AddWithValue("$removed", analysis.RemovedScripts.Count);
                cmd.Parameters.AddWithValue("$renders", JsonConvert.SerializeObject(renders.Select(r => r.View).ToList()));
                cmd.Parameters.AddWithValue("$now", Iso(nowUtc));
                cmd.Parameters.AddWithValue("$id", assetId);
                cmd.ExecuteNonQuery();
            }
        }

        /// <param name="scaleVersion">The labeller's quality-scale version (AssetLabeler.ScaleVersion) the label was made on.</param>
        public void SaveLabel(long assetId, AssetLabel label, DateTime nowUtc, int scaleVersion)
        {
            lock (sync)
            {
                using var tx = Db.BeginTransaction();
                using (var cmd = Db.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "UPDATE assets SET status = 'labeled', label = $label, label_version = $v, category = $cat, style = $style, quality = $q, labeled_utc = $now WHERE asset_id = $id";
                    cmd.Parameters.AddWithValue("$label", JsonConvert.SerializeObject(label, Json));
                    cmd.Parameters.AddWithValue("$v", scaleVersion);
                    cmd.Parameters.AddWithValue("$cat", label.Category.ToLowerInvariant());
                    cmd.Parameters.AddWithValue("$style", label.Style.ToLowerInvariant());
                    cmd.Parameters.AddWithValue("$q", label.Quality);
                    cmd.Parameters.AddWithValue("$now", Iso(nowUtc));
                    cmd.Parameters.AddWithValue("$id", assetId);
                    cmd.ExecuteNonQuery();
                }
                var record = GetLocked(assetId, tx);
                if (record != null)
                {
                    string colours = string.Join(" ", record.Measurements?.Colors.Select(c => c.Name) ?? Enumerable.Empty<string>());
                    string materials = string.Join(" ", record.Measurements?.Materials.Select(c => c.Material) ?? Enumerable.Empty<string>());
                    string body = string.Join(" ", new[]
                    {
                        label.Description, label.Category, label.Subcategory, label.Style, string.Join(" ", label.Themes),
                        string.Join(" ", label.GoodFor), string.Join(" ", label.PairsWith), colours, materials, record.Description,
                        record.CategoryPath.Replace("__", " ").Replace('-', ' '), record.Measurements?.Proportions ?? "",
                    });
                    IndexText(assetId, record.Name, label.What, body, tx);
                }
                tx.Commit();
            }
        }

        public void MarkRejected(long assetId, string reason) => SetStatus(assetId, AssetStatus.Rejected, reason, incrementAttempts: false);

        public void MarkFailed(long assetId, string reason)
        {
            lock (sync)
            {
                using var cmd = Db.CreateCommand();
                cmd.CommandText = "UPDATE assets SET attempts = attempts + 1, status_reason = $r, status = CASE WHEN attempts + 1 >= 3 THEN 'failed' ELSE status END WHERE asset_id = $id";
                cmd.Parameters.AddWithValue("$r", Clip(reason, 400));
                cmd.Parameters.AddWithValue("$id", assetId);
                cmd.ExecuteNonQuery();
            }
        }

        private void SetStatus(long assetId, string status, string? reason, bool incrementAttempts)
        {
            lock (sync)
            {
                using var cmd = Db.CreateCommand();
                cmd.CommandText = "UPDATE assets SET status = $s, status_reason = $r" + (incrementAttempts ? ", attempts = attempts + 1" : "") + " WHERE asset_id = $id";
                cmd.Parameters.AddWithValue("$s", status);
                cmd.Parameters.AddWithValue("$r", (object?)reason ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$id", assetId);
                cmd.ExecuteNonQuery();
            }
        }

        public void RecordUse(long assetId, string gameId, string? note)
        {
            lock (sync)
            {
                var rec = GetLocked(assetId, null);
                if (rec == null) return;
                var notes = rec.UsageNotes;
                notes.Add($"{gameId}: {note ?? "placed"}");
                if (notes.Count > 20) notes.RemoveRange(0, notes.Count - 20);
                using var cmd = Db.CreateCommand();
                cmd.CommandText = "UPDATE assets SET times_used = times_used + 1, usage_notes = $n WHERE asset_id = $id";
                cmd.Parameters.AddWithValue("$n", JsonConvert.SerializeObject(notes));
                cmd.Parameters.AddWithValue("$id", assetId);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>Raises the download priority of assets that match what an upcoming game needs.</summary>
        public int Prioritize(string text, int priority)
        {
            string match = FtsQuery(text);
            if (match.Length == 0) return 0;
            lock (sync)
            {
                using var cmd = Db.CreateCommand();
                cmd.CommandText = "UPDATE assets SET priority = MAX(priority, $p) WHERE asset_id IN (SELECT asset_id FROM assets_fts WHERE assets_fts MATCH $q LIMIT 200)";
                cmd.Parameters.AddWithValue("$p", priority);
                cmd.Parameters.AddWithValue("$q", match);
                return cmd.ExecuteNonQuery();
            }
        }

        // ─────────────── Reads ───────────────

        public AssetRecord? Get(long assetId)
        {
            lock (sync) return GetLocked(assetId, null);
        }

        private AssetRecord? GetLocked(long assetId, SqliteTransaction? tx)
        {
            using var cmd = Db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT * FROM assets WHERE asset_id = $id";
            cmd.Parameters.AddWithValue("$id", assetId);
            return ReadRecords(cmd).FirstOrDefault();
        }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<long, (DateTime Stamp, byte[] Bytes)> modelBytes = new();

        /// <summary>
        /// The cleaned model, ready to place (scripts and unsafe instances already removed). A fresh copy every call
        /// (the compiler moves its parts); the file's bytes are kept in memory, since a forest of 80 trees is 80
        /// loads of the same model.
        /// </summary>
        public RbxInstance? LoadModel(long assetId)
        {
            string path = CleanModelPath(assetId);
            if (!File.Exists(path)) return null;
            var stamp = File.GetLastWriteTimeUtc(path);
            if (!modelBytes.TryGetValue(assetId, out var cached) || cached.Stamp != stamp)
            {
                cached = (stamp, File.ReadAllBytes(path));
                if (modelBytes.Count > 600) modelBytes.Clear();
                modelBytes[assetId] = cached;
            }
            var doc = RbxFile.Read(cached.Bytes);
            return doc.Roots.FirstOrDefault();
        }

        public List<AssetRecord> Search(AssetSearchQuery q)
        {
            string match = FtsQuery(q.Text);
            var where = new List<string>();
            if (q.PlaceableOnly) where.Add("a.placeable = 1 AND a.status IN ('analyzed','labeled')");
            if (!string.IsNullOrWhiteSpace(q.Category)) where.Add("a.category = $cat");
            if (!string.IsNullOrWhiteSpace(q.Style)) where.Add("a.style LIKE $style");
            if (q.MinHeight is float minH) where.Add("a.size_y >= $minh");
            if (q.MaxHeight is float maxH) where.Add("a.size_y <= $maxh");
            if (q.MaxFootprint is float maxF) where.Add("MAX(a.size_x, a.size_z) <= $maxf");
            string filter = where.Count > 0 ? " AND " + string.Join(" AND ", where) : "";
            string sql = match.Length > 0
                ? $@"SELECT a.*, bm25(assets_fts, 0, 8.0, 10.0, 2.0) AS rank FROM assets_fts JOIN assets a ON a.asset_id = assets_fts.asset_id
                     WHERE assets_fts MATCH $match{filter}
                     ORDER BY rank - (a.quality * 0.35) - (CASE WHEN a.status = 'labeled' THEN 1.5 ELSE 0 END) - MIN(a.times_used, 5) * 0.2 LIMIT $n"
                : $@"SELECT a.* FROM assets a WHERE 1=1{filter} ORDER BY a.quality DESC, a.up_votes DESC LIMIT $n";
            lock (sync)
            {
                using var cmd = Db.CreateCommand();
                cmd.CommandText = sql;
                if (match.Length > 0) cmd.Parameters.AddWithValue("$match", match);
                if (!string.IsNullOrWhiteSpace(q.Category)) cmd.Parameters.AddWithValue("$cat", q.Category.Trim().ToLowerInvariant());
                if (!string.IsNullOrWhiteSpace(q.Style)) cmd.Parameters.AddWithValue("$style", "%" + q.Style.Trim().ToLowerInvariant() + "%");
                if (q.MinHeight is float a) cmd.Parameters.AddWithValue("$minh", a);
                if (q.MaxHeight is float b) cmd.Parameters.AddWithValue("$maxh", b);
                if (q.MaxFootprint is float c) cmd.Parameters.AddWithValue("$maxf", c);
                cmd.Parameters.AddWithValue("$n", Math.Clamp(q.Limit, 1, 50));
                return ReadRecords(cmd);
            }
        }

        public List<AssetRecord> Recent(string? status, int limit, int offset = 0)
        {
            lock (sync)
            {
                using var cmd = Db.CreateCommand();
                cmd.CommandText = "SELECT * FROM assets" + (status == null ? "" : " WHERE status = $s") + " ORDER BY COALESCE(labeled_utc, analyzed_utc, discovered_utc) DESC LIMIT $n OFFSET $o";
                if (status != null) cmd.Parameters.AddWithValue("$s", status);
                cmd.Parameters.AddWithValue("$n", Math.Clamp(limit, 1, 200));
                cmd.Parameters.AddWithValue("$o", Math.Max(0, offset));
                return ReadRecords(cmd);
            }
        }

        /// <summary>How many usable assets exist for a theme — the research loop's feasibility signal.</summary>
        public (long Labeled, long Analyzed, long Discovered) Coverage(string text)
        {
            string match = FtsQuery(text);
            if (match.Length == 0) return (0, 0, 0);
            lock (sync)
            {
                using var cmd = Db.CreateCommand();
                cmd.CommandText = @"SELECT
  SUM(CASE WHEN a.status = 'labeled' THEN 1 ELSE 0 END),
  SUM(CASE WHEN a.status = 'analyzed' AND a.placeable = 1 THEN 1 ELSE 0 END),
  COUNT(*)
FROM assets_fts JOIN assets a ON a.asset_id = assets_fts.asset_id WHERE assets_fts MATCH $q";
                cmd.Parameters.AddWithValue("$q", match);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return (0, 0, 0);
                return (r.IsDBNull(0) ? 0 : r.GetInt64(0), r.IsDBNull(1) ? 0 : r.GetInt64(1), r.IsDBNull(2) ? 0 : r.GetInt64(2));
            }
        }

        public LibraryStats Stats()
        {
            var stats = new LibraryStats();
            lock (sync)
            {
                using (var cmd = Db.CreateCommand())
                {
                    cmd.CommandText = "SELECT status, COUNT(*) FROM assets GROUP BY status";
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) stats.ByStatus[r.GetString(0)] = r.GetInt64(1);
                }
                using (var cmd = Db.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*), SUM(placeable) FROM assets";
                    using var r = cmd.ExecuteReader();
                    if (r.Read())
                    {
                        stats.Total = r.GetInt64(0);
                        stats.Placeable = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                    }
                }
                using (var cmd = Db.CreateCommand())
                {
                    cmd.CommandText = "SELECT category, COUNT(*) FROM assets WHERE status = 'labeled' AND category <> '' GROUP BY category ORDER BY COUNT(*) DESC LIMIT 12";
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) stats.TopCategories.Add(new CategoryCount(r.GetString(0), r.GetInt64(1)));
                }
            }
            return stats;
        }

        // ─────────────── helpers ───────────────

        private void IndexText(long assetId, string name, string what, string body, SqliteTransaction? tx)
        {
            using (var del = Db.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM assets_fts WHERE asset_id = $id";
                del.Parameters.AddWithValue("$id", assetId);
                del.ExecuteNonQuery();
            }
            using var ins = Db.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO assets_fts (asset_id, name, what, body) VALUES ($id, $name, $what, $body)";
            ins.Parameters.AddWithValue("$id", assetId);
            ins.Parameters.AddWithValue("$name", name);
            ins.Parameters.AddWithValue("$what", what);
            ins.Parameters.AddWithValue("$body", Clip(body, 4000));
            ins.ExecuteNonQuery();
        }

        /// <summary>Free text → an FTS5 query: every word as a prefix term, any of them may match (bm25 ranks).</summary>
        internal static string FtsQuery(string text)
        {
            var words = new List<string>();
            var current = new StringBuilder();
            foreach (char ch in (text ?? "").ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch)) current.Append(ch);
                else if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); }
            }
            if (current.Length > 0) words.Add(current.ToString());
            var stop = new HashSet<string> { "a", "an", "the", "of", "and", "or", "with", "for", "to", "in", "on", "about", "studs", "stud", "tall", "big", "small" };
            var terms = words.Where(w => w.Length > 1 && !stop.Contains(w)).Distinct().Take(12).Select(w => $"\"{w}\"*").ToList();
            return string.Join(" OR ", terms);
        }

        private static List<long> ReadIds(SqliteCommand cmd)
        {
            var ids = new List<long>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
            return ids;
        }

        private static List<AssetRecord> ReadRecords(SqliteCommand cmd)
        {
            var list = new List<AssetRecord>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string? S(string col) { int i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : r.GetString(i); }
                long L(string col) { int i = r.GetOrdinal(col); return r.IsDBNull(i) ? 0 : r.GetInt64(i); }
                var rec = new AssetRecord
                {
                    AssetId = L("asset_id"),
                    Name = S("name") ?? "",
                    Description = S("description") ?? "",
                    CreatorName = S("creator_name") ?? "",
                    CreatorVerified = L("creator_verified") == 1,
                    CategoryPath = S("category_path") ?? "",
                    UpVotes = L("up_votes"),
                    DownVotes = L("down_votes"),
                    UpVotePercent = (int)L("up_percent"),
                    HasScripts = L("has_scripts") == 1,
                    ScriptCount = (int)L("script_count"),
                    Triangles = L("triangles"),
                    MeshParts = (int)L("mesh_parts"),
                    Status = S("status") ?? AssetStatus.Discovered,
                    StatusReason = S("status_reason"),
                    Attempts = (int)L("attempts"),
                    Placeable = L("placeable") == 1,
                    ScriptsRemoved = (int)L("scripts_removed"),
                    HasThumbnail = L("has_thumbnail") == 1,
                    LabelVersion = (int)L("label_version"),
                    TimesUsed = (int)L("times_used"),
                    DiscoveredQuery = S("discovered_query") ?? "",
                    Priority = (int)L("priority"),
                    DiscoveredUtc = ParseIso(S("discovered_utc")) ?? DateTime.MinValue,
                    AnalyzedUtc = ParseIso(S("analyzed_utc")),
                    LabeledUtc = ParseIso(S("labeled_utc")),
                };
                if (S("measurements") is string m) rec.Measurements = JsonConvert.DeserializeObject<AssetMeasurements>(m);
                if (S("script_flags") is string f) rec.ScriptFlags = JsonConvert.DeserializeObject<List<string>>(f) ?? new();
                if (S("label") is string l) rec.Label = JsonConvert.DeserializeObject<AssetLabel>(l);
                if (S("renders") is string rs) rec.Renders = JsonConvert.DeserializeObject<List<string>>(rs) ?? new();
                if (S("usage_notes") is string u) rec.UsageNotes = JsonConvert.DeserializeObject<List<string>>(u) ?? new();
                list.Add(rec);
            }
            return list;
        }

        private static string Iso(DateTime utc) => utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

        // RoundtripKind cannot be combined with AdjustToUniversal; parse the round-trip form, then normalise.
        private static DateTime? ParseIso(string? s) =>
            s != null && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d.ToUniversalTime() : null;

        private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
    }
}
