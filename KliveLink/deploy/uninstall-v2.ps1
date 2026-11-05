param(
    [string]$InstallDir = "$env:LOCALAPPDATA\Microsoft\Windows\Caches\kl",
    [string]$RunValue = "WindowsCacheHost",
    [string]$ProcessName = "WindowsCacheHost"
)

$ErrorActionPreference = "SilentlyContinue"
Get-Process -Name $ProcessName | Stop-Process -Force
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name $RunValue
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "KL"
if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
$legacy = Join-Path $env:APPDATA "SysMon"
if (Test-Path $legacy) { Remove-Item $legacy -Recurse -Force }
Write-Host "Removed $ProcessName, Run values '$RunValue' and 'KL', and install directories." -ForegroundColor Green
