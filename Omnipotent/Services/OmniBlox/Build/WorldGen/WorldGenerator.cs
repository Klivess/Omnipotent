using System.Globalization;
using System.Text;

namespace Omnipotent.Services.OmniBlox.Build.WorldGen
{
    /// <summary>What a generation made, for the agent (and the map report) to read.</summary>
    public sealed class GenerationResult
    {
        public required SceneDocument Scene { get; init; }
        public List<string> Notes { get; } = new();
        public List<string> Warnings { get; } = new();
        public int Generated { get; set; }
        public int EstimatedParts { get; set; }
        /// <summary>Role → the library models it used.</summary>
        public SortedDictionary<string, SortedSet<long>> Models { get; } = new(StringComparer.Ordinal);

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Generated {Generated} objects (about {EstimatedParts} parts).");
            foreach (var n in Notes) sb.AppendLine("- " + n);
            if (Models.Count > 0)
                sb.AppendLine("Models by role: " + string.Join("; ", Models.Select(kv => $"{kv.Key} {string.Join(",", kv.Value)}")));
            foreach (var w in Warnings) sb.AppendLine("! " + w);
            return sb.ToString();
        }
    }

    /// <summary>
    /// Turns a <see cref="WorldSpec"/> into a finished map. For the hub layout:
    /// <list type="number">
    /// <item>Layout: a spawn hub in the middle, zones round it in the order players reach them, the spawn in a gap
    /// between paths, looking over the hub towards the first zone.</item>
    /// <item>Land: rolling ground everywhere, a flattened hub with a paved plaza, each zone a plateau a little higher
    /// than the last with its own ground material, ramped roads from the hub, forested ridges between zones,
    /// rocky mounds, a lake where there is room, mountains past the edges so the world never ends in a void.</item>
    /// <item>Gameplay: the spawn pad, a sell pad in sight of the spawn, stands for upgrades and the shop, a rebirth
    /// altar, a leaderboard, a gate at each zone's entrance, the zone's resource nodes and landmark, signposts.</item>
    /// <item>Dressing in layers: a ring of trees round each zone (open at its gate), dense groves on the ridges
    /// between zones (with small clearings), a tree line past the play area, bushes and flowers by paths and
    /// props round the hub, a few accents in each zone. Everything stands on the ground at its lowest point,
    /// faces what it should, keeps clear of paths and of everything else, and the whole stays in a part budget
    /// phones can carry.</item>
    /// </list>
    /// Deterministic: the same spec and library give the same map.
    /// </summary>
    public static class WorldGenerator
    {
        public const string Source = "gen";
        public const int PartBudget = 5600;

        public static GenerationResult Generate(WorldSpec spec, GameManifest manifest, AssetCatalog catalog, SceneDocument? existing = null)
        {
            var gen = new Generation(spec, manifest, catalog, existing);
            string layout = (spec.Layout ?? "hub").Trim().ToLowerInvariant();
            if (layout == "course") ObbyCourse.Build(gen);
            else if (layout == "plots") TycoonPlots.Build(gen);
            else HubLayout.Build(gen);
            return gen.Finish();
        }

        /// <summary>Default zones for a biome when the spec names none: four, opening in order, each a new biome.</summary>
        public static List<ZoneSpec> DefaultZones(string biome, string resource)
        {
            var cycle = (biome ?? "meadow").ToLowerInvariant() switch
            {
                "snow" => new[] { "snow", "forest", "crystal", "volcanic" },
                "desert" => new[] { "desert", "beach", "volcanic", "crystal" },
                "candy" => new[] { "candy", "meadow", "crystal", "spooky" },
                "beach" => new[] { "beach", "jungle", "volcanic", "crystal" },
                "spooky" => new[] { "spooky", "autumn", "volcanic", "crystal" },
                _ => new[] { "meadow", "forest", "autumn", "snow" },
            };
            var names = new Dictionary<string, string>
            {
                ["meadow"] = "Sunny Meadow", ["forest"] = "Pine Forest", ["autumn"] = "Autumn Woods", ["snow"] = "Frosty Peaks", ["desert"] = "Desert Dunes",
                ["beach"] = "Palm Beach", ["jungle"] = "Jungle", ["volcanic"] = "Lava Lands", ["candy"] = "Candy Land", ["spooky"] = "Spooky Hollow", ["crystal"] = "Crystal Caves",
            };
            return cycle.Select((b, i) => new ZoneSpec
            {
                Id = "z" + (i + 1), Name = names.TryGetValue(b, out var n) ? n : "Zone " + (i + 1), Biome = b, Order = i + 1,
                Price = HubLayout.StartingPrice(i + 1),
                Resource = new ResourceSpec { Kind = resource, Count = 12, Hp = (int)Math.Round(4 * Math.Pow(2.4, i)), Value = (int)Math.Round(2 * Math.Pow(2.7, i)), Respawn = 8 },
            }).ToList();
        }

        public static string LightingFor(string biome) => (biome ?? "").ToLowerInvariant() switch
        {
            "autumn" => "golden",
            "snow" => "snowy",
            "beach" or "jungle" => "tropical",
            "candy" or "crystal" => "candy",
            "spooky" => "spooky",
            "volcanic" => "golden",
            _ => "sunny",
        };

        /// <summary>Terrain look for a biome: the ground, the plaza and the path materials.</summary>
        public static (string Ground, string Plaza, string Path) MaterialsFor(string biome) => (biome ?? "").ToLowerInvariant() switch
        {
            "forest" => ("LeafyGrass", "Cobblestone", "Ground"),
            "autumn" => ("Grass", "Cobblestone", "Ground"),
            "snow" => ("Snow", "Slate", "Ice"),
            "desert" => ("Sand", "Sandstone", "Sandstone"),
            "beach" => ("Sand", "WoodPlanks", "Sand"),
            "jungle" => ("LeafyGrass", "Limestone", "Mud"),
            "volcanic" => ("Basalt", "Slate", "CrackedLava"),
            "candy" => ("Grass", "Pavement", "Salt"),
            "spooky" => ("Mud", "Slate", "Ground"),
            "crystal" => ("Slate", "Limestone", "Pavement"),
            _ => ("Grass", "Cobblestone", "Ground"),
        };

        /// <summary>The roles a zone's resource kind is drawn from.</summary>
        public static string[] RolesForResource(string kind, string biome) => (kind ?? "tree").Trim().ToLowerInvariant() switch
        {
            "tree" or "wood" or "log" or "lumber" => biome is "snow" or "forest" ? new[] { Roles.Conifer, Roles.Tree } : biome is "beach" or "jungle" ? new[] { Roles.Palm, Roles.Tree } : biome == "spooky" ? new[] { Roles.DeadTree, Roles.Tree } : new[] { Roles.Tree, Roles.Conifer },
            "pine" or "conifer" => new[] { Roles.Conifer, Roles.Tree },
            "palm" => new[] { Roles.Palm, Roles.Tree },
            "rock" or "stone" or "ore" or "boulder" => new[] { Roles.Rock, Roles.Boulder },
            "crystal" or "gem" or "ice" => new[] { Roles.Crystal },
            "mushroom" => new[] { Roles.Mushroom },
            "pumpkin" => new[] { Roles.Pumpkin },
            "cactus" => new[] { Roles.Cactus },
            "candy" or "sweet" => new[] { Roles.Candy },
            "flower" => new[] { Roles.Flower },
            "bush" or "berry" => new[] { Roles.Bush },
            _ => new[] { Roles.Tree, Roles.Rock },
        };

        public static float YawToward(float fromX, float fromZ, float toX, float toZ)
        {
            float dx = toX - fromX, dz = toZ - fromZ;
            if (dx * dx + dz * dz < 1e-6f) return 0;
            float len = MathF.Sqrt(dx * dx + dz * dz);
            return PropBuilder.Norm(MathF.Atan2(-dx / len, -dz / len) * 180 / MathF.PI);
        }

        /// <summary>Direction for an angle in degrees, clockwise from −Z seen from above (0 = −Z, 90 = +X).</summary>
        public static (float X, float Z) Dir(float degrees)
        {
            float r = degrees * MathF.PI / 180;
            return (MathF.Sin(r), -MathF.Cos(r));
        }
    }

    /// <summary>The working state of one generation: the scene, the ground, what is occupied, ids, the part budget.</summary>
    public sealed class Generation
    {
        public WorldSpec Spec { get; }
        public GameManifest Manifest { get; }
        public AssetCatalog Catalog { get; }
        public Random Rng { get; }
        public SceneDocument Scene { get; }
        public List<SceneObject> Objects { get; } = new();
        public GenerationResult Result { get; }
        public TerrainMap? Ground { get; private set; }
        public string Style { get; }
        public int Parts { get; private set; }
        public int Budget { get; set; } = WorldGenerator.PartBudget;
        /// <summary>Parts left for scenery once the gameplay is placed (what the dressing layers share).</summary>
        public int DressingBudget => Math.Max(0, WorldGenerator.PartBudget - Parts);
        private readonly HashSet<string> usedIds;
        private int nextId = 1;
        private readonly SpatialIndex occupied = new(16);
        private readonly List<(List<float[]> Points, float Half)> keepLines = new();
        private readonly List<(float X, float Z, float R)> keepCircles = new();
        private readonly Dictionary<string, List<CatalogEntry>> palettes = new();

        public Generation(WorldSpec spec, GameManifest manifest, AssetCatalog catalog, SceneDocument? existing)
        {
            Spec = spec;
            Manifest = manifest;
            Catalog = catalog;
            Rng = new Random(spec.Seed * 7919 + 17);
            // Keep what an agent placed by hand; replace what the generator made last time.
            Scene = existing != null ? SceneDocument.Clone(existing) : new SceneDocument();
            Scene.Objects.RemoveAll(o => o.Source == WorldGenerator.Source);
            Scene.Spec = spec;
            usedIds = Scene.Objects.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
            Result = new GenerationResult { Scene = Scene };
            Style = spec.Style is { Length: > 0 } s ? AssetCatalog.StyleFamily(s) : catalog.Entries.Count > 0 ? catalog.DominantStyle(spec.Biome) : "stylized";
            Result.Notes.Add($"Art style: {Style}.");
            foreach (var o in Scene.Objects) Parts += o.Kind == SceneKinds.Asset && o.AssetId is long a && catalog.Get(a) is { } e ? Math.Max(1, e.Parts) : 1;
        }

        public string NewId()
        {
            string id;
            do id = "g" + nextId++.ToString(CultureInfo.InvariantCulture);
            while (usedIds.Contains(id));
            usedIds.Add(id);
            return id;
        }

        public PropBuilder Prop(string folder, float x, float z, float yaw) => new(Objects, NewId, folder, x, GroundAt(x, z), z, yaw);
        /// <summary>A prop standing on something other than the ground (a platform in the air) at height <paramref name="y"/>.</summary>
        public PropBuilder PropAt(string folder, float x, float y, float z, float yaw) => new(Objects, NewId, folder, x, y, z, yaw);

        public void SetGround(TerrainRecipe recipe)
        {
            Scene.Land = recipe;
            Ground = TerrainBuilder.Build(recipe);
        }

        public float GroundAt(float x, float z) => Ground != null && Ground.Covers(x, z) ? Ground.HeightAt(x, z) : 0;
        public float SlopeAt(float x, float z) => Ground != null && Ground.Covers(x, z) ? Ground.SlopeAt(x, z) : 0;
        public bool Wet(float x, float z) => Ground != null && Ground.Covers(x, z) && Ground.InWater(x, z);

        // ── keep-out ──

        public void KeepLine(List<float[]> points, float half) => keepLines.Add((points, half));
        public void KeepCircle(float x, float z, float r) => keepCircles.Add((x, z, r));

        public bool Kept(float x, float z, float margin)
        {
            foreach (var (pts, half) in keepLines)
                if (TerrainBuilder.DistanceToPolyline(x, z, pts, out _) < half + margin) return true;
            foreach (var (cx, cz, cr) in keepCircles)
                if ((x - cx) * (x - cx) + (z - cz) * (z - cz) < (cr + margin) * (cr + margin)) return true;
            return false;
        }

        public bool Free(float x, float z, float radius) => !occupied.Overlaps(x, z, radius) && !Kept(x, z, radius);
        /// <summary>Clear of other objects (keep-out areas such as roads are not checked: for things that belong beside them).</summary>
        public bool Unoccupied(float x, float z, float radius) => !occupied.Overlaps(x, z, radius);
        public void Occupy(float x, float z, float radius) => occupied.Add(x, z, radius);

        // ── palettes ──

        /// <summary>The models used for a role in a biome: the spec's own choice first, then the library's best (2-4 of them).</summary>
        public List<CatalogEntry> Palette(string role, string biome, IDictionary<string, List<long>>? overrides = null, int count = 3)
        {
            string key = role + "|" + biome + "|" + (overrides == null ? "" : string.Join(",", overrides.TryGetValue(role, out var o) ? o : new List<long>()));
            if (palettes.TryGetValue(key, out var cached)) return cached;
            var chosen = new List<CatalogEntry>();
            foreach (var src in new[] { overrides, Spec.Palette })
                if (src != null && src.TryGetValue(role, out var ids))
                    foreach (long id in ids)
                        if (Catalog.Get(id) is { } e && chosen.All(c => c.AssetId != id)) chosen.Add(e);
                        else if (Catalog.Get(id) == null) Result.Warnings.Add($"palette model {id} for '{role}' is not a placeable, labelled library model; skipped");
            if (chosen.Count == 0)
            {
                chosen = Catalog.Best(role, biome, Style, count).Where(e => !e.Untextured && AssetCatalog.Affinity(e, biome) > -0.3).ToList();
                // A role this style lacks: allow the other stylised families, never a jarring realistic one in a cartoon map.
                if (chosen.Count == 0 && Style != "realistic")
                    chosen = Catalog.Best(role, biome, "", count).Where(e => !e.Untextured && e.Style != "realistic" && AssetCatalog.Affinity(e, biome) > -0.3).ToList();
            }
            palettes[key] = chosen;
            return chosen;
        }

        /// <summary>One model from a palette, the best ones more often.</summary>
        public CatalogEntry? Pick(List<CatalogEntry> palette)
        {
            if (palette.Count == 0) return null;
            double total = 0;
            var weights = palette.Select((e, i) => { double w = 1.0 / (1 + i * 0.6); total += w; return w; }).ToList();
            double r = Rng.NextDouble() * total;
            for (int i = 0; i < palette.Count; i++)
            {
                r -= weights[i];
                if (r <= 0) return palette[i];
            }
            return palette[^1];
        }

        // ── placing ──

        /// <summary>
        /// Places a library model standing on the ground: its footprint (turned by its yaw) is sampled at the centre,
        /// the corners and the edge middles, and it stands at the lowest of them, sunk a little — so it never hangs
        /// over a slope. Returns null when it would not fit the budget.
        /// </summary>
        public SceneObject? PlaceModel(CatalogEntry e, float x, float z, float yaw, float scale, string folder, string? name = null,
            IEnumerable<string>? tags = null, Dictionary<string, object>? config = null, bool force = false, float occupyFactor = 0.42f)
        {
            int parts = Math.Max(1, e.Parts);
            OutOfBudget = !force && Parts + parts > Budget;
            if (OutOfBudget) return null;
            scale = Math.Clamp(scale, 0.25f, 4f);
            var (y, spread) = Seat(e, x, z, yaw, scale);
            // Too steep for this model: one side would hang in the air or the other be buried.
            if (!force && spread > MaxSpread(e, scale)) return null;
            var o = new SceneObject
            {
                Id = NewId(), Kind = SceneKinds.Asset, AssetId = e.AssetId, Name = name ?? Short(e.Name), Folder = folder, Source = WorldGenerator.Source,
                Position = new[] { MathF.Round(x, 2), MathF.Round(y, 2), MathF.Round(z, 2) }, Yaw = PropBuilder.Norm(yaw), Scale = MathF.Round(scale, 2),
                Tags = tags?.ToList() ?? new List<string>(), Config = config ?? new Dictionary<string, object>(),
            };
            Objects.Add(o);
            Parts += parts;
            Occupy(x, z, e.Footprint * scale * occupyFactor);
            if (!Result.Models.TryGetValue(e.Role, out var set)) Result.Models[e.Role] = set = new SortedSet<long>();
            set.Add(e.AssetId);
            return o;
        }

        /// <summary>The last <see cref="PlaceModel"/> returned null because the part budget is spent (not because the spot was too steep).</summary>
        public bool OutOfBudget { get; private set; }

        /// <summary>
        /// How much of a model's footprint touches the ground: a tree stands on its trunk (its canopy hangs over the
        /// slope), a rock or a building on all of it.
        /// </summary>
        public static float Contact(string role) => role switch
        {
            Roles.Tree or Roles.Conifer or Roles.Palm or Roles.DeadTree or Roles.Cactus => 0.2f,
            Roles.Lamp or Roles.Sign or Roles.Statue or Roles.Landmark => 0.45f,
            Roles.Bush or Roles.Flower or Roles.Grass or Roles.Mushroom or Roles.Crystal or Roles.Candy or Roles.Pumpkin => 0.65f,
            _ => 0.9f,
        };

        /// <summary>How uneven the ground under a model may be (studs between its lowest and highest contact) before the spot is refused.</summary>
        public static float MaxSpread(CatalogEntry e, float scale) => e.Role switch
        {
            Roles.Tree or Roles.Conifer or Roles.Palm or Roles.DeadTree or Roles.Cactus => 2.6f,
            Roles.Rock or Roles.Boulder => MathF.Max(1.2f, e.Height * scale * 0.45f),
            Roles.Building or Roles.Stall or Roles.Fountain or Roles.Well or Roles.Bench or Roles.Fence => 0.9f,
            _ => MathF.Max(0.8f, e.Height * scale * 0.3f),
        };

        /// <summary>
        /// Where a model's bottom lands: the lowest ground under its contact footprint (sampled at the centre, the
        /// corners, the edge middles and between), sunk by a little of its height; and how uneven that ground is.
        /// </summary>
        public (float Y, float Spread) Seat(CatalogEntry e, float x, float z, float yaw, float scale) => SeatOn(GroundAt, e, x, z, yaw, scale);

        public static (float Y, float Spread) SeatOn(Func<float, float, float> GroundAt, CatalogEntry e, float x, float z, float yaw, float scale)
        {
            float a = (yaw + e.FrontYaw) * MathF.PI / 180f;
            float c = MathF.Cos(a), s = MathF.Sin(a);
            float k = Contact(e.Role);
            float hx = MathF.Max(0.6f, e.SizeX * scale / 2 * k), hz = MathF.Max(0.6f, e.SizeZ * scale / 2 * k);
            float lowest = GroundAt(x, z), highest = lowest;
            foreach (var (lx, lz) in new[] { (-hx, -hz), (hx, -hz), (-hx, hz), (hx, hz), (0f, -hz), (0f, hz), (-hx, 0f), (hx, 0f), (-hx * 0.5f, -hz * 0.5f), (hx * 0.5f, hz * 0.5f), (-hx * 0.5f, hz * 0.5f), (hx * 0.5f, -hz * 0.5f) })
            {
                float h = GroundAt(x + c * lx + s * lz, z - s * lx + c * lz);
                lowest = MathF.Min(lowest, h);
                highest = MathF.Max(highest, h);
            }
            float sink = MathF.Min(0.6f, e.Height * scale * 0.04f);
            return (lowest - sink, highest - lowest);
        }

        /// <summary>Runs one dressing layer with its own share of the parts left, so no layer starves the others.</summary>
        public void WithBudget(float share, int total, Action layer)
        {
            int saved = Budget;
            Budget = Math.Min(saved, Parts + (int)(total * share));
            try { layer(); }
            finally { Budget = saved; }
        }

        /// <summary>The scale that brings a model near a target height, varied a little (no two trees alike).</summary>
        public float ScaleFor(CatalogEntry e, float targetHeight, float jitter = 0.15f)
        {
            float baseScale = e.Height > 0.1f ? targetHeight / e.Height : 1;
            // Keep a model near its own size when it is already plausible: big models stay big, small stay small.
            var (minH, maxH, _) = Roles.Bounds(e.Role);
            if (e.Height >= minH && e.Height <= maxH) baseScale = 1 + (baseScale - 1) * 0.55f;
            float j = 1 + ((float)Rng.NextDouble() * 2 - 1) * jitter;
            return Math.Clamp(baseScale * j, 0.4f, 3.2f);
        }

        public void AddParts(int n) => Parts += n;

        /// <summary>Spots inside a region, at least <paramref name="spacing"/> apart, each passing <paramref name="fits"/> (dart throwing on a grid).</summary>
        public List<(float X, float Z)> Darts(float minX, float minZ, float maxX, float maxZ, float spacing, int max, Func<float, float, bool> fits, int attemptsPerPoint = 30)
        {
            var found = new List<(float X, float Z)>();
            var grid = new SpatialIndex(Math.Max(4, spacing));
            int attempts = Math.Max(200, max * attemptsPerPoint);
            for (int i = 0; i < attempts && found.Count < max; i++)
            {
                float x = minX + (float)Rng.NextDouble() * (maxX - minX), z = minZ + (float)Rng.NextDouble() * (maxZ - minZ);
                if (grid.Overlaps(x, z, spacing / 2)) continue;
                if (!fits(x, z)) continue;
                grid.Add(x, z, spacing / 2);
                found.Add((x, z));
            }
            return found;
        }

        public static string Short(string name)
        {
            string s = new string(name.Where(ch => char.IsLetterOrDigit(ch) || ch == ' ').ToArray()).Trim();
            if (s.Length == 0) s = "Model";
            return s.Length <= 32 ? s : s[..32].TrimEnd();
        }

        public GenerationResult Finish()
        {
            Scene.Objects.AddRange(Objects);
            Result.Generated = Objects.Count;
            Result.EstimatedParts = Parts;
            if (Parts > Budget) Result.Warnings.Add($"about {Parts} parts is over the {Budget} budget phones carry well");
            return Result;
        }
    }

    /// <summary>Circles on the ground in a hash grid: is this spot clear of what is already there?</summary>
    public sealed class SpatialIndex
    {
        private readonly float cell;
        private readonly Dictionary<(int, int), List<(float X, float Z, float R)>> cells = new();
        private float maxR;

        public SpatialIndex(float cell) => this.cell = Math.Max(1, cell);

        public void Add(float x, float z, float r)
        {
            var key = ((int)MathF.Floor(x / cell), (int)MathF.Floor(z / cell));
            if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<(float, float, float)>();
            list.Add((x, z, r));
            maxR = MathF.Max(maxR, r);
        }

        public bool Overlaps(float x, float z, float r)
        {
            int reach = (int)MathF.Ceiling((r + maxR) / cell);
            int cx = (int)MathF.Floor(x / cell), cz = (int)MathF.Floor(z / cell);
            for (int dz = -reach; dz <= reach; dz++)
                for (int dx = -reach; dx <= reach; dx++)
                {
                    if (!cells.TryGetValue((cx + dx, cz + dz), out var list)) continue;
                    foreach (var (ox, oz, or) in list)
                        if ((ox - x) * (ox - x) + (oz - z) * (oz - z) < (or + r) * (or + r)) return true;
                }
            return false;
        }
    }
}
