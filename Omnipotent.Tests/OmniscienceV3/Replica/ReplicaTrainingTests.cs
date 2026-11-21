using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Replica;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Tests.OmniscienceV3.Replica
{
    /// <summary>
    /// Versioned training (F011–F016, F266–F286): every stage is a durable job, builds never wedge, the serving version
    /// keeps serving until the swap, optional stages degrade, required ones fail visibly, preconditions are refused
    /// before anything is queued, MinorGuard blocks and purges.
    /// </summary>
    public class ReplicaTrainingTests
    {
        [Fact]
        public async Task Train_RunsEveryStageAndSwapsTheVersionIn()
        {
            await using var h = await ReplicaHarness.StartAsync();
            var v = await h.TrainAsync(Personas.Lena);
            Assert.Equal(VersionStates.Ready, v.State);
            foreach (var stage in Stages.All) Assert.True(StageStates.IsTerminal(v.Stage(stage).State), $"{stage} is {v.Stage(stage).State}");
            Assert.Equal(StageStates.Done, v.Stage(Stages.Index).State);
            Assert.Equal(StageStates.Done, v.Stage(Stages.Vectors).State);
            Assert.Equal(StageStates.Done, v.Stage(Stages.Reflex).State);
            Assert.Equal(StageStates.Done, v.Stage(Stages.Persona).State);
            var person = h.R.Services.Versions.Person(Personas.Lena)!;
            Assert.Equal(v.VersionId, person.ServingVersionId);
            Assert.Null(person.BuildingVersionId);
            Assert.Equal(PersonStatus.Ready, person.Status);
            var counts = RJson.Read<ReplicaCountsDto>(v.CountsJson)!;
            Assert.True(counts.Replies > 300, $"replies {counts.Replies}");
            Assert.True(counts.HoldoutTest > 0 && counts.HoldoutTune > 0);
            Assert.NotNull(v.EncoderModel);
            var metrics = RJson.Read<ReplicaMetricsDto>(v.MetricsJson);
            Assert.NotNull(metrics);
            Assert.True(metrics!.Evaluated > 0, metrics.Note);
            // The card read model reflects it.
            var card = ReplicaCards.Build(h.R.Services, Personas.Lena)!;
            Assert.Equal(v.VersionId, card.Serving!.VersionId);
            Assert.Single(card.FidelityTrend);
        }

        [Fact]
        public async Task Train_RefusesTheUnknownBeforeQueueing_ButAllowsAMinor()
        {
            await using var h = await ReplicaHarness.StartAsync();
            var unknown = await Assert.ThrowsAsync<ReplicaRefusedException>(() => h.R.Trainer.RequestAsync("p-nobody", "t", "tester", CancellationToken.None));
            Assert.True(unknown.NotFound);
            var minor = await Assert.ThrowsAsync<ReplicaRefusedException>(() => h.R.Trainer.RequestAsync(Personas.Max, "t", "tester", CancellationToken.None));
            Assert.DoesNotContain("under 18", minor.Message);
            Assert.Contains("at least 100", minor.Message);
            Assert.Null(h.R.Services.Versions.Person(Personas.Max)); // no build was queued, and nobody was blocked
        }

        [Fact]
        public async Task Train_ClearlyTooLittleData_IsRefusedWithTheNumbers()
        {
            await using var h = await ReplicaHarness.StartAsync();
            var ex = await Assert.ThrowsAsync<ReplicaRefusedException>(() => h.R.Trainer.RequestAsync(Personas.Bob, "t", "tester", CancellationToken.None));
            Assert.Contains("Not possible yet", ex.Message);
            Assert.Matches(@"\d+ messages", ex.Message);
        }

        [Fact]
        public async Task Train_OnlyBotCommands_FailsAtTheIndexWithReadinessNumbers()
        {
            await using var h = await ReplicaHarness.StartAsync();
            // 120 messages that are all bot commands answered by a bot: enough for the quick count, nothing the person really wrote.
            await h.Runtime.Stores.Corpus.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "INSERT INTO persons(person_id, created_at_ms, display_name) VALUES('p-cmd',0,'Cmd')");
                c.Exec(tx, "INSERT INTO person_canonical(person_id, canonical_id) VALUES('p-cmd','p-cmd')");
                c.Exec(tx, "INSERT INTO identities(identity_id, platform, platform_user_id, person_id, kind, first_seen_ms, last_seen_ms) VALUES('discord:cmd','discord','cmd','p-cmd','user',0,0)");
                long max = c.Scalar<long?>(tx, "SELECT MAX(order_key) FROM messages") ?? 0;
                var w = new Personas.Writer { C = c, Tx = tx, Now = h.Clock.NowMs, Snowflake = max };
                for (int i = 0; i < 120; i++)
                {
                    long at = Personas.Start + i * 3_600_000L * 5;
                    var cmd = Personas.Post(w, Personas.Guild, "p-cmd", at, "!roll d" + (i + 2), identity: "discord:cmd");
                    Personas.Post(w, Personas.Guild, "p-bot", at + 1_500, "rolled " + i, identity: "discord:bot1", bot: true, replyTo: cmd);
                }
            });
            var v = await h.TrainAsync("p-cmd");
            Assert.Equal(VersionStates.Failed, v.State);
            Assert.Contains("Not possible yet", v.Error);
            var readiness = RJson.Read<ReplicaReadinessDto>(h.R.Services.Versions.Person("p-cmd")!.ReadinessJson)!;
            Assert.Equal(ReplicaTier.NotPossible, readiness.Tier);
            Assert.Equal(0, readiness.Replies + readiness.Initiations);
            var job = h.Runtime.Host.Jobs.Query(new JobQuery { RunId = v.VersionId, Kind = ReplicaTrainer.KindIndex }).Items.Single();
            Assert.Equal(JobStates.Succeeded, job.State); // the stage ran correctly; the data said no
        }

        [Fact]
        public async Task Train_IsSingleFlightAndReturnsItsOwnJob()
        {
            await using var h = await ReplicaHarness.StartAsync();
            var first = await h.R.Trainer.RequestAsync(Personas.Lena, "t", "tester", CancellationToken.None);
            var second = await h.R.Trainer.RequestAsync(Personas.Lena, "t", "tester", CancellationToken.None);
            Assert.False(first.AlreadyRunning);
            Assert.True(second.AlreadyRunning);
            Assert.Equal(first.VersionId, second.VersionId);
            Assert.True(first.JobId > 0);
            Assert.Equal(first.JobId, h.Runtime.Host.Jobs.Query(new JobQuery { RunId = first.VersionId, Kind = ReplicaTrainer.KindIndex }).Items.Single().JobId);
        }

        [Fact]
        public async Task Train_CrashMidStage_IsReclaimedAndFinishes()
        {
            await using var h = await ReplicaHarness.StartAsync();
            bool crashed = false;
            var result = await h.R.Trainer.RequestAsync(Personas.Lena, "t", "tester", CancellationToken.None);
            await h.DrainAsync(crashWhen: j => { if (j.Kind == ReplicaTrainer.KindPersona && !crashed) { crashed = true; return true; } return false; });
            Assert.True(crashed);
            Assert.Equal(VersionStates.Building, h.R.Services.Versions.Version(result.VersionId)!.State); // the crashed lease still holds a stage
            h.Clock.Advance(TimeSpan.FromMinutes(11));
            Assert.True(await h.Runtime.Host.Jobs.ReclaimExpiredAsync() >= 1);
            await h.DrainAsync();
            Assert.Equal(VersionStates.Ready, h.R.Services.Versions.Version(result.VersionId)!.State);
        }

        [Fact]
        public async Task Train_EncoderFailure_DegradesInsteadOfFailing()
        {
            await using var h = await ReplicaHarness.StartAsync();
            h.Encoder.Fail = true;
            var v = await h.TrainAsync(Personas.Arthur);
            Assert.Equal(VersionStates.Ready, v.State);
            Assert.Equal(StageStates.Degraded, v.Stage(Stages.Vectors).State);
            Assert.Contains(v.Degraded, d => d.Contains("vectors", StringComparison.OrdinalIgnoreCase));
            Assert.Null(v.EncoderModel);
        }

        [Fact]
        public async Task Train_WithoutAnEncoder_StillServes()
        {
            await using var h = await ReplicaHarness.StartAsync(encoder: false);
            var v = await h.TrainAsync(Personas.Sofia);
            Assert.Equal(VersionStates.Ready, v.State);
            Assert.Equal(StageStates.Degraded, v.Stage(Stages.Vectors).State);
            Assert.Contains(v.Degraded, d => d.Contains("encoder", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task Retrain_KeepsServingThePreviousVersionUntilTheSwap()
        {
            await using var h = await ReplicaHarness.StartAsync();
            var v1 = await h.TrainAsync(Personas.Lena);
            var request = await h.R.Trainer.RequestAsync(Personas.Lena, "again", "tester", CancellationToken.None);
            Assert.NotEqual(v1.VersionId, request.VersionId);
            await h.DrainAsync(max: 1); // the index only
            var mid = h.R.Services.Versions.Person(Personas.Lena)!;
            Assert.Equal(v1.VersionId, mid.ServingVersionId);
            Assert.Equal(request.VersionId, mid.BuildingVersionId);
            Assert.Equal(PersonStatus.Ready, mid.Status);
            await h.DrainAsync();
            var after = h.R.Services.Versions.Person(Personas.Lena)!;
            Assert.Equal(request.VersionId, after.ServingVersionId);
            Assert.Equal(VersionStates.Retired, h.R.Services.Versions.Version(v1.VersionId)!.State);
        }

        [Fact]
        public async Task FailedRetrain_LeavesTheServingVersionAlone()
        {
            await using var h = await ReplicaHarness.StartAsync();
            var v1 = await h.TrainAsync(Personas.Lena);
            var request = await h.R.Trainer.RequestAsync(Personas.Lena, "again", "tester", CancellationToken.None);
            // The person disappears from the corpus before the index runs: a required stage fails for good.
            await h.Runtime.Stores.Corpus.WriteAsync((c, tx) => c.Exec(tx, "DELETE FROM persons WHERE person_id=$p", ("$p", Personas.Lena)));
            await h.DrainAsync();
            var v2 = h.R.Services.Versions.Version(request.VersionId)!;
            Assert.Equal(VersionStates.Failed, v2.State);
            Assert.Contains("no longer exists", v2.Error);
            var job = h.Runtime.Host.Jobs.Query(new JobQuery { RunId = v2.VersionId, Kind = ReplicaTrainer.KindIndex }).Items.Single();
            Assert.Equal(JobStates.Dead, job.State); // dead-lettered with its reason, visible on the Jobs page (F016)
            var person = h.R.Services.Versions.Person(Personas.Lena)!;
            Assert.Equal(v1.VersionId, person.ServingVersionId);
            Assert.Equal(PersonStatus.Ready, person.Status);
            Assert.Contains("Last build failed", person.StatusDetail);
        }

        [Fact]
        public async Task Cancel_EndsTheBuildAndItsJobs()
        {
            await using var h = await ReplicaHarness.StartAsync();
            var request = await h.R.Trainer.RequestAsync(Personas.Lena, "t", "tester", CancellationToken.None);
            await h.DrainAsync(max: 1);
            Assert.True(await h.R.Trainer.CancelAsync(Personas.Lena, "tester", CancellationToken.None));
            Assert.Equal(VersionStates.Cancelled, h.R.Services.Versions.Version(request.VersionId)!.State);
            Assert.Empty(h.Runtime.Host.Jobs.Query(new JobQuery { RunId = request.VersionId, State = JobStates.Queued }).Items);
            await h.DrainAsync();
            Assert.Equal(VersionStates.Cancelled, h.R.Services.Versions.Version(request.VersionId)!.State);
        }

        [Fact]
        public async Task Maintenance_RehandsOutStagesWhoseJobsVanished()
        {
            await using var h = await ReplicaHarness.StartAsync();
            var request = await h.R.Trainer.RequestAsync(Personas.Arthur, "t", "tester", CancellationToken.None);
            await h.DrainAsync(max: 1);
            // Something cancelled every follow-up job (a module disable, an operator mistake): the build would wait forever.
            foreach (var job in h.Runtime.Host.Jobs.Query(new JobQuery { RunId = request.VersionId, State = JobStates.Queued }).Items)
                await h.Runtime.Host.Jobs.CancelAsync(job.JobId);
            await h.Runtime.Host.Jobs.EnqueueAsync(new JobRequest { ModuleId = ReplicaSchema.ModuleId, Kind = ReplicaModule.KindMaintenance, Lane = Lane.Cpu });
            await h.DrainAsync();
            Assert.Equal(VersionStates.Ready, h.R.Services.Versions.Version(request.VersionId)!.State);
        }

        [Fact]
        public async Task MinorEvidence_LeavesServingAndDerivedDataAlone()
        {
            await using var h = await ReplicaHarness.StartAsync();
            var v = await h.TrainAsync(Personas.Lena);
            await h.Runtime.Stores.Operator.WriteAsync((c, tx) => c.Exec(tx, "INSERT INTO person_settings(person_id, minor_marked, updated_at_ms) VALUES($p,1,0)", ("$p", Personas.Lena)));
            await h.Runtime.Host.Jobs.EnqueueAsync(new JobRequest { ModuleId = ReplicaSchema.ModuleId, Kind = ReplicaModule.KindMaintenance, Lane = Lane.Cpu });
            await h.DrainAsync();
            var person = h.R.Services.Versions.Person(Personas.Lena)!;
            Assert.NotEqual(PersonStatus.Blocked, person.Status);
            Assert.NotNull(person.ServingVersionId);
            using (var c = h.Runtime.Stores.Intel.OpenRead())
            {
                Assert.True(c.Scalar<long>(null, "SELECT COUNT(*) FROM replica_pairs WHERE version_id=$v", ("$v", v.VersionId)) > 0);
                Assert.True(c.Scalar<long>(null, "SELECT COUNT(*) FROM replica_persona_items WHERE version_id=$v", ("$v", v.VersionId)) > 0);
                Assert.True(c.Scalar<long>(null, "SELECT COUNT(*) FROM replica_eval_items WHERE person_id=$p", ("$p", Personas.Lena)) > 0);
            }
            using (var c = h.Runtime.Stores.Vectors.OpenRead())
                Assert.True(c.Scalar<long>(null, "SELECT COUNT(*) FROM vectors WHERE partition_key=$p", ("$p", Personas.Lena)) > 0);
        }

        [Fact]
        public async Task NewMessages_MakeTheReplicaStaleAndRetrainIt()
        {
            await using var h = await ReplicaHarness.StartAsync();
            var v1 = await h.TrainAsync(Personas.Lena);
            await h.Runtime.Host.Settings.SetAsync("replica.retrain_min_new_messages", 10, "test");
            long at = Personas.End + 600_000;
            for (int i = 0; i < 12; i++)
                await Personas.AddMessageAsync(h.Runtime.Stores, h.Clock, Personas.DmLena, Personas.Lena, at + i * 60_000, "new msg " + i);
            await h.Runtime.Host.PumpChangesOnceAsync();
            await h.Runtime.Host.PlanOnceAsync();
            await h.DrainAsync();
            var person = h.R.Services.Versions.Person(Personas.Lena)!;
            Assert.NotEqual(v1.VersionId, person.ServingVersionId);
            var v2 = h.R.Services.Versions.Version(person.ServingVersionId!)!;
            Assert.StartsWith("stale:", v2.Reason);
            Assert.False(person.Stale);
        }
    }
}
