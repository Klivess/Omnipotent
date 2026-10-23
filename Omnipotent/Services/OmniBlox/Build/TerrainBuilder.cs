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
    /// always gives the same ground): a flat core at y = 0 for building, rolling hills outside it, rugged
    /// mountains along the edges so the map has no visible end, then the recipe's operations in order, and
    /// finally materials by biome (rock on steep slopes, snow on peaks, sand on shores) with painted areas on top.
    /// </summary>
    public static class TerrainBuilder
    {
        public const int MaxCells = 300 * 300;

        private sealed record BiomeLook(string Ground, string Slope, string Peak, string Shore, string Bed);

        private static readonly Dictionary<string, BiomeLook> Biomes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["meadow"] = new("Grass", "Rock", "Snow", "Sand", "Sand"),
            ["forest"] = new("LeafyGrass", "Rock", "Snow", "Ground", "Mud"),
            ["desert"] = new("Sand", "Sandstone", "Sandstone", "Sand", "Sand"),
            ["snow"] = new("Snow", "Glacier", "Snow", "Ice", "Slate"),
            ["beach"] = new("Grass", "Rock", "Rock", "Sand", "Sand"),
            ["volcanic"] = new("Basalt", "Basalt", "CrackedLava", "Ground", "Basalt"),
            ["candy"] = new("Grass", "Pavement", "Snow", "Sand", "Sand"),
        };

        public static readonly string[] Materials =
        {
            "Grass", "LeafyGrass", "Sand", "Rock", "Snow", "Ground", "Mud", "Slate", "Ice", "Glacier", "Sandstone", "Basalt",
            "CrackedLava", "Asphalt", "Cobblestone", "Brick", "Pavement", "Limestone", "Salt", "WoodPlanks", "Concrete",
        };

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
            int seed = recipe.Seed;

            // Base shape: flat core, hills, border mountains.
            for (int row = 0; row < depth; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    float x = ox + (col + 0.5f) * cell, z = oz + (row + 0.5f) * cell;
                    float d = MathF.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz));
                    float h = 0;
                    if (recipe.Undulation > 0)
                    {
                        // Gentle rolls everywhere: a perfectly flat core reads as a plate, not as land.
                        float us = Math.Max(16, recipe.UndulationScale);
                        h += recipe.Undulation * Fbm(x / us, z / us, seed + 31, 3);
                    }
                    if (recipe.Hills > 0)
                    {
                        float scale = Math.Max(16, recipe.HillScale);
                        float n = Fbm(x / scale, z / scale, seed, 4) * 0.5f + 0.5f; // 0..1
                        float weight = SmoothStep(recipe.FlatRadius, recipe.FlatRadius + Math.Max(24, scale * 0.6f), d);
                        h += recipe.Hills * MathF.Pow(n, 1.4f) * weight;
                    }
                    if (recipe.BorderHeight > 0)
                    {
                        float edge = MathF.Min(MathF.Min(x - ox, ox + width * cell - x), MathF.Min(z - oz, oz + depth * cell - z));
                        float b = 1 - SmoothStep(0, Math.Max(8, recipe.BorderWidth), edge);
                        if (b > 0)
                        {
                            float rugged = 0.65f + 0.35f * (Fbm(x / 40f, z / 40f, seed + 17, 3) * 0.5f + 0.5f);
                            h += recipe.BorderHeight * b * b * rugged;
                        }
                    }
                    heights[row * width + col] = h;
                }
            }

            float? water = recipe.WaterLevel;
            foreach (var op in recipe.Ops)
                Apply(op, heights, painted, width, depth, ox, oz, cell, ref water);

            // Materials: biome rules by slope, height and shore, then painted areas on top.
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
            float peakLine = recipe.BorderHeight > 0 ? recipe.BorderHeight * 0.72f : float.MaxValue;
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
                    string m = ground;
                    if (water is float w && h < w - 0.5f) m = look.Bed;
                    else if (water is float w2 && h < w2 + 2.5f && fromWater[i] <= shoreCells) m = look.Shore;
                    else if (h > peakLine) m = look.Peak;
                    else if (slope > 0.85f) m = look.Slope;
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

        private static void Apply(TerrainOp op, float[] heights, string?[] painted, int width, int depth, float ox, float oz, float cell, ref float? water)
        {
            string kind = (op.Kind ?? "").Trim().ToLowerInvariant();
            float falloff = Math.Clamp(op.Falloff, 0, 1);
            float cx = At(op.Center, 0, 0), cz = At(op.Center, 1, 0);
            var points = (op.Points ?? new List<float[]>()).Where(p => p.Length >= 2).ToList();
            float level = water ?? -1f;
            if (kind is "lake" or "river" && water == null) water = level;
            float rampLength = 0;
            for (int p = 0; p + 1 < points.Count; p++)
                rampLength += MathF.Sqrt((points[p + 1][0] - points[p][0]) * (points[p + 1][0] - points[p][0]) + (points[p + 1][1] - points[p][1]) * (points[p + 1][1] - points[p][1]));

            for (int row = 0; row < depth; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    int i = row * width + col;
                    float x = ox + (col + 0.5f) * cell, z = oz + (row + 0.5f) * cell;
                    switch (kind)
                    {
                        case "flatten":
                        case "plateau":
                            {
                                float w = RoundWeight(x, z, cx, cz, op.Radius, falloff);
                                if (w <= 0) break;
                                heights[i] = Lerp(heights[i], op.Height, w);
                                if (op.Material != null && w > 0.5f) painted[i] = op.Material;
                                break;
                            }
                        case "raise":
                        case "hill":
                            {
                                float w = RoundWeight(x, z, cx, cz, op.Radius, Math.Max(falloff, 0.6f));
                                if (w > 0) heights[i] += Math.Abs(op.Height == 0 ? 12 : op.Height) * w;
                                if (op.Material != null && w > 0.35f) painted[i] = op.Material;
                                break;
                            }
                        case "lower":
                        case "crater":
                            {
                                float w = RoundWeight(x, z, cx, cz, op.Radius, Math.Max(falloff, 0.6f));
                                if (w > 0) heights[i] -= Math.Abs(op.Height == 0 ? 6 : op.Height) * w;
                                break;
                            }
                        case "lake":
                            {
                                float w = RoundWeight(x, z, cx, cz, op.Radius, Math.Max(falloff, 0.4f));
                                if (w <= 0) break;
                                float bottom = (water ?? level) - Math.Max(2, Math.Abs(op.Height == 0 ? 6 : op.Height));
                                heights[i] = Math.Min(heights[i], Lerp(heights[i], bottom, w));
                                break;
                            }
                        case "ramp":
                            {
                                // A path that climbs evenly from Height at its first point to EndHeight at its last,
                                // with sloped shoulders, so a raised zone is reached by a walkable road.
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
                                    : (x - cx) * (x - cx) + (z - cz) * (z - cz) <= op.Radius * op.Radius;
                                if (inside) painted[i] = op.Material ?? "Ground";
                                break;
                            }
                    }
                }
            }
        }

        // ─────────────── maths ───────────────

        private static float At(float[]? a, int i, float fallback) => a != null && a.Length > i ? a[i] : fallback;

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
