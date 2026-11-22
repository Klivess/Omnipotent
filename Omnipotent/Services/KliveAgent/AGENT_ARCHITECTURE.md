# KliveAgent Architecture

This document explains what KliveAgent is, how it turns a prompt into actions, and how it
relates to a "canonical" coding/assistant agent. It is the reference behind the hardening work
tracked in the plan (persistence of actions, streaming, analytics, Discord gating, etc.).

## What KliveAgent is (and isn't)

KliveAgent is **not** a code-rewriting coding agent for the Omnipotent source. It is Klives's
personal assistant, **embedded inside Omnipotent, that operates and orchestrates the
platform's live services at runtime**. It does this with native tools and, where no tool fits, by
writing JavaScript (`execute_js`) that runs in-process (Jint) against the live service graph:
reading state, calling service methods, pulling logs and errors, spawning background tasks and
saving durable memories. Until 22 Nov 2026 those scripts were C#, compiled by Roslyn; see
"Scripts are JavaScript" below for why that changed.

Personality: sarcastic, witty, fiercely loyal to Klives; punchy answers, no walls of text.
Access: gated to Klives (API routes require `KMPermissions.Klives`; Discord is owner-only).

The key consequence: KliveAgent's "tools" are **arbitrary runtime scripts**, not only a fixed set of
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
3. **Parse** the reply: native tool calls (`execute_js` and the rest), or, in the text protocol,
   `{{{ ... }}}` / ```` ```js ```` blocks via `ParseLLMResponse`.
4. **Execute** each script in the conversation's JavaScript session (`KliveAgentJsEngine`); capture
   `[OK | ms] <output>` or `[ERROR | ms] <failure>`, truncated to a token budget, into a
   `[Script Observations]` block.
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
  first call queued behind the Demiurge fleet was killed and the run labelled "Done".
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
  Demiurge desktop owned by `kliveagent` (`ExternalDesktopOwners`), driven through the same
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
- **Computer-use reliability (Oct 2026).** See the section after next.
- **Scripts, restarts and compaction (Nov 2026).** See the next section.

## Scripts are JavaScript (22 November 2026)

`execute_csharp` is gone. KliveAgent's script tool is `execute_js`, run by Jint (a JavaScript
interpreter written in .NET) inside the process, over the same helpers (`ScriptGlobals`). The
text protocol's `{{{ }}}` blocks, `wait_for` check probes and `SpawnBackgroundTask` run JavaScript
too. The Roslyn host (`KliveAgentScriptSession.cs`, repair, diagnostics) is no longer used by
KliveAgent. Demiurge's Stewards still have their own `execute_csharp`.

**Why.** Running a script was never the slow part; failing to run was. On 11 Nov 2026, 44% of live
scripts failed, 224 of 380 at compile time, almost all where C#'s static types met Omnipotent's
internals: a member called on an `object`, a cast to an internal type, a missing `await`, the script
dialect's rules (`using var`, `await (Task<T>)x`). Each failure cost a model round trip. None of
those exists in JavaScript.

**How it behaves** (`KliveAgentJsEngine.cs`):
- **Nothing to compile.** A script is parsed (Acornima) in about a millisecond. There is no cold first
  compile and no Roslyn warm-up at start-up (that built ~80 MB of reference metadata).
- **Every value is dynamic.** Jint binds members by reflection on the runtime type, so a service's
  real member names work on whatever `GetService`/`Svc` returned, internal types included, and the first
  letter's case does not matter. .NET lists take `for…of` and JS array methods; JS arrays pass to .NET
  array parameters. `GetObjectMember`/`CallObjectMethod` still reach non-public members.
- **Top-level `await`, and variables that last.** Jint allows top-level `await` only in modules, whose
  variables die with them. So each script runs inside an async function, and its top-level
  declarations are rewritten as assignments (`const s = await …` becomes `;(s = await …);`), which
  set globals. Functions are copied to the global object; classes become assignments. Declaring a
  name again just reassigns it, instead of failing with "already declared". The last expression is
  returned and logged as `Result:`. Line numbers are preserved. A conversation's session is kept for
  `KliveAgent_KeepScriptSessionMinutes`, as before.
- **A real stop.** Each call gets Jint's `OperationDeadlineConstraint` for its budget, and a
  cancellation for awaits on .NET tasks. A runaway loop or a hung await is ended at the timeout, and
  the session is still usable. The Roslyn host could only abandon a timed-out script.
- **Errors that say what to write.**
  - A `SyntaxError` names its line and column.
  - A wrong member name lists the nearest real ones.
  - Code that looks like C# (memories, shortcuts and history from before the switch) gets a hint to
    translate it.
  - The helpers the C# prompt taught (`Dyn`, `Raw`, `Aw`) are identity functions, so a remembered
    recipe needs little change.
- **Same result shape.** Results are `AgentScriptResult`, so run history, script health, the tool-speed
  record and the website are unchanged. Syntax errors count as `compile`; a timeout is `timeout`.
- **Personality.** The saved default said "via C# scripts". OmniSettings saved that text on first read,
  so `KliveAgentPersonality.Resolve` swaps the old default for the new one, and changes the phrase in an
  edited personality.

Tests: `KliveAgentJsEngineTests` (top-level await, internal types, persistence, kept-after-throw,
wrong-member hints, syntax line, the C# hint, runaway loop, hung await, stop, Log formatting).

## Scripts and long unattended runs (November 2026)

*This section describes the Roslyn C# host, which KliveAgent no longer uses (see above). The
long-run plumbing (ledger, durable runs, tool deadlines, compaction) still applies.*

Measured on the live server on 11 Nov 2026, before this work: 44% of scripts failed (380 of 865
sampled; 224 of those never compiled, and four in ten of the compile failures were the same six
mechanical slips); the first script of a run took a median 4.5 s to compile against 132 ms for every
later one; three Alpha Lab runs were cut by a restart and never answered; a run past 70K tokens kept
240 characters of its task; and two tool calls ran for over two hours with nothing bounding them.

### The script host

- **One reference set.** `OmniScriptReferences` builds the references once from the runtime's own
  assembly list plus Omnipotent's dependencies, metadata only, and every script host uses it
  (KliveAgent, Demiurge work scripts, Demiurge stimulus hooks). Before, each host referenced
  "whatever was loaded when it started", which is why three stimulus hooks failed at start-up with
  `CS0234 'Linq' does not exist in the namespace 'System'`.
- **A held root.** `KliveAgentScriptEngine` (in `KliveAgentScriptSession.cs`) compiles one empty root
  script and keeps it. A session's first script is a continuation of that root, so it compiles like
  any later script instead of paying the cold start.
- **Kept sessions.** A conversation's script session outlives the run for
  `KliveAgent_KeepScriptSessionMinutes` (20; at most 6 are kept). A follow-up message finds its
  variables still there and is told which ones (`[Script session] Variables … still exist`).
- **Two budgets.** Compiling has its own (`KliveAgent_ScriptCompileBudgetSeconds`, 120). Running keeps
  `KliveAgent_ScriptTimeoutSeconds` (30), and one script may ask for more with `timeoutSeconds` on
  `execute_csharp` (up to 300). A slow compile on a busy server is no longer reported as a script
  that timed out.
- **Variables survive a throw.** A script that throws keeps what it declared before the throw; the
  result says which variables exist (`AgentScriptResult.SessionNote`).
- **Repair before reporting.** `KliveAgentScriptRepair` fixes the mechanical compile errors and
  compiles again (at most 3 rounds, `KliveAgent_ScriptAutoRepair`): a top-level `using var`, `await (…)`
  read as a call, a `using` directive below a statement, a type whose namespace is missing and
  unambiguous, an assignment to a name that was never declared, a member, `foreach`, index or `await`
  on an `object`, a `Task<T>` used as `T`, an internal type or non-public member, and an untyped lambda
  passed to a dynamic call. Every fix is listed in the result (`[auto-fixed before running, write it
  this way next time: …]`) so the model learns the rule. Valid code is never rewritten: repair only
  runs on a compile failure, and only at the spans the compiler reported.
- **`Dyn`, `Svc`, `Raw`, `Aw`.** Most Omnipotent types are internal, so a script cannot name them, and
  `GetService` returns `object`. `Dyn(x)` wraps any value in `KliveAgentDynamic`, which binds members
  by reflection at run time (public or not), awaits tasks, enumerates, converts on a cast and adapts
  lambdas to the real delegate type. A missing member answers with the nearest real names and
  signatures. `Svc("Name")` is `Dyn(GetService("Name"))`; `Raw(x)` unwraps; `Aw(x)` awaits anything.
- **Errors that say what to write.** `KliveAgentScriptDiagnostics` adds an arrow line under a
  diagnostic: the nearest real members of the type for CS1061, the real overloads for CS1501/CS1503,
  the namespace for CS0246. Scripts compile with debug information, so a runtime error names its line.
- **The result's first line is the finding.** A result reads `[OK | 132ms] <the output…>` or
  `[ERROR | 77ms] <the first diagnostic>`. Compaction keeps the first line of each tool result (and
  the first 160 characters when it stubs old ones), so a status alone on that line meant a compacted
  run remembered that scripts ran and nothing they found. `ScriptRules` tells the model to log its
  conclusion first for the same reason.
- **A failure streak is answered.** There is still no ceiling on failures, but 3 failed scripts in a
  row get the instruction to stop guessing names and look them up, 6 get "say what you will do
  differently", then every 4 more (`FailureStreakGuidance`).

### Runs that outlive a restart, and their own context

- **Background runs are real runs.** Alpha Lab and scheduled tasks go through `RunDurableAsync`: a run
  record, the stall watchdog, the live view and a truthful ending, where they used to call the brain
  directly and vanish with the process. A background run asks for model slots as agent work, so it no
  longer competes with a waiting Klives as an equal, and a clean finish is recorded without a
  message. A run someone is waiting on holds AIRouter's interactive reserve while it works.
  Background runs are deliberately not continued after a restart: their callers already redo what
  was interrupted (a scheduled task is still due and fires late; Alpha Lab puts the papers back in
  its queue and, since Nov 2026, hands back the attempt the restart used up), so continuing as well
  would do the work twice. A run started from the website is continued.
- **The ledger.** `task_ledger` (plan, done marks, facts) is the run's own working memory
  (`AgentRunLedger`). It is restated to the model after every compaction, carried into a continuation,
  kept on the run record and shown on the website with the run. A task run that reaches step 8
  without one is nudged once.
- **The task is protected.** KliveAgent passes `compactProtectPrefixMessages: 1`, so the message that
  holds the task stays verbatim through every compaction.
- **A continuation is told what happened.** After a restart the continuation message carries the
  whole task (up to 60,000 characters; it was cut at 4,000 while Alpha Lab briefs are 33 KB), the
  ledger, the last 60 activity lines and the last 6 scripts with their outcomes. Continuations stop
  after two that made no progress, not after two in total: a deploy day has a dozen restarts.
- **Tools are bounded.** A native or service tool call that is still running after
  `KliveAgent_NativeToolDeadlineSeconds` (600) is reported to the model as timed out and the run moves
  on. Tools that wait on purpose (`wait_for…`) are exempt. Calls of a minute or more are kept with the
  statistics, by tool and operation, so a hang can be named after a restart.
- **A guessed route gets the real ones.** `omni_api` answers a 404 with the nearest registered routes.
- **Ready sooner.** Stored conversations load four at a time at start-up.

### What is measured

- `GET /kliveagent/stats/scripts?days=N`: outcomes by day and week (compile, runtime, timeout, compile
  timeout, stopped, repaired), compile-time percentiles for first and later compiles, repairs by rule,
  the commonest error codes, the latest failures (the compiler's sentence only, never script text) and
  slow tool calls. Shown on the website under Insights.
- Run stages: `script.coldCompile`, `script.compile`, `script.run`; `model.queued`, `model.awaiting`,
  `model.reasoning`, `model.streaming`, `model.retrying` (`ModelStateClock`); `model.alone`,
  `model.with1other`, `model.with2others` (other KliveAgent calls in flight when the call was sent);
  `model.think.<level>`. `KliveAgent_ThinkingEscalationMode` (`steps` | `errors`) is the switch those
  last stages are evidence for; the default is unchanged.
- **The watch** (`KliveAgentStats.Watch.cs`): two rolling checks that message Klives once and then
  wait. Fewer than 60% of the last 50 scripts ran (24 h between messages), or the median wait for the
  provider to start answering was over 30 s across at least 6 streamed calls in 15 minutes (6 h
  between messages). `KliveAgent_HealthAlerts` turns the messages off. The state is on
  `/kliveagent/status` as `health`, so the page says "the provider is slow right now" beside the chat.

### Considered and not built

- **Continuing the model session across typed turns** (as talk mode does). Only the first model call
  of a typed message misses the prompt cache (16% hit; 23 such calls in the week measured), every
  later call hits 94%, and the change reaches into session ownership in both the brain and KliveLLM.
  Kept sessions give a follow-up its variables without it.
- **Resuming from a saved transcript** instead of a summary. The ledger and the fuller continuation
  message carry what a restart used to lose, without serialising model sessions to disk.
- **A richer compaction digest inside KliveLLM.** The headline-first result gets the same effect from
  KliveAgent's side and leaves the digest Demiurge depends on byte-identical.

## Tool speed: the fastest tool for the job (November 2026)

A run's wall clock is model calls plus tool calls, and until now only tool calls of a minute or more
were recorded. The model chose tools by their descriptions alone: a job `grep` does in a fraction of a
second went to `execute_csharp` (a compile first), a page already indexed was fetched again, and an
operation that hangs one call in five held the run for the whole ten-minute deadline each time it did.

- **The record** (`KliveAgentToolSpeed`, `SavedData/KliveAgent/ToolSpeed.json`). Every native, service,
  computer and script call is timed with its outcome (ok, error, overran). Each tool keeps its last 64
  calls, and so does each operation where the operation changes the time: a service tool's `op`,
  `omni_api`'s method and path (ids and numbers folded), `web_search` with or without `fetchTop`,
  `search_knowledge` with or without messages. Every dedicated service tool also counts toward
  `[service tools]`. Durations only: no arguments, results or content. Eight calls make a measurement
  trusted. Until then a built-in expectation per tool stands in.
- **Tiers.** Instant (median under 300 ms), fast (under 2 s), moderate (under 10 s), slow, and
  hang-prone: one call in ten over two minutes, or two or more overruns making up a tenth of the calls.
  Tools that wait on purpose (`wait_for`, `klivemail_wait_for_email`, `tool_jobs`, `request_human`, the
  approval tools) are their own tier and are never ranked or cut short.
- **Families.** `KliveAgentToolSpeed.Families` lists, for each goal, the tools that can reach it. Goals
  include searching code, finding files, reading source, reading or listing runtime data, recall,
  cross-system history, reading a page, web facts, live services, and seeing or acting on a page.
  `execute_csharp` is in many families but is never offered as the faster choice. The live-service family
  only sets the order: a service's own tool, `omniservice` and `omni_api` reach the same method, so
  when that method is slow no route is faster.
- **Steering the model.**
  - `SpeedRule` sits in the cached operating rules.
  - The `[Tool speed]` block below the cache breakpoint ranks each goal's tools fastest first, with
    measured typical times rounded to a few steps so the block rarely changes. It names what is slow
    right now.
  - A slow result (5 s or more) gets one `[speed]` line naming the faster tool for the same job, once per
    tool per run.
  - The tool definitions carry nothing measured, so the tools array stays byte-stable for the prefix cache.
- **Patience instead of one deadline** (`KliveAgent_AdaptiveToolPatience`, default on).
  - A call is waited for four times what nine in ten of its operation's good calls take. That wait is at
    least `KliveAgent_ToolPatienceFloorSeconds` (20) and at most `KliveAgent_NativeToolDeadlineSeconds`
    (600). Overruns are left out of that basis, so the hangs cannot set it.
  - A call still running after its patience is moved to a background job, so its result is kept. The
    plain deadline threw it away. The model is told on the result's first line.
  - An irreversible service operation waits for Klives' approval inside the call
    (`KliveAgentServiceTools.MayAwaitApproval`). It keeps the whole deadline and stays out of the record.
  - Computer tools are timed but never cut short or moved: they act on the desktop in order.
- **The gate** (`KliveAgent_ToolSpeedGate`, default on). A call to a measured hang-prone tool, when a tool
  at least five times faster does the same job, is answered once per run per operation with
  `[NOT RUN | slow tool]` and the alternative. Calling it again runs it, because the model may know the
  faster tool cannot do this particular job.
- **Background jobs** (`KliveAgentToolJobs`, on the conversation's `ScriptGlobals`, not persisted).
  - `start_background_tool` starts a native or service tool as a job with its own cancellation (30-minute
    ceiling unless the tool waits on purpose), for example waiting for an email while doing other work.
  - `tool_jobs` lists jobs, reads a result, waits for one job (or the first to finish) and cancels.
  - Finished jobs are announced once in the next step's guidance, with the first line of what they found.
  - A final answer while this run's jobs are still out, or finished unread, is sent back once.
  - Not for computer tools, scripts, `wait_for`, `task_ledger` or the job tools themselves.
- **New and faster tools.**
  - `grep` is now `KliveAgentCodeSearch`: `outputMode` files or count, `context` lines, `glob` for other
    file types, and `ignoreCase`. It skips bin, obj, node_modules and the .claude worktrees, gives each
    regex match 250 ms and each search 15 s, and runs off the caller's thread.
  - `find_files` searches file names by glob, newest first.
  - `read_data_file` (a window, or `fromEnd` for the newest lines of a journal) and `list_data_directory`
    reach runtime data under the app folder by GlobalPaths key, with no script. Files holding credentials
    (settings, the account registry, vaults, keys, tokens, certificates) are listed but never read.
    Everything read passes through the secret guard.
  - `tool_speed` shows the record and the ranking.
- **Measured:** `GET /kliveagent/stats/tools` (the record per tool and operation, and the ranking per goal).

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
  all tool output (`SecretEchoScrubber`). `klive-cdp` is the same engine for the terminal. Steward
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
- Kill switch: `DEMIURGE_INPUT_VERIFICATION=0` turns preflight/receipt off (input then goes out exactly
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

## KliveRAG — cross-system knowledge (shared with Demiurge)

`Services/KliveRAG` is a separate `OmniService` that gives both KliveAgent and the Demiurge task
force semantic + lexical recall across **all** of Klives' systems, plus live web. It is local and
free: MiniLM ONNX embeddings (the same `ReplicaEmbedder` Omniscience uses), a SQLite store with an
FTS5 lexical leg and BLOB vectors, brute-force cosine, and a self-hosted SearXNG container for web
search (no API keys).

- **Sources (connectors, incremental via watermarks/hashes):** Demiurge event logs + digests
  (live via `EventAppended`, cross-Steward), KliveAgent conversations + memories (turn-pair chunks),
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
  with no deadline. With the Demiurge event log indexed, a message full of common words ("use your
  computer to make a tumblr account…") made bm25 score millions of rows. That held KliveAgent at
  "preparing context" for 5–12 minutes, and every Demiurge wake ran the same query. A "??" nudge has
  no searchable words, which is why the nudges always got through. Measured on a synthetic
  1M-chunk index: 3.3 s warm and growing linearly with size before the fix; 0.2–0.5 s after.
- **Delivery:** a budgeted `[Relevant Knowledge]` block auto-injected into KliveAgent's system prompt
  (below the cache breakpoint, `KnowledgeBudget`) and into Demiurge wake seeds (a `RELEVANT KNOWLEDGE`
  section, own-Steward events excluded), **plus** the native tools `search_knowledge`,
  `read_knowledge_doc`, `web_search`, `web_fetch` on both agents.
- **Jobs:** background embed queue, live/periodic connector scans, and a nightly 04:15 sweep
  (`KliveRAGScheduler` — after Omniscience's 03:30) that reconciles connectors and evicts expired web
  docs. HTTP surface under `/kliverag/*` (`search`, `doc`, `stats`, `sources`, `reindex`, `websearch`).

## Talk mode (spoken conversation)

Talk mode is the spoken alternative to typing on the KliveAgent page. Klives speaks, KliveAgent answers out
loud, and the conversation is the same one the typed chat uses. Since Nov 2026 the voice is OpenAI's
**GPT-Live** (model `KliveAgent_LiveModel`, default `gpt-live-1.1`; any GPT-Live model id works), a
full-duplex voice model that listens while it speaks and hands reasoning and tool use to a backend agent.
KliveAgent is that backend. The AIRouter Whisper/Kokoro talk mode it replaced is gone (AIRouter's audio
client stays for Omniscience's media transcription).

**Three connections:**
- Browser ⇄ OpenAI, WebRTC: the microphone goes up, the voice comes down, and GPT-Live's events (transcripts,
  standby) arrive on the `oai-events` data channel. The server never touches audio. The browser may only
  mute, unmute and hang up on that channel (`client.data_channel.allowed_client_events`).
- Browser ⇄ KliveAgent, `/kliveagent/voice?conversationId=…` (typed `KliveAPI.CreateWebSocketRoute`,
  `kliveagent.chat.use`): the page sends its SDP offer; the server creates the session with
  `POST {OpenAIBaseURL}/live/sessions` (key, instructions and history stay on the server) and returns OpenAI's
  answer. The socket then carries `delegation`, `heard`, `turn` (the same shape the typed chat follows),
  `said`, `state`, `tool`, `usage`, `standby` and `closed`. A second tab for the same conversation takes it
  over. `/kliveagent/talk` (the old protocol) only tells an old page to reload (close 4403).
- KliveAgent ⇄ OpenAI, the sideband `wss://…/live/sessions/{id}/attach`: every transcript and delegation
  event, and the commands that steer the voice.

**A request, end to end.** GPT-Live decides a reply needs the backend and raises a client delegation
(`session.delegation.created`, which carries no text, only `offset_ms`). `LiveTranscript` takes every word
Klives began saying up to that moment that no earlier delegation took (it waits up to 1.2 s for the last
words' transcript). Anything said on the call in between (a clarifying question and its answer) goes along as
an `[On the call, before this]` turn note. The words are queued to KliveAgent as a spoken turn (steering if a
run is active). As the reply streams, `SpeechSegmenter` cuts it into speakable pieces (code, `screen` blocks,
scripts, URLs and IDs never reach the voice); the first piece goes as soon as it exists, later ones every
250 ms, as `session.commentary.append` under the delegation's id, which GPT-Live says in its own words. Tool
activity goes as silent `session.thinking.append` context, so "what are you doing?" has an answer. A run
quiet for `KliveAgent_LiveProgressSeconds` (default 10) gets a spoken "still working on it". When the run
ends, a thinking note says it is finished (client delegations have no completion event).

| Stage | Mechanism |
|---|---|
| Turn-taking, barge-in, acknowledgement | GPT-Live itself. It says a short acknowledgement after `KliveAgent_LiveAckDelayMs` (default 500 ms) unless KliveAgent's first words arrive sooner. |
| Prompt context | Memory and knowledge are fetched as soon as Klives pauses (`PrepareTalkTurnContextAsync`), before GPT-Live delegates. |
| Prefill | Unchanged: one append-only tool session per spoken conversation (`TalkEpoch`), warmed when talk opens and when he starts speaking after a while, rebuilt when its compatibility key changes, it goes idle past the prefix lifetime, exceeds `KliveAgent_TalkSessionMaxTokens`, or a tool exchange is unfinished. |
| Model slot | Unchanged: one AIRouter slot held back from background work while talking (`KliveAgent_TalkReserveSlot`). |
| Reasoning | Unchanged: spoken turns pinned to `KliveAgent_TalkThinking` (default low) on `KliveAgent_TalkModel`. |

**Session settings.** Voice `KliveAgent_LiveVoice` (a built-in such as `marin`, or a custom voice id; fixed per
session, so a new voice is a new call). Standby after `KliveAgent_LiveIdleSeconds` of silence (default 20;
0 = never; billed lower, woken by speech). The last `KliveAgent_LiveHistoryMessages` messages (default 20) seed
the voice. `KliveAgent_LiveExtraInstructions` is appended to the built-in instructions (`LiveInstructions`:
role, style, backchannel, interruption and delegation policies, as OpenAI's prompting guide lays out; for the
first model, `gpt-live-1`, the delegation wording becomes `<|SpawnThinking|>`). `KliveAgent_TalkVocabulary`
gives it names to expect; `KliveAgent_TalkPronunciations` respells what KliveAgent hands it.

**Local commands.** A delegated "stop" / "never mind" cancels the run without a new agent turn; "say that
again" hands the last reply back. The stop button and Escape send `hush`: GPT-Live gets an instruction to stop
talking, and the rest of that reply stays unsaid.

**OpenAI as a provider.** The KliveLLM router `openai` (key `OpenAILLMToken`, base `OpenAIBaseURL`, default
chat model `OpenAIModelID`) serves both chat completions and GPT-Live; switching it off on the KliveLLM page
turns the voice off. Each voice session is one ledger row on KliveAgent's `voice` lane, costed from GPT-Live's
own usage at `KliveAgent_LiveActivePerMinuteUsd` / `KliveAgent_LiveStandbyPerMinuteUsd` (0.05 / 0.02).

**Code:**
- Server:
  - `Services/KliveAgent/Talk/` — `KliveAgentVoiceSession` (the session), `LiveTranscript`, `LiveInstructions`,
    `SpeechSegmenter`, `TalkText` (commands), `KliveAgentTalkHub` (attachment + epoch), `TalkPorts`.
  - `Services/KliveLLM/OpenAILive.cs` — the GPT-Live client and sideband; `KliveLLM.OpenAI.cs` — key, client,
    ledger record.
- Website:
  - `composables/useTalkMode.ts` (WebRTC + the voice socket), `components/KliveAgent/TalkBar.vue`,
    `scripts/talk/prefs.ts`.
- Telemetry stages (Analytics → performance): `talk.liveCreate`, `talk.liveAttach`, `talk.delegationSettle`,
  `talk.firstWords` (delegation → KliveAgent's first words to the voice), `talk.firstAudio` (end of his words →
  the voice answering), `talk.continued` / `talk.rebuilt`, `talk.context(Prepared)`.

**Unverified until the first live call** (no OpenAI key was available when this was built): the exact moment
`offset_ms` marks relative to transcript timestamps, whether `session.commentary.append` with a null delegation
id is accepted (used for runs started by typing while on a call), and the sideband's audio reflection volume.
