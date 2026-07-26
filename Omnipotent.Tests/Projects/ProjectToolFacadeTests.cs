using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects;

/// <summary>
/// The offered tool surface must stay under the provider tool-count limit while still reaching
/// every canonical capability. These tests pin both halves of that contract.
/// </summary>
public class ProjectToolFacadeTests
{
    private static List<HFWrapper.HFTool> CommanderCanonical()
    {
        var tools = ProjectCommanderAgent.BuildCoreToolDefinitions();
        tools.AddRange(ProjectCommanderAgent.BuildComputerToolDefinitions());
        return tools;
    }

    private static List<HFWrapper.HFTool> WorkerCanonical(ProjectAgentTier tier)
    {
        var router = new ProjectTierRouter(new ProjectSettingsStore());
        var tools = ProjectCommanderAgent.BuildCoreToolDefinitions()
            .Where(t => router.IsToolAllowed(tier, t.function.name) && !ProjectTierRouter.IsCommanderOnly(t.function.name))
            .ToList();
        tools.AddRange(ProjectCommanderAgent.BuildComputerToolDefinitions()
            .Where(t => router.IsToolAllowed(tier, t.function.name)));
        return tools;
    }

    [Fact]
    public void CommanderOfferedSurfaceStaysUnderTheLimit()
    {
        var offered = ProjectToolFacade.Fold(CommanderCanonical());
        Assert.True(offered.Count <= ProjectToolFacade.OfferedToolLimit,
            $"Commander is offered {offered.Count} tools, over the {ProjectToolFacade.OfferedToolLimit} limit.");
        Assert.Equal(offered.Count, offered.Select(t => t.function.name).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(ProjectAgentTier.Text)]
    [InlineData(ProjectAgentTier.TextImage)]
    [InlineData(ProjectAgentTier.TextImageVideo)]
    [InlineData(ProjectAgentTier.TextImageVideoAudio)]
    public void WorkerOfferedSurfaceStaysUnderTheLimit(ProjectAgentTier tier)
    {
        var offered = ProjectToolFacade.Fold(WorkerCanonical(tier));
        Assert.True(offered.Count <= ProjectToolFacade.OfferedToolLimit,
            $"{tier} worker is offered {offered.Count} tools, over the {ProjectToolFacade.OfferedToolLimit} limit.");
    }

    [Fact]
    public void EveryCanonicalToolRemainsReachable()
    {
        var canonical = CommanderCanonical();
        var offered = ProjectToolFacade.Fold(canonical).Select(t => t.function.name).ToHashSet(StringComparer.Ordinal);

        foreach (var tool in canonical)
        {
            string name = tool.function.name;
            if (offered.Contains(name)) continue;

            // Folded away: some offered tool must resolve back to exactly this canonical tool.
            var reached = offered
                .Select(offeredName => new { offeredName, ops = OpsOf(offeredName) })
                .SelectMany(x => x.ops.Select(op => ProjectToolFacade.Unfold(x.offeredName, $"{{\"op\":\"{op}\"}}")))
                .Any(u => u.IsValid && u.ToolName == name);
            // Or it is an alias whose survivor dispatches identically.
            reached |= ProjectToolFacade.Unfold(name, "{}").ToolName != name;
            Assert.True(reached, $"Canonical tool '{name}' is no longer reachable from the offered surface.");
        }
    }

    [Fact]
    public void FoldedCallResolvesToItsCanonicalToolAndDropsTheSelector()
    {
        var unfolded = ProjectToolFacade.Unfold("web", "{\"op\":\"search\",\"query\":\"tiktok api\",\"maxResults\":4}");

        Assert.True(unfolded.IsValid, unfolded.ErrorText);
        Assert.Equal("web_search", unfolded.ToolName);
        var args = JObject.Parse(unfolded.ArgumentsJson);
        Assert.Null(args["op"]);
        Assert.Equal("tiktok api", (string?)args["query"]);

        var contract = ProjectToolContract.ValidateAndNormalize(unfolded.ToolName, unfolded.ArgumentsJson,
            ProjectCommanderAgent.BuildCoreToolDefinitions());
        Assert.True(contract.IsValid, contract.ErrorText);
    }

    [Fact]
    public void ToolsThatOwnAnOpKeepIt()
    {
        var unfolded = ProjectToolFacade.Unfold("checkpoint",
            "{\"op\":\"upsert_fact\",\"key\":\"mail\",\"value\":\"catch-all works\",\"evidenceReference\":\"evt:42\"}");

        Assert.True(unfolded.IsValid, unfolded.ErrorText);
        Assert.Equal("update_checkpoint", unfolded.ToolName);
        Assert.Equal("upsert_fact", (string?)JObject.Parse(unfolded.ArgumentsJson)["op"]);

        var read = ProjectToolFacade.Unfold("checkpoint", "{\"op\":\"get\"}");
        Assert.Equal("get_checkpoint", read.ToolName);
        Assert.Null(JObject.Parse(read.ArgumentsJson)["op"]);
    }

    [Fact]
    public void UnknownOpIsRejectedWithTheAllowedList()
    {
        var unfolded = ProjectToolFacade.Unfold("vault", "{\"op\":\"destroy\",\"name\":\"token\"}");

        Assert.False(unfolded.IsValid);
        Assert.Contains("TOOL_ARGUMENT_ERROR", unfolded.ErrorText);
        Assert.Contains("save", unfolded.ErrorText);
        Assert.Contains("list", unfolded.ErrorText);
    }

    [Fact]
    public void MissingOpIsInferredWhenExactlyOneOperationFits()
    {
        var unfolded = ProjectToolFacade.Unfold("klivemail", "{\"address\":\"tiktok.memesquad\",\"purpose\":\"tiktok-signup\"}");

        Assert.True(unfolded.IsValid, unfolded.ErrorText);
        Assert.Equal("klivemail_create_mailbox", unfolded.ToolName);
        Assert.NotEmpty(unfolded.Warnings);
    }

    [Fact]
    public void ObservableWithoutAnOpStillNormalizesToSet()
    {
        var unfolded = ProjectToolFacade.Unfold("observable", "{\"name\":\"followers\",\"value\":42}");
        Assert.Equal("update_observable", unfolded.ToolName);

        var contract = ProjectToolContract.ValidateAndNormalize(unfolded.ToolName, unfolded.ArgumentsJson,
            ProjectCommanderAgent.BuildCoreToolDefinitions());
        Assert.True(contract.IsValid, contract.ErrorText);
        Assert.Equal("set", (string?)JObject.Parse(contract.NormalizedArgumentsJson!)["op"]);
    }

    [Fact]
    public void CanonicalNamesStillResolveSoOlderGuidanceKeepsWorking()
    {
        var unfolded = ProjectToolFacade.Unfold("web_search", "{\"query\":\"anything\"}");

        Assert.True(unfolded.IsValid, unfolded.ErrorText);
        Assert.Equal("web_search", unfolded.ToolName);
    }

    [Fact]
    public void DuplicateScriptToolIsAbsorbedByTheSurvivingConsole()
    {
        var offered = ProjectToolFacade.Fold(CommanderCanonical()).Select(t => t.function.name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("execute_csharp", offered);
        Assert.DoesNotContain("run_script", offered);
        Assert.Equal("execute_csharp", ProjectToolFacade.Unfold("run_script", "{\"code\":\"1+1\"}").ToolName);
    }

    [Fact]
    public void TierFilteringRemovesOpsAWorkerMayNotPerform()
    {
        var offered = ProjectToolFacade.Fold(WorkerCanonical(ProjectAgentTier.TextImage));
        var agents = offered.SingleOrDefault(t => t.function.name == "manage_agents");

        Assert.NotNull(agents);
        var ops = OpsOf(agents!).ToList();
        Assert.Contains("spawn", ops);
        Assert.DoesNotContain("retire", ops);      // commander-only
        Assert.DoesNotContain("assign_work", ops); // commander-only
        Assert.DoesNotContain("grand_plan", offered.Select(t => t.function.name));
    }

    [Fact]
    public void EveryFoldedToolDeclaresItsOpSelector()
    {
        foreach (var tool in ProjectToolFacade.Fold(CommanderCanonical()))
        {
            if (!ProjectToolFacade.FoldedToolNames.Contains(tool.function.name)) continue;
            var schema = (JObject)tool.function.parameters;
            Assert.Equal("op", (string?)(schema["required"] as JArray)?.First);
            Assert.NotEmpty(OpsOf(tool));
            Assert.False(string.IsNullOrWhiteSpace(tool.function.description));
        }
    }

    private static IEnumerable<string> OpsOf(HFWrapper.HFTool tool) =>
        ((tool.function.parameters as JObject)?["properties"]?["op"]?["enum"] as JArray)?
        .Values<string>().Where(v => v != null).Select(v => v!) ?? Enumerable.Empty<string>();

    private static IEnumerable<string> OpsOf(string offeredName)
    {
        var tool = ProjectToolFacade.Fold(CommanderCanonical())
            .FirstOrDefault(t => t.function.name == offeredName);
        return tool == null ? Enumerable.Empty<string>() : OpsOf(tool);
    }
}
