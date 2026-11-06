# Omniscience v3 — progress log

The plan itself lives outside the repo (`C:\Users\klive\.claude\plans\indexed-baking-dahl.md`, approved 2026-11-06).
This file is the in-repo record of where the rewrite stands, so any session can resume.
Fix-by-fix status is in [traceability.md](traceability.md).

## Ground rules (from the plan)
- **Additive until cutover:**
  - v3 lives in `Omnipotent/Services/OmniscienceV3/` with service name `OmniscienceV3`, routes under `/omniscience/v3/*` and data in `SavedData/Omniscience/v3/`.
  - The legacy `Services/Omniscience/` keeps running untouched until cutover (plan R8).
- **Shadow mode:** v3 is registered only when `OmniscienceV3_ShadowEnabled=true`. It tails the legacy DB and opens no Discord gateways.
- **Architecture rules (R4):**
  - no `DateTime.Now/UtcNow/Today` in v3 code outside `Temporal/SystemClock`;
  - no empty `catch {}`;
  - no `async void` outside OmniService entry points;
  - no fire-and-forget `Task.Run`.

  An architecture test enforces these.
- **Data:** all instants are UTC epoch milliseconds (T1). Every derived row records its source watermark (R3).
- **Tests:** synthetic data only (both repos are public). Run them with:
  `$env:DOTNET_ROLL_FORWARD='Major'; dotnet test Omnipotent.Tests/Omnipotent.Tests.csproj --filter "FullyQualifiedName~OmniscienceV3"`

## Where to work
Work in the git worktree `C:\Projects\Software\Omnipotent\.claude\worktrees\omniscience-v3` (branch `omniscience-v3`).
Another live session commits and pushes on master from the main folder, so never check the branch out there.

## Phases
| Phase | Scope | Status |
|---|---|---|
| 0 | Branch, docs, contracts | done |
| 1 | Foundations: temporal ✓, storage ✓, kernel ✓, LLM gateway ✓, ML runtime, policy, API skeleton, legacy importer, synthetic corpus | in progress |
| 2 | Workstreams WS-A ingest/media/faces, WS-B knowledge/deduction/inquiry, WS-C classification/analytics/read models/signals, WS-D replica, WS-E UI | not started |
| 3 | Integration, shadow run, parity gates, cutover | not started |

## Log
- 2026-11-06:
  - Branch `omniscience-v3` created; traceability matrix generated (560 items).
  - f1ccdad: temporal library + storage + architecture rules.
  - 200655d: kernel — durable jobs, lane workers, module host, events, settings; the chaos test passes.
  - 183cf96: KliveLLM gateway changes:
    - `ModelContextRegistry` (dynamic windows);
    - `QueryAsync(LlmRequest)` (structured output, no "now" stamp, no lossy compaction);
    - the AIRouter `Background` work class.
  - Known pre-existing failure, not ours: `KliveLLM.BriefContinuityTests.MultiWakeReplay_MeasuresWeightedReuse…` fails on master too.

## Next
1. ML runtime spike: tokenizer support (SentencePiece/Unigram for XLM-R/e5), ONNX provider probe, ModelHub with real SHA pinning.
2. Policy (MinorGuard, sensitive traits, outbound confirmation) + Omniscience-side LLM gateway wrapper (llm_calls audit, ContextPacker, CacheShapedSession, per-class cache metering — F358).
3. API skeleton: typed routes, WebSocket live stream, chunked answer streaming, `[OmniTool]` surface, DTO → TS generation.
4. LegacyImporter (+ shadow tail mode), synthetic corpus generator, perf harness; OmniscienceV3 service shell (shadow-gated) registered in Program.cs.
5. Then freeze contracts and start Phase 2 workstreams.
