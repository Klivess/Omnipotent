using InTheHand.Net;
using InTheHand.Net.Sockets;
using Omnipotent.Data_Handling;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace Omnipotent.Services.KliveTechHub
{
    /// <summary>
    /// The hub's own account of what it is doing, for <c>GET /klivetech/status</c>.
    ///
    /// Before this the only view of KliveTech was the list of gadgets connected at that moment. A
    /// remembered gadget that did not answer at start-up, a KliveTech device that discovery skipped
    /// because enrolment is off, and a relay hub whose handshake failed all left no trace anywhere a
    /// person could see: on 13 Nov 2026 two remembered gadgets failed five attempts each at start-up
    /// and the page said nothing had ever reported in.
    ///
    /// Everything here is in memory and bounded. It describes the current process only.
    /// </summary>
    public sealed partial class KliveTechHub
    {
        private const int MaximumHubEvents = 200;
        private const int MaximumRelayAttempts = 50;
        private const int MaximumNearbyDevices = 32;
        private static readonly TimeSpan NearbyDeviceLifetime = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan EnrolmentWindow = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan ToolchainProbeLifetime = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan ToolchainProbeTimeout = TimeSpan.FromSeconds(10);

        private readonly object hubStatusLock = new();
        private readonly LinkedList<HubEvent> hubEvents = new();
        private readonly LinkedList<RelayAttempt> relayAttempts = new();
        private readonly Dictionary<string, ConnectionRecord> connectionRecords =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, NearbyDevice> nearbyDevices =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> enrolmentAllowances =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly DiscoveryStats discoveryStats = new();
        private DateTime hubStartedUtc = DateTime.UtcNow;
        private bool hubBluetoothAvailable;
        private long hubEventSequence;
        private long relayConnectedTotal;
        private long relayRefusedTotal;
        private ToolchainStatus? toolchainStatus;
        private Task? toolchainProbe;

        /* ------------------------------------------------------------ public shapes --- */

        public sealed class HubEvent
        {
            public long seq { get; init; }
            public DateTime atUtc { get; init; }
            /// <summary>connected, disconnected, connect-failed, discovery-failed, nearby, enrolled,
            /// forgotten, action, action-failed, relay-connected, relay-refused, relay-closed, firmware.</summary>
            public string kind { get; init; } = string.Empty;
            /// <summary>ok, info, warn or crit.</summary>
            public string tone { get; init; } = "info";
            public string gadgetID { get; init; } = string.Empty;
            public string gadgetName { get; init; } = string.Empty;
            public string text { get; init; } = string.Empty;
            public string by { get; init; } = string.Empty;
        }

        public sealed class RelayAttempt
        {
            public DateTime atUtc { get; init; } = DateTime.UtcNow;
            public string hubID { get; internal set; } = string.Empty;
            public string hubName { get; internal set; } = string.Empty;
            /// <summary>pending, connected, closed, refused, bad-hello, handshake-timeout or failed.</summary>
            public string outcome { get; internal set; } = "pending";
            public string detail { get; internal set; } = string.Empty;
            public DateTime? endedUtc { get; internal set; }
        }

        public sealed class RememberedStatus
        {
            public string gadgetID { get; init; } = string.Empty;
            public string name { get; init; } = string.Empty;
            public string address { get; init; } = string.Empty;
            public string connectionType { get; init; } = "Bluetooth";
            public bool isHub { get; init; }
            public string connectedViaHubID { get; init; } = string.Empty;
            public bool online { get; init; }
            /// <summary>online, connecting, unreachable, offline, waiting-for-hub or not-tried.</summary>
            public string state { get; init; } = "not-tried";
            public int failedAttempts { get; init; }
            public int totalAttempts { get; init; }
            public DateTime? lastAttemptUtc { get; init; }
            public DateTime? lastConnectedUtc { get; init; }
            public DateTime? lastDisconnectedUtc { get; init; }
            public string lastDisconnectReason { get; init; } = string.Empty;
            public string lastError { get; init; } = string.Empty;
            public DateTime? lastErrorUtc { get; init; }
            /// <summary>Whether this server can try the gadget again itself (direct Bluetooth only).</summary>
            public bool canRetry { get; init; }
        }

        public sealed class NearbyStatus
        {
            public string address { get; init; } = string.Empty;
            public string name { get; init; } = string.Empty;
            public DateTime firstSeenUtc { get; init; }
            public DateTime lastSeenUtc { get; init; }
            public int sightings { get; init; }
            /// <summary>Enrolment was allowed for this device and the hub is trying it.</summary>
            public bool enrolling { get; init; }
        }

        public sealed class ToolchainStatus
        {
            public string command { get; init; } = string.Empty;
            public bool found { get; init; }
            public string version { get; init; } = string.Empty;
            public string error { get; init; } = string.Empty;
            public DateTime checkedUtc { get; init; } = DateTime.UtcNow;
        }

        public sealed class HubStatus
        {
            public DateTime serverUtc { get; init; } = DateTime.UtcNow;
            public DateTime startedUtc { get; init; }
            public object bluetooth { get; init; } = new { };
            public object enrolment { get; init; } = new { };
            public IReadOnlyList<RememberedStatus> remembered { get; init; } = Array.Empty<RememberedStatus>();
            public IReadOnlyList<NearbyStatus> nearby { get; init; } = Array.Empty<NearbyStatus>();
            public object relay { get; init; } = new { };
            public object firmware { get; init; } = new { };
            public object streamables { get; init; } = new { };
            public IReadOnlyList<HubEvent> events { get; init; } = Array.Empty<HubEvent>();
        }

        /* ---------------------------------------------------------- private records --- */

        private sealed class ConnectionRecord
        {
            internal int FailedAttempts;
            internal int TotalAttempts;
            internal DateTime? LastAttemptUtc;
            internal DateTime? LastConnectedUtc;
            internal DateTime? LastDisconnectedUtc;
            internal string LastDisconnectReason = string.Empty;
            internal string LastError = string.Empty;
            internal DateTime? LastErrorUtc;
        }

        private sealed class NearbyDevice
        {
            internal required string Address { get; init; }
            internal string Name = string.Empty;
            internal DateTime FirstSeenUtc;
            internal DateTime LastSeenUtc;
            internal int Sightings;
        }

        private sealed class DiscoveryStats
        {
            internal long Scans;
            internal long Failures;
            internal int ConsecutiveFailures;
            internal DateTime? LastScanUtc;
            internal long LastScanMs;
            internal int LastDevicesSeen;
            internal int LastKliveTechSeen;
            internal string LastError = string.Empty;
            internal DateTime? LastErrorUtc;
        }

        /* ---------------------------------------------------------------- recorders --- */

        private void ResetHubStatus(bool bluetoothAvailable)
        {
            lock (hubStatusLock)
            {
                hubEvents.Clear();
                relayAttempts.Clear();
                connectionRecords.Clear();
                nearbyDevices.Clear();
                discoveryStats.Scans = 0;
                discoveryStats.Failures = 0;
                discoveryStats.ConsecutiveFailures = 0;
                discoveryStats.LastScanUtc = null;
                discoveryStats.LastScanMs = 0;
                discoveryStats.LastDevicesSeen = 0;
                discoveryStats.LastKliveTechSeen = 0;
                discoveryStats.LastError = string.Empty;
                discoveryStats.LastErrorUtc = null;
                hubStartedUtc = DateTime.UtcNow;
                hubBluetoothAvailable = bluetoothAvailable;
                relayConnectedTotal = 0;
                relayRefusedTotal = 0;
            }
            enrolmentAllowances.Clear();
        }

        internal void RecordHubEvent(
            string kind,
            string tone,
            string text,
            KliveTechGadget? gadget = null,
            string by = "")
        {
            HubEvent entry = new()
            {
                seq = Interlocked.Increment(ref hubEventSequence),
                atUtc = DateTime.UtcNow,
                kind = kind,
                tone = tone,
                gadgetID = gadget?.gadgetID ?? string.Empty,
                gadgetName = gadget?.name ?? string.Empty,
                text = Truncate(text, 300),
                by = by ?? string.Empty
            };
            lock (hubStatusLock)
            {
                hubEvents.AddFirst(entry);
                while (hubEvents.Count > MaximumHubEvents)
                {
                    hubEvents.RemoveLast();
                }
            }
        }

        private ConnectionRecord RecordFor(string address)
        {
            if (!connectionRecords.TryGetValue(address, out ConnectionRecord? record))
            {
                record = new ConnectionRecord();
                connectionRecords[address] = record;
            }
            return record;
        }

        /// <summary>One connection attempt to a Bluetooth gadget, successful or not.</summary>
        internal void RecordConnectionAttempt(
            string address,
            string displayName,
            Exception? error,
            int attempt,
            int maximumAttempts,
            KliveTechGadget? connected = null)
        {
            string normalized = NormalizeAddress(address);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            bool finalFailure = false;
            string describedError = string.Empty;
            lock (hubStatusLock)
            {
                ConnectionRecord record = RecordFor(normalized);
                record.TotalAttempts++;
                record.LastAttemptUtc = now;
                if (error == null)
                {
                    record.FailedAttempts = 0;
                    record.LastConnectedUtc = now;
                    record.LastError = string.Empty;
                }
                else
                {
                    record.FailedAttempts++;
                    describedError = DescribeConnectionError(error);
                    record.LastError = describedError;
                    record.LastErrorUtc = now;
                    finalFailure = attempt + 1 >= maximumAttempts;
                }
            }

            if (error == null)
            {
                enrolmentAllowances.TryRemove(normalized, out _);
                lock (hubStatusLock)
                {
                    nearbyDevices.Remove(normalized);
                }
                string how = connected == null || connected.RelayConnection == null
                    ? "Connected over Bluetooth."
                    : connected.isHub
                        ? "Relay hub registered."
                        : $"Connected through {connected.RelayConnection.HubName}.";
                RecordHubEvent("connected", "ok", how, connected);
            }
            else if (finalFailure)
            {
                // One event per run of attempts, not one per attempt: five lines saying the same
                // thing would push everything else out of the history.
                RecordHubEvent(
                    "connect-failed",
                    "warn",
                    $"{displayName} did not connect after {maximumAttempts} attempts: {describedError}");
            }
        }

        /// <summary>Called whenever a gadget is taken offline, with the reason.</summary>
        internal void RecordGadgetOffline(KliveTechGadget gadget, Exception reason, bool wasOnline)
        {
            if (!wasOnline)
            {
                return;
            }

            string normalized = NormalizeAddress(gadget.IPAddress);
            string described = reason is OperationCanceledException
                ? "The KliveTech service stopped."
                : DescribeConnectionError(reason);
            lock (hubStatusLock)
            {
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    ConnectionRecord record = RecordFor(normalized);
                    record.LastDisconnectedUtc = DateTime.UtcNow;
                    record.LastDisconnectReason = described;
                }
            }
            RecordHubEvent("disconnected", "warn", $"Went offline: {described}", gadget);
        }

        internal void RecordDiscoveryScan(long elapsedMs, int devicesSeen, int kliveTechSeen)
        {
            lock (hubStatusLock)
            {
                discoveryStats.Scans++;
                discoveryStats.ConsecutiveFailures = 0;
                discoveryStats.LastScanUtc = DateTime.UtcNow;
                discoveryStats.LastScanMs = elapsedMs;
                discoveryStats.LastDevicesSeen = devicesSeen;
                discoveryStats.LastKliveTechSeen = kliveTechSeen;
            }
        }

        internal void RecordDiscoveryFailure(Exception error, long elapsedMs)
        {
            int consecutive;
            lock (hubStatusLock)
            {
                discoveryStats.Failures++;
                consecutive = ++discoveryStats.ConsecutiveFailures;
                discoveryStats.LastScanUtc = DateTime.UtcNow;
                discoveryStats.LastScanMs = elapsedMs;
                discoveryStats.LastError = error is TimeoutException
                    ? $"The Bluetooth scan did not finish within {DiscoveryTimeout.TotalSeconds:0} s."
                    : Truncate(error.Message, 200);
                discoveryStats.LastErrorUtc = DateTime.UtcNow;
            }
            // The first failure and then every tenth: a radio that keeps failing is one story.
            if (consecutive == 1 || consecutive % 10 == 0)
            {
                RecordHubEvent(
                    "discovery-failed",
                    consecutive >= 10 ? "crit" : "warn",
                    consecutive == 1
                        ? $"A Bluetooth scan failed: {discoveryStats.LastError}"
                        : $"{consecutive} Bluetooth scans in a row have failed: {discoveryStats.LastError}");
            }
        }

        /// <summary>A KliveTech device discovery saw but did not connect, because enrolment is off.</summary>
        internal void RecordNearbyDevice(string address, string name)
        {
            string normalized = NormalizeAddress(address);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            bool first = false;
            lock (hubStatusLock)
            {
                PruneNearbyLocked(now);
                if (!nearbyDevices.TryGetValue(normalized, out NearbyDevice? device))
                {
                    if (nearbyDevices.Count >= MaximumNearbyDevices)
                    {
                        string oldest = nearbyDevices.Values.OrderBy(d => d.LastSeenUtc).First().Address;
                        nearbyDevices.Remove(oldest);
                    }
                    device = new NearbyDevice { Address = normalized, FirstSeenUtc = now };
                    nearbyDevices[normalized] = device;
                    first = true;
                }
                device.Name = string.IsNullOrWhiteSpace(name) ? device.Name : Truncate(name, 64);
                device.LastSeenUtc = now;
                device.Sightings++;
            }

            if (first)
            {
                RecordHubEvent(
                    "nearby",
                    "info",
                    $"{(string.IsNullOrWhiteSpace(name) ? FormatAddress(normalized) : name)} is nearby but not " +
                    "enrolled, so the hub left it alone.");
            }
        }

        private void PruneNearbyLocked(DateTime now)
        {
            foreach (string stale in nearbyDevices.Values
                         .Where(d => now - d.LastSeenUtc > NearbyDeviceLifetime)
                         .Select(d => d.Address)
                         .ToList())
            {
                nearbyDevices.Remove(stale);
            }
        }

        internal RelayAttempt BeginRelayAttempt()
        {
            RelayAttempt attempt = new();
            lock (hubStatusLock)
            {
                relayAttempts.AddFirst(attempt);
                while (relayAttempts.Count > MaximumRelayAttempts)
                {
                    relayAttempts.RemoveLast();
                }
            }
            return attempt;
        }

        internal void FinishRelayAttempt(
            RelayAttempt attempt,
            string outcome,
            string detail,
            string hubId = "",
            string hubName = "")
        {
            bool connected = outcome == "connected";
            bool refused = outcome is "refused" or "bad-hello" or "handshake-timeout";
            lock (hubStatusLock)
            {
                if (!string.IsNullOrWhiteSpace(hubId)) attempt.hubID = hubId;
                if (!string.IsNullOrWhiteSpace(hubName)) attempt.hubName = hubName;
                attempt.outcome = outcome;
                attempt.detail = Truncate(detail, 200);
                if (!connected)
                {
                    attempt.endedUtc = DateTime.UtcNow;
                }
                if (connected) relayConnectedTotal++;
                if (refused) relayRefusedTotal++;
            }

            string who = string.IsNullOrWhiteSpace(attempt.hubName) ? "A relay hub" : attempt.hubName;
            switch (outcome)
            {
                case "connected":
                    RecordHubEvent("relay-connected", "ok", $"{who} connected.");
                    break;
                case "closed":
                case "failed":
                    RecordHubEvent("relay-closed", "warn", $"{who} disconnected: {attempt.detail}");
                    break;
                default:
                    RecordHubEvent("relay-refused", "crit", $"{who} was turned away: {attempt.detail}");
                    break;
            }
        }

        internal void RecordFirmwareJobFinished(FirmwareJob job)
        {
            string target = string.IsNullOrWhiteSpace(job.gadgetName) ? "compile only" : job.gadgetName;
            switch (job.state)
            {
                case "Completed":
                    RecordHubEvent(
                        "firmware",
                        "ok",
                        job.kind == "Update"
                            ? $"Flashed {job.project} to {target}."
                            : $"Compiled {job.project}.");
                    break;
                case "Cancelled":
                    RecordHubEvent("firmware", "info", $"Firmware job for {job.project} ({target}) was cancelled.");
                    break;
                default:
                    RecordHubEvent("firmware", "crit", $"Firmware job for {job.project} ({target}) failed: {job.error}");
                    break;
            }
        }

        /* ----------------------------------------------------------------- status ----- */

        internal HubStatus BuildHubStatus(int eventLimit = 100)
        {
            DateTime now = DateTime.UtcNow;
            EnsureToolchainProbe(now);

            List<RememberedStatus> remembered;
            List<NearbyStatus> nearby;
            List<HubEvent> events;
            List<RelayAttempt> attempts;
            object bluetooth;
            lock (hubStatusLock)
            {
                PruneNearbyLocked(now);
                remembered = BuildRememberedLocked();
                nearby = nearbyDevices.Values
                    .OrderByDescending(d => d.LastSeenUtc)
                    .Select(d => new NearbyStatus
                    {
                        address = FormatAddress(d.Address),
                        name = d.Name,
                        firstSeenUtc = d.FirstSeenUtc,
                        lastSeenUtc = d.LastSeenUtc,
                        sightings = d.Sightings,
                        enrolling = enrolmentAllowances.ContainsKey(d.Address)
                    })
                    .ToList();
                events = hubEvents.Take(Math.Clamp(eventLimit, 0, MaximumHubEvents)).ToList();
                attempts = relayAttempts.Select(CloneAttempt).ToList();
                bluetooth = new
                {
                    available = hubBluetoothAvailable,
                    scanning = discoveryLoopTask != null && !discoveryLoopTask.IsCompleted,
                    intervalSeconds = CurrentDiscoveryInterval().TotalSeconds,
                    timeoutSeconds = DiscoveryTimeout.TotalSeconds,
                    scans = discoveryStats.Scans,
                    failures = discoveryStats.Failures,
                    consecutiveFailures = discoveryStats.ConsecutiveFailures,
                    lastScanUtc = discoveryStats.LastScanUtc,
                    lastScanMs = discoveryStats.LastScanMs,
                    lastDevicesSeen = discoveryStats.LastDevicesSeen,
                    lastKliveTechSeen = discoveryStats.LastKliveTechSeen,
                    lastError = discoveryStats.LastError,
                    lastErrorUtc = discoveryStats.LastErrorUtc
                };
            }

            object relay = new
            {
                endpoint = KliveTechRelayProtocol.WebSocketRoute,
                tokenConfigured = !string.IsNullOrWhiteSpace(relayAccessToken),
                connectedTotal = Interlocked.Read(ref relayConnectedTotal),
                refusedTotal = Interlocked.Read(ref relayRefusedTotal),
                hubs = relayHubConnections.Values
                    .OrderBy(hub => hub.HubName, StringComparer.OrdinalIgnoreCase)
                    .Select(hub => new
                    {
                        hubID = hub.HubId,
                        name = hub.HubName,
                        open = hub.IsOpen,
                        connectedSinceUtc = hub.ConnectedUtc,
                        lastMessageUtc = hub.LastMessageReceived,
                        gadgets = Math.Max(0, hub.DeviceIds.Count - 1)
                    })
                    .ToList(),
                attempts
            };

            return new HubStatus
            {
                serverUtc = now,
                startedUtc = hubStartedUtc,
                bluetooth = bluetooth,
                enrolment = new
                {
                    allowNewDevices,
                    setting = "KliveTechAllowNewDevices",
                    windowMinutes = EnrolmentWindow.TotalMinutes,
                    allowances = enrolmentAllowances
                        .Where(pair => now - pair.Value <= EnrolmentWindow)
                        .Select(pair => FormatAddress(pair.Key))
                        .ToList()
                },
                remembered = remembered,
                nearby = nearby,
                relay = relay,
                firmware = BuildFirmwareReadiness(),
                streamables = BuildStreamableTotals(),
                events = events
            };
        }

        private List<RememberedStatus> BuildRememberedLocked()
        {
            Dictionary<string, RememberedGadget> byAddress = new(rememberedDevices, StringComparer.OrdinalIgnoreCase);
            HashSet<string> addresses = new(byAddress.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (string key in gadgetsByAddress.Keys)
            {
                addresses.Add(NormalizeAddress(key));
            }

            List<RememberedStatus> result = new();
            foreach (string address in addresses)
            {
                byAddress.TryGetValue(address, out RememberedGadget? stored);
                gadgetsByAddress.TryGetValue(address, out KliveTechGadget? live);
                connectionRecords.TryGetValue(address, out ConnectionRecord? record);

                string connectionType = live?.connectionType ?? stored?.connectionType ?? "Bluetooth";
                bool relayed = connectionType.Equals("Relayed", StringComparison.OrdinalIgnoreCase) ||
                               connectionType.Equals("Hub", StringComparison.OrdinalIgnoreCase);
                bool online = live?.isOnline == true;
                string state;
                if (online) state = "online";
                else if (connectionsInProgress.ContainsKey(address)) state = "connecting";
                else if (relayed) state = "waiting-for-hub";
                else if (record?.FailedAttempts > 0) state = "unreachable";
                else if (record?.LastConnectedUtc != null) state = "offline";
                else state = "not-tried";

                result.Add(new RememberedStatus
                {
                    gadgetID = live?.gadgetID ?? stored?.gadgetID ?? string.Empty,
                    name = live?.name ?? stored?.name ?? $"KliveTech-{address}",
                    address = FormatAddress(address),
                    connectionType = connectionType,
                    isHub = live?.isHub ?? stored?.isHub ?? false,
                    connectedViaHubID = live?.connectedViaHubID ?? stored?.connectedViaHubID ?? string.Empty,
                    online = online,
                    state = state,
                    failedAttempts = record?.FailedAttempts ?? 0,
                    totalAttempts = record?.TotalAttempts ?? 0,
                    lastAttemptUtc = record?.LastAttemptUtc,
                    lastConnectedUtc = record?.LastConnectedUtc ?? (online ? live!.timeConnected : null),
                    lastDisconnectedUtc = record?.LastDisconnectedUtc,
                    lastDisconnectReason = record?.LastDisconnectReason ?? string.Empty,
                    lastError = record?.LastError ?? string.Empty,
                    lastErrorUtc = record?.LastErrorUtc,
                    canRetry = !relayed && !online && hubBluetoothAvailable
                });
            }

            return result
                .OrderBy(r => r.online ? 0 : 1)
                .ThenBy(r => r.name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private object BuildFirmwareReadiness()
        {
            int projects = 0;
            try
            {
                if (!string.IsNullOrWhiteSpace(firmwareInboxDirectory) && Directory.Exists(firmwareInboxDirectory))
                {
                    projects = Directory.EnumerateDirectories(firmwareInboxDirectory).Take(MaximumProjectEntries).Count();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                projects = -1;
            }

            return new
            {
                toolchain = toolchainStatus,
                libraryConfigured = !string.IsNullOrWhiteSpace(kliveTechLibraryPath),
                inboxProjects = projects,
                activeJobs = firmwareJobs.Values.Count(job => !IsTerminalFirmwareState(job.state)),
                rememberedJobs = firmwareJobs.Count
            };
        }

        private object BuildStreamableTotals()
        {
            int declared;
            int enabled;
            int gadgets;
            lock (streamableStateLock)
            {
                declared = streamableStates.Count;
                enabled = streamableStates.Values.Count(state => state.Definition.Enabled);
                gadgets = streamableStates.Values.Select(state => state.GadgetID).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            }
            return new
            {
                declared,
                enabled,
                gadgets,
                droppedEvents = Interlocked.Read(ref droppedStreamEvents),
                liveViewers = streamLiveSubscribers.Count,
                maximumLiveViewers = MaximumLiveSubscribers,
                retainedSamples = Volatile.Read(ref retainedSampleCount),
                retainedSampleBytes = Volatile.Read(ref retainedSampleBytes),
                retainedFrameBytes = Volatile.Read(ref retainedFrameBytes)
            };
        }

        private static RelayAttempt CloneAttempt(RelayAttempt attempt) => new()
        {
            atUtc = attempt.atUtc,
            hubID = attempt.hubID,
            hubName = attempt.hubName,
            outcome = attempt.outcome,
            detail = attempt.detail,
            endedUtc = attempt.endedUtc
        };

        /* -------------------------------------------------------- firmware toolchain -- */

        /// <summary>
        /// Whether arduino-cli can actually be run. Checked in the background, at most every ten
        /// minutes and only while someone reads the status, so a build is never the first thing to
        /// find out that the toolchain is missing.
        /// </summary>
        private void EnsureToolchainProbe(DateTime now)
        {
            ToolchainStatus? current = toolchainStatus;
            if (current != null && now - current.checkedUtc < ToolchainProbeLifetime)
            {
                return;
            }
            Task? running = toolchainProbe;
            if (running != null && !running.IsCompleted)
            {
                return;
            }
            toolchainProbe = Task.Run(async () => toolchainStatus = await ProbeToolchainAsync(arduinoCliPath));
        }

        internal static async Task<ToolchainStatus> ProbeToolchainAsync(string command)
        {
            string executable = string.IsNullOrWhiteSpace(command) ? "arduino-cli" : command;
            try
            {
                ProcessStartInfo info = new()
                {
                    FileName = executable,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                info.ArgumentList.Add("version");
                using Process process = new() { StartInfo = info };
                process.Start();
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> errors = process.StandardError.ReadToEndAsync();
                using CancellationTokenSource deadline = new(ToolchainProbeTimeout);
                try
                {
                    await process.WaitForExitAsync(deadline.Token);
                }
                catch (OperationCanceledException)
                {
                    TryKillProcess(process);
                    return new ToolchainStatus
                    {
                        command = executable,
                        found = false,
                        error = $"'{executable} version' did not answer within {ToolchainProbeTimeout.TotalSeconds:0} s."
                    };
                }

                string text = (await output).Trim();
                string firstLine = text.Split('\n', 2)[0].Trim();
                if (process.ExitCode != 0)
                {
                    string problem = (await errors).Trim();
                    return new ToolchainStatus
                    {
                        command = executable,
                        found = false,
                        error = Truncate(string.IsNullOrWhiteSpace(problem)
                            ? $"'{executable} version' exited with code {process.ExitCode}."
                            : problem, 200)
                    };
                }
                return new ToolchainStatus
                {
                    command = executable,
                    found = true,
                    version = Truncate(firstLine, 120)
                };
            }
            catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException)
            {
                return new ToolchainStatus
                {
                    command = executable,
                    found = false,
                    error = $"'{executable}' could not be started on this server. Install arduino-cli or set " +
                            "KliveTechArduinoCliPath to its full path."
                };
            }
        }

        /* ------------------------------------------------------- operator actions ---- */

        /// <summary>Finds a remembered or live gadget by its ID. Returns its normalised address.</summary>
        private bool TryFindKnownGadget(
            string? gadgetId,
            out string address,
            out RememberedGadget? remembered,
            out KliveTechGadget? live)
        {
            address = string.Empty;
            remembered = null;
            live = null;
            if (string.IsNullOrWhiteSpace(gadgetId))
            {
                return false;
            }

            string id = gadgetId.Trim();
            foreach ((string key, KliveTechGadget gadget) in gadgetsByAddress)
            {
                if (string.Equals(gadget.gadgetID, id, StringComparison.OrdinalIgnoreCase))
                {
                    address = NormalizeAddress(key);
                    live = gadget;
                    rememberedDevices.TryGetValue(address, out remembered);
                    return true;
                }
            }
            foreach ((string key, RememberedGadget stored) in rememberedDevices)
            {
                if (string.Equals(stored.gadgetID, id, StringComparison.OrdinalIgnoreCase))
                {
                    address = NormalizeAddress(key);
                    remembered = stored;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Tries a remembered Bluetooth gadget again now, rather than waiting for discovery to see it.
        /// Returns "online", "connecting" or a reason it cannot be tried.
        /// </summary>
        internal string RetryRememberedGadget(string? gadgetId, string by)
        {
            if (!TryFindKnownGadget(gadgetId, out string address, out RememberedGadget? remembered, out KliveTechGadget? live))
            {
                throw new KeyNotFoundException("No remembered gadget has that ID.");
            }
            if (live?.isOnline == true)
            {
                return "online";
            }

            string connectionType = live?.connectionType ?? remembered?.connectionType ?? "Bluetooth";
            if (!connectionType.Equals("Bluetooth", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "This gadget connects through a relay hub; it comes back when its hub reports it.");
            }
            if (!hubBluetoothAvailable || discoveryClient == null)
            {
                throw new InvalidOperationException("This server has no Bluetooth adapter.");
            }
            if (connectionsInProgress.ContainsKey(address))
            {
                return "connecting";
            }

            long addressValue = remembered?.IPAddressLong > 0
                ? remembered.IPAddressLong
                : live?.IPAddressLong ?? 0;
            if (addressValue <= 0)
            {
                throw new InvalidOperationException("The remembered gadget has no Bluetooth address.");
            }

            string name = live?.name ?? remembered?.name ?? FormatAddress(address);
            RecordHubEvent("retry", "info", $"Trying {name} again.", live, by);
            CancellationToken token = cancellationToken.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await TryConnectToDeviceInternal(
                        new BluetoothDeviceInfo(new BluetoothAddress(addressValue)),
                        remembered,
                        startingAttempt: 0,
                        token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // Service stopping.
                }
                catch (Exception ex)
                {
                    await ServiceLogError(ex, $"Retrying KliveTech gadget {name} failed.");
                }
            }, CancellationToken.None);
            return "connecting";
        }

        /// <summary>
        /// Lets one nearby KliveTech device connect although KliveTechAllowNewDevices is off. The
        /// allowance lasts ten minutes or until it connects, and the hub tries it straight away.
        /// </summary>
        internal string AllowEnrolment(string? address, string by)
        {
            string normalized = NormalizeAddress(address);
            if (normalized.Length != 12)
            {
                throw new InvalidDataException("address must be a 12-digit Bluetooth address.");
            }

            string name;
            lock (hubStatusLock)
            {
                PruneNearbyLocked(DateTime.UtcNow);
                if (!nearbyDevices.TryGetValue(normalized, out NearbyDevice? device))
                {
                    throw new KeyNotFoundException(
                        "That device has not been seen nearby in the last 15 minutes.");
                }
                name = string.IsNullOrWhiteSpace(device.Name) ? FormatAddress(normalized) : device.Name;
            }
            if (rememberedDevices.ContainsKey(normalized))
            {
                return "remembered";
            }

            enrolmentAllowances[normalized] = DateTime.UtcNow;
            RecordHubEvent("enrolled", "info", $"Enrolment allowed for {name}; connecting.", by: by);

            if (hubBluetoothAvailable && discoveryClient != null && !connectionsInProgress.ContainsKey(normalized))
            {
                CancellationToken token = cancellationToken.Token;
                long value = long.Parse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await TryConnectToDeviceInternal(
                            new BluetoothDeviceInfo(new BluetoothAddress(value)),
                            remembered: null,
                            startingAttempt: 0,
                            token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        // Service stopping.
                    }
                    catch (Exception ex)
                    {
                        await ServiceLogError(ex, $"Enrolling KliveTech device {name} failed.");
                    }
                }, CancellationToken.None);
            }
            return "connecting";
        }

        /// <summary>
        /// How long discovery waits before the next scan. A Bluetooth inquiry takes the radio for about
        /// 10 s and was repeated every 5 s for ever (two scans timed out at 30 s on 13 Nov 2026), even with
        /// every gadget connected and enrolment off, when a scan can only find something to list. It
        /// keeps the fast pace while a remembered gadget is missing or enrolment is open.
        /// </summary>
        internal static TimeSpan NextDiscoveryInterval(int rememberedBluetoothOffline, bool enrolmentOpen)
            => rememberedBluetoothOffline > 0 || enrolmentOpen ? DiscoveryInterval : IdleDiscoveryInterval;

        private TimeSpan CurrentDiscoveryInterval()
        {
            int offline = 0;
            foreach ((string address, RememberedGadget remembered) in rememberedDevices)
            {
                if (!string.Equals(remembered.connectionType, "Bluetooth", StringComparison.OrdinalIgnoreCase)) continue;
                if (!gadgetsByAddress.TryGetValue(address, out KliveTechGadget? live) || !live.isOnline) offline++;
            }
            bool open = allowNewDevices || enrolmentAllowances.Any(pair => DateTime.UtcNow - pair.Value <= EnrolmentWindow);
            return NextDiscoveryInterval(offline, open);
        }

        /// <summary>Whether discovery may connect a device that is not remembered.</summary>
        private bool MayEnrol(string normalizedAddress)
        {
            if (allowNewDevices)
            {
                return true;
            }
            if (!enrolmentAllowances.TryGetValue(normalizedAddress, out DateTime allowedAt))
            {
                return false;
            }
            if (DateTime.UtcNow - allowedAt <= EnrolmentWindow)
            {
                return true;
            }
            enrolmentAllowances.TryRemove(normalizedAddress, out _);
            return false;
        }

        /// <summary>
        /// Forgets an offline gadget: removes its remembered file so start-up and discovery stop
        /// trying it. It can be enrolled again later.
        /// </summary>
        internal async Task<string> ForgetGadgetAsync(string? gadgetId, string by)
        {
            if (!TryFindKnownGadget(gadgetId, out string address, out RememberedGadget? remembered, out KliveTechGadget? live))
            {
                throw new KeyNotFoundException("No remembered gadget has that ID.");
            }
            if (live?.isOnline == true)
            {
                throw new InvalidOperationException("The gadget is online. Forget it once it is offline.");
            }

            string directory = Path.GetFullPath(
                OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveTechHubGadgetsDirectory));
            string path = Path.GetFullPath(Path.Combine(directory, $"{address}.kliveTechGadget"));
            EnsurePathIsWithinDirectory(path, directory);
            if (File.Exists(path))
            {
                await GetDataHandler().DeleteFile(path);
            }

            rememberedDevices.TryRemove(address, out _);
            if (live != null)
            {
                gadgetsByAddress.TryRemove(address, out _);
            }
            lock (hubStatusLock)
            {
                connectionRecords.Remove(address);
            }
            string name = live?.name ?? remembered?.name ?? FormatAddress(address);
            RecordHubEvent("forgotten", "info", $"Forgot {name}.", by: by);
            await ServiceLog($"KliveTech gadget {name} was forgotten by {by}.");
            return name;
        }

        /// <summary>
        /// The fleet as one Discord message (KliveBot's /getConnectedKliveTechGadgets). It used to send an
        /// empty message when nothing was connected, which Discord refuses, so the answer was "an error
        /// occurred"; it listed offline gadgets as connected and never mentioned the unreachable ones.
        /// </summary>
        internal string DescribeFleetText(int maxLength = 1900)
        {
            HubStatus status = BuildHubStatus(eventLimit: 0);
            List<KliveTechGadget> live = connectedGadgets.Where(gadget => gadget.isOnline).ToList();
            StringBuilder text = new();
            if (live.Count == 0)
            {
                text.AppendLine("No KliveTech gadget is connected.");
            }
            foreach (KliveTechGadget gadget in live)
            {
                text.AppendLine($"**{gadget.name}** · {gadget.connectionType}");
                if (gadget.actions.Count == 0)
                {
                    text.AppendLine("• no actions");
                }
                foreach (KliveTechActions.KliveTechAction action in gadget.actions)
                {
                    string about = string.IsNullOrWhiteSpace(action.paramDescription) ? string.Empty : $" — {action.paramDescription}";
                    text.AppendLine($"• {action.name} ({action.parameters}){about}");
                }
            }

            List<RememberedStatus> down = status.remembered.Where(entry => !entry.online).ToList();
            if (down.Count > 0)
            {
                text.AppendLine();
                text.AppendLine($"Not connected ({down.Count}):");
                foreach (RememberedStatus entry in down)
                {
                    string why = string.IsNullOrWhiteSpace(entry.lastError) ? string.Empty : $" ({entry.lastError})";
                    text.AppendLine($"• {entry.name}: {entry.state.Replace('-', ' ')}{why}");
                }
            }
            if (status.nearby.Count > 0)
            {
                text.AppendLine();
                text.AppendLine($"{status.nearby.Count} KliveTech device(s) nearby but not enrolled: enrol from the KliveTech page.");
            }

            string result = text.ToString().Trim();
            return result.Length <= maxLength ? result : result[..(maxLength - 1)] + "…";
        }

        /* ---------------------------------------------------------------- helpers ---- */

        /// <summary>
        /// Turns a connection failure into what it means for the person holding the gadget. The raw
        /// Windows message ("the connected party did not properly respond…") says none of that.
        /// </summary>
        internal static string DescribeConnectionError(Exception error)
        {
            Exception root = error is AggregateException aggregate && aggregate.InnerException != null
                ? aggregate.InnerException
                : error;
            switch (root)
            {
                case SocketException socket:
                    return socket.SocketErrorCode switch
                    {
                        SocketError.TimedOut => "no answer (it is off, out of range or not accepting connections)",
                        SocketError.HostDown or SocketError.HostUnreachable or SocketError.NetworkUnreachable =>
                            "unreachable over Bluetooth",
                        SocketError.ConnectionRefused => "refused the connection (Bluetooth serial port not open)",
                        SocketError.ConnectionReset or SocketError.ConnectionAborted => "the connection dropped",
                        SocketError.AddressAlreadyInUse or SocketError.IsConnected => "already connected elsewhere",
                        _ => $"Bluetooth error {(int)socket.SocketErrorCode} ({socket.SocketErrorCode})"
                    };
                case TimeoutException:
                    return "timed out";
                case EndOfStreamException:
                    return "the Bluetooth stream closed";
                case IOException io when io.InnerException is SocketException inner:
                    return DescribeConnectionError(inner);
                default:
                    return Truncate(root.Message, 160);
            }
        }

        /// <summary>12 hex digits as a colon-separated MAC, the form printed on the device.</summary>
        internal static string FormatAddress(string? address)
        {
            string normalized = NormalizeAddress(address);
            if (normalized.Length != 12)
            {
                return normalized;
            }
            StringBuilder builder = new(17);
            for (int index = 0; index < 12; index += 2)
            {
                if (index > 0) builder.Append(':');
                builder.Append(normalized, index, 2);
            }
            return builder.ToString();
        }

        private static string Truncate(string? value, int length)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            string trimmed = value.Trim();
            return trimmed.Length <= length ? trimmed : trimmed[..(length - 1)] + "…";
        }

        // Focused tests drive the recorders without the service host.
        internal void ResetHubStatusForTests(bool bluetoothAvailable) => ResetHubStatus(bluetoothAvailable);

        internal void AddRememberedForTests(string address, string name, string gadgetId, string connectionType = "Bluetooth")
        {
            string normalized = NormalizeAddress(address);
            rememberedDevices[normalized] = new RememberedGadget
            {
                name = name,
                IPAddress = address,
                IPAddressLong = long.Parse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                gadgetID = gadgetId,
                connectionType = connectionType,
                isHub = connectionType == "Hub"
            };
        }

        internal void AddLiveGadgetForTests(KliveTechGadget gadget) =>
            gadgetsByAddress[NormalizeAddress(gadget.IPAddress)] = gadget;

        internal bool MayEnrolForTests(string address) => MayEnrol(NormalizeAddress(address));

        internal void SetAllowNewDevicesForTests(bool value) => allowNewDevices = value;
    }
}
