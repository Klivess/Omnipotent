param(
    [Parameter(Mandatory = $true)][string]$Agent,
    [Parameter(Mandatory = $true)][string]$Config,
    [string]$Output = "artifacts/klive-setup",
    [string]$SignCert = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$outPath = Join-Path $repo $Output
New-Item -ItemType Directory -Force -Path $outPath | Out-Null

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath = if (Test-Path $vswhere) { & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath } else { "" }
if (-not $vsPath) { $vsPath = "D:\VSIDE\Product" }
$vcvars = Join-Path $vsPath "VC\Auxiliary\Build\vcvars32.bat"
if (-not (Test-Path $vcvars)) { throw "vcvars32.bat not found." }

$source = Join-Path $repo "KliveLink.Setup"
$setupExe = Join-Path $outPath "setup.exe"
$setupCmd = "`"$vcvars`" >nul && cl /nologo /std:c++20 /EHsc /O2 /MT /utf-8 /DUNICODE /D_UNICODE `"$source\Setup.cpp`" /Fe:`"$setupExe`" /link /SUBSYSTEM:WINDOWS /ENTRY:wWinMainCRTStartup shell32.lib advapi32.lib user32.lib shlwapi.lib && mt.exe -manifest `"$source\setup.manifest`" -outputresource:`"$setupExe`";#1"
cmd.exe /d /s /c $setupCmd
if ($LASTEXITCODE -ne 0) { throw "Setup compiler failed." }

if ($SignCert) {
    $signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
    if (-not $signtool) { throw "signtool.exe not found." }
    & $signtool sign /sha1 $SignCert /fd SHA256 /tr $TimestampUrl /td SHA256 $setupExe
    & $signtool verify /pa /all $setupExe
    if ($LASTEXITCODE -ne 0) { throw "Setup signature verification failed." }
}

$agentPath = (Resolve-Path $Agent).Path
$configPath = (Resolve-Path $Config).Path
$agentName = Split-Path $agentPath -Leaf
$configName = Split-Path $configPath -Leaf

$package = Join-Path $outPath "payload.7z"
$sevenZip = "C:\Program Files\7-Zip\7z.exe"
$sfx = "C:\Program Files\7-Zip\7z.sfx"
if (-not (Test-Path $sevenZip) -or -not (Test-Path $sfx)) { throw "7-Zip (7z.exe and 7z.sfx) is required." }

Push-Location $outPath
try {
    Copy-Item $agentPath (Join-Path $outPath $agentName) -Force
    Copy-Item $configPath (Join-Path $outPath $configName) -Force
    if (Test-Path $package) { Remove-Item $package -Force }
    & $sevenZip a -t7z -mx=9 $package "setup.exe" $agentName $configName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "7-Zip packaging failed." }
    $sfxConfig = Join-Path $outPath "sfx-config.txt"
    [System.IO.File]::WriteAllText($sfxConfig, ";!@Install@!UTF-8!`r`nGUIMode=`"2`"`r`nRunProgram=`"setup.exe`"`r`n;!@InstallEnd@!`r`n", [System.Text.UTF8Encoding]::new($false))
    $installer = Join-Path $outPath "WindowsCacheHostSetup.exe"
    if (Test-Path $installer) { Remove-Item $installer -Force }
    cmd.exe /d /s /c "copy /b `"$sfx`" + `"$sfxConfig`" + `"$package`" `"$installer`"" | Out-Null
    if (-not (Test-Path $installer)) { throw "SFX assembly failed." }
    Remove-Item $package -Force
    Remove-Item $sfxConfig -Force
    Write-Host "One-click installer: $installer" -ForegroundColor Green
    Get-FileHash $installer -Algorithm SHA256 | Select-Object Algorithm, Hash, Path | Format-List
    if ($SignCert) {
        $signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
        if (-not $signtool) { throw "signtool.exe not found." }
        & $signtool sign /sha1 $SignCert /fd SHA256 /tr $TimestampUrl /td SHA256 $installer
        & $signtool verify /pa /all $installer
        if ($LASTEXITCODE -ne 0) { throw "Installer signature verification failed." }
    }
}
finally { Pop-Location }

