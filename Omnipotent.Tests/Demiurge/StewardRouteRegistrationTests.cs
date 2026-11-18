using System.Diagnostics;
using Omnipotent.Services.Demiurge;
using ApiService = Omnipotent.Services.KliveAPI.KliveAPI;

namespace Omnipotent.Tests.Demiurge;

public sealed class StewardRouteRegistrationTests
{
    [Fact]
    public void Cache_health_admission_serializes_count_and_waiters_with_distinct_camelcase_names()
    {
        var gate = new StewardWakeAdmission(() => 1);
        Assert.True(gate.TryAdmit("running", "commander", "go", out _));
        Assert.False(gate.TryAdmit("waiting", "worker", "go", out _));
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(DemiurgeRoutes.DescribeAdmission(gate.Describe()),
            new Newtonsoft.Json.JsonSerializerSettings
            {
                ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
            });
        var result = Newtonsoft.Json.Linq.JObject.Parse(json);
        Assert.Equal(1, (int)result["deferredCount"]!);
        var waiter = Assert.Single((Newtonsoft.Json.Linq.JArray)result["deferred"]!);
        Assert.Equal("waiting", (string?)waiter["stewardID"]);
        Assert.Equal(1, (int)result["active"]!);
    }

    [Fact]
    public async Task Core_routes_register_against_injected_api_without_service_lookup()
    {
        var api = new ApiService();
        var demiurge = new Omnipotent.Services.Demiurge.Demiurge(api);
        var stopwatch = Stopwatch.StartNew();

        await new DemiurgeRoutes(demiurge).RegisterRoutes().WaitAsync(TimeSpan.FromSeconds(2));

        stopwatch.Stop();
        Assert.True(api.ControllerLookup.ContainsKey("/demiurge/overview"));
        Assert.True(api.ControllerLookup.ContainsKey("/demiurge/get"));
        Assert.True(api.ControllerLookup.ContainsKey("/demiurge/analytics/all"));
        Assert.True(api.ControllerLookup.ContainsKey("/demiurge/cost-simulator"));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Core route registration took {stopwatch.Elapsed}.");
    }
}
