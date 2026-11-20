using System.Globalization;
using System.Text;
using System.Text.Json;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Knowledge.Deduction;
using Omnipotent.Services.OmniscienceV3.Knowledge.Evidence;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Knowledge.Prompting;
using Omnipotent.Services.OmniscienceV3.Knowledge.Retrieval;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.Llm;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Inquiry
{
    /// <summary>What a tactic asks the engine.</summary>
    public sealed record InquiryRequest
    {
        public required string Question { get; init; }
        public required string TacticKey { get; init; }
        public IReadOnlyList<string> PersonIds { get; init; } = Array.Empty<string>();
        public InquiryAnswerType? AnswerType { get; init; }
        public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();
        public int Passes { get; init; } = 5;
        public bool Interactive { get; init; } = true;
        public bool Standing { get; init; }
        public long? StandingMinIntervalMs { get; init; }
        public string? StandingId { get; init; }
        /// <summary>Extra instruction for the reasoning passes (X06 gaps, X12 consistency, X07 stance).</summary>
        public string? Framing { get; init; }
        /// <summary>Evidence computed by the tactic itself (X08 behaviour statistics, X05 discovery).</summary>
        public IReadOnlyList<EvidenceItem> ExtraEvidence { get; init; } = Array.Empty<EvidenceItem>();
        /// <summary>Restrict evidence to a window (X09, X13).</summary>
        public long? FromMs { get; init; }
    }

    /// <summary>
    /// The inquiry engine (plan X-inquiry, the flagship "ask anything → deduction job"). Stages are checkpointed with
    /// <see cref="TacticRunContext.StageAsync{T}"/>, so a paused or restarted run resumes where it stopped:
    /// <list type="number">
    /// <item><b>understand</b>: subjects resolved through names and aliases over time (ambiguity stops the run and asks),
    /// answer type, options, time scope (cross-checked by the deterministic resolver in Klives's zone), a neutral restatement;</item>
    /// <item><b>policy</b>: special-category questions are refused (quoting only the person's own explicit statements);
    /// restricted or routine questions about minors are refused, with the reason;</item>
    /// <item><b>plan</b>: evidence facets with multilingual queries and counter-queries, and a labelled base-rate prior;</item>
    /// <item><b>gather</b>: hybrid retrieval of the subjects' own words, others' words in their conversations, what they
    /// reacted to, and their facts, ranked by relevance × reliability × recency, near-duplicates folded, jokes and fiction
    /// excluded and counted, nothing after the as-of;</item>
    /// <item><b>reason</b>: K independent passes on one cached dossier (each its own session on the same prefix, served from the
    /// prefix cache, with a different reading order against position bias), then a devil's advocate;</item>
    /// <item><b>verify</b>: citations must exist and support their point; passes citing nothing are dropped;</item>
    /// <item><b>aggregate</b>: median with a disagreement penalty, the advocate's shrink, the Quality Lab's inquiry curve, a band,
    /// evidence strength, the verdict;</item>
    /// <item><b>report</b> and <b>persist</b>: evidence for and against with dates and places, a temporal note, what would change the
    /// answer, questions to ask, coverage; an <c>inferred</c> assertion Klives can confirm or reject; optionally standing.</item>
    /// </list>
    /// </summary>
    public sealed class InquiryEngine
    {
        private readonly KnowledgeKit kit;

        public InquiryEngine(KnowledgeKit kit) => this.kit = kit;

        private OmniLlm Llm => kit.Context.Llm ?? throw new JobFailureException(ErrorClass.Transient, "The LLM gateway is not available in this engine.");

        public async Task<InquiryReportDto> RunAsync(TacticRunContext ctx, InquiryRequest req)
        {
            var spec = await ctx.StageAsync("understand", ct => UnderstandAsync(ctx, req, ct)).ConfigureAwait(false);
            await ctx.ReportAsync(new { stage = "understood", spec.Restatement, subjects = spec.Subjects.Select(s => s.Label), spec.AnswerType }).ConfigureAwait(false);
            if (spec.Ambiguous.Count > 0 || (spec.Subjects.Count == 0 && req.TacticKey != "x15.ask_corpus"))
                return await ctx.StageAsync("persist", ct => PersistAsync(ctx, req, spec, NeedsDisambiguation(ctx, spec), ct)).ConfigureAwait(false);

            var refusal = Policy(spec, ctx.AsOfMs);
            if (refusal != null)
                return await ctx.StageAsync("persist", ct => PersistAsync(ctx, req, spec, Refused(ctx, spec, refusal), ct)).ConfigureAwait(false);

            var plan = await ctx.StageAsync("plan", ct => PlanAsync(ctx, req, spec, ct)).ConfigureAwait(false);
            var gathered = await ctx.StageAsync("gather", ct => GatherAsync(spec, plan, req, ct)).ConfigureAwait(false);
            await ctx.ReportAsync(new { stage = "gathered", items = gathered.Items.Count, gathered.Coverage.MessagesConsidered }).ConfigureAwait(false);
            var passes = await ctx.StageAsync("reason", ct => ReasonAsync(ctx, req, spec, gathered, ct)).ConfigureAwait(false);
            var verified = await ctx.StageAsync("verify", ct => VerifyAsync(ctx, req, spec, gathered, passes, ct)).ConfigureAwait(false);
            var report = Aggregate(ctx, req, spec, plan, gathered, verified);
            return await ctx.StageAsync("persist", ct => PersistAsync(ctx, req, spec, report, ct)).ConfigureAwait(false);
        }

        // ── understand ───────────────────────────────────────────────────────────

        private async Task<InquirySpec> UnderstandAsync(TacticRunContext ctx, InquiryRequest req, CancellationToken ct)
        {
            var zone = kit.Context.Settings.KlivesZone;
            var given = req.PersonIds.Select(kit.Corpus.Canonical).Distinct(StringComparer.Ordinal).ToList();
            var candidates = given.Count > 0 ? given.Select(p => Candidate(p)).ToList() : FindCandidates(req.Question);
            foreach (var selfId in kit.Corpus.SelfPersonIds())
                if (!candidates.Any(c => c.PersonId == selfId)) candidates.Add(new PersonCandidateDto(selfId, "Klives (the operator)", "self"));
            var sb = new StringBuilder();
            sb.Append("CANDIDATES (people in the archive whose names match the question)\n");
            foreach (var c in candidates) sb.Append("- ").Append(c.PersonId).Append(": ").Append(EvidenceRenderer.Sanitize(c.Label)).Append(c.Detail == null ? "" : " (" + EvidenceRenderer.Sanitize(c.Detail) + ")").Append('\n');
            var asOfLocal = Zones.ToLocal(ctx.AsOfMs, zone);
            sb.Append("\nThe inquiry is as of ").Append(asOfLocal.ToString("yyyy-MM-dd dddd", CultureInfo.InvariantCulture)).Append(" in Klives's zone ").Append(zone.Id).Append(".\n");
            sb.Append("\nQUESTION\n").Append(EvidenceRenderer.FenceOpen).Append('\n').Append(EvidenceRenderer.Sanitize(req.Question)).Append('\n').Append(EvidenceRenderer.FenceClose);
            StructuredAnswer<UnderstandAnswer> answer;
            using (var s = Llm.OpenSession(KnowledgeSchema.InquiryModule, "inquiry.understand", SessionKey(ctx, req, "u"), InquiryPrompts.UnderstandSystem, sb.ToString(),
                       WorkClass(req), InquiryPrompts.UnderstandVersion, null))
            {
                answer = await StructuredLlm.AskAsync<UnderstandAnswer>(s, "Structure this inquiry.", InquiryPrompts.UnderstandSchema, 1_500,
                    new ModelSamplingParameters(Temperature: 0.0, Seed: 1), a => string.IsNullOrWhiteSpace(a.Restatement) ? "restatement is required" : null, ct).ConfigureAwait(false);
            }
            var u = answer.Value ?? new UnderstandAnswer { Restatement = req.Question };
            var subjects = new List<InquirySubjectRef>();
            var ambiguous = new List<DisambiguationDto>();
            var self = kit.Corpus.SelfPersonIds().ToHashSet(StringComparer.Ordinal);
            if (given.Count > 0)
                subjects.AddRange(given.Select(p => new InquirySubjectRef(p, Label(p), self.Contains(p))));
            else
            {
                foreach (var s in u.Subjects)
                {
                    if (s.PersonId != null && candidates.Any(c => c.PersonId == s.PersonId))
                    {
                        string p = kit.Corpus.Canonical(s.PersonId);
                        if (!subjects.Any(x => x.PersonId == p)) subjects.Add(new InquirySubjectRef(p, Label(p), self.Contains(p)));
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(s.Mention)) continue;
                    var matches = FindCandidates(s.Mention).Where(c => !self.Contains(c.PersonId)).ToList();
                    if (matches.Count == 1) subjects.Add(new InquirySubjectRef(matches[0].PersonId, Label(matches[0].PersonId), false));
                    else if (matches.Count > 1) ambiguous.Add(new DisambiguationDto(s.Mention, matches));
                }
                if (u.AboutKlives) foreach (var p in self) if (!subjects.Any(x => x.PersonId == p)) subjects.Add(new InquirySubjectRef(p, "Klives", true));
            }

            long asOf = ctx.AsOfMs;
            long? from = req.FromMs;
            var scopeFrom = Verifier.ParseBound(u.ScopeFrom, zone);
            var scopeTo = Verifier.ParseBound(u.ScopeTo, zone);
            if (scopeTo == null && scopeFrom == null && !string.IsNullOrWhiteSpace(u.TimeScope))
            {
                // The deterministic resolver reads "in 2021" / "last summer" against the inquiry's as-of in Klives's zone.
                var det = TemporalResolver.Resolve(u.TimeScope, asOf, zone).Select(r => r.Interval).FirstOrDefault(i => i != null);
                if (det != null) { scopeFrom = det; scopeTo = det; }
            }
            if (scopeTo is { } st && st.LatestMs < asOf) asOf = st.LatestMs;
            if (scopeFrom is { } sf && sf.EarliestMs != long.MinValue) from ??= sf.EarliestMs;

            // Inactive subjects are answered as of their last activity.
            string? note = null;
            if (subjects.Count == 1 && !subjects[0].IsKlives)
            {
                var identities = kit.Corpus.IdentitiesOf(kit.Corpus.PersonIdsOf(subjects[0].PersonId)).Select(i => i.IdentityId).ToList();
                var last = kit.Corpus.RecentAuthoredBy(identities, asOf, 1).FirstOrDefault();
                if (last != null && asOf - last.SentAtMs > 180 * 86_400_000L)
                    note = $"{subjects[0].Label} has been inactive since {Instant.Iso(last.SentAtMs)[..10]}; the answer is as of then.";
            }

            var type = req.AnswerType ?? ParseType(u.AnswerType);
            var options = req.Options.Count > 0 ? req.Options : u.Options;
            if (type == InquiryAnswerType.Choice && options.Count < 2) type = InquiryAnswerType.Open;
            if (type is InquiryAnswerType.Ranking && subjects.Count < 2) type = InquiryAnswerType.Binary;
            return new InquirySpec
            {
                Question = req.Question, Restatement = (u.Restatement ?? req.Question).Trim(), AnswerType = type, Options = options.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Subjects = subjects, AsOfMs = asOf, FromMs = from, TimeScope = u.TimeScope, Conditions = u.Conditions,
                SensitiveCategory = SensitiveTraits.IsSensitive(u.SensitiveCategory ?? "") ? u.SensitiveCategory : null,
                RestrictedAttribute = string.IsNullOrWhiteSpace(u.RestrictedAttribute) ? null : u.RestrictedAttribute, RoutineInference = u.RoutineInference,
                AboutKlives = u.AboutKlives, NeedsWorldKnowledge = u.NeedsWorldKnowledge, Topic = u.Topic, Ambiguous = ambiguous, AsOfNote = note,
            };
        }

        private static InquiryAnswerType ParseType(string? t) => (t ?? "").Trim().ToLowerInvariant() switch
        {
            "choice" => InquiryAnswerType.Choice,
            "ranking" => InquiryAnswerType.Ranking,
            "numeric" => InquiryAnswerType.Numeric,
            "date_interval" => InquiryAnswerType.DateInterval,
            "open" => InquiryAnswerType.Open,
            "change_point" => InquiryAnswerType.ChangePoint,
            "prediction" => InquiryAnswerType.Prediction,
            _ => InquiryAnswerType.Binary,
        };

        private string Label(string person) => kit.Corpus.IsSelf(person) ? "Klives" : kit.Corpus.DisplayName(person) ?? person;

        private PersonCandidateDto Candidate(string person)
        {
            var identities = kit.Corpus.IdentitiesOf(kit.Corpus.PersonIdsOf(person)).Select(i => i.IdentityId).ToList();
            var names = kit.Corpus.NamesOf(identities).OrderByDescending(n => n.ValidFromMs).Select(n => n.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(4);
            return new PersonCandidateDto(person, Label(person), string.Join(" / ", names) + " · " + kit.Corpus.TierOf(person));
        }

        /// <summary>People whose names or confirmed/accepted aliases appear in the text (names are time-scoped; every period counts).</summary>
        public List<PersonCandidateDto> FindCandidates(string text)
        {
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var word in text.Split(new[] { ' ', ',', '.', '?', '!', ';', ':', '"', '\'', '(', ')', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (word.Length >= 2) tokens.Add(word.TrimStart('@'));
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 1 < words.Length; i++) tokens.Add((words[i] + " " + words[i + 1]).Trim(',', '.', '?', '!', '\'', '"'));
            var found = new Dictionary<string, PersonCandidateDto>(StringComparer.Ordinal);
            foreach (var t in tokens)
            {
                foreach (var (person, name, _, _) in kit.Corpus.FindByName(t, 20))
                    found.TryAdd(person, Candidate(person));
                using var c = kit.Context.Stores.Intel.OpenRead();
                foreach (var p in c.Query(null, "SELECT DISTINCT person_id FROM kn_aliases WHERE alias_norm=$a AND status IN ('accepted','confirmed')", r => r.GetString(0), ("$a", NaturalKeys.Normalize(t))))
                    found.TryAdd(kit.Corpus.Canonical(p), Candidate(kit.Corpus.Canonical(p)));
            }
            return found.Values.Take(30).ToList();
        }

        // ── policy ───────────────────────────────────────────────────────────────

        private string? Policy(InquirySpec spec, long now)
        {
            if (spec.SensitiveCategory != null && !SensitiveTraits.AllowsInference(spec.SensitiveCategory)) return "sensitive";
            foreach (var s in spec.Subjects)
            {
                var band = kit.Context.Policy.BandOf(s.PersonId, now);
                if (!MinorGuard.IsRestricted(band)) continue;
                if (spec.RestrictedAttribute != null || spec.RoutineInference) return "minor:" + s.Label + ":" + band;
            }
            return null;
        }

        // ── plan ─────────────────────────────────────────────────────────────────

        private async Task<InquiryPlan> PlanAsync(TacticRunContext ctx, InquiryRequest req, InquirySpec spec, CancellationToken ct)
        {
            var sb = new StringBuilder();
            sb.Append("QUESTION: ").Append(EvidenceRenderer.Sanitize(spec.Restatement)).Append('\n');
            sb.Append("ANSWER TYPE: ").Append(spec.AnswerType).Append(spec.Options.Count > 0 ? " (options: " + string.Join(" | ", spec.Options.Select(EvidenceRenderer.Sanitize)) + ")" : "").Append('\n');
            foreach (var s in spec.Subjects)
            {
                sb.Append("\nSUBJECT ").Append(EvidenceRenderer.Sanitize(s.Label)).Append(" — sample of their recent messages (for language and vocabulary):\n").Append(EvidenceRenderer.FenceOpen).Append('\n');
                var identities = kit.Corpus.IdentitiesOf(kit.Corpus.PersonIdsOf(s.PersonId)).Select(i => i.IdentityId).ToList();
                foreach (var m in kit.Corpus.RecentAuthoredBy(identities, spec.AsOfMs, 25).Where(m => !string.IsNullOrWhiteSpace(m.Content)))
                    sb.Append("- ").Append(EvidenceRenderer.Content(m.Content, _ => null, 200).Replace('\n', ' ')).Append('\n');
                sb.Append(EvidenceRenderer.FenceClose).Append('\n');
            }
            StructuredAnswer<PlanAnswer> answer;
            using (var s = Llm.OpenSession(KnowledgeSchema.InquiryModule, "inquiry.plan", SessionKey(ctx, req, "p"), InquiryPrompts.PlanSystem, sb.ToString(), WorkClass(req),
                       InquiryPrompts.PlanVersion, null))
            {
                answer = await StructuredLlm.AskAsync<PlanAnswer>(s, "Plan the evidence facets.", InquiryPrompts.PlanSchema, 2_500, new ModelSamplingParameters(Temperature: 0.2, Seed: 1),
                    a => a.Facets == null || a.Facets.Count == 0 ? "at least one facet is required" : null, ct).ConfigureAwait(false);
            }
            var facets = (answer.Value?.Facets ?? new List<FacetPlan>())
                .Where(f => f.Queries.Count > 0)
                .Select(f => new Facet((f.Name ?? "facet").Trim(), (f.Kind ?? "direct").Trim(), f.Queries.Where(q => !string.IsNullOrWhiteSpace(q)).Take(8).ToList(),
                    f.CounterQueries.Where(q => !string.IsNullOrWhiteSpace(q)).Take(6).ToList()))
                .Take(8).ToList();
            if (facets.Count == 0) facets.Add(new Facet("direct", "direct", new[] { spec.Restatement, spec.Topic ?? spec.Question }.Where(q => !string.IsNullOrWhiteSpace(q)).ToList()!, Array.Empty<string>()));
            double? prior = answer.Value?.BaseRate?.Probability is double p && p is >= 0 and <= 1 ? p : null;
            return new InquiryPlan(facets, prior, answer.Value?.BaseRate?.Rationale);
        }

        // ── gather ───────────────────────────────────────────────────────────────

        private async Task<GatherResult> GatherAsync(InquirySpec spec, InquiryPlan plan, InquiryRequest req, CancellationToken ct)
        {
            var candidates = new Dictionary<string, (double Score, string Facet, string Reliability, bool Counter, string? Subject)>(StringComparer.Ordinal);
            var notes = new List<string>();
            int considered = 0;
            bool vectors = false;
            var platforms = new HashSet<string>(StringComparer.Ordinal);
            void Add(IReadOnlyList<RetrievedMessage> results, string facet, string reliability, bool counter, string? subject, double weight)
            {
                double max = results.Count == 0 ? 1 : results.Max(r => r.Score);
                foreach (var r in results)
                {
                    double score = weight * (max <= 0 ? 0 : r.Score / max);
                    if (!candidates.TryGetValue(r.MessageId, out var e) || e.Score < score) candidates[r.MessageId] = (score, facet, reliability, counter, subject);
                }
            }
            foreach (var subject in spec.Subjects)
            {
                var identities = kit.Corpus.IdentitiesOf(kit.Corpus.PersonIdsOf(subject.PersonId)).Select(i => i.IdentityId).ToList();
                // The sixty conversations they have lately been most active in (from their newest messages, a bounded read).
                var conversations = kit.Corpus.ConversationsOf(identities, newest: 5_000).Take(60).Select(c => c.ConversationId).ToList();
                List<string> reacted;
                using (var c = kit.Context.Stores.Corpus.OpenRead())
                    reacted = c.QueryIn(null, "SELECT message_id, added_at_ms FROM reactions WHERE identity_id IN ({keys}) ORDER BY added_at_ms DESC LIMIT 2000",
                            identities, r => (Id: r.GetString(0), At: r.GetInt64OrNull(1)))
                        .Where(x => x.At == null || x.At <= spec.AsOfMs).Select(x => x.Id).Distinct(StringComparer.Ordinal).Take(800).ToList();
                foreach (var facet in plan.Facets)
                {
                    ct.ThrowIfCancellationRequested();
                    var own = new RetrievalScope { AuthorIdentityIds = identities, FromMs = spec.FromMs, ToMs = spec.AsOfMs, Limit = 14 };
                    var (selfHits, cov) = await kit.Retriever.SearchAsync(facet.Queries, own, ct).ConfigureAwait(false);
                    vectors |= cov.Vectors;
                    if (cov.Note != null && !notes.Contains(cov.Note)) notes.Add(cov.Note);
                    Add(selfHits, facet.Name, "self", false, subject.PersonId, 1.0);
                    if (facet.CounterQueries.Count > 0)
                    {
                        var (counter, _) = await kit.Retriever.SearchAsync(facet.CounterQueries, own with { Limit = 8 }, ct).ConfigureAwait(false);
                        Add(counter, facet.Name, "self", true, subject.PersonId, 0.95);
                    }
                    if (conversations.Count > 0)
                    {
                        var theirs = new RetrievalScope { ConversationIds = conversations, FromMs = spec.FromMs, ToMs = spec.AsOfMs, Limit = 10 };
                        var (others, _) = await kit.Retriever.SearchAsync(facet.Queries, theirs, ct).ConfigureAwait(false);
                        Add(others, facet.Name, "third_party", false, subject.PersonId, 0.6);
                    }
                    if (reacted.Count > 0)
                    {
                        var (liked, _) = await kit.Retriever.SearchAsync(facet.Queries, new RetrievalScope { MessageIds = reacted, ToMs = spec.AsOfMs, Limit = 6 }, ct).ConfigureAwait(false);
                        Add(liked, facet.Name, "behaviour", false, subject.PersonId, 0.8);
                    }
                }
            }
            var messages = kit.Corpus.Messages(candidates.Keys.ToList()).ToDictionary(m => m.MessageId, StringComparer.Ordinal);
            considered = messages.Count;
            var subjectsIdentityPerson = spec.Subjects.ToDictionary(s => s.PersonId, s => s.Label, StringComparer.Ordinal);
            var items = new List<(double Score, EvidenceItem Item, string Print)>();
            int excludedJoke = 0, excludedLater = 0;
            var conversationsInfo = kit.Corpus.Conversations(messages.Values.Select(m => m.ConversationId).Distinct(StringComparer.Ordinal).ToList());
            foreach (var (id, c) in candidates)
            {
                if (!messages.TryGetValue(id, out var m)) continue;
                if (m.SentAtMs > spec.AsOfMs) { excludedLater++; continue; }
                var opinion = kit.Modality.Get(m.MessageId, m.Version);
                if (opinion is { NonLiteralProbability: >= 0.8 } || kit.Fiction.FictionProbability(m.ConversationId) >= 0.7) { excludedJoke++; continue; }
                string reliability = c.Reliability;
                if (reliability == "third_party" && m.AuthorPersonId != null && subjectsIdentityPerson.ContainsKey(m.AuthorPersonId)) reliability = "self";
                if (reliability == "self" && (m.ForwardedJson != null || m.IsBot || m.IsWebhook)) reliability = "third_party";
                double recency = new DecayParameters(365, 0.3).Weight(m.SentAtMs, spec.AsOfMs);
                double score = c.Score * recency;
                conversationsInfo.TryGetValue(m.ConversationId, out var conv);
                string where = conv == null ? m.Platform : (conv.Kind == "dm" ? "DM" : conv.Kind == "group_dm" ? "group chat" : (conv.GuildName ?? "server") + (conv.Title == null ? "" : " #" + conv.Title)) + " · " + m.Platform;
                platforms.Add(m.Platform);
                string author = m.AuthorPersonId != null ? Label(m.AuthorPersonId) : "unknown";
                string text = EvidenceRenderer.Content(m.Content, _ => null, 800);
                var zone = m.AuthorPersonId != null ? kit.Zones.ZoneAt(m.AuthorPersonId, m.SentAtMs) : null;
                string rendered = $"{EvidenceRenderer.Stamp("@", m.SentAtMs, m.Quality, zone)} {EvidenceRenderer.Sanitize(author)} in {EvidenceRenderer.Sanitize(where)} [{reliability}{(c.Counter ? ", counter-evidence search" : "")}{(m.DeletedAtMs != null ? ", later deleted" : "")}]: {text}";
                if (reliability == "behaviour") rendered = $"{EvidenceRenderer.Stamp("@", m.SentAtMs, m.Quality, zone)} {EvidenceRenderer.Sanitize(Label(c.Subject!))} reacted to a message by {EvidenceRenderer.Sanitize(author)} in {EvidenceRenderer.Sanitize(where)} [behaviour]: {text}";
                items.Add((score, new EvidenceItem("", reliability == "behaviour" ? "reaction" : "message", m.MessageId, null, c.Subject, reliability == "behaviour" ? c.Subject : m.AuthorPersonId,
                    m.SentAtMs, rendered, c.Facet, reliability, Math.Round(score, 4), c.Counter, m.ConversationId, m.GuildId, m.Platform, where), (m.AuthorPersonId ?? "") + "|" + TextMatch.Fingerprint(m.Content)));
            }
            // Facts (stated; inferences are labelled and never treated as evidence of themselves).
            int facts = 0;
            foreach (var subject in spec.Subjects)
            {
                var views = kit.Assertions.Query(new AssertionQuery
                {
                    SubjectIds = kit.Corpus.PersonIdsOf(subject.PersonId), AtMs = spec.AsOfMs, Mode = spec.AsOfMs < kit.Context.Clock.NowMs - 86_400_000L ? KnowledgeMode.AsOfWorld : KnowledgeMode.Current,
                    EvidenceCutoffMs = spec.AsOfMs, Statuses = new[] { AssertionStatus.Accepted, AssertionStatus.Confirmed }, IncludeInferred = false, Limit = 400,
                });
                var band = kit.Context.Policy.BandOf(subject.PersonId, spec.AsOfMs);
                foreach (var v in views)
                {
                    if (Attributes.IsBlockedFor(v.Record.Attribute, band) || Attributes.IsSensitive(v.Record.Attribute)) continue;
                    facts++;
                    var r = v.Record;
                    string validity = (r.ValidFromLatestMs is long vf ? " from ~" + Instant.Iso(vf)[..10] : "") + (r.ValidToLatestMs is long vt ? " until ~" + Instant.Iso(vt)[..10] : "");
                    string rendered = $"[fact about {EvidenceRenderer.Sanitize(subject.Label)}] {v.Definition.Title}: {(r.Value.Polarity == Polarity.Negated ? "NOT " : "")}{EvidenceRenderer.Sanitize(r.ValueText ?? r.Value.Text)}{validity} (last evidence {Instant.Iso(r.AssertedAtMs ?? r.RecordedAtMs)[..10]}, confidence {v.EffectiveProbability:0.00}{(r.Status == AssertionStatus.Confirmed ? ", confirmed by Klives" : "")})";
                    items.Add((0.9 * v.EffectiveProbability, new EvidenceItem("", "fact", null, r.AssertionId, subject.PersonId, subject.PersonId, r.AssertedAtMs ?? r.RecordedAtMs, rendered, "facts", "fact",
                        Math.Round(0.9 * v.EffectiveProbability, 4), false, null, null, null, null), "fact|" + r.AssertionId));
                }
            }
            foreach (var extra in req.ExtraEvidence) items.Add((extra.Score, extra, "extra|" + extra.Text.GetHashCode()));
            // Near-duplicates (the same wording by the same author) fold into the best-scored copy.
            var folded = items.GroupBy(i => i.Print, StringComparer.Ordinal).Select(g => g.OrderByDescending(x => x.Score).First())
                .OrderByDescending(i => i.Score).ThenBy(i => i.Item.AtMs).ThenBy(i => i.Item.MessageId ?? i.Item.AssertionId, StringComparer.Ordinal).Take(400).ToList();
            var numbered = folded.Select((x, i) => x.Item with { Id = "E" + (i + 1).ToString(CultureInfo.InvariantCulture) }).ToList();
            if (!vectors) notes.Add("Vector search was not available; evidence comes from full-text search and facts.");
            notes.Add("Structured activity (presence, games, music) is not consulted by inquiries yet; it arrives with the Explore engine.");
            var coverage = new CoverageDto(spec.Subjects.Select(s => s.Label).ToList(), spec.FromMs, spec.AsOfMs, considered, facts, numbered.Count, excludedJoke, excludedLater, vectors,
                platforms.OrderBy(p => p, StringComparer.Ordinal).ToList(), notes);
            return new GatherResult(numbered, coverage);
        }

        // ── pack + reason ────────────────────────────────────────────────────────

        private (string Prefix, IReadOnlyList<string> Included) Dossier(InquirySpec spec, GatherResult g, int tokenBudget, long byteBudget)
        {
            var header = new List<string>
            {
                "QUESTION: " + EvidenceRenderer.Sanitize(spec.Restatement),
                "AS OF: " + Instant.Iso(spec.AsOfMs)[..10] + (spec.AsOfNote == null ? "" : " — " + EvidenceRenderer.Sanitize(spec.AsOfNote)),
                "SUBJECTS: " + string.Join(", ", spec.Subjects.Select(s => EvidenceRenderer.Sanitize(s.Label) + (s.IsKlives ? " (Klives, the operator)" : ""))),
            };
            if (spec.Options.Count > 0) header.Add("OPTIONS: " + string.Join(" | ", spec.Options.Select(EvidenceRenderer.Sanitize)));
            if (spec.Conditions.Count > 0) header.Add("CONDITIONS: " + string.Join("; ", spec.Conditions.Select(EvidenceRenderer.Sanitize)));
            var facts = g.Items.Where(i => i.Kind == "fact").Select(i => i.Id + " " + i.Text).ToList();
            var evidence = g.Items.Where(i => i.Kind != "fact").Select(i => i.Id + " " + i.Text).ToList();
            var pack = ContextPacker.Pack(new[]
            {
                new PackSection("header", 100, header, PackStrategy.Required),
                new PackSection("facts", 60, facts, PackStrategy.KeepOldest, "KNOWN FACTS (verified from earlier conversations)"),
                new PackSection("evidence", 50, evidence, PackStrategy.KeepOldest, "EVIDENCE (most relevant first)\n" + EvidenceRenderer.FenceOpen),
                new PackSection("fence", 40, new[] { EvidenceRenderer.FenceClose }, PackStrategy.Required),
            }, tokenBudget, Llm.Estimator, byteBudget);
            var included = new List<string>();
            int nf = pack.Coverage["facts"].Included, ne = pack.Coverage["evidence"].Included;
            included.AddRange(g.Items.Where(i => i.Kind == "fact").Take(nf).Select(i => i.Id));
            included.AddRange(g.Items.Where(i => i.Kind != "fact").Take(ne).Select(i => i.Id));
            return (pack.Text, included);
        }

        private async Task<List<PassResult>> ReasonAsync(TacticRunContext ctx, InquiryRequest req, InquirySpec spec, GatherResult g, CancellationToken ct)
        {
            var info = await Llm.ContextAsync(null, ct).ConfigureAwait(false);
            int budget = ContextPacker.Budget(info, 4_000) - Llm.Estimator.Estimate(InquiryPrompts.ReasonSystem) - Llm.Estimator.Estimate(InquiryPrompts.ReasonSchema.Schema.ToString()) - 1_500;
            var (prefix, included) = Dossier(spec, g, Math.Max(2_000, budget), ContextPacker.ByteBudget(info));
            int k = Math.Clamp(req.Passes, 1, 9);
            var results = new List<PassResult>();
            string key = SessionKey(ctx, req, "r");
            for (int i = 0; i < k; i++)
            {
                ct.ThrowIfCancellationRequested();
                var order = Shuffle(included, i);
                string instruction = InquiryPrompts.AnswerInstruction(spec, order) + (req.Framing == null ? "" : "\n" + req.Framing);
                bool last = i == k - 1;
                // A session per pass, all with the same key, dossier and schemas: the provider prefills the dossier for the
                // first pass and serves it from cache for the rest. (Not one session for all: each pass lists the evidence
                // in its own reading order, and AIRouter's scheduler would measure a pass against the whole of the one
                // before it, reading order included.)
                using var s = Llm.OpenSession(KnowledgeSchema.InquiryModule, "inquiry.reason", key, InquiryPrompts.ReasonSystem, prefix, WorkClass(req), InquiryPrompts.ReasonVersion, null,
                    schemas: InquiryPrompts.ReasonSchemas);
                var answer = await StructuredLlm.AskAsync<PassAnswer>(s, instruction, InquiryPrompts.ReasonSchema, 3_000, new ModelSamplingParameters(Temperature: i == 0 ? 0.3 : 0.8, Seed: 100 + i),
                    a => a.Probability is null or < 0 or > 1 ? "probability must be between 0 and 1" : null, ct).ConfigureAwait(false);
                if (answer.Value != null) results.Add(new PassResult(i, answer.Value, answer.Result?.Model));
                await ctx.ReportAsync(new { stage = "pass", pass = i + 1, of = k, answer = answer.Value?.Answer, probability = answer.Value?.Probability }).ConfigureAwait(false);
                if (last && results.Count > 0)
                {
                    // The devil's advocate argues the opposite of the last pass's answer: a follow-up to it, on top of the dossier.
                    var devil = await StructuredLlm.AskAsync<AdvocateAnswer>(s, InquiryPrompts.AdvocateQuestion, InquiryPrompts.AdvocateSchema, 1_500,
                        new ModelSamplingParameters(Temperature: 0.5, Seed: 999), a => a.Argument == null ? "argument is required" : null, ct, followUp: true).ConfigureAwait(false);
                    if (devil.Value != null)
                        results.Add(new PassResult(-1, new PassAnswer { Answer = "devil", Reasoning = devil.Value.Argument, EvidenceFor = devil.Value.Cites, Distribution = devil.Value.Strength == null ? null : new() { [devil.Value.Strength] = 1 } },
                            devil.Result?.Model, Devil: true));
                }
            }
            return results;
        }

        private static List<string> Shuffle(IReadOnlyList<string> ids, int seed)
        {
            var list = ids.ToList();
            if (seed == 0) return list;
            var rng = new Random(seed * 7919);
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
            return list;
        }

        // ── verify ───────────────────────────────────────────────────────────────

        private async Task<List<PassResult>> VerifyAsync(TacticRunContext ctx, InquiryRequest req, InquirySpec spec, GatherResult g, List<PassResult> passes, CancellationToken ct)
        {
            var valid = g.Items.Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            static string Norm(string id) => id.Trim().Trim('[', ']').ToUpperInvariant();
            // Citations that name no dossier item are hallucinated: removed before anything else.
            var cleaned = passes.Select(p => p with
            {
                Answer = new PassAnswer
                {
                    Answer = p.Answer.Answer, Probability = p.Answer.Probability, Distribution = p.Answer.Distribution, Estimate = p.Answer.Estimate, Low = p.Answer.Low, High = p.Answer.High,
                    ChangeFrom = p.Answer.ChangeFrom, ChangeTo = p.Answer.ChangeTo, Scores = p.Answer.Scores, Reasoning = p.Answer.Reasoning, TemporalNote = p.Answer.TemporalNote,
                    WouldChange = p.Answer.WouldChange, QuestionsToAsk = p.Answer.QuestionsToAsk, UsedWorldKnowledge = p.Answer.UsedWorldKnowledge, ExcludedAsJoke = p.Answer.ExcludedAsJoke,
                    EvidenceFor = p.Answer.EvidenceFor.Select(Norm).Where(valid.Contains).Distinct().ToList(),
                    EvidenceAgainst = p.Answer.EvidenceAgainst.Select(Norm).Where(valid.Contains).Distinct().ToList(),
                },
            }).ToList();
            var toCheck = cleaned.Where(p => !p.Devil).SelectMany(p => p.Answer.EvidenceFor.Select(id => (id, $"supports the answer \"{p.Answer.Answer}\""))
                    .Concat(p.Answer.EvidenceAgainst.Select(id => (id, $"argues against the answer \"{p.Answer.Answer}\""))))
                .Concat(cleaned.Where(p => p.Devil).SelectMany(p => p.Answer.EvidenceFor.Select(id => (id, "supports the opposite of the most likely answer"))))
                .Distinct().Take(80).ToList();
            var unsupported = new HashSet<(string, string)>();
            if (toCheck.Count > 0)
            {
                var info = await Llm.ContextAsync(null, ct).ConfigureAwait(false);
                int budget = ContextPacker.Budget(info, 4_000) - Llm.Estimator.Estimate(InquiryPrompts.ReasonSystem) - Llm.Estimator.Estimate(InquiryPrompts.ReasonSchema.Schema.ToString()) - 1_500;
                var (prefix, _) = Dossier(spec, g, Math.Max(2_000, budget), ContextPacker.ByteBudget(info));
                // Same key, prefix and schemas as the passes: the dossier is served from the prefix cache.
                using var s = Llm.OpenSession(KnowledgeSchema.InquiryModule, "inquiry.verify", SessionKey(ctx, req, "r"), InquiryPrompts.ReasonSystem, prefix, WorkClass(req), InquiryPrompts.VerifyVersion, null,
                    schemas: InquiryPrompts.ReasonSchemas);
                var checks = await StructuredLlm.AskAsync<CitationVerdicts>(s, InquiryPrompts.VerifyQuestion(toCheck), InquiryPrompts.VerifySchema, 4_000,
                    new ModelSamplingParameters(Temperature: 0.0, Seed: 1), a => a.Checks == null ? "checks is required" : null, ct).ConfigureAwait(false);
                if (checks.Value != null)
                {
                    foreach (var c in checks.Value.Checks.Where(c => !c.Supports && c.Id != null))
                    {
                        string id = Norm(c.Id!);
                        foreach (var (cid, point) in toCheck.Where(t => t.id == id && (c.Point == null || t.Item2 == c.Point || t.Item2.Contains(c.Point, StringComparison.OrdinalIgnoreCase))))
                            unsupported.Add((cid, point));
                    }
                }
            }
            var verified = new List<PassResult>();
            foreach (var p in cleaned)
            {
                string forPoint = p.Devil ? "supports the opposite of the most likely answer" : $"supports the answer \"{p.Answer.Answer}\"";
                string againstPoint = $"argues against the answer \"{p.Answer.Answer}\"";
                p.Answer.EvidenceFor = p.Answer.EvidenceFor.Where(id => !unsupported.Contains((id, forPoint))).ToList();
                p.Answer.EvidenceAgainst = p.Answer.EvidenceAgainst.Where(id => !unsupported.Contains((id, againstPoint))).ToList();
                // A pass that cites nothing verifiable is dropped (plan step 6); the advocate is kept to report its case.
                if (!p.Devil && p.Answer.EvidenceFor.Count + p.Answer.EvidenceAgainst.Count == 0) continue;
                verified.Add(p);
            }
            await ctx.ReportAsync(new { stage = "verified", passes = verified.Count(p => !p.Devil), dropped = passes.Count(p => !p.Devil) - verified.Count(p => !p.Devil) }).ConfigureAwait(false);
            return verified;
        }

        // ── aggregate + report ───────────────────────────────────────────────────

        private InquiryReportDto Aggregate(TacticRunContext ctx, InquiryRequest req, InquirySpec spec, InquiryPlan plan, GatherResult g, List<PassResult> verified)
        {
            var passes = verified.Where(p => !p.Devil).ToList();
            var devil = verified.FirstOrDefault(p => p.Devil);
            var byId = g.Items.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);
            // Each pass cites what supports and what argues against its own answer. For a yes/no question the report's "for"
            // means for yes, so a pass that answered no has its two lists swapped: otherwise passes that disagree would put
            // evidence for both answers under "for".
            bool yesNo = spec.AnswerType is InquiryAnswerType.Binary or InquiryAnswerType.Prediction;
            bool Swapped(PassResult p) => yesNo && string.Equals(p.Answer.Answer?.Trim(), "no", StringComparison.OrdinalIgnoreCase);
            var citedFor = passes.SelectMany(p => Swapped(p) ? p.Answer.EvidenceAgainst : p.Answer.EvidenceFor).GroupBy(x => x).OrderByDescending(x => x.Count()).Select(x => byId[x.Key]).ToList();
            var citedAgainst = passes.SelectMany(p => Swapped(p) ? p.Answer.EvidenceFor : p.Answer.EvidenceAgainst).GroupBy(x => x).OrderByDescending(x => x.Count()).Select(x => byId[x.Key]).ToList();
            int strength = InquiryAggregation.Strength(citedFor.Concat(citedAgainst));
            int dropped = Math.Max(0, Math.Clamp(req.Passes, 1, 9) - passes.Count);
            string? devilStrength = devil?.Answer.Distribution?.Keys.FirstOrDefault();
            int devilCites = devil?.Answer.EvidenceFor.Count ?? 0;

            double? raw = null, probability = null, low = null, high = null, estimate = null;
            bool calibrated = false;
            double disagreement = 0;
            long? changeFrom = null, changeTo = null;
            var distribution = new List<ChoiceDto>();
            var ranking = new List<RankedPersonDto>();
            InquiryVerdict verdict;
            var ps = passes.Select(p => p.Answer.Probability ?? 0.5).ToList();
            switch (spec.AnswerType)
            {
                case InquiryAnswerType.Binary or InquiryAnswerType.Prediction:
                {
                    var yes = passes.Select(p => YesProbability(p.Answer)).ToList();
                    var (p, sd) = InquiryAggregation.Binary(yes, devilStrength, devilCites);
                    disagreement = sd;
                    raw = p;
                    (probability, calibrated) = kit.Calibration.ApplyScope(p, CalibrationStore.InquiryScope);
                    (low, high) = InquiryAggregation.Band(yes, probability.Value, calibrated);
                    verdict = passes.Count == 0 ? InquiryVerdict.InsufficientEvidence : InquiryAggregation.VerdictFor(probability.Value, strength);
                    break;
                }
                case InquiryAnswerType.Choice:
                    distribution = InquiryAggregation.Choice(passes.Select(p => p.Answer.Distribution ?? new Dictionary<string, double>()).ToList(), spec.Options).ToList();
                    raw = probability = distribution.FirstOrDefault()?.Probability;
                    disagreement = InquiryAggregation.StdDev(ps);
                    verdict = passes.Count == 0 || strength < InquiryAggregation.MinimumStrength ? InquiryVerdict.InsufficientEvidence : InquiryVerdict.Answered;
                    break;
                case InquiryAnswerType.Ranking:
                {
                    var labels = spec.Subjects.Select(s => s.Label).ToList();
                    var ranked = InquiryAggregation.Ranking(passes.Select(p => p.Answer.Scores ?? new Dictionary<string, double>()).ToList(), labels);
                    foreach (var (label, score, spread) in ranked)
                    {
                        var subject = spec.Subjects.First(s => s.Label == label);
                        var ev = citedFor.Concat(citedAgainst).Where(i => i.SubjectPersonId == subject.PersonId).Select(i => i.Id).Distinct().Take(6).ToList();
                        ranking.Add(new RankedPersonDto(subject.PersonId, label, double.IsNaN(score) ? 0 : score, spread > 0.2 ? "passes disagreed" : null, ev));
                    }
                    raw = probability = ranking.FirstOrDefault()?.Score;
                    disagreement = ranked.Count == 0 ? 0 : ranked.Average(r => r.Spread);
                    verdict = passes.Count == 0 || strength < InquiryAggregation.MinimumStrength ? InquiryVerdict.InsufficientEvidence : InquiryVerdict.Answered;
                    break;
                }
                case InquiryAnswerType.Numeric:
                {
                    var nums = passes.Where(p => p.Answer.Estimate != null).Select(p => (p.Answer.Estimate!.Value, p.Answer.Low, p.Answer.High)).ToList();
                    var (e, lo, hi) = InquiryAggregation.Numeric(nums);
                    estimate = double.IsNaN(e) ? null : e;
                    low = double.IsNaN(lo) ? null : lo;
                    high = double.IsNaN(hi) ? null : hi;
                    raw = probability = ps.Count == 0 ? null : InquiryAggregation.Median(ps);
                    verdict = nums.Count == 0 || strength < InquiryAggregation.MinimumStrength ? InquiryVerdict.InsufficientEvidence : InquiryVerdict.Answered;
                    break;
                }
                case InquiryAnswerType.ChangePoint or InquiryAnswerType.DateInterval:
                {
                    var zone = kit.Context.Settings.KlivesZone;
                    var intervals = passes.Select(p => (Verifier.ParseBound(p.Answer.ChangeFrom, zone)?.EarliestMs, Verifier.ParseBound(p.Answer.ChangeTo, zone)?.LatestMs)).ToList();
                    (changeFrom, changeTo) = InquiryAggregation.Interval(intervals);
                    raw = probability = ps.Count == 0 ? null : InquiryAggregation.Median(ps);
                    verdict = changeFrom == null && changeTo == null || strength < InquiryAggregation.MinimumStrength ? InquiryVerdict.InsufficientEvidence : InquiryVerdict.Answered;
                    break;
                }
                default:
                    raw = probability = ps.Count == 0 ? null : InquiryAggregation.Median(ps);
                    verdict = passes.Count == 0 || strength < InquiryAggregation.MinimumStrength ? InquiryVerdict.InsufficientEvidence : InquiryVerdict.Answered;
                    break;
            }
            var representative = passes.OrderBy(p => Math.Abs((p.Answer.Probability ?? 0.5) - (raw ?? 0.5))).FirstOrDefault();
            string? temporal = passes.Select(p => p.Answer.TemporalNote).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? TemporalShift(citedFor, citedAgainst);
            bool insufficient = verdict == InquiryVerdict.InsufficientEvidence;
            return new InquiryReportDto(
                InquiryId(ctx), ctx.RunId, spec.Question, spec.Restatement, spec.AnswerType, verdict,
                insufficient && spec.AnswerType is InquiryAnswerType.Binary or InquiryAnswerType.Prediction ? null : Round(probability), Round(raw), Round(low), Round(high), calibrated,
                distribution, ranking, estimate, changeFrom, changeTo,
                citedFor.Take(6).Select(ToDto).ToList(), citedAgainst.Take(6).Select(ToDto).ToList(),
                representative?.Answer.Reasoning, temporal,
                passes.SelectMany(p => p.Answer.WouldChange).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList(),
                passes.SelectMany(p => p.Answer.QuestionsToAsk).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList(),
                g.Coverage with { Notes = g.Coverage.Notes.Concat(passes.SelectMany(p => p.Answer.ExcludedAsJoke).Any() ? new[] { "Some evidence was set aside as jokes or roleplay by the reasoning passes." } : Array.Empty<string>()).ToList() },
                strength, passes.Count, dropped, Math.Round(disagreement, 4), devil?.Answer.Reasoning,
                insufficient || strength < 4 ? plan.PriorProbability : null, insufficient || strength < 4 ? plan.PriorRationale : null,
                spec.NeedsWorldKnowledge || passes.Any(p => p.Answer.UsedWorldKnowledge), spec.AnswerType == InquiryAnswerType.Prediction, null,
                Array.Empty<DisambiguationDto>(), spec.AsOfMs, spec.AsOfNote, InquiryPrompts.ReasonVersion, representative?.Model, null, req.StandingId);
        }

        private static double YesProbability(PassAnswer a)
        {
            double p = Math.Clamp(a.Probability ?? 0.5, 0, 1);
            // The schema asks for P(yes); a pass that answered "no" with its confidence in "no" is normalised.
            if (string.Equals(a.Answer?.Trim(), "no", StringComparison.OrdinalIgnoreCase) && p > 0.5) return 1 - p;
            return p;
        }

        /// <summary>A deterministic temporal note when the evidence for and against sit in different eras.</summary>
        private static string? TemporalShift(IReadOnlyList<EvidenceItem> forItems, IReadOnlyList<EvidenceItem> against)
        {
            if (forItems.Count < 2 || against.Count < 2) return null;
            double mf = InquiryAggregation.Median(forItems.Select(i => (double)i.AtMs).ToList());
            double ma = InquiryAggregation.Median(against.Select(i => (double)i.AtMs).ToList());
            if (Math.Abs(mf - ma) < 180 * 86_400_000.0) return null;
            return mf < ma
                ? $"The evidence for is mostly older (around {Instant.Iso((long)mf)[..7]}) and the evidence against newer (around {Instant.Iso((long)ma)[..7]}): this may have changed."
                : $"The evidence against is mostly older (around {Instant.Iso((long)ma)[..7]}) and the evidence for newer (around {Instant.Iso((long)mf)[..7]}): this may have changed.";
        }

        private static double? Round(double? v) => v is double d && double.IsFinite(d) ? Math.Round(d, 4) : null;

        private static InquiryEvidenceDto ToDto(EvidenceItem i) => new(i.Id, i.Kind, i.MessageId, i.AssertionId, i.AuthorPersonId, i.AtMs, i.Text.Length > 600 ? i.Text[..600] + "…" : i.Text,
            i.ConversationId, i.GuildId, i.Platform, i.Where, i.Reliability, i.Facet);

        private InquiryReportDto NeedsDisambiguation(TacticRunContext ctx, InquirySpec spec) => Empty(ctx, spec, InquiryVerdict.NeedsDisambiguation,
            spec.Ambiguous.Count > 0 ? "Several people match a name in the question: choose one and ask again." : "No person in the archive matches the question: name them or select them.") with
        { Disambiguation = spec.Ambiguous };

        private InquiryReportDto Refused(TacticRunContext ctx, InquirySpec spec, string reason)
        {
            if (reason == "sensitive")
            {
                // The person's own explicit statement may be quoted with its date; nothing is inferred.
                var quotes = new List<InquiryEvidenceDto>();
                foreach (var s in spec.Subjects)
                {
                    var own = kit.Assertions.Query(new AssertionQuery
                    {
                        SubjectIds = kit.Corpus.PersonIdsOf(s.PersonId), AtMs = spec.AsOfMs, Attribute = spec.SensitiveCategory, Statuses = new[] { AssertionStatus.Accepted, AssertionStatus.Confirmed },
                        IncludeInferred = false, Limit = 10,
                    }).Where(v => v.Record.SourceKind == ClaimSource.SelfDisclosure);
                    foreach (var v in own)
                        foreach (var e in kit.Assertions.Evidence(v.Record.AssertionId).Take(3))
                            quotes.Add(new InquiryEvidenceDto("Q" + (quotes.Count + 1).ToString(CultureInfo.InvariantCulture), "quote", e.MessageId, v.Record.AssertionId, s.PersonId,
                                e.SentAtMs ?? v.Record.AssertedAtMs ?? 0, e.Quote ?? "", null, null, null, null, "self", "explicit statement"));
                }
                string note = SensitiveTraits.RefusalText + (quotes.Count > 0 ? " Their own explicit statement is quoted with its date." : "");
                return Empty(ctx, spec, InquiryVerdict.Refused, note) with { EvidenceFor = quotes };
            }
            var parts = reason.Split(':');
            return Empty(ctx, spec, InquiryVerdict.Refused,
                $"{(parts.Length > 1 ? parts[1] : "This person")} is, or may be, under 18 ({(parts.Length > 2 ? parts[2] : "minor")}). Omniscience does not infer or show their real name, school, location, contacts, family or routines.");
        }

        private InquiryReportDto Empty(TacticRunContext ctx, InquirySpec spec, InquiryVerdict verdict, string note) => new(
            InquiryId(ctx), ctx.RunId, spec.Question, spec.Restatement, spec.AnswerType, verdict, null, null, null, null, false, Array.Empty<ChoiceDto>(), Array.Empty<RankedPersonDto>(),
            null, null, null, Array.Empty<InquiryEvidenceDto>(), Array.Empty<InquiryEvidenceDto>(), null, null, Array.Empty<string>(), Array.Empty<string>(),
            new CoverageDto(spec.Subjects.Select(s => s.Label).ToList(), spec.FromMs, spec.AsOfMs, 0, 0, 0, 0, 0, false, Array.Empty<string>(), Array.Empty<string>()),
            0, 0, 0, 0, null, null, null, false, false, note, Array.Empty<DisambiguationDto>(), spec.AsOfMs, spec.AsOfNote, InquiryPrompts.ReasonVersion, null, null, null);

        // ── persist ──────────────────────────────────────────────────────────────

        public static string InquiryId(TacticRunContext ctx) => NaturalKeys.Id("inq", ctx.RunId, 20);

        private async Task<InquiryReportDto> PersistAsync(TacticRunContext ctx, InquiryRequest req, InquirySpec spec, InquiryReportDto report, CancellationToken ct)
        {
            long now = kit.Context.Clock.NowMs;
            string? assertionId = null;
            string? primary = spec.Subjects.FirstOrDefault()?.PersonId;
            bool answerable = report.Verdict is not (InquiryVerdict.Refused or InquiryVerdict.NeedsDisambiguation or InquiryVerdict.InsufficientEvidence);
            if (answerable && primary != null && report.Probability is double p)
            {
                // An inquiry answer is an inferred assertion (never a fact), with its evidence, that Klives can confirm or reject.
                var evidence = report.EvidenceFor.Concat(report.EvidenceAgainst).Where(e => e.MessageId != null)
                    .Select(e => new EvidenceRef(e.MessageId!, MessageVersion(e.MessageId!), report.EvidenceFor.Contains(e) ? EvidenceRoles.Support : EvidenceRoles.Contra, null, 1, e.AtMs))
                    .GroupBy(e => (e.MessageId, e.Role)).Select(gr => gr.First()).ToList();
                if (evidence.Count > 0)
                {
                    string value = $"{spec.Restatement} → {Describe(report)}";
                    var draft = new AssertionDraft
                    {
                        SubjectKind = "person", SubjectId = primary, Attribute = "inquiry_answer", Value = new AssertionValue(value, NaturalKeys.Normalize(spec.Restatement), Polarity.Affirmed,
                            JsonSerializer.SerializeToElement(new { inquiryId = report.InquiryId, verdict = report.Verdict.ToString(), report.AnswerType }, KernelJson.Options)),
                        Episode = report.InquiryId, AssertedAtMs = evidence.Max(e => e.SentAtMs ?? 0), Status = AssertionStatus.Inferred, SourceKind = ClaimSource.Inferred,
                        Probability = Math.Clamp(p, 0, 1), Calibrated = report.Calibrated, Producer = Producers.Inquiry, ModuleVersion = 1, PromptVersion = InquiryPrompts.ReasonVersion,
                        Model = report.Model, SourceSeq = kit.Corpus.MaxSeq(), Derivation = new { kind = "inquiry", runId = ctx.RunId, asOfMs = spec.AsOfMs },
                        Evidence = evidence, Dims = new AssertionDims(spec.AsOfMs, evidence.Min(e => e.SentAtMs ?? spec.AsOfMs), primary, null, null, null, null, null, evidence.Count, report.EvidenceStrength, ctx.RunId),
                    };
                    var result = await kit.Assertions.ReconcileAsync(KnowledgeSchema.InquiryModule, Producers.Inquiry, "person", new[] { primary }, new[] { draft }, keepUnlisted: _ => true, ct: ct).ConfigureAwait(false);
                    assertionId = result.InsertedIds.FirstOrDefault() ?? ExistingAssertion(draft.NaturalKey);
                }
            }
            string? standingId = req.StandingId;
            if (req.Standing && standingId == null && report.Verdict is not (InquiryVerdict.Refused or InquiryVerdict.NeedsDisambiguation))
                standingId = await CreateStandingAsync(ctx, req, spec, report, ct).ConfigureAwait(false);
            var final = report with { AssertionId = assertionId, StandingId = standingId };
            string state = report.Verdict switch
            {
                InquiryVerdict.Refused => "refused",
                InquiryVerdict.NeedsDisambiguation => "needs_disambiguation",
                InquiryVerdict.InsufficientEvidence => "insufficient",
                _ => "answered",
            };
            await kit.Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO kn_inquiries(inquiry_id, run_id, tactic_key, question, restatement, answer_type, subjects_json, primary_subject_id, as_of_ms, verdict, probability_raw,
                    probability, band_low, band_high, evidence_strength, evidence_count, report_json, assertion_id, standing_id, model, prompt_version, state, created_at_ms, finished_at_ms, source_seq)
                VALUES($id,$run,$t,$q,$r,$at,$subj,$ps,$asof,$v,$raw,$p,$lo,$hi,$str,$ec,$rep,$as,$st,$m,$pv,$state,$n,$n,$seq)
                ON CONFLICT(inquiry_id) DO UPDATE SET verdict=excluded.verdict, probability_raw=excluded.probability_raw, probability=excluded.probability, band_low=excluded.band_low,
                    band_high=excluded.band_high, evidence_strength=excluded.evidence_strength, evidence_count=excluded.evidence_count, report_json=excluded.report_json,
                    assertion_id=excluded.assertion_id, standing_id=excluded.standing_id, model=excluded.model, state=excluded.state, finished_at_ms=excluded.finished_at_ms
                """, ("$id", final.InquiryId), ("$run", ctx.RunId), ("$t", req.TacticKey), ("$q", spec.Question), ("$r", spec.Restatement), ("$at", spec.AnswerType.ToString()),
                ("$subj", JsonSerializer.Serialize(spec.Subjects, KernelJson.Options)), ("$ps", primary), ("$asof", spec.AsOfMs), ("$v", final.Verdict.ToString()), ("$raw", final.ProbabilityRaw),
                ("$p", final.Probability), ("$lo", final.BandLow), ("$hi", final.BandHigh), ("$str", final.EvidenceStrength), ("$ec", final.EvidenceFor.Count + final.EvidenceAgainst.Count),
                ("$rep", JsonSerializer.Serialize(final, KernelJson.Options)), ("$as", assertionId), ("$st", standingId), ("$m", final.Model), ("$pv", final.PromptVersion), ("$state", state),
                ("$n", now), ("$seq", kit.Corpus.MaxSeq())), ct).ConfigureAwait(false);
            return final;
        }

        private static string Describe(InquiryReportDto r) => r.Verdict switch
        {
            InquiryVerdict.Answered when r.Distribution.Count > 0 => r.Distribution[0].Option,
            InquiryVerdict.Answered when r.Ranking.Count > 0 => r.Ranking[0].Label,
            InquiryVerdict.Answered when r.Estimate != null => r.Estimate.Value.ToString("0.##", CultureInfo.InvariantCulture),
            var v => v.ToString(),
        };

        private int MessageVersion(string messageId) => kit.Corpus.Message(messageId)?.Version ?? 1;

        private string? ExistingAssertion(string naturalKey)
        {
            using var c = kit.Context.Stores.Intel.OpenRead();
            return c.Scalar<string>(null, "SELECT assertion_id FROM kn_assertion_dims WHERE natural_key=$k AND live=1 LIMIT 1", ("$k", naturalKey));
        }

        private async Task<string> CreateStandingAsync(TacticRunContext ctx, InquiryRequest req, InquirySpec spec, InquiryReportDto report, CancellationToken ct)
        {
            string id = NaturalKeys.Id("sq", ctx.RunId, 20);
            long now = kit.Context.Clock.NowMs;
            long interval = Math.Max(req.StandingMinIntervalMs ?? 6 * 3_600_000L, 15 * 60_000L);
            var definition = new { question = req.Question, tactic = req.TacticKey, personIds = spec.Subjects.Select(s => s.PersonId).ToList(), answerType = spec.AnswerType, options = spec.Options,
                passes = req.Passes, framing = req.Framing };
            await kit.Context.Stores.Operator.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "INSERT OR IGNORE INTO standing_questions(question_id, inquiry_json, enabled, min_interval_ms, last_evaluated_ms, created_at_ms, updated_at_ms) VALUES($id,$j,1,$i,$n,$n,$n)",
                    ("$id", id), ("$j", JsonSerializer.Serialize(definition, KernelJson.Options)), ("$i", interval), ("$n", now));
                c.Exec(tx, "INSERT INTO audit_log(at_ms, actor, action, target, detail_json) VALUES($n,$a,'standing.create',$id,$d)",
                    ("$n", now), ("$a", ctx.Actor), ("$id", id), ("$d", JsonSerializer.Serialize(new { req.Question }, KernelJson.Options)));
            }, ct).ConfigureAwait(false);
            await kit.Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT OR IGNORE INTO kn_standing_state(question_id, last_seq, last_inquiry_id, last_run_id, last_verdict, last_probability, pending, updated_at_ms)
                VALUES($id,$seq,$inq,$run,$v,$p,0,$n)
                """, ("$id", id), ("$seq", kit.Corpus.MaxSeq()), ("$inq", report.InquiryId), ("$run", ctx.RunId), ("$v", report.Verdict.ToString()), ("$p", report.Probability), ("$n", now)), ct).ConfigureAwait(false);
            return id;
        }

        private static AIRouterWorkClass WorkClass(InquiryRequest req) => req.Interactive ? AIRouterWorkClass.Interactive : AIRouterWorkClass.Background;

        private static string SessionKey(TacticRunContext ctx, InquiryRequest req, string stage) =>
            (req.Interactive ? "omniscience-ia-inquiry-" : "omniscience-bg-inquiry-") + NaturalKeys.Sha256Hex(ctx.RunId + "|" + stage)[..16];
    }
}
