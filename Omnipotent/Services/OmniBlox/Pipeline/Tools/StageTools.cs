using System.Text;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniBlox.Agents;
using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Insights;
using Omnipotent.Services.OmniBlox.Marketing;
using Omnipotent.Services.OmniBlox.Rbx;
using static Omnipotent.Services.OmniBlox.Agents.Schema;

namespace Omnipotent.Services.OmniBlox.Pipeline.Tools
{
    /// <summary>The playtest reviewer's tools: look, read, measure, report, decide. It cannot change the game.</summary>
    public static class ReviewTools
    {
        public sealed class Verdict
        {
            public bool? Pass { get; set; }
            public string Summary { get; set; } = "";
            public List<TestIssue> Issues { get; } = new();
        }

        public static readonly string[] Looking = { "render_scene", "scene_summary", "map_report", "list_files", "read_file", "search_code", "view_asset", "get_manifest", "kit_reference", "pacing_check", "playbook_search", "design_world" };

        /// <summary>What a reviewer scores (1-10) before it may pass a build.</summary>
        public static readonly string[] Aspects = { "first_impression", "world_beauty", "world_fullness", "clear_goal", "fun_loop", "progression" };

        public static AgentToolbox Create(StudioContext ctx, string gameId, Verdict verdict)
        {
            var box = BuildTools.Create(ctx, gameId).Subset(Looking);
            box.Add("report_issue", "Report one problem found in review: what a player would hit, where, and what to change.",
                Object(Req("severity", Str("error = must fix before launch (broken, confusing, unfinished or no fun); warning = should fix", "error", "warning")), Req("area", Str("e.g. spawn, first minute, zone 2, shop, HUD, visuals, economy")), Req("message", Str("What is wrong, where, and what to do"))),
                args =>
                {
                    string message = Args.Required(args, "message");
                    string area = Args.Str(args, "area");
                    string severity = Args.Str(args, "severity", "warning") == "error" ? "error" : "warning";
                    if (verdict.Issues.FirstOrDefault(i => i.Area.Equals(area, StringComparison.OrdinalIgnoreCase) && i.Message.Equals(message, StringComparison.OrdinalIgnoreCase)) is { } same)
                    {
                        if (same.Severity == severity) return ToolResult.Ok("Already noted.");
                        same.Severity = severity;
                        return ToolResult.Ok($"Changed to {severity}.");
                    }
                    verdict.Issues.Add(new TestIssue { Severity = severity, Area = area, Message = message });
                    return ToolResult.Ok($"Noted ({verdict.Issues.Count} so far).");
                });
            box.Add("verdict", "Pass or fail the build, with your scores (1-10, against the anchors in your instructions) for: first_impression (the spawn's first ten seconds), world_beauty, world_fullness, clear_goal (a child knows what to do), fun_loop (the core action feels good), progression (there is always a next goal). A pass needs no reported error, every score at least 6 and an average of at least 7.",
                Object(Req("pass", Bool("True only if nothing is broken and a child could pick it up and have fun")), Req("summary", Str("Why, in two or three sentences")),
                    Opt("scores", Object(Aspects.Select(a => Req(a, Int("1-10"))).ToArray()))),
                args =>
                {
                    bool pass = Args.Bool(args, "pass");
                    var scores = Aspects.ToDictionary(a => a, a => args["scores"] is JObject sc && sc[a] != null ? Math.Clamp(Args.Int(sc, a, 0), 0, 10) : 0);
                    bool scored = scores.Values.All(v => v > 0);
                    if (pass && verdict.Issues.Any(i => i.Severity == "error"))
                        return ToolResult.Error("you reported errors (must-fix problems): a build with them cannot pass. Fail it, or downgrade an issue you no longer think must be fixed by reporting it again as a warning and explaining why.");
                    if (pass && !scored)
                        return ToolResult.Error("score all six aspects (scores: " + string.Join(", ", Aspects) + ") before passing a build.");
                    if (pass && (scores.Values.Min() < 6 || scores.Values.Average() < 7))
                        return ToolResult.Error($"your scores ({string.Join(", ", scores.Select(kv => $"{kv.Key} {kv.Value}"))}) are a fail: a pass needs every aspect at least 6 and an average of at least 7. Report what drags the low ones down and fail the build.");
                    verdict.Pass = pass;
                    verdict.Summary = Args.Required(args, "summary") + (scored ? " Scores: " + string.Join(", ", scores.Select(kv => $"{kv.Key.Replace('_', ' ')} {kv.Value}")) + "." : "");
                    return ToolResult.Ok("Verdict recorded. Now call finish_cycle.");
                });
            return box;
        }
    }

    /// <summary>The launch marketer's tools (the maintainer reuses the visuals and store-page ones).</summary>
    public static class LaunchTools
    {
        public static AgentToolbox Create(StudioContext ctx, string gameId)
        {
            var box = BuildTools.Create(ctx, gameId).Subset("render_scene", "scene_summary", "view_asset", "get_manifest", "playbook_search");
            AddPlacedAssets(box, ctx, gameId);
            AddStorePage(box, ctx, gameId, "set_store_page");
            AddVisuals(box, ctx, gameId);
            AddProducts(box, ctx, gameId);
            AddCompliance(box, ctx, gameId);
            box.Add("request_launch", "Hand the game to the launch sequence: a final build and engine run, then the owner's launch approval (of this build, store page and products), and only then products, the store page, thumbnails, publishing and going public. Needs the store page, icon, at least one thumbnail and a passing compliance check.",
                Object(),
                _ =>
                {
                    var game = ctx.Game(gameId)!;
                    if (!StageGates.MarketingReady(game))
                        return ToolResult.Error("not ready: " + string.Join("; ", StageGates.Checklist(game, ctx).Where(c => !c.Met && !c.Text.Contains("public") && !c.Text.Contains("owner")).Select(c => c.Text)));
                    ctx.Update(gameId, g => g.Publish.LaunchRequestedUtc = ctx.Clock());
                    return ToolResult.Ok("Launch requested. Call finish_cycle with stage_complete true.");
                });
            return box;
        }

        internal static void AddPlacedAssets(AgentToolbox box, StudioContext ctx, string gameId) =>
            box.Add("placed_assets", "The library models actually placed in this game (candidates to feature in thumbnails).",
                Object(),
                _ =>
                {
                    var ids = ctx.Game(gameId)!.Build.AssetsUsed;
                    if (ids.Count == 0) return ToolResult.Ok("No library models are placed in this game.");
                    return ToolResult.Ok(string.Join("\n", ids.Select(id => ctx.Library.Get(id)?.Summary() ?? $"#{id}")));
                });

        internal static void AddStorePage(AgentToolbox box, StudioContext ctx, string gameId, string toolName) =>
            box.Add(toolName, "Write the store page: title (max 50 characters, catchy and searchable), description (max 1000: hook, what you do, features, controls, updates; no links), genre and search keywords.",
                Object(Opt("title", Str("Title")), Opt("description", Str("Description")), Opt("genre", Str("Genre")), Opt("keywords", StrList("Search keywords"))),
                args =>
                {
                    ctx.Update(gameId, g =>
                    {
                        if (Args.Str(args, "title") is { Length: > 0 } t) g.Store.Title = t.Trim();
                        if (Args.Str(args, "description") is { Length: > 0 } d) g.Store.Description = d.Trim();
                        if (Args.Str(args, "genre") is { Length: > 0 } ge) g.Store.Genre = ge.Trim();
                        if (args["keywords"] != null) g.Store.Keywords = Args.List(args, "keywords").Take(20).ToList();
                        g.Store.ComplianceOk = null; // changed text must be re-checked
                        g.Store.Applied = false;
                    });
                    var s = ctx.Game(gameId)!.Store;
                    var warn = new List<string>();
                    if (s.Title.Length > ComplianceChecker.TitleMax) warn.Add($"title is {s.Title.Length} characters (max {ComplianceChecker.TitleMax})");
                    if (s.Description.Length > ComplianceChecker.DescriptionMax) warn.Add($"description is {s.Description.Length} characters (max {ComplianceChecker.DescriptionMax})");
                    return ToolResult.Ok($"Store page saved: \"{s.Title}\" ({s.Description.Length} characters). Run compliance_check again." + (warn.Count > 0 ? " Problems: " + string.Join("; ", warn) : ""));
                });

        internal static void AddVisuals(AgentToolbox box, StudioContext ctx, string gameId)
        {
            ArtSpec Spec(JObject args, int renderW, int renderH)
            {
                var result = ctx.Compile(gameId);
                if (result.Parts.Count == 0 && result.Terrain == null) throw new InvalidOperationException("the world is empty; nothing to show");
                // The store look: the real world, rendered vivid (stronger colour, a vignette, no overlays).
                byte[] render = ctx.RenderCompiled(gameId, result, Args.Str(args, "view", "aerial"), renderW, renderH, labels: false, caption: false, marketing: true);
                var featured = new List<byte[]>();
                foreach (var idText in Args.List(args, "featured_asset_ids").Take(3))
                {
                    if (!long.TryParse(idText, out long id)) continue;
                    if (!ctx.Game(gameId)!.Build.AssetsUsed.Contains(id)) throw new ArgumentException($"asset {id} is not in this game; only feature models players will really find");
                    string path = ctx.Library.ThumbnailPath(id);
                    if (File.Exists(path)) featured.Add(File.ReadAllBytes(path));
                }
                var theme = ctx.Workspace(gameId).LoadManifest().Theme;
                var accent = Args.Str(args, "accent") is { Length: > 0 } a ? PlaceCompiler.ParseColor(a)
                    : theme.TryGetValue("Accent", out var themed) && themed.Length > 0 ? PlaceCompiler.ParseColor(themed)
                    : new RbxColor3uint8(76, 175, 255);
                return new ArtSpec
                {
                    Headline = Args.Required(args, "headline"),
                    Subtitle = Args.Str(args, "subtitle") is { Length: > 0 } sub ? sub : null,
                    Badge = Args.Str(args, "badge") is { Length: > 0 } b ? b : null,
                    SceneRender = render,
                    Featured = featured,
                    Accent = accent,
                };
            }

            box.Add("make_icon", "Make the 512×512 game icon from the real world in the bright store style (vivid colour, sunburst, chunky lettering): a render from 'view', an optional featured model that is in the game as a die-cut sticker, and a short headline readable on a phone (or none).",
                Object(Req("headline", Str("1-3 words")), Opt("view", Str("What to show: aerial (default), iso, spawn, zone:<id>, an object id (close-up) or a camera \"x,y,z>x,y,z\"")), Opt("featured_asset_ids", StrList("Up to 1 placed model to feature")), Opt("badge", Str("Optional tiny badge, e.g. NEW")), Opt("accent", Str("Accent colour #RRGGBB (default: the game's theme accent, made vivid)"))),
                args =>
                {
                    byte[] png = ctx.Marketing.MakeIcon(Spec(args, 1024, 1024));
                    string path = Path.Combine(ctx.Store.MediaDirectory(gameId), "icon.png");
                    OmniBloxStore.WriteAtomic(path, png);
                    ctx.Update(gameId, g => g.Store.IconFile = "icon.png");
                    return ToolResult.WithImages("Icon saved. Look at it: is the headline crisp and the focal point clear at phone size? Remake it if not.", new[] { png });
                });

            box.Add("make_thumbnail", "Make a 1920×1080 store thumbnail from the real world: a render, a big headline, a subtitle and up to 3 models that are really in the game. 'slot' 1-3 keeps several variants.",
                Object(Req("headline", Str("Big text, 1-5 words")), Opt("subtitle", Str("Short line")), Opt("view", Str("What to show: aerial (default), iso, spawn, zone:<id>, an object id (close-up) or a camera \"x,y,z>x,y,z\"")), Opt("featured_asset_ids", StrList("Placed models to feature")),
                    Opt("badge", Str("Optional badge")), Opt("accent", Str("Accent colour #RRGGBB (default: the game's theme accent, made vivid)")), Opt("slot", Int("1-3 (default 1)"))),
                args =>
                {
                    byte[] png = ctx.Marketing.MakeThumbnail(Spec(args, 1920, 1080));
                    int slot = Math.Clamp(Args.Int(args, "slot", 1), 1, 3);
                    string file = $"thumb{slot}.png";
                    OmniBloxStore.WriteAtomic(Path.Combine(ctx.Store.MediaDirectory(gameId), file), png);
                    ctx.Update(gameId, g =>
                    {
                        if (!g.Store.ThumbnailFiles.Contains(file)) g.Store.ThumbnailFiles.Add(file);
                        g.Store.ThumbnailFiles = g.Store.ThumbnailFiles.OrderBy(f => f).ToList();
                    });
                    return ToolResult.WithImages($"Thumbnail {slot} saved.", new[] { png });
                });

            box.Add("make_trailer", "Make a short trailer (MP4) of camera moves through the real world. Shots: kind = orbit, flyover, title or an object id; seconds 2-8; optional caption.",
                Object(Req("shots", Arr(Object(Req("kind", Str("orbit, flyover, title or an object id")), Opt("seconds", Num("2-8")), Opt("caption", Str("Caption"))), "2-6 shots"))),
                async (args, ct) =>
                {
                    var result = ctx.Compile(gameId);
                    var shots = (args["shots"] as JArray ?? new JArray()).OfType<JObject>().Take(6)
                        .Select(s => new TrailerShot { Kind = Args.Str(s, "kind", "orbit"), Seconds = Math.Clamp(Args.Num(s, "seconds") ?? 4, 2, 8), Caption = Args.Str(s, "caption") is { Length: > 0 } c ? c : null })
                        .ToList();
                    string title = ctx.Game(gameId)!.DisplayTitle;
                    string output = Path.Combine(ctx.Store.MediaDirectory(gameId), "trailer.mp4");
                    var (path, message) = await ctx.Marketing.MakeTrailerAsync(ctx.RenderWorldFor(gameId, result), title, shots, output, ct);
                    if (path != null) ctx.Update(gameId, g => g.Store.TrailerFile = "trailer.mp4");
                    return path != null ? ToolResult.Ok("Trailer saved: " + message) : ToolResult.Ok("No trailer: " + message);
                });
        }

        internal static void AddProducts(AgentToolbox box, StudioContext ctx, string gameId) =>
            box.Add("set_products", "Set the game's products (passes and developer products) with fair prices and clear descriptions. Existing products keep their Roblox ids by key.",
                Object(Req("products", Arr(Object(Req("key", Str("code id")), Req("kind", Str("GamePass or DeveloperProduct", "GamePass", "DeveloperProduct")), Req("name", Str("Shop name, max 50")),
                    Req("description", Str("Exactly what the player gets")), Req("price", Int("Robux")), Req("grant", Str("currency:N, multiplier:N, speed:N, flag:NAME, item:NAME or skipstage"))), "Products"))),
                args =>
                {
                    var template = ctx.Game(gameId)!.Brief?.Template ?? "";
                    var problems = new List<string>();
                    var list = new List<ProductRecord>();
                    foreach (var p in (args["products"] as JArray ?? new JArray()).OfType<JObject>().Take(10))
                    {
                        string key = Args.Str(p, "key").ToLowerInvariant();
                        string grant = Args.Str(p, "grant").Trim();
                        string grantKind = grant.Split(':')[0].ToLowerInvariant();
                        string kind = Args.Str(p, "kind");
                        int price = Args.Int(p, "price", 0);
                        if (key.Length == 0 || !key.All(c => char.IsLetterOrDigit(c) || c == '_')) problems.Add($"bad key '{key}'");
                        if (!ResearchTools.Grants.Contains(grantKind)) problems.Add($"{key}: unknown grant '{grant}'");
                        if (grantKind == "skipstage" && template != "obby") problems.Add("skipstage only works in an obby");
                        if (grantKind is "multiplier" or "speed" or "flag" && kind != "GamePass") problems.Add($"{key}: permanent perks must be game passes");
                        if (price < 1 || price > 10000) problems.Add($"{key}: price must be 1-10000");
                        list.Add(new ProductRecord
                        {
                            Key = key,
                            Kind = kind == "GamePass" ? ProductKind.GamePass : ProductKind.DeveloperProduct,
                            Name = Args.Str(p, "name"),
                            Description = Args.Str(p, "description"),
                            PriceRobux = price,
                            Grant = grant,
                        });
                    }
                    if (list.Select(x => x.Key).Distinct().Count() != list.Count) problems.Add("keys must be unique");
                    if (problems.Count > 0) return ToolResult.Error("products not saved:\n- " + string.Join("\n- ", problems));
                    ctx.Update(gameId, g =>
                    {
                        var old = g.Products.ToDictionary(x => x.Key);
                        foreach (var p in list)
                            if (old.TryGetValue(p.Key, out var prev) && prev.Kind == p.Kind) { p.RobloxId = prev.RobloxId; p.SyncedUtc = null; }
                        g.Products = list;
                        g.Store.ComplianceOk = null;
                    });
                    return ToolResult.Ok($"{list.Count} product(s) saved. Run compliance_check again.");
                });

        internal static void AddCompliance(AgentToolbox box, StudioContext ctx, string gameId) =>
            box.Add("compliance_check", "Check the store page and products against Roblox's rules (deterministic checks plus an independent review). Must pass before launch.",
                Object(),
                async (args, ct) =>
                {
                    var game = ctx.Game(gameId)!;
                    var report = ComplianceChecker.Check(game.Store, game.Products, game.Brief);
                    string review = "";
                    if (report.Ok)
                    {
                        try { review = await ReviewAsync(ctx, game, ct); }
                        catch (Exception ex) when (ex is not OperationCanceledException) { review = "(independent review unavailable: " + ex.Message + ")"; }
                        if (review.StartsWith("FAIL", StringComparison.Ordinal)) report.Errors.Add(review[5..].Trim());
                    }
                    ctx.Update(gameId, g =>
                    {
                        g.Store.ComplianceOk = report.Ok;
                        g.Store.ComplianceNotes = report.Errors.Concat(report.Warnings).Concat(report.QuestionnaireNotes.Select(n => "questionnaire: " + n)).Take(30).ToList();
                    });
                    return ToolResult.Ok(report + (review.Length > 0 && !review.StartsWith("FAIL", StringComparison.Ordinal) ? "\nIndependent review: " + review : ""));
                });

        /// <summary>One-shot LLM review of the store page; returns "FAIL: …" or a short OK note.</summary>
        private static async Task<string> ReviewAsync(StudioContext ctx, GameDossier game, CancellationToken ct)
        {
            string session = "omniblox-compliance-" + game.GameId + "-" + Guid.NewGuid().ToString("N")[..6];
            ctx.Llm.StartSession(session, AgentPrompts.ComplianceReviewer);
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Title: {game.Store.Title}");
                sb.AppendLine($"Description: {game.Store.Description}");
                sb.AppendLine($"Genre: {game.Store.Genre}; keywords: {string.Join(", ", game.Store.Keywords)}");
                sb.AppendLine("Products: " + string.Join("; ", game.Products.Select(p => $"{p.Name} ({p.Kind}, {p.PriceRobux} Robux): {p.Description}")));
                sb.AppendLine($"The game: {game.Brief?.Pitch} Core loop: {game.Brief?.CoreLoop}");
                ctx.Llm.AppendUser(session, sb.ToString());
                var turn = await ctx.Llm.QueryAsync(session, Array.Empty<ToolDefinition>(), new LlmCallOptions { MaxTokens = 600, Thinking = "low", WorkClass = AIRouterWorkClass.OneShot }, ct);
                ctx.AddUsage(game.GameId, new CycleReport { Turns = 1, PromptTokens = turn.PromptTokens, CachedTokens = turn.CachedTokens, CompletionTokens = turn.CompletionTokens });
                if (!turn.Success) return "(review unavailable)";
                int start = turn.Text.IndexOf('{'), end = turn.Text.LastIndexOf('}');
                if (start < 0 || end <= start) return "(review unreadable)";
                var j = JObject.Parse(turn.Text[start..(end + 1)]);
                var problems = Args.List(j, "problems");
                bool ok = Args.Bool(j, "ok", true) || problems.Count == 0;
                return ok ? "OK" + (Args.List(j, "suggestions") is { Count: > 0 } s ? " — suggestions: " + string.Join("; ", s) : "") : "FAIL: " + string.Join("; ", problems);
            }
            finally
            {
                ctx.Llm.EndSession(session);
            }
        }
    }

    /// <summary>The maintainer's tools: the builder's, plus numbers, store refreshes, shipping, lessons and sunset.</summary>
    public static class MaintenanceTools
    {
        public static AgentToolbox Create(StudioContext ctx, string gameId)
        {
            var box = BuildTools.Create(ctx, gameId);
            box.Add("game_metrics", "Roblox's analytics for this game, one row per day (players, visits, session length, D1/D7 retention, revenue, payers, peak concurrency).",
                Object(Opt("days", Int("Days (default 14)"))),
                args => ToolResult.Ok(AnalyticsSync.Describe(ctx.Game(gameId)!, Math.Clamp(Args.Int(args, "days", 14), 1, 90))));
            box.Add("telemetry", "The game's own anonymous feed: sessions, session length, funnel drop-offs (e.g. obby stages), deaths, purchases, errors and feedback.",
                Object(Opt("days", Int("Days (default 7)"))),
                args =>
                {
                    int days = Math.Clamp(Args.Int(args, "days", 7), 1, 90);
                    return ToolResult.Ok(TelemetryStore.Describe(ctx.Telemetry.Summarize(gameId, ctx.Clock().AddDays(-days))));
                });
            LaunchTools.AddPlacedAssets(box, ctx, gameId);
            LaunchTools.AddStorePage(box, ctx, gameId, "refresh_store_page");
            LaunchTools.AddVisuals(box, ctx, gameId);
            LaunchTools.AddProducts(box, ctx, gameId);
            LaunchTools.AddCompliance(box, ctx, gameId);
            CommonTools.AddLesson(box, ctx, gameId);
            box.Add("ship_update", "Ship your changes: the studio rebuilds, tests and republishes the live game (and re-applies the store page if it changed). The build must be clean first.",
                Object(Req("release_notes", Str("What changed, for the update log"))),
                args =>
                {
                    var game = ctx.Game(gameId)!;
                    if (!game.Build.LastBuildOk) return ToolResult.Error("run build and fix every error first");
                    ctx.Update(gameId, g => g.PendingUpdateNotes = Args.Required(args, "release_notes"));
                    return ToolResult.Ok("Update queued; it ships after this cycle once the tests pass.");
                });
            box.Add("recommend_sunset", "Stop investing in this game (it stays up, but gets no more maintenance). Use only when it has had no meaningful players for weeks and nothing cheap could change that.",
                Object(Req("reason", Str("Why, with numbers"))),
                args =>
                {
                    ctx.Update(gameId, g => g.SunsetReason = Args.Required(args, "reason"));
                    return ToolResult.Ok("Sunset recorded; the studio will stop maintaining this game after this cycle.");
                });
            return box;
        }
    }
}
