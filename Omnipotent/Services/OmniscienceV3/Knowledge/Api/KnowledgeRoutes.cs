using System.Globalization;
using System.Net;
using System.Text.Json;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Services.OmniscienceV3.Api;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Knowledge.Deduction;
using Omnipotent.Services.OmniscienceV3.Knowledge.Entities;
using Omnipotent.Services.OmniscienceV3.Knowledge.Inquiry;
using Omnipotent.Services.OmniscienceV3.Knowledge.Plans;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Knowledge.Quality;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.Knowledge.Tactics;
using Omnipotent.Services.OmniscienceV3.Knowledge.Timeline;
using Omnipotent.Services.OmniscienceV3.ReadModels;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using UserRequest = Omnipotent.Services.KliveAPI.KliveAPI.UserRequest;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Api
{
    /// <summary>
    /// The knowledge API under <c>/omniscience/v3/knowledge/</c> (typed registration; replaces v2's DeductionRoutes, F248–F265,
    /// F550/F551, and the review page's backend, F121–F126). Reads are indexed lookups of maintained state (facts, review queue,
    /// claims, runs, aliases, links, plans, inquiries) or read models (profile, timeline, agenda, Quality Lab); every list is
    /// keyset-paged; every decision is a POST that records a gold label and an audit row and returns the job it started.
    /// MinorGuard and the special-category rules are applied again at serialization (defence in depth).
    /// </summary>
    public sealed class KnowledgeRoutes
    {
        private const string P = V3Routes.Prefix + "/knowledge/";
        private readonly V3Host host;
        private readonly RouteRegistrar registrar;

        public KnowledgeRoutes(V3Host host, RouteRegistrar registrar)
        {
            this.host = host;
            this.registrar = registrar;
        }

        private V3Runtime Rt => host.Require();

        private T Module<T>() where T : class => Rt.Host.Modules.OfType<T>().FirstOrDefault()
                                                 ?? throw ApiException.Unavailable(ApiErrors.Unavailable, $"The {typeof(T).Name} is not installed in this build.");

        private KnowledgeKit Kit => KnowledgeKit.For(Rt.Host.Context);

        public async Task RegisterAsync(CancellationToken ct)
        {
            // ── facts ───────────────────────────────────────────────────────────
            await Get("facts", OmnisciencePerms.PersonsRead, req =>
            {
                var q = ApiQuery.Of(req);
                var kit = Kit;
                string person = kit.Corpus.Canonical(q.Required("person", 256));
                if (!kit.Corpus.PersonExists(person)) throw ApiException.NotFound("No such person.");
                long at = q.Instant("at") ?? Rt.Clock.NowMs;
                var mode = (q.Str("mode", 16) ?? "current") switch
                {
                    "current" => KnowledgeMode.Current,
                    "asof" => KnowledgeMode.AsOfWorld,
                    "known" => KnowledgeMode.AsKnownAt,
                    _ => throw ApiException.BadRequest("'mode' must be current, asof or known."),
                };
                var statuses = q.List("status", 10);
                var band = Rt.Host.Policy.BandOf(person, Rt.Clock.NowMs);
                var named = MinorGuard.NamedBands(Rt.Host.Policy, Rt.Clock.NowMs);
                // Every mode, the history included: what was believed once is still not shown about someone who is now covered by the guard.
                var views = kit.Assertions.Query(new AssertionQuery
                {
                    SubjectIds = kit.Corpus.PersonIdsOf(person), AtMs = at, Mode = mode, Attribute = q.Str("attribute", 64), Category = q.Str("category", 64),
                    Statuses = statuses.Count == 0 ? null : statuses, IncludeInferred = q.Bool("inferred") ?? true, Limit = 5_000,
                    EvidenceCutoffMs = q.Bool("knownByThen") == true ? at : null,
                }).Where(v => Attributes.IsShownFor(v.Record.Attribute, v.Record.Status, band, named(v.Record.ObjectId))).ToList();
                string? cursor = q.Str("cursor", 128);
                int limit = q.Int("limit", 200, 1, 1_000);
                var after = cursor == null ? views : views.SkipWhile(v => v.Record.AssertionId != cursor).Skip(1).ToList();
                var page = after.Take(limit).ToList();
                string? next = after.Count > limit ? page[^1].Record.AssertionId : null;
                return ApiResponses.OkAsync(req, new KnowledgeFactsPageDto(person, mode.ToString(), at, page.Select(DeductionModule.ToFactDto).ToList(), next));
            }, ct).ConfigureAwait(false);

            await Get("facts/get", OmnisciencePerms.PersonsRead, req =>
            {
                var kit = Kit;
                var got = kit.Assertions.Get(ApiQuery.Of(req).Required("id", 128)) ?? throw ApiException.NotFound("No such fact.");
                var r = got.Record;
                var band = Rt.Host.Policy.BandOf(kit.Corpus.Canonical(r.SubjectId), Rt.Clock.NowMs);
                if (!Attributes.IsShownFor(r.Attribute, r.Status, band, MinorGuard.NamedBands(Rt.Host.Policy, Rt.Clock.NowMs)(r.ObjectId))) throw ApiException.NotFound("No such fact.");
                var def = Attributes.Get(r.Attribute);
                var view = new AssertionView(r, def, Confidence.Effective(def, r.Probability ?? 0, r.AssertedAtMs, Rt.Clock.NowMs));
                var gold = kit.Gold.ActiveFor(GoldKinds.Assertion, r.NaturalKey);
                return ApiResponses.OkAsync(req, new AssertionDetailDto(DeductionModule.ToFactDto(view), r.NaturalKey, r.Modality, r.DerivationJson,
                    r.Value.Detail?.GetRawText(), Context(kit, got.Evidence), kit.Assertions.History(r.NaturalKey).Select(h => new AssertionVersionDto(h.AssertionId, h.Status, h.Probability,
                        h.RecordedAtMs, h.SupersededAtMs, h.RetractedAtMs, h.ValueText, h.ValidFromLatestMs, h.ValidToLatestMs, h.PromptVersion, h.Model)).ToList(), gold?.Verdict, gold?.DecidedAtMs));
            }, ct).ConfigureAwait(false);

            await Post("facts/review", OmnisciencePerms.ReviewAct, async req =>
            {
                var body = ApiBody.Read<FactReviewRequest>(req);
                if (!GoldVerdicts.IsKnown(body.Verdict)) throw ApiException.BadRequest("'verdict' must be confirm, reject or correct.");
                var kit = Kit;
                var got = kit.Assertions.Get(body.AssertionId) ?? throw ApiException.NotFound("No such fact.");
                object? correction = body.Verdict == GoldVerdicts.Correct ? new { value = body.Value, validFromMs = body.ValidFromMs, validToMs = body.ValidToMs } : null;
                await kit.Gold.RecordAsync(GoldKinds.Assertion, got.Record.NaturalKey, body.Verdict, correction, body.Note, ApiBody.Actor(req), host.Stopping).ConfigureAwait(false);
                long job = await Module<DeductionModule>().ConsolidateNowAsync(got.Record.SubjectId, host.Stopping).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, new KnowledgeJobStartedDto(job, "The fact is re-consolidated with your decision.")).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Get("profile", OmnisciencePerms.PersonsRead, req =>
            {
                string person = Kit.Corpus.Canonical(ApiQuery.Of(req).Required("person", 256));
                var band = Rt.Host.Policy.BandOf(person, Rt.Clock.NowMs);
                var row = Rt.Host.ReadModels.Get(DeductionModule.ProfileReadModel, person)
                          ?? throw new ApiException(HttpStatusCode.NotFound, "not_built", "This view has not been built yet; it is computed in the background.");
                // Defence in depth: a profile built before a band became restricted (the person's own, or that of someone one
                // of its facts names) is filtered at serialization too.
                var body = JsonSerializer.Deserialize<KnowledgeProfileDto>(row.BodyJson, KernelJson.Options)!;
                var shown = Withhold(body, band, MinorGuard.NamedBands(Rt.Host.Policy, Rt.Clock.NowMs));
                if (ReferenceEquals(shown, body)) return Serve(req, row);
                return ApiResponses.OkAsync(req, new { model = row.ModelId, key = row.Key, version = row.Version, schemaVersion = row.SchemaVersion, asOfMs = row.AsOfMs, sourceSeq = row.SourceSeq,
                    computedAtMs = row.ComputedAtMs, body = shown });
            }, ct).ConfigureAwait(false);

            // ── review queue ────────────────────────────────────────────────────
            await Get("review", OmnisciencePerms.PersonsRead, req =>
            {
                var q = ApiQuery.Of(req);
                var review = new ReviewQueue(Rt.Stores, Rt.Clock);
                string? person = q.Str("person", 256) is { } p ? Kit.Corpus.Canonical(p) : null;
                try
                {
                    var (items, next) = review.Page(q.Str("state", 16) ?? ReviewStates.Open, q.Str("kind", 32), person, (q.Str("sort", 16) ?? "priority") == "age", q.Str("cursor", 256), q.Int("limit", 100, 1, 500));
                    // Each item with what it points at named (people, places, the words said, the thing to decide): its detail carries ids.
                    return ApiResponses.OkAsync(req, new KnowledgeReviewPageDto(Rt.Clock.NowMs, new ReviewContexts(Kit, Rt.Stores).Dtos(items), next));
                }
                catch (ArgumentException ex) { throw ApiException.BadRequest(ex.Message); }
            }, ct).ConfigureAwait(false);

            await Post("review/resolve", OmnisciencePerms.ReviewAct, async req =>
            {
                var body = ApiBody.Read<ReviewResolveRequest>(req);
                if (body.State is not (ReviewStates.Resolved or ReviewStates.Dismissed)) throw ApiException.BadRequest("'state' must be resolved or dismissed.");
                bool changed = await new ReviewQueue(Rt.Stores, Rt.Clock).ResolveAsync(body.ItemId, body.State, body.Resolution, ApiBody.Actor(req), host.Stopping).ConfigureAwait(false);
                if (!changed) throw ApiException.Conflict("That item is not open.");
                await ApiResponses.OkAsync(req, new { body.ItemId, body.State }).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            // ── claims and extraction runs (audit, Quality Lab) ─────────────────
            await Get("claims", OmnisciencePerms.PersonsRead, req =>
            {
                var q = ApiQuery.Of(req);
                string? person = q.Str("person", 256) is { } p ? Kit.Corpus.Canonical(p) : null;
                var (items, next) = Claims(person, q.Str("verdict", 16), q.Str("reason", 64), q.Str("run", 128), q.Str("cursor", 128), q.Int("limit", 100, 1, 500));
                return ApiResponses.OkAsync(req, new ClaimPageDto(items, next));
            }, ct).ConfigureAwait(false);

            await Get("runs", OmnisciencePerms.PersonsRead, req =>
            {
                var q = ApiQuery.Of(req);
                string? person = q.Str("person", 256) is { } p ? Kit.Corpus.Canonical(p) : null;
                var (items, next) = Runs(person, q.Str("cursor", 128), q.Int("limit", 50, 1, 500));
                return ApiResponses.OkAsync(req, new ExtractionRunPageDto(items, next));
            }, ct).ConfigureAwait(false);

            await Post("deduction/run", OmnisciencePerms.AnalysisRun, async req =>
            {
                var body = ApiBody.Read<PersonRequest>(req);
                if (!Kit.Corpus.PersonExists(Kit.Corpus.Canonical(body.PersonId))) throw ApiException.NotFound("No such person.");
                long job = await Module<DeductionModule>().RunNowAsync(body.PersonId, host.Stopping).ConfigureAwait(false);
                await OperatorAudit.WriteAsync(Rt.Stores.Operator, Rt.Clock, ApiBody.Actor(req), "deduction.run", body.PersonId, new { job }, host.Stopping).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, new KnowledgeJobStartedDto(job, "Extraction, consolidation and the detective run as one pipeline."), HttpStatusCode.Accepted).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            // ── age band (MinorGuard) ───────────────────────────────────────────
            await Get("age", OmnisciencePerms.PersonsRead, req =>
            {
                var kit = Kit;
                string person = kit.Corpus.Canonical(ApiQuery.Of(req).Required("person", 256));
                long now = Rt.Clock.NowMs;
                var oracle = Rt.Host.Policy as EvidencePolicyOracle;
                var band = Rt.Host.Policy.BandOf(person, now);
                bool marked = new OperatorPolicyOracle(Rt.Stores).BandOf(person, now) == AgeBand.Minor;
                var rows = AllAgeEvidence(person);
                var quotes = kit.Corpus.Messages(rows.Select(r => r.MessageId).Distinct().ToList()).ToDictionary(m => m.MessageId, m => m.Content);
                return ApiResponses.OkAsync(req, new AgeBandDto(person, band.ToString(), MinorGuard.IsRestricted(band), marked, now,
                    rows.Select(r => new AgeStatementDto(r.EvidenceId, r.Kind, r.Status, r.SourceKind, r.AssertedAtMs, r.MessageId, r.Probability,
                        quotes.TryGetValue(r.MessageId, out var t) ? Evidence.EvidenceRenderer.Truncate(t ?? "", 400) : null)).ToList()));
            }, ct).ConfigureAwait(false);

            await Post("age/review", OmnisciencePerms.ReviewAct, async req =>
            {
                var body = ApiBody.Read<AgeReviewRequest>(req);
                if (body.Verdict is not (GoldVerdicts.Reject or GoldVerdicts.Confirm)) throw ApiException.BadRequest("'verdict' must be confirm or reject.");
                string? key, person;
                using (var c = Rt.Stores.Intel.OpenRead())
                {
                    var row = c.Query(null, "SELECT natural_key, person_id FROM kn_age_evidence WHERE evidence_id=$e", r => (r.GetString(0), r.GetString(1)), ("$e", body.EvidenceId)).FirstOrDefault();
                    (key, person) = row;
                }
                if (key == null || person == null) throw ApiException.NotFound("No such age statement.");
                await Kit.Gold.RecordAsync(GoldKinds.AgeEvidence, key, body.Verdict, null, body.Note, ApiBody.Actor(req), host.Stopping).ConfigureAwait(false);
                if (Rt.Host.Policy is EvidencePolicyOracle oracle) oracle.Invalidate(Kit.Corpus.Canonical(person));
                long job = await Module<DeductionModule>().ConsolidateNowAsync(person, host.Stopping).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, new KnowledgeJobStartedDto(job, "The band is recomputed from the remaining statements.")).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            // ── aliases, entities, hypotheses ───────────────────────────────────
            await Get("aliases", OmnisciencePerms.PersonsRead, req =>
            {
                string person = Kit.Corpus.Canonical(ApiQuery.Of(req).Required("person", 256));
                using var c = Rt.Stores.Intel.OpenRead();
                var rows = c.Query(null, """
                    SELECT alias_id, person_id, alias, name_kind, scope_kind, scope_id, valid_from_ms, valid_to_ms, usages, speakers, self_claims, probability, status, common_word
                    FROM kn_aliases WHERE person_id=$p ORDER BY probability DESC, alias_id
                    """, r => new AliasDto(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetStringOrNull(5), r.GetInt64(6), r.GetInt64OrNull(7),
                    r.GetInt32(8), r.GetInt32(9), r.GetInt32(10), r.GetDouble(11), r.GetString(12), r.GetInt32(13) == 1, r.GetString(12) == AliasStatus.Confirmed), ("$p", person));
                return ApiResponses.OkAsync(req, rows);
            }, ct).ConfigureAwait(false);

            await Post("aliases/review", OmnisciencePerms.ReviewAct, async req =>
            {
                var body = ApiBody.Read<AliasReviewRequest>(req);
                if (body.Verdict is not (GoldVerdicts.Confirm or GoldVerdicts.Reject)) throw ApiException.BadRequest("'verdict' must be confirm or reject.");
                (string? alias, string? person, string? scopeKind, string? scopeId) row;
                using (var c = Rt.Stores.Intel.OpenRead())
                    row = c.Query(null, "SELECT alias, person_id, scope_kind, scope_id FROM kn_aliases WHERE alias_id=$a", r => ((string?)r.GetString(0), (string?)r.GetString(1), (string?)r.GetString(2), r.GetStringOrNull(3)),
                        ("$a", body.AliasId)).FirstOrDefault();
                if (row.alias == null) throw ApiException.NotFound("No such alias.");
                string key = NaturalKeys.Canonical(("a", NaturalKeys.Normalize(row.alias)), ("s", "person:" + row.person), ("scope", row.scopeKind + ":" + row.scopeId));
                await Kit.Gold.RecordAsync(GoldKinds.Alias, key, body.Verdict, null, body.Note, ApiBody.Actor(req), host.Stopping).ConfigureAwait(false);
                await Rt.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE kn_aliases SET status=$s, updated_at_ms=$n WHERE alias_id=$a",
                    ("$s", body.Verdict == GoldVerdicts.Confirm ? AliasStatus.Confirmed : AliasStatus.Rejected), ("$n", Rt.Clock.NowMs), ("$a", body.AliasId)), host.Stopping).ConfigureAwait(false);
                await new ReviewQueue(Rt.Stores, Rt.Clock).ResolveAsync(ReviewQueue.ItemIdFor(ReviewKinds.Alias, key), ReviewStates.Resolved, body.Verdict, ApiBody.Actor(req), host.Stopping).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, new { body.AliasId, status = body.Verdict == GoldVerdicts.Confirm ? AliasStatus.Confirmed : AliasStatus.Rejected }).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Get("entities", OmnisciencePerms.PersonsRead, req =>
            {
                string person = Kit.Corpus.Canonical(ApiQuery.Of(req).Required("person", 256));
                using var c = Rt.Stores.Intel.OpenRead();
                return ApiResponses.OkAsync(req, c.Query(null, """
                    SELECT entity_id, owner_person_id, kind, name, attributes_json, first_evidence_ms, last_evidence_ms, mentions, status FROM kn_entities
                    WHERE owner_person_id=$p AND status='active' ORDER BY last_evidence_ms DESC, entity_id
                    """, r => new EntityDto(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5), r.GetInt64(6), r.GetInt32(7), r.GetString(8)), ("$p", person)));
            }, ct).ConfigureAwait(false);

            await Get("hypotheses", OmnisciencePerms.PersonsRead, req =>
            {
                var q = ApiQuery.Of(req);
                string person = Kit.Corpus.Canonical(q.Required("person", 256));
                using var c = Rt.Stores.Intel.OpenRead();
                return ApiResponses.OkAsync(req, c.Query(null, """
                    SELECT hypothesis_id, person_id, statement, attribute, probability, status, created_at_ms, expires_at_ms, resolved_at_ms, resolution_note, derived_from_json
                    FROM kn_hypotheses WHERE person_id=$p AND ($s IS NULL OR status=$s) ORDER BY created_at_ms DESC, hypothesis_id LIMIT 500
                    """, r => new HypothesisDto(r.GetString(0), r.GetString(1), r.GetString(2), r.GetStringOrNull(3), r.GetDouble(4), r.GetString(5), r.GetInt64(6), r.GetInt64(7),
                    r.GetInt64OrNull(8), r.GetStringOrNull(9), r.GetString(10)), ("$p", person), ("$s", q.Str("status", 16))));
            }, ct).ConfigureAwait(false);

            // ── identity links and merges (X67) ─────────────────────────────────
            await Get("identity-links", OmnisciencePerms.PersonsRead, req =>
            {
                var q = ApiQuery.Of(req);
                try
                {
                    var (items, next) = Module<IdentityModule>().Page(q.Str("verdict", 16) ?? "suggested", q.Str("person", 256), q.Str("cursor", 128), q.Int("limit", 50, 1, 500));
                    var accounts = new ReviewContexts(Kit, Rt.Stores).Accounts(items.SelectMany(l => new[] { l.IdentityA, l.IdentityB }).ToList());
                    return ApiResponses.OkAsync(req, new IdentityLinkPageDto(items.Select(l => ToDto(l, accounts)).ToList(), next));
                }
                catch (ArgumentException ex) { throw ApiException.BadRequest(ex.Message); }
            }, ct).ConfigureAwait(false);

            await Post("identity-links/decide", OmnisciencePerms.ReviewAct, async req =>
            {
                var body = ApiBody.Read<IdentityLinkDecisionRequest>(req);
                if (body.Accept && !body.Confirm)
                    throw new ApiException(HttpStatusCode.Conflict, "confirmation_required", "Accepting merges the two people. Review the evidence and send confirm=true (it can be undone).");
                if (body.Accept && !req.Can(OmnisciencePerms.SourcesManage)) throw ApiException.Forbidden("Merging people needs the 'Manage sources' permission.");
                try
                {
                    var (link, merge) = await Module<IdentityModule>().DecideAsync(body.LinkId, body.Accept, ApiBody.Actor(req), body.Note, host.Stopping).ConfigureAwait(false);
                    var accounts = new ReviewContexts(Kit, Rt.Stores).Accounts(new[] { link.IdentityA, link.IdentityB });
                    await ApiResponses.OkAsync(req, new IdentityLinkDecisionDto(ToDto(link, accounts), merge == null ? null : new MergeResultDto(merge.MergeId, merge.Survivor, merge.Absorbed, merge.Changed, merge.Reason))).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex) { throw ApiException.Conflict(ex.Message); }
            }, ct).ConfigureAwait(false);

            await Post("identity-links/undo", OmnisciencePerms.SourcesManage, async req =>
            {
                var body = ApiBody.Read<LinkActionRequest>(req);
                bool undone = await Module<IdentityModule>().UndoAsync(body.LinkId, ApiBody.Actor(req), host.Stopping).ConfigureAwait(false);
                if (!undone) throw ApiException.Conflict("That suggestion has no merge to undo.");
                await ApiResponses.OkAsync(req, new { body.LinkId, undone }).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Post("persons/merge", OmnisciencePerms.SourcesManage, async req =>
            {
                var body = ApiBody.Read<MergeRequest>(req);
                if (!body.Confirm) throw new ApiException(HttpStatusCode.Conflict, "confirmation_required", "Merging two people needs confirm=true (it can be undone).");
                var kit = Kit;
                if (!kit.Corpus.PersonExists(kit.Corpus.Canonical(body.PersonA)) || !kit.Corpus.PersonExists(kit.Corpus.Canonical(body.PersonB))) throw ApiException.NotFound("No such person.");
                var merge = await new PersonMerges(Rt.Stores, Rt.Clock, kit.Corpus).MergeAsync(body.PersonA, body.PersonB, ApiBody.Actor(req), body.Reason ?? "manual merge", host.Stopping).ConfigureAwait(false);
                Rt.Host.Kick();
                await ApiResponses.OkAsync(req, new MergeResultDto(merge.MergeId, merge.Survivor, merge.Absorbed, merge.Changed, merge.Reason)).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Post("persons/unmerge", OmnisciencePerms.SourcesManage, async req =>
            {
                var body = ApiBody.Read<UnmergeRequest>(req);
                bool undone = await new PersonMerges(Rt.Stores, Rt.Clock, Kit.Corpus).UnmergeAsync(body.MergeId, ApiBody.Actor(req), host.Stopping).ConfigureAwait(false);
                if (!undone) throw ApiException.Conflict("That merge does not exist or was already undone.");
                Rt.Host.Kick();
                await ApiResponses.OkAsync(req, new { body.MergeId, undone }).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Get("persons/merges", OmnisciencePerms.PersonsRead, req =>
            {
                var history = new PersonMerges(Rt.Stores, Rt.Clock, Kit.Corpus).History(ApiQuery.Of(req).Required("person", 256));
                var own = new ReviewContexts(Kit, Rt.Stores).OwnNames(history.SelectMany(m => new[] { m.FromPersonId, m.IntoPersonId }).ToList());
                return ApiResponses.OkAsync(req, history.Select(m => new MergeRowDto(m.MergeId, m.FromPersonId, m.IntoPersonId, m.MergedAtMs, m.Actor, m.Reason, m.UndoneAtMs,
                    own.GetValueOrDefault(m.FromPersonId), own.GetValueOrDefault(m.IntoPersonId))).ToList());
            }, ct).ConfigureAwait(false);

            // ── timeline, plans, agenda ─────────────────────────────────────────
            await Get("timeline", OmnisciencePerms.PersonsRead, req => ServeReadModel(req, TimelineModule.ReadModel, Kit.Corpus.Canonical(ApiQuery.Of(req).Required("person", 256))), ct).ConfigureAwait(false);
            await Get("agenda", OmnisciencePerms.PersonsRead, req => ServeReadModel(req, PlansModule.AgendaModel, "klives"), ct).ConfigureAwait(false);
            await Get("plans", OmnisciencePerms.PersonsRead, req =>
            {
                var q = ApiQuery.Of(req);
                string? person = q.Str("person", 256) is { } p ? Kit.Corpus.Canonical(p) : null;
                var (items, next) = PlansPage(person, q.Str("status", 16), q.Str("kind", 16), q.Bool("klivesOnly") ?? false, q.Str("cursor", 128), q.Int("limit", 100, 1, 500));
                return ApiResponses.OkAsync(req, new PlanPageDto(items, next));
            }, ct).ConfigureAwait(false);

            // ── inquiries ───────────────────────────────────────────────────────
            await Post("ask/plan", OmniscienceV3Perms.TacticsRun, async req =>
            {
                var body = ApiBody.Read<AskRequest>(req);
                if (string.IsNullOrWhiteSpace(body.Question)) throw ApiException.BadRequest("'question' is required.");
                var catalog = Rt.Tactics.Catalog().Where(d => PermissionCatalog.Get(d.RequiredPermission) is { } perm && req.Can(perm) && !d.Outbound).ToList();
                var plan = await Module<InquiryModule>().PlanAskAsync(body.Question, catalog, host.Stopping).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, plan).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            await Post("ask", OmniscienceV3Perms.TacticsRun, async req =>
            {
                var body = ApiBody.Read<AskRequest>(req);
                if (string.IsNullOrWhiteSpace(body.Question)) throw ApiException.BadRequest("'question' is required.");
                if (!req.Can(OmnisciencePerms.PersonsAsk)) throw ApiException.Forbidden("Asking about people needs the 'Ask about people' permission.");
                string tactic = body.Tactic ?? (body.Standing ? "x14.standing_question" : ReasoningTactics.AskYesNoKey);
                if (Rt.Tactics.Find(tactic) is not { } t || t.Descriptor.Category != TacticCategory.Inquiry) throw ApiException.BadRequest($"'{tactic}' is not an inquiry tactic.");
                var selections = new List<Selection> { new(SelectionKind.Question, new[] { new SelectionItem("q", null, body.Question) }) };
                if (body.PersonIds is { Count: > 0 } people) selections.Add(Selection.Of(SelectionKind.Person, people));
                var input = new TacticInput(selections.Where(s => t.Descriptor.Accepts.Contains(s.Kind)).Take(t.Descriptor.MaxSelections).ToList(),
                    JsonSerializer.SerializeToElement(new { question = body.Question, standing = body.Standing }, KernelJson.Options), body.AsOfMs);
                try
                {
                    string runId = await Rt.Tactics.StartAsync(tactic, input, ApiBody.Actor(req), ct: host.Stopping).ConfigureAwait(false);
                    await ApiResponses.OkAsync(req, new AskStartedDto(runId, tactic), HttpStatusCode.Accepted).ConfigureAwait(false);
                }
                catch (TacticRefusedException ex) { throw new ApiException(HttpStatusCode.UnprocessableEntity, "refused", ex.Message); }
            }, ct).ConfigureAwait(false);

            await Get("inquiries", OmnisciencePerms.PersonsRead, req =>
            {
                var q = ApiQuery.Of(req);
                var (from, to) = q.Window();
                try
                {
                    var (items, next) = Module<InquiryModule>().Page(q.Str("person", 256), q.Str("state", 32), q.Str("standing", 128), from, to, q.Str("cursor", 128), q.Int("limit", 50, 1, 500),
                        q.List("persons", ScopeFilter.MaxValues), q.Str("q", 200), q.List("verdicts", 10));
                    return ApiResponses.OkAsync(req, new InquiryPageDto(items.Select(i => new InquiryRowDto(i.InquiryId, i.RunId, i.TacticKey, i.Question, i.Restatement, i.AnswerType, i.PrimarySubjectId,
                        i.AsOfMs, i.Verdict, i.Probability, i.State, i.Feedback, i.StandingId, i.CreatedAtMs)).ToList(), next));
                }
                catch (ArgumentException ex) { throw ApiException.BadRequest(ex.Message); }
            }, ct).ConfigureAwait(false);

            await Get("inquiries/get", OmnisciencePerms.PersonsRead, req =>
                ApiResponses.OkAsync(req, Module<InquiryModule>().Get(ApiQuery.Of(req).Required("id", 128)) ?? throw ApiException.NotFound("No such inquiry.")), ct).ConfigureAwait(false);

            await Post("inquiries/feedback", OmnisciencePerms.ReviewAct, async req =>
            {
                var body = ApiBody.Read<InquiryFeedbackRequest>(req);
                try
                {
                    bool ok = await Module<InquiryModule>().FeedbackAsync(body.InquiryId, body.Feedback, ApiBody.Actor(req), body.Note, host.Stopping).ConfigureAwait(false);
                    await ApiResponses.OkAsync(req, new { body.InquiryId, recorded = ok }).ConfigureAwait(false);
                }
                catch (ArgumentException ex) { throw ApiException.BadRequest(ex.Message); }
            }, ct).ConfigureAwait(false);

            await Get("standing", OmnisciencePerms.PersonsRead, req => ApiResponses.OkAsync(req, Standing()), ct).ConfigureAwait(false);

            await Post("standing/enabled", OmniscienceV3Perms.TacticsRun, async req =>
            {
                var body = ApiBody.Read<StandingEnabledRequest>(req);
                long now = Rt.Clock.NowMs;
                int n = await Rt.Stores.Operator.WriteAsync((c, tx) => c.Exec(tx, "UPDATE standing_questions SET enabled=$e, updated_at_ms=$n WHERE question_id=$q",
                    ("$e", body.Enabled ? 1 : 0), ("$n", now), ("$q", body.QuestionId)), host.Stopping).ConfigureAwait(false);
                if (n == 0) throw ApiException.NotFound("No such standing question.");
                await OperatorAudit.WriteAsync(Rt.Stores.Operator, Rt.Clock, ApiBody.Actor(req), "standing.enabled", body.QuestionId, new { body.Enabled }, host.Stopping).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, new { body.QuestionId, body.Enabled }).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            // ── Quality Lab ─────────────────────────────────────────────────────
            await Get("quality", OmnisciencePerms.OverviewView, req => ServeReadModel(req, QualityModule.ReadModel, "lab"), ct).ConfigureAwait(false);
            await Post("quality/run", OmnisciencePerms.AnalysisRun, async req =>
            {
                var body = ApiBody.Read<QualityRunRequest>(req);
                var lab = Module<QualityModule>();
                long job = body.Kind switch
                {
                    "lab" => await lab.StartLabAsync(host.Stopping).ConfigureAwait(false),
                    "planted" => await lab.StartPlantedEvaluationAsync(host.Stopping).ConfigureAwait(false),
                    _ => throw ApiException.BadRequest("'kind' must be lab or planted."),
                };
                await OperatorAudit.WriteAsync(Rt.Stores.Operator, Rt.Clock, ApiBody.Actor(req), "quality.run", body.Kind, new { job }, host.Stopping).ConfigureAwait(false);
                await ApiResponses.OkAsync(req, new KnowledgeJobStartedDto(job, body.Kind == "planted" ? "Runs the real model on the synthetic planted-fact corpus in a scratch store." : null), HttpStatusCode.Accepted).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }

        // ── helpers ──────────────────────────────────────────────────────────────

        private Task ServeReadModel(UserRequest req, string model, string key) =>
            Serve(req, Rt.Host.ReadModels.Get(model, key)
                       ?? throw new ApiException(HttpStatusCode.NotFound, "not_built", "This view has not been built yet; it is computed in the background."));

        private static Task Serve(UserRequest req, ReadModelRow row) =>
            ApiResponses.JsonAsync(req, DataRoutesEnvelope(row));

        /// <summary>
        /// A stored profile as it may be served now (MinorGuard): without inferences and restricted facts when the person is
        /// covered by the guard, and without a restricted fact that names someone who is. The same instance when nothing is withheld.
        /// </summary>
        internal static KnowledgeProfileDto Withhold(KnowledgeProfileDto body, AgeBand band, Func<string?, AgeBand?> named)
        {
            bool Shown(KnowledgeFactDto f) => Attributes.IsShownFor(f.Attribute, f.Status, band, named(f.ObjectPersonId));
            bool restricted = MinorGuard.IsRestricted(band);
            if (!restricted && body.Inferences.All(Shown) && body.Categories.All(c => c.Facts.All(Shown))) return body;
            return body with
            {
                Inferences = restricted ? Array.Empty<KnowledgeFactDto>() : body.Inferences.Where(Shown).ToList(),
                Categories = body.Categories.Select(c => c with { Facts = c.Facts.Where(Shown).ToList() }).Where(c => c.Facts.Count > 0).ToList(),
            };
        }

        private static string DataRoutesEnvelope(ReadModelRow row) =>
            "{\"model\":" + JsonSerializer.Serialize(row.ModelId) + ",\"key\":" + JsonSerializer.Serialize(row.Key) + ",\"version\":" + row.Version.ToString(CultureInfo.InvariantCulture) +
            ",\"schemaVersion\":" + row.SchemaVersion.ToString(CultureInfo.InvariantCulture) + ",\"asOfMs\":" + row.AsOfMs.ToString(CultureInfo.InvariantCulture) +
            ",\"sourceSeq\":" + row.SourceSeq.ToString(CultureInfo.InvariantCulture) + ",\"computedAtMs\":" + row.ComputedAtMs.ToString(CultureInfo.InvariantCulture) + ",\"body\":" + row.BodyJson + "}";

        private IReadOnlyList<EvidenceContextDto> Context(KnowledgeKit kit, IReadOnlyList<EvidenceRef> evidence) =>
            EvidenceContexts.List(kit, evidence, new ReviewContexts(kit, Rt.Stores).Names);

        private IReadOnlyList<AgeEvidenceRow> AllAgeEvidence(string person)
        {
            var ids = Kit.Corpus.PersonIdsOf(person);
            using var c = Rt.Stores.Intel.OpenRead();
            return c.QueryIn(null, """
                SELECT evidence_id, person_id, kind, birth_earliest_ms, birth_latest_ms, month, day, source_kind, asserted_at_ms, message_id, message_version, claim_id, probability, status, natural_key
                FROM kn_age_evidence WHERE person_id IN ({keys}) ORDER BY asserted_at_ms
                """, ids, EvidencePolicyOracle.Map);
        }

        private (IReadOnlyList<ClaimDto>, string?) Claims(string? person, string? verdict, string? reason, string? run, string? cursor, int limit)
        {
            var where = new List<string>();
            var args = new List<(string, object?)>();
            if (person != null) { where.Add("person_id=$p"); args.Add(("$p", person)); }
            if (verdict != null) { where.Add("verdict=$v"); args.Add(("$v", verdict)); }
            if (reason != null) { where.Add("reason=$r"); args.Add(("$r", reason)); }
            if (run != null) { where.Add("run_id=$run"); args.Add(("$run", run)); }
            if (cursor != null)
            {
                int bar = cursor.IndexOf('|');
                if (bar <= 0 || !long.TryParse(cursor[..bar], NumberStyles.Integer, CultureInfo.InvariantCulture, out long at)) throw ApiException.BadRequest("Malformed cursor.");
                where.Add("(recorded_at_ms < $ca OR (recorded_at_ms = $ca AND claim_id < $ci))");
                args.Add(("$ca", at));
                args.Add(("$ci", cursor[(bar + 1)..]));
            }
            args.Add(("$l", limit + 1));
            using var c = Rt.Stores.Intel.OpenRead();
            var rows = c.Query(null, $"""
                SELECT claim_id, run_id, person_id, attribute, category, value_text, modality, source_kind, probability, agreement, verdict, reason, critic, asserted_at_ms, recorded_at_ms,
                       prompt_version, model, conversation_id
                FROM kn_claims {(where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where))} ORDER BY recorded_at_ms DESC, claim_id DESC LIMIT $l
                """, r => new ClaimDto(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetStringOrNull(5), r.GetStringOrNull(6), r.GetStringOrNull(7),
                    r.GetDoubleOrNull(8), r.GetString(9), r.GetString(10), r.GetStringOrNull(11), r.GetStringOrNull(12), r.GetInt64(13), r.GetInt64(14), r.GetString(15), r.GetStringOrNull(16),
                    r.GetStringOrNull(17)), args.ToArray());
            if (rows.Count <= limit) return (rows, null);
            var page = rows.Take(limit).ToList();
            return (page, page[^1].RecordedAtMs.ToString(CultureInfo.InvariantCulture) + "|" + page[^1].ClaimId);
        }

        private (IReadOnlyList<ExtractionRunDto>, string?) Runs(string? person, string? cursor, int limit)
        {
            var where = new List<string>();
            var args = new List<(string, object?)>();
            if (person != null) { where.Add("person_id=$p"); args.Add(("$p", person)); }
            if (cursor != null)
            {
                int bar = cursor.IndexOf('|');
                if (bar <= 0 || !long.TryParse(cursor[..bar], NumberStyles.Integer, CultureInfo.InvariantCulture, out long at)) throw ApiException.BadRequest("Malformed cursor.");
                where.Add("(started_at_ms < $ca OR (started_at_ms = $ca AND run_id < $ci))");
                args.Add(("$ca", at));
                args.Add(("$ci", cursor[(bar + 1)..]));
            }
            args.Add(("$l", limit + 1));
            using var c = Rt.Stores.Intel.OpenRead();
            var rows = c.Query(null, $"""
                SELECT run_id, person_id, job_id, prompt_version, model, slices, messages, claims_raw, claims_agreed, claims_verified, claims_rejected, rejections_json, llm_calls, outcome,
                       started_at_ms, finished_at_ms
                FROM kn_extraction_runs {(where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where))} ORDER BY started_at_ms DESC, run_id DESC LIMIT $l
                """, r => new ExtractionRunDto(r.GetString(0), r.GetString(1), r.GetInt64OrNull(2), r.GetString(3), r.GetStringOrNull(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7),
                    r.GetInt32(8), r.GetInt32(9), r.GetInt32(10), r.GetStringOrNull(11), r.GetInt32(12), r.GetString(13), r.GetInt64(14), r.GetInt64OrNull(15)), args.ToArray());
            if (rows.Count <= limit) return (rows, null);
            var page = rows.Take(limit).ToList();
            return (page, page[^1].StartedAtMs.ToString(CultureInfo.InvariantCulture) + "|" + page[^1].RunId);
        }

        /// <summary>Where a plan stands in the list: when it is for, or, when it names no date, when it was said (the
        /// expression ix_kn_plans_time is built on). Plans without a date used to be listed first, in no order at all.</summary>
        internal const string PlanOrder = "COALESCE(scheduled_earliest_ms, said_at_ms, 0)";

        private (IReadOnlyList<PlanItemDto>, string?) PlansPage(string? person, string? status, string? kind, bool klivesOnly, string? cursor, int limit)
        {
            var where = new List<string> { "status <> 'cancelled' OR $s = 'cancelled'" };
            var args = new List<(string, object?)> { ("$s", status) };
            if (person != null) { where.Add("(owner_person_id=$p OR counterparty_person_id=$p)"); args.Add(("$p", person)); }
            if (status != null) { where.Add("status=$s"); }
            if (kind != null) { where.Add("kind=$k"); args.Add(("$k", kind)); }
            if (klivesOnly) where.Add("involves_klives=1");
            if (cursor != null)
            {
                int bar = cursor.IndexOf('|');
                if (bar <= 0 || !long.TryParse(cursor[..bar], NumberStyles.Integer, CultureInfo.InvariantCulture, out long at)) throw ApiException.BadRequest("Malformed cursor.");
                where.Add($"({PlanOrder} > $ca OR ({PlanOrder} = $ca AND plan_id > $ci))");
                args.Add(("$ca", at));
                args.Add(("$ci", cursor[(bar + 1)..]));
            }
            args.Add(("$l", limit + 1));
            var plans = Module<PlansModule>();
            List<(string Id, string Owner, long? Earliest)> rows;
            using (var c = Rt.Stores.Intel.OpenRead())
                rows = c.Query(null, $"SELECT plan_id, owner_person_id, {PlanOrder} FROM kn_plans WHERE {string.Join(" AND ", where.Select(w => "(" + w + ")"))} ORDER BY {PlanOrder}, plan_id LIMIT $l",
                    r => (r.GetString(0), r.GetString(1), r.GetInt64OrNull(2)), args.ToArray());
            long now = Rt.Clock.NowMs;
            var built = rows.Select(r => r.Owner).Distinct().SelectMany(o => plans.Build(o, now)).ToDictionary(p => p.PlanId, StringComparer.Ordinal);
            var items = rows.Take(limit).Where(r => built.ContainsKey(r.Id)).Select(r => plans.ToDto(built[r.Id], now)).ToList();
            string? next = rows.Count > limit ? (rows[limit - 1].Earliest ?? 0).ToString(CultureInfo.InvariantCulture) + "|" + rows[limit - 1].Id : null;
            return (items, next);
        }

        private IReadOnlyList<StandingQuestionDto> Standing()
        {
            var states = new Dictionary<string, (string? Verdict, double? P, bool Pending)>(StringComparer.Ordinal);
            using (var c = Rt.Stores.Intel.OpenRead())
            {
                bool has = c.Scalar<long>(null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='kn_standing_state'") > 0;
                if (has)
                    foreach (var r in c.Query(null, "SELECT question_id, last_verdict, last_probability, pending FROM kn_standing_state", r => (r.GetString(0), r.GetStringOrNull(1), r.GetDoubleOrNull(2), r.GetInt32(3) == 1)))
                        states[r.Item1] = (r.Item2, r.Item3, r.Item4);
            }
            using var o = Rt.Stores.Operator.OpenRead();
            return o.Query(null, "SELECT question_id, inquiry_json, enabled, min_interval_ms, last_evaluated_ms, created_at_ms FROM standing_questions ORDER BY created_at_ms DESC, question_id",
                r =>
                {
                    StandingDefinition? def = null;
                    try { def = JsonSerializer.Deserialize<StandingDefinition>(r.GetString(1), KernelJson.Options); }
                    catch (JsonException) { def = null; }
                    states.TryGetValue(r.GetString(0), out var st);
                    return new StandingQuestionDto(r.GetString(0), def?.Question ?? "(unreadable)", def?.PersonIds ?? Array.Empty<string>(), r.GetInt32(2) == 1, r.GetInt64(3), r.GetInt64OrNull(4),
                        st.Verdict, st.P, st.Pending, r.GetInt64(5));
                });
        }

        private static IdentityLinkDto ToDto(IdentityLinkRow l, IReadOnlyDictionary<string, ReviewAccountDto> accounts) => new(l.LinkId, l.IdentityA, l.IdentityB, l.PersonA, l.PersonB,
            l.Signals.Select(s => new LinkSignalDto(s.Kind, s.Strength, s.Detail)).ToList(), l.IndependentSignals, l.Probability, l.Verdict, l.JudgeJson, l.CreatedAtMs, l.UpdatedAtMs, l.DecidedAtMs, l.MergeId,
            accounts.GetValueOrDefault(l.IdentityA), accounts.GetValueOrDefault(l.IdentityB));

        private Task Get(string path, PermissionDef permission, Func<UserRequest, Task> handler, CancellationToken ct) =>
            registrar.RouteAsync(P + path, HttpMethod.Get, permission, handler, RouteRegistrar.DefaultMaxBodyBytes, ct);

        private Task Post(string path, PermissionDef permission, Func<UserRequest, Task> handler, CancellationToken ct) =>
            registrar.RouteAsync(P + path, HttpMethod.Post, permission, handler, RouteRegistrar.DefaultMaxBodyBytes, ct);
    }
}
