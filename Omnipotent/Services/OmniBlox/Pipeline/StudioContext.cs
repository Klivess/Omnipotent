using System.Security.Cryptography;
using Omnipotent.Services.OmniBlox.Agents;
using Omnipotent.Services.OmniBlox.Assets;
using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Insights;
using Omnipotent.Services.OmniBlox.Marketing;
using Omnipotent.Services.OmniBlox.Rbx;
using Omnipotent.Services.OmniBlox.Roblox;

namespace Omnipotent.Services.OmniBlox.Pipeline
{
    /// <summary>The owner's switches, read from OmniSettings every minute.</summary>
    public sealed class StudioSettings
    {
        public bool Enabled { get; set; }
        public string? ApiKey { get; set; }
        public long? CreatorUserId { get; set; }
        public long? CreatorGroupId { get; set; }
        public List<ExperienceSlot> Slots { get; set; } = new();
        /// <summary>A private experience kept for the owner's playtests of any game at any stage (never a game's slot).</summary>
        public ExperienceSlot? PlaytestSlot { get; set; }
        public bool RequireLaunchApproval { get; set; } = true;
        public int MaxGamesInProduction { get; set; } = 1;
        public bool HarvesterEnabled { get; set; } = true;
        public string TelemetryBaseUrl { get; set; } = "https://klive.dev";
        /// <summary>US dollars paid per Robux by the Developer Exchange (shown as an estimate only).</summary>
        public double DevExRate { get; set; } = 0.0038;
        public int MaintenanceIntervalDays { get; set; } = 7;
    }

    /// <summary>Web research for the research loop (KliveRAG's SearXNG search and page fetch in production).</summary>
    public interface IWebResearch
    {
        Task<string> SearchAsync(string query, int maxResults, string? timeRange, CancellationToken ct);
        Task<string> FetchAsync(string url, CancellationToken ct);
    }

    /// <summary>Everything the pipeline's stages and tools share.</summary>
    public sealed class StudioContext
    {
        public required OmniBloxStore Store { get; init; }
        public required AssetLibrary Library { get; init; }
        public required RobloxPublicClient Public { get; init; }
        public required RobloxCloudClient Cloud { get; init; }
        public required IOmniBloxLlm Llm { get; init; }
        public required AgentCycleRunner Runner { get; init; }
        public required Playbook Playbook { get; init; }
        public required TelemetryStore Telemetry { get; init; }
        public required AnalyticsSync Analytics { get; init; }
        public required MarketingStudio Marketing { get; init; }
        public required AssetHarvester Harvester { get; init; }
        public required IWebResearch Web { get; init; }
        public required Func<DateTime> Clock { get; init; }
        public required Action<string> Log { get; init; }
        public Func<string, Task> Alert { get; init; } = _ => Task.CompletedTask;
        public StudioSettings Settings { get; set; } = new();

        private Playtests? playtests;

        /// <summary>The owner's playtests: builds of any revision, published to a private experience or downloaded for Studio.</summary>
        public Playtests Playtests => LazyInitializer.EnsureInitialized(ref playtests, () => new Playtests(this));

        public RobloxCreator Creator => new(Settings.CreatorUserId, Settings.CreatorGroupId);

        public GameWorkspace Workspace(string gameId) =>
            new(Store.WorkspaceDirectory(gameId), Path.Combine(Store.GameDirectory(gameId), "snapshots"));

        public string TelemetryUrl => Settings.TelemetryBaseUrl.TrimEnd('/') + "/omniblox/telemetry";

        public GameDossier? Game(string gameId) => Store.Read(s => s.Game(gameId) is { } g ? OmniBloxStore.Clone(g) : null);

        public void Update(string gameId, Action<GameDossier> change)
        {
            Store.Mutate(s =>
            {
                var g = s.Game(gameId) ?? throw new KeyNotFoundException("No game " + gameId);
                change(g);
                s.Touch(g, Clock());
            });
        }

        public void Event(EventLevel level, string kind, string message, string? gameId = null) =>
            Store.Mutate(s => s.AddEvent(level, kind, message, gameId, Clock()));

        /// <summary>How to stand a library asset on the ground, from its measurements and label.</summary>
        public AssetPlacement? AssetInfo(long assetId)
        {
            var rec = Library.Get(assetId);
            if (rec?.Measurements is not { } m || !rec.Placeable) return null;
            return new AssetPlacement(new RbxVector3(m.Pivot[0], m.Pivot[1], m.Pivot[2]), rec.Label?.FrontYaw ?? 0, new RbxVector3(m.SizeX, m.SizeY, m.SizeZ), rec.Label?.What is { Length: > 0 } w ? w : rec.Name);
        }

        /// <summary>Compiles a game's workspace into a place (with the given product ids baked into its config).</summary>
        public CompileResult Compile(string gameId)
        {
            var game = Game(gameId) ?? throw new KeyNotFoundException("No game " + gameId);
            return PlaceCompiler.Compile(new CompileInput
            {
                GameId = gameId,
                Workspace = Workspace(gameId),
                LoadAsset = Library.LoadModel,
                AssetInfo = AssetInfo,
                Products = game.Products,
                TelemetryUrl = TelemetryUrl,
                TelemetrySecret = game.TelemetrySecret,
            });
        }

        public static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

        public void AddUsage(string gameId, CycleReport report) => Store.Mutate(s =>
        {
            var g = s.Game(gameId);
            if (g != null)
            {
                g.Usage.Add(report.Turns, report.PromptTokens, report.CachedTokens, report.CompletionTokens);
                g.Usage.AddResent(report.ResentTokens, report.ResentCachedTokens);
                s.MarkGame(gameId);
            }
            AssetHarvester.AddDailyUsage(s.Studio, Clock(), report.Turns, report.PromptTokens, report.CachedTokens, report.CompletionTokens,
                report.ResentTokens, report.ResentCachedTokens);
            s.MarkStudio();
        });
    }
}
