using Omnipotent.Services.OmniGram.Api;
using Omnipotent.Services.OmniGram.Models;

namespace Omnipotent.Services.OmniGram.Engine
{
    // ─────────────────────────────── DTOs served to the website ───────────────────────────────

    public sealed class PostSummaryDto
    {
        public string PostId { get; set; } = "";
        public string AccountId { get; set; } = "";
        public string? Username { get; set; }
        public string Status { get; set; } = "";
        public string Origin { get; set; } = "";
        public string Format { get; set; } = "";
        public DateTime ScheduledUtc { get; set; }
        public DateTime? SlotUtc { get; set; }
        public DateTime? PublishedUtc { get; set; }
        public DateTime? NextAttemptUtc { get; set; }
        public string? Caption { get; set; }
        public List<string> Hashtags { get; set; } = new();
        public string HashtagPlacement { get; set; } = "";
        public string? FirstComment { get; set; }
        public string? Credit { get; set; }
        public string? Footer { get; set; }
        /// <summary>The caption exactly as Instagram will receive it.</summary>
        public string ComposedCaption { get; set; } = "";
        public string? AltText { get; set; }
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
        public bool Prepared { get; set; }
        public string? PrepareNotes { get; set; }
        public string? PrepareError { get; set; }
        public string? BlockedReason { get; set; }
        public string? LastError { get; set; }
        public string? LastErrorCode { get; set; }
        public int Attempts { get; set; }
        public string Stage { get; set; } = "";
        public string? ContainerStatus { get; set; }
        public string? Permalink { get; set; }
        public string? IgMediaId { get; set; }
        public string? FirstCommentError { get; set; }
        public bool FirstCommentPending { get; set; }
        public string Trial { get; set; } = "";
        public bool ShareToFeed { get; set; }
        public string? CompanionPostId { get; set; }
        public long? Views { get; set; }
        public long? Reach { get; set; }
        public long? Likes { get; set; }
        public long? Comments { get; set; }
        public long? Shares { get; set; }
        public long? Saves { get; set; }
        public long Interactions { get; set; }
        public long? AvgWatchTimeMs { get; set; }
        public double? SkipRate { get; set; }
        public long? ViewsAt24h { get; set; }
        public long? ViewsAt7d { get; set; }
        public DateTime? MetricsSyncedUtc { get; set; }
        public string? ContentSource { get; set; }
        public string? ContentOrigin { get; set; }
        public string? ContentUrl { get; set; }
        public string? OriginalCaption { get; set; }
        public long? ContentViews { get; set; }
    }

    public sealed class SeriesPointDto
    {
        public DateTime T { get; set; }
        public double V { get; set; }
    }

    public sealed class AccountSummaryDto
    {
        public string AccountId { get; set; } = "";
        public string Username { get; set; } = "";
        public string? Name { get; set; }
        public string? ProfilePictureUrl { get; set; }
        public string ProfileUrl { get; set; } = "";
        public string? AccountType { get; set; }
        public bool Autopilot { get; set; }
        public bool Paused { get; set; }
        public bool RequireApproval { get; set; }
        public string Connection { get; set; } = "";
        public string? ConnectionDetail { get; set; }
        public DateTime? TokenExpiresUtc { get; set; }
        public long? Followers { get; set; }
        public long? FollowersDelta7d { get; set; }
        public List<double> FollowersSpark { get; set; } = new();
        public long? MediaCount { get; set; }
        public DateTime? LastPublishedUtc { get; set; }
        public PostSummaryDto? NextPost { get; set; }
        public PostSummaryDto? LastPost { get; set; }
        public int Pending { get; set; }
        public int AwaitingApproval { get; set; }
        public int Published7d { get; set; }
        public int Failed7d { get; set; }
        public double? AvgViews30d { get; set; }
        public double? AvgInteractions30d { get; set; }
        public long? Reach7d { get; set; }
        public int SlotsPerWeek { get; set; }
        public string Source { get; set; } = "";
        public string CaptionMode { get; set; } = "";
        public string TimeZone { get; set; } = "";
        public string? LastError { get; set; }
        public DateTime? LastErrorUtc { get; set; }
        public int ConsecutiveFailures { get; set; }
        public string? ContentWarning { get; set; }
        public int NewComments { get; set; }
        public bool Imported { get; set; }
        /// <summary>ok | attention | error | paused | idle | connect</summary>
        public string State { get; set; } = "ok";
        public string StateReason { get; set; } = "";
    }

    public sealed class AttentionItemDto
    {
        /// <summary>error | warning | info</summary>
        public string Level { get; set; } = "warning";
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        public string? AccountId { get; set; }
        /// <summary>A hint the UI turns into a button: settings, reconnect, connect, strategy, queue, public-url, v1-passwords.</summary>
        public string? Action { get; set; }
    }

    public sealed class HeatCellDto
    {
        public int Dow { get; set; }
        public int Hour { get; set; }
        public int Posts { get; set; }
        public double AvgViews { get; set; }
    }

    public sealed class RankedDto
    {
        public string Key { get; set; } = "";
        public int Posts { get; set; }
        public double AvgViews { get; set; }
        public double AvgInteractions { get; set; }
    }

    public sealed class TopPostDto
    {
        public string Id { get; set; } = "";
        public string? AccountId { get; set; }
        public string? Username { get; set; }
        public string? OmniPostId { get; set; }
        public string? Permalink { get; set; }
        public string? Caption { get; set; }
        public string? ProductType { get; set; }
        public string? ThumbnailUrl { get; set; }
        public long? Views { get; set; }
        public long? Likes { get; set; }
        public long? Comments { get; set; }
        public long? Interactions { get; set; }
        public DateTime PublishedUtc { get; set; }
        public bool Ours { get; set; }
        public bool HasThumbnail { get; set; }
    }

    public sealed class DailyAccountDto
    {
        public string Day { get; set; } = "";
        public long? Reach { get; set; }
        public long? Views { get; set; }
        public long? Interactions { get; set; }
        public long? Follows { get; set; }
        public long? Unfollows { get; set; }
        public long? ProfileLinksTaps { get; set; }
    }

    public sealed class DailyPostingDto
    {
        public string Day { get; set; } = "";
        public int Ours { get; set; }
        public int Others { get; set; }
        public long Views { get; set; }
    }

    public sealed class AnalyticsDto
    {
        public string? AccountId { get; set; }
        public int Days { get; set; }
        public DateTime FromUtc { get; set; }
        public DateTime ToUtc { get; set; }
        public string TimeZone { get; set; } = "UTC";
        public long? Followers { get; set; }
        public long? FollowersDelta { get; set; }
        public double? FollowersGrowthPerWeek { get; set; }
        public long? NewFollows { get; set; }
        public long? Unfollows { get; set; }
        public int PostsPublished { get; set; }
        public int PostsPublishedPrev { get; set; }
        public long Views { get; set; }
        public long ViewsPrev { get; set; }
        public double? AvgViews { get; set; }
        public double? MedianViews { get; set; }
        public long Interactions { get; set; }
        public long InteractionsPrev { get; set; }
        public double? AvgInteractions { get; set; }
        /// <summary>Interactions ÷ reach on posts published in range, percent.</summary>
        public double? EngagementRate { get; set; }
        /// <summary>Average interactions per post ÷ followers, percent.</summary>
        public double? EngagementPerFollower { get; set; }
        public long? AccountReach { get; set; }
        public long? AccountViews { get; set; }
        public long? Likes { get; set; }
        public long? Comments { get; set; }
        public long? Shares { get; set; }
        public long? Saves { get; set; }
        public double? AvgWatchSeconds { get; set; }
        public List<SeriesPointDto> FollowersSeries { get; set; } = new();
        public List<DailyAccountDto> Account { get; set; } = new();
        public List<DailyPostingDto> Posting { get; set; } = new();
        public List<HeatCellDto> Heatmap { get; set; } = new();
        public string? BestSlotLabel { get; set; }
        /// <summary>Average followers online per hour (0–23) in the analytics time zone; empty when unavailable.</summary>
        public List<double> OnlineByHour { get; set; } = new();
        public string? OnlinePeakLabel { get; set; }
        public List<TopPostDto> TopPosts { get; set; } = new();
        public List<RankedDto> Hashtags { get; set; } = new();
        public List<RankedDto> Sources { get; set; } = new();
        public List<RankedDto> CaptionModes { get; set; } = new();
        public List<RankedDto> Formats { get; set; } = new();
        public Dictionary<string, List<RankedDto>> Demographics { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string? DemographicsNote { get; set; }
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
        public long Views30d { get; set; }
        public double? AvgViews30d { get; set; }
        public double? AvgInteractions30d { get; set; }
        public long? Reach7d { get; set; }
        public int Pending { get; set; }
        public int AwaitingApproval { get; set; }
        public int Failed7d { get; set; }
        public double? SuccessRate30d { get; set; }
        public DateTime? NextPostUtc { get; set; }
        public string? NextPostAccount { get; set; }
        public int AutopilotAccounts { get; set; }
        public int Accounts { get; set; }
        public int ConnectedAccounts { get; set; }
    }

    /// <summary>
    /// Read models for the website, built from the live state (callers hold the store lock). Everything is
    /// computed from in-memory data, so routes stay O(state) with no I/O.
    /// </summary>
    internal static class OmniGramViews
    {
        public static PostSummaryDto Summary(OmniGramPost p, OmniGramAccount? account)
        {
            var first = p.Media.FirstOrDefault();
            return new PostSummaryDto
            {
                PostId = p.PostId,
                AccountId = p.AccountId,
                Username = account?.Username,
                Status = p.Status.ToString(),
                Origin = p.Origin.ToString(),
                Format = p.Format.ToString(),
                ScheduledUtc = p.ScheduledUtc,
                SlotUtc = p.SlotUtc,
                PublishedUtc = p.PublishedUtc,
                NextAttemptUtc = p.NextAttemptUtc,
                Caption = p.Caption,
                Hashtags = p.Hashtags.ToList(),
                HashtagPlacement = p.HashtagPlacement.ToString(),
                FirstComment = p.FirstComment,
                Credit = p.Credit,
                Footer = p.Footer,
                ComposedCaption = p.Format == PostFormat.Story ? "" : IgCaption.ComposeCaption(p.Caption, p.Hashtags, p.HashtagPlacement, p.Credit, p.Footer),
                AltText = p.AltText,
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
                Prepared = p.Media.Count > 0 && p.Media.All(m => m.PreparedPath != null && m.PreparedFor == p.Format),
                PrepareNotes = first?.PrepareNotes,
                PrepareError = p.Media.Select(m => m.PrepareError).FirstOrDefault(e => e != null),
                BlockedReason = p.BlockedReason,
                LastError = p.LastError,
                LastErrorCode = p.LastErrorCode,
                Attempts = p.Attempts,
                Stage = p.Publish.Stage.ToString(),
                ContainerStatus = p.Publish.ContainerStatus,
                Permalink = p.Permalink,
                IgMediaId = p.IgMediaId,
                FirstCommentError = p.FirstCommentError,
                FirstCommentPending = p.FirstCommentPending,
                Trial = p.Reel.Trial.ToString(),
                ShareToFeed = p.Reel.ShareToFeed,
                CompanionPostId = p.CompanionPostId,
                Views = p.Metrics.Views,
                Reach = p.Metrics.Reach,
                Likes = p.Metrics.Likes,
                Comments = p.Metrics.Comments,
                Shares = p.Metrics.Shares,
                Saves = p.Metrics.Saves,
                Interactions = p.Metrics.Interactions,
                AvgWatchTimeMs = p.Metrics.AvgWatchTimeMs,
                SkipRate = p.Metrics.SkipRate,
                ViewsAt24h = p.Metrics.ViewsAt24h,
                ViewsAt7d = p.Metrics.ViewsAt7d,
                MetricsSyncedUtc = p.Metrics.InsightsSyncedUtc ?? p.Metrics.CountsSyncedUtc,
                ContentSource = p.Content?.Kind.ToString(),
                ContentOrigin = p.Content?.Origin,
                ContentUrl = p.Content?.OriginalUrl,
                OriginalCaption = p.Content?.OriginalCaption,
                ContentViews = p.Content?.Views,
            };
        }

        public static long? FollowerDelta(AccountInsights insights, DateTime now, TimeSpan span, long? current)
        {
            if (current == null || insights.Snapshots.Count == 0) return null;
            var baseline = insights.Snapshots.LastOrDefault(s => s.Utc <= now - span && s.Followers.HasValue)
                ?? insights.Snapshots.FirstOrDefault(s => s.Followers.HasValue && s.Utc <= now - TimeSpan.FromHours(span.TotalHours / 2));
            return baseline?.Followers is long b ? current.Value - b : null;
        }

        public static List<double> FollowerSpark(AccountInsights insights, DateTime now, int days = 14)
        {
            var from = now.AddDays(-days);
            return insights.Snapshots.Where(s => s.Utc >= from && s.Followers.HasValue)
                .GroupBy(s => s.Utc.Date)
                .OrderBy(g => g.Key)
                .Select(g => (double)g.OrderBy(s => s.Utc).Last().Followers!.Value)
                .ToList();
        }

        private static IEnumerable<OmniGramPost> Published(IEnumerable<OmniGramPost> posts, DateTime from) =>
            posts.Where(p => p.Status == PostStatus.Published && p.PublishedUtc >= from && !OmniGramPlanner.IsCompanionStory(p));

        public static AccountSummaryDto AccountSummary(OmniGramState s, OmniGramAccount a, DateTime now)
        {
            var posts = s.PostsOf(a.AccountId);
            var insights = s.InsightsOf(a.AccountId);
            var pending = posts.Where(p => p.IsPending).OrderBy(p => p.ScheduledUtc).ToList();
            var published30 = Published(posts, now.AddDays(-30)).Where(p => p.Format != PostFormat.Story).ToList();
            var withViews = published30.Where(p => p.Metrics.Views.HasValue).ToList();
            var last = posts.Where(p => p.Status == PostStatus.Published).OrderByDescending(p => p.PublishedUtc).FirstOrDefault();
            var reachDays = insights.Days.Values.Where(d => string.CompareOrdinal(d.Day, now.AddDays(-7).ToString("yyyy-MM-dd")) > 0 && d.Reach.HasValue).ToList();
            var dto = new AccountSummaryDto
            {
                AccountId = a.AccountId,
                Username = a.Username,
                Name = a.Name,
                ProfilePictureUrl = a.ProfilePictureUrl,
                ProfileUrl = InstagramGraphClient.ProfileUrl(a.Username),
                AccountType = a.AccountType,
                Autopilot = a.Autopilot,
                Paused = a.Paused,
                RequireApproval = a.RequireApproval,
                Connection = a.Connection.ToString(),
                ConnectionDetail = a.ConnectionDetail,
                TokenExpiresUtc = a.TokenExpiresUtc,
                Followers = a.Stats.Followers,
                FollowersDelta7d = FollowerDelta(insights, now, TimeSpan.FromDays(7), a.Stats.Followers),
                FollowersSpark = FollowerSpark(insights, now),
                MediaCount = a.Stats.MediaCount,
                LastPublishedUtc = a.Stats.LastPublishedUtc,
                NextPost = pending.FirstOrDefault() is OmniGramPost next ? Summary(next, a) : null,
                LastPost = last == null ? null : Summary(last, a),
                Pending = pending.Count,
                AwaitingApproval = pending.Count(p => p.Status == PostStatus.AwaitingApproval),
                Published7d = Published(posts, now.AddDays(-7)).Count(),
                Failed7d = posts.Count(p => p.Status == PostStatus.Failed && p.UpdatedUtc > now.AddDays(-7)),
                AvgViews30d = withViews.Count > 0 ? Math.Round(withViews.Average(p => (double)p.Metrics.Views!.Value), 1) : null,
                AvgInteractions30d = published30.Count > 0 ? Math.Round(published30.Average(p => (double)p.Metrics.Interactions), 1) : null,
                Reach7d = reachDays.Count > 0 ? reachDays.Sum(d => d.Reach ?? 0) : null,
                SlotsPerWeek = OmniGramScheduleMath.SlotsPerWeek(a.Strategy),
                Source = a.Strategy.Source.ToString(),
                CaptionMode = a.Strategy.CaptionMode.ToString(),
                TimeZone = a.Strategy.TimeZone,
                LastError = a.Health.LastError,
                LastErrorUtc = a.Health.LastErrorUtc,
                ConsecutiveFailures = a.Health.ConsecutiveFailures,
                ContentWarning = a.Health.ContentWarning,
                NewComments = insights.Comments.Count(c => !c.Own && !c.Replied && c.ParentId == null && c.Utc > now.AddDays(-7)),
                Imported = a.Legacy != null,
            };
            (dto.State, dto.StateReason) = AccountState(a, pending, now);
            return dto;
        }

        private static (string, string) AccountState(OmniGramAccount a, List<OmniGramPost> pending, DateTime now)
        {
            if (!a.IsConnected) return ("connect", a.Legacy != null ? "Imported from v1 — connect it with Instagram" : "Not connected to Instagram yet");
            if (a.Connection == ConnectionState.NeedsReauth) return ("error", "Reconnect: " + (a.ConnectionDetail ?? "Instagram no longer accepts its token"));
            if (a.Connection == ConnectionState.Restricted) return ("error", "Restricted by Instagram: " + (a.ConnectionDetail ?? "open the Instagram app"));
            if (a.Paused) return ("paused", "Paused");
            if (a.Health.CooldownUntilUtc is DateTime cooldown && cooldown > now) return ("attention", $"Cooling down until {cooldown:HH:mm} UTC: {a.Health.CooldownReason}");
            if (a.Health.ConsecutiveFailures >= 2) return ("error", "Publishing is failing: " + (a.Health.LastError ?? "unknown error"));
            if (a.Health.ContentWarning != null && a.Autopilot) return ("attention", a.Health.ContentWarning);
            var blocked = pending.FirstOrDefault(p => p.ScheduledUtc <= now && p.BlockedReason != null);
            if (blocked != null) return ("attention", blocked.BlockedReason!);
            if (pending.Any(p => p.Status == PostStatus.AwaitingApproval)) return ("attention", "Posts are waiting for approval");
            if (a.Autopilot && a.Strategy.Slots.Count == 0) return ("attention", "Autopilot is on but the schedule has no slots");
            if (a.TokenExpiresUtc is DateTime expires && expires - now < TimeSpan.FromDays(5)) return ("attention", $"Its Instagram token expires {expires:d MMM}; it renews automatically while the account is in use");
            if (!a.Autopilot && pending.Count == 0) return ("idle", "Autopilot is off and nothing is scheduled");
            return ("ok", a.Autopilot ? "On autopilot" : "Manual posting");
        }

        public static FleetKpisDto FleetKpis(OmniGramState s, DateTime now)
        {
            var all = s.AllPosts().ToList();
            var published30 = Published(all, now.AddDays(-30)).ToList();
            var feed30 = published30.Where(p => p.Format != PostFormat.Story).ToList();
            var withViews = feed30.Where(p => p.Metrics.Views.HasValue).ToList();
            int failed30 = all.Count(p => p.Status == PostStatus.Failed && p.UpdatedUtc > now.AddDays(-30));
            var next = all.Where(p => p.IsPending).OrderBy(p => p.ScheduledUtc).FirstOrDefault();
            var kpis = new FleetKpisDto
            {
                Accounts = s.Accounts.Count,
                ConnectedAccounts = s.Accounts.Values.Count(a => a.IsConnected),
                AutopilotAccounts = s.Accounts.Values.Count(a => a.Autopilot && !a.Paused && a.IsConnected),
                Followers = s.Accounts.Values.Sum(a => a.Stats.Followers ?? 0),
                Published7d = Published(all, now.AddDays(-7)).Count(),
                PublishedPrev7d = all.Count(p => p.Status == PostStatus.Published && p.PublishedUtc <= now.AddDays(-7) && p.PublishedUtc > now.AddDays(-14) && !OmniGramPlanner.IsCompanionStory(p)),
                Views30d = withViews.Sum(p => p.Metrics.Views ?? 0),
                AvgViews30d = withViews.Count > 0 ? Math.Round(withViews.Average(p => (double)p.Metrics.Views!.Value), 1) : null,
                AvgInteractions30d = feed30.Count > 0 ? Math.Round(feed30.Average(p => (double)p.Metrics.Interactions), 1) : null,
                Pending = all.Count(p => p.IsPending),
                AwaitingApproval = all.Count(p => p.Status == PostStatus.AwaitingApproval),
                Failed7d = all.Count(p => p.Status == PostStatus.Failed && p.UpdatedUtc > now.AddDays(-7)),
                SuccessRate30d = published30.Count + failed30 > 0 ? Math.Round(100.0 * published30.Count / (published30.Count + failed30), 1) : null,
                NextPostUtc = next?.ScheduledUtc,
                NextPostAccount = next == null ? null : s.Account(next.AccountId)?.Username,
            };

            long? d7 = null, d30 = null, reach = null;
            string weekAgo = now.AddDays(-7).ToString("yyyy-MM-dd");
            foreach (var account in s.Accounts.Values)
            {
                var insights = s.InsightsOf(account.AccountId);
                if (FollowerDelta(insights, now, TimeSpan.FromDays(7), account.Stats.Followers) is long a) d7 = (d7 ?? 0) + a;
                if (FollowerDelta(insights, now, TimeSpan.FromDays(30), account.Stats.Followers) is long c) d30 = (d30 ?? 0) + c;
                foreach (var day in insights.Days.Values.Where(d => string.CompareOrdinal(d.Day, weekAgo) > 0 && d.Reach.HasValue)) reach = (reach ?? 0) + day.Reach;
            }
            kpis.FollowersDelta7d = d7;
            kpis.FollowersDelta30d = d30;
            kpis.Reach7d = reach;
            kpis.FollowersSeries = FleetFollowerSeries(s, now, 30);
            return kpis;
        }

        /// <summary>Daily total followers across accounts, carrying each account's last known value forward.</summary>
        public static List<SeriesPointDto> FleetFollowerSeries(OmniGramState s, DateTime now, int days)
        {
            var result = new List<SeriesPointDto>();
            var start = now.Date.AddDays(-days + 1);
            var perAccount = s.Accounts.Values.Select(a => s.InsightsOf(a.AccountId).Snapshots.Where(x => x.Followers.HasValue).OrderBy(x => x.Utc).ToList()).ToList();
            for (var day = start; day <= now.Date; day = day.AddDays(1))
            {
                var end = day.AddDays(1);
                long total = 0;
                bool any = false;
                foreach (var snaps in perAccount)
                {
                    var lastKnown = snaps.LastOrDefault(x => x.Utc < end);
                    if (lastKnown?.Followers is long f) { total += f; any = true; }
                }
                if (any) result.Add(new SeriesPointDto { T = day, V = total });
            }
            return result;
        }

        /// <param name="mediaToolsProblem">Why FFmpeg cannot be used, or null when it works (or has not been checked yet).</param>
        public static List<AttentionItemDto> Attention(OmniGramState s, DateTime now, bool publishingEnabled, bool aiAvailable, bool memeScraperReady, string publicBaseUrl, string? mediaToolsProblem = null)
        {
            var items = new List<AttentionItemDto>();
            if (!publishingEnabled)
                items.Add(new AttentionItemDto { Level = "warning", Title = "Publishing is switched off", Detail = "The OmniSetting OmniGramV2_PublishingEnabled is false; posts are prepared but not published.", Action = "settings" });
            if (mediaToolsProblem != null && s.Accounts.Values.Any(a => a.IsConnected))
                items.Add(new AttentionItemDto
                {
                    Level = "warning",
                    Title = "FFmpeg is not usable",
                    Detail = $"{mediaToolsProblem}. MP4 videos and JPEG images are posted as they are, without checks; anything else waits. OmniGram looks for FFmpeg in the shared FFmpeg folder, then on the PATH.",
                    Action = "settings",
                });

            var check = s.Engine.PublicUrlCheck;
            bool fetchedRecently = s.Engine.LastExternalMediaFetchUtc is DateTime fetched && now - fetched < TimeSpan.FromDays(3);
            if (check is { Ok: false } && !fetchedRecently && s.Accounts.Values.Any(a => a.IsConnected))
                items.Add(new AttentionItemDto { Level = "warning", Title = "Instagram may not be able to download media", Detail = (check.Detail ?? "The public media URL check failed.") + $" Instagram downloads every photo and video from {publicBaseUrl}.", Action = "public-url" });
            int fetchFailures = s.AllPosts().Count(p => p.Status == PostStatus.Failed && p.UpdatedUtc > now.AddDays(-2) && p.LastErrorCode != null && (p.LastErrorCode.Contains("2207052") || p.LastErrorCode.StartsWith("9004")));
            if (fetchFailures > 0)
                items.Add(new AttentionItemDto { Level = "error", Title = "Instagram could not download media", Detail = $"{fetchFailures} post(s) failed because Instagram could not fetch the file from {publicBaseUrl}. Check the public media URL in Settings.", Action = "public-url" });

            if (s.Accounts.Count == 0)
                items.Add(new AttentionItemDto { Level = "info", Title = "Connect an Instagram account", Detail = "OmniGram posts to Instagram professional (Creator or Business) accounts through Instagram's official API. Connect one to get started.", Action = "connect" });
            foreach (var a in s.Accounts.Values.Where(a => !a.IsConnected).OrderBy(a => a.Username))
                items.Add(new AttentionItemDto
                {
                    Level = a.Legacy != null ? "warning" : "info",
                    Title = $"Connect @{a.Username}",
                    Detail = a.Legacy != null
                        ? "Imported from OmniGram v1, which logged in with passwords and stopped working. Connect it through Instagram's official login (it must be a Creator or Business account); its v1 settings and history are kept."
                        : "This account has no Instagram token yet.",
                    AccountId = a.AccountId,
                    Action = "connect",
                });
            foreach (var a in s.Accounts.Values.Where(a => a.Connection == ConnectionState.NeedsReauth))
                items.Add(new AttentionItemDto { Level = "error", Title = $"Reconnect @{a.Username}", Detail = a.ConnectionDetail ?? "Instagram no longer accepts this account's token.", AccountId = a.AccountId, Action = "reconnect" });
            foreach (var a in s.Accounts.Values.Where(a => a.Connection == ConnectionState.Restricted))
                items.Add(new AttentionItemDto { Level = "error", Title = $"Instagram restricted @{a.Username}", Detail = (a.ConnectionDetail ?? "") + " Open the Instagram app on this account and complete whatever it asks; posting resumes by itself.", AccountId = a.AccountId });
            foreach (var a in s.Accounts.Values.Where(a => a.IsConnected && a.Connection == ConnectionState.Healthy && a.TokenExpiresUtc is DateTime e && e - now < TimeSpan.FromDays(7)))
                items.Add(new AttentionItemDto { Level = "warning", Title = $"@{a.Username}'s token expires soon", Detail = $"It expires {a.TokenExpiresUtc:d MMM} and could not be renewed yet. Reconnect the account if renewal keeps failing.", AccountId = a.AccountId, Action = "reconnect" });

            bool needsAi = s.Accounts.Values.Any(a => a.Autopilot && a.Strategy.CaptionMode == CaptionMode.AI);
            if (needsAi && !aiAvailable)
                items.Add(new AttentionItemDto { Level = "warning", Title = "AI captions unavailable", Detail = "KliveLLM is not running; AI-captioned posts will wait or use their fallback caption." });
            bool needsReels = s.Accounts.Values.Any(a => a.Autopilot && a.Strategy.Source == ContentSourceKind.MemeScraper);
            if (needsReels && !memeScraperReady)
                items.Add(new AttentionItemDto { Level = "warning", Title = "MemeScraper is not ready", Detail = "Accounts that post MemeScraper reels cannot plan new posts until MemeScraper has loaded." });

            foreach (var a in s.Accounts.Values.OrderBy(a => a.Username))
            {
                if (a.Health.ConsecutiveFailures >= 2)
                    items.Add(new AttentionItemDto { Level = "error", Title = $"@{a.Username} is failing to publish", Detail = a.Health.LastError ?? "", AccountId = a.AccountId, Action = "queue" });
                if (a.Autopilot && a.Health.ContentWarning != null)
                    items.Add(new AttentionItemDto { Level = "warning", Title = $"@{a.Username} is out of content", Detail = a.Health.ContentWarning, AccountId = a.AccountId, Action = "strategy" });
                int awaiting = s.PostsOf(a.AccountId).Count(p => p.Status == PostStatus.AwaitingApproval);
                if (awaiting > 0)
                    items.Add(new AttentionItemDto { Level = "info", Title = $"{awaiting} post(s) on @{a.Username} need approval", Detail = "Approve them in the queue or turn off approval in the account's strategy.", AccountId = a.AccountId, Action = "queue" });
                if (a.Autopilot && !a.Paused && a.Strategy.Slots.Count == 0)
                    items.Add(new AttentionItemDto { Level = "warning", Title = $"@{a.Username} has no schedule", Detail = "Autopilot is on but the weekly schedule has no time slots.", AccountId = a.AccountId, Action = "strategy" });
            }

            if (s.Engine.V1PasswordFilesPresent)
                items.Add(new AttentionItemDto { Level = "warning", Title = "Old Instagram passwords are stored in plain text", Detail = "OmniGram v1 kept account passwords unencrypted under SavedData/OmniGram/Accounts. v2 never needs them. Remove those files once you no longer need the passwords.", Action = "v1-passwords" });
            return items;
        }

        // ─────────────────────────────── Analytics ───────────────────────────────

        public static AnalyticsDto Analytics(OmniGramState s, string? accountId, int days, DateTime now)
        {
            days = Math.Clamp(days, 1, 730);
            var accounts = accountId == null ? s.Accounts.Values.ToList() : s.Account(accountId) is OmniGramAccount only ? new List<OmniGramAccount> { only } : new();
            var tz = accountId != null && accounts.Count == 1 ? OmniGramScheduleMath.ResolveTimeZone(accounts[0].Strategy.TimeZone) : TimeZoneInfo.Utc;
            var from = now.AddDays(-days);
            var prevFrom = from.AddDays(-days);
            var dto = new AnalyticsDto { AccountId = accountId, Days = days, FromUtc = from, ToUtc = now, TimeZone = tz.Id };

            var ours = accounts.SelectMany(a => s.PostsOf(a.AccountId).Select(p => (Account: a, Post: p)))
                .Where(x => x.Post.Status == PostStatus.Published && x.Post.PublishedUtc.HasValue && !OmniGramPlanner.IsCompanionStory(x.Post))
                .ToList();
            var inRange = ours.Where(x => x.Post.PublishedUtc >= from).ToList();
            var inPrev = ours.Where(x => x.Post.PublishedUtc >= prevFrom && x.Post.PublishedUtc < from).ToList();
            dto.PostsPublished = inRange.Count;
            dto.PostsPublishedPrev = inPrev.Count;
            dto.Views = inRange.Sum(x => x.Post.Metrics.Views ?? 0);
            dto.ViewsPrev = inPrev.Sum(x => x.Post.Metrics.Views ?? 0);
            dto.Interactions = inRange.Sum(x => x.Post.Metrics.Interactions);
            dto.InteractionsPrev = inPrev.Sum(x => x.Post.Metrics.Interactions);
            var feed = inRange.Where(x => x.Post.Format != PostFormat.Story).ToList();
            var withViews = feed.Where(x => x.Post.Metrics.Views.HasValue).Select(x => (double)x.Post.Metrics.Views!.Value).OrderBy(v => v).ToList();
            if (withViews.Count > 0)
            {
                dto.AvgViews = Math.Round(withViews.Average(), 1);
                dto.MedianViews = withViews.Count % 2 == 1 ? withViews[withViews.Count / 2] : (withViews[withViews.Count / 2 - 1] + withViews[withViews.Count / 2]) / 2;
            }
            if (feed.Count > 0) dto.AvgInteractions = Math.Round(feed.Average(x => (double)x.Post.Metrics.Interactions), 1);
            long reachOnPosts = feed.Sum(x => x.Post.Metrics.Reach ?? 0);
            long interactionsWithReach = feed.Where(x => x.Post.Metrics.Reach > 0).Sum(x => x.Post.Metrics.Interactions);
            if (reachOnPosts > 0) dto.EngagementRate = Math.Round(100.0 * interactionsWithReach / reachOnPosts, 2);
            if (feed.Count > 0)
            {
                dto.Likes = feed.Sum(x => x.Post.Metrics.Likes ?? 0);
                dto.Comments = feed.Sum(x => x.Post.Metrics.Comments ?? 0);
                dto.Shares = feed.Sum(x => x.Post.Metrics.Shares ?? 0);
                dto.Saves = feed.Sum(x => x.Post.Metrics.Saves ?? 0);
            }
            var watch = feed.Where(x => x.Post.Metrics.AvgWatchTimeMs > 0).Select(x => x.Post.Metrics.AvgWatchTimeMs!.Value).ToList();
            if (watch.Count > 0) dto.AvgWatchSeconds = Math.Round(watch.Average() / 1000.0, 1);

            // Followers.
            long? followers = accounts.Any(a => a.Stats.Followers.HasValue) ? accounts.Sum(a => a.Stats.Followers ?? 0) : null;
            dto.Followers = followers;
            dto.FollowersSeries = accountId == null
                ? FleetFollowerSeries(s, now, days)
                : accounts.Count == 1
                    ? s.InsightsOf(accounts[0].AccountId).Snapshots.Where(x => x.Utc >= from && x.Followers.HasValue).Select(x => new SeriesPointDto { T = x.Utc, V = x.Followers!.Value }).ToList()
                    : new();
            long? delta = null;
            foreach (var a in accounts)
                if (FollowerDelta(s.InsightsOf(a.AccountId), now, TimeSpan.FromDays(days), a.Stats.Followers) is long d) delta = (delta ?? 0) + d;
            dto.FollowersDelta = delta;
            if (delta.HasValue && days >= 7) dto.FollowersGrowthPerWeek = Math.Round(delta.Value / (days / 7.0), 1);
            if (followers is > 0 && dto.AvgInteractions.HasValue) dto.EngagementPerFollower = Math.Round(dto.AvgInteractions.Value / followers.Value * 100, 3);

            // Daily account insights (Instagram's days).
            var daily = new SortedDictionary<string, DailyAccountDto>(StringComparer.Ordinal);
            string fromDay = from.ToString("yyyy-MM-dd");
            foreach (var a in accounts)
            {
                foreach (var (key, day) in s.InsightsOf(a.AccountId).Days)
                {
                    if (string.CompareOrdinal(key, fromDay) < 0) continue;
                    if (!daily.TryGetValue(key, out var row)) daily[key] = row = new DailyAccountDto { Day = key };
                    row.Reach = Add(row.Reach, day.Reach);
                    row.Views = Add(row.Views, day.Views);
                    row.Interactions = Add(row.Interactions, day.TotalInteractions);
                    row.Follows = Add(row.Follows, day.Follows);
                    row.Unfollows = Add(row.Unfollows, day.Unfollows);
                    row.ProfileLinksTaps = Add(row.ProfileLinksTaps, day.ProfileLinksTaps);
                }
            }
            dto.Account = daily.Values.ToList();
            if (dto.Account.Any(d => d.Reach.HasValue)) dto.AccountReach = dto.Account.Sum(d => d.Reach ?? 0);
            if (dto.Account.Any(d => d.Views.HasValue)) dto.AccountViews = dto.Account.Sum(d => d.Views ?? 0);
            if (dto.Account.Any(d => d.Follows.HasValue)) dto.NewFollows = dto.Account.Sum(d => d.Follows ?? 0);
            if (dto.Account.Any(d => d.Unfollows.HasValue)) dto.Unfollows = dto.Account.Sum(d => d.Unfollows ?? 0);

            // Posting per day (ours vs made in the app) with the views they earned.
            var index = accounts.SelectMany(a => s.InsightsOf(a.AccountId).Index.Select(m => (Account: a, Media: m))).ToList();
            var posting = new SortedDictionary<string, DailyPostingDto>(StringComparer.Ordinal);
            for (var d = OmniGramScheduleMath.UtcToLocal(from, tz).Date; d <= OmniGramScheduleMath.UtcToLocal(now, tz).Date; d = d.AddDays(1))
                posting[d.ToString("yyyy-MM-dd")] = new DailyPostingDto { Day = d.ToString("yyyy-MM-dd") };
            foreach (var (_, m) in index.Where(x => x.Media.PublishedUtc >= from))
            {
                string key = OmniGramScheduleMath.UtcToLocal(m.PublishedUtc, tz).ToString("yyyy-MM-dd");
                if (!posting.TryGetValue(key, out var row)) posting[key] = row = new DailyPostingDto { Day = key };
                if (m.OmniPostId != null) row.Ours++; else row.Others++;
                row.Views += m.Views ?? 0;
            }
            dto.Posting = posting.Values.ToList();

            // Best times: media 1–120 days old by local weekday/hour of publishing, judged on views (likes when views are unknown).
            var sample = index.Where(x => x.Media.PublishedUtc <= now.AddDays(-1) && x.Media.PublishedUtc >= now.AddDays(-120) && x.Media.ProductType != "STORY").Select(x => x.Media).ToList();
            dto.SampleSize = sample.Count;
            bool byViews = sample.Count(m => m.Views.HasValue) >= sample.Count / 2;
            dto.Heatmap = sample
                .GroupBy(m =>
                {
                    var local = OmniGramScheduleMath.UtcToLocal(m.PublishedUtc, tz);
                    return ((int)local.DayOfWeek, local.Hour);
                })
                .Select(g => new HeatCellDto
                {
                    Dow = g.Key.Item1,
                    Hour = g.Key.Hour,
                    Posts = g.Count(),
                    AvgViews = Math.Round(g.Average(m => (double)(byViews ? m.Views ?? 0 : m.Likes ?? 0)), 1),
                })
                .OrderBy(c => c.Dow).ThenBy(c => c.Hour)
                .ToList();
            var best = dto.Heatmap.Where(c => c.Posts >= 2).OrderByDescending(c => c.AvgViews).FirstOrDefault();
            if (best != null)
                dto.BestSlotLabel = $"{(DayOfWeek)best.Dow} {best.Hour:00}:00–{(best.Hour + 1) % 24:00}:00 ({best.AvgViews:0.#} {(byViews ? "views" : "likes")} avg over {best.Posts} posts)";

            // When followers are online (Instagram reports Pacific-time hours; shown in the analytics zone).
            if (accounts.Count == 1 && s.InsightsOf(accounts[0].AccountId).OnlineFollowersByHour is { Count: > 0 } online)
            {
                var pt = OmniGramScheduleMath.ResolveTimeZone(OmniGramScheduleMath.InsightsTimeZone);
                var ptMidnight = OmniGramScheduleMath.LocalToUtc(OmniGramScheduleMath.UtcToLocal(now, pt).Date, pt);
                var byLocal = new double[24];
                for (int h = 0; h < 24; h++)
                {
                    if (!online.TryGetValue(h, out var count)) continue;
                    int localHour = OmniGramScheduleMath.UtcToLocal(ptMidnight.AddHours(h), tz).Hour;
                    byLocal[localHour] = count;
                }
                dto.OnlineByHour = byLocal.ToList();
                int peak = Array.IndexOf(byLocal, byLocal.Max());
                dto.OnlinePeakLabel = $"Most followers are online around {peak:00}:00 ({byLocal[peak]:0} on average)";
            }

            // Top media in range (all of the account's posts, ours flagged).
            var ourById = ours.Where(x => x.Post.IgMediaId != null).GroupBy(x => x.Post.IgMediaId!).ToDictionary(g => g.Key, g => g.First().Post, StringComparer.Ordinal);
            dto.TopPosts = index.Where(x => x.Media.PublishedUtc >= from)
                .OrderByDescending(x => x.Media.Views ?? 0).ThenByDescending(x => (x.Media.Likes ?? 0) + (x.Media.Comments ?? 0))
                .Take(12)
                .Select(x => new TopPostDto
                {
                    Id = x.Media.Id,
                    AccountId = x.Account.AccountId,
                    Username = x.Account.Username,
                    OmniPostId = x.Media.OmniPostId,
                    Permalink = x.Media.Permalink,
                    Caption = x.Media.Caption,
                    ProductType = x.Media.ProductType,
                    ThumbnailUrl = x.Media.ThumbnailUrl,
                    Views = x.Media.Views,
                    Likes = x.Media.Likes,
                    Comments = x.Media.Comments,
                    Interactions = x.Media.TotalInteractions ?? (x.Media.Likes ?? 0) + (x.Media.Comments ?? 0) + (x.Media.Shares ?? 0) + (x.Media.Saves ?? 0),
                    PublishedUtc = x.Media.PublishedUtc,
                    Ours = x.Media.OmniPostId != null,
                    HasThumbnail = x.Media.OmniPostId != null && ourById.TryGetValue(x.Media.Id, out var p) && p.Media.Any(m => m.ThumbnailPath != null),
                })
                .ToList();

            // What works: hashtags, content sources, caption styles, formats (our posts, 90+ days).
            var scored = ours.Where(x => x.Post.PublishedUtc >= now.AddDays(-Math.Max(days, 90)) && x.Post.Format != PostFormat.Story).Select(x => x.Post).ToList();
            dto.Hashtags = Rank(scored.SelectMany(p => p.Hashtags.Select(t => (Key: t.ToLowerInvariant(), Post: p))), 2, 15);
            dto.Sources = Rank(scored.Where(p => p.Content?.Origin != null).Select(p => (Key: p.Content!.Origin!, Post: p)), 1, 12);
            dto.CaptionModes = Rank(scored.Select(p => (Key: p.CaptionInfo.Mode.ToString() + (p.CaptionInfo.UsedVision ? " (vision)" : ""), Post: p)), 1, 8);
            dto.Formats = Rank(scored.Select(p => (Key: p.Format.ToString() + (p.Reel.Trial != TrialReelMode.Off ? " (trial)" : ""), Post: p)), 1, 6);

            // Audience.
            if (accounts.Count == 1 && s.InsightsOf(accounts[0].AccountId).Demographics is { } demo)
            {
                foreach (var (breakdown, values) in demo.Followers)
                {
                    long total = values.Values.Sum();
                    dto.Demographics[breakdown] = values.OrderByDescending(kv => kv.Value).Take(10)
                        .Select(kv => new RankedDto { Key = kv.Key, Posts = (int)Math.Min(int.MaxValue, kv.Value), AvgViews = total > 0 ? Math.Round(100.0 * kv.Value / total, 1) : 0 })
                        .ToList();
                }
                dto.DemographicsNote = demo.Error;
            }
            return dto;
        }

        private static long? Add(long? a, long? b) => a == null && b == null ? null : (a ?? 0) + (b ?? 0);

        private static List<RankedDto> Rank(IEnumerable<(string Key, OmniGramPost Post)> items, int minPosts, int take) =>
            items.GroupBy(i => i.Key, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= minPosts)
                .Select(g => new RankedDto
                {
                    Key = g.Key,
                    Posts = g.Count(),
                    AvgViews = Math.Round(g.Average(i => (double)(i.Post.Metrics.Views ?? 0)), 1),
                    AvgInteractions = Math.Round(g.Average(i => (double)i.Post.Metrics.Interactions), 1),
                })
                .OrderByDescending(r => r.AvgViews).ThenByDescending(r => r.AvgInteractions).ThenByDescending(r => r.Posts)
                .Take(take)
                .ToList();
    }
}
