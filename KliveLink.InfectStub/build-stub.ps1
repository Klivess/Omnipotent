param(
    [string]$Output = "artifacts/infect-stub"
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$outPath = Join-Path $repo $Output
New-Item -ItemType Directory -Force -Path $outPath | Out-Null

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath = if (Test-Path $vswhere) { & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath } else { "" }
if (-not $vsPath) { $vsPath = "D:\VSIDE\Product" }
$vcvars = Join-Path $vsPath "VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvars)) { throw "vcvars64.bat not found." }

$source = Join-Path $repo "KliveLink.InfectStub\Stub.cpp"
$exe = Join-Path $outPath "InfectStub.exe"
$cmd = "`"$vcvars`" >nul && cl /nologo /std:c++20 /EHsc /O2 /MT /utf-8 /DUNICODE /D_UNICODE `"$source`" /Fe:`"$exe`" /link /SUBSYSTEM:WINDOWS /ENTRY:wWinMainCRTStartup shell32.lib advapi32.lib user32.lib shlwapi.lib"
cmd.exe /d /s /c $cmd
if ($LASTEXITCODE -ne 0) { throw "Infect stub build failed." }
Write-Host "Infect stub: $exe" -ForegroundColor Green
Get-FileHash $exe -Algorithm SHA256 | Select-Object Algorithm, Hash, Path | Format-List
