using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox.Agents;
using Omnipotent.Services.OmniBlox.Assets;
using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Rbx;
using static Omnipotent.Services.OmniBlox.Agents.Schema;

namespace Omnipotent.Services.OmniBlox.Pipeline.Tools
{
    /// <summary>The builder's tools (and the maintainer's, for shipping updates): files, settings, the scene, assets, build and look.</summary>
    public static class BuildTools
    {
        public static AgentToolbox Create(StudioContext ctx, string gameId, AgentToolbox? into = null)
        {
            var box = into ?? new AgentToolbox();
            var ws = ctx.Workspace(gameId);

            // ─────────────── files ───────────────

            box.Add("list_files", "List the game's source files (game.json, scene.json, src/...).", Object(), _ =>
                ToolResult.Ok(string.Join("\n", ws.ListFiles().Select(f => $"{f.Path} ({f.Bytes} bytes)")).DefaultIfEmptyText("(empty)")));

            box.Add("read_file", "Read a source file, optionally a line range.",
                Object(Req("path", Str("e.g. src/server/Main.server.luau")), Opt("start_line", Int("First line (1-based)")), Opt("end_line", Int("Last line"))),
                args =>
                {
                    string? text = ws.ReadFile(Args.Required(args, "path"));
                    if (text == null) return ToolResult.Error("no such file");
                    var lines = text.Split('\n');
                    int start = Math.Max(1, Args.Int(args, "start_line", 1));
                    int end = Math.Min(lines.Length, Args.Int(args, "end_line", Math.Min(lines.Length, start + 399)));
                    var sb = new StringBuilder();
                    for (int i = start; i <= end; i++) sb.Append(i.ToString(CultureInfo.InvariantCulture).PadLeft(4)).Append("  ").AppendLine(lines[i - 1].TrimEnd('\r'));
                    if (end < lines.Length) sb.AppendLine($"… {lines.Length - end} more lines");
                    return ToolResult.Ok(sb.ToString());
                });

            box.Add("write_file", "Create or replace a Luau source file under src/ (server/, client/, shared/, character/, gui/ or tests/). Use .server.luau for Scripts, .client.luau for LocalScripts, .luau for ModuleScripts. The file is syntax-checked immediately.",
                Object(Req("path", Str("e.g. src/server/Lava.server.luau")), Req("content", Str("The whole file"))),
                args =>
                {
                    string path = Args.Required(args, "path");
                    if (!path.Replace('\\', '/').StartsWith("src/", StringComparison.OrdinalIgnoreCase))
                        return ToolResult.Error("scripts live under src/; edit game.json with update_manifest and the world with the scene tools");
                    string content = args["content"]?.ToString() ?? "";
                    ws.WriteFile(path, content);
                    var issues = LuauSyntax.Check(content).Concat(LuauSyntax.Lint(content)).ToList();
                    return ToolResult.Ok($"Wrote {path} ({LuauSyntax.CountLines(content)} lines)." +
                        (issues.Count == 0 ? " Syntax OK." : "\n" + LuauSyntax.Format(issues.Take(10), path)));
                });

            box.Add("delete_file", "Delete a source file.", Object(Req("path", Str("File path"))), args =>
                ws.DeleteFile(Args.Required(args, "path")) ? ToolResult.Ok("Deleted.") : ToolResult.Error("no such file"));

            // ─────────────── settings ───────────────

            box.Add("get_manifest", "Show game.json (title, template, currency, movement, tips, template settings).", Object(), _ =>
                ToolResult.Ok(JsonConvert.SerializeObject(ws.LoadManifest(), Formatting.Indented)));

            box.Add("update_manifest", "Change game.json fields. template_settings merges into the existing settings (obby: StageReward, FinishReward; simulator: BaseCapacity, Upgrades [{Key, Name, Costs:[...], Step}]; any: DailyReward, LeaderboardStat).",
                Object(Opt("title", Str("Game title")), Opt("template", Str("Template", GameKit.Templates.Keys.ToArray())), Opt("currency_name", Str("e.g. Coins, Gems")),
                    Opt("starting_currency", Int("Currency for new players")), Opt("walk_speed", Num("Default 16")), Opt("jump_power", Num("Default 50")),
                    Opt("tips", StrList("Up to 3 short tips for new players")), Opt("music_id", Str("rbxassetid://… of a free, public music track, or empty")),
                    Opt("daily_rewards", Bool("Daily login rewards")), Opt("leaderboard", Bool("Global leaderboard")), Opt("feedback", Bool("Feedback button")),
                    Opt("template_settings", Free("Template settings to merge"))),
                args =>
                {
                    var m = ws.LoadManifest();
                    if (Args.Str(args, "title") is { Length: > 0 } title) m.Title = Clip(title, 60);
                    if (Args.Str(args, "template") is { Length: > 0 } template)
                    {
                        if (!GameKit.Templates.ContainsKey(template)) return ToolResult.Error("unknown template");
                        m.Template = template.ToLowerInvariant();
                    }
                    if (Args.Str(args, "currency_name") is { Length: > 0 } cur) m.CurrencyName = Clip(cur, 20);
                    if (Args.Num(args, "starting_currency") is double sc) m.StartingCurrency = (int)Math.Clamp(sc, 0, 1_000_000);
                    if (Args.Num(args, "walk_speed") is double wsp) m.WalkSpeed = (float)Math.Clamp(wsp, 8, 60);
                    if (Args.Num(args, "jump_power") is double jp) m.JumpPower = (float)Math.Clamp(jp, 20, 150);
                    if (args["tips"] != null) m.Tips = Args.List(args, "tips").Take(3).Select(t => Clip(t, 90)).ToList();
                    if (args["music_id"] != null) m.MusicId = Args.Str(args, "music_id");
                    if (args["daily_rewards"] != null) m.DailyRewards = Args.Bool(args, "daily_rewards", true);
                    if (args["leaderboard"] != null) m.Leaderboard = Args.Bool(args, "leaderboard", true);
                    if (args["feedback"] != null) m.Feedback = Args.Bool(args, "feedback", true);
                    if (args["template_settings"] is JObject ts)
                        foreach (var p in ts.Properties()) m.TemplateSettings[p.Name] = p.Value;
                    m.Save(ws.ManifestPath);
                    return ToolResult.Ok("game.json updated.");
                });

            // ─────────────── the world ───────────────

            box.Add("scene_summary", "List the world's objects (id, kind, name, folder, position, size, tags) and terrain, optionally one folder.",
                Object(Opt("folder", Str("Only this folder")), Opt("limit", Int("Max objects (default 120)"))),
                args =>
                {
                    var scene = ws.LoadScene();
                    string folder = Args.Str(args, "folder");
                    int limit = Math.Clamp(Args.Int(args, "limit", 120), 10, 400);
                    var objs = scene.Objects.Where(o => folder.Length == 0 || o.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)).ToList();
                    var sb = new StringBuilder();
                    sb.AppendLine($"{scene.Objects.Count} objects; lighting {scene.Lighting.Preset}; {scene.Terrain.Count} terrain fill(s).");
                    foreach (var g in scene.Objects.GroupBy(o => o.Folder.Length == 0 ? "(Workspace)" : o.Folder)) sb.AppendLine($"Folder {g.Key}: {g.Count()}");
                    foreach (var t in scene.Terrain) sb.AppendLine($"terrain {t.Shape} {t.Material} centre {V(t.Center)} size {V(t.Size)}");
                    foreach (var o in objs.Take(limit)) sb.AppendLine(Describe(o));
                    if (objs.Count > limit) sb.AppendLine($"… {objs.Count - limit} more (use folder to narrow)");
                    return ToolResult.Ok(sb.ToString());
                });

            box.Add("add_objects", "Add parts, spawns, lights or text signs to the world (up to 80 per call). Position is the CENTRE. Returns the new ids.",
                Object(Req("objects", Arr(Object(
                    Opt("kind", Str("part (default), spawn, light or text", "part", "spawn", "light", "text")),
                    Opt("name", Str("Name")), Opt("folder", Str("Folder path, e.g. Obby/Stage3")),
                    Req("position", Vec3("Centre")), Opt("size", Vec3("Size")), Opt("yaw", Num("Rotation about Y, degrees")), Opt("rotation", Vec3("Rotation about X, Y, Z in degrees")),
                    Opt("shape", Str("Block, Ball, Cylinder (along X), Wedge, CornerWedge, Truss")), Opt("color", Str("#RRGGBB or a basic name")),
                    Opt("material", Str("SmoothPlastic, Plastic, Neon, Wood, WoodPlanks, Grass, Sand, Concrete, Slate, Brick, Metal, Glass, Ice, Marble, Granite, Cobblestone, Fabric…")),
                    Opt("transparency", Num("0-1")), Opt("anchored", Bool("Default true")), Opt("can_collide", Bool("Default true")),
                    Opt("tags", StrList("Game Kit tags (kit_reference tags)")), Opt("config", Free("Values the kit reads, e.g. {\"Stage\": 3}")),
                    Opt("light_type", Str("point, spot or surface")), Opt("range", Num("Light range")), Opt("brightness", Num("Light brightness")), Opt("text", Str("Text for a sign"))), "Objects to add"))),
                args =>
                {
                    var scene = ws.LoadScene();
                    var created = new List<string>();
                    var problems = new List<string>();
                    foreach (var spec in (args["objects"] as JArray ?? new JArray()).OfType<JObject>().Take(80))
                    {
                        try
                        {
                            var obj = ParseObject(spec, scene, existing: null);
                            if (obj.Kind == SceneKinds.Asset) throw new ArgumentException("use place_asset for library models");
                            scene.Objects.Add(obj);
                            created.Add(obj.Id);
                        }
                        catch (Exception ex) when (ex is ArgumentException or FormatException)
                        {
                            problems.Add($"{Args.Str(spec, "name", "object")}: {ex.Message}");
                        }
                    }
                    ws.SaveScene(scene);
                    return ToolResult.Ok($"Added {created.Count}: {string.Join(", ", created)}." + (problems.Count > 0 ? "\nProblems:\n- " + string.Join("\n- ", problems) : ""));
                });

            box.Add("update_objects", "Change existing objects (any field add_objects takes, plus asset scale). Only the fields you give change.",
                Object(Req("updates", Arr(Object(Req("id", Str("Object id"))), "Each: {id, ...fields}"))),
                args =>
                {
                    var scene = ws.LoadScene();
                    var done = new List<string>();
                    var problems = new List<string>();
                    foreach (var spec in (args["updates"] as JArray ?? new JArray()).OfType<JObject>().Take(120))
                    {
                        string id = Args.Str(spec, "id");
                        var obj = scene.Find(id);
                        if (obj == null) { problems.Add($"{id}: not found"); continue; }
                        try
                        {
                            var updated = ParseObject(spec, scene, obj);
                            scene.Objects[scene.Objects.IndexOf(obj)] = updated;
                            done.Add(id);
                        }
                        catch (Exception ex) when (ex is ArgumentException or FormatException)
                        {
                            problems.Add($"{id}: {ex.Message}");
                        }
                    }
                    ws.SaveScene(scene);
                    return ToolResult.Ok($"Updated {done.Count}." + (problems.Count > 0 ? "\nProblems:\n- " + string.Join("\n- ", problems) : ""));
                });

            box.Add("remove_objects", "Remove objects by id (or every object in a folder).",
                Object(Opt("ids", StrList("Object ids")), Opt("folder", Str("Remove everything in this folder"))),
                args =>
                {
                    var scene = ws.LoadScene();
                    var ids = Args.List(args, "ids").ToHashSet(StringComparer.Ordinal);
                    string folder = Args.Str(args, "folder");
                    int removed = scene.Objects.RemoveAll(o => ids.Contains(o.Id) || folder.Length > 0 && o.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase));
                    ws.SaveScene(scene);
                    return ToolResult.Ok($"Removed {removed}.");
                });

            box.Add("set_terrain", "Set natural terrain (generated in-game at server start). Fills are blocks or balls of a material.",
                Object(Req("fills", Arr(Object(Opt("shape", Str("block or ball")), Req("material", Str("Grass, Sand, Water, Rock, Snow, Ground, Mud, Ice, Slate, Sandstone, LeafyGrass, Asphalt, Basalt, CrackedLava")),
                    Req("center", Vec3("Centre")), Req("size", Vec3("Size"))), "Terrain fills")), Opt("replace", Bool("Replace existing fills (default true)"))),
                args =>
                {
                    var scene = ws.LoadScene();
                    if (Args.Bool(args, "replace", true)) scene.Terrain.Clear();
                    foreach (var f in (args["fills"] as JArray ?? new JArray()).OfType<JObject>().Take(40))
                    {
                        var c = Args.Vec3(f, "center") ?? throw new ArgumentException("terrain center must be [x, y, z]");
                        var s = Args.Vec3(f, "size") ?? throw new ArgumentException("terrain size must be [x, y, z]");
                        scene.Terrain.Add(new TerrainFill { Shape = Args.Str(f, "shape", "block").ToLowerInvariant() == "ball" ? "ball" : "block", Material = Args.Str(f, "material", "Grass"), Center = c, Size = s });
                    }
                    ws.SaveScene(scene);
                    return ToolResult.Ok($"{scene.Terrain.Count} terrain fill(s).");
                });

            box.Add("set_lighting", "Set the lighting preset and time of day.",
                Object(Req("preset", Str("Preset", "day", "sunset", "night", "overcast")), Opt("clock_time", Num("Hour 0-24")), Opt("brightness", Num("0-10")), Opt("shadows", Bool("Shadows"))),
                args =>
                {
                    var scene = ws.LoadScene();
                    scene.Lighting = new LightingSpec
                    {
                        Preset = Args.Required(args, "preset"),
                        ClockTime = Args.Num(args, "clock_time") is double t ? (float)t : null,
                        Brightness = Args.Num(args, "brightness") is double b ? (float)b : null,
                        Shadows = Args.Bool(args, "shadows", true),
                    };
                    ws.SaveScene(scene);
                    return ToolResult.Ok("Lighting set.");
                });

            // ─────────────── assets ───────────────

            box.Add("search_assets", "Search the Asset Library of labelled free models (plain-language query plus filters). Returns id, what it is, size in studs, style, colours and quality.",
                Object(Req("query", Str("e.g. 'low-poly pine tree'")), Opt("category", Str("Category", AssetLabeler.Categories)), Opt("style", Str("e.g. low-poly, cartoon, realistic")),
                    Opt("min_height", Num("Studs")), Opt("max_height", Num("Studs")), Opt("max_footprint", Num("Largest of width/depth, studs")), Opt("limit", Int("Default 10"))),
                args =>
                {
                    var hits = ctx.Library.Search(new AssetSearchQuery
                    {
                        Text = Args.Required(args, "query"),
                        Category = Args.Str(args, "category") is { Length: > 0 } c ? c : null,
                        Style = Args.Str(args, "style") is { Length: > 0 } s ? s : null,
                        MinHeight = Args.Num(args, "min_height") is double a ? (float)a : null,
                        MaxHeight = Args.Num(args, "max_height") is double b ? (float)b : null,
                        MaxFootprint = Args.Num(args, "max_footprint") is double f ? (float)f : null,
                        Limit = Math.Clamp(Args.Int(args, "limit", 10), 1, 25),
                    });
                    if (hits.Count == 0)
                    {
                        ctx.Harvester.RequestAssets(new[] { Args.Required(args, "query") });
                        return ToolResult.Ok("No placeable models match yet. The harvester has been asked to look for this; meanwhile build it from parts or try other words.");
                    }
                    return ToolResult.Ok(string.Join("\n", hits.Select(h => h.Summary())));
                });

            box.Add("view_asset", "See a library model: Roblox's own thumbnail plus a 3/4 render with its exact size, and its full label (what it is, front direction, uses, colours, issues).",
                Object(Req("asset_id", Int("Asset id"))),
                args =>
                {
                    long id = (long)(Args.Num(args, "asset_id") ?? 0);
                    var rec = ctx.Library.Get(id);
                    if (rec == null) return ToolResult.Error("no such asset in the library");
                    var sb = new StringBuilder(rec.Summary()).AppendLine();
                    if (rec.Measurements is { } m)
                    {
                        sb.AppendLine($"Size {m.SizeX:0.##} × {m.SizeY:0.##} × {m.SizeZ:0.##} studs (W×H×D), {m.Proportions}, {m.PartCount} parts ({m.MeshPartCount} mesh), performance cost {m.PerformanceCost}.");
                        sb.AppendLine("Colours: " + string.Join(", ", m.Colors.Select(c => $"{c.Name} {c.Hex} {c.Share:P0}")) + "; materials: " + string.Join(", ", m.Materials.Select(x => x.Material)));
                    }
                    if (rec.Label is { } l)
                    {
                        sb.AppendLine($"{l.Category}/{l.Subcategory}, {l.Style}; themes {string.Join(", ", l.Themes)}");
                        sb.AppendLine(l.Description);
                        sb.AppendLine("Good for: " + string.Join("; ", l.GoodFor));
                        if (l.PairsWith.Count > 0) sb.AppendLine("Pairs with: " + string.Join("; ", l.PairsWith));
                        sb.AppendLine($"Walkable: {l.Walkable}. Its front is already turned to face −Z at yaw 0.");
                        if (l.Issues.Count > 0) sb.AppendLine("Issues: " + string.Join("; ", l.Issues));
                    }
                    if (!rec.Placeable) sb.AppendLine($"NOT placeable: {rec.StatusReason ?? rec.Status}");
                    var images = new List<byte[]>();
                    if (File.Exists(ctx.Library.ThumbnailPath(id))) images.Add(File.ReadAllBytes(ctx.Library.ThumbnailPath(id)));
                    if (File.Exists(ctx.Library.RenderPath(id, "iso"))) images.Add(File.ReadAllBytes(ctx.Library.RenderPath(id, "iso")));
                    return ToolResult.WithImages(sb.ToString(), images);
                });

            box.Add("place_asset", "Place a library model in the world. Position is where its BOTTOM-CENTRE lands (y = the surface it stands on). Yaw 0 = its front faces −Z. Returns the new object's id and footprint.",
                Object(Req("asset_id", Int("Asset id")), Req("position", Vec3("Bottom-centre")), Opt("yaw", Num("Degrees about Y")), Opt("scale", Num("0.25–4, default 1")),
                    Opt("name", Str("Name")), Opt("folder", Str("Folder")), Opt("tags", StrList("Kit tags")), Opt("config", Free("Kit values"))),
                args =>
                {
                    long id = (long)(Args.Num(args, "asset_id") ?? 0);
                    var info = ctx.AssetInfo(id);
                    if (info == null)
                    {
                        var rec = ctx.Library.Get(id);
                        return ToolResult.Error(rec == null ? "no such asset" : $"asset {id} cannot be placed yet ({rec.StatusReason ?? rec.Status}); choose a measured, placeable model");
                    }
                    if (!File.Exists(ctx.Library.CleanModelPath(id))) return ToolResult.Error($"asset {id} has no cleaned model file");
                    var scene = ws.LoadScene();
                    var spec = (JObject)args.DeepClone();
                    spec["kind"] = SceneKinds.Asset;
                    if (spec["name"] == null) spec["name"] = Shorten(info.Name);
                    var obj = ParseObject(spec, scene, existing: null);
                    obj.AssetId = id;
                    scene.Objects.Add(obj);
                    ws.SaveScene(scene);
                    float s = obj.Scale;
                    bool turned = MathF.Abs(MathF.Sin((obj.Yaw) * MathF.PI / 180f)) > 0.7f;
                    float w = (turned ? info.Size.Z : info.Size.X) * s, d = (turned ? info.Size.X : info.Size.Z) * s;
                    return ToolResult.Ok($"Placed {obj.Id} ({info.Name}): footprint {w:0.#} × {d:0.#} studs, {info.Size.Y * s:0.#} tall, centred at x {obj.Position[0]:0.#}, z {obj.Position[2]:0.#}, standing on y {obj.Position[1]:0.#}.");
                });

            // ─────────────── build and look ───────────────

            box.Add("build", "Compile the whole game (every script's syntax, the scene, the kit) and run the geometry verifier. Fix every error; warnings matter too. A clean build is snapshotted.",
                Object(),
                _ => ToolResult.Ok(RunBuild(ctx, gameId).Report));

            box.Add("render_scene", "See the world: 'overview' (from above), 'iso' (3/4), 'spawn' (what a player sees on spawning) or an object id for a close-up. Labels show object ids.",
                Object(Opt("view", Str("overview, iso, spawn or an object id")), Opt("labels", Bool("Draw object ids (default false)"))),
                args =>
                {
                    var result = ctx.Compile(gameId);
                    if (result.Parts.Count == 0) return ToolResult.Error("the world is empty");
                    var png = SceneRenderer.Render(result.Parts, ws.LoadScene(), Args.Str(args, "view", "iso"), 960, 600, Args.Bool(args, "labels"));
                    return ToolResult.WithImages($"Rendered ({result.Parts.Count} parts). Mesh models show as dashed boxes; check positions, spacing and scale.", new[] { png });
                });

            box.Add("kit_reference", "Game Kit documentation: 'tags' (behaviours for tagged parts), 'templates' (obby/tycoon/simulator conventions), 'modules' (server and shared Luau APIs) or 'luau' (conventions with examples).",
                Object(Req("topic", Str("Topic", "tags", "templates", "modules", "luau"))),
                args => ToolResult.Ok(KitReference(Args.Required(args, "topic"))));

            box.Add("set_milestones", "Replace the milestone list (core loop first, then must-haves and content, then polish).",
                Object(Req("milestones", StrList("Milestone titles, in order"))),
                args =>
                {
                    var titles = Args.List(args, "milestones").Take(20).ToList();
                    if (titles.Count == 0) return ToolResult.Error("give at least one milestone");
                    ctx.Update(gameId, g =>
                    {
                        var old = g.Milestones.ToDictionary(m => m.Title, m => m, StringComparer.OrdinalIgnoreCase);
                        g.Milestones = titles.Select((t, i) => old.TryGetValue(t, out var keep) ? new Milestone { MilestoneId = "m" + (i + 1), Title = t, Done = keep.Done, Notes = keep.Notes } : new Milestone { MilestoneId = "m" + (i + 1), Title = Clip(t, 160) }).ToList();
                    });
                    return ToolResult.Ok(string.Join("\n", ctx.Game(gameId)!.Milestones.Select(m => $"{m.MilestoneId}: {m.Title}")));
                });

            box.Add("update_milestone", "Mark a milestone done (or not) with a note.",
                Object(Req("id", Str("e.g. m2")), Req("done", Bool("Done")), Opt("notes", Str("What was done"))),
                args =>
                {
                    string id = Args.Required(args, "id");
                    bool found = false;
                    ctx.Update(gameId, g =>
                    {
                        var m = g.Milestones.FirstOrDefault(x => x.MilestoneId.Equals(id, StringComparison.OrdinalIgnoreCase));
                        if (m == null) return;
                        found = true;
                        m.Done = Args.Bool(args, "done");
                        if (Args.Str(args, "notes") is { Length: > 0 } n) m.Notes = Clip(n, 300);
                    });
                    return found ? ToolResult.Ok("Milestone updated.") : ToolResult.Error("no milestone " + id);
                });

            box.Add("mark_feature_complete", "Declare that every must-have in the brief is built. Only allowed when the latest build is clean.",
                Object(Req("notes", Str("How each must-have is met"))),
                args =>
                {
                    var game = ctx.Game(gameId)!;
                    if (!game.Build.LastBuildOk || game.Build.Revision == 0) return ToolResult.Error("run build and fix every error first");
                    ctx.Update(gameId, g =>
                    {
                        g.Build.FeatureComplete = true;
                        g.Decisions.Add(new Decision { Utc = ctx.Clock(), Stage = g.Stage, What = "Declared feature complete", Why = Clip(Args.Required(args, "notes"), 400) });
                    });
                    return ToolResult.Ok("Marked feature complete.");
                });

            CommonTools.AddPlaybookSearch(box, ctx);
            return box;
        }

        public sealed record BuildRun(bool Ok, string Report, CompileResult Compile, List<GeometryIssue> Geometry);

        /// <summary>Compile + verify, record the result in the dossier, snapshot and save the place on success.</summary>
        public static BuildRun RunBuild(StudioContext ctx, string gameId)
        {
            var ws = ctx.Workspace(gameId);
            var result = ctx.Compile(gameId);
            var geometry = result.Parts.Count > 0 ? GeometryVerifier.Verify(result.Parts, ws.LoadScene(), ws.LoadManifest()) : new List<GeometryIssue>();
            bool ok = result.Ok && !geometry.Any(g => g.IsError);
            var errors = result.Errors.Concat(geometry.Where(g => g.IsError).Select(g => "[geometry] " + g)).ToList();
            var warnings = result.Warnings.Concat(geometry.Where(g => !g.IsError).Select(g => "[geometry] " + g)).ToList();
            int revision = 0;
            var newAssets = new List<long>();
            ctx.Update(gameId, g =>
            {
                if (ok) g.Build.Revision++;
                revision = g.Build.Revision;
                g.Build.LastBuildUtc = ctx.Clock();
                g.Build.LastBuildOk = ok;
                g.Build.Errors = errors.Take(60).ToList();
                g.Build.Warnings = warnings.Take(60).ToList();
                g.Build.InstanceCount = result.InstanceCount;
                g.Build.ScriptCount = result.ScriptCount;
                g.Build.PlaceBytes = result.PlaceXml.LongLength;
                if (ok)
                    foreach (var id in result.AssetsUsed.Keys)
                        if (!g.Build.AssetsUsed.Contains(id)) { g.Build.AssetsUsed.Add(id); newAssets.Add(id); }
            });
            if (ok)
            {
                ws.Snapshot(revision);
                Core.OmniBloxStore.WriteAtomic(Path.Combine(ctx.Store.BuildDirectory(gameId), "place.rbxlx"), result.PlaceXml);
                foreach (var id in newAssets) ctx.Library.RecordUse(id, gameId, "placed");
            }
            var sb = new StringBuilder();
            sb.AppendLine(ok ? $"BUILD OK — revision {revision}: {result.InstanceCount} instances, {result.ScriptCount} scripts, {result.Parts.Count} parts, {result.PlaceXml.Length / 1024} KB." : $"BUILD FAILED ({errors.Count} error(s)).");
            if (errors.Count > 0) sb.AppendLine("Errors:\n" + string.Join("\n", errors.Take(30).Select(e => "- " + e)));
            if (warnings.Count > 0) sb.AppendLine("Warnings:\n" + string.Join("\n", warnings.Take(20).Select(e => "- " + e)));
            return new BuildRun(ok, sb.ToString(), result, geometry);
        }

        // ─────────────── object specs ───────────────

        internal static SceneObject ParseObject(JObject spec, SceneDocument scene, SceneObject? existing)
        {
            var o = existing != null ? JsonConvert.DeserializeObject<SceneObject>(JsonConvert.SerializeObject(existing))! : new SceneObject();
            string kind = Args.Str(spec, "kind", existing?.Kind ?? SceneKinds.Part).ToLowerInvariant();
            if (kind is not (SceneKinds.Part or SceneKinds.Spawn or SceneKinds.Light or SceneKinds.Text or SceneKinds.Asset))
                throw new ArgumentException($"kind '{kind}' is not part, spawn, light or text");
            o.Kind = kind;
            if (existing == null)
                o.Id = scene.NextId(kind switch { SceneKinds.Spawn => "s", SceneKinds.Light => "l", SceneKinds.Text => "t", SceneKinds.Asset => "a", _ => "p" });
            if (Args.Str(spec, "name") is { Length: > 0 } name) o.Name = Clip(name, 60);
            if (spec["folder"] != null) o.Folder = Clip(Args.Str(spec, "folder").Trim('/'), 120);
            if (Args.Vec3(spec, "position") is { } pos) o.Position = pos;
            else if (existing == null) throw new ArgumentException("position must be [x, y, z]");
            if (spec["size"] != null) o.Size = Args.Vec3(spec, "size") ?? throw new ArgumentException("size must be [x, y, z]");
            if (Args.Num(spec, "yaw") is double yaw) o.Yaw = (float)yaw;
            if (spec["rotation"] != null) o.Rotation = Args.Vec3(spec, "rotation");
            if (Args.Num(spec, "scale") is double scale) o.Scale = (float)Math.Clamp(scale, 0.25, 4);
            if (Args.Str(spec, "shape") is { Length: > 0 } shape) o.Shape = shape;
            if (Args.Str(spec, "color") is { Length: > 0 } color) { PlaceCompiler.ParseColor(color); o.Color = color; }
            if (Args.Str(spec, "material") is { Length: > 0 } material)
            {
                if (RbxMaterials.Parse(material) == null) throw new ArgumentException($"unknown material '{material}'");
                o.Material = material;
            }
            if (Args.Num(spec, "transparency") is double tr) o.Transparency = (float)Math.Clamp(tr, 0, 1);
            if (spec["anchored"] != null) o.Anchored = Args.Bool(spec, "anchored", true);
            if (spec["can_collide"] != null) o.CanCollide = Args.Bool(spec, "can_collide", true);
            if (spec["tags"] != null) o.Tags = Args.List(spec, "tags").Take(8).ToList();
            if (spec["config"] is JObject config)
            {
                o.Config = new Dictionary<string, object>();
                foreach (var p in config.Properties().Take(12))
                {
                    if (p.Value.Type is JTokenType.Object) throw new ArgumentException($"config '{p.Name}' must be a number, text, true/false or [x, y, z]");
                    o.Config[p.Name] = p.Value;
                }
            }
            if (Args.Str(spec, "light_type") is { Length: > 0 } lt) o.LightType = lt;
            if (Args.Num(spec, "range") is double range) o.Range = (float)range;
            if (Args.Num(spec, "brightness") is double br) o.Brightness = (float)br;
            if (spec["text"] != null) o.Text = Clip(Args.Str(spec, "text"), 200);
            if (o.Size is { } sz && (sz.Length != 3 || sz.Any(v => v <= 0 || v > 2048))) throw new ArgumentException("every size value must be between 0 and 2048 studs");
            if (o.Position.Any(v => float.IsNaN(v) || MathF.Abs(v) > 100_000)) throw new ArgumentException("position is out of range");
            if (string.IsNullOrWhiteSpace(o.Name)) o.Name = o.Id;
            return o;
        }

        internal static string Describe(SceneObject o) =>
            $"{o.Id} [{o.Kind}] {o.Name}" + (o.Folder.Length > 0 ? $" in {o.Folder}" : "") + $" at {V(o.Position)}" +
            (o.Size != null ? $" size {V(o.Size)}" : "") + (o.AssetId is long a ? $" asset {a}" + (o.Scale != 1 ? $" ×{o.Scale:0.##}" : "") : "") +
            (o.Yaw != 0 ? $" yaw {o.Yaw:0}" : "") + (o.Tags.Count > 0 ? $" tags {string.Join(",", o.Tags)}" : "") +
            (o.Config.Count > 0 ? " config " + string.Join(",", o.Config.Select(kv => $"{kv.Key}={(kv.Value is JToken t ? t.ToString(Formatting.None) : kv.Value)}")) : "");

        private static string V(float[]? v) => v == null ? "?" : "[" + string.Join(", ", v.Select(x => x.ToString("0.##", CultureInfo.InvariantCulture))) + "]";

        private static string Shorten(string s) => new string(s.Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Trim() is { Length: > 0 } t ? Clip(t, 40) : "Model";

        private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

        private static string DefaultIfEmptyText(this string s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s;

        internal static string KitReference(string topic) => topic.ToLowerInvariant() switch
        {
            "tags" => "Tags (add to a part's tags; values go in config):\n" + string.Join("\n", GameKit.Tags.Select(kv => $"- {kv.Key}: {kv.Value}")),
            "templates" =>
@"obby — checkpoints are parts tagged Checkpoint with config Stage = 1..N (1 is at the spawn; put the spawn on or next to it). A part tagged Finish after the last stage. Players advance one stage at a time; progress saves; 'skipstage' products skip one stage. Settings: StageReward (default 5 + stage), FinishReward (250). Make each stage a short challenge (3-8 jumps), mark hazards red/neon (KillBrick), ramp difficulty, mix in Mover/Spinner/Disappearing/Bounce parts, and give a lava/void floor tagged KillBrick under the course.
tycoon — each plot is a Model in a folder, tagged TycoonPlot, containing: an 'Items' Model/Folder of item models (hidden until bought; give items or their parts Income = currency per second), parts tagged TycoonButton with config Price, Unlocks (an item's name) and optional Requires, a part tagged TycoonCollector (touch to bank income), optionally a part named OwnerSign with a SurfaceGui TextLabel. Make 4-6 identical plots spaced apart. Droppers (tag Dropper inside items) are cosmetic.
simulator — parts tagged Collectible (Value, Respawn) spread over zones with rising Value; a part tagged SellZone; upgrades in template settings Upgrades = [{Key: 'capacity'|'speed'|'value', Name, Costs: [...], Step}], BaseCapacity (25).
sandbox — no core loop from the kit: write it yourself under src/, using Currency, Data, Shop and Analytics.
Every template: currency on the leaderboard, a shop of the game's products, daily rewards, a feedback button, telemetry.",
            "modules" =>
@"Server (require(game.ServerScriptService.OmniKitServer.X)):
- Data: Get(player) → profile table (Currency, Stage, Owned, Upgrades, Stats…); Update(player, fn); MarkDirty(player); Loaded:Connect(fn(player, profile)).
- Currency: Get(player); Add(player, amount, reason?, applyMultiplier?) → gained; Spend(player, amount, reason?, sku?) → bool; SetMultiplier(player, source, value); Name.
- Shop: Owns(player, key); Prompt(player, key); RegisterGrant(kind, fn(player, argument, product)); Notify(player, text, kind?).
- Analytics: Event(name, player?, fields?); Funnel(player, funnel, step, stepName?); Custom(player, name, value?); Economy(player, 'Source'|'Sink', amount, balance, reason).
- Templates.Obby: Reach(player, stage), SkipStage(player), StageCount. Templates.Tycoon / Templates.Simulator similar.
Shared (require(game.ReplicatedStorage.OmniKit.X)):
- Net: Event(name) → RemoteEvent, Function(name) → RemoteFunction (server creates, client waits).
- Util: Setting(instance, name, default) reads a part's config; PlayerFromHit(hit) → player, humanoid; Format(number); Debouncer(seconds) → fn(key) → bool.
- UI (client only): Root(name), Panel, Label, Button, Modal, Toast(root, text, kind).
The HUD (currency, shop, feedback, template widget, toasts) already exists; RemoteEvent 'Notify' FireClient(player, {text=…, kind='info'|'reward'|'purchase'}) shows a toast.",
            "luau" =>
@"- Scripts: src/server/X.server.luau (server), src/client/X.client.luau (each player), src/shared/X.luau (modules for both), src/tests/X.luau (specs).
- Use game:GetService(...), task.wait/task.spawn/task.delay, :Connect for events, pcall around anything that can fail (DataStores, HTTP, MarketplaceService).
- Never trust the client: validate every RemoteEvent argument on the server; currency and progress change only on the server.
- Example server script:
local CollectionService = game:GetService('CollectionService')
local Kit = game.ServerScriptService:WaitForChild('OmniKitServer')
local Currency = require(Kit.Currency)
local Util = require(game.ReplicatedStorage:WaitForChild('OmniKit'):WaitForChild('Util'))
for _, pad in ipairs(CollectionService:GetTagged('BonusPad')) do
	local ready = Util.Debouncer(5)
	pad.Touched:Connect(function(hit)
		local player = Util.PlayerFromHit(hit)
		if player and ready(player.UserId) then Currency.Add(player, 10, 'BonusPad') end
	end)
end
- A spec (src/tests/Bonus.luau): return { ['bonus pads exist'] = function(t) t.ok(#game:GetService('CollectionService'):GetTagged('BonusPad') > 0, 'no pads') end }",
            _ => "Topics: tags, templates, modules, luau.",
        };
    }
}
