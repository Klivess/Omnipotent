using System.Globalization;
using System.Text;
using Reel = Omnipotent.Services.MemeScraper.InstagramScrapeUtilities.InstagramReel;
using Report = Omnipotent.Services.MemeScraper.MemeScraper.ScrapeReport;
using Source = Omnipotent.Services.MemeScraper.MemeScraperSources.InstagramSource;

namespace Omnipotent.Services.MemeScraper.Analytics
{
    /// <summary>
    /// Every number on the MemeScraper pages, computed from the in-memory sources, reels and scrape log
    /// for one date window. Pure (no I/O, clock passed in) so the maths is testable, and one linear pass
    /// over the reels per request — the old analytics route rescanned every reel once per day and once per
    /// source and returned the whole library to the browser.
    /// </summary>
    public static class MemeScraperInsights
    {
        public const int StackSources = 6;
        public const int SparkPoints = 30;
        public const int TopReelCount = 12;
        public const int MaxDays = 3660;
        /// <summary>A source whose scrapes work but which has produced nothing for this long has gone quiet.</summary>
        public const int QuietAfterDays = 14;
        public const long ViralViews = 1_000_000;
        public const string OtherKey = "Other";
        public const string NoNiche = "(no niche)";

        // ─────────────────────────────── Window ───────────────────────────────

        public sealed class ResolvedRange
        {
            public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Utc;
            public string ZoneId { get; init; } = "UTC";
            public DateOnly From { get; init; }
            public DateOnly To { get; init; }
            public DateOnly Today { get; init; }
            public int Days => To.DayNumber - From.DayNumber + 1;
            public bool AllTime { get; init; }
            public bool HasBaseline => !AllTime;
            public DateOnly PrevTo => From.AddDays(-1);
            public DateOnly PrevFrom => From.AddDays(-Days);
            public string Granularity { get; init; } = "day";

            public bool Contains(DateOnly day) => day >= From && day <= To;
            public bool ContainsPrevious(DateOnly day) => HasBaseline && day >= PrevFrom && day <= PrevTo;

            public int BucketIndex(DateOnly day) => Granularity switch
            {
                "week" => (MondayOf(day).DayNumber - MondayOf(From).DayNumber) / 7,
                "month" => (day.Year * 12 + day.Month) - (From.Year * 12 + From.Month),
                _ => day.DayNumber - From.DayNumber,
            };

            public RangeView ToView() => new()
            {
                From = Fmt(From),
                To = Fmt(To),
                Days = Days,
                FromUtc = LocalMidnightUtc(From, Zone),
                ToUtc = LocalMidnightUtc(To.AddDays(1), Zone),
                AllTime = AllTime,
                HasBaseline = HasBaseline,
                PrevFrom = HasBaseline ? Fmt(PrevFrom) : null,
                PrevTo = HasBaseline ? Fmt(PrevTo) : null,
                Granularity = Granularity,
                TimeZone = ZoneId,
                Today = Fmt(Today),
            };
        }

        public static ResolvedRange ResolveRange(InsightsQuery query, DateTime nowUtc, DateOnly? firstDay = null)
        {
            var (zone, zoneId) = ResolveZone(query.TimeZone);
            var today = LocalDay(nowUtc, zone);
            DateOnly to = Min(query.To ?? today, today);
            bool allTime = false;
            DateOnly from;
            if (query.From is DateOnly explicitFrom)
            {
                from = Min(explicitFrom, today);
            }
            else if (query.Days == 0)
            {
                allTime = true;
                from = Min(firstDay ?? to, to);
            }
            else
            {
                from = to.AddDays(-Math.Clamp(query.Days ?? InsightsQuery.DefaultDays, 1, MaxDays) + 1);
            }
            if (from > to) (from, to) = (to, from);
            if (to.DayNumber - from.DayNumber + 1 > MaxDays) from = to.AddDays(-MaxDays + 1);

            int days = to.DayNumber - from.DayNumber + 1;
            string granularity = query.Granularity switch
            {
                "day" when days <= 1100 => "day",
                "day" or "week" => "week",
                "month" => "month",
                _ => days <= 92 ? "day" : days <= 731 ? "week" : "month",
            };
            return new ResolvedRange { Zone = zone, ZoneId = zoneId, From = from, To = to, Today = today, AllTime = allTime, Granularity = granularity };
        }

        // ─────────────────────────────── Facts ───────────────────────────────

        /// <summary>One reel, resolved once per request: its source, local download day and derived ratios.</summary>
        internal sealed class ReelFact
        {
            public Reel Reel = null!;
            public string Key = "";
            public string Owner = "";
            public Source? Source;
            public DateTime? DownloadedUtc;
            public DateOnly? Day;
            public DateTime? PostedUtc;
            public double? AgeHours;
            public long Views;
            public long Likes;
            public long Comments;
            public double? EngagementPct;
            public string Via = "";
            public List<string> Niches = new();
        }

        internal sealed class Universe
        {
            /// <summary>Every reel after the source/niche filters.</summary>
            public List<ReelFact> Reels = new();
            public int Unfiltered;
            /// <summary>Registered sources after the filters, by lower-case username.</summary>
            public Dictionary<string, Source> Sources = new(StringComparer.Ordinal);
            public List<Source> AllSources = new();
            public Func<Report, Source?> SourceOfReport = _ => null;
            public Func<Report, bool> ReportMatches = _ => true;
        }

        internal static Universe BuildUniverse(IEnumerable<Source> sources, IEnumerable<Reel> reels, InsightsQuery query, TimeZoneInfo zone)
        {
            var all = sources.Where(s => s != null && !string.IsNullOrWhiteSpace(s.Username)).ToList();
            var byAccount = new Dictionary<string, Source>(StringComparer.Ordinal);
            var byUser = new Dictionary<string, Source>(StringComparer.Ordinal);
            // Sources compare by reference; resolved once instead of once per reel.
            var nichesOf = new Dictionary<Source, List<string>>(ReferenceEqualityComparer.Instance);
            foreach (var s in all)
            {
                if (!string.IsNullOrEmpty(s.AccountID)) byAccount.TryAdd(s.AccountID, s);
                byUser.TryAdd(s.Username.ToLowerInvariant(), s);
                nichesOf.TryAdd(s, NicheNames(s));
            }
            Source? resolve(string? accountId, string? username) =>
                !string.IsNullOrEmpty(accountId) && byAccount.TryGetValue(accountId, out var a) ? a
                : !string.IsNullOrEmpty(username) && byUser.TryGetValue(username.ToLowerInvariant(), out var u) ? u
                : null;
            List<string> niches(Source? source) => source != null && nichesOf.TryGetValue(source, out var list) ? list : new List<string>();

            var wanted = query.Sources.Count > 0 ? new HashSet<string>(query.Sources.Select(s => s.ToLowerInvariant()), StringComparer.Ordinal) : null;
            string? niche = query.Niche;
            bool keep(string key, Source? source) =>
                (wanted == null || wanted.Contains(key))
                && (niche == null || (niche == NoNiche
                    ? source != null && niches(source).Count == 0
                    : source != null && niches(source).Contains(niche, StringComparer.OrdinalIgnoreCase)));

            var universe = new Universe { AllSources = all };
            foreach (var s in all)
            {
                string key = s.Username.ToLowerInvariant();
                if (keep(key, s)) universe.Sources.TryAdd(key, s);
            }

            foreach (var reel in reels)
            {
                if (reel == null) continue;
                universe.Unfiltered++;
                var source = resolve(reel.OwnerID, reel.OwnerUsername);
                string owner = source?.Username ?? (string.IsNullOrWhiteSpace(reel.OwnerUsername) ? "unknown" : reel.OwnerUsername);
                string key = owner.ToLowerInvariant();
                if (!keep(key, source)) continue;

                var fact = new ReelFact
                {
                    Reel = reel,
                    Key = key,
                    Owner = owner,
                    Source = source,
                    DownloadedUtc = LocalishToUtc(reel.DateTimeReelDownloaded),
                    PostedUtc = UtcishToUtc(reel.CreatedAt),
                    Views = Math.Max(0, reel.ViewCount),
                    Likes = Math.Max(0, reel.LikeCount),
                    Comments = Math.Max(0, reel.CommentCount),
                    Via = string.IsNullOrWhiteSpace(reel.DiscoveredVia) ? "unknown" : reel.DiscoveredVia!,
                    Niches = niches(source),
                };
                if (fact.DownloadedUtc is DateTime downloaded)
                {
                    fact.Day = LocalDay(downloaded, zone);
                    if (fact.PostedUtc is DateTime posted && downloaded >= posted) fact.AgeHours = (downloaded - posted).TotalHours;
                }
                // Likes are missing from some providers (0); a 0% engagement from absent data would be a lie.
                if (fact.Views > 0 && fact.Likes > 0) fact.EngagementPct = (fact.Likes + fact.Comments) * 100.0 / fact.Views;
                universe.Reels.Add(fact);
            }

            universe.SourceOfReport = report => resolve(report.AccountID, report.Username);
            universe.ReportMatches = report =>
            {
                var source = universe.SourceOfReport(report);
                string key = (source?.Username ?? report.Username ?? "").ToLowerInvariant();
                return keep(key, source);
            };
            return universe;
        }

        // ─────────────────────────────── Dashboard ───────────────────────────────

        public static DashboardView BuildDashboard(IEnumerable<Source> sources, IEnumerable<Reel> reels, IEnumerable<Report> history,
            InsightsQuery query, DateTime nowUtc, int scrapeIntervalHours = 24)
        {
            var zone = ResolveZone(query.TimeZone).Zone;
            var universe = BuildUniverse(sources, reels, query, zone);
            var firstDay = universe.Reels.Where(f => f.Day.HasValue).Select(f => f.Day!.Value).DefaultIfEmpty(LocalDay(nowUtc, zone)).Min();
            var range = ResolveRange(query, nowUtc, firstDay);
            var buckets = BuildBuckets(range);
            int n = buckets.Count;

            var cur = universe.Reels.Where(f => f.Day is DateOnly d && range.Contains(d)).ToList();
            var prev = range.HasBaseline ? universe.Reels.Where(f => f.Day is DateOnly d && range.ContainsPrevious(d)).ToList() : new List<ReelFact>();
            var reports = history.Where(r => r != null).ToList();
            var curReports = reports.Where(r => universe.ReportMatches(r) && range.Contains(LocalDay(AsUtc(r.StartedUtc), zone))).ToList();
            var prevReports = range.HasBaseline
                ? reports.Where(r => universe.ReportMatches(r) && range.ContainsPrevious(LocalDay(AsUtc(r.StartedUtc), zone))).ToList()
                : new List<Report>();

            var view = new DashboardView
            {
                GeneratedUtc = nowUtc,
                Range = range.ToView(),
                FilterSources = query.Sources,
                FilterNiche = query.Niche,
                Options = OptionsOf(universe),
            };

            // Library context (all time, within the filters).
            var registered = universe.Sources.Values.ToList();
            view.Library = new LibraryView
            {
                TotalReels = universe.Unfiltered,
                MatchingReels = universe.Reels.Count,
                Sources = registered.Count,
                ReelSources = registered.Count(s => s.DownloadReels),
                PausedSources = registered.Count(s => !s.DownloadReels),
                FailingSources = registered.Count(s => s.DownloadReels && s.ConsecutiveScrapeFailures > 0),
                Niches = registered.SelectMany(NicheNames).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                FirstDownloadUtc = universe.Reels.Select(f => f.DownloadedUtc).Where(d => d.HasValue).Min(),
                LastDownloadUtc = universe.Reels.Select(f => f.DownloadedUtc).Where(d => d.HasValue).Max(),
            };

            // ── Series ──
            var stackKeys = cur.GroupBy(f => f.Key)
                .Select(g => (Key: g.Key, Owner: g.First().Owner, Count: g.Count()))
                .OrderByDescending(g => g.Count).ThenBy(g => g.Key, StringComparer.Ordinal)
                .ToList();
            var stacked = stackKeys.Take(StackSources).ToList();
            bool hasOther = stackKeys.Count > StackSources;
            view.StackKeys = stacked.Select(s => s.Owner).ToList();
            if (hasOther) view.StackKeys.Add(OtherKey);
            var stackIndex = stacked.Select((s, i) => (s.Key, i)).ToDictionary(x => x.Key, x => x.i, StringComparer.Ordinal);
            int stackWidth = view.StackKeys.Count;

            var series = buckets.Select(b => new BucketView
            {
                Key = b.Key, From = b.From, To = b.To, Days = b.Days, Incomplete = b.Incomplete,
                Stack = new int[stackWidth],
                Previous = range.HasBaseline ? 0 : null,
            }).ToList();

            foreach (var f in cur)
            {
                int i = range.BucketIndex(f.Day!.Value);
                if (i < 0 || i >= n) continue;
                var bucket = series[i];
                bucket.Downloads++;
                bucket.Views += f.Views;
                if (stackWidth > 0) bucket.Stack[stackIndex.TryGetValue(f.Key, out int s) ? s : stackWidth - 1]++;
            }
            foreach (var f in prev)
            {
                // The previous window laid over this one, day for day.
                int i = range.BucketIndex(f.Day!.Value.AddDays(range.Days));
                if (i >= 0 && i < n) series[i].Previous = (series[i].Previous ?? 0) + 1;
            }
            int running = universe.Reels.Count(f => f.Day is DateOnly d && d < range.From);
            int libraryAtStart = running;
            foreach (var bucket in series)
            {
                running += bucket.Downloads;
                bucket.Cumulative = running;
            }
            foreach (var r in curReports)
            {
                int i = range.BucketIndex(LocalDay(AsUtc(r.StartedUtc), zone));
                if (i < 0 || i >= n) continue;
                series[i].Scrapes++;
                if (!string.IsNullOrEmpty(r.Error)) series[i].ScrapeFailures++;
                series[i].NewReelsSeen += r.NewReels;
            }
            view.Series = series;

            // ── KPIs ──
            var dayCounts = new int[range.Days];
            foreach (var f in cur) dayCounts[f.Day!.Value.DayNumber - range.From.DayNumber]++;
            var k = new KpiView
            {
                Downloaded = cur.Count,
                DownloadedPrev = range.HasBaseline ? prev.Count : null,
                PerDay = cur.Count / (double)range.Days,
                PerDayPrev = range.HasBaseline ? prev.Count / (double)range.Days : null,
                ActiveSources = cur.Select(f => f.Key).Distinct().Count(),
                ActiveSourcesPrev = range.HasBaseline ? prev.Select(f => f.Key).Distinct().Count() : null,
                SourcesInScope = universe.Sources.Count + cur.Select(f => f.Key).Distinct().Count(key => !universe.Sources.ContainsKey(key)),
                ReelSources = view.Library.ReelSources,
                TotalViews = cur.Sum(f => f.Views),
                AvgViews = cur.Count > 0 ? cur.Average(f => (double)f.Views) : null,
                MedianViews = Median(cur.Select(f => (double)f.Views)),
                MedianViewsPrev = range.HasBaseline ? Median(prev.Select(f => (double)f.Views)) : null,
                AvgLikes = cur.Any(f => f.Likes > 0) ? cur.Where(f => f.Likes > 0).Average(f => (double)f.Likes) : null,
                AvgComments = cur.Count > 0 ? cur.Average(f => (double)f.Comments) : null,
                MedianEngagementPct = Median(cur.Where(f => f.EngagementPct.HasValue).Select(f => f.EngagementPct!.Value)),
                MedianEngagementPctPrev = range.HasBaseline ? Median(prev.Where(f => f.EngagementPct.HasValue).Select(f => f.EngagementPct!.Value)) : null,
                MedianAgeHours = Median(cur.Where(f => f.AgeHours.HasValue).Select(f => f.AgeHours!.Value)),
                MedianAgeHoursPrev = range.HasBaseline ? Median(prev.Where(f => f.AgeHours.HasValue).Select(f => f.AgeHours!.Value)) : null,
                ViralReels = cur.Count(f => f.Views >= ViralViews),
                Scrapes = curReports.Count,
                ScrapesPrev = range.HasBaseline ? prevReports.Count : null,
                ScrapeFailures = curReports.Count(r => !string.IsNullOrEmpty(r.Error)),
                SuccessRate = SuccessRate(curReports),
                SuccessRatePrev = range.HasBaseline ? SuccessRate(prevReports) : null,
                MedianScrapeSeconds = Median(curReports.Where(r => r.FinishedUtc.HasValue).Select(r => (AsUtc(r.FinishedUtc!.Value) - AsUtc(r.StartedUtc)).TotalSeconds).Where(s => s >= 0)),
                DownloadFailures = curReports.Sum(r => r.DownloadFailures),
                LibraryAtStart = libraryAtStart,
                LibraryAtEnd = running,
            };
            // Days that are over; today with nothing yet is not a gap.
            int completeDays = range.To >= range.Today ? range.Days - 1 : range.Days;
            int gap = 0, gapStart = -1;
            for (int i = 0; i < completeDays; i++)
            {
                if (dayCounts[i] == 0)
                {
                    k.ZeroDays++;
                    if (gap == 0) gapStart = i;
                    gap++;
                    if (gap > k.LongestGapDays)
                    {
                        k.LongestGapDays = gap;
                        k.LongestGapFrom = Fmt(range.From.AddDays(gapStart));
                        k.LongestGapTo = Fmt(range.From.AddDays(i));
                    }
                }
                else gap = 0;
            }
            int best = -1;
            for (int i = 0; i < dayCounts.Length; i++) if (dayCounts[i] > 0 && (best < 0 || dayCounts[i] > dayCounts[best])) best = i;
            if (best >= 0)
            {
                k.BestDay = Fmt(range.From.AddDays(best));
                k.BestDayCount = dayCounts[best];
            }
            view.Kpis = k;

            // ── Sources ──
            var curByKey = cur.GroupBy(f => f.Key).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var prevByKey = prev.GroupBy(f => f.Key).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var allByKey = universe.Reels.GroupBy(f => f.Key).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var reportsByKey = curReports.GroupBy(r => (universe.SourceOfReport(r)?.Username ?? r.Username ?? "").ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var rowKeys = universe.Sources.Keys.Union(curByKey.Keys).ToList();
            int sparkWidth = Math.Min(n, SparkPoints);
            foreach (string key in rowKeys)
            {
                universe.Sources.TryGetValue(key, out var source);
                var inRange = curByKey.TryGetValue(key, out var c) ? c : new List<ReelFact>();
                var ever = allByKey.TryGetValue(key, out var e) ? e : new List<ReelFact>();
                var ranReports = reportsByKey.TryGetValue(key, out var rr) ? rr : new List<Report>();
                var spark = new int[sparkWidth];
                if (sparkWidth > 0)
                {
                    foreach (var f in inRange)
                    {
                        int i = range.BucketIndex(f.Day!.Value);
                        if (i >= 0 && i < n) spark[(int)((long)i * sparkWidth / n)]++;
                    }
                }
                var row = new SourceRow
                {
                    Username = source?.Username ?? (inRange.FirstOrDefault()?.Owner ?? key),
                    Registered = source != null,
                    Downloaded = inRange.Count,
                    DownloadedPrev = range.HasBaseline ? (prevByKey.TryGetValue(key, out int p) ? p : 0) : null,
                    Share = cur.Count > 0 ? inRange.Count * 100.0 / cur.Count : 0,
                    TotalReels = ever.Count,
                    AvgViews = inRange.Count > 0 ? inRange.Average(f => (double)f.Views) : null,
                    MedianViews = Median(inRange.Select(f => (double)f.Views)),
                    TopViews = inRange.Count > 0 ? inRange.Max(f => f.Views) : null,
                    MedianAgeHours = Median(inRange.Where(f => f.AgeHours.HasValue).Select(f => f.AgeHours!.Value)),
                    MedianEngagementPct = Median(inRange.Where(f => f.EngagementPct.HasValue).Select(f => f.EngagementPct!.Value)),
                    FirstDownloadUtc = ever.Select(f => f.DownloadedUtc).Where(d => d.HasValue).Min(),
                    LastDownloadUtc = ever.Select(f => f.DownloadedUtc).Where(d => d.HasValue).Max(),
                    Spark = spark,
                    Scrapes = ranReports.Count,
                    ScrapeFailures = ranReports.Count(r => !string.IsNullOrEmpty(r.Error)),
                    AvgNewPerScrape = ranReports.Count > 0 ? ranReports.Average(r => (double)r.NewReels) : null,
                };
                if (source != null)
                {
                    lock (source)
                    {
                        row.AccountID = source.AccountID;
                        row.FullName = source.FullName;
                        row.ProfilePictureUrl = source.ProfilePictureUrl;
                        row.Bio = Truncate(source.Bio, 400);
                        row.Followers = source.Followers;
                        row.DownloadReels = source.DownloadReels;
                        row.DownloadPosts = source.DownloadPosts;
                        row.Niches = NicheNames(source);
                        row.TopHashtags = (source.AccountTopHashtags ?? new()).OrderByDescending(h => h.Count).Take(8)
                            .Select(h => new HashtagView { Hashtag = h.Hashtag, Count = h.Count }).ToList();
                        row.AddedUtc = LocalishToUtc(source.DateTimeAdded);
                        row.ProfileUpdatedUtc = LocalishToUtc(source.LastUpdated);
                        row.NextScrapeDueUtc = source.NextScrapeDueUtc;
                        row.LastScrapeAttemptUtc = source.LastScrapeAttemptUtc;
                        row.LastSuccessfulScrapeUtc = source.LastSuccessfulScrapeUtc;
                        row.ConsecutiveScrapeFailures = source.ConsecutiveScrapeFailures;
                        row.LastScrapeError = source.LastScrapeError;
                        row.LastScrapeSummary = source.LastScrapeSummary;
                        row.LastScrapeReelsFound = source.LastScrapeReelsFound;
                        row.LastScrapeReelsDownloaded = source.LastScrapeReelsDownloaded;
                        row.LastScrapeDownloadFailures = source.LastScrapeDownloadFailures;
                    }
                }
                (row.State, row.StateReason) = SourceState(row, nowUtc);
                view.Sources.Add(row);
            }
            view.Sources = view.Sources
                .OrderByDescending(s => s.Downloaded)
                .ThenByDescending(s => s.TotalReels)
                .ThenBy(s => s.Username, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // ── Niches ──
            var nicheNames = registered.SelectMany(NicheNames).Concat(cur.SelectMany(f => f.Niches))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (string name in nicheNames)
            {
                var inNiche = cur.Where(f => f.Niches.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
                view.Niches.Add(new NicheRow
                {
                    Name = name,
                    Sources = registered.Count(s => NicheNames(s).Contains(name, StringComparer.OrdinalIgnoreCase)),
                    Downloaded = inNiche.Count,
                    DownloadedPrev = range.HasBaseline ? prev.Count(f => f.Niches.Contains(name, StringComparer.OrdinalIgnoreCase)) : null,
                    Share = cur.Count > 0 ? inNiche.Count * 100.0 / cur.Count : 0,
                    AvgViews = inNiche.Count > 0 ? inNiche.Average(f => (double)f.Views) : null,
                    MedianViews = Median(inNiche.Select(f => (double)f.Views)),
                });
            }
            var unsorted = cur.Where(f => f.Niches.Count == 0).ToList();
            if (unsorted.Count > 0 || registered.Any(s => NicheNames(s).Count == 0))
            {
                view.Niches.Add(new NicheRow
                {
                    Name = NoNiche,
                    Sources = registered.Count(s => NicheNames(s).Count == 0),
                    Downloaded = unsorted.Count,
                    DownloadedPrev = range.HasBaseline ? prev.Count(f => f.Niches.Count == 0) : null,
                    Share = cur.Count > 0 ? unsorted.Count * 100.0 / cur.Count : 0,
                    AvgViews = unsorted.Count > 0 ? unsorted.Average(f => (double)f.Views) : null,
                    MedianViews = Median(unsorted.Select(f => (double)f.Views)),
                });
            }
            view.Niches = view.Niches.OrderByDescending(x => x.Downloaded).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();

            // ── Distributions ──
            view.ViewsDistribution = Distribute(cur, f => f.Views, new (string, long, long)[]
            {
                ("No views", 0, 1), ("1–999", 1, 1_000), ("1k–10k", 1_000, 10_000), ("10k–100k", 10_000, 100_000),
                ("100k–1M", 100_000, 1_000_000), ("1M–10M", 1_000_000, 10_000_000), ("10M+", 10_000_000, long.MaxValue),
            });
            view.AgeDistribution = DistributeAge(cur);

            // ── When sources post (local time) ──
            var cells = new Dictionary<(int, int), (int Count, long Views)>();
            foreach (var f in cur)
            {
                if (f.PostedUtc is not DateTime posted) continue;
                var local = TimeZoneInfo.ConvertTimeFromUtc(posted, zone);
                var cell = ((int)local.DayOfWeek, local.Hour);
                cells.TryGetValue(cell, out var acc);
                cells[cell] = (acc.Count + 1, acc.Views + f.Views);
            }
            view.Heatmap = cells.Select(c => new HeatCell { Dow = c.Key.Item1, Hour = c.Key.Item2, Reels = c.Value.Count, AvgViews = Math.Round(c.Value.Views / (double)c.Value.Count) })
                .OrderBy(c => c.Dow).ThenBy(c => c.Hour).ToList();
            int postedReels = view.Heatmap.Sum(c => c.Reels);
            var busiest = view.Heatmap.OrderByDescending(c => c.Reels).ThenByDescending(c => c.AvgViews).FirstOrDefault();
            if (busiest != null && busiest.Reels >= Math.Max(3, postedReels * 0.02)) view.BusiestSlot = SlotLabel(busiest);
            int minimumForViews = Math.Max(3, (int)Math.Ceiling(postedReels * 0.01));
            var bestViews = view.Heatmap.Where(c => c.Reels >= minimumForViews).OrderByDescending(c => c.AvgViews).FirstOrDefault();
            if (bestViews != null) view.BestViewsSlot = SlotLabel(bestViews);

            // ── Providers ──
            view.DiscoveredVia = cur.GroupBy(f => f.Via).Select(g => new NamedCount { Name = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count).ToList();
            view.ScrapeProviders = curReports.Where(r => string.IsNullOrEmpty(r.Error))
                .GroupBy(r => string.IsNullOrEmpty(r.SucceededVia) ? "unknown" : r.SucceededVia!)
                .Select(g => new NamedCount { Name = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count).ToList();

            view.TopReels = cur.OrderByDescending(f => f.Views).ThenByDescending(f => f.Likes).Take(TopReelCount).Select(f => ToRow(f, 220)).ToList();
            view.Insights = BuildInsights(view, range, cur, prev, universe, nowUtc, scrapeIntervalHours);
            return view;
        }

        private static FilterOptions OptionsOf(Universe universe) => new()
        {
            Sources = universe.AllSources.Select(s => s.Username).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
            Niches = universe.AllSources.SelectMany(NicheNames).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
        };

        private static (string State, string Reason) SourceState(SourceRow row, DateTime nowUtc)
        {
            if (!row.Registered) return ("removed", "No longer a source; its reels are kept.");
            if (!row.DownloadReels) return ("paused", "Reel downloads are switched off for this source.");
            if (row.ConsecutiveScrapeFailures >= MemeScraper.FailuresBeforeAlert)
                return ("bad", $"{row.ConsecutiveScrapeFailures} failed scrapes in a row: {row.LastScrapeError}");
            if (row.ConsecutiveScrapeFailures > 0) return ("warn", $"Last scrape failed: {row.LastScrapeError}");
            if (row.LastScrapeDownloadFailures > 0) return ("warn", $"{row.LastScrapeDownloadFailures} download(s) failed last time; they are retried next scrape.");
            if (row.LastSuccessfulScrapeUtc == null) return ("idle", "Not scraped yet.");
            if (row.LastDownloadUtc is DateTime last && nowUtc - last > TimeSpan.FromDays(QuietAfterDays))
                return ("quiet", $"Scrapes work, but no new reel for {(int)(nowUtc - last).TotalDays} days: the account has gone quiet.");
            if (row.LastDownloadUtc == null) return ("quiet", "Scrapes work, but no reel has been downloaded from this account yet.");
            return ("ok", "Scraping normally.");
        }

        // ─────────────────────────────── Insights ───────────────────────────────

        private static List<Insight> BuildInsights(DashboardView view, ResolvedRange range, List<ReelFact> cur, List<ReelFact> prev,
            Universe universe, DateTime nowUtc, int intervalHours)
        {
            var list = new List<Insight>();
            var k = view.Kpis;
            string window = range.AllTime ? "since the first download" : range.To >= range.Today ? $"in the last {Plural(range.Days, "day")}" : $"between {Pretty(range.From)} and {Pretty(range.To)}";
            string previousWindow = $"the previous {Plural(range.Days, "day")}";

            // Volume.
            if (universe.Reels.Count == 0)
            {
                list.Add(new Insight { Level = "info", Title = "No reels yet", Detail = "Nothing has been downloaded. Add a source, or run a scrape from the Health tab." });
            }
            else if (cur.Count == 0)
            {
                var last = view.Library.LastDownloadUtc;
                list.Add(new Insight
                {
                    Level = range.Days >= 2 ? "warn" : "info",
                    Title = $"No reels downloaded {window}",
                    Detail = last.HasValue ? $"The last reel arrived {Ago(nowUtc - last.Value)} ago." : "No reel has a download date.",
                });
            }
            else if (range.HasBaseline && prev.Count > 0)
            {
                double change = (cur.Count - prev.Count) * 100.0 / prev.Count;
                if (change >= 25)
                    list.Add(new Insight { Level = "good", Title = $"Downloads up {change:0}%", Detail = $"{cur.Count:n0} reels {window}, against {prev.Count:n0} in {previousWindow}." });
                else if (change <= -25)
                    list.Add(new Insight { Level = "warn", Title = $"Downloads down {-change:0}%", Detail = $"{cur.Count:n0} reels {window}, against {prev.Count:n0} in {previousWindow}." });
            }
            else if (range.HasBaseline && prev.Count == 0 && cur.Count > 0 && universe.Reels.Any(f => f.Day < range.PrevFrom))
            {
                list.Add(new Insight { Level = "good", Title = "Downloads resumed", Detail = $"{cur.Count:n0} reels {window} after none in {previousWindow}." });
            }

            // Sources that need a person.
            var reelSources = view.Sources.Where(s => s.Registered && s.DownloadReels).ToList();
            var failing = reelSources.Where(s => s.State == "bad").ToList();
            if (failing.Count > 0)
            {
                list.Add(new Insight
                {
                    Level = "bad",
                    Title = failing.Count == 1 ? $"@{failing[0].Username} keeps failing" : $"{failing.Count} sources keep failing",
                    Detail = failing.Count == 1
                        ? $"{failing[0].ConsecutiveScrapeFailures} scrapes in a row: {Truncate(failing[0].LastScrapeError, 160)}"
                        : $"{string.Join(", ", failing.Take(4).Select(s => "@" + s.Username))}{(failing.Count > 4 ? $" and {failing.Count - 4} more" : "")} have failed {MemeScraper.FailuresBeforeAlert}+ scrapes in a row. Run a provider check on the Health tab.",
                    Sources = failing.Select(s => s.Username).ToList(),
                });
            }
            var quiet = reelSources.Where(s => s.State == "quiet" && s.LastDownloadUtc.HasValue).OrderBy(s => s.LastDownloadUtc).ToList();
            if (quiet.Count > 0)
            {
                list.Add(new Insight
                {
                    Level = "warn",
                    Title = quiet.Count == 1 ? $"@{quiet[0].Username} has gone quiet" : $"{quiet.Count} sources have gone quiet",
                    Detail = (quiet.Count == 1
                        ? $"No new reel for {(int)(nowUtc - quiet[0].LastDownloadUtc!.Value).TotalDays} days, though its scrapes succeed."
                        : $"{string.Join(", ", quiet.Take(4).Select(s => $"@{s.Username} ({(int)(nowUtc - s.LastDownloadUtc!.Value).TotalDays} d)"))}{(quiet.Count > 4 ? $" and {quiet.Count - 4} more" : "")}: scrapes succeed but nothing new is posted.")
                        + " Consider replacing them with more active accounts.",
                    Sources = quiet.Select(s => s.Username).ToList(),
                });
            }

            // Where the content comes from.
            var active = view.Sources.Where(s => s.Downloaded > 0).ToList();
            if (active.Count >= 3 && active[0].Share >= 45)
            {
                list.Add(new Insight
                {
                    Level = "info",
                    Title = $"@{active[0].Username} supplied {active[0].Share:0}% of reels",
                    Detail = $"{active[0].Downloaded:n0} of {cur.Count:n0} {window}; the other {active.Count - 1} active sources shared the rest.",
                    Sources = new List<string> { active[0].Username },
                });
            }
            if (range.HasBaseline)
            {
                var mover = view.Niches.Where(x => x.Name != NoNiche && x.Downloaded >= 10 && x.DownloadedPrev is int p && p > 0)
                    .Select(x => (Niche: x, Change: (x.Downloaded - x.DownloadedPrev!.Value) * 100.0 / x.DownloadedPrev!.Value))
                    .OrderByDescending(x => Math.Abs(x.Change)).FirstOrDefault();
                if (mover.Niche != null && Math.Abs(mover.Change) >= 50)
                {
                    list.Add(new Insight
                    {
                        Level = "info",
                        Title = $"“{mover.Niche.Name}” {(mover.Change > 0 ? "up" : "down")} {Math.Abs(mover.Change):0}%",
                        Detail = $"{mover.Niche.Downloaded:n0} reels {window}, against {mover.Niche.DownloadedPrev:n0} in {previousWindow}.",
                    });
                }
            }

            // Freshness: how long after posting a reel is caught.
            if (k.MedianAgeHours is double age && cur.Count(f => f.AgeHours.HasValue) >= 10)
            {
                if (age > Math.Max(72, intervalHours * 2.5))
                {
                    list.Add(new Insight
                    {
                        Level = "warn",
                        Title = $"Reels are {Duration(age)} old when downloaded",
                        Detail = $"That is the median {window}. Scrapes run every {Plural(intervalHours, "hour")}; most of the delay is backfill of older reels or sources that are scraped late.",
                    });
                }
                else if (age <= Math.Max(24, intervalHours))
                {
                    list.Add(new Insight { Level = "good", Title = "Reels arrive fresh", Detail = $"The median reel is downloaded {Duration(age)} after it was posted." });
                }
            }

            // Performance.
            if (k.ViralReels > 0)
            {
                var top = view.TopReels.FirstOrDefault();
                list.Add(new Insight
                {
                    Level = "good",
                    Title = $"{Plural(k.ViralReels, "reel")} passed 1M views",
                    Detail = top != null ? $"The biggest, from @{top.Owner}, has {Compact(top.Views)} views." : "",
                    Sources = top != null ? new List<string> { top.Owner } : new List<string>(),
                });
            }
            if (view.BestViewsSlot != null && k.AvgViews is double avg && avg > 0)
            {
                var cell = view.Heatmap.Where(c => SlotLabel(c) == view.BestViewsSlot).FirstOrDefault();
                if (cell != null && cell.AvgViews >= avg * 1.5)
                {
                    list.Add(new Insight
                    {
                        Level = "info",
                        Title = $"Reels posted {view.BestViewsSlot} do best",
                        Detail = $"{Compact((long)cell.AvgViews)} views on average across {Plural(cell.Reels, "reel")}, against {Compact((long)avg)} overall ({view.Range.TimeZone}). Useful when scheduling OmniGram and OmniTumblr posts.",
                    });
                }
            }

            // The scraper itself.
            if (k.Scrapes >= 5 && k.SuccessRate is double rate && rate < 90)
            {
                list.Add(new Insight
                {
                    Level = rate < 75 ? "bad" : "warn",
                    Title = $"{rate:0}% of scrapes succeeded",
                    Detail = $"{k.ScrapeFailures} of {k.Scrapes} scrapes {window} failed. The Health tab shows which provider is at fault.",
                });
            }
            if (k.LongestGapDays >= 3 && cur.Count > 0)
            {
                list.Add(new Insight
                {
                    Level = "warn",
                    Title = $"{k.LongestGapDays} days without a download",
                    Detail = $"Nothing arrived from {PrettyIso(k.LongestGapFrom)} to {PrettyIso(k.LongestGapTo)}.",
                });
            }
            if (cur.Count >= 20)
            {
                double noViews = cur.Count(f => f.Views == 0) * 100.0 / cur.Count;
                if (noViews >= 20)
                {
                    list.Add(new Insight
                    {
                        Level = "info",
                        Title = $"{noViews:0}% of reels have no view count",
                        Detail = "Usually reels found by the Instagram fallback, which does not report views. View-based averages leave them as zero.",
                    });
                }
            }

            int Order(string level) => level switch { "bad" => 0, "warn" => 1, "good" => 2, _ => 3 };
            return list.OrderBy(i => Order(i.Level)).ToList();
        }

        // ─────────────────────────────── Library ───────────────────────────────

        public static ReelPage QueryReels(IEnumerable<Source> sources, IEnumerable<Reel> reels, ReelQuery query, DateTime nowUtc,
            Func<Reel, (bool Exists, long Bytes)>? fileInfo = null)
        {
            var (rows, range, universe) = FilterReels(sources, reels, query, nowUtc);
            var page = new ReelPage
            {
                Total = universe.Unfiltered,
                Options = OptionsOf(universe),
                Filtered = rows.Count,
                Offset = Math.Min(query.Offset, Math.Max(0, rows.Count)),
                Limit = query.Limit,
                Sort = query.Sort,
                Descending = query.Descending,
                Range = range.ToView(),
            };
            page.Rows = rows.Skip(page.Offset).Take(query.Limit).Select(f =>
            {
                var row = ToRow(f, 600);
                if (fileInfo != null)
                {
                    var (exists, bytes) = fileInfo(f.Reel);
                    row.VideoExists = exists;
                    row.Bytes = exists ? bytes : null;
                }
                return row;
            }).ToList();
            return page;
        }

        /// <summary>Every matching reel as CSV (UTF-8, RFC 4180 quoting), newest download first unless sorted.</summary>
        public static string ReelsCsv(IEnumerable<Source> sources, IEnumerable<Reel> reels, ReelQuery query, DateTime nowUtc, int maxRows = 50_000)
        {
            var (rows, _, _) = FilterReels(sources, reels, query, nowUtc);
            var csv = new StringBuilder();
            csv.AppendLine("PostID,ShortCode,Owner,Niches,Views,Likes,Comments,EngagementPct,PostedUtc,DownloadedUtc,AgeHours,Via,Url,Description");
            foreach (var f in rows.Take(maxRows))
            {
                csv.Append(Csv(f.Reel.PostID)).Append(',')
                    .Append(Csv(f.Reel.ShortCode)).Append(',')
                    .Append(Csv(f.Owner)).Append(',')
                    .Append(Csv(string.Join("; ", f.Niches))).Append(',')
                    .Append(f.Views.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(f.Likes.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(f.Comments.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(f.EngagementPct?.ToString("0.###", CultureInfo.InvariantCulture) ?? "").Append(',')
                    .Append(f.PostedUtc?.ToString("u", CultureInfo.InvariantCulture) ?? "").Append(',')
                    .Append(f.DownloadedUtc?.ToString("u", CultureInfo.InvariantCulture) ?? "").Append(',')
                    .Append(f.AgeHours?.ToString("0.#", CultureInfo.InvariantCulture) ?? "").Append(',')
                    .Append(Csv(f.Via)).Append(',')
                    .Append(Csv(ReelUrl(f.Reel))).Append(',')
                    .Append(Csv(f.Reel.Description?.Replace("\r", " ").Replace("\n", " ")))
                    .AppendLine();
            }
            return csv.ToString();
        }

        private static (List<ReelFact> Rows, ResolvedRange Range, Universe Universe) FilterReels(IEnumerable<Source> sources, IEnumerable<Reel> reels, ReelQuery query, DateTime nowUtc)
        {
            var zone = ResolveZone(query.Window.TimeZone).Zone;
            var universe = BuildUniverse(sources, reels, query.Window, zone);
            var firstDay = universe.Reels.Where(f => f.Day.HasValue).Select(f => f.Day!.Value).DefaultIfEmpty(LocalDay(nowUtc, zone)).Min();
            var range = ResolveRange(query.Window, nowUtc, firstDay);
            IEnumerable<ReelFact> rows = universe.Reels.Where(f => range.AllTime ? true : f.Day is DateOnly d && range.Contains(d));
            if (query.MinViews is long min) rows = rows.Where(f => f.Views >= min);
            if (query.Via != null) rows = rows.Where(f => f.Via.Split('+').Contains(query.Via, StringComparer.OrdinalIgnoreCase) || string.Equals(f.Via, query.Via, StringComparison.OrdinalIgnoreCase));
            if (query.Search is string text)
            {
                rows = rows.Where(f =>
                    (f.Reel.Description?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
                    || f.Owner.Contains(text.TrimStart('@'), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f.Reel.ShortCode, text, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f.Reel.PostID, text, StringComparison.Ordinal));
            }
            Func<ReelFact, double> key = query.Sort switch
            {
                "posted" => f => f.PostedUtc?.Ticks ?? double.MinValue,
                "views" => f => f.Views,
                "likes" => f => f.Likes,
                "comments" => f => f.Comments,
                "engagement" => f => f.EngagementPct ?? double.MinValue,
                "age" => f => f.AgeHours ?? double.MinValue,
                _ => f => f.DownloadedUtc?.Ticks ?? double.MinValue,
            };
            List<ReelFact> sorted = query.Sort == "owner"
                ? (query.Descending ? rows.OrderByDescending(f => f.Owner, StringComparer.OrdinalIgnoreCase) : rows.OrderBy(f => f.Owner, StringComparer.OrdinalIgnoreCase))
                    .ThenByDescending(f => f.DownloadedUtc).ToList()
                : (query.Descending ? rows.OrderByDescending(key) : rows.OrderBy(key))
                    .ThenByDescending(f => f.DownloadedUtc).ThenBy(f => f.Reel.PostID, StringComparer.Ordinal).ToList();
            return (sorted, range, universe);
        }

        /// <summary>One reel with its full description, resolved against the registered sources.</summary>
        public static ReelRow ToRow(Reel reel, IEnumerable<Source> sources)
        {
            var universe = BuildUniverse(sources, new[] { reel }, new InsightsQuery(), TimeZoneInfo.Utc);
            return ToRow(universe.Reels[0], int.MaxValue);
        }

        private static ReelRow ToRow(ReelFact f, int descriptionLimit) => new()
        {
            PostID = f.Reel.PostID ?? "",
            ShortCode = f.Reel.ShortCode,
            Owner = f.Owner,
            Registered = f.Source != null,
            Niches = f.Niches,
            Views = f.Views,
            Likes = f.Likes,
            Comments = f.Comments,
            EngagementPct = f.EngagementPct.HasValue ? Math.Round(f.EngagementPct.Value, 3) : null,
            PostedUtc = f.PostedUtc,
            DownloadedUtc = f.DownloadedUtc,
            AgeHours = f.AgeHours.HasValue ? Math.Round(f.AgeHours.Value, 1) : null,
            Description = Truncate(f.Reel.Description, descriptionLimit),
            Url = ReelUrl(f.Reel),
            Via = f.Via,
        };

        private static string? ReelUrl(Reel reel) =>
            !string.IsNullOrWhiteSpace(reel.ShortURL) ? reel.ShortURL
            : !string.IsNullOrWhiteSpace(reel.ShortCode) ? $"https://www.instagram.com/reel/{reel.ShortCode}/"
            : null;

        // ─────────────────────────────── Scrape history ───────────────────────────────

        public static ScrapePage QueryScrapes(IEnumerable<Source> sources, IEnumerable<Report> history, ScrapeQuery query, DateTime nowUtc)
        {
            var zone = ResolveZone(query.Window.TimeZone).Zone;
            var universe = BuildUniverse(sources, Array.Empty<Reel>(), query.Window, zone);
            var all = history.Where(r => r != null).ToList();
            var firstDay = all.Select(r => LocalDay(AsUtc(r.StartedUtc), zone)).DefaultIfEmpty(LocalDay(nowUtc, zone)).Min();
            var range = ResolveRange(query.Window, nowUtc, firstDay);
            var inWindow = all.Where(r => universe.ReportMatches(r) && range.Contains(LocalDay(AsUtc(r.StartedUtc), zone))).ToList();

            IEnumerable<Report> rows = inWindow;
            rows = query.Status switch
            {
                "failed" => rows.Where(r => !string.IsNullOrEmpty(r.Error)),
                "partial" => rows.Where(r => string.IsNullOrEmpty(r.Error) && r.DownloadFailures > 0),
                "ok" => rows.Where(r => string.IsNullOrEmpty(r.Error) && r.DownloadFailures == 0),
                _ => rows,
            };
            rows = query.Trigger switch
            {
                "schedule" => rows.Where(r => r.Trigger == "schedule"),
                "manual" => rows.Where(r => r.Trigger != "schedule"),
                _ => rows,
            };
            var filtered = rows.OrderByDescending(r => r.StartedUtc).ToList();
            return new ScrapePage
            {
                Total = all.Count,
                Options = OptionsOf(universe),
                Filtered = filtered.Count,
                Offset = query.Offset,
                Limit = query.Limit,
                Range = range.ToView(),
                Stats = new ScrapeStats
                {
                    Scrapes = inWindow.Count,
                    Failed = inWindow.Count(r => !string.IsNullOrEmpty(r.Error)),
                    Partial = inWindow.Count(r => string.IsNullOrEmpty(r.Error) && r.DownloadFailures > 0),
                    SuccessRate = SuccessRate(inWindow),
                    MedianSeconds = Median(inWindow.Where(r => r.FinishedUtc.HasValue).Select(r => (AsUtc(r.FinishedUtc!.Value) - AsUtc(r.StartedUtc)).TotalSeconds).Where(s => s >= 0)),
                    ReelsSeen = inWindow.Sum(r => r.ReelsFound),
                    NewReels = inWindow.Sum(r => r.NewReels),
                    Downloaded = inWindow.Sum(r => r.Downloaded),
                    DownloadFailures = inWindow.Sum(r => r.DownloadFailures),
                },
                Rows = filtered.Skip(query.Offset).Take(query.Limit).Select(ToScrapeRow).ToList(),
            };
        }

        public static ScrapeRow ToScrapeRow(Report r) => new()
        {
            Username = r.Username,
            AccountID = string.IsNullOrEmpty(r.AccountID) ? null : r.AccountID,
            Trigger = r.Trigger,
            StartedUtc = AsUtc(r.StartedUtc),
            FinishedUtc = r.FinishedUtc.HasValue ? AsUtc(r.FinishedUtc.Value) : null,
            DurationMs = r.FinishedUtc.HasValue ? (long)Math.Max(0, (AsUtc(r.FinishedUtc.Value) - AsUtc(r.StartedUtc)).TotalMilliseconds) : null,
            ReelsFound = r.ReelsFound,
            NewReels = r.NewReels,
            Downloaded = r.Downloaded,
            DownloadFailures = r.DownloadFailures,
            Error = r.Error,
            ProviderSummary = r.ProviderSummary,
            SucceededVia = r.SucceededVia,
            Notes = r.Notes ?? new List<string>(),
            Status = !string.IsNullOrEmpty(r.Error) ? "failed" : r.DownloadFailures > 0 ? "partial" : "ok",
        };

        // ─────────────────────────────── Summary ───────────────────────────────

        /// <summary>The handful of numbers the dashboard and schemes cards show, without the analytics bill.</summary>
        public static SummaryView BuildSummary(IEnumerable<Source> sources, IEnumerable<Reel> reels, string? timeZone, DateTime nowUtc)
        {
            var zone = ResolveZone(timeZone).Zone;
            var today = LocalDay(nowUtc, zone);
            var list = sources.Where(s => s != null).ToList();
            var summary = new SummaryView
            {
                TotalInstagramSources = list.Count,
                ReelSources = list.Count(s => s.DownloadReels),
                FailingSources = list.Count(s => s.DownloadReels && s.ConsecutiveScrapeFailures > 0),
            };
            var perDay = new int[14];
            foreach (var reel in reels)
            {
                if (reel == null) continue;
                summary.TotalReelsDownloaded++;
                if (LocalishToUtc(reel.DateTimeReelDownloaded) is not DateTime downloaded) continue;
                if (summary.LastDownloadUtc == null || downloaded > summary.LastDownloadUtc) summary.LastDownloadUtc = downloaded;
                int age = today.DayNumber - LocalDay(downloaded, zone).DayNumber;
                if (age < 0) continue;
                if (age == 0) summary.DownloadedToday++;
                if (age < 7) summary.Downloaded7d++;
                else if (age < 14) summary.DownloadedPrev7d++;
                if (age < 14) perDay[age]++;
            }
            for (int age = 13; age >= 0; age--) summary.MemesDownloadedPerDay[Fmt(today.AddDays(-age))] = perDay[age];
            return summary;
        }

        // ─────────────────────────────── Helpers ───────────────────────────────

        private sealed class BucketShape
        {
            public string Key = "", From = "", To = "";
            public int Days;
            public bool Incomplete;
        }

        private static List<BucketShape> BuildBuckets(ResolvedRange r)
        {
            var list = new List<BucketShape>();
            DateOnly cursor = r.Granularity switch
            {
                "week" => MondayOf(r.From),
                "month" => new DateOnly(r.From.Year, r.From.Month, 1),
                _ => r.From,
            };
            while (cursor <= r.To)
            {
                DateOnly next = r.Granularity switch { "week" => cursor.AddDays(7), "month" => cursor.AddMonths(1), _ => cursor.AddDays(1) };
                DateOnly start = cursor < r.From ? r.From : cursor;
                DateOnly end = next.AddDays(-1) > r.To ? r.To : next.AddDays(-1);
                int covered = end.DayNumber - start.DayNumber + 1;
                list.Add(new BucketShape
                {
                    Key = Fmt(cursor),
                    From = Fmt(start),
                    To = Fmt(end),
                    Days = covered,
                    Incomplete = covered < next.DayNumber - cursor.DayNumber || end >= r.Today,
                });
                cursor = next;
            }
            return list;
        }

        private static List<DistributionBucket> Distribute(List<ReelFact> facts, Func<ReelFact, long> value, (string Label, long Min, long Max)[] edges)
        {
            var result = edges.Select(e => new DistributionBucket { Label = e.Label }).ToList();
            var views = edges.Select(_ => 0L).ToArray();
            foreach (var f in facts)
            {
                long v = value(f);
                for (int i = 0; i < edges.Length; i++)
                {
                    if (v >= edges[i].Min && v < edges[i].Max)
                    {
                        result[i].Count++;
                        views[i] += f.Views;
                        break;
                    }
                }
            }
            for (int i = 0; i < result.Count; i++)
            {
                result[i].Share = facts.Count > 0 ? result[i].Count * 100.0 / facts.Count : 0;
                result[i].AvgViews = result[i].Count > 0 ? Math.Round(views[i] / (double)result[i].Count) : null;
            }
            return result;
        }

        private static List<DistributionBucket> DistributeAge(List<ReelFact> facts)
        {
            var edges = new (string Label, double Min, double Max)[]
            {
                ("Under 6 h", 0, 6), ("6–24 h", 6, 24), ("1–3 days", 24, 72), ("3–7 days", 72, 168),
                ("1–4 weeks", 168, 720), ("1–3 months", 720, 2160), ("Older", 2160, double.MaxValue),
            };
            var result = edges.Select(e => new DistributionBucket { Label = e.Label }).ToList();
            var unknown = new DistributionBucket { Label = "Unknown" };
            var views = new long[edges.Length + 1];
            foreach (var f in facts)
            {
                if (f.AgeHours is not double age)
                {
                    unknown.Count++;
                    views[edges.Length] += f.Views;
                    continue;
                }
                for (int i = 0; i < edges.Length; i++)
                {
                    if (age >= edges[i].Min && age < edges[i].Max)
                    {
                        result[i].Count++;
                        views[i] += f.Views;
                        break;
                    }
                }
            }
            if (unknown.Count > 0) result.Add(unknown);
            for (int i = 0; i < result.Count; i++)
            {
                result[i].Share = facts.Count > 0 ? result[i].Count * 100.0 / facts.Count : 0;
                result[i].AvgViews = result[i].Count > 0 ? Math.Round(views[i] / (double)result[i].Count) : null;
            }
            return result;
        }

        private static double? SuccessRate(List<Report> reports) =>
            reports.Count == 0 ? null : reports.Count(r => string.IsNullOrEmpty(r.Error)) * 100.0 / reports.Count;

        internal static double? Median(IEnumerable<double> values)
        {
            var list = values.ToList();
            if (list.Count == 0) return null;
            list.Sort();
            int mid = list.Count / 2;
            return list.Count % 2 == 1 ? list[mid] : (list[mid - 1] + list[mid]) / 2;
        }

        internal static List<string> NicheNames(Source source) =>
            (source.Niches ?? new List<MemeScraperSources.Niche>())
                .Where(n => n != null && !string.IsNullOrWhiteSpace(n.NicheTagName))
                .Select(n => n.NicheTagName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        public static (TimeZoneInfo Zone, string Id) ResolveZone(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return (TimeZoneInfo.Utc, "UTC");
            string trimmed = id.Trim();
            try { return (TimeZoneInfo.FindSystemTimeZoneById(trimmed), trimmed); } catch { }
            try
            {
                if (TimeZoneInfo.TryConvertIanaIdToWindowsId(trimmed, out var windowsId)) return (TimeZoneInfo.FindSystemTimeZoneById(windowsId), trimmed);
            }
            catch { }
            return (TimeZoneInfo.Utc, "UTC");
        }

        internal static DateOnly LocalDay(DateTime utc, TimeZoneInfo zone) =>
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utc), zone));

        private static DateTime LocalMidnightUtc(DateOnly day, TimeZoneInfo zone)
        {
            var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            // A zone that springs forward at midnight has no 00:00 that day.
            for (int i = 0; i < 3 && zone.IsInvalidTime(local); i++) local = local.AddHours(1);
            return TimeZoneInfo.ConvertTimeToUtc(local, zone);
        }

        internal static DateOnly MondayOf(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

        private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;

        internal static DateTime AsUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

        /// <summary>Download stamps were written with DateTime.Now: zone-less ones are server-local.</summary>
        internal static DateTime? LocalishToUtc(DateTime value)
        {
            if (value.Year < 2000) return null;
            return value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime(),
            };
        }

        /// <summary>Instagram's own stamps are UTC; zone-less ones are taken as UTC.</summary>
        internal static DateTime? UtcishToUtc(DateTime value) => value.Year < 2000 ? null : AsUtc(value);

        private static string Fmt(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        private static string Pretty(DateOnly day) => day.ToString("d MMM", CultureInfo.InvariantCulture);
        private static string PrettyIso(string? iso) => InsightsQuery.ParseDay(iso) is DateOnly d ? Pretty(d) : iso ?? "";

        private static readonly string[] DayNames = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        private static string SlotLabel(HeatCell cell) => $"{DayNames[cell.Dow]} {cell.Hour:00}:00–{(cell.Hour + 1) % 24:00}:00";

        private static string Plural(int count, string noun) => $"{count:n0} {noun}{(count == 1 ? "" : "s")}";

        private static string Duration(double hours) =>
            hours < 1 ? $"{Math.Max(1, Math.Round(hours * 60)):0} min"
            : hours < 48 ? $"{hours:0.#} h"
            : $"{hours / 24:0.#} days";

        private static string Ago(TimeSpan span) =>
            span.TotalHours < 1 ? $"{Math.Max(1, (int)span.TotalMinutes)} min"
            : span.TotalHours < 48 ? $"{(int)span.TotalHours} h"
            : $"{(int)span.TotalDays} days";

        private static string Compact(long value) =>
            value >= 1_000_000_000 ? $"{value / 1e9:0.#}B"
            : value >= 1_000_000 ? $"{value / 1e6:0.#}M"
            : value >= 1_000 ? $"{value / 1e3:0.#}k"
            : value.ToString(CultureInfo.InvariantCulture);

        private static string? Truncate(string? text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
            return text.Substring(0, max).TrimEnd() + "…";
        }

        private static string Csv(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            // A leading formula character would execute in a spreadsheet.
            if ("=+-@".Contains(value[0])) value = "'" + value;
            return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }
    }
}
