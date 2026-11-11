using System.Diagnostics;
using Omnipotent.Services.KliveAPI.Telemetry;
using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects
{
    /// <summary>
    /// Every fleet action Klives took in October held its request for an hour or more: halt-all
    /// 84 min on 31 Oct, pause 82 min, unhalt-all 136 min on 30 Oct, clearing the cache halt 65 min
    /// on 29 Oct. The browser gave up each time and nothing said which step was waiting. These pin the
    /// guards: a lifecycle request answers within its bound while the action finishes on its own, the
    /// step that is stuck is named while it is stuck, and a halt with no wake to cancel does not leave
    /// the project reading "Pausing" for good.
    /// </summary>
    public class LifecycleActionTests
    {
        [Fact]
        public async Task SlowAction_AnswersPendingWithItsStep_NamesItWhileStuck_AndStillFinishes()
        {
            var logs = new List<string>();
            using var release = new ManualResetEventSlim(false);
            var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var trace = new ProjectActionTrace("halt-all", m => { lock (logs) logs.Add(m); },
                stuckAfter: TimeSpan.FromMilliseconds(200), slowStep: TimeSpan.FromMilliseconds(100));

            var clock = Stopwatch.StartNew();
            var (done, _) = await ProjectsRoutes.RunBoundedAsync<bool>(null, trace, () =>
            {
                trace.Step("p-1", "save-status", () => { });
                trace.Step("p-1", "cancel-wake", () => release.Wait(TimeSpan.FromSeconds(20)));
                finished.SetResult(true);
                return true;
            }, TimeSpan.FromMilliseconds(300), lateFailure: null);

            Assert.False(done);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"answered after {clock.Elapsed}");
            Assert.Equal("cancel-wake", trace.CurrentStep);
            await WaitUntil(() => Logged(logs, "'cancel-wake'", "still waiting"));
            Assert.False(Logged(logs, "'save-status'", "took"));

            release.Set();
            Assert.True(await finished.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            await WaitUntil(() => Logged(logs, "'cancel-wake'", "took"));
        }

        [Fact]
        public async Task FastAction_ReturnsItsResult_AndItsStepsBecomeRequestSpans()
        {
            var trace = new ProjectActionTrace("pause", _ => { });
            var request = new RequestTrace(Stopwatch.GetTimestamp());

            var (done, result) = await ProjectsRoutes.RunBoundedAsync(request, trace, () =>
            {
                trace.Step("p-1", "save-status", () => { });
                return trace.Step("p-1", "cancel-wake", () => 42);
            }, TimeSpan.FromSeconds(10), lateFailure: null);

            Assert.True(done);
            Assert.Equal(42, result);
            var spans = Enumerable.Range(0, request.SpanCount).Select(request.SpanName).ToList();
            Assert.Equal(new[] { "projects-save-status", "projects-cancel-wake" }, spans);
        }

        [Fact]
        public async Task ActionThatFailsAfterAnswering_IsReported()
        {
            var trace = new ProjectActionTrace("resume", _ => { });
            var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            var (done, _) = await ProjectsRoutes.RunBoundedAsync<bool>(null, trace, () =>
            {
                Thread.Sleep(400);
                throw new InvalidOperationException("disk full");
            }, TimeSpan.FromMilliseconds(50), ex => failed.TrySetResult(ex));

            Assert.False(done);
            Assert.Equal("disk full", (await failed.Task.WaitAsync(TimeSpan.FromSeconds(10))).Message);
        }

        [Theory]
        [InlineData(ProjectStatus.Paused, ProjectExecutionDisposition.Pausing, false, true)]
        [InlineData(ProjectStatus.Archived, ProjectExecutionDisposition.Pausing, false, true)]
        [InlineData(ProjectStatus.Paused, ProjectExecutionDisposition.Pausing, true, false)]   // the live wake settles it
        [InlineData(ProjectStatus.Active, ProjectExecutionDisposition.Pausing, false, false)]  // a resume is under way
        [InlineData(ProjectStatus.Paused, ProjectExecutionDisposition.Paused, false, false)]
        public void StrandedPausing_IsOnlyAPausedProjectWithNoWakeLeft(ProjectStatus status,
            ProjectExecutionDisposition disposition, bool liveWake, bool stranded)
        {
            Assert.Equal(stranded, Omnipotent.Services.Projects.Projects.IsStrandedPausing(status, disposition, liveWake));
        }

        private static bool Logged(List<string> logs, string a, string b)
        {
            lock (logs) return logs.Any(l => l.Contains(a) && l.Contains(b));
        }

        private static async Task WaitUntil(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
                await Task.Delay(20);
            }
        }
    }
}
