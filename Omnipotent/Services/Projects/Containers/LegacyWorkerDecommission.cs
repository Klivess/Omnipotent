using System.Runtime.Versioning;
using System.Text;

namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// Removes the Incus "persistent Linux worker" that the reverted Incus computer system
    /// provisioned on the host: a SYSTEM scheduled task that re-ran its installer every two minutes
    /// (and restarted the VM whenever it was off), a Hyper-V VM holding a fixed 6 GB of RAM while
    /// serving no project, its disks, its private switch/NAT and its state directory.
    ///
    /// Only objects carrying that installer's own ownership marks are touched: the reserved task
    /// name, the reserved VM/switch names with notes beginning "Omnipotent:", disks under
    /// X:\OmnipotentComputers\&lt;32-hex id&gt;\, and %ProgramData%\Omnipotent\AgentWorker. No project
    /// ever ran on it (cut-over required a verified migration that never happened). Idempotent;
    /// a marker file records completion so it runs once per host.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class LegacyWorkerDecommission
    {
        internal const string Script = """
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $done = New-Object System.Collections.Generic.List[string]
            $state = Join-Path $env:ProgramData 'Omnipotent\AgentWorker'
            $vmName = 'Omnipotent-Agent-Worker'
            $switchName = 'Omnipotent-Agent-Network'
            $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
            if (-not $admin) { 'RESULT=elevation_required'; exit 2 }

            # 1. The task first, so nothing restarts the VM while it is being removed.
            $task = Get-ScheduledTask -TaskName 'Omnipotent-Agent-Worker-Setup' -ErrorAction SilentlyContinue
            if ($task) {
                Stop-ScheduledTask -TaskName 'Omnipotent-Agent-Worker-Setup' -ErrorAction SilentlyContinue
                Unregister-ScheduledTask -TaskName 'Omnipotent-Agent-Worker-Setup' -Confirm:$false
                $done.Add('setup task removed')
            }
            Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
                Where-Object { $_.CommandLine -like '*Ensure-KAWorker.ps1*' } |
                ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue; $done.Add('running installer stopped') }

            $ownerDir = $null
            $ownerFile = Join-Path $state 'owner.json'
            if (Test-Path -LiteralPath $ownerFile) {
                $owner = Get-Content -LiteralPath $ownerFile -Raw | ConvertFrom-Json
                if ($owner.directory -match '^[A-Za-z]:\\OmnipotentComputers\\[a-f0-9]{32}$') { $ownerDir = $owner.directory }
            }

            # 2. The VM and its disks.
            if (Get-Module -ListAvailable Hyper-V) {
                Import-Module Hyper-V
                $vm = Get-VM -Name $vmName -ErrorAction SilentlyContinue
                if ($vm) {
                    if (-not ("$($vm.Notes)".StartsWith('Omnipotent:'))) { "RESULT=refused: VM '$vmName' is not marked as Omnipotent's"; exit 3 }
                    $disks = @(Get-VMHardDiskDrive -VM $vm | ForEach-Object { $_.Path })
                    if ($vm.State -ne 'Off') {
                        try { Stop-VM -VM $vm -Force -ErrorAction Stop } catch { Stop-VM -VM $vm -TurnOff -Force }
                    }
                    Remove-VM -VM $vm -Force
                    $done.Add('worker VM removed')
                    foreach ($disk in $disks) {
                        if ($disk -match '^[A-Za-z]:\\OmnipotentComputers\\[a-f0-9]{32}\\[^\\]+\.vhdx$' -and (Test-Path -LiteralPath $disk)) {
                            Remove-Item -LiteralPath $disk -Force
                            $done.Add("disk removed: $disk")
                        }
                    }
                }
                $switch = Get-VMSwitch -Name $switchName -ErrorAction SilentlyContinue
                if ($switch -and "$($switch.Notes)".StartsWith('Omnipotent:')) {
                    Remove-VMSwitch -Name $switchName -Force
                    $done.Add('private switch removed')
                }
            }
            if (Get-NetNat -Name $switchName -ErrorAction SilentlyContinue) {
                Remove-NetNat -Name $switchName -Confirm:$false
                $done.Add('NAT removed')
            }

            # 3. Its storage and state directories.
            if ($ownerDir -and (Test-Path -LiteralPath $ownerDir)) {
                Remove-Item -LiteralPath $ownerDir -Recurse -Force
                $done.Add("storage removed: $ownerDir")
                $parent = Split-Path -Parent $ownerDir
                if (-not (Get-ChildItem -LiteralPath $parent -Force -ErrorAction SilentlyContinue)) { Remove-Item -LiteralPath $parent -Force }
            }
            if (Test-Path -LiteralPath $state) {
                Remove-Item -LiteralPath $state -Recurse -Force
                $done.Add('worker state removed')
            }
            if ($done.Count -eq 0) { 'RESULT=nothing to remove' } else { 'RESULT=' + ($done -join '; ') }
            """;

        private static string MarkerPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Omnipotent", "DockerRecovery",
            "incus-worker-decommissioned.txt");

        /// <summary>Runs the removal once per host. Never throws; failures are logged and retried next start.</summary>
        public static async Task RunOnceAsync(Action<string> log, CancellationToken ct = default)
        {
            try
            {
                if (File.Exists(MarkerPath)) return;
                string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Script));
                var result = await ContainerDependencyBootstrapper.RunProcessAsync(
                    Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                    new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded },
                    TimeSpan.FromMinutes(10), ct);
                string output = result.Output.Trim();
                if (result.ExitCode == 0)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
                    File.WriteAllText(MarkerPath, $"{DateTime.UtcNow:o} {output}");
                    log($"Projects: Incus worker decommission complete — {output}");
                }
                else log($"Projects: Incus worker decommission did not complete (exit {result.ExitCode}) — {output}. It will retry on the next start.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { log($"Projects: Incus worker decommission failed ({ex.Message}); it will retry on the next start."); }
        }
    }
}
