using System.IO.Compression;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox;
using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Pipeline;
using Omnipotent.Services.OmniBlox.Pipeline.Tools;
using Xunit.Abstractions;

namespace Omnipotent.Tests.OmniBlox
{
    /// <summary>
    /// The local engine (Pipeline/LocalEngine.cs): the Game Kit's own engine test — the kit's checks, every module
    /// loading, the server booting and running, then the playthroughs (a stand-in player harvests, sells, buys a
    /// tool and unlocks every zone; buys out a tycoon plot; reaches every obby stage) — run outside Roblox in Lune,
    /// and the studio standing it in for Roblox's engine while there is no API key or experience slot. The Lune
    /// tests opt in with OMNIBLOX_LUNE (lune's executable); with OMNIBLOX_LIVE_DIR their worlds are made of the live
    /// studio's models, else of <see cref="TestModels"/>. The rest run on <see cref="FakeLocalEngine"/>.
    /// </summary>
    public class LocalEngineTests
    {
        private readonly ITestOutputHelper output;
        public LocalEngineTests(ITestOutputHelper output) => this.output = output;

        private static string? Lune() => Environment.GetEnvironmentVariable("OMNIBLOX_LUNE") is { Length: > 0 } lune && File.Exists(lune) ? lune : null;

        /// <summary>A game in Build on its generated baseline (a simulator unless another brief is given).</summary>
        private static string GameInBuild(StudioHarness h, GameBrief? brief = null)
        {
            TestModels.Seed(h.Ctx.Library);
            string id = h.Orchestrator.StartGame();
            h.Ctx.Update(id, g => g.Brief = brief ?? (GameBrief)LiveMapTests.Briefs().First()[1]);
            h.Orchestrator.Enter(id, GameStage.Build);
            return id;
        }

        private static Criterion? EngineCriterion(StudioHarness h, string id) =>
            StageGates.Checklist(h.Game(id), h.Ctx).FirstOrDefault(c => c.Text.Contains("engine", StringComparison.Ordinal));

        private void Print(EngineRun run, string log = "")
        {
            output.WriteLine(run.Summary);
            foreach (var c in run.Results) output.WriteLine($"  {(c.Ok ? "ok  " : "FAIL")} [{c.Phase}/{c.Owner}] {c.Name}{(c.Detail is { Length: > 0 } d ? " — " + d : "")}");
            foreach (var e in run.Errors) output.WriteLine($"  ERROR [{e.Phase}] {e.Message}{(e.File != null ? $" ({e.File}:{e.Line})" : "")}");
            if (log.Length > 0) output.WriteLine(log);
        }

        // ─────────────── in Lune ───────────────

        /// <summary>The studio's own install, from the real release (off unless OMNIBLOX_LIVE=1): the pinned zip downloads, matches its hash and runs.</summary>
        [Fact]
        public async Task ThePinnedLune_Downloads_MatchesItsHash_AndRuns()
        {
            if (Environment.GetEnvironmentVariable("OMNIBLOX_LIVE") != "1") return;
            using var tools = new TempDir();
            var lune = new LuneToolchain(tools.Path);
            Assert.True(await lune.EnsureInstalledAsync(CancellationToken.None), lune.Problem);
            Assert.Null(lune.Problem);
            var version = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(lune.ExecutablePath, "--version") { RedirectStandardOutput = true, UseShellExecute = false })!;
            string printed = await version.StandardOutput.ReadToEndAsync();
            await version.WaitForExitAsync();
            Assert.Equal("lune " + LuneToolchain.Version, printed.Trim());
        }

        [Theory]
        [MemberData(nameof(LiveMapTests.Briefs), MemberType = typeof(LiveMapTests))]
        public async Task TheBaseline_BootsAndPlaysThrough_InTheKitsOwnEngineTest(string name, GameBrief brief)
        {
            if (Lune() is not { } lune) return;
            using var h = new StudioHarness(apiKey: false);
            string? live = Environment.GetEnvironmentVariable("OMNIBLOX_LIVE_DIR");
            if (!string.IsNullOrEmpty(live) && Directory.Exists(live)) LiveLibrary.ImportInto(h.Ctx.Library, live);
            else TestModels.Seed(h.Ctx.Library);
            string id = h.Orchestrator.StartGame();
            h.Ctx.Update(id, g => g.Brief = brief);
            h.Orchestrator.Enter(id, GameStage.Build);

            var compiled = h.Ctx.Compile(id, engineTest: true);
            Assert.True(compiled.Ok, string.Join("\n", compiled.Errors));
            if (Environment.GetEnvironmentVariable("OMNIBLOX_RENDER_OUT") is { Length: > 0 } keep)
                File.WriteAllBytes(Path.Combine(keep, name + "-enginetest.rbxlx"), compiled.PlaceXml);
            var (run, log) = await LocalEngine.RunAsync(new LuneEngine(LuneToolchain.At(lune)), compiled, LocalEngine.BootSeconds, CancellationToken.None);

            Assert.True(run != null, log);
            output.WriteLine(name + ":");
            Print(run!, log);
            Assert.True(run!.Local);
            Assert.True(run.Results.Count > 5, "the runner reported almost nothing: " + run.Summary);
            Assert.True(run.Ok, run.Summary + "\n" + string.Join("\n", run.Results.Where(c => !c.Ok).Select(c => $"{c.Name}: {c.Detail}")) + "\n" + string.Join("\n", run.Errors.Select(e => e.Message)));
        }

        /// <summary>The local engine is no rubber stamp: a system that crashes at start and a failing spec are caught and located.</summary>
        [Fact]
        public async Task ABrokenSystemAndAFailingSpec_AreCaught_WithTheirFileAndLine()
        {
            if (Lune() is not { } lune) return;
            using var h = new StudioHarness(apiKey: false);
            string id = GameInBuild(h);
            var ws = h.Ctx.Workspace(id);
            ws.WriteFile("src/server/systems/Broken.luau", "local Broken = {}\nfunction Broken.Start()\n\tlocal x = nil :: any\n\tprint(x.Health)\nend\nreturn Broken\n");
            ws.WriteFile("src/tests/Bad.luau", "return {\n\t[\"two is one\"] = function(t)\n\t\tt.eq(1, 2, \"one\")\n\tend,\n}\n");

            var compiled = h.Ctx.Compile(id, engineTest: true);
            Assert.True(compiled.Ok, string.Join("\n", compiled.Errors));
            var (run, log) = await LocalEngine.RunAsync(new LuneEngine(LuneToolchain.At(lune)), compiled, bootSeconds: 3, CancellationToken.None);
            Assert.True(run != null, log);
            Print(run!);

            Assert.False(run!.Ok);
            Assert.Contains(run.Results, c => !c.Ok && c.Name.Contains("two is one") && (c.Detail ?? "").Contains("expected 2"));
            Assert.Contains(run.Results, c => !c.Ok && c.Phase == "boot");
            Assert.Contains(run.Errors, e => e.Message.Contains("Health") && e.File == "src/server/systems/Broken.luau" && e.Line == 4);
            // The kit's own checks and its playthroughs still pass: the failures are the game's, not the studio's.
            Assert.DoesNotContain(run.Results, c => !c.Ok && c.Owner == "kit");
        }

        /// <summary>Code that never yields hangs the engine: the local engine stops it at its time limit and fails the run, as Roblox fails a task that times out.</summary>
        [Fact]
        public async Task AGameThatNeverYields_IsStoppedAtTheTimeLimit_AndFails()
        {
            if (Lune() is not { } lune) return;
            using var h = new StudioHarness(apiKey: false);
            string id = GameInBuild(h);
            h.Ctx.Workspace(id).WriteFile("src/server/systems/Spinner.luau", "local Spinner = {}\nfunction Spinner.Start()\n\tlocal turns = 0\n\twhile true do\n\t\tturns += 1\n\tend\nend\nreturn Spinner\n");
            var compiled = h.Ctx.Compile(id, engineTest: true);
            Assert.True(compiled.Ok, string.Join("\n", compiled.Errors));

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var (run, log) = await LocalEngine.RunAsync(new LuneEngine(LuneToolchain.At(lune), TimeSpan.FromSeconds(20)), compiled, bootSeconds: 3, CancellationToken.None);
            Assert.True(run != null, log);
            Print(run!, log);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "the run was not stopped: " + clock.Elapsed);
            Assert.False(run!.Ok);
            Assert.Contains("did not finish in 20 s", run.Summary);
            Assert.Contains(run.Results, c => c.Name == "the test run completes" && !c.Ok && (c.Detail ?? "").Contains("never yields"));
        }

        /// <summary>A game's own system using server services Lune has nothing for (collision groups, badges, filtering, paths, messaging) runs on their stand-ins, so a correct game is not failed for them.</summary>
        [Fact]
        public async Task CommonServerServices_HaveStandIns_SoACorrectGameIsNotFailedForThem()
        {
            if (Lune() is not { } lune) return;
            using var h = new StudioHarness(apiKey: false);
            string id = GameInBuild(h);
            var ws = h.Ctx.Workspace(id);
            ws.WriteFile("src/server/systems/Extras.luau", @"local BadgeService = game:GetService(""BadgeService"")
local MessagingService = game:GetService(""MessagingService"")
local PathfindingService = game:GetService(""PathfindingService"")
local PhysicsService = game:GetService(""PhysicsService"")
local TextService = game:GetService(""TextService"")

local Extras = {}

function Extras.Start()
	PhysicsService:RegisterCollisionGroup(""Pets"")
	PhysicsService:CollisionGroupSetCollidable(""Pets"", ""Pets"", false)
	if not PhysicsService:CollisionGroupsAreCollidable(""Pets"", ""Pets"") then
		print(""pets do not collide"")
	end
	MessagingService:SubscribeAsync(""Shout"", function() end)
	local path = PathfindingService:CreatePath({ AgentRadius = 2 })
	path:ComputeAsync(Vector3.new(0, 0, 0), Vector3.new(20, 0, 20))
	if path.Status ~= Enum.PathStatus.Success then
		print(""no path in a test run:"", #path:GetWaypoints())
	end
end

function Extras.Name(text: string, userId: number): string
	return TextService:FilterStringAsync(text, userId):GetNonChatStringForBroadcastAsync()
end

function Extras.Reward(userId: number): boolean
	return BadgeService:AwardBadge(userId, 2124567890)
end

return Extras
");
            ws.WriteFile("src/tests/Extras.luau", @"local Extras = require(game:GetService(""ServerScriptService""):WaitForChild(""Systems""):WaitForChild(""Extras""))

return {
	[""names come back filtered and badges are awarded""] = function(t)
		local p = t.player()
		t.eq(Extras.Name(""Sparky"", p.UserId), ""Sparky"", ""filtered name"")
		t.ok(Extras.Reward(p.UserId), ""badge awarded"")
	end,
}
");
            var compiled = h.Ctx.Compile(id, engineTest: true);
            Assert.True(compiled.Ok, string.Join("\n", compiled.Errors));
            var (run, log) = await LocalEngine.RunAsync(new LuneEngine(LuneToolchain.At(lune)), compiled, bootSeconds: 3, CancellationToken.None);
            Assert.True(run != null, log);
            Print(run!, log);
            Assert.True(run!.Ok, run.Summary + "\n" + string.Join("\n", run.Results.Where(c => !c.Ok).Select(c => $"{c.Name}: {c.Detail}")) + "\n" + string.Join("\n", run.Errors.Select(e => e.Message)));
            Assert.Contains(run.Results, c => c.Ok && c.Name.Contains("names come back filtered and badges are awarded"));
            Assert.Contains("pets do not collide", log);
            Assert.Contains("no path in a test run", log);
        }

        /// <summary>
        /// The feature end to end: a game built without an API key gets the kit's boot and playthrough check from the
        /// local engine through the builder's own run_tests, recorded in the dossier, shown on the Tests tab, and
        /// enough to leave Build — but not to launch.
        /// </summary>
        [Fact]
        public async Task AGameBuiltWithoutAKey_GetsTheKitsBootAndPlaythrough_FromTheLocalEngine()
        {
            if (Lune() is not { } lune) return;
            using var h = new StudioHarness(apiKey: false, localEngine: new LuneEngine(LuneToolchain.At(lune)));
            string id = GameInBuild(h);
            Assert.False(EngineCriterion(h, id)!.Met);

            string text = (await WorldToolTests.Call(BuildTools.Create(h.Ctx, id), "run_tests", new { })).Text;
            output.WriteLine(text);

            var game = h.Game(id);
            var run = game.Build.LastEngineRun!;
            Assert.True(run.Local);
            Assert.True(run.Ok, text);
            Assert.Equal(game.Build.Revision, run.Revision);
            Assert.Contains(run.Results, c => c.Ok && c.Phase == "boot");
            Assert.Contains(run.Results, c => c.Ok && c.Name.Contains("every zone can be unlocked in order and harvested"));
            Assert.Contains("PASSED", text);
            Assert.Contains("local engine", text);
            Assert.True(EngineCriterion(h, id)!.Met);
            Assert.False(StageGates.EnginePassed(game)); // a launch still needs Roblox's engine
            Assert.DoesNotContain(h.Roblox.Requests, r => r.Url.Contains("luau-execution"));

            var detail = JObject.Parse(OmniBloxViews.Serialize(OmniBloxViews.GameDetail(h.Ctx, id)!));
            Assert.True((bool)detail["LocalEngine"]!);
            Assert.True((bool)detail["Game"]!["Build"]!["LastEngineRun"]!["Local"]!);
            Assert.True((bool)detail["Summary"]!["Engine"]!["Local"]!);
        }

        // ─────────────── the studio's use of it ───────────────

        [Fact]
        public async Task WithoutAKey_RunTestsFallsBackToTheLocalEngine_WhichCountsOnlyUntilRobloxCanRunTheGame()
        {
            var engine = new FakeLocalEngine();
            using var h = new StudioHarness(apiKey: false, localEngine: engine);
            string id = GameInBuild(h);
            var criterion = EngineCriterion(h, id)!;
            Assert.Contains("local engine", criterion.Text);
            Assert.False(criterion.Met);

            var (run, message) = await BuildTools.RunEngineTestsAsync(h.Ctx, id, CancellationToken.None);
            Assert.True(run != null, message);
            Assert.True(run!.Local);
            Assert.True(run.Ok, message);
            Assert.StartsWith("Local engine: ", run.Summary);
            var (place, boot) = Assert.Single(engine.Runs);
            Assert.Equal(LocalEngine.BootSeconds, boot);
            Assert.Contains("TestRunner", Encoding.UTF8.GetString(place)); // the engine-test build, the kit inside
            Assert.DoesNotContain(h.Roblox.Requests, r => r.Url.Contains("luau-execution") || r.Url.Contains("versionType"));

            var game = h.Game(id);
            Assert.True(game.Build.LastEngineRun!.Local);
            Assert.Null(game.Publish.EngineTestedRevision);
            Assert.True(StageGates.EnginePassed(game, local: true));
            Assert.False(StageGates.EnginePassed(game));
            Assert.True(EngineCriterion(h, id)!.Met);
            Assert.DoesNotContain(BuildTools.FeatureCompleteProblems(h.Ctx, id), p => p.Contains("run_tests"));
            Assert.Contains("(In the studio's local engine", EngineTests.Describe(run));
            Assert.Contains("passed on this build in the local engine", (await BuildTools.RunBuildAsync(h.Ctx, id, CancellationToken.None)).Report);
            var detail = JObject.Parse(OmniBloxViews.Serialize(OmniBloxViews.GameDetail(h.Ctx, id)!));
            Assert.True((bool)detail["LocalEngine"]!);
            Assert.False((bool)detail["EngineAvailable"]!);
            Assert.True((bool)detail["Summary"]!["Engine"]!["Local"]!);

            // The key arrives: from now on only Roblox's engine counts, and run_tests uses it.
            h.Ctx.Settings.ApiKey = "test-key";
            criterion = EngineCriterion(h, id)!;
            Assert.Contains("Roblox's engine", criterion.Text);
            Assert.False(criterion.Met);
            Assert.Contains(BuildTools.FeatureCompleteProblems(h.Ctx, id), p => p.Contains("has not run in Roblox's engine"));
            Assert.False(JObject.Parse(OmniBloxViews.Serialize(OmniBloxViews.GameDetail(h.Ctx, id)!))["LocalEngine"]!.Value<bool>());

            (run, message) = await BuildTools.RunEngineTestsAsync(h.Ctx, id, CancellationToken.None);
            Assert.False(run!.Local);
            Assert.True(run.Ok, message);
            Assert.Single(engine.Runs);
            Assert.Contains(h.Roblox.Requests, r => r.Url.Contains("luau-execution"));
            Assert.True(EngineCriterion(h, id)!.Met);
            Assert.Equal(h.Game(id).Build.Revision, h.Game(id).Publish.EngineTestedRevision);
        }

        [Fact]
        public async Task AFailingLocalRun_KeepsTheGameInBuild_WithItsFailuresLocatedInTheGamesFiles()
        {
            using var h = new StudioHarness(apiKey: false, localEngine: new FakeLocalEngine { Result = FakeLocalEngine.FailingRun });
            string id = GameInBuild(h, (GameBrief)LiveMapTests.Briefs().Last()[1]);
            h.Ctx.Workspace(id).WriteFile("src/server/Welcome.server.luau", "local Players = game:GetService(\"Players\")\n\nPlayers.PlayerAdded:Connect(function(player)\n\tprint(\"Welcome, \" .. player.Nickname)\nend)\n");

            var (run, _) = await BuildTools.RunEngineTestsAsync(h.Ctx, id, CancellationToken.None);
            Print(run!);
            Assert.True(run!.Local);
            Assert.False(run.Ok);
            Assert.False(TestStage.GameChecksPass(run));
            Assert.Contains(run.Errors, e => e.File == "src/server/Welcome.server.luau" && e.Line == 4);
            Assert.False(EngineCriterion(h, id)!.Met);
            Assert.Contains(BuildTools.FeatureCompleteProblems(h.Ctx, id), p => p.Contains("run_tests failed on this build: Local engine:"));
            string described = EngineTests.Describe(run);
            Assert.Contains("FAIL [game/game] Factory · the first dropper is affordable: starting candy 50 < first dropper 60", described);
            Assert.Contains("src/server/Welcome.server.luau:4", described);
        }

        /// <summary>Lune that cannot be downloaded (offline) or does not match its pinned hash is never run, and a game is never failed for it.</summary>
        [Fact]
        public async Task OfflineMeansNotRunLocally_NeverAFailure()
        {
            using var tools = new TempDir();
            var offline = new LuneToolchain(tools.Path, handler: new Downloads(_ => throw new HttpRequestException("No such host is known. (github.com:443)")));
            using var h = new StudioHarness(apiKey: false, localEngine: new LuneEngine(offline));
            string id = GameInBuild(h);
            Assert.NotNull(EngineCriterion(h, id)); // worth trying until it fails

            var (run, message) = await BuildTools.RunEngineTestsAsync(h.Ctx, id, CancellationToken.None);
            output.WriteLine(message);
            Assert.Null(run);
            Assert.Contains("Roblox API key", message);
            Assert.Contains("could not run it either", message);
            Assert.Null(h.Game(id).Build.LastEngineRun);
            Assert.False(h.Ctx.LocalEngine!.Available);
            Assert.NotNull(h.Ctx.LocalEngine.Problem);
            Assert.Null(EngineCriterion(h, id)); // nothing to pass: the game is judged as before, never blocked
            Assert.DoesNotContain(BuildTools.FeatureCompleteProblems(h.Ctx, id), p => p.Contains("run_tests"));
            Assert.Contains("could not be installed", (await BuildTools.RunEngineTestsAsync(h.Ctx, id, CancellationToken.None)).Message);

            // A download that is not the pinned build is never installed.
            using var other = new TempDir();
            var zip = new MemoryStream();
            using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
            using (var entry = archive.CreateEntry(OperatingSystem.IsWindows() ? "lune.exe" : "lune").Open())
                entry.Write(Encoding.UTF8.GetBytes("not lune"));
            var tampered = new LuneToolchain(other.Path, handler: new Downloads(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip.ToArray()) }));
            Assert.False(await tampered.EnsureInstalledAsync(CancellationToken.None));
            Assert.Contains("SHA-256", tampered.Problem);
            Assert.False(tampered.Installed);
        }

        /// <summary>What Lune printed, read as Roblox's own run would be: a hang fails the game, a crash of the stand-in tells nothing.</summary>
        [Fact]
        public void WhatTheLocalEnginePrinted_IsReadLikeARobloxRun()
        {
            static LocalEngineOutput Printed(string stdout, string stderr = "", bool timedOut = false) => new(stdout, stderr, timedOut ? -1 : 0, timedOut, TimeSpan.FromSeconds(timedOut ? 240 : 14));

            var (passed, log) = LocalEngine.Interpret(Printed("Welcome to the factory\n" + FakeLocalEngine.Line(FakeLocalEngine.GreenRun)), null);
            Assert.True(passed!.Ok);
            Assert.True(passed.Local);
            Assert.Equal("Local engine: 5 checks passed; the server booted with no errors.", passed.Summary);
            Assert.Contains("Welcome to the factory", log);
            Assert.Contains("\"seconds\":14.2", log);

            // A hang: stopped at the time limit, and failed (in Roblox the task would time out too).
            var (hung, _) = LocalEngine.Interpret(Printed("booting\n", "[error] ServerScriptService.Spinner:3: Script timeout\n", timedOut: true), null);
            Assert.False(hung!.Ok);
            Assert.True(hung.Local);
            var completes = Assert.Single(hung.Results);
            Assert.Equal(("the test run completes", "run", false), (completes.Name, completes.Phase, completes.Ok));
            Assert.Contains("never yields", completes.Detail);
            Assert.Contains(hung.Errors, e => e.Message.Contains("Script timeout"));
            Assert.Contains("did not finish in 240 s", hung.Summary);
            Assert.False(TestStage.GameChecksPass(hung));

            // The task itself failed: recorded as Roblox records a failed task.
            var (failed, _) = LocalEngine.Interpret(Printed(LocalEngine.Marker + "{\"ok\":false,\"error\":\"TestRunner:158: attempt to index nil with 'UseMemory'\\nstack\",\"stats\":{}}\n"), null);
            Assert.False(failed!.Ok);
            Assert.Contains("the test run itself failed: TestRunner:158", failed.Summary);

            // The stand-in crashed or printed nonsense: nothing was learned about the game.
            var (crashed, why) = LocalEngine.Interpret(new LocalEngineOutput("", "thread 'main' panicked at 'deserialize'", 101, false, TimeSpan.FromSeconds(1)), null);
            Assert.Null(crashed);
            Assert.Contains("printed no result (exit 101)", why);
            Assert.Null(LocalEngine.Interpret(Printed(LocalEngine.Marker + "{not json"), null).Run);
        }

        /// <summary>
        /// A whole game made without an API key: the builder's run_tests runs in the local engine and its pass lets
        /// the game through Build and Test; the launch waits for the key, and then runs the game in Roblox's engine
        /// before anything is created on Roblox.
        /// </summary>
        [Fact]
        public async Task WithoutAKey_AGameGoesThroughBuildAndTestOnTheLocalEngine_AndLaunchStillRunsRobloxsEngine()
        {
            var engine = new FakeLocalEngine();
            using var h = new StudioHarness(apiKey: false, localEngine: engine);
            await h.Harvester.RunOnceAsync(CancellationToken.None);
            PipelineEndToEndTests.ScriptResearch(h.Llm);
            h.Llm.Script("builder",
                ("design_world", new { }),
                ("build", new { }),
                ("update_milestone", new { id = "m1", done = true }),
                ("update_milestone", new { id = "m2", done = true }),
                ("update_milestone", new { id = "m3", done = true }),
                ("update_milestone", new { id = "m4", done = true }),
                ("update_milestone", new { id = "m5", done = true }),
                ("mark_feature_complete", new { notes = "not yet: the engine has not run" }),
                ("run_tests", new { }),
                ("mark_feature_complete", new { notes = "the generated course, checkpoints and the finish" }));
            PipelineEndToEndTests.ScriptReview(h.Llm);
            PipelineEndToEndTests.ScriptMarketing(h.Llm, featureAsset: false);
            await h.StepUntilAsync(() => h.Ctx.Store.Read(s => s.Games.Count) == 1 && h.OnlyGame().WaitingReason != null, 40);

            var game = h.OnlyGame();
            Assert.Equal(GameStage.Launch, game.Stage);
            Assert.Contains("API key", game.WaitingReason);
            Assert.Contains(h.Llm.ToolResults, r => r.StartsWith("builder.mark_feature_complete") && r.Contains("run_tests has not run on this build"));
            Assert.Contains(h.Llm.ToolResults, r => r.StartsWith("builder.run_tests") && r.Contains("PASSED") && r.Contains("local engine"));
            Assert.True(game.LastTest!.Passed, game.LastTest.Summary);
            Assert.True(game.LastTest.Engine!.Local);
            Assert.Contains("every local engine check green", game.LastTest.Summary);
            Assert.Contains(game.LastTest.Issues, i => i.Message.Contains("ran in the studio's local engine"));
            Assert.DoesNotContain(game.LastTest.Issues, i => i.Message.Contains("engine tests wait"));
            Assert.Single(engine.Runs); // the Test stage reused the builder's pass on the same revision
            Assert.DoesNotContain(h.Roblox.Requests, r => r.Url.Contains("luau-execution"));

            // The key arrives: the launch runs the game in Roblox's engine before it creates anything there.
            h.Ctx.Settings.ApiKey = "test-key";
            h.Now += TimeSpan.FromHours(1);
            await h.StepUntilAsync(() => h.OnlyGame().Stage == GameStage.Live, 40);
            game = h.OnlyGame();
            Assert.False(game.Build.LastEngineRun!.Local);
            Assert.Equal(game.Build.Revision, game.Publish.EngineTestedRevision);
            var requests = h.Roblox.Requests.Select(r => r.Url).ToList();
            int engineRun = requests.FindIndex(u => u.Contains("luau-execution-session-tasks"));
            int firstProduct = requests.FindIndex(u => u.Contains("/game-passes/v1/") || u.Contains("/developer-products/v2/"));
            Assert.True(engineRun >= 0 && firstProduct > engineRun, $"engine run at {engineRun}, first product at {firstProduct}");
        }

        private sealed class Downloads : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> answer;
            public Downloads(Func<HttpRequestMessage, HttpResponseMessage> answer) => this.answer = answer;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(answer(request));
        }
    }

    /// <summary>A local engine that prints a canned run of the kit's TestRunner (green unless a test says otherwise) and records what it was given.</summary>
    internal sealed class FakeLocalEngine : ILocalEngine
    {
        public const string GreenRun = "{\"passed\":5,\"failed\":0,\"skipped\":0,\"results\":[" +
            "{\"name\":\"Core · new profiles start fresh\",\"ok\":true,\"phase\":\"kit\",\"owner\":\"kit\",\"seconds\":0.01}," +
            "{\"name\":\"boot: the server starts and runs for 8 s without errors\",\"ok\":true,\"phase\":\"boot\",\"owner\":\"game\",\"seconds\":8}," +
            "{\"name\":\"Places · there is somewhere to spawn\",\"ok\":true,\"phase\":\"world\",\"owner\":\"kit\",\"seconds\":0.01}," +
            "{\"name\":\"Places · obby stages run 1..N with a finish\",\"ok\":true,\"phase\":\"world\",\"owner\":\"kit\",\"seconds\":0.02}," +
            "{\"name\":\"Places · obby advances one stage at a time\",\"ok\":true,\"phase\":\"world\",\"owner\":\"kit\",\"seconds\":0.4}]," +
            "\"errors\":[],\"warnings\":[],\"booted\":[\"ServerScriptService.OmniKitServer.OmniKitBoot\"],\"kitVersion\":2}";

        public const string FailingRun = "{\"passed\":2,\"failed\":2,\"skipped\":0,\"results\":[" +
            "{\"name\":\"Core · new profiles start fresh\",\"ok\":true,\"phase\":\"kit\",\"owner\":\"kit\"}," +
            "{\"name\":\"boot: the server starts and runs for 8 s without errors\",\"ok\":false,\"detail\":\"1 error(s) while starting and running (see boot errors)\",\"phase\":\"boot\",\"owner\":\"game\",\"seconds\":8}," +
            "{\"name\":\"Places · there is somewhere to spawn\",\"ok\":true,\"phase\":\"world\",\"owner\":\"kit\"}," +
            "{\"name\":\"Factory · the first dropper is affordable\",\"ok\":false,\"detail\":\"starting candy 50 < first dropper 60\",\"phase\":\"game\",\"owner\":\"game\"}]," +
            "\"errors\":[{\"kind\":\"error\",\"message\":\"ServerScriptService.Welcome:4: attempt to concatenate string with nil\",\"phase\":\"boot\",\"trace\":\"ServerScriptService.Welcome:4\"}],\"warnings\":[]}";

        public bool Available { get; set; } = true;
        public string? Problem { get; set; }
        /// <summary>What RunAll returns this time (JSON).</summary>
        public string Result { get; set; } = GreenRun;
        public List<(byte[] Place, int BootSeconds)> Runs { get; } = new();

        /// <summary>The line engine.luau ends with, for a run that returned <paramref name="result"/>.</summary>
        public static string Line(string result) => LocalEngine.Marker + "{\"ok\":true,\"result\":" + result + ",\"stats\":{\"seconds\":14.2,\"errors\":0}}\n";

        public Task<LocalEngineOutput?> RunAsync(byte[] placeXml, int bootSeconds, CancellationToken ct)
        {
            lock (Runs) Runs.Add((placeXml, bootSeconds));
            return Task.FromResult<LocalEngineOutput?>(new LocalEngineOutput("Welcome to the factory\n" + Line(Result), "", 0, false, TimeSpan.FromSeconds(14)));
        }
    }
}
