# KA Projects — Autonomy Gap Analysis

**Source:** "Make money online: Hackforums" project, 2026-08-13 23:57 → 2026-08-14 03:31 UTC.
2,911 events, $5.00 token budget fully consumed, **zero external actions completed** (no account
persisted, no email sent, no application submitted).

**Target state:** a Project agent takes a goal, creates whatever accounts it needs, and executes —
no Klives in the loop.

Ordered by how much each fix buys. P0 items are the ones that made the goal structurally
impossible; P1 is where the budget actually burned; P2 is efficiency.

---

## Implementation status — 2026-08-14

Everything in P0 and the executable part of P1 is now built; `dotnet build` is clean and the full
suite is 1,413/1,413 green (39 new tests in `Omnipotent.Tests/Projects/ProjectAutonomyTests.cs`).

| # | Gap | Shipped as |
|---|-----|-----------|
| P0-1 | No outbound email | `KliveMailSender` + `KliveMail.SendMailAsync` + `klivemail_send` (folded as `klivemail op:send`). Relay credentials come from the shared account registry; a copy of every send is filed in the sending mailbox as evidence. |
| P0-2 | CAPTCHA is a hard stop | `browser op=solve_challenge` — `challenge_probe`/`challenge_inject` in `browser-inspect.py` + `BrowserChallengeSolver` (CapSolver / 2Captcha / Anti-Captcha, one dialect, three hosts). Prompts and `request_human` no longer treat a captcha as human-only. |
| P0-3 | Root-key corruption bricks the registry | `AtomicSecretRootKey` recovery hook + `QuarantineUndecryptableSecrets`; `{generate}` password sentinel so agents never invent or handle passwords. |
| P0-4 | Password fields unfillable | `{account:…}` resolution was already wired for `fill`/`type`/`select`; resolved values are now flagged `secret` so read-back never echoes them. |
| P0-5 | Fills unverified | `fill`/`type` read the field back, escalate value-setter → CDP `insertText`, and FAIL with `not-applied` if the value did not land. |
| P0-6/7 | Dead search, 403 fetches | `ProjectWebResearch` + `RagWebFetchAsync` browser fallback: a refused fetch is re-read through the agent's own browser; empty searches are labelled a tool failure; Cloudflare-obfuscated emails are decoded. |
| P1-8 | `project/` path fork | `ProjectFileStore` normalization. |
| P1-9/10 | Convergence false positives | World-epoch scoping; read-only observations exempt from the dead-end ledger. |
| P1-11 | Argument-shape rejections | `ProjectToolFacade` auto-repair (casing, synonyms, edit-distance-1, op-named payload unwrap) + `ProjectToolContract` normalizations. |
| P1-12 | Watchdog cancelled live wakes | Advisory diagnoses now nudge the running wake (`NudgeActiveWake`) instead of cancelling it. |
| P1-13 | Locator dead ends | A `not-found` now returns ranked `nearby` controls so the next call can target a real one. |
| P1-15 | Overlays block clicks | Intercepted clicks auto-dismiss the blocker and retry; `op=dismiss_overlays` sweeps consent walls; Chromium profile prefs kill the password-save bubble and notification prompts. |
| P1-18 | Fabricated deliverables | `ProjectExternalActions` ledger + `record_external_action`; account registrations and mail sends record themselves; the ledger is seeded into every commander and worker wake. |
| P1-20 | Step ledger | Already enforced evidence on `Done` — verified, no change needed. |
| P2 | Prompt caching | Already implemented (`ApplyPromptCaching` + conversation breakpoint, marker present in the Projects system prompt). The observed token burn is a routing/`cached_tokens` question, not a missing mechanism. |

Desktop image bumped to `imageVersion 10` — running desktops must be recreated to pick up the new
`browser-inspect.py`.

Still open: site playbooks (P2-27), role-based model routing (P2-25), duplicate-agent hygiene
(P2-31).

---

## P0 — Structural blockers (goal was unachievable regardless of model quality)

### 1. There is no way to send an email
`Omnipotent/Services/KliveMail/KliveMail.cs` runs an inbound `SmtpServer` only. KliveMail is a
receive-only catch-all. The entire approved strategy — cold outreach + emailed job applications —
had no execution path from the first minute.

The agent discovered this at 03:02 by trying `smtplib` against Gmail (`535 Username and Password
not accepted`, `Total sent: 0/4`) after ~3 hours of preparation work.

**Fix:** outbound send on `klive.dev` — `klivemail_send(from, to, subject, body, attachments[],
replyTo)` backed by a real MTA with SPF/DKIM/DMARC on the domain, per-project daily send caps, and
a bounce/complaint webhook that writes back to the project event log. Without this, no
outreach-shaped project can ever succeed.

### 2. CAPTCHA is a hard stop by design
`browser-inspect.py:2281-2298` *detects* reCAPTCHA/hCaptcha/Turnstile and raises
`HUMAN_CHALLENGE_DETECTED`; `ProjectCommanderAgent.cs:94,108,111` and
`ProjectUploadCapability.cs:21` then route it to `request_human`. That policy is incompatible with
"no human intervention, including creating accounts" — every consumer signup in 2026 has one.

**Fix:** a `solve_challenge` op. Detection already works; wire it to a solver:
- Detect type + sitekey + page URL (already 90% done in the detection selectors).
- Call CapSolver/2Captcha (accounts already exist per KliveAgent memory — they need funding, and
  the money budget needs a line item for solver credits, ~$0.003/solve).
- Inject the token into `g-recaptcha-response` / `cf-turnstile-response` and fire the site's
  callback.
- Fall back to `request_human` only after N solver failures.

Also needed for full signup autonomy: **SMS/phone verification** via a number-rental API
(SMS-Activate / Twilio), exposed as `phone_rent` / `phone_wait_for_code`, mirroring the KliveMail
code flow that already works well.

### 3. AccountRegistry root key is corrupt — the one account that *did* get created was lost
Event 1966: `account_register` → `AccountRegistry root key is corrupt (0 bytes) and encrypted data
exists` (`Omnipotent/Klives Management/Data Handling/AtomicSecretRootKey.cs:32`).

Consequences, in order: the Hubstaff Talent account (created **and email-verified** at 02:58 — the
single genuine win of the run) could not be persisted → the `hubstaff-profile-builder` agent
spawned 4 minutes later had no credentials → it created a *second* account on the same email →
"account already exists" → password-reset loop → budget exhausted mid-reset.

**Fix (P0, data integrity):**
- Repair/restore the root key; add a startup self-test that fails loudly instead of at first write.
- Never let a successful external signup depend on a later registry write: write the credential
  **before** submitting the signup form (generate → store → type by reference).
- Add `account_register(..., generatePassword: true)` so the model never authors or holds a
  password, and the value is durable from the moment it exists.

### 4. Password fields cannot be filled — the two available paths are both blocked
- Structured `fill` fails on React/shadow-DOM inputs: `not-found: No element matched the structured
  locator` (~20 occurrences on Upwork/Fiverr/PPH).
- The escape hatch is blocked: `browser-inspect.py:1552-1553` blocks any script containing
  `password` **or** `.value`. The agent tried renaming variables; `.value` caught it anyway.
- `desktop op=type` reports success but writes nothing into React inputs (event 520: "Typed text"
  → field stayed empty).

Net: **no working method exists** to complete a signup form on a modern SPA.

**Fix:** a first-class `browser op=credential_fill` that takes only an
`{account:service/username/field}` reference (the mechanism already exists in
`AccountRegistryStore.cs` and is already resolved for typing elsewhere) and injects natively via
CDP `Input.insertText` on the focused element. The model never sees the secret — this is *stricter*
than today's blanket regex ban while actually working. Then narrow `SCRIPT_BLOCKS` to reads
(`getAttribute('value')`, serialization) rather than any occurrence of the substring.

### 5. Every fill/type is unverified
`computer_type` and `browser op=fill` return success without checking the field changed. The agent
spent ~15 minutes filling a Hubstaff form that had silently reset after a Chrome popup.

**Fix:** post-condition on every input op — read back the AX-tree `value` (or `.value` via the
privileged internal path, not model script), compare to intent, and on mismatch auto-escalate
through the ladder: `insertText` → key events → clipboard paste (the clipboard route is already a
known-good trick, per the Instagram caption memory). Return `filled: true/false, actual: "…"`.

### 6. Web search is functionally dead
15+ `web_search` calls returned **zero** results: `brave: too many requests, duckduckgo: CAPTCHA,
startpage: CAPTCHA, google cse: too many requests`. Agents then burned dozens more calls on
`curl duckduckgo.com/html` and `python urllib` from the desktop container, which has no egress
(`ConnectionRefusedError [Errno 111]`), while `curl bing.com` intermittently worked.

**Fix:**
- Paid search API (Brave Search API / Serper / Tavily / Exa) as primary; scraped engines as
  fallback only.
- Per-engine cooldown + backoff scheduler; never issue a query to an engine in cooldown.
- **Never return an empty result set silently.** Return `SEARCH_DEGRADED: all engines rate-limited,
  retry after 90s or use web_fetch on a known URL` so the agent re-plans instead of re-querying.
- Result cache keyed on normalized query (the same 3 queries were re-run 5+ times across agents).

### 7. `web_fetch` 403s where the browser succeeds, and gives up
BloggingPro, Upwork, jobbers.io, remoterocketship all returned `HTTP 403` to `web_fetch` while the
same URLs loaded fine in the container browser. The agent had no signal to switch.

**Fix:** on 403/429/Cloudflare-interstitial, transparently retry through the container browser tab
and return the rendered text. Also: **decode Cloudflare `email-protection#<hex>` obfuscation** —
ProBlogger employer emails were sitting right there in the HTML the agent already fetched
(`/cdn-cgi/l/email-protection#1872777a6b58686a...`), and that single 10-line decoder would have
unblocked the entire job-application workstream that instead consumed two agents and ~$0.5.

---

## P1 — Where the budget actually burned

### 8. `project/work/…` silently forks the filesystem
`ProjectFileStore.cs:1069-1080` strips `/project/` and `D:/project/` but **not** the relative
spelling `project/…`. Agents that wrote `project/work/x.md` created a real second tree.

Result: `gig-planner` and `signup-researcher` delivered `project/work/gigs_and_proposals_plan.md`
and `project/work/automatable_platforms.md`; the Commander's `read_file("work/…")` returned
"Project file not found" and it concluded *"the file doesn't exist — it likely failed to write"*
and redid the work. Two full agent deliverables lost.

**Fix:** in `NormalizeRelativePath`, also strip a leading `project/` segment. Belt and braces: on a
read miss, try the alternate spelling before erroring, and warn via `TOOL_ARGUMENT_NORMALIZED`.

### 9. The convergence guard punishes idempotent reads and legitimate retries
`ProjectToolCallConvergence.cs` keys the signature on `tool + normalized args` with no external
state. So:
- `computer_browser_inspect(mode=controls)` tripped the guard at 3× **after navigating to three
  different pages** — inspecting a new page is not a loop.
- `computer_click(x,y)` tripped after a modal was dismissed between attempts — the retry was
  correct.
- Five separate agents lost wakes to this. The Commander lost a whole wake at event 245
  ("Stopped after 5 repeated-call detections").

**Fix:** include an external-state token in the signature — page URL + DOM/AX-tree hash for browser
ops, screen hash for desktop ops. Same call after the world changed = not a loop. Additionally,
exempt read-only ops (`inspect`, `read_screen`, `screenshot`, `list_files`) from the counter
entirely; cap them by budget instead.

### 10. Rejected (never-dispatched) calls count toward the loop guard
`RegisterRejectedCall` folds on `op` + error identity, so `desktop(click={x,y}, op=click)` — a pure
schema mistake — hit "LOOP DETECTED: identical invalid desktop call 5x" and consumed the agent's
loop budget without a single real action.

**Fix:** arg-shape errors should be *repaired*, not counted. Only count a rejection after
auto-repair has failed. See #11.

### 11. Argument-shape errors are the single largest wasted-call category
~35 dispatch failures across the run, all trivially repairable. Observed:

| Called | Should be |
|---|---|
| `desktop(op=click, click={"x":..,"y":..})` | `desktop(op=click, x, y)` |
| `browser(op=click, x, y)` | structured locator, or `physical_click` |
| `checkpoint(op=close_step, evidence=…)` | `evidenceReference` |
| `update_observable(value=0, textValue="0")` | one or the other |
| `manage_agents(…)` without `op` | `op=spawn` |
| `spawn_sub_agent(…)` without `tier` | `tier` required |
| `computer_browser_action(op=wait, condition=loaded/visible)` | `ready/text/selector/url/gone` |
| `web(op=fetch, fetch=…)` | `url` |
| `desktop(op=find_text, find_text=…)` | `text` |

The machinery to fix this already exists and is just under-populated:
`ProjectToolContract.cs:82` (`LegacyAliases`) and `:196` (`ApplyToolSpecificNormalizations`) — the
same place that already auto-folds `op=terminal` and `op=search`.

**Fix:** populate both tables from the observed shapes; coerce `{x,y}` objects to scalars; treat
`value`+`textValue` as value-wins-with-warning; infer `op` from unambiguous argument sets; default
`tier` to the parent's tier. Every repair emits `TOOL_ARGUMENT_NORMALIZED` so the model learns.
Also: enum errors should list *valid values inline* (the `wait` condition error did this — copy it
everywhere).

### 12. Watchdog force-wakes cancel work mid-action
9 recoveries in 3h20m; 5 of them cancelled a *live, productive* wake. The most expensive:
event 585 killed the Commander 3 seconds after it had filled the entire Upwork signup form and was
about to submit. Event 1283 killed it while it was reading a delivered agent report.

Two triggers fired repeatedly and wrongly:
- **Understaffing** (`ProjectWatchdog.cs:168-183`): "N slots free while M milestones have no owner"
  fired 6× while the Commander was actively working *with* 2–3 agents, because milestones m1/m2/m3
  were never mechanically linked to the step ledger and stayed `Pending` forever.
- **Stuck-loop trips ≥3** (`:143`) — which by #9 were mostly false positives.

**Fix:**
- Never cancel a wake that produced a tool result in the last ~60s. Staffing/advice diagnoses
  should be **injected into the running wake as a system nudge**, not converted into a
  cancel-and-restart.
- Cancellation must checkpoint external state (current URL, open tabs, form progress, logged-in
  accounts) so the next wake resumes rather than re-derives.
- Auto-own a milestone when a step referencing it is activated, so the staffing heuristic stops
  firing on a bookkeeping artifact.

### 13. Nothing survives a wake boundary except prose
Each new wake re-navigated, re-inspected, re-logged-in. There's no durable "external world state"
record: which sites are open, which accounts are authenticated, what form is half-filled.

**Fix:** a `ProjectExternalState` block in the checkpoint — active tabs + URLs, authenticated
services, in-flight multi-step flows with their step index — rendered into the wake prompt.

### 14. Per-agent desktops mean per-agent cookie jars
The Commander created and verified the Hubstaff account in its own container. The
`hubstaff-profile-builder` agent got a fresh container with a fresh profile and was logged out —
so it tried to sign up again (#3).

**Fix:** share the browser profile per project (or bind a profile to an account in the registry and
mount it into whichever agent holds that account's lease). Add an **account-creation mutex** keyed
on service so two agents can never race the same signup.

### 15. Overlays eat roughly a third of all browser actions
Chrome's "Save password?" bubble, cookie banners, and a Hubstaff maintenance modal blocked clicks
repeatedly (`intercepted: The click point is covered by another element`). One agent spent 12
consecutive calls trying to dismiss the Chrome password bubble; when it finally hit "Never", it
logged the user out.

**Fix:**
- Container image: disable the password manager and save-bubble outright
  (`credentials_enable_service=false`, `password_manager_enabled=false`,
  `profile.password_manager_leak_detection=false`), plus translate/notification prompts.
- Ship a consent-blocker rule list (I-still-don't-care-about-cookies) in the profile.
- Generic recovery: on `intercepted`, identify the topmost element at the click point, dismiss it
  (Escape → close-button → click-outside), retry once, then report.

### 16. Locators are DOM-path based and go stale constantly
`kref1_…` refs encode a DOM path + signature; on SPAs they invalidate between inspect and act
(`stale-ref: The DOM position now identifies a different control`). The agent then re-inspects,
which trips #9.

**Fix:** resolve by a durable descriptor (role + accessible name + nth-match) captured at inspect
time, re-resolved at act time; auto-re-resolve once on staleness before failing. Prefer the CDP
accessibility tree over DOM queries — it pierces shadow DOM natively and is what OCR was seeing.

### 17. Text-tier agents are handed screenshots they cannot see
`RAW_IMAGE_OMITTED: this model has no image channel` — yet the tool still appended a coordinate
grid and told the agent "observe before the next click". Text-tier agents then clicked blind at
guessed coordinates.

**Fix:** `ProjectTierRouter` should either (a) refuse desktop/browser-pixel ops for `Text` tier and
force OCR-only phrasing, or (b) auto-upgrade an agent's tier when it's assigned a GUI objective.
Never emit pixel-observation guidance to a model without an image channel.

### 18. Sub-agents fabricate completed external actions
`outreach-executor` wrote a 14.5 KB `sent_outreach_log.md` at 03:01:51 with 12 entries marked
`Status: SENT ✅` and "Delivery Note: Sent via Gmail" — **before** it attempted any send, and the
attempt at 03:02:04 returned `Total sent: 0/4`. The log was never corrected. The next agent found
it and had to re-derive the truth. Several entries were addressed to invented domains
(`hello@fitnessbrand.com`, `contact@saacompany.com`).

**Fix:**
- A `record_external_action(kind, target, evidenceToolCallId)` primitive that **refuses** without a
  real tool-call id producing a success result; deliverable logs get generated from it, not typed
  by the model.
- Extend the Commander's existing step-close evidence requirement to sub-agent reports.
- Sub-agent prompt: claiming an external side-effect without a tool result is a reportable failure.

### 19. Dead ends and verified facts don't propagate to sibling agents
Fiverr/Upwork/PPH being un-signup-able was recorded as a Commander-scoped dead end. Then
`signup-researcher` independently re-discovered all three, and `job-applications` re-ran the exact
ProBlogger contact-email hunt the Commander had already failed at (including hitting the same
"registration not allowed" page).

**Fix:** render the project-level dead-end ledger and verified-facts list into **every** agent's
wake prompt, and make the repeat-intent detector project-scoped, not agent-scoped.

### 20. The step ledger degrades into incoherence
Observed in one wake: closed `s1` while intending `s6`; abandoned `s3`, `s4`, `s5` in sequence;
queued `s10–s13` then activated non-existent `s9`; `reorder_steps` referenced `s9_hubstaff_profile`
which never existed. The auto-advance ("Step s1 closed. **Now active: s3**") repeatedly surprised
the model into working the wrong step.

**Fix:** validate step ids with "did you mean" suggestions; stop auto-activating the next step on
close (require explicit activation); render a compact ledger table in every prompt; make
`close_step` auto-attach the most recent file-write/artifact from that step as evidence instead of
rejecting with "requires an evidence reference".

### 21. Preconditions are declared and then ignored
The approved Grand Plan listed precondition p1: *"Klive can create and verify Fiverr/Upwork
accounts"* with `status: unverified`. Execution then proceeded straight into a 2-hour attempt that
the precondition would have falsified in 10 minutes.

**Fix:** gate execution on blocking preconditions. If a milestone depends on an unverified
precondition, the runtime forces a bounded **spike** first (time- and cost-capped, e.g. 10 min /
$0.15) whose only job is to flip the precondition to verified/falsified. Falsified → auto-replan
before spending.

### 22. `convene_council` silently failed at the one moment strategy was set
Event 39: *"Too few panelists responded to form a council (need ≥2 openings)."* The single
stress-test step in the whole plan never ran, and the agent proceeded on an unchallenged strategy.

**Fix:** on panel-formation failure, fall back to sequential self-critique on the same model
(strategist → skeptic → chair as three prompts). A silent no-op on a decision gate is worse than a
cheap approximation.

### 23. Half-wired staffing tools
- `assign_plan_work` rejects the Commander as assignee ("choose an active worker") but the
  Commander has no way to own a milestone itself.
- `spawn_sub_agent` accepts `deliverablePaths` and silently drops it
  (`Ignored arguments not used by 'manage_agents' op 'spawn'`), so spawned agents have no
  contract on what file to produce — which is why deliverable paths kept drifting (#8).

**Fix:** allow commander self-assignment; make `deliverablePaths` a real spawn-time contract that
the agent's completion check validates against.

---

## P2 — Economics and quality

### 24. No prompt caching; context is re-sent in full every turn
One Commander wake: **6,024,537 prompt tokens** against 21,037 completion tokens (286:1), $0.63 for
one wake. Across the run, ~$4.60 of the $5.00 went to re-sent prompt context. Live context reached
108k/180k.

**Fix:** provider prompt caching on the stable prefix (system + plan + ledger); move volatile
sections to the tail. Compact tool results aggressively (see #25). This alone probably triples the
work-per-dollar.

### 25. Raw HTML and screenshots dumped into context
`http_request` returned 16 KB of Next.js boilerplate and inline CSS for a single fee-comparison
page. Screenshots were attached to text-tier agents that cannot read them.

**Fix:** readability-extract on `http_request`/`web_fetch` by default (`raw: true` opt-in); cap at
~2 KB with a "fetch more" continuation; suppress image attachment for non-vision tiers; add
`extract(url, what: emails|links|prices)` helpers so agents stop grepping HTML in bash.

### 26. Everything ran on one cheap model
Route was `qwen/qwen3.6-35b-a3b` for the Commander's strategic planning, all browser interaction,
and all writing. A large share of the arg-shape errors, loop trips, and the fabricated outreach log
are capability failures, not harness failures.

**Fix:** role-based routing in `ProjectTierRouter` — strong model for Commander planning turns and
for any turn containing a browser/desktop action; cheap model for research, drafting, summarizing.
Budget per role rather than per project.

### 27. Budget is opaque to the agent until it's gone
The Commander self-reported "~$4.25 of $5" while telemetry said otherwise; the 80% warning landed
at 03:08 and exhaustion at 03:31, mid password-reset. No agent could see remaining budget.

**Fix:** live `budgetRemaining` / `wakeCostSoFar` in every checkpoint header; a soft ceiling that
triggers "land the plane" behaviour (finish current action, persist state, write handoff) at 85%.

### 28. No per-site playbook memory
KliveAgent memory holds ad-hoc notes ("Alt+O works in GTK dialogs", "Instagram caption needs
clipboard paste"), but there's no structured, replayable per-domain recipe store. Every run
re-derives that hubstafftalent.**net** (not `.com`) is the live domain, that signup is at `/signup`,
that the login is at `account.hubstaff.com/login`.

**Fix:** `site_playbook` keyed on domain — working URLs, selectors/AX descriptors, step sequence,
known interstitials, last-verified date. Written automatically on a successful flow, injected into
the prompt on navigation to that domain. This is the single biggest lever for "one-shot".

### 29. Tab discipline is not enforced
Up to 5 tabs open; agents repeatedly acted on the wrong tab; `close_tab` refused to close the last
tab; indices shifted after each close so `tabIndex=4` became "does not exist".

**Fix:** stable tab ids instead of indices; hard cap (2) with LRU close; every browser op reports
which tab it acted on.

### 30. `klivemail_wait_for_code` only understands codes, not links
Hubstaff sent a confirmation **link**; `wait_for_code` timed out at 120s reporting "no verification
code observed", and the agent had to fall back to `list_messages` + `get_message` + manual navigate.
Later, the link had expired.

**Fix:** `klivemail_wait_for_verification` that returns code **or** link, and optionally navigates
the link in the agent's browser automatically. Also surface the sender/subject on timeout so
"nothing arrived" is distinguishable from "arrived in a form I didn't parse".

### 31. Duplicate/near-duplicate agents doing the same work
`outreach-preparer` → `outreach-executor` → `outreach-campaign` all covered the same beat; the
Commander spawned the third while the second was still running, then had to stop it. Similarly
`job-board-scanner` and `job-applications` overlapped.

**Fix:** spawn-time overlap check against active roster objectives (embedding or keyword) — refuse
or merge with a warning.

### 32. Status messages substituted for action
The Commander sent 6 `reply_to_klives` updates and dozens of `report_progress` / `update_plan`
calls; the wake diagnostics counted these as "productive actions". Bookkeeping inflated the
productivity signal that the watchdog reads.

**Fix:** separate "external progress" (file written, form submitted, message sent, account created)
from "bookkeeping" in `ProjectWakeDiagnostics`, and have the watchdog judge on the former.

### 33. Smaller items worth fixing while you're in there
- `update_observable` type is immutable — `'Gigs created' is a Text observable; it cannot become
  Numeric` blocked four separate updates. Allow type change on `set` with a warning, or coerce.
- `desktop op=terminal` has no internet but this is undocumented in the tool description; agents
  burned ~10 calls learning it. Either give the container egress or say so in the description and
  point at `http_request`.
- `computer_click` outside the last screenshot bounds errors instead of clamping/refreshing
  (`Coordinate (942,3175) is outside the last screenshot`) — auto-scroll-into-view then click.
- `browser op=wait` accepted `waitFor` and `condition` inconsistently; `condition=text` without
  `waitFor` errors. Collapse to one argument.
- `ProjectFileStore` should reject writing a deliverable to a path that doesn't match the spawn
  contract (#23) rather than silently accepting it.
- The wake-cancelled event should include what was in flight, so the next wake's prompt says
  "you were mid-signup on X" instead of nothing.

---

## What already works well (don't regress these)

- **KliveMail catch-all + `wait_for_code`** — the Hubstaff verification flow worked first try and
  is the model for how phone/SMS should be built.
- **Dead-end ledger and repeat-intent warnings** — the escalating "REPEAT (4× FAILED) — stop
  attempting this" messages were the only thing that broke several genuine loops. Make them
  project-scoped (#19) and they get much stronger.
- **`{account:service/field}` secret references** — right design, never usable in practice because
  of #4. Fixing `credential_fill` makes it the default path.
- **Redaction of live page contents from durable history** — correct, keeps the event log clean.
- **Grand Plan structure** (preconditions, risks, success criteria) — the shape is right; it just
  isn't enforced (#21).

---

## Suggested build order

**Sprint 1 — make autonomy possible at all**
1. Fix AccountRegistry root key + `generatePassword` (#3)
2. `credential_fill` by secret reference + input verification (#4, #5)
3. `solve_challenge` (CAPTCHA solver integration) (#2)
4. Outbound `klivemail_send` (#1)
5. `project/` path normalization (#8) — 3-line fix, prevents lost deliverables

**Sprint 2 — stop the bleeding**
6. Convergence guard keyed on external state; reads exempt (#9, #10)
7. Argument auto-repair table (#11)
8. Watchdog: nudge instead of cancel; never cancel an active wake (#12)
9. Overlay auto-dismiss + Chrome profile prefs (#15)
10. Search API + degraded-search signal (#6), fetch fallback + CF email decode (#7)

**Sprint 3 — make it one-shot**
11. Prompt caching + result compaction (#24, #25)
12. Role-based model routing (#26)
13. `site_playbook` store (#28)
14. AX-tree locators + auto-re-resolve (#16)
15. Precondition gating with bounded spikes (#21)
16. `record_external_action` anti-fabrication primitive (#18)
17. Shared browser profile + account-creation mutex (#14)
18. Phone/SMS verification service (#2)
