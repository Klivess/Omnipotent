[CmdletBinding()]
param(
    [ValidateSet('Inspect','Create')][string]$Mode = 'Inspect',
    [string]$Name = 'KA-Agent-Worker',
    [string]$BaseVhdPath,
    [string]$BaseVhdSha256,
    [string]$StorageDirectory,
    [string]$SwitchName,
    [int]$MemoryGB = 6,
    [int]$CpuCount = 3,
    [int]$DataDiskGB = 256
)
$ErrorActionPreference = 'Stop'
$os = Get-CimInstance Win32_OperatingSystem
$cpu = Get-CimInstance Win32_Processor
$hyperV = [bool](Get-Module -ListAvailable Hyper-V)
$report = [ordered]@{
    ComputerName = $env:COMPUTERNAME
    Windows = $os.Caption
    Version = $os.Version
    TotalMemoryGB = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)
    FreeMemoryGB = [math]::Round($os.FreePhysicalMemory / 1MB, 1)
    HyperVManagementAvailable = $hyperV
    VirtualizationFirmwareEnabled = @($cpu.VirtualizationFirmwareEnabled)
    SlatAvailable = @($cpu.SecondLevelAddressTranslationExtensions)
    ExistingWorker = if ($hyperV) { [bool](Get-VM -Name $Name -ErrorAction SilentlyContinue) } else { $false }
}
if ($Mode -eq 'Inspect') { $report | ConvertTo-Json; return }
if (-not $hyperV) { throw 'Hyper-V must be enabled during the scheduled host maintenance window. No changes made.' }
if ($report.ExistingWorker) { throw 'Worker already exists. This command never replaces an existing VM or its disks.' }
if (-not $BaseVhdPath -or -not $StorageDirectory -or -not $SwitchName -or -not $BaseVhdSha256) {
    throw 'Create requires a prepared Ubuntu 24.04 VHDX, its SHA-256, storage directory and an existing Hyper-V switch.'
}
if ($MemoryGB -lt 4 -or $MemoryGB -gt ($report.TotalMemoryGB - 6)) { throw 'Keep at least 6 GB for Windows; use a suitable pilot or production allocation.' }
if ($report.FreeMemoryGB -lt ($MemoryGB + 2)) { throw 'Insufficient current memory headroom to start the worker safely.' }
$base = (Resolve-Path -LiteralPath $BaseVhdPath).Path
if ((Get-FileHash -LiteralPath $base -Algorithm SHA256).Hash -ne $BaseVhdSha256) { throw 'Base VHDX checksum mismatch.' }
$storage = [IO.Path]::GetFullPath($StorageDirectory)
if (-not [IO.Path]::IsPathRooted($storage)) { throw 'Storage must be an absolute local path.' }
$drive = Get-Volume -FilePath $storage
if ($drive.DriveType -ne 'Fixed' -or $drive.SizeRemaining -lt 80GB) { throw 'Use reliably attached fixed storage with at least 80 GB free.' }
$null = Get-VMSwitch -Name $SwitchName
$vmDirectory = Join-Path $storage $Name
if (Test-Path -LiteralPath $vmDirectory) { throw 'Target directory already exists; refusing to overwrite it.' }
$null = New-Item -ItemType Directory -Path $vmDirectory
$osDisk = Join-Path $vmDirectory 'ubuntu.vhdx'
$dataDisk = Join-Path $vmDirectory 'computers.vhdx'
Copy-Item -LiteralPath $base -Destination $osDisk
$null = New-VHD -Path $dataDisk -Dynamic -SizeBytes ($DataDiskGB * 1GB)
$null = New-VM -Name $Name -Generation 2 -MemoryStartupBytes ($MemoryGB * 1GB) -VHDPath $osDisk -SwitchName $SwitchName -Path $vmDirectory
Set-VMMemory -VMName $Name -DynamicMemoryEnabled $false
Set-VMProcessor -VMName $Name -Count $CpuCount
Set-VMFirmware -VMName $Name -EnableSecureBoot On -SecureBootTemplate MicrosoftUEFICertificateAuthority
Add-VMHardDiskDrive -VMName $Name -Path $dataDisk
Set-VM -Name $Name -AutomaticStartAction Start -AutomaticStartDelay 30 -AutomaticStopAction ShutDown
Start-VM -Name $Name
Get-VM -Name $Name | Select-Object Name, State, MemoryAssigned, ProcessorCount
