using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Ml;
using Omnipotent.Services.OmniscienceV3.ReadModels;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    /// <summary>
    /// The replica module (WS-D, plan "Replica (fidelity first)"): versioned training, chat, evaluation, Rehearsal (N5),
    /// Group Simulation (N6) and the social tactics X53–X65. It owns its intel tables (<see cref="ReplicaSchema"/>), reads
    /// the corpus, the knowledge store (facts as of a cutoff) and the shared text encoder (<see cref="IEmbeddingEncoder"/>,
    /// provided by the analytics workstream; optional: without it retrieval is lexical and the judge has no embedding
    /// signals, and every version says so). All work is durable jobs: training on the background LLM lane, chat on the
    /// interactive one. MinorGuard: no replica of a minor, ever (checked at request, build, swap, use and hourly).
    /// </summary>
    public sealed class ReplicaModule : IntelModuleBase
    {
        public const int ModuleVersion = 1;
        public const string KindPersonChanged = "person.changed";
        public const string KindMaintenance = "maintenance";

        private readonly Func<IEmbeddingEncoder?> encoder;
        private ReplicaRuntime? runtime;
        private readonly IReadOnlyList<ITactic> tactics;

        public ReplicaModule(Func<IEmbeddingEncoder?>? encoder = null)
        {
            this.encoder = encoder ?? (() => null);
            ReplicaRuntime R() => runtime ?? throw new TacticRefusedException("The replica module is still starting.");
            tactics = new ITactic[]
            {
                new BestTimeTactic(R), new ConversationStartersTactic(R), new RehearsalTactic(R), new ToneCheckTactic(R), new DraftReplyTactic(R),
                new GiftFinderTactic(R), new InvitePlannerTactic(R), new ReconnectTactic(R), new MeetingPrepTactic(R), new FollowThroughTactic(R),
                new GroupSimulationTactic(R), new WhoCanHelpTactic(R), new MediationTactic(R),
            };
        }

        /// <summary>The module's live parts (null until initialised).</summary>
        public ReplicaRuntime? Runtime => runtime;

        public override IReadOnlyList<ITactic> Tactics => tactics;

        public override IReadOnlyList<Migration> IntelMigrations => ReplicaSchema.Migrations;

        public override ModuleManifest Manifest { get; } = new()
        {
            Id = ReplicaSchema.ModuleId,
            Version = ModuleVersion,
            Title = "Replicas",
            Category = "replica",
            Description = "Replicas of people from their real messages: versioned training (index, style, persona, reflexes, evaluation), chat, rehearsal, group simulation and social tactics. Fidelity is measured on held-out real replies.",
            DefaultLane = Lane.Cpu,
            MinorRestricted = true,
            SubscribedChangeKinds = new[]
            {
                ChangeKinds.MessageInsert, ChangeKinds.MessageVersion, ChangeKinds.MessageDelete, ChangeKinds.PersonMerge, ChangeKinds.PersonUnmerge, ChangeKinds.PersonErase,
            },
            Schedules = new[]
            {
                new ScheduleTrigger(KindMaintenance, Every: TimeSpan.FromHours(1)),
                new ScheduleTrigger("house-models", Every: TimeSpan.FromHours(24)),
            },
            Settings = ReplicaSettings.Descriptors,
            ReadModels = new[]
            {
                new ReadModelDescriptor(ReplicaCards.ReadModelId, "Replica card", "omniscience.replica.read", ReplicaCards.SchemaVersion, (long)TimeSpan.FromMinutes(10).TotalMilliseconds,
                    "Readiness with numbers, the serving and building versions with every stage, fidelity per version and staleness."),
            },
            Datasets = new[]
            {
                new DatasetDescriptor("replica.pairs", "Replica exchanges", new[]
                {
                    new DimensionBinding("time.occurred", "reply_at_ms"), new DimensionBinding("person.author", "person_id"), new DimensionBinding("person.partner", "partner_person_id"),
                    new DimensionBinding("where.conversation", "conversation_id"), new DimensionBinding("where.guild", "guild_id"), new DimensionBinding("where.kind", "context_kind"),
                    new DimensionBinding("where.platform", "platform"), new DimensionBinding("content.length", "reply_chars"), new DimensionBinding("content.is_reply", "explicit_reply"),
                    new DimensionBinding("prov.status", "split"),
                }, new[]
                {
                    new MeasureDescriptor("exchanges", "Exchanges", "count", null), new MeasureDescriptor("latency_p50", "Median reply latency", "p50", "latency_ms", "ms"),
                    new MeasureDescriptor("latency_avg", "Mean reply latency", "avg", "latency_ms", "ms"), new MeasureDescriptor("reply_chars", "Mean reply length", "avg", "reply_chars", "chars"),
                    new MeasureDescriptor("partners", "Distinct partners", "distinct", "partner_person_id"),
                }, "replica_pair", "omniscience.replica.read", MinorFiltered: true),
                new DatasetDescriptor("replica.generations", "Replica turns", new[]
                {
                    new DimensionBinding("time.occurred", "created_at_ms"), new DimensionBinding("person.subject", "person_id"), new DimensionBinding("where.kind", "context_kind"),
                    new DimensionBinding("prov.model", "model"), new DimensionBinding("prov.prompt_version", "prompt_version"), new DimensionBinding("prov.status", "outcome"),
                    new DimensionBinding("prov.run", "run_id"), new DimensionBinding("prov.module", "source"),
                }, new[]
                {
                    new MeasureDescriptor("turns", "Turns", "count", null), new MeasureDescriptor("latency_p95", "p95 latency", "p95", "latency_ms", "ms"),
                    new MeasureDescriptor("prompt_tokens", "Prompt tokens", "sum", "prompt_tokens", "tokens"), new MeasureDescriptor("cached_tokens", "Cached tokens", "sum", "cached_tokens", "tokens"),
                    new MeasureDescriptor("completion_tokens", "Completion tokens", "sum", "completion_tokens", "tokens"), new MeasureDescriptor("judge_score", "Mean judge score", "avg", "chosen_score"),
                }, "replica_generation", "omniscience.replica.read", MinorFiltered: true),
                new DatasetDescriptor("replica.evals", "Replica fidelity evaluations", new[]
                {
                    new DimensionBinding("time.occurred", "occurred_ms"), new DimensionBinding("time.recorded", "created_at_ms"), new DimensionBinding("person.subject", "person_id"),
                    new DimensionBinding("where.kind", "context_kind"), new DimensionBinding("prov.model", "model"), new DimensionBinding("prov.prompt_version", "prompt_version"),
                    new DimensionBinding("prov.status", "outcome"),
                }, new[]
                {
                    new MeasureDescriptor("items", "Evaluated replies", "count", null), new MeasureDescriptor("discrimination", "Judge accuracy (50% = indistinguishable)", "avg", "judge_correct"),
                    new MeasureDescriptor("similarity", "Embedding similarity", "avg", "embedding_sim"), new MeasureDescriptor("style_distance", "Style distance", "avg", "style_distance"),
                    new MeasureDescriptor("length_error", "Length error", "avg", "length_error"),
                }, "replica_eval", "omniscience.replica.read", MinorFiltered: true),
            },
        };

        public override async Task InitializeAsync(ModuleContext context, CancellationToken ct)
        {
            await base.InitializeAsync(context, ct).ConfigureAwait(false);
            var services = new ReplicaServices(context, encoder, ModuleVersion);
            var chat = new ReplicaChatOrchestrator(services);
            runtime = new ReplicaRuntime
            {
                Services = services,
                Trainer = new ReplicaTrainer(services),
                Chat = chat,
                Groups = new GroupSimulation(services, chat.Chats),
                Blind = new BlindTest(services),
            };
        }

        // ── change feed and planning ─────────────────────────────────────────────

        /// <summary>Authors of changed messages and merged/erased people become dirty (pure: ids straight from the change log).</summary>
        public override IEnumerable<DirtySubject> MapChanges(IReadOnlyList<ChangeRecord> changes)
        {
            foreach (var c in changes)
            {
                switch (c.Kind)
                {
                    case ChangeKinds.MessageInsert or ChangeKinds.MessageVersion or ChangeKinds.MessageDelete:
                        if (c.PersonId != null) yield return new DirtySubject("person", c.PersonId);
                        break;
                    case ChangeKinds.PersonMerge or ChangeKinds.PersonUnmerge or ChangeKinds.PersonErase:
                        yield return new DirtySubject("person", c.EntityId);
                        if (c.PersonId != null) yield return new DirtySubject("person", c.PersonId);
                        break;
                }
            }
        }

        public override IEnumerable<JobRequest> Plan(PlanContext context)
        {
            var jobs = new List<JobRequest>();
            if (context.Dirty.Count > 0)
            {
                // Only people who have (or had) a replica need anything; everyone else's messages cost one lookup here.
                HashSet<string> known;
                using (var c = Context.Stores.Intel.OpenRead())
                    known = c.Query(null, "SELECT person_id FROM replica_persons", r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);
                if (known.Count > 0)
                    foreach (var d in context.Dirty.Where(d => d.Kind == "person"))
                    {
                        string canonical = Context.Policy.Canonical(d.Id);
                        if (known.Contains(d.Id) || known.Contains(canonical)) jobs.Add(Job(KindPersonChanged, "person", known.Contains(d.Id) ? d.Id : canonical, priority: 60));
                    }
            }
            if (context.VersionChanged || context.DueSchedules.Any(s => s.Name == KindMaintenance))
                jobs.Add(Job(KindMaintenance, priority: 50, payloadJson: context.VersionChanged ? RJson.Write(new { moduleUpgrade = true }) : null));
            if (context.DueSchedules.Any(s => s.Name == "house-models")) jobs.Add(Job(AssistantVoiceTrainer.Kind, priority: 20, lane: Lane.LlmBackground));
            return jobs.DistinctBy(j => j.DedupeKey).ToList();
        }

        public override async Task<string?> ExecuteAsync(JobContext job)
        {
            var r = runtime ?? throw ReplicaFailures.Transient("The replica module is not initialised yet.");
            return job.Job.Kind switch
            {
                ReplicaChatOrchestrator.KindTurn => await r.Chat.ExecuteTurnAsync(job).ConfigureAwait(false),
                GroupSimulation.KindAdvance => await r.Groups.ExecuteAdvanceAsync(job).ConfigureAwait(false),
                KindPersonChanged => await PersonChangedAsync(r, job).ConfigureAwait(false),
                KindMaintenance => await MaintenanceAsync(r, job).ConfigureAwait(false),
                AssistantVoiceTrainer.Kind => await new AssistantVoiceTrainer(r.Services).ExecuteAsync(job).ConfigureAwait(false),
                _ => await r.Trainer.ExecuteAsync(job).ConfigureAwait(false),
            };
        }

        // ── staleness and auto-retrain (F284) ────────────────────────────────────

        private async Task<string?> PersonChangedAsync(ReplicaRuntime r, JobContext job)
        {
            var s = r.Services;
            var ct = job.Cancellation;
            string id = job.Job.SubjectId!;
            string canonical = Context.Policy.Canonical(id);
            if (canonical != id && s.Versions.Person(id) != null)
            {
                await s.Versions.RekeyAsync(id, canonical, ct).ConfigureAwait(false);
                s.Snapshots.Clear();
            }
            var person = s.Versions.Person(canonical);
            if (person == null) return null;
            PersonScope? scope;
            using (var corpus = s.Stores.Corpus.OpenRead()) scope = People.Resolve(corpus, canonical);
            if (scope == null)
            {
                // Erased from the corpus (N20 erase): nothing of them may remain in derived stores.
                await r.Trainer.BlockPersonAsync(canonical, "The person was erased from the corpus.", ct).ConfigureAwait(false);
                return RJson.Write(new { canonical, erased = true });
            }
            if (person.Status == PersonStatus.Blocked) return null;
            if (MinorGuard.IsRestricted(Context.Policy.BandOf(canonical, s.Clock.NowMs)))
            {
                await r.Trainer.BlockPersonAsync(canonical, "MinorGuard: new evidence says the person is, or may be, under 18.", ct).ConfigureAwait(false);
                return RJson.Write(new { canonical, blocked = true });
            }
            if (person.ServingVersionId == null) return null;
            int fresh = NewAuthored(s, scope, person.ServedSourceSeq);
            var serving = s.Versions.Version(person.ServingVersionId);
            bool old = serving?.FinishedAtMs is long done && s.Clock.NowMs - done >= (long)s.Settings.RetrainMaxAge.TotalMilliseconds;
            bool stale = fresh >= s.Settings.RetrainMinNewMessages || (fresh > 0 && old);
            if (!stale)
            {
                await s.Versions.SetNewAuthoredAsync(canonical, fresh, ct).ConfigureAwait(false);
                return null;
            }
            string reason = fresh >= s.Settings.RetrainMinNewMessages ? $"{fresh} new messages since v{serving?.Number}" : $"v{serving?.Number} is over {s.Settings.RetrainMaxAge.TotalDays:0} days old and {fresh} new messages arrived";
            await s.Versions.MarkStaleAsync(canonical, reason, fresh, ct).ConfigureAwait(false);
            await AutoRetrainAsync(r, canonical, reason, ct).ConfigureAwait(false);
            await ReplicaCards.RefreshAsync(s, canonical, ct).ConfigureAwait(false);
            return RJson.Write(new { canonical, stale = true, reason });
        }

        private static async Task AutoRetrainAsync(ReplicaRuntime r, string personId, string reason, CancellationToken ct)
        {
            var s = r.Services;
            var person = s.Versions.Person(personId);
            if (!s.Settings.AutoRetrain || person == null || !person.AutoRetrain || person.BuildingVersionId != null) return;
            try { await r.Trainer.RequestAsync(personId, "stale: " + reason, "replica-auto", ct).ConfigureAwait(false); }
            catch (ReplicaRefusedException ex) { await s.PublishAsync(ReplicaTopics.Person, new { personId, autoRetrain = "refused", reason = ex.Message }, ct).ConfigureAwait(false); }
        }

        /// <summary>New authored messages since a corpus watermark (change log by person: an index range).</summary>
        private static int NewAuthored(ReplicaServices s, PersonScope scope, long sinceSeq)
        {
            using var corpus = s.Stores.Corpus.OpenRead();
            long n = 0;
            foreach (var member in scope.MemberIds)
                n += corpus.Scalar<long>(null, "SELECT COUNT(*) FROM change_log WHERE person_id=$p AND seq > $s AND kind=$k", ("$p", member), ("$s", sinceSeq), ("$k", ChangeKinds.MessageInsert));
            return (int)Math.Min(int.MaxValue, n);
        }

        // ── maintenance (hourly safety net) ──────────────────────────────────────

        private sealed record MaintenancePayload(bool ModuleUpgrade);

        /// <summary>
        /// The safety net that makes "training forever" impossible: MinorGuard re-checked for everyone with a replica;
        /// builds whose stage jobs vanished are re-handed out, builds without progress for a day are failed with that
        /// reason (the serving version is untouched); versions built with another encoder or module version are marked
        /// stale and retrained; old version data is pruned.
        /// </summary>
        private async Task<string?> MaintenanceAsync(ReplicaRuntime r, JobContext job)
        {
            var s = r.Services;
            var ct = job.Cancellation;
            bool upgrade = job.Payload<MaintenancePayload>()?.ModuleUpgrade == true;
            int blocked = 0, resumed = 0, stalled = 0, staled = 0;
            string? encoderModel = s.EncoderModel;
            foreach (var person in s.Versions.Persons())
            {
                ct.ThrowIfCancellationRequested();
                if (person.Status == PersonStatus.Blocked) continue;
                if ((person.ServingVersionId != null || person.BuildingVersionId != null) && MinorGuard.IsRestricted(Context.Policy.BandOf(person.PersonId, s.Clock.NowMs)))
                {
                    await r.Trainer.BlockPersonAsync(person.PersonId, "MinorGuard: the person is, or may be, under 18.", ct).ConfigureAwait(false);
                    blocked++;
                    continue;
                }
                if (person.ServingVersionId is { } serving && s.Versions.Version(serving) is { } v && !person.Stale)
                {
                    string? reason = upgrade && v.ModuleVersion != ModuleVersion ? $"the replica module was upgraded to v{ModuleVersion}"
                        : encoderModel != null && v.EncoderModel != encoderModel ? "the shared text encoder changed" : null;
                    if (reason != null)
                    {
                        await s.Versions.MarkStaleAsync(person.PersonId, reason, person.NewAuthored, ct).ConfigureAwait(false);
                        await AutoRetrainAsync(r, person.PersonId, reason, ct).ConfigureAwait(false);
                        staled++;
                    }
                }
                await s.Versions.PruneAsync(person.PersonId, s.Settings.VersionRetention, ct).ConfigureAwait(false);
            }
            foreach (var building in s.Versions.Building())
            {
                bool live = s.Kernel.Jobs.Query(new JobQuery { ModuleId = ReplicaSchema.ModuleId, RunId = building.VersionId, State = JobStates.Queued, Limit = 1 }).Items.Count > 0
                            || s.Kernel.Jobs.Query(new JobQuery { ModuleId = ReplicaSchema.ModuleId, RunId = building.VersionId, State = JobStates.Leased, Limit = 1 }).Items.Count > 0;
                if (live) continue;
                long lastProgress = new[] { building.CreatedAtMs, building.StartedAtMs ?? 0 }
                    .Concat(building.Stages.Values.Select(st => Math.Max(st.StartedAtMs ?? 0, st.FinishedAtMs ?? 0))).Max();
                if (s.Clock.NowMs - lastProgress > 24 * 3_600_000L)
                {
                    await s.Versions.EndAsync(building.VersionId, VersionStates.Failed, "stalled: no progress for 24 hours (the serving version is unaffected); train again to retry", ct).ConfigureAwait(false);
                    await ReplicaCards.RefreshAsync(s, building.PersonId, ct).ConfigureAwait(false);
                    stalled++;
                }
                else
                {
                    await r.Trainer.ContinueAsync(building.VersionId, ct).ConfigureAwait(false);
                    resumed++;
                }
            }
            return RJson.Write(new { blocked, resumed, stalled, staled, upgrade });
        }
    }
}
