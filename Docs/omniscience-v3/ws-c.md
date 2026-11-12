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
| Transforms without a model (X20–X22, X28, X29, X34, X37–X40) | the commit after digests and briefs | `Modules/Transforms`, `Api/TransformsRoutes.cs`. `TransformsModule` is given the module list (to check saved charts against declared datasets), like Explore. |
| Notices, relationship dynamics and communities (N12, X36, X45) | the commit after the transforms | `Kernel/Modules/Notices.cs` (additive contract), `Modules/Insights`, `Api/InsightsRoutes.cs`, signals migration 2. `InsightsModule` is given the signals module (its notice sink). |
| Wellbeing check-ins (N14, X44) | 9cb702a | `InsightsModule.Wellbeing.cs`, insights migration 2. |
| Data controls (N20, X76) | 5bf8ec4 | `Modules/DataControls`, `Storage/Forgotten.cs`, `Storage/AnalysisOff.cs`, `Api/DataControlsRoutes.cs`; analysis off touches the classifier, rollups, signals, briefs, insights, transforms, explore, projections and the tactic runner (a few lines each); the old-database importer honours both. See "For the merge" in its section. |
| Community pages (N13), similar people and map (N16) | 59c185d | `InsightsModule.Spaces.cs`, `InsightsModule.People.cs`, `Layout2D.cs`; insights migrations 3 and 4; rollups migration 2 (`rollup_scope_day.klives`). |
| Link intelligence (N15, X23) and translation (N19, X33) | the commit after community pages | `Modules/Links`, `Modules/Translate`, `Api/LinksRoutes.cs`, `Api/TranslateRoutes.cs`, `Kernel/Modules/Translations.cs` (additive). `BriefsModule` now takes the translation layer; in `BuildModules()` it is added after the list for that reason. Looking links up at providers is **off by default** (`links.unfurl.enabled`). |

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

## Transforms without a model (X20–X22, X28, X29, X34, X37–X40)
Module id `transforms`, code in `Modules/Transforms/`. No jobs and no intel tables: the tactics read the corpus, the
vectors and the rollups, and keep bundles and charts in operator.db (`notes` kind `bundle`, `saved_views` page
`dashboard`).

| Tactic | What it does | Notes |
|---|---|---|
| `x20.find_similar` | messages (or a description in `text`) → the messages closest in meaning | Query = the centre of the seeds' vectors. With people, conversations, servers or a time range selected the search is exact over that scope's newest 50,000 messages; otherwise it goes through the IVF index. Seeds, deleted messages and anything after the as-of are never returned. |
| `x21.context` | one message → the messages before and after, the message it replied to, the replies, the reactions | Honours the as-of: later replies and reactions are not there yet. Reactions come from who reacted, or from the observed count where only a count exists. |
| `x22.phrase_tracker` | a phrase → first use, each person's first use, monthly and per-server counts, who picked it up from whom | FTS5 phrase match with the phrase quoted as data. "Picked up from" = whoever last used it before them in the conversation of their first use, within 30 days. |
| `x28.cohort_comparison` | a cohort against everyone else active in the period | Mann–Whitney U per measure, Benjamini–Hochberg at 5% across measures, sample sizes on every row. Needs three qualifying people on each side. |
| `x29.period_comparison` | two periods of a server, a conversation, a person or everything | Activity, who appeared and who went quiet, and one chi-square per label task (corrected across tasks). |
| `x34.common_ground` | two to five people → shared servers and conversations, mutual contacts, shared facts, what is between them | Facts pass the dossier's filters per person. |
| `x37.closest_contacts` | a person → who they interact with most, how mutual, growing or fading | From the rollups' pair totals. |
| `x38.evidence_bundle` | any selection → a case file with a title and a note; run again with its id to add | Exports as Markdown or JSON (`GET bundles/export`), written from the corpus as it is now; a protected fact is listed as withheld. **No PDF**: print the Markdown. |
| `x39.dataset_export` | an Explore drill selection → what to post to `explore/export` | The file is streamed by the route; the tactic exists so the export can be chained and offered by Ask. |
| `x40.custom_chart` | a dataset, dimensions and measures → a chart saved on the dashboard | Checked against the dataset's own dimensions and measures before it is saved; the saved view records the permission the dataset needs. |

MinorGuard: X28, X29, X34 and X37 are refused when a selected person is or may be a minor, and minors are left out of
rankings, comparisons and member lists (counted, never named). `Stats.cs` holds the tests (Mann–Whitney with tie and
continuity correction, chi-square with its exact tail, Benjamini–Hochberg), checked against textbook values.

**Not built here:** X17 (extract from a selection) calls WS-B's extractor and is a merge step; X23 (link tracker) comes
with link intelligence (N15); X32, X33, X35 and X36 are pending.

## Relationship dynamics and communities (N12, X36, X45) and notices
Module id `insights`, code in `Modules/Insights/`; tables `ins_pair_week`, `ins_pairs`, `ins_pair_events`,
`ins_communities`, `ins_members`. One job (`insights/relationships`, nightly at 04:15 in Klives's zone, or
`POST insights/refresh`) rebuilds everything from `rollup_pair_day`, so it can be thrown away and recomputed.

- **Series:** per pair and week (Monday to Sunday): interactions each way, and how many of them the ensemble read as
  conflict, negative or positive. `closeness` = their interactions over the geometric mean of everything each of them did
  in the same weeks, so it is a share of attention, not a volume: someone who writes less to everyone is not "drifting".
- **Trend** (last 8 weeks against the 26 before): `new`, `closer`, `steady`, `drifting`, or `quiet` when one of them
  stopped writing to anyone. Pairs with fewer than `insights.pair.min_interactions` in the window are not tracked.
- **Events** (stable ids, so a rebuild finds nothing twice): `fight` (a week with at least 3 conflict-read interactions
  that are at least 30% of the week), `reconciled` (within 8 weeks: they talk again, calmly, and it did not flare up in
  between), `drifting` / `closer` / `new` (once per half-year), `introduced` (their first interaction comes after both had
  at least 5 interactions with a third person in the 12 weeks before; the event names that person).
- **Communities:** Leiden (`Leiden.cs`: local moving, refinement, aggregation; deterministic) on the interactions of the
  last half year. A community keeps its id while it keeps most of its people (Jaccard matching against the previous run).
  A **bridge** is someone with a quarter or more of their interactions in another community.
- **Notices (X45):** a new event of the last two weeks in one of Klives's own relationships, or between two people he
  tracks, becomes a notice (a fight or drifting of his own asks for attention; the rest is for information).
  `x45.relationship_watch` switches this on and off and lists what was found; `x36.influence` shows whom a person brought
  together, which communities they bridge, and who turns to them far more than they turn back (direction of attention,
  **not** topics: "whose topics they amplify" needs the topic model and is not claimed).
- **Minors** are in none of it: not in pairs, events, communities or tactic outputs.
- **Reply-time asymmetry** is computed when a pair is opened (`GET insights/pair`), from the rollups' latency histograms.
- **Routes:** `GET insights/communities` (also read model `insights.communities`), `GET insights/person`,
  `GET insights/pair`, `POST insights/refresh`. Explore datasets `insights.pairs` and `insights.pair_events`.
- **Coverage:** a community exists only in interactions Omniscience has seen; the read model says so.

**Notices** are the one list for findings of any module (`Kernel/Modules/Notices.cs`: `Notice`, `INoticeSink`; the signals
module implements the sink, table `sig_notices`, routes `GET signals/notices`, `POST signals/notices/state`, dataset
`signals.notices`). A notice id is chosen by the module, so finding the same thing again changes nothing. Severity `info`
is listed only, `attention` follows quiet hours and the hourly cap, `urgent` is pushed at once. A finding older than 48
hours when handed in is listed, never pushed. A person-targeted notice about a possible minor is not recorded.
**For the integrator:** WS-B's standing-question flips (X50) and WS-A's source health can hand their findings to the same
sink: pass the `SignalsModule` instance to them in `BuildModules()`.

## Wellbeing check-ins (N14, X44)
Part of the `insights` module (`InsightsModule.Wellbeing.cs`, table `ins_wellbeing`); the job `insights/wellbeing` runs
after the nightly relationship job.

- **Who:** the people Klives tracks or pinned, and the `insights.wellbeing.close_contacts` (30) people he interacts with
  most. Never minors.
- **What is measured:** per person and week, the share of answered messages the ensemble read as negative (weeks with at
  least 10 answered), and how many messages they wrote (people who usually write at least 15 a week). Each is compared
  with that person's own previous 26 weeks by a robust z (median and MAD, with a floor so a perfectly regular history does
  not turn a wobble into an alarm).
- **When it suggests:** both of the last two complete weeks beyond `insights.wellbeing.threshold` (2): `mood` or
  `withdrawn`. Not when the same two weeks a year earlier looked the same against their own baseline: that is recorded as
  `seasonal` and not suggested. One suggestion per person and kind in four weeks.
- **Crisis:** a live message of the last 48 hours that the ensemble read as `distress = crisis` (probability at least
  `insights.wellbeing.crisis_probability`) is suggested at once, as an urgent notice, with a general text on what to do.
- **What it says:** the measurements (shares, counts, z-scores, the weeks), never a judgement or a diagnosis. Suggestions
  only; nothing is sent to anyone.
- **Routes:** `GET insights/wellbeing`, `POST insights/wellbeing/state` (done, dismissed). `x44.wellbeing_watch` switches
  it on or off (`insights.wellbeing.enabled`) and lists the open suggestions.
- **Forgetting a person** removes their rows from every insights table at once (the module follows `person.erase`).
- Not exercised: the ensemble's `sentiment` and `distress` readings on real messages (the tests write the rollups and the
  labels themselves).

## Community pages (N13): servers and group chats
Part of the `insights` module (`InsightsModule.Spaces.cs`, table `ins_spaces`). The plan calls a server or group chat a
"community"; the code calls it a **space**, because N12 already uses "community" for the circles found in who interacts
with whom. The job `insights/spaces` runs after the nightly relationship job and builds a page for every server and group
chat with at least `insights.space.min_messages` (200) messages in the window (`insights.space.window_days`, 90), the
busiest `insights.space.max_spaces` (150) of them. `POST insights/space/refresh` builds one page on request, whatever its
size. Routes: `GET insights/spaces` (the list), `GET insights/space?scopeKind=guild|conversation&scopeId=`.

A page holds, for the window and compared with the window before it:
- **Activity**: messages and people, a count per day (Klives's days), the busiest channels of a server.
- **Core members**: the share of everything written there, the days they were there, and their *devotion* (the part of
  everything they wrote anywhere that they wrote there), so a member is not "core" only because they write a lot everywhere.
- **Rising members**: at least 15 messages in the last 30 days and at least twice what their earlier rate predicts;
  `isNew` when they did not write there before, `everywhere` when they became as much more active everywhere.
- **Tone**: how the messages read (shares of what the ensemble answered) and the share read as tense, against the window before.
- **About Klives**: messages to or about him by kind (addressed, mentioned, indirect), the tone of exactly those messages
  (target-aware, not the mood of the place), and who wrote them. This comes from a new rollup column:
  `rollup_scope_day.klives` (rollups migration 2), filled when a scope day is built.
- **Notable days**: a day far busier than its previous four weeks (robust: median and MAD), a day with many tense
  messages, a wave of members joining or leaving.
- **Factions**: Leiden on who answers and mentions whom inside the place in the window (edges with at least
  `insights.space.faction_min_edge` interactions). A faction keeps its id from night to night while it keeps most of its people.
- **Coverage**: how many of the channels Omniscience knows of it has messages from, how many are read back to their first
  message, and a sentence that says so. Every figure is about that part only.

People who are or may be under 18 are counted and never named (`peopleLeftOut`); people whose analysis is switched off are
not in the rollups at all. Both rules are applied again when a page is served, because the page is from last night and the
rule is from now. When a person is forgotten, they are taken out of every stored page at once; the counts follow at the next
build. Not exercised: real label volumes and real servers (the tests seed a server of nine people and write the readings
themselves); topics per community are the digests' (`latestDigestId` points at the newest digest of the place), a topic
model of its own is not built.

## Similar people and their map (N16)
Part of the `insights` module (`InsightsModule.People.cs`, `Layout2D.cs`, table `ins_people`). The job `insights/people`
runs after the nightly relationship job, for everyone with at least `insights.people.min_messages` (50) messages in a year
who is an adult and whose analysis is on.

- **One vector per person**, four parts: what they write about (the centre of their last 200 message vectors, weight
  0.35), how their messages read (the share of every label of twelve readings, 0.25), how they write (thirteen ratios:
  words per message, replies, links, pictures, edits, direct chats, reactions, bursts, ...; 0.2) and when they write (the
  hour distribution, 0.2). Tone and style are standardised across everyone, each part is unit length, so the cosine of
  two people is the weighted sum of four cosines. Stored in the vector store (`person` space, model `people-v1`).
- **`GET insights/similar?personId=`**: the nearest people, each with the four part-similarities, the traits both share
  (both at least one standard deviation from the average in the same direction: "long messages", "up at night",
  "often negative (sentiment)", ...) and the servers both are most active in. A person's own strongest traits come with it.
  Minors are refused; someone whose analysis is off is refused and never offered as a match.
- **`GET insights/map`**: everyone on a plane, coloured by their N12 community. The `insights.map.max_people` (2000) most
  active are laid out by an exact t-SNE (cosine distance) started from the principal components; everyone else is placed
  at the weighted centre of the five people they resemble most. There is no randomness. Each night starts from the
  previous night's positions and is then turned, mirrored, scaled and shifted onto them (Procrustes), so the map changes
  only as much as the people did. Stored in the layout's own scale, served fitted to a unit circle.
- Limits: t-SNE is exact, so time and memory grow with the square of the people it lays out (2,000 people: two 16 MB
  tables, a few seconds on half the cores). A trait that hardly varies between people still counts as much as one that
  does, once standardised. "Personality" here is the mix of readings of someone's messages, nothing more.

## Link intelligence (N15, X23)
Module `links` (`Modules/Links`), routes under `links/` (`Api/LinksRoutes.cs`), tactic `x23.link_tracker`.

- **What a link is.** `LinkParser` finds the links in a message and normalises them, structurally: tracking parameters and
  fragments go, the rest is put in one order, and every spelling of a known thing becomes one address (`youtu.be/…`,
  `/shorts/…`, `music.youtube.com/watch?v=…` are one video; `twitter.com`, `vxtwitter.com` and `x.com` one post; and so on
  for Spotify, Steam, Reddit, Vimeo). Each link has a `kind` (video, music, game, post, code, reference, gif, image, shop,
  invite, page) from a table of hosts. The platform's own media links are not links.
- **Sensitive links.** A link with credentials in it, a query parameter that carries a token, code or signature, a long
  opaque value, a path that signs in or acts on an emailed link (reset, verify, invite, …), or a host where the link is
  itself the permission to read a private file (Google Docs, Dropbox, OneDrive, Discord invites, …) is kept **without its
  query**, marked, and never looked up anywhere.
- **Where the description comes from.** First the preview stored with the message (`message_embeds`): no request at all,
  stamped with when the message arrived. Only when a link has none, **and only when `links.unfurl.enabled` is switched on
  (it is off by default)**, the provider's own metadata endpoint is asked: eight fixed hosts (`LinkParser.EndpointHosts`),
  about a public link, never the shared address itself. The fetcher (`HttpLinkFetcher`) allows only https, those hosts,
  default port and no credentials; resolves the host itself and refuses private, loopback and link-local addresses, then
  connects to exactly the address it checked; follows at most two redirects, each through the same checks; sends no
  cookies; reads at most 256 KB in eight seconds. What comes back is cleaned to plain, bounded text and stamped with when
  it was fetched. A provider that fails is tried three times, then left alone. `links.unfurl.per_run` (20) every five minutes.
- **Topics.** Every half hour links with a title are filed under one of twelve topics with a one-line summary
  (`links.categorise`, background). The list has no special category on purpose: what someone shares must not become a
  reading of their politics, religion, health or sexuality, so such links are "news" or "other", and a topic outside the
  list is "other" too.
- **Reads.** `GET links/person` (a person's media diet: kinds, providers, sites, topics, the latest), `GET links/search`
  ("what did X share about Y": words, person, site, kind, topic, time), `GET links/track` (one address in any spelling, or
  a whole site: who shared it first, who at all, where, month by month, and the reactions those messages got). X23 does
  the same from an address, a site or a selected message, inside selected servers, conversations or a time range.
- **Rules.** Bots' links are nobody's. A minor's shares are counted and not named, and their diet is refused. Someone
  whose analysis is off has no shares and none are read. A forgotten person's shares leave the counts and a link only
  they shared is removed. Edits and deletions are followed.
- Not built: an Explore dataset for shares; "summarised" is one line from title and description (the page itself is never read).
  Not exercised: a real provider (the tests use a stand-in that is held to the real fetcher's rules), and real embeds.

## Translation (N19, X33)
Module `translate` (`Modules/Translate`), routes under `translate/` (`Api/TranslateRoutes.cs`), tactic
`x33.translate_selection`, kernel contract `Kernel/Modules/Translations.cs` (`ITranslations.Cached`, additive).

- **Which messages.** The classifier already reads each message's language, leaning on the author's usual language for
  short ones. Messages read as another language than `translate.target` (en) with at least `translate.min_probability`
  (0.7) are queued from the label outbox: what was written to Klives directly and by people he tracks first, then the
  rest, newest first. Not his own messages, not bots', not deleted ones, not someone's whose analysis is off.
- **Budget.** The background job translates at most `translate.auto.per_hour` (300) messages an hour, in batches of
  `translate.batch` (20). A backlog of years therefore takes time; anything can be translated at once on request.
- **Per version.** A translation belongs to one version of a message. After an edit the old one is not served for the new
  text, the new text is queued again by its own reading, and the old row is dropped.
- **What is never translated.** Mentions, channel and role references, custom emoji, links and code are taken out by
  their syntax before the model sees the text and put back afterwards; if the model drops or invents a marker the
  translation is marked `lossy`. The names of the people involved are listed for the model as words to keep.
- **Originals.** Never replaced; every answer carries the original beside the translation and a state (translated, same,
  skipped, failed).
- **Use.** `POST translate/messages` and X33 translate a selection now (up to 200 messages; a conversation's latest 200).
  `GET translate/search` searches the translations (their own FTS index), so a search in Klives's language finds what was
  said in another. Digests read a foreign message with its stored translation beside it (`BriefsModule` takes the
  layer; nothing is translated on a digest's account). `GET translate/status` shows the queue and the hour's use.
- Forgetting a person removes their translations with their messages, out of the search index too (the index follows the
  table by triggers).
- For the merge: WS-B's extraction and search should read `ITranslations.Cached` the way digests do (the general search
  can union `translate/search`). "Multilingual embeddings" is the encoder's property (C-base), not this module's.
- Not exercised: a real model's translations, and real language readings (the tests write the readings and script the model).

## Data controls (N20, X76): two forget levels, retention, backups, export
Module `datacontrols` (`Modules/DataControls`), routes under `data/` (`Api/DataControlsRoutes.cs`).

**Forgetting a person** has the two levels of plan R3. Both start with a preview that changes nothing, says exactly what
would go, and returns a token: valid ten minutes, for that person, that level and that choice about direct chats, usable
once. Klives himself is refused. The confirmation writes the standing mark at once and queues a job that works in batches,
can be resumed after a crash, and reports what it removed by kind. The audit records that a person was forgotten, at which
level and by whom, never who. The job is addressed by a run id; who it concerns is kept only until it is done.

- **`erase`** removes everything: their messages with versions, mentions, embeds, polls, reactions to them and attachments;
  their own reactions and votes; presence, typing, voice and member events; their accounts with names and pictures; their
  person rows and merges; their direct chats with Klives including what Klives wrote there (unless the preview was taken
  with `includeDirectChats: false`); the vectors of those messages; every derived row keyed by one of the messages, by the
  person or by an account, in every table of intel.db whichever module owns it (the sweep reads the schema, so a module
  added later is covered); read models, tactic runs and stream events that name them; and Klives's own records about
  them (tier, notes, corrections, dismissals, replica chats, standing questions, saved views, audit rows; watch rules and
  digest subscriptions lose them from their scope, and are deleted when they were the whole scope, because a rule
  without its scope would watch everybody; cohorts and case files lose them and their messages, and keep the rest).
  A stored file goes when nothing else refers to it. Files in the old database's media folder are never touched and are
  counted as left. Deleted pages are overwritten (`secure_delete`) and the search index is rewritten at the end, so their
  words do not linger. The run then checks that nothing of theirs is left in the corpus.
- **`derived`** keeps the archive and forgets what was worked out: the labels and vectors of their messages, statistics,
  facts, summaries, alerts and reminders, replica chats; watch rules, digests and standing questions aimed at them. They
  drop to the archive tier and are unpinned. Klives's notes, corrections and case files stay. From then on **analysis is
  off** for them (below). It can be switched back on (`POST data/analysis/on`), and everything is built again from the
  archive.
- The change `person.erase` is appended **last**, when nothing of theirs is left to read, so a module that rebuilds on it
  cannot rebuild from rows that are about to go. Briefs, insights, rollups, signals, projections and the mirror follow it.

**Tombstones (`Storage/Forgotten.cs`).** After an erase, operator.db keeps the forgotten accounts (platform and user id),
person ids and direct chats as `dismissals` rows. They are the only way to recognise the person's data when it is offered
again. The old-database importer honours them on every run: a forgotten person, everyone merged into them, their
accounts (also one the old database attaches to them later), their direct chats, messages, edits, deleted-message
archive, attachments, reactions (theirs, and anybody's to their messages), events, and what Klives kept about them are
skipped without a trace. They are not quarantined either, because a quarantined row keeps its text. A message somebody
else wrote that pinged them is imported without the record that it pinged them.

**Analysis off (`Storage/AnalysisOff.cs`).** A standing mark on the person; it covers everyone merged with them, so a
merge cannot switch it back on. While it stands:
- the classifier does not embed, label, sample or train on what they write;
- the rollups build no cell for them, and nobody else's cell counts or names them (pair cells; conversation, server and
  global days);
- signals raise no alert and no reminder from what they write;
- digests leave their messages out, and a brief, digest or subscription about them is refused;
- the tactic runner refuses every tactic with them in its selection (one place, so the tactics of every workstream are
  covered);
- the analytics mirror returns no row that names them, whatever the dataset and however the query is phrased; a message
  somebody else wrote to them stays counted and simply does not name them;
- relationships, communities and wellbeing leave them out; the phrase tracker does not count them;
- the dossier carries `analysisOff: true`, so the page can say so instead of looking like a person nothing is known about;
- the old-database importer keeps importing their messages, but not the tier, pin, watch or replica chats that were removed.

Their messages stay readable and searchable by their words. `GET data/analysis` lists who is off and since when.
Switching it back on appends `person.reanalyse`: the classifier embeds and labels everything they wrote again, the rollups
rebuild their cells and the cells of the conversations, servers and people around them, and the mirror writes their rows
again. Alerts are not raised after the fact for what they wrote while it was off.

**Retention** (`datacontrols/retention`, nightly at 03:30 in Klives's zone): presence, typing and voice events older than
`datacontrols.retention.{presence,typing,voice}_days` are deleted by when they happened (a call by its end, or by its start
when it never ended), in slices. 0 (the default) keeps them for ever. Messages are never subject to it. `GET data/retention`
says per stream how long it is kept, the cutoff, and how many events were removed in total and last night, so the pages can
say how far back a stream reaches (F519: disclosed, never silent). The rollups keep the counts they made before.

**Backups** (`datacontrols/backup`, nightly unless `datacontrols.backup.enabled` is off, or `POST data/backups/run`): a
consistent snapshot of corpus.db and operator.db taken with SQLite's online backup while the service runs, encrypted with
AES-256-GCM in 1 MiB chunks (each chunk is authenticated with its index and whether it is the last, so a cut-off, reordered
or altered file fails to open). The plain copy is deleted. The key is 256 random bits kept only under Windows DPAPI for the
service account. `export-key` wraps it with a passphrase of at least 12 characters (PBKDF2-SHA256, 600,000 rounds) for
another machine; `import-key` installs it there and refuses to replace a different key. `restore` decrypts into a folder
beside the backup and checks the recorded hash and SQLite's integrity check; it never swaps the live files (that is done
by hand with the service stopped). The newest `datacontrols.backup.keep` (7) are kept, so erased data ages out of the
backups; the erase preview says so.

**Export** (`POST data/export/person`, answers with the file): accounts and their names over time, the facts recorded,
and every message they wrote, as one JSON document or as Markdown, streamed in pages.

Routes: `data/forget/preview`, `data/forget/confirm` (202), `data/forget/run`, `data/analysis`, `data/analysis/on`,
`data/retention`, `data/backups`, `data/backups/run`, `data/backups/restore`, `data/backups/export-key`,
`data/backups/import-key`, `data/export/person`. Retention needs the overview permission and the export the data-export
permission; everything else needs engine management.

**For the merge:**
- WS-A's ingest writer (gateway, backfill, export imports) must ask `Forgotten.Load(...).Blocks(identity, conversation)`
  before it stores anything, and skip a forgotten account's events. Its face labels, crops, thumbnails, OCR and caption
  rows are swept when their tables key by `message_id`, `person_id` or `identity_id`; files it stores outside the CAS need
  its own handler on `person.erase`. Faces and captions of an analysis-off person are not computed.
- WS-B's extraction, inquiry and standing questions, and WS-D's replica training, must skip the people in
  `AnalysisOff.Load(...)` in their background jobs (tactics are refused centrally). An assertion about somebody else that
  loses its last evidence message to an erase should be retracted by WS-B.
- KliveRAG's Omniscience connector must drop what it indexed about a person on `person.erase`.
- A route that sets a person's tier must not be read as switching analysis back on: the mark is what counts, not the tier.
- `IPolicyOracle` is unchanged (WS-B wraps it).

Tests: `DataControlsTests` (6: an erase across all stores, with a scan proving that none of their words and no account id
is left anywhere but the block list; the lighter level, what stays off, and switching back on; the mirror; retention;
backups with damage and key transfer; export) and two more in `LegacyImporterTests` (forgotten people on a snapshot run
and on the tail; analysis off). What the tests found and this fixed: conversation and server statistics kept naming a
forgotten person (the rollups only looked at the person's own cells, which the sweep had already removed), the
communities summary kept their name until the next night, and module state rows keyed by an account were left. Not
exercised: a real corpus's size (the erase works in batches of 2,000 messages; the final rewrite of the search index
holds the corpus writer for its duration, seconds on a few million messages).

## Shared files touched (small, additive)
- **Connection pools.** v3 no longer calls `SqliteConnection.ClearAllPools()` anywhere (it did in `LegacyDatabase.SnapshotAsync`
  and `LegacyImportModule`, for connections that were unpooled anyway): that call closes the pooled connections of every
  SQLite database in the process, the other services' included, and can close one at the moment it is handed out.
  `SqliteStore.OpenRead` now takes another connection when that happens to it (other services still clear all pools when
  they shut down). The v3 tests no longer clear all pools either; a store clears its own pool when it is disposed. Found as
  a rare test failure (`ObjectDisposedException` in a rollup drain while another test class was tearing down).
- Links and translation: `Kernel/Modules/Translations.cs` (new, additive); one line each in `Api/V3Routes.cs`; in
  `OmniscienceV3.BuildModules()` the links and translation modules join the list and `BriefsModule` is added after it
  (it is handed the translation layer).
- Data controls: `Storage/Forgotten.cs` and `Storage/AnalysisOff.cs` (new); `Storage/ChangeLog.cs` gains the change kind
  `person.reanalyse`; `Storage/Legacy/LegacyImporter*.cs` honour the tombstones and analysis off (and a quarantined
  message now also names its author as v3 knows the account); `Kernel/Tactics/TacticRunner.cs` refuses a tactic on an
  analysis-off person; `DossierDto` gains `AnalysisOff` (optional, last). One line each in `Api/V3Routes.cs` and
  `OmniscienceV3.BuildModules()`. The test harness (`SignalsHarness`) now keeps v3 in a `v3` folder of its own home, so
  the old database's media folder is inside the test's folder instead of the system temp folder.
- `Kernel/Modules/Notices.cs`: `Notice`, `NoticeSeverity`, `INoticeSink` (additive). Insights: one line in `Api/V3Routes.cs`; in `OmniscienceV3.BuildModules()` the signals module is built first and handed to `InsightsModule`.
- Transforms: one line each in `Api/V3Routes.cs` (`TransformsRoutes`) and `OmniscienceV3.BuildModules()` (`TransformsModule`, before Explore).
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
| Relationship dynamics and communities (N12), notices, X36, X45 | done, tested (Leiden on planted graphs; the relationship job on written pair rollups, so the conflict labels it reads are the test's) |
| Wellbeing check-ins (N14, X44) | done, tested (the rollups and labels it reads are written by the test) |
| Data controls (N20, X76): erase and "forget what was worked out" with analysis off, tombstones honoured by the importer, retention, encrypted backups with restore and key transfer, per-person export | done, tested (6 tests, 2 importer tests; real sizes and WS-A's ingest hook are not exercised) |
| Community pages of servers and group chats (N13) | done, tested (2 tests on a seeded server; real volumes not exercised) |
| Similar people and their map (N16) | done, tested (layout maths on planted clusters; similarity and map on 18 and 60 planted people) |
| Link intelligence (N15, X23) | done, tested (5 tests: parser rules, fetch guards, collection, opt-in lookups, topics, tracker, forgetting; no real provider) |
| Translation (N19, X33) | done, tested (4 tests with a scripted model: queue, budget, per-version, shielding, selection, digests) |
| Transforms X20, X21, X22, X28, X29, X34, X37, X38, X39, X40 | done, tested (8 tests; statistics checked against textbook values) |
| Tactics X17 (merge step), X32, X35; monitors X42, X43, X46–X48, X50–X51 | pending |
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
