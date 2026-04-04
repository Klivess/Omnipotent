namespace Omnipotent.Services.OmniGram
{
    public enum OmniGramDispatchMode
    {
        SingleAccount = 0,
        AllManagedAccounts = 1
    }

    public enum OmniGramCaptionMode
    {
        User = 0,
        AI = 1
    }

    public enum OmniGramPostTarget
    {
        Feed = 0,
        Reel = 1,
        Story = 2
    }

    public enum OmniGramPostStatus
    {
        Pending = 0,
        Posting = 1,
        Posted = 2,
        Failed = 3,
        Cancelled = 4
    }

    public enum OmniGramAccountStatus
    {
        Active = 0,
        NeedsVerification = 1,
        Disabled = 2
    }

    public class OmniGramAccount
    {
        public string AccountId { get; set; } = "";
        public string Username { get; set; } = "";
        public string EncryptedPassword { get; set; } = "";
        public OmniGramAccountStatus Status { get; set; } = OmniGramAccountStatus.Active;
        public bool UseMemeScraperSource { get; set; }
        public string? MemeScraperSourceAccountId { get; set; }
        public bool AutonomousPostingEnabled { get; set; } = true;
        public int AutonomousPostingIntervalMinutes { get; set; } = 240;
        public string? AutonomousCaptionPrompt { get; set; }
        public List<string> PostedMemeReelPostIds { get; set; } = new();
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public DateTime? LastAuthenticatedUtc { get; set; }
        public string AddedBy { get; set; } = "";
    }

    public class OmniGramCampaign
    {
        public string CampaignId { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; }
        public string CreatedBy { get; set; } = "";
        public OmniGramDispatchMode DispatchMode { get; set; }
        public string Status { get; set; } = "Scheduled";
        public List<string> PlannedPostIds { get; set; } = new();
    }

    public class OmniGramPostPlan
    {
        public string PostId { get; set; } = "";
        public string CampaignId { get; set; } = "";
        public string AccountId { get; set; } = "";
        public OmniGramPostTarget Target { get; set; } = OmniGramPostTarget.Feed;
        public OmniGramCaptionMode CaptionMode { get; set; } = OmniGramCaptionMode.User;
        public string? UserCaption { get; set; }
        public string? AICaptionPrompt { get; set; }
        public string? MediaPath { get; set; }
        public DateTime ScheduledForUtc { get; set; }
        public OmniGramPostStatus Status { get; set; } = OmniGramPostStatus.Pending;
        public int RetryCount { get; set; }
        public string? LastError { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? LastAttemptUtc { get; set; }
        public DateTime? PostedAtUtc { get; set; }
        public string? ProviderPostId { get; set; }
        public string? SelectedMemeReelPostId { get; set; }
    }

    public class OmniGramScheduleRequest
    {
        public OmniGramDispatchMode DispatchMode { get; set; } = OmniGramDispatchMode.SingleAccount;
        public string? AccountId { get; set; }
        public OmniGramPostTarget Target { get; set; } = OmniGramPostTarget.Feed;
        public OmniGramCaptionMode CaptionMode { get; set; } = OmniGramCaptionMode.User;
        public string? UserCaption { get; set; }
        public string? AICaptionPrompt { get; set; }
        public string? MediaPath { get; set; }
        public DateTime? ScheduledForUtc { get; set; }
    }

    public class OmniGramAddAccountRequest
    {
        public string username { get; set; } = "";
        public string password { get; set; } = "";
        public bool useMemeScraperSource { get; set; }
        public string? memeScraperSourceAccountId { get; set; }
        public bool? autonomousPostingEnabled { get; set; }
        public int? autonomousPostingIntervalMinutes { get; set; }
        public string? autonomousCaptionPrompt { get; set; }
    }

    public class OmniGramServiceEvent
    {
        public string EventId { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
        public string Level { get; set; } = "Info";
        public string Message { get; set; } = "";
        public string? AccountId { get; set; }
        public string? PostId { get; set; }
        public string? MetadataJson { get; set; }
    }

    public class OmniGramUploadMetric
    {
        public string MetricId { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
        public string AccountId { get; set; } = "";
        public string? Username { get; set; }
        public string PostId { get; set; } = "";
        public OmniGramPostStatus Status { get; set; }
        public DateTime ScheduledForUtc { get; set; }
        public DateTime? PostedAtUtc { get; set; }
        public int RetryCount { get; set; }
        public string? ProviderPostId { get; set; }
        public string? SelectedMemeReelPostId { get; set; }
        public int CaptionLength { get; set; }
        public string? FailureReason { get; set; }
    }

    public record OmniGramPublishResult(bool Success, string ProviderPostId, string Error);
}
