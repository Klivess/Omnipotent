using System.Collections.Specialized;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Profiles.Activity;
using Omnipotent.Profiles.Permissions;
using Omnipotent.Profiles.Sessions;

namespace Omnipotent.Profiles
{
    /// <summary>
    /// The live channel. Every website tab keeps a SessionWatch socket open: the server pushes
    /// access changes, suspensions and sign-outs down it the moment they happen, and the tab reports
    /// which page it is on (presence). Profile managers can follow another profile live over
    /// /KMProfiles/admin/live.
    /// </summary>
    public partial class KMProfileManager
    {
        private static readonly TimeSpan SafetyCheckInterval = TimeSpan.FromSeconds(5);

        /// <summary>
        /// "Starting up, reconnect shortly". A private-use code: HTTP.sys's WebSocket stack refuses
        /// the later-registered 1013 "Try Again Later" and resets the connection instead.
        /// </summary>
        internal const WebSocketCloseStatus TryAgainLater = (WebSocketCloseStatus)4013;

        private async Task CreateLiveRoutes()
        {
            var api = await ResolveApiAsync();

            // Public at the pipeline: the handler authenticates itself so it can tell the tab *why*
            // its session ended (revoked, expired, disabled) before closing.
            await api.CreateWebSocketRoute("/KMProfiles/SessionWatch", (context, socket, query, _) => HandleSessionWatchAsync(context, socket, query),
                Perms.Public);

            await api.CreateWebSocketRoute("/KMProfiles/admin/live", (context, socket, query, user) => HandleAdminLiveAsync(socket, query, user),
                ProfilesPerms.ActivityLive);
        }

        private async Task HandleSessionWatchAsync(HttpListenerContext context, WebSocket socket, NameValueCollection query)
        {
            // The route is Public, so it is reachable while profiles are still loading after a
            // restart. "Not found" then would be a lie that signs every open tab out: close with
            // "try again later" instead and let the tab reconnect.
            if (!loaded)
            {
                // CloseOutput: nothing to wait for — the tab reconnects on its own schedule.
                try { await socket.CloseOutputAsync(TryAgainLater, "Starting", CancellationToken.None); } catch { }
                return;
            }

            string ip = "";
            try { ip = Omnipotent.Services.OmniDefence.OmniDefence.ExtractClientIp(context.Request); } catch { }
            var auth = Authenticate(query["authorization"] ?? context.Request.Headers["Authorization"], ip);
            string? failure = auth.Profile == null
                ? auth.Failure switch
                {
                    AccessDenyReason.SessionRevoked => "SessionRevoked",
                    AccessDenyReason.SessionExpired => "SessionExpired",
                    _ => "ProfileNotFound",
                }
                : !auth.Profile.CanLogin ? "ProfileDisabled" : null;
            if (failure != null)
            {
                await SendStateAndCloseAsync(socket, failure);
                return;
            }

            var profile = auth.Profile!;
            var session = auth.Session;
            var conn = Presence.Register(profile.UserID, session?.SessionId, ip, session?.Label ?? SessionStore.DescribeUserAgent(context.Request.UserAgent), DateTime.UtcNow);
            using var closed = new CancellationTokenSource();
            try
            {
                await SendAsync(socket, JsonConvert.SerializeObject(new { type = "session-state", state = "SessionActive" }), closed.Token);
                await SendAsync(socket, JsonConvert.SerializeObject(new { type = "hello", accessVersion = profile.AccessVersion, connectionId = conn.ConnectionId }), closed.Token);

                var sendLoop = Task.Run(() => SendLoopAsync(socket, conn, closed.Token));
                var safetyLoop = Task.Run(() => SafetyLoopAsync(conn, session?.SessionId, closed.Token));
                await ReceiveLoopAsync(socket, conn, closed.Token);
                closed.Cancel();
                await Task.WhenAny(Task.WhenAll(sendLoop, safetyLoop), Task.Delay(2000));
            }
            catch (WebSocketException) { }
            catch (OperationCanceledException) { }
            finally
            {
                try { closed.Cancel(); } catch { }
                Presence.Unregister(conn, DateTime.UtcNow);
                try
                {
                    if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                }
                catch { }
            }
        }

        /// <summary>Reads presence frames: {type:"presence", path, title, visible}.</summary>
        private async Task ReceiveLoopAsync(WebSocket socket, PresenceConnection conn, CancellationToken token)
        {
            var buffer = new byte[8192];
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (sb.Length > 64 * 1024) return; // a presence frame is tiny; anything else is abuse
                } while (!result.EndOfMessage);

                try
                {
                    var msg = JObject.Parse(sb.ToString());
                    string? type = msg.Value<string>("type");
                    if (type == "presence" || type == "ping")
                    {
                        bool? visible = msg["visible"]?.Type == JTokenType.Boolean ? msg.Value<bool>("visible") : null;
                        Presence.Update(conn, msg.Value<string>("path"), msg.Value<string>("title"), visible, DateTime.UtcNow);
                    }
                }
                catch (JsonException) { /* ignore malformed frames */ }
            }
        }

        private static async Task SendLoopAsync(WebSocket socket, PresenceConnection conn, CancellationToken token)
        {
            try
            {
                while (await conn.Outbox.Reader.WaitToReadAsync(token))
                {
                    while (conn.Outbox.Reader.TryRead(out var json))
                    {
                        await SendAsync(socket, json, token);
                        // A terminal state ends the session: close after delivering it.
                        if (json.Contains("\"session-state\"", StringComparison.Ordinal) && !json.Contains("SessionActive", StringComparison.Ordinal))
                        {
                            try { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Session ended", CancellationToken.None); } catch { }
                            return;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// Belt and braces: pushes are the primary signal, but a tab also re-checks its session
        /// every few seconds so a missed push can never leave a revoked session open.
        /// </summary>
        private async Task SafetyLoopAsync(PresenceConnection conn, string? sessionId, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(SafetyCheckInterval, token);
                    var profile = GetProfileByIDFast(conn.ProfileId);
                    string? state = null;
                    if (profile == null) state = "ProfileNotFound";
                    else if (!profile.CanLogin) state = "ProfileDisabled";
                    else if (sessionId != null)
                    {
                        var session = Sessions.Get(sessionId);
                        if (session == null || session.RevokedUtc != null) state = "SessionRevoked";
                        else if (session.ExpiresUtc <= DateTime.UtcNow) state = "SessionExpired";
                    }
                    if (state != null)
                    {
                        conn.Outbox.Writer.TryWrite(JsonConvert.SerializeObject(new { type = "session-state", state }));
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        private static async Task SendStateAndCloseAsync(WebSocket socket, string state)
        {
            try
            {
                await SendAsync(socket, JsonConvert.SerializeObject(new { type = "session-state", state }), CancellationToken.None);
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, state, CancellationToken.None);
            }
            catch { }
        }

        private static readonly SemaphoreSlim NoGate = new(1, 1);

        private static async Task SendAsync(WebSocket socket, string json, CancellationToken token)
        {
            if (socket.State != WebSocketState.Open) return;
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
        }

        /// <summary>
        /// Follow one profile live: an initial snapshot (presence, sessions), then every presence
        /// change, request and event as it happens.
        /// </summary>
        private async Task HandleAdminLiveAsync(WebSocket socket, NameValueCollection query, KMProfile? viewer)
        {
            var target = GetProfileByIDFast(query["profileId"]);
            if (viewer == null || target == null)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Profile not found", CancellationToken.None); } catch { }
                return;
            }
            string profileId = target.UserID;
            var outbox = System.Threading.Channels.Channel.CreateBounded<string>(new System.Threading.Channels.BoundedChannelOptions(512)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

            void OnPresence(PresenceChange change)
            {
                if (change.ProfileId != profileId) return;
                outbox.Writer.TryWrite(JsonConvert.SerializeObject(new
                {
                    type = "presence",
                    change = change.Kind,
                    presence = Presence.Summarize(profileId, DateTime.UtcNow),
                }, ApiJson));
            }
            void OnRequest(ActivityRequest r)
            {
                if (r.ProfileId != profileId) return;
                outbox.Writer.TryWrite(JsonConvert.SerializeObject(new { type = "request", item = r }, ApiJson));
            }
            void OnEvent(ActivityEvent e)
            {
                if (e.ProfileId != profileId) return;
                outbox.Writer.TryWrite(JsonConvert.SerializeObject(new { type = "event", item = e }, ApiJson));
            }

            Presence.Changed += OnPresence;
            if (Activity != null)
            {
                Activity.RequestRecorded += OnRequest;
                Activity.EventRecorded += OnEvent;
            }
            using var closed = new CancellationTokenSource();
            try
            {
                await SendAsync(socket, JsonConvert.SerializeObject(new
                {
                    type = "snapshot",
                    presence = Presence.Summarize(profileId, DateTime.UtcNow),
                    sessions = Sessions.ForProfile(profileId).Select(s => SessionRow(s, null)).ToList(),
                }, ApiJson), closed.Token);

                var receive = Task.Run(async () =>
                {
                    var buffer = new byte[1024];
                    try
                    {
                        while (socket.State == WebSocketState.Open)
                        {
                            var r = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), closed.Token);
                            if (r.MessageType == WebSocketMessageType.Close) break;
                        }
                    }
                    catch { }
                    finally { closed.Cancel(); }
                });

                while (!closed.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    using var tick = CancellationTokenSource.CreateLinkedTokenSource(closed.Token);
                    tick.CancelAfter(TimeSpan.FromSeconds(20));
                    try
                    {
                        if (await outbox.Reader.WaitToReadAsync(tick.Token))
                        {
                            while (outbox.Reader.TryRead(out var json)) await SendAsync(socket, json, closed.Token);
                        }
                    }
                    catch (OperationCanceledException) when (!closed.IsCancellationRequested)
                    {
                        // Keep-alive: refresh the presence card even when nothing happened.
                        await SendAsync(socket, JsonConvert.SerializeObject(new { type = "presence", change = "tick", presence = Presence.Summarize(profileId, DateTime.UtcNow) }, ApiJson), closed.Token);
                    }
                    // Stop following a profile the viewer may no longer watch.
                    var liveViewer = GetProfileByIDFast(viewer.UserID);
                    if (!AccessEvaluator.Can(liveViewer, ProfilesPerms.ActivityLive)) break;
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            finally
            {
                Presence.Changed -= OnPresence;
                if (Activity != null)
                {
                    Activity.RequestRecorded -= OnRequest;
                    Activity.EventRecorded -= OnEvent;
                }
                try { closed.Cancel(); } catch { }
                try
                {
                    if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                }
                catch { }
            }
        }
    }
}
