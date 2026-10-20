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
    /// sequence), otherwise the next game starts. Stages advance only on their exit criteria. A watchdog
    /// resets a stuck stage once, then abandons the game and moves on; three abandoned games in a row stop
    /// the studio and tell the owner.
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

            // 3. Room for a new game?
            if (production.Count < Math.Max(1, ctx.Settings.MaxGamesInProduction))
            {
                if (AbandonedInARow(games) >= AbandonedInARowLimit)
                {
                    Status = $"stopped: the last {AbandonedInARowLimit} games were abandoned — see the escalations";
                    return false;
                }
                string id = StartGame();
                CurrentGameId = id;
                await AdvanceAsync(id, ct);
                return true;
            }

            Status = production.Count > 0 ? "waiting: " + string.Join("; ", production.Select(g => $"\"{g.DisplayTitle}\" — {g.WaitingReason ?? "backing off"}")) : "idle";
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
            if (!string.IsNullOrWhiteSpace(themeHint)) game.KeyFacts.Add("The owner suggested exploring this theme: " + themeHint.Trim());
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
            if (StageGates.IsMet(game) && game.Stage != GameStage.Launch)
            {
                Enter(gameId, NextStage(game.Stage));
                return;
            }
            if (game.Stage != GameStage.Launch || !LaunchReady(game))
            {
                if (game.StageCycles.GetValueOrDefault(game.Stage) >= StageGates.CycleBudget(game.Stage) || game.StageFailures.GetValueOrDefault(game.Stage) >= 6)
                {
                    Watchdog(gameId, $"{game.Stage} has taken {game.StageCycles.GetValueOrDefault(game.Stage)} cycles and failed {game.StageFailures.GetValueOrDefault(game.Stage)} times");
                    return;
                }
            }

            switch (game.Stage)
            {
                case GameStage.Research:
                    Status = $"researching \"{game.DisplayTitle}\"";
                    await RunCycleAsync(gameId, "researcher", AgentPrompts.Researcher, ResearchTools.Create(ctx, gameId), 36, ct);
                    break;
                case GameStage.Build:
                    Status = $"building \"{game.DisplayTitle}\" (revision {game.Build.Revision})";
                    await RunCycleAsync(gameId, "builder", AgentPrompts.Builder, BuildTools.Create(ctx, gameId), 45, ct);
                    break;
                case GameStage.Test:
                    {
                        Status = $"testing \"{game.DisplayTitle}\"";
                        var report = await TestStage.RunAsync(ctx, gameId, withReview: true, ct);
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
                    if (!LaunchReady(game))
                    {
                        Status = $"marketing \"{game.DisplayTitle}\"";
                        await RunCycleAsync(gameId, "marketer", AgentPrompts.Marketer, LaunchTools.Create(ctx, gameId), 36, ct);
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
            if (StageGates.IsMet(game) && game.Stage != GameStage.Launch) Enter(gameId, NextStage(game.Stage));
        }

        private static bool LaunchReady(GameDossier g) => StageGates.MarketingReady(g) && g.Publish.LaunchRequestedUtc != null;

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
                    break;
                case LaunchResult.BackToBuild:
                    ctx.Update(gameId, g => { g.Publish.LastLaunchError = outcome.message; g.Publish.LaunchRequestedUtc = null; g.Build.FeatureComplete = false; });
                    Enter(gameId, GameStage.Build, outcome.message);
                    break;
                default:
                    ctx.Update(gameId, g =>
                    {
                        g.Publish.LastLaunchError = outcome.message;
                        g.StageFailures[GameStage.Launch] = g.StageFailures.GetValueOrDefault(GameStage.Launch) + 1;
                        g.NextAttemptUtc = ctx.Clock() + ErrorBackoff;
                    });
                    ctx.Event(EventLevel.Error, "launch.failed", "Launch step failed: " + outcome.message, gameId);
                    break;
            }
        }

        private async Task RunCycleAsync(string gameId, string role, string prompt, AgentToolbox tools, int maxTurns, CancellationToken ct)
        {
            var game = ctx.Game(gameId)!;
            var outcome = await ctx.Runner.RunAsync(new CycleRequest
            {
                GameId = gameId,
                Stage = game.Stage,
                Role = role,
                SystemPrompt = prompt,
                Briefing = Briefing.Build(game, ctx),
                Tools = tools,
                MaxTurns = maxTurns,
                Options = new LlmCallOptions { MaxTokens = 6000, Thinking = "medium" },
            }, ct);
            Record(ctx, gameId, outcome);
            if (outcome.Error != null)
                TryUpdate(gameId, g =>
                {
                    g.StageFailures[g.Stage] = g.StageFailures.GetValueOrDefault(g.Stage) + 1;
                    g.NextAttemptUtc = ctx.Clock() + ErrorBackoff;
                });
            if (outcome.BlockedReason is string blocked)
            {
                var now = ctx.Clock();
                ctx.Store.Mutate(s => s.FileOwnerTask(OwnerTaskKind.Escalation, "blocked:" + gameId + ":" + Math.Abs(blocked.GetHashCode()), $"OmniBlox needs you for \"{game.DisplayTitle}\"", blocked, gameId, now));
                TryUpdate(gameId, g => { g.WaitingReason = blocked; g.NextAttemptUtc = now + TimeSpan.FromHours(1); });
                await ctx.Alert($"OmniBlox is blocked on \"{game.DisplayTitle}\": {blocked}");
            }
        }

        /// <summary>Writes a cycle's hand-over into the dossier: report, decisions, kept facts, counters, usage.</summary>
        public static void Record(StudioContext ctx, string gameId, CycleOutcome outcome)
        {
            ctx.Update(gameId, g =>
            {
                g.Cycles.Add(outcome.Report);
                if (g.Cycles.Count > GameDossier.MaxCycleReports) g.Cycles.RemoveRange(0, g.Cycles.Count - GameDossier.MaxCycleReports);
                g.Decisions.AddRange(outcome.NewDecisions);
                if (g.Decisions.Count > GameDossier.MaxDecisions) g.Decisions.RemoveRange(0, g.Decisions.Count - GameDossier.MaxDecisions);
                foreach (var fact in outcome.KeyFacts)
                    if (!g.KeyFacts.Contains(fact, StringComparer.OrdinalIgnoreCase)) g.KeyFacts.Add(fact);
                if (g.KeyFacts.Count > GameDossier.MaxKeyFacts) g.KeyFacts.RemoveRange(0, g.KeyFacts.Count - GameDossier.MaxKeyFacts);
                g.StageCycles[outcome.Report.Stage] = g.StageCycles.GetValueOrDefault(outcome.Report.Stage) + 1;
            });
            ctx.AddUsage(gameId, outcome.Report);
            ctx.Event(outcome.Error != null ? EventLevel.Warning : EventLevel.Info, "cycle." + outcome.Report.Role,
                $"{outcome.Report.Role} cycle ({outcome.Report.Turns} turns, {outcome.Report.Outcome}): {Clip(outcome.Report.Done, 240)}", gameId);
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
                        Template = brief.Template,
                        TemplateSettings = new Dictionary<string, object>(),
                    });
                    ctx.Harvester.RequestAssets(brief.AssetNeeds);
                }
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
                            new() { MilestoneId = "m1", Title = "Core loop playable end to end" },
                            new() { MilestoneId = "m2", Title = "Every must-have from the brief built" },
                            new() { MilestoneId = "m3", Title = "Polish: decoration, lighting, signs, tips, balance" },
                        };
                    if (string.IsNullOrEmpty(g.TelemetrySecret)) g.TelemetrySecret = StudioContext.NewSecret();
                }
                if (stage == GameStage.Launch && string.IsNullOrWhiteSpace(g.Store.Title) && g.Brief != null)
                    g.Store.Genre = g.Brief.Genre;
                var previous = g.Stage;
                g.Stage = stage;
                g.StageEnteredUtc = now;
                g.WaitingReason = null;
                g.NextAttemptUtc = null;
                // Re-entering a stage (Build after a failed test) keeps its count: rework spends the same budget.
                if (!g.StageCycles.ContainsKey(stage)) g.StageCycles[stage] = 0;
                s.Touch(g, now);
                s.AddEvent(EventLevel.Info, "game.stage", $"\"{g.DisplayTitle}\" → {stage}" + (why != null ? $" ({Clip(why, 200)})" : ""), gameId, now);
            });
        }

        /// <summary>A stage ran over budget: reset its approach once, then give the game up.</summary>
        public void Watchdog(string gameId, string why)
        {
            var game = ctx.Game(gameId)!;
            var now = ctx.Clock();
            if (game.Escalations >= 1)
            {
                Slots.Release(ctx, gameId);
                ctx.Store.Mutate(s =>
                {
                    var g = s.Game(gameId)!;
                    g.Stage = GameStage.Abandoned;
                    g.AbandonReason = why;
                    g.StageEnteredUtc = now;
                    g.Escalations++;
                    s.Touch(g, now);
                    s.AddEvent(EventLevel.Error, "game.abandoned", $"Gave up on \"{g.DisplayTitle}\": {why}", gameId, now);
                });
                _ = ctx.Alert($"OmniBlox gave up on \"{game.DisplayTitle}\" ({why}) and is moving on to the next game.");
                return;
            }
            ctx.Store.Mutate(s =>
            {
                var g = s.Game(gameId)!;
                g.Escalations++;
                switch (g.Stage)
                {
                    case GameStage.Research:
                        g.Concepts.Clear();
                        g.Brief = null;
                        g.KeyFacts.Add("Watchdog: the first research attempt stalled; start over and pick a simpler, clearly feasible idea.");
                        break;
                    case GameStage.Build:
                        g.KeyFacts.Add("Watchdog: the build stalled; cut scope to the core loop plus the most important must-haves and finish.");
                        g.Build.FeatureComplete = false;
                        break;
                    case GameStage.Test:
                        g.Stage = GameStage.Build;
                        g.StageEnteredUtc = now;
                        g.Build.FeatureComplete = false;
                        g.KeyFacts.Add("Watchdog: tests kept failing; fix the reported issues one by one, simplify anything fragile, and rebuild.");
                        break;
                    case GameStage.Launch:
                        g.Publish.LaunchRequestedUtc = null;
                        g.Store.ComplianceOk = null;
                        g.KeyFacts.Add("Watchdog: launch stalled; simplify the store page and products and fix the compliance problems.");
                        break;
                }
                g.StageCycles[g.Stage] = StageGates.CycleBudget(g.Stage) / 2;
                g.StageFailures[g.Stage] = 0;
                s.Touch(g, now);
                s.FileOwnerTask(OwnerTaskKind.Escalation, "watchdog:" + gameId + ":" + g.Stage, $"OmniBlox is struggling with \"{g.DisplayTitle}\" ({g.Stage})",
                    why + ". The studio is trying once more with a simpler approach; nothing is needed unless you want to steer it.", gameId, now);
            });
            if (game.Stage == GameStage.Build && game.Build.Revision > 0)
            {
                try { ctx.Workspace(gameId).Restore(game.Build.Revision); }
                catch (Exception ex) when (ex is IOException or FileNotFoundException) { ctx.Log("[OmniBlox] watchdog restore failed: " + ex.Message); }
            }
            _ = ctx.Alert($"OmniBlox is struggling with \"{game.DisplayTitle}\" ({why}); retrying with a simpler approach.");
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
                if (Slots.AnyFree(ctx)) s.ResolveOwnerTasks("slots", now);
                // A game waiting on the owner gets looked at again as soon as its task is done.
                foreach (var g in s.Games.Values.Where(g => g.WaitingReason != null))
                {
                    bool stillBlocked = s.Studio.OwnerTasks.Any(t => t.Status == OwnerTaskStatus.Open && (t.GameId == g.GameId && t.Kind is OwnerTaskKind.LaunchApproval or OwnerTaskKind.Escalation || t.GameId == null && t.Kind is OwnerTaskKind.ApiKey or OwnerTaskKind.ExperienceSlot));
                    if (!stillBlocked && g.NextAttemptUtc > now)
                    {
                        g.NextAttemptUtc = now;
                        s.MarkGame(g.GameId);
                    }
                }
            });

            foreach (var id in ctx.Store.Read(s => s.Games.Values.Where(g => g.Stage == GameStage.Live).Select(g => g.GameId).ToList()))
                LiveOps.FlagUrgent(ctx, id);

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
