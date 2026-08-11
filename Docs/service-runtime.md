# The service runtime

Omnipotent is not a collection of microservices. Every module is an `OmniService` subclass, and
they all run inside **one .NET 9 process**, sharing a service graph, a logger, a settings store, a
scheduler and an HTTP listener. This document describes the runtime that makes that safe.

Source: [`Omnipotent/Service Manager/`](../Omnipotent/Service%20Manager)

## Why one process

In-process modules share references, not serialised messages. A module can call another module's
methods directly, hold a live reference to its state, and subscribe to its events without a broker,
a network hop or a schema. The cost of that convenience is blast radius: an unhandled exception on
any thread can take the whole process down, and one blocking module can starve the rest. Most of the
runtime exists to pay that cost down.

## `OmniService` — the module contract

[`OmniService.cs`](../Omnipotent/Service%20Manager/OmniService.cs)

A module subclasses `OmniService`, declares a name and a priority, and overrides `ServiceMain()`:

```csharp
public class MyService : OmniService
{
    public MyService() : base("My Service", ThreadAnteriority.Standard) { }

    protected override async void ServiceMain()
    {
        await ServiceLog("Started.");
        await CreateAPIRoute("/myservice/status", HandleStatus,
                             HttpMethod.Get, KMPermissions.Klives);
    }
}
```

Subclassing gets the module, for free:

| Facility | Methods |
|---|---|
| Structured logging | `ServiceLog`, `ServiceLogError`, `ServiceUpdateLoggedMessage` |
| HTTP + WebSocket routes | `CreateAPIRoute`, `CreateBufferedAPIRoute`, `CreateStreamingAPIRoute` |
| Typed persistent settings | `GetBoolOmniSetting`, `GetIntOmniSetting`, `GetStringOmniSetting`, `GetDropdownOmniSetting`, `GetStringListOmniSetting` |
| Scheduled work | `ServiceCreateScheduledTask(dueDateTime, name, …)` |
| File and data access | `GetDataHandler()` |
| Service discovery | `GetServiceByName`, `GetServicesByType<T>`, `GetActiveServices` |
| Cross-module invocation | `ExecuteServiceMethod<T>`, `GetServiceObject<T>` |
| Lifecycle | `ServiceStart`, `TerminateService`, `RestartService`, `GetServiceUptime` |

Settings are declarative rather than config-file driven. A module asks for a value and describes it;
`OmniGlobalSettingsManager` persists it, exposes it to the dashboard, and can mark it `sensitive`
(hidden in UI) or `askKlivesForFulfillment` (prompt the owner over Discord when unset instead of
silently defaulting).

## Threading model

Each service runs on its **own dedicated `Thread`**, not a pooled task. The thread is named
`OmniServiceThread_<name>` — so a stack dump or debugger attach shows exactly which module is where —
and its OS priority is derived from the declared `ThreadAnteriority`:

```
Low  →  Standard  →  High  →  Critical
```

`ServiceMain()` runs on that thread, then the thread parks on `Task.Delay(-1).Wait()` so the module
stays alive to serve callbacks, routes and timers.

Because most modules do their real work in `async void` event handlers and continuations, exceptions
there would normally escape onto arbitrary pool threads and kill the process. The runtime installs a
custom `SynchronizationContext` per service thread
([`ServiceExceptionSynchronizationContext`](../Omnipotent/Service%20Manager/OmniService.cs)) that
routes every posted callback's exceptions back to that service's own handler. Faults stay attributed
to the module that caused them.

The process also raises the thread-pool floor to 128 at startup
([`Program.cs`](../Omnipotent/Program.cs)). The default floor is `ProcessorCount`, and the runtime
only grows the pool by roughly one thread per 500 ms — so a burst of dashboard requests that block on
synchronous SQLite I/O could pin every worker and leave the API's health endpoint queued behind them.

## Failure handling and recovery

`HandleUnhandledServiceException` is the single funnel for a module's fatal errors:

1. **Deduplicate.** An `Interlocked.CompareExchange` latch ensures one crash produces one response,
   even when several threads fault at once.
2. **Mark inactive.** The module stops reporting healthy immediately.
3. **Log** the full exception.
4. **Alert the owner** with a formatted Discord embed.
5. **Terminate** the module cleanly — cancellation token, quit callback, thread interrupt.
6. **Restart if `Critical`.** Modules declared `ThreadAnteriority.Critical` self-restart after a
   2-second delay. Everything else stays down until restarted deliberately.

Each step is individually guarded, so a failure inside crash handling (a dead Discord connection, say)
cannot mask the original fault.

There are three tiers of recovery, in widening scope:

| Tier | Watcher | Handles |
|---|---|---|
| Thread | `SynchronizationContext` per service | Exceptions escaping `async void` |
| Process | `HandleUnhandledServiceException` + `OmniServiceMonitor` | Module crash, restart of Critical modules |
| External | [`OmnipotentProcessMonitor`](../OmnipotentProcessMonitor/Program.cs) | Whole-process death |

The external watchdog is a separate executable. When the main process dies it relaunches it with
`errorOccurred=<path>`; startup detects that argument, reads the crash log, and posts it to Discord as
a file attachment. A crash that happens while nobody is watching still produces a notification with
the log attached.

## Boot sequence

[`OmniServiceManager`](../Omnipotent/Service%20Manager/OmniServiceManager.cs) constructs the
platform in dependency order, because later stages need earlier ones to log and persist:

1. `OmniLogging` — everything after this can report failure.
2. `DataUtil` — file and serialisation layer.
3. `OmniServiceMonitor` — begins sampling before modules start.
4. `OmniStartupManager` — one-shot prerequisite work, **bounded to 30 seconds**. If it overruns, the
   manager logs and continues rather than hanging boot forever.
5. `TimeManager` — scheduled and recurring tasks.

Then [`Program.cs`](../Omnipotent/Program.cs) starts the modules themselves via
`CreateAndStartNewMonitoredOmniService`, which rejects duplicate names (unless explicitly overridden),
attaches the shared manager, starts the thread and registers the module with the monitor.

Some modules are environment-gated: `OmniPaths.CheckIfOnServer()` decides what runs on the production
host versus a development machine.

## Service discovery

Modules find each other by name or by type rather than by injected interface:

```csharp
var rag = (await GetServicesByType<KliveRAG>())[0];
var result = await ExecuteServiceMethod<KliveRAG>("SearchAsync", query);
```

`GetServiceByClassType<T>` **waits** for the target to appear instead of failing, which removes
start-order coupling: a module that boots early can reference one that boots later without a barrier.
`ExecuteServiceMethod<T>` and `GetServiceObject<T>` resolve members reflectively, which is what allows
KliveAgent's compiled C# to reach any live module without a pre-registered tool list
(see [the agent doc](agent-and-rag.md)).

## Monitoring

[`OmniServiceMonitor`](../Omnipotent/Service%20Manager/OmniServiceMonitor.cs) samples continuously:

- **Per-thread CPU and memory** for every monitored module.
- **System CPU**, computed from `GetSystemTimes` deltas, and total system RAM.
- **Uptime statistics** — every run is recorded as a period with start and end; the store derives
  total uptime, average uptime, current uptime, period count and cumulative outage, and persists to
  disk so history survives restarts.

These feed the dashboard tiles, and the same data is what makes "recovered automatically at 03:14"
verifiable after the fact rather than a claim.

## Known limitations

- One process means one blast radius. Critical-tier restart and the external watchdog reduce the
  consequences; they do not give per-module isolation the way separate processes would.
- `TerminateService` interrupts the service thread. A module blocked in unmanaged code may not
  respond promptly.
- `GetServiceByClassType<T>` waits indefinitely for a module that never starts.
- Restart is not backoff-limited: a module that crashes deterministically on start and is marked
  Critical will restart on a 2-second cycle.
