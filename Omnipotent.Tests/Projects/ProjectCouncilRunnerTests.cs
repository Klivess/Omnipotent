using System.Collections.Concurrent;
using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects
{
    /// <summary>
    /// Council orchestration, driven by scripted LLM turns (no real model). Verifies the
    /// three-round adversarial structure, spend booking, event emission, graceful degradation,
    /// cancellation, and the per-wake/per-day caps.
    /// </summary>
    [Collection("ProjectsSerial")]
    public class ProjectCouncilRunnerTests
    {
        private static string NewProjectId() => "test_" + Guid.NewGuid().ToString("N");
        private static Project NewProject(string pid) => new()
        {
            ProjectID = pid, Name = "t", Goal = "win", Status = ProjectStatus.Planning,
            TokenBudgetUsd = 100,
        };

        private sealed class Harness
        {
            public ProjectCouncilStore Store = new(_ => { });
            public ProjectEventLogStore Log = new(_ => { });
            public ProjectCouncilRunner Runner = null!;
            public List<(string sid, string user)> QueryCalls = new();
            public List<(string sid, string user)> ContinueCalls = new();
            public int SpendCalls;
            public HashSet<string> ReturnNullForSessionSubstring = new();

            public Harness()
            {
                Runner = new ProjectCouncilRunner(Store, Log, _ => { })
                {
                    QueryAsync = (sid, sys, user, model, max, ct) =>
                    {
                        ct.ThrowIfCancellationRequested();
                        lock (QueryCalls) QueryCalls.Add((sid, user));
                        if (ReturnNullForSessionSubstring.Any(sid.Contains))
                            return Task.FromResult<CouncilTurn?>(null);
                        string text = sid.EndsWith("-chair") ? "CHAIR-VERDICT" : $"OPENING:{sid}";
                        return Task.FromResult<CouncilTurn?>(new CouncilTurn(true, text, 100, 50, "gen", 0.001));
                    },
                    ContinueAsync = (sid, user, model, max, ct) =>
                    {
                        ct.ThrowIfCancellationRequested();
                        lock (ContinueCalls) ContinueCalls.Add((sid, user));
                        return Task.FromResult<CouncilTurn?>(new CouncilTurn(true, $"REBUTTAL:{sid}", 80, 40, "gen", 0.001));
                    },
                    RecordSpendAsync = (pid, p, c, g, cost) => { Interlocked.Increment(ref SpendCalls); return Task.CompletedTask; },
                };
            }
        }

        [Fact]
        public async Task FullCouncil_ThreeRounds_SevenStatements_AndVerdict()
        {
            var h = new Harness();
            string pid = NewProjectId();
            var session = await h.Runner.ConveneAsync(NewProject(pid), "w1", "Pivot?", "briefing text",
                roles: null, "elevated", "planning", "test/model", maxPerWake: 5, maxPerDay: 10, CancellationToken.None);

            Assert.Equal(CouncilStatus.Completed, session.Status);
            Assert.Equal(3, session.Roles.Count); // default panel
            Assert.Equal(3, session.Statements.Count(s => s.Round == 1));
            Assert.Equal(3, session.Statements.Count(s => s.Round == 2));
            Assert.Equal(1, session.Statements.Count(s => s.Round == 3));
            Assert.Equal("CHAIR-VERDICT", session.VerdictText);
            Assert.Equal(7, h.SpendCalls); // every model turn booked to the ledger
            Assert.True(session.TotalCostUsd > 0);
        }

        [Fact]
        public async Task RebuttalPrompts_ContainOtherOpenings()
        {
            var h = new Harness();
            string pid = NewProjectId();
            await h.Runner.ConveneAsync(NewProject(pid), "w1", "T", "B", null, "routine", "decision",
                "m", 5, 10, CancellationToken.None);

            // The Strategist's rebuttal must include the Skeptic's and Pragmatist's opening text.
            var strategistRebuttal = h.ContinueCalls.First(c => c.sid.EndsWith("-strategist")).user;
            Assert.Contains("skeptic", strategistRebuttal, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("pragmatist", strategistRebuttal, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("-strategist", strategistRebuttal); // not its own opening
        }

        [Fact]
        public async Task Events_EmittedInOrder()
        {
            var h = new Harness();
            string pid = NewProjectId();
            await h.Runner.ConveneAsync(NewProject(pid), "w1", "T", "B", null, "routine", "decision",
                "m", 5, 10, CancellationToken.None);

            var types = h.Log.ReadSince(pid, 0).Select(e => e.Type).ToList();
            Assert.Equal(ProjectEventTypes.CouncilConvened, types.First());
            Assert.Equal(ProjectEventTypes.CouncilVerdict, types.Last());
            Assert.Equal(7, types.Count(t => t == ProjectEventTypes.CouncilStatement));
        }

        [Fact]
        public async Task OnePanelistFails_StillCompletesWithRemaining()
        {
            var h = new Harness();
            h.ReturnNullForSessionSubstring.Add("-skeptic"); // skeptic never opens
            string pid = NewProjectId();
            var session = await h.Runner.ConveneAsync(NewProject(pid), "w1", "T", "B", null, "routine", "decision",
                "m", 5, 10, CancellationToken.None);

            Assert.Equal(CouncilStatus.Completed, session.Status);
            Assert.Equal(2, session.Statements.Count(s => s.Round == 1)); // 2 openings survived
            Assert.Equal(2, session.Statements.Count(s => s.Round == 2)); // only respondents rebut
            Assert.Equal("CHAIR-VERDICT", session.VerdictText);
        }

        [Fact]
        public async Task TooFewOpenings_Fails()
        {
            var h = new Harness();
            h.ReturnNullForSessionSubstring.Add("-skeptic");
            h.ReturnNullForSessionSubstring.Add("-pragmatist"); // only strategist opens → <2
            string pid = NewProjectId();
            var session = await h.Runner.ConveneAsync(NewProject(pid), "w1", "T", "B", null, "routine", "decision",
                "m", 5, 10, CancellationToken.None);

            Assert.Equal(CouncilStatus.Failed, session.Status);
            Assert.Contains("panelists", session.Error!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Cancellation_MarksCancelled_AndRethrows()
        {
            var h = new Harness();
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            string pid = NewProjectId();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                h.Runner.ConveneAsync(NewProject(pid), "w1", "T", "B", null, "routine", "decision", "m", 5, 10, cts.Token));

            var stored = h.Store.List(pid).FirstOrDefault();
            Assert.NotNull(stored);
            Assert.Equal(CouncilStatus.Cancelled, stored!.Status);
        }

        [Fact]
        public async Task CustomRoles_AreClampedAndUsed()
        {
            var h = new Harness();
            string pid = NewProjectId();
            var session = await h.Runner.ConveneAsync(NewProject(pid), "w1", "T", "B",
                roles: new[] { "Economist", "Engineer", "Lawyer", "Chair", "Economist" }, // Chair stripped, dupes removed
                "routine", "decision", "m", 5, 10, CancellationToken.None);

            Assert.Equal(new[] { "Economist", "Engineer", "Lawyer" }, session.Roles);
        }

        [Fact]
        public async Task PerWakeCap_Refuses_WithoutPersisting()
        {
            var h = new Harness();
            string pid = NewProjectId();
            h.Store.Create(new CouncilSession { ProjectID = pid, WakeID = "w1", Topic = "prior" });
            h.Store.Create(new CouncilSession { ProjectID = pid, WakeID = "w1", Topic = "prior2" });

            var session = await h.Runner.ConveneAsync(NewProject(pid), "w1", "T", "B", null, "routine", "decision",
                "m", maxPerWake: 2, maxPerDay: 10, CancellationToken.None);

            Assert.Equal(CouncilStatus.Failed, session.Status);
            Assert.Contains("wake", session.Error!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, h.Store.List(pid).Count); // no new session persisted
        }

        [Fact]
        public async Task PerDayCap_Refuses()
        {
            var h = new Harness();
            string pid = NewProjectId();
            h.Store.Create(new CouncilSession { ProjectID = pid, WakeID = "wX", Topic = "prior" });

            var session = await h.Runner.ConveneAsync(NewProject(pid), "w1", "T", "B", null, "routine", "decision",
                "m", maxPerWake: 5, maxPerDay: 1, CancellationToken.None);

            Assert.Equal(CouncilStatus.Failed, session.Status);
            Assert.Contains("daily", session.Error!, StringComparison.OrdinalIgnoreCase);
        }
    }
}
