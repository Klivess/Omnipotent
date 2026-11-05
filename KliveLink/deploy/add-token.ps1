param(
    [Parameter(Mandatory = $true)][string]$Token,
    [string]$TokenFile = "SavedData\KliveLink\v2-tokens.json"
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$path = Join-Path $repo $TokenFile
$dir = Split-Path $path -Parent
New-Item -ItemType Directory -Force -Path $dir | Out-Null

$sha = [System.Security.Cryptography.SHA256]::Create()
$bytes = [Text.Encoding]::UTF8.GetBytes($Token)
$hash = -join ($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString("x2") })

$doc = if (Test-Path $path) { Get-Content $path -Raw | ConvertFrom-Json } else { [pscustomobject]@{ tokens = @() } }
$tokens = @($doc.tokens) + $hash
$tokens = $tokens | Select-Object -Unique
[pscustomobject]@{ tokens = $tokens } | ConvertTo-Json | Set-Content $path -Encoding UTF8
Write-Host "Enrollment token hash added to $path" -ForegroundColor Green
