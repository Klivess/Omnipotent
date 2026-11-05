param(
    [string]$Output = "artifacts/klive-native",
    [string]$Name = "WindowsCacheHostNative"
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$native = Join-Path $repo "KliveLink.Native"
$outPath = Join-Path $repo $Output
New-Item -ItemType Directory -Force -Path $outPath | Out-Null

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath = if (Test-Path $vswhere) { & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath } else { "" }
if (-not $vsPath) { $vsPath = "D:\VSIDE\Product" }
$vcvars = Join-Path $vsPath "VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvars)) { throw "vcvars64.bat not found. Install the MSVC x64 build tools." }

$exe = Join-Path $outPath "$Name.exe"
$source = Join-Path $native "src\main.cpp"
$include = Join-Path $native "third_party"
$cmd = "`"$vcvars`" >nul && cl /nologo /std:c++20 /EHsc /O2 /MT /utf-8 /DUNICODE /D_UNICODE /I`"$include`" `"$source`" /Fo:`"$outPath\\`" /Fe:`"$exe`" /link winhttp.lib bcrypt.lib crypt32.lib shlwapi.lib advapi32.lib gdi32.lib user32.lib ole32.lib gdiplus.lib"
cmd.exe /d /s /c $cmd
if ($LASTEXITCODE -ne 0) { throw "Native build failed." }

$hash = Get-FileHash $exe -Algorithm SHA256
$hash.Hash | Set-Content (Join-Path $outPath "payload.sha256") -Encoding ASCII
Write-Host "Native build complete: $exe" -ForegroundColor Green
Write-Host "Hash: $($hash.Hash)"

