using System.Text;
using System.Text.Json;
using Omnipotent.Services.Projects.Containers;

namespace Omnipotent.Tests.Projects;

[Collection("ProjectsSerial")]
public sealed class FreeBrowserChallengeTests
{
    private const string Probe = """
        {"ok":true,"detected":true,"interstitial":false,"url":"https://site.example",
         "documentId":"document-1","tab":{"id":"tab-1"},
         "widgets":[{"provider":"turnstile","sitekey":"K","id":"widget-1"}]}
        """;

    private static JsonElement Payload(string command)
    {
        string encoded = command.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1].Replace('-', '+').Replace('_', '/');
        encoded += new string('=', (4 - encoded.Length % 4) % 4);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        return doc.RootElement.Clone();
    }

    [Theory]
    [InlineData("""{"ok":true,"state":"response-present"}""", true)]
    [InlineData("""{"ok":true,"state":"challenge-cleared"}""", true)]
    [InlineData("""{"ok":true}""", false)]
    [InlineData("""{"ok":false,"state":"response-present"}""", false)]
    [InlineData("""{"ok":true,"state":"processing"}""", false)]
    [InlineData("[]", false)]
    [InlineData("not-json", false)]
    public void TransportSuccessAloneIsNotFreeSolveSuccess(string json, bool expected)
        => Assert.Equal(expected, ContainerToolAdapter.FreeChallengeCompleted(json));

    [Theory]
    [InlineData("""{"ok":true,"state":"response-present"}""", true)]
    [InlineData("""{"ok":false,"error":{"code":"free-solver-unresolved","message":"Quota exhausted; request_human."}}""", false)]
    [InlineData("""{"ok":true}""", false)]
    public async Task DefaultFreeModeNeverReadsPaidCredentials(string response, bool success)
    {
        string? previous = Environment.GetEnvironmentVariable("PROJECTS_CAPTCHA_ALLOW_PAID");
        Environment.SetEnvironmentVariable("PROJECTS_CAPTCHA_ALLOW_PAID", null);
        try
        {
            Assert.False(ContainerToolAdapter.PaidChallengeFallbackEnabled());
            using var transport = new VncTransport("127.0.0.1", 1, _ => { });
            using var gate = new SemaphoreSlim(1, 1);
            int credentialReads = 0, waits = 0;
            var adapter = new ContainerToolAdapter(transport, "free-test", "worker", gate,
                dockerControlAsync: (_, _, _) => Task.CompletedTask,
                resolveSecretsAsync: _ => { credentialReads++; return Task.FromResult("A_FUNDED_PAID_KEY"); },
                terminalAsync: (command, _, _, _) =>
                {
                    var payload = Payload(command);
                    if (payload.GetProperty("op").GetString() == "challenge_probe")
                        return Task.FromResult(new ContainerShellResult(0, Probe, "", false, false));
                    Assert.Equal("challenge_wait", payload.GetProperty("op").GetString());
                    Assert.Equal("tab-1", payload.GetProperty("tabId").GetString());
                    Assert.Equal(90_000, payload.GetProperty("timeoutMs").GetInt32());
                    waits++;
                    return Task.FromResult(new ContainerShellResult(0, response, "", false, false));
                });
            var result = await adapter.ExecuteAsync("computer_browser_action", """{"op":"solve_challenge"}""");
            Assert.Equal(success, result.Success);
            Assert.Equal(0, credentialReads);
            Assert.Equal(1, waits);
            Assert.False(transport.Connected);
            if (success) Assert.Contains("acceptance is unverified", result.Text);
            else
            {
                // Adapters are recreated between tool dispatches; cooldown must survive that.
                var second = new ContainerToolAdapter(transport, "free-test", "worker", gate,
                    dockerControlAsync: (_, _, _) => Task.CompletedTask,
                    terminalAsync: (command, _, _, _) =>
                    {
                        Assert.Equal("challenge_probe", Payload(command).GetProperty("op").GetString());
                        return Task.FromResult(new ContainerShellResult(0, Probe, "", false, false));
                    });
                var retried = await second.ExecuteAsync("computer_browser_action", """{"op":"solve_challenge"}""");
                Assert.False(retried.Success);
                Assert.Contains("cooling down", retried.Text);
            }
        }
        finally { Environment.SetEnvironmentVariable("PROJECTS_CAPTCHA_ALLOW_PAID", previous); }
    }

    [Fact]
    public async Task AlreadyAnsweredWidgetDoesNotWaitOrPurchase()
    {
        using var transport = new VncTransport("127.0.0.1", 1, _ => { });
        using var gate = new SemaphoreSlim(1, 1);
        int calls = 0;
        var adapter = new ContainerToolAdapter(transport, "answered", "worker", gate,
            dockerControlAsync: (_, _, _) => Task.CompletedTask,
            resolveSecretsAsync: _ => throw new Exception("Must not read credentials"),
            terminalAsync: (command, _, _, _) =>
            {
                Assert.Equal("challenge_probe", Payload(command).GetProperty("op").GetString());
                calls++;
                return Task.FromResult(new ContainerShellResult(0, """
                    {"ok":true,"detected":false,"widgets":[
                     {"id":"one","sitekey":"K","provider":"turnstile","responsePresent":true}]}
                    """, "", false, false));
            });
        var result = await adapter.ExecuteAsync("computer_browser_action", """{"op":"solve_challenge"}""");
        Assert.True(result.Success);
        Assert.Contains("already present", result.Text);
        Assert.Equal(1, calls);
    }
}
