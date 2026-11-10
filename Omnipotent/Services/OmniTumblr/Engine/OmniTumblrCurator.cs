using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniTumblr.Api;
using Omnipotent.Services.OmniTumblr.Models;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.OmniTumblr.Engine
{
    /// <summary>
    /// Curated reblogs. A few times a day, for each blog that has them switched on, picks a well-liked post
    /// from the tags the blog lives in and queues a reblog of it.
    ///
    /// Why: in November 2026 the three blogs had posted for a month and reblogged nothing. On Tumblr a reblog
    /// is how a blog takes part: the original poster is told, the blog's name sits in the post's notes, and
    /// the blog's page is more than one account's uploads. Liking and following in bulk were left out on
    /// purpose: that is the pattern Tumblr treats as spam.
    ///
    /// These are other people's posts going onto Klives's blogs, so every pick is screened first:
    /// <list type="bullet">
    /// <item>hard rules: an original post (not a reblog), not ours, recent, already liked by enough people, not
    /// marked mature, reblogs allowed, not already reblogged by any of our blogs, and not a blog this one
    /// reblogged in the last week;</item>
    /// <item>then a model looks at the post (its picture, text and tags) and says whether it fits the blog and
    /// is safe to put there. When unsure it says no, and a post it turned down is never asked about again;</item>
    /// <item>a post the model could not look at (a video, or the picture would not load) is always held for
    /// approval, and by default every pick is (<see cref="ReblogSettings.RequireApproval"/>).</item>
    /// </list>
    /// Tumblr's listing gives a tag's posts newest first only, so candidates are found by reading pages from
    /// several hours and days back, where posts have had time to collect notes.
    /// </summary>
    internal sealed class OmniTumblrCurator
    {
        /// <summary>How far ahead a reblog is picked. Short, so what goes out is still fresh.</summary>
        public static readonly TimeSpan LeadTime = TimeSpan.FromHours(4);
        /// <summary>A reblog time that passed this long ago with nothing picked is let go.</summary>
        public static readonly TimeSpan SlotGrace = TimeSpan.FromMinutes(20);
        public static readonly TimeSpan ApprovalExpiresAfter = TimeSpan.FromHours(48);
        private static readonly TimeSpan CurateEachBlogEvery = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan ListingFreshFor = TimeSpan.FromHours(3);
        private static readonly TimeSpan SameSourceCooldown = TimeSpan.FromDays(7);
        /// <summary>How far back each page of a tag's listing starts: first the hours where posts have just peaked.</summary>
        private static readonly int[] PageHoursBack = { 6, 14, 26, 10, 20, 38, 50, 62 };
        private const int FirstPages = 3;
        private const int MaxTagsPerPass = 6;
        private const int MaxScreensPerPass = 6;
        private const int MaxHistory = 800;
        private const int MaxPictureBytes = 8 * 1024 * 1024;
        private const int MaxPictureBytesForModel = 1_500_000;

        private readonly OmniTumblrStore store;
        private readonly ITumblrApi api;
        private readonly OmniTumblrAuth auth;
        private readonly ICaptionModel model;
        private readonly OmniTumblrMediaTools media;
        private readonly Func<DateTime> clock;
        private readonly OmniTumblrApiBudget? budget;
        private readonly TumblrEdgeGate? edgeGate;
        private readonly Action<string> log;
        private readonly ConcurrentDictionary<string, DateTime> lastCuratedUtc = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, DateTime> lastShortfallEventUtc = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Listing> listings = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim passLock = new(1, 1);

        private sealed class Listing
        {
            public DateTime FetchedUtc;
            public int PagesRead;
            public Dictionary<string, TumblrPostSummary> Posts = new(StringComparer.Ordinal);
        }

        public OmniTumblrCurator(OmniTumblrStore store, ITumblrApi api, OmniTumblrAuth auth, ICaptionModel model, OmniTumblrMediaTools media, Func<DateTime> clock,
            OmniTumblrApiBudget? budget = null, TumblrEdgeGate? edgeGate = null, Action<string>? log = null)
        {
            this.store = store;
            this.api = api;
            this.auth = auth;
            this.model = model;
            this.media = media;
            this.clock = clock;
            this.budget = budget;
            this.edgeGate = edgeGate;
            this.log = log ?? (_ => { });
        }

        /// <summary>The OmniSetting OmniTumblr_ReblogsEnabled.</summary>
        public Func<bool> Enabled { get; set; } = () => true;

        /// <summary>Downloads a post's picture. Only Tumblr's own media hosts are fetched.</summary>
        public Func<string, CancellationToken, Task<byte[]?>> FetchPicture { get; set; } = DownloadPictureAsync;

        private bool CanLookUp => auth.AppOnlyCredentials() != null && edgeGate?.IsBlocked != true && budget?.AllowBackground() != false;

        // ─────────────────────────────── When ───────────────────────────────

        /// <summary>
        /// The blog's reblog times on one local day: <see cref="ReblogSettings.PerDay"/> of them, spread evenly
        /// between the start and end hour, each nudged by a few minutes that stay the same for that blog and day.
        /// </summary>
        public static List<DateTime> SlotsOn(ReblogSettings settings, TimeZoneInfo tz, string blogId, DateTime localDate)
        {
            int perDay = Math.Clamp(settings.PerDay, 1, 12);
            int start = Math.Clamp(settings.StartHour, 0, 23);
            int end = Math.Clamp(settings.EndHour, 1, 24);
            if (end <= start) { start = 9; end = 23; }
            double spacing = (end - start) * 60.0 / perDay;
            int nudge = (int)Math.Min(25, spacing / 4);
            var slots = new List<DateTime>();
            for (int i = 0; i < perDay; i++)
            {
                int jitter = nudge == 0 ? 0 : (int)(OmniTumblrScheduleMath.Fnv1a($"reblog|{blogId}|{localDate:yyyyMMdd}|{i}") % (uint)(2 * nudge + 1)) - nudge;
                var local = localDate.Date.AddMinutes(start * 60 + spacing * (i + 0.5) + jitter);
                slots.Add(OmniTumblrScheduleMath.LocalToUtc(local, tz));
            }
            return slots;
        }

        /// <summary>Makes the next pass look at this blog at once (its reblog settings changed).</summary>
        public void Expedite(string blogId) => lastCuratedUtc.TryRemove(blogId, out _);

        /// <summary>Picks reblogs for every blog that is due a look. Returns how many were queued.</summary>
        public async Task<int> CurateDueAsync(CancellationToken ct)
        {
            if (!Enabled()) return 0;
            if (!await passLock.WaitAsync(0, ct)) return 0;
            try
            {
                DateTime now = clock();
                var blogIds = store.Read(s => s.Blogs.Values
                    .Where(b => b.Autopilot && !b.Paused && b.Strategy.Reblogs?.Enabled == true)
                    .Select(b => b.BlogId).ToList());
                int queued = 0;
                foreach (string blogId in blogIds)
                {
                    if (ct.IsCancellationRequested) break;
                    if (lastCuratedUtc.TryGetValue(blogId, out var last) && now - last < CurateEachBlogEvery) continue;
                    lastCuratedUtc[blogId] = now;
                    try { queued += await CurateBlogAsync(blogId, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { log($"OmniTumblr curator: @{blogId} failed: {ex.Message}"); }
                }
                return queued;
            }
            finally
            {
                passLock.Release();
            }
        }

        // ─────────────────────────────── One blog ───────────────────────────────

        internal sealed class BlogView
        {
            public string BlogId = "", Name = "";
            public string? Title, Description;
            public OmniTumblrStrategy Strategy = new();
            public List<DateTime> FreeSlots = new();
            public HashSet<string> OwnBlogs = new(StringComparer.OrdinalIgnoreCase);
            /// <summary>Posts no blog of ours may reblog (again), and ones this blog already considered.</summary>
            public HashSet<string> Excluded = new(StringComparer.Ordinal);
            public HashSet<string> RecentSources = new(StringComparer.OrdinalIgnoreCase);
            public List<string> Tags = new();
        }

        private BlogView? View(string blogId, DateTime now, bool forSwap = false) => store.Mutate(s =>
        {
            var blog = s.Blog(blogId);
            if (blog == null || blog.Paused || !blog.Autopilot || blog.Strategy.Reblogs?.Enabled != true) return null;
            var settings = blog.Strategy.Reblogs;
            var posts = s.PostsOf(blogId);

            // Picks nobody approved in two days are stale: let them go.
            foreach (var stale in posts.Where(p => p.IsReblog && p.Status == PostStatus.AwaitingApproval && now - p.CreatedUtc > ApprovalExpiresAfter).ToList())
            {
                stale.Status = PostStatus.Cancelled;
                stale.LastError = "Not approved within two days.";
                s.Touch(stale, now);
            }

            var view = new BlogView
            {
                BlogId = blogId, Name = blog.Name, Title = blog.Title, Description = blog.Description,
                Strategy = OmniTumblrStore.Clone(blog.Strategy),
                OwnBlogs = s.Blogs.Values.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
            };

            var tz = OmniTumblrScheduleMath.ResolveTimeZone(blog.Strategy.TimeZone);
            var today = OmniTumblrScheduleMath.UtcToLocal(now, tz).Date;
            var taken = posts.Where(p => p.Reblog?.SlotUtc != null).Select(p => p.Reblog!.SlotUtc!.Value).ToHashSet();
            view.FreeSlots = SlotsOn(settings, tz, blogId, today).Concat(SlotsOn(settings, tz, blogId, today.AddDays(1)))
                .Where(slot => slot >= now - SlotGrace && slot <= now + LeadTime && !taken.Contains(slot))
                .OrderBy(slot => slot)
                .ToList();
            // With approval on, don't keep piling picks onto a queue nobody is clearing.
            int waiting = posts.Count(p => p.IsReblog && p.Status == PostStatus.AwaitingApproval);
            int room = Math.Max(0, Math.Clamp(settings.PerDay, 1, 12) * 2 - waiting);
            if (view.FreeSlots.Count > room) view.FreeSlots = view.FreeSlots.Take(room).ToList();
            if (view.FreeSlots.Count == 0 && !forSwap) return view;

            foreach (var other in s.Blogs.Values)
            {
                foreach (var p in s.PostsOf(other.BlogId))
                    if (p.Reblog != null && p.Status != PostStatus.Cancelled) view.Excluded.Add(p.Reblog.ParentPostId);
                foreach (var (id, decision) in other.ReblogHistory)
                    if (other.BlogId == blogId || decision.Outcome == "taken") view.Excluded.Add(id);
            }
            view.RecentSources = posts.Where(p => p.Reblog != null && p.Status is not (PostStatus.Cancelled or PostStatus.Failed) && now - (p.PublishedUtc ?? p.ScheduledUtc) < SameSourceCooldown)
                .Select(p => p.Reblog!.ParentBlogName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var tags = (settings.Tags is { Count: > 0 } chosen ? chosen : (blog.Strategy.FixedTags ?? new()).Concat(blog.Strategy.RotatingTags ?? new()))
                .Select(OmniTumblrTagMath.Key).Where(t => t.Length > 0).Distinct()
                // A tag nobody else posts under has nothing to reblog.
                .Where(t => !(s.Tags.TryGetValue(t, out var research) && research.Verdict == TagVerdict.Dead))
                .ToList();
            // Start from a different tag each time, so one busy tag doesn't supply every reblog.
            if (tags.Count > 1)
            {
                int offset = (int)((OmniTumblrScheduleMath.Fnv1a(blogId) + (uint)(now.Ticks / TimeSpan.FromHours(3).Ticks)) % (uint)tags.Count);
                tags = tags.Skip(offset).Concat(tags.Take(offset)).ToList();
            }
            view.Tags = tags;
            return view;
        });

        /// <summary>Fills the blog's upcoming reblog times. Returns how many reblogs were queued.</summary>
        public async Task<int> CurateBlogAsync(string blogId, CancellationToken ct)
        {
            if (!Enabled()) return 0;
            DateTime now = clock();
            var view = View(blogId, now);
            if (view == null || view.FreeSlots.Count == 0) return 0;
            if (view.Tags.Count == 0)
            {
                Shortfall(view, "It has no tags to look in: add some under Reblogs, or fixed tags, in the strategy.", now);
                return 0;
            }
            if (!CanLookUp || !await model.IsAvailableAsync(ct)) return 0; // nothing goes out unscreened; try again next pass

            var picks = await PickAsync(view, view.FreeSlots.Count, ct);
            if (picks.Count == 0)
            {
                // Nothing passed (or the model gave no usable answers): look again in an hour, not every ten minutes.
                lastCuratedUtc[blogId] = now.AddMinutes(50);
                Shortfall(view, $"Nothing in {string.Join(", ", view.Tags.Take(MaxTagsPerPass).Select(t => "#" + t))} had {view.Strategy.Reblogs.MinNotes}+ notes and passed screening.", now);
                return 0;
            }

            return store.Mutate(s =>
            {
                var blog = s.Blog(blogId);
                if (blog == null || blog.Paused || !blog.Autopilot || blog.Strategy.Reblogs?.Enabled != true) return 0;
                var posts = s.PostsOf(blogId);
                var taken = posts.Where(p => p.Reblog?.SlotUtc != null).Select(p => p.Reblog!.SlotUtc!.Value).ToHashSet();
                int queued = 0;
                foreach (var (slot, pick) in view.FreeSlots.Where(slot => !taken.Contains(slot)).Zip(picks))
                {
                    var post = NewReblog(blog, pick, slot, now);
                    posts.Add(post);
                    queued++;
                    s.AddEvent(EventLevel.Info, "reblog.picked",
                        $"@{blog.Name}: picked @{pick.Post.BlogName}'s post from #{pick.Tag} ({pick.Post.NoteCount} notes) to reblog at {slot:HH:mm} UTC{(post.Status == PostStatus.AwaitingApproval ? "; waiting for approval" : "")}.",
                        blogId, post.PostId, now);
                }
                if (queued > 0) s.MarkPosts(blogId);
                return queued;
            });
        }

        private static OmniTumblrPost NewReblog(OmniTumblrBlog blog, Pick pick, DateTime slot, DateTime now)
        {
            bool hold = blog.Strategy.Reblogs.RequireApproval || !pick.Seen;
            return new OmniTumblrPost
            {
                BlogId = blog.BlogId,
                Origin = PostOrigin.Autopilot,
                Kind = PostKind.Reblog,
                Status = hold ? PostStatus.AwaitingApproval : PostStatus.Ready,
                Approved = !hold,
                CreatedUtc = now,
                UpdatedUtc = now,
                ScheduledUtc = slot < now ? now : slot,
                // Tumblr lists only original posts under a tag, so tags on a reblog are for the blog's own archive.
                Tags = new List<string> { pick.Tag },
                TumblrState = blog.Strategy.PostState,
                CaptionPending = false,
                CaptionInfo = new CaptionInfo { Mode = CaptionMode.None },
                Reblog = RefFor(pick, slot),
            };
        }

        private static ReblogRef RefFor(Pick pick, DateTime? slot) => new()
        {
            ParentPostId = pick.Post.Id,
            ParentBlogUuid = pick.Post.BlogUuid ?? "",
            ParentBlogName = pick.Post.BlogName ?? "",
            ReblogKey = pick.Post.ReblogKey ?? "",
            ParentUrl = pick.Post.PostUrl ?? (pick.Post.BlogName == null ? null : TumblrApiClient.BuildPostUrl(pick.Post.BlogName, pick.Post.Id)),
            Summary = pick.Post.Summary,
            ParentTags = pick.Post.Tags.Take(12).ToList(),
            ParentNotes = pick.Post.NoteCount,
            ParentPublishedUtc = pick.Post.PublishedUtc,
            ParentType = pick.Post.Type is "video" or "photo" ? pick.Post.Type : "text",
            FoundUnderTag = pick.Tag,
            SlotUtc = slot,
            PreviewPath = pick.PreviewPath,
            SeenByModel = pick.Seen,
            Reason = pick.Seen ? pick.Reason : "Not seen by the screening model (" + pick.Unseen + "), so it waits for approval. " + pick.Reason,
        };

        /// <summary>Swaps a queued reblog for the next good candidate ("pick another"); the first is not offered again.</summary>
        public async Task<(bool Ok, string Message)> ReplaceAsync(string postId, CancellationToken ct)
        {
            await passLock.WaitAsync(ct); // one search at a time: the listings are shared
            try { return await ReplaceCoreAsync(postId, ct); }
            finally { passLock.Release(); }
        }

        private async Task<(bool Ok, string Message)> ReplaceCoreAsync(string postId, CancellationToken ct)
        {
            DateTime now = clock();
            var target = store.Read(s => s.FindPost(postId) is { IsReblog: true, Reblog: not null } p && p.Status is PostStatus.Ready or PostStatus.AwaitingApproval
                ? (p.BlogId, Parent: p.Reblog.ParentPostId, Source: p.Reblog.ParentBlogName) : default);
            if (target.BlogId == null) return (false, "Only a queued reblog can be swapped for another.");
            store.Mutate(s =>
            {
                if (s.Blog(target.BlogId) is OmniTumblrBlog blog) Remember(s, blog, target.Parent, "rejected", "Swapped out by hand.", target.Source, now);
            });
            var view = View(target.BlogId, now, forSwap: true);
            if (view == null) return (false, "Curated reblogs are switched off for this blog.");
            if (view.Tags.Count == 0) return (false, "The blog has no tags to look in.");
            if (!CanLookUp) return (false, "Tumblr cannot be asked right now; try again in a few minutes.");
            if (!await model.IsAvailableAsync(ct)) return (false, "The screening model is not available, and nothing goes out unscreened.");

            var picks = await PickAsync(view, 1, ct);
            if (picks.Count == 0) return (false, $"Nothing else with {view.Strategy.Reblogs.MinNotes}+ notes passed screening just now.");
            return store.Mutate(s =>
            {
                var post = s.FindPost(postId);
                var blog = post == null ? null : s.Blog(post.BlogId);
                if (post == null || blog == null || !post.IsReblog || !(post.Status is PostStatus.Ready or PostStatus.AwaitingApproval))
                    return (false, "The post changed while another was being found.");
                var pick = picks[0];
                bool hold = blog.Strategy.Reblogs.RequireApproval || !pick.Seen;
                post.Reblog = RefFor(pick, post.Reblog?.SlotUtc);
                post.Tags = new List<string> { pick.Tag };
                post.Status = hold ? PostStatus.AwaitingApproval : PostStatus.Ready;
                post.Approved = !hold;
                post.Slug = null;
                post.Attempts = 0;
                post.LastError = null;
                post.BlockedReason = null;
                s.Touch(post, now);
                s.AddEvent(EventLevel.Info, "reblog.picked", $"@{blog.Name}: swapped a queued reblog for @{pick.Post.BlogName}'s post from #{pick.Tag}.", blog.BlogId, postId, now);
                return (true, $"Now @{pick.Post.BlogName}'s post from #{pick.Tag}.");
            });
        }

        // ─────────────────────────────── Finding ───────────────────────────────

        private sealed record Pick(TumblrPostSummary Post, string Tag, bool Seen, string Unseen, string Reason, string? PreviewPath);

        /// <summary>Up to <paramref name="needed"/> posts that passed every rule and the model, best first.</summary>
        private async Task<List<Pick>> PickAsync(BlogView view, int needed, CancellationToken ct)
        {
            DateTime now = clock();
            var settings = view.Strategy.Reblogs;
            var picks = new List<Pick>();
            var considered = new HashSet<string>(view.Excluded, StringComparer.Ordinal);
            var sources = new HashSet<string>(view.RecentSources, StringComparer.OrdinalIgnoreCase);
            int screens = 0;

            foreach (string tag in view.Tags.Take(MaxTagsPerPass))
            {
                if (picks.Count >= needed || screens >= MaxScreensPerPass) break;
                for (int round = 0; round < 2 && picks.Count < needed && screens < MaxScreensPerPass; round++)
                {
                    // First the pages where posts have just peaked; the rest only if those gave too little.
                    var listing = await ListingAsync(tag, round == 0 ? FirstPages : PageHoursBack.Length, now, ct);
                    var candidates = listing
                        .Where(p => Eligible(p, view, settings, now) && !considered.Contains(p.Id) && !sources.Contains(p.BlogName ?? ""))
                        // Notes for its age: a post with 200 notes in three hours beats one with 500 in three days.
                        .OrderByDescending(p => p.NoteCount / Math.Sqrt((now - p.PublishedUtc).TotalHours + 2))
                        .ToList();
                    foreach (var candidate in candidates)
                    {
                        if (picks.Count >= needed || screens >= MaxScreensPerPass) break;
                        if (sources.Contains(candidate.BlogName ?? "")) continue;
                        considered.Add(candidate.Id);
                        screens++;
                        var verdict = await ScreenAsync(view, candidate, tag, ct);
                        if (verdict == null) continue; // the model gave no usable answer: not held against the post
                        if (!verdict.Ok)
                        {
                            store.Mutate(s => { if (s.Blog(view.BlogId) is OmniTumblrBlog blog) Remember(s, blog, candidate.Id, "rejected", verdict.Reason, candidate.BlogName, now); });
                            continue;
                        }
                        picks.Add(new Pick(candidate, tag, verdict.Seen, verdict.Unseen, verdict.Reason, verdict.PreviewPath));
                        sources.Add(candidate.BlogName ?? "");
                    }
                    if (listing.Count == 0) break;
                }
            }
            return picks;
        }

        internal static bool Eligible(TumblrPostSummary p, BlogView view, ReblogSettings settings, DateTime now)
        {
            if (string.IsNullOrEmpty(p.Id) || string.IsNullOrEmpty(p.ReblogKey) || string.IsNullOrEmpty(p.BlogUuid) || string.IsNullOrEmpty(p.BlogName)) return false;
            if (p.IsReblog || p.Mature || !p.ReblogsOpen) return false;
            if (view.OwnBlogs.Contains(p.BlogName)) return false;
            if (p.PublishedUtc == DateTime.MinValue || now - p.PublishedUtc > TimeSpan.FromHours(Math.Clamp(settings.MaxAgeHours, 1, 24 * 30))) return false;
            if (p.NoteCount < Math.Max(0, settings.MinNotes)) return false;
            return p.Type switch
            {
                "video" => settings.Videos,
                "photo" => settings.Images,
                _ => settings.Text,
            };
        }

        /// <summary>A tag's posts from the last few days, read a page at a time from several points back and kept for three hours.</summary>
        private async Task<List<TumblrPostSummary>> ListingAsync(string tag, int pages, DateTime now, CancellationToken ct)
        {
            var listing = listings.GetOrAdd(tag, _ => new Listing { FetchedUtc = now });
            if (now - listing.FetchedUtc > ListingFreshFor)
            {
                listing = new Listing { FetchedUtc = now };
                listings[tag] = listing;
            }
            var creds = auth.AppOnlyCredentials();
            while (creds != null && listing.PagesRead < Math.Min(pages, PageHoursBack.Length) && CanLookUp)
            {
                long before = new DateTimeOffset(listing.FetchedUtc.AddHours(-PageHoursBack[listing.PagesRead])).ToUnixTimeSeconds();
                listing.PagesRead++;
                List<TumblrPostSummary> page;
                try { page = await api.GetTaggedAsync(creds, tag, before, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (TumblrApiException) { break; }
                foreach (var p in page)
                    if (p.Id.Length > 0) listing.Posts[p.Id] = p;
            }
            foreach (string stale in listings.Where(kv => now - kv.Value.FetchedUtc > ListingFreshFor + ListingFreshFor).Select(kv => kv.Key).ToList())
                listings.TryRemove(stale, out _);
            return listing.Posts.Values.ToList();
        }

        private static void Remember(OmniTumblrState s, OmniTumblrBlog blog, string parentPostId, string outcome, string? reason, string? source, DateTime now)
        {
            blog.ReblogHistory[parentPostId] = new ReblogDecision { Utc = now, Outcome = outcome, Reason = reason, SourceBlog = source };
            if (blog.ReblogHistory.Count > MaxHistory)
                foreach (string old in blog.ReblogHistory.OrderBy(kv => kv.Value.Utc).Take(blog.ReblogHistory.Count - MaxHistory + 50).Select(kv => kv.Key).ToList())
                    blog.ReblogHistory.Remove(old);
            s.MarkBlog(blog.BlogId);
        }

        /// <summary>Says once a day why a blog's reblog times are going unfilled.</summary>
        private void Shortfall(BlogView view, string why, DateTime now)
        {
            if (lastShortfallEventUtc.TryGetValue(view.BlogId, out var last) && now - last < TimeSpan.FromHours(24)) return;
            lastShortfallEventUtc[view.BlogId] = now;
            store.Mutate(s => s.AddEvent(EventLevel.Warning, "reblog.none", $"@{view.Name}: found nothing to reblog. {why}", view.BlogId, utc: now));
        }

        // ─────────────────────────────── Screening ───────────────────────────────

        internal sealed record Verdict(bool Ok, string Reason, bool Seen, string Unseen, string? PreviewPath);

        /// <summary>
        /// Asks the model whether the post fits the blog and is safe to put on it. Null when no usable answer
        /// came back. <c>Seen</c> is false when the post has a picture or video the model was not shown.
        /// </summary>
        internal async Task<Verdict?> ScreenAsync(BlogView view, TumblrPostSummary post, string tag, CancellationToken ct)
        {
            DateTime now = clock();
            bool video = post.Type == "video";
            bool hasPicture = video || post.Type == "photo" || post.Images.Count > 0;
            byte[]? picture = null;
            string? previewPath = null;
            string unseen = "";
            if (hasPicture)
            {
                (picture, previewPath) = await LoadPictureAsync(post, ct);
                if (picture == null) unseen = "its picture could not be loaded";
                else if (!await model.SupportsImagesAsync(view.Strategy.Ai.Model, ct))
                {
                    picture = null;
                    unseen = "the model cannot look at pictures";
                }
            }
            // A poster frame is one frame: what the rest of a video shows and says is unknown.
            if (video && unseen.Length == 0) unseen = "only one frame of a video can be shown to it";

            string system = BuildScreenSystemPrompt(view);
            string user = BuildScreenUserPrompt(post, tag, now, picture != null, video);
            try
            {
                var reply = await model.CompleteAsync(system, user, picture == null ? Array.Empty<byte[]>() : new[] { picture }, view.Strategy.Ai.Model, 400, ct);
                var parsed = ParseVerdict(reply.Text);
                if (parsed == null) return null;
                return new Verdict(parsed.Value.Ok, parsed.Value.Reason, hasPicture ? picture != null && !video : true, unseen, previewPath);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log($"OmniTumblr curator: screening failed: {ex.Message}");
                return null;
            }
        }

        internal static string BuildScreenSystemPrompt(BlogView view)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"You screen posts for the Tumblr blog \"{view.Name}\"{(string.IsNullOrWhiteSpace(view.Title) ? "" : $" (\"{view.Title}\")")}. " +
                "The blog reblogs a few posts a day that its followers would enjoy, and you decide whether this one may go on it.");
            string about = OmniTumblrCaptioner.StripHtml(view.Description ?? "");
            if (about.Length > 0) sb.AppendLine("The blog describes itself as: " + (about.Length > 300 ? about[..300] : about));
            if (!string.IsNullOrWhiteSpace(view.Strategy.Ai.Persona)) sb.AppendLine("Its voice: " + view.Strategy.Ai.Persona.Trim());
            sb.AppendLine();
            sb.AppendLine("Answer \"skip\" if any of these is true:");
            sb.AppendLine("- It does not fit: it is not the kind of thing this blog posts, or it is not funny or interesting on its own.");
            sb.AppendLine("- Sexual or suggestive content, nudity, gore, real violence or injury, self-harm, drugs.");
            sb.AppendLine("- It attacks, mocks or demeans a person or a group (race, religion, nationality, gender, sexuality, disability, body), or uses a slur.");
            sb.AppendLine("- Politics, elections, wars, tragedies, activism, religion, or anything asking for money, votes or signatures.");
            sb.AppendLine("- A personal post: a selfie, a diary entry, a call for help, a blog announcement, a commission advert.");
            sb.AppendLine("- Advertising, spam, or a post that exists to send people somewhere else.");
            sb.AppendLine($"- You cannot read it (it is not in {(string.IsNullOrWhiteSpace(view.Strategy.Ai.Language) ? "English" : view.Strategy.Ai.Language.Trim())}) or cannot tell what it shows.");
            if (!string.IsNullOrWhiteSpace(view.Strategy.Reblogs.Guidance)) sb.AppendLine("- " + view.Strategy.Reblogs.Guidance.Trim().Replace("\n", "\n  "));
            sb.AppendLine("When in doubt, skip: there are plenty of other posts.");
            sb.AppendLine();
            sb.Append("Reply with ONLY a JSON object, no prose and no code fences: {\"verdict\": \"reblog\" or \"skip\", \"reason\": \"one short sentence\"}");
            return sb.ToString();
        }

        internal static string BuildScreenUserPrompt(TumblrPostSummary post, string tag, DateTime now, bool pictureAttached, bool video)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Posted by @{post.BlogName} under #{tag}, {Math.Max(1, (int)(now - post.PublishedUtc).TotalHours)} hours ago. It has {post.NoteCount} notes.");
            if (post.Tags.Count > 0) sb.AppendLine("Its tags: " + string.Join(", ", post.Tags.Take(15)));
            string text = (post.Summary ?? "").Replace('\n', ' ').Trim();
            sb.AppendLine(text.Length == 0 ? "It has no text." : $"Its text: \"{(text.Length > 400 ? text[..400] + "…" : text)}\"");
            if (pictureAttached)
                sb.AppendLine(video ? "It is a video. One frame of it is attached; you cannot see the rest or hear it." : "Its picture is attached.");
            else if (video || post.Type == "photo")
                sb.AppendLine($"It is {(video ? "a video" : "a picture")} you cannot see. Judge from the text and tags alone, and skip unless they make clear what it is.");
            sb.Append("Write the JSON now.");
            return sb.ToString();
        }

        internal static (bool Ok, string Reason)? ParseVerdict(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string text = Regex.Replace(raw, "<think>.*?</think>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase).Trim();
            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            JObject obj;
            try { obj = JObject.Parse(text[start..(end + 1)]); } catch { return null; }
            string verdict = ((string?)obj["verdict"] ?? "").Trim().ToLowerInvariant();
            if (verdict is not ("reblog" or "skip")) return null;
            string reason = ((string?)obj["reason"] ?? "").Trim();
            if (reason.Length > 200) reason = reason[..200];
            return (verdict == "reblog", reason.Length == 0 ? (verdict == "reblog" ? "Fits the blog." : "Turned down by the screening model.") : reason);
        }

        // ─────────────────────────────── Pictures ───────────────────────────────

        /// <summary>
        /// The post's picture as a small JPEG: for the model to look at, and kept as the queue's preview.
        /// Null when it could not be fetched, or could not be made small enough to send.
        /// </summary>
        private async Task<(byte[]? Jpeg, string? PreviewPath)> LoadPictureAsync(TumblrPostSummary post, CancellationToken ct)
        {
            var image = ChoosePicture(post.Images);
            if (image == null) return (null, null);
            try
            {
                byte[]? bytes = await FetchPicture(image.Url, ct);
                if (bytes == null || bytes.Length < 64) return (null, null);
                string id = Regex.Replace(post.Id, "[^A-Za-z0-9]", "");
                string preview = Path.Combine(store.ThumbnailsDirectory, $"reblog-{id}.jpg");
                string source = Path.Combine(store.FramesCacheDirectory, $"reblog-{id}{Extension(bytes)}");
                Directory.CreateDirectory(store.FramesCacheDirectory);
                await File.WriteAllBytesAsync(source, bytes, ct);
                try
                {
                    if (await media.CreateThumbnailAsync(source, preview, null, ct))
                        return (await File.ReadAllBytesAsync(preview, ct), preview);
                }
                finally
                {
                    try { File.Delete(source); } catch { }
                }
                // Without ffmpeg a JPEG can still be used as it came, if it is small enough to send.
                if (Extension(bytes) != ".jpg" || bytes.Length > MaxPictureBytesForModel) return (null, null);
                Directory.CreateDirectory(store.ThumbnailsDirectory);
                await File.WriteAllBytesAsync(preview, bytes, ct);
                return (bytes, preview);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log($"OmniTumblr curator: could not load a picture: {ex.Message}");
                return (null, null);
            }
        }

        /// <summary>The smallest size at least 400 pixels wide (enough to read a meme), else the largest there is.</summary>
        internal static TumblrImage? ChoosePicture(IReadOnlyList<TumblrImage> images)
        {
            var usable = images.Where(i => IsTumblrMedia(i.Url)).ToList();
            if (usable.Count == 0) return null;
            return usable.Where(i => i.Width >= 400).OrderBy(i => i.Width).FirstOrDefault()
                ?? usable.OrderByDescending(i => i.Width).First();
        }

        /// <summary>Only Tumblr's own media hosts: a listing's HTML can point anywhere.</summary>
        internal static bool IsTumblrMedia(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && (uri.Host.Equals("media.tumblr.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".media.tumblr.com", StringComparison.OrdinalIgnoreCase));

        private static string Extension(byte[] b) =>
            b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 ? ".jpg"
            : b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 ? ".png"
            : b.Length > 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' ? ".gif"
            : b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P' ? ".webp"
            : ".bin";

        private static readonly HttpClient PictureHttp = new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        })
        { Timeout = TimeSpan.FromSeconds(30) };

        private static async Task<byte[]?> DownloadPictureAsync(string url, CancellationToken ct)
        {
            if (!IsTumblrMedia(url)) return null;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", TumblrApiClient.UserAgent);
            using var response = await PictureHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxPictureBytes) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > MaxPictureBytes) return null;
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
    }
}
