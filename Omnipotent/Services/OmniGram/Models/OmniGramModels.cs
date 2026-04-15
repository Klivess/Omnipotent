using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.OmniGram.Models
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum OmniGramContentType
    {
        Photo,
        Reel,
        Story,
        Carousel
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum OmniGramPostStatus
    {
        Queued,
        Posting,
        Posted,
        Failed
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum OmniGramContentSource
    {
        MemeScraper,
        ManualUpload,
        ContentFolder
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum OmniGramLoginStatus
    {
        LoggedIn,
        LoggedOut,
        CredentialsInvalid,
        AccountDisabled,
        RateLimited,
        Awaiting2FA,
        AwaitingChallenge,
        ChallengeTimedOut,
        CheckpointRequired,
        Error
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum OmniGramCaptionMode
    {
        Static,
        RandomFromList,
        AIGenerated
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum OmniGramContentSelectionMode
    {
        Sequential,
        Random
    }

    // ── Per-Account Content Configuration ──

    public class OmniGramAccountContentConfig
    {
        public OmniGramContentSource ContentSource { get; set; } = OmniGramContentSource.MemeScraper;
        public List<OmniGramContentType> AllowedContentTypes { get; set; } = new() { OmniGramContentType.Reel };
        public string ContentFolderPath { get; set; }
        public OmniGramContentSelectionMode SelectionMode { get; set; } = OmniGramContentSelectionMode.Random;

        // Caption configuration
        public OmniGramCaptionMode CaptionMode { get; set; } = OmniGramCaptionMode.Static;
        public string StaticCaption { get; set; } = "";
        public List<string> CandidateCaptions { get; set; } = new();
        public string AICaptionPrompt { get; set; } = "Write a short engaging Instagram caption for this content.";

        // Hashtag configuration
        public List<string> Hashtags { get; set; } = new();
        public bool RotateHashtags { get; set; }
        public int MaxHashtagsPerPost { get; set; } = 20;

        // Scheduling
        public int PostsPerDay { get; set; } = 3;
        public int MinIntervalMinutes { get; set; } = 120;
        public List<int> PreferredPostHoursUTC { get; set; } = new() { 9, 13, 18 };
        public List<int> ActiveDaysOfWeek { get; set; } = new() { 0, 1, 2, 3, 4, 5, 6 };
        public int ScheduleRandomOffsetMinutes { get; set; } = 30;

        // MemeScraper-specific
        public string MemeScraperNicheFilter { get; set; } = "";
        public bool UseAICaptionsForMemeScraper { get; set; }

        // Content folder tracking
        public List<string> UsedContentPaths { get; set; } = new();
    }

    // ── Account ──

    public class OmniGramAccount
    {
        public string AccountId { get; set; } = RandomGeneration.GenerateRandomLengthOfNumbers(8);
        public string Username { get; set; }

        [JsonIgnore]
        public string Password { get; set; }

        public string ProxyAddress { get; set; }
        public bool IsActive { get; set; } = true;
        public bool IsPaused { get; set; }
        public DateTime AddedDate { get; set; } = DateTime.UtcNow;
        public DateTime? LastLoginTime { get; set; }
        public DateTime? LastPostTime { get; set; }
        public DateTime? LastLoginAttemptTime { get; set; }
        public List<string> Tags { get; set; } = new();
        public string Notes { get; set; }

        public int FollowerCount { get; set; }
        public int FollowingCount { get; set; }
        public int MediaCount { get; set; }
        public string ProfilePicUrl { get; set; }
        public string Biography { get; set; }

        public OmniGramLoginStatus LoginStatus { get; set; } = OmniGramLoginStatus.LoggedOut;
        public int LoginRetryCount { get; set; }
        public string LoginErrorMessage { get; set; }

        public OmniGramAccountContentConfig ContentConfig { get; set; } = new();
    }

    // ── Post ──

    public class OmniGramPost
    {
        public string PostId { get; set; } = RandomGeneration.GenerateRandomLengthOfNumbers(8);
        public string AccountId { get; set; }
        public OmniGramContentType ContentType { get; set; }
        public List<string> MediaPaths { get; set; } = new();
        public string Caption { get; set; }
        public List<string> Hashtags { get; set; } = new();
        public DateTime ScheduledTime { get; set; }
        public DateTime? PostedTime { get; set; }
        public OmniGramPostStatus Status { get; set; } = OmniGramPostStatus.Queued;
        public string ErrorMessage { get; set; }
        public OmniGramContentSource SourceType { get; set; }
        public string SourceId { get; set; }
        public string InstagramMediaId { get; set; }
        public int RetryCount { get; set; }
        public int MaxRetries { get; set; } = 3;

        public string GetCaptionWithHashtags()
        {
            if (Hashtags == null || Hashtags.Count == 0)
                return Caption ?? string.Empty;
            var tags = string.Join(" ", Hashtags.Select(h => h.StartsWith("#") ? h : $"#{h}"));
            return string.IsNullOrEmpty(Caption) ? tags : $"{Caption}\n\n{tags}";
        }
    }

    // ── Analytics ──

    public class OmniGramAnalyticsSnapshot
    {
        public string AccountId { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public int FollowerCount { get; set; }
        public int FollowingCount { get; set; }
        public int MediaCount { get; set; }
        public double EngagementRate { get; set; }
        public int FollowerChange { get; set; }
        public int FollowingChange { get; set; }
        public int MediaChange { get; set; }
        public double AvgLikes { get; set; }
        public double AvgComments { get; set; }
        public int PostsToday { get; set; }
        public double ReachEstimate { get; set; }
        public double ProfileVisits { get; set; }
    }

    public class OmniGramAccountAnalyticsSummary
    {
        public string AccountId { get; set; }
        public string Username { get; set; }
        public int CurrentFollowers { get; set; }
        public int CurrentFollowing { get; set; }
        public int CurrentMediaCount { get; set; }
        public double LatestEngagementRate { get; set; }
        public int FollowerChange1d { get; set; }
        public int FollowerChange7d { get; set; }
        public int FollowerChange30d { get; set; }
        public double AvgEngagement7d { get; set; }
        public int TotalPostsLast7d { get; set; }
        public int TotalPostsLast30d { get; set; }
        public double BestEngagementRate { get; set; }
        public int PeakFollowerCount { get; set; }
        public List<OmniGramAnalyticsDataPoint> FollowerHistory { get; set; } = new();
        public List<OmniGramAnalyticsDataPoint> EngagementHistory { get; set; } = new();
        public List<OmniGramAnalyticsDataPoint> MediaHistory { get; set; } = new();
    }

    public class OmniGramAnalyticsDataPoint
    {
        public DateTime Timestamp { get; set; }
        public double Value { get; set; }
    }

    // ── Fleet Dashboard Stats ──

    public class OmniGramDashboardStats
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
        public int TotalFollowers { get; set; }
        public int FollowerGainToday { get; set; }
        public double AvgEngagementRate { get; set; }
        public int PostsToday { get; set; }
        public int PostsThisWeek { get; set; }
    }

    // ── Event Log ──

    public class OmniGramEvent
    {
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string AccountId { get; set; }
        public string EventType { get; set; }
        public string Message { get; set; }
        public string Severity { get; set; } = "Info";
    }
}
