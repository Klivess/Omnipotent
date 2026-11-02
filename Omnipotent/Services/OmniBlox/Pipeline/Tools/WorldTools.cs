using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox.Agents;
using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Build.WorldGen;
using static Omnipotent.Services.OmniBlox.Agents.Schema;

namespace Omnipotent.Services.OmniBlox.Pipeline.Tools
{
    /// <summary>
    /// The builder's world tools: the builder designs every map itself, at the scale of a map. A landform (an
    /// island, an archipelago, a valley, a canyon, a mountain, highlands, a crater, a coast, a basin, plains) and
    /// its edge; sculpting steps that make places (plateaus, mesas, ridges, canyons, mountains, islands, terraces,
    /// lakes, rivers, ramps, paths, biome regions); a play area the kit walls in; a map plan of zones, paths and
    /// landmarks; landscaping layers dressed over any area by role in one art style (woods with clearings,
    /// meadows, rock fields, tree lines, rims, roadsides, biome touches); kit-built props (gates, arches,
    /// signposts, stands, pads, bridges, stairs, platforms); and single models, scatters, rows and rings.
    /// Anything given as [x, z] stands on the ground by itself.
    ///
    /// Maps used to come from three fixed layouts (a hub of zones, an obby course, tycoon plots), so every game
    /// looked the same; the builder now composes the map from the brief.
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

        private static readonly string SculptKinds = "flatten, plateau, mesa, raise, lower, mountain, crater, island, lake, ridge, canyon, river, path, ramp, terrace, roughen, smooth, paint";

        public static void Add(AgentToolbox box, StudioContext ctx, string gameId)
        {
            var ws = ctx.Workspace(gameId);

            box.Add("scene_summary", "The world as data: terrain (landform, edge, biome, sculpt steps), play area, art style and palette, map plan, folders, lighting, and the objects (id, kind, name, folder, position, size, tags), optionally one folder.",
                Object(Opt("folder", Str("Only this folder")), Opt("limit", Int("Max objects (default 120)"))),
                args =>
                {
                    var scene = ws.LoadScene();
                    string folder = Args.Str(args, "folder");
                    int limit = Math.Clamp(Args.Int(args, "limit", 120), 10, 400);
                    var objs = scene.Objects.Where(o => folder.Length == 0 || o.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase) || o.Folder.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)).ToList();
                    var sb = new StringBuilder();
                    sb.AppendLine($"{scene.Objects.Count} objects (about {Landscaper.PartsOf(scene, ctx.Catalog())} parts of the {Landscaper.MapBudget} phones run well); lighting {scene.Lighting.Preset}; {scene.Terrain.Count} simple terrain fill(s).");
                    if (scene.Land is { } land)
                    {
                        sb.AppendLine($"Terrain: {TerrainBuilder.ShapeOf(land)} with a {TerrainBuilder.EdgeOf(land)} edge, {land.Size[0]:0}×{land.Size[1]:0} studs centred ({land.Center[0]:0}, {land.Center[1]:0}), {land.Biome}/{land.Material ?? "biome ground"}, hills {land.Hills:0} (flat within {land.FlatRadius:0} of the centre), water {(land.WaterLevel is float w ? "at y " + w.ToString("0.#", CultureInfo.InvariantCulture) : "where the shape makes it")}, {land.Ops.Count} sculpt step(s).");
                        foreach (var f in TerrainBuilder.Features(land)) sb.AppendLine($"  {f.Name} at ({f.X:0}, {f.Z:0}) r{f.Radius:0}: {f.Note}");
                    }
                    else sb.AppendLine("Terrain: none yet (terrain_generate).");
                    sb.AppendLine(scene.Bounds.HasArea ? $"Play area: x {scene.Bounds.MinX:0}…{scene.Bounds.MaxX:0}, z {scene.Bounds.MinZ:0}…{scene.Bounds.MaxZ:0} (walls {(scene.Bounds.Walls ? "on" : "off")})." : "Play area: not set (set_map_area).");
                    sb.AppendLine($"Art style: {(scene.Style.Length > 0 ? scene.Style : "not fixed (map_palette style)")}" + (scene.Palette.Count > 0 ? "; palette " + string.Join("; ", scene.Palette.Select(kv => $"{kv.Key} {string.Join(",", kv.Value)}")) : "") + ".");
                    if (scene.Layout.Concept.Length > 0) sb.AppendLine("Concept: " + scene.Layout.Concept);
                    if (scene.Layout.Zones.Count > 0)
                        sb.AppendLine("Zones: " + string.Join("; ", scene.Layout.Zones.OrderBy(z => z.Order).Select(z => $"{z.Order}. {z.Id} {z.Name} [{V(z.Circle ?? z.Area)}] {z.Purpose}" + (z.Gated ? " (gated)" : ""))));
                    if (scene.Layout.Paths.Count > 0) sb.AppendLine("Paths: " + string.Join("; ", scene.Layout.Paths.Select(p => $"{p.From}→{p.To} ({p.Points.Count} points)")));
                    if (scene.Layout.Landmarks.Count > 0) sb.AppendLine("Landmarks: " + string.Join("; ", scene.Layout.Landmarks.Select(l => $"{l.Name} at {V(l.Position)}" + (l.ObjectId != null ? $" ({l.ObjectId})" : ""))));
                    foreach (var (path, spec) in scene.Folders) sb.AppendLine($"Folder {path}: {spec.Class}" + (spec.Tags.Count > 0 ? " tags " + string.Join(",", spec.Tags) : ""));
                    foreach (var g in scene.Objects.GroupBy(o => o.Folder.Length == 0 ? "(Workspace)" : o.Folder)) sb.AppendLine($"In {g.Key}: {g.Count()}");
                    foreach (var t in scene.Terrain) sb.AppendLine($"terrain {t.Shape} {t.Material} centre {V(t.Center)} size {V(t.Size)}");
                    foreach (var o in objs.Take(limit)) sb.AppendLine(BuildTools.Describe(o));
                    if (objs.Count > limit) sb.AppendLine($"… {objs.Count - limit} more (use folder to narrow)");
                    return ToolResult.Ok(sb.ToString());
                });

            box.Add("terrain_generate", "Generate the map's land: its landform, its edge and its look. The landform is the map's character — pick the one the brief's world calls for, and not the one the studio's last game used: basin (a flat core ringed by hills), plains (open rolling land), island (one island, beaches all round, the sea framing it), archipelago (islands across the sea, a main one in the middle — link them with bridges), coast (land on one side, the sea on the other; direction says where the sea lies), valley (a long valley between high walls along direction), canyon (a winding canyon cut into a high plateau along direction), mountain (a peak in the middle to climb; its foot is the flat land round it), highlands (terraced plateaus with cliffs between), crater (a flat bowl inside a high rim). edge frames the map: mountains, sea, cliffs or none (default: sea round islands and coasts, mountains elsewhere). "
                + "Make the terrain 100-150 studs bigger than the play area on every side. Keeps sculpt steps unless reset is true. The result lists the landform's features with coordinates to plan the places on.",
                Object(Opt("shape", Str("Landform", TerrainBuilder.Shapes)), Opt("edge", Str("What frames the map", TerrainBuilder.Edges)),
                    Opt("direction", Num("Degrees: the way a valley or canyon runs (0 = along Z), or where a coast's sea lies (0 = −Z, 90 = +X)")), Opt("islands", Int("Archipelago: 2-8 islands")),
                    Opt("relief", Num("Height of the landform's big feature (a peak, valley walls, a canyon rim, a crater rim), studs")),
                    Opt("size", Arr(new JObject { ["type"] = "number" }, "[x, z] studs (default 640 × 640)")), Opt("center", Arr(new JObject { ["type"] = "number" }, "[x, z] (default 0, 0)")),
                    Opt("biome", Str("Look of ground, slopes, peaks and shores", TerrainBuilder.BiomeNames.ToArray())), Opt("material", Str("Ground material (Grass, LeafyGrass, Sand, Snow, Ground…)")),
                    Opt("hills", Num("Hill height outside the flat core, studs (0 = flat; 8-20 is gentle)")), Opt("hill_scale", Num("Studs between hills (default 120)")), Opt("seed", Int("Changes the shape")),
                    Opt("flat_radius", Num("Radius kept flat round the centre for building (default 160)")), Opt("border_height", Num("The edge's height (mountains, cliffs; default 60; 0 = none)")),
                    Opt("border_width", Num("How far in from the edge it rises (default 70)")), Opt("water_level", Num("Water fills everything below this y (islands, coasts and sea edges default to -2.5)")),
                    Opt("undulation", Num("Gentle rolls everywhere, the flat core included (studs, default 0; 2-4 reads as land, not a plate)")),
                    Opt("reset", Bool("Drop every sculpt step too"))),
                args =>
                {
                    var scene = ws.LoadScene();
                    var before = TerrainOf(scene);
                    var land = scene.Land ?? new TerrainRecipe { Undulation = 2.5f, UndulationScale = 64 };
                    if (Args.Bool(args, "reset")) land.Ops.Clear();
                    if (Args.Str(args, "shape") is { Length: > 0 } shape)
                    {
                        if (!TerrainBuilder.Shapes.Contains(shape.ToLowerInvariant())) return ToolResult.Error("shape must be one of " + string.Join(", ", TerrainBuilder.Shapes));
                        land.Shape = shape.ToLowerInvariant();
                    }
                    if (Args.Str(args, "edge") is { Length: > 0 } edge)
                    {
                        if (!TerrainBuilder.Edges.Contains(edge.ToLowerInvariant())) return ToolResult.Error("edge must be one of " + string.Join(", ", TerrainBuilder.Edges));
                        land.Edge = edge.ToLowerInvariant();
                    }
                    if (Args.Num(args, "direction") is double dir) land.Direction = (float)dir;
                    if (args["islands"] != null) land.Islands = Math.Clamp(Args.Int(args, "islands", 4), 2, 8);
                    if (Args.Num(args, "relief") is double relief) land.Relief = (float)Math.Clamp(relief, 0, 300);
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
                    if (Args.Num(args, "undulation") is double un) land.Undulation = (float)Math.Clamp(un, 0, 12);
                    if (args["water_level"] != null) land.WaterLevel = Args.Num(args, "water_level") is double wl ? (float)wl : null;
                    scene.Land = land;
                    var map = TerrainOf(scene)!;
                    int moved = WorldOps.Reground(scene, before, map, ctx.Catalog().Get);
                    if (scene.Lighting.Preset == "sunny" && land.Biome != "meadow") scene.Lighting.Preset = MapLook.LightingFor(land.Biome);
                    ws.SaveScene(scene);
                    var sb = new StringBuilder();
                    sb.AppendLine($"Terrain: the {TerrainBuilder.ShapeOf(land)} landform with a {TerrainBuilder.EdgeOf(land)} edge, {map.Width}×{map.Depth} cells of {map.Cell:0.#} studs covering x {map.OriginX:0}…{map.MaxX:0}, z {map.OriginZ:0}…{map.MaxZ:0}; heights {map.Heights.Min():0.#}…{map.Heights.Max():0.#}; water {(map.WaterLevel is float w ? "at y " + w.ToString("0.#", CultureInfo.InvariantCulture) : "none")}; materials {string.Join(", ", map.MaterialNames)}.");
                    foreach (var f in TerrainBuilder.Features(land)) sb.AppendLine($"- {f.Name} at ({f.X:0}, {f.Z:0}), r{f.Radius:0}: {f.Note}");
                    float inset = Math.Max(110, land.BorderWidth + 40);
                    sb.AppendLine($"A play area that keeps the edge out of sight: about x {map.OriginX + inset:0}…{map.MaxX - inset:0}, z {map.OriginZ + inset:0}…{map.MaxZ - inset:0} (set_map_area).");
                    if (moved > 0) sb.AppendLine($"{moved} object(s) standing on the old ground were moved onto the new.");
                    sb.Append("Look at it: render_scene map and aerial. Then shape the places with terrain_sculpt.");
                    return ToolResult.Ok(sb.ToString());
                });

            box.Add("terrain_sculpt", "Sculpt the terrain, step by step in order: flatten/plateau (an area to a height: a plaza, a building pad, a raised place), mesa (a flat-topped height with sheer rock sides), raise/lower (a hill or dip), mountain (a ridged peak), crater (a bowl with a raised rim), island (land rising out of the water to height), lake (a pond; sets water), ridge (a range of hills along points), canyon (a steep-walled cut along points, height = depth), river (water along points), path (a walkable path along points, painted), ramp (a road that climbs from height at its first point to end_height at its last: how players reach a plateau or mesa), terrace (steps of height studs over an area), roughen (bumps over an area), smooth (soften an area), paint (a material, or a whole biome's look with biome, over an area). "
                + "Area steps take center + radius (rough 0.1-0.4 makes the outline wander, so places do not look stamped) or a polygon of 3+ points. Use undo to drop the last steps, clear to drop all. Everything standing there moves with the ground.",
                Object(Opt("ops", Arr(Object(Req("kind", Str("Step", SculptKinds.Split(", "))),
                        Opt("center", Arr(new JObject { ["type"] = "number" }, "[x, z]")), Opt("radius", Num("Radius, studs")), Opt("points", Arr(Arr(new JObject { ["type"] = "number" }, "[x, z]"), "A line (path, river, ridge, canyon, ramp) or a polygon for an area step")),
                        Opt("width", Num("Path/river/canyon width; a ridge's width; roughen's bump size")), Opt("height", Num("Target height (flatten, plateau, mesa, island), amount (raise, lower, mountain, ridge, roughen), depth (lake, canyon, crater, river), a ramp's start height, a terrace's step")),
                        Opt("end_height", Num("A ramp's height at its last point")), Opt("rough", Num("0-0.5: how ragged a round area's outline is")),
                        Opt("material", Str("Material to paint (paths: Ground, Mud, Cobblestone, Sand, Pavement, WoodPlanks…)")), Opt("biome", Str("paint: a biome's look over the area", TerrainBuilder.BiomeNames.ToArray())),
                        Opt("falloff", Num("0 hard edge … 1 very soft"))), "Steps to add")),
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
                        string kind = Args.Str(o, "kind", "flatten").ToLowerInvariant();
                        if (!SculptKinds.Split(", ").Contains(kind) && kind is not ("hill" or "trench")) return ToolResult.Error($"unknown step '{kind}'; use {SculptKinds}");
                        var op = new TerrainOp
                        {
                            Kind = kind,
                            Center = Vec2(o, "center"),
                            Radius = (float)(Args.Num(o, "radius") ?? 30),
                            Points = Points(o["points"]),
                            Width = (float)(Args.Num(o, "width") ?? kind switch { "ridge" => 60, "canyon" or "trench" => 26, "roughen" => 24, "smooth" => 12, "ramp" => 14, _ => 12 }),
                            Height = (float)(Args.Num(o, "height") ?? 0),
                            EndHeight = Args.Num(o, "end_height") is double eh ? (float)eh : null,
                            Material = Args.Str(o, "material") is { Length: > 0 } m ? m : null,
                            Biome = Args.Str(o, "biome") is { Length: > 0 } b ? b.ToLowerInvariant() : null,
                            Falloff = (float)Math.Clamp(Args.Num(o, "falloff") ?? 0.5, 0, 1),
                            Rough = (float)Math.Clamp(Args.Num(o, "rough") ?? 0, 0, 0.5),
                        };
                        if (op.Material != null && !TerrainBuilder.Materials.Contains(op.Material, StringComparer.OrdinalIgnoreCase))
                            return ToolResult.Error($"terrain material must be one of {string.Join(", ", TerrainBuilder.Materials)}");
                        if (op.Biome != null && !TerrainBuilder.BiomeNames.Contains(op.Biome)) return ToolResult.Error("biome must be one of " + string.Join(", ", TerrainBuilder.BiomeNames));
                        bool line = kind is "path" or "river" or "ridge" or "canyon" or "trench" or "ramp";
                        if (line && (op.Points?.Count ?? 0) < 2) return ToolResult.Error($"a {kind} needs at least 2 points");
                        if (!line && op.Center == null && (op.Points?.Count ?? 0) < 3) return ToolResult.Error($"a {kind} needs a center [x, z] (with a radius) or a polygon of 3+ points");
                        scene.Land.Ops.Add(op);
                        added.Add(kind);
                    }
                    var map = TerrainOf(scene)!;
                    int moved = WorldOps.Reground(scene, before, map, ctx.Catalog().Get);
                    ws.SaveScene(scene);
                    return ToolResult.Ok($"{(added.Count > 0 ? "Added " + string.Join(", ", added) : "No steps added")}; {scene.Land.Ops.Count} step(s) in all. Heights now {map.Heights.Min():0.#}…{map.Heights.Max():0.#}, water {(map.WaterLevel is float w ? "at y " + w.ToString("0.#", CultureInfo.InvariantCulture) : "none")}." +
                        (moved > 0 ? $" {moved} object(s) standing on the old ground were moved onto the new." : ""));
                });

            box.Add("set_map_area", "Set the play area: the rectangle players stay inside. The kit puts invisible walls on its edges and puts anyone who falls off back at the spawn. Keep scenery (mountains, the sea, a tree line) just outside it to frame the map.",
                Object(Req("area", Arr(new JObject { ["type"] = "number" }, "[minX, minZ, maxX, maxZ]")), Opt("walls", Bool("Invisible walls on the edges (default true)"))),
                args =>
                {
                    var a = (args["area"] as JArray)?.Select(x => (float)x).ToArray();
                    if (a is not { Length: 4 } || a[2] <= a[0] || a[3] <= a[1]) return ToolResult.Error("area must be [minX, minZ, maxX, maxZ] with max above min");
                    var scene = ws.LoadScene();
                    scene.Bounds.Area = a;
                    scene.Bounds.Walls = Args.Bool(args, "walls", true);
                    ws.SaveScene(scene);
                    string warn = scene.Land != null && TerrainOf(scene) is { } t && (a[0] < t.OriginX + 40 || a[2] > t.MaxX - 40 || a[1] < t.OriginZ + 40 || a[3] > t.MaxZ - 40)
                        ? " The terrain does not reach 40+ studs past this area on every side: players would see its edge. Make the terrain bigger (terrain_generate size)." : "";
                    return ToolResult.Ok($"Play area {a[2] - a[0]:0}×{a[3] - a[1]:0} studs." + warn);
                });

            box.Add("plan_map", "Write (or replace) the map plan: a one-paragraph concept, the places players move through in order (1 = where they spawn), the paths between them and the landmarks they navigate by. The map view draws it, the map report checks the world against it, dress_area and scatter_assets take its zones as areas, and a gated zone with a circle and an entrance is one the kit keeps players out of until they unlock it (build_prop gate). carve lays every path into the ground.",
                Object(Opt("concept", Str("What the map is and how a player moves through it")),
                    Opt("zones", Arr(Object(Req("id", Str("Short id, e.g. spawn, docks, z1")), Req("name", Str("Name shown to players")),
                        Opt("area", Arr(new JObject { ["type"] = "number" }, "[minX, minZ, maxX, maxZ]")), Opt("circle", Arr(new JObject { ["type"] = "number" }, "[x, z, radius] (instead of area)")),
                        Opt("purpose", Str("What happens there")), Opt("order", Int("1 = where players spawn")), Opt("theme", Str("Its look, with its biome word (e.g. 'misty snow peaks')")),
                        Opt("gated", Bool("Players unlock it (a gate at its entrance)")), Opt("entrance", Arr(new JObject { ["type"] = "number" }, "[x, z] where players walk in"))), "Zones")),
                    Opt("paths", Arr(Object(Req("from", Str("Zone id")), Req("to", Str("Zone id")), Req("points", Arr(Arr(new JObject { ["type"] = "number" }, "[x, z]"), "Waypoints, first in from, last in to")), Opt("width", Num("Default 12"))), "Paths")),
                    Opt("landmarks", Arr(Object(Req("name", Str("Name")), Req("position", Arr(new JObject { ["type"] = "number" }, "[x, z]")), Opt("note", Str("What it is"))), "Landmarks")),
                    Opt("carve", Bool("Lay every path into the terrain (a walkable path painted with the biome's path material)"))),
                args =>
                {
                    var scene = ws.LoadScene();
                    var plan = scene.Layout;
                    if (args["concept"] != null) plan.Concept = Clip(Args.Str(args, "concept"), 800);
                    if (args["zones"] is JArray zones)
                    {
                        plan.Zones = new List<MapZone>();
                        int order = 0;
                        foreach (var z in zones.OfType<JObject>().Take(20))
                        {
                            var area = (z["area"] as JArray)?.Select(x => (float)x).ToArray();
                            var circle = (z["circle"] as JArray)?.Select(x => (float)x).ToArray();
                            if (circle is { Length: 3 }) area ??= new[] { circle[0] - circle[2], circle[1] - circle[2], circle[0] + circle[2], circle[1] + circle[2] };
                            if (area is not { Length: 4 }) return ToolResult.Error("each zone needs area [minX, minZ, maxX, maxZ] or circle [x, z, radius]");
                            plan.Zones.Add(new MapZone
                            {
                                Id = Clip(Args.Str(z, "id"), 24), Name = Clip(Args.Str(z, "name"), 40), Area = new[] { Math.Min(area[0], area[2]), Math.Min(area[1], area[3]), Math.Max(area[0], area[2]), Math.Max(area[1], area[3]) },
                                Circle = circle is { Length: 3 } ? circle : null,
                                Purpose = Clip(Args.Str(z, "purpose"), 200), Order = Args.Int(z, "order", ++order), Theme = Clip(Args.Str(z, "theme"), 80),
                                Gated = Args.Bool(z, "gated"), Entrance = Vec2(z, "entrance"),
                            });
                        }
                    }
                    if (args["paths"] is JArray paths)
                        plan.Paths = paths.OfType<JObject>().Take(30).Select(p => new MapPath { From = Args.Str(p, "from"), To = Args.Str(p, "to"), Points = Points(p["points"]) ?? new(), Width = (float)(Args.Num(p, "width") ?? 12) }).ToList();
                    if (args["landmarks"] is JArray landmarks)
                        plan.Landmarks = landmarks.OfType<JObject>().Take(30).Select(l => new MapLandmark { Name = Clip(Args.Str(l, "name"), 40), Position = Vec2(l, "position") ?? new float[] { 0, 0 }, Note = Clip(Args.Str(l, "note"), 160) }).ToList();
                    var unknown = plan.Paths.SelectMany(p => new[] { p.From, p.To }).Where(id => !plan.Zones.Any(z => z.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).Distinct().ToList();
                    string carved = "";
                    if (Args.Bool(args, "carve"))
                    {
                        if (scene.Land == null) return ToolResult.Error("there is no terrain to carve the paths into yet; call terrain_generate first");
                        var before = TerrainOf(scene);
                        string material = MapLook.MaterialsFor(scene.Land.Biome).Path;
                        int n = 0;
                        foreach (var p in plan.Paths.Where(p => p.Points.Count >= 2))
                        {
                            if (scene.Land.Ops.Any(op => op.Kind == "path" && op.Points is { } pts && pts.Count == p.Points.Count && pts.Zip(p.Points).All(q => MathF.Abs(q.First[0] - q.Second[0]) < 0.5f && MathF.Abs(q.First[1] - q.Second[1]) < 0.5f))) continue;
                            scene.Land.Ops.Add(new TerrainOp { Kind = "path", Points = p.Points.Select(q => (float[])q.Clone()).ToList(), Width = p.Width, Material = material, Falloff = 0.4f });
                            n++;
                        }
                        int moved = WorldOps.Reground(scene, before, TerrainOf(scene), ctx.Catalog().Get);
                        carved = $" Carved {n} path(s) into the ground in {material}" + (moved > 0 ? $" ({moved} object(s) moved with the ground)." : ".");
                    }
                    ws.SaveScene(scene);
                    return ToolResult.Ok($"Map plan: {plan.Zones.Count} zone(s), {plan.Paths.Count} path(s), {plan.Landmarks.Count} landmark(s)." +
                        (unknown.Count > 0 ? " Paths name unknown zones: " + string.Join(", ", unknown) : "") + carved +
                        (carved.Length == 0 && plan.Paths.Count > 0 ? " Lay the paths into the ground with carve true (or terrain_sculpt path / ramp)," : "") + " Look at it with render_scene map.");
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

            box.Add("map_palette", "The map's art style and its models by role. With no changes: the style, the models pinned for each role, and the library's best candidates for the scenery roles in a biome (id, name, size, parts) — browse_assets shows any of them. set {role: [asset ids]} pins models for a role (dress_area and role placement use them first; [] unpins); style fixes the art style (stylized, voxel, realistic). One style per map: never PBR or realistic models in a cartoon map (they lose their textures and come out grey).",
                Object(Opt("biome", Str("Biome to suggest models for (default the terrain's)")), Opt("roles", StrList("Only these roles")), Opt("set", Free("{role: [asset ids]} to pin")),
                    Opt("style", Str("The map's art style", "stylized", "voxel", "realistic"))),
                args =>
                {
                    var scene = ws.LoadScene();
                    var catalog = ctx.Catalog();
                    var changes = new List<string>();
                    if (Args.Str(args, "style") is { Length: > 0 } style) { scene.Style = AssetCatalog.StyleFamily(style); changes.Add("style " + scene.Style); }
                    if (args["set"] is JObject set)
                        foreach (var p in set.Properties())
                        {
                            var ids = (p.Value as JArray ?? new JArray()).Select(x => (long?)x ?? 0).Where(x => x > 0).Distinct().Take(8).ToList();
                            var bad = ids.Where(id => catalog.Get(id) == null).ToList();
                            if (bad.Count > 0) return ToolResult.Error($"not placeable, labelled library models: {string.Join(", ", bad)} (search_assets / browse_assets find ones that are)");
                            if (ids.Count == 0) scene.Palette.Remove(p.Name);
                            else scene.Palette[p.Name.ToLowerInvariant()] = ids;
                            changes.Add(ids.Count == 0 ? $"{p.Name} unpinned" : $"{p.Name} → {string.Join(",", ids)}");
                        }
                    if (changes.Count > 0) ws.SaveScene(scene);
                    string biome = Args.Str(args, "biome") is { Length: > 0 } bm ? bm.ToLowerInvariant() : scene.Land?.Biome ?? "meadow";
                    var l = new Landscaper(scene, null, catalog, Array.Empty<Footprint>(), 1);
                    var roles = Args.List(args, "roles") is { Count: > 0 } only ? only : MapLook.TreeRoles(biome).Concat(new[] { Roles.Bush, Roles.Flower, Roles.Grass, Roles.Rock, Roles.Boulder }).Concat(MapLook.AccentRoles(biome))
                        .Concat(new[] { Roles.Landmark, Roles.Building, Roles.Fence, Roles.Lamp }).Distinct().ToList();
                    var sb = new StringBuilder();
                    if (changes.Count > 0) sb.AppendLine("Saved: " + string.Join("; ", changes) + ".");
                    sb.AppendLine($"Art style: {l.Style}{(scene.Style.Length == 0 ? " (the library's best for this biome; style fixes it)" : "")}. Biome {biome}.");
                    foreach (string role in roles)
                    {
                        var pinned = scene.Palette.TryGetValue(role, out var p) ? p : null;
                        var best = catalog.Best(role, biome, l.Style, 4).Where(e => !e.Untextured).ToList();
                        sb.Append($"- {role}: ");
                        if (pinned != null) sb.Append("PINNED " + string.Join(", ", pinned.Select(id => catalog.Get(id) is { } e ? Entry(e) : id.ToString(CultureInfo.InvariantCulture))) + "; ");
                        sb.AppendLine(best.Count == 0 ? "nothing in this style yet" : string.Join(", ", best.Select(Entry)));
                    }
                    var missing = roles.Where(r => !scene.Palette.ContainsKey(r) && catalog.Best(r, biome, l.Style, 1).Count == 0).ToList();
                    if (missing.Count > 0)
                    {
                        ctx.Harvester.RequestAssets(missing.Select(r => $"{l.Style} {biome} {r}"));
                        sb.AppendLine("The harvester has been asked to find: " + string.Join(", ", missing) + ".");
                    }
                    return ToolResult.Ok(sb.ToString());
                });

            box.Add("dress_area", "Landscape an area in one call, as a level artist would: models chosen by role from the library in the map's art style (map_palette shows and pins them), standing on the ground, off paths, water, spawns, steep slopes and what already stands there, inside the map's part budget. "
                + "kind: forest (woods with clearings: trees with bushes and rocks under them), meadow (flower and grass patches, the odd bush and stone), rocks (boulders with smaller rocks round them), treeline (a band of trees just past the play area's edge, so the map ends in a wood), rim (a ring of trees round a place, open where paths come in and at openings), roadside (lamps and plants along the paths), accents (the biome's small touches: mushrooms, stumps, pumpkins, cacti, crystals…). "
                + "Where: zone (a map-plan zone), circle [x, z, r] or polygon [[x, z], …]; none = the whole play area (or every path for roadside). Layer it: woods between places, a rim round each place, meadows and accents inside, roadsides, a tree line. replace true redoes a layer in the same folder.",
                Object(Req("kind", Str("What to plant", Dressing.Kinds)), Opt("zone", Str("A map-plan zone id")), Opt("circle", Arr(new JObject { ["type"] = "number" }, "[x, z, radius]")),
                    Opt("polygon", Arr(Arr(new JObject { ["type"] = "number" }, "[x, z]"), "An area of 3+ points")), Opt("biome", Str("Models of this biome (default: the zone's theme, else the terrain's)", MapLook.Biomes)),
                    Opt("density", Num("0.3 sparse … 2 lush (default 1)")), Opt("clearings", Num("forest: share left open, 0 solid … 0.8 scattered (default 0.4)")), Opt("scale", Num("Size against the role's usual (0.5-2.5)")),
                    Opt("roles", StrList("Roles to plant instead of the kind's own (e.g. palm, bush)")), Opt("asset_ids", Arr(Int("asset id"), "Models to use first for their roles")),
                    Opt("openings", Arr(Arr(new JObject { ["type"] = "number" }, "[x, z]"), "rim: entrances to leave open")), Opt("points", Arr(Arr(new JObject { ["type"] = "number" }, "[x, z]"), "roadside: a line to dress instead of the paths")),
                    Opt("width", Num("roadside: that line's width (default 12)")), Opt("lamps", Bool("roadside: lamp posts (default true)")),
                    Opt("avoid_zones", StrList("Zone ids to keep clear")), Opt("folder", Str("Folder (default Scenery/<Kind>)")), Opt("replace", Bool("Remove what this folder holds first")), Opt("seed", Int("Changes the layout"))),
                args =>
                {
                    var scene = ws.LoadScene();
                    var terrain = TerrainOf(scene);
                    if (terrain == null) return ToolResult.Error("there is no terrain yet: terrain_generate first (dressing stands on it)");
                    string kind = Args.Required(args, "kind").ToLowerInvariant();
                    if (!Dressing.Kinds.Contains(kind)) return ToolResult.Error("kind must be one of " + string.Join(", ", Dressing.Kinds));
                    MapZone? zone = null;
                    if (Args.Str(args, "zone") is { Length: > 0 } zid)
                    {
                        zone = scene.Layout.Zones.FirstOrDefault(z => z.Id.Equals(zid, StringComparison.OrdinalIgnoreCase) || z.Name.Equals(zid, StringComparison.OrdinalIgnoreCase));
                        if (zone == null) return ToolResult.Error($"no zone '{zid}' in the map plan (plan_map)");
                    }
                    var request = new DressRequest
                    {
                        Kind = kind,
                        Density = (float)(Args.Num(args, "density") ?? 1),
                        Clearings = (float)(Args.Num(args, "clearings") ?? 0.4),
                        Scale = (float)(Args.Num(args, "scale") ?? 1),
                        Roles = Args.List(args, "roles").Select(r => r.ToLowerInvariant()).Take(8).ToList(),
                        Assets = (args["asset_ids"] as JArray ?? new JArray()).Select(x => (long?)x ?? 0).Where(x => x > 0).Distinct().Take(12).ToList(),
                        Openings = Points(args["openings"]) ?? new List<float[]>(),
                        Lamps = Args.Bool(args, "lamps", true),
                        Biome = Args.Str(args, "biome") is { Length: > 0 } b ? b.ToLowerInvariant() : MapLook.BiomeIn(zone?.Theme) ?? scene.Land?.Biome ?? "meadow",
                    };
                    if (Points(args["points"]) is { Count: >= 2 } line) request.Lines.Add((line, (float)(Args.Num(args, "width") ?? 12)));
                    if (zone != null)
                    {
                        if (zone.Circle is { Length: 3 } c) request.Circle = (c[0], c[1], c[2]);
                        else request.Polygon = new List<float[]> { new[] { zone.Area[0], zone.Area[1] }, new[] { zone.Area[2], zone.Area[1] }, new[] { zone.Area[2], zone.Area[3] }, new[] { zone.Area[0], zone.Area[3] } };
                        if (zone.Entrance is { Length: 2 } e) request.Openings.Add(e);
                    }
                    else if ((args["circle"] as JArray)?.Select(x => (float)x).ToArray() is { Length: 3 } circle) request.Circle = (circle[0], circle[1], circle[2]);
                    else if (Points(args["polygon"]) is { Count: >= 3 } polygon) request.Polygon = polygon;
                    string title = char.ToUpperInvariant(kind[0]) + kind[1..];
                    request.Folder = Args.Str(args, "folder") is { Length: > 0 } f ? f.Trim('/') : "Scenery/" + title + (zone != null ? "/" + Folder(zone.Id) : "");
                    int removed = 0;
                    if (Args.Bool(args, "replace"))
                    {
                        removed = scene.Objects.RemoveAll(o => o.Folder.Equals(request.Folder, StringComparison.OrdinalIgnoreCase) || o.Folder.StartsWith(request.Folder + "/", StringComparison.OrdinalIgnoreCase));
                        ws.SaveScene(scene);
                    }
                    var compiled = ctx.Compile(gameId);
                    int seed = args["seed"] != null ? Args.Int(args, "seed", 1) : scene.Objects.Count * 7919 + kind.Length * 31 + 7;
                    var l = new Landscaper(scene, terrain, ctx.Catalog(), WorldOps.Footprints(compiled.Parts), seed);
                    foreach (var zid2 in Args.List(args, "avoid_zones"))
                        if (scene.Layout.Zones.FirstOrDefault(z => z.Id.Equals(zid2, StringComparison.OrdinalIgnoreCase)) is { } avoid && avoid != zone)
                        {
                            if (avoid.Circle is { Length: 3 } ac) l.KeepCircle(ac[0], ac[1], ac[2]);
                            else l.KeepRectangle(avoid.Area);
                        }
                    LandscapeResult result;
                    try { result = Dressing.Dress(l, request); }
                    catch (ArgumentException ex) { return ToolResult.Error(ex.Message); }
                    ws.SaveScene(scene);
                    if (result.Missing.Count > 0) ctx.Harvester.RequestAssets(result.Missing.Select(r => $"{l.Style} {request.Biome} {r}"));
                    return ToolResult.Ok($"Dressed {kind} ({request.Biome}, {l.Style}) into {request.Folder}" + (removed > 0 ? $" (replacing {removed})" : "") + ". " + result.Describe() +
                        (result.Missing.Count > 0 ? $"No library model yet for: {string.Join(", ", result.Missing)} — the harvester has been asked.\n" : "") +
                        (result.Placed == 0 ? "Nothing fitted: the area may be full, under water, too steep, on paths or outside the terrain.\n" : "") + "Look at it: render_scene aerial or map.");
                });

            box.Add("build_prop", "Build a kit prop from parts, standing on the ground and facing where you say (face [x, z] or yaw). spawn: the pad players appear on (replaces the last one; face the first goal). gate: a place's gate — pillars and a beam with its name; with a price, a glowing barrier the kit turns into a lock with an Unlock prompt, and the plan zone of that name becomes one the kit keeps players out of. arch: a named entrance. signpost: a real post with a board. stand: a market stand whose mat opens a HUD panel (shop, rebirth, or your own: hud.AddPanel). pad: a glowing pad with a sign, carrying your tags and values (a goal, a portal, a sell or start spot). rebirth: the rebirth altar. leaderboard: the global top 10. lamp, bench, crystal, tree. fence: along points. bridge: from [x, z] to [x, z] over water or a gap, walkable, with rails and pillars. stairs: up height studs towards where it faces. platform: a raised deck on posts.",
                Object(Req("kind", Str("Prop", "spawn", "gate", "arch", "signpost", "stand", "pad", "rebirth", "leaderboard", "lamp", "bench", "crystal", "tree", "fence", "bridge", "stairs", "platform")),
                    Opt("position", Arr(new JObject { ["type"] = "number" }, "[x, z]")), Opt("yaw", Num("Degrees (0 faces −Z)")), Opt("face", Arr(new JObject { ["type"] = "number" }, "[x, z] to turn towards")),
                    Opt("text", Str("Sign, arch, pad or stand label")), Opt("zone", Str("gate: the zone's name")), Opt("price", Int("gate: unlock price (0 = an open arch)")), Opt("requires", Str("gate: a zone to unlock first")),
                    Opt("currency", Str("gate: an extra currency's name")), Opt("panel", Str("stand: the HUD panel its mat opens")), Opt("width", Num("gate/arch/bridge/stairs width")),
                    Opt("height", Num("stairs: rise; platform: top above the ground; crystal/tree: size")), Opt("size", Arr(new JObject { ["type"] = "number" }, "pad [side] or platform [x, z]")),
                    Opt("from", Arr(new JObject { ["type"] = "number" }, "bridge start [x, z]")), Opt("to", Arr(new JObject { ["type"] = "number" }, "bridge end [x, z]")),
                    Opt("points", Arr(Arr(new JObject { ["type"] = "number" }, "[x, z]"), "fence line")), Opt("color", Str("#RRGGBB main colour")), Opt("accent", Str("#RRGGBB accent (default the biome's)")),
                    Opt("tags", StrList("pad: tags for the pad")), Opt("config", Free("pad: values the game reads")), Opt("folder", Str("Folder (default World/Props)"))),
                args =>
                {
                    var scene = ws.LoadScene();
                    var terrain = TerrainOf(scene);
                    string kind = Args.Required(args, "kind").ToLowerInvariant();
                    string biome = scene.Land?.Biome ?? "meadow";
                    string accent = Args.Str(args, "accent") is { Length: > 0 } ac ? ac : MapLook.AccentFor(biome);
                    string color = Args.Str(args, "color");
                    try
                    {
                        if (color.Length > 0) PlaceCompiler.ParseColor(color);
                        PlaceCompiler.ParseColor(accent);
                    }
                    catch (FormatException) { return ToolResult.Error("colours are #RRGGBB"); }
                    string folder = Args.Str(args, "folder") is { Length: > 0 } fo ? fo.Trim('/') : "World/" + (kind is "spawn" ? "Spawn" : kind is "gate" or "arch" ? "Gates" : "Props");
                    var l = new Landscaper(scene, terrain, ctx.Catalog(), Array.Empty<Footprint>(), scene.Objects.Count + 1) { Source = "kit" };
                    var pos = Vec2(args, "position");
                    float Yaw(float x, float z) => Vec2(args, "face") is { } f ? WorldOps.YawToward(x, z, f[0], f[1]) : (float)(Args.Num(args, "yaw") ?? 0);
                    string text = Args.Str(args, "text");
                    string note = "";
                    switch (kind)
                    {
                        case "bridge":
                            {
                                var from = Vec2(args, "from") ?? pos;
                                var to = Vec2(args, "to");
                                if (from == null || to == null) return ToolResult.Error("a bridge needs from [x, z] and to [x, z]");
                                float dx = to[0] - from[0], dz = to[1] - from[1], length = MathF.Sqrt(dx * dx + dz * dz);
                                if (length < 6 || length > 400) return ToolResult.Error("a bridge spans 6-400 studs");
                                float yaw = WorldOps.YawToward(from[0], from[1], to[0], to[1]);
                                float y0 = WorldOps.GroundAt(scene, terrain, from[0], from[1]), y1 = WorldOps.GroundAt(scene, terrain, to[0], to[1]);
                                float Ground(float localZ)
                                {
                                    float t = -localZ / length;
                                    return WorldOps.GroundAt(scene, terrain, from[0] + dx * t, from[1] + dz * t) - y0;
                                }
                                bool overWater = Enumerable.Range(1, 9).Any(i => terrain != null && terrain.InWater(from[0] + dx * i / 10f, from[1] + dz * i / 10f));
                                float arch = overWater ? MathF.Min(7, length * 0.07f) : 0.4f;
                                KitProps.Bridge(new PropBuilder(l.Objects, l.NewId, folder, from[0], y0, from[1], yaw), length, (float)(Args.Num(args, "width") ?? 8), y1 - y0, arch, Ground, color.Length > 0 ? color : KitProps.Wood);
                                note = $" A {length:0}-stud bridge from ({from[0]:0}, {from[1]:0}) to ({to[0]:0}, {to[1]:0}){(overWater ? " arched over the water" : "")}.";
                                break;
                            }
                        case "fence":
                            {
                                var pts = Points(args["points"]);
                                if (pts is not { Count: >= 2 }) return ToolResult.Error("a fence needs points (2+)");
                                foreach (var (a, b) in pts.Zip(pts.Skip(1)))
                                {
                                    float dx = b[0] - a[0], dz = b[1] - a[1], len = MathF.Sqrt(dx * dx + dz * dz);
                                    int spans = Math.Max(1, (int)MathF.Ceiling(len / 8));
                                    float yaw = MathF.Atan2(-dz, dx) * 180 / MathF.PI;
                                    for (int i = 0; i < spans; i++)
                                    {
                                        float x = a[0] + dx * i / spans, z = a[1] + dz * i / spans;
                                        KitProps.FenceSpan(l.Prop(folder, x, z, yaw), len / spans, color.Length > 0 ? color : KitProps.Wood);
                                    }
                                }
                                break;
                            }
                        default:
                            {
                                if (pos == null) return ToolResult.Error($"a {kind} needs position [x, z]");
                                float yaw = Yaw(pos[0], pos[1]);
                                var b = l.Prop(folder, pos[0], pos[1], yaw);
                                switch (kind)
                                {
                                    case "spawn":
                                        scene.Objects.RemoveAll(o => o.Kind == SceneKinds.Spawn);
                                        KitProps.Spawn(l.Objects, l.NewId, folder, pos[0], l.GroundAt(pos[0], pos[1]), pos[1], yaw, color.Length > 0 ? color : "#57C46A");
                                        break;
                                    case "gate":
                                    case "arch":
                                        {
                                            string zone = Args.Str(args, "zone") is { Length: > 0 } zn ? zn : text;
                                            if (zone.Length == 0) return ToolResult.Error($"a {kind} needs its zone's name (zone or text)");
                                            long price = kind == "gate" ? Math.Max(0, (long)(Args.Num(args, "price") ?? 0)) : 0;
                                            string? requires = Args.Str(args, "requires") is { Length: > 0 } rq ? rq : null;
                                            string? currency = Args.Str(args, "currency") is { Length: > 0 } cu ? cu : null;
                                            KitProps.Gate(b, Math.Clamp((float)(Args.Num(args, "width") ?? 15), 6, 40), zone, price, requires, currency, color.Length > 0 ? color : biome is "snow" ? "#DDE7F0" : biome is "desert" ? "#D6B27A" : KitProps.Stone, accent);
                                            if (price > 0 && scene.Layout.Zones.FirstOrDefault(z => z.Name.Equals(zone, StringComparison.OrdinalIgnoreCase)) is { } planned)
                                            {
                                                planned.Gated = true;
                                                planned.Entrance = new[] { MathF.Round(pos[0], 1), MathF.Round(pos[1], 1) };
                                                note = planned.Circle is { Length: 3 }
                                                    ? $" {zone} is now a gated zone: the kit keeps players out of its circle until they unlock it here."
                                                    : $" {zone} is gated at this entrance; give its plan zone a circle (plan_map) and the kit also keeps players out of its whole area.";
                                            }
                                            else if (price > 0) note = $" No plan zone is named {zone}: only this barrier blocks the way, so wall the rest of the place in with the land (a mesa, a ridge, water) or plan the zone with a circle.";
                                            break;
                                        }
                                    case "signpost":
                                        if (text.Length == 0) return ToolResult.Error("a signpost needs text");
                                        KitProps.Signpost(b, Clip(text, 40), accent);
                                        break;
                                    case "stand":
                                        {
                                            string panel = Args.Str(args, "panel", "shop").ToLowerInvariant();
                                            KitProps.Stand(b, text.Length > 0 ? Clip(text, 24) : panel == "shop" ? "🛒 SHOP" : panel.ToUpperInvariant(), panel, color.Length > 0 ? color : "#FF5E8A", accent);
                                            if (panel is not ("shop" or "rebirth")) note = $" Its mat opens the '{panel}' panel: register it in a client controller with hud.AddPanel('{panel}', open).";
                                            break;
                                        }
                                    case "pad":
                                        {
                                            float side = (args["size"] as JArray)?.Select(x => (float)x).FirstOrDefault() ?? 12;
                                            var config = new Dictionary<string, object>();
                                            if (args["config"] is JObject cfg) foreach (var p in cfg.Properties().Take(12)) config[p.Name] = p.Value;
                                            KitProps.Pad(b, Clip(text, 30), side, color.Length > 0 ? color : "#36E07A", accent, Args.List(args, "tags").Take(8), config);
                                            break;
                                        }
                                    case "rebirth": KitProps.RebirthAltar(b); break;
                                    case "leaderboard": KitProps.Leaderboard(b); break;
                                    case "lamp": KitProps.Lamp(b); break;
                                    case "bench": KitProps.Bench(b); break;
                                    case "crystal": KitProps.Crystal(b, (float)Math.Clamp(Args.Num(args, "height") ?? 14, 3, 60), color.Length > 0 ? color : accent, l.Rng, new[] { "Glow", "Sparkle" }); break;
                                    case "tree": KitProps.Tree(b, (float)Math.Clamp(Args.Num(args, "height") ?? 20, 6, 80), color.Length > 0 ? color : MapLook.LeafColor(biome), l.Rng); break;
                                    case "stairs":
                                        {
                                            float height = (float)(Args.Num(args, "height") ?? 0);
                                            if (height < 1 || height > 80) return ToolResult.Error("stairs need a height of 1-80 studs");
                                            KitProps.Stairs(b, height, Math.Clamp((float)(Args.Num(args, "width") ?? 8), 3, 30), color.Length > 0 ? color : KitProps.Stone);
                                            note = $" They climb {height:0} studs towards yaw {yaw:0}; the top landing is about {MathF.Ceiling(height) * 2 + 2.5f:0} studs ahead.";
                                            break;
                                        }
                                    case "platform":
                                        {
                                            var size = (args["size"] as JArray)?.Select(x => (float)x).ToArray() is { Length: 2 } sz ? sz : new[] { 16f, 16f };
                                            KitProps.Platform(b, Math.Clamp(size[0], 4, 120), Math.Clamp(size[1], 4, 120), (float)Math.Clamp(Args.Num(args, "height") ?? 6, 0.5, 120), color.Length > 0 ? color : KitProps.Wood);
                                            break;
                                        }
                                    default:
                                        return ToolResult.Error("unknown prop kind");
                                }
                                break;
                            }
                    }
                    var made = l.Objects.Select(o => o.Id).ToList();
                    l.Finish();
                    ws.SaveScene(scene);
                    return ToolResult.Ok($"Built {kind}: {made.Count} part(s) in {folder} ({Ids(made)}).{note}");
                });

            box.Add("add_objects", "Add parts, spawns, lights or text signs (up to 80 a call). position [x, y, z] is the part's CENTRE; [x, z] stands it on the ground. A spawn faces its yaw (0 = towards −Z): point it at the action. build_prop builds the common props for you.",
                Object(Req("objects", Arr(Object(
                    Opt("kind", Str("part (default), spawn, light or text", "part", "spawn", "light", "text")),
                    Opt("name", Str("Name")), Opt("folder", Str("Folder path, e.g. Course/Stage3")),
                    Req("position", Arr(new JObject { ["type"] = "number" }, "Centre [x, y, z], or [x, z] to stand on the ground")), Opt("size", Vec3("Size")), Opt("yaw", Num("Rotation about Y, degrees (0 faces −Z)")), Opt("rotation", Vec3("Rotation about X, Y, Z in degrees")),
                    Opt("shape", Str("Block, Ball, Cylinder (along X), Wedge, CornerWedge, Truss")), Opt("color", Str("#RRGGBB or a basic name")),
                    Opt("material", Str("SmoothPlastic, Plastic, Neon, Wood, WoodPlanks, Grass, Sand, Concrete, Slate, Brick, Metal, Glass, Ice, Marble, Granite, Cobblestone, Fabric…")),
                    Opt("transparency", Num("0-1")), Opt("anchored", Bool("Default true")), Opt("can_collide", Bool("Default true")),
                    Opt("tags", StrList("Kit tags (kit_reference tags) or your own")), Opt("config", Free("Values your systems or the kit read, e.g. {\"Stage\": 3}")),
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

            box.Add("remove_objects", "Remove objects by id (or everything in a folder and the folders under it).",
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

            box.Add("set_folder", "Make a folder a tagged Model or Folder with values your systems read (a group of parts that is one thing in the game: a vehicle, a building, a plot). Objects are put in it by their folder path.",
                Object(Req("path", Str("Folder path, e.g. Plots/Plot1")), Opt("class", Str("Model (default) or Folder", "Model", "Folder")), Opt("tags", StrList("Tags")), Opt("config", Free("Values")), Opt("remove", Bool("Forget this folder's settings"))),
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

        private static void AddPlacement(AgentToolbox box, StudioContext ctx, string gameId, GameWorkspace ws)
        {
            box.Add("place_asset", "Place a library model (asset_id, or role to take the map palette's best for it). position [x, z] stands it on the ground (or [x, y, z]: y is where its BOTTOM lands, e.g. on a platform). Yaw 0 = its front faces −Z. Scale 0.25-4; tint recolours it (\"#FFD700\" for a golden version). Returns its id and footprint.",
                Object(Opt("asset_id", Int("Asset id")), Opt("role", Str("Or a role (tree, landmark, building, statue, fountain…): the map palette's best model for it")),
                    Req("position", Arr(new JObject { ["type"] = "number" }, "[x, z] on the ground, or [x, y, z]")), Opt("yaw", Num("Degrees about Y")), Opt("scale", Num("0.25–4, default 1")),
                    Opt("tint", Str("#RRGGBB to recolour every part")), Opt("name", Str("Name")), Opt("folder", Str("Folder")), Opt("tags", StrList("Tags")), Opt("config", Free("Values"))),
                args =>
                {
                    var scene = ws.LoadScene();
                    long id = (long)(Args.Num(args, "asset_id") ?? 0);
                    if (id <= 0 && Args.Str(args, "role") is { Length: > 0 } role)
                    {
                        var models = RoleModels(ctx, scene, role, 1);
                        if (models.Count == 0) return ToolResult.Error($"the library has no '{role}' model in this map's style yet (the harvester has been asked); search_assets finds others");
                        id = models[0];
                    }
                    if (id <= 0) return ToolResult.Error("give asset_id or role");
                    var info = ctx.AssetInfo(id);
                    if (info == null)
                    {
                        var rec = ctx.Library.Get(id);
                        return ToolResult.Error(rec == null ? "no such asset" : $"asset {id} cannot be placed yet ({rec.StatusReason ?? rec.Status}); choose a measured, placeable model");
                    }
                    if (!File.Exists(ctx.Library.CleanModelPath(id))) return ToolResult.Error($"asset {id} has no cleaned model file");
                    var spec = Grounded((JObject)args.DeepClone(), scene, TerrainOf(scene), asset: true);
                    spec.Remove("role");
                    spec["kind"] = SceneKinds.Asset;
                    if (spec["name"] == null) spec["name"] = BuildTools.Shorten(info.Name);
                    var obj = BuildTools.ParseObject(spec, scene, existing: null);
                    obj.AssetId = id;
                    scene.Objects.Add(obj);
                    ws.SaveScene(scene);
                    var (w, d, h) = Footprint(info, obj.Scale, obj.Yaw);
                    return ToolResult.Ok($"Placed {obj.Id} ({info.Name}, #{id}): footprint {w:0.#} × {d:0.#} studs, {h:0.#} tall, at x {obj.Position[0]:0.#}, z {obj.Position[2]:0.#}, standing on y {obj.Position[1]:0.#}.");
                });

            box.Add("scatter_assets", "Scatter many library models over an area in one call, naturally spaced (no grid, no overlaps), standing on the ground, avoiding paths, spawns, water, steep slopes and existing objects. Give asset_ids, or a role to use the map palette's models for it. For a whole landscape layer, dress_area does more (clearings, undergrowth, rims).",
                Object(Opt("asset_ids", Arr(Int("asset id"), "Models to mix (picked at random)")), Opt("role", Str("Or a role: the map palette's models for it")), Opt("area", Arr(Arr(new JObject { ["type"] = "number" }, "[x, z]"), "Two corners [[x0, z0], [x1, z1]] or a polygon of 3+ points")),
                    Opt("zone", Str("Or: a zone id from the map plan")), Req("count", Int("How many (up to 150)")), Opt("spacing", Num("Minimum distance between them (default: their size)")),
                    Opt("scale", Arr(new JObject { ["type"] = "number" }, "[min, max] random scale (default [0.85, 1.2])")), Opt("avoid_zones", StrList("Zone ids to keep out of")),
                    Opt("folder", Str("Folder (default Scenery)")), Opt("name", Str("Name for them")), Opt("random_yaw", Bool("Random rotation (default true)")), Opt("seed", Int("Changes the layout")),
                    Opt("tags", StrList("Tags for each (e.g. your game's pickups)")), Opt("config", Free("Values for each"))),
                args =>
                {
                    var scene = ws.LoadScene();
                    var ids = (args["asset_ids"] as JArray ?? new JArray()).Select(x => (long?)x ?? 0).Where(x => x > 0).Distinct().Take(12).ToList();
                    if (ids.Count == 0 && Args.Str(args, "role") is { Length: > 0 } role) ids = RoleModels(ctx, scene, role, 3);
                    if (ids.Count == 0) return ToolResult.Error(Args.Str(args, "role").Length > 0 ? "the library has no model for that role in this map's style yet (the harvester has been asked)" : "give asset_ids or role");
                    var infos = new List<(long Id, AssetPlacement Info)>();
                    foreach (var id in ids)
                    {
                        var info = ctx.AssetInfo(id);
                        if (info == null || !File.Exists(ctx.Library.CleanModelPath(id))) return ToolResult.Error($"asset {id} cannot be placed (not in the library, or not measured yet)");
                        infos.Add((id, info));
                    }
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
                    return ToolResult.Ok($"Scattered {spots.Count} model(s) ({Ids(newIds)}; models {string.Join(",", ids)}) in folder {folder}, at least {spacing:0.#} studs apart." +
                        (spots.Count < count ? $" Only {spots.Count} of {count} fitted: the area is crowded or blocked." : "") + " Look at it with render_scene (map or aerial).");
                });

            box.Add("place_along", "Place a library model (asset_id or role) or a part every few studs along a line: fences, lamp posts, trees lining an avenue, a row of shops. Offset puts them beside the line (both_sides for an avenue); they face along it, or towards it with face_line.",
                Object(Req("points", Arr(Arr(new JObject { ["type"] = "number" }, "[x, z]"), "The line")), Req("spacing", Num("Studs between them")), Opt("asset_id", Int("A library model")), Opt("role", Str("Or a role: the map palette's best model")),
                    Opt("part", Free("Or a part spec as in add_objects (size, color, material, shape…; position is ignored)")), Opt("offset", Num("Sideways distance from the line")), Opt("both_sides", Bool("Both sides")),
                    Opt("face_line", Bool("Face the line instead of along it")), Opt("yaw_offset", Num("Extra rotation, degrees")), Opt("scale", Num("Model scale")), Opt("folder", Str("Folder")), Opt("name", Str("Name")),
                    Opt("tags", StrList("Tags")), Opt("config", Free("Values"))),
                args => PlaceSpots(ctx, ws, args, (scene, terrain) =>
                    WorldOps.Along(scene, terrain, Points(args["points"]) ?? new(), (float)(Args.Num(args, "spacing") ?? 10), (float)(Args.Num(args, "offset") ?? 0), Args.Bool(args, "both_sides"), Args.Bool(args, "face_line"))));

            box.Add("place_ring", "Place a library model (asset_id or role) or a part evenly round a circle: pillars round a plaza, stalls round a fountain, stones in a ring.",
                Object(Req("center", Arr(new JObject { ["type"] = "number" }, "[x, z]")), Req("radius", Num("Studs")), Req("count", Int("How many")), Opt("asset_id", Int("A library model")), Opt("role", Str("Or a role: the map palette's best model")),
                    Opt("part", Free("Or a part spec as in add_objects")), Opt("face_in", Bool("Face the centre (default: face out)")), Opt("yaw_offset", Num("Extra rotation")), Opt("scale", Num("Model scale")),
                    Opt("folder", Str("Folder")), Opt("name", Str("Name")), Opt("tags", StrList("Tags")), Opt("config", Free("Values"))),
                args => PlaceSpots(ctx, ws, args, (scene, terrain) =>
                {
                    var c = Vec2(args, "center") ?? throw new ArgumentException("center must be [x, z]");
                    return WorldOps.Ring(scene, terrain, c[0], c[1], (float)(Args.Num(args, "radius") ?? 20), Math.Clamp(Args.Int(args, "count", 8), 1, 80), Args.Bool(args, "face_in"));
                }));
        }

        /// <summary>The models for a role on this map: its pinned palette, else the library's best in the map's style for its biome.</summary>
        private static List<long> RoleModels(StudioContext ctx, SceneDocument scene, string role, int count)
        {
            var l = new Landscaper(scene, null, ctx.Catalog(), Array.Empty<Footprint>(), 1);
            var list = l.Palette(role.Trim().ToLowerInvariant(), scene.Land?.Biome ?? "meadow", null, count).Select(e => e.AssetId).Take(count).ToList();
            if (list.Count == 0) ctx.Harvester.RequestAssets(new[] { $"{l.Style} {scene.Land?.Biome ?? "meadow"} {role}" });
            return list;
        }

        private static ToolResult PlaceSpots(StudioContext ctx, GameWorkspace ws, JObject args, Func<SceneDocument, TerrainMap?, List<Spot>> spotsFor)
        {
            var scene = ws.LoadScene();
            var terrain = TerrainOf(scene);
            var spots = spotsFor(scene, terrain).Take(150).ToList();
            if (spots.Count == 0) return ToolResult.Error("no spots: check the points/centre and spacing");
            long assetId = (long)(Args.Num(args, "asset_id") ?? 0);
            if (assetId <= 0 && Args.Str(args, "role") is { Length: > 0 } role)
            {
                var models = RoleModels(ctx, scene, role, 1);
                if (models.Count == 0) return ToolResult.Error($"the library has no '{role}' model in this map's style yet (the harvester has been asked)");
                assetId = models[0];
            }
            var part = args["part"] as JObject;
            AssetPlacement? info = null;
            if (assetId > 0)
            {
                info = ctx.AssetInfo(assetId);
                if (info == null || !File.Exists(ctx.Library.CleanModelPath(assetId))) return ToolResult.Error($"asset {assetId} cannot be placed");
            }
            else if (part == null) return ToolResult.Error("give asset_id, role or part");
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

        private static string Entry(CatalogEntry e) => $"#{e.AssetId} {Landscaper.Short(e.Name)} ({e.SizeX:0}×{e.SizeY:0}×{e.SizeZ:0}, {e.Parts} parts)";

        private static string Folder(string id)
        {
            string s = new string(id.Where(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-').ToArray());
            return s.Length == 0 ? "Zone" : s;
        }

        private static string Ids(List<string> ids) => ids.Count <= 12 ? string.Join(", ", ids) : $"{string.Join(", ", ids.Take(6))} … {string.Join(", ", ids.TakeLast(3))}";

        private static string V(float[]? v) => v == null ? "?" : "[" + string.Join(", ", v.Select(x => x.ToString("0.##", CultureInfo.InvariantCulture))) + "]";

        private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
    }
}
