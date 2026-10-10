using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.OmniTumblr.Api;
using Omnipotent.Services.OmniTumblr.Engine;
using Omnipotent.Services.OmniTumblr.Models;

namespace Omnipotent.Services.OmniTumblr
{
    /// <summary>
    /// Runs Tumblr blogs on autopilot: each managed blog has a strategy (weekly schedule, content source,
    /// caption style, tags) that the engine turns into posts — planned ahead, captioned (by AI, looking at
    /// the video), published on time, and measured afterwards.
    ///
    /// v2 (Oct 2026) replaced the v1 service, which had stopped working: it scheduled through TimeManager
    /// (whose replaced tasks keep firing, so restarts double-fired posts), never pulled MemeScraper content
    /// automatically, posted the AI prompt itself as the caption, and talked to Tumblr through an
    /// unmaintained library on the legacy post API. See Docs/omnitumblr.md.
    /// </summary>
    public class OmniTumblr : OmniService
    {
        private readonly Omnipotent.Services.KliveAPI.KliveAPI? injectedApi;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal OmniTumblrStore? Store { get; private set; }
        internal OmniTumblrAuth? Auth { get; private set; }
        internal OmniTumblrPlanner? Planner { get; private set; }
        internal OmniTumblrPublisher? Publisher { get; private set; }
        internal OmniTumblrAnalyticsSync? Sync { get; private set; }
        internal OmniTumblrEngine? Engine { get; private set; }
        internal OmniTumblrContentSources? ContentSources { get; private set; }
        internal OmniTumblrCaptioner? Captioner { get; private set; }
        internal OmniTumblrMediaTools? MediaTools { get; private set; }
        internal OmniTumblrApiBudget? ApiBudget { get; private set; }
        internal TumblrApiClient? TumblrApi { get; private set; }
        internal TumblrEdgeGate? EdgeGate { get; private set; }

        internal Task Ready => ready.Task;
        internal bool Enabled { get; private set; } = true;
        internal bool PublishingEnabled { get; private set; } = true;
        internal DateTime StartedUtc { get; private set; }
        internal string StartupState { get; private set; } = "starting";

        public OmniTumblr() : this(null) { }

        /// <summary>Program.cs hands over the KliveAPI instance so route registration never waits on service discovery.</summary>
        public OmniTumblr(Omnipotent.Services.KliveAPI.KliveAPI? kliveApi)
        {
            name = "OmniTumblr";
            threadAnteriority = ThreadAnteriority.Standard;
            injectedApi = kliveApi;
        }

        protected override async void ServiceMain()
        {
            StartedUtc = DateTime.UtcNow;
            // Routes first: data-backed handlers wait (briefly) for startup instead of 404ing.
            _ = new OmniTumblrRoutes(this, injectedApi).RegisterAsync();

            try
            {
                StartupState = "loading data";
                string root = OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniTumblrV2Directory);
                var store = new OmniTumblrStore(root, message => _ = ServiceLog(message, false));
                store.Load();
                store.Vault.EnsureReady();
                store.StartFlusher();
                Store = store;

                Func<DateTime> clock = () => DateTime.UtcNow;
                ApiBudget = new OmniTumblrApiBudget(clock);
                EdgeGate = new TumblrEdgeGate(clock) { Changed = OnEdgeChanged };
                TumblrApi = new TumblrApiClient(onCall: ApiBudget.Count, onRateHeaders: ApiBudget.Observe, edgeGate: EdgeGate);
                MediaTools = new OmniTumblrMediaTools(OmniPaths.GetPath(OmniPaths.GlobalPaths.FFMpegDirectory));
                ContentSources = new OmniTumblrContentSources(new MemeScraperReelCatalog(FindService<MemeScraper.MemeScraper>), MediaTools, store.LibraryDirectory);
                Captioner = new OmniTumblrCaptioner(new KliveLlmCaptionModel(FindService<KliveLLM.KliveLLM>));
                Auth = new OmniTumblrAuth(store, TumblrApi, clock);
                Planner = new OmniTumblrPlanner(store, ContentSources, Captioner, MediaTools, clock);
                Publisher = new OmniTumblrPublisher(store, TumblrApi, Auth, clock, AlertAsync, EdgeGate) { PublishingEnabled = () => PublishingEnabled };
                Sync = new OmniTumblrAnalyticsSync(store, TumblrApi, Auth, ApiBudget, clock, message => _ = ServiceLog(message, false), EdgeGate);
                Engine = new OmniTumblrEngine(store, Planner, Publisher, Sync, clock, (ex, message) => _ = ServiceLogError(ex, message));
                var planner = Planner;
                var engine = Engine;
                Publisher.OnContentFailure = async postId =>
                {
                    await planner.RefillSlotAsync(postId, CancellationToken.None);
                    engine.WakePlanner();
                };

                StartupState = "migrating v1 data";
                string? legacyCallback = null;
                try { legacyCallback = await GetStringOmniSetting("OmniTumblr_OAuthCallbackUrl", OmniTumblrAppConfig.DefaultCallbackUrl); } catch { }
                string? migration = OmniTumblrMigration.MigrateV1(store, OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniTumblrDirectory),
                    legacyCallback, DateTime.UtcNow, message => _ = ServiceLog(message));
                if (migration != null) await ServiceLog("[OmniTumblr] " + migration);

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
                    StartupState = "disabled (OmniTumblr_Enabled = false)";
                }
                _ = Task.Run(SettingsLoopAsync);

                var counts = store.Read(s => (Blogs: s.Blogs.Count, Connections: s.Connections.Count, Pending: s.AllPosts().Count(p => p.IsPending)));
                await ServiceLog($"[OmniTumblr] Ready in {(DateTime.UtcNow - StartedUtc).TotalMilliseconds:0} ms: {counts.Blogs} blog(s), {counts.Connections} connection(s), {counts.Pending} pending post(s). Engine {(Enabled ? "running" : "disabled")}.");
            }
            catch (Exception ex)
            {
                StartupState = "failed to start: " + ex.Message;
                ready.TrySetException(ex);
                await ServiceLogError(ex, "[OmniTumblr] failed to start");
            }
        }

        private async Task RefreshSettingsAsync()
        {
            Enabled = await GetBoolOmniSetting("OmniTumblr_Enabled", defaultValue: true);
            PublishingEnabled = await GetBoolOmniSetting("OmniTumblrV2_PublishingEnabled", defaultValue: true);

            string proxy = (await GetStringOmniSetting("OmniTumblr_Proxy", "") ?? "").Trim();
            if (TumblrApi != null && proxy != TumblrApi.Proxy)
            {
                if (TumblrApi.TrySetProxy(proxy, out string? error))
                {
                    rejectedProxy = null;
                    EdgeGate?.ProbeNow(); // a new route may well get through: try it at once
                    await ServiceLog(proxy.Length == 0 ? "[OmniTumblr] Tumblr traffic now goes direct." : "[OmniTumblr] Tumblr traffic now goes through the proxy set in OmniTumblr_Proxy.");
                }
                else if (proxy != rejectedProxy)
                {
                    rejectedProxy = proxy; // said once, not every minute
                    await ServiceLog($"[OmniTumblr] Ignoring OmniTumblr_Proxy: {error}");
                }
            }
        }

        private string? rejectedProxy;

        /// <summary>Tumblr's edge started refusing this server, or answered again: one event and log line per change.</summary>
        private void OnEdgeChanged(TumblrEdgeGateStatus status)
        {
            string message = status.Blocked
                ? $"Tumblr's edge is refusing this server's requests (an HTML 403 from nginx, before the API) — the server's network, not the accounts or posts. Posts and sync wait; next check {status.NextCheckUtc:HH:mm} UTC."
                : $"Tumblr is answering this server again after {OmniTumblrPublisher.Describe(TimeSpan.FromMinutes(status.LastBlockMinutes ?? 0))}; waiting posts are going out.";
            try { Store?.Mutate(s => s.AddEvent(status.Blocked ? EventLevel.Warning : EventLevel.Success, status.Blocked ? "tumblr.edge-blocked" : "tumblr.edge-cleared", message)); } catch { }
            _ = ServiceLog("[OmniTumblr] " + message, false);
            if (!status.Blocked)
            {
                Engine?.WakePublisher();
                Engine?.WakeSync();
            }
        }

        /// <summary>Picks up OmniSettings changes (kill switches) without a restart.</summary>
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
                        await ServiceLog("[OmniTumblr] Enabled via OmniSettings; engine started.");
                    }
                    else if (!Enabled && wasEnabled)
                    {
                        await Engine.StopAsync();
                        StartupState = "disabled (OmniTumblr_Enabled = false)";
                        await ServiceLog("[OmniTumblr] Disabled via OmniSettings; engine stopped.");
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
                await ServiceLogError(ex, "[OmniTumblr] could not send a Discord alert: " + message, false);
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
