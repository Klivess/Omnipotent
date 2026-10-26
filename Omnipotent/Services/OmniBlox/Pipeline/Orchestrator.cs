using System.Security.Cryptography;
using Omnipotent.Services.OmniBlox.Agents;
using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Pipeline.Tools;

namespace Omnipotent.Services.OmniBlox.Pipeline
{
    /// <summary>
    /// The studio's control loop. One unit of work at a time (AIRouter's three slots are shared with every
    /// other agent): first a live game that is broken or due maintenance or a post-mortem, otherwise the
    /// game in production moves one step (a research, build or launch cycle, the test stage, or the launch
    /// sequence), otherwise the next game starts. Stages advance only on their exit criteria.
    ///
    /// There is no cycle budget: a game keeps working until its stage is done. The <see cref="Watchdog"/>
    /// measures progress instead and, when a game stalls, changes the approach and finally asks the owner;
    /// only the owner abandons a game. A failure of the model or its provider is not the game's failure: it
    /// backs off and tries again without counting anything against the game.
    /// </summary>
    public sealed class Orchestrator
    {
        private readonly StudioContext ctx;
        private DateTime lastHousekeepingUtc = DateTime.MinValue;
        private DateTime lastAnalyticsSweepUtc = DateTime.MinValue;

        public Orchestrator(StudioContext ctx) => this.ctx = ctx;

        public string Status { get; private set; } = "starting";
        public string? CurrentGameId { get; private set; }

        public static readonly TimeSpan WaitRecheck = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan ErrorBackoff = TimeSpan.FromMinutes(5);
        public static readonly TimeSpan MaxProviderBackoff = TimeSpan.FromMinutes(30);
        public const int AbandonedInARowLimit = 3;

        public async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                bool worked = false;
                try
                {
                    worked = await StepAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Status = "error: " + ex.Message;
                    ctx.Log("[OmniBlox] orchestrator step failed: " + ex);
                    ctx.Event(EventLevel.Error, "studio.error", "Orchestrator step failed: " + ex.Message, CurrentGameId);
                    if (CurrentGameId is string failing) TryUpdate(failing, g => g.NextAttemptUtc = ctx.Clock() + ErrorBackoff);
                }
                CurrentGameId = null;
                try { await Task.Delay(worked ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(30), ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        /// <summary>Does at most one unit of work. Returns whether anything ran.</summary>
        public async Task<bool> StepAsync(CancellationToken ct)
        {
            if (!ctx.Settings.Enabled)
            {
                Status = "off (switch OmniBlox on to start the studio)";
                return false;
            }
            await HousekeepingAsync(ct);

            var (available, reason) = await ctx.Llm.CheckAvailableAsync(ct);
            if (!available)
            {
                Status = "model work paused: " + reason;
                return false;
            }

            var now = ctx.Clock();
            var games = ctx.Store.Read(s => s.Games.Values.Select(OmniBloxStore.Clone).ToList());

            // 1. Live games first: broken ones, then due maintenance, then post-mortems.
            var live = games.Where(g => LiveOps.MaintenanceDue(g, now) && Ready(g, now))
                .OrderByDescending(g => g.UrgentMaintenance).ThenBy(g => g.NextMaintenanceUtc).FirstOrDefault();
            if (live != null)
            {
                CurrentGameId = live.GameId;
                Status = $"maintaining \"{live.DisplayTitle}\"" + (live.UrgentMaintenance ? " (urgent)" : "");
                await LiveOps.RunMaintenanceAsync(ctx, live.GameId, ct);
                return true;
            }
            foreach (var g in games.Where(g => Ready(g, now)))
            {
                if (LiveOps.PostMortemDue(g, now) is int day)
                {
                    CurrentGameId = g.GameId;
                    Status = $"day-{day} post-mortem of \"{g.DisplayTitle}\"";
                    try { await LiveOps.RunPostMortemAsync(ctx, g.GameId, day, ct); }
                    catch (Exception ex) when (ex is FormatException or InvalidOperationException or Newtonsoft.Json.JsonException)
                    {
                        // Record an empty post-mortem rather than retrying forever.
                        ctx.Update(g.GameId, x => x.PostMortems.Add(new PostMortem { Utc = ctx.Clock(), DaysSinceLaunch = day, DidNotWork = "post-mortem failed: " + ex.Message }));
                    }
                    return true;
                }
            }

            // 2. The game(s) in production.
            var production = games.Where(g => g.InProduction && !g.Paused).OrderBy(g => g.CreatedUtc).ToList();
            var next = production.FirstOrDefault(g => Ready(g, now));
            if (next != null)
            {
                CurrentGameId = next.GameId;
                await AdvanceAsync(next.GameId, ct);
                return true;
            }

            // 3. Room for a new game? A game waiting on the owner (paused) still holds its place: the studio does
            // not start something new behind the owner's back while one is waiting for a decision.
            int holding = games.Count(g => g.InProduction);
            if (holding < Math.Max(1, ctx.Settings.MaxGamesInProduction))
            {
                if (AbandonedInARow(games) >= AbandonedInARowLimit)
                {
                    Status = $"stopped: the last {AbandonedInARowLimit} games were abandoned — start one from the dashboard to go again";
                    return false;
                }
                string id = StartGame();
                CurrentGameId = id;
                await AdvanceAsync(id, ct);
                return true;
            }

            Status = production.Count > 0
                ? "waiting: " + string.Join("; ", production.Select(g => $"\"{g.DisplayTitle}\" — {g.WaitingReason ?? "backing off"}"))
                : games.Any(g => g.InProduction && g.Paused) ? "waiting for you: " + string.Join("; ", games.Where(g => g.InProduction && g.Paused).Select(g => $"\"{g.DisplayTitle}\" is paused")) : "idle";
            return false;
        }

        private static bool Ready(GameDossier g, DateTime now) => !g.Paused && (g.NextAttemptUtc == null || g.NextAttemptUtc <= now);

        private static int AbandonedInARow(List<GameDossier> games)
        {
            int count = 0;
            foreach (var g in games.Where(g => g.Stage is GameStage.Abandoned or GameStage.Live or GameStage.Sunset).OrderByDescending(g => g.StageEnteredUtc))
            {
                if (g.Stage != GameStage.Abandoned) break;
                count++;
            }
            return count;
        }

        // ─────────────── production ───────────────

        public string StartGame(string? themeHint = null)
        {
            var now = ctx.Clock();
            string id = $"g{now:yyyyMMdd}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant()}";
            var game = new GameDossier
            {
                GameId = id,
                WorkingTitle = "New game",
                Stage = GameStage.Research,
                CreatedUtc = now,
                UpdatedUtc = now,
                StageEnteredUtc = now,
                TelemetrySecret = StudioContext.NewSecret(),
            };
            if (!string.IsNullOrWhiteSpace(themeHint))
                game.Directives.Add(new OwnerDirective { DirectiveId = "d1", Utc = now, Text = "Explore this theme for the next game: " + themeHint.Trim() });
            ctx.Store.Mutate(s =>
            {
                s.Games[id] = game;
                s.Touch(game, now);
                s.AddEvent(EventLevel.Info, "game.started", "Started researching a new game.", id, now);
            });
            return id;
        }

        public async Task AdvanceAsync(string gameId, CancellationToken ct)
        {
            var game = ctx.Game(gameId)!;
            if (StageGates.IsMet(game, ctx) && game.Stage != GameStage.Launch)
            {
                Enter(gameId, NextStage(game.Stage));
                return;
            }

            switch (game.Stage)
            {
                case GameStage.Research:
                    Status = $"researching \"{game.DisplayTitle}\"";
                    await RunCycleAsync(gameId, "researcher", AgentPrompts.Researcher, ResearchTools.Create(ctx, gameId), "high", ct);
                    break;
                case GameStage.Build:
                    // A game that reached Build before the world generator existed gets its generated baseline first.
                    TryScaffold(gameId);
                    Status = $"building \"{game.DisplayTitle}\" (revision {game.Build.Revision})";
                    await RunCycleAsync(gameId, "builder", AgentPrompts.Builder, BuildTools.Create(ctx, gameId), "medium", ct);
                    break;
                case GameStage.Test:
                    {
                        Status = $"testing \"{game.DisplayTitle}\"";
                        var report = await TestStage.RunAsync(ctx, gameId, withReview: true, ct);
                        if (report.Infrastructure)
                        {
                            // Roblox or the model was unreachable: nothing was learned about the game. Try again later.
                            ctx.Update(gameId, g => g.NextAttemptUtc = ctx.Clock() + ErrorBackoff);
                            return;
                        }
                        Watchdog.Observe(ctx, gameId, report.Issues.Where(i => i.Severity == "error").Select(i => $"{i.Area}: {i.Message}"), "a test run");
                        if (report.Passed) Enter(gameId, GameStage.Launch);
                        else
                        {
                            ctx.Update(gameId, g =>
                            {
                                g.StageFailures[GameStage.Test] = g.StageFailures.GetValueOrDefault(GameStage.Test) + 1;
                                g.Build.FeatureComplete = false;
                            });
                            Enter(gameId, GameStage.Build, "tests failed: " + report.Summary);
                        }
                        return;
                    }
                case GameStage.Launch:
                    // Gate 1: the owner plays this build and signs it off before any marketing work.
                    if (Approvals.SignoffNeeded(ctx, game))
                    {
                        if (!ctx.Store.Read(s => s.Studio.OwnerTasks.Any(t => t.Status == OwnerTaskStatus.Open && t.Kind == OwnerTaskKind.BuildSignoff && t.GameId == gameId && t.Title.Contains($"(r{game.Build.Revision})", StringComparison.Ordinal))))
                            Approvals.RequestSignoff(ctx, gameId);
                        Status = $"\"{game.DisplayTitle}\" is waiting for you to play r{game.Build.Revision} and sign it off";
                        ctx.Update(gameId, g => g.NextAttemptUtc = ctx.Clock() + WaitRecheck);
                        return;
                    }
                    if (!LaunchReady(game))
                    {
                        Status = $"marketing \"{game.DisplayTitle}\"";
                        await RunCycleAsync(gameId, "marketer", AgentPrompts.Marketer, LaunchTools.Create(ctx, gameId), "medium", ct);
                    }
                    else
                    {
                        Status = $"launching \"{game.DisplayTitle}\" ({game.Publish.LastLaunchStep ?? "starting"})";
                        await LaunchStepAsync(gameId, ct);
                    }
                    return;
                default:
                    return;
            }

            game = ctx.Game(gameId)!;
            if (StageGates.IsMet(game, ctx) && game.Stage != GameStage.Launch) Enter(gameId, NextStage(game.Stage));
        }

        private static bool LaunchReady(GameDossier g) => StageGates.MarketingReady(g) && g.Publish.LaunchRequestedUtc != null;

        /// <summary>Makes the game's generated baseline when it has none (never fatal: the builder can design_world itself).</summary>
        private void TryScaffold(string gameId)
        {
            try
            {
                if (GameScaffold.Ensure(ctx, gameId) is { } done) ctx.Log($"[OmniBlox] {gameId}: {done}");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or Newtonsoft.Json.JsonException or KeyNotFoundException)
            {
                ctx.Log($"[OmniBlox] {gameId}: the baseline could not be generated: {ex}");
                ctx.Event(EventLevel.Warning, "build.scaffold", "The baseline world could not be generated: " + ex.Message, gameId);
            }
        }

        private async Task LaunchStepAsync(string gameId, CancellationToken ct)
        {
            (LaunchResult result, string message) outcome;
            try
            {
                outcome = await LaunchSequence.RunAsync(ctx, gameId, ct);
            }
            catch (Exception ex) when (ex is Roblox.RobloxApiException or TimeoutException or HttpRequestException or InvalidOperationException)
            {
                outcome = (LaunchResult.Failed, ex.Message);
            }
            var game = ctx.Game(gameId)!;
            if (outcome.result != LaunchResult.Failed && game.Publish.LaunchFailures > 0) ctx.Update(gameId, g => g.Publish.LaunchFailures = 0);
            switch (outcome.result)
            {
                case LaunchResult.Done:
                    await ctx.Alert($"OmniBlox launched \"{game.Store.Title}\" — https://www.roblox.com/games/{game.Slot?.PlaceId}");
                    ResetWaiting(gameId);
                    break;
                case LaunchResult.Waiting:
                    ctx.Update(gameId, g => { g.WaitingReason = outcome.message; g.NextAttemptUtc = ctx.Clock() + WaitRecheck; });
                    break;
                case LaunchResult.BackToMarketing:
                    ctx.Update(gameId, g => { g.WaitingReason = null; g.Publish.LastLaunchError = outcome.message; g.StageFailures[GameStage.Launch] = g.StageFailures.GetValueOrDefault(GameStage.Launch) + 1; });
                    ctx.Event(EventLevel.Warning, "launch.back", "Back to marketing: " + outcome.message, gameId);
                    Watchdog.Observe(ctx, gameId, new[] { outcome.message }, "a launch step");
                    break;
                case LaunchResult.BackToBuild:
                    ctx.Update(gameId, g => { g.Publish.LastLaunchError = outcome.message; g.Publish.LaunchRequestedUtc = null; g.Build.FeatureComplete = false; });
                    Watchdog.Observe(ctx, gameId, new[] { outcome.message }, "a launch step");
                    Enter(gameId, GameStage.Build, outcome.message);
                    break;
                default:
                    {
                        // Each failure in a row waits twice as long (5 min, 10, 20… up to 6 h), and after a few the
                        // owner is asked: the first studio hammered one refused request every five minutes for hours.
                        int failures = game.Publish.LaunchFailures + 1;
                        var wait = TimeSpan.FromMinutes(Math.Min(360, ErrorBackoff.TotalMinutes * Math.Pow(2, failures - 1)));
                        ctx.Update(gameId, g =>
                        {
                            g.Publish.LastLaunchError = outcome.message;
                            g.Publish.LaunchFailures = failures;
                            g.StageFailures[GameStage.Launch] = g.StageFailures.GetValueOrDefault(GameStage.Launch) + 1;
                            g.NextAttemptUtc = ctx.Clock() + wait;
                        });
                        ctx.Event(EventLevel.Error, "launch.failed", $"Launch step failed ({failures} in a row; next try in {wait.TotalMinutes:0} min): " + outcome.message, gameId);
                        if (failures == 4)
                        {
                            ctx.Store.Mutate(s => s.FileOwnerTask(OwnerTaskKind.Escalation, "launchfail:" + gameId, $"The launch of \"{game.DisplayTitle}\" keeps failing",
                                $"The step \"{game.Publish.LastLaunchStep}\" failed {failures} times in a row: {outcome.message}\nThe studio keeps retrying, more slowly each time. Fix what the error points at (often an API key permission) and Resume the game.", gameId, ctx.Clock()));
                            await ctx.Alert($"OmniBlox: the launch of \"{game.DisplayTitle}\" keeps failing at \"{game.Publish.LastLaunchStep}\": {outcome.message}");
                        }
                        break;
                    }
            }
        }

        /// <summary>The reasoning effort for a cycle: the role's own, raised to high while the watchdog says the game is stalling.</summary>
        internal static string Effort(GameDossier game, string roleEffort) => game.Progress.Level >= 1 ? "high" : roleEffort;

        private async Task RunCycleAsync(string gameId, string role, string prompt, AgentToolbox tools, string effort, CancellationToken ct)
        {
            var game = ctx.Game(gameId)!;
            tools.OnInternalError = (tool, ex) => ctx.Log($"[OmniBlox] tool {tool} failed for {gameId}: {ex}");
            var outcome = await ctx.Runner.RunAsync(new CycleRequest
            {
                GameId = gameId,
                Stage = game.Stage,
                Role = role,
                SystemPrompt = prompt,
                Briefing = Briefing.Build(game, ctx),
                Tools = tools,
                ContextWindowTokens = ctx.Settings.ModelContextTokens,
                Options = new LlmCallOptions { MaxTokens = ctx.Settings.MaxOutputTokens, Thinking = Effort(game, effort) },
                Observer = new CycleLog(ctx, gameId),
                OwnerMessages = () => TakeNewDirectives(ctx, gameId),
            }, ct);
            Record(ctx, gameId, outcome);
            AfterCycle(ctx, gameId, outcome);
            if (outcome.BlockedReason is string blocked)
            {
                var now = ctx.Clock();
                ctx.Store.Mutate(s => s.FileOwnerTask(OwnerTaskKind.Escalation, "blocked:" + gameId + ":" + Math.Abs(blocked.GetHashCode()), $"OmniBlox needs you for \"{game.DisplayTitle}\"", blocked, gameId, now));
                TryUpdate(gameId, g => { g.WaitingReason = blocked; g.NextAttemptUtc = now + TimeSpan.FromHours(1); });
                await ctx.Alert($"OmniBlox is blocked on \"{game.DisplayTitle}\": {blocked}");
            }
        }

        /// <summary>
        /// After a cycle: a provider failure backs off (longer each time in a row) without touching the game's
        /// record; anything else is fed to the watchdog as progress or a stall.
        /// </summary>
        internal static void AfterCycle(StudioContext ctx, string gameId, CycleOutcome outcome)
        {
            if (outcome.ProviderError)
            {
                int failures = 0;
                ctx.Update(gameId, g => failures = ++g.ProviderFailures);
                var wait = TimeSpan.FromMinutes(Math.Min(MaxProviderBackoff.TotalMinutes, Math.Pow(2, Math.Min(failures, 6) - 1)));
                ctx.Update(gameId, g => g.NextAttemptUtc = ctx.Clock() + wait);
                ctx.Event(EventLevel.Warning, "model.unavailable", $"The model call failed ({Clip(outcome.Error ?? "", 200)}); trying again in {wait.TotalMinutes:0} min. This does not count against the game.", gameId);
                return;
            }
            ctx.Update(gameId, g => g.ProviderFailures = 0);
            var game = ctx.Game(gameId);
            if (game == null) return;
            var failuresNow = new List<string>();
            if (outcome.Error != null) failuresNow.Add("the cycle failed: " + outcome.Error);
            if (game.Stage == GameStage.Build && !game.Build.LastBuildOk && game.Build.LastBuildUtc != null) failuresNow.AddRange(game.Build.Errors.Take(8));
            Watchdog.Observe(ctx, gameId, failuresNow, $"a {outcome.Report.Role} cycle");
        }

        /// <summary>Owner directives that a running cycle has not seen yet (marked as delivered).</summary>
        internal static IReadOnlyList<(string Id, string Text)> TakeNewDirectives(StudioContext ctx, string gameId)
        {
            var list = new List<(string, string)>();
            ctx.Store.Mutate(s =>
            {
                if (s.Game(gameId) is not { } g) return;
                foreach (var d in g.Directives.Where(d => d.Status == DirectiveStatus.Open && !d.Delivered))
                {
                    d.Delivered = true;
                    list.Add((d.DirectiveId, d.Text));
                }
                if (list.Count > 0) s.MarkGame(gameId);
            });
            return list;
        }

        /// <summary>Writes a cycle's hand-over into the dossier: report, decisions, kept facts, directives, counters, usage.</summary>
        public static void Record(StudioContext ctx, string gameId, CycleOutcome outcome)
        {
            ctx.Update(gameId, g =>
            {
                g.Cycles.Add(outcome.Report);
                if (g.Cycles.Count > GameDossier.MaxCycleReports) g.Cycles.RemoveRange(0, g.Cycles.Count - GameDossier.MaxCycleReports);
                g.Decisions.AddRange(outcome.NewDecisions);
                if (g.Decisions.Count > GameDossier.MaxDecisions) g.Decisions.RemoveRange(0, g.Decisions.Count - GameDossier.MaxDecisions);
                FactBook.Merge(g.KeyFacts, outcome.KeyFacts, outcome.DropFacts);
                foreach (var (id, response) in outcome.DirectivesDone)
                {
                    var d = g.Directives.FirstOrDefault(x => x.DirectiveId.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (d == null || d.Status != DirectiveStatus.Open) continue;
                    d.Status = DirectiveStatus.Done;
                    d.Response = response.Length > 0 ? response : "done";
                    d.ResolvedUtc = ctx.Clock();
                }
                if (!outcome.ProviderError) g.StageCycles[outcome.Report.Stage] = g.StageCycles.GetValueOrDefault(outcome.Report.Stage) + 1;
            });
            ctx.AddUsage(gameId, outcome.Report);
            ctx.Event(outcome.Error != null ? EventLevel.Warning : EventLevel.Info, "cycle." + outcome.Report.Role,
                $"{outcome.Report.Role} cycle ({outcome.Report.Turns} turns, {outcome.Report.ToolCalls} calls, {outcome.Report.Outcome}): {Clip(outcome.Report.Done, 240)}", gameId);
        }

        private static GameStage NextStage(GameStage stage) => stage switch
        {
            GameStage.Research => GameStage.Build,
            GameStage.Build => GameStage.Test,
            GameStage.Test => GameStage.Launch,
            GameStage.Launch => GameStage.Live,
            _ => stage,
        };

        /// <summary>Moves a game to a stage, preparing what that stage needs.</summary>
        public void Enter(string gameId, GameStage stage, string? why = null)
        {
            var now = ctx.Clock();
            if (stage == GameStage.Build)
            {
                var game = ctx.Game(gameId)!;
                var ws = ctx.Workspace(gameId);
                if (!ws.Exists && game.Brief is { } brief)
                {
                    ws.EnsureCreated(new GameManifest
                    {
                        Title = brief.Title,
                        Template = GameScaffold.TemplateFor(brief, new GameManifest()),
                        TemplateSettings = new Dictionary<string, object>(),
                    });
                    ctx.Harvester.RequestAssets(brief.AssetNeeds);
                }
                TryScaffold(gameId);
            }
            ctx.Store.Mutate(s =>
            {
                var g = s.Game(gameId)!;
                if (stage == GameStage.Build)
                {
                    if (g.Products.Count == 0 && g.Brief != null)
                        g.Products = g.Brief.Monetization.Select(p => new ProductRecord { Key = p.Key, Kind = p.Kind, Name = p.Name, Description = p.Description, PriceRobux = p.PriceRobux, Grant = p.Grant }).ToList();
                    if (g.Milestones.Count == 0)
                        g.Milestones = new List<Milestone>
                        {
                            new() { MilestoneId = "m1", Title = "Know the baseline: read design.md, look at the renders (spawn, aerial, every zone), map_report and the economy timeline; write in design.md what will make this game special" },
                            new() { MilestoneId = "m2", Title = "The world: tune the world spec (zone names and biomes, the models, density, lighting) with design_world until every render looks great and the map scores 85+" },
                            new() { MilestoneId = "m3", Title = "The loop for this brief: the template configured (tool, items, tiers, prices with balance_economy); custom systems only for what the template lacks, each with specs; run_tests green" },
                            new() { MilestoneId = "m4", Title = "Every must-have from the brief built, with its UI and quests" },
                            new() { MilestoneId = "m5", Title = "Polish: the tutorial quests, juice (effects, sounds, music), theme colours, and every test green on the final build" },
                        };
                    if (string.IsNullOrEmpty(g.TelemetrySecret)) g.TelemetrySecret = StudioContext.NewSecret();
                }
                if (stage == GameStage.Launch && string.IsNullOrWhiteSpace(g.Store.Title) && g.Brief != null)
                    g.Store.Genre = g.Brief.Genre;
                g.Stage = stage;
                g.StageEnteredUtc = now;
                g.WaitingReason = null;
                g.NextAttemptUtc = null;
                // Re-entering a stage (Build after a failed test) keeps its count; it is a statistic, not a budget.
                if (!g.StageCycles.ContainsKey(stage)) g.StageCycles[stage] = 0;
                s.Touch(g, now);
                s.AddEvent(EventLevel.Info, "game.stage", $"\"{g.DisplayTitle}\" → {stage}" + (why != null ? $" ({Clip(why, 200)})" : ""), gameId, now);
            });
        }

        /// <summary>
        /// The owner gives up on a game. It keeps everything (files, snapshots, history) and can be revived where it
        /// stopped; its experience slot is freed for the next game.
        /// </summary>
        public static void Abandon(StudioContext ctx, string gameId, string reason)
        {
            var now = ctx.Clock();
            var game = ctx.Game(gameId) ?? throw new KeyNotFoundException("No game " + gameId);
            if (game.Stage is GameStage.Live or GameStage.Sunset) throw new InvalidOperationException("Live games are sunset by maintenance, not abandoned.");
            if (game.Stage == GameStage.Abandoned) return;
            Slots.Release(ctx, gameId);
            ctx.Store.Mutate(s =>
            {
                var g = s.Game(gameId)!;
                g.AbandonedFromStage = g.Stage;
                g.Stage = GameStage.Abandoned;
                g.AbandonReason = Clip(reason, 400);
                g.StageEnteredUtc = now;
                g.WaitingReason = null;
                g.Paused = false;
                s.ResolveOwnerTasks("stuck:" + gameId, now);
                s.ResolveOwnerTasks("approve:" + gameId, now);
                s.Touch(g, now);
                s.AddEvent(EventLevel.Warning, "owner.abandon", "Abandoned by the owner: " + Clip(reason, 200), gameId, now);
            });
        }

        /// <summary>
        /// Brings an abandoned game back to the stage it was abandoned in (the owner's call). Its files, snapshots
        /// and history are all still there; the watchdog starts over.
        /// </summary>
        public static void Revive(StudioContext ctx, string gameId)
        {
            var now = ctx.Clock();
            ctx.Store.Mutate(s =>
            {
                var g = s.Game(gameId) ?? throw new KeyNotFoundException("No game " + gameId);
                if (g.Stage != GameStage.Abandoned) throw new InvalidOperationException("Only an abandoned game can be revived.");
                var stage = g.AbandonedFromStage ?? (g.Brief == null ? GameStage.Research : GameStage.Build);
                if (stage == GameStage.Test || stage == GameStage.Launch) stage = GameStage.Build; // re-earn the test on the current kit
                if (g.Slot is { } slot)
                {
                    // Its experience may have gone to another game meanwhile: then it gets a free one when it next needs one.
                    string key = slot.ToString();
                    if (s.Studio.SlotAssignments.TryGetValue(key, out var holder) && holder != gameId && s.Game(holder) is { Stage: not GameStage.Abandoned })
                        g.Slot = null;
                    else
                    {
                        s.Studio.SlotAssignments[key] = gameId;
                        // Products a later game's launch took off sale go back on sale when this one launches again.
                        foreach (var p in g.Products.Where(p => p.Retired)) { p.SyncedUtc = null; p.Retired = false; p.RetiredUtc = null; }
                    }
                    s.MarkStudio();
                }
                g.Stage = stage;
                g.StageEnteredUtc = now;
                g.AbandonReason = null;
                g.Paused = false;
                g.WaitingReason = null;
                g.NextAttemptUtc = null;
                g.Build.FeatureComplete = false;
                g.Publish.LaunchRequestedUtc = null;
                g.StageFailures.Clear();
                Watchdog.Reset(g, now, "revived by the owner");
                FactBook.Merge(g.KeyFacts, new[]
                {
                    new KeyFact { Key = "revived", Text = $"Revived by the owner on {now:yyyy-MM-dd} after it had been abandoned. The studio's Game Kit and tools have changed since: rebuild, run run_tests and fix what it reports rather than trusting older notes about failing tests.", Utc = now, Stage = stage, Source = FactBook.Studio },
                });
                s.Touch(g, now);
                s.AddEvent(EventLevel.Success, "game.revived", $"\"{g.DisplayTitle}\" revived by the owner; back to {stage}.", gameId, now);
            });
        }

        /// <summary>The owner resumed a game the watchdog paused (or any paused game): it starts over on the ladder.</summary>
        public static void Resume(StudioContext ctx, string gameId)
        {
            var now = ctx.Clock();
            ctx.Store.Mutate(s =>
            {
                var g = s.Game(gameId) ?? throw new KeyNotFoundException("No game " + gameId);
                g.Paused = false;
                g.WaitingReason = null;
                g.NextAttemptUtc = null;
                Watchdog.Reset(g, now, "resumed by the owner");
                s.ResolveOwnerTasks("stuck:" + gameId, now);
                s.Touch(g, now);
            });
        }

        private void ResetWaiting(string gameId) => TryUpdate(gameId, g => { g.WaitingReason = null; g.NextAttemptUtc = null; });

        private void TryUpdate(string gameId, Action<GameDossier> change)
        {
            try { ctx.Update(gameId, change); }
            catch (KeyNotFoundException) { }
        }

        // ─────────────── housekeeping (no model calls) ───────────────

        private async Task HousekeepingAsync(CancellationToken ct)
        {
            var now = ctx.Clock();
            if (now - lastHousekeepingUtc < TimeSpan.FromMinutes(5)) return;
            lastHousekeepingUtc = now;

            ctx.Store.Mutate(s =>
            {
                if (ctx.Cloud.HasKey) s.ResolveOwnerTasks("apikey", now);
                if (Slots.AnyFree(ctx) || Slots.CanCreate(ctx)) s.ResolveOwnerTasks(Slots.OwnerKey, now);
                // A game waiting on the owner gets looked at again as soon as its task is done.
                foreach (var g in s.Games.Values.Where(g => g.WaitingReason != null && !g.Paused))
                {
                    bool stillBlocked = s.Studio.OwnerTasks.Any(t => t.Status == OwnerTaskStatus.Open && (t.GameId == g.GameId && t.Kind is OwnerTaskKind.LaunchApproval or OwnerTaskKind.BuildSignoff or OwnerTaskKind.Escalation || t.GameId == null && t.Kind is OwnerTaskKind.ApiKey or OwnerTaskKind.ExperienceSlot));
                    if (!stillBlocked && g.NextAttemptUtc > now)
                    {
                        g.NextAttemptUtc = now;
                        s.MarkGame(g.GameId);
                    }
                }
            });

            foreach (var id in ctx.Store.Read(s => s.Games.Values.Where(g => g.Stage == GameStage.Live).Select(g => g.GameId).ToList()))
                LiveOps.FlagUrgent(ctx, id);

            // The Creator Dashboard chores launches left: old products off sale, icons, thumbnails, questionnaires.
            try { await StorePageChores.SweepAsync(ctx, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { ctx.Log("[OmniBlox] store-page chores: " + ex); }

            // Experiences the studio created that are not ready yet (the API key could not reach them).
            try { await Slots.SweepAsync(ctx, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { ctx.Log("[OmniBlox] experiences: " + ex); }

            if (ctx.Cloud.HasKey && now - lastAnalyticsSweepUtc > TimeSpan.FromHours(12))
            {
                lastAnalyticsSweepUtc = now;
                foreach (var id in ctx.Store.Read(s => s.Games.Values.Where(g => g.Stage is GameStage.Live or GameStage.Sunset && g.Slot != null).Select(g => g.GameId).ToList()))
                {
                    try { await ctx.Analytics.SyncAsync(id, ct, 3); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { ctx.Log($"[OmniBlox] analytics sync {id}: {ex.Message}"); }
                }
                try { ctx.Telemetry.Prune(now); } catch (Exception ex) { ctx.Log("[OmniBlox] telemetry prune: " + ex.Message); }
            }
        }

        private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
    }
}
