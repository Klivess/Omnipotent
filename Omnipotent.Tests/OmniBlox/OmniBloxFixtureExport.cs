using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox;
using Omnipotent.Services.OmniBlox.Assets;
using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Pipeline;
using Omnipotent.Services.OmniBlox.Rbx;
using Omnipotent.Services.OmniBlox.Roblox;
using SkiaSharp;

namespace Omnipotent.Tests.OmniBlox
{
    /// <summary>
    /// Writes the website's OmniBlox e2e fixtures by running the real studio (scripted model, fake Roblox)
    /// and serializing what the routes return through OmniBloxViews, so the dashboard is exercised against
    /// exactly the shapes the server sends. Off unless OMNIBLOX_FIXTURES names the output directory, e.g.
    /// klives-management-website/tests/e2e/fixtures/omniblox.
    /// </summary>
    public class OmniBloxFixtureExport
    {
        /// <summary>The fixtures' "now"; the e2e spec pins the browser clock to it.</summary>
        private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime LaunchDay = new(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task ExportWebsiteFixtures()
        {
            string? output = Environment.GetEnvironmentVariable("OMNIBLOX_FIXTURES");
            if (string.IsNullOrWhiteSpace(output)) return;
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
            Directory.CreateDirectory(output);

            using var h = new StudioHarness();
            var ctx = h.Ctx;

            // ── An early game the watchdog gave up on.
            h.Now = new DateTime(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);
            string abandoned = h.Orchestrator.StartGame("space");
            ctx.Update(abandoned, g =>
            {
                g.WorkingTitle = "Space Station Simulator";
                g.Concepts.Add(new GameConcept { ConceptId = "c1", Pitch = "Mine asteroids and upgrade a space station", Genre = "simulator", Template = "simulator", CoreLoop = "Mine, sell, upgrade the drill", Scores = new ConceptScores { Demand = 5, Competition = 3, Monetization = 6, Feasibility = 4, Lessons = 5 } });
                g.Concepts.Add(new GameConcept { ConceptId = "c2", Pitch = "Zero-gravity obby between satellites", Genre = "obby", Template = "obby", CoreLoop = "Float between platforms", Scores = new ConceptScores { Demand = 4, Competition = 4, Monetization = 4, Feasibility = 3, Lessons = 5 } });
                g.StageCycles[GameStage.Research] = 12;
                g.StageFailures[GameStage.Research] = 2;
                g.Escalations = 2;
                g.Usage.Add(96, 1_480_000, 1_310_000, 41_000);
                g.AbandonedFromStage = GameStage.Research;
                g.Stage = GameStage.Abandoned;
                g.StageEnteredUtc = h.Now.AddDays(2);
                g.AbandonReason = "Research took 12 cycles over two attempts and no concept reached the minimum score of 5 (space themes had too few usable free models).";
            });
            ctx.Event(EventLevel.Warning, "watchdog.reset", "Research stalled after 6 cycles; reset with a different approach.", abandoned);
            ctx.Event(EventLevel.Error, "watchdog.abandon", "Abandoned \"Space Station Simulator\": no buildable concept after two attempts.", abandoned);

            // ── The Asset Library: the harvester's pine tree plus a varied shelf.
            h.Now = LaunchDay.AddHours(-2);
            await h.Harvester.RunOnceAsync(CancellationToken.None);
            SeedLibrary(ctx.Library, h.Now);

            // ── Game 1: idea to live, through the owner's two gates (play and sign off the build, then approve the launch).
            h.Now = LaunchDay;
            ctx.Settings.RequireBuildSignoff = true;
            ctx.Settings.RequireLaunchApproval = true;
            PipelineEndToEndTests.ScriptResearch(h.Llm);
            PipelineEndToEndTests.ScriptBuild(h.Llm);
            PipelineEndToEndTests.ScriptReview(h.Llm);
            PipelineEndToEndTests.ScriptMarketing(h.Llm);
            await h.StepUntilAsync(() => ctx.Store.Read(s => s.Games.Values.Any(g => g.WaitingReason?.Contains("sign it off") == true)), 40);
            string live = ctx.Store.Read(s => s.Games.Values.Single(g => g.Stage == GameStage.Launch).GameId);
            Approvals.Approve(ctx, live, Approvals.BuildGate, "Played it to the finish: the jumps are fair and the lava reads as danger.");
            await h.StepUntilAsync(() => h.Game(live).WaitingReason?.Contains("launch approval") == true, 40);
            Approvals.Approve(ctx, live, Approvals.LaunchGate, "Store page and prices look right.");
            await h.StepUntilAsync(() => h.Game(live).Stage == GameStage.Live, 40);
            PolishHandovers(ctx, live);

            IngestTelemetry(ctx, live);

            h.Now = LaunchDay.AddDays(2).AddHours(1);
            ScriptMaintenance(h.Llm, "Day 2: 214 sessions, median 5.6 minutes, nobody bought anything yet. Stage 2 loses 38% of players; widened its first jump.",
                ("Widen stage 2's first jump by two studs", "38% of players fail it and most of them leave"),
                new[] { "Compare stage 2 completion after the wider jump" },
                ("game_metrics", new { days = 7 }), ("telemetry", new { days = 7 }),
                ("add_lesson", new { topic = "mechanic", text = "Players who clear stage 2 of an obby tend to stay five minutes or more.", evidence = "telemetry: 214 sessions, median 5.6 min; 62% reach stage 2", outcome = "worked", tags = new[] { "obby" } }));
            await h.StepUntilAsync(() => h.Game(live).Maintenance.Count == 1);

            h.Now = LaunchDay.AddDays(7).AddHours(2);
            ScriptMaintenance(h.Llm, "Week 1: DAU up to 310, D1 retention 21%. The Skip Stage product sells; VIP does not. Recommend a cheaper 2x coins boost next update.",
                ("Plan a 2x coins boost at 49 R$", "VIP at 149 R$ barely sells while the 25 R$ Skip Stage does"),
                new[] { "Add the boost with the next content update" },
                ("game_metrics", new { days = 7 }), ("telemetry", new { days = 7 }));
            await h.StepUntilAsync(() => h.Game(live).PostMortems.Count == 1);

            h.Now = LaunchDay.AddDays(11).AddHours(3);
            ScriptMaintenance(h.Llm, "Day 11: steady growth (DAU ~480). One error repeats in the checkpoint script; logged for the next update. Thumbnail click-through is 5.1%.",
                ("Fix the checkpoint error in the next update", "it repeats on most servers, though nobody is blocked by it"),
                new[] { "Guard Obby.luau line 88 against a missing Stage value", "Ship the fix with the 2x coins boost" },
                ("game_metrics", new { days = 7 }), ("telemetry", new { days = 7 }),
                ("add_lesson", new { topic = "marketing", text = "A thumbnail showing the lava and the finish together gets about 5% click-through for obbies.", evidence = "analytics: 5.1% CTR over 9 days", outcome = "worked", tags = new[] { "obby", "thumbnail" } }));
            await h.StepUntilAsync(() => h.Game(live).Maintenance.Count == 3);
            ctx.Update(live, g => g.NextMaintenanceUtc = Now.AddDays(1).AddHours(4)); // out of game 2's way

            // ── Game 2: researched yesterday, two build cycles in; its engine run found a failing check and a boot error.
            // (Game 1 holds the first experience slot; a second one lets game 2 run in the engine.)
            ctx.Settings.Slots.Add(new ExperienceSlot { UniverseId = 5005, PlaceId = 6006 });
            h.Now = Now.AddHours(-26);
            h.Roblox.EngineOutput = "{\"results\":[{\"passed\":4,\"failed\":1,\"results\":[" +
                "{\"name\":\"Kit · new profiles start fresh\",\"ok\":true,\"phase\":\"kit\",\"owner\":\"kit\"}," +
                "{\"name\":\"Load · every module loads\",\"ok\":true,\"phase\":\"load\",\"owner\":\"kit\"}," +
                "{\"name\":\"Boot · the server starts\",\"ok\":true,\"phase\":\"boot\",\"owner\":\"kit\"}," +
                "{\"name\":\"Places · a tycoon plot exists\",\"ok\":true,\"phase\":\"world\",\"owner\":\"kit\",\"warnings\":[\"only one plot: servers hold up to 6 players\"]}," +
                "{\"name\":\"Factory · the first dropper is affordable\",\"ok\":false,\"detail\":\"starting candy 50 < first dropper 60\",\"phase\":\"game\",\"owner\":\"game\"}]," +
                "\"errors\":[{\"message\":\"ServerScriptService.Welcome:4: attempt to concatenate nil with string\",\"phase\":\"boot\",\"trace\":\"ServerScriptService.Welcome:4\"}],\"warnings\":[]}]}";
            ScriptTycoonResearch(h.Llm);
            ScriptTycoonBuild(h.Llm);
            await h.StepUntilAsync(() => ctx.Store.Read(s => s.Games.Values.Any(g => g.Stage == GameStage.Build && g.Cycles.Count(c => c.Role == "builder") >= 2)), 12);
            string building = ctx.Store.Read(s => s.Games.Values.Single(g => g.Stage == GameStage.Build).GameId);
            Assert.True(h.Game(building).Build.LastBuildOk, string.Join("; ", h.Game(building).Build.Errors));
            Assert.False(h.Game(building).Build.LastEngineRun!.Ok);
            ctx.Update(building, g =>
            {
                g.Directives.Add(new OwnerDirective { DirectiveId = "d1", Utc = Now.AddHours(-20), Text = "Make the factory plot bigger: kids want room for twelve droppers.", Status = DirectiveStatus.Done, Response = "Doubled the plot to 100×100 and spaced the buttons out.", ResolvedUtc = Now.AddHours(-19), Delivered = true });
                g.Directives.Add(new OwnerDirective { DirectiveId = "d2", Utc = Now.AddMinutes(-30), Text = "Add a candy machine finale that unlocks after the first rebirth.", Delivered = true });
            });
            await SeedPlaytestsAsync(h, building);
            h.Now = Now;
            SeedDailyMetrics(ctx, live); // after the steps: the analytics sweep merges the fake's single data point

            // ── Export, through the same builders the routes use.
            var status = new StudioStatus($"building \"{h.Game(building).DisplayTitle}\" (revision {h.Game(building).Build.Revision})", building, true, null,
                "discovered 0 · thumbnails 0 · analyzed 0 · labeled 0", Now.AddMinutes(-4));
            void Write(string name, object? value)
            {
                Assert.NotNull(value);
                File.WriteAllText(Path.Combine(output, name + ".json"), OmniBloxViews.Serialize(value!));
            }
            void WriteBytes(string relative, byte[] bytes)
            {
                string path = Path.Combine(output, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
            }

            Write("overview", OmniBloxViews.Overview(ctx, status));
            Write("games", OmniBloxViews.Games(ctx));
            Write("events", OmniBloxViews.Events(ctx, 200, ""));
            Write("playbook", OmniBloxViews.Playbook(ctx));
            Write("tasks", OmniBloxViews.Tasks(ctx));
            foreach (string id in new[] { live, building, abandoned })
            {
                Write($"game-{id}", OmniBloxViews.GameDetail(ctx, id));
                Write($"files-{id}", OmniBloxViews.Files(ctx, id));
                Write($"events-{id}", OmniBloxViews.Events(ctx, 200, id));
                foreach (int days in new[] { 7, 30 }) Write($"telemetry-{id}-{days}", OmniBloxViews.Telemetry(ctx, id, days));
                Write($"live-{id}", new { Live = ctx.Live.Get(id) });
                foreach (string cycleId in CycleLog.Available(ctx, id).Take(6))
                {
                    var entries = CycleLog.Read(ctx, id, cycleId)!;
                    Write($"cycle-{id}-{cycleId}", new { CycleId = cycleId, Report = h.Game(id).Cycles.FirstOrDefault(c => c.CycleId == cycleId), Entries = entries });
                    foreach (string file in entries.SelectMany(e => e.Images).Distinct())
                        if (CycleLog.ImagePath(ctx, id, cycleId, file) is { } picture) WriteBytes($"cycle/{id}/{cycleId}/{file}", Shrink(File.ReadAllBytes(picture), 960));
                }
                var workspace = ctx.Workspace(id);
                if (workspace.Exists)
                {
                    var contents = new JObject();
                    foreach (var file in workspace.ListFiles()) contents[file.Path] = workspace.ReadFile(file.Path);
                    File.WriteAllText(Path.Combine(output, $"filecontents-{id}.json"), contents.ToString(Formatting.None));
                    Write($"map-{id}", OmniBloxViews.Map(ctx, id));
                    var compiled = ctx.Compile(id);
                    if (compiled.Parts.Count > 0 || compiled.Terrain != null)
                    {
                        var views = new List<string> { "map", "overview", "aerial", "iso", "spawn" };
                        views.AddRange(workspace.LoadScene().Layout.Zones.Select(z => "zone:" + z.Id));
                        foreach (string view in views)
                            WriteBytes($"render/{id}-{view.Replace(':', '-')}.png", Shrink(ctx.RenderCompiled(id, compiled, view, 1280, 760), 960));
                    }
                }
                foreach (string file in OmniBloxViews.MediaFiles(ctx, id))
                {
                    byte[] bytes = File.ReadAllBytes(Path.Combine(ctx.Store.MediaDirectory(id), file));
                    WriteBytes($"media/{id}/{file}", file.EndsWith(".png") ? Shrink(bytes, file == "icon.png" ? 256 : 960) : bytes);
                }
            }

            Write("assets", OmniBloxViews.Assets(ctx, "", null, 60, 0, placeableOnly: true));
            Write("assets-search-tree", OmniBloxViews.Assets(ctx, "tree", null, 60, 0, placeableOnly: true));
            foreach (var rec in ctx.Library.Recent(null, 200))
            {
                Write($"asset-{rec.AssetId}", OmniBloxViews.AssetDetail(ctx, rec.AssetId));
                if (File.Exists(ctx.Library.ThumbnailPath(rec.AssetId))) WriteBytes($"asset/{rec.AssetId}-thumb.png", Shrink(File.ReadAllBytes(ctx.Library.ThumbnailPath(rec.AssetId)), 256));
                foreach (string view in rec.Renders)
                    WriteBytes($"asset/{rec.AssetId}-{view}.png", Shrink(File.ReadAllBytes(ctx.Library.RenderPath(rec.AssetId, view)), 256));
            }

            File.WriteAllText(Path.Combine(output, "meta.json"), JsonConvert.SerializeObject(new
            {
                NowUtc = Now,
                Live = live,
                Building = building,
                Abandoned = abandoned,
                PineTree = FakeRoblox.AssetId,
                PlaytestPlaceId = PlaytestSlot.PlaceId,
            }, OmniBloxViews.Json));
        }

        private static readonly ExperienceSlot PlaytestSlot = new() { UniverseId = 3003, PlaceId = 4004 };

        /// <summary>
        /// The owner trying game 2 while it is built: a Studio run last night, then the playtest experience now
        /// (still in it, with Follow on), where a script error and some feedback came in.
        /// </summary>
        private static async Task SeedPlaytestsAsync(StudioHarness h, string gameId)
        {
            var ctx = h.Ctx;
            ctx.Settings.PlaytestSlot = PlaytestSlot;
            JObject Batch(string server, string build, string where, int players, params JObject[] events) =>
                new() { ["server"] = server, ["build"] = build, ["where"] = where, ["players"] = players, ["events"] = new JArray(events) };
            JObject E(string name, object? fields = null) =>
                new() { ["n"] = name, ["s"] = "owner", ["f"] = fields == null ? null : JObject.FromObject(fields) };

            ctx.Playtests.Ingest(gameId, Batch("studio-run-1", "r1+", Playtests.Studio, 1,
                E("session_start"),
                E("economy", new { flow = "Source", amount = 120, balance = 170, reason = "Collector" }),
                E("tycoon_unlock", new { item = "Dropper1", price = 50 }),
                E("purchase", new { key = "candy_500", kind = "product", robux = 0, test = true }),
                E("feedback", new { rating = 3, reason = "want_more" }),
                E("session_end", new { seconds = 262 })), Now.AddHours(-16));

            h.Now = Now.AddMinutes(-12);
            await ctx.Playtests.PublishAsync(gameId, "latest", fresh: false, followed: false, CancellationToken.None);
            const string error = "ServerScriptService.Rebirth:12: attempt to compare number <= nil";
            ctx.Playtests.Ingest(gameId, Batch("srv-playtest-7", "r2", Playtests.Roblox, 1,
                E("session_start"),
                E("tycoon_unlock", new { item = "Dropper1", price = 50 }),
                E("economy", new { flow = "Source", amount = 340, balance = 390, reason = "Collector" }),
                E("tycoon_unlock", new { item = "Upgrader", price = 250 })), Now.AddMinutes(-11));
            ctx.Playtests.Ingest(gameId, Batch("srv-playtest-7", "r2", Playtests.Roblox, 1,
                E("purchase", new { key = "double", kind = "pass", robux = 0, test = true }),
                new JObject { ["n"] = "error", ["f"] = JObject.FromObject(new { message = error, source = "ServerScriptService.Rebirth", count = 1 }) },
                E("economy", new { flow = "Source", amount = 610, balance = 750, reason = "Collector" }),
                E("feedback", new { rating = 2, reason = "confusing" })), Now.AddMinutes(-6));
            ctx.Playtests.Ingest(gameId, Batch("srv-playtest-7", "r2", Playtests.Roblox, 1,
                new JObject { ["n"] = "error", ["f"] = JObject.FromObject(new { message = error, count = 3 }) },
                new JObject { ["n"] = "heartbeat", ["f"] = JObject.FromObject(new { players = 1 }) }), Now.AddMinutes(-1));
            ctx.Playtests.SetFollow(gameId, true);
        }

        /// <summary>The shared end-to-end scripts end each cycle with a stock line; give game 1 the hand-overs a real cycle writes.</summary>
        private static void PolishHandovers(StudioContext ctx, string gameId)
        {
            var text = new Dictionary<string, string>
            {
                ["researcher"] = "Lava obbies are trending (Lava Rush Obby: 5,400 playing, 90% likes) and the library has forest models. Chose a lava obby through a pine forest; brief written with Skip Stage and VIP.",
                ["builder"] = "Shortened the generated course to sixteen stages over the lava basin (one new mechanic at a time, a checkpoint at every stage) and put a pine tree on the start platform. Every milestone is done and the build is clean.",
                ["tester"] = "Clear path, fair jumps, the lava is obvious. Engine specs passed.",
                ["marketer"] = "Store page, icon and thumbnail made from the real world; compliance passed; launch requested.",
            };
            var next = new Dictionary<string, string[]>
            {
                ["researcher"] = new[] { "Builder: start from the generated course; keep the first stages easy" },
                ["builder"] = new[] { "Tester: play it from the spawn", "Check the first five stages are easy" },
                ["tester"] = new[] { "Marketer: lead with the lava in the thumbnail" },
                ["marketer"] = new[] { "Wait for the launch sequence; upload the icon by hand" },
            };
            ctx.Store.Mutate(s =>
            {
                if (s.Game(gameId) is not { } g) return;
                foreach (var c in g.Cycles)
                {
                    if (c.Done.StartsWith("Scripted ", StringComparison.Ordinal) && text.TryGetValue(c.Role, out var done)) c.Done = done;
                    if (c.NextSteps is ["carry on"] && next.TryGetValue(c.Role, out var steps)) c.NextSteps = steps.ToList();
                }
                var at = g.Cycles.LastOrDefault()?.Utc ?? LaunchDay;
                g.KeyFacts = new List<KeyFact>
                {
                    new() { Key = "experience", Text = "Experience 1001, place 2002", Utc = at, Stage = GameStage.Launch, Source = FactBook.Studio },
                    new() { Key = "products", Text = "Skip Stage (developer product, 25 R$), VIP (game pass, 149 R$)", Utc = at, Stage = GameStage.Launch },
                    new() { Key = "course", Text = "16 stages snaking in rows from the start platform (top at y 14, z 120) to the gold finish; a checkpoint pad at every stage", Utc = at, Stage = GameStage.Build },
                    new() { Key = "lava", Text = "An invisible kill floor just above the lava basin catches every fall", Utc = at, Stage = GameStage.Build },
                    new() { Key = "direction", Text = "Keep the first three stages easy: the owner's playtest showed younger players giving up on stage 2.", Utc = at, Stage = GameStage.Build, Source = FactBook.Owner },
                };
                foreach (var e in s.Events.Where(e => e.GameId == gameId))
                    foreach (var (role, done) in text)
                        e.Message = e.Message.Replace($"Scripted {role} cycle done.", done);
                s.MarkGame(gameId);
            });
        }

        private static void ScriptMaintenance(ScriptedLlm llm, string summary, (string What, string Why) decision, string[] next, params (string Tool, object Args)[] steps) =>
            llm.Script("maintainer", new
            {
                summary,
                decisions = new[] { new { what = decision.What, why = decision.Why } },
                next_steps = next,
                stage_complete = true,
            }, steps);

        private static void ScriptTycoonResearch(ScriptedLlm llm) =>
            llm.Script("researcher",
                new
                {
                    summary = "Tycoons with a food theme are rising on the charts and the library has enough factory and candy models. Chose a candy factory tycoon with a rebirth loop.",
                    decisions = new[] { new { what = "Build Candy Factory Tycoon", why = "highest total score (7.4); the tycoon template fits and the theme has free models" } },
                    next_steps = new[] { "Builder: droppers, conveyor and collector first" },
                    key_facts = new[] { "Template: tycoon", "Currency: Candy" },
                    stage_complete = true,
                },
                ("roblox_charts", new { top = 10 }),
                ("save_finding", new { finding = "Three food-themed tycoons entered the top 50 this month." }),
                ("propose_concept", new { pitch = "A candy factory tycoon with a rebirth loop", genre = "tycoon", template = "tycoon", core_loop = "Buy droppers that make candy, sell it at the collector, buy upgrades, rebirth for a multiplier", twist = "every upgrade changes the factory's colours", evidence = new[] { "food tycoons rising" }, demand = 8, competition = 5, monetization = 8, feasibility = 8, lessons = 6 }),
                ("propose_concept", new { pitch = "A pizza delivery simulator", genre = "simulator", template = "simulator", core_loop = "Collect pizzas, deliver, upgrade the scooter", demand = 6, competition = 5, monetization = 6, feasibility = 5 }),
                ("propose_concept", new { pitch = "A candy-cane obby", genre = "obby", template = "obby", core_loop = "Jump across giant sweets", demand = 5, competition = 3, monetization = 5, feasibility = 9 }),
                ("choose_concept", new { concept_id = "c1" }),
                ("write_brief", new
                {
                    title = "Candy Factory Tycoon",
                    genre = "tycoon",
                    template = "tycoon",
                    pitch = "Build the sweetest factory on the server: droppers, conveyors and a giant candy machine.",
                    target_players = "8-13 year olds who like building and numbers going up",
                    core_loop = "Buy droppers that make candy, collect the cash, buy upgrades, then rebirth for a permanent multiplier.",
                    must_have = new[] { "A plot per player with droppers, a conveyor and a collector", "At least 12 buttons in a sensible order", "A rebirth with a multiplier", "A leaderboard" },
                    nice_to_have = new[] { "Daily reward", "A candy machine finale" },
                    asset_needs = new[] { "candy", "factory machine", "conveyor" },
                    monetization = new object[]
                    {
                        new { key = "double", kind = "GamePass", name = "2x Candy", description = "Double candy from every dropper", price = 99, grant = "multiplier:2" },
                        new { key = "candy_500", kind = "DeveloperProduct", name = "500 Candy", description = "A bag of 500 candy", price = 25, grant = "currency:500" },
                    },
                    marketing = new { hook = "Build the sweetest factory!", title_ideas = new[] { "Candy Factory Tycoon" }, keywords = new[] { "tycoon", "candy", "factory" } },
                    done_when = new[] { "A new player can buy their first dropper in under a minute", "Rebirth works and saves" },
                }));

        private static void ScriptTycoonBuild(ScriptedLlm llm)
        {
            llm.Script("builder",
                new
                {
                    summary = "Read the generated baseline (four plots round a plaza, twelve buttons each in price order) and gave it the candy look. The build compiles; the first dropper's price is next.",
                    decisions = new[] { new { what = "Kept the generated plots and gave them the candy theme", why = "every plot already has its line of twelve buttons in price order, a collector and a spawn" } },
                    problems = new[] { "Players start with 50 candy but the first dropper costs 60" },
                    next_steps = new[] { "Lower the first dropper or raise the starting candy", "Line the plaza with candy canes" },
                    key_facts = new[] { "Four candy plots face the plaza; the shop and the leaderboard are by the spawn" },
                    stage_complete = false,
                },
                ("set_milestones", new { milestones = new[] { "Core loop: droppers, conveyor and collector", "Twelve buttons and a rebirth", "Polish: decoration, leaderboard and sounds" } }),
                ("update_manifest", new { title = "Candy Factory Tycoon", currency_name = "Candy", starting_currency = 50, tips = new[] { "Step on the green buttons to buy!" } }),
                ("design_world", new { }),
                ("design_world", new { spec = new { biome = "candy", lighting = "candy", plots = new { count = 4, theme = "candy" } } }),
                ("build", new { }),
                ("update_milestone", new { id = "m1", done = false, notes = "the plots work; the first dropper is too dear" }));
            llm.Script("builder",
                new
                {
                    summary = "Lined the plaza with candy canes and added the welcome script and a spec. Revision 2 compiles with no geometry errors.",
                    next_steps = new[] { "Fix the first dropper's price", "Add the candy machine finale" },
                    stage_complete = false,
                },
                ("scatter_assets", new { asset_ids = new[] { 900105L, FakeRoblox.AssetId }, zone = "plaza", count = 8, seed = 2 }),
                ("write_file", new { path = "src/server/Welcome.server.luau", content = "local Players = game:GetService(\"Players\")\n\nPlayers.PlayerAdded:Connect(function(player)\n\tprint(\"Welcome to the factory,\", player.Name)\nend)\n" }),
                ("write_file", new { path = "src/tests/Factory.luau", content = "return {\n\t[\"the first dropper is affordable\"] = function(t)\n\t\tlocal p = t.player()\n\t\tt.ok(true)\n\tend,\n}\n" }),
                ("build", new { }),
                ("render_scene", new { view = "map" }),
                ("run_tests", new { }));
        }

        // ─────────────── seeded data ───────────────

        private static RbxInstance Part(RbxInstance parent, string name, RbxVector3 size, RbxVector3 position, RbxColor3uint8 color, uint material, uint shape = RbxPartShapes.Block)
        {
            var p = parent.AddChild(new RbxInstance("Part", name));
            p.Set("size", RbxValue.Vec3(size));
            p.Set("CFrame", RbxValue.Frame(new RbxCFrame(position)));
            p.Set("Color3uint8", RbxValue.Color8(color));
            p.Set("Material", RbxValue.Token(material));
            if (shape != RbxPartShapes.Block) p.Set("shape", RbxValue.Token(shape));
            p.Set("Anchored", RbxValue.Bool(true));
            return p;
        }

        private static (RbxDocument Doc, RbxInstance Model) Model(string name)
        {
            var doc = new RbxDocument();
            return (doc, doc.AddRoot(new RbxInstance("Model", name)));
        }

        private static AssetLabel Label(string what, string category, string sub, string style, string description, double quality, string[] themes, string[] goodFor, bool walkable = false) => new()
        {
            What = what, Category = category, Subcategory = sub, Style = style, Description = description, Quality = quality,
            Themes = themes.ToList(), GoodFor = goodFor.ToList(), PairsWith = new List<string>(), Walkable = walkable, Model = "qwen3-vl",
        };

        private static void SeedLibrary(AssetLibrary library, DateTime now)
        {
            void Seed(long id, string name, string creator, bool verified, int up, int down, string query, (RbxDocument Doc, RbxInstance Model) m, AssetLabel? label, bool analyze = true)
            {
                library.UpsertDiscovered(new[]
                {
                    new CreatorStoreAsset { AssetId = id, Name = name, Description = name + " — free model", AssetTypeId = 10, CategoryPath = "3d__models", CreatorName = creator, CreatorVerified = verified, UpVotes = up, DownVotes = down, UpVotePercent = up + down > 0 ? (int)Math.Round(100.0 * up / (up + down)) : 0, Triangles = 400 },
                }, query, 0, now);
                var prims = SchematicRenderer.PrimitivesOf(m.Model);
                var (min, max) = SchematicRenderer.Bounds(prims);
                var c = (min + max) * 0.5f;
                float r = MathF.Max(2, (max - min).Magnitude * 0.5f);
                library.SaveThumbnail(id, SchematicRenderer.RenderPng(prims, new RenderCamera { Eye = c + new RbxVector3(r * 1.9f, r * 1.5f, -r * 2.3f), Target = c, FieldOfView = 38 }, new RenderOptions { Width = 256, Height = 256, Grid = false }));
                if (!analyze) return;
                var analysis = AssetAnalyzer.Analyze(m.Doc, id, name);
                var renders = SchematicRenderer.RenderStandardViews(SchematicRenderer.PrimitivesOf(analysis.Model), 384);
                library.SaveAnalysis(id, RbxXmlWriter.WriteBytes(m.Doc), analysis, analysis.Placeable ? renders : new List<(string, byte[])>(), 400, now);
                if (label != null) library.SaveLabel(id, label, now, AssetLabeler.ScaleVersion);
            }

            var crate = Model("Wooden Crate");
            Part(crate.Model, "Box", new(4, 4, 4), new(0, 2, 0), new(156, 112, 64), RbxMaterials.Wood);
            Part(crate.Model, "Band", new(4.2f, 0.6f, 4.2f), new(0, 2, 0), new(96, 66, 38), RbxMaterials.Wood);
            Seed(900101, "Wooden Crate", "PropShop", true, 840, 22, "crate", crate,
                Label("wooden crate", "props", "container", "realistic", "A square wooden crate with a dark band around the middle.", 7, new[] { "warehouse", "pirate", "farm" }, new[] { "stacking as cover", "warehouse decoration" }, walkable: true));

            var rocks = Model("Boulder Cluster");
            Part(rocks.Model, "Big", new(6, 5, 6), new(0, 2.5f, 0), new(128, 128, 132), RbxMaterials.Slate, RbxPartShapes.Ball);
            Part(rocks.Model, "Small", new(3, 2.5f, 3), new(3.5f, 1.25f, 2), new(110, 110, 115), RbxMaterials.Slate, RbxPartShapes.Ball);
            Seed(900102, "Boulder Cluster", "NatureKit", true, 1290, 40, "rock", rocks,
                Label("grey boulder cluster", "nature", "rock", "low-poly", "Two rounded grey boulders, one large and one small, sitting together.", 7, new[] { "forest", "mountain", "obby" }, new[] { "edges of paths", "breaking up flat ground" }));

            var lamp = Model("Street Lamp");
            Part(lamp.Model, "Pole", new(0.6f, 12, 0.6f), new(0, 6, 0), new(40, 40, 44), RbxMaterials.Metal);
            Part(lamp.Model, "Arm", new(3, 0.4f, 0.4f), new(1.2f, 11.8f, 0), new(40, 40, 44), RbxMaterials.Metal);
            Part(lamp.Model, "Light", new(1.4f, 0.8f, 1.4f), new(2.4f, 11.2f, 0), new(255, 236, 160), RbxMaterials.Neon);
            Seed(900103, "Street Lamp", "CityBuilder", false, 312, 18, "lamp", lamp,
                Label("street lamp", "lighting", "lamp post", "modern", "A thin dark metal lamp post with a glowing warm light on an arm.", 6, new[] { "city", "park", "night" }, new[] { "lining roads and paths" }));

            var bench = Model("Park Bench");
            Part(bench.Model, "Seat", new(6, 0.5f, 2), new(0, 2, 0), new(150, 100, 60), RbxMaterials.Wood);
            Part(bench.Model, "Back", new(6, 2, 0.4f), new(0, 3.2f, 0.9f), new(150, 100, 60), RbxMaterials.Wood);
            Part(bench.Model, "LegL", new(0.4f, 2, 1.8f), new(-2.6f, 1, 0), new(50, 50, 54), RbxMaterials.Metal);
            Part(bench.Model, "LegR", new(0.4f, 2, 1.8f), new(2.6f, 1, 0), new(50, 50, 54), RbxMaterials.Metal);
            Seed(900104, "Park Bench", "CityBuilder", false, 560, 31, "bench", bench,
                Label("wooden park bench", "furniture", "seating", "realistic", "A wooden slatted bench with black metal legs, facing forward.", 7, new[] { "park", "city", "lobby" }, new[] { "lobbies", "parks", "waiting areas" }));

            var cane = Model("Giant Candy Cane");
            for (int i = 0; i < 6; i++)
                Part(cane.Model, "Stripe" + i, new(1.2f, 1.6f, 1.2f), new(0, 0.8f + i * 1.6f, 0), i % 2 == 0 ? new RbxColor3uint8(220, 40, 50) : new RbxColor3uint8(250, 250, 250), RbxMaterials.SmoothPlastic);
            Part(cane.Model, "Hook", new(3, 1.2f, 1.2f), new(1, 10.2f, 0), new(220, 40, 50), RbxMaterials.SmoothPlastic);
            Seed(900105, "Giant Candy Cane", "SweetWorks", true, 2210, 64, "candy", cane,
                Label("giant candy cane", "decoration", "candy", "cartoon", "A tall red-and-white striped candy cane with a hooked top.", 6, new[] { "candy", "christmas", "tycoon" }, new[] { "candy-themed decoration", "lining a path" }));

            var slab = Model("Lava Rock Platform");
            Part(slab.Model, "Rock", new(8, 1.4f, 8), new(0, 0.7f, 0), new(60, 50, 48), RbxMaterials.Slate);
            Part(slab.Model, "Glow", new(6, 0.2f, 6), new(0, 1.5f, 0), new(255, 120, 30), RbxMaterials.Neon);
            Seed(900106, "Lava Rock Platform", "ObbyParts", true, 980, 25, "lava", slab,
                Label("lava rock platform", "obby", "platform", "stylised", "A flat dark rock slab with a glowing orange top, sized for jumping on.", 8, new[] { "lava", "volcano", "obby" }, new[] { "obby stages", "stepping stones over lava" }, walkable: true));

            // Not placeable: unions cannot be published through the API.
            var arch = Model("Union Arch");
            var union = arch.Model.AddChild(new RbxInstance("UnionOperation", "Arch"));
            union.Set("size", RbxValue.Vec3(new RbxVector3(10, 8, 2)));
            Seed(900107, "Union Arch", "Builder123", false, 45, 30, "arch", arch, null);

            // Waiting for the vision labeller.
            var cone = Model("Traffic Cone");
            Part(cone.Model, "Base", new(2, 0.3f, 2), new(0, 0.15f, 0), new(240, 110, 20), RbxMaterials.Plastic);
            Part(cone.Model, "Cone", new(1.2f, 2.4f, 1.2f), new(0, 1.5f, 0), new(250, 120, 25), RbxMaterials.Plastic);
            Seed(900108, "Traffic Cone", "CityBuilder", false, 120, 6, "cone", cone, null);

            // Only discovered and previewed so far.
            var palm = Model("Cartoon Palm Tree");
            Part(palm.Model, "Trunk", new(1.2f, 9, 1.2f), new(0, 4.5f, 0), new(150, 110, 70), RbxMaterials.Wood);
            Part(palm.Model, "Leaves", new(8, 2, 8), new(0, 9.5f, 0), new(60, 170, 70), RbxMaterials.Grass, RbxPartShapes.Ball);
            Seed(900109, "Cartoon Palm Tree", "IslandPack", true, 410, 12, "palm tree", palm, null, analyze: false);
        }

        /// <summary>Sessions, deaths, purchases, feedback and a repeating error, one batch per day since launch.</summary>
        private static void IngestTelemetry(StudioContext ctx, string gameId)
        {
            var random = new Random(20261002);
            for (int day = 0; day < 18; day++)
            {
                var events = new JArray();
                int sessions = 10 + day * 3 + random.Next(0, 6);
                for (int i = 0; i < sessions; i++)
                {
                    string s = $"d{day}s{i}";
                    events.Add(new JObject { ["n"] = "session_start", ["s"] = s });
                    int reached = 1 + (random.NextDouble() < 0.62 ? 1 : 0) + (random.NextDouble() < 0.45 ? 1 : 0);
                    for (int step = 1; step <= reached; step++) events.Add(new JObject { ["n"] = "funnel", ["s"] = s, ["f"] = new JObject { ["step"] = step } });
                    for (int d = random.Next(0, 4); d > 0; d--) events.Add(new JObject { ["n"] = "death", ["s"] = s });
                    if (random.NextDouble() < 0.05) events.Add(new JObject { ["n"] = "purchase", ["s"] = s, ["f"] = new JObject { ["key"] = "skip", ["robux"] = 25 } });
                    if (random.NextDouble() < 0.01) events.Add(new JObject { ["n"] = "purchase", ["s"] = s, ["f"] = new JObject { ["key"] = "vip", ["robux"] = 149 } });
                    if (random.NextDouble() < 0.12)
                    {
                        // The Game Kit's fixed vocabulary (GameKit/Server/Feedback.luau): 1 Bad, 2 Okay, 3 Great.
                        int rating = random.NextDouble() < 0.7 ? 3 : random.NextDouble() < 0.6 ? 2 : 1;
                        string reason = rating == 3 ? (random.NextDouble() < 0.7 ? "fun" : "want_more")
                            : rating == 2 ? (random.NextDouble() < 0.6 ? "too_hard" : "too_easy")
                            : (random.NextDouble() < 0.5 ? "boring" : "bug");
                        events.Add(new JObject { ["n"] = "feedback", ["s"] = s, ["f"] = new JObject { ["rating"] = rating, ["reason"] = reason } });
                    }
                    events.Add(new JObject { ["n"] = "session_end", ["s"] = s, ["f"] = new JObject { ["seconds"] = 90 + random.Next(0, 420) } });
                }
                events.Add(new JObject { ["n"] = "heartbeat", ["f"] = new JObject { ["players"] = Math.Min(30, 3 + day) } });
                if (day % 3 == 1) events.Add(new JObject { ["n"] = "error", ["f"] = new JObject { ["message"] = "ServerScriptService.OmniKitServer.Obby:88: attempt to index nil with 'Stage'", ["count"] = day > 6 ? 25 : 1 } });
                ctx.Telemetry.Ingest(gameId, new JObject { ["server"] = "srv-" + day, ["events"] = events }, LaunchDay.Date.AddDays(day).AddHours(18));
            }
            // The telemetry route keeps these running totals on the dossier; ingesting directly skips it.
            var all = ctx.Telemetry.Summarize(gameId, LaunchDay.AddDays(-1));
            ctx.Update(gameId, g =>
            {
                var t = g.Stats.Telemetry;
                t.Events = all.Events;
                t.Sessions = all.Sessions;
                t.Errors = all.TopErrors.Sum(e => e.Count);
                t.Purchases = all.Purchases;
                t.Feedback = all.FeedbackByRating.Values.Sum();
                t.LastEventUtc = all.LastEventUtc;
            });
        }

        /// <summary>Two and a half weeks of Roblox analytics for the live game: growing players, steady revenue.</summary>
        private static void SeedDailyMetrics(StudioContext ctx, string gameId)
        {
            var random = new Random(7);
            ctx.Update(gameId, g =>
            {
                g.Stats.Daily.Clear();
                for (int day = 0; day < 18; day++)
                {
                    double dau = 40 + day * 34 + random.Next(-12, 18);
                    g.Stats.Daily.Add(new DailyMetrics
                    {
                        Date = LaunchDay.Date.AddDays(day),
                        DailyActiveUsers = dau,
                        Visits = Math.Round(dau * 1.55),
                        PlayHours = Math.Round(dau * 0.11, 1),
                        AvgSessionMinutes = Math.Round(5.2 + random.NextDouble() * 1.4, 1),
                        RevenueRobux = Math.Round(dau * (0.7 + random.NextDouble() * 0.5)),
                        PayingUsers = Math.Round(dau * 0.014),
                        D1Retention = Math.Round(0.18 + random.NextDouble() * 0.06, 3),
                        D7Retention = day >= 7 ? Math.Round(0.05 + random.NextDouble() * 0.03, 3) : null,
                        PeakConcurrent = Math.Round(dau / 11),
                        ClickThrough = Math.Round(0.042 + random.NextDouble() * 0.014, 3),
                    });
                }
                g.Stats.LastSyncUtc = Now.AddHours(-6);
                g.NextMaintenanceUtc = Now.AddDays(1).AddHours(4);
            });
        }

        private static byte[] Shrink(byte[] png, int maxWidth)
        {
            using var bitmap = SKBitmap.Decode(png);
            if (bitmap == null || bitmap.Width <= maxWidth) return png;
            int height = (int)Math.Round(bitmap.Height * (maxWidth / (double)bitmap.Width));
            using var surface = SKSurface.Create(new SKImageInfo(maxWidth, height));
            using var image = SKImage.FromBitmap(bitmap);
            surface.Canvas.DrawImage(image, new SKRect(0, 0, maxWidth, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            using var snapshot = surface.Snapshot();
            using var data = snapshot.Encode(SKEncodedImageFormat.Png, 90);
            return data.ToArray();
        }
    }
}
