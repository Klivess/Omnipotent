using System.Globalization;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.OmniBlox.Agents;
using Omnipotent.Services.OmniBlox.Assets;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Insights;
using Omnipotent.Services.OmniBlox.Marketing;
using Omnipotent.Services.OmniBlox.Pipeline;
using Omnipotent.Services.OmniBlox.Roblox;

namespace Omnipotent.Services.OmniBlox
{
    /// <summary>
    /// An autonomous Roblox game studio: it researches what to build, builds the game (labelled free models
    /// + the Game Kit + its own Luau), tests it in Roblox's engine, makes its own marketing, publishes and
    /// monetizes it, then maintains it while the next game starts. Spends no money; its only model provider
    /// is AIRouter. See Docs/omniblox.md (technical) and Docs/omniblox-plan.md (product plan).
    /// </summary>
    public class OmniBlox : OmniService
    {
        private readonly Omnipotent.Services.KliveAPI.KliveAPI? injectedApi;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource stop = new();

        internal Task Ready => ready.Task;
        internal string StartupState { get; private set; } = "starting";
        internal StudioContext? Context { get; private set; }
        internal Orchestrator? Orchestrator { get; private set; }
        internal HarvestStepReport? LastHarvest { get; private set; }
        internal DateTime? LastHarvestUtc { get; private set; }
        internal (bool Ok, string? Reason) ModelGate { get; private set; } = (false, "not checked yet");

        public OmniBlox() : this(null) { }

        public OmniBlox(Omnipotent.Services.KliveAPI.KliveAPI? kliveApi)
        {
            name = "OmniBlox";
            threadAnteriority = ThreadAnteriority.Standard;
            injectedApi = kliveApi;
        }

        protected override async void ServiceMain()
        {
            _ = new OmniBloxRoutes(this, injectedApi).RegisterAsync();
            try
            {
                StartupState = "loading";
                string root = OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniBloxDirectory);
                var store = new OmniBloxStore(root, message => _ = ServiceLog(message, false));
                store.Load();
                store.StartFlusher();
                var library = new AssetLibrary(Path.Combine(root, "assets"));
                library.Open();
                var telemetry = new TelemetryStore(Path.Combine(root, "telemetry.db"));
                telemetry.Open();

                Func<DateTime> clock = () => DateTime.UtcNow;
                var settings = new StudioSettings();
                var http = new RobloxHttp(RobloxHttp.CreateDefaultClient(), clock);
                var publicApi = new RobloxPublicClient(http);
                var cloud = new RobloxCloudClient(http, () => settings.ApiKey);
                var session = new RobloxSessionClient(http, () => settings.SessionCookie, rotated => _ = SaveRotatedSessionAsync(rotated));
                var llm = new KliveLlmBridge(FindService<KliveLLM.KliveLLM>);
                var runner = new AgentCycleRunner(llm, clock);
                var playbook = new Playbook(store, clock);
                var analytics = new AnalyticsSync(cloud, store, clock);
                string ffmpeg = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.FFMpegDirectory), "ffmpeg.exe");
                var marketing = new MarketingStudio(ffmpeg);
                var harvester = new AssetHarvester(library, publicApi, cloud, new AssetLabeler(llm), llm, store, clock, message => _ = ServiceLog(message, false));

                var ctx = new StudioContext
                {
                    Store = store,
                    Library = library,
                    Public = publicApi,
                    Cloud = cloud,
                    Session = session,
                    Llm = llm,
                    Runner = runner,
                    Playbook = playbook,
                    Telemetry = telemetry,
                    Analytics = analytics,
                    Marketing = marketing,
                    Harvester = harvester,
                    Web = new KliveRagWebResearch(FindService<KliveRAG.KliveRAG>),
                    Clock = clock,
                    Log = message => _ = ServiceLog(message, false),
                    Alert = AlertAsync,
                    Settings = settings,
                    Luau = new Build.LuauToolchain(Path.Combine(root, "tools"), message => _ = ServiceLog(message, false)),
                    LocalEngine = new LuneEngine(new Build.LuneToolchain(Path.Combine(root, "tools"), message => _ = ServiceLog(message, false))),
                };
                Context = ctx;
                await RefreshSettingsAsync();
                Orchestrator = new Orchestrator(ctx);
                ServiceQuitRequest += OnQuit;
                ready.TrySetResult();
                StartupState = "running";

                _ = Task.Run(() => Orchestrator.RunAsync(stop.Token));
                _ = Task.Run(HarvesterLoopAsync);
                _ = Task.Run(SettingsLoopAsync);

                var counts = store.Read(s => (Games: s.Games.Count, Live: s.Games.Values.Count(g => g.Stage == GameStage.Live), Tasks: s.Studio.OwnerTasks.Count(t => t.Status == OwnerTaskStatus.Open)));
                await ServiceLog($"[OmniBlox] Ready: {counts.Games} game(s), {counts.Live} live, {counts.Tasks} open owner task(s). Studio {(settings.Enabled ? "ON" : "off")}, harvester {(settings.HarvesterEnabled ? "on" : "off")}, API key {(cloud.HasKey ? "set" : "missing")}.");
            }
            catch (Exception ex)
            {
                StartupState = "failed to start: " + ex.Message;
                ready.TrySetException(ex);
                await ServiceLogError(ex, "[OmniBlox] failed to start");
            }
        }

        internal async Task RefreshSettingsAsync()
        {
            var ctx = Context;
            if (ctx == null) return;
            var s = new StudioSettings
            {
                Enabled = await GetBoolOmniSetting("OmniBlox_Enabled", defaultValue: false),
                ApiKey = NullIfBlank(await GetStringOmniSetting("OmniBlox_RobloxApiKey", "", sensitive: true)),
                SessionCookie = NullIfBlank(await GetStringOmniSetting(SessionCookieSetting, "", sensitive: true)),
                CreatorUserId = ParseId(await GetStringOmniSetting("OmniBlox_CreatorUserId", "")),
                CreatorGroupId = ParseId(await GetStringOmniSetting("OmniBlox_CreatorGroupId", "")),
                Slots = (await GetStringListOmniSetting("OmniBlox_ExperienceSlots", new List<string>())).Select(ExperienceSlot.Parse).Where(x => x != null).Select(x => x!).ToList(),
                PlaytestSlot = ExperienceSlot.Parse(await GetStringOmniSetting("OmniBlox_PlaytestSlot", "")),
                RequireLaunchApproval = await GetBoolOmniSetting("OmniBlox_RequireLaunchApproval", defaultValue: true),
                RequireBuildSignoff = await GetBoolOmniSetting("OmniBlox_RequireBuildSignoff", defaultValue: true),
                MaxGamesInProduction = Math.Clamp(await GetIntOmniSetting("OmniBlox_MaxGamesInProduction", 1), 1, 3),
                HarvesterEnabled = await GetBoolOmniSetting("OmniBlox_HarvesterEnabled", defaultValue: false),
                TelemetryBaseUrl = (await GetStringOmniSetting("OmniBlox_TelemetryBaseUrl", "https://klive.dev")).Trim(),
                DevExRate = double.TryParse(await GetStringOmniSetting("OmniBlox_DevExRate", "0.0038"), NumberStyles.Float, CultureInfo.InvariantCulture, out double rate) ? rate : 0.0038,
                MaintenanceIntervalDays = Math.Clamp(await GetIntOmniSetting("OmniBlox_MaintenanceIntervalDays", 7), 1, 60),
                MaxOutputTokens = Math.Clamp(await GetIntOmniSetting("OmniBlox_MaxOutputTokens", 16_384), 4096, 65_536),
                // The model's window, not a hand-over size: the hand-over is worked out from it. (It replaced
                // OmniBlox_CycleContextTokens, whose 120K default is already stored on the server.)
                ModelContextTokens = Math.Clamp(await GetIntOmniSetting("OmniBlox_ModelContextTokens", 260_000), 64_000, 2_000_000),
            };
            // The playtest experience runs unfinished builds, so a game is never given it as its own.
            if (s.PlaytestSlot is { } playtest && s.Slots.RemoveAll(slot => slot.UniverseId == playtest.UniverseId) > 0 && ctx.Settings.PlaytestSlot?.UniverseId != playtest.UniverseId)
                await ServiceLog($"[OmniBlox] experience {playtest.UniverseId} is the playtest experience (OmniBlox_PlaytestSlot), so it is not used as an experience slot.", false);
            // Mutate in place: the Roblox client holds a closure over this object.
            var t = ctx.Settings;
            t.Enabled = s.Enabled;
            t.ApiKey = s.ApiKey;
            t.SessionCookie = s.SessionCookie;
            t.CreatorUserId = s.CreatorUserId;
            t.CreatorGroupId = s.CreatorGroupId;
            t.Slots = s.Slots;
            t.PlaytestSlot = s.PlaytestSlot;
            t.RequireLaunchApproval = s.RequireLaunchApproval;
            t.RequireBuildSignoff = s.RequireBuildSignoff;
            t.MaxGamesInProduction = s.MaxGamesInProduction;
            t.HarvesterEnabled = s.HarvesterEnabled;
            t.TelemetryBaseUrl = string.IsNullOrWhiteSpace(s.TelemetryBaseUrl) ? "https://klive.dev" : s.TelemetryBaseUrl;
            t.DevExRate = s.DevExRate;
            t.MaintenanceIntervalDays = s.MaintenanceIntervalDays;
            t.MaxOutputTokens = s.MaxOutputTokens;
            t.ModelContextTokens = s.ModelContextTokens;
            try { ModelGate = await ctx.Llm.CheckAvailableAsync(CancellationToken.None); } catch { }
        }

        internal const string SessionCookieSetting = "OmniBlox_RobloxSessionCookie";

        /// <summary>Roblox rotated the session cookie: save the new one, or the next restart starts with a dead session.</summary>
        private async Task SaveRotatedSessionAsync(string cookie)
        {
            try
            {
                var manager = await GetOmniGlobalSettingsManager();
                await manager.SetOmniSetting(SessionCookieSetting, cookie, serviceID, name, OmniSettingType.String, sensitive: true);
                if (Context is { } ctx) ctx.Settings.SessionCookie = cookie;
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "[OmniBlox] could not save the rotated Roblox session", false);
            }
        }

        internal async Task SetSwitchAsync(string setting, bool value)
        {
            var manager = await GetOmniGlobalSettingsManager();
            await manager.SetBoolOmniSetting(setting, value, serviceID, name);
            await RefreshSettingsAsync();
        }

        private async Task SettingsLoopAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(60), stop.Token); }
                catch (OperationCanceledException) { return; }
                try { await RefreshSettingsAsync(); } catch { /* settings briefly unavailable */ }
            }
        }

        private async Task HarvesterLoopAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                var ctx = Context;
                bool ran = false;
                if (ctx != null && ctx.Settings.HarvesterEnabled)
                {
                    try
                    {
                        LastHarvest = await ctx.Harvester.RunOnceAsync(stop.Token);
                        LastHarvestUtc = DateTime.UtcNow;
                        ran = true;
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        ctx.Store.Mutate(s => { s.Studio.Harvester.LastError = ex.Message; s.MarkStudio(); });
                        await ServiceLogError(ex, "[OmniBlox] harvester step failed", false);
                    }
                }
                try { await Task.Delay(ran ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(60), stop.Token); }
                catch (OperationCanceledException) { return; }
            }
        }

        private void OnQuit()
        {
            try { stop.Cancel(); } catch { }
            try { Context?.Store.StopAsync().Wait(TimeSpan.FromSeconds(8)); } catch { }
            try { Context?.Library.Dispose(); } catch { }
            try { Context?.Telemetry.Dispose(); } catch { }
        }

        internal async Task AlertAsync(string message)
        {
            try
            {
                var discord = FindService<KliveBot_Discord.KliveBotDiscord>();
                if (discord != null) await discord.SendMessageToKlives("[OmniBlox] " + message);
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "[OmniBlox] could not send a Discord alert", false);
            }
        }

        /// <summary>Finds a running service without the unbounded wait of GetServicesByType.</summary>
        internal T? FindService<T>() where T : OmniService
        {
            try { return GetActiveServices().ToArray().OfType<T>().FirstOrDefault(); }
            catch (InvalidOperationException) { return null; }
            catch (ArgumentException) { return null; }
        }

        private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private static long? ParseId(string? s) => long.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) && id > 0 ? id : null;
    }

    /// <summary>KliveRAG's self-hosted search (SearXNG) and page fetcher, for the research loop.</summary>
    internal sealed class KliveRagWebResearch : IWebResearch
    {
        private readonly Func<KliveRAG.KliveRAG?> resolve;

        public KliveRagWebResearch(Func<KliveRAG.KliveRAG?> resolve) => this.resolve = resolve;

        public async Task<string> SearchAsync(string query, int maxResults, string? timeRange, CancellationToken ct)
        {
            var rag = resolve();
            if (rag == null) return "Web search is unavailable right now (KliveRAG is not running); rely on the Roblox charts and game details.";
            return await rag.WebSearchAsync(query, maxResults, 2, timeRange).WaitAsync(TimeSpan.FromSeconds(90), ct);
        }

        public async Task<string> FetchAsync(string url, CancellationToken ct)
        {
            var rag = resolve();
            if (rag == null) return "Page fetching is unavailable right now (KliveRAG is not running).";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return "Not a web URL.";
            return await rag.WebFetchAsync(url, 2500).WaitAsync(TimeSpan.FromSeconds(60), ct);
        }
    }
}
