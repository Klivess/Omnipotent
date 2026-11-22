using System.Globalization;
using System.Text;
using System.Text.Json;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Knowledge.Evidence;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Knowledge.Prompting;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.Llm;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Deduction
{
    public sealed record ExtractionOptions
    {
        public int MaxOutputTokens { get; init; } = 16_000;
        /// <summary>
        /// Evidence tokens in one slice, at most. Claims come out about as long as the evidence goes in (measured on the
        /// server: 0.4 to 2.6 output tokens per evidence token), so what bounds a slice is the output ceiling, not the
        /// model's window. Slices that filled the window (150,000 tokens) were cut off at the ceiling every time.
        /// </summary>
        public int SliceTokens { get; init; } = 5_000;
        /// <summary>Read every slice a second time and adjudicate the difference. Off: one reading, then the verifier and the
        /// critic. Each reading is a model call of minutes on the background queue, and the second doubles them.</summary>
        public bool SecondReading { get; init; }
        /// <summary>How hard the model thinks on these calls ("off", "low", "medium", "high").</summary>
        public string ReasoningEffort { get; init; } = "low";
        public int MaxSlicesPerJob { get; init; } = 4;
        public double CriticThreshold { get; init; } = 0.4;
        public EducationSystem DefaultEducationSystem { get; init; } = EducationSystem.EnglandWales;
        public double Temperature { get; init; } = 0.6;
        public AssemblyOptions Assembly { get; init; } = new();
    }

    /// <summary>What one extraction job did (persisted in <c>kn_extraction_runs</c>; the Quality Lab and the Modules page read it).</summary>
    public sealed class ExtractionRunStats
    {
        public string RunId { get; init; } = "";
        public string PersonId { get; init; } = "";
        public int Slices { get; set; }
        public int Messages { get; set; }
        public int ClaimsRaw { get; set; }
        public int ClaimsAgreed { get; set; }
        public int ClaimsVerified { get; set; }
        public int ClaimsRejected { get; set; }
        public int LlmCalls { get; set; }
        public int Refusals { get; set; }
        public int Splits { get; set; }
        public bool Pending { get; set; }
        /// <summary>Anchors this prompt version had already read at the same message version (skipped: coverage).</summary>
        public int AlreadyRead { get; set; }
        public string? Model { get; set; }
        public Dictionary<string, int> Rejections { get; } = new(StringComparer.Ordinal);
        public HashSet<string> TouchedSubjects { get; } = new(StringComparer.Ordinal);
        public string Outcome { get; set; } = "ok";
    }

    /// <summary>
    /// One extraction job for one tracked person (plan "Extraction"; replaces v2's ExtractionJob, F020–F025, F176–F205).
    /// Uncovered evidence (per message version, on the ingest sequence) is assembled into whole-conversation slices that
    /// fill the model's real window; each slice is read once, or twice by independent sampled passes that share one cached
    /// prefix (the second pass is the same bytes on the same session, served from AIRouter's prefix cache), the claims
    /// only one reading found are adjudicated over that same prefix, everything left is verified deterministically and
    /// then judged by the critic, MinorGuard redacts restricted values before anything is written, and the slice's claims,
    /// age evidence and coverage commit in one transaction. Overflow or truncation splits the slice; a refusal is recorded
    /// as a refusal, never as "no facts".
    /// </summary>
    public sealed class ExtractionPipeline
    {
        private readonly KnowledgeKit kit;
        private readonly ClaimStore claims;
        private readonly Verifier verifier;
        private readonly EvidenceAssembler assembler;
        private readonly ExtractionOptions options;
        private readonly Func<IReadOnlyCollection<string>> tracked;
        private const int MaxSplitDepth = 3;

        /// <param name="tracked">The people who are read continuously (whom new messages are routed to). Default: the "tracked" tier.</param>
        public ExtractionPipeline(KnowledgeKit kit, ClaimStore claims, ExtractionOptions options, Func<IReadOnlyCollection<string>>? tracked = null)
        {
            this.kit = kit;
            this.claims = claims;
            this.options = options;
            this.tracked = tracked ?? (() => kit.Corpus.PersonsInTiers(new[] { "tracked" }));
            verifier = new Verifier(kit);
            assembler = new EvidenceAssembler(kit, options.Assembly);
        }

        public EvidenceAssembler Assembler => assembler;

        /// <summary>How many anchors a job wants waiting in the person's queue before it plans its slices: several jobs' worth,
        /// so most jobs find the queue full and look at no history at all.</summary>
        internal static int QueueTarget { get; set; } = 600;

        /// <summary>How long a job may spend looking through more of the person's history before it reads what it has. What is
        /// left is looked through by the next job (or by the sweep, which keeps the queues topped up outside the model lane).</summary>
        internal static TimeSpan TopUpBudget { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>How much of the change log a job routes before it reads (the sweep routes the rest).</summary>
        internal static int RouteChangesPerJob { get; set; } = 4_000;

        internal static TimeSpan RouteBudgetPerJob { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>How many of the person's messages the last run looked at to fill the queue (what finding its evidence cost).</summary>
        internal int LookedAt { get; private set; }

        /// <summary>The people whose queues gained something when the last run routed the change log: each needs a reading of
        /// their own (the caller queues it).</summary>
        public IReadOnlyCollection<string> RoutedTo { get; private set; } = Array.Empty<string>();

        private OmniLlm Llm => kit.Context.Llm ?? throw new JobFailureException(ErrorClass.Transient, "The LLM gateway is not available in this engine.");

        /// <summary>What one reading runs under: cancellation, the job its model calls are audited against (none for a tactic
        /// run), and the class AIRouter schedules them in (background for the continuous pipeline; interactive when Klives asked
        /// and is waiting).</summary>
        private sealed record SliceRun(CancellationToken Ct, long? JobId, AIRouterWorkClass WorkClass, Func<ExtractionRunStats, Task>? AfterSlice = null);

        /// <param name="afterSlice">Called after every slice whose claims were committed, so the caller can turn them into
        /// facts while the job is still reading. A job reads for hours and can fail on a later slice; claims that waited
        /// for the job to return never became facts at all.</param>
        public async Task<ExtractionRunStats> RunAsync(string personId, JobContext job, Func<ExtractionRunStats, Task>? afterSlice = null)
        {
            var ct = job.Cancellation;
            string canonical = kit.Corpus.Canonical(personId);
            var stats = new ExtractionRunStats { RunId = NaturalKeys.Id("xr", canonical + "|" + job.Job.JobId.ToString(CultureInfo.InvariantCulture) + "|" + job.Job.Attempts.ToString(CultureInfo.InvariantCulture), 20), PersonId = canonical };
            long started = kit.Context.Clock.NowMs;
            await CloseAbandonedRunsAsync(canonical, stats.RunId, started, ct).ConfigureAwait(false);
            await RecordRunAsync(stats, job.Job.JobId, started, null, ct).ConfigureAwait(false);

            long asOf = kit.Context.Clock.NowMs;
            // What is waiting to be read about this person is a queue (EvidenceQueue), not something worked out here. The
            // job used to begin by reading every message the person ever wrote, every reply to each and every mention: for
            // the three busiest people that never ended (twelve runs of three to fifteen hours, no model call, 21 Nov 2026),
            // and it was done again by every job. Now: what has arrived since the last look at the change log is routed,
            // the queue is topped up from the person's history by a page or two when it runs low, and its head is read.
            var queue = EvidenceQueue.For(kit);
            await job.ReportProgressAsync(new { person = canonical, stage = "evidence" }).ConfigureAwait(false);
            if (await queue.RouteAsync(tracked(), ExtractionPrompt.Version, RouteChangesPerJob, RouteBudgetPerJob, ct).ConfigureAwait(false) is { } routed)
                RoutedTo = routed.Persons;
            var topUp = await queue.TopUpAsync(canonical, ExtractionPrompt.Version, QueueTarget, TopUpBudget, ct, job.ReportProgressAsync).ConfigureAwait(false);
            LookedAt = topUp.Looked;
            ct.ThrowIfCancellationRequested();
            var unread = queue.Head(canonical, QueueTarget);
            stats.AlreadyRead = AlreadyRead(canonical);
            await job.ReportProgressAsync(new { person = canonical, stage = "planning", anchors = unread.Count, unread = topUp.Queued, lookedAt = topUp.Looked, historyDone = topUp.Complete })
                .ConfigureAwait(false);
            try
            {
                await ReadAsync(canonical, asOf, unread, stats, new SliceRun(ct, job.Job.JobId, AIRouterWorkClass.Background, afterSlice), job.ReportProgressAsync, queue).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The run is closed with what it managed, so it is not listed as running for ever; the job still fails.
                stats.Outcome = "failed";
                await RecordRunAsync(stats, job.Job.JobId, started, kit.Context.Clock.NowMs, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            // More is left whenever the queue is not empty or the history has not been looked through to its end.
            if (!topUp.Complete || queue.Count(canonical) > 0) stats.Pending = true;
            await RecordRunAsync(stats, job.Job.JobId, started, kit.Context.Clock.NowMs, ct).ConfigureAwait(false);
            return stats;
        }

        /// <summary>How many of the person's messages had been read before this run (the running count kept with their state).</summary>
        private int AlreadyRead(string canonical)
        {
            using var c = kit.Context.Stores.Intel.OpenRead();
            return (int)(c.Scalar<long?>(null, "SELECT covered_messages FROM kn_person_state WHERE person_id=$p", ("$p", canonical)) ?? 0);
        }

        /// <summary>Runs of this person still marked as running belong to an attempt that is gone (the service stopped, or
        /// the job was stopped): they are closed as interrupted, with whatever they had recorded.</summary>
        private Task CloseAbandonedRunsAsync(string person, string currentRunId, long nowMs, CancellationToken ct) =>
            kit.Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx,
                "UPDATE kn_extraction_runs SET outcome='interrupted', finished_at_ms=$n WHERE person_id=$p AND outcome='running' AND run_id<>$r",
                ("$n", nowMs), ("$p", person), ("$r", currentRunId)), ct);

        /// <summary>
        /// X17 (extract from a selection): the same reading, limited to chosen messages — the subject's anchors among them
        /// (<see cref="EvidenceAssembler.AnchorsAmong"/>), less what this prompt version already read at the same message
        /// version. Everything after the anchors is the continuous pipeline's: whole-conversation slices, two readings,
        /// adjudication, deterministic verification, the critic, MinorGuard before anything is written, and one commit per slice.
        /// A resumed run keeps its run id (<paramref name="runKey"/>), and coverage keeps it from reading anything twice.
        /// </summary>
        public async Task<ExtractionRunStats> RunSelectedAsync(string personId, IReadOnlyCollection<string> messageIds, string runKey, CancellationToken ct, Func<object, Task> progress)
        {
            string canonical = kit.Corpus.Canonical(personId);
            var stats = new ExtractionRunStats { RunId = NaturalKeys.Id("xr", canonical + "|selection|" + runKey, 20), PersonId = canonical };
            long started = kit.Context.Clock.NowMs;
            await RecordRunAsync(stats, null, started, null, ct).ConfigureAwait(false);
            var identities = kit.Corpus.IdentitiesOf(kit.Corpus.PersonIdsOf(canonical)).Select(i => i.IdentityId).ToList();
            long asOf = kit.Context.Clock.NowMs;
            var anchors = assembler.AnchorsAmong(messageIds, identities, asOf);
            var uncovered = assembler.Uncovered(canonical, anchors, ExtractionPrompt.Version);
            stats.AlreadyRead = anchors.Count - uncovered.Count;
            await ReadAsync(canonical, asOf, uncovered, stats, new SliceRun(ct, null, AIRouterWorkClass.Interactive), progress, null).ConfigureAwait(false);
            await RecordRunAsync(stats, null, started, kit.Context.Clock.NowMs, ct).ConfigureAwait(false);
            return stats;
        }

        /// <summary>Reads uncovered anchors: plans them oldest first, packs whole-conversation slices into the model's window
        /// and processes at most <see cref="ExtractionOptions.MaxSlicesPerJob"/> of them (the rest stay pending).</summary>
        /// <param name="queue">The person's queue when the anchors came from it: an anchor that cannot be read any more (its
        /// message is gone, or its author's analysis is switched off) is taken out, or it would head the queue for ever.</param>
        private async Task ReadAsync(string canonical, long asOf, IReadOnlyList<EvidenceAnchor> uncovered, ExtractionRunStats stats, SliceRun run, Func<object, Task> progress,
            EvidenceQueue? queue)
        {
            var ct = run.Ct;
            if (uncovered.Count == 0)
            {
                stats.Outcome = "nothing_new";
                return;
            }
            var planned = uncovered.Take(options.Assembly.MaxAnchorsPerJob).ToList();

            var info = await Llm.ContextAsync(null, ct).ConfigureAwait(false);
            maxOutput = Math.Min(options.MaxOutputTokens, Math.Max(1_024, info.MaxOutputTokens));
            var estimator = Llm.Estimator;
            string profile = ProfileText(canonical, asOf);
            int overhead = estimator.Estimate(ExtractionPrompt.System) + estimator.Estimate(profile) + estimator.Estimate(ExtractionPrompt.ExtractSchema.Schema.ToString())
                           + estimator.Estimate(ExtractionPrompt.ExtractQuestion) + 2_000; // roster and headers
            // The window bounds what can be sent; the output ceiling bounds what can come back, and it is the tighter one.
            int window = Math.Max(2_000, (int)((ContextPacker.Budget(info, maxOutput) - overhead) * 0.95));
            int budget = Math.Min(window, Math.Max(1_000, Math.Min(options.SliceTokens, maxOutput / 2)));
            // Only as much is prepared as this job will read. A job reads a few slices (MaxSlicesPerJob), each a few thousand
            // tokens, so a few hundred messages; preparing a slice means reading the conversation around every anchor in it
            // and rendering it. All of the planned anchors used to be prepared first, up to 4,000 of them (a limit from when
            // a slice filled the model's whole window), of which the job then read the first hundred or so and threw the
            // rest away, on every job. The anchors are now taken a page at a time, oldest first, until there are enough
            // slices for this job and one more (to know that something is left).
            var slices = new List<EvidenceSlice>();
            int prepared = 0;
            while (prepared < planned.Count && slices.Count <= options.MaxSlicesPerJob)
            {
                ct.ThrowIfCancellationRequested();
                var page = planned.Skip(prepared).Take(PlanPage).ToList();
                prepared += page.Count;
                var packed = assembler.Pack(canonical, assembler.Segments(page), budget, estimator);
                if (queue != null)
                {
                    var readable = packed.SelectMany(s => s.AnchorIds).ToHashSet(StringComparer.Ordinal);
                    var gone = page.Where(a => !readable.Contains(a.MessageId)).Select(a => a.MessageId).ToList();
                    if (gone.Count > 0) await queue.DropAsync(canonical, gone, ct).ConfigureAwait(false);
                }
                foreach (var slice in packed) slices.Add(slice with { Index = slices.Count });
                await progress(new { person = canonical, stage = "preparing", anchors = prepared, of = planned.Count, slices = slices.Count }).ConfigureAwait(false);
            }
            PreparedAnchors = prepared;
            long sourceSeq = kit.Corpus.MaxSeq();
            int done = 0;
            foreach (var slice in slices)
            {
                ct.ThrowIfCancellationRequested();
                if (done >= options.MaxSlicesPerJob)
                {
                    stats.Pending = true;
                    break;
                }
                await ProcessSliceAsync(canonical, slice, profile, budget, estimator, stats, run, sourceSeq, 0).ConfigureAwait(false);
                done++;
                await progress(new { person = canonical, slice = done, slices = slices.Count, claims = stats.ClaimsVerified, rejected = stats.ClaimsRejected }).ConfigureAwait(false);
                if (run.AfterSlice != null) await run.AfterSlice(stats).ConfigureAwait(false);
            }
            if (prepared < planned.Count || uncovered.Count > planned.Count) stats.Pending = true;
        }

        /// <summary>How many anchors are prepared at a time while a job's slices are put together.</summary>
        internal static int PlanPage { get; set; } = 200;

        /// <summary>How many anchors the last run prepared slices for (what its planning cost).</summary>
        internal int PreparedAnchors { get; private set; }

        private async Task ProcessSliceAsync(string subject, EvidenceSlice slice, string profile, int budget, TokenEstimator estimator, ExtractionRunStats stats,
            SliceRun run, long sourceSeq, int depth)
        {
            var ct = run.Ct;
            string subjectHandle = slice.Roster.HandleFor(subject);
            var extra = slice.Roster.Entries.Where(e => e.Role != RosterRole.Subject && e.Tracked).Select(e => e.Handle).ToList();
            string prefix = ExtractionPrompt.Prefix(slice, subjectHandle, profile, extra);
            string sessionKey = "omniscience-bg-deduction-" + NaturalKeys.Sha256Hex(subject + "|" + slice.Index.ToString(CultureInfo.InvariantCulture) + "|" + stats.RunId)[..16];

            // One session for everything asked about this slice. Every question (the reading, a second reading, the
            // adjudicator, the critic) is a question of its own over the same prefix and says everything it is about, so
            // each goes out as the prefix plus itself: the provider prefills the evidence once, for the first reading,
            // and serves it from cache for the rest, and no reading's answer is ever sent back. They all carry the same
            // schemas, because the tool definitions sit ahead of the evidence in what the provider reads.
            using var s = Llm.OpenSession(KnowledgeSchema.DeductionModule, "deduction.extract", sessionKey, ExtractionPrompt.System, prefix, run.WorkClass,
                ExtractionPrompt.Version, run.JobId, options.ReasoningEffort, ExtractionPrompt.Schemas(options.SecondReading));
            var a = await StructuredLlm.AskAsync<ExtractionAnswer>(s, ExtractionPrompt.ExtractQuestion, ExtractionPrompt.ExtractSchema, MaxOutput(), Sampling(1), ExtractionPrompt.Validate, ct).ConfigureAwait(false);
            stats.LlmCalls += a.Calls;
            if (a.Outcome is StructuredOutcome.Overflow or StructuredOutcome.Truncated)
            {
                await SplitAsync(subject, slice, profile, budget, estimator, stats, run, sourceSeq, depth).ConfigureAwait(false);
                return;
            }
            if (a.Outcome == StructuredOutcome.Refused)
            {
                stats.Refusals++;
                await CommitAsync(slice, subject, "refused", new List<VerifiedClaim>(), new List<RejectedClaim>(), new List<AgeEvidenceRow>(), stats, sourceSeq, ct).ConfigureAwait(false);
                return;
            }
            if (a.Outcome is StructuredOutcome.Failed or StructuredOutcome.Invalid && a.Value == null)
                throw new JobFailureException(ErrorClass.Transient, $"Extraction pass A failed ({a.Outcome}): {a.Problem}");
            stats.Model ??= a.Result?.Model;

            // Pass B (the same bytes as pass A, another seed: served from the prefix cache), then adjudication and the critic
            // (each the prefix and its own question; the session sees to it that a long critic after a long adjudication is
            // not measured against it by AIRouter's scheduler).
            List<(RawClaim Claim, string Agreement)> agreed;
            var verified = new List<VerifiedClaim>();
            var rejected = new List<RejectedClaim>();
            {
                var b = options.SecondReading
                    ? await StructuredLlm.AskAsync<ExtractionAnswer>(s, ExtractionPrompt.ExtractQuestion, ExtractionPrompt.ExtractSchema, MaxOutput(), Sampling(2), ExtractionPrompt.Validate, ct).ConfigureAwait(false)
                    : new StructuredAnswer<ExtractionAnswer>(null, StructuredOutcome.Ok, null, null, 0);
                stats.LlmCalls += b.Calls;
                var aClaims = a.Value?.Claims ?? new List<RawClaim>();
                stats.ClaimsRaw += aClaims.Count + (b.Value?.Claims.Count ?? 0);
                if (b.Value == null)
                {
                    // One reading only (B refused, failed or truncated): no agreement signal; claims go on at reduced weight.
                    agreed = aClaims.Select(c => (c, Agreement.Single)).ToList();
                }
                else
                {
                    var (both, disputed) = Match(aClaims, b.Value.Claims);
                    agreed = both.Select(c => (c, Agreement.Both)).ToList();
                    if (disputed.Count > 0)
                    {
                        var adjudication = await StructuredLlm.AskAsync<AdjudicationAnswer>(s, ExtractionPrompt.AdjudicateQuestion(disputed), ExtractionPrompt.AdjudicateSchema,
                            MaxOutput(), Sampling(3), x => x.Decisions == null ? "No decisions." : null, ct).ConfigureAwait(false);
                        stats.LlmCalls += adjudication.Calls;
                        var decisions = adjudication.Value?.Decisions.Where(d => d.Id != null).GroupBy(d => d.Id!).ToDictionary(g => g.Key, g => g.Last()) ?? new();
                        foreach (var (id, claim, _) in disputed)
                        {
                            if (decisions.TryGetValue(id, out var d) && d.Keep) agreed.Add((d.Claim ?? claim, Agreement.Adjudicated));
                            else rejected.Add(RejectedRaw(claim, slice, subject, RejectReasons.AdjudicatedOut, d?.Reason ?? "No adjudication.", stats.RunId));
                        }
                    }
                }
                stats.ClaimsAgreed += agreed.Count;

                foreach (var (claim, agreement) in agreed)
                {
                    var outcome = await verifier.VerifyAsync(claim, slice, stats.RunId, ExtractionPrompt.Version, stats.Model, agreement, ct).ConfigureAwait(false);
                    if (outcome.Claim != null) verified.Add(outcome.Claim);
                    else rejected.Add(RejectedRaw(claim, slice, subject, outcome.RejectReason ?? RejectReasons.InvalidClaim, outcome.Detail, stats.RunId));
                }

                // The critic: an adversarial "is this proven?" pass over every verified claim above the threshold, evidence in context.
                var toJudge = verified.Where(v => v.Probability >= options.CriticThreshold && v.Age == null).ToList();
                if (toJudge.Count > 0)
                {
                    var items = toJudge.Select((v, i) => ("f" + (i + 1).ToString(CultureInfo.InvariantCulture), Describe(v, slice), (IReadOnlyList<string>)v.Evidence.Select(e => e.Ref).ToList())).ToList();
                    var critic = await StructuredLlm.AskAsync<CriticAnswer>(s, ExtractionPrompt.CriticQuestion(items), ExtractionPrompt.CriticSchema, Math.Min(MaxOutput(), 4_000),
                        Sampling(4), x => x.Verdicts == null ? "No verdicts." : null, ct).ConfigureAwait(false);
                    stats.LlmCalls += critic.Calls;
                    var verdicts = critic.Value?.Verdicts.Where(v => v.Id != null).GroupBy(v => v.Id!).ToDictionary(g => g.Key, g => g.Last()) ?? new();
                    for (int i = 0; i < toJudge.Count; i++)
                    {
                        string id = items[i].Item1;
                        var v = toJudge[i];
                        verdicts.TryGetValue(id, out var verdict);
                        string? label = verdict?.Verdict is CriticVerdicts.Supported or CriticVerdicts.Unsupported or CriticVerdicts.Insufficient ? verdict.Verdict : null;
                        int index = verified.IndexOf(v);
                        if (label == CriticVerdicts.Unsupported)
                        {
                            verified.RemoveAt(index);
                            rejected.Add(new RejectedClaim(v.ClaimId, v.SubjectPersonId, v.Attribute, v.ValueText, RejectReasons.CriticUnsupported, verdict?.Reason,
                                v.Evidence.Select(e => e.MessageId).ToList(), v.Probability, v.Modality, v.SourceKind, v.AssertedAtMs, v.AgreementKind,
                                v.Evidence[0].ConversationId, v.Evidence[0].GuildId, v.Evidence[0].Platform, v.Evidence[0].AuthorPersonId));
                        }
                        else verified[index] = v with { Critic = label };
                    }
                }
            }

            // Age evidence first (policy only), then MinorGuard on everything else before a single value is written.
            // A corrected statement ("I'm 32" → "actually 23, typo") never stays age evidence; the correction is the evidence.
            var correctedMessages = verified.Where(v => v.CorrectsMessageId != null).Select(v => v.CorrectsMessageId!).ToHashSet(StringComparer.Ordinal);
            var ages = verified.Where(v => !v.Evidence.All(e => correctedMessages.Contains(e.MessageId)))
                .Select(v => AgeEvidenceBuilder.FromClaim(v, options.DefaultEducationSystem)).OfType<AgeEvidenceRow>().ToList();
            if (correctedMessages.Count > 0) await claims.RetractAgeEvidenceAsync(correctedMessages, ct).ConfigureAwait(false);
            var kept = new List<VerifiedClaim>();
            foreach (var group in verified.GroupBy(v => v.SubjectPersonId, StringComparer.Ordinal))
            {
                var band = BandWith(group.Key, ages.Where(x => x.PersonId == group.Key).ToList());
                foreach (var v in group)
                {
                    if (Attributes.IsBlockedFor(v.Attribute, band) || (v.ObjectPersonId != null && Attributes.IsBlockedFor(v.Attribute, BandWith(v.ObjectPersonId, ages.Where(x => x.PersonId == v.ObjectPersonId).ToList()))))
                        rejected.Add(new RejectedClaim(v.ClaimId, v.SubjectPersonId, v.Attribute, null, RejectReasons.RestrictedForMinor, null,
                            v.Evidence.Select(e => e.MessageId).ToList(), v.Probability, v.Modality, v.SourceKind, v.AssertedAtMs, v.AgreementKind, v.Evidence[0].ConversationId,
                            v.Evidence[0].GuildId, v.Evidence[0].Platform, v.Evidence[0].AuthorPersonId));
                    else kept.Add(v);
                }
            }
            await CommitAsync(slice, subject, "ok", kept, rejected, ages, stats, sourceSeq, ct).ConfigureAwait(false);
            if (ages.Count > 0 && kit.Context.Policy is EvidencePolicyOracle oracle)
                foreach (var p in ages.Select(x => x.PersonId).Distinct(StringComparer.Ordinal)) oracle.Invalidate(p);
        }

        private AgeBand BandWith(string personId, IReadOnlyList<AgeEvidenceRow> fresh)
        {
            long now = kit.Context.Clock.NowMs;
            var band = kit.Context.Policy.BandOf(personId, now);
            if (fresh.Count == 0) return band;
            IReadOnlyList<AgeEvidenceRow> existing = kit.Context.Policy is EvidencePolicyOracle o ? o.EvidenceFor(kit.Corpus.Canonical(personId)) : Array.Empty<AgeEvidenceRow>();
            return AgeBands.Stricter(band, AgeBands.Evaluate(existing.Concat(fresh).ToList(), now));
        }

        private async Task SplitAsync(string subject, EvidenceSlice slice, string profile, int budget, TokenEstimator estimator, ExtractionRunStats stats, SliceRun run,
            long sourceSeq, int depth)
        {
            stats.Splits++;
            if (depth >= MaxSplitDepth || (slice.Segments.Count <= 1 && slice.Segments.Sum(s => s.Messages.Count) <= 1))
            {
                // Cannot shrink further: record it honestly and move on (the Quality Lab shows these as "too large").
                await CommitAsync(slice, subject, "too_large", new List<VerifiedClaim>(), new List<RejectedClaim>(), new List<AgeEvidenceRow>(), stats, sourceSeq, run.Ct).ConfigureAwait(false);
                return;
            }
            foreach (var part in assembler.Pack(subject, slice.Segments, Math.Max(1_000, budget / 2), estimator))
                await ProcessSliceAsync(subject, part with { Index = slice.Index * 10 + part.Index + 1 }, profile, budget / 2, estimator, stats, run, sourceSeq, depth + 1).ConfigureAwait(false);
        }

        private async Task CommitAsync(EvidenceSlice slice, string subject, string outcome, List<VerifiedClaim> verified, List<RejectedClaim> rejected,
            List<AgeEvidenceRow> ages, ExtractionRunStats stats, long sourceSeq, CancellationToken ct)
        {
            var anchorSet = slice.AnchorIds.ToHashSet(StringComparer.Ordinal);
            var anchors = slice.Messages.Where(m => anchorSet.Contains(m.MessageId)).Select(m => (m.MessageId, m.Version)).ToList();
            await claims.CommitAsync(new SliceCommit(stats.RunId, subject, anchors, outcome, verified, rejected, ages, sourceSeq), ExtractionPrompt.Version, stats.Model, ct).ConfigureAwait(false);
            stats.Slices++;
            stats.Messages += anchors.Count;
            stats.ClaimsVerified += verified.Count;
            stats.ClaimsRejected += rejected.Count;
            foreach (var r in rejected) stats.Rejections[r.Reason] = stats.Rejections.GetValueOrDefault(r.Reason) + 1;
            foreach (var v in verified) stats.TouchedSubjects.Add(v.SubjectPersonId);
            foreach (var a in ages) stats.TouchedSubjects.Add(a.PersonId);
        }

        /// <summary>The output ceiling of this run: the setting, or the model's own limit when that is lower.</summary>
        private int maxOutput;

        private int MaxOutput() => maxOutput > 0 ? maxOutput : options.MaxOutputTokens;

        private ModelSamplingParameters Sampling(int seed) => new(Temperature: options.Temperature, Seed: seed);

        /// <summary>Pairs the claims of two readings: the same subject, attribute, value (or other person) and polarity agree;
        /// everything else (one reading only, or a different modality/source) is disputed.</summary>
        internal static (List<RawClaim> Both, List<(string Id, RawClaim Claim, string Pass)> Disputed) Match(IReadOnlyList<RawClaim> a, IReadOnlyList<RawClaim> b)
        {
            static string Key(RawClaim c) => string.Join("|", (c.Subject ?? "").Trim().ToLowerInvariant(), (c.Attribute ?? "").Trim().ToLowerInvariant(),
                c.Object != null ? "o:" + c.Object.Trim().ToLowerInvariant() : "v:" + NaturalKeys.Normalize(c.Value ?? c.Entity?.Name), c.Polarity == Polarity.Negated ? "neg" : "aff");
            var bByKey = b.GroupBy(Key).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var both = new List<RawClaim>();
            var disputed = new List<(string, RawClaim, string)>();
            var usedB = new HashSet<RawClaim>();
            int n = 0;
            foreach (var ca in a)
            {
                string key = Key(ca);
                var cb = bByKey.TryGetValue(key, out var list) ? list.FirstOrDefault(x => !usedB.Contains(x)) : null;
                if (cb == null) { disputed.Add(("d" + (++n).ToString(CultureInfo.InvariantCulture), ca, "A")); continue; }
                usedB.Add(cb);
                bool sameReading = string.Equals(ca.Modality ?? Modality.Literal, cb.Modality ?? Modality.Literal, StringComparison.Ordinal)
                                   && string.Equals(ca.SourceKind, cb.SourceKind, StringComparison.Ordinal);
                if (!sameReading)
                {
                    disputed.Add(("d" + (++n).ToString(CultureInfo.InvariantCulture), ca, "A"));
                    disputed.Add(("d" + (++n).ToString(CultureInfo.InvariantCulture), cb, "B"));
                    continue;
                }
                // Agreed: keep the better-supported reading, with the union of the evidence both cited.
                var keep = (cb.Evidence?.Count ?? 0) > (ca.Evidence?.Count ?? 0) || (cb.Probability ?? 0) > (ca.Probability ?? 0) + 0.15 ? cb : ca;
                var other = ReferenceEquals(keep, ca) ? cb : ca;
                var evidence = (keep.Evidence ?? new()).Concat(other.Evidence ?? new())
                    .GroupBy(e => EvidenceSlice.NormalizeRef(e.Ref) + "|" + NaturalKeys.Normalize(e.Quote)).Select(g => g.First()).ToList();
                keep.Evidence = evidence;
                keep.Probability = Math.Round(((ca.Probability ?? 0.6) + (cb.Probability ?? 0.6)) / 2, 4);
                both.Add(keep);
            }
            foreach (var cb in b.Where(x => !usedB.Contains(x))) disputed.Add(("d" + (++n).ToString(CultureInfo.InvariantCulture), cb, "B"));
            return (both, disputed);
        }

        private static string Describe(VerifiedClaim v, EvidenceSlice slice)
        {
            string who = slice.Roster.HandleFor(v.SubjectPersonId);
            string not = v.Polarity == Polarity.Negated ? "NOT " : "";
            string obj = v.ObjectPersonId != null ? " (" + slice.Roster.HandleFor(v.ObjectPersonId) + ")" : "";
            return $"{who}: {not}{Attributes.Get(v.Attribute).Title.ToLowerInvariant()} = \"{v.ValueText}\"{obj} [{v.SourceKind}]";
        }

        private static RejectedClaim RejectedRaw(RawClaim c, EvidenceSlice slice, string subject, string reason, string? detail, string runId)
        {
            string person = (c.Subject == null ? null : slice.Roster.PersonFor(c.Subject)) ?? subject;
            var messages = (c.Evidence ?? new()).Select(e => slice.ByRef(e.Ref ?? "")?.MessageId).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            var first = messages.Count > 0 ? slice.ById(messages[0]) : null;
            string id = NaturalKeys.Id("cl", runId + "|r|" + slice.Index.ToString(CultureInfo.InvariantCulture) + "|" + (c.Id ?? "") + "|" + person + "|" + c.Attribute + "|" + NaturalKeys.Normalize(c.Value) + "|" + reason);
            // A sensitive or restricted value that never passed verification is not kept either: only the reason is.
            // Kept as it will be read: with the people named, not their handles (the id above is of the claim as it came).
            string? value = Attributes.IsSensitive(c.Attribute ?? "") || Attributes.Get(c.Attribute).Sensitivity == AttributeSensitivity.RestrictedForMinors || c.Value == null
                ? null : HandleText.Named(c.Value, slice.Roster.NameFor);
            return new RejectedClaim(id, person, Attributes.TryGet(c.Attribute, out var d) ? d.Id : Attributes.Other, value, reason, detail, messages, c.Probability,
                c.Modality, c.SourceKind, first?.SentAtMs ?? 0, "", first?.ConversationId, first?.GuildId, first?.Platform, first?.AuthorPersonId);
        }

        /// <summary>The consolidated profile so far, as read-only context for the next slice (F186/F212): stated facts only, never inferences.</summary>
        public string ProfileText(string canonical, long asOfMs)
        {
            var facts = kit.Assertions.Query(new AssertionQuery
            {
                SubjectIds = kit.Corpus.PersonIdsOf(canonical),
                AtMs = asOfMs,
                Producer = Producers.Extract,
                Statuses = new[] { AssertionStatus.Accepted, AssertionStatus.Confirmed },
                IncludeInferred = false,
                Limit = 400,
            });
            var sb = new StringBuilder();
            foreach (var f in facts)
            {
                var r = f.Record;
                string valid = r.ValidTo != null ? " (until ~" + Instant.Iso(r.ValidToLatestMs ?? r.ValidToEarliestMs ?? 0)[..10] + ")" : "";
                string not = r.Value.Polarity == Polarity.Negated ? "not " : "";
                sb.Append("- ").Append(f.Definition.Title).Append(": ").Append(not).Append(EvidenceRenderer.Sanitize(r.ValueText ?? r.Value.Text)).Append(valid)
                    .Append(r.Status == AssertionStatus.Confirmed ? " [confirmed by Klives]" : "").Append('\n');
            }
            return sb.ToString().TrimEnd();
        }

        private Task RecordRunAsync(ExtractionRunStats s, long? jobId, long startedMs, long? finishedMs, CancellationToken ct) =>
            kit.Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO kn_extraction_runs(run_id, person_id, job_id, prompt_version, model, slices, messages, claims_raw, claims_agreed, claims_verified, claims_rejected,
                    rejections_json, llm_calls, outcome, started_at_ms, finished_at_ms)
                VALUES($r,$p,$j,$pv,$m,$s,$msg,$raw,$agr,$ver,$rej,$rj,$calls,$o,$st,$fin)
                ON CONFLICT(run_id) DO UPDATE SET model=excluded.model, slices=excluded.slices, messages=excluded.messages, claims_raw=excluded.claims_raw,
                    claims_agreed=excluded.claims_agreed, claims_verified=excluded.claims_verified, claims_rejected=excluded.claims_rejected,
                    rejections_json=excluded.rejections_json, llm_calls=excluded.llm_calls, outcome=excluded.outcome, finished_at_ms=excluded.finished_at_ms
                """, ("$r", s.RunId), ("$p", s.PersonId), ("$j", jobId), ("$pv", ExtractionPrompt.Version), ("$m", s.Model), ("$s", s.Slices), ("$msg", s.Messages),
                ("$raw", s.ClaimsRaw), ("$agr", s.ClaimsAgreed), ("$ver", s.ClaimsVerified), ("$rej", s.ClaimsRejected),
                ("$rj", JsonSerializer.Serialize(new { s.Rejections, s.Refusals, s.Splits, pending = s.Pending, alreadyRead = s.AlreadyRead }, KernelJson.Options)), ("$calls", s.LlmCalls),
                ("$o", finishedMs == null ? "running" : s.Outcome), ("$st", startedMs), ("$fin", finishedMs)), ct);
    }
}
