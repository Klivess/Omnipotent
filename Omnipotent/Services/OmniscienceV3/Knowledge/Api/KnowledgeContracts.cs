using Omnipotent.Services.OmniscienceV3.Api;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Api
{
    // The knowledge workstream's HTTP contract. Instants are UTC epoch milliseconds; open interval ends are null.

    /// <summary>One fact or inference as the UI shows it. <see cref="Probability"/> is the confidence at the as-of (read-time decay);
    /// <see cref="StoredProbability"/> the calibrated evidence strength it decays from.</summary>
    [ApiContract]
    public sealed record KnowledgeFactDto(
        string AssertionId,
        string Attribute,
        string AttributeTitle,
        string Category,
        string Value,
        string Polarity,
        string? ObjectPersonId,
        string Status,
        double Probability,
        double StoredProbability,
        bool Calibrated,
        long? ValidFromEarliestMs,
        long? ValidFromLatestMs,
        long? ValidToEarliestMs,
        long? ValidToLatestMs,
        long? LastEvidenceMs,
        long? FirstEvidenceMs,
        long RecordedAtMs,
        string? SourceKind,
        string Sensitivity,
        string? Producer);

    [ApiContract]
    public sealed record KnowledgeCategoryDto(string Category, IReadOnlyList<KnowledgeFactDto> Facts);

    /// <summary>One life event (N7). Uncertain dates carry their interval; <see cref="MidpointMs"/> orders them.</summary>
    [ApiContract]
    public sealed record LifeEventDto(string EventId, string Category, string Title, string Attribute, string AssertionId, long? EarliestMs, long? LatestMs,
        long? MidpointMs, string Precision, double Probability, int EvidenceCount, IReadOnlyList<string> EvidenceMessageIds);

    /// <summary>The <c>knowledge.timeline</c> read model of one person.</summary>
    [ApiContract]
    public sealed record LifeTimelineDto(string PersonId, long AsOfMs, IReadOnlyList<LifeEventDto> Events, int TotalEvents);

    /// <summary>One plan, promise or important date (N8), with its status at the as-of and its next occurrence for recurring dates.</summary>
    [ApiContract]
    public sealed record PlanItemDto(
        string PlanId,
        string Kind,
        string? Subtype,
        string OwnerPersonId,
        string? CounterpartyPersonId,
        bool InvolvesKlives,
        bool KlivesPromised,
        string Title,
        long? ScheduledEarliestMs,
        long? ScheduledLatestMs,
        long? NextOccurrenceMs,
        string? LocalZone,
        bool AllDay,
        string Firmness,
        string Status,
        string? Recurrence,
        int EvidenceCount,
        IReadOnlyList<string> EvidenceMessageIds,
        string? ConversationId);

    /// <summary>The <c>knowledge.agenda</c> read model: what is coming up in Klives's zone, overdue promises, and his own promises.</summary>
    [ApiContract]
    public sealed record AgendaDto(long AsOfMs, string Zone, IReadOnlyList<PlanItemDto> Upcoming, IReadOnlyList<PlanItemDto> Overdue, IReadOnlyList<PlanItemDto> KlivesPromises);

    /// <summary>The <c>knowledge.profile</c> read model of one person.</summary>
    [ApiContract]
    public sealed record KnowledgeProfileDto(
        string PersonId,
        long AsOfMs,
        bool MinorRestricted,
        IReadOnlyList<KnowledgeCategoryDto> Categories,
        IReadOnlyList<KnowledgeFactDto> Inferences,
        int CoveredMessages,
        long? LastExtractMs,
        bool BacklogPending,
        string PromptVersion);
}
