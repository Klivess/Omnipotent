using System.Globalization;
using System.Text;
using Omnipotent.Services.OmniBlox.Build.WorldGen;

namespace Omnipotent.Services.OmniBlox.Build
{
    public sealed record MapFinding(string Severity, string Area, string Message)
    {
        public bool IsError => Severity == "error";
        public override string ToString() => $"[{Severity}] {Area}: {Message}";
    }

    /// <summary>One part of the score: what it measures, what it got out of its maximum, and why.</summary>
    public sealed record MapCategory(string Name, int Score, int Max, string Note);

    /// <summary>What the map report found: numbers, a 0-100 score in categories, and the specific problems with how to fix them.</summary>
    public sealed class MapAnalysis
    {
        public int Score { get; set; }
        /// <summary>open (zones round a hub, a free world), obby or tycoon: each is judged on what matters for it.</summary>
        public string Kind { get; set; } = "open";
        public float AreaWidth { get; set; }
        public float AreaDepth { get; set; }
        public int Objects { get; set; }
        public int DistinctModels { get; set; }
        /// <summary>Share of the play area with something in it or near it (studs-accurate footprints, not boxes).</summary>
        public double DressedShare { get; set; }
        public List<MapCategory> Categories { get; } = new();
        /// <summary>Whether a player can walk to everything (null for an obby: its course has its own jump check).</summary>
        public PlayabilityReport? Play { get; set; }
        public List<MapFinding> Findings { get; } = new();
        public List<string> Facts { get; } = new();
        public IEnumerable<MapFinding> Errors => Findings.Where(f => f.IsError);
        /// <summary>A blocking problem caps the score at <see cref="MapReport.BlockedCap"/> however good the rest is.</summary>
        public bool Blocked => Findings.Any(f => f.IsError);

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Map score {Score}/100 ({Kind}; the bar is {MapReport.Bar}) · play area {AreaWidth:0}×{AreaDepth:0} studs · {Objects} objects, {DistinctModels} different models · {DressedShare:P0} of the area dressed.");
            if (Categories.Count > 0)
                sb.AppendLine("Categories: " + string.Join(" · ", Categories.Select(c => $"{c.Name} {c.Score}/{c.Max}" + (c.Note.Length > 0 ? $" ({c.Note})" : ""))));
            if (Blocked) sb.AppendLine($"Blocked: the score is capped at {MapReport.BlockedCap} until every [error] below is fixed.");
            foreach (var f in Facts) sb.AppendLine("- " + f);
            if (Play is { Checked: true } play && play.Distances.Count > 0)
                sb.AppendLine("- walking from the spawn: " + string.Join(", ", play.Distances.Select(d => $"{d.Key} {d.Value:0} studs")) + $"; {play.NodesReachable}/{play.Nodes} nodes reachable");
            if (Findings.Count == 0) sb.AppendLine("No problems found.");
            foreach (var f in Findings.OrderBy(f => f.IsError ? 0 : f.Severity == "warning" ? 1 : 2)) sb.AppendLine(f.ToString());
            return sb.ToString();
        }
    }

    /// <summary>
    /// The map's quality bar, measured. The first version scored a 140-stud strip of flat grass floating in the
    /// sky 100/100: it counted any bounding box touching a 24-stud cell as "dressed", never looked at the ground
    /// or the world's edge, never checked the gameplay was in the map, and turned blocking problems into small
    /// deductions. This one is strict:
    /// <list type="bullet">
    /// <item>Blockers cap the score at 45: no real terrain, a visible edge of the world, no walls, a play area
    /// too small or a corridor, a spawn that sees nothing, geometry errors, gameplay missing from the map
    /// (simulator nodes, the sell pad, a gated zone with nothing in it; tycoon plots; obby stages, the finish,
    /// unreachable stages).</item>
    /// <item>The rest is scored in categories, each with what it measured: ground and world edge, coverage on
    /// an 8-stud grid with real footprints, density and composition (per zone, clustering, height variety),
    /// gameplay layout, landmarks visible from the hub and signposts, variety and one art style, and
    /// correctness (floating, sunk, overlapping, signs and stands facing the wrong way).</item>
    /// </list>
    /// Obbies are judged on their course and tycoons on their plots.
    /// </summary>
    public static class MapReport
    {
        /// <summary>The Build stage's bar.</summary>
        public const int Bar = 75;
        /// <summary>The most a map with a blocking problem can score.</summary>
        public const int BlockedCap = 45;
        public const float Cell = 8;
        public const float MinSide = 200;

        private sealed record Obj(string Id, string Name, string Kind, float MinX, float MinZ, float MaxX, float MaxZ, float MinY, float MaxY, bool IsAsset,
            long? AssetId, IReadOnlyList<string> Tags, SceneObject? Source, bool Plate)
        {
            public float X => (MinX + MaxX) / 2;
            public float Z => (MinZ + MaxZ) / 2;
            public float Height => MaxY - MinY;
            public float Radius => MathF.Max(MaxX - MinX, MaxZ - MinZ) / 2;
            public bool Has(string tag) => Tags.Contains(tag, StringComparer.OrdinalIgnoreCase);
        }

        private static readonly HashSet<string> GameplayTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "Resource", "Collectible", "SellZone", "ZoneGate", "Stand", "Leaderboard", "Checkpoint", "Finish", "KillBrick", "TycoonButton", "TycoonCollector",
            "Mover", "Spinner", "Disappearing", "Bounce", "Coin",
        };

        public static MapAnalysis Analyze(SceneDocument scene, IReadOnlyList<WorldPart> parts, TerrainMap? terrain, GameManifest manifest,
            IReadOnlyList<GeometryIssue>? geometry = null, Func<long, CatalogEntry?>? catalog = null)
        {
            string template = (manifest.Template ?? "").ToLowerInvariant();
            var report = new MapAnalysis { Kind = template == "obby" ? "obby" : template == "tycoon" ? "tycoon" : "open" };
            var byId = scene.Objects.ToDictionary(o => o.Id, o => o, StringComparer.Ordinal);
            var objects = parts.GroupBy(p => p.ObjectId).Select(g => Summarize(g.Key, g.ToList(), byId.GetValueOrDefault(g.Key))).ToList();
            var scenery = objects.Where(o => !o.Plate && o.Kind != SceneKinds.Spawn && !o.Tags.Any(GameplayTags.Contains)).ToList();
            report.Objects = objects.Count(o => !o.Plate);
            report.DistinctModels = scene.Objects.Where(o => o.AssetId != null).Select(o => o.AssetId).Distinct().Count();
            geometry ??= parts.Count > 0 ? GeometryVerifier.Verify(parts, scene, manifest, terrain, catalog) : new List<GeometryIssue>();

            float minX, minZ, maxX, maxZ;
            if (scene.Bounds.HasArea) (minX, minZ, maxX, maxZ) = (scene.Bounds.MinX, scene.Bounds.MinZ, scene.Bounds.MaxX, scene.Bounds.MaxZ);
            else if (objects.Count > 0) (minX, minZ, maxX, maxZ) = (objects.Min(o => o.MinX), objects.Min(o => o.MinZ), objects.Max(o => o.MaxX), objects.Max(o => o.MaxZ));
            else (minX, minZ, maxX, maxZ) = (-50, -50, 50, 50);
            report.AreaWidth = maxX - minX;
            report.AreaDepth = maxZ - minZ;
            var ctx = new Context(scene, terrain, manifest, objects, scenery, geometry, catalog, report, minX, minZ, maxX, maxZ);

            switch (report.Kind)
            {
                case "obby": Obby(ctx); break;
                case "tycoon": Open(ctx, tycoon: true); break;
                default: Open(ctx, tycoon: false); break;
            }
            if (report.Kind != "obby" && parts.Count > 0)
            {
                // Can a player actually walk to it all? A blocked sell pad or zone is a blocker like any other.
                report.Play = Playability.Check(scene, parts, terrain, manifest);
                report.Findings.AddRange(report.Play.Findings);
            }

            int total = report.Categories.Sum(c => c.Score);
            report.Score = Math.Clamp(report.Blocked ? Math.Min(total, BlockedCap) : total, 0, 100);
            return report;
        }

        private sealed record Context(SceneDocument Scene, TerrainMap? Terrain, GameManifest Manifest, List<Obj> Objects, List<Obj> Scenery,
            IReadOnlyList<GeometryIssue> Geometry, Func<long, CatalogEntry?>? Catalog, MapAnalysis Report, float MinX, float MinZ, float MaxX, float MaxZ)
        {
            public float Width => MaxX - MinX;
            public float Depth => MaxZ - MinZ;
            public void Error(string area, string message) => Report.Findings.Add(new MapFinding("error", area, message));
            public void Warn(string area, string message) => Report.Findings.Add(new MapFinding("warning", area, message));
            public void Info(string area, string message) => Report.Findings.Add(new MapFinding("info", area, message));
            public void Category(string name, double fraction, int max, string note) =>
                Report.Categories.Add(new MapCategory(name, (int)Math.Round(Math.Clamp(fraction, 0, 1) * max), max, note));
            public float Ground(float x, float z) => Terrain != null && Terrain.Covers(x, z) ? Terrain.HeightAt(x, z) : 0;
        }

        // ─────────────── open worlds (simulators, sandboxes) and tycoons ───────────────

        private static void Open(Context c, bool tycoon)
        {
            var scene = c.Scene;
            bool simulator = string.Equals(c.Manifest.Template, "simulator", StringComparison.OrdinalIgnoreCase);

            // ── blockers ──
            if (!scene.Bounds.HasArea)
                c.Error("edges", "No play area is set, so the map has no edges: players can walk or fall off it. Generate the world (design_world) or call set_map_area.");
            if (c.Terrain == null)
                c.Error("ground", scene.Terrain.Count > 0 || c.Objects.Any(o => o.Plate)
                    ? "The ground is a flat slab (a terrain fill or a big part) floating in the void: from anywhere on the map players see where the world ends. Generate real terrain with hills and border mountains (design_world, or terrain_generate)."
                    : "There is no ground. Generate the world (design_world) or terrain (terrain_generate).");
            if (c.Width < MinSide || c.Depth < MinSide)
                c.Error("size", $"The play area is only {c.Width:0}×{c.Depth:0} studs. A {(tycoon ? "tycoon" : "simulator")} needs room: at least {MinSide:0}×{MinSide:0}, typically 400-600 a side with zones round a hub.");
            float aspect = MathF.Max(c.Width, c.Depth) / MathF.Max(1, MathF.Min(c.Width, c.Depth));
            if (aspect > 2.2f)
                c.Error("shape", $"The play area is a corridor ({c.Width:0}×{c.Depth:0}, {aspect:0.#}:1): players walk one line and see two walls. Lay zones round a hub instead (design_world layout hub).");

            // ── ground and the world's edge ──
            double relief = 0, framed = 0;
            int materials = 0;
            float margin = float.MaxValue;
            if (c.Terrain is { } t)
            {
                margin = MathF.Min(MathF.Min(c.MinX - t.OriginX, t.MaxX - c.MaxX), MathF.Min(c.MinZ - t.OriginZ, t.MaxZ - c.MaxZ));
                if (margin < 40)
                    c.Error("edges", $"The terrain reaches only {margin:0} studs past the play area on one side: players see the world end. Make it 100+ studs bigger than the play area on every side.");
                else if (margin < 90)
                    c.Warn("edges", $"The terrain reaches only {margin:0} studs past the play area on one side; 100+ hides the edge of the world from every hill.");
                var heights = new List<float>();
                var mats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (float x = c.MinX + 6; x < c.MaxX; x += 12)
                    for (float z = c.MinZ + 6; z < c.MaxZ; z += 12)
                    {
                        heights.Add(t.HeightAt(x, z));
                        mats.Add(t.MaterialAt(x, z));
                    }
                materials = mats.Count;
                if (heights.Count > 1)
                {
                    double mean = heights.Average();
                    relief = Math.Sqrt(heights.Sum(h => (h - mean) * (h - mean)) / heights.Count);
                }
                framed = Framing(c);
            }
            if (c.Terrain != null && relief < 1.2)
                c.Warn("ground", $"The land is almost perfectly flat (heights vary by {relief:0.#} studs): rolling ground, raised zones and hills between them read as a place, a flat field as a plate.");
            if (c.Terrain != null && framed < 0.75)
                c.Warn("edges", $"Only {framed:P0} of the play area's edge is framed by hills, mountains or trees: the rest shows open land ending at an invisible wall.");
            c.Category("Ground & edge", (Math.Min(relief / 3.0, 1) * 0.35 + Math.Min(materials / 3.0, 1) * 0.25 + framed * 0.4) * (c.Terrain == null ? 0 : 1), 12,
                c.Terrain == null ? "no terrain" : $"relief {relief:0.#}, {materials} materials, {framed:P0} of the edge framed");

            // ── coverage ──
            var (covered, hole) = Coverage(c);
            c.Report.DressedShare = covered;
            if (covered < 0.4)
                c.Error("emptiness", $"Only {covered:P0} of the play area has anything in it or near it: it is a barren field. Regenerate the world with more density (design_world density) or dress it (scatter_assets).");
            else if (covered < 0.65)
                c.Warn("emptiness", $"{covered:P0} of the play area has something in it or near it; aim for 80%+ (groves, rocks and flowers between the zones, props by every path).");
            if (hole.Area > 0)
            {
                float share = hole.Area * Cell * Cell / MathF.Max(1, c.Width * c.Depth);
                string at = $"x {hole.X0:0}…{hole.X1:0}, z {hole.Z0:0}…{hole.Z1:0}";
                if (share > 0.15f) c.Error("emptiness", $"A barren patch of {hole.X1 - hole.X0:0}×{hole.Z1 - hole.Z0:0} studs ({share:P0} of the map) at {at}. Give it a purpose or dress it (scatter_assets area [[{hole.X0:0},{hole.Z0:0}],[{hole.X1:0},{hole.Z1:0}]]).");
                else if (share > 0.05f) c.Warn("emptiness", $"Empty patch of {hole.X1 - hole.X0:0}×{hole.Z1 - hole.Z0:0} studs at {at}. Dress it (scatter_assets area [[{hole.X0:0},{hole.Z0:0}],[{hole.X1:0},{hole.Z1:0}]]).");
            }
            c.Category("Coverage", (covered - 0.45) / (0.85 - 0.45), 18, $"{covered:P0} of the area dressed");

            // ── density and composition ──
            var zones = scene.Layout.Zones.Where(z => !z.Id.Equals("hub", StringComparison.OrdinalIgnoreCase) && !z.Id.Equals("plaza", StringComparison.OrdinalIgnoreCase)).ToList();
            double zoneDensity = 1;
            if (zones.Count > 0)
            {
                var densities = new List<double>();
                foreach (var z in zones)
                {
                    var (zx, zz, zr) = Circle(z);
                    float ring = zr + 16;
                    int count = c.Scenery.Count(o => (o.X - zx) * (o.X - zx) + (o.Z - zz) * (o.Z - zz) < ring * ring);
                    double per1000 = count / (MathF.PI * ring * ring / 1000);
                    densities.Add(per1000);
                    c.Report.Facts.Add($"zone {z.Id} \"{z.Name}\": {count} scenery objects in and round it ({per1000:0.0} per 1000 sq studs)");
                    if (count == 0) c.Error("zone " + z.Id, $"Zone {z.Name} has no scenery at all: nothing frames it or tells its theme ({z.Theme}).");
                    else if (per1000 < 0.9) c.Warn("zone " + z.Id, $"Zone {z.Name} is sparse ({per1000:0.0} scenery objects per 1000 sq studs in and round it; aim for 2+): ring it with trees and add its biome's touches.");
                }
                zoneDensity = densities.Average(d => Math.Min(d / 2.0, 1));
            }
            double clustering = Clustering(c, out double r);
            double tallShare = c.Scenery.Count == 0 ? 0 : c.Scenery.Count(o => o.Height >= 10) / (double)c.Scenery.Count;
            if (c.Scenery.Count > 30 && tallShare < 0.12)
                c.Warn("composition", $"Only {tallShare:P0} of the scenery stands taller than 10 studs: add trees and boulders so the land has height, not just ground cover.");
            c.Category("Density & composition", zoneDensity * 0.5 + clustering * 0.3 + Math.Min(tallShare / 0.25, 1) * 0.2, 14,
                $"zones {zoneDensity:P0} of target, clustering R {r:0.00}, {tallShare:P0} tall");

            // ── gameplay layout ──
            var spawn = scene.Objects.FirstOrDefault(o => o.Kind == SceneKinds.Spawn);
            double gameplay = tycoon ? Tycoon(c, spawn) : simulator ? Simulator(c, spawn, zones) : Sandbox(c, zones);
            Spawn(c, spawn, required: true);

            // ── landmarks and navigation ──
            double landmarks = Landmarks(c, zones);

            // ── variety and style ──
            double variety = Variety(c, zones);

            // ── correctness ──
            Correctness(c, spawn, 16);
            _ = gameplay + landmarks + variety;
        }

        private static double Simulator(Context c, SceneObject? spawn, List<MapZone> zones)
        {
            var nodes = c.Objects.Where(o => o.Has("Resource")).ToList();
            // Kit-built nodes are tagged folders, not objects.
            var nodeFolders = c.Scene.Folders.Where(kv => kv.Value.Tags.Contains("Resource", StringComparer.OrdinalIgnoreCase)).ToList();
            var collectibles = c.Objects.Count(o => o.Has("Collectible") || o.Has("Coin"));
            var sells = c.Objects.Where(o => o.Has("SellZone")).ToList();
            var gates = c.Scene.Objects.Where(o => o.Tags.Contains("ZoneGate", StringComparer.OrdinalIgnoreCase)).ToList();
            int nodeCount = nodes.Count + nodeFolders.Count;
            double score = 0;
            if (nodeCount == 0 && collectibles == 0)
                c.Error("gameplay", "There is nothing to harvest in the map: a simulator needs resource nodes (models tagged Resource with Zone, Hp, Value) in every zone. Generate the world (design_world) — it places them.");
            else score += 0.25;
            if (sells.Count == 0) c.Error("gameplay", "There is no sell pad (a part tagged SellZone): players can fill their bag but never cash it in.");
            else if (spawn != null && sells.Min(s => Dist(s.X, s.Z, spawn.Position[0], spawn.Position[2])) > 80)
                c.Warn("gameplay", $"The sell pad is {sells.Min(s => Dist(s.X, s.Z, spawn.Position[0], spawn.Position[2])):0} studs from the spawn: keep it within 60 so the first sale is quick.");
            else score += 0.2;
            var stands = c.Scene.Objects.Where(o => o.Tags.Contains("Stand", StringComparer.OrdinalIgnoreCase)).ToList();
            if (!stands.Any(s => s.Config.TryGetValue("Panel", out var p) && string.Equals(Convert.ToString(p is Newtonsoft.Json.Linq.JValue jv ? jv.Value : p, CultureInfo.InvariantCulture), "upgrades", StringComparison.OrdinalIgnoreCase)))
                c.Warn("gameplay", "No upgrade stand in the hub: players find upgrades only in the side menu. A stand (tag Stand, Panel upgrades) shows them where to go.");
            else score += 0.1;
            // Every zone: its nodes, and its gate if it costs anything.
            var byZone = nodes.Select(n => n.Source?.Config is { } cfg && cfg.TryGetValue("Zone", out var z) ? Convert.ToString(z is Newtonsoft.Json.Linq.JValue jv ? jv.Value : z, CultureInfo.InvariantCulture) : null)
                .Concat(nodeFolders.Select(f => f.Value.Config.TryGetValue("Zone", out var z) ? Convert.ToString(z is Newtonsoft.Json.Linq.JValue jv ? jv.Value : z, CultureInfo.InvariantCulture) : null))
                .GroupBy(z => z ?? "").ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            int good = 0;
            foreach (var z in zones)
            {
                int n = byZone.GetValueOrDefault(z.Name);
                bool gated = gates.Any(g => g.Config.TryGetValue("Zone", out var v) && string.Equals(Convert.ToString(v is Newtonsoft.Json.Linq.JValue jv ? jv.Value : v, CultureInfo.InvariantCulture), z.Name, StringComparison.OrdinalIgnoreCase));
                if (n == 0 && nodeCount > 0) c.Error("zone " + z.Id, $"Zone {z.Name} has no resource nodes: unlocking it gives players nothing to do.");
                else if (n > 0 && n < 6) c.Warn("zone " + z.Id, $"Zone {z.Name} has only {n} resource nodes; 8-16 keep several players busy.");
                else if (n > 0) good++;
                if (z.Gated && !gated) c.Error("zone " + z.Id, $"Zone {z.Name} is meant to be unlocked but no ZoneGate guards it.");
                if (!c.Scene.Layout.Paths.Any(p => p.To.Equals(z.Id, StringComparison.OrdinalIgnoreCase) || p.From.Equals(z.Id, StringComparison.OrdinalIgnoreCase)))
                    c.Warn("zone " + z.Id, $"No road leads to zone {z.Name}: players will not find it.");
            }
            if (zones.Count > 0) score += 0.3 * good / zones.Count;
            else if (nodeCount > 0) score += 0.2;
            // The first reward: a node or coin close to the spawn.
            if (spawn != null)
            {
                var first = nodes.Concat(c.Objects.Where(o => o.Has("Collectible") || o.Has("Coin")))
                    .Select(o => Dist(o.X, o.Z, spawn.Position[0], spawn.Position[2])).DefaultIfEmpty(float.MaxValue).Min();
                if (first > 200) c.Warn("gameplay", $"The nearest thing to harvest is {(first == float.MaxValue ? "nowhere" : first.ToString("0", CultureInfo.InvariantCulture) + " studs")} from the spawn: the first reward should be under a minute's walk (~150 studs).");
                else score += 0.15;
            }
            c.Report.Facts.Add($"{nodeCount} resource node(s){(collectibles > 0 ? $", {collectibles} collectible(s)" : "")}, {sells.Count} sell pad(s), {gates.Count} zone gate(s), {stands.Count} stand mat(s)");
            c.Category("Gameplay layout", score, 20, $"{nodeCount} nodes, {good}/{zones.Count} zones stocked");
            return score;
        }

        private static double Tycoon(Context c, SceneObject? spawn)
        {
            var plots = c.Scene.Folders.Where(kv => kv.Value.Tags.Contains("TycoonPlot", StringComparer.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
            double score = 0;
            if (plots.Count == 0)
            {
                c.Error("gameplay", "There are no tycoon plots (a folder tagged TycoonPlot with an Items folder, buttons and a collector). Generate the world (design_world layout plots).");
                c.Category("Gameplay layout", 0, 20, "no plots");
                return 0;
            }
            int complete = 0;
            foreach (string plot in plots)
            {
                var inPlot = c.Scene.Objects.Where(o => o.Folder.Equals(plot, StringComparison.OrdinalIgnoreCase) || o.Folder.StartsWith(plot + "/", StringComparison.OrdinalIgnoreCase)).ToList();
                int buttons = inPlot.Count(o => o.Tags.Contains("TycoonButton", StringComparer.OrdinalIgnoreCase));
                int items = c.Scene.Folders.Keys.Count(k => k.StartsWith(plot + "/Items/", StringComparison.OrdinalIgnoreCase)) + inPlot.Count(o => o.Folder.Equals(plot + "/Items", StringComparison.OrdinalIgnoreCase));
                bool collector = inPlot.Any(o => o.Tags.Contains("TycoonCollector", StringComparer.OrdinalIgnoreCase));
                if (buttons < 6 || items < 6) c.Warn("plot", $"{plot} has {buttons} buttons and {items} items: 10-14 keep a player building for an hour.");
                if (!collector) c.Error("plot", $"{plot} has no collector (a part tagged TycoonCollector): its income can never be banked.");
                if (buttons >= 6 && items >= 6 && collector) complete++;
            }
            score += 0.6 * complete / plots.Count;
            if (plots.Count >= 2) score += 0.15;
            if (c.Scene.Objects.Any(o => o.Tags.Contains("Stand", StringComparer.OrdinalIgnoreCase))) score += 0.1;
            if (c.Scene.Objects.Any(o => o.Tags.Contains("Leaderboard", StringComparer.OrdinalIgnoreCase))) score += 0.15;
            c.Report.Facts.Add($"{plots.Count} plot(s), {complete} complete");
            c.Category("Gameplay layout", score, 20, $"{complete}/{plots.Count} plots complete");
            return score;
        }

        private static double Sandbox(Context c, List<MapZone> zones)
        {
            double score = zones.Count == 0 ? 0.4 : 0.4 + 0.6 * zones.Count(z =>
            {
                var (zx, zz, zr) = Circle(z);
                return c.Objects.Any(o => o.Tags.Any(GameplayTags.Contains) && Dist(o.X, o.Z, zx, zz) < zr);
            }) / zones.Count;
            if (zones.Count == 0) c.Warn("layout", "There is no layout plan: plan_map (or design_world) gives the map zones players move through.");
            c.Category("Gameplay layout", score, 20, $"{zones.Count} zone(s)");
            return score;
        }

        private static void Spawn(Context c, SceneObject? spawn, bool required)
        {
            if (spawn == null)
            {
                if (required) c.Error("spawn", "There is no spawn, so players appear wherever Roblox puts them.");
                return;
            }
            if (c.Scene.Bounds.HasArea && !c.Scene.Bounds.Contains(spawn.Position[0], spawn.Position[2]))
                c.Error("spawn", "The spawn is outside the play area.");
            float yaw = spawn.Yaw * MathF.PI / 180;
            float fx = -MathF.Sin(yaw), fz = -MathF.Cos(yaw);
            var ahead = c.Objects.Where(o => !o.Plate && o.Id != spawn.Id && o.Height >= 3)
                .Select(o => (o, dx: o.X - spawn.Position[0], dz: o.Z - spawn.Position[2]))
                .Select(t => (t.o, dist: MathF.Sqrt(t.dx * t.dx + t.dz * t.dz), cos: (t.dx * fx + t.dz * fz) / MathF.Max(0.01f, MathF.Sqrt(t.dx * t.dx + t.dz * t.dz))))
                .Where(t => t.dist < 90 && t.cos > 0.55f)
                .OrderBy(t => t.dist).ToList();
            c.Report.Facts.Add($"spawn {spawn.Id} at ({spawn.Position[0]:0}, {spawn.Position[2]:0}) faces yaw {spawn.Yaw:0}; ahead within 90 studs: " +
                (ahead.Count == 0 ? "nothing" : string.Join(", ", ahead.Take(5).Select(a => $"{a.o.Name} ({a.dist:0} studs)"))));
            if (ahead.Count == 0)
                c.Error("spawn", "A player spawning here looks at nothing: turn the spawn towards the hub's centre and the first zone (update_objects yaw), or build the first goal in front of it.");
        }

        private static double Landmarks(Context c, List<MapZone> zones)
        {
            if (zones.Count == 0)
            {
                int tall = c.Objects.Count(o => !o.Plate && o.Height >= 25);
                c.Category("Landmarks & navigation", Math.Min(tall / 3.0, 1), 10, $"{tall} landmark(s)");
                return 0;
            }
            int marked = 0, seen = 0;
            var hub = c.Scene.Layout.Zones.FirstOrDefault(z => z.Id.Equals("hub", StringComparison.OrdinalIgnoreCase) || z.Id.Equals("plaza", StringComparison.OrdinalIgnoreCase));
            var (hx, hz, _) = hub != null ? Circle(hub) : (0, 0, 0);
            foreach (var z in zones)
            {
                var (zx, zz, zr) = Circle(z);
                var tallest = c.Objects.Where(o => !o.Plate && Dist(o.X, o.Z, zx, zz) < zr + 18).OrderByDescending(o => o.MaxY).FirstOrDefault();
                if (tallest == null || tallest.Height < 25)
                {
                    c.Warn("landmarks", $"Zone {z.Name} has nothing taller than 25 studs: give it a landmark (a giant tree, a tower, a crystal) players can head for.");
                    continue;
                }
                marked++;
                if (hub == null || Visible(c, hx, hz, tallest)) seen++;
                else c.Info("landmarks", $"Zone {z.Name}'s landmark ({tallest.Name}) is hidden from the hub by the land.");
            }
            int signs = c.Scene.Objects.Count(o => o.Kind == SceneKinds.Text);
            if (signs < zones.Count) c.Warn("navigation", $"{signs} sign(s) for {zones.Count} zones: a signpost where each road leaves the hub tells players where it goes.");
            double score = 0.5 * marked / zones.Count + 0.3 * seen / zones.Count + 0.2 * Math.Min(1, signs / (double)zones.Count);
            c.Category("Landmarks & navigation", score, 10, $"{marked}/{zones.Count} zones marked, {seen} seen from the hub, {signs} signs");
            return score;
        }

        private static double Variety(Context c, List<MapZone> zones)
        {
            int distinct = c.Report.DistinctModels;
            double score = Math.Min(distinct / 10.0, 1) * 0.5;
            if (distinct < 6 && c.Scenery.Count > 20)
                c.Warn("variety", $"Only {distinct} different models: mix 3-4 trees, bushes, rocks and flowers per biome so it does not look copy-pasted.");
            var perZone = zones.Select(z =>
            {
                var (zx, zz, zr) = Circle(z);
                return c.Objects.Where(o => o.AssetId != null && Dist(o.X, o.Z, zx, zz) < zr + 16).Select(o => o.AssetId).Distinct().Count();
            }).ToList();
            if (perZone.Count > 0) score += 0.25 * perZone.Average(n => Math.Min(n / 4.0, 1));
            else score += 0.25;
            if (c.Catalog != null)
            {
                var entries = c.Objects.Where(o => o.AssetId != null).Select(o => c.Catalog(o.AssetId!.Value)).OfType<CatalogEntry>().ToList();
                if (entries.Count > 0)
                {
                    var family = entries.GroupBy(e => e.Style).OrderByDescending(g => g.Count()).First();
                    double share = family.Count() / (double)entries.Count;
                    if (share < 0.8) c.Warn("style", $"Only {share:P0} of the models share one art style ({family.Key}); mixing realistic and cartoon models makes a map look patched together.");
                    score += 0.25 * Math.Min(1, share / 0.8);
                    foreach (var grey in entries.Where(e => e.Untextured).GroupBy(e => e.AssetId).Take(3))
                        c.Warn("style", $"Model #{grey.Key} ({grey.First().Name}) is a PBR model whose textures the publishing API strips: in the game it is plain grey. Use another.");
                }
                else score += 0.25;
            }
            else score += 0.25;
            c.Category("Variety & style", score, 10, $"{distinct} models");
            return score;
        }

        private static void Correctness(Context c, SceneObject? spawn, int max)
        {
            var issues = c.Geometry.Where(g => !g.IsError).ToList();
            foreach (var e in c.Geometry.Where(g => g.IsError).Take(8)) c.Error("geometry", $"{e.ObjectId}: {e.Message}");
            int floating = issues.Count(i => i.Message.Contains("floats", StringComparison.Ordinal));
            int sunk = issues.Count(i => i.Message.Contains("sunk", StringComparison.Ordinal));
            int overlaps = issues.Count(i => i.Message.Contains("overlaps", StringComparison.Ordinal));
            foreach (var i in issues.Where(i => i.Message.Contains("floats", StringComparison.Ordinal) || i.Message.Contains("sunk", StringComparison.Ordinal)).Take(6))
                c.Warn("placement", $"{i.ObjectId}: {i.Message}");
            if (floating + sunk > 6) c.Error("placement", $"{floating} object(s) float and {sunk} are sunk into the ground or each other: everything must stand on the ground (give positions as [x, z], or regenerate the world).");
            if (overlaps > 0) c.Info("placement", $"{overlaps} overlapping pair(s) (geometry check lists them).");
            // Signs and stands face where people are.
            int facingAway = 0;
            var hub = c.Scene.Layout.Zones.FirstOrDefault(z => z.Id is "hub" or "plaza");
            foreach (var sign in c.Scene.Objects.Where(o => o.Kind == SceneKinds.Text && o.TwoSided != true))
            {
                var target = NearestPathPoint(c.Scene, sign.Position[0], sign.Position[2]) ?? (hub != null ? (Circle(hub).X, Circle(hub).Z) : ((float, float)?)null);
                if (target is not { } tp || Dist(tp.Item1, tp.Item2, sign.Position[0], sign.Position[2]) < 3) continue;
                float yaw = sign.Yaw * MathF.PI / 180;
                float fx = -MathF.Sin(yaw), fz = -MathF.Cos(yaw);
                float dx = tp.Item1 - sign.Position[0], dz = tp.Item2 - sign.Position[2], len = MathF.Sqrt(dx * dx + dz * dz);
                if ((dx * fx + dz * fz) / len < -0.2f)
                {
                    facingAway++;
                    if (facingAway <= 3) c.Warn("facing", $"Sign {sign.Id} (\"{sign.Text}\") faces away from the nearest path: its text is on the side nobody walks. Turn it (yaw) or make it two-sided.");
                }
            }
            double score = 1 - Math.Min(1, (floating + sunk) * 0.12 + overlaps * 0.02 + facingAway * 0.08);
            c.Category("Correctness", score, max, $"{floating} floating, {sunk} sunk, {overlaps} overlaps, {facingAway} signs facing away");
        }

        // ─────────────── obbies ───────────────

        private static void Obby(Context c)
        {
            var scene = c.Scene;
            var checkpoints = scene.Objects.Where(o => o.Tags.Contains("Checkpoint", StringComparer.OrdinalIgnoreCase)).ToList();
            var finish = scene.Objects.Where(o => o.Tags.Contains("Finish", StringComparer.OrdinalIgnoreCase)).ToList();
            var unreachable = c.Geometry.Where(g => g.Message.Contains("unreachable", StringComparison.Ordinal)).ToList();
            if (checkpoints.Count < 2) c.Error("course", "An obby needs checkpoints (parts tagged Checkpoint with Stage = 1, 2, 3…): generate the course (design_world layout course).");
            if (finish.Count == 0) c.Error("course", "There is no finish (a part tagged Finish).");
            foreach (var u in unreachable.Take(5)) c.Error("course", $"{u.ObjectId}: {u.Message}");
            if (checkpoints.Count is > 1 and < 10) c.Warn("course", $"Only {checkpoints.Count} stages: 25-50 give an obby an hour of play.");
            var mechanics = new[] { "Mover", "Disappearing", "Spinner", "Bounce", "KillBrick" }.Where(t => scene.Objects.Any(o => o.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))).ToList();
            if (scene.Objects.Any(o => string.Equals(o.Shape, "truss", StringComparison.OrdinalIgnoreCase))) mechanics.Add("Truss");
            if (mechanics.Count < 3 && checkpoints.Count >= 10) c.Warn("course", $"The course uses {mechanics.Count} kinds of obstacle ({string.Join(", ", mechanics)}): mix in moving, vanishing, spinning and bouncing ones, one new kind at a time.");
            c.Category("Course", Math.Min(checkpoints.Count / 25.0, 1) * 0.45 + Math.Min(mechanics.Count / 5.0, 1) * 0.35 + (finish.Count > 0 ? 0.2 : 0), 40,
                $"{checkpoints.Count} stages, {mechanics.Count} obstacle kinds");

            // Under and round the course.
            bool ground = c.Terrain != null;
            bool killFloor = scene.Objects.Any(o => o.Tags.Contains("KillBrick", StringComparer.OrdinalIgnoreCase) && o.Size is { Length: 3 } s && s[0] > 100 && s[2] > 100);
            if (!ground) c.Error("scenery", "The course floats in a void with nothing below or round it: generate the land (design_world layout course) so it runs over a lava valley between hills.");
            if (!killFloor && !ground) c.Warn("scenery", "Nothing below the course stops a fall: a kill floor (a big part tagged KillBrick) sends fallers back to their checkpoint at once.");
            int around = c.Scenery.Count(o => o.IsAsset);
            double framed = ground ? Framing(c) : 0;
            c.Category("Scenery", (ground ? 0.35 : 0) + Math.Min(around / 120.0, 1) * 0.4 + framed * 0.25, 25, $"{around} scenery models, {framed:P0} of the edge framed");

            // The start.
            var spawn = scene.Objects.FirstOrDefault(o => o.Kind == SceneKinds.Spawn);
            double start = 0;
            if (spawn == null) c.Error("spawn", "There is no spawn.");
            else
            {
                var first = checkpoints.OrderBy(cp => cp.Config.TryGetValue("Stage", out var st) ? Convert.ToInt32(st is Newtonsoft.Json.Linq.JValue jv ? jv.Value : st, CultureInfo.InvariantCulture) : 999).FirstOrDefault();
                float d = first == null ? float.MaxValue : Dist(first.Position[0], first.Position[2], spawn.Position[0], spawn.Position[2]);
                if (d > 60) c.Warn("spawn", $"The first checkpoint is {(d == float.MaxValue ? "missing" : d.ToString("0", CultureInfo.InvariantCulture) + " studs")} from the spawn: players should start the course straight away.");
                else start += 0.6;
                if (scene.Objects.Any(o => o.Tags.Contains("Stand", StringComparer.OrdinalIgnoreCase) || o.Tags.Contains("Leaderboard", StringComparer.OrdinalIgnoreCase))) start += 0.2;
                if (scene.Objects.Any(o => o.Kind == SceneKinds.Text)) start += 0.2;
            }
            c.Category("Start", start, 15, spawn == null ? "no spawn" : "");
            Correctness(c, spawn, 20);
            c.Report.DressedShare = Coverage(c).Covered;
            c.Report.Facts.Add($"{checkpoints.Count} checkpoints, {finish.Count} finish, obstacles: {string.Join(", ", mechanics)}");
        }

        // ─────────────── measurements ───────────────

        /// <summary>The share of the play area with something in it or within 14 studs, and the largest empty rectangle (in cells, with its studs).</summary>
        private static (double Covered, (int Area, float X0, float Z0, float X1, float Z1) Hole) Coverage(Context c)
        {
            int cols = Math.Max(1, (int)MathF.Ceiling(c.Width / Cell)), rows = Math.Max(1, (int)MathF.Ceiling(c.Depth / Cell));
            var occupied = new bool[cols * rows];
            void Mark(float x, float z, float radius)
            {
                int c0 = (int)MathF.Floor((x - radius - c.MinX) / Cell), c1 = (int)MathF.Floor((x + radius - c.MinX) / Cell);
                int r0 = (int)MathF.Floor((z - radius - c.MinZ) / Cell), r1 = (int)MathF.Floor((z + radius - c.MinZ) / Cell);
                for (int r = Math.Max(0, r0); r <= Math.Min(rows - 1, r1); r++)
                    for (int col = Math.Max(0, c0); col <= Math.Min(cols - 1, c1); col++)
                    {
                        float cx = c.MinX + (col + 0.5f) * Cell, cz = c.MinZ + (r + 0.5f) * Cell;
                        if ((cx - x) * (cx - x) + (cz - z) * (cz - z) <= (radius + Cell * 0.5f) * (radius + Cell * 0.5f)) occupied[r * cols + col] = true;
                    }
            }
            foreach (var o in c.Objects.Where(o => !o.Plate)) Mark(o.X, o.Z, MathF.Max(1.5f, o.Radius * (o.IsAsset ? 0.7f : 1f)));
            // Roads and plazas are designed space.
            foreach (var p in c.Scene.Layout.Paths.Where(p => p.Points.Count >= 2))
                for (int i = 0; i + 1 < p.Points.Count; i++)
                {
                    float ax = p.Points[i][0], az = p.Points[i][1], bx = p.Points[i + 1][0], bz = p.Points[i + 1][1];
                    float len = Dist(ax, az, bx, bz);
                    for (float t = 0; t <= len; t += Cell / 2) Mark(ax + (bx - ax) * t / MathF.Max(1, len), az + (bz - az) * t / MathF.Max(1, len), p.Width / 2);
                }
            foreach (var hub in c.Scene.Layout.Zones.Where(z => z.Id is "hub" or "plaza"))
            {
                var (x, z, r) = Circle(hub);
                Mark(x, z, r * 0.85f);
            }
            if (c.Terrain?.WaterLevel != null)
                for (int r = 0; r < rows; r++)
                    for (int col = 0; col < cols; col++)
                        if (c.Terrain.InWater(c.MinX + (col + 0.5f) * Cell, c.MinZ + (r + 0.5f) * Cell)) occupied[r * cols + col] = true;
            // Near something: within 14 studs of an occupied cell reads as dressed.
            int reach = (int)MathF.Ceiling(14 / Cell);
            var near = new bool[cols * rows];
            for (int r = 0; r < rows; r++)
                for (int col = 0; col < cols; col++)
                {
                    if (!occupied[r * cols + col]) continue;
                    for (int dr = -reach; dr <= reach; dr++)
                        for (int dc = -reach; dc <= reach; dc++)
                        {
                            int rr = r + dr, cc = col + dc;
                            if (rr < 0 || cc < 0 || rr >= rows || cc >= cols || dr * dr + dc * dc > reach * reach) continue;
                            near[rr * cols + cc] = true;
                        }
                }
            double covered = near.Count(n => n) / (double)near.Length;
            var hole = LargestEmpty(near, cols, rows);
            return (covered, (hole.Area, c.MinX + hole.C0 * Cell, c.MinZ + hole.R0 * Cell, c.MinX + (hole.C1 + 1) * Cell, c.MinZ + (hole.R1 + 1) * Cell));
        }

        /// <summary>How much of the play area's edge is framed: the land rising beyond it, or tall scenery standing at it.</summary>
        private static double Framing(Context c)
        {
            if (!c.Scene.Bounds.HasArea || c.Terrain == null) return 0;
            int framed = 0, total = 0;
            var tall = c.Objects.Where(o => !o.Plate && o.Height >= 10).ToList();
            float perimeter = 2 * (c.Width + c.Depth);
            for (int i = 0; i < 64; i++)
            {
                float s = perimeter * i / 64f;
                float x, z, nx, nz;
                if (s < c.Width) { x = c.MinX + s; z = c.MinZ; nx = 0; nz = -1; }
                else if (s < c.Width + c.Depth) { x = c.MaxX; z = c.MinZ + (s - c.Width); nx = 1; nz = 0; }
                else if (s < 2 * c.Width + c.Depth) { x = c.MaxX - (s - c.Width - c.Depth); z = c.MaxZ; nx = 0; nz = 1; }
                else { x = c.MinX; z = c.MaxZ - (s - 2 * c.Width - c.Depth); nx = -1; nz = 0; }
                total++;
                float edge = c.Ground(x, z);
                float rise = MathF.Max(c.Ground(x + nx * 35, z + nz * 35), c.Ground(x + nx * 70, z + nz * 70)) - edge;
                if (rise >= 10 || tall.Any(o => Dist(o.X, o.Z, x + nx * 12, z + nz * 12) < 26)) framed++;
            }
            return total == 0 ? 0 : framed / (double)total;
        }

        /// <summary>
        /// Clark-Evans clustering of the scenery (R: 1 is random, below is clustered, above is regular): natural
        /// scenery is mildly clustered; a grid is not. Returns how good it is (0-1).
        /// </summary>
        private static double Clustering(Context c, out double r)
        {
            var pts = c.Scenery.Where(o => o.IsAsset && o.X >= c.MinX && o.X <= c.MaxX && o.Z >= c.MinZ && o.Z <= c.MaxZ).Select(o => (o.X, o.Z)).ToList();
            r = 1;
            if (pts.Count < 12) return pts.Count / 12.0;
            var grid = new Dictionary<(int, int), List<(float X, float Z)>>();
            const float g = 20;
            foreach (var p in pts)
            {
                var key = ((int)MathF.Floor(p.X / g), (int)MathF.Floor(p.Z / g));
                if (!grid.TryGetValue(key, out var list)) grid[key] = list = new();
                list.Add(p);
            }
            double sum = 0;
            foreach (var p in pts)
            {
                float best = float.MaxValue;
                int kx = (int)MathF.Floor(p.X / g), kz = (int)MathF.Floor(p.Z / g);
                for (int reach = 1; reach <= 6 && best == float.MaxValue; reach++)
                    for (int dz = -reach; dz <= reach; dz++)
                        for (int dx = -reach; dx <= reach; dx++)
                            if (grid.TryGetValue((kx + dx, kz + dz), out var list))
                                foreach (var q in list)
                                {
                                    if (q == p) continue;
                                    float d = Dist(p.X, p.Z, q.X, q.Z);
                                    if (d < best) best = d;
                                }
                sum += best == float.MaxValue ? 60 : best;
            }
            double density = pts.Count / Math.Max(1.0, c.Width * c.Depth);
            r = sum / pts.Count / (0.5 / Math.Sqrt(density));
            if (r > 1.35) c.Warn("composition", $"The scenery is spread too evenly (clustering R {r:0.00}): nature grows in groves and patches with clearings between, not on a grid.");
            return r switch { < 0.35 => 0.5, < 1.2 => 1.0, < 1.45 => 0.6, _ => 0.25 };
        }

        /// <summary>Whether the top of an object can be seen from a hub's centre at eye height (the land does not hide it).</summary>
        private static bool Visible(Context c, float hx, float hz, Obj target)
        {
            if (c.Terrain == null) return true;
            float ey = c.Ground(hx, hz) + 5, ty = target.MaxY;
            float d = Dist(hx, hz, target.X, target.Z);
            for (float s = 8; s < d - 8; s += 6)
            {
                float k = s / d;
                float x = hx + (target.X - hx) * k, z = hz + (target.Z - hz) * k;
                if (c.Ground(x, z) > ey + (ty - ey) * k) return false;
            }
            return true;
        }

        private static (float X, float Z)? NearestPathPoint(SceneDocument scene, float x, float z)
        {
            (float X, float Z)? best = null;
            float bestD = 40;
            foreach (var p in scene.Layout.Paths.Where(p => p.Points.Count >= 2))
                for (int i = 0; i + 1 < p.Points.Count; i++)
                {
                    float ax = p.Points[i][0], az = p.Points[i][1], bx = p.Points[i + 1][0], bz = p.Points[i + 1][1];
                    float dx = bx - ax, dz = bz - az, len2 = dx * dx + dz * dz;
                    float t = len2 < 1e-4f ? 0 : Math.Clamp(((x - ax) * dx + (z - az) * dz) / len2, 0, 1);
                    float px = ax + dx * t, pz = az + dz * t, d = Dist(px, pz, x, z);
                    if (d < bestD) { bestD = d; best = (px, pz); }
                }
            return best;
        }

        private static (float X, float Z, float R) Circle(MapZone z) => z.Circle is { Length: 3 } c
            ? (c[0], c[1], c[2])
            : (z.CenterX, z.CenterZ, MathF.Max(z.Area[2] - z.Area[0], z.Area[3] - z.Area[1]) / 2);

        private static float Dist(float ax, float az, float bx, float bz) => MathF.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));

        private static Obj Summarize(string id, List<WorldPart> parts, SceneObject? source)
        {
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (var p in parts.Where(p => p.Transparency < 0.98f).DefaultIfEmpty(parts[0]))
                foreach (var c in Assets.AssetAnalyzer.Corners(p.CFrame, p.Size))
                {
                    minX = MathF.Min(minX, c.X); maxX = MathF.Max(maxX, c.X);
                    minZ = MathF.Min(minZ, c.Z); maxZ = MathF.Max(maxZ, c.Z);
                    minY = MathF.Min(minY, c.Y); maxY = MathF.Max(maxY, c.Y);
                }
            bool plate = (maxX - minX) >= 150 && (maxZ - minZ) >= 150 && (maxY - minY) < 8;
            return new Obj(id, parts[0].Name, parts[0].Kind, minX, minZ, maxX, maxZ, minY, maxY, parts[0].AssetId != null, parts[0].AssetId,
                source?.Tags ?? (IReadOnlyList<string>)parts[0].Tags, source, plate);
        }

        /// <summary>The largest rectangle of cells that are not covered (maximal rectangle in a histogram, row by row).</summary>
        private static (int Area, int C0, int R0, int C1, int R1) LargestEmpty(bool[] covered, int cols, int rows)
        {
            var heights = new int[cols];
            (int Area, int C0, int R0, int C1, int R1) best = (0, 0, 0, 0, 0);
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++) heights[c] = covered[r * cols + c] ? 0 : heights[c] + 1;
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
