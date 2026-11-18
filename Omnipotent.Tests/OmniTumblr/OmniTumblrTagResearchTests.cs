using Omnipotent.Services.OmniTumblr.Api;
using Omnipotent.Services.OmniTumblr.Engine;
using Omnipotent.Services.OmniTumblr.Models;

namespace Omnipotent.Tests.OmniTumblr
{
    /// <summary>
    /// Tag research: a blog with few followers is seen through its tags, so a tag is only worth a slot if other
    /// people's posts under it get notes. The shapes below are the ones measured on 10 Nov 2026: #funny at two
    /// posts a minute with nothing to show for it, #real madrid at a post every hour or two with 6 notes each,
    /// and the caption model's own inventions with nobody in them but the blog itself.
    /// </summary>
    public class OmniTumblrTagResearchTests
    {
        private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        private static readonly IReadOnlySet<string> Own = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "memeblog" };

        private static List<TumblrPostSummary> Posts(int count, TimeSpan newestAge, TimeSpan every, long notes, string blog = "someone", string type = "photo", params string[] tags) =>
            Enumerable.Range(0, count).Select(i => new TumblrPostSummary
            {
                Id = $"{blog}-{newestAge.Ticks}-{i}",
                BlogName = blog + (i % 5),
                PublishedUtc = Now - newestAge - TimeSpan.FromTicks(every.Ticks * i),
                NoteCount = notes,
                Type = type,
                Tags = tags.ToList(),
            }).ToList();

        // ─────────────────────────────── The verdict ───────────────────────────────

        [Fact]
        public void ATagOnlyOurOwnBlogUses_IsDead()
        {
            var ours = Posts(3, TimeSpan.FromHours(2), TimeSpan.FromDays(1), notes: 0, blog: "memeblog");
            ours.ForEach(p => p.BlogName = "MemeBlog");

            var r = OmniTumblrTagMath.Evaluate("The Shirt Is The Alibi", ours, null, Own, Now);

            Assert.Equal("the shirt is the alibi", r.Tag);
            Assert.Equal(TagVerdict.Dead, r.Verdict);
            Assert.Equal(0, r.Sampled);
            Assert.Equal(0, r.Score);
            Assert.Contains("Nobody else has ever posted", OmniTumblrTagMath.Describe(r));
        }

        [Fact]
        public void ATagNobodyHasTouchedInMonths_IsDead_HoweverBusyItOnceWas()
        {
            // Twenty posts inside one week, a year ago: measured to now that is a post every 18 days, not three a day.
            var old = Posts(20, TimeSpan.FromDays(360), TimeSpan.FromHours(8), notes: 40);

            var r = OmniTumblrTagMath.Evaluate("old fandom", old, null, Own, Now);

            Assert.Equal(TagVerdict.Dead, r.Verdict);
            Assert.True(r.PostsPerDay < 0.1);
            Assert.Equal(0, r.Score);
        }

        [Fact]
        public void AFloodedTag_IsCrowded_AndScoresBelowASmallTagWithAFollowing()
        {
            // #funny: two posts a minute, and a day later the typical post still has nothing.
            var funnyNow = Posts(20, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30), notes: 0);
            Assert.True(OmniTumblrTagMath.NeedsOlderPage(funnyNow, Now));
            var funnyYesterday = Posts(20, TimeSpan.FromHours(24), TimeSpan.FromSeconds(30), notes: 0);
            funnyYesterday[3].NoteCount = 17;
            var funny = OmniTumblrTagMath.Evaluate("funny", funnyNow, funnyYesterday, Own, Now);

            // #real madrid: a post every hour or two, 6 notes each.
            var madridPage = Posts(20, TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(100), notes: 6);
            Assert.False(OmniTumblrTagMath.NeedsOlderPage(madridPage, Now));
            var madrid = OmniTumblrTagMath.Evaluate("real madrid", madridPage, null, Own, Now);

            Assert.Equal(TagVerdict.Crowded, funny.Verdict);
            Assert.True(funny.PostsPerDay > 2000);
            Assert.Equal(0, funny.TypicalNotes);
            Assert.Equal(20, funny.Settled);
            Assert.Equal(TagVerdict.Active, madrid.Verdict);
            Assert.InRange(madrid.PostsPerDay!.Value, 10, 20);
            Assert.Equal(6, madrid.TypicalNotes);
            Assert.True(madrid.Score > 60, $"real madrid scored {madrid.Score}");
            Assert.True(funny.Score < 10, $"funny scored {funny.Score}");
            Assert.Contains("Flooded", OmniTumblrTagMath.Describe(funny));
            Assert.Contains("minutes", OmniTumblrTagMath.Describe(funny));
            Assert.Contains("6 notes", OmniTumblrTagMath.Describe(madrid));
        }

        [Fact]
        public void ASlowTagWithRegulars_IsQuiet_AndStillWorthUsing()
        {
            var page = Posts(8, TimeSpan.FromDays(2), TimeSpan.FromDays(6), notes: 12); // one post every six days

            var r = OmniTumblrTagMath.Evaluate("niche show", page, null, Own, Now);

            Assert.Equal(TagVerdict.Quiet, r.Verdict);
            Assert.True(r.Score > 30, $"scored {r.Score}");
            Assert.True(r.Score < 100 * (1 - Math.Exp(-12 / 4.0))); // marked down: few people watch a tag this slow
        }

        [Fact]
        public void OnePostWithThousandsOfNotes_DoesNotMakeATagLookGood()
        {
            var page = Posts(20, TimeSpan.FromHours(13), TimeSpan.FromMinutes(90), notes: 0);
            page[0].NoteCount = 4000;

            var r = OmniTumblrTagMath.Evaluate("lottery", page, null, Own, Now);

            Assert.Equal(0, r.TypicalNotes);
            Assert.True(r.Score < 15, $"scored {r.Score}");
        }

        [Fact]
        public void Research_RecordsWhatElsePeopleTag_AndWhatTheyPost()
        {
            var page = Posts(6, TimeSpan.FromHours(13), TimeSpan.FromHours(5), notes: 3, tags: new[] { "Joker", "DC Comics", "joaquin phoenix" });
            page.AddRange(Posts(4, TimeSpan.FromHours(14), TimeSpan.FromHours(5), notes: 3, blog: "other", type: "video", tags: new[] { "joker", "dc comics" }));
            page.Add(new TumblrPostSummary { Id = "odd", BlogName = "one", PublishedUtc = Now.AddHours(-20), NoteCount = 1, Type = "text", Tags = new List<string> { "joker", "unique thing" } });

            var r = OmniTumblrTagMath.Evaluate("joker", page, null, Own, Now);

            Assert.Equal(new[] { "dc comics", "joaquin phoenix" }, r.Related); // used twice or more; never the tag itself
            Assert.Equal(6, r.Images);
            Assert.Equal(4, r.Videos);
            Assert.Equal(1, r.Texts);
        }

        // ─────────────────────────────── Choosing a post's tags ───────────────────────────────

        private static void SeedTheNovemberTags(Harness h)
        {
            h.SeedTag("funny", 60, TimeSpan.FromSeconds(30), notes: 0);                                       // flooded, nothing to show
            h.SeedTag("funny", 30, TimeSpan.FromSeconds(30), notes: 0, newestAge: TimeSpan.FromHours(25));
            h.SeedTag("real madrid", 30, TimeSpan.FromMinutes(100), notes: 6);                                // a following
            h.SeedTag("football", 40, TimeSpan.FromMinutes(20), notes: 2);                                    // busier, thinner
            h.SeedTag("football", 30, TimeSpan.FromMinutes(20), notes: 2, newestAge: TimeSpan.FromHours(25));
            h.Api.Tagged["the supreme leader approves"] = new List<TumblrPostSummary>                         // only ever our own post
            {
                new() { Id = "ours", BlogName = "memeblog", PublishedUtc = h.Clock.Now.AddDays(-3), Tags = new List<string> { "the supreme leader approves" } },
            };
        }

        [Fact]
        public async Task Vetting_PutsTagsWithAFollowingFirst_AndLeavesOutTheOnesNobodyUses()
        {
            using var h = new Harness(Now);
            string conn = h.Connect();
            h.AddBlog(conn);
            SeedTheNovemberTags(h);

            var vetting = await h.TagResearch.VetAsync(new[] { "The Supreme Leader Approves", "#funny", "football", "real madrid", "red lighting" }, keep: 3, CancellationToken.None);

            Assert.True(vetting.Researched);
            Assert.Equal(new[] { "real madrid", "football", "funny" }, vetting.Kept);
            Assert.Equal(new[] { "the supreme leader approves", "red lighting" }, vetting.Dropped);

            // Remembered: asking again costs no calls.
            int calls = h.Api.Calls.Count;
            var again = await h.TagResearch.VetAsync(new[] { "real madrid", "funny" }, keep: 3, CancellationToken.None);
            Assert.Equal(new[] { "real madrid", "funny" }, again.Kept);
            Assert.Equal(calls, h.Api.Calls.Count);
            Assert.Contains("tagged:funny:before", h.Api.Calls); // the busy tag needed a page from a day back
            Assert.DoesNotContain("tagged:real madrid:before", h.Api.Calls);
        }

        [Fact]
        public async Task AHyphenatedTagNobodyUses_IsTriedWithSpaces()
        {
            using var h = new Harness(Now);
            h.AddBlog(h.Connect());
            h.SeedTag("game show", 30, TimeSpan.FromHours(2), notes: 4);

            var vetting = await h.TagResearch.VetAsync(new[] { "game-show", "staring-contest" }, keep: 5, CancellationToken.None);

            Assert.Equal(new[] { "game show" }, vetting.Kept);
            Assert.Equal(new[] { "staring-contest" }, vetting.Dropped);
        }

        [Fact]
        public async Task WhenTumblrCannotBeAsked_TheModelsTagsAreUsedAsGiven()
        {
            using var h = new Harness(Now);
            h.AddBlog(h.Connect());
            SeedTheNovemberTags(h);
            h.Gate.RecordRefused("blog/posts"); // Tumblr's edge is refusing this server

            var vetting = await h.TagResearch.VetAsync(new[] { "red lighting", "real madrid", "funny" }, keep: 2, CancellationToken.None);

            Assert.False(vetting.Researched);
            Assert.Equal(new[] { "red lighting", "real madrid" }, vetting.Kept);
            Assert.Empty(vetting.Dropped);
            Assert.DoesNotContain(h.Api.Calls, c => c.StartsWith("tagged:"));
        }

        [Fact]
        public async Task AFailedLookup_IsNotHeldAgainstTheTag_AndIsNotRetriedAtOnce()
        {
            using var h = new Harness(Now);
            h.AddBlog(h.Connect());
            int attempts = 0;
            h.Api.OnTagged = (tag, _) => { attempts++; throw new TumblrApiException(TumblrErrorKind.ServiceUnavailable, 503, null, "tagged failed (503)"); };

            var first = await h.TagResearch.VetAsync(new[] { "joker" }, keep: 3, CancellationToken.None);
            var second = await h.TagResearch.VetAsync(new[] { "joker" }, keep: 3, CancellationToken.None);

            Assert.Equal(new[] { "joker" }, first.Kept);
            Assert.False(first.Researched);
            Assert.Equal(new[] { "joker" }, second.Kept);
            Assert.Equal(1, attempts);

            h.Clock.Advance(TimeSpan.FromMinutes(31));
            h.Api.OnTagged = null;
            h.SeedTag("joker", 30, TimeSpan.FromHours(1), notes: 5);
            var later = await h.TagResearch.VetAsync(new[] { "joker" }, keep: 3, CancellationToken.None);
            Assert.True(later.Researched);
        }

        // ─────────────────────────────── In the planner ───────────────────────────────

        private static string AutopilotBlog(Harness h, Action<OmniTumblrStrategy>? configure = null) =>
            h.AddBlog(h.Connect(), b =>
            {
                b.Autopilot = true;
                b.Strategy.Source = ContentSourceKind.MemeScraper;
                b.Strategy.CaptionMode = CaptionMode.AI;
                b.Strategy.FixedTags = new List<string> { "memes" };
                b.Strategy.Ai.MaxSuggestedTags = 3;
                configure?.Invoke(b.Strategy);
            });

        [Fact]
        public async Task APreparedPost_CarriesOnlyTagsWithSomeoneInThem_BestFirst()
        {
            using var h = new Harness(Now);
            string blogId = AutopilotBlog(h);
            SeedTheNovemberTags(h);
            h.AddReel("a");
            h.Model.Default = "{\"caption\": \"propaganda lands harder when the target is laughing\", \"tags\": [\"the supreme leader approves\", \"funny\", \"football\", \"real madrid\", \"red lighting\"], \"alt_text\": \"A footballer in the front row of a comedy show.\"}";
            await h.ResearchPlanner.PlanBlogAsync(blogId, CancellationToken.None);

            await h.ResearchPlanner.PrepareDueAsync(5, CancellationToken.None);

            var post = Assert.Single(h.Posts(blogId));
            Assert.Equal(PostStatus.Ready, post.Status);
            Assert.Equal(new[] { "memes", "real madrid", "football", "funny" }, post.Tags);
            Assert.Equal(new[] { "real madrid", "football", "funny" }, post.CaptionInfo.SuggestedTags);
            Assert.Equal(new[] { "the supreme leader approves", "red lighting" }, post.CaptionInfo.DroppedTags);
            Assert.True(post.CaptionInfo.TagsResearched);
            // The model is asked for more tags than the post will carry, so there is something to choose from.
            Assert.Contains("up to 8 Tumblr tags", h.Model.Calls.Single().System);
            Assert.Contains(h.Store.Read(s => s.Events.Select(e => e.Kind).ToList()), k => k == "tags.dropped");
        }

        [Fact]
        public async Task WithResearchOffForTheBlog_TheModelsTagsGoOnAsBefore()
        {
            using var h = new Harness(Now);
            string blogId = AutopilotBlog(h, s => s.ResearchTags = false);
            h.AddReel("a");
            await h.ResearchPlanner.PlanBlogAsync(blogId, CancellationToken.None);

            await h.ResearchPlanner.PrepareDueAsync(5, CancellationToken.None);

            var post = Assert.Single(h.Posts(blogId));
            Assert.Equal(new[] { "memes", "programming", "relatable" }, post.Tags);
            Assert.False(post.CaptionInfo.TagsResearched);
            Assert.Contains("up to 3 Tumblr tags", h.Model.Calls.Single().System);
            Assert.DoesNotContain(h.Api.Calls, c => c.StartsWith("tagged:"));
        }

        [Fact]
        public async Task SwappingAPostsContent_TakesTheOldClipsTagsOff()
        {
            using var h = new Harness(Now);
            string blogId = AutopilotBlog(h);
            h.AddReel("first", views: 9000);
            h.AddReel("second", views: 10);
            await h.Planner.PlanBlogAsync(blogId, CancellationToken.None);
            h.Model.Replies.Enqueue(() => "{\"caption\": \"about the first clip\", \"tags\": [\"tennis\", \"rolex\"]}");
            await h.Planner.PrepareDueAsync(5, CancellationToken.None);
            string postId = h.Posts(blogId).Single().PostId;
            Assert.Equal(new[] { "memes", "tennis", "rolex" }, h.Post(postId).Tags);

            var (ok, _) = await h.Planner.SwapContentAsync(postId, CancellationToken.None);
            Assert.True(ok);
            Assert.Equal(new[] { "memes" }, h.Post(postId).Tags); // until Nov 2026 "tennis" and "rolex" stayed on

            h.Model.Replies.Enqueue(() => "{\"caption\": \"about the second clip\", \"tags\": [\"south park\"]}");
            await h.Planner.PrepareDueAsync(5, CancellationToken.None);
            Assert.Equal(new[] { "memes", "south park" }, h.Post(postId).Tags);
        }

        [Fact]
        public async Task RewritingACaption_ReplacesTheTagsTheEarlierCaptionAdded()
        {
            using var h = new Harness(Now);
            string blogId = AutopilotBlog(h, s => s.FixedTags = new List<string> { "memes", "funny" });
            h.AddReel("a");
            await h.Planner.PlanBlogAsync(blogId, CancellationToken.None);
            h.Model.Replies.Enqueue(() => "{\"caption\": \"first go\", \"tags\": [\"funny\", \"cats\"]}");
            await h.Planner.PrepareDueAsync(5, CancellationToken.None);
            string postId = h.Posts(blogId).Single().PostId;
            Assert.Equal(new[] { "memes", "funny", "cats" }, h.Post(postId).Tags);
            Assert.Equal(new[] { "cats" }, h.Post(postId).CaptionInfo.SuggestedTags); // "funny" was the blog's own already

            h.Model.Replies.Enqueue(() => "{\"caption\": \"second go\", \"tags\": [\"dogs\"]}");
            await h.Planner.PreparePostAsync(postId, forceRegenerate: true, extraInstruction: null, CancellationToken.None);

            Assert.Equal(new[] { "memes", "funny", "dogs" }, h.Post(postId).Tags);
        }

        // ─────────────────────────────── The caption model's reply ───────────────────────────────

        private static readonly AiCaptionSettings Settings = new() { MaxLength = 80, SuggestTags = true, MaxSuggestedTags = 3 };

        [Fact]
        public void AReplyCutOffPartWay_KeepsTheTagsThatArrivedWhole()
        {
            const string cutOff = "{\"caption\": \"chat velocity now exceeds escape velocity\", \"tags\": [\"will smith\", \"ishowspeed\", \"stream";

            var result = OmniTumblrCaptioner.ParseReply(cutOff, Settings, maxTags: 8)!;

            Assert.Equal("chat velocity now exceeds escape velocity", result.Caption);
            Assert.Equal(new[] { "will smith", "ishowspeed" }, result.Tags);
        }

        [Fact]
        public async Task AReplyWithNoTags_IsAskedForAgainOnce_AndStillUsedIfTheSecondHasNoneEither()
        {
            var model = new FakeCaptionModel();
            var captioner = new OmniTumblrCaptioner(model);
            var request = new CaptionRequest { Settings = Settings, BlogName = "memeblog" };

            model.Replies.Enqueue(() => "{\"caption\": \"cut off before the tags\", \"tags\": [\"ca");
            model.Replies.Enqueue(() => "{\"caption\": \"second time lucky\", \"tags\": [\"cats\"]}");
            var result = await captioner.GenerateAiAsync(request, CancellationToken.None);
            Assert.Equal("second time lucky", result.Caption);
            Assert.Equal(new[] { "cats" }, result.Tags);
            Assert.Equal(2, model.Calls.Count);
            Assert.Contains("had no tags", model.Calls[1].User);

            model.Calls.Clear();
            model.Replies.Enqueue(() => "{\"caption\": \"nothing recognisable\", \"tags\": []}");
            model.Replies.Enqueue(() => "{\"caption\": \"still nothing\", \"tags\": []}");
            var none = await captioner.GenerateAiAsync(request, CancellationToken.None);
            Assert.Equal("still nothing", none.Caption);
            Assert.Empty(none.Tags);
            Assert.Equal(2, model.Calls.Count);
        }

        [Fact]
        public void ThePrompt_AsksForTagsPeopleFollow_NotDescriptionsOfTheFrame()
        {
            string prompt = OmniTumblrCaptioner.BuildSystemPrompt(new CaptionRequest { Settings = Settings, BlogName = "memeblog", TagCandidates = 12 });

            Assert.Contains("up to 12 Tumblr tags", prompt);
            Assert.Contains("already browse it", prompt);
            Assert.Contains("Never describe what the frame looks like", prompt);
            Assert.Contains("Do not guess who someone is", prompt);

            string off = OmniTumblrCaptioner.BuildSystemPrompt(new CaptionRequest { Settings = new AiCaptionSettings { SuggestTags = false }, BlogName = "memeblog", TagCandidates = 12 });
            Assert.DoesNotContain("Tumblr tags", off);
        }

        // ─────────────────────────────── Sync: the blog's own tags, and is it listed? ───────────────────────────────

        [Fact]
        public async Task RefreshingABlog_LooksUpItsOwnTags()
        {
            using var h = new Harness(Now);
            string blogId = AutopilotBlog(h, s =>
            {
                s.FixedTags = new List<string> { "memes", "funny" };
                s.RotatingTags = new List<string> { "real madrid" };
            });
            SeedTheNovemberTags(h);

            await h.ResearchSync.SyncBlogNowAsync(blogId, CancellationToken.None);

            Assert.Equal(TagVerdict.Crowded, h.TagResearch.Known("funny")!.Verdict);
            Assert.Equal(TagVerdict.Active, h.TagResearch.Known("real madrid")!.Verdict);
            Assert.Equal(TagVerdict.Dead, h.TagResearch.Known("memes")!.Verdict); // nothing seeded under it

            // Survives a restart.
            await h.Store.FlushAsync();
            var reloaded = new OmniTumblrStore(h.Store.Root, _ => { }, h.Store.Vault);
            reloaded.Load();
            Assert.Equal(TagVerdict.Crowded, reloaded.Read(s => s.Tags["funny"].Verdict));
        }

        private static string PublishedPost(Harness h, string blogId, string tumblrId, TimeSpan age, params string[] tags) =>
            h.AddReadyPost(blogId, p =>
            {
                p.Status = PostStatus.Published;
                p.TumblrPostId = tumblrId;
                p.PublishedUtc = h.Clock.Now - age;
                p.Tags = tags.ToList();
            });

        [Fact]
        public async Task ARecentPostFoundInItsTagsListing_MeansTheBlogIsListed()
        {
            using var h = new Harness(Now);
            string blogId = AutopilotBlog(h);
            h.SeedTag("real madrid", 30, TimeSpan.FromMinutes(100), notes: 6);
            h.SeedTag("funny", 60, TimeSpan.FromSeconds(30), notes: 0);
            await h.TagResearch.ResearchAsync(new[] { "real madrid", "funny" }, CancellationToken.None);
            PublishedPost(h, blogId, "777", TimeSpan.FromHours(3), "funny", "real madrid");
            h.Api.Tagged["real madrid"].Add(new TumblrPostSummary { Id = "777", BlogName = "memeblog", PublishedUtc = Now.AddHours(-3) });
            h.Api.Calls.Clear();

            Assert.True(await h.TagResearch.CheckListedAsync(blogId, CancellationToken.None));

            var search = h.Blog(blogId).Health.Search;
            Assert.True(search.Listed);
            Assert.Equal("real madrid", search.Tag); // the slow tag: in #funny the post is pages down within minutes
            Assert.Equal(new[] { "tagged:real madrid" }, h.Api.Calls.ToArray());
        }

        [Fact]
        public async Task ThreeDifferentPostsMissingFromTheirTags_FlagTheBlogAsUnlisted()
        {
            using var h = new Harness(Now);
            string blogId = AutopilotBlog(h);
            h.SeedTag("real madrid", 30, TimeSpan.FromMinutes(100), notes: 6);
            await h.TagResearch.ResearchAsync(new[] { "real madrid" }, CancellationToken.None);

            PublishedPost(h, blogId, "1", TimeSpan.FromHours(3), "real madrid");
            Assert.True(await h.TagResearch.CheckListedAsync(blogId, CancellationToken.None));
            Assert.True(await h.TagResearch.CheckListedAsync(blogId, CancellationToken.None)); // the same post again is not a second miss
            Assert.Equal(1, h.Blog(blogId).Health.Search.Misses);
            Assert.Null(h.Blog(blogId).Health.Search.Listed);

            for (int day = 2; day <= 3; day++)
            {
                h.Clock.Advance(TimeSpan.FromHours(20));
                PublishedPost(h, blogId, day.ToString(), TimeSpan.FromHours(3), "real madrid");
                Assert.True(await h.TagResearch.CheckListedAsync(blogId, CancellationToken.None));
            }

            var search = h.Blog(blogId).Health.Search;
            Assert.False(search.Listed);
            Assert.Equal(3, search.Misses);
            Assert.Contains(h.Store.Read(s => s.Events.Select(e => e.Kind).ToList()), k => k == "reach.unlisted");

            // Found again: the flag clears.
            h.Clock.Advance(TimeSpan.FromHours(20));
            PublishedPost(h, blogId, "4", TimeSpan.FromHours(3), "real madrid");
            h.Api.Tagged["real madrid"].Add(new TumblrPostSummary { Id = "4", BlogName = "memeblog", PublishedUtc = h.Clock.Now.AddHours(-3) });
            Assert.True(await h.TagResearch.CheckListedAsync(blogId, CancellationToken.None));
            Assert.True(h.Blog(blogId).Health.Search.Listed);
            Assert.Equal(0, h.Blog(blogId).Health.Search.Misses);
        }

        [Fact]
        public async Task WithNoPostThatMustStillBeOnPageOne_NothingIsConcluded()
        {
            using var h = new Harness(Now);
            string blogId = AutopilotBlog(h);
            h.SeedTag("funny", 60, TimeSpan.FromSeconds(30), notes: 0);
            await h.TagResearch.ResearchAsync(new[] { "funny" }, CancellationToken.None);
            PublishedPost(h, blogId, "1", TimeSpan.FromHours(3), "funny"); // hundreds of posts have been filed since
            h.Api.Calls.Clear();

            Assert.False(await h.TagResearch.CheckListedAsync(blogId, CancellationToken.None));

            Assert.Null(h.Blog(blogId).Health.Search.Listed);
            Assert.Empty(h.Api.Calls);
        }

        // ─────────────────────────────── The reach check ───────────────────────────────

        [Fact]
        public async Task TheReachCheck_NamesWhatIsHoldingTheBlogBack()
        {
            using var h = new Harness(Now);
            string blogId = AutopilotBlog(h, s =>
            {
                s.FixedTags = new List<string> { "funny" };
                s.Slots = Enum.GetValues<DayOfWeek>().Select(d => new WeeklySlot(d, 18 * 60)).ToList(); // once a day
                s.Ai.MaxSuggestedTags = 5;
            });
            h.SeedTag("funny", 60, TimeSpan.FromSeconds(30), notes: 0);
            h.SeedTag("funny", 30, TimeSpan.FromSeconds(30), notes: 0, newestAge: TimeSpan.FromHours(25));
            h.SeedTag("real madrid", 30, TimeSpan.FromMinutes(100), notes: 6);
            await h.TagResearch.ResearchAsync(new[] { "funny", "real madrid" }, CancellationToken.None);
            for (int i = 0; i < 6; i++)
            {
                string postId = PublishedPost(h, blogId, "p" + i, TimeSpan.FromDays(2 + i), "funny", i == 0 ? "real madrid" : "plastic chair");
                if (i == 0) h.Store.Mutate(s => s.FindPost(postId)!.Metrics.Notes = 5);
            }
            h.Store.Mutate(s =>
            {
                var blog = s.Blog(blogId)!;
                blog.Description = "";
                blog.Stats.InfoSyncedUtc = Now;
                blog.Stats.HeaderImageUrl = "https://assets.tumblr.com/images/default_header/optica_pattern_11.png";
            });

            var report = h.Store.Read(s => OmniTumblrReach.Build(s, s.Blog(blogId)!, Now, tagResearchEnabled: true));

            Assert.Equal(6, report.Posts30d);
            Assert.Equal(1, report.WithNotes30d);
            Assert.Equal(1, report.PostsPerDay);
            var titles = report.Findings.Select(f => f.Title).ToList();
            Assert.Contains("5 of the last 6 posts got no notes at all", titles);
            Assert.Contains("#funny is flooded", titles);
            Assert.Contains("Posts once a day", titles);
            Assert.Contains("Only 5 suggested tag(s) fit on a post", titles);
            Assert.Contains(titles, t => t.StartsWith("The blog page looks unfinished: no description, the default header image"));
            Assert.Contains("Suggested tags are checked against Tumblr", titles);
            Assert.Equal("problem", report.Findings[0].Level); // worst first

            var funny = report.Tags.Single(t => t.Tag == "funny");
            Assert.Equal("fixed", funny.Source);
            Assert.Equal("Crowded", funny.Verdict);
            Assert.Equal(6, funny.OurPosts);
            var madrid = report.Tags.Single(t => t.Tag == "real madrid");
            Assert.Equal("ai", madrid.Source);
            Assert.Equal(5, madrid.OurAvgNotes);
            Assert.Equal("real madrid", report.Tags.First(t => t.Source == "ai").Tag); // the tag that earned notes leads
            Assert.Equal("Unknown", report.Tags.Single(t => t.Tag == "plastic chair").Verdict);
        }

        [Fact]
        public void AnUnlistedBlog_IsTheFirstFinding()
        {
            using var h = new Harness(Now);
            string blogId = AutopilotBlog(h);
            h.Store.Mutate(s => s.Blog(blogId)!.Health.Search = new SearchVisibility { Listed = false, Misses = 3, Tag = "joker", CheckedUtc = Now });

            var report = h.Store.Read(s => OmniTumblrReach.Build(s, s.Blog(blogId)!, Now, tagResearchEnabled: true));

            Assert.Equal("Tumblr is not listing this blog's posts under their tags", report.Findings[0].Title);
            Assert.Equal("problem", report.Findings[0].Level);
        }
    }
}
