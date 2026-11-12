using Omnipotent.Services.OmniscienceV3.Storage;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Llm;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    /// <summary>A training request refused before any work starts, with the reason and (when known) the readiness numbers (F013).</summary>
    public sealed class ReplicaRefusedException : Exception
    {
        public ReplicaRefusedException(string reason, ReplicaReadinessDto? readiness = null, bool notFound = false) : base(reason)
        {
            Readiness = readiness;
            NotFound = notFound;
        }

        public ReplicaReadinessDto? Readiness { get; }
        public bool NotFound { get; }
    }

    /// <summary>How a stage ended.</summary>
    internal sealed record StageResult(string State, string? Detail, string? Error = null, bool Block = false)
    {
        public static StageResult Done(string detail) => new(StageStates.Done, detail);
        public static StageResult Degraded(string detail, string? error = null) => new(StageStates.Degraded, detail, error ?? detail);
        public static StageResult Skipped(string detail) => new(StageStates.Skipped, detail);
        public static StageResult Fail(string error) => new(StageStates.Failed, null, error);
        public static StageResult Blocked(string error) => new(StageStates.Failed, null, error, Block: true);
        /// <summary>The stage goes on in other jobs (persona slices); whoever finishes last completes it.</summary>
        public static StageResult Continues(string detail) => new(StageStates.Running, detail);
    }

    /// <summary>
    /// Versioned replica training (plan "Replica", F011–F016, F266–F286). A build is a version row and a chain of durable
    /// leased jobs — index → (vectors ∥ reflexes ∥ persona slices) → evaluation → finalise — each retried with backoff,
    /// never fire-and-forget, so nothing can sit in "training" forever: a crashed worker's lease expires and the job is
    /// re-run (every stage is idempotent). Optional stages degrade instead of failing the build (F270); required ones
    /// fail it with a classified, visible reason; the serving version keeps serving throughout and is replaced only by
    /// the atomic swap at the end (F268/F269). Preconditions are checked before anything is queued and the caller gets
    /// the new version and its job id back (F013/F014).
    /// </summary>
    public sealed class ReplicaTrainer
    {
        public const string KindIndex = "train.index";
        public const string KindVectors = "train.vectors";
        public const string KindReflex = "train.reflex";
        public const string KindPersona = "train.persona";
        public const string KindEval = "train.eval";
        public const string KindFinalize = "train.finalize";
        public const string KindReadiness = "readiness";

        private readonly ReplicaServices s;
        private readonly ReplicaEvaluator evaluator;

        public ReplicaTrainer(ReplicaServices services)
        {
            s = services;
            evaluator = new ReplicaEvaluator(services);
        }

        private sealed record SlicePayload(int Slice);

        // ── requests ─────────────────────────────────────────────────────────────

        /// <summary>Readiness numbers from a counting pass over the person's history (the readiness job).</summary>
        public async Task<ReplicaReadinessDto> ComputeReadinessAsync(string personId, CancellationToken ct)
        {
            PersonScope person;
            IndexResult result;
            long? adultFrom;
            AgeBand band;
            long now = s.Clock.NowMs;
            using (var corpus = s.Stores.Corpus.OpenRead())
            {
                person = People.Resolve(corpus, personId) ?? throw new ReplicaRefusedException($"No person '{personId}' in the corpus.", notFound: true);
                band = s.Kernel.Policy.BandOf(person.CanonicalId, now);
                long first = FirstMessageMs(corpus, person) ?? now;
                var (restricted, af) = People.AdultFrom(s.Kernel.Policy, person.CanonicalId, first, now);
                adultFrom = af;
                var indexer = new ReplicaIndexer(corpus, (pid, at) => s.Zones.ZoneAt(pid, person.IsSelf, at), AnalysisOff.Load(s.Stores));
                result = restricted
                    ? new IndexResult(person, Array.Empty<ReplicaPair>(), new IndexStats(), ChangeLogSeq(corpus))
                    : indexer.Build(person, Options(adultFrom, now) with { KeepPairs = false });
            }
            ct.ThrowIfCancellationRequested();
            var readiness = Readiness.From(person, result.Stats, band, adultFrom, s.Settings.MinRepliesExperimental, s.Settings.MinRepliesReady, now, result.SourceSeq);
            await s.Versions.SaveReadinessAsync(person.CanonicalId, person.DisplayName, readiness, ct).ConfigureAwait(false);
            await ReplicaCards.RefreshAsync(s, person.CanonicalId, ct).ConfigureAwait(false);
            return readiness;
        }

        public async Task<long> RequestReadinessAsync(string personId, CancellationToken ct)
        {
            string canonical;
            using (var corpus = s.Stores.Corpus.OpenRead())
            {
                if (People.Resolve(corpus, personId) is not { } person) throw new ReplicaRefusedException($"No person '{personId}' in the corpus.", notFound: true);
                canonical = person.CanonicalId;
            }
            return await s.Kernel.Jobs.EnqueueAsync(new JobRequest
            {
                ModuleId = ReplicaSchema.ModuleId, Kind = KindReadiness, SubjectKind = "person", SubjectId = canonical,
                DedupeKey = JobRequest.KeyFor(ReplicaSchema.ModuleId, KindReadiness, "person", canonical), Lane = Lane.Cpu, Priority = 150, MaxAttempts = 3,
            }, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Starts a training build. Validates synchronously and throws <see cref="ReplicaRefusedException"/> with the reason
        /// (unknown person, a minor, a bot, no gateway, clearly too little data); otherwise returns the new (or already
        /// running) version and the job id that will build it, created before any work starts.
        /// </summary>
        public async Task<ReplicaTrainResultDto> RequestAsync(string personId, string reason, string actor, CancellationToken ct)
        {
            if (s.Llm == null) throw new ReplicaRefusedException("This engine has no LLM gateway; replicas cannot be trained.");
            PersonScope person;
            int authored;
            using (var corpus = s.Stores.Corpus.OpenRead())
            {
                person = People.Resolve(corpus, personId) ?? throw new ReplicaRefusedException($"No person '{personId}' in the corpus.", notFound: true);
                authored = AuthoredCount(corpus, person);
            }
            long now = s.Clock.NowMs;
            var band = s.Kernel.Policy.BandOf(person.CanonicalId, now);
            var cached = RJson.Read<ReplicaReadinessDto>(s.Versions.Person(person.CanonicalId)?.ReadinessJson);
            if (MinorGuard.IsRestricted(band))
            {
                await s.Versions.BlockAsync(person.CanonicalId, $"MinorGuard: {person.DisplayName} is, or may be, under 18.", ct).ConfigureAwait(false);
                await ReplicaCards.RefreshAsync(s, person.CanonicalId, ct).ConfigureAwait(false);
                throw new ReplicaRefusedException($"Not available: the evidence says {person.DisplayName} is, or may be, under 18 ({band}). Omniscience never builds a replica of a minor.", cached);
            }
            if (AnalysisOff.Load(s.Stores).Person(person.CanonicalId))
                throw new ReplicaRefusedException($"Not available: analysis is switched off for {person.DisplayName}. Switch it back on under Data controls first.", cached);
            if (cached is { IsBot: true }) throw new ReplicaRefusedException("Not possible: this is a bot account.", cached);
            if (authored < s.Settings.MinRepliesExperimental)
                throw new ReplicaRefusedException($"Not possible yet: {person.DisplayName} has written {authored} messages in the corpus; at least {s.Settings.MinRepliesExperimental} replies are needed.", cached);

            var (version, created) = await s.Versions.CreateAsync(person.CanonicalId, person.DisplayName, string.IsNullOrWhiteSpace(reason) ? "requested" : reason.Trim(), actor, s.ModuleVersion, ct).ConfigureAwait(false);
            long jobId = created
                ? await EnqueueStageAsync(version.VersionId, Stages.Index, ct: ct).ConfigureAwait(false)
                : s.Kernel.Jobs.Query(new JobQuery { ModuleId = ReplicaSchema.ModuleId, RunId = version.VersionId, Limit = 1 }).Items.FirstOrDefault()?.JobId ?? 0;
            if (created)
            {
                await OperatorAuditAsync(actor, "replica.train", person.CanonicalId, new { version.VersionId, version.Number, reason }, ct).ConfigureAwait(false);
                await PublishAsync(version, "queued", null, ct).ConfigureAwait(false);
            }
            await ReplicaCards.RefreshAsync(s, person.CanonicalId, ct).ConfigureAwait(false);
            return new ReplicaTrainResultDto(person.CanonicalId, version.VersionId, version.Number, jobId, !created, cached);
        }

        /// <summary>Cancels the person's running build; the serving version is untouched.</summary>
        public async Task<bool> CancelAsync(string personId, string actor, CancellationToken ct)
        {
            var person = s.Versions.Person(personId);
            if (person?.BuildingVersionId is not { } building) return false;
            bool ended = await s.Versions.EndAsync(building, VersionStates.Cancelled, $"cancelled by {actor}", ct).ConfigureAwait(false);
            await CancelJobsAsync(building, ct).ConfigureAwait(false);
            if (ended)
            {
                await OperatorAuditAsync(actor, "replica.train.cancel", personId, new { building }, ct).ConfigureAwait(false);
                if (s.Versions.Version(building) is { } v) await PublishAsync(v, "cancelled", null, ct).ConfigureAwait(false);
            }
            await ReplicaCards.RefreshAsync(s, personId, ct).ConfigureAwait(false);
            return ended;
        }

        /// <summary>Cancels a build's queued and running jobs (except the caller's own, which records its outcome itself).</summary>
        public async Task CancelJobsAsync(string versionId, CancellationToken ct, long? exceptJobId = null)
        {
            foreach (var state in new[] { JobStates.Queued, JobStates.Leased })
                foreach (var job in s.Kernel.Jobs.Query(new JobQuery { ModuleId = ReplicaSchema.ModuleId, RunId = versionId, State = state, Limit = 1000 }).Items)
                    if (job.JobId != exceptJobId) await s.Kernel.Jobs.CancelAsync(job.JobId, ct).ConfigureAwait(false);
        }

        // ── stage jobs ───────────────────────────────────────────────────────────

        public Task<string?> ExecuteAsync(JobContext job) => job.Job.Kind switch
        {
            KindReadiness => ReadinessJobAsync(job),
            KindIndex => RunStageAsync(job, Stages.Index, IndexAsync),
            KindVectors => RunStageAsync(job, Stages.Vectors, VectorsAsync),
            KindReflex => RunStageAsync(job, Stages.Reflex, ReflexAsync),
            KindPersona => PersonaJobAsync(job),
            KindEval => RunStageAsync(job, Stages.Eval, (v, j) => evaluator.RunAsync(v, j)),
            KindFinalize => FinalizeAsync(job),
            _ => throw ReplicaFailures.Permanent($"Unknown training job kind '{job.Job.Kind}'."),
        };

        private async Task<string?> ReadinessJobAsync(JobContext job)
        {
            try
            {
                var r = await ComputeReadinessAsync(job.Job.SubjectId!, job.Cancellation).ConfigureAwait(false);
                return RJson.Write(new { r.Tier, r.Replies, r.Initiations });
            }
            catch (ReplicaRefusedException ex) { throw ReplicaFailures.Permanent(ex.Message); }
        }

        private static string VersionOf(JobContext job) => (job.Job.SubjectId ?? throw ReplicaFailures.Permanent("Training job without a version.")).Split('#')[0];

        private static bool LastAttempt(JobContext job) => job.Job.Attempts >= job.Job.MaxAttempts;

        /// <summary>
        /// The shared stage protocol: no-op unless the version is still building; mark the stage running; run it; on success
        /// record it and start whatever may follow (in one transaction); on the final failure degrade an optional stage and
        /// carry on, or fail the build for a required one — then rethrow so the job is dead-lettered with its error (visible
        /// on the Jobs and Modules pages, F016). Transient failures before the last attempt are retried by the queue.
        /// </summary>
        private async Task<string?> RunStageAsync(JobContext job, string stage, Func<VersionRecord, JobContext, Task<StageResult>> work)
        {
            string versionId = VersionOf(job);
            var ct = job.Cancellation;
            var v = s.Versions.Version(versionId);
            if (v == null || v.State != VersionStates.Building) return null;
            if (StageStates.IsTerminal(v.Stage(stage).State)) { await ContinueAsync(versionId, ct).ConfigureAwait(false); return null; }
            var started = await s.Versions.UpdateStageAsync(versionId, stage, st => st with
            {
                State = StageStates.Running, Attempts = job.Job.Attempts, StartedAtMs = st.StartedAtMs ?? s.Clock.NowMs, Error = st.Error,
            }, ct).ConfigureAwait(false);
            if (started == null) return null;
            await PublishAsync(v, "stage", stage, ct).ConfigureAwait(false);

            StageResult result;
            try
            {
                result = await work(v, job).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (LastAttempt(job) || ex is JobFailureException { Class: ErrorClass.Permanent } || LaneWorkers.Classify(ex) == ErrorClass.Permanent)
            {
                string error = ex is JobFailureException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
                await FinishStageAsync(v, stage, Stages.Required(stage) ? StageResult.Fail(error) : StageResult.Degraded($"{stage} failed: {error}", error), ct, job.Job.JobId).ConfigureAwait(false);
                throw;
            }
            if (result.State == StageStates.Running) return RJson.Write(new { versionId, stage, result.State, result.Detail });
            await FinishStageAsync(v, stage, result, ct, job.Job.JobId).ConfigureAwait(false);
            return RJson.Write(new { versionId, stage, result.State, result.Detail });
        }

        private async Task FinishStageAsync(VersionRecord v, string stage, StageResult result, CancellationToken ct, long? currentJobId = null)
        {
            if (result.Block)
            {
                await BlockPersonAsync(v.PersonId, result.Error ?? "MinorGuard", ct).ConfigureAwait(false);
                return;
            }
            if (result.State == StageStates.Failed && Stages.Required(stage))
            {
                await s.Versions.UpdateStageAsync(v.VersionId, stage, st => st with { State = StageStates.Failed, Error = result.Error, FinishedAtMs = s.Clock.NowMs }, ct).ConfigureAwait(false);
                await s.Versions.EndAsync(v.VersionId, VersionStates.Failed, $"{stage}: {result.Error}", ct).ConfigureAwait(false);
                await CancelJobsAsync(v.VersionId, CancellationToken.None, currentJobId).ConfigureAwait(false);
                await PublishAsync(v, "failed", stage, ct).ConfigureAwait(false);
                await ReplicaCards.RefreshAsync(s, v.PersonId, ct).ConfigureAwait(false);
                return;
            }
            if (result.State is StageStates.Degraded or StageStates.Failed && result.Detail != null)
                await s.Versions.UpdateFieldsAsync(v.VersionId, addDegraded: result.Detail, ct: ct).ConfigureAwait(false);
            var current = s.Versions.Version(v.VersionId)?.Stage(stage) ?? new StageState(StageStates.Running);
            var next = await s.Versions.CompleteStageAsync(v.VersionId, stage, current with
            {
                State = result.State == StageStates.Failed ? StageStates.Degraded : result.State, Detail = result.Detail, Error = result.Error,
            }, ct).ConfigureAwait(false);
            foreach (var n in next) await EnqueueStageAsync(v.VersionId, n, ct: ct).ConfigureAwait(false);
            await PublishAsync(v, "stage-done", stage, ct).ConfigureAwait(false);
            await ReplicaCards.RefreshAsync(s, v.PersonId, ct).ConfigureAwait(false);
        }

        /// <summary>Re-hands out stages a crash may have left queued without a job (idempotent: dedupe keys).</summary>
        public async Task ContinueAsync(string versionId, CancellationToken ct)
        {
            var v = s.Versions.Version(versionId);
            if (v == null || v.State != VersionStates.Building) return;
            foreach (var stage in Stages.All.Where(st => v.Stage(st).State is StageStates.Queued or StageStates.Running))
                await EnqueueStageAsync(versionId, stage, ct: ct).ConfigureAwait(false);
        }

        public async Task<long> EnqueueStageAsync(string versionId, string stage, int slice = -1, CancellationToken ct = default)
        {
            string kind = stage switch
            {
                Stages.Index => KindIndex,
                Stages.Vectors => KindVectors,
                Stages.Reflex => KindReflex,
                Stages.Persona => KindPersona,
                Stages.Eval => KindEval,
                _ => KindFinalize,
            };
            var lane = stage is Stages.Reflex or Stages.Persona or Stages.Eval ? Lane.LlmBackground : Lane.Cpu;
            string subject = slice >= 0 ? versionId + "#" + slice.ToString(CultureInfo.InvariantCulture) : versionId;
            return await s.Kernel.Jobs.EnqueueAsync(new JobRequest
            {
                ModuleId = ReplicaSchema.ModuleId,
                Kind = kind,
                SubjectKind = "replica_version",
                SubjectId = subject,
                DedupeKey = JobRequest.KeyFor(ReplicaSchema.ModuleId, kind, "replica_version", subject),
                Lane = lane,
                Priority = stage == Stages.Finalize ? 160 : 120,
                MaxAttempts = stage switch { Stages.Index => 4, Stages.Vectors => 4, Stages.Finalize => 6, _ => 3 },
                PayloadJson = slice >= 0 ? RJson.Write(new SlicePayload(slice)) : null,
                RunId = versionId,
            }, ct).ConfigureAwait(false);
        }

        // ── index ────────────────────────────────────────────────────────────────

        private IndexOptions Options(long? adultFrom, long upTo) => new()
        {
            BurstGap = s.Settings.BurstGap,
            StimulusWindow = s.Settings.StimulusWindow,
            AdultFromMs = adultFrom,
            UpToMs = upTo,
            MaxMessages = s.Settings.MaxMessages,
        };

        private async Task<StageResult> IndexAsync(VersionRecord v, JobContext job)
        {
            var ct = job.Cancellation;
            long now = s.Clock.NowMs;
            PersonScope person;
            IndexResult result;
            long? adultFrom;
            using (var corpus = s.Stores.Corpus.OpenRead())
            {
                person = People.Resolve(corpus, v.PersonId) ?? throw ReplicaFailures.Permanent($"The person {v.PersonId} no longer exists in the corpus.");
                long first = FirstMessageMs(corpus, person) ?? now;
                var (restricted, af) = People.AdultFrom(s.Kernel.Policy, person.CanonicalId, first, now);
                if (restricted) return StageResult.Blocked($"MinorGuard: {person.DisplayName} is, or may be, under 18; no replica is built.");
                adultFrom = af;
                var indexer = new ReplicaIndexer(corpus, (pid, at) => s.Zones.ZoneAt(pid, person.IsSelf, at), AnalysisOff.Load(s.Stores));
                result = indexer.Build(person, Options(adultFrom, now));
            }
            ct.ThrowIfCancellationRequested();
            var band = s.Kernel.Policy.BandOf(person.CanonicalId, now);
            var readiness = Readiness.From(person, result.Stats, band, adultFrom, s.Settings.MinRepliesExperimental, s.Settings.MinRepliesReady, now, result.SourceSeq);
            await s.Versions.SaveReadinessAsync(v.PersonId, person.DisplayName, readiness, ct).ConfigureAwait(false);
            if (!Readiness.CanTrain(readiness)) return StageResult.Fail(readiness.Reason);

            var zoneAt = s.ZoneFunc(person.CanonicalId, person.IsSelf);
            var eras = EraDetector.Detect(result.Pairs, zoneAt);
            var pairs = result.Pairs.Select(p => p with { EraIndex = EraDetector.EraOf(eras, p.ReplyAtMs) }).ToList();
            var splits = HoldoutPlanner.Assign(pairs, new HoldoutPlanner.Options(s.Settings.HoldoutFraction, 20, s.Settings.HoldoutMax));
            pairs = pairs.Select(p => p with { Split = splits[p.PairId] }).ToList();
            var visible = pairs.Where(p => PairSplits.IsVisible(p.Split)).ToList();

            var model = await s.ModelAsync(ct).ConfigureAwait(false);
            int sliceBudget = Math.Max(4_000, ContextPacker.Budget(model, 6_000) - 3_000);
            var slices = SlicePlanner.Plan(visible, sliceBudget, s.Settings.PersonaSliceMax, s.Estimator, ReplicaPrompts.Persona);
            var sliceOf = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var sl in slices) foreach (var id in sl.PairIds) sliceOf[id] = sl.Index;
            pairs = pairs.Select(p => p with { SliceIndex = sliceOf.TryGetValue(p.PairId, out int si) ? si : -1 }).ToList();

            var background = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var p in visible)
                foreach (var t in TextFeatures.Tokens(p.StimulusPlain))
                    if (!t.StartsWith("<link:", StringComparison.Ordinal)) background[t] = background.GetValueOrDefault(t) + 1;
            var style = StyleFingerprint.Compute(visible, new StyleFingerprint.Inputs { Background = background, ZoneAt = zoneAt });
            var counts = new ReplicaCountsDto(pairs.Count, pairs.Count(p => !p.IsInitiation), pairs.Count(p => p.IsInitiation), visible.Count,
                pairs.Count(p => p.Split == PairSplits.Tune), pairs.Count(p => p.Split == PairSplits.Test), pairs.Count(p => p.Split == PairSplits.Shadow),
                eras.Count, slices.Count, 0, 0, 0, 0);

            await s.Stores.Intel.WriteAsync((c, tx) =>
            {
                foreach (var table in new[] { "replica_pairs", "replica_eras", "replica_slices", "replica_persona_items" })
                    c.Exec(tx, $"DELETE FROM {table} WHERE version_id=$v", ("$v", v.VersionId));
                PairStore.Insert(c, tx, v.VersionId, pairs);
                foreach (var e in eras)
                    c.Exec(tx, "INSERT INTO replica_eras(version_id, era_index, from_ms, to_ms, pairs, summary_json) VALUES($v,$i,$f,$t,$n,$s)",
                        ("$v", v.VersionId), ("$i", e.Index), ("$f", e.FromMs), ("$t", e.ToMs), ("$n", e.Pairs), ("$s", RJson.Write(e.Means)));
                foreach (var sl in slices)
                    c.Exec(tx, "INSERT INTO replica_slices(version_id, slice_index, from_ms, to_ms, pairs, content_hash, state) VALUES($v,$i,$f,$t,$n,$h,'pending')",
                        ("$v", v.VersionId), ("$i", sl.Index), ("$f", sl.FromMs), ("$t", sl.ToMs), ("$n", sl.PairIds.Count), ("$h", sl.ContentHash));
            }, ct).ConfigureAwait(false);
            await s.Versions.UpdateFieldsAsync(v.VersionId, sourceSeq: result.SourceSeq, dataFrom: result.Stats.FirstMs, dataTo: result.Stats.LastMs, adultFrom: adultFrom,
                windowTokens: model.ContextWindowTokens, countsJson: RJson.Write(counts), styleJson: RJson.Write(StyleDto(style)), ct: ct).ConfigureAwait(false);
            if (result.Stats.Truncated)
                await s.Versions.UpdateFieldsAsync(v.VersionId, addDegraded: $"Only the most recent {s.Settings.MaxMessages} messages were read (replica.max_messages).", ct: ct).ConfigureAwait(false);
            return StageResult.Done(string.Create(CultureInfo.InvariantCulture,
                $"{pairs.Count} pairs ({counts.Replies} replies, {counts.Initiations} initiations); {counts.HoldoutTune + counts.HoldoutTest} held out, {counts.Shadow} shadowed; {eras.Count} eras; {slices.Count} persona slices"));
        }

        internal static ReplicaStyleDto StyleDto(StyleFingerprint f) => new(f.Bursts, f.FromMs, f.ToMs, f.CharsP50, f.CharsP90, f.BurstShares, f.LatencyP50Ms, f.Casing, f.Endings,
            f.EmojiLines, f.TopEmoji, f.Laughter, f.Distinctive, f.Scripts, f.PeakLocalHours);

        private static long? FirstMessageMs(SqliteConnection corpus, PersonScope person)
        {
            long? first = null;
            foreach (var id in person.IdentityIds)
            {
                long? t = corpus.Scalar<long?>(null, "SELECT sent_at_ms FROM messages WHERE author_identity_id=$i ORDER BY sent_at_ms LIMIT 1", ("$i", id));
                if (t is long x && (first == null || x < first)) first = x;
            }
            return first;
        }

        private static int AuthoredCount(SqliteConnection corpus, PersonScope person) =>
            (int)Math.Min(int.MaxValue, person.IdentityIds.Sum(id => corpus.Scalar<long>(null, "SELECT COUNT(*) FROM messages WHERE author_identity_id=$i AND deleted_at_ms IS NULL", ("$i", id))));

        private static long ChangeLogSeq(SqliteConnection corpus) => Storage.ChangeLog.MaxSeq(corpus);

        // ── vectors ──────────────────────────────────────────────────────────────

        private async Task<StageResult> VectorsAsync(VersionRecord v, JobContext job)
        {
            var ct = job.Cancellation;
            var encoder = s.Encoder();
            if (encoder == null) return StageResult.Degraded("No shared text encoder is installed: retrieval is lexical only and the judge has no embedding signals.");
            string model = encoder.ModelId + "@" + encoder.ModelVersion;
            List<ReplicaPair> pairs;
            using (var c = s.Stores.Intel.OpenRead())
                pairs = c.Query(null, $"SELECT {PairStore.Columns} FROM replica_pairs WHERE version_id=$v ORDER BY reply_at_ms", PairStore.Map, ("$v", v.VersionId));
            var wanted = new List<(string Space, string Item, string Text)>();
            foreach (var p in pairs)
            {
                if (p.StimulusPlain.Length > 0) wanted.Add((VectorIds.StimulusSpace, VectorIds.Stimulus(p), TextFeatures.HeadTail(p.StimulusPlain, 1500)));
                wanted.Add((VectorIds.ReplySpace, VectorIds.Reply(p), TextFeatures.HeadTail(StyleLine.Prose(p.ReplyText), 1500)));
            }
            HashSet<string> have;
            using (var c = s.Stores.Vectors.OpenRead())
                have = Sql.QueryIn(c, "SELECT space || '|' || item_id FROM vectors WHERE model=$m AND partition_key=$p AND item_id IN ({keys})",
                    wanted.Select(w => w.Item).ToList(), r => r.GetString(0), ("$m", model), ("$p", v.PersonId)).ToHashSet(StringComparer.Ordinal);
            var missing = wanted.Where(w => !have.Contains(w.Space + "|" + w.Item)).ToList();
            int done = 0;
            foreach (var batch in missing.Chunk(64))
            {
                ct.ThrowIfCancellationRequested();
                var vectors = await encoder.EmbedAsync(batch.Select(b => b.Text.Length == 0 ? " " : b.Text).ToList(), ct).ConfigureAwait(false);
                if (vectors.Length != batch.Length) throw ReplicaFailures.Transient($"The encoder returned {vectors.Length} vectors for {batch.Length} texts.");
                long now = s.Clock.NowMs;
                await s.Stores.Vectors.WriteAsync((c, tx) =>
                {
                    using var stmt = new PreparedStatement(c, tx, """
                        INSERT OR REPLACE INTO vectors(space, item_id, model, dims, quant, scale, vec, partition_key, source_version, created_at_ms) VALUES($s,$i,$m,$d,'i8',$sc,$v,$p,1,$t)
                        """, "$s", "$i", "$m", "$d", "$sc", "$v", "$p", "$t");
                    for (int k = 0; k < batch.Length; k++)
                    {
                        var (bytes, scale) = VecMath.QuantizeI8(VecMath.Normalized(vectors[k]));
                        stmt.Run(batch[k].Space, batch[k].Item, model, vectors[k].Length, scale, bytes, v.PersonId, now);
                    }
                }, ct).ConfigureAwait(false);
                done += batch.Length;
                await job.ReportProgressAsync(new { stage = Stages.Vectors, done, total = missing.Count }).ConfigureAwait(false);
            }
            await s.Versions.UpdateFieldsAsync(v.VersionId, encoder: model, ct: ct).ConfigureAwait(false);
            return StageResult.Done($"{done} embedded, {wanted.Count - missing.Count} reused ({model})");
        }

        // ── reflexes ─────────────────────────────────────────────────────────────

        private async Task<StageResult> ReflexAsync(VersionRecord v, JobContext job)
        {
            var ct = job.Cancellation;
            if (s.Llm == null) return StageResult.Degraded("No LLM gateway: reflex classes are unavailable.");
            if (s.Llm.Llm.CircuitOpen) throw ReplicaFailures.Transient("The LLM circuit is open (credentials refused); reflexes wait.");
            List<ReplicaPair> pairs;
            using (var c = s.Stores.Intel.OpenRead())
                pairs = c.Query(null, $"SELECT {PairStore.Columns} FROM replica_pairs WHERE version_id=$v AND split='train' AND is_initiation=0 ORDER BY reply_at_ms DESC", PairStore.Map, ("$v", v.VersionId));
            // Klives-addressed stimuli first (chats are with him), then the most recent; one label per distinct stimulus.
            var chosen = pairs.OrderByDescending(p => p.PartnerIsSelf).ThenByDescending(p => p.ReplyAtMs)
                .DistinctBy(p => p.StimulusHash).Take(s.Settings.ReflexMaxStimuli).ToList();
            var cached = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var c = s.Stores.Intel.OpenRead())
                foreach (var (hash, label) in Sql.QueryIn(c, "SELECT stimulus_hash, label FROM replica_reflex_labels WHERE prompt_version=$pv AND stimulus_hash IN ({keys})",
                             chosen.Select(p => p.StimulusHash).ToList(), r => (r.GetString(0), r.GetString(1)), ("$pv", ReplicaPrompts.Reflex)))
                    cached[hash] = label;
            var todo = chosen.Where(p => !cached.ContainsKey(p.StimulusHash)).Select(p => (p.StimulusHash, StimulusForLabel(p))).ToList();
            string? problem = null;
            int calls = 0;
            if (todo.Count > 0)
            {
                var model = await s.ModelAsync(ct).ConfigureAwait(false);
                var classifier = new ReflexClassifier(s.Llm);
                var (labels, n, p, m) = await classifier.ClassifyAsync(todo, ContextPacker.Budget(model, 16_000), job.Job.JobId, ct).ConfigureAwait(false);
                calls = n;
                problem = p;
                long now = s.Clock.NowMs;
                await s.Stores.Intel.WriteAsync((c, tx) =>
                {
                    foreach (var (hash, label) in labels)
                        c.Exec(tx, "INSERT OR REPLACE INTO replica_reflex_labels(stimulus_hash, prompt_version, label, model, created_at_ms) VALUES($h,$pv,$l,$m,$t)",
                            ("$h", hash), ("$pv", ReplicaPrompts.Reflex), ("$l", label), ("$m", m), ("$t", now));
                }, ct).ConfigureAwait(false);
                foreach (var (hash, label) in labels) cached[hash] = label;
            }
            await s.Stores.Intel.WriteAsync((c, tx) =>
            {
                using var stmt = new PreparedStatement(c, tx, "UPDATE replica_pairs SET reflex_class=$l WHERE version_id=$v AND stimulus_hash=$h", "$l", "$v", "$h");
                foreach (var (hash, label) in cached) stmt.Run(label, v.VersionId, hash);
            }, ct).ConfigureAwait(false);
            int labelled = chosen.Count(p => cached.ContainsKey(p.StimulusHash));
            await UpdateCountsAsync(v.VersionId, c => c with { ReflexLabelled = labelled }, ct).ConfigureAwait(false);
            if (labelled == 0 && chosen.Count > 0) return StageResult.Degraded("No stimulus could be classified: " + (problem ?? "no labels returned"));
            string detail = string.Create(CultureInfo.InvariantCulture, $"{labelled} of {chosen.Count} stimuli labelled ({todo.Count} new, {calls} call(s))");
            return problem == null ? StageResult.Done(detail) : StageResult.Degraded(detail + "; some batches failed: " + problem);
        }

        private static string StimulusForLabel(ReplicaPair p)
        {
            var lines = p.StimulusPlain.Split('\n');
            return string.Join(" / ", lines.TakeLast(3));
        }

        private async Task UpdateCountsAsync(string versionId, Func<ReplicaCountsDto, ReplicaCountsDto> update, CancellationToken ct)
        {
            var v = s.Versions.Version(versionId);
            var counts = RJson.Read<ReplicaCountsDto>(v?.CountsJson);
            if (counts == null) return;
            await s.Versions.UpdateFieldsAsync(versionId, countsJson: RJson.Write(update(counts)), ct: ct).ConfigureAwait(false);
        }

        // ── persona ──────────────────────────────────────────────────────────────

        private async Task<string?> PersonaJobAsync(JobContext job)
        {
            var payload = job.Payload<SlicePayload>();
            if (payload == null || payload.Slice < 0) return await RunStageAsync(job, Stages.Persona, PersonaFanOutAsync).ConfigureAwait(false);
            return await PersonaSliceAsync(job, payload.Slice).ConfigureAwait(false);
        }

        /// <summary>Hands out one job per slice; the stage completes when every slice has (the join in <see cref="CompleteSliceAsync"/>).</summary>
        private async Task<StageResult> PersonaFanOutAsync(VersionRecord v, JobContext job)
        {
            var ct = job.Cancellation;
            if (!s.Settings.PersonaEnabled) return StageResult.Skipped("Persona synthesis is switched off (replica.persona_enabled).");
            if (s.Llm == null) return StageResult.Degraded("No LLM gateway: no persona sheet; measured style still runs.");
            List<int> slices;
            using (var c = s.Stores.Intel.OpenRead())
                slices = c.Query(null, "SELECT slice_index FROM replica_slices WHERE version_id=$v AND state NOT IN ('done','reused','failed') ORDER BY slice_index", r => r.GetInt32(0), ("$v", v.VersionId));
            int total;
            using (var c = s.Stores.Intel.OpenRead())
                total = (int)c.Scalar<long>(null, "SELECT COUNT(*) FROM replica_slices WHERE version_id=$v", ("$v", v.VersionId));
            if (total == 0) return StageResult.Skipped("No visible exchanges to write a persona from.");
            await s.Versions.UpdateStageAsync(v.VersionId, Stages.Persona, st => st with { Total = total }, ct).ConfigureAwait(false);
            foreach (int slice in slices) await EnqueueStageAsync(v.VersionId, Stages.Persona, slice, ct).ConfigureAwait(false);
            if (slices.Count == 0) await CompleteSliceAsync(v.VersionId, -1, null, null, ct).ConfigureAwait(false);
            return StageResult.Continues($"{slices.Count} slice job(s) handed out");
        }

        private async Task<string?> PersonaSliceAsync(JobContext job, int slice)
        {
            string versionId = VersionOf(job);
            var ct = job.Cancellation;
            var v = s.Versions.Version(versionId);
            if (v == null || v.State != VersionStates.Building || s.Llm == null) return null;
            (long From, long To, string Hash, string State)? row;
            using (var c = s.Stores.Intel.OpenRead())
                row = c.Query(null, "SELECT from_ms, to_ms, content_hash, state FROM replica_slices WHERE version_id=$v AND slice_index=$i",
                    r => ((long From, long To, string Hash, string State)?)(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3)), ("$v", versionId), ("$i", slice)).FirstOrDefault();
            if (row is not { } sl) return null;
            if (sl.State is "done" or "reused" or "failed") { await CompleteSliceAsync(versionId, slice, sl.State, null, ct).ConfigureAwait(false); return null; }
            try
            {
                var (items, dropped, state, detail) = await ExtractSliceAsync(v, slice, sl.From, sl.To, sl.Hash, job, ct).ConfigureAwait(false);
                await s.Stores.Intel.WriteAsync((c, tx) =>
                {
                    c.Exec(tx, "DELETE FROM replica_persona_items WHERE version_id=$v AND slice_index=$i", ("$v", versionId), ("$i", slice));
                    foreach (var item in items)
                        c.Exec(tx, """
                            INSERT OR REPLACE INTO replica_persona_items(version_id, item_id, slice_index, kind, subject, text, strength, first_ms, last_ms, visible_from_ms, evidence_json, sensitive)
                            VALUES($v,$id,$i,$k,$s,$t,$st,$f,$l,$vis,$e,$sen)
                            """, ("$v", versionId), ("$id", item.ItemId), ("$i", slice), ("$k", item.Kind), ("$s", item.Subject), ("$t", item.Text), ("$st", item.Strength),
                            ("$f", item.FirstMs), ("$l", item.LastMs), ("$vis", sl.To), ("$e", RJson.Write(item.Evidence)), ("$sen", item.Sensitive ? 1 : 0));
                }, ct).ConfigureAwait(false);
                await CompleteSliceAsync(versionId, slice, state, detail, ct, items.Count, dropped).ConfigureAwait(false);
                return RJson.Write(new { versionId, slice, items = items.Count, dropped, state });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (LastAttempt(job) || ex is JobFailureException { Class: ErrorClass.Permanent })
            {
                await CompleteSliceAsync(versionId, slice, "failed", ex.Message, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        private async Task<(IReadOnlyList<PersonaItem> Items, int Dropped, string State, string? Detail)> ExtractSliceAsync(VersionRecord v, int slice, long fromMs, long toMs,
            string contentHash, JobContext job, CancellationToken ct)
        {
            // The same slice content (same pairs, same text, same prompt version) was already written once: reuse it.
            string? cachedJson;
            int cachedDropped;
            using (var c = s.Stores.Intel.OpenRead())
            {
                var hit = c.Query(null, "SELECT items_json, dropped FROM replica_slice_cache WHERE person_id=$p AND content_hash=$h AND prompt_version=$pv",
                    r => (Json: r.GetString(0), Dropped: r.GetInt32(1)), ("$p", v.PersonId), ("$h", contentHash), ("$pv", ReplicaPrompts.Persona)).FirstOrDefault();
                cachedJson = hit.Json;
                cachedDropped = hit.Dropped;
            }
            if (cachedJson != null && RJson.Read<List<PersonaItem>>(cachedJson) is { } reused)
                return (reused.Select(i => i with { SliceIndex = slice, VisibleFromMs = toMs }).ToList(), cachedDropped, "reused", "reused from an earlier version");

            if (s.Llm!.Llm.CircuitOpen) throw ReplicaFailures.Transient("The LLM circuit is open (credentials refused); the persona waits.");
            List<ReplicaPair> pairs;
            using (var c = s.Stores.Intel.OpenRead())
                pairs = c.Query(null, $"SELECT {PairStore.Columns} FROM replica_pairs WHERE version_id=$v AND slice_index=$i AND split='train' ORDER BY reply_at_ms", PairStore.Map,
                    ("$v", v.VersionId), ("$i", slice));
            var plan = new SlicePlan(slice, fromMs, toMs, pairs.Select(p => p.PairId).ToList(), 0, contentHash);
            string name;
            Func<string, string?> resolve;
            using (var corpus = s.Stores.Corpus.OpenRead())
            {
                var person = People.Resolve(corpus, v.PersonId);
                var book = new NameBook(corpus);
                if (person != null) book.Load(person.IdentityIds);
                name = person?.IdentityIds.Select(i => book.NameAt(i, toMs)).FirstOrDefault(n => n != null) ?? person?.DisplayName ?? "them";
                resolve = PartnerResolver(corpus, pairs);
            }
            var model = await s.ModelAsync(ct).ConfigureAwait(false);
            var extractor = new PersonaExtractor(s.Llm);
            var result = await extractor.ExtractAsync(name, plan, pairs, resolve, ContextPacker.Budget(model, 6_000), job.Job.JobId, ct).ConfigureAwait(false);
            if (result.Problem != null && result.Items.Count == 0)
                throw ReplicaFailures.Transient("Persona extraction failed: " + result.Problem);
            long now = s.Clock.NowMs;
            await s.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT OR REPLACE INTO replica_slice_cache(person_id, content_hash, prompt_version, items_json, dropped, model, created_at_ms) VALUES($p,$h,$pv,$j,$d,$m,$t)
                """, ("$p", v.PersonId), ("$h", contentHash), ("$pv", ReplicaPrompts.Persona), ("$j", RJson.Write(result.Items)), ("$d", result.Dropped), ("$m", result.Model), ("$t", now)), ct).ConfigureAwait(false);
            return (result.Items, result.Dropped, "done", $"{result.Items.Count} items, {result.Dropped} dropped by the verifier");
        }

        /// <summary>Names in a slice → the partner person they belong to (the names those people had at the time, plan T5).</summary>
        private static Func<string, string?> PartnerResolver(SqliteConnection corpus, IReadOnlyList<ReplicaPair> pairs)
        {
            var partners = pairs.Where(p => p.PartnerPersonId != null).GroupBy(p => p.PartnerPersonId!).ToList();
            var book = new NameBook(corpus);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in partners)
            {
                var identities = corpus.Query(null, "SELECT i.identity_id FROM identities i LEFT JOIN person_canonical c ON c.person_id=i.person_id WHERE COALESCE(c.canonical_id, i.person_id)=$p",
                    r => r.GetString(0), ("$p", g.Key));
                book.Load(identities);
                foreach (var id in identities)
                    foreach (var p in g)
                        if (book.NameAt(id, p.ReplyAtMs) is { } n) map.TryAdd(n.Trim(), g.Key);
            }
            return label => map.TryGetValue(label.Trim().TrimStart('@'), out var person) ? person : null;
        }

        /// <summary>
        /// Records a finished slice and, in the same transaction, joins: when every slice is terminal the persona stage
        /// completes (degraded if any slice failed) and the next stage is handed out.
        /// </summary>
        private async Task CompleteSliceAsync(string versionId, int slice, string? state, string? detail, CancellationToken ct, int items = 0, int dropped = 0)
        {
            var next = await s.Stores.Intel.WriteAsync((c, tx) =>
            {
                if (slice >= 0 && state != null)
                    c.Exec(tx, "UPDATE replica_slices SET state=$s, detail=$d WHERE version_id=$v AND slice_index=$i", ("$s", state), ("$d", detail), ("$v", versionId), ("$i", slice));
                var counts = c.Query(tx, "SELECT state, COUNT(*) FROM replica_slices WHERE version_id=$v GROUP BY state", r => (r.GetString(0), r.GetInt32(1)), ("$v", versionId))
                    .ToDictionary(x => x.Item1, x => x.Item2);
                int total = counts.Values.Sum(), ok = counts.GetValueOrDefault("done") + counts.GetValueOrDefault("reused"), failed = counts.GetValueOrDefault("failed");
                var v = VersionStore.Version(c, tx, versionId);
                if (v == null || v.State != VersionStates.Building) return (IReadOnlyList<string>)Array.Empty<string>();
                var stage = v.Stage(Stages.Persona);
                stage = stage with { Total = total, Completed = ok, Failed = failed };
                int itemTotal = (int)c.Scalar<long>(tx, "SELECT COUNT(*) FROM replica_persona_items WHERE version_id=$v", ("$v", versionId));
                if (ok + failed < total)
                {
                    var stages = new Dictionary<string, StageState>(v.Stages, StringComparer.Ordinal) { [Stages.Persona] = stage with { State = StageStates.Running } };
                    c.Exec(tx, "UPDATE replica_versions SET stages_json=$s WHERE version_id=$v", ("$s", RJson.Write(stages)), ("$v", versionId));
                    return Array.Empty<string>();
                }
                var final = failed == 0
                    ? stage with { State = StageStates.Done, Detail = $"{itemTotal} persona items from {total} slice(s)" }
                    : stage with { State = StageStates.Degraded, Detail = $"{failed} of {total} slice(s) failed; {itemTotal} items from the rest", Error = detail };
                if (failed > 0)
                {
                    var deg = v.Degraded.ToList();
                    string note = $"Persona partial: {failed} of {total} slices failed.";
                    if (!deg.Contains(note)) deg.Add(note);
                    c.Exec(tx, "UPDATE replica_versions SET degraded_json=$d WHERE version_id=$v", ("$d", RJson.Write(deg)), ("$v", versionId));
                }
                return VersionStore.CompleteStageCore(c, tx, versionId, Stages.Persona, final, s.Clock.NowMs);
            }, ct).ConfigureAwait(false);
            if (dropped > 0 || items > 0) await UpdateCountsAsync(versionId, c => c with { PersonaItems = c.PersonaItems + items, PersonaDropped = c.PersonaDropped + dropped }, ct).ConfigureAwait(false);
            foreach (var n in next) await EnqueueStageAsync(versionId, n, ct: ct).ConfigureAwait(false);
            if (s.Versions.Version(versionId) is { } version)
            {
                await PublishAsync(version, "stage", Stages.Persona, ct).ConfigureAwait(false);
                await ReplicaCards.RefreshAsync(s, version.PersonId, ct).ConfigureAwait(false);
            }
        }

        // ── finalise ─────────────────────────────────────────────────────────────

        private async Task<string?> FinalizeAsync(JobContext job)
        {
            string versionId = VersionOf(job);
            var ct = job.Cancellation;
            var v = s.Versions.Version(versionId);
            if (v == null || v.State != VersionStates.Building) return null;
            if (!Stages.All.Where(st => st != Stages.Finalize).All(st => StageStates.IsTerminal(v.Stage(st).State)))
            {
                await ContinueAsync(versionId, ct).ConfigureAwait(false);
                throw ReplicaFailures.Transient("Not every stage has finished yet.");
            }
            // MinorGuard is checked again at the last moment: evidence may have arrived while the build ran.
            if (MinorGuard.IsRestricted(s.Kernel.Policy.BandOf(v.PersonId, s.Clock.NowMs)))
            {
                await BlockPersonAsync(v.PersonId, "MinorGuard: the person is, or may be, under 18.", ct).ConfigureAwait(false);
                return null;
            }
            await s.Versions.UpdateStageAsync(versionId, Stages.Finalize, st => st with { State = StageStates.Running, Attempts = job.Job.Attempts, StartedAtMs = s.Clock.NowMs }, ct).ConfigureAwait(false);
            await s.Versions.CompleteStageAsync(versionId, Stages.Finalize, new StageState(StageStates.Done, job.Job.Attempts, null, null, "swapped in"), ct).ConfigureAwait(false);
            var (swapped, previous) = await s.Versions.SwapAsync(versionId, ct).ConfigureAwait(false);
            if (!swapped) return null;
            s.Snapshots.Evict(versionId);
            await s.Versions.PruneAsync(v.PersonId, s.Settings.VersionRetention, ct).ConfigureAwait(false);
            var now = s.Versions.Version(versionId)!;
            await PublishAsync(now, "ready", null, ct).ConfigureAwait(false);
            await ReplicaCards.RefreshAsync(s, v.PersonId, ct).ConfigureAwait(false);
            return RJson.Write(new { versionId, previous, state = VersionStates.Ready });
        }

        // ── MinorGuard ───────────────────────────────────────────────────────────

        /// <summary>No replica of a (likely) minor: serving stops at once, the build ends, and every derived replica row
        /// about them is purged (operator.db chats are Klives's own and stay, read-only).</summary>
        public async Task BlockPersonAsync(string personId, string reason, CancellationToken ct)
        {
            var building = s.Versions.Person(personId)?.BuildingVersionId;
            await s.Versions.BlockAsync(personId, reason, ct).ConfigureAwait(false);
            if (building != null) await CancelJobsAsync(building, CancellationToken.None).ConfigureAwait(false);
            await PurgeDerivedAsync(personId, ct).ConfigureAwait(false);
            s.Snapshots.Clear();
            await s.PublishAsync(ReplicaTopics.Person, new { personId, status = PersonStatus.Blocked, reason }, ct).ConfigureAwait(false);
            await ReplicaCards.RefreshAsync(s, personId, ct).ConfigureAwait(false);
        }

        /// <summary>Deletes every derived replica row and vector about a person (the "forget derived" level, plan R3).</summary>
        public async Task PurgeDerivedAsync(string personId, CancellationToken ct)
        {
            await s.Stores.Intel.WriteAsync((c, tx) =>
            {
                var versions = c.Query(tx, "SELECT version_id FROM replica_versions WHERE person_id=$p", r => r.GetString(0), ("$p", personId));
                foreach (var v in versions)
                    foreach (var table in new[] { "replica_pairs", "replica_eras", "replica_slices", "replica_persona_items", "replica_eval_items" })
                        c.Exec(tx, $"DELETE FROM {table} WHERE version_id=$v", ("$v", v));
                c.Exec(tx, "DELETE FROM replica_slice_cache WHERE person_id=$p", ("$p", personId));
                c.Exec(tx, "DELETE FROM replica_generations WHERE person_id=$p", ("$p", personId));
                c.Exec(tx, "DELETE FROM replica_blind_sessions WHERE person_id=$p", ("$p", personId));
                c.Exec(tx, "UPDATE replica_versions SET metrics_json=NULL, style_json=NULL, config_json=NULL WHERE person_id=$p", ("$p", personId));
            }, ct).ConfigureAwait(false);
            await s.Stores.Vectors.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "DELETE FROM vectors WHERE space IN ($a,$b) AND partition_key=$p", ("$a", VectorIds.StimulusSpace), ("$b", VectorIds.ReplySpace), ("$p", personId));
            }, ct).ConfigureAwait(false);
            // Summaries of chats that are no longer in Klives's records: forgetting a person removes their chats there first.
            HashSet<string> chats;
            using (var op = s.Stores.Operator.OpenRead()) chats = op.Query(null, "SELECT chat_id FROM replica_chats", r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);
            await s.Stores.Intel.WriteAsync((c, tx) =>
            {
                foreach (string chat in c.Query(tx, "SELECT chat_id FROM replica_chat_memory", r => r.GetString(0)).Where(id => !chats.Contains(id)).ToList())
                    c.Exec(tx, "DELETE FROM replica_chat_memory WHERE chat_id=$c", ("$c", chat));
            }, ct).ConfigureAwait(false);
        }

        // ── helpers ──────────────────────────────────────────────────────────────

        private Task PublishAsync(VersionRecord v, string what, string? stage, CancellationToken ct) =>
            s.PublishAsync(ReplicaTopics.Version, new { personId = v.PersonId, versionId = v.VersionId, number = v.Number, what, stage }, ct);

        private Task OperatorAuditAsync(string actor, string action, string target, object detail, CancellationToken ct) =>
            Kernel.OperatorAudit.WriteAsync(s.Stores.Operator, s.Clock, actor, action, target, detail, ct);
    }
}
