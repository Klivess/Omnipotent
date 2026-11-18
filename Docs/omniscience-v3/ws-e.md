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
4. **2026-11-14, the pages for those routes** (`74aec4a`, `29d1eb4`): what is known about a person, the review queue,
   asking, the agenda, the Quality Lab, replicas, sources and imports, and image search. Two small backend additions went
   with them (`7be4506`): `GET people/names` and contract types for the Quality Lab's read model.
5. **2026-11-15, its own design** (uncommitted in the worktree at the time of writing). Klives asked for the Omniscience
   pages to stop using a shared template ("Do NOT use a template for the UI like the other pages", then "make sure youre
   editing omniscience v3"). The pages no longer borrow OmniTrader's design system: one stylesheet and six components of
   v3's own replace it, the frame and the first page are new, and the people directory gained a quick look and a
   comparison. No backend change, and no live data: the server was down that day.

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
| Composables | `composables/useOmniscienceV3.ts` | `useOmniResource` (one panel, one request, last-good data kept on a failed refresh), `useOmniPaged` (keyset paging), `useOmniEngine` (shared status), `useOmniTime` (lens + ticking ages), `useOmniStream` (WebSocket with resume cursor, reset handling, jittered backoff), `useOmniCarry` (a set handed to a tactic), `useOmniToasts`, `useOmniAction`, `useOmniDebounced`, `useOmniUrlState` (filters and tabs kept in the link, each instance owning only its own keys; it replaced OmniTrader's `useUrlState`), `useOmniRecent` (the people opened last, kept in this browser only). |
| Design | `assets/scss/omniscience-v3.scss` | Omniscience's own surface since 2026-11-15, loaded by `Shell.vue` and by nothing else. Two roots: `.omni-v3` (the frame) and `.omni-layer` (what teleports to `<body>`: drawers, the palette, toasts). Classes and variables are `omni-*` / `--omni-*`. Component rules sit inside `:where()`, so they weigh one class and a page's scoped style always wins; classes used on a `<button>` are repeated at the roots' full weight, because the site-wide `button:hover` rule would otherwise repaint them. The frame is a size container (`container: omni / inline-size`): the narrow-layout rules ask how wide the frame is, not the window, because the site's own sidebar takes 56 to 220 px of the window. Near-black planes, one accent (iris), colour otherwise only for meaning, always beside a word. |
| Components | `components/OmniscienceV3/` | `Shell` (engine state, lens, navigation in two levels: Brief and five areas, then the pages of the area you are in or point at; a Ctrl+K palette that finds pages, people by name, the people opened last and, once something is typed, tactics; toasts), `Panel` (card bound to a request: loading / empty / error / stale), `ErrorBlock` (a guardrail refusal reads "Withheld by policy", not a failure), `Time`, `Json`, `SettingRows`, `SchemaForm`, `SelectionEditor`, `TacticRunForm`, `TacticRunView`. Its own primitives since 2026-11-15: `Card`, `Kpi`, `Drawer` (modal, or modeless for a quick look that leaves the list usable), `StateBlock`, `DataTable`, `BarList`. Nothing is imported from `components/OmniTrader` or `useOmniTrader` any more. |
| Brief | `index.vue` | The first page since 2026-11-15. A "Needs you" list, worst first, each line leading to where it is dealt with: LLM calls failing, dead jobs, open data-health problems, overdue plans, messages waiting on a reply, new alerts, possible same-person links, review items, new notices. Six tiles, the reply inbox, the latest digest, what is coming up, and people (opened recently, then the tracked people who spoke last). It reads what other pages own (`signals/inbox`, `signals/alerts`, `signals/notices`, `knowledge/review`, `knowledge/identity-links`, `knowledge/agenda`, `monitors/health`, `digests`, `people`) every two minutes while visible, computes nothing beyond counting, and leaves out what the profile may not read. |
| Engine overview | `engine.vue` | Phase, lanes, stores, modules needing attention, LLM last hour, cache hit rate. It was the first page until 2026-11-15 and is now `/omniscience-v3/engine`. |
| Modules | `modules.vue` | Health per module, filters, drawer with schedules, datasets, settings, enable / disable. |
| Jobs | `jobs.vue` | Server-side filters, keyset paging, drawer with timing, payload, progress, result; cancel and retry. |
| LLM | `llm.vue` | Breakdown by work class / module / purpose / model / outcome over a window, drill into the call list. |
| Settings | `settings.vue` | Typed editors validated by the server, changed filter, reset to default. |
| Tactics | `tactics.vue` | Catalogue, schema-generated run form with estimate and as-of, run list that follows the live stream, run viewer on the NDJSON stream, outputs handed to another tactic (chaining). |
| Playbooks | `playbooks.vue` | List, editor (trigger in Klives's zone, chained steps), live server validation with next-run time, run now, history. |
| Cohorts | `cohorts.vue` | List, members, hand the cohort to a tactic. |
| Explore | `explore.vue` | Dataset, measures, split-by and filters generated from dataset metadata; time bucket and lens; as-of; result table and ranking; drill-down drawer; explain; CSV of the loaded rows; hand the result to a tactic. All view state is in the URL. |
| Live | `live.vue` | The event stream as a feed: topic filter, search, pause without loss, gap notice. |
| People | `people/index.vue` | The directory (`GET people`): name search, tier, platform, sort, keyset paging, tone bar; people under 18 (or who may be) are marked. Since 2026-11-15: a row opens a quick look beside the list (`PersonPeek.vue`, the dossier read model, modeless) and the name opens the page; two picked people are compared (`PairCompare.vue`: both cards, then `insights/pair` with interactions, closeness, reciprocity, tension, messages a week each way and who answers faster; a pair the engine does not track says so instead of failing); `/` focuses the search. |
| Person | `people/[id].vue` | The dossier read model (`rm?model=person.dossier`): activity, per-day series, hours (never for minors), zone, who they talk with, where, facts; the brief (`briefs/person`, write one on request); relationships; similar people (refused for minors and analysis-off people, said so); what they share (`links/person`); hand the person to a tactic; a link to data controls. Analysis-off and stale evidence are banners. |
| Signals | `signals.vue` | Tabs: alerts (state and kind filters, right/wrong verdicts that teach the classifier, dismiss, mark all seen, a drawer with every hit), the reply inbox (snooze a day, dismiss, reopen), notices (in words, mark seen, dismiss), watch rules (create, edit, on/off, delete), check-in suggestions (reached out, dismiss). Unseen counts on the tabs. |
| Digests | `digests.vue` | The list by scope; a digest with its overall points, sections, children, notable items and new facts, every point with its citation count; "mark as read" (the next digest starts there); write one now for everything, a server, a chat or a person over a chosen window, followed until it is written; subscriptions (daily or weekly, time in Klives's zone). |
| Communities | `communities.vue` | Places (N13): the busiest servers and group chats, each with its trend, who carries it, who is rising, tone against its usual, what it says about Klives, what changed, factions, channels and coverage; rebuild one. Circles (N12) with bridges. The people map (N16) as an SVG scatter, coloured by circle, sized by messages, each point linking to the person. |
| Links | `links.vue` | Search shared links by words, kind, topic and site; private links are marked and never looked up; follow a link or a site: first share, who passed it on, where, when, every share. |
| Data controls | `data.vue` | Forget a person at either level (switch analysis off, or erase everything with or without Klives's side of their direct chats): a preview with counts and notes, a typed confirmation word (FORGET or ERASE), the run followed to its end. The analysis-off list with "switch back on". Retention per stream. Encrypted backups: list, back up now, restore into a folder, export the key under a passphrase, import a key. Export one person as JSON or Markdown (needs `data.export`). |
| Person: what is known | `people/[id].vue` (tabs), `PersonKnowledge.vue`, `FactDrawer.vue` | The knowledge profile (`knowledge/profile`): stated facts by category with status, confidence, the period they held ("about" when it is only known as a range) and how it is known; what was only inferred, labelled as such; open guesses; the names people use for them (confirm, reject); what they mention; look back (`knowledge/facts`, `PersonLookBack.vue`): what was true on a date as known today, the same going only by what had been said by then, what was on record that day, and the facts the list leaves out (still being checked, or rejected), with the view kept in the link; the age band with its statements (confirm, reject) and what follows from it. A fact opens with its quoted evidence, its earlier versions and Klives's own decision; confirm, reject or correct it. "Read their messages again now". |
| Person: timeline, audit | `PersonTimeline.vue`, `PersonAudit.vue`, `PlanList.vue` | Dated life events with the range each could have happened in, and their plans. How it was worked out: reading runs, claims with the reason each was rejected, and merges with undo. |
| Ask | `ask.vue`, `InquiryReport.vue`, `InquiryEvidence.vue` | A question with optional people, an as-of time and "keep watching"; a preview of how it would be answered (`knowledge/ask/plan`); the run followed live, then the answer: verdict, probability with its band, evidence for and against in the people's own words, what was read and left out, what would change it. Right/wrong marks. Past answers with a "Which person did you mean?" hand-back. Questions being watched, on and off. |
| Review | `review.vue` | The queue by state, kind and order, each kind explained and pointed at where it is decided; settle or dismiss. "Same person?": suggested identity links with their independent signs; a yes merges only after a confirmation and with `sources.manage`, and can be undone. Merge two people by hand. |
| Agenda | `agenda.vue` | Coming up, overdue and Klives's own promises from the agenda read model; every recorded plan with filters. |
| Quality Lab | `quality.vue`, `QualityTable.vue` | The gate decision on the extraction prompt in use, measurements against Klives's decisions and against the planted-fact corpus with their intervals; start either measurement. |
| Replicas | `replicas/index.vue`, `replicas/[id].vue`, `ReplicaChat.vue` | Status and the list; what there is to learn from, the version in use and how often a judge tells it apart, a build with its stages, auto-rebuild; chats with replies streamed as they are written (`omniPostStream`), every written line marked as a simulation, ratings; versions with style, periods and the settings tried; test replies beside the real ones; a blind test, named in the link so a reload carries on (`replica/blind/session`), carried on or looked at again from the results, and showing which reply was which only for pairs that were answered. Group simulations. Someone under 18 has no replica and the page says so. |
| Sources | `sources.vue` | The Discord connection and reading a channel's history now; the import inbox: upload (`omniUpload`, a streamed PUT), "look inside" (a job, followed to its result), the question it cannot answer by itself (a WhatsApp export's time zone), import, and what was imported before. |
| Images | `images.vue`, `Media.vue` | Search by description with poster, conversation and date filters; thumbnails loaded as blobs (the media routes need the auth header); a drawer with the image, its caption, tags, the text in it and why it was found. |
| Health | `health.vue` | Open data-health problems in words with links to jobs or model calls, resolved ones, the monitors (person activity, topic surge) with on/off and delete and a topic's 28-day status, what the monitors found, translation status. Polls every minute. |
| Registration | `nuxt.config.ts`, `scripts/pageAccess.ts`, `layouts/navbar.vue`, `Shell.vue` | `ssr:false` rules, access rules (playbooks `tactics.run`; cohorts, people, communities, signals, ask, review, agenda `persons.read`; links, digests, images `messages.read`; replicas `replica.read`; sources `sources.view`; health, quality `overview.view`; data controls `engine.manage`), navigation: Brief, then the areas Work / People / Knowledge / Watch / Engine. The Brief and the engine overview fall under the general `/omniscience-v3` rule. |

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

2026-11-14 (slice 3):
- `npx vue-tsc --noEmit`: only the same two errors from `master`.
- `npx playwright test tests/e2e/omniscience-v3*.spec.ts --project=chromium`: 37 passed (10 new in
  `omniscience-v3-knowledge.spec.ts`; its mock is `tests/e2e/fixtures/omniscience-v3-knowledge.ts`, with the server's own
  value sets: fact statuses, inquiry states, job states, replica statuses).
- Every first page load of the three specs goes through `loadPage` (in the shared fixture). With several dev servers
  running, one test in 37 failed per run, a different one each time. Either a module request failed
  (`net::ERR_NO_BUFFER_SPACE`), or one was never answered at all, so the page never finished loading; both leave a blank
  page that the test would wait on until it timed out. `loadPage` waits for the app to mount (not for the load event),
  for at most 35 s, and loads the page again when it has not, up to three times.
- With the look-back panel and the blind-test changes added (later the same day): the same command, 37 passed (the
  look-back steps are part of the two person tests, the blind-test steps part of the replica test).
- `npm run build`: succeeds.
- Routes against pages: a script compared every route the backend registers with what the pages call. Every route of the
  knowledge, review, inquiry, replica, source, import and media groups is called by a page. Two were not, and are now:
  `knowledge/facts` (look back) and `replica/blind/session` (carry a blind test on). The ones still without a page are
  listed under "Not built".

2026-11-15 (own design):
- `npx vue-tsc --noEmit` after `npx nuxi prepare` (so the new components are typed): only the same two errors from `master`.
- `npx playwright test tests/e2e/omniscience-v3*.spec.ts --project=chromium`: 40 passed, on each of the last three runs, the last one on the final stylesheet (3 new in
  `omniscience-v3-domain.spec.ts`: the Brief, navigation with the palette's people search, comparing two people; the mock
  gained `GET insights/pair`). The first run after the new components were added lost three old tests (digests, data
  controls, tactics) to an error page whose stack named two copies of the Vue runtime (`?v=1f0a2a29` and `?v=7bbabe3b`):
  Vite re-optimising dependencies while the tests ran. A second run on the warm server is the check.
- `npm run build`: succeeds (run again on the final stylesheet).
- Screenshots at 1600 px looked at: the Brief, the engine overview, a person page, the quick look, the comparison, a
  tactic run. Found and fixed that way: the quick look dimmed the list it is meant to keep in view (now modeless), cards
  that follow one another outside a grid touched, and a chart of three weeks drew bars 120 px wide.
- Narrow widths looked at with a throwaway spec (windows of 390, 760 and 1000 px; the Brief, people, engine, a person,
  signals, jobs). Three defects, all fixed: the top bar ran 148 px off a 390 px screen on every page; segmented controls
  (sort, tabs) were wider than the screen; a table written straight into a card widened the page (person, signals). The
  frame is now a size container and the narrow-layout rules use its own width; a segmented control scrolls sideways
  within its row; such a table scrolls inside its card. After that: no sideways overflow on any of the six pages at
  390 px, nor at 760 and 1000 px, and the quick look opens at a phone's width.
- A script listed every `omni-*` class a page uses that nothing defines: only hooks without styles (test ids, markers)
  are left.
- The renaming itself was done by script over 56 files (tags, classes, variables, the link-state import), then read: no
  `OmniTrader`, `ot-` or `useOmniTrader` is left in `pages/omniscience-v3`, `components/OmniscienceV3` or the v3 specs.

- **Not verified against a running backend.** Nothing here has been clicked through against a real `OmniscienceV3` service.

Running the tests: let Playwright start the dev server from the worktree on its own port —
`PLAYWRIGHT_PORT=4199 npx playwright test tests/e2e/omniscience-v3*.spec.ts --project=chromium` (the default 4173 may be the
main folder's server, and the session's preview tool starts the main folder's site, not the worktree's). Within a test, only
the first navigation is a full load (`go()` in the specs): repeated full loads of the dev server exhaust socket buffers on Windows.

## Not built, and why
- **Conversations and the message view, message search, faces, the knowledge graph.** There is no route that returns
  messages (see gap 10), faces are not built on the backend, and no graph route exists.
- **Citations open nothing yet.** Briefs and digests cite message ids; there is no v3 route that returns messages by id, so a
  point shows how many messages back it, not the messages.
- **Primitives that only those pages need** — context-menu tactics on a selection, jobs / tactics tray, virtualised list with
  multi-select, saved-views manager, network graph (cytoscape), image viewer with face boxes, dataset-driven global filter bar
  shared across pages, the global as-of picker.
- **Ask bar in the shell** (natural language → tactic + parameters, from any page): the Ask page uses `knowledge/ask/plan`
  for this; the shell's palette does not.
- **Outbound tactics** (X77–X81) cannot be started: see contract gap 2. The UI says so and disables Run.
- **Routes no page calls yet** (twelve; `insights/pair` got its page on 2026-11-15, the comparison in the people
  directory): `classify/tasks`, `classify/concepts`, `classify/labels` and `classify/correct`
  (what a message was classified as, and correcting it); `bundles` and
  `bundles/export` (the transform bundles and their export); `selections/combine` (set algebra on two selections);
  `rm/catalog` (the list of read models); `events` (the event log by sequence number; the pages use the live stream);
  `translate/search` (search across translations); `explore/facets` and `explore/export` (gap 5). They belong to the
  message view, which is not built, or are small additions to pages that exist.

## Contract gaps for the other workstreams and Phase 3
1. **Read-model bodies need contract types.** Mark each read model's body record `[ApiContract]` so it lands in
   `api-contracts.ts`; WS-E builds the domain pages on those types. Typed so far: `person.dossier` (`DossierDto`),
   `knowledge.profile`, `knowledge.timeline`, `knowledge.agenda` and, since `7be4506`, `knowledge.quality` (`QualityLabDto`).
   The pages cast `ReadModelDto.body` to them, because the envelope itself still answers `body: unknown`.
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
9. **No pickers for places.** Watch rules, digests and subscriptions take server and chat ids typed by hand. People have a
   picker now (`PersonPick.vue`, on the directory search) and ids are shown as names (`GET people/names`, `useOmniNames`);
   the older pages do not use them yet. Still needed: a lookup route for servers and conversations by name.
10. **Messages by id.** See "Citations open nothing yet": `messages/get?ids=` (minor- and analysis-off-aware) would let every
    cited point, alert hit and inbox item open its message.

## At cutover
Rename `pages/omniscience-v3/` to `pages/omniscience/` and `OMNI_BASE` in `scripts/omniscience/api.ts`; update the
registration files; delete the legacy pages and what only they use (below) and the legacy spec and fixture.

The legacy pages changed after this was first written. On 2026-11-15 the live `/omniscience` pages were rebuilt on
`master` as one console with a design of its own (`.omx`; website `3032626`, pushed): `pages/omniscience.vue` is the
whole console, `pages/omniscience-{person,review,search,explore}.vue` redirect into it, with `components/Omniscience/*`,
`composables/useOmniscienceConsole.ts`, `scripts/omniscienceConsole.ts`, `assets/scss/omniscience-console.scss`,
`composables/useOmniscienceApi.ts` and `components/ReplicaChatPanel.vue`. `assets/scss/omniscience.scss` (F064) is
already gone there. All of that talks to the old `/omniscience/*` routes and goes at cutover.

This branch is 14 commits behind `master` (2026-11-18) and does not have that console, the Admin or OmniDefence
consoles, or the Projects to Demiurge rename. Merge `master` in before cutover: expect to settle `nuxt.config.ts`,
`scripts/pageAccess.ts` and `layouts/navbar.vue` by hand. The two designs are separate on purpose (`.omx` for the old
engine's console, `omni-*` here), so neither stylesheet touches the other's pages.
