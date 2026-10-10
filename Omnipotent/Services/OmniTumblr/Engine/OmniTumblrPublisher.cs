using Omnipotent.Services.OmniTumblr.Api;
using Omnipotent.Services.OmniTumblr.Models;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Omnipotent.Services.OmniTumblr.Engine
{
    /// <summary>
    /// Turns due posts into Tumblr posts. A post is claimed (Ready → Publishing) under the store lock before
    /// any network call, and only one publish pass runs at a time, so nothing is ever uploaded twice
    /// concurrently. Failures are handled by kind: limits and transcoding postpone without spending an
    /// attempt, a revoked token parks the connection for reauthorization, bad media fails the post and
    /// refills its slot, and anything ambiguous (timeout, 5xx) is reconciled by slug before retrying —
    /// the post may already exist. While Tumblr's edge refuses this server (<see cref="TumblrEdgeGate"/>),
    /// posts wait without spending attempts and go out when it answers again.
    /// </summary>
    internal sealed class OmniTumblrPublisher
    {
        public const int MaxAttempts = 5;
        /// <summary>LastErrorCode of a post waiting out an edge block.</summary>
        public const string EdgeWaitCode = "edge-403";
        /// <summary>How long the edge may refuse before Klives is told on Discord.</summary>
        public static readonly TimeSpan EdgeAlertAfter = TimeSpan.FromHours(2);
        private static readonly TimeSpan[] Backoff =
        {
            TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1),
        };

        private readonly OmniTumblrStore store;
        private readonly ITumblrApi api;
        private readonly OmniTumblrAuth auth;
        private readonly Func<DateTime> clock;
        private readonly Func<string, Task> alert;
        private readonly TumblrEdgeGate edgeGate;
        private readonly SemaphoreSlim passLock = new(1, 1);
        private readonly ConcurrentDictionary<string, DateTime> lastAlertUtc = new(StringComparer.Ordinal);

        /// <summary>Kill switch (OmniSetting OmniTumblrV2_PublishingEnabled).</summary>
        public Func<bool> PublishingEnabled { get; set; } = () => true;
        /// <summary>Called after an autopilot post failed because of its content, to refill its slot.</summary>
        public Func<string, Task>? OnContentFailure { get; set; }
        public TimeSpan ReconcileDelay { get; set; } = TimeSpan.FromSeconds(8);

        public OmniTumblrPublisher(OmniTumblrStore store, ITumblrApi api, OmniTumblrAuth auth, Func<DateTime> clock, Func<string, Task> alert,
            TumblrEdgeGate? edgeGate = null)
        {
            this.store = store;
            this.api = api;
            this.auth = auth;
            this.clock = clock;
            this.alert = alert;
            this.edgeGate = edgeGate ?? new TumblrEdgeGate(clock);
        }

        private sealed class Claimed
        {
            public string PostId = "";
            public string BlogId = "";
            public string BlogName = "";
            public string BlogIdentifier = "";
            public string ConnectionId = "";
            public OmniTumblrPost Post = new();
            public bool Reconcile;
            public DateTime ClaimedUtc;
        }

        /// <summary>Publishes every post that is due now, one at a time. Returns how many went out.</summary>
        public async Task<int> PublishDueAsync(CancellationToken ct)
        {
            if (!await passLock.WaitAsync(0, ct)) return 0; // a pass is already running and will see new posts
            try
            {
                await AlertIfEdgeBlockedLongAsync();
                int published = 0;
                var tried = new HashSet<string>(StringComparer.Ordinal);
                while (!ct.IsCancellationRequested)
                {
                    var claim = ClaimNext(tried);
                    if (claim == null) break;
                    tried.Add(claim.PostId);
                    if (await PublishClaimedAsync(claim, ct)) published++;
                }
                return published;
            }
            finally
            {
                passLock.Release();
            }
        }

        /// <summary>When the next post is due (for the engine's sleep), or null if none is pending.</summary>
        public DateTime? NextDueUtc() => store.Read(s =>
        {
            DateTime? next = null;
            foreach (var p in s.AllPosts())
            {
                if (p.Status != PostStatus.Ready || p.CaptionPending) continue;
                DateTime at = p.NextAttemptUtc.HasValue && p.NextAttemptUtc > p.ScheduledUtc ? p.NextAttemptUtc.Value : p.ScheduledUtc;
                if (next == null || at < next) next = at;
            }
            return next;
        });

        /// <summary>Posts left in Publishing by a crash: whether they were created is unknown, so reconcile first.</summary>
        public int RecoverInterrupted()
        {
            DateTime now = clock();
            return store.Mutate(s =>
            {
                int recovered = 0;
                foreach (var post in s.AllPosts().Where(p => p.Status == PostStatus.Publishing))
                {
                    post.Status = PostStatus.Ready;
                    post.NeedsReconcile = true;
                    post.BlockedReason = "Recovering from an interrupted upload.";
                    s.Touch(post, now);
                    recovered++;
                }
                if (recovered > 0)
                    s.AddEvent(EventLevel.Warning, "publish.recovered", $"{recovered} upload(s) were interrupted by a restart; they will be checked on Tumblr before retrying.", utc: now);
                return recovered;
            });
        }

        // ─────────────────────────────── Claiming ───────────────────────────────

        private Claimed? ClaimNext(HashSet<string> tried)
        {
            var contentFailures = new List<string>();
            var claimed = ClaimNextLocked(tried, contentFailures);
            // Refill slots outside the lock (the planner takes it again).
            foreach (string failedPostId in contentFailures)
                _ = Task.Run(() => NotifyContentFailureAsync(failedPostId));
            return claimed;
        }

        private Claimed? ClaimNextLocked(HashSet<string> tried, List<string> contentFailures)
        {
            DateTime now = clock();
            bool publishingEnabled = PublishingEnabled();
            return store.Mutate(s =>
            {
                var due = s.AllPosts()
                    .Where(p => p.Status == PostStatus.Ready && !p.CaptionPending && !tried.Contains(p.PostId)
                        && p.ScheduledUtc <= now && (p.NextAttemptUtc == null || p.NextAttemptUtc <= now))
                    .OrderBy(p => p.ScheduledUtc)
                    .ToList();

                foreach (var post in due)
                {
                    var blog = s.Blog(post.BlogId);
                    if (blog == null)
                    {
                        post.Status = PostStatus.Failed;
                        post.LastError = "The blog was removed.";
                        s.Touch(post, now);
                        continue;
                    }

                    if (post.Origin == PostOrigin.Autopilot && !post.ManualOverride && IsMissed(post, blog, now, out var lateBy))
                    {
                        post.Status = PostStatus.Skipped;
                        post.LastError = $"Missed its slot by {Describe(lateBy)}; skipped so stale posts don't pile up.";
                        if (post.Content != null) blog.UsedContentKeys.Remove(post.Content.Key); // unused, so reusable
                        s.MarkBlog(blog.BlogId);
                        s.Touch(post, now);
                        s.AddEvent(EventLevel.Warning, "publish.missed", $"@{blog.Name}: skipped a post that missed its slot by {Describe(lateBy)}.", blog.BlogId, post.PostId, now);
                        continue;
                    }

                    var (blocked, retryAt, edgeWait) = CheckGates(s, post, blog, now, publishingEnabled);
                    if (blocked == null)
                    {
                        var missing = post.Media.FirstOrDefault(m => !File.Exists(m.Path));
                        if (missing != null)
                        {
                            post.Status = PostStatus.Failed;
                            post.LastError = "The media file is missing: " + missing.Path;
                            post.LastErrorCode = "media.missing";
                            if (post.Content != null) blog.RejectedContentKeys.Add(post.Content.Key);
                            s.MarkBlog(blog.BlogId);
                            s.Touch(post, now);
                            s.AddEvent(EventLevel.Error, "publish.failed", $"@{blog.Name}: {post.LastError}", blog.BlogId, post.PostId, now);
                            if (post.Origin == PostOrigin.Autopilot) contentFailures.Add(post.PostId);
                            continue;
                        }
                    }
                    if (blocked != null)
                    {
                        bool changed = false;
                        if (post.BlockedReason != blocked) { post.BlockedReason = blocked; changed = true; }
                        if (retryAt.HasValue && post.NextAttemptUtc != retryAt)
                        {
                            post.NextAttemptUtc = retryAt;
                            // Waiting out an edge block is downtime, not a deliberate deferral: the missed-slot policy still applies.
                            if (!edgeWait) post.Deferrals++;
                            changed = true;
                        }
                        if (changed) s.Touch(post, now);
                        continue;
                    }

                    post.Status = PostStatus.Publishing;
                    post.BlockedReason = null;
                    post.Attempts++;
                    post.Slug ??= TumblrNpf.MakeSlug(post.Caption, post.PostId);
                    s.Touch(post, now);
                    return new Claimed
                    {
                        PostId = post.PostId,
                        BlogId = blog.BlogId,
                        BlogName = blog.Name,
                        BlogIdentifier = string.IsNullOrEmpty(blog.Uuid) ? blog.Name : blog.Uuid,
                        ConnectionId = blog.ConnectionId,
                        Post = OmniTumblrStore.Clone(post),
                        Reconcile = post.NeedsReconcile,
                        ClaimedUtc = now,
                    };
                }
                return null;
            });
        }

        private static bool IsMissed(OmniTumblrPost post, OmniTumblrBlog blog, DateTime now, out TimeSpan lateBy)
        {
            lateBy = now - post.ScheduledUtc;
            if (post.Attempts > 0 || post.Deferrals > 0) return false; // already in progress: deferrals/retries are deliberate
            var grace = TimeSpan.FromHours(Math.Clamp(blog.Strategy.MissedSlotGraceHours, 0, 72));
            if (blog.Strategy.MissedSlots == MissedSlotPolicy.Skip) grace = TimeSpan.FromMinutes(Math.Min(30, grace.TotalMinutes));
            return lateBy > grace && lateBy > TimeSpan.FromMinutes(10);
        }

        private (string? Blocked, DateTime? RetryAt, bool EdgeWait) CheckGates(OmniTumblrState s, OmniTumblrPost post, OmniTumblrBlog blog, DateTime now, bool publishingEnabled)
        {
            if (!publishingEnabled) return ("Publishing is switched off (OmniSetting OmniTumblrV2_PublishingEnabled).", null, false);
            if (!s.App.IsConfigured) return ("The Tumblr app credentials are not set.", null, false);
            if (blog.Paused) return ("The blog is paused.", null, false);
            if (!post.Approved && blog.RequireApproval && post.Origin == PostOrigin.Autopilot) return ("Waiting for approval.", null, false);
            var conn = s.Connection(blog.ConnectionId);
            if (conn == null) return ("The blog has no Tumblr connection; reconnect its account.", null, false);
            if (conn.Health == ConnectionHealth.NeedsReauth) return ($"Reconnect @{conn.UserName}: {conn.HealthDetail ?? "authorization expired or was revoked."}", null, false);

            if (post.Origin == PostOrigin.Autopilot && !post.ManualOverride)
            {
                var tz = OmniTumblrScheduleMath.ResolveTimeZone(blog.Strategy.TimeZone);
                var dayStart = OmniTumblrScheduleMath.LocalDayStartUtc(now, tz);
                int publishedToday = s.PostsOf(blog.BlogId).Count(p => p.Status == PostStatus.Published && p.PublishedUtc >= dayStart);
                int cap = Math.Max(1, blog.Strategy.MaxPostsPerDay);
                if (publishedToday >= cap)
                {
                    var nextDay = OmniTumblrScheduleMath.LocalToUtc(OmniTumblrScheduleMath.UtcToLocal(now, tz).Date.AddDays(1), tz);
                    return ($"Daily cap of {cap} post(s) reached; continues tomorrow.", nextDay.AddMinutes(5), false);
                }
                int gap = Math.Clamp(blog.Strategy.MinGapMinutes, 0, 24 * 60);
                if (gap > 0 && blog.Stats.LastPublishedUtc is DateTime last && now - last < TimeSpan.FromMinutes(gap))
                    return ($"Spacing posts at least {gap} min apart.", last.AddMinutes(gap), false);
            }

            foreach (var (key, limit) in RelevantLimits(conn, post))
            {
                if (limit.Remaining <= 0 && limit.ResetUtc is DateTime reset && reset > now)
                    return ($"Tumblr's daily {Humanize(key)} limit is used up until {reset:HH:mm} UTC.", reset.AddMinutes(2), false);
            }

            // Last, so the probe slot is only taken by a post that will really be sent.
            if (!edgeGate.TryEnter(out var probeAt)) return (EdgeWaitReason(probeAt), probeAt, true);
            return (null, null, false);
        }

        private string EdgeWaitReason(DateTime retryAt)
        {
            var since = edgeGate.Snapshot().BlockedSinceUtc;
            return $"Tumblr's edge has been refusing this server's requests{(since is DateTime t ? $" since {t:HH:mm} UTC" : "")} " +
                $"(an HTML 403 before the API — the server's network, not this post or the account); waiting, next try {retryAt:HH:mm} UTC.";
        }

        private async Task AlertIfEdgeBlockedLongAsync()
        {
            var edge = edgeGate.Snapshot();
            if (edge.BlockedSinceUtc is not DateTime since || clock() - since < EdgeAlertAfter) return;
            await AlertOnceAsync("edge-blocked",
                $"OmniTumblr: Tumblr's edge has refused every request from this server since {since:HH:mm} UTC (HTML 403 from nginx, not the API). " +
                "Posts are waiting, not failing, and go out when it lifts. If it keeps happening, set the OmniSetting OmniTumblr_Proxy to route Tumblr traffic another way.");
        }

        /// <summary>The /user/limits entries that apply to this post (names vary, so match loosely).</summary>
        internal static IEnumerable<(string Key, TumblrLimit Limit)> RelevantLimits(OmniTumblrConnection conn, OmniTumblrPost post)
        {
            bool video = post.Media.Any(m => m.IsVideo);
            bool image = post.Media.Any(m => !m.IsVideo);
            foreach (var (key, limit) in conn.Limits)
            {
                string k = key.ToLowerInvariant();
                if (k.Contains("video") && video) yield return (key, limit);
                else if ((k.Contains("photo") || k.Contains("image")) && image) yield return (key, limit);
                else if (k is "posts" or "post" or "posts_per_day") yield return (key, limit);
            }
        }

        private static string Humanize(string key) => key.Replace('_', ' ').ToLowerInvariant();

        // ─────────────────────────────── Publishing ───────────────────────────────

        private async Task<bool> PublishClaimedAsync(Claimed c, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            TumblrCredentials creds;
            try
            {
                creds = await auth.GetCredentialsAsync(c.ConnectionId, ct);
            }
            catch (ConnectionUnavailableException ex)
            {
                Release(c, ex.Message, null);
                return false;
            }
            catch (TumblrApiException ex) when (ex.Kind == TumblrErrorKind.Unauthorized)
            {
                auth.MarkNeedsReauth(c.ConnectionId, ex.Message);
                Release(c, "Reconnect the Tumblr account: " + ex.Message, null);
                return false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Release(c, null, null);
                throw;
            }
            catch (Exception ex)
            {
                Release(c, "Could not load the account's credentials: " + ex.Message, clock().AddMinutes(5));
                return false;
            }

            if (c.Reconcile)
            {
                var existing = await TryFindPublishedAsync(creds, c, ct);
                if (existing != null)
                {
                    ApplySuccess(c, existing.Id, existing.PostUrl, sw.ElapsedMilliseconds, reconciled: true);
                    return true;
                }
            }

            NpfPostRequest request;
            try
            {
                request = TumblrNpf.Build(new NpfBuildInput
                {
                    Kind = c.Post.Kind,
                    Media = c.Post.Media,
                    Caption = c.Post.Caption,
                    Title = c.Post.Title,
                    LinkUrl = c.Post.LinkUrl,
                    Tags = c.Post.Tags,
                    SourceUrl = c.Post.SourceUrl,
                    Slug = c.Post.Slug,
                    State = c.Post.TumblrState,
                    AltText = c.Post.CaptionInfo.AltText,
                });
            }
            catch (Exception ex)
            {
                await ApplyFailureAsync(c, new TumblrApiException(TumblrErrorKind.BadRequest, 0, null, "The post could not be built: " + ex.Message), sw.ElapsedMilliseconds);
                return false;
            }

            try
            {
                var created = await api.CreatePostAsync(creds, c.BlogIdentifier, request, ct);
                bool provisional = request.Uploads.Any(u => u.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                    || string.Equals(created.State, "transcoding", StringComparison.OrdinalIgnoreCase);
                ApplySuccess(c, created.Id, null, sw.ElapsedMilliseconds, reconciled: false, provisional);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutting down mid-upload: whether Tumblr got it is unknown.
                MarkForReconcile(c, "Interrupted by shutdown.");
                throw;
            }
            catch (TumblrApiException ex)
            {
                if (ex.Ambiguous || ex.Kind is TumblrErrorKind.ServerError or TumblrErrorKind.ServiceUnavailable or TumblrErrorKind.Unknown)
                {
                    try
                    {
                        await Task.Delay(ReconcileDelay, ct);
                        var existing = await TryFindPublishedAsync(creds, c, ct);
                        if (existing != null)
                        {
                            ApplySuccess(c, existing.Id, existing.PostUrl, sw.ElapsedMilliseconds, reconciled: true);
                            return true;
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { MarkForReconcile(c, ex.Message); throw; }
                    catch { /* fall through to the failure policy, which keeps NeedsReconcile set */ }
                }
                await ApplyFailureAsync(c, ex, sw.ElapsedMilliseconds);
                return false;
            }
            catch (Exception ex)
            {
                await ApplyFailureAsync(c, new TumblrApiException(TumblrErrorKind.Unknown, 0, null, ex.Message, inner: ex) { Ambiguous = true }, sw.ElapsedMilliseconds);
                return false;
            }
        }

        /// <summary>Looks for the post on the blog by its unique slug (or, failing that, by time + caption + tags).</summary>
        private async Task<TumblrPostSummary?> TryFindPublishedAsync(TumblrCredentials creds, Claimed c, CancellationToken ct)
        {
            if (c.Post.TumblrState != TumblrPostState.Published) return null; // drafts/private posts are not in the public listing
            TumblrPostsPage page;
            try { page = await api.GetPostsAsync(creds, c.BlogIdentifier, 0, 20, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return null; }
            string? slug = c.Post.Slug;
            if (!string.IsNullOrEmpty(slug))
            {
                var bySlug = page.Posts.FirstOrDefault(p => p.Slug != null && p.Slug.StartsWith(slug, StringComparison.OrdinalIgnoreCase));
                if (bySlug != null) return bySlug;
            }
            DateTime since = (c.Post.AttemptLog.FirstOrDefault()?.Utc ?? c.ClaimedUtc).AddMinutes(-3);
            var wantTags = new HashSet<string>(TumblrNpf.NormalizeTags(c.Post.Tags), StringComparer.OrdinalIgnoreCase);
            string captionHead = (c.Post.Caption ?? "").Trim();
            if (captionHead.Length > 30) captionHead = captionHead[..30];
            return page.Posts.FirstOrDefault(p => p.PublishedUtc >= since
                && wantTags.SetEquals(p.Tags)
                && (captionHead.Length == 0 || (p.Summary ?? "").Contains(captionHead, StringComparison.OrdinalIgnoreCase)));
        }

        private void ApplySuccess(Claimed c, string tumblrId, string? url, long durationMs, bool reconciled, bool provisional = false)
        {
            DateTime now = clock();
            store.Mutate(s =>
            {
                var post = s.FindPost(c.PostId);
                if (post == null) return;
                var blog = s.Blog(post.BlogId);
                post.Status = PostStatus.Published;
                post.TumblrPostId = tumblrId;
                post.TumblrIdProvisional = provisional;
                post.PublishedUtc = now;
                post.TumblrUrl = url ?? TumblrApiClient.BuildPostUrl(blog?.Name ?? c.BlogName, tumblrId);
                post.NeedsReconcile = false;
                post.LastError = null;
                post.LastErrorCode = null;
                post.BlockedReason = null;
                post.NextAttemptUtc = null;
                AddAttempt(post, new PublishAttempt { Utc = now, Ok = true, Code = reconciled ? "reconciled" : "201", DurationMs = durationMs, Message = reconciled ? "Found on Tumblr after an uncertain response." : null });
                post.Metrics.History.Add(new MetricPoint { Utc = now, Notes = 0 });
                s.Touch(post, now);

                if (blog != null)
                {
                    blog.Stats.LastPublishedUtc = now;
                    if (blog.Stats.Posts.HasValue) blog.Stats.Posts++;
                    blog.Health.ConsecutiveFailures = 0;
                    blog.Health.LastSuccessUtc = now;
                    s.MarkBlog(blog.BlogId);

                    var conn = s.Connection(blog.ConnectionId);
                    if (conn != null)
                    {
                        foreach (var (_, limit) in RelevantLimits(conn, post)) limit.Remaining = Math.Max(0, limit.Remaining - 1);
                        s.MarkConnections();
                    }

                    // A provisional id never appears on Tumblr; the live one is indexed once it is found.
                    if (!provisional) OmniTumblrInsights.MergeIndex(s.InsightsOf(blog.BlogId), new[]
                    {
                        new TumblrPostSummary
                        {
                            Id = tumblrId, Slug = post.Slug, PublishedUtc = now, NoteCount = 0, Tags = post.Tags.ToList(),
                            PostUrl = post.TumblrUrl, Type = post.Kind.ToString().ToLowerInvariant(), Summary = post.Caption,
                        },
                    }, _ => post.PostId);
                    s.MarkInsights(blog.BlogId);
                    s.AddEvent(EventLevel.Success, "publish.ok",
                        $"@{blog.Name}: published {post.Kind.ToString().ToLowerInvariant()}{(reconciled ? " (confirmed after an uncertain response)" : "")}{(provisional ? " — Tumblr is processing the video" : " — " + post.TumblrUrl)}",
                        blog.BlogId, post.PostId, now);
                }
            });
        }

        private async Task ApplyFailureAsync(Claimed c, TumblrApiException ex, long durationMs)
        {
            DateTime now = clock();
            string? alertMessage = null;
            string? alertKey = null;
            bool contentFailure = false;

            store.Mutate(s =>
            {
                var post = s.FindPost(c.PostId);
                if (post == null) return;
                var blog = s.Blog(post.BlogId);
                var conn = blog == null ? null : s.Connection(blog.ConnectionId);
                string blogName = blog?.Name ?? c.BlogName;
                string? previousCode = post.LastErrorCode;
                AddAttempt(post, new PublishAttempt { Utc = now, Ok = false, Code = ex.Code, Message = ex.Message, DurationMs = durationMs });
                post.LastError = ex.Message;
                post.LastErrorCode = ex.Code;
                bool countsAsFailure = true;

                switch (ex.Kind)
                {
                    case TumblrErrorKind.Unauthorized:
                        post.Status = PostStatus.Ready;
                        post.Attempts = Math.Max(0, post.Attempts - 1);
                        post.BlockedReason = "Reconnect the Tumblr account (authorization rejected).";
                        if (conn != null && conn.Health != ConnectionHealth.NeedsReauth)
                        {
                            conn.Health = ConnectionHealth.NeedsReauth;
                            conn.HealthDetail = ex.Message;
                            conn.HealthChangedUtc = now;
                            s.MarkConnections();
                            s.AddEvent(EventLevel.Error, "connection.reauth", $"Tumblr rejected @{conn.UserName}'s authorization; reconnect it to resume posting.", blog?.BlogId, post.PostId, now);
                            alertKey = "reauth:" + conn.ConnectionId;
                            alertMessage = $"OmniTumblr: Tumblr rejected @{conn.UserName}'s authorization. Posting to {blogName} is paused until you reconnect the account on the OmniTumblr page.";
                        }
                        countsAsFailure = false;
                        break;

                    case TumblrErrorKind.DailyPostLimit:
                    case TumblrErrorKind.DailyMediaLimit:
                    case TumblrErrorKind.DailyVideoLimit:
                    {
                        post.Status = PostStatus.Ready;
                        post.Attempts = Math.Max(0, post.Attempts - 1);
                        post.Deferrals++;
                        DateTime? reset = null;
                        if (conn != null)
                        {
                            foreach (var (_, limit) in RelevantLimits(conn, post))
                            {
                                limit.Remaining = 0;
                                if (limit.ResetUtc > now && (reset == null || limit.ResetUtc > reset)) reset = limit.ResetUtc;
                            }
                            s.MarkConnections();
                        }
                        post.NextAttemptUtc = (reset ?? ex.RetryAfterUtc ?? now.Date.AddDays(1).AddMinutes(15)).AddMinutes(2);
                        post.BlockedReason = $"Tumblr's daily limit was reached ({ex.Code}); retrying at {post.NextAttemptUtc:HH:mm} UTC.";
                        s.AddEvent(EventLevel.Warning, "publish.limit", $"@{blogName}: {post.BlockedReason}", blog?.BlogId, post.PostId, now);
                        countsAsFailure = false;
                        break;
                    }

                    case TumblrErrorKind.EdgeBlocked:
                        // The request never reached the API, so nothing was posted and nothing is wrong with the post:
                        // wait for the edge to answer again (the gate paces the retries), however long that takes.
                        post.Status = PostStatus.Ready;
                        post.Attempts = Math.Max(0, post.Attempts - 1);
                        post.NextAttemptUtc = edgeGate.RetryAtUtc ?? now.AddMinutes(5);
                        post.BlockedReason = EdgeWaitReason(post.NextAttemptUtc.Value);
                        post.LastErrorCode = EdgeWaitCode;
                        if (previousCode != EdgeWaitCode)
                            s.AddEvent(EventLevel.Warning, "publish.edge-wait", $"@{blogName}: Tumblr's edge refused the upload (an HTML 403 — this server's network is being blocked, not the post); it will go out when Tumblr answers again.", blog?.BlogId, post.PostId, now);
                        countsAsFailure = false;
                        break;

                    case TumblrErrorKind.RateLimited:
                        post.Status = PostStatus.Ready;
                        post.Attempts = Math.Max(0, post.Attempts - 1);
                        post.Deferrals++;
                        post.NextAttemptUtc = ex.RetryAfterUtc ?? now.AddMinutes(15);
                        post.BlockedReason = "Tumblr rate limit; retrying shortly.";
                        countsAsFailure = false;
                        break;

                    case TumblrErrorKind.VideoTranscoding when post.Deferrals < 36:
                        post.Status = PostStatus.Ready;
                        post.Attempts = Math.Max(0, post.Attempts - 1);
                        post.Deferrals++;
                        post.NextAttemptUtc = now.AddMinutes(5);
                        post.BlockedReason = "Tumblr is still processing an earlier video; retrying in 5 minutes.";
                        countsAsFailure = false;
                        break;

                    case TumblrErrorKind.InvalidMedia:
                    case TumblrErrorKind.BadRequest:
                        post.Status = PostStatus.Failed;
                        post.NeedsReconcile = false;
                        if (post.Content != null && blog != null && ex.Kind == TumblrErrorKind.InvalidMedia)
                        {
                            blog.RejectedContentKeys.Add(post.Content.Key);
                            s.MarkBlog(blog.BlogId);
                        }
                        contentFailure = post.Origin == PostOrigin.Autopilot;
                        s.AddEvent(EventLevel.Error, "publish.failed", $"@{blogName}: Tumblr rejected the post ({ex.Code}): {ex.Message}", blog?.BlogId, post.PostId, now);
                        break;

                    case TumblrErrorKind.NotFound:
                        post.Status = PostStatus.Ready;
                        post.Attempts = Math.Max(0, post.Attempts - 1);
                        post.NextAttemptUtc = now.AddHours(1);
                        post.BlockedReason = "Tumblr says this blog does not exist — it may have been renamed or deleted.";
                        alertKey = "notfound:" + c.BlogId;
                        alertMessage = $"OmniTumblr: Tumblr reports blog @{blogName} as not found. Check whether it was renamed or deleted.";
                        s.AddEvent(EventLevel.Error, "publish.blog-missing", $"@{blogName}: {post.BlockedReason}", blog?.BlogId, post.PostId, now);
                        break;

                    case TumblrErrorKind.Forbidden:
                    case TumblrErrorKind.QueueFull:
                        post.Status = PostStatus.Failed;
                        post.NeedsReconcile = false;
                        alertKey = "forbidden:" + c.BlogId;
                        alertMessage = $"OmniTumblr: Tumblr refused a post to @{blogName} ({ex.Code}): {ex.Message}";
                        s.AddEvent(EventLevel.Error, "publish.failed", $"@{blogName}: Tumblr refused the post ({ex.Code}): {ex.Message}", blog?.BlogId, post.PostId, now);
                        break;

                    default:
                        post.NeedsReconcile = post.NeedsReconcile || ex.Ambiguous || ex.Kind is TumblrErrorKind.ServerError or TumblrErrorKind.ServiceUnavailable or TumblrErrorKind.Unknown or TumblrErrorKind.VideoTranscoding;
                        if (post.Attempts >= MaxAttempts)
                        {
                            post.Status = PostStatus.Failed;
                            s.AddEvent(EventLevel.Error, "publish.failed", $"@{blogName}: gave up after {post.Attempts} attempts. Last error ({ex.Code}): {ex.Message}", blog?.BlogId, post.PostId, now);
                            alertKey = "failed:" + c.BlogId;
                            alertMessage = $"OmniTumblr: a post to @{blogName} failed after {post.Attempts} attempts — {ex.Message}";
                        }
                        else
                        {
                            post.Status = PostStatus.Ready;
                            var wait = Backoff[Math.Min(post.Attempts - 1, Backoff.Length - 1)];
                            post.NextAttemptUtc = now + wait;
                            post.BlockedReason = $"Retrying in {wait.TotalMinutes:0} min after: {ex.Message}";
                            if (post.Attempts == 1)
                                s.AddEvent(EventLevel.Warning, "publish.retry", $"@{blogName}: publish failed ({ex.Code}); retrying in {wait.TotalMinutes:0} min. {ex.Message}", blog?.BlogId, post.PostId, now);
                        }
                        break;
                }

                if (blog != null)
                {
                    blog.Health.LastError = ex.Message;
                    blog.Health.LastErrorUtc = now;
                    if (countsAsFailure) blog.Health.ConsecutiveFailures++;
                    s.MarkBlog(blog.BlogId);
                }
                s.Touch(post, now);
            });

            if (alertMessage != null && alertKey != null) await AlertOnceAsync(alertKey, alertMessage);
            if (contentFailure) await NotifyContentFailureAsync(c.PostId);
        }

        // ─────────────────────────────── Provisional ids ───────────────────────────────

        public static readonly TimeSpan ConfirmFirstAfter = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan ConfirmEvery = TimeSpan.FromMinutes(1);
        public static readonly TimeSpan ConfirmGiveUpAfter = TimeSpan.FromHours(6);
        private readonly ConcurrentDictionary<string, DateTime> lastConfirmCheckUtc = new(StringComparer.Ordinal);

        /// <summary>
        /// Video posts are answered with a placeholder id while Tumblr transcodes; the post then goes live under
        /// a new id. Finds the live post by its unique slug and re-keys ours, so links, metrics and deletes
        /// point at the real post. <paramref name="force"/> skips the pacing (a user is waiting on it).
        /// Returns how many were confirmed.
        /// </summary>
        public async Task<int> ConfirmProvisionalIdsAsync(CancellationToken ct, string? onlyPostId = null, bool force = false)
        {
            if (!force && edgeGate.IsBlocked) return 0; // checked again once Tumblr answers; sync does the probing
            DateTime now = clock();
            var pending = store.Read(s => s.AllPosts()
                .Where(p => p.Status == PostStatus.Published && p.TumblrIdProvisional && p.PublishedUtc.HasValue
                    && (onlyPostId == null || p.PostId == onlyPostId))
                .Select(p => (p.PostId, p.BlogId, p.Slug, Published: p.PublishedUtc!.Value))
                .ToList());
            int confirmed = 0;
            foreach (var group in pending.GroupBy(p => p.BlogId))
            {
                var due = group.Where(p => force || (now - p.Published >= ConfirmFirstAfter
                    && (!lastConfirmCheckUtc.TryGetValue(p.PostId, out var last) || now - last >= ConfirmEvery))).ToList();
                if (due.Count == 0) continue;
                var target = store.Read(s => s.Blog(group.Key) is { } b
                    ? (Identifier: string.IsNullOrEmpty(b.Uuid) ? b.Name : b.Uuid, b.ConnectionId)
                    : (Identifier: (string?)null, ConnectionId: ""));
                if (target.Identifier == null) continue;
                foreach (var p in due) lastConfirmCheckUtc[p.PostId] = now;

                TumblrPostsPage page;
                try
                {
                    var creds = await auth.GetCredentialsAsync(target.ConnectionId, ct);
                    page = await api.GetPostsAsync(creds, target.Identifier, 0, 20, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { continue; } // checked again next pass

                foreach (var p in due)
                {
                    var live = string.IsNullOrEmpty(p.Slug) ? null
                        : page.Posts.FirstOrDefault(x => x.Slug != null && x.Slug.StartsWith(p.Slug, StringComparison.OrdinalIgnoreCase));
                    if (live != null)
                    {
                        if (ApplyConfirmedId(p.PostId, live)) confirmed++;
                        lastConfirmCheckUtc.TryRemove(p.PostId, out _);
                    }
                    else if (now - p.Published > ConfirmGiveUpAfter)
                    {
                        store.Mutate(s =>
                        {
                            var post = s.FindPost(p.PostId);
                            if (post == null || !post.TumblrIdProvisional) return;
                            post.TumblrIdProvisional = false;
                            s.Touch(post, now);
                            s.AddEvent(EventLevel.Warning, "publish.unconfirmed",
                                $"Tumblr accepted a video post but it had not appeared on the blog after {Describe(ConfirmGiveUpAfter)}; its processing may have failed.",
                                post.BlogId, post.PostId, now);
                        });
                        lastConfirmCheckUtc.TryRemove(p.PostId, out _);
                    }
                }
            }
            return confirmed;
        }

        /// <summary>When the next provisional id is worth checking (for the engine's sleep), or null.</summary>
        public DateTime? NextConfirmUtc() => store.Read(s =>
        {
            DateTime? next = null;
            foreach (var p in s.AllPosts())
            {
                if (p.Status != PostStatus.Published || !p.TumblrIdProvisional || p.PublishedUtc is not DateTime published) continue;
                DateTime at = published + ConfirmFirstAfter;
                if (lastConfirmCheckUtc.TryGetValue(p.PostId, out var last) && last + ConfirmEvery > at) at = last + ConfirmEvery;
                if (next == null || at < next) next = at;
            }
            return next;
        });

        /// <summary>
        /// Looks a published post up on Tumblr by slug and, if it lives under a different id than ours (a
        /// placeholder recorded before provisional ids were tracked), re-keys it. Returns the live id, or null.
        /// </summary>
        public async Task<string?> RelinkBySlugAsync(string postId, CancellationToken ct)
        {
            var target = store.Read(s =>
            {
                var post = s.FindPost(postId);
                var blog = post == null ? null : s.Blog(post.BlogId);
                if (post == null || blog == null || string.IsNullOrEmpty(post.Slug)) return null;
                return new { post.Slug, post.TumblrPostId, Identifier = string.IsNullOrEmpty(blog.Uuid) ? blog.Name : blog.Uuid, blog.ConnectionId };
            });
            if (target == null) return null;
            var creds = await auth.GetCredentialsAsync(target.ConnectionId, ct);
            var page = await api.GetPostsAsync(creds, target.Identifier, 0, 20, ct);
            var live = page.Posts.FirstOrDefault(x => x.Slug != null && x.Slug.StartsWith(target.Slug, StringComparison.OrdinalIgnoreCase));
            if (live == null) return null;
            if (live.Id != target.TumblrPostId) ApplyConfirmedId(postId, live, requireProvisional: false);
            return live.Id;
        }

        private bool ApplyConfirmedId(string postId, TumblrPostSummary live, bool requireProvisional = true)
        {
            DateTime now = clock();
            return store.Mutate(s =>
            {
                var post = s.FindPost(postId);
                if (post == null || (requireProvisional && !post.TumblrIdProvisional)) return false;
                var blog = s.Blog(post.BlogId);
                string? oldId = post.TumblrPostId;
                post.TumblrPostId = live.Id;
                post.TumblrIdProvisional = false;
                post.TumblrUrl = !string.IsNullOrEmpty(live.PostUrl) ? live.PostUrl : TumblrApiClient.BuildPostUrl(blog?.Name ?? live.BlogName ?? "", live.Id);
                s.Touch(post, now);
                if (blog != null)
                {
                    var insights = s.InsightsOf(blog.BlogId);
                    if (oldId != null && oldId != live.Id) insights.Index.RemoveAll(e => e.Id == oldId);
                    OmniTumblrInsights.MergeIndex(insights, new[] { live }, _ => post.PostId);
                    s.MarkInsights(blog.BlogId);
                    s.AddEvent(EventLevel.Success, "publish.live", $"@{blog.Name}: the post is live — {post.TumblrUrl}", blog.BlogId, post.PostId, now);
                }
                return true;
            });
        }

        /// <summary>Returns a claimed post to Ready without counting the attempt (nothing was sent).</summary>
        private void Release(Claimed c, string? reason, DateTime? retryAt)
        {
            DateTime now = clock();
            store.Mutate(s =>
            {
                var post = s.FindPost(c.PostId);
                if (post == null || post.Status != PostStatus.Publishing) return;
                post.Status = PostStatus.Ready;
                post.Attempts = Math.Max(0, post.Attempts - 1);
                post.BlockedReason = reason;
                if (retryAt.HasValue) post.NextAttemptUtc = retryAt;
                s.Touch(post, now);
            });
        }

        private void MarkForReconcile(Claimed c, string reason)
        {
            DateTime now = clock();
            store.Mutate(s =>
            {
                var post = s.FindPost(c.PostId);
                if (post == null || post.Status != PostStatus.Publishing) return;
                post.Status = PostStatus.Ready;
                post.NeedsReconcile = true;
                post.BlockedReason = reason;
                s.Touch(post, now);
            });
        }

        private static void AddAttempt(OmniTumblrPost post, PublishAttempt attempt)
        {
            post.AttemptLog.Add(attempt);
            if (post.AttemptLog.Count > 12) post.AttemptLog.RemoveRange(0, post.AttemptLog.Count - 12);
        }

        private async Task NotifyContentFailureAsync(string postId)
        {
            var callback = OnContentFailure;
            if (callback == null) return;
            try { await callback(postId); } catch { }
        }

        private async Task AlertOnceAsync(string key, string message)
        {
            DateTime now = clock();
            if (lastAlertUtc.TryGetValue(key, out var last) && now - last < TimeSpan.FromHours(6)) return;
            lastAlertUtc[key] = now;
            try { await alert(message); } catch { }
        }

        internal static string Describe(TimeSpan span) =>
            span.TotalDays >= 1 ? $"{span.TotalDays:0.#} days" : span.TotalHours >= 1 ? $"{span.TotalHours:0.#} h" : $"{Math.Max(1, span.TotalMinutes):0} min";
    }
}
