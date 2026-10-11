using Newtonsoft.Json;
using Omnipotent.Services.MemeScraper;
using Omnipotent.Services.MemeScraper.Analytics;
using Reel = Omnipotent.Services.MemeScraper.InstagramScrapeUtilities.InstagramReel;
using Report = Omnipotent.Services.MemeScraper.MemeScraper.ScrapeReport;
using Source = Omnipotent.Services.MemeScraper.MemeScraperSources.InstagramSource;

namespace Omnipotent.Tests.MemeScraper
{
    /// <summary>
    /// Writes the website's MemeScraper e2e fixtures from the real analytics engine, so the pages are
    /// exercised against exactly what the routes return. Off unless MEMESCRAPER_FIXTURES names the
    /// output directory, e.g. klives-management-website/tests/e2e/fixtures/memescraper.
    /// </summary>
    public class MemeScraperFixtureExport
    {
        private static readonly DateTime Now = new(2026, 10, 11, 15, 0, 0, DateTimeKind.Utc);
        private const string Zone = "Europe/London";

        private sealed record Spec(string User, string Name, string[] Niches, int Followers, double PerDay, double MedianViews,
            int StartDaysAgo, int QuietDaysAgo = 0, int Failures = 0, bool Paused = false);

        private static readonly Spec[] Specs =
        {
            new("codememes", "Code Memes", new[] { "programming", "relatable" }, 412_000, 3.2, 85_000, 400),
            new("catmemes.daily", "Cat Memes Daily", new[] { "cats", "animals" }, 1_240_000, 4.5, 240_000, 380),
            new("dogs.of.reddit", "Dogs of Reddit", new[] { "dogs", "animals" }, 890_000, 2.4, 160_000, 300),
            new("gymhumor", "Gym Humor", new[] { "gym", "relatable" }, 305_000, 1.6, 52_000, 260),
            new("f1memes", "F1 Memes", new[] { "f1", "sport" }, 640_000, 2.0, 120_000, 200),
            new("techhumour", "Tech Humour", new[] { "programming" }, 98_000, 1.1, 14_000, 330, Failures: 4),
            new("uni.struggles", "Uni Struggles", new[] { "relatable", "students" }, 210_000, 1.4, 33_000, 150, QuietDaysAgo: 32),
            new("oddlysatisfying.reels", "Oddly Satisfying", new[] { "satisfying" }, 2_100_000, 3.0, 420_000, 120),
            new("dadjokes.hq", "Dad Jokes HQ", new[] { "relatable" }, 76_000, 0.8, 9_000, 400, Paused: true),
            new("sigmagrindset", "Sigma Grindset", new[] { "motivation" }, 150_000, 1.2, 40_000, 90, Failures: 1),
            new("memezar", "Memezar", new[] { "relatable", "cats" }, 520_000, 2.6, 95_000, 60),
            new("startup.lol", "Startup LOL", Array.Empty<string>(), 45_000, 0.6, 7_000, 45),
        };

        // Every provider was blocked for these days: nothing downloaded, every scrape failed.
        private static readonly DateTime OutageStart = new(2026, 8, 14, 0, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime OutageEnd = new(2026, 8, 18, 0, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void ExportWebsiteFixtures()
        {
            string? output = Environment.GetEnvironmentVariable("MEMESCRAPER_FIXTURES");
            if (string.IsNullOrWhiteSpace(output)) return;
            Directory.CreateDirectory(output);

            var (sources, reels, history) = Build();
            void Write(string name, object value) =>
                File.WriteAllText(Path.Combine(output, name + ".json"), JsonConvert.SerializeObject(value, Formatting.None));

            InsightsQuery Window(string range, Action<InsightsQuery>? tweak = null)
            {
                var q = InsightsQuery.Parse(k => k switch { "range" => range, "tz" => Zone, _ => null });
                tweak?.Invoke(q);
                return q;
            }

            var dashboards = new Dictionary<string, InsightsQuery>
            {
                // Only the windows the e2e spec visits; anything else is answered with 30d.
                ["30d"] = Window("30d"),
                ["90d"] = Window("90d"),
                ["all"] = Window("all"),
                ["custom"] = Window("30d", q => { q.Days = null; q.From = new DateOnly(2026, 8, 1); q.To = new DateOnly(2026, 8, 31); }),
                ["30d-src"] = Window("30d", q => q.Sources = new List<string> { "codememes" }),
            };
            var dashboard = dashboards.ToDictionary(kv => kv.Key, kv => (object)MemeScraperInsights.BuildDashboard(sources, reels, history, kv.Value, Now, 24));
            Write("dashboards", dashboard);

            ReelQuery Reels(string range, Action<ReelQuery>? tweak = null)
            {
                var q = ReelQuery.Parse(k => k switch { "range" => range, "tz" => Zone, "limit" => "48", _ => null });
                tweak?.Invoke(q);
                return q;
            }
            var reelQueries = new Dictionary<string, ReelQuery>
            {
                ["30d"] = Reels("30d"),
                ["30d-views"] = Reels("30d", q => q.Sort = "views"),
                ["30d-src"] = Reels("30d", q => q.Window.Sources = new List<string> { "codememes" }),
                ["30d-q"] = Reels("30d", q => q.Search = "cat"),
            };
            var pages = reelQueries.ToDictionary(kv => kv.Key, kv => MemeScraperInsights.QueryReels(sources, reels, kv.Value, Now, _ => (true, 4_200_000)));
            Write("reels", pages);

            // The reels a test can open (the top of each list), with the detail route's extra fields.
            var details = new Dictionary<string, object>();
            foreach (var row in pages.Values.SelectMany(p => p.Rows.Take(6)).Concat(dashboard.Values.Cast<DashboardView>().SelectMany(d => d.TopReels.Take(6))))
            {
                if (details.ContainsKey(row.PostID)) continue;
                var reel = reels.First(r => r.PostID == row.PostID);
                var siblings = reels.Where(r => string.Equals(r.OwnerUsername, reel.OwnerUsername, StringComparison.OrdinalIgnoreCase)).Select(r => (long)r.ViewCount).ToList();
                var full = MemeScraperInsights.ToRow(reel, sources);
                full.VideoExists = true;
                full.Bytes = 4_200_000;
                details[row.PostID] = new
                {
                    Reel = full,
                    OwnerReels = siblings.Count,
                    OwnerRank = siblings.Count(v => v > reel.ViewCount) + 1,
                    OwnerMedianViews = siblings.OrderBy(v => v).ElementAt(siblings.Count / 2),
                    Thumbnail = false,
                };
            }
            Write("reel-details", details);

            ScrapeQuery Scrapes(string range, Action<ScrapeQuery>? tweak = null)
            {
                var q = ScrapeQuery.Parse(k => k switch { "range" => range, "tz" => Zone, "limit" => "25", _ => null });
                tweak?.Invoke(q);
                return q;
            }
            var historyPages = new Dictionary<string, ScrapePage>
            {
                ["30d"] = MemeScraperInsights.QueryScrapes(sources, history, Scrapes("30d"), Now),
                ["30d-failed"] = MemeScraperInsights.QueryScrapes(sources, history, Scrapes("30d", q => q.Status = "failed"), Now),
                ["all"] = MemeScraperInsights.QueryScrapes(sources, history, Scrapes("all", q => q.Limit = 120), Now),
                ["src"] = MemeScraperInsights.QueryScrapes(sources, history, Scrapes("all", q => { q.Window.Sources = new List<string> { "codememes" }; q.Limit = 8; }), Now),
            };
            Write("scrape-history", historyPages);
            Write("summary", MemeScraperInsights.BuildSummary(sources, reels, Zone, Now));

            // The health route's shape (MemeScraper.BuildHealthSnapshot), filled from the same data.
            var lastSuccess = history.Where(r => r.Error == null).Max(r => r.FinishedUtc);
            Write("health", new
            {
                GeneratedUtc = Now,
                DataLoaded = true,
                OnServer = true,
                SchedulerState = "idle",
                LastSchedulerTickUtc = Now.AddMinutes(-3),
                NextScrapeDueUtc = sources.Where(s => s.DownloadReels).Min(s => s.NextScrapeDueUtc),
                CurrentScrape = (string?)null,
                ReelsOnDisk = reels.Count,
                Providers = new object[]
                {
                    new { Name = "inflact", ConsecutiveFailures = 0, TotalSuccesses = 214, TotalFailures = 9, LastSuccess = lastSuccess, LastFailure = Now.AddDays(-3), LastError = "inflact: HTTP 429 Too Many Requests", BenchedUntil = (DateTime?)null },
                    new { Name = "instagram", ConsecutiveFailures = 0, TotalSuccesses = 37, TotalFailures = 2, LastSuccess = Now.AddHours(-9), LastFailure = Now.AddDays(-12), LastError = "Instagram answered 401 for the logged-out profile page", BenchedUntil = (DateTime?)null },
                },
                FailingSources = sources.Count(s => s.ConsecutiveScrapeFailures > 0),
                Sources = sources.OrderBy(s => s.NextScrapeDueUtc).Select(s => new
                {
                    s.Username, s.AccountID, s.DownloadReels, s.NextScrapeDueUtc, s.LastScrapeAttemptUtc, s.LastSuccessfulScrapeUtc,
                    s.ConsecutiveScrapeFailures, s.LastScrapeError, s.LastScrapeSummary, s.LastScrapeReelsFound, s.LastScrapeReelsDownloaded,
                    s.LastScrapeDownloadFailures, s.TotalReelsDownloaded,
                }).ToList(),
                RecentScrapes = history.OrderByDescending(r => r.StartedUtc).Take(50).ToList(),
                ScrapeHistoryCount = history.Count,
                ProviderCheck = new
                {
                    Username = "codememes", RequestedBy = "Klives", State = "complete", QueuedUtc = Now.AddHours(-5), StartedUtc = Now.AddHours(-5),
                    FinishedUtc = Now.AddHours(-5).AddSeconds(74),
                    Results = new object[]
                    {
                        new { Provider = "inflact", Ok = true, Listed = 24, Reels = 24, Pages = 2, DurationMs = 41_200, Error = (string?)null, Note = (string?)null, DownloadOk = (bool?)true, DownloadBytes = 3_912_004, DownloadHost = "scontent-lhr8-1.cdninstagram.com", DownloadError = (string?)null },
                        new { Provider = "instagram", Ok = true, Listed = 12, Reels = 12, Pages = 1, DurationMs = 28_700, Error = (string?)null, Note = "logged-out pages list the newest 12 reels", DownloadOk = (bool?)true, DownloadBytes = 3_912_004, DownloadHost = "scontent-lhr6-2.cdninstagram.com", DownloadError = (string?)null },
                    },
                },
                ProfileRefresh = (object?)null,
                ScrapeIntervalHours = 24,
                Settings = new { Paused = false, IntervalHours = 24, MaxPagesPerScrape = 40, ReadUtc = Now },
                ScrapeQueued = 0,
                Disk = new { Bytes = reels.Count * 4_200_000L - 3 * 4_200_000L, Files = reels.Count - 3 + 2, MissingVideos = 3, OrphanFiles = 2, ThumbnailBytes = 18_400_000L, MeasuredUtc = Now.AddMinutes(-4), DurationMs = 212 },
            });
        }

        private static (List<Source> Sources, List<Reel> Reels, List<Report> History) Build()
        {
            var rng = new Random(42);
            var sources = new List<Source>();
            var reels = new List<Reel>();
            var history = new List<Report>();
            long postId = 3_400_000_000_000_000_000;
            string[] captions =
            {
                "when the code works on the first try and you don't trust it", "my cat at 3am vs my cat at 3pm", "nobody: absolutely nobody: my dog:",
                "leg day? never heard of her", "this pit stop was faster than my wifi", "me explaining my code to the rubber duck",
                "the satisfying part is at the end", "dad joke of the day, you're welcome", "rise and grind (at 11am)",
                "the audacity of this cat 😭", "group project be like", "when the intern pushes to main on friday",
                "he really said 'trust me bro'", "that's a 10/10 landing", "pov: the gym mirror after one set",
            };

            for (int i = 0; i < Specs.Length; i++)
            {
                var spec = Specs[i];
                var start = Now.Date.AddDays(-spec.StartDaysAgo);
                var source = new Source
                {
                    SourceID = Guid.NewGuid().ToString(),
                    Username = spec.User,
                    AccountID = (1_000_000 + i * 7919).ToString(),
                    FullName = spec.Name,
                    Followers = spec.Followers,
                    ProfilePictureUrl = "",
                    Bio = $"{spec.Name} · daily memes · DM for credit/removal",
                    DownloadReels = !spec.Paused,
                    Niches = spec.Niches.Select(n => new MemeScraperSources.Niche { NicheTagName = n }).ToList(),
                    AccountTopHashtags = new List<Source.AccountTopHashtag>
                    {
                        new() { Hashtag = "memes", Count = 40 + i }, new() { Hashtag = spec.Niches.FirstOrDefault() ?? "funny", Count = 22 }, new() { Hashtag = "viral", Count = 9 },
                    },
                    DateTimeAdded = start.AddHours(10),
                    LastUpdated = start.AddHours(10),
                };
                sources.Add(source);

                double scrapeHour = 2 + i * 1.7;
                var stopDownloading = spec.Paused ? Now.Date.AddDays(-60) : Now.AddDays(1);
                var quietSince = spec.QuietDaysAgo > 0 ? Now.Date.AddDays(-spec.QuietDaysAgo) : Now.AddDays(1);
                for (var day = start; day <= Now.Date; day = day.AddDays(1))
                {
                    var scrapeAt = day.AddHours(scrapeHour).AddMinutes(rng.Next(0, 40));
                    if (scrapeAt > Now || scrapeAt >= stopDownloading) break;
                    bool outage = scrapeAt >= OutageStart && scrapeAt < OutageEnd;
                    bool failingTail = spec.Failures > 0 && scrapeAt > Now.AddDays(-spec.Failures);
                    // Random failures stay out of the last two days, so each source's current state is the one its spec describes.
                    bool randomFail = rng.NextDouble() < 0.035 && scrapeAt < Now.AddDays(-2);
                    var report = new Report
                    {
                        Username = spec.User,
                        AccountID = source.AccountID,
                        Trigger = rng.NextDouble() < 0.05 ? "manual:Klives" : "schedule",
                        StartedUtc = scrapeAt,
                        FinishedUtc = scrapeAt.AddSeconds(Math.Exp(Normal(rng, Math.Log(70), 0.45))),
                    };
                    if (outage || failingTail || randomFail)
                    {
                        report.Error = outage ? "every provider failed for " + spec.User + ": inflact: FAILED — blocked (HTTP 403) | instagram: FAILED — HTTP 429"
                            : failingTail ? "every provider failed for " + spec.User + ": inflact: FAILED — profile not found | instagram: FAILED — the profile page has no reels"
                            : "inflact: FAILED — timed out after 20 min | instagram: FAILED — HTTP 429";
                        report.ProviderSummary = report.Error;
                        history.Add(report);
                        continue;
                    }

                    // The first scrape back-fills older reels; later ones catch what was posted since.
                    bool backfill = day == start;
                    int count = backfill ? rng.Next(25, 55) : day >= quietSince ? 0 : Poisson(rng, spec.PerDay * (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 1.25 : 1.0));
                    int partialFailures = !backfill && count > 0 && rng.NextDouble() < 0.06 ? 1 : 0;
                    int saved = 0;
                    for (int n = 0; n < count; n++)
                    {
                        DateTime posted;
                        if (backfill) posted = scrapeAt.AddHours(-rng.Next(24, 24 * 90));
                        else
                        {
                            // Evening posting (UK time) is most common.
                            int hour = PickHour(rng);
                            posted = scrapeAt.Date.AddDays(-1).AddHours(hour).AddMinutes(rng.Next(0, 60));
                            if (posted >= scrapeAt) posted = scrapeAt.AddHours(-1 - rng.NextDouble() * 5);
                            if (rng.NextDouble() < 0.15) posted = posted.AddDays(-rng.Next(1, 6));
                        }
                        bool viaInstagram = rng.NextDouble() < 0.08;
                        long views = viaInstagram ? 0 : (long)Math.Max(120, Math.Exp(Normal(rng, Math.Log(spec.MedianViews), 1.05)));
                        if (!viaInstagram && rng.NextDouble() < 0.012) views *= 25; // the occasional viral reel
                        long likes = viaInstagram ? 0 : (long)(views * (0.03 + rng.NextDouble() * 0.06));
                        int comments = viaInstagram ? 0 : (int)(likes * (0.01 + rng.NextDouble() * 0.04));
                        string shortCode = ShortCode(rng);
                        reels.Add(new Reel
                        {
                            PostID = (postId++).ToString(),
                            ShortCode = shortCode,
                            OwnerUsername = spec.User,
                            OwnerID = source.AccountID,
                            ViewCount = (int)Math.Min(int.MaxValue, views),
                            LikeCount = likes,
                            CommentCount = comments,
                            CreatedAt = posted,
                            DateTimeReelDownloaded = scrapeAt.AddSeconds(20 + n * 9),
                            Description = captions[rng.Next(captions.Length)] + (rng.NextDouble() < 0.5 ? $" #memes #{spec.Niches.FirstOrDefault() ?? "funny"}" : ""),
                            ShortURL = $"https://www.instagram.com/reels/{shortCode}/",
                            DiscoveredVia = viaInstagram ? "instagram" : rng.NextDouble() < 0.05 ? "inflact+instagram" : "inflact",
                            InstagramReelVideoFilePath = $"SavedData/MemeScraper/Instagram/Media/ReelMedia{postId - 1}.mp4",
                        });
                        saved++;
                    }
                    report.ReelsFound = backfill ? count : Math.Min(48, count + rng.Next(8, 20));
                    report.NewReels = count;
                    report.Downloaded = saved - partialFailures;
                    report.DownloadFailures = partialFailures;
                    report.SucceededVia = rng.NextDouble() < 0.9 ? "inflact" : "instagram";
                    report.ProviderSummary = report.SucceededVia == "inflact"
                        ? $"inflact: ok, {count} reels, {Math.Max(1, count / 12 + 1)} page(s)"
                        : $"inflact: FAILED — HTTP 429 | instagram: ok, {Math.Min(12, count)} reels, 1 page(s)";
                    if (partialFailures > 0) report.Notes.Add($"1 download(s) failed, e.g. {reels[^1].ShortCode}: HTTP 403 from the CDN");
                    history.Add(report);
                }

                // Health fields as the scraper leaves them.
                var mine = history.Where(r => r.Username == spec.User).OrderBy(r => r.StartedUtc).ToList();
                var last = mine.LastOrDefault();
                int trailingFailures = mine.AsEnumerable().Reverse().TakeWhile(r => r.Error != null).Count();
                source.LastScrapeAttemptUtc = last?.StartedUtc;
                source.LastSuccessfulScrapeUtc = mine.LastOrDefault(r => r.Error == null)?.FinishedUtc;
                source.ConsecutiveScrapeFailures = trailingFailures;
                source.LastScrapeError = last?.Error;
                source.LastScrapeSummary = last?.ProviderSummary;
                source.LastScrapeReelsFound = last?.ReelsFound ?? 0;
                source.LastScrapeReelsDownloaded = last?.Downloaded ?? 0;
                source.LastScrapeDownloadFailures = last?.DownloadFailures ?? 0;
                source.TotalReelsDownloaded = reels.Count(r => r.OwnerUsername == spec.User);
                source.LastScraped = (last?.StartedUtc ?? start).ToLocalTime();
                source.NextScrapeDueUtc = spec.Paused ? null
                    : trailingFailures > 0 ? (last!.FinishedUtc ?? Now).AddHours(Math.Min(Math.Pow(2, trailingFailures - 1), 24))
                    : (last?.StartedUtc ?? Now).AddHours(24);
            }

            // A deleted source whose reels were kept.
            for (int n = 0; n < 40; n++)
            {
                var at = Now.AddDays(-300 + n * 2.5);
                string code = ShortCode(rng);
                reels.Add(new Reel
                {
                    PostID = (postId++).ToString(), ShortCode = code, OwnerUsername = "oldmemes.archive", OwnerID = "999001",
                    ViewCount = rng.Next(4_000, 90_000), LikeCount = rng.Next(200, 4_000), CommentCount = rng.Next(5, 80),
                    CreatedAt = at.AddHours(-30), DateTimeReelDownloaded = at, Description = captions[rng.Next(captions.Length)],
                    ShortURL = $"https://www.instagram.com/reels/{code}/", DiscoveredVia = "inflact",
                    InstagramReelVideoFilePath = $"SavedData/MemeScraper/Instagram/Media/ReelMedia{postId - 1}.mp4",
                });
            }
            return (sources, reels, history);
        }

        private static double Normal(Random rng, double mean, double sigma)
        {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            return mean + sigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        }

        private static int Poisson(Random rng, double lambda)
        {
            double l = Math.Exp(-lambda), p = 1.0;
            int k = 0;
            do { k++; p *= rng.NextDouble(); } while (p > l);
            return k - 1;
        }

        private static readonly int[] HourWeights = { 3, 2, 1, 1, 1, 1, 2, 3, 4, 5, 6, 7, 8, 8, 9, 10, 12, 15, 18, 19, 17, 13, 9, 5 };

        private static int PickHour(Random rng)
        {
            int roll = rng.Next(HourWeights.Sum());
            for (int h = 0; h < 24; h++)
            {
                roll -= HourWeights[h];
                if (roll < 0) return h;
            }
            return 19;
        }

        private static string ShortCode(Random rng)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-";
            return "C" + new string(Enumerable.Range(0, 10).Select(_ => chars[rng.Next(chars.Length)]).ToArray());
        }
    }
}
