using Omnipotent.Services.OmniTumblr.Models;

namespace Omnipotent.Services.OmniTumblr.Api
{
    /// <summary>The credentials one API call is made with. Secrets are plaintext here and never persisted.</summary>
    public sealed class TumblrCredentials
    {
        public string ConsumerKey { get; init; } = "";
        public string ConsumerSecret { get; init; } = "";
        public TumblrAuthMode Mode { get; init; } = TumblrAuthMode.OAuth1;
        /// <summary>OAuth1 token, or the OAuth2 access token. Null for app-only (api_key) calls.</summary>
        public string? Token { get; init; }
        /// <summary>OAuth1 token secret.</summary>
        public string? TokenSecret { get; init; }

        public bool HasUserToken => !string.IsNullOrEmpty(Token);
    }

    public sealed class TumblrUserInfo
    {
        public string Name { get; set; } = "";
        public long? Following { get; set; }
        public long? Likes { get; set; }
        public List<TumblrBlogRef> Blogs { get; set; } = new();
    }

    public sealed class TumblrBlogInfo
    {
        public string Name { get; set; } = "";
        public string? Uuid { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? Url { get; set; }
        public long? Posts { get; set; }
        public long? Likes { get; set; }
        public DateTime? UpdatedUtc { get; set; }
    }

    public sealed class TumblrPostSummary
    {
        public string Id { get; set; } = "";
        public string? Slug { get; set; }
        public string? BlogName { get; set; }
        public DateTime PublishedUtc { get; set; }
        public string? Type { get; set; }
        public long NoteCount { get; set; }
        public List<string> Tags { get; set; } = new();
        public string? PostUrl { get; set; }
        public string? State { get; set; }
        /// <summary>First text of the post, trimmed, for display.</summary>
        public string? Summary { get; set; }
    }

    public sealed class TumblrPostsPage
    {
        public List<TumblrPostSummary> Posts { get; set; } = new();
        public long? TotalPosts { get; set; }
    }

    public sealed class TumblrNotesSummary
    {
        public long TotalNotes { get; set; }
        public long? TotalLikes { get; set; }
        public long? TotalReblogs { get; set; }
        /// <summary>Notes that are neither likes nor reblogs (mostly replies).</summary>
        public long? Replies => TotalLikes.HasValue && TotalReblogs.HasValue
            ? Math.Max(0, TotalNotes - TotalLikes.Value - TotalReblogs.Value)
            : null;
    }

    public sealed class TumblrNotification
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";
        public DateTime Utc { get; set; }
        public string? FromBlog { get; set; }
        public string? TargetPostId { get; set; }
        public string? Text { get; set; }
    }

    public sealed class TumblrNotificationsPage
    {
        public List<TumblrNotification> Items { get; set; } = new();
        /// <summary>Unix timestamp to pass as "before" for the next (older) page, if any.</summary>
        public long? NextBefore { get; set; }
    }

    public sealed class TumblrCreatedPost
    {
        public string Id { get; set; } = "";
    }

    public sealed class TumblrOAuth2Token
    {
        public string AccessToken { get; set; } = "";
        public string? RefreshToken { get; set; }
        public int ExpiresIn { get; set; }
        public string? Scope { get; set; }
    }

    /// <summary>What the API reported about our call budget in its rate-limit headers.</summary>
    public sealed class TumblrRateLimitHeaders
    {
        public long? PerHourLimit { get; set; }
        public long? PerHourRemaining { get; set; }
        public long? PerHourResetSeconds { get; set; }
        public long? PerDayLimit { get; set; }
        public long? PerDayRemaining { get; set; }
        public long? PerDayResetSeconds { get; set; }
        public DateTime ObservedUtc { get; set; }
    }

    /// <summary>
    /// The Tumblr API surface OmniTumblr uses. Implemented by <see cref="TumblrApiClient"/>; tests
    /// substitute fakes to drive the publisher/planner through every failure mode.
    /// </summary>
    public interface ITumblrApi
    {
        Task<TumblrUserInfo> GetUserInfoAsync(TumblrCredentials creds, CancellationToken ct);
        Task<Dictionary<string, TumblrLimit>> GetUserLimitsAsync(TumblrCredentials creds, CancellationToken ct);
        Task<TumblrBlogInfo> GetBlogInfoAsync(TumblrCredentials creds, string blog, CancellationToken ct);
        Task<long?> GetFollowerCountAsync(TumblrCredentials creds, string blog, CancellationToken ct);
        Task<TumblrPostsPage> GetPostsAsync(TumblrCredentials creds, string blog, int offset, int limit, CancellationToken ct);
        Task<TumblrNotesSummary> GetNotesSummaryAsync(TumblrCredentials creds, string blog, string postId, CancellationToken ct);
        Task<TumblrNotificationsPage> GetNotificationsAsync(TumblrCredentials creds, string blog, long? before, CancellationToken ct);
        Task<TumblrCreatedPost> CreatePostAsync(TumblrCredentials creds, string blog, NpfPostRequest request, CancellationToken ct);
        Task DeletePostAsync(TumblrCredentials creds, string blog, string postId, CancellationToken ct);

        Task<(string Token, string Secret)> GetOAuth1RequestTokenAsync(string consumerKey, string consumerSecret, string callbackUrl, CancellationToken ct);
        Task<(string Token, string Secret)> GetOAuth1AccessTokenAsync(string consumerKey, string consumerSecret, string requestToken, string requestTokenSecret, string verifier, CancellationToken ct);
        string BuildOAuth1AuthorizeUrl(string requestToken);
        string BuildOAuth2AuthorizeUrl(string consumerKey, string state, string redirectUri);
        Task<TumblrOAuth2Token> ExchangeOAuth2CodeAsync(string consumerKey, string consumerSecret, string code, string redirectUri, CancellationToken ct);
        Task<TumblrOAuth2Token> RefreshOAuth2TokenAsync(string consumerKey, string consumerSecret, string refreshToken, CancellationToken ct);
    }
}
