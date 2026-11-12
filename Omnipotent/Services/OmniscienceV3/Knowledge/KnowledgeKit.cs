using System.Runtime.CompilerServices;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Knowledge.Evidence;
using Omnipotent.Services.OmniscienceV3.Knowledge.Retrieval;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.Ml;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Knowledge
{
    /// <summary>A second opinion on how a message was meant (plan A2: the irony/modality and dialogue-act ensembles, WS-C).</summary>
    public sealed record ModalityOpinion(string? Label, double NonLiteralProbability, double QuestionProbability, string ModelVersion);

    /// <summary>The A2 modality/dialogue-act ensemble's per-message opinion. Null when no model has labelled the message.</summary>
    public interface IModalityOracle
    {
        ModalityOpinion? Get(string messageId, int version);
    }

    /// <summary>NLI over (quote, claim): does the quoted text entail, contradict or say nothing about the claim?</summary>
    public enum Entailment { Entails, Contradicts, Neutral }

    public interface IClaimEntailment
    {
        Task<(Entailment Verdict, double Probability)?> CheckAsync(string premise, string hypothesis, CancellationToken ct);
    }

    /// <summary>Text that came with a message but was not typed by its author: OCR of an image, a transcript, a caption.</summary>
    public sealed record AttachmentText(string AttachmentId, string MessageId, string MediaKind, string? Filename, string? Text, string TextKind);

    /// <summary>OCR, transcripts and captions per message (WS-A writes them; the default reads alt text and file names).</summary>
    public interface IAttachmentTextSource
    {
        IReadOnlyList<AttachmentText> For(IReadOnlyCollection<string> messageIds);
    }

    /// <summary>A person's IANA zone at an instant (plan T4, inferred per period by WS-C). Null = unknown: the stamp then says so.</summary>
    public interface IPersonZoneOracle
    {
        TimeZoneInfo? ZoneAt(string canonicalPersonId, long atMs);
    }

    /// <summary>Whether a conversation is fiction (roleplay channel, A2 channel classifier). 0 when unknown.</summary>
    public interface IFictionOracle
    {
        double FictionProbability(string conversationId);
    }

    /// <summary>
    /// The services every knowledge module and tactic shares within one running engine: the assertion and gold-label stores,
    /// the corpus reader, hybrid retrieval, calibration, and the plug-in points other workstreams fill at integration
    /// (WS-C's shared encoder and A2 ensembles, WS-A's OCR/transcripts, the person-zone inference). Keyed by the kernel's
    /// <see cref="ModuleContext"/>, which is one object per engine, so two engines in one process (tests) never share state.
    /// </summary>
    public sealed class KnowledgeKit
    {
        private static readonly ConditionalWeakTable<ModuleContext, KnowledgeKit> kits = new();

        public static KnowledgeKit For(ModuleContext context) => kits.GetValue(context, c => new KnowledgeKit(c));

        private KnowledgeKit(ModuleContext context)
        {
            Context = context;
            Assertions = new AssertionStore(context.Stores, context.Clock);
            Gold = new GoldLabelStore(context.Stores, context.Clock);
            Corpus = new CorpusReader(context.Stores, context.Policy);
            Modality = new LabelTableModalityOracle(context.Stores.Intel);
            Media = new CorpusAttachmentTextSource(context.Stores);
            Fiction = new LabelTableFictionOracle(context.Stores.Intel);
            Zones = new SelfZoneOracle(Corpus, () => context.Settings.KlivesZone);
            Retriever = new HybridRetriever(context.Stores, Corpus, () => Encoder);
            Calibration = new Deduction.CalibrationStore(context.Stores.Intel, context.Clock);
        }

        public ModuleContext Context { get; }
        public AssertionStore Assertions { get; }
        public GoldLabelStore Gold { get; }
        public CorpusReader Corpus { get; }
        public HybridRetriever Retriever { get; }
        public Deduction.CalibrationStore Calibration { get; }

        /// <summary>WS-C's shared multilingual encoder (vector half of hybrid retrieval, semantic matching). Null until wired:
        /// retrieval then runs on full-text search alone and says so in its coverage.</summary>
        public IEmbeddingEncoder? Encoder { get; set; }
        public IModalityOracle Modality { get; set; }
        public IClaimEntailment? Entailment { get; set; }
        public IAttachmentTextSource Media { get; set; }
        public IPersonZoneOracle Zones { get; set; }
        public IFictionOracle Fiction { get; set; }
        /// <summary>WS-C's Explore engine (X11 explains a chart point from its drill records). Null until wired.</summary>
        public Kernel.Explore.IExploreEngine? Explore { get; set; }
    }

    /// <summary>Reads WS-C's per-message labels (intel.db <c>labels</c>, contract R7 #5) for the modality and dialogue-act tasks.</summary>
    public sealed class LabelTableModalityOracle : IModalityOracle
    {
        /// <summary>Label task names the A2 ensembles write (agreed contract with WS-C; see ws-b.md).</summary>
        public const string ModalityTask = "modality";
        public const string DialogueActTask = "dialogue_act";

        private readonly SqliteStore intel;

        public LabelTableModalityOracle(SqliteStore intel) => this.intel = intel;

        public ModalityOpinion? Get(string messageId, int version)
        {
            using var c = intel.OpenRead();
            var rows = c.Query(null, """
                SELECT task, label, probability, abstained, model_version FROM labels
                WHERE item_kind='message' AND item_id=$i AND item_version=$v AND task IN ($m,$d)
                ORDER BY as_of_ms DESC
                """, r => (Task: r.GetString(0), Label: r.GetStringOrNull(1), P: r.GetDoubleOrNull(2) ?? 0, Abstained: r.GetInt32(3) == 1, Version: r.GetString(4)),
                ("$i", messageId), ("$v", version), ("$m", ModalityTask), ("$d", DialogueActTask));
            if (rows.Count == 0) return null;
            var modality = rows.FirstOrDefault(r => r.Task == ModalityTask && !r.Abstained);
            var act = rows.FirstOrDefault(r => r.Task == DialogueActTask && !r.Abstained);
            double nonLiteral = modality.Label == null ? 0 : modality.Label == Store.Modality.Literal ? 1 - modality.P : modality.P;
            double question = act.Label == null ? 0 : act.Label == "question" ? act.P : 1 - act.P;
            if (modality.Label == null && act.Label == null) return null;
            return new ModalityOpinion(modality.Label, nonLiteral, act.Label == null ? 0 : question, modality.Version ?? act.Version ?? "");
        }
    }

    /// <summary>Reads the A2 channel classifier's fiction label for a conversation (task <c>conversation_fiction</c>).</summary>
    public sealed class LabelTableFictionOracle : IFictionOracle
    {
        public const string Task = "conversation_fiction";
        private readonly SqliteStore intel;

        public LabelTableFictionOracle(SqliteStore intel) => this.intel = intel;

        public double FictionProbability(string conversationId)
        {
            using var c = intel.OpenRead();
            var row = c.Query(null, "SELECT label, probability, abstained FROM labels WHERE item_kind='conversation' AND item_id=$i AND task=$t ORDER BY as_of_ms DESC LIMIT 1",
                r => (Label: r.GetStringOrNull(0), P: r.GetDoubleOrNull(1) ?? 0, Abstained: r.GetInt32(2) == 1), ("$i", conversationId), ("$t", Task)).FirstOrDefault();
            if (row.Label == null || row.Abstained) return 0;
            return row.Label == "fiction" ? row.P : 1 - row.P;
        }
    }

    /// <summary>Until WS-C infers zones per person and period, only Klives's own zone is known (his setting).</summary>
    public sealed class SelfZoneOracle : IPersonZoneOracle
    {
        private readonly CorpusReader corpus;
        private readonly Func<TimeZoneInfo> klivesZone;

        public SelfZoneOracle(CorpusReader corpus, Func<TimeZoneInfo> klivesZone)
        {
            this.corpus = corpus;
            this.klivesZone = klivesZone;
        }

        public TimeZoneInfo? ZoneAt(string canonicalPersonId, long atMs) => corpus.IsSelf(canonicalPersonId) ? klivesZone() : null;
    }
}
