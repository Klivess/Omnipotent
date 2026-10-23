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
    /// The builder's world, through the very tools an agent calls: the generated baseline, design_world, local
    /// sculpting (what stands there moves with the ground), hand-placed extras, a build, the map report and the
    /// renders it looks at. With OMNIBLOX_RENDER_OUT set, every picture (and store art made from them) is written
    /// there to be looked at.
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

        /// <summary>
        /// What a builder does to the generated baseline: reads the spec, changes the look and regenerates, raises a
        /// gentle hill in a zone (what stands there moves with the ground) and adds a few models of its own there.
        /// </summary>
        internal static async Task<MapZone> DecorateAsync(StudioHarness h, string id, AgentToolbox box)
        {
            string spec = (await Call(box, "design_world", new { })).Text;
            Assert.Contains("\"Layout\": \"hub\"", spec);
            Assert.Contains("\"Biome\": \"forest\"", spec);
            string regenerated = (await Call(box, "design_world", new { spec = new { density = 1.2, lighting = "golden" }, seed = 11 })).Text;
            Assert.Contains("Regenerated", regenerated);
            Assert.Contains("ECONOMY", regenerated);
            var zone = h.Ctx.Workspace(id).LoadScene().Layout.Zones.First(z => z.Gated);
            string raised = (await Call(box, "terrain_sculpt", new { ops = new object[] { new { kind = "raise", center = new[] { zone.Circle![0], zone.Circle[1] }, radius = 26, height = 2, falloff = 0.9 } } })).Text;
            Assert.Contains("moved onto the new", raised);
            await Call(box, "scatter_assets", new { asset_ids = new[] { FakeRoblox.AssetId }, zone = zone.Id, count = 5, seed = 3 });
            return zone;
        }

        [Fact]
        public async Task TheGeneratedWorld_WithTheBuildersOwnTouches_IsAMapTheReportAndTheRendererUnderstand()
        {
            var (h, id, box) = await SimulatorAsync();
            using var _ = h;
            var raisedZone = await DecorateAsync(h, id, box);

            var scene = h.Ctx.Workspace(id).LoadScene();
            var zones = scene.Layout.Zones.Where(z => z.Id != "hub").OrderBy(z => z.Order).ToList();
            Assert.True(zones.Count >= 3, "zones: " + zones.Count);
            string summary = (await Call(box, "scene_summary", new { limit = 10 })).Text;
            Assert.Contains(zones[0].Name, summary);

            string build = (await Call(box, "build", new { })).Text;
            Assert.Contains("BUILD OK — revision 1", build);
            var game = h.Game(id);
            Assert.True(game.Build.LastBuildOk, string.Join("\n", game.Build.Errors));
            Assert.True(game.Build.MapScore >= StageGates.MinMapScore, $"map score {game.Build.MapScore}: " + string.Join(" | ", game.Build.MapFindings));
            Assert.DoesNotContain(game.Build.MapFindings, f => f.StartsWith("[error]", StringComparison.Ordinal));
            Assert.False(game.Build.MapFindings.Any(f => f.Contains("floats") || f.Contains("is sunk")), string.Join("\n", game.Build.MapFindings));
            Assert.False(string.IsNullOrEmpty(game.Build.Economy), "a simulator's build plays its economy");
            Assert.Empty(game.Build.EconomyFindings);

            // A rebuild of unchanged files is the same revision (so a passing engine run on it still counts).
            string again = (await Call(box, "build", new { })).Text;
            Assert.Contains("revision 1 (no file changed", again);
            Assert.Equal(1, h.Game(id).Build.Revision);

            var report = await Call(box, "map_report", new { });
            Assert.Contains("Map score", report.Text);
            Assert.Contains($"zone {raisedZone.Id}", report.Text);
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
        public async Task HandEditsOfWhatTheGeneratorOwns_AreRefused_WithTheWayToMakeTheChange()
        {
            var (h, id, box) = await SimulatorAsync();
            using var _ = h;
            var before = JsonConvert.SerializeObject(h.Ctx.Workspace(id).LoadScene());
            foreach (var (tool, args) in new (string, object)[]
            {
                ("terrain_generate", new { hills = 30, flat_radius = 40 }),
                ("set_map_area", new { area = new[] { -50, -50, 50, 50 } }),
                ("plan_map", new { concept = "one long corridor" }),
            })
            {
                var refused = await box.ExecuteAsync(tool, JsonConvert.SerializeObject(args), CancellationToken.None);
                Assert.True(refused.IsError, tool + " was allowed");
                Assert.Contains("design_world", refused.Text);
                Assert.Contains("terrain_sculpt", refused.Text);
            }
            Assert.Equal(before, JsonConvert.SerializeObject(h.Ctx.Workspace(id).LoadScene()));

            // The template decides the layout.
            var wrong = await box.ExecuteAsync("design_world", JsonConvert.SerializeObject(new { spec = new { layout = "course" } }), CancellationToken.None);
            Assert.True(wrong.IsError);
            Assert.Contains("layout 'hub'", wrong.Text);
        }

        [Fact]
        public async Task AnEmptyWorld_IsCalledOutByTheMapReport_AndCannotBeDeclaredComplete()
        {
            var (h, id, box) = await SimulatorAsync();
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
            var (h, id, box) = await SimulatorAsync();
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
