using System.Text.Json;
using System.Text.Json.Serialization;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Store
{
    /// <summary>Epistemic status of an assertion (D2 provenance "status"). Record time (superseded, retracted) is separate.</summary>
    public static class AssertionStatus
    {
        /// <summary>Extracted and verified, not yet through the critic.</summary>
        public const string Proposed = "proposed";
        /// <summary>Verified, consolidated and supported by the critic: shown as a fact.</summary>
        public const string Accepted = "accepted";
        /// <summary>Klives confirmed it (gold). Never overwritten by new evidence; contrary evidence raises a review item.</summary>
        public const string Confirmed = "confirmed";
        /// <summary>Klives rejected it (gold). Kept for as-known-at and calibration; never shown as current.</summary>
        public const string Rejected = "rejected";
        /// <summary>The critic found it insufficient, or a conflict awaits review: kept, flagged, not shown as settled.</summary>
        public const string Contested = "contested";
        /// <summary>A second-order inference or an inquiry answer: always labelled, never a fact, never evidence.</summary>
        public const string Inferred = "inferred";

        public static bool IsShownAsKnowledge(string status) => status is Accepted or Confirmed or Contested or Inferred;
    }

    /// <summary>How something was said (plan "modality"). Only <see cref="Literal"/> statements become facts.</summary>
    public static class Modality
    {
        public const string Literal = "literal";
        public const string Joke = "joke";
        public const string Sarcasm = "sarcasm";
        public const string Hypothetical = "hypothetical";
        public const string Question = "question";
        public const string Negated = "negated";
        public const string Quoted = "quoted";
        public const string Roleplay = "roleplay";
        public const string LyricsCopypasta = "lyrics_copypasta";

        public static readonly IReadOnlyList<string> All = new[] { Literal, Joke, Sarcasm, Hypothetical, Question, Negated, Quoted, Roleplay, LyricsCopypasta };

        public static bool IsKnown(string? m) => m != null && All.Contains(m);
    }

    /// <summary>Where a claim comes from: the subject about themselves, someone else, or an inference.</summary>
    public static class ClaimSource
    {
        public const string SelfDisclosure = "self_disclosure";
        public const string ThirdParty = "third_party";
        public const string Inferred = "inferred";
        /// <summary>Observed behaviour (what they share, play, react to) rather than a statement.</summary>
        public const string Behaviour = "behaviour";
        /// <summary>Klives entered or corrected it himself.</summary>
        public const string Klives = "klives";

        public static readonly IReadOnlyList<string> All = new[] { SelfDisclosure, ThirdParty, Inferred, Behaviour, Klives };
    }

    /// <summary>Which pipeline produced an assertion. Each producer reconciles only its own rows for a subject.</summary>
    public static class Producers
    {
        public const string Extract = "extract";
        public const string Detective = "detective";
        public const string Inquiry = "inquiry";
        public const string Manual = "manual";
    }

    public static class Polarity
    {
        public const string Affirmed = "affirmed";
        public const string Negated = "negated";
    }

    /// <summary>The three bitemporal query modes (plan rule T2), all through one API.</summary>
    public enum KnowledgeMode
    {
        /// <summary>Valid at the instant, best known now.</summary>
        Current,
        /// <summary>What was true in the world at the instant, as Omniscience knows it now.</summary>
        AsOfWorld,
        /// <summary>What Omniscience believed at the instant (audits, briefing reproduction, evaluation).</summary>
        AsKnownAt,
    }

    /// <summary>The stored value of an assertion (value_json).</summary>
    public sealed record AssertionValue(
        string Text,
        string Normalized,
        string Polarity = Store.Polarity.Affirmed,
        JsonElement? Detail = null);

    /// <summary>One piece of evidence an assertion cites: the exact message version and the verbatim quote.</summary>
    public sealed record EvidenceRef(string MessageId, int MessageVersion, string Role, string? Quote, double Weight, long? SentAtMs = null);

    public static class EvidenceRoles
    {
        public const string Support = "support";
        public const string Contra = "contra";
        public const string Context = "context";
        /// <summary>The author deleted the cited message: it still counts, labelled and at reduced weight (plan T5).</summary>
        public const string Deleted = "deleted";
    }

    /// <summary>The D2 dimensions of an assertion, stored beside it so every fact is filterable by time, person and place.</summary>
    public sealed record AssertionDims(
        long OccurredAtMs,
        long? FirstEvidenceAtMs,
        string? AuthorPersonId,
        string? Platform,
        string? SourceId,
        string? GuildId,
        string? ConversationId,
        string? ConversationKind,
        int EvidenceCount,
        int IndependentCount,
        string? RunId = null);

    /// <summary>What a producer wants to be true for one natural key. The store turns drafts into append-only versions.</summary>
    public sealed record AssertionDraft
    {
        public required string SubjectKind { get; init; }
        /// <summary>Canonical subject id at write time (queries resolve merges through the canonical map).</summary>
        public required string SubjectId { get; init; }
        public required string Attribute { get; init; }
        public required AssertionValue Value { get; init; }
        public string? ObjectKind { get; init; }
        public string? ObjectId { get; init; }
        public string? Episode { get; init; }
        public TimeInterval? ValidFrom { get; init; }
        /// <summary>Null = still valid (open-ended).</summary>
        public TimeInterval? ValidTo { get; init; }
        /// <summary>Time of the latest supporting evidence (last corroboration, drives read-time decay).</summary>
        public long? AssertedAtMs { get; init; }
        public required string Status { get; init; }
        public string? Modality { get; init; }
        public string? SourceKind { get; init; }
        public required double Probability { get; init; }
        public bool Calibrated { get; init; }
        public string Sensitivity { get; init; } = "normal";
        public required string Producer { get; init; }
        public required int ModuleVersion { get; init; }
        public string? PromptVersion { get; init; }
        public string? Model { get; init; }
        public long? JobId { get; init; }
        public required long SourceSeq { get; init; }
        /// <summary>Derivation chain (fact ids an inference rests on, or the claim ids a fact consolidates).</summary>
        public object? Derivation { get; init; }
        public required IReadOnlyList<EvidenceRef> Evidence { get; init; }
        public required AssertionDims Dims { get; init; }

        [JsonIgnore]
        public string NaturalKey => NaturalKeys.Assertion(SubjectKind, SubjectId, Attribute, Value.Normalized, ObjectKind, ObjectId, Episode);
    }

    /// <summary>A stored assertion row (one version).</summary>
    public sealed record AssertionRecord(
        string AssertionId,
        string NaturalKey,
        string SubjectKind,
        string SubjectId,
        string Attribute,
        string AttrClass,
        string ValueJson,
        string? ValueText,
        string? ObjectKind,
        string? ObjectId,
        long? ValidFromEarliestMs,
        long? ValidFromLatestMs,
        long? ValidToEarliestMs,
        long? ValidToLatestMs,
        long? AssertedAtMs,
        long RecordedAtMs,
        long? SupersededAtMs,
        long? RetractedAtMs,
        string Status,
        string? Modality,
        string? SourceKind,
        double? Probability,
        bool Calibrated,
        string Sensitivity,
        string ModuleId,
        int ModuleVersion,
        string? PromptVersion,
        string? Model,
        long? JobId,
        long SourceSeq,
        string? DerivationJson,
        string? Producer,
        string? Category,
        long? FirstEvidenceAtMs = null)
    {
        public AssertionValue Value => JsonSerializer.Deserialize<AssertionValue>(ValueJson, Kernel.Jobs.KernelJson.Options)!;

        public TimeInterval? ValidFrom => ValidFromEarliestMs is long e && ValidFromLatestMs is long l ? new TimeInterval(e, l) : null;

        public TimeInterval? ValidTo => ValidToEarliestMs is long e && ValidToLatestMs is long l ? new TimeInterval(e, l) : null;

        public bool IsLiveRecord => SupersededAtMs == null && RetractedAtMs == null;

        /// <summary>Possibly valid in the world at <paramref name="atMs"/>: the uncertainty intervals allow it.</summary>
        public bool PossiblyValidAt(long atMs) =>
            (ValidFromEarliestMs == null || ValidFromEarliestMs <= atMs) && (ValidToLatestMs == null || ValidToLatestMs >= atMs);

        /// <summary>Recorded and not yet superseded or retracted at <paramref name="atMs"/> (record time).</summary>
        public bool BelievedAt(long atMs) =>
            RecordedAtMs <= atMs && (SupersededAtMs == null || SupersededAtMs > atMs) && (RetractedAtMs == null || RetractedAtMs > atMs);
    }

    /// <summary>Confidence of a fact at an as-of instant, computed at read time from the last corroboration (fixes F027/F028/F216/F217:
    /// decay is never applied to the stored value, depends on elapsed time not run count, and never hides historical truth).</summary>
    public static class Confidence
    {
        public const double DecayFloor = 0.2;

        public static double Effective(AttributeDefinition attribute, double probability, long? lastCorroborationMs, long asOfMs)
        {
            if (attribute.HalfLifeDays is not double hl || lastCorroborationMs is not long last) return probability;
            if (attribute.Class is AttributeClass.Immutable or AttributeClass.Event or AttributeClass.TimeDerived) return probability;
            return probability * new DecayParameters(hl, DecayFloor).Weight(Math.Min(last, asOfMs), asOfMs);
        }
    }
}
