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
| `Build/` | Game workspace on disk, scene document, place compiler, Luau syntax checker, geometry verifier, scene renderer, map report, walking check (`Playability`), economy bot (`Economy`), Game Kit loader |
| `Build/WorldGen/` | The world generator: the model catalog (roles, styles, sizes), world specs, the hub, obby-course and tycoon-plot layouts, kit-built props |
| `GameKit/` | The Luau modules every game starts from (embedded resources): data saving, shop and receipts, currency, rewards, leaderboard, feedback, analytics, world tags, templates, in-engine test runner |
| `LocalEngine/` | The local engine's Lune script (embedded): the kit's engine test run outside Roblox, for games Roblox's engine cannot run yet (runner in `Pipeline/LocalEngine.cs`, Lune installer in `Build/LuneToolchain.cs`) |
| `Agents/` | Work-cycle runner on KliveLLM tool sessions, tool registry, role prompts, the KliveLLM bridge (model gate) |
| `Pipeline/` | Orchestrator (stage machine, production queue, maintenance schedule, watchdog), stage gates, the owner's approval gates, the generated baseline (`GameScaffold`), briefings, test stage, launch sequence, live ops, each role's tools, settings |
| `Marketing/` | Icon / thumbnail / trailer composition, compliance checks |
| `Insights/` | Analytics sync, telemetry store, Playbook (lessons learned) |

### Lifecycle

Each game moves through `Research → Build ⇄ Test → Launch → Live → Sunset`. The owner has two gates on the way to players (see [The owner's gates](#the-owners-gates)): a tested build waits for the owner to play it and sign it off before any marketing, and the launch waits for the owner's approval of that build, store page and products before anything happens on Roblox. The orchestrator advances a stage only when its exit criteria hold (see `Pipeline/StageGates.cs`), keeps one game in production at a time (configurable), and maintains every live game: every 3 days for its first two weeks, then every `OmniBlox_MaintenanceIntervalDays` (weekly by default), sooner when telemetry shows an error spike or a game goes quiet after an update. Post-mortems run at 7 and 30 days live and feed the Playbook. Every transition is persisted, so a restart resumes exactly where the game was.

### The agent brain

Work happens in **cycles**. A cycle is one KliveLLM tool session with:

1. a role system prompt and tool list that never change between cycles (cache-stable prefix),
2. a briefing built from the game dossier: the owner's open directions and the watchdog's note first, then the goal, the brief, the game's own `design.md`, Playbook lessons, milestones, kept facts, decisions, the last hand-overs, the current state (build, strict types, map score, the last engine run, the last test) and the exit criteria,
3. an append-only tool loop.

**There is no cycle or turn budget.** A stage takes as many cycles as it needs. A cycle ends when the agent calls `finish_cycle`; when its context fills the runner asks it to finish the step it is on and hand over, and the next cycle continues from that report. "Fills" is measured against the model's window (`OmniBlox_ModelContextTokens`, 260K): the request plus room for the reply must fit, the agent is asked to hand over 64K before that and stopped 32K before it, using the provider's own token count for each request. Pictures go to the model as JPEG and every one is shown; AIRouter refuses a request body somewhere between 3.9 and 4.9 MB (HTTP 413), so when the next request would pass 3.5 MB the oldest pictures are swapped for a note, in one batch down to a third under the limit so the prefix cache is rewritten rarely. A refused request drops the pictures and carries on. A turn cap of 300 remains only as a runaway guard. The structured report is written back to the dossier: summary, decisions, problems, next steps, key facts as `{key, fact}` pairs, `drop_facts`, and `directives_done`. That rollover is the compaction: nothing important lives only in a context window.

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

**The baseline.** When a game reaches Build, `Pipeline/GameScaffold.cs` makes a game that already plays, from the brief: the template (obby, tycoon or simulator; a "sandbox" brief that harvests things is a simulator), the world spec (the biome the brief talks about most, the zones it names, what players harvest and with what tool), the generated world, the template's settings (tool tiers, upgrades, rebirth, tips), a simulator economy balanced by a bot playing it on that map, a tutorial quest chain whose guide lines point at real things, and `design.md` describing all of it. An older hand-built world is kept in `snapshots/scene.before-worldgen.json`. The first studio's builder started from an empty plate, spent its cycles on scaffolding and shipped a strip of grass floating in the sky with its game hidden in client scripts.

The builder then works milestone by milestone: know the baseline (its renders, `map_report` and the economy timeline), tune the world with `design_world`, configure and balance the template for the brief, add only what the template lacks (systems with specs), polish. Its tools (`Pipeline/Tools/`):

- **Files** (`FileTools.cs`): `list_files`, `read_file` (including the Game Kit's own source under `GameKit/`), `write_file`, `append_file`, `edit_file`, `delete_file`, `search_code`. Every write is syntax- and type-checked on the spot.
- **World** (`WorldTools.cs`): `design_world` (shows the world spec; given changes — in any case, nested fields kept — it regenerates the terrain, layout, gameplay objects and scenery the generator made, keeps what was placed by hand, rebalances a simulator's prices and remakes quests whose targets are gone), `balance_economy`, `terrain_sculpt` (flatten, raise, lake, river, path, paint; everything standing there moves with the ground), `add_objects`, `place_asset`, `scatter_assets` (Poisson-disk, avoiding paths, water, slopes and objects), `place_along`, `place_ring`, `set_folder`, `set_lighting` (sunny, golden, night, spooky, tropical, snowy, candy, overcast), `ground_height`, `scene_summary`. `[x, z]` positions stand anything on the ground. `terrain_generate`, `set_map_area` and `plan_map` refuse on a generated world (its ground, play area and plan are the layout) and point at `design_world` and `terrain_sculpt`.
- **Library and sounds**: `search_assets`, `browse_assets` (up to 12 models side by side as one picture), `view_asset`, `search_sounds` (only Roblox's own and APM's uploads, which are licensed for every game; ids it returns are recorded and `update_manifest` refuses any other audio id).
- **Seeing it**: `render_scene` (views below), `map_report` (with the economy timeline for a simulator).
- **Checking it**: `build`, `run_tests`, `diagnose_failure` (the failing check's own source, the functions it calls, the code around an error's line), `pacing_check` (minutes per step against what keeps kids playing), `kit_reference` (the kit's API with exact signatures, by topic).
- **Progress**: `set_milestones`, `update_milestone`, `mark_feature_complete` — accepted only when the current files built clean, the map reaches the bar (75) with no blocking problem, every milestone is done and, where the engine can run, `run_tests` passed on this build.

The tester's `verdict` needs scores (1–10) for first impression, world beauty, world fullness, a clear goal, the core loop and progression: a pass needs no reported error, every score at least 6 and an average of 7.

### The world generator

`Build/WorldGen/WorldGenerator.cs` turns a world spec into terrain and objects, deterministically from its seed:

- **Layouts.** `hub` (simulators): a spawn hub with the sell pad and the upgrade stand in sight of the spawn, the shop, the rebirth altar, the leaderboard, a centrepiece; zones in a ring in the order they open, each on a plateau a little higher than the last, a ramped road with a signpost to each, a gate at each paid zone's entrance, a landmark at its far side, resource nodes inside, ridges or a lake between zones, mountains past the edge. `course` (obbies): a start platform (spawn looking down the course through a START arch, shop, leaderboard), stages snaking in rows over a lava or water basin with one new mechanic at a time and a breather every few stages, a checkpoint at every stage, a golden finish, an invisible kill floor. `plots` (tycoons): a plaza with plots facing it, each with its line of twelve buttons in price order, droppers, a conveyor, an upgrader, a collector, a sign and a spawn.
- **Placement.** Every library model stands on the lowest ground under its contact footprint (trees on their trunk) and is refused on slopes too steep for it; stands, signs and gates face where players come from; nothing overlaps roads or other objects (a spatial hash); one art style per map; untextured (PBR-only) models are left out; scatter models above 60 parts are left out; each dressing layer has its share of a part budget phones carry.
- **Dressing in layers.** A ring of trees round each zone (open at its gate), groves and clearings between zones by noise, bushes, flowers and rocks along the roads, a few of each biome's own touches, a tree line past the play area.
- **The model catalog** (`AssetCatalog.cs`) classifies the labelled library into roles (tree, conifer, bush, rock, fence, lamp, fountain, landmark… with size bounds), style families (stylized, voxel, realistic) and biomes. Where the library has nothing for a role, kit-built props (`KitProps.cs`) stand in, and the harvester is asked to find models.
- **A simulator's zones** open in order: the first is free, every later one is behind a gate, and the economy plays them in that order.

### The economy check

`Build/Economy.cs` reads a simulator's economy from the game itself (nodes by zone, gate prices, tools, upgrades, rebirth) and a bot plays it greedily on the map, walking distances included. `Balance` sets every price so that a good player meets a gentle schedule: a first sale in about a minute, a first purchase in the first minute or two, a new zone every several minutes, a rebirth about 25 minutes after the last zone. `build` runs it on every simulator build; a wall (a zone out of reach within six hours) or a slow first purchase is an error that blocks the Build stage.

**`build`** compiles the place and checks every script's syntax, then its **strict types** with [luau-lsp](https://github.com/JohnnyMorganz/luau-lsp) `analyze` (`Build/LuauAnalyzer.cs`): calling a method a class does not have, a kit function that does not exist, an unknown global, the wrong number of arguments. luau-lsp 1.70.1 and Roblox's type definitions are downloaded on first use into the studio's `tools/` folder and verified against pinned SHA-256 hashes; without them a build says "not type-checked". Then the geometry verifier and the map report. A clean build of files that have not changed since the last clean revision (on the same Game Kit) keeps that revision, so a passing engine run on it still counts.

**`run_tests`** runs the game in Roblox's real engine (`Pipeline/EngineTests.cs`): it compiles the engine-test flavour of the place (Luau execution loads a place without running its scripts, so each server Script becomes a ModuleScript returning its body as a function, tagged `OmniTestScript`), saves it as a new unpublished version, and runs the kit's `TestRunner` there. Phases: **kit** (the kit checks itself before any game code loads — a failure there is the studio's bug: the owner gets a task and the game is not blamed), **load** (every module loads), **boot** (the whole server starts — kit, the game's systems and scripts — and runs for 8 s; every error comes back with the game's file and line), **world** (spawn, products, quests, gates, the template's loop), **game** (the game's own specs in `src/tests`). There are no players or physics in the engine, so game logic lives in systems whose functions the specs call with `t.player()` stand-ins. The first studio's three games all died on a kit test bug (every test reused player id -1001, so profiles carried over between tests); the phases and the kit control check exist so that can never again be blamed on a game.

**The local engine.** While Roblox's engine cannot run a game here (no API key, or no free experience slot), `run_tests` runs the same task script in the studio's local engine instead (`Pipeline/LocalEngine.cs`): the engine-test build in [Lune](https://github.com/lune-org/lune) 0.10.5 with `LocalEngine/engine.luau`, which stands in for what the kit uses of the engine (events and RemoteEvents, `WaitForChild`, CollectionService, a Heartbeat, TweenService, Debris, DataStores in memory, a MarketplaceService that sells nothing, terrain accepted and dropped, models' pivots and bounding boxes, ModuleScript `require`) and for the server services games often use (collision groups, badges, text filtering, paths, messaging). It has no physics, replication or clients. Lune is downloaded on first use into the studio's `tools/` folder and verified against pinned SHA-256 hashes (Windows and Linux x86_64 builds). The run is recorded as `Build.LastEngineRun` with `Local: true`; its summary starts "Local engine:", and the agents' reports say it ran there.

- **What a local pass counts for.** While the local engine stands in, the Build stage's exit criterion is a local pass on the current build ("booted and played through in the local engine"), and `mark_feature_complete` needs it too. So no game reaches Test, the owner's sign-off or marketing without having booted and played through, key or not. The Test stage reuses that pass. Once the key and a slot exist, only a run in Roblox's engine counts: the criterion switches back and a local pass no longer satisfies it. The launch sequence and `ship_update` always run the game in Roblox's engine; a local pass never lets them skip it.
- **The studio's problems are never the game's.** Lune that cannot be downloaded (offline), no pinned build for the platform, a hash mismatch, or a crash of the stand-in itself (no result printed) means the game is simply not run locally. `run_tests` says why, nothing is recorded, and the criterion disappears, so the game is judged as it was before the local engine existed. A failed install is retried after an hour. A failure of the kit's own checks in the local engine is reported to the owner as a bug in the kit or the stand-ins.
- **What still fails a game.** A run that does not finish in 240 s (the limit Roblox gives the same task) fails, as a hung task fails in Roblox. A real Roblox API the stand-in has nothing for fails as "… is not a valid member of …". The builder is told to report that rather than rewrite working code, and a game stuck on it reaches the owner through the watchdog; adding the API key ends it, or the stand-in can be extended in `engine.luau`.

### The Game Kit

Every game starts from `GameKit/` (embedded Luau, strict-typed and checked clean by luau-lsp in the tests): saving (`Data`), currencies with extra currencies and multipliers (`Currency`), the shop and receipts (`Shop`), daily rewards, the leaderboard, feedback, analytics, effects (bursts, floating numbers, sounds, toasts), zones behind gates (`Progression`, tag `ZoneGate`), rebirth, quests with a glowing guide line, the world (terrain from the heightmap, map edges, void rescue, tags such as `KillBrick`, `Coin`, `Bounce`, `Spin`, `Bob`, `Glow`, `Sparkle`), the three templates (obby, tycoon, simulator), a phone-first HUD (animated counters, a side menu, modals, toasts, a welcome card, a quest tracker) and the test runner. A game's own code is systems (`src/server/systems/<Name>.luau`, started by the kit's Boot) and client controllers (`src/client/controllers/<Name>.luau`, given the HUD's API).

### Seeing the world

`Build/WorldRenderer.cs` draws a compiled world in software: the terrain with smooth light and biome colours, water, parts with their shapes, materials and sun shadows, every library model as Roblox's own picture of it standing at its real size, the sky and grading of the lighting preset, and distance haze that starts behind the subject. Views: `map` (from above with the plan: zones, paths, landmarks, the play area, a grid in studs, where the spawn faces, labels that group big sets such as "Pine Tree ×70"), `overview`, `aerial`, `iso`, `spawn` (what a player sees on joining), `zone:<id>` (seen from the map's middle, outwards), an object id, or a camera `"x,y,z>x,y,z"`. Pictures the agents look at during a cycle are kept with the cycle's log.

`Build/MapReport.cs` scores the map out of 100 in categories — ground and edge (12), coverage (18), density and composition (14), gameplay layout (20), landmarks and navigation (10), variety and style (10), correctness (16); an obby is scored on its course (40), scenery, start and correctness — and lists what to fix with coordinates. Blocking problems cap the score at 45 whatever else is right: a flat slab of ground or none, a play area under 200 studs or a corridor (aspect over 2.2), terrain that ends within 40 studs of the play area, less than 40% dressed or a barren hole over 15%, a zone without scenery or nodes, a gated zone without a gate, no sell pad, a spawn facing nothing, geometry errors or more than six floating or sunk objects, an obby with stages that cannot be reached. The bar is 75; aim for 85+. The first studio's report gave its flat 140×398 strip of grass 100/100; it now scores 45 and is blocked.

`Build/Playability.cs` walks the map on a 3-stud grid from the spawn (steps no higher than a character climbs, models blocking only round their trunk, gates passable) and reports a sell pad, a zone entrance, resource nodes or a plot that cannot be reached, with walking distances the economy uses.

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
| Experience icon | `POST publish.roblox.com/v1/games/{u}/icon` (form field `request.files`) | session |
| Store thumbnails | `POST publish.roblox.com/v1/games/{u}/thumbnail/image` (only when the homepage API refused them) | session |
| Maturity & Compliance questionnaire | `apis.roblox.com/experience-questionnaire/v1/questionnaires/{u}/latest`, `/v1/questionnaires/{id}`, `/v2/questionnaires/additional/universes/{u}`, `/v1/responses/validate`, `/v1/responses/preview`, `/v2/responses/{u}/submissions`, `/v2/responses/preview`, `/v2/responses/publish` | session |

**Multipart bodies** go through `RobloxForm` (`Roblox/RobloxHttp.cs`). .NET's `MultipartFormDataContent` quotes its boundary (`boundary="…"`), and Roblox's edge firewall answers that with `403 9009 "WAF Blocked Request"` on developer products before the key is even checked. That is why old products could not be taken off sale. `RobloxForm` sends a bare-token boundary and quoted part names, with no `filename*`.

**The owner's session.** No API key can set an experience's icon (the API-key gateway has the badge icon only, and the localization icon route refuses the source language) or answer the questionnaire. `Roblox/RobloxSessionClient.cs` does what the Creator Dashboard does, with the owner's `.ROBLOSECURITY` cookie (`OmniBlox_RobloxSessionCookie`) and Roblox's CSRF handshake. When Roblox rotates the cookie (`Set-Cookie`), the client uses the new one at once and saves it to the setting. If the session is signed out (401), one **Sign OmniBlox in to Roblox again** task is filed, and everything waiting resumes once a session works.

One thing still has no API at all: **creating an experience**. You create blank experiences once, and they become the "experience slots" the bot fills.

**The questionnaire** (`Marketing/MaturityQuestionnaire.cs`, `Pipeline/StorePageChores.cs`) is answered from the content notes the compliance check prepares (the ones the owner approves with the launch), the game's design and the Game Kit's fixed behaviour (`MaturityQuestionnaire.KitFacts`). Roblox's questions change without notice, so the model maps those facts onto whatever is asked: radio, checkbox and text questions, with follow-ups under the option that reveals them. The rest is deterministic:
- Every question shown must be answered with its own options, and answers to hidden follow-ups are dropped. Values are JSON, as the dashboard sends them.
- Roblox's own validation must pass. Problems go back to the model, up to three rounds.
- Nothing is saved or published unless Roblox's preview rating fits the content: below 9+ when the notes say no violence or fear, at most 9+ when they say mild. The rating must also restrict no countries.
- Otherwise the answers stay saved for the owner, and the owner gets a task with the preview and every answer.
- The answers and the rating are kept in `Publish.Questionnaire`. A game is answered again only when its content notes change.

Places are published as XML (`.rbxlx`). The publishing API cannot carry unions (`PartOperation`), `SurfaceAppearance`, `EditableImage`, `EditableMesh` or `BaseWrap`, so the Asset Library marks models containing them as not placeable.

**The launch sequence** (`Pipeline/LaunchSequence.cs`) runs nothing on Roblox until the game has run clean in the engine and the owner has approved the launch: key → slot → compliance → final build → engine tests in Roblox's engine (skipped when a passing Roblox run already covers these files; a local engine pass never does) → **the owner's launch approval** → an earlier abandoned game's products in the same experience are taken off sale → products → the publish build (the same files, with the product ids) → store page → thumbnails → the icon and the maturity questionnaire, through the owner's session → **a maturity rating on Roblox** → publish → public. A step that keeps failing backs off (5 minutes doubling to 6 hours) and, at the fourth failure, becomes an owner task and an alert, instead of retrying every five minutes for hours.

**Public is not playable.** Since September 2025 Roblox lets nobody but the creator play an experience with no maturity rating. The first launched game (Oct 24 2026) went public unrated and sat closed to every player for four days while the dashboard called it live. Its maintainer read the empty numbers as a slow start. So:
- The launch goes public only once Roblox shows a rating (`PlayerReach.RatingGateAsync`, the public `experience-guidelines-api/…/get-age-recommendation`). Without one it waits, and the questionnaire task says the game will have no players until it is answered. If Roblox cannot be asked, the launch goes ahead and the watch below catches it.
- `Pipeline/LiveWatch.cs` runs whether or not the studio is on. Every 30 minutes it asks Roblox, as a signed-out visitor, whether each public game can be joined (`games.roblox.com/v1/games/multiget-playability-status`: `GuestProhibited`/`Playable` is open; `ContextualPlayabilityUnrated`, private, under review and the like are closed). Every 12 hours it syncs analytics, including impressions and clicks. Neither needs a model or writes to Roblox.
- A public game players cannot join puts an error at the top of the dashboard and a banner on its page, sends one Discord message per cause, and leads its maintainer's briefing and `game_metrics`. When a rating appears that the studio never submitted, it is recorded as the owner's (`QuestionnaireRecord.ByOwner`) and the questionnaire task closes.

**Store-page chores** (`Pipeline/StorePageChores.cs`): taking old products off sale, the icon, store thumbnails and the questionnaire. They never block a launch (the rating gate above does that). Whatever is not done at launch is retried from housekeeping (every 5 minutes, for live and sunset games), waiting 5 minutes and doubling to 6 hours (`Studio.Chores`). The owner hears only when a person is needed:
- No session is set. The icon and questionnaire tasks say how to add one.
- Roblox signed the session out.
- The questionnaire's rating came out above what the content allows.
- A chore has kept failing for 24 hours.

The same sweep finishes tasks that older launches filed for the owner, and closes them.

### The owner's gates

`Pipeline/Approvals.cs`. Neither gate can be passed by an agent or by the Act-tier task route; only the owner's `games/approve` (or `games/reject`) on the dashboard does, and each approval is for exactly what was looked at.

1. **Build sign-off** (`OmniBlox_RequireBuildSignoff`). When a game passes its tests it waits in Launch, before any marketing, for the owner to play that revision (▶ Play) and **Approve build**, or **Request changes**: the note becomes a direction for the builder and the game goes back to Build. A new revision needs a new sign-off.
2. **Launch approval** (`OmniBlox_RequireLaunchApproval`). When the store page, art and products are ready and the final build has run clean in the engine, the owner approves this revision with this store page and these prices (a fingerprint of the title, description, art and every product). A change to any of them afterwards needs approving again. **Request changes** sends the note to the marketer, or back to Build when the game itself needs work.

Every decision is kept in the dossier (`Publish.Approvals`) and the owner's change requests go into the agents' briefings.

## Setup

1. **API key.** In the Creator Dashboard → Open Cloud → API Keys, create a key with the experiences you will give OmniBlox and these permissions: `universe-places` (write), `universe` (write), `universe.place` (write), `universe.place.luau-execution-session` (read, write), `game-pass` (read, write), `developer-product` (read, write), `universe-analytics` (read), `legacy-asset` (manage), `universe.thumbnail` (write). Put it in the OmniSetting `OmniBlox_RobloxApiKey`.
2. **Creator.** Set `OmniBlox_CreatorUserId` (or `OmniBlox_CreatorGroupId` if you publish under a group).
3. **Roblox account** (for creating experiences, the icon, store thumbnails and the maturity questionnaire). On the studio page, use **Roblox account → Connect with Quick Log In** (`Pipeline/RobloxConnection.cs`, `Roblox/RobloxQuickLogin.cs`):
   - The dashboard shows a short code. On a device signed in to Roblox as the account that owns the experiences, open Account Settings → Quick Log In, enter the code and press Confirm Login.
   - Roblox accepts a code only from nearby, so confirm from the server's network. The studio then signs itself in with the code: no password passes through it.
   - The session is made from the server's own network, so Roblox does not sign it out for being used from another region.
   - Before the session is kept, Roblox must accept it and it must belong to `OmniBlox_CreatorUserId` (a session for another account is refused and signed out).
   - Captchas and two-step checks are never answered for you. If Roblox asks for one, paste a cookie instead.
   - The fallback is **Or paste a session cookie**: the `.ROBLOSECURITY` value (a bare value, `.ROBLOSECURITY=…` or a whole Cookie header), checked the same way. Its route is registered with `KliveAPI.MarkSecretBodyRoute`, so OmniDefence never stores the body.
   - Either way the session lands in `OmniBlox_RobloxSessionCookie`. Whatever waited for it (icon, thumbnails, questionnaire) runs at once, with the studio on or off.
   - **Disconnect** signs out a session the studio made, but never a pasted one, which would sign your browser out.
   - If Roblox signs the session out later, the dashboard asks you to connect again. Without a session these chores stay owner tasks, and the questionnaire task says the game is unplayable until it is answered.
4. **Experience slots.** Create a few blank experiences in the Creator Dashboard and add each as `universeId:placeId` to `OmniBlox_ExperienceSlots`.
5. **AIRouter.** OmniBlox only runs model work while `RemoteLLMProvider` is `AIRouter`.
6. Open **Schemery → OmniBlox** on the KM site. Switch the **harvester** on first so the Asset Library fills up, then switch the **studio** on.
7. **Playtest experience** (optional). To play any game in Roblox at any stage, live ones included, create one more blank experience. Give the key access to it and add it as `universeId:placeId` to `OmniBlox_PlaytestSlot` (see [Playtests](#playtests-playing-a-game-at-any-stage)). Without it, unlaunched games play in their own private experience, and Open in Studio needs nothing at all.

Without a key OmniBlox still researches, harvests asset metadata and thumbnails, and builds and checks games offline, running their engine tests in the [local engine](#building-a-game); publishing, Roblox's own engine tests, model downloads and analytics wait for the key (the dashboard shows an owner task saying so).

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

The dashboard's **Owner tasks** list is everything OmniBlox cannot or must not do itself:
- add the API key and the Roblox session;
- add experience slots;
- play and sign off a tested build, and approve a launch (the two [gates](#the-owners-gates));
- look at escalations from the watchdog.

With a session, the studio uploads the icon and answers the maturity questionnaire itself. Those become tasks only without a session, when a rating comes out higher than the content allows, or when Roblox keeps refusing for a day. Each task carries what is needed, such as the icon file or the prepared answers. Money never moves: no ads, no purchases, and cashing out Robux stays with you.

**Do it yourself.** Every task the studio could take over has a **Do it yourself** button (`GameActions.Delegate`):
- The icon and the questionnaire are done at once with the connected account. Without one, the button says to connect it first.
- An experience slot is filled by creating an experience on the connected account.
- A product left on sale is taken off sale now.
- Anything else on a game (an escalation, a note) is handed to that game's agent as a direction. A live game's maintainer runs next.
- Approvals, the API key and signing in stay yours, and the button says so.

### Managing a game on Roblox

The game page's **Manage on Roblox** card (`Pipeline/GameActions.cs`, `studio.manage`) runs one action at a time per game, never while an agent cycle works on it. Each action is recorded step by step on the game (`OwnerAction`):
- **Update on Roblox**: every product updated, the current build engine-tested and published, the store page applied, the icon uploaded again, the questionnaire answered if owed, and the reach checked.
- **Make private / Make public**: going public needs a maturity rating. A game that was taken down comes back live, with its products on sale and maintenance resumed.
- **Take down**: private, products off sale, maintenance stopped (Sunset). Optionally it is also **archived**: Roblox has no delete, and archiving is the Creator Dashboard's nearest thing. It needs the session (`PATCH develop.roblox.com/v2/universes/{id}/configuration {"isArchived":true}`), and the experience is never used again (`StudioState.RetiredExperiences`).
- **Move**: the game is relaunched, as it is, in another experience: a new one the studio creates on the connected account, or one you name as `universeId:placeId`.
  - The old experience is made private, its products come off sale, and it is retired.
  - Everything the game had on Roblox is reset, and its products are made again there.
  - Moving counts as your approval of the same build, store page and products, recorded with a note.
  - One launch step runs at once. If the launch then waits (e.g. for a rating), the studio carries it on when switched on.

**Changing accounts.** On the Roblox account card:
1. Use **Switch to another account** and confirm the code with the other account. It becomes the creator (`OmniBlox_CreatorUserId`).
2. Every experience of the old account (the studio's and your listed slots) is retired, so no new game lands there. Games already published are flagged (`Publish.OnOldAccount`) and listed with **Move to this account**.
3. Set the new account's API key on the same card. Roblox's introspection (`POST apis.roblox.com/api-keys/v1/introspect`) must say it exists, is enabled, has not expired, works from the server, and belongs to the studio's account. A key for another account is refused.

## Settings

| Setting | Default | Meaning |
|---|---|---|
| `OmniBlox_Enabled` | `false` | Master switch for the production pipeline (research through maintenance) |
| `OmniBlox_HarvesterEnabled` | `false` | Background asset harvesting; independent of the master switch so the library can fill before the first game |
| `OmniBlox_RobloxApiKey` | — | Open Cloud API key (sensitive) |
| `OmniBlox_RobloxSessionCookie` | — | The owner's `.ROBLOSECURITY` cookie, for the icon, store thumbnails and maturity questionnaire (sensitive; Roblox's rotations are saved back here) |
| `OmniBlox_CreatorUserId` / `OmniBlox_CreatorGroupId` | — | Who owns uploaded assets |
| `OmniBlox_ExperienceSlots` | — | `universeId:placeId` entries the bot may fill |
| `OmniBlox_PlaytestSlot` | — | `universeId:placeId` of a private experience for the owner's playtests of any game at any stage (never used as an experience slot) |
| `OmniBlox_RequireBuildSignoff` | `true` | Owner plays and signs off each tested build before it is marketed |
| `OmniBlox_RequireLaunchApproval` | `true` | Owner approves each launch (build, store page, products) before anything happens on Roblox |
| `OmniBlox_MaxGamesInProduction` | `1` | Games in production at once (1–3) |
| `OmniBlox_MaintenanceIntervalDays` | `7` | Maintenance interval once a game is past its first two weeks |
| `OmniBlox_MaxOutputTokens` | `16384` | Output limit per model reply (4096–65536); a tool call cut off by it is not run |
| `OmniBlox_ModelContextTokens` | `260000` | The model's context window; cycles hand over as their prompt nears it (64000–2000000). Replaces `OmniBlox_CycleContextTokens` |
| `OmniBlox_TelemetryBaseUrl` | `https://klive.dev` | Where games send anonymous gameplay telemetry (`/omniblox/telemetry`) |
| `OmniBlox_DevExRate` | `0.0038` | USD per Robux, only for the dashboard's cash estimate |

## HTTP API

All under `/omniblox/`, permission group `omniblox`. Reads: `overview`, `games`, `game?id=` (includes `Playtest`, `Live` — the running or last cycle step by step, `CycleLogs`, `EngineAvailable` (Roblox's engine can run it), `LocalEngine` (it cannot, and the local engine stands in), `Approvals` — the gate it waits at and every decision — and `Watchdog`), `game/live?id=` (just the live cycle, for close polling), `game/cycle?id=&cycle=` (one past cycle's full log), `game/cycle/image?id=&cycle=&file=` (a picture from it), `game/map?id=` (the map report on the current files: score, rubric, bar, categories, findings), `game/media`, `game/render?id=&view=&labels=&marketing=&w=&h=`, `game/files`, `game/file`, `game/place?id=&build=` (a playtest build as a place file; the build it is comes back in `X-OmniBlox-Build`, a fallback reason URL-encoded in `X-OmniBlox-Note`), `game/telemetry`, `assets`, `asset`, `asset/image`, `events`, `playbook`, `tasks`. Actions (`studio.act`): `tasks/resolve` (not the build sign-off or the launch approval), `games/new`, `games/pause`, `games/resume` (also after a watchdog pause; the ladder starts over), `games/steer` (`{gameId, note}` → a direction, up to 2000 characters; steering a game the watchdog paused resumes it), `games/directive` (`{gameId, directiveId, action: drop|reopen}`), `games/build` (builds now and answers with the report), `games/test` (runs the engine tests in the background, in the local engine while it stands in — the answer's `local` says which; the result lands in `Build.LastEngineRun`), `games/maintain`, `games/playtest` (`{gameId, build, fresh}` → the join link), `games/playtest/follow` (`{gameId, follow}`); owner-level (`studio.manage`): `games/approve` (`{gameId, gate?: build|launch, note?}`; the gate defaults to the one the game waits at, and the launch cannot be approved before the build is signed off), `games/reject` (`{gameId, gate, note, backToBuild?}`), `games/abandon`, `games/revive` (back to the stage it was abandoned in; Test and Launch go back to Build), `studio/toggle`, and the Roblox account: `GET roblox` (connected account, a pending Quick Log In code and its state, the last problem, what waits for it; never the session or the code's key), `POST roblox/quicklogin` (a new code; `{switchAccount: true}` makes whoever confirms the studio's account), `roblox/quicklogin/poll` (asks Roblox; signs in once confirmed), `roblox/quicklogin/cancel`, `roblox/cookie` (`{cookie, switchAccount?}`, a secret-body route), `roblox/apikey` (`{apiKey}`, checked by introspection; a secret-body route), `roblox/verify`, `roblox/disconnect`, `games/action` (`{gameId, action: sync|private|public|takedown|move, archive?, target?: "new"|"universeId:placeId"}`; 409 with the reason when it cannot start). Also `tasks/delegate` (`{taskId}`, `studio.act`; "do it yourself", 409 when only the owner can). `POST /omniblox/telemetry` is the public endpoint the games call, authenticated by a per-game secret (or, for playtest builds, the game's playtest key, which routes the batch to its playtest log).

The JSON bodies are built in `OmniBloxViews.cs`, shared by the routes and the website's fixture export.

## Dashboard

KM site → **Schemery → OmniBlox** (`pages/schemery/omniblox`, components in `components/OmniBlox`, types and helpers in `composables/useOmniBlox.ts`):

- **Studio**: owner tasks with their actions and file downloads, readiness banners, the studio and harvester switches, "New game" with an optional theme, KPIs (live games, the game in production, revenue with the DevEx estimate and "spent $0", players, model calls and cache hit rate, labelled models), every game as a card with its stage track, players and revenue across live games, model usage, the harvester, and the activity log.
- **Play** (`?play=1` on a game, linked from every playable card and the studio header): build picker, Play in Roblox, Open in Studio, Follow, and the recent playtests with their errors and feedback.
- **Game** (`/game/[id]`): at a gate, the gate card — *Play and sign off r12* (map score, engine result, economy, the review; ▶ Play, Approve build, Request changes) or *Approve the launch* (the store page, every product and price, the build; Approve launch, Request changes with "the game itself needs work") — and every earlier decision; stage track with the current exit checklist; the watchdog's state when it is stepping in (with Resume when it paused the game); **Working now** — the running cycle step by step, polled every 4 s: what the agent says, every tool call and its result, the pictures it looked at; **Your directions** — send one, see which are open, delivered or done (with what was done), drop or reopen them. Tabs: the brief, milestones, last test, key facts (keyed, with the owner's marked), decisions and scored concepts; **World** (map with the plan, aerial, 3/4, spawn and each zone; the map report with its score against the bar, each category's score, findings and measurements; the map plan; a simulator's economy timeline and any wall; build problems; placed models); **Tests** (each check on the current files — compiles, strict types, geometry, map, Roblox's engine or the local engine standing in for it — with Build now and Run in Roblox (Run locally while the local engine stands in), the last engine run marked with where it ran, its failing checks, runtime errors with file and line, warnings and passed checks, and the Test stage's report); the store page, art, products and publishing record; numbers; **Brain** (every hand-over with its tool calls and cut-offs, and every step of the recent cycles); the workspace files; and the game's events. Pause/resume, steer, maintain now, abandon and revive live in the header; the overview lists your approvals.
- **Assets**: the library by status or by search, with a drawer showing the labeller's label, the measurements, what was stripped and where the model came from.
- **Playbook**: every lesson with its outcome, confidence and evidence.

## Testing

`Omnipotent.Tests/OmniBlox` covers the file formats (against the Roblox files installed on the machine), the Luau checker (against every script Roblox ships), the compiler (both flavours), verifier, terrain, map report and renderers, compliance, the Roblox clients, the cycle runner (hand-overs, cut-off replies, owner directions mid-cycle), the fact book and the watchdog, the world generator (`WorldGenTests`, on a part-built library of every role, `TestModels`: determinism; a simulator standing on the ground, facing the hub, gated in order, stocked and walkable; an obby's stages 1..N; a tycoon's plots and buttons; the old flat strip blocked; a zone walled off from the spawn; the economy's schedule and walls; re-grounding after a terrain edit; the baseline from a brief; the launch fingerprint), the world tools end to end (`WorldToolTests`: the generated world through the agents' own tools, the map report, every view, store art), and the whole pipeline end to end against a scripted model and a fake Roblox (harvest → research → build → test → both owner gates → launch → maintenance → post-mortem, plus the no-key, changed-price, changes-requested, failed-review and watchdog-pause paths). Set `OMNIBLOX_RENDER_OUT` to a folder to save the rendered images and store art for a look.

The strict type check runs against the real luau-lsp when `OMNIBLOX_LUAU_LSP` names its executable and `OMNIBLOX_LUAU_DEFS` Roblox's definitions (`globalTypes.None.d.luau`): the whole Game Kit must type-check clean for every template, and a game's API mistakes must be caught with their file and line (`LuauAnalyzerTests`).

With `OMNIBLOX_LIVE_DIR` pointing at an export of the live studio's games (place files with their `scene.json`, and each model's label and thumbnail; see `LiveLibrary`), `LiveMapTests` rebuilds the asset library from real models, generates and renders a simulator, an obby and a tycoon, makes each template's baseline from a brief exactly as the studio does and writes the place files to open in Roblox Studio, and checks the live studio's old map is now blocked.

**The local engine** ([above](#building-a-game)) runs the kit's engine test (`TestRunner.RunAll`, the same task script Open Cloud runs) outside Roblox, reporting errors as Roblox does. `LocalEngineTests` covers it two ways. With `OMNIBLOX_LUNE` naming lune's executable, it runs in Lune: each template's baseline passes every check (boot, products, quests, gates, a simulator harvested, sold, upgraded and unlocked zone by zone, a tycoon plot bought out, every obby stage reached); a system crashing at start and a failing spec are caught with their file and line, so it is no rubber stamp; a game's own system using collision groups, badges, filtering, paths and messaging passes on the stand-ins; and a keyless game gets the boot and playthrough check through the builder's own `run_tests`, in the dossier and the Tests tab's data. The other tests run on a fake engine and always run: the fallback and what counts once the key arrives, a failing run located in the game's files, offline meaning "not run locally", how a hang, a crash of the stand-in and a failed task are read, and a whole keyless game through Build and Test on the local engine whose launch, once the key arrives, still runs Roblox's engine before anything is created there. `OMNIBLOX_LIVE=1` also downloads the pinned Lune release and runs it.

The dashboard's e2e spec (`tests/e2e/omniblox.spec.ts` on the KM site) runs against fixtures that `OmniBloxFixtureExport` writes from a real run of the studio. Regenerate them after any change to a route's shape:

```bash
OMNIBLOX_FIXTURES=<site>/tests/e2e/fixtures/omniblox DOTNET_ROLL_FORWARD=Major dotnet test Omnipotent.Tests --filter FullyQualifiedName~OmniBloxFixtureExport
```
