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

Without a key OmniBlox still researches, harvests asset metadata and thumbnails, and builds and checks games offline; publishing, engine tests, model downloads and analytics wait for the key (the dashboard shows an owner task saying so).

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
| `OmniBlox_RequireLaunchApproval` | `true` | Owner approves each game before it goes public |
| `OmniBlox_MaxGamesInProduction` | `1` | Games in production at once (1–3) |
| `OmniBlox_MaintenanceIntervalDays` | `7` | Maintenance interval once a game is past its first two weeks |
| `OmniBlox_TelemetryBaseUrl` | `https://klive.dev` | Where games send anonymous gameplay telemetry (`/omniblox/telemetry`) |
| `OmniBlox_DevExRate` | `0.0038` | USD per Robux, only for the dashboard's cash estimate |

## HTTP API

All under `/omniblox/`, permission group `omniblox`. Reads: `overview`, `games`, `game?id=`, `game/media`, `game/render`, `game/files`, `game/file`, `game/telemetry`, `assets`, `asset`, `asset/image`, `events`, `playbook`, `tasks`. Actions: `tasks/resolve` (not launch approvals), `games/new`, `games/pause`, `games/steer`, `games/maintain`; owner-level (`studio.manage`): `games/approve`, `games/abandon`, `studio/toggle`. `POST /omniblox/telemetry` is the public endpoint the games call, authenticated by a per-game secret.

The JSON bodies are built in `OmniBloxViews.cs`, shared by the routes and the website's fixture export.

## Dashboard

KM site → **Schemery → OmniBlox** (`pages/schemery/omniblox`, components in `components/OmniBlox`, types and helpers in `composables/useOmniBlox.ts`):

- **Studio**: owner tasks with their actions and file downloads, readiness banners, the studio and harvester switches, "New game" with an optional theme, KPIs (live games, the game in production, revenue with the DevEx estimate and "spent $0", players, model calls and cache hit rate, labelled models), every game as a card with its stage track, players and revenue across live games, model usage, the harvester, and the activity log.
- **Game** (`/game/[id]`): stage track with the current exit checklist; tabs for the brief, milestones, last test, key facts, decisions and scored concepts; the world (3/4, above and spawn renders) with build problems and placed models; the store page, art, products and publishing record; numbers (Roblox analytics, telemetry sessions, funnel, feedback, purchases, script errors, maintenance and post-mortems); every cycle's hand-over; the workspace files; and the game's events. Pause, steer, maintain now, approve launch and abandon live in the header.
- **Assets**: the library by status or by search, with a drawer showing the labeller's label, the measurements, what was stripped and where the model came from.
- **Playbook**: every lesson with its outcome, confidence and evidence.

## Testing

`Omnipotent.Tests/OmniBlox` covers the file formats (against the Roblox files installed on the machine), the Luau checker (against every script Roblox ships), the compiler, verifier and renderers, compliance, the Roblox clients, the cycle runner, and the whole pipeline end to end against a scripted model and a fake Roblox (harvest → research → build → test → launch → maintenance → post-mortem, plus the no-key, approval, failed-review and watchdog paths). Set `OMNIBLOX_RENDER_OUT` to a folder to save the rendered images for a look.

The dashboard's e2e spec (`tests/e2e/omniblox.spec.ts` on the KM site) runs against fixtures that `OmniBloxFixtureExport` writes from a real run of the studio. Regenerate them after any change to a route's shape:

```bash
OMNIBLOX_FIXTURES=<site>/tests/e2e/fixtures/omniblox DOTNET_ROLL_FORWARD=Major dotnet test Omnipotent.Tests --filter FullyQualifiedName~OmniBloxFixtureExport
```
