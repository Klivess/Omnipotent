using System.Globalization;
using Omnipotent.Services.OmniscienceV3.Knowledge.Evidence;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Deduction
{
    /// <summary>The verifier's decision on one raw claim.</summary>
    public sealed record VerifierOutcome(VerifiedClaim? Claim, string? RejectReason, string? Detail)
    {
        public bool Accepted => Claim != null;
    }

    /// <summary>
    /// The deterministic verifier (plan "Verifier") with the A2 ensembles as second opinions. It rejects: references outside
    /// the slice (zero hallucinated refs), quotes not found (fuzzy ≥ 0.92) in the cited message version, self-disclosures
    /// whose cited author is not the subject, questions treated as statements, non-literal or insincere statements, claims
    /// that rest only on forwarded/bot/system text, fiction-channel statements, inferred special-category traits, and
    /// (with an NLI model installed) claims their own quote contradicts (dropped negations). Temporal resolutions are
    /// cross-checked with the deterministic resolver at the author-local reference time: a contradicted interval is replaced
    /// by the deterministic one and the claim's confidence lowered (T3), never silently kept.
    /// </summary>
    public sealed class Verifier
    {
        private readonly KnowledgeKit kit;

        public Verifier(KnowledgeKit kit) => this.kit = kit;

        public async Task<VerifierOutcome> VerifyAsync(RawClaim raw, EvidenceSlice slice, string runId, string promptVersion, string? model, string agreement,
            CancellationToken ct)
        {
            // Subject and object resolve through the roster: an unknown handle is a hallucinated person.
            string? subject = raw.Subject == null ? null : slice.Roster.PersonFor(raw.Subject);
            if (subject == null) return Reject(RejectReasons.UnknownSubject, raw.Subject);
            string? objectPerson = string.IsNullOrWhiteSpace(raw.Object) ? null : slice.Roster.PersonFor(raw.Object);

            var def = Attributes.Get(raw.Attribute);
            string attribute = Attributes.TryGet(raw.Attribute, out var known) ? known.Id : Attributes.Other;
            string? other = attribute == Attributes.Other ? raw.AttributeOther?.Trim().ToLowerInvariant() : null;
            // An "other" attribute that names a special category is that category (the guard cannot be sidestepped by naming).
            if (other != null && (SensitiveTraits.IsSensitive(other) || other.Replace(' ', '_') is var o && SensitiveTraits.IsSensitive(o)))
            {
                attribute = SensitiveTraits.Categories.First(c => c == other || c == other.Replace(' ', '_'));
                def = Attributes.Get(attribute);
            }

            // Evidence: every reference must exist in this slice and every quote must be found in that message version.
            if (raw.Evidence == null || raw.Evidence.Count == 0) return Reject(RejectReasons.NoEvidence, null);
            var evidence = new List<ClaimEvidence>();
            var attachmentEvidence = new HashSet<string>(StringComparer.Ordinal);
            string? firstProblem = null;
            foreach (var e in raw.Evidence)
            {
                var msg = slice.ByRef(e.Ref ?? "");
                if (msg == null) { firstProblem ??= RejectReasons.RefOutsideSlice + ":" + e.Ref; continue; }
                bool inText = TextMatch.QuoteFound(e.Quote, msg.Text);
                bool inAttachment = !inText && msg.AttachmentText != null && TextMatch.QuoteFound(e.Quote, msg.AttachmentText);
                if (!inText && !inAttachment) { firstProblem ??= RejectReasons.QuoteNotFound + ":" + e.Ref; continue; }
                evidence.Add(new ClaimEvidence(msg.Ref, msg.MessageId, msg.Version, e.Quote!.Trim(), msg.AuthorPersonId, msg.SentAtMs, msg.Deleted,
                    msg.ConversationId, msg.GuildId, msg.Platform, msg.ConversationKind, msg.SourceKind, msg.BestSourceId, msg.AuthorZoneId));
                if (inAttachment) attachmentEvidence.Add(msg.MessageId);
            }
            if (evidence.Count == 0)
            {
                var reason = firstProblem?.Split(':')[0] ?? RejectReasons.NoEvidence;
                return Reject(reason, firstProblem);
            }

            string modality = Modality.IsKnown(raw.Modality) ? raw.Modality! : Modality.Literal;
            string polarity = raw.Polarity == Polarity.Negated ? Polarity.Negated : Polarity.Affirmed;
            if (modality == Modality.Negated) { modality = Modality.Literal; polarity = Polarity.Negated; }
            string source = raw.SourceKind is ClaimSource.SelfDisclosure or ClaimSource.ThirdParty or ClaimSource.Inferred ? raw.SourceKind : ClaimSource.ThirdParty;
            var flags = new List<string>();
            double probability = Math.Clamp(raw.Probability ?? 0.6, 0.01, 0.99);

            if (raw.SpeechAct == "question" || modality == Modality.Question) return Reject(RejectReasons.QuestionAsStatement, raw.Value);
            if (modality != Modality.Literal) return Reject(RejectReasons.NonLiteral, modality);
            if (raw.Sincerity == "insincere") return Reject(RejectReasons.Insincere, raw.Value);
            if (raw.Sincerity == "unclear") { probability *= 0.7; flags.Add("sincerity_unclear"); }

            // Self-disclosure: at least one cited message must be the subject's own words (not forwarded, not a bot relay).
            var ownWords = evidence.Where(e => e.AuthorPersonId == subject && !attachmentEvidence.Contains(e.MessageId)
                                               && slice.ById(e.MessageId) is { NonAuthored: false }).ToList();
            if (source == ClaimSource.SelfDisclosure && ownWords.Count == 0)
            {
                return evidence.Any(e => e.AuthorPersonId == subject)
                    ? Reject(RejectReasons.NonAuthoredEvidence, "The subject's cited message is forwarded, a bot relay or text inside an image.")
                    : Reject(RejectReasons.SelfDisclosureAuthorMismatch, raw.Subject);
            }
            if (evidence.All(e => slice.ById(e.MessageId) is { NonAuthored: true } || attachmentEvidence.Contains(e.MessageId)))
            {
                // Only quoted/forwarded/OCR text: never a statement by anyone in the conversation.
                return Reject(RejectReasons.NonAuthoredEvidence, null);
            }

            // Special-category traits: only the person's own literal statement is ever kept (plan Guardrails).
            if (Attributes.IsSensitive(attribute) && !SensitiveTraits.AllowsAssertion(attribute, source, modality))
                return Reject(RejectReasons.SensitiveInference, attribute);

            // Second opinions from the A2 ensembles.
            foreach (var e in evidence)
            {
                var opinion = kit.Modality.Get(e.MessageId, e.Version);
                if (opinion == null) continue;
                if (opinion.QuestionProbability >= 0.85 && raw.SpeechAct is "statement" or null && e.AuthorPersonId == subject && source == ClaimSource.SelfDisclosure)
                    return Reject(RejectReasons.QuestionAsStatement, $"The dialogue-act model reads {e.Ref} as a question ({opinion.QuestionProbability:0.00}).");
                if (opinion.NonLiteralProbability >= 0.9) return Reject(RejectReasons.NonLiteral, $"{opinion.Label} ({opinion.NonLiteralProbability:0.00}, {opinion.ModelVersion})");
                if (opinion.NonLiteralProbability >= 0.7) { probability *= 0.6; flags.Add("modality_disputed:" + opinion.Label); }
            }
            double fiction = evidence.Select(e => kit.Fiction.FictionProbability(e.ConversationId)).DefaultIfEmpty(0).Min();
            if (fiction >= 0.85) return Reject(RejectReasons.Fiction, $"{fiction:0.00}");
            if (fiction >= 0.7) { probability *= 0.6; flags.Add("fiction_channel"); }

            if (kit.Entailment != null)
            {
                string statement = Statement(raw, polarity);
                foreach (var e in evidence)
                {
                    var nli = await kit.Entailment.CheckAsync(e.Quote, statement, ct).ConfigureAwait(false);
                    if (nli is { Verdict: Entailment.Contradicts, Probability: >= 0.8 })
                        return Reject(RejectReasons.NegationDropped, $"The quote at {e.Ref} contradicts \"{statement}\".");
                }
            }

            // Deleted evidence still counts, labelled and at reduced weight (plan T5).
            if (evidence.All(e => e.Deleted)) { probability *= 0.7; flags.Add("evidence_deleted_by_author"); }

            // Time: parse the model's intervals in the anchor's author-local zone, then cross-check deterministically.
            var anchor = raw.Time?.Anchor != null ? slice.ByRef(raw.Time.Anchor) : null;
            anchor ??= slice.ByRef(evidence[0].Ref);
            var zone = anchor?.AuthorZoneId != null && Zones.TryGet(anchor.AuthorZoneId, out var z) ? z : null;
            var occurred = ParseInterval(raw.Time?.Occurred, zone);
            var validFrom = ParseInterval(raw.Time?.ValidFrom, zone);
            var validTo = ParseInterval(raw.Time?.ValidTo, zone);
            string? timeCheck = null;
            if (!string.IsNullOrWhiteSpace(raw.Time?.Surface) && anchor != null)
            {
                bool isEvent = def.Class == AttributeClass.Event || raw.Plan != null;
                var modelInterval = isEvent ? occurred : validFrom ?? occurred;
                var check = TemporalResolver.Check(modelInterval, raw.Time.Surface, anchor.SentAtMs, zone);
                timeCheck = check.Check.ToString();
                if (check.Check == TemporalCheck.Disagrees)
                {
                    probability *= 0.7;
                    flags.Add("time_resolution_disagreed");
                    if (isEvent) occurred = check.Chosen; else if (validFrom != null) validFrom = check.Chosen; else occurred = check.Chosen;
                }
                else if (check.Check == TemporalCheck.ModelSilent && check.Chosen != null)
                {
                    if (isEvent) occurred = check.Chosen; else validFrom ??= check.Chosen;
                }
            }

            long assertedAt = evidence.Max(e => e.SentAtMs);
            string value = (raw.Value ?? raw.Entity?.Name ?? "").Trim();
            if (value.Length == 0 && objectPerson == null) return Reject(RejectReasons.InvalidClaim, "empty value");
            if (value.Length > 500) value = value[..500];
            var claim = new VerifiedClaim
            {
                ClaimId = Store.NaturalKeys.Id("cl", runId + "|" + slice.Index.ToString(CultureInfo.InvariantCulture) + "|" + (raw.Id ?? "") + "|" + subject + "|" + attribute + "|" + Store.NaturalKeys.Normalize(value) + "|" + agreement),
                RunId = runId,
                SliceIndex = slice.Index,
                SubjectPersonId = subject,
                Attribute = attribute,
                Category = def.Category,
                ValueText = other != null && attribute == Attributes.Other ? other + ": " + value : value,
                ValueNorm = Store.NaturalKeys.Normalize(other != null && attribute == Attributes.Other ? other + ": " + value : value),
                ObjectPersonId = objectPerson,
                NameKind = raw.NameKind,
                Polarity = polarity,
                Modality = modality,
                SourceKind = source,
                SpeechAct = raw.SpeechAct,
                Sincerity = raw.Sincerity,
                Probability = Math.Clamp(probability, 0.01, 0.99),
                Occurred = occurred,
                ValidFrom = validFrom,
                ValidTo = validTo,
                IsCurrent = raw.Time?.IsCurrent,
                TimeSurface = raw.Time?.Surface,
                TimePrecision = raw.Time?.Precision,
                TimeCheck = timeCheck,
                Age = raw.Age,
                Plan = raw.Plan,
                Entity = raw.Entity,
                Relation = raw.Relation,
                CorrectsMessageId = raw.Corrects == null ? null : slice.ByRef(raw.Corrects)?.MessageId,
                PlanRefersToMessageId = raw.Plan?.RefersTo == null ? null : slice.ByRef(raw.Plan.RefersTo)?.MessageId,
                PromiserPersonId = raw.Plan?.Promiser == null ? null : slice.Roster.PersonFor(raw.Plan.Promiser),
                PromiseePersonId = raw.Plan?.Promisee == null ? null : slice.Roster.PersonFor(raw.Plan.Promisee),
                Evidence = evidence,
                AgreementKind = agreement,
                Flags = flags.Count == 0 ? null : string.Join(",", flags),
                AssertedAtMs = assertedAt,
                PromptVersion = promptVersion,
                Model = model,
            };
            return new VerifierOutcome(claim, null, null);
        }

        private static VerifierOutcome Reject(string reason, string? detail) => new(null, reason, detail);

        /// <summary>The claim as a sentence (for NLI and the critic).</summary>
        public static string Statement(RawClaim raw, string polarity)
        {
            string attr = (raw.Attribute == Attributes.Other ? raw.AttributeOther : Attributes.Get(raw.Attribute).Title)?.ToLowerInvariant() ?? "fact";
            string not = polarity == Polarity.Negated ? "not " : "";
            return $"{raw.Subject} {not}has {attr}: {raw.Value}";
        }

        /// <summary>An ISO date, month, year or date-time in the author's zone (UTC ± 14 h when unknown) as an interval.</summary>
        public static TimeInterval? ParseInterval(RawInterval? raw, TimeZoneInfo? zone)
        {
            if (raw == null) return null;
            var a = ParseBound(raw.Earliest, zone);
            var b = ParseBound(raw.Latest, zone);
            if (a == null && b == null) return null;
            long start = (a ?? b)!.Value.EarliestMs;
            long end = (b ?? a)!.Value.LatestMs;
            if (end < start) (start, end) = (Math.Min(start, end), Math.Max(start, end));
            var interval = new TimeInterval(start, end);
            return zone == null ? interval.Widen(TimeSpan.FromHours(14)) : interval;
        }

        public static TimeInterval? ParseBound(string? text, TimeZoneInfo? zone)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string s = text.Trim();
            var z = zone ?? TimeZoneInfo.Utc;
            if (DateTime.TryParseExact(s, new[] { "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                long at = Zones.ResolveLocal(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), z).UtcMs;
                return new TimeInterval(at, at + 59_999);
            }
            if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                var (start, end) = Zones.LocalDayRange(d, z);
                return new TimeInterval(start, end - 1);
            }
            if (DateTime.TryParseExact(s, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
            {
                var first = DateOnly.FromDateTime(month);
                return new TimeInterval(Zones.DayStartUtcMs(first, z), Zones.DayStartUtcMs(first.AddMonths(1), z) - 1);
            }
            if (s.Length == 4 && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int year) && year is > 1800 and < 2300)
                return new TimeInterval(Zones.DayStartUtcMs(new DateOnly(year, 1, 1), z), Zones.DayStartUtcMs(new DateOnly(year + 1, 1, 1), z) - 1);
            return null;
        }
    }
}
