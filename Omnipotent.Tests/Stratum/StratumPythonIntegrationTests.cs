using Omnipotent.Services.Stratum;

namespace Omnipotent.Tests.Stratum
{
    /// <summary>
    /// Real CadQuery/OCC integration tests. Gated behind STRATUM_PY_TESTS=1 because the first
    /// run bootstraps a Python venv + CadQuery (several minutes, ~1 GB). Run with:
    ///   $env:STRATUM_PY_TESTS='1'; dotnet test --filter Category=PythonIntegration
    /// </summary>
    [Trait("Category", "PythonIntegration")]
    public class StratumPythonIntegrationTests
    {
        private static bool Enabled => Environment.GetEnvironmentVariable("STRATUM_PY_TESTS") == "1";
        private static readonly TimeSpan ScriptTimeout = TimeSpan.FromMinutes(3);

        private static async Task<StratumPythonRunner> RunnerAsync()
        {
            var runner = new StratumPythonRunner();
            if (!runner.Status().cadqueryInstalled)
                await runner.EnsureBootstrappedAsync(_ => { }, CancellationToken.None);
            return runner;
        }

        private static string NewWorkDir(string label)
        {
            string dir = Path.Combine(Path.GetTempPath(), "stratum_pytests", $"{label}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public async Task Verifier_PlateWithContractHole_PassesProbes()
        {
            if (!Enabled) return;
            var runner = await RunnerAsync();
            var registry = StratumDimensionRegistry.CreateDefault();
            var slot = new MechanicalBlueprintSlot
            {
                SubtaskTitle = "Plate",
                BoundingBoxMm = new[] { 60.0, 40, 4 },
                PrincipalAxis = "+X",
                LocalOrigin = "geometric centre",
                RequiredFeatures = new List<RequiredFeature>
                {
                    new()
                    {
                        FeatureId = "bc_B1", ContractId = "bc", FeatureKind = "thru-hole",
                        LocalPositionMm = new[] { 20.0, 10.0, 0.0 }, LocalAxisDir = new[] { 0.0, 0, 1 },
                        DiaMm = 3.4, DepthMm = 0, Spec = "M3 clearance thru-hole",
                    },
                },
            };
            string script = "import cadquery as cq\nbody = cq.Workplane('XY').box(60, 40, 4)\nresult = stratum_apply_contract_features(body)";
            var result = await runner.RunScriptAsync(
                StratumGeometryVerifier.AssemblePartScript(script, slot, registry),
                NewWorkDir("verify_ok"), ScriptTimeout, _ => { }, CancellationToken.None);

            Assert.True(result.Success, $"script failed: {result.Stderr}");
            var report = StratumGeometryVerifier.ParseReport(result.Stdout);
            Assert.NotNull(report);
            Assert.True(report!.Valid);
            Assert.True(report.Watertight);
            Assert.True(report.GeometryPassed, "probes should pass: " + string.Join("; ", report.Failures));
            Assert.Null(StratumGeometryVerifier.CheckBBox(slot, report.Bbox));
        }

        [Fact]
        public async Task Verifier_HoleOutsideMaterial_FailsRingProbe()
        {
            if (!Enabled) return;
            var runner = await RunnerAsync();
            var registry = StratumDimensionRegistry.CreateDefault();
            // The hole is expected at x=100 — entirely outside the 60mm plate. The bore-clear
            // probe trivially passes (no material) but the material-ring probe must fail.
            var slot = new MechanicalBlueprintSlot
            {
                SubtaskTitle = "Plate",
                BoundingBoxMm = new[] { 60.0, 40, 4 },
                PrincipalAxis = "+X",
                RequiredFeatures = new List<RequiredFeature>
                {
                    new()
                    {
                        FeatureId = "bc_B1", ContractId = "bc", FeatureKind = "thru-hole",
                        LocalPositionMm = new[] { 100.0, 0.0, 0.0 }, LocalAxisDir = new[] { 0.0, 0, 1 },
                        DiaMm = 3.4, DepthMm = 0, Spec = "hole positioned outside the part",
                    },
                },
            };
            string script = "import cadquery as cq\nbody = cq.Workplane('XY').box(60, 40, 4)\nresult = body";
            var result = await runner.RunScriptAsync(
                StratumGeometryVerifier.AssemblePartScript(script, slot, registry),
                NewWorkDir("verify_fail"), ScriptTimeout, _ => { }, CancellationToken.None);

            Assert.True(result.Success, $"script failed: {result.Stderr}");
            var report = StratumGeometryVerifier.ParseReport(result.Stdout);
            Assert.NotNull(report);
            Assert.False(report!.GeometryPassed, "the ring probe must catch the missing material around the hole");
            Assert.Contains(report.Features, f => f.Id == "bc_B1_ring" && !f.Pass);
        }

        [Fact]
        public async Task Collision_OverlappingBoxes_ReportIntersectionVolume()
        {
            if (!Enabled) return;
            var runner = await RunnerAsync();
            string workDir = NewWorkDir("collide");

            // Produce one 20mm cube STEP, reuse it for both parts.
            var mk = await runner.RunScriptAsync(
                StratumGeometryVerifier.AssemblePartScript(
                    "import cadquery as cq\nresult = cq.Workplane('XY').box(20, 20, 20)", null, StratumDimensionRegistry.CreateDefault()),
                workDir, ScriptTimeout, _ => { }, CancellationToken.None);
            Assert.True(mk.Success, mk.Stderr);
            var step = mk.ProducedFiles.First(f => f.Extension.Equals(".step", StringComparison.OrdinalIgnoreCase));
            File.Copy(step.FullName, Path.Combine(workDir, "candidate.step"), true);
            File.Copy(step.FullName, Path.Combine(workDir, "neighbor_000.step"), true);

            // Neighbour offset 15mm on X → 5mm overlap → 5×20×20 = 2000 mm³ intersection.
            var candidate = new StratumCompositionEntry
            {
                StagedFileName = "candidate.step",
                SubtaskTitle = "A",
                Slot = new MechanicalBlueprintSlot { SubtaskTitle = "A", WorldPosition = new[] { 0.0, 0, 0 } },
            };
            var neighbor = new StratumCompositionEntry
            {
                StagedFileName = "neighbor_000.step",
                SubtaskTitle = "B",
                Slot = new MechanicalBlueprintSlot { SubtaskTitle = "B", WorldPosition = new[] { 15.0, 0, 0 } },
            };
            var result = await runner.RunScriptAsync(
                StratumGeometryOps.BuildCollisionScript(candidate, new List<StratumCompositionEntry> { neighbor }),
                workDir, ScriptTimeout, _ => { }, CancellationToken.None);
            Assert.True(result.Success, result.Stderr);

            var pairs = StratumGeometryOps.ParseCollisionReport(result.Stdout);
            var pair = Assert.Single(pairs);
            Assert.Equal("occ", pair.Method);
            Assert.InRange(pair.IntersectionMm3, 1900, 2100);
        }

        [Fact]
        public async Task Render_ProducesAFourViewPng()
        {
            if (!Enabled) return;
            var runner = await RunnerAsync();
            string workDir = NewWorkDir("render");

            var mk = await runner.RunScriptAsync(
                StratumGeometryVerifier.AssemblePartScript(
                    "import cadquery as cq\nresult = cq.Workplane('XY').box(40, 25, 10).faces('>Z').workplane().hole(6)",
                    null, StratumDimensionRegistry.CreateDefault()),
                workDir, ScriptTimeout, _ => { }, CancellationToken.None);
            Assert.True(mk.Success, mk.Stderr);
            var step = mk.ProducedFiles.First(f => f.Extension.Equals(".step", StringComparison.OrdinalIgnoreCase));
            File.Copy(step.FullName, Path.Combine(workDir, "part.step"), true);

            var entry = new StratumCompositionEntry { StagedFileName = "part.step", SubtaskTitle = "Part", Slot = null };
            var result = await runner.RunScriptAsync(
                StratumGeometryOps.BuildRenderScript(new List<StratumCompositionEntry> { entry }, "render.png", "test part"),
                workDir, ScriptTimeout, _ => { }, CancellationToken.None);
            Assert.True(result.Success, result.Stderr);
            var png = result.ProducedFiles.FirstOrDefault(f => f.Name == "render.png");
            Assert.NotNull(png);
            Assert.True(png!.Length > 10_000, $"render too small ({png.Length} B) to plausibly contain 4 shaded views");
        }
    }
}
