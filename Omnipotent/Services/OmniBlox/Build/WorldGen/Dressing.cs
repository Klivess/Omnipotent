namespace Omnipotent.Services.OmniBlox.Build.WorldGen
{
    /// <summary>One landscaping pass the builder asks for (dress_area): what kind of nature, where, and how much.</summary>
    public sealed class DressRequest
    {
        /// <summary>forest, meadow, rocks, treeline, rim, roadside or accents (<see cref="Dressing.Kinds"/>).</summary>
        public string Kind { get; set; } = "forest";
        /// <summary>The area as a polygon of 3+ [x, z] points; null with no circle: the whole play area.</summary>
        public List<float[]>? Polygon { get; set; }
        public (float X, float Z, float R)? Circle { get; set; }
        public string Biome { get; set; } = "meadow";
        /// <summary>0.3 sparse … 2 lush.</summary>
        public float Density { get; set; } = 1;
        /// <summary>Forest only: the share of the area left as clearings (0 a solid wood … 0.8 scattered trees).</summary>
        public float Clearings { get; set; } = 0.4f;
        /// <summary>Size of what is placed against its role's usual size (0.5 … 2.5).</summary>
        public float Scale { get; set; } = 1;
        /// <summary>Roles to use instead of the kind's own (e.g. palm and bush for a tropical forest).</summary>
        public List<string> Roles { get; set; } = new();
        /// <summary>Models to use for their roles first.</summary>
        public List<long> Assets { get; set; } = new();
        /// <summary>Roadside: the lines to dress (each with its width); empty: every planned and carved path.</summary>
        public List<(List<float[]> Points, float Width)> Lines { get; set; } = new();
        /// <summary>Rim: gaps to leave open (entrances), besides where paths cross it.</summary>
        public List<float[]> Openings { get; set; } = new();
        /// <summary>Roadside: lamp posts along the way.</summary>
        public bool Lamps { get; set; } = true;
        public string Folder { get; set; } = "";
    }

    /// <summary>
    /// The landscaping layers a level artist dresses a map in, each over an area the builder chooses: woods with
    /// clearings, flower meadows, rock fields, a tree line that frames the edge of the play area, a ring of trees
    /// that gives a place its shape (open where paths come in), lamps and plants along paths, and a biome's own
    /// small touches. They used to run once, in fixed places round a fixed hub; now they go wherever the map
    /// needs them.
    /// </summary>
    public static class Dressing
    {
        public static readonly string[] Kinds = { "forest", "meadow", "rocks", "treeline", "rim", "roadside", "accents" };

        public static LandscapeResult Dress(Landscaper l, DressRequest r)
        {
            string kind = (r.Kind ?? "forest").Trim().ToLowerInvariant();
            if (!Kinds.Contains(kind)) throw new ArgumentException($"kind must be one of {string.Join(", ", Kinds)}");
            r.Density = Math.Clamp(r.Density <= 0 ? 1 : r.Density, 0.3f, 2f);
            r.Scale = Math.Clamp(r.Scale <= 0 ? 1 : r.Scale, 0.5f, 2.5f);
            if (r.Folder.Length == 0) r.Folder = "Scenery/" + char.ToUpperInvariant(kind[0]) + kind[1..];
            var area = Area.Of(l.Scene, r, kind);
            switch (kind)
            {
                case "forest": Forest(l, r, area); break;
                case "meadow": Meadow(l, r, area); break;
                case "rocks": Rocks(l, r, area); break;
                case "treeline": Treeline(l, r); break;
                case "rim": Rim(l, r, area); break;
                case "roadside": Roadside(l, r); break;
                case "accents": Accents(l, r, area); break;
            }
            return l.Finish();
        }

        /// <summary>Where a pass may place things: a polygon, a circle or the play area, with its bounding box.</summary>
        private sealed class Area
        {
            public List<float[]>? Polygon;
            public (float X, float Z, float R)? Circle;
            public float MinX, MinZ, MaxX, MaxZ;
            public float CenterX => (MinX + MaxX) / 2;
            public float CenterZ => (MinZ + MaxZ) / 2;
            public float Size => (MaxX - MinX) * (MaxZ - MinZ) * (Circle != null ? MathF.PI / 4 : Polygon != null ? 0.75f : 1);

            public bool Inside(float x, float z) =>
                Polygon != null ? TerrainBuilder.InPolygon(x, z, Polygon)
                : Circle is { } c ? (x - c.X) * (x - c.X) + (z - c.Z) * (z - c.Z) <= c.R * c.R
                : x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;

            public static Area Of(SceneDocument scene, DressRequest r, string kind)
            {
                var a = new Area { Polygon = r.Polygon is { Count: >= 3 } p ? p : null, Circle = r.Circle };
                if (a.Polygon != null)
                    (a.MinX, a.MinZ, a.MaxX, a.MaxZ) = (a.Polygon.Min(q => q[0]), a.Polygon.Min(q => q[1]), a.Polygon.Max(q => q[0]), a.Polygon.Max(q => q[1]));
                else if (a.Circle is { } c)
                    (a.MinX, a.MinZ, a.MaxX, a.MaxZ) = (c.X - c.R, c.Z - c.R, c.X + c.R, c.Z + c.R);
                else if (scene.Bounds.HasArea)
                    (a.MinX, a.MinZ, a.MaxX, a.MaxZ) = (scene.Bounds.MinX, scene.Bounds.MinZ, scene.Bounds.MaxX, scene.Bounds.MaxZ);
                else if (kind is not ("roadside" or "treeline"))
                    throw new ArgumentException("give an area (polygon, circle or zone), or set the play area first (set_map_area)");
                if (a.Polygon == null && a.Circle == null && a.MaxX - a.MinX < 1) (a.MinX, a.MinZ, a.MaxX, a.MaxZ) = (-1, -1, 1, 1);
                return a;
            }
        }

        // ─────────────── the layers ───────────────

        /// <summary>
        /// Woods: dense groves where the noise is high and clearings where it is low (so the land reads as woods and
        /// glades, not trees sprinkled evenly), trees with bushes and rocks under them, filling the share of the
        /// budget the area deserves.
        /// </summary>
        private static void Forest(Landscaper l, DressRequest r, Area a)
        {
            var trees = Trees(l, r);
            var bushes = RoleOr(l, r, Roles.Bush);
            var rocks = RoleOr(l, r, Roles.Rock);
            var sample = trees.Concat(bushes).ToList();
            float avgParts = sample.Count > 0 ? (float)sample.Average(e => Math.Max(1, e.Parts)) : 5;
            int remaining = Math.Max(0, l.Budget - l.Parts - 150);
            float wooded = MathF.Max(0.15f, 1 - Math.Clamp(r.Clearings, 0, 0.85f));
            float byBudget = MathF.Sqrt(MathF.Max(1, a.Size * wooded) / MathF.Max(1, remaining * 0.85f / avgParts));
            float spacing = Math.Clamp(MathF.Max(byBudget, 9.5f * r.Scale), 7, 26) / MathF.Sqrt(r.Density);
            float threshold = -0.55f + 1.1f * Math.Clamp(r.Clearings, 0, 0.85f);
            int seed = l.Rng.Next(100000);
            // Woods where the noise is high; in the clearings only the odd lone tree, where a finer noise peaks (a random
            // chance per attempt would fill the clearings in, since the darts keep throwing until the wood is full).
            var spots = l.Darts(a.MinX, a.MinZ, a.MaxX, a.MaxZ, spacing, 6000, (x, z) =>
            {
                if (!a.Inside(x, z) || !l.Free(x, z, 2.2f) || l.Wet(x, z) || l.SlopeAt(x, z) > 1.2f) return false;
                return TerrainBuilder.Fbm(x / 52f, z / 52f, seed, 3) > threshold || TerrainBuilder.Fbm(x / 7f, z / 7f, seed + 77, 1) > 0.82f;
            }, attemptsPerPoint: 6);
            int placed = 0;
            foreach (var (x, z) in spots.OrderBy(s => (s.X - a.CenterX) * (s.X - a.CenterX) + (s.Z - a.CenterZ) * (s.Z - a.CenterZ)))
            {
                double roll = l.Rng.NextDouble();
                var palette = roll < 0.68 ? trees : roll < 0.9 ? bushes : rocks;
                if (palette.Count == 0) palette = trees.Count > 0 ? trees : bushes;
                if (!Place(l, r, palette, x, z, 1, 0.28f, kitTree: palette == trees || palette.Count == 0)) { if (l.BudgetSpent) break; continue; }
                placed++;
            }
            l.Result.Notes.Add($"Forest: {placed} trees, bushes and rocks about {spacing:0.#} studs apart where it is wooded, {r.Clearings:P0} left as clearings.");
        }

        /// <summary>Flower meadows: patches of flowers and grass with the odd bush and stone.</summary>
        private static void Meadow(Landscaper l, DressRequest r, Area a)
        {
            var flowers = RoleOr(l, r, Roles.Flower);
            var grass = RoleOr(l, r, Roles.Grass);
            var bushes = RoleOr(l, r, Roles.Bush);
            var rocks = RoleOr(l, r, Roles.Rock);
            var ground = flowers.Concat(grass).ToList();
            if (ground.Count == 0 && bushes.Count == 0) { l.Result.Warnings.Add("the library has no flowers, grass or bushes in this style yet (the harvester has been asked)"); return; }
            float spacing = 13 / MathF.Sqrt(r.Density);
            int patches = 0;
            foreach (var (cx, cz) in l.Darts(a.MinX, a.MinZ, a.MaxX, a.MaxZ, spacing, 900, (x, z) => a.Inside(x, z) && l.Free(x, z, 2) && !l.Wet(x, z) && l.SlopeAt(x, z) < 0.9f))
            {
                double roll = l.Rng.NextDouble();
                if (roll < 0.15 && bushes.Count > 0) { if (!Place(l, r, bushes, cx, cz, 1, 0.32f) && l.BudgetSpent) break; continue; }
                if (roll < 0.22 && rocks.Count > 0) { if (!Place(l, r, rocks, cx, cz, 0.7f, 0.35f) && l.BudgetSpent) break; continue; }
                if (ground.Count == 0) continue;
                int n = 3 + l.Rng.Next(4);
                for (int k = 0; k < n; k++)
                {
                    float x = cx + (k == 0 ? 0 : ((float)l.Rng.NextDouble() * 2 - 1) * 4.5f), z = cz + (k == 0 ? 0 : ((float)l.Rng.NextDouble() * 2 - 1) * 4.5f);
                    if (k > 0 && (!a.Inside(x, z) || !l.Free(x, z, 0.8f))) continue;
                    Place(l, r, ground, x, z, 1, 0.3f);
                }
                patches++;
                if (l.BudgetSpent) break;
            }
            l.Result.Notes.Add($"Meadow: {patches} patches of flowers and grass with the odd bush and stone.");
        }

        /// <summary>Rock fields: a boulder with smaller rocks round it, cluster by cluster.</summary>
        private static void Rocks(Landscaper l, DressRequest r, Area a)
        {
            var boulders = RoleOr(l, r, Roles.Boulder);
            var rocks = RoleOr(l, r, Roles.Rock);
            if (boulders.Count == 0) boulders = rocks;
            if (rocks.Count == 0) rocks = boulders;
            if (rocks.Count == 0) { l.Result.Warnings.Add("the library has no rocks in this style yet (the harvester has been asked)"); return; }
            float spacing = 22 / MathF.Sqrt(r.Density);
            int clusters = 0;
            foreach (var (cx, cz) in l.Darts(a.MinX, a.MinZ, a.MaxX, a.MaxZ, spacing, 400, (x, z) => a.Inside(x, z) && l.Free(x, z, 4) && !l.Wet(x, z) && l.SlopeAt(x, z) < 0.9f))
            {
                if (!Place(l, r, boulders, cx, cz, 1.6f, 0.4f) && l.BudgetSpent) break;
                int n = 2 + l.Rng.Next(3);
                for (int k = 0; k < n; k++)
                {
                    double ang = l.Rng.NextDouble() * Math.PI * 2;
                    float d = 4 + (float)l.Rng.NextDouble() * 4;
                    float x = cx + (float)Math.Cos(ang) * d, z = cz + (float)Math.Sin(ang) * d;
                    if (a.Inside(x, z) && l.Free(x, z, 1) && !l.Wet(x, z)) Place(l, r, rocks, x, z, 0.55f + (float)l.Rng.NextDouble() * 0.35f, 0.4f);
                }
                clusters++;
            }
            l.Result.Notes.Add($"Rocks: {clusters} clusters of a boulder with smaller rocks round it.");
        }

        /// <summary>A tree line just past the play area, so its edge is a wood rather than an invisible wall in a field.</summary>
        private static void Treeline(Landscaper l, DressRequest r)
        {
            var scene = l.Scene;
            if (!scene.Bounds.HasArea) throw new ArgumentException("a tree line frames the play area: set it first (set_map_area)");
            var trees = Trees(l, r);
            float minX = scene.Bounds.MinX, minZ = scene.Bounds.MinZ, maxX = scene.Bounds.MaxX, maxZ = scene.Bounds.MaxZ;
            float band = 70;
            int placed = 0;
            foreach (var (x, z) in l.Darts(minX - band, minZ - band, maxX + band, maxZ + band, 13 * r.Scale / MathF.Sqrt(r.Density), 1600, (x, z) =>
            {
                bool outside = x < minX || x > maxX || z < minZ || z > maxZ;
                return outside && (r.Polygon == null || TerrainBuilder.InPolygon(x, z, r.Polygon)) && l.Free(x, z, 2.5f) && !l.Wet(x, z) && l.SlopeAt(x, z) < 1.4f;
            }, attemptsPerPoint: 5))
            {
                if (!Place(l, r, trees, x, z, 1.1f, 0.3f, kitTree: true)) { if (l.BudgetSpent) break; continue; }
                placed++;
            }
            l.Result.Notes.Add($"Tree line: {placed} trees in the {band:0}-stud band past the play area's edge.");
        }

        /// <summary>A ring of trees and bushes round a place, open where paths come in and at the openings given: what gives a place its shape.</summary>
        private static void Rim(Landscaper l, DressRequest r, Area a)
        {
            if (a.Polygon == null && a.Circle == null) throw new ArgumentException("a rim goes round a place: give its circle, polygon or zone");
            var trees = Trees(l, r);
            var bushes = RoleOr(l, r, Roles.Bush);
            var gaps = Gaps(l.Scene, r);
            List<float[]>? ring = a.Polygon?.Append(a.Polygon[0]).ToList();
            float spacing = 10.5f * r.Scale / MathF.Sqrt(r.Density);
            int placed = 0;
            foreach (var (x, z) in l.Darts(a.MinX - 18, a.MinZ - 18, a.MaxX + 18, a.MaxZ + 18, spacing, 600, (x, z) =>
            {
                if (a.Circle is { } c)
                {
                    float d = MathF.Sqrt((x - c.X) * (x - c.X) + (z - c.Z) * (z - c.Z));
                    if (d < c.R - 3 || d > c.R + 14) return false;
                }
                else if (TerrainBuilder.DistanceToPolyline(x, z, ring!, out _) > 9) return false;
                if (gaps.Any(g => (x - g.X) * (x - g.X) + (z - g.Z) * (z - g.Z) < g.R * g.R)) return false;
                return l.Free(x, z, 2.5f) && !l.Wet(x, z) && l.SlopeAt(x, z) < 1.1f;
            }))
            {
                bool bush = bushes.Count > 0 && l.Rng.NextDouble() < 0.28;
                if (!Place(l, r, bush ? bushes : trees, x, z, bush ? 1 : 1.05f, 0.3f, kitTree: !bush)) { if (l.BudgetSpent) break; continue; }
                placed++;
            }
            l.Result.Notes.Add($"Rim: {placed} trees and bushes round the place, open at {gaps.Count} entrance(s).");
        }

        /// <summary>Where a rim stays open: every path crossing it, and the openings asked for.</summary>
        private static List<(float X, float Z, float R)> Gaps(SceneDocument scene, DressRequest r)
        {
            var gaps = r.Openings.Select(o => (o[0], o[1], 13f)).ToList();
            foreach (var (points, width) in PathsOf(scene))
                for (int i = 0; i + 1 < points.Count; i++)
                {
                    float len = MathF.Sqrt(MathF.Pow(points[i + 1][0] - points[i][0], 2) + MathF.Pow(points[i + 1][1] - points[i][1], 2));
                    for (float t = 0; t <= len; t += 6)
                    {
                        float k = len < 0.01f ? 0 : t / len;
                        gaps.Add((points[i][0] + (points[i + 1][0] - points[i][0]) * k, points[i][1] + (points[i + 1][1] - points[i][1]) * k, width / 2 + 6));
                    }
                }
            return gaps;
        }

        /// <summary>Every planned path and every path carved into the terrain, with its width.</summary>
        public static List<(List<float[]> Points, float Width)> PathsOf(SceneDocument scene)
        {
            var list = scene.Layout.Paths.Where(p => p.Points.Count >= 2).Select(p => (p.Points, p.Width)).ToList();
            foreach (var op in scene.Land?.Ops ?? new List<TerrainOp>())
                if (op.Kind is "path" or "ramp" && op.Points is { Count: >= 2 } pts && !list.Any(l => SameLine(l.Points, pts)))
                    list.Add((pts, op.Width));
            return list;
        }

        private static bool SameLine(List<float[]> a, List<float[]> b) =>
            a.Count == b.Count && a.Zip(b).All(p => MathF.Abs(p.First[0] - p.Second[0]) < 0.5f && MathF.Abs(p.First[1] - p.Second[1]) < 0.5f);

        /// <summary>Lamps every ~40 studs (alternating sides) and flowers, rocks and bushes at the edges of paths.</summary>
        private static void Roadside(Landscaper l, DressRequest r)
        {
            var lines = r.Lines.Count > 0 ? r.Lines : PathsOf(l.Scene);
            if (lines.Count == 0) throw new ArgumentException("there are no paths to line: plan them (plan_map paths) or carve them (terrain_sculpt path), or give points");
            var edge = RoleOr(l, r, Roles.Flower).Concat(RoleOr(l, r, Roles.Rock)).Concat(RoleOr(l, r, Roles.Bush)).ToList();
            int lamps = 0, plants = 0;
            foreach (var (points, width) in lines)
            {
                float length = WorldOps.PathLength(points);
                int side = 1;
                if (r.Lamps)
                    for (float t = 16; t < length - 10; t += 40)
                    {
                        var (x, z, ux, uz) = WorldOps.PointAlong(points, t);
                        float ox = -uz * side * (width / 2 + 2.6f), oz = ux * side * (width / 2 + 2.6f);
                        side = -side;
                        if (!l.Unoccupied(x + ox, z + oz, 1.2f) || l.Wet(x + ox, z + oz)) continue;
                        KitProps.Lamp(l.Prop(r.Folder, x + ox, z + oz, 0));
                        l.Placed("lamp", 4, x + ox, z + oz, 1.4f);
                        lamps++;
                    }
                if (edge.Count == 0) continue;
                for (float t = 6; t < length - 4; t += 9 / r.Density)
                {
                    if (l.Rng.NextDouble() < 0.45) continue;
                    var (x, z, ux, uz) = WorldOps.PointAlong(points, t);
                    int s = l.Rng.NextDouble() < 0.5 ? 1 : -1;
                    float off = width / 2 + 3.5f + (float)l.Rng.NextDouble() * 4;
                    float px = x - uz * s * off, pz = z + ux * s * off;
                    if (!l.Free(px, pz, 1.8f) || l.Wet(px, pz)) continue;
                    if (!Place(l, r, edge, px, pz, 1, 0.35f)) { if (l.BudgetSpent) break; continue; }
                    plants++;
                }
            }
            l.Result.Notes.Add($"Roadside: {lamps} lamp(s) and {plants} plants and stones along {lines.Count} path(s).");
        }

        /// <summary>The small things that tell a place's biome (mushrooms, stumps, pumpkins, cacti, crystals…), with flowers in patches.</summary>
        private static void Accents(Landscaper l, DressRequest r, Area a)
        {
            var roles = r.Roles.Count > 0 ? r.Roles : new List<string> { Roles.Flower, Roles.Rock, Roles.Bush }.Concat(MapLook.AccentRoles(r.Biome)).ToList();
            var palette = roles.SelectMany(role => l.Palette(role, r.Biome, r.Assets, 2)).ToList();
            if (palette.Count == 0) { l.Result.Warnings.Add("the library has no models for this biome's touches yet (the harvester has been asked)"); return; }
            int count = (int)Math.Clamp(a.Size / 1500 * r.Density, 4, 160);
            int placed = 0;
            foreach (var (cx, cz) in l.Darts(a.MinX, a.MinZ, a.MaxX, a.MaxZ, 14 / MathF.Sqrt(r.Density), count, (x, z) => a.Inside(x, z) && l.Free(x, z, 3) && !l.Wet(x, z)))
            {
                var e = l.Pick(palette);
                if (e == null) continue;
                // Flowers come in patches; everything else alone.
                int patch = e.Role == Roles.Flower ? 3 + l.Rng.Next(3) : 1;
                for (int k = 0; k < patch; k++)
                {
                    float x = cx + (k == 0 ? 0 : ((float)l.Rng.NextDouble() * 2 - 1) * 4), z = cz + (k == 0 ? 0 : ((float)l.Rng.NextDouble() * 2 - 1) * 4);
                    if (k > 0 && !l.Free(x, z, 1)) continue;
                    if (l.PlaceModel(e, x, z, (float)l.Rng.NextDouble() * 360, l.ScaleFor(e, Roles.TypicalHeight(e.Role) * r.Scale), r.Folder, occupyFactor: 0.4f) != null) placed++;
                    else if (l.OutOfBudget) return;
                }
            }
            l.Result.Notes.Add($"Accents: {placed} small touches of the {r.Biome} biome.");
        }

        // ─────────────── helpers ───────────────

        /// <summary>The trees for this pass: the roles asked for, else the biome's own trees.</summary>
        private static List<CatalogEntry> Trees(Landscaper l, DressRequest r)
        {
            var roles = r.Roles.Count > 0 ? r.Roles.ToArray() : MapLook.TreeRoles(r.Biome);
            var list = new List<CatalogEntry>();
            for (int i = 0; i < roles.Length; i++)
                list.AddRange(l.Palette(roles[i], r.Biome, r.Assets, i == 0 ? 3 : 2).Where(e => list.All(x => x.AssetId != e.AssetId)));
            return list;
        }

        /// <summary>A role's palette for this pass, unless the call named other roles (then those).</summary>
        private static List<CatalogEntry> RoleOr(Landscaper l, DressRequest r, string role) =>
            r.Roles.Count > 0 ? r.Roles.SelectMany(x => l.Palette(x, r.Biome, r.Assets, 2)).ToList() : l.Palette(role, r.Biome, r.Assets, 3);

        /// <summary>One model from the palette at a spot (or a kit-built tree where the library has none).</summary>
        private static bool Place(Landscaper l, DressRequest r, List<CatalogEntry> palette, float x, float z, float size, float occupy, bool kitTree = false)
        {
            if (l.Pick(palette) is { } e)
                return l.PlaceModel(e, x, z, (float)l.Rng.NextDouble() * 360, l.ScaleFor(e, Roles.TypicalHeight(e.Role) * size * r.Scale), r.Folder, occupyFactor: occupy) != null;
            if (!kitTree || l.Parts + 4 > l.Budget) return false;
            float h = (14 + (float)l.Rng.NextDouble() * 10) * r.Scale;
            KitProps.Tree(l.Prop(r.Folder, x, z, (float)l.Rng.NextDouble() * 360), h, MapLook.LeafColor(r.Biome), l.Rng);
            l.Placed("kit tree", 4, x, z, h * 0.12f);
            return true;
        }
    }
}
