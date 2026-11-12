using System.Globalization;
using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Api;
using Omnipotent.Services.OmniscienceV3.Kernel.Explore;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Modules.Explore
{
    public sealed partial class ExploreModule
    {
        private enum ColumnType { Text, Integer, Real, Flag }

        /// <summary>A dataset as the engine serves it: its declaration and where its rows live in the mirror.
        /// <see cref="Source"/> and <see cref="Columns"/> are set for datasets copied generically from another module's table.</summary>
        private sealed record Bound(DatasetDescriptor Dataset, string Table, string Key, string Time, bool SubjectZone, bool TimeQuality,
            ExploreSource? Source, IReadOnlyList<(string Name, ColumnType Type)> Columns);

        private const int SourceBatch = 20_000;
        private static readonly long WholeCopyEveryMs = (long)TimeSpan.FromHours(24).TotalMilliseconds;
        private static readonly Regex Identifier = new("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.Compiled);
        private static readonly Regex NotTableChar = new("[^a-z0-9]+", RegexOptions.Compiled);
        private readonly Dictionary<string, Bound> bound = new(StringComparer.Ordinal);

        /// <summary>Every dataset this engine can answer for: its own and the mirrored ones of other modules.</summary>
        public IReadOnlyList<DatasetDescriptor> ServedDatasets => bound.Values.Select(b => b.Dataset).ToList();

        private Bound BoundOf(string datasetId) =>
            datasetId != null && bound.TryGetValue(datasetId, out var b) ? b
                : throw ApiException.BadRequest($"No dataset '{datasetId}' in the Explore mirror (a module may declare a dataset without offering it to Explore).");

        /// <summary>Binds this module's datasets and every dataset other modules offer; creates (or re-creates, when a
        /// declaration changed) the mirror table of each offered dataset.</summary>
        private void BindDatasets()
        {
            bound.Clear();
            foreach (var ds in ExploreDatasets.All)
            {
                var (key, time) = ExploreDatasets.KeyOf(ds.Id);
                bound[ds.Id] = new Bound(ds, ds.Id, key, time, ExploreDatasets.HasSubjectZone(ds.Id), ExploreDatasets.HasTimeQuality(ds.Id), null, Array.Empty<(string, ColumnType)>());
            }
            if (otherModules == null) return;
            foreach (var module in otherModules())
            {
                if (ReferenceEquals(module, this) || module is not IExploreSourceProvider provider) continue;
                foreach (var source in provider.ExploreSources)
                {
                    try
                    {
                        BindSource(module, source);
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DuckDBException)
                    {
                        // One badly declared dataset must not take the engine or the other datasets down: it is reported and left out.
                        Context.LogError($"[OmniscienceV3] Explore cannot mirror dataset '{source.DatasetId}' of module '{module.Manifest.Id}': {ex.Message}", ex);
                    }
                }
            }
        }

        private void BindSource(IIntelModule module, ExploreSource source)
        {
            var ds = module.Manifest.Datasets.FirstOrDefault(d => d.Id == source.DatasetId)
                     ?? throw new ArgumentException("the module's manifest declares no dataset with that id.");
            if (bound.ContainsKey(ds.Id)) throw new ArgumentException("another module already provides a dataset with that id.");
            var names = new[] { source.Table, source.KeyColumn, source.TimeColumn, source.ChangeColumn }
                .Concat(ds.Dimensions.Select(b => b.Column)).Concat(ds.Measures.Select(m => m.Column)).OfType<string>();
            // The names go into SQL text: only plain identifiers are accepted (they come from module code, never from a request).
            foreach (var name in names)
                if (!Identifier.IsMatch(name)) throw new ArgumentException($"'{name}' is not a plain identifier.");

            var columns = new List<(string Name, ColumnType Type)>();
            void Add(string name, ColumnType type)
            {
                if (!columns.Any(c => c.Name == name)) columns.Add((name, type));
            }
            Add(source.KeyColumn, ColumnType.Text);
            Add(source.TimeColumn, ColumnType.Integer);
            foreach (var b in ds.Dimensions)
                Add(b.Column, DimensionCatalog.Get(b.DimensionId).Kind switch
                {
                    DimensionKind.Time => ColumnType.Integer,
                    DimensionKind.Number => ColumnType.Real,
                    DimensionKind.Flag => ColumnType.Flag,
                    _ => ColumnType.Text,
                });
            foreach (var m in ds.Measures)
                if (m.Column != null) Add(m.Column, ColumnType.Real);

            string table = "x_" + NotTableChar.Replace(ds.Id.ToLowerInvariant(), "_").Trim('_');
            if (bound.Values.Any(b => b.Table == table)) throw new ArgumentException($"its mirror table name '{table}' collides with another dataset's.");

            // The mirror table follows the declaration: when the columns changed, it is rebuilt from the source.
            var wanted = columns.Select(c => (c.Name, Type: DuckType(c.Type))).ToList();
            if (source.ChangeColumn != null) wanted.Add(("_change", "BIGINT"));
            var existing = new List<(string Name, string Type)>();
            using (var cmd = Db.CreateCommand())
            {
                cmd.CommandText = "SELECT column_name, data_type FROM information_schema.columns WHERE table_name = $t ORDER BY ordinal_position";
                cmd.Parameters.Add(new DuckDBParameter("t", table));
                using var r = cmd.ExecuteReader();
                while (r.Read()) existing.Add((r.GetString(0), r.GetString(1)));
            }
            if (existing.Count > 0 && !existing.SequenceEqual(wanted))
            {
                Exec($"DROP TABLE {table}");
                ClearState(SourceState(ds.Id));
                ClearState(SourceState(ds.Id) + ".whole_at");
                existing.Clear();
            }
            if (existing.Count == 0) Exec($"CREATE TABLE {table}({string.Join(", ", wanted.Select(c => c.Name + " " + c.Type))})");
            bound[ds.Id] = new Bound(ds, table, source.KeyColumn, source.TimeColumn, false, false, source, columns);
        }

        private static string DuckType(ColumnType t) => t switch
        {
            ColumnType.Integer => "BIGINT",
            ColumnType.Real => "DOUBLE",
            ColumnType.Flag => "BOOLEAN",
            _ => "VARCHAR",
        };

        private static string SourceState(string datasetId) => "src." + datasetId;

        /// <summary>Copies every offered dataset from its module's table. A failing dataset is reported and does not stop the others.</summary>
        private Dictionary<string, long> MirrorSources(CancellationToken ct)
        {
            var result = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var b in bound.Values.Where(b => b.Source != null).OrderBy(b => b.Dataset.Id, StringComparer.Ordinal).ToList())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    result[b.Dataset.Id] = MirrorSource(b, ct);
                }
                catch (Exception ex) when (ex is SqliteException or DuckDBException or InvalidCastException or FormatException or InvalidOperationException)
                {
                    Context.LogError($"[OmniscienceV3] Explore could not mirror dataset '{b.Dataset.Id}' from '{b.Source!.Table}'.", ex);
                    result[b.Dataset.Id] = -1;
                }
            }
            return result;
        }

        private long MirrorSource(Bound b, CancellationToken ct)
        {
            var src = b.Source!;
            using var intel = Context.Stores.Intel.OpenRead();
            // The module may not have created its table yet (its migration runs with it), or it is switched off.
            if (intel.Scalar<long>(null, "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('table','view') AND name=$t", ("$t", src.Table)) == 0) return 0;
            long sourceRows = intel.Scalar<long>(null, $"SELECT COUNT(*) FROM {src.Table}");
            string stateKey = SourceState(b.Dataset.Id);
            long now = Context.Clock.NowMs;
            long? cursor = src.ChangeColumn == null ? null : ReadState(stateKey);
            long mirrored = CountRows(b.Table);
            // Whole copy: no change column; the first pass; rows vanished from the source (the mirror holds more than it);
            // or the daily reconciliation that catches deletions an incremental pass cannot see.
            bool whole = src.ChangeColumn == null || cursor == null || mirrored > sourceRows || now - (ReadState(stateKey + ".whole_at") ?? 0) >= WholeCopyEveryMs;
            if (whole)
            {
                int max = Context.Settings.Get("explore.whole_copy_max_rows", 1_000_000);
                if (src.ChangeColumn == null && sourceRows > max)
                    throw new InvalidOperationException($"'{src.Table}' has {sourceRows} rows and no change column; above {max} rows a dataset must declare one to be mirrored.");
                long maxChange = 0, copied = 0;
                using var tx = Db.BeginTransaction();
                Exec($"DELETE FROM {b.Table}");
                using (var appender = Db.CreateAppender(b.Table))
                using (var cmd = intel.Cmd(null, $"SELECT {SelectList(b)} FROM {src.Table}"))
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        if ((copied & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
                        var row = ReadRow(b, r);
                        if (row.Values[0] == null) continue; // a record without a key cannot be drilled to or updated
                        Append(appender, b, row);
                        maxChange = Math.Max(maxChange, row.Change);
                        copied++;
                    }
                }
                tx.Commit();
                if (src.ChangeColumn != null)
                {
                    WriteState(stateKey, maxChange);
                    WriteState(stateKey + ".whole_at", now);
                }
                return copied;
            }

            long done = 0, at = cursor!.Value;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var rows = ReadRows(intel, b, $"WHERE {src.ChangeColumn} > $c ORDER BY {src.ChangeColumn} LIMIT {SourceBatch.ToString(CultureInfo.InvariantCulture)}", ("$c", at));
                if (rows.Count == 0) break;
                bool full = rows.Count == SourceBatch;
                if (full)
                {
                    // The last change value may continue past the limit. Usually it is left for the next round; when the whole
                    // batch carries one value (a bulk write stamped it), every row with that value is taken at once.
                    long last = rows[^1].Change;
                    if (rows[0].Change == last) rows = ReadRows(intel, b, $"WHERE {src.ChangeColumn} = $c", ("$c", last));
                    else rows.RemoveAll(x => x.Change == last);
                }
                long top = rows.Max(x => x.Change);
                rows.RemoveAll(x => x.Values[0] == null); // a record without a key cannot be drilled to or updated
                using (var tx = Db.BeginTransaction())
                {
                    DeleteIn(b.Table, b.Key, rows.Select(x => (string)x.Values[0]!).ToList());
                    // The same key twice in one batch (a view that fans out) keeps its last row.
                    using (var appender = Db.CreateAppender(b.Table))
                        foreach (var row in rows.GroupBy(x => (string)x.Values[0]!, StringComparer.Ordinal).Select(g => g.Last())) Append(appender, b, row);
                    tx.Commit();
                }
                at = Math.Max(at, top);
                WriteState(stateKey, at);
                done += rows.Count;
                if (!full) break;
            }
            return done;
        }

        private long CountRows(string table)
        {
            using var cmd = Db.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private static string SelectList(Bound b) =>
            string.Join(", ", b.Columns.Select(c => c.Name)) + (b.Source!.ChangeColumn != null ? ", " + b.Source.ChangeColumn : "");

        private sealed record SourceRow(object?[] Values, long Change);

        private static List<SourceRow> ReadRows(SqliteConnection intel, Bound b, string tail, params (string, object?)[] args)
        {
            using var cmd = intel.Cmd(null, $"SELECT {SelectList(b)} FROM {b.Source!.Table} {tail}", args);
            using var r = cmd.ExecuteReader();
            var rows = new List<SourceRow>();
            while (r.Read()) rows.Add(ReadRow(b, r));
            return rows;
        }

        /// <summary>One source row in the mirror's column types. SQLite columns hold whatever was written, so each value is
        /// read by what it is, not by what the column was declared as; a value that cannot be the wanted type is unknown (null).</summary>
        private static SourceRow ReadRow(Bound b, SqliteDataReader r)
        {
            var values = new object?[b.Columns.Count];
            for (int i = 0; i < values.Length; i++)
            {
                if (r.IsDBNull(i)) continue;
                object raw = r.GetValue(i);
                values[i] = b.Columns[i].Type switch
                {
                    ColumnType.Integer => raw switch
                    {
                        long l => l,
                        double d when !double.IsNaN(d) && !double.IsInfinity(d) => (long)Math.Round(d),
                        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) => l,
                        _ => null,
                    },
                    ColumnType.Real => raw switch
                    {
                        long l => (double)l,
                        double d => d,
                        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) => d,
                        _ => null,
                    },
                    ColumnType.Flag => raw switch
                    {
                        long l => l != 0,
                        double d => d != 0,
                        string s when bool.TryParse(s, out bool f) => f,
                        string s when s is "1" or "0" => s == "1",
                        _ => null,
                    },
                    _ => raw switch
                    {
                        string s => s,
                        byte[] => null,
                        _ => Convert.ToString(raw, CultureInfo.InvariantCulture),
                    },
                };
            }
            long change = 0;
            if (b.Source!.ChangeColumn != null && !r.IsDBNull(values.Length))
                change = r.GetValue(values.Length) switch { long l => l, double d => (long)d, _ => 0 };
            return new SourceRow(values, change);
        }

        private static void Append(DuckDBAppender appender, Bound b, SourceRow row)
        {
            var target = appender.CreateRow();
            for (int i = 0; i < row.Values.Length; i++)
            {
                object? v = row.Values[i];
                target = b.Columns[i].Type switch
                {
                    ColumnType.Integer => target.AppendValue((long?)v),
                    ColumnType.Real => target.AppendValue((double?)v),
                    ColumnType.Flag => target.AppendValue((bool?)v),
                    _ => target.AppendValue((string?)v),
                };
            }
            if (b.Source!.ChangeColumn != null) target = target.AppendValue((long?)row.Change);
            target.EndRow();
        }

        /// <summary>Person columns of the mirrored module datasets (an erased person's rows leave them too, N20).</summary>
        private IEnumerable<(string Table, string Column)> SourcePersonColumns() =>
            bound.Values.Where(b => b.Source != null).SelectMany(b => b.Dataset.Dimensions
                .Where(d => DimensionCatalog.Get(d.DimensionId).EntityType == "person").Select(d => (b.Table, d.Column))).Distinct();
    }
}
