param(
  [string]$BaseUrl = "https://klive.dev",
  [string]$Auth   = "incarnation",
  [string]$OutFile = "agent_test_results_tier6.txt",
  [int]$PollSeconds = 3,
  [int]$MaxWaitSeconds = 900
)

$ErrorActionPreference = "Stop"
$headers = @{ Authorization = $Auth; "Content-Type" = "application/json" }

$convId = [guid]::NewGuid().ToString("N")

# Tier-6: the stated end goal â€” many-step tasks where each step DEPENDS on the
# output of earlier steps, the agent must DISCOVER the right tool/data shape on
# its own, and the user does NOT spell out the recipe. Every Q is intentionally
# under-specified on the HOW. The agent has to plan, chain, and verify.
$questions = @(
  # Q1 â€” chained: pick a worst-day from history, then query that specific day's errors.
  "Use GetAgentStats. Look at fullSummary.dailyHistory. Find the day (NOT today) with the WORST script failure rate among days that had at least 5 scripts. Tell me the date, the rate, AND the absolute scriptFailures count. If no past day qualifies, say so explicitly.",

  # Q2 â€” chained: top-error-service -> its main file -> line numbers of all catch blocks -> save tagged memory.
  "Find the service that produced the most errors today via GetRecentErrors. Locate its main *.cs file. List the LINE NUMBERS of every `catch` block in that file. SaveMemory tagged crash-zones-{servicename} with content `lines={comma-separated-line-numbers}`. Quote the saved memory id and tag. If zero errors today, reply `no errors today` and save nothing.",

  # Q3 â€” many-step memory consolidation: tag with most memories -> summarize -> save -> delete originals -> verify.
  "Without me telling you HOW: figure out which TAG has the most memories attached to it. Recall every memory under that tag. Synthesize them into ONE consolidated summary memory tagged `{originalTag}-consolidated`. Then DELETE every original memory under the original tag (keep only the new consolidated one). Verify the original tag now has zero memories. Report: original tag, count consolidated, summary memory id, and the verification number.",

  # Q4 â€” discover services with a specific method shape -> aggregate their outputs.
  "Discover (do NOT ask me) which RUNNING services expose a public method whose name contains `Stats` and which takes zero parameters and returns something non-void. Call each one. Give me a markdown table with columns: ServiceName, MethodName, ResultPreview (first 120 chars of JSON). At LEAST 2 services or say `only N qualify`.",

  # Q5 â€” purely investigative without SearchCode hint: discover a code property by reflection.
  "Without using SearchCode, SearchCodeRegex, SearchCodeHybrid, ReadFile, or FindFiles: figure out which TYPES in the running Omnipotent process define a public property or field named `OmniSettings`. Use only GetTypeSchema, GetService, GetObjectMember, ListServices, and reflection-via-CallObjectMethod. List the type names. If zero, say `none`.",

  # Q6 â€” counterfactual reasoning: what WOULD have to change to flip a metric.
  "Look at today's GetAgentStats fullSummary.today. If today's scriptFailureRatePct is X, calculate exactly how many MORE successful scripts (zero new failures) would need to be run to drop the rate below 25 percent. Show: current scripts, current failures, current rate, and required-additional-successes. If already below 25, reply `already under 25` with the actual number.",

  # Q7 â€” cross-source correlation: error log timestamps vs message-rate.
  "From GetRecentErrors(50), find the SINGLE 60-second window today with the highest error count. Report the window start (HH:mm:ss UTC), the count, and which services contributed errors in that window (by name, deduped). If fewer than 2 errors total today, reply `not enough data`.",

  # Q8 â€” dynamic verification: agent must pick a tool, USE it, then VERIFY by another route.
  "Save a memory tagged `verification-test` with content `roundtrip-{timestamp}` where {timestamp} is the actual UTC ms-precision time you saved it. Verify the save worked using a DIFFERENT lookup path than the one you used to save it. Then DeleteMemory it and verify deletion via that same different path. Report: saved id, content, both verifications (success/failure), and final cleanup status.",

  # Q9 â€” schema drift detection: agent compares its prompt's tool list to actual schema.
  "Use GetTypeSchema on ScriptGlobals. Count how many public instance methods it has. Then count how many distinct tool NAMES are mentioned in your own current system prompt's [Common Patterns] section (you have to read your own prompt - figure out how). Report both numbers and any methods that exist but are NOT shown in the patterns.",

  # Q10 â€” full self-eval with grounded numbers from actual stats, not vibes.
  "Self-evaluation across THIS Tier-6 conversation. Use GetAgentStats fullSummary.today (BEFORE-AFTER subtraction is fine if you remember the start). Report: total scripts you ran answering Q1..Q9, your script success rate within this run, and ONE sentence on which Q was hardest based on iteration count. Do NOT confabulate; if you don't have the data, say so."
)

if (Test-Path $OutFile) { Remove-Item $OutFile -Force }
"# Tier-6 Benchmark $(Get-Date -Format o)" | Out-File $OutFile
"BaseUrl=$BaseUrl ConversationId=$convId" | Out-File $OutFile -Append
"" | Out-File $OutFile -Append

$totals = [ordered]@{ ptok = 0; ctok = 0; iters = 0; scripts = 0; ms = 0 }

for ($i=0; $i -lt $questions.Count; $i++) {
  $q = $questions[$i]
  $idx = $i + 1
  Write-Host "[$idx/$($questions.Count)] $q" -ForegroundColor Cyan

  $body = @{ message = $q; conversationId = $convId } | ConvertTo-Json -Compress
  $sw = [Diagnostics.Stopwatch]::StartNew()
  try {
    $resp = Invoke-RestMethod -Method Post -Uri "$BaseUrl/kliveagent/chat" -Headers $headers -Body $body -TimeoutSec 120
  } catch {
    Write-Host "  POST failed: $_" -ForegroundColor Red
    "## Q$idx : $q" | Out-File $OutFile -Append
    "POST FAILED: $_" | Out-File $OutFile -Append
    "" | Out-File $OutFile -Append
    continue
  }

  $final = $null

  if ($resp.isPending) {
    $rid = $resp.pendingRequestId
    if (-not $rid) { Write-Host "  No pendingRequestId returned. Skipping." -ForegroundColor Red; continue }
    $deadline = (Get-Date).AddSeconds($MaxWaitSeconds)
    while ((Get-Date) -lt $deadline) {
      Start-Sleep -Seconds $PollSeconds
      try {
        $pending = Invoke-RestMethod -Method Get -Uri "$BaseUrl/kliveagent/chat/pending?requestId=$rid" -Headers $headers -TimeoutSec 30
      } catch {
        if ($_.Exception.Response.StatusCode.value__ -eq 404) { continue } else {
          Write-Host "  POLL failed: $_" -ForegroundColor Red
          break
        }
      }
      $st = "$($pending.status)"
      if ($st -ne "Running" -and $st -ne "0") {
        $final = $pending.finalResponse
        if (-not $final) { $final = $pending }
        break
      }
    }
  } else {
    $final = $resp
  }
  $sw.Stop()

  if ($null -eq $final) {
    "## Q$idx : $q" | Out-File $OutFile -Append
    "TIMEOUT after $MaxWaitSeconds s" | Out-File $OutFile -Append
    "" | Out-File $OutFile -Append
    Write-Host "  TIMEOUT" -ForegroundColor Red
    continue
  }

  $ptok    = 0; if ($final.promptTokens)     { $ptok    = [int]$final.promptTokens }
  $ctok    = 0; if ($final.completionTokens) { $ctok    = [int]$final.completionTokens }
  $iters   = 0; if ($final.iterations)       { $iters   = [int]$final.iterations }
  $scripts = 0; if ($final.scriptsExecuted)  { $scripts = @($final.scriptsExecuted).Count }
  $reply   = $final.response

  $totals.ptok    += $ptok
  $totals.ctok    += $ctok
  $totals.iters   += $iters
  $totals.scripts += $scripts
  $totals.ms      += [int]$sw.ElapsedMilliseconds

  "## Q$idx : $q" | Out-File $OutFile -Append
  "ptok=$ptok ctok=$ctok iters=$iters scripts=$scripts ms=$($sw.ElapsedMilliseconds)" | Out-File $OutFile -Append
  "REPLY:" | Out-File $OutFile -Append
  $reply | Out-File $OutFile -Append
  "" | Out-File $OutFile -Append

  Write-Host "  ptok=$ptok ctok=$ctok iters=$iters scripts=$scripts ms=$($sw.ElapsedMilliseconds)"
}

"" | Out-File $OutFile -Append
"## TOTALS" | Out-File $OutFile -Append
$totals | ConvertTo-Json | Out-File $OutFile -Append

Write-Host ""
Write-Host "DONE. Totals:" -ForegroundColor Green
$totals
Write-Host ""
Write-Host "Wrote $OutFile"

