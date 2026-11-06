using System.Collections.Concurrent;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Kernel.Events
{
    /// <summary>One live-stream event. Ids are monotonic so a reconnecting client resumes with <c>since=</c>.</summary>
    public sealed record StreamEvent(long EventId, long AtMs, string Topic, string PayloadJson);

    /// <summary>Well-known topics pushed to the UI over the WebSocket stream.</summary>
    public static class Topics
    {
        public const string JobProgress = "job.progress";
        public const string JobFinished = "job.finished";
        public const string TacticProgress = "tactic.progress";
        public const string TacticFinished = "tactic.finished";
        public const string ReadModelUpdated = "readmodel.updated";
        public const string Alert = "alert";
        public const string LiveMessage = "live.message";
        public const string ModuleHealth = "module.health";
    }

    /// <summary>
    /// Durable, replayable event fan-out for the live stream (plan: WebSocket hub with replay cursors).
    /// Events are written to intel.db first (so a client that reconnects after a blip replays what it
    /// missed), then broadcast to in-process subscribers. Subscriber exceptions are isolated and logged.
    /// </summary>
    public sealed class EventHub
    {
        private readonly SqliteStore intel;
        private readonly IClock clock;
        private readonly Action<string, Exception?> log;
        private readonly ConcurrentDictionary<long, Func<StreamEvent, ValueTask>> subscribers = new();
        private long nextSubscriberId;

        public EventHub(SqliteStore intel, IClock clock, Action<string, Exception?> log)
        {
            this.intel = intel;
            this.clock = clock;
            this.log = log;
        }

        public async Task<StreamEvent> PublishAsync(string topic, object payload, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(topic);
            string json = payload as string ?? JsonSerializer.Serialize(payload, KernelJson.Options);
            long now = clock.NowMs;
            long id = await intel.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "INSERT INTO stream_events(at_ms, topic, payload_json) VALUES($a,$t,$p)", ("$a", now), ("$t", topic), ("$p", json));
                return c.Scalar<long>(tx, "SELECT last_insert_rowid()");
            }, ct).ConfigureAwait(false);
            var ev = new StreamEvent(id, now, topic, json);
            foreach (var (subId, handler) in subscribers)
            {
                try { await handler(ev).ConfigureAwait(false); }
                catch (Exception ex) { log($"[OmniscienceV3] stream subscriber {subId} failed on event {id}; it stays subscribed.", ex); }
            }
            return ev;
        }

        /// <summary>Events after <paramref name="sinceEventId"/>, oldest first (bounded).</summary>
        public IReadOnlyList<StreamEvent> Replay(long sinceEventId, int limit = 500)
        {
            using var c = intel.OpenRead();
            return c.Query(null, "SELECT event_id, at_ms, topic, payload_json FROM stream_events WHERE event_id > $s ORDER BY event_id LIMIT $l",
                r => new StreamEvent(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3)),
                ("$s", sinceEventId), ("$l", Math.Clamp(limit, 1, 5000)));
        }

        public IDisposable Subscribe(Func<StreamEvent, ValueTask> handler)
        {
            long id = Interlocked.Increment(ref nextSubscriberId);
            subscribers[id] = handler;
            return new Subscription(() => subscribers.TryRemove(id, out _));
        }

        public int SubscriberCount => subscribers.Count;

        /// <summary>Drops events older than <paramref name="retain"/> (by event time).</summary>
        public Task<int> PruneAsync(TimeSpan retain, CancellationToken ct = default) =>
            intel.WriteAsync((c, tx) => c.Exec(tx, "DELETE FROM stream_events WHERE at_ms < $cut",
                ("$cut", clock.NowMs - (long)retain.TotalMilliseconds)), ct);

        private sealed class Subscription : IDisposable
        {
            private Action? onDispose;
            public Subscription(Action onDispose) => this.onDispose = onDispose;
            public void Dispose() => Interlocked.Exchange(ref onDispose, null)?.Invoke();
        }
    }
}
