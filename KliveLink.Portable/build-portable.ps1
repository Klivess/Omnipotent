param(
    [string]$Output = "artifacts/klivelink-portable",
    [string]$Version = "2.1.2"
)
$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$out = Join-Path $repo $Output
New-Item -ItemType Directory -Force $out | Out-Null
$project = Join-Path $repo "KliveLink.Portable\KliveLink.Portable.csproj"
$rids = @("linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64", "win-arm64")
$results = @()

foreach ($rid in $rids) {
    $ridDir = Join-Path $out $rid
    Remove-Item -Recurse -Force $ridDir -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $ridDir | Out-Null
    Write-Host "Publishing $rid..." -ForegroundColor Cyan
    dotnet publish $project -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=None -o $ridDir
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid" }

    $exeName = if ($rid.StartsWith("win-")) { "klivelink-agent.exe" } else { "klivelink-agent" }
    $binary = Join-Path $ridDir $exeName
    if (-not (Test-Path $binary)) { throw "Missing $binary" }

    $archiveName = "klivelink-agent-$rid-$Version"
    $archive = Join-Path $out ($archiveName + $(if ($rid.StartsWith("win-")) { ".zip" } else { ".tar.gz" }))
    Remove-Item -Force $archive -ErrorAction SilentlyContinue
    Push-Location $ridDir
    try {
        if ($rid.StartsWith("win-")) {
            Compress-Archive -Path $exeName -DestinationPath $archive -Force
        } else {
            tar -czf $archive $exeName
        }
    } finally { Pop-Location }

    $hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -Path ($archive + ".sha256") -Value "$hash  $(Split-Path $archive -Leaf)" -Encoding ASCII
    $results += [pscustomobject]@{
        rid = $rid
        archive = (Split-Path $archive -Leaf)
        bytes = (Get-Item $archive).Length
        sha256 = $hash
    }
}

$manifest = [ordered]@{
    version = $Version
    generatedUtc = (Get-Date).ToUniversalTime().ToString("O")
    assets = $results
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $out "manifest.json") -Encoding UTF8
$results | Format-Table -AutoSize
Write-Host "Manifest: $(Join-Path $out 'manifest.json')" -ForegroundColor Green


