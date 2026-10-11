using System.Globalization;

namespace Omnipotent.Services.MemeScraper.Analytics
{
    /// <summary>The date window and filters every MemeScraper analytics route shares.</summary>
    public sealed class InsightsQuery
    {
        public const int DefaultDays = 30;

        /// <summary>First day (inclusive, in <see cref="TimeZone"/>). Overrides <see cref="Days"/>.</summary>
        public DateOnly? From { get; set; }
        /// <summary>Last day (inclusive). Defaults to today.</summary>
        public DateOnly? To { get; set; }
        /// <summary>The last N days ending on <see cref="To"/>; 0 = everything ever downloaded.</summary>
        public int? Days { get; set; }
        /// <summary>IANA or Windows id; days start at local midnight here. UTC when missing or unknown.</summary>
        public string? TimeZone { get; set; }
        /// <summary>day, week or month. Picked from the window's length when missing.</summary>
        public string? Granularity { get; set; }
        /// <summary>Usernames to restrict to (any case). Empty = every source.</summary>
        public List<string> Sources { get; set; } = new();
        public string? Niche { get; set; }

        /// <summary>
        /// from/to (yyyy-MM-dd), days or range (7d, 30d, 1y, all), tz, granularity, sources (comma list) and niche.
        /// </summary>
        public static InsightsQuery Parse(Func<string, string?> get)
        {
            var query = new InsightsQuery
            {
                From = ParseDay(get("from")),
                To = ParseDay(get("to")),
                TimeZone = Clean(get("tz")),
                Granularity = Clean(get("granularity"))?.ToLowerInvariant(),
                Niche = Clean(get("niche")),
            };
            if (int.TryParse(get("days"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int days))
            {
                query.Days = Math.Max(0, days);
            }
            else if (Clean(get("range"))?.ToLowerInvariant() is string range)
            {
                query.Days = range switch
                {
                    "all" => 0,
                    "1y" => 365,
                    _ when range.EndsWith('d') && int.TryParse(range[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int d) => Math.Max(0, d),
                    _ => null,
                };
            }
            string? sources = get("sources") ?? get("source");
            if (!string.IsNullOrWhiteSpace(sources))
            {
                query.Sources = sources.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(MemeScraperSources.NormalizeUsername)
                    .Where(s => s.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            return query;
        }

        internal static DateOnly? ParseDay(string? value) =>
            DateOnly.TryParseExact(value?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

        private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>Library listing: the shared window and filters plus search, sort and paging.</summary>
    public sealed class ReelQuery
    {
        public InsightsQuery Window { get; set; } = new();
        public string? Search { get; set; }
        public long? MinViews { get; set; }
        public string? Via { get; set; }
        /// <summary>downloaded, posted, views, likes, comments, engagement, age or owner.</summary>
        public string Sort { get; set; } = "downloaded";
        public bool Descending { get; set; } = true;
        public int Offset { get; set; }
        public int Limit { get; set; } = 48;

        public const int MaxLimit = 200;

        public static ReelQuery Parse(Func<string, string?> get)
        {
            var window = InsightsQuery.Parse(get);
            // The library's natural scope is everything; a range narrows it.
            if (window.From == null && window.Days == null && get("range") == null) window.Days = 0;
            var query = new ReelQuery
            {
                Window = window,
                Search = string.IsNullOrWhiteSpace(get("q")) ? null : get("q")!.Trim(),
                Via = string.IsNullOrWhiteSpace(get("via")) ? null : get("via")!.Trim().ToLowerInvariant(),
                Sort = (get("sort") ?? "downloaded").Trim().ToLowerInvariant(),
                Descending = !string.Equals(get("dir"), "asc", StringComparison.OrdinalIgnoreCase),
            };
            if (long.TryParse(get("minViews"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long minViews) && minViews > 0) query.MinViews = minViews;
            if (int.TryParse(get("offset"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int offset)) query.Offset = Math.Max(0, offset);
            if (int.TryParse(get("limit"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int limit)) query.Limit = Math.Clamp(limit, 1, MaxLimit);
            return query;
        }
    }

    /// <summary>Scrape history listing: the shared window plus outcome/trigger filters and paging.</summary>
    public sealed class ScrapeQuery
    {
        public InsightsQuery Window { get; set; } = new();
        /// <summary>all, ok, partial or failed.</summary>
        public string Status { get; set; } = "all";
        /// <summary>all, schedule or manual.</summary>
        public string Trigger { get; set; } = "all";
        public int Offset { get; set; }
        public int Limit { get; set; } = 50;

        public static ScrapeQuery Parse(Func<string, string?> get)
        {
            var query = new ScrapeQuery
            {
                Window = InsightsQuery.Parse(get),
                Status = (get("status") ?? "all").Trim().ToLowerInvariant(),
                Trigger = (get("trigger") ?? "all").Trim().ToLowerInvariant(),
            };
            if (int.TryParse(get("offset"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int offset)) query.Offset = Math.Max(0, offset);
            if (int.TryParse(get("limit"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int limit)) query.Limit = Math.Clamp(limit, 1, 500);
            return query;
        }
    }

    // ─────────────────────────────── Response shapes ───────────────────────────────

    public sealed class RangeView
    {
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public int Days { get; set; }
        public DateTime FromUtc { get; set; }
        /// <summary>Exclusive: local midnight after the last day.</summary>
        public DateTime ToUtc { get; set; }
        public bool AllTime { get; set; }
        /// <summary>False for "all time": there is no window before it to compare with.</summary>
        public bool HasBaseline { get; set; }
        public string? PrevFrom { get; set; }
        public string? PrevTo { get; set; }
        public string Granularity { get; set; } = "day";
        public string TimeZone { get; set; } = "UTC";
        public string Today { get; set; } = "";
    }

    public sealed class BucketView
    {
        /// <summary>Nominal start of the day/week/month (weeks start on Monday).</summary>
        public string Key { get; set; } = "";
        /// <summary>The part of the bucket inside the window.</summary>
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public int Days { get; set; }
        /// <summary>Cut by the window's edge or still in progress (contains today): not a full period.</summary>
        public bool Incomplete { get; set; }
        public int Downloads { get; set; }
        public long Views { get; set; }
        /// <summary>Library size (within the filters) at the end of the bucket.</summary>
        public int Cumulative { get; set; }
        /// <summary>Downloads in the matching bucket of the previous window; null without a baseline.</summary>
        public int? Previous { get; set; }
        /// <summary>Downloads per <see cref="DashboardView.StackKeys"/> entry.</summary>
        public int[] Stack { get; set; } = Array.Empty<int>();
        public int Scrapes { get; set; }
        public int ScrapeFailures { get; set; }
        public int NewReelsSeen { get; set; }
    }

    public sealed class KpiView
    {
        public int Downloaded { get; set; }
        public int? DownloadedPrev { get; set; }
        public double PerDay { get; set; }
        public double? PerDayPrev { get; set; }
        public int ActiveSources { get; set; }
        public int? ActiveSourcesPrev { get; set; }
        /// <summary>What <see cref="ActiveSources"/> is out of: every source within the filters, plus deleted ones whose reels are in the window.</summary>
        public int SourcesInScope { get; set; }
        public int ReelSources { get; set; }
        public long TotalViews { get; set; }
        public double? AvgViews { get; set; }
        public double? MedianViews { get; set; }
        public double? MedianViewsPrev { get; set; }
        public double? AvgLikes { get; set; }
        public double? AvgComments { get; set; }
        public double? MedianEngagementPct { get; set; }
        public double? MedianEngagementPctPrev { get; set; }
        /// <summary>How old a reel was (since Instagram posting) when it was downloaded.</summary>
        public double? MedianAgeHours { get; set; }
        public double? MedianAgeHoursPrev { get; set; }
        public int ViralReels { get; set; }
        public int Scrapes { get; set; }
        public int? ScrapesPrev { get; set; }
        public int ScrapeFailures { get; set; }
        public double? SuccessRate { get; set; }
        public double? SuccessRatePrev { get; set; }
        public double? MedianScrapeSeconds { get; set; }
        public int DownloadFailures { get; set; }
        public int LibraryAtStart { get; set; }
        public int LibraryAtEnd { get; set; }
        public int ZeroDays { get; set; }
        public int LongestGapDays { get; set; }
        public string? LongestGapFrom { get; set; }
        public string? LongestGapTo { get; set; }
        public string? BestDay { get; set; }
        public int BestDayCount { get; set; }
    }

    public sealed class HashtagView
    {
        public string Hashtag { get; set; } = "";
        public int Count { get; set; }
    }

    public sealed class SourceRow
    {
        public string Username { get; set; } = "";
        public string? AccountID { get; set; }
        public string? FullName { get; set; }
        public string? ProfilePictureUrl { get; set; }
        public string? Bio { get; set; }
        public int? Followers { get; set; }
        /// <summary>False for reels whose account is no longer a source (deleted, memes kept).</summary>
        public bool Registered { get; set; }
        public bool DownloadReels { get; set; }
        public bool DownloadPosts { get; set; }
        public List<string> Niches { get; set; } = new();
        public List<HashtagView> TopHashtags { get; set; } = new();
        public DateTime? AddedUtc { get; set; }
        public DateTime? ProfileUpdatedUtc { get; set; }
        public int Downloaded { get; set; }
        public int? DownloadedPrev { get; set; }
        /// <summary>Percent of the window's downloads.</summary>
        public double Share { get; set; }
        public int TotalReels { get; set; }
        public double? AvgViews { get; set; }
        public double? MedianViews { get; set; }
        public long? TopViews { get; set; }
        public double? MedianAgeHours { get; set; }
        public double? MedianEngagementPct { get; set; }
        public DateTime? FirstDownloadUtc { get; set; }
        public DateTime? LastDownloadUtc { get; set; }
        /// <summary>Downloads across the window, folded to at most <see cref="MemeScraperInsights.SparkPoints"/> points.</summary>
        public int[] Spark { get; set; } = Array.Empty<int>();
        public int Scrapes { get; set; }
        public int ScrapeFailures { get; set; }
        public double? AvgNewPerScrape { get; set; }
        public DateTime? NextScrapeDueUtc { get; set; }
        public DateTime? LastScrapeAttemptUtc { get; set; }
        public DateTime? LastSuccessfulScrapeUtc { get; set; }
        public int ConsecutiveScrapeFailures { get; set; }
        public string? LastScrapeError { get; set; }
        public string? LastScrapeSummary { get; set; }
        public int LastScrapeReelsFound { get; set; }
        public int LastScrapeReelsDownloaded { get; set; }
        public int LastScrapeDownloadFailures { get; set; }
        /// <summary>ok, quiet, idle, warn, bad, paused or removed.</summary>
        public string State { get; set; } = "ok";
        public string StateReason { get; set; } = "";
    }

    public sealed class NicheRow
    {
        public string Name { get; set; } = "";
        public int Sources { get; set; }
        public int Downloaded { get; set; }
        public int? DownloadedPrev { get; set; }
        public double Share { get; set; }
        public double? AvgViews { get; set; }
        public double? MedianViews { get; set; }
    }

    public sealed class DistributionBucket
    {
        public string Label { get; set; } = "";
        public int Count { get; set; }
        public double Share { get; set; }
        public double? AvgViews { get; set; }
    }

    public sealed class HeatCell
    {
        /// <summary>.NET DayOfWeek: 0 = Sunday.</summary>
        public int Dow { get; set; }
        public int Hour { get; set; }
        public int Reels { get; set; }
        public double AvgViews { get; set; }
    }

    public sealed class NamedCount
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
    }

    public sealed class ReelRow
    {
        public string PostID { get; set; } = "";
        public string? ShortCode { get; set; }
        public string Owner { get; set; } = "";
        public bool Registered { get; set; }
        public List<string> Niches { get; set; } = new();
        public long Views { get; set; }
        public long Likes { get; set; }
        public long Comments { get; set; }
        public double? EngagementPct { get; set; }
        public DateTime? PostedUtc { get; set; }
        public DateTime? DownloadedUtc { get; set; }
        public double? AgeHours { get; set; }
        public string? Description { get; set; }
        public string? Url { get; set; }
        public string? Via { get; set; }
        public bool? VideoExists { get; set; }
        public long? Bytes { get; set; }
    }

    public sealed class Insight
    {
        /// <summary>bad, warn, good or info.</summary>
        public string Level { get; set; } = "info";
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        public List<string> Sources { get; set; } = new();
    }

    public sealed class LibraryView
    {
        public int TotalReels { get; set; }
        public int MatchingReels { get; set; }
        public int Sources { get; set; }
        public int ReelSources { get; set; }
        public int PausedSources { get; set; }
        public int FailingSources { get; set; }
        public int Niches { get; set; }
        public DateTime? FirstDownloadUtc { get; set; }
        public DateTime? LastDownloadUtc { get; set; }
    }

    public sealed class FilterOptions
    {
        public List<string> Sources { get; set; } = new();
        public List<string> Niches { get; set; } = new();
    }

    public sealed class DashboardView
    {
        public DateTime GeneratedUtc { get; set; }
        public RangeView Range { get; set; } = new();
        public List<string> FilterSources { get; set; } = new();
        public string? FilterNiche { get; set; }
        public FilterOptions Options { get; set; } = new();
        public LibraryView Library { get; set; } = new();
        public KpiView Kpis { get; set; } = new();
        /// <summary>The window's biggest sources (each bucket's <see cref="BucketView.Stack"/> follows this order); "Other" last when present.</summary>
        public List<string> StackKeys { get; set; } = new();
        public List<BucketView> Series { get; set; } = new();
        public List<SourceRow> Sources { get; set; } = new();
        public List<NicheRow> Niches { get; set; } = new();
        public List<DistributionBucket> ViewsDistribution { get; set; } = new();
        public List<DistributionBucket> AgeDistribution { get; set; } = new();
        public List<HeatCell> Heatmap { get; set; } = new();
        public string? BusiestSlot { get; set; }
        public string? BestViewsSlot { get; set; }
        public List<NamedCount> DiscoveredVia { get; set; } = new();
        public List<NamedCount> ScrapeProviders { get; set; } = new();
        public List<ReelRow> TopReels { get; set; } = new();
        public List<Insight> Insights { get; set; } = new();
    }

    public sealed class ReelPage
    {
        public int Total { get; set; }
        public int Filtered { get; set; }
        public int Offset { get; set; }
        public int Limit { get; set; }
        public string Sort { get; set; } = "downloaded";
        public bool Descending { get; set; }
        public RangeView Range { get; set; } = new();
        public FilterOptions Options { get; set; } = new();
        public List<ReelRow> Rows { get; set; } = new();
    }

    public sealed class ScrapeRow
    {
        public string Username { get; set; } = "";
        public string? AccountID { get; set; }
        public string Trigger { get; set; } = "";
        public DateTime StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public long? DurationMs { get; set; }
        public int ReelsFound { get; set; }
        public int NewReels { get; set; }
        public int Downloaded { get; set; }
        public int DownloadFailures { get; set; }
        public string? Error { get; set; }
        public string? ProviderSummary { get; set; }
        public string? SucceededVia { get; set; }
        public List<string> Notes { get; set; } = new();
        /// <summary>ok, partial (some downloads failed) or failed.</summary>
        public string Status { get; set; } = "ok";
    }

    public sealed class ScrapeStats
    {
        public int Scrapes { get; set; }
        public int Failed { get; set; }
        public int Partial { get; set; }
        public double? SuccessRate { get; set; }
        public double? MedianSeconds { get; set; }
        public int ReelsSeen { get; set; }
        public int NewReels { get; set; }
        public int Downloaded { get; set; }
        public int DownloadFailures { get; set; }
    }

    public sealed class ScrapePage
    {
        public int Total { get; set; }
        public int Filtered { get; set; }
        public int Offset { get; set; }
        public int Limit { get; set; }
        public RangeView Range { get; set; } = new();
        public FilterOptions Options { get; set; } = new();
        public ScrapeStats Stats { get; set; } = new();
        public List<ScrapeRow> Rows { get; set; } = new();
    }

    public sealed class SummaryView
    {
        public int TotalInstagramSources { get; set; }
        public int ReelSources { get; set; }
        public int FailingSources { get; set; }
        public int TotalReelsDownloaded { get; set; }
        public int DownloadedToday { get; set; }
        public int Downloaded7d { get; set; }
        public int DownloadedPrev7d { get; set; }
        public DateTime? LastDownloadUtc { get; set; }
        /// <summary>Last 14 local days, keyed yyyy-MM-dd (the shape the old analytics route used).</summary>
        public Dictionary<string, int> MemesDownloadedPerDay { get; set; } = new();
    }
}
