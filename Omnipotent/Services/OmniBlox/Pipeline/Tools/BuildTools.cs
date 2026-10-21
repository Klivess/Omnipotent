using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox.Agents;
using Omnipotent.Services.OmniBlox.Assets;
using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Rbx;
using Omnipotent.Services.OmniBlox.Roblox;
using SkiaSharp;
using static Omnipotent.Services.OmniBlox.Agents.Schema;

namespace Omnipotent.Services.OmniBlox.Pipeline.Tools
{
    /// <summary>
    /// The builder's tools (and the maintainer's, for shipping updates): files (FileTools), the world
    /// (WorldTools), game.json, the asset library and licensed sounds, and the checks — build (syntax, types,
    /// geometry, the map report), run_tests (the game in Roblox's engine), renders, the map report,
    /// diagnose_failure and pacing_check — plus milestones and the feature-complete declaration.
    /// </summary>
    public static class BuildTools
    {
        /// <summary>Roblox's own sounds the kit uses by default (Sounds.luau): always allowed in game.json.</summary>
        public static readonly HashSet<long> KitSounds = new()
        {
            15675059323, 15675032796, 10066931761, 10066968815, 15674975792, 15675024286, 10066947742, 127645268874265,
            17208319162, 17208361335, 17208372272, 12222253, 12221967, 12222046, 12222095, 15675075163, 15675069601,
        };

        public static AgentToolbox Create(StudioContext ctx, string gameId, AgentToolbox? into = null)
        {
            var box = into ?? new AgentToolbox();
            var ws = ctx.Workspace(gameId);

            FileTools.Add(box, ctx, gameId);

            // ─────────────── game.json ───────────────

            box.Add("get_manifest", "Show game.json: title, template, currencies, movement, tips, theme, sounds, quests, rebirth, template settings.", Object(), _ =>
                ToolResult.Ok(JsonConvert.SerializeObject(ws.LoadManifest(), Formatting.Indented)));

            box.Add("update_manifest", "Change game.json. Only the fields you give change. Lists you give replace the old list. Sounds and music must be ids from search_sounds (licensed) or the kit's defaults.",
                Object(Opt("title", Str("Game title")), Opt("template", Str("Template", GameKit.Templates.Keys.ToArray())), Opt("currency_name", Str("e.g. Coins, Gems")), Opt("currency_icon", Str("An emoji, e.g. 🪙")),
                    Opt("starting_currency", Int("Main currency for new players")), Opt("currencies", Arr(Object(Req("name", Str("e.g. Gems")), Opt("icon", Str("Emoji")), Opt("start", Int("Starting amount")), Opt("leaderstat", Bool("Show on the leaderboard"))), "Extra currencies")),
                    Opt("walk_speed", Num("Default 16")), Opt("jump_power", Num("Default 50")), Opt("tips", StrList("Up to 3 short tips for the welcome card")),
                    Opt("music_id", Str("A licensed music id from search_sounds (APM or Roblox), or empty")), Opt("daily_rewards", Bool("Daily login rewards")), Opt("daily_reward", Int("Day-one reward (0 turns them off)")),
                    Opt("leaderboard", Bool("Global leaderboard")), Opt("feedback", Bool("Feedback button")),
                    Opt("theme", Object(Opt("accent", Str("#RRGGBB main button colour")), Opt("accent2", Str("#RRGGBB secondary")), Opt("panel", Str("#RRGGBB panel colour")))),
                    Opt("sounds", Free("Sound overrides by name: {\"coin\": 127645268874265, \"chop\": 123…}")),
                    Opt("quests", Arr(Object(Req("text", Str("What to do, e.g. 'Chop 5 trees'")), Req("event", Str("Event that counts: collect, sell, upgrade, stage, finish, unlock, zone, rebirth, coin, or your own")),
                        Opt("count", Int("How many (default 1)")), Opt("reward", Int("Main-currency reward")), Opt("reward_currency", Str("Pay in an extra currency instead")), Opt("target", Str("Part/model name or tag the guide line points at")), Opt("id", Str("Id"))), "The quest chain (replaces it)")),
                    Opt("rebirth", Object(Opt("enabled", Bool("On")), Opt("cost", Int("First rebirth's cost")), Opt("growth", Num("Cost multiplier per rebirth (2-3)")), Opt("bonus", Num("Earnings bonus per rebirth (0.5 = +50%)")), Opt("keep_upgrades", Bool("Keep upgrades on rebirth")))),
                    Opt("template_settings", Free("Template settings to merge (simulator: BaseCapacity, Upgrades [{Key, Name, Icon, Costs:[...], Step}]; obby: StageReward, FinishReward; any: LeaderboardStat)"))),
                args =>
                {
                    var m = ws.LoadManifest();
                    var problems = new List<string>();
                    if (Args.Str(args, "title") is { Length: > 0 } title) m.Title = Clip(title, 60);
                    if (Args.Str(args, "template") is { Length: > 0 } template)
                    {
                        if (!GameKit.Templates.ContainsKey(template)) return ToolResult.Error("unknown template");
                        m.Template = template.ToLowerInvariant();
                    }
                    if (Args.Str(args, "currency_name") is { Length: > 0 } cur) m.CurrencyName = Clip(cur, 20);
                    if (Args.Str(args, "currency_icon") is { Length: > 0 } icon) m.CurrencyIcon = Clip(icon, 8);
                    if (Args.Num(args, "starting_currency") is double sc) m.StartingCurrency = (int)Math.Clamp(sc, 0, 1_000_000);
                    if (args["currencies"] is JArray currencies)
                        m.Currencies = currencies.OfType<JObject>().Take(3).Select(c => new CurrencySpec { Name = Clip(Args.Str(c, "name"), 20), Icon = Clip(Args.Str(c, "icon", "💎"), 8), Start = Args.Int(c, "start", 0), Leaderstat = Args.Bool(c, "leaderstat") })
                            .Where(c => c.Name.Length > 0 && !c.Name.Equals(m.CurrencyName, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (Args.Num(args, "walk_speed") is double wsp) m.WalkSpeed = (float)Math.Clamp(wsp, 8, 60);
                    if (Args.Num(args, "jump_power") is double jp) m.JumpPower = (float)Math.Clamp(jp, 20, 150);
                    if (args["tips"] != null) m.Tips = Args.Prose(args, "tips").Take(3).Select(t => Clip(t, 90)).ToList();
                    if (args["music_id"] != null)
                    {
                        string music = Args.Str(args, "music_id");
                        if (music.Length > 0 && !IsLicensed(ctx, music)) problems.Add($"music {music} is not a licensed sound: find music with search_sounds (APM's or Roblox's uploads)");
                        else m.MusicId = music;
                    }
                    if (args["daily_rewards"] != null) m.DailyRewards = Args.Bool(args, "daily_rewards", true);
                    if (args["daily_reward"] != null) m.DailyReward = Math.Clamp(Args.Int(args, "daily_reward", 25), 0, 1_000_000);
                    if (args["leaderboard"] != null) m.Leaderboard = Args.Bool(args, "leaderboard", true);
                    if (args["feedback"] != null) m.Feedback = Args.Bool(args, "feedback", true);
                    if (args["theme"] is JObject theme)
                        foreach (var (key, field) in new[] { ("Accent", "accent"), ("Accent2", "accent2"), ("Panel", "panel") })
                            if (Args.Str(theme, field) is { Length: > 0 } color)
                            {
                                try { PlaceCompiler.ParseColor(color); m.Theme[key] = color.StartsWith('#') ? color : "#" + color; }
                                catch (FormatException ex) { problems.Add(ex.Message); }
                            }
                    if (args["sounds"] is JObject sounds)
                        foreach (var p in sounds.Properties().Take(30))
                        {
                            string id = p.Value.ToString();
                            if (!IsLicensed(ctx, id)) problems.Add($"sound '{p.Name}' = {id} is not a licensed sound: use an id from search_sounds or a kit sound name");
                            else m.Sounds[p.Name] = long.TryParse(id, out long n) ? n : id;
                        }
                    if (args["quests"] is JArray quests)
                        m.Quests = quests.OfType<JObject>().Take(30).Select((q, i) => new QuestSpec
                        {
                            Id = Args.Str(q, "id") is { Length: > 0 } qid ? qid : "q" + (i + 1),
                            Text = Clip(Args.Str(q, "text"), 60),
                            Event = Args.Str(q, "event").ToLowerInvariant(),
                            Count = Math.Max(1, Args.Int(q, "count", 1)),
                            Reward = Math.Max(0, Args.Int(q, "reward", 0)),
                            RewardCurrency = Args.Str(q, "reward_currency") is { Length: > 0 } rc ? rc : null,
                            Target = Args.Str(q, "target") is { Length: > 0 } tg ? tg : null,
                        }).Where(q => q.Text.Length > 0 && q.Event.Length > 0).ToList();
                    if (args["rebirth"] is JObject rebirth)
                    {
                        var r = m.Rebirth ?? new RebirthSpec();
                        if (rebirth["enabled"] != null) r.Enabled = Args.Bool(rebirth, "enabled", true);
                        if (Args.Num(rebirth, "cost") is double cost) r.Cost = (long)Math.Clamp(cost, 1, 1e12);
                        if (Args.Num(rebirth, "growth") is double growth) r.Growth = Math.Clamp(growth, 1.1, 10);
                        if (Args.Num(rebirth, "bonus") is double bonus) r.Bonus = Math.Clamp(bonus, 0.05, 10);
                        if (rebirth["keep_upgrades"] != null) r.KeepUpgrades = Args.Bool(rebirth, "keep_upgrades");
                        m.Rebirth = r;
                    }
                    if (args["template_settings"] is JObject ts)
                        foreach (var p in ts.Properties()) m.TemplateSettings[p.Name] = p.Value;
                    m.Save(ws.ManifestPath);
                    return problems.Count == 0 ? ToolResult.Ok("game.json updated.") : ToolResult.Ok("game.json updated, except:\n- " + string.Join("\n- ", problems));
                });

            WorldTools.Add(box, ctx, gameId);

            // ─────────────── the asset library and sounds ───────────────

            box.Add("search_assets", "Search the Asset Library of labelled free models (plain-language query plus filters). Returns id, what it is, size in studs, style, colours and quality. browse_assets shows the hits as one picture.",
                Object(Req("query", Str("e.g. 'low-poly pine tree'")), Opt("category", Str("Category", AssetLabeler.Categories)), Opt("style", Str("e.g. low-poly, cartoon, realistic")),
                    Opt("min_height", Num("Studs")), Opt("max_height", Num("Studs")), Opt("max_footprint", Num("Largest of width/depth, studs")), Opt("limit", Int("Default 10"))),
                args =>
                {
                    var hits = Search(ctx, args, 25);
                    if (hits.Count == 0)
                    {
                        ctx.Harvester.RequestAssets(new[] { Args.Required(args, "query") });
                        return ToolResult.Ok("No placeable models match yet. The harvester has been asked to look for this; meanwhile build it from parts or try other words.");
                    }
                    return ToolResult.Ok(string.Join("\n", hits.Take(Math.Clamp(Args.Int(args, "limit", 10), 1, 25)).Select(h => h.Summary())));
                });

            box.Add("browse_assets", "See up to 12 library models side by side as ONE picture (Roblox's render of each, with its id and size): the quick way to pick models that match each other in style and colour.",
                Object(Req("query", Str("e.g. 'autumn tree' or 'rock'")), Opt("category", Str("Category", AssetLabeler.Categories)), Opt("style", Str("Style")), Opt("min_height", Num("Studs")), Opt("max_height", Num("Studs"))),
                args =>
                {
                    var hits = Search(ctx, args, 12);
                    if (hits.Count == 0) return ToolResult.Ok("No placeable models match yet.");
                    var cells = hits.Select(h => (h.AssetId, File.Exists(ctx.Library.ThumbnailPath(h.AssetId)) ? File.ReadAllBytes(ctx.Library.ThumbnailPath(h.AssetId)) : null,
                        h.Measurements is { } m ? $"{h.AssetId} · {m.SizeX:0}×{m.SizeY:0}×{m.SizeZ:0}" : h.AssetId.ToString(CultureInfo.InvariantCulture))).ToList();
                    return ToolResult.WithImages(string.Join("\n", hits.Select(h => h.Summary())), new[] { ContactSheet(cells) });
                });

            box.Add("view_asset", "See one library model: Roblox's own picture plus a 3/4 render with its exact size, and its full label (what it is, front direction, uses, colours, issues).",
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

            box.Add("search_sounds", "Find sound effects and music that are licensed for every game (only Roblox's own uploads and APM's music). Use the ids it returns in update_manifest sounds/music_id or Sounds.Play(id); never invent an id.",
                Object(Req("query", Str("e.g. 'chop', 'coin', 'level up', 'calm music'")), Opt("kind", Str("Only effects or only music", "effect", "music"))),
                async (args, ct) =>
                {
                    var hits = await ctx.Public.SearchAudioAsync(Args.Required(args, "query"), ct);
                    string kind = Args.Str(args, "kind");
                    if (kind == "effect") hits = hits.Where(h => !h.Type.Equals("Music", StringComparison.OrdinalIgnoreCase) && h.Seconds <= 20).ToList();
                    if (kind == "music") hits = hits.Where(h => h.Type.Equals("Music", StringComparison.OrdinalIgnoreCase) || h.Seconds > 20).ToList();
                    if (hits.Count == 0) return ToolResult.Ok("No licensed sounds match. Try simpler words (click, whoosh, coin, win, pop, music); the kit's built-in names (kit_reference juice) always work.");
                    ctx.Store.Mutate(s =>
                    {
                        foreach (var h in hits.Take(60)) s.Studio.LicensedAudio[h.Id] = Clip(h.Name, 60);
                        s.MarkStudio();
                    });
                    return ToolResult.Ok(string.Join("\n", hits.Take(30).Select(h => $"{h.Id} · {h.Name} · {h.Type} {h.Seconds:0.#} s · by {h.Creator}")));
                });

            // ─────────────── build and look ───────────────

            box.Add("build", "Compile the whole game and check it: every script's syntax and types (strict), the scene, the geometry, and the map report. Fix every error; warnings matter too. A clean build is snapshotted. It does not run the game: run_tests does.",
                Object(),
                async (_, ct) => ToolResult.Ok((await RunBuildAsync(ctx, gameId, ct)).Report));

            box.Add("run_tests", "Run the game in Roblox's real engine: the kit's own checks, every module loading, the whole server booting for 8 s (every runtime error with its file and line), the world checks and your specs. Takes about a minute. Run it after each meaningful change; the Build stage needs it to pass on the current build.",
                Object(),
                async (_, ct) =>
                {
                    var (run, message) = await RunEngineTestsAsync(ctx, gameId, ct);
                    return run == null ? ToolResult.Ok(message) : ToolResult.Ok(EngineTests.Describe(run, maxPassed: 40));
                });

            box.Add("render_scene", "See the world as a picture: 'map' (from above with the plan, grid, labels and the spawn's facing), 'overview' (from above), 'aerial', 'iso' (3/4), 'spawn' (what a player sees on joining), 'zone:<id>', an object id (close-up), or a camera \"x,y,z>x,y,z\". Library models appear as Roblox's own picture of them.",
                Object(Opt("view", Str("map, overview, aerial, iso, spawn, zone:<id>, an object id, or \"x,y,z>x,y,z\"")), Opt("labels", Bool("Draw object ids"))),
                args =>
                {
                    string view = Args.Str(args, "view", "iso");
                    byte[] png;
                    try { png = ctx.Render(gameId, view, 1280, 760, Args.Bool(args, "labels")); }
                    catch (InvalidOperationException ex) { return ToolResult.Error(ex.Message); }
                    return ToolResult.WithImages($"Rendered '{view}'. Judge it as a player: is it full, varied, readable, inviting? Is anything floating, buried, overlapping or out of scale?", new[] { png });
                });

            box.Add("map_report", "Measure the map: play area, how much is dressed and where the empty patches are (with coordinates), each zone's density, what the spawn faces, edges, landmarks, variety — a score out of 100 with specific fixes, plus the annotated map.",
                Object(Opt("picture", Bool("Include the map picture (default true)"))),
                args =>
                {
                    var compiled = ctx.Compile(gameId);
                    var analysis = MapReport.Analyze(ws.LoadScene(), compiled.Parts, compiled.Terrain, ws.LoadManifest());
                    if (!Args.Bool(args, "picture", true) || (compiled.Parts.Count == 0 && compiled.Terrain == null)) return ToolResult.Ok(analysis.Describe());
                    return ToolResult.WithImages(analysis.Describe(), new[] { ctx.RenderCompiled(gameId, compiled, "map", 1280, 760, labels: false) });
                });

            box.Add("diagnose_failure", "Explain a failing check or runtime error from the last run_tests: shows the check's own source (kit or game), the kit or game functions it calls, and for an error the code around its line. Use it before changing anything to fix a failure.",
                Object(Req("failure", Str("Part of the failing check's name or the error message, e.g. 'new profiles' or 'Forest:150'"))),
                args => ToolResult.Ok(Diagnose(ctx, gameId, Args.Required(args, "failure"))));

            box.Add("pacing_check", "Check the economy's pacing: give the steps a player goes through (each with its cost and the income per minute the player has when they start saving for it) and see how long each takes against what keeps kids playing (first buy within a minute, each early step a few minutes, later steps slowly longer).",
                Object(Req("steps", Arr(Object(Req("name", Str("e.g. 'Iron axe'")), Req("cost", Num("Price")), Req("income_per_minute", Num("Earnings per minute while saving for it"))), "In order"))),
                args => ToolResult.Ok(Pacing((args["steps"] as JArray ?? new JArray()).OfType<JObject>().Select(s => (Args.Str(s, "name"), Args.Num(s, "cost") ?? 0, Args.Num(s, "income_per_minute") ?? 0)).ToList())));

            box.Add("kit_reference", "Game Kit documentation: 'overview' (how a game is put together), 'modules' (every server/shared API with exact signatures), 'client' (the HUD API for your controllers, UI style), 'tags', 'templates', 'progression' (zones, rebirth, quests), 'world' (how to build a great map), 'luau' (conventions with examples), 'juice' (effects and sounds), 'testing' (run_tests and writing specs).",
                Object(Req("topic", Str("Topic", KitDocs.Topics))),
                args => ToolResult.Ok(KitDocs.For(Args.Required(args, "topic"))));

            box.Add("set_milestones", "Replace the milestone list (design and map plan first, then the world, the core loop, every must-have, polish).",
                Object(Req("milestones", StrList("Milestone titles, in order"))),
                args =>
                {
                    var titles = Args.Prose(args, "milestones").Take(20).ToList();
                    if (titles.Count == 0) return ToolResult.Error("give at least one milestone");
                    ctx.Update(gameId, g =>
                    {
                        var old = g.Milestones.ToDictionary(m => m.Title, m => m, StringComparer.OrdinalIgnoreCase);
                        g.Milestones = titles.Select((t, i) => old.TryGetValue(t, out var keep) ? new Milestone { MilestoneId = "m" + (i + 1), Title = t, Done = keep.Done, Notes = keep.Notes } : new Milestone { MilestoneId = "m" + (i + 1), Title = Clip(t, 160) }).ToList();
                    });
                    return ToolResult.Ok(string.Join("\n", ctx.Game(gameId)!.Milestones.Select(m => $"{m.MilestoneId}: {m.Title}")));
                });

            box.Add("update_milestone", "Mark a milestone done (or not) with a note of what was done.",
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
                        if (Args.Str(args, "notes") is { Length: > 0 } n) m.Notes = Clip(n, 400);
                    });
                    return found ? ToolResult.Ok("Milestone updated.") : ToolResult.Error("no milestone " + id);
                });

            box.Add("mark_feature_complete", "Declare every must-have in the brief built and working. Only accepted when the current files built clean, the map report has no blocking problems, every milestone is done and (where the engine can run) run_tests passed on this build.",
                Object(Req("notes", Str("How each must-have is met"))),
                args =>
                {
                    var problems = FeatureCompleteProblems(ctx, gameId);
                    if (problems.Count > 0) return ToolResult.Error("not yet:\n- " + string.Join("\n- ", problems));
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

        // ─────────────── build ───────────────

        public sealed record BuildRun(bool Ok, string Report, CompileResult Compile, List<GeometryIssue> Geometry, LuauAnalysis? Types, MapAnalysis Map);

        /// <summary>
        /// Compile, type-check, verify the geometry and measure the map; record the result in the dossier;
        /// snapshot and save the place on success. A clean build of files that have not changed since the last clean
        /// revision (on the same Game Kit) keeps that revision: it is the same game, so the engine run that passed
        /// on it still counts and nothing is run again for nothing.
        /// </summary>
        public static async Task<BuildRun> RunBuildAsync(StudioContext ctx, string gameId, CancellationToken ct)
        {
            var ws = ctx.Workspace(gameId);
            var before = ctx.Game(gameId)!.Build;
            bool unchanged = false;
            if (before.LastBuildOk && before.Revision > 0 && before.KitFingerprint == GameKit.Fingerprint)
            {
                var (a, c, r) = ws.DiffAgainst(before.Revision);
                unchanged = a.Count + c.Count + r.Count == 0;
            }
            var result = ctx.Compile(gameId);
            var scene = ws.LoadScene();
            var manifest = ws.LoadManifest();
            var geometry = result.Parts.Count > 0 ? GeometryVerifier.Verify(result.Parts, scene, manifest, result.Terrain) : new List<GeometryIssue>();
            LuauAnalysis? types = null;
            if (result.Ok && ctx.Luau != null)
            {
                try { types = await LuauAnalyzer.AnalyzeAsync(ctx.Luau, result.Scripts, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { types = new LuauAnalysis { Ran = false, Problem = ex.Message }; }
            }
            var map = MapReport.Analyze(scene, result.Parts, result.Terrain, manifest);
            var typeErrors = types?.Errors.ToList() ?? new List<LuauDiagnostic>();
            bool ok = result.Ok && !geometry.Any(g => g.IsError) && typeErrors.Count == 0;
            var errors = result.Errors.Concat(typeErrors.Select(d => "[types] " + d)).Concat(geometry.Where(g => g.IsError).Select(g => "[geometry] " + g)).ToList();
            var warnings = result.Warnings.Concat((types?.Warnings ?? Enumerable.Empty<LuauDiagnostic>()).Take(25).Select(d => "[types] " + d)).Concat(geometry.Where(g => !g.IsError).Select(g => "[geometry] " + g)).ToList();
            int revision = 0;
            var newAssets = new List<long>();
            bool newRevision = ok && !unchanged;
            ctx.Update(gameId, g =>
            {
                if (newRevision) g.Build.Revision++;
                if (ok) g.Build.KitFingerprint = GameKit.Fingerprint;
                revision = g.Build.Revision;
                g.Build.LastBuildUtc = ctx.Clock();
                g.Build.LastBuildOk = ok;
                g.Build.Errors = errors.Take(60).ToList();
                g.Build.Warnings = warnings.Take(60).ToList();
                g.Build.InstanceCount = result.InstanceCount;
                g.Build.ScriptCount = result.ScriptCount;
                g.Build.PlaceBytes = result.PlaceXml.LongLength;
                g.Build.TypeChecked = types?.Ran == true;
                g.Build.TypeCheckNote = types == null ? (ctx.Luau == null ? "no type checker in this studio" : "not type-checked: the build did not compile") : types.Problem;
                g.Build.MapScore = map.Score;
                g.Build.MapFindings = map.Findings.Where(f => f.Severity != "info").Select(f => f.ToString()).Take(20).ToList();
                if (ok)
                    foreach (var id in result.AssetsUsed.Keys)
                        if (!g.Build.AssetsUsed.Contains(id)) { g.Build.AssetsUsed.Add(id); newAssets.Add(id); }
            });
            if (ok)
            {
                if (newRevision) ws.Snapshot(revision);
                // Always: the place carries the products' current ids, which are not in the files.
                Core.OmniBloxStore.WriteAtomic(Path.Combine(ctx.Store.BuildDirectory(gameId), "place.rbxlx"), result.PlaceXml);
                foreach (var id in newAssets) ctx.Library.RecordUse(id, gameId, "placed");
                if (newRevision) ctx.Playtests.OnCleanBuild(gameId);
            }
            var sb = new StringBuilder();
            sb.AppendLine(ok ? $"BUILD OK — revision {revision}{(newRevision ? "" : " (no file changed since it was built)")}: {result.InstanceCount} instances, {result.ScriptCount} scripts, {result.Parts.Count} parts, {result.PlaceXml.Length / 1024} KB." : $"BUILD FAILED ({errors.Count} error(s)).");
            if (types == null) sb.AppendLine(ctx.Luau == null ? "Types: not checked (no type checker here)." : "Types: not checked until it compiles.");
            else if (!types.Ran) sb.AppendLine("Types: not checked — " + types.Problem);
            else sb.AppendLine($"Types: {typeErrors.Count} error(s), {types.Warnings.Count()} warning(s) (strict).");
            if (errors.Count > 0) sb.AppendLine("Errors:\n" + string.Join("\n", errors.Take(30).Select(e => "- " + e)));
            if (warnings.Count > 0) sb.AppendLine("Warnings:\n" + string.Join("\n", warnings.Take(20).Select(e => "- " + e)));
            sb.AppendLine($"Map: score {map.Score}/100" + (map.Errors.Any() ? " — blocking: " + string.Join(" | ", map.Errors.Select(f => f.Message).Take(3)) : map.Findings.Count > 0 ? " — " + string.Join(" | ", map.Findings.Where(f => f.Severity != "info").Select(f => f.Message).Take(2)) : "") + " (map_report for the details).");
            if (ok)
            {
                var game = ctx.Game(gameId)!;
                sb.AppendLine(game.Build.LastEngineRun is { } last && last.Revision == revision && last.Ok
                    ? "Engine tests: passed on this build."
                    : "Engine tests: not run on this build — run_tests runs the game in Roblox's engine.");
            }
            return new BuildRun(ok, sb.ToString(), result, geometry, types, map);
        }

        private static readonly ConcurrentDictionary<string, SemaphoreSlim> engineGates = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, DateTime> lastEngineRun = new(StringComparer.Ordinal);

        /// <summary>An engine test run is going for this game right now (one at a time per game).</summary>
        public static bool EngineRunning(string gameId) => engineGates.TryGetValue(gameId, out var gate) && gate.CurrentCount == 0;

        /// <summary>
        /// Builds (unless the caller just built clean: <paramref name="built"/>), then runs the engine tests on that
        /// build and records the run. Null run (with the reason) when they cannot run: no key, no experience slot,
        /// a failed build, or Roblox unreachable.
        /// </summary>
        public static async Task<(EngineRun? Run, string Message)> RunEngineTestsAsync(StudioContext ctx, string gameId, CancellationToken ct, BuildRun? built = null)
        {
            if (!ctx.Cloud.HasKey) return (null, "Engine tests need the Roblox API key, which is not set yet; they run before launch. build covers the static checks meanwhile.");
            var slot = Slots.Ensure(ctx, gameId);
            if (slot == null) return (null, "Engine tests wait for a free experience slot (the owner has been asked for one). build covers the static checks meanwhile.");
            var gate = engineGates.GetOrAdd(gameId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                // Roblox allows a few Luau execution tasks a minute per key: space the runs.
                if (lastEngineRun.TryGetValue(gameId, out var last) && ctx.Clock() - last < TimeSpan.FromSeconds(15))
                    await Task.Delay(TimeSpan.FromSeconds(15) - (ctx.Clock() - last), ct);
                var build = built is { Ok: true } ? built : await RunBuildAsync(ctx, gameId, ct);
                if (!build.Ok) return (null, "Fix the build first — the engine tests run on a clean build:\n" + build.Report);
                EngineRun run;
                try { run = await EngineTests.RunAsync(ctx, gameId, slot, ct); }
                catch (RobloxApiException ex) when (ex.IsAuth)
                {
                    ctx.Store.Mutate(s => s.FileOwnerTask(OwnerTaskKind.ApiKey, "apikey-engine", "Let the Roblox API key run engine tests",
                        $"Running the game in Roblox's engine was refused ({ex.Message}). The key needs universe-places:write (to save a test version) and luau-execution-session:write (to run it) on experience {slot.UniverseId}.", gameId, ctx.Clock()));
                    return (null, "The engine tests were refused by Roblox (the API key lacks a permission; the owner has been asked). Nothing is wrong with the game for that.");
                }
                catch (Exception ex) when (ex is RobloxApiException or TimeoutException or HttpRequestException or RobloxKeyMissingException
                    || ex is OperationCanceledException && !ct.IsCancellationRequested)
                {
                    return (null, "The engine tests could not run (" + ex.Message + "). Nothing is wrong with the game for that; try again later.");
                }
                finally
                {
                    lastEngineRun[gameId] = ctx.Clock();
                }
                ctx.Update(gameId, g =>
                {
                    g.Build.LastEngineRun = run;
                    if (run.Ok) g.Publish.EngineTestedRevision = run.Revision;
                });
                if (run.KitFailed) ReportKitFailure(ctx, gameId, run);
                return (run, run.Summary);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>The kit's own checks failed before any game code ran: the studio is broken, not the game. Tell the owner once.</summary>
        internal static void ReportKitFailure(StudioContext ctx, string gameId, EngineRun run)
        {
            var failures = run.Results.Where(r => !r.Ok && r.Phase == "kit").Select(r => $"{r.Name}: {r.Detail}").ToList();
            ctx.Store.Mutate(s => s.FileOwnerTask(OwnerTaskKind.Escalation, "kit-failure", "The Game Kit's own engine checks are failing",
                "These checks run before any game code is loaded, so this is a bug in OmniBlox's Game Kit, not in the game. The game is not blamed for it.\n- " + string.Join("\n- ", failures.Take(6)),
                gameId, ctx.Clock()));
            ctx.Event(EventLevel.Error, "kit.failure", "The Game Kit's own engine checks failed: " + string.Join("; ", failures.Take(2)), gameId);
        }

        /// <summary>What still stands between the game and "feature complete".</summary>
        public static List<string> FeatureCompleteProblems(StudioContext ctx, string gameId)
        {
            var game = ctx.Game(gameId)!;
            var problems = new List<string>();
            if (!game.Build.LastBuildOk || game.Build.Revision == 0) problems.Add("the latest build failed or there is none: run build and fix every error");
            else
            {
                var (added, changed, removed) = ctx.Workspace(gameId).DiffAgainst(game.Build.Revision);
                if (added.Count + changed.Count + removed.Count > 0) problems.Add($"files changed since the last clean build (r{game.Build.Revision}): run build again");
            }
            bool obby = string.Equals(ctx.Workspace(gameId).LoadManifest().Template, "obby", StringComparison.OrdinalIgnoreCase);
            var mapErrors = game.Build.MapFindings.Where(f => f.StartsWith("[error]", StringComparison.Ordinal)).ToList();
            if (mapErrors.Count > 0) problems.Add("the map report has blocking problems: " + string.Join(" | ", mapErrors.Take(3)));
            if (!obby && (game.Build.MapScore ?? 0) < StageGates.MinMapScore) problems.Add($"the map scores {game.Build.MapScore ?? 0}/100; get it to {StageGates.MinMapScore}+ (map_report lists what to fix)");
            var open = game.Milestones.Where(m => !m.Done).Select(m => m.MilestoneId).ToList();
            if (open.Count > 0) problems.Add("milestones not done: " + string.Join(", ", open));
            if (StageGates.EngineAvailable(ctx, game) && !StageGates.EnginePassed(game))
            {
                var run = game.Build.LastEngineRun;
                problems.Add(run == null || run.Revision != game.Build.Revision ? "run_tests has not run on this build" : "run_tests failed on this build: " + run.Summary);
            }
            return problems;
        }

        // ─────────────── diagnosis ───────────────

        private static readonly Regex Call = new(@"\b([A-Z][A-Za-z0-9_]*)\.([A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled);

        internal static string Diagnose(StudioContext ctx, string gameId, string query)
        {
            var game = ctx.Game(gameId)!;
            var run = game.Build.LastEngineRun;
            if (run == null) return "There is no engine test run yet: run run_tests first.";
            var ws = ctx.Workspace(gameId);
            var sb = new StringBuilder();
            var cases = run.Results.Where(r => !r.Ok && (r.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || (r.Detail ?? "").Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
            var errors = run.Errors.Where(e => e.Message.Contains(query, StringComparison.OrdinalIgnoreCase) || $"{e.File}:{e.Line}".Contains(query, StringComparison.OrdinalIgnoreCase) || (e.Source ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            if (cases.Count == 0 && errors.Count == 0)
                return "Nothing in the last run (r" + run.Revision + ") matches that. Failing: " + string.Join("; ", run.Results.Where(r => !r.Ok).Select(r => r.Name).Concat(run.Errors.Select(e => Clip(e.Message, 80))).Take(10));

            foreach (var c in cases.Take(2))
            {
                sb.AppendLine($"FAILING CHECK [{c.Phase}/{c.Owner}] {c.Name}");
                sb.AppendLine("Result: " + c.Detail);
                int dot = c.Name.IndexOf(" · ", StringComparison.Ordinal);
                if (dot > 0)
                {
                    string module = c.Name[..dot], caseName = c.Name[(dot + 3)..];
                    var (path, source) = SpecSource(ws, module, c.Owner);
                    if (source != null)
                    {
                        string block = ExtractCase(source, caseName);
                        sb.AppendLine($"The check ({path}):").AppendLine(block);
                        foreach (var (name, code) in CalledFunctions(ws, block).Take(5))
                            sb.AppendLine($"It calls {name}:").AppendLine(code);
                    }
                }
                if (c.Owner == "kit" && c.Phase == "kit")
                    sb.AppendLine("This is one of the kit's own control checks (run before any game code): if it fails, the kit itself is at fault and the owner has been told. Do not change game code to satisfy it.");
                sb.AppendLine();
            }
            foreach (var e in errors.Take(3))
            {
                sb.AppendLine($"RUNTIME ERROR [{e.Phase}] {e.Message}");
                if (e.Trace is { Length: > 0 } trace) sb.AppendLine("Trace: " + trace);
                if (e.File != null && e.Line is int line && ws.ReadFile(e.File) is { } text)
                {
                    var lines = text.Split('\n');
                    int from = Math.Max(1, line - 12), to = Math.Min(lines.Length, line + 12);
                    sb.AppendLine($"{e.File} around line {line}:");
                    for (int i = from; i <= to; i++) sb.Append(i == line ? ">>" : "  ").Append(i.ToString(CultureInfo.InvariantCulture).PadLeft(4)).Append("  ").AppendLine(lines[i - 1].TrimEnd('\r'));
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static (string Path, string? Source) SpecSource(GameWorkspace ws, string module, string owner)
        {
            var kit = GameKit.Files.FirstOrDefault(k => k.Path.Equals($"Server/Specs/{module}.luau", StringComparison.OrdinalIgnoreCase));
            if (owner == "kit" && kit != null) return ("GameKit/" + kit.Path, kit.Source);
            var file = ws.ListFiles().FirstOrDefault(f => f.Path.StartsWith("src/tests/", StringComparison.OrdinalIgnoreCase) && Path.GetFileNameWithoutExtension(f.Path).Equals(module, StringComparison.OrdinalIgnoreCase));
            if (file != null) return (file.Path, ws.ReadFile(file.Path));
            return kit != null ? ("GameKit/" + kit.Path, kit.Source) : ("?", null);
        }

        /// <summary>The source of one case of a spec table: from its ["name"] key to the next key.</summary>
        internal static string ExtractCase(string source, string caseName)
        {
            var lines = source.Split('\n');
            int start = Array.FindIndex(lines, l => l.Contains("[\"" + caseName + "\"]", StringComparison.Ordinal) || l.Contains("['" + caseName + "']", StringComparison.Ordinal));
            if (start < 0) return "(the check's source was not found)";
            int indent = lines[start].Length - lines[start].TrimStart().Length;
            var sb = new StringBuilder();
            for (int i = start; i < lines.Length && i < start + 70; i++)
            {
                if (i > start)
                {
                    string trimmed = lines[i].TrimStart();
                    int ind = lines[i].Length - trimmed.Length;
                    if (ind <= indent && (trimmed.StartsWith("[\"", StringComparison.Ordinal) || trimmed.StartsWith("['", StringComparison.Ordinal) || trimmed.StartsWith("}", StringComparison.Ordinal) || trimmed.StartsWith("spec[", StringComparison.Ordinal))) break;
                }
                sb.Append((i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(4)).Append("  ").AppendLine(lines[i].TrimEnd('\r'));
            }
            return sb.ToString();
        }

        private static IEnumerable<(string Name, string Code)> CalledFunctions(GameWorkspace ws, string block)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var sources = GameKit.Files.Select(k => ("GameKit/" + k.Path, k.Source))
                .Concat(ws.ListFiles().Where(f => f.Path.EndsWith(".luau", StringComparison.OrdinalIgnoreCase)).Select(f => (f.Path, ws.ReadFile(f.Path) ?? ""))).ToList();
            foreach (Match m in Call.Matches(block))
            {
                string name = m.Groups[1].Value + "." + m.Groups[2].Value;
                if (m.Groups[1].Value is "t" or "Vector3" or "CFrame" or "Color3" or "UDim2" or "Instance" or "TweenInfo" || !seen.Add(name)) continue;
                foreach (var (path, text) in sources)
                {
                    int at = text.IndexOf("function " + name + "(", StringComparison.Ordinal);
                    if (at < 0) continue;
                    var lines = text[at..].Split('\n');
                    var sb = new StringBuilder();
                    int startLine = text[..at].Count(c => c == '\n') + 1;
                    for (int i = 0; i < lines.Length && i < 50; i++)
                    {
                        sb.Append((startLine + i).ToString(CultureInfo.InvariantCulture).PadLeft(4)).Append("  ").AppendLine(lines[i].TrimEnd('\r'));
                        if (i > 0 && lines[i].TrimEnd('\r') == "end") break;
                    }
                    yield return ($"{name} ({path})", sb.ToString());
                    break;
                }
            }
        }

        // ─────────────── pacing ───────────────

        internal static string Pacing(List<(string Name, double Cost, double IncomePerMinute)> steps)
        {
            if (steps.Count == 0) return "Give the steps in order: [{name, cost, income_per_minute}].";
            var sb = new StringBuilder();
            double total = 0, previous = 0;
            var notes = new List<string>();
            for (int i = 0; i < steps.Count; i++)
            {
                var (name, cost, income) = steps[i];
                double minutes = income > 0 ? cost / income : double.PositiveInfinity;
                total += minutes;
                string verdict = "";
                if (double.IsInfinity(minutes)) verdict = "never: no income";
                else if (i == 0 && minutes > 1.5) verdict = "too slow for a first buy (aim under 1 minute)";
                else if (i < 4 && minutes > 5) verdict = "slow for an early step (aim 1-5 minutes)";
                else if (i > 0 && previous > 0 && minutes < previous * 0.7) verdict = "quicker than the step before: progress should slow gently";
                else if (i > 0 && previous > 0 && minutes > previous * 4) verdict = "a big wall after the step before (more than 4×)";
                else if (minutes < 0.15) verdict = "almost instant: feels unearned";
                sb.AppendLine($"{i + 1}. {name}: {cost:0.#} at {income:0.#}/min → {Minutes(minutes)} (total {Minutes(total)})" + (verdict.Length > 0 ? " — " + verdict : ""));
                if (verdict.Length > 0) notes.Add(name);
                previous = minutes;
            }
            sb.AppendLine(notes.Count == 0 ? "Pacing looks healthy." : $"Adjust: {string.Join(", ", notes)}.");
            sb.AppendLine("Targets: first purchase < 1 min; first upgrade 2-3 min; next zone 8-15 min; first rebirth 30-60 min.");
            return sb.ToString();
        }

        private static string Minutes(double m) => double.IsInfinity(m) ? "∞" : m < 1 ? $"{m * 60:0} s" : m < 120 ? $"{m:0.#} min" : $"{m / 60:0.#} h";

        // ─────────────── helpers ───────────────

        private static List<AssetRecord> Search(StudioContext ctx, JObject args, int limit) => ctx.Library.Search(new AssetSearchQuery
        {
            Text = Args.Required(args, "query"),
            Category = Args.Str(args, "category") is { Length: > 0 } c ? c : null,
            Style = Args.Str(args, "style") is { Length: > 0 } s ? s : null,
            MinHeight = Args.Num(args, "min_height") is double a ? (float)a : null,
            MaxHeight = Args.Num(args, "max_height") is double b ? (float)b : null,
            MaxFootprint = Args.Num(args, "max_footprint") is double f ? (float)f : null,
            Limit = limit,
        });

        private static bool IsLicensed(StudioContext ctx, string value)
        {
            string id = value.Trim().Replace("rbxassetid://", "", StringComparison.OrdinalIgnoreCase);
            if (!long.TryParse(id, out long n)) return Regex.IsMatch(id, "^[a-z]+$"); // a kit sound name ("coin")
            return KitSounds.Contains(n) || ctx.Store.Read(s => s.Studio.LicensedAudio.ContainsKey(n));
        }

        /// <summary>Up to 12 model pictures in a grid with their ids and sizes, as one PNG.</summary>
        internal static byte[] ContactSheet(List<(long Id, byte[]? Png, string Caption)> cells)
        {
            const int cell = 220, columns = 4;
            int rows = Math.Max(1, (cells.Count + columns - 1) / columns);
            using var surface = SKSurface.Create(new SKImageInfo(columns * cell, rows * (cell + 26), SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(236, 240, 246));
            using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default, 14);
            using var ink = new SKPaint { Color = new SKColor(30, 34, 44), IsAntialias = true };
            using var frame = new SKPaint { Color = new SKColor(200, 206, 218), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
            for (int i = 0; i < cells.Count; i++)
            {
                float x = i % columns * cell, y = i / columns * (cell + 26);
                canvas.DrawRect(new SKRect(x + 4, y + 4, x + cell - 4, y + cell + 22), frame);
                if (cells[i].Png is { } png)
                {
                    using var bitmap = SKBitmap.Decode(png);
                    if (bitmap != null)
                    {
                        using var image = SKImage.FromBitmap(bitmap);
                        float scale = Math.Min((cell - 16f) / bitmap.Width, (cell - 16f) / bitmap.Height);
                        float w = bitmap.Width * scale, h = bitmap.Height * scale;
                        canvas.DrawImage(image, new SKRect(x + (cell - w) / 2, y + 8 + (cell - 16 - h) / 2, x + (cell + w) / 2, y + 8 + (cell - 16 + h) / 2), sampling);
                    }
                }
                canvas.DrawText(cells[i].Caption, x + cell / 2f, y + cell + 16, SKTextAlign.Center, font, ink);
            }
            using var snapshot = surface.Snapshot();
            using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
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
            else if (existing == null) throw new ArgumentException("position must be [x, y, z] (or [x, z] to stand on the ground)");
            if (spec["size"] != null) o.Size = Args.Vec3(spec, "size") ?? throw new ArgumentException("size must be [x, y, z]");
            if (Args.Num(spec, "yaw") is double yaw) o.Yaw = (float)yaw;
            if (spec["rotation"] != null) o.Rotation = Args.Vec3(spec, "rotation");
            if (Args.Num(spec, "scale") is double scale) o.Scale = (float)Math.Clamp(scale, 0.25, 4);
            if (Args.Str(spec, "shape") is { Length: > 0 } shape) o.Shape = shape;
            if (Args.Str(spec, "color") is { Length: > 0 } color) { PlaceCompiler.ParseColor(color); o.Color = color; }
            if (spec["tint"] != null)
            {
                string tint = Args.Str(spec, "tint");
                if (tint.Length > 0) PlaceCompiler.ParseColor(tint);
                o.Tint = tint.Length > 0 ? tint : null;
            }
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
            if (o.Position.Length != 3 || o.Position.Any(v => float.IsNaN(v) || MathF.Abs(v) > 100_000)) throw new ArgumentException("position is out of range");
            if (string.IsNullOrWhiteSpace(o.Name)) o.Name = o.Id;
            return o;
        }

        internal static string Describe(SceneObject o) =>
            $"{o.Id} [{o.Kind}] {o.Name}" + (o.Folder.Length > 0 ? $" in {o.Folder}" : "") + $" at {V(o.Position)}" +
            (o.Size != null ? $" size {V(o.Size)}" : "") + (o.AssetId is long a ? $" asset {a}" + (o.Scale != 1 ? $" ×{o.Scale:0.##}" : "") : "") +
            (o.Tint != null ? $" tint {o.Tint}" : "") + (o.Yaw != 0 ? $" yaw {o.Yaw:0}" : "") + (o.Tags.Count > 0 ? $" tags {string.Join(",", o.Tags)}" : "") +
            (o.Config.Count > 0 ? " config " + string.Join(",", o.Config.Select(kv => $"{kv.Key}={(kv.Value is JToken t ? t.ToString(Formatting.None) : kv.Value)}")) : "");

        private static string V(float[]? v) => v == null ? "?" : "[" + string.Join(", ", v.Select(x => x.ToString("0.##", CultureInfo.InvariantCulture))) + "]";

        internal static string Shorten(string s) => new string(s.Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Trim() is { Length: > 0 } t ? Clip(t, 40) : "Model";

        private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
    }
}
