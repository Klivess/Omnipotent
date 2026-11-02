using Newtonsoft.Json;
using Omnipotent.Services.OmniBlox.Agents;
using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Marketing;
using Omnipotent.Services.OmniBlox.Pipeline;
using Omnipotent.Services.OmniBlox.Pipeline.Tools;
using SkiaSharp;

namespace Omnipotent.Tests.OmniBlox
{
    /// <summary>
    /// The builder's world, through the very tools an agent calls: a game with no template designs its own map —
    /// a landform, sculpted places, a plan with paths carved in, kit props, landmarks, landscaping layers and its
    /// own gameplay objects — then builds, and the map report and the renderer judge it. With OMNIBLOX_RENDER_OUT
    /// set, every picture (and store art made from them) is written there to be looked at.
    /// </summary>
    public class WorldToolTests
    {
        /// <summary>A new game in Build (no template: the kit alone and an empty world), with the test library to dress it.</summary>
        internal static async Task<(StudioHarness H, string Id, AgentToolbox Box)> IslandGameAsync()
        {
            var h = new StudioHarness();
            await h.Harvester.RunOnceAsync(CancellationToken.None);
            TestModels.Seed(h.Ctx.Library);
            string id = h.Orchestrator.StartGame();
            h.Ctx.Update(id, g => g.Brief = new GameBrief
            {
                Title = "Crab Cove", Genre = "collecting adventure", Pitch = "Catch crabs round a forested island and climb to the crystal on the cliff.",
                CoreLoop = "Catch crabs on the beach, sell them at the docks, buy a better net, unlock the old grove and its shells.",
                World = "One island in a blue sea: docks where you spawn, a crab beach to the west, an old grove behind a gate to the east, and a cliff with a glowing crystal to the south, reached by a ramp.",
                MustHave = { "Four places on one island", "Nets", "A crystal on the cliff" }, DoneWhen = { "A new player catches a crab within 30 seconds" },
            });
            h.Orchestrator.Enter(id, GameStage.Build);
            return (h, id, BuildTools.Create(h.Ctx, id));
        }

        internal static async Task<ToolResult> Call(AgentToolbox box, string tool, object args)
        {
            var result = await box.ExecuteAsync(tool, JsonConvert.SerializeObject(args), CancellationToken.None);
            Assert.False(result.IsError, $"{tool}: {result.Text}");
            return result;
        }

        /// <summary>
        /// What a builder does with the map tools, milestone by milestone: the land (an island), the places shaped
        /// into it, the plan with its paths carved, the props and landmarks, the game's own objects, then the
        /// landscaping layers. The scripted builder of the pipeline tests makes the same map (it needs
        /// <see cref="TestModels"/> in the library).
        /// </summary>
        internal static (string Tool, object Args)[] MapSteps()
        {
            var steps = new List<(string, object)>
            {
                ("terrain_generate", new { shape = "island", biome = "forest", size = new[] { 760, 760 }, hills = 10, seed = 5 }),
                ("set_map_area", new { area = new[] { -200, -200, 200, 200 } }),
                ("terrain_sculpt", new
                {
                    ops = new object[]
                    {
                        new { kind = "flatten", center = new[] { 0, 120 }, radius = 46, height = 0, falloff = 0.4 },
                        new { kind = "mesa", center = new[] { 0, -150 }, radius = 38, height = 16, rough = 0.15 },
                        new { kind = "ramp", points = new[] { new[] { 0, -76 }, new[] { 0, -112 } }, height = 0.5, end_height = 16, width = 14 },
                        new { kind = "flatten", center = new[] { 100, 14 }, radius = 16, height = 0, falloff = 0.5 },
                        new { kind = "lake", center = new[] { -120, 110 }, radius = 26, height = 6, rough = 0.2 },
                        new { kind = "paint", center = new[] { -110, -20 }, radius = 50, material = "Sand", rough = 0.2 },
                    },
                }),
                ("plan_map", new
                {
                    concept = "Docks in the north where players arrive; a crab beach to the west; the old grove behind a gate to the east; a crystal cliff in the south, up a ramp.",
                    zones = new object[]
                    {
                        new { id = "docks", name = "The Docks", circle = new[] { 0, 120, 48 }, purpose = "sell crabs, buy nets", order = 1, theme = "sunny meadow docks" },
                        new { id = "beach", name = "Crab Beach", circle = new[] { -110, -20, 52 }, purpose = "catch crabs", order = 2, theme = "sandy beach" },
                        new { id = "grove", name = "Old Grove", circle = new[] { 112, -44, 52 }, purpose = "find shells", order = 3, theme = "misty forest" },
                        new { id = "cliff", name = "Crystal Cliff", circle = new[] { 0, -150, 36 }, purpose = "the crystal goal", order = 4, theme = "rocky forest cliff" },
                    },
                    paths = new object[]
                    {
                        new { from = "docks", to = "beach", points = new[] { new[] { -20, 90 }, new[] { -70, 40 }, new[] { -100, 20 } }, width = 12 },
                        new { from = "docks", to = "grove", points = new[] { new[] { 20, 90 }, new[] { 70, 40 }, new[] { 100, 12 } }, width = 12 },
                        new { from = "docks", to = "cliff", points = new[] { new[] { 0, 74 }, new[] { 0, -74 } }, width = 12 },
                    },
                    carve = true,
                }),
                ("build_prop", new { kind = "spawn", position = new[] { 0, 140 }, face = new[] { 0, 60 } }),
                ("build_prop", new { kind = "stand", position = new[] { 24, 112 }, face = new[] { 0, 140 }, text = "🦀 NETS", panel = "nets" }),
                ("build_prop", new { kind = "pad", position = new[] { -24, 112 }, text = "💰 CRAB MARKET", tags = new[] { "CrabMarket" }, size = new[] { 12 }, face = new[] { 0, 140 } }),
                ("build_prop", new { kind = "leaderboard", position = new[] { -40, 140 }, face = new[] { 0, 140 } }),
                ("build_prop", new { kind = "signpost", position = new[] { 12, 82 }, text = "🦀 Beach · Grove 🌲", face = new[] { 0, 140 } }),
                ("build_prop", new { kind = "gate", zone = "Old Grove", price = 250, position = new[] { 100, 14 }, face = new[] { 70, 40 } }),
                ("build_prop", new { kind = "crystal", position = new[] { 0, -156 }, height = 30 }),
                ("build_prop", new { kind = "pad", position = new[] { 0, -136 }, text = "⭐ SUMMIT", tags = new[] { "Summit" }, size = new[] { 10 }, face = new[] { 0, -76 } }),
                ("place_asset", new { role = "tree", position = new[] { 30, 150 }, scale = 3, name = "Great Oak" }),
                ("place_asset", new { role = "conifer", position = new[] { -125, -50 }, scale = 2.6, name = "Giant Pine" }),
                ("place_asset", new { role = "tree", position = new[] { 128, -70 }, scale = 3.2, name = "Elder Oak" }),
                ("place_asset", new { role = "fountain", position = new[] { -10, 100 } }),
                // The game's own objects: crabs on the beach, shells in the grove.
                ("add_objects", new { objects = Enumerable.Range(0, 8).Select(i => new { name = "Crab", position = new[] { -125 + i % 4 * 12, -40 + i / 4 * 26 }, size = new[] { 2.5, 1.2, 2 }, color = "#E0503C", tags = new[] { "Crab" } }).ToArray() }),
                ("add_objects", new { objects = Enumerable.Range(0, 6).Select(i => new { name = "Shell", position = new[] { 98 + i % 3 * 14, -56 + i / 3 * 20 }, size = new[] { 1.6, 1, 1.6 }, color = "#F4D6E8", shape = "Ball", tags = new[] { "Shell" } }).ToArray() }),
                ("map_palette", new { style = "stylized" }),
            };
            foreach (var layer in new object[]
            {
                new { kind = "forest", avoid_zones = new[] { "docks", "beach", "cliff" }, clearings = 0.35 },
                new { kind = "rim", zone = "docks" },
                new { kind = "rim", zone = "beach", density = 0.8 },
                new { kind = "rim", zone = "cliff" },
                new { kind = "meadow", zone = "docks", density = 0.8 },
                new { kind = "accents", zone = "beach" },
                new { kind = "accents", zone = "grove" },
                new { kind = "rocks", zone = "cliff" },
                new { kind = "roadside" },
                new { kind = "treeline" },
            })
                steps.Add(("dress_area", layer));
            return steps.ToArray();
        }

        /// <summary>Makes the map of <see cref="MapSteps"/> through the tools, checking what each says. Returns the plan's places.</summary>
        internal static async Task<List<MapZone>> DecorateAsync(StudioHarness h, string id, AgentToolbox box)
        {
            foreach (var (tool, args) in MapSteps())
            {
                string text = (await Call(box, tool, args)).Text;
                switch (tool)
                {
                    case "terrain_generate":
                        Assert.Contains("the island landform with a sea edge", text);
                        Assert.Contains("island at (0, 0)", text);
                        break;
                    case "plan_map": Assert.Contains("Carved 3 path(s)", text); break;
                    case "build_prop" when text.Contains("Built gate"): Assert.Contains("Old Grove is now a gated zone", text); break;
                    case "map_palette": Assert.Contains("Art style: stylized", text); break;
                    case "dress_area": Assert.Contains("Dressed", text); break;
                }
            }
            return h.Ctx.Workspace(id).LoadScene().Layout.Zones;
        }

        [Fact]
        public async Task AMapBuiltWithTheMapTools_ClearsTheBar_AndTheReportAndTheRendererUnderstandIt()
        {
            var (h, id, box) = await IslandGameAsync();
            using var _ = h;
            var zones = await DecorateAsync(h, id, box);

            var scene = h.Ctx.Workspace(id).LoadScene();
            Assert.Equal("island", scene.Land!.Shape);
            Assert.True(scene.Layout.Zones.Single(z => z.Id == "grove").Gated);
            string summary = (await Call(box, "scene_summary", new { limit = 10 })).Text;
            Assert.Contains("Terrain: island with a sea edge", summary);
            Assert.Contains("Crab Beach", summary);

            string build = (await Call(box, "build", new { })).Text;
            Assert.Contains("BUILD OK — revision 1", build);
            var game = h.Game(id);
            Assert.True(game.Build.LastBuildOk, string.Join("\n", game.Build.Errors));
            Assert.True(game.Build.MapScore >= StageGates.MinMapScore, $"map score {game.Build.MapScore}: " + string.Join(" | ", game.Build.MapFindings));
            Assert.DoesNotContain(game.Build.MapFindings, f => f.StartsWith("[error]", StringComparison.Ordinal));
            Assert.False(game.Build.MapFindings.Any(f => f.Contains("floats") || f.Contains("is sunk")), string.Join("\n", game.Build.MapFindings));
            Assert.True(string.IsNullOrEmpty(game.Build.Economy), "only an old simulator has the economy bot");

            // A rebuild of unchanged files is the same revision (so a passing engine run on it still counts).
            string again = (await Call(box, "build", new { })).Text;
            Assert.Contains("revision 1 (no file changed", again);
            Assert.Equal(1, h.Game(id).Build.Revision);

            var report = await Call(box, "map_report", new { });
            Assert.Contains("Map score", report.Text);
            Assert.Contains("zone beach", report.Text);
            Assert.Single(report.Images);

            string? output = Environment.GetEnvironmentVariable("OMNIBLOX_RENDER_OUT");
            foreach (string view in new[] { "map", "overview", "aerial", "iso", "spawn" }.Concat(zones.Select(z => "zone:" + z.Id)))
            {
                var shot = await Call(box, "render_scene", new { view });
                var png = Assert.Single(shot.Images);
                using (var bitmap = SKBitmap.Decode(png))
                {
                    Assert.Equal((1280, 760), (bitmap.Width, bitmap.Height));
                    Assert.True(Colourful(bitmap) > 0.2, $"'{view}' looks flat or empty");
                }
                if (!string.IsNullOrEmpty(output))
                {
                    Directory.CreateDirectory(output);
                    File.WriteAllBytes(Path.Combine(output, "world-" + view.Replace(':', '-') + ".png"), png);
                }
            }

            // Store art from the world in the marketing look.
            var studio = new MarketingStudio(null);
            byte[] backdrop = h.Ctx.Render(id, "aerial", 1920, 1080, marketing: true);
            byte[] sticker = File.ReadAllBytes(h.Ctx.Library.ThumbnailPath(FakeRoblox.AssetId));
            byte[] thumb = studio.MakeThumbnail(new ArtSpec { Headline = "Crab Cove", Subtitle = "Catch. Sell. Explore!", SceneRender = backdrop, Featured = { sticker }, Badge = "NEW", Accent = new Services.OmniBlox.Rbx.RbxColor3uint8(46, 160, 67) });
            byte[] icon = studio.MakeIcon(new ArtSpec { Headline = "Crabs", SceneRender = h.Ctx.Render(id, "iso", 1024, 1024, marketing: true), Featured = { sticker }, Accent = new Services.OmniBlox.Rbx.RbxColor3uint8(46, 160, 67) });
            using (var t = SKBitmap.Decode(thumb)) Assert.Equal((1920, 1080), (t.Width, t.Height));
            using (var i = SKBitmap.Decode(icon)) Assert.Equal((512, 512), (i.Width, i.Height));
            if (!string.IsNullOrEmpty(output))
            {
                File.WriteAllBytes(Path.Combine(output, "store-thumbnail.png"), thumb);
                File.WriteAllBytes(Path.Combine(output, "store-icon.png"), icon);
            }
        }

        [Fact]
        public async Task TheTemplateTools_AreGone_AndAWorldGeneratedFromTheOldSpec_IsEditedLikeAnyOther()
        {
            var (h, id, box) = await IslandGameAsync();
            using var _ = h;
            foreach (string gone in new[] { "design_world", "balance_economy" })
            {
                var refused = await box.ExecuteAsync(gone, "{}", CancellationToken.None);
                Assert.True(refused.IsError, gone + " still exists");
            }
            // A scene.json saved by the old generator (with its world spec) loads; the spec is dropped and the tools edit the world.
            var ws = h.Ctx.Workspace(id);
            File.WriteAllText(ws.ScenePath, "{\"Spec\": {\"Layout\": \"hub\", \"Zones\": [{\"Name\": \"Pine Forest\"}]}, \"Land\": {\"Size\": [500, 500], \"Hills\": 12}, \"Objects\": [{\"Id\": \"g1\", \"Kind\": \"part\", \"Name\": \"Old\", \"Source\": \"gen\", \"Position\": [0, 1, 0], \"Size\": [4, 2, 4]}]}");
            Assert.Single(ws.LoadScene().Objects);
            foreach (var (tool, args) in new (string, object)[]
            {
                ("terrain_generate", new { shape = "valley", hills = 14 }),
                ("set_map_area", new { area = new[] { -150, -150, 150, 150 } }),
                ("plan_map", new { concept = "A valley of two villages" }),
            })
                await Call(box, tool, args);
            var scene = ws.LoadScene();
            Assert.Equal("valley", scene.Land!.Shape);
            Assert.Single(scene.Objects, o => o.Name == "Old");
            Assert.DoesNotContain("\"Spec\"", File.ReadAllText(ws.ScenePath));
        }

        [Fact]
        public async Task AnEmptyWorld_IsCalledOutByTheMapReport_AndCannotBeDeclaredComplete()
        {
            var (h, id, box) = await IslandGameAsync();
            using var _ = h;
            h.Ctx.Workspace(id).SaveScene(new SceneDocument()); // what the first studio's builder started from
            await Call(box, "add_objects", new { objects = new object[] { new { kind = "spawn", name = "Start", position = new[] { 0, 0.5, 0 }, size = new[] { 6, 1, 6 } } } });
            await Call(box, "build", new { });
            var game = h.Game(id);
            Assert.True(game.Build.LastBuildOk);
            Assert.True(game.Build.MapScore <= MapReport.BlockedCap, "an empty map scored " + game.Build.MapScore);
            Assert.Contains(game.Build.MapFindings, f => f.Contains("No play area"));
            Assert.Contains(game.Build.MapFindings, f => f.Contains("no ground"));

            var refused = await box.ExecuteAsync("mark_feature_complete", "{\"notes\":\"done\"}", CancellationToken.None);
            Assert.True(refused.IsError);
            Assert.Contains("map", refused.Text);
            Assert.Contains("milestones not done", refused.Text);
            Assert.Contains("run_tests has not run", refused.Text);
        }

        [Fact]
        public async Task RunTests_RunsTheGameInTheEngine_AndDiagnoseShowsAFailingChecksSource()
        {
            var (h, id, box) = await IslandGameAsync();
            using var _ = h;
            await Call(box, "write_file", new { path = "src/tests/Forest.luau", content = "return {\n\t[\"chopping pays\"] = function(t)\n\t\tlocal p = t.player()\n\t\tt.eq(1, 2, \"coins after a chop\")\n\tend,\n}\n" });
            h.Roblox.EngineOutput = "{\"results\":[{\"passed\":1,\"failed\":1,\"results\":[" +
                "{\"name\":\"Kit · profiles start fresh\",\"ok\":true,\"phase\":\"kit\",\"owner\":\"kit\"}," +
                "{\"name\":\"Forest · chopping pays\",\"ok\":false,\"detail\":\"coins after a chop: expected 2, got 1\",\"phase\":\"game\",\"owner\":\"game\"}]," +
                "\"errors\":[{\"message\":\"ServerScriptService.Systems.Forest:3: attempt to index nil with 'Health'\",\"phase\":\"boot\"}]}]}";
            await Call(box, "write_file", new { path = "src/server/systems/Forest.luau", content = "local Forest = {}\nfunction Forest.Start()\n\tlocal x = nil :: any\n\tprint(x.Health)\nend\nreturn Forest\n" });

            string run = (await Call(box, "run_tests", new { })).Text;
            Assert.Contains("FAILED", run);
            Assert.Contains("chopping pays", run);
            Assert.Contains("src/server/systems/Forest.luau:3", run);
            var game = h.Game(id);
            Assert.NotNull(game.Build.LastEngineRun);
            Assert.Equal(game.Build.Revision, game.Build.LastEngineRun!.Revision);
            Assert.False(game.Build.LastEngineRun.Ok);
            Assert.Contains(h.Roblox.Requests, r => r.Url.Contains("versionType=Saved"));

            string diagnosis = (await Call(box, "diagnose_failure", new { failure = "chopping" })).Text;
            Assert.Contains("FAILING CHECK", diagnosis);
            Assert.Contains("t.eq(1, 2", diagnosis);
            string error = (await Call(box, "diagnose_failure", new { failure = "Forest.luau:3" })).Text;
            Assert.Contains(">>", error);
            Assert.Contains("print(x.Health)", error);
        }

        [Fact]
        public void Pacing_FlagsASlowFirstPurchase_AndAWall()
        {
            string verdict = BuildTools.Pacing(new List<(string, double, double)> { ("Stone axe", 50, 20), ("Iron axe", 200, 60), ("Gold axe", 6000, 70) });
            Assert.Contains("too slow for a first buy", verdict);
            Assert.Contains("Adjust: Stone axe, Gold axe", verdict);
            string wall = BuildTools.Pacing(new List<(string, double, double)> { ("A", 10, 20), ("B", 40, 30), ("C", 90, 40), ("D", 200, 60), ("E", 400, 80), ("F", 12000, 90) });
            Assert.Contains("big wall", wall);
            string fine = BuildTools.Pacing(new List<(string, double, double)> { ("Stone axe", 15, 30), ("Iron axe", 90, 45), ("Gold axe", 300, 90) });
            Assert.Contains("healthy", fine);
        }

        [Fact]
        public void ExtractCase_FindsOneCaseOfASpec()
        {
            const string spec = "return {\n\t[\"first\"] = function(t)\n\t\tt.ok(true)\n\tend,\n\t[\"second\"] = function(t)\n\t\tt.eq(1, 1)\n\tend,\n}\n";
            string block = BuildTools.ExtractCase(spec, "second");
            Assert.Contains("t.eq(1, 1)", block);
            Assert.DoesNotContain("t.ok(true)", block);
        }

        [Fact]
        public void ContactSheet_LaysOutModelsWithCaptions()
        {
            using var square = new SKBitmap(64, 64);
            square.Erase(SKColors.OrangeRed);
            using var data = SKImage.FromBitmap(square).Encode(SKEncodedImageFormat.Png, 100);
            var png = data.ToArray();
            byte[] sheet = BuildTools.ContactSheet(Enumerable.Range(1, 6).Select(i => ((long)i, (byte[]?)png, $"{i} · 4×4×4")).ToList());
            using var bitmap = SKBitmap.Decode(sheet);
            Assert.Equal(4 * 220, bitmap.Width);
            Assert.Equal(2 * 246, bitmap.Height);
        }

        /// <summary>How much of the picture is saturated colour (a blank or grey render is near 0).</summary>
        private static double Colourful(SKBitmap bitmap)
        {
            int colourful = 0, total = 0;
            for (int y = 0; y < bitmap.Height; y += 8)
                for (int x = 0; x < bitmap.Width; x += 8)
                {
                    bitmap.GetPixel(x, y).ToHsv(out _, out float s, out float v);
                    if (s > 25 && v > 25) colourful++;
                    total++;
                }
            return colourful / (double)total;
        }
    }
}
