using Omnipotent.Services.OmniTumblr.Models;
using System.Collections.Concurrent;

namespace Omnipotent.Services.OmniTumblr.Engine
{
    /// <summary>A piece of content the planner can turn into a post.</summary>
    public sealed class ContentCandidate
    {
        public string Key { get; set; } = "";
        public ContentSourceKind Source { get; set; }
        public PostKind Kind { get; set; }
        public string FilePath { get; set; } = "";
        public long Bytes { get; set; }
        public string? Origin { get; set; }
        public string? OriginalCaption { get; set; }
        public string? OriginalUrl { get; set; }
        public long? Views { get; set; }
        public long? Likes { get; set; }
        public DateTime? CreatedUtc { get; set; }
        public double? DurationSeconds { get; set; }
        public double Score { get; set; }
    }

    /// <summary>A scraped reel, decoupled from MemeScraper's own model.</summary>
    public sealed class ReelInfo
    {
        public string PostId { get; set; } = "";
        public string? ShortCode { get; set; }
        public string? Owner { get; set; }
        public string? Description { get; set; }
        public long Views { get; set; }
        public long Likes { get; set; }
        public long Comments { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime DownloadedUtc { get; set; }
        /// <summary>Absolute path of the downloaded video, if any.</summary>
        public string? FilePath { get; set; }
        public string? Url { get; set; }
    }

    public interface IReelCatalog
    {
        /// <summary>False until MemeScraper has loaded its reels.</summary>
        bool IsReady { get; }
        IReadOnlyList<ReelInfo> GetReels();
        /// <summary>Instagram username (lowercase) → the MemeScraper niches its source is tagged with.</summary>
        IReadOnlyDictionary<string, IReadOnlyList<string>> GetSourceNiches();
    }

    /// <summary>Reads MemeScraper's reels (resolving their base-directory-relative paths).</summary>
    internal sealed class MemeScraperReelCatalog : IReelCatalog
    {
        private readonly Func<Omnipotent.Services.MemeScraper.MemeScraper?> resolve;
        private readonly object cacheGate = new();
        private (DateTime At, List<ReelInfo> Reels)? cache;
        private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(45);

        public MemeScraperReelCatalog(Func<Omnipotent.Services.MemeScraper.MemeScraper?> resolve) => this.resolve = resolve;

        public bool IsReady => resolve()?.mediaManager != null;

        public IReadOnlyList<ReelInfo> GetReels()
        {
            lock (cacheGate)
            {
                if (cache is { } c && DateTime.UtcNow - c.At < CacheFor) return c.Reels;
            }
            var scraper = resolve();
            var media = scraper?.mediaManager;
            if (media == null) return Array.Empty<ReelInfo>();
            var reels = media.allScrapedReels.Where(r => r != null && !string.IsNullOrEmpty(r.PostID)).Select(r => new ReelInfo
            {
                PostId = r.PostID,
                ShortCode = r.ShortCode,
                Owner = r.OwnerUsername,
                Description = r.Description,
                Views = r.ViewCount,
                Likes = r.LikeCount,
                Comments = r.CommentCount,
                CreatedUtc = DateTime.SpecifyKind(r.CreatedAt, r.CreatedAt.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : r.CreatedAt.Kind).ToUniversalTime(),
                DownloadedUtc = r.DateTimeReelDownloaded.ToUniversalTime(),
                FilePath = string.IsNullOrWhiteSpace(r.InstagramReelVideoFilePath)
                    ? null
                    : Path.IsPathRooted(r.InstagramReelVideoFilePath) ? r.InstagramReelVideoFilePath : r.GetInstagramReelVideoFilePath(),
                Url = !string.IsNullOrWhiteSpace(r.ShortURL) ? r.ShortURL
                    : !string.IsNullOrWhiteSpace(r.ShortCode) ? $"https://www.instagram.com/reel/{r.ShortCode}/" : null,
            }).ToList();
            lock (cacheGate) cache = (DateTime.UtcNow, reels);
            return reels;
        }

        public IReadOnlyDictionary<string, IReadOnlyList<string>> GetSourceNiches()
        {
            var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var sources = resolve()?.SourceManager?.InstagramSources;
                if (sources == null) return result;
                foreach (var s in sources.ToList())
                {
                    if (string.IsNullOrWhiteSpace(s?.Username)) continue;
                    result[s.Username] = (s.Niches ?? new()).Where(n => !string.IsNullOrWhiteSpace(n?.NicheTagName)).Select(n => n.NicheTagName).ToList();
                }
            }
            catch { }
            return result;
        }
    }

    /// <summary>
    /// Finds content for a blog according to its strategy: MemeScraper reels (filtered by niche, source,
    /// age, views and duration), a folder on disk, or the blog's own uploaded library. Keys are stable
    /// ("ig:{postId}", "file:{name}:{size}", "lib:{name}") so used content is never posted twice.
    /// </summary>
    internal sealed class OmniTumblrContentSources
    {
        private readonly IReelCatalog catalog;
        private readonly OmniTumblrMediaTools media;
        private readonly string libraryRoot;
        private readonly ConcurrentDictionary<string, double?> durations = new(StringComparer.Ordinal);

        public OmniTumblrContentSources(IReelCatalog catalog, OmniTumblrMediaTools media, string libraryRoot)
        {
            this.catalog = catalog;
            this.media = media;
            this.libraryRoot = libraryRoot;
        }

        public bool CatalogReady => catalog.IsReady;

        public static string ReelKey(string postId) => "ig:" + postId;
        public static string FileKey(FileInfo file) => $"file:{file.Name.ToLowerInvariant()}:{file.Length}";
        public static string LibraryKey(string fileName) => "lib:" + fileName.ToLowerInvariant();
        public string LibraryDirectoryFor(string blogId) => Path.Combine(libraryRoot, blogId);

        /// <summary>
        /// Up to <paramref name="needed"/> candidates in the strategy's preferred order, excluding
        /// <paramref name="excluded"/> keys. Probes durations lazily, only for the candidates it returns.
        /// </summary>
        public async Task<List<ContentCandidate>> FindCandidatesAsync(OmniTumblrStrategy strategy, string blogId,
            IReadOnlySet<string> excluded, int needed, DateTime nowUtc, CancellationToken ct)
        {
            var ordered = Enumerate(strategy, blogId, excluded, nowUtc);
            var picked = new List<ContentCandidate>();
            int maxDuration = strategy.Source == ContentSourceKind.MemeScraper ? strategy.MemeScraper.MaxDurationSeconds : 0;
            int probed = 0;
            foreach (var candidate in ordered)
            {
                if (picked.Count >= needed || ct.IsCancellationRequested) break;
                if (!File.Exists(candidate.FilePath)) continue;
                if (candidate.Kind == PostKind.Video)
                {
                    if (!durations.TryGetValue(candidate.Key, out var duration) && probed < 40)
                    {
                        probed++;
                        duration = (await media.ProbeAsync(candidate.FilePath, ct))?.DurationSeconds;
                        durations[candidate.Key] = duration;
                    }
                    candidate.DurationSeconds = duration;
                    if (maxDuration > 0 && duration is double d && d > maxDuration) continue;
                }
                try { candidate.Bytes = new FileInfo(candidate.FilePath).Length; } catch { }
                picked.Add(candidate);
            }
            return picked;
        }

        /// <summary>
        /// Finds a candidate by its key within what this blog's strategy can see (no exclusions), so the
        /// website can preview content by key without ever passing file paths around.
        /// </summary>
        public ContentCandidate? Resolve(OmniTumblrStrategy strategy, string blogId, string key, DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            var none = new HashSet<string>(StringComparer.Ordinal);
            if (key.StartsWith("ig:", StringComparison.Ordinal))
            {
                string postId = key[3..];
                var reel = catalog.GetReels().FirstOrDefault(r => r.PostId == postId);
                if (reel?.FilePath == null) return null;
                return new ContentCandidate
                {
                    Key = key, Source = ContentSourceKind.MemeScraper, Kind = PostKind.Video, FilePath = reel.FilePath,
                    Origin = reel.Owner, OriginalCaption = reel.Description, OriginalUrl = reel.Url,
                    Views = reel.Views, Likes = reel.Likes, CreatedUtc = reel.CreatedUtc,
                };
            }
            return Enumerate(strategy, blogId, none, nowUtc).FirstOrDefault(c => c.Key == key);
        }

        public sealed class NicheOption
        {
            public string Name { get; set; } = "";
            public int Sources { get; set; }
            public int Reels { get; set; }
        }

        public sealed class SourceOption
        {
            public string Username { get; set; } = "";
            public List<string> Niches { get; set; } = new();
            public int Reels { get; set; }
            public DateTime? NewestUtc { get; set; }
        }

        public sealed class CatalogOptions
        {
            public bool Ready { get; set; }
            public int TotalReels { get; set; }
            public int DownloadedReels { get; set; }
            public List<NicheOption> Niches { get; set; } = new();
            public List<SourceOption> Sources { get; set; } = new();
        }

        /// <summary>The niches and source accounts MemeScraper offers, with reel counts, for the strategy editor.</summary>
        public CatalogOptions DescribeCatalog()
        {
            var options = new CatalogOptions { Ready = catalog.IsReady };
            if (!options.Ready) return options;
            var reels = catalog.GetReels();
            var niches = catalog.GetSourceNiches();
            options.TotalReels = reels.Count;
            options.DownloadedReels = reels.Count(r => !string.IsNullOrWhiteSpace(r.FilePath));
            var byOwner = reels.Where(r => !string.IsNullOrWhiteSpace(r.Owner))
                .GroupBy(r => r.Owner!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (Count: g.Count(), Newest: g.Max(r => r.CreatedUtc)), StringComparer.OrdinalIgnoreCase);
            foreach (string owner in byOwner.Keys.Union(niches.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(o => o, StringComparer.OrdinalIgnoreCase))
            {
                byOwner.TryGetValue(owner, out var stats);
                niches.TryGetValue(owner, out var ownerNiches);
                options.Sources.Add(new SourceOption
                {
                    Username = owner,
                    Niches = ownerNiches?.ToList() ?? new List<string>(),
                    Reels = stats.Count,
                    NewestUtc = stats.Count > 0 ? stats.Newest : null,
                });
            }
            options.Niches = options.Sources
                .SelectMany(s => s.Niches.Select(n => (Niche: n, s.Reels)))
                .GroupBy(x => x.Niche, StringComparer.OrdinalIgnoreCase)
                .Select(g => new NicheOption { Name = g.Key, Sources = g.Count(), Reels = g.Sum(x => x.Reels) })
                .OrderByDescending(x => x.Reels)
                .ToList();
            return options;
        }

        /// <summary>How much eligible content is left (cheap: no probing), plus the next few in line.</summary>
        public (int Eligible, List<ContentCandidate> Next) Runway(OmniTumblrStrategy strategy, string blogId, IReadOnlySet<string> excluded, DateTime nowUtc, int preview = 12)
        {
            var all = Enumerate(strategy, blogId, excluded, nowUtc).Where(c => File.Exists(c.FilePath)).ToList();
            return (all.Count, all.Take(preview).ToList());
        }

        private IEnumerable<ContentCandidate> Enumerate(OmniTumblrStrategy strategy, string blogId, IReadOnlySet<string> excluded, DateTime nowUtc)
        {
            return strategy.Source switch
            {
                ContentSourceKind.MemeScraper => FromReels(strategy.MemeScraper, excluded, nowUtc),
                ContentSourceKind.Folder => FromFolder(strategy.Folder, excluded),
                ContentSourceKind.Library => FromLibrary(blogId, excluded),
                _ => Enumerable.Empty<ContentCandidate>(),
            };
        }

        private IEnumerable<ContentCandidate> FromReels(MemeScraperContentFilter filter, IReadOnlySet<string> excluded, DateTime nowUtc)
        {
            var reels = catalog.GetReels();
            if (reels.Count == 0) return Enumerable.Empty<ContentCandidate>();
            var nicheMap = filter.Niches.Count > 0 ? catalog.GetSourceNiches() : null;
            var wantedSources = new HashSet<string>(filter.Sources.Select(s => s.Trim().TrimStart('@')).Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
            var wantedNiches = new HashSet<string>(filter.Niches.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
            DateTime? minCreated = filter.MaxAgeDays > 0 ? nowUtc.AddDays(-filter.MaxAgeDays) : null;

            var eligible = reels.Where(r =>
            {
                if (string.IsNullOrWhiteSpace(r.FilePath)) return false;
                if (excluded.Contains(ReelKey(r.PostId))) return false;
                if (wantedSources.Count > 0 && (r.Owner == null || !wantedSources.Contains(r.Owner))) return false;
                if (wantedNiches.Count > 0)
                {
                    if (r.Owner == null || nicheMap == null || !nicheMap.TryGetValue(r.Owner, out var niches)) return false;
                    if (!niches.Any(wantedNiches.Contains)) return false;
                }
                // Reels whose post date is unknown fall back to when they were downloaded.
                DateTime age = r.CreatedUtc.Year > 2000 ? r.CreatedUtc : r.DownloadedUtc;
                if (minCreated.HasValue && age < minCreated.Value) return false;
                if (filter.MinViews > 0 && r.Views < filter.MinViews) return false;
                return true;
            }).Select(r => new ContentCandidate
            {
                Key = ReelKey(r.PostId),
                Source = ContentSourceKind.MemeScraper,
                Kind = PostKind.Video,
                FilePath = r.FilePath!,
                Origin = r.Owner,
                OriginalCaption = r.Description,
                OriginalUrl = r.Url,
                Views = r.Views,
                Likes = r.Likes,
                CreatedUtc = r.CreatedUtc.Year > 2000 ? r.CreatedUtc : r.DownloadedUtc,
                Score = ReelScore(r, nowUtc),
            });

            return Order(eligible, filter.Pick);
        }

        /// <summary>Popularity on Instagram, discounted by age: a strong recent reel beats a viral fossil.</summary>
        internal static double ReelScore(ReelInfo r, DateTime nowUtc)
        {
            DateTime created = r.CreatedUtc.Year > 2000 ? r.CreatedUtc : r.DownloadedUtc;
            double ageDays = Math.Max(0, (nowUtc - created).TotalDays);
            return Math.Log10(r.Views + 1) + 0.6 * Math.Log10(r.Likes + 1) + 0.2 * Math.Log10(r.Comments + 1) - ageDays / 45.0;
        }

        private IEnumerable<ContentCandidate> FromFolder(FolderContentSettings folder, IReadOnlySet<string> excluded)
        {
            if (string.IsNullOrWhiteSpace(folder.Path) || !Directory.Exists(folder.Path)) return Enumerable.Empty<ContentCandidate>();
            IEnumerable<FileInfo> files;
            try
            {
                files = new DirectoryInfo(folder.Path)
                    .EnumerateFiles("*", folder.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    .ToList();
            }
            catch
            {
                return Enumerable.Empty<ContentCandidate>();
            }
            var eligible = files.Where(f =>
                    (folder.Videos && OmniTumblrMediaTools.IsVideo(f.Name)) || (folder.Images && OmniTumblrMediaTools.IsImage(f.Name)))
                .Where(f => !excluded.Contains(FileKey(f)))
                .Select(f => new ContentCandidate
                {
                    Key = FileKey(f),
                    Source = ContentSourceKind.Folder,
                    Kind = OmniTumblrMediaTools.IsVideo(f.Name) ? PostKind.Video : PostKind.Photo,
                    FilePath = f.FullName,
                    Origin = Path.GetFileName(folder.Path.TrimEnd('\\', '/')),
                    CreatedUtc = f.LastWriteTimeUtc,
                    Score = f.LastWriteTimeUtc.Ticks,
                });
            return Order(eligible, folder.Pick == ContentPick.Best ? ContentPick.Newest : folder.Pick);
        }

        private IEnumerable<ContentCandidate> FromLibrary(string blogId, IReadOnlySet<string> excluded)
        {
            string dir = LibraryDirectoryFor(blogId);
            if (!Directory.Exists(dir)) return Enumerable.Empty<ContentCandidate>();
            return new DirectoryInfo(dir).EnumerateFiles()
                .Where(f => OmniTumblrMediaTools.IsSupported(f.Name))
                .Where(f => !excluded.Contains(LibraryKey(f.Name)))
                .OrderBy(f => f.CreationTimeUtc) // first in, first out
                .Select(f => new ContentCandidate
                {
                    Key = LibraryKey(f.Name),
                    Source = ContentSourceKind.Library,
                    Kind = OmniTumblrMediaTools.IsVideo(f.Name) ? PostKind.Video : PostKind.Photo,
                    FilePath = f.FullName,
                    Origin = "library",
                    CreatedUtc = f.CreationTimeUtc,
                })
                .ToList();
        }

        private static IEnumerable<ContentCandidate> Order(IEnumerable<ContentCandidate> items, ContentPick pick) => pick switch
        {
            ContentPick.Newest => items.OrderByDescending(c => c.CreatedUtc ?? DateTime.MinValue),
            ContentPick.Oldest => items.OrderBy(c => c.CreatedUtc ?? DateTime.MaxValue),
            ContentPick.Random => items.OrderBy(_ => Random.Shared.Next()),
            _ => items.OrderByDescending(c => c.Score),
        };
    }
}
