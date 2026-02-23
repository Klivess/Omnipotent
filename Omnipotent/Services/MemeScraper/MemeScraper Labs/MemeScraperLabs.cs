using static Omnipotent.Services.MemeScraper.MemeScraperSources;
using static Omnipotent.Services.MemeScraper.MemeScraperMedia;

namespace Omnipotent.Services.MemeScraper.MemeScraper_Labs
{
    public class MemeScraperLabs
    {
        MemeScraper parent;
        public MemeScraperLabs(MemeScraper parent)
        {
            this.parent = parent;
        }

        public class MemeScraperAnalytics
        {
            public DateTime DateTimeOfAnalyticsProduced;

            public int TotalInstagramSources;
            public int TotalReelsDownloaded;
            public double AverageReelsPerSource;
            public DateTime? EarliestReelDownload;
            public DateTime? LatestReelDownload;
            public InstagramSource? MostActiveSource;
            public Int128 TotalViewCount;
            public double AverageViewCountPerReel;
            public Dictionary<DateTime, int> MemesDownloadedPerDay;
            public Dictionary<DateTime, int> CumulativeDownloadedMemesPerDay;

            public Dictionary<string, int> ReelsDownloadedPerSource;
            public DateTime? MostActiveDownloadDay;
            public List<InstagramSource> InactiveSources;
            public double GrowthRateOfDownloads;
            public double SourceDiversityIndex;

            public Dictionary<string, int> TopNichesByDownload;
            public List<InstagramScrapeUtilities.InstagramReel> ReelsWithHighEngagement;
            public int DownloadGaps;
            public double ReelsPerSourceStdDev;
            public double PercentageOfSourcesWithRecentActivity;
            public DayOfWeek? MostCommonDownloadDayOfWeek;
            public List<InstagramScrapeUtilities.InstagramReel> ReelsWithNoViews;

            public MemeScraperAnalytics(List<InstagramSource> sources, List<InstagramScrapeUtilities.InstagramReel> reels)
            {
                DateTimeOfAnalyticsProduced = DateTime.Now;

                TotalInstagramSources = sources?.Count ?? 0;
                TotalReelsDownloaded = reels?.Count ?? 0;
                AverageReelsPerSource = (TotalInstagramSources > 0) ? (double)TotalReelsDownloaded / TotalInstagramSources : 0;
                EarliestReelDownload = reels != null && reels.Count > 0 ? reels.Min(r => r.DateTimeReelDownloaded) : null;
                LatestReelDownload = reels != null && reels.Count > 0 ? reels.Max(r => r.DateTimeReelDownloaded) : null;
                // With this code to prevent overflow and correctly sum into Int128:
                TotalViewCount = 0;
                if (reels != null)
                {
                    foreach (var r in reels)
                    {
                        TotalViewCount += (Int128)r.ViewCount;
                    }
                }
                AverageViewCountPerReel = (TotalReelsDownloaded > 0) ? (double)TotalViewCount / TotalReelsDownloaded : 0;

                // Find most active source (by number of reels downloaded)
                if (sources != null && reels != null && sources.Count > 0 && reels.Count > 0)
                {
                    var sourceReelCounts = sources.ToDictionary(
                        s => s,
                        s => reels.Count(r => r.OwnerUsername == s.Username)
                    );
                    MostActiveSource = sourceReelCounts.OrderByDescending(kv => kv.Value).FirstOrDefault().Key;
                }

                // Calculate MemesDownloadedPerDay
                MemesDownloadedPerDay = new Dictionary<DateTime, int>();
                if (EarliestReelDownload.HasValue && LatestReelDownload.HasValue)
                {
                    var start = EarliestReelDownload.Value.Date;
                    var end = LatestReelDownload.Value.Date;
                    for (var date = start; date <= end; date = date.AddDays(1))
                    {
                        int count = reels.Count(r => r.DateTimeReelDownloaded.Date == date);
                        MemesDownloadedPerDay[date] = count;
                    }
                }

                //Calculate CumulativeDownloadedMemesPerDay
                CumulativeDownloadedMemesPerDay = new Dictionary<DateTime, int>();
                int cumulativeCount = 0;
                foreach (var kv in MemesDownloadedPerDay.OrderBy(kv => kv.Key))
                {
                    cumulativeCount += kv.Value;
                    CumulativeDownloadedMemesPerDay[kv.Key] = cumulativeCount;
                }

                // 1. ReelsDownloadedPerSource
                ReelsDownloadedPerSource = new Dictionary<string, int>();
                if (sources != null && reels != null)
                {
                    foreach (var src in sources)
                    {
                        int count = reels.Count(r => r.OwnerUsername == src.Username);
                        ReelsDownloadedPerSource[src.Username] = count;
                    }
                }

                // 4. MostActiveDownloadDay
                MostActiveDownloadDay = MemesDownloadedPerDay.Count > 0 ?
                    MemesDownloadedPerDay.OrderByDescending(kv => kv.Value).FirstOrDefault().Key : (DateTime?)null;

                // 5. InactiveSources
                InactiveSources = sources != null ?
                    sources.Where(s => !reels.Any(r => r.OwnerUsername == s.Username)).ToList() : new List<InstagramSource>();

                // 6. GrowthRateOfDownloads (compare first 7 days vs last 7 days)
                GrowthRateOfDownloads = 0;
                if (MemesDownloadedPerDay.Count >= 14)
                {
                    var orderedDays = MemesDownloadedPerDay.OrderBy(kv => kv.Key).ToList();
                    double first7 = orderedDays.Take(7).Sum(kv => kv.Value);
                    double last7 = orderedDays.Skip(orderedDays.Count - 7).Sum(kv => kv.Value);
                    if (first7 > 0)
                        GrowthRateOfDownloads = ((last7 - first7) / first7) * 100.0;
                    else if (last7 > 0)
                        GrowthRateOfDownloads = 100.0;
                }

                // 10. SourceDiversityIndex
                int sourcesWithDownloads = ReelsDownloadedPerSource.Count(kv => kv.Value > 0);
                SourceDiversityIndex = (TotalInstagramSources > 0) ? (double)sourcesWithDownloads / TotalInstagramSources : 0;

                // 3. TopNichesByDownload
                TopNichesByDownload = new Dictionary<string, int>();
                if (sources != null && reels != null)
                {
                    foreach (var src in sources)
                    {
                        if (src.Niches != null)
                        {
                            int count = reels.Count(r => r.OwnerUsername == src.Username);
                            foreach (var niche in src.Niches)
                            {
                                if (!TopNichesByDownload.ContainsKey(niche.NicheTagName))
                                    TopNichesByDownload[niche.NicheTagName] = 0;
                                TopNichesByDownload[niche.NicheTagName] += count;
                            }
                        }
                    }
                }

                // 5. ReelsWithHighEngagement (above average views or comments)
                double avgViews = AverageViewCountPerReel;
                double avgComments = reels != null && reels.Count > 0 ? reels.Average(r => r.CommentCount) : 0;
                ReelsWithHighEngagement = (reels?.Where(r => r.ViewCount > avgViews || r.CommentCount > avgComments).ToList() ?? new List<InstagramScrapeUtilities.InstagramReel>()).Take(100).ToList();

                // 6. DownloadGaps (longest period with zero downloads)
                DownloadGaps = 0;
                if (MemesDownloadedPerDay.Count > 0)
                {
                    int currentGap = 0;
                    foreach (var day in MemesDownloadedPerDay.OrderBy(kv => kv.Key))
                    {
                        if (day.Value == 0)
                            currentGap++;
                        else
                        {
                            if (currentGap > DownloadGaps)
                                DownloadGaps = currentGap;
                            currentGap = 0;
                        }
                    }
                    if (currentGap > DownloadGaps)
                        DownloadGaps = currentGap;
                }

                // 7. ReelsPerSourceStdDev
                ReelsPerSourceStdDev = 0;
                if (ReelsDownloadedPerSource.Count > 0)
                {
                    double mean = ReelsDownloadedPerSource.Values.Average();
                    double sumSq = ReelsDownloadedPerSource.Values.Sum(v => (v - mean) * (v - mean));
                    ReelsPerSourceStdDev = Math.Sqrt(sumSq / ReelsDownloadedPerSource.Count);
                }

                // 8. PercentageOfSourcesWithRecentActivity (last 7 days)
                PercentageOfSourcesWithRecentActivity = 0;
                if (sources != null && reels != null && reels.Count > 0)
                {
                    DateTime threshold = DateTime.Now.AddDays(-7);
                    int activeSources = sources.Count(s => reels.Any(r => r.OwnerUsername == s.Username && r.DateTimeReelDownloaded >= threshold));
                    PercentageOfSourcesWithRecentActivity = (TotalInstagramSources > 0) ? (double)activeSources / TotalInstagramSources * 100.0 : 0;
                }

                // 9. ReelsWithMissingMetadata
                //ReelsWithMissingMetadata = reels?.Where(r => string.IsNullOrWhiteSpace(r.Description) || string.IsNullOrWhiteSpace(r.VideoDownloadURL)).ToList() ?? new List<InstagramScrapeUtilities.InstagramReel>();

                // 13. MostCommonDownloadDayOfWeek
                MostCommonDownloadDayOfWeek = null;
                if (MemesDownloadedPerDay.Count > 0)
                {
                    var dayOfWeekCounts = MemesDownloadedPerDay
                        .GroupBy(kv => kv.Key.DayOfWeek)
                        .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value));
                    if (dayOfWeekCounts.Count > 0)
                        MostCommonDownloadDayOfWeek = dayOfWeekCounts.OrderByDescending(kv => kv.Value).First().Key;
                }

                // ReelsWithNoViews (as a list)
                ReelsWithNoViews = reels?.Where(r => r.ViewCount == 0).ToList() ?? new List<InstagramScrapeUtilities.InstagramReel>();
            }
        }
    }
}
