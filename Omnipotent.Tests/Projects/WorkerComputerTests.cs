using System.Net;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.ComputerControl;
using Omnipotent.Services.Projects;
using Omnipotent.Services.Projects.Computers;

namespace Omnipotent.Tests.Projects;

public class WorkerComputerTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    [Fact]
    public async Task LostMutationResponseReturnsIdentityWithoutReplay()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; throw new HttpRequestException("Disconnected after dispatch"); })) { BaseAddress = new Uri("https://worker.test") };
        var controller = new WorkerComputerController(new WorkerClient(http), "project", "ka-one", "agent");
        var result = await controller.ExecuteComputerActionAsync(new ComputerActionRequest("computer_click", "{\"operationID\":\"click-once\",\"x\":10,\"y\":20}"));
        Assert.Equal(1, calls);
        Assert.False(result.Success);
        Assert.Contains("click-once", result.Text);
        Assert.Contains("Do not replay", result.Text);
    }

    [Fact]
    public async Task PollingAnExistingJobDoesNotLaunchACommand()
    {
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Contains("jobs/existing?projectID=project&cursor=22", request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"id\":\"existing\",\"state\":\"running\",\"output\":\"progress\",\"cursor\":30,\"result\":{}}") });
        })) { BaseAddress = new Uri("https://worker.test") };
        var controller = new WorkerComputerController(new WorkerClient(http), "project", "ka-one", "agent");
        var result = await controller.ExecuteComputerActionAsync(new ComputerActionRequest("computer_terminal", "{\"jobID\":\"existing\",\"cursor\":22}"));
        Assert.Contains("progress", result.Text);
        Assert.Contains("do not resubmit", result.Text);
    }

    [Fact]
    public void PendingAndUncertainActionsAreNotReportedCompleted()
    {
        foreach (string state in new[] { "queued", "running", "outcome_unknown", "interrupted", "failed" })
        {
            var result = WorkerComputerController.Describe(JObject.FromObject(new { id = "one", state, result = new { } }), false);
            Assert.False(result.Success);
            Assert.Equal(state, result.Error);
        }
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("a/b")]
    [InlineData("a?projectID=other")]
    public void WorkerIdentifiersCannotChangeRequestScope(string value) => Assert.Throws<ArgumentException>(() => WorkerClient.Segment(value));

    [Fact]
    public void GenericSettingsCannotSwitchProvidersBeforeMigration()
    {
        var settings = new ProjectSettings();
        Assert.Equal("docker", settings.ComputerProvider);
        Assert.False(settings.TrySet("ComputerProvider", "incus"));
    }
}
