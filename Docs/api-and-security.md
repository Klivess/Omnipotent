# The API control plane

`KliveAPI` is the single HTTP/WebSocket surface for the whole platform. It is itself an
`OmniService`, so it starts, logs, restarts and reports health like every other module — but every
other module registers *through* it. That means one place enforces permissions, body limits, caching
and statistics, and no module ships its own web server.

Source: [`Omnipotent/Services/KliveAPI/`](../Omnipotent/Services/KliveAPI) and, for profiles,
permissions and sessions, [`Omnipotent/Klives Management/`](../Omnipotent/Klives%20Management)

## Routes are declared by the modules that own them

There is no central routing table to keep in sync. A module registers its own routes during
`ServiceMain()`, and every registration names the **permission** it requires — the parameter is not
optional (a null throws at registration), so a route cannot be added without a deliberate access
decision:

```csharp
await CreateAPIRoute("/projects/list", HandleList,
                     HttpMethod.Get, ProjectsPerms.OverviewView);

await CreateStreamingAPIRoute("/KliveCloud/UploadFile", HandleUpload,
                              HttpMethod.Post, KliveCloudPerms.FilesUpload,
                              maxBodyBytes: 8L * 1024 * 1024 * 1024);
```

Four registration forms, each permission-gated:

| Form | Body handling | Use |
|---|---|---|
| `CreateAPIRoute` | Buffered | Normal JSON endpoints |
| `CreateBufferedAPIRoute` | Buffered, explicit byte cap | Bounded uploads |
| `CreateStreamingAPIRoute` | Streamed, explicit byte cap | Large files; body never fully materialised |
| `CreateWebSocketRoute` | Upgrade | Live event push |

Body caps are enforced as bytes arrive, not after: exceeding the limit throws
`RequestBodyTooLargeException` mid-read rather than buffering the overrun first.

**About 530 HTTP routes and a dozen WebSocket routes are registered this way**, gated by **164
permissions** across 22 services — the surface is large because the modules are, not because any one
of them is.

## Permissions

Access is a set of **granular permissions**, not a rank. Each permission is a key —
`service.area.action`, e.g. `omnitrader.orders.place`, `klivecloud.files.delete` — defined next to the
service it protects, in a `*Perms.cs` file:

```csharp
[PermissionSet]
public static class KliveCloudPerms
{
    private static readonly PermissionGroup G = new("klivecloud", "KliveCloud");

    public static readonly PermissionDef FilesDelete = G.Define("files.delete", "Files", PermissionTier.Manage,
        "Delete files", "Delete items where you are an editor, including everything inside folders.", ProfileRank.Guest,
        implies: new[] { "files.browse" });
}
```

Keys need at least three segments; a malformed or duplicate key throws in the type initialiser, which
the test suite catches before it could take startup down. The catalog
([`PermissionCatalog`](../Omnipotent/Klives%20Management/Permissions/PermissionCatalog.cs)) discovers
every `[PermissionSet]` class by reflection and resolves `implies` closures (holding *Manage* a thing
gives its *Read*).

Every permission sits in a **tier** — how much information or power it gives:

| Tier | Meaning | Examples |
|---|---|---|
| 1 · Glance | Status, counts, summaries | `omnitrader.status.view`, `klivecloud.drive.view` |
| 2 · Read | Full records, history, content | `omniscience.persons.read`, `klivemail.messages.read` |
| 3 · Act | Routine, mostly reversible operations | `omnitrader.backtests.run`, `klivecloud.files.upload` |
| 4 · Manage | Creating, deleting, configuring | `klivemail.mailboxes.manage`, `projects.lifecycle.manage` |
| 5 · Critical | Live money, host control, secrets, other profiles | `omnitrader.orders.place`, `system.terminal.use` |

Routes reading the same data at the same depth share a key; every mutation has its own key separate
from its reads; destructive, money and host actions are always their own key. Two pseudo-keys cover
the rest: `Perms.Public` (no authentication — login, share links, `/ping`) and `Perms.SignedIn` (any
enabled profile — its own profile, sessions, `/batch`, client telemetry). Handlers that refine a check
inside a route (revealing a secret on a list route, deleting *any* chat room) call `req.Can(...)`, and
those permissions list the route under `Refines` so the console can still show what they unlock.
KliveTools registers one key per tool at runtime (`klivetools.tool.<slug>.run`).

**Profiles** ([`KMProfileManager`](../Omnipotent/Klives%20Management/KMProfileManager.cs)) hold a list
of grants, each optionally expiring (temporary access). On top of the grants:

- **Owner.** Klives' profile holds every permission, current and future, and can't be suspended,
  disabled or edited by anyone else.
- **Suspended** until a time, with a reason: every permission is refused until then.
- **Read-only:** reads keep working, every Tier ≥ 3 permission is refused.
- **Sign-in off** (`CanLogin`): sessions end and the credential stops working.

**Rank** survives only as a hierarchy label — `Guest < Manager < Associate < Admin < Klives` — and
never grants or refuses anything. It answers "who is above whom": a non-owner may manage, and assign
ranks to, only profiles ranked strictly below them, and nobody manages themselves. Delegation is
bounded the same way: a non-owner can only hand out permissions they hold, and the Tier-5
`profiles.*` permissions (granting permissions, live access control, password resets, deletion) only
Klives can hand out — so control over other profiles can never spread.

The first start after this model shipped migrated every profile: Klives became the owner, everyone
else was granted exactly the permissions whose routes their old rank could reach (each permission
records that `LegacyRank`), and the old files were backed up as `*.kmp.v1.bak`.

## The gate

One evaluator — [`AccessEvaluator`](../Omnipotent/Klives%20Management/Permissions/AccessEvaluator.cs)
— decides for HTTP routes, every `/batch` item and every WebSocket upgrade, in this order: public →
credential → sign-in allowed → signed-in pseudo-key → owner → suspended → read-only → grants (expiry
checked at that moment). It reads a frozen per-profile snapshot, so a check is O(1) with no I/O.

The answer follows one contract, which the website relies on:

| Status | Meaning | `RequestDeniedCode` | Website does |
|---|---|---|---|
| **401** | Not signed in (any more): no or bad credential, session revoked or expired, sign-in off | 0, 1, 5, 6, 4 | Signs out and says why on the sign-in page |
| **403** | Signed in, not allowed: missing permission, suspended, read-only | 2, 7, 8 | Explains it in place — nothing redirects |
| **503** + `Retry-After` | Profiles are still loading after a restart | — | Retries quietly (a restart never signs anyone out) |

A 403 names what was missing:

```json
{ "error": "AccessDenied", "reason": "MissingPermission", "code": 2, "route": "/api/omnitrader/orders/place",
  "permission": { "key": "omnitrader.orders.place", "title": "Place and manage orders", "service": "OmniTrader",
                  "area": "Orders", "tier": "Critical", "tierValue": 5, "description": "…" },
  "message": "You need the “Place and manage orders” permission (OmniTrader)." }
```

Browsers can't put headers on a WebSocket, so sockets carry the session token as
`?authorization=<token>`; the pipeline resolves it exactly like the header, and the same evaluator
(including the sign-in check) decides the upgrade.

Access is part of the response cache's correctness: `req.Can()` records the profile's
`kmprofile:{id}:access` dependency, every access change bumps it, and every `/KMProfiles/*` route is
uncacheable — so a demoted profile is never served a response cached while it still had access.
`/allRoutes` lists every route with its method and permission (`system.api.routes.read`).

## Sign-in and sessions

`POST /KMProfiles/Login {password}` returns a **session**: `{token, sessionId, expiresUtc, me}`.
Tokens are `kms_` + 256 random bits; only their SHA-256 is stored
(`SavedData/KlivesManagement/sessions.json`, written atomically). A session lasts 30 days from last use
and can be revoked on its own, everywhere, or "everywhere else" by its owner.

Passwords are stored as an HMAC-SHA256 lookup (an O(1) index, keyed by
`SavedData/KlivesManagement/credential-lookup.key`, with a check file that notices a lost key and falls
back to a scan) plus a PBKDF2-SHA256 hash (600 000 iterations) for verification. No route returns a
password; a reset generates one that is shown once. Two profiles can't share a password. Failed
sign-ins are throttled per IP, and every sign-in — successful or not — lands in OmniDefence.

Sending the password itself as the `Authorization` header still works while
`KMProfiles_AcceptPasswordAsBearer` is on (the default, so devices and scripts from before sessions keep
working); once it is off, only profiles with *Password API access* switched on may. In-process callers
(`OmniApiClient`) use an internal owner session from `IssueInternalToken()` instead of a password.

## The live channel

Every website tab keeps **`/KMProfiles/SessionWatch`** open. The server pushes:

- `session-state` — `SessionActive`, or why the tab must sign out (`SessionRevoked` with the reason,
  `SessionExpired`, `ProfileDisabled`, `PasswordChanged`, `ProfileNotFound`); a safety check every 5 s
  backs up the pushes;
- `hello {accessVersion}` on connect, and `access-changed {version}` the moment a grant, suspension,
  read-only flag or rank changes — the tab re-reads `/KMProfiles/me` and its navigation, page guard
  and buttons update in place.

The tab reports `presence {path, title, visible}` on navigation, on visibility changes and every 30 s.
While profiles are still loading the socket is closed with 4013 ("try again") rather than told the
profile doesn't exist.

`/KMProfiles/admin/live?profileId=` (`profiles.activity.live`) follows one profile: a snapshot of where
it is and its sessions, then every presence change, request and access event as it happens.

## Activity

[`ProfileActivityStore`](../Omnipotent/Klives%20Management/Activity/ProfileActivityStore.cs) keeps its own
SQLite file (`SavedData/KlivesManagement/profile_activity.db`, never OmniDefence's connection) with a
batched, fire-and-forget writer:

- **requests** — one row per request, and one per `/batch` item (OmniDefence only sees `/batch`):
  route, permission, status, duration, IP, page, denial reason;
- **page views** — from presence, with time on page;
- **events** — sign-ins (and refusals), sign-outs, revoked sessions, password changes and resets,
  permission changes, suspensions, read-only, sign-in on/off, profile edits;
- **daily rollups** per permission, kept forever (raw rows: `KMActivityRawRetentionDays`, 90).

The profile console reads it through `/KMProfiles/activity` (timeline, cursor-paged and filtered),
`activity/summary` (requests per hour by service, top permissions and pages, IPs, refusals, sign-ins)
and `activity/usage` (which granted permissions are actually used — and which haven't been for 30 days).

## Profile routes

| Route | Permission |
|---|---|
| `Login`, `LoginStatus` | Public |
| `me`, `Logout`, `sessions/mine`, `sessions/mine/revoke`, `password/change`, `permissions/catalog` | Signed in |
| `list`, `get`, `presence` | `profiles.directory.view` |
| `permissions/audit` (and route lists in the catalog) | `profiles.permissions.view` |
| `activity`, `activity/summary`, `activity/usage` | `profiles.activity.read` |
| `create` | `profiles.lifecycle.create` |
| `update` (name, Discord, rank) | `profiles.lifecycle.edit` |
| `permissions/set` (the full set, with expiries; optimistic `accessVersion`) | `profiles.permissions.grant` |
| `access/suspend`, `unsuspend`, `read-only`, `login`, `password-api`, `sessions`, `sessions/revoke` | `profiles.access.control` |
| `credentials/reset` | `profiles.credentials.reset` |
| `delete` (type the name to confirm) | `profiles.lifecycle.delete` |

Everything that changes a profile is recorded as an activity event, audited into OmniDefence and,
where it matters, sent to Klives on Discord — after it succeeds, never for a refused attempt.

## KliveCloud access lists

KliveCloud items are shared with **people**, not ranks. Each file or folder has its own list —
people with *Viewer* (open, download) or *Editor* (also upload, rename, move, delete, share) access,
an optional level for *Everyone*, and whether it also inherits the list of the folder above. A
profile's level is the best one found walking up the folders until one that stops inheriting. Klives,
and anyone holding `klivecloud.files.all`, can reach everything. Items shared with someone whose
parent folder they can't see appear under a virtual **Shared with me** folder.

Both gates apply to every action: the route's permission (`klivecloud.files.upload`, `.delete`, …) and
the item's level — creating in a folder needs Editor on it, deleting a folder needs Editor on every
item inside it. Share links only ever serve what their creator can still see, and a link is always its
creator's own. An item-level refusal answers `403 {error: "ItemAccessDenied", reason: "NotEditor" |
"NotShared", message}`.

## Request pipeline

```
http.sys
   │
   ▼
N concurrent accept loops        max(4, ProcessorCount)
   │
   ▼  Task.Run — hand off immediately
thread pool (min 128)
   │
   ├─ resolve route + method
   ├─ OmniDefence gate            fire-and-forget; never awaited
   ├─ authenticate + permission check   one evaluator for HTTP, /batch and WebSockets
   ├─ response cache lookup       dependency-versioned
   ├─ read body                   buffered or streamed, capped
   ├─ handler
   └─ record statistics + telemetry trace + cache fill
```

Two details in that path are load-bearing, and both were bugs first:

**Multiple accept loops.** A single `GetContextAsync` loop is both a throughput ceiling — one request
dequeued from http.sys at a time — and a fragility, since nothing is accepted while that one thread is
busy. The listener supports concurrent accepts, so the service runs `max(4, ProcessorCount)` of them.

**Offloading before the first await.** An `async` method runs *synchronously on the calling thread*
until its first suspending `await`. The pipeline's prologue — auth lookup, defence gate, cache probe —
plus any handler's synchronous prologue all complete before that suspension. Running it inline
executed that work on the accept thread and blocked acceptance of every other request, which
presented as whole-site latency rather than as a slow endpoint. `Task.Run` moves it to the pool so a
blocking handler can never stall global acceptance.

The accept timestamp is updated on every dequeue, so a stale value while health checks fail
distinguishes a wedged listener from a merely slow handler downstream.

## Response cache

[`Caching/`](../Omnipotent/Services/KliveAPI/Caching) implements a **dependency-versioned** cache
rather than a TTL. Handlers do not annotate cache lifetimes; instead the stores they read are
instrumented, each request runs inside a dependency scope that records which stores were touched, and
the entry is invalidated when any of them is written. A cached response is therefore never stale.

- Entries are sealed with the dependency set captured during the fill; a mutation bumps the version
  and orphans them.
- Cached bodies replay with correct binary/text semantics and participate in ETag/304 handling.
- Streaming responses are not capturable and mark the fill abandoned rather than storing a truncated
  body.
- Per-route hit and miss counters, a prefix denylist, a global kill switch, and
  `/KliveAPI/cache/stats` and `/KliveAPI/cache/clear` routes (`system.api.cache.manage`).

The trap, learned in production: correctness depends on **every** store a route reads being
instrumented. An uninstrumented store behind a cacheable route serves stale data indefinitely — which
is exactly how it first manifested, as settings that appeared not to save.

## Batch endpoint

`POST /batch` executes several sub-requests in one round trip, on the thread pool in parallel. Each
sub-request goes through the same evaluator as a direct request — a refused item carries the same 403
body inside the batch response — and is recorded in profile activity as its own request. Sub-requests
share the same cache as direct GETs, so a batch is never a way to bypass either the gate or the cache.
This exists because a dashboard page load is a burst of a dozen small reads, and paying HTTPS setup and
scheduling for each one is what made the site feel slow.

## Statistics

[`KliveApiStatisticsStore`](../Omnipotent/Services/KliveAPI/KliveApiStatisticsStore.cs) records every
request and persists across restarts: totals, successes, client errors, server errors, 404s, 401s,
mean and max response time, last-request timestamp, and per-day and per-route buckets. This is the
data behind the dashboard's API tiles, and it is how a regression like the serialisation bug above
becomes visible as a shape in a chart rather than a vague feeling that things got slower.

## Telemetry

[`Telemetry/`](../Omnipotent/Services/KliveAPI/Telemetry) is the detailed layer on top of the
statistics above: every request is traced stage by stage, aggregated into mergeable histograms at
several resolutions, and served to the website's **API telemetry** page (`/administration/api-telemetry`).

**Stages.** A `RequestTrace` is always "in" exactly one stage, and `Enter(stage)` charges the time
since the last transition to the current one — so stages partition a request's lifetime with no gaps:

| Stage | What it measures |
|---|---|
| `queue` | Accept → a pool thread starts the request. Rises under thread-pool starvation. |
| `prologue` | Route/query parsing, header capture, method and permission checks. |
| `auth` | Resolving the profile from the session token (or Authorization header). |
| `gate` / `delay` | OmniDefence gate evaluation / its deliberate tarpit sleeps (`delay` is excluded from latency). |
| `body` | Reading the request body (client upload). |
| `cache` | Response-cache key + lookup. |
| `handler` | The handler's own work until it starts emitting a response. |
| `encode` / `etag` / `compress` | UTF-8/JSON encoding, weak-ETag hashing, Brotli/gzip. |
| `write` | Handing bytes to http.sys. Only tracks the network for bodies larger than the socket buffer. |
| `teardown` | Post-response bookkeeping; not client-visible, excluded from latency. |

Handlers can add named spans (`using (req.Trace?.Span("db")) {…}`); Omniscience's `GatedRead` records
`gate-wait` and `query`. Every response carries a multi-entry `Server-Timing` header
(`queue;dur=…, handler;dur=…, app;dur=…, trace;desc=<id>`), which browser devtools show directly.

**Client half.** The website observes Resource Timing entries for API calls and beacons
DNS / TCP / TLS / network-wait / download phases to `POST /KliveAPI/telemetry/rum`. Beacons carrying the
`trace` id of a kept server trace are joined to it, giving a full browser → server → browser waterfall.

**Aggregation.** `Record()` is one non-blocking channel write. A single engine thread owns all state and
folds traces into 10 s (global only), 1 m, 1 h and 1 d buckets per series (`METHOD route`, plus
`*` global, `*batch` batch items, `(denied)`, `(unmatched)`, `(preflight)`). Latency and every stage use
one fixed log-scale histogram (≤ ±9 % percentile error) so buckets merge exactly across time and series.
`/batch` items get their own child traces — counted in their route, not in global throughput.
Denied/tarpitted requests stay out of global latency.

**Persistence.** `SavedData/KliveAPI/Analytics/telemetry.db` (its own SQLite file — never OmniDefence's
connection). Closed minutes are written each minute; open hour/day buckets are checkpointed every minute
so a restart resumes them. Kept traces: every request over the slow threshold, every 5xx/disconnect,
plus N samples per route per minute. Retention and thresholds are OmniSettings
(`KliveAPITelemetry*`; kill switches `KliveAPITelemetryDisabled`, `KliveAPITelemetryRumDisabled`).

**Serving.** Every standard preset (15m … all) of overview/routes/runtime/rum, plus weekly and health,
is rebuilt by the telemetry engine and stored pre-serialized and pre-compressed with an ETag. Long-range
presets refresh every minute, and the live view refreshes every second. Custom range and route queries
are queued into a bounded background build cache; an uncached request gets a small `202` response and
the website retries it. Active custom views refresh in the background. Trace lists, trace details, and
route exemplars have separate bounded background snapshot workers, so HTTP handlers do not query SQLite.
Asking for resolution the retained data no longer has returns a coarser bucket with `degraded: true`
and a `note`. Routes (`system.api.telemetry.read`):
`/KliveAPI/telemetry/{overview,routes,route,runtime,rum,weekly,health,live,traces,trace}`.

`OmniDefence` consumes the same request outcomes for abuse tracking. Its writes are deliberately
never awaited on the request path — doing so once turned a background SQLite write into a
ten-minute login stall.

## Security posture — read this before deploying anything

This is a **single-owner, self-hosted personal system**, and the threat model is "the owner's own
machine on the owner's own network". It has not been through an external security review.

Specifically:

- Authentication is a password exchanged for a revocable session. There is no MFA and no OAuth.
  Passwords are stored hashed; while `KMProfiles_AcceptPasswordAsBearer` is on, a password still works
  as an API credential (turn it off once nothing relies on it).
- Session tokens live in a cookie readable by the website's own scripts, because the API is a
  different origin and needs them in a header. They are revocable and expire, unlike the password they
  replaced.
- Transport uses a self-signed certificate installed locally
  ([`CertificateInstaller.cs`](../Omnipotent/Services/KliveAPI/CertificateInstaller.cs)); clients
  must trust it explicitly.
- The owner can execute arbitrary C# in-process through KliveAgent, and the Tier-5 permissions reach
  the host (terminal, desktop control, settings, live money). Compromise of the owner's sign-in — or of
  a profile given those permissions — is compromise of the host, not just of the API.
- Rate limiting is handled by `OmniDefence` (plus the per-IP sign-in throttle) and is tuned for a
  personal deployment, not for hostile public traffic.

Do not expose this to the public internet. It is published to be read, not to be run by others.
