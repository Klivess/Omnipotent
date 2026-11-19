using System.Net;
using Omnipotent.Services.OmniscienceV3.Api;

namespace Omnipotent.Tests.OmniscienceV3.Api
{
    /// <summary>
    /// "Timeouts are not an acceptable solution. There must always be a response." (Klives, 19 Nov 2026, when link and
    /// image search left their requests open for over a minute.) A read is answered within a few seconds whatever it
    /// costs: with its data, or with "pending" while the same work carries on and is handed to the next ask.
    /// </summary>
    public class AnswerGuardTests
    {
        private sealed class Request
        {
            public readonly List<(string Json, HttpStatusCode Status)> Written = new();
            public Task Write(string json, HttpStatusCode status) { lock (Written) Written.Add((json, status)); return Task.CompletedTask; }
            public Task Pending() => Write(AnswerGuard.PendingJson, HttpStatusCode.Accepted);
        }

        /// <summary>A handler as the routes write them: it works, then answers through ApiResponses' slot.</summary>
        private static Func<Task> Handler(Request request, Task until, string json, Action? started = null) => async () =>
        {
            var slot = ApiResponses.Slot!;
            started?.Invoke();
            await until;
            await slot.AnswerAsync(json, HttpStatusCode.OK, () => request.Write(json, HttpStatusCode.OK));
        };

        private static AnswerGuard Guard(int withinMs = 150) => new() { AnswerWithin = TimeSpan.FromMilliseconds(withinMs) };

        [Fact]
        public async Task AQuickRead_IsAnsweredByItsHandler_AndNothingIsKept()
        {
            var guard = Guard();
            var request = new Request();
            await guard.RunAsync("u|/links/search", Handler(request, Task.CompletedTask, """{"items":[]}"""), request.Write, request.Pending);
            Assert.Equal(("""{"items":[]}""", HttpStatusCode.OK), Assert.Single(request.Written));
            Assert.Equal((0L, 0), (guard.Deferred, guard.Waiting));
        }

        [Fact]
        public async Task ASlowRead_IsAnsweredPendingAtOnce_KeepsRunning_AndTheNextAskGetsItsAnswer()
        {
            var guard = Guard();
            var finish = new TaskCompletionSource();
            int runs = 0;
            var first = new Request();
            var asked = System.Diagnostics.Stopwatch.StartNew();
            await guard.RunAsync("u|/links/search", Handler(first, finish.Task, """{"items":[1]}""", () => Interlocked.Increment(ref runs)), first.Write, first.Pending);

            // Answered, and soon: not left open for the minute the search needs.
            var pending = Assert.Single(first.Written);
            Assert.Equal(HttpStatusCode.Accepted, pending.Status);
            Assert.Contains("\"code\":\"pending\"", pending.Json);
            Assert.Contains("\"retryAfterMs\":1500", pending.Json);
            Assert.True(asked.ElapsedMilliseconds < 2_000, $"answered after {asked.ElapsedMilliseconds} ms");
            Assert.Equal(1, guard.Waiting);

            // Asked again while it is still working: the same work is waited on (not started a second time), then "pending" again.
            var second = new Request();
            await guard.RunAsync("u|/links/search", Handler(second, Task.CompletedTask, "never", () => Interlocked.Increment(ref runs)), second.Write, second.Pending);
            Assert.Equal(HttpStatusCode.Accepted, Assert.Single(second.Written).Status);
            Assert.Equal(1, runs);

            // The work finishes. Its answer goes to whoever asks next, exactly as the handler gave it.
            finish.SetResult();
            var third = new Request();
            await guard.RunAsync("u|/links/search", Handler(third, Task.CompletedTask, "never", () => Interlocked.Increment(ref runs)), third.Write, third.Pending);
            Assert.Equal(("""{"items":[1]}""", HttpStatusCode.OK), Assert.Single(third.Written));
            Assert.Equal(1, runs);
            // The request that was told "pending" got nothing more written to it.
            Assert.Single(first.Written);

            // Handed over once: the next ask is a fresh read.
            var fourth = new Request();
            await guard.RunAsync("u|/links/search", Handler(fourth, Task.CompletedTask, """{"items":[2]}""", () => Interlocked.Increment(ref runs)), fourth.Write, fourth.Pending);
            Assert.Equal("""{"items":[2]}""", Assert.Single(fourth.Written).Json);
            Assert.Equal((2, 0), (runs, guard.Waiting));
            Assert.Equal(2L, guard.Deferred);
        }

        /// <summary>
        /// Most v3 handlers have no await in them: <c>req => OkAsync(req, module.Search(...))</c> runs the whole search
        /// before it returns a task. The first deployment of the guard called the handler directly, so its clock only
        /// started once such a handler had finished: on the server two searches still held their requests for over a minute.
        /// </summary>
        [Fact]
        public async Task AHandlerThatNeverAwaits_IsStillAnsweredInTime()
        {
            var guard = Guard();
            using var release = new ManualResetEventSlim(false);
            var first = new Request();
            Func<Task> synchronous = () =>
            {
                var slot = ApiResponses.Slot!;
                release.Wait(TimeSpan.FromSeconds(30)); // a query on a busy disk: no await anywhere
                return slot.AnswerAsync("""{"items":[7]}""", HttpStatusCode.OK, () => first.Write("""{"items":[7]}""", HttpStatusCode.OK));
            };
            var asked = System.Diagnostics.Stopwatch.StartNew();
            await guard.RunAsync("k", synchronous, first.Write, first.Pending);
            Assert.True(asked.ElapsedMilliseconds < 5_000, $"answered after {asked.ElapsedMilliseconds} ms");
            Assert.Equal(HttpStatusCode.Accepted, Assert.Single(first.Written).Status);

            release.Set();
            var second = new Request();
            await guard.RunAsync("k", Handler(second, Task.CompletedTask, "never"), second.Write, second.Pending);
            Assert.Equal(("""{"items":[7]}""", HttpStatusCode.OK), Assert.Single(second.Written));
        }

        [Fact]
        public async Task TheAskThatWaits_IsGivenTheAnswerAsSoonAsItIsThere()
        {
            var guard = Guard(withinMs: 400);
            var finish = new TaskCompletionSource();
            var first = new Request();
            await guard.RunAsync("k", Handler(first, finish.Task, """{"ok":true}"""), first.Write, first.Pending);

            var second = new Request();
            var waiting = guard.RunAsync("k", Handler(second, Task.CompletedTask, "never"), second.Write, second.Pending);
            finish.SetResult();
            await waiting;
            Assert.Equal(("""{"ok":true}""", HttpStatusCode.OK), Assert.Single(second.Written));
        }

        [Fact]
        public async Task ALateFailure_IsKeptToo_SoTheNextAskLearnsWhy()
        {
            var guard = Guard();
            var finish = new TaskCompletionSource();
            var first = new Request();
            Func<Task> failing = async () =>
            {
                var slot = ApiResponses.Slot!;
                await finish.Task;
                // What the registrar does with an exception: a classified error, through the same slot.
                await slot.AnswerAsync("""{"code":"not_found","message":"No person 'x'."}""", HttpStatusCode.NotFound, () => first.Write("direct", HttpStatusCode.NotFound));
            };
            await guard.RunAsync("k", failing, first.Write, first.Pending);
            finish.SetResult();

            var second = new Request();
            await guard.RunAsync("k", Handler(second, Task.CompletedTask, "never"), second.Write, second.Pending);
            var answer = Assert.Single(second.Written);
            Assert.True(answer.Status == HttpStatusCode.NotFound, $"second got {answer.Status} {answer.Json}; first got {string.Join(" | ", first.Written)}");
            Assert.Contains("not_found", answer.Json);
        }

        [Fact]
        public async Task DifferentAsks_AndDifferentPeople_DoNotShareWork()
        {
            var guard = Guard();
            var never = new TaskCompletionSource();
            var a = new Request();
            await guard.RunAsync("ada|/links/search&q=cats", Handler(a, never.Task, "a"), a.Write, a.Pending);

            foreach (string key in new[] { "ada|/links/search&q=dogs", "bo|/links/search&q=cats" })
            {
                var other = new Request();
                await guard.RunAsync(key, Handler(other, Task.CompletedTask, """{"mine":true}"""), other.Write, other.Pending);
                Assert.Equal("""{"mine":true}""", Assert.Single(other.Written).Json);
            }
            never.SetResult();
        }

        [Fact]
        public async Task AHandlerThatWritesItsOwnResponse_IsLeftToIt()
        {
            var guard = Guard();
            var gate = new TaskCompletionSource();
            var request = new Request();
            bool direct = false, afterDeadline = true;
            // A stream claims the response as it starts; the guard never answers over it, however long it runs.
            var run = guard.RunAsync("k", async () =>
            {
                direct = ApiResponses.BeginDirect();
                await gate.Task;
            }, request.Write, request.Pending);
            await Task.Delay(400);
            Assert.False(run.IsCompleted);
            Assert.Empty(request.Written);
            gate.SetResult();
            await run;
            Assert.True(direct);

            // One that is slow to start its stream finds the request answered, and writes nothing.
            var slowToStart = new TaskCompletionSource();
            var second = new Request();
            var done = new TaskCompletionSource();
            await guard.RunAsync("k2", async () =>
            {
                var slot = ApiResponses.Slot!;
                await slowToStart.Task;
                afterDeadline = slot.TryWriteDirect();
                done.SetResult();
            }, second.Write, second.Pending);
            Assert.Equal(HttpStatusCode.Accepted, Assert.Single(second.Written).Status);
            slowToStart.SetResult();
            await done.Task;
            Assert.False(afterDeadline);
        }
    }
}
