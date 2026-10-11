namespace Omnipotent.Services.OmniGram.Api
{
    /// <summary>The token pair Business Login hands back for an authorization code (short-lived, ~1 hour).</summary>
    public sealed class IgShortLivedToken
    {
        public string AccessToken { get; set; } = "";
        public string? UserId { get; set; }
        public List<string> Permissions { get; set; } = new();
    }

    /// <summary>A long-lived (60-day) Instagram user token, from the exchange or a refresh.</summary>
    public sealed class IgLongLivedToken
    {
        public string AccessToken { get; set; } = "";
        public string? TokenType { get; set; }
        public long ExpiresInSeconds { get; set; }
    }

    public sealed class IgProfile
    {
        /// <summary>The app-scoped id ("id").</summary>
        public string? Id { get; set; }
        /// <summary>The Instagram professional account id used for publishing ("user_id").</summary>
        public string? UserId { get; set; }
        public string Username { get; set; } = "";
        public string? Name { get; set; }
        public string? AccountType { get; set; }
        public string? ProfilePictureUrl { get; set; }
        public long? FollowersCount { get; set; }
        public long? FollowsCount { get; set; }
        public long? MediaCount { get; set; }
        public string? Biography { get; set; }
        public string? Website { get; set; }

        /// <summary>The id to publish and read insights as.</summary>
        public string? PublishingId => !string.IsNullOrEmpty(UserId) ? UserId : Id;
    }

    /// <summary>Everything one POST /{ig-user-id}/media can carry. Null/empty values are not sent.</summary>
    public sealed class IgContainerRequest
    {
        /// <summary>REELS, STORIES, CAROUSEL, VIDEO (carousel video item) — or null for a feed image.</summary>
        public string? MediaType { get; set; }
        public string? ImageUrl { get; set; }
        public string? VideoUrl { get; set; }
        public string? Caption { get; set; }
        public bool? IsCarouselItem { get; set; }
        public List<string> Children { get; set; } = new();
        public bool? ShareToFeed { get; set; }
        public long? ThumbOffsetMs { get; set; }
        public string? CoverUrl { get; set; }
        public string? AudioName { get; set; }
        public string? AltText { get; set; }
        public List<string> Collaborators { get; set; } = new();
        public string? LocationId { get; set; }
        /// <summary>MANUAL or SS_PERFORMANCE (trial reels).</summary>
        public string? TrialGraduationStrategy { get; set; }
        public bool? IsAiGenerated { get; set; }
    }

    public sealed class IgContainerStatus
    {
        public string Id { get; set; } = "";
        /// <summary>EXPIRED, ERROR, FINISHED, IN_PROGRESS or PUBLISHED.</summary>
        public string StatusCode { get; set; } = "";
        /// <summary>Free-text detail; for ERROR it says why (sometimes with a 22070xx subcode).</summary>
        public string? Status { get; set; }
    }

    public sealed class IgPublishingLimit
    {
        public int QuotaUsage { get; set; }
        public int? QuotaTotal { get; set; }
        public int? QuotaDurationSeconds { get; set; }
    }

    public sealed class IgMediaItem
    {
        public string Id { get; set; } = "";
        public string? Caption { get; set; }
        /// <summary>IMAGE, VIDEO or CAROUSEL_ALBUM.</summary>
        public string? MediaType { get; set; }
        /// <summary>FEED, REELS, STORY or AD.</summary>
        public string? MediaProductType { get; set; }
        public string? MediaUrl { get; set; }
        public string? ThumbnailUrl { get; set; }
        public string? Permalink { get; set; }
        public string? Shortcode { get; set; }
        public DateTime TimestampUtc { get; set; }
        public long? LikeCount { get; set; }
        public long? CommentsCount { get; set; }
        public bool? IsSharedToFeed { get; set; }
    }

    public sealed class IgMediaPage
    {
        public List<IgMediaItem> Items { get; set; } = new();
        /// <summary>Cursor for the next (older) page, or null at the end.</summary>
        public string? After { get; set; }
    }

    /// <summary>Insight values by metric name; a metric Instagram had no data for is absent.</summary>
    public sealed class IgInsightValues
    {
        public Dictionary<string, double> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>metric → breakdown dimension value(s) joined with "|" → value.</summary>
        public Dictionary<string, Dictionary<string, double>> Breakdowns { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public long? Long(string metric) => Values.TryGetValue(metric, out var v) ? (long)Math.Round(v) : null;
        public double? Double(string metric) => Values.TryGetValue(metric, out var v) ? v : null;
    }

    public sealed class IgSeriesPoint
    {
        public DateTime EndTimeUtc { get; set; }
        public double Value { get; set; }
    }

    public sealed class IgComment
    {
        public string Id { get; set; } = "";
        public string? Text { get; set; }
        public string? Username { get; set; }
        public DateTime TimestampUtc { get; set; }
        public long? LikeCount { get; set; }
        public bool Hidden { get; set; }
        public string? ParentId { get; set; }
        public List<IgComment> Replies { get; set; } = new();
    }

    /// <summary>Meta's own report of how much of the app's call allowance is used (X-App-Usage / X-Business-Use-Case-Usage).</summary>
    public sealed class IgUsageHeaders
    {
        public DateTime ObservedUtc { get; set; }
        /// <summary>Highest of call_count / total_time / total_cputime, in percent.</summary>
        public double? AppUsagePercent { get; set; }
        public double? AccountUsagePercent { get; set; }
        /// <summary>Minutes until access is regained once throttled (0 when not throttled).</summary>
        public int? RegainAccessMinutes { get; set; }

        public double MaxPercent => Math.Max(AppUsagePercent ?? 0, AccountUsagePercent ?? 0);
    }
}
