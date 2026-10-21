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
2. a briefing built from the game dossier: the owner's open directions and the watchdog's note first, then the goal, the brief, the game's own `design.md`, Playbook lessons, milestones, kept facts, decisions, the last hand-overs, the current state (build, strict types, map score, the last engine run, the last test) and the exit criteria,
3. an append-only tool loop.

**There is no cycle or turn budget.** A stage takes as many cycles as it needs. A cycle ends when the agent calls `finish_cycle`; when its context fills (`OmniBlox_CycleContextTokens`) the runner asks it to finish the step it is on and hand over, and the next cycle continues from that report. A turn cap of 300 remains only as a runaway guard. The structured report is written back to the dossier: summary, decisions, problems, next steps, key facts as `{key, fact}` pairs, `drop_facts`, and `directives_done`. That rollover is the compaction: nothing important lives only in a context window.

- **Kept facts** (`Core/FactBook.cs`). A fact with a key replaces the older fact with that key; an unkeyed fact that restates an older one replaces it; the owner's and the studio's facts are never pushed out by an agent's; when the book is full the oldest agent fact goes first. Old dossiers with plain-string facts still load (`KeyFactListConverter`, which also recognises the old "Owner's direction (date): …" format as the owner's).
- **Cut-off replies.** Replies may run to `OmniBlox_MaxOutputTokens` (16K) with reasoning on. A tool call cut off by the output limit (`finish_reason: length`) is never run: the agent is told to send long files as `write_file` + `append_file`. The first studio ran cut-off `write_file` calls and truncated the ends of files.
- **The owner's directions** (`Directives`). Steering creates a direction. A running cycle receives it at its next turn; it leads every briefing until an agent reports it in `directives_done` (or the owner drops it).
- **The watchdog** (`Pipeline/Watchdog.cs`) measures progress, not cycle counts. A cycle that changes the game's files, checklist, milestones or store page is progress; the same failure again with nothing changed is a stall. Stalls climb a ladder: 3 → *rethink* (told plainly the approach is not working, higher reasoning effort), 5 → *deep dive* (prove the cause with `diagnose_failure` before changing anything), 8 → the game **pauses and asks the owner** (an owner task and an alert). Resuming or steering resets the ladder. **Nothing is abandoned automatically**; the owner can abandon a game and revive it later where it stopped.
- **Provider failures** (AIRouter down, queue full) back off exponentially up to 30 minutes and never count against the game. A test run that learned nothing (Roblox or the model unreachable) is marked `Infrastructure` and retried.

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

### Building a game

The builder works milestone by milestone: design (`design.md`: the core loop, the first minute, progression and economy numbers, the map plan, the systems and their specs), the world, the core loop, every must-have, polish. Its tools (`Pipeline/Tools/`):

- **Files** (`FileTools.cs`): `list_files`, `read_file` (including the Game Kit's own source under `GameKit/`), `write_file`, `append_file`, `edit_file`, `delete_file`, `search_code`. Every write is syntax- and type-checked on the spot.
- **World** (`WorldTools.cs`): `terrain_generate` (a deterministic heightmap: flat core, hills, border mountains, biome materials), `terrain_sculpt` (flatten, raise, lake, river, path, paint), `set_map_area` (the play area: invisible walls and a void rescue), `plan_map` (zones, paths, landmarks), `add_objects`, `place_asset`, `scatter_assets` (Poisson-disk, avoiding paths, water, slopes and objects), `place_along`, `place_ring`, `set_folder`, `set_lighting` (sunny, golden, night, spooky, tropical, snowy, candy, overcast), `ground_height`, `scene_summary`. `[x, z]` positions stand anything on the ground.
- **Library and sounds**: `search_assets`, `browse_assets` (up to 12 models side by side as one picture), `view_asset`, `search_sounds` (only Roblox's own and APM's uploads, which are licensed for every game; ids it returns are recorded and `update_manifest` refuses any other audio id).
- **Seeing it**: `render_scene` (views below), `map_report`.
- **Checking it**: `build`, `run_tests`, `diagnose_failure` (the failing check's own source, the functions it calls, the code around an error's line), `pacing_check` (minutes per step against what keeps kids playing), `kit_reference` (the kit's API with exact signatures, by topic).
- **Progress**: `set_milestones`, `update_milestone`, `mark_feature_complete` — accepted only when the current files built clean, the map scores at least 60 with no blocking problems (obbies are judged on their course), every milestone is done and, where the engine can run, `run_tests` passed on this build.

**`build`** compiles the place and checks every script's syntax, then its **strict types** with [luau-lsp](https://github.com/JohnnyMorganz/luau-lsp) `analyze` (`Build/LuauAnalyzer.cs`): calling a method a class does not have, a kit function that does not exist, an unknown global, the wrong number of arguments. luau-lsp 1.70.1 and Roblox's type definitions are downloaded on first use into the studio's `tools/` folder and verified against pinned SHA-256 hashes; without them a build says "not type-checked". Then the geometry verifier and the map report. A clean build of files that have not changed since the last clean revision (on the same Game Kit) keeps that revision, so a passing engine run on it still counts.

**`run_tests`** runs the game in Roblox's real engine (`Pipeline/EngineTests.cs`): it compiles the engine-test flavour of the place (Luau execution loads a place without running its scripts, so each server Script becomes a ModuleScript returning its body as a function, tagged `OmniTestScript`), saves it as a new unpublished version, and runs the kit's `TestRunner` there. Phases: **kit** (the kit checks itself before any game code loads — a failure there is the studio's bug: the owner gets a task and the game is not blamed), **load** (every module loads), **boot** (the whole server starts — kit, the game's systems and scripts — and runs for 8 s; every error comes back with the game's file and line), **world** (spawn, products, quests, gates, the template's loop), **game** (the game's own specs in `src/tests`). There are no players or physics in the engine, so game logic lives in systems whose functions the specs call with `t.player()` stand-ins. The first studio's three games all died on a kit test bug (every test reused player id -1001, so profiles carried over between tests); the phases and the kit control check exist so that can never again be blamed on a game.

### The Game Kit

Every game starts from `GameKit/` (embedded Luau, strict-typed and checked clean by luau-lsp in the tests): saving (`Data`), currencies with extra currencies and multipliers (`Currency`), the shop and receipts (`Shop`), daily rewards, the leaderboard, feedback, analytics, effects (bursts, floating numbers, sounds, toasts), zones behind gates (`Progression`, tag `ZoneGate`), rebirth, quests with a glowing guide line, the world (terrain from the heightmap, map edges, void rescue, tags such as `KillBrick`, `Coin`, `Bounce`, `Spin`, `Bob`, `Glow`, `Sparkle`), the three templates (obby, tycoon, simulator), a phone-first HUD (animated counters, a side menu, modals, toasts, a welcome card, a quest tracker) and the test runner. A game's own code is systems (`src/server/systems/<Name>.luau`, started by the kit's Boot) and client controllers (`src/client/controllers/<Name>.luau`, given the HUD's API).

### Seeing the world

`Build/WorldRenderer.cs` draws a compiled world in software: the terrain with smooth light and biome colours, water, parts with their shapes, materials and sun shadows, every library model as Roblox's own picture of it standing at its real size, the sky and grading of the lighting preset, and distance haze that starts behind the subject. Views: `map` (from above with the plan: zones, paths, landmarks, the play area, a grid in studs, where the spawn faces, labels that group big sets such as "Pine Tree ×70"), `overview`, `aerial`, `iso`, `spawn` (what a player sees on joining), `zone:<id>` (seen from the map's middle, outwards), an object id, or a camera `"x,y,z>x,y,z"`. Pictures the agents look at during a cycle are kept with the cycle's log.

`Build/MapReport.cs` scores the map out of 100: whether it has edges and ground, the play area's size (140 studs a side at least; 300-600 typical), how much of it is dressed and where the largest empty patch is (with coordinates), each planned zone's density, paths, variety, landmarks, and what the spawn faces. It is the builder's written view of its map and the Build stage's bar (60; aim for 80+).

**Store art** (`Marketing/MarketingStudio.cs`) is made from the real world rendered in the vivid store look: saturated colour, sunburst rays, chunky yellow-to-orange lettering with a thick outline, die-cut stickers of models that are really in the game (a picture on a plain background is keyed out), sparkles and a starburst badge. The accent colour defaults to the game's theme, made vivid. Trailers are rendered through the same renderer.

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

**The launch sequence** (`Pipeline/LaunchSequence.cs`) runs nothing irreversible until the game has run clean in the engine: key → slot → compliance → final build → engine tests (skipped when a passing run already covers these files) → an earlier abandoned game's products in the same experience are taken off sale → products → the publish build (the same files, with the product ids) → store page → thumbnails → owner tasks → approval → publish → public.

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
| `OmniBlox_MaxOutputTokens` | `16384` | Output limit per model reply (4096–65536); a tool call cut off by it is not run |
| `OmniBlox_CycleContextTokens` | `120000` | Prompt size at which a cycle hands over to the next one (40000–400000) |
| `OmniBlox_TelemetryBaseUrl` | `https://klive.dev` | Where games send anonymous gameplay telemetry (`/omniblox/telemetry`) |
| `OmniBlox_DevExRate` | `0.0038` | USD per Robux, only for the dashboard's cash estimate |

## HTTP API

All under `/omniblox/`, permission group `omniblox`. Reads: `overview`, `games`, `game?id=` (includes `Playtest`, `Live` — the running or last cycle step by step, `CycleLogs`, `EngineAvailable` and `Watchdog`), `game/live?id=` (just the live cycle, for close polling), `game/cycle?id=&cycle=` (one past cycle's full log), `game/cycle/image?id=&cycle=&file=` (a picture from it), `game/map?id=` (the map report on the current files), `game/media`, `game/render?id=&view=&labels=&marketing=&w=&h=`, `game/files`, `game/file`, `game/place?id=&build=` (a playtest build as a place file; the build it is comes back in `X-OmniBlox-Build`, a fallback reason URL-encoded in `X-OmniBlox-Note`), `game/telemetry`, `assets`, `asset`, `asset/image`, `events`, `playbook`, `tasks`. Actions (`studio.act`): `tasks/resolve` (not launch approvals), `games/new`, `games/pause`, `games/resume` (also after a watchdog pause; the ladder starts over), `games/steer` (`{gameId, note}` → a direction, up to 2000 characters; steering a game the watchdog paused resumes it), `games/directive` (`{gameId, directiveId, action: drop|reopen}`), `games/build` (builds now and answers with the report), `games/test` (runs the engine tests in the background; the result lands in `Build.LastEngineRun`), `games/maintain`, `games/playtest` (`{gameId, build, fresh}` → the join link), `games/playtest/follow` (`{gameId, follow}`); owner-level (`studio.manage`): `games/approve`, `games/abandon`, `games/revive` (back to the stage it was abandoned in; Test and Launch go back to Build), `studio/toggle`. `POST /omniblox/telemetry` is the public endpoint the games call, authenticated by a per-game secret (or, for playtest builds, the game's playtest key, which routes the batch to its playtest log).

The JSON bodies are built in `OmniBloxViews.cs`, shared by the routes and the website's fixture export.

## Dashboard

KM site → **Schemery → OmniBlox** (`pages/schemery/omniblox`, components in `components/OmniBlox`, types and helpers in `composables/useOmniBlox.ts`):

- **Studio**: owner tasks with their actions and file downloads, readiness banners, the studio and harvester switches, "New game" with an optional theme, KPIs (live games, the game in production, revenue with the DevEx estimate and "spent $0", players, model calls and cache hit rate, labelled models), every game as a card with its stage track, players and revenue across live games, model usage, the harvester, and the activity log.
- **Play** (`?play=1` on a game, linked from every playable card and the studio header): build picker, Play in Roblox, Open in Studio, Follow, and the recent playtests with their errors and feedback.
- **Game** (`/game/[id]`): stage track with the current exit checklist; the watchdog's state when it is stepping in (with Resume when it paused the game); **Working now** — the running cycle step by step, polled every 4 s: what the agent says, every tool call and its result, the pictures it looked at; **Your directions** — send one, see which are open, delivered or done (with what was done), drop or reopen them. Tabs: the brief, milestones, last test, key facts (keyed, with the owner's marked), decisions and scored concepts; **World** (map with the plan, aerial, 3/4, spawn and each zone; the map report with its score, findings and measurements; the map plan; build problems; placed models); **Tests** (each check on the current files — compiles, strict types, geometry, map, Roblox's engine — with Build now and Run in Roblox, the last engine run's failing checks, runtime errors with file and line, warnings and passed checks, and the Test stage's report); the store page, art, products and publishing record; numbers; **Brain** (every hand-over with its tool calls and cut-offs, and every step of the recent cycles); the workspace files; and the game's events. Pause/resume, steer, maintain now, approve launch, abandon and revive live in the header.
- **Assets**: the library by status or by search, with a drawer showing the labeller's label, the measurements, what was stripped and where the model came from.
- **Playbook**: every lesson with its outcome, confidence and evidence.

## Testing

`Omnipotent.Tests/OmniBlox` covers the file formats (against the Roblox files installed on the machine), the Luau checker (against every script Roblox ships), the compiler (both flavours), verifier, terrain, map report and renderers, compliance, the Roblox clients, the cycle runner (hand-overs, cut-off replies, owner directions mid-cycle), the fact book and the watchdog, the world tools end to end (`WorldToolTests`: a whole map built through the agents' own tools, the map report, every view, store art), and the whole pipeline end to end against a scripted model and a fake Roblox (harvest → research → build → test → launch → maintenance → post-mortem, plus the no-key, approval, failed-review and watchdog-pause paths). Set `OMNIBLOX_RENDER_OUT` to a folder to save the rendered images and store art for a look.

The strict type check runs against the real luau-lsp when `OMNIBLOX_LUAU_LSP` names its executable and `OMNIBLOX_LUAU_DEFS` Roblox's definitions (`globalTypes.None.d.luau`): the whole Game Kit must type-check clean for every template, and a game's API mistakes must be caught with their file and line (`LuauAnalyzerTests`).

The dashboard's e2e spec (`tests/e2e/omniblox.spec.ts` on the KM site) runs against fixtures that `OmniBloxFixtureExport` writes from a real run of the studio. Regenerate them after any change to a route's shape:

```bash
OMNIBLOX_FIXTURES=<site>/tests/e2e/fixtures/omniblox DOTNET_ROLL_FORWARD=Major dotnet test Omnipotent.Tests --filter FullyQualifiedName~OmniBloxFixtureExport
```
