using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox.Agents;
using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Rbx;
using static Omnipotent.Services.OmniBlox.Agents.Schema;

namespace Omnipotent.Services.OmniBlox.Pipeline.Tools
{
    /// <summary>
    /// The builder's world tools. The first studio could only place one part or model at a time at exact
    /// coordinates on a flat plate, so its maps were small and bare. These build at the scale of a map: generated
    /// terrain with hills, borders, water and paths; a play area the kit walls in; a layout plan of zones, paths
    /// and landmarks; whole forests and rock fields scattered naturally in one call; fences and lamps along a
    /// path; rings round a plaza. Anything given as [x, z] stands on the ground by itself.
    /// </summary>
    public static class WorldTools
    {
        private static readonly object terrainCacheLock = new();
        private static (string Key, TerrainMap Map)? terrainCache;

        /// <summary>The scene's baked terrain (cached by recipe; recipes are small).</summary>
        public static TerrainMap? TerrainOf(SceneDocument scene)
        {
            if (scene.Land == null) return null;
            string key = JsonConvert.SerializeObject(scene.Land);
            lock (terrainCacheLock)
            {
                if (terrainCache is { } c && c.Key == key) return c.Map;
                var map = TerrainBuilder.Build(scene.Land);
                terrainCache = (key, map);
                return map;
            }
        }

        public static void Add(AgentToolbox box, StudioContext ctx, string gameId)
        {
            var ws = ctx.Workspace(gameId);

            box.Add("design_world", "The world's design, and regenerating it. With no arguments: the current world spec (layout, biome, art style, lighting, density, water, the hub, the zones with their resources and prices, the obby course or the tycoon plots, the model palette). "
                + "Give spec (any of its fields) to change it; a zones list you give replaces the zones (each: id, name, biome, theme, price, resource {kind, assets, count, hp, value, respawn, scale}, landmark, palette {role: [asset ids]}). "
                + "Then the world is regenerated: terrain, layout, gameplay objects and scenery the generator made are rebuilt; objects you placed yourself stay. For a simulator the economy is rebalanced on the new map (balance false to keep your prices). "
                + "Biomes: meadow, forest, autumn, snow, desert, beach, jungle, volcanic, candy, spooky, crystal. Roles for palette: tree, conifer, palm, deadtree, bush, flower, grass, rock, boulder, mushroom, stump, log, crystal, cactus, fence, lamp, bench, sign, building, stall, landmark, fountain, statue, campfire, crate, barrel, cart, pumpkin, candy, well.",
                Object(Opt("spec", Free("Fields of the world spec to change, e.g. {\"density\": 1.3, \"zones\": [...]}")), Opt("regenerate", Bool("Rebuild the world now (default true)")),
                    Opt("seed", Int("A new layout and scatter (keeps the design)")), Opt("balance", Bool("Rebalance a simulator's prices on the new map (default true)"))),
                args =>
                {
                    var scene = ws.LoadScene();
                    var manifest = ws.LoadManifest();
                    var game = ctx.Game(gameId)!;
                    string template = (manifest.Template ?? "").ToLowerInvariant();
                    var spec = scene.Spec ?? (game.Brief != null ? GameScaffold.SpecFor(game.Brief, GameScaffold.TemplateFor(game.Brief, manifest), scene.Objects.Count + 1) : new Build.WorldGen.WorldSpec());
                    if (args["spec"] == null && args["seed"] == null && args["regenerate"] == null)
                        return ToolResult.Ok("World spec (change any field with design_world spec):\n" + JsonConvert.SerializeObject(spec, Formatting.Indented, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
                    if (args["spec"] is JObject change)
                    {
                        var merged = JObject.FromObject(spec);
                        MergeInto(merged, change);
                        try { spec = merged.ToObject<Build.WorldGen.WorldSpec>() ?? spec; }
                        catch (JsonException ex) { return ToolResult.Error("the spec does not fit: " + ex.Message); }
                    }
                    if (args["seed"] != null) spec.Seed = Args.Int(args, "seed", spec.Seed);
                    string expected = template switch { "obby" => "course", "tycoon" => "plots", _ => "hub" };
                    if (!string.Equals(spec.Layout, expected, StringComparison.OrdinalIgnoreCase))
                        return ToolResult.Error($"a {template} game's world has layout '{expected}' (the template decides the layout)");
                    if (!Args.Bool(args, "regenerate", true))
                    {
                        scene.Spec = spec;
                        ws.SaveScene(scene);
                        return ToolResult.Ok("World spec saved (not regenerated yet).");
                    }
                    var result = Build.WorldGen.WorldGenerator.Generate(spec, manifest, ctx.Catalog(), scene);
                    string economy = "";
                    if (template == "simulator" && Args.Bool(args, "balance", true))
                        economy = GameScaffold.TuneEconomy(manifest, result.Scene);
                    // Quests that point at things the new world no longer has are remade.
                    if (manifest.Quests.Count == 0 || manifest.Quests.Any(q => q.Target is { } t && t.StartsWith("Gate_", StringComparison.Ordinal) && !result.Scene.Objects.Any(o => o.Name == t)))
                        manifest.Quests = GameScaffold.QuestsFor(template, manifest, result.Scene);
                    manifest.Save(ws.ManifestPath);
                    ws.SaveScene(result.Scene);
                    var compiled = ctx.Compile(gameId);
                    var map = MapReport.Analyze(result.Scene, compiled.Parts, compiled.Terrain, manifest, catalog: ctx.Catalog().Get);
                    string text = "Regenerated. " + result.Describe() + "\n" + map.Describe() + (economy.Length > 0 ? "\nECONOMY (rebalanced on this map):\n" + economy : "")
                        + "\nLook at it: render_scene spawn, aerial and zone:<id>.";
                    if (compiled.Parts.Count == 0 && compiled.Terrain == null) return ToolResult.Ok(text);
                    return ToolResult.WithImages(text, new[] { ctx.RenderCompiled(gameId, compiled, "map", 1280, 760, labels: false) });
                });

            box.Add("balance_economy", "Simulator only: rebalance every price (zones, tools, bag, speed, rebirth) with a bot that plays the game's own numbers on its map, so a good player meets a gentle schedule (a sale in ~1 minute, a tool in the first minute or two, a new zone every several minutes, rebirth ~25 minutes after the last zone). Run it after changing node hits/values, tools or the map. Returns the bot's timeline.",
                Object(),
                args =>
                {
                    var manifest = ws.LoadManifest();
                    if (!string.Equals(manifest.Template, "simulator", StringComparison.OrdinalIgnoreCase)) return ToolResult.Error("only a simulator's economy is balanced this way");
                    var scene = ws.LoadScene();
                    string report = GameScaffold.TuneEconomy(manifest, scene);
                    if (report.Length == 0) return ToolResult.Error("there are no resource nodes in the map to balance against (design_world makes them)");
                    manifest.Save(ws.ManifestPath);
                    ws.SaveScene(scene);
                    return ToolResult.Ok("Prices rebalanced (gates and game.json updated).\n" + report);
                });

            box.Add("scene_summary", "The world as data: terrain, play area, layout plan, folders, lighting, and the objects (id, kind, name, folder, position, size, tags), optionally one folder.",
                Object(Opt("folder", Str("Only this folder")), Opt("limit", Int("Max objects (default 120)"))),
                args =>
                {
                    var scene = ws.LoadScene();
                    string folder = Args.Str(args, "folder");
                    int limit = Math.Clamp(Args.Int(args, "limit", 120), 10, 400);
                    var objs = scene.Objects.Where(o => folder.Length == 0 || o.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)).ToList();
                    var sb = new StringBuilder();
                    sb.AppendLine($"{scene.Objects.Count} objects; lighting {scene.Lighting.Preset}; {scene.Terrain.Count} simple terrain fill(s).");
                    if (scene.Land is { } land)
                        sb.AppendLine($"Terrain: {land.Size[0]:0}×{land.Size[1]:0} studs centred ({land.Center[0]:0}, {land.Center[1]:0}), {land.Biome}/{land.Material ?? "biome ground"}, hills {land.Hills:0} (flat within {land.FlatRadius:0} of the centre), border {land.BorderHeight:0} high, water {(land.WaterLevel is float w ? "at y " + w.ToString("0.#", CultureInfo.InvariantCulture) : "none")}, {land.Ops.Count} sculpt step(s).");
                    else sb.AppendLine("Terrain: none generated (design_world).");
                    sb.AppendLine(scene.Bounds.HasArea ? $"Play area: x {scene.Bounds.MinX:0}…{scene.Bounds.MaxX:0}, z {scene.Bounds.MinZ:0}…{scene.Bounds.MaxZ:0} (walls {(scene.Bounds.Walls ? "on" : "off")})." : "Play area: not set (design_world generates one).");
                    if (scene.Layout.Zones.Count > 0)
                        sb.AppendLine("Zones: " + string.Join("; ", scene.Layout.Zones.OrderBy(z => z.Order).Select(z => $"{z.Order}. {z.Id} {z.Name} [{V(z.Area)}] {z.Purpose}")));
                    if (scene.Layout.Paths.Count > 0) sb.AppendLine("Paths: " + string.Join("; ", scene.Layout.Paths.Select(p => $"{p.From}→{p.To} ({p.Points.Count} points)")));
                    if (scene.Layout.Landmarks.Count > 0) sb.AppendLine("Landmarks: " + string.Join("; ", scene.Layout.Landmarks.Select(l => $"{l.Name} at {V(l.Position)}" + (l.ObjectId != null ? $" ({l.ObjectId})" : ""))));
                    foreach (var (path, spec) in scene.Folders) sb.AppendLine($"Folder {path}: {spec.Class}" + (spec.Tags.Count > 0 ? " tags " + string.Join(",", spec.Tags) : ""));
                    foreach (var g in scene.Objects.GroupBy(o => o.Folder.Length == 0 ? "(Workspace)" : o.Folder)) sb.AppendLine($"In {g.Key}: {g.Count()}");
                    foreach (var t in scene.Terrain) sb.AppendLine($"terrain {t.Shape} {t.Material} centre {V(t.Center)} size {V(t.Size)}");
                    foreach (var o in objs.Take(limit)) sb.AppendLine(BuildTools.Describe(o));
                    if (objs.Count > limit) sb.AppendLine($"… {objs.Count - limit} more (use folder to narrow)");
                    return ToolResult.Ok(sb.ToString());
                });

            box.Add("terrain_generate", "Generate the map's terrain: a flat core at y = 0 for building, rolling hills outside it, mountains along the edges so the world has no visible end, optional water. Make it 80-150 studs bigger than the play area on every side. Keeps sculpt steps unless reset is true. Biomes pick the materials of slopes, peaks and shores. Only for a world that was not generated: a generated world's ground comes from design_world.",
                Object(Opt("size", Arr(new JObject { ["type"] = "number" }, "[x, z] studs (default 640 × 640)")), Opt("center", Arr(new JObject { ["type"] = "number" }, "[x, z] (default 0, 0)")),
                    Opt("biome", Str("Look of slopes/peaks/shores", "meadow", "forest", "desert", "snow", "beach", "volcanic", "candy")), Opt("material", Str("Ground material (Grass, LeafyGrass, Sand, Snow, Ground…)")),
                    Opt("hills", Num("Hill height outside the flat core, studs (0 = flat; 8-20 is gentle)")), Opt("hill_scale", Num("Studs between hills (default 120)")), Opt("seed", Int("Changes the shape")),
                    Opt("flat_radius", Num("Radius kept flat round the centre for building (default 160)")), Opt("border_height", Num("Edge mountains' height (default 60; 0 = none)")),
                    Opt("border_width", Num("How far in from the edge they rise (default 70)")), Opt("water_level", Num("Water fills everything below this y (omit for none; lakes and rivers set it)")),
                    Opt("reset", Bool("Drop every sculpt step too"))),
                args =>
                {
                    var scene = ws.LoadScene();
                    if (scene.Spec != null) return Generated("terrain_generate", "its ground (zone plateaus, ramped roads, the lake, the mountains past the edge) is shaped round the layout");
                    var before = TerrainOf(scene);
                    var land = scene.Land ?? new TerrainRecipe();
                    if (Args.Bool(args, "reset")) land.Ops.Clear();
                    if (Vec2(args, "size") is { } size) land.Size = new[] { Math.Clamp(size[0], 64, 2048), Math.Clamp(size[1], 64, 2048) };
                    if (Vec2(args, "center") is { } center) land.Center = center;
                    if (Args.Str(args, "biome") is { Length: > 0 } biome)
                    {
                        land.Biome = biome.ToLowerInvariant();
                        land.Material = null; // the new biome's own ground, unless a material is given below
                    }
                    if (Args.Str(args, "material") is { Length: > 0 } material)
                    {
                        if (!TerrainBuilder.Materials.Contains(material, StringComparer.OrdinalIgnoreCase)) return ToolResult.Error($"terrain material must be one of {string.Join(", ", TerrainBuilder.Materials)}");
                        land.Material = material;
                    }
                    if (Args.Num(args, "hills") is double hills) land.Hills = (float)Math.Clamp(hills, 0, 120);
                    if (Args.Num(args, "hill_scale") is double hs) land.HillScale = (float)Math.Clamp(hs, 20, 600);
                    if (args["seed"] != null) land.Seed = Args.Int(args, "seed", land.Seed);
                    if (Args.Num(args, "flat_radius") is double fr) land.FlatRadius = (float)Math.Clamp(fr, 0, 2000);
                    if (Args.Num(args, "border_height") is double bh) land.BorderHeight = (float)Math.Clamp(bh, 0, 300);
                    if (Args.Num(args, "border_width") is double bw) land.BorderWidth = (float)Math.Clamp(bw, 8, 400);
                    if (args["water_level"] != null) land.WaterLevel = Args.Num(args, "water_level") is double wl ? (float)wl : null;
                    scene.Land = land;
                    var map = TerrainOf(scene)!;
                    int moved = WorldOps.Reground(scene, before, map, ctx.Catalog().Get);
                    ws.SaveScene(scene);
                    return ToolResult.Ok($"Terrain: {map.Width}×{map.Depth} cells of {map.Cell:0.#} studs covering x {map.OriginX:0}…{map.MaxX:0}, z {map.OriginZ:0}…{map.MaxZ:0}; heights {map.Heights.Min():0.#}…{map.Heights.Max():0.#}; materials {string.Join(", ", map.MaterialNames)}." +
                        (moved > 0 ? $" {moved} object(s) standing on the old ground were moved onto the new." : "") + " Look at it with render_scene (map, aerial).");
                });

            box.Add("terrain_sculpt", "Sculpt the terrain, in order: flatten/plateau (a round area to a height, e.g. a plaza or a building pad), raise/lower (a hill or dip), lake (a round pond; sets water), river (water along points), path (a walkable path along points, painted with a material), paint (a material over a circle or polygon). Use undo to drop the last steps, clear to drop all.",
                Object(Opt("ops", Arr(Object(Req("kind", Str("Step", "flatten", "plateau", "raise", "lower", "lake", "river", "path", "paint")),
                        Opt("center", Arr(new JObject { ["type"] = "number" }, "[x, z]")), Opt("radius", Num("Radius, studs")), Opt("points", Arr(Arr(new JObject { ["type"] = "number" }, "[x, z]"), "Points of a path, river or painted polygon")),
                        Opt("width", Num("Path/river width")), Opt("height", Num("Target height (flatten/plateau), amount (raise/lower), depth (lake/river)")),
                        Opt("material", Str("Material to paint (paths: Ground, Mud, Cobblestone, Sand, Pavement, WoodPlanks…)")), Opt("falloff", Num("0 hard edge … 1 very soft"))), "Steps to add")),
                    Opt("undo", Int("Drop this many of the last steps first")), Opt("clear", Bool("Drop every step first"))),
                args =>
                {
                    var scene = ws.LoadScene();
                    if (scene.Land == null) return ToolResult.Error("there is no terrain yet; call terrain_generate first");
                    var before = TerrainOf(scene);
                    if (Args.Bool(args, "clear")) scene.Land.Ops.Clear();
                    int undo = Math.Clamp(Args.Int(args, "undo", 0), 0, scene.Land.Ops.Count);
                    if (undo > 0) scene.Land.Ops.RemoveRange(scene.Land.Ops.Count - undo, undo);
                    var added = new List<string>();
                    foreach (var o in (args["ops"] as JArray ?? new JArray()).OfType<JObject>().Take(40))
                    {
                        var op = new TerrainOp
                        {
                            Kind = Args.Str(o, "kind", "flatten").ToLowerInvariant(),
                            Center = Vec2(o, "center"),
                            Radius = (float)(Args.Num(o, "radius") ?? 30),
                            Points = Points(o["points"]),
                            Width = (float)(Args.Num(o, "width") ?? 12),
                            Height = (float)(Args.Num(o, "height") ?? 0),
                            Material = Args.Str(o, "material") is { Length: > 0 } m ? m : null,
                            Falloff = (float)Math.Clamp(Args.Num(o, "falloff") ?? 0.5, 0, 1),
                        };
                        if (op.Material != null && !TerrainBuilder.Materials.Contains(op.Material, StringComparer.OrdinalIgnoreCase))
                            return ToolResult.Error($"terrain material must be one of {string.Join(", ", TerrainBuilder.Materials)}");
                        if (op.Kind is "path" or "river" && (op.Points?.Count ?? 0) < 2) return ToolResult.Error($"a {op.Kind} needs at least 2 points");
                        if (op.Kind is "flatten" or "plateau" or "raise" or "lower" or "lake" && op.Center == null) return ToolResult.Error($"a {op.Kind} needs a center [x, z]");
                        scene.Land.Ops.Add(op);
                        added.Add(op.Kind);
                    }
                    var map = TerrainOf(scene)!;
                    int moved = WorldOps.Reground(scene, before, map, ctx.Catalog().Get);
                    ws.SaveScene(scene);
                    return ToolResult.Ok($"{(added.Count > 0 ? "Added " + string.Join(", ", added) : "No steps added")}; {scene.Land.Ops.Count} step(s) in all. Heights now {map.Heights.Min():0.#}…{map.Heights.Max():0.#}, water {(map.WaterLevel is float w ? "at y " + w.ToString("0.#", CultureInfo.InvariantCulture) : "none")}." +
                        (moved > 0 ? $" {moved} object(s) standing on the old ground were moved onto the new." : ""));
                });

            box.Add("set_map_area", "Set the play area: the rectangle players stay inside. The kit puts invisible walls on its edges and puts anyone who falls off back at the spawn. Keep scenery (mountains, forest edges) just outside it to frame the map. Only for a world that was not generated (design_world sizes a generated one).",
                Object(Req("area", Arr(new JObject { ["type"] = "number" }, "[minX, minZ, maxX, maxZ]")), Opt("walls", Bool("Invisible walls on the edges (default true)"))),
                args =>
                {
                    var a = (args["area"] as JArray)?.Select(x => (float)x).ToArray();
                    if (a is not { Length: 4 } || a[2] <= a[0] || a[3] <= a[1]) return ToolResult.Error("area must be [minX, minZ, maxX, maxZ] with max above min");
                    var scene = ws.LoadScene();
                    if (scene.Spec != null) return Generated("set_map_area", "its play area is sized to the layout, with the tree line and mountains just outside it");
                    scene.Bounds.Area = a;
                    scene.Bounds.Walls = Args.Bool(args, "walls", true);
                    ws.SaveScene(scene);
                    string warn = scene.Land != null && TerrainOf(scene) is { } t && (a[0] < t.OriginX + 40 || a[2] > t.MaxX - 40 || a[1] < t.OriginZ + 40 || a[3] > t.MaxZ - 40)
                        ? " The terrain does not reach 40+ studs past this area on every side: players would see its edge. Make the terrain bigger (terrain_generate size)." : "";
                    return ToolResult.Ok($"Play area {a[2] - a[0]:0}×{a[3] - a[1]:0} studs." + warn);
                });

            box.Add("plan_map", "Write (or replace) the map plan before building: a one-paragraph concept, the zones players move through in order (1 = the spawn hub), the paths between them and a landmark per zone. The map view draws it and the map report checks the world against it. Only for a world that was not generated (a generated world's plan is its layout; design_world changes it).",
                Object(Opt("concept", Str("What the map is and how a player moves through it")),
                    Opt("zones", Arr(Object(Req("id", Str("Short id, e.g. hub, z1")), Req("name", Str("Name shown to players")), Req("area", Arr(new JObject { ["type"] = "number" }, "[minX, minZ, maxX, maxZ]")),
                        Opt("purpose", Str("What happens there")), Opt("order", Int("1 = spawn hub")), Opt("theme", Str("Its look"))), "Zones")),
                    Opt("paths", Arr(Object(Req("from", Str("Zone id")), Req("to", Str("Zone id")), Req("points", Arr(Arr(new JObject { ["type"] = "number" }, "[x, z]"), "Waypoints, first in from, last in to")), Opt("width", Num("Default 12"))), "Paths")),
                    Opt("landmarks", Arr(Object(Req("name", Str("Name")), Req("position", Arr(new JObject { ["type"] = "number" }, "[x, z]")), Opt("note", Str("What it is"))), "Landmarks"))),
                args =>
                {
                    var scene = ws.LoadScene();
                    if (scene.Spec != null) return Generated("plan_map", "its map plan (the zones, their gates and roads) is the layout the generator built; zones that players are walked out of until they pay come from it");
                    var plan = scene.Layout;
                    if (args["concept"] != null) plan.Concept = Clip(Args.Str(args, "concept"), 800);
                    if (args["zones"] is JArray zones)
                    {
                        plan.Zones = new List<MapZone>();
                        int order = 0;
                        foreach (var z in zones.OfType<JObject>().Take(20))
                        {
                            var area = (z["area"] as JArray)?.Select(x => (float)x).ToArray();
                            if (area is not { Length: 4 }) return ToolResult.Error("each zone needs area [minX, minZ, maxX, maxZ]");
                            plan.Zones.Add(new MapZone
                            {
                                Id = Clip(Args.Str(z, "id"), 24), Name = Clip(Args.Str(z, "name"), 40), Area = new[] { Math.Min(area[0], area[2]), Math.Min(area[1], area[3]), Math.Max(area[0], area[2]), Math.Max(area[1], area[3]) },
                                Purpose = Clip(Args.Str(z, "purpose"), 200), Order = Args.Int(z, "order", ++order), Theme = Clip(Args.Str(z, "theme"), 80),
                            });
                        }
                    }
                    if (args["paths"] is JArray paths)
                        plan.Paths = paths.OfType<JObject>().Take(30).Select(p => new MapPath { From = Args.Str(p, "from"), To = Args.Str(p, "to"), Points = Points(p["points"]) ?? new(), Width = (float)(Args.Num(p, "width") ?? 12) }).ToList();
                    if (args["landmarks"] is JArray landmarks)
                        plan.Landmarks = landmarks.OfType<JObject>().Take(30).Select(l => new MapLandmark { Name = Clip(Args.Str(l, "name"), 40), Position = Vec2(l, "position") ?? new float[] { 0, 0 }, Note = Clip(Args.Str(l, "note"), 160) }).ToList();
                    var unknown = plan.Paths.SelectMany(p => new[] { p.From, p.To }).Where(id => !plan.Zones.Any(z => z.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).Distinct().ToList();
                    ws.SaveScene(scene);
                    return ToolResult.Ok($"Map plan: {plan.Zones.Count} zone(s), {plan.Paths.Count} path(s), {plan.Landmarks.Count} landmark(s)." +
                        (unknown.Count > 0 ? " Paths name unknown zones: " + string.Join(", ", unknown) : "") +
                        " Lay the paths into the ground with terrain_sculpt (kind path) and look at render_scene map.");
                });

            box.Add("ground_height", "The ground's height, material and steepness at points (what [x, z] positions snap to).",
                Object(Req("points", Arr(Arr(new JObject { ["type"] = "number" }, "[x, z]"), "Up to 40 points"))),
                args =>
                {
                    var scene = ws.LoadScene();
                    var terrain = TerrainOf(scene);
                    var sb = new StringBuilder();
                    foreach (var p in Points(args["points"])?.Take(40) ?? Enumerable.Empty<float[]>())
                    {
                        float h = WorldOps.GroundAt(scene, terrain, p[0], p[1]);
                        sb.Append($"({p[0]:0.#}, {p[1]:0.#}): y {h:0.##}");
                        if (terrain != null && terrain.Covers(p[0], p[1]))
                            sb.Append($", {terrain.MaterialAt(p[0], p[1])}, slope {terrain.SlopeAt(p[0], p[1]):0.##}" + (terrain.InWater(p[0], p[1]) ? ", under water" : ""));
                        sb.AppendLine();
                    }
                    return ToolResult.Ok(sb.Length == 0 ? "Give points as [[x, z], …]." : sb.ToString());
                });

            box.Add("add_objects", "Add parts, spawns, lights or text signs (up to 80 a call). position [x, y, z] is the part's CENTRE; [x, z] stands it on the ground. A spawn faces its yaw (0 = towards −Z): point it at the action.",
                Object(Req("objects", Arr(Object(
                    Opt("kind", Str("part (default), spawn, light or text", "part", "spawn", "light", "text")),
                    Opt("name", Str("Name")), Opt("folder", Str("Folder path, e.g. Obby/Stage3")),
                    Req("position", Arr(new JObject { ["type"] = "number" }, "Centre [x, y, z], or [x, z] to stand on the ground")), Opt("size", Vec3("Size")), Opt("yaw", Num("Rotation about Y, degrees (0 faces −Z)")), Opt("rotation", Vec3("Rotation about X, Y, Z in degrees")),
                    Opt("shape", Str("Block, Ball, Cylinder (along X), Wedge, CornerWedge, Truss")), Opt("color", Str("#RRGGBB or a basic name")),
                    Opt("material", Str("SmoothPlastic, Plastic, Neon, Wood, WoodPlanks, Grass, Sand, Concrete, Slate, Brick, Metal, Glass, Ice, Marble, Granite, Cobblestone, Fabric…")),
                    Opt("transparency", Num("0-1")), Opt("anchored", Bool("Default true")), Opt("can_collide", Bool("Default true")),
                    Opt("tags", StrList("Game Kit tags (kit_reference tags)")), Opt("config", Free("Values the kit reads, e.g. {\"Stage\": 3}")),
                    Opt("light_type", Str("point, spot or surface")), Opt("range", Num("Light range")), Opt("brightness", Num("Light brightness")), Opt("text", Str("Text for a sign"))), "Objects to add"))),
                args =>
                {
                    var scene = ws.LoadScene();
                    var terrain = TerrainOf(scene);
                    var created = new List<string>();
                    var problems = new List<string>();
                    foreach (var spec in (args["objects"] as JArray ?? new JArray()).OfType<JObject>().Take(80))
                    {
                        try
                        {
                            var obj = BuildTools.ParseObject(Grounded(spec, scene, terrain, asset: false), scene, existing: null);
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
                    return ToolResult.Ok($"Added {created.Count}: {Ids(created)}." + (problems.Count > 0 ? "\nProblems:\n- " + string.Join("\n- ", problems) : ""));
                });

            box.Add("update_objects", "Change existing objects (any field add_objects or place_asset takes, plus scale and tint). Only the fields you give change; [x, z] positions stand them on the ground.",
                Object(Req("updates", Arr(Object(Req("id", Str("Object id"))), "Each: {id, ...fields}"))),
                args =>
                {
                    var scene = ws.LoadScene();
                    var terrain = TerrainOf(scene);
                    var done = new List<string>();
                    var problems = new List<string>();
                    foreach (var spec in (args["updates"] as JArray ?? new JArray()).OfType<JObject>().Take(150))
                    {
                        string id = Args.Str(spec, "id");
                        var obj = scene.Find(id);
                        if (obj == null) { problems.Add($"{id}: not found"); continue; }
                        try
                        {
                            var updated = BuildTools.ParseObject(Grounded(spec, scene, terrain, obj.Kind == SceneKinds.Asset, obj.Size), scene, obj);
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

            box.Add("remove_objects", "Remove objects by id (or everything in a folder).",
                Object(Opt("ids", StrList("Object ids")), Opt("folder", Str("Remove everything in this folder"))),
                args =>
                {
                    var scene = ws.LoadScene();
                    var ids = Args.List(args, "ids").ToHashSet(StringComparer.Ordinal);
                    string folder = Args.Str(args, "folder");
                    int removed = scene.Objects.RemoveAll(o => ids.Contains(o.Id) || folder.Length > 0 && (o.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase) || o.Folder.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)));
                    ws.SaveScene(scene);
                    return ToolResult.Ok($"Removed {removed}.");
                });

            box.Add("set_folder", "Make a folder a tagged Model or Folder with values the kit reads: a tycoon plot is a folder tagged TycoonPlot holding an Items folder, TycoonButton parts and a TycoonCollector. Objects are put in it by their folder path.",
                Object(Req("path", Str("Folder path, e.g. Tycoon/Plot1")), Opt("class", Str("Model (default) or Folder", "Model", "Folder")), Opt("tags", StrList("Tags")), Opt("config", Free("Values")), Opt("remove", Bool("Forget this folder's settings"))),
                args =>
                {
                    var scene = ws.LoadScene();
                    string path = Args.Required(args, "path").Trim('/');
                    if (Args.Bool(args, "remove"))
                    {
                        scene.Folders.Remove(path);
                        ws.SaveScene(scene);
                        return ToolResult.Ok("Folder settings removed.");
                    }
                    var spec = scene.Folders.TryGetValue(path, out var existing) ? existing : new FolderSpec();
                    if (Args.Str(args, "class") is { Length: > 0 } cls) spec.Class = cls.Equals("Folder", StringComparison.OrdinalIgnoreCase) ? "Folder" : "Model";
                    if (args["tags"] != null) spec.Tags = Args.List(args, "tags").Take(8).ToList();
                    if (args["config"] is JObject config)
                    {
                        spec.Config = new Dictionary<string, object>();
                        foreach (var p in config.Properties().Take(12)) spec.Config[p.Name] = p.Value;
                    }
                    scene.Folders[path] = spec;
                    ws.SaveScene(scene);
                    int inside = scene.Objects.Count(o => o.Folder.Equals(path, StringComparison.OrdinalIgnoreCase) || o.Folder.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));
                    return ToolResult.Ok($"Folder {path}: {spec.Class}" + (spec.Tags.Count > 0 ? ", tags " + string.Join(", ", spec.Tags) : "") + $"; {inside} object(s) inside.");
                });

            box.Add("set_lighting", "Set the look: a preset (sunny, golden, night, spooky, tropical, snowy, candy, overcast) with atmosphere, sky and colour grading made for a young audience, plus optional tweaks.",
                Object(Req("preset", Str("Preset", "sunny", "golden", "night", "spooky", "tropical", "snowy", "candy", "overcast")), Opt("clock_time", Num("Hour 0-24")), Opt("brightness", Num("0-10")),
                    Opt("saturation", Num("-1…1 (presets are already a little vivid)")), Opt("haze", Num("Atmosphere density 0-1")), Opt("tint", Str("#RRGGBB colour tint")), Opt("shadows", Bool("Shadows (default true)"))),
                args =>
                {
                    var scene = ws.LoadScene();
                    string preset = Args.Required(args, "preset").ToLowerInvariant();
                    if (!PlaceCompiler.Looks.ContainsKey(preset) && preset is not ("day" or "sunset")) return ToolResult.Error("unknown preset; use " + string.Join(", ", PlaceCompiler.Looks.Keys));
                    scene.Lighting = new LightingSpec
                    {
                        Preset = preset,
                        ClockTime = Args.Num(args, "clock_time") is double t ? (float)t : null,
                        Brightness = Args.Num(args, "brightness") is double b ? (float)b : null,
                        Saturation = Args.Num(args, "saturation") is double s ? (float)s : null,
                        Haze = Args.Num(args, "haze") is double h ? (float)h : null,
                        Tint = Args.Str(args, "tint") is { Length: > 0 } tint ? tint : null,
                        Shadows = Args.Bool(args, "shadows", true),
                    };
                    if (scene.Lighting.Tint != null) PlaceCompiler.ParseColor(scene.Lighting.Tint);
                    ws.SaveScene(scene);
                    return ToolResult.Ok($"Lighting set to {preset}. render_scene shows the look.");
                });

            AddPlacement(box, ctx, gameId, ws);
        }

        /// <summary>
        /// Merges a change into the spec, matching names in any case (agents write "course", the spec has "Course"), so a
        /// nested change keeps the fields it does not name; lists are replaced whole.
        /// </summary>
        internal static void MergeInto(JObject target, JObject change)
        {
            foreach (var prop in change.Properties())
            {
                var existing = target.Properties().FirstOrDefault(p => string.Equals(p.Name, prop.Name, StringComparison.OrdinalIgnoreCase));
                if (existing?.Value is JObject into && prop.Value is JObject from) MergeInto(into, from);
                else target[existing?.Name ?? prop.Name] = prop.Value.DeepClone();
            }
        }

        /// <summary>The refusal for a hand edit of what the world generator owns.</summary>
        private static ToolResult Generated(string tool, string why) =>
            ToolResult.Error($"this world is generated from its world spec and {why}, so {tool} would break it. Change the design with design_world (biome, zones, density, water, lighting, seed, …; it regenerates and keeps what you placed by hand), " +
                "make local ground changes with terrain_sculpt (everything standing there moves with the ground), and add your own things with place_asset, scatter_assets, place_along and add_objects.");

        private static void AddPlacement(AgentToolbox box, StudioContext ctx, string gameId, GameWorkspace ws)
        {
            box.Add("place_asset", "Place a library model. position [x, z] stands it on the ground (or [x, y, z]: y is where its BOTTOM lands, e.g. on a platform). Yaw 0 = its front faces −Z. Scale 0.25-4; tint recolours it (\"#FFD700\" for a golden version). Returns its id and footprint.",
                Object(Req("asset_id", Int("Asset id")), Req("position", Arr(new JObject { ["type"] = "number" }, "[x, z] on the ground, or [x, y, z]")), Opt("yaw", Num("Degrees about Y")), Opt("scale", Num("0.25–4, default 1")),
                    Opt("tint", Str("#RRGGBB to recolour every part")), Opt("name", Str("Name")), Opt("folder", Str("Folder")), Opt("tags", StrList("Kit tags")), Opt("config", Free("Kit values"))),
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
                    var spec = Grounded((JObject)args.DeepClone(), scene, TerrainOf(scene), asset: true);
                    spec["kind"] = SceneKinds.Asset;
                    if (spec["name"] == null) spec["name"] = BuildTools.Shorten(info.Name);
                    var obj = BuildTools.ParseObject(spec, scene, existing: null);
                    obj.AssetId = id;
                    scene.Objects.Add(obj);
                    ws.SaveScene(scene);
                    var (w, d, h) = Footprint(info, obj.Scale, obj.Yaw);
                    return ToolResult.Ok($"Placed {obj.Id} ({info.Name}): footprint {w:0.#} × {d:0.#} studs, {h:0.#} tall, at x {obj.Position[0]:0.#}, z {obj.Position[2]:0.#}, standing on y {obj.Position[1]:0.#}.");
                });

            box.Add("scatter_assets", "Scatter many library models over an area in one call, naturally spaced (no grid, no overlaps), standing on the ground, avoiding paths, spawns, water, steep slopes and existing objects. For forests, rock fields, flower meadows, crowds of props. Mix several asset_ids for variety.",
                Object(Req("asset_ids", Arr(Int("asset id"), "Models to mix (picked at random)")), Opt("area", Arr(Arr(new JObject { ["type"] = "number" }, "[x, z]"), "Two corners [[x0, z0], [x1, z1]] or a polygon of 3+ points")),
                    Opt("zone", Str("Or: a zone id from the map plan")), Req("count", Int("How many (up to 150)")), Opt("spacing", Num("Minimum distance between them (default: their size)")),
                    Opt("scale", Arr(new JObject { ["type"] = "number" }, "[min, max] random scale (default [0.85, 1.2])")), Opt("avoid_zones", StrList("Zone ids to keep out of")),
                    Opt("folder", Str("Folder (default Scenery)")), Opt("name", Str("Name for them")), Opt("random_yaw", Bool("Random rotation (default true)")), Opt("seed", Int("Changes the layout")),
                    Opt("tags", StrList("Kit tags for each (e.g. Collectible)")), Opt("config", Free("Kit values for each"))),
                args =>
                {
                    var ids = (args["asset_ids"] as JArray ?? new JArray()).Select(x => (long?)x ?? 0).Where(x => x > 0).Distinct().Take(12).ToList();
                    if (ids.Count == 0) return ToolResult.Error("give asset_ids");
                    var infos = new List<(long Id, AssetPlacement Info)>();
                    foreach (var id in ids)
                    {
                        var info = ctx.AssetInfo(id);
                        if (info == null || !File.Exists(ctx.Library.CleanModelPath(id))) return ToolResult.Error($"asset {id} cannot be placed (not in the library, or not measured yet)");
                        infos.Add((id, info));
                    }
                    var scene = ws.LoadScene();
                    var terrain = TerrainOf(scene);
                    var area = AreaOf(args, scene, out string? areaProblem);
                    if (area == null) return ToolResult.Error(areaProblem!);
                    int count = Math.Clamp(Args.Int(args, "count", 20), 1, 150);
                    var scaleRange = (args["scale"] as JArray)?.Select(x => (float)x).ToArray() is { Length: 2 } sr ? sr : new[] { 0.85f, 1.2f };
                    float typical = infos.Average(i => MathF.Max(i.Info.Size.X, i.Info.Size.Z)) * (scaleRange[0] + scaleRange[1]) / 2;
                    float spacing = (float)(Args.Num(args, "spacing") ?? Math.Max(4, typical * 1.1));
                    var keepout = WorldOps.DefaultKeepout(scene);
                    foreach (var zid in Args.List(args, "avoid_zones"))
                        if (scene.Layout.Zones.FirstOrDefault(z => z.Id.Equals(zid, StringComparison.OrdinalIgnoreCase)) is { } zone) keepout.Rectangles.Add(zone.Area);
                    var compiled = ctx.Compile(gameId);
                    var occupied = WorldOps.Footprints(compiled.Parts);
                    int seed = args["seed"] != null ? Args.Int(args, "seed", 1) : scene.Objects.Count * 7919 + 13;
                    var spots = WorldOps.Scatter(scene, terrain, area, count, spacing, typical / 2, occupied, keepout, seed, randomYaw: Args.Bool(args, "random_yaw", true));
                    if (spots.Count == 0) return ToolResult.Error("no room: the area is full, blocked (paths, water, slopes, objects) or outside the play area. Try a bigger or different area, or a smaller spacing.");
                    var rng = new Random(seed);
                    string folder = Args.Str(args, "folder", "Scenery");
                    string name = Args.Str(args, "name");
                    var tags = Args.List(args, "tags").Take(8).ToList();
                    var config = args["config"] as JObject;
                    var newIds = scene.NextIds("a", spots.Count).ToList();
                    for (int i = 0; i < spots.Count; i++)
                    {
                        var (id, info) = infos[rng.Next(infos.Count)];
                        float scale = scaleRange[0] + (float)rng.NextDouble() * (scaleRange[1] - scaleRange[0]);
                        var obj = new SceneObject
                        {
                            Id = newIds[i], Kind = SceneKinds.Asset, AssetId = id, Name = name.Length > 0 ? name : BuildTools.Shorten(info.Name), Folder = folder,
                            Position = new[] { spots[i].X, spots[i].Y, spots[i].Z }, Yaw = MathF.Round(spots[i].Yaw), Scale = MathF.Round(Math.Clamp(scale, 0.25f, 4f), 2), Tags = tags.ToList(),
                        };
                        if (config != null) foreach (var p in config.Properties().Take(12)) obj.Config[p.Name] = p.Value;
                        scene.Objects.Add(obj);
                    }
                    ws.SaveScene(scene);
                    return ToolResult.Ok($"Scattered {spots.Count} model(s) ({Ids(newIds)}) in folder {folder}, at least {spacing:0.#} studs apart." +
                        (spots.Count < count ? $" Only {spots.Count} of {count} fitted: the area is crowded or blocked." : "") + " Look at it with render_scene (map or aerial).");
                });

            box.Add("place_along", "Place a library model (or a part) every few studs along a line: fences, lamp posts, trees lining a path, a row of shops. Offset puts them beside the line (both_sides for an avenue); they face along it, or towards it with face_line.",
                Object(Req("points", Arr(Arr(new JObject { ["type"] = "number" }, "[x, z]"), "The line")), Req("spacing", Num("Studs between them")), Opt("asset_id", Int("A library model")),
                    Opt("part", Free("Or a part spec as in add_objects (size, color, material, shape…; position is ignored)")), Opt("offset", Num("Sideways distance from the line")), Opt("both_sides", Bool("Both sides")),
                    Opt("face_line", Bool("Face the line instead of along it")), Opt("yaw_offset", Num("Extra rotation, degrees")), Opt("scale", Num("Model scale")), Opt("folder", Str("Folder")), Opt("name", Str("Name")),
                    Opt("tags", StrList("Kit tags")), Opt("config", Free("Kit values"))),
                args => PlaceSpots(ctx, ws, args, (scene, terrain) =>
                    WorldOps.Along(scene, terrain, Points(args["points"]) ?? new(), (float)(Args.Num(args, "spacing") ?? 10), (float)(Args.Num(args, "offset") ?? 0), Args.Bool(args, "both_sides"), Args.Bool(args, "face_line"))));

            box.Add("place_ring", "Place a library model (or a part) evenly round a circle: pillars round a plaza, stalls round a fountain, stones in a ring.",
                Object(Req("center", Arr(new JObject { ["type"] = "number" }, "[x, z]")), Req("radius", Num("Studs")), Req("count", Int("How many")), Opt("asset_id", Int("A library model")),
                    Opt("part", Free("Or a part spec as in add_objects")), Opt("face_in", Bool("Face the centre (default: face out)")), Opt("yaw_offset", Num("Extra rotation")), Opt("scale", Num("Model scale")),
                    Opt("folder", Str("Folder")), Opt("name", Str("Name")), Opt("tags", StrList("Kit tags")), Opt("config", Free("Kit values"))),
                args => PlaceSpots(ctx, ws, args, (scene, terrain) =>
                {
                    var c = Vec2(args, "center") ?? throw new ArgumentException("center must be [x, z]");
                    return WorldOps.Ring(scene, terrain, c[0], c[1], (float)(Args.Num(args, "radius") ?? 20), Math.Clamp(Args.Int(args, "count", 8), 1, 80), Args.Bool(args, "face_in"));
                }));
        }

        private static ToolResult PlaceSpots(StudioContext ctx, GameWorkspace ws, JObject args, Func<SceneDocument, TerrainMap?, List<Spot>> spotsFor)
        {
            var scene = ws.LoadScene();
            var terrain = TerrainOf(scene);
            var spots = spotsFor(scene, terrain).Take(150).ToList();
            if (spots.Count == 0) return ToolResult.Error("no spots: check the points/centre and spacing");
            long assetId = (long)(Args.Num(args, "asset_id") ?? 0);
            var part = args["part"] as JObject;
            AssetPlacement? info = null;
            if (assetId > 0)
            {
                info = ctx.AssetInfo(assetId);
                if (info == null || !File.Exists(ctx.Library.CleanModelPath(assetId))) return ToolResult.Error($"asset {assetId} cannot be placed");
            }
            else if (part == null) return ToolResult.Error("give asset_id or part");
            float yawOffset = (float)(Args.Num(args, "yaw_offset") ?? 0);
            string folder = Args.Str(args, "folder", "Scenery");
            string name = Args.Str(args, "name");
            var created = new List<string>();
            var problems = new List<string>();
            foreach (var spot in spots)
            {
                try
                {
                    var spec = part != null ? (JObject)part.DeepClone() : new JObject();
                    float height = part != null && Args.Vec3(part, "size") is { } size ? size[1] : 0;
                    spec["position"] = new JArray(spot.X, spot.Y + (part != null ? height / 2 : 0), spot.Z);
                    spec["yaw"] = MathF.Round(spot.Yaw + yawOffset);
                    spec["folder"] = folder;
                    if (name.Length > 0) spec["name"] = name;
                    if (args["tags"] != null) spec["tags"] = args["tags"]!.DeepClone();
                    if (args["config"] is JObject cfg) spec["config"] = cfg.DeepClone();
                    if (info != null)
                    {
                        spec["kind"] = SceneKinds.Asset;
                        if (args["scale"] != null) spec["scale"] = args["scale"]!.DeepClone();
                        if (spec["name"] == null) spec["name"] = BuildTools.Shorten(info.Name);
                    }
                    var obj = BuildTools.ParseObject(spec, scene, existing: null);
                    if (info != null) obj.AssetId = assetId;
                    scene.Objects.Add(obj);
                    created.Add(obj.Id);
                }
                catch (Exception ex) when (ex is ArgumentException or FormatException)
                {
                    problems.Add(ex.Message);
                    if (problems.Count > 3) break;
                }
            }
            ws.SaveScene(scene);
            return ToolResult.Ok($"Placed {created.Count}: {Ids(created)}." + (problems.Count > 0 ? "\nProblems: " + string.Join("; ", problems.Distinct()) : ""));
        }

        // ─────────────── helpers ───────────────

        /// <summary>A spec whose 2-number position stands on the ground (assets by their bottom, parts by their centre).</summary>
        internal static JObject Grounded(JObject spec, SceneDocument scene, TerrainMap? terrain, bool asset, float[]? existingSize = null)
        {
            if (spec["position"] is JArray pos && pos.Count == 2)
            {
                float x = (float)pos[0], z = (float)pos[1];
                float ground = WorldOps.GroundAt(scene, terrain, x, z);
                float y = ground;
                if (!asset)
                {
                    string kind = Args.Str(spec, "kind", "part").ToLowerInvariant();
                    var size = Args.Vec3(spec, "size") ?? existingSize ?? (kind == SceneKinds.Spawn ? new float[] { 8, 1, 8 } : kind == SceneKinds.Text ? new float[] { 10, 4, 0.5f } : new float[] { 4, 1, 2 });
                    y = ground + size[1] / 2;
                }
                var copy = (JObject)spec.DeepClone();
                copy["position"] = new JArray(x, MathF.Round(y, 2), z);
                return copy;
            }
            return spec;
        }

        private static List<float[]>? AreaOf(JObject args, SceneDocument scene, out string? problem)
        {
            problem = null;
            if (Args.Str(args, "zone") is { Length: > 0 } zid)
            {
                var zone = scene.Layout.Zones.FirstOrDefault(z => z.Id.Equals(zid, StringComparison.OrdinalIgnoreCase) || z.Name.Equals(zid, StringComparison.OrdinalIgnoreCase));
                if (zone == null) { problem = $"no zone '{zid}' in the map plan"; return null; }
                return new List<float[]> { new[] { zone.Area[0], zone.Area[1] }, new[] { zone.Area[2], zone.Area[3] } };
            }
            var points = Points(args["area"]);
            if (points == null || points.Count < 2) { problem = "give area (two corners [[x0, z0], [x1, z1]] or a polygon) or zone"; return null; }
            return points;
        }

        internal static List<float[]>? Points(JToken? token)
        {
            if (token is not JArray arr) return null;
            var list = new List<float[]>();
            foreach (var p in arr)
            {
                if (p is JArray pa && pa.Count >= 2 && pa.All(x => x.Type is JTokenType.Integer or JTokenType.Float)) list.Add(new[] { (float)pa[0], (float)pa[pa.Count == 3 ? 2 : 1] });
                else if (p is JObject po && po["x"] != null && po["z"] != null) list.Add(new[] { (float)po["x"]!, (float)po["z"]! });
            }
            return list;
        }

        internal static float[]? Vec2(JObject args, string key)
        {
            var t = args[key];
            if (t is JArray arr && arr.Count >= 2 && arr.All(x => x.Type is JTokenType.Integer or JTokenType.Float)) return new[] { (float)arr[0], (float)arr[arr.Count == 3 ? 2 : 1] };
            if (t is JObject o && o["x"] != null && o["z"] != null) return new[] { (float)o["x"]!, (float)o["z"]! };
            return null;
        }

        private static (float W, float D, float H) Footprint(AssetPlacement info, float scale, float yaw)
        {
            bool turned = MathF.Abs(MathF.Sin(yaw * MathF.PI / 180f)) > 0.7f;
            return ((turned ? info.Size.Z : info.Size.X) * scale, (turned ? info.Size.X : info.Size.Z) * scale, info.Size.Y * scale);
        }

        private static string Ids(List<string> ids) => ids.Count <= 12 ? string.Join(", ", ids) : $"{string.Join(", ", ids.Take(6))} … {string.Join(", ", ids.TakeLast(3))}";

        private static string V(float[]? v) => v == null ? "?" : "[" + string.Join(", ", v.Select(x => x.ToString("0.##", CultureInfo.InvariantCulture))) + "]";

        private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
    }
}
