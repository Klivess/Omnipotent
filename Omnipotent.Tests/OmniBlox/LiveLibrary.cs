using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox.Assets;
using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Rbx;
using Omnipotent.Services.OmniBlox.Roblox;

namespace Omnipotent.Tests.OmniBlox
{
    /// <summary>
    /// A local Asset Library rebuilt from what the live studio exported: its games' place files with their
    /// scene.json (so every placed library model can be found), plus each model's label and thumbnail. Each model
    /// is recovered from one placed copy (un-turned and un-scaled back into its own space) and measured again
    /// exactly as the harvester would. Used by the env-gated tests that generate real maps with real models
    /// (OMNIBLOX_LIVE_DIR), never by the regular suite.
    ///
    /// Folder layout: <c>X.rbxlx</c> + <c>X_scene.json</c> pairs, and <c>thumbs/&lt;id&gt;.png</c> + <c>thumbs/&lt;id&gt;.json</c>
    /// (the dashboard's asset detail).
    /// </summary>
    internal static class LiveLibrary
    {
        public static AssetLibrary Import(string liveDir, string libraryDir, Action<string>? log = null)
        {
            var library = new AssetLibrary(libraryDir);
            library.Open();
            ImportInto(library, liveDir, log);
            return library;
        }

        /// <summary>Adds every exported model to an open library (a studio's own, for the scaffold).</summary>
        public static void ImportInto(AssetLibrary library, string liveDir, Action<string>? log = null)
        {
            var details = Directory.GetFiles(Path.Combine(liveDir, "thumbs"), "*.json")
                .Select(f => (Id: long.Parse(Path.GetFileNameWithoutExtension(f)), Json: JObject.Parse(File.ReadAllText(f))))
                .ToDictionary(x => x.Id, x => x.Json);
            var places = Directory.GetFiles(liveDir, "*.rbxlx")
                .Select(p => (Place: p, Scene: Path.Combine(liveDir, Path.GetFileNameWithoutExtension(p) + "_scene.json")))
                .Where(x => File.Exists(x.Scene))
                .Select(x => (Doc: RbxFile.Read(File.ReadAllBytes(x.Place)), Scene: SceneDocument.Load(x.Scene)))
                .ToList();
            var now = DateTime.UtcNow;
            foreach (var (id, json) in details)
            {
                var asset = json["Asset"] as JObject;
                var label = asset?["Label"]?.ToObject<AssetLabel>();
                if (asset == null || label == null) continue;
                var model = Recover(places, id, label.FrontYaw);
                if (model == null) { log?.Invoke($"{id}: no placed copy found"); continue; }
                var doc = new RbxDocument();
                doc.AddRoot(model);
                string name = (string?)asset["Name"] ?? "Model";
                var analysis = AssetAnalyzer.Analyze(doc, id, name);
                library.UpsertDiscovered(new[] { new CreatorStoreAsset { AssetId = id, Name = name, AssetTypeId = 10, CreatorName = (string?)asset["CreatorName"] ?? "", CreatorVerified = (bool?)asset["CreatorVerified"] ?? false, UpVotes = (long?)asset["UpVotes"] ?? 0 } }, "live", 0, now);
                var source = RbxXmlWriter.WriteBytes(doc);
                library.SaveAnalysis(id, source, analysis, Array.Empty<(string, byte[])>(), (long?)asset["Triangles"] ?? 0, now);
                library.SaveLabel(id, label, now, AssetLabeler.ScaleVersion);
                string thumb = Path.Combine(liveDir, "thumbs", id + ".png");
                if (File.Exists(thumb)) library.SaveThumbnail(id, File.ReadAllBytes(thumb));
                log?.Invoke($"{id} {name}: {analysis.Measurements.SizeX:0.#}×{analysis.Measurements.SizeY:0.#}×{analysis.Measurements.SizeZ:0.#}, {analysis.Measurements.PartCount} parts");
            }
        }

        /// <summary>One placed copy of a model, moved back into its own space: pivot at the origin, unturned, unscaled.</summary>
        private static RbxInstance? Recover(List<(RbxDocument Doc, SceneDocument Scene)> places, long assetId, float frontYaw)
        {
            foreach (var (doc, scene) in places)
            {
                var candidates = scene.Objects.Where(o => o.AssetId == assetId)
                    .OrderBy(o => MathF.Min(o.Yaw % 360, 360 - o.Yaw % 360) + MathF.Abs(o.Scale - 1) * 100).ToList();
                var workspace = doc.Roots.FirstOrDefault(r => r.ClassName == "Workspace");
                if (workspace == null) continue;
                foreach (var obj in candidates)
                {
                    RbxInstance? at = workspace;
                    foreach (string segment in obj.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
                        at = at?.Children.FirstOrDefault(c => c.Name == segment && c.ClassName is "Folder" or "Model");
                    if (at == null) continue;
                    RbxInstance? best = null;
                    float bestDistance = float.MaxValue;
                    foreach (var m in at.Children.Where(c => c.ClassName == "Model" && c.Name == (string.IsNullOrWhiteSpace(obj.Name) ? obj.Id : obj.Name)))
                    {
                        var measured = new AssetMeasurements();
                        AssetAnalyzer.Measure(m, measured);
                        float d = MathF.Abs(measured.Pivot[0] - obj.Position[0]) + MathF.Abs(measured.Pivot[2] - obj.Position[2]) + MathF.Abs(measured.Pivot[1] - obj.Position[1]);
                        if (d < bestDistance) { bestDistance = d; best = m; }
                    }
                    if (best == null || bestDistance > 1.5f) continue;
                    var copy = Clone(best);
                    float scale = obj.Scale <= 0 ? 1 : obj.Scale;
                    var rotation = RbxCFrame.FromYaw(RbxVector3.Zero, (obj.Yaw + frontYaw) * MathF.PI / 180f);
                    var inverse = rotation.Inverse();
                    var target = new RbxVector3(obj.Position[0], obj.Position[1], obj.Position[2]);
                    RbxCFrame Back(RbxCFrame cf)
                    {
                        var local = inverse.RotateVector(cf.Position - target) * (1 / scale);
                        return (inverse * cf.WithPosition(RbxVector3.Zero)).WithPosition(local);
                    }
                    foreach (var inst in copy.SelfAndDescendants())
                    {
                        if (inst.CFrame is { } cf && inst.Size is { } size)
                        {
                            inst.Set("CFrame", RbxValue.Frame(Back(cf)));
                            inst.Set(inst.Properties.ContainsKey("size") ? "size" : "Size", RbxValue.Vec3(size * (1 / scale)));
                        }
                        if (inst.Get("WorldPivotData")?.Value is RbxCFrame pivot) inst.Set("WorldPivotData", new RbxValue(RbxType.OptionalCFrame, (RbxCFrame?)Back(pivot)));
                        if (scale != 1)
                        {
                            foreach (string local in new[] { "C0", "C1" })
                                if (inst.Get(local)?.Value is RbxCFrame c) inst.Set(local, RbxValue.Frame(c.WithPosition(c.Position * (1 / scale))));
                            if (inst.ClassName is "SpecialMesh" or "BlockMesh" or "CylinderMesh")
                            {
                                if (inst.GetAs<RbxVector3>("Scale") is { } s) inst.Set("Scale", RbxValue.Vec3(s * (1 / scale)));
                                if (inst.GetAs<RbxVector3>("Offset") is { } o) inst.Set("Offset", RbxValue.Vec3(o * (1 / scale)));
                            }
                        }
                    }
                    // The placed copy carries the game's tags and config values: drop them.
                    copy.Properties.Remove("Tags");
                    foreach (var child in copy.Children.Where(c => c.ClassName.EndsWith("Value", StringComparison.Ordinal)).ToList()) child.RemoveFromParent();
                    return copy;
                }
            }
            return null;
        }

        private static RbxInstance Clone(RbxInstance source)
        {
            var doc = new RbxDocument();
            var copy = DeepCopy(source);
            doc.AddRoot(copy);
            return RbxFile.Read(RbxXmlWriter.WriteBytes(doc)).Roots[0];
        }

        private static RbxInstance DeepCopy(RbxInstance source)
        {
            var copy = new RbxInstance(source.ClassName, source.Name);
            foreach (var (k, v) in source.Properties) copy.Properties[k] = v;
            foreach (var child in source.Children) copy.AddChild(DeepCopy(child));
            return copy;
        }
    }
}
