using Newtonsoft.Json;
using Omnipotent.Services.OmniBlox.Agents;
using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Marketing;
using Omnipotent.Services.OmniBlox.Pipeline.Tools;
using SkiaSharp;

namespace Omnipotent.Tests.OmniBlox
{
    /// <summary>
    /// The builder's world, through the very tools an agent calls: terrain, the play area, the map plan,
    /// scattering and lines of models, a build, the map report and the renders it looks at. With
    /// OMNIBLOX_RENDER_OUT set, every picture (and store art made from them) is written there to be looked at.
    /// </summary>
    public class WorldToolTests
    {
        internal static async Task<(StudioHarness H, string Id, AgentToolbox Box)> SimulatorAsync()
        {
            var h = new StudioHarness();
            await h.Harvester.RunOnceAsync(CancellationToken.None);
            string id = h.Orchestrator.StartGame();
            h.Ctx.Update(id, g => g.Brief = new GameBrief
            {
                Title = "Timber Island", Genre = "simulator", Template = "simulator", Pitch = "Chop trees, sell logs, unlock new forests.",
                CoreLoop = "Chop trees for logs, sell them, buy better axes, unlock the next forest.",
                MustHave = { "Three zones behind gates", "Axe upgrades", "A rebirth" }, DoneWhen = { "A new player chops a tree within 30 seconds" },
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

        /// <summary>A simulator map the way the builder prompt asks for one: a hub, two themed zones, paths, a lake, dressing and a landmark.</summary>
        internal static async Task DressAsync(AgentToolbox box)
        {
            long tree = FakeRoblox.AssetId;
            await Call(box, "terrain_generate", new { size = new[] { 640, 640 }, biome = "meadow", hills = 14, seed = 7, flat_radius = 120 });
            await Call(box, "set_map_area", new { area = new[] { -220, -220, 220, 220 } });
            await Call(box, "plan_map", new
            {
                concept = "A sunny island: the hub with the shop in the middle, the Pine Woods to the west, the Golden Meadow to the east past the lake.",
                zones = new object[]
                {
                    new { id = "hub", name = "Camp", area = new[] { -60, -60, 60, 60 }, order = 1, purpose = "shop, sell spot, first trees", theme = "sunny clearing" },
                    new { id = "woods", name = "Pine Woods", area = new[] { -220, -220, -70, 220 }, order = 2, purpose = "the first forest", theme = "dense pines" },
                    new { id = "meadow", name = "Golden Meadow", area = new[] { 70, -220, 220, 220 }, order = 3, purpose = "rare golden trees", theme = "flowers and gold" },
                },
                paths = new object[]
                {
                    new { from = "hub", to = "woods", points = new[] { new[] { 0, 0 }, new[] { -150, 0 } } },
                    new { from = "hub", to = "meadow", points = new[] { new[] { 0, 0 }, new[] { 150, 0 } } },
                },
                landmarks = new object[] { new { name = "Watch Tower", position = new[] { 0, -40 }, note = "seen from everywhere" } },
            });
            await Call(box, "terrain_sculpt", new
            {
                ops = new object[]
                {
                    new { kind = "path", points = new[] { new[] { 0, 0 }, new[] { -150, 0 } }, width = 12, material = "Ground" },
                    new { kind = "path", points = new[] { new[] { 0, 0 }, new[] { 150, 0 } }, width = 12, material = "Ground" },
                    new { kind = "lake", center = new[] { 110, 140 }, radius = 32, height = 6 },
                },
            });
            await Call(box, "set_lighting", new { preset = "sunny" });
            await Call(box, "add_objects", new
            {
                objects = new object[]
                {
                    new { kind = "spawn", name = "Start", position = new[] { 0, 30 }, size = new[] { 8, 1, 8 }, yaw = 0 },
                    new { name = "ShopFloor", folder = "Hub/Shop", position = new[] { 0, -20 }, size = new[] { 24, 1, 16 }, color = "#C8A27A", material = "WoodPlanks" },
                    new { name = "ShopRoof", folder = "Hub/Shop", position = new[] { 0, 9, -20 }, size = new[] { 28, 2, 20 }, color = "#E53935" },
                    new { name = "ShopSign", kind = "text", folder = "Hub/Shop", position = new[] { 0, 12, -10 }, size = new[] { 14, 3, 1 }, text = "AXE SHOP" },
                    new { name = "SellPad", position = new[] { 20, 0 }, size = new[] { 10, 1, 10 }, color = "#43A047", material = "Neon", tags = new[] { "SellZone" } },
                    new { name = "WatchTower", folder = "Hub", position = new[] { 0, 22, -40 }, size = new[] { 8, 44, 8 }, color = "#8D6E63", material = "WoodPlanks" },
                    new { name = "Beacon", folder = "Hub", position = new[] { 0, 46, -40 }, size = new[] { 6, 4, 6 }, color = "#FFD54F", material = "Neon", tags = new[] { "Glow", "Sparkle" } },
                },
            });
            await Call(box, "scatter_assets", new { asset_ids = new[] { tree }, zone = "woods", count = 70, seed = 3 });
            await Call(box, "scatter_assets", new { asset_ids = new[] { tree }, zone = "meadow", count = 40, seed = 4, scale = new[] { 0.7, 1.1 } });
            await Call(box, "scatter_assets", new { asset_ids = new[] { tree }, area = new[] { new[] { -60, 40 }, new[] { 60, 60 } }, count = 8, seed = 5 });
            await Call(box, "scatter_assets", new { asset_ids = new[] { tree }, area = new[] { new[] { -70, 62 }, new[] { 70, 220 } }, count = 30, seed = 6, scale = new[] { 0.8, 1.3 } });
            await Call(box, "scatter_assets", new { asset_ids = new[] { tree }, area = new[] { new[] { -70, -220 }, new[] { 70, -62 } }, count = 30, seed = 8, scale = new[] { 0.8, 1.3 } });
            await Call(box, "place_along", new { points = new[] { new[] { -60, 0 }, new[] { -140, 0 } }, spacing = 16, offset = 9, both_sides = true, part = new { size = new[] { 1, 8, 1 }, color = "#FFF59D", material = "Neon" }, name = "PathLamp" });
            await Call(box, "place_ring", new { center = new[] { 0, 0 }, radius = 46, count = 10, part = new { size = new[] { 3, 6, 3 }, color = "#8D6E63", material = "Wood" }, name = "Stump", tags = new[] { "Collectible" }, config = new { Value = 1 } });
        }

        [Fact]
        public async Task TheWorldTools_BuildABigDressedMap_ThatTheReportAndTheRendererUnderstand()
        {
            var (h, id, box) = await SimulatorAsync();
            using var _ = h;
            await DressAsync(box);

            string summary = (await Call(box, "scene_summary", new { limit = 10 })).Text;
            Assert.Contains("Pine Woods", summary);

            string build = (await Call(box, "build", new { })).Text;
            Assert.Contains("BUILD OK — revision 1", build);
            var game = h.Game(id);
            Assert.True(game.Build.LastBuildOk, string.Join("\n", game.Build.Errors));
            Assert.NotNull(game.Build.MapScore);
            Assert.True(game.Build.MapScore >= 60, $"map score {game.Build.MapScore}: " + string.Join(" | ", game.Build.MapFindings));
            Assert.DoesNotContain(game.Build.MapFindings, f => f.StartsWith("[error]", StringComparison.Ordinal));

            // A rebuild of unchanged files is the same revision (so a passing engine run on it still counts).
            string again = (await Call(box, "build", new { })).Text;
            Assert.Contains("revision 1 (no file changed", again);
            Assert.Equal(1, h.Game(id).Build.Revision);

            var report = await Call(box, "map_report", new { });
            Assert.Contains("Map score", report.Text);
            Assert.Contains("zone woods", report.Text);
            Assert.Single(report.Images);

            string? output = Environment.GetEnvironmentVariable("OMNIBLOX_RENDER_OUT");
            foreach (string view in new[] { "map", "overview", "aerial", "iso", "spawn", "zone:woods", "zone:meadow" })
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
            byte[] thumb = studio.MakeThumbnail(new ArtSpec { Headline = "Timber Island", Subtitle = "Chop. Sell. Unlock!", SceneRender = backdrop, Featured = { sticker }, Badge = "NEW", Accent = new Services.OmniBlox.Rbx.RbxColor3uint8(46, 160, 67) });
            byte[] icon = studio.MakeIcon(new ArtSpec { Headline = "Timber", SceneRender = h.Ctx.Render(id, "iso", 1024, 1024, marketing: true), Featured = { sticker }, Accent = new Services.OmniBlox.Rbx.RbxColor3uint8(46, 160, 67) });
            using (var t = SKBitmap.Decode(thumb)) Assert.Equal((1920, 1080), (t.Width, t.Height));
            using (var i = SKBitmap.Decode(icon)) Assert.Equal((512, 512), (i.Width, i.Height));
            if (!string.IsNullOrEmpty(output))
            {
                File.WriteAllBytes(Path.Combine(output, "store-thumbnail.png"), thumb);
                File.WriteAllBytes(Path.Combine(output, "store-icon.png"), icon);
            }
        }

        [Fact]
        public async Task AnEmptyWorld_IsCalledOutByTheMapReport_AndCannotBeDeclaredComplete()
        {
            var (h, id, box) = await SimulatorAsync();
            using var _ = h;
            await Call(box, "add_objects", new { objects = new object[] { new { kind = "spawn", name = "Start", position = new[] { 0, 0.5, 0 }, size = new[] { 6, 1, 6 } } } });
            await Call(box, "build", new { });
            var game = h.Game(id);
            Assert.True(game.Build.LastBuildOk);
            Assert.True(game.Build.MapScore < 60, "an empty map scored " + game.Build.MapScore);
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
            var (h, id, box) = await SimulatorAsync();
            using var _ = h;
            await DressAsync(box);
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
