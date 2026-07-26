using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects;

/// <summary>
/// Per-route LLM parameter configuration: what the catalog reports as settable, what a route may
/// store, and what actually reaches a request.
/// </summary>
public class ModelParameterCatalogTests
{
    // ── validation ──

    [Fact]
    public void Normalize_ClampsOutOfRangeValuesAndRoundsIntegers()
    {
        var result = ModelParameterCatalog.Normalize(new Dictionary<string, JToken>
        {
            ["temperature"] = 9.5,     // above the 0–2 range
            ["top_p"] = -1,            // below the 0–1 range
            ["top_k"] = 40.6,          // integer parameter given a fraction
            ["frequency_penalty"] = -2.5,
        });

        Assert.Equal(2d, result["temperature"].Value<double>());
        Assert.Equal(0d, result["top_p"].Value<double>());
        Assert.Equal(41, result["top_k"].Value<int>());
        Assert.Equal(-2d, result["frequency_penalty"].Value<double>());
    }

    [Fact]
    public void Normalize_DropsUnknownAndProtocolOwnedParameters()
    {
        var result = ModelParameterCatalog.Normalize(new Dictionary<string, JToken>
        {
            ["temperature"] = 0.4,
            ["not_a_parameter"] = 1,
            // The agent loop owns these; a stored value would break tool calling rather than tune it.
            ["tools"] = new JArray(),
            ["tool_choice"] = "auto",
            ["response_format"] = new JObject(),
            ["max_tokens"] = 4096,
        });

        Assert.Equal(new[] { "temperature" }, result.Keys);
    }

    [Fact]
    public void Normalize_AcceptsEnumOptionsCaseInsensitivelyAndRejectsOthers()
    {
        var accepted = ModelParameterCatalog.Normalize(
            new Dictionary<string, JToken> { ["reasoning"] = "HIGH" });
        Assert.Equal("high", accepted["reasoning"].Value<string>());

        Assert.Empty(ModelParameterCatalog.Normalize(
            new Dictionary<string, JToken> { ["reasoning"] = "maximum" }));
    }

    [Fact]
    public void Normalize_ParsesStringNumbersAndRejectsUnusableValues()
    {
        var result = ModelParameterCatalog.Normalize(new Dictionary<string, JToken>
        {
            ["temperature"] = "0.35",
            ["top_p"] = "not a number",
            ["min_p"] = JValue.CreateNull(),
        });

        Assert.Equal(new[] { "temperature" }, result.Keys);
        Assert.Equal(0.35, result["temperature"].Value<double>());
    }

    // ── projection onto a request ──

    [Fact]
    public void ToSamplingParameters_CarriesOnlyPinnedValues()
    {
        var parameters = ModelParameterCatalog.ToSamplingParameters(
            ModelParameterCatalog.Normalize(new Dictionary<string, JToken>
            {
                ["temperature"] = 0.2,
                ["top_k"] = 20,
            }));

        Assert.NotNull(parameters);
        Assert.Equal(0.2, parameters!.Temperature);
        Assert.Equal(20, parameters.TopK);
        Assert.Null(parameters.TopP);
        Assert.Null(parameters.Seed);
    }

    [Fact]
    public void ToSamplingParameters_ReasoningIsCarriedSeparatelyAndNeverAsSampling()
    {
        var values = ModelParameterCatalog.Normalize(
            new Dictionary<string, JToken> { ["reasoning"] = "low" });

        // Reasoning rides KliveLLM's thinking-override path, where the global ceiling clamps it.
        Assert.Equal("low", ModelParameterCatalog.ReasoningEffort(values));
        Assert.Null(ModelParameterCatalog.ToSamplingParameters(values));
    }

    [Fact]
    public void ToSamplingParameters_NoPinnedValuesMeansNoOverridesAtAll()
    {
        Assert.Null(ModelParameterCatalog.ToSamplingParameters(null));
        Assert.Null(ModelParameterCatalog.ToSamplingParameters(new Dictionary<string, JToken>()));
        Assert.Null(ModelParameterCatalog.ReasoningEffort(null));
    }

    // ── settings storage ──

    [Fact]
    public void Settings_StoreParametersPerRouteAndClampOnSet()
    {
        var settings = new ProjectSettings { ProjectID = "p1" };

        Assert.True(settings.TrySet("commanderParameters",
            JObject.Parse("{\"temperature\":0.15,\"top_p\":5,\"bogus\":1}")));
        Assert.True(settings.TrySet("tierTextParameters", JObject.Parse("{\"seed\":42}")));

        var commander = settings.ParametersForRoute(ProjectSettings.RouteNames.Commander);
        Assert.Equal(0.15, commander["temperature"].Value<double>());
        Assert.Equal(1d, commander["top_p"].Value<double>());
        Assert.False(commander.ContainsKey("bogus"));

        // Routes are independent — one route's parameters never leak into another.
        Assert.Equal(42, settings.ParametersForTier(ProjectAgentTier.Text)["seed"].Value<int>());
        Assert.Empty(settings.ParametersForRoute(ProjectSettings.RouteNames.Utility));
        Assert.Empty(settings.ParametersForTier(ProjectAgentTier.TextImage));
    }

    [Fact]
    public void Settings_EmptyObjectClearsARouteBackToModelDefaults()
    {
        var settings = new ProjectSettings { ProjectID = "p1" };
        settings.TrySet("councilParameters", JObject.Parse("{\"temperature\":0.9}"));
        Assert.NotEmpty(settings.ParametersForRoute(ProjectSettings.RouteNames.Council));

        Assert.True(settings.TrySet("councilParameters", new JObject()));
        Assert.Empty(settings.ParametersForRoute(ProjectSettings.RouteNames.Council));
    }

    [Fact]
    public void Settings_RouteParametersReplacesEveryRouteAndIgnoresUnknownRouteNames()
    {
        var settings = new ProjectSettings { ProjectID = "p1" };
        settings.TrySet("commanderParameters", JObject.Parse("{\"temperature\":0.1}"));

        Assert.True(settings.TrySet("routeParameters", JObject.Parse(
            "{\"utility\":{\"temperature\":0.6},\"nonsense\":{\"temperature\":0.6}}")));

        Assert.Empty(settings.ParametersForRoute(ProjectSettings.RouteNames.Commander));
        Assert.Equal(0.6, settings.ParametersForRoute(ProjectSettings.RouteNames.Utility)["temperature"].Value<double>());
        Assert.Equal(new[] { ProjectSettings.RouteNames.Utility }, settings.RouteParameters.Keys);
    }

    [Fact]
    public void Settings_SurviveAJsonRoundTripAndAreReclampedOnLoad()
    {
        var settings = new ProjectSettings { ProjectID = "p1" };
        settings.TrySet("commanderParameters", JObject.Parse("{\"temperature\":0.3,\"reasoning\":\"high\"}"));

        var reloaded = JsonConvert.DeserializeObject<ProjectSettings>(JsonConvert.SerializeObject(settings))!;
        reloaded.NormalizeRoutes();

        var commander = reloaded.ParametersForRoute(ProjectSettings.RouteNames.Commander);
        Assert.Equal(0.3, commander["temperature"].Value<double>());
        Assert.Equal("high", ModelParameterCatalog.ReasoningEffort(commander));

        // A hand-edited file cannot smuggle an out-of-range value past load-time normalization.
        var tampered = JsonConvert.DeserializeObject<ProjectSettings>(
            "{\"RouteParameters\":{\"commander\":{\"temperature\":99,\"junk\":1}}}")!;
        tampered.NormalizeRoutes();
        var clamped = tampered.ParametersForRoute(ProjectSettings.RouteNames.Commander);
        Assert.Equal(2d, clamped["temperature"].Value<double>());
        Assert.False(clamped.ContainsKey("junk"));
    }

    [Fact]
    public void Settings_DefaultProjectPinsNothing()
    {
        var settings = new ProjectSettings { ProjectID = "p1" };
        settings.NormalizeRoutes();

        Assert.Empty(settings.RouteParameters);
        foreach (string route in ProjectSettings.RouteNames.All)
            Assert.Null(ModelParameterCatalog.ToSamplingParameters(settings.ParametersForRoute(route)));
    }

    // ── live capability discovery ──

    [Fact]
    public async Task ResolveParameters_ReportsWhatEachRouteModelAdvertises()
    {
        var handler = new CatalogHandler(Catalog(
            Model("vendor/full", "temperature", "top_p", "top_k", "seed"),
            Model("vendor/basic", "temperature")));
        using var http = new HttpClient(handler);
        var resolver = new OpenRouterContextWindowResolver(
            () => Task.FromResult<string?>("secret"), _ => { }, http);

        var support = await resolver.ResolveParametersAsync(new[] { "vendor/full", "vendor/basic" });

        Assert.All(support, s => Assert.True(s.FromCatalog));
        Assert.Contains("top_k", support.Single(s => s.RequestedModel == "vendor/full").SupportedParameters);
        Assert.DoesNotContain("top_k", support.Single(s => s.RequestedModel == "vendor/basic").SupportedParameters);

        // Only temperature holds for the whole route, which is what the UI offers uncaveated.
        var common = support.Select(s => s.SupportedParameters)
            .Aggregate((a, b) => a.Intersect(b, StringComparer.OrdinalIgnoreCase).ToList());
        Assert.Equal(new[] { "temperature" }, common);
    }

    [Fact]
    public async Task ResolveParameters_UnknownModelReportsUnknownRatherThanUnsupported()
    {
        var handler = new CatalogHandler(Catalog(Model("vendor/known", "temperature")));
        using var http = new HttpClient(handler);
        var resolver = new OpenRouterContextWindowResolver(
            () => Task.FromResult<string?>(null), _ => { }, http);

        var support = await resolver.ResolveParametersAsync(new[] { "vendor/known", "vendor/unlisted" });

        var unlisted = support.Single(s => s.RequestedModel == "vendor/unlisted");
        Assert.False(unlisted.FromCatalog);
        Assert.Empty(unlisted.SupportedParameters);
    }

    [Fact]
    public async Task ResolveParameters_CatalogFailureIsNotTreatedAsSupportingNothing()
    {
        var handler = new CatalogHandler("{}", HttpStatusCode.ServiceUnavailable);
        using var http = new HttpClient(handler);
        var resolver = new OpenRouterContextWindowResolver(
            () => Task.FromResult<string?>("secret"), _ => { }, http);

        var support = await resolver.ResolveParametersAsync(new[] { "vendor/model" });

        Assert.False(Assert.Single(support).FromCatalog);
    }

    private static string Model(string id, params string[] supported) =>
        $$"""
          {
            "id": "{{id}}",
            "context_length": 128000,
            "top_provider": { "context_length": 128000, "max_completion_tokens": 8192 },
            "supported_parameters": [{{string.Join(",", supported.Select(s => $"\"{s}\""))}}]
          }
          """;

    private static string Catalog(params string[] models) =>
        "{\"data\":[" + string.Join(",", models) + "]}";

    private sealed class CatalogHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public AuthenticationHeaderValue? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
