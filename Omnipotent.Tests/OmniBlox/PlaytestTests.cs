using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox;
using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Pipeline;
using Omnipotent.Services.OmniBlox.Pipeline.Tools;
using Omnipotent.Services.OmniBlox.Rbx;

namespace Omnipotent.Tests.OmniBlox
{
    /// <summary>
    /// The owner jumping into a game at any stage: playtest builds of the files as they are, of the last clean
    /// build or of an earlier one; publishing them to a private experience (never a live one); Studio files;
    /// the playtest log the builder reads; and Follow, which moves a running playtest onto each clean build.
    /// </summary>
    public class PlaytestTests
    {
        /// <summary>A studio whose one game has just built cleanly (r1) and is waiting for its tests.</summary>
        private static async Task<(StudioHarness H, string GameId)> BuiltGameAsync()
        {
            var h = new StudioHarness();
            await h.Harvester.RunOnceAsync(CancellationToken.None);
            PipelineEndToEndTests.ScriptResearch(h.Llm);
            PipelineEndToEndTests.ScriptBuild(h.Llm);
            await h.StepUntilAsync(() => h.Ctx.Store.Read(s => s.Games.Count) == 1 && h.OnlyGame().Stage == GameStage.Test);
            return (h, h.OnlyGame().GameId);
        }

        private static (string Server, string Client) Configs(byte[] placeXml)
        {
            var all = RbxFile.Read(placeXml).AllInstances().ToList();
            return (all.Single(i => i.Name == "Config" && i.Parent?.Name == "OmniKitServer").GetString("Source")!,
                all.Single(i => i.Name == "PublicConfig").GetString("Source")!);
        }

        private static bool Has(byte[] placeXml, string name) => RbxFile.Read(placeXml).AllInstances().Any(i => i.Name == name);

        [Fact]
        public async Task APlaytestBuild_SimulatesPurchasesSavesApartAndNeverCarriesTheLiveKey()
        {
            var (h, id) = await BuiltGameAsync();
            using var _ = h;

            var built = h.Ctx.Playtests.Build(id, "latest", Playtests.Roblox);
            Assert.True(built.Compile.Ok, string.Join("\n", built.Compile.Errors));
            Assert.Equal("r1", built.Label);
            Assert.Null(built.Note);
            var game = h.Game(id);
            Assert.False(string.IsNullOrEmpty(game.Playtest.Secret));
            var (server, client) = Configs(built.Compile.PlaceXml);
            Assert.Contains(game.Playtest.Secret, server);
            Assert.DoesNotContain(game.TelemetrySecret, server);
            Assert.Contains($"DataStore = \"OmniPlaytest_{id}_1\"", server);
            Assert.Contains("MemoryData = false", server);
            Assert.Contains("Build = \"r1\"", client);
            Assert.Contains("Where = \"roblox\"", client);
            Assert.DoesNotContain(game.Playtest.Secret, client);
            Assert.True(LuauSyntax.IsValid(server), server);
            Assert.True(LuauSyntax.IsValid(client), client);

            // The real build knows nothing of playtests.
            Assert.DoesNotContain("Playtest", Configs(h.Ctx.Compile(id).PlaceXml).Server);

            // Studio has no DataStores for a place file, so its saves stay in memory.
            var studio = h.Ctx.Playtests.Build(id, "latest", Playtests.Studio);
            Assert.Contains("MemoryData = true", Configs(studio.Compile.PlaceXml).Server);
            Assert.NotEqual(built.Fingerprint, studio.Fingerprint);
            Assert.Equal(built.Fingerprint, h.Ctx.Playtests.Build(id, "r1", Playtests.Roblox).Fingerprint);
            Assert.Equal("Lava Forest Obby r1.rbxlx", Playtests.FileName(game, built.Label));
        }

        [Fact]
        public async Task AnyBuildCanBePlayed_TheFilesNow_TheLastCleanOne_OrAnEarlierOne()
        {
            var (h, id) = await BuiltGameAsync();
            using var _ = h;
            var ws = h.Ctx.Workspace(id);

            ws.WriteFile("src/server/RisingLava.server.luau", "print('the lava rises')\n");
            var wip = h.Ctx.Playtests.Build(id, "latest", Playtests.Roblox);
            Assert.Equal("r1+", wip.Label);
            Assert.True(Has(wip.Compile.PlaceXml, "RisingLava"));

            // Mid-edit, a file that does not parse: "latest" plays the last clean build, "current" says why it cannot.
            ws.WriteFile("src/server/Broken.server.luau", "local x = (\n");
            var fallback = h.Ctx.Playtests.Build(id, "latest", Playtests.Roblox);
            Assert.Equal("r1", fallback.Label);
            Assert.Contains("do not compile", fallback.Note);
            Assert.False(Has(fallback.Compile.PlaceXml, "RisingLava"));
            var broken = Assert.Throws<PlaytestException>(() => h.Ctx.Playtests.Build(id, "current", Playtests.Roblox));
            Assert.Equal(PlaytestProblem.DoesNotCompile, broken.Problem);
            Assert.Contains("Broken.server.luau", broken.Message);

            // The next clean build, and r1 is still there to play as it was.
            ws.DeleteFile("src/server/Broken.server.luau");
            Assert.True(BuildTools.RunBuild(h.Ctx, id).Ok);
            Assert.Equal("r2", h.Ctx.Playtests.Build(id, "latest", Playtests.Roblox).Label);
            Assert.False(Has(h.Ctx.Playtests.Build(id, "r1", Playtests.Roblox).Compile.PlaceXml, "RisingLava"));
            Assert.True(Has(h.Ctx.Playtests.Build(id, "2", Playtests.Roblox).Compile.PlaceXml, "RisingLava"));
            Assert.Equal(PlaytestProblem.NoSuchBuild, Assert.Throws<PlaytestException>(() => h.Ctx.Playtests.Build(id, "r9", Playtests.Roblox)).Problem);
            Assert.Equal(PlaytestProblem.NoSuchBuild, Assert.Throws<PlaytestException>(() => h.Ctx.Playtests.Build(id, "newest", Playtests.Roblox)).Problem);

            // Research has no world yet.
            string fresh = h.Orchestrator.StartGame();
            Assert.Equal(PlaytestProblem.NoWorld, Assert.Throws<PlaytestException>(() => h.Ctx.Playtests.Build(fresh, "latest", Playtests.Studio)).Problem);
        }

        [Fact]
        public async Task PlayingInRoblox_PublishesToTheGamesPrivateExperience_AndMovesItsServerOntoIt()
        {
            var (h, id) = await BuiltGameAsync();
            using var _ = h;
            var access = h.Ctx.Playtests.Describe(h.Game(id));
            Assert.True(access.Available);
            Assert.True(access.OwnExperience);
            Assert.Null(access.Slot); // borrowed on the first play, as Test would

            var first = await h.Ctx.Playtests.PublishAsync(id, "latest", fresh: false, followed: false, CancellationToken.None);
            Assert.False(first.Reused);
            Assert.Equal("https://www.roblox.com/games/start?placeId=2002", first.JoinUrl);
            Assert.Equal("r1", first.Publish.Build);
            Assert.True(first.Publish.ServersRestarted);
            Assert.Empty(first.Warnings);
            Assert.Contains(h.Roblox.Requests, r => r.Method == "POST" && r.Url.Contains("/universes/v1/1001/places/2002/versions?versionType=Published"));
            Assert.Contains(h.Roblox.Requests, r => r.Method == "POST" && r.Url.EndsWith("/cloud/v2/universes/1001:restartServers"));
            var game = h.Game(id);
            Assert.Equal(2002, game.Slot!.PlaceId);
            Assert.Equal(first.Publish.Version, game.Playtest.LastPublish!.Version);
            Assert.Null(game.Publish.LastPublishedVersion); // a playtest is not a release
            Assert.False(game.Publish.IsPublic);
            Assert.Contains(h.Ctx.Store.Read(s => s.Events.ToList()), e => e.Kind == "playtest.published" && e.GameId == id);

            // Rejoining the same build sends nothing, so a running server is left alone.
            int requests = h.Roblox.Requests.Count;
            var again = await h.Ctx.Playtests.PublishAsync(id, "latest", false, false, CancellationToken.None);
            Assert.True(again.Reused);
            Assert.Equal(requests, h.Roblox.Requests.Count);

            // Starting fresh is a new save store, so a new build.
            var fresh = await h.Ctx.Playtests.PublishAsync(id, "latest", fresh: true, followed: false, CancellationToken.None);
            Assert.False(fresh.Reused);
            Assert.Equal(2, h.Game(id).Playtest.SaveSlot);

            // A key without universe:write still publishes; the owner is told to rejoin.
            h.Roblox.RestartRefused = true;
            h.Ctx.Workspace(id).WriteFile("src/server/RisingLava.server.luau", "print('the lava rises')\n");
            var unmoved = await h.Ctx.Playtests.PublishAsync(id, "latest", false, false, CancellationToken.None);
            Assert.Equal("r1+", unmoved.Publish.Build);
            Assert.False(unmoved.Publish.ServersRestarted);
            Assert.Contains(unmoved.Warnings, w => w.Contains("rejoin"));
        }

        [Fact]
        public async Task APublicGame_IsOnlyEverPlayedInThePlaytestExperience()
        {
            var (h, id) = await BuiltGameAsync();
            using var _ = h;
            h.Ctx.Update(id, g =>
            {
                g.Stage = GameStage.Live;
                g.Publish.IsPublic = true;
                g.Slot = new ExperienceSlot { UniverseId = 1001, PlaceId = 2002 };
            });
            var closed = h.Ctx.Playtests.Describe(h.Game(id));
            Assert.False(closed.Available);
            Assert.Contains("OmniBlox_PlaytestSlot", closed.Reason);
            var refused = await Assert.ThrowsAsync<PlaytestException>(() => h.Ctx.Playtests.PublishAsync(id, "latest", false, false, CancellationToken.None));
            Assert.Equal(PlaytestProblem.NoRoblox, refused.Problem);

            h.Ctx.Settings.PlaytestSlot = new ExperienceSlot { UniverseId = 3003, PlaceId = 4004 };
            var result = await h.Ctx.Playtests.PublishAsync(id, "latest", false, false, CancellationToken.None);
            Assert.Equal("https://www.roblox.com/games/start?placeId=4004", result.JoinUrl);
            Assert.False(result.Publish.OwnExperience);
            Assert.Equal(id, h.Ctx.Store.Read(s => s.Studio.PlaytestSlot!.GameId));
            Assert.DoesNotContain(h.Roblox.Requests, r => r.Url.Contains("/places/2002/versions") || r.Url.Contains("/universes/1001:"));

            // The page shows the link while this game is what the playtest experience runs.
            var view = JObject.Parse(OmniBloxViews.Serialize(OmniBloxViews.GameDetail(h.Ctx, id)!));
            Assert.Equal(result.JoinUrl, (string?)view["Playtest"]!["JoinUrl"]);
            Assert.Equal(4004, (long?)view["Playtest"]!["Roblox"]!["PlaceId"]);
            Assert.Equal("", (string?)view["Game"]!["Playtest"]!["Secret"]);
            Assert.DoesNotContain(h.Game(id).Playtest.Secret, view.ToString());
        }

        [Fact]
        public async Task WithoutAKey_TheGameStillOpensInStudio()
        {
            var (h, id) = await BuiltGameAsync();
            using var _ = h;
            h.Ctx.Settings.ApiKey = null;
            var access = h.Ctx.Playtests.Describe(h.Game(id));
            Assert.False(access.Available);
            Assert.Contains("Open in Studio works without it", access.Reason);
            Assert.True(h.Ctx.Playtests.Build(id, "latest", Playtests.Studio).Compile.Ok);
            await Assert.ThrowsAsync<PlaytestException>(() => h.Ctx.Playtests.PublishAsync(id, "latest", false, false, CancellationToken.None));
        }

        [Fact]
        public async Task WhatAPlaytestReports_StaysOutOfTheGamesNumbers_AndReachesTheBuilder()
        {
            var (h, id) = await BuiltGameAsync();
            using var _ = h;
            const string error = "ServerScriptService.Lava:3: attempt to index nil with 'Position'";
            var payload = JObject.FromObject(new
            {
                game = id,
                server = "srv-1",
                players = 1,
                build = "r1",
                where = "roblox",
                events = new object[]
                {
                    new { n = "session_start", s = "a" },
                    new { n = "funnel", s = "a", f = new { funnel = "Obby", step = 2.0, name = "Stage 2" } },
                    new { n = "funnel", s = "a", f = new { funnel = "Obby", step = 3.0, name = "Stage 3" } },
                    new { n = "death", s = "a" },
                    new { n = "death", s = "a" },
                    new { n = "economy", s = "a", f = new { flow = "Source", amount = 50.0, balance = 50.0, reason = "Stage" } },
                    new { n = "purchase", s = "a", f = new { key = "skip", kind = "product", robux = 0, test = true } },
                    new { n = "feedback", s = "a", f = new { rating = 2.0, reason = "too_hard" } },
                    new { n = "feedback", s = "a", f = new { rating = 9.0, reason = "made up" } },
                    new { n = "error", f = new { message = error, source = "ServerScriptService.Lava", count = 1.0 } },
                    new { n = "daily_reward", s = "a", f = new { streak = 1, amount = 25 } },
                    new { n = "heartbeat", f = new { players = 1.0 } },
                },
            });
            Assert.Equal(12, h.Ctx.Playtests.Ingest(id, payload, h.Now));
            h.Ctx.Playtests.Ingest(id, JObject.FromObject(new { server = "srv-1", players = 1, events = new object[] { new { n = "error", f = new { message = error, count = 25.0 } } } }), h.Now.AddSeconds(30));

            var session = h.Game(id).Playtest.Sessions.Single();
            Assert.Equal("roblox", session.Where);
            Assert.Equal("r1", session.Build);
            Assert.Equal(1, session.Joins);
            Assert.Equal(3, session.FurthestStep);
            Assert.Equal("Stage 3", session.FurthestStepName);
            Assert.Equal(2, session.Deaths);
            Assert.Equal(50, session.Earned);
            Assert.Equal(new[] { "skip" }, session.TestPurchases);
            Assert.Equal(2, session.Feedback.Single().Rating);
            Assert.Equal(25, session.Errors.Single().Count);
            Assert.Equal(1, session.Actions["daily_reward"]);
            Assert.True(h.Ctx.Playtests.IsPlaying(h.Game(id)));
            Assert.Equal(0, h.Ctx.Telemetry.Summarize(id, h.Now.AddDays(-1)).Events);
            Assert.Equal(0, h.Game(id).Stats.Telemetry.Events);

            string briefing = Briefing.Build(h.Game(id), h.Ctx);
            Assert.Contains("The owner playtested r1", briefing);
            Assert.Contains("reached Stage 3", briefing);
            Assert.Contains("test-bought skip", briefing);
            Assert.Contains("Okay (too hard)", briefing);
            Assert.Contains(error + " [ServerScriptService.Lava] ×25", briefing);

            // A Studio run is a session of its own; an old playtest drops out of the briefing.
            h.Ctx.Playtests.Ingest(id, JObject.FromObject(new { server = "studio-1", players = 1, build = "r1+", where = "studio", events = new object[] { new { n = "session_start", s = "b" } } }), h.Now.AddMinutes(1));
            Assert.Equal(2, h.Game(id).Playtest.Sessions.Count);
            Assert.Equal("studio", h.Game(id).Playtest.Sessions[^1].Where);
            h.Now = h.Now.AddDays(4);
            Assert.DoesNotContain("The owner playtested", Briefing.Build(h.Game(id), h.Ctx));
            Assert.False(h.Ctx.Playtests.IsPlaying(h.Game(id)));
        }

        [Fact]
        public async Task Follow_MovesTheOwnersRunningPlaytestOntoEachCleanBuild()
        {
            var (h, id) = await BuiltGameAsync();
            using var _ = h;
            h.Ctx.Playtests.FollowGap = TimeSpan.Zero;
            await h.Ctx.Playtests.PublishAsync(id, "latest", false, false, CancellationToken.None);
            h.Ctx.Playtests.SetFollow(id, true);
            var ws = h.Ctx.Workspace(id);
            int Restarts() => h.Roblox.Requests.Count(r => r.Url.EndsWith(":restartServers"));

            // Nobody is in the playtest: a clean build stays where it is.
            ws.WriteFile("src/server/RisingLava.server.luau", "print('the lava rises')\n");
            Assert.True(BuildTools.RunBuild(h.Ctx, id).Ok);
            await h.Ctx.Playtests.FollowTask;
            Assert.Equal("r1", h.Game(id).Playtest.LastPublish!.Build);
            Assert.Equal(1, Restarts());

            // The owner is in (their server reports a player): the next clean build follows them.
            h.Ctx.Playtests.Ingest(id, JObject.FromObject(new { server = "srv-1", players = 1, build = "r1", where = "roblox", events = new object[] { new { n = "heartbeat" } } }), h.Now);
            ws.WriteFile("src/server/RisingLava.server.luau", "print('the lava rises faster')\n");
            Assert.True(BuildTools.RunBuild(h.Ctx, id).Ok);
            await h.Ctx.Playtests.FollowTask;
            var last = h.Game(id).Playtest.LastPublish!;
            Assert.Equal("r3", last.Build);
            Assert.True(last.Followed);
            Assert.Equal(2, Restarts());

            // Follow off: builds stay put again.
            h.Ctx.Playtests.SetFollow(id, false);
            ws.WriteFile("src/server/RisingLava.server.luau", "print('the lava cools')\n");
            Assert.True(BuildTools.RunBuild(h.Ctx, id).Ok);
            await h.Ctx.Playtests.FollowTask;
            Assert.Equal("r3", h.Game(id).Playtest.LastPublish!.Build);
            Assert.Equal(2, Restarts());
        }
    }
}
