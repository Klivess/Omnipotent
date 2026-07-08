namespace Omnipotent.Services.Projects.Stimulus
{
    /// <summary>An IDisposable that runs a callback once on dispose — used to package event unsubscription as a token.</summary>
    public sealed class ActionDisposable : IDisposable
    {
        private Action? onDispose;
        public ActionDisposable(Action onDispose) => this.onDispose = onDispose;
        public void Dispose() { var a = onDispose; onDispose = null; a?.Invoke(); }
    }

    /// <summary>Delivery durability class for a stimulus (§5.4).</summary>
    public enum StimulusDurability
    {
        /// <summary>At-least-once, per-source FIFO, replayed across restarts.</summary>
        Standard,
        /// <summary>Durable-with-supersession: a newer envelope from the same source key replaces an
        /// undelivered older one, and carries a short TTL — for high-frequency sensors (screen diffs).</summary>
        SupersedingByKey,
    }

    /// <summary>
    /// A stimulus hook: a durable subscription (§5.1). Full CRUD by the Commander and by Klives
    /// via the UI; every change is itself an event in the log.
    /// </summary>
    public class StimulusHookRecord
    {
        public string HookID { get; set; } = "";
        public string ProjectID { get; set; } = "";
        /// <summary>Agent that owns/created the hook (Commander or a sub-agent).</summary>
        public string OwningAgentID { get; set; } = "commander";
        /// <summary>Source kind: timer | webhook | file-watch | screen-diff | process-exit | inter-agent | klives | email | discord | script.</summary>
        public string SourceKind { get; set; } = "";
        /// <summary>Source-specific spec (e.g. timer interval, watched path, screen region) as JSON.</summary>
        public string SourceSpecJson { get; set; } = "{}";
        /// <summary>Natural-language criterion the Stimulus Agent evaluates raw events against (§5.1).</summary>
        public string RecognitionCriterion { get; set; } = "";
        /// <summary>Agent the confirmed stimulus is delivered to (its wake trigger). "commander" by default.</summary>
        public string DestinationAgentID { get; set; } = "commander";
        public int Priority { get; set; } = 0;
        public StimulusDurability Durability { get; set; } = StimulusDurability.Standard;
        public bool Enabled { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// A raw stimulus flowing through the bus. Produced by an adapter, triaged by the Stimulus
    /// Agent against the owning hook's criterion, and (if confirmed) delivered to the
    /// destination agent as its wake trigger.
    /// </summary>
    public class StimulusEnvelope
    {
        public string EnvelopeID { get; set; } = Guid.NewGuid().ToString("N");
        public string ProjectID { get; set; } = "";
        public string HookID { get; set; } = "";
        public string SourceKind { get; set; } = "";
        /// <summary>Supersession key: for screen-class sources, a newer envelope with the same key
        /// replaces an undelivered older one. Empty = never superseded.</summary>
        public string SupersessionKey { get; set; } = "";
        public StimulusDurability Durability { get; set; } = StimulusDurability.Standard;
        /// <summary>Raw payload text (a message, a webhook body, a screen-change description, …).</summary>
        public string Payload { get; set; } = "";
        /// <summary>Optional artifact IDs (a clip, a screenshot) backing the payload.</summary>
        public List<string> ArtifactIDs { get; set; } = new();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        /// <summary>For superseding stimuli, the wall-clock after which even the latest is dropped undelivered.</summary>
        public DateTime? ExpiresAt { get; set; }
        /// <summary>Set after triage: the Stimulus Agent's one-line verdict delivered alongside the payload.</summary>
        public string? Verdict { get; set; }
        /// <summary>Agent this envelope is addressed to; stamped by the queue at enqueue time.</summary>
        public string DestinationAgentID { get; set; } = "commander";
    }
}
