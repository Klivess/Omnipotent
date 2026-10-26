using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Omnipotent.Services.OmniBlox.Core
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum GameStage { Research, Build, Test, Launch, Live, Sunset, Abandoned }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum EventLevel { Info, Success, Warning, Error }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum OwnerTaskKind { ApiKey, Creator, ExperienceSlot, LaunchApproval, UploadIcon, Questionnaire, Escalation, Other, BuildSignoff, RobloxSession }

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

        /// <summary>The owner playing the game at any stage: builds sent to Roblox and what those sessions reported.</summary>
        public PlaytestState Playtest { get; set; } = new();

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
        /// <summary>
        /// Facts kept word for word in every briefing. A fact with a key replaces the older fact with the same key,
        /// so a cycle updates "economy" or "file layout" instead of piling up near-copies. Old dossiers stored plain
        /// strings here; <see cref="KeyFactListConverter"/> reads both.
        /// </summary>
        [JsonConverter(typeof(KeyFactListConverter))]
        public List<KeyFact> KeyFacts { get; set; } = new();
        /// <summary>What the owner asked for (steering). Open ones lead every briefing until an agent reports them done.</summary>
        public List<OwnerDirective> Directives { get; set; } = new();
        public Dictionary<GameStage, int> StageFailures { get; set; } = new();
        public Dictionary<GameStage, int> StageCycles { get; set; } = new();
        public UsageTotals Usage { get; set; } = new();
        /// <summary>The orchestrator leaves the game alone until then (after an error, or while waiting on the owner).</summary>
        public DateTime? NextAttemptUtc { get; set; }
        /// <summary>How many times the watchdog has had to step in on this game.</summary>
        public int Escalations { get; set; }
        public string? AbandonReason { get; set; }
        /// <summary>The stage a game was in when it was abandoned, so the owner can revive it there.</summary>
        public GameStage? AbandonedFromStage { get; set; }
        /// <summary>Whether the game is moving: what the watchdog measures instead of counting cycles.</summary>
        public ProgressState Progress { get; set; } = new();
        /// <summary>Model or provider failures in a row (AIRouter down, queue full). They back off; they never count against the game.</summary>
        public int ProviderFailures { get; set; }

        [JsonIgnore]
        public string DisplayTitle => !string.IsNullOrWhiteSpace(Store.Title) ? Store.Title
            : !string.IsNullOrWhiteSpace(Brief?.Title) ? Brief!.Title
            : !string.IsNullOrWhiteSpace(WorkingTitle) ? WorkingTitle : GameId;

        [JsonIgnore]
        public bool InProduction => Stage is GameStage.Research or GameStage.Build or GameStage.Test or GameStage.Launch;

        public const int MaxCycleReports = 80;
        public const int MaxKeyFacts = 40;
        public const int MaxKeyFactLength = 600;
        public const int MaxDecisions = 80;
        public const int MaxDirectives = 40;
    }

    /// <summary>One fact a cycle asked to keep. <see cref="Key"/> is a short topic ("economy", "plot layout"); empty for an unkeyed fact.</summary>
    public sealed class KeyFact
    {
        public string Key { get; set; } = "";
        public string Text { get; set; } = "";
        public DateTime Utc { get; set; }
        public GameStage? Stage { get; set; }
        /// <summary>Written by the owner (steering) or the studio itself (watchdog): never pushed out by an agent's facts.</summary>
        public string Source { get; set; } = "agent";

        public override string ToString() => Key.Length > 0 ? $"{Key}: {Text}" : Text;
    }

    /// <summary>Reads KeyFacts written as plain strings (old dossiers) or as objects, and always writes objects.</summary>
    public sealed class KeyFactListConverter : JsonConverter<List<KeyFact>>
    {
        public override List<KeyFact> ReadJson(JsonReader reader, Type objectType, List<KeyFact>? existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            var list = new List<KeyFact>();
            if (reader.TokenType == JsonToken.Null) return list;
            var array = Newtonsoft.Json.Linq.JArray.Load(reader);
            foreach (var item in array)
            {
                if (item.Type == Newtonsoft.Json.Linq.JTokenType.String)
                {
                    string text = ((string?)item ?? "").Trim();
                    if (text.Length == 0) continue;
                    // The old steering route wrote the owner's words as "Owner's direction (2026-10-21): …": keep them as the owner's.
                    var owner = System.Text.RegularExpressions.Regex.Match(text, @"^Owner's direction \((?<day>\d{4}-\d\d-\d\d)\):\s*(?<text>.+)$", System.Text.RegularExpressions.RegexOptions.Singleline);
                    if (owner.Success)
                    {
                        DateTime.TryParse(owner.Groups["day"].Value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var day);
                        list.Add(new KeyFact { Key = "owner " + owner.Groups["day"].Value, Text = FactBook.Normalize(owner.Groups["text"].Value), Utc = day, Source = FactBook.Owner });
                    }
                    else list.Add(FactBook.Parse(text, default, null));
                }
                else if (item is Newtonsoft.Json.Linq.JObject obj)
                {
                    var fact = obj.ToObject<KeyFact>(serializer);
                    if (fact != null && fact.Text.Trim().Length > 0) list.Add(fact);
                }
            }
            return list;
        }

        public override void WriteJson(JsonWriter writer, List<KeyFact>? value, JsonSerializer serializer)
        {
            writer.WriteStartArray();
            foreach (var fact in value ?? new List<KeyFact>())
            {
                writer.WriteStartObject();
                writer.WritePropertyName("Key"); writer.WriteValue(fact.Key);
                writer.WritePropertyName("Text"); writer.WriteValue(fact.Text);
                writer.WritePropertyName("Utc"); writer.WriteValue(fact.Utc);
                writer.WritePropertyName("Stage"); if (fact.Stage is GameStage s) writer.WriteValue(s.ToString()); else writer.WriteNull();
                writer.WritePropertyName("Source"); writer.WriteValue(fact.Source);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum DirectiveStatus { Open, Done, Dropped }

    /// <summary>A direction from the owner. Shown at the top of every briefing while open; also delivered into a running cycle.</summary>
    public sealed class OwnerDirective
    {
        public string DirectiveId { get; set; } = "";
        public DateTime Utc { get; set; }
        public string Text { get; set; } = "";
        public DirectiveStatus Status { get; set; } = DirectiveStatus.Open;
        /// <summary>What the agent said it did about it.</summary>
        public string? Response { get; set; }
        public DateTime? ResolvedUtc { get; set; }
        /// <summary>Already shown to a running cycle (so it is not injected twice).</summary>
        public bool Delivered { get; set; }
    }

    /// <summary>
    /// Progress, as the watchdog measures it. A stage never runs out of cycles: what stops a game is a stall,
    /// the same failure again and again with nothing changing, and even then the studio changes approach and
    /// finally asks the owner rather than giving up on its own.
    /// </summary>
    public sealed class ProgressState
    {
        /// <summary>Cycles (or test runs) in a row that changed nothing that matters.</summary>
        public int StalledCycles { get; set; }
        /// <summary>The signature of the last failure; the same signature again counts as a stall.</summary>
        public string? LastFailureSignature { get; set; }
        /// <summary>The same failure signature in a row.</summary>
        public int SameFailureCount { get; set; }
        /// <summary>What progress looked like after the last cycle (clean revision, checklist met, milestones done…).</summary>
        public string? LastMark { get; set; }
        public DateTime? LastProgressUtc { get; set; }
        /// <summary>How far up the stall ladder the game is: 0 none, 1 rethink, 2 deep dive, 3 asked the owner.</summary>
        public int Level { get; set; }
        /// <summary>The watchdog's current instruction to the agents (shown in the briefing until progress resets it).</summary>
        public string? Directive { get; set; }
        /// <summary>Recent watchdog interventions, newest last.</summary>
        public List<string> History { get; set; } = new();
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
        /// <summary>Whether the last build was type-checked (and why not, when it was not).</summary>
        public bool TypeChecked { get; set; }
        public string? TypeCheckNote { get; set; }
        /// <summary>The map report's score (0-100) at the last build, and what it found.</summary>
        public int? MapScore { get; set; }
        public List<string> MapFindings { get; set; } = new();
        /// <summary>The economy check on the last build (a bot playing the game's own numbers), and its problems.</summary>
        public string? Economy { get; set; }
        public List<string> EconomyFindings { get; set; } = new();
        /// <summary>The newest engine test run (run_tests, the Test stage or a launch), with every case and runtime error.</summary>
        public EngineRun? LastEngineRun { get; set; }
        /// <summary>The Game Kit version the current revision was built on (a kit update makes the same files a new build).</summary>
        public string? KitFingerprint { get; set; }
    }

    /// <summary>One run of the game in Roblox's engine (see Pipeline/EngineTests), or in the studio's local engine (Pipeline/LocalEngine).</summary>
    public sealed class EngineRun
    {
        public DateTime Utc { get; set; }
        public int Revision { get; set; }
        public bool Ran { get; set; }
        public bool Ok { get; set; }
        /// <summary>
        /// It ran in the studio's local engine (Lune), a stand-in with no physics, replication or clients, because
        /// Roblox's could not run the game yet. Counts only while that is so; a launch always runs Roblox's.
        /// </summary>
        public bool Local { get; set; }
        public long? Version { get; set; }
        public int Passed { get; set; }
        public int Failed { get; set; }
        /// <summary>The kit's own checks (run before any game code) failed: a studio bug, not the game's.</summary>
        public bool KitFailed { get; set; }
        public string Summary { get; set; } = "";
        public List<EngineCase> Results { get; set; } = new();
        public List<EngineError> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }

    public sealed class EngineCase
    {
        public string Name { get; set; } = "";
        public bool Ok { get; set; }
        public string? Detail { get; set; }
        /// <summary>kit (control, before game code), load, boot, world, game.</summary>
        public string Phase { get; set; } = "";
        /// <summary>kit or game.</summary>
        public string Owner { get; set; } = "";
        public double Seconds { get; set; }
        public bool Skipped { get; set; }
        public List<string> Warnings { get; set; } = new();
    }

    public sealed class EngineError
    {
        public string Message { get; set; } = "";
        public string? Source { get; set; }
        public string? Trace { get; set; }
        public string Phase { get; set; } = "";
        /// <summary>The game's file and line the error points at, when it could be found.</summary>
        public string? File { get; set; }
        public int? Line { get; set; }
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
        /// <summary>The run told nothing about the game: Roblox or the model could not be reached. Retried later.</summary>
        public bool Infrastructure { get; set; }
        /// <summary>The engine run this test was decided on (also kept as Build.LastEngineRun).</summary>
        public EngineRun? Engine { get; set; }
        public int? MapScore { get; set; }
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

    public enum ExperiencePurpose { Game, Playtest }

    /// <summary>An experience the studio created itself, through the owner's Roblox session.</summary>
    public sealed class CreatedExperience
    {
        public long UniverseId { get; set; }
        public long PlaceId { get; set; }
        public ExperiencePurpose Purpose { get; set; }
        public DateTime CreatedUtc { get; set; }
        /// <summary>The game it was made for (another may hold it since).</summary>
        public string? MadeFor { get; set; }
        /// <summary>Whether the API key can change it: null until that is known.</summary>
        public bool? KeyReaches { get; set; }
        /// <summary>Made private and named; false while that is still owed.</summary>
        public bool Prepared { get; set; }

        public ExperienceSlot ToSlot() => new() { UniverseId = UniverseId, PlaceId = PlaceId };
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
        /// <summary>Taken off sale because the game was abandoned and its experience went to another game.</summary>
        public bool Retired { get; set; }
        /// <summary>When Roblox confirmed it is off sale (null on products marked before that was checked).</summary>
        public DateTime? RetiredUtc { get; set; }
    }

    /// <summary>The owner's decision at a launch gate.</summary>
    public sealed class ApprovalRecord
    {
        /// <summary>build (sign-off) or launch.</summary>
        public string Gate { get; set; } = "";
        /// <summary>approved or changes.</summary>
        public string Decision { get; set; } = "";
        public string Note { get; set; } = "";
        public int Revision { get; set; }
        public DateTime Utc { get; set; }
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
        /// <summary>The owner played this revision and signed it off for marketing (the build gate).</summary>
        public int? SignoffRevision { get; set; }
        public DateTime? SignoffUtc { get; set; }
        /// <summary>The launch approval is for this revision with this store page and these products (see Approvals.Fingerprint).</summary>
        public int? ApprovedRevision { get; set; }
        public string? ApprovedFingerprint { get; set; }
        /// <summary>Every decision the owner made at a gate, newest last.</summary>
        public List<ApprovalRecord> Approvals { get; set; } = new();
        /// <summary>Launch steps that failed in a row (the retry waits longer each time, and the owner is asked after a few).</summary>
        public int LaunchFailures { get; set; }
        public DateTime? WentPublicUtc { get; set; }
        /// <summary>The engine-test result for the revision about to be published (launch and updates).</summary>
        public int? EngineTestedRevision { get; set; }
        public string? LastLaunchStep { get; set; }
        public string? LastLaunchError { get; set; }
        /// <summary>The icon on Roblox now, by content hash (a new icon file is uploaded again).</summary>
        public string? IconUploadedHash { get; set; }
        public DateTime? IconUploadedUtc { get; set; }
        /// <summary>The homepage-thumbnail API refused the thumbnails at launch, so they go on the store page through the owner's session.</summary>
        public bool StoreThumbnailsPending { get; set; }
        /// <summary>How many of those are up (a retry carries on after them instead of adding them twice).</summary>
        public int StoreThumbnailsUploaded { get; set; }
        /// <summary>The newest answering of Roblox's maturity questionnaire, published or stopped for the owner.</summary>
        public QuestionnaireRecord? Questionnaire { get; set; }
    }

    /// <summary>What the studio answered in Roblox's Maturity &amp; Compliance questionnaire, and what rating came of it.</summary>
    public sealed class QuestionnaireRecord
    {
        public DateTime Utc { get; set; }
        /// <summary>The game's content facts the answers were drawn from, by hash: new facts mean answering again.</summary>
        public string FactsHash { get; set; } = "";
        public List<string> QuestionnaireIds { get; set; } = new();
        /// <summary>Published to Roblox: the experience carries the rating below.</summary>
        public bool Published { get; set; }
        /// <summary>Why it was not published, when the studio left the decision to the owner.</summary>
        public string? StoppedReason { get; set; }
        public string? AgeRating { get; set; }
        public int? MinimumAge { get; set; }
        /// <summary>"Question → answer (because …)", in the questionnaire's order.</summary>
        public List<string> Answers { get; set; } = new();
    }

    /// <summary>
    /// The owner's playtests of one game. A playtest build simulates purchases, saves apart from the real game
    /// and reports to <see cref="Sessions"/> instead of the game's own numbers, so nothing here touches players.
    /// </summary>
    public sealed class PlaytestState
    {
        public const int MaxSessions = 12;

        /// <summary>Authenticates telemetry from playtest builds (never the game's real telemetry secret).</summary>
        public string Secret { get; set; } = "";
        /// <summary>While the owner is in the playtest, each new clean build is published and the server moved onto it.</summary>
        public bool Follow { get; set; }
        /// <summary>Bumped by "start fresh": playtest saves live in a DataStore named after it.</summary>
        public int SaveSlot { get; set; } = 1;
        public PlaytestPublish? LastPublish { get; set; }
        /// <summary>One per playtest server (a Roblox server, or one run in Studio), newest last.</summary>
        public List<PlaytestSession> Sessions { get; set; } = new();
    }

    public sealed class PlaytestPublish
    {
        public DateTime Utc { get; set; }
        public long UniverseId { get; set; }
        public long PlaceId { get; set; }
        public long Version { get; set; }
        /// <summary>"r12" is the clean build r12; "r12+" is the files as they were, past r12; "wip" is before the first clean build.</summary>
        public string Build { get; set; } = "";
        public int Revision { get; set; }
        /// <summary>The game's own experience (private until launch) rather than the studio's playtest experience.</summary>
        public bool OwnExperience { get; set; }
        /// <summary>What went into the build (files, settings, products); the same inputs are not published twice.</summary>
        public string Fingerprint { get; set; } = "";
        /// <summary>Published by Follow after a clean build, not by the owner.</summary>
        public bool Followed { get; set; }
        /// <summary>Running servers were asked to move onto this version.</summary>
        public bool ServersRestarted { get; set; }
        public string? Note { get; set; }
    }

    public sealed class PlaytestSession
    {
        public string Server { get; set; } = "";
        /// <summary>roblox or studio.</summary>
        public string Where { get; set; } = "";
        public string Build { get; set; } = "";
        public DateTime StartedUtc { get; set; }
        public DateTime LastEventUtc { get; set; }
        /// <summary>Players on the server at its last report.</summary>
        public int Players { get; set; }
        public int Joins { get; set; }
        /// <summary>Seconds played, from the sessions that have ended.</summary>
        public long PlayedSeconds { get; set; }
        public long Events { get; set; }
        public int Deaths { get; set; }
        /// <summary>The furthest funnel step reached (an obby's stage) and its name.</summary>
        public int? FurthestStep { get; set; }
        public string? FurthestStepName { get; set; }
        public bool Completed { get; set; }
        public long Earned { get; set; }
        /// <summary>Gameplay events by name (unlocks, sales, upgrades, rewards…).</summary>
        public Dictionary<string, long> Actions { get; set; } = new();
        /// <summary>Product keys bought with simulated purchases.</summary>
        public List<string> TestPurchases { get; set; } = new();
        public List<PlaytestError> Errors { get; set; } = new();
        public List<PlaytestFeedback> Feedback { get; set; } = new();
    }

    public sealed class PlaytestError
    {
        public string Message { get; set; } = "";
        public string? Source { get; set; }
        public long Count { get; set; }
    }

    public sealed class PlaytestFeedback
    {
        public DateTime Utc { get; set; }
        /// <summary>1 Bad, 2 Okay, 3 Great.</summary>
        public int Rating { get; set; }
        public string Reason { get; set; } = "";
    }

    /// <summary>Which game the studio's playtest experience (OmniBlox_PlaytestSlot) is running.</summary>
    public sealed class PlaytestOccupant
    {
        public string GameId { get; set; } = "";
        public string Build { get; set; } = "";
        public long Version { get; set; }
        public DateTime Utc { get; set; }
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
        /// <summary>Names the cycle's full log (games/&lt;id&gt;/cycles/&lt;CycleId&gt;.jsonl): every call, result and picture.</summary>
        public string CycleId { get; set; } = "";
        public DateTime? StartedUtc { get; set; }
        public DateTime Utc { get; set; }
        public GameStage Stage { get; set; }
        public string Role { get; set; } = "";
        /// <summary>finished, handed over (context full; the next cycle continues), cut short, or error.</summary>
        public string Outcome { get; set; } = "";
        public string Done { get; set; } = "";
        public List<string> Decisions { get; set; } = new();
        public List<string> Problems { get; set; } = new();
        public List<string> NextSteps { get; set; } = new();
        public int Turns { get; set; }
        public int ToolCalls { get; set; }
        public int ToolErrors { get; set; }
        /// <summary>Replies that hit the output limit (their cut-off calls were not run, and the agent was told).</summary>
        public int Truncations { get; set; }
        public string? Error { get; set; }
        public long PromptTokens { get; set; }
        public long CachedTokens { get; set; }
        public long CompletionTokens { get; set; }
        /// <summary>See <see cref="UsageTotals.ResentTokens"/>.</summary>
        public long ResentTokens { get; set; }
        public long ResentCachedTokens { get; set; }
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
        /// <summary>
        /// Prompt tokens a continuation turn sent AGAIN: the previous request of the same append-only
        /// session, which is an exact prefix of this one. Unlike a turn's new tool output, these could
        /// always have come from the provider's cache.
        /// </summary>
        public long ResentTokens { get; set; }
        /// <summary>How many of <see cref="ResentTokens"/> the cache actually served. The gap is waste: a
        /// prefix that expired between turns or was rewritten.</summary>
        public long ResentCachedTokens { get; set; }

        public void Add(long calls, long prompt, long cached, long completion)
        {
            Calls += calls;
            PromptTokens += prompt;
            CachedTokens += cached;
            CompletionTokens += completion;
        }

        public void AddResent(long resent, long resentCached)
        {
            ResentTokens += resent;
            ResentCachedTokens += Math.Min(resent, resentCached);
        }

        public void Add(UsageTotals other)
        {
            Add(other.Calls, other.PromptTokens, other.CachedTokens, other.CompletionTokens);
            AddResent(other.ResentTokens, other.ResentCachedTokens);
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
        /// <summary>The experiences the studio created itself (the owner's OmniBlox_ExperienceSlots are not listed here).</summary>
        public List<CreatedExperience> CreatedExperiences { get; set; } = new();
        /// <summary>What the playtest experience runs now; null until the first playtest there.</summary>
        public PlaytestOccupant? PlaytestSlot { get; set; }
        public Dictionary<string, UsageTotals> DailyUsage { get; set; } = new();
        /// <summary>The asset labeller's share of <see cref="DailyUsage"/> (already included in it).</summary>
        public Dictionary<string, UsageTotals> DailyLabelerUsage { get; set; } = new();
        /// <summary>Everything else in <see cref="DailyUsage"/>: agent cycles and their one-shot reviews. Kept
        /// on its own rather than as the difference, which would count days before the split as agent work.</summary>
        public Dictionary<string, UsageTotals> DailyAgentUsage { get; set; } = new();
        public UsageTotals LabelerUsage { get; set; } = new();
        /// <summary>The labeller's frozen reference examples (null until the library has labels to pick from).</summary>
        public LabelReferenceSet? LabelerReference { get; set; }
        public HarvesterState Harvester { get; set; } = new();
        public DateTime? LastPortfolioReviewUtc { get; set; }
        public int GamesLaunched { get; set; }
        /// <summary>
        /// Sounds known to be licensed for any experience (Roblox's and APM's uploads, found by search_sounds), by id.
        /// A game may only use these or the kit's defaults: the first studio invented audio ids that played nothing.
        /// </summary>
        public Dictionary<long, string> LicensedAudio { get; set; } = new();
        /// <summary>Creator Dashboard chores still to finish (by owner-task key, e.g. "icon:g_123"): their retry state.</summary>
        public Dictionary<string, ChoreState> Chores { get; set; } = new();
    }

    /// <summary>A chore the studio keeps retrying: each failure waits longer, and the owner hears only when it stays stuck.</summary>
    public sealed class ChoreState
    {
        public int Failures { get; set; }
        public DateTime? FirstFailureUtc { get; set; }
        public DateTime? NextAttemptUtc { get; set; }
        public string? LastError { get; set; }
    }

    /// <summary>
    /// Earlier labels the labeller shows before every new model: anchors for its quality scale and
    /// vocabulary, and a prefix that is byte-identical across calls so the provider caches it once
    /// for all of them. Frozen when built (text, answer and image files) — any change to a byte
    /// would make every later call miss it.
    /// </summary>
    public sealed class LabelReferenceSet
    {
        public int Version { get; set; }
        /// <summary>The labeller prompt version it was built for; a new prompt rebuilds it.</summary>
        public int PromptVersion { get; set; }
        public DateTime CreatedUtc { get; set; }
        public List<LabelReferenceExample> Examples { get; set; } = new();
    }

    public sealed class LabelReferenceExample
    {
        public long AssetId { get; set; }
        /// <summary>Its score on the calibrated scale: the anchor level it stands for.</summary>
        public double Quality { get; set; }
        /// <summary>The user turn, exactly as sent.</summary>
        public string Text { get; set; } = "";
        /// <summary>The label JSON, exactly as sent as the assistant's answer.</summary>
        public string Answer { get; set; } = "";
        /// <summary>Image files in the set's folder, in the order they are sent.</summary>
        public List<string> ImageFiles { get; set; } = new();
    }

    public sealed class HarvesterState
    {
        public Dictionary<string, string> QueryCursors { get; set; } = new();
        public Dictionary<string, DateTime> QueryLastRunUtc { get; set; } = new();
        public List<string> PriorityQueries { get; set; } = new();
        public long Discovered { get; set; }
        public long Downloaded { get; set; }
        public long Labeled { get; set; }
        /// <summary>Labels re-rated on the current quality scale (AssetLabeler.ScaleVersion).</summary>
        public long Relabeled { get; set; }
        public DateTime? LastRunUtc { get; set; }
        public string? LastError { get; set; }
    }
}
