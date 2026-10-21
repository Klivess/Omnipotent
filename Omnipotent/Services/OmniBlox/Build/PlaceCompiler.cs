using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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
        RbxColor3uint8 Color, float Transparency, bool IsMesh, bool CanCollide, IReadOnlyList<string> Tags, long? AssetId,
        string Material = "SmoothPlastic", bool Neon = false);

    public sealed class CompileInput
    {
        public required string GameId { get; init; }
        public required GameWorkspace Workspace { get; init; }
        public Func<long, RbxInstance?> LoadAsset { get; init; } = _ => null;
        public Func<long, AssetPlacement?> AssetInfo { get; init; } = _ => null;
        public List<ProductRecord> Products { get; init; } = new();
        public string? TelemetryUrl { get; init; }
        public string? TelemetrySecret { get; init; }
        /// <summary>Set for the owner's playtest builds; null for the real game.</summary>
        public PlaytestOptions? Playtest { get; init; }
        /// <summary>
        /// The engine-test build: Config.Testing is on (memory saves, no telemetry), and every server Script
        /// becomes a ModuleScript returning its code as a function (tagged OmniTestScript), because Roblox's Luau
        /// execution runs none of a place's scripts on its own. The TestRunner starts them itself.
        /// </summary>
        public bool EngineTest { get; init; }
    }

    /// <summary>
    /// What makes a build a playtest (Config.Playtest in the Game Kit): purchases are simulated, saves go to
    /// <paramref name="DataStore"/> (or stay in memory, for Studio, where a place file has no DataStores), and
    /// the HUD says which build it is.
    /// </summary>
    public sealed record PlaytestOptions(string Build, string Where, string DataStore, bool MemoryData);

    /// <summary>Where a script ended up in the place: its path in the data model and where its source came from.</summary>
    public sealed record ScriptPlacement(IReadOnlyList<string> DataModelPath, string ClassName, string Source, string? WorkspaceFile, string? KitFile)
    {
        public string FullName => string.Join(".", DataModelPath);
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
        /// <summary>Every script (the kit's, the generated config, the game's) and where it lives.</summary>
        public List<ScriptPlacement> Scripts { get; } = new();
        /// <summary>The baked terrain (null when the scene has no generated terrain).</summary>
        public TerrainMap? Terrain { get; set; }
        public bool Ok => Errors.Count == 0;

        /// <summary>The workspace file for a script's full name (ServerScriptService.Systems.Forest → src/server/systems/Forest.luau).</summary>
        public string? FileFor(string fullName) =>
            Scripts.FirstOrDefault(s => s.FullName.Equals(fullName, StringComparison.Ordinal))?.WorkspaceFile;
    }

    /// <summary>
    /// Turns a game's workspace into a Roblox place (.rbxlx): services, the world from scene.json (parts,
    /// spawns, lights, signs, cleaned library assets placed by their measured pivot and labelled front, tagged
    /// folders), generated terrain, lighting with atmosphere, the Game Kit and the game's own Luau, plus generated
    /// Config modules. Every script is syntax-checked first; any error fails the build with file:line:column so
    /// the builder can fix it.
    /// </summary>
    public static class PlaceCompiler
    {
        /// <summary>Folder names with a meaning to the kit, written with the capital the kit looks for.</summary>
        private static readonly Dictionary<string, string> KnownFolders = new(StringComparer.OrdinalIgnoreCase)
        {
            ["systems"] = "Systems",
            ["controllers"] = "Controllers",
        };

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
                if (s.Container == "client" && s.ClassName == "ModuleScript" && s.FolderPath.Length == 0)
                    result.Warnings.Add($"{s.RelativePath}: a module directly in src/client never runs unless something requires it; use .client.luau for a LocalScript, or put it in src/client/controllers/ with a Start(hud) function.");
                if (s.Container == "server" && s.ClassName == "ModuleScript" && (s.FolderPath.Length == 0 || !KnownFolders.ContainsKey(s.FolderPath[0])))
                    result.Warnings.Add($"{s.RelativePath}: a module in src/server only runs when something requires it; put game systems in src/server/systems/ with a Start() function so the kit starts them (and the engine tests can).");
            }
            foreach (var k in GameKit.Files)
                foreach (var issue in LuauSyntax.Check(k.Source).Where(i => i.IsError))
                    result.Errors.Add($"GameKit/{k.Path}:{issue.Line}:{issue.Column}: {issue.Message}");

            string templateModule = GameKit.TemplateModule(manifest.Template);
            if (!GameKit.Templates.ContainsKey(manifest.Template ?? ""))
                result.Warnings.Add($"game.json template '{manifest.Template}' is unknown; using sandbox (no template). Known: {string.Join(", ", GameKit.Templates.Keys)}.");

            if (scene.Land != null)
            {
                try { result.Terrain = TerrainBuilder.Build(scene.Land); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { result.Errors.Add("the terrain could not be built: " + ex.Message); }
            }

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
            http.Set("HttpEnabled", RbxValue.Bool(!string.IsNullOrEmpty(input.TelemetryUrl) && !input.EngineTest));

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
                var placed = parent.AddChild(ScriptFor(cls, name, k.Source, input.EngineTest && parent == serverScripts));
                result.Scripts.Add(new ScriptPlacement(PathOf(placed), cls, k.Source, null, k.Path));
            }
            var config = BuildConfig(input, manifest, scene, templateModule, result.Terrain);
            string serverConfig = ConfigModule(config, "Generated by OmniBlox; edit game.json instead.");
            var serverConfigInstance = kitServer.AddChild(Script("ModuleScript", "Config", serverConfig));
            result.Scripts.Add(new ScriptPlacement(PathOf(serverConfigInstance), "ModuleScript", serverConfig, null, "(generated) Config"));
            config.Remove("Telemetry");
            config.Remove("Terrain"); // clients never build terrain, and the heightmap is large
            string publicConfig = ConfigModule(config, "Generated by OmniBlox; safe for clients (no secrets).");
            var publicConfigInstance = kitShared.AddChild(Script("ModuleScript", "PublicConfig", publicConfig));
            result.Scripts.Add(new ScriptPlacement(PathOf(publicConfigInstance), "ModuleScript", publicConfig, null, "(generated) PublicConfig"));
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
                var folderPath = s.FolderPath.Select((f, i) => i == 0 && KnownFolders.TryGetValue(f, out var known) ? known : f).ToArray();
                var parent = EnsureFolders(container, folderPath);
                var existing = parent.Children.FirstOrDefault(c => c.Name == s.Name && c.ClassName == "Folder");
                bool wrap = input.EngineTest && s.Container == "server" && s.ClassName == "Script";
                var script = ScriptFor(s.ClassName, s.Name, s.Source, wrap);
                if (existing != null)
                {
                    // init.luau: the folder becomes the script; its other files become the script's children.
                    foreach (var child in existing.Children.ToList()) script.AddChild(child);
                    existing.RemoveFromParent();
                }
                parent.AddChild(script);
                result.Scripts.Add(new ScriptPlacement(PathOf(script), s.ClassName, s.Source, s.RelativePath, null));
            }

            // 5. The world.
            var folders = new Dictionary<string, RbxInstance>(StringComparer.OrdinalIgnoreCase);
            RbxInstance FolderFor(string path)
            {
                if (string.IsNullOrWhiteSpace(path)) return workspace;
                string key = path.Trim().Trim('/');
                if (folders.TryGetValue(key, out var found)) return found;
                // Each level is a Model unless the scene says otherwise; a spec can make it a Folder and tag it.
                var segments = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var at = workspace;
                for (int i = 0; i < segments.Length; i++)
                {
                    string sub = string.Join("/", segments.Take(i + 1));
                    if (folders.TryGetValue(sub, out var known)) { at = known; continue; }
                    scene.Folders.TryGetValue(sub, out var spec);
                    string cls = spec?.Class?.Equals("Folder", StringComparison.OrdinalIgnoreCase) == true ? "Folder" : "Model";
                    var next = at.Children.FirstOrDefault(c => c.Name == segments[i] && c.ClassName is "Folder" or "Model") ?? at.AddChild(new RbxInstance(cls, segments[i]));
                    if (spec != null) AddTagsAndConfig(next, spec.Tags, spec.Config);
                    folders[sub] = next;
                    at = next;
                }
                return at;
            }
            foreach (var (path, spec) in scene.Folders)
                if (spec.Tags.Count > 0 || spec.Config.Count > 0) FolderFor(path);

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
                float ground = result.Terrain?.HeightAt(0, 0) ?? 0;
                var spawn = new SceneObject { Id = "default-spawn", Kind = SceneKinds.Spawn, Name = "Spawn", Position = new float[] { 0, ground + 0.5f, 0 } };
                workspace.AddChild(BuildObject(spawn, input, result)!);
            }

            result.InstanceCount = doc.AllInstances().Count();
            result.ScriptCount = doc.AllInstances().Count(i => i.ClassName is "Script" or "LocalScript" or "ModuleScript");
            if (result.Ok) result.PlaceXml = RbxXmlWriter.WriteBytes(doc);
            return result;
        }

        private static List<string> PathOf(RbxInstance instance)
        {
            var parts = new List<string>();
            for (var at = instance; at != null; at = at.Parent) parts.Add(at.Name);
            parts.Reverse();
            return parts;
        }

        /// <summary>The generated config as a module the type checker reads as a string-keyed table of anything.</summary>
        private static string ConfigModule(Dictionary<string, object?> config, string header) =>
            "-- " + header + "\nlocal config: { [string]: any } = " + LuauLiteral.Write(config) + "\nreturn config\n";

        private static Dictionary<string, object?> BuildConfig(CompileInput input, GameManifest manifest, SceneDocument scene, string templateModule, TerrainMap? terrain)
        {
            var template = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (k, v) in manifest.TemplateSettings) template[k] = v is JToken j ? j : v;
            var terrainConfig = scene.Terrain.Select(t => (object?)new Dictionary<string, object?>
            {
                ["Shape"] = t.Shape,
                ["Material"] = t.Material,
                ["Center"] = t.Center,
                ["Size"] = t.Size,
            }).ToList();
            if (terrain != null) terrainConfig.Insert(0, terrain.ToConfig());
            var config = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["GameId"] = input.GameId,
                ["Title"] = manifest.Title,
                ["TemplateName"] = templateModule,
                ["CurrencyName"] = string.IsNullOrWhiteSpace(manifest.CurrencyName) ? "Coins" : manifest.CurrencyName,
                ["CurrencyIcon"] = string.IsNullOrWhiteSpace(manifest.CurrencyIcon) ? "🪙" : manifest.CurrencyIcon,
                ["StartingCurrency"] = manifest.StartingCurrency,
                ["Currencies"] = manifest.Currencies.Where(c => !string.IsNullOrWhiteSpace(c.Name)).Select(c => (object?)new Dictionary<string, object?>
                {
                    ["Name"] = c.Name, ["Icon"] = c.Icon, ["Start"] = c.Start, ["Leaderstat"] = c.Leaderstat,
                }).ToList(),
                ["WalkSpeed"] = manifest.WalkSpeed,
                ["DailyRewards"] = manifest.DailyRewards,
                ["DailyReward"] = manifest.DailyReward,
                ["Leaderboard"] = manifest.Leaderboard,
                ["Feedback"] = manifest.Feedback,
                ["MusicId"] = manifest.MusicId ?? "",
                ["Tips"] = manifest.Tips,
                ["Theme"] = manifest.Theme.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                ["Sounds"] = manifest.Sounds.ToDictionary(kv => kv.Key, kv => kv.Value is JToken t ? (object?)t : kv.Value),
                ["Quests"] = manifest.Quests.Where(q => !string.IsNullOrWhiteSpace(q.Event)).Select((q, i) => (object?)new Dictionary<string, object?>
                {
                    ["Id"] = string.IsNullOrWhiteSpace(q.Id) ? "q" + (i + 1) : q.Id,
                    ["Text"] = q.Text,
                    ["Event"] = q.Event,
                    ["Count"] = Math.Max(1, q.Count),
                    ["Reward"] = q.Reward,
                    ["RewardCurrency"] = q.RewardCurrency,
                    ["Target"] = q.Target,
                }).ToList(),
                ["Rebirth"] = manifest.Rebirth is { } r ? new Dictionary<string, object?>
                {
                    ["Enabled"] = r.Enabled, ["Cost"] = r.Cost, ["Growth"] = r.Growth, ["Bonus"] = r.Bonus, ["KeepUpgrades"] = r.KeepUpgrades,
                } : null,
                ["Template"] = template,
                ["Terrain"] = terrainConfig,
                ["Water"] = terrain?.WaterLevel != null ? new Dictionary<string, object?> { ["Color"] = WaterColor(scene), ["Transparency"] = 0.3 } : null,
                ["Bounds"] = scene.Bounds.HasArea ? new Dictionary<string, object?>
                {
                    ["MinX"] = scene.Bounds.MinX, ["MinZ"] = scene.Bounds.MinZ, ["MaxX"] = scene.Bounds.MaxX, ["MaxZ"] = scene.Bounds.MaxZ,
                    ["FloorY"] = Math.Min(-20, (terrain?.Floor ?? -20) + 4), ["Walls"] = scene.Bounds.Walls,
                } : null,
                ["Products"] = input.Products.Select(p => (object?)new Dictionary<string, object?>
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
                    ["Enabled"] = !string.IsNullOrEmpty(input.TelemetryUrl) && !input.EngineTest,
                    ["Url"] = input.TelemetryUrl ?? "",
                    ["Secret"] = input.TelemetrySecret ?? "",
                },
            };
            if (input.EngineTest) config["Testing"] = true;
            if (input.Playtest is { } playtest)
                config["Playtest"] = new Dictionary<string, object?>
                {
                    ["Build"] = playtest.Build,
                    ["Where"] = playtest.Where,
                    ["DataStore"] = playtest.DataStore,
                    ["MemoryData"] = playtest.MemoryData,
                };
            return config;
        }

        private static string WaterColor(SceneDocument scene) => (scene.Lighting.Preset ?? "").ToLowerInvariant() switch
        {
            "tropical" => "#1EC8D8",
            "spooky" => "#4B3A7A",
            "candy" => "#7FD3FF",
            "snowy" => "#7FB8D8",
            _ => "#2A9FD8",
        };

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
                        var size = obj.SizeVector ?? new RbxVector3(8, 1, 8);
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
                        label.Set("TextStrokeTransparency", RbxValue.Float(0));
                        label.Set("FontFace", new RbxValue(RbxType.Font, new RbxFont("rbxasset://fonts/families/FredokaOne.json", 400, "Normal", "")));
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
            AddTagsAndConfig(inst, obj.Tags, obj.Config);
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
            RbxColor3uint8? tint = obj.Tint is { Length: > 0 } t ? ParseColor(t) : null;

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
                    if (tint is { } tc)
                    {
                        inst.Properties.Remove("BrickColor");
                        inst.Properties.Remove("Color");
                        inst.Set("Color3uint8", RbxValue.Color8(tc));
                        // A texture would hide the new colour; a tinted mesh shows the colour on its shape.
                        if (inst.ClassName == "MeshPart") inst.Set("TextureID", RbxValue.ContentUrl(""));
                    }
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
                if (tint != null && inst.ClassName == "SpecialMesh") inst.Set("TextureId", RbxValue.ContentUrl(""));
            }
            model.ClassName = "Model";
            model.Name = name;
            AddTagsAndConfig(model, obj.Tags, obj.Config);
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
                "TrussPart" => "Truss",
                _ => RbxPartShapes.Name(part.GetAs<uint>("shape") ?? RbxPartShapes.Block),
            };
            uint materialToken = part.GetAs<uint>("Material") ?? RbxMaterials.SmoothPlastic;
            string material = RbxMaterials.Name(materialToken) ?? "SmoothPlastic";
            return new WorldPart(obj.Id, part.Name, obj.Kind, part.CFrame!.Value, part.Size!.Value, shape,
                part.Color ?? new RbxColor3uint8(163, 162, 165), part.Transparency,
                part.ClassName is "MeshPart" or "UnionOperation" || part.FindFirstChildOfClass("SpecialMesh") != null,
                part.GetBool("CanCollide") ?? true, obj.Tags, assetId, material, material.Equals("Neon", StringComparison.OrdinalIgnoreCase));
        }

        private static void AddTagsAndConfig(RbxInstance inst, List<string> tagList, Dictionary<string, object> configValues)
        {
            var tags = tagList.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct().ToList();
            if (tags.Count > 0) inst.Set("Tags", new RbxValue(RbxType.BinaryString, Encoding.UTF8.GetBytes(string.Join("\0", tags))));
            foreach (var (key, raw) in configValues)
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

        private static readonly Regex ExportType = new(@"^(\s*)export\s+type\b", RegexOptions.Multiline | RegexOptions.Compiled);

        /// <summary>
        /// A script as it goes into the place. In the engine-test build a server Script becomes a ModuleScript
        /// returning its code as a function, on the same first line so error line numbers stay the same.
        /// </summary>
        private static RbxInstance ScriptFor(string className, string name, string source, bool wrapForTests)
        {
            if (!wrapForTests || className != "Script") return Script(className, name, source);
            string body = ExportType.Replace(source, "$1type");
            var wrapped = Script("ModuleScript", name, "return function(...) " + body + "\nend\n");
            wrapped.Set("Tags", new RbxValue(RbxType.BinaryString, Encoding.UTF8.GetBytes("OmniTestScript")));
            return wrapped;
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

        // ─────────────── lighting ───────────────

        /// <summary>What a lighting preset looks like: the sky, the light, and the grading that makes a kid-friendly world pop.</summary>
        public sealed record LightingLook(float ClockTime, float Brightness, RbxColor3uint8 Ambient, RbxColor3uint8 Outdoor, float Saturation, float Contrast,
            RbxColor3uint8 Tint, float Haze, float Density, RbxColor3uint8 AtmosphereColor, RbxColor3uint8 AtmosphereDecay, float Bloom, float SunRays,
            RbxColor3uint8 SkyTop, RbxColor3uint8 SkyHorizon);

        private static RbxColor3uint8 C8(int r, int g, int b) => new((byte)r, (byte)g, (byte)b);

        public static readonly IReadOnlyDictionary<string, LightingLook> Looks = new Dictionary<string, LightingLook>(StringComparer.OrdinalIgnoreCase)
        {
            ["sunny"] = new(14.2f, 2.6f, C8(110, 110, 120), C8(150, 150, 160), 0.18f, 0.06f, C8(255, 255, 255), 0.6f, 0.28f, C8(199, 220, 255), C8(106, 112, 125), 0.5f, 0.06f, C8(70, 150, 255), C8(190, 225, 255)),
            ["golden"] = new(17.3f, 2.2f, C8(120, 95, 85), C8(175, 135, 110), 0.22f, 0.08f, C8(255, 238, 220), 1.2f, 0.32f, C8(255, 196, 150), C8(160, 100, 90), 0.7f, 0.12f, C8(90, 120, 210), C8(255, 186, 130)),
            ["night"] = new(0.2f, 1.4f, C8(70, 75, 120), C8(95, 105, 165), 0.2f, 0.05f, C8(220, 225, 255), 0.8f, 0.3f, C8(80, 90, 150), C8(40, 40, 80), 0.9f, 0f, C8(20, 24, 60), C8(60, 70, 130)),
            ["spooky"] = new(23.6f, 1.2f, C8(85, 60, 110), C8(110, 80, 150), 0.12f, 0.1f, C8(225, 200, 255), 2.4f, 0.45f, C8(150, 100, 200), C8(60, 30, 80), 0.9f, 0f, C8(40, 20, 70), C8(120, 70, 150)),
            ["tropical"] = new(13.5f, 2.9f, C8(115, 120, 120), C8(160, 165, 160), 0.28f, 0.08f, C8(255, 252, 240), 0.4f, 0.22f, C8(190, 235, 255), C8(100, 140, 140), 0.55f, 0.08f, C8(40, 160, 255), C8(170, 235, 255)),
            ["snowy"] = new(12.5f, 2.3f, C8(125, 130, 145), C8(170, 175, 195), 0.1f, 0.04f, C8(235, 245, 255), 1.0f, 0.35f, C8(220, 235, 255), C8(120, 130, 150), 0.6f, 0.05f, C8(120, 170, 230), C8(225, 238, 255)),
            ["candy"] = new(14f, 2.5f, C8(130, 110, 130), C8(175, 150, 175), 0.32f, 0.05f, C8(255, 235, 250), 0.8f, 0.25f, C8(255, 200, 235), C8(160, 110, 150), 0.6f, 0.05f, C8(150, 170, 255), C8(255, 210, 240)),
            ["overcast"] = new(12f, 1.6f, C8(110, 110, 118), C8(150, 150, 158), 0.04f, 0.02f, C8(245, 245, 250), 1.5f, 0.4f, C8(200, 205, 215), C8(110, 110, 120), 0.3f, 0f, C8(150, 158, 170), C8(205, 210, 220)),
        };

        public static LightingLook LookFor(LightingSpec spec)
        {
            string preset = (spec.Preset ?? "sunny").Trim().ToLowerInvariant() switch
            {
                "day" or "" => "sunny",
                "sunset" => "golden",
                var p => p,
            };
            var look = Looks.TryGetValue(preset, out var l) ? l : Looks["sunny"];
            if (spec.ClockTime is float ct) look = look with { ClockTime = Math.Clamp(ct, 0, 24) };
            if (spec.Brightness is float b) look = look with { Brightness = Math.Clamp(b, 0, 10) };
            if (spec.Ambient != null) look = look with { Ambient = ParseColor(spec.Ambient) };
            if (spec.Saturation is float s) look = look with { Saturation = Math.Clamp(s, -1, 1) };
            if (spec.Haze is float h) look = look with { Density = Math.Clamp(h, 0, 1) };
            if (spec.Tint is { Length: > 0 } tint) look = look with { Tint = ParseColor(tint) };
            return look;
        }

        private static void ApplyLighting(RbxInstance lighting, LightingSpec spec)
        {
            var look = LookFor(spec);
            int hours = (int)look.ClockTime, minutes = (int)((look.ClockTime - hours) * 60);
            lighting.Set("TimeOfDay", RbxValue.Str($"{hours:D2}:{minutes:D2}:00"));
            lighting.Set("Brightness", RbxValue.Float(look.Brightness));
            lighting.Set("Ambient", RbxValue.Color(look.Ambient.ToColor3()));
            lighting.Set("OutdoorAmbient", RbxValue.Color(look.Outdoor.ToColor3()));
            lighting.Set("GlobalShadows", RbxValue.Bool(spec.Shadows));
            lighting.Set("EnvironmentDiffuseScale", RbxValue.Float(0.6f));
            lighting.Set("EnvironmentSpecularScale", RbxValue.Float(0.6f));
            lighting.Set("ShadowSoftness", RbxValue.Float(0.3f));
            lighting.Set("Technology", RbxValue.Token(3)); // ShadowMap: good looks, fine on phones

            var sky = lighting.AddChild(new RbxInstance("Sky", "Sky"));
            sky.Set("SunAngularSize", RbxValue.Float(14));
            sky.Set("MoonAngularSize", RbxValue.Float(12));
            sky.Set("StarCount", RbxValue.Int(look.ClockTime < 6 || look.ClockTime > 19 ? 3000 : 0));

            var atmosphere = lighting.AddChild(new RbxInstance("Atmosphere", "Atmosphere"));
            atmosphere.Set("Density", RbxValue.Float(look.Density));
            atmosphere.Set("Offset", RbxValue.Float(0.15f));
            atmosphere.Set("Color", RbxValue.Color(look.AtmosphereColor.ToColor3()));
            atmosphere.Set("Decay", RbxValue.Color(look.AtmosphereDecay.ToColor3()));
            atmosphere.Set("Glare", RbxValue.Float(0.2f));
            atmosphere.Set("Haze", RbxValue.Float(look.Haze));

            var grading = lighting.AddChild(new RbxInstance("ColorCorrectionEffect", "Grading"));
            grading.Set("Saturation", RbxValue.Float(look.Saturation));
            grading.Set("Contrast", RbxValue.Float(look.Contrast));
            grading.Set("Brightness", RbxValue.Float(0.02f));
            grading.Set("TintColor", RbxValue.Color(look.Tint.ToColor3()));
            grading.Set("Enabled", RbxValue.Bool(true));

            var bloom = lighting.AddChild(new RbxInstance("BloomEffect", "Bloom"));
            bloom.Set("Intensity", RbxValue.Float(look.Bloom));
            bloom.Set("Size", RbxValue.Float(24));
            bloom.Set("Threshold", RbxValue.Float(1.4f));
            bloom.Set("Enabled", RbxValue.Bool(true));

            if (look.SunRays > 0)
            {
                var rays = lighting.AddChild(new RbxInstance("SunRaysEffect", "SunRays"));
                rays.Set("Intensity", RbxValue.Float(look.SunRays));
                rays.Set("Spread", RbxValue.Float(0.8f));
                rays.Set("Enabled", RbxValue.Bool(true));
            }
        }

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
