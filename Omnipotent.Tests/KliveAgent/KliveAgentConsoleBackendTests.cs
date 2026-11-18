using System.Collections.Concurrent;
using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveAgent;
using Omnipotent.Services.KliveAgent.Models;
using Xunit.Abstractions;

namespace Omnipotent.Tests.KliveAgent;

/// <summary>
/// Backend behaviour the KliveAgent page depends on, reproduced from live measurements on 2026-11-11.
/// </summary>
public class KliveAgentConsoleBackendTests
{
    private readonly ITestOutputHelper output;
    public KliveAgentConsoleBackendTests(ITestOutputHelper output) => this.output = output;

    private static global::Omnipotent.Services.KliveLLM.KliveLLM.KliveLLMResponse Call(int prompt, int cached) => new()
    {
        PromptTokens = prompt, CachedPromptTokens = cached, CacheMetricsAvailable = true,
        CompletionTokens = 400, Provider = "AIRouter", Model = "Qwen3.8",
        RequestDurationMs = 30_000, QueueDurationMs = 10, ProviderDurationMs = 29_990,
        LatencyBreakdownAvailable = true,
    };

    private static string TempRoot(string name) =>
        Path.Combine(Path.GetTempPath(), $"kliveagent-{name}-" + Guid.NewGuid().ToString("N"));

    // ── prompt cache (live: 11–21 s per read of a 7,325-line journal, whatever the range) ──

    [Fact]
    public void PromptCache_ParsesTheJournalOnceThenOnlyNewLines()
    {
        string root = TempRoot("cache-tail");
        try
        {
            string path = Path.Combine(root, "usage.jsonl");
            var writer = new KliveAgentPromptCache(path);
            for (int conversation = 0; conversation < 160; conversation++)
                for (int turn = 1; turn <= 20; turn++)
                    writer.Record($"conv_{conversation:D4}", turn, Call(45_000 + turn * 900, turn == 1 ? 0 : 44_000 + turn * 800));

            // A new process: nothing parsed yet.
            var journal = new KliveAgentPromptCache(path);
            var first = Stopwatch.StartNew();
            var month = journal.GetSnapshot("30d");
            first.Stop();
            Assert.Equal(3_200, month.Requests);
            Assert.Equal(3_200, journal.ParsedLines);

            var second = Stopwatch.StartNew();
            var week = journal.GetSnapshot("7d");
            second.Stop();
            Assert.Equal(3_200, week.Requests);
            Assert.Equal(3_200, journal.ParsedLines); // nothing re-read

            journal.Record("conv_new", 1, Call(50_000, 0));
            Assert.Equal(3_201, journal.GetSnapshot("7d").Requests);
            Assert.Equal(3_201, journal.ParsedLines);

            output.WriteLine($"first {first.ElapsedMilliseconds} ms · second {second.ElapsedMilliseconds} ms");
            Assert.True(second.ElapsedMilliseconds < 1_000, $"second read took {second.ElapsedMilliseconds} ms");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void PromptCache_LeavesAHalfWrittenLineForTheNextReadAndSurvivesABom()
    {
        string root = TempRoot("cache-partial");
        try
        {
            string path = Path.Combine(root, "usage.jsonl");
            new KliveAgentPromptCache(path).Record("a", 1, Call(10_000, 0));
            string whole = File.ReadAllText(path);
            // A BOM in front (an editor or an older writer) and another process half-way through a line.
            File.WriteAllText(path, "﻿" + whole + whole.TrimEnd()[..40], new System.Text.UTF8Encoding(false));

            var journal = new KliveAgentPromptCache(path);
            Assert.Equal(1, journal.GetSnapshot("7d").Requests);

            // The other writer finishes its line.
            File.WriteAllText(path, "﻿" + whole + whole, new System.Text.UTF8Encoding(false));
            Assert.Equal(2, journal.GetSnapshot("7d").Requests);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void PromptCache_ReplacedFileIsReadAgainFromTheStart()
    {
        string root = TempRoot("cache-replace");
        try
        {
            string path = Path.Combine(root, "usage.jsonl");
            var journal = new KliveAgentPromptCache(path);
            journal.Record("a", 1, Call(10_000, 0));
            journal.Record("a", 2, Call(11_000, 9_000));
            Assert.Equal(2, journal.GetSnapshot("7d").Requests);

            File.WriteAllText(path, File.ReadLines(path).First() + Environment.NewLine);
            Assert.Equal(1, journal.GetSnapshot("7d").Requests);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    // ── mark all read (live: 3 calls on 14 Oct took 217–220 s each) ──

    [Fact]
    public async Task MarkAllRead_MarksAtOnceAndAnswersWithoutWaitingForEveryWrite()
    {
        var all = Enumerable.Range(0, 40)
            .Select(i => new AgentNotification { NotificationId = $"n{i}", CreatedAt = DateTime.UtcNow.AddMinutes(-i) })
            .Append(new AgentNotification { NotificationId = "already", CreatedAt = DateTime.UtcNow, ReadAt = DateTime.UtcNow.AddDays(-1) })
            .ToList();
        var written = new ConcurrentBag<string>();
        int inFlight = 0, peak = 0;
        async Task SlowPersist(AgentNotification n)
        {
            int now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref peak, now);
            await Task.Delay(250); // one queued, fsync'd write on a busy disk
            written.Add(n.NotificationId);
            Interlocked.Decrement(ref inFlight);
        }

        var clock = Stopwatch.StartNew();
        int marked = await global::Omnipotent.Services.KliveAgent.KliveAgent.MarkAllReadAsync(
            all, SlowPersist, TimeSpan.FromMilliseconds(600), _ => { }, concurrency: 6);
        clock.Stop();

        Assert.Equal(40, marked);
        Assert.All(all, n => Assert.NotNull(n.ReadAt));
        Assert.Equal(DateTime.UtcNow.AddDays(-1).Date, all.Single(n => n.NotificationId == "already").ReadAt!.Value.Date);
        // One after another this was 40 × 250 ms = 10 s; the request now waits ~0.6 s at most.
        Assert.True(clock.ElapsedMilliseconds < 2_500, $"took {clock.ElapsedMilliseconds} ms");
        Assert.True(peak <= 6, $"peak {peak} concurrent writes");

        // The rest finish in the background.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (written.Count < 40 && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Equal(40, written.Distinct().Count());
    }

    [Fact]
    public async Task MarkAllRead_AFailedWriteIsReportedAndTheOthersStillHappen()
    {
        var all = Enumerable.Range(0, 5).Select(i => new AgentNotification { NotificationId = $"n{i}" }).ToList();
        var errors = new ConcurrentBag<Exception>();
        var written = new ConcurrentBag<string>();
        int marked = await global::Omnipotent.Services.KliveAgent.KliveAgent.MarkAllReadAsync(all, n =>
        {
            if (n.NotificationId == "n2") throw new IOException("disk said no");
            written.Add(n.NotificationId);
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(2), ex => errors.Add(ex));

        Assert.Equal(5, marked);
        Assert.Single(errors);
        Assert.Equal(4, written.Count);
        Assert.Equal(0, await global::Omnipotent.Services.KliveAgent.KliveAgent.MarkAllReadAsync(
            all, _ => Task.CompletedTask, TimeSpan.FromSeconds(1), _ => { }));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value
            && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    // ── conversation files (live: one timed out at 60 s in the start-up storm and went missing) ──

    [Fact]
    public async Task ConversationFile_IsReadDirectlyOnceAWriterLetsGo()
    {
        string root = TempRoot("conversation-read");
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "conv_slow.json");
            File.WriteAllText(path, JsonConvert.SerializeObject(new AgentConversation
            {
                ConversationId = "conv_slow",
                Messages = new List<AgentMessage> { new() { Role = AgentMessageRole.User, Content = "kept" } },
            }));

            // Something holds the file exclusively for a moment (an atomic replace in progress).
            var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            _ = Task.Run(async () => { await Task.Delay(700); holder.Dispose(); });

            var conversation = await global::Omnipotent.Services.KliveAgent.KliveAgent.ReadConversationFileAsync(
                path, attempts: 3, timeout: TimeSpan.FromSeconds(5));
            Assert.NotNull(conversation);
            Assert.Equal("conv_slow", conversation!.ConversationId);
            Assert.Equal("kept", conversation.Messages.Single().Content);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ConversationFile_ThatStaysUnreadableThrowsInsteadOfReadingAsEmpty()
    {
        string root = TempRoot("conversation-locked");
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "conv_locked.json");
            File.WriteAllText(path, "{}");
            using var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            await Assert.ThrowsAnyAsync<IOException>(() => global::Omnipotent.Services.KliveAgent.KliveAgent
                .ReadConversationFileAsync(path, attempts: 2, timeout: TimeSpan.FromSeconds(2)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    // ── scheduled tasks (live: 7 restored at start-up, shown nowhere) ──

    [Fact]
    public void ScheduleSnapshot_ListsActiveSoonestFirstAndRecentNewestFirst()
    {
        var now = DateTime.UtcNow;
        var tasks = new[]
        {
            new AgentScheduledTask { Id = "later", Instruction = "check later", DueAtUtc = now.AddHours(5) },
            new AgentScheduledTask { Id = "soon", Instruction = "check soon", DueAtUtc = now.AddMinutes(5), RepeatEvery = TimeSpan.FromHours(24), FiredCount = 3 },
            new AgentScheduledTask { Id = "done-old", Instruction = "x", Enabled = false, LastFiredAt = now.AddDays(-3) },
            new AgentScheduledTask { Id = "done-new", Instruction = "y", Enabled = false, LastFiredAt = now.AddHours(-1), LastOutcome = "ok" },
        };

        var json = JObject.Parse(JsonConvert.SerializeObject(
            KliveAgentScheduler.BuildSnapshot(tasks, id => id == "soon", recentLimit: 20)));

        Assert.Equal(new[] { "soon", "later" }, json["active"]!.Select(t => (string)t["id"]!));
        Assert.Equal(new[] { "done-new", "done-old" }, json["recent"]!.Select(t => (string)t["id"]!));
        var soon = json["active"]![0]!;
        Assert.Equal(86_400, (long)soon["repeatEverySeconds"]!);
        Assert.True((bool)soon["firing"]!);
        Assert.Equal(JTokenType.Null, json["active"]![1]!["repeatEverySeconds"]!.Type);
        Assert.Equal(64, (int)json["maxActive"]!);
    }
}
