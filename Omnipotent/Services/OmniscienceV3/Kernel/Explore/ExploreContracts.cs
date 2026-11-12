using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;

namespace Omnipotent.Services.OmniscienceV3.Kernel.Explore
{
    public enum FilterOp { Eq, NotEq, In, NotIn, Between, Gte, Lte, Contains, IsNull, IsNotNull }

    /// <summary>One filter on a catalogue dimension (D2). <see cref="Values"/>: one value for Eq/Gte/…, two for Between,
    /// any number for In/NotIn, none for IsNull/IsNotNull. Time values are UTC epoch ms.</summary>
    public sealed record ExploreFilter(string Dimension, FilterOp Op, IReadOnlyList<JsonElement> Values);

    /// <summary>Whose clock "hour of day" and "day" are read on (plan T4 time lens).</summary>
    public enum TimeLens { Klives, Subject, Utc }

    public enum TimeBucket { None, Hour, Day, Week, Month, Quarter, Year, HourOfDay, DayOfWeek }

    /// <summary>
    /// A bounded aggregate query over one module dataset (plan D4): filters on any declared dimension, grouping by
    /// dimensions and/or a time bucket on a lens, and the dataset's measures. The same contract drives every chart,
    /// table and export, so "Explain" can always show the exact query.
    /// </summary>
    public sealed record ExploreQuery(
        string Dataset,
        IReadOnlyList<ExploreFilter> Filters,
        IReadOnlyList<string> GroupBy,
        IReadOnlyList<string> Measures,
        TimeBucket Bucket = TimeBucket.None,
        string? TimeDimension = null,
        TimeLens Lens = TimeLens.Klives,
        string? Zone = null,
        long? AsOfMs = null,
        int Limit = 1000,
        string? Cursor = null);

    public sealed record ExploreColumn(string Id, string Title, string Kind);

    /// <summary>Which data a result covers (plan "Explain shows … coverage").</summary>
    public sealed record ExploreCoverage(IReadOnlyList<string> Sources, long? FromMs, long? ToMs, long Records, bool Sampled);

    /// <summary>A result: typed columns and rows, its as-of, coverage, a cursor for more rows, a drill token that
    /// re-selects the exact records behind it, and the query explanation.</summary>
    public sealed record ExploreResult(
        IReadOnlyList<ExploreColumn> Columns,
        IReadOnlyList<IReadOnlyList<JsonElement>> Rows,
        long AsOfMs,
        ExploreCoverage Coverage,
        string? NextCursor,
        string? DrillToken,
        string Explain);

    /// <summary>The records behind a result (or one row/point of it), shown in the drill-down drawer.</summary>
    public sealed record ExploreRecordsRequest(string DrillToken, IReadOnlyList<ExploreFilter>? Narrow = null, int Limit = 100, string? Cursor = null);

    public sealed record ExploreRecordsPage(string RecordKind, IReadOnlyList<JsonElement> Records, string? NextCursor, long Total);

    /// <summary>
    /// The Explore engine (implemented by the analytics workstream on the DuckDB mirror). Implementations must enforce
    /// the dataset's permission and MinorGuard filtering, bound every query (time and rows) and never scan the corpus
    /// per request.
    /// </summary>
    public interface IExploreEngine
    {
        Task<ExploreResult> QueryAsync(ExploreQuery query, Func<string, bool> mayReadPermission, CancellationToken ct);
        Task<ExploreRecordsPage> RecordsAsync(ExploreRecordsRequest request, Func<string, bool> mayReadPermission, CancellationToken ct);
    }

    /// <summary>
    /// How the Explore mirror carries a dataset a module declares in its manifest (plan D4 "self-describing": a new module's
    /// data is filterable the day it ships). <see cref="Table"/> is the intel.db table or view whose columns are the
    /// dataset's bound columns; <see cref="KeyColumn"/> identifies a record and <see cref="TimeColumn"/> orders records.
    /// With <see cref="ChangeColumn"/> — a number that only grows when a row is written (a sequence, a version, an
    /// updated-at) — the mirror follows the table incrementally. Without it the table is copied whole on every mirror
    /// pass, which is always correct and suits tables of up to a few hundred thousand rows.
    /// Column names must be plain identifiers (letters, digits, underscore).
    /// </summary>
    public sealed record ExploreSource(string DatasetId, string Table, string KeyColumn, string TimeColumn, string? ChangeColumn = null);

    /// <summary>Implemented by a module whose declared datasets the Explore mirror should carry.</summary>
    public interface IExploreSourceProvider
    {
        IReadOnlyList<ExploreSource> ExploreSources { get; }
    }

    /// <summary>The values of <see cref="ExploreColumn.Kind"/>: a requested measure, the time-bucket column (id
    /// <c>@bucket</c>), or a group-by dimension named by its <see cref="DimensionKind"/>.</summary>
    public static class ExploreColumnKinds
    {
        public const string Measure = "Measure";
        public const string Bucket = "Bucket";
        /// <summary>A group-by column of a time dimension (UTC epoch ms).</summary>
        public const string Time = nameof(DimensionKind.Time);
        public const string Entity = nameof(DimensionKind.Entity);
        public const string Category = nameof(DimensionKind.Category);
        public const string Number = nameof(DimensionKind.Number);
        public const string Flag = nameof(DimensionKind.Flag);

        /// <summary>The pseudo-dimension a drill narrows by to select one row of a bucketed result
        /// (<c>{"dimension":"@bucket","op":"Eq","values":[the row's bucket value]}</c>).</summary>
        public const string BucketDimension = "@bucket";
    }

    /// <summary>Checks a query against the declared datasets before any engine sees it.</summary>
    public static class ExploreValidation
    {
        public static string? Validate(ExploreQuery q, IReadOnlyList<DatasetDescriptor> datasets)
        {
            var ds = datasets.FirstOrDefault(d => d.Id == q.Dataset);
            if (ds == null) return $"No dataset '{q.Dataset}'.";
            var dims = ds.Dimensions.Select(d => d.DimensionId).ToHashSet(StringComparer.Ordinal);
            foreach (var f in q.Filters)
            {
                if (!dims.Contains(f.Dimension)) return $"Dataset '{ds.Id}' has no dimension '{f.Dimension}'.";
                int n = f.Values.Count;
                bool ok = f.Op switch
                {
                    FilterOp.IsNull or FilterOp.IsNotNull => n == 0,
                    FilterOp.Between => n == 2,
                    FilterOp.In or FilterOp.NotIn => n >= 1 && n <= 1000,
                    _ => n == 1,
                };
                if (!ok) return $"Filter on '{f.Dimension}' has the wrong number of values for {f.Op}.";
            }
            foreach (var g in q.GroupBy)
                if (!dims.Contains(g)) return $"Dataset '{ds.Id}' cannot group by '{g}'.";
            var measures = ds.Measures.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
            if (q.Measures.Count == 0) return "Ask for at least one measure.";
            foreach (var m in q.Measures)
                if (!measures.Contains(m)) return $"Dataset '{ds.Id}' has no measure '{m}'.";
            if (q.Bucket != TimeBucket.None)
            {
                string? time = q.TimeDimension ?? ds.Dimensions.Select(d => d.DimensionId).FirstOrDefault(id => DimensionCatalog.Get(id).Kind == DimensionKind.Time);
                if (time == null || !dims.Contains(time) || DimensionCatalog.Get(time).Kind != DimensionKind.Time)
                    return "Time buckets need a time dimension of this dataset.";
            }
            if (q.Lens == TimeLens.Subject && q.Zone != null) return "Choose either the subject's zone or an explicit zone, not both.";
            if (q.Zone != null && !Temporal.Zones.TryGet(q.Zone, out _)) return $"'{q.Zone}' is not an IANA time zone.";
            if (q.Limit is < 1 or > 10_000) return "Limit must be between 1 and 10000.";
            return null;
        }
    }
}
