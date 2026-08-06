using InTheHand.Net;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace Omnipotent.Services.KliveTechHub
{
    public sealed partial class KliveTechHub : OmniService
    {
        private const int MaximumConnectionAttempts = 5;
        private const int MaximumActionDiscoveryAttempts = 10;
        private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ConnectionAttemptTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(5);

        private readonly ConcurrentDictionary<string, KliveTechGadget> gadgetsByAddress =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<int, PendingRequest> pendingRequests = new();
        private readonly ConcurrentDictionary<string, RememberedGadget> rememberedDevices =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> connectionsInProgress =
            new(StringComparer.OrdinalIgnoreCase);

        private BluetoothClient? discoveryClient;
        private Channel<OutboundCommand>? sendQueue;
        private KliveTechRoutes? routes;
        private Task? sendLoopTask;
        private Task? discoveryLoopTask;
        private int nextMessageId;
        private bool allowNewDevices;

        public KliveTechHub()
        {
            name = "KliveTech Hub";
            threadAnteriority = ThreadAnteriority.High;
        }

        public IReadOnlyList<KliveTechGadget> connectedGadgets => gadgetsByAddress.Values
            .OrderBy(gadget => gadget.name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        public sealed class KliveTechGadget
        {
            public string name = string.Empty;
            public string IPAddress = string.Empty;
            public long IPAddressLong;
            public string gadgetID = Guid.NewGuid().ToString("N");
            public List<KliveTechActions.KliveTechAction> actions = new();
            public DateTime timeConnected;
            public bool isOnline;
            public DateTime lastMessageReceived;
            public string connectionType = "Bluetooth";
            public bool isHub;
            public string hubID = string.Empty;
            public string connectedViaHubID = string.Empty;
            public int connectedGadgetCount;
            public int streamableCount;

            [JsonIgnore]
            public BluetoothDeviceInfo? deviceInfo;

            [JsonIgnore]
            internal BluetoothClient? ConnectedClient;

            [JsonIgnore]
            internal NetworkStream? Stream;

            [JsonIgnore]
            internal CancellationTokenSource? ConnectionCancellation;

            [JsonIgnore]
            internal Task<bool>? ReceiveTask;

            [JsonIgnore]
            internal Task<bool>? ActionDiscoveryTask;

            [JsonIgnore]
            internal Task? StreamDiscoveryTask;

            [JsonIgnore]
            internal Task? MonitorTask;

            [JsonIgnore]
            internal RelayHubConnection? RelayConnection { get; set; }

            [JsonIgnore]
            internal string RelayDeviceID { get; set; } = string.Empty;

            public void DisconnectDevice()
            {
                isOnline = false;
                try { ConnectionCancellation?.Cancel(); } catch { }
                try { Stream?.Close(); } catch { }
                try { ConnectedClient?.Close(); } catch { }
            }
        }

        private sealed class OutboundCommand
        {
            public required KliveTechGadget Gadget { get; init; }
            public required KliveTechGadgetMessage Message { get; init; }
            public required CancellationToken CancellationToken { get; init; }
            public TaskCompletionSource<bool> Sent { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class PendingRequest
        {
            public required KliveTechGadget Gadget { get; init; }
            public TaskCompletionSource<KliveTechGadgetResponse> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class KliveTechGadgetMessage
        {
            public int ID { get; init; }
            public string DATA { get; init; } = string.Empty;
            public KliveTechActions.OperationNumber OP { get; init; }
            public bool RESPEXPECT { get; init; }
        }

        private sealed class KliveTechGadgetResponse
        {
            public int ID { get; set; }
            public JToken? DATA { get; set; }
            public bool RESPEXPECT { get; set; }
            public int STATUS { get; set; }

            public static KliveTechGadgetResponse SentWithoutResponse(int id)
            {
                return new KliveTechGadgetResponse
                {
                    ID = id,
                    STATUS = (int)HttpStatusCode.OK
                };
            }
        }

        private sealed class ActionListPayload
        {
            public List<ActionDescriptor> Actions { get; set; } = new();
        }

        private sealed class ActionDescriptor
        {
            public string? Name { get; set; }
            public string? ParamDescription { get; set; }
            public int Type { get; set; }
        }

        private sealed class RememberedGadget
        {
            public string name { get; set; } = string.Empty;
            public string IPAddress { get; set; } = string.Empty;
            public long IPAddressLong { get; set; }
            public string gadgetID { get; set; } = string.Empty;
            public string connectionType { get; set; } = "Bluetooth";
            public bool isHub { get; set; }
            public string hubID { get; set; } = string.Empty;
            public string connectedViaHubID { get; set; } = string.Empty;
        }

        public static bool CheckIfBluetoothProtocolExistsOnDevice()
        {
            try
            {
                return BluetoothRadio.IsSupported;
            }
            catch
            {
                return false;
            }
        }

        protected override async void ServiceMain()
        {
            try
            {
                bool bluetoothAvailable = CheckIfBluetoothProtocolExistsOnDevice();
                ResetRuntimeState(bluetoothAvailable);
                ServiceQuitRequest += StopRuntimeState;

                allowNewDevices = await GetBoolOmniSetting(
                    "KliveTechAllowNewDevices",
                    defaultValue: false,
                    askKlivesForFulfillment: false);
                await InitializeRelayAccessTokenAsync();
                await InitializeFirmwareManagerAsync();

                CancellationToken serviceToken = cancellationToken.Token;
                sendLoopTask = Task.Run(() => SendDataLoopAsync(serviceToken), serviceToken);
                StartStreamableRuntime(serviceToken);

                routes = new KliveTechRoutes(this);
                await routes.RegisterRoutes();

                if (bluetoothAvailable)
                {
                    await ReconnectToRememberedDevices(serviceToken);
                    discoveryLoopTask = Task.Run(
                        () => DiscoverNewKliveTechGadgets(serviceToken),
                        serviceToken);
                }
                else
                {
                    await ServiceLog(
                        "No Bluetooth adapter is available; KliveTech is running in Internet relay-only mode.");
                }

                await ServiceLog(
                    allowNewDevices
                        ? "KliveTech Hub started; enrollment of new KliveTech devices is enabled."
                        : "KliveTech Hub started in remembered-device-only mode. Enable " +
                          "KliveTechAllowNewDevices to enroll a new gadget.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal service shutdown.
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "KliveTech Hub failed during startup.");
                _ = TerminateService();
            }
        }

        private void ResetRuntimeState(bool bluetoothAvailable)
        {
            StopRuntimeState();
            gadgetsByAddress.Clear();
            rememberedDevices.Clear();
            connectionsInProgress.Clear();
            pendingRequests.Clear();
            ResetStreamableRuntime();
            sendQueue = Channel.CreateUnbounded<OutboundCommand>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            discoveryClient = bluetoothAvailable ? new BluetoothClient() : null;
        }

        private void StopRuntimeState()
        {
            sendQueue?.Writer.TryComplete();
            StopRelayConnections();
            StopFirmwareJobs();
            StopStreamableRuntime();

            foreach ((int id, PendingRequest pending) in pendingRequests.ToArray())
            {
                if (pendingRequests.TryRemove(id, out PendingRequest? removed))
                {
                    removed.Completion.TrySetCanceled();
                }
            }

            foreach (KliveTechGadget gadget in gadgetsByAddress.Values)
            {
                gadget.DisconnectDevice();
            }

            try { discoveryClient?.Close(); } catch { }
        }

        public Task RememberKliveTechDevice(KliveTechGadget gadget)
        {
            ArgumentNullException.ThrowIfNull(gadget);
            string directory = Path.GetFullPath(
                OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveTechHubGadgetsDirectory));
            Directory.CreateDirectory(directory);

            string normalizedAddress = NormalizeAddress(gadget.IPAddress);
            if (string.IsNullOrWhiteSpace(normalizedAddress))
            {
                throw new InvalidDataException("The gadget has no valid Bluetooth address.");
            }

            string path = Path.GetFullPath(Path.Combine(
                directory,
                $"{normalizedAddress}.kliveTechGadget"));
            EnsurePathIsWithinDirectory(path, directory);

            RememberedGadget persisted = new()
            {
                name = gadget.name,
                IPAddress = gadget.IPAddress,
                IPAddressLong = gadget.IPAddressLong,
                gadgetID = gadget.gadgetID,
                connectionType = gadget.connectionType,
                isHub = gadget.isHub,
                hubID = gadget.hubID,
                connectedViaHubID = gadget.connectedViaHubID
            };

            rememberedDevices[normalizedAddress] = persisted;
            return GetDataHandler().WriteToFile(path, JsonConvert.SerializeObject(persisted));
        }

        public Task ReconnectToRememberedDevices()
        {
            return ReconnectToRememberedDevices(cancellationToken.Token);
        }

        private async Task ReconnectToRememberedDevices(CancellationToken serviceToken)
        {
            await ServiceLog("Reconnecting to remembered KliveTech gadgets.");
            string directory = Path.GetFullPath(
                OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveTechHubGadgetsDirectory));
            Directory.CreateDirectory(directory);

            foreach (string file in Directory.EnumerateFiles(directory, "*.kliveTechGadget"))
            {
                serviceToken.ThrowIfCancellationRequested();
                try
                {
                    string json = await GetDataHandler().ReadDataFromFile(file);
                    RememberedGadget? remembered = JsonConvert.DeserializeObject<RememberedGadget>(json);
                    if (remembered == null || remembered.IPAddressLong <= 0)
                    {
                        await ServiceLogError($"Ignoring invalid remembered KliveTech file: {file}");
                        continue;
                    }

                    BluetoothAddress address = new(remembered.IPAddressLong);
                    string normalizedAddress = NormalizeAddress(address.ToString());
                    if (!rememberedDevices.TryAdd(normalizedAddress, remembered))
                    {
                        continue;
                    }

                    if (string.Equals(
                            remembered.connectionType,
                            "Relayed",
                            StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(
                            remembered.connectionType,
                            "Hub",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        // A relayed device reconnects when its hub publishes inventory. It is
                        // still remembered here so Bluetooth discovery can take over as fallback.
                        continue;
                    }

                    await TryConnectToDeviceInternal(
                        new BluetoothDeviceInfo(address),
                        remembered,
                        startingAttempt: 0,
                        serviceToken);
                }
                catch (OperationCanceledException) when (serviceToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    await ServiceLogError(ex, $"Could not reconnect remembered KliveTech file '{file}'.");
                }
            }

            await ServiceLog(
                $"Finished reconnecting to remembered KliveTech gadgets; " +
                $"{gadgetsByAddress.Count} gadget(s) are known in memory.");
        }

        private async Task DiscoverNewKliveTechGadgets(CancellationToken serviceToken)
        {
            while (!serviceToken.IsCancellationRequested)
            {
                try
                {
                    BluetoothClient activeDiscoveryClient = discoveryClient
                        ?? throw new InvalidOperationException(
                            "Bluetooth discovery was started without an adapter.");
                    BluetoothDeviceInfo[] devices = await Task.Run(
                            () => activeDiscoveryClient.DiscoverDevices(),
                            CancellationToken.None)
                        .WaitAsync(DiscoveryTimeout, serviceToken);

                    foreach (BluetoothDeviceInfo device in devices)
                    {
                        try
                        {
                            serviceToken.ThrowIfCancellationRequested();
                            string deviceName = device.DeviceName ?? string.Empty;
                            if (!deviceName.StartsWith("klivetech", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            string address = NormalizeAddress(device.DeviceAddress.ToString());
                            bool isRemembered = rememberedDevices.TryGetValue(
                                address,
                                out RememberedGadget? remembered);
                            if (!isRemembered && !allowNewDevices)
                            {
                                continue;
                            }

                            if (gadgetsByAddress.TryGetValue(address, out KliveTechGadget? existing) &&
                                existing.isOnline)
                            {
                                continue;
                            }

                            await TryConnectToDeviceInternal(
                                device,
                                remembered,
                                startingAttempt: 0,
                                serviceToken);
                        }
                        catch (OperationCanceledException) when (serviceToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            await ServiceLogError(
                                ex,
                                $"Could not inspect or connect Bluetooth device at " +
                                $"'{device.DeviceAddress}'.");
                        }
                    }
                }
                catch (OperationCanceledException) when (serviceToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    await ServiceLogError(ex, "KliveTech Bluetooth discovery failed; retrying.");
                    try { discoveryClient?.Close(); } catch { }
                    discoveryClient = new BluetoothClient();
                }

                try
                {
                    await Task.Delay(DiscoveryInterval, serviceToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        public Task TryConnectToDevice(BluetoothDeviceInfo device, int attempts = 0)
        {
            ArgumentNullException.ThrowIfNull(device);
            string address = NormalizeAddress(device.DeviceAddress.ToString());
            if (!allowNewDevices && !rememberedDevices.ContainsKey(address))
            {
                return Task.CompletedTask;
            }

            return TryConnectToDeviceInternal(
                device,
                remembered: null,
                startingAttempt: Math.Max(0, attempts),
                cancellationToken.Token);
        }

        private async Task<bool> TryConnectToDeviceInternal(
            BluetoothDeviceInfo device,
            RememberedGadget? remembered,
            int startingAttempt,
            CancellationToken serviceToken)
        {
            string address = NormalizeAddress(device.DeviceAddress.ToString());
            if (string.IsNullOrWhiteSpace(address) || !connectionsInProgress.TryAdd(address, 0))
            {
                return false;
            }

            try
            {
                if (gadgetsByAddress.TryGetValue(address, out KliveTechGadget? current) && current.isOnline)
                {
                    return true;
                }

                if (current != null)
                {
                    current.DisconnectDevice();
                    gadgetsByAddress.TryRemove(address, out _);
                }

                for (int attempt = startingAttempt; attempt < MaximumConnectionAttempts; attempt++)
                {
                    serviceToken.ThrowIfCancellationRequested();
                    BluetoothClient connectedClient = new();
                    bool connected = false;
                    try
                    {
                        await Task.Run(
                                () => connectedClient.Connect(
                                    device.DeviceAddress,
                                    BluetoothService.SerialPort),
                                CancellationToken.None)
                            .WaitAsync(ConnectionAttemptTimeout, serviceToken);

                        device.Refresh();
                        await Task.Delay(250, serviceToken);
                        string deviceName = device.DeviceName ?? remembered?.name ?? string.Empty;

                        if (remembered == null &&
                            !deviceName.StartsWith("klivetech", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException(
                                "The Bluetooth device no longer advertises a KliveTech name.");
                        }

                        KliveTechGadget gadget = new()
                        {
                            name = string.IsNullOrWhiteSpace(deviceName) ? $"KliveTech-{address}" : deviceName,
                            IPAddress = device.DeviceAddress.ToString(),
                            IPAddressLong = device.DeviceAddress.ToInt64(),
                            gadgetID = string.IsNullOrWhiteSpace(remembered?.gadgetID)
                                ? Guid.NewGuid().ToString("N")
                                : remembered.gadgetID,
                            deviceInfo = device,
                            ConnectedClient = connectedClient,
                            Stream = connectedClient.GetStream(),
                            isOnline = true,
                            timeConnected = DateTime.UtcNow,
                            lastMessageReceived = DateTime.MinValue,
                            ConnectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(serviceToken)
                        };

                        connected = true;
                        gadgetsByAddress[address] = gadget;
                        try
                        {
                            await RememberKliveTechDevice(gadget);
                        }
                        catch (Exception ex)
                        {
                            // A persistence failure must not strand an otherwise valid live connection.
                            await ServiceLogError(
                                ex,
                                $"Connected to {gadget.name}, but could not remember the device.");
                        }

                        CancellationToken connectionToken = gadget.ConnectionCancellation.Token;
                        gadget.ReceiveTask = Task.Run(
                            () => ReadDataLoop(gadget, connectionToken),
                            connectionToken);
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
                                bool initialized = await gadget.ActionDiscoveryTask!;
                                if (initialized && gadget.isOnline)
                                {
                                    await CheckConnectionStatusOfGadget(gadget, connectionToken);
                                }
                            }
                            catch (OperationCanceledException) when (connectionToken.IsCancellationRequested)
                            {
                                // Normal disconnect or service shutdown.
                            }
                            catch (Exception ex)
                            {
                                MarkGadgetOffline(gadget, ex);
                                await ServiceLogError(ex, $"Monitor failed for {gadget.name}.");
                            }
                        }, connectionToken);

                        await ServiceLog($"Connected to KliveTech gadget: {gadget.name}.");
                        _ = NotifyGadgetConnected(gadget.name);
                        return true;
                    }
                    catch (OperationCanceledException) when (serviceToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        await ServiceLogError(
                            ex,
                            $"Could not connect to KliveTech device at '{device.DeviceAddress}' " +
                            $"(attempt {attempt + 1}/{MaximumConnectionAttempts}).");
                        if (attempt + 1 < MaximumConnectionAttempts)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(attempt + 1), serviceToken);
                        }
                    }
                    finally
                    {
                        if (!connected)
                        {
                            try { connectedClient.Close(); } catch { }
                        }
                    }
                }

                return false;
            }
            finally
            {
                connectionsInProgress.TryRemove(address, out _);
            }
        }

        private async Task NotifyGadgetConnected(string gadgetName)
        {
            try
            {
                await ExecuteServiceMethod<KliveBot_Discord.KliveBotDiscord>(
                    "SendMessageToKlives",
                    $"Found KliveTech gadget: {gadgetName}");
            }
            catch
            {
                // Gadget connectivity must not depend on Discord availability.
            }
        }

        private async Task<bool> GetGadgetActions(
            KliveTechGadget gadget,
            CancellationToken connectionToken)
        {
            for (int attempt = 0; attempt < MaximumActionDiscoveryAttempts; attempt++)
            {
                try
                {
                    KliveTechGadgetResponse response = await SendData(
                        gadget,
                        KliveTechActions.OperationNumber.GetActions,
                        "GetActions",
                        expectResponse: true,
                        ResponseTimeout,
                        connectionToken);

                    if (response.STATUS != (int)HttpStatusCode.OK)
                    {
                        throw new InvalidDataException(
                            $"Gadget returned status {response.STATUS} while listing actions.");
                    }

                    ActionListPayload? payload = response.DATA?.ToObject<ActionListPayload>();
                    if (payload == null)
                    {
                        throw new InvalidDataException("Gadget returned no action list.");
                    }

                    List<KliveTechActions.KliveTechAction> actions = new();
                    foreach (ActionDescriptor descriptor in payload.Actions)
                    {
                        if (string.IsNullOrWhiteSpace(descriptor.Name) ||
                            !Enum.IsDefined(typeof(KliveTechActions.ActionParameterType), descriptor.Type))
                        {
                            continue;
                        }

                        actions.Add(new KliveTechActions.KliveTechAction
                        {
                            name = descriptor.Name,
                            paramDescription = descriptor.ParamDescription ?? string.Empty,
                            parameters = (KliveTechActions.ActionParameterType)descriptor.Type
                        });
                    }

                    gadget.actions = actions;
                    await ServiceLog($"{actions.Count} action(s) loaded from {gadget.name}.");
                    return true;
                }
                catch (OperationCanceledException) when (connectionToken.IsCancellationRequested)
                {
                    return false;
                }
                catch (Exception ex)
                {
                    await ServiceLogError(
                        ex,
                        $"Could not retrieve actions from {gadget.name} " +
                        $"(attempt {attempt + 1}/{MaximumActionDiscoveryAttempts}).");
                    if (attempt + 1 < MaximumActionDiscoveryAttempts)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), connectionToken);
                    }
                }
            }

            MarkGadgetOffline(gadget, new TimeoutException("Action discovery exhausted its retries."));
            return false;
        }

        public Task CheckConnectionStatusOfGadget(KliveTechGadget gadget, int attempts = 0)
        {
            return CheckConnectionStatusOfGadget(gadget, cancellationToken.Token);
        }

        private async Task CheckConnectionStatusOfGadget(
            KliveTechGadget gadget,
            CancellationToken connectionToken)
        {
            try
            {
                while (gadget.isOnline && !connectionToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), connectionToken);
                    if (!await IsDeviceConnected(gadget, connectionToken))
                    {
                        MarkGadgetOffline(
                            gadget,
                            new IOException($"KliveTech gadget '{gadget.name}' stopped responding."));
                        await ServiceLog($"KliveTech gadget {gadget.name} disconnected.");
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (connectionToken.IsCancellationRequested)
            {
                // Normal disconnect or service shutdown.
            }
            catch (Exception ex)
            {
                MarkGadgetOffline(gadget, ex);
                await ServiceLogError(ex, $"Connection monitor failed for {gadget.name}.");
            }
        }

        private async Task<bool> ReadDataLoop(
            KliveTechGadget gadget,
            CancellationToken connectionToken)
        {
            NetworkStream stream = gadget.Stream
                ?? throw new InvalidOperationException("The gadget has no Bluetooth stream.");
            byte[] receiveBuffer = new byte[4096];
            char[] characterBuffer = new char[Encoding.UTF8.GetMaxCharCount(receiveBuffer.Length)];
            Decoder decoder = Encoding.UTF8.GetDecoder();
            StringBuilder bufferedText = new();

            try
            {
                while (gadget.isOnline && !connectionToken.IsCancellationRequested)
                {
                    int bytesRead = await stream.ReadAsync(receiveBuffer.AsMemory(), connectionToken);
                    if (bytesRead == 0)
                    {
                        throw new EndOfStreamException("The Bluetooth stream closed.");
                    }

                    int characters = decoder.GetChars(
                        receiveBuffer,
                        0,
                        bytesRead,
                        characterBuffer,
                        0,
                        flush: false);
                    bufferedText.Append(characterBuffer, 0, characters);

                    foreach (string frame in KliveTechProtocol.ExtractCompleteFrames(bufferedText))
                    {
                        await ProcessReceivedFrame(gadget, frame);
                    }
                }

                return true;
            }
            catch (OperationCanceledException) when (connectionToken.IsCancellationRequested)
            {
                return true;
            }
            catch (Exception ex)
            {
                MarkGadgetOffline(gadget, ex);
                await ServiceLogError(ex, $"Receive loop failed for {gadget.name}.");
                return false;
            }
        }

        private async Task ProcessReceivedFrame(KliveTechGadget gadget, string frame)
        {
            JObject message;
            try
            {
                message = JObject.Parse(frame, new JsonLoadSettings
                {
                    CommentHandling = CommentHandling.Ignore,
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                    LineInfoHandling = LineInfoHandling.Ignore
                });
            }
            catch (JsonException ex)
            {
                await ServiceLogError(ex, $"Received invalid KliveTech JSON from {gadget.name}.");
                return;
            }

            if (KliveTechStreamProtocol.IsStreamEvent(message))
            {
                string connectionKey = NormalizeAddress(gadget.IPAddress);
                if (!gadgetsByAddress.TryGetValue(connectionKey, out KliveTechGadget? active) ||
                    !ReferenceEquals(active, gadget))
                {
                    return;
                }
                string streamError = string.Empty;
                if (Encoding.UTF8.GetByteCount(frame) > KliveTechStreamProtocol.MaximumEventBytes ||
                    !TryAcceptStreamEvent(gadget, message, out streamError))
                {
                    if (ShouldLogInvalidStreamEvent(gadget.gadgetID))
                    {
                        await ServiceLogError(
                            $"Ignored invalid Streamables event from {gadget.name}: " +
                            (string.IsNullOrWhiteSpace(streamError) ? "event exceeded its size limit" : streamError));
                    }
                    return;
                }
                gadget.lastMessageReceived = DateTime.UtcNow;
                return;
            }

            KliveTechGadgetResponse? response;
            try
            {
                response = message.ToObject<KliveTechGadgetResponse>();
            }
            catch (JsonException ex)
            {
                await ServiceLogError(ex, $"Received invalid KliveTech response from {gadget.name}.");
                return;
            }

            if (response == null || response.ID <= 0)
            {
                await ServiceLogError($"Received an invalid KliveTech response from {gadget.name}.");
                return;
            }

            gadget.lastMessageReceived = DateTime.UtcNow;
            await ServiceLog($"Received response {response.ID} from {gadget.name}.", false);

            if (pendingRequests.TryRemove(response.ID, out PendingRequest? pending))
            {
                if (!ReferenceEquals(pending.Gadget, gadget))
                {
                    pending.Completion.TrySetException(
                        new InvalidDataException("A response came from the wrong KliveTech gadget."));
                    return;
                }

                pending.Completion.TrySetResult(response);
            }
        }

        private async Task<KliveTechGadgetResponse> SendData(
            KliveTechGadget gadget,
            KliveTechActions.OperationNumber operation,
            string data,
            bool expectResponse,
            TimeSpan timeout,
            CancellationToken callerToken)
        {
            if (!gadget.isOnline)
            {
                throw new IOException($"KliveTech gadget '{gadget.name}' is offline.");
            }

            Channel<OutboundCommand> queue = sendQueue
                ?? throw new InvalidOperationException("The KliveTech send queue has not started.");
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken.Token,
                callerToken);
            deadline.CancelAfter(timeout);

            int id = GetNextMessageId();
            PendingRequest? pending = null;
            if (expectResponse)
            {
                pending = new PendingRequest { Gadget = gadget };
                if (!pendingRequests.TryAdd(id, pending))
                {
                    throw new InvalidOperationException($"Duplicate active KliveTech message ID {id}.");
                }
            }

            OutboundCommand command = new()
            {
                Gadget = gadget,
                CancellationToken = deadline.Token,
                Message = new KliveTechGadgetMessage
                {
                    ID = id,
                    DATA = data,
                    OP = operation,
                    RESPEXPECT = expectResponse
                }
            };

            try
            {
                await queue.Writer.WriteAsync(command, deadline.Token);
                await command.Sent.Task.WaitAsync(deadline.Token);
                if (!expectResponse)
                {
                    return KliveTechGadgetResponse.SentWithoutResponse(id);
                }

                return await pending!.Completion.Task.WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested && !callerToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Timed out waiting for KliveTech gadget '{gadget.name}' response {id}.");
            }
            finally
            {
                if (pending != null)
                {
                    pendingRequests.TryRemove(id, out _);
                }
            }
        }

        private async Task SendDataLoopAsync(CancellationToken serviceToken)
        {
            Channel<OutboundCommand> queue = sendQueue
                ?? throw new InvalidOperationException("The KliveTech send queue has not started.");

            try
            {
                await foreach (OutboundCommand command in queue.Reader.ReadAllAsync(serviceToken))
                {
                    if (command.CancellationToken.IsCancellationRequested)
                    {
                        command.Sent.TrySetCanceled(command.CancellationToken);
                        continue;
                    }

                    try
                    {
                        if (!command.Gadget.isOnline)
                        {
                            throw new IOException(
                                $"KliveTech gadget '{command.Gadget.name}' is offline.");
                        }

                        if (command.Gadget.RelayConnection != null)
                        {
                            if (string.IsNullOrWhiteSpace(command.Gadget.RelayDeviceID))
                            {
                                throw new InvalidDataException(
                                    $"KliveTech gadget '{command.Gadget.name}' has no relay device ID.");
                            }
                            await command.Gadget.RelayConnection.SendCommandAsync(
                                command.Gadget.RelayDeviceID,
                                command.Message,
                                command.CancellationToken);
                        }
                        else
                        {
                            NetworkStream stream = command.Gadget.Stream
                                ?? throw new IOException(
                                    $"KliveTech gadget '{command.Gadget.name}' has no Bluetooth stream.");
                            string json = JsonConvert.SerializeObject(command.Message);
                            byte[] bytes = Encoding.UTF8.GetBytes(KliveTechProtocol.Frame(json));
                            await stream.WriteAsync(bytes.AsMemory(), command.CancellationToken);
                            await stream.FlushAsync(command.CancellationToken);
                        }
                        command.Sent.TrySetResult(true);
                    }
                    catch (OperationCanceledException) when (command.CancellationToken.IsCancellationRequested)
                    {
                        command.Sent.TrySetCanceled(command.CancellationToken);
                    }
                    catch (Exception ex)
                    {
                        command.Sent.TrySetException(ex);
                        MarkGadgetOffline(command.Gadget, ex);
                        await ServiceLogError(ex, $"Could not send data to {command.Gadget.name}.");
                    }
                }
            }
            catch (OperationCanceledException) when (serviceToken.IsCancellationRequested)
            {
                // Normal service shutdown.
            }
            catch (ChannelClosedException) when (serviceToken.IsCancellationRequested)
            {
                // Normal service shutdown.
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "The KliveTech send loop stopped unexpectedly.");
                FailAllPending(ex);
                _ = TerminateService();
            }
        }

        private int GetNextMessageId()
        {
            while (true)
            {
                int id = Interlocked.Increment(ref nextMessageId);
                if (id <= 0)
                {
                    Interlocked.CompareExchange(ref nextMessageId, 0, id);
                    continue;
                }

                if (!pendingRequests.ContainsKey(id))
                {
                    return id;
                }
            }
        }

        public KliveTechGadget? GetKliveTechGadgetByID(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            return gadgetsByAddress.Values.FirstOrDefault(
                gadget => string.Equals(gadget.gadgetID, id.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        public KliveTechGadget? GetKliveTechGadgetByName(string? gadgetName)
        {
            if (string.IsNullOrWhiteSpace(gadgetName))
            {
                return null;
            }

            return gadgetsByAddress.Values.FirstOrDefault(
                gadget => string.Equals(
                    gadget.name,
                    gadgetName.Trim(),
                    StringComparison.OrdinalIgnoreCase));
        }

        public async Task<bool> ExecuteActionByName(
            KliveTechGadget? gadget,
            string? actionName,
            string? data)
        {
            if (gadget == null || string.IsNullOrWhiteSpace(actionName) || !gadget.isOnline)
            {
                return false;
            }

            KliveTechActions.KliveTechAction? action = gadget.actions.FirstOrDefault(
                candidate => string.Equals(candidate.name, actionName, StringComparison.Ordinal));
            if (action == null)
            {
                return false;
            }

            object parameter;
            switch (action.parameters)
            {
                case KliveTechActions.ActionParameterType.Integer:
                    if (!int.TryParse(
                            data,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out int integerParameter))
                    {
                        return false;
                    }
                    parameter = integerParameter;
                    break;

                case KliveTechActions.ActionParameterType.Bool:
                    if (!bool.TryParse(data, out bool booleanParameter))
                    {
                        return false;
                    }
                    parameter = booleanParameter;
                    break;

                case KliveTechActions.ActionParameterType.String:
                    parameter = data ?? string.Empty;
                    break;

                case KliveTechActions.ActionParameterType.None:
                    parameter = string.Empty;
                    break;

                default:
                    return false;
            }

            string serializedRequest = JsonConvert.SerializeObject(new KliveTechActions.KliveTechActionRequest
            {
                ActionName = action.name,
                Param = parameter
            });

            KliveTechGadgetResponse response = await SendData(
                gadget,
                KliveTechActions.OperationNumber.ExecuteAction,
                serializedRequest,
                expectResponse: true,
                ResponseTimeout,
                gadget.ConnectionCancellation?.Token ?? cancellationToken.Token);
            return response.STATUS == (int)HttpStatusCode.OK;
        }

        public Task<bool> IsDeviceConnected(KliveTechGadget gadget, int attempts = 0)
        {
            return IsDeviceConnected(gadget, cancellationToken.Token, Math.Max(0, attempts));
        }

        private Task<bool> IsDeviceConnected(
            KliveTechGadget gadget,
            CancellationToken connectionToken)
        {
            return IsDeviceConnected(gadget, connectionToken, startingAttempt: 0);
        }

        private async Task<bool> IsDeviceConnected(
            KliveTechGadget gadget,
            CancellationToken connectionToken,
            int startingAttempt)
        {
            if (!gadget.isOnline)
            {
                return false;
            }

            if (gadget.lastMessageReceived != DateTime.MinValue &&
                gadget.lastMessageReceived.AddSeconds(10) > DateTime.UtcNow)
            {
                return true;
            }

            const int maximumAttempts = 3;
            for (int attempt = startingAttempt; attempt < maximumAttempts; attempt++)
            {
                try
                {
                    KliveTechGadgetResponse response = await SendData(
                        gadget,
                        KliveTechActions.OperationNumber.Ping,
                        "Ping",
                        expectResponse: true,
                        PingTimeout,
                        connectionToken);
                    return response.STATUS == (int)HttpStatusCode.OK;
                }
                catch (OperationCanceledException) when (connectionToken.IsCancellationRequested)
                {
                    return false;
                }
                catch (TimeoutException)
                {
                    // Retry below.
                }
                catch (IOException)
                {
                    // Retry below.
                }
            }

            return false;
        }

        private void MarkGadgetOffline(KliveTechGadget gadget, Exception reason)
        {
            gadget.DisconnectDevice();
            foreach ((int id, PendingRequest pending) in pendingRequests.ToArray())
            {
                if (ReferenceEquals(pending.Gadget, gadget) &&
                    pendingRequests.TryRemove(id, out PendingRequest? removed))
                {
                    removed.Completion.TrySetException(reason);
                }
            }
        }

        private void FailAllPending(Exception reason)
        {
            foreach ((int id, PendingRequest pending) in pendingRequests.ToArray())
            {
                if (pendingRequests.TryRemove(id, out PendingRequest? removed))
                {
                    removed.Completion.TrySetException(reason);
                }
            }
        }

        private static string NormalizeAddress(string? address)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return string.Empty;
            }

            StringBuilder result = new(address.Length);
            foreach (char character in address)
            {
                if (Uri.IsHexDigit(character))
                {
                    result.Append(char.ToUpperInvariant(character));
                }
            }
            return result.ToString();
        }

        private static void EnsurePathIsWithinDirectory(string path, string directory)
        {
            string directoryPrefix = directory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Resolved gadget path escaped the KliveTech directory.");
            }
        }
    }
}
