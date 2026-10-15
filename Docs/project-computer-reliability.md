# Project computer control plane — 15 October 2026 (klive-computerd)

## What was failing

- Hot paths still went through `docker exec`: browser launch on every navigate, physical clicks,
  the terminal, window state, readiness probes and the clipboard. On this host an exec costs
  seconds and fails outright while dockerd is slow.
- Any readiness failure restarted the whole container, including failures of cosmetic parts
  (XFCE panel, session). The restart killed Chromium and every page the agent had open.
- A browser-service call that timed out *after* it was sent was retried through `docker exec`,
  so a click or a typed string could happen twice.
- Agents could not see crashes. An OOM-killed or hung Chromium looked like a tool error, and
  every wake re-derived what state its computer was in.
- Browser inspection returned large JSON with ~200-character base64 element refs. It truncated
  and spent context on refs.

## Changes

- **klive-computerd.** `browser-service.py` is now a stdlib-only control daemon inside each
  desktop (protocol 2), on the same port 5902, published to `127.0.0.1` only. `/run` keeps its
  allowlist. New routes: `/exec`, `/input`, `/screenshot`, `/clipboard`, `/windows`,
  `/browser/state`, `/diag`. `/health` is a superset of the old response (components, browser,
  memory/OOM, load, disk, image version, event journal).
- **Per-container token.** Privileged routes need `X-Klive-Token` (or a bearer token): the
  first 48 hex characters of HMAC-SHA256(host key, `klive-computerd|<containerID>`). The host
  key lives next to the containers file (`computerd.key`). The token is written to
  `/etc/klive-computerd.token` (root, 0644) through the Docker archive API, on creation and on
  every helper sync. The daemon re-reads it on demand, so rotation needs no restart. With no
  token configured the privileged routes refuse (403) and the harness uses its pre-daemon paths.
- **Exec without docker exec.** `/exec` runs an argv or `bash -lc` under a wall-clock budget,
  with bounded output and optional stdin. The client separates *not reached* (safe to fall back
  to `docker exec`) from *reached* (never re-run; the result or the timeout is final). Only
  idempotent desktop controls (launch/focus browser, focus terminal or window) may retry.
- **Input and screen.** VNC stays the primary input route (6 s connect budget; a failed VNC is
  skipped for 60 s). When it is down, the daemon's xdotool/XTEST route takes over, and
  `HumanizedInput` drives either one through `IDesktopInput`. Screenshots fall back to the
  daemon's `/screenshot` (JPEG, PNG or raw BGRA, via `python3-pil`).
- **Readiness ladder.** Required: display, window manager, a frame, input. Panel, session and
  desktop are advisory and never cause a restart. `EnsureDesktopReady` re-probes after 6 s,
  then restarts failed components in place, and restarts the container only when the display
  or the VNC server itself is gone.
- **Browser watchdog.** The daemon checks Chromium's debugger every 15 s. After six silent
  checks (about 90 s) it kills the hung browser, and the next browser tool relaunches it. It
  journals `started`, `oom-kill`, `browser-exited`, `browser-hung-killed` and `watchdog-error`.
- **Agents see their computer.** New journal events are prefixed to the agent's next computer
  tool result. Every wake seed carries a computer section (state, tabs, memory, recent crashes;
  450-token budget).
- **No double execution.** A mutating browser-service call that times out after sending now
  reports that it "may or may not have been applied" instead of re-running through `docker exec`.
  The connect timeout is 4 s.
- **Diagnostics and repair.** `GET /projects/computers/list[?projectID]`,
  `GET /projects/computers/diagnostics?containerID=…` and `POST /projects/computers/repair`
  `{ containerID, action }`. Actions, gentlest first: `restart-browser`, `restart-components`,
  `restart-computer`, `refresh-helpers`. The agent using the computer is told what changed.
- Image v14 (`python3-pil`, computerd capabilities); Chromium runs with
  `--renderer-process-limit=4`. Running desktops receive computerd and their token through the
  in-place helper sync; nothing is recreated.

## Status

`Omnipotent.Tests/Projects/computerd_test.py` (19 cases, runs on Windows) and the C# Projects
suite pass. Not deployed. Still to verify on a live desktop: token delivery to existing desktops
through the helper sync, daemon input while VNC is down, and the watchdog killing a hung Chromium.

# Project computer reliability — 23 September 2026 (Docker hardening, Incus removed)

## What broke

Live telemetry (`/projects/computers/health`, five projects' event logs) on KLIVESHOMESERVE
(Windows 10 19042, 16 GB, i5-3570, Docker Desktop 4.2):

- **Sep 10–11: load flaps.** The daemon went unreachable several times a day, image builds hit
  i/o timeouts and host shells took 120–180 s. Nothing capped how many 2 GB desktops ran, and an
  uncapped WSL VM competed with Windows and Omnipotent for the same 16 GB.
- **Sep 18 onward: "Docker Engine failed to start".** A project agent "repaired" the host through
  `run_powershell`. It ran `wsl --update` and a Docker Desktop reset, then installed the
  standalone WSL 2.5.10 MSI. That package does not run below build 19044, so every `wsl.exe` call
  answers `WSL_E_OS_NOT_SUPPORTED` and Docker's WSL backend cannot start. The daemon pipe has
  been absent since.
- **Sep 19–21: Incus.** The Incus migration disabled Docker auto-start/recovery
  (`PROJECTS_LEGACY_DOCKER_AUTOSTART`) and idle suspension, and provisioned a Hyper-V VM that
  holds a fixed 6 GB while serving no project.

## Changes

- **Incus removed.** Both Incus commits are reverted: the Docker path, recovery, idle suspension,
  retired/finished teardown and tab hygiene are back. `LegacyWorkerDecommission` runs once at
  startup, elevated. It removes the SYSTEM setup task first, then the owned VM and its disks,
  the private switch and NAT, and `%ProgramData%\Omnipotent\AgentWorker`. It only touches objects
  carrying the installer's ownership marks.
- **WSL repair.** When WSL answers `WSL_E_OS_NOT_SUPPORTED` below build 19044, the bootstrapper
  removes the standalone "Windows Subsystem for Linux" package (never the "…Update" kernel MSI).
  It never unregisters a distro. It then reports `restartRequired`: **one Windows restart**
  restores the built-in WSL that ran Docker before Sep 18. Omnipotent never restarts the host.
- **Real Docker Desktop restart.** Launching an already-running Docker Desktop only focuses its
  window, and 4.2 has no `docker desktop restart`. A running Docker Desktop with a dead engine
  now has only its own processes killed and `com.docker.service` restarted, then it is
  relaunched. The start budget is back to 4 minutes (1 minute was shorter than this host's
  cold start). Docker's own WSL distros are terminated too, and the WSL VM is shut down when
  nothing else runs in it. The first live run after the WSL repair showed why: the
  `docker-desktop` distro stayed "Running" with a dead init ("Waiting for Procd service"), and
  every relaunch reattached to it.
- **Continuous supervision.** Every minute the daemon is probed. Recovery runs when it is down
  (single-flight, 10-minute cooldown), and desktops are reattached when it returns. Before this,
  recovery ran only at startup or when an agent tripped over it.
- **Memory admission** (`DesktopCapacityPolicy`). Each desktop's hard ceiling is 1.5 GB, plus
  512 MB swap, a 512 MB soft reservation, OOM score +500 and a 4096 pids limit. The sum of
  running ceilings never exceeds Docker's VM memory minus 1 GB for the daemon. When a new
  desktop would overcommit, the least-recently-used idle desktop is stopped in place. If none can
  be stopped, the request fails cleanly (`DesktopCapacityException`) instead of overcommitting.
  The VM can no longer reach global OOM, so dockerd cannot be the victim. Existing containers
  are moved onto the new ceilings during reconcile.
- **WSL VM cap.** `[wsl2] memory=` is set to host RAM minus 6 GB, clamped to 3–12 GB, only when
  the owner has not set one.
- **Idle release.** A desktop unused for `Projects_DesktopIdleSuspendMinutes` (default 20) is
  stopped in place. It is skipped while an action is in flight, while an input lease is held,
  while Klives has it open interactively, or while its CPU is at or above 25% of a core. Apps,
  home files and browser profiles survive; the next computer tool resumes it.
- **Guardrail** (`ProjectHostInfrastructurePolicy`). Project agents can no longer change WSL, run
  msiexec, reset or stop Docker Desktop, stop Docker/WSL/Hyper-V services, change Windows
  features or Hyper-V objects, reboot, remove or prune shared Docker objects, or edit
  `.wslconfig`/Docker settings. Read-only diagnostics stay allowed.
- `GET /projects/computers/health` now reports `restartRequired`, supervision time and a
  `capacity` block (VM memory, slots, running and stopped counts).

## Status

Omnipotent and the test project build. Deployment, the one Windows restart and live
verification are pending.

# Project computer reliability — 18 September 2026

## Changes

- Idle cleanup stops a computer in place. Resuming reuses the same container, refreshes both published ports, and verifies the desktop frame. Installed system packages, home files and desktop settings survive sleep.
- Commander ownership is distinct from the active sub-agent roster. Cleanup no longer classifies the Commander as a retired worker.
- A rebuilt base image applies to new computers. Acquiring an existing computer no longer deletes its writable filesystem to pick up an image update. Existing computers will need an explicit, backed-up upgrade when base-image changes are required.
- Desktop repair restarts the existing container under provisioning/action locks. It does not replace the computer after a transient readiness failure. Finished projects and explicitly retired workers retain their existing teardown behavior.
- Native desktop readiness is independent of Chromium and its inspection helper. Application launches load the XFCE session environment, detach standard input, reject missing executables, and return immediate startup failures. Explicit Firefox launches now open Firefox; arbitrary window titles no longer get redirected to Chromium or Terminal.
- Daemon health probes enforce a wall-clock deadline and propagate caller cancellation. A failed Docker inventory cannot authorize cleanup.
- Host recovery respects the configured endpoint: it only manages local Docker Desktop named pipes. It uses bounded launch/restart attempts, a cooldown, and WSL diagnostics. Installer stdout and stderr are drained concurrently. No automatic WSL unregister, factory reset, global WSL shutdown, or host reboot is introduced.
- Owner-only `GET /projects/computers/health` reports host availability and recovery status without response caching. `POST /projects/computers/resume?projectID=...` with `{ "containerID": "..." }` resumes an existing project computer. Inventory includes `suspended` and `lost`.
- The desktop wall distinguishes sleeping computers, missing computers and host outages, and offers an explicit Resume button. Agents can also wake their computers by using a computer tool.

## Live host observations

Authenticated diagnostics found Windows 10 Pro 19042.1288, Docker Desktop 4.2.0.70708, an inbox `wsl.exe` at 10.0.19041.1151, and a separately installed WSL 2.5.10.0. Docker's engine named pipe was absent. The legacy LxssManager service was stopped; starting it succeeded. Starting WSLService failed with service exit 1058. HNS, SharedAccess, vmcompute, HvHost and the Plan 9 redirector were running in subsequent diagnostics.

Docker's logs showed its internal WSL services failing to become reachable and repeated waits for Procd. These observations do **not** establish corruption of the Docker data filesystem. No distro was unregistered and no image, volume or project container was removed during this investigation.

Automatic approval review blocked a bounded WSL subprocess diagnostic and then a graceful Docker Desktop restart, giving only “blocked by policy”. The restart did not execute. Production recovery remains unverified; these source changes have not been deployed. A deployment or reboot must not be represented as completed based on these tests.

## Validation

Backend tests cover same-container suspend/reconcile/resume, fresh port discovery, failed inventory preservation, Commander ownership, remote endpoint isolation, process deadlines/stderr draining, native readiness and actual launcher failure exit codes. Browser tests cover sleeping-computer resume and a host outage with no misleading connecting tiles. The website production build passes.

The workstation lacks a .NET 9 runtime; tests use the installed .NET 10 runtime via `DOTNET_ROLL_FORWARD=Major`, while compilation still targets net9.0. Live Linux GUI/app-install and reboot persistence checks remain pending host recovery.

## Recovery references

- [Docker Desktop CLI](https://docs.docker.com/desktop/features/desktop-cli/) describes the supported restart/status commands available in modern Docker Desktop. The deployed 4.2.0 installation predates that CLI.
- [Docker WSL backend](https://docs.docker.com/desktop/features/wsl/) describes its data architecture. Distribution state alone is not a filesystem-integrity test.
- [Microsoft WSL commands](https://learn.microsoft.com/en-us/windows/wsl/basic-commands) documents that unregistering a distribution permanently removes its data.
