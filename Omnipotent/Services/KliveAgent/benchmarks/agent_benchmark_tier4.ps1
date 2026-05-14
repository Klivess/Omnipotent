param(
  [string]$BaseUrl = "https://klive.dev",
  [string]$Auth   = "incarnation",
  [string]$OutFile = "agent_test_results_tier4.txt",
  [int]$PollSeconds = 3,
  [int]$MaxWaitSeconds = 600
)

$ErrorActionPreference = "Stop"
$headers = @{ Authorization = $Auth; "Content-Type" = "application/json" }

$convId = [guid]::NewGuid().ToString("N")

# Tier-4: deeper reasoning, cross-tool dependencies, conditional branches,
# self-introspection, error recovery, hygiene/dedup with verification.
$questions = @(
  "Pipeline task: (1) Use GetRecentErrors(50) to find which service has the most errors today. (2) FindFiles for that services main *.cs file under Omnipotent/Services/. (3) ReadFile and count occurrences of catch as a whole word. Report all three numbers in one short paragraph. If there are zero errors, reply no errors today.",
  "Inspect GetRecentErrors(50). Pick ONE specific recurring error pattern (or say no recurring patterns). Propose ONE concrete code change to reduce that error class - state the file and approach in one sentence. SaveMemory tagged self-improvement with the proposal. Quote the saved id.",
  "Memory hygiene: RecallMemoriesByTag(klive-profile) and DeleteMemory ALL of them except the SINGLE most recent (highest CreatedAt). Then RecallMemoriesByTag(klive-profile) again and confirm exactly 1 remains. Report the kept id and how many you deleted.",
  "Use GetTypeSchema for KliveAgentMemory. List every method whose ReturnType contains Task<bool>. If none, say so. Otherwise list each by Name only.",
  "Read your own GetAgentStats. If today script failure rate is GREATER than 35 percent, send a Discord DM to Klives saying KliveAgent failure rate is X.X percent today, investigate (replace X.X with the real number). If 35 or lower, instead SaveMemory tagged rate-ok with content Today rate=X.X under threshold. Report exactly what you did.",
  "Read your own Omnipotent/Services/KliveAgent/KliveAgentBrain.cs. Find the LooksLikeUnexecutedScript method. In 3 SHORT bullets, explain the heuristics it uses to decide a final reply still contains an unexecuted script. Cite line numbers from the file.",
  "Try to call a method named TotallyFakeMethod on a service via CallObjectMethod or ExecuteServiceMethod. When it errors, recover gracefully and tell me: a) what error you got, b) what you tried as a fallback, c) the conclusion. Do NOT loop retrying the same fake call - one attempt then move on.",
  "RecallMemoriesByTag(self-improvement). Tell me how many self-improvement memories exist total (including the one you just made in Q2). Quote the most recent ones content verbatim.",
  "GetAgentStats. Compare today promptTokens to today completionTokens. Which is bigger and by what ratio (one decimal place)? Also report the today scriptSuccessRatePct from fullSummary.",
  "Self-evaluation: Across THIS Tier-4 conversation, which question consumed the MOST iterations or scripts? Use GetAgentStats fullSummary if it gives per-conversation breakdown; otherwise reason from your own conversational context. ONE paragraph."
)

if (Test-Path $OutFile) { Remove-Item $OutFile -Force }
"# Tier-4 Benchmark $(Get-Date -Format o)" | Out-File $OutFile
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
