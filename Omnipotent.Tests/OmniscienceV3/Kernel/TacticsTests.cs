using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3;
using Omnipotent.Services.OmniscienceV3.Api;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Kernel.Events;
using Omnipotent.Services.OmniscienceV3.Kernel.Explore;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Llm;
using Omnipotent.Services.OmniscienceV3.ReadModels;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.Kernel
{
    /// <summary>The X-engine (tactics, selections, set algebra, runs, playbooks), read models and the Explore contract.</summary>
    public class TacticsTests : IAsyncLifetime
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_tactics_" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new(Instant.FromUtc(new DateTime(2024, 11, 5, 12, 0, 0, DateTimeKind.Utc)));
        private readonly TestTactics tactics = new();
        private V3Runtime rt = null!;

        public async Task InitializeAsync()
        {
            var lease = TimeSpan.FromSeconds(3);
            var poll = TimeSpan.FromMilliseconds(25);
            rt = await V3Runtime.StartAsync(new V3RuntimeOptions
            {
                Paths = new StorePaths(root),
                Clock = clock,
                LlmClient = new NoLlm(),
                Log = _ => { },
                LogError = (_, _) => { },
                Kernel = new KernelOptions
                {
                    LoopInterval = TimeSpan.FromMilliseconds(25),
                    Lanes = new[]
                    {
                        new LaneOptions(Lane.Cpu, 2, lease, poll), new LaneOptions(Lane.Io, 1, lease, poll),
                        new LaneOptions(Lane.LlmBackground, 1, lease, poll), new LaneOptions(Lane.LlmInteractive, 1, lease, poll),
                        new LaneOptions(Lane.Coordinator, 1, lease, poll),
                    },
                },
                Modules = new IIntelModule[] { tactics },
            });
            // People: p-b was merged into p-a; p-m is marked a minor by Klives.
            await rt.Stores.Corpus.WriteAsync((c, tx) =>
            {
                foreach (var p in new[] { "p-a", "p-b", "p-c", "p-m" })
                    c.Exec(tx, "INSERT INTO persons(person_id, created_at_ms, display_name) VALUES($p,0,$p)", ("$p", p));
                c.Exec(tx, "INSERT INTO person_canonical(person_id, canonical_id) VALUES('p-a','p-a'),('p-b','p-a'),('p-c','p-c'),('p-m','p-m')");
            });
            await rt.Stores.Operator.WriteAsync((c, tx) => c.Exec(tx, "INSERT INTO person_settings(person_id, minor_marked, updated_at_ms) VALUES('p-m',1,0)"));
        }

        public async Task DisposeAsync()
        {
            tactics.Release.TrySetResult();
            await rt.DisposeAsync();
            try { Directory.Delete(root, true); }
            catch (IOException) { /* lingering handle on Windows; unique temp dir */ }
        }

        private sealed class NoLlm : ILlmClient
        {
            public Task<LlmResult> QueryAsync(LlmRequest request, CancellationToken ct) => throw new InvalidOperationException("No LLM here.");
            public Task<ModelContextInfo> GetContextAsync(string? model, CancellationToken ct) => throw new InvalidOperationException("No LLM here.");
            public void EndSession(string sessionKey) { }
        }

        /// <summary>A module contributing two test tactics: a two-stage one that can be held mid-run, and a minor-restricted one.</summary>
        private sealed class TestTactics : IntelModuleBase
        {
            public int StageOneRuns;
            public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public override ModuleManifest Manifest { get; } = new()
            {
                Id = "test.tactics", Version = 1, Title = "Test tactics",
                ReadModels = new[] { new ReadModelDescriptor("test.card", "Test card", "omniscience.overview.view", 1, 60_000) },
                Datasets = new[]
                {
                    new DatasetDescriptor("test.messages", "Messages", new[] { new DimensionBinding("time.occurred", "sent_at_ms"), new DimensionBinding("person.author", "author") },
                        new[] { new MeasureDescriptor("count", "Messages", "count", null) }, "message", "omniscience.overview.view", true),
                },
            };

            public override Task<string?> ExecuteAsync(JobContext job) => Task.FromResult<string?>(null);

            public override IReadOnlyList<ITactic> Tactics => new ITactic[] { new TwoStage(this), new MinorOnly() };

            private sealed class TwoStage : TacticBase
            {
                private readonly TestTactics owner;
                public TwoStage(TestTactics owner) => this.owner = owner;

                public override TacticDescriptor Descriptor { get; } = new()
                {
                    Key = "test.two_stage", Code = "T1", Title = "Two stages", Category = TacticCategory.Inquiry,
                    Accepts = new[] { SelectionKind.Person }, MinSelections = 0, MaxSelections = 1,
                    Outputs = new[] { TacticOutputKind.Answer }, RequiredPermission = "omniscience.overview.view",
                };

                public override async Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context)
                {
                    int a = await context.StageAsync("one", _ => { Interlocked.Increment(ref owner.StageOneRuns); return Task.FromResult(41); });
                    int b = await context.StageAsync("two", async ct => { await owner.Release.Task.WaitAsync(ct); return a + 1; });
                    return new[] { new TacticOutput(TacticOutputKind.Answer, Data: Json(new { value = b, asOf = context.AsOfMs })) };
                }
            }

            private sealed class MinorOnly : TacticBase
            {
                public override TacticDescriptor Descriptor { get; } = new()
                {
                    Key = "test.replica_like", Code = "T2", Title = "Replica-like", Category = TacticCategory.Social,
                    Accepts = new[] { SelectionKind.Person }, MinSelections = 1, MaxSelections = 1,
                    Outputs = new[] { TacticOutputKind.Report }, RequiredPermission = "omniscience.replica.read",
                };

                public override Task<IReadOnlyList<TacticOutput>> RunAsync(TacticRunContext context) =>
                    Task.FromResult<IReadOnlyList<TacticOutput>>(new[] { new TacticOutput(TacticOutputKind.Report, Summary: "ok") });
            }
        }

        private static async Task Until(Func<bool> condition, int timeoutMs = 15_000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!condition())
            {
                if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condition not met in time.");
                await Task.Delay(15);
            }
        }

        /// <summary>
        /// With a fixed clock a schedule's first due time is set on the kernel's first planning pass, which may come after
        /// a jump; nudging the clock a minute at a time until the condition holds makes schedule tests deterministic.
        /// </summary>
        private async Task AdvanceUntil(Func<bool> condition, int timeoutMs = 20_000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!condition())
            {
                if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condition not met in time.");
                clock.Advance(TimeSpan.FromSeconds(61));
                await Task.Delay(100);
            }
        }

        private async Task<TacticRunRecord> Finished(string runId)
        {
            await Until(() => rt.Tactics.Get(runId) is { } r && TacticRunStates.IsTerminal(r.State));
            return rt.Tactics.Get(runId)!;
        }

        private static JsonElement Params(object p) => JsonSerializer.SerializeToElement(p);

        private static Selection People(params string[] ids) => Selection.Of(SelectionKind.Person, ids);

        // ── set algebra ──────────────────────────────────────────────────────────

        [Fact]
        public void SetAlgebra_CombinesThroughMergesAndKeepsScoresSensible()
        {
            Func<string, string> canonical = id => id == "p-b" ? "p-a" : id;
            var a = new Selection(SelectionKind.Person, new[] { new SelectionItem("p-a", 0.2), new SelectionItem("p-c", 0.9) });
            var b = new Selection(SelectionKind.Person, new[] { new SelectionItem("p-b", 0.7), new SelectionItem("p-d", 0.1) });
            var union = SetAlgebra.Combine(a, b, SetOperation.Union, canonical);
            Assert.Equal(new[] { "p-a", "p-c", "p-d" }, union.Items.Select(i => i.Id).OrderBy(x => x));
            Assert.Equal(0.7, union.Items.Single(i => i.Id == "p-a").Score);
            var inter = SetAlgebra.Combine(a, b, SetOperation.Intersect, canonical);
            Assert.Equal("p-a", Assert.Single(inter.Items).Id);
            Assert.Equal(0.2, inter.Items[0].Score);
            var diff = SetAlgebra.Combine(a, b, SetOperation.Difference, canonical);
            Assert.Equal("p-c", Assert.Single(diff.Items).Id);
            Assert.Equal(new[] { "p-c", "p-a" }, SetAlgebra.TopK(a, 2).Items.Select(i => i.Id));
            Assert.Throws<ArgumentException>(() => SetAlgebra.Combine(a, Selection.Of(SelectionKind.Message, new[] { "m" }), SetOperation.Union));
        }

        [Fact]
        public void SetAlgebra_TreatsTimeRangesAsIntervalSets()
        {
            var a = new Selection(SelectionKind.TimeRange, new[] { new SelectionItem(Selection.TimeRangeId(0, 100)), new SelectionItem(Selection.TimeRangeId(200, 300)) });
            var b = Selection.Window(50, 250);
            Assert.Equal(new[] { "0..300" }, SetAlgebra.Combine(a, b, SetOperation.Union).Items.Select(i => i.Id));
            Assert.Equal(new[] { "50..100", "200..250" }, SetAlgebra.Combine(a, b, SetOperation.Intersect).Items.Select(i => i.Id));
            Assert.Equal(new[] { "0..50", "250..300" }, SetAlgebra.Combine(a, b, SetOperation.Difference).Items.Select(i => i.Id));
        }

        [Fact]
        public void Stratify_KeepsEveryStratumAndDisclosesSampling()
        {
            var items = Enumerable.Range(0, 1000).Select(i => new SelectionItem($"m{i}", Label: i < 900 ? "busy" : "quiet")).ToList();
            var s = SetAlgebra.Stratify(new Selection(SelectionKind.Message, items), 100, i => i.Label!);
            Assert.Equal(100, s.Count);
            Assert.True(s.Sampled);
            Assert.Equal(1000, s.TotalBeforeSampling);
            Assert.Equal(10, s.Items.Count(i => i.Label == "quiet"));
            Assert.Equal(s.Items.Select(i => i.Id), SetAlgebra.Stratify(new Selection(SelectionKind.Message, items), 100, i => i.Label!).Items.Select(i => i.Id));
        }

        // ── read models ──────────────────────────────────────────────────────────

        [Fact]
        public async Task ReadModels_VersionOnlyOnRealChangesAndAnnounceThem()
        {
            var store = rt.Host.ReadModels;
            var seen = new List<string>();
            using var sub = rt.Host.Events.Subscribe(ev => { if (ev.Topic == Topics.ReadModelUpdated) seen.Add(ev.PayloadJson); return ValueTask.CompletedTask; });
            var w1 = await store.PutAsync("test.card", "p-a", 1, 100, 5, new { name = "A" });
            var w2 = await store.PutAsync("test.card", "p-a", 1, 200, 6, new { name = "A" });
            var w3 = await store.PutAsync("test.card", "p-a", 1, 300, 7, new { name = "A2" });
            Assert.Equal((1L, true), (w1.Version, w1.Changed));
            Assert.Equal((1L, false), (w2.Version, w2.Changed));
            Assert.Equal((2L, true), (w3.Version, w3.Changed));
            Assert.Equal(2, seen.Count);
            var row = store.Get("test.card", "p-a")!;
            Assert.Equal(300, row.AsOfMs);
            Assert.Equal(7, row.SourceSeq);
            Assert.Contains("\"body\":{\"name\":\"A2\"}", DataRoutes.Envelope(row));
            await store.PutAsync("test.card", "p-b", 1, 0, 0, new { name = "B" });
            await store.PutAsync("test.other", "p-a", 1, 0, 0, new { });
            Assert.Equal(new[] { "p-a", "p-b" }, store.List("test.card", "p-", null, 10).Select(r => r.Key));
            Assert.Equal(new[] { "p-b" }, store.List("test.card", null, "p-a", 10).Select(r => r.Key));
            Assert.True(await store.DeleteAsync("test.card", "p-b"));
            Assert.Null(store.Get("test.card", "p-b"));
        }

        // ── runs ─────────────────────────────────────────────────────────────────

        [Fact]
        public async Task SetAlgebraTactic_RunsAsAJobAndChainsItsOutput()
        {
            string runId = await rt.Tactics.StartAsync(SetAlgebraTactic.Key,
                new TacticInput(new[] { People("p-a", "p-b"), People("p-c") }, Params(new { op = "union" })), "tester");
            var run = await Finished(runId);
            Assert.Equal(TacticRunStates.Succeeded, run.State);
            var outputs = JsonSerializer.Deserialize<List<TacticOutput>>(run.OutputsJson!, KernelJson.Options)!;
            var cohort = outputs.Single().Selection!;
            Assert.Equal(new[] { "p-a", "p-c" }, cohort.Items.Select(i => i.Id).OrderBy(x => x));

            // The output feeds straight into another tactic.
            string saved = await rt.Tactics.StartAsync(CohortBuilderTactic.Key, new TacticInput(new[] { cohort }, Params(new { name = "Test friends" })), "tester");
            Assert.Equal(TacticRunStates.Succeeded, (await Finished(saved)).State);
            var stored = Assert.Single(Cohorts.List(rt.Stores));
            Assert.Equal("Test friends", stored.Name);
            Assert.Equal(new[] { "p-a", "p-c" }, stored.Members);
        }

        [Fact]
        public async Task Start_RefusesBadShapesAndInvalidParameters_ButAllowsMinors()
        {
            var tooMany = await Assert.ThrowsAsync<TacticRefusedException>(() => rt.Tactics.StartAsync(SetAlgebraTactic.Key,
                new TacticInput(new[] { People("p-a"), People("p-b"), People("p-c") }, Params(new { op = "union" })), "t"));
            Assert.Contains("at most 2", tooMany.Message);
            var wrongKind = await Assert.ThrowsAsync<TacticRefusedException>(() => rt.Tactics.StartAsync(CohortBuilderTactic.Key,
                new TacticInput(new[] { Selection.Of(SelectionKind.Message, new[] { "discord:1" }) }, Params(new { name = "x" })), "t"));
            Assert.Contains("does not accept a Message", wrongKind.Message);
            await Assert.ThrowsAsync<TacticRefusedException>(() => rt.Tactics.StartAsync(SetAlgebraTactic.Key, new TacticInput(new[] { People("p-a") }, Params(new { op = "explode" })), "t"));
            Assert.Equal(TacticRunStates.Succeeded, (await Finished(await rt.Tactics.StartAsync("test.replica_like", new TacticInput(new[] { People("p-m") }), "t"))).State);
            Assert.Equal(TacticRunStates.Succeeded, (await Finished(await rt.Tactics.StartAsync("test.replica_like", new TacticInput(new[] { People("p-c") }), "t"))).State);
            await Assert.ThrowsAsync<KeyNotFoundException>(() => rt.Tactics.StartAsync("nope", new TacticInput(Array.Empty<Selection>()), "t"));
        }

        [Fact]
        public async Task PausedRun_ResumesWithoutRedoingFinishedStages()
        {
            string runId = await rt.Tactics.StartAsync("test.two_stage", new TacticInput(Array.Empty<Selection>()), "tester");
            await Until(() => rt.Tactics.Get(runId)?.CurrentStage == "one");
            Assert.True(await rt.Tactics.PauseAsync(runId));
            await Until(() => rt.Tactics.Get(runId)!.State == TacticRunStates.Paused);
            Assert.Equal(1, tactics.StageOneRuns);

            tactics.Release.TrySetResult();
            Assert.True(await rt.Tactics.ResumeAsync(runId));
            var run = await Finished(runId);
            Assert.Equal(TacticRunStates.Succeeded, run.State);
            Assert.Equal(1, tactics.StageOneRuns); // stage one came from the checkpoint
            Assert.Contains("\"value\":42", run.OutputsJson);
        }

        [Fact]
        public async Task CancelledRun_StopsPromptlyAndStaysCancelled()
        {
            string runId = await rt.Tactics.StartAsync("test.two_stage", new TacticInput(Array.Empty<Selection>()), "tester");
            await Until(() => rt.Tactics.Get(runId)?.CurrentStage == "one");
            Assert.True(await rt.Tactics.CancelAsync(runId, "tester"));
            var run = await Finished(runId);
            Assert.Equal(TacticRunStates.Cancelled, run.State);
            Assert.False(await rt.Tactics.CancelAsync(runId, "tester"));
            await Until(() => rt.Host.Jobs.Get(run.JobId!.Value)!.State == JobStates.Cancelled);
        }

        [Fact]
        public async Task Rerun_UsesNewParametersAndRecordsItsParent()
        {
            string first = await rt.Tactics.StartAsync(SetAlgebraTactic.Key, new TacticInput(new[] { People("p-c", "p-a") }, Params(new { op = "sort", order = "Id" })), "tester");
            await Finished(first);
            string second = await rt.Tactics.RerunAsync(first, "tester", Params(new { op = "topk", k = 1 }));
            var run = await Finished(second);
            Assert.Equal(first, run.ParentRunId);
            Assert.Equal(TacticRunStates.Succeeded, run.State);
            Assert.Single(JsonSerializer.Deserialize<List<TacticOutput>>(run.OutputsJson!, KernelJson.Options)!.Single().Selection!.Items);
        }

        [Fact]
        public async Task Runs_GoStaleWhenTheirSubjectsChange()
        {
            string runId = await rt.Tactics.StartAsync(SetAlgebraTactic.Key, new TacticInput(new[] { People("p-c") }, Params(new { op = "sort" })), "tester");
            var run = await Finished(runId);
            Assert.False(rt.Tactics.IsStale(run));
            await rt.Stores.Corpus.WriteAsync((c, tx) => ChangeLog.Append(c, tx, clock.NowMs, ChangeKinds.MessageInsert, "discord:999", "p-c", "discord:5", SourceKinds.Live));
            Assert.True(rt.Tactics.IsStale(run));
        }

        [Fact]
        public async Task RunList_PagesByKeysetAndFiltersBySubject()
        {
            var ids = new List<string>();
            for (int i = 0; i < 5; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                ids.Add(await rt.Tactics.StartAsync(SetAlgebraTactic.Key, new TacticInput(new[] { People(i % 2 == 0 ? "p-a" : "p-c") }, Params(new { op = "sort" })), "tester"));
            }
            foreach (var id in ids) await Finished(id);
            var seen = new List<string>();
            string? cursor = null;
            do
            {
                var page = rt.Tactics.List(new TacticRunQuery { Limit = 2, Cursor = cursor });
                seen.AddRange(page.Items.Select(r => r.RunId));
                cursor = page.NextCursor;
            } while (cursor != null);
            Assert.Equal(ids.AsEnumerable().Reverse(), seen);
            Assert.Equal(3, rt.Tactics.List(new TacticRunQuery { SubjectId = "p-a" }).Items.Count);
        }

        // ── playbooks ────────────────────────────────────────────────────────────

        [Fact]
        public void Playbooks_AreValidatedBeforeSaving()
        {
            var engine = rt.Tactics.Playbooks;
            var step = new PlaybookStep(SetAlgebraTactic.Key, Params(new { op = "sort" }), new PlaybookStepInput(PlaybookStepInput.FromTrigger));
            Assert.Empty(engine.Validate(new PlaybookDefinition(new[] { step }), new PlaybookTrigger(PlaybookTriggerKind.Daily, At: "10:00")));
            Assert.NotEmpty(engine.Validate(new PlaybookDefinition(Array.Empty<PlaybookStep>()), new PlaybookTrigger(PlaybookTriggerKind.Manual)));
            Assert.NotEmpty(engine.Validate(new PlaybookDefinition(new[] { step }), new PlaybookTrigger(PlaybookTriggerKind.Event, Topic: "tactic.finished")));
            Assert.NotEmpty(engine.Validate(new PlaybookDefinition(new[] { step }), new PlaybookTrigger(PlaybookTriggerKind.Daily, At: "25:00")));
            Assert.NotEmpty(engine.Validate(new PlaybookDefinition(new[] { step with { Input = new PlaybookStepInput(PlaybookStepInput.FromStep, Step: 0) } }), new PlaybookTrigger(PlaybookTriggerKind.Manual)));
            Assert.NotEmpty(engine.Validate(new PlaybookDefinition(new[] { new PlaybookStep("missing.tactic") }), new PlaybookTrigger(PlaybookTriggerKind.Manual)));
            Assert.NotEmpty(engine.Validate(new PlaybookDefinition(new[] { step }), new PlaybookTrigger(PlaybookTriggerKind.Weekly, At: "10:00")));
        }

        [Fact]
        public void Playbooks_ScheduleOnKlivesLocalTimeAcrossDst()
        {
            var london = Zones.GetOrThrow("Europe/London");
            long created = Instant.FromUtc(new DateTime(2024, 3, 30, 12, 0, 0, DateTimeKind.Utc)); // the day before the clocks go forward
            var daily = new Playbook("p", "p", new PlaybookDefinition(Array.Empty<PlaybookStep>()), new PlaybookTrigger(PlaybookTriggerKind.Daily, At: "10:00"), true, null, created, created);
            long next = PlaybookEngine.NextDue(daily, london)!.Value;
            Assert.Equal(Instant.FromUtc(new DateTime(2024, 3, 31, 9, 0, 0, DateTimeKind.Utc)), next); // 10:00 BST
            var weekly = daily with { Trigger = new PlaybookTrigger(PlaybookTriggerKind.Weekly, At: "10:00", Days: new[] { DayOfWeek.Wednesday }) };
            Assert.Equal(Instant.FromUtc(new DateTime(2024, 4, 3, 9, 0, 0, DateTimeKind.Utc)), PlaybookEngine.NextDue(weekly, london)!.Value);
        }

        [Fact]
        public async Task EventPlaybook_FiresOnAMatchingEventAndChainsSteps()
        {
            var engine = rt.Tactics.Playbooks;
            var playbook = await engine.SaveAsync(null, "When p-c speaks", new PlaybookDefinition(new[]
            {
                new PlaybookStep(SetAlgebraTactic.Key, Params(new { op = "sort" }), new PlaybookStepInput(PlaybookStepInput.FromTrigger)),
                new PlaybookStep(CohortBuilderTactic.Key, Params(new { name = "From playbook" }), new PlaybookStepInput(PlaybookStepInput.FromStep, Step: 0)),
            }), new PlaybookTrigger(PlaybookTriggerKind.Event, Topic: "live.message", Match: new Dictionary<string, string> { ["personId"] = "p-c" }), true, "tester");

            await rt.Host.Events.PublishAsync("live.message", new { personId = "p-a" });
            await rt.Host.Events.PublishAsync("live.message", new { personId = "p-c" });
            await AdvanceUntil(() => engine.Runs(playbook.Id).Any(r => r.State is "succeeded" or "failed")); // playbook ticks come due
            await Task.Delay(300);
            var run = Assert.Single(engine.Runs(playbook.Id)); // one matching event, one run
            Assert.Equal("succeeded", run.State);
            Assert.Equal(new[] { "p-c" }, Assert.Single(Cohorts.List(rt.Stores)).Members);
            Assert.Equal(2, rt.Tactics.List(new TacticRunQuery { PlaybookRunId = run.PlaybookRunId }).Items.Count);
        }

        [Fact]
        public async Task ScheduledPlaybook_MissedRunsFireOnceLate()
        {
            var engine = rt.Tactics.Playbooks;
            var playbook = await engine.SaveAsync(null, "Daily sort", new PlaybookDefinition(new[]
            {
                new PlaybookStep(SetAlgebraTactic.Key, Params(new { op = "sort" }), new PlaybookStepInput(PlaybookStepInput.FromLiteral, Selections: new[] { People("p-a") })),
            }), new PlaybookTrigger(PlaybookTriggerKind.Daily, At: "10:00"), true, "tester");
            clock.Advance(TimeSpan.FromDays(3)); // three 10:00s pass while "down"
            await AdvanceUntil(() => engine.Runs(playbook.Id).Any(r => r.State == "succeeded"));
            await Task.Delay(300);
            Assert.Single(engine.Runs(playbook.Id)); // three missed 10:00s → one late run
            long next = PlaybookEngine.NextDue(engine.Get(playbook.Id)!, rt.Host.Settings.KlivesZone)!.Value;
            Assert.True(next > clock.NowMs, "nothing further is due until the next 10:00");
            Assert.Equal(10, Zones.ToLocal(next, rt.Host.Settings.KlivesZone).Hour);
        }

        [Fact]
        public async Task Playbooks_NeverStarveTheirStepsOfWorkers()
        {
            // One CPU worker: a playbook waiting on a CPU step must not be holding it.
            string other = Path.Combine(Path.GetTempPath(), "omniv3_tactics1_" + Guid.NewGuid().ToString("N"));
            var lease = TimeSpan.FromSeconds(3);
            var poll = TimeSpan.FromMilliseconds(25);
            await using var single = await V3Runtime.StartAsync(new V3RuntimeOptions
            {
                Paths = new StorePaths(other), Clock = clock, LlmClient = new NoLlm(), Log = _ => { }, LogError = (_, _) => { },
                Kernel = new KernelOptions
                {
                    LoopInterval = poll,
                    Lanes = new[]
                    {
                        new LaneOptions(Lane.Cpu, 1, lease, poll), new LaneOptions(Lane.Io, 1, lease, poll), new LaneOptions(Lane.LlmBackground, 1, lease, poll),
                        new LaneOptions(Lane.LlmInteractive, 1, lease, poll), new LaneOptions(Lane.Coordinator, 1, lease, poll),
                    },
                },
            });
            var engine = single.Tactics.Playbooks;
            var playbook = await engine.SaveAsync(null, "Two CPU steps", new PlaybookDefinition(new[]
            {
                new PlaybookStep(SetAlgebraTactic.Key, Params(new { op = "sort" }), new PlaybookStepInput(PlaybookStepInput.FromLiteral, Selections: new[] { People("p-a", "p-c") })),
                new PlaybookStep(SetAlgebraTactic.Key, Params(new { op = "topk", k = 1 }), new PlaybookStepInput(PlaybookStepInput.FromStep, Step: 0)),
            }), new PlaybookTrigger(PlaybookTriggerKind.Manual), true, "tester");
            string runId = await engine.StartAsync(playbook.Id, "tester");
            await Until(() => engine.GetRun(runId)?.State is "succeeded" or "failed");
            Assert.Equal("succeeded", engine.GetRun(runId)!.State);
            await single.DisposeAsync();
            try { Directory.Delete(other, true); }
            catch (IOException) { /* lingering handle on Windows; unique temp dir */ }
        }

        // ── Explore contract ─────────────────────────────────────────────────────

        [Fact]
        public void ExploreQueries_AreCheckedAgainstTheDeclaredDatasets()
        {
            var datasets = rt.Host.Modules.SelectMany(m => m.Manifest.Datasets).ToList();
            JsonElement V(object v) => JsonSerializer.SerializeToElement(v);
            var ok = new ExploreQuery("test.messages", new[] { new ExploreFilter("person.author", FilterOp.Eq, new[] { V("p-a") }) }, new[] { "person.author" }, new[] { "count" }, TimeBucket.Day);
            Assert.Null(ExploreValidation.Validate(ok, datasets));
            Assert.NotNull(ExploreValidation.Validate(ok with { Dataset = "nope" }, datasets));
            Assert.NotNull(ExploreValidation.Validate(ok with { GroupBy = new[] { "where.guild" } }, datasets));
            Assert.NotNull(ExploreValidation.Validate(ok with { Measures = new[] { "sum_x" } }, datasets));
            Assert.NotNull(ExploreValidation.Validate(ok with { Filters = new[] { new ExploreFilter("time.occurred", FilterOp.Between, new[] { V(1) }) } }, datasets));
            Assert.NotNull(ExploreValidation.Validate(ok with { Zone = "Mars/Base" }, datasets));
            Assert.Null(rt.Explore); // the engine arrives with the analytics workstream
        }

        [Fact]
        public async Task Routes_IncludeTheTacticsAndDataSurface()
        {
            var api = new Omnipotent.Services.KliveAPI.KliveAPI();
            var registrar = new RouteRegistrar(api, () => Array.Empty<object>(), (_, _) => { });
            var host = new V3Host();
            await new V3Routes(host, registrar, new EngineQueries(host, clock, () => registrar.Registered)).RegisterAsync(CancellationToken.None);
            foreach (var expected in new[]
            {
                "GET /omniscience/v3/tactics", "POST /omniscience/v3/tactics/run", "POST /omniscience/v3/tactics/estimate", "GET /omniscience/v3/tactics/runs",
                "GET /omniscience/v3/tactics/runs/get", "GET /omniscience/v3/tactics/runs/stream", "POST /omniscience/v3/tactics/runs/cancel",
                "POST /omniscience/v3/tactics/runs/pause", "POST /omniscience/v3/tactics/runs/resume", "POST /omniscience/v3/tactics/runs/rerun",
                "POST /omniscience/v3/selections/combine", "GET /omniscience/v3/cohorts", "GET /omniscience/v3/cohorts/get",
                "GET /omniscience/v3/playbooks", "POST /omniscience/v3/playbooks/save", "POST /omniscience/v3/playbooks/validate", "POST /omniscience/v3/playbooks/run",
                "GET /omniscience/v3/playbooks/runs", "GET /omniscience/v3/rm", "GET /omniscience/v3/rm/catalog", "GET /omniscience/v3/explore/datasets",
                "POST /omniscience/v3/explore/query", "POST /omniscience/v3/explore/records",
            })
                Assert.Contains(expected, registrar.Registered);
        }
    }
}
