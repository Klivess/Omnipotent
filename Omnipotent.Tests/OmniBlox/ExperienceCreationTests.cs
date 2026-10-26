using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Pipeline;
using Omnipotent.Services.OmniBlox.Roblox;
using Xunit;

namespace Omnipotent.Tests.OmniBlox
{
    /// <summary>
    /// The studio makes its own experiences: through the owner's session when a game needs one and none is free,
    /// made private, named and checked to be within the API key's reach before a game gets it; sent once, backed
    /// off and capped so it never makes a pile of them; and the owner asked only when a person is needed.
    /// </summary>
    public class ExperienceCreationTests
    {
        private const string CreateUrl = "https://apis.roblox.com/universes/v1/universes/create";
        private static readonly CancellationToken None = CancellationToken.None;

        /// <summary>A studio with the owner's session and no experiences of the owner's.</summary>
        private static StudioHarness Studio(bool session = true)
        {
            var h = new StudioHarness(session: session);
            h.Ctx.Settings.Slots.Clear();
            return h;
        }

        private static string SeedGame(StudioHarness h, string id, string title = "Lava Forest Obby", GameStage stage = GameStage.Build)
        {
            h.Ctx.Store.Mutate(s =>
            {
                s.Games[id] = new GameDossier { GameId = id, WorkingTitle = title, Stage = stage, CreatedUtc = h.Now, UpdatedUtc = h.Now, StageEnteredUtc = h.Now };
                s.MarkGame(id);
            });
            return id;
        }

        private static List<CreatedExperience> Created(StudioHarness h) => h.Ctx.Store.Read(s => s.Studio.CreatedExperiences.Select(OmniBloxStore.Clone).ToList());

        private static List<OwnerTask> Open(StudioHarness h) => h.Ctx.Store.Read(s => s.Studio.OwnerTasks.Where(t => t.Status == OwnerTaskStatus.Open).Select(OmniBloxStore.Clone).ToList());

        [Fact]
        public async Task WhenNoneIsFree_TheStudioCreatesOne_PrivateNamedAndReachable()
        {
            using var h = Studio();
            string id = SeedGame(h, "g1");
            Assert.True(StageGates.EngineAvailable(h.Ctx, h.Game(id))); // Roblox's engine, not the local one: an experience can be made

            var slot = await Slots.EnsureAsync(h.Ctx, id, None);

            long universe = FakeRoblox.FirstCreatedUniverse;
            Assert.NotNull(slot);
            Assert.Equal((universe, universe + 1), (slot!.UniverseId, slot.PlaceId));
            Assert.Equal(1, h.Roblox.CreateCalls);
            var create = h.Roblox.Requests.Last(r => r.Url == CreateUrl);
            Assert.Equal("POST", create.Method);
            Assert.Equal(RobloxSessionClient.BaseplateTemplate, (long)JObject.Parse(create.Body)["templatePlaceId"]!);
            Assert.False((bool)JObject.Parse(create.Body)["isPublish"]!);

            // Made private (which proves the key reaches it) and named for its game, before the game got it.
            Assert.Contains(h.Roblox.Requests, r => r.Method == "PATCH" && r.Url.EndsWith($"/cloud/v2/universes/{universe}?updateMask=visibility") && r.Body.Contains("PRIVATE"));
            Assert.Contains(h.Roblox.Requests, r => r.Method == "PATCH" && r.Url.Contains($"/cloud/v2/universes/{universe}/places/{universe + 1}?updateMask=displayName") && r.Body.Contains("OmniBlox · Lava Forest Obby"));
            var made = Assert.Single(Created(h));
            Assert.Equal((ExperiencePurpose.Game, id, true), (made.Purpose, made.MadeFor, made.KeyReaches));
            Assert.Equal(universe, h.Game(id).Slot!.UniverseId);
            Assert.Equal(id, h.Ctx.Store.Read(s => s.Studio.SlotAssignments[$"{universe}:{universe + 1}"]));
            Assert.Contains(h.Ctx.Store.Read(s => s.Events.ToList()), e => e.Kind == "experience.created" && e.GameId == id);

            // The game keeps it; nothing more is made.
            Assert.Equal(universe, (await Slots.EnsureAsync(h.Ctx, id, None))!.UniverseId);
            Assert.Equal(1, h.Roblox.CreateCalls);
            Assert.Empty(Open(h));
        }

        [Fact]
        public async Task TheOwnersExperiencesComeFirst_AndAnAbandonedGamesIsReused()
        {
            using var h = new StudioHarness(session: true); // the owner listed 1001:2002
            Assert.Equal(1001, (await Slots.EnsureAsync(h.Ctx, SeedGame(h, "g1"), None))!.UniverseId);
            Assert.Equal(0, h.Roblox.CreateCalls);

            Assert.Equal(FakeRoblox.FirstCreatedUniverse, (await Slots.EnsureAsync(h.Ctx, SeedGame(h, "g2", "Timber Tycoon"), None))!.UniverseId);
            Assert.Equal(1, h.Roblox.CreateCalls);

            // g2 is abandoned: the next game takes its experience instead of making another.
            Orchestrator.Abandon(h.Ctx, "g2", "not fun");
            Assert.Equal(FakeRoblox.FirstCreatedUniverse, (await Slots.EnsureAsync(h.Ctx, SeedGame(h, "g3", "Pet Sim"), None))!.UniverseId);
            Assert.Equal(1, h.Roblox.CreateCalls);
            Assert.Equal((2, 0), Slots.Count(h.Ctx));
        }

        [Fact]
        public async Task GroupStudios_CreateUnderTheGroup()
        {
            using var h = Studio();
            h.Ctx.Settings.CreatorGroupId = 777;
            await Slots.EnsureAsync(h.Ctx, SeedGame(h, "g1"), None);
            Assert.Contains(h.Roblox.Requests, r => r.Url == CreateUrl + "?groupId=777");
        }

        [Fact]
        public async Task WithoutASession_TheOwnerIsAskedOnce_AndTheLocalEngineStandsIn()
        {
            using var h = Studio(session: false);
            string id = SeedGame(h, "g1");
            Assert.False(StageGates.EngineAvailable(h.Ctx, h.Game(id)));
            Assert.Null(await Slots.EnsureAsync(h.Ctx, id, None));
            Assert.Equal(0, h.Roblox.CreateCalls);

            string why = Slots.Unavailable(h.Ctx, ask: true);
            Assert.Contains("session", why);
            Slots.Unavailable(h.Ctx, ask: true);
            var task = Assert.Single(Open(h));
            Assert.Equal((OwnerTaskKind.ExperienceSlot, Slots.OwnerKey), (task.Kind, task.DedupeKey));
            Assert.Contains("OmniBlox_RobloxSessionCookie", task.Detail);
            Assert.Contains("OmniBlox_ExperienceSlots", task.Detail);
            var banner = ((JArray)JObject.Parse(OmniBloxViews.Serialize(OmniBloxViews.Overview(h.Ctx, new StudioStatus("idle", null, true, null, null, null))))["Attention"]!).Select(a => (string)a["Title"]!);
            Assert.DoesNotContain("OmniBlox cannot create experiences", banner); // the task says it

            // The owner sets the session: the next game that needs one gets one made.
            h.Ctx.Settings.SessionCookie = "cookie-1";
            Assert.True(StageGates.EngineAvailable(h.Ctx, h.Game(id)));
            Assert.NotNull(await Slots.EnsureAsync(h.Ctx, id, None));
            Assert.Empty(Open(h));
        }

        [Fact]
        public async Task AKeyThatCannotReachIt_AsksTheOwnerOnce_AndNoGameGetsItUntilItCan()
        {
            using var h = Studio();
            h.Roblox.KeyReachesNew = false;
            string id = SeedGame(h, "g1");

            Assert.Null(await Slots.EnsureAsync(h.Ctx, id, None));
            Assert.Null(h.Game(id).Slot);
            Assert.False(Assert.Single(Created(h)).KeyReaches);
            var task = Assert.Single(Open(h));
            Assert.Equal(Slots.ReachKey, task.DedupeKey);
            Assert.Contains("Restrict by Experience", task.Detail);

            // Making another would not help.
            Assert.Contains("cannot reach", Slots.WhyNotCreating(h.Ctx));
            Assert.Null(await Slots.EnsureAsync(h.Ctx, id, None));
            Assert.Equal(1, h.Roblox.CreateCalls);

            // The owner widens the key; housekeeping notices within a quarter of an hour.
            h.Roblox.KeyUnreachable.Clear();
            await Slots.SweepAsync(h.Ctx, None);
            Assert.False(Assert.Single(Created(h)).KeyReaches); // not due yet
            h.Now += Slots.ReachRecheck;
            await Slots.SweepAsync(h.Ctx, None);
            Assert.True(Assert.Single(Created(h)).KeyReaches);
            Assert.Empty(Open(h));
            Assert.Equal(FakeRoblox.FirstCreatedUniverse, (await Slots.EnsureAsync(h.Ctx, id, None))!.UniverseId);
            Assert.Equal(1, h.Roblox.CreateCalls);
        }

        [Fact]
        public async Task WhenRobloxRefuses_EachTryIsSentOnce_BacksOff_AndTheOwnerHearsAfterFour()
        {
            using var h = Studio();
            h.Roblox.CreateRefused = true;
            string id = SeedGame(h, "g1");

            for (int attempt = 1; attempt <= Slots.EscalateAfterFailures; attempt++)
            {
                Assert.Null(await Slots.EnsureAsync(h.Ctx, id, None));
                Assert.Equal(attempt, h.Roblox.CreateCalls); // a 500 is not retried: the experience may have been made
                Assert.Null(await Slots.EnsureAsync(h.Ctx, id, None)); // waiting out the failure
                Assert.Equal(attempt, h.Roblox.CreateCalls);
                Assert.False(StageGates.EngineAvailable(h.Ctx, h.Game(id)));
                Assert.Equal(attempt == Slots.EscalateAfterFailures, Open(h).Any(t => t.DedupeKey == Slots.CreateKey));
                h.Now += StorePageChores.MaxRetry;
            }
            Assert.Contains("ExperienceSlots", Open(h).Single(t => t.DedupeKey == Slots.CreateKey).Detail);

            h.Roblox.CreateRefused = false;
            Assert.NotNull(await Slots.EnsureAsync(h.Ctx, id, None));
            Assert.Empty(Open(h));
            Assert.Empty(h.Ctx.Store.Read(s => s.Studio.Chores.Keys.ToList()));
        }

        [Fact]
        public async Task ASignedOutSession_IsOneTask_AndASessionSignedInAgainIsUsedWithinHalfAnHour()
        {
            using var h = Studio();
            h.Roblox.AcceptedCookie = "cookie-2";
            string id = SeedGame(h, "g1");
            Assert.Null(await Slots.EnsureAsync(h.Ctx, id, None));
            Assert.Equal(StorePageChores.SessionKey, Assert.Single(Open(h)).DedupeKey);

            h.Ctx.Settings.SessionCookie = "cookie-2";
            h.Now += TimeSpan.FromMinutes(30);
            Assert.NotNull(await Slots.EnsureAsync(h.Ctx, id, None));
            Assert.Empty(Open(h));
        }

        [Fact]
        public async Task NoMoreThanFourAreMadeInADay()
        {
            using var h = Studio();
            for (int i = 0; i < Slots.MaxCreatedPerDay; i++) Assert.NotNull(await Slots.EnsureAsync(h.Ctx, SeedGame(h, "g" + i), None));
            string late = SeedGame(h, "g-late");
            Assert.Null(await Slots.EnsureAsync(h.Ctx, late, None));
            Assert.Contains("the most it makes in one", Slots.Unavailable(h.Ctx, ask: true));
            Assert.Empty(Open(h)); // nothing for the owner to do: it waits
            Assert.Equal(Slots.MaxCreatedPerDay, h.Roblox.CreateCalls);

            h.Now += TimeSpan.FromDays(1);
            Assert.NotNull(await Slots.EnsureAsync(h.Ctx, late, None));
        }

        [Fact]
        public async Task TheLaunchCreatesItsExperience_AndWaitsWithTheReasonWhenItCannot()
        {
            using var h = Studio(session: false);
            string id = SeedGame(h, "g1", stage: GameStage.Launch);
            var (result, message) = await LaunchSequence.RunAsync(h.Ctx, id, None);
            Assert.Equal(LaunchResult.Waiting, result);
            Assert.Contains("session", message);
            Assert.Equal(Slots.OwnerKey, Assert.Single(Open(h)).DedupeKey);

            h.Ctx.Settings.SessionCookie = "cookie-1";
            await LaunchSequence.RunAsync(h.Ctx, id, None);
            Assert.Equal(FakeRoblox.FirstCreatedUniverse, h.Game(id).Slot?.UniverseId);
            Assert.NotEqual("experience slot", h.Game(id).Publish.LastLaunchStep);
        }

        [Fact]
        public async Task AGameBuiltWithNoOwnerExperiences_IsTestedInRobloxsEngine_InOneTheStudioMade()
        {
            using var h = Studio();
            await h.Harvester.RunOnceAsync(None);
            PipelineEndToEndTests.ScriptResearch(h.Llm);
            PipelineEndToEndTests.ScriptBuild(h.Llm);
            await h.StepUntilAsync(() => h.Ctx.Store.Read(s => s.Games.Count) == 1 && h.OnlyGame().Stage == GameStage.Test);

            var game = h.OnlyGame();
            Assert.Equal(FakeRoblox.FirstCreatedUniverse, game.Slot?.UniverseId);
            Assert.False(game.Build.LastEngineRun?.Local);
            Assert.Equal(1, h.Roblox.CreateCalls);
            Assert.Contains(h.Roblox.Requests, r => r.Url.Contains($"/universes/v1/{FakeRoblox.FirstCreatedUniverse}/places/{FakeRoblox.FirstCreatedUniverse + 1}/versions"));
        }
    }
}
