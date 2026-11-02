using System.Text;

namespace Omnipotent.Services.OmniBlox.Build
{
    /// <summary>
    /// A baked heightmap: one height and one material per cell. The compiler writes it into the kit's config
    /// (built with Terrain:FillBlock at server start), the renderers draw it, and every placement tool snaps to
    /// it, so the agent's ground, the picture it sees and the game players get are the same ground.
    /// </summary>
    public sealed class TerrainMap
    {
        public float OriginX { get; init; }
        public float OriginZ { get; init; }
        public float Cell { get; init; } = 4;
        public int Width { get; init; }
        public int Depth { get; init; }
        /// <summary>The bottom of every column (terrain is solid from here up to each cell's height).</summary>
        public float Floor { get; init; }
        public required float[] Heights { get; init; }
        public required byte[] Materials { get; init; }
        public required List<string> MaterialNames { get; init; }
        public float? WaterLevel { get; init; }

        public float MaxX => OriginX + Width * Cell;
        public float MaxZ => OriginZ + Depth * Cell;

        public bool Covers(float x, float z) => x >= OriginX && x <= MaxX && z >= OriginZ && z <= MaxZ;

        private float At(int col, int row)
        {
            col = Math.Clamp(col, 0, Width - 1);
            row = Math.Clamp(row, 0, Depth - 1);
            return Heights[row * Width + col];
        }

        /// <summary>The ground's height at a point (bilinear between cell centres); the floor outside the map.</summary>
        public float HeightAt(float x, float z)
        {
            if (!Covers(x, z)) return Floor;
            float fx = (x - OriginX) / Cell - 0.5f, fz = (z - OriginZ) / Cell - 0.5f;
            int c0 = (int)MathF.Floor(fx), r0 = (int)MathF.Floor(fz);
            float tx = fx - c0, tz = fz - r0;
            float a = At(c0, r0), b = At(c0 + 1, r0), c = At(c0, r0 + 1), d = At(c0 + 1, r0 + 1);
            return (a * (1 - tx) + b * tx) * (1 - tz) + (c * (1 - tx) + d * tx) * tz;
        }

        public string MaterialAt(float x, float z)
        {
            if (!Covers(x, z)) return MaterialNames[0];
            int col = Math.Clamp((int)((x - OriginX) / Cell), 0, Width - 1), row = Math.Clamp((int)((z - OriginZ) / Cell), 0, Depth - 1);
            return MaterialNames[Materials[row * Width + col]];
        }

        public bool InWater(float x, float z) => WaterLevel is float w && HeightAt(x, z) < w - 0.3f;

        /// <summary>The steepest rise between neighbouring cells around a point, in studs per stud.</summary>
        public float SlopeAt(float x, float z)
        {
            float h = Cell;
            float dx = (HeightAt(x + h, z) - HeightAt(x - h, z)) / (2 * h);
            float dz = (HeightAt(x, z + h) - HeightAt(x, z - h)) / (2 * h);
            return MathF.Sqrt(dx * dx + dz * dz);
        }

        private const string B64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

        /// <summary>Two base-64 characters per cell: the height above <see cref="Floor"/> in half studs (12 bits).</summary>
        public string EncodeHeights()
        {
            var sb = new StringBuilder(Heights.Length * 2);
            foreach (float h in Heights)
            {
                int q = Math.Clamp((int)MathF.Round((h - Floor) * 2), 0, 4095);
                sb.Append(B64[q >> 6]).Append(B64[q & 63]);
            }
            return sb.ToString();
        }

        /// <summary>One base-64 character per cell: an index into <see cref="MaterialNames"/>.</summary>
        public string EncodeMaterials()
        {
            var sb = new StringBuilder(Materials.Length);
            foreach (byte m in Materials) sb.Append(B64[Math.Min(m, (byte)63)]);
            return sb.ToString();
        }

        /// <summary>The config table the kit's World module builds from (Kind = "heightmap").</summary>
        public Dictionary<string, object?> ToConfig() => new()
        {
            ["Kind"] = "heightmap",
            ["Origin"] = new[] { OriginX, OriginZ },
            ["Cell"] = Cell,
            ["Width"] = Width,
            ["Depth"] = Depth,
            ["Floor"] = Floor,
            ["Heights"] = EncodeHeights(),
            ["Materials"] = MaterialNames,
            ["MaterialMap"] = EncodeMaterials(),
            ["WaterLevel"] = WaterLevel,
        };
    }

    /// <summary>
    /// Bakes a <see cref="TerrainRecipe"/> into a <see cref="TerrainMap"/>, deterministically (the same recipe
    /// always gives the same ground): the landform (<see cref="TerrainRecipe.Shape"/>: a basin, an island, an
    /// archipelago, a coast, a valley, a canyon, a mountain, terraced highlands, a crater or plains, with a flat
    /// core at y = 0 for building where it has one), rolling hills, the edge that frames the map (mountains, sea
    /// or cliffs), then the recipe's operations in order, and finally materials by biome (rock on steep slopes,
    /// snow on peaks, sand on shores) with painted materials and biome regions on top.
    ///
    /// Every map used to be the same flat disc ringed by hills and mountains; the landform is now the first
    /// decision a map makes, and the sculpting steps (ridges, canyons, mesas, mountains, islands, terraces) shape
    /// the places in it.
    /// </summary>
    public static class TerrainBuilder
    {
        public const int MaxCells = 300 * 300;
        /// <summary>Where water stands when a shape makes a sea and the recipe sets no level: just under the flat core.</summary>
        public const float SeaLevel = -2.5f;
        private const float SeaFloor = -20;

        public static readonly string[] Shapes = { "basin", "plains", "island", "archipelago", "coast", "valley", "canyon", "mountain", "highlands", "crater" };
        public static readonly string[] Edges = { "mountains", "sea", "cliffs", "none" };

        private sealed record BiomeLook(string Ground, string Slope, string Peak, string Shore, string Bed);

        private static readonly Dictionary<string, BiomeLook> Biomes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["meadow"] = new("Grass", "Rock", "Snow", "Sand", "Sand"),
            ["forest"] = new("LeafyGrass", "Rock", "Snow", "Ground", "Mud"),
            ["autumn"] = new("Grass", "Rock", "Rock", "Ground", "Mud"),
            ["jungle"] = new("LeafyGrass", "Rock", "Rock", "Mud", "Mud"),
            ["swamp"] = new("Mud", "Ground", "Rock", "Mud", "Mud"),
            ["desert"] = new("Sand", "Sandstone", "Sandstone", "Sand", "Sand"),
            ["snow"] = new("Snow", "Glacier", "Snow", "Ice", "Slate"),
            ["beach"] = new("Grass", "Rock", "Rock", "Sand", "Sand"),
            ["volcanic"] = new("Basalt", "Basalt", "CrackedLava", "Ground", "Basalt"),
            ["candy"] = new("Grass", "Pavement", "Snow", "Sand", "Sand"),
            ["spooky"] = new("Mud", "Slate", "Slate", "Ground", "Mud"),
            ["crystal"] = new("Slate", "Rock", "Glacier", "Limestone", "Slate"),
        };

        public static IEnumerable<string> BiomeNames => Biomes.Keys;

        public static readonly string[] Materials =
        {
            "Grass", "LeafyGrass", "Sand", "Rock", "Snow", "Ground", "Mud", "Slate", "Ice", "Glacier", "Sandstone", "Basalt",
            "CrackedLava", "Asphalt", "Cobblestone", "Brick", "Pavement", "Limestone", "Salt", "WoodPlanks", "Concrete",
        };

        /// <summary>The edge a recipe gets: its own, else what suits its shape (sea round islands and coasts, mountains elsewhere).</summary>
        public static string EdgeOf(TerrainRecipe recipe)
        {
            string edge = (recipe.Edge ?? "").Trim().ToLowerInvariant();
            if (Edges.Contains(edge)) return edge;
            return ShapeOf(recipe) is "island" or "archipelago" or "coast" ? "sea" : "mountains";
        }

        public static string ShapeOf(TerrainRecipe recipe)
        {
            string shape = (recipe.Shape ?? "").Trim().ToLowerInvariant();
            return Shapes.Contains(shape) ? shape : "basin";
        }

        /// <summary>The big features of a recipe's landform, with where they are: what the builder plans the map round.</summary>
        public sealed record Feature(string Name, float X, float Z, float Radius, string Note);

        /// <summary>The geometry of a landform, worked out once per recipe (deterministic from its seed).</summary>
        private sealed class Landform
        {
            public required string Shape { get; init; }
            public required string Edge { get; init; }
            public float Cx, Cz, Half, Relief, Ux, Uz, Nx, Nz;
            public List<(float X, float Z, float R)> Islands { get; } = new();
            public List<Feature> Features { get; } = new();
        }

        private static Landform LandformOf(TerrainRecipe recipe, float sizeX, float sizeZ)
        {
            string shape = ShapeOf(recipe);
            var f = new Landform { Shape = shape, Edge = EdgeOf(recipe) };
            f.Cx = At(recipe.Center, 0, 0);
            f.Cz = At(recipe.Center, 1, 0);
            f.Half = MathF.Min(sizeX, sizeZ) / 2;
            float a = recipe.Direction * MathF.PI / 180;
            // Along the shape's direction (0 = along −Z, as a yaw), and across it.
            (f.Ux, f.Uz) = (MathF.Sin(a), -MathF.Cos(a));
            (f.Nx, f.Nz) = (-f.Uz, f.Ux);
            float half = f.Half;
            f.Relief = recipe.Relief > 0 ? recipe.Relief : shape switch
            {
                "mountain" => 120, "valley" => 70, "canyon" => 46, "crater" => 48, "highlands" => 40, _ => 0,
            };
            var rng = new Random(recipe.Seed * 977 + 5);
            switch (shape)
            {
                case "island":
                    f.Islands.Add((f.Cx, f.Cz, half * 0.6f));
                    f.Features.Add(new("island", f.Cx, f.Cz, half * 0.6f, "the whole island, beaches all round; the sea frames it"));
                    break;
                case "archipelago":
                    {
                        int n = Math.Clamp(recipe.Islands, 2, 8);
                        float main = half * (n <= 3 ? 0.36f : 0.3f);
                        f.Islands.Add((f.Cx, f.Cz, main));
                        f.Features.Add(new("island 1 (the main island)", f.Cx, f.Cz, main, "in the middle"));
                        float start = (float)(rng.NextDouble() * 360);
                        for (int i = 1; i < n; i++)
                        {
                            float ang = (start + (i - 1) * 360f / (n - 1) + (float)(rng.NextDouble() * 2 - 1) * 14) * MathF.PI / 180;
                            float r = half * (0.15f + (float)rng.NextDouble() * 0.07f);
                            float dist = main + r + half * (0.12f + (float)rng.NextDouble() * 0.08f);
                            float x = f.Cx + MathF.Sin(ang) * dist, z = f.Cz - MathF.Cos(ang) * dist;
                            f.Islands.Add((x, z, r));
                            f.Features.Add(new($"island {i + 1}", R(x), R(z), R(r), $"{dist - main - r:0} studs of sea from the main island"));
                        }
                        break;
                    }
                case "coast":
                    f.Features.Add(new("coast", f.Cx + f.Ux * half * 0.3f, f.Cz + f.Uz * half * 0.3f, half, $"land on one side, the sea towards ({f.Ux:0.##}, {f.Uz:0.##}); the shoreline runs about {half * 0.3f:0} studs from the centre"));
                    break;
                case "valley":
                    f.Features.Add(new("valley floor", f.Cx, f.Cz, MathF.Max(60, recipe.FlatRadius * 0.6f), $"runs through the centre towards ({f.Ux:0.##}, {f.Uz:0.##}), walls {f.Relief:0} studs high on both sides"));
                    break;
                case "canyon":
                    f.Features.Add(new("canyon floor", f.Cx, f.Cz, MathF.Max(40, recipe.FlatRadius * 0.45f), $"a winding canyon through the centre towards ({f.Ux:0.##}, {f.Uz:0.##}), cut {f.Relief:0} studs into a high plateau"));
                    break;
                case "mountain":
                    f.Features.Add(new("peak", f.Cx, f.Cz, half * 0.55f, $"a mountain {f.Relief:0} studs high in the middle; its foot is the flat land round it"));
                    break;
                case "crater":
                    f.Features.Add(new("crater", f.Cx, f.Cz, half * 0.5f, $"a flat bowl inside a rim {f.Relief:0} studs high"));
                    break;
                case "highlands":
                    f.Features.Add(new("highlands", f.Cx, f.Cz, half, "terraced plateaus with cliffs between, a flat core in the middle"));
                    break;
            }
            return f;
        }

        /// <summary>The landform's features, for the builder to plan its places on.</summary>
        public static List<Feature> Features(TerrainRecipe recipe)
        {
            float sizeX = Math.Clamp(At(recipe.Size, 0, 640), 32, 4096), sizeZ = Math.Clamp(At(recipe.Size, 1, 640), 32, 4096);
            return LandformOf(recipe, sizeX, sizeZ).Features;
        }

        /// <summary>The base ground of a landform at a point, before the edge and the sculpting steps.</summary>
        private static float BaseHeight(TerrainRecipe recipe, Landform f, float x, float z)
        {
            int seed = recipe.Seed;
            float dx = x - f.Cx, dz = z - f.Cz;
            float d = MathF.Sqrt(dx * dx + dz * dz);
            float scale = Math.Max(16, recipe.HillScale);
            float Hills(float weight, float amount) => amount > 0 && weight > 0 ? amount * MathF.Pow(Fbm(x / scale, z / scale, seed, 4) * 0.5f + 0.5f, 1.4f) * weight : 0;
            float Core(float distance) => SmoothStep(recipe.FlatRadius, recipe.FlatRadius + Math.Max(24, scale * 0.6f), distance);
            float along = dx * f.Ux + dz * f.Uz, across = dx * f.Nx + dz * f.Nz;
            switch (f.Shape)
            {
                case "plains":
                    return Hills(Core(d), recipe.Hills * 0.6f) + 2.5f * Fbm(x / (scale * 1.6f), z / (scale * 1.6f), seed + 3, 3);
                case "island":
                case "archipelago":
                    {
                        // The island with the most land here; each has its own flat top and hills towards its shore.
                        float best = SeaFloor;
                        for (int i = 0; i < f.Islands.Count; i++)
                        {
                            var (ix, iz, ir) = f.Islands[i];
                            float idx = x - ix, idz = z - iz, id = MathF.Sqrt(idx * idx + idz * idz);
                            float theta = MathF.Atan2(idz, idx);
                            float shore = ir * (1 + 0.2f * Fbm(MathF.Cos(theta) * 1.6f + i * 3.1f, MathF.Sin(theta) * 1.6f, seed + 41 + i, 3));
                            float land = SmoothStep(shore + 14, shore - 16, id); // 1 inland, 0 at sea
                            if (land <= 0) continue;
                            float flat = i == 0 && f.Shape == "island" ? recipe.FlatRadius : ir * 0.45f;
                            float h = Hills(SmoothStep(flat, flat + Math.Max(24, ir * 0.35f), id) * SmoothStep(shore, shore - 30, id), recipe.Hills);
                            best = MathF.Max(best, SeaFloor + (h - SeaFloor) * land);
                        }
                        return best;
                    }
                case "coast":
                    {
                        float shoreline = f.Half * 0.3f + 34 * Fbm(across / 150f, 0.5f, seed + 43, 3);
                        float land = SmoothStep(shoreline + 18, shoreline - 22, along);
                        float h = Hills(Core(d) * SmoothStep(shoreline, shoreline - 60, along), recipe.Hills);
                        return SeaFloor + (h - SeaFloor) * land;
                    }
                case "valley":
                    {
                        float floor = MathF.Max(60, recipe.FlatRadius * 0.6f);
                        float wiggle = 26 * Fbm(along / 220f, 0.3f, seed + 47, 2);
                        float side = MathF.Abs(across - wiggle);
                        float wall = SmoothStep(floor, floor + f.Half * 0.45f, side);
                        float rugged = 0.75f + 0.25f * (Fbm(x / 60f, z / 60f, seed + 7, 3) * 0.5f + 0.5f);
                        return f.Relief * MathF.Pow(wall, 1.3f) * rugged + Hills(SmoothStep(floor * 0.6f, floor, side), recipe.Hills * 0.4f);
                    }
                case "canyon":
                    {
                        float floor = MathF.Max(40, recipe.FlatRadius * 0.45f);
                        float wiggle = 48 * Fbm(along / 170f, 0.7f, seed + 53, 2);
                        float side = MathF.Abs(across - wiggle);
                        float wall = SmoothStep(floor, floor + 10 + f.Relief * 0.3f, side);
                        // Banded walls: the cut shows layers, as real canyons do.
                        float banded = Terrace(f.Relief * wall, MathF.Max(6, f.Relief / 5));
                        return banded + 3 * Fbm(x / 70f, z / 70f, seed + 59, 3) * wall;
                    }
                case "mountain":
                    {
                        float foot = f.Half * 0.55f;
                        float t = Math.Clamp(1 - d / foot, 0, 1);
                        float ridges = 1 - MathF.Abs(Fbm(x / 70f, z / 70f, seed + 61, 4));
                        float peak = f.Relief * MathF.Pow(t, 1.7f) * (0.78f + 0.22f * ridges);
                        return peak + Hills(SmoothStep(foot, foot + 60, d), recipe.Hills * 0.6f);
                    }
                case "highlands":
                    {
                        float n = Fbm(x / scale, z / scale, seed, 4) * 0.5f + 0.5f;
                        float raw = f.Relief * MathF.Pow(n, 1.2f) * Core(d);
                        return Terrace(raw, MathF.Max(5, f.Relief / 4));
                    }
                case "crater":
                    {
                        float rim = f.Half * 0.5f;
                        float ring = MathF.Exp(-MathF.Pow((d - rim) / (f.Half * 0.11f), 2));
                        float outside = SmoothStep(rim, rim + 30, d);
                        float rugged = 0.8f + 0.2f * (Fbm(x / 45f, z / 45f, seed + 67, 3) * 0.5f + 0.5f);
                        return f.Relief * ring * rugged + outside * (f.Relief * 0.18f + Hills(1, recipe.Hills * 0.5f));
                    }
                default: // basin
                    return Hills(Core(d), recipe.Hills);
            }
        }

        /// <summary>Heights in steps of <paramref name="step"/> with short steep risers between (terraces, banded cliffs).</summary>
        private static float Terrace(float h, float step)
        {
            if (step <= 0) return h;
            float k = MathF.Floor(h / step), frac = h / step - k;
            return (k + SmoothStep(0.72f, 1f, frac)) * step;
        }

        public static TerrainMap Build(TerrainRecipe recipe)
        {
            float cell = Math.Clamp(recipe.Cell, 2, 16);
            float sizeX = Math.Clamp(At(recipe.Size, 0, 640), 32, 4096), sizeZ = Math.Clamp(At(recipe.Size, 1, 640), 32, 4096);
            int width = Math.Max(2, (int)MathF.Ceiling(sizeX / cell));
            int depth = Math.Max(2, (int)MathF.Ceiling(sizeZ / cell));
            while ((long)width * depth > MaxCells)
            {
                cell *= 1.25f;
                width = Math.Max(2, (int)MathF.Ceiling(sizeX / cell));
                depth = Math.Max(2, (int)MathF.Ceiling(sizeZ / cell));
            }
            float cx = At(recipe.Center, 0, 0), cz = At(recipe.Center, 1, 0);
            float ox = cx - width * cell / 2, oz = cz - depth * cell / 2;
            var heights = new float[width * depth];
            var painted = new string?[width * depth];
            var biomes = new string?[width * depth];
            int seed = recipe.Seed;
            var form = LandformOf(recipe, sizeX, sizeZ);
            float? water = recipe.WaterLevel;
            if (water == null && (form.Edge == "sea" || form.Shape is "island" or "archipelago" or "coast")) water = SeaLevel;

            // Base shape: the landform, gentle rolls everywhere, the edge.
            for (int row = 0; row < depth; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    float x = ox + (col + 0.5f) * cell, z = oz + (row + 0.5f) * cell;
                    float h = BaseHeight(recipe, form, x, z);
                    if (recipe.Undulation > 0)
                    {
                        // Gentle rolls everywhere: a perfectly flat core reads as a plate, not as land.
                        float us = Math.Max(16, recipe.UndulationScale);
                        h += recipe.Undulation * Fbm(x / us, z / us, seed + 31, 3);
                    }
                    if (recipe.BorderHeight > 0 && form.Edge != "none")
                    {
                        float edge = MathF.Min(MathF.Min(x - ox, ox + width * cell - x), MathF.Min(z - oz, oz + depth * cell - z));
                        float b = 1 - SmoothStep(0, Math.Max(8, recipe.BorderWidth), edge);
                        if (b > 0)
                        {
                            float rugged = 0.65f + 0.35f * (Fbm(x / 40f, z / 40f, seed + 17, 3) * 0.5f + 0.5f);
                            switch (form.Edge)
                            {
                                case "sea":
                                    h = h + (SeaFloor - h) * SmoothStep(0, 0.6f, b);
                                    break;
                                case "cliffs":
                                    h += recipe.BorderHeight * SmoothStep(0.35f, 0.6f, b) * (0.85f + 0.15f * rugged);
                                    break;
                                default:
                                    // Mountains stand on land: the sea round an island or off a coast stays sea.
                                    if (form.Shape is not ("island" or "archipelago" or "coast") || water is not float wl || h > wl)
                                        h += recipe.BorderHeight * b * b * rugged;
                                    break;
                            }
                        }
                    }
                    heights[row * width + col] = h;
                }
            }

            foreach (var op in recipe.Ops)
                Apply(op, heights, painted, biomes, width, depth, ox, oz, cell, ref water);

            // Materials: biome rules by slope, height and shore (the recipe's biome, or a painted region's), then painted materials on top.
            var look = Biomes.TryGetValue(recipe.Biome ?? "meadow", out var b2) ? b2 : Biomes["meadow"];
            string ground = string.IsNullOrWhiteSpace(recipe.Material) ? look.Ground : recipe.Material;
            var names = new List<string> { ground };
            byte Index(string name)
            {
                int i = names.FindIndex(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) return (byte)i;
                names.Add(name);
                return (byte)(names.Count - 1);
            }
            var materials = new byte[width * depth];
            float peakLine = recipe.BorderHeight > 0 && form.Edge is "mountains" or "cliffs" ? recipe.BorderHeight * 0.72f : float.MaxValue;
            // A mountain's top is snow or rock; a canyon's plateau and a valley's walls are land, not peaks.
            if (form.Shape == "mountain") peakLine = MathF.Min(peakLine, form.Relief * 0.7f);
            else if (form.Shape is "canyon" or "valley" or "highlands" or "crater") peakLine = MathF.Max(peakLine, form.Relief + 12);
            // Shores are the ground beside water, a few cells wide — not every low cell on the map (a pond used to
            // turn a whole flat meadow into beach, since the flat core sits just above the water line).
            int shoreCells = Math.Max(1, (int)MathF.Ceiling(10 / cell));
            var fromWater = ShoreDistance(heights, width, depth, water, shoreCells);
            for (int row = 0; row < depth; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    int i = row * width + col;
                    float h = heights[i];
                    float hx = heights[row * width + Math.Min(width - 1, col + 1)] - heights[row * width + Math.Max(0, col - 1)];
                    float hz = heights[Math.Min(depth - 1, row + 1) * width + col] - heights[Math.Max(0, row - 1) * width + col];
                    float slope = MathF.Sqrt(hx * hx + hz * hz) / (2 * cell);
                    var here = biomes[i] is string region && Biomes.TryGetValue(region, out var rl) ? rl : look;
                    string m = biomes[i] != null ? here.Ground : ground;
                    if (water is float w && h < w - 0.5f) m = here.Bed;
                    else if (water is float w2 && h < w2 + 2.5f && fromWater[i] <= shoreCells) m = here.Shore;
                    else if (h > peakLine) m = here.Peak;
                    else if (slope > 0.85f) m = here.Slope;
                    if (painted[i] is string p) m = p;
                    materials[i] = Index(m);
                }
            }

            float min = heights.Min();
            float floor = MathF.Floor(Math.Min(-24, min - 12));
            return new TerrainMap
            {
                OriginX = ox, OriginZ = oz, Cell = cell, Width = width, Depth = depth, Floor = floor,
                Heights = heights, Materials = materials, MaterialNames = names, WaterLevel = water,
            };
        }

        /// <summary>Each cell's distance in cells (4-neighbour steps) to the nearest underwater cell, up to <paramref name="limit"/>; beyond it, int.MaxValue.</summary>
        private static int[] ShoreDistance(float[] heights, int width, int depth, float? water, int limit)
        {
            var distance = new int[heights.Length];
            Array.Fill(distance, int.MaxValue);
            if (water is not float level) return distance;
            var queue = new Queue<int>();
            for (int i = 0; i < heights.Length; i++)
                if (heights[i] < level - 0.5f) { distance[i] = 0; queue.Enqueue(i); }
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                int d = distance[i];
                if (d >= limit) continue;
                int row = i / width, col = i % width;
                void Visit(int r, int c)
                {
                    if (r < 0 || c < 0 || r >= depth || c >= width) return;
                    int n = r * width + c;
                    if (distance[n] <= d + 1) return;
                    distance[n] = d + 1;
                    queue.Enqueue(n);
                }
                Visit(row - 1, col); Visit(row + 1, col); Visit(row, col - 1); Visit(row, col + 1);
            }
            return distance;
        }

        /// <summary>The sculpting steps that cover an area (a circle with a ragged outline, or a polygon of 3+ points).</summary>
        private static readonly HashSet<string> AreaSteps = new() { "flatten", "plateau", "raise", "hill", "lower", "mesa", "mountain", "crater", "island", "lake", "terrace", "roughen", "smooth", "paint" };

        private static void Apply(TerrainOp op, float[] heights, string?[] painted, string?[] biomes, int width, int depth, float ox, float oz, float cell, ref float? water)
        {
            string kind = (op.Kind ?? "").Trim().ToLowerInvariant();
            float falloff = Math.Clamp(op.Falloff, 0, 1);
            float cx = At(op.Center, 0, 0), cz = At(op.Center, 1, 0);
            var points = (op.Points ?? new List<float[]>()).Where(p => p.Length >= 2).ToList();
            bool polygon = points.Count >= 3 && AreaSteps.Contains(kind);
            if (kind is "lake" or "river" && water == null) water = -1f;
            if (kind == "island" && water == null) water = SeaLevel;
            float level = water ?? -1f;
            float rampLength = 0;
            for (int p = 0; p + 1 < points.Count; p++)
                rampLength += MathF.Sqrt((points[p + 1][0] - points[p][0]) * (points[p + 1][0] - points[p][0]) + (points[p + 1][1] - points[p][1]) * (points[p + 1][1] - points[p][1]));
            // A stable seed (string.GetHashCode differs from process to process, and the same recipe must make the same ground).
            int stepSeed = (int)(cx * 13 + cz * 7 + op.Radius * 3) ^ kind.Aggregate(17, (h, ch) => unchecked(h * 31 + ch));
            float rough = Math.Clamp(kind is "mesa" or "island" && op.Rough == 0 ? 0.22f : op.Rough, 0, 0.5f);

            // Where the step reaches: a polygon, or a circle whose outline wanders by Rough.
            float minX = ox, maxX = ox + width * cell, minZ = oz, maxZ = oz + depth * cell;
            if (polygon)
            {
                float pad = Math.Max(4, falloff * 24);
                (minX, maxX, minZ, maxZ) = (points.Min(p => p[0]) - pad, points.Max(p => p[0]) + pad, points.Min(p => p[1]) - pad, points.Max(p => p[1]) + pad);
            }
            else if (AreaSteps.Contains(kind) && op.Center != null)
            {
                float reach = Math.Max(1, op.Radius) * (1 + rough) + 2;
                (minX, maxX, minZ, maxZ) = (cx - reach, cx + reach, cz - reach, cz + reach);
            }
            int c0 = Math.Clamp((int)MathF.Floor((minX - ox) / cell) - 1, 0, width - 1), c1 = Math.Clamp((int)MathF.Ceiling((maxX - ox) / cell) + 1, 0, width - 1);
            int r0 = Math.Clamp((int)MathF.Floor((minZ - oz) / cell) - 1, 0, depth - 1), r1 = Math.Clamp((int)MathF.Ceiling((maxZ - oz) / cell) + 1, 0, depth - 1);

            float Weight(float x, float z, float soft)
            {
                if (polygon)
                {
                    if (!InPolygon(x, z, points)) return 0;
                    float edge = DistanceToPolyline(x, z, points.Append(points[0]).ToList(), out _);
                    return SmoothStep(0, Math.Max(2, soft * 24), edge);
                }
                float r = Math.Max(1, op.Radius);
                if (rough > 0)
                {
                    float theta = MathF.Atan2(z - cz, x - cx);
                    r *= 1 + rough * Fbm(MathF.Cos(theta) * 1.7f + stepSeed % 97 * 0.37f, MathF.Sin(theta) * 1.7f, stepSeed, 3);
                }
                return RoundWeight(x, z, cx, cz, r, soft);
            }

            // Smoothing reads the ground as it was before this step.
            float[]? before = kind == "smooth" ? (float[])heights.Clone() : null;
            int blur = Math.Max(1, (int)MathF.Round(Math.Max(4, op.Width) / cell / 2));

            for (int row = r0; row <= r1; row++)
            {
                for (int col = c0; col <= c1; col++)
                {
                    int i = row * width + col;
                    float x = ox + (col + 0.5f) * cell, z = oz + (row + 0.5f) * cell;
                    switch (kind)
                    {
                        case "flatten":
                        case "plateau":
                            {
                                float w = Weight(x, z, falloff);
                                if (w <= 0) break;
                                heights[i] = Lerp(heights[i], op.Height, w);
                                if (op.Material != null && w > 0.5f) painted[i] = op.Material;
                                break;
                            }
                        case "mesa":
                            {
                                // A flat-topped height with sheer sides (rock shows on them by itself).
                                float w = Weight(x, z, Math.Min(falloff, 0.15f));
                                if (w <= 0) break;
                                float top = op.Height == 0 ? 24 : op.Height;
                                heights[i] = MathF.Max(heights[i], Lerp(heights[i], top, w));
                                if (op.Material != null && w > 0.9f) painted[i] = op.Material;
                                break;
                            }
                        case "raise":
                        case "hill":
                            {
                                float w = Weight(x, z, Math.Max(falloff, 0.6f));
                                if (w > 0) heights[i] += Math.Abs(op.Height == 0 ? 12 : op.Height) * w;
                                if (op.Material != null && w > 0.35f) painted[i] = op.Material;
                                break;
                            }
                        case "mountain":
                            {
                                // A peak with ridged flanks, not a smooth dome.
                                float w = Weight(x, z, 1);
                                if (w <= 0) break;
                                float ridges = 1 - MathF.Abs(Fbm(x / Math.Max(20, op.Radius * 0.45f), z / Math.Max(20, op.Radius * 0.45f), stepSeed, 4));
                                heights[i] += Math.Abs(op.Height == 0 ? 60 : op.Height) * MathF.Pow(w, 1.6f) * (0.72f + 0.28f * ridges);
                                if (op.Material != null && w > 0.35f) painted[i] = op.Material;
                                break;
                            }
                        case "lower":
                            {
                                float w = Weight(x, z, Math.Max(falloff, 0.6f));
                                if (w > 0) heights[i] -= Math.Abs(op.Height == 0 ? 6 : op.Height) * w;
                                break;
                            }
                        case "crater":
                            {
                                // A bowl sunk by Height with a raised rim round it.
                                float r = Math.Max(4, op.Radius);
                                float d = MathF.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz));
                                float depthOf = Math.Abs(op.Height == 0 ? 10 : op.Height);
                                float bowl = 1 - SmoothStep(r * 0.55f, r * 0.85f, d);
                                float rim = MathF.Exp(-MathF.Pow((d - r * 0.9f) / (r * 0.16f), 2));
                                heights[i] += -depthOf * bowl + depthOf * 0.6f * rim;
                                break;
                            }
                        case "island":
                            {
                                // Land rising out of the water to Height, with beaches round it.
                                float w = Weight(x, z, Math.Max(falloff, 0.35f));
                                if (w <= 0) break;
                                heights[i] = MathF.Max(heights[i], Lerp(Math.Min(heights[i], level - 6), op.Height, w));
                                break;
                            }
                        case "lake":
                            {
                                float w = Weight(x, z, Math.Max(falloff, 0.4f));
                                if (w <= 0) break;
                                float bottom = (water ?? level) - Math.Max(2, Math.Abs(op.Height == 0 ? 6 : op.Height));
                                heights[i] = Math.Min(heights[i], Lerp(heights[i], bottom, w));
                                break;
                            }
                        case "terrace":
                            {
                                float w = Weight(x, z, falloff);
                                if (w <= 0) break;
                                heights[i] = Lerp(heights[i], Terrace(heights[i], Math.Max(2, op.Height == 0 ? 8 : op.Height)), w);
                                break;
                            }
                        case "roughen":
                            {
                                float w = Weight(x, z, Math.Max(falloff, 0.5f));
                                if (w <= 0) break;
                                float s = Math.Max(6, op.Width);
                                heights[i] += Math.Abs(op.Height == 0 ? 4 : op.Height) * Fbm(x / s, z / s, stepSeed, 3) * w;
                                break;
                            }
                        case "smooth":
                            {
                                float w = Weight(x, z, Math.Max(falloff, 0.5f));
                                if (w <= 0) break;
                                float sum = 0;
                                int n = 0;
                                for (int dr = -blur; dr <= blur; dr++)
                                    for (int dc = -blur; dc <= blur; dc++)
                                    {
                                        int rr = Math.Clamp(row + dr, 0, depth - 1), cc = Math.Clamp(col + dc, 0, width - 1);
                                        sum += before![rr * width + cc];
                                        n++;
                                    }
                                heights[i] = Lerp(heights[i], sum / n, w);
                                break;
                            }
                        case "ridge":
                            {
                                // A range of hills along a line, its crest wandering in height.
                                if (points.Count < 2) break;
                                float d = DistanceToPolyline(x, z, points, out float along);
                                float half = Math.Max(4, op.Width / 2);
                                if (d >= half) break;
                                float w = 1 - SmoothStep(0, half, d);
                                float crest = 0.7f + 0.3f * (Fbm(along / 40f, 0.5f, stepSeed, 3) * 0.5f + 0.5f);
                                heights[i] += Math.Abs(op.Height == 0 ? 30 : op.Height) * MathF.Pow(w, 1.3f) * crest;
                                if (op.Material != null && w > 0.5f) painted[i] = op.Material;
                                break;
                            }
                        case "canyon":
                        case "trench":
                            {
                                // A cut along a line with steep walls; its floor follows the ground Height studs below it.
                                if (points.Count < 2) break;
                                float d = DistanceToPolyline(x, z, points, out _);
                                float half = Math.Max(3, op.Width / 2);
                                float wall = 3 + falloff * 14;
                                if (d >= half + wall) break;
                                float w = 1 - SmoothStep(half, half + wall, d);
                                heights[i] -= Math.Abs(op.Height == 0 ? 16 : op.Height) * w;
                                if (op.Material != null && d <= half) painted[i] = op.Material;
                                break;
                            }
                        case "ramp":
                            {
                                // A path that climbs evenly from Height at its first point to EndHeight at its last,
                                // with sloped shoulders, so a raised place is reached by a walkable road.
                                if (points.Count < 2) break;
                                float d = DistanceToPolyline(x, z, points, out float along);
                                float half = Math.Max(2, op.Width / 2);
                                float shoulder = half + Math.Max(6, Math.Abs((op.EndHeight ?? op.Height) - op.Height) * 1.2f);
                                if (d > shoulder) break;
                                float t = rampLength > 0 ? Math.Clamp(along / rampLength, 0, 1) : 0;
                                float target = Lerp(op.Height, op.EndHeight ?? op.Height, SmoothStep(0, 1, t));
                                float w = d <= half ? 1 : 1 - SmoothStep(half, shoulder, d);
                                heights[i] = Lerp(heights[i], target, w);
                                if (d <= half && op.Material != null) painted[i] = op.Material;
                                break;
                            }
                        case "path":
                        case "river":
                            {
                                if (points.Count < 2) break;
                                float d = DistanceToPolyline(x, z, points, out float along);
                                float half = Math.Max(2, op.Width / 2);
                                if (d > half * 1.6f) break;
                                float w = 1 - SmoothStep(half * (1 - falloff * 0.5f), half * 1.6f, d);
                                if (kind == "river")
                                {
                                    float bottom = (water ?? level) - Math.Max(2, Math.Abs(op.Height == 0 ? 4 : op.Height));
                                    heights[i] = Math.Min(heights[i], Lerp(heights[i], bottom, w));
                                }
                                else
                                {
                                    // A path follows the ground, smoothed a little so it does not climb every bump.
                                    heights[i] = Lerp(heights[i], heights[i] * 0.6f + op.Height * 0.4f, w * 0.5f);
                                    if (d <= half) painted[i] = op.Material ?? "Ground";
                                }
                                break;
                            }
                        case "paint":
                            {
                                bool inside = points.Count >= 3 ? InPolygon(x, z, points)
                                    : points.Count == 2 ? DistanceToPolyline(x, z, points, out _) <= Math.Max(2, op.Width / 2)
                                    : rough > 0 ? Weight(x, z, 0) > 0.5f
                                    : (x - cx) * (x - cx) + (z - cz) * (z - cz) <= op.Radius * op.Radius;
                                if (!inside) break;
                                if (op.Biome is { Length: > 0 } biome && Biomes.ContainsKey(biome)) biomes[i] = biome;
                                if (op.Material != null || op.Biome == null) painted[i] = op.Material ?? "Ground";
                                break;
                            }
                    }
                }
            }
        }

        // ─────────────── maths ───────────────

        private static float At(float[]? a, int i, float fallback) => a != null && a.Length > i ? a[i] : fallback;

        private static float R(float v) => MathF.Round(v, 1);

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        public static float SmoothStep(float edge0, float edge1, float x)
        {
            if (Math.Abs(edge1 - edge0) < 1e-5f) return x < edge0 ? 0 : 1;
            float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
            return t * t * (3 - 2 * t);
        }

        /// <summary>1 inside the inner radius, easing to 0 at the radius.</summary>
        private static float RoundWeight(float x, float z, float cx, float cz, float radius, float falloff)
        {
            float d = MathF.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz));
            float r = Math.Max(1, radius);
            float inner = r * (1 - falloff);
            if (d <= inner) return 1;
            if (d >= r) return 0;
            return 1 - SmoothStep(inner, r, d);
        }

        public static float DistanceToPolyline(float x, float z, List<float[]> points, out float along)
        {
            float best = float.MaxValue;
            along = 0;
            float walked = 0;
            for (int i = 0; i + 1 < points.Count; i++)
            {
                float ax = points[i][0], az = points[i][1], bx = points[i + 1][0], bz = points[i + 1][1];
                float dx = bx - ax, dz = bz - az;
                float len2 = dx * dx + dz * dz;
                float t = len2 < 1e-6f ? 0 : Math.Clamp(((x - ax) * dx + (z - az) * dz) / len2, 0, 1);
                float px = ax + dx * t, pz = az + dz * t;
                float d = MathF.Sqrt((x - px) * (x - px) + (z - pz) * (z - pz));
                float segment = MathF.Sqrt(len2);
                if (d < best)
                {
                    best = d;
                    along = walked + segment * t;
                }
                walked += segment;
            }
            return best;
        }

        public static bool InPolygon(float x, float z, List<float[]> polygon)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                float xi = polygon[i][0], zi = polygon[i][1], xj = polygon[j][0], zj = polygon[j][1];
                if (zi > z != zj > z && x < (xj - xi) * (z - zi) / (zj - zi + 1e-9f) + xi) inside = !inside;
            }
            return inside;
        }

        private static float Hash(int x, int z, int seed)
        {
            unchecked
            {
                uint h = (uint)x * 374761393u + (uint)z * 668265263u + (uint)seed * 2246822519u;
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0xFFFFFF * 2 - 1;
            }
        }

        /// <summary>Smooth value noise in -1…1.</summary>
        public static float Noise(float x, float z, int seed)
        {
            int x0 = (int)MathF.Floor(x), z0 = (int)MathF.Floor(z);
            float tx = x - x0, tz = z - z0;
            float sx = tx * tx * (3 - 2 * tx), sz = tz * tz * (3 - 2 * tz);
            float a = Hash(x0, z0, seed), b = Hash(x0 + 1, z0, seed), c = Hash(x0, z0 + 1, seed), d = Hash(x0 + 1, z0 + 1, seed);
            return Lerp(Lerp(a, b, sx), Lerp(c, d, sx), sz);
        }

        /// <summary>Fractal noise (octaves of value noise) in about -1…1.</summary>
        public static float Fbm(float x, float z, int seed, int octaves)
        {
            float sum = 0, amp = 1, norm = 0, freq = 1;
            for (int o = 0; o < octaves; o++)
            {
                sum += Noise(x * freq, z * freq, seed + o * 101) * amp;
                norm += amp;
                amp *= 0.5f;
                freq *= 2.03f;
            }
            return sum / norm;
        }
    }
}
