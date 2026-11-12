using Omnipotent.Services.OmniscienceV3.Knowledge.Evidence;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Quality
{
    /// <summary>The deduction accuracy of one run against planted truth (plan "Accept" gates).</summary>
    public sealed record DeductionEvaluation(
        Proportion Precision,
        Proportion LiteralSelfPrecision,
        Proportion Recall,
        IReadOnlyDictionary<string, Proportion> PrecisionByCategory,
        int RestrictedFactsStoredForMinors,
        int HallucinatedEvidenceRefs,
        int SensitiveInferencesStored,
        bool RelocationClosed,
        IReadOnlyList<string> FalsePositives,
        IReadOnlyList<string> Missed)
    {
        public const double LiteralSelfTarget = 0.95;
        public const double OverallTarget = 0.85;

        /// <summary>The plan's acceptance: literal self-disclosure precision ≥ 0.95, overall ≥ 0.85, zero restricted facts stored for
        /// minors, zero hallucinated evidence references, zero sensitive inferences.</summary>
        public bool PassesGates => (double.IsNaN(LiteralSelfPrecision.Value) || LiteralSelfPrecision.Value >= LiteralSelfTarget)
                                   && (double.IsNaN(Precision.Value) || Precision.Value >= OverallTarget)
                                   && RestrictedFactsStoredForMinors == 0 && HallucinatedEvidenceRefs == 0 && SensitiveInferencesStored == 0;
    }

    /// <summary>
    /// Scores what the deduction engine stored against a planted-fact corpus: precision of every fact shown as knowledge
    /// (accepted or confirmed), precision of literal self-disclosures, recall of the planted facts, the MinorGuard and
    /// sensitive-trait invariants, and that every stored evidence reference exists and quotes its message version.
    /// </summary>
    public static class DeductionEvaluator
    {
        public static DeductionEvaluation Evaluate(KnowledgeKit kit, PlantedCorpus corpus, long asOfMs)
        {
            var positives = corpus.Messages.Where(m => m.Kind is PlantKind.LiteralSelf or PlantKind.ThirdParty or PlantKind.Negated or PlantKind.Relocation
                                                         && m.Attribute is not ("age" or "school_year")).ToList();
            var shown = new List<AssertionView>();
            foreach (var person in corpus.Persons)
                shown.AddRange(kit.Assertions.Query(new AssertionQuery
                {
                    SubjectIds = kit.Corpus.PersonIdsOf(person), AtMs = asOfMs, Mode = KnowledgeMode.AsKnownAt, Producer = Producers.Extract,
                    Statuses = new[] { AssertionStatus.Accepted, AssertionStatus.Confirmed }, IncludeInferred = false, Limit = 5_000,
                }));
            int tp = 0, fp = 0, selfTp = 0, selfFp = 0;
            var falsePositives = new List<string>();
            var byCategory = new Dictionary<string, (int Tp, int Fp)>(StringComparer.Ordinal);
            var matchedPlants = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in shown)
            {
                var r = a.Record;
                var match = positives.FirstOrDefault(p => p.SubjectPersonId == kit.Corpus.Canonical(r.SubjectId) && p.Attribute == r.Attribute
                                                          && Same(p.Value, r.ValueText ?? r.Value.Text) && (p.Polarity ?? Polarity.Affirmed) == r.Value.Polarity);
                bool ok = match != null;
                if (ok) { tp++; matchedPlants.Add(match!.MessageId); } else { fp++; falsePositives.Add($"{r.SubjectId} {r.Attribute}={r.ValueText} ({r.Value.Polarity}, {r.Status})"); }
                var cat = byCategory.GetValueOrDefault(a.Definition.Category);
                byCategory[a.Definition.Category] = ok ? (cat.Tp + 1, cat.Fp) : (cat.Tp, cat.Fp + 1);
                if (r.SourceKind == ClaimSource.SelfDisclosure) { if (ok) selfTp++; else selfFp++; }
            }
            var missed = positives.Where(p => !matchedPlants.Contains(p.MessageId) && !shown.Any(a => a.Record.Attribute == p.Attribute && Same(p.Value, a.Record.ValueText ?? "")))
                .Select(p => $"{p.SubjectPersonId} {p.Attribute}={p.Value}").ToList();

            // MinorGuard: no restricted fact of the minor anywhere (assertions of any status, claim values).
            int restricted = 0;
            var restrictedAttrs = Attributes.All.Where(x => x.Sensitivity == AttributeSensitivity.RestrictedForMinors).Select(x => x.Id).ToList();
            using (var c = kit.Context.Stores.Intel.OpenRead())
            {
                foreach (var attr in restrictedAttrs)
                {
                    restricted += (int)c.Scalar<long>(null, "SELECT COUNT(*) FROM assertions WHERE subject_id=$p AND attribute=$a", ("$p", corpus.MinorPersonId), ("$a", attr));
                    restricted += (int)c.Scalar<long>(null, "SELECT COUNT(*) FROM kn_claims WHERE person_id=$p AND attribute=$a AND value_text IS NOT NULL", ("$p", corpus.MinorPersonId), ("$a", attr));
                }
            }
            // Sensitive inference: hearsay about a special-category trait must never be stored.
            int sensitive = 0;
            foreach (var trap in corpus.Messages.Where(m => m.Kind == PlantKind.SensitiveInference))
                sensitive += shown.Count(a => a.Record.SubjectId == trap.SubjectPersonId && a.Record.Attribute == trap.Attribute && a.Record.SourceKind != ClaimSource.SelfDisclosure);
            using (var c = kit.Context.Stores.Intel.OpenRead())
                sensitive += (int)c.Scalar<long>(null, "SELECT COUNT(*) FROM assertions WHERE sensitivity='sensitive_self' AND (source_kind IS NULL OR source_kind <> 'self_disclosure')");

            // Evidence references: every cited message exists and its quote is in the cited version.
            int hallucinated = 0;
            List<(string Message, int Version, string? Quote)> evidence;
            using (var c = kit.Context.Stores.Intel.OpenRead())
                evidence = c.Query(null, "SELECT message_id, message_version, quote FROM assertion_evidence", r => (r.GetString(0), r.GetInt32(1), r.GetStringOrNull(2)));
            foreach (var (message, version, quote) in evidence)
            {
                string? text = kit.Corpus.VersionText(message, version);
                if (text == null || (quote != null && !TextMatch.QuoteFound(quote, text))) hallucinated++;
            }

            // Relocation: the older city episode is closed by the newer one.
            bool relocationClosed = true;
            foreach (var reloc in corpus.Messages.Where(m => m.Kind == PlantKind.Relocation))
            {
                var episodes = kit.Assertions.Query(new AssertionQuery
                {
                    SubjectIds = kit.Corpus.PersonIdsOf(reloc.SubjectPersonId!), AtMs = asOfMs, Mode = KnowledgeMode.AsKnownAt, Attribute = reloc.Attribute,
                    Statuses = new[] { AssertionStatus.Accepted, AssertionStatus.Confirmed, AssertionStatus.Contested, AssertionStatus.Proposed }, Limit = 50,
                });
                var old = episodes.FirstOrDefault(e => Same(reloc.Value, e.Record.ValueText ?? ""));
                relocationClosed &= old != null && old.Record.ValidTo != null;
            }

            var cats = byCategory.ToDictionary(kv => kv.Key, kv => Metrics.Precision(kv.Value.Tp, kv.Value.Fp), StringComparer.Ordinal);
            return new DeductionEvaluation(Metrics.Precision(tp, fp), Metrics.Precision(selfTp, selfFp), Metrics.Recall(tp, Math.Max(0, positives.Count - matchedPlants.Count)),
                cats, restricted, hallucinated, sensitive, relocationClosed, falsePositives, missed);
        }

        private static bool Same(string? planted, string stored)
        {
            string a = NaturalKeys.Normalize(planted), b = NaturalKeys.Normalize(stored);
            return a.Length > 0 && b.Length > 0 && (a == b || a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal));
        }
    }
}
