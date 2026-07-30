using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects;

/// <summary>
/// Cross-wake repetition was invisible. The convergence guard keys on the raw
/// <c>toolName + "|" + argsJson</c> string and its counter is declared inside the wake loop, so one changed
/// character defeated it and a fresh wake reset it entirely — which is exactly how a project spends a day
/// re-attempting yesterday's failure.
///
/// The attempt ledger is the durable, automatic counterpart: keyed by INTENT, written on every tool result
/// with no model cooperation, and seeded into every wake. It warns; it never blocks, because an intent that
/// failed three times can still be right once an external condition changes.
/// </summary>
public sealed class ProjectAttemptLedgerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "omnipotent-attempt-tests", Guid.NewGuid().ToString("N"));

    private ProjectRuntimeStateStore NewStore() => new(_ => { }, root);

    // ── key normalisation: same intent collides, different intent does not ──

    [Fact]
    public void KeyIgnoresArgumentOrderAndWhitespaceAndCase()
    {
        string a = ProjectAttemptKey.For("computer_navigate", "{\"url\":\"https://Example.com/Login\",\"waitFor\":\"form\"}");
        string b = ProjectAttemptKey.For("computer_navigate", "{ \"waitFor\" : \"form\" ,  \"url\" : \"https://example.com/login\" }");
        Assert.Equal(a, b);
    }

    [Fact]
    public void KeyIgnoresRegeneratedBrowserRefs()
    {
        // Element refs are re-minted by every inspect, so the same logical click never looked identical.
        string first = ProjectAttemptKey.For("computer_click_browser_control", "{\"ref\":\"e17\",\"label\":\"Sign up\"}");
        string second = ProjectAttemptKey.For("computer_click_browser_control", "{\"ref\":\"e42\",\"label\":\"Sign up\"}");
        Assert.Equal(first, second);
    }

    [Fact]
    public void KeyIgnoresOneTimeVerificationCodes()
    {
        string first = ProjectAttemptKey.For("computer_type", "{\"text\":\"submit\",\"code\":\"114522\"}");
        string second = ProjectAttemptKey.For("computer_type", "{\"text\":\"submit\",\"code\":\"998271\"}");
        Assert.Equal(first, second);
    }

    [Fact]
    public void NearbyClicksCollide_DistantClicksDoNot()
    {
        string atOrigin = ProjectAttemptKey.For("computer_click", "{\"x\":100,\"y\":200}");
        string nudged = ProjectAttemptKey.For("computer_click", "{\"x\":104,\"y\":198}");
        string elsewhere = ProjectAttemptKey.For("computer_click", "{\"x\":700,\"y\":900}");
        Assert.Equal(atOrigin, nudged);
        Assert.NotEqual(atOrigin, elsewhere);
    }

    [Fact]
    public void DifferentIntentKeepsADifferentKey()
    {
        string login = ProjectAttemptKey.For("computer_navigate", "{\"url\":\"https://example.com/login\"}");
        string signup = ProjectAttemptKey.For("computer_navigate", "{\"url\":\"https://example.com/signup\"}");
        Assert.NotEqual(login, signup);
    }

    [Fact]
    public void DifferentContentIsADifferentAttempt()
    {
        // Iterating on a file is progress, not repetition — the full value is hashed, not a prefix.
        string v1 = ProjectAttemptKey.For("write_file", "{\"path\":\"work/report.md\",\"content\":\"first draft\"}");
        string v2 = ProjectAttemptKey.For("write_file", "{\"path\":\"work/report.md\",\"content\":\"second draft\"}");
        Assert.NotEqual(v1, v2);
    }

    [Fact]
    public void OpDiscriminatesFoldedTools_AndStaysReadable()
    {
        string set = ProjectAttemptKey.For("update_checkpoint", "{\"op\":\"upsert_fact\",\"key\":\"k\"}");
        string dead = ProjectAttemptKey.For("update_checkpoint", "{\"op\":\"record_dead_end\",\"key\":\"k\"}");
        Assert.NotEqual(set, dead);
        Assert.StartsWith("update_checkpoint:upsert_fact:", set, StringComparison.Ordinal);
    }

    [Fact]
    public void UnparseableArgumentsStillProduceAStableKey()
    {
        string a = ProjectAttemptKey.For("grep", "not json at all");
        string b = ProjectAttemptKey.For("grep", "not json at all");
        Assert.Equal(a, b);
        Assert.NotEmpty(a);
    }

    // ── repeatable tools are recorded but never nagged about ──

    [Theory]
    [InlineData("klivemail_wait_for_code")]
    [InlineData("computer_read_screen")]
    [InlineData("computer_screenshot")]
    [InlineData("computer_browser_inspect")]
    [InlineData("query_events")]
    [InlineData("list_files")]
    [InlineData("get_checkpoint")]
    public void PollShapedToolsAreNeverWarnedAbout(string tool)
    {
        Assert.True(ProjectAttemptKey.IsDeliberatelyRepeatable(tool));
        var prior = new ProjectAttempt { FailureCount = 9, Count = 9, LastSucceeded = false };
        Assert.False(ProjectAttemptWarning.ShouldWarn(prior, succeeded: false, tool));
    }

    [Fact]
    public void ActionToolsAreNotExemptFromWarnings()
    {
        Assert.False(ProjectAttemptKey.IsDeliberatelyRepeatable("computer_navigate"));
        var prior = new ProjectAttempt { FailureCount = 2, Count = 2, LastSucceeded = false };
        Assert.True(ProjectAttemptWarning.ShouldWarn(prior, succeeded: false, "computer_navigate"));
    }

    // ── warning thresholds ──

    [Fact]
    public void FirstFailureDoesNotWarn()
    {
        Assert.False(ProjectAttemptWarning.ShouldWarn(null, succeeded: false, "computer_navigate"));
        var once = new ProjectAttempt { FailureCount = 1, Count = 1, LastSucceeded = false };
        Assert.False(ProjectAttemptWarning.ShouldWarn(once, succeeded: false, "computer_navigate"));
    }

    [Fact]
    public void WarningStartsAtTheSecondPriorFailure_AndEscalates()
    {
        var twice = new ProjectAttempt
        {
            FailureCount = 2, Count = 2, LastSucceeded = false, LastOutcome = "403 Forbidden",
            FirstAt = DateTime.UtcNow.AddHours(-9), LastAt = DateTime.UtcNow.AddHours(-4),
        };
        Assert.True(ProjectAttemptWarning.ShouldWarn(twice, succeeded: false, "computer_navigate"));
        string mild = ProjectAttemptWarning.Render(twice);
        Assert.Contains("REPEAT", mild, StringComparison.Ordinal);
        Assert.Contains("403 Forbidden", mild, StringComparison.Ordinal);
        Assert.Contains("record_dead_end", mild, StringComparison.Ordinal);

        var many = new ProjectAttempt { FailureCount = 5, Count = 5, LastSucceeded = false, LastOutcome = "403 Forbidden" };
        Assert.Contains("stop attempting this", ProjectAttemptWarning.Render(many), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASuccessClearsTheWarnedState()
    {
        var store = NewStore();
        const string pid = "p1";
        string key = ProjectAttemptKey.For("computer_navigate", "{\"url\":\"https://example.com\"}");

        store.RecordAttempt(pid, key, "computer_navigate", "navigate", succeeded: false, "timeout", "w1", "commander");
        store.RecordAttempt(pid, key, "computer_navigate", "navigate", succeeded: false, "timeout", "w1", "commander");
        Assert.Single(store.GetRepeatedFailures(pid));

        store.RecordAttempt(pid, key, "computer_navigate", "navigate", succeeded: true, "200 OK", "w2", "commander");
        Assert.Empty(store.GetRepeatedFailures(pid));

        var prior = store.GetAttempt(pid, key);
        Assert.NotNull(prior);
        Assert.True(prior!.LastSucceeded);
        Assert.Equal(3, prior.Count);
        Assert.Equal(2, prior.FailureCount);
    }

    // ── durability ──

    [Fact]
    public void PriorSnapshotIsReturned_NotThePostWriteState()
    {
        var store = NewStore();
        const string pid = "p1";
        string key = ProjectAttemptKey.For("web_search", "{\"query\":\"x\"}");

        Assert.Null(store.RecordAttempt(pid, key, "web_search", "search", false, "0 hits", "w1", "commander"));
        var afterFirst = store.RecordAttempt(pid, key, "web_search", "search", false, "0 hits", "w1", "commander");
        Assert.NotNull(afterFirst);
        Assert.Equal(1, afterFirst!.FailureCount); // the caller sees what had happened BEFORE this attempt
    }

    [Fact]
    public void LedgerSurvivesAProcessRestart()
    {
        const string pid = "p1";
        string key = ProjectAttemptKey.For("computer_navigate", "{\"url\":\"https://example.com/signup\"}");
        var first = NewStore();
        first.RecordAttempt(pid, key, "computer_navigate", "navigate signup", false, "captcha wall", "w1", "commander");
        first.RecordAttempt(pid, key, "computer_navigate", "navigate signup", false, "captcha wall", "w1", "commander");

        // A brand-new store reading the same directory is what a fresh wake (or a restart) sees.
        var reloaded = NewStore();
        var failures = reloaded.GetRepeatedFailures(pid);
        Assert.Single(failures);
        Assert.Equal(2, failures[0].FailureCount);
        Assert.Contains("captcha wall", failures[0].LastOutcome, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedFailuresAreSeededIntoEveryWake()
    {
        var store = NewStore();
        const string pid = "p1";
        string key = ProjectAttemptKey.For("computer_navigate", "{\"url\":\"https://example.com/signup\"}");
        store.RecordAttempt(pid, key, "computer_navigate", "computer_navigate(url=…/signup)", false, "403 Forbidden", "w1", "commander");
        store.RecordAttempt(pid, key, "computer_navigate", "computer_navigate(url=…/signup)", false, "403 Forbidden", "w2", "commander");

        string seeded = store.DescribeForWake(pid);
        Assert.Contains("already tried", seeded, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("403 Forbidden", seeded, StringComparison.Ordinal);
        Assert.Contains("failed 2× of 2", seeded, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleFailuresAreNotSeeded_OnlyRepeatedOnes()
    {
        var store = NewStore();
        const string pid = "p1";
        store.RecordAttempt(pid, ProjectAttemptKey.For("grep", "{\"pattern\":\"x\"}"), "grep", "grep(x)", false, "no matches", "w1", "commander");
        Assert.DoesNotContain("already tried", store.DescribeForWake(pid), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkersShareTheProjectLedger()
    {
        var store = NewStore();
        const string pid = "p1";
        string key = ProjectAttemptKey.For("computer_navigate", "{\"url\":\"https://example.com/signup\"}");
        store.RecordAttempt(pid, key, "computer_navigate", "navigate signup", false, "blocked", "w1", "agent-a");
        store.RecordAttempt(pid, key, "computer_navigate", "navigate signup", false, "blocked", "w2", "agent-b");

        // Worker seeds previously carried none of this, so each new worker re-attempted the same wall.
        string workerView = store.DescribeProjectKnowledgeForWorker(pid);
        Assert.Contains("already tried", workerView, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("blocked", workerView, StringComparison.Ordinal);
    }

    [Fact]
    public void EvictionKeepsFailuresOverSuccesses()
    {
        var store = NewStore();
        const string pid = "p1";
        // One intent that keeps failing, then flood the ledger with successful ones.
        string sore = ProjectAttemptKey.For("computer_navigate", "{\"url\":\"https://example.com/sore\"}");
        store.RecordAttempt(pid, sore, "computer_navigate", "the sore spot", false, "always 403", "w1", "commander");
        store.RecordAttempt(pid, sore, "computer_navigate", "the sore spot", false, "always 403", "w1", "commander");
        for (int i = 0; i < ProjectRuntimeStateStore.MaxAttempts + 20; i++)
            store.RecordAttempt(pid, ProjectAttemptKey.For("grep", "{\"pattern\":\"p" + i + "\"}"),
                "grep", "grep p" + i, true, "ok", "w1", "commander");

        var kept = store.GetAttempt(pid, sore);
        Assert.NotNull(kept);
        Assert.Equal(2, kept!.FailureCount);
    }

    [Fact]
    public void BlankKeyIsIgnoredRatherThanStored()
    {
        var store = NewStore();
        Assert.Null(store.RecordAttempt("p1", "", "grep", "grep", false, "x", "w1", "commander"));
        Assert.Empty(store.GetRepeatedFailures("p1"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
    }
}
