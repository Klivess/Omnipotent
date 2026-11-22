using Omnipotent.Services.KliveAgent;

namespace Omnipotent.Tests.KliveAgent;

/// <summary>
/// KliveAgent's scripts are JavaScript (Jint) since 22 Nov 2026, replacing Roslyn C#. These pin what the
/// switch was for: no compile step, every object dynamic (internal types included), top-level await,
/// variables that outlive the script, errors that say what to write, and a timeout that really stops.
/// </summary>
public sealed class KliveAgentJsEngineTests
{
    private static KliveAgentJsSession NewSession(out ScriptGlobals globals, CancellationToken token = default)
    {
        globals = new ScriptGlobals(null!, token);
        var session = new KliveAgentJsSession(globals);
        session.SetGlobal("trader", new FakeTraderForJs());
        return session;
    }

    [Fact]
    public async Task TopLevelAwait_OnADotNetTask_AndTheLastValueIsTheResult()
    {
        var session = NewSession(out _);
        var r = await session.ExecuteAsync("const fresh = await trader.RefreshAsync();\nLog('got', fresh);\nfresh.length");
        Assert.True(r.Success, r.ErrorMessage);
        Assert.StartsWith("got fresh", r.Output);
        Assert.Contains("Result: 5", r.Output);
        Assert.NotNull(r.CompileMs);
    }

    [Fact]
    public async Task InternalTypes_AreFullyDynamic_ListsTakeArrayMethods()
    {
        var session = NewSession(out _);
        var r = await session.ExecuteAsync("Log(trader.Name, trader.positions.Count, trader.Positions.map(p => p.Symbol).join('+'), trader.TotalValue())");
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("OmniTrader 2 AAPL+TSLA 1234.5", r.Output);
    }

    [Fact]
    public async Task TopLevelNames_OutliveTheScript_AndDeclaringAgainReassigns()
    {
        var session = NewSession(out _);
        var first = await session.ExecuteAsync("let total = 40;\nfunction twice(v) { return v * 2 }\nclass Box { constructor(v) { this.v = v } }\nconst {a, b: renamed} = {a: 1, b: 2};");
        Assert.True(first.Success, first.ErrorMessage);
        Assert.Contains("total", first.SessionNote);
        Assert.Contains("twice", first.SessionNote);
        Assert.Contains("renamed", first.SessionNote);

        var second = await session.ExecuteAsync("const total = twice(new Box(total + a).v); Log(total, renamed)");
        Assert.True(second.Success, second.ErrorMessage);
        Assert.Equal("82 2", second.Output);
        Assert.Contains("total", session.DescribeVariables());
    }

    [Fact]
    public async Task AScriptThatThrows_KeepsWhatItAssignedBeforeTheThrow()
    {
        var session = NewSession(out _);
        var failed = await session.ExecuteAsync("const kept = 7;\nthrow new Error('boom');");
        Assert.False(failed.Success);
        Assert.Equal("runtime", failed.FailureKind);
        Assert.Contains("boom", failed.ErrorMessage);

        var after = await session.ExecuteAsync("Log(kept)");
        Assert.Equal("7", after.Output);
    }

    [Fact]
    public async Task AWrongMemberName_NamesTheNearestRealOnes()
    {
        var session = NewSession(out _);
        var r = await session.ExecuteAsync("Log(trader.Psitions.Count)");
        Assert.False(r.Success);
        Assert.Equal("runtime", r.FailureKind);
        Assert.Contains("Positions", r.ErrorMessage);
    }

    [Fact]
    public async Task ASyntaxError_NamesItsLine_AndCSharpIsRecognised()
    {
        var session = NewSession(out _);
        var r = await session.ExecuteAsync("Log(1);\nLog(2);\nlet x = ;");
        Assert.False(r.Success);
        Assert.Equal("compile", r.FailureKind);
        Assert.StartsWith("SyntaxError at line 3:", r.ErrorMessage);

        var csharp = await session.ExecuteAsync("foreach (var p in trader.Positions) Log($\"{p.Symbol}\");");
        Assert.False(csharp.Success);
        Assert.Contains("This looks like C#", csharp.ErrorMessage);
    }

    [Fact]
    public async Task ARunawayLoop_IsReallyStopped_AndTheSessionStillWorks()
    {
        var session = NewSession(out _);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var r = await session.ExecuteAsync("let n = 0; while (true) { n++ }", TimeSpan.FromMilliseconds(400));
        Assert.False(r.Success);
        Assert.Equal("timeout", r.FailureKind);
        Assert.Contains("really was stopped", r.ErrorMessage);
        Assert.True(timer.ElapsedMilliseconds < 10_000);

        var after = await session.ExecuteAsync("Log('still here')");
        Assert.True(after.Success, after.ErrorMessage);
        Assert.Equal("still here", after.Output);
    }

    [Fact]
    public async Task AWaitOnATaskThatNeverEnds_IsStoppedAtTheTimeout()
    {
        var session = NewSession(out _);
        var r = await session.ExecuteAsync("await trader.Never(); Log('unreachable')", TimeSpan.FromMilliseconds(400));
        Assert.False(r.Success);
        Assert.Equal("timeout", r.FailureKind);
    }

    [Fact]
    public async Task StoppingTheRun_StopsTheScript()
    {
        using var stop = new CancellationTokenSource(300);
        var session = NewSession(out _, stop.Token);
        var r = await session.ExecuteAsync("while (true) {}", TimeSpan.FromSeconds(30));
        Assert.False(r.Success);
        Assert.Equal("stopped", r.FailureKind);
    }

    [Fact]
    public async Task Log_PrintsJsObjectsAsJson_AndNumbersAsTheyAre()
    {
        var session = NewSession(out _);
        var r = await session.ExecuteAsync("Log({a: 1, b: [1, 2.5, 'x']}, 42, true, null); console.log('also', [1, 2])");
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("{\"a\":1,\"b\":[1,2.5,\"x\"]} 42 true null\nalso [1,2]", r.Output.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task JsArrays_PassToDotNetArrayParameters()
    {
        var session = NewSession(out _);
        var r = await session.ExecuteAsync("trader.Tag(['a', 'b'])");
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Contains("Result: a,b", r.Output);
    }

    [Fact]
    public async Task OldHelpers_StillWork_SoARememberedRecipeTranslatesEasily()
    {
        var session = NewSession(out _);
        var r = await session.ExecuteAsync("const v = await Aw(trader.RefreshAsync()); Log(Dyn(trader).Name, Raw(v))");
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("OmniTrader fresh", r.Output);
    }

    [Fact]
    public void Prepare_KeepsLineNumbers_AndOnlyRewritesTheTopLevel()
    {
        var p = KliveAgentJsScript.Prepare("let a = 1;\nif (a) { const inner = 2 }\nfunction f() { const local = 3 }");
        Assert.Null(p.Error);
        Assert.Equal(new[] { "a", "f" }, p.Names);
        Assert.Equal(3, p.Code.Split('\n').Length - 1);
        Assert.Contains("const inner = 2", p.Code);
        Assert.Contains("const local = 3", p.Code);
    }

    [Fact]
    public void ConversationSessions_AreKept_AndReused()
    {
        var engine = new KliveAgentJsEngine();
        var (first, globals, reusedFirst) = engine.AcquireConversationSession("c1", () => new ScriptGlobals(null!), TimeSpan.FromMinutes(20));
        Assert.False(reusedFirst);
        var (again, sameGlobals, _) = engine.AcquireConversationSession("c1", () => new ScriptGlobals(null!), TimeSpan.FromMinutes(20));
        Assert.Same(first, again);
        Assert.Same(globals, sameGlobals);

        var (other, _, _) = engine.AcquireConversationSession("c2", () => new ScriptGlobals(null!), TimeSpan.Zero);
        Assert.NotSame(first, other);
        Assert.Equal(0, engine.KeptSessionCount);   // zero keep-time drops everything kept
    }

    [Fact]
    public void ThePrelude_BindsTheScriptHelpers()
    {
        Assert.Contains("var GetService = (...a) => G.GetService(...a);", KliveAgentJsSession.Prelude);
        Assert.Contains("var RunPowerShell = ", KliveAgentJsSession.Prelude);
        Assert.Contains("var Log = ", KliveAgentJsSession.Prelude);
        Assert.DoesNotContain("var TakeOutput", KliveAgentJsSession.Prelude);
    }

    [Fact]
    public void ThePersonality_NoLongerTellsTheModelToWriteCSharp()
    {
        Assert.Equal(KliveAgentPersonality.Default, KliveAgentPersonality.Resolve(KliveAgentPersonality.LegacyDefault));
        Assert.Equal(KliveAgentPersonality.Default, KliveAgentPersonality.Resolve(null));
        Assert.Equal("Be blunt. Use JavaScript scripts.", KliveAgentPersonality.Resolve("Be blunt. Use C# scripts."));
        Assert.DoesNotContain("C#", KliveAgentPersonality.Default);
    }
}

internal sealed class FakeTraderForJs
{
    public string Name => "OmniTrader";
    public List<FakePositionForJs> Positions { get; } = new() { new("AAPL"), new("TSLA") };
    public decimal TotalValue() => 1234.5m;
    public async Task<string> RefreshAsync() { await Task.Delay(10); return "fresh"; }
    public Task<int> Never() => new TaskCompletionSource<int>().Task;
    public string Tag(string[] tags) => string.Join(",", tags);
}

internal sealed record FakePositionForJs(string Symbol);
