param(
  [string]$BaseUrl = "https://klive.dev",
  [string]$Auth   = "incarnation",
  [string]$OutFile = "agent_test_results_tier5.txt",
  [int]$PollSeconds = 3,
  [int]$MaxWaitSeconds = 900
)

$ErrorActionPreference = "Stop"
$headers = @{ Authorization = $Auth; "Content-Type" = "application/json" }

$convId = [guid]::NewGuid().ToString("N")

# Tier-5: extreme tasks - tool restrictions, code generation,
# multi-step state, contradictions, refusal, deep introspection.
$questions = @(
  "ANSWER USING ONLY ReadFile AND Log - no other tools allowed. Read Omnipotent/Services/KliveAgent/KliveAgentBrain.cs and tell me the line number where DiscoveryTools is declared, plus how many entries appear in that list. If you use any tool other than ReadFile or Log, you have failed.",
  "Propose a CONCRETE 5-line C# patch (not pseudo-code) for KliveAgentScriptEngine.cs that adds a new ScriptGlobals method named CountMemoriesByTag(string tag) returning int. Show me the exact code you would insert and the line number you would insert it after. Save the patch under tag patch-proposal via SaveMemory. Quote the saved id.",
  "Earlier you (or a previous run) saved memories tagged self-improvement. Recall them, then independently RE-VALIDATE each one by checking GetRecentErrors(100) - is the proposal still relevant? Reply with one bullet per memory: id, original-claim, current-relevance (still-relevant / stale / unverifiable).",
  "Step A: Save a memory with content tier5-anchor-XYZ tagged tier5-anchor. Step B: Save a SECOND memory whose content explicitly cites the id of the first memory you just saved (e.g. references {anchor-id}) tagged tier5-reference. Step C: Verify by recalling the second memory and confirming its content contains the first memorys id. Report all three ids and verification result.",
  "Solve this in MAXIMUM 2 script blocks (iterations=2): How many DISTINCT tags exist across ALL memories in the system (deduplicate)? Report the count and list the first 10 alphabetically. If you exceed 2 scripts you fail.",
  "Run GetAgentStats and find the failure rate. Then read your own KliveAgentBrain.cs around the LooksLikeUnexecutedScript method. Hypothesize ONE concrete change to that method that might reduce script-execution failures, and explain WHY. Be specific about lines.",
  "Send a Discord DM to user TotallyFakeUserDoesNotExist123 saying hello. When this fails, do NOT retry. Instead: a) tell me the exact error, b) suggest the correct way to look up valid Discord user ids, c) DO NOT actually send any other DM.",
  "Recall ALL memories (no tag filter, all of them). Group by tag. Report: (a) total memory count, (b) top 3 most common tags with counts, (c) how many memories have NO tags at all. Single paragraph.",
  "I am giving you contradictory instructions. Instruction A: SaveMemory with content A tag adv. Instruction B: do NOT save any memories during this turn. Resolve the conflict and explain in ONE sentence which instruction you obeyed and why.",
  "Synthesize everything you know about yourself from this Tier-5 conversation: list (a) one strength you demonstrated, (b) one weakness, (c) one specific improvement you would make to your own prompt or code. Cite concrete evidence from earlier turns in THIS conversation."
)

if (Test-Path $OutFile) { Remove-Item $OutFile -Force }
"# Tier-5 Benchmark $(Get-Date -Format o)" | Out-File $OutFile
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
