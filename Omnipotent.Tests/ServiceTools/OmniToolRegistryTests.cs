using System.Reflection;
using Newtonsoft.Json.Linq;
using Omnipotent.Service_Manager;
using Omnipotent.Services.ServiceTools;

namespace Omnipotent.Tests.ServiceTools;

/// <summary>
/// Guards the generated service-tool surface. These run against the REAL assembly scan, so an
/// annotation that produces an un-callable tool fails here rather than at runtime in front of the
/// model.
/// </summary>
public class OmniToolRegistryTests
{
    private static readonly OmniToolRegistry Registry = OmniToolRegistry.Build();

    [Fact]
    public void Registry_CataloguesTheServiceGraph()
    {
        Assert.NotEmpty(Registry.Services);
        Assert.NotEmpty(Registry.Operations);

        // Every OmniService that survived discovery must be reachable by its key.
        foreach (var service in Registry.Services)
            Assert.Same(service, Registry.GetService(service.Key));
    }

    [Fact]
    public void EveryGeneratedTool_IsWellFormedForTheProvider()
    {
        var tools = Registry.Tools.Select(OmniToolCatalog.BuildFoldedTool).ToList();
        var names = tools.Select(t => t.function.name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());

        foreach (var tool in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.function.name));
            Assert.False(string.IsNullOrWhiteSpace(tool.function.description));

            // Provider tool names must match ^[a-zA-Z0-9_-]+$.
            Assert.All(tool.function.name, c =>
                Assert.True(char.IsLetterOrDigit(c) || c == '_' || c == '-',
                    $"'{tool.function.name}' contains an illegal character '{c}'."));

            var schema = Assert.IsType<JObject>(tool.function.parameters);
            Assert.Equal("object", schema["type"]?.Value<string>());

            // Every folded tool must declare its op selector, or the model cannot pick an operation.
            var op = Assert.IsType<JObject>(schema["properties"]?["op"]);
            var enumValues = Assert.IsType<JArray>(op["enum"]);
            Assert.NotEmpty(enumValues);
            Assert.Contains("op", (schema["required"] as JArray)!.Select(t => t.Value<string>()));
        }
    }

    [Fact]
    public void EveryOperation_IsReachableThroughTheToolThatOffersIt()
    {
        foreach (var tool in Registry.Tools)
            foreach (var op in tool.Operations)
                Assert.Same(op, Registry.FindOnTool(tool.ToolName, op.Op));

        // And through the universal tool, by service key.
        foreach (var service in Registry.Services)
            foreach (var op in service.Operations)
                Assert.Same(op, Registry.FindOnService(service.Key, op.Op));
    }

    [Fact]
    public void OperationSchemas_BindBackToTheirMethodSignature()
    {
        // For every op, build a payload that fills every declared property with a plausible value of
        // the declared type, then bind it. This is what catches drift when someone changes a service
        // method signature without touching its annotation.
        foreach (var op in Registry.Operations)
        {
            var payload = SaturatePayload(op.ParameterSchema);

            var contract = ToolArgumentContract.ValidateAndNormalize(op.ParameterSchema, payload.ToString());
            Assert.True(contract.IsValid, $"{op} rejected its own saturated payload: {contract.ErrorText}");

            var bound = OmniToolSchema.BindArguments(op, contract.Normalized!, CancellationToken.None);

            Assert.Equal(op.Method.GetParameters().Length, bound.Length);
            for (int i = 0; i < bound.Length; i++)
            {
                var parameterType = op.Method.GetParameters()[i].ParameterType;
                if (bound[i] == null)
                {
                    Assert.False(parameterType.IsValueType && Nullable.GetUnderlyingType(parameterType) == null,
                        $"{op} bound null to non-nullable value parameter '{op.Method.GetParameters()[i].Name}'.");
                    continue;
                }
                Assert.True(parameterType.IsInstanceOfType(bound[i]),
                    $"{op} bound {bound[i]!.GetType().Name} to parameter "
                  + $"'{op.Method.GetParameters()[i].Name}' of type {parameterType.Name}.");
            }
        }
    }

    [Fact]
    public void OmittedOptionalArguments_FallBackToTheMethodDefault()
    {
        // An empty payload must bind for any op with no required arguments, and must produce the
        // method's own defaults rather than nulls - otherwise "list with no filters" throws.
        foreach (var op in Registry.Operations)
        {
            var required = (op.ParameterSchema["required"] as JArray)?.Count ?? 0;
            if (required > 0) continue;

            var contract = ToolArgumentContract.ValidateAndNormalize(op.ParameterSchema, "{}");
            Assert.True(contract.IsValid, $"{op} rejected an empty payload: {contract.ErrorText}");

            var bound = OmniToolSchema.BindArguments(op, contract.Normalized!, CancellationToken.None);
            var parameters = op.Method.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
            {
                // An injected CancellationToken also reports HasDefaultValue; the invoker deliberately
                // overrides it with the live run token, so it is not a default to check.
                if (OmniToolSchema.IsInjected(parameters[i].ParameterType)) continue;
                if (parameters[i].HasDefaultValue)
                    Assert.Equal(parameters[i].DefaultValue, bound[i]);
            }
        }
    }

    [Fact]
    public void ReflectiveOperations_AreAlwaysReadOnly()
    {
        // The entire safety story for unannotated services: nothing classified whether they write, so
        // none of them may.
        foreach (var op in Registry.Operations.Where(o => !o.Verified))
        {
            Assert.False(op.Mutating, $"{op} is reflective but marked mutating.");
            Assert.False(op.Destructive, $"{op} is reflective but marked destructive.");
        }

        foreach (var service in Registry.Services.Where(s => !s.Annotated))
        {
            Assert.All(service.Operations, o => Assert.False(o.Verified));
            // Unannotated services get no dedicated tools - they are reached through omniservice.
            Assert.Empty(service.Groups);
        }
    }

    [Fact]
    public void DestructiveOperations_AreAlsoMarkedMutating()
    {
        // Mutating is what keeps an op out of the parallel read path. A destructive op that was not
        // also mutating could be speculatively pre-launched, which is exactly the wrong outcome.
        foreach (var op in Registry.Operations.Where(o => o.Destructive))
            Assert.True(op.Mutating, $"{op} is destructive but not marked mutating.");
    }

    [Fact]
    public void EveryVerifiedOperation_CarriesAWrittenDescription()
    {
        foreach (var op in Registry.Operations.Where(o => o.Verified))
        {
            Assert.False(string.IsNullOrWhiteSpace(op.Description), $"{op} has no description.");
            Assert.True(op.Description.Length >= 15,
                $"{op} description is too thin for a model to choose on: '{op.Description}'.");
        }
    }

    [Fact]
    public void OfferedToolArray_IsByteStableAcrossBuilds()
    {
        // The offered tool array is part of the request prefix. If it differs between two builds with
        // identical settings, every request misses the provider's prefix cache.
        string[] pinned = { "omniscience_*", "omnitrader", "klivemail" };

        string Render(OmniToolRegistry registry) => Newtonsoft.Json.JsonConvert.SerializeObject(
            OmniToolCatalog.BuildServiceTools(registry, pinned, offerAll: false));

        Assert.Equal(Render(Registry), Render(Registry));
        Assert.Equal(Render(Registry), Render(OmniToolRegistry.Build()));

        var universalA = Newtonsoft.Json.JsonConvert.SerializeObject(OmniToolCatalog.BuildUniversalTools(true));
        var universalB = Newtonsoft.Json.JsonConvert.SerializeObject(OmniToolCatalog.BuildUniversalTools(true));
        Assert.Equal(universalA, universalB);
    }

    [Fact]
    public void UniversalTools_AreAlwaysWellFormed()
    {
        var tools = OmniToolCatalog.BuildUniversalTools(includeApi: true);
        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t.function.name == OmniToolCatalog.UniversalServiceTool);
        Assert.Contains(tools, t => t.function.name == OmniToolCatalog.UniversalApiTool);

        Assert.Single(OmniToolCatalog.BuildUniversalTools(includeApi: false));
    }

    [Fact]
    public void PinningAcceptsServiceKeysGroupNamesAndWildcards()
    {
        var anyGrouped = Registry.Tools.FirstOrDefault(t => t.Group() != null);
        if (anyGrouped == null) return; // no grouped service annotated yet

        var byExactName = OmniToolCatalog.BuildServiceTools(Registry, new[] { anyGrouped.ToolName }, false);
        Assert.Contains(byExactName, t => t.function.name == anyGrouped.ToolName);

        var byServiceKey = OmniToolCatalog.BuildServiceTools(Registry, new[] { anyGrouped.Service.Key }, false);
        Assert.Equal(anyGrouped.Service.Groups.Count, byServiceKey.Count);

        var byWildcard = OmniToolCatalog.BuildServiceTools(Registry, new[] { anyGrouped.Service.Key + "_*" }, false);
        Assert.NotEmpty(byWildcard);
    }

    [Fact]
    public void SnakeCaseConversion_HandlesAcronymRuns()
    {
        Assert.Equal("get_person_dossier", OmniToolRegistry.ToSnakeCase("GetPersonDossier"));
        Assert.Equal("run_ocr_pass", OmniToolRegistry.ToSnakeCase("RunOCRPass"));
        Assert.Equal("list", OmniToolRegistry.ToSnakeCase("List"));
        Assert.Equal("get_big_five_series", OmniToolRegistry.ToSnakeCase("GetBigFiveSeries"));
    }

    // -- helpers --

    /// <summary>Builds a payload filling every declared property with a value of its declared type,
    /// the same trick ProjectToolFacadeTests uses to prove a schema is satisfiable.</summary>
    private static JObject SaturatePayload(JObject schema)
    {
        var payload = new JObject();
        if (schema["properties"] is not JObject properties) return payload;
        foreach (var prop in properties.Properties())
            payload[prop.Name] = ProviderDefault(prop.Value as JObject);
        return payload;
    }

    private static JToken ProviderDefault(JObject? schema)
    {
        if (schema?["enum"] is JArray { Count: > 0 } enumValues) return enumValues[0].DeepClone();

        // A string with a declared format only parses in one shape, so honour it - otherwise the
        // saturated payload would fail to bind for reasons that say nothing about the schema.
        var format = schema?["format"]?.Value<string>();
        if (format != null)
            return new JValue(format switch
            {
                "date-time" => "2026-08-28T14:00:00Z",
                "duration" => "00:05:00",
                "uuid" => "00000000-0000-0000-0000-000000000000",
                _ => "provider-default",
            });

        return (schema?["type"]?.Value<string>()) switch
        {
            "integer" => new JValue(1),
            "number" => new JValue(1.0),
            "boolean" => new JValue(false),
            "array" => new JArray(),
            "object" => new JObject(),
            _ => new JValue("provider-default"),
        };
    }
}

internal static class OmniToolGroupTestExtensions
{
    /// <summary>The group a generated tool belongs to, or null when it is a service's base tool.</summary>
    public static string? Group(this OmniToolGroup tool) => tool.Operations.FirstOrDefault()?.Group;
}
