using System.Net;
using System.Text.Json;
using Omnipotent.Services.Projects.Containers;

namespace Omnipotent.Tests.Projects;

public sealed class BrowserChallengeRecoveryTests
{
    private static readonly BrowserChallengeSolver.ChallengeWidget Widget = new("turnstile", "site-key", false, "login");
    private static readonly BrowserChallengeClient.Credential[] Keys = [new("capsolver", "SECRET_ONE"), new("2captcha", "SECRET_TWO")];
    private const string Ready = """{"errorId":0,"status":"ready","solution":{"token":"SOLVED_TOKEN"}}""";

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body) };

    [Theory]
    [InlineData("capsolver", "metadata", "cdata")]
    [InlineData("2captcha", null, "data")]
    [InlineData("anticaptcha", null, "cData")]
    public void TurnstileRequestUsesProviderSpecificMetadata(string service, string? parent, string cDataName)
    {
        string request = BrowserChallengeSolver.BuildCreateTaskRequest(service, "key",
            Widget with { CData = "challenge-data" }, "https://test.example/form");
        using var json = JsonDocument.Parse(request);
        var task = json.RootElement.GetProperty("task");
        var fields = parent == null ? task : task.GetProperty(parent);
        Assert.Equal("login", fields.GetProperty("action").GetString());
        Assert.Equal("challenge-data", fields.GetProperty(cDataName).GetString());
        Assert.False(task.TryGetProperty("pageAction", out _));
    }

    [Theory]
    [InlineData("2captcha", "pagedata")]
    [InlineData("anticaptcha", "chlPageData")]
    public void ManagedPageDataIsNotDiscarded(string service, string name)
    {
        using var request = JsonDocument.Parse(BrowserChallengeSolver.BuildCreateTaskRequest(service, "key",
            Widget with { PageData = "opaque-page-data" }, "https://test.example"));
        Assert.Equal("opaque-page-data", request.RootElement.GetProperty("task").GetProperty(name).GetString());
    }

    [Fact]
    public void EnterprisePayloadUsesTheActualSValue()
    {
        using var request = JsonDocument.Parse(BrowserChallengeSolver.BuildCreateTaskRequest("capsolver", "key",
            new("recaptcha_enterprise", "site-key", false, null) { DataS = "actual-s" }, "https://test.example"));
        Assert.Equal("actual-s", request.RootElement.GetProperty("task").GetProperty("enterprisePayload").GetProperty("s").GetString());
    }

    [Fact]
    public void ProbePreservesIdentityAndSelectsVisibleUnansweredWidget()
    {
        var probe = BrowserChallengeSolver.ParseProbe("""
            {"detected":true,"url":"https://test.example","documentId":"doc","tab":{"id":"tab-1"},
             "widgets":[
               {"id":"a","provider":"turnstile","sitekey":"answered","responsePresent":true},
               {"id":"b","provider":"turnstile","sitekey":"hidden","visible":false},
               {"id":"c","provider":"turnstile","sitekey":"visible","action":"login","cData":"extra","dataS":"s"}]}
            """);
        Assert.Equal("tab-1", probe.TabId);
        Assert.Equal("doc", probe.DocumentId);
        Assert.Equal("c", probe.Primary?.Id);
        Assert.Equal("extra", probe.Primary?.CData);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("""{"ok":true}""")]
    public void MalformedProbeDoesNotClaimNoCaptcha(string response)
        => Assert.NotNull(BrowserChallengeSolver.ParseProbe(response).Error);

    [Theory]
    [InlineData("""{"status":"failed"}""")]
    [InlineData("""{"errorId":"1","errorCode":"ERROR_ZERO_BALANCE"}""")]
    [InlineData("""{"errorCode":"ERROR_KEY_DOES_NOT_EXIST"}""")]
    [InlineData("""{"status":"nonsense"}""")]
    [InlineData("""{}""")]
    public void FailedOrMalformedResultTerminatesPolling(string response)
        => Assert.NotNull(BrowserChallengeSolver.ReadResult(response).Error);

    [Fact]
    public async Task EmptyAccountFallsThroughAndImmediateSolutionRequiresNoPolling()
    {
        var hosts = new List<string>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            hosts.Add(request.RequestUri!.Host);
            return Task.FromResult(Json(hosts.Count == 1
                ? """{"errorId":1,"errorCode":"ERROR_ZERO_BALANCE","errorDescription":"SECRET_ONE"}""" : Ready));
        }));
        var client = new BrowserChallengeClient(http, TimeSpan.Zero);
        var result = await client.SolveAsync(Keys, Widget, "https://test.example", TimeSpan.FromSeconds(1), default);
        Assert.Equal("SOLVED_TOKEN", result.Token);
        Assert.Equal("2captcha", result.Service);
        Assert.Equal(["api.capsolver.com", "api.2captcha.com"], hosts);
        hosts.Clear();
        // A separate agent call reuses the health state instead of retrying the known empty account.
        await client.SolveAsync(Keys, Widget, "https://test.example", TimeSpan.FromSeconds(1), default);
        Assert.DoesNotContain("api.capsolver.com", hosts);
    }

    [Fact]
    public async Task ReplacedKeyIsNotBlockedByTheOldKeysCooldown()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(++calls == 1
            ? """{"errorId":1,"errorCode":"ERROR_KEY_DOES_NOT_EXIST"}""" : Ready))));
        var client = new BrowserChallengeClient(http, TimeSpan.Zero);
        await client.SolveAsync([Keys[0]], Widget, "https://test.example", TimeSpan.FromSeconds(1), default);
        var result = await client.SolveAsync([new("capsolver", "REPLACEMENT_KEY")], Widget,
            "https://test.example", TimeSpan.FromSeconds(1), default);
        Assert.Equal("SOLVED_TOKEN", result.Token);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task TransientPollingOutageReusesThePaidTask()
    {
        int creates = 0, polls = 0;
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/createTask")
            {
                creates++;
                return Json("""{"errorId":0,"taskId":123}""");
            }
            string body = await request.Content!.ReadAsStringAsync(ct);
            Assert.Contains("123", body);
            return ++polls == 1 ? Json("temporary failure", HttpStatusCode.ServiceUnavailable) : Json(Ready);
        }));
        var client = new BrowserChallengeClient(http, TimeSpan.Zero);
        var result = await client.SolveAsync(Keys, Widget, "https://test.example", TimeSpan.FromSeconds(1), default);
        Assert.Equal("SOLVED_TOKEN", result.Token);
        Assert.Equal(1, creates);
        Assert.Equal(2, polls);
    }

    [Fact]
    public async Task SlowFirstProviderLeavesTimeForFallback()
    {
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            if (request.RequestUri!.Host == "api.capsolver.com") await Task.Delay(Timeout.Infinite, ct);
            return Json(Ready);
        }));
        var client = new BrowserChallengeClient(http, TimeSpan.Zero);
        var result = await client.SolveAsync(Keys, Widget, "https://test.example", TimeSpan.FromMilliseconds(400), default);
        Assert.Equal("2captcha", result.Service);
    }

    [Fact]
    public async Task UserCancellationNeverStartsTheFallback()
    {
        using var cancel = new CancellationTokenSource();
        int calls = 0;
        using var http = new HttpClient(new Handler(async (_, ct) =>
        {
            calls++;
            cancel.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return Json(Ready);
        }));
        var client = new BrowserChallengeClient(http, TimeSpan.Zero);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SolveAsync(
            Keys, Widget, "https://test.example", TimeSpan.FromSeconds(2), cancel.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExhaustedProvidersReturnRecoveryWithoutLeakingCredentials()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(Json("""{"errorId":1,"errorCode":"SECRET_ONE","errorDescription":"SECRET_TWO"}"""));
        }));
        var client = new BrowserChallengeClient(http, TimeSpan.Zero);
        var result = await client.SolveAsync(Keys, Widget, "https://test.example", TimeSpan.FromSeconds(1), default);
        Assert.Null(result.Token);
        Assert.Equal(2, calls);
        Assert.Contains("request_human", result.Error);
        Assert.DoesNotContain("SECRET_ONE", result.Error);
        Assert.DoesNotContain("SECRET_TWO", result.Error);
    }

    [Fact]
    public async Task UnsupportedProviderIsSkippedWithoutSendingItsKey()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Json(Ready)); }));
        var result = await new BrowserChallengeClient(http).SolveAsync([new("unknown", "secret")],
            Widget, "https://test.example", TimeSpan.FromSeconds(1), default);
        Assert.Null(result.Token);
        Assert.Equal(0, calls);
        Assert.Throws<ArgumentException>(() => BrowserChallengeSolver.EndpointFor("unknown", "createTask"));
    }
}
