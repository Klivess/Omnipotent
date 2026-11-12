using Omnipotent.Services.KliveAgent;
using Omnipotent.Services.ServiceTools;
using AgentService = Omnipotent.Services.KliveAgent.KliveAgent;

namespace Omnipotent.Tests.KliveAgent
{
    /// <summary>What keeps a long unattended run moving: a tool that hangs is bounded, a guessed route is
    /// answered with the real ones, and the task the run was given is protected from compaction.</summary>
    public class KliveAgentLongRunTests
    {
        // ── tool deadlines (live: one call ran 11,740 s, another 7,303 s) ──

        [Fact]
        public async Task ToolThatOverrunsItsDeadline_IsReported_NotWaitedFor()
        {
            var never = new TaskCompletionSource<(bool ok, string output)>();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var (ok, output) = await KliveAgentBrain.WithToolDeadlineAsync(never.Task, "omnitrader_research",
                TimeSpan.FromMilliseconds(150), CancellationToken.None);
            Assert.False(ok);
            Assert.True(timer.ElapsedMilliseconds < 5000);
            Assert.Contains("omnitrader_research was still running", output);
            Assert.Contains(KliveAgentBrain.ToolDeadlineMarker, output);
            Assert.Contains("wait_for", output);
        }

        [Fact]
        public async Task ToolThatFinishesInTime_ReturnsItsOwnResult()
        {
            var (ok, output) = await KliveAgentBrain.WithToolDeadlineAsync(Task.FromResult((true, "done")), "grep",
                TimeSpan.FromSeconds(30), CancellationToken.None);
            Assert.True(ok);
            Assert.Equal("done", output);
        }

        [Fact]
        public async Task StoppedRun_EndsTheWaitAtOnce()
        {
            var never = new TaskCompletionSource<(bool ok, string output)>();
            using var stop = new CancellationTokenSource(100);
            var (ok, output) = await KliveAgentBrain.WithToolDeadlineAsync(never.Task, "grep", TimeSpan.FromMinutes(5), stop.Token);
            Assert.False(ok);
            Assert.Contains("stopped", output);
        }

        [Theory]
        [InlineData("omnitrader_research", 600, 600)]
        [InlineData("grep", 0, 0)]                       // 0 turns the limit off
        [InlineData("klivemail_wait_for_email", 600, 0)] // waits on purpose and bounds itself
        public void Deadline_AppliesToOrdinaryToolsOnly(string tool, int setting, int expectedSeconds)
        {
            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), KliveAgentBrain.ToolDeadlineFor(tool, setting));
        }

        [Theory]
        [InlineData("{\"op\":\"evaluate\",\"id\":\"x\"}", "evaluate")]
        [InlineData("{\"op\":\"call\",\"service\":\"OmniTrader\",\"method\":\"Run\"}", "call OmniTrader Run")]
        [InlineData("{\"method\":\"GET\",\"path\":\"/api/omnitrader/status\"}", "GET /api/omnitrader/status")]
        [InlineData("{\"query\":\"abc\"}", null)]
        [InlineData("not json", null)]
        public void SlowTool_IsNamedByItsOperation(string arguments, string? expected)
        {
            Assert.Equal(expected, KliveAgentBrain.ToolOperationOf(arguments));
        }

        [Fact]
        public void SlowTools_AreKeptWithTheStatistics()
        {
            var stats = new KliveAgentStats(Path.Combine(Path.GetTempPath(), "kliveagent-stats-" + Guid.NewGuid().ToString("N") + ".json"));
            stats.RecordSlowTool("omnitrader_research", "evaluate", 600_000, timedOut: true);
            stats.RecordSlowTool("web_fetch", null, 75_000, timedOut: false);
            var view = Newtonsoft.Json.Linq.JObject.FromObject(stats.BuildScriptHealth(7));
            var slow = (Newtonsoft.Json.Linq.JArray)view["slowTools"]!;
            Assert.Equal(2, slow.Count);
            Assert.Equal("web_fetch", (string)slow[0]!["tool"]!);
            Assert.True((bool)slow[1]!["timedOut"]!);
            Assert.Equal("evaluate", (string)slow[1]!["operation"]!);
        }

        // ── a guessed route (live: three 404s in a row for /omnitrader/…) ──

        [Fact]
        public void GuessedRoute_IsAnsweredWithTheRegisteredOnesNearIt()
        {
            var registered = new[]
            {
                "/api/omnitrader/status", "/api/omnitrader/firm/overview", "/api/omnitrader/firm/positions",
                "/api/omnitrader/firm/reconciliation", "/kliveagent/status", "/projects/overview", "/omniscience/stats/overview",
            };
            Assert.Equal("/api/omnitrader/status", OmniApiClient.ClosestRoutes("/omnitrader/status", registered)[0]);
            Assert.Contains("/api/omnitrader/firm/positions", OmniApiClient.ClosestRoutes("/omnitrader/positions", registered));
            Assert.Contains("/api/omnitrader/firm/reconciliation", OmniApiClient.ClosestRoutes("/omnitrader/reconciliation?x=1", registered));
            Assert.Empty(OmniApiClient.ClosestRoutes("/nothing/like/this/anywhere", registered));
        }

        // ── the task survives compaction (live: 11 compactions in 30 days, all in the longest runs) ──

        [Fact]
        public void Compaction_KeepsTheTaskMessage_WhenItsPrefixIsProtected()
        {
            string task = "TASK: " + string.Join(" ", Enumerable.Range(0, 400).Select(i => "deliver-item-" + i)) + " END-OF-TASK";
            var session = NewSession();
            session.structuredMessages.Add(new Omnipotent.Services.KliveLLM.HFWrapper.HFMessage { role = "system", content = "system prompt" });
            session.structuredMessages.Add(new Omnipotent.Services.KliveLLM.HFWrapper.HFMessage { role = "user", content = task });
            for (int i = 0; i < 60; i++)
            {
                session.structuredMessages.Add(new Omnipotent.Services.KliveLLM.HFWrapper.HFMessage { role = "assistant", content = "step " + i + " " + new string('a', 2000) });
                session.structuredMessages.Add(new Omnipotent.Services.KliveLLM.HFWrapper.HFMessage { role = "user", content = "continue " + new string('b', 2000) });
            }

            // As KliveAgent used to call it: nothing protected, the task is clipped to 240 characters.
            var unprotected = Clone(session);
            Assert.True(Omnipotent.Services.KliveLLM.KliveLLM.CompactToolSessionIfNeeded(unprotected, 20_000, 28, protectPrefixMessages: 0));
            Assert.DoesNotContain(unprotected.structuredMessages, m => (m.content as string)?.Contains("END-OF-TASK") == true);

            // As it calls it now.
            Assert.True(Omnipotent.Services.KliveLLM.KliveLLM.CompactToolSessionIfNeeded(session, 20_000, 28, protectPrefixMessages: 1));
            Assert.Equal(task, session.structuredMessages[1].content as string);
            Assert.True(session.structuredMessages.Count < 122);
        }

        // ── the run ledger ──

        [Fact]
        public void Ledger_KeepsThePlanDoneMarksAndFacts()
        {
            var ledger = new AgentRunLedger();
            Assert.True(ledger.IsEmpty);
            Assert.Contains("needs 'steps'", ledger.Apply("{\"op\":\"set_plan\"}"));

            string shown = ledger.Apply("{\"op\":\"set_plan\",\"steps\":[\"Create the mailbox\",\"Sign up\",\"Create the API key\"]}");
            Assert.Contains("Plan (0 of 3 done)", shown);
            ledger.Apply("{\"op\":\"done\",\"step\":1,\"text\":\"mailbox ready\"}");
            ledger.Apply("{\"op\":\"note\",\"text\":\"Store.Mutate takes Action<OmniTumblrState>\"}");
            ledger.Apply("{\"op\":\"note\",\"text\":\"Store.Mutate takes Action<OmniTumblrState>\"}");
            Assert.Contains("there is no step 9", ledger.Apply("{\"op\":\"done\",\"step\":9}"));

            string rendered = ledger.Render();
            Assert.Contains("Plan (1 of 3 done)", rendered);
            Assert.Contains("1. [x] Create the mailbox — mailbox ready", rendered);
            Assert.Contains("2. [ ] Sign up", rendered);
            Assert.Single(ledger.Facts);

            // Re-planning keeps what is already done.
            ledger.Apply("{\"op\":\"set_plan\",\"steps\":[\"Create the mailbox\",\"Sign up\",\"Verify the email\",\"Create the API key\"]}");
            Assert.True(ledger.Steps[0].Done);
            Assert.Equal(4, ledger.Steps.Count);

            // It round-trips through the run record.
            var restored = Newtonsoft.Json.JsonConvert.DeserializeObject<AgentRunLedger>(Newtonsoft.Json.JsonConvert.SerializeObject(ledger.Copy()))!;
            Assert.Equal(ledger.Render(), restored.Render());
            Assert.Contains("not an operation", ledger.Apply("{\"op\":\"erase\"}"));
        }

        [Fact]
        public void Ledger_IsBounded()
        {
            var ledger = new AgentRunLedger();
            for (int i = 0; i < 100; i++) ledger.AddFact("fact " + i + " " + new string('x', 1000));
            Assert.Equal(AgentRunLedger.MaxFacts, ledger.Facts.Count);
            Assert.All(ledger.Facts, fact => Assert.True(fact.Length <= AgentRunLedger.MaxEntryChars));
            Assert.Contains("fact 99", ledger.Facts[^1]);
            ledger.SetPlan(Enumerable.Range(0, 100).Select(i => "step " + i));
            Assert.Equal(AgentRunLedger.MaxSteps, ledger.Steps.Count);
        }

        [Fact]
        public void TaskLedger_IsOfferedAsATool_AndMakesTheRunATask()
        {
            Assert.Contains(KliveAgentBrain.BuildToolDefinitions(), t => t.function.name == "task_ledger");
            Assert.True(KliveAgentBrain.IsTaskTool("task_ledger"));
        }

        // ── continuing after a restart ──

        [Fact]
        public void Continuation_CarriesTheWholeTaskTheLedgerAndTheLastScripts()
        {
            string brief = "Long brief. " + string.Join(" ", Enumerable.Range(0, 3000).Select(i => "requirement-" + i)) + " FINAL-REQUIREMENT";
            Assert.True(brief.Length > 30_000);
            var ledger = new AgentRunLedger();
            ledger.SetPlan(new[] { "Read the papers", "Build two strategies" });
            ledger.MarkDone(1);
            ledger.AddFact("candidates are submitted with omnitrader_research op=submit");

            var run = new Omnipotent.Services.KliveAgent.Models.AgentPendingChatResponse
            {
                UserMessage = brief,
                Ledger = ledger.Copy(),
                ScriptsExecuted = new List<Omnipotent.Services.KliveAgent.Models.AgentScriptResult>
                {
                    new() { Code = "var papers = await LoadAsync();\nLog(papers.Count);", Success = true, Output = "3" },
                    new() { Code = "Log(missing.Name);", Success = false, ErrorMessage = "Compilation failed\n[CS0103] line 1:5 — The name 'missing' does not exist" },
                },
            };

            string message = AgentService.BuildContinuationMessage(run);
            Assert.Contains("FINAL-REQUIREMENT", message);       // was cut at 4,000 characters
            Assert.Contains("1. [x] Read the papers", message);
            Assert.Contains("op=submit", message);
            Assert.Contains("OK: var papers = await LoadAsync(); Log(papers.Count);", message);
            Assert.Contains("FAILED: Log(missing.Name);", message);
            Assert.Contains("variables are gone with the restart", message);
        }

        [Fact]
        public void Continuations_StopAfterTwoThatGotNowhere_NotAfterTwoInTotal()
        {
            var original = new Omnipotent.Services.KliveAgent.Models.AgentPendingChatResponse { Iteration = 30 };
            Assert.Equal(0, AgentService.NextResumeCount(null));
            Assert.Equal(1, AgentService.NextResumeCount(original));

            // A continuation that worked before the next restart does not use up the allowance…
            var worked = new Omnipotent.Services.KliveAgent.Models.AgentPendingChatResponse { ResumeCount = 1, Iteration = 12 };
            Assert.True(AgentService.MadeProgress(worked));
            Assert.Equal(1, AgentService.NextResumeCount(worked));

            // …one that died before doing anything does, and the second such ends the chain.
            var stillborn = new Omnipotent.Services.KliveAgent.Models.AgentPendingChatResponse { ResumeCount = 1, Iteration = 1 };
            Assert.False(AgentService.MadeProgress(stillborn));
            Assert.Equal(AgentService.MaxAutoResumes, AgentService.NextResumeCount(stillborn));
        }

        // ── reasoning effort ──

        [Theory]
        [InlineData(0, 0, true, "low")]
        [InlineData(6, 0, true, "medium")]
        [InlineData(12, 0, true, "high")]
        [InlineData(40, 0, false, "low")]      // "errors" mode: a long, clean run stays cheap
        [InlineData(40, 1, false, "medium")]
        [InlineData(3, 2, false, "high")]
        public void ThinkingLevel_RisesWithErrors_AndWithStepsOnlyWhenAskedTo(int step, int errorSteps, bool bySteps, string expected)
        {
            Assert.Equal(expected, KliveAgentBrain.ThinkingLevelFor(step, errorSteps, 0, forcedFinal: false, bySteps));
        }

        /// <summary>Compaction only touches structuredMessages; the real constructor needs a live KliveLLM service.</summary>
        private static Omnipotent.Services.KliveLLM.KliveLLM.KliveLLMSession NewSession()
        {
            var session = (Omnipotent.Services.KliveLLM.KliveLLM.KliveLLMSession)System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(typeof(Omnipotent.Services.KliveLLM.KliveLLM.KliveLLMSession));
            session.sessionId = "test";
            session.structuredMessages = new List<Omnipotent.Services.KliveLLM.HFWrapper.HFMessage>();
            return session;
        }

        private static Omnipotent.Services.KliveLLM.KliveLLM.KliveLLMSession Clone(Omnipotent.Services.KliveLLM.KliveLLM.KliveLLMSession source)
        {
            var copy = NewSession();
            copy.structuredMessages.AddRange(source.structuredMessages);
            return copy;
        }
    }
}
