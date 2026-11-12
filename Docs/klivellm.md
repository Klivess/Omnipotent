# KliveLLM: routing, the call ledger and the KliveLLM page

KliveLLM is the one place every language-model call in Omnipotent goes through. Since November 2026 it
knows who is calling, can send each service (and each part of a service) to its own router and model,
records every call, measures the routers, and prices what it all costs. The KM website shows and controls
all of it on `/klivellm` (Ops ▸ KliveLLM).

Words: a **router** is the endpoint KliveLLM posts to (OpenRouter, AIRouter, HuggingFace, the custom
endpoint, the local model, or one added from the page). A **provider** is whoever ran the model upstream
(only OpenRouter reports it). A **lane** is one part of a service with its own traffic (Projects ›
commander, KliveAgent › chat…). A **subject** is the caller's own grouping key (a project id, a game id).

## Who is calling

`Routing/LlmCaller.cs`. Every entry point (`QueryLLM`, `StartToolSession`, `QueryToolSessionAsync`,
`WarmToolSessionPrefixAsync`, the probes, `AIRouterAudioClient`) takes an optional `LlmCaller(Service,
Lane, Subject?, Operation?)`. Without one, `[CallerFilePath]` and `LlmConsumerCatalog.PathRules` name the
lane from the calling file, so Omniscience and KliveBot needed no edits. `LlmConsumerCatalog.Lanes` lists
every lane with its label, AIRouter work class, whether it needs tool calling, its default spending, where
its own model setting lives, and a fixed router if it has one (KliveAgent › speech is always AIRouter).
`KliveLLMRoutingTests.EveryCallSite_ResolvesToACataloguedLane` fails if a new call site falls through.

## How a route is chosen

`Routing/LlmRouteResolver.cs` (pure; the page shows its reasons as "How this route was chosen"):

1. A lane's fixed router, if it has one.
2. The emergency override, while it lasts (optionally only for some services).
3. The lane's own router, then its service's router.
4. A free-model call (`useFreeModel`) with nothing set: OpenRouter's free model, as before.
5. The default route's router, then the `RemoteLLMProvider` setting (an empty document routes everything
   exactly as before; `KliveLLMRoutedCallTests` checks the request bytes are unchanged).

Model: a **pinned** model wins; otherwise the caller's own model if the router serves it (Projects'
per-project routes, `KliveAgent_FastModel`, `StratumEngineerModelID`…); then the route's model; then the
router's default. A level whose router differs from the one chosen does not lend its model.

Guards, in order: paused → budget → spending → router enabled and keyed → tool calling available. A refused
call fails before anything is sent, with `RemoteLLMFailureKind` `Paused`, `BudgetExceeded` or
`SpendingNotAllowed` (not retryable; Projects defers the wake with a plain blocker).

**Spending** (per service or lane):

| Value | Meaning |
|---|---|
| `Never` | Free and flat-fee routers only, as primary and as fallback (OmniBlox, Omniscience's free lanes). |
| `PrimaryOnly` (default) | May be routed to a per-token router, but never *falls* from a free or flat-fee router onto one. |
| `Anywhere` | Falls back onto per-token routers too. |

**Fallback**: tried only after the primary's own retries end in a router-side failure (network, timeout,
5xx, long rate limit, no credit, model unavailable); never for a bad request, a cancel, or a stream that has
already produced output. After repeated failures a per-lane circuit skips the router (2 min, doubling to
15 min, one half-open probe), so later calls do not pay the retry chain first. A session whose router
changes rotates its cache epoch (its prefix restarts once on the new router).

**Budgets**: daily and monthly, in USD or tokens, per service or lane (UTC days and months). At 80% the page
flags it; at 100% the budget's action applies: `warn`, `fallback` (to a free or flat-fee fallback, refused
if there is none) or `block`.

The routing document is `SavedData/KliveLLM/routing.json` (revisioned, optimistic concurrency: an apply
against an older revision is refused with 409) with `routing-history.jsonl` beside it. Added routers live in
the document; their keys are sensitive OmniSettings `KliveLLM_RouterKey_<id>` and no route returns a key.

## The call ledger

`Telemetry/`. One record per call (with its attempts) is handed to a bounded channel on the request path;
a single writer thread does the rest (drops are counted, never block). SQLite in WAL mode,
`SavedData/KliveLLM/Telemetry/klivellm.db`:

- `calls`: every call (service, lane, subject, session, router, provider, models asked and served, outcome,
  attempts, tokens incl. cached / cache-write / reasoning, cost reported, timings, flags, routing revision).
- `agg_1m`, `agg_1h`, `agg_1d`: rollups keyed by time × service × lane × router × provider × model × kind,
  with log histograms for latency and sizes; `subject_1d`; `router_1m` / `router_1h` (health and gauges);
  `marks` (routing changes, restarts, the Projects import).
- Retention: calls 90 days (`KliveLLM_LedgerCallRetentionDays`, 7–730), minute rollups 14 days, hourly
  400 days, daily forever. Queries are time-bounded, index-driven and keyset-paged.
- Kept text (opt-in, per service or lane, for 1 h / 24 h / 3 days, self-expiring):
  `klivellm-content.db`, gzipped, images and audio replaced by size markers, 2 MB a call, 2 GB in all,
  `KliveLLM_KeptTextRetentionDays` (default 3, max 14). Readable only with `klivellm.content.read`;
  every read is logged.
- On first start the Projects usage journals are imported (records before the ledger's first live call),
  so Projects has history from day one.

Costs are computed when read, from the price book (`SavedData/KliveLLM/prices.json`, filled from
OpenRouter's catalogue and editable): reported cost where the router gives one, else tokens × price; flat-fee
and free routers cost nothing per token, and their *list-price equivalent* is shown beside.

## Router health

Passive: every attempt's outcome. A minute is down when half or more of its attempts failed on the router's
side, degraded when any did or the router refused (rate limit, key, credit), up otherwise; our own bad
requests never count. Active: a model-list read per keyed router once a minute (`KliveLLM_RouterProbes`, no
tokens), and OpenRouter's `/key` for credit. Gauges every 10 s: in flight and queued per router, and
AIRouter's slots, queue, windows, cool-off, warm cohort, measured prefix lifetime and rolling cache hit.

## Routes and permissions

All under `/klivellm`, typed KliveAPI routes, never response-cached. Permissions (Klives rank):
`klivellm.overview.view`, `analytics.read`, `calls.read`, `content.read` (sensitive), `routing.manage`,
`routers.manage` (critical), `prices.manage`, `data.export`, `settings.manage` (critical).

- Read: `overview`, `live`, `series`, `breakdown`, `distribution`, `heatmap`, `simulator` (token counts only;
  the page multiplies prices), `calls`, `call`, `call/content`, `routers`, `router`, `routing`,
  `routing/history`, `prices`, `settings`, `export` (CSV).
- Write: `routing/preview` (saves nothing), `routing/apply`, `routing/revert`, `routing/override`,
  `service/pause`, `budgets/save`, `content/capture`, `circuits/reset`, `routers/save`, `routers/delete`,
  `routers/key`, `routers/test`, `prices/save`, `prices/refresh`, `settings`.

## The page

`pages/klivellm.vue` → `components/Llm/Console.vue`; state in `composables/useKliveLlm.ts`, pure rules in
`scripts/kliveLlm.ts`, styles in `assets/scss/klivellm.scss` (`.llx`, `.llx-layer`). Live state every 5 s,
the overview plus the open tab's reads in one `/batch` every 20 s, routing and routers every 60 s, nothing
while the tab is hidden. Tabs: Overview (verdict, figures, routing map, in flight), Services, Routing (the
editable matrix, preview, apply, history, override, skipped routers), Routers (cards, add/edit, keys,
tests, AIRouter envelope, detail sheet), Analytics (explorer, small multiples, distributions, weekday ×
hour), Costs (spend, router economics, budgets, simulator, forecast, price book), Calls (the log and each
call's attempts and kept text), Insights. End-to-end tests: `tests/e2e/klivellm.spec.ts` against the fake
in `tests/e2e/fixtures/klivellm-api.ts`.
