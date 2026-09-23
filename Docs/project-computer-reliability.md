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
  cold start).
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
