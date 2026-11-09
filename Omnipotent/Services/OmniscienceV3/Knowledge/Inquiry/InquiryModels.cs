using System.Text.Json.Serialization;
using Omnipotent.Services.OmniscienceV3.Api;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Inquiry
{
    /// <summary>What kind of answer a question wants (plan X-inquiry "answer type" and variants).</summary>
    [ApiContract]
    public enum InquiryAnswerType { Binary, Choice, Ranking, Numeric, DateInterval, Open, ChangePoint, Prediction }

    /// <summary>The verdict bands of a binary inquiry (plan: Yes / Leans yes / Uncertain / Leans no / No / Insufficient evidence).</summary>
    [ApiContract]
    public enum InquiryVerdict { Yes, LeansYes, Uncertain, LeansNo, No, InsufficientEvidence, Refused, NeedsDisambiguation, Answered }

    // ── model outputs (prompt-facing shapes) ─────────────────────────────────────

    public sealed class UnderstandAnswer
    {
        public string? Restatement { get; set; }
        [JsonPropertyName("answer_type")] public string? AnswerType { get; set; }
        public List<string> Options { get; set; } = new();
        public List<UnderstandSubject> Subjects { get; set; } = new();
        [JsonPropertyName("time_scope")] public string? TimeScope { get; set; }
        [JsonPropertyName("scope_from")] public string? ScopeFrom { get; set; }
        [JsonPropertyName("scope_to")] public string? ScopeTo { get; set; }
        public List<string> Conditions { get; set; } = new();
        [JsonPropertyName("sensitive_category")] public string? SensitiveCategory { get; set; }
        [JsonPropertyName("restricted_attribute")] public string? RestrictedAttribute { get; set; }
        [JsonPropertyName("about_klives")] public bool AboutKlives { get; set; }
        [JsonPropertyName("needs_world_knowledge")] public bool NeedsWorldKnowledge { get; set; }
        [JsonPropertyName("routine_inference")] public bool RoutineInference { get; set; }
        public string? Topic { get; set; }
    }

    public sealed class UnderstandSubject
    {
        public string? Mention { get; set; }
        [JsonPropertyName("person_id")] public string? PersonId { get; set; }
    }

    public sealed class PlanAnswer
    {
        public List<FacetPlan> Facets { get; set; } = new();
        [JsonPropertyName("base_rate")] public BaseRate? BaseRate { get; set; }
    }

    public sealed class FacetPlan
    {
        public string? Name { get; set; }
        public string? Kind { get; set; }
        public List<string> Queries { get; set; } = new();
        [JsonPropertyName("counter_queries")] public List<string> CounterQueries { get; set; } = new();
    }

    public sealed class BaseRate
    {
        public double? Probability { get; set; }
        public string? Rationale { get; set; }
    }

    public sealed class PassAnswer
    {
        public string? Answer { get; set; }
        public double? Probability { get; set; }
        public Dictionary<string, double>? Distribution { get; set; }
        public double? Estimate { get; set; }
        public double? Low { get; set; }
        public double? High { get; set; }
        [JsonPropertyName("change_from")] public string? ChangeFrom { get; set; }
        [JsonPropertyName("change_to")] public string? ChangeTo { get; set; }
        public Dictionary<string, double>? Scores { get; set; }
        [JsonPropertyName("evidence_for")] public List<string> EvidenceFor { get; set; } = new();
        [JsonPropertyName("evidence_against")] public List<string> EvidenceAgainst { get; set; } = new();
        public string? Reasoning { get; set; }
        [JsonPropertyName("temporal_note")] public string? TemporalNote { get; set; }
        [JsonPropertyName("would_change")] public List<string> WouldChange { get; set; } = new();
        [JsonPropertyName("questions_to_ask")] public List<string> QuestionsToAsk { get; set; } = new();
        [JsonPropertyName("used_world_knowledge")] public bool UsedWorldKnowledge { get; set; }
        [JsonPropertyName("excluded_as_joke")] public List<string> ExcludedAsJoke { get; set; } = new();
    }

    public sealed class AdvocateAnswer
    {
        public string? Argument { get; set; }
        public List<string> Cites { get; set; } = new();
        [JsonPropertyName("strength")] public string? Strength { get; set; }
    }

    public sealed class CitationVerdicts
    {
        public List<CitationVerdict> Checks { get; set; } = new();
    }

    public sealed class CitationVerdict
    {
        public string? Id { get; set; }
        public string? Point { get; set; }
        public bool Supports { get; set; }
        public string? Reason { get; set; }
    }

    // ── engine state (stage results; JSON round-trippable) ───────────────────────

    public sealed record InquirySubjectRef(string PersonId, string Label, bool IsKlives);

    /// <summary>The structured inquiry (stage "understand").</summary>
    public sealed record InquirySpec
    {
        public required string Question { get; init; }
        public required string Restatement { get; init; }
        public required InquiryAnswerType AnswerType { get; init; }
        public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();
        public required IReadOnlyList<InquirySubjectRef> Subjects { get; init; }
        public required long AsOfMs { get; init; }
        public long? FromMs { get; init; }
        public string? TimeScope { get; init; }
        public IReadOnlyList<string> Conditions { get; init; } = Array.Empty<string>();
        public string? SensitiveCategory { get; init; }
        public string? RestrictedAttribute { get; init; }
        public bool RoutineInference { get; init; }
        public bool AboutKlives { get; init; }
        public bool NeedsWorldKnowledge { get; init; }
        public string? Topic { get; init; }
        /// <summary>Names that matched several people (the run stops and asks which one).</summary>
        public IReadOnlyList<DisambiguationDto> Ambiguous { get; init; } = Array.Empty<DisambiguationDto>();
        /// <summary>Inactive subjects are answered as of their last activity.</summary>
        public string? AsOfNote { get; init; }
    }

    public sealed record Facet(string Name, string Kind, IReadOnlyList<string> Queries, IReadOnlyList<string> CounterQueries);

    public sealed record InquiryPlan(IReadOnlyList<Facet> Facets, double? PriorProbability, string? PriorRationale);

    /// <summary>One numbered piece of evidence in the dossier.</summary>
    public sealed record EvidenceItem(
        string Id,
        string Kind,
        string? MessageId,
        string? AssertionId,
        string? SubjectPersonId,
        string? AuthorPersonId,
        long AtMs,
        string Text,
        string Facet,
        string Reliability,
        double Score,
        bool Counter,
        string? ConversationId,
        string? GuildId,
        string? Platform,
        string? Where);

    public sealed record GatherResult(IReadOnlyList<EvidenceItem> Items, CoverageDto Coverage);

    public sealed record PassResult(int Pass, PassAnswer Answer, string? Model, bool Devil = false);

    // ── report (API contract) ────────────────────────────────────────────────────

    [ApiContract]
    public sealed record DisambiguationDto(string Mention, IReadOnlyList<PersonCandidateDto> Candidates);

    [ApiContract]
    public sealed record PersonCandidateDto(string PersonId, string Label, string? Detail);

    /// <summary>One piece of evidence for or against, linked into its context.</summary>
    [ApiContract]
    public sealed record InquiryEvidenceDto(string Id, string Kind, string? MessageId, string? AssertionId, string? AuthorPersonId, long AtMs, string Quote,
        string? ConversationId, string? GuildId, string? Platform, string? Where, string Reliability, string Facet);

    /// <summary>What was searched (plan "coverage": sources and time ranges, what was excluded and why).</summary>
    [ApiContract]
    public sealed record CoverageDto(
        IReadOnlyList<string> Subjects,
        long? FromMs,
        long ToMs,
        int MessagesConsidered,
        int FactsConsidered,
        int ItemsInDossier,
        int ExcludedAsJokeOrFiction,
        int ExcludedAfterAsOf,
        bool VectorSearch,
        IReadOnlyList<string> Platforms,
        IReadOnlyList<string> Notes);

    [ApiContract]
    public sealed record ChoiceDto(string Option, double Probability);

    [ApiContract]
    public sealed record RankedPersonDto(string PersonId, string Label, double Score, string? Reason, IReadOnlyList<string> EvidenceIds);

    /// <summary>The inquiry report (plan X-inquiry step 8).</summary>
    [ApiContract]
    public sealed record InquiryReportDto(
        string InquiryId,
        string RunId,
        string Question,
        string Restatement,
        InquiryAnswerType AnswerType,
        InquiryVerdict Verdict,
        double? Probability,
        double? ProbabilityRaw,
        double? BandLow,
        double? BandHigh,
        bool Calibrated,
        IReadOnlyList<ChoiceDto> Distribution,
        IReadOnlyList<RankedPersonDto> Ranking,
        double? Estimate,
        long? ChangeFromMs,
        long? ChangeToMs,
        IReadOnlyList<InquiryEvidenceDto> EvidenceFor,
        IReadOnlyList<InquiryEvidenceDto> EvidenceAgainst,
        string? Reasoning,
        string? TemporalNote,
        IReadOnlyList<string> WouldChange,
        IReadOnlyList<string> QuestionsToAsk,
        CoverageDto Coverage,
        int EvidenceStrength,
        int Passes,
        int PassesDropped,
        double Disagreement,
        string? DevilsAdvocate,
        double? PriorProbability,
        string? PriorRationale,
        bool WorldKnowledgeDependent,
        bool IsPrediction,
        string? PolicyNote,
        IReadOnlyList<DisambiguationDto> Disambiguation,
        long AsOfMs,
        string? AsOfNote,
        string PromptVersion,
        string? Model,
        string? AssertionId,
        string? StandingId);
}
