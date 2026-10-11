using Omnipotent.Services.OmniGram.Models;

namespace Omnipotent.Services.OmniGram.Engine
{
    public sealed class PlanOutcome
    {
        public int Planned { get; set; }
        public int FreeSlots { get; set; }
        public string? Warning { get; set; }
    }

    /// <summary>
    /// Autopilot. <see cref="PlanAccountAsync"/> fills every free schedule slot inside the plan horizon with
    /// content (reserving it so no other slot or account takes it); <see cref="PrepareDueAsync"/> then gets each
    /// planned post ready: probes the media, makes the Instagram-ready copy, renders a preview and writes the
    /// caption. Long work (FFmpeg, LLM calls) happens outside the store lock on cloned data, and results are
    /// applied only if the post is still in the state it was read in.
    /// </summary>
    internal sealed class OmniGramPlanner
    {
        private const int MaxPlannedPerTick = 14;
        private static readonly TimeSpan PrepareRetryAfter = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan[] CaptionBackoff =
        {
            TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(2),
        };

        private readonly OmniGramStore store;
        private readonly OmniGramContentSources content;
        private readonly OmniGramCaptioner captioner;
        private readonly OmniGramMediaTools media;
        private readonly Func<DateTime> clock;

        public OmniGramPlanner(OmniGramStore store, OmniGramContentSources content, OmniGramCaptioner captioner,
            OmniGramMediaTools media, Func<DateTime> clock)
        {
            this.store = store;
            this.content = content;
            this.captioner = captioner;
            this.media = media;
            this.clock = clock;
        }

        /// <summary>Called after a content failure, to fill the slot with something else.</summary>
        public Func<string, Task>? OnContentRejected { get; set; }

        // ─────────────────────────────── Planning ───────────────────────────────

        public async Task<PlanOutcome> PlanAccountAsync(string accountId, CancellationToken ct)
        {
            DateTime now = clock();
            var outcome = new PlanOutcome();
            ReconcileSlots(accountId);

            var snapshot = store.Read(s =>
            {
                var account = s.Account(accountId);
                if (account == null || !account.Autopilot || account.Paused) return null;
                var strategy = OmniGramStore.Clone(account.Strategy);
                if (strategy.Source == ContentSourceKind.None) return null;
                int horizon = Math.Clamp(strategy.PlanAheadDays, 1, 30);
                var slots = OmniGramScheduleMath.SlotsBetween(strategy, now, now.AddDays(horizon));
                var occupied = OccupiedSlots(s.PostsOf(accountId));
                var free = slots.Where(slot => !occupied.Contains(slot)).Take(MaxPlannedPerTick).ToList();
                return new
                {
                    Strategy = strategy,
                    Free = free,
                    Excluded = ExcludedKeys(s, account, strategy) as IReadOnlySet<string>,
                };
            });
            if (snapshot == null || snapshot.Free.Count == 0) return outcome;
            outcome.FreeSlots = snapshot.Free.Count;

            if (snapshot.Strategy.Source == ContentSourceKind.MemeScraper && !content.CatalogReady)
            {
                outcome.Warning = "MemeScraper has not loaded its reels yet.";
                return outcome;
            }

            var candidates = await content.FindCandidatesAsync(snapshot.Strategy, accountId, snapshot.Excluded, snapshot.Free.Count, now, ct);

            store.Mutate(s =>
            {
                var account = s.Account(accountId);
                if (account == null || !account.Autopilot || account.Paused) return;
                var posts = s.PostsOf(accountId);
                var occupied = OccupiedSlots(posts);
                var excludedNow = ExcludedKeys(s, account, account.Strategy);
                int next = 0;
                foreach (var slot in snapshot.Free)
                {
                    if (occupied.Contains(slot)) continue;
                    while (next < candidates.Count && excludedNow.Contains(candidates[next].Key)) next++;
                    if (next >= candidates.Count)
                    {
                        string warning = DescribeShortage(account.Strategy);
                        outcome.Warning = warning;
                        if (account.Health.ContentWarning != warning || account.Health.ContentWarningUtc is not DateTime at || now - at > TimeSpan.FromHours(12))
                        {
                            account.Health.ContentWarning = warning;
                            account.Health.ContentWarningUtc = now;
                            s.MarkAccount(accountId);
                            s.AddEvent(EventLevel.Warning, "content.exhausted", $"@{account.Username}: {warning}", accountId, utc: now);
                        }
                        break;
                    }
                    var candidate = candidates[next++];
                    var post = CreateAutopilotPost(account, candidate, slot, now);
                    posts.Add(post);
                    account.UsedContentKeys.Add(candidate.Key);
                    excludedNow.Add(candidate.Key);
                    occupied.Add(slot);
                    outcome.Planned++;
                    s.MarkPosts(accountId);
                    s.MarkAccount(accountId);
                }
                if (outcome.Planned > 0)
                {
                    if (outcome.Warning == null && account.Health.ContentWarning != null)
                    {
                        account.Health.ContentWarning = null;
                        account.Health.ContentWarningUtc = null;
                    }
                    s.AddEvent(EventLevel.Info, "plan", $"@{account.Username}: planned {outcome.Planned} post(s) for upcoming slots.", accountId, utc: now);
                }
            });
            return outcome;
        }

        internal static OmniGramPost CreateAutopilotPost(OmniGramAccount account, ContentCandidate candidate, DateTime slotUtc, DateTime now)
        {
            var strategy = account.Strategy;
            var format = candidate.IsVideo ? strategy.VideoFormat : strategy.ImageFormat;
            return new OmniGramPost
            {
                AccountId = account.AccountId,
                Origin = PostOrigin.Autopilot,
                Format = format,
                Status = PostStatus.Planned,
                CreatedUtc = now,
                UpdatedUtc = now,
                SlotUtc = slotUtc,
                ScheduledUtc = OmniGramScheduleMath.ApplyJitter(slotUtc, Math.Clamp(strategy.JitterMinutes, 0, 120), account.AccountId),
                Approved = !account.RequireApproval,
                Media = new List<PostMedia>
                {
                    new()
                    {
                        Path = candidate.FilePath,
                        MimeType = OmniGramMediaTools.MimeFor(candidate.FilePath),
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
                Hashtags = ComposeHashtags(strategy, Array.Empty<string>()),
                HashtagPlacement = strategy.HashtagPlacement,
                Reel = OmniGramStore.Clone(strategy.Reels),
                LabelAsAiGenerated = strategy.LabelAsAiGenerated,
                Footer = string.IsNullOrWhiteSpace(strategy.CaptionFooter) ? null : strategy.CaptionFooter,
                Credit = candidate.Source == ContentSourceKind.MemeScraper ? IgCaption.Credit(strategy.CreditTemplate, candidate.Origin) : null,
                CaptionPending = format != PostFormat.Story,
                CaptionInfo = new CaptionInfo { Mode = format == PostFormat.Story ? CaptionMode.None : strategy.CaptionMode },
            };
        }

        /// <summary>Fixed hashtags + a fresh sample of the rotating pool + any AI suggestions, capped at MaxHashtags.</summary>
        public static List<string> ComposeHashtags(OmniGramStrategy strategy, IEnumerable<string> aiTags)
        {
            var tags = new List<string>(strategy.FixedHashtags ?? new());
            var pool = (strategy.RotatingHashtags ?? new()).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            int take = Math.Clamp(strategy.RotatingHashtagsPerPost, 0, pool.Count);
            tags.AddRange(pool.OrderBy(_ => Random.Shared.Next()).Take(take));
            tags.AddRange(aiTags);
            return IgCaption.NormalizeHashtags(tags, Math.Clamp(strategy.MaxHashtags, 0, IgCaption.MaxHashtags));
        }

        private static HashSet<DateTime> OccupiedSlots(IEnumerable<OmniGramPost> posts) =>
            posts.Where(p => p.SlotUtc.HasValue && (p.IsPending || p.Status is PostStatus.Published or PostStatus.Skipped))
                 .Select(p => p.SlotUtc!.Value)
                 .ToHashSet();

        /// <summary>A story queued automatically after its reel was published (it shares the reel's slot).</summary>
        internal static bool IsCompanionStory(OmniGramPost p) => p.Format == PostFormat.Story && p.CompanionPostId != null;

        /// <summary>Content this account must not use: its own history, anything Instagram rejected, and (unless
        /// reuse is allowed) everything other managed accounts have used.</summary>
        internal static HashSet<string> ExcludedKeys(OmniGramState s, OmniGramAccount account, OmniGramStrategy strategy)
        {
            var excluded = new HashSet<string>(account.UsedContentKeys, StringComparer.Ordinal);
            foreach (var other in s.Accounts.Values)
            {
                excluded.UnionWith(other.RejectedContentKeys);
                if (other.AccountId != account.AccountId && !strategy.MemeScraper.AllowReuseAcrossAccounts)
                    excluded.UnionWith(other.UsedContentKeys);
            }
            return excluded;
        }

        internal static string DescribeShortage(OmniGramStrategy strategy) => strategy.Source switch
        {
            ContentSourceKind.MemeScraper => "Ran out of unused MemeScraper reels matching this account's filters. Loosen the filters (niches, sources, age, duration) or add MemeScraper sources.",
            ContentSourceKind.Folder => $"No unused media left in the folder \"{strategy.Folder.Path}\".",
            ContentSourceKind.Library => "The account's content library has no unused media left. Upload more.",
            _ => "No content source is configured.",
        };

        /// <summary>
        /// After a schedule change, moves pending autopilot posts off slots that no longer exist onto free new
        /// slots (keeping their content and captions). Posts with nowhere to go are cancelled and their content
        /// released for reuse.
        /// </summary>
        public int ReconcileSlots(string accountId)
        {
            DateTime now = clock();
            return store.Mutate(s =>
            {
                var account = s.Account(accountId);
                if (account == null) return 0;
                var strategy = account.Strategy;
                int horizon = Math.Clamp(strategy.PlanAheadDays, 1, 30);
                var posts = s.PostsOf(accountId);
                var movable = posts.Where(p => p.Origin == PostOrigin.Autopilot && p.SlotUtc.HasValue && !IsCompanionStory(p)
                        && p.Status is PostStatus.Planned or PostStatus.Ready or PostStatus.AwaitingApproval
                        && p.SlotUtc.Value > now)
                    .OrderBy(p => p.SlotUtc)
                    .ToList();
                if (movable.Count == 0) return 0;
                var validSlots = OmniGramScheduleMath.SlotsBetween(strategy, now, now.AddDays(Math.Max(horizon, 14)));
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
                        post.ScheduledUtc = OmniGramScheduleMath.ApplyJitter(slot, Math.Clamp(strategy.JitterMinutes, 0, 120), account.AccountId);
                        post.NextAttemptUtc = null;
                    }
                    else
                    {
                        post.Status = PostStatus.Cancelled;
                        post.LastError = "Its schedule slot was removed.";
                        if (post.Content != null) account.UsedContentKeys.Remove(post.Content.Key);
                        s.MarkAccount(accountId);
                    }
                    s.Touch(post, now);
                    changed++;
                }
                if (changed > 0)
                    s.AddEvent(EventLevel.Info, "plan.reslot", $"@{account.Username}: moved {changed} planned post(s) to the new schedule.", accountId, utc: now);
                return changed;
            });
        }

        /// <summary>
        /// After an autopilot post failed because of its content (rejected or unusable media), plans a
        /// replacement for the same slot with different content — as long as the slot is still within the
        /// missed-slot grace period. Returns the new post's id, or null when nothing was planned.
        /// </summary>
        public async Task<string?> RefillSlotAsync(string failedPostId, CancellationToken ct)
        {
            DateTime now = clock();
            var snap = store.Read(s =>
            {
                var failed = s.FindPost(failedPostId);
                if (failed == null || failed.Origin != PostOrigin.Autopilot || failed.SlotUtc == null || IsCompanionStory(failed)) return null;
                var account = s.Account(failed.AccountId);
                if (account == null || !account.Autopilot || account.Paused || account.Strategy.Source == ContentSourceKind.None) return null;
                var grace = TimeSpan.FromHours(Math.Clamp(account.Strategy.MissedSlotGraceHours, 0, 72));
                if (now - failed.SlotUtc.Value > grace) return null;
                bool filled = s.PostsOf(account.AccountId).Any(p => p.PostId != failedPostId && p.SlotUtc == failed.SlotUtc && !IsCompanionStory(p)
                    && (p.IsPending || p.Status is PostStatus.Published or PostStatus.Skipped));
                if (filled) return null;
                return new
                {
                    AccountId = account.AccountId,
                    Slot = failed.SlotUtc.Value,
                    Strategy = OmniGramStore.Clone(account.Strategy),
                    Excluded = ExcludedKeys(s, account, account.Strategy) as IReadOnlySet<string>,
                };
            });
            if (snap == null) return null;
            var candidates = await content.FindCandidatesAsync(snap.Strategy, snap.AccountId, snap.Excluded, 3, now, ct);

            return store.Mutate(s =>
            {
                var account = s.Account(snap.AccountId);
                if (account == null) return null;
                var excludedNow = ExcludedKeys(s, account, account.Strategy);
                var candidate = candidates.FirstOrDefault(c => !excludedNow.Contains(c.Key));
                if (candidate == null)
                {
                    s.AddEvent(EventLevel.Warning, "plan.refill", $"@{account.Username}: could not find replacement content for a failed post.", account.AccountId, failedPostId, now);
                    return null;
                }
                var post = CreateAutopilotPost(account, candidate, snap.Slot, now);
                if (post.ScheduledUtc < now.AddMinutes(2)) post.ScheduledUtc = now.AddMinutes(2);
                s.PostsOf(account.AccountId).Add(post);
                account.UsedContentKeys.Add(candidate.Key);
                s.MarkPosts(account.AccountId);
                s.MarkAccount(account.AccountId);
                s.AddEvent(EventLevel.Info, "plan.refill", $"@{account.Username}: replaced a failed post with different content.", account.AccountId, post.PostId, now);
                return post.PostId;
            });
        }

        /// <summary>
        /// Replaces a pending post's content with the next candidate in line (the old content stays marked as
        /// used, so it does not come back). The media is prepared and the caption rewritten for the new content.
        /// </summary>
        public async Task<(bool Ok, string Message)> SwapContentAsync(string postId, CancellationToken ct)
        {
            DateTime now = clock();
            var snap = store.Read(s =>
            {
                var post = s.FindPost(postId);
                if (post == null || !(post.Status is PostStatus.Planned or PostStatus.Ready or PostStatus.AwaitingApproval)) return null;
                var account = s.Account(post.AccountId);
                if (account == null || account.Strategy.Source == ContentSourceKind.None) return null;
                return new
                {
                    AccountId = account.AccountId,
                    Strategy = OmniGramStore.Clone(account.Strategy),
                    Excluded = ExcludedKeys(s, account, account.Strategy) as IReadOnlySet<string>,
                };
            });
            if (snap == null) return (false, "Only a pending post on an account with a content source can have its content swapped.");
            var candidates = await content.FindCandidatesAsync(snap.Strategy, snap.AccountId, snap.Excluded, 2, now, ct);

            return store.Mutate(s =>
            {
                var post = s.FindPost(postId);
                var account = post == null ? null : s.Account(post.AccountId);
                if (post == null || account == null || !(post.Status is PostStatus.Planned or PostStatus.Ready or PostStatus.AwaitingApproval))
                    return (false, "The post changed while new content was being found.");
                var excludedNow = ExcludedKeys(s, account, account.Strategy);
                var candidate = candidates.FirstOrDefault(c => !excludedNow.Contains(c.Key));
                if (candidate == null) return (false, DescribeShortage(account.Strategy));

                var fresh = CreateAutopilotPost(account, candidate, post.SlotUtc ?? post.ScheduledUtc, now);
                post.Format = post.Origin == PostOrigin.Autopilot ? fresh.Format : candidate.IsVideo ? (post.Format == PostFormat.Story ? PostFormat.Story : PostFormat.Reel) : (post.Format == PostFormat.Story ? PostFormat.Story : PostFormat.Photo);
                post.Media = fresh.Media;
                post.Content = fresh.Content;
                post.Credit = fresh.Credit;
                post.Status = PostStatus.Planned;
                var mode = post.Origin == PostOrigin.Autopilot ? account.Strategy.CaptionMode : post.CaptionInfo.Mode;
                if (post.Format == PostFormat.Story) mode = CaptionMode.None;
                post.CaptionPending = mode != CaptionMode.None;
                post.CaptionInfo = new CaptionInfo { Mode = mode };
                post.Caption = null;
                post.AltText = null;
                post.Attempts = 0;
                post.Publish = new PublishProgress();
                post.BlockedReason = null;
                post.LastError = null;
                if (account.RequireApproval) post.Approved = false;
                account.UsedContentKeys.Add(candidate.Key);
                s.MarkAccount(account.AccountId);
                s.Touch(post, now);
                s.AddEvent(EventLevel.Info, "post.swapped", $"@{account.Username}: swapped a planned post's content.", account.AccountId, post.PostId, now);
                return (true, "Swapped; the new media is being prepared and a new caption written.");
            });
        }

        /// <summary>
        /// After a reel is published with "also post as a story", queues the same video as a story a few minutes
        /// later (trimmed to 60 s when preparing). The story bypasses the daily cap and spacing: it is part of
        /// the same post.
        /// </summary>
        public string? CreateCompanionStory(string reelPostId)
        {
            DateTime now = clock();
            return store.Mutate(s =>
            {
                var reel = s.FindPost(reelPostId);
                if (reel == null || reel.Format != PostFormat.Reel || reel.Status != PostStatus.Published || !reel.Reel.AlsoPostStory || reel.CompanionPostId != null) return null;
                var account = s.Account(reel.AccountId);
                if (account == null || reel.Media.Count == 0) return null;
                var story = new OmniGramPost
                {
                    AccountId = reel.AccountId,
                    Origin = reel.Origin,
                    Format = PostFormat.Story,
                    Status = PostStatus.Planned,
                    CreatedUtc = now,
                    UpdatedUtc = now,
                    SlotUtc = reel.SlotUtc,
                    ScheduledUtc = now.AddMinutes(3),
                    Approved = true,
                    ManualOverride = true,
                    Media = new List<PostMedia> { new() { Path = reel.Media[0].Path, MimeType = reel.Media[0].MimeType, Bytes = reel.Media[0].Bytes, DurationSeconds = reel.Media[0].DurationSeconds } },
                    Content = reel.Content == null ? null : OmniGramStore.Clone(reel.Content),
                    CaptionInfo = new CaptionInfo { Mode = CaptionMode.None },
                    CompanionPostId = reel.PostId,
                    LabelAsAiGenerated = reel.LabelAsAiGenerated,
                };
                reel.CompanionPostId = story.PostId;
                s.PostsOf(account.AccountId).Add(story);
                s.Touch(reel, now);
                s.AddEvent(EventLevel.Info, "post.story-companion", $"@{account.Username}: queued the reel as a story too.", account.AccountId, story.PostId, now);
                return story.PostId;
            });
        }

        // ─────────────────────────────── Preparation ───────────────────────────────

        /// <summary>Prepares up to <paramref name="max"/> posts that still need media work or a caption, soonest first.</summary>
        public async Task<int> PrepareDueAsync(int max, CancellationToken ct)
        {
            DateTime now = clock();
            var due = store.Read(s => s.AllPosts()
                .Where(p => p.Status is PostStatus.Planned or PostStatus.Ready or PostStatus.AwaitingApproval)
                .Where(p => p.Status == PostStatus.Planned || p.CaptionPending || p.Media.Any(m => NeedsThumbnail(m, now) || NeedsPreparation(m, p.Format, now)))
                .Where(p => p.CaptionInfo.RetryAfterUtc == null || p.CaptionInfo.RetryAfterUtc <= now || !p.CaptionPending)
                .Where(p => !p.Media.Any(m => m.PrepareError != null && m.PrepareAttemptUtc > now - PrepareRetryAfter) || p.CaptionPending)
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

        /// <summary>The Instagram-ready copy is missing, gone, or was made for another format.</summary>
        internal static bool NeedsPreparation(PostMedia m, PostFormat format, DateTime now)
        {
            bool missing = m.PreparedPath == null || !File.Exists(m.PreparedPath) || m.PreparedFor != format;
            if (!missing) return false;
            return m.PrepareAttemptUtc == null || m.PrepareError == null || now - m.PrepareAttemptUtc > PrepareRetryAfter;
        }

        /// <summary>
        /// Brings one post to Ready: media info, the Instagram-ready copy and a thumbnail, then the caption per
        /// its mode. Returns true when the post changed. With <paramref name="forceRegenerate"/> the caption is
        /// rewritten even if set.
        /// </summary>
        public async Task<bool> PreparePostAsync(string postId, bool forceRegenerate, string? extraInstruction, CancellationToken ct)
        {
            DateTime now = clock();
            var snap = store.Read(s =>
            {
                var post = s.FindPost(postId);
                if (post == null || !(post.Status is PostStatus.Planned or PostStatus.Ready or PostStatus.AwaitingApproval or PostStatus.Draft)) return null;
                var account = s.Account(post.AccountId);
                if (account == null) return null;
                var recent = s.PostsOf(account.AccountId)
                    .Where(p => p.PostId != postId && !string.IsNullOrWhiteSpace(p.Caption) && p.Status is PostStatus.Published or PostStatus.Ready or PostStatus.AwaitingApproval)
                    .OrderByDescending(p => p.PublishedUtc ?? p.ScheduledUtc)
                    .Select(p => p.Caption!)
                    .Take(8)
                    .ToList();
                return new { Post = OmniGramStore.Clone(post), Account = OmniGramStore.Clone(account), Recent = recent };
            });
            if (snap == null) return false;
            var post = snap.Post;
            var account = snap.Account;

            // ── media info, Instagram-ready copies, thumbnails ──
            var mediaUpdates = new List<(int Index, PostMedia Media)>();
            string? permanentFailure = null;
            for (int i = 0; i < post.Media.Count; i++)
            {
                var m = post.Media[i];
                if (!File.Exists(m.Path))
                {
                    permanentFailure ??= "The media file is missing: " + m.Path;
                    continue;
                }
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

                if (NeedsPreparation(m, post.Format, now))
                {
                    m.PrepareAttemptUtc = now;
                    changed = true;
                    string stem = Path.Combine(store.PreparedDirectory, $"{post.PostId}-{i}");
                    var result = await media.PrepareAsync(m.Path, post.Format, post.Format == PostFormat.Carousel, account.Strategy.ImageFit, stem, post.ForceTranscode, ct);
                    if (result.Ok)
                    {
                        m.PreparedPath = result.OutputPath;
                        m.PreparedMimeType = result.MimeType;
                        m.PreparedBytes = result.Bytes;
                        m.PreparedUtc = now;
                        m.PreparedFor = post.Format;
                        m.PrepareNotes = result.Notes;
                        m.PrepareError = null;
                        if (m.DurationSeconds == null && result.DurationSeconds != null) m.DurationSeconds = result.DurationSeconds;
                    }
                    else
                    {
                        m.PrepareError = result.Error;
                        if (result.Permanent) permanentFailure ??= result.Error;
                    }
                }

                if ((m.ThumbnailPath == null || !File.Exists(m.ThumbnailPath))
                    && (m.ThumbnailAttemptUtc == null || now - m.ThumbnailAttemptUtc > TimeSpan.FromHours(6)))
                {
                    m.ThumbnailAttemptUtc = now;
                    changed = true;
                    string thumb = Path.Combine(store.ThumbnailsDirectory, $"{post.PostId}-{i}.jpg");
                    if (await media.CreateThumbnailAsync(m.Path, thumb, m.DurationSeconds, ct))
                        m.ThumbnailPath = thumb;
                    else if (m.ThumbnailPath == null && OmniGramMediaTools.IsImage(m.Path))
                        m.ThumbnailPath = m.Path; // without ffmpeg, an image is its own preview
                }
                if (changed) mediaUpdates.Add((i, m));
            }

            if (permanentFailure != null)
            {
                bool autopilot = post.Origin == PostOrigin.Autopilot;
                store.Mutate(s =>
                {
                    var live = s.FindPost(postId);
                    if (live == null || live.IsTerminal) return;
                    var liveAccount = s.Account(live.AccountId);
                    foreach (var (index, m) in mediaUpdates)
                        if (index < live.Media.Count && live.Media[index].Path == m.Path) live.Media[index] = m;
                    live.Status = PostStatus.Failed;
                    live.LastError = permanentFailure;
                    live.LastErrorCode = "media.unusable";
                    live.BlockedReason = null;
                    if (liveAccount != null && live.Content != null)
                    {
                        liveAccount.RejectedContentKeys.Add(live.Content.Key);
                        s.MarkAccount(liveAccount.AccountId);
                    }
                    s.Touch(live, now);
                    s.AddEvent(EventLevel.Error, "media.unusable", $"@{liveAccount?.Username}: the post's media can't be used on Instagram — {permanentFailure}", live.AccountId, live.PostId, now);
                });
                if (autopilot && OnContentRejected != null) { try { await OnContentRejected(postId); } catch { } }
                return true;
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
                        caption = account.Strategy.FixedCaption ?? "";
                        break;
                    case CaptionMode.Original:
                        caption = OmniGramCaptioner.CleanSourceCaption(post.Content?.OriginalCaption);
                        break;
                    case CaptionMode.Rotate:
                        caption = null; // advanced under the lock below (the cursor is account state)
                        break;
                    case CaptionMode.AI:
                        try
                        {
                            var frames = await FramesForAsync(post, account.Strategy.Ai.UseVision, ct);
                            ai = await captioner.GenerateAiAsync(new CaptionRequest
                            {
                                Settings = account.Strategy.Ai,
                                Username = account.Username,
                                AccountName = account.Name,
                                Biography = account.Biography,
                                Format = post.Format,
                                IsVideo = post.IsVideo,
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
                var liveAccount = s.Account(live.AccountId);
                if (liveAccount == null) return false;
                bool changed = false;

                foreach (var (index, m) in mediaUpdates)
                {
                    if (index >= live.Media.Count || live.Media[index].Path != m.Path) continue;
                    live.Media[index] = m;
                    changed = true;
                }

                if (wantCaption && (forceRegenerate || !live.CaptionInfo.Edited))
                {
                    if (mode == CaptionMode.Rotate) caption = OmniGramCaptioner.NextFromPool(liveAccount);
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
                            // Fixed + rotating (or hand-picked) hashtags were chosen before; AI suggestions fill up to the
                            // strategy's cap. A rewrite replaces the previous suggestions rather than piling up, and never
                            // drops hashtags that were not the AI's.
                            var previous = live.CaptionInfo.SuggestedHashtags ?? new List<string>();
                            var kept = live.Hashtags.Where(t => !previous.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
                            int cap = Math.Clamp(Math.Max(kept.Count, liveAccount.Strategy.MaxHashtags), 0, IgCaption.MaxHashtags);
                            live.Hashtags = IgCaption.NormalizeHashtags(kept.Concat(ai.Hashtags), cap);
                            live.CaptionInfo.Model = ai.Model;
                            live.CaptionInfo.UsedVision = ai.UsedVision;
                            live.CaptionInfo.SuggestedHashtags = ai.Hashtags;
                            if (live.Format is PostFormat.Photo or PostFormat.Carousel && string.IsNullOrWhiteSpace(live.AltText)) live.AltText = ai.AltText;
                        }
                        if (mode == CaptionMode.Rotate) s.MarkAccount(liveAccount.AccountId);
                        changed = true;
                    }
                    else if (error != null)
                    {
                        live.CaptionInfo.Failures++;
                        live.CaptionInfo.Error = error;
                        var wait = CaptionBackoff[Math.Min(live.CaptionInfo.Failures - 1, CaptionBackoff.Length - 1)];
                        live.CaptionInfo.RetryAfterUtc = now + wait;
                        bool deadline = live.ScheduledUtc - now < TimeSpan.FromMinutes(20) || live.CaptionInfo.Failures >= 4;
                        if (deadline && liveAccount.Strategy.Ai.Fallback != CaptionFallback.Hold)
                        {
                            live.Caption = liveAccount.Strategy.Ai.Fallback == CaptionFallback.Fixed ? liveAccount.Strategy.FixedCaption ?? "" : "";
                            live.CaptionPending = false;
                            s.AddEvent(EventLevel.Warning, "caption.fallback",
                                $"@{liveAccount.Username}: AI caption failed {live.CaptionInfo.Failures}×; using the {liveAccount.Strategy.Ai.Fallback.ToString().ToLowerInvariant()} fallback. ({error})",
                                liveAccount.AccountId, postId, now);
                        }
                        else if (live.CaptionInfo.Failures == 1)
                        {
                            s.AddEvent(EventLevel.Warning, "caption.failed", $"@{liveAccount.Username}: AI caption failed; retrying in {wait.TotalMinutes:0} min. ({error})", liveAccount.AccountId, postId, now);
                        }
                        live.BlockedReason = live.CaptionPending ? "Waiting for an AI caption: " + error : null;
                        changed = true;
                    }
                }

                var unprepared = live.Media.FirstOrDefault(m => m.PreparedPath == null || m.PreparedFor != live.Format);
                if (unprepared != null)
                {
                    string reason = unprepared.PrepareError != null ? "Preparing the media for Instagram failed: " + unprepared.PrepareError : "Preparing the media for Instagram…";
                    if (live.BlockedReason != reason && !(live.CaptionPending && live.BlockedReason?.StartsWith("Waiting for an AI caption") == true))
                    {
                        live.BlockedReason = reason;
                        changed = true;
                    }
                    // A Ready post whose prepared copy went missing goes back to preparing.
                    if (live.Status == PostStatus.Ready) { live.Status = PostStatus.Planned; changed = true; }
                }
                else if (live.Status == PostStatus.Planned && !live.CaptionPending)
                {
                    live.Status = liveAccount.RequireApproval && !live.Approved && live.Origin == PostOrigin.Autopilot ? PostStatus.AwaitingApproval : PostStatus.Ready;
                    if (live.BlockedReason?.StartsWith("Waiting for an AI caption") == true || live.BlockedReason?.StartsWith("Preparing the media") == true) live.BlockedReason = null;
                    changed = true;
                }
                if (changed) s.Touch(live, now);
                return changed;
            });
        }

        /// <summary>Frames for a vision caption: four from a video, or the (JPEG) preview of an image.</summary>
        internal async Task<List<byte[]>> FramesForAsync(OmniGramPost post, bool useVision, CancellationToken ct)
        {
            var frames = new List<byte[]>();
            if (!useVision) return frames;
            var video = post.Media.FirstOrDefault(m => m.IsVideo && File.Exists(m.Path));
            if (video != null) return await media.ExtractFramesAsync(video.Path, 4, 512, video.DurationSeconds, ct);
            foreach (var image in post.Media.Where(m => !m.IsVideo).Take(4))
            {
                string? jpeg = new[] { image.ThumbnailPath, image.PreparedPath, image.Path }
                    .FirstOrDefault(p => p != null && File.Exists(p) && (p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)));
                if (jpeg != null) frames.Add(await File.ReadAllBytesAsync(jpeg, ct));
            }
            return frames;
        }
    }
}
