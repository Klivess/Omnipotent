using Newtonsoft.Json.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Websocket.Client;

namespace Omnipotent.Services.Omniscience.Ingest.Discord
{
    /// <summary>
    /// Maintains a Discord Gateway websocket for a single user token. Sends IDENTIFY
    /// with the same super-properties the official client advertises, handles the
    /// heartbeat handshake, and emits raw event JSON via <see cref="OnDispatch"/>.
    /// </summary>
    public class DiscordGateway : IAsyncDisposable
    {
        private const string GatewayUrl = "wss://gateway.discord.gg/?v=9&encoding=json";
        private readonly string token;
        private WebsocketClient? socket;
        private CancellationTokenSource? cts;
        private Task? heartbeatTask;
        private int heartbeatIntervalMs = 41250;
        private int? lastSequence;
        private string? sessionId;

        public event Action<string, JObject>? OnDispatch;
        public event Action<Exception>? OnError;

        public DiscordGateway(string token) { this.token = token; }

        public async Task StartAsync(CancellationToken ct)
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            socket = new WebsocketClient(new Uri(GatewayUrl))
            {
                ReconnectTimeout = TimeSpan.FromMinutes(2),
                ErrorReconnectTimeout = TimeSpan.FromSeconds(5),
            };
            socket.MessageReceived.Subscribe(OnSocketMessage);
            socket.DisconnectionHappened.Subscribe(d =>
            {
                if (d.Exception != null) OnError?.Invoke(d.Exception);
                lastSequence = null;
                sessionId = null;
            });
            await socket.Start();
        }

        public async Task StopAsync()
        {
            try { cts?.Cancel(); } catch { }
            if (socket != null)
            {
                try { await socket.Stop(WebSocketCloseStatus.NormalClosure, "shutdown"); } catch { }
                socket.Dispose();
                socket = null;
            }
        }

        public ValueTask DisposeAsync() => new(StopAsync());

        private void OnSocketMessage(ResponseMessage msg)
        {
            if (msg.Text == null) return;
            JObject root;
            try { root = JObject.Parse(msg.Text); } catch { return; }

            int op = root.Value<int?>("op") ?? -1;
            int? s = root.Value<int?>("s");
            if (s.HasValue) lastSequence = s;

            switch (op)
            {
                case 10: // HELLO
                    heartbeatIntervalMs = root["d"]?.Value<int?>("heartbeat_interval") ?? 41250;
                    StartHeartbeat();
                    SendIdentify();
                    break;
                case 11: // HEARTBEAT_ACK
                    break;
                case 0: // DISPATCH
                    string t = root.Value<string>("t") ?? "";
                    if (t == "READY") sessionId = root["d"]?.Value<string>("session_id");
                    var d = root["d"] as JObject;
                    if (d != null) OnDispatch?.Invoke(t, d);
                    break;
                case 7: // RECONNECT
                case 9: // INVALID_SESSION
                    sessionId = null;
                    lastSequence = null;
                    break;
            }
        }

        private void StartHeartbeat()
        {
            heartbeatTask?.Dispose();
            heartbeatTask = Task.Run(async () =>
            {
                while (cts != null && !cts.IsCancellationRequested && socket != null && socket.IsRunning)
                {
                    try
                    {
                        var hb = new JObject(
                            new JProperty("op", 1),
                            new JProperty("d", lastSequence.HasValue ? (JToken)lastSequence.Value : JValue.CreateNull()));
                        socket.Send(hb.ToString(Newtonsoft.Json.Formatting.None));
                    }
                    catch (Exception ex) { OnError?.Invoke(ex); }
                    try { await Task.Delay(heartbeatIntervalMs, cts.Token); } catch { return; }
                }
            });
        }

        private void SendIdentify()
        {
            // Decode super-properties so we can attach them as a JSON object inside IDENTIFY.
            byte[] propsBytes = Convert.FromBase64String(DiscordRestClient.SuperProperties);
            JObject props = JObject.Parse(Encoding.UTF8.GetString(propsBytes));

            var identify = new JObject(
                new JProperty("op", 2),
                new JProperty("d", new JObject(
                    new JProperty("token", token),
                    new JProperty("capabilities", 16381),
                    new JProperty("properties", props),
                    new JProperty("presence", new JObject(
                        new JProperty("status", "online"),
                        new JProperty("since", 0),
                        new JProperty("activities", new JArray()),
                        new JProperty("afk", false))),
                    new JProperty("compress", false),
                    new JProperty("client_state", new JObject(
                        new JProperty("guild_versions", new JObject()),
                        new JProperty("highest_last_message_id", "0"),
                        new JProperty("read_state_version", 0),
                        new JProperty("user_guild_settings_version", -1),
                        new JProperty("private_channels_version", "0"),
                        new JProperty("api_code_version", 0)))
                )));
            socket?.Send(identify.ToString(Newtonsoft.Json.Formatting.None));
        }
    }
}
