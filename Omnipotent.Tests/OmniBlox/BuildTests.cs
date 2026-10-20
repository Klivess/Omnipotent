using System.Text;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox.Assets;
using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Rbx;

namespace Omnipotent.Tests.OmniBlox
{
    public sealed class TempDir : IDisposable
    {
        public TempDir() => Path = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "omniblox-test-" + Guid.NewGuid().ToString("N")[..10])).FullName;
        public string Path { get; }
        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }

    public class BuildTests
    {
        [Fact]
        public void EveryGameKitFile_ParsesAsLuau()
        {
            var files = GameKit.Files;
            Assert.Contains(files, f => f.Path == "Server/Data.luau");
            Assert.Contains(files, f => f.Path == "Server/Templates/Obby.luau");
            Assert.Contains(files, f => f.Path == "Client/Client.client.luau");
            Assert.Contains(files, f => f.Path == "Server/Boot.server.luau");
            foreach (var f in files)
            {
                var errors = LuauSyntax.Check(f.Source).Where(i => i.IsError).ToList();
                Assert.True(errors.Count == 0, $"{f.Path}: {string.Join("; ", errors)}");
            }
        }

        /// <summary>A small playable obby: spawn → three stepping stones with checkpoints → finish, plus a tree asset.</summary>
        internal static GameWorkspace ObbyWorkspace(string root, float gap = 6)
        {
            var ws = new GameWorkspace(Path.Combine(root, "workspace"), Path.Combine(root, "snapshots"));
            ws.EnsureCreated(new GameManifest { Title = "Test Obby", Template = "obby", Tips = { "Jump on the green pads!" } });
            var scene = new SceneDocument();
            scene.Objects.Add(new SceneObject { Id = "spawn", Kind = SceneKinds.Spawn, Name = "Start", Position = new float[] { 0, 0.5f, 0 }, Size = new float[] { 8, 1, 8 }, Tags = { "Checkpoint" }, Config = { ["Stage"] = 1L } });
            float z = 0;
            for (int stage = 2; stage <= 3; stage++)
            {
                z -= 8 + gap;
                scene.Objects.Add(new SceneObject { Id = "pad" + stage, Name = "Stage" + stage, Position = new float[] { 0, 2f * stage, z }, Size = new float[] { 8, 1, 8 }, Color = "#4CAF50", Tags = { "Checkpoint" }, Config = { ["Stage"] = (long)stage } });
            }
            z -= 8 + gap;
            scene.Objects.Add(new SceneObject { Id = "finish", Name = "Finish", Position = new float[] { 0, 8, z }, Size = new float[] { 8, 1, 8 }, Color = "gold", Tags = { "Finish" } });
            scene.Objects.Add(new SceneObject { Id = "lava", Name = "Lava", Position = new float[] { 0, -2, -20 }, Size = new float[] { 40, 1, 60 }, Color = "red", Material = "Neon", Tags = { "KillBrick" } });
            scene.Objects.Add(new SceneObject { Id = "tree1", Kind = SceneKinds.Asset, AssetId = 42, Name = "Pine", Position = new float[] { 20, 0, 0 }, Yaw = 0 });
            scene.Terrain.Add(new TerrainFill { Material = "Grass", Center = new float[] { 60, -4, 0 }, Size = new float[] { 60, 8, 60 } });
            ws.SaveScene(scene);
            ws.WriteFile("src/server/Hello.server.luau", "local Players = game:GetService(\"Players\")\nPlayers.PlayerAdded:Connect(function(p) print(\"hi\", p.Name) end)\n");
            ws.WriteFile("src/shared/Constants.luau", "return { Answer = 42 }\n");
            ws.WriteFile("src/tests/Game.spec.luau", "return { [\"answer\"] = function(t) t.eq(42, 42) end }\n");
            return ws;
        }

        internal static (Func<long, RbxInstance?> Load, Func<long, AssetPlacement?> Info) TreeAsset(float frontYaw = 0)
        {
            var analysis = AssetAnalyzer.Analyze(AssetAnalysisTests.SampleTree(), 42, "Pine Tree");
            var m = analysis.Measurements;
            var placement = new AssetPlacement(new RbxVector3(m.Pivot[0], m.Pivot[1], m.Pivot[2]), frontYaw, new RbxVector3(m.SizeX, m.SizeY, m.SizeZ), "Pine Tree");
            byte[] clean = RbxXmlWriter.WriteBytes(new Func<RbxDocument>(() => { var d = new RbxDocument(); d.AddRoot(analysis.Model); return d; })());
            return (id => id == 42 ? RbxFile.Read(clean).Roots[0] : null, id => id == 42 ? placement : null);
        }

        [Fact]
        public void Compile_ProducesAPlaceWithKitConfigScriptsAndWorld()
        {
            using var dir = new TempDir();
            var ws = ObbyWorkspace(dir.Path);
            var (load, info) = TreeAsset();
            var result = PlaceCompiler.Compile(new CompileInput
            {
                GameId = "g_test",
                Workspace = ws,
                LoadAsset = load,
                AssetInfo = info,
                Products = { new ProductRecord { Key = "skip", Kind = ProductKind.DeveloperProduct, Name = "Skip Stage", PriceRobux = 25, Grant = "skipstage", RobloxId = 123 } },
                TelemetryUrl = "https://example.test/omniblox/telemetry",
                TelemetrySecret = "s3cret",
            });
            Assert.True(result.Ok, string.Join("\n", result.Errors));
            var place = RbxFile.Read(result.PlaceXml);
            var all = place.AllInstances().ToList();

            var config = all.Single(i => i.Name == "Config" && i.Parent?.Name == "OmniKitServer");
            string configSource = config.GetString("Source")!;
            Assert.Contains("GameId = \"g_test\"", configSource);
            Assert.Contains("TemplateName = \"Obby\"", configSource);
            Assert.Contains("s3cret", configSource);
            Assert.True(LuauSyntax.IsValid(configSource), configSource);
            var publicConfig = all.Single(i => i.Name == "PublicConfig");
            Assert.DoesNotContain("s3cret", publicConfig.GetString("Source")!);
            Assert.True(LuauSyntax.IsValid(publicConfig.GetString("Source")!));

            Assert.Contains(all, i => i.Name == "OmniKitBoot" && i.ClassName == "Script" && i.Parent?.ClassName == "ServerScriptService");
            Assert.Contains(all, i => i.Name == "OmniKitClient" && i.ClassName == "LocalScript");
            Assert.Contains(all, i => i.Name == "Obby" && i.Parent?.Name == "Templates");
            Assert.Contains(all, i => i.Name == "Hello" && i.ClassName == "Script");
            Assert.Contains(all, i => i.Name == "Constants" && i.Parent?.Name == "Shared");
            Assert.Contains(all, i => i.Name == "Game.spec" && i.Parent?.Name == "OmniTests");
            Assert.True(all.Single(i => i.ClassName == "HttpService").GetBool("HttpEnabled"));

            var pad = all.Single(i => i.Name == "Stage2");
            Assert.Equal("Checkpoint", Encoding.UTF8.GetString((byte[])pad.Get("Tags")!.Value!));
            Assert.Equal(2.0, pad.FindFirstChild("Stage")!.GetAs<double>("Value"));
            Assert.Equal(new RbxColor3uint8(0x4C, 0xAF, 0x50), pad.Color);
            Assert.Contains(all, i => i.ClassName == "SpawnLocation" && i.Name == "Start");
        }

        [Fact]
        public void Compile_PlacesAssetsByBottomCentreAndLabelledFront()
        {
            using var dir = new TempDir();
            var ws = ObbyWorkspace(dir.Path);
            var (load, info) = TreeAsset(frontYaw: 90);
            var result = PlaceCompiler.Compile(new CompileInput { GameId = "g_test", Workspace = ws, LoadAsset = load, AssetInfo = info });
            Assert.True(result.Ok, string.Join("\n", result.Errors));
            var treeParts = result.Parts.Where(p => p.ObjectId == "tree1").ToList();
            Assert.Equal(2, treeParts.Count);
            var (min, max) = SchematicRenderer.Bounds(treeParts.Select(p => new RenderPrimitive(p.CFrame, p.Size, p.Shape, p.Color, 0, false)).ToList());
            Assert.Equal(0f, min.Y, 2);            // standing on y = 0
            Assert.Equal(20f, (min.X + max.X) / 2, 2); // centred on x = 20
            Assert.Equal(0f, (min.Z + max.Z) / 2, 2);
            Assert.Equal(20f, max.Y, 2);
            // FrontYaw 90 turned the model a quarter turn about Y.
            var trunk = treeParts.Single(p => p.Name == "Trunk");
            Assert.Equal(0f, trunk.CFrame.RightVector.X, 3);
        }

        [Fact]
        public void Compile_FailsWithFileAndLineOnBrokenLuau()
        {
            using var dir = new TempDir();
            var ws = ObbyWorkspace(dir.Path);
            ws.WriteFile("src/server/Broken.server.luau", "local x = 1\nif x then\n  print(x)\n");
            var result = PlaceCompiler.Compile(new CompileInput { GameId = "g_test", Workspace = ws });
            Assert.False(result.Ok);
            Assert.Contains(result.Errors, e => e.StartsWith("src/server/Broken.server.luau:4:") && e.Contains("expected 'end'"));
        }

        [Fact]
        public void Verifier_PassesAPlayableObby_AndCatchesImpossibleJumps()
        {
            using var dir = new TempDir();
            var (load, info) = TreeAsset();
            var good = ObbyWorkspace(Path.Combine(dir.Path, "good"));
            var ok = PlaceCompiler.Compile(new CompileInput { GameId = "g", Workspace = good, LoadAsset = load, AssetInfo = info });
            var issues = GeometryVerifier.Verify(ok.Parts, good.LoadScene(), good.LoadManifest());
            Assert.DoesNotContain(issues, i => i.IsError);
            Assert.DoesNotContain(issues, i => i.Message.Contains("unreachable"));

            var bad = ObbyWorkspace(Path.Combine(dir.Path, "bad"), gap: 30);
            var compiled = PlaceCompiler.Compile(new CompileInput { GameId = "g", Workspace = bad, LoadAsset = load, AssetInfo = info });
            var badIssues = GeometryVerifier.Verify(compiled.Parts, bad.LoadScene(), bad.LoadManifest());
            Assert.Contains(badIssues, i => i.ObjectId == "pad2" && i.Message.Contains("unreachable"));
        }

        [Fact]
        public void Verifier_FlagsFloatingAssets_AndCrampedSpawns()
        {
            using var dir = new TempDir();
            var ws = ObbyWorkspace(dir.Path);
            var scene = ws.LoadScene();
            scene.Find("tree1")!.Position = new float[] { 60, 9, 0 }; // terrain top is at y = 0 there
            scene.Objects.Add(new SceneObject { Id = "roof", Name = "Roof", Position = new float[] { 0, 3, 0 }, Size = new float[] { 6, 1, 6 } });
            ws.SaveScene(scene);
            var (load, info) = TreeAsset();
            var result = PlaceCompiler.Compile(new CompileInput { GameId = "g", Workspace = ws, LoadAsset = load, AssetInfo = info });
            var issues = GeometryVerifier.Verify(result.Parts, ws.LoadScene(), ws.LoadManifest());
            Assert.Contains(issues, i => i.ObjectId == "tree1" && i.Message.Contains("floats 9"));
            Assert.Contains(issues, i => i.ObjectId == "spawn" && i.IsError && i.Message.Contains("headroom"));
        }

        [Fact]
        public void Workspace_RefusesPathsOutsideItself_AndSnapshotsRestore()
        {
            using var dir = new TempDir();
            var ws = ObbyWorkspace(dir.Path);
            Assert.Throws<ArgumentException>(() => ws.WriteFile("../escape.luau", "x"));
            Assert.Throws<ArgumentException>(() => ws.WriteFile("src/run.exe", "x"));
            ws.Snapshot(1);
            ws.WriteFile("src/server/Hello.server.luau", "print('changed')");
            var (added, changed, removed) = ws.DiffAgainst(1);
            Assert.Contains("src/server/Hello.server.luau", changed);
            ws.Restore(1);
            Assert.Contains("PlayerAdded", ws.ReadFile("src/server/Hello.server.luau"));
        }

        [Theory]
        [InlineData("#FF8000", 255, 128, 0)]
        [InlineData("rgb(10, 20, 30)", 10, 20, 30)]
        [InlineData("dark green", 0x28, 0x7F, 0x47)]
        public void Colours_Parse(string text, int r, int g, int b) =>
            Assert.Equal(new RbxColor3uint8((byte)r, (byte)g, (byte)b), PlaceCompiler.ParseColor(text));

        [Fact]
        public void LuauLiteral_WritesValidTables()
        {
            string lua = "return " + LuauLiteral.Write(new Dictionary<string, object?>
            {
                ["Name"] = "Quote \" and \\ slash\nnewline",
                ["end"] = 1,
                ["List"] = new List<object?> { 1, 2.5, true, null },
                ["Nested"] = new Dictionary<string, object?> { ["X"] = JToken.Parse("{\"a\": [1, 2]}") },
            });
            Assert.True(LuauSyntax.IsValid(lua), lua);
            Assert.Contains("[\"end\"] = 1", lua);
        }
    }
}
