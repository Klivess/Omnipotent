using System.Text;

namespace Omnipotent.Services.OmniBlox.Build
{
    public sealed record MapFinding(string Severity, string Area, string Message)
    {
        public bool IsError => Severity == "error";
        public override string ToString() => $"[{Severity}] {Area}: {Message}";
    }

    /// <summary>What the map report found: numbers, a 0-100 score, and the specific problems with how to fix them.</summary>
    public sealed class MapAnalysis
    {
        public int Score { get; set; }
        public float AreaWidth { get; set; }
        public float AreaDepth { get; set; }
        public int Objects { get; set; }
        public int DistinctModels { get; set; }
        public double DressedShare { get; set; }
        public List<MapFinding> Findings { get; } = new();
        public List<string> Facts { get; } = new();
        public IEnumerable<MapFinding> Errors => Findings.Where(f => f.IsError);

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Map score {Score}/100 · play area {AreaWidth:0}×{AreaDepth:0} studs · {Objects} objects, {DistinctModels} different models · {DressedShare:P0} of the area dressed.");
            foreach (var f in Facts) sb.AppendLine("- " + f);
            if (Findings.Count == 0) sb.AppendLine("No problems found.");
            foreach (var f in Findings.OrderBy(f => f.IsError ? 0 : 1)) sb.AppendLine(f.ToString());
            return sb.ToString();
        }
    }

    /// <summary>
    /// The agent's written view of its map, and the quality bar the Build stage holds it to. The first studio's maps
    /// were a flat plate with a few trees in one corner, open edges players fell off, and a spawn facing nothing;
    /// nothing measured any of it. This report does: the size of the play area, how much of it is dressed and where
    /// the empty patches are (with coordinates), how full each planned zone is, what a player sees from the spawn,
    /// whether the edges are closed, whether the terrain hides the void, landmarks and variety. Obbies are judged
    /// on their course instead of their acreage.
    /// </summary>
    public static class MapReport
    {
        public const float Cell = 24;
        public const float MinSide = 140;

        public static MapAnalysis Analyze(SceneDocument scene, IReadOnlyList<WorldPart> parts, TerrainMap? terrain, GameManifest manifest)
        {
            var report = new MapAnalysis();
            bool obby = string.Equals(manifest.Template, "obby", StringComparison.OrdinalIgnoreCase);
            var objects = parts.GroupBy(p => p.ObjectId).Select(g => Summarize(g.Key, g.ToList())).ToList();
            var dressing = objects.Where(o => !o.IsGround).ToList();
            report.Objects = dressing.Count;
            report.DistinctModels = scene.Objects.Where(o => o.AssetId != null).Select(o => o.AssetId).Distinct().Count();

            // The play area: as set, else what is built.
            float minX, minZ, maxX, maxZ;
            if (scene.Bounds.HasArea) (minX, minZ, maxX, maxZ) = (scene.Bounds.MinX, scene.Bounds.MinZ, scene.Bounds.MaxX, scene.Bounds.MaxZ);
            else if (dressing.Count > 0) (minX, minZ, maxX, maxZ) = (dressing.Min(o => o.MinX), dressing.Min(o => o.MinZ), dressing.Max(o => o.MaxX), dressing.Max(o => o.MaxZ));
            else (minX, minZ, maxX, maxZ) = (-50, -50, 50, 50);
            report.AreaWidth = maxX - minX;
            report.AreaDepth = maxZ - minZ;
            int score = 100;

            void Add(string severity, string area, string message, int cost)
            {
                report.Findings.Add(new MapFinding(severity, area, message));
                score -= cost;
            }

            // ── edges ── (an obby is a course in the air: falling off is part of it, so these only advise there)
            if (!scene.Bounds.HasArea)
                Add(obby ? "warning" : "error", "edges", obby
                    ? "No play area is set. Falling is part of an obby, but set_map_area around the course lets the kit rescue anyone who falls far below it instead of leaving them in the void."
                    : "No play area is set, so the map has no edges: players can walk or fall off it. Call set_map_area with the rectangle players should stay in (the kit walls it in and rescues anyone who falls).", obby ? 4 : 20);
            if (terrain == null && scene.Terrain.Count == 0 && !parts.Any(p => p.Size.X >= 200 && p.Size.Z >= 200))
                Add(obby ? "warning" : "error", "ground", obby
                    ? "Nothing is below the course: generated terrain far below (terrain_generate, e.g. a lava or ocean biome) or a kill floor makes the world feel finished."
                    : "There is no ground: generate terrain (terrain_generate) so the world has hills, a border and no visible void.", obby ? 6 : 20);
            else if (terrain == null && parts.Any(p => p.Size.X >= 200 && p.Size.Z >= 200))
                Add("warning", "ground", "The ground is one big flat part. Generated terrain (terrain_generate) gives rolling hills, a mountain border that frames the map, paths and water — far more inviting than a plate.", 12);
            if (terrain != null && scene.Bounds.HasArea)
            {
                float margin = MathF.Min(MathF.Min(scene.Bounds.MinX - terrain.OriginX, terrain.MaxX - scene.Bounds.MaxX), MathF.Min(scene.Bounds.MinZ - terrain.OriginZ, terrain.MaxZ - scene.Bounds.MaxZ));
                if (margin < 30)
                    Add("warning", "edges", $"The terrain reaches only {margin:0} studs past the play area on one side: players will see where the world ends. Make the terrain at least 60 studs bigger than the play area on every side.", 6);
            }

            if (!obby)
            {
                // ── size ──
                if (report.AreaWidth < MinSide || report.AreaDepth < MinSide)
                    Add("error", "size", $"The play area is only {report.AreaWidth:0}×{report.AreaDepth:0} studs. A simulator or tycoon needs room to explore: at least {MinSide:0}×{MinSide:0}, typically 300-600 a side with several zones.", 15);

                // ── coverage ──
                int cols = Math.Max(1, (int)MathF.Ceiling(report.AreaWidth / Cell)), rows = Math.Max(1, (int)MathF.Ceiling(report.AreaDepth / Cell));
                var dressed = new bool[cols * rows];
                foreach (var o in dressing)
                {
                    int c0 = Math.Clamp((int)((o.MinX - minX) / Cell), 0, cols - 1), c1 = Math.Clamp((int)((o.MaxX - minX) / Cell), 0, cols - 1);
                    int r0 = Math.Clamp((int)((o.MinZ - minZ) / Cell), 0, rows - 1), r1 = Math.Clamp((int)((o.MaxZ - minZ) / Cell), 0, rows - 1);
                    for (int r = r0; r <= r1; r++) for (int c = c0; c <= c1; c++) dressed[r * cols + c] = true;
                }
                // Painted paths and water count as designed space too.
                if (terrain != null)
                    for (int r = 0; r < rows; r++)
                        for (int c = 0; c < cols; c++)
                        {
                            float x = minX + (c + 0.5f) * Cell, z = minZ + (r + 0.5f) * Cell;
                            if (terrain.InWater(x, z) || !terrain.MaterialAt(x, z).Equals(terrain.MaterialNames[0], StringComparison.OrdinalIgnoreCase)) dressed[r * cols + c] = true;
                        }
                report.DressedShare = dressed.Count(d => d) / (double)dressed.Length;
                var hole = LargestEmpty(dressed, cols, rows);
                if (report.DressedShare < 0.35)
                    Add("error", "emptiness", $"Only {report.DressedShare:P0} of the play area has anything in it. Fill it: scatter_assets for forests, rocks, flowers and props; place_along for fences, lamps and path edging; landmarks in every zone.", 20);
                else if (report.DressedShare < 0.55)
                    Add("warning", "emptiness", $"{report.DressedShare:P0} of the play area is dressed; aim for 60%+ so there is something to see everywhere.", 8);
                if (hole.Area >= 6)
                {
                    float hx0 = minX + hole.C0 * Cell, hz0 = minZ + hole.R0 * Cell, hx1 = minX + (hole.C1 + 1) * Cell, hz1 = minZ + (hole.R1 + 1) * Cell;
                    Add(hole.Area >= 15 ? "warning" : "info", "emptiness", $"Empty patch of {(hx1 - hx0):0}×{(hz1 - hz0):0} studs at x {hx0:0}…{hx1:0}, z {hz0:0}…{hz1:0}. Give it a purpose or dress it (scatter_assets with area [[{hx0:0},{hz0:0}],[{hx1:0},{hz1:0}]]).", hole.Area >= 15 ? 6 : 0);
                }

                // ── zones ──
                var zones = scene.Layout.Zones;
                if (zones.Count == 0)
                    Add("warning", "layout", "There is no layout plan. plan_map the zones (a spawn hub, then areas that open up as players progress), the paths between them and a landmark for each — it is how the map gets a shape players understand.", 8);
                foreach (var zone in zones)
                {
                    int inside = dressing.Count(o => zone.Contains(o.CenterX, o.CenterZ));
                    float area = MathF.Max(1, (zone.Area[2] - zone.Area[0]) * (zone.Area[3] - zone.Area[1]));
                    double per1000 = inside / (area / 1000);
                    report.Facts.Add($"zone {zone.Id} \"{zone.Name}\": {inside} objects ({per1000:0.0} per 1000 sq studs)");
                    if (inside == 0) Add("error", "zone " + zone.Id, $"Zone {zone.Name} is planned but nothing is built in it yet.", 8);
                    else if (per1000 < 0.6) Add("warning", "zone " + zone.Id, $"Zone {zone.Name} is sparse ({inside} objects in {area:0} sq studs). Dress it to its theme ({zone.Theme}).", 4);
                    if (zones.Count > 1 && zone.Order > 1 && !scene.Layout.Paths.Any(p => p.To.Equals(zone.Id, StringComparison.OrdinalIgnoreCase) || p.From.Equals(zone.Id, StringComparison.OrdinalIgnoreCase)))
                        Add("warning", "zone " + zone.Id, $"No path leads to zone {zone.Name}: plan one (plan_map paths) and lay it (terrain_sculpt kind path) so players can find their way.", 3);
                }

                // ── variety and landmarks ──
                if (report.DistinctModels < 6 && dressing.Count > 10)
                    Add("warning", "variety", $"Only {report.DistinctModels} different models: mix 3-4 trees, rocks, bushes, flowers and props per biome so it does not look copy-pasted.", 6);
                int tall = dressing.Count(o => o.Height >= 20);
                if (tall == 0) Add("warning", "landmarks", "Nothing stands taller than 20 studs: add landmarks (a big tree, a tower, a statue, a glowing goal) players can navigate by.", 6);
                else report.Facts.Add($"{tall} landmark(s) taller than 20 studs");
            }

            // ── the spawn ──
            var spawn = scene.Objects.FirstOrDefault(o => o.Kind == SceneKinds.Spawn);
            if (spawn == null)
            {
                Add("error", "spawn", "There is no spawn (add_objects kind spawn), so players appear wherever Roblox puts them.", 15);
            }
            else
            {
                float yaw = spawn.Yaw * MathF.PI / 180;
                float fx = -MathF.Sin(yaw), fz = -MathF.Cos(yaw);
                var ahead = dressing
                    .Where(o => o.Id != spawn.Id)
                    .Select(o => (o, dx: o.CenterX - spawn.Position[0], dz: o.CenterZ - spawn.Position[2]))
                    .Select(t => (t.o, dist: MathF.Sqrt(t.dx * t.dx + t.dz * t.dz), cos: (t.dx * fx + t.dz * fz) / MathF.Max(0.01f, MathF.Sqrt(t.dx * t.dx + t.dz * t.dz))))
                    .Where(t => t.dist < 160 && t.cos > 0.64f)
                    .OrderByDescending(t => t.o.Height / MathF.Max(8, t.dist))
                    .ToList();
                report.Facts.Add($"spawn {spawn.Id} at ({spawn.Position[0]:0}, {spawn.Position[2]:0}) faces yaw {spawn.Yaw:0}; ahead within 160 studs: " +
                    (ahead.Count == 0 ? "nothing" : string.Join(", ", ahead.Take(5).Select(a => $"{a.o.Name} ({a.o.Id}, {a.dist:0} studs)"))));
                if (ahead.Count == 0)
                    Add(obby ? "warning" : "error", "spawn", "A player spawning here looks at nothing. Turn the spawn (update_objects yaw) towards the action, or build the first goal (a shop, the first zone, a landmark) in front of it.", 10);
                if (scene.Bounds.HasArea && !scene.Bounds.Contains(spawn.Position[0], spawn.Position[2]))
                    Add("error", "spawn", "The spawn is outside the play area.", 10);
            }

            report.Score = Math.Clamp(score, 0, 100);
            return report;
        }

        private sealed record ObjectSummary(string Id, string Name, float MinX, float MinZ, float MaxX, float MaxZ, float Height, bool IsGround)
        {
            public float CenterX => (MinX + MaxX) / 2;
            public float CenterZ => (MinZ + MaxZ) / 2;
        }

        private static ObjectSummary Summarize(string id, List<WorldPart> parts)
        {
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (var p in parts)
                foreach (var c in Assets.AssetAnalyzer.Corners(p.CFrame, p.Size))
                {
                    minX = MathF.Min(minX, c.X); maxX = MathF.Max(maxX, c.X);
                    minZ = MathF.Min(minZ, c.Z); maxZ = MathF.Max(maxZ, c.Z);
                    minY = MathF.Min(minY, c.Y); maxY = MathF.Max(maxY, c.Y);
                }
            bool ground = (maxX - minX) >= 150 && (maxZ - minZ) >= 150 && (maxY - minY) < 8;
            return new ObjectSummary(id, parts[0].Name, minX, minZ, maxX, maxZ, maxY - minY, ground);
        }

        /// <summary>The largest all-empty rectangle of cells (maximal rectangle in a histogram, row by row).</summary>
        private static (int Area, int C0, int R0, int C1, int R1) LargestEmpty(bool[] dressed, int cols, int rows)
        {
            var heights = new int[cols];
            (int Area, int C0, int R0, int C1, int R1) best = (0, 0, 0, 0, 0);
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++) heights[c] = dressed[r * cols + c] ? 0 : heights[c] + 1;
                var stack = new Stack<int>();
                for (int c = 0; c <= cols; c++)
                {
                    int h = c == cols ? 0 : heights[c];
                    while (stack.Count > 0 && heights[stack.Peek()] >= h)
                    {
                        int top = stack.Pop();
                        int height = heights[top];
                        int left = stack.Count == 0 ? 0 : stack.Peek() + 1;
                        int width = c - left;
                        if (height * width > best.Area) best = (height * width, left, r - height + 1, c - 1, r);
                    }
                    stack.Push(c);
                }
            }
            return best;
        }
    }
}
