using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Llm;
using Omnipotent.Services.OmniscienceV3.Ml;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    /// <summary>Live-stream topics of the replica (permission: replica.read, see V3Routes.TopicFilterFor).</summary>
    public static class ReplicaTopics
    {
        public const string Version = "replica.version";
        public const string Chat = "replica.chat";
        public const string Group = "replica.group";
        public const string Person = "replica.person";
    }

    /// <summary>The replica module's shared services, built once at initialisation.</summary>
    public sealed class ReplicaServices
    {
        private readonly object avSync = new();
        private AssistantVoiceModel? assistantVoice;
        private long assistantVoiceCheckedMs = long.MinValue;
        private long assistantVoiceUpdatedMs = -1;

        public ReplicaServices(ModuleContext kernel, Func<IEmbeddingEncoder?> encoder, int moduleVersion)
        {
            Kernel = kernel;
            Encoder = encoder;
            ModuleVersion = moduleVersion;
            Settings = new ReplicaSettings(kernel.Settings);
            Versions = new VersionStore(kernel.Stores.Intel, kernel.Clock);
            Llm = kernel.Llm == null ? null : new ReplicaLlm(kernel.Llm);
            Estimator = kernel.Llm?.Estimator ?? new TokenEstimator();
            Composer = new ReplicaComposer(Estimator);
            Generator = Llm == null ? null : new ReplicaGenerator(Llm, encoder, AssistantVoice);
            Snapshots = new SnapshotCache(kernel.Stores);
            Zones = new PersonZones(kernel.Stores, () => kernel.Settings.KlivesZone);
        }

        public ModuleContext Kernel { get; }
        public OmniscienceStores Stores => Kernel.Stores;
        public IClock Clock => Kernel.Clock;
        public int ModuleVersion { get; }
        public ReplicaSettings Settings { get; }
        public VersionStore Versions { get; }
        public ReplicaLlm? Llm { get; }
        public TokenEstimator Estimator { get; }
        public Func<IEmbeddingEncoder?> Encoder { get; }
        public ReplicaComposer Composer { get; }
        public ReplicaGenerator? Generator { get; }
        public SnapshotCache Snapshots { get; }
        public PersonZones Zones { get; }

        public string? EncoderModel => Encoder() is { } e ? e.ModelId + "@" + e.ModelVersion : null;

        public Func<long, TimeZoneInfo?> ZoneFunc(string personId, bool isSelf) => at => Zones.ZoneAt(personId, isSelf, at).Zone;

        /// <summary>The active model's real limits (fetched dynamically); a conservative default without a gateway.</summary>
        public async Task<ModelContextInfo> ModelAsync(CancellationToken ct) =>
            Llm == null ? new ModelContextInfo("none", "none", ModelContextRegistry.DefaultContextWindowTokens, ModelContextRegistry.DefaultMaxOutputTokens, "default", null, 0, null)
                        : await Llm.ContextAsync(ct).ConfigureAwait(false);

        public Task PublishAsync(string topic, object payload, CancellationToken ct) => Kernel.Events.PublishAsync(topic, payload, ct);

        /// <summary>The trained assistant-voice house model (re-read when retrained; checked at most once a minute).</summary>
        public AssistantVoiceModel? AssistantVoice()
        {
            long now = Clock.NowMs;
            lock (avSync)
            {
                if (now - assistantVoiceCheckedMs < 60_000) return assistantVoice;
                assistantVoiceCheckedMs = now;
            }
            using var c = Stores.Intel.OpenRead();
            var row = c.Query(null, "SELECT updated_at_ms, params_json, metrics_json, state FROM replica_models WHERE model_id=$m",
                r => (Updated: r.GetInt64(0), Params: r.GetStringOrNull(1), Metrics: r.GetStringOrNull(2), State: r.GetString(3)), ("$m", AssistantVoiceModel.ModelId)).FirstOrDefault();
            lock (avSync)
            {
                if (row.State != "ready") { assistantVoice = null; assistantVoiceUpdatedMs = -1; return null; }
                if (row.Updated != assistantVoiceUpdatedMs)
                {
                    assistantVoice = AssistantVoiceModel.FromJson(row.Params, row.Metrics);
                    assistantVoiceUpdatedMs = row.Updated;
                }
                return assistantVoice;
            }
        }

        /// <summary>Forces the next <see cref="AssistantVoice"/> call to re-read the model.</summary>
        public void InvalidateAssistantVoice()
        {
            lock (avSync) assistantVoiceCheckedMs = long.MinValue;
        }
    }
}
