using Omnipotent.Services.OmniBlox.Assets;

namespace Omnipotent.Services.OmniBlox.Build.WorldGen
{
    /// <summary>
    /// What a library model is good for when a map is generated: its role (a canopy tree, a bush, a lamp post…),
    /// the art-style family it belongs to, the biomes it suits, and its measured size. The world generator
    /// dresses a map by role, so it needs every model sorted this way; the labeller's free-text labels are the
    /// raw material.
    /// </summary>
    public sealed record CatalogEntry(long AssetId, string Name, string Role, string Style, IReadOnlyList<string> Biomes, float SizeX, float SizeY, float SizeZ,
        int Parts, double Quality, float FrontYaw, bool Untextured, int TimesUsed)
    {
        public float Footprint => MathF.Max(SizeX, SizeZ);
        public float Height => SizeY;
    }

    /// <summary>The roles the generator dresses a map with.</summary>
    public static class Roles
    {
        public const string Tree = "tree";
        public const string Conifer = "conifer";
        public const string Palm = "palm";
        public const string DeadTree = "deadtree";
        public const string Bush = "bush";
        public const string Flower = "flower";
        public const string Grass = "grass";
        public const string Rock = "rock";
        public const string Boulder = "boulder";
        public const string Mushroom = "mushroom";
        public const string Stump = "stump";
        public const string Log = "log";
        public const string Crystal = "crystal";
        public const string Cactus = "cactus";
        public const string Fence = "fence";
        public const string Lamp = "lamp";
        public const string Bench = "bench";
        public const string Sign = "sign";
        public const string Building = "building";
        public const string Stall = "stall";
        public const string Landmark = "landmark";
        public const string Fountain = "fountain";
        public const string Statue = "statue";
        public const string Campfire = "campfire";
        public const string Crate = "crate";
        public const string Barrel = "barrel";
        public const string Cart = "cart";
        public const string Pumpkin = "pumpkin";
        public const string Candy = "candy";
        public const string Well = "well";

        /// <summary>Roles of living scenery, scattered in numbers (part counts matter most here).</summary>
        public static readonly HashSet<string> Scatter = new() { Tree, Conifer, Palm, DeadTree, Bush, Flower, Grass, Rock, Mushroom, Stump, Log, Cactus, Crystal, Pumpkin, Candy };

        /// <summary>Roles that can be a resource node players harvest (simulators).</summary>
        /// <summary>Plants: shapes whose boxes say little about where they are (a canopy over a neighbour's trunk is a forest, not a fault).</summary>
        public static readonly HashSet<string> Plants = new() { Tree, Conifer, Palm, DeadTree, Bush, Flower, Grass, Mushroom, Cactus };

        public static readonly HashSet<string> Harvestable = new() { Tree, Conifer, Palm, DeadTree, Rock, Boulder, Crystal, Mushroom, Cactus, Pumpkin, Candy, Bush, Flower, Log };

        /// <summary>Studs: (min height, max height, max footprint) a model of the role may have before scaling.</summary>
        public static (float MinH, float MaxH, float MaxFoot) Bounds(string role) => role switch
        {
            Tree or Conifer or Palm or DeadTree => (8, 90, 60),
            Bush => (1.2f, 14, 24),
            Flower => (0.4f, 8, 16),
            Grass => (0.3f, 6, 16),
            Rock => (0.8f, 14, 24),
            Boulder => (6, 50, 70),
            Mushroom => (1, 40, 30),
            Stump => (0.8f, 9, 14),
            Log => (0.8f, 10, 30),
            Crystal => (1.5f, 60, 30),
            Cactus => (2, 40, 20),
            Fence => (1.5f, 12, 48),
            Lamp => (5, 30, 8),
            Bench => (1.5f, 8, 16),
            Sign => (2, 22, 20),
            Building => (7, 70, 90),
            Stall => (5, 24, 34),
            Landmark => (12, 160, 90),
            Fountain => (2, 40, 50),
            Statue => (4, 80, 40),
            Campfire => (0.5f, 9, 14),
            Crate or Barrel => (0.8f, 9, 10),
            Cart => (2, 14, 18),
            Pumpkin => (0.8f, 16, 16),
            Candy => (1, 40, 24),
            Well => (3, 20, 18),
            _ => (0.5f, 200, 200),
        };

        /// <summary>The height (studs) the generator scales a role towards when it has a choice.</summary>
        public static float TypicalHeight(string role) => role switch
        {
            Tree => 26, Conifer => 30, Palm => 26, DeadTree => 20,
            Bush => 5.5f, Flower => 2.5f, Grass => 2f, Rock => 4, Boulder => 14, Mushroom => 5, Stump => 2.5f, Log => 3,
            Crystal => 9, Cactus => 12, Fence => 5, Lamp => 12, Bench => 3.5f, Sign => 9, Building => 20, Stall => 11,
            Landmark => 40, Fountain => 10, Statue => 18, Campfire => 2.5f, Crate => 3.5f, Barrel => 4, Cart => 6,
            Pumpkin => 3, Candy => 8, Well => 9,
            _ => 8,
        };
    }

    /// <summary>
    /// The library sorted by role, style and biome. Built from labelled, placeable models; models whose look
    /// depended on things the publishing API strips (PBR SurfaceAppearance) are marked untextured — they would
    /// show up as plain grey in the game, so the generator avoids them.
    /// </summary>
    public sealed class AssetCatalog
    {
        public IReadOnlyList<CatalogEntry> Entries { get; }
        private readonly Dictionary<long, CatalogEntry> byId;

        /// <summary>The most parts a model scattered in numbers (trees, rocks, flowers…) may have.</summary>
        public const int MaxScatterParts = 60;
        /// <summary>The most parts a one-off model (a landmark, a building, a centrepiece) may have.</summary>
        public const int MaxHeroParts = 500;

        public AssetCatalog(IEnumerable<CatalogEntry> entries)
        {
            Entries = entries.ToList();
            byId = Entries.GroupBy(e => e.AssetId).ToDictionary(g => g.Key, g => g.First());
        }

        public static readonly AssetCatalog Empty = new(Array.Empty<CatalogEntry>());

        public CatalogEntry? Get(long assetId) => byId.TryGetValue(assetId, out var e) ? e : null;

        public static AssetCatalog FromRecords(IEnumerable<AssetRecord> records)
        {
            var list = new List<CatalogEntry>();
            foreach (var r in records)
                if (Classify(r) is { } entry) list.Add(entry);
            return new AssetCatalog(list);
        }

        /// <summary>A model as a catalog entry, or null when it is no use for dressing a map (vehicles, characters, tools…).</summary>
        public static CatalogEntry? Classify(AssetRecord r)
        {
            if (!r.Placeable || r.Label == null || r.Measurements is not { } m) return null;
            if (m.HasHumanoid || m.HasTool) return null;
            string role = RoleOf(r.Label, r.Name, m.SizeX, m.SizeY, m.SizeZ) ?? "";
            if (role.Length == 0) return null;
            var (minH, maxH, maxFoot) = Roles.Bounds(role);
            float foot = MathF.Max(m.SizeX, m.SizeZ);
            // A model far outside its role's sizes is mislabelled or a whole scene; even scaled it would look wrong.
            if (m.SizeY < minH * 0.35f || m.SizeY > maxH * 3f || foot > maxFoot * 3f) return null;
            string style = StyleFamily(r.Label.Style);
            bool untextured = IsUntextured(r);
            return new CatalogEntry(r.AssetId, r.Name, role, style, BiomesOf(r), m.SizeX, m.SizeY, m.SizeZ, m.PartCount, r.Label.Quality, r.Label.FrontYaw, untextured, r.TimesUsed);
        }

        private static bool Has(string text, params string[] words) => words.Any(w => text.Contains(w, StringComparison.Ordinal));

        /// <summary>The role a labelled model plays in a map.</summary>
        public static string? RoleOf(AssetLabel label, string name, float sx, float sy, float sz)
        {
            string cat = (label.Category ?? "").Trim().ToLowerInvariant();
            string sub = (label.Subcategory ?? "").Trim().ToLowerInvariant();
            string text = $" {sub} {(label.What ?? "").ToLowerInvariant()} {name.ToLowerInvariant()} ";
            float foot = MathF.Max(sx, sz);
            float tall = sy;

            if (cat is "vehicle" or "character" or "creature" or "tool" or "weapon" or "effect" or "terrain" or "platform" or "obstacle") return null;
            if (Has(text, " gun", "weapon", "sword", "pistol", "rifle", "car ", " truck", "vehicle", "coin", "traffic", "parking", "bus stop", "baseplate", "skybox", "decal", "texture", "map ")) return null;

            if (Has(text, "palm")) return Roles.Palm;
            if (Has(text, "cactus", "cacti")) return Roles.Cactus;
            if (Has(text, "crystal", " gem", "geode", "ore ")) return foot > 30 || tall > 40 ? Roles.Landmark : Roles.Crystal;
            if (Has(text, "mushroom", "toadstool")) return tall > 18 ? Roles.Landmark : Roles.Mushroom;
            if (Has(text, "pumpkin", "jack-o", "jack o")) return tall > 18 ? Roles.Landmark : Roles.Pumpkin;
            if (Has(text, "fountain")) return Roles.Fountain;
            if (Has(text, "statue", "monument", "totem")) return Roles.Statue;
            if (Has(text, " well ")) return Roles.Well;

            switch (cat)
            {
                case "nature":
                    if (Has(text, "dead tree", "dead ", "bare tree", "leafless", "spooky tree")) return Roles.DeadTree;
                    if (Has(text, "stump")) return Roles.Stump;
                    if (Has(text, " log", "logs", "log pile", "wood pile", "firewood", "twig", "branch")) return Roles.Log;
                    if (Has(text, "tree", "oak", "birch", "maple", "willow", "sakura", "cherry blossom"))
                        return Has(text, "pine", "spruce", "fir ", "conifer", "christmas", "evergreen", "cedar", "redwood") ? Roles.Conifer : Roles.Tree;
                    if (Has(text, "bush", "shrub", "hedge", "fern", "foliage")) return Roles.Bush;
                    if (Has(text, "flower", "tulip", "rose", "daisy", "sunflower", "lily", "poppy")) return Roles.Flower;
                    if (Has(text, "grass", "weed", "reed", "clover", "plant", "vine", "crop", "corn", "wheat")) return tall > 8 ? Roles.Bush : Roles.Grass;
                    if (Has(text, "boulder", "cliff", "rock formation")) return Roles.Boulder;
                    if (Has(text, "rock", "stone", "pebble")) return foot > 14 || tall > 10 ? Roles.Boulder : Roles.Rock;
                    return null;
                case "prop":
                case "decoration":
                    if (Has(text, "campfire", "fire pit", "bonfire")) return Roles.Campfire;
                    if (Has(text, "crate", " box", "container", "chest")) return Roles.Crate;
                    if (Has(text, "barrel", "keg")) return Roles.Barrel;
                    if (Has(text, "cart", "wheelbarrow", "wagon")) return Roles.Cart;
                    if (Has(text, "stump")) return Roles.Stump;
                    if (Has(text, " log", "logs", "wood pile", "firewood", "log pile")) return Roles.Log;
                    if (Has(text, "rock", "stone")) return Roles.Rock;
                    if (Has(text, "lantern", "torch", "lamp")) return Roles.Lamp;
                    if (Has(text, "flower", "potted plant", "planter", "flower pot", "flower bed")) return Roles.Flower;
                    if (Has(text, "candy", "lollipop", "sweet", "gumdrop", "cupcake")) return Roles.Candy;
                    if (Has(text, "wooden sign", "signpost", "sign post")) return Roles.Sign;
                    if (Has(text, " arch")) return Roles.Landmark;
                    return null;
                case "food":
                    return Has(text, "candy", "lollipop", "sweet", "cupcake", "cake", "donut") ? Roles.Candy : null;
                case "light":
                    return Has(text, "wall", "ceiling", "pendant", "hanging", "sconce") ? null : Roles.Lamp;
                case "sign":
                    if (Has(text, "road", "bus", "parking", "stop", "traffic", "exit", "emergency", "warning", "for sale", "billboard", "logo", "sticker", "neon", "door"))
                        return null;
                    return Roles.Sign;
                case "furniture":
                    if (Has(text, "bench")) return Roles.Bench;
                    if (Has(text, "market", "stand", "stall", "counter", "kiosk")) return Roles.Stall;
                    return null;
                case "structure":
                    if (Has(text, "fence", "railing")) return Roles.Fence;
                    if (Has(text, "stall", "kiosk", "booth", "shop", "market")) return Roles.Stall;
                    if (Has(text, "tower", "lighthouse", "windmill", "treehouse", "tree house", "castle", "temple", "ruin", "obelisk")) return Roles.Landmark;
                    if (Has(text, "gate", "arch")) return Roles.Landmark;
                    if (Has(text, "barn", "shed", "hut", "house", "cabin")) return Roles.Building;
                    return null;
                case "building":
                    if (Has(text, "skyscraper", "apartment", "office", "factory", "warehouse", "station", "garage", "elevator", "wall", "stairs", "window", "roof", "door", "ladder", "ramp", "block", "fence"))
                        return null;
                    if (Has(text, "tower", "castle", "fort", "watchtower", "lighthouse", "windmill", "temple", "tree house", "treehouse")) return Roles.Landmark;
                    if (Has(text, "kiosk", "booth", "stall", "stand", "food stall", "tent")) return Roles.Stall;
                    return Roles.Building;
            }
            return null;
        }

        /// <summary>
        /// Art-style family: "stylized" (low-poly, cartoon and stylized models mix well — the look of most kids' games),
        /// "voxel" (blocky) or "realistic". A map keeps to one family.
        /// </summary>
        public static string StyleFamily(string? style)
        {
            string s = (style ?? "").ToLowerInvariant();
            if (s.Contains("voxel") || s.Contains("blocky") || s.Contains("minecraft") || s.Contains("brick")) return "voxel";
            bool low = s.Contains("low-poly") || s.Contains("low poly") || s.Contains("lowpoly") || s.Contains("cartoon") || s.Contains("stylized") || s.Contains("stylised") || s.Contains("toon");
            if ((s.Contains("realistic") || s.Contains("pbr") || s.Contains("photo")) && !low) return "realistic";
            return "stylized";
        }

        /// <summary>
        /// A model whose look came from SurfaceAppearance (PBR maps): the publishing API cannot carry it, so the cleaned
        /// copy is plain default-grey plastic. The labeller judged the thumbnail, which still shows the textures.
        /// </summary>
        public static bool IsUntextured(AssetRecord r)
        {
            if (r.Label?.Style is string st && st.Contains("pbr", StringComparison.OrdinalIgnoreCase)) return true;
            var m = r.Measurements;
            if (m == null) return false;
            bool mostlyDefaultGrey = m.Colors.Count > 0 && m.Colors[0].Hex.Equals("#A3A2A5", StringComparison.OrdinalIgnoreCase) && m.Colors[0].Share > 0.8;
            // Grey is the true colour of rocks, stone and metal: only a model that should be colourful is suspect.
            string what = ((r.Label?.What ?? "") + " " + (r.Label?.Subcategory ?? "") + " " + r.Name).ToLowerInvariant();
            bool greyByNature = new[] { "rock", "stone", "boulder", "metal", "steel", "iron", "concrete", "silver", "pebble", "cliff" }.Any(what.Contains);
            return mostlyDefaultGrey && m.MeshPartCount > 0 && m.TexturedShare < 0.05 && !greyByNature;
        }

        private static readonly (string Biome, string[] Words)[] BiomeWords =
        {
            ("meadow", new[] { "meadow", "grass", "flower", "garden", "park", "farm", "field", "spring", "village", "daisy" }),
            ("forest", new[] { "forest", "wood", "pine", "oak", "woodland", "moss", "camp", "lumber", "cabin", "nature", "mushroom" }),
            ("autumn", new[] { "autumn", "fall", "orange", "maple", "harvest", "pumpkin", "scarecrow", "hay" }),
            ("snow", new[] { "snow", "winter", "ice", "icy", "frozen", "christmas", "holiday", "frost", "arctic", "igloo" }),
            ("desert", new[] { "desert", "sand", "cactus", "dune", "egypt", "dry", "western", "canyon", "oasis", "mesa" }),
            ("beach", new[] { "beach", "tropical", "palm", "island", "coconut", "ocean", "shell", "summer", "surf", "pirate" }),
            ("jungle", new[] { "jungle", "tropical", "rainforest", "vine", "fern", "bamboo", "temple" }),
            ("volcanic", new[] { "lava", "volcano", "fire", "magma", "obsidian", "hell", "burnt", "ash", "ember" }),
            ("candy", new[] { "candy", "sweet", "lollipop", "dessert", "cake", "sugar", "pastel", "gummy", "cupcake", "donut" }),
            ("spooky", new[] { "spooky", "halloween", "haunted", "dead", "dark", "grave", "ghost", "swamp", "witch", "skull" }),
            ("crystal", new[] { "crystal", "gem", "magic", "space", "alien", "planet", "neon", "glow", "fantasy", "fairy" }),
        };

        public static IReadOnlyList<string> BiomesOf(AssetRecord r)
        {
            var l = r.Label!;
            string text = " " + string.Join(" ", new[] { l.What, l.Subcategory, r.Name, l.Description, string.Join(" ", l.Themes), string.Join(" ", l.GoodFor) }).ToLowerInvariant() + " ";
            var biomes = BiomeWords.Where(b => b.Words.Any(w => text.Contains(w, StringComparison.Ordinal))).Select(b => b.Biome).ToList();
            if (r.Measurements?.Colors.FirstOrDefault()?.Name is string c)
            {
                if (c is "orange" or "red" or "brown" && !biomes.Contains("autumn")) biomes.Add("autumn~");
                if (c is "white" or "light blue" && !biomes.Contains("snow")) biomes.Add("snow~");
                if (c is "pink" or "purple" && !biomes.Contains("candy")) biomes.Add("candy~");
            }
            return biomes;
        }

        /// <summary>How well a model suits a biome: 1 for an explicit match, a little for a colour hint, a little for neutral nature.</summary>
        public static double Affinity(CatalogEntry e, string biome)
        {
            biome = (biome ?? "").ToLowerInvariant();
            if (e.Biomes.Contains(biome)) return 1.0;
            if (e.Biomes.Contains(biome + "~")) return 0.45;
            // Plain nature suits the temperate biomes; clearly themed models clash with the other biomes.
            bool themed = e.Biomes.Any(b => b is "snow" or "desert" or "beach" or "volcanic" or "candy" or "spooky" or "jungle");
            bool temperate = biome is "meadow" or "forest" or "autumn" or "";
            if (temperate && !themed) return 0.55;
            if (!temperate && !themed && e.Role is Roles.Rock or Roles.Boulder or Roles.Log or Roles.Stump or Roles.Lamp or Roles.Fence or Roles.Bench or Roles.Sign or Roles.Crate or Roles.Barrel or Roles.Building or Roles.Stall)
                return 0.4;
            if (themed && e.Biomes.Any(b => BiomeCompatible(b, biome))) return 0.5;
            return themed ? -0.6 : 0.05;
        }

        private static bool BiomeCompatible(string a, string b) => (a, b) switch
        {
            ("forest", "autumn") or ("autumn", "forest") or ("forest", "snow") or ("meadow", "autumn") => true,
            ("beach", "jungle") or ("jungle", "beach") or ("spooky", "autumn") or ("crystal", "candy") => true,
            _ => false,
        };

        /// <summary>
        /// The best models for a role in a biome and style, best first: quality, how well they suit the biome, the
        /// style family, a low part count for things scattered in numbers, and a little for proven use.
        /// </summary>
        public List<CatalogEntry> Best(string role, string biome, string style, int count, ISet<long>? exclude = null)
        {
            var (minH, maxH, maxFoot) = Roles.Bounds(role);
            // Scenery is placed by the hundred: a model of hundreds of parts would eat a phone's whole budget.
            int maxParts = Roles.Scatter.Contains(role) ? MaxScatterParts : MaxHeroParts;
            return Entries
                .Where(e => e.Role == role && e.Parts <= maxParts && (exclude == null || !exclude.Contains(e.AssetId)))
                .Select(e => (e, score: Score(e, role, biome, style, minH, maxH, maxFoot)))
                .Where(x => x.score > -50)
                .OrderByDescending(x => x.score)
                .Select(x => x.e)
                .GroupBy(e => NormalizeName(e.Name))
                .Select(g => g.First())
                .Take(count)
                .ToList();
        }

        private static double Score(CatalogEntry e, string role, string biome, string style, float minH, float maxH, float maxFoot)
        {
            double s = e.Quality;
            s += Affinity(e, biome) * 3.0;
            if (style.Length > 0) s += e.Style == style ? 2.0 : (e.Style == "realistic" || style == "realistic" ? -6.0 : -2.5);
            if (e.Untextured) s -= 8;
            if (Roles.Scatter.Contains(role))
            {
                if (e.Parts > 150) s -= 4;
                else if (e.Parts > 60) s -= 2;
                else if (e.Parts > 25) s -= 0.7;
            }
            if (e.Height < minH || e.Height > maxH || e.Footprint > maxFoot) s -= 2.5; // usable only when scaled
            s += Math.Min(e.TimesUsed, 5) * 0.08;
            return s;
        }

        private static string NormalizeName(string name)
        {
            var chars = name.ToLowerInvariant().Where(char.IsLetter).ToArray();
            return new string(chars);
        }

        /// <summary>Which style family has the widest choice of scenery for a biome (what an unspecified map uses).</summary>
        public string DominantStyle(string biome)
        {
            var families = new[] { "stylized", "voxel", "realistic" };
            return families
                .Select(f => (f, n: new[] { Roles.Tree, Roles.Conifer, Roles.Bush, Roles.Rock, Roles.Flower }.Sum(role => Math.Min(4, Best(role, biome, f, 4).Count(e => e.Style == f && !e.Untextured)))))
                .OrderByDescending(x => x.n).ThenBy(x => Array.IndexOf(families, x.f))
                .First().f;
        }

        /// <summary>How many usable models each role has in a biome and style (the generator's and the researcher's feasibility view).</summary>
        public Dictionary<string, int> Coverage(string biome, string style)
        {
            var roles = new[] { Roles.Tree, Roles.Conifer, Roles.Palm, Roles.Bush, Roles.Flower, Roles.Rock, Roles.Boulder, Roles.Mushroom, Roles.Crystal, Roles.Cactus,
                Roles.Lamp, Roles.Fence, Roles.Bench, Roles.Building, Roles.Stall, Roles.Landmark, Roles.Fountain, Roles.Statue, Roles.Crate, Roles.Barrel, Roles.Campfire, Roles.Log, Roles.Stump };
            return roles.ToDictionary(r => r, r => Best(r, biome, style, 50).Count(e => !e.Untextured && Affinity(e, biome) > 0 && (style.Length == 0 || e.Style == style)));
        }
    }
}
