namespace Omnipotent.Services.OmniTumblr
{
    public enum OmniTumblrDispatchMode
    {
        SingleAccount = 0,
        AllManagedAccounts = 1
    }

    public enum OmniTumblrPostStatus
    {
        Pending = 0,
        Posting = 1,
        Posted = 2,
        Failed = 3,
        Cancelled = 4
    }

    public enum OmniTumblrAccountStatus
    {
        Active = 0,
        NeedsVerification = 1,
        Disabled = 2
    }

    public class OmniTumblrAccount
    {
        public string AccountId { get; set; } = "";
        public string Email { get; set; } = "";
        public string EncryptedPassword { get; set; } = "";
        public string BlogName { get; set; } = "";
        public string OAuthTokenKey { get; set; } = "";
        public string OAuthTokenSecret { get; set; } = "";
        public OmniTumblrAccountStatus Status { get; set; } = OmniTumblrAccountStatus.Active;
        public bool UseMemeScraperSource { get; set; }
        public List<string> PreferredMemeNiches { get; set; } = new();
        public bool AutonomousPostingEnabled { get; set; } = true;
        public int AutonomousPostingIntervalMinutes { get; set; } = 240;
        public int AutonomousPostingRandomOffsetMinutes { get; set; } = 0;
        public string? AutonomousCaptionPrompt { get; set; }
        public List<string> PostedMemeReelPostIds { get; set; } = new();
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public DateTime? LastAuthenticatedUtc { get; set; }
        public string? LastAuthenticationError { get; set; }
        public string AddedBy { get; set; } = "";
    }

    public class OmniTumblrCampaign
    {
        public string CampaignId { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; }
        public string CreatedBy { get; set; } = "";
        public OmniTumblrDispatchMode DispatchMode { get; set; }
        public string Status { get; set; } = "Scheduled";
        public List<string> PlannedPostIds { get; set; } = new();
    }

    public class OmniTumblrPostPlan
    {
        public string PostId { get; set; } = "";
        public string CampaignId { get; set; } = "";
        public string AccountId { get; set; } = "";
        public string? UserCaption { get; set; }
        public string? AICaptionPrompt { get; set; }
        public string? MediaPath { get; set; }
        public DateTime ScheduledForUtc { get; set; }
        public OmniTumblrPostStatus Status { get; set; } = OmniTumblrPostStatus.Pending;
        public int RetryCount { get; set; }
        public string? LastError { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? LastAttemptUtc { get; set; }
        public DateTime? PostedAtUtc { get; set; }
        public string? ProviderPostId { get; set; }
        public string? SelectedMemeReelPostId { get; set; }
    }

    public class OmniTumblrAddAccountRequest
    {
        public string email { get; set; } = "";
        public string password { get; set; } = "";
        public string blogName { get; set; } = "";
        public string oauthTokenKey { get; set; } = "";
        public string oauthTokenSecret { get; set; } = "";
        public bool useMemeScraperSource { get; set; }
        public List<string>? memeNiches { get; set; }
        public bool? autonomousPostingEnabled { get; set; }
        public int? autonomousPostingIntervalMinutes { get; set; }
        public int? autonomousPostingRandomOffsetMinutes { get; set; }
        public string? autonomousCaptionPrompt { get; set; }
    }

    public class OmniTumblrUpdateAccountSettingsRequest
    {
        public string accountId { get; set; } = "";
        public bool? useMemeScraperSource { get; set; }
        public List<string>? memeNiches { get; set; }
        public bool? autonomousPostingEnabled { get; set; }
        public int? autonomousPostingIntervalMinutes { get; set; }
        public int? autonomousPostingRandomOffsetMinutes { get; set; }
        public string? autonomousCaptionPrompt { get; set; }
        public string? blogName { get; set; }
        public string? oauthTokenKey { get; set; }
        public string? oauthTokenSecret { get; set; }
    }

    public class OmniTumblrScheduleRequest
    {
        public OmniTumblrDispatchMode DispatchMode { get; set; } = OmniTumblrDispatchMode.SingleAccount;
        public string? AccountId { get; set; }
        public string? UserCaption { get; set; }
        public string? AICaptionPrompt { get; set; }
        public string? MediaPath { get; set; }
        public string? UploadedFileName { get; set; }
        public DateTime? ScheduledForUtc { get; set; }
    }

    public class OmniTumblrDeletePostRequest
    {
        public string accountId { get; set; } = "";
        public long postId { get; set; }
    }

    public class OmniTumblrDeleteAccountRequest
    {
        public string accountId { get; set; } = "";
        public bool deleteAssociatedPosts { get; set; }
    }

    public class OmniTumblrServiceEvent
    {
        public string EventId { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
        public string Level { get; set; } = "Info";
        public string Message { get; set; } = "";
        public string? AccountId { get; set; }
        public string? PostId { get; set; }
        public string? MetadataJson { get; set; }
    }

    public record OmniTumblrPublishResult(bool Success, string ProviderPostId, string Error);
}
