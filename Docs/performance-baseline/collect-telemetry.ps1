param(
    [string]$BaseUrl = 'https://klive.dev',
    [string]$OutputDirectory = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($env:KLIVE_API_PASSWORD)) {
    throw 'Set KLIVE_API_PASSWORD in the process environment before collecting telemetry.'
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$headers = @{ Authorization = $env:KLIVE_API_PASSWORD }
$measurements = [System.Collections.Generic.List[object]]::new()

function Read-Telemetry([string]$Name, [string]$Path, [bool]$SaveBody = $false) {
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $response = Invoke-WebRequest -Uri ($BaseUrl.TrimEnd('/') + $Path) -Method Get -Headers $headers -TimeoutSec 20
        $stopwatch.Stop()
        $body = $response.Content
        if ($SaveBody) {
            [System.IO.File]::WriteAllText((Join-Path $OutputDirectory ($Name + '.json')), $body,
                [System.Text.UTF8Encoding]::new($false))
        }
        $measurements.Add([pscustomobject]@{
            route = $Path.Split('?')[0]
            query = if ($Path.StartsWith('/KliveAPI/telemetry/trace?')) { '[redacted]' } elseif ($Path.Contains('?')) { $Path.Substring($Path.IndexOf('?') + 1) } else { '' }
            status = [int]$response.StatusCode
            wallMs = [math]::Round($stopwatch.Elapsed.TotalMilliseconds, 1)
            bodyBytes = [System.Text.Encoding]::UTF8.GetByteCount($body)
            cache = $response.Headers['X-KliveAPI-Cache']
            serverTiming = (($response.Headers['Server-Timing'] -join ', ') -replace '(?:,\s*)?trace;desc=[^,]+', '')
        })
        return $body | ConvertFrom-Json
    }
    catch {
        $stopwatch.Stop()
        $measurements.Add([pscustomobject]@{
            route = $Path.Split('?')[0]
            query = if ($Path.StartsWith('/KliveAPI/telemetry/trace?')) { '[redacted]' } elseif ($Path.Contains('?')) { $Path.Substring($Path.IndexOf('?') + 1) } else { '' }
            status = 'error'
            wallMs = [math]::Round($stopwatch.Elapsed.TotalMilliseconds, 1)
            bodyBytes = 0
            cache = ''
            serverTiming = ''
        })
        throw
    }
}

# Aggregate payloads contain route metrics, not profile or trace identifiers.
$routes15m = Read-Telemetry 'routes-15m' '/KliveAPI/telemetry/routes?range=15m' $true
$routes1h = Read-Telemetry 'routes-1h' '/KliveAPI/telemetry/routes?range=1h' $true
$routes6h = Read-Telemetry 'routes-6h' '/KliveAPI/telemetry/routes?range=6h' $true
$routes24h = Read-Telemetry 'routes-24h' '/KliveAPI/telemetry/routes?range=24h' $true
$routes7d = Read-Telemetry 'routes-7d' '/KliveAPI/telemetry/routes?range=7d' $true
$routes30d = Read-Telemetry 'routes-30d' '/KliveAPI/telemetry/routes?range=30d' $true
$routes90d = Read-Telemetry 'routes-90d' '/KliveAPI/telemetry/routes?range=90d' $true
$routes1y = Read-Telemetry 'routes-1y' '/KliveAPI/telemetry/routes?range=1y' $true
$routesAll = Read-Telemetry 'routes-all' '/KliveAPI/telemetry/routes?range=all' $true
$overview24h = Read-Telemetry 'overview-24h' '/KliveAPI/telemetry/overview?range=24h' $false
$overview7d = Read-Telemetry 'overview-7d' '/KliveAPI/telemetry/overview?range=7d' $false
$overview30d = Read-Telemetry 'overview-30d' '/KliveAPI/telemetry/overview?range=30d' $false
$overview24h.PSObject.Properties.Remove('users')
$overview7d.PSObject.Properties.Remove('users')
$overview30d.PSObject.Properties.Remove('users')
$overview24h | ConvertTo-Json -Depth 100 -Compress | Set-Content -Path (Join-Path $OutputDirectory 'overview-24h.json') -Encoding utf8
$overview7d | ConvertTo-Json -Depth 100 -Compress | Set-Content -Path (Join-Path $OutputDirectory 'overview-7d.json') -Encoding utf8
$overview30d | ConvertTo-Json -Depth 100 -Compress | Set-Content -Path (Join-Path $OutputDirectory 'overview-30d.json') -Encoding utf8
$null = Read-Telemetry 'runtime-24h' '/KliveAPI/telemetry/runtime?range=24h' $true
$null = Read-Telemetry 'runtime-7d' '/KliveAPI/telemetry/runtime?range=7d' $true
$null = Read-Telemetry 'rum-24h' '/KliveAPI/telemetry/rum?range=24h' $true
$null = Read-Telemetry 'rum-7d' '/KliveAPI/telemetry/rum?range=7d' $true
$null = Read-Telemetry 'weekly' '/KliveAPI/telemetry/weekly' $true
$null = Read-Telemetry 'health' '/KliveAPI/telemetry/health' $true
$null = Read-Telemetry 'live' '/KliveAPI/telemetry/live' $false
$null = Read-Telemetry 'route-example' '/KliveAPI/telemetry/route?range=24h&route=%2Fomnidefence%2Foverview&method=GET' $false
$null = Read-Telemetry 'routes-filtered' '/KliveAPI/telemetry/routes?range=7d&method=GET' $false
$null = Read-Telemetry 'route-custom' '/KliveAPI/telemetry/route?range=7d&route=%2Fprojects%2Flist&method=GET' $false

# Trace bodies may contain personal identifiers. Record only HTTP timing/size.
$traces = Read-Telemetry 'traces' '/KliveAPI/telemetry/traces?range=24h&sort=slowest&limit=100' $false
if ($traces.traces.Count -gt 0 -and $traces.traces[0].id) {
    $traceId = [uri]::EscapeDataString([string]$traces.traces[0].id)
    $null = Read-Telemetry 'trace-detail' "/KliveAPI/telemetry/trace?id=$traceId" $false
}

$measurements | ConvertTo-Json -Depth 8 | Set-Content -Path (Join-Path $OutputDirectory 'endpoint-timings.json') -Encoding utf8
[pscustomobject]@{
    collectedUtc = (Get-Date).ToUniversalTime().ToString('O')
    routeSeries24h = $routes24h.routes.Count
    routeSeries7d = $routes7d.routes.Count
    routeSeries30d = $routes30d.routes.Count
    routeSeries90d = $routes90d.routes.Count
    routeSeries1y = $routes1y.routes.Count
    routeSeriesAll = $routesAll.routes.Count
    files = @(Get-ChildItem $OutputDirectory -Filter '*.json' | Select-Object -ExpandProperty Name)
} | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $OutputDirectory 'collection.json') -Encoding utf8
