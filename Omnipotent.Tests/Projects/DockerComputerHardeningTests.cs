using Newtonsoft.Json;
using Omnipotent.Services.Projects;
using Omnipotent.Services.Projects.Containers;

namespace Omnipotent.Tests.Projects;

/// <summary>
/// Pins the Docker computer hardening of 2026-09-23: memory admission that can never overcommit
/// Docker's VM, idle release, the WSL repair for the WSL_E_OS_NOT_SUPPORTED outage, and the
/// guardrail that stops project agents "repairing" shared host infrastructure again.
/// </summary>
public class DockerComputerHardeningTests
{
    private const long GiB = 1024L * 1024 * 1024;

    private static DesktopContainerRecord Desktop(string id, int idleMinutes, bool suspended = false) => new()
    {
        ContainerID = id, ProjectID = "p" + id, AgentID = "a" + id,
        LastUsedAt = DateTime.UtcNow.AddMinutes(-idleMinutes),
        CreatedAt = DateTime.UtcNow.AddDays(-1),
        Suspended = suspended,
    };

    // ── capacity ──

    [Fact]
    public void SumOfDesktopCeilingsNeverExceedsTheDockerVm()
    {
        foreach (long vm in new[] { 3 * GiB, 8 * GiB, 10 * GiB, 12 * GiB, 31 * GiB })
        {
            int slots = DesktopCapacityPolicy.MaxRunningDesktops(vm);
            Assert.True(slots * DesktopCapacityPolicy.DesktopMemoryLimitBytes + DesktopCapacityPolicy.DaemonReserveBytes <= vm,
                $"{slots} desktops overcommit a {vm / GiB} GiB VM");
        }
        Assert.Equal(1, DesktopCapacityPolicy.MaxRunningDesktops(0)); // unknown VM size: one, not none
        Assert.Equal(6, DesktopCapacityPolicy.MaxRunningDesktops(10 * GiB));
    }

    [Fact]
    public void AdmissionWithAFreeSlotEvictsNothing()
    {
        var running = new[] { Desktop("1", 60), Desktop("2", 60) };
        var evictions = DesktopCapacityPolicy.ChooseEvictions(running, 3, null, DateTime.UtcNow, _ => false);
        Assert.NotNull(evictions);
        Assert.Empty(evictions!);
    }

    [Fact]
    public void AdmissionEvictsTheLeastRecentlyUsedIdleDesktop()
    {
        var running = new[] { Desktop("recent", 1), Desktop("oldest", 90), Desktop("older", 30) };
        var evictions = DesktopCapacityPolicy.ChooseEvictions(running, 3, null, DateTime.UtcNow, _ => false);
        Assert.Equal(new[] { "oldest" }, evictions!.Select(e => e.ContainerID));
    }

    [Fact]
    public void AdmissionNeverEvictsARecentlyUsedOrPinnedDesktop()
    {
        var running = new[] { Desktop("recent", 1), Desktop("pinned", 90) };
        var evictions = DesktopCapacityPolicy.ChooseEvictions(running, 2, null, DateTime.UtcNow,
            r => r.ContainerID == "pinned");
        Assert.Null(evictions); // refuse to start rather than overcommit or stop a desktop in use
    }

    [Fact]
    public void ResumingDesktopDoesNotCountAgainstItself_AndStoppedOnesAreFree()
    {
        var running = new[] { Desktop("me", 60, suspended: true), Desktop("other", 60), Desktop("asleep", 60, suspended: true) };
        var evictions = DesktopCapacityPolicy.ChooseEvictions(running, 2, "me", DateTime.UtcNow, _ => false);
        Assert.Empty(evictions!);
    }

    [Fact]
    public void IdleMeansUnusedPastTheWindowAndStillRunning()
    {
        var window = TimeSpan.FromMinutes(20);
        Assert.True(DesktopCapacityPolicy.IsIdle(Desktop("a", 25), DateTime.UtcNow, window));
        Assert.False(DesktopCapacityPolicy.IsIdle(Desktop("b", 5), DateTime.UtcNow, window));
        Assert.False(DesktopCapacityPolicy.IsIdle(Desktop("c", 25, suspended: true), DateTime.UtcNow, window));
        Assert.False(DesktopCapacityPolicy.IsIdle(Desktop("d", 25), DateTime.UtcNow, TimeSpan.Zero)); // 0 disables
    }

    [Fact]
    public void CpuPercentIsPerCoreAndZeroForBadSamples()
    {
        // 0.5 s of CPU over 4 s of 4-core system time = 12.5% of the machine = 50% of one core.
        Assert.Equal(50, DesktopCapacityPolicy.CpuPercentOfOneCore(1_500_000_000, 1_000_000_000, 8_000_000_000, 4_000_000_000, 4), 3);
        Assert.Equal(0, DesktopCapacityPolicy.CpuPercentOfOneCore(1, 1, 2, 1, 4));
    }

    [Fact]
    public void WslCapLeavesWindowsSixGigabytesWithinBounds()
    {
        Assert.Equal(10 * GiB, DesktopCapacityPolicy.WslMemoryCapBytes(16 * GiB));
        Assert.Equal(3 * GiB, DesktopCapacityPolicy.WslMemoryCapBytes(8 * GiB));
        Assert.Equal(12 * GiB, DesktopCapacityPolicy.WslMemoryCapBytes(64 * GiB));
    }

    // ── WSL ──

    [Fact]
    public void OnlyTheOsNotSupportedSignatureBelowBuild19044TriggersPackageRemoval()
    {
        string output = "Windows 10.0.19042.1288 does not support the packaged version of WSL.\r\nError code: Wsl/WSL_E_OS_NOT_SUPPORTED";
        Assert.True(WslHostCompatibility.IndicatesIncompatiblePackage(output, 19042));
        Assert.True(WslHostCompatibility.IndicatesIncompatiblePackage("W\0S\0L\0_\0E\0_\0O\0S\0_\0N\0O\0T\0_\0S\0U\0P\0P\0O\0R\0T\0E\0D", 19042));
        Assert.False(WslHostCompatibility.IndicatesIncompatiblePackage(output, 19045)); // supported build: never remove
        Assert.False(WslHostCompatibility.IndicatesIncompatiblePackage("Default Version: 2", 19042));
    }

    [Fact]
    public void TheWsl2KernelUpdateIsNeverMistakenForTheStandalonePackage()
    {
        Assert.True(WslHostCompatibility.IsStandaloneWslPackage("Windows Subsystem for Linux", "Microsoft Corporation"));
        Assert.False(WslHostCompatibility.IsStandaloneWslPackage("Windows Subsystem for Linux Update", "Microsoft Corporation"));
        Assert.False(WslHostCompatibility.IsStandaloneWslPackage("Windows Subsystem for Linux", "Someone Else"));
    }

    [Fact]
    public void MemoryCapIsAddedButNeverOverridesTheOwner()
    {
        string fresh = WslHostCompatibility.WithMemoryCap("", 10 * GiB);
        Assert.Contains("[wsl2]", fresh);
        Assert.Contains("memory=10GB", fresh);
        Assert.Contains("swap=2GB", fresh);

        string existingSection = "[wsl2]\r\nprocessors=2\r\n";
        string merged = WslHostCompatibility.WithMemoryCap(existingSection, 10 * GiB);
        Assert.Contains("processors=2", merged);
        Assert.Contains("memory=10GB", merged);
        Assert.Contains("\r\n", merged); // keeps the owner's line endings

        string owned = "[wsl2]\nmemory=4GB\n";
        Assert.Equal(owned, WslHostCompatibility.WithMemoryCap(owned, 10 * GiB));
    }

    // ── host-infrastructure guardrail ──

    private static string Ps(string script) => JsonConvert.SerializeObject(new { script });

    [Theory]
    [InlineData("wsl --update")]
    [InlineData("wsl.exe --shutdown")]
    [InlineData("wsl --unregister docker-desktop-data")]
    [InlineData("msiexec /i wsl.2.5.10.0.x64.msi /qn")]
    [InlineData("& 'C:\\Program Files\\Docker\\Docker\\Docker Desktop.exe' --reset")]
    [InlineData("Stop-Process -Name 'com.docker.backend' -Force")]
    [InlineData("Restart-Service LxssManager")]
    [InlineData("Enable-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform")]
    [InlineData("Restart-Computer -Force")]
    [InlineData("docker rm -f omniproj-37bd0132-commande-cf52a7")]
    [InlineData("docker system prune -af")]
    [InlineData("Set-Content $env:USERPROFILE\\.wslconfig '[wsl2]'")]
    [InlineData("winget install Microsoft.WSL")]
    public void AgentsCannotMutateSharedHostInfrastructure(string script)
    {
        string? violation = ProjectHostInfrastructurePolicy.FindViolation("run_powershell", Ps(script));
        Assert.NotNull(violation);
        Assert.StartsWith("HOST_INFRASTRUCTURE_PROTECTED", violation);
    }

    [Theory]
    [InlineData("docker ps -a")]
    [InlineData("docker info --format '{{.ServerVersion}}'")]
    [InlineData("docker logs omniproj-37bd0132 --tail 50")]
    [InlineData("wsl --status")]
    [InlineData("wsl -l -v")]
    [InlineData("Get-Process 'Docker Desktop','com.docker.backend' | Select Name,Id")]
    [InlineData("git pull && dotnet test")]
    public void ReadOnlyDiagnosticsAndOrdinaryWorkStayAllowed(string script)
    {
        Assert.Null(ProjectHostInfrastructurePolicy.FindViolation("run_powershell", Ps(script)));
        Assert.Null(ProjectHostInfrastructurePolicy.FindViolation("run_bash", Ps(script)));
    }

    [Fact]
    public void GuardrailCoversCSharpAndIgnoresTheAgentsOwnContainer()
    {
        string code = JsonConvert.SerializeObject(new { code = "System.Diagnostics.Process.Start(\"wsl\", \"--update\");" });
        Assert.NotNull(ProjectHostInfrastructurePolicy.FindViolation("execute_csharp", code));
        // Inside its own desktop container an agent may manage its own processes freely.
        string terminal = JsonConvert.SerializeObject(new { command = "pkill -f docker-helper; sudo apt-get install -y x" });
        Assert.Null(ProjectHostInfrastructurePolicy.FindViolation("computer_terminal", terminal));
    }

    [Fact]
    public void GuardrailRunsBeforeEveryCommanderAndSubAgentTool()
    {
        string source = File.ReadAllText(FindRepoFile("Omnipotent", "Services", "Projects", "Projects.cs"));
        int dispatch = source.IndexOf("public async Task<CommanderToolResult> CommanderToolDispatch(", StringComparison.Ordinal);
        int guard = source.IndexOf("ProjectHostInfrastructurePolicy.FindViolation(toolName, argsJson)", dispatch, StringComparison.Ordinal);
        int firstRoute = source.IndexOf("toolName.StartsWith(\"computer_\"", dispatch, StringComparison.Ordinal);
        Assert.True(dispatch > 0 && guard > dispatch && guard < firstRoute);
    }

    // ── Incus removal ──

    [Fact]
    public void DecommissionOnlyTouchesTheIncusInstallersOwnObjects()
    {
        string script = LegacyWorkerDecommission.Script;
        // Task first, so nothing restarts the VM mid-removal.
        Assert.True(script.IndexOf("Unregister-ScheduledTask", StringComparison.Ordinal)
                    < script.IndexOf("Remove-VM", StringComparison.Ordinal));
        Assert.Contains("StartsWith('Omnipotent:')", script);
        Assert.Contains(@"\\OmnipotentComputers\\[a-f0-9]{32}", script);
        Assert.DoesNotContain("Restart-Computer", script);
        Assert.DoesNotContain("wsl", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IncusProviderIsGoneAndDockerIsTheOnlyComputerPath()
    {
        string projects = Path.Combine(FindRepoFile("Omnipotent", "Services", "Projects"));
        Assert.False(Directory.Exists(Path.Combine(projects, "Computers")));
        Assert.False(File.Exists(Path.Combine(projects, "ProjectsComputers.cs")));
        string manager = File.ReadAllText(Path.Combine(projects, "Containers", "ContainerDesktopManager.cs"));
        Assert.DoesNotContain("PROJECTS_LEGACY_DOCKER_AUTOSTART", manager); // Docker recovery is on by default again
    }

    private static string FindRepoFile(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(string.Join('/', parts));
    }
}
