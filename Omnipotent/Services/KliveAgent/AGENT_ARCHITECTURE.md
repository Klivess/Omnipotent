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
6. **Stop** when the model replies with no scripts (the final answer). Guardrails: stuck-loop
   detection (same script twice / same error 3×), a soft nudge at 12 steps, a hard cap at 30.

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
| Loop | Think→Script→Observe, stuck detection, no hard step cap (safety cap 30) | ReAct loop, usually bounded |
| Memory of its own actions | Now persisted + replayed into later turns (Phase 1) | Native via tool/assistant/tool-result messages |
| Streaming | Non-streaming today (Phase 2 adds it) | Streamed |
| Reliability | Retry/backoff on transient LLM errors (Phase 6) | Provider SDK retries |
| Context mgmt | Budgeted selection; compaction planned (Phase 8) | Token-counted, compaction/summarization |

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
  `execute_csharp` tool so the model uses the provider's structured `tool_calls` channel —
  removing the regex parsing and "don't emit XML" defensive rules — while keeping Roslyn's power.
- **Phase 8 – compaction.** Summarize the oldest turns near the budget instead of dropping them.
