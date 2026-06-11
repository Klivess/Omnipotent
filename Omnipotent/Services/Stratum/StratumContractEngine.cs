using System.Globalization;

namespace Omnipotent.Services.Stratum
{
    /// <summary>
    /// Shared 3D math for blueprint/contract frame transforms. The composer applies a part's
    /// rotation as intrinsic Euler XYZ (rotate about X, then Y, then Z) followed by translation
    /// to the slot's world position. These helpers are the single source of truth for that
    /// convention — do not re-derive it elsewhere.
    /// </summary>
    public static class StratumSpatialMath
    {
        public static double[] Add(double[] a, double[] b) =>
            new[] { C(a, 0) + C(b, 0), C(a, 1) + C(b, 1), C(a, 2) + C(b, 2) };

        public static double[] Subtract(double[] a, double[] b) =>
            new[] { C(a, 0) - C(b, 0), C(a, 1) - C(b, 1), C(a, 2) - C(b, 2) };

        public static double[] Scale(double[] v, double s) =>
            new[] { C(v, 0) * s, C(v, 1) * s, C(v, 2) * s };

        public static double Dot(double[] a, double[] b) =>
            C(a, 0) * C(b, 0) + C(a, 1) * C(b, 1) + C(a, 2) * C(b, 2);

        public static double[] Cross(double[] a, double[] b) => new[]
        {
            C(a, 1) * C(b, 2) - C(a, 2) * C(b, 1),
            C(a, 2) * C(b, 0) - C(a, 0) * C(b, 2),
            C(a, 0) * C(b, 1) - C(a, 1) * C(b, 0),
        };

        public static double Length(double[] v) => Math.Sqrt(Dot(v, v));

        /// <summary>Returns the unit vector, or [0,0,0] when the input is degenerate.</summary>
        public static double[] Normalize(double[] v)
        {
            double len = Length(v);
            return len < 1e-12 ? new double[] { 0, 0, 0 } : Scale(v, 1.0 / len);
        }

        /// <summary>Forward rotation: applies Rx, then Ry, then Rz (intrinsic Euler XYZ, degrees).</summary>
        public static double[] RotateEulerXyzDeg(double[] v, double[] rotDeg)
        {
            double x = C(v, 0), y = C(v, 1), z = C(v, 2);
            double rx = C(rotDeg, 0) * Math.PI / 180.0;
            double ry = C(rotDeg, 1) * Math.PI / 180.0;
            double rz = C(rotDeg, 2) * Math.PI / 180.0;
            double cx = Math.Cos(rx), sx = Math.Sin(rx);
            (y, z) = (cx * y - sx * z, sx * y + cx * z);
            double cy = Math.Cos(ry), sy = Math.Sin(ry);
            (x, z) = (cy * x + sy * z, -sy * x + cy * z);
            double cz = Math.Cos(rz), sz = Math.Sin(rz);
            (x, y) = (cz * x - sz * y, sz * x + cz * y);
            return new[] { x, y, z };
        }

        /// <summary>
        /// Exact inverse of <see cref="RotateEulerXyzDeg"/>: applies Rz(-rz), then Ry(-ry), then
        /// Rx(-rx). NOTE: negating the angles and reapplying the forward helper is NOT a correct
        /// inverse for compound rotations — always use this for world→local direction transforms.
        /// </summary>
        public static double[] RotateEulerXyzInverseDeg(double[] v, double[] rotDeg)
        {
            double x = C(v, 0), y = C(v, 1), z = C(v, 2);
            double rx = -C(rotDeg, 0) * Math.PI / 180.0;
            double ry = -C(rotDeg, 1) * Math.PI / 180.0;
            double rz = -C(rotDeg, 2) * Math.PI / 180.0;
            double cz = Math.Cos(rz), sz = Math.Sin(rz);
            (x, y) = (cz * x - sz * y, sz * x + cz * y);
            double cy = Math.Cos(ry), sy = Math.Sin(ry);
            (x, z) = (cy * x + sy * z, -sy * x + cy * z);
            double cx = Math.Cos(rx), sx = Math.Sin(rx);
            (y, z) = (cx * y - sx * z, sx * y + cx * z);
            return new[] { x, y, z };
        }

        /// <summary>A single placement pose (position + Euler XYZ rotation) of a slot instance.</summary>
        public sealed class Pose
        {
            public double[] PositionMm = new double[] { 0, 0, 0 };
            public double[] RotationDeg = new double[] { 0, 0, 0 };
        }

        /// <summary>All world poses a slot occupies (its instances, or the single slot pose).</summary>
        public static List<Pose> SlotPoses(MechanicalBlueprintSlot slot)
        {
            if (slot.Instances != null && slot.Instances.Count > 0)
            {
                return slot.Instances.Select(i => new Pose
                {
                    PositionMm = new[] { C(i, 0), C(i, 1), C(i, 2) },
                    RotationDeg = new[] { C(i, 3), C(i, 4), C(i, 5) },
                }).ToList();
            }
            return new List<Pose>
            {
                new Pose { PositionMm = slot.WorldPosition ?? new double[3], RotationDeg = slot.WorldRotationDeg ?? new double[3] }
            };
        }

        /// <summary>The slot pose whose position is nearest a world point — used to resolve which
        /// instance of a replicated part a contract frame refers to.</summary>
        public static Pose NearestPose(MechanicalBlueprintSlot slot, double[] worldPoint)
        {
            var poses = SlotPoses(slot);
            return poses.OrderBy(p => Length(Subtract(worldPoint, p.PositionMm))).First();
        }

        public static double[] WorldPointToLocal(Pose pose, double[] world) =>
            RotateEulerXyzInverseDeg(Subtract(world, pose.PositionMm), pose.RotationDeg);

        public static double[] WorldDirToLocal(Pose pose, double[] worldDir) =>
            RotateEulerXyzInverseDeg(worldDir, pose.RotationDeg);

        public static double[] LocalPointToWorld(Pose pose, double[] local) =>
            Add(RotateEulerXyzDeg(local, pose.RotationDeg), pose.PositionMm);

        public static double[] LocalDirToWorld(Pose pose, double[] localDir) =>
            RotateEulerXyzDeg(localDir, pose.RotationDeg);

        private static double C(double[]? v, int i) => v != null && v.Length > i ? v[i] : 0;
    }

    /// <summary>
    /// Deterministic derivation + validation of typed assembly contracts. Given an approved
    /// blueprint, computes the exact local-frame features each participating part must contain
    /// (positions from frame transforms, diameters from <see cref="StratumFitTable"/>), emits
    /// ready-made CadQuery helper code that builds them, and lists them for verification probes.
    /// The LLM never invents a mating dimension.
    /// </summary>
    public static class StratumContractEngine
    {
        // ───────────────────────── validation ─────────────────────────

        /// <summary>
        /// Structural validation of a blueprint against the plan. Returns a list of problems
        /// phrased for the LLM retry prompt; empty = valid.
        /// </summary>
        public static List<string> ValidateBlueprint(MechanicalBlueprint bp, StratumPlannerOutput plan)
        {
            var errors = new List<string>();
            var slots = bp.Slots ?? new List<MechanicalBlueprintSlot>();

            // Every planner subtask must have exactly one slot.
            foreach (var t in plan.MechanicalSubtasks ?? new List<StratumPlannerSubtask>())
            {
                int n = slots.Count(s => string.Equals(s.SubtaskTitle, t.Title, StringComparison.OrdinalIgnoreCase));
                if (n == 0) errors.Add($"The blueprint is missing a slot for `{t.Title}`. Every planner subtask must appear in `slots[]`.");
                else if (n > 1) errors.Add($"Subtask `{t.Title}` appears in {n} slots — it must appear exactly once.");
            }

            var allowedAxes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "+X", "-X", "+Y", "-Y", "+Z", "-Z" };
            foreach (var s in slots)
            {
                if (s.Virtual) continue;
                if (s.Quantity > 1 && (s.Instances == null || s.Instances.Count != s.Quantity))
                    errors.Add($"`{s.SubtaskTitle}` has quantity={s.Quantity} but `instances[]` has {s.Instances?.Count ?? 0} entries — must equal quantity, each as [x,y,z,rx,ry,rz].");
                if (string.IsNullOrWhiteSpace(s.PrincipalAxis) || !allowedAxes.Contains(s.PrincipalAxis.Trim()))
                    errors.Add($"`{s.SubtaskTitle}` is missing a valid `principalAxis` (must be one of +X, -X, +Y, -Y, +Z, -Z).");
            }

            // Heuristic: catch virtual subtasks the planner forgot to mark.
            var virtualHeuristic = new System.Text.RegularExpressions.Regex(
                "finali[sz]e|integrat|verify|inspect|assembl(y|e) cad", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (var s in slots)
            {
                if (s.Virtual) continue;
                bool tiny = s.BoundingBoxMm != null && s.BoundingBoxMm.Length >= 3
                            && s.BoundingBoxMm[0] <= 5 && s.BoundingBoxMm[1] <= 5 && s.BoundingBoxMm[2] <= 5;
                if (virtualHeuristic.IsMatch(s.SubtaskTitle) || tiny)
                    errors.Add($"`{s.SubtaskTitle}` looks like a non-physical / integration task but is not marked virtual. Set `virtual: true` on slots that don't correspond to a real machined/printed part.");
            }

            // Gross interpenetration precheck: unrotated, single-instance slots whose AABBs
            // (assumed centred on worldPosition) overlap by >25% of the smaller dimension on
            // every axis are almost certainly a layout mistake.
            var checkable = slots.Where(s => !s.Virtual
                    && (s.Instances == null || s.Instances.Count == 0)
                    && IsNearZero(s.WorldRotationDeg)
                    && s.BoundingBoxMm != null && s.BoundingBoxMm.Length >= 3
                    && s.WorldPosition != null && s.WorldPosition.Length >= 3).ToList();
            for (int i = 0; i < checkable.Count; i++)
                for (int j = i + 1; j < checkable.Count; j++)
                {
                    var a = checkable[i]; var b = checkable[j];
                    bool gross = true;
                    for (int ax = 0; ax < 3 && gross; ax++)
                    {
                        double overlap = (a.BoundingBoxMm[ax] + b.BoundingBoxMm[ax]) / 2.0
                                         - Math.Abs(a.WorldPosition[ax] - b.WorldPosition[ax]);
                        double smaller = Math.Min(a.BoundingBoxMm[ax], b.BoundingBoxMm[ax]);
                        if (overlap < 0.25 * smaller) gross = false;
                    }
                    if (gross)
                        errors.Add($"Slots `{a.SubtaskTitle}` and `{b.SubtaskTitle}` interpenetrate heavily given their worldPosition + boundingBoxMm. Move them apart or shrink their boxes — parts must not overlap.");
                }

            // Contract checks.
            var contracts = bp.Contracts ?? new List<AssemblyContract>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in contracts)
            {
                string label = string.IsNullOrWhiteSpace(c.ContractId) ? $"({c.Kind} {c.PartA}↔{c.PartB})" : $"`{c.ContractId}`";
                if (string.IsNullOrWhiteSpace(c.ContractId))
                    errors.Add($"A contract {label} has no `contractId`. Give every contract a short unique id.");
                else if (!seenIds.Add(c.ContractId))
                    errors.Add($"Duplicate contractId `{c.ContractId}` — ids must be unique.");

                if (!StratumContractKinds.All.Contains((c.Kind ?? "").Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    errors.Add($"Contract {label} has unknown kind `{c.Kind}`. Allowed: {string.Join(", ", StratumContractKinds.All)}.");
                    continue;
                }

                var slotA = FindSlot(bp, c.PartA);
                var slotB = FindSlot(bp, c.PartB);
                if (slotA == null) errors.Add($"Contract {label}: partA `{c.PartA}` does not match any slot subtaskTitle.");
                if (slotB == null) errors.Add($"Contract {label}: partB `{c.PartB}` does not match any slot subtaskTitle.");
                if (slotA != null && slotB != null && ReferenceEquals(slotA, slotB))
                    errors.Add($"Contract {label}: partA and partB are the same slot — a contract joins two different parts.");
                if (slotA != null && slotA.Virtual) errors.Add($"Contract {label}: partA `{c.PartA}` is virtual — contracts must join physical parts.");
                if (slotB != null && slotB.Virtual) errors.Add($"Contract {label}: partB `{c.PartB}` is virtual — contracts must join physical parts.");

                if (c.WorldFrame == null || StratumSpatialMath.Length(c.WorldFrame.AxisDir ?? new double[3]) < 1e-9)
                {
                    errors.Add($"Contract {label}: worldFrame.axisDir is missing or zero. It must be a direction pointing from partA into partB.");
                    continue;
                }
                var axis = StratumSpatialMath.Normalize(c.WorldFrame.AxisDir ?? new double[] { 0, 0, 1 });
                var clock = StratumSpatialMath.Normalize(c.WorldFrame.ClockDir ?? new double[] { 1, 0, 0 });
                if (StratumSpatialMath.Length(clock) > 1e-9 && Math.Abs(StratumSpatialMath.Dot(axis, clock)) > 0.999)
                    errors.Add($"Contract {label}: worldFrame.clockDir is parallel to axisDir — pick a perpendicular zero-angle reference.");

                // Frame origin should be anchored near both participants.
                foreach (var (slot, name) in new[] { (slotA, c.PartA), (slotB, c.PartB) })
                {
                    if (slot == null || slot.BoundingBoxMm == null || slot.BoundingBoxMm.Length < 3) continue;
                    var pose = StratumSpatialMath.NearestPose(slot, c.WorldFrame.OriginMm ?? new double[3]);
                    double dist = StratumSpatialMath.Length(StratumSpatialMath.Subtract(c.WorldFrame.OriginMm ?? new double[3], pose.PositionMm));
                    double halfDiag = 0.5 * StratumSpatialMath.Length(slot.BoundingBoxMm);
                    if (dist > halfDiag + 30.0)
                        errors.Add($"Contract {label}: worldFrame.originMm is {dist:0} mm from `{name}` — anchor the frame on the shared face/axis between the two parts.");
                }

                switch ((c.Kind ?? "").Trim().ToLowerInvariant())
                {
                    case StratumContractKinds.BoltCircle:
                        if (c.BoltCircle == null) { errors.Add($"Contract {label}: kind bolt-circle requires a `boltCircle` params object."); break; }
                        if (c.BoltCircle.BoltCount < 2 || c.BoltCircle.BoltCount > 16)
                            errors.Add($"Contract {label}: boltCount {c.BoltCircle.BoltCount} is outside the sane range 2–16.");
                        if (!StratumFitTable.KnownScrew(c.BoltCircle.ScrewSize))
                            errors.Add($"Contract {label}: unknown screwSize `{c.BoltCircle.ScrewSize}` (use M2, M2.5, M3, M4 or M5).");
                        if (c.BoltCircle.CircleDiaMm < 2 * StratumFitTable.GetScrew(c.BoltCircle.ScrewSize).ClearanceDiaMm)
                            errors.Add($"Contract {label}: circleDiaMm {c.BoltCircle.CircleDiaMm} is too small for {c.BoltCircle.ScrewSize} screws.");
                        break;
                    case StratumContractKinds.HolePattern:
                        if (c.HolePattern == null || c.HolePattern.HoleOffsetsMm == null || c.HolePattern.HoleOffsetsMm.Count == 0)
                            errors.Add($"Contract {label}: kind hole-pattern requires `holePattern.holeOffsetsMm` with at least one [x,y] entry.");
                        else if (!StratumFitTable.KnownScrew(c.HolePattern.ScrewSize))
                            errors.Add($"Contract {label}: unknown screwSize `{c.HolePattern.ScrewSize}`.");
                        break;
                    case StratumContractKinds.ShaftBore:
                        if (c.ShaftBore == null || c.ShaftBore.NominalDiaMm <= 0 || c.ShaftBore.EngagementLenMm <= 0)
                            errors.Add($"Contract {label}: kind shaft-bore requires `shaftBore` with positive nominalDiaMm and engagementLenMm.");
                        break;
                    case StratumContractKinds.SlotTab:
                        if (c.SlotTab == null || c.SlotTab.WidthMm <= 0 || c.SlotTab.DepthMm <= 0 || c.SlotTab.LengthMm <= 0)
                            errors.Add($"Contract {label}: kind slot-tab requires `slotTab` with positive widthMm, depthMm and lengthMm.");
                        break;
                    case StratumContractKinds.PressFitBoss:
                        if (c.PressFitBoss == null || c.PressFitBoss.OuterDiaMm <= 0 || c.PressFitBoss.HeightMm <= 0)
                            errors.Add($"Contract {label}: kind press-fit-boss requires `pressFitBoss` with positive outerDiaMm and heightMm.");
                        break;
                }
            }

            return errors;
        }

        // ───────────────────────── derivation ─────────────────────────

        /// <summary>
        /// Populates every slot's <see cref="MechanicalBlueprintSlot.RequiredFeatures"/> from the
        /// blueprint's contracts. Pure deterministic code — positions via frame transforms,
        /// diameters via the fit table. Clears previously derived features first (idempotent).
        /// </summary>
        public static void DeriveRequiredFeatures(MechanicalBlueprint bp, StratumDimensionRegistry registry, Action<string>? log = null)
        {
            foreach (var s in bp.Slots) s.RequiredFeatures = new List<RequiredFeature>();
            foreach (var c in bp.Contracts ?? new List<AssemblyContract>())
            {
                var slotA = FindSlot(bp, c.PartA);
                var slotB = FindSlot(bp, c.PartB);
                if (slotA == null || slotB == null) continue;

                var origin = c.WorldFrame?.OriginMm ?? new double[3];
                var axis = StratumSpatialMath.Normalize(c.WorldFrame?.AxisDir ?? new double[] { 0, 0, 1 });
                if (StratumSpatialMath.Length(axis) < 1e-9) axis = new double[] { 0, 0, 1 };
                var clock0 = c.WorldFrame?.ClockDir ?? new double[] { 1, 0, 0 };
                // Orthogonalise clock against axis; fall back to any perpendicular.
                var clock = StratumSpatialMath.Normalize(StratumSpatialMath.Subtract(clock0, StratumSpatialMath.Scale(axis, StratumSpatialMath.Dot(clock0, axis))));
                if (StratumSpatialMath.Length(clock) < 1e-9)
                    clock = StratumSpatialMath.Normalize(StratumSpatialMath.Cross(axis, Math.Abs(axis[2]) < 0.9 ? new double[] { 0, 0, 1 } : new double[] { 1, 0, 0 }));
                var third = StratumSpatialMath.Cross(axis, clock);

                var poseA = StratumSpatialMath.NearestPose(slotA, origin);
                var poseB = StratumSpatialMath.NearestPose(slotB, origin);

                switch ((c.Kind ?? "").Trim().ToLowerInvariant())
                {
                    case StratumContractKinds.BoltCircle:
                    {
                        var p = c.BoltCircle ?? new BoltCircleParams();
                        var screw = StratumFitTable.GetScrew(p.ScrewSize);
                        double r = p.CircleDiaMm / 2.0;
                        for (int i = 0; i < Math.Max(p.BoltCount, 1); i++)
                        {
                            double ang = (p.StartAngleDeg + i * 360.0 / Math.Max(p.BoltCount, 1)) * Math.PI / 180.0;
                            var world = StratumSpatialMath.Add(origin,
                                StratumSpatialMath.Add(StratumSpatialMath.Scale(clock, r * Math.Cos(ang)),
                                                       StratumSpatialMath.Scale(third, r * Math.Sin(ang))));
                            AddScrewPair(c, slotA, poseA, slotB, poseB, world, axis, screw, p.ScrewSize, p.AThreaded, i + 1);
                        }
                        break;
                    }
                    case StratumContractKinds.HolePattern:
                    {
                        var p = c.HolePattern ?? new HolePatternParams();
                        var screw = StratumFitTable.GetScrew(p.ScrewSize);
                        int i = 0;
                        foreach (var off in p.HoleOffsetsMm ?? new List<double[]>())
                        {
                            i++;
                            double ox = off != null && off.Length > 0 ? off[0] : 0;
                            double oy = off != null && off.Length > 1 ? off[1] : 0;
                            var world = StratumSpatialMath.Add(origin,
                                StratumSpatialMath.Add(StratumSpatialMath.Scale(clock, ox), StratumSpatialMath.Scale(third, oy)));
                            AddScrewPair(c, slotA, poseA, slotB, poseB, world, axis, screw, p.ScrewSize, p.AThreaded, i);
                        }
                        break;
                    }
                    case StratumContractKinds.ShaftBore:
                    {
                        var p = c.ShaftBore ?? new ShaftBoreParams();
                        double boreDia = p.NominalDiaMm + StratumFitTable.FitAllowanceMm(p.FitClass);
                        slotA.RequiredFeatures.Add(new RequiredFeature
                        {
                            FeatureId = $"{c.ContractId}_shaft", ContractId = c.ContractId, FeatureKind = "shaft",
                            LocalPositionMm = StratumSpatialMath.WorldPointToLocal(poseA, origin),
                            LocalAxisDir = StratumSpatialMath.WorldDirToLocal(poseA, axis),
                            LocalClockDir = StratumSpatialMath.WorldDirToLocal(poseA, clock),
                            DiaMm = p.NominalDiaMm, DepthMm = p.EngagementLenMm,
                            Spec = $"Shaft Ø{p.NominalDiaMm:0.##} mm × {p.EngagementLenMm:0.##} mm protruding into `{c.PartB}` ({p.FitClass} fit). Contract {c.ContractId}.",
                        });
                        slotB.RequiredFeatures.Add(new RequiredFeature
                        {
                            FeatureId = $"{c.ContractId}_bore", ContractId = c.ContractId, FeatureKind = "bore",
                            LocalPositionMm = StratumSpatialMath.WorldPointToLocal(poseB, origin),
                            LocalAxisDir = StratumSpatialMath.WorldDirToLocal(poseB, axis),
                            LocalClockDir = StratumSpatialMath.WorldDirToLocal(poseB, clock),
                            DiaMm = boreDia, DepthMm = p.EngagementLenMm + 0.5,
                            Spec = $"Bore Ø{boreDia:0.##} mm × {p.EngagementLenMm + 0.5:0.##} mm deep accepting `{c.PartA}`'s shaft ({p.FitClass} fit → Ø{p.NominalDiaMm:0.##}+{StratumFitTable.FitAllowanceMm(p.FitClass):+0.##;-0.##}). Contract {c.ContractId}.",
                        });
                        break;
                    }
                    case StratumContractKinds.SlotTab:
                    {
                        var p = c.SlotTab ?? new SlotTabParams();
                        double fit = StratumFitTable.FitAllowanceMm(p.FitClass);
                        slotA.RequiredFeatures.Add(new RequiredFeature
                        {
                            FeatureId = $"{c.ContractId}_tab", ContractId = c.ContractId, FeatureKind = "tab",
                            LocalPositionMm = StratumSpatialMath.WorldPointToLocal(poseA, origin),
                            LocalAxisDir = StratumSpatialMath.WorldDirToLocal(poseA, axis),
                            LocalClockDir = StratumSpatialMath.WorldDirToLocal(poseA, clock),
                            SizeMm = new[] { p.WidthMm, p.LengthMm, p.DepthMm }, DepthMm = p.DepthMm,
                            Spec = $"Tab {p.WidthMm:0.##}×{p.LengthMm:0.##} mm protruding {p.DepthMm:0.##} mm into `{c.PartB}`. Contract {c.ContractId}.",
                        });
                        slotB.RequiredFeatures.Add(new RequiredFeature
                        {
                            FeatureId = $"{c.ContractId}_slot", ContractId = c.ContractId, FeatureKind = "slot",
                            LocalPositionMm = StratumSpatialMath.WorldPointToLocal(poseB, origin),
                            LocalAxisDir = StratumSpatialMath.WorldDirToLocal(poseB, axis),
                            LocalClockDir = StratumSpatialMath.WorldDirToLocal(poseB, clock),
                            SizeMm = new[] { p.WidthMm + fit, p.LengthMm + fit, p.DepthMm + 0.4 }, DepthMm = p.DepthMm + 0.4,
                            Spec = $"Slot {p.WidthMm + fit:0.##}×{p.LengthMm + fit:0.##} mm, {p.DepthMm + 0.4:0.##} mm deep, accepting `{c.PartA}`'s tab ({p.FitClass} fit). Contract {c.ContractId}.",
                        });
                        break;
                    }
                    case StratumContractKinds.PressFitBoss:
                    {
                        var p = c.PressFitBoss ?? new PressFitBossParams();
                        double recessDia = p.OuterDiaMm + StratumFitTable.FitAllowanceMm("press");
                        slotA.RequiredFeatures.Add(new RequiredFeature
                        {
                            FeatureId = $"{c.ContractId}_boss", ContractId = c.ContractId, FeatureKind = "boss",
                            LocalPositionMm = StratumSpatialMath.WorldPointToLocal(poseA, origin),
                            LocalAxisDir = StratumSpatialMath.WorldDirToLocal(poseA, axis),
                            LocalClockDir = StratumSpatialMath.WorldDirToLocal(poseA, clock),
                            DiaMm = p.OuterDiaMm, DepthMm = p.HeightMm,
                            Spec = $"Press-fit boss Ø{p.OuterDiaMm:0.##} mm × {p.HeightMm:0.##} mm protruding into `{c.PartB}`. Contract {c.ContractId}.",
                        });
                        slotB.RequiredFeatures.Add(new RequiredFeature
                        {
                            FeatureId = $"{c.ContractId}_recess", ContractId = c.ContractId, FeatureKind = "bore",
                            LocalPositionMm = StratumSpatialMath.WorldPointToLocal(poseB, origin),
                            LocalAxisDir = StratumSpatialMath.WorldDirToLocal(poseB, axis),
                            LocalClockDir = StratumSpatialMath.WorldDirToLocal(poseB, clock),
                            DiaMm = recessDia, DepthMm = p.HeightMm + 0.2,
                            Spec = $"Press-fit recess Ø{recessDia:0.##} mm × {p.HeightMm + 0.2:0.##} mm deep accepting `{c.PartA}`'s boss (interference fit). Contract {c.ContractId}.",
                        });
                        break;
                    }
                    case StratumContractKinds.SnapFit:
                    {
                        foreach (var (slot, pose, other) in new[] { (slotA, poseA, c.PartB), (slotB, poseB, c.PartA) })
                        {
                            slot.RequiredFeatures.Add(new RequiredFeature
                            {
                                FeatureId = $"{c.ContractId}_{(ReferenceEquals(slot, slotA) ? "A" : "B")}", ContractId = c.ContractId,
                                FeatureKind = "contact-face",
                                LocalPositionMm = StratumSpatialMath.WorldPointToLocal(pose, origin),
                                LocalAxisDir = StratumSpatialMath.WorldDirToLocal(pose, axis),
                                LocalClockDir = StratumSpatialMath.WorldDirToLocal(pose, clock),
                                Spec = $"Snap-fit engagement with `{other}` at this location. {c.Notes}".Trim(),
                            });
                        }
                        break;
                    }
                }
            }

            foreach (var s in bp.Slots.Where(s => s.RequiredFeatures.Count > 0))
                log?.Invoke($"Contract features for '{s.SubtaskTitle}': " + string.Join(", ",
                    s.RequiredFeatures.GroupBy(f => f.FeatureKind).Select(g => $"{g.Count()} {g.Key}")));
        }

        private static void AddScrewPair(
            AssemblyContract c, MechanicalBlueprintSlot slotA, StratumSpatialMath.Pose poseA,
            MechanicalBlueprintSlot slotB, StratumSpatialMath.Pose poseB,
            double[] worldHole, double[] axis, StratumFitTable.ScrewSpec screw, string screwSize, bool aThreaded, int index)
        {
            // A side: cut proceeds INTO A, i.e. against the contract axis (axis points A→B).
            var intoA = StratumSpatialMath.Scale(axis, -1);
            if (aThreaded)
            {
                double pilotDepth = Math.Max(2.5 * screw.NominalDiaMm, 6.0);
                slotA.RequiredFeatures.Add(new RequiredFeature
                {
                    FeatureId = $"{c.ContractId}_A{index}", ContractId = c.ContractId, FeatureKind = "pilot-hole",
                    LocalPositionMm = StratumSpatialMath.WorldPointToLocal(poseA, worldHole),
                    LocalAxisDir = StratumSpatialMath.WorldDirToLocal(poseA, intoA),
                    DiaMm = screw.PilotDiaMm, DepthMm = pilotDepth,
                    Spec = $"{screwSize} pilot hole Ø{screw.PilotDiaMm:0.##} mm × {pilotDepth:0.##} mm deep (self-tap). Contract {c.ContractId}, hole {index}.",
                });
            }
            else
            {
                slotA.RequiredFeatures.Add(new RequiredFeature
                {
                    FeatureId = $"{c.ContractId}_A{index}", ContractId = c.ContractId, FeatureKind = "thru-hole",
                    LocalPositionMm = StratumSpatialMath.WorldPointToLocal(poseA, worldHole),
                    LocalAxisDir = StratumSpatialMath.WorldDirToLocal(poseA, intoA),
                    DiaMm = screw.ClearanceDiaMm, DepthMm = 0,
                    Spec = $"{screwSize} clearance thru-hole Ø{screw.ClearanceDiaMm:0.##} mm. Contract {c.ContractId}, hole {index}.",
                });
            }
            slotB.RequiredFeatures.Add(new RequiredFeature
            {
                FeatureId = $"{c.ContractId}_B{index}", ContractId = c.ContractId, FeatureKind = "thru-hole",
                LocalPositionMm = StratumSpatialMath.WorldPointToLocal(poseB, worldHole),
                LocalAxisDir = StratumSpatialMath.WorldDirToLocal(poseB, axis),
                DiaMm = screw.ClearanceDiaMm, DepthMm = 0,
                Spec = $"{screwSize} clearance thru-hole Ø{screw.ClearanceDiaMm:0.##} mm. Contract {c.ContractId}, hole {index}.",
            });
        }

        // ───────────────────── electronics integration (moved from StratumMechanicalAgent) ─────────────────────

        /// <summary>
        /// For each module placement in the approved electronics layout, derive the bosses,
        /// thru-holes, and wall-cutouts the hosting mechanical part must implement, transforming
        /// the placement from world space into the host part's local frame. Pure deterministic
        /// code — no LLM call. Mutates the blueprint slots in place.
        /// </summary>
        public static void AttachIntegrationFeatures(
            MechanicalBlueprint blueprint, StratumElectronicsLayout layout,
            StratumDimensionRegistry registry, Action<string>? log = null)
        {
            double bossWall = registry.Get("boss_wall", 2.0);
            double bossHeight = registry.Get("boss_height", 5.0);
            double connClear = registry.Get("connector_clearance", 0.5);

            foreach (var s in blueprint.Slots) s.IntegrationFeatures = new List<MechanicalIntegrationFeature>();

            foreach (var placement in layout.Placements)
            {
                var slot = blueprint.Slots.FirstOrDefault(s => string.Equals(s.SubtaskTitle, placement.HostingPart, StringComparison.OrdinalIgnoreCase));
                if (slot == null)
                {
                    log?.Invoke($"Electronics layout assigns '{placement.InstanceId}' to host '{placement.HostingPart}', but no matching mechanical slot exists. Skipping integration features for this module.");
                    continue;
                }
                if (slot.Virtual)
                {
                    log?.Invoke($"Electronics layout assigns '{placement.InstanceId}' to host '{slot.SubtaskTitle}' which is marked virtual. Skipping integration features.");
                    continue;
                }

                var hostPose = new StratumSpatialMath.Pose { PositionMm = slot.WorldPosition, RotationDeg = slot.WorldRotationDeg };
                var localPos = StratumSpatialMath.WorldPointToLocal(hostPose, placement.WorldPositionMm);
                var localRotDeg = StratumSpatialMath.Subtract(placement.WorldRotationDeg, slot.WorldRotationDeg);
                var f = placement.Footprint;

                // 1. Reservation: a "do-not-fill" volume the size of the module's footprint, centred at localPos.
                slot.IntegrationFeatures.Add(new MechanicalIntegrationFeature
                {
                    FeatureKind = "reservation",
                    LocalPositionMm = (double[])localPos.Clone(),
                    LocalRotationDeg = (double[])localRotDeg.Clone(),
                    SizeMm = new[] { f.DxMm, f.DyMm, f.DzMm },
                    ForModuleInstanceId = placement.InstanceId,
                    Spec = $"Reserve volume for {placement.ModuleId} ({placement.InstanceId}). Do NOT fill this region; ribs/walls must route around it.",
                });

                // 2. Bosses + thru-holes for every mount hole in the footprint.
                if (string.Equals(f.MountStrategy, "screw-bosses", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var hole in f.MountHolesMm)
                    {
                        if (hole == null || hole.Length < 3) continue;
                        double hx = hole[0], hy = hole[1], holeDia = hole[2];
                        var moduleLocalHole = new[] { hx, hy, 0.0 };
                        var worldHole = StratumSpatialMath.Add(
                            StratumSpatialMath.RotateEulerXyzDeg(moduleLocalHole, placement.WorldRotationDeg),
                            placement.WorldPositionMm);
                        var localHole = StratumSpatialMath.WorldPointToLocal(hostPose, worldHole);

                        double bossOuter = Math.Max(holeDia + 2 * bossWall, 6.0);
                        slot.IntegrationFeatures.Add(new MechanicalIntegrationFeature
                        {
                            FeatureKind = "boss",
                            LocalPositionMm = (double[])localHole.Clone(),
                            LocalRotationDeg = (double[])localRotDeg.Clone(),
                            SizeMm = new[] { bossOuter, bossHeight, holeDia },
                            ForModuleInstanceId = placement.InstanceId,
                            Spec = $"Mounting boss for {placement.InstanceId}: cylinder Ø{bossOuter:0.#} mm × {bossHeight:0.#} mm tall, centred thru-hole Ø{holeDia:0.#} mm (M{Math.Round(holeDia - 0.2)} clearance). Boss top face must contact the module PCB.",
                        });
                    }
                }

                // 3. Wall cutouts for every connector that needs external access.
                foreach (var conn in f.Connectors ?? new List<ConnectorAccess>())
                {
                    var moduleLocalConn = new[] { conn.LocalPositionMm[0], conn.LocalPositionMm[1], conn.LocalPositionMm.Length > 2 ? conn.LocalPositionMm[2] : 0.0 };
                    var worldConn = StratumSpatialMath.Add(
                        StratumSpatialMath.RotateEulerXyzDeg(moduleLocalConn, placement.WorldRotationDeg),
                        placement.WorldPositionMm);
                    var localConn = StratumSpatialMath.WorldPointToLocal(hostPose, worldConn);

                    double cdx = conn.CutoutSizeMm.Length > 0 ? conn.CutoutSizeMm[0] : 8.0;
                    double cdy = conn.CutoutSizeMm.Length > 1 ? conn.CutoutSizeMm[1] : 8.0;
                    slot.IntegrationFeatures.Add(new MechanicalIntegrationFeature
                    {
                        FeatureKind = "wall-cutout",
                        LocalPositionMm = (double[])localConn.Clone(),
                        LocalRotationDeg = (double[])localRotDeg.Clone(),
                        SizeMm = new[] { cdx + 2 * connClear, cdy + 2 * connClear, 0.0 },
                        ForModuleInstanceId = placement.InstanceId,
                        Spec = $"Through-wall cutout for {conn.Kind} on {placement.InstanceId}, oriented along (world) {conn.Direction}. Aperture {cdx + 2 * connClear:0.#} × {cdy + 2 * connClear:0.#} mm; subtract through the nearest exterior wall.",
                    });
                }
            }

            foreach (var s in blueprint.Slots)
            {
                if (s.IntegrationFeatures.Count == 0) continue;
                int bosses = s.IntegrationFeatures.Count(f => f.FeatureKind == "boss");
                int cuts = s.IntegrationFeatures.Count(f => f.FeatureKind == "wall-cutout");
                int res = s.IntegrationFeatures.Count(f => f.FeatureKind == "reservation");
                log?.Invoke($"Integration features for '{s.SubtaskTitle}': {bosses} boss(es), {cuts} cutout(s), {res} reservation(s).");
            }
        }

        // ───────────────────── python helper + prompt generation ─────────────────────

        /// <summary>
        /// Emits the auto-injected CadQuery prelude that builds this slot's contract features.
        /// The part script calls `result = stratum_apply_contract_features(body)` as its last
        /// step; additive features (bosses/shafts/tabs) are unioned, subtractive ones cut.
        /// Returns "" when the slot has no buildable contract features.
        /// </summary>
        public static string BuildContractHelperPython(MechanicalBlueprintSlot slot)
        {
            var adds = new List<string>();
            var cuts = new List<string>();
            foreach (var f in slot.RequiredFeatures ?? new List<RequiredFeature>())
            {
                switch (f.FeatureKind)
                {
                    case "thru-hole":
                    case "pilot-hole":
                    case "bore":
                        cuts.Add(CylSpec(f, isCut: true));
                        break;
                    case "shaft":
                    case "boss":
                        adds.Add(CylSpec(f, isCut: false));
                        break;
                    case "tab":
                        adds.Add(BoxSpec(f, extraDepth: 0, back: 0));
                        break;
                    case "slot":
                        cuts.Add(BoxSpec(f, extraDepth: 0, back: 0.2));
                        break;
                        // contact-face: advisory only.
                }
            }
            if (adds.Count == 0 && cuts.Count == 0) return "";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# ─── Stratum contract feature helper (auto-injected) ───");
            sb.AppendLine("# Call `result = stratum_apply_contract_features(body)` as the LAST step of your script,");
            sb.AppendLine("# where `body` is your finished part (cq.Workplane with one solid, in the part's LOCAL frame).");
            sb.AppendLine("import cadquery as _cq_contract");
            sb.AppendLine();
            sb.AppendLine("_STRATUM_CONTRACT_ADD = [");
            foreach (var a in adds) sb.AppendLine("    " + a + ",");
            sb.AppendLine("]");
            sb.AppendLine("_STRATUM_CONTRACT_CUT = [");
            foreach (var cspec in cuts) sb.AppendLine("    " + cspec + ",");
            sb.AppendLine("]");
            sb.AppendLine(@"
def _stratum_contract_tool(spec):
    if spec['shape'] == 'cyl':
        px, py, pz = spec['pos']
        d = _cq_contract.Vector(*spec['dir']).normalized()
        start = _cq_contract.Vector(px, py, pz) - d.multiply(spec.get('back', 0.0))
        return _cq_contract.Solid.makeCylinder(spec['dia'] / 2.0, spec['len'], start, d)
    if spec['shape'] == 'box':
        plane = _cq_contract.Plane(origin=tuple(spec['pos']), xDir=tuple(spec['xdir']), normal=tuple(spec['normal']))
        return _cq_contract.Workplane(plane).box(spec['w'], spec['l'], spec['h'], centered=(True, True, False)).val()
    raise ValueError('unknown contract tool shape: ' + str(spec.get('shape')))

def stratum_apply_contract_features(body):
    """"""Apply this part's deterministic contract features (host-derived, exact).
    Pass your finished part; returns it with contract bosses/shafts/tabs added and
    all contract holes/bores/slots cut. Call LAST, just before assigning `result`.""""""
    if not isinstance(body, _cq_contract.Workplane):
        body = _cq_contract.Workplane(obj=body)
    for _spec in _STRATUM_CONTRACT_ADD:
        body = body.union(_stratum_contract_tool(_spec))
    for _spec in _STRATUM_CONTRACT_CUT:
        body = body.cut(_stratum_contract_tool(_spec))
    return body
");
            return sb.ToString();
        }

        private static string CylSpec(RequiredFeature f, bool isCut)
        {
            double len, back;
            if (isCut)
            {
                if (f.DepthMm <= 0) { len = 2000.0; back = 1000.0; }       // through-everything, centred on pos
                else { len = f.DepthMm + 0.5; back = 0.5; }                  // start just outside the surface
            }
            else { len = Math.Max(f.DepthMm, 0.1); back = 0.0; }             // additive: base exactly at pos
            return $"{{'shape': 'cyl', 'pos': {Vec(f.LocalPositionMm)}, 'dir': {Vec(f.LocalAxisDir)}, 'dia': {N(f.DiaMm)}, 'len': {N(len)}, 'back': {N(back)}}}";
        }

        private static string BoxSpec(RequiredFeature f, double extraDepth, double back)
        {
            double w = f.SizeMm.Length > 0 ? f.SizeMm[0] : 1;
            double l = f.SizeMm.Length > 1 ? f.SizeMm[1] : 1;
            double h = (f.SizeMm.Length > 2 ? f.SizeMm[2] : Math.Max(f.DepthMm, 1)) + extraDepth;
            var pos = back > 0
                ? StratumSpatialMath.Subtract(f.LocalPositionMm, StratumSpatialMath.Scale(StratumSpatialMath.Normalize(f.LocalAxisDir), back))
                : f.LocalPositionMm;
            return $"{{'shape': 'box', 'pos': {Vec(pos)}, 'normal': {Vec(f.LocalAxisDir)}, 'xdir': {Vec(f.LocalClockDir)}, 'w': {N(w)}, 'l': {N(l)}, 'h': {N(h + back)}}}";
        }

        /// <summary>Prompt section listing the slot's derived contract features.</summary>
        public static string BuildRequiredFeaturesPromptSection(MechanicalBlueprintSlot slot)
        {
            var feats = slot.RequiredFeatures ?? new List<RequiredFeature>();
            if (feats.Count == 0) return "";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("CONTRACT FEATURES (host-derived, EXACT — the injected `stratum_apply_contract_features(body)` builds these for you):");
            foreach (var f in feats)
            {
                string pos = $"({f.LocalPositionMm[0]:0.##}, {f.LocalPositionMm[1]:0.##}, {f.LocalPositionMm[2]:0.##})";
                sb.AppendLine($"  • [{f.FeatureKind}] {f.FeatureId} @ local {pos} mm — {f.Spec}");
            }
            sb.AppendLine();
            sb.AppendLine("Rules for contract features:");
            sb.AppendLine("  - Do NOT model these yourself — call `result = stratum_apply_contract_features(body)` as the LAST step; the host-injected helper adds/cuts them at exact coordinates.");
            sb.AppendLine("  - Your part's solid MUST provide material around each hole/bore position (e.g. the wall or flange the hole passes through) and a base surface for each boss/shaft/tab.");
            sb.AppendLine("  - The host PROBES the final geometry at every feature position; missing material or blocked holes fail verification.");
            return sb.ToString();
        }

        /// <summary>Unordered pairs of subtask titles joined by at least one contract (contact allowed).</summary>
        public static HashSet<(string a, string b)> ContractPairs(MechanicalBlueprint bp)
        {
            var set = new HashSet<(string, string)>();
            foreach (var c in bp.Contracts ?? new List<AssemblyContract>())
            {
                if (string.IsNullOrWhiteSpace(c.PartA) || string.IsNullOrWhiteSpace(c.PartB)) continue;
                var a = c.PartA.Trim().ToLowerInvariant();
                var b = c.PartB.Trim().ToLowerInvariant();
                set.Add(string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a));
            }
            return set;
        }

        private static MechanicalBlueprintSlot? FindSlot(MechanicalBlueprint bp, string title) =>
            bp.Slots?.FirstOrDefault(s => string.Equals(s.SubtaskTitle, title, StringComparison.OrdinalIgnoreCase));

        private static bool IsNearZero(double[]? rot) =>
            rot == null || rot.All(r => Math.Abs(r) < 1e-6);

        private static string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        private static string Vec(double[] v) =>
            $"[{N(v.Length > 0 ? v[0] : 0)}, {N(v.Length > 1 ? v[1] : 0)}, {N(v.Length > 2 ? v[2] : 0)}]";
    }
}
