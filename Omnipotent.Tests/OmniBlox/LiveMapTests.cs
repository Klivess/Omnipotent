using Omnipotent.Services.OmniBlox.Assets;
using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Build.WorldGen;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Pipeline;
using Xunit.Abstractions;

namespace Omnipotent.Tests.OmniBlox
{
    /// <summary>
    /// Real maps from real models: with OMNIBLOX_LIVE_DIR pointing at an export of the live studio (see
    /// <see cref="LiveLibrary"/>), builds a map with the builder's own map tools, compiles it into a place, checks
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
            var manifest = new GameManifest { Title = "Chop the Forest" };
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

        /// <summary>Briefs of new games (no template: each runs its own loop in its own world).</summary>
        public static IEnumerable<object[]> Briefs() => new[]
        {
            new object[] { "crab-cove", new GameBrief
            {
                Title = "Crab Cove", Genre = "collecting adventure", Pitch = "Catch crabs round a forested island and climb to the crystal on the cliff.",
                CoreLoop = "Catch crabs on the beach, sell them at the docks, buy a better net, unlock the old grove.",
                World = "One island in a blue sea: docks, a crab beach, an old grove behind a gate and a crystal cliff up a ramp.",
                MustHave = { "Four places on one island", "Nets", "A crystal on the cliff" }, DoneWhen = { "A new player catches a crab within 30 seconds", "The grove can be unlocked" },
            } },
            new object[] { "canyon-run", new GameBrief
            {
                Title = "Canyon Run", Genre = "parkour", Pitch = "Race along the ledges of a winding red canyon.",
                CoreLoop = "Run and jump from ledge to ledge, touching each camp to save progress, and reach the arch at the end.",
                World = "A winding desert canyon cut into a high plateau: camps on ledges along its walls, rope bridges across it, a stone arch at its far end.",
                MustHave = { "Twelve camps", "Rope bridges", "A finish arch" }, DoneWhen = { "Every camp can be reached", "The finish pays out" },
            } },
        };

        /// <summary>
        /// The map the builder's tools make (<see cref="WorldToolTests.MapSteps"/>) from the live studio's real models,
        /// compiled into the place file that is published and written out to open in Roblox Studio and play.
        /// </summary>
        [Fact]
        public async Task AMapFromTheMapTools_WithRealModels_IsACompletePlaceToPlay()
        {
            string? live = Environment.GetEnvironmentVariable("OMNIBLOX_LIVE_DIR");
            if (string.IsNullOrEmpty(live) || !Directory.Exists(live)) return;
            string outDir = Environment.GetEnvironmentVariable("OMNIBLOX_RENDER_OUT") is { Length: > 0 } o ? o : Path.Combine(Path.GetTempPath(), "omniblox-live-maps");
            Directory.CreateDirectory(outDir);
            using var h = new StudioHarness(apiKey: false);
            LiveLibrary.ImportInto(h.Ctx.Library, live);
            string id = h.Orchestrator.StartGame();
            h.Ctx.Update(id, g => g.Brief = (GameBrief)Briefs().First()[1]);
            h.Orchestrator.Enter(id, GameStage.Build);
            var box = Services.OmniBlox.Pipeline.Tools.BuildTools.Create(h.Ctx, id);
            foreach (var (tool, args) in WorldToolTests.MapSteps())
            {
                // Real models: a role the live library lacks is not this test's failure.
                var result = await box.ExecuteAsync(tool, Newtonsoft.Json.JsonConvert.SerializeObject(args), CancellationToken.None);
                output.WriteLine($"{tool}: {(result.Text.Length > 300 ? result.Text[..300] : result.Text)}");
            }

            var ws = h.Ctx.Workspace(id);
            var scene = ws.LoadScene();
            var manifest = ws.LoadManifest();
            var compiled = h.Ctx.Compile(id);
            Assert.True(compiled.Ok, string.Join("\n", compiled.Errors));
            var geometry = GeometryVerifier.Verify(compiled.Parts, scene, manifest, compiled.Terrain, h.Ctx.Catalog().Get);
            var map = MapReport.Analyze(scene, compiled.Parts, compiled.Terrain, manifest, geometry, h.Ctx.Catalog().Get);
            output.WriteLine(map.Describe());
            File.WriteAllBytes(Path.Combine(outDir, "crab-cove.rbxlx"), compiled.PlaceXml);
            foreach (string view in new[] { "spawn", "aerial", "map", "iso" })
                File.WriteAllBytes(Path.Combine(outDir, $"crab-cove_{view}.png"), h.Ctx.RenderCompiled(id, compiled, view, 1280, 720));
            Assert.DoesNotContain(geometry, g => g.IsError);
            Assert.False(map.Blocked, map.Describe());
            Assert.True(map.Score >= MapReport.Bar, map.Describe());
        }
    }
}
