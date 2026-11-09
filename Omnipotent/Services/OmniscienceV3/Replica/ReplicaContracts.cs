using Omnipotent.Services.OmniscienceV3.Api;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    // The replica's HTTP contract (WS-D). Instants are UTC epoch milliseconds, durations milliseconds (T1, F559).
    // Every replica message is a simulation: the UI labels it as such (Guardrails); replicas never post anywhere.

    /// <summary>Whether a person can have a replica, from data sufficiency and MinorGuard (F015).</summary>
    [ApiContract]
    public enum ReplicaTier { Ready, Experimental, NotPossible, Blocked }

    /// <summary>Readiness with the numbers behind it (plan "Readiness": ≥300 replies ready, 100–300 experimental, fewer not possible).</summary>
    [ApiContract]
    public sealed record ReplicaReadinessDto(
        string PersonId,
        string Name,
        ReplicaTier Tier,
        string Mode,
        string Reason,
        int Replies,
        int Initiations,
        int Effective,
        int ExplicitReplies,
        int RepliesToKlives,
        int AuthoredMessages,
        int NonAuthored,
        int Deleted,
        int MinorPeriodExcluded,
        IReadOnlyDictionary<string, int> ByContext,
        long? FirstMs,
        long? LastMs,
        int Conversations,
        string AgeBand,
        long? AdultFromMs,
        int ThresholdExperimental,
        int ThresholdReady,
        bool IsSelf,
        bool IsBot,
        bool Truncated,
        long ComputedAtMs,
        long SourceSeq);

    [ApiContract]
    public sealed record ReplicaStageDto(string Stage, string State, int Attempts, long? StartedAtMs, long? FinishedAtMs, string? Detail, string? Error, int Total, int Completed, int Failed);

    [ApiContract]
    public sealed record ReplicaCountsDto(int Pairs, int Replies, int Initiations, int Train, int HoldoutTune, int HoldoutTest, int Shadow, int Eras, int Slices,
        int PersonaItems, int PersonaDropped, int ReflexLabelled, int Vectors);

    [ApiContract]
    public sealed record ReplicaTuneResultDto(string ConfigKey, double Temperature, int Retrieved, bool BurstSampling, int Pairs, double? DiscriminationAccuracy,
        double? EmbeddingSimilarity, double? StyleDistance, bool Chosen);

    /// <summary>Fidelity on held-out real replies (plan "Evaluation"): judge discrimination accuracy (50% = indistinguishable,
    /// with a 95% Wilson interval), embedding similarity, style distance, length and burst-size error.</summary>
    [ApiContract]
    public sealed record ReplicaMetricsDto(
        int Evaluated,
        int Failed,
        double? DiscriminationAccuracy,
        double? DiscriminationCiLow,
        double? DiscriminationCiHigh,
        double? EmbeddingSimilarity,
        double? StyleDistance,
        double? LengthError,
        double? BurstError,
        string? ConfigKey,
        IReadOnlyList<ReplicaTuneResultDto> Tuning,
        IReadOnlyDictionary<string, double> DiscriminationByContext,
        string? Model,
        long? EvaluatedAtMs,
        string Note);

    [ApiContract]
    public sealed record ReplicaVersionSummaryDto(
        string VersionId,
        int Number,
        string State,
        string Reason,
        long CreatedAtMs,
        long? FinishedAtMs,
        long? DataFromMs,
        long? DataToMs,
        string? EncoderModel,
        IReadOnlyList<ReplicaStageDto> Stages,
        ReplicaMetricsDto? Metrics,
        IReadOnlyList<string> Degraded,
        ReplicaCountsDto? Counts,
        string? Error);

    [ApiContract]
    public sealed record ReplicaFidelityPointDto(string VersionId, int Number, long FinishedAtMs, double? DiscriminationAccuracy, double? EmbeddingSimilarity, double? StyleDistance, int Evaluated);

    /// <summary>The replica card of one person (read model <c>replica.person</c>).</summary>
    [ApiContract]
    public sealed record ReplicaPersonDto(
        string PersonId,
        string Name,
        string Status,
        string? StatusDetail,
        bool Stale,
        string? StaleReason,
        long? StaleSinceMs,
        int NewMessagesSinceServing,
        bool AutoRetrain,
        ReplicaReadinessDto? Readiness,
        ReplicaVersionSummaryDto? Serving,
        ReplicaVersionSummaryDto? Building,
        IReadOnlyList<ReplicaFidelityPointDto> FidelityTrend,
        long UpdatedAtMs);

    [ApiContract]
    public sealed record ReplicaPersonPageDto(IReadOnlyList<ReplicaPersonDto> Items, string? NextCursor);

    [ApiContract]
    public sealed record ReplicaEraDto(int Index, long FromMs, long ToMs, int Pairs, IReadOnlyDictionary<string, double> Means);

    [ApiContract]
    public sealed record ReplicaStyleDto(int Bursts, long FromMs, long ToMs, double CharsP50, double CharsP90, IReadOnlyList<double> BurstShares, double? LatencyP50Ms,
        IReadOnlyDictionary<string, double> Casing, IReadOnlyDictionary<string, double> Endings, double EmojiLines, IReadOnlyList<string> TopEmoji,
        IReadOnlyList<string> Laughter, IReadOnlyList<string> Distinctive, IReadOnlyDictionary<string, double> Scripts, IReadOnlyList<int> PeakLocalHours);

    [ApiContract]
    public sealed record ReplicaVersionDto(ReplicaVersionSummaryDto Summary, string PersonId, int ModuleVersion, string RequestedBy, IReadOnlyList<ReplicaEraDto> Eras,
        ReplicaStyleDto? Style, GenerationConfig? Config);

    [ApiContract]
    public sealed record ReplicaVersionListDto(IReadOnlyList<ReplicaVersionSummaryDto> Versions);

    [ApiContract]
    public sealed record ReplicaEvalItemDto(string PairId, string ConfigKey, string Split, string ContextKind, long OccurredMs, long AsOfMs, string? Stimulus, string? RealReply,
        string? GeneratedReply, string Outcome, bool? JudgeCorrect, double? JudgeConfidence, double? EmbeddingSimilarity, double? StyleDistance, double? LengthError, string? Model);

    [ApiContract]
    public sealed record ReplicaEvalPageDto(IReadOnlyList<ReplicaEvalItemDto> Items, string? NextCursor, bool TextsIncluded);

    [ApiContract]
    public sealed record ReplicaTrainRequest(string PersonId, string? Reason = null);

    [ApiContract]
    public sealed record ReplicaTrainResultDto(string PersonId, string VersionId, int Number, long JobId, bool AlreadyRunning, ReplicaReadinessDto? Readiness);

    [ApiContract]
    public sealed record ReplicaPersonRequest(string PersonId);

    [ApiContract]
    public sealed record ReplicaAutoRetrainRequest(string PersonId, bool Enabled);

    [ApiContract]
    public sealed record ReplicaJobStartedDto(string PersonId, long JobId);

    // ── chats ────────────────────────────────────────────────────────────────

    [ApiContract]
    public sealed record ReplicaChatDto(string ChatId, string Kind, string PersonId, string? Title, long? AsOfMs, string ContextKind, string? AddresseePersonId, bool Archived,
        long CreatedAtMs, long UpdatedAtMs, IReadOnlyList<string> Members, string? Topic);

    [ApiContract]
    public sealed record ReplicaChatPageDto(IReadOnlyList<ReplicaChatDto> Items, string? NextCursor);

    /// <summary>One chat line. <see cref="Simulated"/> is true for every replica-written line (the UI labels it a simulation).</summary>
    [ApiContract]
    public sealed record ReplicaChatMessageDto(string ChatId, int Seq, string Role, string Content, long AtMs, string? PersonId, string? Speaker, string? TurnId, string? Status,
        string? Error, bool Simulated, string? VersionId, string? GenerationId, int? BurstIndex, int? BurstSize);

    [ApiContract]
    public sealed record ReplicaChatMessagePageDto(IReadOnlyList<ReplicaChatMessageDto> Items, int? NextCursor);

    [ApiContract]
    public sealed record ReplicaChatCreateRequest(string PersonId, string? Title = null, long? AsOfMs = null, string? ContextKind = null, string? AddresseePersonId = null);

    [ApiContract]
    public sealed record ReplicaChatUpdateRequest(string ChatId, string? Title = null, bool? Archived = null);

    [ApiContract]
    public sealed record ReplicaChatSendRequest(string ChatId, string Content);

    [ApiContract]
    public sealed record ReplicaChatActionRequest(string ChatId);

    [ApiContract]
    public sealed record ReplicaChatRateRequest(string ChatId, int Seq, int Rating, string? Note = null);

    [ApiContract]
    public sealed record ReplicaActionResultDto(bool Changed, string? Detail);

    /// <summary>
    /// One NDJSON line of a streamed replica turn: <c>accepted</c>, <c>stage</c> (composing, generating i/n, judging),
    /// <c>message</c> (one line of the reply burst), <c>done</c>, <c>failed</c>, <c>cancelled</c>, <c>heartbeat</c>.
    /// </summary>
    [ApiContract]
    public sealed record ReplicaStreamEventDto(string Type, string ChatId, string? TurnId, string? Stage = null, int? Index = null, int? Of = null,
        ReplicaChatMessageDto? Message = null, string? Code = null, string? Text = null);

    // ── group simulation (N6) ────────────────────────────────────────────────

    [ApiContract]
    public sealed record ReplicaGroupCreateRequest(IReadOnlyList<string> PersonIds, string Topic, string? ConversationId = null, int Turns = 8);

    [ApiContract]
    public sealed record ReplicaGroupAdvanceRequest(string ChatId, int Turns = 4);

    [ApiContract]
    public sealed record ReplicaGroupSendRequest(string ChatId, string Content, int Turns = 3);

    // ── blind test ───────────────────────────────────────────────────────────

    [ApiContract]
    public sealed record ReplicaBlindStartRequest(string PersonId, int Count = 10);

    [ApiContract]
    public sealed record ReplicaBlindItemDto(int Index, string Stimulus, string OptionA, string OptionB, string? Answered, bool? Correct);

    [ApiContract]
    public sealed record ReplicaBlindSessionDto(string SessionId, string PersonId, string VersionId, long CreatedAtMs, IReadOnlyList<ReplicaBlindItemDto> Items,
        int Answered, int Correct, double? Accuracy, long? FinishedAtMs);

    [ApiContract]
    public sealed record ReplicaBlindAnswerRequest(string SessionId, int Index, string Choice);

    [ApiContract]
    public sealed record ReplicaBlindAnswerDto(int Index, string Choice, string Real, bool Correct, int Answered, int CorrectTotal);

    [ApiContract]
    public sealed record ReplicaBlindResultsDto(string PersonId, int Sessions, int Answered, int Correct, double? Accuracy, IReadOnlyList<ReplicaBlindSessionDto> Recent);

    // ── module status ────────────────────────────────────────────────────────

    [ApiContract]
    public sealed record ReplicaHouseModelDto(string ModelId, int Version, string State, long? TrainedAtMs, AssistantVoiceMetrics? Metrics, string? Detail);

    [ApiContract]
    public sealed record ReplicaModuleStatusDto(string? EncoderModel, bool EncoderAvailable, ReplicaHouseModelDto? AssistantVoice, int Replicas, int Ready, int Building,
        int Failed, int Blocked, int Stale, long NowMs);
}
