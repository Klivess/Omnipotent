using Omnipotent.Services.Projects.Containers;

namespace Omnipotent.Tests.Projects
{
    /// <summary>
    /// The wire protocol for Klives' remote-control input sessions on container desktops
    /// (/projects/containers/remote/input). Parsing is exercised without an RFB socket.
    /// </summary>
    public class ContainerRemoteInputTests
    {
        [Fact]
        public void Parse_ClickEvent_MapsTypeButtonAndCoordinates()
        {
            var ev = ContainerRemoteInput.Parse("""{"t":"down","x":0.5,"y":0.25,"button":"right"}""");
            Assert.NotNull(ev);
            Assert.Equal("down", ev!.Type);
            Assert.Equal(0.5, ev.X);
            Assert.Equal(0.25, ev.Y);
            Assert.Equal(3, ev.Button);
        }

        [Fact]
        public void Parse_ClampsCoordinatesIntoUnitRange()
        {
            var ev = ContainerRemoteInput.Parse("""{"t":"move","x":-3.2,"y":42.0}""");
            Assert.NotNull(ev);
            Assert.Equal(0, ev!.X);
            Assert.Equal(1, ev.Y);
        }

        [Fact]
        public void Parse_KeyChord_PreservesKeyOrder()
        {
            var ev = ContainerRemoteInput.Parse("""{"t":"key","keys":["ctrl","shift","t"]}""");
            Assert.NotNull(ev);
            Assert.Equal(new[] { "ctrl", "shift", "t" }, ev!.Keys);
        }

        [Fact]
        public void Parse_SingleKeyProperty_FallsBackIntoKeysArray()
        {
            var ev = ContainerRemoteInput.Parse("""{"t":"keydown","key":"shift"}""");
            Assert.NotNull(ev);
            Assert.Equal(new[] { "shift" }, ev!.Keys);
        }

        [Fact]
        public void Parse_ScrollNotchesAreClamped()
        {
            var ev = ContainerRemoteInput.Parse("""{"t":"scroll","x":0.5,"y":0.5,"dy":500,"dx":-500}""");
            Assert.NotNull(ev);
            Assert.Equal(20, ev!.Dy);
            Assert.Equal(-20, ev.Dx);
        }

        [Theory]
        [InlineData("")]
        [InlineData("not json at all")]
        [InlineData("42")]
        [InlineData("[1,2,3]")]
        [InlineData("{}")]
        [InlineData("""{"x":0.5}""")]
        public void Parse_MalformedOrTypelessFrames_ReturnNull(string frame)
        {
            Assert.Null(ContainerRemoteInput.Parse(frame));
        }

        [Theory]
        [InlineData("left", 1)]
        [InlineData("middle", 2)]
        [InlineData("right", 3)]
        [InlineData("RIGHT", 3)]
        [InlineData("", 1)]
        [InlineData(null, 1)]
        [InlineData("bogus", 1)]
        public void ParseButton_MapsNamesWithLeftFallback(string? name, int expected)
        {
            Assert.Equal(expected, ContainerRemoteInput.ParseButton(name));
        }

        [Fact]
        public void ToPixels_MapsCornersAndCentre()
        {
            Assert.Equal((0, 0), ContainerRemoteInput.ToPixels(0, 0, 1920, 1080));
            Assert.Equal((1919, 1079), ContainerRemoteInput.ToPixels(1, 1, 1920, 1080));
            Assert.Equal((960, 540), ContainerRemoteInput.ToPixels(0.5, 0.5, 1920, 1080));
        }

        [Fact]
        public void ToPixels_ZeroGeometryNeverGoesNegative()
        {
            // Before the transport's first connect Width/Height are 0 — events must still be safe.
            Assert.Equal((0, 0), ContainerRemoteInput.ToPixels(0.7, 0.7, 0, 0));
        }

        [Fact]
        public async Task Apply_UnknownEventType_IsIgnoredWithoutTouchingTheTransport()
        {
            // Port 1 is never dialed: an unknown type must return false before any socket use.
            using var transport = new VncTransport("127.0.0.1", 1, _ => { });
            var ev = ContainerRemoteInput.Parse("""{"t":"teleport","x":0.5,"y":0.5}""");
            Assert.NotNull(ev);
            Assert.False(await ContainerRemoteInput.ApplyAsync(transport, ev!, CancellationToken.None));
        }

        [Fact]
        public async Task Apply_EmptyText_IsANoOpSuccess()
        {
            using var transport = new VncTransport("127.0.0.1", 1, _ => { });
            var ev = ContainerRemoteInput.Parse("""{"t":"text","text":""}""");
            Assert.NotNull(ev);
            Assert.True(await ContainerRemoteInput.ApplyAsync(transport, ev!, CancellationToken.None));
        }
    }
}
