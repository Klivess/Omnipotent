using Omnipotent.Services.Projects.Containers;
using Omnipotent.Services.Projects;

namespace Omnipotent.Tests.Projects;

[Collection("ProjectsSerial")]
public sealed class BrowserProfileHandoffTests
{
    [Fact]
    public void Copy_ReplacesDestinationKeepsSource_AndDropsChromiumRuntimeLocks()
    {
        string root = Path.Combine(Path.GetTempPath(), "omnipotent-profile-handoff-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string destination = Path.Combine(root, "destination");
        try
        {
            Directory.CreateDirectory(Path.Combine(source, "Default"));
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(source, "Default", "Cookies"), "opaque-session-state");
            File.WriteAllText(Path.Combine(source, "Preferences"), "{\"signin\":true}");
            File.WriteAllText(Path.Combine(source, "SingletonLock"), "must-not-transfer");
            File.WriteAllText(Path.Combine(source, ".omnipotent-launch.lock"), "must-not-transfer");
            File.WriteAllText(Path.Combine(destination, "old-profile"), "replace-me");

            var result = BrowserProfileHandoff.Copy(source, destination);

            Assert.True(result.ReplacedDestination);
            Assert.True(result.DestinationPreviouslyExisted);
            Assert.Equal("opaque-session-state", File.ReadAllText(Path.Combine(destination, "Default", "Cookies")));
            Assert.Equal("opaque-session-state", File.ReadAllText(Path.Combine(source, "Default", "Cookies")));
            Assert.False(File.Exists(Path.Combine(destination, "old-profile")));
            Assert.False(File.Exists(Path.Combine(destination, "SingletonLock")));
            Assert.False(File.Exists(Path.Combine(destination, ".omnipotent-launch.lock")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("commander", "commander")]
    [InlineData("worker/a?", "worker_a_")]
    [InlineData("", "shared")]
    public void ProfileSegment_IsStableAndFilesystemSafe(string agentID, string expected)
        => Assert.Equal(expected, BrowserProfileHandoff.ProfileSegment(agentID));

    [Fact]
    public async Task TakeSession_BrowserOperationDelegatesWithoutReadingOrReturningSessionValues()
    {
        using var transport = new VncTransport("127.0.0.1", 1, _ => { });
        using var gate = new SemaphoreSlim(1, 1);
        string? source = null;
        var adapter = new ContainerToolAdapter(transport, "destination", "worker-b", gate,
            terminalAsync: (_, _, _, _) => Task.FromResult(new ContainerShellResult(0, "", "", false, false)),
            takeBrowserSessionAsync: (sourceAgentId, _) =>
            {
                source = sourceAgentId;
                return Task.FromResult("Browser session taken and Chromium restarted.");
            });

        var result = await adapter.ExecuteAsync("computer_browser_action",
            """{"op":"take_session","sourceAgentId":"commander"}""");

        Assert.True(result.Success);
        Assert.Equal("commander", source);
        Assert.DoesNotContain("token=", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("PerAgentContainers", DesktopAllocationMode.PerAgentContainers)]
    [InlineData("SharedDesktopWithInputLock", DesktopAllocationMode.SharedDesktopWithInputLock)]
    [InlineData("shared", DesktopAllocationMode.SharedDesktopWithInputLock)]
    public void DesktopAllocation_WireValuesParse(string value, DesktopAllocationMode expected)
    {
        Assert.True(ProjectDesktopAllocation.TryParse(value, out var actual));
        Assert.Equal(expected, actual);
    }
}
