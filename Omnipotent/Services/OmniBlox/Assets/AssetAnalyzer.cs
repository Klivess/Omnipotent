using System.Text.RegularExpressions;
using Omnipotent.Services.OmniBlox.Rbx;

namespace Omnipotent.Services.OmniBlox.Assets
{
    public sealed class ScriptFinding
    {
        public string ClassName { get; set; } = "";
        public string Path { get; set; } = "";
        public int Bytes { get; set; }
        public List<string> Flags { get; set; } = new();
        public bool Suspicious => Flags.Count > 0;
    }

    public sealed class ColorShare
    {
        public string Hex { get; set; } = "";
        public string Name { get; set; } = "";
        public double Share { get; set; }
    }

    public sealed class MaterialShare
    {
        public string Material { get; set; } = "";
        public double Share { get; set; }
    }

    /// <summary>What the analyzer measured about one model, in the model's own coordinates.</summary>
    public sealed class AssetMeasurements
    {
        public float SizeX { get; set; }
        public float SizeY { get; set; }
        public float SizeZ { get; set; }
        /// <summary>Bottom-centre of the visible bounding box: the point placed on the ground.</summary>
        public float[] Pivot { get; set; } = new float[3];
        public float[] BoundsMin { get; set; } = new float[3];
        public float[] BoundsMax { get; set; } = new float[3];
        public int PartCount { get; set; }
        public int MeshPartCount { get; set; }
        public int InstanceCount { get; set; }
        public int LightCount { get; set; }
        public int SoundCount { get; set; }
        public int ParticleCount { get; set; }
        public int DecalCount { get; set; }
        public bool HasHumanoid { get; set; }
        public bool HasSeat { get; set; }
        public bool HasTool { get; set; }
        public string Anchored { get; set; } = "all";
        public double AverageTransparency { get; set; }
        public double TexturedShare { get; set; }
        public List<ColorShare> Colors { get; set; } = new();
        public List<MaterialShare> Materials { get; set; } = new();
        public Dictionary<string, int> ClassCounts { get; set; } = new();
        /// <summary>tall, wide, flat or compact.</summary>
        public string Proportions { get; set; } = "compact";
        /// <summary>low, medium or high (parts + mesh triangles).</summary>
        public string PerformanceCost { get; set; } = "low";
    }

    public sealed class AssetAnalysis
    {
        public required RbxInstance Model { get; init; }
        public required AssetMeasurements Measurements { get; init; }
        public List<ScriptFinding> RemovedScripts { get; } = new();
        public List<string> RemovedOther { get; } = new();
        public bool Placeable { get; set; } = true;
        public List<string> Problems { get; } = new();
        public List<string> Notes { get; } = new();
    }

    /// <summary>
    /// Cleans and measures a downloaded free model. Scripts are always removed — free models are a known
    /// source of hidden backdoors (remote require, obfuscated loaders) — and recorded with what they looked
    /// like. Instances the publishing API cannot carry (unions, SurfaceAppearance, wraps, editables) make
    /// a model not placeable; movers, explosions, packages and spawn points are stripped.
    /// </summary>
    public static class AssetAnalyzer
    {
        private static readonly HashSet<string> ScriptClasses = new(StringComparer.Ordinal) { "Script", "LocalScript", "ModuleScript", "CoreScript" };

        private static readonly HashSet<string> StripClasses = new(StringComparer.Ordinal)
        {
            "Explosion", "BodyVelocity", "BodyForce", "BodyGyro", "BodyPosition", "BodyThrust", "BodyAngularVelocity",
            "RocketPropulsion", "LinearVelocity", "AngularVelocity", "VectorForce", "Torque", "LineForce",
            "RemoteEvent", "RemoteFunction", "BindableEvent", "BindableFunction", "UnreliableRemoteEvent",
            "PackageLink", "Camera", "SurfaceAppearance", "WrapLayer", "WrapTarget", "EditableImage", "EditableMesh",
            "Sky", "Atmosphere", "Clouds", "PostEffect", "BloomEffect", "BlurEffect", "ColorCorrectionEffect", "DepthOfFieldEffect", "SunRaysEffect",
        };

        private static readonly HashSet<string> UnpublishableClasses = new(StringComparer.Ordinal)
        {
            "UnionOperation", "NegateOperation", "IntersectOperation", "PartOperation",
        };

        internal static readonly HashSet<string> PartClasses = new(StringComparer.Ordinal)
        {
            "Part", "MeshPart", "WedgePart", "CornerWedgePart", "TrussPart", "SpawnLocation", "Seat", "VehicleSeat",
            "SkateboardPlatform", "UnionOperation", "NegateOperation", "IntersectOperation", "PartOperation", "FlagStand",
        };

        private static readonly (Regex Pattern, string Flag)[] SuspiciousPatterns =
        {
            (new Regex(@"require\s*\(\s*\d{6,}", RegexOptions.Compiled), "loads a remote module by asset id (classic backdoor)"),
            (new Regex(@"\b(getfenv|setfenv)\b", RegexOptions.Compiled), "rewrites function environments"),
            (new Regex(@"\bloadstring\b", RegexOptions.Compiled), "compiles code at runtime"),
            (new Regex(@"string\.reverse|\\\d{2,3}\\\d{2,3}\\\d{2,3}|string\.char\s*\(\s*\d+\s*,\s*\d+", RegexOptions.Compiled), "obfuscated text"),
            (new Regex(@"HttpService[\s\S]{0,200}(GetAsync|PostAsync|RequestAsync)", RegexOptions.Compiled), "talks to outside servers"),
            (new Regex(@"\bTeleportService\b", RegexOptions.Compiled), "teleports players"),
            (new Regex(@"MarketplaceService[\s\S]{0,120}Prompt", RegexOptions.Compiled), "prompts purchases"),
            (new Regex(@"\bInsertService\b", RegexOptions.Compiled), "inserts more assets at runtime"),
            (new Regex(@":Kick\s*\(", RegexOptions.Compiled), "kicks players"),
        };

        public static AssetAnalysis Analyze(RbxDocument doc, long assetId, string assetName)
        {
            var model = WrapRoots(doc, assetName);
            var analysis = new AssetAnalysis { Model = model, Measurements = new AssetMeasurements() };

            foreach (var inst in model.Descendants().ToList())
            {
                if (ScriptClasses.Contains(inst.ClassName))
                {
                    string source = inst.GetString("Source") ?? "";
                    var finding = new ScriptFinding { ClassName = inst.ClassName, Path = inst.FullName, Bytes = source.Length };
                    foreach (var (pattern, flag) in SuspiciousPatterns)
                        if (pattern.IsMatch(source) && !finding.Flags.Contains(flag)) finding.Flags.Add(flag);
                    if (source.Split('\n').Any(l => l.Length > 2000)) finding.Flags.Add("very long lines (packed code)");
                    analysis.RemovedScripts.Add(finding);
                    inst.RemoveFromParent();
                    continue;
                }
                if (UnpublishableClasses.Contains(inst.ClassName))
                {
                    analysis.Placeable = false;
                    if (!analysis.Problems.Contains("contains unions, which the publishing API cannot carry"))
                        analysis.Problems.Add("contains unions, which the publishing API cannot carry");
                }
                if (StripClasses.Contains(inst.ClassName))
                {
                    analysis.RemovedOther.Add(inst.ClassName);
                    inst.RemoveFromParent();
                    continue;
                }
                if (inst.ClassName == "SpawnLocation")
                {
                    // Our games place their own spawns; keep the geometry as a plain part.
                    inst.ClassName = "Part";
                    foreach (string p in new[] { "AllowTeamChangeOnTouch", "Duration", "Neutral", "TeamColor", "Enabled" }) inst.Properties.Remove(p);
                    analysis.Notes.Add("a spawn point was turned into a plain part");
                }
                if (inst.ClassName == "Sound")
                {
                    inst.Set("Playing", RbxValue.Bool(false));
                    inst.Set("PlayOnRemove", RbxValue.Bool(false));
                }
                // Free models frequently ship anchored=false decor that falls apart on load.
                if (PartClasses.Contains(inst.ClassName)) inst.Set("Anchored", RbxValue.Bool(true));
            }

            if (analysis.RemovedScripts.Count > 0)
            {
                int bad = analysis.RemovedScripts.Count(s => s.Suspicious);
                analysis.Notes.Add($"removed {analysis.RemovedScripts.Count} script(s)" + (bad > 0 ? $", {bad} with suspicious patterns" : ""));
            }

            Measure(model, analysis.Measurements);
            var m = analysis.Measurements;
            if (m.PartCount == 0)
            {
                analysis.Placeable = false;
                analysis.Problems.Add("has no visible parts");
            }
            if (m.SizeX > 2048 || m.SizeY > 2048 || m.SizeZ > 2048)
            {
                analysis.Placeable = false;
                analysis.Problems.Add("is larger than 2048 studs");
            }
            if (m.PartCount > 4000)
            {
                analysis.Placeable = false;
                analysis.Problems.Add($"has {m.PartCount} parts, too heavy for mobile players");
            }
            return analysis;
        }

        /// <summary>A model file can have several roots; placement wants exactly one Model.</summary>
        private static RbxInstance WrapRoots(RbxDocument doc, string assetName)
        {
            if (doc.Roots.Count == 1 && doc.Roots[0].ClassName == "Model") return doc.Roots[0];
            var model = new RbxInstance("Model", string.IsNullOrWhiteSpace(assetName) ? "Asset" : assetName);
            foreach (var root in doc.Roots.ToList()) model.AddChild(root);
            return model;
        }

        public static void Measure(RbxInstance model, AssetMeasurements m)
        {
            var parts = model.Descendants().Where(i => PartClasses.Contains(i.ClassName) && i.Size != null && i.CFrame != null).ToList();
            m.InstanceCount = model.Descendants().Count();
            m.ClassCounts = model.Descendants().GroupBy(i => i.ClassName).OrderByDescending(g => g.Count()).Take(20).ToDictionary(g => g.Key, g => g.Count());
            m.MeshPartCount = parts.Count(p => p.ClassName == "MeshPart");
            m.LightCount = model.Descendants().Count(i => i.ClassName is "PointLight" or "SpotLight" or "SurfaceLight");
            m.SoundCount = model.Descendants().Count(i => i.ClassName == "Sound");
            m.ParticleCount = model.Descendants().Count(i => i.ClassName is "ParticleEmitter" or "Fire" or "Smoke" or "Sparkles" or "Beam" or "Trail");
            m.DecalCount = model.Descendants().Count(i => i.ClassName is "Decal" or "Texture");
            m.HasHumanoid = model.Descendants().Any(i => i.ClassName == "Humanoid");
            m.HasSeat = parts.Any(p => p.ClassName is "Seat" or "VehicleSeat");
            m.HasTool = model.SelfAndDescendants().Any(i => i.ClassName == "Tool");

            var visible = parts.Where(p => p.Transparency < 0.95f).ToList();
            m.PartCount = visible.Count;
            var boundsParts = visible.Count > 0 ? visible : parts;
            if (boundsParts.Count == 0) return;

            var min = new RbxVector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new RbxVector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var p in boundsParts)
                foreach (var corner in Corners(p.CFrame!.Value, p.Size!.Value))
                {
                    min = min.Min(corner);
                    max = max.Max(corner);
                }
            var size = max - min;
            m.SizeX = Round(size.X);
            m.SizeY = Round(size.Y);
            m.SizeZ = Round(size.Z);
            m.BoundsMin = new[] { Round(min.X), Round(min.Y), Round(min.Z) };
            m.BoundsMax = new[] { Round(max.X), Round(max.Y), Round(max.Z) };
            m.Pivot = new[] { Round((min.X + max.X) / 2), Round(min.Y), Round((min.Z + max.Z) / 2) };

            float horizontal = MathF.Max(size.X, size.Z);
            m.Proportions = size.Y > horizontal * 1.6f ? "tall"
                : size.Y < horizontal * 0.25f ? "flat"
                : MathF.Max(size.X, size.Z) > MathF.Min(size.X, size.Z) * 2.5f ? "wide"
                : "compact";

            int anchored = parts.Count(p => p.GetBool("Anchored") == true);
            m.Anchored = anchored == parts.Count ? "all" : anchored == 0 ? "none" : "some";

            double totalArea = 0, transparencySum = 0, texturedArea = 0;
            var colorArea = new Dictionary<string, (RbxColor3uint8 Color, double Area)>();
            var materialArea = new Dictionary<string, double>();
            foreach (var p in visible.Count > 0 ? visible : parts)
            {
                var s = p.Size!.Value;
                double area = 2 * (s.X * s.Y + s.Y * s.Z + s.X * s.Z);
                totalArea += area;
                transparencySum += p.Transparency * area;
                bool textured = p.ClassName == "MeshPart" && !string.IsNullOrEmpty(p.GetString("TextureID"))
                    || p.Children.Any(c => c.ClassName is "SpecialMesh" && !string.IsNullOrEmpty(c.GetString("TextureId")))
                    || p.Children.Any(c => c.ClassName is "Texture" or "Decal");
                if (textured) texturedArea += area;
                if (p.Color is { } color)
                {
                    // Textures paint over the part colour: count it at a quarter weight.
                    double weight = textured ? area * 0.25 : area;
                    string key = color.Hex;
                    colorArea[key] = colorArea.TryGetValue(key, out var e) ? (color, e.Area + weight) : (color, weight);
                }
                string material = RbxMaterials.Name(p.GetAs<uint>("Material") ?? RbxMaterials.Plastic);
                materialArea[material] = materialArea.GetValueOrDefault(material) + area;
            }
            if (totalArea > 0)
            {
                m.AverageTransparency = Math.Round(transparencySum / totalArea, 3);
                m.TexturedShare = Math.Round(texturedArea / totalArea, 3);
                double colorTotal = colorArea.Values.Sum(v => v.Area);
                m.Colors = colorArea.Values
                    .GroupBy(v => RbxColorNames.Describe(v.Color))
                    .Select(g => new ColorShare
                    {
                        Name = g.Key,
                        Hex = g.OrderByDescending(v => v.Area).First().Color.Hex,
                        Share = Math.Round(g.Sum(v => v.Area) / colorTotal, 3),
                    })
                    .OrderByDescending(c => c.Share).Take(5).ToList();
                m.Materials = materialArea.OrderByDescending(kv => kv.Value).Take(4)
                    .Select(kv => new MaterialShare { Material = kv.Key, Share = Math.Round(kv.Value / totalArea, 3) }).ToList();
            }
        }

        public static string PerformanceCost(int parts, long triangles) =>
            parts > 600 || triangles > 60_000 ? "high" : parts > 150 || triangles > 15_000 ? "medium" : "low";

        public static IEnumerable<RbxVector3> Corners(RbxCFrame cf, RbxVector3 size)
        {
            var h = size * 0.5f;
            for (int i = 0; i < 8; i++)
                yield return cf.PointToWorldSpace(new RbxVector3((i & 1) == 0 ? -h.X : h.X, (i & 2) == 0 ? -h.Y : h.Y, (i & 4) == 0 ? -h.Z : h.Z));
        }

        private static float Round(float v) => MathF.Round(v, 2);
    }
}
