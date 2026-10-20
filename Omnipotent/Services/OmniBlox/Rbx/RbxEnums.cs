namespace Omnipotent.Services.OmniBlox.Rbx
{
    /// <summary>Enum.Material token values as Roblox serializes them.</summary>
    public static class RbxMaterials
    {
        private static readonly Dictionary<uint, string> Names = new()
        {
            [256] = "Plastic", [272] = "SmoothPlastic", [288] = "Neon", [512] = "Wood", [528] = "WoodPlanks",
            [784] = "Marble", [788] = "Basalt", [800] = "Slate", [804] = "CrackedLava", [816] = "Concrete",
            [820] = "Limestone", [832] = "Granite", [836] = "Pavement", [848] = "Brick", [864] = "Pebble",
            [880] = "Cobblestone", [896] = "Rock", [912] = "Sandstone", [1040] = "CorrodedMetal",
            [1056] = "DiamondPlate", [1072] = "Foil", [1088] = "Metal", [1280] = "Grass", [1284] = "LeafyGrass",
            [1296] = "Sand", [1312] = "Fabric", [1328] = "Snow", [1344] = "Mud", [1360] = "Ground",
            [1376] = "Asphalt", [1392] = "Salt", [1536] = "Ice", [1552] = "Glacier", [1568] = "Glass",
            [1584] = "ForceField", [1792] = "Air", [2048] = "Water",
        };

        private static readonly Dictionary<string, uint> Tokens =
            Names.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

        public const uint Plastic = 256;
        public const uint SmoothPlastic = 272;
        public const uint Neon = 288;
        public const uint Wood = 512;
        public const uint Grass = 1280;
        public const uint Sand = 1296;
        public const uint Concrete = 816;
        public const uint Slate = 800;
        public const uint Glass = 1568;
        public const uint Metal = 1088;
        public const uint ForceField = 1584;

        public static string Name(uint token) => Names.TryGetValue(token, out var n) ? n : $"Material{token}";

        public static uint? Parse(string? name) =>
            !string.IsNullOrWhiteSpace(name) && Tokens.TryGetValue(name.Trim(), out var t) ? t : null;

        public static IReadOnlyCollection<string> AllNames => Names.Values;
    }

    /// <summary>Enum.PartType ("shape" on Part).</summary>
    public static class RbxPartShapes
    {
        public const uint Ball = 0, Block = 1, Cylinder = 2, Wedge = 3, CornerWedge = 4;

        public static string Name(uint token) => token switch
        {
            0 => "Ball",
            1 => "Block",
            2 => "Cylinder",
            3 => "Wedge",
            4 => "CornerWedge",
            _ => "Block",
        };
    }

    /// <summary>Enum.SurfaceType for TopSurface/BottomSurface etc.</summary>
    public static class RbxSurfaces
    {
        public const uint Smooth = 0;
    }

    /// <summary>The legacy BrickColor palette (older models store BrickColor instead of Color3uint8).</summary>
    public static class RbxBrickColors
    {
        private static readonly Dictionary<int, (byte R, byte G, byte B)> Palette = new()
        {
            [1] = (242, 243, 243), [2] = (161, 165, 162), [3] = (249, 233, 153), [5] = (215, 197, 154),
            [6] = (194, 218, 184), [9] = (232, 186, 200), [11] = (128, 187, 219), [12] = (203, 132, 66),
            [18] = (204, 142, 105), [21] = (196, 40, 28), [22] = (196, 112, 160), [23] = (13, 105, 172),
            [24] = (245, 205, 48), [25] = (98, 71, 50), [26] = (27, 42, 53), [27] = (109, 110, 108),
            [28] = (40, 127, 71), [29] = (161, 196, 140), [37] = (75, 151, 75), [38] = (160, 95, 53),
            [45] = (180, 210, 228), [101] = (218, 134, 122), [102] = (110, 153, 202), [103] = (199, 193, 183),
            [104] = (107, 50, 124), [105] = (226, 155, 64), [106] = (218, 133, 65), [107] = (0, 143, 156),
            [119] = (164, 189, 71), [125] = (234, 184, 146), [127] = (220, 188, 129), [128] = (174, 122, 89),
            [131] = (156, 163, 168), [135] = (116, 134, 157), [141] = (39, 70, 45), [151] = (120, 144, 130),
            [154] = (123, 46, 47), [192] = (105, 64, 40), [194] = (163, 162, 165), [199] = (99, 95, 98),
            [208] = (229, 228, 223), [217] = (124, 92, 70), [226] = (253, 234, 141), [301] = (80, 109, 84),
            [302] = (91, 93, 105), [304] = (44, 101, 29), [313] = (31, 128, 29), [315] = (9, 137, 207),
            [327] = (151, 0, 0), [333] = (239, 184, 56), [352] = (199, 172, 120), [361] = (86, 66, 54),
            [1001] = (248, 248, 248), [1002] = (205, 205, 205), [1003] = (17, 17, 17), [1004] = (255, 0, 0),
            [1005] = (255, 176, 0), [1009] = (255, 255, 0), [1010] = (0, 0, 255), [1011] = (0, 32, 96),
            [1012] = (33, 84, 185), [1013] = (4, 175, 236), [1015] = (170, 0, 170), [1016] = (255, 102, 204),
            [1018] = (18, 238, 212), [1019] = (0, 255, 255), [1020] = (0, 255, 0), [1021] = (58, 125, 21),
            [1031] = (98, 37, 209), [1032] = (255, 0, 191),
        };

        public static RbxColor3uint8 ToColor(int brickColor) =>
            Palette.TryGetValue(brickColor, out var c) ? new RbxColor3uint8(c.R, c.G, c.B) : new RbxColor3uint8(163, 162, 165);
    }

    /// <summary>Plain-language colour names, so a label can say "dark green" rather than #287F47.</summary>
    public static class RbxColorNames
    {
        private static readonly (string Name, byte R, byte G, byte B)[] Named =
        {
            ("white", 245, 245, 245), ("light grey", 200, 200, 200), ("grey", 140, 140, 140), ("dark grey", 80, 80, 80),
            ("black", 20, 20, 20), ("red", 200, 30, 30), ("dark red", 120, 20, 20), ("pink", 240, 150, 200),
            ("orange", 235, 130, 40), ("brown", 120, 80, 45), ("dark brown", 75, 50, 30), ("tan", 210, 180, 140),
            ("beige", 225, 210, 170), ("yellow", 245, 215, 40), ("gold", 220, 170, 60), ("olive", 120, 125, 50),
            ("lime green", 120, 220, 60), ("green", 60, 150, 60), ("dark green", 30, 90, 40), ("teal", 20, 140, 140),
            ("cyan", 40, 200, 230), ("light blue", 150, 200, 235), ("blue", 40, 90, 200), ("navy", 20, 35, 90),
            ("purple", 120, 60, 170), ("violet", 170, 120, 220), ("magenta", 200, 40, 170),
        };

        public static string Describe(RbxColor3uint8 c)
        {
            string best = "grey";
            double bestDistance = double.MaxValue;
            foreach (var (name, r, g, b) in Named)
            {
                // Redmean colour distance: cheap and much closer to perception than plain RGB.
                double rm = (c.R + r) / 2.0;
                double dr = c.R - r, dg = c.G - g, db = c.B - b;
                double d = (2 + rm / 256) * dr * dr + 4 * dg * dg + (2 + (255 - rm) / 256) * db * db;
                if (d < bestDistance) { bestDistance = d; best = name; }
            }
            return best;
        }
    }
}
