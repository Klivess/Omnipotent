using Omnipotent.Services.MemeScraper;
using Omnipotent.Services.MemeScraper.Analytics;
using Reel = Omnipotent.Services.MemeScraper.InstagramScrapeUtilities.InstagramReel;
using Report = Omnipotent.Services.MemeScraper.MemeScraper.ScrapeReport;
using Source = Omnipotent.Services.MemeScraper.MemeScraperSources.InstagramSource;

namespace Omnipotent.Tests.MemeScraper
{
    public class MemeScraperInsightsTests
    {
        // Sunday 11 October 2026, midday UTC.
        private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

        private static DateTime Day(int month, int day, int hour = 12) => new(2026, month, day, hour, 0, 0, DateTimeKind.Utc);

        private static Source Src(string user, string id, params string[] niches) => new()
        {
            Username = user,
            AccountID = id,
            FullName = user.ToUpperInvariant(),
            DownloadReels = true,
            Niches = niches.Select(n => new MemeScraperSources.Niche { NicheTagName = n }).ToList(),
            LastSuccessfulScrapeUtc = Now.AddHours(-2),
            NextScrapeDueUtc = Now.AddHours(20),
            DateTimeAdded = Day(1, 1),
        };

        private static Reel R(string id, string owner, string ownerId, DateTime downloadedUtc, int views = 1000, long likes = 50, int comments = 5, DateTime? posted = null) => new()
        {
            PostID = id,
            ShortCode = "sc" + id,
            OwnerUsername = owner,
            OwnerID = ownerId,
            ViewCount = views,
            LikeCount = likes,
            CommentCount = comments,
            CreatedAt = posted ?? downloadedUtc.AddHours(-10),
            DateTimeReelDownloaded = downloadedUtc,
            DiscoveredVia = "inflact",
            Description = "desc " + id,
            ShortURL = $"https://www.instagram.com/reels/sc{id}/",
        };

        private static Report Scrape(string user, string id, DateTime started, string? error = null, int downloadFailures = 0, int newReels = 2) => new()
        {
            Username = user,
            AccountID = id,
            Trigger = "schedule",
            StartedUtc = started,
            FinishedUtc = started.AddSeconds(90),
            Error = error,
            DownloadFailures = downloadFailures,
            NewReels = newReels,
            Downloaded = newReels - downloadFailures,
            ReelsFound = 12,
            SucceededVia = error == null ? "inflact" : null,
        };

        /// <summary>alpha (cats): 3 reels this week + 1 the week before; beta (dogs): 1 + 1; gamma (cats): 2 in September.</summary>
        private static (List<Source> Sources, List<Reel> Reels) Fixture()
        {
            var sources = new List<Source> { Src("alpha", "1", "cats"), Src("beta", "2", "dogs"), Src("gamma", "3", "cats") };
            var reels = new List<Reel>
            {
                R("a1", "alpha", "1", Day(10, 5), views: 10_000),
                R("a2", "alpha", "1", Day(10, 7), views: 2_000_000),
                R("a3", "alpha", "1", Day(10, 11, 9), views: 500),
                R("b1", "beta", "2", Day(10, 6), views: 30_000),
                R("a0", "alpha", "1", Day(10, 1), views: 4_000),
                R("b0", "beta", "2", Day(9, 30), views: 6_000),
                R("g1", "gamma", "3", Day(9, 1), views: 100),
                R("g2", "gamma", "3", Day(9, 1, 15), views: 200),
            };
            return (sources, reels);
        }

        // ─────────────────────────────── Window ───────────────────────────────

        [Fact]
        public void LastSevenDaysEndsTodayAndComparesWithTheSevenBefore()
        {
            var range = MemeScraperInsights.ResolveRange(new InsightsQuery { Days = 7 }, Now);

            Assert.Equal(new DateOnly(2026, 10, 5), range.From);
            Assert.Equal(new DateOnly(2026, 10, 11), range.To);
            Assert.Equal(7, range.Days);
            Assert.Equal(new DateOnly(2026, 9, 28), range.PrevFrom);
            Assert.Equal(new DateOnly(2026, 10, 4), range.PrevTo);
            Assert.Equal("day", range.Granularity);
            Assert.True(range.HasBaseline);
        }

        [Fact]
        public void ExplicitDatesAreClampedToTodayAndPutInOrder()
        {
            var range = MemeScraperInsights.ResolveRange(new InsightsQuery { From = new DateOnly(2026, 10, 20), To = new DateOnly(2026, 10, 1) }, Now);

            Assert.Equal(new DateOnly(2026, 10, 1), range.From);
            Assert.Equal(new DateOnly(2026, 10, 11), range.To);
        }

        [Theory]
        [InlineData(30, null, "day")]
        [InlineData(120, null, "week")]
        [InlineData(800, null, "month")]
        [InlineData(2000, "day", "week")]
        [InlineData(30, "month", "month")]
        public void GranularityFollowsTheWindowUnlessAsked(int days, string? asked, string expected)
        {
            var range = MemeScraperInsights.ResolveRange(new InsightsQuery { Days = days, Granularity = asked }, Now);
            Assert.Equal(expected, range.Granularity);
        }

        [Fact]
        public void AllTimeStartsAtTheFirstDownloadAndHasNoBaseline()
        {
            var range = MemeScraperInsights.ResolveRange(new InsightsQuery { Days = 0 }, Now, new DateOnly(2025, 3, 4));

            Assert.True(range.AllTime);
            Assert.False(range.HasBaseline);
            Assert.Equal(new DateOnly(2025, 3, 4), range.From);
        }

        [Fact]
        public void ParsesPresetsDaysAndSourceLists()
        {
            InsightsQuery Parse(Dictionary<string, string> p) => InsightsQuery.Parse(k => p.TryGetValue(k, out var v) ? v : null);

            Assert.Equal(90, Parse(new() { ["range"] = "90d" }).Days);
            Assert.Equal(0, Parse(new() { ["range"] = "all" }).Days);
            Assert.Equal(365, Parse(new() { ["range"] = "1y" }).Days);
            Assert.Equal(14, Parse(new() { ["range"] = "90d", ["days"] = "14" }).Days);
            Assert.Equal(new DateOnly(2026, 2, 3), Parse(new() { ["from"] = "2026-02-03" }).From);
            Assert.Null(Parse(new() { ["from"] = "03/02/2026" }).From);
            Assert.Equal(new[] { "Alpha", "beta" }, Parse(new() { ["sources"] = "@Alpha, beta,,alpha" }).Sources);
        }

        [Fact]
        public void WeeklyBucketsStartOnMondayAndFlagPartialWeeks()
        {
            var (sources, reels) = Fixture();
            var view = MemeScraperInsights.BuildDashboard(sources, reels, Array.Empty<Report>(),
                new InsightsQuery { From = new DateOnly(2026, 9, 1), Granularity = "week" }, Now);

            Assert.Equal(6, view.Series.Count);
            Assert.Equal("2026-08-31", view.Series[0].Key);
            Assert.Equal("2026-09-01", view.Series[0].From);
            Assert.Equal(6, view.Series[0].Days);
            Assert.True(view.Series[0].Incomplete);
            Assert.False(view.Series[2].Incomplete);
            Assert.True(view.Series[^1].Incomplete); // contains today
            Assert.Equal(2, view.Series[0].Downloads); // gamma's two reels on 1 September
            Assert.Equal(reels.Count, view.Series.Sum(s => s.Downloads));
        }

        // ─────────────────────────────── Dashboard ───────────────────────────────

        [Fact]
        public void CountsTheWindowAgainstThePreviousOne()
        {
            var (sources, reels) = Fixture();
            var view = MemeScraperInsights.BuildDashboard(sources, reels, Array.Empty<Report>(), new InsightsQuery { Days = 7 }, Now);

            Assert.Equal(4, view.Kpis.Downloaded);
            Assert.Equal(2, view.Kpis.DownloadedPrev);
            Assert.Equal(2, view.Kpis.ActiveSources);
            Assert.Equal(2, view.Kpis.ActiveSourcesPrev);
            Assert.Equal(1, view.Kpis.ViralReels);
            Assert.Equal(4, view.Kpis.LibraryAtStart);
            Assert.Equal(8, view.Kpis.LibraryAtEnd);

            Assert.Equal(7, view.Series.Count);
            Assert.Equal(new[] { 1, 1, 1, 0, 0, 0, 1 }, view.Series.Select(s => s.Downloads));
            Assert.Equal(new int?[] { 0, 0, 1, 1, 0, 0, 0 }, view.Series.Select(s => s.Previous)); // 30 Sep → 7 Oct, 1 Oct → 8 Oct
            Assert.Equal(8, view.Series[^1].Cumulative);
            Assert.True(view.Series[^1].Incomplete);
            Assert.False(view.Series[0].Incomplete);

            Assert.Equal(new[] { "alpha", "beta" }, view.StackKeys);
            Assert.Equal(new[] { 1, 0 }, view.Series[0].Stack);
            Assert.Equal(new[] { 0, 1 }, view.Series[1].Stack);

            var alpha = view.Sources.Single(s => s.Username == "alpha");
            Assert.Equal(3, alpha.Downloaded);
            Assert.Equal(1, alpha.DownloadedPrev);
            Assert.Equal(75, alpha.Share);
            Assert.Equal(4, alpha.TotalReels);
            Assert.Equal(2_000_000, alpha.TopViews);
            var gamma = view.Sources.Single(s => s.Username == "gamma");
            Assert.Equal(0, gamma.Downloaded);
            Assert.Equal(2, gamma.TotalReels);
            Assert.Equal("alpha", view.Sources[0].Username);
        }

        [Fact]
        public void DaysStartAtLocalMidnightInTheRequestedZone()
        {
            var sources = new List<Source> { Src("alpha", "1") };
            // 23:30 UTC on 4 October is 01:30 on 5 October in Berlin (CEST, UTC+2).
            var reels = new List<Reel> { R("late", "alpha", "1", new DateTime(2026, 10, 4, 23, 30, 0, DateTimeKind.Utc)) };

            var utc = MemeScraperInsights.BuildDashboard(sources, reels, Array.Empty<Report>(), new InsightsQuery { Days = 7 }, Now);
            var berlin = MemeScraperInsights.BuildDashboard(sources, reels, Array.Empty<Report>(), new InsightsQuery { Days = 7, TimeZone = "Europe/Berlin" }, Now);

            Assert.Equal(0, utc.Kpis.Downloaded);
            Assert.Equal(1, utc.Kpis.DownloadedPrev);
            Assert.Equal(1, berlin.Kpis.Downloaded);
            Assert.Equal("Europe/Berlin", berlin.Range.TimeZone);
            Assert.Equal(new DateTime(2026, 10, 4, 22, 0, 0, DateTimeKind.Utc), berlin.Range.FromUtc);
        }

        [Fact]
        public void NicheAndSourceFiltersNarrowEverythingButTheOptions()
        {
            var (sources, reels) = Fixture();
            var cats = MemeScraperInsights.BuildDashboard(sources, reels, Array.Empty<Report>(), new InsightsQuery { Days = 0, Niche = "CATS" }, Now);

            Assert.Equal(6, cats.Library.MatchingReels);
            Assert.Equal(8, cats.Library.TotalReels);
            Assert.Equal(new[] { "alpha", "gamma" }, cats.Sources.Select(s => s.Username).OrderBy(s => s));
            Assert.Equal(new[] { "cats", "dogs" }, cats.Options.Niches);
            Assert.Equal(3, cats.Options.Sources.Count);

            var beta = MemeScraperInsights.BuildDashboard(sources, reels, Array.Empty<Report>(), new InsightsQuery { Days = 0, Sources = { "BETA" } }, Now);
            Assert.Equal(2, beta.Kpis.Downloaded);
            Assert.Single(beta.Sources);
        }

        [Fact]
        public void ReelsFollowTheirAccountThroughARenameAndOrphansStayVisible()
        {
            var sources = new List<Source> { Src("newname", "1") };
            var reels = new List<Reel>
            {
                R("x1", "oldname", "1", Day(10, 10)),
                R("x2", "ghost", "999", Day(10, 10)),
            };
            var view = MemeScraperInsights.BuildDashboard(sources, reels, Array.Empty<Report>(), new InsightsQuery { Days = 7 }, Now);

            var renamed = view.Sources.Single(s => s.Username == "newname");
            Assert.Equal(1, renamed.Downloaded);
            Assert.True(renamed.Registered);
            var orphan = view.Sources.Single(s => s.Username == "ghost");
            Assert.False(orphan.Registered);
            Assert.Equal("removed", orphan.State);
        }

        [Fact]
        public void KpisReadScrapeHistoryAndDayGaps()
        {
            var sources = new List<Source> { Src("alpha", "1") };
            var reels = new List<Reel> { R("1", "alpha", "1", Day(10, 5)), R("2", "alpha", "1", Day(10, 9)) };
            var history = new List<Report>
            {
                Scrape("alpha", "1", Day(10, 5, 1)),
                Scrape("alpha", "1", Day(10, 6, 1)),
                Scrape("alpha", "1", Day(10, 7, 1), error: "inflact: blocked"),
                Scrape("alpha", "1", Day(10, 9, 1), downloadFailures: 1),
                Scrape("alpha", "1", Day(9, 20, 1)), // before the window
            };
            var view = MemeScraperInsights.BuildDashboard(sources, reels, history, new InsightsQuery { Days = 7 }, Now);

            Assert.Equal(4, view.Kpis.Scrapes);
            Assert.Equal(1, view.Kpis.ScrapeFailures);
            Assert.Equal(75, view.Kpis.SuccessRate);
            Assert.Equal(90, view.Kpis.MedianScrapeSeconds);
            Assert.Equal(1, view.Kpis.DownloadFailures);
            // Complete days 5–10 Oct: nothing on the 6th, 7th, 8th and 10th; today is not a gap yet.
            Assert.Equal(4, view.Kpis.ZeroDays);
            Assert.Equal(3, view.Kpis.LongestGapDays);
            Assert.Equal("2026-10-06", view.Kpis.LongestGapFrom);
            Assert.Equal("2026-10-08", view.Kpis.LongestGapTo);
            Assert.Equal(1, view.Series[2].ScrapeFailures);
            Assert.Equal(new[] { "inflact" }, view.ScrapeProviders.Select(p => p.Name));
        }

        [Fact]
        public void InsightsNameFailingAndQuietSourcesFirst()
        {
            var (sources, reels) = Fixture();
            sources[1].ConsecutiveScrapeFailures = 4;
            sources[1].LastScrapeError = "every provider failed";
            // gamma's scrapes succeed but its last reel is from 1 September.
            var view = MemeScraperInsights.BuildDashboard(sources, reels, Array.Empty<Report>(), new InsightsQuery { Days = 7 }, Now);

            Assert.Equal("bad", view.Insights[0].Level);
            Assert.Contains("beta", view.Insights[0].Title);
            Assert.Equal(new[] { "beta" }, view.Insights[0].Sources);
            var quiet = Assert.Single(view.Insights, i => i.Title.Contains("gone quiet"));
            Assert.Equal(new[] { "gamma" }, quiet.Sources);
            Assert.Contains(view.Insights, i => i.Level == "good" && i.Title == "Downloads up 100%");
            Assert.Contains(view.Insights, i => i.Title.Contains("passed 1M views"));
            Assert.Equal("bad", view.Sources.Single(s => s.Username == "beta").State);
            Assert.Equal("quiet", view.Sources.Single(s => s.Username == "gamma").State);
        }

        [Fact]
        public void DistributionsBucketViewsAndAge()
        {
            var sources = new List<Source> { Src("alpha", "1") };
            var at = Day(10, 10);
            var reels = new List<Reel>
            {
                R("0", "alpha", "1", at, views: 0, posted: at.AddHours(-2)),
                R("1", "alpha", "1", at, views: 500, posted: at.AddHours(-30)),
                R("2", "alpha", "1", at, views: 5_000, posted: at.AddDays(-10)),
                R("3", "alpha", "1", at, views: 2_000_000, posted: at.AddDays(-200)),
            };
            var view = MemeScraperInsights.BuildDashboard(sources, reels, Array.Empty<Report>(), new InsightsQuery { Days = 7 }, Now);

            int Views(string label) => view.ViewsDistribution.Single(b => b.Label == label).Count;
            Assert.Equal(1, Views("No views"));
            Assert.Equal(1, Views("1–999"));
            Assert.Equal(1, Views("1k–10k"));
            Assert.Equal(1, Views("1M–10M"));
            int Age(string label) => view.AgeDistribution.Single(b => b.Label == label).Count;
            Assert.Equal(1, Age("Under 6 h"));
            Assert.Equal(1, Age("1–3 days"));
            Assert.Equal(1, Age("1–4 weeks"));
            Assert.Equal(1, Age("Older"));
            Assert.Equal(135, view.Kpis.MedianAgeHours); // median of 2, 30, 240 and 4800 h
        }

        // ─────────────────────────────── Library ───────────────────────────────

        [Fact]
        public void LibraryDefaultsToEverythingAndSearchesSortsAndPages()
        {
            var (sources, reels) = Fixture();
            ReelQuery Parse(Dictionary<string, string> p) => ReelQuery.Parse(k => p.TryGetValue(k, out var v) ? v : null);

            var all = MemeScraperInsights.QueryReels(sources, reels, Parse(new()), Now);
            Assert.Equal(8, all.Total);
            Assert.Equal(8, all.Filtered);
            Assert.Equal("a3", all.Rows[0].PostID); // newest download first

            var byViews = MemeScraperInsights.QueryReels(sources, reels, Parse(new() { ["sort"] = "views", ["limit"] = "2", ["offset"] = "1" }), Now);
            Assert.Equal(new[] { "b1", "a1" }, byViews.Rows.Select(r => r.PostID));

            var search = MemeScraperInsights.QueryReels(sources, reels, Parse(new() { ["q"] = "@gamma" }), Now);
            Assert.Equal(2, search.Filtered);

            var week = MemeScraperInsights.QueryReels(sources, reels, Parse(new() { ["range"] = "7d", ["minViews"] = "1000" }), Now);
            Assert.Equal(new[] { "a1", "a2", "b1" }, week.Rows.Select(r => r.PostID).OrderBy(id => id));
            Assert.Equal(3, week.Filtered);
        }

        [Fact]
        public void CsvQuotesTextAndDefusesFormulas()
        {
            var sources = new List<Source> { Src("alpha", "1") };
            var reel = R("1", "alpha", "1", Day(10, 10));
            reel.Description = "=HYPERLINK(\"x\"), \"quoted\"\nnext line";
            string csv = MemeScraperInsights.ReelsCsv(sources, new[] { reel }, ReelQuery.Parse(_ => null), Now);

            var lines = csv.TrimEnd().Split('\n');
            Assert.Equal(2, lines.Length);
            Assert.EndsWith("\"'=HYPERLINK(\"\"x\"\"), \"\"quoted\"\" next line\"", lines[1].TrimEnd('\r'));
        }

        // ─────────────────────────────── Scrape history ───────────────────────────────

        [Fact]
        public void ScrapeHistoryFiltersByOutcomeAndSummarisesTheWindow()
        {
            var sources = new List<Source> { Src("alpha", "1") };
            var history = new List<Report>
            {
                Scrape("alpha", "1", Day(10, 5)),
                Scrape("alpha", "1", Day(10, 6), error: "blocked"),
                Scrape("alpha", "1", Day(10, 7), downloadFailures: 1),
            };
            ScrapeQuery Parse(string status) => ScrapeQuery.Parse(k => k switch { "status" => status, "range" => "7d", _ => null });

            var failed = MemeScraperInsights.QueryScrapes(sources, history, Parse("failed"), Now);
            Assert.Equal("blocked", Assert.Single(failed.Rows).Error);
            Assert.Equal("failed", failed.Rows[0].Status);
            Assert.Equal(3, failed.Stats.Scrapes);
            Assert.Equal(1, failed.Stats.Failed);
            Assert.Equal(1, failed.Stats.Partial);

            var all = MemeScraperInsights.QueryScrapes(sources, history, Parse("all"), Now);
            Assert.Equal(new[] { "partial", "failed", "ok" }, all.Rows.Select(r => r.Status)); // newest first
            Assert.Equal(90_000, all.Rows[0].DurationMs);
        }

        [Fact]
        public void SummaryCountsTodayAndTheLastTwoWeeks()
        {
            var (sources, reels) = Fixture();
            var summary = MemeScraperInsights.BuildSummary(sources, reels, null, Now);

            Assert.Equal(3, summary.TotalInstagramSources);
            Assert.Equal(8, summary.TotalReelsDownloaded);
            Assert.Equal(1, summary.DownloadedToday);
            Assert.Equal(4, summary.Downloaded7d);
            Assert.Equal(2, summary.DownloadedPrev7d);
            Assert.Equal(14, summary.MemesDownloadedPerDay.Count);
            Assert.Equal(1, summary.MemesDownloadedPerDay["2026-10-11"]);
            Assert.Equal("2026-09-28", summary.MemesDownloadedPerDay.Keys.First());
        }

        // ─────────────────────────────── Scrape log ───────────────────────────────

        [Fact]
        public async Task ScrapeLogSurvivesARestartCompactsAndSkipsATornLine()
        {
            string dir = Path.Combine(Path.GetTempPath(), "memescraper-log-" + Guid.NewGuid().ToString("N"));
            try
            {
                string path = Path.Combine(dir, "ScrapeHistory.jsonl");
                var log = new MemeScraperScrapeLog(path, maxEntries: 3);
                for (int i = 0; i < 7; i++) await log.AppendAsync(Scrape("u" + i, "1", Now.AddMinutes(i)));

                Assert.Equal(3, log.Count);
                Assert.Equal(3, File.ReadAllLines(path).Length); // compacted on the 7th line (> 2 × 3)

                await File.AppendAllTextAsync(path, "{\"Username\":\"torn");
                var reloaded = new MemeScraperScrapeLog(path, maxEntries: 3);
                Assert.Equal(1, await reloaded.LoadAsync());
                Assert.Equal(new[] { "u6", "u5", "u4" }, reloaded.Latest(10).Select(r => r.Username));
                Assert.Equal("inflact", reloaded.Latest(1)[0].SucceededVia);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
