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
| **C-base 3/3: rollups + read models (C-base complete)** | 2b948cc | `Modules/Rollups`, `Modules/Projections`, `Api/WsCRoutes.cs`, serving filter. **Merge this commit as "C-base".** |
| Explore engine | the commit after 2b948cc | `Modules/Explore`, `Kernel/Explore/ExploreContracts.cs` (additive), `Temporal/Zones.OffsetSegments` (additive), routes `explore/facets` and `explore/export`. Done by the coordinating session from 2026-11-12 (the workstream's own session stopped mid-way through it). |

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

## Explore (plan D4)
- **Engine:** `Modules.Explore.ExploreModule` implements the frozen `IExploreEngine` on `analytics.duckdb`. `V3Runtime.Explore`
  finds it, so the Phase 1 routes `explore/query` and `explore/records` work as they are. It adds `POST explore/facets`
  (`ExploreFacetsRequest` → `ExploreFacetsDto`: top values of up to 12 dimensions under the current filters) and
  `POST explore/export` (`ExploreExportRequest`: a drill token → every record as CSV, JSON lines or Parquet; a query → the
  aggregate as CSV or JSON lines; answered as a file download, permission `omniscience.data.export`).
- **Own datasets** (`ExploreDatasets`): `messages` (with the A2 labels as columns), `events` (reactions, presence, voice, typing,
  membership), `relations` (pair-days from the rollups), `concepts` (X19 hits), `assertions`, `tactic_runs`, `jobs`, `llm_calls`.
  Messages follow the change log, classification's outbox and zone changes; engine tables are refreshed every 5 minutes.
- **Other modules' datasets (self-describing, plan D4):** a module declares its `DatasetDescriptor`s in its manifest as before and
  additionally implements `Kernel.Explore.IExploreSourceProvider`, returning one `ExploreSource(datasetId, table, keyColumn,
  timeColumn, changeColumn?)` per dataset. Explore then copies that intel.db table or view into its own mirror table and serves
  the dataset — no change to Explore. With a change column (a number that only grows when a row is written) the copy is
  incremental, with a whole copy once a day and whenever the mirror holds more rows than the source (deletions); without one
  the table is copied whole on every pass (refused above `explore.whole_copy_max_rows`, default 1,000,000). Column types come from
  the dimension kinds (Time → integer, Number → real, Flag → boolean, others → text; measure-only columns → real). A source that is
  badly declared (unknown dataset id, a name that is not a plain identifier, a clashing id) is reported in the service log and
  left out; the rest are served. **Integrator:** construct Explore last, as `new ExploreModule(() => modules)` (see
  `OmniscienceV3.BuildModules`), and have WS-B's knowledge modules return their `KnowledgeSchema.DatasetTables` as
  `ExploreSource`s. WS-B's `knowledge.assertions` then supersedes Explore's own smaller `assertions` dataset, which can be
  removed from `ExploreDatasets` in the same change.
- **Lens and buckets:** every bucket (hour, day, week, month, quarter, year, hour of day, weekday) is computed on Klives's zone,
  the author's own zone (messages only: each message is mirrored with its author's zone for that month and that zone's UTC
  offset at the instant; UTC while the zone is unknown), UTC, or any IANA zone. Offsets come from `Temporal.Zones.OffsetSegments`
  (the same time-zone data as everything else in v3), so DST days are right and no calendar library runs per row. Day-precision
  and legacy unknown-zone timestamps are left out of hour buckets (R4).
- **Policy at planning time, by what a dataset holds (not its name):** presence, voice and typing events of minors are never
  counted; a dataset that binds `content.attribute` (or WS-B's `knowledge.attribute`) never shows special-category attributes, nor
  the restricted identity attributes of a minor; hour, hour-of-day and weekday buckets leave out every row a minor wrote or was
  written to, **whatever the query filters on** (a filter on one direct chat narrows to a person as surely as a person filter).
  Each exclusion is stated in the result's `explain`. A drill token only restates a query: permissions and policy are applied
  again when it is used, and message-level records need `omniscience.messages.read`.
- **Bounds:** 2 s timeout (`explore.timeout_ms`; a slower query is interrupted and answered `422 too_slow`), 10,000 groups, DuckDB
  memory limit and thread count as settings, a result cache of 256 answers keyed by query and mirror version.
- **The mirror is derived data.** `ExploreDatasets.SchemaVersion` numbers its layout; a file with another number is emptied and
  rebuilt from the stores. Erasing a person removes their rows from the mirror in the same pass that sees the erasure.

### Answers to WS-E's contract gaps (ws-e.md 3–5)
- **Gap 3, drilling into one bucket:** narrow the drill with the pseudo-dimension `@bucket`
  (`ExploreColumnKinds.BucketDimension`): `{"dimension":"@bucket","op":"Eq","values":[<the row's bucket value>]}`. `In`,
  `Between` (both ends included), `Gte` and `Lte` work too, so a brushed range is one filter. The engine evaluates it on the same
  lens as the result, so the browser never computes bucket bounds.
- **Gap 4, column kinds:** `ExploreColumn.kind` is one of `Measure`, `Bucket` (the column whose id is `@bucket`), or a dimension
  kind `Time` / `Entity` / `Category` / `Number` / `Flag` (`ExploreColumnKinds`). A `Time` group-by column is UTC epoch ms.
  Bucket values are text for calendar buckets (`2025-03-30T13:00`, `2025-03-30`, a week by its Monday, `2025-03`, `2025-Q1`,
  `2025`) and numbers for hour of day (0–23) and weekday (1 = Monday … 7 = Sunday).
- **Gap 5:** `explore/facets` and `explore/export` exist (above). Export is a `POST` whose response is the file: read it with
  `fetch` and save the blob (the Bearer header is kept).

## Shared files touched (small, additive)
- `Kernel/Explore/ExploreContracts.cs`: `ExploreSource`, `IExploreSourceProvider`, `ExploreColumnKinds` (additive; the frozen
  records are unchanged). `Temporal/Zones.cs`: `OffsetSegments` (additive).
- `Kernel/Modules/Datasets.cs`: three more D2 dimensions (`content.event`, `content.activity`, `content.attribute`).
- `OmniscienceV3.BuildModules()`: builds a list and adds `ExploreModule` last with the list. `V3Runtime.DisposeAsync`: disposes
  modules that own resources after the kernel stops. **WS-A's branch adds the same disposal inside `ModuleHost.DisposeAsync`**;
  keep one of the two at the merge (`ExploreModule.DisposeAsync` is safe to call twice either way).
- `Modules/Rollups`: the rollup outbox is now trimmed by the rollup drain after a week instead of by the projections after a
  day (it has two readers: projections and the Explore mirror; a reader further behind rebuilds from the cells).
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
- **Explore on 5M mirrored messages** (`ExploreTests.FiveMillionRows…`, opt-in with `OMNISCIENCE_PERF=1`; plan D4 budget: p95
  under 500 ms for 3 filters + 2 group-bys): **p50 197 ms, p95 290 ms** with DuckDB at 2 threads, a monthly bucket on Klives's
  lens, count and distinct people. The first version converted every row's time through DuckDB's ICU time-zone functions and
  formatted the bucket as text per row: p50 381 ms, p95 591 ms, over budget. Buckets are now integer arithmetic on a local
  time obtained by adding the zone's offset (a small decision tree over the zone's transitions), and the coverage query runs
  beside the aggregate instead of after it. Appending the 5M rows took 18 s.

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
| Explore engine (DuckDB mirror, lenses, drill, facets, export, other modules' datasets, policy) | done, tested (8 tests + the opt-in 5M-row run) |
| Analytics modules, radar/watchlists, digests/briefs, reply inbox, N12–N16, N19, N20 | pending |
| Tactics X17–X40 (except X19 done, X24–X25 WS-A), monitors X41–X52 | pending |
| Traceability rows | Explore rows ticked (F485, F486, F553, F555, F557; F061 and F148 wait for the website). **The C-base rows (encoder, classification, rollups, read models) are not ticked yet**: do that from the tests of each area before the merge. |

## Known limits (Explore)
- The mirror holds no message text; a drill fetches the text of the page it shows from the corpus. Exports therefore carry
  ids and dimensions, not text (the per-person export of N20 will carry text).
- A whole-copy dataset is copied on every 5-minute pass even when nothing changed. Give a large dataset a change column.
- An incremental dataset notices deleted rows when the mirror holds more rows than the source, or at the daily whole copy; a
  deletion balanced by an insertion in the same pass waits for the daily copy (erasing a person forces the copy at once).
- The subject lens exists for messages only; other datasets fall back to Klives's zone and say so in `explain`.
- Aggregate paging (`cursor`) is offset-based over at most 10,000 groups; record paging is keyset.
- `explore.threads` and `explore.memory_mb` are read when the service starts.
