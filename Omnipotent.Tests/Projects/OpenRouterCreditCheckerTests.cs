using System.Net;
using System.Text;
using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects;

public class OpenRouterCreditCheckerTests
{
    [Fact]
    public async Task ZeroRemainingCredit_IsDetectedBeforeDispatch()
    {
        var http = Client(HttpStatusCode.OK, "{\"data\":{\"limit\":5,\"limit_remaining\":0}}");
        var checker = new OpenRouterCreditChecker(() => Task.FromResult<string?>("secret"), _ => { }, http);

        var result = await checker.CheckAsync();

        Assert.Equal(OpenRouterCreditStatus.Exhausted, result.Status);
        Assert.Equal(0, result.RemainingUsd);
    }

    [Fact]
    public async Task NullRemainingCredit_MeansUnlimitedKey()
    {
        var http = Client(HttpStatusCode.OK, "{\"data\":{\"limit\":null,\"limit_remaining\":null}}");
        var checker = new OpenRouterCreditChecker(() => Task.FromResult<string?>("secret"), _ => { }, http);

        var result = await checker.CheckAsync();

        Assert.Equal(OpenRouterCreditStatus.Available, result.Status);
        Assert.Null(result.RemainingUsd);
    }

    [Fact]
    public async Task UnavailablePreflight_FailsOpenToExistingProviderTelemetry()
    {
        var http = Client(HttpStatusCode.ServiceUnavailable, "{}");
        var checker = new OpenRouterCreditChecker(() => Task.FromResult<string?>("secret"), _ => { }, http);

        Assert.Equal(OpenRouterCreditStatus.Unknown, (await checker.CheckAsync()).Status);
    }

    private static HttpClient Client(HttpStatusCode status, string body) =>
        new(new StubHandler(status, body));

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://openrouter.ai/api/v1/key", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
