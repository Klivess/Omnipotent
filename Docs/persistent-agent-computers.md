# Persistent project computers

## Deployment status

The replacement backend, worker/session services, workspace integration, UI and
deployment tools are implemented in the working trees. **This is not a report of
a restored production fleet.** A Linux/Incus end-to-end run, fault tests, browser
login verification, backup restore and the production capacity soak are required
before migration. No existing project has been switched by this change.

Automatic host setup is now part of Omnipotent startup and its build/publish
payload. It no longer requires a prepared VHDX, manually installed Incus, copied
certificates or a `PROJECTS_WORKER_CONFIG` environment variable. Windows elevation
and a required reboot are explicit boundaries; neither is disguised as a Docker
timeout. The real Hyper-V first-boot path has not been executed on this development
machine, so the implementation is not yet a production deployment verification.

Local validation: 121 selected .NET tests, 15 worker tests and four Playwright
computer tests passed (three Linux-only tests remain skipped on Windows);
the Nuxt production build, Python compilation and shell syntax checks passed.
An independent ISO reader verified all three generated NoCloud files, including
a multi-sector payload. The browser tests use mocked worker connections, so they
verify UI ownership and job reconnection contracts, not real Incus desktops.
The new upload regression covers a lost commit response with KeepBoth and verifies
that retry does not create another copy. These checks do not satisfy the Linux
fault, restore, native application or 12-computer/72-hour release gates below.

Live telemetry checked on 2026-09-19 still reports Docker unavailable on
`KLIVESHOMESERVE`, Windows build 19042, approximately 16 GB RAM and an i5-3570.
The live diagnostics include `WSL_E_OS_NOT_SUPPORTED`. The development computer
is a different Windows 11 computer with 32 GB RAM; it has neither Hyper-V
management nor a WSL distribution installed. Do not provision the wrong host.

## Runtime and persistence

Windows Omnipotent -> mutually authenticated HTTPS -> Linux worker broker ->
per-computer session API. Incus manages persistent unprivileged system containers;
routine input, screenshots and terminal requests never invoke Incus exec.

`AgentWorker/ka_worker` uses the Python standard library. SQLite WAL/FULL records
operation identities, computer ownership and admission state. Corrupt databases
fail closed and are not replaced with empty inventories. Identical operation IDs
cannot be reused for different requests. A disconnected HTTP caller never cancels
a job. Terminal runners are independent systemd units with PTYs, output cursors,
exit status and explicit cancellation. Session recovery marks interrupted visual
actions uncertain; it does not replay clicks or publishing actions.

Openbox, LXPanel, PCManFM and Xvnc provide an ordinary Linux desktop. Agents have
sudo inside their own computer. Apps, shortcuts, home directories and profiles
remain on persistent root disks. GUI services start on demand. Terminal commands
do not acquire the desktop input gate. Human input uses an exclusive renewable
lease; queued input from an ended human session is discarded.

New starts and declared heavy jobs queue when memory/swap/PSI or disk headroom is
insufficient. Reservations persist across broker restarts and cover startup plus
an observation interval. CPU/I/O priorities are equal. `memory.high` is a soft
3 GiB reclaim threshold, **not** a 3 GiB hard memory limit. OOM grouping contains
an affected computer's process group when the kernel selects it; it does not
guarantee protection against arbitrary exhaustion of the shared kernel.

Existing computers are never evicted to admit another computer. Age-based
reaping, retired-agent deletion and automatic navigation tab pruning have been
removed from the legacy harness too. Legacy host auto-start/recovery is disabled
unless `PROJECTS_LEGACY_DOCKER_AUTOSTART=true` is explicitly configured.

Screenshots are captured on demand and shared briefly between viewers. The session
retains at most four operation observations in memory. Durable operation records
do not contain screenshot bodies. Unwatched overview tiles disconnect. Full
computer and worker reboots still interrupt processes; their persistent data
survives. This is one host, not high availability.

## Interfaces and configuration

`IProjectComputerProvider` has Docker and Incus implementations; existing
`IComputerController` vocabulary remains. New projects explicitly select Incus
and create their workspace on the worker. Creation waits for worker availability
instead of creating a Docker fallback. Existing settings without a provider retain
their legacy Docker interpretation until verified migration. Corrupt settings fail
closed instead of reverting to Docker. Generic settings updates cannot change the
provider.

Each Incus project also records the worker CA identity at creation/cutover. Moving
the executable or restoring project settings onto another host cannot silently
provision empty replacements. Restore the original worker data/identity or perform
a verified migration before rebinding. A matching hostname/IP alone is insufficient.

Local worker configuration is generated under
`%ProgramData%/Omnipotent/AgentWorker/client.json`. To override automatic local
provisioning with an existing remote worker, optionally set `PROJECTS_WORKER_CONFIG`
to a protected JSON file on the Omnipotent host:

```json
{
  "endpoint": "https://10.78.0.2:7443",
  "ca": "D:/KA/private/ca.pem",
  "cert": "D:/KA/private/ka-api.pem",
  "key": "D:/KA/private/ka-api-key.pem"
}
```

The endpoint must match the worker certificate SAN. Only the `ka-api` certificate
is accepted by the broker; only `ka-broker` is accepted by sessions. Never put
these keys in a project volume, browser bundle, image template or git. Rotate leaf
certificates before their one-year expiry, verify reconnects, and retain the CA.

Owner-authenticated application routes:

| Route | Behavior |
| --- | --- |
| GET `/projects/computers?projectID=...` | Stable IDs, provider, state and queue reason |
| GET `/projects/computers/health?projectID=...` | Worker pressure and admission diagnostics |
| POST `/projects/computers/setup` | Owner starts Windows prerequisite setup/UAC; never reboots Windows |
| POST `/projects/computers/request?projectID=...` | Scoped action/job submission and inspection |
| POST `/projects/computers/activate?projectID=...` | Verified, paused-project cutover |
| Existing `/projects/containers` and desktop WebSockets | Compatibility routing by computer ID |

The request route takes `{computerID,target,method,payload,cursor}`. Targets are
allow-listed (`jobs`, `jobs/ID`, `jobs/ID/input`, `actions`, `operations/ID`,
`health`). Desktop actions take `{operationID,actorID,tool,arguments}`. Terminal
submission takes `{operationID,command,cwd,interactive,heavy}`. The same job is
inspected with GET `jobs/ID` and an output cursor. PTY input/cancellation requires
a fresh operation ID and is never retried blindly. Agent `computer_terminal`
supports `jobID`, `cursor`, `input`, `cancel`, `interactive`, `heavy` and
`waitSeconds`; `workingDirectory` remains supported. Credentials are not expanded
inside arbitrary terminal commands.

`IProjectWorkspaceBackend` provides Linux-native list/read/import/mutation access.
The existing file API keeps SQLite provenance on Windows and reconciles only a
complete successful inventory. A failed read is not a deletion. `/project`
normalizes to Linux storage after migration. Host APIs that require a pathname
use disposable explicit exports; host shell commands do not pretend the Linux
workspace is a Windows directory. File and screen stimulus subscriptions are
routed to the worker. Copies back to Linux carry expected content versions.

The file API retains its existing portable filename rules. Native terminals can
use Linux filesystem features; symlinks are deliberately not followed by the
worker's privileged workspace API.

## Installation and staged migration

1. Start a complete Omnipotent build on the intended host during maintenance. The
   Computers page displays automatic setup state. If Omnipotent already has admin
   permission, prerequisites install automatically. Otherwise use **Set up computer
   host** and accept Windows elevation. Hyper-V/OpenSSH are enabled without
   an automatic reboot. After a required reboot, a protected SYSTEM task resumes
   setup even if Omnipotent has not reopened. No WSL or Docker installation is used.
2. `Ensure-KAWorker.ps1` selects an internal NTFS/ReFS volume with at least 100 GiB
   free, downloads the Ubuntu 24.04 `release-20260911` Azure VHD archive and verifies
   SHA-256 `bcf5f2e60e55b3eb0eb57201fd57c0e34ced8b2dd1cdf4718084f41854786195`
   from [Canonical's release checksums](https://cloud-images.ubuntu.com/releases/noble/release-20260911/SHA256SUMS).
   It resumes partial downloads, quarantines checksum failures and converts the
   verified image to VHDX. A private internal Hyper-V switch/NAT and static address
   are provisioned automatically; conflicting networks are reported, not replaced.
3. A native ISO writer creates the NoCloud seed with a pinned SSH host identity,
   key-only bootstrap account and worker payload. The marked VM uses Secure Boot,
   fixed memory (6 GiB pilot; 40 GiB on a 64 GiB host), automatic startup and graceful
   shutdown. Its data disk is attached at a recorded SCSI location. Linux accepts
   only an unpartitioned, signature-free disk at that location with the expected
   size, labels it uniquely, formats Btrfs and mounts by UUID. Existing disks and
   running VMs are never replaced to retry installation.
4. A Linux systemd timer installs Incus and the desktop image, creates certificates,
   pins base/result image fingerprints and records packages. Windows retrieves only
   its API identity over pinned SSH. The application user can read configuration;
   installer code/state are writable only by SYSTEM/administrators. Host backing
   disk telemetry is required for new admissions, since guest free space alone is
   insufficient with thin VHDX storage.
5. Before returning credentials, first boot runs the Linux unit tests and a
   disposable computer smoke check: sudo, a persistent file/shortcut, graphical
   editor, screenshot and durable terminal completion. Tests use stable operation
   IDs on retries. The canary is retained for inspection. These are first-install
   checks, not substitutes for the fault/restore/72-hour production gates below.
   `New-KAWorker.ps1` remains an expert diagnostic/manual provisioning tool, not a
   required step of normal portable setup.
6. On a separate backup disk, initialize an encrypted restic repository. Supply
   `RESTIC_REPOSITORY` and `RESTIC_PASSWORD_FILE` in `/etc/ka/backup.env`; enable
   `ka-backup.timer`. A backup snapshots each project subvolume separately, exports
   stopped snapshot clones of computer roots, and backs up consistent broker
   metadata. It retains seven daily and four weekly recovery points. Restore to
   an isolated instance and compare files before counting this gate as passed.
7. Run Linux unit tests and the acceptance/fault matrix below. The soak script
   only operates on `acceptance-*` projects, creates independent browsers with
   three local tabs each, and exercises them concurrently. No production accounts
   or publishing endpoints are used. A short pilot run is **not** a passed 72-hour
   production soak.
8. Pause the canary project and wait for its wake to end. Keep old computers
   stopped/offline, including after a future Docker recovery. Use
   `migrate-workspace.py --config ... --project ID --source ... --backup ...
   --offline-source`. It creates/verifies a separate source backup, copies
   resumably and verifies every file hash. It does not switch providers. Preserve
   inaccessible old container-root applications for later recovery; do not claim
   they were copied when only the project volume was recoverable.
9. Before launching each new browser, POST to the private worker route
   `/computers/ID/import-browser-profile?projectID=ID`. It imports that agent's
   staged `.klive/browser-profiles/AGENT` directory and refuses a running browser
   or a browser-version downgrade. Verify actual authentication afterward; cookie
   portability is not assumed. For shared allocation use agent ID `shared`.
10. Verify `spammez2026` staged media, posting history, deduplication records,
    native dialogs and login **without publishing**. Then POST
    `/projects/computers/activate?projectID=ID` with `{ "canaryVerified": true }`.
    This requires a verified migration receipt and a paused project with no active
    wake; it leaves the project paused. Resume only after these checks. Retain
    legacy data for at least 30 days after successful migration.

Rollback is a controlled reverse migration: pause work, export new Linux writes,
compare against the original backup and reconcile differences before selecting
the old provider. Never overwrite current work with the old backup or replay
uncertain external actions.

## Validation and release gates

Local commands:

```text
cd AgentWorker
python -m unittest discover -s tests -v
```

On Windows, Linux dirfd/PTY integration tests are explicitly skipped; the Ubuntu
CI job runs them. C# tests cover operation identities, no replay after a lost
response, job polling, provider migration guards and authoritative remote files.
The Nuxt production build validates the UI bundle.

Before production activation, record evidence for **all** of:

- Install and launch an app; desktop shortcut; file dialog/upload; interactive
  terminal; background job; files and apps survive a computer/worker reboot.
- Kill/restart the session API, broker, Incus daemon and Omnipotent independently.
  Confirm existing jobs keep their IDs and finish without duplicates. Report
  unknown input outcomes accurately.
- Saturate CPU/I/O and apply controlled memory pressure on disposable computers.
  Existing computers must make progress, excess starts must queue, and the queue
  must drain after pressure subsides. Test disk-full and OOM containment separately.
- Disconnect requests after submission, then reconnect and retrieve original
  job output/exit status. Exercise interactive input, cancellation and takeover.
- Verify project boundary, symlink escape rejection, TLS identity checks, firewall
  boundaries and exclusive input leases.
- Restore a backup into an isolated computer. Compare root/home/project contents.
- Run `deploy/soak.py --config ... --report soak.json --computers 12 --hours 72` on
  production-capacity hardware. Inspect host/kernel OOM logs as well as its report.
  All computers remain resident; sleeping computers do not count.

Response speed is diagnostic. Data loss, duplicate side effects, unexplained
replacement, management-induced job termination and a single computer causing
fleet failure block release. A passed soak does not substitute for the fault,
restore, isolation or account-verification gates.
