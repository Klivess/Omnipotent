using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox.Agents;
using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Roblox;
using static Omnipotent.Services.OmniBlox.Agents.Schema;

namespace Omnipotent.Services.OmniBlox.Pipeline.Tools
{
    /// <summary>The researcher's tools: what is popular on Roblox, what players want, what the studio can build, and the brief.</summary>
    public static class ResearchTools
    {
        private static readonly object chartLock = new();
        private static (DateTime At, List<ChartSort> Sorts)? chartCache;

        public static readonly string[] Grants = { "currency", "multiplier", "speed", "flag", "item", "skipstage" };

        public static AgentToolbox Create(StudioContext ctx, string gameId)
        {
            var box = new AgentToolbox();

            box.Add("roblox_charts", "Roblox's charts right now (Top Trending, Up-and-Coming, Top Playing Now, Fun with Friends, Top Revisited…): rank, live players, like ratio and genre for each game.",
                Object(Opt("sorts", StrList("Only these sorts (ids or names); default all")), Opt("top", Int("Games per sort (default 25, max 60)"))),
                async (args, ct) =>
                {
                    var sorts = await ChartsAsync(ctx, ct);
                    var only = Args.List(args, "sorts").Select(s => s.ToLowerInvariant()).ToList();
                    int top = Math.Clamp(Args.Int(args, "top", 25), 5, 60);
                    var sb = new StringBuilder();
                    foreach (var sort in sorts.Where(s => only.Count == 0 || only.Contains(s.SortId.ToLowerInvariant()) || only.Contains(s.DisplayName.ToLowerInvariant())))
                    {
                        sb.AppendLine($"## {sort.DisplayName} ({sort.SortId})");
                        foreach (var g in sort.Games.Take(top))
                            sb.AppendLine($"{g.Rank}. {g.Name} — {g.PlayerCount:N0} playing, {g.LikeRatio:P0} likes, {g.Genre}, universe {g.UniverseId}{(g.IsSponsored ? " (sponsored)" : "")}");
                    }
                    return ToolResult.Ok(sb.Length == 0 ? "No charts matched." : sb.ToString());
                });

            box.Add("roblox_game_details", "Details of specific Roblox games: description, visits, favourites, live players, likes, created and updated dates.",
                Object(Req("universe_ids", Arr(Int("universe id"), "Up to 20 universe ids"))),
                async (args, ct) =>
                {
                    var ids = (args["universe_ids"] as JArray ?? new JArray()).Select(x => (long?)x ?? 0).Where(x => x > 0).Take(20).ToList();
                    if (ids.Count == 0) return ToolResult.Error("give at least one universe id (from roblox_charts)");
                    var details = await ctx.Public.GetGameDetailsAsync(ids, ct);
                    var sb = new StringBuilder();
                    foreach (var d in details)
                    {
                        double likes = d.UpVotes + d.DownVotes == 0 ? 0 : d.UpVotes / (double)(d.UpVotes + d.DownVotes);
                        sb.AppendLine($"## {d.Name} (universe {d.UniverseId}) by {d.CreatorName}");
                        sb.AppendLine($"{d.Playing:N0} playing · {d.Visits:N0} visits · {d.Favorites:N0} favourites · {likes:P0} likes · max {d.MaxPlayers} per server · {d.GenreL1}/{d.GenreL2}");
                        sb.AppendLine($"Created {d.CreatedUtc:yyyy-MM-dd}, updated {d.UpdatedUtc:yyyy-MM-dd}");
                        sb.AppendLine("Description: " + Clip(d.Description.Replace("\n", " "), 500));
                    }
                    ctx.Update(gameId, g =>
                    {
                        foreach (var id in ids) if (!g.Research.ReferenceUniverses.Contains(id)) g.Research.ReferenceUniverses.Add(id);
                        if (g.Research.ReferenceUniverses.Count > 60) g.Research.ReferenceUniverses.RemoveRange(0, g.Research.ReferenceUniverses.Count - 60);
                        g.Research.LastScanUtc = ctx.Clock();
                    });
                    return ToolResult.Ok(sb.ToString());
                });

            box.Add("web_search", "Search the web (free, self-hosted search) for Roblox trends, player wishes, viral game ideas and genre know-how.",
                Object(Req("query", Str("Search terms")), Opt("time_range", Str("Freshness filter", "day", "week", "month", "year"))),
                async (args, ct) => ToolResult.Ok(await ctx.Web.SearchAsync(Args.Required(args, "query"), 8, Args.Str(args, "time_range") is { Length: > 0 } t ? t : null, ct)));

            box.Add("web_fetch", "Read a web page found by web_search (text only).",
                Object(Req("url", Str("The page URL"))),
                async (args, ct) => ToolResult.Ok(await ctx.Web.FetchAsync(Args.Required(args, "url"), ct)));

            box.Add("asset_coverage", "How many usable free models the studio's Asset Library holds for each theme — the feasibility signal. 'labelled' are ready to place; 'measured' are downloaded but not yet described; 'found' have only been discovered.",
                Object(Req("themes", StrList("Themes or objects, e.g. 'pirate ship', 'palm tree', 'castle'"))),
                args =>
                {
                    var lines = Args.List(args, "themes").Take(25).Select(theme =>
                    {
                        var (labeled, analyzed, found) = ctx.Library.Coverage(theme);
                        return $"- {theme}: {labeled} labelled, {analyzed} measured, {found} found";
                    });
                    var stats = ctx.Library.Stats();
                    return ToolResult.Ok($"Library: {stats.Total} models known, {stats.Placeable} placeable.\n" + string.Join("\n", lines) +
                                         "\n(Themes with few models get searched for by the harvester once the brief lists them as asset needs.)");
                });

            CommonTools.AddPlaybookSearch(box, ctx);
            CommonTools.AddPortfolio(box, ctx);

            box.Add("save_finding", "Keep a research finding in the dossier (trends, numbers, competitor strengths and gaps).",
                Object(Req("finding", Str("One finding, with its source or numbers"))),
                args =>
                {
                    string finding = Clip(Args.Required(args, "finding"), 400);
                    ctx.Update(gameId, g =>
                    {
                        g.Research.Findings.Add(finding);
                        if (g.Research.Findings.Count > 60) g.Research.Findings.RemoveRange(0, g.Research.Findings.Count - 60);
                    });
                    return ToolResult.Ok("Saved.");
                });

            box.Add("propose_concept", "Add a game concept with its evidence and 0-10 scores (demand, competition where 10 = little competition, monetization, feasibility, lessons).",
                Object(Req("pitch", Str("One-sentence pitch")), Req("genre", Str("Genre, e.g. obby, tycoon, simulator, hangout")),
                    Req("template", Str("Game Kit template", GameKit.Templates.Keys.ToArray())), Req("core_loop", Str("What the player does over and over")),
                    Opt("twist", Str("What makes it fresh")), Opt("evidence", StrList("Facts that support it (with numbers)")),
                    Req("demand", Num("0-10")), Req("competition", Num("0-10, 10 = little competition")), Req("monetization", Num("0-10")),
                    Req("feasibility", Num("0-10: can the studio build this well with its kit and models?")), Opt("lessons", Num("0-10: fit with Playbook lessons"))),
                args =>
                {
                    string template = Args.Required(args, "template").ToLowerInvariant();
                    if (!GameKit.Templates.ContainsKey(template)) return ToolResult.Error($"template must be one of {string.Join(", ", GameKit.Templates.Keys)}");
                    double S(string k) => Math.Clamp(Args.Num(args, k) ?? 5, 0, 10);
                    var concept = new GameConcept
                    {
                        Pitch = Clip(Args.Required(args, "pitch"), 300),
                        Genre = Clip(Args.Required(args, "genre"), 60),
                        Template = template,
                        CoreLoop = Clip(Args.Required(args, "core_loop"), 400),
                        Twist = Clip(Args.Str(args, "twist"), 300),
                        Evidence = Args.List(args, "evidence").Take(10).Select(e => Clip(e, 300)).ToList(),
                        Scores = new ConceptScores { Demand = S("demand"), Competition = S("competition"), Monetization = S("monetization"), Feasibility = S("feasibility"), Lessons = S("lessons") },
                    };
                    string id = "";
                    ctx.Update(gameId, g =>
                    {
                        id = "c" + (g.Concepts.Count + 1);
                        concept.ConceptId = id;
                        g.Concepts.Add(concept);
                    });
                    return ToolResult.Ok($"Concept {id} saved, total score {concept.Scores.Total:0.##}.");
                });

            box.Add("choose_concept", "Choose the concept to build.",
                Object(Req("concept_id", Str("e.g. c2"))),
                args =>
                {
                    string id = Args.Required(args, "concept_id");
                    GameConcept? chosen = null;
                    ctx.Update(gameId, g =>
                    {
                        chosen = g.Concepts.FirstOrDefault(c => c.ConceptId.Equals(id, StringComparison.OrdinalIgnoreCase));
                        if (chosen == null) return;
                        foreach (var c in g.Concepts) c.Chosen = c == chosen;
                    });
                    if (chosen == null) return ToolResult.Error("no concept " + id);
                    return ToolResult.Ok($"Chose {chosen.ConceptId} (score {chosen.Scores.Total:0.##})." +
                        (chosen.Scores.Total < StageGates.MinConceptScore ? $" Note: the stage needs a score of at least {StageGates.MinConceptScore}." : ""));
                });

            box.Add("write_brief", "Write (or rewrite) the brief Build will work from. The asset needs are sent to the asset harvester right away.",
                Object(Req("title", Str("Working title")), Req("genre", Str("Genre")), Req("template", Str("Game Kit template", GameKit.Templates.Keys.ToArray())),
                    Req("pitch", Str("One or two sentences")), Opt("target_players", Str("Who it is for")), Req("core_loop", Str("The core loop")),
                    Req("must_have", StrList("3-8 things the game must have to be complete")), Opt("nice_to_have", StrList("Extras if time allows")),
                    Opt("art_style", Str("Visual style")), Opt("asset_needs", StrList("Models to find, in plain words")),
                    Req("monetization", Arr(Object(Req("key", Str("code id, lowercase, e.g. vip or coins_500")), Req("kind", Str("GamePass or DeveloperProduct", "GamePass", "DeveloperProduct")),
                        Req("name", Str("Shown in the shop")), Req("description", Str("What the player gets")), Req("price", Int("Robux")), Req("grant", Str("currency:N, multiplier:N, speed:N, flag:NAME, item:NAME or skipstage"))), "2-5 fair products")),
                    Opt("marketing", Object(Opt("hook", Str("The one-line reason to play")), Opt("title_ideas", StrList("Title ideas")), Opt("thumbnail_ideas", StrList("Thumbnail ideas")), Opt("keywords", StrList("Search keywords")))),
                    Req("done_when", StrList("Checks the builder can verify, e.g. '30 stages, each reachable'")), Opt("evidence", StrList("Why this will work"))),
                args =>
                {
                    string template = Args.Required(args, "template").ToLowerInvariant();
                    if (!GameKit.Templates.ContainsKey(template)) return ToolResult.Error($"template must be one of {string.Join(", ", GameKit.Templates.Keys)}");
                    var products = new List<ProductIdea>();
                    var problems = new List<string>();
                    foreach (var p in (args["monetization"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        string key = Args.Str(p, "key").ToLowerInvariant();
                        string grant = Args.Str(p, "grant");
                        string kind = Args.Str(p, "kind");
                        int price = Args.Int(p, "price", 0);
                        string grantKind = grant.Split(':')[0].Trim().ToLowerInvariant();
                        if (key.Length == 0 || !key.All(c => char.IsLetterOrDigit(c) || c == '_')) problems.Add($"product key '{key}' must be lowercase letters, digits and _");
                        if (!Grants.Contains(grantKind)) problems.Add($"product {key}: grant '{grant}' is not one the kit knows ({string.Join(", ", Grants)})");
                        if (grantKind == "skipstage" && template != "obby") problems.Add("skipstage only works in an obby");
                        if (kind is not ("GamePass" or "DeveloperProduct")) problems.Add($"product {key}: kind must be GamePass or DeveloperProduct");
                        if (grantKind is "multiplier" or "speed" or "flag" && kind == "DeveloperProduct") problems.Add($"product {key}: permanent perks ({grantKind}) must be game passes");
                        if (price < 1 || price > 10000) problems.Add($"product {key}: price {price} must be 1-10000 Robux");
                        products.Add(new ProductIdea
                        {
                            Key = key,
                            Kind = kind == "GamePass" ? ProductKind.GamePass : ProductKind.DeveloperProduct,
                            Name = Clip(Args.Str(p, "name"), 50),
                            Description = Clip(Args.Str(p, "description"), 200),
                            PriceRobux = price,
                            Grant = grant.Trim(),
                        });
                    }
                    if (products.Select(p => p.Key).Distinct().Count() != products.Count) problems.Add("product keys must be unique");
                    if (problems.Count > 0) return ToolResult.Error("the brief was not saved:\n- " + string.Join("\n- ", problems));

                    var m = args["marketing"] as JObject ?? new JObject();
                    var brief = new GameBrief
                    {
                        Title = Clip(Args.Required(args, "title"), 60),
                        Genre = Clip(Args.Required(args, "genre"), 60),
                        Template = template,
                        Pitch = Clip(Args.Required(args, "pitch"), 500),
                        TargetPlayers = Clip(Args.Str(args, "target_players"), 200),
                        CoreLoop = Clip(Args.Required(args, "core_loop"), 500),
                        MustHave = Args.List(args, "must_have").Take(12).Select(x => Clip(x, 200)).ToList(),
                        NiceToHave = Args.List(args, "nice_to_have").Take(10).Select(x => Clip(x, 200)).ToList(),
                        ArtStyle = Clip(Args.Str(args, "art_style"), 200),
                        AssetNeeds = Args.List(args, "asset_needs").Take(30).Select(x => Clip(x, 60)).ToList(),
                        Monetization = products,
                        Marketing = new MarketingAngle
                        {
                            Hook = Clip(Args.Str(m, "hook"), 200),
                            TitleIdeas = Args.List(m, "title_ideas").Take(8).ToList(),
                            ThumbnailIdeas = Args.List(m, "thumbnail_ideas").Take(8).ToList(),
                            Keywords = Args.List(m, "keywords").Take(15).ToList(),
                        },
                        DoneWhen = Args.List(args, "done_when").Take(12).Select(x => Clip(x, 200)).ToList(),
                        Evidence = Args.List(args, "evidence").Take(10).Select(x => Clip(x, 300)).ToList(),
                    };
                    ctx.Update(gameId, g =>
                    {
                        g.Brief = brief;
                        g.WorkingTitle = brief.Title;
                    });
                    ctx.Harvester.RequestAssets(brief.AssetNeeds);
                    var gate = StageGates.Checklist(ctx.Game(gameId)!, ctx);
                    return ToolResult.Ok("Brief saved; the harvester is looking for its models.\nExit criteria:\n" + string.Join("\n", gate.Select(c => $"- [{(c.Met ? "x" : " ")}] {c.Text}")));
                });

            return box;
        }

        internal static async Task<List<ChartSort>> ChartsAsync(StudioContext ctx, CancellationToken ct)
        {
            lock (chartLock)
                if (chartCache is { } cached && ctx.Clock() - cached.At < TimeSpan.FromHours(2)) return cached.Sorts;
            var sorts = await ctx.Public.GetChartsAsync(ct);
            lock (chartLock) chartCache = (ctx.Clock(), sorts);
            return sorts;
        }

        internal static void ClearChartCache()
        {
            lock (chartLock) chartCache = null;
        }

        private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
    }
}
