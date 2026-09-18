using System.Diagnostics;
using Omnipotent.Services.Projects;
using ApiService = Omnipotent.Services.KliveAPI.KliveAPI;

namespace Omnipotent.Tests.Projects;

public sealed class ProjectRouteRegistrationTests
{
    [Fact]
    public void Cache_health_admission_serializes_count_and_waiters_with_distinct_camelcase_names()
    {
        var gate = new ProjectWakeAdmission(() => 1);
        Assert.True(gate.TryAdmit("running", "commander", "go", out _));
        Assert.False(gate.TryAdmit("waiting", "worker", "go", out _));
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(ProjectsRoutes.DescribeAdmission(gate.Describe()),
            new Newtonsoft.Json.JsonSerializerSettings
            {
                ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
            });
        var result = Newtonsoft.Json.Linq.JObject.Parse(json);
        Assert.Equal(1, (int)result["deferredCount"]!);
        var waiter = Assert.Single((Newtonsoft.Json.Linq.JArray)result["deferred"]!);
        Assert.Equal("waiting", (string?)waiter["projectID"]);
        Assert.Equal(1, (int)result["active"]!);
    }

    [Fact]
    public async Task Core_routes_register_against_injected_api_without_service_lookup()
    {
        var api = new ApiService();
        var projects = new Omnipotent.Services.Projects.Projects(api);
        var stopwatch = Stopwatch.StartNew();

        await new ProjectsRoutes(projects).RegisterRoutes().WaitAsync(TimeSpan.FromSeconds(2));

        stopwatch.Stop();
        Assert.True(api.ControllerLookup.ContainsKey("/projects/overview"));
        Assert.True(api.ControllerLookup.ContainsKey("/projects/get"));
        Assert.True(api.ControllerLookup.ContainsKey("/projects/analytics/all"));
        Assert.True(api.ControllerLookup.ContainsKey("/projects/cost-simulator"));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Core route registration took {stopwatch.Elapsed}.");
    }
}
