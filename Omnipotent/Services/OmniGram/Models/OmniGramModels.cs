using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.OmniGram.Models
{
    // ─────────────────────────────── Enums ───────────────────────────────

    /// <summary>Whether OmniGram can act as an account right now.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum ConnectionState
    {
        /// <summary>Imported from v1 (or added by name) and never authorized: no token yet.</summary>
        AwaitingConnection,
        Healthy,
        /// <summary>The token expired, was revoked, or lacks a permission. Reconnect.</summary>
        NeedsReauth,
        /// <summary>Instagram restricted the account (checkpoint, spam flag). Fix it in the Instagram app.</summary>
        Restricted,
        Error,
    }

    /// <summary>
    /// A post's lifecycle. Planned → Ready (→ AwaitingApproval) → Publishing → Published is the happy path;
    /// Failed/Cancelled/Skipped/Removed are terminal. Only the publisher moves a post into or out of
    /// Publishing, and it claims the post under the store lock first, so nothing is sent twice concurrently.
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum PostStatus { Draft, Planned, Ready, AwaitingApproval, Publishing, Published, Failed, Cancelled, Skipped, Removed }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum PostOrigin { Autopilot, Manual, Imported }

    /// <summary>What the post becomes on Instagram.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum PostFormat { Reel, Photo, Carousel, Story }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum ContentSourceKind { None, MemeScraper, Folder, Library }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum ContentPick { Best, Newest, Random, Oldest }

    /// <summary>How a caption is written. Manual is a post's own hand-written caption (never a strategy mode).</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum CaptionMode { None, Fixed, Rotate, AI, Original, Manual }

    /// <summary>What a post does when AI caption generation keeps failing.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum CaptionFallback { Empty, Fixed, Hold }

    /// <summary>Where a post's hashtags go: at the end of the caption, in a first comment, or nowhere.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum HashtagPlacement { Caption, FirstComment, None }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum MissedSlotPolicy { PublishLate, Skip }

    /// <summary>
    /// Trial reels are shown to non-followers first. Manual graduation leaves the decision in the
    /// Instagram app; Auto lets Instagram graduate a reel that performs well.
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum TrialReelMode { Off, Manual, Auto }

    /// <summary>How an image that is outside Instagram's 4:5–1.91:1 feed range is fitted.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum ImageFit { BlurPad, Pad, Crop }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum EventLevel { Info, Success, Warning, Error }

    /// <summary>Where a post is in the two-step Instagram publish (create container → publish it).</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum PublishStage
    {
        /// <summary>Nothing sent to Instagram yet.</summary>
        None,
        /// <summary>A media container exists; Instagram is fetching/processing the media.</summary>
        Processing,
        /// <summary>media_publish was sent and its outcome is not yet known (timeout, crash): check before resending.</summary>
        PublishRequested,
        Published,
    }

    public static class OmniGramIds
    {
        public static string New() => Guid.NewGuid().ToString("N")[..12];
    }

    // ─────────────────────────────── App ───────────────────────────────

    /// <summary>
    /// The Meta app's Instagram product (Business Login for Instagram). The app id and secret are the
    /// "Instagram app ID/secret" under Instagram → API setup with Instagram business login → Business login
    /// settings, not the Meta app's own id. The redirect URL must be listed there exactly. The app is
    /// only needed for the OAuth connect flow; accounts can also be connected by pasting a token
    /// generated in the App Dashboard.
    /// </summary>
    public sealed class OmniGramAppConfig
    {
        public const string DefaultRedirectUrl = "https://klive.dev/omnigram/oauth/callback";

        public string? AppId { get; set; }
        public string? AppSecretCipher { get; set; }
        public string RedirectUrl { get; set; } = DefaultRedirectUrl;
        public DateTime? VerifiedUtc { get; set; }
        public string? LastVerifyError { get; set; }
        public DateTime? UpdatedUtc { get; set; }

        [JsonIgnore] public bool IsConfigured => !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrEmpty(AppSecretCipher);
    }

    // ─────────────────────────────── Accounts + strategy ───────────────────────────────

    /// <summary>An Instagram professional account OmniGram manages: its token, strategy, stats and health.</summary>
    public sealed class OmniGramAccount
    {
        public string AccountId { get; set; } = OmniGramIds.New();
        /// <summary>The Instagram user id the token acts as (Instagram Login's user_id). Null until connected.</summary>
        public string? IgUserId { get; set; }
        public string Username { get; set; } = "";
        public string? Name { get; set; }
        /// <summary>BUSINESS or MEDIA_CREATOR.</summary>
        public string? AccountType { get; set; }
        public string? ProfilePictureUrl { get; set; }
        public string? Biography { get; set; }
        public string? Website { get; set; }

        // ── Connection ──
        /// <summary>Long-lived Instagram user access token. Encrypted.</summary>
        public string? TokenCipher { get; set; }
        public DateTime? TokenIssuedUtc { get; set; }
        public DateTime? TokenExpiresUtc { get; set; }
        public DateTime? TokenRefreshedUtc { get; set; }
        public List<string> Scopes { get; set; } = new();
        /// <summary>"oauth" (Business Login redirect) or "token" (pasted from the App Dashboard).</summary>
        public string? ConnectedVia { get; set; }
        public DateTime? ConnectedUtc { get; set; }
        public ConnectionState Connection { get; set; } = ConnectionState.AwaitingConnection;
        public string? ConnectionDetail { get; set; }
        public DateTime? ConnectionChangedUtc { get; set; }

        // ── Operation ──
        /// <summary>When on, the planner fills every upcoming schedule slot with content automatically.</summary>
        public bool Autopilot { get; set; }
        /// <summary>Stops all publishing for this account (manual posts included) until resumed.</summary>
        public bool Paused { get; set; }
        /// <summary>Planned posts wait in AwaitingApproval until approved on the website.</summary>
        public bool RequireApproval { get; set; }
        public string? Notes { get; set; }
        public List<string> Tags { get; set; } = new();
        public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

        public AccountStats Stats { get; set; } = new();
        public AccountHealth Health { get; set; } = new();
        public PublishingQuota Quota { get; set; } = new();
        public OmniGramStrategy Strategy { get; set; } = OmniGramStrategy.CreateDefault();

        /// <summary>Content keys this account has used or reserved; never picked again for it.</summary>
        public HashSet<string> UsedContentKeys { get; set; } = new(StringComparer.Ordinal);
        /// <summary>Content Instagram rejected (or that could not be made compliant); never picked again by any account.</summary>
        public HashSet<string> RejectedContentKeys { get; set; } = new(StringComparer.Ordinal);
        /// <summary>Position in the caption pool for Rotate mode.</summary>
        public int CaptionPoolCursor { get; set; }

        /// <summary>Set for accounts carried over from OmniGram v1 (which logged in with passwords).</summary>
        public LegacyAccountInfo? Legacy { get; set; }

        [JsonIgnore] public bool IsConnected => !string.IsNullOrEmpty(TokenCipher) && !string.IsNullOrEmpty(IgUserId);
    }

    public sealed class LegacyAccountInfo
    {
        public string? V1AccountId { get; set; }
        public DateTime ImportedUtc { get; set; }
        public string? V1LoginStatus { get; set; }
        public int V1PostedCount { get; set; }
    }

    public sealed class AccountStats
    {
        public long? Followers { get; set; }
        public long? Follows { get; set; }
        public long? MediaCount { get; set; }
        public DateTime? ProfileSyncedUtc { get; set; }
        public DateTime? MediaIndexSyncedUtc { get; set; }
        public DateTime? PostInsightsSyncedUtc { get; set; }
        public DateTime? AccountInsightsSyncedUtc { get; set; }
        public DateTime? DemographicsSyncedUtc { get; set; }
        public DateTime? CommentsSyncedUtc { get; set; }
        public DateTime? LastPublishedUtc { get; set; }
    }

    public sealed class AccountHealth
    {
        public string? LastError { get; set; }
        public DateTime? LastErrorUtc { get; set; }
        public int ConsecutiveFailures { get; set; }
        public DateTime? LastSuccessUtc { get; set; }
        /// <summary>Set when the planner could not find content for an upcoming slot.</summary>
        public string? ContentWarning { get; set; }
        public DateTime? ContentWarningUtc { get; set; }
        /// <summary>After a spam flag (2207051) publishing backs off until this time.</summary>
        public DateTime? CooldownUntilUtc { get; set; }
        public string? CooldownReason { get; set; }
    }

    /// <summary>Instagram's own rolling 24-hour API publishing allowance (content_publishing_limit).</summary>
    public sealed class PublishingQuota
    {
        public int? QuotaTotal { get; set; }
        public int? QuotaUsage { get; set; }
        public int QuotaDurationSeconds { get; set; } = 86400;
        public DateTime? FetchedUtc { get; set; }
    }

    public sealed class WeeklySlot
    {
        /// <summary>Always a number on the wire (Sunday = 0): the website indexes day names by it, and the routes'
        /// global StringEnumConverter would otherwise send "Friday".</summary>
        [JsonConverter(typeof(DayOfWeekNumberConverter))]
        public DayOfWeek Day { get; set; }
        /// <summary>Minutes after local midnight, 0–1439, in the strategy's time zone.</summary>
        public int Minute { get; set; }

        public WeeklySlot() { }
        public WeeklySlot(DayOfWeek day, int minute) { Day = day; Minute = minute; }
    }

    public sealed class MemeScraperContentFilter
    {
        /// <summary>Only reels from sources tagged with one of these MemeScraper niches (empty = any).</summary>
        public List<string> Niches { get; set; } = new();
        /// <summary>Only reels from these Instagram accounts (empty = any).</summary>
        public List<string> Sources { get; set; } = new();
        /// <summary>Ignore reels older than this many days (0 = no limit).</summary>
        public int MaxAgeDays { get; set; } = 120;
        public long MinViews { get; set; }
        /// <summary>Skip videos longer than this (0 = no limit). Reels allow up to 15 minutes.</summary>
        public int MaxDurationSeconds { get; set; } = 90;
        public ContentPick Pick { get; set; } = ContentPick.Best;
        /// <summary>When off (default) a reel used by one managed account is never used by another.</summary>
        public bool AllowReuseAcrossAccounts { get; set; }
    }

    public sealed class FolderContentSettings
    {
        public string Path { get; set; } = "";
        public bool IncludeSubfolders { get; set; }
        public bool Images { get; set; } = true;
        public bool Videos { get; set; } = true;
        public ContentPick Pick { get; set; } = ContentPick.Random;
    }

    public sealed class AiCaptionSettings
    {
        public string Persona { get; set; } = "A deadpan, terminally online meme page. Dry, short and funny; never cringe, never salesy.";
        /// <summary>Extra guidance appended to the prompt.</summary>
        public string Instructions { get; set; } = "";
        /// <summary>Captions in the voice wanted; the model imitates their style, never copies them.</summary>
        public List<string> Examples { get; set; } = new();
        public int MaxLength { get; set; } = 150;
        /// <summary>Send a few frames of the video to a vision-capable model.</summary>
        public bool UseVision { get; set; } = true;
        /// <summary>Give the model the original post's caption as context.</summary>
        public bool UseSourceCaption { get; set; } = true;
        /// <summary>Let the model suggest hashtags specific to what is shown.</summary>
        public bool SuggestHashtags { get; set; } = true;
        public int MaxSuggestedHashtags { get; set; } = 4;
        public bool AllowEmoji { get; set; } = true;
        public string Language { get; set; } = "English";
        public CaptionFallback Fallback { get; set; } = CaptionFallback.Empty;
        /// <summary>Optional model id override for the active LLM provider.</summary>
        public string? Model { get; set; }
    }

    /// <summary>Reel-only publishing options.</summary>
    public sealed class ReelSettings
    {
        /// <summary>Also show the reel in the profile grid / feed (Instagram's share_to_feed).</summary>
        public bool ShareToFeed { get; set; } = true;
        /// <summary>Cover frame, seconds into the video (null = Instagram's default, the first frame).</summary>
        public double? CoverFrameSeconds { get; set; }
        public TrialReelMode Trial { get; set; } = TrialReelMode.Off;
        /// <summary>Up to three usernames invited as collaborators (they must accept in the app).</summary>
        public List<string> Collaborators { get; set; } = new();
        /// <summary>After a reel is published, also post the same video as a story (videos up to 60 s).</summary>
        public bool AlsoPostStory { get; set; }
    }

    /// <summary>Everything that decides what an account posts, when, and how it is captioned and tagged.</summary>
    public sealed class OmniGramStrategy
    {
        // ── Schedule ──
        /// <summary>IANA or Windows time zone id the weekly slots are expressed in.</summary>
        public string TimeZone { get; set; } = "Europe/London";
        public List<WeeklySlot> Slots { get; set; } = new();
        /// <summary>Each post goes out up to ± this many minutes from its slot (stable per slot).</summary>
        public int JitterMinutes { get; set; } = 9;
        /// <summary>How far ahead the planner prepares posts (content picked, media prepared, caption written).</summary>
        public int PlanAheadDays { get; set; } = 7;
        public int MaxPostsPerDay { get; set; } = 3;
        /// <summary>Minimum spacing between two posts on this account.</summary>
        public int MinGapMinutes { get; set; } = 90;
        public MissedSlotPolicy MissedSlots { get; set; } = MissedSlotPolicy.PublishLate;
        /// <summary>A missed slot is still published if no more than this many hours late.</summary>
        public int MissedSlotGraceHours { get; set; } = 6;

        // ── Content ──
        public ContentSourceKind Source { get; set; } = ContentSourceKind.MemeScraper;
        public MemeScraperContentFilter MemeScraper { get; set; } = new();
        public FolderContentSettings Folder { get; set; } = new();
        /// <summary>What an image from the content source becomes: a feed photo or a story.</summary>
        public PostFormat ImageFormat { get; set; } = PostFormat.Photo;
        /// <summary>What a video from the content source becomes: a reel or a story.</summary>
        public PostFormat VideoFormat { get; set; } = PostFormat.Reel;
        public ImageFit ImageFit { get; set; } = ImageFit.BlurPad;
        public ReelSettings Reels { get; set; } = new();

        // ── Captions ──
        public CaptionMode CaptionMode { get; set; } = CaptionMode.AI;
        public string FixedCaption { get; set; } = "";
        public List<string> CaptionPool { get; set; } = new();
        public AiCaptionSettings Ai { get; set; } = new();
        /// <summary>Appended to every caption, e.g. "Follow @page for more". Empty = none.</summary>
        public string CaptionFooter { get; set; } = "";
        /// <summary>
        /// Credit line for content that came from another account, with {origin} replaced by its username,
        /// e.g. "🎥 @{origin}". Empty = no credit.
        /// </summary>
        public string CreditTemplate { get; set; } = "";

        // ── Hashtags ──
        public List<string> FixedHashtags { get; set; } = new();
        public List<string> RotatingHashtags { get; set; } = new();
        public int RotatingHashtagsPerPost { get; set; } = 3;
        /// <summary>Instagram allows 30 per post; a handful of specific ones works best.</summary>
        public int MaxHashtags { get; set; } = 8;
        public HashtagPlacement HashtagPlacement { get; set; } = HashtagPlacement.Caption;

        // ── Disclosure ──
        /// <summary>Applies Instagram's "AI info" label (is_ai_generated). For media made with AI, not for AI captions.</summary>
        public bool LabelAsAiGenerated { get; set; }

        /// <summary>The out-of-the-box strategy: one meme reel every Friday evening with an AI caption.</summary>
        public static OmniGramStrategy CreateDefault() => new()
        {
            Slots = new List<WeeklySlot> { new(DayOfWeek.Friday, 18 * 60) },
            FixedHashtags = new List<string> { "memes", "funny" },
        };
    }

    // ─────────────────────────────── Posts ───────────────────────────────

    public sealed class OmniGramPost
    {
        public string PostId { get; set; } = OmniGramIds.New();
        public string AccountId { get; set; } = "";
        public PostStatus Status { get; set; } = PostStatus.Planned;
        public PostOrigin Origin { get; set; } = PostOrigin.Manual;
        public PostFormat Format { get; set; } = PostFormat.Reel;
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>The schedule slot an autopilot post fills (before jitter). Null for manual posts.</summary>
        public DateTime? SlotUtc { get; set; }
        public DateTime ScheduledUtc { get; set; }
        public bool Approved { get; set; }
        /// <summary>Set by "publish now"/"retry": skips autopilot's daily cap, spacing and missed-slot rules.</summary>
        public bool ManualOverride { get; set; }

        public List<PostMedia> Media { get; set; } = new();
        /// <summary>The caption text (without hashtags, footer or credit, which are added at publish time).</summary>
        public string? Caption { get; set; }
        public List<string> Hashtags { get; set; } = new();
        public HashtagPlacement HashtagPlacement { get; set; } = HashtagPlacement.Caption;
        /// <summary>Extra first comment written by hand (hashtags in FirstComment placement are added to it).</summary>
        public string? FirstComment { get; set; }
        /// <summary>Alt text for photos and carousel images (Instagram ignores it on reels and stories).</summary>
        public string? AltText { get; set; }
        public ReelSettings Reel { get; set; } = new();
        public bool LabelAsAiGenerated { get; set; }
        /// <summary>The account's footer and credit line, resolved when the post was planned.</summary>
        public string? Footer { get; set; }
        public string? Credit { get; set; }

        /// <summary>Set while a caption still has to be generated; the post is not published until cleared.</summary>
        public bool CaptionPending { get; set; }
        public CaptionInfo CaptionInfo { get; set; } = new();
        public ContentRef? Content { get; set; }
        /// <summary>A story created automatically from this reel, or the reel this story was made from.</summary>
        public string? CompanionPostId { get; set; }
        /// <summary>Instagram refused a copy that was only repackaged: the next preparation re-encodes fully.</summary>
        public bool ForceTranscode { get; set; }

        // ── Publishing ──
        public int Attempts { get; set; }
        /// <summary>Times publishing was postponed without counting as a failure (quota, spacing, processing).</summary>
        public int Deferrals { get; set; }
        public DateTime? NextAttemptUtc { get; set; }
        public string? LastError { get; set; }
        public string? LastErrorCode { get; set; }
        /// <summary>Why a ready post is not going out right now (paused, reconnect needed, quota…).</summary>
        public string? BlockedReason { get; set; }
        public List<PublishAttempt> AttemptLog { get; set; } = new();
        public PublishProgress Publish { get; set; } = new();
        public string? IgMediaId { get; set; }
        public string? Permalink { get; set; }
        public string? Shortcode { get; set; }
        /// <summary>What Instagram reports the post as: REELS, FEED or STORY.</summary>
        public string? MediaProductType { get; set; }
        public DateTime? PublishedUtc { get; set; }
        public string? FirstCommentId { get; set; }
        public string? FirstCommentError { get; set; }
        /// <summary>The first comment still has to be posted (it failed transiently after publishing).</summary>
        public bool FirstCommentPending { get; set; }
        public int FirstCommentAttempts { get; set; }

        public PostMetrics Metrics { get; set; } = new();

        [JsonIgnore]
        public bool IsTerminal => Status is PostStatus.Published or PostStatus.Failed or PostStatus.Cancelled or PostStatus.Skipped or PostStatus.Removed;

        /// <summary>Not yet published and still going to be: occupies its slot.</summary>
        [JsonIgnore]
        public bool IsPending => Status is PostStatus.Draft or PostStatus.Planned or PostStatus.Ready or PostStatus.AwaitingApproval or PostStatus.Publishing;

        [JsonIgnore] public bool IsVideo => Media.Count > 0 && Media[0].IsVideo;
    }

    /// <summary>The Instagram side of a publish in progress. Persisted so a restart resumes instead of re-sending.</summary>
    public sealed class PublishProgress
    {
        public PublishStage Stage { get; set; } = PublishStage.None;
        public string? ContainerId { get; set; }
        /// <summary>Carousel item containers, in order.</summary>
        public List<string> ChildContainerIds { get; set; } = new();
        public DateTime? ContainerCreatedUtc { get; set; }
        /// <summary>Last status_code read: IN_PROGRESS, FINISHED, ERROR, EXPIRED or PUBLISHED.</summary>
        public string? ContainerStatus { get; set; }
        public string? ContainerStatusDetail { get; set; }
        public DateTime? NextCheckUtc { get; set; }
        public int StatusChecks { get; set; }
        public DateTime? PublishRequestedUtc { get; set; }
        /// <summary>Times Instagram (or anything else) fetched this post's media from the public URL.</summary>
        public int MediaFetches { get; set; }
        public DateTime? LastMediaFetchUtc { get; set; }
        public string? LastMediaFetchAgent { get; set; }
        /// <summary>Retried once without thumb_offset after Instagram rejected the cover frame.</summary>
        public bool DroppedCoverFrame { get; set; }
    }

    public sealed class PostMedia
    {
        /// <summary>Absolute path of the original file on this machine.</summary>
        public string Path { get; set; } = "";
        public string MimeType { get; set; } = "application/octet-stream";
        public long Bytes { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public double? DurationSeconds { get; set; }
        /// <summary>A small JPEG preview (an early frame for videos).</summary>
        public string? ThumbnailPath { get; set; }
        /// <summary>When a preview was last attempted (so a failing one is not retried every tick).</summary>
        public DateTime? ThumbnailAttemptUtc { get; set; }

        // ── The Instagram-compliant copy served to Instagram ──
        public string? PreparedPath { get; set; }
        public string? PreparedMimeType { get; set; }
        public long? PreparedBytes { get; set; }
        public DateTime? PreparedUtc { get; set; }
        /// <summary>The format the prepared copy was made for (a story video is trimmed differently from a reel).</summary>
        public PostFormat? PreparedFor { get; set; }
        /// <summary>What preparation did: "remuxed (faststart)", "transcoded to H.264/AAC", "converted to JPEG, padded to 4:5"…</summary>
        public string? PrepareNotes { get; set; }
        public string? PrepareError { get; set; }
        public DateTime? PrepareAttemptUtc { get; set; }

        [JsonIgnore] public bool IsVideo => MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Where a post's content came from.</summary>
    public sealed class ContentRef
    {
        public ContentSourceKind Kind { get; set; }
        /// <summary>Namespaced stable key: "ig:{postId}", "file:{name}:{size}", "lib:{name}".</summary>
        public string Key { get; set; } = "";
        /// <summary>Instagram account or folder the content came from.</summary>
        public string? Origin { get; set; }
        public string? OriginalCaption { get; set; }
        public string? OriginalUrl { get; set; }
        public long? Views { get; set; }
        public long? Likes { get; set; }
        public DateTime? CreatedUtc { get; set; }
    }

    public sealed class CaptionInfo
    {
        public CaptionMode Mode { get; set; }
        public string? Model { get; set; }
        public bool UsedVision { get; set; }
        public DateTime? GeneratedUtc { get; set; }
        /// <summary>True once someone edited the caption by hand; regeneration then needs an explicit request.</summary>
        public bool Edited { get; set; }
        public string? Error { get; set; }
        public int Failures { get; set; }
        /// <summary>After a failed generation, when to try again.</summary>
        public DateTime? RetryAfterUtc { get; set; }
        /// <summary>Extra direction for the AI on this post (from the composer or a regenerate request).</summary>
        public string? Direction { get; set; }
        public List<string> SuggestedHashtags { get; set; } = new();
    }

    public sealed class PublishAttempt
    {
        public DateTime Utc { get; set; }
        public bool Ok { get; set; }
        public string? Step { get; set; }
        public string? Code { get; set; }
        public string? Message { get; set; }
        public long DurationMs { get; set; }
    }

    /// <summary>A post's numbers. Counts come from the media list hourly; the rest from insights at milestones.</summary>
    public sealed class PostMetrics
    {
        public long? Views { get; set; }
        public long? Reach { get; set; }
        public long? Likes { get; set; }
        public long? Comments { get; set; }
        public long? Shares { get; set; }
        public long? Saves { get; set; }
        public long? Reposts { get; set; }
        public long? TotalInteractions { get; set; }
        public long? Follows { get; set; }
        public long? ProfileVisits { get; set; }
        public long? Replies { get; set; }
        /// <summary>Reels: average watch time per play, in milliseconds.</summary>
        public long? AvgWatchTimeMs { get; set; }
        public long? TotalWatchTimeMs { get; set; }
        /// <summary>Reels: share of plays skipped in the first 3 seconds, 0–100.</summary>
        public double? SkipRate { get; set; }
        public DateTime? CountsSyncedUtc { get; set; }
        public DateTime? InsightsSyncedUtc { get; set; }
        public string? InsightsError { get; set; }
        /// <summary>Readings at fixed ages after publishing, for comparing posts fairly.</summary>
        public long? ViewsAt24h { get; set; }
        public long? ViewsAt7d { get; set; }
        public long? InteractionsAt24h { get; set; }
        public long? InteractionsAt7d { get; set; }
        public List<MetricPoint> History { get; set; } = new();

        /// <summary>Likes + comments + shares + saves when Instagram's own total is missing.</summary>
        [JsonIgnore]
        public long Interactions => TotalInteractions ?? ((Likes ?? 0) + (Comments ?? 0) + (Shares ?? 0) + (Saves ?? 0));
    }

    public sealed class MetricPoint
    {
        public DateTime Utc { get; set; }
        public long? Views { get; set; }
        public long? Reach { get; set; }
        public long? Likes { get; set; }
        public long? Comments { get; set; }
        public long? Interactions { get; set; }
    }

    // ─────────────────────────────── Analytics data ───────────────────────────────

    public sealed class AccountSnapshot
    {
        public DateTime Utc { get; set; }
        public long? Followers { get; set; }
        public long? Follows { get; set; }
        public long? MediaCount { get; set; }
    }

    /// <summary>A post on the account as Instagram lists it (ours or made in the app), for account-wide analytics.</summary>
    public sealed class IndexedMedia
    {
        public string Id { get; set; } = "";
        public DateTime PublishedUtc { get; set; }
        /// <summary>IMAGE, VIDEO or CAROUSEL_ALBUM.</summary>
        public string? MediaType { get; set; }
        /// <summary>FEED, REELS or STORY.</summary>
        public string? ProductType { get; set; }
        public string? Permalink { get; set; }
        public string? Shortcode { get; set; }
        public string? Caption { get; set; }
        public string? ThumbnailUrl { get; set; }
        public long? Likes { get; set; }
        public long? Comments { get; set; }
        public long? Views { get; set; }
        public long? Reach { get; set; }
        public long? Shares { get; set; }
        public long? Saves { get; set; }
        public long? TotalInteractions { get; set; }
        public DateTime? InsightsSyncedUtc { get; set; }
        /// <summary>Set when the media was published by OmniGram.</summary>
        public string? OmniPostId { get; set; }
        public DateTime? LastSeenUtc { get; set; }
    }

    /// <summary>One day of account insights (Instagram's day, in Pacific time, keyed by its yyyy-MM-dd).</summary>
    public sealed class AccountDay
    {
        public string Day { get; set; } = "";
        public long? Reach { get; set; }
        public long? Views { get; set; }
        public long? AccountsEngaged { get; set; }
        public long? TotalInteractions { get; set; }
        public long? Likes { get; set; }
        public long? Comments { get; set; }
        public long? Shares { get; set; }
        public long? Saves { get; set; }
        public long? Replies { get; set; }
        public long? Reposts { get; set; }
        public long? Follows { get; set; }
        public long? Unfollows { get; set; }
        public long? ProfileLinksTaps { get; set; }
        public DateTime? SyncedUtc { get; set; }
    }

    public sealed class DemographicsSnapshot
    {
        public DateTime Utc { get; set; }
        /// <summary>breakdown (age, gender, country, city) → value → count. Instagram returns the top 45.</summary>
        public Dictionary<string, Dictionary<string, long>> Followers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Error { get; set; }
    }

    public sealed class CommentItem
    {
        public string Id { get; set; } = "";
        public string MediaId { get; set; } = "";
        public string? OmniPostId { get; set; }
        public string? ParentId { get; set; }
        public string? Username { get; set; }
        public string? Text { get; set; }
        public DateTime Utc { get; set; }
        public long? LikeCount { get; set; }
        public bool Hidden { get; set; }
        /// <summary>Written by the account itself (a reply or the first comment).</summary>
        public bool Own { get; set; }
        public bool Replied { get; set; }
    }

    /// <summary>Everything OmniGram tracks about an account besides its config and posts.</summary>
    public sealed class AccountInsights
    {
        public List<AccountSnapshot> Snapshots { get; set; } = new();
        public List<IndexedMedia> Index { get; set; } = new();
        /// <summary>yyyy-MM-dd → that day's account insights.</summary>
        public SortedDictionary<string, AccountDay> Days { get; set; } = new(StringComparer.Ordinal);
        /// <summary>Average followers online per hour (0–23, Instagram's reporting zone), from online_followers.</summary>
        public Dictionary<int, double> OnlineFollowersByHour { get; set; } = new();
        public DateTime? OnlineFollowersSyncedUtc { get; set; }
        public DemographicsSnapshot? Demographics { get; set; }
        public List<CommentItem> Comments { get; set; } = new();
        /// <summary>Comment ids already seen, so "new comment" counts stay right across syncs.</summary>
        public List<string> SeenCommentIds { get; set; } = new();
    }

    public sealed class OmniGramEvent
    {
        public DateTime Utc { get; set; } = DateTime.UtcNow;
        public EventLevel Level { get; set; } = EventLevel.Info;
        public string Kind { get; set; } = "";
        public string? AccountId { get; set; }
        public string? PostId { get; set; }
        public string Message { get; set; } = "";
    }

    /// <summary>Result of the last check that the public media URL is reachable from the internet.</summary>
    public sealed class PublicUrlCheck
    {
        public DateTime Utc { get; set; }
        public bool Ok { get; set; }
        public string BaseUrl { get; set; } = "";
        public int? Status { get; set; }
        public long? LatencyMs { get; set; }
        public string? Detail { get; set; }
    }

    /// <summary>Persisted engine bookkeeping: when each periodic job is next due, API usage, learned limits.</summary>
    public sealed class OmniGramEngineState
    {
        public Dictionary<string, DateTime> NextDueUtc { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> ApiCallsByHour { get; set; } = new(StringComparer.Ordinal);
        /// <summary>Media product type → insight metrics Instagram refused for it (learned at runtime).</summary>
        public Dictionary<string, List<string>> UnsupportedMetrics { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public PublicUrlCheck? PublicUrlCheck { get; set; }
        /// <summary>Last time Instagram fetched any media from the public URL — proof the URL works from outside.</summary>
        public DateTime? LastExternalMediaFetchUtc { get; set; }
        public string? LastExternalMediaFetchAgent { get; set; }
        public DateTime? MigratedV1Utc { get; set; }
        public string? MigrationSummary { get; set; }
        /// <summary>v1 left Instagram passwords in plain text under SavedData/OmniGram/Accounts; cleared once removed.</summary>
        public bool V1PasswordFilesPresent { get; set; }
    }

    /// <summary>A pending Business Login authorization (persisted so a restart mid-flow does not lose it).</summary>
    public sealed class PendingAuthFlow
    {
        public string FlowId { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        /// <summary>pending, completed or failed.</summary>
        public string State { get; set; } = "pending";
        public string? Error { get; set; }
        public string? AccountId { get; set; }
        /// <summary>When reconnecting a specific account.</summary>
        public string? ReconnectAccountId { get; set; }
        public string? Preset { get; set; }
        public bool Autopilot { get; set; }
        public DateTime? CompletedUtc { get; set; }
    }
}
