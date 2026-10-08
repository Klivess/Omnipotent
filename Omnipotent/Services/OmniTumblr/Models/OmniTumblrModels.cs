using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Omnipotent.Services.OmniTumblr.Models
{
    // ─────────────────────────────── Enums ───────────────────────────────

    [JsonConverter(typeof(StringEnumConverter))]
    public enum TumblrAuthMode { OAuth1, OAuth2 }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum ConnectionHealth { Unknown, Healthy, NeedsReauth, Error }

    /// <summary>
    /// A post's lifecycle. Planned → Ready (→ AwaitingApproval) → Publishing → Published is the happy
    /// path; Failed/Cancelled/Skipped are terminal. Only the publisher moves a post into or out of
    /// Publishing, and it claims the post under the store lock first, so a post can never be uploaded
    /// twice concurrently.
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum PostStatus { Draft, Planned, Ready, AwaitingApproval, Publishing, Published, Failed, Cancelled, Skipped, Removed }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum PostOrigin { Autopilot, Manual, Imported }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum PostKind { Video, Photo, Text, Link }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum ContentSourceKind { None, MemeScraper, Folder, Library }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum ContentPick { Best, Newest, Random, Oldest }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum CaptionMode { None, Fixed, Rotate, AI, Original }

    /// <summary>What a post does when AI caption generation fails.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum CaptionFallback { Empty, Fixed, Hold }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum MissedSlotPolicy { PublishLate, Skip }

    /// <summary>The state the post is created in on Tumblr itself.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum TumblrPostState { Published, Draft, Private }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum EventLevel { Info, Success, Warning, Error }

    public static class OmniTumblrIds
    {
        public static string New() => Guid.NewGuid().ToString("N")[..12];
    }

    // ─────────────────────────────── App + connections ───────────────────────────────

    /// <summary>
    /// The Tumblr application (consumer key/secret) every connection authorizes against. Entered once;
    /// the secrets are stored encrypted. The callback URL must match the one registered on the app at
    /// https://www.tumblr.com/oauth/apps exactly ("Default callback URL" for OAuth 1.0a, "OAuth2 redirect
    /// URLs" for OAuth 2.0).
    /// </summary>
    public sealed class OmniTumblrAppConfig
    {
        public const string DefaultCallbackUrl = "https://klive.dev/omnitumblr/oauth/callback";

        public string? ConsumerKeyCipher { get; set; }
        public string? ConsumerSecretCipher { get; set; }
        /// <summary>Last four characters of the consumer key, for display only.</summary>
        public string? ConsumerKeyHint { get; set; }
        public string CallbackUrl { get; set; } = DefaultCallbackUrl;
        public TumblrAuthMode PreferredAuthMode { get; set; } = TumblrAuthMode.OAuth1;
        public DateTime? VerifiedUtc { get; set; }
        public string? LastVerifyError { get; set; }
        public DateTime? UpdatedUtc { get; set; }

        [JsonIgnore] public bool IsConfigured => !string.IsNullOrEmpty(ConsumerKeyCipher) && !string.IsNullOrEmpty(ConsumerSecretCipher);
    }

    /// <summary>One authorized Tumblr user account. Several managed blogs can share a connection.</summary>
    public sealed class OmniTumblrConnection
    {
        public string ConnectionId { get; set; } = OmniTumblrIds.New();
        public string? UserName { get; set; }
        public TumblrAuthMode AuthMode { get; set; }
        /// <summary>OAuth1 token, or the OAuth2 access token. Encrypted.</summary>
        public string? TokenCipher { get; set; }
        /// <summary>OAuth1 token secret. Encrypted.</summary>
        public string? TokenSecretCipher { get; set; }
        /// <summary>OAuth2 refresh token. Encrypted.</summary>
        public string? RefreshTokenCipher { get; set; }
        public DateTime? AccessTokenExpiresUtc { get; set; }
        public string? Scope { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? LastVerifiedUtc { get; set; }
        public ConnectionHealth Health { get; set; } = ConnectionHealth.Unknown;
        public string? HealthDetail { get; set; }
        public DateTime? HealthChangedUtc { get; set; }
        /// <summary>Every blog this user can post to, from /v2/user/info.</summary>
        public List<TumblrBlogRef> Blogs { get; set; } = new();
        /// <summary>From /v2/user/limits: posts, videos, photos… with remaining counts and reset times.</summary>
        public Dictionary<string, TumblrLimit> Limits { get; set; } = new();
        public DateTime? LimitsFetchedUtc { get; set; }
        public DateTime? UserInfoFetchedUtc { get; set; }
    }

    public sealed class TumblrBlogRef
    {
        public string Name { get; set; } = "";
        public string? Uuid { get; set; }
        public string? Title { get; set; }
        public string? Url { get; set; }
        public long? Followers { get; set; }
        public bool Primary { get; set; }
        public bool? Admin { get; set; }
        public string? Type { get; set; }
    }

    public sealed class TumblrLimit
    {
        public string? Description { get; set; }
        public long Limit { get; set; }
        public long Remaining { get; set; }
        public DateTime? ResetUtc { get; set; }
    }

    // ─────────────────────────────── Blogs + strategy ───────────────────────────────

    /// <summary>A blog OmniTumblr manages: its strategy, live stats, health and content bookkeeping.</summary>
    public sealed class OmniTumblrBlog
    {
        public string BlogId { get; set; } = OmniTumblrIds.New();
        /// <summary>Short name (the part before .tumblr.com). Refreshed from Tumblr; the uuid is the stable id.</summary>
        public string Name { get; set; } = "";
        public string? Uuid { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? Url { get; set; }
        public string ConnectionId { get; set; } = "";
        /// <summary>When on, the planner fills every upcoming schedule slot with content automatically.</summary>
        public bool Autopilot { get; set; }
        /// <summary>Stops all publishing for this blog (manual posts included) until resumed.</summary>
        public bool Paused { get; set; }
        /// <summary>Planned posts wait in AwaitingApproval until approved on the website.</summary>
        public bool RequireApproval { get; set; }
        public string? Notes { get; set; }
        public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
        public BlogStats Stats { get; set; } = new();
        public BlogHealth Health { get; set; } = new();
        public OmniTumblrStrategy Strategy { get; set; } = OmniTumblrStrategy.CreateDefault();
        /// <summary>Content keys this blog has used or reserved; never picked again for this blog.</summary>
        public HashSet<string> UsedContentKeys { get; set; } = new(StringComparer.Ordinal);
        /// <summary>Content Tumblr rejected (bad media); never picked again by any blog.</summary>
        public HashSet<string> RejectedContentKeys { get; set; } = new(StringComparer.Ordinal);
        /// <summary>Position in the caption pool for Rotate mode.</summary>
        public int CaptionPoolCursor { get; set; }
    }

    public sealed class BlogStats
    {
        public long? Followers { get; set; }
        public long? Posts { get; set; }
        public long? Likes { get; set; }
        public DateTime? InfoSyncedUtc { get; set; }
        public DateTime? FollowersSyncedUtc { get; set; }
        public DateTime? PostsIndexSyncedUtc { get; set; }
        public DateTime? ActivitySyncedUtc { get; set; }
        public DateTime? LastPublishedUtc { get; set; }
    }

    public sealed class BlogHealth
    {
        public string? LastError { get; set; }
        public DateTime? LastErrorUtc { get; set; }
        public int ConsecutiveFailures { get; set; }
        public DateTime? LastSuccessUtc { get; set; }
        /// <summary>Set when the planner could not find content for an upcoming slot.</summary>
        public string? ContentWarning { get; set; }
        public DateTime? ContentWarningUtc { get; set; }
    }

    public sealed class WeeklySlot
    {
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
        /// <summary>Skip videos longer than this (0 = no limit). Tumblr allows 60 minutes of video per day.</summary>
        public int MaxDurationSeconds { get; set; } = 180;
        public ContentPick Pick { get; set; } = ContentPick.Best;
        /// <summary>When off (default) a reel used by one managed blog is never used by another.</summary>
        public bool AllowReuseAcrossBlogs { get; set; }
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
        public string Persona { get; set; } = "A deadpan, terminally online meme blog. Dry, short and funny; never cringe, never salesy.";
        /// <summary>Extra guidance appended to the prompt.</summary>
        public string Instructions { get; set; } = "";
        /// <summary>Captions in the voice wanted; the model imitates their style, never copies them.</summary>
        public List<string> Examples { get; set; } = new();
        public int MaxLength { get; set; } = 120;
        /// <summary>Send a few frames of the video to a vision-capable model.</summary>
        public bool UseVision { get; set; } = true;
        /// <summary>Give the model the original post's caption as context.</summary>
        public bool UseSourceCaption { get; set; } = true;
        public bool SuggestTags { get; set; } = true;
        public int MaxSuggestedTags { get; set; } = 5;
        public bool AllowEmoji { get; set; } = true;
        public string Language { get; set; } = "English";
        public CaptionFallback Fallback { get; set; } = CaptionFallback.Empty;
        /// <summary>Optional model id override for the active LLM provider.</summary>
        public string? Model { get; set; }
    }

    /// <summary>Everything that decides what a blog posts, when, and how it is captioned and tagged.</summary>
    public sealed class OmniTumblrStrategy
    {
        // ── Schedule ──
        /// <summary>IANA or Windows time zone id the weekly slots are expressed in.</summary>
        public string TimeZone { get; set; } = "Europe/London";
        public List<WeeklySlot> Slots { get; set; } = new();
        /// <summary>Each post goes out up to ± this many minutes from its slot (stable per slot).</summary>
        public int JitterMinutes { get; set; } = 7;
        /// <summary>How far ahead the planner prepares posts (content picked, caption written).</summary>
        public int PlanAheadDays { get; set; } = 7;
        public int MaxPostsPerDay { get; set; } = 8;
        /// <summary>Minimum spacing between two posts on this blog.</summary>
        public int MinGapMinutes { get; set; } = 30;
        public MissedSlotPolicy MissedSlots { get; set; } = MissedSlotPolicy.PublishLate;
        /// <summary>A missed slot is still published if no more than this many hours late.</summary>
        public int MissedSlotGraceHours { get; set; } = 6;

        // ── Content ──
        public ContentSourceKind Source { get; set; } = ContentSourceKind.MemeScraper;
        public MemeScraperContentFilter MemeScraper { get; set; } = new();
        public FolderContentSettings Folder { get; set; } = new();

        // ── Captions ──
        public CaptionMode CaptionMode { get; set; } = CaptionMode.AI;
        public string FixedCaption { get; set; } = "";
        public List<string> CaptionPool { get; set; } = new();
        public AiCaptionSettings Ai { get; set; } = new();

        // ── Tags ──
        public List<string> FixedTags { get; set; } = new();
        public List<string> RotatingTags { get; set; } = new();
        public int RotatingTagsPerPost { get; set; } = 3;
        public int MaxTags { get; set; } = 12;

        // ── Post ──
        public TumblrPostState PostState { get; set; } = TumblrPostState.Published;
        /// <summary>Attach the original content's URL as the post's Tumblr "source".</summary>
        public bool CreditSource { get; set; }

        /// <summary>The out-of-the-box strategy: one meme video every Friday evening with an AI caption.</summary>
        public static OmniTumblrStrategy CreateDefault() => new()
        {
            Slots = new List<WeeklySlot> { new(DayOfWeek.Friday, 18 * 60) },
            FixedTags = new List<string> { "memes", "funny" },
        };
    }

    // ─────────────────────────────── Posts ───────────────────────────────

    public sealed class OmniTumblrPost
    {
        public string PostId { get; set; } = OmniTumblrIds.New();
        public string BlogId { get; set; } = "";
        public PostStatus Status { get; set; } = PostStatus.Planned;
        public PostOrigin Origin { get; set; } = PostOrigin.Manual;
        public PostKind Kind { get; set; } = PostKind.Video;
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>The schedule slot an autopilot post fills (before jitter). Null for manual posts.</summary>
        public DateTime? SlotUtc { get; set; }
        public DateTime ScheduledUtc { get; set; }
        public bool Approved { get; set; }
        /// <summary>Set by "publish now"/"retry": skips autopilot's daily cap, spacing and missed-slot rules.</summary>
        public bool ManualOverride { get; set; }

        public List<PostMedia> Media { get; set; } = new();
        public string? Caption { get; set; }
        public string? Title { get; set; }
        public string? LinkUrl { get; set; }
        public List<string> Tags { get; set; } = new();
        /// <summary>Tumblr "source" attribution URL.</summary>
        public string? SourceUrl { get; set; }
        public TumblrPostState TumblrState { get; set; } = TumblrPostState.Published;

        /// <summary>Set while a caption still has to be generated; the post is not published until cleared.</summary>
        public bool CaptionPending { get; set; }
        public CaptionInfo CaptionInfo { get; set; } = new();
        public ContentRef? Content { get; set; }

        // ── Publishing ──
        public int Attempts { get; set; }
        /// <summary>Times publishing was postponed without counting as a failure (limits, transcoding, gaps).</summary>
        public int Deferrals { get; set; }
        public DateTime? NextAttemptUtc { get; set; }
        public string? LastError { get; set; }
        public string? LastErrorCode { get; set; }
        /// <summary>Why a ready post is not going out right now (paused, reauth needed, daily limit…).</summary>
        public string? BlockedReason { get; set; }
        public List<PublishAttempt> AttemptLog { get; set; } = new();
        /// <summary>Unique permalink slug sent with the post; used to detect a post that was created even
        /// though the response was lost, so a retry never double-posts.</summary>
        public string? Slug { get; set; }
        /// <summary>The last attempt may have created the post (timeout, crash mid-upload): look for it on
        /// Tumblr by slug before uploading again.</summary>
        public bool NeedsReconcile { get; set; }
        public string? TumblrPostId { get; set; }
        public string? TumblrUrl { get; set; }
        public DateTime? PublishedUtc { get; set; }

        public PostMetrics Metrics { get; set; } = new();

        [JsonIgnore]
        public bool IsTerminal => Status is PostStatus.Published or PostStatus.Failed or PostStatus.Cancelled or PostStatus.Skipped or PostStatus.Removed;

        /// <summary>Not yet published and still going to be: occupies its slot.</summary>
        [JsonIgnore]
        public bool IsPending => Status is PostStatus.Draft or PostStatus.Planned or PostStatus.Ready or PostStatus.AwaitingApproval or PostStatus.Publishing;
    }

    public sealed class PostMedia
    {
        /// <summary>Absolute path to the file on this machine.</summary>
        public string Path { get; set; } = "";
        public string MimeType { get; set; } = "application/octet-stream";
        public long Bytes { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public double? DurationSeconds { get; set; }
        /// <summary>A small JPEG preview (first frame for videos).</summary>
        public string? ThumbnailPath { get; set; }
        /// <summary>When a preview was last attempted (so a failing one is not retried every tick).</summary>
        public DateTime? ThumbnailAttemptUtc { get; set; }

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
        public string? AltText { get; set; }
        public List<string> SuggestedTags { get; set; } = new();
    }

    public sealed class PublishAttempt
    {
        public DateTime Utc { get; set; }
        public bool Ok { get; set; }
        public string? Code { get; set; }
        public string? Message { get; set; }
        public long DurationMs { get; set; }
    }

    public sealed class PostMetrics
    {
        public long Notes { get; set; }
        public long? Likes { get; set; }
        public long? Reblogs { get; set; }
        public long? Replies { get; set; }
        public DateTime? SyncedUtc { get; set; }
        public DateTime? BreakdownSyncedUtc { get; set; }
        /// <summary>Notes at fixed ages after publishing, for comparing posts fairly.</summary>
        public long? NotesAt24h { get; set; }
        public long? NotesAt7d { get; set; }
        public List<MetricPoint> History { get; set; } = new();
    }

    public sealed class MetricPoint
    {
        public DateTime Utc { get; set; }
        public long Notes { get; set; }
        public long? Likes { get; set; }
        public long? Reblogs { get; set; }
    }

    // ─────────────────────────────── Analytics data ───────────────────────────────

    public sealed class BlogSnapshot
    {
        public DateTime Utc { get; set; }
        public long? Followers { get; set; }
        public long? Posts { get; set; }
    }

    /// <summary>A post on the blog as Tumblr reports it (ours or made by hand), for blog-wide analytics.</summary>
    public sealed class IndexedPost
    {
        public string Id { get; set; } = "";
        public DateTime PublishedUtc { get; set; }
        public string? Type { get; set; }
        public long Notes { get; set; }
        public List<string> Tags { get; set; } = new();
        public string? Url { get; set; }
        public string? Slug { get; set; }
        public string? Summary { get; set; }
        /// <summary>Set when the post was published by OmniTumblr.</summary>
        public string? OmniPostId { get; set; }
    }

    public sealed class ActivityItem
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";
        public DateTime Utc { get; set; }
        public string? FromBlog { get; set; }
        public string? TargetPostId { get; set; }
        public string? Text { get; set; }
    }

    public sealed class BlogActivity
    {
        public List<ActivityItem> Recent { get; set; } = new();
        /// <summary>yyyy-MM-dd (UTC) → activity type → count.</summary>
        public SortedDictionary<string, Dictionary<string, int>> Daily { get; set; } = new(StringComparer.Ordinal);
        public DateTime? NewestSeenUtc { get; set; }
        public List<string> SeenIds { get; set; } = new();
    }

    /// <summary>Everything OmniTumblr tracks about a blog besides its config and posts.</summary>
    public sealed class BlogInsights
    {
        public List<BlogSnapshot> Snapshots { get; set; } = new();
        public List<IndexedPost> Index { get; set; } = new();
        public BlogActivity Activity { get; set; } = new();
    }

    public sealed class OmniTumblrEvent
    {
        public DateTime Utc { get; set; } = DateTime.UtcNow;
        public EventLevel Level { get; set; } = EventLevel.Info;
        public string Kind { get; set; } = "";
        public string? BlogId { get; set; }
        public string? PostId { get; set; }
        public string Message { get; set; } = "";
    }

    /// <summary>Persisted engine bookkeeping: when each periodic job is next due, API budget usage.</summary>
    public sealed class OmniTumblrEngineState
    {
        public Dictionary<string, DateTime> NextDueUtc { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> ApiCallsByHour { get; set; } = new(StringComparer.Ordinal);
        public DateTime? MigratedV1Utc { get; set; }
        public string? MigrationSummary { get; set; }
    }

    /// <summary>A pending OAuth authorization (persisted so a restart mid-flow does not lose it).</summary>
    public sealed class PendingAuthFlow
    {
        public string FlowId { get; set; } = Guid.NewGuid().ToString("N");
        public TumblrAuthMode Mode { get; set; }
        public string? RequestToken { get; set; }
        public string? RequestTokenSecretCipher { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        /// <summary>pending, completed or failed.</summary>
        public string State { get; set; } = "pending";
        public string? Error { get; set; }
        public string? ConnectionId { get; set; }
        /// <summary>When reconnecting an existing connection.</summary>
        public string? ReconnectConnectionId { get; set; }
        public DateTime? CompletedUtc { get; set; }
    }
}
