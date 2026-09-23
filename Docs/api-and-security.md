# The API control plane

`KliveAPI` is the single HTTP/WebSocket surface for the whole platform. It is itself an
`OmniService`, so it starts, logs, restarts and reports health like every other module — but every
other module registers *through* it. That means one place enforces permissions, body limits, caching
and statistics, and no module ships its own web server.

Source: [`Omnipotent/Services/KliveAPI/`](../Omnipotent/Services/KliveAPI)

## Routes are declared by the modules that own them

There is no central routing table to keep in sync. A module registers its own routes during
`ServiceMain()`, and every registration must state the permission it requires — the parameter is not
optional, so a route cannot be added without a deliberate access decision:

```csharp
await CreateAPIRoute("/projects/list", HandleList,
                     HttpMethod.Get, KMPermissions.Klives);

await CreateStreamingAPIRoute("/klivecloud/upload", HandleUpload,
                              HttpMethod.Post, KMPermissions.Admin,
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

**380 routes are registered this way across 36 files** — the surface is large because the modules
are, not because any one of them is.

## Permission model

A single ordered ladder, defined in
[`KMProfileManager`](../Omnipotent/Klives%20Management/KMProfileManager.cs):

```
Anybody(0) → Guest(1) → Manager(2) → Associate(3) → Admin(4) → Klives(5)
```

Checks are `user.KlivesManagementRank >= route.authenticationLevelRequired`, so a rank inherits every
capability below it. `Anybody` routes skip user resolution entirely — health checks and the login
endpoint do not pay for authentication.

Beyond the rank comparison:

- Profiles carry a `CanLogin` flag, checked independently of rank, so access can be revoked without
  demoting or deleting a profile.
- WebSocket upgrades run the same gate as HTTP before the socket is accepted.
- Denials are categorised (`UnauthRoute`, `InvalidPassword`, missing header) rather than collapsed
  into one 401, which is what makes the defence layer's logs useful.

Anything that can operate the platform — KliveAgent, Projects, the service terminal, cache controls —
is `KMPermissions.Klives`, the owner-only top rank.

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
   ├─ authenticate + rank check
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
- Per-route hit and miss counters, a prefix denylist, a global kill switch, and Klives-only
  `/KliveAPI/cache/stats` and `/KliveAPI/cache/clear` routes.

The trap, learned in production: correctness depends on **every** store a route reads being
instrumented. An uninstrumented store behind a cacheable route serves stale data indefinitely — which
is exactly how it first manifested, as settings that appeared not to save.

## Batch endpoint

`POST /batch` executes several sub-requests in one round trip, on the thread pool in parallel, with
each sub-request independently permission-checked against the caller's rank. Sub-requests share the
same cache as direct GETs, so a batch is never a way to bypass either the gate or the cache. This
exists because a dashboard page load is a burst of a dozen small reads, and paying HTTPS setup and
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
| `auth` | Resolving the profile from the Authorization header. |
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
is rebuilt on the engine thread when buckets close and stored pre-serialized and pre-compressed with an
ETag — a request is a dictionary lookup (usually a 304). Custom ranges/buckets are single-flighted
engine queries over the in-memory tiers, capped at 300 points; asking for resolution the retained data
no longer has returns a coarser bucket with `degraded: true` and a `note`, never a slow scan.
Routes (Klives): `/KliveAPI/telemetry/{overview,routes,route,runtime,rum,weekly,health,live,traces,trace}`.

`OmniDefence` consumes the same request outcomes for abuse tracking. Its writes are deliberately
never awaited on the request path — doing so once turned a background SQLite write into a
ten-minute login stall.

## Security posture — read this before deploying anything

This is a **single-owner, self-hosted personal system**, and the threat model is "the owner's own
machine on the owner's own network". It has not been through an external security review.

Specifically:

- Authentication is password-based against local profiles. There is no MFA, no session rotation, and
  no OAuth.
- Transport uses a self-signed certificate installed locally
  ([`CertificateInstaller.cs`](../Omnipotent/Services/KliveAPI/CertificateInstaller.cs)); clients
  must trust it explicitly.
- The top rank (`Klives`) can execute arbitrary C# in-process through KliveAgent. Compromise of that
  credential is compromise of the host, not just of the API.
- Rate limiting is handled by `OmniDefence` and is tuned for a personal deployment, not for hostile
  public traffic.

Do not expose this to the public internet. It is published to be read, not to be run by others.
