using Omnipotent.Services.OmniGram.Api;
using Omnipotent.Services.OmniGram.Models;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Omnipotent.Services.OmniGram.Engine
{
    /// <summary>
    /// Turns due posts into Instagram posts through the official two-step publish: create a media container
    /// (Instagram downloads the media from a signed public URL), wait until Instagram has processed it, then
    /// publish it. Every step is persisted on the post (<see cref="PublishProgress"/>), so a restart resumes
    /// instead of re-sending, and media_publish is only ever called on a container whose status is FINISHED —
    /// if its answer is lost, the next check sees PUBLISHED and adopts the post rather than publishing twice.
    /// Failures are handled by kind (Meta's error codes): an expired token parks the account for reconnecting,
    /// limits postpone without spending attempts, broken media fails the post and refills its slot, and
    /// transient errors retry with backoff on a fresh container.
    /// </summary>
    internal sealed class OmniGramPublisher
    {
        public const int MaxAttempts = 5;
        public const int MaxFetchFailures = 3;
        public static readonly TimeSpan ContainerLifetime = TimeSpan.FromHours(23);
        public static readonly TimeSpan MaxProcessing = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan[] Backoff =
        {
            TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1),
        };
        private static readonly TimeSpan[] StatusPoll =
        {
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(60),
        };

        private readonly OmniGramStore store;
        private readonly IInstagramApi api;
        private readonly OmniGramAuth auth;
        private readonly OmniGramMediaHost mediaHost;
        private readonly Func<DateTime> clock;
        private readonly Func<string, Task> alert;
        private readonly SemaphoreSlim passLock = new(1, 1);
        private readonly ConcurrentDictionary<string, DateTime> lastAlertUtc = new(StringComparer.Ordinal);

        /// <summary>Kill switch (OmniSetting OmniGramV2_PublishingEnabled).</summary>
        public Func<bool> PublishingEnabled { get; set; } = () => true;
        /// <summary>Called after an autopilot post failed because of its content, to refill its slot.</summary>
        public Func<string, Task>? OnContentFailure { get; set; }
        /// <summary>Called after a post went live (companion stories, sync wake-ups).</summary>
        public Func<string, Task>? OnPublished { get; set; }

        public OmniGramPublisher(OmniGramStore store, IInstagramApi api, OmniGramAuth auth, OmniGramMediaHost mediaHost, Func<DateTime> clock, Func<string, Task> alert)
        {
            this.store = store;
            this.api = api;
            this.auth = auth;
            this.mediaHost = mediaHost;
            this.clock = clock;
            this.alert = alert;
        }

        private sealed class Job
        {
            public string PostId = "";
            public string AccountId = "";
            public string Username = "";
            public OmniGramPost Post = new();
        }

        // ─────────────────────────────── The pass ───────────────────────────────

        /// <summary>
        /// One publishing pass: advances posts Instagram is processing, starts every post that is due, and
        /// retries pending first comments. Returns how many posts went live.
        /// </summary>
        public async Task<int> PublishDueAsync(CancellationToken ct)
        {
            if (!await passLock.WaitAsync(0, ct)) return 0; // a pass is already running and will see new posts
            try
            {
                int published = 0;
                foreach (string postId in DueForCheck())
                {
                    if (ct.IsCancellationRequested) break;
                    if (await AdvanceAsync(postId, ct)) published++;
                }

                var tried = new HashSet<string>(StringComparer.Ordinal);
                while (!ct.IsCancellationRequested)
                {
                    var job = ClaimNext(tried);
                    if (job == null) break;
                    tried.Add(job.PostId);
                    await StartAsync(job, ct);
                    if (await AdvanceAsync(job.PostId, ct)) published++; // images are often ready at once
                }

                await PostPendingFirstCommentsAsync(ct);
                return published;
            }
            finally
            {
                passLock.Release();
            }
        }

        /// <summary>When the engine should wake next: the earliest due post or container check.</summary>
        public DateTime? NextDueUtc() => store.Read(s =>
        {
            DateTime? next = null;
            void Consider(DateTime at) { if (next == null || at < next) next = at; }
            foreach (var p in s.AllPosts())
            {
                if (p.Status == PostStatus.Ready && !p.CaptionPending)
                    Consider(p.NextAttemptUtc.HasValue && p.NextAttemptUtc > p.ScheduledUtc ? p.NextAttemptUtc.Value : p.ScheduledUtc);
                else if (p.Status == PostStatus.Publishing && p.Publish.NextCheckUtc is DateTime check)
                    Consider(check);
                if (p.FirstCommentPending && p.NextAttemptUtc is DateTime retry) Consider(retry);
            }
            return next;
        });

        /// <summary>
        /// After a restart, posts left in Publishing either have a container (keep checking it — it may even
        /// be live already) or never got one (nothing reached Instagram: back to Ready).
        /// </summary>
        public int RecoverInterrupted()
        {
            DateTime now = clock();
            return store.Mutate(s =>
            {
                int recovered = 0;
                foreach (var post in s.AllPosts().Where(p => p.Status == PostStatus.Publishing))
                {
                    if (post.Publish.ContainerId != null || post.Publish.ChildContainerIds.Count > 0)
                    {
                        post.Publish.NextCheckUtc = now.AddSeconds(15);
                        post.BlockedReason = "Checking on an upload that was in progress during a restart.";
                    }
                    else
                    {
                        post.Status = PostStatus.Ready;
                        post.Publish.Stage = PublishStage.None;
                        post.Attempts = Math.Max(0, post.Attempts - 1);
                        post.BlockedReason = null;
                    }
                    s.Touch(post, now);
                    recovered++;
                }
                if (recovered > 0)
                    s.AddEvent(EventLevel.Warning, "publish.recovered", $"{recovered} upload(s) were interrupted by a restart; they are being checked on Instagram before anything is resent.", utc: now);
                return recovered;
            });
        }

        // ─────────────────────────────── Claiming ───────────────────────────────

        private List<string> DueForCheck()
        {
            DateTime now = clock();
            return store.Read(s => s.AllPosts()
                .Where(p => p.Status == PostStatus.Publishing && (p.Publish.NextCheckUtc == null || p.Publish.NextCheckUtc <= now))
                .OrderBy(p => p.Publish.NextCheckUtc ?? DateTime.MinValue)
                .Select(p => p.PostId)
                .ToList());
        }

        private Job? ClaimNext(HashSet<string> tried)
        {
            var contentFailures = new List<string>();
            var claimed = ClaimNextLocked(tried, contentFailures);
            foreach (string failedPostId in contentFailures)
                _ = Task.Run(() => NotifyContentFailureAsync(failedPostId));
            return claimed;
        }

        private Job? ClaimNextLocked(HashSet<string> tried, List<string> contentFailures)
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
                    var account = s.Account(post.AccountId);
                    if (account == null)
                    {
                        post.Status = PostStatus.Failed;
                        post.LastError = "The account was removed.";
                        s.Touch(post, now);
                        continue;
                    }

                    if (post.Origin == PostOrigin.Autopilot && !post.ManualOverride && IsMissed(post, account, now, out var lateBy))
                    {
                        post.Status = PostStatus.Skipped;
                        post.LastError = $"Missed its slot by {Describe(lateBy)}; skipped so stale posts don't pile up.";
                        if (post.Content != null) account.UsedContentKeys.Remove(post.Content.Key); // unused, so reusable
                        s.MarkAccount(account.AccountId);
                        s.Touch(post, now);
                        s.AddEvent(EventLevel.Warning, "publish.missed", $"@{account.Username}: skipped a post that missed its slot by {Describe(lateBy)}.", account.AccountId, post.PostId, now);
                        continue;
                    }

                    var missing = post.Media.FirstOrDefault(m => m.PreparedPath == null || !File.Exists(m.PreparedPath));
                    if (missing != null)
                    {
                        if (!File.Exists(missing.Path))
                        {
                            post.Status = PostStatus.Failed;
                            post.LastError = "The media file is missing: " + missing.Path;
                            post.LastErrorCode = "media.missing";
                            if (post.Content != null) account.RejectedContentKeys.Add(post.Content.Key);
                            s.MarkAccount(account.AccountId);
                            s.Touch(post, now);
                            s.AddEvent(EventLevel.Error, "publish.failed", $"@{account.Username}: {post.LastError}", account.AccountId, post.PostId, now);
                            if (post.Origin == PostOrigin.Autopilot) contentFailures.Add(post.PostId);
                        }
                        else
                        {
                            // The Instagram-ready copy was cleaned up or never made: back to preparing.
                            post.Status = PostStatus.Planned;
                            post.BlockedReason = "Preparing the media for Instagram…";
                            s.Touch(post, now);
                        }
                        continue;
                    }

                    var (blocked, retryAt) = CheckGates(s, post, account, now, publishingEnabled);
                    if (blocked != null)
                    {
                        bool changed = false;
                        if (post.BlockedReason != blocked) { post.BlockedReason = blocked; changed = true; }
                        if (retryAt.HasValue && post.NextAttemptUtc != retryAt)
                        {
                            post.NextAttemptUtc = retryAt;
                            post.Deferrals++;
                            changed = true;
                        }
                        if (changed) s.Touch(post, now);
                        continue;
                    }

                    post.Status = PostStatus.Publishing;
                    post.BlockedReason = null;
                    post.Attempts++;
                    s.Touch(post, now);
                    return new Job { PostId = post.PostId, AccountId = account.AccountId, Username = account.Username, Post = OmniGramStore.Clone(post) };
                }
                return null;
            });
        }

        private static bool IsMissed(OmniGramPost post, OmniGramAccount account, DateTime now, out TimeSpan lateBy)
        {
            lateBy = now - post.ScheduledUtc;
            if (post.Attempts > 0 || post.Deferrals > 0) return false; // already in progress: deferrals/retries are deliberate
            var grace = TimeSpan.FromHours(Math.Clamp(account.Strategy.MissedSlotGraceHours, 0, 72));
            if (account.Strategy.MissedSlots == MissedSlotPolicy.Skip) grace = TimeSpan.FromMinutes(Math.Min(30, grace.TotalMinutes));
            return lateBy > grace && lateBy > TimeSpan.FromMinutes(10);
        }

        /// <summary>Why a due post can't go out now (null = it can), and when to look again.</summary>
        internal (string? Blocked, DateTime? RetryAt) CheckGates(OmniGramState s, OmniGramPost post, OmniGramAccount account, DateTime now, bool publishingEnabled)
        {
            if (!publishingEnabled) return ("Publishing is switched off (OmniSetting OmniGramV2_PublishingEnabled).", null);
            if (!account.IsConnected) return ($"Connect @{account.Username} to Instagram (Settings → Connect an account).", null);
            if (account.Connection == ConnectionState.NeedsReauth) return ($"Reconnect @{account.Username}: {account.ConnectionDetail ?? "Instagram no longer accepts its token."}", null);
            if (account.Connection == ConnectionState.Restricted) return ($"Instagram has restricted @{account.Username}: {account.ConnectionDetail} Resolve it in the Instagram app.", now.AddHours(1));
            if (account.Paused) return ("The account is paused.", null);
            if (account.Health.CooldownUntilUtc is DateTime cooldown && cooldown > now)
                return ($"Cooling down until {cooldown:HH:mm} UTC: {account.Health.CooldownReason}", cooldown);
            if (!post.Approved && account.RequireApproval && post.Origin == PostOrigin.Autopilot) return ("Waiting for approval.", null);
            if (!Uri.TryCreate(mediaHost.BaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
                return ($"The public media URL \"{mediaHost.BaseUrl}\" is not an https:// address Instagram can download from (OmniSetting OmniGram_PublicBaseUrl).", null);

            // Instagram's own rolling 24-hour publishing allowance.
            var since = now.AddHours(-24);
            var ours24h = s.PostsOf(account.AccountId).Where(p => p.Status == PostStatus.Published && p.PublishedUtc >= since).Select(p => p.PublishedUtc!.Value).OrderBy(t => t).ToList();
            int quotaTotal = account.Quota.QuotaTotal ?? 50;
            int quotaUsed = account.Quota.FetchedUtc is DateTime fetched && now - fetched < TimeSpan.FromHours(2)
                ? Math.Max(account.Quota.QuotaUsage ?? 0, ours24h.Count)
                : ours24h.Count;
            if (quotaUsed >= quotaTotal)
            {
                DateTime reset = ours24h.Count > 0 ? ours24h[0].AddHours(24).AddMinutes(2) : now.AddHours(1);
                return ($"Instagram's limit of {quotaTotal} API posts per 24 hours is used up; continues around {reset:HH:mm} UTC.", reset);
            }

            if (!post.ManualOverride && post.Origin == PostOrigin.Autopilot)
            {
                var tz = OmniGramScheduleMath.ResolveTimeZone(account.Strategy.TimeZone);
                var dayStart = OmniGramScheduleMath.LocalDayStartUtc(now, tz);
                int publishedToday = s.PostsOf(account.AccountId).Count(p => p.Status == PostStatus.Published && p.PublishedUtc >= dayStart && !OmniGramPlanner.IsCompanionStory(p));
                int cap = Math.Max(1, account.Strategy.MaxPostsPerDay);
                if (publishedToday >= cap)
                {
                    var nextDay = OmniGramScheduleMath.LocalToUtc(OmniGramScheduleMath.UtcToLocal(now, tz).Date.AddDays(1), tz);
                    return ($"Daily cap of {cap} post(s) reached; continues tomorrow.", nextDay.AddMinutes(5));
                }
                int gap = Math.Clamp(account.Strategy.MinGapMinutes, 0, 24 * 60);
                if (gap > 0 && account.Stats.LastPublishedUtc is DateTime last && now - last < TimeSpan.FromMinutes(gap))
                    return ($"Spacing posts at least {gap} min apart.", last.AddMinutes(gap));
            }
            return (null, null);
        }

        // ─────────────────────────────── Containers ───────────────────────────────

        /// <summary>Creates the post's container(s) unless a usable one already exists.</summary>
        private async Task StartAsync(Job job, CancellationToken ct)
        {
            var p = job.Post;
            DateTime now = clock();
            bool reusable = p.Publish.Stage is PublishStage.Processing or PublishStage.PublishRequested
                && (p.Publish.ContainerId != null || p.Publish.ChildContainerIds.Count > 0)
                && p.Publish.ContainerCreatedUtc is DateTime created && now - created < ContainerLifetime;
            if (reusable)
            {
                Update(job.PostId, post => post.Publish.NextCheckUtc = now);
                return;
            }

            string token, igUserId;
            try
            {
                (token, igUserId) = auth.GetCredentials(job.AccountId);
            }
            catch (AccountUnavailableException ex)
            {
                Release(job.PostId, ex.Message, null);
                return;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                if (p.Format == PostFormat.Carousel)
                {
                    var children = new List<string>();
                    for (int i = 0; i < p.Media.Count; i++)
                    {
                        var m = p.Media[i];
                        var child = new IgContainerRequest { IsCarouselItem = true };
                        if (m.IsVideo) { child.MediaType = "VIDEO"; child.VideoUrl = mediaHost.CreateUrl(p.PostId, i, ".mp4"); }
                        else child.ImageUrl = mediaHost.CreateUrl(p.PostId, i, ".jpg");
                        children.Add(await api.CreateContainerAsync(token, igUserId, child, ct));
                    }
                    now = clock();
                    Update(job.PostId, post =>
                    {
                        post.Publish = new PublishProgress
                        {
                            Stage = PublishStage.Processing,
                            ChildContainerIds = children,
                            ContainerCreatedUtc = now,
                            NextCheckUtc = now.AddSeconds(3),
                            MediaFetches = post.Publish.MediaFetches,
                            LastMediaFetchUtc = post.Publish.LastMediaFetchUtc,
                            LastMediaFetchAgent = post.Publish.LastMediaFetchAgent,
                            DroppedCoverFrame = post.Publish.DroppedCoverFrame,
                        };
                        AddAttempt(post, new PublishAttempt { Utc = now, Ok = true, Step = "carousel items", Code = $"{children.Count} created", DurationMs = sw.ElapsedMilliseconds });
                    });
                    return;
                }

                string containerId = await api.CreateContainerAsync(token, igUserId, BuildRequest(p), ct);
                now = clock();
                Update(job.PostId, post =>
                {
                    post.Publish = new PublishProgress
                    {
                        Stage = PublishStage.Processing,
                        ContainerId = containerId,
                        ContainerCreatedUtc = now,
                        NextCheckUtc = now.AddSeconds(post.IsVideo ? 8 : 2),
                        MediaFetches = post.Publish.MediaFetches,
                        LastMediaFetchUtc = post.Publish.LastMediaFetchUtc,
                        LastMediaFetchAgent = post.Publish.LastMediaFetchAgent,
                        DroppedCoverFrame = post.Publish.DroppedCoverFrame,
                    };
                    AddAttempt(post, new PublishAttempt { Utc = now, Ok = true, Step = "container", Code = "created", DurationMs = sw.ElapsedMilliseconds });
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Release(job.PostId, null, null);
                throw;
            }
            catch (InstagramApiException ex)
            {
                await ApplyFailureAsync(job.PostId, ex, "container", sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                await ApplyFailureAsync(job.PostId, new InstagramApiException(IgErrorKind.Unknown, 0, null, null, ex.Message, "media/create", ex), "container", sw.ElapsedMilliseconds);
            }
        }

        /// <summary>The container request for a single-media post (reel, photo or story).</summary>
        internal IgContainerRequest BuildRequest(OmniGramPost p)
        {
            var m = p.Media[0];
            string caption = IgCaption.ComposeCaption(p.Caption, p.Hashtags, p.HashtagPlacement, p.Credit, p.Footer);
            var request = new IgContainerRequest { IsAiGenerated = p.LabelAsAiGenerated ? true : null };
            switch (p.Format)
            {
                case PostFormat.Reel:
                    request.MediaType = "REELS";
                    request.VideoUrl = mediaHost.CreateUrl(p.PostId, 0, ".mp4");
                    request.Caption = caption;
                    request.ShareToFeed = p.Reel.ShareToFeed;
                    if (p.Reel.CoverFrameSeconds is double cover && !p.Publish.DroppedCoverFrame)
                    {
                        long ms = (long)Math.Round(cover * 1000);
                        if (m.DurationSeconds is double d) ms = Math.Clamp(ms, 0, Math.Max(0, (long)(d * 1000) - 100));
                        request.ThumbOffsetMs = ms;
                    }
                    request.Collaborators = p.Reel.Collaborators.Take(3).ToList();
                    request.TrialGraduationStrategy = p.Reel.Trial switch { TrialReelMode.Manual => "MANUAL", TrialReelMode.Auto => "SS_PERFORMANCE", _ => null };
                    break;
                case PostFormat.Story:
                    request.MediaType = "STORIES";
                    if (m.IsVideo) request.VideoUrl = mediaHost.CreateUrl(p.PostId, 0, ".mp4");
                    else request.ImageUrl = mediaHost.CreateUrl(p.PostId, 0, ".jpg");
                    break;
                default:
                    if (m.IsVideo)
                    {
                        // A single feed video is published as a reel (Instagram retired plain feed videos).
                        request.MediaType = "REELS";
                        request.VideoUrl = mediaHost.CreateUrl(p.PostId, 0, ".mp4");
                        request.ShareToFeed = true;
                    }
                    else
                    {
                        request.ImageUrl = mediaHost.CreateUrl(p.PostId, 0, ".jpg");
                        request.AltText = string.IsNullOrWhiteSpace(p.AltText) ? null : p.AltText.Trim();
                    }
                    request.Caption = caption;
                    break;
            }
            return request;
        }

        // ─────────────────────────────── Processing → published ───────────────────────────────

        /// <summary>Checks a post's container and publishes it when Instagram is done. Returns true when it went live.</summary>
        private async Task<bool> AdvanceAsync(string postId, CancellationToken ct)
        {
            var snap = store.Read(s =>
            {
                var p = s.FindPost(postId);
                if (p == null || p.Status != PostStatus.Publishing) return null;
                var a = s.Account(p.AccountId);
                return a == null ? null : new { Post = OmniGramStore.Clone(p), a.Username, a.Paused, Connection = a.Connection };
            });
            if (snap == null) return false;
            var post = snap.Post;
            DateTime now = clock();
            if (post.Publish.NextCheckUtc is DateTime due && due > now) return false;

            string token, igUserId;
            try
            {
                (token, igUserId) = auth.GetCredentials(post.AccountId);
            }
            catch (AccountUnavailableException ex)
            {
                // The container stays valid for a day: keep it and wait for the reconnect.
                Release(postId, ex.Message, null, keepContainer: true);
                return false;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                // Carousel: wait for every item, then create the album container.
                if (post.Format == PostFormat.Carousel && post.Publish.ContainerId == null)
                {
                    foreach (string child in post.Publish.ChildContainerIds)
                    {
                        var childStatus = await api.GetContainerStatusAsync(token, child, ct);
                        if (childStatus.StatusCode == "IN_PROGRESS") { ScheduleCheck(postId, "IN_PROGRESS", childStatus.Status); return false; }
                        if (childStatus.StatusCode is "ERROR" or "EXPIRED")
                        {
                            var error = childStatus.StatusCode == "EXPIRED"
                                ? new InstagramApiException(IgErrorKind.ContainerExpired, 0, null, null, "A carousel item expired before the album was published.", "container")
                                : InstagramApiException.FromContainerError(child, childStatus.Status);
                            await ApplyFailureAsync(postId, error, "carousel item", sw.ElapsedMilliseconds);
                            return false;
                        }
                    }
                    string album = await api.CreateContainerAsync(token, igUserId, new IgContainerRequest
                    {
                        MediaType = "CAROUSEL",
                        Children = post.Publish.ChildContainerIds.ToList(),
                        Caption = IgCaption.ComposeCaption(post.Caption, post.Hashtags, post.HashtagPlacement, post.Credit, post.Footer),
                        IsAiGenerated = post.LabelAsAiGenerated ? true : null,
                    }, ct);
                    now = clock();
                    Update(postId, p =>
                    {
                        p.Publish.ContainerId = album;
                        p.Publish.NextCheckUtc = now.AddSeconds(3);
                        p.Publish.StatusChecks = 0;
                        AddAttempt(p, new PublishAttempt { Utc = now, Ok = true, Step = "album container", Code = "created", DurationMs = sw.ElapsedMilliseconds });
                    });
                    return false;
                }

                string containerId = post.Publish.ContainerId!;
                var status = await api.GetContainerStatusAsync(token, containerId, ct);
                switch (status.StatusCode)
                {
                    case "PUBLISHED":
                        // Published by an earlier call whose answer was lost: adopt it.
                        return await AdoptPublishedAsync(postId, post, token, igUserId, sw.ElapsedMilliseconds, ct);
                    case "IN_PROGRESS":
                        if (post.Publish.ContainerCreatedUtc is DateTime created && clock() - created > MaxProcessing)
                        {
                            await ApplyFailureAsync(postId, new InstagramApiException(IgErrorKind.ServerError, 0, null, null,
                                $"Instagram was still processing the media after {MaxProcessing.TotalMinutes:0} minutes.", "container"), "processing", sw.ElapsedMilliseconds);
                            return false;
                        }
                        ScheduleCheck(postId, "IN_PROGRESS", status.Status);
                        return false;
                    case "ERROR":
                        await ApplyFailureAsync(postId, InstagramApiException.FromContainerError(containerId, status.Status), "processing", sw.ElapsedMilliseconds);
                        return false;
                    case "EXPIRED":
                        await ApplyFailureAsync(postId, new InstagramApiException(IgErrorKind.ContainerExpired, 0, null, null, "The media container expired before it was published.", "container"), "processing", sw.ElapsedMilliseconds);
                        return false;
                    case "FINISHED":
                        break;
                    default:
                        ScheduleCheck(postId, status.StatusCode, status.Status);
                        return false;
                }

                // FINISHED: last checks before going live (the account may have been paused meanwhile).
                if (!PublishingEnabled() || snap.Paused || snap.Connection is ConnectionState.NeedsReauth or ConnectionState.Restricted)
                {
                    Release(postId, snap.Paused ? "The account is paused." : !PublishingEnabled() ? "Publishing is switched off." : "The account needs attention before posting.", null, keepContainer: true);
                    return false;
                }

                now = clock();
                Update(postId, p =>
                {
                    p.Publish.Stage = PublishStage.PublishRequested;
                    p.Publish.PublishRequestedUtc = now;
                    p.Publish.ContainerStatus = "FINISHED";
                });
                await store.FlushAsync(); // the "publish was requested" mark must survive a crash in the next line

                string mediaId;
                try
                {
                    mediaId = await api.PublishContainerAsync(token, igUserId, containerId, ct);
                }
                catch (InstagramApiException ex) when (ex.Kind == IgErrorKind.NotReady)
                {
                    Update(postId, p => p.Publish.Stage = PublishStage.Processing);
                    ScheduleCheck(postId, "FINISHED", "Instagram asked to wait a moment before publishing.");
                    return false;
                }
                catch (InstagramApiException ex) when (ex.Ambiguous || ex.Kind is IgErrorKind.Timeout or IgErrorKind.Network or IgErrorKind.ServerError)
                {
                    // Maybe it went out. The next check reads the container: PUBLISHED → adopt, FINISHED → publish again.
                    Update(postId, p =>
                    {
                        p.Publish.NextCheckUtc = clock().AddSeconds(20);
                        p.BlockedReason = "Confirming with Instagram after an uncertain answer: " + ex.Message;
                        AddAttempt(p, new PublishAttempt { Utc = clock(), Ok = false, Step = "publish", Code = ex.Code, Message = ex.Message, DurationMs = sw.ElapsedMilliseconds });
                    });
                    return false;
                }
                await ApplySuccessAsync(postId, mediaId, sw.ElapsedMilliseconds, reconciled: false, token, ct);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (InstagramApiException ex) when (ex.Kind is IgErrorKind.Network or IgErrorKind.Timeout or IgErrorKind.ServerError or IgErrorKind.RateLimited)
            {
                // A status check failed: nothing changed on Instagram's side, look again shortly.
                ScheduleCheck(postId, null, ex.Message, ex.RetryAfterUtc ?? clock().AddMinutes(1));
                return false;
            }
            catch (InstagramApiException ex)
            {
                await ApplyFailureAsync(postId, ex, "processing", sw.ElapsedMilliseconds);
                return false;
            }
            catch (Exception ex)
            {
                ScheduleCheck(postId, null, ex.Message, clock().AddMinutes(1));
                return false;
            }
        }

        private void ScheduleCheck(string postId, string? statusCode, string? detail, DateTime? at = null)
        {
            DateTime now = clock();
            Update(postId, p =>
            {
                p.Publish.StatusChecks++;
                if (statusCode != null) p.Publish.ContainerStatus = statusCode;
                p.Publish.ContainerStatusDetail = detail;
                p.Publish.NextCheckUtc = at ?? now + StatusPoll[Math.Min(p.Publish.StatusChecks - 1, StatusPoll.Length - 1)];
                if (statusCode == "IN_PROGRESS") p.BlockedReason = "Instagram is processing the media…";
            });
        }

        /// <summary>
        /// The container is PUBLISHED but the media id never arrived: find the post among the account's newest
        /// media (by time and caption) so links and metrics work. Without a match it still counts as published.
        /// </summary>
        private async Task<bool> AdoptPublishedAsync(string postId, OmniGramPost post, string token, string igUserId, long durationMs, CancellationToken ct)
        {
            string? mediaId = null;
            if (post.Format != PostFormat.Story)
            {
                try
                {
                    var page = await api.GetMediaPageAsync(token, igUserId, null, 12, ct);
                    DateTime since = (post.Publish.PublishRequestedUtc ?? post.Publish.ContainerCreatedUtc ?? post.UpdatedUtc).AddMinutes(-5);
                    string head = Head(IgCaption.ComposeCaption(post.Caption, post.Hashtags, post.HashtagPlacement, post.Credit, post.Footer));
                    mediaId = page.Items.FirstOrDefault(m => m.TimestampUtc >= since && (head.Length == 0 || Head(m.Caption).Equals(head, StringComparison.Ordinal)))?.Id;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { /* still published; sync links it later */ }
            }
            await ApplySuccessAsync(postId, mediaId, durationMs, reconciled: true, token, ct);
            return true;
        }

        private static string Head(string? caption)
        {
            string c = (caption ?? "").Trim();
            return c.Length > 60 ? c[..60] : c;
        }

        private async Task ApplySuccessAsync(string postId, string? mediaId, long durationMs, bool reconciled, string token, CancellationToken ct)
        {
            IgMediaItem? item = null;
            if (mediaId != null)
            {
                try { item = await api.GetMediaItemAsync(token, mediaId, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { /* the permalink is filled in by the next media sync */ }
            }

            DateTime now = clock();
            string? accountId = null;
            bool wantsStory = false;
            store.Mutate(s =>
            {
                var post = s.FindPost(postId);
                if (post == null) return;
                var account = s.Account(post.AccountId);
                accountId = post.AccountId;
                post.Status = PostStatus.Published;
                post.PublishedUtc = item?.TimestampUtc > DateTime.MinValue ? item!.TimestampUtc : now;
                post.IgMediaId = mediaId;
                post.Permalink = item?.Permalink;
                post.Shortcode = item?.Shortcode;
                post.MediaProductType = item?.MediaProductType ?? (post.Format == PostFormat.Story ? "STORY" : post.Format == PostFormat.Reel || post.IsVideo ? "REELS" : "FEED");
                post.Publish.Stage = PublishStage.Published;
                post.Publish.ContainerStatus = "PUBLISHED";
                post.Publish.NextCheckUtc = null;
                post.LastError = null;
                post.LastErrorCode = null;
                post.BlockedReason = null;
                post.NextAttemptUtc = null;
                post.Metrics.Likes = item?.LikeCount ?? 0;
                post.Metrics.Comments = item?.CommentsCount ?? 0;
                post.Metrics.CountsSyncedUtc = now;
                post.Metrics.History.Add(new MetricPoint { Utc = now, Likes = 0, Comments = 0, Views = 0 });
                string? firstComment = IgCaption.ComposeFirstComment(post.FirstComment, post.Hashtags, post.HashtagPlacement);
                post.FirstCommentPending = firstComment != null && mediaId != null;
                post.FirstCommentAttempts = 0;
                if (firstComment != null && mediaId == null) post.FirstCommentError = "The post's id could not be confirmed, so the first comment was not added.";
                AddAttempt(post, new PublishAttempt { Utc = now, Ok = true, Step = "publish", Code = reconciled ? "reconciled" : "published", DurationMs = durationMs,
                    Message = reconciled ? "Found already published after an uncertain answer." : null });
                s.Touch(post, now);
                wantsStory = post.Format == PostFormat.Reel && post.Reel.AlsoPostStory && post.CompanionPostId == null;

                if (account != null)
                {
                    account.Stats.LastPublishedUtc = now;
                    if (account.Stats.MediaCount.HasValue && post.Format != PostFormat.Story) account.Stats.MediaCount++;
                    account.Health.ConsecutiveFailures = 0;
                    account.Health.LastSuccessUtc = now;
                    if (account.Quota.QuotaUsage.HasValue) account.Quota.QuotaUsage++;
                    s.MarkAccount(account.AccountId);
                    if (mediaId != null && item != null && post.Format != PostFormat.Story)
                    {
                        OmniGramInsights.MergeIndex(s.InsightsOf(account.AccountId), new[] { item }, _ => post.PostId, now);
                        s.MarkInsights(account.AccountId);
                    }
                    string where = post.Permalink != null ? " — " + post.Permalink : "";
                    s.AddEvent(EventLevel.Success, "publish.ok",
                        $"@{account.Username}: published a {FormatNoun(post.Format)}{(reconciled ? " (confirmed after an uncertain answer)" : "")}{where}",
                        account.AccountId, post.PostId, now);
                }
            });

            await PostFirstCommentAsync(postId, token, ct);
            if (OnPublished != null) { try { await OnPublished(postId); } catch { } }
        }

        // ─────────────────────────────── First comments ───────────────────────────────

        private async Task PostPendingFirstCommentsAsync(CancellationToken ct)
        {
            DateTime now = clock();
            var pending = store.Read(s => s.AllPosts()
                .Where(p => p.FirstCommentPending && p.Status == PostStatus.Published && (p.NextAttemptUtc == null || p.NextAttemptUtc <= now))
                .Select(p => (p.PostId, p.AccountId)).ToList());
            foreach (var (postId, accountId) in pending)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var (token, _) = auth.GetCredentials(accountId);
                    await PostFirstCommentAsync(postId, token, ct);
                }
                catch (AccountUnavailableException) { }
            }
        }

        private async Task PostFirstCommentAsync(string postId, string token, CancellationToken ct)
        {
            var target = store.Read(s =>
            {
                var p = s.FindPost(postId);
                return p is { FirstCommentPending: true, IgMediaId: not null }
                    ? (MediaId: p.IgMediaId, Text: IgCaption.ComposeFirstComment(p.FirstComment, p.Hashtags, p.HashtagPlacement))
                    : (MediaId: (string?)null, Text: (string?)null);
            });
            if (target.MediaId == null || target.Text == null) return;
            DateTime now = clock();
            try
            {
                string commentId = await api.CreateCommentAsync(token, target.MediaId, target.Text, ct);
                Update(postId, p =>
                {
                    p.FirstCommentPending = false;
                    p.FirstCommentId = commentId;
                    p.FirstCommentError = null;
                    p.NextAttemptUtc = null;
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (InstagramApiException ex)
            {
                bool permanent = ex.Kind is IgErrorKind.PermissionDenied or IgErrorKind.BadRequest or IgErrorKind.InvalidCaption or IgErrorKind.NotFound;
                Update(postId, p =>
                {
                    p.FirstCommentAttempts++;
                    p.FirstCommentError = ex.Kind == IgErrorKind.PermissionDenied
                        ? "Instagram refused the comment: reconnect the account and allow it to manage comments."
                        : ex.Message;
                    if (permanent || p.FirstCommentAttempts >= 3)
                    {
                        p.FirstCommentPending = false;
                        p.NextAttemptUtc = null;
                    }
                    else
                    {
                        p.NextAttemptUtc = now.AddMinutes(5 * p.FirstCommentAttempts);
                    }
                });
            }
        }

        // ─────────────────────────────── Failures ───────────────────────────────

        private async Task ApplyFailureAsync(string postId, InstagramApiException ex, string step, long durationMs)
        {
            DateTime now = clock();
            string? alertMessage = null;
            string? alertKey = null;
            bool contentFailure = false;
            string? accountToReauth = null;
            string? accountToRestrict = null;

            store.Mutate(s =>
            {
                var post = s.FindPost(postId);
                if (post == null) return;
                var account = s.Account(post.AccountId);
                string name = account?.Username ?? "account";
                AddAttempt(post, new PublishAttempt { Utc = now, Ok = false, Step = step, Code = ex.Code, Message = ex.Message, DurationMs = durationMs });
                post.LastError = ex.Message;
                post.LastErrorCode = ex.Code;
                bool countsAsFailure = true;

                void Defer(DateTime retryAt, string reason, bool keepContainer = false)
                {
                    post.Status = PostStatus.Ready;
                    post.Attempts = Math.Max(0, post.Attempts - 1);
                    post.Deferrals++;
                    post.NextAttemptUtc = retryAt;
                    post.BlockedReason = reason;
                    if (!keepContainer) post.Publish.Stage = PublishStage.None;
                    countsAsFailure = false;
                }

                void Retry(string reason)
                {
                    post.Publish.Stage = PublishStage.None; // a fresh container next time
                    if (post.Attempts >= MaxAttempts)
                    {
                        post.Status = PostStatus.Failed;
                        s.AddEvent(EventLevel.Error, "publish.failed", $"@{name}: gave up after {post.Attempts} attempts. Last error ({ex.Code}): {ex.Message}", account?.AccountId, post.PostId, now);
                        alertKey = "failed:" + post.AccountId;
                        alertMessage = $"OmniGram: a post to @{name} failed after {post.Attempts} attempts — {ex.Message}";
                    }
                    else
                    {
                        post.Status = PostStatus.Ready;
                        var wait = Backoff[Math.Min(post.Attempts - 1, Backoff.Length - 1)];
                        post.NextAttemptUtc = now + wait;
                        post.BlockedReason = $"Retrying in {wait.TotalMinutes:0} min after: {reason}";
                        if (post.Attempts == 1)
                            s.AddEvent(EventLevel.Warning, "publish.retry", $"@{name}: publishing failed ({ex.Code}); retrying in {wait.TotalMinutes:0} min. {ex.Message}", account?.AccountId, post.PostId, now);
                    }
                }

                switch (ex.Kind)
                {
                    case IgErrorKind.TokenInvalid:
                    case IgErrorKind.PermissionDenied:
                        Defer(now.AddMinutes(30), $"Reconnect @{name}: Instagram refused its token ({ex.Code}).", keepContainer: true);
                        post.NextAttemptUtc = null;
                        accountToReauth = post.AccountId;
                        alertKey = "reauth:" + post.AccountId;
                        alertMessage = $"OmniGram: Instagram no longer accepts @{name}'s token ({ex.Message}). Posting is paused until you reconnect the account on the OmniGram settings page.";
                        break;

                    case IgErrorKind.AccountRestricted:
                        Defer(now.AddHours(1), $"Instagram has restricted @{name}. Open the Instagram app on that account and complete any checks it asks for.");
                        accountToRestrict = post.AccountId;
                        alertKey = "restricted:" + post.AccountId;
                        alertMessage = $"OmniGram: Instagram restricted @{name} (inactive, checkpointed or restricted). Open the Instagram app on that account and complete any checks; posting resumes automatically.";
                        break;

                    case IgErrorKind.SpamRestricted:
                    {
                        var until = now.AddHours(24);
                        Defer(until, "Instagram flagged the last action as spam; posting backs off for 24 hours.");
                        if (account != null)
                        {
                            account.Health.CooldownUntilUtc = until;
                            account.Health.CooldownReason = "Instagram flagged an action as spam (2207051).";
                            s.MarkAccount(account.AccountId);
                        }
                        s.AddEvent(EventLevel.Error, "publish.spam", $"@{name}: Instagram flagged the post as spam; pausing posts for 24 hours. Consider posting less often.", account?.AccountId, post.PostId, now);
                        alertKey = "spam:" + post.AccountId;
                        alertMessage = $"OmniGram: Instagram flagged a post on @{name} as spam. OmniGram pauses this account's posting for 24 hours; consider a slower schedule.";
                        break;
                    }

                    case IgErrorKind.DailyPublishLimit:
                    {
                        var since = now.AddHours(-24);
                        var oldest = s.PostsOf(post.AccountId).Where(p => p.Status == PostStatus.Published && p.PublishedUtc >= since).Min(p => p.PublishedUtc);
                        var reset = (oldest?.AddHours(24) ?? now.AddHours(2)).AddMinutes(2);
                        if (account != null)
                        {
                            account.Quota.QuotaUsage = account.Quota.QuotaTotal ?? account.Quota.QuotaUsage;
                            account.Quota.FetchedUtc = now;
                            s.MarkAccount(account.AccountId);
                        }
                        Defer(reset, $"Instagram's 24-hour publishing limit was reached; retrying at {reset:HH:mm} UTC.");
                        s.AddEvent(EventLevel.Warning, "publish.limit", $"@{name}: {post.BlockedReason}", account?.AccountId, post.PostId, now);
                        break;
                    }

                    case IgErrorKind.RateLimited:
                        Defer(ex.RetryAfterUtc ?? now.AddMinutes(15), "Instagram's API rate limit; retrying shortly.");
                        break;

                    case IgErrorKind.InvalidCoverFrame when !post.Publish.DroppedCoverFrame:
                        Defer(now.AddSeconds(5), "Instagram refused the cover frame; retrying with its default cover.");
                        post.Publish.DroppedCoverFrame = true;
                        break;

                    case IgErrorKind.InvalidCaption when post.Hashtags.Count > 5 || (post.Caption?.Length ?? 0) > 1500:
                        // Trim once (fewer hashtags, shorter text) and try again before giving up.
                        post.Hashtags = post.Hashtags.Take(5).ToList();
                        if ((post.Caption?.Length ?? 0) > 1500) post.Caption = IgCaption.ClampLength(post.Caption!, 1500);
                        Defer(now.AddSeconds(5), "Instagram refused the caption; retrying with a shorter caption and fewer hashtags.");
                        break;

                    case IgErrorKind.InvalidMedia when post.Media.Any(m => m.PrepareNotes?.StartsWith("remuxed", StringComparison.Ordinal) == true || m.PrepareNotes?.StartsWith("used as is", StringComparison.Ordinal) == true || m.PrepareNotes?.StartsWith("copied", StringComparison.Ordinal) == true):
                        // The copy was only repackaged: convert it fully and try once more.
                        foreach (var m in post.Media)
                        {
                            if (m.PreparedPath != null) TryDelete(m.PreparedPath);
                            m.PreparedPath = null;
                            m.PrepareNotes = null;
                            m.PrepareError = null;
                            m.PrepareAttemptUtc = null;
                        }
                        post.Status = PostStatus.Planned;
                        post.Publish.Stage = PublishStage.None;
                        post.BlockedReason = "Instagram refused the file; converting it fully and retrying.";
                        post.ForceTranscode = true;
                        s.AddEvent(EventLevel.Warning, "publish.reconvert", $"@{name}: Instagram refused the media ({ex.Code}); converting it fully and retrying.", account?.AccountId, post.PostId, now);
                        break;

                    case IgErrorKind.InvalidMedia:
                    case IgErrorKind.InvalidCaption:
                    case IgErrorKind.InvalidCoverFrame:
                    case IgErrorKind.BadRequest:
                        post.Status = PostStatus.Failed;
                        post.Publish.Stage = PublishStage.None;
                        if (post.Content != null && account != null && ex.Kind == IgErrorKind.InvalidMedia)
                        {
                            account.RejectedContentKeys.Add(post.Content.Key);
                            s.MarkAccount(account.AccountId);
                        }
                        contentFailure = post.Origin == PostOrigin.Autopilot && ex.Kind == IgErrorKind.InvalidMedia;
                        s.AddEvent(EventLevel.Error, "publish.failed", $"@{name}: Instagram rejected the post ({ex.Code}): {ex.Message}", account?.AccountId, post.PostId, now);
                        break;

                    case IgErrorKind.MediaFetchFailed:
                        post.Publish.Stage = PublishStage.None;
                        int fetchFailures = post.AttemptLog.Count(a => !a.Ok && a.Code != null && (a.Code.Contains("2207052") || a.Code.StartsWith("9004")));
                        if (fetchFailures >= MaxFetchFailures || post.Attempts >= MaxAttempts)
                        {
                            post.Status = PostStatus.Failed;
                            s.AddEvent(EventLevel.Error, "publish.fetch-failed", $"@{name}: Instagram could not download the media from {mediaHost.BaseUrl} ({fetchFailures}×). Check that the public media URL works from the internet (Settings → Public media URL).", account?.AccountId, post.PostId, now);
                            alertKey = "fetch";
                            alertMessage = $"OmniGram: Instagram cannot download media from {mediaHost.BaseUrl}, so posts fail. Check the public media URL on the OmniGram settings page (OmniSetting OmniGram_PublicBaseUrl).";
                        }
                        else
                        {
                            Retry("Instagram could not download the media from the public URL");
                        }
                        break;

                    default:
                        Retry(ex.Message);
                        break;
                }

                if (account != null)
                {
                    account.Health.LastError = ex.Message;
                    account.Health.LastErrorUtc = now;
                    if (countsAsFailure) account.Health.ConsecutiveFailures++;
                    s.MarkAccount(account.AccountId);
                }
                s.Touch(post, now);
            });

            if (accountToReauth != null) auth.MarkNeedsReauth(accountToReauth, ex.Message);
            if (accountToRestrict != null) auth.MarkRestricted(accountToRestrict, ex.Message);
            if (alertMessage != null && alertKey != null) await AlertOnceAsync(alertKey, alertMessage);
            if (contentFailure) await NotifyContentFailureAsync(postId);
        }

        /// <summary>Returns a claimed post to Ready (the attempt is not counted).</summary>
        private void Release(string postId, string? reason, DateTime? retryAt, bool keepContainer = false)
        {
            DateTime now = clock();
            store.Mutate(s =>
            {
                var post = s.FindPost(postId);
                if (post == null || post.Status != PostStatus.Publishing) return;
                post.Status = PostStatus.Ready;
                post.Attempts = Math.Max(0, post.Attempts - 1);
                post.BlockedReason = reason;
                if (!keepContainer) post.Publish.Stage = PublishStage.None;
                if (retryAt.HasValue) post.NextAttemptUtc = retryAt;
                s.Touch(post, now);
            });
        }

        private void Update(string postId, Action<OmniGramPost> change)
        {
            DateTime now = clock();
            store.Mutate(s =>
            {
                var post = s.FindPost(postId);
                if (post == null) return;
                change(post);
                s.Touch(post, now);
            });
        }

        private static void AddAttempt(OmniGramPost post, PublishAttempt attempt)
        {
            post.AttemptLog.Add(attempt);
            if (post.AttemptLog.Count > 16) post.AttemptLog.RemoveRange(0, post.AttemptLog.Count - 16);
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

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        internal static string FormatNoun(PostFormat format) => format switch
        {
            PostFormat.Reel => "reel",
            PostFormat.Photo => "photo",
            PostFormat.Carousel => "carousel",
            _ => "story",
        };

        internal static string Describe(TimeSpan span) =>
            span.TotalDays >= 1 ? $"{span.TotalDays:0.#} days" : span.TotalHours >= 1 ? $"{span.TotalHours:0.#} h" : $"{Math.Max(1, span.TotalMinutes):0} min";
    }
}
