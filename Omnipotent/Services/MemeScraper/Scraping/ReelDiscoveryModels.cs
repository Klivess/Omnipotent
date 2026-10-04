namespace Omnipotent.Services.MemeScraper.Scraping
{
    /// <summary>
    /// One reel as seen by a discovery provider, before it is downloaded and persisted as an
    /// <see cref="InstagramScrapeUtilities.InstagramReel"/>. Carries every video URL the provider
    /// offered so the downloader can fall through them when one has expired or is refused.
    /// </summary>
    public sealed class DiscoveredReel
    {
        public string PostID = "";
        public string ShortCode = "";
        public string OwnerUsername = "";
        public string OwnerID = "";
        public long ViewCount;
        public long LikeCount;
        public int CommentCount;
        public DateTime CreatedAtUtc;
        public string Description = "";
        public List<string> VideoUrls = new();
        public string Provider = "";

        public bool HasIdentity => !string.IsNullOrEmpty(PostID) || !string.IsNullOrEmpty(ShortCode);

        /// <summary>Stable key used to merge the same reel reported by different providers.</summary>
        public string Key => !string.IsNullOrEmpty(ShortCode) ? "c:" + ShortCode : "p:" + PostID;

        /// <summary>Fills blanks from <paramref name="other"/> and unions its video URLs (ours first).</summary>
        public void MergeFrom(DiscoveredReel other)
        {
            if (string.IsNullOrEmpty(PostID)) PostID = other.PostID;
            if (string.IsNullOrEmpty(ShortCode)) ShortCode = other.ShortCode;
            if (string.IsNullOrEmpty(OwnerUsername)) OwnerUsername = other.OwnerUsername;
            if (string.IsNullOrEmpty(OwnerID)) OwnerID = other.OwnerID;
            if (string.IsNullOrEmpty(Description)) Description = other.Description;
            if (CreatedAtUtc == default) CreatedAtUtc = other.CreatedAtUtc;
            ViewCount = Math.Max(ViewCount, other.ViewCount);
            LikeCount = Math.Max(LikeCount, other.LikeCount);
            CommentCount = Math.Max(CommentCount, other.CommentCount);
            foreach (var url in other.VideoUrls)
            {
                if (!VideoUrls.Contains(url)) VideoUrls.Add(url);
            }
            if (!Provider.Split('+').Contains(other.Provider)) Provider = string.IsNullOrEmpty(Provider) ? other.Provider : Provider + "+" + other.Provider;
        }
    }

    /// <summary>What the scraper already holds, so providers can stop paging once they reach it.</summary>
    public sealed class ReelDiscoveryRequest
    {
        public required string Username;
        public Func<DiscoveredReel, bool> IsKnown = _ => false;
        /// <summary>Stop paging once a whole page is already-downloaded reels.</summary>
        public bool StopWhenCaughtUp = true;
        public int MaxPages = 50;
        /// <summary>Per-reel page loads allowed (Instagram provider); null uses the provider's default.</summary>
        public int? MaxDetailPages;
    }

    /// <summary>One provider's answer for one profile.</summary>
    public sealed class ReelDiscoveryResult
    {
        public string Provider = "";
        public List<DiscoveredReel> Reels = new();
        /// <summary>Reels the provider could see listed (≥ Reels.Count when it only resolves some of them).</summary>
        public int Listed;
        public int PagesFetched;
        /// <summary>True when the provider walked back to reels we already had, or to the end of the profile.</summary>
        public bool CoveredAllNewReels;
        /// <summary>Why paging stopped early, if it did (null when it finished normally).</summary>
        public string? TruncatedReason;
    }

    /// <summary>A provider that can list a profile's reels.</summary>
    public interface IReelProvider
    {
        string Name { get; }
        Task<ReelDiscoveryResult> DiscoverAsync(ReelDiscoveryRequest request, CancellationToken ct);
    }

    /// <summary>A provider that can produce fresh video URLs for a single reel (used when stored URLs have expired).</summary>
    public interface IReelResolver
    {
        Task<Dictionary<string, DiscoveredReel>> ResolveAsync(IReadOnlyCollection<string> shortCodes, CancellationToken ct);
    }

    /// <summary>Profile facts needed to register a new source.</summary>
    public sealed class DiscoveredProfile
    {
        public string Username = "";
        public string AccountID = "";
        public string FullName = "";
        public int Followers;
        public string ProfilePictureUrl = "";
        public string Bio = "";
        public float AverageLikes;
        public float AverageComments;
        public List<(string Name, int Count, string Url)> Hashtags = new();
        public string Provider = "";
    }

    /// <summary>
    /// A provider failed in a way worth reporting verbatim (blocked, rate limited, layout changed).
    /// The message is written to the source's health record, so keep it specific.
    /// </summary>
    public sealed class ReelProviderException : Exception
    {
        public string Provider { get; }
        public ReelProviderException(string provider, string message, Exception? inner = null)
            : base($"[{provider}] {message}", inner)
        {
            Provider = provider;
        }
    }
}
