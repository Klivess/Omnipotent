using System.Text;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniBlox.Agents;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Insights;
using Omnipotent.Services.OmniBlox.Pipeline.Tools;
using Omnipotent.Services.OmniBlox.Roblox;

namespace Omnipotent.Services.OmniBlox.Pipeline
{
    /// <summary>
    /// Running live games: the maintenance session (numbers → decisions → changes → ship), shipping updates
    /// through the same build and engine tests as a launch, the day-7 and day-30 post-mortems that feed the
    /// Playbook, and the cheap checks that pull maintenance forward when a game breaks.
    /// </summary>
    public static class LiveOps
    {
        public static readonly int[] PostMortemDays = { 7, 30 };

        public static bool MaintenanceDue(GameDossier g, DateTime now) =>
            g.Stage == GameStage.Live && !g.Paused && (g.UrgentMaintenance || g.NextMaintenanceUtc is DateTime due && due <= now);

        public static int? PostMortemDue(GameDossier g, DateTime now)
        {
            if (g.Stage is not (GameStage.Live or GameStage.Sunset) || g.Publish.WentPublicUtc is not DateTime live) return null;
            foreach (int day in PostMortemDays)
                if (now - live >= TimeSpan.FromDays(day) && !g.PostMortems.Any(p => p.DaysSinceLaunch >= day)) return day;
            return null;
        }

        public static async Task RunMaintenanceAsync(StudioContext ctx, string gameId, CancellationToken ct)
        {
            if (ctx.Cloud.HasKey)
            {
                try { await ctx.Analytics.SyncAsync(gameId, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { ctx.Log($"[OmniBlox] analytics sync for {gameId} failed: {ex.Message}"); }
            }
            var game = ctx.Game(gameId)!;
            var telemetry = ctx.Telemetry.Summarize(gameId, ctx.Clock().AddDays(-7));
            string extra = "## This week's numbers\n" + AnalyticsSync.Describe(game, 14) + "\n\n## The game's own telemetry (7 days)\n" + TelemetryStore.Describe(telemetry)
                + (game.UrgentMaintenance ? "\n\nURGENT: this session was pulled forward because something looks broken (see errors above). Fix that first." : "");
            var outcome = await ctx.Runner.RunAsync(new CycleRequest
            {
                GameId = gameId,
                Stage = GameStage.Live,
                Role = "maintainer",
                SystemPrompt = AgentPrompts.Maintainer,
                Briefing = Briefing.Build(game, ctx, extra),
                Tools = MaintenanceTools.Create(ctx, gameId),
                MaxTurns = 40,
                Options = new LlmCallOptions { MaxTokens = 4096, Thinking = "medium" },
            }, ct);
            Orchestrator.Record(ctx, gameId, outcome);

            game = ctx.Game(gameId)!;
            var record = new MaintenanceRecord { Utc = ctx.Clock(), Summary = Clip(outcome.Report.Done, 600), Urgent = game.UrgentMaintenance, Actions = outcome.Report.Decisions.Take(10).ToList() };
            if (game.PendingUpdateNotes is string notes)
            {
                var (ok, message) = await ShipUpdateAsync(ctx, gameId, notes, ct);
                record.ShippedUpdate = ok;
                record.Actions.Add((ok ? "Shipped update: " : "Update failed: ") + message);
            }
            int interval = game.Publish.WentPublicUtc is DateTime live && ctx.Clock() - live < TimeSpan.FromDays(14) ? 3 : Math.Max(1, ctx.Settings.MaintenanceIntervalDays);
            ctx.Update(gameId, g =>
            {
                g.Maintenance.Add(record);
                if (g.Maintenance.Count > 60) g.Maintenance.RemoveRange(0, g.Maintenance.Count - 60);
                g.UrgentMaintenance = false;
                g.NextMaintenanceUtc = ctx.Clock().AddDays(record.ShippedUpdate || outcome.Error == null ? interval : 1);
                if (g.SunsetReason != null)
                {
                    g.Stage = GameStage.Sunset;
                    g.StageEnteredUtc = ctx.Clock();
                    g.NextMaintenanceUtc = null;
                }
            });
            if (ctx.Game(gameId)!.Stage == GameStage.Sunset)
                ctx.Event(EventLevel.Info, "game.sunset", $"Stopped maintaining \"{game.DisplayTitle}\": {game.SunsetReason}", gameId);
        }

        /// <summary>Rebuild → engine tests → publish → re-apply the store page. The live game is only touched when every check passes.</summary>
        public static async Task<(bool Ok, string Message)> ShipUpdateAsync(StudioContext ctx, string gameId, string notes, CancellationToken ct)
        {
            ctx.Update(gameId, g => g.PendingUpdateNotes = null);
            var game = ctx.Game(gameId)!;
            if (game.Slot is not { } slot) return (false, "the game has no experience");
            if (!ctx.Cloud.HasKey) return (false, "no API key");
            var run = BuildTools.RunBuild(ctx, gameId);
            if (!run.Ok) return (false, "build failed: " + string.Join("; ", run.Compile.Errors.Take(3)));
            int revision = ctx.Game(gameId)!.Build.Revision;
            try
            {
                var engine = await EngineTests.RunAsync(ctx, slot, run.Compile.PlaceXml, ct);
                if (!engine.Ok)
                {
                    ctx.Update(gameId, g => g.UrgentMaintenance = false);
                    return (false, "engine tests failed: " + string.Join("; ", engine.Failures.Take(3)));
                }
                var published = await ctx.Cloud.PublishPlaceAsync(slot.UniverseId, slot.PlaceId, run.Compile.PlaceXml, isXml: true, publish: true, ct);
                if (!game.Store.Applied && game.Store.ComplianceOk == true)
                {
                    await ctx.Cloud.UpdatePlaceAsync(slot.UniverseId, slot.PlaceId, game.Store.Title, game.Store.Description, ct);
                    ctx.Update(gameId, g => g.Store.Applied = true);
                }
                ctx.Update(gameId, g =>
                {
                    g.Publish.LastPublishedVersion = published.VersionNumber;
                    g.Publish.LastPublishedRevision = revision;
                    g.Publish.LastPublishedUtc = ctx.Clock();
                    g.Publish.EngineTestedRevision = revision;
                });
                ctx.Event(EventLevel.Success, "game.updated", $"Shipped an update to \"{game.DisplayTitle}\": {Clip(notes, 200)}", gameId);
                return (true, $"version {published.VersionNumber}: {Clip(notes, 200)}");
            }
            catch (Exception ex) when (ex is RobloxApiException or TimeoutException or HttpRequestException)
            {
                return (false, ex.Message);
            }
        }

        public static async Task RunPostMortemAsync(StudioContext ctx, string gameId, int day, CancellationToken ct)
        {
            if (ctx.Cloud.HasKey)
            {
                try { await ctx.Analytics.SyncAsync(gameId, ct, 35); }
                catch (Exception ex) when (ex is not OperationCanceledException) { ctx.Log($"[OmniBlox] analytics sync for {gameId} failed: {ex.Message}"); }
            }
            var game = ctx.Game(gameId)!;
            var sb = new StringBuilder();
            sb.AppendLine($"Day {day} post-mortem for \"{game.DisplayTitle}\" ({game.Brief?.Template}, {game.Brief?.Genre}).");
            sb.AppendLine($"Pitch: {game.Brief?.Pitch}\nCore loop: {game.Brief?.CoreLoop}");
            sb.AppendLine("Products: " + string.Join("; ", game.Products.Select(p => $"{p.Name} {p.PriceRobux} R$")));
            sb.AppendLine($"Store title: {game.Store.Title}");
            sb.AppendLine("Build took " + game.StageCycles.GetValueOrDefault(GameStage.Build) + " cycles; test failures " + game.StageFailures.GetValueOrDefault(GameStage.Test) + ".");
            sb.AppendLine("Maintenance so far: " + string.Join(" | ", game.Maintenance.Select(m => $"{m.Utc:MM-dd}: {Clip(m.Summary, 160)}")));
            sb.AppendLine("\nRoblox analytics:\n" + AnalyticsSync.Describe(game, 35));
            sb.AppendLine("\nTelemetry since launch:\n" + TelemetryStore.Describe(ctx.Telemetry.Summarize(gameId, game.Publish.WentPublicUtc ?? ctx.Clock().AddDays(-day))));

            string session = $"omniblox-postmortem-{gameId}-{Guid.NewGuid():N}";
            ctx.Llm.StartSession(session, AgentPrompts.PostMortem);
            try
            {
                ctx.Llm.AppendUser(session, sb.ToString());
                var turn = await ctx.Llm.QueryAsync(session, Array.Empty<ToolDefinition>(), new LlmCallOptions { MaxTokens = 1500, Thinking = "medium", WorkClass = AIRouterWorkClass.OneShot }, ct);
                if (!turn.Success) throw new InvalidOperationException(turn.Error ?? "post-mortem call failed");
                int start = turn.Text.IndexOf('{'), end = turn.Text.LastIndexOf('}');
                if (start < 0 || end <= start) throw new FormatException("the post-mortem was not JSON");
                var j = JObject.Parse(turn.Text[start..(end + 1)]);
                var pm = new PostMortem { Utc = ctx.Clock(), DaysSinceLaunch = day, WorkedWell = Clip(Args.Str(j, "workedWell"), 1000), DidNotWork = Clip(Args.Str(j, "didNotWork"), 1000) };
                foreach (var lesson in (j["lessons"] as JArray ?? new JArray()).OfType<JObject>().Take(6))
                {
                    string text = Args.Str(lesson, "text");
                    if (text.Length < 8) continue;
                    var tags = Args.List(lesson, "tags");
                    if (game.Brief?.Template is { Length: > 0 } t && !tags.Contains(t)) tags.Add(t);
                    ctx.Playbook.Add(Args.Str(lesson, "topic", "genre"), text, $"{game.DisplayTitle}, day {day}", Args.Str(lesson, "outcome", "neutral"), Args.Num(lesson, "confidence") ?? 0.5, tags, gameId);
                    pm.Lessons.Add(text);
                }
                ctx.Update(gameId, g => g.PostMortems.Add(pm));
                ctx.Event(EventLevel.Info, "game.postmortem", $"Day {day} post-mortem for \"{game.DisplayTitle}\": {pm.Lessons.Count} lesson(s) added to the Playbook.", gameId);
                ctx.AddUsage(gameId, new CycleReport { Turns = 1, PromptTokens = turn.PromptTokens, CachedTokens = turn.CachedTokens, CompletionTokens = turn.CompletionTokens });
            }
            finally
            {
                ctx.Llm.EndSession(session);
            }
        }

        /// <summary>Cheap, no-model checks that pull a game's maintenance forward when it looks broken.</summary>
        public static void FlagUrgent(StudioContext ctx, string gameId)
        {
            var game = ctx.Game(gameId);
            if (game == null || game.Stage != GameStage.Live || game.UrgentMaintenance) return;
            var day = ctx.Telemetry.Summarize(gameId, ctx.Clock().AddDays(-1));
            long errors = day.TopErrors.Sum(e => e.Count);
            bool errorSpike = errors >= 50 && (day.Sessions == 0 || errors / (double)Math.Max(1, day.Sessions) > 0.5);
            var week = ctx.Telemetry.Summarize(gameId, ctx.Clock().AddDays(-7));
            bool wentSilent = week.Sessions >= 60 && day.Sessions == 0 && ctx.Clock() - (game.Publish.LastPublishedUtc ?? DateTime.MinValue) < TimeSpan.FromDays(3);
            if (!errorSpike && !wentSilent) return;
            ctx.Update(gameId, g => g.UrgentMaintenance = true);
            ctx.Event(EventLevel.Warning, "game.urgent", $"\"{game.DisplayTitle}\" needs attention: " + (errorSpike ? $"{errors} script errors in a day" : "players stopped arriving right after an update"), gameId);
        }

        private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
    }
}
