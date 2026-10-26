using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox.Assets;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Pipeline;

namespace Omnipotent.Tests.OmniBlox
{
    /// <summary>
    /// The whole studio, wired to a scripted model and a fake Roblox: research → build → test → launch →
    /// maintenance → post-mortem, checking at every stage that the dossier, the files, the Roblox calls and
    /// the Playbook end up where they should.
    /// </summary>
    public class PipelineEndToEndTests
    {
        internal static void ScriptResearch(ScriptedLlm llm)
        {
            llm.Script("researcher",
                ("roblox_charts", new { top = 10 }),
                ("roblox_game_details", new { universe_ids = new[] { 111 } }),
                ("web_search", new { query = "roblox obby trends", time_range = "month" }),
                ("asset_coverage", new { themes = new[] { "pine tree", "lava" } }),
                ("save_finding", new { finding = "Lava Rush Obby has 5,400 players and a 90% like ratio." }),
                ("propose_concept", new { pitch = "A lava obby through a pine forest", genre = "obby", template = "obby", core_loop = "Jump across platforms over lava to reach the next checkpoint", twist = "forest setting", evidence = new[] { "lava obbies trend" }, demand = 8, competition = 6, monetization = 7, feasibility = 9, lessons = 5 }),
                ("propose_concept", new { pitch = "A candy tycoon", genre = "tycoon", template = "tycoon", core_loop = "Buy droppers, collect cash, expand", demand = 6, competition = 4, monetization = 7, feasibility = 6 }),
                ("propose_concept", new { pitch = "A gem simulator", genre = "simulator", template = "simulator", core_loop = "Collect gems, sell, upgrade", demand = 6, competition = 5, monetization = 6, feasibility = 7 }),
                ("choose_concept", new { concept_id = "c1" }),
                ("write_brief", new
                {
                    title = "Lava Forest Obby",
                    genre = "obby",
                    template = "obby",
                    pitch = "Leap over glowing lava through a pine forest.",
                    target_players = "8-14 year olds who like quick challenges",
                    core_loop = "Jump from platform to platform over lava, touching each checkpoint to save progress.",
                    must_have = new[] { "Three stages with checkpoints", "A lava floor that resets players", "A finish with a reward" },
                    asset_needs = new[] { "pine tree" },
                    monetization = new object[]
                    {
                        new { key = "skip", kind = "DeveloperProduct", name = "Skip Stage", description = "Skips the stage you are on", price = 25, grant = "skipstage" },
                        new { key = "vip", kind = "GamePass", name = "VIP", description = "Earn double coins forever", price = 149, grant = "multiplier:2" },
                    },
                    marketing = new { hook = "Don't touch the lava!", title_ideas = new[] { "Lava Forest Obby" }, keywords = new[] { "obby", "lava" } },
                    done_when = new[] { "Every stage is reachable", "The finish pays out" },
                }));
        }

        /// <summary>The start platform of a generated obby (top at y 14, 38 studs square round x 0, z 120): a free corner for a hand-placed tree.</summary>
        internal static readonly int[] StartCorner = { -14, 14, 134 };

        internal static void ScriptBuild(ScriptedLlm llm)
        {
            llm.Script("builder",
                ("set_milestones", new { milestones = new[] { "Core loop playable", "Every must-have", "Polish" } }),
                ("update_manifest", new { title = "Lava Forest Obby", currency_name = "Coins", tips = new[] { "Touch the green pads to save!" } }),
                ("design_world", new { }),
                ("design_world", new { spec = new { course = new { stages = 16 } } }),
                ("search_assets", new { query = "pine tree" }),
                ("view_asset", new { asset_id = FakeRoblox.AssetId }),
                ("place_asset", new { asset_id = FakeRoblox.AssetId, position = StartCorner, yaw = 30 }),
                ("write_file", new { path = "src/server/Welcome.server.luau", content = "local Players = game:GetService(\"Players\")\nPlayers.PlayerAdded:Connect(function(player)\n\tprint(\"Welcome\", player.Name)\nend)\n" }),
                ("write_file", new { path = "src/tests/Stages.luau", content = "return { [\"has stages\"] = function(t) t.ok(true) end }\n" }),
                ("build", new { }),
                ("run_tests", new { }),
                ("update_milestone", new { id = "m1", done = true }),
                ("update_milestone", new { id = "m2", done = true }),
                ("update_milestone", new { id = "m3", done = true }),
                ("mark_feature_complete", new { notes = "sixteen stages over lava, checkpoints, a finish" }));
        }

        internal static void ScriptReview(ScriptedLlm llm, bool pass = true) =>
            llm.Script("tester",
                ("render_scene", new { view = "spawn" }),
                ("verdict", pass
                    ? new { pass, summary = "Clear path, fair jumps, lava is obvious.", scores = new { first_impression = 8, world_beauty = 8, world_fullness = 7, clear_goal = 9, fun_loop = 7, progression = 7 } }
                    : (object)new { pass, summary = "Stage 2 is too far away." }));

        internal static void ScriptMarketing(ScriptedLlm llm, bool featureAsset = true) =>
            llm.Script("marketer",
                ("set_store_page", new { title = "Lava Forest Obby", description = "Don't touch the lava! Leap across glowing platforms through a pine forest, save at every checkpoint and race to the golden finish. New stages are coming soon.", genre = "Obby", keywords = new[] { "obby", "lava", "parkour" } }),
                ("make_icon", new { headline = "Lava Obby", view = "iso" }),
                ("make_thumbnail", new { headline = "Lava Forest Obby", subtitle = "Can you reach the finish?", view = "iso", featured_asset_ids = featureAsset ? new[] { FakeRoblox.AssetId.ToString() } : Array.Empty<string>() }),
                ("compliance_check", new { }),
                ("request_launch", new { }));

        [Fact]
        public async Task TheStudioTakesAGameFromIdeaToLiveAndLearnsFromIt()
        {
            using var h = new StudioHarness();

            // The harvester fills the library first (it runs beside the pipeline).
            var harvest = await h.Harvester.RunOnceAsync(CancellationToken.None);
            Assert.True(harvest.Discovered >= 1, harvest.ToString());
            var asset = h.Ctx.Library.Get(FakeRoblox.AssetId)!;
            Assert.Equal(AssetStatus.Labeled, asset.Status);
            Assert.Equal("low-poly pine tree", asset.Label!.What);
            Assert.Equal(2, asset.ScriptsRemoved);
            Assert.Contains(asset.ScriptFlags, f => f.Contains("backdoor"));

            // Research.
            ScriptResearch(h.Llm);
            await h.StepUntilAsync(() => h.Ctx.Store.Read(s => s.Games.Count) == 1 && h.OnlyGame().Stage == GameStage.Build);
            var game = h.OnlyGame();
            Assert.Equal("Lava Forest Obby", game.Brief!.Title);
            Assert.Equal(3, game.Concepts.Count);
            Assert.True(game.Concepts.Single(c => c.Chosen).Scores.Total >= 5);
            Assert.Equal(2, game.Products.Count);
            Assert.Contains("## Exit criteria for this stage", h.Llm.Briefings[0]); // every cycle starts from the dossier briefing
            Assert.Contains(h.Llm.ToolResults, r => r.StartsWith("researcher.roblox_charts") && r.Contains("Lava Rush Obby"));

            // Build.
            ScriptBuild(h.Llm);
            await h.StepUntilAsync(() => h.OnlyGame().Stage == GameStage.Test);
            game = h.OnlyGame();
            Assert.True(game.Build.LastBuildOk, string.Join("\n", game.Build.Errors));
            Assert.True(game.Build.FeatureComplete);
            Assert.Contains(FakeRoblox.AssetId, game.Build.AssetsUsed);
            Assert.Contains(h.Llm.ToolResults, r => r.StartsWith("builder.design_world") && r.Contains("\"Layout\": \"course\""));
            Assert.Contains(h.Llm.ToolResults, r => r.StartsWith("builder.design_world") && r.Contains("Regenerated"));
            Assert.Contains(h.Llm.ToolResults, r => r.StartsWith("builder.place_asset") && r.Contains("footprint"));
            Assert.Contains(h.Llm.ToolResults, r => r.StartsWith("builder.build") && r.Contains("BUILD OK"));
            Assert.True(game.Build.MapScore >= StageGates.MinMapScore, "map " + game.Build.MapScore + ": " + string.Join(" | ", game.Build.MapFindings));
            var scene = h.Ctx.Workspace(game.GameId).LoadScene();
            Assert.Equal(16, scene.Objects.Count(o => o.Tags.Contains("Checkpoint")));
            Assert.Contains(scene.Objects, o => o.Tags.Contains("Finish"));
            Assert.Contains(scene.Objects, o => o.AssetId == FakeRoblox.AssetId && o.Source != "gen"); // the hand-placed tree survived the regeneration

            // Test: clean build, engine specs in "Roblox", and the playtest review.
            ScriptReview(h.Llm);
            await h.StepUntilAsync(() => h.OnlyGame().Stage == GameStage.Launch);
            game = h.OnlyGame();
            Assert.True(game.LastTest!.Passed, game.LastTest.Summary + string.Join("\n", game.LastTest.Issues.Select(i => i.Message)));
            Assert.True(game.LastTest.EngineRan);
            Assert.Contains(h.Roblox.Requests, r => r.Url.Contains("versionType=Saved"));
            Assert.Contains(h.Roblox.Requests, r => r.Url.Contains("luau-execution-session-tasks"));

            // Launch: marketing, then the publish sequence.
            ScriptMarketing(h.Llm);
            await h.StepUntilAsync(() => h.OnlyGame().Stage == GameStage.Live);
            game = h.OnlyGame();
            Assert.True(game.Publish.IsPublic);
            Assert.All(game.Products, p => Assert.NotNull(p.RobloxId));
            Assert.Equal("icon.png", game.Store.IconFile);
            Assert.True(File.Exists(Path.Combine(h.Ctx.Store.MediaDirectory(game.GameId), "thumb1.png")));
            Assert.Contains(h.Roblox.Requests, r => r.Url.Contains("versionType=Published"));
            Assert.Contains(h.Roblox.Requests, r => r.Method == "PATCH" && r.Url.Contains("/cloud/v2/universes/1001?") && r.Body.Contains("PUBLIC"));
            Assert.Contains(h.Roblox.Requests, r => r.Method == "PATCH" && r.Url.Contains("/places/2002?updateMask=displayName,description") && r.Body.Contains("Lava Forest Obby"));
            Assert.Contains(h.Roblox.Requests, r => r.Method == "POST" && r.Url.Contains("/game-passes/v1/universes/1001/game-passes"));
            Assert.Contains(h.Roblox.Requests, r => r.Method == "POST" && r.Url.Contains("/developer-products/v2/universes/1001/developer-products"));
            // No Roblox session yet: the icon and questionnaire wait for the owner, who is told how to hand them over.
            var tasks = h.Ctx.Store.Read(s => s.Studio.OwnerTasks.ToList());
            Assert.Contains(tasks, t => t.Kind == OwnerTaskKind.UploadIcon && t.Files.Contains("icon.png") && t.Detail.Contains("OmniBlox_RobloxSessionCookie"));
            Assert.Contains(tasks, t => t.Kind == OwnerTaskKind.Questionnaire);
            Assert.Contains(h.Ctx.Store.Read(s => s.Events.ToList()), e => e.Kind == "game.launched");
            // The owner sets the session once: housekeeping uploads the icon, answers the questionnaire and closes both tasks.
            h.Ctx.Settings.SessionCookie = "cookie-1";
            await StorePageChores.SweepAsync(h.Ctx, CancellationToken.None);
            Assert.DoesNotContain(h.Ctx.Store.Read(s => s.Studio.OwnerTasks.Where(t => t.Status == OwnerTaskStatus.Open).ToList()), t => t.Kind is OwnerTaskKind.UploadIcon or OwnerTaskKind.Questionnaire);
            Assert.True(h.OnlyGame().Publish.Questionnaire!.Published);
            Assert.Contains(h.Roblox.Requests, r => r.Url == "https://publish.roblox.com/v1/games/1001/icon");
            // The final place carries the real product ids.
            string config = File.ReadAllText(Path.Combine(h.Ctx.Store.BuildDirectory(game.GameId), "place.rbxlx"));
            Assert.Contains($"Id = {game.Products.First().RobloxId}", config);

            // Players arrive; telemetry flows in.
            int kept = h.Ctx.Telemetry.Ingest(game.GameId, JObject.Parse(@"{""server"":""s1"",""events"":[
                {""n"":""session_start"",""s"":""a""},{""n"":""funnel"",""s"":""a"",""f"":{""step"":1}},{""n"":""funnel"",""s"":""a"",""f"":{""step"":2}},
                {""n"":""death"",""s"":""a""},{""n"":""session_end"",""s"":""a"",""f"":{""seconds"":300}},
                {""n"":""session_start"",""s"":""b""},{""n"":""funnel"",""s"":""b"",""f"":{""step"":1}},{""n"":""feedback"",""s"":""b"",""f"":{""rating"":3,""reason"":""fun""}},
                {""n"":""session_end"",""s"":""b"",""f"":{""seconds"":120}}]}"), h.Now);
            Assert.Equal(9, kept);

            // Maintenance two days later.
            h.Now = h.Now.AddDays(2).AddMinutes(1);
            h.Llm.Script("maintainer",
                ("game_metrics", new { days = 7 }),
                ("telemetry", new { days = 7 }),
                ("add_lesson", new { topic = "mechanic", text = "Players who reach stage 2 tend to stay for five minutes.", evidence = "telemetry: 2 sessions, 300 s and 120 s", outcome = "worked", tags = new[] { "obby" } }));
            await h.StepUntilAsync(() => h.OnlyGame().Maintenance.Count == 1);
            game = h.OnlyGame();
            Assert.Contains(h.Llm.ToolResults, r => r.StartsWith("maintainer.telemetry") && r.Contains("2 sessions") && r.Contains("Funnel"));
            Assert.Contains(h.Llm.ToolResults, r => r.StartsWith("maintainer.game_metrics") && r.Contains("DAU"));
            Assert.NotNull(game.NextMaintenanceUtc);
            Assert.Contains(h.Ctx.Store.Read(s => s.Playbook.ToList()), l => l.Text.Contains("stage 2"));

            // Day-7 post-mortem feeds the Playbook.
            h.Now = game.Publish.WentPublicUtc!.Value.AddDays(7).AddMinutes(1);
            h.Ctx.Update(game.GameId, g => g.NextMaintenanceUtc = h.Now.AddDays(5)); // keep maintenance out of the way
            await h.StepUntilAsync(() => h.OnlyGame().PostMortems.Count == 1);
            Assert.Equal(2, h.OnlyGame().PostMortems[0].Lessons.Count);
            Assert.Contains(h.Ctx.Store.Read(s => s.Playbook.ToList()), l => l.Topic == "pricing" && l.Outcome == "failed");
            Assert.True(h.OnlyGame().Usage.Calls > 10);
        }

        [Fact]
        public async Task LaunchWaitsForTheOwnersSignoff_ThenForTheOwnersApproval_BeforeAnythingHappensOnRoblox()
        {
            using var h = new StudioHarness(requireApproval: true);
            await h.Harvester.RunOnceAsync(CancellationToken.None);
            ScriptResearch(h.Llm);
            ScriptBuild(h.Llm);
            ScriptReview(h.Llm);
            ScriptMarketing(h.Llm);
            List<OwnerTask> Tasks() => h.Ctx.Store.Read(s => s.Studio.OwnerTasks.ToList());
            bool NothingOnRoblox() => !h.Roblox.Requests.Any(r => r.Url.Contains("versionType=Published") || r.Method != "GET" && (r.Url.Contains("/game-passes/") || r.Url.Contains("/developer-products/") || r.Url.Contains("/cloud/v2/universes/1001?")));

            // Gate 1: the tested build waits for the owner to play it, before any marketing work.
            await h.StepUntilAsync(() => h.Ctx.Store.Read(s => s.Games.Count) == 1 && h.OnlyGame().WaitingReason?.Contains("sign it off") == true, 40);
            var game = h.OnlyGame();
            Assert.Equal(GameStage.Launch, game.Stage);
            Assert.Contains(Tasks(), t => t.Kind == OwnerTaskKind.BuildSignoff && t.Status == OwnerTaskStatus.Open && t.Title.Contains($"r{game.Build.Revision}"));
            Assert.DoesNotContain(h.Llm.ToolResults, r => r.StartsWith("marketer."));
            for (int i = 0; i < 3; i++) await h.Orchestrator.StepAsync(CancellationToken.None);
            Assert.DoesNotContain(h.Llm.ToolResults, r => r.StartsWith("marketer.")); // it really waits
            Assert.Throws<KeyNotFoundException>(() => Approvals.Approve(h.Ctx, "nope", Approvals.BuildGate));
            Approvals.Approve(h.Ctx, game.GameId, Approvals.BuildGate, "Plays well; the lava is scary in a good way.");
            Assert.DoesNotContain(Tasks(), t => t.Kind == OwnerTaskKind.BuildSignoff && t.Status == OwnerTaskStatus.Open);

            // Gate 2: marketing, the final build and engine run, then the launch approval — nothing on Roblox yet.
            await h.StepUntilAsync(() => h.OnlyGame().WaitingReason?.Contains("launch approval") == true, 40);
            game = h.OnlyGame();
            Assert.Equal(GameStage.Launch, game.Stage);
            Assert.Contains(h.Llm.ToolResults, r => r.StartsWith("marketer.request_launch"));
            Assert.True(NothingOnRoblox(), "something happened on Roblox before the launch approval");
            var approval = Assert.Single(Tasks(), t => t.Kind == OwnerTaskKind.LaunchApproval && t.Status == OwnerTaskStatus.Open);
            Assert.Contains("Skip Stage", approval.Detail);
            Assert.Contains("R$ 25", approval.Detail);
            for (int i = 0; i < 3; i++) await h.Orchestrator.StepAsync(CancellationToken.None);
            Assert.True(NothingOnRoblox());

            // Approved: products, store page, publish, public.
            Approvals.Approve(h.Ctx, game.GameId, Approvals.LaunchGate);
            await h.StepUntilAsync(() => h.OnlyGame().Stage == GameStage.Live);
            game = h.OnlyGame();
            Assert.Contains(h.Roblox.Requests, r => r.Url.Contains("versionType=Published"));
            Assert.All(game.Products, p => Assert.NotNull(p.RobloxId));
            Assert.DoesNotContain(Tasks(), t => t.Kind == OwnerTaskKind.LaunchApproval && t.Status == OwnerTaskStatus.Open);
            Assert.Equal(new[] { "build:approved", "launch:approved" }, game.Publish.Approvals.Select(a => a.Gate + ":" + a.Decision));
            Assert.Equal(game.Build.Revision, game.Publish.ApprovedRevision);
        }

        [Fact]
        public async Task AnApprovalIsForWhatTheOwnerSaw_AChangedPriceNeedsApprovingAgain()
        {
            using var h = new StudioHarness(requireApproval: true);
            await h.Harvester.RunOnceAsync(CancellationToken.None);
            ScriptResearch(h.Llm);
            ScriptBuild(h.Llm);
            ScriptReview(h.Llm);
            ScriptMarketing(h.Llm);
            await h.StepUntilAsync(() => h.Ctx.Store.Read(s => s.Games.Count) == 1 && h.OnlyGame().WaitingReason?.Contains("sign it off") == true, 40);
            string id = h.OnlyGame().GameId;
            Approvals.Approve(h.Ctx, id, Approvals.BuildGate);
            await h.StepUntilAsync(() => h.OnlyGame().WaitingReason?.Contains("launch approval") == true, 40);
            Approvals.Approve(h.Ctx, id, Approvals.LaunchGate);
            Assert.True(Approvals.LaunchApproved(h.Ctx, h.Game(id)));

            // A price changes after the approval: the approval no longer covers the launch.
            h.Ctx.Update(id, g => g.Products[0].PriceRobux = 49);
            Assert.False(Approvals.LaunchApproved(h.Ctx, h.Game(id)));
            await h.StepUntilAsync(() => h.OnlyGame().WaitingReason?.Contains("launch approval") == true, 10);
            Assert.DoesNotContain(h.Roblox.Requests, r => r.Url.Contains("versionType=Published"));
            Assert.Contains(h.Ctx.Store.Read(s => s.Studio.OwnerTasks.ToList()), t => t.Kind == OwnerTaskKind.LaunchApproval && t.Status == OwnerTaskStatus.Open && t.Detail.Contains("R$ 49"));
        }

        [Fact]
        public async Task ChangesAskedAtTheSignoff_GoToTheBuilder_AndTheGameGoesBackToBuild()
        {
            using var h = new StudioHarness(requireApproval: true);
            await h.Harvester.RunOnceAsync(CancellationToken.None);
            ScriptResearch(h.Llm);
            ScriptBuild(h.Llm);
            ScriptReview(h.Llm);
            await h.StepUntilAsync(() => h.Ctx.Store.Read(s => s.Games.Count) == 1 && h.OnlyGame().WaitingReason?.Contains("sign it off") == true, 40);
            var game = h.OnlyGame();
            Assert.Throws<ArgumentException>(() => Approvals.RequestChanges(h.Ctx, h.Orchestrator, game.GameId, Approvals.BuildGate, " ", backToBuild: false));
            Approvals.RequestChanges(h.Ctx, h.Orchestrator, game.GameId, Approvals.BuildGate, "Stage 7 is too hard for a first visit; make the jumps shorter.", backToBuild: false);

            game = h.OnlyGame();
            Assert.Equal(GameStage.Build, game.Stage);
            Assert.False(game.Build.FeatureComplete);
            Assert.Null(game.WaitingReason);
            Assert.Contains(game.Directives, d => d.Text.Contains("Stage 7 is too hard"));
            Assert.DoesNotContain(h.Ctx.Store.Read(s => s.Studio.OwnerTasks.ToList()), t => t.Kind == OwnerTaskKind.BuildSignoff && t.Status == OwnerTaskStatus.Open);
            Assert.Equal("changes", Assert.Single(game.Publish.Approvals).Decision);

            // The builder's next cycle is told.
            await h.Orchestrator.StepAsync(CancellationToken.None);
            Assert.Contains(h.Llm.Briefings, b => b.Contains("Stage 7 is too hard"));
        }

        [Fact]
        public async Task WithoutAKey_TheStudioBuildsOfflineAndAsksTheOwner()
        {
            using var h = new StudioHarness(apiKey: false);
            // No key: models are discovered and previewed but not downloaded, so place nothing from the library.
            var harvest = await h.Harvester.RunOnceAsync(CancellationToken.None);
            Assert.Contains(harvest.Notes, n => n.Contains("no API key"));
            Assert.Equal(AssetStatus.Previewed, h.Ctx.Library.Get(FakeRoblox.AssetId)!.Status);

            ScriptResearch(h.Llm);
            h.Llm.Script("builder",
                ("design_world", new { }),
                ("place_asset", new { asset_id = FakeRoblox.AssetId, position = StartCorner }),
                ("build", new { }),
                ("update_milestone", new { id = "m1", done = true }),
                ("update_milestone", new { id = "m2", done = true }),
                ("update_milestone", new { id = "m3", done = true }),
                ("update_milestone", new { id = "m4", done = true }),
                ("update_milestone", new { id = "m5", done = true }),
                ("mark_feature_complete", new { notes = "core loop done" }));
            ScriptReview(h.Llm);
            ScriptMarketing(h.Llm, featureAsset: false);
            await h.StepUntilAsync(() => h.Ctx.Store.Read(s => s.Games.Count) == 1 && h.OnlyGame().WaitingReason != null, 40);
            var game = h.OnlyGame();
            Assert.Equal(GameStage.Launch, game.Stage);
            Assert.Contains("API key", game.WaitingReason);
            Assert.Contains(h.Llm.ToolResults, r => r.StartsWith("builder.place_asset") && r.Contains("cannot be placed yet"));
            Assert.Contains(game.LastTest!.Issues, i => i.Message.Contains("engine tests wait for the Roblox API key"));
            Assert.Contains(h.Ctx.Store.Read(s => s.Studio.OwnerTasks.ToList()), t => t.Kind == OwnerTaskKind.ApiKey && t.Status == OwnerTaskStatus.Open);
            Assert.DoesNotContain(h.Roblox.Requests, r => r.Url.Contains("apis.roblox.com/universes"));
        }

        [Fact]
        public async Task AFailedPlaytestSendsTheGameBackToBuild()
        {
            using var h = new StudioHarness();
            await h.Harvester.RunOnceAsync(CancellationToken.None);
            ScriptResearch(h.Llm);
            ScriptBuild(h.Llm);
            ScriptReview(h.Llm, pass: false);
            await h.StepUntilAsync(() => h.Ctx.Store.Read(s => s.Games.Count) == 1 && h.OnlyGame().LastTest != null);
            var game = h.OnlyGame();
            Assert.Equal(GameStage.Build, game.Stage);
            Assert.False(game.LastTest!.Passed);
            Assert.False(game.Build.FeatureComplete);
            Assert.Contains(game.LastTest.Issues, i => i.Message.Contains("too far"));
            Assert.Equal(1, game.StageFailures[GameStage.Test]);
        }

        [Fact]
        public async Task NothingRunsUnlessAIRouterIsTheProvider()
        {
            using var h = new StudioHarness();
            h.Llm.Available = false;
            bool worked = await h.Orchestrator.StepAsync(CancellationToken.None);
            Assert.False(worked);
            Assert.Contains("AIRouter", h.Orchestrator.Status);
            Assert.Equal(0, h.Ctx.Store.Read(s => s.Games.Count));
        }

        [Fact]
        public async Task AStuckGame_ClimbsTheWatchdogLadder_ThenPausesAndAsksTheOwner_AndIsNeverAbandoned()
        {
            using var h = new StudioHarness();
            // A researcher that never writes a brief (and changes nothing the watchdog counts as progress).
            for (int i = 0; i < 14; i++) h.Llm.Script("researcher", new { summary = "still thinking", stage_complete = false });
            await h.StepUntilAsync(() => h.Ctx.Store.Read(s => s.Games.Count) == 1 && h.OnlyGame().Paused, 20);
            var game = h.OnlyGame();
            Assert.Equal(GameStage.Research, game.Stage);
            Assert.Equal(3, game.Progress.Level);
            Assert.Equal(1, game.Escalations);
            Assert.Contains(game.Progress.History, line => line.Contains("level 1"));
            Assert.Contains(game.Progress.History, line => line.Contains("level 2"));
            Assert.Contains(h.Ctx.Store.Read(s => s.Studio.OwnerTasks.ToList()), t => t.Kind == OwnerTaskKind.Escalation && t.DedupeKey == "stuck:" + game.GameId);

            // Paused, it waits however long it takes: nothing runs and nothing gives up on it.
            int cycles = game.Cycles.Count;
            for (int i = 0; i < 5; i++) await h.Orchestrator.StepAsync(CancellationToken.None);
            Assert.Equal(cycles, h.Game(game.GameId).Cycles.Count);
            Assert.NotEqual(GameStage.Abandoned, h.Game(game.GameId).Stage);

            // The owner resumes it: the ladder starts over and the task is resolved.
            Services.OmniBlox.Pipeline.Orchestrator.Resume(h.Ctx, game.GameId);
            var resumed = h.Game(game.GameId);
            Assert.False(resumed.Paused);
            Assert.Equal(0, resumed.Progress.Level);
            Assert.DoesNotContain(h.Ctx.Store.Read(s => s.Studio.OwnerTasks.ToList()), t => t.DedupeKey == "stuck:" + game.GameId && t.Status == OwnerTaskStatus.Open);
        }
    }
}
