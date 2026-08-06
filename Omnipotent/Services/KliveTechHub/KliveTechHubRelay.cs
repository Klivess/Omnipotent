using Newtonsoft.Json.Linq;
using Omnipotent.Profiles;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace Omnipotent.Services.KliveTechHub
{
    public sealed partial class KliveTechHub
    {
        private readonly ConcurrentDictionary<string, RelayHubConnection> relayHubConnections =
            new(StringComparer.OrdinalIgnoreCase);
        private string relayAccessToken = string.Empty;

        internal sealed class RelayHubConnection
        {
            private readonly SemaphoreSlim sendLock = new(1, 1);

            internal RelayHubConnection(string hubId, string hubName, WebSocket socket)
            {
                HubId = hubId;
                HubName = hubName;
                Socket = socket;
                LastMessageReceived = DateTime.UtcNow;
            }

            internal string HubId { get; }
            internal string HubName { get; }
            internal WebSocket Socket { get; }
            internal DateTime LastMessageReceived { get; set; }
            internal ConcurrentDictionary<string, byte> DeviceIds { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            internal bool IsOpen => Socket.State == WebSocketState.Open;

            internal async Task SendCommandAsync(
                string deviceId,
                object payload,
                CancellationToken cancellationToken)
            {
                if (!IsOpen)
                {
                    throw new IOException($"KliveTech hub '{HubName}' is offline.");
                }

                string serialized = KliveTechRelayProtocol.SerializeCommand(HubId, deviceId, payload);
                byte[] bytes = Encoding.UTF8.GetBytes(serialized);
                if (bytes.Length > KliveTechRelayProtocol.MaximumDeviceMessageBytes)
                {
                    throw new InvalidDataException("The relayed KliveTech command is too large.");
                }

                await sendLock.WaitAsync(cancellationToken);
                try
                {
                    if (!IsOpen)
                    {
                        throw new IOException($"KliveTech hub '{HubName}' disconnected.");
                    }
                    await Socket.SendAsync(
                        bytes.AsMemory(),
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        cancellationToken);
                }
                finally
                {
                    sendLock.Release();
                }
            }

            internal async Task CloseAsync(string reason)
            {
                try
                {
                    if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        await Socket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            reason,
                            CancellationToken.None);
                    }
                }
                catch
                {
                    // The transport may already have disappeared.
                }
            }
        }

        private async Task InitializeRelayAccessTokenAsync()
        {
            string generated = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            relayAccessToken = await GetStringOmniSetting(
                "KliveTechHubAccessToken",
                defaultValue: generated,
                sensitive: true,
                askKlivesForFulfillment: false);
            if (string.IsNullOrWhiteSpace(relayAccessToken))
            {
                throw new InvalidOperationException("KliveTechHubAccessToken could not be initialized.");
            }
        }

        internal async Task RegisterRelayRouteAsync()
        {
            await ExecuteServiceMethod<Omnipotent.Services.KliveAPI.KliveAPI>(
                "CreateWebSocketRoute",
                KliveTechRelayProtocol.WebSocketRoute,
                (Func<HttpListenerContext, WebSocket, NameValueCollection, KMProfileManager.KMProfile?, Task>)
                    (async (_, socket, _, _) => await HandleRelayHubConnectionAsync(socket)),
                KMProfileManager.KMPermissions.Anybody);
        }

        private async Task HandleRelayHubConnectionAsync(WebSocket socket)
        {
            RelayHubConnection? connection = null;
            try
            {
                using CancellationTokenSource handshakeDeadline =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken.Token);
                handshakeDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                string? firstMessage = await ReceiveWebSocketTextAsync(socket, handshakeDeadline.Token);
                string helloError = "The hub did not send a hello message.";
                if (firstMessage == null ||
                    !KliveTechRelayProtocol.TryParseHello(
                        firstMessage,
                        out KliveTechRelayProtocol.HelloMessage? hello,
                        out helloError) ||
                    hello == null)
                {
                    await CloseRelaySocketAsync(socket, WebSocketCloseStatus.PolicyViolation, helloError);
                    return;
                }

                if (!KliveTechRelayProtocol.TokensEqual(relayAccessToken, hello.Token))
                {
                    await CloseRelaySocketAsync(socket, WebSocketCloseStatus.PolicyViolation, "Unauthorized");
                    await ServiceLogError("Rejected an unauthorized KliveTech relay hub connection.");
                    return;
                }

                connection = new RelayHubConnection(hello.HubId, hello.HubName, socket);
                if (relayHubConnections.TryGetValue(hello.HubId, out RelayHubConnection? previous))
                {
                    relayHubConnections[hello.HubId] = connection;
                    await previous.CloseAsync("Replaced by a newer hub connection");
                }
                else if (!relayHubConnections.TryAdd(hello.HubId, connection))
                {
                    await CloseRelaySocketAsync(socket, WebSocketCloseStatus.InternalServerError, "Could not register hub");
                    return;
                }

                connection.DeviceIds[hello.HubId] = 0;
                await UpsertRelayedGadgetAsync(
                    connection,
                    hello.HubId,
                    hello.Hub?.Name ?? hello.HubName,
                    isHub: true);
                await ServiceLog($"KliveTech relay hub connected: {hello.HubName} ({hello.HubId}).");

                while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    string? message = await ReceiveWebSocketTextAsync(socket, cancellationToken.Token);
                    if (message == null)
                    {
                        break;
                    }
                    connection.LastMessageReceived = DateTime.UtcNow;
                    await ProcessRelayHubMessageAsync(connection, message);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal service shutdown.
            }
            catch (OperationCanceledException)
            {
                await CloseRelaySocketAsync(socket, WebSocketCloseStatus.PolicyViolation, "Handshake timeout");
            }
            catch (WebSocketException)
            {
                // The hub disconnected without a close frame.
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "KliveTech relay hub connection failed.");
                await CloseRelaySocketAsync(socket, WebSocketCloseStatus.InternalServerError, "Relay failure");
            }
            finally
            {
                if (connection != null &&
                    relayHubConnections.TryGetValue(connection.HubId, out RelayHubConnection? active) &&
                    ReferenceEquals(active, connection))
                {
                    relayHubConnections.TryRemove(connection.HubId, out _);
                    MarkRelayConnectionOffline(
                        connection,
                        new IOException($"KliveTech relay hub '{connection.HubName}' disconnected."));
                    await ServiceLog($"KliveTech relay hub disconnected: {connection.HubName}.");
                }
            }
        }

        private async Task ProcessRelayHubMessageAsync(RelayHubConnection connection, string message)
        {
            if (!KliveTechRelayProtocol.TryParseEnvelope(message, out JObject? envelope) || envelope == null)
            {
                await ServiceLogError($"Ignored invalid relay message from hub {connection.HubName}.");
                return;
            }

            string? suppliedHubId = envelope.Value<string>("HubId");
            if (!string.IsNullOrWhiteSpace(suppliedHubId) &&
                (!KliveTechRelayProtocol.TryNormalizeDeviceId(suppliedHubId, out string normalizedHubId) ||
                 !string.Equals(normalizedHubId, connection.HubId, StringComparison.OrdinalIgnoreCase)))
            {
                await ServiceLogError($"Ignored a relay message with the wrong hub ID from {connection.HubName}.");
                return;
            }

            switch (envelope.Value<string>("Type"))
            {
                case "inventory":
                    await ApplyRelayInventoryAsync(connection, envelope["Gadgets"] as JArray);
                    break;
                case "presence":
                    await ApplyRelayPresenceAsync(connection, envelope);
                    break;
                case "response":
                    await ApplyRelayResponseAsync(connection, envelope);
                    break;
                case "stream":
                    await ApplyRelayStreamAsync(connection, envelope);
                    break;
            }
        }

        private async Task ApplyRelayInventoryAsync(RelayHubConnection connection, JArray? inventory)
        {
            if (inventory == null || inventory.Count > 64)
            {
                return;
            }

            HashSet<string> reported = new(StringComparer.OrdinalIgnoreCase)
            {
                connection.HubId
            };
            foreach (JToken entry in inventory)
            {
                if (!KliveTechRelayProtocol.TryNormalizeDeviceId(
                        entry.Value<string>("DeviceId"),
                        out string deviceId) ||
                    string.Equals(deviceId, connection.HubId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                reported.Add(deviceId);
                connection.DeviceIds[deviceId] = 0;
                await UpsertRelayedGadgetAsync(
                    connection,
                    deviceId,
                    KliveTechRelayProtocol.NormalizeName(
                        entry.Value<string>("Name"),
                        $"KliveTech-{deviceId}"),
                    isHub: false);
            }

            foreach (string previousId in connection.DeviceIds.Keys.ToArray())
            {
                if (reported.Contains(previousId))
                {
                    continue;
                }
                connection.DeviceIds.TryRemove(previousId, out _);
                MarkRelayedGadgetOffline(connection, previousId, "The gadget left its KliveTech hub.");
            }
            UpdateRelayHubCount(connection);
        }

        private async Task ApplyRelayPresenceAsync(RelayHubConnection connection, JObject envelope)
        {
            if (!KliveTechRelayProtocol.TryNormalizeDeviceId(
                    envelope.Value<string>("DeviceId"),
                    out string deviceId) ||
                string.Equals(deviceId, connection.HubId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            bool isConnected = envelope.Value<bool?>("Connected") ?? false;
            if (!isConnected)
            {
                connection.DeviceIds.TryRemove(deviceId, out _);
                MarkRelayedGadgetOffline(connection, deviceId, "The gadget left its KliveTech hub.");
                UpdateRelayHubCount(connection);
                return;
            }

            connection.DeviceIds[deviceId] = 0;
            await UpsertRelayedGadgetAsync(
                connection,
                deviceId,
                KliveTechRelayProtocol.NormalizeName(
                    envelope.Value<string>("Name"),
                    $"KliveTech-{deviceId}"),
                isHub: false);
            UpdateRelayHubCount(connection);
        }

        private async Task ApplyRelayResponseAsync(RelayHubConnection connection, JObject envelope)
        {
            if (!KliveTechRelayProtocol.TryNormalizeDeviceId(
                    envelope.Value<string>("DeviceId"),
                    out string deviceId) ||
                envelope["Payload"] is not JObject payload ||
                payload.Value<int?>("ID") is not int responseId || responseId <= 0 ||
                (envelope.Value<int?>("RequestId") is int envelopeId && envelopeId != responseId))
            {
                return;
            }

            if (!gadgetsByAddress.TryGetValue(deviceId, out KliveTechGadget? gadget) ||
                !ReferenceEquals(gadget.RelayConnection, connection))
            {
                return;
            }
            await ProcessReceivedFrame(gadget, payload.ToString(Newtonsoft.Json.Formatting.None));
        }

        private async Task ApplyRelayStreamAsync(RelayHubConnection connection, JObject envelope)
        {
            if (!KliveTechRelayProtocol.TryGetStreamPayload(
                    envelope,
                    out string deviceId,
                    out JObject? payload) || payload == null)
            {
                return;
            }

            if (!connection.DeviceIds.ContainsKey(deviceId) ||
                !gadgetsByAddress.TryGetValue(deviceId, out KliveTechGadget? gadget) ||
                !ReferenceEquals(gadget.RelayConnection, connection))
            {
                return;
            }
            await ProcessReceivedFrame(gadget, payload.ToString(Newtonsoft.Json.Formatting.None));
        }

        private async Task<KliveTechGadget> UpsertRelayedGadgetAsync(
            RelayHubConnection connection,
            string deviceId,
            string gadgetName,
            bool isHub)
        {
            if (gadgetsByAddress.TryGetValue(deviceId, out KliveTechGadget? current) &&
                current.isOnline && ReferenceEquals(current.RelayConnection, connection))
            {
                current.name = KliveTechRelayProtocol.NormalizeName(
                    gadgetName,
                    $"KliveTech-{deviceId}");
                if (isHub)
                {
                    current.connectedGadgetCount = Math.Max(0, connection.DeviceIds.Count - 1);
                }
                return current;
            }

            if (gadgetsByAddress.TryGetValue(deviceId, out KliveTechGadget? existing) &&
                existing.isOnline && existing.RelayConnection == null)
            {
                // A local Bluetooth path is already active and has lower latency. The relay
                // remains inventoried and will take over if that direct path disappears.
                return existing;
            }

            existing?.DisconnectDevice();
            rememberedDevices.TryGetValue(deviceId, out RememberedGadget? remembered);
            KliveTechGadget gadget = existing ?? new KliveTechGadget();
            gadget.name = KliveTechRelayProtocol.NormalizeName(gadgetName, $"KliveTech-{deviceId}");
            gadget.IPAddress = deviceId;
            gadget.IPAddressLong = Convert.ToInt64(deviceId, 16);
            gadget.gadgetID = string.IsNullOrWhiteSpace(existing?.gadgetID)
                ? string.IsNullOrWhiteSpace(remembered?.gadgetID)
                    ? Guid.NewGuid().ToString("N")
                    : remembered.gadgetID
                : existing.gadgetID;
            gadget.isOnline = true;
            gadget.timeConnected = DateTime.UtcNow;
            gadget.lastMessageReceived = DateTime.MinValue;
            gadget.connectionType = isHub ? "Hub" : "Relayed";
            gadget.isHub = isHub;
            gadget.hubID = isHub ? connection.HubId : string.Empty;
            gadget.connectedViaHubID = isHub ? string.Empty : connection.HubId;
            gadget.connectedGadgetCount = isHub
                ? Math.Max(0, connection.DeviceIds.Count - 1)
                : 0;
            gadget.RelayDeviceID = deviceId;
            gadget.RelayConnection = connection;
            gadget.ConnectionCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken.Token);
            gadgetsByAddress[deviceId] = gadget;

            try
            {
                await RememberKliveTechDevice(gadget);
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, $"Could not remember relayed gadget {gadget.name}.");
            }

            CancellationToken connectionToken = gadget.ConnectionCancellation.Token;
            gadget.ActionDiscoveryTask = Task.Run(
                () => GetGadgetActions(gadget, connectionToken),
                connectionToken);
            gadget.StreamDiscoveryTask = Task.Run(
                () => RefreshStreamablesAsync(gadget, connectionToken),
                connectionToken);
            gadget.MonitorTask = Task.Run(async () =>
            {
                try
                {
                    if (await gadget.ActionDiscoveryTask && gadget.isOnline)
                    {
                        await CheckConnectionStatusOfGadget(gadget, connectionToken);
                    }
                }
                catch (OperationCanceledException) when (connectionToken.IsCancellationRequested)
                {
                    // Normal disconnect.
                }
                catch (Exception ex)
                {
                    MarkGadgetOffline(gadget, ex);
                    await ServiceLogError(ex, $"Relay monitor failed for {gadget.name}.");
                }
            }, connectionToken);

            await ServiceLog(
                isHub
                    ? $"Registered KliveTech hub gadget: {gadget.name}."
                    : $"Connected to KliveTech gadget {gadget.name} through {connection.HubName}.");
            _ = NotifyGadgetConnected(gadget.name);
            return gadget;
        }

        private void UpdateRelayHubCount(RelayHubConnection connection)
        {
            if (gadgetsByAddress.TryGetValue(connection.HubId, out KliveTechGadget? hub) &&
                ReferenceEquals(hub.RelayConnection, connection))
            {
                hub.connectedGadgetCount = Math.Max(0, connection.DeviceIds.Count - 1);
                hub.lastMessageReceived = DateTime.UtcNow;
            }
        }

        private void MarkRelayedGadgetOffline(
            RelayHubConnection connection,
            string deviceId,
            string reason)
        {
            if (gadgetsByAddress.TryGetValue(deviceId, out KliveTechGadget? gadget) &&
                ReferenceEquals(gadget.RelayConnection, connection))
            {
                MarkGadgetOffline(gadget, new IOException(reason));
            }
        }

        private void MarkRelayConnectionOffline(RelayHubConnection connection, Exception reason)
        {
            foreach (KliveTechGadget gadget in gadgetsByAddress.Values)
            {
                if (ReferenceEquals(gadget.RelayConnection, connection))
                {
                    MarkGadgetOffline(gadget, reason);
                }
            }
        }

        private void StopRelayConnections()
        {
            foreach ((string _, RelayHubConnection connection) in relayHubConnections.ToArray())
            {
                _ = connection.CloseAsync("KliveTech service stopping");
                MarkRelayConnectionOffline(connection, new OperationCanceledException("KliveTech service stopping."));
            }
            relayHubConnections.Clear();
        }

        private static async Task<string?> ReceiveWebSocketTextAsync(
            WebSocket socket,
            CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[4096];
            using MemoryStream message = new();
            while (true)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new InvalidDataException("KliveTech hubs must send text messages.");
                }
                if (message.Length + result.Count > KliveTechRelayProtocol.MaximumMessageBytes)
                {
                    throw new InvalidDataException("KliveTech hub message exceeded the size limit.");
                }
                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
                }
            }
        }

        private static async Task CloseRelaySocketAsync(
            WebSocket socket,
            WebSocketCloseStatus status,
            string? reason)
        {
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    string safeReason = string.IsNullOrWhiteSpace(reason) ? "Invalid relay connection" : reason;
                    await socket.CloseAsync(status, safeReason[..Math.Min(safeReason.Length, 120)], CancellationToken.None);
                }
            }
            catch
            {
                // The peer may already be gone.
            }
        }
    }
}
