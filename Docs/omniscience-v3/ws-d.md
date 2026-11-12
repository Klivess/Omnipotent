# WS-D — Replica (Omniscience v3 Phase 2)

Branch `claude/omniscience-v3-ws-d-21c7c3`, cut from the Phase 1 commit `3dc87df`. Code lives in
`Omnipotent/Services/OmniscienceV3/Replica/` (module id `replica`, its own intel migration line `ReplicaSchema`).
This file is the workstream's progress record: what is done, what is not, and why. It is kept current as work lands.

## Status (2026-11-12)
Everything below is committed; 194 v3 tests pass on this branch. The workstream's own session stopped on 2026-11-09; since
2026-11-12 the coordinating session works here.

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
| Versioned trainer, readiness, stale marking and retrain | done; `ReplicaTrainingTests` (14): every stage, refusals with the numbers, single flight, a crash mid-stage reclaimed, encoder failure degrades, the previous version keeps serving, cancel, minors blocked and purged |
| Fidelity gates | done; `ReplicaFidelityGateTests` (5): zero post-cutoff tokens in an as-of prompt, the holdout never reaches a prompt-visible source, a stable stratified holdout, distinct personas stay distinct, tuning and reporting on separate splits |
| Chat | done; `ReplicaChatTests` (10) |
| Rehearsal (X55), drafts in Klives's voice (X57), Group Simulation (X63), social tactics X53, X54, X56, X58–X62, X64, X65 | done; `ReplicaSocialTests` (4): the catalogue is well formed, every tactic runs as a job on the seeded people, simulations and generated drafts are marked as such in their data, minors are refused before anything runs. **These are run-through tests with a scripted model: what each report says is not checked in depth.** |
| Evaluator, auto-tune, blind test | built; the evaluator is covered by the split test above. **The blind test (Klives guesses real vs replica) has no test.** |
| Routes (`ReplicaRoutes.cs`) | built, **never exercised** (no route test harness; first real exercise is the shadow run) |
| Fidelity on real people | **not measured.** The acceptance target (median judge discrimination ≤ 65% for people with ≥ 1,000 replies) can only be checked on the real corpus, in the shadow run. |
| Traceability | **rows of this workstream are not ticked**: do that row by row against the tests before the merge. |

Defect found by the 2026-11-12 tests and fixed: a draft in Klives's voice (X57) was marked as generated only in its wording;
its data now says `generated: true`.

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
