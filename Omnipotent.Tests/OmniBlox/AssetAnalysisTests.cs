using Omnipotent.Services.OmniBlox.Assets;
using Omnipotent.Services.OmniBlox.Rbx;
using Xunit.Abstractions;

namespace Omnipotent.Tests.OmniBlox
{
    public class AssetAnalysisTests
    {
        private readonly ITestOutputHelper output;
        public AssetAnalysisTests(ITestOutputHelper output) => this.output = output;

        internal static RbxDocument SampleTree()
        {
            var doc = new RbxDocument();
            var model = doc.AddRoot(new RbxInstance("Model", "Pine Tree"));
            var trunk = model.AddChild(new RbxInstance("Part", "Trunk"));
            trunk.Set("size", RbxValue.Vec3(new RbxVector3(2, 10, 2)));
            trunk.Set("CFrame", RbxValue.Frame(new RbxCFrame(new RbxVector3(100, 5, 50))));
            trunk.Set("Color3uint8", RbxValue.Color8(new RbxColor3uint8(110, 75, 45)));
            trunk.Set("Material", RbxValue.Token(RbxMaterials.Wood));
            trunk.Set("Anchored", RbxValue.Bool(false));
            var leaves = model.AddChild(new RbxInstance("Part", "Leaves"));
            leaves.Set("size", RbxValue.Vec3(new RbxVector3(8, 12, 8)));
            leaves.Set("CFrame", RbxValue.Frame(new RbxCFrame(new RbxVector3(100, 14, 50))));
            leaves.Set("Color3uint8", RbxValue.Color8(new RbxColor3uint8(35, 100, 45)));
            leaves.Set("Material", RbxValue.Token(RbxMaterials.Grass));
            leaves.Set("shape", RbxValue.Token(RbxPartShapes.Ball));
            var backdoor = model.AddChild(new RbxInstance("Script", "Spread"));
            backdoor.Set("Source", RbxValue.Source("local x = require(5821749324)\nx.load()"));
            var harmless = leaves.AddChild(new RbxInstance("Script", "Sway"));
            harmless.Set("Source", RbxValue.Source("while true do task.wait(1) end"));
            model.AddChild(new RbxInstance("Explosion", "Boom"));
            return doc;
        }

        [Fact]
        public void Analyzer_StripsScriptsAndFlagsBackdoors()
        {
            var a = AssetAnalyzer.Analyze(SampleTree(), 1, "Pine Tree");
            Assert.Equal(2, a.RemovedScripts.Count);
            var bad = Assert.Single(a.RemovedScripts, s => s.Suspicious);
            Assert.Contains(bad.Flags, f => f.Contains("backdoor"));
            Assert.Contains("Explosion", a.RemovedOther);
            Assert.DoesNotContain(a.Model.Descendants(), i => i.ClassName is "Script" or "Explosion");
            Assert.True(a.Placeable);
            // Everything placed comes out anchored, even decor that shipped unanchored.
            Assert.All(a.Model.Descendants().Where(i => i.ClassName == "Part"), p => Assert.True(p.GetBool("Anchored")));
        }

        [Fact]
        public void Analyzer_MeasuresBoundsPivotColoursAndMaterials()
        {
            var a = AssetAnalyzer.Analyze(SampleTree(), 1, "Pine Tree");
            var m = a.Measurements;
            Assert.Equal(8, m.SizeX);
            Assert.Equal(20, m.SizeY); // trunk bottom at 0, leaves top at 20
            Assert.Equal(8, m.SizeZ);
            Assert.Equal(new float[] { 100, 0, 50 }, m.Pivot);
            Assert.Equal("tall", m.Proportions);
            Assert.Equal("dark green", m.Colors[0].Name);
            Assert.Contains(m.Colors, c => c.Name == "brown");
            Assert.Equal("Grass", m.Materials[0].Material);
            Assert.Equal(2, m.PartCount);
        }

        [Fact]
        public void Analyzer_RejectsUnions()
        {
            var doc = SampleTree();
            var union = doc.Roots[0].AddChild(new RbxInstance("UnionOperation", "Rock"));
            union.Set("size", RbxValue.Vec3(new RbxVector3(3, 3, 3)));
            union.Set("CFrame", RbxValue.Frame(new RbxCFrame(new RbxVector3(100, 1.5f, 55))));
            var a = AssetAnalyzer.Analyze(doc, 1, "Pine Tree");
            Assert.False(a.Placeable);
            Assert.Contains(a.Problems, p => p.Contains("unions"));
        }

        [Fact]
        public void Renderer_ProducesFourPngViews()
        {
            var a = AssetAnalyzer.Analyze(SampleTree(), 1, "Pine Tree");
            var views = SchematicRenderer.RenderStandardViews(SchematicRenderer.PrimitivesOf(a.Model), 256);
            Assert.Equal(new[] { "front", "right", "top", "iso" }, views.Select(v => v.View));
            foreach (var (_, png) in views)
            {
                Assert.True(png.Length > 500);
                Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
            }
            Save("tree", views);
        }

        [Fact]
        public void Renderer_DrawsTheRobloxCharacterRig()
        {
            string? path = RbxFormatTests.FindInstalled("characterR15.rbxm");
            if (path == null) { output.WriteLine("characterR15.rbxm not installed; skipped."); return; }
            var a = AssetAnalyzer.Analyze(RbxFile.Read(File.ReadAllBytes(path)), 0, "Character");
            output.WriteLine($"size {a.Measurements.SizeX}×{a.Measurements.SizeY}×{a.Measurements.SizeZ}, parts {a.Measurements.PartCount}");
            Assert.InRange(a.Measurements.SizeY, 4.5f, 6.5f); // an R15 character is about 5.3 studs tall
            var views = SchematicRenderer.RenderStandardViews(SchematicRenderer.PrimitivesOf(a.Model), 512);
            Save("characterR15", views);
        }

        private static void Save(string name, List<(string View, byte[] Png)> views)
        {
            string? dir = Environment.GetEnvironmentVariable("OMNIBLOX_RENDER_OUT");
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);
            foreach (var (view, png) in views) File.WriteAllBytes(Path.Combine(dir, $"{name}_{view}.png"), png);
        }
    }
}
