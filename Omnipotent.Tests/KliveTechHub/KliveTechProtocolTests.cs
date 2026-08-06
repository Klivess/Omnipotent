using Omnipotent.Services.KliveTechHub;
using System.Text;

namespace Omnipotent.Tests.KliveTechHub
{
    public sealed class KliveTechProtocolTests
    {
        [Fact]
        public void ExtractCompleteFrames_ReturnsEveryFrameAndRetainsPartialTail()
        {
            StringBuilder buffer = new(
                "noise" +
                KliveTechProtocol.Frame("{\"ID\":1}") +
                KliveTechProtocol.Frame("{\"ID\":2}") +
                KliveTechProtocol.StartCommand +
                "{\"ID\":3");

            IReadOnlyList<string> frames = KliveTechProtocol.ExtractCompleteFrames(buffer);

            Assert.Equal(new[] { "{\"ID\":1}", "{\"ID\":2}" }, frames);
            Assert.Equal(KliveTechProtocol.StartCommand + "{\"ID\":3", buffer.ToString());
        }

        [Fact]
        public void ExtractCompleteFrames_RecognizesMarkerSplitAcrossReads()
        {
            StringBuilder buffer = new("ignored{startCo");

            IReadOnlyList<string> firstRead = KliveTechProtocol.ExtractCompleteFrames(buffer);
            buffer.Append("mm}{\"ok\":true}{endComm}");
            IReadOnlyList<string> secondRead = KliveTechProtocol.ExtractCompleteFrames(buffer);

            Assert.Empty(firstRead);
            Assert.Single(secondRead);
            Assert.Equal("{\"ok\":true}", secondRead[0]);
            Assert.Empty(buffer.ToString());
        }

        [Fact]
        public void ExtractCompleteFrames_RejectsOversizedIncompleteFrame()
        {
            StringBuilder buffer = new(KliveTechProtocol.StartCommand);
            buffer.Append('x', KliveTechProtocol.MaxBufferedCharacters + 1);

            Assert.Throws<InvalidDataException>(
                () => KliveTechProtocol.ExtractCompleteFrames(buffer));
        }

        [Fact]
        public void Frame_AddsExactlyOnePairOfProtocolMarkers()
        {
            const string payload = "{\"ID\":42,\"DATA\":{}}";

            string frame = KliveTechProtocol.Frame(payload);

            Assert.Equal(
                KliveTechProtocol.StartCommand + payload + KliveTechProtocol.EndCommand,
                frame);
        }

        [Theory]
        [InlineData("before{startComm}after")]
        [InlineData("before{endComm}after")]
        public void Frame_RejectsReservedMarkersInsidePayload(string payload)
        {
            Assert.Throws<InvalidDataException>(() => KliveTechProtocol.Frame(payload));
        }

        [Fact]
        public void Frame_RejectsOversizedPayload()
        {
            string payload = new('x', KliveTechProtocol.MaxBufferedCharacters + 1);

            Assert.Throws<InvalidDataException>(() => KliveTechProtocol.Frame(payload));
        }
    }
}
