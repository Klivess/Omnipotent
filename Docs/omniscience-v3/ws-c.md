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
| Signals (radar, watch rules, reply inbox, X41, X49) | the commit after the Explore engine | `Modules/Signalling`, `Api/SignalsRoutes.cs`. Needs nothing from another workstream; reads the classifier's labels and the rollups when they are installed. |
| Digests and briefs (N10, N4, X18, X30, X31, X52) | the commit after Signals | `Modules/Briefs`, `Modules/PersonZones.cs`, `Api/BriefsRoutes.cs`. Reads the rollups, the reply inbox and the frozen `assertions` table when they are there. |

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

## Signals: radar, watch rules, reply inbox (plan "Signals, briefs and search", N9, N11, X41, X49)
Module id `signals`, code in `Modules/Signalling/` (the namespace is `Modules.Signalling` on purpose: a namespace called
`Signals` hides the classifier's static class `Signals` from every sibling module). Its own intel migration line; tables
`sig_alerts`, `sig_alert_hits`, `sig_inbox`, `sig_pending`, `sig_watch_state`, `sig_watch_candidates`, `sig_watch_judgements`,
`sig_state`. Rules, dismissals and Klives's verdicts live in operator.db (`watchlists` kind `semantic`, `dismissals` kind
`inbox`, `gold_labels` kind `signal`), so they survive a rebuild of the derived store.

**One scan job** (`signals/scan`, CPU lane, planned on message changes and every 30 s) reads the corpus change log and the
classification label outbox from its own cursors. A message is looked at when it arrives (for what is exact) and again when
its labels arrive (for what needs meaning). Every write is keyed by the message, so a second look never alerts twice.
Nothing runs on the ingest path (F050, F366).

- **Live (T5, F047):** a message can alert when its change is `source_kind=live` and arrived within `signals.freshness_hours`
  (12 h), or, when it is looked at again for its labels, when it first arrived live within that time. An edit alerts only for a
  message that is itself that young. Backfills and imports never alert, never push, never nudge.
- **Radar:** a hit is (in this order) a structured @-mention of any of Klives's identities (F049, F531), the ensemble reading
  `about_klives = mentioned` or `indirect` above its threshold, or a confirmed name matched as a whole word in any script and
  letter case with code, links and custom emoji removed (F048). A name match is exact about the word, not about who is meant:
  with a reading the reading decides ("not about him" drops it, "spoken to him" is the inbox's business), without one it waits
  `signals.radar.reading_grace_seconds` (90 s) and then alerts on the name alone. Only confirmed names can alert (F254, F527):
  `signals.radar.aliases`, the current names of his own accounts, and whatever the integrator passes as `confirmedAliases`.
  His own messages, bots, his own direct chats and non-authored text (copypasta, forwards) never alert.
- **Grouping and delivery:** hits in one server (any of its channels) or one chat within `signals.burst_minutes` (10) join
  the open alert. A new alert is `pushed` (live stream topic `alert`), `quiet` (inside `signals.quiet_from`–`quiet_to` in
  Klives's zone) or `digest` (more than `signals.max_pushed_per_hour` already pushed). Held alerts are kept and counted in the
  read model `signals.summary`. A deleted source message marks its alert (`source_deleted_at_ms`); the alert stays.
- **Reply inbox (N9, F502):** an item opens for a message said to Klives (his direct chat, a reply to him, an @-mention, or
  the reading `addressed`) that the ensemble reads as a question or a request, and for a direct chat that ends on the other
  side (`unanswered`, one item per chat). In a group or a server a question has to be addressed to him. It resolves when he
  writes later in his direct chat, replies to the message, or writes in that channel or group within
  `signals.inbox.channel_answer_minutes` (120); or when he dismisses or snoozes it. A newer message from the same person
  reopens a dismissed item. Items go `stale` after `signals.inbox.stale_days` (30). The list is ranked by how much he talks
  with the person (rollup pair totals of the last half year) times how long it has waited. **Reading of "live-only":** recent
  imported or backfilled messages are listed (marked `live = false`) but never push an event and are never nudged.
- **Nudges (X49):** an open live item older than `signals.inbox.nudge_hours` (24) is pushed once as `nudge`, outside quiet
  hours. The tactic `x49.reply_nudges` gives the same list on demand.
- **Watch rules (N11, X41):** a rule is plain text plus an optional scope (people, conversations, servers).
  1. Policy: a rule scoped to someone who is or may be a minor is refused when saved (MinorGuard, person-targeted alerts);
     the model then judges the text against the special categories and a rule aimed at one is refused with the standard
     refusal text. A rule whose scope later includes a minor stops.
  2. Calibration: the rule text is embedded with the shared encoder; the prefilter's gate starts where 3% of a random
     sample of message vectors pass, and after five confirmed matches moves just below the least similar confirmed one
     (kept between where 10% and where 1% pass). Recalibrated weekly and when the encoder changes.
  3. Prefilter (in the scan, no model): live, authored, in-scope messages at least as similar as the gate become candidates.
  4. Confirmation (`signals/confirm`, LLM background lane): batches of 25, most similar first, at most
     `signals.watch.max_checks_per_hour` (120) per rule; the prompt says a negation, a question, a quote or a joke is not
     a match. A match at `signals.watch.match_confidence` (0.6) or more becomes an alert, grouped like the radar.
  5. Feedback: Klives's verdict on an alert is gold. A wrong one dismisses the alert, becomes "does not match" in the rule's
     prompt from then on, and is a judgement the gate learns from. A verdict on a radar alert also corrects the classifier's
     `about_klives` label through the classification module (done in the route).
- **Forgetting a person:** on `person.erase` everything here that refers to them or to a message the corpus no longer has is
  removed, including hits inside other people's grouped alerts and Klives's verdicts on those messages.
- **Explore:** datasets `signals.alerts` and `signals.inbox` (followed by a change counter), both minor-filtered.

**Routes** (`Api/SignalsRoutes.cs`, all under `signals/`): `GET alerts` (keyset; `state`, `kind`, `rule`, `person`),
`POST alerts/state` (the named ones, or `all` unseen in one call: F160), `POST alerts/feedback`, `GET watchlists`,
`POST watchlists/save|enabled|delete`, `GET inbox` (`state`, ranked when open), `POST inbox/resolve` (dismiss, snooze,
reopen). Reads need `omniscience.persons.read`, changes `omniscience.alerts.manage`. A refusal is a 422 with code
`refused` and the reason. Rows carry ids; texts, names and titles are read from the corpus when the page is shown (F387).

**For the integrator**
- `new SignalsModule(confirmedAliases: …)`: pass a function that returns Klives's accepted nicknames from the knowledge
  store (WS-B). Until then only the settings and his account names are matched.
- Alerts reach the live stream and the dashboard. Delivery by KliveBot DM (plan N11) is not built: it needs a subscriber on
  topic `alert` in the service that owns the bot.
- Role mentions are stored but not attributed to Klives: his role membership is not in the corpus.
- How well `about_klives` and `dialogue_act` read real messages is measured in the shadow run, not here: the tests give the
  module its readings.

## Digests and briefs (N10, N4, X18, X30, X31, X52)
Module id `briefs`, code in `Modules/Briefs/`. Tables `brief_digests`, `brief_person`, `brief_runs`. Subscriptions are
`watchlists` rows of kind `digest`; where Klives last looked is `last_seen` (both operator.db).

- **Cited summaries (`CitedSummarizer`):** groups of items go to the model in as few calls as the real window allows
  (`ContextPacker.Budget`, at most 6 calls a run). Every item has a ref number and every point cites refs. A ref that was
  not shown is dropped, and a point left without a ref is dropped with it. A level up (servers over channels, the whole
  over servers) is the same operation over the points below, whose citations resolve back to message ids. When the input
  does not fit, each group keeps its newest items in proportion and the result says how many were shown.
- **Dates:** every message line carries a T3 stamp (author-local when the rollups know the zone). The prompt forbids
  "today", "yesterday" and the like; points carry message ids, so the page words time relative to when it is read.
- **Digests (N10):** scope `all`, `guild`, `conversation` or `person`, over [from, to). Without a start it begins where
  Klives last looked at that scope (`POST digests/seen`), else `briefs.digest.default_hours` back; never further than
  `briefs.digest.max_days`. A period with no messages is "nothing new" and costs no model call. Normally two calls:
  one over the conversations, one that merges their points into server points and the overall points. A server with one
  active channel has that channel's points. `newFacts` lists assertions recorded in the period (person and `all` scopes),
  with the dossier's filters (MinorGuard attributes, sensitive traits). A digest that cannot be written ends as `failed`
  with the reason (three attempts), never as "running" for ever.
- **Scheduled (X52):** a daily catch-up at `briefs.digest.daily_at` (07:30 in Klives's zone; empty switches it off) and
  subscriptions (daily or weekly at HH:mm). A tick every five minutes runs what came due since its last run; a missed
  occurrence runs once, late. A scheduled digest covers what happened since its previous run, or since Klives last looked
  when that is later.
- **Person brief (N4):** one model call over four groups (the last 7 days, the days before, between them and Klives,
  messages read as tension or distress) plus what needs no model: activity and style from the rollups, the sentiment mix
  of the last 30 days against the 60 before, facts (filtered), and what of theirs is waiting in the reply inbox. It is
  `stale` when the newest message it rests on is older than `briefs.person.stale_days`. **Not in it yet:** upcoming dates
  and plans, which come from WS-B's N8 after the merge.
- **MinorGuard:** a digest, a subscription or a brief about one person is refused for someone who is or may be a minor,
  and a brief written earlier is no longer shown. Server and chat digests include what minors said in those conversations
  (their conversations stay readable).
- **Tactics:** `x18.summarise` (messages, conversations, a time range), `x30.topic_snapshot` (a server or conversation and
  a period), `x31.action_items` (plans, promises, unanswered questions, open loops), `x52.digest_subscription`.

**Routes** (`Api/BriefsRoutes.cs`): `GET digests` (keyset), `GET digests/item`, `POST digests/run` (202),
`POST digests/seen`, `GET digests/subscriptions`, `POST digests/subscriptions/save|delete`, `GET briefs/person`,
`POST briefs/person` (202). Reading needs `messages.read` (digests) or `persons.read` (briefs); asking for one needs
`analysis.run`; subscriptions need `alerts.manage`. The finished item is also read model `briefs.digest` (key `all`,
`guild:<id>`, `conversation:<id>`, `person:<id>`) and `briefs.person` (key: person id), announced on the live stream.

## Shared files touched (small, additive)
- Briefs: one line each in `Api/V3Routes.cs` (`BriefsRoutes`) and `OmniscienceV3.BuildModules()` (`BriefsModule`).
- Signals: one line in `Api/V3Routes.cs` registering `SignalsRoutes`, one in `OmniscienceV3.BuildModules()` adding `SignalsModule` (before Explore, which mirrors its datasets). Nothing else shared.
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
| Signals: radar, semantic watch rules (N11), reply inbox (N9), nudges; tactics X41 and X49; routes; Explore datasets | done, tested (16 tests with supplied readings; the classifier's real readings and KliveBot delivery are not exercised) |
| Digests (N10, scheduled X52), person brief (N4), cited summaries; tactics X18, X30, X31 | done, tested (8 tests with a scripted model: what a real model writes is first seen in the shadow run) |
| Analytics modules, N12–N16, N19, N20 | pending |
| Tactics X17, X20–X23, X28–X29, X32–X40 (X18, X19, X26, X27, X30, X31 done; X24–X25 WS-A), monitors X42–X48 and X50–X51 | pending |
| Traceability rows | Explore rows ticked (F485, F486, F553, F555, F557; F061 and F148 wait for the website). Signals rows ticked (F047–F050, F387, F502, F528–F531 fixed; F086, F125, F158, F160, F161, F254, F366, F527 in progress, waiting for the website or for WS-B). **The C-base rows (encoder, classification, rollups, read models) are not ticked yet**: do that from the tests of each area before the merge. |

## Known limits (Explore)
- The mirror holds no message text; a drill fetches the text of the page it shows from the corpus. Exports therefore carry
  ids and dimensions, not text (the per-person export of N20 will carry text).
- A whole-copy dataset is copied on every 5-minute pass even when nothing changed. Give a large dataset a change column.
- An incremental dataset notices deleted rows when the mirror holds more rows than the source, or at the daily whole copy; a
  deletion balanced by an insertion in the same pass waits for the daily copy (erasing a person forces the copy at once).
- The subject lens exists for messages only; other datasets fall back to Klives's zone and say so in `explain`.
- Aggregate paging (`cursor`) is offset-based over at most 10,000 groups; record paging is keyset.
- `explore.threads` and `explore.memory_mb` are read when the service starts.
