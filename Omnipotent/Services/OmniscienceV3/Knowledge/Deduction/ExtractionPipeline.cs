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
        public int MaxOutputTokens { get; init; } = 12_000;
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
        public string? Model { get; set; }
        public Dictionary<string, int> Rejections { get; } = new(StringComparer.Ordinal);
        public HashSet<string> TouchedSubjects { get; } = new(StringComparer.Ordinal);
        public string Outcome { get; set; } = "ok";
    }

    /// <summary>
    /// One extraction job for one tracked person (plan "Extraction"; replaces v2's ExtractionJob, F020–F025, F176–F205).
    /// Uncovered evidence (per message version, on the ingest sequence) is assembled into whole-conversation slices that
    /// fill the model's real window; each slice is read twice by independent sampled passes that share one cached prefix
    /// (the second pass is served from AIRouter's prefix cache: a fresh session on the same key and identical bytes), the
    /// claims only one reading found are adjudicated in the same cohort, everything left is verified deterministically and
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
        private const int MaxSplitDepth = 3;

        public ExtractionPipeline(KnowledgeKit kit, ClaimStore claims, ExtractionOptions options)
        {
            this.kit = kit;
            this.claims = claims;
            this.options = options;
            verifier = new Verifier(kit);
            assembler = new EvidenceAssembler(kit, options.Assembly);
        }

        public EvidenceAssembler Assembler => assembler;

        private OmniLlm Llm => kit.Context.Llm ?? throw new JobFailureException(ErrorClass.Transient, "The LLM gateway is not available in this engine.");

        public async Task<ExtractionRunStats> RunAsync(string personId, JobContext job)
        {
            var ct = job.Cancellation;
            string canonical = kit.Corpus.Canonical(personId);
            var stats = new ExtractionRunStats { RunId = NaturalKeys.Id("xr", canonical + "|" + job.Job.JobId.ToString(CultureInfo.InvariantCulture) + "|" + job.Job.Attempts.ToString(CultureInfo.InvariantCulture), 20), PersonId = canonical };
            long started = kit.Context.Clock.NowMs;
            await RecordRunAsync(stats, job.Job.JobId, started, null, ct).ConfigureAwait(false);

            var ids = kit.Corpus.PersonIdsOf(canonical);
            var identities = kit.Corpus.IdentitiesOf(ids).Select(i => i.IdentityId).ToList();
            long asOf = kit.Context.Clock.NowMs;
            var anchors = assembler.Anchors(identities, asOf);
            var uncovered = assembler.Uncovered(canonical, anchors, ExtractionPrompt.Version);
            if (uncovered.Count == 0)
            {
                stats.Outcome = "nothing_new";
                await RecordRunAsync(stats, job.Job.JobId, started, kit.Context.Clock.NowMs, ct).ConfigureAwait(false);
                return stats;
            }
            var planned = uncovered.Take(options.Assembly.MaxAnchorsPerJob).ToList();
            var segments = assembler.Segments(planned);

            var info = await Llm.ContextAsync(null, ct).ConfigureAwait(false);
            int output = Math.Min(options.MaxOutputTokens, Math.Max(1_024, info.MaxOutputTokens));
            var estimator = Llm.Estimator;
            string profile = ProfileText(canonical, asOf);
            int overhead = estimator.Estimate(ExtractionPrompt.System) + estimator.Estimate(profile) + estimator.Estimate(ExtractionPrompt.ExtractSchema.Schema.ToString())
                           + estimator.Estimate(ExtractionPrompt.ExtractQuestion) + 2_000; // roster and headers
            int budget = Math.Max(2_000, (int)((ContextPacker.Budget(info, output) - overhead) * 0.95));
            var slices = assembler.Pack(canonical, segments, budget, estimator);
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
                await ProcessSliceAsync(canonical, slice, profile, budget, estimator, stats, job, sourceSeq, 0).ConfigureAwait(false);
                done++;
                await job.ReportProgressAsync(new { person = canonical, slice = done, slices = slices.Count, claims = stats.ClaimsVerified, rejected = stats.ClaimsRejected }).ConfigureAwait(false);
            }
            if (uncovered.Count > planned.Count) stats.Pending = true;
            await RecordRunAsync(stats, job.Job.JobId, started, kit.Context.Clock.NowMs, ct).ConfigureAwait(false);
            return stats;
        }

        private async Task ProcessSliceAsync(string subject, EvidenceSlice slice, string profile, int budget, TokenEstimator estimator, ExtractionRunStats stats,
            JobContext job, long sourceSeq, int depth)
        {
            var ct = job.Cancellation;
            string subjectHandle = slice.Roster.HandleFor(subject);
            var extra = slice.Roster.Entries.Where(e => e.Role != RosterRole.Subject && e.Tracked).Select(e => e.Handle).ToList();
            string prefix = ExtractionPrompt.Prefix(slice, subjectHandle, profile, extra);
            string sessionKey = "omniscience-bg-deduction-" + NaturalKeys.Sha256Hex(subject + "|" + slice.Index.ToString(CultureInfo.InvariantCulture) + "|" + stats.RunId)[..16];

            // Pass A: its own session.
            StructuredAnswer<ExtractionAnswer> a;
            using (var s1 = Llm.OpenSession(KnowledgeSchema.DeductionModule, "deduction.extract", sessionKey, ExtractionPrompt.System, prefix, AIRouterWorkClass.Background,
                       ExtractionPrompt.Version, job.Job.JobId))
            {
                a = await StructuredLlm.AskAsync<ExtractionAnswer>(s1, ExtractionPrompt.ExtractQuestion, ExtractionPrompt.ExtractSchema, MaxOutput(), Sampling(1), ExtractionPrompt.Validate, ct).ConfigureAwait(false);
                stats.LlmCalls += a.Calls;
            }
            if (a.Outcome is StructuredOutcome.Overflow or StructuredOutcome.Truncated)
            {
                await SplitAsync(subject, slice, profile, budget, estimator, stats, job, sourceSeq, depth).ConfigureAwait(false);
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

            // Pass B (fresh session, same key and bytes: served from the prefix cache), then adjudication and the critic as continuations.
            List<(RawClaim Claim, string Agreement)> agreed;
            var verified = new List<VerifiedClaim>();
            var rejected = new List<RejectedClaim>();
            using (var s2 = Llm.OpenSession(KnowledgeSchema.DeductionModule, "deduction.extract", sessionKey, ExtractionPrompt.System, prefix, AIRouterWorkClass.Background,
                       ExtractionPrompt.Version, job.Job.JobId))
            {
                var b = await StructuredLlm.AskAsync<ExtractionAnswer>(s2, ExtractionPrompt.ExtractQuestion, ExtractionPrompt.ExtractSchema, MaxOutput(), Sampling(2), ExtractionPrompt.Validate, ct).ConfigureAwait(false);
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
                        var adjudication = await StructuredLlm.AskAsync<AdjudicationAnswer>(s2, ExtractionPrompt.AdjudicateQuestion(disputed), ExtractionPrompt.AdjudicateSchema,
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
                    var critic = await StructuredLlm.AskAsync<CriticAnswer>(s2, ExtractionPrompt.CriticQuestion(items), ExtractionPrompt.CriticSchema, Math.Min(MaxOutput(), 4_000),
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
            var ages = verified.Select(v => AgeEvidenceBuilder.FromClaim(v, options.DefaultEducationSystem)).OfType<AgeEvidenceRow>().ToList();
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

        private async Task SplitAsync(string subject, EvidenceSlice slice, string profile, int budget, TokenEstimator estimator, ExtractionRunStats stats, JobContext job,
            long sourceSeq, int depth)
        {
            stats.Splits++;
            if (depth >= MaxSplitDepth || (slice.Segments.Count <= 1 && slice.Segments.Sum(s => s.Messages.Count) <= 1))
            {
                // Cannot shrink further: record it honestly and move on (the Quality Lab shows these as "too large").
                await CommitAsync(slice, subject, "too_large", new List<VerifiedClaim>(), new List<RejectedClaim>(), new List<AgeEvidenceRow>(), stats, sourceSeq, job.Cancellation).ConfigureAwait(false);
                return;
            }
            foreach (var part in assembler.Pack(subject, slice.Segments, Math.Max(1_000, budget / 2), estimator))
                await ProcessSliceAsync(subject, part with { Index = slice.Index * 10 + part.Index + 1 }, profile, budget / 2, estimator, stats, job, sourceSeq, depth + 1).ConfigureAwait(false);
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

        private int MaxOutput() => options.MaxOutputTokens;

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
            string? value = Attributes.IsSensitive(c.Attribute ?? "") || Attributes.Get(c.Attribute).Sensitivity == AttributeSensitivity.RestrictedForMinors ? null : c.Value;
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

        private Task RecordRunAsync(ExtractionRunStats s, long jobId, long startedMs, long? finishedMs, CancellationToken ct) =>
            kit.Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO kn_extraction_runs(run_id, person_id, job_id, prompt_version, model, slices, messages, claims_raw, claims_agreed, claims_verified, claims_rejected,
                    rejections_json, llm_calls, outcome, started_at_ms, finished_at_ms)
                VALUES($r,$p,$j,$pv,$m,$s,$msg,$raw,$agr,$ver,$rej,$rj,$calls,$o,$st,$fin)
                ON CONFLICT(run_id) DO UPDATE SET model=excluded.model, slices=excluded.slices, messages=excluded.messages, claims_raw=excluded.claims_raw,
                    claims_agreed=excluded.claims_agreed, claims_verified=excluded.claims_verified, claims_rejected=excluded.claims_rejected,
                    rejections_json=excluded.rejections_json, llm_calls=excluded.llm_calls, outcome=excluded.outcome, finished_at_ms=excluded.finished_at_ms
                """, ("$r", s.RunId), ("$p", s.PersonId), ("$j", jobId), ("$pv", ExtractionPrompt.Version), ("$m", s.Model), ("$s", s.Slices), ("$msg", s.Messages),
                ("$raw", s.ClaimsRaw), ("$agr", s.ClaimsAgreed), ("$ver", s.ClaimsVerified), ("$rej", s.ClaimsRejected),
                ("$rj", JsonSerializer.Serialize(new { s.Rejections, s.Refusals, s.Splits, pending = s.Pending }, KernelJson.Options)), ("$calls", s.LlmCalls),
                ("$o", finishedMs == null ? "running" : s.Outcome), ("$st", startedMs), ("$fin", finishedMs)), ct);
    }
}
