using Omnipotent.Services.OmniTumblr.Api;
using Omnipotent.Services.OmniTumblr.Models;

namespace Omnipotent.Services.OmniTumblr.Engine
{
    // ─────────────────────────────── DTOs served to the website ───────────────────────────────

    public sealed class PostSummaryDto
    {
        public string PostId { get; set; } = "";
        public string BlogId { get; set; } = "";
        public string? BlogName { get; set; }
        public string Status { get; set; } = "";
        public string Origin { get; set; } = "";
        public string Kind { get; set; } = "";
        public DateTime ScheduledUtc { get; set; }
        public DateTime? SlotUtc { get; set; }
        public DateTime? PublishedUtc { get; set; }
        public DateTime? NextAttemptUtc { get; set; }
        public string? Caption { get; set; }
        public string? Title { get; set; }
        public string? LinkUrl { get; set; }
        public List<string> Tags { get; set; } = new();
        public bool CaptionPending { get; set; }
        public string CaptionMode { get; set; } = "";
        public string? CaptionError { get; set; }
        public bool CaptionEdited { get; set; }
        public bool UsedVision { get; set; }
        public string? CaptionModel { get; set; }
        public bool Approved { get; set; }
        public bool HasThumbnail { get; set; }
        public int MediaCount { get; set; }
        public bool IsVideo { get; set; }
        public double? DurationSeconds { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public long? Bytes { get; set; }
        public string? BlockedReason { get; set; }
        public string? LastError { get; set; }
        public string? LastErrorCode { get; set; }
        public int Attempts { get; set; }
        public string? TumblrUrl { get; set; }
        public string? TumblrPostId { get; set; }
        public long Notes { get; set; }
        public long? Likes { get; set; }
        public long? Reblogs { get; set; }
        public long? Replies { get; set; }
        public long? NotesAt24h { get; set; }
        public long? NotesAt7d { get; set; }
        public DateTime? MetricsSyncedUtc { get; set; }
        public string? ContentSource { get; set; }
        public string? ContentOrigin { get; set; }
        public string? ContentUrl { get; set; }
        public string? OriginalCaption { get; set; }
        public long? ContentViews { get; set; }
        public string TumblrState { get; set; } = "";
    }

    public sealed class SeriesPointDto
    {
        public DateTime T { get; set; }
        public double V { get; set; }
    }

    public sealed class BlogSummaryDto
    {
        public string BlogId { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Title { get; set; }
        public string? Url { get; set; }
        public string AvatarUrl { get; set; } = "";
        public bool Autopilot { get; set; }
        public bool Paused { get; set; }
        public bool RequireApproval { get; set; }
        public string ConnectionId { get; set; } = "";
        public string? ConnectionUser { get; set; }
        public string ConnectionHealth { get; set; } = "";
        public long? Followers { get; set; }
        public long? FollowersDelta7d { get; set; }
        public List<double> FollowersSpark { get; set; } = new();
        public long? Posts { get; set; }
        public DateTime? LastPublishedUtc { get; set; }
        public PostSummaryDto? NextPost { get; set; }
        public PostSummaryDto? LastPost { get; set; }
        public int Pending { get; set; }
        public int AwaitingApproval { get; set; }
        public int Published7d { get; set; }
        public int Failed7d { get; set; }
        public long Notes30d { get; set; }
        public double? AvgNotes30d { get; set; }
        public int SlotsPerWeek { get; set; }
        public string Source { get; set; } = "";
        public string CaptionMode { get; set; } = "";
        public string TimeZone { get; set; } = "";
        public string? LastError { get; set; }
        public DateTime? LastErrorUtc { get; set; }
        public int ConsecutiveFailures { get; set; }
        public string? ContentWarning { get; set; }
        /// <summary>ok | attention | error | paused | idle</summary>
        public string State { get; set; } = "ok";
        public string StateReason { get; set; } = "";
    }

    public sealed class AttentionItemDto
    {
        /// <summary>error | warning | info</summary>
        public string Level { get; set; } = "warning";
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        public string? BlogId { get; set; }
        public string? ConnectionId { get; set; }
        /// <summary>A hint the UI turns into a button: settings, reconnect, strategy, queue.</summary>
        public string? Action { get; set; }
    }

    public sealed class HeatCellDto
    {
        public int Dow { get; set; }
        public int Hour { get; set; }
        public int Posts { get; set; }
        public double AvgNotes { get; set; }
    }

    public sealed class RankedDto
    {
        public string Key { get; set; } = "";
        public int Posts { get; set; }
        public double AvgNotes { get; set; }
        public long TotalNotes { get; set; }
    }

    public sealed class TopPostDto
    {
        public string Id { get; set; } = "";
        public string? BlogId { get; set; }
        public string? BlogName { get; set; }
        public string? OmniPostId { get; set; }
        public string? Url { get; set; }
        public string? Summary { get; set; }
        public string? Type { get; set; }
        public long Notes { get; set; }
        public DateTime PublishedUtc { get; set; }
        public bool Ours { get; set; }
        public bool HasThumbnail { get; set; }
    }

    public sealed class DailyActivityDto
    {
        public string Day { get; set; } = "";
        public int Likes { get; set; }
        public int Reblogs { get; set; }
        public int Replies { get; set; }
        public int Follows { get; set; }
        public int Other { get; set; }
    }

    public sealed class DailyPostingDto
    {
        public string Day { get; set; } = "";
        public int Ours { get; set; }
        public int Others { get; set; }
        public long Notes { get; set; }
    }

    public sealed class AnalyticsDto
    {
        public string? BlogId { get; set; }
        public int Days { get; set; }
        public DateTime FromUtc { get; set; }
        public DateTime ToUtc { get; set; }
        public string TimeZone { get; set; } = "UTC";
        public long? Followers { get; set; }
        public long? FollowersDelta { get; set; }
        public double? FollowersGrowthPerWeek { get; set; }
        public int PostsPublished { get; set; }
        public int PostsPublishedPrev { get; set; }
        public long Notes { get; set; }
        public long NotesPrev { get; set; }
        public double? AvgNotes { get; set; }
        public double? MedianNotes { get; set; }
        public double? EngagementRate { get; set; }
        public long? Likes { get; set; }
        public long? Reblogs { get; set; }
        public long? Replies { get; set; }
        public List<SeriesPointDto> FollowersSeries { get; set; } = new();
        public List<DailyPostingDto> Posting { get; set; } = new();
        public List<DailyActivityDto> Activity { get; set; } = new();
        public List<HeatCellDto> Heatmap { get; set; } = new();
        public string? BestSlotLabel { get; set; }
        public List<TopPostDto> TopPosts { get; set; } = new();
        public List<RankedDto> Tags { get; set; } = new();
        public List<RankedDto> Sources { get; set; } = new();
        public List<RankedDto> CaptionModes { get; set; } = new();
        public List<RankedDto> Kinds { get; set; } = new();
        public int SampleSize { get; set; }
    }

    public sealed class FleetKpisDto
    {
        public long Followers { get; set; }
        public long? FollowersDelta7d { get; set; }
        public long? FollowersDelta30d { get; set; }
        public List<SeriesPointDto> FollowersSeries { get; set; } = new();
        public int Published7d { get; set; }
        public int PublishedPrev7d { get; set; }
        public long Notes30d { get; set; }
        public double? AvgNotes30d { get; set; }
        public int Pending { get; set; }
        public int AwaitingApproval { get; set; }
        public int Failed7d { get; set; }
        public double? SuccessRate30d { get; set; }
        public DateTime? NextPostUtc { get; set; }
        public string? NextPostBlog { get; set; }
        public int AutopilotBlogs { get; set; }
        public int Blogs { get; set; }
    }

    /// <summary>
    /// Read models for the website, built from the live state (callers hold the store lock). Everything
    /// is computed from in-memory data, so routes stay O(state) with no I/O.
    /// </summary>
    internal static class OmniTumblrViews
    {
        public static PostSummaryDto Summary(OmniTumblrPost p, OmniTumblrBlog? blog)
        {
            var first = p.Media.FirstOrDefault();
            return new PostSummaryDto
            {
                PostId = p.PostId,
                BlogId = p.BlogId,
                BlogName = blog?.Name,
                Status = p.Status.ToString(),
                Origin = p.Origin.ToString(),
                Kind = p.Kind.ToString(),
                ScheduledUtc = p.ScheduledUtc,
                SlotUtc = p.SlotUtc,
                PublishedUtc = p.PublishedUtc,
                NextAttemptUtc = p.NextAttemptUtc,
                Caption = p.Caption,
                Title = p.Title,
                LinkUrl = p.LinkUrl,
                Tags = p.Tags.ToList(),
                CaptionPending = p.CaptionPending,
                CaptionMode = p.CaptionInfo.Mode.ToString(),
                CaptionError = p.CaptionInfo.Error,
                CaptionEdited = p.CaptionInfo.Edited,
                UsedVision = p.CaptionInfo.UsedVision,
                CaptionModel = p.CaptionInfo.Model,
                Approved = p.Approved,
                HasThumbnail = p.Media.Any(m => m.ThumbnailPath != null),
                MediaCount = p.Media.Count,
                IsVideo = first?.IsVideo ?? false,
                DurationSeconds = first?.DurationSeconds,
                Width = first?.Width,
                Height = first?.Height,
                Bytes = first?.Bytes,
                BlockedReason = p.BlockedReason,
                LastError = p.LastError,
                LastErrorCode = p.LastErrorCode,
                Attempts = p.Attempts,
                TumblrUrl = p.TumblrUrl,
                TumblrPostId = p.TumblrPostId,
                Notes = p.Metrics.Notes,
                Likes = p.Metrics.Likes,
                Reblogs = p.Metrics.Reblogs,
                Replies = p.Metrics.Replies,
                NotesAt24h = p.Metrics.NotesAt24h,
                NotesAt7d = p.Metrics.NotesAt7d,
                MetricsSyncedUtc = p.Metrics.SyncedUtc,
                ContentSource = p.Content?.Kind.ToString(),
                ContentOrigin = p.Content?.Origin,
                ContentUrl = p.Content?.OriginalUrl,
                OriginalCaption = p.Content?.OriginalCaption,
                ContentViews = p.Content?.Views,
                TumblrState = p.TumblrState.ToString(),
            };
        }

        public static long? FollowerDelta(BlogInsights insights, DateTime now, TimeSpan span, long? current)
        {
            if (current == null || insights.Snapshots.Count == 0) return null;
            var baseline = insights.Snapshots.LastOrDefault(s => s.Utc <= now - span && s.Followers.HasValue)
                ?? insights.Snapshots.FirstOrDefault(s => s.Followers.HasValue && s.Utc <= now - TimeSpan.FromHours(span.TotalHours / 2));
            return baseline?.Followers is long b ? current.Value - b : null;
        }

        public static List<double> FollowerSpark(BlogInsights insights, DateTime now, int days = 14)
        {
            var from = now.AddDays(-days);
            var daily = insights.Snapshots.Where(s => s.Utc >= from && s.Followers.HasValue)
                .GroupBy(s => s.Utc.Date)
                .OrderBy(g => g.Key)
                .Select(g => (double)g.OrderBy(s => s.Utc).Last().Followers!.Value)
                .ToList();
            return daily;
        }

        public static BlogSummaryDto BlogSummary(OmniTumblrState s, OmniTumblrBlog b, DateTime now)
        {
            var posts = s.PostsOf(b.BlogId);
            var conn = s.Connection(b.ConnectionId);
            var insights = s.InsightsOf(b.BlogId);
            var pending = posts.Where(p => p.IsPending).OrderBy(p => p.ScheduledUtc).ToList();
            var published30 = posts.Where(p => p.Status == PostStatus.Published && p.PublishedUtc > now.AddDays(-30)).ToList();
            var last = posts.Where(p => p.Status == PostStatus.Published).OrderByDescending(p => p.PublishedUtc).FirstOrDefault();
            var dto = new BlogSummaryDto
            {
                BlogId = b.BlogId,
                Name = b.Name,
                Title = b.Title,
                Url = b.Url ?? $"https://{b.Name}.tumblr.com/",
                AvatarUrl = TumblrApiClient.BuildAvatarUrl(b.Name, 128),
                Autopilot = b.Autopilot,
                Paused = b.Paused,
                RequireApproval = b.RequireApproval,
                ConnectionId = b.ConnectionId,
                ConnectionUser = conn?.UserName,
                ConnectionHealth = (conn?.Health ?? ConnectionHealth.Unknown).ToString(),
                Followers = b.Stats.Followers,
                FollowersDelta7d = FollowerDelta(insights, now, TimeSpan.FromDays(7), b.Stats.Followers),
                FollowersSpark = FollowerSpark(insights, now),
                Posts = b.Stats.Posts,
                LastPublishedUtc = b.Stats.LastPublishedUtc,
                NextPost = pending.FirstOrDefault() is OmniTumblrPost next ? Summary(next, b) : null,
                LastPost = last == null ? null : Summary(last, b),
                Pending = pending.Count,
                AwaitingApproval = pending.Count(p => p.Status == PostStatus.AwaitingApproval),
                Published7d = posts.Count(p => p.Status == PostStatus.Published && p.PublishedUtc > now.AddDays(-7)),
                Failed7d = posts.Count(p => p.Status == PostStatus.Failed && p.UpdatedUtc > now.AddDays(-7)),
                Notes30d = published30.Sum(p => p.Metrics.Notes),
                AvgNotes30d = published30.Count > 0 ? Math.Round(published30.Average(p => (double)p.Metrics.Notes), 1) : null,
                SlotsPerWeek = OmniTumblrScheduleMath.SlotsPerWeek(b.Strategy),
                Source = b.Strategy.Source.ToString(),
                CaptionMode = b.Strategy.CaptionMode.ToString(),
                TimeZone = b.Strategy.TimeZone,
                LastError = b.Health.LastError,
                LastErrorUtc = b.Health.LastErrorUtc,
                ConsecutiveFailures = b.Health.ConsecutiveFailures,
                ContentWarning = b.Health.ContentWarning,
            };
            (dto.State, dto.StateReason) = BlogState(s, b, conn, pending, now);
            return dto;
        }

        private static (string, string) BlogState(OmniTumblrState s, OmniTumblrBlog b, OmniTumblrConnection? conn, List<OmniTumblrPost> pending, DateTime now)
        {
            if (conn == null) return ("error", "No Tumblr connection");
            if (conn.Health == ConnectionHealth.NeedsReauth) return ("error", "Reconnect @" + conn.UserName);
            if (b.Paused) return ("paused", "Paused");
            if (b.Health.ConsecutiveFailures >= 2) return ("error", "Publishing is failing: " + (b.Health.LastError ?? "unknown error"));
            if (b.Health.ContentWarning != null && b.Autopilot) return ("attention", b.Health.ContentWarning);
            var blocked = pending.FirstOrDefault(p => p.ScheduledUtc <= now && p.BlockedReason != null);
            if (blocked != null) return ("attention", blocked.BlockedReason!);
            if (pending.Any(p => p.Status == PostStatus.AwaitingApproval)) return ("attention", "Posts are waiting for approval");
            if (b.Autopilot && b.Strategy.Slots.Count == 0) return ("attention", "Autopilot is on but the schedule has no slots");
            if (!b.Autopilot && pending.Count == 0) return ("idle", "Autopilot is off and nothing is scheduled");
            return ("ok", b.Autopilot ? "On autopilot" : "Manual posting");
        }

        public static FleetKpisDto FleetKpis(OmniTumblrState s, DateTime now)
        {
            var all = s.AllPosts().ToList();
            var published30 = all.Where(p => p.Status == PostStatus.Published && p.PublishedUtc > now.AddDays(-30)).ToList();
            int failed30 = all.Count(p => p.Status == PostStatus.Failed && p.UpdatedUtc > now.AddDays(-30));
            var next = all.Where(p => p.IsPending).OrderBy(p => p.ScheduledUtc).FirstOrDefault();
            var kpis = new FleetKpisDto
            {
                Blogs = s.Blogs.Count,
                AutopilotBlogs = s.Blogs.Values.Count(b => b.Autopilot && !b.Paused),
                Followers = s.Blogs.Values.Sum(b => b.Stats.Followers ?? 0),
                Published7d = all.Count(p => p.Status == PostStatus.Published && p.PublishedUtc > now.AddDays(-7)),
                PublishedPrev7d = all.Count(p => p.Status == PostStatus.Published && p.PublishedUtc <= now.AddDays(-7) && p.PublishedUtc > now.AddDays(-14)),
                Notes30d = published30.Sum(p => p.Metrics.Notes),
                AvgNotes30d = published30.Count > 0 ? Math.Round(published30.Average(p => (double)p.Metrics.Notes), 1) : null,
                Pending = all.Count(p => p.IsPending),
                AwaitingApproval = all.Count(p => p.Status == PostStatus.AwaitingApproval),
                Failed7d = all.Count(p => p.Status == PostStatus.Failed && p.UpdatedUtc > now.AddDays(-7)),
                SuccessRate30d = published30.Count + failed30 > 0 ? Math.Round(100.0 * published30.Count / (published30.Count + failed30), 1) : null,
                NextPostUtc = next?.ScheduledUtc,
                NextPostBlog = next == null ? null : s.Blog(next.BlogId)?.Name,
            };

            long? d7 = null, d30 = null;
            foreach (var blog in s.Blogs.Values)
            {
                var insights = s.InsightsOf(blog.BlogId);
                if (FollowerDelta(insights, now, TimeSpan.FromDays(7), blog.Stats.Followers) is long a) d7 = (d7 ?? 0) + a;
                if (FollowerDelta(insights, now, TimeSpan.FromDays(30), blog.Stats.Followers) is long c) d30 = (d30 ?? 0) + c;
            }
            kpis.FollowersDelta7d = d7;
            kpis.FollowersDelta30d = d30;
            kpis.FollowersSeries = FleetFollowerSeries(s, now, 30);
            return kpis;
        }

        /// <summary>Daily total followers across blogs, carrying each blog's last known value forward.</summary>
        public static List<SeriesPointDto> FleetFollowerSeries(OmniTumblrState s, DateTime now, int days)
        {
            var result = new List<SeriesPointDto>();
            var start = now.Date.AddDays(-days + 1);
            var perBlog = s.Blogs.Values.Select(b => s.InsightsOf(b.BlogId).Snapshots.Where(x => x.Followers.HasValue).OrderBy(x => x.Utc).ToList()).ToList();
            for (var day = start; day <= now.Date; day = day.AddDays(1))
            {
                var end = day.AddDays(1);
                long total = 0;
                bool any = false;
                foreach (var snaps in perBlog)
                {
                    var lastKnown = snaps.LastOrDefault(x => x.Utc < end);
                    if (lastKnown?.Followers is long f) { total += f; any = true; }
                }
                if (any) result.Add(new SeriesPointDto { T = day, V = total });
            }
            return result;
        }

        public static List<AttentionItemDto> Attention(OmniTumblrState s, DateTime now, bool publishingEnabled, bool aiAvailable, bool memeScraperReady)
        {
            var items = new List<AttentionItemDto>();
            if (!s.App.IsConfigured)
                items.Add(new AttentionItemDto { Level = "error", Title = "Connect your Tumblr app", Detail = "Enter the OAuth consumer key and secret from tumblr.com/oauth/apps so OmniTumblr can authorize blogs.", Action = "settings" });
            else if (s.App.LastVerifyError != null)
                items.Add(new AttentionItemDto { Level = "warning", Title = "Tumblr app not verified", Detail = s.App.LastVerifyError, Action = "settings" });
            if (!publishingEnabled)
                items.Add(new AttentionItemDto { Level = "warning", Title = "Publishing is switched off", Detail = "The OmniSetting OmniTumblrV2_PublishingEnabled is false; posts are prepared but not published.", Action = "settings" });
            foreach (var conn in s.Connections.Values.Where(c => c.Health == ConnectionHealth.NeedsReauth))
                items.Add(new AttentionItemDto { Level = "error", Title = $"Reconnect @{conn.UserName}", Detail = conn.HealthDetail ?? "Tumblr no longer accepts this account's authorization.", ConnectionId = conn.ConnectionId, Action = "reconnect" });
            if (s.App.IsConfigured && s.Blogs.Count == 0)
                items.Add(new AttentionItemDto { Level = "info", Title = "Add a blog", Detail = "Connect a Tumblr account and pick the blogs OmniTumblr should manage.", Action = "add-blog" });

            bool needsAi = s.Blogs.Values.Any(b => b.Autopilot && b.Strategy.CaptionMode == CaptionMode.AI);
            if (needsAi && !aiAvailable)
                items.Add(new AttentionItemDto { Level = "warning", Title = "AI captions unavailable", Detail = "KliveLLM is not running; AI-captioned posts will wait or fall back to their configured fallback caption." });
            bool needsReels = s.Blogs.Values.Any(b => b.Autopilot && b.Strategy.Source == ContentSourceKind.MemeScraper);
            if (needsReels && !memeScraperReady)
                items.Add(new AttentionItemDto { Level = "warning", Title = "MemeScraper is not ready", Detail = "Blogs that post MemeScraper reels cannot plan new posts until MemeScraper has loaded." });

            foreach (var blog in s.Blogs.Values.OrderBy(b => b.Name))
            {
                if (blog.Health.ConsecutiveFailures >= 2)
                    items.Add(new AttentionItemDto { Level = "error", Title = $"@{blog.Name} is failing to publish", Detail = blog.Health.LastError ?? "", BlogId = blog.BlogId, Action = "queue" });
                if (blog.Autopilot && blog.Health.ContentWarning != null)
                    items.Add(new AttentionItemDto { Level = "warning", Title = $"@{blog.Name} is out of content", Detail = blog.Health.ContentWarning, BlogId = blog.BlogId, Action = "strategy" });
                int awaiting = s.PostsOf(blog.BlogId).Count(p => p.Status == PostStatus.AwaitingApproval);
                if (awaiting > 0)
                    items.Add(new AttentionItemDto { Level = "info", Title = $"{awaiting} post(s) on @{blog.Name} need approval", Detail = "Approve them in the queue or turn off approval in the blog's strategy.", BlogId = blog.BlogId, Action = "queue" });
                if (blog.Autopilot && !blog.Paused && blog.Strategy.Slots.Count == 0)
                    items.Add(new AttentionItemDto { Level = "warning", Title = $"@{blog.Name} has no schedule", Detail = "Autopilot is on but the weekly schedule has no time slots.", BlogId = blog.BlogId, Action = "strategy" });
            }
            return items;
        }

        // ─────────────────────────────── Analytics ───────────────────────────────

        public static AnalyticsDto Analytics(OmniTumblrState s, string? blogId, int days, DateTime now)
        {
            days = Math.Clamp(days, 1, 730);
            var blogs = blogId == null ? s.Blogs.Values.ToList() : s.Blog(blogId) is OmniTumblrBlog only ? new List<OmniTumblrBlog> { only } : new();
            var tz = blogId != null && blogs.Count == 1 ? OmniTumblrScheduleMath.ResolveTimeZone(blogs[0].Strategy.TimeZone) : TimeZoneInfo.Utc;
            var from = now.AddDays(-days);
            var prevFrom = from.AddDays(-days);
            var dto = new AnalyticsDto
            {
                BlogId = blogId,
                Days = days,
                FromUtc = from,
                ToUtc = now,
                TimeZone = tz.Id,
            };

            var ourPosts = blogs.SelectMany(b => s.PostsOf(b.BlogId).Select(p => (Blog: b, Post: p))).ToList();
            var published = ourPosts.Where(x => x.Post.Status == PostStatus.Published && x.Post.PublishedUtc.HasValue).ToList();
            var inRange = published.Where(x => x.Post.PublishedUtc >= from).ToList();
            var inPrev = published.Where(x => x.Post.PublishedUtc >= prevFrom && x.Post.PublishedUtc < from).ToList();
            dto.PostsPublished = inRange.Count;
            dto.PostsPublishedPrev = inPrev.Count;
            dto.Notes = inRange.Sum(x => x.Post.Metrics.Notes);
            dto.NotesPrev = inPrev.Sum(x => x.Post.Metrics.Notes);
            if (inRange.Count > 0)
            {
                var notes = inRange.Select(x => (double)x.Post.Metrics.Notes).OrderBy(v => v).ToList();
                dto.AvgNotes = Math.Round(notes.Average(), 1);
                dto.MedianNotes = notes.Count % 2 == 1 ? notes[notes.Count / 2] : (notes[notes.Count / 2 - 1] + notes[notes.Count / 2]) / 2;
                var withBreakdown = inRange.Where(x => x.Post.Metrics.Likes.HasValue).ToList();
                if (withBreakdown.Count > 0)
                {
                    dto.Likes = withBreakdown.Sum(x => x.Post.Metrics.Likes ?? 0);
                    dto.Reblogs = withBreakdown.Sum(x => x.Post.Metrics.Reblogs ?? 0);
                    dto.Replies = withBreakdown.Sum(x => x.Post.Metrics.Replies ?? 0);
                }
            }

            // Followers
            long? followers = blogs.Any(b => b.Stats.Followers.HasValue) ? blogs.Sum(b => b.Stats.Followers ?? 0) : null;
            dto.Followers = followers;
            if (blogId == null)
            {
                dto.FollowersSeries = FleetFollowerSeries(s, now, days);
            }
            else if (blogs.Count == 1)
            {
                dto.FollowersSeries = s.InsightsOf(blogs[0].BlogId).Snapshots
                    .Where(x => x.Utc >= from && x.Followers.HasValue)
                    .Select(x => new SeriesPointDto { T = x.Utc, V = x.Followers!.Value })
                    .ToList();
            }
            long? delta = null;
            foreach (var b in blogs)
                if (FollowerDelta(s.InsightsOf(b.BlogId), now, TimeSpan.FromDays(days), b.Stats.Followers) is long d) delta = (delta ?? 0) + d;
            dto.FollowersDelta = delta;
            if (delta.HasValue && days >= 7) dto.FollowersGrowthPerWeek = Math.Round(delta.Value / (days / 7.0), 1);
            if (followers is > 0 && dto.AvgNotes.HasValue) dto.EngagementRate = Math.Round(dto.AvgNotes.Value / followers.Value * 100, 3);

            // Posting per day (ours vs posts made outside OmniTumblr) with notes.
            var index = blogs.SelectMany(b => s.InsightsOf(b.BlogId).Index.Select(p => (Blog: b, Post: p))).ToList();
            var posting = new SortedDictionary<string, DailyPostingDto>(StringComparer.Ordinal);
            DailyPostingDto Day(DateTime utc)
            {
                string key = OmniTumblrScheduleMath.UtcToLocal(utc, tz).ToString("yyyy-MM-dd");
                if (!posting.TryGetValue(key, out var row)) posting[key] = row = new DailyPostingDto { Day = key };
                return row;
            }
            for (var d = OmniTumblrScheduleMath.UtcToLocal(from, tz).Date; d <= OmniTumblrScheduleMath.UtcToLocal(now, tz).Date; d = d.AddDays(1))
                posting[d.ToString("yyyy-MM-dd")] = new DailyPostingDto { Day = d.ToString("yyyy-MM-dd") };
            foreach (var (_, p) in index.Where(x => x.Post.PublishedUtc >= from))
            {
                var row = Day(p.PublishedUtc);
                if (p.OmniPostId != null) row.Ours++; else row.Others++;
                row.Notes += p.Notes;
            }
            dto.Posting = posting.Values.ToList();

            // Activity (likes/reblogs/replies/follows per day, from the activity feed).
            var activity = new SortedDictionary<string, DailyActivityDto>(StringComparer.Ordinal);
            for (var d = from.Date; d <= now.Date; d = d.AddDays(1))
                activity[d.ToString("yyyy-MM-dd")] = new DailyActivityDto { Day = d.ToString("yyyy-MM-dd") };
            foreach (var b in blogs)
            {
                foreach (var (day, counts) in s.InsightsOf(b.BlogId).Activity.Daily)
                {
                    if (!activity.TryGetValue(day, out var row)) continue;
                    foreach (var (type, count) in counts)
                    {
                        switch (type)
                        {
                            case "like": row.Likes += count; break;
                            case "reblog": row.Reblogs += count; break;
                            case "reply": row.Replies += count; break;
                            case "follow": row.Follows += count; break;
                            default: row.Other += count; break;
                        }
                    }
                }
            }
            dto.Activity = activity.Values.ToList();

            // Best times: every indexed post 1–120 days old, by local weekday/hour of publishing.
            var sample = index.Where(x => x.Post.PublishedUtc <= now.AddDays(-1) && x.Post.PublishedUtc >= now.AddDays(-120)).Select(x => x.Post).ToList();
            dto.SampleSize = sample.Count;
            dto.Heatmap = sample
                .GroupBy(p =>
                {
                    var local = OmniTumblrScheduleMath.UtcToLocal(p.PublishedUtc, tz);
                    return ((int)local.DayOfWeek, local.Hour);
                })
                .Select(g => new HeatCellDto { Dow = g.Key.Item1, Hour = g.Key.Hour, Posts = g.Count(), AvgNotes = Math.Round(g.Average(p => (double)p.Notes), 1) })
                .OrderBy(c => c.Dow).ThenBy(c => c.Hour)
                .ToList();
            var best = dto.Heatmap.Where(c => c.Posts >= 2).OrderByDescending(c => c.AvgNotes).FirstOrDefault();
            if (best != null)
                dto.BestSlotLabel = $"{(DayOfWeek)best.Dow} {best.Hour:00}:00–{(best.Hour + 1) % 24:00}:00 ({best.AvgNotes:0.#} notes avg over {best.Posts} posts)";

            // Top posts in range (all posts on the blog, ours flagged).
            var ourById = published.Where(x => x.Post.TumblrPostId != null).GroupBy(x => x.Post.TumblrPostId!).ToDictionary(g => g.Key, g => g.First().Post, StringComparer.Ordinal);
            dto.TopPosts = index.Where(x => x.Post.PublishedUtc >= from)
                .OrderByDescending(x => x.Post.Notes)
                .Take(12)
                .Select(x => new TopPostDto
                {
                    Id = x.Post.Id,
                    BlogId = x.Blog.BlogId,
                    BlogName = x.Blog.Name,
                    OmniPostId = x.Post.OmniPostId,
                    Url = x.Post.Url,
                    Summary = x.Post.Summary,
                    Type = x.Post.Type,
                    Notes = x.Post.Notes,
                    PublishedUtc = x.Post.PublishedUtc,
                    Ours = x.Post.OmniPostId != null,
                    HasThumbnail = x.Post.OmniPostId != null && ourById.TryGetValue(x.Post.Id, out var ours) && ours.Media.Any(m => m.ThumbnailPath != null),
                })
                .ToList();

            // What works: tags, content sources, caption modes, post kinds (our posts, 2+ uses).
            var scored = published.Where(x => x.Post.PublishedUtc >= now.AddDays(-Math.Max(days, 90))).Select(x => x.Post).ToList();
            dto.Tags = Rank(scored.SelectMany(p => p.Tags.Select(t => (Key: t.ToLowerInvariant(), Notes: p.Metrics.Notes))), 2, 15);
            dto.Sources = Rank(scored.Where(p => p.Content?.Origin != null).Select(p => (Key: p.Content!.Origin!, Notes: p.Metrics.Notes)), 1, 12);
            dto.CaptionModes = Rank(scored.Select(p => (Key: p.CaptionInfo.Mode.ToString() + (p.CaptionInfo.UsedVision ? " (vision)" : ""), Notes: p.Metrics.Notes)), 1, 8);
            dto.Kinds = Rank(scored.Select(p => (Key: p.Kind.ToString(), Notes: p.Metrics.Notes)), 1, 6);
            return dto;
        }

        private static List<RankedDto> Rank(IEnumerable<(string Key, long Notes)> items, int minPosts, int take) =>
            items.GroupBy(i => i.Key, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= minPosts)
                .Select(g => new RankedDto { Key = g.Key, Posts = g.Count(), TotalNotes = g.Sum(i => i.Notes), AvgNotes = Math.Round(g.Average(i => (double)i.Notes), 1) })
                .OrderByDescending(r => r.AvgNotes)
                .ThenByDescending(r => r.Posts)
                .Take(take)
                .ToList();
    }
}
