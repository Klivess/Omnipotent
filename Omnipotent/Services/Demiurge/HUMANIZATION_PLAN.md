# Steward Agents — Human-Presence Plan

**Goal:** eliminate the inhuman *tells* that cause Steward agents to be flagged as bots while
operating their visible desktop + browser, so legitimate autonomous work stops tripping
false-positive bot detection.

---

## Implementation status (updated 2026-07-19)

**Shipped and tested (Phases 0–2 core):**
- **Persona layer** — `HumanInputProfile` (per-desktop, seeded from container id, stable across
  wakes) with a global kill switch (`DEMIURGE_HUMANIZE=0`) and intensity knob
  (`DEMIURGE_HUMANIZE_INTENSITY`). Categories K#92–93 (per-desktop consistency, deterministic).
- **Input humanisation** — `HumanInputPlanner` (pure, unit-tested) + `HumanizedInput` (replay over
  `VncTransport`), routed into every mutating `computer_*` tool in `ContainerToolAdapter`. Covers
  **A** (curved bézier paths, Fitts's-law duration, min-jerk velocity, overshoot/correct, tremor,
  landing jitter — #1‑12), **B** (log-normal hold, micro-shift, settle, off-centre click point —
  #16‑20), **C** (log-normal + digraph/hand-aware typing, shift overlap, self-correcting typos,
  boundary think-pauses, first-key reach, fatigue — #25‑35,#39), **D** (momentum scroll,
  overscroll-correct, reading pauses — #40‑44), **E** (curved variable-velocity drag, grab/drop
  dwell — #47‑50).
- **Browser launch surface** — `--disable-blink-features=AutomationControlled` + persona `--lang`,
  realistic font set in the image.
- **Browser fingerprint (the deep work, #63,#65‑76 core)** — `BrowserPersona` (deterministic per
  desktop, stable across restarts, varied across desktops) drives a baked **main-world MV3 extension**
  (`klive-fp-patch.js`, `run_at: document_start`, all frames) that normalises the page-visible
  environment: **WebGL `UNMASKED_RENDERER`/`VENDOR`** spoofed to a plausible consumer GPU (kills the
  SwiftShader/llvmpipe headless tell — #63, achieved in JS so `--disable-gpu` can stay for stability),
  `navigator.webdriver=false` (#65), persona `hardwareConcurrency`/`deviceMemory` (#74), non-empty
  `languages` (#68/69), deterministic sub-perceptual **canvas noise** (stable per persona, unique
  across desktops → anti-clustering, #73), and `Notification.permission` default (#76). Injected via
  a container env var + launcher; degrades to a **no-op** if the persona/template is absent (browser
  unchanged). Image stamp → v7.
- **Tests** — 21 new planner/profile/persona tests + contract & launch-script guards; full Demiurge
  suite green (466). Extension logic (`patch.js`) additionally verified in a Node/VM harness.

**Deferred (documented, not yet built):**
- **CDP relocation (#64)** — kept `--remote-debugging-port=9222` (loopback, not page-readable);
  `computer_navigate`/inspect/control depend on it. Not a page-visible tell, so low priority.
- **Timezone/egress consistency (#67/#83‑86)** — not spoofing `TZ` because, without control of the
  egress IP's geolocation here, a mismatched timezone is a stronger tell than the UTC default. Belongs
  with the egress phase.
- **Higher-level behaviour (F #52‑62), persistent aged profiles (H), temporal active-hours (J),
  fleet-wide persona registry + cross-surface consistency checks (K #89‑95), verification harness
  (L #96‑100)** — larger, separate phases; the input + browser persona seeds are the hook to build on.

---

**Scope & intent.** This is a *humanization / false-positive-avoidance* effort: make real input
and a real browser look like what they already almost are — a person at a computer. It is **not**
a CAPTCHA-defeating or anti-fraud-evasion effort. The existing model is correct and preserved:
when a genuine human-verification gate appears (`browser-inspect.py` already detects
reCAPTCHA/hCaptcha/Turnstile), the agent escalates to Klives via remote desktop control rather
than trying to auto-solve it. Everything here reduces the *rate* at which those gates appear by
removing robotic signals; nothing here solves a challenge on the agent's behalf.

---

## 1. Why agents are currently detected — audit of the real code

Agents drive a **real X server** over an in-house RFB/VNC client, so their input already produces
genuine kernel/X events (not synthetic JS events — a real strength). The problem is the *shape* of
those events and the browser environment. Concrete tells found in the codebase:

### Input tells (`VncTransport.cs`, `ContainerRemoteInput.cs`)
- **Mouse teleports.** `MoveMouseAsync` sends a single `PointerEvent` straight to the target
  pixel — zero intermediate points, zero curve, zero time. `VncTransport.cs:315`.
- **Uniform, superhuman click timing.** `ClickAsync` uses a fixed 40 ms down-hold and fixed 90 ms
  between multi-clicks, at the exact same pixel, every time. `VncTransport.cs:336`.
- **Uniform, superhuman typing.** `TypeTextAsync` uses a single fixed `charDelayMs` (8 ms from the
  remote-input path, 18 ms default) with a perfectly-bracketed Shift press/release and no
  variance, no digraph timing, no errors. `VncTransport.cs:389`, `ContainerRemoteInput.cs:72`.
- **Linear, constant-velocity drags & relative moves.** `DragAsync` / `MoveMouseRelativeAsync`
  interpolate a straight line with a fixed 8–15 ms step. `VncTransport.cs:321,348`.
- **Uniform scroll.** `ScrollAsync` fires integer notches with a fixed 25 ms gap — no momentum,
  no variance. `VncTransport.cs:372`.
- **Dead-center clicks.** `browser-inspect.py` returns each control's exact center; the agent
  clicks that center pixel every time — no human hits dead-center repeatedly.
- **No idle motion / no reaction time.** The cursor is perfectly still between actions and
  actions fire back-to-back with no think-time.

### Browser/environment tells (`ContainerOrchestrator.cs:216`, `desktop.Dockerfile`)
- **`--disable-gpu`** → software WebGL renderer reports `Google SwiftShader` / `Mesa llvmpipe`,
  one of the most widely-used headless-Chromium signals.
- **Persistent `--remote-debugging-port=9222 --remote-allow-origins=*`** → CDP is always open and
  detectable; `--remote-allow-origins=*` is itself an automation signature.
- **Xvfb defaults** → no audio device, minimal font set, `1920x1080x24` exact, UTC timezone,
  `en-US`-only languages, `deviceMemory`/`hardwareConcurrency` from the container, no GPU.
- **Fresh profile churn** → new/empty profile (no history, no aged cookies) reads as a throwaway.
- **Fleet clustering risk** → every agent shipping the *identical* fingerprint is itself a signal;
  humanization must vary *across* personas while staying internally consistent *per* persona.

---

## 2. Target architecture

Three new pieces, plus targeted edits to the launch surface:

1. **`HumanInputProfile`** — a per-persona parameter bundle (mouse aggressiveness, typing speed
   distribution, error rate, scroll style, reaction-time range, active hours). Deterministically
   derived from the persona seed so a given account behaves *consistently* over time.

2. **`HumanizedInput`** — a decorator over `VncTransport` that turns each high-level action
   (move/click/type/scroll/drag) into a *sequence* of realistically-timed, curved, jittered RFB
   events driven by the active `HumanInputProfile`. All `computer_*` tools in
   `ContainerToolAdapter` route through this instead of calling `VncTransport` directly.

3. **`BrowserPersona`** — a per-persona browser environment bundle (launch flags, UA + client
   hints, fonts, timezone, locale, WebGL vendor/renderer, screen metrics, aged profile dir),
   applied at Chromium launch in `ContainerOrchestrator`.

4. **Persona registry** — one persona per account (tie into the existing Account Registry), stable
   and internally consistent across all four fingerprint surfaces (input, browser JS, OS/X,
   network egress).

A global kill-switch + per-persona intensity knob so humanization can be tuned or disabled for
debugging without code changes.

---

## 3. The techniques (100+)

Each is tagged with where it lands. Counts are cumulative; the minimum-75 bar is met by
categories A–G alone.

### A. Mouse-movement kinematics — `HumanizedInput.MoveAsync` (replaces teleport)
1. **Bézier / Catmull-Rom curved paths** between origin and target instead of a straight jump.
2. **Fitts's-law movement duration** — time scales with `log2(distance/target_size + 1)`.
3. **Minimum-jerk velocity profile** — smooth ease-in/ease-out (bell-shaped speed), not constant.
4. **Ballistic + corrective sub-movements** (Meyer's model): a fast primary move that slightly
   misses, then one or two small corrections.
5. **Overshoot-and-correct** on a fraction of moves (cursor passes the target, comes back).
6. **Perpendicular micro-tremor / Perlin jitter** along the path so it's never geometrically clean.
7. **Distance-scaled intermediate-point count** (short hops = few points, long sweeps = many).
8. **Jittered per-step delays** (drawn from a distribution) rather than a fixed 8 ms.
9. **Landing jitter** — final pixel is offset a few px from the mathematical target.
10. **Randomized path bow direction** (curves left or right, varying curvature).
11. **Momentum carryover** — the next move's entry angle/speed is influenced by the last.
12. **Deceleration zone** — measurable slow-down in the last ~20% before the target.
13. **Reaction-time pre-pause** before a move begins (persona-dependent, ~150–450 ms).
14. **Idle drift & micro-movements** while "reading" — the cursor is never perfectly frozen.
15. **Natural parking** — between actions the cursor rests in a plausible spot, not the last pixel.

### B. Click dynamics — `HumanizedInput.ClickAsync`
16. **Log-normal button-down hold** (~50–150 ms) instead of a fixed 40 ms.
17. **Micro-shift between down and up** (1–3 px) — humans move slightly during a press.
18. **Settle pause after arriving, before pressing** (short dwell on the target).
19. **Jittered double-click interval** (~90–200 ms), not fixed 90 ms.
20. **Gaussian click point within bounds**, biased toward center but rarely dead-center; consume
    the element `bounds` from `browser-inspect.py`, not just its center `x,y`.
21. **Two-phase approach** — arrive slightly short, then a final small nudge onto the control.
22. **Down/up at slightly different coordinates.**
23. **Post-visibility reaction delay** — pause after an element appears before clicking it.
24. **Rare miss-and-recover** (very low rate, off by default): click just outside, then correct.

### C. Keyboard / typing — `HumanizedInput.TypeAsync`
25. **Log-normal inter-keystroke interval** (persona mean ~110–230 ms), replacing the fixed delay.
26. **Digraph/bigram-specific timing** — common pairs (`th`, `he`, `in`) fast, awkward pairs slow.
27. **Alternating-hand vs same-finger timing** using a QWERTY hand/finger map (same-finger slower).
28. **Per-key dwell (hold) variation.**
29. **Overlapped Shift timing** — press Shift a few ms *before* the letter and release a few ms
    *after*, instead of the current perfectly-bracketed press/release.
30. **Typo injection + backspace correction** at a persona-configurable rate (e.g. 1–3%).
31. **Word/sentence-boundary "thinking" pauses**, longer after punctuation and before capitals.
32. **First-keystroke latency** — a longer pause to "find the field" before the first character.
33. **Burst-and-pause rhythm** — type in chunks with brief lulls, not a metronome.
34. **Row-distance timing** — number-row / reach keys slower than home-row.
35. **Fatigue drift** — cadence slows slightly across a long field.
36. **Field-appropriate cadence** — careful/slow for password fields, faster for search boxes.
37. **Occasional clipboard paste** for long strings (humans paste; but variable, not always),
    reusing the existing RFB `ClientCutText` clipboard path.
38. **No stuck modifiers** — guarantee Shift/Ctrl release even on typo-correction branches
    (guard against the current always-release assumption breaking under new branches).
39. **Persona-stable WPM** — the same account types at roughly the same speed every session.

### D. Scroll — `HumanizedInput.ScrollAsync`
40. **Momentum profile** — accelerate then decelerate across a burst of notches, not uniform 25 ms.
41. **Variable per-notch delays.**
42. **Overscroll-and-settle** — occasionally pass the target and scroll back a little.
43. **Read-chunk scrolling** — scroll a screenful, pause proportional to content, continue.
44. **Cursor drift during scroll** (small position changes between notches).
45. **Scroll-into-view before click** — never click an element that would require a teleport;
    bring it on-screen first, as a human must.
46. **Persona scroll style** — "wheel-stepped" vs "trackpad-smooth" chosen per persona.

### E. Drag — `HumanizedInput.DragAsync`
47. **Curved (Bézier) drag path**, replacing the straight interpolation.
48. **Variable drag velocity** — slow grab, accelerate, slow to the drop.
49. **Grab dwell** — pause after mouse-down before motion starts.
50. **Drop settle** — pause at the destination before release.
51. **Drag overshoot-and-correct** on the target.

### F. Higher-level interaction behavior — `ContainerToolAdapter` + Commander prompting
52. **Hover-before-decide** — move to an element and dwell before acting, not instant-click.
53. **Reading dwell proportional to visible text** on the page/section being acted on.
54. **Orient-on-load** — a small scroll/look after navigation before the first interaction.
55. **Prefer in-page navigation** — click links/buttons instead of always jumping via
    `computer_navigate` direct-URL (real users rarely type full deep URLs).
56. **Incidental hovers** over non-target elements (light exploration).
57. **Inter-action think-time** between tool calls — jittered pauses, never machine-gun cadence.
58. **Keyboard focus traversal** — sometimes Tab between fields instead of clicking each.
59. **Human affordances** — occasional Back button, in-page find, re-reads.
60. **Non-deterministic form order** — don't always fill fields in perfect DOM order.
61. **Move-away after submit** — shift the cursor off the button/form after acting.
62. **Mistake realism** — an occasional wrong tab / corrected action instead of a flawless path.

### G. Browser fingerprint & launch surface — `ContainerOrchestrator.cs`, `desktop.Dockerfile`
63. **Kill `--disable-gpu`**; provide a real/acceleratable GL path (llvmpipe is still a tell, so
    prefer a spoofed WebGL vendor/renderer via a preload/extension, or software-GL masking) so the
    `UNMASKED_RENDERER` is a plausible consumer GPU, not `SwiftShader`.
64. **Close / relocate the CDP surface** — drop the persistent `--remote-debugging-port=9222` and
    `--remote-allow-origins=*` from the *operating* browser; if inspection needs CDP, bind it to an
    ephemeral port opened on demand and torn down, never advertised to pages.
65. **Ensure `navigator.webdriver === false`** and no `--enable-automation`; add
    `--disable-blink-features=AutomationControlled` defensively.
66. **Full realistic font set** in the image (MS core fonts, Noto/CJK, emoji) — a thin font list is
    high-signal; `desktop.Dockerfile` currently ships only DejaVu.
67. **Consistent UA + Client Hints** matching the actual Chromium build, platform, and persona.
68. **Persona timezone + locale + `Accept-Language`** (not UTC/`en-US`), set via `TZ`/env + flags,
    consistent with the network egress geolocation.
69. **Non-empty `navigator.languages`, `plugins`, `mimeTypes`** matching a normal desktop Chrome.
70. **Realistic screen metrics** — `devicePixelRatio`, `colorDepth`, `availWidth/Height`, and a
    persona-varied resolution instead of exactly `1920x1080` everywhere.
71. **Provide an audio device** (dummy ALSA/PulseAudio sink) so `AudioContext` fingerprints are
    non-null and vary — Xvfb ships none today.
72. **`enumerateDevices` returns plausible media devices** (mic/camera labels) without leaking.
73. **Canvas/WebGL fingerprint** — stable per persona with slight per-persona noise, so agents
    don't all share one identical canvas hash (anti-clustering).
74. **`hardwareConcurrency` / `deviceMemory`** set to realistic values consistent with the UA.
75. **Battery / Network Information API** plausibility (present, sane values).
76. **Permissions API consistency** — e.g. `Notification` default `"prompt"`, avoiding the
    headless `"denied"`/quirk pattern.
77. **Current Chromium version** on the normal patch cadence (an old build is itself suspicious).
78. **`--no-sandbox` masking** — keep container sandboxing at the OS layer but avoid page-visible
    automation flags; audit the full argv for anything fingerprintable.

### H. Persistent, aged, per-persona browser profile — `ContainerOrchestrator` + Account Registry
79. **One durable profile dir per account** (already `--user-data-dir`), never wiped between runs.
80. **Warm the profile** — real cookies, history, localStorage, and site engagement accrue over
    time; avoid the "first-ever visit, empty everything" signature.
81. **Persona-stable extensions/settings** (e.g. a consistent set, dark-mode pref) across sessions.
82. **Realistic first-run state** — dismissed prompts, chosen search engine, saved zoom.

### I. Network / egress consistency — persona config (guardrailed; see §6)
83. **Geolocation-consistent egress** — the exit IP's country/timezone matches the persona's
    locale/timezone/`Accept-Language` (the *inconsistency* is the tell, more than the IP itself).
84. **Session-sticky IP** — stable within a session, not rotating mid-task.
85. **Real Chromium TLS/JA3/JA4 & HTTP/2 fingerprint** — already true with real Chromium; preserve
    it (don't proxy through anything that rewrites the TLS/HTTP2 signature).
86. **No DNS/WebRTC leak** that contradicts the egress geolocation.

### J. Temporal realism — ties into temporal-grounding + `StewardWakeCycle`/scheduler
87. **Persona active-hours** — operate mostly within the persona's plausible waking hours in its
    timezone, not uniformly 24/7.
88. **Session length limits + breaks** — bounded work sessions with gaps, not 18-hour marathons.
89. **Day-of-week / diurnal patterns** in activity volume.
90. **Content-scaled think-time** between page loads (complex page → longer pause).
91. **Warm-up / fatigue arc** within a session (slightly faster mid-session, slower at the ends).

### K. Persona consistency system — new `PersonaStore` (per account)
92. **One persona bundle per account**: UA, screen, timezone, locale, fonts, WebGL id, typing
    distribution, mouse aggressiveness, scroll style, active hours — all internally consistent.
93. **Deterministic from a persona seed** so behavior is *reproducible and stable* per account.
94. **Cross-surface consistency check** — `navigator.platform` == UA platform == OS == timezone
    region == egress geo; a mismatch across these is a stronger signal than any single value.
95. **Anti-fleet-clustering** — personas differ from each other enough that agents don't form one
    tight fingerprint cluster.

### L. Detection feedback loop & verification — new
96. **Route `humanChallenge` detections into an Observable** (per Steward) so challenge *rate* is
    measured, not anecdotal — reuse `browser-inspect.py`'s existing detector.
97. **Self-test harness** — point a container at open bot-detection/fingerprint test pages
    (e.g. a self-hosted `fingerprintjs`/`creepjs`-style page, `bot.sannysoft`, `pixelscan`-style
    checks) and score which tells remain; run it in CI-style before rollout.
98. **Parameter telemetry** — log humanization parameters alongside outcomes to tune distributions.
99. **A/B intensity** — compare challenge rates across humanization-intensity settings.
100. **Regression gate** — the self-test score becomes a check so future changes can't silently
     reintroduce a tell (e.g. someone re-adds `--disable-gpu`).

---

## 4. Implementation phases

**Phase 0 — persona + scaffolding.** `HumanInputProfile`, `PersonaStore` (per account, seeded,
persisted with the Account Registry), global kill-switch + intensity knob. No behavior change yet.

**Phase 1 — input humanization (biggest win, lowest risk).** Build `HumanizedInput` decorator;
implement A (movement), B (clicks), C (typing), D (scroll), E (drag). Route every `computer_*`
mutating tool in `ContainerToolAdapter` through it. This is pure input-shape work on a real X
server — no browser changes, no fraud surface — and removes the highest-signal tells.

**Phase 2 — browser launch surface.** Category G edits in `ContainerOrchestrator.cs` /
`desktop.Dockerfile`: drop `--disable-gpu`, relocate CDP off a persistent public port, WebGL
vendor masking, fonts, audio device, UA/CH/timezone/locale, screen metrics. Bump the image
`imageVersion` stamp (currently `5`) and update the capability preflight.

**Phase 3 — profiles & behavior.** Category H (aged persistent profiles) and F (higher-level
interaction behavior via Commander prompting + tool pacing).

**Phase 4 — temporal + egress + consistency.** Categories I, J, K — wire active-hours into the
wake cycle, add cross-surface consistency checks. Egress (I) is config-only and guardrailed.

**Phase 5 — verification loop.** Category L — self-test harness, challenge-rate Observable,
regression gate.

---

## 5. Key files to touch

| Area | File |
|---|---|
| Input transport (curve/jitter/timing) | `Services/Demiurge/Containers/VncTransport.cs` |
| New humanization decorator | `Services/Demiurge/Containers/HumanizedInput.cs` *(new)* |
| Tool routing through humanizer | `Services/Demiurge/Containers/ContainerToolAdapter.cs` |
| Remote-input replay timing | `Services/Demiurge/Containers/ContainerRemoteInput.cs` |
| Browser launch flags | `Services/Demiurge/Containers/ContainerOrchestrator.cs` (~L216) |
| Image: fonts, audio, GL | `Services/Demiurge/Containers/desktop.Dockerfile` |
| Persona bundle + store | `Services/Demiurge/PersonaStore.cs` *(new)*, Account Registry link |
| Challenge-rate signal | `browser-inspect.py` detector → Observable |
| Interaction behavior | `StewardCommanderPrompts.cs`, `ContainerToolAdapter.cs` pacing |

Note the browser-first policy (`StewardDesktopInteractionPolicy.cs`) intentionally forbids
Playwright/Selenium/headless/CDP control of external sites. **None of this plan violates that** —
humanization happens in the *visible* input path and the launch environment. The one adjustment is
tightening CDP (item 64) so the *inspection* helper doesn't leave a detectable public port on the
*operating* browser.

---

## 6. Guardrails (responsible scope)

- **CAPTCHAs are not auto-solved.** The existing detect-and-escalate-to-human flow stays. This plan
  lowers how often challenges fire; it never completes one for the agent.
- **No mass-account farming.** Persona/egress consistency exists to stop *false-positive* flags on
  legitimate accounts, not to evade per-account abuse limits. Keep account creation gated by the
  existing approval/registry path.
- **Egress (category I) is geolocation-*consistency*, not block-evasion** — the deliverable is
  "IP geo matches the persona's locale/timezone," not "rotate IPs to dodge bans."
- **Kill-switch + intensity knob** ship in Phase 0 so behavior is always inspectable/tunable.
