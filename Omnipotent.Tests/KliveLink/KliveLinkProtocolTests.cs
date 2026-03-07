using Newtonsoft.Json;
using Omnipotent.Services.KliveLink;

namespace Omnipotent.Tests.KliveLink
{
    public class KliveLinkProtocolTests
    {
        #region KliveLinkMessage Serialization

        [Fact]
        public void Serialize_ProducesValidJson()
        {
            var message = new KliveLinkMessage
            {
                Command = KliveLinkCommandType.Ping,
                Payload = "test",
            };

            string json = message.Serialize();
            Assert.False(string.IsNullOrWhiteSpace(json));
            Assert.Contains("Ping", json);
        }

        [Fact]
        public void Deserialize_ValidJson_ReturnsMessage()
        {
            var original = new KliveLinkMessage
            {
                Command = KliveLinkCommandType.Heartbeat,
                Payload = "hello",
            };

            string json = original.Serialize();
            var deserialized = KliveLinkMessage.Deserialize(json);

            Assert.NotNull(deserialized);
            Assert.Equal(KliveLinkCommandType.Heartbeat, deserialized!.Command);
            Assert.Equal("hello", deserialized.Payload);
            Assert.Equal(original.MessageId, deserialized.MessageId);
        }

        [Fact]
        public void Deserialize_InvalidJson_ThrowsJsonException()
        {
            Assert.ThrowsAny<JsonException>(() => KliveLinkMessage.Deserialize("not json"));
        }

        [Fact]
        public void RoundTrip_PreservesAllProperties()
        {
            var original = new KliveLinkMessage
            {
                Command = KliveLinkCommandType.RunTerminalCommand,
                Payload = JsonConvert.SerializeObject(new TerminalCommandPayload
                {
                    Command = "dir",
                    TimeoutSeconds = 15
                }),
                ReplyToMessageId = "abc123",
            };

            string json = original.Serialize();
            var deserialized = KliveLinkMessage.Deserialize(json);

            Assert.NotNull(deserialized);
            Assert.Equal(original.MessageId, deserialized!.MessageId);
            Assert.Equal(original.ReplyToMessageId, deserialized.ReplyToMessageId);
            Assert.Equal(original.Command, deserialized.Command);
            Assert.Equal(original.Payload, deserialized.Payload);
        }

        #endregion

        #region KliveLinkMessage Defaults

        [Fact]
        public void NewMessage_HasNonEmptyMessageId()
        {
            var message = new KliveLinkMessage();
            Assert.False(string.IsNullOrWhiteSpace(message.MessageId));
        }

        [Fact]
        public void NewMessage_HasRecentTimestamp()
        {
            var before = DateTime.UtcNow.AddSeconds(-1);
            var message = new KliveLinkMessage();
            var after = DateTime.UtcNow.AddSeconds(1);

            Assert.InRange(message.Timestamp, before, after);
        }

        [Fact]
        public void NewMessage_ReplyToMessageId_IsNull()
        {
            var message = new KliveLinkMessage();
            Assert.Null(message.ReplyToMessageId);
        }

        #endregion

        #region KliveLinkCommandType Enum

        [Theory]
        [InlineData(KliveLinkCommandType.Ping)]
        [InlineData(KliveLinkCommandType.Pong)]
        [InlineData(KliveLinkCommandType.Heartbeat)]
        [InlineData(KliveLinkCommandType.HeartbeatAck)]
        [InlineData(KliveLinkCommandType.RunProcess)]
        [InlineData(KliveLinkCommandType.RunTerminalCommand)]
        [InlineData(KliveLinkCommandType.Error)]
        [InlineData(KliveLinkCommandType.SelfDestruct)]
        public void KliveLinkCommandType_AllValuesAreDefined(KliveLinkCommandType commandType)
        {
            Assert.True(Enum.IsDefined(typeof(KliveLinkCommandType), commandType));
        }

        #endregion

        #region Payload Types

        [Fact]
        public void SystemInfoPayload_DefaultsAreEmpty()
        {
            var payload = new SystemInfoPayload();
            Assert.Equal("", payload.MachineName);
            Assert.Equal("", payload.OSVersion);
            Assert.Equal("", payload.UserName);
            Assert.Equal(0, payload.ProcessorCount);
            Assert.Equal(0, payload.TotalMemoryMB);
            Assert.Equal("", payload.AgentVersion);
        }

        [Fact]
        public void RunProcessPayload_Defaults()
        {
            var payload = new RunProcessPayload();
            Assert.Equal("", payload.FileName);
            Assert.Equal("", payload.Arguments);
            Assert.False(payload.WaitForExit);
            Assert.Equal(30, payload.TimeoutSeconds);
        }

        [Fact]
        public void TerminalCommandPayload_Defaults()
        {
            var payload = new TerminalCommandPayload();
            Assert.Equal("", payload.Command);
            Assert.Equal(30, payload.TimeoutSeconds);
        }

        [Fact]
        public void RunProcessResultPayload_Defaults()
        {
            var payload = new RunProcessResultPayload();
            Assert.Equal("", payload.StandardOutput);
            Assert.Equal("", payload.StandardError);
            Assert.False(payload.TimedOut);
            Assert.Null(payload.ExitCode);
        }

        [Fact]
        public void TerminalCommandResultPayload_Defaults()
        {
            var payload = new TerminalCommandResultPayload();
            Assert.Equal(0, payload.ExitCode);
            Assert.Equal("", payload.Output);
            Assert.Equal("", payload.Error);
            Assert.False(payload.TimedOut);
        }

        #endregion

        #region JSON Serialization with Command Enum

        [Fact]
        public void Serialize_CommandEnumAsString()
        {
            var message = new KliveLinkMessage
            {
                Command = KliveLinkCommandType.GetSystemInfo,
            };

            string json = message.Serialize();
            // KliveLinkCommandType has StringEnumConverter, so it should serialize as string
            Assert.Contains("GetSystemInfo", json);
        }

        #endregion
    }
}
