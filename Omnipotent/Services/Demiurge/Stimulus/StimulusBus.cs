using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.Demiurge.Stimulus
{
    /// <summary>
    /// Ties the stimulus subsystem together: raw envelope from an adapter → triage against the
    /// hook's criterion → (if confirmed) durable enqueue → delivery to the destination agent,
    /// which for the Commander means a wake. Inter-agent and Klives messages ride the same bus,
    /// so the protocol is uniform (§5.2).
    /// </summary>
    public class StimulusBus
    {
        private readonly StimulusHookStore hooks;
        private readonly StimulusQueue queue;
        private readonly StimulusAgent triageAgent;
        private readonly StewardEventLogStore eventLog;
        private readonly StewardStore stewardStore;
        private readonly Action<string> log;

        /// <summary>Delivers a confirmed stimulus to its destination. Set by the service to wake the Commander.</summary>
        public Func<StimulusEnvelope, Task<string?>>? DeliverToAgent { get; set; }

        public StimulusBus(
            StimulusHookStore hooks, StimulusQueue queue, StimulusAgent triageAgent,
            StewardEventLogStore eventLog, StewardStore stewardStore, Action<string> log)
        {
            this.hooks = hooks;
            this.queue = queue;
            this.triageAgent = triageAgent;
            this.eventLog = eventLog;
            this.stewardStore = stewardStore;
            this.log = log ?? (_ => { });

            queue.OnClaim = HandleDeliveryAsync;
        }

        /// <summary>
        /// Ingests a raw event produced by an adapter for a specific hook. Triages it; on confirm,
        /// durably enqueues it for the hook's destination agent.
        /// </summary>
        public async Task IngestAsync(StimulusHookRecord hook, string payload, List<string>? artifactIDs = null,
            string? supersessionKey = null, TimeSpan? ttl = null)
        {
            if (!hook.Enabled) return;
            var steward = stewardStore.GetSteward(hook.StewardID);
            if (steward == null) return;
            // A paused/completed Steward doesn't consume stimuli — they simply don't wake it.
            if (steward.Status is StewardStatus.Completed or StewardStatus.Archived) return;

            var env = new StimulusEnvelope
            {
                StewardID = hook.StewardID,
                HookID = hook.HookID,
                SourceKind = hook.SourceKind,
                Durability = hook.Durability,
                SupersessionKey = supersessionKey ?? "",
                Payload = payload,
                ArtifactIDs = artifactIDs ?? new(),
                ExpiresAt = ttl.HasValue ? DateTime.UtcNow + ttl.Value : null,
            };

            var triage = await triageAgent.EvaluateAsync(env, EffectiveRecognitionCriterion(hook));
            if (!triage.Confirmed)
            {
                log($"Stimulus rejected ({hook.SourceKind}/{hook.HookID}): {triage.Verdict}");
                return;
            }
            env.Verdict = triage.Verdict;
            if (env.SourceKind != "inter-agent")
            {
                eventLog.Append(new StewardEvent
                {
                    StewardID = env.StewardID,
                    AgentID = hook.DestinationAgentID,
                    Type = StewardEventTypes.Stimulus,
                    Author = "stimulus",
                    Text = $"[{env.SourceKind}] {env.Verdict}\n{Truncate(env.Payload, 1000)}",
                    StimulusID = env.EnvelopeID,
                    ArtifactIDs = env.ArtifactIDs,
                });
            }
            await queue.EnqueueAsync(env, hook.DestinationAgentID);
        }

        /// <summary>Called by the queue reader when a confirmed envelope reaches the front for its agent.</summary>
        private async Task<string?> HandleDeliveryAsync(StimulusEnvelope env)
        {
            return DeliverToAgent == null ? null : await DeliverToAgent(env);
        }

        /// <summary>Boot: replay durable undelivered envelopes so nothing is lost across a restart.</summary>
        public void Replay() => queue.ReplayUndelivered();

        private static string EffectiveRecognitionCriterion(StimulusHookRecord hook)
        {
            string criterion = (hook.RecognitionCriterion ?? "").Trim();
            if (!hook.SourceKind.Equals("discord", StringComparison.OrdinalIgnoreCase)) return criterion;

            try
            {
                var spec = JObject.Parse(string.IsNullOrWhiteSpace(hook.SourceSpecJson) ? "{}" : hook.SourceSpecJson);
                string? contains = spec["contains"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(contains))
                {
                    string semanticFilter = "The Discord message content must contain the literal text "
                        + JsonConvert.ToString(contains.Trim()) + ".";
                    criterion = criterion.Length == 0 ? semanticFilter : criterion + "\n" + semanticFilter;
                }
            }
            catch (JsonException) { }
            return criterion;
        }

        private static string Truncate(string s, int max) => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
    }
}
