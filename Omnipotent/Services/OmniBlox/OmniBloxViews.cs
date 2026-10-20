using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Omnipotent.Services.OmniBlox.Assets;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Pipeline;

namespace Omnipotent.Services.OmniBlox
{
    /// <summary>What the running service adds to the overview: the orchestrator's status, the model gate and the last harvest.</summary>
    internal sealed record StudioStatus(string Status, string? CurrentGameId, bool ModelAvailable, string? ModelReason, string? LastHarvest, DateTime? LastHarvestUtc);

    /// <summary>
    /// The JSON bodies the /omniblox/* routes return, built from the studio's state. The routes and the KM
    /// site's e2e fixture export both use these, so the dashboard is always tested against the real shapes.
    /// Times come from the studio clock.
    /// </summary>
    internal static class OmniBloxViews
    {
        public static readonly JsonSerializerSettings Json = new()
        {
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            Converters = { new StringEnumConverter() },
            NullValueHandling = NullValueHandling.Include,
        };

        public static string Serialize(object body) => JsonConvert.SerializeObject(body, Json);

        /// <summary>Below this many labelled models the overview suggests switching the harvester on.</summary>
        private const long SmallLibrary = 200;

        public static object GameSummary(GameDossier g)
        {
            var checklist = StageGates.Checklist(g);
            int budget = StageGates.CycleBudget(g.Stage);
            return new
            {
                g.GameId,
                Title = g.DisplayTitle,
                g.Stage,
                g.Paused,
                g.WaitingReason,
                g.NextAttemptUtc,
                Template = g.Brief?.Template,
                Genre = g.Brief?.Genre,
                Pitch = g.Brief?.Pitch,
                g.CreatedUtc,
                g.UpdatedUtc,
                g.StageEnteredUtc,
                g.Publish.WentPublicUtc,
                g.Publish.IsPublic,
                PlaceId = g.Slot?.PlaceId,
                UniverseId = g.Slot?.UniverseId,
                BuildRevision = g.Build.Revision,
                LastBuildOk = g.Build.LastBuildOk,
                TestPassed = g.LastTest?.Passed,
                Cycles = g.StageCycles,
                StageCycles = g.StageCycles.GetValueOrDefault(g.Stage),
                StageCycleBudget = budget == int.MaxValue ? (int?)null : budget,
                ChecklistMet = checklist.Count(c => c.Met),
                ChecklistTotal = checklist.Count,
                MilestonesDone = g.Milestones.Count(m => m.Done),
                MilestonesTotal = g.Milestones.Count,
                g.Usage,
                HasIcon = g.Store.IconFile != null,
                Revenue14d = g.Stats.Daily.TakeLast(14).Sum(d => d.RevenueRobux ?? 0),
                Dau7d = g.Stats.Daily.TakeLast(7).Select(d => d.DailyActiveUsers ?? 0).DefaultIfEmpty().Average(),
                DauSpark = g.Stats.Daily.TakeLast(14).Select(d => d.DailyActiveUsers ?? 0).ToList(),
                g.NextMaintenanceUtc,
                g.UrgentMaintenance,
                g.Escalations,
                g.AbandonReason,
                g.SunsetReason,
                LastActivity = g.Cycles.LastOrDefault()?.Done,
                LastActivityUtc = g.Cycles.LastOrDefault()?.Utc,
            };
        }

        public static object Overview(StudioContext ctx, StudioStatus status)
        {
            var now = ctx.Clock();
            var (games, tasks, events, studio, lessons) = ctx.Store.Read(s => (
                s.Games.Values.Select(OmniBloxStore.Clone).ToList(),
                s.Studio.OwnerTasks.Where(t => t.Status == OwnerTaskStatus.Open).Select(OmniBloxStore.Clone).ToList(),
                s.Events.TakeLast(40).Reverse().Select(OmniBloxStore.Clone).ToList(),
                OmniBloxStore.Clone(s.Studio),
                s.Playbook.Count));
            string today = now.ToString("yyyy-MM-dd");
            string weekAgo = now.AddDays(-7).ToString("yyyy-MM-dd");
            var week = studio.DailyUsage.Where(kv => string.CompareOrdinal(kv.Key, weekAgo) > 0).Select(kv => kv.Value).ToList();
            double revenue7 = games.Sum(g => g.Stats.Daily.Where(d => d.Date >= now.Date.AddDays(-7)).Sum(d => d.RevenueRobux ?? 0));
            double revenue30 = games.Sum(g => g.Stats.Daily.Where(d => d.Date >= now.Date.AddDays(-30)).Sum(d => d.RevenueRobux ?? 0));
            var library = ctx.Library.Stats();
            int slotsFree = ctx.Settings.Slots.Count(sl => !studio.SlotAssignments.TryGetValue(sl.ToString(), out var holder)
                || games.FirstOrDefault(g => g.GameId == holder)?.Stage is null or GameStage.Abandoned);

            // Studio-wide daily numbers across every game (Roblox analytics, so live games only).
            var daily = games.SelectMany(g => g.Stats.Daily)
                .Where(d => d.Date >= now.Date.AddDays(-30))
                .GroupBy(d => d.Date.Date)
                .OrderBy(grp => grp.Key)
                .Select(grp => new { Day = grp.Key.ToString("yyyy-MM-dd"), Robux = grp.Sum(d => d.RevenueRobux ?? 0), Players = grp.Sum(d => d.DailyActiveUsers ?? 0), Visits = grp.Sum(d => d.Visits ?? 0) })
                .ToList();

            return new
            {
                NowUtc = now,
                Enabled = ctx.Settings.Enabled,
                HarvesterEnabled = ctx.Settings.HarvesterEnabled,
                status.Status,
                status.CurrentGameId,
                Model = new { Available = status.ModelAvailable, Reason = status.ModelReason },
                ApiKey = ctx.Cloud.HasKey,
                CreatorSet = ctx.Creator.IsSet,
                Slots = new { Total = ctx.Settings.Slots.Count, Free = slotsFree },
                ctx.Settings.RequireLaunchApproval,
                ctx.Settings.MaxGamesInProduction,
                Attention = Attention(ctx, status, tasks, library, slotsFree),
                StageCounts = Enum.GetValues<GameStage>().ToDictionary(st => st.ToString(), st => games.Count(g => g.Stage == st)),
                Games = games.OrderByDescending(g => g.CreatedUtc).Select(GameSummary).ToList(),
                OwnerTasks = tasks.OrderByDescending(t => t.CreatedUtc).ToList(),
                Library = library,
                Harvester = new
                {
                    studio.Harvester.Discovered,
                    studio.Harvester.Downloaded,
                    studio.Harvester.Labeled,
                    studio.Harvester.LastRunUtc,
                    studio.Harvester.LastError,
                    studio.Harvester.PriorityQueries,
                    Last = status.LastHarvest,
                    status.LastHarvestUtc,
                },
                Usage = new
                {
                    Today = studio.DailyUsage.GetValueOrDefault(today) ?? new UsageTotals(),
                    Week = new UsageTotals
                    {
                        Calls = week.Sum(u => u.Calls),
                        PromptTokens = week.Sum(u => u.PromptTokens),
                        CachedTokens = week.Sum(u => u.CachedTokens),
                        CompletionTokens = week.Sum(u => u.CompletionTokens),
                    },
                    Labeler = studio.LabelerUsage,
                    Daily = studio.DailyUsage.OrderBy(kv => kv.Key).TakeLast(30).Select(kv => new { Day = kv.Key, kv.Value.Calls, kv.Value.PromptTokens, kv.Value.CachedTokens, kv.Value.CompletionTokens }).ToList(),
                },
                Money = new
                {
                    Spent = 0,
                    Revenue7dRobux = revenue7,
                    Revenue30dRobux = revenue30,
                    DevExRate = ctx.Settings.DevExRate,
                    Cash30dEstimateUsd = Math.Round(revenue30 * ctx.Settings.DevExRate, 2),
                },
                Daily = daily,
                studio.GamesLaunched,
                PlaybookLessons = lessons,
                Events = events,
            };
        }

        /// <summary>Why the studio cannot do its work right now, most serious first. Needs the owner files as tasks are not repeated here.</summary>
        private static List<object> Attention(StudioContext ctx, StudioStatus status, List<OwnerTask> openTasks, LibraryStats library, int slotsFree)
        {
            var items = new List<(int Rank, object Item)>();
            void Add(string level, string title, string detail, string? action) =>
                items.Add((level == "error" ? 0 : level == "warning" ? 1 : 2, new { Level = level, Title = title, Detail = detail, Action = action }));

            if (!ctx.Settings.Enabled)
                Add("info", "The studio is switched off", "Nothing is researched, built or maintained until it is on. The asset harvester has its own switch.", "studio");
            if (!status.ModelAvailable)
                Add(ctx.Settings.Enabled ? "error" : "warning", "No model work right now", status.ModelReason ?? "KliveLLM is not available.", null);
            if (!ctx.Cloud.HasKey && !openTasks.Any(t => t.Kind == OwnerTaskKind.ApiKey))
                Add("warning", "No Roblox API key", "Games are researched and built offline, but publishing, engine tests, model downloads and analytics wait for the OmniSetting OmniBlox_RobloxApiKey.", null);
            if (ctx.Settings.Slots.Count == 0 && !openTasks.Any(t => t.Kind == OwnerTaskKind.ExperienceSlot))
                Add("warning", "No experience slots", "Roblox has no API for creating an experience. Create a few blank ones and add them as universeId:placeId to OmniBlox_ExperienceSlots.", null);
            else if (ctx.Settings.Slots.Count > 0 && slotsFree == 0 && !openTasks.Any(t => t.Kind == OwnerTaskKind.ExperienceSlot))
                Add("info", "Every experience slot is taken", "The next game to reach Launch will ask for a new blank experience.", null);
            long labeled = library.ByStatus.GetValueOrDefault(AssetStatus.Labeled);
            if (!ctx.Settings.HarvesterEnabled && labeled < SmallLibrary)
                Add("info", "The asset harvester is off", $"The Asset Library has {labeled} labelled model(s). Builders place far better worlds with a few hundred to choose from.", "harvester");
            return items.OrderBy(i => i.Rank).Select(i => i.Item).ToList();
        }

        public static object Games(StudioContext ctx)
        {
            var games = ctx.Store.Read(s => s.Games.Values.OrderByDescending(g => g.CreatedUtc).Select(OmniBloxStore.Clone).ToList());
            return new { Games = games.Select(GameSummary).ToList() };
        }

        /// <summary>The whole dossier (minus its telemetry secret), its checklist, owner tasks and saved revisions; null when there is no such game.</summary>
        public static object? GameDetail(StudioContext ctx, string gameId)
        {
            var game = ctx.Game(gameId);
            if (game == null) return null;
            game.TelemetrySecret = "";
            var tasks = ctx.Store.Read(s => s.Studio.OwnerTasks.Where(t => t.GameId == game.GameId).OrderByDescending(t => t.CreatedUtc).Select(OmniBloxStore.Clone).ToList());
            var workspace = ctx.Workspace(game.GameId);
            return new
            {
                Game = game,
                Summary = GameSummary(game),
                Goal = StageGates.Goal(game.Stage),
                Checklist = StageGates.Checklist(game),
                OwnerTasks = tasks,
                Revisions = workspace.Exists ? workspace.Revisions() : (IReadOnlyList<int>)Array.Empty<int>(),
                Media = MediaFiles(ctx, game.GameId),
            };
        }

        public static readonly string[] MediaNames = { "icon.png", "thumb1.png", "thumb2.png", "thumb3.png", "trailer.mp4" };

        /// <summary>Which of the marketing files exist on disk.</summary>
        public static List<string> MediaFiles(StudioContext ctx, string gameId)
        {
            string dir = ctx.Store.MediaDirectory(gameId);
            return MediaNames.Where(n => File.Exists(Path.Combine(dir, n))).ToList();
        }

        public static object Files(StudioContext ctx, string gameId)
        {
            var workspace = ctx.Workspace(gameId);
            return new { Files = workspace.Exists ? workspace.ListFiles() : (IReadOnlyList<Build.WorkspaceFile>)Array.Empty<Build.WorkspaceFile>() };
        }

        public static object Telemetry(StudioContext ctx, string gameId, int days)
        {
            var summary = ctx.Telemetry.Summarize(gameId, ctx.Clock().AddDays(-days));
            return new
            {
                Days = days,
                summary.Events, summary.Sessions, summary.AvgSessionSeconds, summary.MedianSessionSeconds, summary.DeathsPerSession,
                summary.Purchases, summary.PurchaseRobux, summary.PurchasesByKey, summary.FeedbackByRating, summary.FeedbackByReason,
                TopErrors = summary.TopErrors.Select(e => new { e.Message, e.Count }).ToList(), summary.Funnel, summary.SessionsByDay, summary.PeakPlayersPerServer, summary.LastEventUtc,
            };
        }

        public static object Assets(StudioContext ctx, string text, string? status, int limit, int offset, bool placeableOnly)
        {
            var records = text.Length > 0
                ? ctx.Library.Search(new AssetSearchQuery { Text = text, PlaceableOnly = placeableOnly, Limit = Math.Min(50, limit) })
                : ctx.Library.Recent(status, limit, offset);
            return new { Assets = records.Select(AssetDto).ToList(), Stats = ctx.Library.Stats() };
        }

        public static object AssetDto(AssetRecord r) => new
        {
            r.AssetId, r.Name, r.CreatorName, r.CreatorVerified, r.Status, r.StatusReason, r.Placeable, r.UpVotes, r.UpVotePercent,
            r.HasScripts, r.ScriptsRemoved, r.ScriptFlags, r.Triangles, r.HasThumbnail, r.Renders, r.TimesUsed, r.DiscoveredQuery,
            Size = r.Measurements == null ? null : new[] { r.Measurements.SizeX, r.Measurements.SizeY, r.Measurements.SizeZ },
            Colors = r.Measurements?.Colors, Materials = r.Measurements?.Materials, Proportions = r.Measurements?.Proportions, PerformanceCost = r.Measurements?.PerformanceCost,
            Label = r.Label, Summary = r.Summary(), r.DiscoveredUtc, r.AnalyzedUtc, r.LabeledUtc,
        };

        /// <summary>One asset with its measurements and the notes builders left about it; null when unknown.</summary>
        public static object? AssetDetail(StudioContext ctx, long assetId)
        {
            var rec = ctx.Library.Get(assetId);
            return rec == null ? null : new { Asset = AssetDto(rec), rec.Description, rec.Measurements, rec.UsageNotes };
        }

        public static object Events(StudioContext ctx, int limit, string gameId) =>
            new { Events = ctx.Store.Read(s => s.Events.Where(e => gameId.Length == 0 || e.GameId == gameId).TakeLast(limit).Reverse().Select(OmniBloxStore.Clone).ToList()) };

        public static object Playbook(StudioContext ctx) =>
            new { Lessons = ctx.Store.Read(s => s.Playbook.OrderByDescending(l => l.Confidence).ThenByDescending(l => l.Utc).Select(OmniBloxStore.Clone).ToList()) };

        public static object Tasks(StudioContext ctx) =>
            new { Tasks = ctx.Store.Read(s => s.Studio.OwnerTasks.OrderByDescending(t => t.CreatedUtc).Take(200).Select(OmniBloxStore.Clone).ToList()) };
    }
}
