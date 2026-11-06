param(
    [switch]$System,
    [switch]$Uninstall,
    [string]$Rid = ""
)
$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$BaseUrl = "__BASE_URL__"
$Version = "2.1.1"
$ReleaseTag = "klivelink-agents-2.1.1"
$GitHub = "https://github.com/Klivess/Omnipotent/releases/download/$ReleaseTag"

function Write-Log([string]$Message) {
    Write-Host $Message
    try {
        if ($script:InstallLog) {
            Add-Content -Path $script:InstallLog -Value ("{0} {1}" -f (Get-Date -Format o), $Message) -Encoding UTF8
        }
    } catch { }
}

function Fail([string]$Message) {
    Write-Log "ERROR: $Message"
    throw $Message
}

function Download([string]$Url, [string]$Path) {
    $last = $null
    for ($i = 0; $i -lt 5; $i++) {
        try {
            if (Test-Path $Path) { Remove-Item -Force $Path }
            Invoke-WebRequest -Uri $Url -OutFile $Path -UseBasicParsing -MaximumRedirection 10 -TimeoutSec 90
            if ((Get-Item $Path).Length -gt 0) { return $true }
        } catch {
            $last = $_.Exception.Message
            Start-Sleep -Seconds ([Math]::Min(10, 2 + $i * 2))
        }
    }
    Write-Log "Download failed: $Url ($last)"
    return $false
}

function DownloadText([string]$Url) {
    for ($i = 0; $i -lt 3; $i++) {
        try { return (Invoke-WebRequest -Uri $Url -UseBasicParsing -MaximumRedirection 10 -TimeoutSec 60).Content } catch { Start-Sleep -Seconds 2 }
    }
    return $null
}

if (-not $Rid) {
    $arch = $env:PROCESSOR_ARCHITECTURE
    if ($arch -eq "ARM64") { $Rid = "win-arm64" }
    elseif ($arch -eq "AMD64") { $Rid = "win-x64" }
    else { Fail "Unsupported Windows architecture '$arch' (x64 and ARM64 are supported)." }
}
if ($Rid -notin @("win-x64", "win-arm64")) { Fail "Unsupported RID '$Rid'." }

$IsAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($System -and -not $IsAdmin) {
    Write-Log "System install requested without elevation; falling back to a per-user install."
    $System = $false
}

$InstallDir = if ($System) { Join-Path $env:ProgramData "KliveLink" } else { Join-Path $env:LOCALAPPDATA "KliveLink" }
$ConfigDir = $InstallDir
$LogDir = $InstallDir
New-Item -ItemType Directory -Force -Path $InstallDir, $ConfigDir, $LogDir | Out-Null
$script:InstallLog = Join-Path $LogDir "install.log"
$Agent = Join-Path $InstallDir "klivelink-agent.exe"
$Config = Join-Path $ConfigDir "klive.config.json"
$AgentLog = Join-Path $LogDir "agent.log"
$RunKey = if ($System) { "HKLM:\Software\Microsoft\Windows\CurrentVersion\Run" } else { "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" }
$RunName = "KliveLinkAgent"

function Stop-Agent {
    Get-Process -Name "klivelink-agent" -ErrorAction SilentlyContinue | ForEach-Object {
        try { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    Start-Sleep -Seconds 1
}

if ($Uninstall) {
    Write-Log "Removing KliveLink from $InstallDir"
    Stop-Agent
    Remove-ItemProperty -Path $RunKey -Name $RunName -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $InstallDir -ErrorAction SilentlyContinue
    Write-Log "KliveLink removed."
    return
}

Write-Log "KliveLink Windows bootstrap: $Rid"

$Bootstrap = $null
try { $Bootstrap = DownloadText "$BaseUrl/klivelink/v2/bootstrap" | ConvertFrom-Json } catch { }
if (-not $Bootstrap) { Fail "Could not read $BaseUrl/klivelink/v2/bootstrap" }
if (-not $Bootstrap.server -or -not $Bootstrap.token) { Fail "Bootstrap response is missing server/token." }

$Asset = "klivelink-agent-$Rid-$Version.zip"
$Temp = Join-Path $env:TEMP ("klive_" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $Temp | Out-Null
try {
    $Archive = Join-Path $Temp $Asset
    Write-Log "Downloading $Asset ..."
    $ok = Download "$BaseUrl/klivelink/v2/agent?rid=$Rid" $Archive
    if (-not $ok) { $ok = Download "$GitHub/$Asset" $Archive }
    if (-not $ok) { Fail "Download failed from both the server mirror and GitHub." }

    $expected = DownloadText "$BaseUrl/klivelink/v2/agent/hash?rid=$Rid"
    if (-not $expected) { $expected = DownloadText "$GitHub/$Asset.sha256" }
    if ($expected) {
        $expectedHash = ($expected -split "\s+")[0].Trim().ToLowerInvariant()
        $actualHash = (Get-FileHash $Archive -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($expectedHash -ne $actualHash) { Fail "Checksum mismatch (expected $expectedHash, got $actualHash)." }
    } else {
        Write-Log "WARNING: checksum unavailable; verifying with --self-test."
    }

    Expand-Archive -Path $Archive -DestinationPath $Temp -Force
    $source = Join-Path $Temp "klivelink-agent.exe"
    if (-not (Test-Path $source)) { Fail "Archive did not contain klivelink-agent.exe" }
    Stop-Agent
    Copy-Item -Path $source -Destination $Agent -Force
    $autoTier = if ($Bootstrap.autoTier) { [string]$Bootstrap.autoTier } else { "T1" }
    $configObject = [ordered]@{
        server = [string]$Bootstrap.server
        token = [string]$Bootstrap.token
        agentId = ""
        autoTier = $autoTier
    }
    $configObject | ConvertTo-Json | Set-Content -Path $Config -Encoding UTF8

    $selfTest = & $Agent --self-test 2>&1
    if ($LASTEXITCODE -ne 0) { Fail "Agent self-test failed: $selfTest" }

    New-Item -Path $RunKey -Force | Out-Null
    New-ItemProperty -Path $RunKey -Name $RunName -Value ('"' + $Agent + '" --config "' + $Config + '" --installed') -PropertyType String -Force | Out-Null
    Start-Process -FilePath $Agent -ArgumentList @("--config", $Config, "--installed") -WorkingDirectory $InstallDir -WindowStyle Hidden

    Start-Sleep -Seconds 3
    if (-not (Get-Process -Name "klivelink-agent" -ErrorAction SilentlyContinue)) {
        $tail = if (Test-Path $AgentLog) { Get-Content $AgentLog -Tail 40 | Out-String } else { "no agent log" }
        Fail "Agent did not start. $tail"
    }
    Write-Log "KliveLink installed and running."
    Write-Log "Binary: $Agent"
    Write-Log "Config: $Config"
    Write-Log "Log:    $AgentLog"
}
finally {
    Remove-Item -Recurse -Force $Temp -ErrorAction SilentlyContinue
}


