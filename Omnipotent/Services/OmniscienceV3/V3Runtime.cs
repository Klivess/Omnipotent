using Omnipotent.Services.OmniscienceV3.Api;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Explore;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Llm;
using Omnipotent.Services.OmniscienceV3.Ml;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3
{
    /// <summary>Everything needed to bring the engine up (the service passes production values; tests pass fakes).</summary>
    public sealed record V3RuntimeOptions
    {
        public required StorePaths Paths { get; init; }
        public required IClock Clock { get; init; }
        public required ILlmClient LlmClient { get; init; }
        public required Action<string> Log { get; init; }
        public required Action<string, Exception?> LogError { get; init; }
        public KernelOptions Kernel { get; init; } = new();
        public IReadOnlyList<IIntelModule> Modules { get; init; } = Array.Empty<IIntelModule>();
        public TimeSpan? LockWait { get; init; }
    }

    /// <summary>
    /// The running engine: stores, kernel, LLM gateway and live stream, started in dependency order and stopped in
    /// reverse (F362: v2 never cancelled its token, disposed only snapshots and never flushed its event queue).
    /// </summary>
    public sealed class V3Runtime : IAsyncDisposable
    {
        private readonly HttpClient modelDownloads;
        private int disposed;

        private V3Runtime(OmniscienceStores stores, ModuleHost host, OmniLlm llm, LlmBackgroundGate gate, LiveStreamHub stream, TacticRunner tactics,
            HttpClient modelDownloads, IClock clock, long startedAtMs)
        {
            Stores = stores;
            Host = host;
            Llm = llm;
            BackgroundGate = gate;
            Stream = stream;
            Tactics = tactics;
            this.modelDownloads = modelDownloads;
            Clock = clock;
            StartedAtMs = startedAtMs;
        }

        public OmniscienceStores Stores { get; }
        public ModuleHost Host { get; }
        public OmniLlm Llm { get; }
        public LlmBackgroundGate BackgroundGate { get; }
        public LiveStreamHub Stream { get; }
        /// <summary>The X-engine: the tactic catalogue, runs and playbooks.</summary>
        public TacticRunner Tactics { get; }
        public IClock Clock { get; }
        public long StartedAtMs { get; }

        /// <summary>The Explore engine, when a module provides one (the analytics workstream).</summary>
        public IExploreEngine? Explore => Host.Modules.OfType<IExploreEngine>().FirstOrDefault();

        public static async Task<V3Runtime> StartAsync(V3RuntimeOptions options, Action<string>? stage = null, CancellationToken ct = default)
        {
            long started = options.Clock.NowMs;
            stage?.Invoke("Opening stores");
            var stores = await OmniscienceStores.OpenAsync(options.Paths, options.Clock, ct, options.LockWait).ConfigureAwait(false);
            ModuleHost? host = null;
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            try
            {
                var llm = new OmniLlm(options.LlmClient, stores.Intel, options.Clock, new TokenEstimator());
                var gate = new LlmBackgroundGate(llm);
                var models = new ModelHub(options.Paths.Models, http, options.Clock, options.Log);
                host = new ModuleHost(stores, options.Clock, options.Kernel, options.Log, options.LogError, gate, new KernelServices { Llm = llm, Models = models });
                var registered = host;
                // The tactic runner is a core module: its catalogue is the kernel's tactics plus every module's.
                var tactics = new TacticRunner(() => BuiltinTactics.All.Concat(registered.Modules.Where(m => m is not TacticRunner).SelectMany(m => m.Tactics)));
                host.Register(tactics);
                foreach (var module in options.Modules) host.Register(module);
                stage?.Invoke($"Starting the kernel ({options.Modules.Count} module(s))");
                await host.StartAsync(ct).ConfigureAwait(false);
                _ = tactics.Catalog(); // fail fast on duplicate tactic keys
                var stream = new LiveStreamHub(host.Events, options.Clock, options.LogError);
                return new V3Runtime(stores, host, llm, gate, stream, tactics, http, options.Clock, started);
            }
            catch
            {
                if (host != null) await host.DisposeAsync().ConfigureAwait(false);
                await stores.DisposeAsync().ConfigureAwait(false);
                http.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try
            {
                // Workers release in-flight jobs (not counted as attempts) and the loop stops before the stores close.
                await Host.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await Stores.DisposeAsync().ConfigureAwait(false);
                modelDownloads.Dispose();
            }
        }
    }

    /// <summary>
    /// The service's stable state holder: routes are registered once against it and keep working across service
    /// restarts, answering an honest 503 (disabled / starting with its stage / failed with its reason) whenever
    /// the engine is not running, instead of 404s or hangs (F363).
    /// </summary>
    public sealed class V3Host
    {
        private readonly object gate = new();
        private V3Runtime? runtime;
        private CancellationTokenSource stopping = new();

        public V3Phase Phase { get; private set; } = V3Phase.Starting;
        public string Stage { get; private set; } = "Not started";
        public string? Failure { get; private set; }
        public long? StartedAtMs { get; private set; }
        public long? ReadyAtMs { get; private set; }

        public V3Runtime? Runtime { get { lock (gate) return runtime; } }

        /// <summary>Cancelled when the service stops; replaced on the next start.</summary>
        public CancellationToken Stopping { get { lock (gate) return stopping.Token; } }

        public void Begin(long nowMs)
        {
            lock (gate)
            {
                if (stopping.IsCancellationRequested)
                {
                    stopping.Dispose();
                    stopping = new CancellationTokenSource();
                }
                Phase = V3Phase.Starting;
                Stage = "Starting";
                Failure = null;
                StartedAtMs = nowMs;
                ReadyAtMs = null;
            }
        }

        public void SetStage(string stage)
        {
            lock (gate) Stage = stage;
        }

        public void SetDisabled(string reason)
        {
            lock (gate)
            {
                Phase = V3Phase.Disabled;
                Stage = reason;
            }
        }

        /// <summary>Publishes the started engine. False when a stop arrived while it was starting: the caller must
        /// dispose <paramref name="rt"/> itself, since nothing else will.</summary>
        public bool SetReady(V3Runtime rt, long nowMs)
        {
            lock (gate)
            {
                if (stopping.IsCancellationRequested) return false;
                runtime = rt;
                Phase = V3Phase.Ready;
                Stage = "Ready";
                ReadyAtMs = nowMs;
                return true;
            }
        }

        public void SetFailed(string reason)
        {
            lock (gate)
            {
                Phase = V3Phase.Failed;
                Failure = reason;
            }
        }

        /// <summary>The running engine, or the classified 503 a route should answer with.</summary>
        public V3Runtime Require()
        {
            lock (gate)
            {
                if (runtime != null && Phase == V3Phase.Ready) return runtime;
                throw Phase switch
                {
                    V3Phase.Disabled => ApiException.Unavailable(ApiErrors.Disabled, "Omniscience v3 is installed but off. Set OmniscienceV3_ShadowEnabled=true to run it in shadow mode."),
                    V3Phase.Failed => ApiException.Unavailable(ApiErrors.Failed, "Omniscience v3 failed to start: " + (Failure ?? "unknown reason") + ". The service log has the details."),
                    V3Phase.Stopping => ApiException.Unavailable(ApiErrors.Unavailable, "Omniscience v3 is stopping."),
                    _ => ApiException.Unavailable(ApiErrors.Starting, "Omniscience v3 is starting: " + Stage + "."),
                };
            }
        }

        /// <summary>Stops the engine: cancels in-flight requests and streams, then disposes the runtime.</summary>
        public async Task StopAsync()
        {
            V3Runtime? rt;
            lock (gate)
            {
                rt = runtime;
                runtime = null;
                if (Phase != V3Phase.Disabled && Phase != V3Phase.Failed) Phase = V3Phase.Stopping;
                if (!stopping.IsCancellationRequested) stopping.Cancel();
            }
            if (rt != null) await rt.DisposeAsync().ConfigureAwait(false);
            lock (gate)
            {
                if (Phase == V3Phase.Stopping) Stage = "Stopped";
            }
        }
    }
}
