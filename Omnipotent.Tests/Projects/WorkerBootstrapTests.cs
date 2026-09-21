using Omnipotent.Services.Projects.Computers;

namespace Omnipotent.Tests.Projects;

public sealed class WorkerBootstrapTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ka-bootstrap-tests-" + Guid.NewGuid().ToString("N"));
    public WorkerBootstrapTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public async Task ApplicationRestartObservesIndependentInstallerWithoutReplacingItsState()
    {
        File.WriteAllText(Path.Combine(root, "managed-task.json"), "{\"name\":\"existing-setup\"}");
        const string state = "{\"state\":\"image\",\"reason\":\"Building desktop image\"}";
        File.WriteAllText(Path.Combine(root, "setup.json"), state);
        File.WriteAllText(Path.Combine(root, "owner.json"), "preserve VM identity");
        bool connected = false;
        var bootstrap = new WorkerBootstrapper(_ => connected = true, _ => { }, root, "missing-script");
        await bootstrap.ReconcileAsync();
        Assert.False(connected);
        Assert.Equal("image", bootstrap.Status().Value<string>("state"));
        Assert.Equal(state, File.ReadAllText(Path.Combine(root, "setup.json")));
        Assert.Equal("preserve VM identity", File.ReadAllText(Path.Combine(root, "owner.json")));
    }

    [Fact]
    public void StaleFeatureInstallationIsUnknownWithoutRewritingInstallerState()
    {
        string path = Path.Combine(root, "setup.json");
        string original = new Newtonsoft.Json.Linq.JObject
        {
            ["state"] = "enabling_hyperv", ["updated"] = DateTimeOffset.UtcNow.AddHours(-3).ToString("o")
        }.ToString();
        File.WriteAllText(path, original);
        var bootstrap = new WorkerBootstrapper(_ => { }, _ => { }, root);
        Assert.Equal("setup_progress_unknown", bootstrap.Status().Value<string>("state"));
        Assert.Equal("enabling_hyperv", bootstrap.Status().Value<string>("lastReportedState"));
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public async Task WindowsPowerShellCanReplaceExistingInstallerStateRepeatedly()
    {
        if (!OperatingSystem.IsWindows()) return;
        string installer = Path.Combine(AppContext.BaseDirectory, "AgentWorker", "deploy", "Ensure-KAWorker.ps1");
        string probe = Path.Combine(root, "probe.ps1");
        await File.WriteAllTextAsync(probe, """
            param($installer, $statePath)
            $ErrorActionPreference='Stop'
            $errors=$null; $tokens=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile($installer,[ref]$tokens,[ref]$errors)
            if($errors.Count){throw 'Installer syntax error'}
            $utf8=New-Object Text.UTF8Encoding($false)
            foreach($name in @('Write-Utf8','Save-Json')) {
                $node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
                Invoke-Expression $node.Extent.Text
            }
            foreach($n in 1..3){Save-Json $statePath @{generation=$n}}
            if((Get-Content $statePath -Raw|ConvertFrom-Json).generation -ne 3){throw 'Latest status lost'}
            if((Get-Content "$statePath.previous" -Raw|ConvertFrom-Json).generation -ne 2){throw 'Previous status lost'}
            """);
        var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", probe, installer, Path.Combine(root, "state.json") })
            info.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(info)!;
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(process.ExitCode == 0, await errors);
    }

    [Fact]
    public void SparseUbuntuImageIsMaterializedBeforeHyperVConversion()
    {
        string installer = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "AgentWorker", "deploy", "Ensure-KAWorker.ps1"));
        Assert.Contains("ubuntu-materializing.vhd", installer);
        Assert.Contains("[KASparseFileMaterializer]::Copy", installer);
        string helper = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "AgentWorker", "deploy", "SparseFileMaterializer.cs"));
        Assert.Contains("output.SetLength(input.Length)", helper);
        Assert.Contains("DeviceIoControl", helper);
        Assert.Contains("foreach (var range in Ranges(input))", helper);
        Assert.DoesNotContain("@('sparse','setflag',$base[0].FullName,'0')", installer);
        Assert.True(installer.IndexOf("$base = @($ordinary)", StringComparison.Ordinal)
            < installer.IndexOf("Convert-VHD -Path $base[0].FullName", StringComparison.Ordinal));
    }

    [Fact]
    public void AzureImageIsConvertedIntoAnAutonomousNoCloudWorker()
    {
        string installer = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "AgentWorker", "deploy", "Ensure-KAWorker.ps1"));
        Assert.Contains("Set-NoCloudBoot $osDisk", installer);
        Assert.Contains("ds=nocloud ip=10.78.0.2::10.78.0.1", installer);
        Assert.Contains("/etc/systemd/resolved.conf.d/ka-worker.conf", installer);
        Assert.Contains("sudo systemctl enable --now ka-first-boot.timer", installer);

        string firstBoot = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "AgentWorker", "deploy", "first-boot.sh"));
        Assert.DoesNotContain("wipefs','--no-act','--noheadings", firstBoot);
        Assert.DoesNotContain("image info ka-base-debian12 --format", firstBoot);
        Assert.Contains("incus image list --format json", firstBoot);
    }

    [Fact]
    public async Task MaterializerPreservesDataAndProducesOrdinaryFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        string helper = Path.Combine(AppContext.BaseDirectory, "AgentWorker", "deploy", "SparseFileMaterializer.cs");
        string probe = Path.Combine(root, "materializer.ps1");
        string source = Path.Combine(root, "sparse-source.bin");
        string destination = Path.Combine(root, "ordinary-target.bin");
        await File.WriteAllTextAsync(probe, """
            param($helper,$source,$destination)
            $ErrorActionPreference='Stop'
            $f=[IO.File]::Create($source);$f.SetLength(64MB)
            $f.Position=7MB;$f.WriteByte(123);$f.Position=40MB;$f.WriteByte(45);$f.Dispose()
            Add-Type -Path $helper
            [KASparseFileMaterializer]::Copy($source,$destination)
            $sha=[Security.Cryptography.SHA256]::Create()
            $a=[Convert]::ToBase64String($sha.ComputeHash([IO.File]::OpenRead($source)))
            $b=[Convert]::ToBase64String($sha.ComputeHash([IO.File]::OpenRead($destination)))
            $sha.Dispose();if($a-ne$b){throw 'content mismatch'}
            $bad=[IO.FileAttributes]::Compressed-bor[IO.FileAttributes]::Encrypted-bor[IO.FileAttributes]::SparseFile
            if((Get-Item $destination).Attributes-band$bad){throw 'target attributes rejected by Hyper-V'}
            """);
        var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", probe, helper, source, destination })
            info.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(info)!;
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(process.ExitCode == 0, await errors);
    }

    [Fact]
    public void CorruptStatusIsReportedWithoutDeletingRecoveryEvidence()
    {
        string path = Path.Combine(root, "setup.json");
        File.WriteAllText(path, "incomplete previous write");
        var bootstrap = new WorkerBootstrapper(_ => { }, _ => { }, root);
        Assert.Equal("status_unavailable", bootstrap.Status().Value<string>("state"));
        Assert.Equal("incomplete previous write", File.ReadAllText(path));
    }

    [Fact]
    public void ReleaseIncludesTheEntireSelfSetupPayload()
    {
        foreach (string file in new[] { "deploy/Ensure-KAWorker.ps1", "deploy/SeedIso.cs", "deploy/SparseFileMaterializer.cs", "deploy/first-boot.sh",
            "deploy/install-worker.sh", "deploy/build-computer.sh", "deploy/create-certificates.sh", "deploy/broker.example.json",
            "ka_worker/__main__.py", "ka_worker/broker.py", "ka_worker/session.py", "tests/test_reliability.py", "browser-inspect.py" })
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "AgentWorker", file)), "Missing portable payload: " + file);
    }

    [Fact]
    public void MovingTheApplicationCannotSilentlyReplaceExistingComputers()
    {
        Assert.Throws<InvalidOperationException>(() => Omnipotent.Services.Projects.Projects.ValidateWorkerIdentity("original-worker", "new-worker"));
        Assert.Throws<InvalidOperationException>(() => Omnipotent.Services.Projects.Projects.ValidateWorkerIdentity(null, "new-worker"));
        Omnipotent.Services.Projects.Projects.ValidateWorkerIdentity("restored-worker", "restored-worker");
    }
}
