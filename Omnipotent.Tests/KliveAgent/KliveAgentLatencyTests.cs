using Omnipotent.Services.KliveAgent;
using Omnipotent.Services.KliveAgent.Models;
using Xunit.Abstractions;

namespace Omnipotent.Tests.KliveAgent;

public sealed class KliveAgentLatencyTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompactPolicy_ReducesInputWhileKeepingActionAndMemoryRules(bool computerUse)
    {
        var compact = KliveAgentPromptPolicy.Build(true, computerUse);
        var detailed = KliveAgentPromptPolicy.BuildDetailed(true, computerUse);
        int before = KliveAgentContextBudget.EstimateTokens(detailed);
        int after = KliveAgentContextBudget.EstimateTokens(compact);
        output.WriteLine($"Operating policy estimated tokens: {before} -> {after} ({100.0 * (before - after) / before:F1}% reduction), computer={computerUse}");
        Assert.True(after < before / 3);
        Assert.Contains("approval", compact);
        Assert.Contains("Never expose secrets", compact);
        Assert.Contains("recall_memories", compact);
        Assert.Contains("wait_for", compact);
        Assert.Contains("CreateLongTermJob", compact);
        Assert.Equal(computerUse, compact.Contains("[Computer control]"));
    }

    [Fact]
    public void TokenBuffer_FirstTokenIsImmediate_AndFinalFlushLosesNothing()
    {
        var emitted = new List<string>();
        var buffer = new KliveAgentTokenBuffer(emitted.Add, nowMs: () => 0);
        buffer.Append("");
        Assert.Empty(emitted);
        buffer.Append("a");
        Assert.Equal(new[] { "a" }, emitted);
        for (int i = 0; i < 999; i++) buffer.Append("a");
        Assert.Single(emitted);
        buffer.Flush();
        buffer.Flush();
        Assert.Equal(2, emitted.Count);
        Assert.Equal(new string('a', 1000), emitted[1]);
    }

    [Fact]
    public void TokenBuffer_CoalescesUpdatesUntilTheIntervalElapses()
    {
        long now = 0;
        var emitted = new List<string>();
        var buffer = new KliveAgentTokenBuffer(emitted.Add, nowMs: () => now);
        buffer.Append("first");
        now = 49;
        buffer.Append(" second");
        Assert.Single(emitted);
        now = 50;
        buffer.Append(" third");
        Assert.Equal(new[] { "first", "first second third" }, emitted);
    }

    [Fact]
    public async Task ProgressWait_WakesEveryClient_AndUsesAFreshSignalForNextRevision()
    {
        var run = new AgentPendingChatResponse();
        var first = run.WaitForChangeAsync(run.Sequence, 20_000);
        var second = run.WaitForChangeAsync(run.Sequence, 20_000);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        lock (run) run.AdvanceRevision();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(run.WaitForChangeAsync(0, 20_000).IsCompletedSuccessfully);
        var next = run.WaitForChangeAsync(run.Sequence, 20_000);
        Assert.False(next.IsCompleted);
        lock (run) { run.Status = AgentTaskStatus.Completed; run.AdvanceRevision(); }
        await next.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(run.WaitForChangeAsync(run.Sequence, 20_000).IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ProgressWait_TimesOutOrCancelsWithoutConsumingTheNextSignal()
    {
        var run = new AgentPendingChatResponse();
        await run.WaitForChangeAsync(run.Sequence, 10).WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var canceled = run.WaitForChangeAsync(run.Sequence, 20_000, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        var waiting = run.WaitForChangeAsync(run.Sequence, 20_000);
        lock (run) run.AdvanceRevision();
        await waiting.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Index_PrunesGeneratedTrees_AndPreservesDependencyResolution()
    {
        string root = Path.Combine(Path.GetTempPath(), "omnipotent-latency-tests", Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource();
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "Source.cs"), "namespace A; public class Target {} public class Caller { private Target value; }");
            Directory.CreateDirectory(Path.Combine(root, "Other"));
            await File.WriteAllTextAsync(Path.Combine(root, "Other", "Use.cs"), "namespace B; public class User { private A.Target value; }");
            foreach (var excluded in new[] { "bin", "OBJ", "node_modules", ".git", ".vs", ".nuxt", ".output" })
            {
                var path = Path.Combine(root, excluded, "nested");
                Directory.CreateDirectory(path);
                await File.WriteAllTextAsync(Path.Combine(path, "Generated.cs"), "public class Generated {}");
            }
            Assert.Equal(2, KliveAgentCodebaseIndex.EnumerateSourceFiles(root).Count());
            var index = new KliveAgentCodebaseIndex(root, Path.Combine(root, "cache"));
            await index.InitializeAsync(cancellation.Token);
            Assert.Equal(2, index.GetAllRelativeFilePaths().Count);
            Assert.Empty(index.FindDefinitions("Generated"));
            Assert.Equal(new[] { "Source.cs" }, index.GetImportEdges()["Other/Use.cs"]);
            Assert.Empty(index.GetImportEdges()["Source.cs"]);
            var graph = new KliveAgentSymbolGraph(index);
            await graph.BuildAsync(cancellation.Token);
            Assert.True(graph.IsBuilt);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => graph.BuildAsync(cancellation.Token));
            Assert.Throws<OperationCanceledException>(() => KliveAgentCodebaseIndex.EnumerateSourceFiles(root, cancellation.Token).ToList());
        }
        finally
        {
            cancellation.Cancel();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
