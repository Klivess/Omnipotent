# Agents and retrieval

Three modules make up the AI layer, and they are deliberately different shapes:

| Module | Shape | Runs for |
|---|---|---|
| **KliveAgent** | Single conversational agent, arbitrary C# as its action surface | Seconds to minutes |
| **Projects** | Multi-agent task force with its own containers and budgets | Days to weeks |
| **KliveRAG** | Retrieval service, no agency of its own | Continuous indexing |

For KliveAgent's loop in full detail — prompt assembly, parsing, guardrails, the shipped-versus-open
status of each hardening phase — see
[`AGENT_ARCHITECTURE.md`](../Omnipotent/Services/KliveAgent/AGENT_ARCHITECTURE.md), which is
maintained alongside the code. This document covers how the three fit together.

## KliveAgent — the platform operator

Source: [`Omnipotent/Services/KliveAgent/`](../Omnipotent/Services/KliveAgent)

KliveAgent is not a coding agent for the Omnipotent source. It is an operator for the **running**
platform: an assistant that reads live service state, calls module methods, pulls logs, spawns
background work and remembers what it did.

Its distinguishing decision is the action surface. Most agents are given a fixed set of JSON-schema
tools. KliveAgent is given **the compiler**. It emits C# in `{{{ … }}}` blocks, which
[`KliveAgentScriptEngine`](../Omnipotent/Services/KliveAgent/KliveAgentScriptEngine.cs) compiles with
Roslyn and executes in-process against the live service graph — the same
`GetServicesByType<T>` / `ExecuteServiceMethod<T>` handles any module uses to reach its neighbours
(see [the runtime doc](service-runtime.md)).

The consequence is that **the tool catalogue is the platform itself**. Adding a module adds agent
capability with no tool definition, no schema and no registration. One script can do discovery,
action and logging in a single step, which a fixed tool list cannot express without several
round trips.

The trade-off is real and worth stating: this relies on text parsing and in-process compilation
rather than a provider's structured tool-calling channel, and a script that hangs is abandoned rather
than force-killed — a process-isolated sandbox is the main open item. Native `execute_csharp` tool
calling now runs *alongside* the text protocol rather than replacing it, because local models and
prose replies still need the parser.

What surrounds the loop:

- **Context budgeting** — [`KliveAgentContextBudget`](../Omnipotent/Services/KliveAgent/KliveAgentContextBudget.cs)
  assigns separate budgets to the repo map, memories, conversation history, per-script output and
  replayed prior scripts. History turns are scored by recency plus keyword overlap and greedily fitted;
  the oldest are compacted into a summary rather than dropped.
- **Persistent memory** — [`KliveAgentMemory`](../Omnipotent/Services/KliveAgent/KliveAgentMemory.cs)
  gives cross-conversation recall, BM25-ranked, deduplicated on save.
- **Run budgets** — a per-run token *and* wall-clock budget warns at 80% and force-finalises at 100%.
  There is deliberately no fixed iteration cap: a task takes the steps it needs, and cost is what is
  bounded.
- **Breakers** — a two-strike breaker on no-op or malformed-envelope turns, plus an external
  zero-progress stall watchdog.
- **Scheduling** — the agent can schedule its own future work, giving it prospective memory rather
  than only reactive turns.
- **Streaming** — token streaming to the dashboard, including an immediate acknowledgement while
  data-gathering scripts run concurrently.

Access is owner-only: routes require `KMPermissions.Klives`, and over Discord anyone else reaches the
plain chatbot instead.

## Projects — the long-running task force

Source: [`Omnipotent/Services/Projects/`](../Omnipotent/Services/Projects)

Where KliveAgent handles a request, Projects owns an objective for weeks. A
[`ProjectCommanderRunner`](../Omnipotent/Services/Projects/ProjectCommanderRunner.cs) plans and
delegates; [`ProjectSubAgentManager`](../Omnipotent/Services/Projects/ProjectSubAgentManager.cs)
staffs workers against milestones, mixing bounded "task" missions with ongoing "standing" ones.

The hard problems here are not prompting problems. They are the ones any autonomous system hits once
it runs unattended for longer than a context window:

**Continuity.** Agents wake, work a slice, and sleep. Everything that matters is in durable stores —
event logs, digests, a plan of record, budget ledgers, observables — so the next wake reconstructs
state rather than inheriting it. `ProjectWakeCycle`, `ProjectDigestStore` and `ProjectLoopRecovery`
carry that.

**Not repeating itself.** A journal of tool calls plus convergence detection
(`ProjectToolCallJournal`, `ProjectToolCallConvergence`) catches an agent re-running the same failing
action across wakes, which is invisible within any single wake.

**Bounded cost.** `ProjectBudgetLedger` and `ProjectTokenUsageStore` track spend per project;
`ProjectTierRouter` routes work to a model tier proportional to its difficulty rather than sending
everything to the largest model.

**A bounded tool surface.** Offered tools are hard-capped at 64
([`ProjectToolFacade`](../Omnipotent/Services/Projects/ProjectToolFacade.cs)); related tools fold into
one definition with an `op` parameter. New capabilities are added as operations, not as new tools,
because tool-list bloat degrades selection accuracy.

**Real environments.** [`Containers/`](../Omnipotent/Services/Projects/Containers) gives each project
an isolated Docker desktop, reachable over VNC. Agents get a browser and a shell; the owner can take
over the same desktop live from the dashboard to clear a CAPTCHA or an OAuth prompt.

**Human checkpoints.** `ProjectGateManager` and `ProjectGrandPlanStore` stop the task force at
plan approval, spending and escalation — not at every step. `ProjectCouncilRunner` runs adversarial
review of a plan before it is committed.

## KliveRAG — shared retrieval

Source: [`Omnipotent/Services/KliveRAG/`](../Omnipotent/Services/KliveRAG)

Both agents share one retrieval service, which is local and free to run: MiniLM ONNX embeddings, a
SQLite store with an FTS5 lexical leg alongside BLOB vectors, brute-force cosine similarity, and a
self-hosted SearXNG container for web search — no external API keys.

**Hybrid retrieval.** Embedding search and FTS5 search run in parallel, fuse by Reciprocal Rank
Fusion, then get a recency boost and a per-document diversity cap. Lexical search alone misses
paraphrase; vector search alone misses exact identifiers and error strings. Fusion is cheaper and
more robust than tuning either one.

**Connectors** index incrementally by watermark and hash: Projects event logs and digests (live, and
across projects), KliveAgent conversations and memories as turn-pair chunks, Omniscience's distilled
knowledge, repository markdown, and TTL'd cached web pages. Omniscience's raw message corpus is
**federated at query time** rather than re-embedded — it already has its own index, and duplicating it
would double storage for no recall gain.

**Delivery is both push and pull.** A budgeted `[Relevant Knowledge]` block is auto-injected into
KliveAgent's system prompt and into Projects wake seeds, *and* both agents get explicit
`search_knowledge`, `read_knowledge_doc`, `web_search` and `web_fetch` tools. Auto-injection races a
~300–400 ms timeout and **fails soft**, returning empty: retrieval degrading must never be able to
stall a turn.

A background embed queue, live connector scans and a nightly sweep keep the index current and evict
expired web documents.

## How they compose

```
KliveAgent ──┐                    ┌── live service graph (Roslyn, in-process)
             ├── KliveRAG ────────┤
Projects  ───┘   hybrid recall    └── Omniscience (federated at query time)
```

Nothing here is a wrapper around a single model call. The model is one component inside a runtime
that owns the context budget, the action surface, the durable state and the cost ceiling — and each
of those exists because the version without it failed in a specific, observed way.
