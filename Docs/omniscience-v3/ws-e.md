# WS-E — Website UI (progress)

Repository: `klives-management-website`, branch `omniscience-v3`, worktree
`C:\Projects\Websites\klives-management-website\.claude\worktrees\omniscience-v3` (never the main folder, which stays on `master`).
`node_modules` in the worktree is a junction to the main folder's. Charter: [workstreams.md](workstreams.md).

## Slices
1. **2026-11-09, engine pages.** The frozen contract then covered the engine and kernel only (status, modules, jobs, LLM calls,
   settings, live stream, tactics, runs, selections, cohorts, playbooks, read models with an untyped `body`, Explore), so the first
   slice was the foundation plus every page that contract could drive.
2. **2026-11-12, domain pages on the WS-C contract.** WS-C's `api-contracts.ts` (branch `claude/omniscience-v3-ws-c-99fa83`,
   which merges into `omniscience-v3`) adds the people directory, the dossier body, briefs, digests, signals, insights
   (relationships, community pages, similar people, the people map, check-ins), links, translation, monitors and data controls.
   `scripts/omniscience/contracts.ts` is now a copy of that file. Pages that need WS-A, WS-B or WS-D DTOs still wait (below).
3. **2026-11-13, the merged contract.** After the backend merge, `contracts.ts` is a copy of `api-contracts.ts` on
   `omniscience-v3` with every workstream in it (`5cde727`), so WS-A's, WS-B's and WS-D's DTOs are typed now. The notice
   list has sentences for plan reminders and standing-question flips (`0d518cd`). Their pages are the next slice.

## Built
Pages live under `pages/omniscience-v3/` (routes `/omniscience-v3/*`) so the legacy pages keep working until cutover, mirroring
the backend's `OmniscienceV3` folder. `pages/omniscience/**` is not usable yet: a folder beside `pages/omniscience.vue` would
turn the legacy page into a parent route.

| Piece | Where | What |
|---|---|---|
| Contract types | `scripts/omniscience/contracts.ts` | A copy of the backend's `api-contracts.ts` (branch `omniscience-v3`, all workstreams merged). Re-copy when the backend contract changes; never edit the types here. |
| Data client | `scripts/omniscience/api.ts` | `omniGet` / `omniPost` / `omniPostText` (a POST whose answer is a file) / `omniStream` (NDJSON) / `omniStreamUrl`. One client, no retry layer of its own, an abort signal on every call, errors classified as `OmniApiError.kind` (disabled, starting, failed, unavailable, forbidden, not_built, not_found, bad_request, refused, confirmation_required, network, internal). |
| Time and formatting | `scripts/omniscience/format.ts` | Lens formatter on `Intl` (Klives's zone or UTC), `omniZonedToMs` (wall-clock → UTC with DST gap and overlap rules), ages, durations, bytes, CSV. |
| Findings in words | `scripts/omniscience/findings.ts` | One sentence per notice, monitor finding and data-health problem, written from its data (person back after a silence, first direct message, topic surge, tone toward Klives, silent source, dead jobs, …); an unknown kind shows its name. |
| Composables | `composables/useOmniscienceV3.ts` | `useOmniResource` (one panel, one request, last-good data kept on a failed refresh), `useOmniPaged` (keyset paging), `useOmniEngine` (shared status), `useOmniTime` (lens + ticking ages), `useOmniStream` (WebSocket with resume cursor, reset handling, jittered backoff), `useOmniCarry` (a set handed to a tactic), `useOmniToasts`, `useOmniAction`, `useOmniDebounced`. |
| Components | `components/OmniscienceV3/` | `Shell` (engine state, lens, nav in four groups, Ctrl+K palette, toasts), `Panel` (card bound to a request: loading / empty / error / stale), `ErrorBlock` (a guardrail refusal reads "Withheld by policy", not a failure), `Time`, `Json`, `SettingRows`, `SchemaForm`, `SelectionEditor`, `TacticRunForm`, `TacticRunView`. Built on the existing `OmniTrader*` ot-os components. |
| Overview | `index.vue` | Phase, lanes, stores, modules needing attention, LLM last hour, cache hit rate. |
| Modules | `modules.vue` | Health per module, filters, drawer with schedules, datasets, settings, enable / disable. |
| Jobs | `jobs.vue` | Server-side filters, keyset paging, drawer with timing, payload, progress, result; cancel and retry. |
| LLM | `llm.vue` | Breakdown by work class / module / purpose / model / outcome over a window, drill into the call list. |
| Settings | `settings.vue` | Typed editors validated by the server, changed filter, reset to default. |
| Tactics | `tactics.vue` | Catalogue, schema-generated run form with estimate and as-of, run list that follows the live stream, run viewer on the NDJSON stream, outputs handed to another tactic (chaining). |
| Playbooks | `playbooks.vue` | List, editor (trigger in Klives's zone, chained steps), live server validation with next-run time, run now, history. |
| Cohorts | `cohorts.vue` | List, members, hand the cohort to a tactic. |
| Explore | `explore.vue` | Dataset, measures, split-by and filters generated from dataset metadata; time bucket and lens; as-of; result table and ranking; drill-down drawer; explain; CSV of the loaded rows; hand the result to a tactic. All view state is in the URL. |
| Live | `live.vue` | The event stream as a feed: topic filter, search, pause without loss, gap notice. |
| People | `people/index.vue` | The directory (`GET people`): name search, tier, platform, sort, keyset paging, tone bar; people under 18 (or who may be) are marked. |
| Person | `people/[id].vue` | The dossier read model (`rm?model=person.dossier`): activity, per-day series, hours (never for minors), zone, who they talk with, where, facts; the brief (`briefs/person`, write one on request); relationships; similar people (refused for minors and analysis-off people, said so); what they share (`links/person`); hand the person to a tactic; a link to data controls. Analysis-off and stale evidence are banners. |
| Signals | `signals.vue` | Tabs: alerts (state and kind filters, right/wrong verdicts that teach the classifier, dismiss, mark all seen, a drawer with every hit), the reply inbox (snooze a day, dismiss, reopen), notices (in words, mark seen, dismiss), watch rules (create, edit, on/off, delete), check-in suggestions (reached out, dismiss). Unseen counts on the tabs. |
| Digests | `digests.vue` | The list by scope; a digest with its overall points, sections, children, notable items and new facts, every point with its citation count; "mark as read" (the next digest starts there); write one now for everything, a server, a chat or a person over a chosen window, followed until it is written; subscriptions (daily or weekly, time in Klives's zone). |
| Communities | `communities.vue` | Places (N13): the busiest servers and group chats, each with its trend, who carries it, who is rising, tone against its usual, what it says about Klives, what changed, factions, channels and coverage; rebuild one. Circles (N12) with bridges. The people map (N16) as an SVG scatter, coloured by circle, sized by messages, each point linking to the person. |
| Links | `links.vue` | Search shared links by words, kind, topic and site; private links are marked and never looked up; follow a link or a site: first share, who passed it on, where, when, every share. |
| Data controls | `data.vue` | Forget a person at either level (switch analysis off, or erase everything with or without Klives's side of their direct chats): a preview with counts and notes, a typed confirmation word (FORGET or ERASE), the run followed to its end. The analysis-off list with "switch back on". Retention per stream. Encrypted backups: list, back up now, restore into a folder, export the key under a passphrase, import a key. Export one person as JSON or Markdown (needs `data.export`). |
| Health | `health.vue` | Open data-health problems in words with links to jobs or model calls, resolved ones, the monitors (person activity, topic surge) with on/off and delete and a topic's 28-day status, what the monitors found, translation status. Polls every minute. |
| Registration | `nuxt.config.ts`, `scripts/pageAccess.ts`, `layouts/navbar.vue`, `Shell.vue` | `ssr:false` rules, access rules (playbooks `tactics.run`; cohorts, people, communities, signals `persons.read`; links, digests `messages.read`; health `overview.view`; data controls `engine.manage`), nav groups Work / People / Watch / Engine. |

Rules followed: instants are epoch ms and only formatted in `format.ts`; every list is keyset-paged; nothing is computed in the
browser beyond sums of what the server returned; every panel names its state (never a blank widget); every action is gated with
`useAccess` (alert, inbox, notice, watch, check-in and monitor actions need `alerts.manage`; briefs and digests `analysis.run`;
rebuilding a community page and every data control `engine.manage`); unknown values render as "unknown", not zero; nothing
person-targeted is shown about someone under 18 (the server withholds it; the pages say so instead of showing an empty space).

## Verification
2026-11-09 (slice 1):
- `npx vue-tsc --noEmit`: no errors in v3 files. Two errors remain, both on `master` already and not ours:
  `pages/klivelink/[agentId]/[[tab]].vue(519)` and `pages/schemery/omniblox/index.vue(43)`.
- `npm run build`: succeeds.
- `npx playwright test tests/e2e/omniscience-v3.spec.ts --project=chromium`: 18 passed.
- Legacy `omniscience.spec.ts` and `access-control.spec.ts`: 16 passed.

2026-11-12 (slice 2):
- `npx vue-tsc --noEmit`: only the same two errors from `master`; the new pages, the domain mock and the spec type-check.
- `npm run build`: succeeds.
- `npx playwright test tests/e2e/omniscience-v3-domain.spec.ts tests/e2e/omniscience-v3.spec.ts --project=chromium`: 27 passed
  (9 new, 18 from slice 1). The domain mock is `tests/e2e/fixtures/omniscience-v3-domain.ts`: typed against the contract,
  synthetic people only, plugged into the engine mock so an unknown route still fails the test.

- **Not verified against a running backend.** Nothing here has been clicked through against a real `OmniscienceV3` service.

Running the tests: let Playwright start the dev server from the worktree on its own port —
`PLAYWRIGHT_PORT=4199 npx playwright test tests/e2e/omniscience-v3*.spec.ts --project=chromium` (the default 4173 may be the
main folder's server, and the session's preview tool starts the main folder's site, not the worktree's). Within a test, only
the first navigation is a full load (`go()` in the specs): repeated full loads of the dev server exhaust socket buffers on Windows.

## Not built, and why
- **Pages that need WS-A, WS-B or WS-D DTOs** — conversations and the message view, search, ask, faces, the knowledge graph and
  review queues, replicas, sources and their backfills: their routes are not in WS-C's contract.
- **Citations open nothing yet.** Briefs and digests cite message ids; there is no v3 route that returns messages by id, so a
  point shows how many messages back it, not the messages.
- **Primitives that only those pages need** — context-menu tactics on a selection, jobs / tactics tray, virtualised list with
  multi-select, saved-views manager, network graph (cytoscape), image viewer with face boxes, dataset-driven global filter bar
  shared across pages, the global as-of picker.
- **Ask bar** (natural language → tactic + parameters): no route.
- **Outbound tactics** (X77–X81) cannot be started: see contract gap 2. The UI says so and disables Run.

## Contract gaps for the other workstreams and Phase 3
1. **Read-model bodies need contract types.** Mark each read model's body record `[ApiContract]` so it lands in
   `api-contracts.ts`; WS-E builds the domain pages on those types. `person.dossier` is now typed (`DossierDto`); the page casts
   `ReadModelDto.body` to it, because `rm` itself still answers `body: unknown`.
2. **Outbound confirmation has no route.** `POST tactics/run` answers `confirmation_required` when `confirmationToken` is
   missing, but nothing issues a token. Needed: a preview route that returns what would be sent plus a token (`OutboundPolicy`).
3. **Explore drill-down by time bucket.** The UI narrows a drilled row by its split dimensions (`narrow` filters). A time-bucket
   column is not a dimension id, and the browser cannot compute bucket bounds on the "each person's clock" lens. Needed from
   WS-C: either a per-row drill token, or `[fromMs, toMs)` bounds per bucket row in `ExploreResult`.
4. **Explore column kinds.** The UI treats a column as a measure when its id is a requested measure, and as an instant when
   `kind` is `Time` and the value is larger than 1e10. WS-C should fix the vocabulary of `ExploreColumn.kind` in the contract.
5. **`explore/facets` and `explore/export`** exist on WS-C's branch now; the Explore page still types filter values and exports
   the loaded rows. Wire them after the merge.
6. **Work-class names** logged in `llm_calls` are not an enum in the contract; the LLM page offers only classes it has seen.
7. **e2e permission fixture.** `tests/e2e/fixtures/permissions.ts` is generated from the backend's permission sets and does not
   have the v3 keys; the v3 mock adds them itself. Regenerate the fixture once `OmniscienceV3Perms` is on `master`.
8. **Finding payloads are untyped.** `NoticeDto.data`, `MonitorEventDto.data`, `HealthIssueDto.detail` and
   `MonitorRuleDto.definition` are `unknown`; `findings.ts` writes sentences from the keys the modules use today. Either type each
   kind (`[ApiContract]` records) or send the sentence, so a renamed key cannot silently degrade a notice to its kind's name.
9. **No pickers for places.** Watch rules, digests and subscriptions take server, chat and person ids typed by hand. Needed: a
   lookup route (guilds, conversations and people by name, minor-filtered) for pickers.
10. **Messages by id.** See "Citations open nothing yet": `messages/get?ids=` (minor- and analysis-off-aware) would let every
    cited point, alert hit and inbox item open its message.

## At cutover
Rename `pages/omniscience-v3/` to `pages/omniscience/` and `OMNI_BASE` in `scripts/omniscience/api.ts`; update the
registration files; delete the legacy pages, `composables/useOmniscienceApi.ts`, `components/Omniscience/`,
`assets/scss/omniscience.scss` (F064) and the legacy spec and fixture.
