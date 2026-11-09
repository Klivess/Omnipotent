using Omnipotent.Services.OmniscienceV3.Kernel;

namespace Omnipotent.Services.OmniscienceV3.Ml.Encoders
{
    /// <summary>
    /// The production shared encoder, opened on first use rather than at startup: the pinned files (118 MB for int8) are
    /// fetched inside the first embedding job, so a slow or failed download is a retried job with a classified error,
    /// never a stalled service start. Its <see cref="ModelVersion"/> and <see cref="Dimensions"/> are known before
    /// loading (they come from the pinned variant), so readers can address vectors.db without touching the model.
    /// A failed load is retried on the next call (ModelHub resumes partial downloads).
    /// </summary>
    public sealed class LazyE5Encoder : IEmbeddingEncoder, IEncoderDiagnostics, IDisposable
    {
        private readonly ModelHub hub;
        private readonly OnnxRuntimeHost onnx;
        private readonly EncoderOptions options;
        private readonly CpuThrottle throttle;
        private readonly SemaphoreSlim loadGate = new(1, 1);
        private volatile E5TextEncoder? inner;

        public LazyE5Encoder(ModelHub hub, OnnxRuntimeHost onnx, EncoderVariant variant, EncoderOptions options, CpuThrottle throttle)
        {
            this.hub = hub;
            this.onnx = onnx;
            Variant = variant;
            this.options = options;
            this.throttle = throttle;
            ModelVersion = variant.VersionFor(options);
        }

        public EncoderVariant Variant { get; }
        public string ModelId => Variant.ModelId;
        public string ModelVersion { get; }
        public int Dimensions => Variant.Dimensions;
        public bool Loaded => inner != null;
        public CpuThrottle Throttle => throttle;

        public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            var encoder = inner ?? await LoadAsync(ct).ConfigureAwait(false);
            return await encoder.EmbedAsync(texts, ct).ConfigureAwait(false);
        }

        private async Task<E5TextEncoder> LoadAsync(CancellationToken ct)
        {
            await loadGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return inner ??= await E5TextEncoder.CreateAsync(hub, onnx, Variant, options, throttle, ct).ConfigureAwait(false);
            }
            finally
            {
                loadGate.Release();
            }
        }

        public EncoderStats Stats() => inner?.Stats()
            ?? new EncoderStats(ModelVersion, "not loaded", 0, 0, 0, 0, 0, throttle.Pauses, throttle.PausedMs);

        public void Dispose()
        {
            onnx.Dispose();
            loadGate.Dispose();
        }
    }

    /// <summary>Builds the production encoder from the classification settings (read once at startup).</summary>
    public static class SharedEncoder
    {
        public const string VariantSetting = "classify.encoder_variant";
        public const string ThreadsSetting = "classify.encoder_threads";
        public const string CpuPauseSetting = "classify.cpu_pause_above";

        public const string DefaultVariant = "int8";
        public const int DefaultThreads = 2;
        public const double DefaultCpuPause = 0.85;

        /// <summary>The factory the service passes to <see cref="V3RuntimeOptions.EncoderFactory"/>.</summary>
        public static Func<ModelHub, KernelSettings, IEmbeddingEncoder> Production(Action<string> log) => (hub, settings) =>
        {
            var variant = EncoderCatalog.All.FirstOrDefault(v => string.Equals(v.Key, settings.Get(VariantSetting, DefaultVariant), StringComparison.OrdinalIgnoreCase))
                          ?? EncoderCatalog.Int8;
            int threads = Math.Clamp(settings.Get(ThreadsSetting, DefaultThreads), 1, Math.Max(1, Environment.ProcessorCount));
            double pause = Math.Clamp(settings.Get(CpuPauseSetting, DefaultCpuPause), 0.3, 1.0);
            // Two intra-op threads by default (plan R2: "on 2 spare cores"); DirectML stays off until measured on the host.
            var onnx = new OnnxRuntimeHost(threads, preferDirectML: false, log);
            return new LazyE5Encoder(hub, onnx, variant, new EncoderOptions(), new CpuThrottle(HostCpuMonitor.Shared, pause));
        };
    }
}
