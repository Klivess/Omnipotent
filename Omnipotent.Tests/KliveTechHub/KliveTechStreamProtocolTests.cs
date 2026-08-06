using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveTechHub;
using System.Security.Cryptography;
using System.Text;
using HubService = Omnipotent.Services.KliveTechHub.KliveTechHub;

namespace Omnipotent.Tests.KliveTechHub
{
    public sealed class KliveTechStreamProtocolTests
    {
        private const string Session = "00112233445566778899AABBCCDDEEFF";

        [Fact]
        public void ManifestParser_AcceptsExactProducerSchema()
        {
            JObject manifest = BuildManifest(
                revision: 4,
                Definition("temperature", "number", "application/json", "onChange", 1000, true),
                Definition("doorOpen", "boolean", "application/json", "periodic", 250, true),
                Definition("camera", "binary", "image/jpeg", "manual", 0, false));

            Assert.True(
                KliveTechStreamProtocol.TryParseEvent(manifest, out var parsed, out string error),
                error);
            Assert.NotNull(parsed);
            Assert.Equal(KliveTechStreamEventKind.Manifest, parsed.Kind);
            Assert.Equal(Session, parsed.SessionID);
            Assert.Equal((ulong)4, parsed.Revision);
            Assert.Equal(3, parsed.Definitions.Count);
            Assert.Equal("boolean", parsed.Definitions[1].ValueType);
            Assert.Equal("onChange", parsed.Definitions[0].Mode);
        }

        [Theory]
        [InlineData("_hidden")]
        [InlineData("-camera")]
        [InlineData("bad/id")]
        [InlineData("bad id")]
        public void ManifestParser_RejectsDeviceInvalidStreamIds(string streamId)
        {
            JObject manifest = BuildManifest(
                revision: 1,
                Definition(streamId, "number", "application/json", "onChange", 1000, true));

            Assert.False(KliveTechStreamProtocol.TryParseEvent(manifest, out _, out _));
        }

        [Fact]
        public void ManifestParser_RejectsIdsThatDifferOnlyByCase()
        {
            JObject manifest = BuildManifest(
                revision: 1,
                Definition("Camera", "binary", "image/jpeg", "manual", 0, true),
                Definition("camera", "binary", "image/jpeg", "manual", 0, true));

            Assert.False(KliveTechStreamProtocol.TryParseEvent(manifest, out _, out string error));
            Assert.Contains("duplicate", error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void SampleParser_DecodesOneCompleteJsonValue()
        {
            JObject sample = BuildSample("temperature", 8, "{\"celsius\":21.5}");

            Assert.True(
                KliveTechStreamProtocol.TryParseEvent(sample, out var parsed, out string error),
                error);
            Assert.Equal(21.5, parsed!.Value!["celsius"]!.Value<double>());

            sample["DATA"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("true false"));
            Assert.False(KliveTechStreamProtocol.TryParseEvent(sample, out _, out _));
        }

        [Fact]
        public void FrameParser_VerifiesChunkHashAndExactLayout()
        {
            byte[] frame = Enumerable.Range(0, 1500).Select(index => (byte)(index % 251)).ToArray();
            JObject first = BuildFrameChunk("camera", 10, frame, chunkIndex: 0);

            Assert.True(
                KliveTechStreamProtocol.TryParseEvent(first, out var parsed, out string error),
                error);
            Assert.Equal(1024, parsed!.ChunkData.Length);
            Assert.Equal(2, parsed.ChunkCount);

            first["CHUNKOFFSET"] = 1;
            Assert.False(KliveTechStreamProtocol.TryParseEvent(first, out _, out _));
        }

        [Fact]
        public void State_ReassemblesOutOfOrderFrameAndReturnsDefensiveCopy()
        {
            HubService service = new();
            service.ResetStreamablesForTests();
            HubService.KliveTechGadget gadget = Gadget();
            Assert.True(service.AcceptStreamEventForTests(
                gadget,
                BuildManifest(1, Definition("camera", "binary", "image/jpeg", "manual", 0, true)),
                out _));
            byte[] frame = Enumerable.Range(0, 2500).Select(index => (byte)(index % 239)).ToArray();

            Assert.True(service.AcceptStreamEventForTests(gadget, BuildFrameChunk("camera", 12, frame, 2), out _));
            Assert.True(service.AcceptStreamEventForTests(gadget, BuildFrameChunk("camera", 12, frame, 0), out _));
            Assert.Null(service.GetLatestStreamableBinary(gadget.gadgetID, "camera"));
            Assert.True(service.AcceptStreamEventForTests(gadget, BuildFrameChunk("camera", 12, frame, 1), out _));

            HubService.StreamableBinaryData first =
                Assert.IsType<HubService.StreamableBinaryData>(
                    service.GetLatestStreamableBinary(gadget.gadgetID, "CAMERA"));
            Assert.Equal(frame, first.Data);
            first.Data[0] ^= 0xFF;
            HubService.StreamableBinaryData second = service.GetLatestStreamableBinary(
                gadget.gadgetID.ToLowerInvariant(),
                "camera")!;
            Assert.Equal(frame[0], second.Data[0]);
        }

        [Fact]
        public void State_DropsDuplicateSequencesAndBoundsPerStreamHistory()
        {
            HubService service = new();
            service.ResetStreamablesForTests();
            HubService.KliveTechGadget gadget = Gadget();
            service.AcceptStreamEventForTests(
                gadget,
                BuildManifest(1, Definition("counter", "integer", "application/json", "periodic", 25, true)),
                out _);

            for (ulong sequence = 1; sequence <= 520; sequence++)
            {
                Assert.True(service.AcceptStreamEventForTests(
                    gadget,
                    BuildSample("counter", sequence, sequence.ToString()),
                    out _));
            }
            service.AcceptStreamEventForTests(gadget, BuildSample("counter", 520, "9999"), out _);

            IReadOnlyList<HubService.StreamableSample> history = service.GetStreamableHistory(
                gadget.gadgetID,
                "COUNTER",
                512);
            Assert.Equal(512, history.Count);
            Assert.Equal((ulong)9, history[0].sequence);
            Assert.Equal((ulong)520, history[^1].sequence);
            Assert.Equal(520, history[^1].value.Value<int>());
        }

        [Fact]
        public void State_NewSessionClearsOldSamplesAndManifestRemovesStaleStreams()
        {
            HubService service = new();
            service.ResetStreamablesForTests();
            HubService.KliveTechGadget gadget = Gadget();
            service.AcceptStreamEventForTests(
                gadget,
                BuildManifest(
                    1,
                    Definition("counter", "integer", "application/json", "periodic", 25, true),
                    Definition("old", "string", "application/json", "onChange", 1000, true)),
                out _);
            service.AcceptStreamEventForTests(gadget, BuildSample("counter", 1, "1"), out _);

            JObject nextSession = BuildManifest(
                1,
                Definition("counter", "integer", "application/json", "periodic", 25, true));
            nextSession["SESSIONID"] = "FFEEDDCCBBAA99887766554433221100";
            Assert.True(service.AcceptStreamEventForTests(gadget, nextSession, out _));

            Assert.Empty(service.GetStreamableHistory(gadget.gadgetID, "counter", 10));
            Assert.Single(service.GetStreamableCatalog(gadget.gadgetID));
            Assert.Equal("counter", service.GetStreamableCatalog(gadget.gadgetID)[0].streamID);
        }

        private static HubService.KliveTechGadget Gadget()
        {
            return new HubService.KliveTechGadget
            {
                gadgetID = "AABBCCDDEEFF00112233445566778899",
                name = "Test Gadget",
                isOnline = true
            };
        }

        private static JObject BuildManifest(ulong revision, params JObject[] definitions)
        {
            return new JObject
            {
                ["EVENT"] = "StreamManifest",
                ["VERSION"] = 1,
                ["SESSIONID"] = Session,
                ["REVISION"] = revision,
                ["STREAMABLES"] = new JArray(definitions)
            };
        }

        private static JObject Definition(
            string id,
            string valueType,
            string mimeType,
            string mode,
            uint intervalMs,
            bool enabled)
        {
            return new JObject
            {
                ["ID"] = id,
                ["VALUETYPE"] = valueType,
                ["MIMETYPE"] = mimeType,
                ["MODE"] = mode,
                ["INTERVALMS"] = intervalMs,
                ["ENABLED"] = enabled
            };
        }

        private static JObject BuildSample(string streamId, ulong sequence, string json)
        {
            return new JObject
            {
                ["EVENT"] = "StreamSample",
                ["VERSION"] = 1,
                ["SESSIONID"] = Session,
                ["STREAMID"] = streamId,
                ["SEQUENCE"] = sequence,
                ["TIMESTAMPMS"] = 1234,
                ["ENCODING"] = "base64-json",
                ["DATA"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            };
        }

        private static JObject BuildFrameChunk(
            string streamId,
            ulong sequence,
            byte[] frame,
            int chunkIndex)
        {
            int offset = chunkIndex * KliveTechStreamProtocol.BinaryChunkBytes;
            int length = Math.Min(KliveTechStreamProtocol.BinaryChunkBytes, frame.Length - offset);
            byte[] chunk = frame.AsSpan(offset, length).ToArray();
            return new JObject
            {
                ["EVENT"] = "StreamFrame",
                ["VERSION"] = 1,
                ["SESSIONID"] = Session,
                ["STREAMID"] = streamId,
                ["SEQUENCE"] = sequence,
                ["TIMESTAMPMS"] = 5678,
                ["MIMETYPE"] = "image/jpeg",
                ["FRAMESIZE"] = frame.Length,
                ["FRAMESHA256"] = Convert.ToHexString(SHA256.HashData(frame)),
                ["CHUNKINDEX"] = chunkIndex,
                ["CHUNKCOUNT"] = (frame.Length + KliveTechStreamProtocol.BinaryChunkBytes - 1) /
                    KliveTechStreamProtocol.BinaryChunkBytes,
                ["CHUNKOFFSET"] = offset,
                ["CHUNKSIZE"] = length,
                ["CHUNKSHA256"] = Convert.ToHexString(SHA256.HashData(chunk)),
                ["ENCODING"] = "base64",
                ["DATA"] = Convert.ToBase64String(chunk)
            };
        }
    }
}
