using Omnipotent.Services.KliveAgent;

namespace Omnipotent.Tests.KliveAgent;

/// <summary>
/// The tool-speed system: what each tool really takes is recorded, the fastest tool for a job is put in
/// front of the model, a call is waited for only as long as its own history says it needs, and a call
/// that outlives that keeps running as a background job instead of holding the run or being lost.
/// </summary>
public sealed class KliveAgentToolSpeedTests
{
    private static void RecordMany(KliveAgentToolSpeed speed, string tool, string? args, long ms, int count,
        ToolCallOutcome outcome = ToolCallOutcome.Ok)
    {
        for (int i = 0; i < count; i++) speed.Record(tool, args, ms, outcome);
    }

    // ── expectations and measurements ──

    [Theory]
    [InlineData("read_file", ToolSpeedTier.Instant)]
    [InlineData("grep", ToolSpeedTier.Fast)]
    [InlineData("web_search", ToolSpeedTier.Slow)]
    [InlineData("klivemail_wait_for_email", ToolSpeedTier.Waits)]
    [InlineData("wait_for", ToolSpeedTier.Waits)]
    [InlineData("tool_jobs", ToolSpeedTier.Waits)]
    [InlineData("some_tool_nobody_has_heard_of", ToolSpeedTier.Fast)]
    public void WithNoRecord_TheExpectationStandsIn(string tool, ToolSpeedTier expected)
    {
        var estimate = new KliveAgentToolSpeed().Estimate(tool);
        Assert.False(estimate.Measured);
        Assert.Equal(expected, estimate.Tier);
    }

    [Fact]
    public void AMeasurementReplacesTheExpectation_OnceThereAreEnoughCalls()
    {
        var speed = new KliveAgentToolSpeed();
        RecordMany(speed, "read_file", null, 4_000, KliveAgentToolSpeed.MinSamplesToTrust - 1);
        Assert.False(speed.Estimate("read_file").Measured);

        speed.Record("read_file", null, 4_000, ToolCallOutcome.Ok);
        var estimate = speed.Estimate("read_file");
        Assert.True(estimate.Measured);
        Assert.Equal(4_000, estimate.TypicalMs);
        Assert.Equal(ToolSpeedTier.Moderate, estimate.Tier);
    }

    [Theory]
    [InlineData(50, 120, 0, 20, ToolSpeedTier.Instant)]
    [InlineData(400, 900, 0, 20, ToolSpeedTier.Fast)]
    [InlineData(3_000, 6_000, 0, 20, ToolSpeedTier.Moderate)]
    [InlineData(12_000, 20_000, 0, 20, ToolSpeedTier.Slow)]
    [InlineData(500, 130_000, 0, 20, ToolSpeedTier.HangProne)]  // one call in ten takes over two minutes
    [InlineData(500, 900, 3, 20, ToolSpeedTier.HangProne)]      // three of twenty overran
    [InlineData(500, 900, 1, 20, ToolSpeedTier.Fast)]           // a single overrun is not a pattern
    public void Tiers_FollowTypicalSlowAndOverruns(long typical, long slow, int overruns, int samples, ToolSpeedTier expected)
    {
        Assert.Equal(expected, KliveAgentToolSpeed.TierOf(typical, slow, overruns, samples));
    }

    [Fact]
    public void OldCallsFallOut_SoTheRecordFollowsTheServerAsItIsNow()
    {
        var speed = new KliveAgentToolSpeed();
        RecordMany(speed, "grep", null, 30_000, KliveAgentToolSpeed.SamplesKept);
        RecordMany(speed, "grep", null, 100, KliveAgentToolSpeed.SamplesKept);
        var estimate = speed.Estimate("grep");
        Assert.Equal(KliveAgentToolSpeed.SamplesKept, estimate.Samples);
        Assert.Equal(100, estimate.TypicalMs);
    }

    // ── which operation a call is ──

    [Theory]
    [InlineData("web_search", "{\"query\":\"x\",\"fetchTop\":0}", "snippets")]
    [InlineData("web_search", "{\"query\":\"x\",\"fetchTop\":2}", "fetch")]
    [InlineData("web_search", "{\"query\":\"x\"}", "fetch")]                              // fetchTop defaults to 2
    [InlineData("search_knowledge", "{\"query\":\"x\",\"includeMessages\":false}", "noMessages")]
    [InlineData("search_knowledge", "{\"query\":\"x\"}", null)]
    [InlineData("read_file", "{\"path\":\"Omnipotent/A.cs\"}", null)]                     // a path is not an operation
    [InlineData("grep", "{\"pattern\":\"x\",\"path\":\"Omnipotent\"}", null)]
    [InlineData("tool_jobs", "{\"op\":\"Cancel\",\"id\":\"job-1\"}", "cancel")]
    [InlineData("omni_api", "{\"method\":\"GET\",\"path\":\"/orders/123?full=1\"}", "get /orders/#")]
    public void OperationKey_IsOnlyWhatChangesHowLongACallTakes(string tool, string args, string? expected)
    {
        Assert.Equal(expected, KliveAgentToolSpeed.OperationKey(tool, args));
    }

    [Fact]
    public void Normalize_FoldsIdsAndNumbers_SoTheRecordDoesNotGrowAKeyPerCall()
    {
        Assert.Equal("get /runs/{id}/steps/#", KliveAgentToolSpeed.Normalize("GET /runs/3f2504e0-4f89-11d3-9a0c-0305e82c3301/steps/17?x=1"));
        Assert.Equal("call trader {id}", KliveAgentToolSpeed.Normalize("call Trader 0123456789abcdef0123"));
        Assert.Null(KliveAgentToolSpeed.Normalize("   "));
    }

    [Fact]
    public void AnOperationHasItsOwnRecord_AndTheToolKeepsTheWhole()
    {
        var speed = new KliveAgentToolSpeed();
        RecordMany(speed, "web_search", "{\"fetchTop\":0}", 900, 10);
        RecordMany(speed, "web_search", "{\"fetchTop\":2}", 12_000, 10);
        Assert.Equal(900, speed.Estimate("web_search", "{\"fetchTop\":0}").TypicalMs);
        Assert.Equal(12_000, speed.Estimate("web_search", "{\"fetchTop\":3}").TypicalMs);
        Assert.Equal(20, speed.BuildReport().Tools.Single(t => t.Key == "web_search").Samples);
    }

    // ── patience ──

    [Fact]
    public void Patience_IsFourTimesWhatGoodCallsTake_WithinTheFloorAndTheDeadline()
    {
        var speed = new KliveAgentToolSpeed();
        RecordMany(speed, "grep", null, 200, 20);
        Assert.Equal(TimeSpan.FromSeconds(20), speed.PatienceFor("grep", null, capSeconds: 600, floorSeconds: 20)); // floor

        RecordMany(speed, "omni_api", null, 30_000, 20);
        Assert.Equal(TimeSpan.FromSeconds(120), speed.PatienceFor("omni_api", null, capSeconds: 600, floorSeconds: 20));
        Assert.Equal(TimeSpan.FromSeconds(90), speed.PatienceFor("omni_api", null, capSeconds: 90, floorSeconds: 20)); // deadline
    }

    [Fact]
    public void Patience_IgnoresTheHangs_ItIsMeantToCutShort()
    {
        var speed = new KliveAgentToolSpeed();
        RecordMany(speed, "omnitrader", "{\"op\":\"research\"}", 1_000, 16);
        RecordMany(speed, "omnitrader", "{\"op\":\"research\"}", 600_000, 4, ToolCallOutcome.Overran);
        // 4 x 1 s, raised to the 20 s floor; the 10-minute hangs do not set it.
        Assert.Equal(TimeSpan.FromSeconds(20), speed.PatienceFor("omnitrader", "{\"op\":\"research\"}", 600, 20));
    }

    [Theory]
    [InlineData("klivemail_wait_for_email", 600)]
    [InlineData("wait_for", 600)]
    [InlineData("tool_jobs", 600)]
    [InlineData("grep", 0)]   // the deadline setting at 0 turns limits off
    public void Patience_IsUnlimited_ForToolsThatWaitOnPurposeOrWhenTurnedOff(string tool, int cap)
    {
        Assert.Equal(TimeSpan.Zero, new KliveAgentToolSpeed().PatienceFor(tool, null, cap, 20));
    }

    [Fact]
    public void Patience_WithNoRecord_ComesFromTheExpectedTier()
    {
        var speed = new KliveAgentToolSpeed();
        Assert.Equal(TimeSpan.FromSeconds(20), speed.PatienceFor("read_file", null, 600, 20));   // instant: 4 x 1 s, floor 20 s
        Assert.Equal(TimeSpan.FromSeconds(480), speed.PatienceFor("web_search", null, 600, 20)); // slow: 4 x 120 s
    }

    // ── steering ──

    [Fact]
    public void Gate_TurnsDownAHangProneTool_WhenAMuchFasterOneDoesTheJob()
    {
        var speed = new KliveAgentToolSpeed();
        RecordMany(speed, "web_fetch", null, 40_000, 10);
        RecordMany(speed, "web_fetch", null, 600_000, 4, ToolCallOutcome.Overran);
        RecordMany(speed, "read_knowledge_doc", null, 60, 10);

        var advice = speed.GateAdvice("web_fetch", "{\"url\":\"https://example.com\"}");
        Assert.NotNull(advice);
        Assert.StartsWith("[NOT RUN | slow tool] web_fetch", advice);
        Assert.Contains("read_knowledge_doc", advice);
        Assert.Contains("call it again unchanged", advice);
    }

    [Fact]
    public void Gate_RunsTheCall_WhenNothingFasterDoesTheJob_OrTheRecordIsThin()
    {
        var speed = new KliveAgentToolSpeed();
        Assert.Null(speed.GateAdvice("web_fetch", null));   // no record: never turned down on a guess

        // A hang-prone service operation: omniservice and omni_api reach the same method, so neither is
        // offered as the faster way.
        RecordMany(speed, "omni_api", "{\"method\":\"GET\",\"path\":\"/omnitrader/research\"}", 600_000, 10, ToolCallOutcome.Overran);
        RecordMany(speed, "omniservice", null, 50, 10);
        Assert.Null(speed.GateAdvice("omni_api", "{\"method\":\"GET\",\"path\":\"/omnitrader/research\"}"));
    }

    [Fact]
    public void Gate_LeavesFastTools_Alone()
    {
        var speed = new KliveAgentToolSpeed();
        RecordMany(speed, "grep", null, 300, 20);
        Assert.Null(speed.GateAdvice("grep", null));
    }

    [Fact]
    public void SlowResult_NamesTheFasterToolForTheSameJob()
    {
        var speed = new KliveAgentToolSpeed();
        RecordMany(speed, "web_search", "{\"fetchTop\":0}", 900, 10);
        string? hint = speed.SlowCallHint("web_search", "{\"query\":\"x\",\"fetchTop\":2}", 14_000);
        Assert.NotNull(hint);
        Assert.StartsWith("[speed] web_search took 14s", hint);
        Assert.Contains("web_search fetchTop:0", hint);

        Assert.Null(speed.SlowCallHint("web_search", "{\"fetchTop\":2}", 1_000));       // quick enough
        Assert.Null(speed.SlowCallHint("klivemail_wait_for_email", null, 300_000));      // waiting is its job
    }

    [Fact]
    public void ScriptsAreNeverOfferedAsTheFasterWay()
    {
        var speed = new KliveAgentToolSpeed();
        RecordMany(speed, "execute_csharp", null, 10, 20);
        Assert.Null(speed.SlowCallHint("read_file", null, 60_000));
    }

    [Fact]
    public void PromptBlock_RanksEachGoalFastestFirst_AndNamesWhatIsSlowNow()
    {
        var speed = new KliveAgentToolSpeed();
        RecordMany(speed, "execute_csharp", null, 1_400, 10);
        RecordMany(speed, "grep", null, 380, 10);
        RecordMany(speed, "web_fetch", null, 45_000, 10);
        RecordMany(speed, "web_fetch", null, 600_000, 3, ToolCallOutcome.Overran);

        string block = speed.BuildPromptBlock(computerUse: false);
        Assert.StartsWith("[Tool speed]", block);
        Assert.Contains("- search code by content: grep 400ms → execute_csharp 1.4s", block);
        Assert.Contains("web_fetch", block);
        Assert.Contains("(hang-prone)", block);
        Assert.Contains("Slow right now: web_fetch", block);
        Assert.Contains("start_background_tool", block);
        Assert.DoesNotContain("act on a web page", block);
        Assert.Contains("act on a web page", speed.BuildPromptBlock(computerUse: true));
    }

    [Fact]
    public void PromptBlock_WithNoRecord_StillGivesTheOrder()
    {
        string block = new KliveAgentToolSpeed().BuildPromptBlock(computerUse: false);
        Assert.Contains("- read a source file: read_file → execute_csharp", block);
        Assert.DoesNotContain("Slow right now", block);
    }

    [Theory]
    [InlineData(5, "<10ms")]
    [InlineData(44, "40ms")]
    [InlineData(380, "400ms")]
    [InlineData(1_440, "1.4s")]
    [InlineData(45_000, "45s")]
    [InlineData(600_000, "10min")]
    public void Times_AreRoundedToAFewSteps(long ms, string expected)
    {
        Assert.Equal(expected, KliveAgentToolSpeed.FormatMs(ms));
    }

    [Fact]
    public void Describe_ListsTheRecordAndTheRanking()
    {
        var speed = new KliveAgentToolSpeed();
        RecordMany(speed, "grep", null, 380, 10);
        string text = speed.Describe();
        Assert.Contains("- grep: 10 calls · typical 400ms", text);
        Assert.Contains("Fastest tool per goal:", text);
        Assert.Contains("No recorded tool or operation matches 'nothing'", speed.Describe("nothing"));
    }

    [Fact]
    public async Task TheRecord_SurvivesARestart()
    {
        string path = Path.Combine(Path.GetTempPath(), "kliveagent-toolspeed-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var speed = new KliveAgentToolSpeed(path);
            RecordMany(speed, "grep", null, 380, 12);
            await speed.SaveAsync();

            var reloaded = new KliveAgentToolSpeed(path);
            await reloaded.LoadAsync();
            var estimate = reloaded.Estimate("grep");
            Assert.True(estimate.Measured);
            Assert.Equal(12, estimate.Samples);
            Assert.Equal(380, estimate.TypicalMs);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task ADamagedRecord_StartsEmpty()
    {
        string path = Path.Combine(Path.GetTempPath(), "kliveagent-toolspeed-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, "{ not json");
        try
        {
            var speed = new KliveAgentToolSpeed(path);
            await speed.LoadAsync();
            Assert.False(speed.Estimate("grep").Measured);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // ── waiting for a call ──

    [Fact]
    public async Task AwaitTool_Finished_Overran_Stopped()
    {
        Assert.Equal(KliveAgentBrain.ToolWait.Finished,
            await KliveAgentBrain.AwaitToolAsync(Task.CompletedTask, TimeSpan.FromSeconds(5), CancellationToken.None));

        var never = new TaskCompletionSource();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(KliveAgentBrain.ToolWait.Overran,
            await KliveAgentBrain.AwaitToolAsync(never.Task, TimeSpan.FromMilliseconds(100), CancellationToken.None));
        Assert.True(timer.ElapsedMilliseconds < 5_000);

        using var stop = new CancellationTokenSource(100);
        Assert.Equal(KliveAgentBrain.ToolWait.Stopped,
            await KliveAgentBrain.AwaitToolAsync(never.Task, TimeSpan.Zero, stop.Token));
    }

    [Fact]
    public void MovedCall_SaysSoOnItsFirstLine_AndHowToCollectIt()
    {
        var usual = new ToolSpeedEstimate("omni_api", ToolSpeedTier.Fast, true, 20, 800, 1_500, 2_000, 0, 0);
        string text = KliveAgentBrain.MovedToBackgroundText("omni_api", "job-3", TimeSpan.FromSeconds(20), usual);
        Assert.StartsWith("omni_api was still running after 20s (it usually takes 800ms)", text);
        Assert.Contains("job-3", text);
        Assert.Contains("tool_jobs", text);
        Assert.DoesNotContain("\n", text);
    }

    // ── background jobs ──

    [Fact]
    public async Task FinishedJob_IsAnnouncedOnce_WithWhatItFound()
    {
        var jobs = new KliveAgentToolJobs();
        var done = new TaskCompletionSource<(bool ok, string output)>();
        var job = jobs.Add("web_search", "{\"query\":\"x\"}", done.Task, DateTime.UtcNow, moved: true);
        Assert.Equal("job-1", job.Id);
        Assert.Empty(jobs.TakeNotices());

        done.SetResult((true, "3 results for x\nmore lines"));
        await job.Work;
        var notices = jobs.TakeNotices();
        Assert.Single(notices);
        Assert.StartsWith("[Background job-1 · web_search finished after", notices[0]);
        Assert.Contains("3 results for x", notices[0]);
        Assert.DoesNotContain("more lines", notices[0]);
        Assert.Empty(jobs.TakeNotices());
    }

    [Fact]
    public async Task Result_Wait_List_Cancel()
    {
        var jobs = new KliveAgentToolJobs();
        var slow = new TaskCompletionSource<(bool ok, string output)>();
        using var cancel = new CancellationTokenSource();
        var own = jobs.Add("klivemail_wait_for_email", null, slow.Task, DateTime.UtcNow, moved: false, cancel);
        var moved = jobs.Add("omni_api", null, new TaskCompletionSource<(bool ok, string output)>().Task, DateTime.UtcNow, moved: true);

        Assert.Contains("is still running", await jobs.ApplyAsync("{\"op\":\"result\",\"id\":\"job-1\"}", CancellationToken.None));
        Assert.Contains("2 running", await jobs.ApplyAsync("{\"op\":\"list\"}", CancellationToken.None));

        _ = Task.Delay(100).ContinueWith(_ => slow.SetResult((true, "code 123456")));
        string waited = await jobs.ApplyAsync("{\"op\":\"wait\",\"id\":\"1\",\"waitSeconds\":10}", CancellationToken.None);
        Assert.StartsWith("[OK | job-1 · klivemail_wait_for_email", waited);
        Assert.Contains("code 123456", waited);
        Assert.Empty(jobs.TakeNotices());   // collected, so not announced again

        Assert.Contains("cannot be stopped on its own", await jobs.ApplyAsync("{\"op\":\"cancel\",\"id\":\"job-2\"}", CancellationToken.None));
        Assert.Contains("No background job 'job-9'", await jobs.ApplyAsync("{\"op\":\"result\",\"id\":\"job-9\"}", CancellationToken.None));
        Assert.Contains("not an operation", await jobs.ApplyAsync("{\"op\":\"explode\"}", CancellationToken.None));
    }

    [Fact]
    public async Task Cancel_StopsAJobThatHasItsOwnToken()
    {
        var jobs = new KliveAgentToolJobs();
        var cancel = new CancellationTokenSource();
        var work = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromMinutes(5), cancel.Token); return (true, "late"); }
            catch (OperationCanceledException) { return (false, "cancelled"); }
        });
        jobs.Add("search_knowledge", null, work, DateTime.UtcNow, moved: false, cancel);
        Assert.Equal("Cancelled job-1 (search_knowledge).", await jobs.ApplyAsync("{\"op\":\"cancel\",\"id\":\"job-1\"}", CancellationToken.None));
        var (ok, _) = await work.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(ok);
        Assert.Empty(jobs.TakeNotices());   // a cancelled job is not announced
    }

    [Fact]
    public async Task WaitWithoutId_ReturnsTheFirstToFinish()
    {
        var jobs = new KliveAgentToolJobs();
        jobs.Add("a", null, new TaskCompletionSource<(bool ok, string output)>().Task, DateTime.UtcNow, moved: true);
        var quick = new TaskCompletionSource<(bool ok, string output)>();
        jobs.Add("b", null, quick.Task, DateTime.UtcNow, moved: true);
        _ = Task.Delay(50).ContinueWith(_ => quick.SetResult((true, "b done")));
        string text = await jobs.ApplyAsync("{\"op\":\"wait\",\"waitSeconds\":10}", CancellationToken.None);
        Assert.Contains("job-2", text);
        Assert.Contains("b done", text);
    }

    [Fact]
    public void RunningSince_IsWhatTheFinalAnswerMustAccountFor()
    {
        var jobs = new KliveAgentToolJobs();
        var before = DateTime.UtcNow.AddMinutes(-5);
        jobs.Add("old", null, new TaskCompletionSource<(bool ok, string output)>().Task, before, moved: true);
        jobs.Add("new", null, new TaskCompletionSource<(bool ok, string output)>().Task, DateTime.UtcNow, moved: true);
        var running = jobs.RunningSince(DateTime.UtcNow.AddMinutes(-1));
        Assert.Single(running);
        Assert.Equal("new", running[0].Tool);
    }

    [Theory]
    [InlineData("execute_csharp")]
    [InlineData("wait_for")]
    [InlineData("computer_click")]
    [InlineData("tool_jobs")]
    public void StartBackgroundTool_RefusesToolsThatMustRunInOrder(string tool)
    {
        var globals = new ScriptGlobals(null!);
        Assert.Contains("cannot run in the background", KliveAgentBrain.StartBackgroundTool(globals, tool, null));
        Assert.Equal(0, globals.ToolJobs.RunningCount);
    }

    [Fact]
    public async Task StartBackgroundTool_AnswersAtOnce_AndTheJobCarriesTheResult()
    {
        var globals = new ScriptGlobals(null!);
        Assert.Contains("Unknown tool", KliveAgentBrain.StartBackgroundTool(globals, "no_such_tool", null));
        Assert.Contains("'tool' is required", KliveAgentBrain.StartBackgroundTool(globals, null, null));

        string started = KliveAgentBrain.StartBackgroundTool(globals, "tool_speed", "{}");
        Assert.StartsWith("Started tool_speed in the background as job-1", started);
        string result = await globals.ToolJobs.ApplyAsync("{\"op\":\"wait\",\"id\":\"job-1\",\"waitSeconds\":30}", CancellationToken.None);
        Assert.StartsWith("[OK | job-1 · tool_speed", result);
    }

    [Theory]
    [InlineData("grep", null, false)]
    [InlineData("omni_api", "{\"method\":\"POST\",\"path\":\"/x\"}", false)]                          // routes have no approval gate
    [InlineData("omniservice", "{\"op\":\"list\"}", false)]
    [InlineData("omniservice", "{\"op\":\"call\",\"service\":\"NoSuchService\",\"method\":\"X\"}", true)] // unresolved: assume it may
    public void ApprovalWaits_GetTheWholeDeadline_NotTheUsualTime(string tool, string? args, bool expected)
    {
        Assert.Equal(expected, KliveAgentServiceTools.MayAwaitApproval(tool, args));
    }

    // ── the offered surface ──

    [Fact]
    public void NewTools_AreOffered()
    {
        var names = KliveAgentBrain.BuildToolDefinitions(includeComputerUse: false).Select(t => t.function.name).ToList();
        foreach (var tool in new[] { "find_files", "read_data_file", "list_data_directory", "tool_speed", "tool_jobs", "start_background_tool", "grep" })
            Assert.Contains(tool, names);
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void SpeedRule_IsInTheCachedPolicy_ForNativeToolsOnly()
    {
        Assert.Contains(KliveAgentPromptPolicy.SpeedRule, KliveAgentPromptPolicy.Build(nativeTools: true, computerUse: false));
        Assert.DoesNotContain("start_background_tool", KliveAgentPromptPolicy.Build(nativeTools: false, computerUse: false));
        Assert.Contains(KliveAgentPromptPolicy.SpeedRule, KliveAgentPromptPolicy.BuildDetailed(true, false));
    }
}
