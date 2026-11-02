using System.Globalization;
using System.Text;

namespace Omnipotent.Services.OmniBlox.Build.WorldGen
{
    /// <summary>A map's look by biome: lighting, ground materials, accent colours, and the trees and touches that belong in it.</summary>
    public static class MapLook
    {
        public static readonly string[] Biomes = { "meadow", "forest", "autumn", "snow", "desert", "beach", "jungle", "swamp", "volcanic", "candy", "spooky", "crystal" };

        public static string LightingFor(string biome) => (biome ?? "").ToLowerInvariant() switch
        {
            "autumn" => "golden",
            "snow" => "snowy",
            "beach" or "jungle" => "tropical",
            "candy" or "crystal" => "candy",
            "spooky" or "swamp" => "spooky",
            "volcanic" => "golden",
            _ => "sunny",
        };

        /// <summary>Terrain look for a biome: the ground, a plaza and a path material.</summary>
        public static (string Ground, string Plaza, string Path) MaterialsFor(string biome) => (biome ?? "").ToLowerInvariant() switch
        {
            "forest" => ("LeafyGrass", "Cobblestone", "Ground"),
            "autumn" => ("Grass", "Cobblestone", "Ground"),
            "snow" => ("Snow", "Slate", "Ice"),
            "desert" => ("Sand", "Sandstone", "Sandstone"),
            "beach" => ("Sand", "WoodPlanks", "Sand"),
            "jungle" => ("LeafyGrass", "Limestone", "Mud"),
            "swamp" => ("Mud", "WoodPlanks", "Ground"),
            "volcanic" => ("Basalt", "Slate", "CrackedLava"),
            "candy" => ("Grass", "Pavement", "Salt"),
            "spooky" => ("Mud", "Slate", "Ground"),
            "crystal" => ("Slate", "Limestone", "Pavement"),
            _ => ("Grass", "Cobblestone", "Ground"),
        };

        public static string AccentFor(string biome) => (biome ?? "").ToLowerInvariant() switch
        {
            "forest" => "#2E8B57",
            "autumn" => "#E07A1F",
            "snow" => "#3FA9F5",
            "desert" => "#E0A21B",
            "beach" => "#13B5C8",
            "jungle" => "#2FA84F",
            "swamp" => "#6B8E23",
            "volcanic" => "#E8442E",
            "candy" => "#FF5FB8",
            "spooky" => "#8E44FF",
            "crystal" => "#7B6CFF",
            _ => "#3D8BFF",
        };

        public static string LeafColor(string biome) => (biome ?? "").ToLowerInvariant() switch
        {
            "autumn" => "#E0782A",
            "snow" => "#2F6B4F",
            "candy" => "#FF8BD0",
            "spooky" or "swamp" => "#4B3A5A",
            _ => "#4FA043",
        };

        /// <summary>The tree roles a biome grows, the main one first.</summary>
        public static string[] TreeRoles(string biome) => (biome ?? "").ToLowerInvariant() switch
        {
            "snow" => new[] { Roles.Conifer, Roles.Tree },
            "forest" => new[] { Roles.Conifer, Roles.Tree },
            "beach" or "jungle" => new[] { Roles.Palm, Roles.Tree },
            "spooky" or "swamp" => new[] { Roles.DeadTree, Roles.Tree },
            "desert" => new[] { Roles.Cactus, Roles.Palm },
            "crystal" => new[] { Roles.Crystal, Roles.Conifer },
            "candy" => new[] { Roles.Candy, Roles.Tree },
            "volcanic" => new[] { Roles.DeadTree, Roles.Conifer },
            _ => new[] { Roles.Tree, Roles.Conifer },
        };

        /// <summary>The small touches that tell a biome (mushrooms in a forest, pumpkins in autumn, cacti in a desert…).</summary>
        public static string[] AccentRoles(string biome) => (biome ?? "").ToLowerInvariant() switch
        {
            "forest" => new[] { Roles.Mushroom, Roles.Stump, Roles.Log },
            "autumn" => new[] { Roles.Pumpkin, Roles.Stump, Roles.Log },
            "spooky" => new[] { Roles.Pumpkin, Roles.DeadTree },
            "swamp" => new[] { Roles.Mushroom, Roles.Log, Roles.DeadTree },
            "desert" => new[] { Roles.Cactus },
            "beach" or "jungle" => new[] { Roles.Palm },
            "candy" => new[] { Roles.Candy },
            "crystal" or "volcanic" => new[] { Roles.Crystal },
            "snow" => new[] { Roles.Conifer },
            _ => new[] { Roles.Stump },
        };

        /// <summary>The first biome a piece of text names ("misty pine forest" → forest), or null.</summary>
        public static string? BiomeIn(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string t = text.ToLowerInvariant();
            return Biomes.Where(b => t.Contains(b, StringComparison.Ordinal)).OrderBy(b => t.IndexOf(b, StringComparison.Ordinal)).FirstOrDefault();
        }
    }

    /// <summary>What a landscaping pass placed, for the agent to read.</summary>
    public sealed class LandscapeResult
    {
        public int Placed { get; set; }
        public int Parts { get; set; }
        public int MapParts { get; set; }
        public List<string> Notes { get; } = new();
        public List<string> Warnings { get; } = new();
        /// <summary>Roles the library had no model for in this style (the harvester is asked to find them).</summary>
        public SortedSet<string> Missing { get; } = new(StringComparer.Ordinal);
        /// <summary>Role → how many were placed and from which models.</summary>
        public SortedDictionary<string, (int Count, SortedSet<long> Models)> Roles { get; } = new(StringComparer.Ordinal);

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append($"Placed {Placed} object(s), about {Parts} parts (the map now carries about {MapParts} of the {Landscaper.MapBudget} parts phones run well).");
            if (Roles.Count > 0)
                sb.Append(" " + string.Join("; ", Roles.Select(kv => $"{kv.Key} ×{kv.Value.Count}" + (kv.Value.Models.Count > 0 ? " (" + string.Join(",", kv.Value.Models) + ")" : ""))) + ".");
            sb.AppendLine();
            foreach (var n in Notes) sb.AppendLine("- " + n);
            foreach (var w in Warnings) sb.AppendLine("! " + w);
            return sb.ToString();
        }
    }

    /// <summary>
    /// Places library models on a map the way a level artist would, for the builder's landscaping and role-based
    /// placement tools: every model stands on the lowest ground under its contact footprint (trees on their trunk)
    /// and is refused on slopes too steep for it; nothing lands on paths, spawns, water or what already stands
    /// there (a spatial hash); one art style per map (the map's own, else the style the library covers best);
    /// untextured (PBR-only) models are left out; and everything shares a part budget phones carry.
    ///
    /// It used to drive a fixed hub-and-zones layout, so every game had the same map. Now the builder decides the
    /// map — the landform, the places, the paths — and calls this for each area it dresses.
    /// </summary>
    public sealed class Landscaper
    {
        /// <summary>About as many parts as a whole map should carry for phones (scenery, props and gameplay).</summary>
        public const int MapBudget = 6000;

        public SceneDocument Scene { get; }
        public TerrainMap? Terrain { get; }
        public AssetCatalog Catalog { get; }
        public Random Rng { get; }
        public string Style { get; }
        public List<SceneObject> Objects { get; } = new();
        public LandscapeResult Result { get; } = new();
        /// <summary>Parts on the whole map (what was there plus what this pass placed).</summary>
        public int Parts { get; private set; }
        public int Budget { get; set; } = MapBudget;
        public string Source { get; init; } = "dress";
        private readonly int startParts;
        private readonly HashSet<string> usedIds;
        private int nextId = 1;
        private readonly SpatialIndex occupied = new(16);
        private readonly WorldOps.Keepout keepout;
        private readonly Dictionary<string, List<CatalogEntry>> palettes = new();

        public Landscaper(SceneDocument scene, TerrainMap? terrain, AssetCatalog catalog, IEnumerable<Footprint> standing, int seed)
        {
            Scene = scene;
            Terrain = terrain;
            Catalog = catalog;
            Rng = new Random(seed * 7919 + 17);
            usedIds = scene.Objects.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
            string biome = scene.Land?.Biome ?? "meadow";
            Style = scene.Style is { Length: > 0 } s ? AssetCatalog.StyleFamily(s) : catalog.Entries.Count > 0 ? catalog.DominantStyle(biome) : "stylized";
            Parts = startParts = PartsOf(scene, catalog);
            foreach (var f in standing) occupied.Add(f.X, f.Z, f.Radius);
            keepout = WorldOps.DefaultKeepout(scene);
        }

        /// <summary>How many parts a scene carries: each library model by its measured parts, everything else one.</summary>
        public static int PartsOf(SceneDocument scene, AssetCatalog catalog) =>
            scene.Objects.Sum(o => o.Kind == SceneKinds.Asset && o.AssetId is long a && catalog.Get(a) is { } e ? Math.Max(1, e.Parts) : 1);

        public string NewId()
        {
            string id;
            do id = "d" + nextId++.ToString(CultureInfo.InvariantCulture);
            while (usedIds.Contains(id));
            usedIds.Add(id);
            return id;
        }

        public PropBuilder Prop(string folder, float x, float z, float yaw) => new(Objects, NewId, folder, x, GroundAt(x, z), z, yaw);

        public float GroundAt(float x, float z) => WorldOps.GroundAt(Scene, Terrain, x, z);
        public float SlopeAt(float x, float z) => Terrain != null && Terrain.Covers(x, z) ? Terrain.SlopeAt(x, z) : 0;
        public bool Wet(float x, float z) => Terrain != null && Terrain.Covers(x, z) && Terrain.InWater(x, z);

        public void KeepLine(List<float[]> points, float half) => keepout.Lines.Add((points, half));
        public void KeepCircle(float x, float z, float r) => keepout.Circles.Add((x, z, r));
        public void KeepRectangle(float[] area) => keepout.Rectangles.Add(area);

        /// <summary>Clear of what stands there and of paths, spawns and areas kept out.</summary>
        public bool Free(float x, float z, float radius) => !occupied.Overlaps(x, z, radius) && !keepout.Blocks(x, z, radius);
        /// <summary>Clear of what stands there (paths not checked: for things that belong beside them).</summary>
        public bool Unoccupied(float x, float z, float radius) => !occupied.Overlaps(x, z, radius);
        public void Occupy(float x, float z, float radius) => occupied.Add(x, z, radius);

        /// <summary>
        /// The models for a role in a biome: <paramref name="chosen"/> first (models the call names), then the map's
        /// palette, then the library's best in the map's style (2-4 of them).
        /// </summary>
        public List<CatalogEntry> Palette(string role, string biome, IReadOnlyCollection<long>? chosen = null, int count = 3)
        {
            string key = role + "|" + biome + "|" + (chosen == null ? "" : string.Join(",", chosen));
            if (palettes.TryGetValue(key, out var cached)) return cached;
            var list = new List<CatalogEntry>();
            if (chosen != null)
                foreach (long id in chosen)
                    if (Catalog.Get(id) is { } e && e.Role == role && list.All(c => c.AssetId != id)) list.Add(e);
            if (list.Count == 0 && Scene.Palette.TryGetValue(role, out var ids))
                foreach (long id in ids)
                    if (Catalog.Get(id) is { } e && list.All(c => c.AssetId != id)) list.Add(e);
            if (list.Count == 0)
            {
                list = Catalog.Best(role, biome, Style, count).Where(e => !e.Untextured && AssetCatalog.Affinity(e, biome) > -0.3).ToList();
                // A role this style lacks: allow the other stylised families, never a jarring realistic one in a cartoon map.
                if (list.Count == 0 && Style != "realistic")
                    list = Catalog.Best(role, biome, "", count).Where(e => !e.Untextured && e.Style != "realistic" && AssetCatalog.Affinity(e, biome) > -0.3).ToList();
            }
            if (list.Count == 0) Result.Missing.Add(role);
            palettes[key] = list;
            return list;
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

        /// <summary>
        /// Places a library model standing on the ground: its footprint (turned by its yaw) is sampled at the centre,
        /// the corners and the edge middles, and it stands at the lowest of them, sunk a little — so it never hangs
        /// over a slope. Returns null when the spot is too steep for it or the budget is spent.
        /// </summary>
        public SceneObject? PlaceModel(CatalogEntry e, float x, float z, float yaw, float scale, string folder, string? name = null,
            IEnumerable<string>? tags = null, Dictionary<string, object>? config = null, bool force = false, float occupyFactor = 0.42f)
        {
            int parts = Math.Max(1, e.Parts);
            OutOfBudget = !force && Parts + parts > Budget;
            if (OutOfBudget) return null;
            scale = Math.Clamp(scale, 0.25f, 4f);
            var (y, spread) = SeatOn(GroundAt, e, x, z, yaw, scale);
            // Too steep for this model: one side would hang in the air or the other be buried. A model straddling a
            // cliff's top edge would be seated at its foot, deep in the ground under its middle: refused as well.
            if (!force && (spread > MaxSpread(e, scale) || GroundAt(x, z) - y > MathF.Max(1.2f, e.Height * scale * 0.3f))) return null;
            var o = new SceneObject
            {
                Id = NewId(), Kind = SceneKinds.Asset, AssetId = e.AssetId, Name = name ?? Short(e.Name), Folder = folder, Source = Source,
                Position = new[] { MathF.Round(x, 2), MathF.Round(y, 2), MathF.Round(z, 2) }, Yaw = PropBuilder.Norm(yaw), Scale = MathF.Round(scale, 2),
                Tags = tags?.ToList() ?? new List<string>(), Config = config ?? new Dictionary<string, object>(),
            };
            Objects.Add(o);
            Parts += parts;
            Occupy(x, z, e.Footprint * scale * occupyFactor);
            Count(e.Role, e.AssetId);
            return o;
        }

        /// <summary>A kit-built stand-in (a part-built tree or crystal) where the library has no model: it counts like one.</summary>
        public void Placed(string role, int parts, float x, float z, float radius)
        {
            Parts += parts;
            Occupy(x, z, radius);
            Count(role, null);
        }

        private void Count(string role, long? assetId)
        {
            var (n, models) = Result.Roles.TryGetValue(role, out var entry) ? entry : (0, new SortedSet<long>());
            if (assetId is long id) models.Add(id);
            Result.Roles[role] = (n + 1, models);
            Result.Placed++;
        }

        /// <summary>The last <see cref="PlaceModel"/> returned null because the part budget is spent (not because the spot was too steep).</summary>
        public bool OutOfBudget { get; private set; }

        public bool BudgetSpent => Parts >= Budget;

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
            int attempts = Math.Min(60000, Math.Max(200, max * attemptsPerPoint));
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

        /// <summary>Puts what was placed into the scene and fills in the totals.</summary>
        public LandscapeResult Finish()
        {
            Scene.Objects.AddRange(Objects);
            Result.Parts = Parts - startParts;
            Result.MapParts = Parts;
            if (Parts > MapBudget) Result.Warnings.Add($"the map carries about {Parts} parts, over the {MapBudget} phones run well: thin the scenery (remove_objects a dressing folder and dress it again with a lower density)");
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
