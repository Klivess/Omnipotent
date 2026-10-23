using System.Globalization;
using System.Text;
using Omnipotent.Services.OmniBlox.Rbx;

namespace Omnipotent.Services.OmniBlox.Build
{
    public sealed class PlayabilityReport
    {
        public List<MapFinding> Findings { get; } = new();
        /// <summary>Walking distance in studs from the spawn to things that matter ("sell pad", "zone Pine Forest").</summary>
        public SortedDictionary<string, float> Distances { get; } = new(StringComparer.Ordinal);
        public int NodesReachable { get; set; }
        public int Nodes { get; set; }
        public bool Checked { get; set; }
        public bool Ok => !Findings.Any(f => f.IsError);

        public string Describe()
        {
            if (!Checked) return "Walkability: not checked (no play area or an obby, whose course the jump check covers).";
            var sb = new StringBuilder();
            sb.AppendLine($"Walkability from the spawn: {NodesReachable}/{Nodes} resource nodes reachable" + (Distances.Count > 0 ? "; walking distances: " + string.Join(", ", Distances.Select(d => $"{d.Key} {d.Value:0} studs")) : "") + ".");
            foreach (var f in Findings) sb.AppendLine(f.ToString());
            return sb.ToString();
        }
    }

    /// <summary>
    /// Can a player actually walk to everything? A 3-stud grid over the play area: the ground from the terrain,
    /// solid parts in the way (a library model blocks only round its trunk or base, not under its canopy; tycoon
    /// items not bought yet are not there; zone gates count as passable, since they open), steps no steeper
    /// than a character climbs. A walk from the spawn then finds the sell pad, the stands, every zone's way in
    /// and every resource node, with walking distances the economy check uses. A prop dropped on a road, a
    /// building blocking a gate or a zone cut off by a cliff shows up here before any player finds it.
    /// </summary>
    public static class Playability
    {
        public const float Cell = 3;
        /// <summary>Highest step between neighbouring cells a character walks up (about 47°).</summary>
        public const float MaxStep = 3.2f;

        public static PlayabilityReport Check(SceneDocument scene, IReadOnlyList<WorldPart> parts, TerrainMap? terrain, GameManifest manifest)
        {
            var report = new PlayabilityReport();
            if (!scene.Bounds.HasArea || string.Equals(manifest.Template, "obby", StringComparison.OrdinalIgnoreCase)) return report;
            report.Checked = true;
            float minX = scene.Bounds.MinX, minZ = scene.Bounds.MinZ;
            int cols = Math.Max(1, (int)MathF.Ceiling(scene.Bounds.Width / Cell)), rows = Math.Max(1, (int)MathF.Ceiling(scene.Bounds.Depth / Cell));
            if ((long)cols * rows > 400_000) { report.Checked = false; return report; }
            var ground = new float[cols * rows];
            var blocked = new bool[cols * rows];
            var wet = new bool[cols * rows];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    float x = minX + (c + 0.5f) * Cell, z = minZ + (r + 0.5f) * Cell;
                    ground[r * cols + c] = terrain != null && terrain.Covers(x, z) ? terrain.HeightAt(x, z) : 0;
                    wet[r * cols + c] = terrain != null && terrain.InWater(x, z);
                }

            var byId = scene.Objects.ToDictionary(o => o.Id, o => o, StringComparer.Ordinal);
            foreach (var group in parts.GroupBy(p => p.ObjectId))
            {
                byId.TryGetValue(group.Key, out var obj);
                if (obj != null && (obj.Tags.Contains("ZoneGate", StringComparer.OrdinalIgnoreCase) || obj.Folder.Contains("/Items", StringComparison.OrdinalIgnoreCase))) continue;
                var list = group.ToList();
                bool organic = list[0].AssetId != null && list.Where(p => p.IsMesh).Sum(p => p.Size.X * p.Size.Y * p.Size.Z) >= 0.5f * list.Sum(p => p.Size.X * p.Size.Y * p.Size.Z);
                if (organic)
                {
                    // A tree or a rock: blocks round its middle (the trunk, the base), at most a few studs.
                    float mnX = float.MaxValue, mxX = float.MinValue, mnZ = float.MaxValue, mxZ = float.MinValue, mnY = float.MaxValue;
                    foreach (var p in list.Where(p => p.CanCollide))
                        foreach (var corner in Assets.AssetAnalyzer.Corners(p.CFrame, p.Size))
                        {
                            mnX = MathF.Min(mnX, corner.X); mxX = MathF.Max(mxX, corner.X); mnZ = MathF.Min(mnZ, corner.Z); mxZ = MathF.Max(mxZ, corner.Z); mnY = MathF.Min(mnY, corner.Y);
                        }
                    if (mnX > mxX) continue;
                    float radius = MathF.Min(4.5f, MathF.Max(mxX - mnX, mxZ - mnZ) * 0.22f);
                    Stamp(blocked, cols, rows, minX, minZ, (mnX + mxX) / 2, (mnZ + mxZ) / 2, radius, (x, z) => ground[Index(cols, minX, minZ, x, z)] + 2.2f > mnY);
                    continue;
                }
                foreach (var p in list.Where(p => p.CanCollide && p.Transparency < 0.95f))
                {
                    var h = p.Size * 0.5f;
                    float mnX = float.MaxValue, mxX = float.MinValue, mnZ = float.MaxValue, mxZ = float.MinValue;
                    foreach (var corner in Assets.AssetAnalyzer.Corners(p.CFrame, p.Size))
                    {
                        mnX = MathF.Min(mnX, corner.X); mxX = MathF.Max(mxX, corner.X); mnZ = MathF.Min(mnZ, corner.Z); mxZ = MathF.Max(mxZ, corner.Z);
                    }
                    int c0 = Math.Max(0, (int)((mnX - minX) / Cell)), c1 = Math.Min(cols - 1, (int)((mxX - minX) / Cell));
                    int r0 = Math.Max(0, (int)((mnZ - minZ) / Cell)), r1 = Math.Min(rows - 1, (int)((mxZ - minZ) / Cell));
                    for (int r = r0; r <= r1; r++)
                        for (int c = c0; c <= c1; c++)
                        {
                            int i = r * cols + c;
                            if (blocked[i]) continue;
                            float x = minX + (c + 0.5f) * Cell, z = minZ + (r + 0.5f) * Cell, g = ground[i];
                            // In the way when it fills the space a character's body needs (above step height, below head height).
                            foreach (float y in new[] { g + 2.6f, g + 4.2f })
                            {
                                var local = p.CFrame.PointToObjectSpace(new RbxVector3(x, y, z));
                                if (MathF.Abs(local.X) <= h.X + 0.6f && MathF.Abs(local.Y) <= h.Y && MathF.Abs(local.Z) <= h.Z + 0.6f) { blocked[i] = true; break; }
                            }
                        }
                }
            }

            // Walk from the spawn (Dijkstra on the grid: water is slow).
            var spawn = scene.Objects.FirstOrDefault(o => o.Kind == SceneKinds.Spawn);
            if (spawn == null) { report.Findings.Add(new MapFinding("error", "walking", "There is no spawn to walk from.")); return report; }
            int start = Nearest(cols, rows, minX, minZ, blocked, spawn.Position[0], spawn.Position[2], 4);
            if (start < 0) { report.Findings.Add(new MapFinding("error", "walking", "The spawn is boxed in: there is no open ground beside it.")); return report; }
            var dist = new float[cols * rows];
            Array.Fill(dist, float.MaxValue);
            dist[start] = 0;
            var queue = new PriorityQueue<int, float>();
            queue.Enqueue(start, 0);
            while (queue.TryDequeue(out int i, out float d))
            {
                if (d > dist[i]) continue;
                int r = i / cols, c = i % cols;
                for (int dr = -1; dr <= 1; dr++)
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        if (dr == 0 && dc == 0) continue;
                        int rr = r + dr, cc = c + dc;
                        if (rr < 0 || cc < 0 || rr >= rows || cc >= cols) continue;
                        int j = rr * cols + cc;
                        if (blocked[j] || MathF.Abs(ground[j] - ground[i]) > MaxStep * (dr != 0 && dc != 0 ? 1.4f : 1)) continue;
                        if (dr != 0 && dc != 0 && (blocked[r * cols + cc] || blocked[rr * cols + c])) continue; // no squeezing between corners
                        float step = (dr != 0 && dc != 0 ? 1.414f : 1) * Cell * (wet[j] ? 3 : 1);
                        if (d + step < dist[j]) { dist[j] = d + step; queue.Enqueue(j, d + step); }
                    }
            }
            float DistanceTo(float x, float z, float within)
            {
                float best = float.MaxValue;
                int reach = (int)MathF.Ceiling(within / Cell);
                int cc = (int)((x - minX) / Cell), rr = (int)((z - minZ) / Cell);
                for (int r = rr - reach; r <= rr + reach; r++)
                    for (int c = cc - reach; c <= cc + reach; c++)
                    {
                        if (r < 0 || c < 0 || r >= rows || c >= cols) continue;
                        float cx = minX + (c + 0.5f) * Cell, cz = minZ + (r + 0.5f) * Cell;
                        if ((cx - x) * (cx - x) + (cz - z) * (cz - z) > within * within) continue;
                        best = MathF.Min(best, dist[r * cols + c]);
                    }
                return best;
            }

            foreach (var sell in scene.Objects.Where(o => o.Tags.Contains("SellZone", StringComparer.OrdinalIgnoreCase)))
            {
                float d = DistanceTo(sell.Position[0], sell.Position[2], 9);
                if (d == float.MaxValue) report.Findings.Add(new MapFinding("error", "walking", $"The sell pad {sell.Id} cannot be walked to from the spawn: something blocks the way."));
                else report.Distances["sell pad"] = MathF.Min(report.Distances.GetValueOrDefault("sell pad", float.MaxValue), d);
            }
            foreach (var stand in scene.Objects.Where(o => o.Tags.Contains("Stand", StringComparer.OrdinalIgnoreCase)))
                if (DistanceTo(stand.Position[0], stand.Position[2], 7) == float.MaxValue)
                    report.Findings.Add(new MapFinding("warning", "walking", $"The stand {stand.Id} ({Panel(stand)}) cannot be walked to from the spawn."));
            foreach (var zone in scene.Layout.Zones.Where(z => z.Entrance is { Length: 2 }))
            {
                float d = DistanceTo(zone.Entrance![0], zone.Entrance[1], 12);
                if (d == float.MaxValue) report.Findings.Add(new MapFinding("error", "walking", $"The way into {zone.Name} (at {zone.Entrance[0]:0}, {zone.Entrance[1]:0}) cannot be walked to from the spawn."));
                else report.Distances["zone " + zone.Name] = d;
            }
            var nodes = scene.Objects.Where(o => o.Tags.Contains("Resource", StringComparer.OrdinalIgnoreCase)).ToList();
            report.Nodes = nodes.Count;
            report.NodesReachable = nodes.Count(n => DistanceTo(n.Position[0], n.Position[2], 14) != float.MaxValue);
            if (nodes.Count > 0)
            {
                double share = report.NodesReachable / (double)nodes.Count;
                if (share < 0.6) report.Findings.Add(new MapFinding("error", "walking", $"Only {report.NodesReachable} of {nodes.Count} resource nodes can be walked to from the spawn."));
                else if (share < 0.9) report.Findings.Add(new MapFinding("warning", "walking", $"{nodes.Count - report.NodesReachable} of {nodes.Count} resource nodes cannot be walked to (boxed in by scenery or on a slope too steep)."));
            }
            foreach (var plot in scene.Objects.Where(o => o.Name == "PlotSpawn"))
                if (DistanceTo(plot.Position[0], plot.Position[2], 8) == float.MaxValue)
                    report.Findings.Add(new MapFinding("error", "walking", $"The plot at {plot.Folder} cannot be walked to from the plaza."));
            return report;
        }

        private static string Panel(SceneObject o) => o.Config.TryGetValue("Panel", out var p) ? Convert.ToString(p is Newtonsoft.Json.Linq.JValue jv ? jv.Value : p, CultureInfo.InvariantCulture) ?? "" : "";

        private static int Index(int cols, float minX, float minZ, float x, float z) => Math.Max(0, (int)((z - minZ) / Cell)) * cols + Math.Max(0, Math.Min(cols - 1, (int)((x - minX) / Cell)));

        private static void Stamp(bool[] blocked, int cols, int rows, float minX, float minZ, float x, float z, float radius, Func<float, float, bool> blocks)
        {
            int c0 = Math.Max(0, (int)((x - radius - minX) / Cell)), c1 = Math.Min(cols - 1, (int)((x + radius - minX) / Cell));
            int r0 = Math.Max(0, (int)((z - radius - minZ) / Cell)), r1 = Math.Min(rows - 1, (int)((z + radius - minZ) / Cell));
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                {
                    float cx = minX + (c + 0.5f) * Cell, cz = minZ + (r + 0.5f) * Cell;
                    if ((cx - x) * (cx - x) + (cz - z) * (cz - z) <= radius * radius && blocks(cx, cz)) blocked[r * cols + c] = true;
                }
        }

        private static int Nearest(int cols, int rows, float minX, float minZ, bool[] blocked, float x, float z, int reach)
        {
            int cc = (int)((x - minX) / Cell), rr = (int)((z - minZ) / Cell);
            int best = -1;
            float bestD = float.MaxValue;
            for (int r = rr - reach; r <= rr + reach; r++)
                for (int c = cc - reach; c <= cc + reach; c++)
                {
                    if (r < 0 || c < 0 || r >= rows || c >= cols || blocked[r * cols + c]) continue;
                    float d = (r - rr) * (r - rr) + (c - cc) * (c - cc);
                    if (d < bestD) { bestD = d; best = r * cols + c; }
                }
            return best;
        }
    }
}
