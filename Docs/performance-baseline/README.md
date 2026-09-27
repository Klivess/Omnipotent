# API route performance baseline and snapshot plan

## Scope and method

On 2026-09-27 at 03:00 UTC, the authenticated live API was queried for all nine
preset route ranges, plus telemetry overview, runtime, browser
timing, weekly, health, live, route detail, trace list, and trace detail. The
[collector](collect-telemetry.ps1) records wall time and `Server-Timing` for each
telemetry endpoint. It saves aggregate JSON but omits user summaries and trace
bodies. It stores no credential. The [inventory](route-inventory.md) includes
all **128 route series observed over seven days**; it is not a list of all
registered routes, including routes with no traffic.

These are pre-change production measurements, not load tests. Percentiles are
approximately reconstructed from log-scale histograms. Low-sample percentiles
should be treated as examples of slow requests, not stable estimates.

## Findings

The seven-day route series accumulated 130.4 million milliseconds of server
time over 28,961 observations. A few analytical reads dominate the total:

| Route | 7d calls | p50 | p95 | Max | Cumulative server time |
|---|---:|---:|---:|---:|---:|
| `GET /omnidefence/overview` | 806 | 6.8 s | 8.1 s | 110 min | 14.5 h |
| `POST /batch` | 53 | 64.7 s | 34.5 min | 110 min | 8.3 h |
| `GET /omnidefence/requests` | 796 | 0.106 s | 2.9 s | 107 min | 6.4 h |
| `GET /omnidefence/ip` | 251 | 0.038 s | 8.6 min | 105 min | 5.5 h |
| `GET /projects/overview` | 1,916 | 0.126 s | 3.4 s | 38.7 s | 0.48 h |
| `GET /api/omnitrader/firm/overview` | 31 | 22.9 s | 108.8 s | 126.6 s | 0.29 h |
| `GET /klivemail/stats` | 25 | 11.4 s | 45.7 s | 58.4 s | 0.12 h |

The `/batch` trace examples fan out to slow analytical GETs, especially
OmniDefence overview. It is a multiplier and waiting surface, not necessarily
an independent expensive calculation. The 24-hour global overview reported
p95 6.8 s, p99 about 870 s, 24 peak in-flight requests, and 31 disconnects.
The worst tails also coincide with database contention or queueing; a snapshot
read alone cannot guarantee sub-second network time under an overloaded host.

Other analytical routes with observed seven-day p95 above one second include
OmniTrader risk/backtests/deployments, Projects cost simulator and list, CS2
analytics and balance history, Tripwire summary/events/list, KliveMail
mailboxes, and Omniscience stats/briefing. Their per-route samples and maxima
are in the inventory.

Telemetry preset routes already have cheap server handlers: this collection's
preset reads returned in 78–270 ms wall time, with about 0.1–0.2 ms of reported
application time. The trace list showed variable database delay: an earlier
[probe](trace-probe-0252.json) took **6,702.5 ms**, including 6,607.7 ms in its
handler, while the 03:00 collection took 90.5 ms. The live seven-day route
series reports 22.4 s p95 for the trace list over 39 calls. A first collection
found 30-day views 52.8 minutes old; the second happened just after the hourly
refresh and found them 26 seconds old. Health reported 16,965 recorded and
folded traces, zero dropped/backlogged, zero DB
errors, 48 preset views, and 10 warm route views.

## Implementation

The response rule is: HTTP handlers may validate parameters and copy a ready
response, but analytical scans, API calls, aggregation, and serialization run
in background workers. A missing or expired domain snapshot returns a fast
503; a cold telemetry custom query returns 202 while a bounded background
queue builds it. The website retries pending telemetry queries with backoff.

Background materializers now cover the expensive observed analytics:

- OmniDefence overview is aggregated off-route, persisted atomically, and
  refreshed about every 15 seconds. Request, auth, and IP lookups gained
  composite indexes; overview SQL combines repeated full-table passes.
  Filtered request lists and IP details use bounded, query-keyed background
  materialization; the default request list is persisted, while arbitrary
  filters and IPs stay in a bounded in-memory cache.
- OmniTrader firm overview, risk, backtests, and deployments have bounded-age
  snapshots. Backtest summaries avoid loading full candle and trade arrays.
- Projects overview, cost simulator, and list; KliveMail stats/mailboxes; CS2
  scanned-comparison analytics, balance history, and liquidity plan; and
  Tripwire summary use independent background snapshots. Mutation paths wake
  relevant workers where available.
- Omniscience stats overview and briefing preview have background-only
  snapshots, so cache misses do not trigger database scans on GET.
- Telemetry presets refresh continuously, including one-minute long-range
  views and one-second live view. Custom aggregate, trace list/detail, and
  exemplar work is bounded and precomputed off the request path.

Snapshots are written through temporary files and atomic replacement where
persisted. Their timestamps are checked before serving, so a failed worker
cannot silently serve an indefinitely old response. The tradeoff is a fast
503 during initial warm-up or prolonged refresh failure; callers can retry.

## Verification and rollout

The saved raw aggregates and endpoint timings support reproducing this audit.
The code change needs a production rollout and the same collector run again
before a sub-second claim can be made. Compare the 24-hour p50/p95/max and
`Server-Timing` after rollout, and inspect snapshot freshness, 503/202 counts,
DB errors, CPU, and OmniTrader broker request rates. In particular, OmniTrader
overview and risk builders both perform broker valuations, so their concurrent
background refreshes may raise broker traffic.

Even after analytical handlers are constant-time, network, authentication,
queueing, mutations, large responses, and intentional remote actions can take
more than one second. The target applies to ready analytical GET responses;
the full route inventory exposes other slow routes for continued triage.

## Data files

- [All observed route series](route-inventory.md)
- [Endpoint timings](endpoint-timings.json)
- [24-hour route aggregates](routes-24h.json)
- [Seven-day route aggregates](routes-7d.json)
- [30-day route aggregates](routes-30d.json)
- [Telemetry health](health.json)
- [Collection metadata](collection.json)
