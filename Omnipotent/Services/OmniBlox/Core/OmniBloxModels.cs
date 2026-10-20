using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Omnipotent.Services.OmniBlox.Core
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum GameStage { Research, Build, Test, Launch, Live, Sunset, Abandoned }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum EventLevel { Info, Success, Warning, Error }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum OwnerTaskKind { ApiKey, Creator, ExperienceSlot, LaunchApproval, UploadIcon, Questionnaire, Escalation, Other }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum OwnerTaskStatus { Open, Done, Dismissed }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum ProductKind { GamePass, DeveloperProduct }

    /// <summary>
    /// Everything OmniBlox knows about one game, and the "clear goal" every agent cycle starts from.
    /// Each loop reads it and writes its results back; nothing important lives only in a context window.
    /// </summary>
    public sealed class GameDossier
    {
        public string GameId { get; set; } = "";
        public string WorkingTitle { get; set; } = "";
        public GameStage Stage { get; set; } = GameStage.Research;
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public DateTime StageEnteredUtc { get; set; }

        /// <summary>Why the game is not moving (e.g. waiting on an owner task). Null when it can run.</summary>
        public string? WaitingReason { get; set; }
        public string? WaitingOnTaskId { get; set; }
        public bool Paused { get; set; }

        // Research
        public List<GameConcept> Concepts { get; set; } = new();
        public GameBrief? Brief { get; set; }
        public ResearchNotes Research { get; set; } = new();

        // Build
        public List<Milestone> Milestones { get; set; } = new();
        public BuildState Build { get; set; } = new();

        // Test
        public TestReport? LastTest { get; set; }

        // Launch
        public ExperienceSlot? Slot { get; set; }
        public StorePage Store { get; set; } = new();
        public List<ProductRecord> Products { get; set; } = new();
        public PublishRecord Publish { get; set; } = new();

        // Live
        public LiveStats Stats { get; set; } = new();
        public List<MaintenanceRecord> Maintenance { get; set; } = new();
        public DateTime? NextMaintenanceUtc { get; set; }
        public List<PostMortem> PostMortems { get; set; } = new();
        public string TelemetrySecret { get; set; } = "";
        /// <summary>A live game's maintainer asked to ship its changes (release notes); the orchestrator tests and publishes them.</summary>
        public string? PendingUpdateNotes { get; set; }
        public string? SunsetReason { get; set; }
        public bool UrgentMaintenance { get; set; }

        // The brain
        public List<CycleReport> Cycles { get; set; } = new();
        public List<Decision> Decisions { get; set; } = new();
        public List<string> KeyFacts { get; set; } = new();
        public Dictionary<GameStage, int> StageFailures { get; set; } = new();
        public Dictionary<GameStage, int> StageCycles { get; set; } = new();
        public UsageTotals Usage { get; set; } = new();
        /// <summary>The orchestrator leaves the game alone until then (after an error, or while waiting on the owner).</summary>
        public DateTime? NextAttemptUtc { get; set; }
        /// <summary>How many times the watchdog has had to step in on this game.</summary>
        public int Escalations { get; set; }
        public string? AbandonReason { get; set; }

        [JsonIgnore]
        public string DisplayTitle => !string.IsNullOrWhiteSpace(Store.Title) ? Store.Title
            : !string.IsNullOrWhiteSpace(Brief?.Title) ? Brief!.Title
            : !string.IsNullOrWhiteSpace(WorkingTitle) ? WorkingTitle : GameId;

        [JsonIgnore]
        public bool InProduction => Stage is GameStage.Research or GameStage.Build or GameStage.Test or GameStage.Launch;

        public const int MaxCycleReports = 60;
        public const int MaxKeyFacts = 40;
        public const int MaxDecisions = 80;
    }

    public sealed class ResearchNotes
    {
        public DateTime? LastScanUtc { get; set; }
        public List<string> Findings { get; set; } = new();
        public List<long> ReferenceUniverses { get; set; } = new();
    }

    public sealed class GameConcept
    {
        public string ConceptId { get; set; } = "";
        public string Pitch { get; set; } = "";
        public string Genre { get; set; } = "";
        public string Template { get; set; } = "";
        public string CoreLoop { get; set; } = "";
        public string Twist { get; set; } = "";
        public List<string> Evidence { get; set; } = new();
        public ConceptScores Scores { get; set; } = new();
        public bool Chosen { get; set; }
    }

    /// <summary>Each 0–10. Feasibility weighs most: an idea the bot cannot build well is worth nothing.</summary>
    public sealed class ConceptScores
    {
        public double Demand { get; set; }
        public double Competition { get; set; }
        public double Monetization { get; set; }
        public double Feasibility { get; set; }
        public double Lessons { get; set; }

        /// <summary>Weighted total (serialized for the dashboard; read-only, so it is never loaded back).</summary>
        public double Total => Math.Round(Demand * 0.25 + Competition * 0.15 + Monetization * 0.15 + Feasibility * 0.35 + Lessons * 0.10, 2);
    }

    /// <summary>The research loop's hand-off to Build: what to make and what "done" means.</summary>
    public sealed class GameBrief
    {
        public string Title { get; set; } = "";
        public string Genre { get; set; } = "";
        /// <summary>The Game Kit template this game starts from (obby, tycoon, simulator, …).</summary>
        public string Template { get; set; } = "";
        public string Pitch { get; set; } = "";
        public string TargetPlayers { get; set; } = "";
        public string CoreLoop { get; set; } = "";
        public List<string> MustHave { get; set; } = new();
        public List<string> NiceToHave { get; set; } = new();
        public string ArtStyle { get; set; } = "";
        public List<string> AssetNeeds { get; set; } = new();
        public List<ProductIdea> Monetization { get; set; } = new();
        public MarketingAngle Marketing { get; set; } = new();
        public List<string> DoneWhen { get; set; } = new();
        public List<string> Evidence { get; set; } = new();
    }

    public sealed class ProductIdea
    {
        /// <summary>Stable id the game's code uses, e.g. "vip" or "coins_100".</summary>
        public string Key { get; set; } = "";
        public ProductKind Kind { get; set; }
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public int PriceRobux { get; set; }
        /// <summary>What the purchase grants, in the Game Kit's vocabulary (see GameKit "Shop" module).</summary>
        public string Grant { get; set; } = "";
    }

    public sealed class MarketingAngle
    {
        public List<string> TitleIdeas { get; set; } = new();
        public List<string> ThumbnailIdeas { get; set; } = new();
        public List<string> Keywords { get; set; } = new();
        public string Hook { get; set; } = "";
    }

    public sealed class Milestone
    {
        public string MilestoneId { get; set; } = "";
        public string Title { get; set; } = "";
        public bool Done { get; set; }
        public string? Notes { get; set; }
    }

    public sealed class BuildState
    {
        public int Revision { get; set; }
        public DateTime? LastBuildUtc { get; set; }
        public bool LastBuildOk { get; set; }
        public long PlaceBytes { get; set; }
        public int InstanceCount { get; set; }
        public int ScriptCount { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        /// <summary>The builder says the brief's must-haves are in (checked again by Test).</summary>
        public bool FeatureComplete { get; set; }
        /// <summary>Library models this game has placed (each counted once in the library's usage stats).</summary>
        public List<long> AssetsUsed { get; set; } = new();
    }

    public sealed class TestReport
    {
        public DateTime Utc { get; set; }
        public int BuildRevision { get; set; }
        public bool Passed { get; set; }
        public bool StaticOk { get; set; }
        public bool GeometryOk { get; set; }
        public bool EngineRan { get; set; }
        public bool EngineOk { get; set; }
        public long? EngineVersion { get; set; }
        public List<TestIssue> Issues { get; set; } = new();
        public string Summary { get; set; } = "";
    }

    public sealed class TestIssue
    {
        public string Severity { get; set; } = "error";
        public string Area { get; set; } = "";
        public string Message { get; set; } = "";
    }

    public sealed class ExperienceSlot
    {
        public long UniverseId { get; set; }
        public long PlaceId { get; set; }

        public override string ToString() => $"{UniverseId}:{PlaceId}";

        public static ExperienceSlot? Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var parts = text.Trim().Split(':', '/', ',');
            if (parts.Length != 2 || !long.TryParse(parts[0].Trim(), out long u) || !long.TryParse(parts[1].Trim(), out long p) || u <= 0 || p <= 0)
                return null;
            return new ExperienceSlot { UniverseId = u, PlaceId = p };
        }
    }

    public sealed class StorePage
    {
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string Genre { get; set; } = "";
        public List<string> Keywords { get; set; } = new();
        public string? IconFile { get; set; }
        public List<string> ThumbnailFiles { get; set; } = new();
        public string? TrailerFile { get; set; }
        public bool Applied { get; set; }
        public bool? ComplianceOk { get; set; }
        public List<string> ComplianceNotes { get; set; } = new();
    }

    public sealed class ProductRecord
    {
        public string Key { get; set; } = "";
        public ProductKind Kind { get; set; }
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public int PriceRobux { get; set; }
        public string Grant { get; set; } = "";
        public long? RobloxId { get; set; }
        public DateTime? SyncedUtc { get; set; }
    }

    public sealed class PublishRecord
    {
        public long? LastSavedVersion { get; set; }
        public int? LastSavedRevision { get; set; }
        public long? LastPublishedVersion { get; set; }
        public int? LastPublishedRevision { get; set; }
        public DateTime? FirstPublishedUtc { get; set; }
        public DateTime? LastPublishedUtc { get; set; }
        public bool IsPublic { get; set; }
        public DateTime? LaunchRequestedUtc { get; set; }
        public DateTime? LaunchApprovedUtc { get; set; }
        public DateTime? WentPublicUtc { get; set; }
        /// <summary>The engine-test result for the revision about to be published (launch and updates).</summary>
        public int? EngineTestedRevision { get; set; }
        public string? LastLaunchStep { get; set; }
        public string? LastLaunchError { get; set; }
    }

    public sealed class LiveStats
    {
        public DateTime? LastSyncUtc { get; set; }
        public string? LastSyncError { get; set; }
        public List<DailyMetrics> Daily { get; set; } = new();
        public TelemetryTotals Telemetry { get; set; } = new();
    }

    public sealed class DailyMetrics
    {
        public DateTime Date { get; set; }
        public double? DailyActiveUsers { get; set; }
        public double? Visits { get; set; }
        public double? PlayHours { get; set; }
        public double? AvgSessionMinutes { get; set; }
        public double? RevenueRobux { get; set; }
        public double? PayingUsers { get; set; }
        public double? D1Retention { get; set; }
        public double? D7Retention { get; set; }
        public double? PeakConcurrent { get; set; }
        public double? ClickThrough { get; set; }
    }

    public sealed class TelemetryTotals
    {
        public long Events { get; set; }
        public long Sessions { get; set; }
        public long Errors { get; set; }
        public long Purchases { get; set; }
        public long Feedback { get; set; }
        public DateTime? LastEventUtc { get; set; }
    }

    public sealed class MaintenanceRecord
    {
        public DateTime Utc { get; set; }
        public string Summary { get; set; } = "";
        public List<string> Actions { get; set; } = new();
        public bool ShippedUpdate { get; set; }
        public bool Urgent { get; set; }
    }

    public sealed class PostMortem
    {
        public DateTime Utc { get; set; }
        public int DaysSinceLaunch { get; set; }
        public string WorkedWell { get; set; } = "";
        public string DidNotWork { get; set; } = "";
        public List<string> Lessons { get; set; } = new();
    }

    /// <summary>One agent work cycle's structured hand-over — the compaction unit.</summary>
    public sealed class CycleReport
    {
        public DateTime Utc { get; set; }
        public GameStage Stage { get; set; }
        public string Role { get; set; } = "";
        public string Outcome { get; set; } = "";
        public string Done { get; set; } = "";
        public List<string> Decisions { get; set; } = new();
        public List<string> Problems { get; set; } = new();
        public List<string> NextSteps { get; set; } = new();
        public int Turns { get; set; }
        public long PromptTokens { get; set; }
        public long CachedTokens { get; set; }
        public long CompletionTokens { get; set; }
    }

    public sealed class Decision
    {
        public DateTime Utc { get; set; }
        public GameStage Stage { get; set; }
        public string What { get; set; } = "";
        public string Why { get; set; } = "";
    }

    public sealed class UsageTotals
    {
        public long Calls { get; set; }
        public long PromptTokens { get; set; }
        public long CachedTokens { get; set; }
        public long CompletionTokens { get; set; }

        public void Add(long calls, long prompt, long cached, long completion)
        {
            Calls += calls;
            PromptTokens += prompt;
            CachedTokens += cached;
            CompletionTokens += completion;
        }

        [JsonIgnore]
        public double CacheHitRate => PromptTokens > 0 ? (double)CachedTokens / PromptTokens : 0;
    }

    /// <summary>Something only the owner can do (money, accounts, keys, approvals, things with no API).</summary>
    public sealed class OwnerTask
    {
        public string TaskId { get; set; } = "";
        public OwnerTaskKind Kind { get; set; }
        public string? GameId { get; set; }
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        public OwnerTaskStatus Status { get; set; } = OwnerTaskStatus.Open;
        public DateTime CreatedUtc { get; set; }
        public DateTime? ResolvedUtc { get; set; }
        public List<string> Files { get; set; } = new();
        /// <summary>Stable key so the same need is never filed twice (e.g. "icon:g_123").</summary>
        public string DedupeKey { get; set; } = "";
    }

    /// <summary>A lesson the studio learned, read before every research, build and launch decision.</summary>
    public sealed class PlaybookLesson
    {
        public string LessonId { get; set; } = "";
        public DateTime Utc { get; set; }
        public string? GameId { get; set; }
        /// <summary>genre, mechanic, pricing, marketing, build, test, maintenance.</summary>
        public string Topic { get; set; } = "";
        public string Text { get; set; } = "";
        public string Evidence { get; set; } = "";
        /// <summary>worked, failed or neutral.</summary>
        public string Outcome { get; set; } = "neutral";
        public double Confidence { get; set; } = 0.5;
        public List<string> Tags { get; set; } = new();
        public int TimesUsed { get; set; }
    }

    public sealed class OmniBloxEvent
    {
        public DateTime Utc { get; set; }
        public EventLevel Level { get; set; }
        public string Kind { get; set; } = "";
        public string Message { get; set; } = "";
        public string? GameId { get; set; }
    }

    /// <summary>Studio-wide state that is not per game.</summary>
    public sealed class StudioState
    {
        public List<OwnerTask> OwnerTasks { get; set; } = new();
        /// <summary>Experience slot ("u:p") → game id it holds.</summary>
        public Dictionary<string, string> SlotAssignments { get; set; } = new();
        public Dictionary<string, UsageTotals> DailyUsage { get; set; } = new();
        public UsageTotals LabelerUsage { get; set; } = new();
        public HarvesterState Harvester { get; set; } = new();
        public DateTime? LastPortfolioReviewUtc { get; set; }
        public int GamesLaunched { get; set; }
    }

    public sealed class HarvesterState
    {
        public Dictionary<string, string> QueryCursors { get; set; } = new();
        public Dictionary<string, DateTime> QueryLastRunUtc { get; set; } = new();
        public List<string> PriorityQueries { get; set; } = new();
        public long Discovered { get; set; }
        public long Downloaded { get; set; }
        public long Labeled { get; set; }
        public DateTime? LastRunUtc { get; set; }
        public string? LastError { get; set; }
    }
}
