using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Omnipotent.Services.OmniscienceV3.Kernel.Events;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Api
{
    /// <summary>
    /// The v3 live stream: one WebSocket per open page (EventSource can't send the site's Bearer auth; KliveAPI
    /// accepts it as <c>?authorization=</c> on the upgrade).
    /// <list type="bullet">
    /// <item><b>Resume:</b> the client passes the last id it saw as <c>since=</c>; everything after it is replayed
    /// in pages, then live events follow with no gap and no duplicate (merged by id; the hub publishes in order).</item>
    /// <item><b>Gaps:</b> if events after the cursor were pruned, or the cursor is from another database, the first
    /// frame is <c>stream.reset</c>: the client refetches its views and continues from the id in that frame.</item>
    /// <item><b>Filtering:</b> <c>topics=</c> keeps topics with any of the given prefixes; topics the caller may not
    /// read (raw messages without messages.read, say) are never sent.</item>
    /// <item><b>Back-pressure:</b> each connection has a bounded queue; a client too slow to drain it is closed
    /// with 1008 and simply reconnects with its cursor.</item>
    /// <item><b>Liveness:</b> a <c>stream.ping</c> frame every 25 s carries the cursor, so a client filtering to rare
    /// topics still advances it.</item>
    /// </list>
    /// Frames: <c>{"id":N,"at":ms,"topic":"…","payload":{…}}</c>.
    /// </summary>
    public sealed class LiveStreamHub
    {
        public const string ResetTopic = "stream.reset";
        public const string PingTopic = "stream.ping";
        private const int ReplayPage = 1000;

        private readonly EventHub events;
        private readonly IClock clock;
        private readonly Action<string, Exception?> logError;
        private readonly TimeSpan heartbeat;
        private readonly int queueCapacity;
        private int connections;

        public LiveStreamHub(EventHub events, IClock clock, Action<string, Exception?> logError, TimeSpan? heartbeat = null, int queueCapacity = 2000)
        {
            this.events = events;
            this.clock = clock;
            this.logError = logError;
            this.heartbeat = heartbeat ?? TimeSpan.FromSeconds(25);
            this.queueCapacity = Math.Max(16, queueCapacity);
        }

        public int Connections => Volatile.Read(ref connections);

        public static bool MatchesPrefixes(string topic, IReadOnlyList<string> prefixes) =>
            prefixes.Count == 0 || prefixes.Any(p => topic.StartsWith(p, StringComparison.Ordinal));

        public static IReadOnlyList<string> ParseTopics(string? raw) =>
            string.IsNullOrWhiteSpace(raw) ? Array.Empty<string>()
                : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(50).ToArray();

        public static long ParseSince(string? raw) =>
            long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long s) && s > 0 ? s : 0;

        /// <summary>
        /// Where a client resuming at <paramref name="since"/> starts. Returns the cursor to continue from and,
        /// when events were lost (pruned, or a cursor from another database), the reason for a reset frame.
        /// </summary>
        public static (long Cursor, string? ResetReason) PlanResume(long since, StreamBounds bounds)
        {
            if (since <= 0) return (0, null); // a fresh client replays what is retained
            if (since > bounds.LatestEver) return (bounds.LatestEver, "cursor_ahead");
            if (since == bounds.LatestEver) return (since, null); // nothing has been published since
            // Events after the cursor exist or existed: they are all still here only if the log reaches back to it.
            if (bounds.IsEmpty || bounds.Oldest > since + 1) return (bounds.LatestEver, "events_pruned");
            return (since, null);
        }

        /// <param name="maySee">The caller's per-topic read permission (evaluated once per topic per connection).</param>
        public async Task HandleAsync(WebSocket socket, NameValueCollection query, Func<string, bool> maySee, CancellationToken stopping)
        {
            Interlocked.Increment(ref connections);
            try { await RunAsync(socket, query, maySee, stopping).ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref connections); }
        }

        private async Task RunAsync(WebSocket socket, NameValueCollection query, Func<string, bool> maySee, CancellationToken stopping)
        {
            var prefixes = ParseTopics(query["topics"]);
            var permitted = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
            bool Wants(string topic) => MatchesPrefixes(topic, prefixes) && permitted.GetOrAdd(topic, maySee);

            // Sends (events, pings) stop on sendCts; the receive loop has its own token so a graceful close
            // handshake can still complete after sending has stopped.
            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            using var receiveCts = new CancellationTokenSource();
            using var sendLock = new SemaphoreSlim(1, 1);
            var cursor = new CursorBox();
            int overflowed = 0;
            var queue = Channel.CreateBounded<StreamEvent>(new BoundedChannelOptions(queueCapacity)
            {
                SingleReader = true,
                SingleWriter = true, // the hub publishes one event at a time
                FullMode = BoundedChannelFullMode.Wait, // never blocks here: TryWrite fails instead, and we close
            });

            // Subscribe before replaying so nothing published during the replay is missed; ids dedupe the overlap.
            using var subscription = events.Subscribe(ev =>
            {
                if (!Wants(ev.Topic) || queue.Writer.TryWrite(ev)) return ValueTask.CompletedTask;
                if (Interlocked.Exchange(ref overflowed, 1) == 0) CancelQuietly(sendCts);
                return ValueTask.CompletedTask;
            });

            var receive = ReceiveUntilClosedAsync(socket, sendCts, receiveCts.Token);
            var beat = HeartbeatAsync(socket, sendLock, cursor, sendCts.Token);
            try
            {
                var (start, reset) = PlanResume(ParseSince(query["since"]), events.Bounds());
                cursor.Value = start;
                if (reset != null)
                    await SendAsync(socket, sendLock, Frame(start, clock.NowMs, ResetTopic, JsonSerializer.Serialize(new { reason = reset })), sendCts.Token).ConfigureAwait(false);

                while (true)
                {
                    var page = events.Replay(cursor.Value, ReplayPage);
                    foreach (var ev in page)
                    {
                        if (ev.EventId <= cursor.Value) continue;
                        if (Wants(ev.Topic)) await SendAsync(socket, sendLock, Frame(ev), sendCts.Token).ConfigureAwait(false);
                        cursor.Value = ev.EventId; // past filtered events too, so the next page continues after them
                    }
                    if (page.Count < ReplayPage) break;
                }

                await foreach (var ev in queue.Reader.ReadAllAsync(sendCts.Token).ConfigureAwait(false))
                {
                    if (ev.EventId <= cursor.Value) continue;
                    await SendAsync(socket, sendLock, Frame(ev), sendCts.Token).ConfigureAwait(false);
                    cursor.Value = ev.EventId;
                }
            }
            catch (OperationCanceledException) when (sendCts.IsCancellationRequested)
            {
                // Client closed, service stopping, or the client fell too far behind: closed below.
                CancelQuietly(sendCts);
            }
            catch (WebSocketException)
            {
                // The client vanished mid-send.
                CancelQuietly(sendCts);
            }
            catch (Exception ex)
            {
                logError("[OmniscienceV3] live stream connection failed.", ex);
                CancelQuietly(sendCts);
            }
            finally
            {
                CancelQuietly(sendCts);
                await beat.ConfigureAwait(false);
                var (status, reason) = Volatile.Read(ref overflowed) == 1
                    ? (WebSocketCloseStatus.PolicyViolation, "slow consumer; reconnect with since=")
                    : stopping.IsCancellationRequested
                        ? (WebSocketCloseStatus.EndpointUnavailable, "service stopping")
                        : (WebSocketCloseStatus.NormalClosure, "closed");
                await CloseOutputQuietlyAsync(socket, status, reason).ConfigureAwait(false);
                // The pending receive picks up the client's close frame; a client that never answers is cut off.
                if (await Task.WhenAny(receive, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) != receive)
                    CancelQuietly(receiveCts);
                await receive.ConfigureAwait(false);
            }
        }

        private static void CancelQuietly(CancellationTokenSource source)
        {
            try { source.Cancel(); }
            catch (ObjectDisposedException) { /* the connection already ended */ return; }
        }

        private sealed class CursorBox
        {
            private long current;
            public long Value { get => Interlocked.Read(ref current); set => Interlocked.Exchange(ref current, value); }
        }

        internal static string Frame(StreamEvent ev) => Frame(ev.EventId, ev.AtMs, ev.Topic, ev.PayloadJson);

        internal static string Frame(long id, long atMs, string topic, string payloadJson) =>
            "{\"id\":" + id.ToString(CultureInfo.InvariantCulture) + ",\"at\":" + atMs.ToString(CultureInfo.InvariantCulture) +
            ",\"topic\":" + JsonSerializer.Serialize(topic) + ",\"payload\":" + payloadJson + "}";

        /// <summary>Reads (and discards) client frames so close handshakes and pings are processed; ends the
        /// connection when the client closes or disappears.</summary>
        private static async Task ReceiveUntilClosedAsync(WebSocket socket, CancellationTokenSource endSending, CancellationToken abort)
        {
            var buffer = new byte[1024];
            try
            {
                while (socket.State is WebSocketState.Open or WebSocketState.CloseSent)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), abort).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
                // Aborted, or the client is gone: either way the connection ends.
                CancelQuietly(endSending);
                return;
            }
            // The client closed (or the close handshake finished): stop sending.
            CancelQuietly(endSending);
        }

        private async Task HeartbeatAsync(WebSocket socket, SemaphoreSlim sendLock, CursorBox cursor, CancellationToken ct)
        {
            using var timer = new PeriodicTimer(heartbeat);
            try
            {
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                    await SendAsync(socket, sendLock, Frame(cursor.Value, clock.NowMs, PingTopic, "{}"), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
                // The connection is ending; the main loop closes it.
                return;
            }
        }

        private static async Task SendAsync(WebSocket socket, SemaphoreSlim sendLock, string frame, CancellationToken ct)
        {
            // A WebSocket allows one outstanding send: events and pings take turns.
            await sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(frame)), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
            finally
            {
                sendLock.Release();
            }
        }

        /// <summary>Sends our close frame without waiting for the reply (the receive loop collects it), which is
        /// allowed while a receive is pending, unlike <see cref="WebSocket.CloseAsync"/>.</summary>
        private static async Task CloseOutputQuietlyAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
        {
            if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await socket.CloseOutputAsync(status, reason, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
                // The peer did not complete the handshake; the socket is torn down either way.
                return;
            }
        }
    }
}
