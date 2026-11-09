# WS-E — Website UI (progress)

Repository: `klives-management-website`, branch `omniscience-v3`, worktree
`C:\Projects\Websites\klives-management-website\.claude\worktrees\omniscience-v3` (never the main folder, which stays on `master`).
`node_modules` in the worktree is a junction to the main folder's. Charter: [workstreams.md](workstreams.md).

## Scope of this first slice
WS-E can only build against DTOs that exist. On 2026-11-09 the frozen contract (`api-contracts.ts`) covers the **engine and
kernel** — status, modules, jobs, LLM calls, settings, live stream, tactics, runs, selections, cohorts, playbooks, read models
(with an untyped `body`) and Explore — and nothing person-shaped. So this slice is the foundation plus every page the frozen
contract can drive. The domain pages wait for the DTOs of WS-A to WS-D (list under "Not built").

## Built
Pages live under `pages/omniscience-v3/` (routes `/omniscience-v3/*`) so the legacy pages keep working until cutover, mirroring
the backend's `OmniscienceV3` folder. `pages/omniscience/**` is not usable yet: a folder beside `pages/omniscience.vue` would
turn the legacy page into a parent route.

| Piece | Where | What |
|---|---|---|
| Contract types | `scripts/omniscience/contracts.ts` | A copy of `api-contracts.ts`. Re-copy when the backend contract changes. |
| Data client | `scripts/omniscience/api.ts` | `omniGet` / `omniPost` / `omniStream` (NDJSON) / `omniStreamUrl`. One client, no retry layer of its own, an abort signal on every call, errors classified as `OmniApiError.kind` (disabled, starting, failed, unavailable, forbidden, not_built, not_found, bad_request, refused, confirmation_required, network, internal). |
| Time and formatting | `scripts/omniscience/format.ts` | Lens formatter on `Intl` (Klives's zone or UTC), `omniZonedToMs` (wall-clock → UTC with DST gap and overlap rules), ages, durations, bytes, CSV. |
| Composables | `composables/useOmniscienceV3.ts` | `useOmniResource` (one panel, one request, last-good data kept on a failed refresh), `useOmniPaged` (keyset paging), `useOmniEngine` (shared status), `useOmniTime` (lens + ticking ages), `useOmniStream` (WebSocket with resume cursor, reset handling, jittered backoff), `useOmniCarry` (a set handed to a tactic), `useOmniToasts`, `useOmniAction`, `useOmniDebounced`. |
| Components | `components/OmniscienceV3/` | `Shell` (engine state, lens, nav, Ctrl+K palette, toasts), `Panel` (card bound to a request: loading / empty / error / stale), `ErrorBlock`, `Time`, `Json`, `SettingRows`, `SchemaForm` (form generated from a tactic's JSON Schema), `SelectionEditor`, `TacticRunForm`, `TacticRunView`. Built on the existing `OmniTrader*` ot-os components. |
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
| Registration | `nuxt.config.ts`, `scripts/pageAccess.ts`, `layouts/navbar.vue` | `ssr:false` rules, access rules (playbooks need `tactics.run`, cohorts `persons.read`), nav entry "Omniscience v3". |

Rules followed: instants are epoch ms and only formatted in `format.ts`; every list is keyset-paged; nothing is computed in the
browser beyond sums of what the server returned; every panel names its state (never a blank widget); every action is gated with
`useAccess`; unknown values render as "unknown", not zero.

## Verification (2026-11-09)
- `npx vue-tsc --noEmit`: no errors in v3 files. Two errors remain, both on `master` already and not ours:
  `pages/klivelink/[agentId]/[[tab]].vue(519)` and `pages/schemery/omniblox/index.vue(43)`.
- `npm run build`: succeeds.
- `npx playwright test tests/e2e/omniscience-v3.spec.ts --project=chromium`: 18 passed. The mock
  (`tests/e2e/fixtures/omniscience-v3-api.ts`) is typed against the contract, uses synthetic data only, and records any v3 route
  it does not know; every test asserts that list is empty.
- Legacy `omniscience.spec.ts` and `access-control.spec.ts`: 16 passed.
- **Not verified against a running backend.** Nothing here has been clicked through against a real `OmniscienceV3` service.

Running the tests: start the dev server from the worktree on its own port (the default 4173 may be the main folder's server) —
`npm run dev -- --host 127.0.0.1 --port 4199`, then `PLAYWRIGHT_BASE_URL=http://127.0.0.1:4199 npx playwright test …`.
The first start takes about two minutes, longer than Playwright's own web-server timeout. Within a test, only the first
navigation is a full load (`go()` in the spec): repeated full loads of the dev server exhaust socket buffers on Windows.

## Not built, and why
- **Domain pages** — people, person dossier, conversations, search, ask, faces, graph, review, replicas, radar, digests, sources:
  no routes or DTOs exist for them yet. They are served through read models whose `body` is `unknown` in the contract.
- **Primitives that only those pages need** — context-menu tactics on a selection, jobs / tactics tray, virtualised list with
  multi-select, saved-views manager, network graph (cytoscape), image viewer with face boxes, dataset-driven global filter bar
  shared across pages, the global as-of picker.
- **Ask bar** (natural language → tactic + parameters): no route.
- **Outbound tactics** (X77–X81) cannot be started: see contract gap 2. The UI says so and disables Run.

## Contract gaps for the other workstreams and Phase 3
1. **Read-model bodies need contract types.** Mark each read model's body record `[ApiContract]` so it lands in
   `api-contracts.ts`; WS-E builds the domain pages on those types. The same goes for every new route DTO.
2. **Outbound confirmation has no route.** `POST tactics/run` answers `confirmation_required` when `confirmationToken` is
   missing, but nothing issues a token. Needed: a preview route that returns what would be sent plus a token (`OutboundPolicy`).
3. **Explore drill-down by time bucket.** The UI narrows a drilled row by its split dimensions (`narrow` filters). A time-bucket
   column is not a dimension id, and the browser cannot compute bucket bounds on the "each person's clock" lens. Needed from
   WS-C: either a per-row drill token, or `[fromMs, toMs)` bounds per bucket row in `ExploreResult`.
4. **Explore column kinds.** The UI treats a column as a measure when its id is a requested measure, and as an instant when
   `kind` is `Time` and the value is larger than 1e10. WS-C should fix the vocabulary of `ExploreColumn.kind` in the contract.
5. **`explore/facets` and `explore/export`** (plan D4) do not exist: filter values are typed, not picked, and export is the
   loaded rows as CSV in the browser, gated by `data.export`.
6. **Work-class names** logged in `llm_calls` are not an enum in the contract; the LLM page offers only classes it has seen.
7. **e2e permission fixture.** `tests/e2e/fixtures/permissions.ts` is generated from the backend's permission sets and does not
   have the v3 keys; the v3 mock adds them itself. Regenerate the fixture once `OmniscienceV3Perms` is on `master`.

## At cutover
Rename `pages/omniscience-v3/` to `pages/omniscience/` and `OMNI_BASE` in `scripts/omniscience/api.ts`; update the three
registration files; delete the legacy pages, `composables/useOmniscienceApi.ts`, `components/Omniscience/`,
`assets/scss/omniscience.scss` (F064) and the legacy spec and fixture.
