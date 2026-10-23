using Omnipotent.Services.OmniBlox.Assets;
using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Build.WorldGen;
using Omnipotent.Services.OmniBlox.Rbx;
using Omnipotent.Services.OmniBlox.Roblox;

namespace Omnipotent.Tests.OmniBlox
{
    /// <summary>
    /// A small stylized model library built from parts — trees, pines, bushes, flowers, rocks, a boulder, a mushroom,
    /// a stump, a fence, a lamp, a bench, a fountain, a crystal — analysed and labelled the way the harvester leaves
    /// them, so the world generator has every role it dresses a map with and the tests need no live export.
    /// </summary>
    internal static class TestModels
    {
        public const long Oak = 910001, Pine = 910002, Bush = 910003, Flowers = 910004, Rock = 910005, Boulder = 910006,
            Mushroom = 910007, Stump = 910008, Fence = 910009, Lamp = 910010, Bench = 910011, Fountain = 910012, Crystal = 910013,
            Maple = 910014, SnowyPine = 910015;

        private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>A library at <paramref name="dir"/> holding every test model, measured and labelled.</summary>
        public static AssetLibrary Library(string dir)
        {
            var library = new AssetLibrary(dir);
            library.Open();
            Seed(library);
            return library;
        }

        /// <summary>Adds every test model to a library.</summary>
        public static void Seed(AssetLibrary library)
        {
            var green = new RbxColor3uint8(72, 160, 70);
            var bark = new RbxColor3uint8(112, 78, 50);
            var stone = new RbxColor3uint8(128, 126, 122);

            Add(library, Oak, "Low Poly Oak Tree", "nature", "tree", "low-poly round oak tree", new[] { "forest", "meadow", "park" }, m =>
            {
                Part(m, "Trunk", new(1.6f, 8, 1.6f), new(0, 4, 0), bark, RbxMaterials.Wood);
                Part(m, "Crown", new(9, 8, 9), new(0, 11, 0), green, RbxMaterials.Grass, RbxPartShapes.Ball);
                Part(m, "Crown2", new(6, 5, 6), new(1.5f, 14, 1), new(90, 175, 80), RbxMaterials.Grass, RbxPartShapes.Ball);
            });
            Add(library, Pine, "Low Poly Pine Tree", "nature", "tree", "low-poly pine tree", new[] { "forest", "mountain", "camp" }, m =>
            {
                Part(m, "Trunk", new(1.4f, 5, 1.4f), new(0, 2.5f, 0), bark, RbxMaterials.Wood);
                Part(m, "Tier1", new(8, 4, 8), new(0, 6, 0), new(40, 120, 60), RbxMaterials.Grass);
                Part(m, "Tier2", new(6, 4, 6), new(0, 9.5f, 0), new(46, 132, 66), RbxMaterials.Grass);
                Part(m, "Tier3", new(3.5f, 4, 3.5f), new(0, 13, 0), new(52, 144, 72), RbxMaterials.Grass);
            });
            Add(library, Maple, "Autumn Maple Tree", "nature", "tree", "orange autumn maple tree", new[] { "autumn", "fall", "harvest" }, m =>
            {
                Part(m, "Trunk", new(1.6f, 8, 1.6f), new(0, 4, 0), bark, RbxMaterials.Wood);
                Part(m, "Crown", new(10, 8, 10), new(0, 11, 0), new(232, 128, 40), RbxMaterials.Grass, RbxPartShapes.Ball);
            });
            Add(library, SnowyPine, "Snowy Pine Tree", "nature", "tree", "snow-covered pine tree", new[] { "snow", "winter", "christmas" }, m =>
            {
                Part(m, "Trunk", new(1.4f, 5, 1.4f), new(0, 2.5f, 0), bark, RbxMaterials.Wood);
                Part(m, "Tier1", new(8, 4, 8), new(0, 6, 0), new(225, 236, 245), RbxMaterials.SmoothPlastic);
                Part(m, "Tier2", new(5, 5, 5), new(0, 10, 0), new(235, 242, 250), RbxMaterials.SmoothPlastic);
            });
            Add(library, Bush, "Round Bush", "nature", "bush", "round green bush", new[] { "forest", "meadow", "park", "garden" }, m =>
                Part(m, "Leaves", new(5, 3.6f, 5), new(0, 1.8f, 0), new(60, 150, 64), RbxMaterials.Grass, RbxPartShapes.Ball));
            Add(library, Flowers, "Flower Patch", "nature", "flower", "patch of red and yellow flowers", new[] { "meadow", "garden", "spring" }, m =>
            {
                Part(m, "Leaves", new(3.4f, 0.6f, 3.4f), new(0, 0.3f, 0), new(70, 160, 60), RbxMaterials.Grass);
                Part(m, "Red", new(0.9f, 0.9f, 0.9f), new(0.8f, 1, 0.6f), new(230, 60, 70), RbxMaterials.SmoothPlastic, RbxPartShapes.Ball);
                Part(m, "Yellow", new(0.9f, 0.9f, 0.9f), new(-0.7f, 1, -0.5f), new(250, 210, 60), RbxMaterials.SmoothPlastic, RbxPartShapes.Ball);
            });
            Add(library, Rock, "Mossy Rock", "nature", "rock", "mossy grey rock", new[] { "forest", "mountain", "meadow" }, m =>
            {
                Part(m, "Stone", new(4.5f, 2.6f, 3.6f), new(0, 1.3f, 0), stone, RbxMaterials.Slate);
                Part(m, "Moss", new(3, 0.4f, 2.4f), new(0, 2.7f, 0), new(84, 140, 70), RbxMaterials.Grass);
            });
            Add(library, Boulder, "Big Boulder", "nature", "boulder", "large grey boulder", new[] { "mountain", "forest", "desert" }, m =>
            {
                Part(m, "Big", new(9, 7, 8), new(0, 3.5f, 0), stone, RbxMaterials.Slate, RbxPartShapes.Ball);
                Part(m, "Side", new(4, 3, 4), new(4.5f, 1.5f, 2), new(118, 116, 112), RbxMaterials.Slate, RbxPartShapes.Ball);
            });
            Add(library, Mushroom, "Red Mushroom", "nature", "mushroom", "red spotted mushroom", new[] { "forest", "fantasy" }, m =>
            {
                Part(m, "Stem", new(1, 2.2f, 1), new(0, 1.1f, 0), new(240, 232, 214), RbxMaterials.SmoothPlastic);
                Part(m, "Cap", new(3.2f, 1.6f, 3.2f), new(0, 2.6f, 0), new(220, 50, 50), RbxMaterials.SmoothPlastic, RbxPartShapes.Ball);
            });
            Add(library, Stump, "Tree Stump", "nature", "stump", "cut tree stump", new[] { "forest", "camp", "lumber" }, m =>
                Part(m, "Stump", new(2.6f, 1.8f, 2.6f), new(0, 0.9f, 0), bark, RbxMaterials.Wood));
            Add(library, Fence, "Wooden Fence", "structure", "fence", "short wooden fence section", new[] { "farm", "meadow", "village" }, m =>
            {
                Part(m, "PostL", new(0.5f, 3.4f, 0.5f), new(-3.8f, 1.7f, 0), bark, RbxMaterials.Wood);
                Part(m, "PostR", new(0.5f, 3.4f, 0.5f), new(3.8f, 1.7f, 0), bark, RbxMaterials.Wood);
                Part(m, "Rail1", new(8, 0.4f, 0.3f), new(0, 1.2f, 0), new(150, 108, 66), RbxMaterials.Wood);
                Part(m, "Rail2", new(8, 0.4f, 0.3f), new(0, 2.5f, 0), new(150, 108, 66), RbxMaterials.Wood);
            });
            Add(library, Lamp, "Lantern Post", "light", "lamp", "wooden lantern post", new[] { "village", "park", "camp" }, m =>
            {
                Part(m, "Pole", new(0.6f, 9, 0.6f), new(0, 4.5f, 0), new(60, 48, 40), RbxMaterials.Wood);
                Part(m, "Lantern", new(1.4f, 1.6f, 1.4f), new(0, 9.6f, 0), new(255, 214, 140), RbxMaterials.Neon);
            });
            Add(library, Bench, "Park Bench", "furniture", "bench", "wooden park bench", new[] { "park", "village", "camp" }, m =>
            {
                Part(m, "Seat", new(6, 0.5f, 2), new(0, 2, 0), new(150, 100, 60), RbxMaterials.Wood);
                Part(m, "Back", new(6, 2, 0.4f), new(0, 3.2f, 0.9f), new(150, 100, 60), RbxMaterials.Wood);
                Part(m, "LegL", new(0.4f, 2, 1.8f), new(-2.6f, 1, 0), new(50, 50, 54), RbxMaterials.Metal);
                Part(m, "LegR", new(0.4f, 2, 1.8f), new(2.6f, 1, 0), new(50, 50, 54), RbxMaterials.Metal);
            });
            Add(library, Fountain, "Stone Fountain", "decoration", "fountain", "round stone fountain", new[] { "village", "park", "garden" }, m =>
            {
                Part(m, "Basin", new(10, 1.6f, 10), new(0, 0.8f, 0), new(170, 166, 158), RbxMaterials.Concrete, RbxPartShapes.Cylinder, sideways: true);
                Part(m, "Water", new(8.6f, 0.3f, 8.6f), new(0, 1.5f, 0), new(90, 170, 230), RbxMaterials.Glass, RbxPartShapes.Cylinder, sideways: true);
                Part(m, "Pillar", new(1.6f, 4, 1.6f), new(0, 3.6f, 0), new(170, 166, 158), RbxMaterials.Concrete);
                Part(m, "Bowl", new(4, 0.8f, 4), new(0, 5.8f, 0), new(170, 166, 158), RbxMaterials.Concrete, RbxPartShapes.Cylinder, sideways: true);
            });
            Add(library, Crystal, "Crystal Cluster", "nature", "crystal", "glowing blue crystal cluster", new[] { "crystal", "cave", "magic" }, m =>
            {
                Part(m, "Big", new(1.6f, 5, 1.6f), new(0, 2.5f, 0), new(90, 200, 255), RbxMaterials.Neon);
                Part(m, "Left", new(1.1f, 3.2f, 1.1f), new(-1.2f, 1.6f, 0.4f), new(120, 210, 255), RbxMaterials.Neon);
                Part(m, "Right", new(1, 2.6f, 1), new(1.1f, 1.3f, -0.5f), new(120, 210, 255), RbxMaterials.Neon);
            });
        }

        public static AssetCatalog Catalog(AssetLibrary library) => AssetCatalog.FromRecords(library.Recent(AssetStatus.Labeled, 200));

        public static AssetPlacement? Info(AssetLibrary library, long id)
        {
            var rec = library.Get(id);
            if (rec?.Measurements is not { } m || !rec.Placeable) return null;
            return new AssetPlacement(new RbxVector3(m.Pivot[0], m.Pivot[1], m.Pivot[2]), rec.Label?.FrontYaw ?? 0, new RbxVector3(m.SizeX, m.SizeY, m.SizeZ), rec.Name);
        }

        public static CompileResult Compile(AssetLibrary library, GameWorkspace ws, string gameId) =>
            PlaceCompiler.Compile(new CompileInput { GameId = gameId, Workspace = ws, LoadAsset = library.LoadModel, AssetInfo = id => Info(library, id) });

        private static void Add(AssetLibrary library, long id, string name, string category, string sub, string what, string[] themes, Action<RbxInstance> build)
        {
            var doc = new RbxDocument();
            var model = doc.AddRoot(new RbxInstance("Model", name));
            build(model);
            library.UpsertDiscovered(new[]
            {
                new CreatorStoreAsset { AssetId = id, Name = name, Description = name + " — free model", AssetTypeId = 10, CategoryPath = "3d__models", CreatorName = "TestKit", CreatorVerified = true, UpVotes = 900, DownVotes = 20, UpVotePercent = 98, Triangles = 300 },
            }, sub, 0, Now);
            var analysis = AssetAnalyzer.Analyze(doc, id, name);
            library.SaveAnalysis(id, RbxXmlWriter.WriteBytes(doc), analysis, new List<(string, byte[])>(), 300, Now);
            library.SaveLabel(id, new AssetLabel
            {
                What = what, Category = category, Subcategory = sub, Style = "low-poly", Description = $"A {what}.", Quality = 7,
                Themes = themes.ToList(), GoodFor = new() { "decoration" }, PairsWith = new(), Model = "test",
            }, Now, AssetLabeler.ScaleVersion);
        }

        private static void Part(RbxInstance parent, string name, RbxVector3 size, RbxVector3 position, RbxColor3uint8 color, uint material, uint shape = RbxPartShapes.Block, bool sideways = false)
        {
            var p = parent.AddChild(new RbxInstance("Part", name));
            // A Roblox cylinder lies along its X axis: an upright disc is the part turned a quarter about Z.
            var cframe = sideways ? RbxCFrame.FromEulerXYZ(position, 0, 0, MathF.PI / 2) : new RbxCFrame(position);
            p.Set("size", RbxValue.Vec3(sideways ? new RbxVector3(size.Y, size.X, size.Z) : size));
            p.Set("CFrame", RbxValue.Frame(cframe));
            p.Set("Color3uint8", RbxValue.Color8(color));
            p.Set("Material", RbxValue.Token(material));
            if (shape != RbxPartShapes.Block) p.Set("shape", RbxValue.Token(shape));
            p.Set("Anchored", RbxValue.Bool(true));
        }
    }
}
