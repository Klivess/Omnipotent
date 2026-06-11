using Omnipotent.Services.Stratum;

namespace Omnipotent.Tests.Stratum
{
    /// <summary>
    /// Pins the Euler XYZ convention used by the composer + contract engine, and proves the
    /// load-bearing invariant of the contract system: features derived for BOTH participants
    /// of a contract coincide exactly when transformed back to world space.
    /// </summary>
    public class StratumSpatialMathTests
    {
        private const double Eps = 1e-9;

        [Fact]
        public void RotateEulerXyz_InverseRoundTrips_CompoundRotation()
        {
            var rot = new[] { 30.0, 45.0, 60.0 };
            var v = new[] { 1.0, 2.0, 3.0 };
            var fwd = StratumSpatialMath.RotateEulerXyzDeg(v, rot);
            var back = StratumSpatialMath.RotateEulerXyzInverseDeg(fwd, rot);
            Assert.Equal(v[0], back[0], 9);
            Assert.Equal(v[1], back[1], 9);
            Assert.Equal(v[2], back[2], 9);
        }

        [Fact]
        public void NegatedForwardRotation_IsNotAValidInverse_ForCompoundRotations()
        {
            // Documents the legacy bug: applying the forward XYZ rotation with negated angles
            // is NOT the inverse for multi-axis rotations. The exact inverse must be used.
            var rot = new[] { 90.0, 90.0, 0.0 };
            var v = new[] { 1.0, 0.0, 0.0 };
            var fwd = StratumSpatialMath.RotateEulerXyzDeg(v, rot);
            var wrong = StratumSpatialMath.RotateEulerXyzDeg(fwd, new[] { -rot[0], -rot[1], -rot[2] });
            var right = StratumSpatialMath.RotateEulerXyzInverseDeg(fwd, rot);
            Assert.True(Math.Abs(right[0] - v[0]) < Eps && Math.Abs(right[1] - v[1]) < Eps && Math.Abs(right[2] - v[2]) < Eps);
            double wrongError = Math.Abs(wrong[0] - v[0]) + Math.Abs(wrong[1] - v[1]) + Math.Abs(wrong[2] - v[2]);
            Assert.True(wrongError > 0.5, $"negated-forward should NOT round-trip (error was {wrongError})");
        }

        [Fact]
        public void Pose_WorldLocalRoundTrip()
        {
            var pose = new StratumSpatialMath.Pose { PositionMm = new[] { 10.0, -5.0, 30.0 }, RotationDeg = new[] { 15.0, -25.0, 40.0 } };
            var world = new[] { 42.0, 13.0, -7.0 };
            var local = StratumSpatialMath.WorldPointToLocal(pose, world);
            var back = StratumSpatialMath.LocalPointToWorld(pose, local);
            Assert.Equal(world[0], back[0], 9);
            Assert.Equal(world[1], back[1], 9);
            Assert.Equal(world[2], back[2], 9);
        }

        [Fact]
        public void NearestPose_PicksClosestInstance()
        {
            var slot = new MechanicalBlueprintSlot
            {
                SubtaskTitle = "Leg",
                Quantity = 2,
                Instances = new List<double[]> { new[] { 100.0, 0, 0, 0, 0, 0 }, new[] { -100.0, 0, 0, 0, 0, 180 } },
            };
            var pose = StratumSpatialMath.NearestPose(slot, new[] { -90.0, 5.0, 0.0 });
            Assert.Equal(-100.0, pose.PositionMm[0], 9);
            Assert.Equal(180.0, pose.RotationDeg[2], 9);
        }
    }

    public class StratumFitTableTests
    {
        [Fact]
        public void M3_ClearanceAndPilot_AreStandard()
        {
            var m3 = StratumFitTable.GetScrew("M3");
            Assert.Equal(3.4, m3.ClearanceDiaMm, 6);
            Assert.Equal(2.5, m3.PilotDiaMm, 6);
        }

        [Theory]
        [InlineData("press", -0.10)]
        [InlineData("slide", 0.20)]
        [InlineData("free", 0.40)]
        [InlineData("unknown", 0.20)]
        public void FitAllowances(string fitClass, double expected)
        {
            Assert.Equal(expected, StratumFitTable.FitAllowanceMm(fitClass), 6);
        }
    }

    public class StratumContractEngineTests
    {
        private static (MechanicalBlueprint bp, StratumPlannerOutput plan) BoltedBoxFixture(double lidRotZ = 0)
        {
            var plan = new StratumPlannerOutput
            {
                DeviceConcept = "Bolted test box",
                MechanicalSubtasks = new List<StratumPlannerSubtask>
                {
                    new() { Title = "Base", Description = "the base" },
                    new() { Title = "Lid", Description = "the lid" },
                },
            };
            var bp = new MechanicalBlueprint
            {
                DeviceConcept = plan.DeviceConcept,
                OriginConvention = "origin at base centre, +Z up",
                AssemblyStrategy = "lid bolts onto base",
                SchemaVersion = 2,
                Slots = new List<MechanicalBlueprintSlot>
                {
                    new() { SubtaskTitle = "Base", WorldPosition = new[] { 0.0, 0, 0 }, WorldRotationDeg = new[] { 0.0, 0, 0 }, BoundingBoxMm = new[] { 100.0, 80, 30 }, PrincipalAxis = "+X", LocalOrigin = "geometric centre" },
                    new() { SubtaskTitle = "Lid", WorldPosition = new[] { 0.0, 0, 32.5 }, WorldRotationDeg = new[] { 0.0, 0, lidRotZ }, BoundingBoxMm = new[] { 100.0, 80, 5 }, PrincipalAxis = "+X", LocalOrigin = "geometric centre" },
                },
                Contracts = new List<AssemblyContract>
                {
                    new()
                    {
                        ContractId = "bc_lid",
                        Kind = StratumContractKinds.BoltCircle,
                        PartA = "Base",
                        PartB = "Lid",
                        WorldFrame = new ContractWorldFrame { OriginMm = new[] { 0.0, 0, 30 }, AxisDir = new[] { 0.0, 0, 1 }, ClockDir = new[] { 1.0, 0, 0 } },
                        BoltCircle = new BoltCircleParams { BoltCount = 4, CircleDiaMm = 60, ScrewSize = "M3", AThreaded = true, StartAngleDeg = 45 },
                    },
                },
            };
            return (bp, plan);
        }

        [Fact]
        public void BoltCircle_DerivedHoles_CoincideInWorld_EvenWithRotatedParticipant()
        {
            // The lid is rotated 90° about Z — its local hole coordinates differ from the
            // base's, but every pair must land on the same world point. This is the invariant
            // that makes bolted parts line up without the LLM ever choosing a number.
            var (bp, _) = BoltedBoxFixture(lidRotZ: 90);
            StratumContractEngine.DeriveRequiredFeatures(bp, StratumDimensionRegistry.CreateDefault());

            var baseSlot = bp.Slots[0];
            var lidSlot = bp.Slots[1];
            Assert.Equal(4, baseSlot.RequiredFeatures.Count);
            Assert.Equal(4, lidSlot.RequiredFeatures.Count);

            var basePose = StratumSpatialMath.SlotPoses(baseSlot)[0];
            var lidPose = StratumSpatialMath.SlotPoses(lidSlot)[0];
            for (int i = 1; i <= 4; i++)
            {
                var fA = baseSlot.RequiredFeatures.Single(f => f.FeatureId == $"bc_lid_A{i}");
                var fB = lidSlot.RequiredFeatures.Single(f => f.FeatureId == $"bc_lid_B{i}");
                var wA = StratumSpatialMath.LocalPointToWorld(basePose, fA.LocalPositionMm);
                var wB = StratumSpatialMath.LocalPointToWorld(lidPose, fB.LocalPositionMm);
                Assert.True(StratumSpatialMath.Length(StratumSpatialMath.Subtract(wA, wB)) < 1e-6,
                    $"hole {i}: world A ({wA[0]:0.####},{wA[1]:0.####},{wA[2]:0.####}) != world B ({wB[0]:0.####},{wB[1]:0.####},{wB[2]:0.####})");

                // A-threaded: base gets pilot holes, lid gets clearance holes.
                Assert.Equal("pilot-hole", fA.FeatureKind);
                Assert.Equal(2.5, fA.DiaMm, 6);
                Assert.Equal("thru-hole", fB.FeatureKind);
                Assert.Equal(3.4, fB.DiaMm, 6);
            }
        }

        [Fact]
        public void Derivation_IsIdempotent()
        {
            var (bp, _) = BoltedBoxFixture();
            var reg = StratumDimensionRegistry.CreateDefault();
            StratumContractEngine.DeriveRequiredFeatures(bp, reg);
            StratumContractEngine.DeriveRequiredFeatures(bp, reg);
            Assert.Equal(4, bp.Slots[0].RequiredFeatures.Count);
            Assert.Equal(4, bp.Slots[1].RequiredFeatures.Count);
        }

        [Fact]
        public void ContractHelperPython_EmitsApplyFunction()
        {
            var (bp, _) = BoltedBoxFixture();
            StratumContractEngine.DeriveRequiredFeatures(bp, StratumDimensionRegistry.CreateDefault());
            string helper = StratumContractEngine.BuildContractHelperPython(bp.Slots[1]);
            Assert.Contains("def stratum_apply_contract_features", helper);
            Assert.Contains("_STRATUM_CONTRACT_CUT", helper);
            // Lid gets 4 clearance cuts, no additive geometry.
            Assert.Contains("'dia': 3.4", helper);
        }

        [Fact]
        public void ValidateBlueprint_CleanFixture_HasNoErrors()
        {
            var (bp, plan) = BoltedBoxFixture();
            var errors = StratumContractEngine.ValidateBlueprint(bp, plan);
            Assert.Empty(errors);
        }

        [Fact]
        public void ValidateBlueprint_FlagsMissingSlot()
        {
            var (bp, plan) = BoltedBoxFixture();
            bp.Slots.RemoveAt(1);
            bp.Contracts.Clear();
            var errors = StratumContractEngine.ValidateBlueprint(bp, plan);
            Assert.Contains(errors, e => e.Contains("Lid"));
        }

        [Fact]
        public void ValidateBlueprint_FlagsBadPrincipalAxis()
        {
            var (bp, plan) = BoltedBoxFixture();
            bp.Slots[0].PrincipalAxis = "+Q";
            var errors = StratumContractEngine.ValidateBlueprint(bp, plan);
            Assert.Contains(errors, e => e.Contains("principalAxis"));
        }

        [Fact]
        public void ValidateBlueprint_FlagsQuantityInstanceMismatch()
        {
            var (bp, plan) = BoltedBoxFixture();
            bp.Slots[0].Quantity = 3;
            bp.Slots[0].Instances = new List<double[]> { new[] { 0.0, 0, 0, 0, 0, 0 } };
            var errors = StratumContractEngine.ValidateBlueprint(bp, plan);
            Assert.Contains(errors, e => e.Contains("instances"));
        }

        [Fact]
        public void ValidateBlueprint_FlagsContractWithUnknownParticipant()
        {
            var (bp, plan) = BoltedBoxFixture();
            bp.Contracts[0].PartB = "Roof";
            var errors = StratumContractEngine.ValidateBlueprint(bp, plan);
            Assert.Contains(errors, e => e.Contains("Roof"));
        }

        [Fact]
        public void ValidateBlueprint_FlagsUnknownContractKind()
        {
            var (bp, plan) = BoltedBoxFixture();
            bp.Contracts[0].Kind = "welded";
            var errors = StratumContractEngine.ValidateBlueprint(bp, plan);
            Assert.Contains(errors, e => e.Contains("welded"));
        }

        [Fact]
        public void ValidateBlueprint_FlagsGrossInterpenetration()
        {
            var (bp, plan) = BoltedBoxFixture();
            // Put the lid in the middle of the base — heavy overlap on every axis.
            bp.Slots[1].WorldPosition = new[] { 0.0, 0, 5 };
            bp.Slots[1].BoundingBoxMm = new[] { 100.0, 80, 30 };
            var errors = StratumContractEngine.ValidateBlueprint(bp, plan);
            Assert.Contains(errors, e => e.Contains("interpenetrate"));
        }

        [Fact]
        public void ContractPairs_NormalisesOrder()
        {
            var (bp, _) = BoltedBoxFixture();
            var pairs = StratumContractEngine.ContractPairs(bp);
            Assert.Contains(("base", "lid"), pairs);
        }
    }
}
