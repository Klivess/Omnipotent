using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveTechHub;

namespace Omnipotent.Tests.KliveTechHub
{
    public sealed class KliveTechRelayProtocolTests
    {
        [Theory]
        [InlineData("AA:BB:CC:DD:EE:FF", "AABBCCDDEEFF")]
        [InlineData("aa-bb-cc-dd-ee-ff", "AABBCCDDEEFF")]
        [InlineData("AABBCCDDEEFF", "AABBCCDDEEFF")]
        public void TryNormalizeDeviceId_NormalizesEsp32Addresses(string value, string expected)
        {
            Assert.True(KliveTechRelayProtocol.TryNormalizeDeviceId(value, out string normalized));
            Assert.Equal(expected, normalized);
        }

        [Theory]
        [InlineData("")]
        [InlineData("AABB")]
        [InlineData("AABBCCDDEEFG")]
        [InlineData("AABBCCDDEEFF00")]
        public void TryNormalizeDeviceId_RejectsInvalidIdentifiers(string value)
        {
            Assert.False(KliveTechRelayProtocol.TryNormalizeDeviceId(value, out _));
        }

        [Fact]
        public void TryParseHello_ValidatesAndNormalizesHub()
        {
            const string json = """
                {
                  "Type":"hello",
                  "Protocol":2,
                  "HubId":"aa:bb:cc:dd:ee:ff",
                  "HubName":" Kitchen Hub ",
                  "Token":"secret",
                  "Hub":{"DeviceId":"ignored","Name":""}
                }
                """;

            Assert.True(KliveTechRelayProtocol.TryParseHello(json, out var hello, out string error), error);
            Assert.NotNull(hello);
            Assert.Equal("AABBCCDDEEFF", hello.HubId);
            Assert.Equal("Kitchen Hub", hello.HubName);
            Assert.Equal("Kitchen Hub", hello.Hub!.Name);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        public void TryParseHello_RejectsUnsupportedVersions(int version)
        {
            string json = $$"""
                {"Type":"hello","Protocol":{{version}},"HubId":"AABBCCDDEEFF","HubName":"Hub","Token":"secret"}
                """;

            Assert.False(KliveTechRelayProtocol.TryParseHello(json, out _, out _));
        }

        [Fact]
        public void TokensEqual_RequiresAnExactCaseSensitiveMatch()
        {
            Assert.True(KliveTechRelayProtocol.TokensEqual("Abc123", "Abc123"));
            Assert.False(KliveTechRelayProtocol.TokensEqual("Abc123", "abc123"));
            Assert.False(KliveTechRelayProtocol.TokensEqual("Abc123", "Abc1234"));
        }

        [Fact]
        public void SerializeCommand_EmbedsTypedPayload()
        {
            var payload = new { ID = 42, DATA = "Ping", OP = 2, RESPEXPECT = true };

            JObject command = JObject.Parse(KliveTechRelayProtocol.SerializeCommand(
                "AABBCCDDEEFF",
                "001122334455",
                payload));

            Assert.Equal("command", command.Value<string>("Type"));
            Assert.Equal("AABBCCDDEEFF", command.Value<string>("HubId"));
            Assert.Equal("001122334455", command.Value<string>("DeviceId"));
            Assert.Equal(JTokenType.Object, command["Payload"]!.Type);
            Assert.Equal(42, command["Payload"]!.Value<int>("ID"));
        }

        [Fact]
        public void TryGetStreamPayload_ValidatesAndNormalizesRelayedStreamEvent()
        {
            JObject envelope = JObject.Parse("""
                {
                  "Type":"stream",
                  "HubId":"AABBCCDDEEFF",
                  "DeviceId":"00:11:22:33:44:55",
                  "Payload":{
                    "EVENT":"StreamManifest",
                    "VERSION":1,
                    "SESSIONID":"00112233445566778899AABBCCDDEEFF",
                    "REVISION":1,
                    "STREAMABLES":[]
                  }
                }
                """);

            Assert.True(KliveTechRelayProtocol.TryGetStreamPayload(
                envelope,
                out string deviceId,
                out JObject? payload));
            Assert.Equal("001122334455", deviceId);
            Assert.Equal("StreamManifest", payload!.Value<string>("EVENT"));
        }

        [Fact]
        public void TryGetStreamPayload_RejectsNonStreamPayloads()
        {
            JObject envelope = JObject.Parse("""
                {"Type":"response","DeviceId":"001122334455","Payload":{"ID":4}}
                """);

            Assert.False(KliveTechRelayProtocol.TryGetStreamPayload(envelope, out _, out _));
        }
    }
}
