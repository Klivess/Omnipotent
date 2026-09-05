using System.Reflection;
using Newtonsoft.Json.Linq;
using Omnipotent.Service_Manager;
using Omnipotent.Services.ServiceTools;

namespace Omnipotent.Tests.ServiceTools;

// -- A stand-in service, annotated the way a real one is. Lives in the test assembly so the registry
//    can be built over it in isolation, without the 23 real services in the picture. --

[OmniServiceTools("faketool", "A stand-in service used to exercise the Service Surface end to end.")]
[OmniToolGroup("things", "Operations over the fake things.")]
public sealed class FakeToolService : OmniService
{
    public FakeToolService() { }

    public List<string> Deleted { get; } = new();
    public string? LastNote { get; private set; }

    [OmniTool("list", "Lists the fake things, newest first.", Group = "things")]
    public List<string> ListThings([OmniParam("Maximum rows to return.")] int limit = 10)
        => Enumerable.Range(1, limit).Select(i => $"thing-{i}").ToList();

    [OmniTool("get", "Returns one fake thing by id.", Group = "things")]
    public Task<object> GetThingAsync([OmniParam("The thing's id.")] string id)
        => Task.FromResult<object>(new { id, name = $"thing {id}" });

    [OmniTool("note", "Records a note against a thing.", Group = "things", Mutating = true)]
    public Task<string> NoteAsync(string id, [OmniParam("Free text.")] string note, CancellationToken ct)
    {
        LastNote = $"{id}:{note}";
        return Task.FromResult($"noted {id}");
    }

    [OmniTool("delete", "Permanently deletes a thing.", Group = "things", Mutating = true, Destructive = true)]
    public string Delete(string id)
    {
        Deleted.Add(id);
        return $"deleted {id}";
    }

    [OmniTool("explode", "Always throws, to prove failures come back as values.", Group = "things")]
    public string Explode() => throw new InvalidOperationException("boom");

    [OmniTool("tier", "Takes a constrained value.", Group = "things", Mutating = true)]
    public string SetTier([OmniParam("The tier.", Values = new[] { "tracked", "watch", "archive" })] string tier)
        => $"tier={tier}";

    // No attribute: must never appear on an annotated service's surface.
    public string SecretBackDoor() => "should never be reachable";
}

public class OmniToolInvokerTests
{
    private static readonly OmniToolRegistry Registry =
        OmniToolRegistry.Build(typeof(FakeToolService).Assembly);

    private static (OmniToolInvoker invoker, FakeToolService service) Build(
        Func<OmniOperation, string, CancellationToken, Task<bool>>? gate = null)
    {
        var service = new FakeToolService();
        MarkActive(service);
        var invoker = new OmniToolInvoker(Registry, () => new List<OmniService> { service })
        {
            ApprovalGate = gate,
        };
        return (invoker, service);
    }

    /// <summary>ServiceStart() would spin a real thread and need a service manager; the invoker only
    /// cares that the service reports itself active.</summary>
    private static void MarkActive(OmniService service)
    {
        typeof(OmniService)
            .GetField("ServiceActive", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, true);
    }

    [Fact]
    public void OnlyAnnotatedMethodsAppearOnAnAnnotatedService()
    {
        var surface = Registry.GetService("faketool");
        Assert.NotNull(surface);
        Assert.True(surface!.Annotated);

        var ops = surface.Operations.Select(o => o.Op).ToList();
        Assert.Equal(new[] { "list", "get", "note", "delete", "explode", "tier" }.OrderBy(x => x),
                     ops.OrderBy(x => x));
        Assert.DoesNotContain("secret_back_door", ops);

        // The group suffixes the service key.
        Assert.Single(surface.Groups);
        Assert.Equal("faketool_things", surface.Groups[0].ToolName);
    }

    [Fact]
    public async Task ReadOperationReturnsItsValue()
    {
        var (invoker, _) = Build();
        var result = await invoker.ExecuteToolAsync("faketool_things", """{"op":"list","limit":3}""", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("thing-1", result.Text);
        Assert.Contains("thing-3", result.Text);
        Assert.DoesNotContain("thing-4", result.Text);
    }

    [Fact]
    public async Task AsyncOperationIsAwaitedAndUnwrapped()
    {
        var (invoker, _) = Build();
        var result = await invoker.ExecuteToolAsync("faketool_things", """{"op":"get","id":"abc"}""", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("abc", result.Text);
        Assert.DoesNotContain("System.Threading.Tasks.Task", result.Text);
    }

    [Fact]
    public async Task MutatingOperationRunsWithoutApproval()
    {
        var (invoker, service) = Build();
        var result = await invoker.ExecuteToolAsync("faketool_things",
            """{"op":"note","id":"a1","note":"hello"}""", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("a1:hello", service.LastNote);
    }

    [Fact]
    public async Task DestructiveOperationIsRefusedWhenNoApprovalChannelExists()
    {
        // Failing closed is the point: an irreversible action must never happen just because nothing
        // was listening.
        var (invoker, service) = Build(gate: null);
        var result = await invoker.ExecuteToolAsync("faketool_things", """{"op":"delete","id":"x"}""", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(service.Deleted);
        Assert.Contains("irreversible", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DestructiveOperationIsBlockedWhenKlivesDeclines()
    {
        var (invoker, service) = Build(gate: (_, _, _) => Task.FromResult(false));
        var result = await invoker.ExecuteToolAsync("faketool_things", """{"op":"delete","id":"x"}""", CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.ApprovalRequired);
        Assert.Empty(service.Deleted);
    }

    [Fact]
    public async Task DestructiveOperationRunsOnceApproved()
    {
        string? shownToKlives = null;
        var (invoker, service) = Build(gate: (_, summary, _) =>
        {
            shownToKlives = summary;
            return Task.FromResult(true);
        });

        var result = await invoker.ExecuteToolAsync("faketool_things", """{"op":"delete","id":"x"}""", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(new[] { "x" }, service.Deleted);
        // The approval prompt must say what is about to happen, not just name a tool.
        Assert.Contains("delete", shownToKlives!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"id\":\"x\"", shownToKlives!);
    }

    [Fact]
    public async Task AThrownExceptionComesBackAsAReadableValue()
    {
        var (invoker, _) = Build();
        var result = await invoker.ExecuteToolAsync("faketool_things", """{"op":"explode"}""", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("boom", result.Text);
        // The reflection wrapper must not be what the agent sees.
        Assert.DoesNotContain("TargetInvocationException", result.Text);
    }

    [Fact]
    public async Task MissingOpListsTheAvailableOnes()
    {
        var (invoker, _) = Build();
        var result = await invoker.ExecuteToolAsync("faketool_things", "{}", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("list", result.Text);
        Assert.Contains("delete", result.Text);
    }

    [Fact]
    public async Task UnknownOpIsRejectedWithTheValidSet()
    {
        var (invoker, _) = Build();
        var result = await invoker.ExecuteToolAsync("faketool_things", """{"op":"frobnicate"}""", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("TOOL_ARGUMENT_ERROR", result.Text);
        Assert.Contains("frobnicate", result.Text);
    }

    [Fact]
    public async Task MissingRequiredArgumentIsRejectedBeforeDispatch()
    {
        var (invoker, service) = Build();
        var result = await invoker.ExecuteToolAsync("faketool_things", """{"op":"note","id":"a1"}""", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("missing_required", result.Text);
        Assert.Null(service.LastNote);
    }

    [Fact]
    public async Task AMisspelledArgumentSuggestsTheRightName()
    {
        var (invoker, _) = Build();
        var result = await invoker.ExecuteToolAsync("faketool_things",
            """{"op":"note","id":"a1","not":"hello"}""", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("unknown_property", result.Text);
        Assert.Contains("note", result.Text);
    }

    [Fact]
    public async Task ScalarShapesProvidersGetWrongAreCoerced()
    {
        // "3" for an integer is a near-universal provider habit; failing the call over it teaches the
        // agent nothing useful.
        var (invoker, _) = Build();
        var result = await invoker.ExecuteToolAsync("faketool_things", """{"op":"list","limit":"3"}""", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("TOOL_ARGUMENT_NORMALIZED", result.Text);
        Assert.Contains("thing-3", result.Text);
        Assert.DoesNotContain("thing-4", result.Text);
    }

    [Fact]
    public async Task ConstrainedValuesAreEnforcedAndCanonicalised()
    {
        var (invoker, _) = Build();

        var bad = await invoker.ExecuteToolAsync("faketool_things", """{"op":"tier","tier":"vip"}""", CancellationToken.None);
        Assert.False(bad.Success);
        Assert.Contains("enum_mismatch", bad.Text);
        Assert.Contains("tracked", bad.Text);

        var good = await invoker.ExecuteToolAsync("faketool_things", """{"op":"tier","tier":"TRACKED"}""", CancellationToken.None);
        Assert.True(good.Success);
        Assert.Contains("tier=tracked", good.Text);
    }

    [Fact]
    public async Task ArgumentsEncodedAsAJsonStringAreStillRead()
    {
        var (invoker, _) = Build();
        var result = await invoker.ExecuteToolAsync("faketool_things",
            "\"{\\\"op\\\":\\\"list\\\",\\\"limit\\\":2}\"", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("thing-2", result.Text);
    }

    [Fact]
    public async Task CallingAServiceThatIsNotRunningSaysSo()
    {
        var invoker = new OmniToolInvoker(Registry, () => new List<OmniService>());
        var result = await invoker.ExecuteToolAsync("faketool_things", """{"op":"list"}""", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not running", result.Text);
    }

    [Fact]
    public async Task AnInactiveServiceIsNotCalled()
    {
        var service = new FakeToolService(); // never marked active
        var invoker = new OmniToolInvoker(Registry, () => new List<OmniService> { service });
        var result = await invoker.ExecuteToolAsync("faketool_things", """{"op":"list"}""", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not active", result.Text);
    }

    [Fact]
    public async Task EveryCallIsAudited()
    {
        var (invoker, _) = Build();
        await invoker.ExecuteToolAsync("faketool_things", """{"op":"note","id":"a1","note":"hello"}""", CancellationToken.None);
        await invoker.ExecuteToolAsync("faketool_things", """{"op":"explode"}""", CancellationToken.None);

        var audit = invoker.Audit.Recent();
        Assert.Equal(2, audit.Count);

        var write = audit[0];
        Assert.Equal("note", write.Op);
        Assert.True(write.Mutating);
        Assert.True(write.Success);
        Assert.Contains("hello", write.RedactedArguments);

        Assert.False(audit[1].Success);
        Assert.Contains("boom", audit[1].Error);
    }

    [Fact]
    public async Task SecretArgumentsAreNeverWrittenToTheAudit()
    {
        var (invoker, _) = Build();
        await invoker.ExecuteToolAsync("faketool_things",
            """{"op":"note","id":"a1","note":"hello","password":"hunter2"}""", CancellationToken.None);

        var entry = invoker.Audit.Recent().Single();
        Assert.DoesNotContain("hunter2", entry.RedactedArguments);
        Assert.Contains("[redacted]", entry.RedactedArguments);
    }

    [Fact]
    public async Task TheUniversalToolReachesTheSameOperationsByServiceAndMethod()
    {
        var (invoker, service) = Build();
        var result = await invoker.ExecuteServiceCallAsync("faketool", "note",
            JObject.Parse("""{"id":"z9","note":"via omniservice"}"""), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("z9:via omniservice", service.LastNote);
    }

    [Fact]
    public async Task TheUniversalToolPointsAtDescribeWhenTheMethodIsWrong()
    {
        var (invoker, _) = Build();
        var result = await invoker.ExecuteServiceCallAsync("faketool", "lst", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("describe", result.Text);
    }

    [Fact]
    public void ParallelSafetyFollowsTheOpNotTheToolName()
    {
        // One generated tool covers both reads and writes, so the name alone cannot decide whether a
        // call may be pre-launched alongside others.
        var read = Registry.FindOnTool("faketool_things", "list");
        var write = Registry.FindOnTool("faketool_things", "note");
        var destructive = Registry.FindOnTool("faketool_things", "delete");

        Assert.False(read!.Mutating);
        Assert.True(write!.Mutating);
        Assert.True(destructive!.Mutating);
        Assert.True(destructive.Destructive);
    }

    [Fact]
    public void DescribeRendersSchemasCompletelyEnoughToCallFrom()
    {
        var text = OmniToolCatalog.RenderServiceDescription(Registry.GetService("faketool")!);

        Assert.Contains("note", text);
        Assert.Contains("REQUIRED", text);
        Assert.Contains("[writes]", text);
        Assert.Contains("IRREVERSIBLE", text);
        Assert.Contains("tracked|watch|archive", text);
    }

    [Fact]
    public void FoldedToolScopesEachArgumentToTheOpsThatUseIt()
    {
        var tool = OmniToolCatalog.BuildFoldedTool(Registry.GetTool("faketool_things")!);
        var schema = (JObject)tool.function.parameters;
        var properties = (JObject)schema["properties"]!;

        // 'limit' belongs to op=list only, so the model must be told that.
        var limit = properties["limit"]!["description"]!.Value<string>()!;
        Assert.Contains("list", limit);

        // 'id' is shared by several ops but not all of them.
        Assert.NotNull(properties["id"]);
        Assert.Contains("op=", properties["id"]!["description"]!.Value<string>()!);
    }
}
