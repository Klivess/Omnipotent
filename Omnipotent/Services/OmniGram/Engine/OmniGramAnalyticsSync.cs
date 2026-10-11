using Omnipotent.Services.OmniGram.Api;
using Omnipotent.Services.OmniGram.Models;
using System.Collections.Concurrent;

namespace Omnipotent.Services.OmniGram.Engine
{
    /// <summary>
    /// Counts our Instagram API calls and remembers Meta's usage headers, so background sync backs off long
    /// before the app's allowance could block publishing or the website.
    /// </summary>
    internal sealed class OmniGramApiBudget
    {
        private readonly ConcurrentDictionary<string, int> perHour = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> perEndpointToday = new(StringComparer.Ordinal);
        private readonly Func<DateTime> clock;
        private string endpointDay = "";

        public OmniGramApiBudget(Func<DateTime> clock) => this.clock = clock;

        public IgUsageHeaders? LastUsage { get; private set; }

        public void Count(string label)
        {
            DateTime now = clock();
            perHour.AddOrUpdate(now.ToString("yyyyMMddHH"), 1, (_, v) => v + 1);
            string day = now.ToString("yyyyMMdd");
            if (day != endpointDay) { perEndpointToday.Clear(); endpointDay = day; }
            perEndpointToday.AddOrUpdate(label, 1, (_, v) => v + 1);
            if (perHour.Count > 60)
                foreach (string key in perHour.Keys.OrderBy(k => k).Take(perHour.Count - 48)) perHour.TryRemove(key, out _);
        }

        public void Observe(IgUsageHeaders usage) => LastUsage = usage;

        public int CallsThisHour() => perHour.TryGetValue(clock().ToString("yyyyMMddHH"), out var v) ? v : 0;

        public int CallsToday()
        {
            string day = clock().ToString("yyyyMMdd");
            return perHour.Where(kv => kv.Key.StartsWith(day, StringComparison.Ordinal)).Sum(kv => kv.Value);
        }

        public IReadOnlyDictionary<string, int> CallsByEndpointToday() => new Dictionary<string, int>(perEndpointToday);

        /// <summary>Background analytics may run: Meta reports plenty of headroom (publishing always keeps priority).</summary>
        public bool AllowBackground()
        {
            var u = LastUsage;
            if (u == null || clock() - u.ObservedUtc > TimeSpan.FromHours(1)) return true;
            if (u.RegainAccessMinutes is > 0 && clock() < u.ObservedUtc.AddMinutes(u.RegainAccessMinutes.Value)) return false;
            return u.MaxPercent < 70;
        }
    }

    /// <summary>
    /// Keeps accounts and analytics fresh with a small job scheduler persisted in engine.json. Per account:
    /// profile (followers), token renewal, the publishing allowance, the media index with like/comment counts,
    /// post insights read at 2 h, 1 d, 3 d, 7 d and 30 d after publishing, daily account insights, when
    /// followers are online, audience demographics and comments. Jobs are staggered, budgeted and independent —
    /// one failing account or metric never stalls the rest.
    /// </summary>
    internal sealed class OmniGramAnalyticsSync
    {
        private static readonly TimeSpan ProfileEvery = TimeSpan.FromHours(3);
        private static readonly TimeSpan TokenEvery = TimeSpan.FromHours(12);
        private static readonly TimeSpan LimitEvery = TimeSpan.FromHours(1);
        private static readonly TimeSpan MediaActiveEvery = TimeSpan.FromHours(1);
        private static readonly TimeSpan MediaIdleEvery = TimeSpan.FromHours(6);
        private static readonly TimeSpan InsightsEvery = TimeSpan.FromHours(1);
        private static readonly TimeSpan DaysEvery = TimeSpan.FromHours(6);
        private static readonly TimeSpan OnlineEvery = TimeSpan.FromDays(1);
        private static readonly TimeSpan DemographicsEvery = TimeSpan.FromDays(7);
        private static readonly TimeSpan CommentsEvery = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan HousekeepingEvery = TimeSpan.FromHours(24);
        private static readonly TimeSpan PublicUrlEvery = TimeSpan.FromHours(6);
        private static readonly TimeSpan[] FeedMilestones = { TimeSpan.FromHours(2), TimeSpan.FromDays(1), TimeSpan.FromDays(3), TimeSpan.FromDays(7), TimeSpan.FromDays(30) };
        private static readonly TimeSpan[] StoryMilestones = { TimeSpan.FromHours(1), TimeSpan.FromHours(6), TimeSpan.FromHours(20) };

        internal static readonly string[] ReelMetrics = { "views", "reach", "likes", "comments", "shares", "saved", "total_interactions", "ig_reels_avg_watch_time", "ig_reels_video_view_total_time", "reels_skip_rate", "reposts" };
        internal static readonly string[] FeedMetrics = { "views", "reach", "likes", "comments", "shares", "saved", "total_interactions", "follows", "profile_visits", "reposts" };
        internal static readonly string[] StoryMetrics = { "views", "reach", "shares", "total_interactions", "follows", "profile_visits", "replies", "reposts" };
        internal static readonly string[] AccountMetrics = { "reach", "views", "accounts_engaged", "total_interactions", "likes", "comments", "shares", "saves", "replies", "reposts", "profile_links_taps" };

        private readonly OmniGramStore store;
        private readonly IInstagramApi api;
        private readonly OmniGramAuth auth;
        private readonly OmniGramApiBudget budget;
        private readonly OmniGramMediaHost mediaHost;
        private readonly Func<DateTime> clock;
        private readonly Action<string> log;
        private readonly ConcurrentDictionary<string, string> lastErrors = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim runLock = new(1, 1);

        /// <summary>Plain HTTP for the public-URL self-check (direct, no proxy). Null skips that job.</summary>
        public HttpClient? SelfCheckHttp { get; set; }

        public OmniGramAnalyticsSync(OmniGramStore store, IInstagramApi api, OmniGramAuth auth, OmniGramApiBudget budget, OmniGramMediaHost mediaHost,
            Func<DateTime> clock, Action<string> log)
        {
            this.store = store;
            this.api = api;
            this.auth = auth;
            this.budget = budget;
            this.mediaHost = mediaHost;
            this.clock = clock;
            this.log = log;
        }

        public IReadOnlyDictionary<string, string> LastErrors => lastErrors;

        private enum JobKind { Profile, Token, Limit, Media, Insights, Days, Online, Demographics, Comments, Housekeeping, PublicUrl }

        private sealed record Job(string Key, JobKind Kind, string Target, int Priority);

        /// <summary>Runs every job that is due. Returns how many ran.</summary>
        public async Task<int> RunDueAsync(CancellationToken ct)
        {
            if (!await runLock.WaitAsync(0, ct)) return 0;
            try
            {
                DateTime now = clock();
                var jobs = DueJobs(now);
                int ran = 0;
                foreach (var job in jobs)
                {
                    if (ct.IsCancellationRequested) break;
                    if (job.Priority > 1 && !budget.AllowBackground())
                    {
                        Reschedule(job.Key, clock().AddMinutes(20));
                        continue;
                    }
                    await RunJobAsync(job, ct);
                    ran++;
                }
                return ran;
            }
            finally
            {
                runLock.Release();
            }
        }

        /// <summary>Refreshes one account now (profile, limit, media, insights, comments) — the "refresh" button.</summary>
        public async Task<List<string>> SyncAccountNowAsync(string accountId, CancellationToken ct)
        {
            var errors = new List<string>();
            foreach (var kind in new[] { JobKind.Profile, JobKind.Limit, JobKind.Media, JobKind.Insights, JobKind.Days, JobKind.Comments })
            {
                var job = new Job(JobKey(kind, accountId), kind, accountId, 0);
                await RunJobAsync(job, ct);
                if (lastErrors.TryGetValue(job.Key, out var error)) errors.Add($"{kind}: {error}");
            }
            return errors;
        }

        /// <summary>Makes an account's jobs due immediately (after connecting, reconnecting…).</summary>
        public void ExpediteAccount(string accountId)
        {
            store.Mutate(s =>
            {
                foreach (var kind in new[] { JobKind.Profile, JobKind.Limit, JobKind.Media, JobKind.Days, JobKind.Comments })
                    s.Engine.NextDueUtc[JobKey(kind, accountId)] = DateTime.MinValue;
                s.MarkEngine();
            });
        }

        public void ExpediteHousekeeping(bool publicUrl)
        {
            store.Mutate(s =>
            {
                if (publicUrl) s.Engine.NextDueUtc["public-url"] = DateTime.MinValue;
                s.MarkEngine();
            });
        }

        private static string JobKey(JobKind kind, string target) => kind switch
        {
            JobKind.Housekeeping => "housekeeping",
            JobKind.PublicUrl => "public-url",
            _ => kind.ToString().ToLowerInvariant() + ":" + target,
        };

        private List<Job> DueJobs(DateTime now) => store.Mutate(s =>
        {
            var jobs = new List<Job>();
            var live = new HashSet<string>(StringComparer.Ordinal) { "housekeeping", "public-url" };
            void Consider(JobKind kind, string target, int priority)
            {
                string key = JobKey(kind, target);
                live.Add(key);
                if (!s.Engine.NextDueUtc.TryGetValue(key, out var due))
                {
                    // New job: stagger first runs over a few minutes so a restart doesn't burst the API.
                    due = now.AddSeconds(20 + OmniGramScheduleMath.Fnv1a(key) % 240);
                    s.Engine.NextDueUtc[key] = due;
                    s.MarkEngine();
                }
                if (due <= now) jobs.Add(new Job(key, kind, target, priority));
            }

            foreach (var a in s.Accounts.Values.Where(a => a.IsConnected && a.Connection != ConnectionState.NeedsReauth))
            {
                Consider(JobKind.Token, a.AccountId, 0);
                Consider(JobKind.Profile, a.AccountId, 0);
                Consider(JobKind.Limit, a.AccountId, 1);
                Consider(JobKind.Media, a.AccountId, 2);
                Consider(JobKind.Insights, a.AccountId, 3);
                Consider(JobKind.Comments, a.AccountId, 3);
                Consider(JobKind.Days, a.AccountId, 4);
                Consider(JobKind.Online, a.AccountId, 5);
                Consider(JobKind.Demographics, a.AccountId, 5);
            }
            void Global(string key, JobKind kind, TimeSpan firstIn, int priority)
            {
                if (!s.Engine.NextDueUtc.TryGetValue(key, out var due)) { due = now + firstIn; s.Engine.NextDueUtc[key] = due; s.MarkEngine(); }
                if (due <= now) jobs.Add(new Job(key, kind, "", priority));
            }
            Global("housekeeping", JobKind.Housekeeping, TimeSpan.FromMinutes(10), 6);
            if (SelfCheckHttp != null) Global("public-url", JobKind.PublicUrl, TimeSpan.FromMinutes(2), 1);

            // Forget schedules of removed accounts.
            foreach (string stale in s.Engine.NextDueUtc.Keys.Where(k => !live.Contains(k)).ToList())
            {
                s.Engine.NextDueUtc.Remove(stale);
                s.MarkEngine();
            }
            return jobs.OrderBy(j => j.Priority).ToList();
        });

        public DateTime? NextDueUtc() => store.Read(s => s.Engine.NextDueUtc.Count == 0 ? (DateTime?)null : s.Engine.NextDueUtc.Values.Min());

        private void Reschedule(string key, DateTime dueUtc)
        {
            store.Mutate(s =>
            {
                s.Engine.NextDueUtc[key] = dueUtc;
                s.MarkEngine();
            });
        }

        private async Task RunJobAsync(Job job, CancellationToken ct)
        {
            DateTime now = clock();
            TimeSpan next;
            try
            {
                next = job.Kind switch
                {
                    JobKind.Profile => await SyncProfileAsync(job.Target, ct),
                    JobKind.Token => await SyncTokenAsync(job.Target, ct),
                    JobKind.Limit => await SyncLimitAsync(job.Target, ct),
                    JobKind.Media => await SyncMediaAsync(job.Target, ct),
                    JobKind.Insights => await SyncPostInsightsAsync(job.Target, ct),
                    JobKind.Days => await SyncAccountDaysAsync(job.Target, ct),
                    JobKind.Online => await SyncOnlineFollowersAsync(job.Target, ct),
                    JobKind.Demographics => await SyncDemographicsAsync(job.Target, ct),
                    JobKind.Comments => await SyncCommentsAsync(job.Target, ct),
                    JobKind.PublicUrl => await CheckPublicUrlAsync(ct),
                    _ => await HousekeepingAsync(ct),
                };
                lastErrors.TryRemove(job.Key, out _);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (AccountUnavailableException)
            {
                next = TimeSpan.FromMinutes(30);
            }
            catch (InstagramApiException ex) when (ex.Kind == IgErrorKind.TokenInvalid)
            {
                auth.MarkNeedsReauth(job.Target, ex.Message);
                next = TimeSpan.FromMinutes(30);
            }
            catch (InstagramApiException ex) when (ex.Kind == IgErrorKind.PermissionDenied)
            {
                // A missing optional permission (insights, comments) must not break publishing: note it and wait.
                RecordError(job.Key, "Permission missing — reconnect the account and allow it: " + ex.Message);
                next = TimeSpan.FromHours(24);
            }
            catch (InstagramApiException ex) when (ex.Kind == IgErrorKind.AccountRestricted)
            {
                auth.MarkRestricted(job.Target, ex.Message);
                next = TimeSpan.FromHours(1);
            }
            catch (InstagramApiException ex) when (ex.Kind == IgErrorKind.RateLimited)
            {
                next = ex.RetryAfterUtc is DateTime at && at > now ? at - now : TimeSpan.FromMinutes(30);
                RecordError(job.Key, ex.Message);
            }
            catch (Exception ex)
            {
                next = TimeSpan.FromMinutes(20);
                RecordError(job.Key, ex.Message);
            }
            Reschedule(job.Key, clock() + next);
        }

        private void RecordError(string key, string message)
        {
            bool isNew = !lastErrors.TryGetValue(key, out var previous) || previous != message;
            lastErrors[key] = message;
            if (isNew) log($"OmniGram sync {key}: {message}");
        }

        // ─────────────────────────────── Account jobs ───────────────────────────────

        private async Task<TimeSpan> SyncProfileAsync(string accountId, CancellationToken ct)
        {
            await auth.RefreshProfileAsync(accountId, ct);
            return ProfileEvery;
        }

        private async Task<TimeSpan> SyncTokenAsync(string accountId, CancellationToken ct)
        {
            await auth.RefreshTokenIfDueAsync(accountId, force: false, ct);
            return TokenEvery;
        }

        private async Task<TimeSpan> SyncLimitAsync(string accountId, CancellationToken ct)
        {
            var (token, igUserId) = auth.GetCredentials(accountId);
            var limit = await api.GetPublishingLimitAsync(token, igUserId, ct);
            DateTime now = clock();
            store.Mutate(s =>
            {
                var a = s.Account(accountId);
                if (a == null) return;
                a.Quota.QuotaUsage = limit.QuotaUsage;
                a.Quota.QuotaTotal = limit.QuotaTotal ?? a.Quota.QuotaTotal;
                a.Quota.QuotaDurationSeconds = limit.QuotaDurationSeconds ?? a.Quota.QuotaDurationSeconds;
                a.Quota.FetchedUtc = now;
                s.MarkAccount(accountId);
            });
            return LimitEvery;
        }

        /// <summary>
        /// Reads the account's newest media (all of it on the first run, up to 250 items) into the index, copies
        /// like/comment counts and permalinks onto our posts, and notices published posts that disappeared.
        /// </summary>
        private async Task<TimeSpan> SyncMediaAsync(string accountId, CancellationToken ct)
        {
            var (token, igUserId) = auth.GetCredentials(accountId);
            bool firstSync = store.Read(s => s.InsightsOf(accountId).Index.Count == 0);
            int maxPages = firstSync ? 5 : 2;
            var items = new List<IgMediaItem>();
            string? after = null;
            bool reachedEnd = false;
            for (int page = 0; page < maxPages; page++)
            {
                var result = await api.GetMediaPageAsync(token, igUserId, after, 50, ct);
                items.AddRange(result.Items);
                after = result.After;
                if (after == null || result.Items.Count == 0) { reachedEnd = true; break; }
            }
            DateTime now = clock();
            bool active = store.Mutate(s =>
            {
                var account = s.Account(accountId);
                if (account == null) return false;
                var ours = s.PostsOf(accountId).Where(p => p.Status == PostStatus.Published && p.Format != PostFormat.Story).ToList();
                var byId = ours.Where(p => p.IgMediaId != null).GroupBy(p => p.IgMediaId!).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

                OmniGramInsights.MergeIndex(s.InsightsOf(accountId), items, m => byId.TryGetValue(m.Id, out var p) ? p.PostId : null, now);
                foreach (var item in items)
                {
                    if (!byId.TryGetValue(item.Id, out var post)) continue;
                    post.Permalink = item.Permalink ?? post.Permalink;
                    post.Shortcode = item.Shortcode ?? post.Shortcode;
                    post.MediaProductType = item.MediaProductType ?? post.MediaProductType;
                    post.Metrics.Likes = item.LikeCount ?? post.Metrics.Likes;
                    post.Metrics.Comments = item.CommentsCount ?? post.Metrics.Comments;
                    post.Metrics.CountsSyncedUtc = now;
                    RecordHistory(post, now);
                    s.Touch(post, now);
                }

                // Gone from Instagram: only judged within the window this listing covered.
                DateTime coveredSince = reachedEnd ? DateTime.MinValue : items.Count > 0 ? items.Min(i => i.TimestampUtc) : DateTime.MaxValue;
                var listed = items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var post in ours.Where(p => p.IgMediaId != null && p.PublishedUtc >= coveredSince && p.PublishedUtc < now.AddHours(-1)))
                {
                    if (listed.Contains(post.IgMediaId!)) continue;
                    post.Status = PostStatus.Removed;
                    post.LastError = "The post is no longer on Instagram (deleted in the app or removed by Instagram).";
                    s.Touch(post, now);
                    s.AddEvent(EventLevel.Warning, "post.removed", $"@{account.Username}: a published post is gone from Instagram — {post.Permalink}", accountId, post.PostId, now);
                }

                account.Stats.MediaIndexSyncedUtc = now;
                s.MarkAccount(accountId);
                s.MarkInsights(accountId);
                return ours.Any(p => p.PublishedUtc > now.AddDays(-7));
            });
            return active ? MediaActiveEvery : MediaIdleEvery;
        }

        /// <summary>
        /// Reads insights for posts that crossed a milestone since their last reading (ours first, then other
        /// recent media on the account), up to a dozen a run. Metrics Instagram refuses for a media type are
        /// learned and not asked for again.
        /// </summary>
        private async Task<TimeSpan> SyncPostInsightsAsync(string accountId, CancellationToken ct)
        {
            var (token, _) = auth.GetCredentials(accountId);
            DateTime now = clock();
            var wanted = store.Read(s =>
            {
                var ours = s.PostsOf(accountId)
                    .Where(p => p.Status == PostStatus.Published && p.IgMediaId != null && p.PublishedUtc.HasValue && NeedsInsights(p, now))
                    .OrderBy(p => p.Metrics.InsightsSyncedUtc ?? DateTime.MinValue)
                    .Take(10)
                    .Select(p => (MediaId: p.IgMediaId!, PostId: (string?)p.PostId, Product: p.MediaProductType ?? (p.Format == PostFormat.Story ? "STORY" : p.Format == PostFormat.Reel ? "REELS" : "FEED")))
                    .ToList();
                var oursIds = ours.Select(o => o.MediaId).ToHashSet(StringComparer.Ordinal);
                var others = s.InsightsOf(accountId).Index
                    .Where(m => m.OmniPostId == null && !oursIds.Contains(m.Id) && m.PublishedUtc > now.AddDays(-30)
                        && (m.InsightsSyncedUtc == null || now - m.InsightsSyncedUtc > TimeSpan.FromDays(1)))
                    .OrderBy(m => m.InsightsSyncedUtc ?? DateTime.MinValue)
                    .Take(Math.Max(0, 12 - ours.Count))
                    .Select(m => (MediaId: m.Id, PostId: (string?)null, Product: m.ProductType ?? "FEED"))
                    .ToList();
                return ours.Concat(others).ToList();
            });

            foreach (var (mediaId, postId, product) in wanted)
            {
                if (ct.IsCancellationRequested) break;
                IgInsightValues values;
                try
                {
                    values = await ReadMediaInsightsAsync(token, mediaId, product, ct);
                }
                catch (InstagramApiException ex) when (ex.Kind is IgErrorKind.NotFound or IgErrorKind.BadRequest)
                {
                    // Media made before the account went professional, or deleted: remember the attempt and move on.
                    store.Mutate(s =>
                    {
                        if (postId != null && s.FindPost(postId) is { } p) { p.Metrics.InsightsSyncedUtc = now; p.Metrics.InsightsError = ex.Message; s.Touch(p, now); }
                        var entry = s.InsightsOf(accountId).Index.FirstOrDefault(m => m.Id == mediaId);
                        if (entry != null) { entry.InsightsSyncedUtc = now; s.MarkInsights(accountId); }
                    });
                    continue;
                }
                now = clock();
                store.Mutate(s =>
                {
                    if (postId != null && s.FindPost(postId) is { } post)
                    {
                        ApplyInsights(post, values, now);
                        s.Touch(post, now);
                    }
                    var entry = s.InsightsOf(accountId).Index.FirstOrDefault(m => m.Id == mediaId);
                    if (entry != null)
                    {
                        OmniGramInsights.ApplyMediaInsights(entry, values, now);
                        s.MarkInsights(accountId);
                    }
                    var account = s.Account(accountId);
                    if (account != null) { account.Stats.PostInsightsSyncedUtc = now; s.MarkAccount(accountId); }
                });
            }
            return InsightsEvery;
        }

        internal static bool NeedsInsights(OmniGramPost post, DateTime now)
        {
            var age = now - post.PublishedUtc!.Value;
            var milestones = post.Format == PostFormat.Story ? StoryMilestones : FeedMilestones;
            if (post.Format == PostFormat.Story && age > TimeSpan.FromHours(23)) return false; // story insights vanish after a day
            if (age < milestones[0] || age > TimeSpan.FromDays(45)) return false;
            if (post.Metrics.InsightsSyncedUtc is not DateTime last) return true;
            var lastAge = last - post.PublishedUtc.Value;
            return milestones.Any(m => age >= m && lastAge < m);
        }

        private async Task<IgInsightValues> ReadMediaInsightsAsync(string token, string mediaId, string product, CancellationToken ct)
        {
            string productKey = product.ToUpperInvariant() is "REELS" or "CLIPS" ? "REELS" : product.ToUpperInvariant() == "STORY" ? "STORY" : "FEED";
            var all = productKey switch { "REELS" => ReelMetrics, "STORY" => StoryMetrics, _ => FeedMetrics };
            for (int attempt = 0; attempt < 4; attempt++)
            {
                var refused = store.Read(s => s.Engine.UnsupportedMetrics.TryGetValue(productKey, out var list) ? list.ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                var metrics = all.Where(m => !refused.Contains(m)).ToList();
                if (metrics.Count == 0) return new IgInsightValues();
                try
                {
                    return await api.GetMediaInsightsAsync(token, mediaId, metrics, ct);
                }
                catch (InstagramApiException ex) when (ex.Kind == IgErrorKind.BadRequest && InstagramGraphClient.UnsupportedMetricsIn(ex.Message, metrics) is { Count: > 0 } bad)
                {
                    store.Mutate(s =>
                    {
                        if (!s.Engine.UnsupportedMetrics.TryGetValue(productKey, out var list)) s.Engine.UnsupportedMetrics[productKey] = list = new List<string>();
                        foreach (string m in bad) if (!list.Contains(m, StringComparer.OrdinalIgnoreCase)) list.Add(m);
                        s.MarkEngine();
                    });
                    log($"OmniGram: Instagram does not offer {string.Join(", ", bad)} for {productKey} media; no longer asking for {(bad.Count == 1 ? "it" : "them")}.");
                }
            }
            throw new InstagramApiException(IgErrorKind.BadRequest, 400, 100, null, "Instagram refused every insights metric for this media.");
        }

        /// <summary>Copies an insights reading onto a post: totals, the 24 h/7 d milestones, and a thinned history.</summary>
        internal static void ApplyInsights(OmniGramPost post, IgInsightValues v, DateTime now)
        {
            var m = post.Metrics;
            m.Views = v.Long("views") ?? m.Views;
            m.Reach = v.Long("reach") ?? m.Reach;
            // Insights lag the media list's live counts by up to 48 h: the larger number is the newer one.
            m.Likes = Max(m.Likes, v.Long("likes"));
            m.Comments = Max(m.Comments, v.Long("comments"));
            m.Shares = v.Long("shares") ?? m.Shares;
            m.Saves = v.Long("saved") ?? m.Saves;
            m.Reposts = v.Long("reposts") ?? m.Reposts;
            m.TotalInteractions = v.Long("total_interactions") ?? m.TotalInteractions;
            m.Follows = v.Long("follows") ?? m.Follows;
            m.ProfileVisits = v.Long("profile_visits") ?? m.ProfileVisits;
            m.Replies = v.Long("replies") ?? m.Replies;
            m.AvgWatchTimeMs = v.Long("ig_reels_avg_watch_time") ?? m.AvgWatchTimeMs;
            m.TotalWatchTimeMs = v.Long("ig_reels_video_view_total_time") ?? m.TotalWatchTimeMs;
            m.SkipRate = v.Double("reels_skip_rate") ?? m.SkipRate;
            m.InsightsSyncedUtc = now;
            m.InsightsError = null;
            if (post.PublishedUtc is DateTime published)
            {
                var age = now - published;
                if (age >= TimeSpan.FromHours(24) && m.ViewsAt24h == null) { m.ViewsAt24h = m.Views; m.InteractionsAt24h = m.Interactions; }
                if (age >= TimeSpan.FromDays(7) && m.ViewsAt7d == null) { m.ViewsAt7d = m.Views; m.InteractionsAt7d = m.Interactions; }
            }
            RecordHistory(post, now);
        }

        internal static long? Max(long? a, long? b) => a == null ? b : b == null ? a : Math.Max(a.Value, b.Value);

        internal static void RecordHistory(OmniGramPost post, DateTime now)
        {
            var m = post.Metrics;
            var last = m.History.LastOrDefault();
            bool changed = last == null || last.Views != m.Views || last.Likes != m.Likes || last.Comments != m.Comments;
            if (last == null || (changed && now - last.Utc >= TimeSpan.FromMinutes(30)) || now - last.Utc >= TimeSpan.FromHours(12))
            {
                m.History.Add(new MetricPoint { Utc = now, Views = m.Views, Reach = m.Reach, Likes = m.Likes, Comments = m.Comments, Interactions = m.Interactions });
                if (m.History.Count > 80)
                {
                    // Keep the first readings (the launch curve) and thin the rest.
                    m.History = m.History.Take(30).Concat(m.History.Skip(30).Where((_, i) => i % 2 == 0)).ToList();
                }
            }
        }

        /// <summary>
        /// Daily account insights (reach, views, interactions, follows…) for the last 30 days: missing days are
        /// back-filled a few at a time, and the last two days are always re-read because Instagram finalises
        /// them late. Days are Instagram's (Pacific time).
        /// </summary>
        private async Task<TimeSpan> SyncAccountDaysAsync(string accountId, CancellationToken ct)
        {
            var (token, igUserId) = auth.GetCredentials(accountId);
            var pt = OmniGramScheduleMath.ResolveTimeZone(OmniGramScheduleMath.InsightsTimeZone);
            DateTime now = clock();
            DateTime today = OmniGramScheduleMath.UtcToLocal(now, pt).Date;
            var known = store.Read(s => s.InsightsOf(accountId).Days.Where(kv => kv.Value.SyncedUtc != null).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal));
            var days = new List<DateTime> { today.AddDays(-1), today };
            for (int i = 2; i <= 29 && days.Count < 8; i++)
            {
                var d = today.AddDays(-i);
                if (!known.Contains(d.ToString("yyyy-MM-dd"))) days.Add(d);
            }

            foreach (var day in days)
            {
                if (ct.IsCancellationRequested) break;
                DateTime since = OmniGramScheduleMath.LocalToUtc(day, pt);
                DateTime until = OmniGramScheduleMath.LocalToUtc(day.AddDays(1), pt);
                if (until > now) until = now;
                if (until <= since) continue;
                var totals = await ReadAccountTotalsAsync(token, igUserId, since, until, ct);
                IgInsightValues? follows = null;
                try { follows = await api.GetAccountTotalsAsync(token, igUserId, new[] { "follows_and_unfollows" }, since, until, "follow_type", ct); }
                catch (InstagramApiException ex) when (ex.Kind is IgErrorKind.BadRequest or IgErrorKind.NotFound) { /* under 100 followers, or unavailable */ }
                string key = day.ToString("yyyy-MM-dd");
                store.Mutate(s =>
                {
                    OmniGramInsights.MergeDay(s.InsightsOf(accountId), key, totals, follows, clock());
                    s.MarkInsights(accountId);
                });
            }
            store.Mutate(s =>
            {
                var a = s.Account(accountId);
                if (a != null) { a.Stats.AccountInsightsSyncedUtc = clock(); s.MarkAccount(accountId); }
            });
            return DaysEvery;
        }

        private async Task<IgInsightValues> ReadAccountTotalsAsync(string token, string igUserId, DateTime since, DateTime until, CancellationToken ct)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                var refused = store.Read(s => s.Engine.UnsupportedMetrics.TryGetValue("ACCOUNT", out var list) ? list.ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                var metrics = AccountMetrics.Where(m => !refused.Contains(m)).ToList();
                try
                {
                    return await api.GetAccountTotalsAsync(token, igUserId, metrics, since, until, null, ct);
                }
                catch (InstagramApiException ex) when (ex.Kind == IgErrorKind.BadRequest && InstagramGraphClient.UnsupportedMetricsIn(ex.Message, metrics) is { Count: > 0 } bad)
                {
                    store.Mutate(s =>
                    {
                        if (!s.Engine.UnsupportedMetrics.TryGetValue("ACCOUNT", out var list)) s.Engine.UnsupportedMetrics["ACCOUNT"] = list = new List<string>();
                        foreach (string m in bad) if (!list.Contains(m, StringComparer.OrdinalIgnoreCase)) list.Add(m);
                        s.MarkEngine();
                    });
                }
            }
            return new IgInsightValues();
        }

        /// <summary>When followers are online, averaged per hour over the last week (needs 100+ followers).</summary>
        private async Task<TimeSpan> SyncOnlineFollowersAsync(string accountId, CancellationToken ct)
        {
            var (token, igUserId) = auth.GetCredentials(accountId);
            DateTime now = clock();
            List<(DateTime EndTimeUtc, Dictionary<int, double> ByHour)> rows;
            try
            {
                rows = await api.GetOnlineFollowersAsync(token, igUserId, now.AddDays(-7), now, ct);
            }
            catch (InstagramApiException ex) when (ex.Kind is IgErrorKind.BadRequest or IgErrorKind.NotFound)
            {
                return OnlineEvery; // fewer than 100 followers
            }
            if (rows.Count == 0) return OnlineEvery;
            var average = Enumerable.Range(0, 24).ToDictionary(h => h, h =>
            {
                var values = rows.Where(r => r.ByHour.ContainsKey(h)).Select(r => r.ByHour[h]).ToList();
                return values.Count == 0 ? 0 : Math.Round(values.Average(), 1);
            });
            store.Mutate(s =>
            {
                var insights = s.InsightsOf(accountId);
                insights.OnlineFollowersByHour = average;
                insights.OnlineFollowersSyncedUtc = now;
                s.MarkInsights(accountId);
            });
            return OnlineEvery;
        }

        private async Task<TimeSpan> SyncDemographicsAsync(string accountId, CancellationToken ct)
        {
            var (token, igUserId) = auth.GetCredentials(accountId);
            var snapshot = new DemographicsSnapshot { Utc = clock() };
            foreach (string breakdown in new[] { "country", "city", "age", "gender" })
            {
                try
                {
                    var values = await api.GetFollowerDemographicsAsync(token, igUserId, breakdown, ct);
                    if (values.Breakdowns.TryGetValue("follower_demographics", out var map))
                        snapshot.Followers[breakdown] = map.ToDictionary(kv => kv.Key, kv => (long)kv.Value, StringComparer.OrdinalIgnoreCase);
                }
                catch (InstagramApiException ex) when (ex.Kind is IgErrorKind.BadRequest or IgErrorKind.NotFound)
                {
                    snapshot.Error = "Instagram shares audience demographics once an account has 100+ followers.";
                    break;
                }
            }
            store.Mutate(s =>
            {
                s.InsightsOf(accountId).Demographics = snapshot;
                s.MarkInsights(accountId);
                var a = s.Account(accountId);
                if (a != null) { a.Stats.DemographicsSyncedUtc = snapshot.Utc; s.MarkAccount(accountId); }
            });
            return DemographicsEvery;
        }

        /// <summary>Comments on recent posts (ours from the last 14 days, others from the last 7) whose count changed.</summary>
        private async Task<TimeSpan> SyncCommentsAsync(string accountId, CancellationToken ct)
        {
            var (token, _) = auth.GetCredentials(accountId);
            DateTime now = clock();
            var targets = store.Read(s =>
            {
                var insights = s.InsightsOf(accountId);
                var known = insights.Comments.GroupBy(c => c.MediaId).ToDictionary(g => g.Key, g => g.Count(c => c.ParentId == null), StringComparer.Ordinal);
                return insights.Index
                    .Where(m => m.PublishedUtc > now.AddDays(m.OmniPostId != null ? -14 : -7) && (m.Comments ?? 0) > 0)
                    .Where(m => !known.TryGetValue(m.Id, out var count) || count < (m.Comments ?? 0))
                    .OrderByDescending(m => m.PublishedUtc)
                    .Take(8)
                    .Select(m => (m.Id, m.OmniPostId))
                    .ToList();
            });
            string username = store.Read(s => s.Account(accountId)?.Username ?? "");
            int fresh = 0;
            foreach (var (mediaId, omniPostId) in targets)
            {
                if (ct.IsCancellationRequested) break;
                var comments = await api.GetCommentsAsync(token, mediaId, 50, ct);
                fresh += store.Mutate(s =>
                {
                    int added = OmniGramInsights.MergeComments(s.InsightsOf(accountId), mediaId, omniPostId, comments, username);
                    s.MarkInsights(accountId);
                    return added;
                });
            }
            store.Mutate(s =>
            {
                var a = s.Account(accountId);
                if (a == null) return;
                a.Stats.CommentsSyncedUtc = clock();
                s.MarkAccount(accountId);
                if (fresh > 0) s.AddEvent(EventLevel.Info, "comments.new", $"@{a.Username}: {fresh} new comment{(fresh == 1 ? "" : "s")}.", accountId, utc: clock());
            });
            return CommentsEvery;
        }

        // ─────────────────────────────── Global jobs ───────────────────────────────

        private async Task<TimeSpan> CheckPublicUrlAsync(CancellationToken ct)
        {
            if (SelfCheckHttp == null) return PublicUrlEvery;
            var check = await mediaHost.CheckAsync(SelfCheckHttp, ct);
            if (!check.Ok) RecordError("public-url", check.Detail ?? "The public media URL check failed.");
            return check.Ok ? PublicUrlEvery : TimeSpan.FromMinutes(30);
        }

        private Task<TimeSpan> HousekeepingAsync(CancellationToken ct)
        {
            auth.ExpireFlows();
            DateTime now = clock();
            // Prepared copies stay while their post may still be sent (or re-sent); thumbnails while the post exists.
            var keepPrepared = store.Read(s => new HashSet<string>(
                s.AllPosts().Where(p => !p.IsTerminal || p.UpdatedUtc > now.AddDays(-2))
                    .SelectMany(p => p.Media.Select(m => m.PreparedPath)).Where(p => p != null).Select(p => Path.GetFullPath(p!)),
                StringComparer.OrdinalIgnoreCase));
            var keepOther = store.Read(s => new HashSet<string>(
                s.AllPosts().SelectMany(p => p.Media.SelectMany(m => new[] { m.Path, m.ThumbnailPath }))
                    .Where(p => p != null).Select(p => Path.GetFullPath(p!)),
                StringComparer.OrdinalIgnoreCase));
            int removed = 0;
            removed += DeleteUnreferenced(store.PreparedDirectory, keepPrepared, TimeSpan.FromHours(6), now);
            removed += DeleteUnreferenced(store.ThumbnailsDirectory, keepOther, TimeSpan.FromDays(2), now);
            removed += DeleteUnreferenced(store.UploadsDirectory, keepOther, TimeSpan.FromDays(14), now);
            removed += DeleteUnreferenced(store.CacheDirectory, new HashSet<string>(), TimeSpan.FromDays(3), now);
            if (removed > 0) log($"OmniGram housekeeping removed {removed} unreferenced media file(s).");
            return Task.FromResult(HousekeepingEvery);
        }

        private static int DeleteUnreferenced(string directory, HashSet<string> referenced, TimeSpan minAge, DateTime now)
        {
            if (!Directory.Exists(directory)) return 0;
            int removed = 0;
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    if (referenced.Contains(file.FullName)) continue;
                    if (now - file.LastWriteTimeUtc < minAge) continue;
                    file.Delete();
                    removed++;
                }
                catch { }
            }
            return removed;
        }
    }
}
