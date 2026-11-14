using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveAgent;
using Omnipotent.Services.KliveAgent.Models;
using Xunit.Abstractions;

namespace Omnipotent.Tests.KliveAgent
{
    /// <summary>
    /// The chat page's long-poll of a running agent (12 Nov 2026): each wake returned the whole run, and one
    /// run of 6,535 changes made the page's polls carry 4.27 GB in four hours. The page keeps its last value for
    /// any of response / scriptsExecuted / activity / ledger a snapshot leaves out, so those it already holds
    /// are now left out. These tests play the page's merge (useAgentChat.applyPendingFields) against the server.
    /// </summary>
    public class KliveAgentRunTrimmerTests
    {
        private readonly ITestOutputHelper output;
        public KliveAgentRunTrimmerTests(ITestOutputHelper output) { this.output = output; }

        private static readonly string[] Parts = { "response", "scriptsExecuted", "activity", "ledger", "userMessage", "attachments", "steeringMessages" };

        [Fact]
        public void ALongRun_ThePageAlwaysHoldsTheServersRun_ForAFractionOfTheBytes()
        {
            var (full, replaced) = Simulate(appendActivity: false);
            var (_, appended) = Simulate(appendActivity: true);
            output.WriteLine($"bytes: whole run every poll {full:N0}, unchanged parts left out {replaced:N0}, and activity appended {appended:N0}");
            // Measured: 85,929,913 bytes whole; 29,127,169 with unchanged parts left out (2.95×); 3,363,870 once the
            // page also appends activity (25.5×).
            Assert.True(replaced * 2 < full, $"left out {replaced:N0} against {full:N0}");
            Assert.True(appended * 20 < full, $"appended {appended:N0} against {full:N0}");
        }

        [Fact]
        public void AnOldActivityLineThatChanged_SendsTheWholeList_EvenToAnAppendingPage()
        {
            // Activity is only appended to, but an old line can be masked differently later (a secret learnt since).
            var trimmer = new KliveAgentRunTrimmer();
            var run = Run(sequence: 3, scripts: 0);
            run.Activity.Add(new AgentActivityEvent { Kind = "tool", Text = "second" });
            trimmer.Trim(Copy(run), "r1|reveal", 0, appendActivity: true);

            run.Activity = new List<AgentActivityEvent>(run.Activity) { new() { Kind = "tool", Text = "third" } };
            lock (run) run.AdvanceRevision();
            var appended = Copy(run);
            var result = trimmer.Trim(appended, "r1|reveal", afterSequence: 3, appendActivity: true);
            Assert.Equal(2, result.ActivityFrom);
            Assert.Equal("third", Assert.Single(appended.Activity).Text);

            run.Activity[0].Text = "[masked:token ..ab12]";
            run.Activity = new List<AgentActivityEvent>(run.Activity) { new() { Kind = "tool", Text = "fourth" } };
            lock (run) run.AdvanceRevision();
            var whole = Copy(run);
            result = trimmer.Trim(whole, "r1|reveal", afterSequence: 4, appendActivity: true);
            Assert.Null(result.ActivityFrom);
            Assert.Equal(4, whole.Activity.Count);
        }

        /// <summary>A 600-step run polled after every step, merged the way the page does. Returns the bytes the
        /// whole run would have been and the bytes sent; asserts the page holds the server's run every step.</summary>
        private static (long Full, long Sent) Simulate(bool appendActivity)
        {
            var trimmer = new KliveAgentRunTrimmer();
            // Shaped like the live runs: a long brief (Alpha Lab's was 35 KB), scripts of a few KB, short activity lines.
            var run = new AgentPendingChatResponse { RequestId = "r1", Status = AgentTaskStatus.Running, Response = "", Activity = new(), ScriptsExecuted = new(),
                UserMessage = "Research batch. " + new string('b', 20_000) };
            JObject page = new();
            long pageSequence = 0, fullBytes = 0, sentBytes = 0;
            var rng = new Random(7);

            for (int step = 1; step <= 600; step++)
            {
                // What the agent does between polls: an activity line almost every step, a script now and then.
                if (rng.Next(10) < 9) run.Activity = new List<AgentActivityEvent>(run.Activity) { new() { Iteration = step, Kind = "tool", Text = $"step {step} " + new string('a', 80) } };
                if (step % 16 == 0) run.ScriptsExecuted = new List<AgentScriptResult>(run.ScriptsExecuted) { new() { Code = new string('c', 1_500), Output = new string('o', 2_500), Success = true } };
                if (step % 40 == 0) run.Response += $"Progress note {step}. ";
                if (step == 300) run.SteeringMessages = new List<AgentSteeringMessage> { new() { MessageId = "s1", Message = "Use the cached candles." } };
                lock (run) run.AdvanceRevision();

                // One poll: the route serves a copy of the run, trimmed against what the page said it holds.
                var view = JsonConvert.DeserializeObject<AgentPendingChatResponse>(JsonConvert.SerializeObject(run))!;
                fullBytes += JsonConvert.SerializeObject(view).Length;
                var trimmed = trimmer.Trim(view, "r1|reveal", pageSequence, appendActivity);
                // As the route builds it: activityFrom goes in the body when the activity was cut.
                var body = JObject.FromObject(view);
                if (trimmed.ActivityFrom is int from) body["activityFrom"] = from;
                string json = body.ToString(Formatting.None);
                sentBytes += json.Length;

                Merge(page, JObject.Parse(json));
                pageSequence = page.Value<long>("sequence");
                var server = JObject.FromObject(run);
                foreach (string part in Parts)
                    Assert.True(JToken.DeepEquals(server[part], page[part]), $"step {step}: the page's {part} differs from the run's");
            }
            return (fullBytes, sentBytes);
        }

        [Fact]
        public void AFirstPoll_AnUnknownSequence_AndAnotherViewer_GetTheWholeRun()
        {
            var trimmer = new KliveAgentRunTrimmer();
            var run = Run(sequence: 5, scripts: 2);

            Assert.Empty(trimmer.Trim(Copy(run), "r1|reveal", afterSequence: 0).Omitted);
            // Same run, same sequence, nothing changed: everything the page holds is left out (no ledger, no user message here).
            Assert.Equal(new[] { "response", "scriptsExecuted", "activity", "attachments", "steeringMessages" },
                trimmer.Trim(Copy(run), "r1|reveal", afterSequence: 5).Omitted);
            // A sequence never served, and a viewer whose copy is masked differently: whole.
            Assert.Empty(trimmer.Trim(Copy(run), "r1|reveal", afterSequence: 4).Omitted);
            Assert.Empty(trimmer.Trim(Copy(run), "r1|masked", afterSequence: 5).Omitted);
        }

        [Fact]
        public void ASequenceThatCarriedTwoDifferentRuns_IsNeverTrimmedAgainst()
        {
            // The legacy progress path changes a run without advancing its sequence.
            var trimmer = new KliveAgentRunTrimmer();
            var run = Run(sequence: 9, scripts: 1);
            trimmer.Trim(Copy(run), "r1|reveal", 0);
            run.ScriptsExecuted = new List<AgentScriptResult>(run.ScriptsExecuted) { new() { Code = "x", Output = "y" } };
            trimmer.Trim(Copy(run), "r1|reveal", 0); // same sequence, different scripts

            var view = Copy(run);
            Assert.Empty(trimmer.Trim(view, "r1|reveal", afterSequence: 9).Omitted);
            Assert.Equal(2, view.ScriptsExecuted.Count);
        }

        [Fact]
        public void APartThatChanged_IsSentInFull_AndTheRestLeftOut()
        {
            var trimmer = new KliveAgentRunTrimmer();
            var run = Run(sequence: 3, scripts: 2);
            trimmer.Trim(Copy(run), "r1|reveal", 0);

            run.Activity = new List<AgentActivityEvent>(run.Activity) { new() { Kind = "tool", Text = "one more" } };
            lock (run) run.AdvanceRevision();
            var view = Copy(run);
            var omitted = trimmer.Trim(view, "r1|reveal", afterSequence: 3).Omitted;

            Assert.Equal(new[] { "response", "scriptsExecuted", "attachments", "steeringMessages" }, omitted);
            Assert.Null(view.ScriptsExecuted);
            Assert.Equal(run.Activity.Count, view.Activity.Count);
        }

        private static AgentPendingChatResponse Run(long sequence, int scripts)
        {
            var run = new AgentPendingChatResponse
            {
                RequestId = "r1", Status = AgentTaskStatus.Running, Response = "Working on it.",
                Activity = new List<AgentActivityEvent> { new() { Kind = "tool", Text = "started" } },
                ScriptsExecuted = Enumerable.Range(0, scripts).Select(i => new AgentScriptResult { Code = "code " + i, Output = "out " + i, Success = true }).ToList(),
            };
            run.Sequence = sequence;
            return run;
        }

        private static AgentPendingChatResponse Copy(AgentPendingChatResponse run) =>
            JsonConvert.DeserializeObject<AgentPendingChatResponse>(JsonConvert.SerializeObject(run))!;

        /// <summary>The page's merge (useAgentChat.applyPendingFields): a part left out (null) keeps the page's last
        /// value; activity with activityFrom is appended there.</summary>
        private static void Merge(JObject page, JObject data)
        {
            if (data.Value<long>("sequence") < page.Value<long?>("sequence").GetValueOrDefault()) return;
            page["sequence"] = data["sequence"];
            foreach (string part in Parts)
            {
                if (data[part] is not { Type: not JTokenType.Null } value) continue;
                if (part == "activity" && data.Value<int?>("activityFrom") is int from)
                {
                    var have = (page["activity"] as JArray ?? new JArray()).Take(from).Select(t => t.DeepClone());
                    page["activity"] = new JArray(have.Concat(((JArray)value).Select(t => t.DeepClone())));
                }
                else page[part] = value.DeepClone();
            }
        }
    }
}
