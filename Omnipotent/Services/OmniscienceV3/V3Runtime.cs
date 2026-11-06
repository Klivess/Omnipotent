using Omnipotent.Services.OmniscienceV3.Api;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Llm;
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
        private int disposed;

        private V3Runtime(OmniscienceStores stores, ModuleHost host, OmniLlm llm, LlmBackgroundGate gate, LiveStreamHub stream, IClock clock, long startedAtMs)
        {
            Stores = stores;
            Host = host;
            Llm = llm;
            BackgroundGate = gate;
            Stream = stream;
            Clock = clock;
            StartedAtMs = startedAtMs;
        }

        public OmniscienceStores Stores { get; }
        public ModuleHost Host { get; }
        public OmniLlm Llm { get; }
        public LlmBackgroundGate BackgroundGate { get; }
        public LiveStreamHub Stream { get; }
        public IClock Clock { get; }
        public long StartedAtMs { get; }

        public static async Task<V3Runtime> StartAsync(V3RuntimeOptions options, Action<string>? stage = null, CancellationToken ct = default)
        {
            long started = options.Clock.NowMs;
            stage?.Invoke("Opening stores");
            var stores = await OmniscienceStores.OpenAsync(options.Paths, options.Clock, ct, options.LockWait).ConfigureAwait(false);
            ModuleHost? host = null;
            try
            {
                var llm = new OmniLlm(options.LlmClient, stores.Intel, options.Clock, new TokenEstimator());
                var gate = new LlmBackgroundGate(llm);
                host = new ModuleHost(stores, options.Clock, options.Kernel, options.Log, options.LogError, gate);
                foreach (var module in options.Modules) host.Register(module);
                stage?.Invoke($"Starting the kernel ({options.Modules.Count} module(s))");
                await host.StartAsync(ct).ConfigureAwait(false);
                var stream = new LiveStreamHub(host.Events, options.Clock, options.LogError);
                return new V3Runtime(stores, host, llm, gate, stream, options.Clock, started);
            }
            catch
            {
                if (host != null) await host.DisposeAsync().ConfigureAwait(false);
                await stores.DisposeAsync().ConfigureAwait(false);
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
