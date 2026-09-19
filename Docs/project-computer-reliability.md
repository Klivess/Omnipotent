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
