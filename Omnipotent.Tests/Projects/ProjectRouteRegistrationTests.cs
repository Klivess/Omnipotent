using System.Diagnostics;
using Omnipotent.Services.Projects;
using ApiService = Omnipotent.Services.KliveAPI.KliveAPI;

namespace Omnipotent.Tests.Projects;

public sealed class ProjectRouteRegistrationTests
{
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
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Core route registration took {stopwatch.Elapsed}.");
    }
}
