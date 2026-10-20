using System.Text;
using Omnipotent.Services.OmniBlox.Rbx;
using Xunit.Abstractions;

namespace Omnipotent.Tests.OmniBlox
{
    public class RbxFormatTests
    {
        private readonly ITestOutputHelper output;
        public RbxFormatTests(ITestOutputHelper output) => this.output = output;

        /// <summary>Real Roblox-written files from the local Roblox install (read in place, never copied into the repo).</summary>
        internal static List<string> InstalledRobloxFiles()
        {
            string versions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions");
            if (!Directory.Exists(versions)) return new List<string>();
            var newest = Directory.EnumerateDirectories(versions)
                .Where(d => Directory.Exists(Path.Combine(d, "content")))
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (newest == null) return new List<string>();
            return Directory.EnumerateFiles(Path.Combine(newest, "content"), "*.rbxm*", SearchOption.AllDirectories).ToList();
        }

        internal static string? FindInstalled(string fileName) =>
            InstalledRobloxFiles().FirstOrDefault(f => Path.GetFileName(f).Equals(fileName, StringComparison.OrdinalIgnoreCase));

        [Fact]
        public void EveryInstalledRobloxModelFile_Parses()
        {
            var files = InstalledRobloxFiles();
            if (files.Count == 0) { output.WriteLine("No local Roblox install; skipped."); return; }

            int binary = 0, xml = 0, instances = 0;
            var undecoded = new HashSet<string>();
            foreach (string path in files)
            {
                byte[] data = File.ReadAllBytes(path);
                var doc = RbxFile.Read(data);
                if (RbxBinaryReader.LooksBinary(data)) binary++; else xml++;
                instances += doc.AllInstances().Count();
                foreach (var u in doc.UndecodedProperties) undecoded.Add(u);
                Assert.NotEmpty(doc.Roots);
            }
            output.WriteLine($"{files.Count} files ({binary} binary, {xml} xml), {instances} instances.");
            foreach (var u in undecoded.OrderBy(x => x)) output.WriteLine("undecoded: " + u);
            Assert.True(binary > 0);
        }

        [Fact]
        public void CharacterR15_HasTheRigWithRealSizes()
        {
            string? path = FindInstalled("characterR15.rbxm");
            if (path == null) { output.WriteLine("characterR15.rbxm not installed; skipped."); return; }

            var doc = RbxFile.Read(File.ReadAllBytes(path));
            var all = doc.AllInstances().ToList();
            var root = all.First(i => i.Name == "HumanoidRootPart");
            Assert.Equal("Part", root.ClassName);
            var size = root.Size!.Value;
            Assert.Equal(2f, size.X, 2);
            Assert.Equal(2f, size.Y, 2);
            Assert.Equal(1f, size.Z, 2);
            Assert.Contains(all, i => i.Name == "UpperTorso" && i.ClassName == "MeshPart");
            Assert.Contains(all, i => i.ClassName == "Humanoid");
            Assert.Contains(all, i => i.ClassName == "Motor6D");
            // Joints reference the parts they connect.
            var joint = all.First(i => i.ClassName == "Motor6D");
            Assert.IsType<RbxInstance>(joint.Get("Part0")?.Value);
        }

        [Fact]
        public void BinaryModel_SurvivesXmlRoundTrip()
        {
            string? path = FindInstalled("characterR15.rbxm") ?? InstalledRobloxFiles().FirstOrDefault(f => f.EndsWith(".rbxm", StringComparison.OrdinalIgnoreCase));
            if (path == null) { output.WriteLine("No binary fixture; skipped."); return; }

            var original = RbxFile.Read(File.ReadAllBytes(path));
            byte[] xml = RbxXmlWriter.WriteBytes(original);
            Assert.True(RbxXmlReader.LooksXml(xml));
            var reread = RbxFile.Read(xml);

            var a = original.AllInstances().ToList();
            var b = reread.AllInstances().ToList();
            Assert.Equal(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.Equal(a[i].ClassName, b[i].ClassName);
                Assert.Equal(a[i].Name, b[i].Name);
                Assert.Equal(a[i].Size, b[i].Size);
                Assert.Equal(a[i].CFrame, b[i].CFrame);
                Assert.Equal(a[i].Color, b[i].Color);
                if (a[i].GetString("Source") is string src) Assert.Equal(src, b[i].GetString("Source"));
                if (a[i].Get("Part0")?.Value is RbxInstance p0) Assert.Equal(p0.Name, (b[i].Get("Part0")?.Value as RbxInstance)?.Name);
            }
        }

        [Fact]
        public void EveryValueType_SurvivesXmlRoundTrip()
        {
            var doc = new RbxDocument();
            var model = doc.AddRoot(new RbxInstance("Model", "Thing"));
            var part = model.AddChild(new RbxInstance("Part", "Block"));
            part.Set("size", RbxValue.Vec3(new RbxVector3(4, 1.5f, 2)));
            part.Set("CFrame", RbxValue.Frame(RbxCFrame.FromYaw(new RbxVector3(1, 2, 3), MathF.PI / 2)));
            part.Set("Color3uint8", RbxValue.Color8(new RbxColor3uint8(10, 200, 30)));
            part.Set("Anchored", RbxValue.Bool(true));
            part.Set("Material", RbxValue.Token(RbxMaterials.Wood));
            part.Set("Transparency", RbxValue.Float(0.25f));
            part.Set("Mass", RbxValue.Double(12.5));
            part.Set("CollisionGroupId", RbxValue.Int(-7));
            part.Set("SourceAssetId", new RbxValue(RbxType.Int64, 9_000_000_000L));
            part.Set("Tags", new RbxValue(RbxType.BinaryString, new byte[] { 1, 2, 0, 255 }));
            part.Set("CustomPhysicalProperties", new RbxValue(RbxType.PhysicalProperties, new RbxPhysicalProperties(true, 1, 0.3f, 0.5f, 1, 1)));
            part.Set("PhysicsData", new RbxValue(RbxType.SharedString, Encoding.UTF8.GetBytes("shared bytes")));
            var script = model.AddChild(new RbxInstance("Script", "Main"));
            script.Set("Source", RbxValue.Source("print(\"hi\") -- ]]> tricky\nlocal t = {}"));
            var mesh = model.AddChild(new RbxInstance("MeshPart", "Mesh"));
            mesh.Set("MeshId", RbxValue.ContentUrl("rbxassetid://12345"));
            mesh.Set("TextureID", RbxValue.ContentUrl(""));
            model.Set("PrimaryPart", RbxValue.Reference(part));
            model.Set("WorldPivotData", new RbxValue(RbxType.OptionalCFrame, (RbxCFrame?)new RbxCFrame(new RbxVector3(5, 0, 0))));
            var gui = model.AddChild(new RbxInstance("Frame", "Ui"));
            gui.Set("Size", RbxValue.UDim2(new RbxUDim2(new RbxUDim(0.5f, 10), new RbxUDim(1, -4))));
            var fx = model.AddChild(new RbxInstance("ParticleEmitter", "Fx"));
            fx.Set("Size", new RbxValue(RbxType.NumberSequence, new List<RbxNumberKeypoint> { new(0, 1, 0), new(1, 2, 0.5f) }));
            fx.Set("Color", new RbxValue(RbxType.ColorSequence, new List<RbxColorKeypoint> { new(0, new RbxColor3(1, 0, 0), 0), new(1, new RbxColor3(0, 0, 1), 0) }));
            fx.Set("Lifetime", new RbxValue(RbxType.NumberRange, new RbxNumberRange(1, 3)));
            var label = model.AddChild(new RbxInstance("TextLabel", "Label"));
            label.Set("FontFace", new RbxValue(RbxType.Font, new RbxFont("rbxasset://fonts/families/GothamSSm.json", 700, "Normal", "")));
            label.Set("TextColor3", RbxValue.Color(new RbxColor3(1, 1, 1)));

            var reread = RbxFile.Read(RbxXmlWriter.WriteBytes(doc));
            var m = reread.Roots.Single();
            var p = m.FindFirstChild("Block")!;
            Assert.Equal(new RbxVector3(4, 1.5f, 2), p.Size);
            Assert.Equal(part.CFrame, p.CFrame);
            Assert.Equal(new RbxColor3uint8(10, 200, 30), p.Color);
            Assert.True(p.GetBool("Anchored"));
            Assert.Equal(RbxMaterials.Wood, p.GetAs<uint>("Material"));
            Assert.Equal(0.25f, p.Transparency);
            Assert.Equal(12.5, p.GetAs<double>("Mass"));
            Assert.Equal(-7, p.GetAs<int>("CollisionGroupId"));
            Assert.Equal(9_000_000_000L, p.GetAs<long>("SourceAssetId"));
            Assert.Equal(new byte[] { 1, 2, 0, 255 }, p.Get("Tags")!.Value as byte[]);
            Assert.Equal(new RbxPhysicalProperties(true, 1, 0.3f, 0.5f, 1, 1), p.Get("CustomPhysicalProperties")!.Value);
            Assert.Equal("shared bytes", Encoding.UTF8.GetString((byte[])p.Get("PhysicsData")!.Value!));
            Assert.Equal(script.GetString("Source"), m.FindFirstChild("Main")!.GetString("Source"));
            Assert.Equal(RbxType.ProtectedString, m.FindFirstChild("Main")!.Get("Source")!.Type);
            Assert.Equal("rbxassetid://12345", m.FindFirstChild("Mesh")!.GetString("MeshId"));
            Assert.Equal(RbxType.Content, m.FindFirstChild("Mesh")!.Get("MeshId")!.Type);
            Assert.Same(p, m.Get("PrimaryPart")!.Value);
            Assert.Equal(new RbxCFrame(new RbxVector3(5, 0, 0)), (RbxCFrame?)m.Get("WorldPivotData")!.Value);
            Assert.Equal(new RbxUDim2(new RbxUDim(0.5f, 10), new RbxUDim(1, -4)), m.FindFirstChild("Ui")!.GetAs<RbxUDim2>("Size"));
            var f = m.FindFirstChild("Fx")!;
            Assert.Equal(2, ((List<RbxNumberKeypoint>)f.Get("Size")!.Value!).Count);
            Assert.Equal(new RbxColor3(0, 0, 1), ((List<RbxColorKeypoint>)f.Get("Color")!.Value!)[1].Color);
            Assert.Equal(new RbxNumberRange(1, 3), f.GetAs<RbxNumberRange>("Lifetime"));
            Assert.Equal(700, ((RbxFont)m.FindFirstChild("Label")!.Get("FontFace")!.Value!).Weight);
        }

        [Fact]
        public void BasicRotationIds_AreProperRotations()
        {
            var identity = RbxCFrame.BasicRotation(0x02)!;
            Assert.Equal(new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, identity);
            // 0x03 from the published table: rows (1,0,0), (0,0,-1), (0,1,0).
            Assert.Equal(new float[] { 1, 0, 0, 0, 0, -1, 0, 1, 0 }, RbxCFrame.BasicRotation(0x03)!);
            int valid = 0;
            for (int id = 0; id < 40; id++)
            {
                var m = RbxCFrame.BasicRotation((byte)id);
                if (m == null) continue;
                valid++;
                float det = m[0] * (m[4] * m[8] - m[5] * m[7]) - m[1] * (m[3] * m[8] - m[5] * m[6]) + m[2] * (m[3] * m[7] - m[4] * m[6]);
                Assert.Equal(1f, det, 4);
            }
            Assert.Equal(24, valid);
        }

        [Fact]
        public void Lz4Block_HandlesOverlappingMatches()
        {
            // 'a', then a 15-byte match at offset 1 (an overlapping run), then literals "xyz".
            byte[] block = { 0x1B, (byte)'a', 0x01, 0x00, 0x30, (byte)'x', (byte)'y', (byte)'z' };
            var output = RbxCompression.Lz4Block(block, 19);
            Assert.Equal(new string('a', 16) + "xyz", Encoding.ASCII.GetString(output));
            Assert.Throws<FormatException>(() => RbxCompression.Lz4Block(block, 25));
        }

        [Fact]
        public void ZstdChunks_Decompress()
        {
            byte[] payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("roblox chunk payload ", 200)));
            using var ms = new MemoryStream();
            using (var z = new ZstdSharp.CompressionStream(ms)) z.Write(payload);
            byte[] compressed = ms.ToArray();
            Assert.Equal(new byte[] { 0x28, 0xB5, 0x2F, 0xFD }, compressed[..4]);
            Assert.Equal(payload, RbxCompression.Decompress(compressed, payload.Length));
        }

        [Fact]
        public void CFrameMath_ComposesAndInverts()
        {
            var a = RbxCFrame.FromYaw(new RbxVector3(10, 0, 0), MathF.PI / 2);
            var p = a.PointToWorldSpace(new RbxVector3(0, 0, -1)); // one stud "forward" in local space
            Assert.Equal(9f, p.X, 4);  // yaw +90° turns forward (−Z) toward −X
            Assert.Equal(0f, p.Z, 4);
            var back = a.Inverse() * a;
            Assert.True(back.IsIdentityRotation || MathF.Abs(back.Rotation[0] - 1) < 1e-5f);
            Assert.Equal(0f, back.Position.Magnitude, 4);
            Assert.Equal(90f, a.YawDegrees, 3);
        }
    }
}
