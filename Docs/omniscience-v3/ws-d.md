# WS-D — Replica (Omniscience v3 Phase 2)

Branch `claude/omniscience-v3-ws-d-21c7c3`, cut from the Phase 1 commit `3dc87df`. Code lives in
`Omnipotent/Services/OmniscienceV3/Replica/` (module id `replica`, its own intel migration line `ReplicaSchema`).
This file is the workstream's progress record: what is done, what is not, and why. It is kept current as work lands.

## Status (in progress)

| Area | State |
|---|---|
| Schema (`ReplicaSchema`, intel migration line `replica` v1) | done |
| Text/style measurements (graphemes, emoji, tokens, BM25, vectors, logistic model) | done |
| Indexer (pairs, bursts, stimuli, addressees, initiations, authored-text filters, minor periods) | done |
| Eras (change points), holdout planner (stratified, stable, shadows), persona slices | done |
| Style fingerprint (as-of windows, learned laughter, distinctive vocabulary, ≥90% transforms) | done |
| Persona (dated opinions, relationships, self, interests, jokes; verified), reflex classifier, facts reader | done |
| Composer (as-of, context, addressee, packed to the real window, cache-shaped prefix) | done |
| Generator + judge (N candidates, burst sampling, LLM judge on the cached prefix, assistant-voice model) | done |
| Evaluator + auto-tune, blind test, readiness, versioned trainer, chat, Rehearsal, Group Simulation, tactics | in progress |
| Routes, registration, api-contracts, tests, traceability | in progress |

## Decisions (defaults chosen without asking; each is reversible)
- **Encoder:** the replica codes against `IEmbeddingEncoder` (WS-C). `ReplicaModule` takes an optional encoder resolver;
  without an encoder it runs on BM25 retrieval and deterministic judge signals only, and every version records
  `encoder_model = null` plus a degraded note, so the UI says so. When WS-C's encoder is wired in, versions built without
  it are marked stale and retrained (the encoder id is part of the version). **Integrator:** pass WS-C's encoder to
  `new Replica.ReplicaModule(...)` in `OmniscienceV3.BuildModules()`.
- **What you measure is what you serve:** a version's holdout (and its shadows) is excluded from everything that version
  serves — exemplars, style, persona, reflexes — not only from evaluation. The holdout is ~10% (20–300 pairs), so chat loses
  little, and the fidelity reported is the fidelity of the artifact Klives talks to.
- **Stable holdout:** held-out pairs are chosen by a stable hash within era × context strata, so the same exchanges stay
  held out across versions and fidelity trends compare like with like.
- **Persona visibility:** persona items are written per slice (≤ 4 months, one era, one model window) and become visible to an
  as-of only once their slice has ended; an as-of replica never sees a line written from later evidence.
- **Minor periods:** messages a person wrote while MinorGuard says they were (likely) a minor are never read for their
  replica, and as-of chats into that period are refused.
- **Deleted messages** are never shown (a deleted stimulus becomes "[deleted message]").
