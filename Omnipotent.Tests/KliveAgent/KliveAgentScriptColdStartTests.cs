using System.Diagnostics;
using Omnipotent.Services.KliveAgent;

namespace Omnipotent.Tests.KliveAgent
{
    /// <summary>
    /// The first script of a run. On the server it compiled in 4.5 s at the median (22 s at p90, and nine
    /// times over 40 s) against 132 ms for the scripts after it, because every run began a new compilation
    /// against the whole reference set with nothing kept alive from the last one. These tests hold the
    /// shape of the fix: a new session continues a root that outlives a full collection, so its first
    /// compile costs what any other compile costs.
    /// </summary>
    public class KliveAgentScriptColdStartTests
    {
        private const string Typical =
            "var items = new List<int> { 1, 2, 3 };\n"
            + "var text = string.Join(\",\", items.Select(x => x * 2));\n"
            + "var flags = BindingFlags.Public | BindingFlags.Instance;\n"
            + "Log(text + flags + Path.GetFileName(\"a/b.txt\"));";

        private static KliveAgentScriptEngine.ScriptExecutionSession NewSession() =>
            new KliveAgentScriptEngine(null!).CreateSession(new ScriptGlobals(null!));

        private static void CollectEverything()
        {
            for (int i = 0; i < 2; i++)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }
        }

        [Fact]
        public async Task FirstScriptOfANewSession_AfterAFullCollection_CompilesLikeAnyOther()
        {
            // Pay the one-time cost (reading the references, JIT of the compiler) outside the measurement.
            var warm = NewSession();
            Assert.True((await warm.ExecuteAsync(Typical, TimeSpan.FromMinutes(2))).Success);
            var continued = await warm.ExecuteAsync("Log(items.Count.ToString()); // continuation");
            Assert.True(continued.Success, continued.ErrorMessage);
            Assert.False(continued.ColdStart);

            var firstCompiles = new List<long>();
            for (int i = 0; i < 4; i++)
            {
                warm = null!;
                CollectEverything();
                var session = NewSession();
                var first = await session.ExecuteAsync(Typical + " // run " + i, TimeSpan.FromMinutes(2));
                Assert.True(first.Success, first.ErrorMessage);
                Assert.True(first.ColdStart);
                firstCompiles.Add(first.CompileMs!.Value);
            }

            long median = firstCompiles.OrderBy(x => x).ElementAt(firstCompiles.Count / 2);
            // Generous for a loaded test machine; the server's old median was 4,505 ms.
            Assert.True(median < 1500, $"first-script compiles after a full GC: {string.Join(", ", firstCompiles)} ms");
        }

        [Fact]
        public async Task CompileHasItsOwnBudget_SoASlowCompileIsNotARunTimeout()
        {
            // A run budget far smaller than any compile: the old engine counted the compile against it and
            // reported "compiling took longer than 30s" for what was a busy server.
            var result = await NewSession().ExecuteAsync(Typical, TimeSpan.FromMilliseconds(400));
            Assert.True(result.Success, result.ErrorMessage);
        }

        [Fact]
        public async Task ConversationSession_IsKeptBetweenRuns_AndForgottenAfterItsIdleLimit()
        {
            var engine = new KliveAgentScriptEngine(null!);
            var options = ScriptSessionOptions.Default;

            var (first, firstGlobals, reused) = engine.AcquireConversationSession("conv-a", () => new ScriptGlobals(null!), options, TimeSpan.FromMinutes(20));
            Assert.False(reused);
            Assert.True((await first.ExecuteAsync("var remembered = 41;", TimeSpan.FromMinutes(2))).Success);

            // The next run in the same conversation continues it, on the same globals.
            var (second, secondGlobals, reusedAgain) = engine.AcquireConversationSession("conv-a", () => new ScriptGlobals(null!), options, TimeSpan.FromMinutes(20));
            Assert.True(reusedAgain);
            Assert.Same(first, second);
            Assert.Same(firstGlobals, secondGlobals);
            var next = await second.ExecuteAsync("Log((remembered + 1).ToString());");
            Assert.True(next.Success, next.ErrorMessage);
            Assert.Contains("42", next.Output);
            Assert.False(next.ColdStart);
            Assert.Contains("remembered (int)", second.DescribeVariables());

            // Another conversation never sees it.
            var (other, _, otherReused) = engine.AcquireConversationSession("conv-b", () => new ScriptGlobals(null!), options, TimeSpan.FromMinutes(20));
            Assert.False(otherReused);
            Assert.False((await other.ExecuteAsync("Log(remembered.ToString());")).Success);

            // Keeping switched off (0) gives every run a new session, as before.
            var (fresh, _, freshReused) = engine.AcquireConversationSession("conv-a", () => new ScriptGlobals(null!), options, TimeSpan.Zero);
            Assert.False(freshReused);
            Assert.NotSame(first, fresh);
            Assert.Equal(0, engine.KeptSessionCount);
        }

        [Fact]
        public void KeptSessions_AreBounded()
        {
            var engine = new KliveAgentScriptEngine(null!);
            for (int i = 0; i < KliveAgentScriptEngine.MaxKeptSessions + 5; i++)
                engine.AcquireConversationSession("conv-" + i, () => new ScriptGlobals(null!), ScriptSessionOptions.Default, TimeSpan.FromMinutes(20));
            Assert.True(engine.KeptSessionCount <= KliveAgentScriptEngine.MaxKeptSessions);
        }

        [Fact]
        public async Task WarmupScript_Compiles()
        {
            var result = await NewSession().ExecuteAsync(KliveAgentScriptEngine.WarmupScript, TimeSpan.FromMinutes(2));
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Null(result.Repairs);
        }
    }
}
