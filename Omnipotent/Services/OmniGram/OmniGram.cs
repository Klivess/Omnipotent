using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.OmniGram.Api;
using Omnipotent.Services.OmniGram.Engine;
using Omnipotent.Services.OmniGram.Models;

namespace Omnipotent.Services.OmniGram
{
    /// <summary>
    /// Runs Instagram accounts on autopilot: each managed account has a strategy (weekly schedule, content
    /// source, caption voice, hashtags) that the engine turns into posts — planned ahead, made Instagram-ready,
    /// captioned by AI that looks at the video, published on time through Instagram's official API, and
    /// measured afterwards with real insights.
    ///
    /// v2 (Oct 2026) replaced v1, which had stopped working: it logged in with passwords through an
    /// end-of-life private-API library impersonating a 2020 Android app (Instagram challenged or refused the
    /// logins, and adding an account had crashed outright since April), scheduled through TimeManager (whose
    /// replaced tasks keep firing, so restarts multiplied jobs), never found MemeScraper's reels (relative paths),
    /// and posted the AI prompt itself as the caption. See Docs/omnigram.md.
    /// </summary>
    public class OmniGram : OmniService
    {
        private readonly Omnipotent.Services.KliveAPI.KliveAPI? injectedApi;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static readonly HttpClient SelfCheckHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15) })
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestHeaders = { { "User-Agent", "OmniGram-selfcheck/2.0" } },
        };

        internal OmniGramStore? Store { get; private set; }
        internal OmniGramAuth? Auth { get; private set; }
        internal OmniGramPlanner? Planner { get; private set; }
        internal OmniGramPublisher? Publisher { get; private set; }
        internal OmniGramAnalyticsSync? Sync { get; private set; }
        internal OmniGramEngine? Engine { get; private set; }
        internal OmniGramContentSources? ContentSources { get; private set; }
        internal OmniGramCaptioner? Captioner { get; private set; }
        internal OmniGramMediaTools? MediaTools { get; private set; }
        internal OmniGramMediaHost? MediaHost { get; private set; }
        internal OmniGramApiBudget? ApiBudget { get; private set; }
        internal InstagramGraphClient? InstagramApi { get; private set; }

        internal Task Ready => ready.Task;
        internal bool Enabled { get; private set; } = true;
        internal bool PublishingEnabled { get; private set; } = true;
        internal DateTime StartedUtc { get; private set; }
        internal string StartupState { get; private set; } = "starting";
        internal string V1Root => OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniGramDirectory);

        public OmniGram() : this(null) { }

        /// <summary>Program.cs hands over the KliveAPI instance so route registration never waits on service discovery.</summary>
        public OmniGram(Omnipotent.Services.KliveAPI.KliveAPI? kliveApi)
        {
            name = "OmniGram";
            threadAnteriority = ThreadAnteriority.Standard;
            injectedApi = kliveApi;
        }

        protected override async void ServiceMain()
        {
            StartedUtc = DateTime.UtcNow;
            // Routes first: data-backed handlers wait (briefly) for startup instead of 404ing.
            _ = new OmniGramRoutes(this, injectedApi).RegisterAsync();

            try
            {
                StartupState = "loading data";
                string root = OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniGramV2Directory);
                var store = new OmniGramStore(root, message => _ = ServiceLog(message, false));
                store.Load();
                store.Vault.EnsureReady();
                store.StartFlusher();
                Store = store;

                Func<DateTime> clock = () => DateTime.UtcNow;
                ApiBudget = new OmniGramApiBudget(clock);
                InstagramApi = new InstagramGraphClient(onCall: ApiBudget.Count, onUsage: ApiBudget.Observe);
                MediaTools = new OmniGramMediaTools(OmniPaths.GetPath(OmniPaths.GlobalPaths.FFMpegDirectory));
                MediaHost = new OmniGramMediaHost(store, clock);
                ContentSources = new OmniGramContentSources(new MemeScraperReelCatalog(FindService<MemeScraper.MemeScraper>), MediaTools, store.LibraryDirectory);
                Captioner = new OmniGramCaptioner(new KliveLlmCaptionModel(FindService<KliveLLM.KliveLLM>));
                Auth = new OmniGramAuth(store, InstagramApi, clock);
                Planner = new OmniGramPlanner(store, ContentSources, Captioner, MediaTools, clock);
                Publisher = new OmniGramPublisher(store, InstagramApi, Auth, MediaHost, clock, AlertAsync) { PublishingEnabled = () => PublishingEnabled };
                Sync = new OmniGramAnalyticsSync(store, InstagramApi, Auth, ApiBudget, MediaHost, clock, message => _ = ServiceLog(message, false)) { SelfCheckHttp = SelfCheckHttp };
                Engine = new OmniGramEngine(store, Planner, Publisher, Sync, clock, (ex, message) => _ = ServiceLogError(ex, message));

                var planner = Planner;
                var engine = Engine;
                async Task Refill(string postId)
                {
                    await planner.RefillSlotAsync(postId, CancellationToken.None);
                    engine.WakePlanner();
                }
                Publisher.OnContentFailure = Refill;
                Planner.OnContentRejected = Refill;
                Publisher.OnPublished = postId =>
                {
                    if (planner.CreateCompanionStory(postId) != null) engine.WakePlanner();
                    engine.WakeSync();
                    return Task.CompletedTask;
                };

                StartupState = "importing v1 data";
                string? migration = OmniGramMigration.MigrateV1(store, V1Root, DateTime.UtcNow, message => _ = ServiceLog(message));
                if (migration != null) await ServiceLog("[OmniGram] " + migration);
                bool passwords = OmniGramMigration.V1PasswordsPresent(V1Root);
                store.Mutate(s => { if (s.Engine.V1PasswordFilesPresent != passwords) { s.Engine.V1PasswordFilesPresent = passwords; s.MarkEngine(); } });

                await RefreshSettingsAsync();
                Publisher.RecoverInterrupted();
                ServiceQuitRequest += OnQuit;
                ready.TrySetResult();

                if (Enabled)
                {
                    Engine.Start();
                    StartupState = "running";
                }
                else
                {
                    StartupState = "disabled (OmniGram_Enabled = false)";
                }
                _ = Task.Run(SettingsLoopAsync);
                var mediaTools = MediaTools;
                _ = Task.Run(async () =>
                {
                    // Off the startup path: settings and the overview report it, and media preparation re-checks it anyway.
                    bool ok = await mediaTools.IsAvailableAsync(CancellationToken.None);
                    await ServiceLog($"[OmniGram] FFmpeg {(ok ? "found" : "NOT usable")}: {mediaTools.Status}", false);
                });

                var counts = store.Read(s => (Accounts: s.Accounts.Count, Connected: s.Accounts.Values.Count(a => a.IsConnected), Pending: s.AllPosts().Count(p => p.IsPending)));
                await ServiceLog($"[OmniGram] Ready in {(DateTime.UtcNow - StartedUtc).TotalMilliseconds:0} ms: {counts.Accounts} account(s), {counts.Connected} connected, {counts.Pending} pending post(s). Engine {(Enabled ? "running" : "disabled")}.");
            }
            catch (Exception ex)
            {
                StartupState = "failed to start: " + ex.Message;
                ready.TrySetException(ex);
                await ServiceLogError(ex, "[OmniGram] failed to start");
            }
        }

        private string? rejectedProxy;
        private string? rejectedVersion;

        private async Task RefreshSettingsAsync()
        {
            Enabled = await GetBoolOmniSetting("OmniGram_Enabled", defaultValue: true);
            PublishingEnabled = await GetBoolOmniSetting("OmniGramV2_PublishingEnabled", defaultValue: true);

            string baseUrl = (await GetStringOmniSetting("OmniGram_PublicBaseUrl", "https://klive.dev") ?? "").Trim().TrimEnd('/');
            if (MediaHost != null && baseUrl.Length > 0 && baseUrl != MediaHost.BaseUrl)
            {
                MediaHost.BaseUrl = baseUrl;
                Sync?.ExpediteHousekeeping(publicUrl: true);
            }

            string version = (await GetStringOmniSetting("OmniGram_GraphApiVersion", InstagramGraphClient.DefaultVersion) ?? "").Trim();
            if (InstagramApi != null && version != InstagramApi.Version && version != rejectedVersion)
            {
                if (InstagramApi.TrySetVersion(version)) await ServiceLog($"[OmniGram] Using Graph API {version}.");
                else { rejectedVersion = version; await ServiceLog($"[OmniGram] Ignoring OmniGram_GraphApiVersion \"{version}\" (expected something like {InstagramGraphClient.DefaultVersion})."); }
            }

            string proxy = (await GetStringOmniSetting("OmniGram_Proxy", "") ?? "").Trim();
            if (InstagramApi != null && proxy != InstagramApi.Proxy)
            {
                if (InstagramApi.TrySetProxy(proxy, out string? error))
                {
                    rejectedProxy = null;
                    await ServiceLog(proxy.Length == 0 ? "[OmniGram] Instagram traffic now goes direct." : "[OmniGram] Instagram traffic now goes through the proxy set in OmniGram_Proxy.");
                }
                else if (proxy != rejectedProxy)
                {
                    rejectedProxy = proxy; // said once, not every minute
                    await ServiceLog($"[OmniGram] Ignoring OmniGram_Proxy: {error}");
                }
            }
        }

        /// <summary>Picks up OmniSettings changes (kill switches, public URL) without a restart.</summary>
        private async Task SettingsLoopAsync()
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(60));
                try
                {
                    bool wasEnabled = Enabled;
                    await RefreshSettingsAsync();
                    if (Engine == null) continue;
                    if (Enabled && !wasEnabled)
                    {
                        Engine.Start();
                        StartupState = "running";
                        await ServiceLog("[OmniGram] Enabled via OmniSettings; engine started.");
                    }
                    else if (!Enabled && wasEnabled)
                    {
                        await Engine.StopAsync();
                        StartupState = "disabled (OmniGram_Enabled = false)";
                        await ServiceLog("[OmniGram] Disabled via OmniSettings; engine stopped.");
                    }
                }
                catch { /* settings service briefly unavailable */ }
            }
        }

        private void OnQuit()
        {
            try { Engine?.StopAsync().Wait(TimeSpan.FromSeconds(8)); } catch { }
            try { Store?.StopAsync().Wait(TimeSpan.FromSeconds(8)); } catch { }
        }

        internal async Task AlertAsync(string message)
        {
            try
            {
                var discord = FindService<KliveBot_Discord.KliveBotDiscord>();
                if (discord != null) await discord.SendMessageToKlives(message);
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "[OmniGram] could not send a Discord alert: " + message, false);
            }
        }

        /// <summary>Finds a running service without the unbounded wait of GetServicesByType.</summary>
        internal T? FindService<T>() where T : OmniService
        {
            try { return GetActiveServices().ToArray().OfType<T>().FirstOrDefault(); }
            catch (InvalidOperationException) { return null; }
            catch (ArgumentException) { return null; }
        }
    }
}
