using Newtonsoft.Json;
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
    public void CommanderOfferedSurfaceNamesEachToolOnce()
    {
        var offered = ProjectToolFacade.Fold(CommanderCanonical());
        Assert.Equal(offered.Count, offered.Select(t => t.function.name).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(ProjectAgentTier.Text)]
    [InlineData(ProjectAgentTier.TextImage)]
    [InlineData(ProjectAgentTier.TextImageVideo)]
    [InlineData(ProjectAgentTier.TextImageVideoAudio)]
    public void WorkerOfferedSurfaceNamesEachToolOnce(ProjectAgentTier tier)
    {
        var offered = ProjectToolFacade.Fold(WorkerCanonical(tier));
        Assert.Equal(offered.Count, offered.Select(t => t.function.name).Distinct(StringComparer.Ordinal).Count());
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
    public void FoldedCallDropsEmptyPlaceholdersFromOtherOperations()
    {
        const string lunaStylePayload = """
            {"op":"retire","role":"","tier":"","objective":"","mission":"","milestoneId":"","agentID":"0678f5fb5060","deliverablePaths":[]}
            """;

        var unfolded = ProjectToolFacade.Unfold("manage_agents", lunaStylePayload);

        Assert.True(unfolded.IsValid, unfolded.ErrorText);
        Assert.Equal("retire_sub_agent", unfolded.ToolName);
        Assert.Equal(["agentID"], JObject.Parse(unfolded.ArgumentsJson).Properties().Select(p => p.Name));
        Assert.Contains(unfolded.Warnings, warning => warning.Contains("role", StringComparison.Ordinal));

        var contract = ProjectToolContract.ValidateAndNormalize(unfolded.ToolName, unfolded.ArgumentsJson,
            ProjectCommanderAgent.BuildCoreToolDefinitions());
        Assert.True(contract.IsValid, contract.ErrorText);
    }

    [Fact]
    public void MissingSelectorCanBeInferredDespiteEmptyUnionPlaceholders()
    {
        const string payload = """
            {"role":"","tier":"","objective":"","mission":"","milestoneId":"","agentID":"0678f5fb5060","deliverablePaths":[]}
            """;

        var unfolded = ProjectToolFacade.Unfold("manage_agents", payload);

        Assert.True(unfolded.IsValid, unfolded.ErrorText);
        Assert.Equal("retire_sub_agent", unfolded.ToolName);
        Assert.Equal(["agentID"], JObject.Parse(unfolded.ArgumentsJson).Properties().Select(p => p.Name));
    }

    [Fact]
    public void FoldedCallDropsTypedDefaultsFromAnotherOperation()
    {
        var unfolded = ProjectToolFacade.Unfold("manage_agents",
            "{\"op\":\"retire\",\"agentID\":\"0678f5fb5060\",\"role\":\"replacement-worker\"}");

        Assert.True(unfolded.IsValid, unfolded.ErrorText);
        Assert.Equal("retire_sub_agent", unfolded.ToolName);
        Assert.Equal(["agentID"], JObject.Parse(unfolded.ArgumentsJson).Properties().Select(p => p.Name));
        Assert.Contains(unfolded.Warnings, warning => warning.Contains("role", StringComparison.Ordinal));
    }

    [Fact]
    public void FoldedCallStillRejectsUnknownProperties()
    {
        var unfolded = ProjectToolFacade.Unfold("manage_agents",
            "{\"op\":\"retire\",\"agentID\":\"0678f5fb5060\",\"agnetID\":null}");

        Assert.False(unfolded.IsValid);
        Assert.Contains("agnetID", unfolded.ErrorText);
    }

    public static IEnumerable<object[]> LunaSaturatedPayloads()
    {
        yield return ["project_directive", "{\"op\":\"acknowledge\",\"directiveID\":\"d1\",\"note\":\"accepted\",\"includeResolved\":false,\"summary\":\"\",\"artifactPaths\":[]}", "acknowledge_project_directive"];
        yield return ["checkpoint", "{\"op\":\"get\",\"result\":\"done\",\"evidenceEventSequence\":0,\"grandPlanVersion\":0}", "get_checkpoint"];
        yield return ["desktop", "{\"op\":\"terminal\",\"command\":\"pwd\",\"maxItems\":120}", "computer_terminal"];
        yield return ["desktop", "{\"op\":\"window_state\",\"maxItems\":0}", "computer_window_state"];
        yield return ["observable", "{\"op\":\"list\",\"value\":0}", "list_observables"];
        yield return ["manage_files", "{\"op\":\"stat\",\"path\":\"report.md\",\"recursive\":false}", "stat_file"];
        yield return ["browser", "{\"op\":\"open\",\"newTab\":false}", "computer_open_browser"];
    }

    [Theory]
    [MemberData(nameof(LunaSaturatedPayloads))]
    public void ObservedLunaDefaultsNormalizeToValidCanonicalCalls(
        string foldedTool, string payload, string expectedCanonicalTool)
    {
        var unfolded = ProjectToolFacade.Unfold(foldedTool, payload);

        Assert.True(unfolded.IsValid, unfolded.ErrorText);
        Assert.Equal(expectedCanonicalTool, unfolded.ToolName);
        Assert.NotEmpty(unfolded.Warnings);
        var contract = ProjectToolContract.ValidateAndNormalize(
            unfolded.ToolName, unfolded.ArgumentsJson, CommanderCanonical());
        Assert.True(contract.IsValid, contract.ErrorText);
    }

    [Fact]
    public void EveryFoldedOperationProjectsSaturatedUnionFieldsOntoItsCanonicalSchema()
    {
        var canonical = CommanderCanonical();
        var canonicalSchemas = canonical.ToDictionary(tool => tool.function.name,
            tool => tool.function.parameters is JObject schema
                ? schema
                : JObject.Parse(JsonConvert.SerializeObject(tool.function.parameters)), StringComparer.Ordinal);

        foreach (var folded in ProjectToolFacade.Fold(canonical)
                     .Where(tool => ProjectToolFacade.FoldedToolNames.Contains(tool.function.name)))
        {
            var foldedSchema = (JObject)folded.function.parameters;
            var unionProperties = (JObject)foldedSchema["properties"]!;
            foreach (string op in OpsOf(folded))
            {
                var payload = new JObject { ["op"] = op };
                foreach (var property in unionProperties.Properties().Where(property => property.Name != "op"))
                    payload[property.Name] = ProviderDefault(property.Value);

                var unfolded = ProjectToolFacade.Unfold(folded.function.name, payload.ToString());

                Assert.True(unfolded.IsValid,
                    $"{folded.function.name} op={op} failed to unfold: {unfolded.ErrorText}");
                var allowed = ((JObject?)canonicalSchemas[unfolded.ToolName]["properties"])?
                    .Properties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal) ?? [];
                var unexpected = JObject.Parse(unfolded.ArgumentsJson).Properties()
                    .Select(property => property.Name).Where(name => !allowed.Contains(name)).ToList();
                Assert.True(unexpected.Count == 0,
                    $"{folded.function.name} op={op} leaked cross-operation fields: {string.Join(", ", unexpected)}");
            }
        }
    }

    private static JToken ProviderDefault(JToken propertySchema)
    {
        if (propertySchema["enum"] is JArray values && values.Count > 0)
            return values[0]!.DeepClone();
        string? type = propertySchema["type"]?.Value<string>();
        return type switch
        {
            "boolean" => false,
            "integer" => 0,
            "number" => 0.0,
            "array" => new JArray(),
            "object" => new JObject(),
            _ => "provider-default",
        };
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
