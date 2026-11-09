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

## Status
Work in progress — see the commit log; this file is updated as each part lands.
