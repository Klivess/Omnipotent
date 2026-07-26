using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.KliveLLM;

public class GemmaToolCallCompatibilityTests
{
    [Fact]
    public void OfficialGemmaEnvelope_BecomesAStructuredToolCall()
    {
        var result = Normalize(
            """<|tool_call>call:reply_to_klives{message:<|"|>On it.<|"|>}<tool_call|><|tool_response>""",
            ProjectTools());

        Assert.True(result.Detected);
        Assert.True(result.Success, result.Error);
        Assert.True(result.Adapted);
        Assert.Equal("", result.Content);
        var call = Assert.Single(result.ToolCalls!);
        Assert.StartsWith("call_gemma_", call.id);
        Assert.Equal("reply_to_klives", call.function.name);
        Assert.Equal("On it.", (string?)JObject.Parse(call.function.arguments)["message"]);
    }

    [Fact]
    public void DarkbloomNamespacesAndFoldedOps_AreSafelyNormalized()
    {
        string text = """
            I have started.
            <|tool_call>call:kliveagent:reply_to_klives{message:"Directives acknowledged."}<tool_call|>
            <|tool_call>call:project_directive:op:acknowledge{directive_id:"directive-123"}<tool_call|>
            """;

        var result = Normalize(text, ProjectTools());

        Assert.True(result.Success, result.Error);
        Assert.Equal("I have started.", result.Content);
        Assert.Collection(result.ToolCalls!,
            call =>
            {
                Assert.Equal("reply_to_klives", call.function.name);
                Assert.Equal("Directives acknowledged.",
                    (string?)JObject.Parse(call.function.arguments)["message"]);
            },
            call =>
            {
                Assert.Equal("project_directive", call.function.name);
                var args = JObject.Parse(call.function.arguments);
                Assert.Equal("acknowledge", (string?)args["op"]);
                Assert.Equal("directive-123", (string?)args["directive_id"]);
            });
    }

    [Fact]
    public void UnquotedMultilineCode_WithNestedBracesCommasAndComments_IsPreserved()
    {
        string code = """
            // A closing brace in a comment must not terminate the call: }
            string filePath = "/project/KliveBotDiscord.cs";
            if (System.IO.File.Exists(filePath)) {
                var values = new[] { "one", "two" };
                Output(string.Join(",", values));
            } else {
                Output("missing");
            }
            """;
        string text = "<|tool_call>call:execute_csharp{code:\n" + code + "\n}<tool_call|>";

        var result = Normalize(text, ProjectTools());

        Assert.True(result.Success, result.Error);
        var call = Assert.Single(result.ToolCalls!);
        Assert.Equal("execute_csharp", call.function.name);
        string parsedCode = (string?)JObject.Parse(call.function.arguments)["code"] ?? "";
        Assert.Equal(code.ReplaceLineEndings("\n").Trim(), parsedCode.ReplaceLineEndings("\n").Trim());
    }

    [Fact]
    public void GatewayRewrittenSymmetricMarkers_AreSupported()
    {
        var tools = new[] { Tool("read_file", ("path", "string")) };

        var result = Normalize(
            """<{tool_call}>call:read_file{path:"notes.txt"}<{tool_call}>""", tools);

        Assert.True(result.Success, result.Error);
        Assert.Equal("read_file", Assert.Single(result.ToolCalls!).function.name);
        Assert.Equal("notes.txt",
            (string?)JObject.Parse(result.ToolCalls![0].function.arguments)["path"]);
    }

    [Fact]
    public void MultipleTypedArgumentsAndArrays_AreConvertedToJson()
    {
        var tools = new[]
        {
            Tool("example",
                ("enabled", "boolean"),
                ("count", "integer"),
                ("tags", "array")),
        };

        var result = Normalize(
            """<|tool_call>call:example{enabled:true,count:3,tags:[one,<|"|>two<|"|>]}<tool_call|>""",
            tools);

        Assert.True(result.Success, result.Error);
        var args = JObject.Parse(Assert.Single(result.ToolCalls!).function.arguments);
        Assert.True((bool?)args["enabled"]);
        Assert.Equal(3, (int?)args["count"]);
        Assert.Equal(new[] { "one", "two" }, ((JArray)args["tags"]!).Values<string>());
    }

    [Fact]
    public void NativeStructuredCallsAlwaysWinAndRemainUnchanged()
    {
        var native = new List<HFWrapper.HFToolCall>
        {
            new()
            {
                id = "native-1",
                function = new HFWrapper.HFFunctionCall
                {
                    name = "read_file",
                    arguments = "{\"path\":\"real.txt\"}",
                },
            },
        };
        const string content =
            """<|tool_call>call:read_file{path:<|"|>must-not-be-parsed.txt<|"|>}<tool_call|>""";

        var result = GemmaToolCallCompatibility.Normalize(
            content, native, new[] { Tool("read_file", ("path", "string")) });

        Assert.True(result.Success);
        Assert.False(result.Detected);
        Assert.False(result.Adapted);
        Assert.Same(native, result.ToolCalls);
        Assert.Equal(content, result.Content);
    }

    [Fact]
    public void OrdinaryProseIsUntouched()
    {
        const string content = "All requested work is complete.";

        var result = Normalize(content, ProjectTools());

        Assert.False(result.Detected);
        Assert.True(result.Success);
        Assert.False(result.Adapted);
        Assert.Null(result.ToolCalls);
        Assert.Equal(content, result.Content);
    }

    [Fact]
    public void UnknownToolFailsClosedWithoutReturningPartialCalls()
    {
        string text = """
            <|tool_call>call:reply_to_klives{message:"Starting."}<tool_call|>
            <|tool_call>call:delete_everything{path:"/"}<tool_call|>
            """;

        var result = Normalize(text, ProjectTools());

        Assert.True(result.Detected);
        Assert.False(result.Success);
        Assert.Null(result.ToolCalls);
        Assert.Equal("", result.Content);
        Assert.Contains("does not resolve to a tool offered", result.Error);
    }

    [Theory]
    [InlineData("<|tool_call>call:read_file{path:\"a.txt\"}")]
    [InlineData("<|tool_call>not-a-call<tool_call|>")]
    [InlineData("<|tool_call>call:read_file path:\"a.txt\"<tool_call|>")]
    public void MalformedEnvelopeFailsClosed(string text)
    {
        var result = Normalize(text, new[] { Tool("read_file", ("path", "string")) });

        Assert.True(result.Detected);
        Assert.False(result.Success);
        Assert.Null(result.ToolCalls);
        Assert.Equal("", result.Content);
        Assert.StartsWith("Gemma textual tool-call protocol error:", result.Error);
    }

    [Fact]
    public void EncodedOperationMustExistInTheOfferedSchema()
    {
        var result = Normalize(
            """<|tool_call>call:project_directive:op:destroy{directive_id:"x"}<tool_call|>""",
            ProjectTools());

        Assert.False(result.Success);
        Assert.Contains("is not allowed by the offered 'project_directive' schema", result.Error);
    }

    [Fact]
    public void EncodedAndArgumentOperationsCannotConflict()
    {
        var result = Normalize(
            """<|tool_call>call:project_directive:op:acknowledge{op:"complete",directive_id:"x"}<tool_call|>""",
            ProjectTools());

        Assert.False(result.Success);
        Assert.Contains("conflicts with the 'op' value", result.Error);
    }

    private static GemmaToolCallCompatibility.Result Normalize(
        string content, IReadOnlyList<HFWrapper.HFTool> tools) =>
        GemmaToolCallCompatibility.Normalize(content, null, tools);

    private static List<HFWrapper.HFTool> ProjectTools() =>
        ProjectToolFacade.Fold(ProjectCommanderAgent.BuildCoreToolDefinitions());

    private static HFWrapper.HFTool Tool(string name, params (string Name, string Type)[] properties)
    {
        var propertySchemas = new JObject();
        foreach (var property in properties)
        {
            propertySchemas[property.Name] = property.Type == "array"
                ? new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "string" },
                }
                : new JObject { ["type"] = property.Type };
        }

        return new HFWrapper.HFTool
        {
            function = new HFWrapper.HFFunctionDefinition
            {
                name = name,
                description = "test",
                parameters = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = propertySchemas,
                },
            },
        };
    }
}
