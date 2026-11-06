using System.Collections.Concurrent;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Kernel.Events
{
    /// <summary>One live-stream event. Ids are monotonic so a reconnecting client resumes with <c>since=</c>.</summary>
    public sealed record StreamEvent(long EventId, long AtMs, string Topic, string PayloadJson);

    /// <summary>The retained id range of the stream. <see cref="LatestEver"/> survives pruning (it is the
    /// AUTOINCREMENT high-water mark), so a cursor older than <see cref="Oldest"/> is known to have missed events.</summary>
    public readonly record struct StreamBounds(long Oldest, long Newest, long LatestEver)
    {
        public bool IsEmpty => Newest == 0;
    }

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
    /// missed), then broadcast to in-process subscribers.
    /// <para>
    /// Publication is serialised: each event is committed and broadcast before the next one is written, so
    /// subscribers always see ids in increasing order and every id below one they saw is already committed.
    /// The live hub relies on that to merge replay and live delivery by id without losing or repeating events.
    /// Subscribers must therefore be quick and must not publish from inside their handler.
    /// </para>
    /// </summary>
    public sealed class EventHub
    {
        private readonly SqliteStore intel;
        private readonly IClock clock;
        private readonly Action<string, Exception?> log;
        private readonly ConcurrentDictionary<long, Func<StreamEvent, ValueTask>> subscribers = new();
        private readonly SemaphoreSlim publishOrder = new(1, 1);
        private long nextSubscriberId;

        public EventHub(SqliteStore intel, IClock clock, Action<string, Exception?> log)
        {
            this.intel = intel;
            this.clock = clock;
            this.log = log;
        }

        /// <summary>Publishes <paramref name="payload"/> (any serialisable value; a string is sent as a JSON string,
        /// a <see cref="JsonElement"/> or <see cref="JsonDocument"/> as itself).</summary>
        public async Task<StreamEvent> PublishAsync(string topic, object payload, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(topic);
            string json = payload switch
            {
                JsonDocument d => d.RootElement.GetRawText(),
                _ => JsonSerializer.Serialize(payload, KernelJson.Options),
            };
            await publishOrder.WaitAsync(ct).ConfigureAwait(false);
            try
            {
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
            finally
            {
                publishOrder.Release();
            }
        }

        /// <summary>Events after <paramref name="sinceEventId"/>, oldest first (bounded).</summary>
        public IReadOnlyList<StreamEvent> Replay(long sinceEventId, int limit = 500)
        {
            using var c = intel.OpenRead();
            return c.Query(null, "SELECT event_id, at_ms, topic, payload_json FROM stream_events WHERE event_id > $s ORDER BY event_id LIMIT $l",
                r => new StreamEvent(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3)),
                ("$s", Math.Max(0, sinceEventId)), ("$l", Math.Clamp(limit, 1, 5000)));
        }

        public StreamBounds Bounds()
        {
            using var c = intel.OpenRead();
            var (oldest, newest) = c.Query(null, "SELECT MIN(event_id), MAX(event_id) FROM stream_events",
                r => (r.IsDBNull(0) ? 0L : r.GetInt64(0), r.IsDBNull(1) ? 0L : r.GetInt64(1))).First();
            long ever = c.Scalar<long?>(null, "SELECT seq FROM sqlite_sequence WHERE name='stream_events'") ?? 0;
            return new StreamBounds(oldest, newest, Math.Max(ever, newest));
        }

        public IDisposable Subscribe(Func<StreamEvent, ValueTask> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
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
