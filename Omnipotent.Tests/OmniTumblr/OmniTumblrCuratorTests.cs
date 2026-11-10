using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniTumblr.Api;
using Omnipotent.Services.OmniTumblr.Engine;
using Omnipotent.Services.OmniTumblr.Models;

namespace Omnipotent.Tests.OmniTumblr
{
    /// <summary>
    /// Curated reblogs: other people's posts going onto Klives's blogs. So what matters most here is what is
    /// NOT reblogged, that nothing goes out the screening model has not looked at, and that a reblog's notes
    /// (which are the original's) never pass for the blog's own.
    /// </summary>
    public class OmniTumblrCuratorTests
    {
        // Tuesday 10 Nov 2026. London is on GMT, so local time is UTC.
        private static readonly DateTime Day = new(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
        private const string Reblog = "{\"verdict\": \"reblog\", \"reason\": \"A harmless relatable meme.\"}";
        private const string Skip = "{\"verdict\": \"skip\", \"reason\": \"Mocks a named person.\"}";

        private static string ReblogBlog(Harness h, Action<OmniTumblrStrategy>? configure = null, string name = "memeblog", string? connection = null) =>
            h.AddBlog(connection ?? h.Connect(), b =>
            {
                b.Autopilot = true;
                b.Strategy.Source = ContentSourceKind.MemeScraper;
                b.Strategy.FixedTags = new List<string> { "memes" };
                b.Strategy.Reblogs.Enabled = true;
                configure?.Invoke(b.Strategy);
            }, name);

        /// <summary>A harness whose clock stands one hour before the blog's first reblog time of the day.</summary>
        private static (Harness H, string BlogId, DateTime Slot) AnHourBeforeAReblog(Action<OmniTumblrStrategy>? configure = null)
        {
            var h = new Harness(Day);
            string blogId = ReblogBlog(h, configure);
            var blog = h.Blog(blogId);
            DateTime slot = OmniTumblrCurator.SlotsOn(blog.Strategy.Reblogs, TimeZoneInfo.Utc, blogId, Day)[0];
            h.Clock.Now = slot.AddHours(-1);
            h.Model.Default = Reblog;
            return (h, blogId, slot);
        }

        // ─────────────────────────────── When ───────────────────────────────

        [Fact]
        public void ReblogTimes_AreSpreadThroughTheDay_AndStayPutForABlogAndDay()
        {
            var settings = new ReblogSettings { PerDay = 3, StartHour = 9, EndHour = 23 };

            var slots = OmniTumblrCurator.SlotsOn(settings, TimeZoneInfo.Utc, "blog-a", Day);

            Assert.Equal(3, slots.Count);
            // 14 hours in three parts: the middles are 11:20, 16:00 and 20:40, each nudged by at most 25 minutes.
            var middles = new[] { Day.AddHours(11).AddMinutes(20), Day.AddHours(16), Day.AddHours(20).AddMinutes(40) };
            for (int i = 0; i < 3; i++) Assert.InRange((slots[i] - middles[i]).TotalMinutes, -25, 25);
            Assert.Equal(slots, OmniTumblrCurator.SlotsOn(settings, TimeZoneInfo.Utc, "blog-a", Day.AddHours(13)));
            Assert.NotEqual(slots, OmniTumblrCurator.SlotsOn(settings, TimeZoneInfo.Utc, "blog-b", Day));

            // An end hour before the start is not a schedule: fall back to 9–23.
            var odd = OmniTumblrCurator.SlotsOn(new ReblogSettings { PerDay = 1, StartHour = 22, EndHour = 6 }, TimeZoneInfo.Utc, "blog-a", Day);
            Assert.InRange(Assert.Single(odd), Day.AddHours(15), Day.AddHours(17));
        }

        // ─────────────────────────────── What is picked ───────────────────────────────

        [Fact]
        public async Task ThePick_IsTheBestLikedRecentOriginal_AndEverythingRiskyIsPassedOver()
        {
            var (h, blogId, slot) = AnHourBeforeAReblog();
            using var _ = h;
            h.AddCandidate("memes", "good", "funnyperson", notes: 500, ageHours: 8);
            h.AddCandidate("memes", "fine", "otherperson", notes: 60, ageHours: 10);
            h.AddCandidate("memes", "unloved", "quietperson", notes: 10, ageHours: 9);                  // too few notes
            h.AddCandidate("memes", "stale", "oldperson", notes: 9000, ageHours: 100);                  // older than three days
            h.AddCandidate("memes", "mature", "spicyperson", notes: 8000, ageHours: 9).Mature = true;   // marked mature
            h.AddCandidate("memes", "ours", "MemeBlog", notes: 7000, ageHours: 9);                      // our own blog
            h.AddCandidate("memes", "passed-on", "reblogger", notes: 6000, ageHours: 9).IsReblog = true; // not an original
            h.AddCandidate("memes", "closed", "privateperson", notes: 5000, ageHours: 9).ReblogsOpen = false;
            h.AddCandidate("memes", "clip", "videoperson", notes: 4000, ageHours: 9, type: "video");    // videos are off by default
            h.AddCandidate("memes", "keyless", "brokenperson", notes: 3000, ageHours: 9).ReblogKey = null;

            int queued = await h.Curator.CurateBlogAsync(blogId, CancellationToken.None);

            Assert.Equal(1, queued); // one reblog time falls in the next four hours
            var post = Assert.Single(h.Posts(blogId));
            Assert.Equal(PostKind.Reblog, post.Kind);
            Assert.Equal("good", post.Reblog!.ParentPostId);
            Assert.Equal("t:funnyperson", post.Reblog.ParentBlogUuid);
            Assert.Equal("key-good", post.Reblog.ReblogKey);
            Assert.Equal(500, post.Reblog.ParentNotes);
            Assert.Equal("memes", post.Reblog.FoundUnderTag);
            Assert.Equal(slot, post.ScheduledUtc);
            Assert.Equal(slot, post.Reblog.SlotUtc);
            Assert.Equal(new[] { "memes" }, post.Tags);
            Assert.False(post.CaptionPending);
            // Other people's posts wait for a yes by default.
            Assert.Equal(PostStatus.AwaitingApproval, post.Status);
            Assert.False(post.Approved);
            // The model was shown the picture, and it is kept as the queue's preview.
            Assert.True(post.Reblog.SeenByModel);
            Assert.Equal("A harmless relatable meme.", post.Reblog.Reason);
            Assert.True(File.Exists(post.Reblog.PreviewPath));
            var call = Assert.Single(h.Model.Calls);
            Assert.Equal(1, call.Images);
            Assert.Contains("Posted by @funnyperson under #memes", call.User);
            Assert.Contains("When in doubt, skip", call.System);

            // Nothing more to do until the next reblog time comes into view.
            Assert.Equal(0, await h.Curator.CurateBlogAsync(blogId, CancellationToken.None));
            Assert.Single(h.Model.Calls);
        }

        [Fact]
        public async Task APostTheModelTurnsDown_IsNotReblogged_AndIsNeverAskedAboutAgain()
        {
            var (h, blogId, _) = AnHourBeforeAReblog();
            using var __ = h;
            h.AddCandidate("memes", "nasty", "meanperson", notes: 900, ageHours: 8);
            h.AddCandidate("memes", "nice", "kindperson", notes: 80, ageHours: 9);
            h.Model.Replies.Enqueue(() => Skip);

            await h.Curator.CurateBlogAsync(blogId, CancellationToken.None);

            var post = Assert.Single(h.Posts(blogId));
            Assert.Equal("nice", post.Reblog!.ParentPostId);
            var decision = h.Blog(blogId).ReblogHistory["nasty"];
            Assert.Equal("rejected", decision.Outcome);
            Assert.Equal("Mocks a named person.", decision.Reason);
            Assert.Equal(2, h.Model.Calls.Count);

            // The next reblog time comes into view: "nasty" is not put to the model again, and "nice" is already queued.
            h.Clock.Advance(TimeSpan.FromHours(4.5));
            Assert.Equal(0, await h.Curator.CurateBlogAsync(blogId, CancellationToken.None));
            Assert.Equal(2, h.Model.Calls.Count);
            Assert.Single(h.Posts(blogId));
        }

        [Theory]
        [InlineData("I think this one is fine!")]          // no JSON
        [InlineData("{\"verdict\": \"maybe\"}")]           // not an answer
        [InlineData("")]
        public async Task WithoutAClearYesFromTheModel_NothingIsReblogged(string reply)
        {
            var (h, blogId, _) = AnHourBeforeAReblog(s => s.Reblogs.RequireApproval = false);
            using var __ = h;
            h.AddCandidate("memes", "good", "funnyperson", notes: 500, ageHours: 8);
            h.Model.Default = reply;

            Assert.Equal(0, await h.Curator.CurateBlogAsync(blogId, CancellationToken.None));

            Assert.Empty(h.Posts(blogId));
            Assert.Empty(h.Blog(blogId).ReblogHistory); // an unusable answer is not held against the post
            Assert.Contains(h.Store.Read(s => s.Events.Select(e => e.Kind).ToList()), k => k == "reblog.none");
        }

        [Fact]
        public async Task WithApprovalOff_OnlyWhatTheModelActuallySaw_GoesOutOnItsOwn()
        {
            var (h, blogId, _) = AnHourBeforeAReblog(s =>
            {
                s.Reblogs.RequireApproval = false;
                s.Reblogs.PerDay = 12; // a reblog time every 70 minutes: three fall inside the next four hours
                s.Reblogs.Videos = true;
            });
            using var __ = h;
            h.Clock.Now = OmniTumblrCurator.SlotsOn(h.Blog(blogId).Strategy.Reblogs, TimeZoneInfo.Utc, blogId, Day)[0].AddMinutes(-30);
            h.AddCandidate("memes", "seen", "a-person", notes: 900, ageHours: 8);
            var unloadable = h.AddCandidate("memes", "unseen", "b-person", notes: 800, ageHours: 8);
            h.Pictures.Remove(unloadable.Images[0].Url);                                         // its picture will not load
            h.AddCandidate("memes", "video", "c-person", notes: 700, ageHours: 8, type: "video"); // one frame is not the video

            Assert.Equal(3, await h.Curator.CurateBlogAsync(blogId, CancellationToken.None));

            var posts = h.Posts(blogId).ToDictionary(p => p.Reblog!.ParentPostId);
            Assert.Equal(PostStatus.Ready, posts["seen"].Status);
            Assert.True(posts["seen"].Approved);
            Assert.Equal(PostStatus.AwaitingApproval, posts["unseen"].Status);
            Assert.False(posts["unseen"].Reblog!.SeenByModel);
            Assert.Contains("its picture could not be loaded", posts["unseen"].Reblog!.Reason);
            Assert.Equal(PostStatus.AwaitingApproval, posts["video"].Status);
            Assert.Contains("only one frame of a video", posts["video"].Reblog!.Reason);
            // The model was told what it could not see.
            Assert.Contains(h.Model.Calls, c => c.Images == 0 && c.User.Contains("a picture you cannot see"));
            Assert.Contains(h.Model.Calls, c => c.Images == 1 && c.User.Contains("One frame of it is attached"));
        }

        [Fact]
        public async Task TwoOfOurBlogs_NeverReblogTheSamePost()
        {
            var (h, first, _) = AnHourBeforeAReblog();
            using var __ = h;
            string second = ReblogBlog(h, name: "sideblog", connection: h.Store.Read(s => s.Connections.Keys.First()));
            h.Clock.Now = OmniTumblrCurator.SlotsOn(h.Blog(second).Strategy.Reblogs, TimeZoneInfo.Utc, second, Day)[0].AddMinutes(-30);
            h.AddCandidate("memes", "top", "funnyperson", notes: 500, ageHours: 8);
            h.AddCandidate("memes", "next", "otherperson", notes: 100, ageHours: 8);

            await h.Curator.CurateBlogAsync(first, CancellationToken.None);
            await h.Curator.CurateBlogAsync(second, CancellationToken.None);

            Assert.Equal("top", h.Posts(first).Single().Reblog!.ParentPostId);
            Assert.Equal("next", h.Posts(second).Single().Reblog!.ParentPostId);
        }

        [Fact]
        public async Task NothingIsPicked_WhenSwitchedOff_OrWhenNothingCanScreenIt()
        {
            var (h, blogId, _) = AnHourBeforeAReblog();
            using var __ = h;
            h.AddCandidate("memes", "good", "funnyperson", notes: 500, ageHours: 8);

            h.Model.Available = false; // no screening model: nothing goes out unscreened
            Assert.Equal(0, await h.Curator.CurateBlogAsync(blogId, CancellationToken.None));
            h.Model.Available = true;

            h.Curator.Enabled = () => false; // OmniTumblr_ReblogsEnabled
            Assert.Equal(0, await h.Curator.CurateBlogAsync(blogId, CancellationToken.None));
            Assert.Equal(0, await h.Curator.CurateDueAsync(CancellationToken.None));
            h.Curator.Enabled = () => true;

            h.Store.Mutate(s => s.Blog(blogId)!.Autopilot = false);
            Assert.Equal(0, await h.Curator.CurateDueAsync(CancellationToken.None));
            h.Store.Mutate(s => { s.Blog(blogId)!.Autopilot = true; s.Blog(blogId)!.Strategy.Reblogs.Enabled = false; });
            Assert.Equal(0, await h.Curator.CurateDueAsync(CancellationToken.None));

            Assert.Empty(h.Posts(blogId));
            Assert.Empty(h.Model.Calls);
            Assert.DoesNotContain(h.Api.Calls, c => c.StartsWith("tagged:"));

            h.Store.Mutate(s => s.Blog(blogId)!.Strategy.Reblogs.Enabled = true);
            Assert.Equal(1, await h.Curator.CurateDueAsync(CancellationToken.None));
        }

        [Fact]
        public async Task PickAnother_SwapsTheQueuedReblog_AndDoesNotOfferTheFirstAgain()
        {
            var (h, blogId, slot) = AnHourBeforeAReblog();
            using var __ = h;
            h.AddCandidate("memes", "first", "funnyperson", notes: 500, ageHours: 8);
            h.AddCandidate("memes", "second", "otherperson", notes: 100, ageHours: 8);
            await h.Curator.CurateBlogAsync(blogId, CancellationToken.None);
            string postId = h.Posts(blogId).Single().PostId;

            var (ok, message) = await h.Curator.ReplaceAsync(postId, CancellationToken.None);

            Assert.True(ok, message);
            var post = h.Post(postId);
            Assert.Equal("second", post.Reblog!.ParentPostId);
            Assert.Equal(slot, post.Reblog.SlotUtc);
            Assert.Equal("rejected", h.Blog(blogId).ReblogHistory["first"].Outcome);

            var (again, why) = await h.Curator.ReplaceAsync(postId, CancellationToken.None);
            Assert.False(again);
            Assert.Contains("Nothing else", why);
            Assert.Equal("second", h.Post(postId).Reblog!.ParentPostId); // left as it was
        }

        [Fact]
        public async Task APickNobodyApproves_IsDroppedAfterTwoDays_AndAFullQueueStopsNewPicks()
        {
            var (h, blogId, _) = AnHourBeforeAReblog(s => s.Reblogs.PerDay = 1);
            using var __ = h;
            for (int i = 0; i < 6; i++) h.AddCandidate("memes", "p" + i, "person" + i, notes: 500 - i, ageHours: 8);

            await h.Curator.CurateBlogAsync(blogId, CancellationToken.None);
            h.Clock.Advance(TimeSpan.FromHours(24));
            foreach (var p in h.Api.Tagged["memes"]) p.PublishedUtc = p.PublishedUtc.AddHours(24); // still fresh
            await h.Curator.CurateBlogAsync(blogId, CancellationToken.None);
            Assert.Equal(2, h.Posts(blogId).Count(p => p.Status == PostStatus.AwaitingApproval));

            // One a day and two waiting is as many as the queue takes: nothing more is picked on top of them.
            h.Clock.Advance(TimeSpan.FromHours(23));
            foreach (var p in h.Api.Tagged["memes"]) p.PublishedUtc = p.PublishedUtc.AddHours(23);
            int calls = h.Model.Calls.Count;
            Assert.Equal(0, await h.Curator.CurateBlogAsync(blogId, CancellationToken.None));
            Assert.Equal(calls, h.Model.Calls.Count);

            h.Clock.Advance(TimeSpan.FromMinutes(61));
            await h.Curator.CurateBlogAsync(blogId, CancellationToken.None);
            var posts = h.Posts(blogId);
            // The first pick is now over two days old: dropped, which makes room for one more.
            Assert.Equal(1, posts.Count(p => p.Status == PostStatus.Cancelled && p.LastError == "Not approved within two days."));
            Assert.Equal(2, posts.Count(p => p.Status == PostStatus.AwaitingApproval));
        }

        // ─────────────────────────────── Publishing ───────────────────────────────

        private static string QueuedReblog(Harness h, string blogId, string parentId = "9001") =>
            h.AddReadyPost(blogId, p =>
            {
                p.Kind = PostKind.Reblog;
                p.Origin = PostOrigin.Autopilot;
                p.Caption = null;
                p.Media.Clear();
                p.Tags = new List<string> { "memes" };
                p.Reblog = new ReblogRef
                {
                    ParentPostId = parentId, ParentBlogUuid = "t:funnyperson", ParentBlogName = "funnyperson", ReblogKey = "key-" + parentId,
                    ParentNotes = 500, ParentType = "photo", FoundUnderTag = "memes", Summary = "when the wifi drops", SeenByModel = true,
                };
            });

        [Fact]
        public async Task AReblog_IsSentAsAReblogOfTheOriginal_WithNothingUploaded()
        {
            using var h = new Harness(Day.AddHours(12));
            string blogId = h.AddBlog(h.Connect(), b => b.Autopilot = true);
            string postId = QueuedReblog(h, blogId, "832842388780236800");

            Assert.Equal(1, await h.Publisher.PublishDueAsync(CancellationToken.None));

            var (_, request) = Assert.Single(h.Api.Created);
            Assert.True(request.IsReblog);
            Assert.Empty(request.Uploads);
            var json = JObject.Parse(request.ToJson());
            Assert.Equal("t:funnyperson", (string?)json["parent_tumblelog_uuid"]);
            Assert.Equal(JTokenType.Integer, json["parent_post_id"]!.Type); // Tumblr documents an integer
            Assert.Equal(832842388780236800L, (long)json["parent_post_id"]!);
            Assert.Equal("key-832842388780236800", (string?)json["reblog_key"]);
            Assert.Empty((JArray)json["content"]!);
            Assert.Equal("memes", (string?)json["tags"]);
            Assert.StartsWith("reblog-", (string?)json["slug"]);

            var post = h.Post(postId);
            Assert.Equal(PostStatus.Published, post.Status);
            Assert.Equal("taken", h.Blog(blogId).ReblogHistory["832842388780236800"].Outcome);
            Assert.True(h.Store.Read(s => s.InsightsOf(blogId).Index.Single().IsReblog));
            Assert.Contains(h.Store.Read(s => s.Events.Select(e => e.Message).ToList()), m => m.Contains("reblogged @funnyperson"));
        }

        [Fact]
        public async Task AReblogTumblrRefuses_IsDropped_WithoutAlarmOrAMarkAgainstTheBlog()
        {
            using var h = new Harness(Day.AddHours(12));
            string blogId = h.AddBlog(h.Connect(), b => b.Autopilot = true);
            string postId = QueuedReblog(h, blogId);
            // 400.8002: the original was deleted, or its author does not let this blog reblog it.
            h.Api.OnCreate = (_, _) => throw new TumblrApiException(TumblrErrorKind.BadRequest, 400, 8002, "blog/posts:create failed (400.8002): the reblog parent post cannot be interacted with");

            Assert.Equal(0, await h.Publisher.PublishDueAsync(CancellationToken.None));

            Assert.Equal(PostStatus.Failed, h.Post(postId).Status);
            Assert.Equal(0, h.Blog(blogId).Health.ConsecutiveFailures);
            Assert.Empty(h.Alerts);
            Assert.Empty(h.RefilledFor);
            Assert.Equal("failed", h.Blog(blogId).ReblogHistory["9001"].Outcome);
            Assert.Contains(h.Store.Read(s => s.Events.Select(e => e.Kind).ToList()), k => k == "reblog.refused");
        }

        [Fact]
        public async Task Reblogs_DoNotUseUpTheBlogsOwnPostsForTheDay()
        {
            using var h = new Harness(Day.AddHours(12));
            string blogId = h.AddBlog(h.Connect(), b =>
            {
                b.Autopilot = true;
                b.Strategy.MaxPostsPerDay = 1;
                b.Strategy.MinGapMinutes = 0;
            });
            h.Publisher.FleetSpacing = () => TimeSpan.Zero;
            QueuedReblog(h, blogId, "1");
            QueuedReblog(h, blogId, "2");
            string own = h.AddReadyPost(blogId, p => p.Origin = PostOrigin.Autopilot);

            Assert.Equal(3, await h.Publisher.PublishDueAsync(CancellationToken.None));

            Assert.Equal(PostStatus.Published, h.Post(own).Status);
        }

        // ─────────────────────────────── A reblog's notes are not ours ───────────────────────────────

        [Fact]
        public async Task TheOriginalsNotes_NeverCountAsTheBlogs()
        {
            using var h = new Harness(Day.AddHours(12));
            string blogId = h.AddBlog(h.Connect(), b =>
            {
                b.Autopilot = true;
                b.Strategy.MinGapMinutes = 0;
                b.Strategy.Reblogs.Enabled = true;
            });
            h.Publisher.FleetSpacing = () => TimeSpan.Zero;
            string reblogId = QueuedReblog(h, blogId);
            string ownId = h.AddReadyPost(blogId, p => p.Origin = PostOrigin.Autopilot);
            await h.Publisher.PublishDueAsync(CancellationToken.None);
            // Tumblr reports the original's 50,000 notes on our reblog of it; our own post has 4.
            lock (h.Api.PublishedPosts)
            {
                h.Api.PublishedPosts.Single(p => p.Id == h.Post(reblogId).TumblrPostId).NoteCount = 50_000;
                h.Api.PublishedPosts.Single(p => p.Id == h.Post(ownId).TumblrPostId).NoteCount = 4;
            }
            h.Api.OnNotes = _ => new TumblrNotesSummary { TotalNotes = 4, TotalLikes = 3, TotalReblogs = 1 };
            h.Clock.Advance(TimeSpan.FromDays(2));

            await h.Sync.SyncBlogNowAsync(blogId, CancellationToken.None);

            Assert.Equal(0, h.Post(reblogId).Metrics.Notes);
            Assert.Equal(4, h.Post(ownId).Metrics.Notes);
            Assert.DoesNotContain("blog/notes:" + h.Post(reblogId).TumblrPostId, h.Api.Calls); // no breakdown fetched for it either
            DateTime now = h.Clock.Now;
            var summary = h.Store.Read(s => OmniTumblrViews.BlogSummary(s, s.Blog(blogId)!, now));
            Assert.Equal(4, summary.Notes30d);
            Assert.Equal(4, summary.AvgNotes30d);
            var analytics = h.Store.Read(s => OmniTumblrViews.Analytics(s, blogId, 30, now));
            Assert.Equal(1, analytics.PostsPublished);
            Assert.Equal(4, analytics.Notes);
            Assert.Equal(new[] { 4L }, analytics.TopPosts.Select(t => t.Notes));
            Assert.Equal(4, analytics.Posting.Sum(d => d.Notes));
            Assert.Equal(2, analytics.Posting.Sum(d => d.Ours)); // both went out, and are counted as posts
            var reach = h.Store.Read(s => OmniTumblrReach.Build(s, s.Blog(blogId)!, now, tagResearchEnabled: true));
            Assert.Equal(1, reach.Posts30d);
            Assert.Contains(reach.Findings, f => f.Title == "Curated reblogs are on" && f.Detail.Contains("1 reblog(s)"));
        }

        [Fact]
        public async Task TheListingCheck_NeverLooksForAReblog_BecauseTumblrListsOnlyOriginals()
        {
            using var h = new Harness(Day.AddHours(12));
            string blogId = h.AddBlog(h.Connect(), b => b.Autopilot = true);
            h.SeedTag("memes", 30, TimeSpan.FromHours(2), notes: 6);
            await h.TagResearch.ResearchAsync(new[] { "memes" }, CancellationToken.None);
            string reblogId = QueuedReblog(h, blogId);
            h.Store.Mutate(s =>
            {
                var p = s.FindPost(reblogId)!;
                p.Status = PostStatus.Published;
                p.TumblrPostId = "555";
                p.PublishedUtc = h.Clock.Now.AddHours(-3);
            });

            Assert.False(await h.TagResearch.CheckListedAsync(blogId, CancellationToken.None));

            Assert.Null(h.Blog(blogId).Health.Search.Listed);
            Assert.Equal(0, h.Blog(blogId).Health.Search.Misses);
        }

        [Fact]
        public void ABlogThatNeverReblogs_IsToldSo()
        {
            using var h = new Harness(Day.AddHours(12));
            string blogId = h.AddBlog(h.Connect(), b => b.Autopilot = true);

            var reach = h.Store.Read(s => OmniTumblrReach.Build(s, s.Blog(blogId)!, h.Clock.Now, tagResearchEnabled: true));

            var finding = Assert.Single(reach.Findings, f => f.Title == "The blog only posts; it never reblogs");
            Assert.Equal("strategy", finding.Action);
        }

        // ─────────────────────────────── The pieces ───────────────────────────────

        [Fact]
        public void APostFromTumblr_IsReadForWhatAReblogNeeds()
        {
            var original = TumblrApiClient.ParsePost(JObject.Parse(
                "{\"type\":\"blocks\",\"id_string\":\"11\",\"blog_name\":\"funnyperson\",\"blog\":{\"name\":\"funnyperson\",\"uuid\":\"t:abc\"},\"reblog_key\":\"k1\",\"timestamp\":1794260395,\"note_count\":500," +
                "\"interactability_reblog\":\"everyone\",\"classification\":\"clean\",\"community_labels\":{\"has_community_label\":false},\"trail\":[]," +
                "\"content\":[{\"type\":\"image\",\"media\":[{\"type\":\"image/jpeg\",\"width\":1280,\"url\":\"https://64.media.tumblr.com/a/s1280x1920/x.jpg\"},{\"type\":\"image/jpeg\",\"width\":540,\"url\":\"https://64.media.tumblr.com/a/s540x810/x.jpg\"},{\"type\":\"image/jpeg\",\"width\":100,\"url\":\"https://64.media.tumblr.com/a/s100x200/x.jpg\"}]}]}"));
            Assert.Equal("t:abc", original.BlogUuid);
            Assert.Equal("k1", original.ReblogKey);
            Assert.False(original.IsReblog);
            Assert.False(original.Mature);
            Assert.True(original.ReblogsOpen);
            Assert.Equal(3, original.Images.Count);
            Assert.Equal(540, OmniTumblrCurator.ChoosePicture(original.Images)!.Width); // the smallest that is still readable

            var labelled = TumblrApiClient.ParsePost(JObject.Parse("{\"id\":1,\"community_labels\":{\"has_community_label\":true},\"interactability_reblog\":\"noone\",\"content\":[{\"type\":\"text\",\"text\":\"x\"}]}"));
            Assert.True(labelled.Mature);
            Assert.False(labelled.ReblogsOpen);

            var reblog = TumblrApiClient.ParsePost(JObject.Parse("{\"id\":2,\"content\":[],\"trail\":[{\"content\":[{\"type\":\"video\",\"poster\":[{\"type\":\"image/jpeg\",\"width\":720,\"url\":\"https://44.media.tumblr.com/p/poster.jpg\"}]}]}]}"));
            Assert.True(reblog.IsReblog);
            Assert.Equal("video", reblog.Type);
            Assert.Equal("https://44.media.tumblr.com/p/poster.jpg", reblog.Images.Single().Url);

            // The legacy shape lists a post's own content in its trail: that does not make it a reblog.
            var legacy = TumblrApiClient.ParsePost(JObject.Parse("{\"id\":3,\"type\":\"photo\",\"trail\":[{\"is_current_item\":true,\"is_root_item\":true}],\"photos\":[{\"original_size\":{\"url\":\"https://64.media.tumblr.com/l/full.jpg\",\"width\":900},\"alt_sizes\":[{\"url\":\"https://64.media.tumblr.com/l/s400.jpg\",\"width\":400}]}]}"));
            Assert.False(legacy.IsReblog);
            Assert.Equal(400, OmniTumblrCurator.ChoosePicture(legacy.Images)!.Width);
        }

        [Theory]
        [InlineData("https://64.media.tumblr.com/a/b.jpg", true)]
        [InlineData("https://media.tumblr.com/a/b.jpg", true)]
        [InlineData("http://64.media.tumblr.com/a/b.jpg", false)]             // not https
        [InlineData("https://evil.example/64.media.tumblr.com/b.jpg", false)]  // a post's HTML can point anywhere
        [InlineData("https://media.tumblr.com.evil.example/b.jpg", false)]
        [InlineData("https://127.0.0.1/b.jpg", false)]
        public void OnlyTumblrsMediaHosts_AreEverFetched(string url, bool allowed) =>
            Assert.Equal(allowed, OmniTumblrCurator.IsTumblrMedia(url));

        [Fact]
        public void TheScreeningAnswer_MustBeAClearVerdict()
        {
            Assert.Equal((true, "Fine."), OmniTumblrCurator.ParseVerdict("<think>hmm</think>```json\n{\"verdict\": \"REBLOG\", \"reason\": \"Fine.\"}\n```"));
            Assert.Equal((false, "Turned down by the screening model."), OmniTumblrCurator.ParseVerdict("{\"verdict\": \"skip\"}"));
            Assert.Null(OmniTumblrCurator.ParseVerdict("{\"verdict\": \"yes\"}"));
            Assert.Null(OmniTumblrCurator.ParseVerdict("reblog"));
            Assert.Null(OmniTumblrCurator.ParseVerdict(null));
        }

        [Fact]
        public void ReblogSettings_AreOffUntilSwitchedOn_Clamped_AndNeedSomewhereToLook()
        {
            var fresh = OmniTumblrStrategy.CreateDefault();
            Assert.False(fresh.Reblogs.Enabled);
            Assert.True(fresh.Reblogs.RequireApproval);
            Assert.False(fresh.Reblogs.Videos);

            var s = OmniTumblrStrategyRules.Preset("manual"); // no fixed tags
            s.Reblogs = new ReblogSettings { Enabled = true, PerDay = 99, MinNotes = -5, MaxAgeHours = 1, StartHour = 20, EndHour = 4, Tags = new List<string> { " #Memes ", "memes" } };
            OmniTumblrStrategyRules.Normalize(s);
            Assert.Equal(12, s.Reblogs.PerDay);
            Assert.Equal(0, s.Reblogs.MinNotes);
            Assert.Equal(6, s.Reblogs.MaxAgeHours);
            Assert.Equal((9, 23), (s.Reblogs.StartHour, s.Reblogs.EndHour));
            Assert.Equal(new[] { "Memes" }, s.Reblogs.Tags);
            Assert.Empty(OmniTumblrStrategyRules.Validate(s, autopilot: false));

            s.Reblogs.Tags.Clear();
            Assert.Contains(OmniTumblrStrategyRules.Validate(s, autopilot: false), e => e.Contains("need tags to look in"));
            s.FixedTags.Add("memes");
            s.Reblogs.Images = s.Reblogs.Text = false;
            Assert.Contains(OmniTumblrStrategyRules.Validate(s, autopilot: false), e => e.Contains("at least one kind of post"));
        }
    }
}
