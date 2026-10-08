using Omnipotent.Services.OmniTumblr.Api;
using Omnipotent.Services.OmniTumblr.Models;

namespace Omnipotent.Services.OmniTumblr.Engine
{
    public sealed class PlanOutcome
    {
        public int Planned { get; set; }
        public int FreeSlots { get; set; }
        public string? Warning { get; set; }
    }

    /// <summary>
    /// Autopilot. <see cref="PlanBlogAsync"/> fills every free schedule slot inside the plan horizon with
    /// content (reserving it so no other slot or blog takes it); <see cref="PrepareDueAsync"/> then gets
    /// each planned post ready: probes the media, renders a preview thumbnail and writes the caption.
    /// Long work (ffprobe, LLM calls) happens outside the store lock on cloned data, and results are
    /// applied only if the post is still in the state it was read in.
    /// </summary>
    internal sealed class OmniTumblrPlanner
    {
        private const int MaxPlannedPerTick = 14;
        private static readonly TimeSpan[] CaptionBackoff =
        {
            TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(2),
        };

        private readonly OmniTumblrStore store;
        private readonly OmniTumblrContentSources content;
        private readonly OmniTumblrCaptioner captioner;
        private readonly OmniTumblrMediaTools media;
        private readonly Func<DateTime> clock;

        public OmniTumblrPlanner(OmniTumblrStore store, OmniTumblrContentSources content, OmniTumblrCaptioner captioner,
            OmniTumblrMediaTools media, Func<DateTime> clock)
        {
            this.store = store;
            this.content = content;
            this.captioner = captioner;
            this.media = media;
            this.clock = clock;
        }

        // ─────────────────────────────── Planning ───────────────────────────────

        public async Task<PlanOutcome> PlanBlogAsync(string blogId, CancellationToken ct)
        {
            DateTime now = clock();
            var outcome = new PlanOutcome();
            ReconcileSlots(blogId);

            var snapshot = store.Read(s =>
            {
                var blog = s.Blog(blogId);
                if (blog == null || !blog.Autopilot || blog.Paused) return null;
                var strategy = OmniTumblrStore.Clone(blog.Strategy);
                if (strategy.Source == ContentSourceKind.None) return null;
                int horizon = Math.Clamp(strategy.PlanAheadDays, 1, 30);
                var slots = OmniTumblrScheduleMath.SlotsBetween(strategy, now, now.AddDays(horizon));
                var occupied = OccupiedSlots(s.PostsOf(blogId));
                var free = slots.Where(slot => !occupied.Contains(slot)).Take(MaxPlannedPerTick).ToList();
                return new
                {
                    Strategy = strategy,
                    Free = free,
                    Excluded = ExcludedKeys(s, blog, strategy) as IReadOnlySet<string>,
                    RequireApproval = blog.RequireApproval,
                };
            });
            if (snapshot == null || snapshot.Free.Count == 0) return outcome;
            outcome.FreeSlots = snapshot.Free.Count;

            if (snapshot.Strategy.Source == ContentSourceKind.MemeScraper && !content.CatalogReady)
            {
                outcome.Warning = "MemeScraper has not loaded its reels yet.";
                return outcome;
            }

            var candidates = await content.FindCandidatesAsync(snapshot.Strategy, blogId, snapshot.Excluded, snapshot.Free.Count, now, ct);

            store.Mutate(s =>
            {
                var blog = s.Blog(blogId);
                if (blog == null || !blog.Autopilot || blog.Paused) return;
                var posts = s.PostsOf(blogId);
                var occupied = OccupiedSlots(posts);
                var excludedNow = ExcludedKeys(s, blog, blog.Strategy);
                int next = 0;
                foreach (var slot in snapshot.Free)
                {
                    if (occupied.Contains(slot)) continue;
                    while (next < candidates.Count && excludedNow.Contains(candidates[next].Key)) next++;
                    if (next >= candidates.Count)
                    {
                        string warning = DescribeShortage(blog.Strategy);
                        outcome.Warning = warning;
                        if (blog.Health.ContentWarning != warning || blog.Health.ContentWarningUtc is not DateTime at || now - at > TimeSpan.FromHours(12))
                        {
                            blog.Health.ContentWarning = warning;
                            blog.Health.ContentWarningUtc = now;
                            s.MarkBlog(blogId);
                            s.AddEvent(EventLevel.Warning, "content.exhausted", $"@{blog.Name}: {warning}", blogId, utc: now);
                        }
                        break;
                    }
                    var candidate = candidates[next++];
                    var post = CreateAutopilotPost(blog, candidate, slot, now);
                    posts.Add(post);
                    blog.UsedContentKeys.Add(candidate.Key);
                    excludedNow.Add(candidate.Key);
                    occupied.Add(slot);
                    outcome.Planned++;
                    s.MarkPosts(blogId);
                    s.MarkBlog(blogId);
                }
                if (outcome.Planned > 0)
                {
                    if (outcome.Warning == null && blog.Health.ContentWarning != null)
                    {
                        blog.Health.ContentWarning = null;
                        blog.Health.ContentWarningUtc = null;
                    }
                    s.AddEvent(EventLevel.Info, "plan", $"@{blog.Name}: planned {outcome.Planned} post(s) for upcoming slots.", blogId, utc: now);
                }
            });
            return outcome;
        }

        private static OmniTumblrPost CreateAutopilotPost(OmniTumblrBlog blog, ContentCandidate candidate, DateTime slotUtc, DateTime now)
        {
            var strategy = blog.Strategy;
            var post = new OmniTumblrPost
            {
                BlogId = blog.BlogId,
                Origin = PostOrigin.Autopilot,
                Kind = candidate.Kind,
                Status = PostStatus.Planned,
                CreatedUtc = now,
                UpdatedUtc = now,
                SlotUtc = slotUtc,
                ScheduledUtc = OmniTumblrScheduleMath.ApplyJitter(slotUtc, Math.Clamp(strategy.JitterMinutes, 0, 120), blog.BlogId),
                Approved = !blog.RequireApproval,
                TumblrState = strategy.PostState,
                Media = new List<PostMedia>
                {
                    new()
                    {
                        Path = candidate.FilePath,
                        MimeType = OmniTumblrMediaTools.MimeFor(candidate.FilePath),
                        Bytes = candidate.Bytes,
                        DurationSeconds = candidate.DurationSeconds,
                    },
                },
                Content = new ContentRef
                {
                    Kind = candidate.Source,
                    Key = candidate.Key,
                    Origin = candidate.Origin,
                    OriginalCaption = candidate.OriginalCaption,
                    OriginalUrl = candidate.OriginalUrl,
                    Views = candidate.Views,
                    Likes = candidate.Likes,
                    CreatedUtc = candidate.CreatedUtc,
                },
                SourceUrl = strategy.CreditSource ? candidate.OriginalUrl : null,
                Tags = ComposeTags(strategy, Array.Empty<string>()),
                CaptionPending = true,
                CaptionInfo = new CaptionInfo { Mode = strategy.CaptionMode },
            };
            return post;
        }

        /// <summary>Fixed tags + a fresh sample of the rotating pool + any AI suggestions, capped at MaxTags.</summary>
        public static List<string> ComposeTags(OmniTumblrStrategy strategy, IEnumerable<string> aiTags)
        {
            var tags = new List<string>(strategy.FixedTags ?? new());
            var pool = (strategy.RotatingTags ?? new()).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            int take = Math.Clamp(strategy.RotatingTagsPerPost, 0, pool.Count);
            tags.AddRange(pool.OrderBy(_ => Random.Shared.Next()).Take(take));
            tags.AddRange(aiTags);
            return TumblrNpf.NormalizeTags(tags, Math.Clamp(strategy.MaxTags, 1, TumblrNpf.MaxTags));
        }

        private static HashSet<DateTime> OccupiedSlots(IEnumerable<OmniTumblrPost> posts) =>
            posts.Where(p => p.SlotUtc.HasValue && (p.IsPending || p.Status is PostStatus.Published or PostStatus.Skipped))
                 .Select(p => p.SlotUtc!.Value)
                 .ToHashSet();

        /// <summary>Content this blog must not use: its own history, anything Tumblr rejected, and (unless reuse
        /// is allowed) everything other managed blogs have used.</summary>
        internal static HashSet<string> ExcludedKeys(OmniTumblrState s, OmniTumblrBlog blog, OmniTumblrStrategy strategy)
        {
            var excluded = new HashSet<string>(blog.UsedContentKeys, StringComparer.Ordinal);
            foreach (var other in s.Blogs.Values)
            {
                excluded.UnionWith(other.RejectedContentKeys);
                if (other.BlogId != blog.BlogId && !strategy.MemeScraper.AllowReuseAcrossBlogs)
                    excluded.UnionWith(other.UsedContentKeys);
            }
            return excluded;
        }

        private static string DescribeShortage(OmniTumblrStrategy strategy) => strategy.Source switch
        {
            ContentSourceKind.MemeScraper => "Ran out of unused MemeScraper reels matching this blog's filters. Loosen the filters (niches, sources, age, duration) or add MemeScraper sources.",
            ContentSourceKind.Folder => $"No unused media left in the folder \"{strategy.Folder.Path}\".",
            ContentSourceKind.Library => "The blog's content library has no unused media left. Upload more.",
            _ => "No content source is configured.",
        };

        /// <summary>
        /// After a schedule change, moves pending autopilot posts off slots that no longer exist onto free new
        /// slots (keeping their content and captions). Posts with nowhere to go are cancelled and their content
        /// released for reuse.
        /// </summary>
        public int ReconcileSlots(string blogId)
        {
            DateTime now = clock();
            return store.Mutate(s =>
            {
                var blog = s.Blog(blogId);
                if (blog == null) return 0;
                var strategy = blog.Strategy;
                int horizon = Math.Clamp(strategy.PlanAheadDays, 1, 30);
                var posts = s.PostsOf(blogId);
                var movable = posts.Where(p => p.Origin == PostOrigin.Autopilot && p.SlotUtc.HasValue
                        && p.Status is PostStatus.Planned or PostStatus.Ready or PostStatus.AwaitingApproval
                        && p.SlotUtc.Value > now)
                    .OrderBy(p => p.SlotUtc)
                    .ToList();
                if (movable.Count == 0) return 0;
                var validSlots = OmniTumblrScheduleMath.SlotsBetween(strategy, now, now.AddDays(Math.Max(horizon, 14)));
                var valid = validSlots.ToHashSet();
                var orphans = movable.Where(p => !valid.Contains(p.SlotUtc!.Value)).ToList();
                if (orphans.Count == 0) return 0;

                var occupied = OccupiedSlots(posts.Where(p => !orphans.Contains(p)));
                var freeSlots = new Queue<DateTime>(validSlots.Where(x => !occupied.Contains(x)));
                int changed = 0;
                foreach (var post in orphans)
                {
                    if (freeSlots.Count > 0)
                    {
                        var slot = freeSlots.Dequeue();
                        post.SlotUtc = slot;
                        post.ScheduledUtc = OmniTumblrScheduleMath.ApplyJitter(slot, Math.Clamp(strategy.JitterMinutes, 0, 120), blog.BlogId);
                        post.NextAttemptUtc = null;
                    }
                    else
                    {
                        post.Status = PostStatus.Cancelled;
                        post.LastError = "Its schedule slot was removed.";
                        if (post.Content != null) blog.UsedContentKeys.Remove(post.Content.Key);
                        s.MarkBlog(blogId);
                    }
                    s.Touch(post, now);
                    changed++;
                }
                if (changed > 0)
                    s.AddEvent(EventLevel.Info, "plan.reslot", $"@{blog.Name}: moved {changed} planned post(s) to the new schedule.", blogId, utc: now);
                return changed;
            });
        }

        /// <summary>
        /// After an autopilot post failed because of its content (rejected or missing media), plans a
        /// replacement for the same slot with different content — as long as the slot is still within the
        /// missed-slot grace period. Returns the new post's id, or null when nothing was planned.
        /// </summary>
        public async Task<string?> RefillSlotAsync(string failedPostId, CancellationToken ct)
        {
            DateTime now = clock();
            var snap = store.Read(s =>
            {
                var failed = s.FindPost(failedPostId);
                if (failed == null || failed.Origin != PostOrigin.Autopilot || failed.SlotUtc == null) return null;
                var blog = s.Blog(failed.BlogId);
                if (blog == null || !blog.Autopilot || blog.Paused || blog.Strategy.Source == ContentSourceKind.None) return null;
                var grace = TimeSpan.FromHours(Math.Clamp(blog.Strategy.MissedSlotGraceHours, 0, 72));
                if (now - failed.SlotUtc.Value > grace) return null;
                bool filled = s.PostsOf(blog.BlogId).Any(p => p.PostId != failedPostId && p.SlotUtc == failed.SlotUtc
                    && (p.IsPending || p.Status is PostStatus.Published or PostStatus.Skipped));
                if (filled) return null;
                return new
                {
                    BlogId = blog.BlogId,
                    Slot = failed.SlotUtc.Value,
                    Strategy = OmniTumblrStore.Clone(blog.Strategy),
                    Excluded = ExcludedKeys(s, blog, blog.Strategy) as IReadOnlySet<string>,
                };
            });
            if (snap == null) return null;
            var candidates = await content.FindCandidatesAsync(snap.Strategy, snap.BlogId, snap.Excluded, 3, now, ct);

            return store.Mutate(s =>
            {
                var blog = s.Blog(snap.BlogId);
                if (blog == null) return null;
                var excludedNow = ExcludedKeys(s, blog, blog.Strategy);
                var candidate = candidates.FirstOrDefault(c => !excludedNow.Contains(c.Key));
                if (candidate == null)
                {
                    s.AddEvent(EventLevel.Warning, "plan.refill", $"@{blog.Name}: could not find replacement content for a failed post.", blog.BlogId, failedPostId, now);
                    return null;
                }
                var post = CreateAutopilotPost(blog, candidate, snap.Slot, now);
                if (post.ScheduledUtc < now.AddMinutes(2)) post.ScheduledUtc = now.AddMinutes(2);
                s.PostsOf(blog.BlogId).Add(post);
                blog.UsedContentKeys.Add(candidate.Key);
                s.MarkPosts(blog.BlogId);
                s.MarkBlog(blog.BlogId);
                s.AddEvent(EventLevel.Info, "plan.refill", $"@{blog.Name}: replaced a failed post with different content.", blog.BlogId, post.PostId, now);
                return post.PostId;
            });
        }

        /// <summary>
        /// Replaces a pending post's content with the next candidate in line (the old content stays marked
        /// as used, so it does not come back). The caption is regenerated for the new content.
        /// </summary>
        public async Task<(bool Ok, string Message)> SwapContentAsync(string postId, CancellationToken ct)
        {
            DateTime now = clock();
            var snap = store.Read(s =>
            {
                var post = s.FindPost(postId);
                if (post == null || !(post.Status is PostStatus.Planned or PostStatus.Ready or PostStatus.AwaitingApproval)) return null;
                var blog = s.Blog(post.BlogId);
                if (blog == null || blog.Strategy.Source == ContentSourceKind.None) return null;
                return new
                {
                    BlogId = blog.BlogId,
                    Strategy = OmniTumblrStore.Clone(blog.Strategy),
                    Excluded = ExcludedKeys(s, blog, blog.Strategy) as IReadOnlySet<string>,
                };
            });
            if (snap == null) return (false, "Only a pending post on a blog with a content source can have its content swapped.");
            var candidates = await content.FindCandidatesAsync(snap.Strategy, snap.BlogId, snap.Excluded, 2, now, ct);

            return store.Mutate(s =>
            {
                var post = s.FindPost(postId);
                var blog = post == null ? null : s.Blog(post.BlogId);
                if (post == null || blog == null || !(post.Status is PostStatus.Planned or PostStatus.Ready or PostStatus.AwaitingApproval))
                    return (false, "The post changed while new content was being found.");
                var excludedNow = ExcludedKeys(s, blog, blog.Strategy);
                var candidate = candidates.FirstOrDefault(c => !excludedNow.Contains(c.Key));
                if (candidate == null) return (false, DescribeShortage(blog.Strategy));

                var fresh = CreateAutopilotPost(blog, candidate, post.SlotUtc ?? post.ScheduledUtc, now);
                post.Kind = fresh.Kind;
                post.Media = fresh.Media;
                post.Content = fresh.Content;
                post.SourceUrl = fresh.SourceUrl;
                post.Status = PostStatus.Planned;
                post.CaptionPending = post.CaptionInfo.Mode != CaptionMode.None || blog.Strategy.CaptionMode != CaptionMode.None;
                post.CaptionInfo = new CaptionInfo { Mode = post.Origin == PostOrigin.Autopilot ? blog.Strategy.CaptionMode : post.CaptionInfo.Mode };
                post.Caption = null;
                post.Slug = null;
                post.Attempts = 0;
                post.NeedsReconcile = false;
                post.BlockedReason = null;
                post.LastError = null;
                if (blog.RequireApproval) post.Approved = false;
                blog.UsedContentKeys.Add(candidate.Key);
                s.MarkBlog(blog.BlogId);
                s.Touch(post, now);
                s.AddEvent(EventLevel.Info, "post.swapped", $"@{blog.Name}: swapped a planned post's content.", blog.BlogId, post.PostId, now);
                return (true, "Swapped; a new caption is being written.");
            });
        }

        // ─────────────────────────────── Preparation ───────────────────────────────

        /// <summary>Prepares up to <paramref name="max"/> posts that still need media info or a caption, soonest first.</summary>
        public async Task<int> PrepareDueAsync(int max, CancellationToken ct)
        {
            DateTime now = clock();
            var due = store.Read(s => s.AllPosts()
                .Where(p => p.Status is PostStatus.Planned or PostStatus.Ready or PostStatus.AwaitingApproval)
                .Where(p => p.Status == PostStatus.Planned || p.CaptionPending || p.Media.Any(m => NeedsThumbnail(m, now)))
                .Where(p => p.CaptionInfo.RetryAfterUtc == null || p.CaptionInfo.RetryAfterUtc <= now || !p.CaptionPending)
                .OrderBy(p => p.ScheduledUtc)
                .Select(p => p.PostId)
                .Take(max)
                .ToList());
            int prepared = 0;
            foreach (string postId in due)
            {
                if (ct.IsCancellationRequested) break;
                if (await PreparePostAsync(postId, forceRegenerate: false, extraInstruction: null, ct)) prepared++;
            }
            return prepared;
        }

        /// <summary>A preview is missing and was not attempted in the last six hours (FFmpeg may be absent).</summary>
        private static bool NeedsThumbnail(PostMedia m, DateTime now) =>
            m.ThumbnailPath == null && (m.ThumbnailAttemptUtc == null || now - m.ThumbnailAttemptUtc > TimeSpan.FromHours(6)) && File.Exists(m.Path);

        /// <summary>
        /// Brings one post to Ready: media info + thumbnail, then the caption per its mode. Returns true when
        /// the post changed. With <paramref name="forceRegenerate"/> the caption is rewritten even if set.
        /// </summary>
        public async Task<bool> PreparePostAsync(string postId, bool forceRegenerate, string? extraInstruction, CancellationToken ct)
        {
            DateTime now = clock();
            var snap = store.Read(s =>
            {
                var post = s.FindPost(postId);
                if (post == null || !(post.Status is PostStatus.Planned or PostStatus.Ready or PostStatus.AwaitingApproval or PostStatus.Draft)) return null;
                var blog = s.Blog(post.BlogId);
                if (blog == null) return null;
                var recent = s.PostsOf(blog.BlogId)
                    .Where(p => p.PostId != postId && !string.IsNullOrWhiteSpace(p.Caption) && p.Status is PostStatus.Published or PostStatus.Ready or PostStatus.AwaitingApproval)
                    .OrderByDescending(p => p.PublishedUtc ?? p.ScheduledUtc)
                    .Select(p => p.Caption!)
                    .Take(8)
                    .ToList();
                return new { Post = OmniTumblrStore.Clone(post), Blog = OmniTumblrStore.Clone(blog), Recent = recent };
            });
            if (snap == null) return false;
            var post = snap.Post;
            var blog = snap.Blog;

            // ── media info + thumbnails ──
            var mediaUpdates = new List<(int Index, PostMedia Media)>();
            for (int i = 0; i < post.Media.Count; i++)
            {
                var m = post.Media[i];
                if (!File.Exists(m.Path)) continue;
                bool changed = false;
                if (m.Width == null || (m.IsVideo && m.DurationSeconds == null))
                {
                    var probe = await media.ProbeAsync(m.Path, ct);
                    if (probe != null)
                    {
                        m.Width = probe.Width ?? m.Width;
                        m.Height = probe.Height ?? m.Height;
                        m.DurationSeconds = probe.DurationSeconds ?? m.DurationSeconds;
                        changed = true;
                    }
                }
                if (m.Bytes == 0) { try { m.Bytes = new FileInfo(m.Path).Length; changed = true; } catch { } }
                if ((m.ThumbnailPath == null || !File.Exists(m.ThumbnailPath))
                    && (m.ThumbnailAttemptUtc == null || now - m.ThumbnailAttemptUtc > TimeSpan.FromHours(6)))
                {
                    m.ThumbnailAttemptUtc = now;
                    changed = true;
                    string thumb = Path.Combine(store.ThumbnailsDirectory, $"{post.PostId}-{i}.jpg");
                    if (await media.CreateThumbnailAsync(m.Path, thumb, m.DurationSeconds, ct))
                    {
                        m.ThumbnailPath = thumb;
                        changed = true;
                    }
                    else if (m.ThumbnailPath == null && OmniTumblrMediaTools.IsImage(m.Path))
                    {
                        m.ThumbnailPath = m.Path; // without ffmpeg, an image is its own preview
                        changed = true;
                    }
                }
                if (changed) mediaUpdates.Add((i, m));
            }

            // ── caption ──
            bool wantCaption = forceRegenerate || (post.CaptionPending && !post.CaptionInfo.Edited);
            string? caption = null;
            CaptionResult? ai = null;
            string? error = null;
            var mode = post.CaptionInfo.Mode;
            if (wantCaption)
            {
                switch (mode)
                {
                    case CaptionMode.None:
                        caption = "";
                        break;
                    case CaptionMode.Fixed:
                        caption = blog.Strategy.FixedCaption ?? "";
                        break;
                    case CaptionMode.Original:
                        caption = OmniTumblrCaptioner.CleanSourceCaption(post.Content?.OriginalCaption);
                        break;
                    case CaptionMode.Rotate:
                        caption = null; // advanced under the lock below (the cursor is blog state)
                        break;
                    case CaptionMode.AI:
                        try
                        {
                            var frames = new List<byte[]>();
                            var video = post.Media.FirstOrDefault(m => m.IsVideo && File.Exists(m.Path));
                            if (blog.Strategy.Ai.UseVision)
                            {
                                if (video != null)
                                    frames = await media.ExtractFramesAsync(video.Path, 4, 512, video.DurationSeconds, ct);
                                else if (post.Media.FirstOrDefault(m => !m.IsVideo && File.Exists(m.ThumbnailPath ?? m.Path)) is PostMedia image)
                                {
                                    string preview = image.ThumbnailPath ?? image.Path;
                                    if (preview.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || preview.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                                        frames.Add(await File.ReadAllBytesAsync(preview, ct));
                                }
                            }
                            ai = await captioner.GenerateAiAsync(new CaptionRequest
                            {
                                Settings = blog.Strategy.Ai,
                                BlogName = blog.Name,
                                BlogTitle = blog.Title,
                                BlogDescription = blog.Description,
                                Kind = post.Kind,
                                OriginalCaption = post.Content?.OriginalCaption,
                                Origin = post.Content?.Origin,
                                RecentCaptions = snap.Recent,
                                Frames = frames,
                                ExtraInstruction = extraInstruction ?? post.CaptionInfo.Direction,
                            }, ct);
                            caption = ai.Caption;
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            error = ex.Message;
                        }
                        break;
                }
            }

            return store.Mutate(s =>
            {
                var live = s.FindPost(postId);
                if (live == null || !(live.Status is PostStatus.Planned or PostStatus.Ready or PostStatus.AwaitingApproval or PostStatus.Draft)) return false;
                var liveBlog = s.Blog(live.BlogId);
                if (liveBlog == null) return false;
                bool changed = false;

                foreach (var (index, m) in mediaUpdates)
                {
                    if (index >= live.Media.Count || live.Media[index].Path != m.Path) continue;
                    live.Media[index] = m;
                    changed = true;
                }

                if (wantCaption && (forceRegenerate || !live.CaptionInfo.Edited))
                {
                    if (mode == CaptionMode.Rotate) caption = OmniTumblrCaptioner.NextFromPool(liveBlog);
                    if (caption != null)
                    {
                        live.Caption = caption;
                        live.CaptionPending = false;
                        live.CaptionInfo.Error = null;
                        live.CaptionInfo.RetryAfterUtc = null;
                        live.CaptionInfo.GeneratedUtc = now;
                        live.CaptionInfo.Edited = false;
                        if (ai != null)
                        {
                            live.CaptionInfo.Model = ai.Model;
                            live.CaptionInfo.UsedVision = ai.UsedVision;
                            live.CaptionInfo.AltText = ai.AltText;
                            live.CaptionInfo.SuggestedTags = ai.Tags;
                            // Fixed + rotating tags were chosen at planning time; AI suggestions fill the rest.
                            if (ai.Tags.Count > 0)
                                live.Tags = TumblrNpf.NormalizeTags(live.Tags.Concat(ai.Tags), Math.Clamp(liveBlog.Strategy.MaxTags, 1, TumblrNpf.MaxTags));
                        }
                        if (mode == CaptionMode.Rotate) s.MarkBlog(liveBlog.BlogId);
                        changed = true;
                    }
                    else if (error != null)
                    {
                        live.CaptionInfo.Failures++;
                        live.CaptionInfo.Error = error;
                        var wait = CaptionBackoff[Math.Min(live.CaptionInfo.Failures - 1, CaptionBackoff.Length - 1)];
                        live.CaptionInfo.RetryAfterUtc = now + wait;
                        bool deadline = live.ScheduledUtc - now < TimeSpan.FromMinutes(20) || live.CaptionInfo.Failures >= 4;
                        if (deadline && liveBlog.Strategy.Ai.Fallback != CaptionFallback.Hold)
                        {
                            live.Caption = liveBlog.Strategy.Ai.Fallback == CaptionFallback.Fixed ? liveBlog.Strategy.FixedCaption ?? "" : "";
                            live.CaptionPending = false;
                            s.AddEvent(EventLevel.Warning, "caption.fallback",
                                $"@{liveBlog.Name}: AI caption failed {live.CaptionInfo.Failures}×; using the {liveBlog.Strategy.Ai.Fallback.ToString().ToLowerInvariant()} fallback. ({error})",
                                liveBlog.BlogId, postId, now);
                        }
                        else if (live.CaptionInfo.Failures == 1)
                        {
                            s.AddEvent(EventLevel.Warning, "caption.failed", $"@{liveBlog.Name}: AI caption failed; retrying in {wait.TotalMinutes:0} min. ({error})", liveBlog.BlogId, postId, now);
                        }
                        live.BlockedReason = live.CaptionPending ? "Waiting for an AI caption: " + error : null;
                        changed = true;
                    }
                }

                if (live.Status == PostStatus.Planned && !live.CaptionPending)
                {
                    live.Status = liveBlog.RequireApproval && !live.Approved ? PostStatus.AwaitingApproval : PostStatus.Ready;
                    if (live.BlockedReason?.StartsWith("Waiting for an AI caption") == true) live.BlockedReason = null;
                    changed = true;
                }
                if (changed) s.Touch(live, now);
                return changed;
            });
        }
    }
}
