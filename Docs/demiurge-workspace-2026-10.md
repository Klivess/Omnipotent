# Demiurge: agent friction and the workspace UI — 15 October 2026

Companion to the computer control-plane entry in `steward-computer-reliability.md`. This entry
covers what changed for the agents (perception, site knowledge, limits) and for Klives (the
Demiurge pages on the ot-os design system).

## Agents

- **Compact browser perception.** `computer_browser_inspect` in `controls` mode now returns one
  line per control instead of JSON, e.g. `[e12] textbox[email] "Email" placeholder="…" (required, empty)`.
  The output starts with a `PAGE` line, lists frames as `f1…`, and counts hidden and off-screen
  controls. The default is 150 controls (1–300). `format:"json"` still returns the raw structure.
- **Short refs.** `e12`-style aliases replace the ~200-character `kref1_` refs in every browser
  result. The adapter expands them at dispatch, including `trigger.ref`. An unknown alias is a
  validation failure, never a guess. Each desktop keeps up to 4,000 aliases.
- **Site playbooks** (`StewardSitePlaybookStore`, shared by every Steward). Each playbook holds
  a domain, a summary, the steps that worked, pitfalls, key URLs, its author, a verified time
  and a revision count. After a successful `computer_navigate` or `computer_open_browser`, the
  playbook for that domain (or a parent domain) is appended to the tool result. Tools:
  `playbook_get`, `playbook_save`, `playbook_list` (also the `playbook` fold group). Routes:
  `GET /demiurge/playbooks`, `POST /demiurge/playbooks/save`, `POST /demiurge/playbooks/delete`.
- **Autonomy limits** (`StewardAutonomyPolicy`). Per-Steward settings: `blockedDomains`,
  `askFirstDomains`, `allowHostShell`, `allowOutboundEmail`, `allowAccountCreation`. They are
  enforced in `CommanderToolDispatch`, so they apply to the Commander and every sub-agent:
  - Blocked sites (and their subdomains) are refused for `computer_navigate`,
    `computer_open_browser`, `web_fetch`, `http_request`, and for `computer_cdp`
    `Page.navigate` / `Target.createTarget`.
  - The first use of an ask-first site opens an approval card. An approval lasts 24 hours. It
    is held in memory, so a restart asks again.
  - Host shell/script tools, `klivemail_send` and `account_register` can each be switched off.
    The refusal tells the agent what to do instead.

  The active limits are written into every wake seed. With the defaults nothing changes.
- **Computer awareness.** Each wake seed has a computer section (state, tabs, memory, recent
  crashes). Crash events are prefixed to the next computer tool result. See the control-plane
  entry.

## Klives (management website)

- **One shell.** Every Demiurge page sits in `DemiurgeShell`: a status band, a top bar with the
  fleet or Steward status and a host-problem link, and section navigation (Operations,
  Computers, Playbooks, Accounts | Analytics, Costs). Everything uses the ot-os tokens and
  components; legacy Demiurge components were moved onto the same tokens.
- **Operations** (`/demiurge`). KPI row, a needs-attention card and drawer, spend/delivery
  charts, and the Steward table. Rows keep their order across live refreshes. The result
  inspector and system defaults open as drawers; broadcast is a modal; the prompt-cache halt
  banner keeps its two controls. The page scrolls normally instead of being fitted to the
  viewport.
- **Workspace** (`/demiurge/[id]`). Tabs: Overview, Chat, Timeline, Plan, Agents, Computers,
  Files, Observables, Analytics, Settings, deep-linkable with `?tab=` (the old names `desktops`,
  `councils`, `hooks` and `chat` still work). Overview shows pending approvals first, then the
  numbers, the active step, recent conversation, plan progress, computers, observables and the
  roster. Chat groups tool calls into work bursts, and its composer sends a Task, a Chat
  message or a standing Rule. Working tabs carry a rail with the budget editor, the plan and
  observables.
- **Computers** (`/demiurge/computers` and the workspace tab). Each computer shows its state,
  browser, memory and OOM kills, routes (VNC and daemon) and activity. It has a repair menu, a
  diagnostics drawer (components, resources, largest processes, event journal, logs) and a
  remote-control modal.
- **Playbooks** (`/demiurge/playbooks`): search, a verified filter, and an editor drawer.
  **Accounts**: search, a status filter and summary numbers. **Settings** has an Autonomy limits
  section.

## Verification

- Website: Playwright `demiurge-overview` (17), `demiurge-workspace` (6), `steward-computers` (3),
  `steward-agent-retirement` (7) and `steward-analytics` (1): 34 pass. `nuxi typecheck` is clean
  apart from the existing errors in `scripts/agentMarkdown.ts`.
- Backend: the C# Demiurge suite and `computerd_test.py` pass.
- Nothing is deployed, and none of this has run against live Stewards yet.
