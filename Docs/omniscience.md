# Omniscience workspace and temporal Replica

Omniscience is the local evidence and behavioural-analysis module. The Nuxt dashboard is an investigation workspace over linked message sources, distilled facts, relationship evidence and clearly labelled AI simulations. Raw messages, operator notes and generated Replica chats remain separate: generated text is never promoted into person facts or training evidence.

## Workspace capabilities

The v2 workspace adds these operator-facing capabilities:

1. **O(1) people directory** with search, tier/platform filters, sorting and pagination.
2. **Unified command palette** (`Ctrl+K`) over people, facts, notes, saved queries and actions.
3. **Scoped message search** by text, person, conversation, date, context and platform.
4. **Saved investigations** with names, use counts and one-click reruns.
5. **Recent query history** and reusable shared scopes.
6. **CSV evidence export**.
7. **Pinned people** with a persistent working set.
8. **Investigator notes and tags**, isolated from machine-derived facts.
9. **Evidence timelines** joining messages, facts, identity changes, profiles and notes.
10. **Activity analytics** with daily volume, UTC hour heatmaps, trends and top conversations.
11. **Person comparison** with coverage ratios, shared conversations and direct relationship links.
12. **Interactive relationship graphs** with typed, confidence-scored edges and two-hop expansion.
13. **JSON and Markdown dossier exports** including provenance.
14. **Data-health dashboard** for source status, activity summaries, embedding coverage and failed jobs.
15. **Notification centre** for completed and failed background work.
16. **Temporal Replica chats** with annual eras or an arbitrary as-of date.
17. **DM, group-DM and server context conditioning**.
18. **Measured era/style fingerprints** covering length, casing, punctuation, emoji, laughter and era vocabulary.
19. **Same-prompt era comparison** using independently scoped generations.
20. **Separate evidence consulted panels** for Replica replies, gated by raw-message permission.
21. **Replica feedback**, readiness/staleness indicators and long-term per-chat memory.

Existing source management/backfill, semantic search, cited analyst Q&A, review queues, watchlists, radar, daily briefings, identity resolution, personality profiles and fidelity reports remain available from the same shell.

## Read-path reliability

The old directory and conversation routes computed `GROUP BY` aggregates over the full `messages` table on every cold request. On a multi-million-row corpus that kept requests in the read gate until they returned `503 busy`.

Schema v12 introduces two small materialised summaries:

- `person_message_counts`
- `conversation_message_counts`

`IngestPipeline` updates them in the same transaction as each new message. `ActivitySummaryStore` performs an exact one-time backfill before enabling ingesters, and person merges rebuild affected rows. The legacy routes and the v2 directory read these summaries instead of rescanning the corpus. `ActivitySummaryStore.GetStatus()` exposes warm-up state to the dashboard.

`WorkspaceExplorer` owns read-side queries and `WorkspaceStore` owns operator-created state. `WorkspaceRoutes` registers narrow, permission-checked endpoints under `/omniscience/workspace`. The frontend's `useOmniscienceApi` retries bounded warm-up responses, preserves last-good state and reports section-level failures rather than silently rendering empty panels.

## Temporal Replica v2

The Replica is a prompt/RAG simulation, not a fine-tuned model and not the real person. Every chat is explicitly labelled accordingly.

### Correctness fixes

- Local `KliveLLM` now honours caller-supplied system prompts just like remote inference. Previously it silently replaced every Replica/profile/extraction prompt with `You are a helpful assistant.`
- Relational training now samples the **subject's** messages in conversations shared with each relation. The old query sampled the relation's own writing and contaminated the persona.
- Chat/person ownership is validated, history is ordered by message ID, and the production and benchmark generation paths match.
- Archived content is explicitly treated as untrusted quoted evidence, not instructions.
- Persona prompts are budgeted and deduplicated, with short and diverse exemplars represented alongside longer messages.

### Temporal scoping

`ReplicaChatOptions` carries an as-of timestamp and conversation context. Every relevant input respects that scope:

- topical message retrieval;
- stimulus→reply retrieval;
- lifetime exemplars and conversational reflexes;
- exact style fingerprints;
- fact and relationship timestamps;
- personality-profile selection;
- benchmark holdouts.

Annual `replica_era_profiles` power the era map. Chat does **not** reuse a full-year fingerprint for an arbitrary historical date: `ReplicaTemporalAnalyzer.GetStyleAsync` recomputes against the exact cutoff and applies evaluation exclusions. Era affinity downweights older evidence without zeroing it, while matching conversation contexts receive a bounded boost.

`ReplicaChatMemory` keeps a compact summary of older simulated turns plus twelve recent verbatim rows. Its summaries live only on `replica_chats`; they never enter `person_facts`, analytics or future training data.

### Evidence and evaluation

Assistant rows persist temporal scope, measured style and the evidence consulted. Raw evidence is only returned by `/omniscience/replica/evidence`, which requires `omniscience.messages.read`; ordinary Replica chat permission does not bypass raw-message access.

Fidelity uses short as well as long replies, excludes all holdout reply/stimulus IDs from every prompt-visible source, evaluates at each pair's timestamp, records generation/scoring failures, and reports attempted/scored/failed counts with the actual model metadata. Its score is explicitly experimental, not a calibrated accuracy percentage.

## Schema v12

In addition to activity summaries, migration v12 adds:

- `replica_era_profiles` and `replica_feedback`;
- temporal/memory columns on `replica_chats`;
- temporal, evidence, style and pipeline-version columns on `replica_chat_messages`;
- method/metadata columns on `replica_fidelity_runs`;
- `workspace_saved_queries`, `workspace_query_history`, `workspace_notes` and `workspace_pins`.

Migrations are version-gated and idempotent. Tests verify a fresh database reaches v12, all tables/columns exist, summaries remain exact after ingest/backfill/merge, temporal cutoffs exclude future evidence, and workspace queries work against a temporary SQLite database.

## Main API groups

- `/omniscience/workspace/bootstrap`
- `/omniscience/workspace/directory`
- `/omniscience/workspace/search`
- `/omniscience/workspace/queries*`, `/history*`, `/pins/set`, `/notes*`
- `/omniscience/workspace/timeline`, `/activity`, `/compare`, `/graph`
- `/omniscience/workspace/export/person`, `/search/export`, `/health`
- `/omniscience/replica/eras`, `/compare`, `/evidence`, `/feedback`
- `/omniscience/replica/chats/settings` and the existing chat/training/fidelity routes

The older routes remain for compatibility and for the dashboard's legacy fallback.

## Verification

```powershell
$env:DOTNET_ROLL_FORWARD='Major'
dotnet test Omnipotent.Tests/Omnipotent.Tests.csproj --filter "FullyQualifiedName~Omniscience|FullyQualifiedName~KliveLLMSystemPromptTests" --nologo --verbosity minimal

cd C:\Projects\Websites\klives-management-website
npx vue-tsc --noEmit
npm run build
npx playwright test tests/e2e/omniscience.spec.ts --project=chromium
```

The browser suite covers the overview, command palette, scoped search/save/export, timeline/activity/health, and temporal Replica chat/evidence/era comparison using a fully mocked API (no real person data).
