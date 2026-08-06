using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Omnipotent.Services.KliveTechHub
{
    internal enum KliveTechStreamEventKind
    {
        Manifest,
        Sample,
        FrameChunk
    }

    internal sealed class KliveTechParsedStreamDefinition
    {
        internal required string StreamID { get; init; }
        internal required string ValueType { get; init; }
        internal required string MimeType { get; init; }
        internal required string Mode { get; init; }
        internal required uint IntervalMs { get; init; }
        internal required bool Enabled { get; init; }
    }

    internal sealed class KliveTechParsedStreamEvent
    {
        internal required KliveTechStreamEventKind Kind { get; init; }
        internal required string SessionID { get; init; }
        internal ulong Revision { get; init; }
        internal IReadOnlyList<KliveTechParsedStreamDefinition> Definitions { get; init; } =
            Array.Empty<KliveTechParsedStreamDefinition>();
        internal string StreamID { get; init; } = string.Empty;
        internal ulong Sequence { get; init; }
        internal ulong TimestampMs { get; init; }
        internal JToken? Value { get; init; }
        internal string MimeType { get; init; } = string.Empty;
        internal int FrameSize { get; init; }
        internal string FrameSha256 { get; init; } = string.Empty;
        internal int ChunkIndex { get; init; }
        internal int ChunkCount { get; init; }
        internal int ChunkOffset { get; init; }
        internal byte[] ChunkData { get; init; } = Array.Empty<byte>();
    }

    internal static class KliveTechStreamProtocol
    {
        internal const int CurrentVersion = 1;
        internal const int MaximumEventBytes = 8 * 1024;
        internal const int MaximumStreamablesPerGadget = 32;
        internal const int MaximumStreamIdLength = 48;
        internal const int MaximumMimeTypeLength = 96;
        internal const int MaximumJsonValueBytes = 4 * 1024;
        internal const int BinaryChunkBytes = 1024;
        internal const int MaximumBinaryFrameBytes = 512 * 1024;
        internal const int MinimumStreamIntervalMs = 25;
        internal const int MaximumJsonDepth = 32;

        private static readonly UTF8Encoding StrictUtf8 = new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

        internal static bool IsStreamEvent(JObject value)
        {
            string? eventName = value.Value<string>("EVENT");
            return eventName is "StreamManifest" or "StreamSample" or "StreamFrame";
        }

        internal static bool TryParseEvent(
            JObject value,
            out KliveTechParsedStreamEvent? parsed,
            out string error)
        {
            parsed = null;
            error = string.Empty;
            if (Encoding.UTF8.GetByteCount(value.ToString(Formatting.None)) > MaximumEventBytes)
            {
                error = $"Stream event exceeded {MaximumEventBytes} UTF-8 bytes.";
                return false;
            }

            string eventName = value.Value<string>("EVENT") ?? string.Empty;
            if (eventName is not ("StreamManifest" or "StreamSample" or "StreamFrame"))
            {
                error = "Unsupported Streamables event.";
                return false;
            }
            if (value.Value<int?>("VERSION") != CurrentVersion)
            {
                error = "Unsupported Streamables protocol version.";
                return false;
            }
            if (!TryGetHex(value, "SESSIONID", 32, out string sessionId))
            {
                error = "SESSIONID must contain exactly 32 hexadecimal characters.";
                return false;
            }

            return eventName switch
            {
                "StreamManifest" => TryParseManifest(value, sessionId, out parsed, out error),
                "StreamSample" => TryParseSample(value, sessionId, out parsed, out error),
                "StreamFrame" => TryParseFrameChunk(value, sessionId, out parsed, out error),
                _ => false
            };
        }

        private static bool TryParseManifest(
            JObject value,
            string sessionId,
            out KliveTechParsedStreamEvent? parsed,
            out string error)
        {
            parsed = null;
            error = string.Empty;
            if (!TryGetUnsigned(value, "REVISION", out ulong revision) ||
                value["STREAMABLES"] is not JArray streamables ||
                streamables.Count > MaximumStreamablesPerGadget)
            {
                error = $"Manifest must contain REVISION and no more than " +
                    $"{MaximumStreamablesPerGadget} STREAMABLES.";
                return false;
            }

            HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
            List<KliveTechParsedStreamDefinition> definitions = new(streamables.Count);
            foreach (JToken item in streamables)
            {
                if (item is not JObject definition ||
                    !TryParseDefinition(definition, out KliveTechParsedStreamDefinition? parsedDefinition, out error) ||
                    parsedDefinition == null)
                {
                    return false;
                }
                if (!ids.Add(parsedDefinition.StreamID))
                {
                    error = $"Manifest contains duplicate stream ID '{parsedDefinition.StreamID}'.";
                    return false;
                }
                definitions.Add(parsedDefinition);
            }

            parsed = new KliveTechParsedStreamEvent
            {
                Kind = KliveTechStreamEventKind.Manifest,
                SessionID = sessionId,
                Revision = revision,
                Definitions = definitions
            };
            return true;
        }

        internal static bool TryParseDefinition(
            JObject value,
            out KliveTechParsedStreamDefinition? definition,
            out string error)
        {
            definition = null;
            error = string.Empty;
            string streamId = value.Value<string>("ID") ?? string.Empty;
            if (!IsValidStreamId(streamId))
            {
                error = "Stream ID must be 1-48 ASCII letters, digits, '.', '_' or '-'.";
                return false;
            }

            string valueType = value.Value<string>("VALUETYPE") ?? string.Empty;
            if (valueType is not ("integer" or "number" or "boolean" or "string" or "json" or "binary"))
            {
                error = $"Stream '{streamId}' has an unsupported VALUETYPE.";
                return false;
            }
            string mode = value.Value<string>("MODE") ?? string.Empty;
            if (mode is not ("periodic" or "onChange" or "manual"))
            {
                error = $"Stream '{streamId}' has an unsupported MODE.";
                return false;
            }
            if (!TryGetUnsigned(value, "INTERVALMS", out ulong intervalValue) || intervalValue > uint.MaxValue)
            {
                error = $"Stream '{streamId}' has an invalid INTERVALMS.";
                return false;
            }
            uint intervalMs = (uint)intervalValue;
            if (mode != "manual" && intervalMs < MinimumStreamIntervalMs)
            {
                error = $"Stream '{streamId}' interval is below {MinimumStreamIntervalMs} ms.";
                return false;
            }
            if (value["ENABLED"]?.Type != JTokenType.Boolean)
            {
                error = $"Stream '{streamId}' must contain a boolean ENABLED value.";
                return false;
            }

            string mimeType = value.Value<string>("MIMETYPE") ?? string.Empty;
            if (valueType == "binary")
            {
                if (!IsValidMimeType(mimeType))
                {
                    error = $"Stream '{streamId}' has an invalid MIMETYPE.";
                    return false;
                }
            }
            else if (!string.IsNullOrEmpty(mimeType) && !IsValidMimeType(mimeType))
            {
                error = $"Stream '{streamId}' has an invalid MIMETYPE.";
                return false;
            }

            definition = new KliveTechParsedStreamDefinition
            {
                StreamID = streamId,
                ValueType = valueType,
                MimeType = mimeType,
                Mode = mode,
                IntervalMs = intervalMs,
                Enabled = value.Value<bool>("ENABLED")
            };
            return true;
        }

        private static bool TryParseSample(
            JObject value,
            string sessionId,
            out KliveTechParsedStreamEvent? parsed,
            out string error)
        {
            parsed = null;
            error = string.Empty;
            if (!TryGetCommonSampleFields(value, out string streamId, out ulong sequence, out ulong timestampMs, out error))
            {
                return false;
            }
            if (!string.Equals(value.Value<string>("ENCODING"), "base64-json", StringComparison.Ordinal) ||
                value["DATA"]?.Type != JTokenType.String)
            {
                error = "StreamSample ENCODING must be base64-json and DATA must be a string.";
                return false;
            }

            string encoded = value.Value<string>("DATA")!;
            if (!TryDecodeBase64(encoded, MaximumJsonValueBytes, out byte[] bytes, out error))
            {
                return false;
            }

            JToken sampleValue;
            try
            {
                string json = StrictUtf8.GetString(bytes);
                using JsonTextReader reader = new(new StringReader(json))
                {
                    MaxDepth = MaximumJsonDepth,
                    DateParseHandling = DateParseHandling.None
                };
                sampleValue = JToken.ReadFrom(reader, new JsonLoadSettings
                {
                    CommentHandling = CommentHandling.Ignore,
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                    LineInfoHandling = LineInfoHandling.Ignore
                });
                if (reader.Read())
                {
                    error = "Decoded StreamSample DATA contains trailing JSON.";
                    return false;
                }
            }
            catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
            {
                error = "Decoded StreamSample DATA is not valid bounded UTF-8 JSON.";
                return false;
            }

            parsed = new KliveTechParsedStreamEvent
            {
                Kind = KliveTechStreamEventKind.Sample,
                SessionID = sessionId,
                StreamID = streamId,
                Sequence = sequence,
                TimestampMs = timestampMs,
                Value = sampleValue
            };
            return true;
        }

        private static bool TryParseFrameChunk(
            JObject value,
            string sessionId,
            out KliveTechParsedStreamEvent? parsed,
            out string error)
        {
            parsed = null;
            error = string.Empty;
            if (!TryGetCommonSampleFields(value, out string streamId, out ulong sequence, out ulong timestampMs, out error))
            {
                return false;
            }
            if (!string.Equals(value.Value<string>("ENCODING"), "base64", StringComparison.Ordinal) ||
                value["DATA"]?.Type != JTokenType.String)
            {
                error = "StreamFrame ENCODING must be base64 and DATA must be a string.";
                return false;
            }
            string mimeType = value.Value<string>("MIMETYPE") ?? string.Empty;
            if (!IsValidMimeType(mimeType))
            {
                error = "StreamFrame MIMETYPE is invalid.";
                return false;
            }
            if (!TryGetPositiveInt(value, "FRAMESIZE", MaximumBinaryFrameBytes, out int frameSize) ||
                !TryGetHex(value, "FRAMESHA256", 64, out string frameSha256) ||
                !TryGetNonNegativeInt(value, "CHUNKINDEX", out int chunkIndex) ||
                !TryGetPositiveInt(value, "CHUNKCOUNT", MaximumBinaryFrameBytes, out int chunkCount) ||
                !TryGetNonNegativeInt(value, "CHUNKOFFSET", out int chunkOffset) ||
                !TryGetPositiveInt(value, "CHUNKSIZE", BinaryChunkBytes, out int chunkSize) ||
                !TryGetHex(value, "CHUNKSHA256", 64, out string chunkSha256))
            {
                error = "StreamFrame contains invalid frame or chunk metadata.";
                return false;
            }

            int expectedChunkCount = checked((frameSize + BinaryChunkBytes - 1) / BinaryChunkBytes);
            long expectedOffset = (long)chunkIndex * BinaryChunkBytes;
            if (chunkCount != expectedChunkCount || chunkIndex >= chunkCount ||
                expectedOffset != chunkOffset || chunkOffset >= frameSize ||
                chunkSize != Math.Min(BinaryChunkBytes, frameSize - chunkOffset))
            {
                error = "StreamFrame chunk layout is inconsistent with FRAMESIZE.";
                return false;
            }

            if (!TryDecodeBase64(value.Value<string>("DATA")!, BinaryChunkBytes, out byte[] chunkData, out error) ||
                chunkData.Length != chunkSize)
            {
                if (string.IsNullOrEmpty(error))
                {
                    error = "Decoded StreamFrame DATA length does not match CHUNKSIZE.";
                }
                return false;
            }
            string calculatedChunkHash = Convert.ToHexString(SHA256.HashData(chunkData));
            if (!string.Equals(calculatedChunkHash, chunkSha256, StringComparison.OrdinalIgnoreCase))
            {
                error = "StreamFrame chunk SHA-256 verification failed.";
                return false;
            }

            parsed = new KliveTechParsedStreamEvent
            {
                Kind = KliveTechStreamEventKind.FrameChunk,
                SessionID = sessionId,
                StreamID = streamId,
                Sequence = sequence,
                TimestampMs = timestampMs,
                MimeType = mimeType,
                FrameSize = frameSize,
                FrameSha256 = frameSha256,
                ChunkIndex = chunkIndex,
                ChunkCount = chunkCount,
                ChunkOffset = chunkOffset,
                ChunkData = chunkData
            };
            return true;
        }

        private static bool TryGetCommonSampleFields(
            JObject value,
            out string streamId,
            out ulong sequence,
            out ulong timestampMs,
            out string error)
        {
            streamId = value.Value<string>("STREAMID") ?? string.Empty;
            sequence = 0;
            timestampMs = 0;
            error = string.Empty;
            if (!IsValidStreamId(streamId) ||
                !TryGetUnsigned(value, "SEQUENCE", out sequence) ||
                !TryGetUnsigned(value, "TIMESTAMPMS", out timestampMs))
            {
                error = "Stream event must contain a valid STREAMID, SEQUENCE and TIMESTAMPMS.";
                return false;
            }
            return true;
        }

        private static bool TryDecodeBase64(
            string encoded,
            int maximumDecodedBytes,
            out byte[] data,
            out string error)
        {
            data = Array.Empty<byte>();
            error = string.Empty;
            int maximumEncodedCharacters = checked(((maximumDecodedBytes + 2) / 3) * 4);
            if (encoded.Length == 0 || encoded.Length > maximumEncodedCharacters)
            {
                error = $"Base64 payload exceeds the {maximumDecodedBytes}-byte decoded limit.";
                return false;
            }
            try
            {
                data = Convert.FromBase64String(encoded);
                if (data.Length == 0 || data.Length > maximumDecodedBytes)
                {
                    data = Array.Empty<byte>();
                    error = $"Base64 payload exceeds the {maximumDecodedBytes}-byte decoded limit.";
                    return false;
                }
                return true;
            }
            catch (FormatException)
            {
                error = "Payload is not valid base64.";
                return false;
            }
        }

        private static bool TryGetUnsigned(JObject value, string propertyName, out ulong result)
        {
            result = 0;
            JToken? token = value[propertyName];
            if (token?.Type != JTokenType.Integer)
            {
                return false;
            }
            try
            {
                result = token.Value<ulong>();
                return true;
            }
            catch (Exception ex) when (ex is OverflowException or FormatException)
            {
                return false;
            }
        }

        private static bool TryGetPositiveInt(
            JObject value,
            string propertyName,
            int maximum,
            out int result)
        {
            return TryGetNonNegativeInt(value, propertyName, out result) && result > 0 && result <= maximum;
        }

        private static bool TryGetNonNegativeInt(JObject value, string propertyName, out int result)
        {
            result = -1;
            JToken? token = value[propertyName];
            if (token?.Type != JTokenType.Integer)
            {
                return false;
            }
            try
            {
                long parsed = token.Value<long>();
                if (parsed < 0 || parsed > int.MaxValue)
                {
                    return false;
                }
                result = (int)parsed;
                return true;
            }
            catch (Exception ex) when (ex is OverflowException or FormatException)
            {
                return false;
            }
        }

        private static bool TryGetHex(
            JObject value,
            string propertyName,
            int exactCharacters,
            out string normalized)
        {
            normalized = value.Value<string>(propertyName) ?? string.Empty;
            if (normalized.Length != exactCharacters || normalized.Any(character => !Uri.IsHexDigit(character)))
            {
                normalized = string.Empty;
                return false;
            }
            normalized = normalized.ToUpperInvariant();
            return true;
        }

        internal static bool IsValidStreamId(string value)
        {
            return value.Length is > 0 and <= MaximumStreamIdLength &&
                IsAsciiAlphaNumeric(value[0]) &&
                value.Skip(1).All(character => IsAsciiAlphaNumeric(character) || character is '.' or '_' or '-');
        }

        internal static bool IsValidMimeType(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumMimeTypeLength ||
                value.Contains('\r') || value.Contains('\n') || value.Contains(';'))
            {
                return false;
            }
            int slash = value.IndexOf('/');
            return slash > 0 && slash < value.Length - 1 && slash == value.LastIndexOf('/') &&
                value.All(character => IsAsciiAlphaNumeric(character) || character is '/' or '.' or '+' or '-');
        }

        private static bool IsAsciiAlphaNumeric(char character)
        {
            return character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
        }
    }
}
