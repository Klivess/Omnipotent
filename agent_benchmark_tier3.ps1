param(
  [string]$BaseUrl = "https://klive.dev",
  [string]$Auth   = "incarnation",
  [string]$OutFile = "agent_test_results_tier3.txt",
  [int]$PollSeconds = 3,
  [int]$MaxWaitSeconds = 600
)

$ErrorActionPreference = "Stop"
$headers = @{ Authorization = $Auth; "Content-Type" = "application/json" }

$convId = [guid]::NewGuid().ToString("N")

# Tier-3: extremely complicated tasks. Multi-tool orchestration, conditional logic,
# cross-file synthesis, save-recall-delete round trips, schema introspection.
$questions = @(
  "Find the OmniService that has been up the LONGEST right now (highest uptime). Then SaveMemory with content '\$TYPE has been up for \$UPTIME as of \$NOW' (substituting actual values), tagged 'service-elder'. Confirm the saved id.",
  "Read Omnipotent/Services/KliveAgent/KliveAgentScriptEngine.cs and tell me how many PUBLIC instance methods exist on the ScriptGlobals class. Then list 3 of them by name. Use SearchCodeRegex with a method-signature regex.",
  "Recall every memory tagged 'preferences', synthesize them into ONE sentence describing me, and SaveMemory with that synthesis tagged 'klive-profile'. Quote the saved id.",
  "Use GetRecentErrors(50) and group the errors by source service. Tell me which service has the MOST errors today, or reply 'no errors today' if zero. Format as a markdown table if there are any.",
  "Round-trip test: SaveShortcut name='tier3-roundtrip' content='hello-world' (no tags arg needed). Then list all shortcuts and confirm it's there. Then DeleteMemory by its id. Then list shortcuts again and confirm it's gone. Report each step.",
  "Find every .cs file under Omnipotent/Services/ whose filename contains 'Routes'. Count them and list the first 5 alphabetically. Use SearchCode or SearchSymbols.",
  "Get your today stats via GetAgentStats(). Then RecallMemoriesByTag('klive-profile') and report whether your synthesis from Q3 actually persisted. Two sentences max.",
  "Find the BM25 score function in KliveAgentMemory.cs (we know it's around line 327). Read 30 lines starting at line 320 and explain in 2 short bullets what the average-doc-length normalisation does in practice when avgDocLen > docLen.",
  "Check if any service has fewer than 60 seconds of uptime right now. If yes, list their type names. If no, reply 'all warm' (no DM needed).",
  "Self-evaluation: in this Tier-3 conversation so far, which question forced you to run the MOST scripts? Recall via GetAgentStats and your own scriptsExecuted history if available; otherwise reason from your own context. One paragraph."
)

if (Test-Path $OutFile) { Remove-Item $OutFile -Force }
"# Tier-3 Benchmark $(Get-Date -Format o)" | Out-File $OutFile
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
