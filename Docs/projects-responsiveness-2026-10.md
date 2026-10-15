# Projects responsiveness — 15 October 2026

Klives' report: agents stall with long gaps of no work, take ages to start after a resume, and the
Commander does not answer chat messages straight away. The timeline he shared showed about twenty
minutes of work followed by a forty-minute silence, with `wake-retry`, `wake-deferred` and
`wake-cancelled` events in the window.

No live API credential was available for this pass, so the causes below come from the code. The
changes also make every remaining wait visible in the timeline, so the next gap explains itself.

## Why replies were slow

- **The Commander can only speak at a turn boundary.** A message that arrives mid tool call (a
  browser flow, a terminal command) waits for the call to finish. Then it waits for a model turn in the
  shared AIRouter queue. If the Commander is asleep, it waits for a full cold wake (~59K-token prefill).
- **Klives' turns had no priority.** Commander turns were classed as ordinary agent work. With the
  scheduler in `Fifo` mode (the live setting in September), arrival order was the only ordering, so a
  reply to Klives queued behind every agent turn submitted before it.
- **The post-wake digest rebuild sat on the critical path.** It ran before the single-flight marker was
  released, and it is the lowest-priority request in the queue. A message arriving as a wake ended
  waited for that full utility-model round trip.
- **Backoffs blocked messages.** An open failure circuit held Klives' message in the inbox for up to
  15 minutes.

## Why there were gaps

- **Keepalive backoff after failed wakes.** After two failed wakes the Commander rested 30 minutes,
  after three 60, up to 4 hours. Two transient infrastructure failures alone produce a gap like the
  one in the screenshot.
- **No momentum between wakes.** A Commander that ended a productive wake with open steps, and nobody
  else working on them, was not woken again until the 14-minute keepalive. By then its prompt prefix
  had died, so the next wake also paid a cold restart.
- **Capacity.** Three AIRouter slots serve the whole fleet. The wake-boundary budget admits only as
  many live conversations as can stay cache-warm, and in `Enforce` mode the scheduler runs one cold
  prefill at a time fleet-wide. Wakes refused by either left no trace in the timeline.
- **Restart ramp.** After every process start, the fleet began at 3 live conversations and added one
  every 3 minutes.

## Why resume was slow

The resume wake was not treated as Klives-initiated. It went through the fleet budget and then the
cold-start queue like any background wake. Workers resumed through the same budget with no priority
over the rest of the fleet.

## Changes

- **Instant replies** (`ProjectFrontDesk`, setting `InstantRepliesEnabled`, on by default). When
  Klives messages a Commander, one small interactive-priority model call over a compact project
  snapshot (focus, active step, live activity, the last ~14 events, recent chat) answers in the
  Commander's voice within seconds. The rules forbid claiming anything the snapshot does not show.
  The message is still delivered to the Commander, which is told what was already said, so its own
  `reply_to_klives` carries substance. The reply is skipped when the Commander answered first, and it is
  labelled "instant reply" in the chat. Discord gets the same mirror. Broadcasts and machine notices
  get no instant reply.
- **Klives first in every mode.** The AIRouter queue orders `Interactive`/`Audio` requests ahead of
  background work in `Fifo` and `Observe` too. Commander turns are sent as `Interactive` for up to 3
  turns after a Klives message, or until it replies, and for the first turn after a resume. A Klives
  message also holds one slot back from background work for 3 minutes.
- **Messages and resume retry immediately.** A Klives message (or resume) clears provider backoff and
  half-opens the circuit instead of waiting it out. If the cause persists, the probe defers again on
  its own.
- **Digest rebuild in the background.** It now runs after the next wake has been started, one per
  project at a time, with a follow-up pass if another wake ended meanwhile. Digest writes are
  merge-safe (`ProjectDigestStore.Update`), so the rebuild can never erase the next wake's marker,
  and a late rebuild never rolls back a newer one.
- **Momentum** (`ProjectMomentum`, setting `MomentumEnabled`, on by default). A wake that made verified
  progress, while open steps remain, no worker is awake, no approval is pending and no hook was armed,
  queues one follow-up 45 seconds later, inside the prefix lifetime. It chains only while each wake
  stays productive.
- **Gentler failure backoff.** 5, 10, 20, 40, 80, then 120 minutes, where it used to be 30 minutes up
  to 4 hours.
- **Resume first.** The resumed Commander skips the fleet budget. The two workers with live milestones
  do too; the rest jump the deferral queue (`TryAdmit` priority).
- **Faster restart ramp.** One conversation per 90 seconds instead of per 3 minutes.
- **Every wait is visible.** A new `wake-waiting` event records why an agent could not start:
  waiting for LLM capacity, provider backoff, open circuit, or failure backoff. It is written once per
  reason and kept out of agent context. `GET /projects/schedule?projectID=` reports each agent as
  working, waiting, held, sleeping or done, with its next run time and the AIRouter/admission state.
  The project Overview's Now card and the Chat panel show it.
- Leak fix: throwaway utility sessions (digest rebuild, triage) are now dropped from KliveLLM's session
  table after use.
- Website: the timeline lanes are dark again. An earlier colour pass had turned them into a pale band.

## The honest limit

Three slots are the ceiling. A warm turn holds a slot for 3–17 s and a cold one for 60–120 s, so
the fleet can keep only a handful of conversations working at once. With nine or more projects,
each with several agents, some of them will always be waiting. These changes make that wait shorter,
prioritised and visible. Only more capacity removes it (more slots, or an overflow route).

## Verification

- C#: `ProjectsResponsivenessTests`, `AIRouterHumanFirstTests` and the existing Projects and KliveLLM
  suites pass: 1422 of 1423, plus 2 skipped. The one failure,
  `BriefContinuityTests.MultiWakeReplay_…`, is a prompt-economics benchmark that already failed at
  commit `56e2d35`, before this work (ratio 0.856 against a 0.85 bar).
- Website: typecheck clean apart from the existing `scripts/agentMarkdown.ts` errors. Every Projects
  Playwright spec passes, including a new one for the schedule, the instant-reply label and the
  timeline colour.
- Not deployed and not observed live. Watch the first hour: the instant-reply latency, how often
  momentum fires, and the `wake-waiting` notes in the timeline.
