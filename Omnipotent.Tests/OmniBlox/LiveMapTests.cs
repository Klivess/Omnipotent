using Omnipotent.Services.OmniBlox.Assets;
using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Build.WorldGen;
using Omnipotent.Services.OmniBlox.Core;
using Xunit.Abstractions;

namespace Omnipotent.Tests.OmniBlox
{
    /// <summary>
    /// Real maps from real models: with OMNIBLOX_LIVE_DIR pointing at an export of the live studio (see
    /// <see cref="LiveLibrary"/>), generates a simulator, an obby and a tycoon, compiles each into a place, checks
    /// it, and (with OMNIBLOX_RENDER_OUT) writes every view and the .rbxlx to open in Roblox Studio. Skipped
    /// without the export.
    /// </summary>
    public class LiveMapTests
    {
        private readonly ITestOutputHelper output;
        public LiveMapTests(ITestOutputHelper output) => this.output = output;

        /// <summary>
        /// The live studio's "Chop the Forest" map — a flat 140×398 strip of grass floating in the sky with a few
        /// bushes, which the old report scored 100/100 — must now be blocked and score low.
        /// </summary>
        [Fact]
        public void TheLiveChopMap_IsBlockedAndScoresLow()
        {
            string? live = Environment.GetEnvironmentVariable("OMNIBLOX_LIVE_DIR");
            if (string.IsNullOrEmpty(live) || !File.Exists(Path.Combine(live, "chop_r28_scene.json"))) return;
            using var temp = new TempDir();
            var library = LiveLibrary.Import(live, Path.Combine(temp.Path, "assets"));
            var catalog = AssetCatalog.FromRecords(library.Recent(AssetStatus.Labeled, 200));
            var ws = new GameWorkspace(Path.Combine(temp.Path, "ws"), Path.Combine(temp.Path, "snap"));
            var manifest = new GameManifest { Title = "Chop the Forest", Template = "sandbox" };
            ws.EnsureCreated(manifest);
            File.Copy(Path.Combine(live, "chop_r28_scene.json"), ws.ScenePath, overwrite: true);
            var scene = ws.LoadScene();
            var compiled = PlaceCompiler.Compile(new CompileInput { GameId = "chop", Workspace = ws, LoadAsset = library.LoadModel, AssetInfo = id => Info(library, id) });
            Assert.True(compiled.Ok, string.Join("\n", compiled.Errors));
            var map = MapReport.Analyze(scene, compiled.Parts, compiled.Terrain, manifest, catalog: catalog.Get);
            output.WriteLine(map.Describe());
            Assert.True(map.Blocked);
            Assert.True(map.Score <= MapReport.BlockedCap, "scored " + map.Score);
            Assert.Contains(map.Findings, f => f.IsError && f.Area == "ground");
            Assert.Contains(map.Findings, f => f.IsError && f.Area == "shape");
        }

        private static AssetPlacement? Info(AssetLibrary library, long id)
        {
            var rec = library.Get(id);
            if (rec?.Measurements is not { } m || !rec.Placeable) return null;
            return new AssetPlacement(new Omnipotent.Services.OmniBlox.Rbx.RbxVector3(m.Pivot[0], m.Pivot[1], m.Pivot[2]), rec.Label?.FrontYaw ?? 0, new Omnipotent.Services.OmniBlox.Rbx.RbxVector3(m.SizeX, m.SizeY, m.SizeZ), rec.Name);
        }

        public static IEnumerable<object[]> Briefs() => new[]
        {
            new object[] { "baseline-sim", new GameBrief
            {
                Title = "Timber Island", Genre = "simulator", Template = "simulator", Pitch = "Chop trees, sell logs, unlock new forests.",
                CoreLoop = "Chop trees for logs, sell them, buy better axes, unlock the next forest.",
                MustHave = { "Four zones: Maple Grove, Pine Hills, Frost Woods, Ember Wilds", "Axe upgrades", "A rebirth" }, DoneWhen = { "A new player chops a tree within 30 seconds", "Every zone can be unlocked" },
            } },
            new object[] { "baseline-obby", new GameBrief
            {
                Title = "Lava Forest Obby", Genre = "obby", Template = "obby", Pitch = "Leap over glowing lava through a pine forest.",
                CoreLoop = "Jump from platform to platform over lava, touching each checkpoint to save progress.",
                MustHave = { "24 stages with checkpoints", "A lava floor that resets players", "A finish with a reward" }, DoneWhen = { "Every stage is reachable", "The finish pays out" },
            } },
            new object[] { "baseline-tycoon", new GameBrief
            {
                Title = "Candy Factory Tycoon", Genre = "tycoon", Template = "tycoon", Pitch = "Build the sweetest factory on the server.",
                CoreLoop = "Buy droppers that make candy, collect the cash, buy upgrades.",
                MustHave = { "A plot per player", "Twelve buttons in price order", "A leaderboard" }, DoneWhen = { "A first dropper within a minute", "A plot can be bought out" },
            } },
        };

        /// <summary>
        /// The studio's own baseline for a brief — exactly what a game has when it reaches Build (template settings,
        /// the generated world from real models, a balanced economy, the tutorial) — compiled into the place file
        /// that is published, written out to open in Roblox Studio and play.
        /// </summary>
        [Theory]
        [MemberData(nameof(Briefs))]
        public void TheStudiosBaseline_IsACompletePlaceToPlay(string name, GameBrief brief)
        {
            string? live = Environment.GetEnvironmentVariable("OMNIBLOX_LIVE_DIR");
            if (string.IsNullOrEmpty(live) || !Directory.Exists(live)) return;
            string outDir = Environment.GetEnvironmentVariable("OMNIBLOX_RENDER_OUT") is { Length: > 0 } o ? o : Path.Combine(Path.GetTempPath(), "omniblox-live-maps");
            Directory.CreateDirectory(outDir);
            using var h = new StudioHarness(apiKey: false);
            LiveLibrary.ImportInto(h.Ctx.Library, live);
            string id = h.Orchestrator.StartGame();
            h.Ctx.Update(id, g => g.Brief = brief);
            h.Orchestrator.Enter(id, GameStage.Build);

            var ws = h.Ctx.Workspace(id);
            var scene = ws.LoadScene();
            var manifest = ws.LoadManifest();
            Assert.NotNull(scene.Spec);
            var compiled = h.Ctx.Compile(id);
            Assert.True(compiled.Ok, string.Join("\n", compiled.Errors));
            var geometry = GeometryVerifier.Verify(compiled.Parts, scene, manifest, compiled.Terrain, h.Ctx.Catalog().Get);
            var map = MapReport.Analyze(scene, compiled.Parts, compiled.Terrain, manifest, geometry, h.Ctx.Catalog().Get);
            output.WriteLine(map.Describe());
            output.WriteLine(ws.ReadFile("design.md"));
            File.WriteAllBytes(Path.Combine(outDir, name + ".rbxlx"), compiled.PlaceXml);
            foreach (string view in new[] { "spawn", "aerial" })
                File.WriteAllBytes(Path.Combine(outDir, $"{name}_{view}.png"), h.Ctx.RenderCompiled(id, compiled, view, 1280, 720));
            Assert.DoesNotContain(geometry, g => g.IsError);
            Assert.False(map.Blocked, map.Describe());
            Assert.True(map.Score >= MapReport.Bar, map.Describe());
            if (manifest.Template == "simulator")
            {
                var economy = Economy.Simulate(SimulatorEconomy.FromGame(manifest, scene));
                output.WriteLine(economy.Describe());
                Assert.True(economy.Ok, economy.Describe());
            }
        }

        public static IEnumerable<object[]> Specs() => new[]
        {
            new object[] { "forest-sim", "simulator" },
            new object[] { "obby", "obby" },
            new object[] { "tycoon", "tycoon" },
        };

        [Theory]
        [MemberData(nameof(Specs))]
        public void GeneratesAndRendersRealMaps(string name, string template)
        {
            string? live = Environment.GetEnvironmentVariable("OMNIBLOX_LIVE_DIR");
            if (string.IsNullOrEmpty(live) || !Directory.Exists(live)) return;
            string outDir = Environment.GetEnvironmentVariable("OMNIBLOX_RENDER_OUT") is { Length: > 0 } o ? o : Path.Combine(Path.GetTempPath(), "omniblox-live-maps");
            Directory.CreateDirectory(outDir);
            using var temp = new TempDir();
            var library = LiveLibrary.Import(live, Path.Combine(temp.Path, "assets"), s => output.WriteLine(s));
            var catalog = AssetCatalog.FromRecords(library.Recent(AssetStatus.Labeled, 200));
            output.WriteLine($"catalog: {catalog.Entries.Count} entries: " + string.Join(", ", catalog.Entries.GroupBy(e => e.Role).Select(g => $"{g.Key} {g.Count()}")));

            var manifest = new GameManifest { Title = name, Template = template, Rebirth = template == "simulator" ? new RebirthSpec() : null };
            var spec = template switch
            {
                "obby" => new WorldSpec { Layout = "course", Biome = "meadow", Seed = 3, Course = new CourseSpec { Stages = 24, Themes = { "meadow", "candy", "ice", "lava" } } },
                "tycoon" => new WorldSpec { Layout = "plots", Biome = "meadow", Seed = 5, Plots = new PlotsSpec { Count = 4, Theme = "lumber" } },
                _ => new WorldSpec { Layout = "hub", Biome = "meadow", Seed = 11, Hub = new HubSpec { Name = "Lumber Camp" }, Zones = WorldGenerator.DefaultZones("meadow", "tree") },
            };
            var result = WorldGenerator.Generate(spec, manifest, catalog);
            output.WriteLine(result.Describe());

            var ws = new GameWorkspace(Path.Combine(temp.Path, "ws"), Path.Combine(temp.Path, "snap"));
            ws.EnsureCreated(manifest);
            ws.SaveScene(result.Scene);
            AssetPlacement? Info(long id)
            {
                var rec = library.Get(id);
                if (rec?.Measurements is not { } m || !rec.Placeable) return null;
                return new AssetPlacement(new Omnipotent.Services.OmniBlox.Rbx.RbxVector3(m.Pivot[0], m.Pivot[1], m.Pivot[2]), rec.Label?.FrontYaw ?? 0, new Omnipotent.Services.OmniBlox.Rbx.RbxVector3(m.SizeX, m.SizeY, m.SizeZ), rec.Name);
            }
            var compiled = PlaceCompiler.Compile(new CompileInput { GameId = name, Workspace = ws, LoadAsset = library.LoadModel, AssetInfo = Info });
            Assert.True(compiled.Ok, string.Join("\n", compiled.Errors));
            var geometry = GeometryVerifier.Verify(compiled.Parts, result.Scene, manifest, compiled.Terrain, catalog.Get);
            foreach (var g in geometry.Take(40)) output.WriteLine("geometry " + g);
            var map = MapReport.Analyze(result.Scene, compiled.Parts, compiled.Terrain, manifest, geometry, catalog.Get);
            output.WriteLine(map.Describe());
            File.WriteAllBytes(Path.Combine(outDir, name + ".rbxlx"), compiled.PlaceXml);

            SpriteImage? Sprite(long id)
            {
                string p = library.ThumbnailPath(id);
                return File.Exists(p) ? SpriteImage.Decode(File.ReadAllBytes(p)) : null;
            }
            var world = new RenderWorld { Parts = compiled.Parts, Scene = result.Scene, Terrain = compiled.Terrain, Sprite = Sprite };
            var views = new List<string> { "map", "aerial", "spawn", "iso" };
            views.AddRange(result.Scene.Layout.Zones.Where(z => z.Id != "hub").Take(2).Select(z => "zone:" + z.Id));
            foreach (string view in views)
                File.WriteAllBytes(Path.Combine(outDir, $"{name}_{view.Replace(':', '_')}.png"), WorldRenderer.Render(world, new RenderRequest { View = view, Width = 1280, Height = 720 }));
            if (template == "simulator")
            {
                var economy = SimulatorEconomy.FromGame(manifest, result.Scene);
                output.WriteLine("ECONOMY (defaults):\n" + Economy.Simulate(economy).Describe());
                Economy.Balance(economy);
                output.WriteLine("prices: zones " + string.Join(", ", economy.Zones.Select(z => $"{z.Name} {z.Price} (hp {z.Hp}, value {z.Value}, {z.Distance:0} away)")) + "; tools " + string.Join(", ", economy.Tools.Select(t => t.Price)) + "; bag " + string.Join(", ", economy.Capacity!.Costs) + "; speed " + string.Join(", ", economy.Speed!.Costs) + "; rebirth " + economy.RebirthCost);
                var balanced = Economy.Simulate(economy);
                output.WriteLine("ECONOMY (balanced):\n" + balanced.Describe());
                Assert.True(balanced.Ok, balanced.Describe());
            }
            Assert.DoesNotContain(geometry, g => g.IsError);
            Assert.False(map.Blocked, map.Describe());
            Assert.True(map.Score >= MapReport.Bar, map.Describe());
        }
    }
}
