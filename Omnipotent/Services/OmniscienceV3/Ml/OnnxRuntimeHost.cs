using System.Collections.Concurrent;
using Microsoft.ML.OnnxRuntime;
using SessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace Omnipotent.Services.OmniscienceV3.Ml
{
    /// <summary>Which execution provider a session ended up on.</summary>
    public enum OnnxProvider { Cpu, DirectML }

    /// <summary>A shared ONNX session plus the gate that serialises runs on it.</summary>
    public sealed class SharedOnnxSession : IDisposable
    {
        internal SharedOnnxSession(InferenceSession session, OnnxProvider provider, int maxConcurrentRuns)
        {
            Session = session;
            Provider = provider;
            Gate = new SemaphoreSlim(Math.Max(1, maxConcurrentRuns), Math.Max(1, maxConcurrentRuns));
        }

        public InferenceSession Session { get; }
        public OnnxProvider Provider { get; }
        /// <summary>Bounds concurrent Run() calls (and therefore peak memory).</summary>
        public SemaphoreSlim Gate { get; }

        public void Dispose()
        {
            Session.Dispose();
            Gate.Dispose();
        }
    }

    /// <summary>
    /// One process-wide session per model file (fixes F330: several CPU sessions per model, each with
    /// cores/2 threads). Intra-op threads are capped so the CPU lane leaves headroom for the rest of the
    /// platform (plan R1/R2). DirectML is tried when requested and silently falls back to CPU when the
    /// installed runtime or the host has no DirectX 12 GPU.
    /// </summary>
    public sealed class OnnxRuntimeHost : IDisposable
    {
        private readonly ConcurrentDictionary<string, Lazy<SharedOnnxSession>> sessions = new(StringComparer.OrdinalIgnoreCase);
        private readonly int intraOpThreads;
        private readonly bool preferDirectML;
        private readonly Action<string> log;

        public OnnxRuntimeHost(int intraOpThreads, bool preferDirectML, Action<string> log)
        {
            this.intraOpThreads = Math.Clamp(intraOpThreads, 1, Math.Max(1, Environment.ProcessorCount));
            this.preferDirectML = preferDirectML;
            this.log = log;
        }

        public SharedOnnxSession Get(string modelPath, int maxConcurrentRuns = 1) =>
            sessions.GetOrAdd(modelPath, p => new Lazy<SharedOnnxSession>(() => Create(p, maxConcurrentRuns))).Value;

        private SharedOnnxSession Create(string modelPath, int maxConcurrentRuns)
        {
            if (preferDirectML)
            {
                var dmlOptions = BaseOptions();
                try
                {
                    dmlOptions.AppendExecutionProvider_DML(0);
                    var s = new InferenceSession(modelPath, dmlOptions);
                    log($"[OmniscienceV3] ONNX session on DirectML: {Path.GetFileName(modelPath)}");
                    return new SharedOnnxSession(s, OnnxProvider.DirectML, maxConcurrentRuns);
                }
                catch (Exception ex) when (ex is OnnxRuntimeException or EntryPointNotFoundException or DllNotFoundException or NotSupportedException)
                {
                    log($"[OmniscienceV3] DirectML unavailable ({ex.GetType().Name}); using CPU for {Path.GetFileName(modelPath)}.");
                    dmlOptions.Dispose();
                }
            }
            var options = BaseOptions();
            var session = new InferenceSession(modelPath, options);
            return new SharedOnnxSession(session, OnnxProvider.Cpu, maxConcurrentRuns);
        }

        private SessionOptions BaseOptions() => new()
        {
            IntraOpNumThreads = intraOpThreads,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };

        public void Dispose()
        {
            foreach (var s in sessions.Values)
                if (s.IsValueCreated) s.Value.Dispose();
            sessions.Clear();
        }
    }
}
