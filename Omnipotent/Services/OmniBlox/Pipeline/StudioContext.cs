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
        /// <summary>The owner's signed-in Roblox session (.ROBLOSECURITY), for the dashboard actions no API key can do.</summary>
        public string? SessionCookie { get; set; }
        public long? CreatorUserId { get; set; }
        public long? CreatorGroupId { get; set; }
        public List<ExperienceSlot> Slots { get; set; } = new();
        /// <summary>A private experience kept for the owner's playtests of any game at any stage (never a game's slot).</summary>
        public ExperienceSlot? PlaytestSlot { get; set; }
        public bool RequireLaunchApproval { get; set; } = true;
        /// <summary>The owner plays and signs off each tested build before any marketing work starts.</summary>
        public bool RequireBuildSignoff { get; set; } = true;
        public int MaxGamesInProduction { get; set; } = 1;
        public bool HarvesterEnabled { get; set; } = true;
        public string TelemetryBaseUrl { get; set; } = "https://klive.dev";
        /// <summary>US dollars paid per Robux by the Developer Exchange (shown as an estimate only).</summary>
        public double DevExRate { get; set; } = 0.0038;
        public int MaintenanceIntervalDays { get; set; } = 7;
        /// <summary>The output cap of one agent reply (reasoning included); big enough for a whole source file.</summary>
        public int MaxOutputTokens { get; set; } = 16_384;
        /// <summary>The model's context window. A cycle hands over to the next one as its prompt nears it (the work carries on).</summary>
        public long ModelContextTokens { get; set; } = 260_000;
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
        /// <summary>The owner's Roblox session: the icon, store thumbnails and maturity questionnaire (null where the studio runs without one).</summary>
        public RobloxSessionClient? Session { get; init; }
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

        /// <summary>The cycles running now, for the dashboard's live view.</summary>
        public LiveBoard Live { get; } = new();

        /// <summary>The Luau type checker (luau-lsp); null where the studio runs without it (unit tests).</summary>
        public LuauToolchain? Luau { get; init; }

        /// <summary>The local engine (Lune), which runs the engine test while Roblox's cannot (no API key or slot); null where the studio runs without it.</summary>
        public ILocalEngine? LocalEngine { get; init; }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<long, (DateTime Stamp, SpriteImage? Image)> sprites = new();

        /// <summary>Roblox's own picture of a library model (transparent background), decoded once; null when there is none.</summary>
        public SpriteImage? SpriteFor(long assetId)
        {
            string path = Library.ThumbnailPath(assetId);
            if (!File.Exists(path)) return null;
            var stamp = File.GetLastWriteTimeUtc(path);
            if (sprites.TryGetValue(assetId, out var cached) && cached.Stamp == stamp) return cached.Image;
            SpriteImage? image;
            try { image = SpriteImage.Decode(File.ReadAllBytes(path)); }
            catch (IOException) { image = null; }
            if (sprites.Count > 800) sprites.Clear();
            sprites[assetId] = (stamp, image);
            return image;
        }

        /// <summary>A picture of a game's world as it is now (see <see cref="WorldRenderer"/> for the views).</summary>
        public byte[] Render(string gameId, string view, int width = 1280, int height = 760, bool labels = false, bool caption = true, bool marketing = false,
            (System.Numerics.Vector3 Eye, System.Numerics.Vector3 Target, float Fov)? camera = null)
        {
            var result = Compile(gameId);
            if (result.Parts.Count == 0 && result.Terrain == null) throw new InvalidOperationException("the world is empty");
            return RenderCompiled(gameId, result, view, width, height, labels, caption, marketing, camera);
        }

        /// <summary>Everything the renderer needs to draw a compiled world: its parts, scene, terrain and the library's model pictures.</summary>
        public RenderWorld RenderWorldFor(string gameId, CompileResult result) =>
            new() { Parts = result.Parts, Scene = Workspace(gameId).LoadScene(), Terrain = result.Terrain, Sprite = SpriteFor };

        public byte[] RenderCompiled(string gameId, CompileResult result, string view, int width = 1280, int height = 760, bool labels = false, bool caption = true, bool marketing = false,
            (System.Numerics.Vector3 Eye, System.Numerics.Vector3 Target, float Fov)? camera = null)
        {
            var world = RenderWorldFor(gameId, result);
            return WorldRenderer.Render(world, new RenderRequest
            {
                View = view, Width = width, Height = height, Labels = labels, Caption = caption && !marketing, Marketing = marketing, Camera = camera,
            });
        }

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

        private (long Version, Build.WorldGen.AssetCatalog Catalog)? catalog;
        private readonly object catalogLock = new();

        /// <summary>The library sorted by role, style and biome for the world generator and the map report (rebuilt when the library changes).</summary>
        public Build.WorldGen.AssetCatalog Catalog()
        {
            long version = Library.Version;
            lock (catalogLock)
            {
                if (catalog is { } c && c.Version == version) return c.Catalog;
                var built = Build.WorldGen.AssetCatalog.FromRecords(Library.Labeled());
                catalog = (version, built);
                return built;
            }
        }

        /// <summary>How to stand a library asset on the ground, from its measurements and label.</summary>
        public AssetPlacement? AssetInfo(long assetId)
        {
            var rec = Library.Get(assetId);
            if (rec?.Measurements is not { } m || !rec.Placeable) return null;
            return new AssetPlacement(new RbxVector3(m.Pivot[0], m.Pivot[1], m.Pivot[2]), rec.Label?.FrontYaw ?? 0, new RbxVector3(m.SizeX, m.SizeY, m.SizeZ), rec.Label?.What is { Length: > 0 } w ? w : rec.Name);
        }

        /// <summary>
        /// Compiles a game's workspace into a place (with the given product ids baked into its config). The
        /// engine-test build starts its own scripts in Roblox's engine (see <see cref="CompileInput.EngineTest"/>).
        /// </summary>
        public CompileResult Compile(string gameId, bool engineTest = false)
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
                EngineTest = engineTest,
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
