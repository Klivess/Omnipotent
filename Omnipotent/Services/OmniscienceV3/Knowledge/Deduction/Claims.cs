using System.Text.Json.Serialization;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Deduction
{
    // The extractor's structured output (prompt deduction.extract). Field names are what the model writes.

    public sealed class ExtractionAnswer
    {
        public List<RawClaim> Claims { get; set; } = new();
    }

    public sealed class RawEvidence
    {
        public string? Ref { get; set; }
        public string? Quote { get; set; }
    }

    public sealed class RawInterval
    {
        public string? Earliest { get; set; }
        public string? Latest { get; set; }
    }

    public sealed class RawTime
    {
        public string? Surface { get; set; }
        public string? Anchor { get; set; }
        public string? Precision { get; set; }
        public RawInterval? Occurred { get; set; }
        [JsonPropertyName("valid_from")] public RawInterval? ValidFrom { get; set; }
        [JsonPropertyName("valid_to")] public RawInterval? ValidTo { get; set; }
        [JsonPropertyName("is_current")] public bool? IsCurrent { get; set; }
    }

    public sealed class RawAge
    {
        public string? Form { get; set; }
        public int? Years { get; set; }
        public int? Month { get; set; }
        public int? Day { get; set; }
        public int? Year { get; set; }
        [JsonPropertyName("school_system")] public string? SchoolSystem { get; set; }
        [JsonPropertyName("school_level")] public int? SchoolLevel { get; set; }
        [JsonPropertyName("ahead_surface")] public string? AheadSurface { get; set; }
    }

    public sealed class RawPlan
    {
        public string? Kind { get; set; }
        public string? Subtype { get; set; }
        public string? Firmness { get; set; }
        public string? Promiser { get; set; }
        public string? Promisee { get; set; }
        [JsonPropertyName("status_change")] public string? StatusChange { get; set; }
        [JsonPropertyName("refers_to")] public string? RefersTo { get; set; }
        [JsonPropertyName("all_day")] public bool? AllDay { get; set; }
    }

    public sealed class RawEntity
    {
        public string? Kind { get; set; }
        public string? Name { get; set; }
        public Dictionary<string, string>? Descriptors { get; set; }
    }

    public sealed class RawRelation
    {
        public string? Type { get; set; }
        public string? Tone { get; set; }
    }

    public sealed class RawClaim
    {
        public string? Id { get; set; }
        public string? Subject { get; set; }
        public string? Attribute { get; set; }
        [JsonPropertyName("attribute_other")] public string? AttributeOther { get; set; }
        public string? Value { get; set; }
        public string? Object { get; set; }
        [JsonPropertyName("name_kind")] public string? NameKind { get; set; }
        public string? Polarity { get; set; }
        public string? Modality { get; set; }
        [JsonPropertyName("source_kind")] public string? SourceKind { get; set; }
        [JsonPropertyName("speech_act")] public string? SpeechAct { get; set; }
        public string? Sincerity { get; set; }
        public RawTime? Time { get; set; }
        public RawAge? Age { get; set; }
        public RawPlan? Plan { get; set; }
        public RawEntity? Entity { get; set; }
        public RawRelation? Relation { get; set; }
        public string? Corrects { get; set; }
        public List<RawEvidence> Evidence { get; set; } = new();
        public double? Probability { get; set; }
        public string? Rationale { get; set; }
    }

    public sealed class AdjudicationAnswer
    {
        public List<AdjudicationDecision> Decisions { get; set; } = new();
    }

    public sealed class AdjudicationDecision
    {
        public string? Id { get; set; }
        public bool Keep { get; set; }
        public RawClaim? Claim { get; set; }
        public string? Reason { get; set; }
    }

    public sealed class CriticAnswer
    {
        public List<CriticVerdict> Verdicts { get; set; } = new();
    }

    public sealed class CriticVerdict
    {
        public string? Id { get; set; }
        public string? Verdict { get; set; }
        public string? Reason { get; set; }
    }

    public static class CriticVerdicts
    {
        public const string Supported = "supported";
        public const string Unsupported = "unsupported";
        public const string Insufficient = "insufficient";
    }

    /// <summary>How a claim got through the two sampled passes.</summary>
    public static class Agreement
    {
        public const string Both = "both";
        public const string Adjudicated = "adjudicated";
        /// <summary>Only one pass ran (the other failed or was refused): no agreement signal.</summary>
        public const string Single = "single";
    }

    /// <summary>One verified piece of evidence a claim rests on.</summary>
    public sealed record ClaimEvidence(string Ref, string MessageId, int Version, string Quote, string? AuthorPersonId, long SentAtMs, bool Deleted,
        string ConversationId, string? GuildId, string Platform, string? ConversationKind, string SourceKind, string SourceId, string? AuthorZoneId);

    /// <summary>A claim after normalisation and verification: everything consolidation needs, resolved to persons and instants.</summary>
    public sealed record VerifiedClaim
    {
        public required string ClaimId { get; init; }
        public required string RunId { get; init; }
        public required int SliceIndex { get; init; }
        public required string SubjectPersonId { get; init; }
        public required string Attribute { get; init; }
        public required string Category { get; init; }
        public required string ValueText { get; init; }
        public required string ValueNorm { get; init; }
        public string? ObjectPersonId { get; init; }
        public string? NameKind { get; init; }
        public required string Polarity { get; init; }
        public required string Modality { get; init; }
        public required string SourceKind { get; init; }
        public string? SpeechAct { get; init; }
        public string? Sincerity { get; init; }
        public required double Probability { get; init; }
        public TimeInterval? Occurred { get; init; }
        public TimeInterval? ValidFrom { get; init; }
        public TimeInterval? ValidTo { get; init; }
        public bool? IsCurrent { get; init; }
        public string? TimeSurface { get; init; }
        public string? TimePrecision { get; init; }
        public string? TimeCheck { get; init; }
        public RawAge? Age { get; init; }
        public RawPlan? Plan { get; init; }
        public RawEntity? Entity { get; init; }
        public RawRelation? Relation { get; init; }
        public string? CorrectsMessageId { get; init; }
        public string? PlanRefersToMessageId { get; init; }
        public string? PromiserPersonId { get; init; }
        public string? PromiseePersonId { get; init; }
        public required IReadOnlyList<ClaimEvidence> Evidence { get; init; }
        public required string AgreementKind { get; init; }
        public string? Critic { get; init; }
        public string? Flags { get; init; }
        public required long AssertedAtMs { get; init; }
        public required string PromptVersion { get; init; }
        public string? Model { get; init; }
    }

    /// <summary>Why a claim was rejected (stored with the claim; the Quality Lab counts them per reason).</summary>
    public static class RejectReasons
    {
        public const string UnknownSubject = "unknown_subject";
        public const string NoEvidence = "no_evidence";
        public const string RefOutsideSlice = "ref_outside_slice";
        public const string QuoteNotFound = "quote_not_found";
        public const string SelfDisclosureAuthorMismatch = "self_disclosure_author_mismatch";
        public const string QuestionAsStatement = "question_as_statement";
        public const string NonLiteral = "non_literal";
        public const string Insincere = "insincere";
        public const string NegationDropped = "negation_dropped";
        public const string SensitiveInference = "sensitive_inference";
        public const string RestrictedForMinor = "restricted_for_minor";
        public const string NonAuthoredEvidence = "non_authored_evidence";
        public const string Fiction = "fiction";
        public const string CriticUnsupported = "critic_unsupported";
        public const string AdjudicatedOut = "adjudicated_out";
        public const string InvalidClaim = "invalid_claim";
        public const string GoldRejected = "gold_rejected";
    }
}
