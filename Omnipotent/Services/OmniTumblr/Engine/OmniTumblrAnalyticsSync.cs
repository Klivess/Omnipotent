using Omnipotent.Services.OmniTumblr.Api;
using Omnipotent.Services.OmniTumblr.Models;
using System.Collections.Concurrent;

namespace Omnipotent.Services.OmniTumblr.Engine
{
    /// <summary>
    /// Counts our Tumblr API calls per hour/day and remembers the last rate-limit headers, so background
    /// sync backs off long before the per-app limits (1,000/hour, 5,000/day) could block publishing.
    /// </summary>
    internal sealed class OmniTumblrApiBudget
    {
        public const int ConsumerHourlyLimit = 1000;
        public const int ConsumerDailyLimit = 5000;
        private readonly ConcurrentDictionary<string, int> perHour = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> perEndpointToday = new(StringComparer.Ordinal);
        private readonly Func<DateTime> clock;
        private string endpointDay = "";

        public OmniTumblrApiBudget(Func<DateTime> clock) => this.clock = clock;

        public TumblrRateLimitHeaders? LastHeaders { get; private set; }

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

        public void Observe(TumblrRateLimitHeaders headers) => LastHeaders = headers;

        public int CallsThisHour() => perHour.TryGetValue(clock().ToString("yyyyMMddHH"), out var v) ? v : 0;

        public int CallsToday()
        {
            string day = clock().ToString("yyyyMMdd");
            return perHour.Where(kv => kv.Key.StartsWith(day, StringComparison.Ordinal)).Sum(kv => kv.Value);
        }

        public IReadOnlyDictionary<string, int> CallsByEndpointToday() => new Dictionary<string, int>(perEndpointToday);

        /// <summary>Background analytics may run: leaves ample headroom for publishing and the website.</summary>
        public bool AllowBackground()
        {
            if (CallsThisHour() > ConsumerHourlyLimit * 6 / 10 || CallsToday() > ConsumerDailyLimit * 7 / 10) return false;
            var h = LastHeaders;
            if (h != null && clock() - h.ObservedUtc < TimeSpan.FromHours(1))
            {
                if (h.PerHourRemaining is long hr && hr < 150) return false;
                if (h.PerDayRemaining is long dr && dr < 600) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Keeps analytics fresh with a small job scheduler persisted in engine.json: per connection
    /// (/user/info for follower counts, /user/limits), per blog (/info, the post index with note counts,
    /// note breakdowns at 1d/3d/7d/30d, and the activity feed). Jobs are staggered, budgeted and
    /// independent — one failing blog or endpoint never stalls the rest.
    /// </summary>
    internal sealed class OmniTumblrAnalyticsSync
    {
        private static readonly TimeSpan UserInfoEvery = TimeSpan.FromHours(3);
        private static readonly TimeSpan LimitsEvery = TimeSpan.FromHours(1);
        private static readonly TimeSpan BlogInfoEvery = TimeSpan.FromHours(6);
        private static readonly TimeSpan PostsActiveEvery = TimeSpan.FromHours(1);
        private static readonly TimeSpan PostsIdleEvery = TimeSpan.FromHours(6);
        private static readonly TimeSpan NotesEvery = TimeSpan.FromHours(2);
        private static readonly TimeSpan ActivityEvery = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan HousekeepingEvery = TimeSpan.FromHours(24);
        private static readonly TimeSpan[] BreakdownMilestones = { TimeSpan.FromDays(1), TimeSpan.FromDays(3), TimeSpan.FromDays(7), TimeSpan.FromDays(30) };

        private readonly OmniTumblrStore store;
        private readonly ITumblrApi api;
        private readonly OmniTumblrAuth auth;
        private readonly OmniTumblrApiBudget budget;
        private readonly Func<DateTime> clock;
        private readonly Action<string> log;
        private readonly ConcurrentDictionary<string, string> lastErrors = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim runLock = new(1, 1);

        public OmniTumblrAnalyticsSync(OmniTumblrStore store, ITumblrApi api, OmniTumblrAuth auth, OmniTumblrApiBudget budget, Func<DateTime> clock, Action<string> log)
        {
            this.store = store;
            this.api = api;
            this.auth = auth;
            this.budget = budget;
            this.clock = clock;
            this.log = log;
        }

        public IReadOnlyDictionary<string, string> LastErrors => lastErrors;

        private enum JobKind { UserInfo, Limits, BlogInfo, Posts, Notes, Activity, Housekeeping }

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

        /// <summary>Refreshes one blog now (info, posts, notes, activity) — the "refresh" button.</summary>
        public async Task SyncBlogNowAsync(string blogId, CancellationToken ct)
        {
            string? connectionId = store.Read(s => s.Blog(blogId)?.ConnectionId);
            if (connectionId != null) await RunJobAsync(new Job("user:" + connectionId, JobKind.UserInfo, connectionId, 0), ct);
            foreach (var kind in new[] { JobKind.BlogInfo, JobKind.Posts, JobKind.Notes, JobKind.Activity })
                await RunJobAsync(new Job(JobKey(kind, blogId), kind, blogId, 1), ct);
        }

        /// <summary>Makes a blog's or connection's jobs due immediately (after adding a blog, reconnecting…).</summary>
        public void ExpediteBlog(string blogId)
        {
            store.Mutate(s =>
            {
                foreach (var kind in new[] { JobKind.BlogInfo, JobKind.Posts, JobKind.Activity })
                    s.Engine.NextDueUtc[JobKey(kind, blogId)] = DateTime.MinValue;
                var conn = s.Blog(blogId)?.ConnectionId;
                if (conn != null) s.Engine.NextDueUtc["user:" + conn] = DateTime.MinValue;
                s.MarkEngine();
            });
        }

        public DateTime? NextDueUtc() => store.Read(s => s.Engine.NextDueUtc.Count == 0 ? (DateTime?)null : s.Engine.NextDueUtc.Values.Min());

        private static string JobKey(JobKind kind, string target) => kind switch
        {
            JobKind.UserInfo => "user:" + target,
            JobKind.Limits => "limits:" + target,
            JobKind.BlogInfo => "info:" + target,
            JobKind.Posts => "posts:" + target,
            JobKind.Notes => "notes:" + target,
            JobKind.Activity => "activity:" + target,
            _ => "housekeeping",
        };

        private List<Job> DueJobs(DateTime now) => store.Mutate(s =>
        {
            var jobs = new List<Job>();
            var live = new HashSet<string>(StringComparer.Ordinal) { "housekeeping" };
            void Consider(JobKind kind, string target, int priority)
            {
                string key = JobKey(kind, target);
                live.Add(key);
                if (!s.Engine.NextDueUtc.TryGetValue(key, out var due))
                {
                    // New job: stagger first runs over a few minutes so a restart doesn't burst the API.
                    due = now.AddSeconds(30 + OmniTumblrScheduleMath.Fnv1a(key) % 240);
                    s.Engine.NextDueUtc[key] = due;
                    s.MarkEngine();
                }
                if (due <= now) jobs.Add(new Job(key, kind, target, priority));
            }

            foreach (var conn in s.Connections.Values.Where(c => c.Health != ConnectionHealth.NeedsReauth))
            {
                Consider(JobKind.UserInfo, conn.ConnectionId, 0);
                Consider(JobKind.Limits, conn.ConnectionId, 1);
            }
            foreach (var blog in s.Blogs.Values)
            {
                var conn = s.Connection(blog.ConnectionId);
                if (conn == null || conn.Health == ConnectionHealth.NeedsReauth) continue;
                Consider(JobKind.Posts, blog.BlogId, 2);
                Consider(JobKind.BlogInfo, blog.BlogId, 3);
                Consider(JobKind.Activity, blog.BlogId, 3);
                Consider(JobKind.Notes, blog.BlogId, 4);
            }
            if (!s.Engine.NextDueUtc.TryGetValue("housekeeping", out var hk)) { hk = now.AddMinutes(10); s.Engine.NextDueUtc["housekeeping"] = hk; s.MarkEngine(); }
            if (hk <= now) jobs.Add(new Job("housekeeping", JobKind.Housekeeping, "", 5));

            // Forget schedules of removed blogs/connections.
            foreach (string stale in s.Engine.NextDueUtc.Keys.Where(k => !live.Contains(k)).ToList())
            {
                s.Engine.NextDueUtc.Remove(stale);
                s.MarkEngine();
            }
            return jobs.OrderBy(j => j.Priority).ToList();
        });

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
                    JobKind.UserInfo => await SyncUserInfoAsync(job.Target, ct),
                    JobKind.Limits => await SyncLimitsAsync(job.Target, ct),
                    JobKind.BlogInfo => await SyncBlogInfoAsync(job.Target, ct),
                    JobKind.Posts => await SyncPostsAsync(job.Target, ct),
                    JobKind.Notes => await SyncNotesAsync(job.Target, ct),
                    JobKind.Activity => await SyncActivityAsync(job.Target, ct),
                    _ => await HousekeepingAsync(ct),
                };
                lastErrors.TryRemove(job.Key, out _);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (ConnectionUnavailableException)
            {
                next = TimeSpan.FromMinutes(30);
            }
            catch (TumblrApiException ex) when (ex.Kind == TumblrErrorKind.Unauthorized)
            {
                string? connectionId = job.Kind is JobKind.UserInfo or JobKind.Limits ? job.Target : store.Read(s => s.Blog(job.Target)?.ConnectionId);
                if (connectionId != null) auth.MarkNeedsReauth(connectionId, ex.Message);
                next = TimeSpan.FromMinutes(30);
            }
            catch (TumblrApiException ex) when (ex.Kind == TumblrErrorKind.RateLimited)
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
            if (isNew) log($"OmniTumblr sync {key}: {message}");
        }

        // ─────────────────────────────── Jobs ───────────────────────────────

        private async Task<TimeSpan> SyncUserInfoAsync(string connectionId, CancellationToken ct)
        {
            await auth.RefreshUserInfoAsync(connectionId, ct);
            return UserInfoEvery;
        }

        private async Task<TimeSpan> SyncLimitsAsync(string connectionId, CancellationToken ct)
        {
            var creds = await auth.GetCredentialsAsync(connectionId, ct);
            var limits = await api.GetUserLimitsAsync(creds, ct);
            DateTime now = clock();
            store.Mutate(s =>
            {
                var conn = s.Connection(connectionId);
                if (conn == null) return;
                conn.Limits = limits;
                conn.LimitsFetchedUtc = now;
                s.MarkConnections();
            });
            return LimitsEvery;
        }

        private (string BlogIdentifier, string ConnectionId)? BlogTarget(string blogId) => store.Read(s =>
        {
            var blog = s.Blog(blogId);
            return blog == null ? ((string, string)?)null : (string.IsNullOrEmpty(blog.Uuid) ? blog.Name : blog.Uuid!, blog.ConnectionId);
        });

        private async Task<TimeSpan> SyncBlogInfoAsync(string blogId, CancellationToken ct)
        {
            var target = BlogTarget(blogId);
            if (target == null) return BlogInfoEvery;
            var creds = await auth.GetCredentialsAsync(target.Value.ConnectionId, ct);
            var info = await api.GetBlogInfoAsync(creds, target.Value.BlogIdentifier, ct);
            bool needFollowers = store.Read(s => s.Blog(blogId)?.Stats.FollowersSyncedUtc is not DateTime synced || clock() - synced > TimeSpan.FromHours(12));
            long? followers = null;
            if (needFollowers)
            {
                try { followers = await api.GetFollowerCountAsync(creds, target.Value.BlogIdentifier, ct); }
                catch (TumblrApiException ex) when (ex.Kind is TumblrErrorKind.Forbidden or TumblrErrorKind.NotFound) { }
            }
            DateTime now = clock();
            store.Mutate(s =>
            {
                var blog = s.Blog(blogId);
                if (blog == null) return;
                if (!string.IsNullOrEmpty(info.Uuid)) blog.Uuid = info.Uuid;
                if (!string.IsNullOrEmpty(info.Name) && !string.Equals(info.Name, blog.Name, StringComparison.OrdinalIgnoreCase))
                {
                    s.AddEvent(EventLevel.Info, "blog.renamed", $"@{blog.Name} is now @{info.Name} on Tumblr.", blogId, utc: now);
                    blog.Name = info.Name;
                }
                blog.Title = info.Title ?? blog.Title;
                blog.Description = info.Description ?? blog.Description;
                blog.Url = info.Url ?? blog.Url;
                blog.Stats.Posts = info.Posts ?? blog.Stats.Posts;
                blog.Stats.Likes = info.Likes ?? blog.Stats.Likes;
                blog.Stats.InfoSyncedUtc = now;
                if (followers.HasValue)
                {
                    blog.Stats.Followers = followers;
                    blog.Stats.FollowersSyncedUtc = now;
                }
                OmniTumblrInsights.RecordSnapshot(s.InsightsOf(blogId), now, followers ?? blog.Stats.Followers, blog.Stats.Posts);
                s.MarkInsights(blogId);
                s.MarkBlog(blogId);
            });
            return BlogInfoEvery;
        }

        private async Task<TimeSpan> SyncPostsAsync(string blogId, CancellationToken ct)
        {
            var target = BlogTarget(blogId);
            if (target == null) return PostsIdleEvery;
            var creds = await auth.GetCredentialsAsync(target.Value.ConnectionId, ct);
            bool firstSync = store.Read(s => s.InsightsOf(blogId).Index.Count == 0);
            int maxPages = firstSync ? 10 : 3;
            DateTime now = clock();
            var collected = new List<TumblrPostSummary>();
            long? total = null;
            for (int page = 0; page < maxPages; page++)
            {
                var result = await api.GetPostsAsync(creds, target.Value.BlogIdentifier, page * 20, 20, ct);
                total ??= result.TotalPosts;
                collected.AddRange(result.Posts);
                if (result.Posts.Count < 20) break;
                if (!firstSync && result.Posts.Min(p => p.PublishedUtc) < now.AddDays(-30)) break;
            }

            bool active = store.Mutate(s =>
            {
                var blog = s.Blog(blogId);
                if (blog == null) return false;
                var ours = s.PostsOf(blogId).Where(p => p.Status == PostStatus.Published && p.TumblrPostId != null).ToList();
                var byId = ours.GroupBy(p => p.TumblrPostId!).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                var bySlug = ours.Where(p => !string.IsNullOrEmpty(p.Slug)).GroupBy(p => p.Slug!).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                OmniTumblrPost? Match(TumblrPostSummary summary) =>
                    byId.TryGetValue(summary.Id, out var p) ? p
                    : summary.Slug != null && bySlug.TryGetValue(summary.Slug, out var q) ? q : null;

                OmniTumblrInsights.MergeIndex(s.InsightsOf(blogId), collected, summary => Match(summary)?.PostId);
                foreach (var summary in collected)
                {
                    var post = Match(summary);
                    if (post == null) continue;
                    if (post.TumblrPostId != summary.Id) post.TumblrPostId = summary.Id;
                    if (!string.IsNullOrEmpty(summary.PostUrl)) post.TumblrUrl = summary.PostUrl;
                    UpdateMetrics(post, summary.NoteCount, null, null, now);
                    s.Touch(post, now);
                }
                if (total.HasValue) blog.Stats.Posts = total;
                blog.Stats.PostsIndexSyncedUtc = now;
                s.MarkBlog(blogId);
                s.MarkInsights(blogId);
                return ours.Any(p => p.PublishedUtc > now.AddDays(-14));
            });
            return active ? PostsActiveEvery : PostsIdleEvery;
        }

        /// <summary>Records a metrics reading: totals, the 24h/7d milestones, and a thinned history.</summary>
        internal static void UpdateMetrics(OmniTumblrPost post, long notes, long? likes, long? reblogs, DateTime now)
        {
            var m = post.Metrics;
            m.Notes = Math.Max(notes, 0);
            if (likes.HasValue) m.Likes = likes;
            if (reblogs.HasValue) m.Reblogs = reblogs;
            if (likes.HasValue && reblogs.HasValue) m.Replies = Math.Max(0, m.Notes - likes.Value - reblogs.Value);
            m.SyncedUtc = now;
            if (post.PublishedUtc is DateTime published)
            {
                var age = now - published;
                if (age >= TimeSpan.FromHours(24) && m.NotesAt24h == null) m.NotesAt24h = m.Notes;
                if (age >= TimeSpan.FromDays(7) && m.NotesAt7d == null) m.NotesAt7d = m.Notes;
            }
            var last = m.History.LastOrDefault();
            if (last == null || (last.Notes != m.Notes && now - last.Utc >= TimeSpan.FromMinutes(30)) || now - last.Utc >= TimeSpan.FromHours(12))
            {
                m.History.Add(new MetricPoint { Utc = now, Notes = m.Notes, Likes = m.Likes, Reblogs = m.Reblogs });
                if (m.History.Count > 80)
                {
                    // Keep the first readings (the launch curve) and thin the rest.
                    var thinned = m.History.Take(30).Concat(m.History.Skip(30).Where((_, i) => i % 2 == 0)).ToList();
                    m.History = thinned;
                }
            }
        }

        private async Task<TimeSpan> SyncNotesAsync(string blogId, CancellationToken ct)
        {
            var target = BlogTarget(blogId);
            if (target == null) return NotesEvery;
            DateTime now = clock();
            var wanted = store.Read(s => s.PostsOf(blogId)
                .Where(p => p.Status == PostStatus.Published && p.TumblrPostId != null && p.PublishedUtc.HasValue)
                .Where(p => NeedsBreakdown(p, now))
                .OrderByDescending(p => p.PublishedUtc)
                .Take(6)
                .Select(p => (p.PostId, p.TumblrPostId!))
                .ToList());
            if (wanted.Count == 0) return NotesEvery;
            var creds = await auth.GetCredentialsAsync(target.Value.ConnectionId, ct);
            foreach (var (postId, tumblrId) in wanted)
            {
                TumblrNotesSummary notes;
                try { notes = await api.GetNotesSummaryAsync(creds, target.Value.BlogIdentifier, tumblrId, ct); }
                catch (TumblrApiException ex) when (ex.Kind == TumblrErrorKind.NotFound)
                {
                    MarkRemoved(postId, now);
                    continue;
                }
                store.Mutate(s =>
                {
                    var post = s.FindPost(postId);
                    if (post == null) return;
                    UpdateMetrics(post, Math.Max(post.Metrics.Notes, notes.TotalNotes), notes.TotalLikes, notes.TotalReblogs, now);
                    post.Metrics.BreakdownSyncedUtc = now;
                    s.Touch(post, now);
                });
            }
            return NotesEvery;
        }

        internal static bool NeedsBreakdown(OmniTumblrPost post, DateTime now)
        {
            var age = now - post.PublishedUtc!.Value;
            if (age < TimeSpan.FromHours(2) || age > TimeSpan.FromDays(45)) return false;
            if (post.Metrics.BreakdownSyncedUtc is not DateTime last) return true;
            var lastAge = last - post.PublishedUtc.Value;
            // Re-read once each time the post crosses a milestone it had not reached at the last reading.
            return BreakdownMilestones.Any(m => age >= m && lastAge < m);
        }

        private void MarkRemoved(string postId, DateTime now)
        {
            store.Mutate(s =>
            {
                var post = s.FindPost(postId);
                if (post == null || post.Status != PostStatus.Published) return;
                post.Status = PostStatus.Removed;
                post.LastError = "The post no longer exists on Tumblr (deleted or flagged).";
                s.Touch(post, now);
                s.AddEvent(EventLevel.Warning, "post.removed", $"A published post is gone from Tumblr (deleted or flagged): {post.TumblrUrl}", post.BlogId, post.PostId, now);
            });
        }

        private async Task<TimeSpan> SyncActivityAsync(string blogId, CancellationToken ct)
        {
            var target = BlogTarget(blogId);
            if (target == null) return ActivityEvery;
            var creds = await auth.GetCredentialsAsync(target.Value.ConnectionId, ct);
            DateTime? newestSeen = store.Read(s => s.InsightsOf(blogId).Activity.NewestSeenUtc);
            var items = new List<TumblrNotification>();
            long? before = null;
            for (int page = 0; page < 4; page++)
            {
                TumblrNotificationsPage result;
                try { result = await api.GetNotificationsAsync(creds, target.Value.BlogIdentifier, before, ct); }
                catch (TumblrApiException ex) when (ex.Kind is TumblrErrorKind.Forbidden or TumblrErrorKind.NotFound)
                {
                    return TimeSpan.FromHours(12); // activity is unavailable for this blog/app
                }
                items.AddRange(result.Items);
                if (result.Items.Count == 0 || result.NextBefore == null) break;
                if (newestSeen.HasValue && result.Items.Min(i => i.Utc) <= newestSeen.Value) break;
                if (newestSeen == null && page >= 1) break; // first sync: recent history is enough
                before = result.NextBefore;
            }
            DateTime now = clock();
            store.Mutate(s =>
            {
                var insights = s.InsightsOf(blogId);
                int added = OmniTumblrInsights.MergeActivity(insights.Activity, items);
                var blog = s.Blog(blogId);
                if (blog != null)
                {
                    blog.Stats.ActivitySyncedUtc = now;
                    s.MarkBlog(blogId);
                }
                if (added > 0) s.MarkInsights(blogId);
            });
            return ActivityEvery;
        }

        private Task<TimeSpan> HousekeepingAsync(CancellationToken ct)
        {
            auth.ExpireFlows();
            DateTime now = clock();
            var referenced = store.Read(s => new HashSet<string>(
                s.AllPosts().SelectMany(p => p.Media.SelectMany(m => new[] { m.Path, m.ThumbnailPath }))
                    .Where(p => p != null).Select(p => Path.GetFullPath(p!)),
                StringComparer.OrdinalIgnoreCase));
            int removed = 0;
            removed += DeleteUnreferenced(store.ThumbnailsDirectory, referenced, TimeSpan.FromDays(2), now);
            removed += DeleteUnreferenced(store.UploadsDirectory, referenced, TimeSpan.FromDays(14), now);
            // Candidate previews (content not yet planned) are cheap to regenerate.
            removed += DeleteUnreferenced(store.FramesCacheDirectory, new HashSet<string>(), TimeSpan.FromDays(3), now);
            if (removed > 0) log($"OmniTumblr housekeeping removed {removed} unreferenced media file(s).");
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
