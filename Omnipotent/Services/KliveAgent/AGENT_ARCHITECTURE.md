# KliveAgent Architecture

This document explains what KliveAgent is, how it turns a prompt into actions, and how it
relates to a "canonical" coding/assistant agent. It is the reference behind the hardening work
tracked in the plan (persistence of actions, streaming, analytics, Discord gating, etc.).

## What KliveAgent is (and isn't)

KliveAgent is **not** a code-rewriting coding agent for the Omnipotent source. It is Klives's
personal, Jarvis-like assistant **embedded inside Omnipotent that operates and orchestrates the
platform's live services at runtime**. It does this by writing C# scripts (delimited by
`{{{ ... }}}`) that are compiled and executed in-process via Roslyn against the live service
graph — reading state, calling service methods, pulling logs/errors, spawning background tasks,
and saving durable memories.

From its system prompt:

> "You can access and control every service running on Omnipotent by writing C# scripts… When a
> user just wants to chat, respond naturally without scripts."

Personality: sarcastic, witty, fiercely loyal to Klives; punchy answers, no walls of text.
Access: gated to Klives (API routes require `KMPermissions.Klives`; Discord is owner-only).

The key consequence: KliveAgent's "tools" are **arbitrary runtime C#**, not a fixed set of
JSON-schema functions. That is a deliberately *more* powerful action surface — one composite
script can do discovery + action + logging in a single step — at the cost of relying on text
parsing rather than the provider's native structured tool-calling channel.

## How an assistant agent turns prompts into actions

Every coding/assistant agent is a `while` loop around a single LLM call. The model only emits
text; the *harness* turns that text into real actions and feeds the consequences back:

```
build context (system prompt + tools + history)
        │
        ▼
   call the model  ◀───────────────┐
        │                          │
        ▼                          │
 model emits actions               │ append observations
 (KliveAgent: C# script blocks)    │
        │                          │
        ▼                          │
 harness executes them ────────────┘
        │
        ▼ (model emits no action → final text answer)
      done
```

This is the **ReAct** pattern: *Reason → Act → Observe*, repeated until the model produces a
plain answer with no further actions.

### KliveAgent's concrete loop

Implemented in [KliveAgentBrain.cs](KliveAgentBrain.cs) `ProcessMessageAsync`:

1. **Build context** — `BuildSystemPrompt` assembles personality + `[Rules]` + `[Common Patterns]`
   + a task-personalised repo map (BM25, budgeted) + BM25-ranked memories + a conditionally-shown
   tool catalogue. `BuildUserPrompt` adds budget-selected conversation history + the new message.
2. **Call the model** via KliveLLM (`QueryLLM`), which talks to Local/HuggingFace/OpenRouter.
3. **Parse** the reply with `ParseLLMResponse` — extract `{{{ ... }}}` (or ```` ```csharp ````) blocks.
4. **Execute** each script through the Roslyn `KliveAgentScriptEngine`; capture `[OK | ms]` output
   or `[ERROR | ms]`, truncated to a token budget, into a `[Script Observations]` block.
5. **Observe & loop** — feed observations back as the next turn. Repeat.
6. **Stop** when the model replies with no scripts (the final answer). Guardrails (as actually
   implemented): a 2-strike breaker on XML/JSON tool-envelope or empty no-op turns; adaptive
   reasoning/model escalation as a task shows difficulty; a per-run **token + wall-clock budget**
   (`KliveAgent_MaxRunBillableTokens` / `KliveAgent_MaxTaskMinutes`; the token cap counts billable
   tokens and is skipped on a flat-fee provider) that warns at 80% and force-finalises at
   100%; and an external zero-progress stall watchdog. There is deliberately **no fixed iteration
   cap** — the earlier "same script twice / same error 3× / hard cap at 30" logic was removed in
   favour of these signals, so the loop can take as many steps as a task genuinely needs while the
   budget bounds runaway cost.

### Context management

`KliveAgentContextBudget` estimates tokens (~4 chars/token) and applies *compression* budgets to
regenerable content: repo map (`RepoMapBudget`), memories (`MemoryBudget`), conversation history
(`HistoryBudget`), per-script output (`ScriptOutputBudget`), and replayed prior scripts
(`HistoryScriptBudget`). History turns are scored by recency + keyword overlap and greedily fit to
budget. Persistent memory (`KliveAgentMemory`) gives cross-conversation recall via BM25.

## KliveAgent today vs. a canonical agent

| Aspect | KliveAgent | Canonical coding agent |
|---|---|---|
| Action surface | Arbitrary runtime C# (Roslyn) over the live service graph | Fixed JSON-schema tools (read/edit/shell) |
| Purpose | Operate & orchestrate Omnipotent services | Edit files, run build/test, modify code |
| Tool invocation | Model emits `{{{ C# }}}`; harness regex-parses & compiles | Provider returns structured `tool_calls` |
| Loop | Think→Script→Observe; no fixed iteration cap, bounded by a per-run token/time budget + no-op/stall breakers | ReAct loop, usually bounded |
| Memory of its own actions | Persisted + replayed into later turns; shared memory pool deduped on save, recency-ranked | Native via tool/assistant/tool-result messages |
| Streaming | Token streaming implemented (SSE from the provider, surfaced to the UI) | Streamed |
| Reliability | Transport retry/backoff **and** brain-level per-turn retry on transient LLM errors | Provider SDK retries |
| Context mgmt | Budgeted selection + implemented earlier-turn compaction (`BuildEarlierSummary`) | Token-counted, compaction/summarization |

## Status note (kept honest)

Phases 1–8 below described a hardening plan; most has shipped and this document has been trued up
to match the code (the earlier draft claimed a non-existent 30-step cap and called shipped features
"planned"). What is actually live: action persistence+replay (P1), token streaming + talk-while-
working (P2), analytics rollups (P4), Discord owner-gating (P5), transport **and** brain-level
retries (P6), native `execute_csharp` tool calling **alongside** the retained text-protocol parser
(P7 shipped as a *hybrid* — the regex fallback was NOT removed, since local/prose replies still need
it), and earlier-turn compaction (P8). Added on top of the original plan: a per-run token/wall-clock
budget, background-task restore-on-restart, and memory dedup/recency/df-cache. Still open: a
killable (process-isolated) script sandbox — a timed-out Roslyn script is abandoned, not force-killed.

## Rationale for the hardening phases

- **Phase 1 – remember its own actions.** The loop previously discarded the scripts it ran each
  turn (only the final text was saved) and replayed only text into later turns, so KliveAgent was
  blind to what it had just done. Now each agent turn persists its `ScriptResults`, and the most
  recent turns replay a budgeted code+output digest into the prompt.
- **Phase 2 – stream + talk while working.** Make the loop event-driven: stream an immediate
  conversational acknowledgement while data-gathering scripts run concurrently, like a human who
  talks and works at once.
- **Phase 3 – UI.** Surface the persisted scripts/outputs on reload, render proper markdown +
  C# syntax highlighting, and consume the live stream.
- **Phase 4 – analytics.** Weekly/monthly rollups and richer metrics (latency, per-channel/
  per-service usage, memory activity, cost) on top of the existing per-day stats.
- **Phase 5 – Discord gating.** KliveAgent can read/control everything, so over Discord it answers
  only to Klives; everyone else gets the plain KliveLLM chatbot.
- **Phase 6 – retries.** A transient provider blip no longer aborts the whole agent loop.
- **Phase 7 – hybrid native tool calling.** Expose script execution as a single native
  `execute_csharp` tool so a tool-capable model uses the provider's structured `tool_calls` channel.
  Shipped as a **hybrid**: the regex text-protocol parser and anti-XML defensive rules are **kept**
  as the fallback for Local providers and prose/`{{{ }}}` replies — so they were not removed as the
  original plan imagined; both paths coexist.
- **Phase 8 – compaction.** Summarize the oldest turns near the budget instead of dropping them
  (implemented as `BuildEarlierSummary`).
- **Later hardening (beyond the original 8).** Per-run token/wall-clock budget with an 80% wrap-up
  nudge and 100% force-finalise; brain-level retry of a transient LLM turn; background-task
  restore-or-orphan on restart; shared-memory dedup-on-save + recency ranking + one-pass document
  frequency. Deliberately still open: a process-isolated, force-killable script sandbox.

## Long multi-step tasks (computer, mail, accounts)

The bar: a request like "make a Tumblr account with a KliveMail address, create its API keys and
send me the details" runs top to bottom unattended, survives refreshes and restarts, and ends by
messaging Klives. What makes that work:

- **A bounded start.** Every optional system-prompt section (knowledge, memories, accounts, service
  index, repo map, personality) is built on the thread pool through `BoundedSectionAsync` and waited
  on for at most 1.5 s. A late section is left out, logged and timed as `prompt.<name>`. The run never
  sits at "preparing context" behind an enrichment. A stall stop seals steering the way a manual Stop
  does, so a follow-up message starts a fresh run.
- **Liveness, not silence.** `KliveLLM.ObserveActivity` (an `AsyncLocal` scope) reports what the
  model call is doing every 10s: queued for an AIRouter slot, awaiting the provider, reasoning
  (reasoning deltas are parsed), streaming, retrying. Tools are pulsed the same way (`WithToolPulse`,
  20-min ceiling). The stall watchdog only kills runs that are genuinely silent, and skips turns
  parked in the AIRouter queue. A stream that goes quiet for `KliveAgent_ModelStreamIdleSeconds`
  (240) raises `KliveLLMStreamStalledException` instead of hanging. The original bug: a healthy
  first call queued behind the Projects fleet was killed and the run labelled "Done".
- **Truthful endings.** `AgentChatRunControl` records why a run stopped (user | stall | shutdown)
  *before* cancelling it. The service token reads as "shutdown". `DescribeStop` writes the closing
  line, and the website badge shows Done / Stopped / Failed / Interrupted from status + `stopReason`.
- **Patient retries.** `KliveAgent_ModelRetryAttempts` (6), backoff 3s→3min honouring Retry-After.
  Permanent provider errors fail fast; unknown exceptions get one retry.
- **Task mode.** The first world-acting tool (`computer_*`, `klivemail_*`, account tools,
  `notify_klives`) switches the per-step guidance from "answer now" to "continue until every part is
  done". A text reply that only announces its next step gets sent back to work (at most 2 times). A
  checkpoint nudge fires every 40 steps. If the model rejects screenshots, images are stripped and
  the run continues on DOM/OCR perception.
- **The computer.** `KliveAgent_ComputerTarget` = auto | container | host. The container is a
  Projects desktop owned by `kliveagent` (`ExternalDesktopOwners`), driven through the same
  `ContainerToolAdapter`: structured browser inspect/act, verified fills, overlay dismissal, free
  `solve_challenge`, uploads, a terminal. Host = `HostControlManager`. For a human-only blocker,
  `request_human` posts a takeover card carrying the `containerId`. The website then embeds
  `ContainerRemoteDesktop` in place, and the run resumes when Klives goes idle after input, presses
  Done, or ends remote control.
- **CAPTCHAs stay in the run.** A solved reCAPTCHA expires in about two minutes, so handing one back
  to Klives by ending the run voids his work before the next run reaches the page. Once
  `solve_challenge` fails, the policy says to call `request_human` straight away. A final reply that asks
  him to tick a box (`LooksLikeHumanCheckHandBack`) is sent back once with that instruction, unless a
  takeover has already timed out or been declined. While a takeover is pending the desktop counts as
  watched (`MarkViewed`), so the 20-minute idle suspension cannot stop it under him.
- **The browser restarts clean.** Every relaunch of the container's Chromium follows a kill, so the
  launcher marks the profile's last exit as clean and passes `--hide-crash-restore-bubble`. Before this,
  a "Restore pages?" bubble took the keyboard and swallowed typing and clicks.
- **Computer-use reliability (Oct 2026).** See the next section.

## Computer-use reliability (October 2026)

KliveAgent's own report (`computer-use-report.pdf`, 8 Oct 2026) found structured browser tools ~85%
reliable, coordinate clicks on web content ~40%, typing "silently lost" behind overlays, uploads 0 for 2,
and reCAPTCHA unbeatable by synthetic input. What was actually wrong, and what fixed it:

- **The structured tools were down, not flaky (stall #6).** `BrowserServiceClient` posted with
  `PostAsJsonAsync`, which streams a chunked body with no Content-Length; `browser-service.py` read
  exactly Content-Length bytes and answered every `/run` with 400. Since the service shipped (Sep 16),
  every structured browser action on a desktop that published it failed — and 4xx was deliberately
  not a fallback trigger. That is why the agent kept hand-rolling CDP in the terminal. Now the client
  sends a buffered body, the service also accepts chunked bodies, and a 4xx is logged and falls back
  to `docker exec` (per endpoint+mode, 10 min) instead of failing the agent. A test runs the real
  Python service against the real client.
- **Helpers reach existing desktops.** Image changes only apply to new computers (existing ones keep
  their installed software), so `ContainerDesktopManager.EnsureHelpersCurrentAsync` compares the
  desktop's `/usr/local/bin` helpers with the shipped ones by SHA-256 once per process and refreshes
  them in place through the Docker archive API, restarting the helper service.
- **Verified physical clicks.** `computer_click`, `computer_click_text`, `computer_click_browser_control`
  and approved clicks run through `VerifiedClickAsync`: the helper's `preflight` hit-tests the point
  (through cross-origin frames), closes a browser bubble/menu that holds the keyboard, and arms a
  receipt in a private isolated world (`klive-input`, found again by stored context id — no
  `Runtime.enable`, a known bot-detection signal); `receipt` then says whether the press arrived. The
  result names what was hit, or the exact coordinates of the nearest controls. A click that provably
  never reached the page (no press and the pointer's final approach unseen) is re-delivered once
  through CDP; one the page saw but swallowed is reported, never repeated. OCR clicks whose text centre
  is not on a control move onto the control the text labels.
- **Verified typing.** `computer_type` refuses when no text field has focus (`force:true` for games),
  records the field, types, and reads it back; keystrokes that never arrived are re-entered into that
  same field through CDP, rejected ones are reported. Secrets are compared by SHA-256 only.
- **`computer_cdp`** — evaluate (top-level await, any frame), raw commands, trusted click/type/key,
  JavaScript dialogs, `set_files`, targets, a page-only screenshot. Sign-in wipes are refused, cookie
  values redacted, and every value substituted from the vault/registry on this desktop is masked in
  all tool output (`SecretEchoScrubber`). `klive-cdp` is the same engine for the terminal. Project
  agents keep their browser-first contract: their `evaluate`/`send` are limited to non-hidden work.
- **Uploads by interception.** `computer_upload_file` takes `trigger` (the site's upload button) and
  presses it with `Page.setInterceptFileChooserDialog` on, so no GTK dialog opens and inputs the page
  builds on the fly (the Tumblr-avatar case, invisible to any DOM search) still receive the file. A
  chooser that is already open is closed and the remembered last click re-pressed with interception.
- **One automated attempt per CAPTCHA.** `HumanGateRegistry` records the outcome of a click on a
  challenge frame or a free `solve_challenge`; after one failure further synthetic attempts on that
  site are refused (`HUMAN_GATE`, `ContainerToolFailureKind.HumanRequired`) until a human has driven
  the desktop. KliveAgent hands the desktop over automatically on that kind
  (`KliveAgent_AutoHandOffHumanGates`, default on), pings Discord again after
  `KliveAgent_TakeoverReminderMinutes` (4) if nobody has touched it, and on resume says to submit at
  once — a solved token lives about two minutes.
- **Known dead ends are named in results.** Inspection adds `JS_DIALOG_OPEN` and `PAYMENT_FRAME`
  advisories beside the existing CHALLENGE/NATIVE_DIALOG banners; navigation answers "Leave site?".
- Kill switch: `PROJECTS_INPUT_VERIFICATION=0` turns preflight/receipt off (input then goes out exactly
  as before). Desktop image v13.
- **Task tools.** `klivemail_create_mailbox/list/get/wait_for_email` (returns codes plus links
  ranked verification-first; catches near-miss addresses). `account_register` takes `{generate}`
  passwords; `account_update` stores obtained keys. `notify_klives` sends a Discord DM plus a
  Results entry.
- **Secrets.** The model only ever handles `{account:service/field}` references. Klives'
  authenticated views (`/chat/pending`, `/chat/runs`, `/conversations/get`, served `no-store`) reveal
  them at display time. Stored history, notifications (OS toasts) and Discord never carry values.
- **Come back later.**
  - Deep links: `/kliveagent?conversation=…&takeover=1`.
  - A reload restores each finished turn's activity, tokens and outcome.
  - A run that finishes while watched is filed as read.
  - An unwatched run of 3+ minutes sends a Discord DM (`KliveAgent_DiscordDmOnFinish`,
    `KliveAgent_FinishedDmMinMinutes`).
  - A run interrupted by a process restart **or** a KliveAgent service stop/restart is persisted
    `autoResumePending`. It is continued exactly once at the next start, with a System message that
    carries the request, steering and activity timeline (`KliveAgent_AutoResumeInterruptedRuns`,
    `KliveAgent_AutoResumeMaxAgeHours`, at most 2 chained).
- **Settings note.** OmniSettings persist their default on first read, so a changed default needs a
  new setting name. The settings above supersede `KliveAgent_MaxRunTokens`,
  `KliveAgent_MaxRunMinutes` and `KliveAgent_MaxLlmRetries`, which are no longer read.

## KliveRAG — cross-system knowledge (shared with Projects)

`Services/KliveRAG` is a separate `OmniService` that gives both KliveAgent and the Projects task
force semantic + lexical recall across **all** of Klives' systems, plus live web. It is local and
free: MiniLM ONNX embeddings (the same `ReplicaEmbedder` Omniscience uses), a SQLite store with an
FTS5 lexical leg and BLOB vectors, brute-force cosine, and a self-hosted SearXNG container for web
search (no API keys).

- **Sources (connectors, incremental via watermarks/hashes):** Projects event logs + digests
  (live via `EventAppended`, cross-project), KliveAgent conversations + memories (turn-pair chunks),
  Omniscience distilled knowledge (person facts / Q&A / hypotheses / profiles), repo `*.md` docs, and
  cached web pages (TTL'd). Omniscience's raw-message corpus is **federated at query time** (not
  re-embedded) through its existing `MessageEmbeddingIndex`.
- **Retrieval:** embed + FTS5 in parallel → Reciprocal Rank Fusion → recency boost → per-doc
  diversity cap. Both legs run on the thread pool and every SQLite statement is bound to the search
  deadline by a progress handler (`KliveRAGDb.BindDeadline`, SQLITE_INTERRUPT). Prompt injection uses a
  400 ms deadline over the newest 150K chunks and drops stopwords from the FTS query; the tool/route
  path searches everything with a 15 s deadline. A search that runs out of time returns what its legs
  had, often nothing; it never waits.
- **Why the bound matters:** until Oct 2026 the lexical leg ran synchronously on the caller's thread
  with no deadline. With the Projects event log indexed, a message full of common words ("use your
  computer to make a tumblr account…") made bm25 score millions of rows. That held KliveAgent at
  "preparing context" for 5–12 minutes, and every Projects wake ran the same query. A "??" nudge has
  no searchable words, which is why the nudges always got through. Measured on a synthetic
  1M-chunk index: 3.3 s warm and growing linearly with size before the fix; 0.2–0.5 s after.
- **Delivery:** a budgeted `[Relevant Knowledge]` block auto-injected into KliveAgent's system prompt
  (below the cache breakpoint, `KnowledgeBudget`) and into Projects wake seeds (a `RELEVANT KNOWLEDGE`
  section, own-project events excluded), **plus** the native tools `search_knowledge`,
  `read_knowledge_doc`, `web_search`, `web_fetch` on both agents.
- **Jobs:** background embed queue, live/periodic connector scans, and a nightly 04:15 sweep
  (`KliveRAGScheduler` — after Omniscience's 03:30) that reconciles connectors and evicts expired web
  docs. HTTP surface under `/kliverag/*` (`search`, `doc`, `stats`, `sources`, `reindex`, `websearch`).
