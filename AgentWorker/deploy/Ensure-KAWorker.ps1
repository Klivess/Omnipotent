[CmdletBinding()]
param(
    [string]$StateRoot = "$env:ProgramData\Omnipotent\AgentWorker",
    [switch]$EnablePrerequisites,
    [switch]$InspectOnly
)
# Owns only its marked VM, switch and new VHDX files. Never resets a running VM,
# formats Windows disks, removes Docker data, or reboots Windows.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$utf8 = New-Object Text.UTF8Encoding($false)
$name = 'Omnipotent-Agent-Worker'
$switch = 'Omnipotent-Agent-Network'
$address = '10.78.0.2'
$root = Split-Path -Parent $PSScriptRoot
$stateFile = Join-Path $StateRoot 'setup.json'
function Write-Utf8($path, $text) { [IO.File]::WriteAllText($path, $text, $utf8) }
function Save-Json($path, $value) {
    Write-Utf8 "$path.tmp" ($value | ConvertTo-Json -Depth 20)
    # Windows PowerShell converts $null to an empty string for this overload,
    # which File.Replace rejects. Keep the previous generation as its backup.
    if (Test-Path -LiteralPath $path) { [IO.File]::Replace("$path.tmp", $path, "$path.previous") }
    else { [IO.File]::Move("$path.tmp", $path) }
}
function Status($state, $reason) {
    Save-Json $stateFile ([ordered]@{ state=$state; reason=$reason; updated=[DateTime]::UtcNow.ToString('o'); machine=$env:COMPUTERNAME })
}
function Native($exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "Executable failed: $([IO.Path]::GetFileName($exe)), exit $LASTEXITCODE" }
}
function Protect-Directory($directory) {
    $null = New-Item -ItemType Directory -Path $directory -Force
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $administrators = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $acl.SetOwner($administrators)
    foreach ($identity in @((New-Object Security.Principal.SecurityIdentifier('S-1-5-18')), $administrators)) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    if ($directory -eq $StateRoot) {
        foreach ($sid in (Read-Json (Join-Path $StateRoot 'readers.json'))) {
            $identity = New-Object Security.Principal.SecurityIdentifier($sid)
            $rule = New-Object Security.AccessControl.FileSystemAccessRule($identity, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
            $acl.AddAccessRule($rule)
        }
    }
    Set-Acl -LiteralPath $directory -AclObject $acl
}
function Read-Json($path) { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
function ConvertTo-PlainObject($value) {
    if ($null -eq $value) { return $null }
    if ($value -is [Collections.IDictionary]) {
        $result = New-Object 'System.Collections.Generic.Dictionary[string,object]'
        foreach ($key in $value.Keys) { $result.Add([string]$key,(ConvertTo-PlainObject $value[$key])) }
        return ,$result
    }
    if ($value -is [string] -or $value.GetType().IsPrimitive -or $value -is [decimal]) { return $value }
    if ($value -is [Collections.IEnumerable]) {
        $result = New-Object 'System.Collections.Generic.List[object]'
        foreach ($item in $value) { $result.Add((ConvertTo-PlainObject $item)) }
        return ,$result
    }
    # Never expose PowerShell's extended member metadata to a JSON serializer.
    return [string]$value
}
function Protect-SshKey($path) {
    # OpenSSH rejects a SYSTEM task's key if application-reader ACLs are inherited.
    $acl = New-Object Security.AccessControl.FileSecurity
    $acl.SetAccessRuleProtection($true, $false)
    $administrators = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $acl.SetOwner($administrators)
    foreach ($identity in @((New-Object Security.Principal.SecurityIdentifier('S-1-5-18')), $administrators)) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', 'Allow')))
    }
    Set-Acl -LiteralPath $path -AclObject $acl
}
function Set-NoCloudBoot($vhdPath) {
    # Canonical's Hyper-V-ready VHD is built for Azure and otherwise selects
    # DataSourceAzure before it can read our attached NoCloud seed. Patch only
    # the EFI loader on the newly-created, offline worker disk. The root UUID is
    # read from Canonical's own loader rather than assumed.
    $mounted = $null
    $efi = $null
    $accessPath = $null
    $addedAccessPath = $false
    try {
        $mounted = Mount-VHD -Path $vhdPath -Passthru | Get-Disk
        $efiType = '{C12A7328-F81F-11D2-BA4B-00A0C93EC93B}'
        $efi = Get-Partition -DiskNumber $mounted.Number | Where-Object { $_.GptType -eq $efiType } | Select-Object -First 1
        if (-not $efi) { throw 'The verified Ubuntu image has no EFI system partition.' }
        if ($efi.DriveLetter) { $accessPath = "$($efi.DriveLetter):\" }
        else {
            $letter = [char[]](90..68) | Where-Object { -not (Test-Path "$($_):\") } | Select-Object -First 1
            if (-not $letter) { throw 'No temporary drive letter is available for the worker EFI partition.' }
            $accessPath = "$letter`:\"
            Add-PartitionAccessPath -DiskNumber $mounted.Number -PartitionNumber $efi.PartitionNumber -AccessPath $accessPath
            $addedAccessPath = $true
        }
        $grub = Join-Path $accessPath 'EFI\ubuntu\grub.cfg'
        $original = Get-Content -LiteralPath $grub -Raw
        $match = [regex]::Match($original, 'search\.fs_uuid\s+([0-9a-fA-F-]+)\s+root')
        if (-not $match.Success) { throw 'The Ubuntu EFI loader does not identify its root filesystem.' }
        $uuid = $match.Groups[1].Value
        $loader = @"
serial --unit=0 --speed=115200 --word=8 --parity=no --stop=1
terminal_input serial console
terminal_output serial console
set timeout=3
set default=0
search.fs_uuid $uuid root
menuentry 'Omnipotent NoCloud bootstrap' {
    search.fs_uuid $uuid root
    for kernel in /vmlinuz-*; do set selected_kernel=`$kernel; done
    for ramdisk in /initrd.img-*; do set selected_initrd=`$ramdisk; done
    linux `$selected_kernel root=LABEL=cloudimg-rootfs ro console=ttyS0,115200n8 ds=nocloud ip=10.78.0.2::10.78.0.1:255.255.255.0:ka-worker:eth0:none
    initrd `$selected_initrd
}
"@
        Write-Utf8 $grub $loader
    }
    finally {
        if ($mounted) {
            if ($addedAccessPath) { Remove-PartitionAccessPath -DiskNumber $mounted.Number -PartitionNumber $efi.PartitionNumber -AccessPath $accessPath -ErrorAction SilentlyContinue }
            Dismount-VHD -Path $vhdPath -ErrorAction SilentlyContinue
        }
    }
}

$os = Get-CimInstance Win32_OperatingSystem
$cpu = @(Get-CimInstance Win32_Processor)
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$report = [ordered]@{ machine=$env:COMPUTERNAME; windows=$os.Caption; version=$os.Version; elevated=$admin; hyperV=[bool](Get-Module -ListAvailable Hyper-V); memoryGB=[math]::Round($os.TotalVisibleMemorySize/1MB,1) }
if ($InspectOnly) { $report | ConvertTo-Json; return }
$null = New-Item -ItemType Directory -Path $StateRoot -Force
$lock = $null
try { $lock = [IO.File]::Open((Join-Path $StateRoot 'setup.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
catch { return } # Another app/process is already reconciling this worker.
try {
    if (-not $admin) { Status 'elevation_required' 'Windows requires administrator permission to provision Hyper-V. Use Set up computer host once; no manual Linux setup is needed.'; return }
    $readers = Join-Path $StateRoot 'readers.json'
    if (-not (Test-Path -LiteralPath $readers)) {
        $creator = (Get-Acl -LiteralPath $StateRoot).GetOwner([Security.Principal.SecurityIdentifier]).Value
        Save-Json $readers @($creator,[Security.Principal.WindowsIdentity]::GetCurrent().User.Value)
    }
    Protect-Directory $StateRoot
    if (-not (Test-Path -LiteralPath (Join-Path $StateRoot 'managed-task.json'))) {
        # A protected SYSTEM task resumes after reboot or application exit. The
        # application user can read status/credentials, but cannot edit this task's code.
        $installer = Join-Path $StateRoot 'installer'
        Protect-Directory $installer
        $payloadRoot = Join-Path $installer 'AgentWorker'
        $null = New-Item -ItemType Directory -Path $payloadRoot -Force
        foreach ($part in @('ka_worker','deploy','tests')) { Copy-Item -LiteralPath (Join-Path $root $part) -Destination $payloadRoot -Recurse -Force }
        $helper = Join-Path $root 'browser-inspect.py'
        if (-not (Test-Path -LiteralPath $helper)) { $helper = Join-Path (Split-Path -Parent $root) 'Omnipotent\Services\Projects\Containers\browser-inspect.py' }
        Copy-Item -LiteralPath $helper -Destination (Join-Path $payloadRoot 'browser-inspect.py') -Force
        $taskScript = Join-Path $payloadRoot 'deploy\Ensure-KAWorker.ps1'
        $taskArgs = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$taskScript`" -StateRoot `"$StateRoot`""
        if ($EnablePrerequisites) { $taskArgs += ' -EnablePrerequisites' }
        $action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -Argument $taskArgs
        $triggers = @((New-ScheduledTaskTrigger -AtStartup),(New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(2) -RepetitionInterval (New-TimeSpan -Minutes 2)))
        $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
        $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 4) -StartWhenAvailable
        $existingTask = Get-ScheduledTask -TaskName 'Omnipotent-Agent-Worker-Setup' -ErrorAction SilentlyContinue
        if ($existingTask -and @($existingTask.Actions | Where-Object { $_.Arguments -ne $taskArgs }).Count) { throw 'An unrelated setup task uses the reserved name; refusing to replace it.' }
        $null = Register-ScheduledTask -TaskName 'Omnipotent-Agent-Worker-Setup' -Action $action -Trigger $triggers -Principal $principal -Settings $settings -Force
        Save-Json (Join-Path $StateRoot 'managed-task.json') @{name='Omnipotent-Agent-Worker-Setup';script=$taskScript}
    }
    $managed = Read-Json (Join-Path $StateRoot 'managed-task.json')
    $root = Split-Path -Parent (Split-Path -Parent $managed.script)
    if (-not [Environment]::Is64BitOperatingSystem -or $env:PROCESSOR_ARCHITECTURE -ne 'AMD64') { Status 'unsupported_host' 'The bundled worker image requires an x64 Windows host.'; return }
    if (@($cpu | Where-Object { -not $_.VirtualizationFirmwareEnabled -or -not $_.SecondLevelAddressTranslationExtensions }).Count -and -not (Get-CimInstance Win32_ComputerSystem).HypervisorPresent) {
        Status 'firmware_required' 'Enable CPU virtualization in firmware. Existing computer disks are preserved.'; return
    }
    $hyperVReady = [bool](Get-Module -ListAvailable Hyper-V) -and [bool](Get-CimInstance Win32_ComputerSystem).HypervisorPresent
    if (-not $hyperVReady) {
        # DISM feature inspection is extremely expensive on older Windows hosts.
        # Only invoke it when the installed module and running hypervisor do not
        # already prove that the prerequisite is ready.
        $feature = Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All -ErrorAction SilentlyContinue
        if (-not $feature) { Status 'unsupported_host' 'This Windows edition does not provide Hyper-V. A supported Pro, Enterprise or Server installation is required.'; return }
        if ($feature.State -ne 'Enabled') {
            if ($feature.State -eq 'EnablePending') { Status 'reboot_required' 'Hyper-V was enabled. Restart Windows in the maintenance window; setup resumes when Omnipotent starts.'; return }
            if (-not $EnablePrerequisites) { Status 'prerequisites_required' 'Hyper-V is disabled. Set up computer host enables it without restarting Windows automatically.'; return }
            Status 'enabling_hyperv' 'Enabling Hyper-V. Windows may require a reboot.'
            $result = Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All -All -NoRestart
            if ($result.RestartNeeded) { Status 'reboot_required' 'Restart Windows in the maintenance window. Omnipotent will resume setup afterward.'; return }
        }
    }
    if (-not (Get-Module -ListAvailable Hyper-V)) {
        if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') {
            Status 'reboot_required' 'Windows reports Hyper-V installed, but its management module is not available and servicing requires a reboot. Restart the host during maintenance; setup resumes automatically.'; return
        }
        throw 'Windows reports Hyper-V installed, but its PowerShell management module is missing. Windows feature installation needs repair.'
    }
    Import-Module Hyper-V
    if (-not (Get-CimInstance Win32_ComputerSystem).HypervisorPresent) { Status 'reboot_required' 'Hyper-V is installed but its hypervisor is not running. A Windows restart or firmware change is required.'; return }
    if (-not (Get-Command ssh.exe -ErrorAction SilentlyContinue)) {
        if (-not $EnablePrerequisites) { Status 'prerequisites_required' 'Windows OpenSSH Client is required. Set up computer host installs it automatically.'; return }
        $null = Add-WindowsCapability -Online -Name OpenSSH.Client~~~~0.0.1.0
    }
    $ssh = (Get-Command ssh.exe).Source
    $keygen = (Get-Command ssh-keygen.exe).Source
    $tar = (Get-Command tar.exe).Source
    $ownerFile = Join-Path $StateRoot 'owner.json'
    if (Test-Path -LiteralPath $ownerFile) { $owner = Read-Json $ownerFile }
    else {
        if (Get-VM -Name $name -ErrorAction SilentlyContinue) { throw 'A VM with the worker name exists without an ownership record. Refusing to adopt or replace it.' }
        # Choose an internal fixed volume, never a removable USB backup disk.
        $volumes = @(Get-Volume | Where-Object { $_.DriveLetter -and $_.DriveType -eq 'Fixed' -and $_.FileSystem -in @('NTFS','ReFS') -and $_.SizeRemaining -gt 100GB } | Sort-Object SizeRemaining -Descending)
        $volume = $volumes | Where-Object { (Get-Partition -DriveLetter $_.DriveLetter | Get-Disk).BusType -notin @('USB','SD','MMC') } | Select-Object -First 1
        if (-not $volume) { Status 'storage_required' 'No internal fixed volume has 100 GiB of free space for the worker. Free space or attach suitable internal storage; setup will retry.'; return }
        if ($report.memoryGB -lt 12) { Status 'capacity_required' 'This host needs at least 12 GiB RAM for the pilot worker plus Windows.'; return }
        $id = [guid]::NewGuid().ToString('N')
        $memory = if ($report.memoryGB -ge 60) { 40 } else { 4 }
        $cpuLimit = if ($memory -eq 40) { 12 } else { 3 }
        $owner = [pscustomobject]@{ id=$id; machine=$env:COMPUTERNAME; directory="$($volume.DriveLetter):\OmnipotentComputers\$id"; memoryGB=$memory; cpus=[math]::Max(1,[math]::Min($cpuLimit,($cpu | Measure-Object NumberOfLogicalProcessors -Sum).Sum-1)); dataBytes=256GB; dataLabel="KA-$($id.Substring(0,12))"; vmConfigured=$false; creatingVM=$false }
        Save-Json $ownerFile $owner
    }
    if ($owner.id -notmatch '^[a-f0-9]{32}$' -or $owner.directory -notmatch ('^[A-Za-z]:\\OmnipotentComputers\\'+$owner.id+'$') -or $owner.machine -ne $env:COMPUTERNAME) {
        throw 'Worker ownership manifest does not identify this host and its dedicated storage directory. Existing files are preserved.'
    }
    Protect-Directory $owner.directory
    $vm = Get-VM -Name $name -ErrorAction SilentlyContinue
    $osDisk = Join-Path $owner.directory 'ubuntu.vhdx'
    $dataDisk = Join-Path $owner.directory 'computers.vhdx'
    $mac = '00155D' + $owner.id.Substring(0,6).ToUpperInvariant()
    if ($vm) {
        $attached = @(Get-VMHardDiskDrive -VM $vm | ForEach-Object { $_.Path })
        if ($vm.Notes -ne "Omnipotent:$($owner.id)") {
            if ($owner.creatingVM -and -not $vm.Notes -and $vm.State -eq 'Off' -and $attached.Count -eq 1 -and $attached[0] -eq $osDisk) {
                Set-VM -VM $vm -Notes "Omnipotent:$($owner.id)"
            } else { throw 'Worker VM ownership mismatch; refusing changes.' }
        }
        if ($osDisk -notin $attached -or @($attached | Where-Object { $_ -notin @($osDisk,$dataDisk) }).Count -or ($owner.vmConfigured -and $dataDisk -notin $attached)) { throw 'Worker disks differ from the ownership manifest; refusing replacement.' }
    }
    else {
        Status 'network' 'Preparing the private worker network'
        $network = Get-VMSwitch -Name $switch -ErrorAction SilentlyContinue
        if (-not $network) {
            if (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -like '10.78.0.*' }) { throw 'Worker subnet 10.78.0.0/24 conflicts with an existing network.' }
            $network = New-VMSwitch -Name $switch -SwitchType Internal -Notes "Omnipotent:$($owner.id)"
        }
        if ($network.Notes -ne "Omnipotent:$($owner.id)" -or $network.SwitchType -ne 'Internal') { throw 'Private switch ownership mismatch.' }
        $adapter = Get-NetAdapter -Name "vEthernet ($switch)"
        if (-not (Get-NetIPAddress -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object IPAddress -eq '10.78.0.1')) {
            $null = New-NetIPAddress -InterfaceIndex $adapter.ifIndex -IPAddress 10.78.0.1 -PrefixLength 24
        }
        $nat = Get-NetNat -Name $switch -ErrorAction SilentlyContinue
        if (-not $nat) { $null = New-NetNat -Name $switch -InternalIPInterfaceAddressPrefix '10.78.0.0/24' }
        elseif ($nat.InternalIPInterfaceAddressPrefix -ne '10.78.0.0/24') { throw 'NAT configuration does not match the worker subnet.' }
        $archive = Join-Path $owner.directory 'ubuntu.tar.gz'
        $archiveMarker = Join-Path $owner.directory 'ubuntu.tar.gz.validated'
        $extractMarker = Join-Path $owner.directory 'image.validated'
        $digest = 'bcf5f2e60e55b3eb0eb57201fd57c0e34ced8b2dd1cdf4718084f41854786195'
        $uri = 'https://cloud-images.ubuntu.com/releases/noble/release-20260911/ubuntu-24.04-server-cloudimg-amd64-azure.vhd.tar.gz'
        if (-not (Test-Path -LiteralPath $osDisk)) {
            Status 'downloading' 'Downloading the pinned Ubuntu 24.04 worker image'
            $archiveStamp = if (Test-Path -LiteralPath $archive) { "$digest|$((Get-Item -LiteralPath $archive).Length)" } else { '' }
            $archiveTrusted = (Test-Path -LiteralPath $archiveMarker) -and ((Get-Content -LiteralPath $archiveMarker -Raw).Trim() -eq $archiveStamp)
            if ((Test-Path -LiteralPath $archive) -and -not $archiveTrusted) {
                if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $digest) {
                    Move-Item -LiteralPath $archive -Destination "$archive.invalid-$([guid]::NewGuid().ToString('N'))"
                    Remove-Item -LiteralPath $archiveMarker -Force -ErrorAction SilentlyContinue
                } else {
                    Write-Utf8 $archiveMarker "$archiveStamp`n"
                    $archiveTrusted = $true
                }
            }
            if (-not (Test-Path -LiteralPath $archive)) {
                [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
                $alreadyComplete = (Test-Path -LiteralPath "$archive.part") -and (Get-FileHash -LiteralPath "$archive.part" -Algorithm SHA256).Hash -eq $digest
                if (-not $alreadyComplete) {
                    if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
                        Native (Get-Command curl.exe).Source @('--fail','--location','--proto','=https','--connect-timeout','30','--max-time','1800','--retry','3','--continue-at','-','--output',"$archive.part",$uri)
                    } else { Invoke-WebRequest -UseBasicParsing -Uri $uri -OutFile "$archive.part" -TimeoutSec 1800 }
                }
                if ((Get-FileHash -LiteralPath "$archive.part" -Algorithm SHA256).Hash -ne $digest) {
                    Move-Item -LiteralPath "$archive.part" -Destination "$archive.invalid-$([guid]::NewGuid().ToString('N'))"
                    throw 'Ubuntu image checksum mismatch; download was quarantined and will be fetched again.'
                }
                Move-Item -LiteralPath "$archive.part" -Destination $archive
                $archiveStamp = "$digest|$((Get-Item -LiteralPath $archive).Length)"
                Write-Utf8 $archiveMarker "$archiveStamp`n"
                $archiveTrusted = $true
            }
            if (-not $archiveTrusted) { throw 'Cached Ubuntu image was not validated.' }
            Status 'extracting' 'Converting the verified Ubuntu disk to Hyper-V format'
            $extract = Join-Path $owner.directory 'image'
            $base = if (Test-Path -LiteralPath $extract) { @(Get-ChildItem -LiteralPath $extract -Filter '*.vhd' -Recurse) } else { @() }
            $extractTrusted = (Test-Path -LiteralPath $extractMarker) -and ((Get-Content -LiteralPath $extractMarker -Raw).Trim() -eq $archiveStamp) -and $base.Count -eq 1
            if (-not $extractTrusted) {
                if (Test-Path -LiteralPath $extract) { Remove-Item -LiteralPath $extract -Recurse -Force }
                $null = New-Item -ItemType Directory -Path $extract -Force
                Native $tar @('-xzf',$archive,'-C',$extract)
                $base = @(Get-ChildItem -LiteralPath $extract -Filter '*.vhd' -Recurse)
                if ($base.Count -ne 1) { throw 'Expected exactly one Ubuntu VHD in the verified archive.' }
                Write-Utf8 $extractMarker "$archiveStamp`n"
            }
            $base = @(Get-ChildItem -LiteralPath $extract -Filter '*.vhd' -Recurse)
            if ($base.Count -ne 1) { throw 'Expected exactly one Ubuntu VHD in the verified archive.' }
            # Canonical's Azure VHD is sparse, and Windows tar may also preserve
            # NTFS compression. Hyper-V refuses those attributes. Preallocate an
            # ordinary zero-filled file and write only source blocks containing
            # data. Every source byte is inspected, while sparse zero ranges stay
            # zero without hours of redundant HDD writes.
            if ($base[0].Attributes -band ([IO.FileAttributes]::Compressed -bor [IO.FileAttributes]::Encrypted -bor [IO.FileAttributes]::SparseFile)) {
                $materialized = Join-Path $owner.directory 'ubuntu-materializing.vhd'
                $materializedMarker = "$materialized.validated"
                $materializedStamp = "$archiveStamp|$($base[0].Length)"
                $ordinary = Get-Item -LiteralPath $materialized -ErrorAction SilentlyContinue
                $materializedTrusted = $ordinary -and $ordinary.Length -eq $base[0].Length -and -not ($ordinary.Attributes -band ([IO.FileAttributes]::Compressed -bor [IO.FileAttributes]::Encrypted -bor [IO.FileAttributes]::SparseFile)) -and (Test-Path -LiteralPath $materializedMarker) -and ((Get-Content -LiteralPath $materializedMarker -Raw).Trim() -eq $materializedStamp)
                if (-not $materializedTrusted) {
                    Remove-Item -LiteralPath $materialized -Force -ErrorAction SilentlyContinue
                    Remove-Item -LiteralPath $materializedMarker -Force -ErrorAction SilentlyContinue
                    if (-not ('KASparseFileMaterializer' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'SparseFileMaterializer.cs') }
                    [KASparseFileMaterializer]::Copy($base[0].FullName, $materialized)
                    $ordinary = Get-Item -LiteralPath $materialized
                    if ($ordinary.Length -ne $base[0].Length -or $ordinary.Attributes -band ([IO.FileAttributes]::Compressed -bor [IO.FileAttributes]::Encrypted -bor [IO.FileAttributes]::SparseFile)) {
                        throw 'The materialized Ubuntu VHD is incomplete or has attributes Hyper-V cannot use.'
                    }
                    Write-Utf8 $materializedMarker "$materializedStamp`n"
                }
                $base = @($ordinary)
            }
            $converted = Join-Path $owner.directory 'ubuntu-converting.vhdx'
            if (Test-Path -LiteralPath $converted) { Remove-Item -LiteralPath $converted } # Never attached; interrupted conversion only.
            Convert-VHD -Path $base[0].FullName -DestinationPath $converted -VHDType Dynamic
            Resize-VHD -Path $converted -SizeBytes 40GB
            Set-NoCloudBoot $converted
            Move-Item -LiteralPath $converted -Destination $osDisk
            Remove-Item -LiteralPath (Join-Path $owner.directory 'ubuntu-materializing.vhd') -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath (Join-Path $owner.directory 'ubuntu-materializing.vhd.validated') -Force -ErrorAction SilentlyContinue
        }
        if (-not (Test-Path -LiteralPath $dataDisk)) { $null = New-VHD -Path $dataDisk -Dynamic -SizeBytes $owner.dataBytes }
        Status 'configuring' 'Generating first-boot configuration and private credentials'
        $clientKey = Join-Path $StateRoot 'bootstrap-key'
        $hostKey = Join-Path $StateRoot 'host-key'
        foreach ($key in @($clientKey,$hostKey)) {
            if (-not (Test-Path -LiteralPath $key)) { Native $keygen @('-q','-t','ed25519','-N','""','-f',$key) }
            Protect-SshKey $key
            if (-not (Test-Path -LiteralPath "$key.pub")) {
                $public = & $keygen -y -f $key
                if ($LASTEXITCODE -ne 0) { throw 'Existing SSH identity could not be read; refusing replacement.' }
                Write-Utf8 "$key.pub" (($public -join "`n")+"`n")
            }
        }
        Write-Utf8 (Join-Path $StateRoot 'known_hosts') ("$address " + (Get-Content -LiteralPath "$hostKey.pub" -Raw).Trim() + "`n")
        $payload = Join-Path $StateRoot 'payload.tar.gz'
        # Release packaging supplies the worker, tests and browser helper together.
        # A completed archive is a safe checkpoint. Windows tar can finish the
        # file and then fail to return under heavy host pressure, so a retry
        # must reuse it when it is newer than every source file.
        $newestPayloadSource = (Get-ChildItem -LiteralPath $root -Recurse -File | Measure-Object LastWriteTimeUtc -Maximum).Maximum
        if (-not (Test-Path -LiteralPath $payload) -or (Get-Item -LiteralPath $payload).LastWriteTimeUtc -lt $newestPayloadSource) {
            $payloadPart = "$payload.part"
            $completedPart = (Test-Path -LiteralPath $payloadPart) -and (Get-Item -LiteralPath $payloadPart).Length -gt 0 -and (Get-Item -LiteralPath $payloadPart).LastWriteTimeUtc -ge $newestPayloadSource
            if (-not $completedPart) {
                Remove-Item -LiteralPath $payloadPart -Force -ErrorAction SilentlyContinue
                Native $tar @('-czf',$payloadPart,'-C',(Split-Path -Parent $root),(Split-Path -Leaf $root))
            }
            if (-not (Test-Path -LiteralPath $payloadPart) -or (Get-Item -LiteralPath $payloadPart).Length -eq 0) { throw 'Worker payload archive was not created.' }
            Move-Item -LiteralPath $payloadPart -Destination $payload -Force
        }
        $seed = Join-Path $StateRoot 'seed'
        $null = New-Item -ItemType Directory -Path $seed -Force
        $boot = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'first-boot.sh') -Raw
        $unit = "[Unit]`nAfter=network-online.target cloud-final.service`nWants=network-online.target`n[Service]`nType=oneshot`nExecStart=/bin/bash /opt/ka-bootstrap/first-boot.sh`nTimeoutStartSec=infinity`n"
        $timer = "[Unit]`nDescription=Resume Omnipotent worker setup`n[Timer]`nOnBootSec=30`nOnUnitInactiveSec=120`n[Install]`nWantedBy=timers.target`n"
        $config = @{
            hostname='ka-worker'; manage_etc_hosts=$true; ssh_pwauth=$false; disable_root=$true
            users=@(@{name='ka-bootstrap'; shell='/bin/bash'; sudo='ALL=(ALL) NOPASSWD:ALL'; lock_passwd=$true; ssh_authorized_keys=@((Get-Content -LiteralPath "$clientKey.pub" -Raw).Trim())})
            ssh_keys=@{ ed25519_private=(Get-Content -LiteralPath $hostKey -Raw); ed25519_public=(Get-Content -LiteralPath "$hostKey.pub" -Raw).Trim() }
            write_files=@(
                @{path='/opt/ka-bootstrap/payload.tar.gz'; permissions='0600'; encoding='b64'; content=[Convert]::ToBase64String([IO.File]::ReadAllBytes($payload))},
                @{path='/opt/ka-bootstrap/first-boot.sh'; permissions='0700'; content=$boot.Replace("`r`n","`n")},
                @{path='/etc/ka-bootstrap.json'; permissions='0600'; content=(@{dataLabel=$owner.dataLabel;dataBytes=$owner.dataBytes}|ConvertTo-Json -Compress)},
                @{path='/etc/systemd/system/ka-first-boot.service'; permissions='0644'; content=$unit},
                @{path='/etc/systemd/system/ka-first-boot.timer'; permissions='0644'; content=$timer},
                @{path='/etc/systemd/resolved.conf.d/ka-worker.conf'; permissions='0644'; content="[Resolve]`nDNS=1.1.1.1 8.8.8.8`nFallbackDNS=9.9.9.9`n"}
            )
            runcmd=@(@('systemctl','daemon-reload'),@('systemctl','enable','--now','ka-first-boot.timer'))
        }
        # Windows PowerShell 5.1's ConvertTo-Json can recursively expand
        # extended PSObject metadata here, consuming gigabytes without
        # producing output. JavaScriptSerializer treats these deliberately
        # simple dictionaries and arrays as bounded data.
        Add-Type -AssemblyName System.Web.Extensions
        $serializer = New-Object System.Web.Script.Serialization.JavaScriptSerializer
        $serializer.MaxJsonLength = 16MB
        Write-Utf8 (Join-Path $seed 'user-data') ("#cloud-config`n" + $serializer.Serialize((ConvertTo-PlainObject $config)))
        Write-Utf8 (Join-Path $seed 'meta-data') "instance-id: $($owner.id)`nlocal-hostname: ka-worker`n"
        $colonMac = ($mac -replace '(.{2})(?!$)','$1:').ToLowerInvariant()
        Write-Utf8 (Join-Path $seed 'network-config') "version: 2`nethernets:`n  worker:`n    match:`n      macaddress: '$colonMac'`n    set-name: eth0`n    dhcp4: false`n    addresses: [10.78.0.2/24]`n    routes:`n      - to: default`n        via: 10.78.0.1`n    nameservers:`n      addresses: [1.1.1.1, 8.8.8.8]`n"
        Add-Type -Path (Join-Path $PSScriptRoot 'SeedIso.cs')
        $iso = Join-Path $StateRoot 'seed.iso'
        if (Test-Path -LiteralPath $iso) { Remove-Item -LiteralPath $iso } # No VM exists yet.
        [KASeedIso]::Create($iso,$seed)
        if ((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory*1KB -lt ($owner.memoryGB*1GB+2GB)) { Status 'waiting_capacity' 'Waiting for enough free host memory to start the worker. Existing work is preserved.'; return }
        $owner.creatingVM = $true
        Save-Json $ownerFile $owner
        $vm = New-VM -Name $name -Generation 2 -MemoryStartupBytes ($owner.memoryGB*1GB) -VHDPath $osDisk -SwitchName $switch -Path $owner.directory
    }
    if (-not $owner.vmConfigured) {
        if ($vm.State -ne 'Off') { throw 'Worker started before its initial configuration completed; refusing to interrupt it.' }
        Set-VM -VM $vm -Notes "Omnipotent:$($owner.id)" -AutomaticStartAction Start -AutomaticStartDelay 30 -AutomaticStopAction ShutDown -AutomaticCheckpointsEnabled $false
        Set-VMProcessor -VM $vm -Count $owner.cpus
        Set-VMMemory -VM $vm -DynamicMemoryEnabled $false
        Set-VMNetworkAdapter -VMName $name -StaticMacAddress $mac
        Set-VMFirmware -VM $vm -EnableSecureBoot On -SecureBootTemplate MicrosoftUEFICertificateAuthority
        Set-VMComPort -VM $vm -Number 1 -Path "\\.\pipe\OmnipotentWorker"
        if (-not (Get-VMHardDiskDrive -VM $vm | Where-Object Path -eq $dataDisk)) { Add-VMHardDiskDrive -VM $vm -ControllerType SCSI -ControllerNumber 0 -ControllerLocation 1 -Path $dataDisk }
        $iso = Join-Path $StateRoot 'seed.iso'
        if (-not (Get-VMDvdDrive -VM $vm | Where-Object Path -eq $iso)) { Add-VMDvdDrive -VM $vm -Path $iso }
        $owner.vmConfigured = $true
        $owner.creatingVM = $false
        Save-Json $ownerFile $owner
    }
    if ($vm.State -eq 'Off') {
        if ((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory*1KB -lt ($owner.memoryGB*1GB+2GB)) { Status 'waiting_capacity' 'Waiting for host memory before starting the existing worker.'; return }
        Start-VM -VM $vm
    } elseif ($vm.State -ne 'Running') { Status 'waiting_worker' "Worker state is $($vm.State); preserving it without reset."; return }
    Status 'waiting_linux' 'Ubuntu is completing automatic setup. Applications and disks will not be reset on a slow response.'
    $sshArgs = @('-i',(Join-Path $StateRoot 'bootstrap-key'),'-o','BatchMode=yes','-o','ConnectTimeout=10','-o','ServerAliveInterval=10','-o','ServerAliveCountMax=2','-o','StrictHostKeyChecking=yes','-o',"UserKnownHostsFile=$(Join-Path $StateRoot 'known_hosts')","ka-bootstrap@$address")
    $free = (Get-Volume -FilePath $owner.directory).SizeRemaining
    $telemetry = @{freeBytes=$free;sampled=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds()} | ConvertTo-Json -Compress
    $telemetry | & $ssh @sshArgs 'sudo mkdir -p /var/lib/ka-bootstrap && sudo tee /var/lib/ka-bootstrap/host-pressure.json >/dev/null' 2>$null
    if ($LASTEXITCODE -ne 0) { return }
    $remoteStatus = & $ssh @sshArgs 'sudo cat /var/lib/ka-bootstrap/status.json' 2>$null
    if ($LASTEXITCODE -ne 0) {
        # cloud-init releases differ in how they materialize JSON-form runcmd.
        # Once pinned SSH proves the expected guest is present, enabling this
        # idempotent timer is safe and leaves the installer supervised by Linux.
        & $ssh @sshArgs 'sudo systemctl daemon-reload && sudo systemctl enable --now ka-first-boot.timer && sudo systemctl start --no-block ka-first-boot.service' 2>$null
        Status 'installing' 'Starting the supervised Linux worker installer'
        return
    }
    $progress = $remoteStatus | ConvertFrom-Json
    if ($progress.state -ne 'ready') { Status $progress.state $progress.reason; return }
    foreach ($file in @('ca.pem','ka-api.pem','ka-api-key.pem')) {
        $pem = & $ssh @sshArgs "sudo cat /etc/ka/$file" 2>$null
        if ($LASTEXITCODE -ne 0) { throw 'Unable to retrieve worker identity over pinned SSH connection.' }
        $destination = Join-Path $StateRoot $file
        Write-Utf8 "$destination.tmp" (($pem -join "`n")+"`n")
        if (Test-Path -LiteralPath $destination) { [IO.File]::Replace("$destination.tmp", $destination, "$destination.previous") }
        else { [IO.File]::Move("$destination.tmp", $destination) }
    }
    Save-Json (Join-Path $StateRoot 'client.json') @{endpoint="https://${address}:7443";ca=(Join-Path $StateRoot 'ca.pem');cert=(Join-Path $StateRoot 'ka-api.pem');key=(Join-Path $StateRoot 'ka-api-key.pem')}
    Status 'configured' 'Worker provisioned automatically. Omnipotent is verifying its authenticated connection.'
} catch {
    Status 'retrying' $_.Exception.Message
    exit 1
} finally { if ($lock) { $lock.Dispose() } }
