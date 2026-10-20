using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox.Assets;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Rbx;

namespace Omnipotent.Services.OmniBlox.Build
{
    /// <summary>How to stand a library asset on the ground: its bottom-centre and which way its front faces.</summary>
    public sealed record AssetPlacement(RbxVector3 Pivot, float FrontYaw, RbxVector3 Size, string Name);

    /// <summary>A solid in the compiled world, in world space — what the verifier and the scene renderer look at.</summary>
    public sealed record WorldPart(string ObjectId, string Name, string Kind, RbxCFrame CFrame, RbxVector3 Size, string Shape,
        RbxColor3uint8 Color, float Transparency, bool IsMesh, bool CanCollide, IReadOnlyList<string> Tags, long? AssetId);

    public sealed class CompileInput
    {
        public required string GameId { get; init; }
        public required GameWorkspace Workspace { get; init; }
        public Func<long, RbxInstance?> LoadAsset { get; init; } = _ => null;
        public Func<long, AssetPlacement?> AssetInfo { get; init; } = _ => null;
        public List<ProductRecord> Products { get; init; } = new();
        public string? TelemetryUrl { get; init; }
        public string? TelemetrySecret { get; init; }
    }

    public sealed class CompileResult
    {
        public RbxDocument Document { get; init; } = new();
        public byte[] PlaceXml { get; set; } = Array.Empty<byte>();
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
        public List<WorldPart> Parts { get; } = new();
        public Dictionary<long, int> AssetsUsed { get; } = new();
        public int InstanceCount { get; set; }
        public int ScriptCount { get; set; }
        public bool Ok => Errors.Count == 0;
    }

    /// <summary>
    /// Turns a game's workspace into a Roblox place (.rbxlx): services, the world from scene.json (parts,
    /// spawns, lights, signs and cleaned library assets placed by their measured pivot and labelled front),
    /// the Game Kit and the game's own Luau, plus generated Config modules. Every script is syntax-checked
    /// first; any error fails the build with file:line:column so the builder can fix it.
    /// </summary>
    public static class PlaceCompiler
    {
        public static CompileResult Compile(CompileInput input)
        {
            var result = new CompileResult();
            var manifest = input.Workspace.LoadManifest();
            SceneDocument scene;
            try { scene = input.Workspace.LoadScene(); }
            catch (Exception ex) { result.Errors.Add("scene.json is not valid: " + ex.Message); return result; }
            var scripts = input.Workspace.Scripts();

            // 1. Syntax: the game's scripts and the kit.
            foreach (var s in scripts)
            {
                foreach (var issue in LuauSyntax.Check(s.Source))
                    (issue.IsError ? result.Errors : result.Warnings).Add($"{s.RelativePath}:{issue.Line}:{issue.Column}: {issue.Message}");
                foreach (var lint in LuauSyntax.Lint(s.Source))
                    result.Warnings.Add($"{s.RelativePath}:{lint.Line}:{lint.Column}: {lint.Message}");
            }
            foreach (var k in GameKit.Files)
                foreach (var issue in LuauSyntax.Check(k.Source).Where(i => i.IsError))
                    result.Errors.Add($"GameKit/{k.Path}:{issue.Line}:{issue.Column}: {issue.Message}");

            string templateModule = GameKit.TemplateModule(manifest.Template);
            if (!GameKit.Templates.ContainsKey(manifest.Template ?? ""))
                result.Warnings.Add($"game.json template '{manifest.Template}' is unknown; using sandbox (no template). Known: {string.Join(", ", GameKit.Templates.Keys)}.");

            // 2. The data model.
            var doc = result.Document;
            var workspace = doc.AddRoot(new RbxInstance("Workspace", "Workspace"));
            workspace.AddChild(new RbxInstance("Terrain", "Terrain"));
            var lighting = doc.AddRoot(new RbxInstance("Lighting", "Lighting"));
            ApplyLighting(lighting, scene.Lighting);
            var replicated = doc.AddRoot(new RbxInstance("ReplicatedStorage", "ReplicatedStorage"));
            var serverScripts = doc.AddRoot(new RbxInstance("ServerScriptService", "ServerScriptService"));
            var serverStorage = doc.AddRoot(new RbxInstance("ServerStorage", "ServerStorage"));
            var starterPlayer = doc.AddRoot(new RbxInstance("StarterPlayer", "StarterPlayer"));
            starterPlayer.Set("CharacterWalkSpeed", RbxValue.Float(manifest.WalkSpeed));
            starterPlayer.Set("CharacterJumpPower", RbxValue.Float(manifest.JumpPower));
            starterPlayer.Set("CharacterUseJumpPower", RbxValue.Bool(true));
            var playerScripts = starterPlayer.AddChild(new RbxInstance("StarterPlayerScripts", "StarterPlayerScripts"));
            var characterScripts = starterPlayer.AddChild(new RbxInstance("StarterCharacterScripts", "StarterCharacterScripts"));
            var starterGui = doc.AddRoot(new RbxInstance("StarterGui", "StarterGui"));
            doc.AddRoot(new RbxInstance("StarterPack", "StarterPack"));
            doc.AddRoot(new RbxInstance("Players", "Players"));
            doc.AddRoot(new RbxInstance("SoundService", "SoundService"));
            doc.AddRoot(new RbxInstance("Teams", "Teams"));
            var http = doc.AddRoot(new RbxInstance("HttpService", "HttpService"));
            http.Set("HttpEnabled", RbxValue.Bool(!string.IsNullOrEmpty(input.TelemetryUrl)));

            // 3. Kit + generated config.
            var kitShared = replicated.AddChild(new RbxInstance("Folder", "OmniKit"));
            var kitServer = serverScripts.AddChild(new RbxInstance("Folder", "OmniKitServer"));
            var kitTemplates = kitServer.AddChild(new RbxInstance("Folder", "Templates"));
            var kitSpecs = kitServer.AddChild(new RbxInstance("Folder", "Specs"));
            foreach (var k in GameKit.Files)
            {
                string stem = StripScriptSuffix(k.FileName, out string cls);
                var parts = k.Path.Split('/');
                RbxInstance parent = parts[0] switch
                {
                    "Shared" => kitShared,
                    "Client" => playerScripts,
                    "Server" when parts.Length > 2 && parts[1] == "Templates" => kitTemplates,
                    "Server" when parts.Length > 2 && parts[1] == "Specs" => kitSpecs,
                    "Server" when cls == "Script" => serverScripts,
                    _ => kitServer,
                };
                string name = parts[0] switch
                {
                    "Client" => "OmniKit" + stem,
                    "Server" when cls == "Script" => "OmniKit" + stem,
                    _ => stem,
                };
                parent.AddChild(Script(cls, name, k.Source));
            }
            var config = BuildConfig(input, manifest, scene, templateModule);
            kitServer.AddChild(Script("ModuleScript", "Config", "-- Generated by OmniBlox; edit game.json instead.\nreturn " + LuauLiteral.Write(config) + "\n"));
            config.Remove("Telemetry");
            kitShared.AddChild(Script("ModuleScript", "PublicConfig", "-- Generated by OmniBlox; safe for clients (no secrets).\nreturn " + LuauLiteral.Write(config) + "\n"));
            serverStorage.AddChild(new RbxInstance("Folder", "OmniItems"));

            // 4. The game's own scripts.
            var sharedFolder = replicated.AddChild(new RbxInstance("Folder", "Shared"));
            var testsFolder = serverStorage.AddChild(new RbxInstance("Folder", "OmniTests"));
            foreach (var s in scripts)
            {
                RbxInstance? container = s.Container switch
                {
                    "server" => serverScripts,
                    "client" => playerScripts,
                    "shared" => sharedFolder,
                    "character" => characterScripts,
                    "gui" => starterGui,
                    "tests" => testsFolder,
                    _ => null,
                };
                if (container == null)
                {
                    result.Warnings.Add($"{s.RelativePath}: unknown folder '{s.Container}' (use server, client, shared, character, gui or tests); skipped.");
                    continue;
                }
                var parent = EnsureFolders(container, s.FolderPath);
                var existing = parent.Children.FirstOrDefault(c => c.Name == s.Name && c.ClassName == "Folder");
                var script = Script(s.ClassName, s.Name, s.Source);
                if (existing != null)
                {
                    // init.luau: the folder becomes the script; its other files become the script's children.
                    foreach (var child in existing.Children.ToList()) script.AddChild(child);
                    existing.RemoveFromParent();
                }
                parent.AddChild(script);
            }

            // 5. The world.
            var folders = new Dictionary<string, RbxInstance>(StringComparer.OrdinalIgnoreCase);
            RbxInstance FolderFor(string path)
            {
                if (string.IsNullOrWhiteSpace(path)) return workspace;
                string key = path.Trim().Trim('/');
                if (folders.TryGetValue(key, out var found)) return found;
                var folder = EnsureFolders(workspace, key.Split('/', StringSplitOptions.RemoveEmptyEntries), "Model");
                folders[key] = folder;
                return folder;
            }

            bool hasSpawn = false;
            foreach (var obj in scene.Objects)
            {
                try
                {
                    var built = BuildObject(obj, input, result);
                    if (built == null) continue;
                    if (obj.Kind == SceneKinds.Spawn) hasSpawn = true;
                    FolderFor(obj.Folder).AddChild(built);
                }
                catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
                {
                    result.Errors.Add($"scene object '{obj.Id}' ({obj.Name}): {ex.Message}");
                }
            }
            if (!hasSpawn)
            {
                result.Warnings.Add("scene.json has no spawn; a default SpawnLocation was added at the origin.");
                var spawn = new SceneObject { Id = "default-spawn", Kind = SceneKinds.Spawn, Name = "Spawn", Position = new float[] { 0, 0.5f, 0 } };
                workspace.AddChild(BuildObject(spawn, input, result)!);
            }

            result.InstanceCount = doc.AllInstances().Count();
            result.ScriptCount = doc.AllInstances().Count(i => i.ClassName is "Script" or "LocalScript" or "ModuleScript");
            if (result.Ok) result.PlaceXml = RbxXmlWriter.WriteBytes(doc);
            return result;
        }

        private static Dictionary<string, object?> BuildConfig(CompileInput input, GameManifest manifest, SceneDocument scene, string templateModule)
        {
            var template = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (k, v) in manifest.TemplateSettings) template[k] = v is JToken j ? j : v;
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["GameId"] = input.GameId,
                ["Title"] = manifest.Title,
                ["TemplateName"] = templateModule,
                ["CurrencyName"] = string.IsNullOrWhiteSpace(manifest.CurrencyName) ? "Coins" : manifest.CurrencyName,
                ["StartingCurrency"] = manifest.StartingCurrency,
                ["WalkSpeed"] = manifest.WalkSpeed,
                ["DailyRewards"] = manifest.DailyRewards,
                ["Leaderboard"] = manifest.Leaderboard,
                ["Feedback"] = manifest.Feedback,
                ["MusicId"] = manifest.MusicId ?? "",
                ["Tips"] = manifest.Tips,
                ["Template"] = template,
                ["Terrain"] = scene.Terrain.Select(t => new Dictionary<string, object?>
                {
                    ["Shape"] = t.Shape,
                    ["Material"] = t.Material,
                    ["Center"] = t.Center,
                    ["Size"] = t.Size,
                }).ToList(),
                ["Products"] = input.Products.Select(p => new Dictionary<string, object?>
                {
                    ["Key"] = p.Key,
                    ["Kind"] = p.Kind.ToString(),
                    ["Id"] = p.RobloxId ?? 0,
                    ["Name"] = p.Name,
                    ["Description"] = p.Description,
                    ["Price"] = p.PriceRobux,
                    ["Grant"] = p.Grant,
                }).ToList(),
                ["Telemetry"] = new Dictionary<string, object?>
                {
                    ["Enabled"] = !string.IsNullOrEmpty(input.TelemetryUrl),
                    ["Url"] = input.TelemetryUrl ?? "",
                    ["Secret"] = input.TelemetrySecret ?? "",
                },
            };
        }

        // ─────────────── objects ───────────────

        private static RbxInstance? BuildObject(SceneObject obj, CompileInput input, CompileResult result)
        {
            string name = string.IsNullOrWhiteSpace(obj.Name) ? obj.Id : obj.Name;
            RbxInstance inst;
            switch (obj.Kind)
            {
                case SceneKinds.Asset:
                    return BuildAsset(obj, name, input, result);
                case SceneKinds.Spawn:
                    {
                        inst = new RbxInstance("SpawnLocation", name);
                        var size = obj.SizeVector ?? new RbxVector3(6, 1, 6);
                        SetPartBasics(inst, obj, size, obj.Color ?? "#4CAF50", obj.Material ?? "SmoothPlastic");
                        inst.Set("Neutral", RbxValue.Bool(true));
                        inst.Set("Duration", RbxValue.Int(0));
                        inst.Set("Enabled", RbxValue.Bool(true));
                        break;
                    }
                case SceneKinds.Light:
                    {
                        inst = new RbxInstance("Part", name);
                        SetPartBasics(inst, obj, obj.SizeVector ?? new RbxVector3(1, 1, 1), obj.Color ?? "#FFFFFF", obj.Material ?? "Neon");
                        inst.Set("Transparency", RbxValue.Float(obj.Transparency ?? 1f));
                        inst.Set("CanCollide", RbxValue.Bool(false));
                        string lightClass = (obj.LightType ?? "point").ToLowerInvariant() switch { "spot" => "SpotLight", "surface" => "SurfaceLight", _ => "PointLight" };
                        var light = inst.AddChild(new RbxInstance(lightClass, "Light"));
                        light.Set("Range", RbxValue.Float(Math.Clamp(obj.Range ?? 16f, 0, 60)));
                        light.Set("Brightness", RbxValue.Float(Math.Clamp(obj.Brightness ?? 2f, 0, 40)));
                        light.Set("Color", RbxValue.Color(ParseColor(obj.Color ?? "#FFF4D6").ToColor3()));
                        light.Set("Shadows", RbxValue.Bool(true));
                        break;
                    }
                case SceneKinds.Text:
                    {
                        inst = new RbxInstance("Part", name);
                        SetPartBasics(inst, obj, obj.SizeVector ?? new RbxVector3(10, 4, 0.5f), obj.Color ?? "#1E2430", obj.Material ?? "SmoothPlastic");
                        var gui = inst.AddChild(new RbxInstance("SurfaceGui", "Sign"));
                        gui.Set("Face", RbxValue.Token(5)); // Front
                        var label = gui.AddChild(new RbxInstance("TextLabel", "Text"));
                        label.Set("Size", RbxValue.UDim2(new RbxUDim2(new RbxUDim(1, 0), new RbxUDim(1, 0))));
                        label.Set("BackgroundTransparency", RbxValue.Float(1));
                        label.Set("Text", RbxValue.Str(obj.Text ?? name));
                        label.Set("TextScaled", RbxValue.Bool(true));
                        label.Set("TextColor3", RbxValue.Color(new RbxColor3(1, 1, 1)));
                        label.Set("FontFace", new RbxValue(RbxType.Font, new RbxFont("rbxasset://fonts/families/GothamSSm.json", 700, "Normal", "")));
                        break;
                    }
                case SceneKinds.Part:
                default:
                    {
                        string shape = (obj.Shape ?? "Block").Trim();
                        string cls = shape.ToLowerInvariant() switch
                        {
                            "wedge" => "WedgePart",
                            "cornerwedge" => "CornerWedgePart",
                            "truss" => "TrussPart",
                            _ => "Part",
                        };
                        inst = new RbxInstance(cls, name);
                        var size = obj.SizeVector ?? new RbxVector3(4, 1, 2);
                        if (size.X <= 0 || size.Y <= 0 || size.Z <= 0 || size.X > 2048 || size.Y > 2048 || size.Z > 2048)
                            throw new ArgumentException($"size {size} must be between 0 and 2048 studs on every axis");
                        SetPartBasics(inst, obj, size, obj.Color ?? "#A3A2A5", obj.Material ?? "SmoothPlastic");
                        if (cls == "Part")
                            inst.Set("shape", RbxValue.Token(shape.ToLowerInvariant() switch { "ball" => RbxPartShapes.Ball, "cylinder" => RbxPartShapes.Cylinder, _ => RbxPartShapes.Block }));
                        break;
                    }
            }
            AddTagsAndConfig(inst, obj);
            result.Parts.Add(ToWorldPart(obj, inst, null));
            return inst;
        }

        private static void SetPartBasics(RbxInstance inst, SceneObject obj, RbxVector3 size, string color, string material)
        {
            inst.Set("size", RbxValue.Vec3(size));
            inst.Set("CFrame", RbxValue.Frame(obj.RotationFrame(obj.Pos)));
            inst.Set("Color3uint8", RbxValue.Color8(ParseColor(color)));
            inst.Set("Material", RbxValue.Token(RbxMaterials.Parse(material) ?? RbxMaterials.SmoothPlastic));
            inst.Set("Transparency", RbxValue.Float(Math.Clamp(obj.Transparency ?? 0f, 0f, 1f)));
            inst.Set("Anchored", RbxValue.Bool(obj.Anchored));
            inst.Set("CanCollide", RbxValue.Bool(obj.CanCollide));
            inst.Set("CastShadow", RbxValue.Bool(obj.CastShadow ?? true));
            inst.Set("TopSurface", RbxValue.Token(RbxSurfaces.Smooth));
            inst.Set("BottomSurface", RbxValue.Token(RbxSurfaces.Smooth));
        }

        private static RbxInstance? BuildAsset(SceneObject obj, string name, CompileInput input, CompileResult result)
        {
            if (obj.AssetId is not long assetId) throw new ArgumentException("an asset object needs assetId");
            var placement = input.AssetInfo(assetId) ?? throw new InvalidOperationException($"asset {assetId} is not in the library (or not measured yet)");
            var model = input.LoadAsset(assetId) ?? throw new InvalidOperationException($"asset {assetId} has no cleaned model file");
            float scale = obj.Scale <= 0 ? 1 : Math.Clamp(obj.Scale, 0.05f, 20f);
            float yaw = (obj.Yaw + placement.FrontYaw) * MathF.PI / 180f;
            var rotation = RbxCFrame.FromYaw(RbxVector3.Zero, yaw);
            var target = obj.Pos;

            RbxCFrame Transform(RbxCFrame cf)
            {
                var local = (cf.Position - placement.Pivot) * scale;
                var rotated = rotation * cf.WithPosition(RbxVector3.Zero);
                return rotated.WithPosition(target + rotation.RotateVector(local));
            }

            foreach (var inst in model.SelfAndDescendants())
            {
                bool isPart = AssetAnalyzer.PartClasses.Contains(inst.ClassName);
                if (isPart && inst.CFrame is { } cf)
                {
                    inst.Set("CFrame", RbxValue.Frame(Transform(cf)));
                    if (inst.Size is { } size) inst.Set(inst.Properties.ContainsKey("size") ? "size" : "Size", RbxValue.Vec3(size * scale));
                }
                if (inst.Get("WorldPivotData")?.Value is RbxCFrame pivot)
                    inst.Set("WorldPivotData", new RbxValue(RbxType.OptionalCFrame, (RbxCFrame?)Transform(pivot)));
                if (scale != 1)
                {
                    // Local offsets inside the model scale with it; their rotations do not change.
                    foreach (string local in new[] { "C0", "C1" })
                        if (inst.Get(local)?.Value is RbxCFrame c) inst.Set(local, RbxValue.Frame(c.WithPosition(c.Position * scale)));
                    if (inst.ClassName == "Attachment" && inst.CFrame is { } a) inst.Set("CFrame", RbxValue.Frame(a.WithPosition(a.Position * scale)));
                    if (inst.ClassName is "SpecialMesh" or "BlockMesh" or "CylinderMesh")
                    {
                        if (inst.GetAs<RbxVector3>("Scale") is { } s) inst.Set("Scale", RbxValue.Vec3(s * scale));
                        if (inst.GetAs<RbxVector3>("Offset") is { } o) inst.Set("Offset", RbxValue.Vec3(o * scale));
                    }
                    if (inst.ClassName is "PointLight" or "SpotLight" or "SurfaceLight" && inst.GetAs<float>("Range") is { } range)
                        inst.Set("Range", RbxValue.Float(Math.Min(60, range * scale)));
                }
            }
            model.ClassName = "Model";
            model.Name = name;
            AddTagsAndConfig(model, obj);
            result.AssetsUsed[assetId] = result.AssetsUsed.GetValueOrDefault(assetId) + 1;

            foreach (var part in model.SelfAndDescendants().Where(i => AssetAnalyzer.PartClasses.Contains(i.ClassName) && i.Size != null && i.CFrame != null))
                result.Parts.Add(ToWorldPart(obj, part, assetId));
            return model;
        }

        private static WorldPart ToWorldPart(SceneObject obj, RbxInstance part, long? assetId)
        {
            string shape = part.ClassName switch
            {
                "WedgePart" => "Wedge",
                "CornerWedgePart" => "CornerWedge",
                _ => RbxPartShapes.Name(part.GetAs<uint>("shape") ?? RbxPartShapes.Block),
            };
            return new WorldPart(obj.Id, part.Name, obj.Kind, part.CFrame!.Value, part.Size!.Value, shape,
                part.Color ?? new RbxColor3uint8(163, 162, 165), part.Transparency,
                part.ClassName is "MeshPart" or "UnionOperation" || part.FindFirstChildOfClass("SpecialMesh") != null,
                part.GetBool("CanCollide") ?? true, obj.Tags, assetId);
        }

        private static void AddTagsAndConfig(RbxInstance inst, SceneObject obj)
        {
            var tags = obj.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct().ToList();
            if (tags.Count > 0) inst.Set("Tags", new RbxValue(RbxType.BinaryString, Encoding.UTF8.GetBytes(string.Join("\0", tags))));
            foreach (var (key, raw) in obj.Config)
            {
                if (string.IsNullOrWhiteSpace(key)) continue;
                object? value = raw is JValue jv ? jv.Value : raw;
                RbxInstance holder;
                switch (value)
                {
                    case bool b:
                        holder = new RbxInstance("BoolValue", key);
                        holder.Set("Value", RbxValue.Bool(b));
                        break;
                    case long or int or double or float or decimal:
                        holder = new RbxInstance("NumberValue", key);
                        holder.Set("Value", RbxValue.Double(Convert.ToDouble(value, CultureInfo.InvariantCulture)));
                        break;
                    case JArray arr when arr.Count == 3 && arr.All(x => x.Type is JTokenType.Integer or JTokenType.Float):
                        holder = new RbxInstance("Vector3Value", key);
                        holder.Set("Value", RbxValue.Vec3(new RbxVector3((float)arr[0], (float)arr[1], (float)arr[2])));
                        break;
                    default:
                        holder = new RbxInstance("StringValue", key);
                        holder.Set("Value", RbxValue.Str(Convert.ToString(value is JToken t ? t.ToString(Newtonsoft.Json.Formatting.None) : value, CultureInfo.InvariantCulture) ?? ""));
                        break;
                }
                inst.AddChild(holder);
            }
        }

        // ─────────────── helpers ───────────────

        private static RbxInstance Script(string className, string name, string source)
        {
            var s = new RbxInstance(className, name);
            s.Set("Source", RbxValue.Source(source));
            return s;
        }

        private static string StripScriptSuffix(string fileName, out string className)
        {
            string stem = fileName.EndsWith(".luau", StringComparison.OrdinalIgnoreCase) ? fileName[..^5] : fileName;
            className = "ModuleScript";
            if (stem.EndsWith(".server", StringComparison.OrdinalIgnoreCase)) { className = "Script"; return stem[..^7]; }
            if (stem.EndsWith(".client", StringComparison.OrdinalIgnoreCase)) { className = "LocalScript"; return stem[..^7]; }
            return stem;
        }

        private static RbxInstance EnsureFolders(RbxInstance root, IEnumerable<string> path, string folderClass = "Folder")
        {
            var at = root;
            foreach (string segment in path)
            {
                var next = at.Children.FirstOrDefault(c => c.Name == segment && c.ClassName is "Folder" or "Model");
                if (next == null)
                {
                    next = at.AddChild(new RbxInstance(folderClass, segment));
                }
                at = next;
            }
            return at;
        }

        private static void ApplyLighting(RbxInstance lighting, LightingSpec spec)
        {
            (float time, float brightness, RbxColor3 ambient, RbxColor3 outdoor) = (spec.Preset ?? "day").ToLowerInvariant() switch
            {
                "sunset" => (17.8f, 1.6f, C(90, 70, 60), C(160, 115, 95)),
                "night" => (0.5f, 0.6f, C(40, 42, 64), C(60, 64, 96)),
                "overcast" => (12f, 1.1f, C(105, 105, 112), C(150, 150, 158)),
                _ => (14f, 2.2f, C(80, 80, 85), C(135, 135, 140)),
            };
            if (spec.ClockTime is float ct) time = Math.Clamp(ct, 0, 24);
            if (spec.Brightness is float b) brightness = Math.Clamp(b, 0, 10);
            if (spec.Ambient != null) ambient = ParseColor(spec.Ambient).ToColor3();
            int hours = (int)time, minutes = (int)((time - hours) * 60);
            lighting.Set("TimeOfDay", RbxValue.Str($"{hours:D2}:{minutes:D2}:00"));
            lighting.Set("Brightness", RbxValue.Float(brightness));
            lighting.Set("Ambient", RbxValue.Color(ambient));
            lighting.Set("OutdoorAmbient", RbxValue.Color(outdoor));
            lighting.Set("GlobalShadows", RbxValue.Bool(spec.Shadows));
            lighting.Set("Technology", RbxValue.Token(3)); // ShadowMap: good looks, fine on phones
        }

        private static RbxColor3 C(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f);

        private static readonly Dictionary<string, string> NamedColors = new(StringComparer.OrdinalIgnoreCase)
        {
            ["white"] = "#F2F3F3", ["black"] = "#1B1B1B", ["grey"] = "#A3A2A5", ["gray"] = "#A3A2A5", ["dark grey"] = "#636162",
            ["red"] = "#C4281C", ["dark red"] = "#7B2E2F", ["orange"] = "#DA8541", ["yellow"] = "#F5CD30", ["gold"] = "#EFB838",
            ["green"] = "#4B974B", ["dark green"] = "#287F47", ["lime"] = "#A4BD47", ["blue"] = "#0D69AC", ["light blue"] = "#80BBDB",
            ["navy"] = "#002060", ["cyan"] = "#04AFEC", ["teal"] = "#008F9C", ["purple"] = "#6B327C", ["pink"] = "#FF66CC",
            ["brown"] = "#7C5C46", ["tan"] = "#D7C59A", ["sand"] = "#E5D3A4", ["wood"] = "#A0763F", ["stone"] = "#8A8A8A",
        };

        public static RbxColor3uint8 ParseColor(string text)
        {
            string s = (text ?? "").Trim();
            if (NamedColors.TryGetValue(s, out var named)) s = named;
            if (s.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
            {
                var nums = s.Trim('r', 'g', 'b', 'R', 'G', 'B', '(', ')', ' ').Split(',').Select(x => int.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToArray();
                if (nums.Length == 3) return new RbxColor3uint8((byte)Math.Clamp(nums[0], 0, 255), (byte)Math.Clamp(nums[1], 0, 255), (byte)Math.Clamp(nums[2], 0, 255));
            }
            s = s.TrimStart('#');
            if (s.Length == 6 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex))
                return RbxColor3uint8.FromPacked(hex);
            throw new FormatException($"'{text}' is not a colour (use #RRGGBB, rgb(r,g,b) or a basic colour name)");
        }
    }
}
