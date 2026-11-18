using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.Demiurge;

namespace Omnipotent.Tests.Demiurge;

public class StewardToolContractTests
{
    [Theory]
    [InlineData("run_bash")]
    [InlineData("run_powershell")]
    public void HostShellLegacyCommandAlias_IsNormalizedToScript(string toolName)
    {
        var result = StewardToolContract.ValidateAndNormalize(toolName,
            "{\"command\":\"do work\",\"timeoutSeconds\":12}",
            StewardCommanderAgent.BuildCoreToolDefinitions());

        Assert.True(result.IsValid, result.ErrorText);
        var normalized = JObject.Parse(result.NormalizedArgumentsJson!);
        Assert.Equal("do work", (string?)normalized["script"]);
        Assert.Null(normalized["command"]);
        Assert.Equal(12, (int?)normalized["timeoutSeconds"]);
    }

    [Fact]
    public void CommandRemainsCanonicalForContainerTerminal()
    {
        var result = StewardToolContract.ValidateAndNormalize("computer_terminal",
            "{\"command\":\"pwd\"}", StewardCommanderAgent.BuildComputerToolDefinitions());

        Assert.True(result.IsValid, result.ErrorText);
        var normalized = JObject.Parse(result.NormalizedArgumentsJson!);
        Assert.Equal("pwd", (string?)normalized["command"]);
        Assert.Null(normalized["script"]);
    }

    [Fact]
    public void ContainerTerminalScriptAlias_IsNormalizedToCommand()
    {
        var result = StewardToolContract.ValidateAndNormalize("computer_terminal",
            "{\"script\":\"pwd\",\"timeoutSeconds\":\"30\"}",
            StewardCommanderAgent.BuildComputerToolDefinitions());

        Assert.True(result.IsValid, result.ErrorText);
        var normalized = JObject.Parse(result.NormalizedArgumentsJson!);
        Assert.Equal("pwd", (string?)normalized["command"]);
        Assert.Equal(30, (int?)normalized["timeoutSeconds"]);
        Assert.Null(normalized["script"]);
    }

    [Fact]
    public void HostShellCodeAlias_AndWorkingDirectory_AreAccepted()
    {
        var result = StewardToolContract.ValidateAndNormalize("run_bash",
            "{\"code\":\"pwd\",\"workingDirectory\":\"jobs/today\"}",
            StewardCommanderAgent.BuildCoreToolDefinitions());

        Assert.True(result.IsValid, result.ErrorText);
        var normalized = JObject.Parse(result.NormalizedArgumentsJson!);
        Assert.Equal("pwd", (string?)normalized["script"]);
        Assert.Equal("jobs/today", (string?)normalized["workingDirectory"]);
    }

    [Fact]
    public void ReadFile_StringLineNumbers_AreLosslesslyCoerced()
    {
        var result = StewardToolContract.ValidateAndNormalize("read_file",
            "{\"path\":\"ledger.json\",\"startLine\":\"36\",\"maxLines\":\"14\"}",
            StewardCommanderAgent.BuildCoreToolDefinitions());

        Assert.True(result.IsValid, result.ErrorText);
        var normalized = JObject.Parse(result.NormalizedArgumentsJson!);
        Assert.Equal(36, (int?)normalized["startLine"]);
        Assert.Equal(14, (int?)normalized["maxLines"]);
    }

    [Fact]
    public void MisplacedReadFileExecutionHint_IsIgnoredWithAnExplicitWarning()
    {
        var contract = StewardToolContract.ValidateAndNormalize("read_file",
            "{\"path\":\"signup.py\",\"run_as_cwd\":\"python signup.py\"}",
            StewardCommanderAgent.BuildCoreToolDefinitions());

        Assert.True(contract.IsValid, contract.ErrorText);
        Assert.Single(contract.Warnings);
        Assert.DoesNotContain("run_as_cwd", contract.NormalizedArgumentsJson);
        var result = StewardToolContract.AttachWarnings(contract, new CommanderToolResult("file contents"));
        Assert.StartsWith("TOOL_ARGUMENT_NORMALIZED", result.ResultText);
        Assert.Contains("never executes", result.ResultText);
    }

    [Fact]
    public void UpdatePlan_ObjectSteps_AreReducedToText()
    {
        var result = StewardToolContract.ValidateAndNormalize("update_plan",
            "{\"focus\":\"publish\",\"nextSteps\":[{\"step\":\"open browser\"},{\"description\":\"upload draft\"}]}",
            StewardCommanderAgent.BuildCoreToolDefinitions());

        Assert.True(result.IsValid, result.ErrorText);
        var steps = (JArray)JObject.Parse(result.NormalizedArgumentsJson!)["nextSteps"]!;
        Assert.Equal(new[] { "open browser", "upload draft" }, steps.Values<string>());
    }

    [Fact]
    public void UpdatePlan_UnknownObjectShape_IsRejectedInsteadOfGuessed()
    {
        var result = StewardToolContract.ValidateAndNormalize("update_plan",
            "{\"nextSteps\":[{\"owner\":\"agent-1\",\"status\":\"pending\"}]}",
            StewardCommanderAgent.BuildCoreToolDefinitions());

        Assert.False(result.IsValid);
        Assert.Equal(StewardToolContract.TypeMismatch, result.Error!.Code);
    }

    [Fact]
    public void UpdatePlan_NumberedMultilineString_IsSplitIntoSteps()
    {
        var result = StewardToolContract.ValidateAndNormalize("update_plan",
            "{\"nextSteps\":\"1. Inspect inbox\\n2) Enter code\\n- verify profile\"}",
            StewardCommanderAgent.BuildCoreToolDefinitions());

        Assert.True(result.IsValid, result.ErrorText);
        var steps = (JArray)JObject.Parse(result.NormalizedArgumentsJson!)["nextSteps"]!;
        Assert.Equal(new[] { "Inspect inbox", "Enter code", "verify profile" }, steps.Values<string>());
    }

    [Fact]
    public void ObservableSetOperation_IsInferredFromValue()
    {
        var result = StewardToolContract.ValidateAndNormalize("update_observable",
            "{\"name\":\"posts published\",\"value\":\"4\"}",
            StewardCommanderAgent.BuildCoreToolDefinitions());

        Assert.True(result.IsValid, result.ErrorText);
        var normalized = JObject.Parse(result.NormalizedArgumentsJson!);
        Assert.Equal("set", (string?)normalized["op"]);
        Assert.Equal(4d, (double?)normalized["value"]);
    }

    [Fact]
    public void CompatibilityToolsAreOffered_ButAgentsCannotDeclareStewardBlockers()
    {
        var tools = StewardCommanderAgent.BuildCoreToolDefinitions();
        Assert.Contains(tools, t => t.function.name == "search_code");

        var human = StewardToolContract.ValidateAndNormalize("request_human",
            "{\"title\":\"Captcha\",\"description\":\"Complete the visible challenge\"}", tools);
        var checkpoint = StewardToolContract.ValidateAndNormalize("update_checkpoint",
            "{\"op\":\"set_blocker\",\"blockerSummary\":\"SMS verification required\"}", tools);
        Assert.True(human.IsValid, human.ErrorText);
        Assert.False(checkpoint.IsValid);
    }

    [Fact]
    public void EqualLegacyAndCanonicalAliases_AreDeduplicated()
    {
        var result = StewardToolContract.ValidateAndNormalize("run_bash",
            "{\"command\":\"pwd\",\"script\":\"pwd\"}", StewardCommanderAgent.BuildCoreToolDefinitions());

        Assert.True(result.IsValid, result.ErrorText);
        Assert.Equal("{\"script\":\"pwd\"}", result.NormalizedArgumentsJson);
    }

    [Fact]
    public void LegacyAndCanonicalNamesTogether_AreRejectedWithoutChoosingOne()
    {
        var result = StewardToolContract.ValidateAndNormalize("run_bash",
            "{\"command\":\"one\",\"script\":\"two\"}",
            StewardCommanderAgent.BuildCoreToolDefinitions());

        Assert.False(result.IsValid);
        Assert.Equal(StewardToolContract.AliasConflict, result.Error!.Code);
        Assert.Equal("script", result.Error.Suggestion);
    }

    [Fact]
    public void MalformedJsonAndNonObjectArguments_AreRejected()
    {
        var tool = Tool("example", Obj(new { value = Schema("string") }, "value"));

        var malformed = StewardToolContract.ValidateAndNormalize(tool, "{not json");
        var array = StewardToolContract.ValidateAndNormalize(tool, "[]");

        Assert.Equal(StewardToolContract.InvalidJson, malformed.Error!.Code);
        Assert.Equal(StewardToolContract.ExpectedObject, array.Error!.Code);
        Assert.Null(malformed.NormalizedArgumentsJson);
        Assert.Null(array.NormalizedArgumentsJson);
    }

    [Theory]
    [InlineData("{\"path\":\"a.txt\"}}")]
    [InlineData("{\"path\":\"a.txt\"")]
    [InlineData("{\"path\":\"a.txt\",\"path\":\"a.txt\"}")]
    public void MechanicallySafeJsonEnvelopeDefects_AreRepaired(string json)
    {
        var result = StewardToolContract.ValidateAndNormalize("read_file", json,
            StewardCommanderAgent.BuildCoreToolDefinitions());

        Assert.True(result.IsValid, result.ErrorText);
        Assert.Equal("a.txt", (string?)JObject.Parse(result.NormalizedArgumentsJson!)["path"]);
    }

    [Fact]
    public void ConflictingDuplicateProperties_RemainRejected()
    {
        var result = StewardToolContract.ValidateAndNormalize("read_file",
            "{\"path\":\"a.txt\",\"path\":\"b.txt\"}", StewardCommanderAgent.BuildCoreToolDefinitions());

        Assert.False(result.IsValid);
        Assert.Equal(StewardToolContract.InvalidJson, result.Error!.Code);
    }

    [Fact]
    public void RequiredFields_AreValidated()
    {
        var result = StewardToolContract.ValidateAndNormalize(
            Tool("example", Obj(new { script = Schema("string"), timeout = Schema("integer") }, "script", "timeout")),
            "{}");

        Assert.False(result.IsValid);
        Assert.Equal(StewardToolContract.MissingRequired, result.Error!.Code);
        Assert.Contains("script", result.Error.Message);
        Assert.Contains("timeout", result.Error.Message);
    }

    [Theory]
    [InlineData("{\"value\":12}", "string")]
    [InlineData("{\"value\":1.5}", "integer")]
    [InlineData("{\"value\":0}", "boolean")]
    [InlineData("{\"value\":{}}", "array")]
    [InlineData("{\"value\":[]}", "object")]
    public void JsonSchemaPrimitiveAndContainerTypes_AreValidated(string json, string schemaType)
    {
        var result = StewardToolContract.ValidateAndNormalize(
            Tool("example", Obj(new { value = Schema(schemaType) }, "value")), json);

        Assert.False(result.IsValid);
        Assert.Equal(StewardToolContract.TypeMismatch, result.Error!.Code);
        Assert.Equal("$.value", result.Error.Path);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("2.0")]
    [InlineData("2e0")]
    public void JsonSchemaInteger_AcceptsMathematicalIntegers(string value)
    {
        var result = StewardToolContract.ValidateAndNormalize(
            Tool("example", Obj(new { value = Schema("integer") }, "value")), $"{{\"value\":{value}}}");

        Assert.True(result.IsValid, result.ErrorText);
    }

    [Fact]
    public void ArrayItemTypes_AreValidatedWithAnIndexedPath()
    {
        var schema = Obj(new
        {
            tags = new { type = "array", items = Schema("string") },
        }, "tags");

        var result = StewardToolContract.ValidateAndNormalize(Tool("example", schema),
            "{\"tags\":[\"ok\",7]}");

        Assert.False(result.IsValid);
        Assert.Equal(StewardToolContract.TypeMismatch, result.Error!.Code);
        Assert.Equal("$.tags[1]", result.Error.Path);
    }

    [Fact]
    public void EnumValues_AreValidated()
    {
        var schema = Obj(new
        {
            mode = new { type = "string", @enum = new[] { "safe", "fast" } },
        }, "mode");

        var result = StewardToolContract.ValidateAndNormalize(Tool("example", schema),
            "{\"mode\":\"other\"}");

        Assert.False(result.IsValid);
        Assert.Equal(StewardToolContract.EnumMismatch, result.Error!.Code);
        Assert.Contains("safe", result.Error.Message);
        Assert.Contains("fast", result.Error.Message);
    }

    [Fact]
    public void UnknownProperty_IsRejectedWithNearestNameSuggestionBeforeMissingRequired()
    {
        var result = StewardToolContract.ValidateAndNormalize(
            Tool("example", Obj(new { script = Schema("string"), timeoutSeconds = Schema("integer") }, "script")),
            "{\"scrpt\":\"work\"}");

        Assert.False(result.IsValid);
        Assert.Equal(StewardToolContract.UnknownProperty, result.Error!.Code);
        Assert.Equal("$.scrpt", result.Error.Path);
        Assert.Equal("script", result.Error.Suggestion);
        Assert.Contains("Did you mean 'script'", result.Error.Message);
    }

    [Fact]
    public void NestedUnknownProperty_IsRejected()
    {
        var schema = Obj(new
        {
            options = new
            {
                type = "object",
                properties = new { retries = Schema("integer") },
                required = Array.Empty<string>(),
            },
        }, "options");

        var result = StewardToolContract.ValidateAndNormalize(Tool("example", schema),
            "{\"options\":{\"retrys\":2}}");

        Assert.False(result.IsValid);
        Assert.Equal(StewardToolContract.UnknownProperty, result.Error!.Code);
        Assert.Equal("$.options.retrys", result.Error.Path);
        Assert.Equal("retries", result.Error.Suggestion);
    }

    [Fact]
    public void OpenObjectsAndTypedAdditionalProperties_RemainUsable()
    {
        var schema = Obj(new
        {
            sourceSpec = new { type = "object" },
            labels = new { type = "object", additionalProperties = Schema("string") },
        }, "sourceSpec", "labels");
        var tool = Tool("example", schema);

        var valid = StewardToolContract.ValidateAndNormalize(tool,
            "{\"sourceSpec\":{\"providerSpecific\":42},\"labels\":{\"team\":\"blue\"}}");
        var invalid = StewardToolContract.ValidateAndNormalize(tool,
            "{\"sourceSpec\":{},\"labels\":{\"team\":7}}");

        Assert.True(valid.IsValid, valid.ErrorText);
        Assert.Equal(StewardToolContract.TypeMismatch, invalid.Error!.Code);
        Assert.Equal("$.labels.team", invalid.Error.Path);
    }

    [Fact]
    public void AToolNotOfferedForTheTurn_IsRejected()
    {
        var result = StewardToolContract.ValidateAndNormalize("not_available", "{}",
            new[] { Tool("available", Obj(new { })) });

        Assert.False(result.IsValid);
        Assert.Equal(StewardToolContract.ToolNotOffered, result.Error!.Code);
    }

    [Fact]
    public void StructuredError_IsCompactJsonSuitableForAToolResult()
    {
        var result = StewardToolContract.ValidateAndNormalize(
            Tool("example", Obj(new { script = Schema("string") }, "script")),
            "{\"scrpt\":\"work\"}");

        const string prefix = "TOOL_ARGUMENT_ERROR ";
        Assert.StartsWith(prefix, result.ErrorText);
        var payload = JObject.Parse(result.ErrorText![prefix.Length..]);
        Assert.Equal(StewardToolContract.UnknownProperty, (string?)payload["code"]);
        Assert.Equal("$.scrpt", (string?)payload["path"]);
        Assert.Equal("script", (string?)payload["suggestion"]);
    }

    [Fact]
    public void ValidArguments_AreReturnedAsCompactNormalizedJson()
    {
        var result = StewardToolContract.ValidateAndNormalize(
            Tool("example", Obj(new { enabled = Schema("boolean"), values = new { type = "array", items = Schema("number") } }, "enabled")),
            "{ \"enabled\" : true, \"values\" : [1, 2.5] }");

        Assert.True(result.IsValid, result.ErrorText);
        Assert.Equal("{\"enabled\":true,\"values\":[1,2.5]}", result.NormalizedArgumentsJson);
        Assert.Null(result.Error);
    }

    private static HFWrapper.HFTool Tool(string name, object parameters) => new()
    {
        function = new HFWrapper.HFFunctionDefinition
        {
            name = name,
            description = "test",
            parameters = parameters,
        },
    };

    private static object Obj(object properties, params string[] required) => new
    {
        type = "object",
        properties,
        required,
    };

    private static object Schema(string type) => new { type };
}
