using Omnipotent.Services.Stratum;

namespace Omnipotent.Tests.Stratum
{
    public class StratumVerifierTests
    {
        [Fact]
        public void ParseReport_ReadsCannedVerifyLine()
        {
            string stdout = "noise\r\nSTRATUM_BBOX:100.000,80.000,30.000\n" +
                "STRATUM_VERIFY:{\"valid\":true,\"solids\":1,\"watertight\":true,\"volumeMm3\":1234.5," +
                "\"bbox\":{\"dx\":100.0,\"dy\":80.0,\"dz\":30.0,\"xmin\":-50,\"xmax\":50,\"ymin\":-40,\"ymax\":40,\"zmin\":0,\"zmax\":30}," +
                "\"features\":[{\"id\":\"bc_A1_clear\",\"kind\":\"pilot-hole\",\"severity\":\"hard\",\"pass\":true,\"measuredMm3\":0.0,\"frac\":0.0,\"desc\":\"M3 pilot\"}]," +
                "\"failures\":[],\"warnings\":[]}\nmore noise";
            var report = StratumGeometryVerifier.ParseReport(stdout);
            Assert.NotNull(report);
            Assert.True(report!.Valid);
            Assert.True(report.Watertight);
            Assert.Equal(1234.5, report.VolumeMm3, 3);
            Assert.NotNull(report.Bbox);
            Assert.Equal(100.0, report.Bbox!.Dx, 3);
            Assert.Single(report.Features);
            Assert.True(report.GeometryPassed);
        }

        [Fact]
        public void ParseReport_FailuresBlockGeometryPassed()
        {
            string stdout = "STRATUM_VERIFY:{\"valid\":true,\"solids\":1,\"watertight\":true,\"volumeMm3\":10," +
                "\"bbox\":null,\"features\":[],\"failures\":[\"feature x missing\"],\"warnings\":[]}";
            var report = StratumGeometryVerifier.ParseReport(stdout);
            Assert.NotNull(report);
            Assert.False(report!.GeometryPassed);
        }

        [Fact]
        public void CheckBBox_WithinThreePercent_Passes()
        {
            var slot = new MechanicalBlueprintSlot { BoundingBoxMm = new[] { 100.0, 80, 30 }, PrincipalAxis = "+X" };
            var ok = StratumGeometryVerifier.CheckBBox(slot, new VerifyBBox { Dx = 102.5, Dy = 80, Dz = 30 });
            Assert.Null(ok);
        }

        [Fact]
        public void CheckBBox_Oversize_FailsWithAxisDetail()
        {
            var slot = new MechanicalBlueprintSlot { BoundingBoxMm = new[] { 100.0, 80, 30 }, PrincipalAxis = "+X" };
            var fail = StratumGeometryVerifier.CheckBBox(slot, new VerifyBBox { Dx = 120, Dy = 80, Dz = 30 });
            Assert.NotNull(fail);
            Assert.Contains("X", fail);
            Assert.Contains("principalAxis", fail);
        }

        [Fact]
        public void CheckBBox_ProtrusionAllowance_AppliesWithIntegrationFeatures()
        {
            var slot = new MechanicalBlueprintSlot
            {
                BoundingBoxMm = new[] { 100.0, 80, 30 },
                PrincipalAxis = "+X",
                IntegrationFeatures = new List<MechanicalIntegrationFeature> { new() { FeatureKind = "boss" } },
            };
            // 108 mm would fail bare 3% (103.2 + 0.2) but passes with the 10 mm protrusion allowance.
            Assert.Null(StratumGeometryVerifier.CheckBBox(slot, new VerifyBBox { Dx = 108, Dy = 80, Dz = 30 }));
        }

        [Fact]
        public void AssemblePartScript_LayersPreludeHelperScriptFooter()
        {
            var registry = StratumDimensionRegistry.CreateDefault();
            var slot = new MechanicalBlueprintSlot
            {
                SubtaskTitle = "Lid",
                BoundingBoxMm = new[] { 100.0, 80, 5 },
                PrincipalAxis = "+X",
                RequiredFeatures = new List<RequiredFeature>
                {
                    new()
                    {
                        FeatureId = "bc_B1", ContractId = "bc", FeatureKind = "thru-hole",
                        LocalPositionMm = new[] { 21.2, 21.2, 0.0 }, LocalAxisDir = new[] { 0.0, 0, 1 },
                        DiaMm = 3.4, DepthMm = 0, Spec = "M3 clearance",
                    },
                },
            };
            string assembled = StratumGeometryVerifier.AssemblePartScript("import cadquery as cq\nresult = cq.Workplane('XY').box(100, 80, 5)", slot, registry);

            int prelude = assembled.IndexOf("Stratum dimension registry", StringComparison.Ordinal);
            int helper = assembled.IndexOf("def stratum_apply_contract_features", StringComparison.Ordinal);
            int user = assembled.IndexOf("result = cq.Workplane", StringComparison.Ordinal);
            int footer = assembled.IndexOf("STRATUM_VERIFY", StringComparison.Ordinal);
            Assert.True(prelude >= 0 && helper > prelude && user > helper && footer > user,
                $"layering wrong: prelude={prelude}, helper={helper}, user={user}, footer={footer}");
            Assert.Contains("WALL_THICKNESS", assembled);
            // The hole gets both a bore-clear probe and a material-ring probe.
            Assert.Contains("bc_B1_clear", assembled);
            Assert.Contains("bc_B1_ring", assembled);
        }

        [Fact]
        public void BuildRepairPrompt_ListsHardFailures()
        {
            var report = new StratumVerificationReport
            {
                Valid = true,
                Watertight = true,
                Features = new List<VerifyFeatureResult>
                {
                    new() { Id = "bc_A1_clear", Kind = "pilot-hole", Severity = "hard", Pass = false, MeasuredMm3 = 17.2, Frac = 0.93, Desc = "M3 pilot hole" },
                },
                Failures = new List<string> { "feature bc_A1_clear failed" },
            };
            string prompt = StratumGeometryVerifier.BuildRepairPrompt(report, "bbox exceeded on X");
            Assert.Contains("bc_A1_clear", prompt);
            Assert.Contains("bbox exceeded on X", prompt);
            Assert.Contains("stratum_apply_contract_features", prompt);
        }
    }

    public class StratumCollisionTests
    {
        [Fact]
        public void ParseCollisionReport_ReadsCannedLines()
        {
            string stdout =
                "STRATUM_COLLISION:{\"a\":\"Base_1\",\"b\":\"Lid_1\",\"aTask\":\"Base\",\"bTask\":\"Lid\",\"method\":\"occ\",\"intersectionMm3\":0.0,\"minClearanceMm\":0.0}\n" +
                "STRATUM_COLLISION:{\"a\":\"Base_1\",\"b\":\"Sensor_1\",\"aTask\":\"Base\",\"bTask\":\"Sensor\",\"method\":\"aabb\",\"intersectionMm3\":0.0,\"minClearanceMm\":12.5}\n" +
                "garbage line";
            var pairs = StratumGeometryOps.ParseCollisionReport(stdout);
            Assert.Equal(2, pairs.Count);
            Assert.Equal("Base", pairs[0].ATask);
            Assert.Equal(12.5, pairs[1].MinClearanceMm, 3);
        }

        private static MechanicalBlueprint ContractedBlueprint() => new()
        {
            Slots = new List<MechanicalBlueprintSlot>
            {
                new() { SubtaskTitle = "Base" }, new() { SubtaskTitle = "Lid" }, new() { SubtaskTitle = "Sensor" },
            },
            Contracts = new List<AssemblyContract>
            {
                new() { ContractId = "bc", Kind = StratumContractKinds.BoltCircle, PartA = "Base", PartB = "Lid" },
            },
        };

        [Fact]
        public void EvaluateCollisions_ContractPair_MayTouch()
        {
            var pairs = new List<StratumCollisionPair>
            {
                new() { A = "Base_1", B = "Lid_1", ATask = "Base", BTask = "Lid", Method = "occ", IntersectionMm3 = 0.2, MinClearanceMm = 0.0 },
            };
            var failures = StratumGeometryOps.EvaluateCollisions(pairs, ContractedBlueprint(), StratumDimensionRegistry.CreateDefault());
            Assert.Empty(failures);
        }

        [Fact]
        public void EvaluateCollisions_ContractPair_StillFailsOnInterpenetration()
        {
            var pairs = new List<StratumCollisionPair>
            {
                new() { A = "Base_1", B = "Lid_1", ATask = "Base", BTask = "Lid", Method = "occ", IntersectionMm3 = 480.0, MinClearanceMm = 0.0 },
            };
            var failures = StratumGeometryOps.EvaluateCollisions(pairs, ContractedBlueprint(), StratumDimensionRegistry.CreateDefault());
            Assert.Single(failures);
            Assert.Contains("INTERPENETRATE", failures[0]);
        }

        [Fact]
        public void EvaluateCollisions_NonContractPair_FailsOnTightClearance()
        {
            var pairs = new List<StratumCollisionPair>
            {
                new() { A = "Base_1", B = "Sensor_1", ATask = "Base", BTask = "Sensor", Method = "occ", IntersectionMm3 = 0.0, MinClearanceMm = 0.2 },
            };
            var failures = StratumGeometryOps.EvaluateCollisions(pairs, ContractedBlueprint(), StratumDimensionRegistry.CreateDefault());
            Assert.Single(failures);
            Assert.Contains("0.2", failures[0]);
        }

        [Fact]
        public void EvaluateCollisions_NonContractPair_CleanWhenClear()
        {
            var pairs = new List<StratumCollisionPair>
            {
                new() { A = "Base_1", B = "Sensor_1", ATask = "Base", BTask = "Sensor", Method = "occ", IntersectionMm3 = 0.0, MinClearanceMm = 4.0 },
            };
            var failures = StratumGeometryOps.EvaluateCollisions(pairs, ContractedBlueprint(), StratumDimensionRegistry.CreateDefault());
            Assert.Empty(failures);
        }
    }
}
