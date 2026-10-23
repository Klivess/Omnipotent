namespace Omnipotent.Services.OmniBlox.Build.WorldGen
{
    /// <summary>
    /// The map a game asks for, in design terms rather than coordinates: the layout, the biome and art style, a
    /// spawn hub with its stands, the zones players move through (each with its biome, unlock price, resource and
    /// landmark) and how dense the scenery is. <see cref="WorldGenerator"/> turns it into terrain and placed objects;
    /// it is kept in scene.json so a map can be regenerated after any edit to it, by the builder or the studio.
    ///
    /// Agents were bad at composing maps object by object (a 140-stud strip of grass with a few bushes, signs
    /// floating in the air, buildings facing away); they are good at deciding what a map should contain. The
    /// spec is that decision, and the generator does the composing.
    /// </summary>
    public sealed class WorldSpec
    {
        public int Seed { get; set; } = 1;
        /// <summary>hub (zones round a central spawn hub), course (an obby course), plots (a tycoon plaza).</summary>
        public string Layout { get; set; } = "hub";
        /// <summary>Art-style family: stylized, voxel or realistic. Empty: the family the library covers best for the biome.</summary>
        public string Style { get; set; } = "";
        /// <summary>The hub's and the surrounding land's biome.</summary>
        public string Biome { get; set; } = "meadow";
        /// <summary>A lighting preset; empty: the biome's own.</summary>
        public string Lighting { get; set; } = "";
        /// <summary>Scenery density multiplier (0.5 sparse … 1.6 lush).</summary>
        public float Density { get; set; } = 1;
        /// <summary>none, lake or auto (a lake where there is room).</summary>
        public string Water { get; set; } = "auto";
        public HubSpec Hub { get; set; } = new();
        public List<ZoneSpec> Zones { get; set; } = new();
        /// <summary>Models to use for a role everywhere (role → asset ids), e.g. {"tree": [9281027181, 4553409785]}.</summary>
        public Dictionary<string, List<long>> Palette { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>The obby course (Layout course).</summary>
        public CourseSpec? Course { get; set; }
        /// <summary>The tycoon plots (Layout plots).</summary>
        public PlotsSpec? Plots { get; set; }
    }

    public sealed class HubSpec
    {
        public string Name { get; set; } = "Spawn";
        /// <summary>A model for the middle of the hub (a fountain, a statue, a giant tree); null: the generator picks one.</summary>
        public long? Centerpiece { get; set; }
        /// <summary>Hub radius in studs (0: automatic, about 52).</summary>
        public float Radius { get; set; }
        /// <summary>Plaza ground material (terrain): Cobblestone, Pavement, Sand, WoodPlanks, Brick…; empty: by biome.</summary>
        public string Plaza { get; set; } = "";
        public bool Shop { get; set; } = true;
        public bool Rebirth { get; set; } = true;
        public bool Leaderboard { get; set; } = true;
    }

    public sealed class ZoneSpec
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>meadow, forest, autumn, snow, desert, beach, jungle, volcanic, candy, spooky, crystal.</summary>
        public string Biome { get; set; } = "meadow";
        /// <summary>A few words of look ("misty pine forest").</summary>
        public string Theme { get; set; } = "";
        /// <summary>The order players reach it in (1 = first; the first zone is open from the start).</summary>
        public int Order { get; set; }
        /// <summary>Unlock price (0 = open).</summary>
        public long Price { get; set; }
        /// <summary>An extra currency the price is paid in; null: the main currency.</summary>
        public string? Currency { get; set; }
        /// <summary>What players harvest here (simulators).</summary>
        public ResourceSpec? Resource { get; set; }
        /// <summary>A model for the zone's landmark; null: the generator picks one (the tallest fitting model).</summary>
        public long? Landmark { get; set; }
        /// <summary>Models for a role in this zone only.</summary>
        public Dictionary<string, List<long>> Palette { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Radius in studs (0: automatic, 62-80 by order).</summary>
        public float Radius { get; set; }
    }

    public sealed class ResourceSpec
    {
        /// <summary>The kind of thing (tree, rock, crystal, mushroom, pumpkin, cactus, candy…): it picks the models and the player's verb.</summary>
        public string Kind { get; set; } = "tree";
        /// <summary>Models for the nodes; empty: the generator picks fitting ones.</summary>
        public List<long> Assets { get; set; } = new();
        public int Count { get; set; } = 12;
        /// <summary>Hits (tool power) a node takes to break.</summary>
        public int Hp { get; set; } = 6;
        /// <summary>Bag units a broken node gives (sold for that many coins, times multipliers).</summary>
        public int Value { get; set; } = 3;
        /// <summary>Seconds before a broken node grows back.</summary>
        public float Respawn { get; set; } = 8;
        /// <summary>Model scale (0: automatic, growing with the zone's order).</summary>
        public float Scale { get; set; }
    }

    public sealed class CourseSpec
    {
        public int Stages { get; set; } = 30;
        /// <summary>easy, normal or hard.</summary>
        public string Difficulty { get; set; } = "normal";
        /// <summary>Section themes in order (each covers an equal run of stages): e.g. ["meadow", "candy", "lava", "sky"].</summary>
        public List<string> Themes { get; set; } = new();
        /// <summary>Under the course: lava, water or void.</summary>
        public string Floor { get; set; } = "lava";
    }

    public sealed class PlotsSpec
    {
        public int Count { get; set; } = 4;
        /// <summary>What the tycoon makes (bakery, ice cream, lumber, mine…): names the items.</summary>
        public string Theme { get; set; } = "factory";
        /// <summary>Start price of the first upgrade; later ones grow from it.</summary>
        public long FirstPrice { get; set; } = 25;
    }
}
