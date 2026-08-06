using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Omnipotent.Services.KliveTechHub
{
    internal static class KliveTechRelayProtocol
    {
        internal const int CurrentVersion = 2;
        internal const int MaximumMessageBytes = 64 * 1024;
        internal const int MaximumDeviceMessageBytes = 8 * 1024;
        internal const string WebSocketRoute = "/klivetech/hub/connect";

        internal sealed class GadgetDescriptor
        {
            public string DeviceId { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
        }

        internal sealed class HelloMessage
        {
            public string Type { get; set; } = string.Empty;
            public int Protocol { get; set; }
            public string HubId { get; set; } = string.Empty;
            public string HubName { get; set; } = string.Empty;
            public string Token { get; set; } = string.Empty;
            public GadgetDescriptor? Hub { get; set; }
        }

        internal static bool TryParseHello(
            string json,
            out HelloMessage? hello,
            out string error)
        {
            hello = null;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaximumMessageBytes)
            {
                error = "The hello message is empty or too large.";
                return false;
            }

            try
            {
                hello = JsonConvert.DeserializeObject<HelloMessage>(json);
            }
            catch (JsonException)
            {
                error = "The hello message is not valid JSON.";
                return false;
            }

            if (hello == null || !string.Equals(hello.Type, "hello", StringComparison.Ordinal) ||
                hello.Protocol != CurrentVersion)
            {
                error = "The hub protocol version is unsupported.";
                return false;
            }

            if (!TryNormalizeDeviceId(hello.HubId, out string normalizedHubId))
            {
                error = "The hub device ID is invalid.";
                return false;
            }
            hello.HubId = normalizedHubId;
            hello.HubName = NormalizeName(hello.HubName, $"KliveTech-Hub-{normalizedHubId}");
            if (hello.Hub != null)
            {
                hello.Hub.DeviceId = normalizedHubId;
                hello.Hub.Name = NormalizeName(hello.Hub.Name, hello.HubName);
            }
            return true;
        }

        internal static bool TryParseEnvelope(string json, out JObject? envelope)
        {
            envelope = null;
            if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaximumMessageBytes)
            {
                return false;
            }
            try
            {
                envelope = JObject.Parse(json);
                return envelope["Type"]?.Type == JTokenType.String;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        internal static string SerializeCommand(string hubId, string deviceId, object payload)
        {
            return JsonConvert.SerializeObject(new
            {
                Type = "command",
                HubId = hubId,
                DeviceId = deviceId,
                Payload = payload
            });
        }

        internal static bool TryGetStreamPayload(
            JObject envelope,
            out string deviceId,
            out JObject? payload)
        {
            deviceId = string.Empty;
            payload = null;
            if (!string.Equals(envelope.Value<string>("Type"), "stream", StringComparison.Ordinal) ||
                !TryNormalizeDeviceId(envelope.Value<string>("DeviceId"), out deviceId) ||
                envelope["Payload"] is not JObject streamPayload ||
                Encoding.UTF8.GetByteCount(streamPayload.ToString(Formatting.None)) >
                    MaximumDeviceMessageBytes ||
                !KliveTechStreamProtocol.IsStreamEvent(streamPayload))
            {
                deviceId = string.Empty;
                return false;
            }
            payload = streamPayload;
            return true;
        }

        internal static bool TryNormalizeDeviceId(string? value, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            StringBuilder builder = new(12);
            foreach (char character in value)
            {
                if (character is ':' or '-' or ' ')
                {
                    continue;
                }
                if (!Uri.IsHexDigit(character) || builder.Length == 12)
                {
                    return false;
                }
                builder.Append(char.ToUpperInvariant(character));
            }
            if (builder.Length != 12)
            {
                return false;
            }
            normalized = builder.ToString();
            return true;
        }

        internal static string NormalizeName(string? value, string fallback)
        {
            string candidate = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            if (candidate.Length > 128)
            {
                candidate = candidate[..128];
            }
            return candidate;
        }

        internal static bool TokensEqual(string expected, string supplied)
        {
            byte[] expectedBytes = Encoding.UTF8.GetBytes(expected ?? string.Empty);
            byte[] suppliedBytes = Encoding.UTF8.GetBytes(supplied ?? string.Empty);
            return expectedBytes.Length == suppliedBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
        }
    }
}
