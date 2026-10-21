namespace Omnipotent.Services.OmniBlox.Build
{
    /// <summary>A circle on the ground something already occupies (x, z, radius).</summary>
    public readonly record struct Footprint(float X, float Z, float Radius, string ObjectId);

    /// <summary>A spot chosen for a new object.</summary>
    public readonly record struct Spot(float X, float Y, float Z, float Yaw);

    /// <summary>
    /// The geometry behind the world-building tools: how high the ground is anywhere, what is already standing
    /// where, and where new things can go. Scattering uses Poisson-disk sampling (no two objects closer than the
    /// spacing, no grid look) and keeps clear of paths, spawns, water, steep slopes, other objects and any areas
    /// it is told to avoid; so a forest of 80 trees is one call and looks natural.
    /// </summary>
    public static class WorldOps
    {
        /// <summary>The ground's height at a point: generated terrain, else the top of any terrain block fill there, else 0.</summary>
        public static float GroundAt(SceneDocument scene, TerrainMap? terrain, float x, float z)
        {
            if (terrain != null && terrain.Covers(x, z)) return terrain.HeightAt(x, z);
            float? best = null;
            foreach (var t in scene.Terrain)
            {
                if (t.Shape == "ball") continue;
                if (MathF.Abs(x - t.Center[0]) <= t.Size[0] / 2 && MathF.Abs(z - t.Center[2]) <= t.Size[2] / 2)
                {
                    float top = t.Center[1] + t.Size[1] / 2;
                    if (best == null || top > best) best = top;
                }
            }
            return best ?? 0;
        }

        /// <summary>What every compiled object occupies on the ground (its bounding box as a circle).</summary>
        public static List<Footprint> Footprints(IReadOnlyList<WorldPart> parts, float ignoreBiggerThan = 60)
        {
            var list = new List<Footprint>();
            foreach (var g in parts.Where(p => p.CanCollide || p.Transparency < 0.9f).GroupBy(p => p.ObjectId))
            {
                float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                foreach (var p in g)
                {
                    foreach (var c in Assets.AssetAnalyzer.Corners(p.CFrame, p.Size))
                    {
                        minX = MathF.Min(minX, c.X); maxX = MathF.Max(maxX, c.X);
                        minZ = MathF.Min(minZ, c.Z); maxZ = MathF.Max(maxZ, c.Z);
                    }
                }
                float r = MathF.Max(maxX - minX, maxZ - minZ) / 2;
                if (r > ignoreBiggerThan) continue; // a floor or a big platform: things may stand on it
                list.Add(new Footprint((minX + maxX) / 2, (minZ + maxZ) / 2, r, g.Key));
            }
            return list;
        }

        /// <summary>Areas new objects must keep out of: paths (planned and carved), around spawns, and given rectangles.</summary>
        public sealed class Keepout
        {
            public List<(List<float[]> Points, float HalfWidth)> Lines { get; } = new();
            public List<float[]> Rectangles { get; } = new();
            public List<(float X, float Z, float R)> Circles { get; } = new();

            public bool Blocks(float x, float z, float margin)
            {
                foreach (var (points, half) in Lines)
                    if (points.Count >= 2 && TerrainBuilder.DistanceToPolyline(x, z, points, out _) < half + margin) return true;
                foreach (var r in Rectangles)
                    if (x >= r[0] - margin && x <= r[2] + margin && z >= r[1] - margin && z <= r[3] + margin) return true;
                foreach (var (cx, cz, cr) in Circles)
                    if ((x - cx) * (x - cx) + (z - cz) * (z - cz) < (cr + margin) * (cr + margin)) return true;
                return false;
            }
        }

        /// <summary>The usual keep-out set: every planned path, every carved terrain path, and a clearing round each spawn.</summary>
        public static Keepout DefaultKeepout(SceneDocument scene, float spawnClearing = 14)
        {
            var k = new Keepout();
            foreach (var p in scene.Layout.Paths.Where(p => p.Points.Count >= 2)) k.Lines.Add((p.Points, p.Width / 2));
            foreach (var op in scene.Land?.Ops ?? new List<TerrainOp>())
                if (op.Kind is "path" or "river" && op.Points is { Count: >= 2 } pts) k.Lines.Add((pts, op.Width / 2));
            foreach (var s in scene.Objects.Where(o => o.Kind == SceneKinds.Spawn)) k.Circles.Add((s.Position[0], s.Position[2], spawnClearing));
            return k;
        }

        /// <summary>
        /// Up to <paramref name="count"/> spots inside <paramref name="area"/> (a polygon of x,z points, or a
        /// rectangle as 2 opposite corners), at least <paramref name="spacing"/> apart and clear of everything.
        /// </summary>
        public static List<Spot> Scatter(SceneDocument scene, TerrainMap? terrain, List<float[]> area, int count, float spacing, float objectRadius,
            IReadOnlyList<Footprint> occupied, Keepout keepout, int seed, float maxSlope = 0.65f, bool allowWater = false, bool randomYaw = true)
        {
            var polygon = area.Count == 2
                ? new List<float[]> { new[] { MathF.Min(area[0][0], area[1][0]), MathF.Min(area[0][1], area[1][1]) }, new[] { MathF.Max(area[0][0], area[1][0]), MathF.Min(area[0][1], area[1][1]) },
                                      new[] { MathF.Max(area[0][0], area[1][0]), MathF.Max(area[0][1], area[1][1]) }, new[] { MathF.Min(area[0][0], area[1][0]), MathF.Max(area[0][1], area[1][1]) } }
                : area;
            if (polygon.Count < 3) throw new ArgumentException("an area needs 2 corners or at least 3 points");
            float minX = polygon.Min(p => p[0]), maxX = polygon.Max(p => p[0]), minZ = polygon.Min(p => p[1]), maxZ = polygon.Max(p => p[1]);
            float r = Math.Max(1.5f, spacing);
            var rng = new Random(seed);
            float cellSize = r / MathF.Sqrt(2);
            int gw = Math.Max(1, (int)MathF.Ceiling((maxX - minX) / cellSize)), gh = Math.Max(1, (int)MathF.Ceiling((maxZ - minZ) / cellSize));
            var grid = new int[gw * gh];
            Array.Fill(grid, -1);
            var samples = new List<(float X, float Z)>();
            var active = new List<int>();

            bool Fits(float x, float z)
            {
                if (x < minX || x > maxX || z < minZ || z > maxZ) return false;
                if (!TerrainBuilder.InPolygon(x, z, polygon)) return false;
                if (!scene.Bounds.Contains(x, z, -objectRadius)) return false;
                if (keepout.Blocks(x, z, objectRadius)) return false;
                if (terrain != null && terrain.Covers(x, z))
                {
                    if (!allowWater && terrain.InWater(x, z)) return false;
                    if (terrain.SlopeAt(x, z) > maxSlope) return false;
                }
                foreach (var f in occupied)
                    if ((f.X - x) * (f.X - x) + (f.Z - z) * (f.Z - z) < (f.Radius + objectRadius) * (f.Radius + objectRadius)) return false;
                int gx = (int)((x - minX) / cellSize), gz = (int)((z - minZ) / cellSize);
                for (int dz = -2; dz <= 2; dz++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int nx = gx + dx, nz = gz + dz;
                        if (nx < 0 || nz < 0 || nx >= gw || nz >= gh) continue;
                        int s = grid[nz * gw + nx];
                        if (s >= 0 && (samples[s].X - x) * (samples[s].X - x) + (samples[s].Z - z) * (samples[s].Z - z) < r * r) return false;
                    }
                }
                return true;
            }

            void Add(float x, float z)
            {
                samples.Add((x, z));
                int idx = samples.Count - 1;
                grid[(int)((z - minZ) / cellSize) * gw + (int)((x - minX) / cellSize)] = idx;
                active.Add(idx);
            }

            // Seed points: try a few random starts (an area may be partly blocked).
            for (int attempt = 0; attempt < 60 && samples.Count == 0; attempt++)
            {
                float x = minX + (float)rng.NextDouble() * (maxX - minX), z = minZ + (float)rng.NextDouble() * (maxZ - minZ);
                if (Fits(x, z)) Add(x, z);
            }
            int cap = Math.Max(count * 4, count + 50);
            while (active.Count > 0 && samples.Count < cap)
            {
                int pick = rng.Next(active.Count);
                var (ax, az) = samples[active[pick]];
                bool placed = false;
                for (int k = 0; k < 24; k++)
                {
                    double angle = rng.NextDouble() * Math.PI * 2, dist = r * (1 + rng.NextDouble());
                    float x = ax + (float)(Math.Cos(angle) * dist), z = az + (float)(Math.Sin(angle) * dist);
                    if (Fits(x, z)) { Add(x, z); placed = true; break; }
                }
                if (!placed) active.RemoveAt(pick);
                // Blocked islands: re-seed from a random point now and then.
                if (active.Count == 0 && samples.Count < count)
                {
                    for (int attempt = 0; attempt < 40; attempt++)
                    {
                        float x = minX + (float)rng.NextDouble() * (maxX - minX), z = minZ + (float)rng.NextDouble() * (maxZ - minZ);
                        if (Fits(x, z)) { Add(x, z); break; }
                    }
                }
            }
            // Pick count of them spread over the whole area (shuffle keeps the spacing, removes the growth order).
            var chosen = samples.OrderBy(_ => rng.Next()).Take(count).ToList();
            return chosen.Select(p => new Spot(p.X, GroundAt(scene, terrain, p.X, p.Z), p.Z, randomYaw ? (float)(rng.NextDouble() * 360) : 0)).ToList();
        }

        /// <summary>
        /// Spots every <paramref name="spacing"/> studs along a line, offset sideways (both sides when asked),
        /// facing along the line (yaw) or towards it.
        /// </summary>
        public static List<Spot> Along(SceneDocument scene, TerrainMap? terrain, List<float[]> points, float spacing, float offset, bool bothSides, bool faceLine, float startAt = 0)
        {
            var spots = new List<Spot>();
            if (points.Count < 2) return spots;
            float step = Math.Max(1, spacing);
            float carried = startAt;
            for (int i = 0; i + 1 < points.Count; i++)
            {
                float ax = points[i][0], az = points[i][1], bx = points[i + 1][0], bz = points[i + 1][1];
                float dx = bx - ax, dz = bz - az, len = MathF.Sqrt(dx * dx + dz * dz);
                if (len < 0.01f) continue;
                float ux = dx / len, uz = dz / len;
                // Roblox yaw: 0 faces -Z; facing direction (ux, uz) is yaw = atan2(-ux, -uz).
                float alongYaw = MathF.Atan2(-ux, -uz) * 180 / MathF.PI;
                for (float t = carried; t <= len; t += step)
                {
                    float x = ax + ux * t, z = az + uz * t;
                    foreach (int side in bothSides ? new[] { -1, 1 } : new[] { 1 })
                    {
                        if (offset == 0 && side == -1) continue;
                        // Perpendicular to the right of travel is (-uz, ux).
                        float px = x + -uz * offset * side, pz = z + ux * offset * side;
                        float yaw = faceLine ? alongYaw + (offset == 0 ? 0 : side * 90) : alongYaw;
                        spots.Add(new Spot(px, GroundAt(scene, terrain, px, pz), pz, yaw));
                    }
                }
                carried = step - ((len - carried) % step);
                if (carried >= step) carried = 0;
            }
            return spots;
        }

        /// <summary>count spots evenly round a circle, facing out (or in).</summary>
        public static List<Spot> Ring(SceneDocument scene, TerrainMap? terrain, float cx, float cz, float radius, int count, bool faceIn)
        {
            var spots = new List<Spot>();
            for (int i = 0; i < count; i++)
            {
                float a = i * MathF.PI * 2 / Math.Max(1, count);
                float x = cx + MathF.Cos(a) * radius, z = cz + MathF.Sin(a) * radius;
                float ox = x - cx, oz = z - cz;
                float yaw = MathF.Atan2(-ox, -oz) * 180 / MathF.PI + (faceIn ? 180 : 0);
                spots.Add(new Spot(x, GroundAt(scene, terrain, x, z), z, yaw));
            }
            return spots;
        }
    }
}
