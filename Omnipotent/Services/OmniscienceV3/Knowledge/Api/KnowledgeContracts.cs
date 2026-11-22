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
        string? ConversationId,
        // When it was first said (its earliest evidence): what a plan that names no date is shown and sorted by.
        long? SaidAtMs = null);

    /// <summary>The <c>knowledge.agenda</c> read model: what is coming up in Klives's zone, overdue promises, and his own promises.</summary>
    [ApiContract]
    public sealed record AgendaDto(long AsOfMs, string Zone, IReadOnlyList<PlanItemDto> Upcoming, IReadOnlyList<PlanItemDto> Overdue, IReadOnlyList<PlanItemDto> KlivesPromises);

    [ApiContract]
    public sealed record KnowledgeFactsPageDto(string PersonId, string Mode, long AtMs, IReadOnlyList<KnowledgeFactDto> Items, string? NextCursor);

    /// <summary>A cited message in context: who said it, where, when, and the quoted words of the cited version.</summary>
    [ApiContract]
    public sealed record EvidenceContextDto(string MessageId, int Version, string Role, string? Quote, double Weight, long? SentAtMs, string? AuthorPersonId, string? AuthorName,
        string? ConversationId, string? ConversationKind, string? ConversationTitle, string? GuildId, string? GuildName, string? Platform, bool Deleted, bool Edited, string? Text);

    [ApiContract]
    public sealed record AssertionVersionDto(string AssertionId, string Status, double? Probability, long RecordedAtMs, long? SupersededAtMs, long? RetractedAtMs, string? Value,
        long? ValidFromLatestMs, long? ValidToLatestMs, string? PromptVersion, string? Model);

    [ApiContract]
    public sealed record AssertionDetailDto(KnowledgeFactDto Fact, string NaturalKey, string? Modality, string? DerivationJson, string? DetailJson, IReadOnlyList<EvidenceContextDto> Evidence,
        IReadOnlyList<AssertionVersionDto> History, string? GoldVerdict, long? GoldDecidedAtMs);

    [ApiContract]
    public sealed record FactReviewRequest(string AssertionId, string Verdict, string? Value = null, long? ValidFromMs = null, long? ValidToMs = null, string? Note = null);

    [ApiContract]
    public sealed record KnowledgeJobStartedDto(long JobId, string? Detail = null);

    /// <summary>One item waiting for Klives's decision. <c>DetailJson</c> is what the module stored (it carries ids);
    /// <c>Context</c> is the same named, and is what a page shows.</summary>
    [ApiContract]
    public sealed record KnowledgeReviewItemDto(string ItemId, string Kind, string? SubjectId, string Title, string? DetailJson, double Priority, string State, long CreatedAtMs,
        long UpdatedAtMs, long? ResolvedAtMs, string? Resolution, string? Actor, ReviewContextDto? Context = null);

    /// <summary>
    /// What a review item points at, named, so a page shows people, places and what was said instead of the ids its detail
    /// carries. <c>People</c> names everyone it mentions (the subject included); <c>Messages</c> are the messages it rests on,
    /// in context. The rest is set for the kinds with a decision of their own: <c>Alias</c> (alias), <c>Fact</c> (contradiction,
    /// flapping, unproven, possible_change, rejected_resurfaced), <c>Age</c> (age_conflict), <c>Link</c> (identity_link).
    /// </summary>
    [ApiContract]
    public sealed record ReviewContextDto(IReadOnlyDictionary<string, string> People, IReadOnlyList<EvidenceContextDto> Messages, ReviewAliasDto? Alias = null, ReviewFactDto? Fact = null,
        IReadOnlyList<AgeStatementDto>? Age = null, ReviewLinkDto? Link = null);

    /// <summary>A name people use for someone, as it stands now: where it is used (<c>PlaceName</c>: the server or chat by name;
    /// <c>PlaceKind</c>: server, group or dm) and how often, by how many people.</summary>
    [ApiContract]
    public sealed record ReviewAliasDto(string AliasId, string Alias, string NameKind, string Status, string ScopeKind, string? ScopeId, string? PlaceName, string? PlaceKind,
        int Usages, int Speakers, int SelfClaims, bool CommonWord, long FirstSeenMs, long LastSeenMs);

    /// <summary>The fact a review item is about, as it stands now (<c>AssertionId</c> opens it with its evidence), and every value in question.</summary>
    [ApiContract]
    public sealed record ReviewFactDto(string Attribute, string AttributeTitle, string? AssertionId, string? Value, string? Status, string? ObjectPersonId, IReadOnlyList<string> Values);

    /// <summary>An account by what it is called: the platform, the name it shows now, its handle when that differs, and whose it is.</summary>
    [ApiContract]
    public sealed record ReviewAccountDto(string Platform, string? Name, string? Handle, string PersonId);

    /// <summary>Two accounts that may be one person, and where the suggestion stands.</summary>
    [ApiContract]
    public sealed record ReviewLinkDto(string LinkId, string Verdict, ReviewAccountDto A, ReviewAccountDto B, double Probability, int IndependentSignals, IReadOnlyList<LinkSignalDto> Signals,
        long? MergeId);

    [ApiContract]
    public sealed record KnowledgeReviewPageDto(long AsOfMs, IReadOnlyList<KnowledgeReviewItemDto> Items, string? NextCursor);

    [ApiContract]
    public sealed record ReviewResolveRequest(string ItemId, string State, string Resolution);

    [ApiContract]
    public sealed record ClaimDto(string ClaimId, string RunId, string PersonId, string Attribute, string Category, string? Value, string? Modality, string? SourceKind, double? Probability,
        string Agreement, string Verdict, string? Reason, string? Critic, long AssertedAtMs, long RecordedAtMs, string PromptVersion, string? Model, string? ConversationId);

    [ApiContract]
    public sealed record ClaimPageDto(IReadOnlyList<ClaimDto> Items, string? NextCursor);

    [ApiContract]
    public sealed record ExtractionRunDto(string RunId, string PersonId, long? JobId, string PromptVersion, string? Model, int Slices, int Messages, int ClaimsRaw, int ClaimsAgreed,
        int ClaimsVerified, int ClaimsRejected, string? RejectionsJson, int LlmCalls, string Outcome, long StartedAtMs, long? FinishedAtMs);

    [ApiContract]
    public sealed record ExtractionRunPageDto(IReadOnlyList<ExtractionRunDto> Items, string? NextCursor);

    [ApiContract]
    public sealed record PersonRequest(string PersonId);

    [ApiContract]
    public sealed record AgeStatementDto(string EvidenceId, string Kind, string Status, string SourceKind, long AssertedAtMs, string MessageId, double Probability, string? Quote);

    /// <summary>A person's MinorGuard band and the statements behind it (so a misread can be rejected; no birth date is shown).</summary>
    [ApiContract]
    public sealed record AgeBandDto(string PersonId, string Band, bool Restricted, bool MarkedMinorByKlives, long AtMs, IReadOnlyList<AgeStatementDto> Statements);

    [ApiContract]
    public sealed record AgeReviewRequest(string EvidenceId, string Verdict, string? Note = null);

    [ApiContract]
    public sealed record AliasDto(string AliasId, string PersonId, string Alias, string NameKind, string ScopeKind, string? ScopeId, long ValidFromMs, long? ValidToMs, int Usages, int Speakers,
        int SelfClaims, double Probability, string Status, bool CommonWord, bool AlertEligible);

    [ApiContract]
    public sealed record AliasReviewRequest(string AliasId, string Verdict, string? Note = null);

    [ApiContract]
    public sealed record EntityDto(string EntityId, string OwnerPersonId, string Kind, string Name, string AttributesJson, long FirstEvidenceMs, long LastEvidenceMs, int Mentions, string Status);

    [ApiContract]
    public sealed record HypothesisDto(string HypothesisId, string PersonId, string Statement, string? Attribute, double Probability, string Status, long CreatedAtMs, long ExpiresAtMs,
        long? ResolvedAtMs, string? ResolutionNote, string DerivedFromJson);

    [ApiContract]
    public sealed record LinkSignalDto(string Kind, double Strength, string Detail);

    /// <summary>Two accounts that may be one person. <c>AccountA</c>/<c>AccountB</c> say what the accounts are called (the
    /// identity ids are for requests, never for reading).</summary>
    [ApiContract]
    public sealed record IdentityLinkDto(string LinkId, string IdentityA, string IdentityB, string PersonA, string PersonB, IReadOnlyList<LinkSignalDto> Signals, int IndependentSignals,
        double Probability, string Verdict, string? JudgeJson, long CreatedAtMs, long UpdatedAtMs, long? DecidedAtMs, long? MergeId, ReviewAccountDto? AccountA = null,
        ReviewAccountDto? AccountB = null);

    [ApiContract]
    public sealed record IdentityLinkPageDto(IReadOnlyList<IdentityLinkDto> Items, string? NextCursor);

    /// <summary>Accepting a link merges two people: <see cref="Confirm"/> must be true (an explicit confirmation, F122).</summary>
    [ApiContract]
    public sealed record IdentityLinkDecisionRequest(string LinkId, bool Accept, bool Confirm = false, string? Note = null);

    [ApiContract]
    public sealed record MergeResultDto(long MergeId, string Survivor, string Absorbed, bool Changed, string Rule);

    [ApiContract]
    public sealed record IdentityLinkDecisionDto(IdentityLinkDto Link, MergeResultDto? Merge);

    [ApiContract]
    public sealed record LinkActionRequest(string LinkId);

    [ApiContract]
    public sealed record MergeRequest(string PersonA, string PersonB, bool Confirm = false, string? Reason = null);

    [ApiContract]
    public sealed record UnmergeRequest(long MergeId);

    [ApiContract]
    /// <summary>One merge of two people. <c>FromName</c>/<c>IntoName</c> are each person's own name (a person merged into
    /// another is otherwise known by the name of the one it joined, so both sides would read the same).</summary>
    public sealed record MergeRowDto(long MergeId, string FromPersonId, string IntoPersonId, long MergedAtMs, string Actor, string? Reason, long? UndoneAtMs, string? FromName = null,
        string? IntoName = null);

    [ApiContract]
    public sealed record PlanPageDto(IReadOnlyList<PlanItemDto> Items, string? NextCursor);

    [ApiContract]
    public sealed record InquiryRowDto(string InquiryId, string RunId, string TacticKey, string Question, string? Restatement, string AnswerType, string? PrimarySubjectId, long AsOfMs,
        string? Verdict, double? Probability, string State, string? Feedback, string? StandingId, long CreatedAtMs);

    [ApiContract]
    public sealed record InquiryPageDto(IReadOnlyList<InquiryRowDto> Items, string? NextCursor);

    [ApiContract]
    public sealed record InquiryFeedbackRequest(string InquiryId, string Feedback, string? Note = null);

    [ApiContract]
    public sealed record AskRequest(string Question, IReadOnlyList<string>? PersonIds = null, string? Tactic = null, long? AsOfMs = null, bool Standing = false);

    [ApiContract]
    public sealed record AskStartedDto(string RunId, string Tactic);

    [ApiContract]
    public sealed record StandingQuestionDto(string QuestionId, string Question, IReadOnlyList<string> PersonIds, bool Enabled, long MinIntervalMs, long? LastEvaluatedMs, string? LastVerdict,
        double? LastProbability, bool Pending, long CreatedAtMs);

    [ApiContract]
    public sealed record StandingEnabledRequest(string QuestionId, bool Enabled);

    [ApiContract]
    public sealed record QualityRunRequest(string Kind);

    /// <summary>The <c>knowledge.profile</c> read model of one person.</summary>
    [ApiContract]
    public sealed record KnowledgeProfileDto(
        string PersonId,
        long AsOfMs,
        IReadOnlyList<KnowledgeCategoryDto> Categories,
        IReadOnlyList<KnowledgeFactDto> Inferences,
        int CoveredMessages,
        long? LastExtractMs,
        bool BacklogPending,
        string PromptVersion);
}
