using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Tests.OmniscienceV3.Replica
{
    /// <summary>The replica workstream's tactics X53–X65, each run as a job on the synthetic people of the fidelity suite.</summary>
    public class ReplicaSocialTests
    {
        private static TacticInput With(object? parameters, params Selection[] selections) =>
            new(selections, parameters == null ? null : JsonSerializer.SerializeToElement(parameters));

        private static Selection People(params string[] ids) => Selection.Of(SelectionKind.Person, ids);

        private static async Task<(TacticRunRecord Run, List<TacticOutput> Outputs)> RunAsync(ReplicaHarness h, string key, TacticInput input)
        {
            string runId = await h.Runtime.Tactics.StartAsync(key, input, "tester");
            await h.DrainAsync();
            var run = h.Runtime.Tactics.Get(runId)!;
            Assert.True(run.State == TacticRunStates.Succeeded, $"{key}: {run.State} {run.Error}");
            return (run, JsonSerializer.Deserialize<List<TacticOutput>>(run.OutputsJson!, KernelJson.Options)!);
        }

        [Fact]
        public async Task Catalogue_HoldsX53ToX65_AndEveryTacticIsWellFormed()
        {
            await using var h = await ReplicaHarness.StartAsync(seed: false);
            var mine = h.Runtime.Tactics.Catalog().Where(d => string.CompareOrdinal(d.Code, "X53") >= 0 && string.CompareOrdinal(d.Code, "X65") <= 0).ToList();
            Assert.Equal(Enumerable.Range(53, 13).Select(i => "X" + i), mine.Select(d => d.Code).OrderBy(c => c, StringComparer.Ordinal));
            foreach (var d in mine)
            {
                Assert.True(d.Accepts != null, d.Key);
                Assert.NotEmpty(d.Outputs);
                Assert.False(string.IsNullOrWhiteSpace(d.Title) || string.IsNullOrWhiteSpace(d.Description), d.Key);
                using var schema = JsonDocument.Parse(d.ParameterSchemaJson);
                Assert.Equal(JsonValueKind.Object, schema.RootElement.ValueKind);
                Assert.True(Omnipotent.Profiles.Permissions.PermissionCatalog.Get(d.RequiredPermission) != null, $"{d.Key} names the unknown permission '{d.RequiredPermission}'");
                Assert.False(d.Outbound, d.Key + ": nothing in this workstream sends anything to anyone");
            }
        }

        [Fact]
        public async Task EvidenceTactics_RunAsJobsOnTheSeededPeople_AndReturnAReport()
        {
            await using var h = await ReplicaHarness.StartAsync();
            var problems = new List<string>();
            async Task Try(string key, TacticInput input, Action<List<TacticOutput>>? check = null)
            {
                try
                {
                    var (_, outputs) = await RunAsync(h, key, input);
                    if (outputs.Count == 0 || outputs.All(o => o.Data == null && o.Selection == null)) problems.Add(key + ": no output");
                    check?.Invoke(outputs);
                }
                catch (Exception ex) when (ex is Xunit.Sdk.XunitException or TacticRefusedException or KeyNotFoundException)
                {
                    problems.Add(key + ": " + ex.Message);
                }
            }

            await Try("x53.best_time", With(new { days = 400 }, People(Personas.Lena)));
            await Try("x54.conversation_starters", With(new { days = 120, count = 3 }, People(Personas.Lena)));
            await Try("x56.tone_check", With(new { draft = "fancy a game on Saturday? no pressure" }, People(Personas.Lena)));
            await Try("x58.gift_finder", With(new { budget = "£30", occasion = "birthday" }, People(Personas.Lena)));
            await Try("x59.invite_planner", With(new { @event = "board game night on Saturday" }, People(Personas.Lena, Personas.Arthur, Personas.Sofia)));
            await Try("x60.reconnect", With(new { count = 5, minPastMessages = 5 }));
            await Try("x61.meeting_prep", With(new { purpose = "catch up", days = 120 }, People(Personas.Lena)));
            await Try("x62.follow_through", With(new { days = 120 }));
            await Try("x64.who_can_help", With(new { need = "someone who knows about games", count = 3 }));
            await Try("x65.mediation", With(null, Selection.Of(SelectionKind.Conversation, new[] { Personas.Group })));
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }

        [Fact]
        public async Task ReplicaTactics_RehearseDraftAndSimulate_OnTrainedReplicas_AndEveryLineIsLabelledASimulation()
        {
            await using var h = await ReplicaHarness.StartAsync();
            await h.TrainAsync(Personas.Lena);
            await h.TrainAsync(Personas.Arthur);
            await h.TrainAsync(Personas.Self);
            var problems = new List<string>();
            var all = new List<(string Key, List<TacticOutput> Outputs, string Json)>();
            async Task Try(string key, TacticInput input)
            {
                try
                {
                    var (run, outputs) = await RunAsync(h, key, input);
                    if (outputs.Count == 0) problems.Add(key + ": no output");
                    all.Add((key, outputs, run.OutputsJson!));
                }
                catch (Exception ex) when (ex is Xunit.Sdk.XunitException or TacticRefusedException or KeyNotFoundException)
                {
                    problems.Add(key + ": " + ex.Message);
                }
            }
            await Try("x55.rehearsal", With(new { draft = "hey, are you free on Saturday for a game?", samples = 3 }, People(Personas.Lena)));
            await Try("x57.draft_reply", With(new { count = 2, intent = "say yes to Saturday" }, Selection.Of(SelectionKind.Conversation, new[] { Personas.DmLena })));
            await Try("x63.group_simulation", With(new { topic = "where to go on Saturday", turns = 4 }, People(Personas.Lena, Personas.Arthur)));
            Assert.True(problems.Count == 0, string.Join("\n", problems));
            // Whatever a replica "says" is marked in the output data itself, so it can never be mistaken for a message:
            // another person's replica as a simulation, a draft in Klives's own voice as generated and not sent.
            foreach (var (key, outputs, json) in all)
            {
                var data = outputs.First(o => o.Data != null).Data!.Value;
                if (key == "x57.draft_reply")
                {
                    Assert.True(data.GetProperty("generated").GetBoolean());
                    Assert.Contains("Nothing has been or will be sent", data.GetProperty("note").GetString());
                    Assert.Equal(TacticOutputKind.ActionProposal, outputs[0].Kind);
                    Assert.NotEmpty(data.GetProperty("suggestions").EnumerateArray());
                }
                else
                {
                    Assert.True(data.GetProperty("simulated").GetBoolean(), key + " does not mark its output as a simulation");
                    Assert.True(json.Contains("simulat", StringComparison.OrdinalIgnoreCase));
                }
            }
            // Nothing was written to the corpus, and nothing was sent: these tactics only produce text for Klives to read.
            using var c = h.Runtime.Stores.Corpus.OpenRead();
            Assert.Equal(0L, c.Scalar<long>(null, "SELECT COUNT(*) FROM messages WHERE first_source_kind NOT IN ('import','live','backfill')"));
        }

        [Fact]
        public async Task Minors_CanRunTheReplicaAndRoutineTactics()
        {
            await using var h = await ReplicaHarness.StartAsync();
            await h.Runtime.Stores.Operator.WriteAsync((c, tx) => c.Exec(tx,
                "INSERT INTO person_settings(person_id, minor_marked, updated_at_ms) VALUES($p,1,$t) ON CONFLICT(person_id) DO UPDATE SET minor_marked=1", ("$p", Personas.Lena), ("$t", h.Clock.NowMs)));
            foreach (var (key, input) in new[]
                     {
                         ("x53.best_time", With(null, People(Personas.Lena))),
                         ("x54.conversation_starters", With(null, People(Personas.Lena))),
                         ("x55.rehearsal", With(new { draft = "hello" }, People(Personas.Lena))),
                         ("x58.gift_finder", With(null, People(Personas.Lena))),
                         ("x59.invite_planner", With(new { @event = "a party" }, People(Personas.Lena, Personas.Arthur))),
                         ("x61.meeting_prep", With(null, People(Personas.Lena))),
                         ("x63.group_simulation", With(new { topic = "plans" }, People(Personas.Lena, Personas.Arthur))),
                     })
                Assert.False(string.IsNullOrEmpty(await h.Runtime.Tactics.StartAsync(key, input, "tester")), key);
        }
    }
}
