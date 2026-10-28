using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Pipeline;
using Xunit;

namespace Omnipotent.Tests.OmniBlox
{
    /// <summary>
    /// The owner's actions on a game's place on Roblox (update, private/public, take down and archive, move to another
    /// experience or account), "do it yourself" on their tasks, and switching the studio to another Roblox account.
    /// </summary>
    public class GameActionsTests
    {
        private static readonly CancellationToken None = CancellationToken.None;

        private static JObject Json(object view) => JObject.Parse(OmniBloxViews.Serialize(view));

        private static List<OwnerTask> Open(StudioHarness h) => h.Ctx.Store.Read(s => s.Studio.OwnerTasks.Where(t => t.Status == OwnerTaskStatus.Open).Select(OmniBloxStore.Clone).ToList());

        /// <summary>A game taken from idea to live through both owner gates (the real pipeline, scripted model, fake Roblox).</summary>
        private static async Task<string> LaunchedGameAsync(StudioHarness h)
        {
            await h.Harvester.RunOnceAsync(None);
            PipelineEndToEndTests.ScriptResearch(h.Llm);
            PipelineEndToEndTests.ScriptBuild(h.Llm);
            PipelineEndToEndTests.ScriptReview(h.Llm);
            PipelineEndToEndTests.ScriptMarketing(h.Llm);
            await h.StepUntilAsync(() => h.Ctx.Store.Read(s => s.Games.Count) == 1 && h.OnlyGame().WaitingReason?.Contains("sign it off") == true, 40);
            string id = h.OnlyGame().GameId;
            Approvals.Approve(h.Ctx, id, Approvals.BuildGate);
            await h.StepUntilAsync(() => h.Game(id).WaitingReason?.Contains("launch approval") == true, 40);
            Approvals.Approve(h.Ctx, id, Approvals.LaunchGate);
            await h.StepUntilAsync(() => h.Game(id).Stage == GameStage.Live);
            return id;
        }

        /// <summary>The seeded live game, as if published as version 5 and public.</summary>
        private static string PublicGame(StudioHarness h)
        {
            string id = StorePageChoresTests.SeedLiveGame(h);
            h.Ctx.Update(id, g => { g.Publish.LastPublishedVersion = 5; g.Publish.IsPublic = true; g.Publish.WentPublicUtc = h.Now.AddDays(-2); });
            return id;
        }

        [Fact]
        public async Task UpdateOnRoblox_PutsEverythingThereAgain()
        {
            using var h = new StudioHarness(requireApproval: true, session: true);
            string id = await LaunchedGameAsync(h);
            int before = h.Roblox.Requests.Count;

            var action = await h.ActAsync(id, "sync");
            Assert.True(action.Ok, action.Message + " | " + string.Join(" | ", action.Steps));
            var sent = h.Roblox.Requests.Skip(before).ToList();
            Assert.Contains(sent, r => r.Method == "PATCH" && r.Url.Contains("/game-passes/v1/")); // products updated, not created again
            Assert.DoesNotContain(sent, r => r.Method == "POST" && r.Url.Contains("/game-passes/v1/"));
            Assert.Contains(sent, r => r.Url.Contains("versionType=Published"));
            Assert.Contains(sent, r => r.Url == "https://publish.roblox.com/v1/games/1001/icon");
            Assert.Contains(action.Steps, s => s.StartsWith("Published the current build"));
            Assert.Contains(action.Steps, s => s == "Roblox lets players join it.");
            Assert.Contains(h.Ctx.Store.Read(s => s.Events.ToList()), e => e.Kind == "owner.action" && e.Level == EventLevel.Success);
        }

        [Fact]
        public async Task PrivateThenPublic_AndPublicNeedsARating()
        {
            using var h = new StudioHarness();
            string id = PublicGame(h);

            var action = await h.ActAsync(id, "private");
            Assert.True(action.Ok, action.Message);
            Assert.False(h.Game(id).Publish.IsPublic);
            Assert.Contains(h.Roblox.Requests, r => r.Method == "PATCH" && r.Url.Contains("/cloud/v2/universes/1001") && r.Body.Contains("PRIVATE"));
            Assert.Contains("the owner made the experience private", PlayerReach.Describe(h.Game(id)));

            h.Roblox.Maturity = "unrated";
            action = await h.ActAsync(id, "public");
            Assert.False(action.Ok);
            Assert.Contains("no maturity rating", action.Message);
            Assert.False(h.Game(id).Publish.IsPublic);

            h.Roblox.Maturity = "minimal";
            action = await h.ActAsync(id, "public");
            Assert.True(action.Ok, action.Message);
            Assert.True(h.Game(id).Publish.IsPublic);
        }

        [Fact]
        public async Task TakingDown_StopsSalesAndMaintenance_AndPublicBringsItBack()
        {
            using var h = new StudioHarness();
            string id = PublicGame(h);

            var action = await h.ActAsync(id, "takedown");
            Assert.True(action.Ok, action.Message);
            var game = h.Game(id);
            Assert.Equal(GameStage.Sunset, game.Stage);
            Assert.False(game.Publish.IsPublic);
            Assert.Null(game.NextMaintenanceUtc);
            Assert.All(game.Products, p => Assert.True(p.Retired));
            Assert.Contains(h.Roblox.Requests, r => r.Method == "PATCH" && r.Url.Contains("/game-passes/v1/") && r.Body.Contains("isForSale") && r.Body.Contains("false"));

            action = await h.ActAsync(id, "public");
            Assert.True(action.Ok, action.Message);
            game = h.Game(id);
            Assert.Equal(GameStage.Live, game.Stage);
            Assert.True(game.Publish.IsPublic);
            Assert.All(game.Products, p => Assert.False(p.Retired));
            Assert.NotNull(game.NextMaintenanceUtc);
        }

        [Fact]
        public async Task TakingDownAndArchiving_RetiresTheExperienceForGood()
        {
            using var h = new StudioHarness(session: true);
            string id = PublicGame(h);
            var action = await h.ActAsync(id, "takedown", archive: true);
            Assert.True(action.Ok, action.Message);
            Assert.Equal(new long[] { 1001 }, h.Roblox.Archived);
            Assert.Contains(1001L, h.Ctx.Store.Read(s => s.Studio.RetiredExperiences.ToList()));
            Assert.DoesNotContain(Slots.Pool(h.Ctx), s => s.UniverseId == 1001);
            Assert.Contains("archived", h.Game(id).SunsetReason);
        }

        [Fact]
        public async Task MovingAGame_RelaunchesItInAFreshExperience_WithoutAskingForApprovalsAgain()
        {
            using var h = new StudioHarness(requireApproval: true, session: true);
            string id = await LaunchedGameAsync(h);
            var oldProducts = h.Game(id).Products.Select(p => p.RobloxId).ToList();
            int approvals = h.Game(id).Publish.Approvals.Count;

            var action = await h.ActAsync(id, "move");
            Assert.True(action.Ok, action.Message + " | " + string.Join(" | ", action.Steps));
            Assert.StartsWith("Live again, in experience " + FakeRoblox.FirstCreatedUniverse, action.Message);
            var game = h.Game(id);
            Assert.Equal(GameStage.Live, game.Stage);
            Assert.True(game.Publish.IsPublic);
            Assert.Equal(FakeRoblox.FirstCreatedUniverse, game.Slot!.UniverseId);
            Assert.All(game.Products, p => Assert.DoesNotContain(p.RobloxId, oldProducts)); // made again in the new experience
            Assert.Equal(approvals + 1, game.Publish.Approvals.Count); // the owner's move recorded as the launch approval
            Assert.Contains("moved to experience", game.Publish.Approvals[^1].Note);

            // The old experience: private, its products off sale, never handed out again.
            Assert.Contains(h.Roblox.Requests, r => r.Method == "PATCH" && r.Url.Contains("/cloud/v2/universes/1001") && r.Body.Contains("PRIVATE"));
            Assert.Contains(1001L, h.Ctx.Store.Read(s => s.Studio.RetiredExperiences.ToList()));
            Assert.DoesNotContain(Slots.Pool(h.Ctx), s => s.UniverseId == 1001);
            Assert.Contains(h.Ctx.Store.Read(s => s.Events.ToList()), e => e.Kind == "game.moved");
        }

        [Fact]
        public async Task MovingAGame_IntoAnExperienceTheOwnerNames()
        {
            using var h = new StudioHarness(session: true);
            string id = PublicGame(h);
            h.Ctx.Update(id, g => { g.Store.IconFile = "icon.png"; g.Store.ComplianceOk = true; });
            Assert.True(StageGates.MarketingReady(h.Game(id)));
            Assert.Throws<GameActionRefused>(() => h.Actions.Start(id, "move", target: "not-a-slot"));
            Assert.Throws<GameActionRefused>(() => h.Actions.Start(id, "move", target: "1001:2002"));

            // Its build is not in this harness's workspace, so the relaunch stops at the final build; the move itself is done.
            var action = await h.ActAsync(id, "move", target: "5005:6006");
            Assert.Equal(5005, h.Game(id).Slot!.UniverseId);
            Assert.Contains(action.Steps, s => s.Contains("your experience 5005"));
            Assert.Contains(1001L, h.Ctx.Store.Read(s => s.Studio.RetiredExperiences.ToList()));
            Assert.All(h.Game(id).Products, p => Assert.NotEqual(5001, p.RobloxId)); // never the old experience's product
        }

        [Fact]
        public void ActionsThatCannotStart_SayWhy()
        {
            using var h = new StudioHarness();
            string id = StorePageChoresTests.SeedLiveGame(h);
            Assert.Contains("Only a launched game", Assert.Throws<GameActionRefused>(() => h.Actions.Start(id, "sync")).Message);
            Assert.Contains("never been published", Assert.Throws<GameActionRefused>(() => h.Actions.Start(id, "public")).Message);
            Assert.Contains("needs your Roblox account", Assert.Throws<GameActionRefused>(() => h.Actions.Start(id, "takedown", archive: true)).Message);
            Assert.Contains("Unknown action", Assert.Throws<GameActionRefused>(() => h.Actions.Start(id, "explode")).Message);
            h.Ctx.Settings.ApiKey = null;
            Assert.Contains("API key", Assert.Throws<GameActionRefused>(() => h.Actions.Start(id, "private")).Message);
        }

        // ─────────────── "do it yourself" ───────────────

        [Fact]
        public async Task DoItYourself_TheIconWithTheOwnersAccount_OrAskForTheAccountFirst()
        {
            using var h = new StudioHarness();
            string id = StorePageChoresTests.SeedLiveGame(h);
            await StorePageChores.SweepAsync(h.Ctx, None);
            var icon = Assert.Single(Open(h), t => t.Kind == OwnerTaskKind.UploadIcon);
            Assert.Contains("connect it", Assert.Throws<GameActionRefused>(() => h.Actions.Delegate(icon.TaskId)).Message);

            h.Ctx.Settings.SessionCookie = "cookie-1";
            h.Actions.Delegate(icon.TaskId);
            await h.Actions.Running(id)!;
            Assert.True(h.Game(id).OwnerAction!.Ok, h.Game(id).OwnerAction!.Message);
            Assert.NotNull(h.Game(id).Publish.IconUploadedHash);
            Assert.DoesNotContain(Open(h), t => t.Kind == OwnerTaskKind.UploadIcon);
        }

        [Fact]
        public void DoItYourself_AnythingElseOnAGame_GoesToItsAgent_AndDecisionsStayTheOwners()
        {
            using var h = new StudioHarness();
            string id = StorePageChoresTests.SeedLiveGame(h);
            var (stuck, approval) = h.Ctx.Store.Mutate(s => (
                s.FileOwnerTask(OwnerTaskKind.Escalation, "stuck:" + id, "Make better thumbnails", "The owner would rather the agent made new thumbnails from the latest build.", id, h.Now),
                s.FileOwnerTask(OwnerTaskKind.LaunchApproval, "approve:" + id, "Approve the launch", "…", id, h.Now)));

            string message = h.Actions.Delegate(stuck.TaskId);
            Assert.Contains("agent", message);
            var game = h.Game(id);
            var directive = Assert.Single(game.Directives);
            Assert.Contains("handle this yourself", directive.Text);
            Assert.Contains("Make better thumbnails", directive.Text);
            Assert.True(game.UrgentMaintenance); // a live game's maintainer takes it up next
            Assert.DoesNotContain(Open(h), t => t.TaskId == stuck.TaskId);

            Assert.Contains("your decision", Assert.Throws<GameActionRefused>(() => h.Actions.Delegate(approval.TaskId)).Message);
        }

        [Fact]
        public async Task DoItYourself_AnExperience_IsMadeOnTheOwnersAccount()
        {
            using var h = new StudioHarness(session: true);
            StorePageChoresTests.SeedLiveGame(h); // it holds the only experience
            var task = h.Ctx.Store.Mutate(s => s.FileOwnerTask(OwnerTaskKind.ExperienceSlot, Slots.OwnerKey, "Let OmniBlox create its experiences", "…", null, h.Now));
            h.Actions.Delegate(task.TaskId);
            await (h.Actions.RunningForStudio() ?? Task.CompletedTask);
            Assert.Contains(h.Ctx.Store.Read(s => s.Studio.CreatedExperiences.ToList()), e => e.UniverseId == FakeRoblox.FirstCreatedUniverse);
            Assert.DoesNotContain(Open(h), t => t.TaskId == task.TaskId);
        }

        // ─────────────── another account ───────────────

        [Fact]
        public async Task SwitchingTheStudioToAnotherAccount_RetiresTheOldExperiences_AndFlagsItsGames()
        {
            using var h = new StudioHarness(session: true);
            string id = PublicGame(h);
            h.Roblox.SignedInUserId = 77;

            await h.Connection.StartAsync(None, switchAccount: true);
            h.Roblox.QuickLoginStatus = "Validated";
            var view = Json(await h.Connection.PollAsync(None));
            Assert.True((bool)view["Connected"]!, (string?)view["LastError"]);
            Assert.Equal(77, h.Ctx.Settings.CreatorUserId);
            Assert.Contains(1001L, h.Ctx.Store.Read(s => s.Studio.RetiredExperiences.ToList()));
            Assert.Empty(Slots.Pool(h.Ctx)); // no new game lands on the old account
            Assert.True(h.Game(id).Publish.OnOldAccount);
            Assert.Equal(id, (string)view["OldAccountGames"]![0]!["GameId"]!);

            // The old account's key is refused now; the new account's is kept, with what Roblox said about it.
            view = Json(await h.Connection.SetApiKeyAsync("old-key", None));
            Assert.Contains("belongs to user 42", (string)view["LastError"]!);
            h.Roblox.KeyOwner = 77;
            view = Json(await h.Connection.SetApiKeyAsync("new-key", None));
            Assert.Equal(JTokenType.Null, view["LastError"]!.Type);
            Assert.Equal("new-key", h.Ctx.Settings.ApiKey);
            Assert.Equal(77, (long)view["ApiKey"]!["Info"]!["OwnerUserId"]!);
            Assert.DoesNotContain("new-key", view.ToString());
        }

        [Fact]
        public async Task AnApiKeyRobloxDoesNotVouchFor_IsNotKept()
        {
            using var h = new StudioHarness();
            var view = Json(await h.Connection.SetApiKeyAsync("unknown-key", None));
            Assert.Contains("does not know this API key", (string)view["LastError"]!);
            h.Roblox.KeyEnabled = false;
            view = Json(await h.Connection.SetApiKeyAsync("disabled-key", None));
            Assert.Contains("disabled", (string)view["LastError"]!);
            Assert.Equal("test-key", h.Ctx.Settings.ApiKey);
        }

        [Fact]
        public void TheApiKeyRoute_IsKeptOutOfStoredRequestBodies()
        {
            OmniBloxRoutes.MarkSecretRoutes();
            Assert.True(Omnipotent.Services.KliveAPI.KliveAPI.IsSensitiveSettingsRoute("/omniblox/roblox/apikey"));
        }
    }
}
