using Newtonsoft.Json;

namespace Omnipotent.Services.Omniscience.Replica
{
    /// <summary>
    /// Replica training/runtime status. Persisted in `replicas.status`.
    /// </summary>
    public static class ReplicaStatus
    {
        public const string None = "none";
        public const string Training = "training";
        public const string Ready = "ready";
        public const string Failed = "failed";
        public const string Stale = "stale";
    }

    /// <summary>Stage names persisted into <c>replica_training_jobs.stage</c>.</summary>
    public static class ReplicaStage
    {
        public const string Voice = "voice";
        public const string Opinions = "opinions";
        public const string Reflexes = "reflexes";
        public const string Stylometric = "stylometric";
        public const string Relational = "relational";
        public const string Forbidden = "forbidden";
        public const string Embedding = "embedding";
        public const string Done = "done";
    }

    /// <summary>
    /// Progress payload reported by <see cref="ReplicaTrainer"/> so the UI can render
    /// stage-by-stage progress without polling the SQLite job row mid-stage.
    /// </summary>
    public class ReplicaJobProgress
    {
        public long JobId { get; set; }
        public string PersonId { get; set; } = string.Empty;
        public string Stage { get; set; } = string.Empty;
        public int ProgressPct { get; set; }
        public string? Message { get; set; }
    }

    /// <summary>
    /// Structured persona dossier produced by <see cref="ReplicaTrainer"/>. Persisted as
    /// JSON in <c>replicas.dossier_json</c> and re-read by <c>ReplicaChatOrchestrator</c>
    /// to rebuild the system prompt at chat time.
    /// </summary>
    public class ReplicaDossier
    {
        [JsonProperty("display_name")] public string DisplayName { get; set; } = string.Empty;
        [JsonProperty("handles")] public List<string> Handles { get; set; } = new();
        [JsonProperty("voice_rulebook")] public string VoiceRulebookMarkdown { get; set; } = string.Empty;
        [JsonProperty("opinion_ledger")] public string OpinionLedgerMarkdown { get; set; } = string.Empty;
        [JsonProperty("relational_map")] public string RelationalMapMarkdown { get; set; } = string.Empty;
        [JsonProperty("forbidden_patterns")] public List<string> ForbiddenPatterns { get; set; } = new();
        [JsonProperty("reflex_examples")] public Dictionary<string, List<ReplicaExemplar>> ReflexExamples { get; set; } = new();
        [JsonProperty("stats")] public ReplicaCorpusStats Stats { get; set; } = new();
    }

    /// <summary>
    /// One worked example of how the person actually replied to a stimulus message.
    /// At chat time we feed these to the LLM as alternating user/assistant message
    /// pairs so it can mimic the conditional distribution.
    /// </summary>
    public class ReplicaExemplar
    {
        [JsonProperty("stimulus")] public string? Stimulus { get; set; }
        [JsonProperty("reply")] public string Reply { get; set; } = string.Empty;
        [JsonProperty("conversation_id")] public string? ConversationId { get; set; }
        [JsonProperty("sent_at")] public long SentAt { get; set; }
        [JsonProperty("score")] public float Score { get; set; }
    }

    /// <summary>Summary of the corpus the replica was trained on. Surfaced in the UI.</summary>
    public class ReplicaCorpusStats
    {
        [JsonProperty("total_messages")] public int TotalMessages { get; set; }
        [JsonProperty("messages_used_for_voice")] public int MessagesUsedForVoice { get; set; }
        [JsonProperty("messages_used_for_opinions")] public int MessagesUsedForOpinions { get; set; }
        [JsonProperty("avg_message_length")] public double AverageMessageLength { get; set; }
        [JsonProperty("median_message_length")] public int MedianMessageLength { get; set; }
        [JsonProperty("first_message_at")] public long? FirstMessageAt { get; set; }
        [JsonProperty("last_message_at")] public long? LastMessageAt { get; set; }
    }
}
