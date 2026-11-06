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
| 1 | Foundations: temporal ✓, storage ✓, kernel ✓, LLM gateway ✓, ML runtime ✓, policy ✓, API skeleton ✓, legacy importer ✓, synthetic corpus + perf harness ✓ | done (contracts to freeze) |
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
  - d2f5250: ML runtime (ModelHub with SHA pinning/TOFU lock, ONNX host, WordPiece + SentencePiece-Unigram tokenizers, signal fusion).
  - 6911c13: policy (MinorGuard, sensitive traits, outbound confirmation tokens) + Omniscience LLM layer (audited calls, ContextPacker, CacheShapedSession, background gate).
  - 057843c: API skeleton and service shell:
    - `OmniscienceV3` registered in Program.cs with the typed KliveAPI; routes register first and answer an honest 503 (disabled / starting + stage / failed + reason) until the engine is ready; the engine starts only when `OmniscienceV3_ShadowEnabled=true`.
    - Engine routes under `/omniscience/v3/` (status, modules, jobs, LLM calls + per-class usage, events, settings) and the WebSocket live stream `/omniscience/v3/stream` (replay cursors, `stream.reset` on gaps, per-topic permissions, slow-consumer close, pings with the cursor).
    - New permission keys beside v2's; KliveAgent tools (`omniscience_v3` engine group); TypeScript contract generated to `Docs/omniscience-v3/api-contracts.ts`.
    - Single-instance lock on the data directory; startup requeues orphaned leases at once.
  - b93b746: LegacyImporter + `ingest.legacy` module (snapshot, then tail every 2 min; see the class docs for the mapping rules).
  - Synthetic corpus generator (`Storage/Synthetic/SyntheticCorpus.cs`) + perf harness (`CorpusPerfTests`): every core corpus read path is an index search (EXPLAIN QUERY PLAN asserted); the 5M-message run is opt-in with `OMNISCIENCE_PERF=1`.
  - **5M-message perf run (2026-11-06, dev machine): passed.** p95 over 200 random queries each, cold-ish cache:
    conversation timeline 22 ms, author window 46 ms, guild window 77 ms, global window 2.3 ms, replies 0.04 ms,
    change feed 5.2 ms, mentions 13 ms, reactions 39 ms, pending downloads 0.03 ms, name lookup 0.5 ms, FTS 0.05 ms
    (budget 150 ms; p50s all under 2 ms).
  - **Write throughput is the open risk:** generating those 5M messages took 4 h 54 min (~3.5 ms per message) because
    the seven message indexes plus FTS outgrow the writer's 64 MB page cache and inserts turn into random I/O. The v2
    snapshot import writes the same indexes, so a multi-million-message v2 corpus will take hours to migrate (resumable,
    in the background). Before the shadow run: raise the writer's cache (and `mmap_size`) during bulk loads, consider
    building secondary indexes after a bulk import, and measure the import on a v2-sized synthetic corpus.
  - X-engine and data serving (the rest of the Phase 1 kernel):
    - `Kernel/Tactics/`: `ITactic` SDK, typed selections + set algebra (people matched through merges, time ranges as interval sets, stratified sampling with disclosure), `TacticRunner` (runs are durable jobs with checkpointed stages; cancel, pause, resume, re-run; MinorGuard refusals with reasons; stale detection from the change log), playbooks (daily/weekly/interval in Klives's zone, event triggers from the durable stream, chained steps), built-in X26 cohort builder and X27 set algebra.
    - `ReadModels/ReadModelStore` (versioned rows, change-only bumps, `readmodel.updated` events, KliveAPI cache dependency keys).
    - Explore contract (`Kernel/Explore/`) with validation against declared datasets; the engine is WS-C's.
    - Routes: tactics, runs (incl. NDJSON run stream), selections, cohorts, playbooks, read models, Explore; KliveAgent tactics tools.
    - `Docs/omniscience-v3/workstreams.md`: the Phase 2 charter.

## Frozen contracts (R7) — Phase 2 builds on these; change only with a migration and a note here
- **Ids** (`Storage/CorpusIds.cs`): `platform:native` natural keys for identities, conversations, messages and guilds; attachments `messageId#idx`; persons opaque (merged, never renamed, resolved through `person_canonical`).
- **Stores**: corpus (v2 migration adds quarantine dedupe), intel, operator, vectors schemas as committed; modules own their intel tables through their own migration line.
- **Module contract**: `IIntelModule` / `ModuleManifest` (settings namespaced `<moduleId>.*`, validated at registration), change kinds in `ChangeKinds`, source kinds in `SourceKinds`.
- **API**: everything under `/omniscience/v3/`, typed registration through `RouteRegistrar`, `ApiResponses`/`ApiErrors`/`ApiQuery`/`ApiBody`, keyset paging (`cursor` → `nextCursor`), epoch-ms instants, NDJSON for streamed answers, contract types marked `[ApiContract]` (the generated TS file must stay current).
- **Live stream**: `EventHub` topics (`Topics`), frames `{id, at, topic, payload}`; topic prefixes map to permissions in `V3Routes.TopicFilterFor`.
- **Permissions**: v2 keys (`OmnisciencePerms`) + `OmniscienceV3Perms`.
- **Tactics**: `ITactic`/`TacticDescriptor`/`TacticInput`/`TacticOutput`, `Selection`/`SelectionKind`/`SetAlgebra`, `TacticRunContext.StageAsync`, playbook definitions and triggers; modules contribute tactics through `IIntelModule.Tactics`.
- **Read models**: `ReadModelDescriptor` in the manifest, `ReadModelStore.PutAsync/Get`, served at `GET /omniscience/v3/rm?model=&key=`.
- **Explore**: `ExploreQuery`/`ExploreResult`/`ExploreRecordsRequest`/`ExploreRecordsPage`, `IExploreEngine`, `ExploreValidation`.
- **Module services**: `ModuleContext` gives every module the stores, jobs, events, settings, read models, policy oracle, `OmniLlm` and `ModelHub`.

## Next
1. Phase 2 workstreams, each in its own session and worktree off the Phase 1 commit (plan R7 integration order A → C-base → B → D → E):
   - **WS-A** ingest & media: live Discord/KliveBot ingest reusing `DiscordRaw`, importers (N17), downloader, OCR, transcription, captions, faces (N1).
   - **WS-C** classification, analytics, read models, signals, Explore.
   - **WS-B** knowledge, deduction, inquiries and tactics.
   - **WS-D** replica.
   - **WS-E** the website UI on `api-contracts.ts`.
2. Phase 3: shadow run on live data, parity gates, Quality Lab gates, cutover.
