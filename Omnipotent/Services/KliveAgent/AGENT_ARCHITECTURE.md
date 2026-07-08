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
   (`KliveAgent_MaxRunTokens` / `KliveAgent_MaxRunMinutes`) that warns at 80% and force-finalises at
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
