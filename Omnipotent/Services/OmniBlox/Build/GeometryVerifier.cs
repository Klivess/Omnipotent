using Omnipotent.Services.OmniBlox.Assets;
using Omnipotent.Services.OmniBlox.Rbx;

namespace Omnipotent.Services.OmniBlox.Build
{
    public sealed record GeometryIssue(string Severity, string ObjectId, string Message)
    {
        public bool IsError => Severity == "error";
        public override string ToString() => $"[{Severity}] {ObjectId}: {Message}";
    }

    /// <summary>
    /// Deterministic checks on the compiled world — the ground truth the builder's own claims are measured
    /// against. Out-of-bounds parts, floating or half-buried assets, objects jammed into each other, a spawn
    /// with no room, kill bricks by the spawn, a part budget phones can carry, and for obbies whether every
    /// checkpoint can actually be reached by jumping from the spawn.
    /// </summary>
    public static class GeometryVerifier
    {
        /// <summary>Studs a default character (JumpPower 50) can rise in one jump, with a little margin.</summary>
        public const float JumpHeight = 6.8f;
        /// <summary>Horizontal gap a running jump clears comfortably.</summary>
        public const float JumpGap = 11f;
        public const int PartWarning = 6000;
        public const int PartLimit = 15000;

        private static readonly HashSet<string> FloatingAllowed = new(StringComparer.OrdinalIgnoreCase)
        {
            "Mover", "Spinner", "Coin", "Collectible", "Checkpoint", "Finish", "Bounce", "Disappearing", "KillBrick", "Floating",
        };

        private sealed record Box(RbxVector3 Min, RbxVector3 Max)
        {
            public float Top => Max.Y;
            public float Bottom => Min.Y;
            public RbxVector3 Center => (Min + Max) * 0.5f;
            public RbxVector3 Extent => Max - Min;
            public float Volume => MathF.Max(0, Extent.X) * MathF.Max(0, Extent.Y) * MathF.Max(0, Extent.Z);

            public static Box Of(WorldPart p)
            {
                var min = new RbxVector3(float.MaxValue, float.MaxValue, float.MaxValue);
                var max = new RbxVector3(float.MinValue, float.MinValue, float.MinValue);
                foreach (var c in AssetAnalyzer.Corners(p.CFrame, p.Size))
                {
                    min = min.Min(c);
                    max = max.Max(c);
                }
                return new Box(min, max);
            }

            public static Box Union(IEnumerable<Box> boxes)
            {
                var list = boxes.ToList();
                return new Box(list.Select(b => b.Min).Aggregate((a, b) => a.Min(b)), list.Select(b => b.Max).Aggregate((a, b) => a.Max(b)));
            }

            public Box? Intersect(Box o)
            {
                var min = Min.Max(o.Min);
                var max = Max.Min(o.Max);
                return max.X > min.X && max.Y > min.Y && max.Z > min.Z ? new Box(min, max) : null;
            }

            /// <summary>Horizontal (XZ) distance between two boxes; 0 when their footprints overlap.</summary>
            public float GapXZ(Box o)
            {
                float dx = MathF.Max(0, MathF.Max(o.Min.X - Max.X, Min.X - o.Max.X));
                float dz = MathF.Max(0, MathF.Max(o.Min.Z - Max.Z, Min.Z - o.Max.Z));
                return MathF.Sqrt(dx * dx + dz * dz);
            }

            public bool FootprintContains(float x, float z) => x >= Min.X && x <= Max.X && z >= Min.Z && z <= Max.Z;
        }

        public static List<GeometryIssue> Verify(IReadOnlyList<WorldPart> parts, SceneDocument scene, GameManifest manifest, TerrainMap? terrain = null)
        {
            var issues = new List<GeometryIssue>();
            if (parts.Count == 0)
            {
                issues.Add(new("error", "world", "the world is empty"));
                return issues;
            }

            var boxes = parts.Select(Box.Of).ToList();
            var objects = parts.Select((p, i) => (p, i)).GroupBy(x => x.p.ObjectId)
                .ToDictionary(g => g.Key, g => (Parts: g.Select(x => x.p).ToList(), Box: Box.Union(g.Select(x => boxes[x.i]))));
            // Scenery may stand outside the walled play area (it frames the map), but not outside the world.
            var bounds = new MapBounds
            {
                HalfWidth = Math.Max(scene.Bounds.HalfWidth, terrain == null ? 0 : Math.Max(Math.Max(Math.Abs(terrain.OriginX), Math.Abs(terrain.MaxX)), Math.Max(Math.Abs(terrain.OriginZ), Math.Abs(terrain.MaxZ))) + 50),
                MinY = Math.Min(scene.Bounds.MinY, (terrain?.Floor ?? 0) - 20),
                MaxY = scene.Bounds.MaxY,
            };

            // Budget
            if (parts.Count > PartLimit) issues.Add(new("error", "world", $"{parts.Count} parts is too many for phones (limit {PartLimit})"));
            else if (parts.Count > PartWarning) issues.Add(new("warning", "world", $"{parts.Count} parts is heavy for low-end phones (aim under {PartWarning})"));
            int lights = scene.Objects.Count(o => o.Kind == SceneKinds.Light);
            if (lights > 60) issues.Add(new("warning", "world", $"{lights} lights will cost frame rate; keep it under 60"));

            // Bounds
            foreach (var (id, o) in objects)
            {
                var c = o.Box.Center;
                if (MathF.Abs(c.X) > bounds.HalfWidth || MathF.Abs(c.Z) > bounds.HalfWidth || o.Box.Bottom < bounds.MinY || o.Box.Top > bounds.MaxY)
                    issues.Add(new("error", id, $"is outside the playable area (centre {c}); bounds are ±{bounds.HalfWidth} studs, y {bounds.MinY}…{bounds.MaxY}"));
            }

            // Supports: terrain fills act as ground.
            var terrainTops = scene.Terrain.Select(t => new Box(
                new RbxVector3(t.Center[0] - t.Size[0] / 2, t.Center[1] - t.Size[1] / 2, t.Center[2] - t.Size[2] / 2),
                new RbxVector3(t.Center[0] + t.Size[0] / 2, t.Center[1] + t.Size[1] / 2, t.Center[2] + t.Size[2] / 2))).ToList();

            float? SupportBelow(string id, Box box)
            {
                float x = box.Center.X, z = box.Center.Z;
                float? best = null;
                foreach (var (otherId, other) in objects)
                {
                    if (otherId == id) continue;
                    foreach (var p in other.Parts)
                    {
                        if (!p.CanCollide && p.Transparency > 0.9f) continue;
                        var b = Box.Of(p);
                        if (!b.FootprintContains(x, z)) continue;
                        if (b.Top <= box.Bottom + 0.75f && (best == null || b.Top > best)) best = b.Top;
                    }
                }
                foreach (var t in terrainTops)
                    if (t.FootprintContains(x, z) && t.Top <= box.Bottom + 0.75f && (best == null || t.Top > best)) best = t.Top;
                if (terrain != null && terrain.Covers(x, z))
                {
                    float ground = terrain.HeightAt(x, z);
                    if (ground <= box.Bottom + 0.75f && (best == null || ground > best)) best = ground;
                }
                return best;
            }

            foreach (var obj in scene.Objects.Where(o => o.Kind == SceneKinds.Asset))
            {
                if (!objects.TryGetValue(obj.Id, out var o)) continue;
                if (obj.Tags.Any(FloatingAllowed.Contains)) continue;
                float? support = SupportBelow(obj.Id, o.Box);
                float height = MathF.Max(0.1f, o.Box.Extent.Y);
                if (support == null)
                {
                    if (o.Box.Bottom > 1f) issues.Add(new("warning", obj.Id, $"\"{obj.Name}\" floats {o.Box.Bottom:0.#} studs up with nothing under it"));
                }
                else if (o.Box.Bottom - support.Value > 0.6f)
                {
                    issues.Add(new("warning", obj.Id, $"\"{obj.Name}\" floats {o.Box.Bottom - support.Value:0.#} studs above the surface below it (set its y to {support.Value:0.#})"));
                }
                if (terrain != null && terrain.Covers(o.Box.Center.X, o.Box.Center.Z))
                {
                    float ground = terrain.HeightAt(o.Box.Center.X, o.Box.Center.Z);
                    if (ground - o.Box.Bottom > MathF.Max(1.5f, height * 0.35f))
                        issues.Add(new("warning", obj.Id, $"\"{obj.Name}\" is sunk {ground - o.Box.Bottom:0.#} studs into the terrain (the ground there is at y {ground:0.#}); give its position as [x, z] to stand it on the ground"));
                }
                // Half-buried: something solid passes through its middle height at its centre.
                var centreProbe = new Box(new RbxVector3(o.Box.Center.X - 0.1f, o.Box.Bottom, o.Box.Center.Z - 0.1f), new RbxVector3(o.Box.Center.X + 0.1f, o.Box.Bottom + height * 0.35f, o.Box.Center.Z + 0.1f));
                foreach (var (otherId, other) in objects)
                {
                    if (otherId == obj.Id || other.Box.Volume < o.Box.Volume) continue;
                    if (other.Parts.Any(p => p.CanCollide && Box.Of(p).Intersect(centreProbe) != null && Box.Of(p).Top > o.Box.Bottom + height * 0.35f))
                    {
                        issues.Add(new("warning", obj.Id, $"\"{obj.Name}\" is sunk into \"{otherId}\" by more than a third of its height"));
                        break;
                    }
                }
            }

            // Overlaps between solid objects (coarse, then by part).
            var solid = objects.Where(kv => kv.Value.Parts.Any(p => p.CanCollide && p.Transparency < 0.9f)).ToList();
            int overlapReports = 0;
            for (int a = 0; a < solid.Count && overlapReports < 25; a++)
            {
                for (int b = a + 1; b < solid.Count && overlapReports < 25; b++)
                {
                    var A = solid[a].Value;
                    var B = solid[b].Value;
                    var coarse = A.Box.Intersect(B.Box);
                    if (coarse == null) continue;
                    float smaller = MathF.Min(A.Box.Volume, B.Box.Volume);
                    // A floor or base plate the other stands in is expected; ignore very flat partners.
                    if (A.Box.Extent.Y < 2.5f && A.Box.Volume > B.Box.Volume * 4 || B.Box.Extent.Y < 2.5f && B.Box.Volume > A.Box.Volume * 4) continue;
                    float overlap = 0;
                    foreach (var pa in A.Parts.Where(p => p.CanCollide))
                        foreach (var pb in B.Parts.Where(p => p.CanCollide))
                            overlap += Box.Of(pa).Intersect(Box.Of(pb))?.Volume ?? 0;
                    if (smaller > 0 && overlap / smaller > 0.2f)
                    {
                        issues.Add(new("warning", solid[a].Key, $"overlaps \"{solid[b].Key}\" ({overlap / smaller:P0} of the smaller one) — move one of them"));
                        overlapReports++;
                    }
                }
            }

            // Spawns
            var spawnIds = scene.Objects.Where(o => o.Kind == SceneKinds.Spawn).Select(o => o.Id).ToList();
            if (spawnIds.Count == 0) spawnIds.Add("default-spawn");
            foreach (string spawnId in spawnIds)
            {
                if (!objects.TryGetValue(spawnId, out var spawn)) continue;
                if (terrain != null && terrain.Covers(spawn.Box.Center.X, spawn.Box.Center.Z) && terrain.HeightAt(spawn.Box.Center.X, spawn.Box.Center.Z) > spawn.Box.Top + 0.5f)
                    issues.Add(new("error", spawnId, $"the spawn is buried in the terrain (the ground there is at y {terrain.HeightAt(spawn.Box.Center.X, spawn.Box.Center.Z):0.#}); raise it or flatten the ground (terrain_flatten)"));
                var head = new Box(new RbxVector3(spawn.Box.Center.X - 1, spawn.Box.Top + 0.2f, spawn.Box.Center.Z - 1), new RbxVector3(spawn.Box.Center.X + 1, spawn.Box.Top + 6f, spawn.Box.Center.Z + 1));
                foreach (var (otherId, other) in objects)
                {
                    if (otherId == spawnId) continue;
                    if (other.Parts.Any(p => p.CanCollide && Box.Of(p).Intersect(head) != null))
                    {
                        issues.Add(new("error", spawnId, $"players would spawn inside \"{otherId}\"; leave 6 studs of headroom"));
                        break;
                    }
                }
                foreach (var obj in scene.Objects.Where(o => o.Tags.Contains("KillBrick", StringComparer.OrdinalIgnoreCase)))
                    if (objects.TryGetValue(obj.Id, out var kill) && kill.Box.GapXZ(spawn.Box) < 6 && MathF.Abs(kill.Box.Top - spawn.Box.Top) < 4)
                        issues.Add(new("warning", obj.Id, "a kill brick sits right next to the spawn"));
            }

            if (string.Equals(manifest.Template, "obby", StringComparison.OrdinalIgnoreCase))
                CheckObby(scene, objects.ToDictionary(kv => kv.Key, kv => kv.Value.Parts), spawnIds, manifest, issues);

            return issues;
        }

        private static void CheckObby(SceneDocument scene, Dictionary<string, List<WorldPart>> objects, List<string> spawnIds, GameManifest manifest, List<GeometryIssue> issues)
        {
            var checkpoints = new SortedDictionary<int, string>();
            foreach (var obj in scene.Objects.Where(o => o.Tags.Contains("Checkpoint", StringComparer.OrdinalIgnoreCase)))
            {
                if (!obj.Config.TryGetValue("Stage", out var raw) || !int.TryParse(Convert.ToString(raw is Newtonsoft.Json.Linq.JValue jv ? jv.Value : raw, System.Globalization.CultureInfo.InvariantCulture), out int stage) || stage < 1)
                {
                    issues.Add(new("error", obj.Id, "a Checkpoint needs config Stage = 1, 2, 3…"));
                    continue;
                }
                if (checkpoints.ContainsKey(stage)) issues.Add(new("error", obj.Id, $"stage {stage} is used twice"));
                else checkpoints[stage] = obj.Id;
            }
            if (checkpoints.Count < 2)
            {
                issues.Add(new("error", "obby", "an obby needs at least two checkpoints (Stage 1 at the start, then 2, 3…)"));
                return;
            }
            for (int s = 1; s <= checkpoints.Keys.Max(); s++)
                if (!checkpoints.ContainsKey(s)) issues.Add(new("error", "obby", $"stage {s} is missing (stages must run 1..{checkpoints.Keys.Max()} without gaps)"));
            if (!scene.Objects.Any(o => o.Tags.Contains("Finish", StringComparer.OrdinalIgnoreCase)))
                issues.Add(new("error", "obby", "an obby needs a part tagged Finish after the last checkpoint"));

            // Reachability: surfaces a player can stand on, linked when one jump gets from one to the other.
            float jumpScale = manifest.JumpPower > 0 ? (manifest.JumpPower / 50f) * (manifest.JumpPower / 50f) : 1f;
            var surfaces = new List<(string Id, Box Box, bool Bounce, bool Truss, bool Mover)>();
            foreach (var obj in scene.Objects)
            {
                if (!objects.TryGetValue(obj.Id, out var ps)) continue;
                if (obj.Tags.Contains("KillBrick", StringComparer.OrdinalIgnoreCase)) continue;
                bool bounce = obj.Tags.Contains("Bounce", StringComparer.OrdinalIgnoreCase);
                bool mover = obj.Tags.Contains("Mover", StringComparer.OrdinalIgnoreCase);
                foreach (var p in ps)
                {
                    if (!p.CanCollide && !obj.Tags.Any(t => t is "Checkpoint" or "Finish")) continue;
                    var box = Box.Of(p);
                    if (mover && obj.Config.TryGetValue("Offset", out var off) && TryVector(off, out var v))
                        box = new Box(box.Min.Min(box.Min + v), box.Max.Max(box.Max + v));
                    surfaces.Add((obj.Id, box, bounce, p.Shape == "Truss" || obj.Shape?.Equals("truss", StringComparison.OrdinalIgnoreCase) == true, mover));
                }
            }

            var reached = new bool[surfaces.Count];
            var queue = new Queue<int>();
            for (int i = 0; i < surfaces.Count; i++)
                if (spawnIds.Contains(surfaces[i].Id)) { reached[i] = true; queue.Enqueue(i); }
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                var from = surfaces[i];
                float rise = (from.Bounce ? 20f : JumpHeight) * jumpScale;
                for (int j = 0; j < surfaces.Count; j++)
                {
                    if (reached[j]) continue;
                    var to = surfaces[j];
                    float climb = to.Box.Top - from.Box.Top;
                    float gap = from.Box.GapXZ(to.Box);
                    bool ok = to.Truss || from.Truss
                        ? gap <= 2f && climb <= Math.Max(to.Box.Extent.Y, from.Box.Extent.Y) + JumpHeight
                        : climb <= rise && gap <= JumpGap + MathF.Max(0, -climb) * 0.8f;
                    if (ok)
                    {
                        reached[j] = true;
                        queue.Enqueue(j);
                    }
                }
            }
            var reachedIds = surfaces.Where((s, i) => reached[i]).Select(s => s.Id).ToHashSet();
            foreach (var (stage, id) in checkpoints)
                if (!reachedIds.Contains(id))
                    issues.Add(new("warning", id, $"stage {stage} looks unreachable by jumping from the spawn (max rise {JumpHeight} studs, max gap {JumpGap} studs)"));
            foreach (var finish in scene.Objects.Where(o => o.Tags.Contains("Finish", StringComparer.OrdinalIgnoreCase)))
                if (!reachedIds.Contains(finish.Id))
                    issues.Add(new("warning", finish.Id, "the Finish looks unreachable by jumping from the spawn"));
        }

        private static bool TryVector(object raw, out RbxVector3 v)
        {
            v = RbxVector3.Zero;
            if (raw is Newtonsoft.Json.Linq.JArray arr && arr.Count == 3)
            {
                v = new RbxVector3((float)arr[0], (float)arr[1], (float)arr[2]);
                return true;
            }
            string? s = raw is Newtonsoft.Json.Linq.JValue jv ? jv.Value?.ToString() : raw?.ToString();
            var parts = s?.Split(',');
            if (parts is { Length: 3 } && parts.All(x => float.TryParse(x.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)))
            {
                v = new RbxVector3(float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
                return true;
            }
            return false;
        }
    }
}
