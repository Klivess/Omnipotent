using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.OmniTumblr.Models
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum OmniTumblrPostType
    {
        Photo,
        PhotoSet,
        Video,
        Text,
        Quote,
        Link
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum OmniTumblrPostStatus
    {
        Queued,
        Posting,
        Posted,
        Failed
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum OmniTumblrContentSource
    {
        ManualUpload,
        ContentFolder,
        MemeScraper
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum OmniTumblrConnectionStatus
    {
        Connected,
        Disconnected,
        Error,
        RateLimited
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum OmniTumblrCaptionMode
    {
        Static,
        RandomFromList,
        AIGenerated
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum OmniTumblrContentSelectionMode
    {
        Sequential,
        Random
    }

    // ── Per-Account Content Configuration ──

    public class OmniTumblrAccountContentConfig
    {
        public OmniTumblrContentSource ContentSource { get; set; } = OmniTumblrContentSource.ContentFolder;
        public List<OmniTumblrPostType> AllowedPostTypes { get; set; } = new() { OmniTumblrPostType.Photo, OmniTumblrPostType.Video };
        public string ContentFolderPath { get; set; }
        public OmniTumblrContentSelectionMode SelectionMode { get; set; } = OmniTumblrContentSelectionMode.Random;

        // Caption / body configuration
        public OmniTumblrCaptionMode CaptionMode { get; set; } = OmniTumblrCaptionMode.Static;
        public string StaticCaption { get; set; } = "";
        public List<string> CandidateCaptions { get; set; } = new();
        public string AICaptionPrompt { get; set; } = "Write a short engaging Tumblr caption for this content.";

        // Tags (Tumblr equivalent of hashtags)
        public List<string> Tags { get; set; } = new();
        public bool RotateTags { get; set; }
        public int MaxTagsPerPost { get; set; } = 20;

        // Scheduling
        public int PostsPerDay { get; set; } = 3;
        public int MinIntervalMinutes { get; set; } = 120;
        public List<int> PreferredPostHoursUTC { get; set; } = new() { 9, 13, 18 };
        public List<int> ActiveDaysOfWeek { get; set; } = new() { 0, 1, 2, 3, 4, 5, 6 };
        public int ScheduleRandomOffsetMinutes { get; set; } = 30;

        // Content folder tracking
        public List<string> UsedContentPaths { get; set; } = new();

        // MemeScraper-specific
        public string MemeScraperNicheFilter { get; set; } = "";
        public bool UseAICaptionsForMemeScraper { get; set; }
    }

    // ── Account ──

    public class OmniTumblrAccount
    {
        public string AccountId { get; set; } = RandomGeneration.GenerateRandomLengthOfNumbers(8);
        public string BlogName { get; set; }

        [JsonIgnore]
        public string ConsumerKey { get; set; }
        [JsonIgnore]
        public string ConsumerSecret { get; set; }
        [JsonIgnore]
        public string? OAuthToken { get; set; }
        [JsonIgnore]
        public string? OAuthTokenSecret { get; set; }

        public bool IsActive { get; set; } = true;
        public bool IsPaused { get; set; }
        public DateTime AddedDate { get; set; } = DateTime.UtcNow;
        public DateTime? LastPostTime { get; set; }
        public List<string> Tags { get; set; } = new();
        public string Notes { get; set; }

        public long FollowerCount { get; set; }
        public long PostCount { get; set; }
        public long LikesCount { get; set; }
        public string AvatarUrl { get; set; }
        public string Description { get; set; }
        public string Title { get; set; }
        public string Url { get; set; }

        public OmniTumblrConnectionStatus ConnectionStatus { get; set; } = OmniTumblrConnectionStatus.Disconnected;
        public string ConnectionErrorMessage { get; set; }

        public OmniTumblrAccountContentConfig ContentConfig { get; set; } = new();
    }

    // ── Post ──

    public class OmniTumblrPost
    {
        public string PostId { get; set; } = RandomGeneration.GenerateRandomLengthOfNumbers(8);
        public string AccountId { get; set; }
        public OmniTumblrPostType PostType { get; set; }
        public List<string> MediaPaths { get; set; } = new();
        public string Caption { get; set; }
        public string Title { get; set; }
        public string SourceUrl { get; set; }
        public string QuoteSource { get; set; }
        public List<string> Tags { get; set; } = new();
        public DateTime ScheduledTime { get; set; }
        public DateTime? PostedTime { get; set; }
        public OmniTumblrPostStatus Status { get; set; } = OmniTumblrPostStatus.Queued;
        public string ErrorMessage { get; set; }
        public OmniTumblrContentSource SourceType { get; set; }
        public string SourceId { get; set; }
        public long? TumblrPostId { get; set; }
        public int RetryCount { get; set; }
        public int MaxRetries { get; set; } = 3;

        public string GetBody()
        {
            if (Tags == null || Tags.Count == 0)
                return Caption ?? string.Empty;
            return Caption ?? string.Empty;
        }

        public List<string> GetNormalisedTags()
        {
            if (Tags == null) return new List<string>();
            return Tags.Select(t => t.TrimStart('#')).ToList();
        }
    }

    // ── Analytics ──

    public class OmniTumblrAnalyticsSnapshot
    {
        public string AccountId { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public long FollowerCount { get; set; }
        public long PostCount { get; set; }
        public long LikesCount { get; set; }
        public double AvgNotesPerPost { get; set; }
        public double EngagementRate { get; set; }
        public long FollowerChange { get; set; }
        public long PostChange { get; set; }
        public int PostsToday { get; set; }
    }

    public class OmniTumblrAnalyticsDataPoint
    {
        public DateTime Timestamp { get; set; }
        public double Value { get; set; }
    }

    public class OmniTumblrAccountAnalyticsSummary
    {
        public string AccountId { get; set; }
        public string BlogName { get; set; }
        public long CurrentFollowers { get; set; }
        public long CurrentPostCount { get; set; }
        public long CurrentLikes { get; set; }
        public double LatestEngagementRate { get; set; }
        public long FollowerChange1d { get; set; }
        public long FollowerChange7d { get; set; }
        public long FollowerChange30d { get; set; }
        public double AvgEngagement7d { get; set; }
        public int TotalPostsLast7d { get; set; }
        public int TotalPostsLast30d { get; set; }
        public double BestEngagementRate { get; set; }
        public long PeakFollowerCount { get; set; }
        public List<OmniTumblrAnalyticsDataPoint> FollowerHistory { get; set; } = new();
        public List<OmniTumblrAnalyticsDataPoint> EngagementHistory { get; set; } = new();
        public List<OmniTumblrAnalyticsDataPoint> PostHistory { get; set; } = new();
    }

    // ── Events ──

    public class OmniTumblrEvent
    {
        public string AccountId { get; set; }
        public string EventType { get; set; }
        public string Message { get; set; }
        public string Severity { get; set; } = "Info";
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    // ── Dashboard Stats ──

    public class OmniTumblrDashboardStats
    {
        public int TotalAccounts { get; set; }
        public int ActiveAccounts { get; set; }
        public int PausedAccounts { get; set; }
        public int ErrorAccounts { get; set; }
        public int TotalPosts { get; set; }
        public int PostedCount { get; set; }
        public double SuccessRate { get; set; }
        public int PendingCount { get; set; }
        public int FailedCount { get; set; }
        public long TotalFollowers { get; set; }
        public long FollowerGainToday { get; set; }
        public double AvgEngagementRate { get; set; }
        public int PostsToday { get; set; }
        public int PostsThisWeek { get; set; }
    }

    // ── Content Folder File Info ──

    public class OmniTumblrContentFolderFileInfo
    {
        public string FileName { get; set; }
        public string FullPath { get; set; }
        public string PostType { get; set; }
        public long SizeBytes { get; set; }
        public bool IsUsed { get; set; }
    }
}
