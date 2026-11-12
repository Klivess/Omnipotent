# WS-B — Knowledge, deduction & inquiry (progress)

Branch `claude/omniscience-v3-ws-b-49da68` (worktree `.claude/worktrees/omniscience-v3-ws-b-49da68`), cut from Phase 1 (3dc87df).
Charter: [workstreams.md](workstreams.md). Status of every traceability row of these areas: [traceability.md](traceability.md).

## Layout (`Omnipotent/Services/OmniscienceV3/Knowledge/`)
| Folder | What |
|---|---|
| `Store/` | attribute vocabulary, natural keys, bitemporal `AssertionStore`, `GoldLabelStore`, all knowledge migrations + datasets (`KnowledgeSchema`) |
| `Evidence/` | `CorpusReader`, `Roster`, `EvidenceRenderer`, `EvidenceAssembler`, `TextMatch`, `TemporalResolver` |
| `Retrieval/` | `HybridRetriever` (FTS5 + vectors, RRF) |
| `Prompting/` | `StructuredLlm` (schema → parse → validate → one retry on the cached prefix) |
| `Deduction/` | extraction pipeline, verifier, consolidator, critic, detective, calibration, the `knowledge.deduction` module |
| `Entities/` | aliases, entities, relations, identity links (X67), merges, the `knowledge.identity` module |
| `Policy/EvidencePolicyOracle.cs` | age evidence → MinorGuard band (stricter, never looser) |
| `Timeline/`, `Plans/`, `Quality/` | N7, N8, N18 modules |
| `Inquiry/`, `Tactics/` | the inquiry engine, standing questions, X01–X16 |
| `KnowledgeKit.cs` | per-engine shared services + plug-in points |

## Modules (each its own intel migration line)
`knowledge.deduction`, `knowledge.identity`, `knowledge.timeline`, `knowledge.plans`, `knowledge.quality`, `knowledge.inquiry`.

## Decisions recorded for the integrator
- **NuGet:** `Microsoft.Recognizers.Text.DateTime` 1.8.13 (MIT, exact pin), own commit 9d3ce76. Used only by `Evidence/TemporalResolver.cs`.
- **Shared-file edits:** `Kernel/Modules/Datasets.cs` gains three D2 dimensions (`prov.source_kind`, `knowledge.attribute`,
  `knowledge.category`) — additive lines at the end of the catalogue.
- **Dataset → table map** for the Explore mirror: `KnowledgeSchema.DatasetTables`.
- **Plug-in points** (`KnowledgeKit`, keyed by the engine's `ModuleContext`): `Encoder` (WS-C `IEmbeddingEncoder`), `Modality`
  (A2 modality/dialogue-act second opinion; default reads `labels` tasks `modality` / `dialogue_act`), `Entailment` (local NLI),
  `Media` (OCR/transcripts/captions; default reads `labels` tasks `ocr` / `transcript` / `caption` on attachments with the text in
  `signals_json.text`), `Zones` (person zone per period; default knows only Klives's zone), `Fiction` (roleplay channels; default
  reads `labels` task `conversation_fiction` on conversations). WS-C/WS-A set these at integration.

- **Integrator, labels:** `LabelTableModalityOracle` and `LabelTableFictionOracle` read the frozen `labels` table, but WS-C packs
  its per-message head labels in `classify_message` and exposes them through `ClassificationReader.ForMessages(...).Answer("modality")`
  (see ws-c.md). At the merge, set `KnowledgeKit.Modality` to an oracle over that reader; until then no message has a modality
  opinion and nothing is set aside as a joke by label.
- **Integrator, Explore:** have the knowledge modules implement `Kernel.Explore.IExploreSourceProvider` (WS-C) and return
  `KnowledgeSchema.DatasetTables` as `ExploreSource`s, and set `KnowledgeKit.Explore` so X11 can read a chart point's records.

## Status (2026-11-12)
The workstream's own session stopped on 2026-11-09 with its tests uncommitted and not compiling. Since 2026-11-12 the
coordinating session works here. Everything below is committed; `219` v3 tests pass on this branch.

| Area | State |
|---|---|
| Knowledge store, evidence assembly, retrieval, temporal resolver | built; unit-tested (`KnowledgeUnitTests`) and exercised by every pipeline test |
| Deduction: extraction (two readings, agreement, adjudication), verifier, consolidator, critic, age evidence, MinorGuard | built; **accuracy gate passes** on the planted-fact corpus with a deliberately noisy model (`DeductionAccuracyTests`) |
| Detective, hypothesis watcher, calibration job, sweeps | built; run through the kernel loop with a model that infers nothing (`KnowledgeLoopTests`). **Not tested with real inferences or a resolved hypothesis.** |
| Inquiry engine (understand → policy → plan → gather → reason → verify → aggregate → persist) | built; tested end to end (`InquiryTests`, 11 tests) |
| Reasoning tactics X01–X16 | built; each tested as a job (`InquiryTests`, `ReasoningTacticsTests`). X11 (explain a chart point) is only tested for its refusal path: it needs the Explore engine, which is on WS-C's branch. |
| Standing questions (X14) | built; tested: backfill ignored, unrelated evidence set aside without a model call, a flip alerts, the minimum interval defers, minors are never watched |
| Identity links, merges, X67 | built; tested: two signals, never merged unasked, merge, undo. **Not tested:** that a merge re-attributes every person-keyed derived row. |
| Life timeline (N7), plans and dates (N8) | built; run through the kernel loop on the planted corpus (an overdue promise, a birthday, a relocation). **Not tested:** reminders being sent, cancellations and reschedules, Feb 29 beyond the date arithmetic. |
| Quality Lab (N18) | built; the lab job runs in the kernel loop, the gate and the metrics are unit-tested. **Not tested:** the planted-evaluation job, per-category precision from gold, prompt A/B. |
| Knowledge routes (`Knowledge/Api/KnowledgeRoutes.cs`, 35 routes) and the Ask router | built, **never exercised**: there is no route test harness (a route needs KliveAPI's request object). They delegate to module methods, most of which are tested above. First real exercise is the shadow run. |
| Traceability | eight rows ticked from tests (F026, F195, F241, F258, F259, F537; F264 and F547 in progress). **The rest of this workstream's rows are not ticked**: do that row by row against the code before the merge. |

### Defects the tests found (all fixed on this branch)
1. The age-evidence policy oracle never read its evidence (an overflow in a "table checked at" timestamp): MinorGuard knew only
   Klives's own marks. Found by the accuracy test.
2. A person asked about by first name or part of a handle was never found (names were matched whole), so nearly every
   inquiry ended in "which person?". Names now match by whole words.
3. Every reasoning tactic accepted "null" (the catalogue was built before a static field its descriptors read), so starting
   any of them with a selection threw.
4. The expertise finder (X05) threw on every run: an ambiguous column in its own query.
5. Calibration could produce a curve that was not monotone, and kept steps fitted on a handful of labels.
6. The deterministic date check read "the day" inside "back in the day" as today and overrode the model with it.
7. For a yes/no question, passes that answered no had their supporting evidence filed under "for".
8. Behaviour prediction (X08) counted a message Klives followed up a day later as answered, and its statistics could never
   amount to enough evidence to answer on.
9. Short search words were prefix-matched ("cat" found "Catholic").
10. A standing question could be kept open on a minor.
11. Accounts were paired for same-person comparison only when their names were identical after normalising ("Dee Marsh" and
    "dee_marsh" were never compared).
12. Incident reconstruction (X13) read everything either person said in any channel both use.
13. Explaining a chart point (X11) read the wrong field name from Explore's records.

### Known limits
- Same-person candidates are found by name, avatar and unfurled links. A link that appears only in message text pairs two
  accounts only once they are already candidates (it is then counted as a signal). A link index (WS-C, N15) would let it pair them.
- Without the shared encoder (WS-C) retrieval is full-text only, and says so in every report's coverage.
- Inquiries do not read structured activity (presence, games, music) yet; that arrives with the Explore datasets at the merge.
