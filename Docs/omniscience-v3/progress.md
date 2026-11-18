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
| 2 | Workstreams WS-A ingest/media/faces, WS-B knowledge/deduction/inquiry, WS-C classification/analytics/read models/signals, WS-D replica, WS-E UI | built on every branch (2026-11-12) and merged into `omniscience-v3` (2026-11-13). Captions and visual search (WS-A) and the website pages for the WS-A/B/D routes followed on 2026-11-13/14. Not built: faces (WS-A: the face stack and its model files are still to be chosen) |
| 3 | Integration, shadow run, parity gates, cutover | integration done on `omniscience-v3` ("Next" items 2 and 3): merged A → C → B → D, the open wiring done, X17, X48 and X50 built, KliveRAG fed by v3 behind a switch. Shadow run, parity gates and cutover not started |

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

   WS-A to WS-D were started on 2026-11-06, each in its own session and worktree, all cut from 3dc87df:

   | Workstream | Branch | Report |
   |---|---|---|
   | WS-A | `claude/omniscience-v3-ws-a-878bce` | `Docs/omniscience-v3/ws-a.md` |
   | WS-B | `claude/omniscience-v3-ws-b-49da68` | `Docs/omniscience-v3/ws-b.md` |
   | WS-C | `claude/omniscience-v3-ws-c-99fa83` | `Docs/omniscience-v3/ws-c.md` |
   | WS-D | `claude/omniscience-v3-ws-d-21c7c3` | `Docs/omniscience-v3/ws-d.md` |

   Each worktree is `.claude/worktrees/<branch name without the claude/ prefix>`.

   Workstreams commit only on their own branch. The coordinating session merges them into `omniscience-v3` in the R7 order, and only when Klives says to.

   **WS-E** (website repository, branch `omniscience-v3`, worktree
   `C:\Projects\Websites\klives-management-website\.claude\worktrees\omniscience-v3`) is done by the coordinating session.
   Its first slice landed on 2026-11-09: the data client, time lens, live stream, shell and the ten pages the frozen
   contract can drive (overview, modules, jobs, LLM, settings, tactics, playbooks, cohorts, Explore, live), with e2e.
   Report and the list of contract gaps it found for the other workstreams: [ws-e.md](ws-e.md). The domain pages
   (people, conversations, search, faces, replicas, radar, digests, review, graph, sources) start when their DTOs exist.

   **WS-A** has been done by the coordinating session since 2026-11-10, on Klives's instruction ("do WS-A by yourself"), in
   WS-A's own worktree and branch (its original session had produced no commits and is idle; do not wake it). Landed so far,
   tested (239 v3 tests passing on that branch, tip `211e9a1`): the corpus writer every source goes through, the
   content-addressed media store, five import formats (WhatsApp, Telegram, Instagram/Messenger, the Discord data package,
   CSV/JSON tables) with preview and idempotent commit as kernel jobs, image probing (orientation, taken-time, perceptual
   hash, thumbnails), the attachment downloader, OCR and transcripts filed as labels (tested with stand-in engines; the real
   Tesseract, ffmpeg and Whisper paths have not run), and routes for imports and media. Since 2026-11-10 also: **Discord
   through KliveBot** (module `ingest.discord_bot`, off by default until cutover) — it listens to the connection the KliveBot
   service already holds, reads each channel forward and backward through the bot API with a cursor per channel, and
   records deletions, reactions, presence, voice and membership; an export **upload route**; and automatic preview of
   exports dropped in the inbox. The Discord path is tested against a stand-in connection and a stand-in bot API only:
   **it has never run against Discord.** Not built yet: captions, visual search, faces, Explore dataset declarations, and the
   4014 intent downgrade in the KliveBot service. Details, decisions and the full "not built" list: `ws-a.md` on that branch.
   **A decision there narrows the plan:** the Discord user-token gateway is not built in v3 (official bot gateway and
   Discord's data package only); it is Klives's call to reopen.

   Shared files WS-A's branch changes, for the merge: `Kernel/ModuleHost.DisposeAsync` (disposes modules that hold
   resources), `Api/ApiPlumbing.RouteRegistrar.StreamingRouteAsync`, `OmniscienceV3.BuildModules`, `Storage/Legacy/DiscordRaw`
   (`IsSystemType`), `Storage/Sqlite/SqliteExtensions` (`PreparedStatement.Reader`), `traceability.md` (75 rows, and a new
   `n/a` status) and `api-contracts.ts`.

   State of the other workstreams on 2026-11-10 (all sessions idle since the spend limit on 2026-11-09 23:44 UTC): WS-B at
   `fc02987` (inquiry module, X01–X16, knowledge API; 4 files uncommitted); WS-C at `2b948cc` (C-base 3/3: rollups and
   read-model projections; 8 files uncommitted); WS-D at `d8cead7` (replica fidelity gates; 1 file uncommitted). Each was
   sent the contract gaps that concern it (ws-e.md) on 2026-11-09; whether they acted is in their `ws-x.md`.
   **2026-11-12 — the coordinating session now does every workstream itself**, one at a time, each in that workstream's own
   worktree and branch (Klives: "Implement this plan"; the four workstream sessions have been idle since 2026-11-10 and are
   not woken). What it found and did that day:

   | Branch | Tip | State |
   |---|---|---|
   | WS-C `claude/omniscience-v3-ws-c-99fa83` | `981baeb` | The **Explore engine** is finished and committed (it had been left half-written and uncommitted): mirror, lenses, drill, facets, export, other modules' datasets, policy. 221 v3 tests pass; 5M-row benchmark p95 290 ms (budget 500 ms). Details and the answers to the website's contract gaps 3–5: `ws-c.md`. Still to build: analytics modules, radar and watchlists, digests and briefs, reply inbox, N12–N16, N19, N20, tactics X17–X40 and monitors X41–X52. C-base traceability rows are not ticked. |
   | WS-B `claude/omniscience-v3-ws-b-49da68` | `fcc2b52` | Its uncommitted tests did not compile. Now 219 v3 tests pass, with the whole inquiry engine, every reasoning tactic X01–X16, standing questions, identity links and merges, and the six modules through the kernel loop under test. **Thirteen defects found by those tests and fixed**, listed in `ws-b.md`; the worst: the age-evidence policy oracle never read its evidence (MinorGuard knew only Klives's own marks), a person asked about by first name was never found, and every reasoning tactic threw when started with a selection. Never exercised: the 33 knowledge routes and the Ask router (no route test harness). Traceability: eight rows ticked from tests; the rest are not. |
   | WS-D `claude/omniscience-v3-ws-d-21c7c3` | `ccfb51b` | 194 v3 tests pass. Chat tests and tests for all thirteen tactics X53–X65 landed (run-through tests with a scripted model). One defect fixed (a generated draft was marked as such only in words). Never exercised: the replica routes. Fidelity on real people is unmeasured until the shadow run. Traceability rows are not ticked. |
   | WS-A `claude/omniscience-v3-ws-a-878bce` | `730150f` | Unchanged. Captions, visual search and faces wait on decisions (see `ws-a.md`). |

   Done on 2026-11-12: what WS-B and WS-D had built but never ran is now under test (above). Order of work from here: the
   rest of WS-C, then the website's domain pages, then Phase 3.

   **Later on 2026-11-12** (same session, Klives: "continue everything left to do"):

   | Branch | Tip | State |
   |---|---|---|
   | WS-C `claude/omniscience-v3-ws-c-99fa83` | `366ac1c` | **Complete except X17 (a merge step with WS-B's extractor), X48 and X50 (need WS-B).** Since `981baeb`: signals (radar, watch rules N11, reply inbox N9, X41, X49), briefs and digests (N4, N10, X18, X30, X31, X52), transforms (X20–X22, X28, X29, X34, X37–X40), notices and relationship dynamics (N12, X36, X45), check-ins (N14, X44), data controls with both forget levels and backups (N20, X76), community pages (N13), similar people and the people map (N16), links (N15, X23), translation (N19, X33), monitors (X42, X43, X46, X47, X51) and the discourse tactics X32, X35. 295 v3 tests, 290 pass, 5 opt-in skipped. `ws-c.md` has every section and a "For the merge" list. |
   | WS-A `claude/omniscience-v3-ws-a-878bce` | `d8c8019` | The leftovers that need no model download: the OCR/transcript backlog of tracked people first (plan R2), expired attachment links refreshed through their source (F403; Discord only while its capture is on), audio/video length and size from ffprobe. 243 v3 tests pass, 1 opt-in skipped. Still waiting on decisions: captions, visual search, faces. |
   | WS-E website `omniscience-v3` | `5b49b9c` | Second slice on WS-C's contract: people directory and person page, signals, digests, communities (places, circles, the people map), links, data controls and health; nav groups, page access rules, a typed synthetic domain mock and 9 e2e tests (27 with the engine spec, all pass). Details and ten contract gaps: [ws-e.md](ws-e.md). |

   **Merge survey for Phase 3 (2026-11-12, `git merge-tree` in memory; no branch was touched).** Merging in the R7 order onto
   `omniscience-v3` (`d00b0e9`): WS-A merges cleanly; WS-C then conflicts in `api-contracts.ts` (generated: regenerate with the
   contract test), `traceability.md` (rows ticked on both sides), `Api/V3Routes.cs` (both add route registrations: keep both)
   and `OmniscienceV3.cs` (both rewrite `BuildModules`: keep every module; it must stay an instance method, WS-A's modules use
   the service); WS-B and WS-D conflict in the same files, WS-B also in `V3Runtime.cs`. No conflict in kernel or module code.
   The wiring the branches leave open is the list below plus `ws-c.md` "For the merge".

   **For the merge (Phase 3), wiring the branches left open** (all done on 2026-11-13 except the KliveRAG connector; see item 2): WS-B's modality and fiction oracles read the `labels` table but
   WS-C packs per-message labels in `classify_message` (set `KnowledgeKit.Modality` to an oracle over `ClassificationReader`);
   WS-B's modules should offer their datasets to Explore (`IExploreSourceProvider`) and get `KnowledgeKit.Explore` and
   `KnowledgeKit.Encoder`; WS-D's `ReplicaModule` takes WS-C's encoder; WS-A's and WS-C's module disposal overlap (keep one);
   Explore's own `assertions` dataset gives way to WS-B's `knowledge.assertions`. From WS-C (2026-11-12): WS-A's ingest
   must skip forgotten accounts and conversations (`Forgotten.Load(...).Blocks(...)`); WS-B's extraction and WS-D's replica
   training must leave out people whose analysis is off (`AnalysisOff.Load`); KliveRAG's Omniscience connector must purge a
   forgotten person; WS-B should read stored translations (`ITranslations.Cached`); WS-C removed every
   `SqliteConnection.ClearAllPools()` from v3 code and tests (it closes other databases' pooled connections) while WS-A's
   tests still call it — keep WS-C's version.
2. **Phase 3 integration, 2026-11-12/13** (Klives: "Yes, merge them"). Merged in the R7 order on `omniscience-v3`; the
   conflicts were the ones the survey found, all resolved by keeping both sides (the contract file regenerated by its test).

   | Commit | What |
   |---|---|
   | `d050894` | WS-A merged (no conflicts) |
   | `0476b9f` | WS-C merged; modules are disposed once, by the module host (the runtime disposed them a second time) |
   | `e478d13` | WS-B merged |
   | `cb054dd` | WS-D merged |
   | `621d1e4` | The wiring. One production module list (`ProductionModules`: 27 modules; a startup test checks that ids, settings, datasets, read models and tactic keys and codes are each claimed once). Forgotten people skipped at every ingest path. Analysis off honoured by WS-B (the knowledge reader leaves out what they wrote; no extraction planned, no identity links suggested) and WS-D (training refused, an existing replica blocked and purged and unblocked when switched back on, their messages masked in other replicas). Forgetting reaches WS-B's and WS-D's tables; claims and rows citing a forgotten message go, and the people they were about are consolidated again (change kind `evidence.removed`). WS-B reads the classifier's modality labels, the shared encoder, Explore and stored translations; WS-D gets the shared encoder. The last `ClearAllPools()` calls removed. |
   | `0ac131f` | WS-B's datasets served by Explore (the mirror's own `assertions` gives way to `knowledge.assertions`). X48: plan reminders are notices; a reminder whose plan is gone or held back by the minor guard is cancelled. X50: a standing question's flip is a notice, pushed as its own alert only when there are no notices. The Discord bot's "unauthorised" status now follows the stored row, and its re-ring is paced on the monotonic tick (both were load-dependent test failures). |
   | `9afcf26` | X17 extract facts from chosen messages: WS-B's pipeline on a selection (the people who wrote the messages, or the people chosen; Klives's own messages only when he chooses himself), interactive, coverage kept, consolidated before the run ends. Switching analysis off now reloads the knowledge reader at once (it cached the set for 15 s). |
   | website `5cde727`, `0d518cd` | The contract copied from the merged branch; sentences for the two new notice kinds |

   Tests: 474 of 479 v3 tests pass, twice in a row (5 opt-in skipped). The whole suite: 4,028 of 4,064 pass, 33 skipped, 3 fail, all outside v3. `KliveLLM.BriefContinuityTests.MultiWakeReplay…` fails the same way at the branch point `b270cc7` (master's, as noted on 2026-11-06; checked in a throwaway worktree). `KliveAPI.Telemetry.TelemetryEngineTests.InstantResponses_AtScale` and `Projects.StimulusQueueTests.UnavailableDestinationCannotDeadlock…` failed under the full run's load and pass on their own (outside its own folders the branch changes only KliveLLM's gateway files, `Program.cs` and the project file).
   Website: `vue-tsc` shows only master's two errors; the 27 v3 e2e tests pass.

   Not done at the merge: the v3 connector for KliveRAG (built since: item 3). Still open: the general search does not
   union `translate/search`, and WS-A's source health does not hand findings to the notices.
3. **After the merge, 2026-11-13/14** (Klives: "Do all of these", then "Yes, finish it").

   | Commit | What |
   |---|---|
   | `892d22b` | KliveRAG fed by v3 (`Integration/KnowledgeFeed.cs`, `KliveRAG/Connectors/OmniscienceV3Connector.cs`). One switch, `OmniscienceV3_FeedsKliveRag` (off), says which engine feeds KliveRAG's "omniscience" source; the other engine's documents are removed. A document is one person and category of current stated facts that are accepted or confirmed by Klives: never message text, an inference or a sensitive attribute, and never about someone forgotten, merged away, with analysis off, or who is or may be under 18. The message-search leg leaves the same people out. |
   | `077e481` | Link previews from providers (`links.unfurl.enabled`) are on by default: Klives's decision on 2026-11-13. |
   | `5d597e0` | Image captions (N2, module `media.caption`): a user turn in the LLM gateway can carry images (`LlmTurn.Images`); calls with images are logged as such and kept out of the prefix-cache measure that pauses background work; a daily budget (300); tracked, watched and Klives's own images first. Nothing is captioned for someone under 18 or with analysis off, and a caption never says who anyone is. |
   | `7b2eeb5` | Visual search (N2, module `media.visual`, `GET media/search`): a local CLIP model (ViT-B/32, MIT licence, each file pinned by SHA-256; Klives agreed to the download). Images are ranked by how they look and by the words in their caption and OCR text, and each hit says why it was found. The image pivot X24 is not built. |
   | `2437c28` | KliveRAG lets go at once. Every KliveRAG read (search, prompt injection, `read_knowledge_doc`) drops what v3 no longer allows before it answers, from the moment a forget is confirmed (`KnowledgeFeed.MayHold` reads the tombstone). The forget raises `data.forgetting` at confirmation and `data.forgotten` at the end; KliveRAG runs a pass on both. Documents say whom they name, and one naming someone no longer allowed is rebuilt without waiting for the profile. The forget run also has the people named together with the forgotten person consolidated again, and that path (job kind `reproject`) rebuilds their profile, timeline and plans. While v3 is not running, none of its documents are shown. |
   | `7be4506` | `GET people/names` (display names for person ids) and contract types for the Quality Lab's read model (`QualityLabDto`). |
   | website `74aec4a`, `29d1eb4` | WS-E slice 3: a person's knowledge, timeline and audit tabs, Review, Ask, Agenda, Quality Lab, Replicas, Sources and Images ([ws-e.md](ws-e.md)). Every first page load of the three v3 e2e specs goes through `loadPage`, which loads the page again when the dev machine runs out of socket buffers. |
   | `71f8607` | MinorGuard when a fact is read, not only when it is written. `knowledge/facts` and `knowledge/facts/get` served an inference made before someone was known to be under 18 (live until the next detective run, and in the history after it), and a restricted fact stored under an adult that names someone who later turned out to be under 18. `Attributes.IsShownFor` is the one read-side rule now, used by both routes in every mode, by the profile as it is built and by the profile route as it is served ([ws-b.md](ws-b.md), defect 14). |
   | website `c1aab00`, `5224b4e` | The two routes of the knowledge and replica groups that no page called: a "Look back" panel on a person's knowledge tab (`knowledge/facts`: true on a date as known today, the same going only by what had been said by then, on record that day, and the facts the list leaves out), and a blind test that is named in the link, carried on after a reload or from the results, and looked at again pair by pair (`replica/blind/session`). The image search waits until its filters stop changing. `loadPage` no longer waits for the load event, which a request that is never answered held back until the test timed out. |
   | website, uncommitted in the worktree (2026-11-15) | The pages on a design of their own, at Klives's request ("Do NOT use a template for the UI like the other pages", then "make sure youre editing omniscience v3"). They no longer use OmniTrader's design system: `assets/scss/omniscience-v3.scss` and v3's own `Card`, `Kpi`, `Drawer`, `StateBlock`, `DataTable` and `BarList` replace it. A new frame (navigation in two levels; a palette that finds people by name), a Brief as the first page (what needs you, worst first; the engine overview moved to `/omniscience-v3/engine`), and in the people directory a quick look beside the list and a comparison of two people (`insights/pair`, which no page called before). No backend change. [ws-e.md](ws-e.md), slice 5. |

   Tests: 519 of 525 v3, KliveRAG and gateway tests pass (6 opt-in skipped). The whole suite: 4,047 of 4,084 pass, 34 skipped, 3 fail, the same three as on 2026-11-13 and all outside v3: `KliveLLM.BriefContinuityTests.MultiWakeReplay…` (fails the same way at the branch point, so it is master's), and `KliveAPI.Telemetry.TelemetryEngineTests.InstantResponses_AtScale` and `Projects.StimulusQueueTests.UnavailableDestinationCannotDeadlock…`, which fail under the full run's load and pass on their own.
   Website: `vue-tsc` shows only master's two errors; all 37 v3 e2e tests pass (10 new); `npm run build` succeeds.
   With the uncommitted redesign of 2026-11-15: the same two errors; 40 v3 e2e tests pass (3 new), on each of the last three runs, the last one on the final stylesheet;
   `npm run build` succeeds. Nothing was run against a live engine, then or since.
4. Still to do: faces (N1) and the image pivot X24; the shadow run on live data (`OmniscienceV3_ShadowEnabled=true` on the
   server); parity gates, Quality Lab gates, and cutover, which switches `OmniscienceV3_FeedsKliveRag` and
   `ingest.discord_bot.enabled` on; then the website branch. Smaller open items: the general search does not union
   `translate/search`; WS-A's source health does not hand findings to the notices; no page opens a cited message
   (ws-e.md gap 10); twelve routes outside the knowledge, replica, source and media groups have no page yet
   (ws-e.md, "Not built"); the website branch is 14 commits behind `master` (2026-11-18) and has to take `master` in
   before cutover, where the old engine's pages were rebuilt as their own console in the meantime (ws-e.md, "At cutover"). Decisions still open for Klives: the cutover itself; the face stack and its model files; the Discord
   user-token gateway (not built).
