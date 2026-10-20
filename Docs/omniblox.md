# OmniBlox

An autonomous Roblox game studio. It researches what to build, builds the game with a language-model agent that places real free models from a labeled asset library, tests it, publishes it, makes its own marketing content, monetizes it, measures it and maintains it, then moves on to the next game. The product plan is [omniblox-plan.md](omniblox-plan.md); this document is the technical design and setup guide.

**Hard constraints** (from the plan): OmniBlox spends no money — no paid ads, tools, APIs or assets — and its only model provider is AIRouter (flat fee). It makes all of its own marketing content.

## Architecture

OmniBlox is one `OmniService` (`Omnipotent/Services/OmniBlox`). It talks to the language model through KliveLLM tool sessions, never through a second agent runtime.

| Folder | What lives there |
|---|---|
| `OmniBlox.cs`, `OmniBloxRoutes.cs`, `OmniBloxPerms.cs` | Service shell, `/omniblox/*` HTTP API, permissions |
| `Core/` | Models (game dossier, stages, owner tasks, events), the JSON store |
| `Rbx/` | Roblox file formats: instance tree, binary `.rbxm` reader (LZ4 + Zstd chunks), XML reader, XML `.rbxlx` writer |
| `Roblox/` | HTTP clients: public endpoints (charts, game stats, thumbnails, Creator Store search) and Open Cloud (publishing, places, universes, game passes, developer products, asset delivery, asset upload, Luau execution, analytics) |
| `Assets/` | Asset Library: SQLite + FTS5 index, harvester loop, analyzer (sanitize + measure), schematic renderer, vision labeler |
| `Build/` | Game workspace on disk, scene document, place compiler, Luau syntax checker, geometry verifier, scene renderer, Game Kit loader |
| `GameKit/` | The Luau modules every game starts from (embedded resources): data saving, shop and receipts, currency, rewards, leaderboard, feedback, analytics, world tags, templates, in-engine test runner |
| `Agents/` | Work-cycle runner on KliveLLM tool sessions, tool registry, role prompts, the KliveLLM bridge (model gate) |
| `Pipeline/` | Orchestrator (stage machine, production queue, maintenance schedule, watchdog), stage gates, briefings, test stage, launch sequence, live ops, each role's tools, settings |
| `Marketing/` | Icon / thumbnail / trailer composition, compliance checks |
| `Insights/` | Analytics sync, telemetry store, Playbook (lessons learned) |

### Lifecycle

Each game moves through `Research → Build ⇄ Test → Launch → Live → Sunset`. The orchestrator advances a stage only when its exit criteria hold (see `Pipeline/StageGates.cs`), keeps one game in production at a time (configurable), and maintains every live game: every 3 days for its first two weeks, then every `OmniBlox_MaintenanceIntervalDays` (weekly by default), sooner when telemetry shows an error spike or a game goes quiet after an update. Post-mortems run at 7 and 30 days live and feed the Playbook. Every transition is persisted, so a restart resumes exactly where the game was.

### The agent brain

Work happens in **cycles**. A cycle is one KliveLLM tool session with:

1. a role system prompt and tool list that never change between cycles (cache-stable prefix),
2. a briefing built from the game dossier (mission, stage, exit criteria, milestone checklist, recent progress reports, relevant Playbook lessons),
3. an append-only tool loop with a turn and token budget.

The cycle ends when the agent calls `finish_cycle` (or the budget runs out, in which case the runner asks for the report). The structured report — done, decisions, problems, next steps, key facts kept verbatim — is written back to the dossier. That rollover is the compaction: nothing important lives only in a context window. A watchdog counts repeated failures per stage and forces a different approach, then escalates to the owner.

### AIRouter rules

- The model gate (`KliveLlmBridge.CheckAvailableAsync`) refuses to run any model work unless KliveLLM is up and `KliveLLM.IsFlatFeeProviderAsync()` is true.
- Pipeline cycles use `AIRouterWorkClass.Agent`; one-shot calls (asset descriptions, compliance reviews) use `OneShot`. Interactive work always outranks both.
- The asset labeler only dispatches when the shared AIRouter queue is idle (nobody waiting, at most one request in flight).
- Prompts follow `KliveLLM/PromptPrefixStability.cs`: stable text first, volatile text last, history append-only within a cycle.

### Prompt cache

AIRouter serves a request from its prefix cache only for the run of bytes it shares with an earlier request, counted from the first byte. One changed byte early in a prompt makes everything after it a miss. The dashboard's "cache hits" figure is the share of prompt tokens served from that cache, and AIRouter judges keys by it.

**Agent cycles** re-send their transcript every turn, so nearly all of each request can be served from cache:

- One tool list for the whole cycle, the wrap-up call included. The tool schemas are rendered at the head of the prompt. The old wrap-up offered only `finish_cycle`, which made its call re-prefill the entire cycle (up to ~70K tokens). A call to another tool at that point is answered "not run" instead of being executed.
- The briefing is appended without KliveLLM's receipt stamp, since it closes with its own `Now:` line. Its sections run from rarest change to most frequent: brief, lessons, milestones, facts, decisions, hand-overs, state. The next cycle's briefing can then reuse everything above the first fact the last cycle added.
- Each cycle report counts the tokens a turn re-sent (the previous request, an exact prefix of the next) and how many the cache served. The gap is waste: a prefix that expired during a long tool call or queue wait.

Expected: re-sent prompt ~100% from cache. The raw hit rate is ~90–96%, because each turn's new tool output and pictures cannot be cached before they are first sent.

**The asset labeller** makes one-shot vision calls whose pictures are new every time. Before this design, only the ~520-token system prompt was shared, which put the hit rate near 26%. Now:

- The four schematic views are sent as one 2×2 sheet (768 px) rather than four 512 px pictures. That is about 36% fewer new tokens per call.
- Every call opens with a frozen set of four **reference examples**: earlier models with their labels, chosen at calibrated scores of about 9, 2, 7 and 4 (see below). They are byte-identical in every call, so the provider caches them once. Measured on real prompts (`PromptCacheTests.AWarmLabelCall_IsMostlyItsSharedHead`), a warm call is ~83% cached.
- The references cost about four models' worth of prefill whenever the cache has expired. So they are sent only while still warm (within `KliveLLM.EstimatedPrefixLifetime()`), or when at least three models are waiting. The harvester labels up to six per step, back to back while AIRouter stays idle.
- The set is frozen in `studio.json` (`LabelerReference`) with copies of its pictures under `labeler-reference/v{n}/`. It is rebuilt only for a new `AssetLabeler.PromptVersion` or a missing file. **Bump `PromptVersion` whenever the labeller's system prompt, request text or answer format changes.**

**Quality calibration.** The first labels each judged one model alone against a one-line scale, and nearly everything came out at 6 or 7. A model with nothing to compare against lands in the safe middle. Three changes give it a consistent context:

- **Calibration.** When the reference set is built (once the library has at least six labels), one call shows ~12 models side by side as thumbnails and scores them against each other. The pool alternates the most and least promising by likes, votes and verified creator, one per category where possible. The four anchors are the models nearest 9, 2, 7 and 4 on that scale. They carry their calibrated score and the issues that set it. The pool's new scores are written back to the library.
- **Rubric.** The labeller's prompt now has a six-band scale, with a description of each band. It also tells the model to place each model by comparing it with the anchors.
- **Re-rating.** Labels made on the old scale (`label_version` 0) are re-rated against the anchors when nothing new is waiting, most-used models first. A re-rate that fails leaves the old label in place. `AssetLabeler.ScaleVersion` marks the scale. Bump it only when scores stop being comparable with earlier ones, because that re-rates the whole library.

A literal 100% hit rate is not reachable, because every call carries something new. What should read ~100% is the "Re-sent prompt" row: the share of already-sent tokens the cache served.

### Roblox access

| Capability | Endpoint | Auth |
|---|---|---|
| Free-model search | `GET apis.roblox.com/toolbox-service/v2/assets:search` | none |
| Asset thumbnails | `GET thumbnails.roblox.com/v1/assets` | none |
| Charts | `GET apis.roblox.com/explore-api/v1/get-sorts` | none |
| Game stats / votes | `GET games.roblox.com/v1/games`, `/v1/games/votes` | none |
| Model download | `GET apis.roblox.com/asset-delivery-api/v1/assetId/{id}` | key: `legacy-asset:manage` |
| Publish a place | `POST apis.roblox.com/universes/v1/{u}/places/{p}/versions?versionType=Saved|Published` | key: `universe-places:write` |
| Name / description | `PATCH apis.roblox.com/cloud/v2/universes/{u}/places/{p}` | key: `universe.place:write` |
| Visibility, social links, platforms | `PATCH apis.roblox.com/cloud/v2/universes/{u}` | key: `universe:write` |
| Game passes | `apis.roblox.com/game-passes/v1/universes/{u}/game-passes` | key: `game-pass:read/write` |
| Developer products | `apis.roblox.com/developer-products/v2/universes/{u}/developer-products` | key: `developer-product:read/write` |
| Engine tests | `POST apis.roblox.com/cloud/v2/universes/{u}/places/{p}/versions/{v}/luau-execution-session-tasks` | key: `universe.place.luau-execution-session:write` |
| Analytics | `POST apis.roblox.com/analytics-query-api/v1/universes/{u}/metrics` | key: `universe.analytics:read` |
| Homepage thumbnails | `apis.roblox.com/thumbnail-personalization-api/v1/universes/{u}/thumbnails/uploads` (experimental) | key: `universe.thumbnail:write` |

Two things have no API: **creating an experience** and **setting the experience icon** (and the standard store thumbnails). OmniBlox handles them as owner tasks on the dashboard: you create blank experiences once (they become "experience slots" the bot fills), and the icon is delivered ready to upload with one click.

Places are published as XML (`.rbxlx`). The publishing API cannot carry unions (`PartOperation`), `SurfaceAppearance`, `EditableImage`, `EditableMesh` or `BaseWrap`, so the Asset Library marks models containing them as not placeable.

## Setup

1. **API key.** In the Creator Dashboard → Open Cloud → API Keys, create a key with the experiences you will give OmniBlox and these permissions: `universe-places` (write), `universe` (write), `universe.place` (write), `universe.place.luau-execution-session` (read, write), `game-pass` (read, write), `developer-product` (read, write), `universe-analytics` (read), `legacy-asset` (manage), `universe.thumbnail` (write). Put it in the OmniSetting `OmniBlox_RobloxApiKey`.
2. **Creator.** Set `OmniBlox_CreatorUserId` (or `OmniBlox_CreatorGroupId` if you publish under a group).
3. **Experience slots.** Create a few blank experiences in the Creator Dashboard and add each as `universeId:placeId` to `OmniBlox_ExperienceSlots`.
4. **AIRouter.** OmniBlox only runs model work while `RemoteLLMProvider` is `AIRouter`.
5. Open **Schemery → OmniBlox** on the KM site. Switch the **harvester** on first so the Asset Library fills up, then switch the **studio** on.
6. **Playtest experience** (optional). To play any game in Roblox at any stage, live ones included, create one more blank experience. Give the key access to it and add it as `universeId:placeId` to `OmniBlox_PlaytestSlot` (see [Playtests](#playtests-playing-a-game-at-any-stage)). Without it, unlaunched games play in their own private experience, and Open in Studio needs nothing at all.

Without a key OmniBlox still researches, harvests asset metadata and thumbnails, and builds and checks games offline; publishing, engine tests, model downloads and analytics wait for the key (the dashboard shows an owner task saying so).

### Playtests: playing a game at any stage

The owner can jump into any game from Build on, while it is being built, being tested, waiting to launch or live. On the dashboard, use **▶ Play** on a game card, in the game's header or on its World tab. The Play panel offers:

- **Which build.** "Newest" is the files as they are right now, mid-cycle included. If they do not compile at that moment, the last clean build plays instead, and the panel says so. Any earlier clean revision (`r7`) can be played exactly as it was built, from its snapshot.
- **▶ Play in Roblox.** OmniBlox compiles the build, publishes it to a private experience and opens `roblox.com/games/start?placeId=…`. That link launches the Roblox app, desktop or phone. Running servers are asked to move onto the new version (`universes/{id}:restartServers`), so pressing Play again while you are in the game brings you the new build. Pressing it for a build that is already there publishes nothing, so rejoining never restarts your server. "Start with fresh saves" starts the playtest's progress over.
- **⤓ Open in Roblox Studio.** The same build as a `.rbxlx` file: open it and press Play (F5). This needs no API key and no experience.
- **Follow the build.** While you are in the playtest (its server reported a player in the last 3 minutes), each new clean build the agents make is published to it and your server is moved onto it. You watch the game change as it is built. At most one push every 2 minutes; the newest build wins.

**Where it plays.**
- The studio's playtest experience, set with `OmniBlox_PlaytestSlot`, works for every game at every stage. It holds one game at a time, and playing another replaces it.
- Without one, a game that has not launched plays in its own experience slot. That experience is private until launch, and a free slot is set aside on the first play, as Test would.
- A public game never gets a playtest in its own experience.
- Setup: create one more blank experience (it stays private), give the API key `universe-places` and `universe` write access to it, and add it as `universeId:placeId`.

**A playtest build is safe to play.** It is the real game with the Game Kit's playtest mode on (`Config.Playtest`):
- **Purchases.** Buying anything grants it at once as a free test purchase. Roblox is never asked to charge, and pass ownership comes from the playtest's own saves.
- **Saves.** Saves go to a DataStore of their own (`OmniPlaytest_<game>_<n>`), never the game's. In Studio, where a place file has no DataStores, saves stay in memory. The leaderboard follows the same rules.
- **Telemetry.** It goes to `/omniblox/telemetry` with a separate per-game playtest key, so it never touches the game's real numbers. It lands in the dossier's playtest log: one session per server, with how far you got, deaths, currency earned, gameplay events, test purchases, your in-game feedback and script errors.
- **HUD.** The HUD shows a `PLAYTEST · r12` badge.

**What playtests feed back.** The dashboard lists your recent playtests. The newest one from the last 3 days goes into the agents' briefing ("Current state"). A playtest is often the first time the game's scripts run with a real player and physics, which engine specs cannot cover. So its script errors are listed there word for word, for the builder to fix.

### What only the owner does

The dashboard's **Owner tasks** list is everything OmniBlox cannot or must not do itself: add the API key, add experience slots, approve a launch (while `OmniBlox_RequireLaunchApproval` is on), upload the generated icon, answer the experience questionnaire, and look at escalations from the watchdog. Each task carries what is needed (the icon file, the questionnaire answers it prepared). Money never moves: no ads, no purchases, and cashing out Robux stays with you.

## Settings

| Setting | Default | Meaning |
|---|---|---|
| `OmniBlox_Enabled` | `false` | Master switch for the production pipeline (research through maintenance) |
| `OmniBlox_HarvesterEnabled` | `false` | Background asset harvesting; independent of the master switch so the library can fill before the first game |
| `OmniBlox_RobloxApiKey` | — | Open Cloud API key (sensitive) |
| `OmniBlox_CreatorUserId` / `OmniBlox_CreatorGroupId` | — | Who owns uploaded assets |
| `OmniBlox_ExperienceSlots` | — | `universeId:placeId` entries the bot may fill |
| `OmniBlox_PlaytestSlot` | — | `universeId:placeId` of a private experience for the owner's playtests of any game at any stage (never used as an experience slot) |
| `OmniBlox_RequireLaunchApproval` | `true` | Owner approves each game before it goes public |
| `OmniBlox_MaxGamesInProduction` | `1` | Games in production at once (1–3) |
| `OmniBlox_MaintenanceIntervalDays` | `7` | Maintenance interval once a game is past its first two weeks |
| `OmniBlox_TelemetryBaseUrl` | `https://klive.dev` | Where games send anonymous gameplay telemetry (`/omniblox/telemetry`) |
| `OmniBlox_DevExRate` | `0.0038` | USD per Robux, only for the dashboard's cash estimate |

## HTTP API

All under `/omniblox/`, permission group `omniblox`. Reads: `overview`, `games`, `game?id=` (includes `Playtest`: whether and where the game can be played), `game/media`, `game/render`, `game/files`, `game/file`, `game/place?id=&build=` (a playtest build as a place file; the build it is comes back in `X-OmniBlox-Build`, a fallback reason URL-encoded in `X-OmniBlox-Note`), `game/telemetry`, `assets`, `asset`, `asset/image`, `events`, `playbook`, `tasks`. Actions: `tasks/resolve` (not launch approvals), `games/new`, `games/pause`, `games/steer`, `games/maintain`, `games/playtest` (`{gameId, build, fresh}` → the join link), `games/playtest/follow` (`{gameId, follow}`); owner-level (`studio.manage`): `games/approve`, `games/abandon`, `studio/toggle`. `POST /omniblox/telemetry` is the public endpoint the games call, authenticated by a per-game secret (or, for playtest builds, the game's playtest key, which routes the batch to its playtest log).

The JSON bodies are built in `OmniBloxViews.cs`, shared by the routes and the website's fixture export.

## Dashboard

KM site → **Schemery → OmniBlox** (`pages/schemery/omniblox`, components in `components/OmniBlox`, types and helpers in `composables/useOmniBlox.ts`):

- **Studio**: owner tasks with their actions and file downloads, readiness banners, the studio and harvester switches, "New game" with an optional theme, KPIs (live games, the game in production, revenue with the DevEx estimate and "spent $0", players, model calls and cache hit rate, labelled models), every game as a card with its stage track, players and revenue across live games, model usage, the harvester, and the activity log.
- **Play** (`?play=1` on a game, linked from every playable card and the studio header): build picker, Play in Roblox, Open in Studio, Follow, and the recent playtests with their errors and feedback.
- **Game** (`/game/[id]`): stage track with the current exit checklist; tabs for the brief, milestones, last test, key facts, decisions and scored concepts; the world (3/4, above and spawn renders) with build problems and placed models; the store page, art, products and publishing record; numbers (Roblox analytics, telemetry sessions, funnel, feedback, purchases, script errors, maintenance and post-mortems); every cycle's hand-over; the workspace files; and the game's events. Pause, steer, maintain now, approve launch and abandon live in the header.
- **Assets**: the library by status or by search, with a drawer showing the labeller's label, the measurements, what was stripped and where the model came from.
- **Playbook**: every lesson with its outcome, confidence and evidence.

## Testing

`Omnipotent.Tests/OmniBlox` covers the file formats (against the Roblox files installed on the machine), the Luau checker (against every script Roblox ships), the compiler, verifier and renderers, compliance, the Roblox clients, the cycle runner, and the whole pipeline end to end against a scripted model and a fake Roblox (harvest → research → build → test → launch → maintenance → post-mortem, plus the no-key, approval, failed-review and watchdog paths). Set `OMNIBLOX_RENDER_OUT` to a folder to save the rendered images for a look.

The dashboard's e2e spec (`tests/e2e/omniblox.spec.ts` on the KM site) runs against fixtures that `OmniBloxFixtureExport` writes from a real run of the studio. Regenerate them after any change to a route's shape:

```bash
OMNIBLOX_FIXTURES=<site>/tests/e2e/fixtures/omniblox DOTNET_ROLL_FORWARD=Major dotnet test Omnipotent.Tests --filter FullyQualifiedName~OmniBloxFixtureExport
```
