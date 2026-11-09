# WS-C — Classification, analytics, read models & signals

Branch `claude/omniscience-v3-ws-c-99fa83`, cut from the Phase 1 commit 3dc87df. This file is the workstream's
progress record: what is done, what is not and why, and what the integrator must know. It is kept current as work
lands, so a restart can resume from it.

## Commits that matter to the integrator
| What | Commit | Notes |
|---|---|---|
| NuGet packages | 3f3f2f9 | `DuckDB.NET.Data.Full` 1.5.6.1 (MIT; pulls `DuckDB.NET.Bindings.Full` 1.5.6.1, MIT, and `Apache.Arrow` 23.0.0, Apache-2.0) and an explicit `System.Numerics.Tensors` 10.0.4 (MIT; already resolved transitively at that version, now pinned). Two lines in `Omnipotent/Omnipotent.csproj`. |
| C-base 1/3: encoder + vectors | 3613aee | `Ml/Encoders`, `Ml/Vectors`, kernel wiring of `ModuleContext.Encoder/Vectors`. |
| C-base 2/3: A2 ensembles + X19 | 7621a5b | `Ml/Classification`, `Modules/Classification`. |
| **C-base 3/3: rollups + read models (C-base complete)** | *this commit* | `Modules/Rollups`, `Modules/Projections`, `Api/WsCRoutes.cs`, serving filter. **Merge this commit as "C-base".** |

## What the other workstreams integrate against (C-base)
- **Shared encoder:** `ModuleContext.Encoder` (`IEmbeddingEncoder`, frozen R7 #5). Production: `LazyE5Encoder` from
  `SharedEncoder.Production` (passed by `OmniscienceV3.cs` as `V3RuntimeOptions.EncoderFactory`); it downloads on first
  use inside a job, never at startup. Tests: `new HashingTextEncoder(dims)` via `V3RuntimeOptions.Encoder`.
  `ModelVersion` is known before loading; use it as the vectors.db `model` key.
- **Vector store:** `ModuleContext.Vectors` (`Ml.Vectors.VectorStore`): `UpsertAsync`, `Get`, `Search` (exact over
  candidates/partitions, IVF over the whole space), `RandomSample`, `Page`, `DeleteEverywhereAsync` (forget-person).
  Spaces in `VectorSpaces` (message, stimulus, image, face, person, rule). The message space is owned by `classify`;
  other spaces are free for WS-A (image/face) and WS-D (stimulus). IVF is opt-in by calling `TrainAsync`/`ReassignStepAsync`.
- **Labels:** read through `Modules.Classification.ClassificationReader` (`ForMessages`, `ActiveLabelset`, gold API).
  Per-message head labels are packed in `classify_message` (one row per message and label set: 15 tasks as separate
  `labels` rows would cost ~12 GB at 5M messages); detailed records (LLM teacher/adjudication with rationale, fusions
  that involved the LLM or gold, X19 concept hits) are in the frozen `labels` table (`model_version` `llm.teacher.v1`,
  `llm.adjudicate.v1`, `a2.ls{n}`, `concept.{id}.v{n}`). WS-B's modality second opinion = `ForMessages(...).Answer("modality")`.
- **Gold:** classification corrections are operator.db `gold_labels` kind `classification`, natural key
  `{"item":"message:<id>","task":"<task>"}`; record them with `ClassificationModule.CorrectAsync` (re-fuses at once) or
  `POST /omniscience/v3/classify/correct`.
- **Rollups:** `Modules.Rollups.RollupReader` (person-days in the person's zone, pair totals both ways, scope days, zones).
- **Read models:** `person.dossier`, `overview`, `engine.health`, `conversation.card`, `guild.card`, served by the
  existing `GET /omniscience/v3/rm?model=&key=`; their body types are `[ApiContract]` (`DossierDto`, `OverviewDto`, …).
  Directory: `GET /omniscience/v3/people?sort=&tier=&platform=&q=&cursor=&limit=`.
- **Policy at serving time:** `ReadModels.IReadModelServingFilter` — `DataRoutes` now applies every module's filter to
  the rows it serves (defence in depth for MinorGuard). `ProjectionModule` filters the dossier.

## Shared files touched (small, additive)
- `Kernel/Modules/ModuleContracts.cs`: `ModuleContext.Encoder`, `ModuleContext.Vectors`, same on `KernelServices`.
- `Kernel/ModuleHost.cs`: passes them into the context (one line).
- `Kernel/Modules/Datasets.cs`: seven D2 dimensions for A2 tasks the catalogue lacked (politeness, formality,
  self_disclosure, info_density, conflict, support, about_klives).
- `V3Runtime.cs`: `VectorStore` created and migrated at start; `Encoder`/`EncoderFactory` options; owned encoder disposed.
- `OmniscienceV3.cs`: `EncoderFactory`, and `classify`, `rollups`, `projections` in `BuildModules()`.
- `Api/V3Routes.cs`: one line registering `WsCRoutes`. `Api/DataRoutes.cs`: the serving-filter loop (3 lines).
- `Docs/omniscience-v3/api-contracts.ts`: regenerated.

## Measurements (development machine: AMD Ryzen 9 5900X, AVX2, no AVX-512/VNNI)
- **Encoder agreement:** int8 (`model_qint8_avx512_vnni.onnx`) vs fp32 over a multilingual synthetic set: mean cosine
  0.9958, min 0.9933. Cross-lingual paraphrases rank above unrelated sentences in every pair for both builds.
  → int8 is the default (`classify.encoder_variant`).
- **Throughput (R2 capacity check):** 2 intra-op threads, short chat messages: **int8 336/s, fp32 224/s**. The plan
  assumed ~1,000/s; a 5M-message backfill therefore takes ~4.1 h of two cores (not ~1.5 h). The priority pass (tracked
  people → Klives's DMs → watch tier) runs first, so what matters is embedded first. Batching (64 items) is not yet
  tuned; larger batches may help — measure before changing.
- **Batch independence:** dynamic int8 quantisation scales activations per batch tensor: the same text alone vs in a
  batch differs by cosine ≥ 0.9959 (0.987 for the empty string, which production never embeds); the same batch is
  bit-identical.
- Real-model tests are opt-in: `OMNISCIENCE_MODELS=1` (cache in `OMNISCIENCE_MODEL_CACHE`, default
  `%LOCALAPPDATA%/omniscience-v3-test-models`).

## Decisions (sound defaults, recorded so they can be revisited)
- **DuckDB.NET.Data.Full 1.5.6.1** (released 2026-10-15): the latest release older than two weeks; its `.Full` bindings
  ship the native library for every platform.
- **Heads are in-house softmax regression (L-BFGS)** rather than ML.NET's: the same model family as the plan's "ML.NET
  logistic regression", but deterministic bit-for-bit, applied with SIMD straight to stored embeddings, weights in
  intel.db. LightGBM heads are not needed while linear heads on e5 meet the gates; revisit with Quality Lab numbers.
- **Context heads use [u, v, u⊙v] + exact exchange flags** (DM with Klives, reply to Klives, @mention of Klives, previous
  by Klives, same author) instead of a second encoder pass on the pair: no extra encoding cost.
- **"query: " prefix for every message:** e5's documented choice for embeddings used as features and for symmetric
  similarity; one pass per message serves search, clustering and heads (R2).
- **Encoder files from the official intfloat repository only** (explicit MIT). A community uint8 export exists but its
  repository states no licence, so it is not offered.
- **Rollup cells are recomputed, never adjusted**, and each message belongs to exactly one person-day: its local date in
  the zone of its own UTC month (so a zone change between months cannot double-count or drop a message).
- **Received activity (replies/mentions/reactions to a person) comes from the senders' pair cells**, dated on the
  sender's day; computing it on the receiver's day would need an all-time scan of the receiver's mentions per cell.
- **Zones:** rhythm-based per month (diurnal-template correlation), mapped to IANA zones over the months (DST tells
  Berlin from Lagos); thin months stay unknown unless the nearest confident months on both sides agree; minors are
  skipped (MinorGuard). A stated zone comes from a WS-B assertion with attribute `time_zone` (IANA value) — WS-B
  should use that attribute name.
- **Wellbeing distress task:** a per-message reading used only for N14 check-in suggestions, never stored as a trait
  and not exposed as an Explore dimension. MinorGuard applies to wellbeing suggestions (no person-targeted alerts for
  minors); a crisis-language exception for minors would be Klives's call and is not built.

## Status
| Area | State |
|---|---|
| Shared encoder, CPU throttle, vector store + IVF | done, tested (incl. opt-in real-model tests) |
| A2 ensembles: tasks, heads, calibration, stacker, gate, teacher, adjudication, gold, label sets, X19 | done, tested end to end on a synthetic corpus |
| Rollups (person/pair/conversation/guild/global days, slots, zones, drift) | done, tested (exact counts, DST, deletes, reactions, backfill, merges, zones, minors) |
| Read models (dossier, directory, overview, health, conversation/guild cards) + serving filter | done, tested (index-only reads asserted) |
| Explore engine (DuckDB mirror) | next |
| Analytics modules, radar/watchlists, digests/briefs, reply inbox, N12–N16, N19, N20 | pending |
| Tactics X17–X40 (except X19 done, X24–X25 WS-A), monitors X41–X52 | pending |
| Traceability rows | pending (ticked as each area lands) |
