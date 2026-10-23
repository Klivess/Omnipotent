using Newtonsoft.Json;
using Omnipotent.Services.OmniBlox.Rbx;

namespace Omnipotent.Services.OmniBlox.Build
{
    /// <summary>
    /// The world of one game, as data the build agent edits (scene.json). Compiled into Workspace by
    /// <see cref="PlaceCompiler"/>. Positions are in studs. For library assets <see cref="SceneObject.Position"/>
    /// is where the asset's bottom-centre lands (so "on the ground" is the ground's height there); for parts and
    /// spawns it is the part's centre, as in Roblox.
    ///
    /// Besides the objects it holds the map's design: the generated terrain (<see cref="TerrainRecipe"/>, baked
    /// into a heightmap by <see cref="TerrainBuilder"/>), the playable area (<see cref="Bounds"/>), properties of
    /// folders (a tycoon plot is a tagged folder), and the layout plan (zones, paths, landmarks) the map is built
    /// to and checked against.
    /// </summary>
    public sealed class SceneDocument
    {
        public LightingSpec Lighting { get; set; } = new();
        /// <summary>Simple block and ball fills (the first studio's terrain; still supported).</summary>
        public List<TerrainFill> Terrain { get; set; } = new();
        /// <summary>Generated terrain: hills, flat building ground, borders, water, paths. Null: none.</summary>
        public TerrainRecipe? Land { get; set; }
        public List<SceneObject> Objects { get; set; } = new();
        /// <summary>The playable area; the verifier flags anything outside it and the kit keeps players inside it.</summary>
        public MapBounds Bounds { get; set; } = new();
        /// <summary>Folder path → what the folder is (Model or Folder) and the tags and values it carries.</summary>
        public Dictionary<string, FolderSpec> Folders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>The map plan: zones, the paths between them and the landmarks a player navigates by.</summary>
        public LayoutPlan Layout { get; set; } = new();
        /// <summary>The design the world was generated from (<see cref="WorldGen.WorldGenerator"/>); null for a hand-built world.</summary>
        public WorldGen.WorldSpec? Spec { get; set; }

        public SceneObject? Find(string id) => Objects.FirstOrDefault(o => o.Id == id);

        /// <summary>A deep copy (through JSON, so it is exactly what would be saved and loaded).</summary>
        public static SceneDocument Clone(SceneDocument scene) =>
            JsonConvert.DeserializeObject<SceneDocument>(JsonConvert.SerializeObject(scene, Json), Json) ?? new SceneDocument();

        public string NextId(string prefix)
        {
            int n = 1;
            var used = Objects.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
            while (used.Contains(prefix + n)) n++;
            return prefix + n;
        }

        /// <summary>Ids for a batch: the next free ones, without re-scanning the list per id.</summary>
        public IEnumerable<string> NextIds(string prefix, int count)
        {
            var used = Objects.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
            int n = 1;
            for (int i = 0; i < count; i++)
            {
                while (used.Contains(prefix + n)) n++;
                used.Add(prefix + n);
                yield return prefix + n;
            }
        }

        public static SceneDocument Load(string path)
        {
            if (!File.Exists(path)) return new SceneDocument();
            return JsonConvert.DeserializeObject<SceneDocument>(File.ReadAllText(path), Json) ?? new SceneDocument();
        }

        public void Save(string path) => Core.OmniBloxStore.WriteAtomic(path, JsonConvert.SerializeObject(this, Json));

        internal static readonly JsonSerializerSettings Json = new()
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            MissingMemberHandling = MissingMemberHandling.Ignore,
        };
    }

    /// <summary>
    /// The playable area. The first studio only had a half-width; a map now has a rectangle players stay inside
    /// (the kit puts invisible walls on its edges and rescues anyone who falls off), set by set_map_area.
    /// </summary>
    public sealed class MapBounds
    {
        public float HalfWidth { get; set; } = 512;
        public float MinY { get; set; } = -60;
        public float MaxY { get; set; } = 600;
        /// <summary>The walkable rectangle (null until the builder sets one; then the kit walls it in).</summary>
        public float[]? Area { get; set; }
        /// <summary>Invisible walls on the area's edges (default on once an area is set).</summary>
        public bool Walls { get; set; } = true;

        [JsonIgnore] public bool HasArea => Area is { Length: 4 };
        [JsonIgnore] public float MinX => HasArea ? Area![0] : -HalfWidth;
        [JsonIgnore] public float MinZ => HasArea ? Area![1] : -HalfWidth;
        [JsonIgnore] public float MaxX => HasArea ? Area![2] : HalfWidth;
        [JsonIgnore] public float MaxZ => HasArea ? Area![3] : HalfWidth;
        [JsonIgnore] public float Width => MaxX - MinX;
        [JsonIgnore] public float Depth => MaxZ - MinZ;

        public bool Contains(float x, float z, float margin = 0) => x >= MinX - margin && x <= MaxX + margin && z >= MinZ - margin && z <= MaxZ + margin;
    }

    public sealed class LightingSpec
    {
        /// <summary>sunny (default), golden, night, spooky, tropical, snowy, candy, overcast; "day" and "sunset" still work.</summary>
        public string Preset { get; set; } = "sunny";
        public float? ClockTime { get; set; }
        public float? Brightness { get; set; }
        public string? Ambient { get; set; }
        public bool Shadows { get; set; } = true;
        /// <summary>Colour saturation boost (-1…1, default per preset): kid-appealing worlds are a little more vivid.</summary>
        public float? Saturation { get; set; }
        /// <summary>Atmosphere haze density (0…1).</summary>
        public float? Haze { get; set; }
        /// <summary>A tint over everything ("#FFF0E0" for a warm look).</summary>
        public string? Tint { get; set; }
    }

    /// <summary>Terrain is generated by the Game Kit at server start (Terrain:FillBlock), so no voxel data is serialized.</summary>
    public sealed class TerrainFill
    {
        /// <summary>block or ball.</summary>
        public string Shape { get; set; } = "block";
        public string Material { get; set; } = "Grass";
        public float[] Center { get; set; } = { 0, -4, 0 };
        public float[] Size { get; set; } = { 512, 8, 512 };
    }

    /// <summary>
    /// Generated terrain, as a recipe the agent edits and <see cref="TerrainBuilder"/> bakes into a heightmap.
    /// Ground level is y = 0 over the flat core; hills rise outside it; a border of mountains (or water) frames
    /// the map; the operations then sculpt it in order (flatten a plaza, raise a hill, dig a lake, lay a path).
    /// </summary>
    public sealed class TerrainRecipe
    {
        /// <summary>Centre of the terrain (x, z).</summary>
        public float[] Center { get; set; } = { 0, 0 };
        /// <summary>Size (x, z) in studs; it should reach beyond the playable area so there is no visible edge.</summary>
        public float[] Size { get; set; } = { 640, 640 };
        /// <summary>Grid resolution in studs (4 is Roblox's voxel size).</summary>
        public float Cell { get; set; } = 4;
        /// <summary>The ground's material; null means the biome's own (grass for a meadow, sand for a desert, snow…).</summary>
        public string? Material { get; set; }
        /// <summary>meadow, forest, desert, snow, beach, volcanic, candy: picks the materials of slopes, peaks and shores.</summary>
        public string Biome { get; set; } = "meadow";
        /// <summary>Rolling hills outside the flat core: height in studs (0 = flat everywhere).</summary>
        public float Hills { get; set; } = 10;
        /// <summary>How wide hills are (studs between bumps).</summary>
        public float HillScale { get; set; } = 120;
        public int Seed { get; set; } = 1;
        /// <summary>Radius around the centre kept flat at y = 0 for building (studs).</summary>
        public float FlatRadius { get; set; } = 160;
        /// <summary>Mountains rising at the terrain's edges (height in studs; 0 = none).</summary>
        public float BorderHeight { get; set; } = 60;
        /// <summary>How far in from the edges the border mountains start (studs).</summary>
        public float BorderWidth { get; set; } = 70;
        /// <summary>Water fills everything below this level (null = no water).</summary>
        public float? WaterLevel { get; set; }
        /// <summary>Gentle rolling of the ground everywhere, the flat core included (studs; 0 = none). Flatten and plateau steps still level their areas.</summary>
        public float Undulation { get; set; }
        /// <summary>Studs between the rolls of <see cref="Undulation"/>.</summary>
        public float UndulationScale { get; set; } = 70;
        public List<TerrainOp> Ops { get; set; } = new();
    }

    /// <summary>One sculpting step: flatten, raise, lower, lake, path, ramp, paint or plateau.</summary>
    public sealed class TerrainOp
    {
        public string Kind { get; set; } = "flatten";
        /// <summary>Centre (x, z) for round operations.</summary>
        public float[]? Center { get; set; }
        public float Radius { get; set; } = 30;
        /// <summary>Points (x, z) of a path or polygon.</summary>
        public List<float[]>? Points { get; set; }
        public float Width { get; set; } = 12;
        /// <summary>Target height for flatten/plateau, amount for raise/lower, depth for a lake; a ramp's height at its first point.</summary>
        public float Height { get; set; }
        /// <summary>A ramp's height at its last point (it climbs evenly between).</summary>
        public float? EndHeight { get; set; }
        public string? Material { get; set; }
        /// <summary>How soft the edge is (0 = hard, 1 = very soft).</summary>
        public float Falloff { get; set; } = 0.5f;
    }

    /// <summary>A folder's own properties: a Model instead of a Folder, and tags and values the kit reads (TycoonPlot…).</summary>
    public sealed class FolderSpec
    {
        /// <summary>Folder or Model.</summary>
        public string Class { get; set; } = "Folder";
        public List<string> Tags { get; set; } = new();
        public Dictionary<string, object> Config { get; set; } = new();
    }

    /// <summary>The map plan, written before building and checked by the map report.</summary>
    public sealed class LayoutPlan
    {
        /// <summary>One paragraph: what the map is and how a player moves through it.</summary>
        public string Concept { get; set; } = "";
        public List<MapZone> Zones { get; set; } = new();
        public List<MapPath> Paths { get; set; } = new();
        public List<MapLandmark> Landmarks { get; set; } = new();
    }

    public sealed class MapZone
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>What happens there (the hub with the shops, the first farming area…).</summary>
        public string Purpose { get; set; } = "";
        /// <summary>Rectangle min x, min z, max x, max z.</summary>
        public float[] Area { get; set; } = { -20, -20, 20, 20 };
        /// <summary>Order a player reaches it (1 = the spawn hub).</summary>
        public int Order { get; set; } = 1;
        /// <summary>Its look ("autumn forest", "candy desert").</summary>
        public string Theme { get; set; } = "";
        /// <summary>A round zone: centre x, z and radius (generated maps); the rectangle bounds it.</summary>
        public float[]? Circle { get; set; }
        /// <summary>Where the way in is (its gate, for a gated zone).</summary>
        public float[]? Entrance { get; set; }
        /// <summary>The zone's ground height.</summary>
        public float? Elevation { get; set; }
        /// <summary>Players must unlock it (a ZoneGate guards its entrance and the kit keeps them out until they do).</summary>
        public bool Gated { get; set; }

        [JsonIgnore] public float CenterX => (Area[0] + Area[2]) / 2;
        [JsonIgnore] public float CenterZ => (Area[1] + Area[3]) / 2;
        public bool Contains(float x, float z) => Circle is { Length: 3 } c
            ? (x - c[0]) * (x - c[0]) + (z - c[1]) * (z - c[1]) <= c[2] * c[2]
            : x >= Area[0] && x <= Area[2] && z >= Area[1] && z <= Area[3];
    }

    public sealed class MapPath
    {
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public List<float[]> Points { get; set; } = new();
        public float Width { get; set; } = 12;
    }

    public sealed class MapLandmark
    {
        public string Name { get; set; } = "";
        public float[] Position { get; set; } = { 0, 0 };
        /// <summary>Object id once built.</summary>
        public string? ObjectId { get; set; }
        public string Note { get; set; } = "";
    }

    public static class SceneKinds
    {
        public const string Asset = "asset";
        public const string Part = "part";
        public const string Spawn = "spawn";
        public const string Light = "light";
        public const string Text = "text";
    }

    public sealed class SceneObject
    {
        public string Id { get; set; } = "";
        /// <summary>asset, part, spawn, light or text (a floating sign).</summary>
        public string Kind { get; set; } = SceneKinds.Part;
        public string Name { get; set; } = "";
        /// <summary>Folder path under Workspace, e.g. "Obby/Stage3". Empty = Workspace itself.</summary>
        public string Folder { get; set; } = "";
        public float[] Position { get; set; } = { 0, 0, 0 };
        /// <summary>Rotation about Y in degrees. For assets, 0 means the asset's labelled front faces −Z (Roblox forward).</summary>
        public float Yaw { get; set; }
        /// <summary>Optional full rotation (degrees about X, Y, Z); overrides <see cref="Yaw"/> for parts.</summary>
        public float[]? Rotation { get; set; }
        public float Scale { get; set; } = 1;

        /// <summary>"gen" for what the world generator made (a regeneration replaces it); null for what an agent placed.</summary>
        public string? Source { get; set; }
        /// <summary>The prop this part belongs to (a sign's board and its posts): the verifier never reports the pieces of one prop touching each other.</summary>
        public string? Group { get; set; }

        // asset
        public long? AssetId { get; set; }
        /// <summary>Recolours every part of an asset ("#RRGGBB"), e.g. a golden version of a tree.</summary>
        public string? Tint { get; set; }

        // part / spawn
        /// <summary>Block, Ball, Cylinder, Wedge, CornerWedge, Truss.</summary>
        public string? Shape { get; set; }
        public float[]? Size { get; set; }
        public string? Color { get; set; }
        public string? Material { get; set; }
        public float? Transparency { get; set; }
        public bool Anchored { get; set; } = true;
        public bool CanCollide { get; set; } = true;
        public bool? CastShadow { get; set; }

        // light
        /// <summary>point, spot or surface.</summary>
        public string? LightType { get; set; }
        public float? Range { get; set; }
        public float? Brightness { get; set; }

        // text sign
        public string? Text { get; set; }
        /// <summary>A sign readable from both sides (the text on its back face too).</summary>
        public bool? TwoSided { get; set; }

        /// <summary>CollectionService tags the Game Kit reacts to (Checkpoint, KillBrick, Coin, …).</summary>
        public List<string> Tags { get; set; } = new();
        /// <summary>Parameters the Game Kit reads (written as child Value objects: Stage=3, Price=100…).</summary>
        public Dictionary<string, object> Config { get; set; } = new();

        /// <summary>Bottom-centre anchoring for assets, centre for everything else.</summary>
        [JsonIgnore]
        public bool AnchorsAtBottom => Kind == SceneKinds.Asset;

        [JsonIgnore]
        public RbxVector3 Pos => new(At(Position, 0), At(Position, 1), At(Position, 2));

        [JsonIgnore]
        public RbxVector3? SizeVector => Size is { Length: 3 } s ? new RbxVector3(s[0], s[1], s[2]) : null;

        private static float At(float[]? a, int i) => a != null && a.Length > i ? a[i] : 0;

        public RbxCFrame RotationFrame(RbxVector3 position)
        {
            if (Rotation is { Length: 3 } r)
                return RbxCFrame.FromEulerXYZ(position, r[0] * MathF.PI / 180f, r[1] * MathF.PI / 180f, r[2] * MathF.PI / 180f);
            return RbxCFrame.FromYaw(position, Yaw * MathF.PI / 180f);
        }
    }

    /// <summary>game.json — the game-wide settings the compiler and Game Kit read.</summary>
    public sealed class GameManifest
    {
        public string Title { get; set; } = "";
        /// <summary>Game Kit template: obby, tycoon, simulator or sandbox (a game that runs its own core loop).</summary>
        public string Template { get; set; } = "sandbox";
        public float WalkSpeed { get; set; } = 16;
        public float JumpPower { get; set; } = 50;
        public string CurrencyName { get; set; } = "Coins";
        /// <summary>The main currency's icon in the HUD (an emoji).</summary>
        public string CurrencyIcon { get; set; } = "🪙";
        public int StartingCurrency { get; set; }
        /// <summary>Extra currencies (gems, candy…) beside the main one.</summary>
        public List<CurrencySpec> Currencies { get; set; } = new();
        public bool DailyRewards { get; set; } = true;
        /// <summary>The day-one daily reward (each streak day pays this times the streak). Null: the template's default.</summary>
        public int? DailyReward { get; set; }
        public bool Leaderboard { get; set; } = true;
        public bool Feedback { get; set; } = true;
        public string MusicId { get; set; } = "";
        /// <summary>Up to three short tips shown to players on joining.</summary>
        public List<string> Tips { get; set; } = new();
        /// <summary>The HUD's colours: Accent, Accent2, Panel ("#RRGGBB").</summary>
        public Dictionary<string, string> Theme { get; set; } = new();
        /// <summary>Sound overrides by name (click, coin, purchase…): a Roblox audio id.</summary>
        public Dictionary<string, object> Sounds { get; set; } = new();
        /// <summary>The onboarding quest chain.</summary>
        public List<QuestSpec> Quests { get; set; } = new();
        public RebirthSpec? Rebirth { get; set; }
        /// <summary>Free-form values the template reads (e.g. obby stage count, tycoon income rate).</summary>
        public Dictionary<string, object> TemplateSettings { get; set; } = new();

        public static GameManifest Load(string path)
        {
            if (!File.Exists(path)) return new GameManifest();
            return JsonConvert.DeserializeObject<GameManifest>(File.ReadAllText(path), SceneDocument.Json) ?? new GameManifest();
        }

        public void Save(string path) => Core.OmniBloxStore.WriteAtomic(path, JsonConvert.SerializeObject(this, SceneDocument.Json));
    }

    public sealed class CurrencySpec
    {
        public string Name { get; set; } = "";
        public string Icon { get; set; } = "💎";
        public int Start { get; set; }
        public bool Leaderstat { get; set; }
    }

    public sealed class QuestSpec
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        /// <summary>The event that advances it: collect, sell, upgrade, stage, finish, unlock, zone, rebirth, coin, or the game's own.</summary>
        public string Event { get; set; } = "";
        public int Count { get; set; } = 1;
        public int Reward { get; set; }
        public string? RewardCurrency { get; set; }
        /// <summary>Name of a part or model (or a tag) the guide line points at.</summary>
        public string? Target { get; set; }
    }

    public sealed class RebirthSpec
    {
        public bool Enabled { get; set; } = true;
        public long Cost { get; set; } = 10_000;
        public double Growth { get; set; } = 2.5;
        public double Bonus { get; set; } = 0.5;
        public bool KeepUpgrades { get; set; }
    }
}
