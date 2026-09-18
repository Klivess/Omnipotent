using Omnipotent.Services.Projects;
using Omnipotent.Services.Projects.Containers;
using Docker.DotNet;
using System.Net;
using System.Text;

namespace Omnipotent.Tests.Projects;

public class ProjectComputerReliabilityTests
{
    [Fact]
    public async Task ExplicitFirefoxLaunchDoesNotSilentlyLaunchChromium()
    {
        using var transport = new VncTransport("127.0.0.1", 1, _ => { });
        ContainerDesktopControlCommand? operation = null;
        string? payload = null;
        var bridge = new ContainerDesktopCommandBridge(transport, (command, argument, _) =>
        {
            operation = command; payload = argument; return Task.CompletedTask;
        });
        await bridge.LaunchAsync("firefox", "https://example.test", CancellationToken.None);
        Assert.Equal(ContainerDesktopControlCommand.LaunchApplication, operation);
        Assert.Contains("firefox-esr", payload);
        Assert.Contains("https://example.test", payload);
        await bridge.FocusAsync("Browser notes - Mousepad", null, CancellationToken.None);
        Assert.Equal(ContainerDesktopControlCommand.FocusWindow, operation);
        Assert.Contains("Browser notes - Mousepad", payload);
    }

    [Fact]
    public void NativeDesktopReadinessDoesNotRequireTheBrowserHelper()
    {
        Assert.True(ContainerDesktopManager.DesktopControlIsReady(new Dictionary<string, string>
        {
            ["display"] = "up", ["desktop-shell"] = "up", ["panel"] = "up",
            ["window-manager"] = "up", ["vnc"] = "up", ["frame"] = "usable",
            ["chromium"] = "no", ["browser-inspect"] = "no",
        }));
    }

    [Theory]
    [InlineData("/definitely-not-an-installed-application", 127)]
    [InlineData("/usr/bin/false", 1)]
    [InlineData("/usr/bin/true", 0)]
    public async Task ApplicationLauncherReportsMissingAndImmediatelyFailingApplications(string app, int exit)
    {
        string bash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        if (!File.Exists(bash)) return;
        var result = await ContainerDependencyBootstrapper.RunProcessAsync(bash,
            new[] { "-lc", ContainerOrchestrator.ApplicationLaunchScriptForExec, "desktop-launch", app },
            TimeSpan.FromSeconds(15), CancellationToken.None);
        Assert.Equal(exit, result.ExitCode);
    }

    private sealed class FakeCredentials(HttpMessageHandler handler) : Credentials
    {
        public override bool IsTlsCredentials() => false;
        public override HttpMessageHandler GetHandler(HttpMessageHandler original) { original.Dispose(); return handler; }
    }

    private sealed class ComputerHost : HttpMessageHandler
    {
        public const string ID = "1234567890123456789012345678901234567890123456789012345678901234";
        public bool Running = true;
        public bool FailInventory;
        public List<string> Requests = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = request.RequestUri!.AbsolutePath;
            Requests.Add(request.Method + " " + path);
            if (path.EndsWith("/stop")) Running = false;
            if (path.EndsWith("/start")) Running = true;
            string content = "{}";
            if (path.EndsWith("/containers/json"))
            {
                if (FailInventory) throw new HttpRequestException("Engine unavailable");
                content = System.Text.Json.JsonSerializer.Serialize(new[] { new { Id = ID, State = Running ? "running" : "exited", Labels = new Dictionary<string, string>
                    { [ContainerLabels.Owner] = "projects", [ContainerLabels.ProjectID] = "project", [ContainerLabels.AgentID] = "commander" } } });
            }
            else if (path.EndsWith("/json"))
                content = System.Text.Json.JsonSerializer.Serialize(new { Id = ID, State = new { Running }, NetworkSettings = new
                { Ports = new Dictionary<string, object> { ["5901/tcp"] = new[] { new { HostIp = "127.0.0.1", HostPort = "45123" } },
                    ["5902/tcp"] = new[] { new { HostIp = "127.0.0.1", HostPort = "45124" } } } } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(content, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task IdleComputerSurvivesReconcileAndResumesTheSameContainerWithFreshPorts()
    {
        string folder = Path.Combine(Path.GetTempPath(), "desktop-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new ContainerRegistry(_ => { }, Path.Combine(folder, "registry.json"));
            var record = new DesktopContainerRecord { ContainerID = ComputerHost.ID, ProjectID = "project", AgentID = "commander" };
            registry.Add(record);
            using var host = new ComputerHost();
            using var client = new DockerClientConfiguration(new Uri("http://localhost"), new FakeCredentials(host)).CreateClient();
            var orchestrator = new ContainerOrchestrator(registry, client);
            await orchestrator.SuspendContainerAsync(record);
            Assert.False(host.Running);
            Assert.True(new ContainerRegistry(_ => { }, Path.Combine(folder, "registry.json")).All().Single().Suspended);
            await orchestrator.ReconcileAsync();
            Assert.False(host.Running); // Opening the desktop list must not wake idle computers.
            await orchestrator.ResumeContainerAsync(record);
            Assert.True(host.Running);
            Assert.False(record.Suspended);
            Assert.Equal(45123, record.VncHostPort);
            Assert.Equal(45124, record.BrowserServiceHostPort);
            Assert.Equal(ComputerHost.ID, registry.All().Single().ContainerID);
            Assert.DoesNotContain(host.Requests, r => r.StartsWith("DELETE") || r.Contains("/create"));
            host.FailInventory = true;
            await Assert.ThrowsAsync<HttpRequestException>(() => orchestrator.ReconcileAsync());
            Assert.False(record.Lost); // A failed inventory cannot justify deleting computers.
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("commander", false)]
    [InlineData("Commander", false)]
    [InlineData("active-worker", false)]
    [InlineData("retired-worker", true)]
    public void CleanupKeepsTheCommanderAndSharedComputer(string? owner, bool retired)
    {
        Assert.Equal(retired, Omnipotent.Services.Projects.Projects.IsDesktopOwnerRetired(owner,
            new HashSet<string>(StringComparer.Ordinal) { "active-worker" }));
    }

    [Theory]
    [InlineData("npipe://./pipe/docker_engine", true)]
    [InlineData("npipe://localhost/pipe/dockerDesktopLinuxEngine", true)]
    [InlineData("npipe://other-host/pipe/docker_engine", false)]
    [InlineData("tcp://localhost:2375", false)]
    [InlineData("unix:///var/run/docker.sock", false)]
    public void HostRepairOnlyTargetsLocalDockerDesktop(string endpoint, bool expected) =>
        Assert.Equal(expected, ContainerDependencyBootstrapper.IsLocalDesktopEndpoint(endpoint));

    [Fact]
    public async Task UnavailableRemoteEngineDoesNotStartOrInstallLocalDocker()
    {
        var bootstrap = new ContainerDependencyBootstrapper(_ => { });
        Assert.False(await bootstrap.EnsureDaemonAsync(_ => Task.FromResult<string?>("offline"),
            dockerUri: "tcp://remote.example:2376"));
        Assert.Null(bootstrap.LastAttemptUtc);
        Assert.Contains("does not apply", bootstrap.LastStatus);
    }

    [Fact]
    public async Task HostCommandDrainsLargeStderrWithoutDeadlocking()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await ContainerDependencyBootstrapper.RunProcessAsync("powershell.exe",
            new[] { "-NoProfile", "-NonInteractive", "-Command",
                "[Console]::Error.Write(('x' * 100000)); [Console]::Out.Write('finished'); exit 7" },
            TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.Equal(7, result.ExitCode);
        Assert.Contains("finished", result.Output);
        Assert.True(result.Output.Length <= 2000);
    }

    [Fact]
    public async Task HostCommandTimeoutStopsRecoveryFromWaitingForever()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await ContainerDependencyBootstrapper.RunProcessAsync("powershell.exe",
            new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60" },
            TimeSpan.FromMilliseconds(200), CancellationToken.None);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("Timed out", result.Output);
    }
}
