using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Insights;
using Omnipotent.Services.OmniBlox.Pipeline;
using Xunit;

namespace Omnipotent.Tests.OmniBlox
{
    /// <summary>
    /// Whether players can join a public game. The first launched game (Oct 2026) went public with no maturity rating:
    /// Roblox answered ContextualPlayabilityUnrated / isPlayable false, nobody but the owner could open it for four days,
    /// and the studio called it live while its maintainer read the empty numbers as a slow start.
    /// </summary>
    public class PlayerReachTests
    {
        private static readonly CancellationToken None = CancellationToken.None;

        private static JObject Json(object view) => JObject.Parse(OmniBloxViews.Serialize(view));
        private static StudioStatus Status() => new("idle", null, true, null, null, null);

        /// <summary>A public game in experience 1001, launched a day ago.</summary>
        private static string SeedPublicGame(StudioHarness h, string id = "g-live")
        {
            h.Ctx.Store.Mutate(s =>
            {
                s.Games[id] = new GameDossier
                {
                    GameId = id,
                    WorkingTitle = "Pumpkin Patch",
                    Stage = GameStage.Live,
                    CreatedUtc = h.Now.AddDays(-3),
                    UpdatedUtc = h.Now,
                    StageEnteredUtc = h.Now.AddDays(-1),
                    Slot = new ExperienceSlot { UniverseId = 1001, PlaceId = 2002 },
                    Brief = new GameBrief { Title = "Pumpkin Patch", Genre = "Simulator", Pitch = "Squash pumpkins and sell the candy.", CoreLoop = "Squash, fill the bag, sell, upgrade." },
                    Store = new StorePage { Title = "Squash the Pumpkin Patch", Description = "Squash giant pumpkins, fill your bag and sell candy for coins.", Genre = "Simulator" },
                    Publish = new PublishRecord { IsPublic = true, WentPublicUtc = h.Now.AddDays(-1), FirstPublishedUtc = h.Now.AddDays(-1) },
                };
                s.Studio.SlotAssignments["1001:2002"] = id;
                s.MarkStudio();
                s.MarkGame(id);
            });
            return id;
        }

        private static List<OwnerTask> Open(StudioHarness h) => h.Ctx.Store.Read(s => s.Studio.OwnerTasks.Where(t => t.Status == OwnerTaskStatus.Open).Select(OmniBloxStore.Clone).ToList());

        [Fact]
        public void RobloxsAnswers_AsCapturedLive_ReadAsClosedAndOpen()
        {
            // multiget-playability-status as a signed-out visitor saw the unrated game and a joinable one (Oct 28 2026).
            Assert.False(PlayerReach.Interpret("ContextualPlayabilityUnrated", false));
            Assert.True(PlayerReach.Interpret("GuestProhibited", false));
            Assert.True(PlayerReach.Interpret("Playable", true));
            Assert.Null(PlayerReach.Interpret("SomethingRobloxAddsLater", false));
            Assert.Contains("maturity rating", PlayerReach.Why(new ReachRecord { Status = PlayerReach.Unrated, Playable = false }));
        }

        [Fact]
        public async Task TheClientReadsBothPublicEndpoints()
        {
            using var h = new StudioHarness(apiKey: false);
            h.Roblox.Playability = PlayerReach.Unrated;
            h.Roblox.Maturity = "unrated";
            var playability = Assert.Single(await h.Ctx.Public.GetPlayabilityAsync(new long[] { 1001 }, None));
            Assert.Equal((1001L, PlayerReach.Unrated, false), (playability.UniverseId, playability.Status, playability.IsPlayable));
            var rating = await h.Ctx.Public.GetMaturityAsync(1001, None);
            Assert.False(rating!.Rated);

            h.Roblox.Maturity = "minimal";
            Assert.True((await h.Ctx.Public.GetMaturityAsync(1001, None))!.Rated);
        }

        [Fact]
        public async Task AnUnratedPublicGame_IsFlaggedEverywhere_TheOwnerIsToldOnce_AndItClearsWhenTheOwnerAnswers()
        {
            using var h = new StudioHarness();
            string id = SeedPublicGame(h);
            h.Roblox.Playability = PlayerReach.Unrated;
            h.Roblox.Maturity = "unrated";

            var reach = await PlayerReach.CheckAsync(h.Ctx, id, None);
            Assert.False(reach!.Playable);
            Assert.Equal(PlayerReach.Unrated, h.Game(id).Publish.Reach!.Status);
            Assert.Equal(h.Game(id).Publish.WentPublicUtc, reach.SinceUtc); // never rated: closed since it went public

            // The owner: one Discord message, a questionnaire task that says what is at stake, an error on the dashboard.
            Assert.Contains("Nobody can play", Assert.Single(h.Alerts));
            var task = Assert.Single(Open(h), t => t.Kind == OwnerTaskKind.Questionnaire);
            Assert.Contains("nobody can play", task.Title);
            Assert.Contains("no players at all", task.Detail);
            var overview = Json(OmniBloxViews.Overview(h.Ctx, Status()));
            var first = ((JArray)overview["Attention"]!)[0];
            Assert.Equal("error", (string)first["Level"]!);
            Assert.StartsWith("Nobody can play", (string)first["Title"]!);
            Assert.False((bool)overview["Games"]![0]!["Reach"]!["Playable"]!);
            Assert.Contains(h.Ctx.Store.Read(s => s.Events.ToList()), e => e.Kind == "reach.closed");

            // The agents read the numbers knowing why they are empty.
            Assert.Contains("PLAYERS CANNOT JOIN", AnalyticsSync.Describe(h.Game(id)));
            Assert.Contains("PLAYERS CANNOT JOIN", Briefing.Build(h.Game(id), h.Ctx, ""));

            // Checked again later: still closed, but no second message.
            h.Now += LiveWatch.ReachEvery;
            await PlayerReach.CheckAsync(h.Ctx, id, None);
            Assert.Single(h.Alerts);

            // The owner answers in the Creator Dashboard: the studio sees the rating, records it as theirs, and closes the task.
            h.Roblox.Maturity = "minimal";
            h.Roblox.Playability = "GuestProhibited";
            h.Now += LiveWatch.ReachEvery;
            reach = await PlayerReach.CheckAsync(h.Ctx, id, None);
            Assert.True(reach!.Playable);
            Assert.DoesNotContain(Open(h), t => t.Kind == OwnerTaskKind.Questionnaire);
            Assert.True(h.Game(id).Publish.Questionnaire is { Published: true, ByOwner: true });
            Assert.Contains(h.Ctx.Store.Read(s => s.Events.ToList()), e => e.Kind == "reach.open");
            Assert.DoesNotContain(((JArray)Json(OmniBloxViews.Overview(h.Ctx, Status()))["Attention"]!), a => ((string)a["Title"]!).StartsWith("Nobody can play"));
            Assert.Null(PlayerReach.Describe(h.Game(id)));

            // Housekeeping does not file the questionnaire task again for an answer the owner gave.
            await StorePageChores.SweepAsync(h.Ctx, None);
            Assert.DoesNotContain(Open(h), t => t.Kind == OwnerTaskKind.Questionnaire);
        }

        [Fact]
        public async Task RobloxUnreachable_KeepsWhatWasKnown_AndAlertsNobody()
        {
            using var h = new StudioHarness();
            string id = SeedPublicGame(h);
            await PlayerReach.CheckAsync(h.Ctx, id, None);
            Assert.True(h.Game(id).Publish.Reach!.Playable);

            h.Roblox.Playability = PlayerReach.Unrated;
            h.Roblox.ReachDown = true;
            h.Now += LiveWatch.ReachEvery;
            var reach = await PlayerReach.CheckAsync(h.Ctx, id, None);
            Assert.True(reach!.Playable);
            Assert.NotNull(reach.Error);
            Assert.Empty(h.Alerts);
        }

        [Fact]
        public async Task TheLaunchWaitsForAMaturityRating_BeforeGoingPublic()
        {
            using var h = new StudioHarness();
            await h.Harvester.RunOnceAsync(None);
            PipelineEndToEndTests.ScriptResearch(h.Llm);
            PipelineEndToEndTests.ScriptBuild(h.Llm);
            PipelineEndToEndTests.ScriptReview(h.Llm);
            await h.StepUntilAsync(() => h.Ctx.Store.Read(s => s.Games.Count) == 1 && h.OnlyGame().Stage == GameStage.Launch, 60);

            // No session to answer the questionnaire with, and Roblox shows the experience unrated.
            h.Roblox.Maturity = "unrated";
            PipelineEndToEndTests.ScriptMarketing(h.Llm);
            await h.StepUntilAsync(() => h.OnlyGame().WaitingReason?.Contains("maturity rating") == true, 60);
            var game = h.OnlyGame();
            Assert.False(game.Publish.IsPublic);
            Assert.Equal(GameStage.Launch, game.Stage);
            Assert.DoesNotContain(h.Roblox.Requests, r => r.Method == "PATCH" && r.Body.Contains("PUBLIC"));
            Assert.Contains("nobody can play", Assert.Single(Open(h), t => t.Kind == OwnerTaskKind.Questionnaire).Title);

            // The owner answers it; the next attempt goes public, and the task closes by itself.
            h.Roblox.Maturity = "minimal";
            h.Now += Orchestrator.WaitRecheck;
            await h.StepUntilAsync(() => h.OnlyGame().Stage == GameStage.Live);
            game = h.OnlyGame();
            Assert.True(game.Publish.IsPublic);
            Assert.True(game.Publish.Reach!.Playable);
            Assert.DoesNotContain(Open(h), t => t.Kind == OwnerTaskKind.Questionnaire);
        }

        [Fact]
        public async Task TheWatchRunsWithTheStudioSwitchedOff()
        {
            using var h = new StudioHarness();
            string id = SeedPublicGame(h);
            h.Ctx.Settings.Enabled = false;
            h.Roblox.Playability = PlayerReach.Unrated;
            var watch = new LiveWatch(h.Ctx);

            await watch.StepAsync(None);
            var game = h.Game(id);
            Assert.False(game.Publish.Reach!.Playable);
            Assert.NotNull(game.Stats.LastSyncUtc);
            Assert.Single(h.Alerts);

            // Nothing is due a minute later; both run again once due.
            int requests = h.Roblox.Requests.Count;
            h.Now += TimeSpan.FromMinutes(1);
            await watch.StepAsync(None);
            Assert.Equal(requests, h.Roblox.Requests.Count);
            h.Now += LiveWatch.ReachEvery;
            await watch.StepAsync(None);
            Assert.Contains(h.Roblox.Requests.Skip(requests), r => r.Url.Contains("multiget-playability-status"));
        }

        [Fact]
        public async Task AMetricRobloxDoesNotServe_IsNoData_NotAnError_AndImpressionsAreRead()
        {
            using var h = new StudioHarness();
            string id = SeedPublicGame(h);
            h.Roblox.UnavailableMetrics.Add("PeakConcurrentPlayers"); // as the live game got on every sync

            string result = await h.Ctx.Analytics.SyncAsync(id, None);
            var game = h.Game(id);
            Assert.Null(game.Stats.LastSyncError);
            Assert.DoesNotContain("problems", result);
            var day = Assert.Single(game.Stats.Daily);
            Assert.Null(day.PeakConcurrent);
            Assert.Equal(5, day.Impressions);
            Assert.Equal(1.0, day.ClickThrough);
            Assert.Contains("impressions 5", AnalyticsSync.Describe(game));

            // A metric the game cannot do without is still an error.
            h.Roblox.UnavailableMetrics.Add("Visits");
            await h.Ctx.Analytics.SyncAsync(id, None);
            Assert.Contains("Visits", h.Game(id).Stats.LastSyncError);
        }

        // ─────────────── the dashboard's world results ───────────────

        [Fact]
        public async Task ConcurrentRequestsForOneWorldResult_ShareOneComputation()
        {
            var cache = new SingleFlightCache(TimeSpan.FromMinutes(10), 48, 2);
            var gate = new TaskCompletionSource();
            int builds = 0;
            object Build() { Interlocked.Increment(ref builds); gate.Task.Wait(); return new object(); }

            var waiting = Enumerable.Range(0, 8).Select(_ => cache.GetAsync("map|g1|abc", Build)).ToList();
            await Task.Delay(100);
            gate.SetResult();
            var results = await Task.WhenAll(waiting);
            Assert.Equal(1, builds);
            Assert.All(results, r => Assert.Same(results[0], r));
            Assert.Same(results[0], await cache.GetAsync("map|g1|abc", Build));
            Assert.Equal(1, builds);
        }

        [Fact]
        public async Task OnlyAFewWorldComputationsRunAtOnce()
        {
            var cache = new SingleFlightCache(TimeSpan.FromMinutes(10), 48, 2);
            var gate = new TaskCompletionSource();
            int running = 0, peak = 0;
            object Build()
            {
                int now = Interlocked.Increment(ref running);
                lock (cache) peak = Math.Max(peak, now);
                gate.Task.Wait();
                Interlocked.Decrement(ref running);
                return new object();
            }

            var all = Enumerable.Range(0, 6).Select(i => cache.GetAsync("render|g1|" + i, Build)).ToList();
            await Task.Delay(200);
            Assert.Equal(2, cache.Computing);
            gate.SetResult();
            await Task.WhenAll(all);
            Assert.Equal(2, peak);
        }

        [Fact]
        public async Task AFailedWorldComputation_IsNotKept()
        {
            var cache = new SingleFlightCache(TimeSpan.FromMinutes(10), 48, 2);
            int calls = 0;
            await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync<object>("map|g1|x", () => { calls++; throw new InvalidOperationException("the world is empty"); }));
            Assert.NotNull(await cache.GetAsync("map|g1|x", () => { calls++; return new object(); }));
            Assert.Equal(2, calls);
        }
    }
}
