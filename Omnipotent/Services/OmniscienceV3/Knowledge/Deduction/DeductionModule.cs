using System.Globalization;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Knowledge.Api;
using Omnipotent.Services.OmniscienceV3.Knowledge.Entities;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.ReadModels;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Deduction
{
    /// <summary>
    /// <c>knowledge.deduction</c>: the deduction engine (plan "Knowledge &amp; deduction"). Incremental on the corpus change log:
    /// a new, edited or deleted message dirties its author, its conversation and (for edits/deletes) the message itself; the
    /// planner turns those into single-flight jobs for tracked people (tier setting, default "tracked"). One consistent
    /// pipeline whether planned or run manually (F262): extract → consolidate (+ aliases, entities) → detective → hypothesis
    /// watch, with the timeline and plans projections queued after every change. A sweep each minute continues backlogs (fair
    /// rotation: at most a few slices per job, F184), picks up people newly promoted to tracked (F179/F265) and enforces
    /// MinorGuard on stored facts. Calibration refits daily from Klives's decisions.
    /// </summary>
    public sealed class DeductionModule : IntelModuleBase
    {
        public const string Id = KnowledgeSchema.DeductionModule;
        public const int ModuleVersion = 1;
        public const string ExtractKind = "extract";
        public const string ConsolidateKind = "consolidate";
        public const string DetectiveKind = "detective";
        public const string HypothesesKind = "hypotheses";
        public const string ReverifyKind = "reverify";
        public const string CalibrateKind = "calibrate";
        public const string SweepKind = "sweep";

        public const string TiersSetting = Id + ".tiers";
        public const string MaxOutputSetting = Id + ".max_output_tokens";
        public const string SlicesPerJobSetting = Id + ".max_slices_per_job";
        public const string CriticThresholdSetting = Id + ".critic_threshold";
        public const string AcceptThresholdSetting = Id + ".accept_threshold";
        public const string EducationSetting = Id + ".default_education_system";
        public const string DetectiveIntervalSetting = Id + ".detective_min_interval_hours";

        private KnowledgeKit kit = null!;
        private ClaimStore claims = null!;
        private ReviewQueue review = null!;

        public override ModuleManifest Manifest { get; } = new()
        {
            Id = Id,
            Version = ModuleVersion,
            Title = "Deduction",
            Category = "knowledge",
            Description = "Extracts verified, bitemporal facts about tracked people from whole conversations, consolidates them with calibrated confidence, and derives labelled inferences and hypotheses.",
            DefaultLane = Lane.LlmBackground,
            MinorRestricted = true,
            SubscribedChangeKinds = new[]
            {
                ChangeKinds.MessageInsert, ChangeKinds.MessageVersion, ChangeKinds.MessageDelete, ChangeKinds.PersonMerge, ChangeKinds.PersonUnmerge, ChangeKinds.EvidenceRemoved,
            },
            Schedules = new[]
            {
                new ScheduleTrigger("sweep", Every: TimeSpan.FromMinutes(1)),
                new ScheduleTrigger("calibrate", DailyAtLocal: new TimeOnly(4, 30)),
            },
            Datasets = new[] { KnowledgeSchema.AssertionsDataset, KnowledgeSchema.ClaimsDataset, KnowledgeSchema.ReviewDataset, KnowledgeSchema.HypothesesDataset, KnowledgeSchema.AliasesDataset },
            Settings = new[]
            {
                new SettingDescriptor(TiersSetting, SettingTypes.Json, "[\"tracked\"]", "Tiers whose people are extracted continuously (tracked, watch)."),
                new SettingDescriptor(MaxOutputSetting, SettingTypes.Int, "12000", "Output tokens reserved per extraction pass (the prompt fills the rest of the model's window).", 1_000, 64_000),
                new SettingDescriptor(SlicesPerJobSetting, SettingTypes.Int, "4", "Slices one job processes before yielding to other people (fair rotation).", 1, 50),
                new SettingDescriptor(CriticThresholdSetting, SettingTypes.Number, "0.4", "Claims at or above this probability go through the critic.", 0, 1),
                new SettingDescriptor(AcceptThresholdSetting, SettingTypes.Number, "0.55", "Calibrated probability at which a fact is shown as accepted.", 0, 1),
                new SettingDescriptor(EducationSetting, SettingTypes.String, "\"england_wales\"", "School system assumed for school-year statements when the evidence does not say (england_wales, northern_ireland, scotland, united_states, unknown)."),
                new SettingDescriptor(DetectiveIntervalSetting, SettingTypes.Number, "6", "Minimum hours between detective passes for one person.", 0, 720),
            },
            ReadModels = new[]
            {
                new ReadModelDescriptor(ProfileReadModel, "Knowledge profile", "omniscience.persons.read", 1, 300_000,
                    "Current facts by category with validity, calibrated confidence at as-of, inferences (labelled) and extraction coverage."),
            },
        };

        public const string ProfileReadModel = "knowledge.profile";

        public override IReadOnlyList<Storage.Sqlite.Migration> IntelMigrations => KnowledgeSchema.Deduction;

        public override async Task InitializeAsync(ModuleContext context, CancellationToken ct)
        {
            await base.InitializeAsync(context, ct).ConfigureAwait(false);
            kit = KnowledgeKit.For(context);
            claims = new ClaimStore(context.Stores, context.Clock);
            review = new ReviewQueue(context.Stores, context.Clock);
        }

        internal KnowledgeKit Kit => kit;

        // ── change mapping and planning ──────────────────────────────────────────

        public override IEnumerable<DirtySubject> MapChanges(IReadOnlyList<ChangeRecord> changes)
        {
            foreach (var c in changes)
            {
                switch (c.Kind)
                {
                    case ChangeKinds.MessageInsert:
                        if (c.PersonId != null) yield return new DirtySubject("person", c.PersonId);
                        if (c.ConversationId != null) yield return new DirtySubject("conversation", c.ConversationId);
                        break;
                    case ChangeKinds.MessageVersion or ChangeKinds.MessageDelete:
                        yield return new DirtySubject("message", c.EntityId);
                        if (c.PersonId != null) yield return new DirtySubject("person", c.PersonId);
                        break;
                    case ChangeKinds.PersonMerge or ChangeKinds.PersonUnmerge:
                        if (c.PersonId != null) yield return new DirtySubject("merge", c.PersonId);
                        yield return new DirtySubject("merge", c.EntityId);
                        break;
                    // A forgotten person's messages took claims with them (N20): the facts they supported are consolidated again.
                    case ChangeKinds.EvidenceRemoved:
                        yield return new DirtySubject("consolidate", c.EntityId);
                        break;
                }
            }
        }

        private IReadOnlyList<string> Tiers()
        {
            var tiers = Context.Settings.Get<List<string>>(TiersSetting, new List<string> { "tracked" });
            return tiers.Count == 0 ? new[] { "tracked" } : tiers;
        }

        public override IEnumerable<JobRequest> Plan(PlanContext context)
        {
            var jobs = new List<JobRequest>();
            var tracked = kit.Corpus.PersonsInTiers(Tiers()).ToHashSet(StringComparer.Ordinal);
            HashSet<string>? trackedIdentities = null;
            foreach (var d in context.Dirty)
            {
                switch (d.Kind)
                {
                    case "person":
                    {
                        string canonical = kit.Corpus.Canonical(d.Id);
                        // Nothing is extracted about someone whose analysis is switched off (N20).
                        if (tracked.Contains(canonical) && !kit.Corpus.AnalysisOff.Person(canonical)) jobs.Add(Job(ExtractKind, "person", canonical, 100));
                        break;
                    }
                    case "conversation":
                    {
                        trackedIdentities ??= kit.Corpus.IdentitiesOf(tracked.SelectMany(kit.Corpus.PersonIdsOf).ToList()).Select(i => i.IdentityId).ToHashSet(StringComparer.Ordinal);
                        if (trackedIdentities.Count == 0) break;
                        foreach (var identity in kit.Corpus.MembersOf(d.Id).Where(trackedIdentities.Contains))
                            if (kit.Corpus.PersonOfIdentity(identity) is { } p && tracked.Contains(p)) jobs.Add(Job(ExtractKind, "person", p, 90));
                        break;
                    }
                    case "message":
                        jobs.Add(Job(ReverifyKind, "message", d.Id, 150));
                        break;
                    case "consolidate":
                        jobs.Add(Job(ConsolidateKind, "person", kit.Corpus.Canonical(d.Id), 160));
                        break;
                    case "merge":
                    {
                        string canonical = kit.Corpus.Canonical(d.Id);
                        jobs.Add(Job(ConsolidateKind, "person", canonical, 160));
                        if (tracked.Contains(canonical)) jobs.Add(Job(ExtractKind, "person", canonical, 120));
                        // The merged-away id (unmerged again, or the survivor) is consolidated too, so nothing stays orphaned (F264).
                        if (d.Id != canonical) jobs.Add(Job(ConsolidateKind, "person", d.Id, 160));
                        break;
                    }
                }
            }
            foreach (var (name, _) in context.DueSchedules)
            {
                if (name == "sweep") jobs.Add(Job(SweepKind, lane: Lane.Cpu, priority: 50));
                if (name == "calibrate") jobs.Add(Job(CalibrateKind, lane: Lane.Cpu, priority: 40));
            }
            if (context.VersionChanged)
                foreach (var p in tracked) jobs.Add(Job(ExtractKind, "person", p, 80));
            return jobs.GroupBy(j => j.DedupeKey).Select(g => g.OrderByDescending(j => j.Priority).First()).ToList();
        }

        // ── execution ────────────────────────────────────────────────────────────

        public override async Task<string?> ExecuteAsync(JobContext job) => job.Job.Kind switch
        {
            ExtractKind => await ExtractAsync(job).ConfigureAwait(false),
            ConsolidateKind => await ConsolidateJobAsync(job).ConfigureAwait(false),
            DetectiveKind => await DetectiveAsync(job).ConfigureAwait(false),
            HypothesesKind => await HypothesesAsync(job).ConfigureAwait(false),
            ReverifyKind => await ReverifyAsync(job).ConfigureAwait(false),
            CalibrateKind => await CalibrateAsync(job).ConfigureAwait(false),
            SweepKind => await SweepAsync(job).ConfigureAwait(false),
            _ => throw new JobFailureException(ErrorClass.Permanent, $"Unknown job kind '{job.Job.Kind}'."),
        };

        private ExtractionOptions ExtractionSettings() => new()
        {
            MaxOutputTokens = Context.Settings.Get(MaxOutputSetting, 12_000),
            MaxSlicesPerJob = Context.Settings.Get(SlicesPerJobSetting, 4),
            CriticThreshold = Context.Settings.Get(CriticThresholdSetting, 0.4),
            DefaultEducationSystem = AgeEvidenceBuilder.ParseSystem(Context.Settings.Get(EducationSetting, "england_wales")) ?? EducationSystem.Unknown,
        };

        private Consolidator NewConsolidator() => new(kit, claims, review, new ConsolidationOptions { AcceptThreshold = Context.Settings.Get(AcceptThresholdSetting, 0.55) }, ModuleVersion);

        private string Subject(JobContext job) => job.Job.SubjectId ?? throw new JobFailureException(ErrorClass.Permanent, $"{job.Job.Kind} job without a subject.");

        private async Task<string?> ExtractAsync(JobContext job)
        {
            string person = kit.Corpus.Canonical(Subject(job));
            if (!kit.Corpus.PersonExists(person)) return Result(new { person, outcome = "unknown_person" });
            // The Quality Lab's regression gate: a prompt version it blocked never extracts (N18).
            if (Quality.QualityModule.LatestGate(Context.Stores, "deduction.extract", ExtractionPrompt.Version) is { Decision: "block" } gate)
                throw new JobFailureException(ErrorClass.Permanent, $"Prompt {ExtractionPrompt.Version} is blocked by the Quality Lab gate: {string.Join(" ", gate.Reasons)}");
            var pipeline = new ExtractionPipeline(kit, claims, ExtractionSettings());
            var stats = await pipeline.RunAsync(person, job).ConfigureAwait(false);
            long seq = kit.Corpus.MaxSeq();
            await SetStateAsync(person, tracked: true, pending: stats.Pending, extracted: true, error: null, ct: job.Cancellation).ConfigureAwait(false);
            if (stats.Slices > 0 || stats.TouchedSubjects.Count > 0)
            {
                await ConsolidateAndProjectAsync(person, job, seq).ConfigureAwait(false);
                foreach (var other in stats.TouchedSubjects.Where(s => s != person))
                    await Context.Jobs.EnqueueAsync(Job(ConsolidateKind, "person", other, 120), job.Cancellation).ConfigureAwait(false);
            }
            return Result(new
            {
                person, stats.RunId, stats.Slices, stats.Messages, stats.ClaimsRaw, stats.ClaimsAgreed, stats.ClaimsVerified, stats.ClaimsRejected, stats.Refusals,
                stats.Pending, stats.Outcome, stats.LlmCalls,
            });
        }

        private async Task<string?> ConsolidateJobAsync(JobContext job)
        {
            string person = kit.Corpus.Canonical(Subject(job));
            var result = await ConsolidateAndProjectAsync(person, job, kit.Corpus.MaxSeq()).ConfigureAwait(false);
            return Result(new { person, result.Claims, result.Drafts, result.Reconcile.Inserted, result.Reconcile.Superseded, result.Reconcile.Retracted, blocked = result.BlockedAttributes });
        }

        /// <summary>The one pipeline after any change: consolidate, aliases, entities, the profile read model, then queue the
        /// detective (debounced) and the timeline/plans projections.</summary>
        internal async Task<ConsolidationResult> ConsolidateAndProjectAsync(string person, JobContext job, long seq)
        {
            var ct = job.Cancellation;
            var result = await NewConsolidator().ConsolidateAsync(person, job.Job.JobId, seq, ct).ConfigureAwait(false);
            var verified = claims.LoadVerified(kit.Corpus.PersonIdsOf(person));
            await new AliasEngine(kit, review).RebuildAsync(person, verified, seq, ct).ConfigureAwait(false);
            await new EntityResolver(kit).RebuildAsync(person, verified, seq, ct).ConfigureAwait(false);
            await SetStateAsync(person, tracked: null, pending: null, consolidated: true, error: null, ct: ct).ConfigureAwait(false);
            await WriteProfileAsync(person, seq, ct).ConfigureAwait(false);
            await FlagAgeConflictsAsync(person, seq, ct).ConfigureAwait(false);
            if (result.Reconcile.Changed)
            {
                await Context.Jobs.EnqueueAsync(new JobRequest
                {
                    ModuleId = KnowledgeSchema.TimelineModule, Kind = "project", SubjectKind = "person", SubjectId = person, Lane = Lane.Cpu, Priority = 90,
                    DedupeKey = JobRequest.KeyFor(KnowledgeSchema.TimelineModule, "project", "person", person),
                }, ct).ConfigureAwait(false);
                await Context.Jobs.EnqueueAsync(new JobRequest
                {
                    ModuleId = KnowledgeSchema.PlansModule, Kind = "project", SubjectKind = "person", SubjectId = person, Lane = Lane.Cpu, Priority = 90,
                    DedupeKey = JobRequest.KeyFor(KnowledgeSchema.PlansModule, "project", "person", person),
                }, ct).ConfigureAwait(false);
                if (DetectiveDue(person))
                    await Context.Jobs.EnqueueAsync(Job(DetectiveKind, "person", person, 60), ct).ConfigureAwait(false);
            }
            await Context.Jobs.EnqueueAsync(Job(HypothesesKind, "person", person, 55), ct).ConfigureAwait(false);
            await Context.Events.PublishAsync("person.knowledge.updated", new { person, inserted = result.Reconcile.Inserted, superseded = result.Reconcile.Superseded,
                retracted = result.Reconcile.Retracted }, ct).ConfigureAwait(false);
            return result;
        }

        private bool DetectiveDue(string person)
        {
            double hours = Context.Settings.Get(DetectiveIntervalSetting, 6.0);
            using var c = Context.Stores.Intel.OpenRead();
            long? last = c.Scalar<long?>(null, "SELECT last_detective_ms FROM kn_person_state WHERE person_id=$p", ("$p", person));
            return last == null || Context.Clock.NowMs - last.Value >= (long)(hours * 3_600_000);
        }

        private async Task<string?> DetectiveAsync(JobContext job)
        {
            string person = kit.Corpus.Canonical(Subject(job));
            var result = await new Detective(kit, ModuleVersion).RunAsync(person, job, kit.Corpus.MaxSeq()).ConfigureAwait(false);
            await SetStateAsync(person, tracked: null, pending: null, detective: true, error: null, ct: job.Cancellation).ConfigureAwait(false);
            await WriteProfileAsync(person, kit.Corpus.MaxSeq(), job.Cancellation).ConfigureAwait(false);
            return Result(result);
        }

        private async Task<string?> HypothesesAsync(JobContext job)
        {
            string person = kit.Corpus.Canonical(Subject(job));
            int resolved = await new Detective(kit, ModuleVersion).WatchAsync(person, job, review, kit.Corpus.MaxSeq()).ConfigureAwait(false);
            return Result(new { person, resolved });
        }

        /// <summary>
        /// An edited or deleted message (plan T5, F205): every claim citing it is superseded and its coverage invalidated, so the
        /// next extraction re-reads the new version in context; the affected people are re-consolidated now (a deletion with no
        /// replacement lowers confidence through the deleted-evidence weight; facts left without evidence are retracted).
        /// </summary>
        private async Task<string?> ReverifyAsync(JobContext job)
        {
            string messageId = Subject(job);
            var message = kit.Corpus.Message(messageId);
            var citing = claims.CitingMessage(messageId);
            var affected = citing.Select(c => kit.Corpus.Canonical(c.PersonId)).Distinct(StringComparer.Ordinal).ToList();
            if (message == null)
            {
                // Erased from the corpus (forget/erase): the claims go, and so do the facts that rested only on them.
                await claims.SupersedeAsync(citing.Select(c => c.ClaimId).ToList(), messageId, job.Cancellation).ConfigureAwait(false);
            }
            else if (message.DeletedAtMs != null)
            {
                // Deleted by its author: the claims stand (labelled, lower weight), only the evidence role changes at consolidation.
                await RelabelDeletedAsync(messageId, job.Cancellation).ConfigureAwait(false);
            }
            else
            {
                var stale = citing.Where(c => ClaimVersion(c.ClaimId, messageId) < message.Version).Select(c => c.ClaimId).ToList();
                if (stale.Count > 0) await claims.SupersedeAsync(stale, messageId, job.Cancellation).ConfigureAwait(false);
            }
            if (kit.Context.Policy is EvidencePolicyOracle oracle) foreach (var p in affected) oracle.Invalidate(p);
            var tracked = kit.Corpus.PersonsInTiers(Tiers()).ToHashSet(StringComparer.Ordinal);
            long seq = kit.Corpus.MaxSeq();
            foreach (var p in affected)
            {
                await ConsolidateAndProjectAsync(p, job, seq).ConfigureAwait(false);
                if (tracked.Contains(p)) await Context.Jobs.EnqueueAsync(Job(ExtractKind, "person", p, 140), job.Cancellation).ConfigureAwait(false);
            }
            return Result(new { messageId, claims = citing.Count, people = affected.Count, deleted = message?.DeletedAtMs != null, erased = message == null });
        }

        private int ClaimVersion(string claimId, string messageId)
        {
            using var c = Context.Stores.Intel.OpenRead();
            string? json = c.Scalar<string>(null, "SELECT evidence_json FROM kn_claims WHERE claim_id=$c", ("$c", claimId));
            if (json == null) return int.MaxValue;
            var ev = JsonSerializer.Deserialize<List<ClaimEvidence>>(json, KernelJson.Options) ?? new();
            return ev.Where(e => e.MessageId == messageId).Select(e => e.Version).DefaultIfEmpty(int.MaxValue).Min();
        }

        private Task RelabelDeletedAsync(string messageId, CancellationToken ct) =>
            Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                var rows = c.Query(tx, "SELECT k.claim_id, k.evidence_json FROM kn_claim_messages m JOIN kn_claims k ON k.claim_id=m.claim_id WHERE m.message_id=$m AND k.verdict='verified'",
                    r => (r.GetString(0), r.GetString(1)), ("$m", messageId));
                foreach (var (id, json) in rows)
                {
                    var ev = JsonSerializer.Deserialize<List<ClaimEvidence>>(json, KernelJson.Options) ?? new();
                    var updated = ev.Select(e => e.MessageId == messageId ? e with { Deleted = true } : e).ToList();
                    c.Exec(tx, "UPDATE kn_claims SET evidence_json=$e WHERE claim_id=$c", ("$e", JsonSerializer.Serialize(updated, KernelJson.Options)), ("$c", id));
                }
            }, ct);

        /// <summary>Daily: refit calibration from Klives's decisions (per category, and global). Gold used as few-shot examples is
        /// never part of calibration (none is used today: the extractor runs zero-shot).</summary>
        private async Task<string?> CalibrateAsync(JobContext job)
        {
            var labels = kit.Gold.Active(GoldKinds.Assertion);
            var samples = new List<(string Category, double P, bool Outcome)>();
            using (var c = Context.Stores.Intel.OpenRead())
            {
                foreach (var (key, label) in labels)
                {
                    if (label.Verdict is not (GoldVerdicts.Confirm or GoldVerdicts.Reject)) continue;
                    // The probability Omniscience held when Klives decided (as-known-at the decision): calibration measures what was shown.
                    var row = c.Query(null, """
                        SELECT d.category, a.probability FROM assertions a JOIN kn_assertion_dims d ON d.assertion_id=a.assertion_id
                        WHERE a.natural_key=$k AND a.recorded_at_ms <= $t AND a.status <> 'confirmed' AND a.status <> 'rejected'
                        ORDER BY a.recorded_at_ms DESC LIMIT 1
                        """, r => (Category: r.GetString(0), P: r.GetDoubleOrNull(1)), ("$k", GoldLabelStore.Recanonicalize(key, kit.Corpus.Canonical)), ("$t", label.DecidedAtMs)).FirstOrDefault();
                    if (row.Category == null || row.P == null) continue;
                    samples.Add((row.Category, row.P.Value, label.Verdict == GoldVerdicts.Confirm));
                }
            }
            int fitted = 0;
            foreach (var g in samples.GroupBy(s => s.Category))
            {
                if (g.Count() < Isotonic.MinimumGold) continue;
                await kit.Calibration.FitAsync(CalibrationStore.CategoryScope(g.Key), g.Select(s => (s.P, s.Outcome)).ToList(), job.Cancellation).ConfigureAwait(false);
                fitted++;
            }
            if (samples.Count >= Isotonic.MinimumGold)
            {
                await kit.Calibration.FitAsync(CalibrationStore.GlobalScope, samples.Select(s => (s.P, s.Outcome)).ToList(), job.Cancellation).ConfigureAwait(false);
                fitted++;
            }
            return Result(new { samples = samples.Count, curves = fitted });
        }

        /// <summary>Every minute: continue backlogs, start newly tracked people, and enforce MinorGuard on already-stored facts.</summary>
        private async Task<string?> SweepAsync(JobContext job)
        {
            var ct = job.Cancellation;
            var tracked = kit.Corpus.PersonsInTiers(Tiers());
            Dictionary<string, (bool Tracked, bool Pending, long? LastExtract)> state;
            using (var c = Context.Stores.Intel.OpenRead())
                state = c.Query(null, "SELECT person_id, tracked, pending, last_extract_ms FROM kn_person_state", r => (r.GetString(0), r.GetInt32(1) == 1, r.GetInt32(2) == 1, r.GetInt64OrNull(3)))
                    .ToDictionary(x => x.Item1, x => (x.Item2, x.Item3, x.Item4), StringComparer.Ordinal);
            int enqueued = 0;
            foreach (var p in tracked)
            {
                if (!state.TryGetValue(p, out var s) || s.Pending || s.LastExtract == null)
                {
                    await Context.Jobs.EnqueueAsync(Job(ExtractKind, "person", p, s.LastExtract == null ? 110 : 70), ct).ConfigureAwait(false);
                    enqueued++;
                }
            }
            // People whose tier dropped stop being planned; their stored facts stay (they are history, not tracking).
            foreach (var (p, s) in state.Where(kv => kv.Value.Tracked && !tracked.Contains(kv.Key)))
                await SetStateAsync(p, tracked: false, pending: false, error: null, ct: ct).ConfigureAwait(false);

            // MinorGuard on what is already stored: anyone now in a restricted band with restricted facts gets a purge.
            var restricted = Attributes.All.Where(a => a.Sensitivity == AttributeSensitivity.RestrictedForMinors).Select(a => a.Id).ToList();
            List<string> subjects;
            using (var c = Context.Stores.Intel.OpenRead())
                subjects = c.QueryIn(null, "SELECT DISTINCT subject_id FROM kn_assertion_dims WHERE live=1 AND attribute IN ({keys})", restricted, r => r.GetString(0));
            long now = Context.Clock.NowMs;
            int purges = 0;
            foreach (var s in subjects.Select(kit.Corpus.Canonical).Distinct(StringComparer.Ordinal))
            {
                if (!MinorGuard.IsRestricted(Context.Policy.BandOf(s, now))) continue;
                await Context.Jobs.EnqueueAsync(Job(ConsolidateKind, "person", s, 200), ct).ConfigureAwait(false);
                purges++;
            }
            return Result(new { tracked = tracked.Count, enqueued, purges });
        }

        private async Task FlagAgeConflictsAsync(string person, long seq, CancellationToken ct)
        {
            if (Context.Policy is not EvidencePolicyOracle oracle) return;
            var rows = oracle.EvidenceFor(person);
            if (rows.Count == 0) return;
            var (_, consistent, inconsistent) = AgeBands.Combine(rows);
            await claims.UpdateAgeStatusAsync(inconsistent.Select(r => r.EvidenceId).ToList(), AgeEvidenceStatus.Inconsistent, ct).ConfigureAwait(false);
            await claims.UpdateAgeStatusAsync(consistent.Where(r => r.Status == AgeEvidenceStatus.Inconsistent).Select(r => r.EvidenceId).ToList(), AgeEvidenceStatus.Active, ct).ConfigureAwait(false);
            var band = oracle.BandOf(person, Context.Clock.NowMs);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (inconsistent.Count > 0)
            {
                string key = NaturalKeys.Canonical(("s", "person:" + person), ("k", "age_conflict"));
                keys.Add(key);
                await review.RaiseAsync(ReviewKinds.AgeConflict, person, key, "Age statements that cannot all be true",
                    new { band = band.ToString(), inconsistent = inconsistent.Select(r => new { r.EvidenceId, r.Kind, r.MessageId, r.AssertedAtMs }) }, 0.9, seq, ct).ConfigureAwait(false);
            }
            if (MinorGuard.IsRestricted(band))
            {
                // Klives sees which statements restrict the person, so a misread can be rejected (which recomputes the band).
                foreach (var r in consistent.Concat(inconsistent))
                {
                    keys.Add(r.NaturalKey);
                    await review.RaiseAsync(ReviewKinds.AgeEvidence, person, r.NaturalKey, $"Age statement restricting this person ({band})",
                        new { r.EvidenceId, r.Kind, r.MessageId, r.AssertedAtMs, band = band.ToString() }, 0.95, seq, ct).ConfigureAwait(false);
                }
            }
            await review.AutoResolveAsync(ReviewKinds.AgeEvidence, person, keys, "No longer restricting.", ct).ConfigureAwait(false);
            await review.AutoResolveAsync(ReviewKinds.AgeConflict, person, keys, "Age statements are consistent again.", ct).ConfigureAwait(false);
        }

        private Task SetStateAsync(string person, bool? tracked, bool? pending, bool extracted = false, bool consolidated = false, bool detective = false, string? error = null,
            CancellationToken ct = default)
        {
            long now = Context.Clock.NowMs;
            return Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "INSERT OR IGNORE INTO kn_person_state(person_id, tracked, pending, updated_at_ms) VALUES($p,$t,0,$n)", ("$p", person), ("$t", tracked == true ? 1 : 0), ("$n", now));
                c.Exec(tx, """
                    UPDATE kn_person_state SET
                        tracked = COALESCE($t, tracked), pending = COALESCE($pe, pending),
                        last_extract_ms = CASE WHEN $x = 1 THEN $n ELSE last_extract_ms END,
                        last_consolidate_ms = CASE WHEN $c = 1 THEN $n ELSE last_consolidate_ms END,
                        last_detective_ms = CASE WHEN $d = 1 THEN $n ELSE last_detective_ms END,
                        covered_messages = (SELECT COUNT(*) FROM kn_coverage WHERE person_id=$p),
                        prompt_version = $pv, last_error = $e, updated_at_ms = $n
                    WHERE person_id=$p
                    """, ("$t", tracked == null ? null : tracked.Value ? 1 : 0), ("$pe", pending == null ? null : pending.Value ? 1 : 0), ("$x", extracted ? 1 : 0),
                    ("$c", consolidated ? 1 : 0), ("$d", detective ? 1 : 0), ("$pv", ExtractionPrompt.Version), ("$e", error), ("$n", now), ("$p", person));
            }, ct);
        }

        /// <summary>The per-person profile read model: one row a dossier reads (plan "read models are the only thing routes read").</summary>
        internal async Task WriteProfileAsync(string person, long seq, CancellationToken ct)
        {
            long now = Context.Clock.NowMs;
            var ids = kit.Corpus.PersonIdsOf(person);
            var band = Context.Policy.BandOf(person, now);
            var facts = kit.Assertions.Query(new AssertionQuery { SubjectIds = ids, AtMs = now, Limit = 3_000 });
            (int Covered, long? LastExtract, bool Pending) coverage;
            using (var c = Context.Stores.Intel.OpenRead())
                coverage = c.Query(null, "SELECT covered_messages, last_extract_ms, pending FROM kn_person_state WHERE person_id=$p",
                    r => (r.GetInt32(0), r.GetInt64OrNull(1), r.GetInt32(2) == 1), ("$p", person)).FirstOrDefault();
            var body = new KnowledgeProfileDto(
                person,
                now,
                MinorGuard.IsRestricted(band),
                facts.Where(f => f.Record.Status != AssertionStatus.Inferred && !Attributes.IsBlockedFor(f.Record.Attribute, band))
                    .GroupBy(f => f.Definition.Category, StringComparer.Ordinal)
                    .Select(g => new KnowledgeCategoryDto(g.Key, g.Select(ToFactDto).ToList())).OrderBy(c => c.Category, StringComparer.Ordinal).ToList(),
                MinorGuard.Allows(RestrictedCapability.RoutineInference, band)
                    ? facts.Where(f => f.Record.Status == AssertionStatus.Inferred).Select(ToFactDto).ToList()
                    : new List<KnowledgeFactDto>(),
                coverage.Covered, coverage.LastExtract, coverage.Pending, ExtractionPrompt.Version);
            await Context.ReadModels.PutAsync(ProfileReadModel, person, 1, now, seq, body, ct).ConfigureAwait(false);
        }

        internal static KnowledgeFactDto ToFactDto(AssertionView f) => new(
            f.Record.AssertionId, f.Record.Attribute, f.Definition.Title, f.Definition.Category, f.Record.ValueText ?? f.Record.Value.Text, f.Record.Value.Polarity,
            f.Record.ObjectId, f.Record.Status, Math.Round(f.EffectiveProbability, 4), f.Record.Probability ?? 0, f.Record.Calibrated,
            f.Record.ValidFromEarliestMs, f.Record.ValidFromLatestMs, f.Record.ValidToEarliestMs, f.Record.ValidToLatestMs, f.Record.AssertedAtMs, f.Record.FirstEvidenceAtMs,
            f.Record.RecordedAtMs, f.Record.SourceKind, f.Record.Sensitivity, f.Record.Producer);

        private static string Result(object value) => JsonSerializer.Serialize(value, KernelJson.Options);

        /// <summary>Starts the one consistent pipeline for a person now (manual run, F262/F075): returns the job id.</summary>
        public Task<long> RunNowAsync(string personId, CancellationToken ct) =>
            Context.Jobs.EnqueueAsync(Job(ExtractKind, "person", kit.Corpus.Canonical(personId), 300), ct);

        public Task<long> ConsolidateNowAsync(string personId, CancellationToken ct) =>
            Context.Jobs.EnqueueAsync(Job(ConsolidateKind, "person", kit.Corpus.Canonical(personId), 300), ct);
    }
}
