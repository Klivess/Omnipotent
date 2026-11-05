param(
    [string]$Server = "wss://klive.dev/klivelink/v2/ws",
    [string]$Token = "dev-token",
    [string]$AgentId = "",
    [string]$Output = "artifacts/klive-v2",
    [string]$AssemblyName = "WindowsCacheHost",
    [string]$SignCert = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [string]$PinSha256 = ""
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$project = Join-Path $repo "KliveLink\KliveLink.csproj"
$outPath = Join-Path $repo $Output

Write-Host "Publishing $AssemblyName to $outPath" -ForegroundColor Cyan
dotnet publish $project -c Release -r win-x64 --self-contained true `
    /p:PublishSingleFile=true /p:CopyOutputSymbolsToPublishDirectory=false `
    /p:KliveLinkAssemblyName=$AssemblyName /p:DebugType=None -o $outPath

$exe = Join-Path $outPath "$AssemblyName.exe"
if (-not (Test-Path $exe)) { throw "Published executable not found: $exe" }

$config = [ordered]@{
    server   = $Server
    token    = $Token
    agentId  = $AgentId
    pinSha256 = $PinSha256
}
$config | ConvertTo-Json | Set-Content (Join-Path $outPath "klive.config.json") -Encoding UTF8

$hash = Get-FileHash $exe -Algorithm SHA256
$hash | Select-Object Algorithm, Hash, Path | Format-List
$hash.Hash | Set-Content (Join-Path $outPath "payload.sha256") -Encoding ASCII

if ($SignCert) {
    $signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
    if (-not $signtool) { throw "signtool.exe not found; install the Windows SDK or omit -SignCert." }
    & $signtool sign /sha1 $SignCert /fd SHA256 /tr $TimestampUrl /td SHA256 $exe
    & $signtool verify /pa /all $exe
    if ($LASTEXITCODE -ne 0) { throw "Signature verification failed." }
}

Write-Host "Build complete: $exe" -ForegroundColor Green
Write-Host "Config: $outPath\klive.config.json"
Write-Host "Hash: $($hash.Hash)"
