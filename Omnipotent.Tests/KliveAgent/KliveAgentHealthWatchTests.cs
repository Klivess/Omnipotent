using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveAgent;
using Omnipotent.Services.KliveAgent.Models;
using Omnipotent.Services.KliveLLM;
using LlmService = Omnipotent.Services.KliveLLM.KliveLLM;

namespace Omnipotent.Tests.KliveAgent
{
    /// <summary>
    /// The two rolling checks that tell Klives when scripts start failing more, or the provider is slow
    /// (live, Oct to Nov 2026: failures rose from 36% to 46% and six days averaged over 50 s a call, and
    /// neither was said anywhere), and the result line that survives context compaction.
    /// </summary>
    public class KliveAgentHealthWatchTests
    {
        private static string NewPath() =>
            Path.Combine(Path.GetTempPath(), "kliveagent-stats-" + Guid.NewGuid().ToString("N") + ".json");

        private static (KliveAgentStats Stats, List<KliveAgentHealthAlert> Alerts, Action<TimeSpan> Advance) NewWatch(string? path = null)
        {
            var alerts = new List<KliveAgentHealthAlert>();
            var now = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
            var stats = new KliveAgentStats(path ?? NewPath()) { Alert = alerts.Add };
            stats.WatchClock = () => now;
            return (stats, alerts, span => now += span);
        }

        private static AgentScriptResult Ran() => new() { Code = "x", Success = true, CompileMs = 20, RunMs = 5 };

        private static AgentScriptResult Failed(string error = "Compilation failed — 1 error(s); nothing ran.\n[CS1061] line 1:5 — 'Foo' does not contain a definition for 'Bar'",
            string kind = "compile") => new() { Code = "x", Success = false, FailureKind = kind, ErrorMessage = error, CompileMs = 20 };

        [Fact]
        public void ScriptAlert_SpeaksOnceWhenTheLastFiftyFallBelowTheFloor_ThenWaitsADay()
        {
            var (stats, alerts, advance) = NewWatch();

            // 29 ran and 20 failed: 49 scripts is not yet the window, whatever the rate.
            for (int i = 0; i < 29; i++) stats.RecordScriptOutcome(Ran());
            for (int i = 0; i < 20; i++) stats.RecordScriptOutcome(Failed());
            Assert.Empty(alerts);

            // The fiftieth makes it 29 of 50 (58%), under the 60% floor.
            stats.RecordScriptOutcome(Failed("Runtime error: NullReferenceException: Object reference not set.", "runtime"));
            var alert = Assert.Single(alerts);
            Assert.Equal("scripts", alert.Kind);
            Assert.Contains("21 of the last 50 did not run", alert.Message);
            Assert.Contains("CS1061 ×20", alert.Message);

            // Still failing an hour later: said once already.
            advance(TimeSpan.FromHours(1));
            for (int i = 0; i < 30; i++) stats.RecordScriptOutcome(Failed());
            Assert.Single(alerts);

            // And still failing the next day: said again.
            advance(TimeSpan.FromHours(24));
            stats.RecordScriptOutcome(Failed());
            Assert.Equal(2, alerts.Count);

            var watch = (JObject)JObject.FromObject(stats.BuildScriptHealth(7))["watch"]!["scripts"]!;
            Assert.Equal(50, (int)watch["sampled"]!);
            Assert.True((bool)watch["belowFloor"]!);
            Assert.NotNull(watch["lastAlertAt"]!.ToObject<DateTime?>());
        }

        [Fact]
        public void ScriptAlert_StaysQuietAtAHealthyRate_AndDoesNotCountStoppedScripts()
        {
            var (stats, alerts, _) = NewWatch();
            // 35 of 50 ran (70%). The fifty stopped scripts are nobody's failure and are not counted at all.
            for (int i = 0; i < 50; i++)
                stats.RecordScriptOutcome(new AgentScriptResult { Code = "x", Success = false, FailureKind = "stopped", ErrorMessage = "Script was stopped before it finished." });
            for (int i = 0; i < 35; i++) stats.RecordScriptOutcome(Ran());
            for (int i = 0; i < 15; i++) stats.RecordScriptOutcome(Failed());
            Assert.Empty(alerts);

            var watch = (JObject)JObject.FromObject(stats.BuildWatch())["scripts"]!;
            Assert.Equal(50, (int)watch["sampled"]!);
            Assert.Equal(15, (int)watch["failed"]!);
            Assert.Equal(70.0, (double)watch["firstPassPct"]!);
            Assert.False((bool)watch["belowFloor"]!);
        }

        [Fact]
        public void ProviderAlert_UsesTheMedianWaitOfTheLastFifteenMinutes()
        {
            var (stats, alerts, advance) = NewWatch();

            // One call that took ten minutes is one slow call, not a slow provider.
            stats.RecordModelWait(600_000);
            for (int i = 0; i < 5; i++) stats.RecordModelWait(2_000);
            Assert.Empty(alerts);
            Assert.False((bool)JObject.FromObject(stats.BuildWatch())["provider"]!["slow"]!);

            // Twenty minutes on, those have aged out. Five slow calls are not yet enough to say so…
            advance(TimeSpan.FromMinutes(20));
            for (int i = 0; i < 5; i++) stats.RecordModelWait(45_000);
            Assert.Empty(alerts);

            // …the sixth is.
            stats.RecordModelWait(52_000);
            var alert = Assert.Single(alerts);
            Assert.Equal("provider", alert.Kind);
            Assert.Contains("a median of 45 s", alert.Message);
            Assert.Contains("(6 calls)", alert.Message);

            var provider = (JObject)JObject.FromObject(stats.BuildWatch())["provider"]!;
            Assert.True((bool)provider["slow"]!);
            Assert.Equal(45_000, (long)provider["medianWaitMs"]!);

            // Still slow two hours later: within the cooldown, so nothing more is sent.
            advance(TimeSpan.FromHours(2));
            for (int i = 0; i < 8; i++) stats.RecordModelWait(60_000);
            Assert.Single(alerts);

            // Once the window is empty the page stops saying "slow".
            advance(TimeSpan.FromMinutes(16));
            provider = (JObject)JObject.FromObject(stats.BuildWatch())["provider"]!;
            Assert.Equal(0, (int)provider["calls"]!);
            Assert.False((bool)provider["slow"]!);
        }

        [Fact]
        public async Task Watch_SurvivesARestart_SoARestartDoesNotRepeatTheAlert()
        {
            string path = NewPath();
            try
            {
                var (first, firstAlerts, _) = NewWatch(path);
                for (int i = 0; i < 50; i++) first.RecordScriptOutcome(Failed());
                Assert.Single(firstAlerts);
                for (int i = 0; i < 40 && !File.Exists(path); i++) await Task.Delay(100);
                await Task.Delay(700);

                // Live: 15 process starts on 5 Nov. Each one must not send the same message again.
                var (second, secondAlerts, advance) = NewWatch(path);
                await second.InitializeAsync();
                second.RecordScriptOutcome(Failed());
                Assert.Empty(secondAlerts);
                Assert.Equal(50, (int)JObject.FromObject(second.BuildWatch())["scripts"]!["sampled"]!);

                advance(TimeSpan.FromHours(25));
                second.RecordScriptOutcome(Failed());
                Assert.Single(secondAlerts);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public void ModelCall_HasAProviderWaitOnlyOnceItHasStartedAnswering()
        {
            var performance = new KliveAgentPerformance();

            // A streamed call: queued, then waiting for the provider, then output.
            using (var streamed = new ModelStateClock(performance))
            {
                var whole = System.Diagnostics.Stopwatch.StartNew();
                streamed.Observe(KliveLLMActivityKind.Queued);
                streamed.Observe(KliveLLMActivityKind.AwaitingProvider);
                Assert.Null(streamed.ProviderWaitMs);
                Thread.Sleep(40);
                streamed.Observe(KliveLLMActivityKind.Reasoning);
                Thread.Sleep(60);
                streamed.Observe(KliveLLMActivityKind.Streaming);
                long wait = Assert.IsType<long>(streamed.ProviderWaitMs);
                // The wait is the time before the first output, not the time spent reasoning after it.
                Assert.True(wait >= 30, $"wait {wait} ms");
                Assert.True(wait <= whole.ElapsedMilliseconds - 40, $"wait {wait} ms of {whole.ElapsedMilliseconds} ms");
            }

            // A buffered call is "awaiting" for its whole reply: that is not a wait for the first byte.
            using (var buffered = new ModelStateClock(performance))
            {
                buffered.Observe(KliveLLMActivityKind.AwaitingProvider);
                Thread.Sleep(20);
                Assert.Null(buffered.ProviderWaitMs);
            }
        }

        // ── the result line that outlives compaction ──

        [Fact]
        public void ScriptResult_StartsItsOutputOnTheStatusLine()
        {
            Assert.Equal("[OK | 132ms] 41 services running, 2 stopped\nKliveAPI Running",
                KliveAgentBrain.ScriptOkText(132, "41 services running, 2 stopped\nKliveAPI Running\n"));
            Assert.Equal("[OK | 9ms] (no output)", KliveAgentBrain.ScriptOkText(9, "  \n"));

            // A compile failure leads with its first diagnostic, not with the banner every one of them shares.
            string compile = KliveAgentBrain.ScriptErrorText(77,
                "Compilation failed — 1 error(s); nothing ran. Fix the exact line(s) below:\n[CS1061] line 4:19 — 'Foo' does not contain a definition for 'Bar'\n     4 | var x = foo.Bar;");
            Assert.StartsWith("[ERROR | 77ms] [CS1061] line 4:19 — 'Foo' does not contain a definition for 'Bar'\n", compile);
            Assert.Contains("     4 | var x = foo.Bar;", compile);

            // A one-line failure is not said twice.
            Assert.Equal("[ERROR | 12ms] Runtime error: NullReferenceException: Object reference not set.",
                KliveAgentBrain.ScriptErrorText(12, "Runtime error: NullReferenceException: Object reference not set."));
            Assert.Equal("[ERROR | 3ms] Unknown error.", KliveAgentBrain.ScriptErrorText(3, null));
        }

        [Fact]
        public void Compaction_RemembersWhatEachScriptFound_NotJustThatItRan()
        {
            var session = (LlmService.KliveLLMSession)System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(typeof(LlmService.KliveLLMSession));
            session.sessionId = "test";
            session.structuredMessages = new List<HFWrapper.HFMessage>
            {
                new() { role = "system", content = "system prompt" },
                new() { role = "user", content = "the task" },
            };
            for (int i = 0; i < 60; i++)
            {
                string id = "call" + i;
                session.structuredMessages.Add(new HFWrapper.HFMessage
                {
                    role = "assistant",
                    content = "",
                    tool_calls = new List<HFWrapper.HFToolCall>
                    {
                        new() { id = id, type = "function", function = new HFWrapper.HFFunctionCall { name = "execute_csharp", arguments = "{\"code\":\"Log(Check(" + i + "));\"}" } },
                    },
                });
                string result = i % 2 == 0
                    ? KliveAgentBrain.ScriptOkText(132, $"finding-{i}: the queue holds {i} items\n" + new string('d', 2500))
                    : KliveAgentBrain.ScriptErrorText(77, $"Compilation failed — 1 error(s); nothing ran.\n[CS1061] line 1:5 — 'Queue{i}' does not contain a definition for 'Depth'\n" + new string('e', 2500));
                session.structuredMessages.Add(new HFWrapper.HFMessage { role = "tool", tool_call_id = id, name = "execute_csharp", content = result });
            }

            Assert.True(LlmService.CompactToolSessionIfNeeded(session, 20_000, 28, protectPrefixMessages: 1));
            string digest = Assert.IsType<string>(session.structuredMessages[2].content);
            Assert.Contains("[OK | 132ms] finding-0: the queue holds 0 items", digest);
            Assert.Contains("[ERROR | 77ms] [CS1061] line 1:5 — 'Queue1' does not contain a definition for 'Depth'", digest);
        }
    }
}
