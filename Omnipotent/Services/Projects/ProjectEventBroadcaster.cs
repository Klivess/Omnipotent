using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Server-push backbone (Phase 3): fans out the append-only event log to connected WebSocket
    /// clients so the website no longer has to poll. Two subscription modes:
    ///   * per-project — receives every <see cref="ProjectEvent"/> for one project (replaces the
    ///     ConversationPanel 3s poll and the workspace 5s poll);
    ///   * fleet firehose (no projectID) — receives a lightweight signal on any project's event so
    ///     the otherwise-frozen fleet page refreshes live.
    /// Each subscriber has its own outbound Channel + writer loop, so a single WebSocket is never
    /// written from two threads (SendAsync is not concurrency-safe). Reuses the existing
    /// KliveAPI.CreateWebSocketRoute auth (same mechanism the video streams use).
    /// </summary>
    public class ProjectEventBroadcaster
    {
        // Same serialization the Projects REST routes use — camelCase + enums-as-strings — so the
        // Vue client reads e.type / p.projectID identically to the poll responses (load-bearing).
        private static readonly JsonSerializerSettings CamelCase = new()
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
            Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() },
        };

        private sealed class Subscriber
        {
            public Guid Id { get; } = Guid.NewGuid();
            public WebSocket Socket { get; init; } = null!;
            /// <summary>null = fleet firehose; otherwise the single project this client follows.</summary>
            public string? ProjectID { get; init; }
            public Channel<string> Outbox { get; } = Channel.CreateBounded<string>(new BoundedChannelOptions(512)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
            public int NeedsResync;
        }

        private readonly ConcurrentDictionary<Guid, Subscriber> subscribers = new();
        private readonly Action<string> log;
        private readonly ProjectAgentActivityTracker? activity;

        public ProjectEventBroadcaster(ProjectEventLogStore eventLog, Action<string> log,
            ProjectAgentActivityTracker? activity = null)
        {
            this.log = log ?? (_ => { });
            this.activity = activity;
            eventLog.EventAppended += OnEventAppended;
            if (activity != null)
            {
                activity.Changed += OnActivityChanged;
                activity.Ended += OnActivityEnded;
            }
        }

        private void OnEventAppended(ProjectEvent e)
        {
            if (subscribers.IsEmpty) return;
            string? perProjectMsg = null;
            string? fleetMsg = null;
            foreach (var s in subscribers.Values)
            {
                if (s.ProjectID == null)
                {
                    fleetMsg ??= JsonConvert.SerializeObject(new { kind = "project-event", projectID = e.ProjectID, type = e.Type });
                    if (!s.Outbox.Writer.TryWrite(fleetMsg)) Interlocked.Exchange(ref s.NeedsResync, 1);
                }
                else if (string.Equals(s.ProjectID, e.ProjectID, StringComparison.Ordinal))
                {
                    perProjectMsg ??= JsonConvert.SerializeObject(new { kind = "event", @event = e }, CamelCase);
                    if (!s.Outbox.Writer.TryWrite(perProjectMsg)) Interlocked.Exchange(ref s.NeedsResync, 1);
                }
            }
        }

        /// <summary>
        /// Live agent activity (who is generating tokens, and what) rides the SAME socket as the
        /// event log, but is ephemeral: dropped rather than queued when a client falls behind, and
        /// never flagged for resync. A missed frame is superseded by the next one milliseconds later.
        /// </summary>
        private void OnActivityChanged(ProjectAgentActivity a)
        {
            if (subscribers.IsEmpty) return;
            string? msg = null;
            foreach (var s in subscribers.Values)
            {
                if (s.ProjectID == null || !string.Equals(s.ProjectID, a.ProjectID, StringComparison.Ordinal)) continue;
                msg ??= JsonConvert.SerializeObject(new { kind = "activity", activity = a }, CamelCase);
                s.Outbox.Writer.TryWrite(msg);
            }
        }

        private void OnActivityEnded(string projectID, string agentID)
        {
            if (subscribers.IsEmpty) return;
            string? msg = null;
            foreach (var s in subscribers.Values)
            {
                if (s.ProjectID == null || !string.Equals(s.ProjectID, projectID, StringComparison.Ordinal)) continue;
                msg ??= JsonConvert.SerializeObject(new { kind = "activity-ended", projectID, agentID }, CamelCase);
                s.Outbox.Writer.TryWrite(msg);
            }
        }

        /// <summary>
        /// Handles one WebSocket connection to /projects/events/stream. For a per-project subscriber
        /// it first replays events after <paramref name="since"/> (so a reconnect resumes without a
        /// gap), then streams live. Returns when the socket closes.
        /// </summary>
        public async Task HandleAsync(WebSocket socket, string? projectID, long since,
            Func<string, long, IEnumerable<ProjectEvent>> replay)
        {
            var sub = new Subscriber
            {
                Socket = socket,
                ProjectID = string.IsNullOrEmpty(projectID) || projectID == "*" ? null : projectID,
            };
            // Register BEFORE replaying so no live event slips through the gap between replay and
            // registration; the client dedupes by sequence, so a duplicate at the boundary is fine.
            subscribers[sub.Id] = sub;
            try
            {
                var send = SendLoopAsync(sub);
                if (sub.ProjectID != null)
                {
                    foreach (var e in replay(sub.ProjectID, since))
                        await sub.Outbox.Writer.WriteAsync(JsonConvert.SerializeObject(new { kind = "event", @event = e }, CamelCase));
                    // Whoever is mid-turn right now: without this, a client that connects between
                    // two activity frames shows nothing until the next model turn starts.
                    var live = activity?.ListForProject(sub.ProjectID);
                    if (live is { Count: > 0 })
                        await sub.Outbox.Writer.WriteAsync(
                            JsonConvert.SerializeObject(new { kind = "activity-snapshot", activities = live }, CamelCase));
                }

                var recv = ReceiveUntilCloseAsync(socket);
                await Task.WhenAny(send, recv);
            }
            catch (Exception ex) { log($"Event stream handler error: {ex.Message}"); }
            finally
            {
                subscribers.TryRemove(sub.Id, out _);
                sub.Outbox.Writer.TryComplete();
                try { if (socket.State == WebSocketState.Open) await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
            }
        }

        private async Task SendLoopAsync(Subscriber sub)
        {
            var heartbeat = Task.Delay(TimeSpan.FromSeconds(25));
            while (sub.Socket.State == WebSocketState.Open)
            {
                string msg;
                var read = sub.Outbox.Reader.ReadAsync().AsTask();
                var done = await Task.WhenAny(read, heartbeat);
                if (done == heartbeat)
                {
                    // Keepalive so idle connections (and proxies) don't drop the socket.
                    await SendTextAsync(sub.Socket, "{\"kind\":\"ping\"}");
                    heartbeat = Task.Delay(TimeSpan.FromSeconds(25));
                    continue;
                }
                try { msg = await read; }
                catch (ChannelClosedException) { break; }
                await SendTextAsync(sub.Socket, msg);
                if (Interlocked.Exchange(ref sub.NeedsResync, 0) == 1)
                    await SendTextAsync(sub.Socket, "{\"kind\":\"resync\",\"reason\":\"client-fell-behind\"}");
            }
        }

        private static async Task SendTextAsync(WebSocket socket, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        // The client never sends; ReceiveAsync completes when it closes, which lets us tear down.
        private static async Task ReceiveUntilCloseAsync(WebSocket socket)
        {
            var buf = new byte[1024];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var r = await socket.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch { /* socket gone */ }
        }
    }
}
