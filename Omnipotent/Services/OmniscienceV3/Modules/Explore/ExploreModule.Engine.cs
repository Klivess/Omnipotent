using System.Globalization;
using System.Text;
using System.Text.Json;
using DuckDB.NET.Data;
using Omnipotent.Services.OmniscienceV3.Api;
using Omnipotent.Services.OmniscienceV3.Kernel.Explore;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Modules.Explore
{
    [ApiContract]
    public sealed record ExploreFacetsRequest(string Dataset, IReadOnlyList<ExploreFilter> Filters, IReadOnlyList<string> Dimensions, int Limit = 20);

    [ApiContract]
    public sealed record ExploreFacetValueDto(JsonElement Value, long Count);

    [ApiContract]
    public sealed record ExploreFacetDto(string Dimension, IReadOnlyList<ExploreFacetValueDto> Values);

    [ApiContract]
    public sealed record ExploreFacetsDto(string Dataset, IReadOnlyList<ExploreFacetDto> Facets, long AsOfMs);

    [ApiContract]
    public enum ExportFormat { Csv, Json, Parquet }

    [ApiContract]
    public sealed record ExploreExportRequest(ExportFormat Format, string? DrillToken = null, ExploreQuery? Query = null);

    public sealed partial class ExploreModule
    {
        public const int MaxGroups = 10_000;
        private readonly Dictionary<string, (long Version, ExploreResult Result)> cache = new(StringComparer.Ordinal);
        private readonly LinkedList<string> cacheOrder = new();

        /// <summary>One query's FROM/WHERE: joins for person dimensions (canonical at query time), filters, policy exclusions.</summary>
        private sealed class SqlParts
        {
            public readonly List<string> Joins = new();
            public readonly List<string> Where = new();
            public readonly List<DuckDBParameter> Params = new();
            public readonly List<string> Notes = new();
            private readonly Dictionary<string, string> exprs = new(StringComparer.Ordinal);
            public required Bound Bound { get; init; }
            public DatasetDescriptor Dataset => Bound.Dataset;

            public string Param(object? value)
            {
                string name = "p" + Params.Count.ToString(CultureInfo.InvariantCulture);
                Params.Add(new DuckDBParameter(name, value));
                return "$" + name;
            }

            public string Expr(string dimension)
            {
                if (exprs.TryGetValue(dimension, out var e)) return e;
                var binding = Dataset.Dimensions.FirstOrDefault(b => b.DimensionId == dimension) ?? throw ApiException.BadRequest($"Dataset '{Dataset.Id}' has no dimension '{dimension}'.");
                var def = DimensionCatalog.Get(dimension);
                string col = "t." + binding.Column;
                if (def.EntityType == "person")
                {
                    string alias = "pm" + exprs.Count.ToString(CultureInfo.InvariantCulture);
                    Joins.Add($"LEFT JOIN person_map {alias} ON {alias}.person_id = {col}");
                    e = $"COALESCE({alias}.canonical_id, {col})";
                }
                else e = col;
                return exprs[dimension] = e;
            }

            public string From => $"FROM {Bound.Table} t {string.Join(" ", Joins)}";
            public string WhereSql => Where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", Where);
        }

        private static object? Value(JsonElement v, DimensionKind kind) => v.ValueKind switch
        {
            JsonValueKind.Number => kind is DimensionKind.Time || v.TryGetInt64(out _) && !v.GetRawText().Contains('.') ? v.GetInt64() : v.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => kind == DimensionKind.Flag ? bool.TryParse(v.GetString(), out var b) ? b : v.GetString() : v.GetString(),
            JsonValueKind.Null => null,
            _ => throw ApiException.BadRequest($"Unsupported filter value {v.GetRawText()}."),
        };

        /// <param name="bucketExpr">For a drill: the bucket code expression of the result being drilled, so a filter on
        /// <c>@bucket</c> can select the rows behind one bucket (or a range of buckets) of it.</param>
        private static void AddFilter(SqlParts p, ExploreFilter f, string? bucketExpr = null, TimeBucket bucket = TimeBucket.None)
        {
            string e;
            List<object?> vals;
            if (f.Dimension == ExploreColumnKinds.BucketDimension)
            {
                e = bucketExpr ?? throw ApiException.BadRequest("This drill has no time bucket to narrow.");
                if (f.Op is FilterOp.Contains) throw ApiException.BadRequest("A time bucket cannot be searched by text.");
                vals = f.Values.Select(v => (object?)BucketCodeOf(bucket, v)).ToList();
                // Buckets are whole units: "between a and b" includes both ends.
                if (f.Op == FilterOp.Between) { p.Where.Add($"{e} >= {p.Param(vals[0])} AND {e} <= {p.Param(vals[1])}"); return; }
            }
            else
            {
                e = p.Expr(f.Dimension);
                var kind = DimensionCatalog.Get(f.Dimension).Kind;
                vals = f.Values.Select(v => Value(v, kind)).ToList();
            }
            switch (f.Op)
            {
                case FilterOp.Eq: p.Where.Add($"{e} = {p.Param(vals[0])}"); break;
                case FilterOp.NotEq: p.Where.Add($"({e} IS NULL OR {e} <> {p.Param(vals[0])})"); break;
                case FilterOp.In: p.Where.Add($"{e} IN ({string.Join(",", vals.Select(p.Param))})"); break;
                case FilterOp.NotIn: p.Where.Add($"({e} IS NULL OR {e} NOT IN ({string.Join(",", vals.Select(p.Param))}))"); break;
                case FilterOp.Between: p.Where.Add($"{e} >= {p.Param(vals[0])} AND {e} < {p.Param(vals[1])}"); break;
                case FilterOp.Gte: p.Where.Add($"{e} >= {p.Param(vals[0])}"); break;
                case FilterOp.Lte: p.Where.Add($"{e} <= {p.Param(vals[0])}"); break;
                case FilterOp.Contains: p.Where.Add($"contains(lower(CAST({e} AS VARCHAR)), lower({p.Param(vals[0]?.ToString())}))"); break;
                case FilterOp.IsNull: p.Where.Add($"{e} IS NULL"); break;
                case FilterOp.IsNotNull: p.Where.Add($"{e} IS NOT NULL"); break;
            }
        }

        private readonly Dictionary<string, (long Version, long Min, long Max)> ranges = new(StringComparer.Ordinal);

        /// <summary>The span of a time column in the mirror (cached per mirror version): bounds which zone transitions a query needs.</summary>
        private (long Min, long Max) ColumnRange(string table, string column)
        {
            string key = table + "." + column;
            long v = Version;
            lock (ranges)
                if (ranges.TryGetValue(key, out var hit) && hit.Version == v) return (hit.Min, hit.Max);
            long min = Context.Clock.NowMs, max = min;
            using (var conn = OpenReader())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT MIN({column}), MAX({column}) FROM {table}";
                using var r = cmd.ExecuteReader();
                if (r.Read() && !r.IsDBNull(0))
                {
                    min = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
                    max = Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture);
                }
            }
            lock (ranges) ranges[key] = (v, min, max);
            return (min, max);
        }

        /// <summary>The zone's UTC offset at a time column's value, as a balanced decision tree over the zone's transitions
        /// (a handful of integer comparisons per row instead of a calendar library call).</summary>
        private static string OffsetSql(string column, IReadOnlyList<(long FromMs, long OffsetMs)> segments, int lo, int hi)
        {
            if (lo == hi) return segments[lo].OffsetMs.ToString(CultureInfo.InvariantCulture);
            int mid = (lo + hi + 1) / 2;
            return $"CASE WHEN {column} < {segments[mid].FromMs.ToString(CultureInfo.InvariantCulture)} THEN {OffsetSql(column, segments, lo, mid - 1)} ELSE {OffsetSql(column, segments, mid, hi)} END";
        }

        /// <summary>
        /// A time column as local wall-clock milliseconds on a lens. DST-correct: the zone's offsets come from the same
        /// time-zone data as every other v3 computation (<see cref="Temporal.Zones.OffsetSegments"/>), and each person's
        /// own offset is stored with their messages when they are mirrored.
        /// </summary>
        private string LocalMs(SqlParts p, string timeColumn, TimeLens lens, string? zone)
        {
            string column = "t." + timeColumn;
            TimeZoneInfo tz;
            if (zone != null) tz = Temporal.Zones.GetOrThrow(zone);
            else if (lens == TimeLens.Utc) return column;
            else if (lens == TimeLens.Subject && p.Bound.SubjectZone && timeColumn == p.Bound.Time) return $"({column} + COALESCE(t.s_offset_ms, 0))";
            else
            {
                if (lens == TimeLens.Subject) p.Notes.Add("This dataset has no subject zone for that time; Klives's zone was used.");
                tz = Context.Settings.KlivesZone;
            }
            var (min, max) = ColumnRange(p.Bound.Table, timeColumn);
            var segments = Temporal.Zones.OffsetSegments(tz, min, max);
            return $"({column} + {OffsetSql(column, segments, 0, segments.Count - 1)})";
        }

        /// <summary>
        /// The bucket of a local time as an integer code (hours or days since 1970, months or quarters since year 0, a
        /// year, an hour of day, an ISO weekday): integer arithmetic per row, formatted only for the rows returned.
        /// </summary>
        private static string BucketCode(string localMs, TimeBucket bucket)
        {
            string day = $"({localMs} // 86400000)", ts = $"epoch_ms({localMs})";
            return bucket switch
            {
                TimeBucket.Hour => $"({localMs} // 3600000)",
                TimeBucket.Day => day,
                TimeBucket.Week => $"({day} - (({day} + 3) % 7))", // 1970-01-01 was a Thursday; weeks start on Monday (ISO)
                TimeBucket.Month => $"(year({ts}) * 12 + month({ts}) - 1)",
                TimeBucket.Quarter => $"(year({ts}) * 4 + quarter({ts}) - 1)",
                TimeBucket.Year => $"year({ts})",
                TimeBucket.HourOfDay => $"(({localMs} // 3600000) % 24)",
                TimeBucket.DayOfWeek => $"((({day} + 3) % 7) + 1)",
                _ => throw new ArgumentOutOfRangeException(nameof(bucket)),
            };
        }

        private static readonly int EpochDay = new DateOnly(1970, 1, 1).DayNumber;

        /// <summary>A bucket code as the value a result shows: text for calendar buckets, a number for hour of day and weekday.</summary>
        internal static JsonElement BucketValue(TimeBucket bucket, long code) => bucket switch
        {
            TimeBucket.Hour => JsonSerializer.SerializeToElement(DateTime.UnixEpoch.AddHours(code).ToString("yyyy-MM-dd'T'HH':00'", CultureInfo.InvariantCulture)),
            TimeBucket.Day or TimeBucket.Week => JsonSerializer.SerializeToElement(DateOnly.FromDayNumber(EpochDay + (int)code).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            TimeBucket.Month => JsonSerializer.SerializeToElement($"{(code / 12).ToString("D4", CultureInfo.InvariantCulture)}-{(code % 12 + 1).ToString("D2", CultureInfo.InvariantCulture)}"),
            TimeBucket.Quarter => JsonSerializer.SerializeToElement($"{(code / 4).ToString("D4", CultureInfo.InvariantCulture)}-Q{(code % 4 + 1).ToString(CultureInfo.InvariantCulture)}"),
            TimeBucket.Year => JsonSerializer.SerializeToElement(code.ToString("D4", CultureInfo.InvariantCulture)),
            _ => JsonSerializer.SerializeToElement(code),
        };

        /// <summary>The code of a bucket value as a result showed it (the inverse of <see cref="BucketValue"/>); a drill narrows by it.</summary>
        internal static long BucketCodeOf(TimeBucket bucket, JsonElement value)
        {
            string text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
            const NumberStyles Int = NumberStyles.None;
            var inv = CultureInfo.InvariantCulture;
            bool ok;
            long code = 0;
            switch (bucket)
            {
                case TimeBucket.Hour:
                    ok = DateTime.TryParseExact(text, "yyyy-MM-dd'T'HH':00'", inv, DateTimeStyles.None, out var hour);
                    if (ok) code = (long)(DateTime.SpecifyKind(hour, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalHours;
                    break;
                case TimeBucket.Day or TimeBucket.Week:
                    ok = DateOnly.TryParseExact(text, "yyyy-MM-dd", inv, DateTimeStyles.None, out var date);
                    if (ok) code = date.DayNumber - EpochDay;
                    break;
                case TimeBucket.Month:
                    ok = text.Length == 7 && text[4] == '-' && int.TryParse(text.AsSpan(0, 4), Int, inv, out int my) && int.TryParse(text.AsSpan(5, 2), Int, inv, out int mm) && mm is >= 1 and <= 12;
                    if (ok) code = int.Parse(text.AsSpan(0, 4), Int, inv) * 12L + int.Parse(text.AsSpan(5, 2), Int, inv) - 1;
                    break;
                case TimeBucket.Quarter:
                    ok = text.Length == 7 && text[4] == '-' && text[5] == 'Q' && int.TryParse(text.AsSpan(0, 4), Int, inv, out int qy) && text[6] is >= '1' and <= '4';
                    if (ok) code = int.Parse(text.AsSpan(0, 4), Int, inv) * 4L + (text[6] - '1');
                    break;
                case TimeBucket.Year:
                    ok = long.TryParse(text, Int, inv, out code) && code is >= 0 and <= 9999;
                    break;
                case TimeBucket.HourOfDay:
                    ok = long.TryParse(text, Int, inv, out code) && code is >= 0 and <= 23;
                    break;
                case TimeBucket.DayOfWeek:
                    ok = long.TryParse(text, Int, inv, out code) && code is >= 1 and <= 7;
                    break;
                default:
                    ok = false;
                    break;
            }
            return ok ? code : throw ApiException.BadRequest($"'{text}' is not a {bucket} bucket value of this result.");
        }

        private static string Measure(MeasureDescriptor m) => m.Aggregate switch
        {
            "count" => "COUNT(*)",
            "sum" => $"SUM(t.{m.Column})",
            "avg" => $"AVG(t.{m.Column})",
            "min" => $"MIN(t.{m.Column})",
            "max" => $"MAX(t.{m.Column})",
            "distinct" => $"COUNT(DISTINCT t.{m.Column})",
            "p50" => $"quantile_cont(t.{m.Column}, 0.5)",
            "p90" => $"quantile_cont(t.{m.Column}, 0.9)",
            "p95" => $"quantile_cont(t.{m.Column}, 0.95)",
            _ => throw ApiException.BadRequest($"Unsupported aggregate '{m.Aggregate}'."),
        };

        private static string SqlList(IEnumerable<string> values) => string.Join(",", values.Select(v => "'" + v.ToLowerInvariant().Replace("'", "''") + "'"));

        /// <summary>
        /// Policy at query planning time (plan D4 "MinorGuard: minors' presence/voice/typing and per-person time-of-day/weekday
        /// patterns are excluded"), applied by what a dataset holds rather than by its name, so datasets other modules
        /// declare are covered too:
        /// <list type="bullet">
        /// <item>presence, voice and typing events of minors are never counted;</item>
        /// <item>a dataset that carries fact attributes never shows special-category attributes (they are not inferred, and
        /// not offered as a dimension to slice people by), nor the restricted identity attributes of a minor;</item>
        /// <item>time-of-day and weekday buckets leave out every row a minor wrote or was written to, whatever the query
        /// filters on: a filter on one direct chat, one small channel or a cohort of one narrows an aggregate to a person
        /// as surely as a person filter does, so the rule does not depend on how the query is phrased.</item>
        /// </list>
        /// </summary>
        private static void ApplyPolicy(SqlParts p, TimeBucket bucket)
        {
            var ds = p.Dataset;
            if (!ds.MinorFiltered) return;
            const string Restricted = "(SELECT person_id FROM restricted_persons)";
            if (p.Bound.Table == ExploreDatasets.Events)
            {
                string person = p.Expr("person.subject");
                p.Where.Add($"NOT (t.kind IN ('presence','voice','typing') AND {person} IN {Restricted})");
            }
            var attribute = ds.Dimensions.FirstOrDefault(b => b.DimensionId is "content.attribute" or "knowledge.attribute");
            if (attribute != null)
            {
                string col = $"lower(t.{attribute.Column})";
                if (ds.Dimensions.Any(b => b.DimensionId == "person.subject"))
                    p.Where.Add($"NOT ({p.Expr("person.subject")} IN {Restricted} AND {col} IN ({SqlList(MinorGuard.RestrictedAttributes)}))");
                p.Where.Add($"(t.{attribute.Column} IS NULL OR {col} NOT IN ({SqlList(SensitiveTraits.Categories)}))");
            }
            if (bucket is TimeBucket.HourOfDay or TimeBucket.DayOfWeek or TimeBucket.Hour)
            {
                var personDims = ds.Dimensions.Select(b => b.DimensionId).Where(d => DimensionCatalog.Get(d).EntityType == "person").Distinct(StringComparer.Ordinal).ToList();
                foreach (var dim in personDims)
                {
                    string e = p.Expr(dim);
                    p.Where.Add($"({e} IS NULL OR {e} NOT IN {Restricted})");
                }
                if (personDims.Count > 0) p.Notes.Add("People under 18 (or likely) are left out of time-of-day and weekday statistics (MinorGuard).");
            }
        }

        /// <summary>"As known at" a past instant: what had happened by then and, for messages, what Omniscience had received by then.</summary>
        private static void AddAsOf(SqlParts p, long asOfMs)
        {
            p.Where.Add($"t.{p.Bound.Time} <= {p.Param(asOfMs)}");
            if (p.Bound.Table == ExploreDatasets.Messages) p.Where.Add($"t.ingested <= {p.Param(asOfMs)}");
        }

        /// <summary>Hour statistics leave out timestamps that have no trustworthy hour (R4).</summary>
        private static void AddTimeQuality(SqlParts p, TimeBucket bucket)
        {
            if (bucket is not (TimeBucket.Hour or TimeBucket.HourOfDay) || !p.Bound.TimeQuality) return;
            p.Where.Add("t.time_quality NOT IN (3, 5)");
            p.Notes.Add("Day-precision and legacy unknown-zone timestamps are left out of hour statistics (R4).");
        }

        private static string TimeColumnOf(DatasetDescriptor ds, string? timeDimension)
        {
            string dim = timeDimension ?? ds.Dimensions.First(d => DimensionCatalog.Get(d.DimensionId).Kind == DimensionKind.Time).DimensionId;
            return ds.Dimensions.First(d => d.DimensionId == dim).Column;
        }

        public async Task<ExploreResult> QueryAsync(ExploreQuery q, Func<string, bool> mayReadPermission, CancellationToken ct)
        {
            var b = BoundOf(q.Dataset);
            var ds = b.Dataset;
            if (ExploreValidation.Validate(q, new[] { ds }) is { } problem) throw ApiException.BadRequest(problem);
            if (!mayReadPermission(ds.RequiredPermission)) throw ApiException.Forbidden($"'{ds.Title}' needs the {ds.RequiredPermission} permission.");
            string cacheKey = JsonSerializer.Serialize(q, KernelJson.Options) + "|" + Context.Settings.KlivesZone.Id;
            lock (cache)
                if (cache.TryGetValue(cacheKey, out var hit) && hit.Version == Version) return hit.Result;

            var p = new SqlParts { Bound = b };
            foreach (var f in q.Filters) AddFilter(p, f);
            if (q.AsOfMs is long asOf) AddAsOf(p, asOf);
            var select = new List<string>();
            var columns = new List<ExploreColumn>();
            var groupExprs = new List<string>();
            foreach (var g in q.GroupBy)
            {
                string e = p.Expr(g);
                select.Add(e);
                groupExprs.Add(e);
                columns.Add(new ExploreColumn(g, DimensionCatalog.Get(g).Title, DimensionCatalog.Get(g).Kind.ToString()));
            }
            string? bucketExpr = null;
            if (q.Bucket != TimeBucket.None)
            {
                string timeColumn = TimeColumnOf(ds, q.TimeDimension);
                bucketExpr = BucketCode(LocalMs(p, timeColumn, q.Lens, q.Zone), q.Bucket);
                p.Where.Add($"t.{timeColumn} IS NOT NULL");
                AddTimeQuality(p, q.Bucket);
                select.Insert(0, bucketExpr);
                groupExprs.Insert(0, bucketExpr);
                columns.Insert(0, new ExploreColumn(ExploreColumnKinds.BucketDimension, q.Bucket.ToString(), ExploreColumnKinds.Bucket));
            }
            ApplyPolicy(p, q.Bucket);
            foreach (var mid in q.Measures)
            {
                var m = ds.Measures.First(x => x.Id == mid);
                select.Add(Measure(m));
                columns.Add(new ExploreColumn(m.Id, m.Title, ExploreColumnKinds.Measure));
            }
            int offset = 0;
            if (q.Cursor is { } cur && (!cur.StartsWith("o:", StringComparison.Ordinal) || !int.TryParse(cur[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out offset) || offset < 0))
                throw ApiException.BadRequest("Malformed cursor.");
            int limit = Math.Min(q.Limit, MaxGroups);
            // Buckets in time order; otherwise the largest groups first, with the group values as a tiebreak so paging is stable.
            string order = bucketExpr != null ? string.Join(", ", Enumerable.Range(1, groupExprs.Count).Select(i => i.ToString(CultureInfo.InvariantCulture) + " ASC NULLS LAST"))
                : groupExprs.Count > 0 ? $"{(groupExprs.Count + 1).ToString(CultureInfo.InvariantCulture)} DESC NULLS LAST, " + string.Join(", ", Enumerable.Range(1, groupExprs.Count).Select(i => i.ToString(CultureInfo.InvariantCulture) + " ASC NULLS LAST"))
                : "1";
            string sql = $"SELECT {string.Join(", ", select)} {p.From} {p.WhereSql}" + (groupExprs.Count > 0 ? $" GROUP BY {string.Join(", ", groupExprs)}" : "")
                         + $" ORDER BY {order} LIMIT {(limit + 1).ToString(CultureInfo.InvariantCulture)} OFFSET {offset.ToString(CultureInfo.InvariantCulture)}";
            // Coverage: which data the answer covers (plan "Explain shows … coverage"). It runs beside the aggregate, not after it.
            bool hasPlatform = ds.Dimensions.Any(d => d.DimensionId == "where.platform");
            string platformColumn = hasPlatform ? ds.Dimensions.First(d => d.DimensionId == "where.platform").Column : "";
            string coverageSql = $"SELECT COUNT(*), MIN(t.{b.Time}), MAX(t.{b.Time}){(hasPlatform ? $", string_agg(DISTINCT t.{platformColumn}, ',')" : ", NULL")} {p.From} {p.WhereSql}";
            var mainTask = RunAsync(sql, p.Params, columns.Count, ct);
            var coverageTask = RunAsync(coverageSql, CloneParams(p.Params), 4, ct);
            await Task.WhenAll(mainTask, coverageTask).ConfigureAwait(false);
            var rows = await mainTask.ConfigureAwait(false);
            string? next = rows.Count > limit ? "o:" + (offset + limit).ToString(CultureInfo.InvariantCulture) : null;
            if (rows.Count > limit) rows.RemoveAt(rows.Count - 1);
            if (q.Bucket != TimeBucket.None)
                foreach (var row in rows)
                    ((JsonElement[])row)[0] = BucketValue(q.Bucket, row[0].GetInt64());
            var coverageRow = (await coverageTask.ConfigureAwait(false)).FirstOrDefault();
            long records = coverageRow?[0].ValueKind == JsonValueKind.Number ? coverageRow[0].GetInt64() : 0;
            long? fromMs = coverageRow?[1].ValueKind == JsonValueKind.Number ? coverageRow[1].GetInt64() : null;
            long? toMs = coverageRow?[2].ValueKind == JsonValueKind.Number ? coverageRow[2].GetInt64() : null;
            var sources = coverageRow?[3].ValueKind == JsonValueKind.String ? coverageRow[3].GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries).OrderBy(s => s, StringComparer.Ordinal).ToList() : new List<string>();
            long mirrorAsOf = ReadState("as_of") ?? 0;
            string token = DrillToken(new DrillDto(ds.Id, q.Filters.ToList(), q.AsOfMs, q.Lens, q.Zone, q.Bucket, q.TimeDimension));
            string explain = Explain(sql, p, q, mirrorAsOf);
            var result = new ExploreResult(columns, rows, mirrorAsOf, new ExploreCoverage(sources, fromMs, toMs, records, false), next, token, explain);
            lock (cache)
            {
                if (!cache.ContainsKey(cacheKey)) cacheOrder.AddLast(cacheKey);
                cache[cacheKey] = (Version, result);
                while (cacheOrder.Count > 256) { cache.Remove(cacheOrder.First!.Value); cacheOrder.RemoveFirst(); }
            }
            return result;
        }

        private static List<DuckDBParameter> CloneParams(IEnumerable<DuckDBParameter> ps) => ps.Select(x => new DuckDBParameter(x.ParameterName, x.Value)).ToList();

        private string Explain(string sql, SqlParts p, ExploreQuery q, long asOf)
        {
            var sb = new StringBuilder();
            sb.Append("Dataset: ").Append(q.Dataset).Append(". Lens: ").Append(q.Zone ?? q.Lens.ToString())
              .Append(q.Lens == TimeLens.Klives && q.Zone == null ? " (" + Context.Settings.KlivesZone.Id + ")" : "")
              .Append(". Mirror as of ").Append(Temporal.Instant.Iso(asOf)).Append(" (version ").Append(Version.ToString(CultureInfo.InvariantCulture)).Append(").");
            if (q.AsOfMs is long a) sb.Append(" Data known as of ").Append(Temporal.Instant.Iso(a)).Append('.');
            foreach (var n in p.Notes.Distinct()) sb.Append(' ').Append(n);
            sb.Append("\nSQL: ").Append(sql);
            if (p.Params.Count > 0) sb.Append("\nParameters: ").Append(string.Join(", ", p.Params.Select(x => $"${x.ParameterName}={x.Value}")));
            return sb.ToString();
        }

        /// <summary>
        /// Runs a read with the configured timeout; a query past it is interrupted and reported, never left to hang a request.
        /// The reader connection belongs to the query and is closed by the query's own thread when it has really ended: after
        /// a timeout that is later than this method returns, and closing a connection under a running query is not safe.
        /// </summary>
        private async Task<List<IReadOnlyList<JsonElement>>> RunAsync(string sql, List<DuckDBParameter> ps, int columns, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            int timeout = Context.Settings.Get("explore.timeout_ms", 2000);
            var conn = OpenReader();
            var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var x in ps) cmd.Parameters.Add(x);
            var work = Task.Run(() =>
            {
                try
                {
                    var rows = new List<IReadOnlyList<JsonElement>>();
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var row = new JsonElement[columns];
                        for (int i = 0; i < columns; i++)
                            row[i] = r.IsDBNull(i) ? JsonSerializer.SerializeToElement<object?>(null) : JsonSerializer.SerializeToElement(Normalise(r.GetValue(i)));
                        rows.Add(row);
                    }
                    return rows;
                }
                finally
                {
                    cmd.Dispose();
                    conn.Dispose();
                }
            }, CancellationToken.None);
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var finished = await Task.WhenAny(work, Task.Delay(timeout, timer.Token)).ConfigureAwait(false);
            if (finished != work)
            {
                try { cmd.Cancel(); }
                catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or DuckDBException or ObjectDisposedException)
                {
                    // The engine cannot interrupt this query (or it ended this instant): it finishes in the background under
                    // DuckDB's memory limit, and its connection closes when it does.
                    Context.LogError("[OmniscienceV3] an Explore query passed its timeout and could not be interrupted.", ex);
                }
                // The abandoned query ends with an "interrupted" error that nobody awaits: mark it observed.
                _ = work.ContinueWith(static t => t.Exception?.Handle(static _ => true), CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                ct.ThrowIfCancellationRequested();
                throw new ApiException(System.Net.HttpStatusCode.UnprocessableEntity, "too_slow",
                    $"The query took longer than {timeout} ms. Narrow the time range or the filters, or use fewer groupings.");
            }
            timer.Cancel();
            try { return await work.ConfigureAwait(false); }
            catch (DuckDBException ex)
            {
                Context.LogError("[OmniscienceV3] Explore query failed: " + sql, ex);
                throw ApiException.BadRequest("The query could not run on this dataset (the service log has the details).");
            }
        }

        private static object? Normalise(object v) => v switch
        {
            DateTime d => d.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            System.Numerics.BigInteger b => (long)b,
            decimal m => (double)m,
            _ => v,
        };

        // ── drill tokens and records ─────────────────────────────────────────────

        internal sealed record DrillDto(string Dataset, List<ExploreFilter> Filters, long? AsOfMs, TimeLens Lens, string? Zone, TimeBucket Bucket, string? TimeDimension);

        private static string DrillToken(DrillDto d) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d, KernelJson.Options))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static DrillDto ParseToken(string token)
        {
            try
            {
                string s = token.Replace('-', '+').Replace('_', '/');
                s += new string('=', (4 - s.Length % 4) % 4);
                return JsonSerializer.Deserialize<DrillDto>(Encoding.UTF8.GetString(Convert.FromBase64String(s)), KernelJson.Options) ?? throw new FormatException();
            }
            catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
            {
                throw ApiException.BadRequest("The drill token is not valid (re-run the query to get a new one).");
            }
        }

        /// <summary>
        /// Builds the WHERE of a drill (token filters + narrowing) after validating them against the dataset. A token is not
        /// a capability: it only restates a query, and every permission and policy rule is applied again here.
        /// </summary>
        private (SqlParts Parts, DrillDto Drill) DrillParts(string token, IReadOnlyList<ExploreFilter>? narrow)
        {
            var d = ParseToken(token);
            if (d.Dataset == null || d.Filters == null) throw ApiException.BadRequest("The drill token is not valid (re-run the query to get a new one).");
            var b = BoundOf(d.Dataset);
            var ds = b.Dataset;
            var probe = new ExploreQuery(d.Dataset, d.Filters.Concat((narrow ?? Array.Empty<ExploreFilter>()).Where(f => f.Dimension != ExploreColumnKinds.BucketDimension)).ToList(),
                Array.Empty<string>(), new[] { ds.Measures[0].Id }, d.Bucket, d.TimeDimension, d.Lens, d.Zone);
            if (ExploreValidation.Validate(probe, new[] { ds }) is { } problem) throw ApiException.BadRequest(problem);
            var p = new SqlParts { Bound = b };
            string? bucketExpr = null;
            if (d.Bucket != TimeBucket.None)
            {
                string timeColumn = TimeColumnOf(ds, d.TimeDimension);
                bucketExpr = BucketCode(LocalMs(p, timeColumn, d.Lens, d.Zone), d.Bucket);
                // The same rows the aggregate counted: its null-time, time-quality and policy exclusions apply to the drill as well.
                p.Where.Add($"t.{timeColumn} IS NOT NULL");
                AddTimeQuality(p, d.Bucket);
            }
            foreach (var f in d.Filters) AddFilter(p, f);
            foreach (var f in narrow ?? Array.Empty<ExploreFilter>()) AddFilter(p, f, bucketExpr, d.Bucket);
            if (d.AsOfMs is long a) AddAsOf(p, a);
            ApplyPolicy(p, d.Bucket);
            return (p, d);
        }

        private static void RequireRecords(DatasetDescriptor ds, Func<string, bool> mayReadPermission)
        {
            if (!mayReadPermission(ds.RequiredPermission)) throw ApiException.Forbidden($"'{ds.Title}' needs the {ds.RequiredPermission} permission.");
            if (ds.RecordKind == "message" && !mayReadPermission("omniscience.messages.read")) throw ApiException.Forbidden("Message-level records need the Messages permission.");
        }

        public async Task<ExploreRecordsPage> RecordsAsync(ExploreRecordsRequest request, Func<string, bool> mayReadPermission, CancellationToken ct)
        {
            if (request.Limit is < 1 or > 1000) throw ApiException.BadRequest("Limit must be between 1 and 1000.");
            var (p, _) = DrillParts(request.DrillToken, request.Narrow);
            var b = p.Bound;
            var ds = b.Dataset;
            RequireRecords(ds, mayReadPermission);
            string where = p.WhereSql;
            var countParams = CloneParams(p.Params);
            string keyset = "";
            if (request.Cursor is { } cur)
            {
                // Newest first; records without a time come last, ordered by key alone ("n|key").
                int bar = cur.IndexOf('|');
                if (bar <= 0) throw ApiException.BadRequest("Malformed cursor.");
                string head = cur[..bar], tail = cur[(bar + 1)..];
                if (head == "n") keyset = $"(t.{b.Time} IS NULL AND t.{b.Key} < {p.Param(tail)})";
                else if (long.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out long at))
                    keyset = $"(t.{b.Time} < {p.Param(at)} OR (t.{b.Time} = {p.Param(at)} AND t.{b.Key} < {p.Param(tail)}) OR t.{b.Time} IS NULL)";
                else throw ApiException.BadRequest("Malformed cursor.");
                keyset = (where.Length == 0 ? "WHERE " : " AND ") + keyset;
            }
            var personCols = ds.Dimensions.Where(x => DimensionCatalog.Get(x.DimensionId).EntityType == "person").Select(x => x.Column).Distinct().ToList();
            var cols = TableColumns(b.Table);
            string sql = $"SELECT {string.Join(", ", cols.Select(c => "t." + c))} {p.From} {where}{keyset} ORDER BY t.{b.Time} DESC NULLS LAST, t.{b.Key} DESC LIMIT {(request.Limit + 1).ToString(CultureInfo.InvariantCulture)}";
            var rows = await RunAsync(sql, p.Params, cols.Count, ct).ConfigureAwait(false);
            var totalRow = (await RunAsync($"SELECT COUNT(*) {p.From} {where}", countParams, 1, ct).ConfigureAwait(false)).FirstOrDefault();
            long total = totalRow?[0].GetInt64() ?? 0;
            string? next = null;
            if (rows.Count > request.Limit)
            {
                rows.RemoveAt(rows.Count - 1);
                var last = rows[^1];
                var at = last[cols.IndexOf(b.Time)];
                next = (at.ValueKind == JsonValueKind.Number ? at.GetRawText() : "n") + "|" + last[cols.IndexOf(b.Key)].GetString();
            }
            var records = rows.Select(r => cols.Select((c, i) => (c, r[i])).ToDictionary(x => x.c, x => x.Item2)).ToList();
            // Person columns are shown canonical (merges resolved), and message records carry their text in context.
            var canon = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var rec in records)
                foreach (var pc in personCols)
                    if (rec.TryGetValue(pc, out var v) && v.ValueKind == JsonValueKind.String)
                        rec[pc] = JsonSerializer.SerializeToElement(canon.TryGetValue(v.GetString()!, out var c) ? c : canon[v.GetString()!] = Context.Policy.Canonical(v.GetString()!));
            if (ds.RecordKind == "message")
            {
                string idCol = cols.Contains("message_id") ? "message_id" : b.Key;
                var ids = records.Select(r => r[idCol].GetString()!).ToList();
                using var corpus = Context.Stores.Corpus.OpenRead();
                var text = corpus.QueryIn(null, "SELECT message_id, content FROM messages WHERE message_id IN ({keys})", ids, r => (r.GetString(0), r.GetStringOrNull(1)))
                    .ToDictionary(x => x.Item1, x => x.Item2);
                foreach (var rec in records) rec["content"] = JsonSerializer.SerializeToElement(text.GetValueOrDefault(rec[idCol].GetString()!));
            }
            return new ExploreRecordsPage(ds.RecordKind, records.Select(r => JsonSerializer.SerializeToElement(r)).ToList(), next, total);
        }

        /// <summary>The mirror table's columns that records show (internal bookkeeping columns start with an underscore).</summary>
        private List<string> TableColumns(string table)
        {
            using var conn = OpenReader();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT column_name FROM information_schema.columns WHERE table_name = $t ORDER BY ordinal_position";
            cmd.Parameters.Add(new DuckDBParameter("t", table));
            using var r = cmd.ExecuteReader();
            var cols = new List<string>();
            while (r.Read())
                if (!r.GetString(0).StartsWith('_')) cols.Add(r.GetString(0));
            return cols;
        }

        /// <summary>Top values of each requested dimension under the filters (filter pickers, plan D4 explore/facets).</summary>
        public async Task<ExploreFacetsDto> FacetsAsync(ExploreFacetsRequest request, Func<string, bool> mayReadPermission, CancellationToken ct)
        {
            var b = BoundOf(request.Dataset);
            var ds = b.Dataset;
            if (!mayReadPermission(ds.RequiredPermission)) throw ApiException.Forbidden($"'{ds.Title}' needs the {ds.RequiredPermission} permission.");
            if (request.Dimensions == null || request.Dimensions.Count is < 1 or > 12) throw ApiException.BadRequest("Ask for between 1 and 12 dimensions.");
            var probe = new ExploreQuery(ds.Id, request.Filters ?? Array.Empty<ExploreFilter>(), request.Dimensions, new[] { ds.Measures[0].Id });
            if (ExploreValidation.Validate(probe, new[] { ds }) is { } problem) throw ApiException.BadRequest(problem);
            var facets = new List<ExploreFacetDto>();
            foreach (var dim in request.Dimensions.Distinct())
            {
                var p = new SqlParts { Bound = b };
                foreach (var f in probe.Filters) AddFilter(p, f);
                ApplyPolicy(p, TimeBucket.None);
                string e = p.Expr(dim);
                string sql = $"SELECT {e}, COUNT(*) {p.From} {p.WhereSql} GROUP BY 1 ORDER BY 2 DESC, 1 LIMIT {Math.Clamp(request.Limit, 1, 200).ToString(CultureInfo.InvariantCulture)}";
                var rows = await RunAsync(sql, p.Params, 2, ct).ConfigureAwait(false);
                facets.Add(new ExploreFacetDto(dim, rows.Select(r => new ExploreFacetValueDto(r[0], r[1].GetInt64())).ToList()));
            }
            return new ExploreFacetsDto(ds.Id, facets, ReadState("as_of") ?? 0);
        }

        /// <summary>
        /// A complete export (F557: never silently capped) of the records behind a drill, or of an aggregate result, written by
        /// DuckDB itself (CSV, JSON lines or Parquet) to <paramref name="path"/>. Message text is not part of Explore exports:
        /// the mirror holds no text (the per-person export of N20 carries it). Person columns are exported canonical.
        /// </summary>
        public async Task<long> ExportAsync(ExploreExportRequest request, Func<string, bool> mayReadPermission, string path, CancellationToken ct)
        {
            if (request.DrillToken != null)
            {
                var (p, _) = DrillParts(request.DrillToken, null);
                var b = p.Bound;
                RequireRecords(b.Dataset, mayReadPermission);
                var personCols = b.Dataset.Dimensions.Where(x => DimensionCatalog.Get(x.DimensionId).EntityType == "person").ToList();
                var select = TableColumns(b.Table).Select(c => personCols.FirstOrDefault(x => x.Column == c) is { } pd ? $"{p.Expr(pd.DimensionId)} AS {c}" : "t." + c).ToList();
                string sql = $"SELECT {string.Join(", ", select)} {p.From} {p.WhereSql} ORDER BY t.{b.Time} DESC NULLS LAST, t.{b.Key} DESC";
                string format = request.Format switch { ExportFormat.Csv => "FORMAT CSV, HEADER", ExportFormat.Json => "FORMAT JSON", _ => "FORMAT PARQUET" };
                using var conn = OpenReader();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"COPY ({sql}) TO '{path.Replace("'", "''")}' ({format})";
                foreach (var x in p.Params) cmd.Parameters.Add(x);
                // Not under the query timeout: an export is asked for deliberately and may cover every record.
                return await Task.Run(() => Convert.ToInt64(cmd.ExecuteNonQuery()), ct).ConfigureAwait(false);
            }
            if (request.Query != null)
            {
                if (request.Format == ExportFormat.Parquet) throw ApiException.BadRequest("Aggregate results export as CSV or JSON; Parquet is for records (pass a drill token).");
                var result = await QueryAsync(request.Query with { Limit = MaxGroups, Cursor = null }, mayReadPermission, ct).ConfigureAwait(false);
                await using var w = new StreamWriter(path, false, new UTF8Encoding(false));
                if (request.Format == ExportFormat.Csv)
                {
                    await w.WriteLineAsync(string.Join(",", result.Columns.Select(c => Csv(c.Title)))).ConfigureAwait(false);
                    foreach (var r in result.Rows)
                        await w.WriteLineAsync(string.Join(",", r.Select(v => Csv(v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Null ? "" : v.GetRawText())))).ConfigureAwait(false);
                }
                else
                {
                    foreach (var r in result.Rows)
                        await w.WriteLineAsync(JsonSerializer.Serialize(result.Columns.Select((c, i) => (c.Id, r[i])).ToDictionary(x => x.Id, x => x.Item2))).ConfigureAwait(false);
                }
                return result.Rows.Count;
            }
            throw ApiException.BadRequest("Pass a drill token (records) or a query (aggregate).");
        }

        /// <summary>A CSV cell. Text that a spreadsheet would run as a formula is prefixed with an apostrophe (names and other
        /// corpus text can start with =, +, - or @).</summary>
        private static string Csv(string? s)
        {
            s ??= "";
            if (s.Length > 0 && s[0] is '=' or '+' or '@' || (s.Length > 1 && s[0] == '-' && !char.IsAsciiDigit(s[1]) && s[1] != '.')) s = "'" + s;
            return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
    }
}
