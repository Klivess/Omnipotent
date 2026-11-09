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
