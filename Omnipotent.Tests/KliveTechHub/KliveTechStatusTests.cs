using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveTechHub;
using System.Net.Sockets;
using HubService = Omnipotent.Services.KliveTechHub.KliveTechHub;

namespace Omnipotent.Tests.KliveTechHub
{
    /// <summary>
    /// The hub status behind the KliveTech page. The live failure it exists for (13 Nov 2026): two
    /// remembered gadgets failed five connection attempts each at start-up, the gadget list came back
    /// empty, and nothing anywhere said they existed. Addresses here are made up.
    /// </summary>
    public sealed class KliveTechStatusTests
    {
        private const string DeskAddress = "0A1B2C3D4E5F";
        private const string LampAddress = "112233445566";

        private static HubService NewHub(bool bluetooth = true)
        {
            HubService hub = new();
            hub.ResetHubStatusForTests(bluetooth);
            return hub;
        }

        private static SocketException NoAnswer() => new((int)SocketError.TimedOut);

        [Fact]
        public void RememberedGadgetThatNeverAnswers_IsListedAsUnreachable_WithOneHistoryLine()
        {
            HubService hub = NewHub();
            hub.AddRememberedForTests(DeskAddress, "KliveTech Desk", "desk-id");

            for (int attempt = 0; attempt < 5; attempt++)
            {
                hub.RecordConnectionAttempt(DeskAddress, "KliveTech Desk", NoAnswer(), attempt, 5);
            }

            HubService.HubStatus status = hub.BuildHubStatus();
            HubService.RememberedStatus desk = Assert.Single(status.remembered);
            Assert.Equal("desk-id", desk.gadgetID);
            Assert.Equal("unreachable", desk.state);
            Assert.False(desk.online);
            Assert.Equal(5, desk.failedAttempts);
            Assert.Equal(5, desk.totalAttempts);
            Assert.Equal("0A:1B:2C:3D:4E:5F", desk.address);
            Assert.StartsWith("no answer", desk.lastError);
            Assert.True(desk.canRetry);
            Assert.NotNull(desk.lastAttemptUtc);

            // Five failed attempts are one story, not five.
            HubService.HubEvent failed = Assert.Single(status.events, e => e.kind == "connect-failed");
            Assert.Contains("5 attempts", failed.text);
            Assert.Equal("warn", failed.tone);
        }

        [Fact]
        public void SuccessfulConnect_ClearsTheFailureRun_AndShowsOnline()
        {
            HubService hub = NewHub();
            hub.AddRememberedForTests(DeskAddress, "KliveTech Desk", "desk-id");
            hub.RecordConnectionAttempt(DeskAddress, "KliveTech Desk", NoAnswer(), 0, 5);

            HubService.KliveTechGadget live = new()
            {
                name = "KliveTech Desk",
                IPAddress = DeskAddress,
                gadgetID = "desk-id",
                isOnline = true,
                timeConnected = DateTime.UtcNow
            };
            hub.AddLiveGadgetForTests(live);
            hub.RecordConnectionAttempt(DeskAddress, live.name, null, 1, 5, live);

            HubService.RememberedStatus desk = Assert.Single(hub.BuildHubStatus().remembered);
            Assert.Equal("online", desk.state);
            Assert.Equal(0, desk.failedAttempts);
            Assert.Equal(2, desk.totalAttempts);
            Assert.Equal(string.Empty, desk.lastError);
            Assert.False(desk.canRetry);
            Assert.Contains(hub.BuildHubStatus().events, e => e.kind == "connected" && e.gadgetID == "desk-id");
        }

        [Fact]
        public void GoingOffline_RecordsTheReason_OnlyWhenItWasOnline()
        {
            HubService hub = NewHub();
            HubService.KliveTechGadget lamp = new()
            {
                name = "KliveTech Lamp",
                IPAddress = LampAddress,
                gadgetID = "lamp-id",
                isOnline = false
            };
            hub.AddLiveGadgetForTests(lamp);
            hub.AddRememberedForTests(LampAddress, "KliveTech Lamp", "lamp-id");

            hub.RecordGadgetOffline(lamp, new IOException("KliveTech gadget 'KliveTech Lamp' stopped responding."), wasOnline: true);
            hub.RecordGadgetOffline(lamp, new IOException("again"), wasOnline: false);

            HubService.HubStatus status = hub.BuildHubStatus();
            Assert.Single(status.events, e => e.kind == "disconnected");
            HubService.RememberedStatus row = Assert.Single(status.remembered);
            Assert.Contains("stopped responding", row.lastDisconnectReason);
            Assert.NotNull(row.lastDisconnectedUtc);
        }

        [Fact]
        public void RelayedGadget_WaitsForItsHub_AndCannotBeRetriedFromHere()
        {
            HubService hub = NewHub();
            hub.AddRememberedForTests(LampAddress, "KliveTech Lamp", "lamp-id", connectionType: "Relayed");

            HubService.RememberedStatus row = Assert.Single(hub.BuildHubStatus().remembered);
            Assert.Equal("waiting-for-hub", row.state);
            Assert.False(row.canRetry);
            Assert.Throws<InvalidOperationException>(() => hub.RetryRememberedGadget("lamp-id", "test"));
        }

        [Fact]
        public void Retry_WithoutBluetooth_SaysSo_AndUnknownIdIsNotFound()
        {
            HubService hub = NewHub(bluetooth: false);
            hub.AddRememberedForTests(DeskAddress, "KliveTech Desk", "desk-id");

            Assert.False(Assert.Single(hub.BuildHubStatus().remembered).canRetry);
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => hub.RetryRememberedGadget("desk-id", "test"));
            Assert.Contains("Bluetooth", error.Message);
            Assert.Throws<KeyNotFoundException>(() => hub.RetryRememberedGadget("nope", "test"));
        }

        [Fact]
        public void NearbyDevice_WhileEnrolmentIsOff_IsListed_AndCanBeAllowedOnce()
        {
            HubService hub = NewHub(bluetooth: false);
            hub.SetAllowNewDevicesForTests(false);
            Assert.False(hub.MayEnrolForTests(DeskAddress));

            hub.RecordNearbyDevice(DeskAddress, "KliveTech Desk");
            hub.RecordNearbyDevice(DeskAddress, "KliveTech Desk");

            HubService.HubStatus before = hub.BuildHubStatus();
            HubService.NearbyStatus seen = Assert.Single(before.nearby);
            Assert.Equal(2, seen.sightings);
            Assert.Equal("KliveTech Desk", seen.name);
            Assert.False(seen.enrolling);
            Assert.Single(before.events, e => e.kind == "nearby");

            Assert.Equal("connecting", hub.AllowEnrolment("0a:1b:2c:3d:4e:5f", "test"));
            Assert.True(hub.MayEnrolForTests(DeskAddress));
            Assert.True(Assert.Single(hub.BuildHubStatus().nearby).enrolling);
            JObject enrolment = JObject.FromObject(hub.BuildHubStatus().enrolment);
            Assert.Equal("0A:1B:2C:3D:4E:5F", enrolment["allowances"]![0]!.Value<string>());

            // Only a device seen nearby can be let in, and only by a real address.
            Assert.Throws<KeyNotFoundException>(() => hub.AllowEnrolment(LampAddress, "test"));
            Assert.Throws<InvalidDataException>(() => hub.AllowEnrolment("not-an-address", "test"));
        }

        [Theory]
        [InlineData(0, false, 30)]  // everything connected, nobody may enrol: a scan can only list a neighbour
        [InlineData(1, false, 5)]   // a remembered gadget is missing: look for it often
        [InlineData(0, true, 5)]    // enrolment open: a new gadget should connect quickly
        public void ScanCadence_SlowsOnlyWhenThereIsNothingToFind(int offline, bool enrolmentOpen, int seconds)
        {
            Assert.Equal(TimeSpan.FromSeconds(seconds), HubService.NextDiscoveryInterval(offline, enrolmentOpen));
        }

        [Fact]
        public void Status_ReportsTheCadenceInUse()
        {
            HubService hub = NewHub(bluetooth: false);
            hub.SetAllowNewDevicesForTests(false);
            hub.AddRememberedForTests(DeskAddress, "KliveTech Desk", "desk-id");
            Assert.Equal(5, JObject.FromObject(hub.BuildHubStatus().bluetooth)["intervalSeconds"]!.Value<double>());

            hub.AddLiveGadgetForTests(new HubService.KliveTechGadget { name = "KliveTech Desk", IPAddress = DeskAddress, gadgetID = "desk-id", isOnline = true });
            Assert.Equal(30, JObject.FromObject(hub.BuildHubStatus().bluetooth)["intervalSeconds"]!.Value<double>());

            // A relayed gadget is found by its hub, not by a scan, so it never holds the fast pace.
            hub.AddRememberedForTests(LampAddress, "KliveTech Lamp", "lamp-id", connectionType: "Relayed");
            Assert.Equal(30, JObject.FromObject(hub.BuildHubStatus().bluetooth)["intervalSeconds"]!.Value<double>());

            hub.RecordNearbyDevice("0A1B2C3D4E77", "KliveTech Porch");
            hub.AllowEnrolment("0A1B2C3D4E77", "test");
            Assert.Equal(5, JObject.FromObject(hub.BuildHubStatus().bluetooth)["intervalSeconds"]!.Value<double>());
        }

        [Fact]
        public void DiscordText_IsNeverEmpty_AndNamesTheGadgetsThatAreNotConnected()
        {
            HubService hub = NewHub(bluetooth: false);
            Assert.Equal("No KliveTech gadget is connected.", hub.DescribeFleetText());

            hub.AddRememberedForTests(DeskAddress, "KliveTech Desk", "desk-id");
            for (int attempt = 0; attempt < 5; attempt++)
            {
                hub.RecordConnectionAttempt(DeskAddress, "KliveTech Desk", NoAnswer(), attempt, 5);
            }
            HubService.KliveTechGadget lamp = new()
            {
                name = "KliveTech Lamp",
                IPAddress = LampAddress,
                gadgetID = "lamp-id",
                isOnline = true,
                actions = new() { new KliveTechActions.KliveTechAction { name = "Set light", parameters = KliveTechActions.ActionParameterType.Bool, paramDescription = "On or off" } }
            };
            hub.AddLiveGadgetForTests(lamp);
            hub.RecordNearbyDevice("0A1B2C3D4E77", "KliveTech Porch");

            string text = hub.DescribeFleetText();
            Assert.StartsWith("**KliveTech Lamp** · Bluetooth", text);
            Assert.Contains("• Set light (Bool) — On or off", text);
            Assert.Contains("Not connected (1):", text);
            Assert.Contains("• KliveTech Desk: unreachable (no answer", text);
            Assert.Contains("1 KliveTech device(s) nearby", text);
            Assert.DoesNotContain("No KliveTech gadget is connected.", text);
        }

        [Fact]
        public void DiscordText_StaysWithinDiscordsLimit()
        {
            HubService hub = NewHub(bluetooth: false);
            HubService.KliveTechGadget busy = new() { name = "KliveTech Busy", IPAddress = LampAddress, gadgetID = "busy", isOnline = true };
            for (int i = 0; i < 200; i++)
            {
                busy.actions.Add(new KliveTechActions.KliveTechAction { name = $"Action {i}", paramDescription = new string('x', 30) });
            }
            hub.AddLiveGadgetForTests(busy);

            string text = hub.DescribeFleetText();
            Assert.True(text.Length <= 1900);
            Assert.EndsWith("…", text);
        }

        [Fact]
        public void EnrolmentSetting_AllowsEveryDevice()
        {
            HubService hub = NewHub();
            hub.SetAllowNewDevicesForTests(true);
            Assert.True(hub.MayEnrolForTests(LampAddress));
        }

        [Fact]
        public void DiscoveryFailures_AreCounted_AndOnlyTheFirstAndEveryTenthBecomeHistory()
        {
            HubService hub = NewHub();
            for (int i = 0; i < 12; i++)
            {
                hub.RecordDiscoveryFailure(new TimeoutException("The operation has timed out."), 30_000);
            }

            HubService.HubStatus status = hub.BuildHubStatus();
            JObject bluetooth = JObject.FromObject(status.bluetooth);
            Assert.Equal(12, bluetooth["failures"]!.Value<long>());
            Assert.Equal(12, bluetooth["consecutiveFailures"]!.Value<int>());
            Assert.Contains("did not finish within 30 s", bluetooth["lastError"]!.Value<string>());
            Assert.Equal(2, status.events.Count(e => e.kind == "discovery-failed"));

            hub.RecordDiscoveryScan(10_240, devicesSeen: 7, kliveTechSeen: 1);
            bluetooth = JObject.FromObject(hub.BuildHubStatus().bluetooth);
            Assert.Equal(0, bluetooth["consecutiveFailures"]!.Value<int>());
            Assert.Equal(1, bluetooth["scans"]!.Value<long>());
            Assert.Equal(7, bluetooth["lastDevicesSeen"]!.Value<int>());
            Assert.Equal(1, bluetooth["lastKliveTechSeen"]!.Value<int>());
        }

        [Fact]
        public void RelayAttempts_RecordRefusalsAndSessions()
        {
            HubService hub = NewHub();
            HubService.RelayAttempt refused = hub.BeginRelayAttempt();
            hub.FinishRelayAttempt(refused, "refused", "Its access token does not match KliveTechHubAccessToken.", "AABBCCDDEEFF", "Kitchen Hub");

            HubService.RelayAttempt session = hub.BeginRelayAttempt();
            hub.FinishRelayAttempt(session, "connected", string.Empty, "AABBCCDDEEFF", "Kitchen Hub");
            hub.FinishRelayAttempt(session, "closed", "The hub closed the connection.");

            HubService.HubStatus status = hub.BuildHubStatus();
            JObject relay = JObject.FromObject(status.relay);
            Assert.Equal(1, relay["connectedTotal"]!.Value<long>());
            Assert.Equal(1, relay["refusedTotal"]!.Value<long>());
            JArray attempts = (JArray)relay["attempts"]!;
            Assert.Equal(2, attempts.Count);
            Assert.Equal("closed", attempts[0]!["outcome"]!.Value<string>());   // newest first
            Assert.Equal("refused", attempts[1]!["outcome"]!.Value<string>());
            Assert.Equal("Kitchen Hub", attempts[1]!["hubName"]!.Value<string>());
            Assert.NotNull(attempts[0]!["endedUtc"]!.Value<DateTime?>());
            Assert.Contains(status.events, e => e.kind == "relay-refused" && e.tone == "crit");
            Assert.Contains(status.events, e => e.kind == "relay-connected");
            Assert.Contains(status.events, e => e.kind == "relay-closed");
        }

        [Fact]
        public void EventHistory_IsBounded_AndNewestFirst()
        {
            HubService hub = NewHub();
            for (int i = 0; i < 260; i++)
            {
                hub.RecordHubEvent("action", "ok", $"run {i}");
            }

            HubService.HubStatus status = hub.BuildHubStatus(eventLimit: 500);
            Assert.Equal(200, status.events.Count);
            Assert.Equal("run 259", status.events[0].text);
            Assert.Equal(10, hub.BuildHubStatus(eventLimit: 10).events.Count);
        }

        [Fact]
        public void Status_SerialisesWithThePageContract()
        {
            HubService hub = NewHub();
            hub.AddRememberedForTests(DeskAddress, "KliveTech Desk", "desk-id");
            JObject json = JObject.Parse(JsonConvert.SerializeObject(hub.BuildHubStatus()));

            foreach (string key in new[] { "serverUtc", "startedUtc", "bluetooth", "enrolment", "remembered", "nearby", "relay", "firmware", "streamables", "events" })
            {
                Assert.True(json.ContainsKey(key), key);
            }
            Assert.Equal("not-tried", json["remembered"]![0]!["state"]!.Value<string>());
            Assert.False(json["relay"]!["tokenConfigured"]!.Value<bool>());
            Assert.Equal("/klivetech/hub/connect", json["relay"]!["endpoint"]!.Value<string>());
            Assert.Equal("KliveTechAllowNewDevices", json["enrolment"]!["setting"]!.Value<string>());
            Assert.Equal(32, json["streamables"]!["maximumLiveViewers"]!.Value<int>());
        }

        [Theory]
        [InlineData(SocketError.TimedOut, "no answer")]
        [InlineData(SocketError.ConnectionRefused, "refused")]
        [InlineData(SocketError.HostDown, "unreachable")]
        [InlineData(SocketError.ConnectionReset, "dropped")]
        public void ConnectionErrors_SayWhatTheyMean(SocketError code, string expected)
        {
            Assert.Contains(expected, HubService.DescribeConnectionError(new SocketException((int)code)));
            Assert.Contains(expected, HubService.DescribeConnectionError(new IOException("wrapped", new SocketException((int)code))));
        }

        [Fact]
        public void OtherErrors_KeepTheirMessage()
        {
            Assert.Equal("timed out", HubService.DescribeConnectionError(new TimeoutException("x")));
            Assert.Equal(
                "The Bluetooth device no longer advertises a KliveTech name.",
                HubService.DescribeConnectionError(new InvalidDataException("The Bluetooth device no longer advertises a KliveTech name.")));
        }

        [Fact]
        public void Addresses_PrintLikeTheLabelOnTheDevice()
        {
            Assert.Equal("0A:1B:2C:3D:4E:5F", HubService.FormatAddress("0a1b2c3d4e5f"));
            Assert.Equal("0A:1B:2C:3D:4E:5F", HubService.FormatAddress("0A:1B:2C:3D:4E:5F"));
            Assert.Equal("ABC", HubService.FormatAddress("abc"));
        }

        [Fact]
        public void ActionHistoryLines_ShowWhatRan()
        {
            KliveTechActions.KliveTechAction toggle = new() { name = "Set light", parameters = KliveTechActions.ActionParameterType.Bool };
            KliveTechActions.KliveTechAction say = new() { name = "Say", parameters = KliveTechActions.ActionParameterType.String };
            KliveTechActions.KliveTechAction reboot = new() { name = "Reboot", parameters = KliveTechActions.ActionParameterType.None };

            Assert.Equal("Set light → true", KliveTechRoutes.DescribeActionRun(toggle, "true"));
            Assert.Equal("Say → \"hello\"", KliveTechRoutes.DescribeActionRun(say, "hello"));
            Assert.Equal("Reboot", KliveTechRoutes.DescribeActionRun(reboot, "ignored"));
            Assert.EndsWith("…\"", KliveTechRoutes.DescribeActionRun(say, new string('x', 80)));
        }

        [Fact]
        public async Task MissingToolchain_IsReportedBeforeAnyBuild()
        {
            HubService.ToolchainStatus status = await HubService.ProbeToolchainAsync("klivetech-no-such-arduino-cli-4f1c");
            Assert.False(status.found);
            Assert.Contains("KliveTechArduinoCliPath", status.error);
        }
    }
}
