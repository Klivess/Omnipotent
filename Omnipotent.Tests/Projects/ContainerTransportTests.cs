using System.Buffers.Binary;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Omnipotent.Services.Projects.Containers;

namespace Omnipotent.Tests.Projects
{
    public class VncKeysymTests
    {
        [Theory]
        [InlineData("a", 0x61u)]
        [InlineData("enter", VncKeysyms.Return)]
        [InlineData("esc", VncKeysyms.Escape)]
        [InlineData("ctrl", VncKeysyms.ControlL)]
        [InlineData("f5", VncKeysyms.F1 + 4)]
        [InlineData("f12", VncKeysyms.F1 + 11)]
        [InlineData("left", VncKeysyms.Left)]
        [InlineData("space", 0x20u)]
        public void FromName_MapsKnownKeys(string name, uint expected)
        {
            Assert.Equal(expected, VncKeysyms.FromName(name));
        }

        [Fact]
        public void FromName_UnknownReturnsNull()
        {
            Assert.Null(VncKeysyms.FromName("nonsense-key"));
        }

        [Fact]
        public void FromChar_UsesX11UnicodeRuleForNonLatin()
        {
            // '€' U+20AC → 0x0100_20AC by the X11 Unicode rule.
            Assert.Equal(0x010020ACu, VncKeysyms.FromChar('€'));
        }

        [Fact]
        public void FromChar_PreservesLetterCase()
        {
            // Uppercase is produced via FromChar (+ shift in TypeText), not via FromName.
            Assert.Equal(0x41u, VncKeysyms.FromChar('A'));
            Assert.Equal(0x61u, VncKeysyms.FromChar('a'));
        }
    }

    public class InputLockCoordinatorTests
    {
        [Fact]
        public void FirstAgentAcquires_SecondIsBlocked()
        {
            var c = new InputLockCoordinator();
            Assert.True(c.TryAcquire("cont1", "agentA"));
            Assert.False(c.TryAcquire("cont1", "agentB"));
            Assert.True(c.Holds("cont1", "agentA"));
            Assert.Equal("agentA", c.CurrentHolder("cont1"));
        }

        [Fact]
        public void Release_LetsAnotherAgentAcquire()
        {
            var c = new InputLockCoordinator();
            Assert.True(c.TryAcquire("cont1", "agentA"));
            c.Release("cont1", "agentA");
            Assert.True(c.TryAcquire("cont1", "agentB"));
            Assert.Equal("agentB", c.CurrentHolder("cont1"));
        }

        [Fact]
        public void ExpiredLease_IsReclaimable()
        {
            var c = new InputLockCoordinator();
            Assert.True(c.TryAcquire("cont1", "agentA", TimeSpan.FromMilliseconds(1)));
            Thread.Sleep(20);
            Assert.False(c.Holds("cont1", "agentA")); // lease lapsed
            Assert.True(c.TryAcquire("cont1", "agentB")); // reclaimed
        }

        [Fact]
        public void SameAgentRenews()
        {
            var c = new InputLockCoordinator();
            Assert.True(c.TryAcquire("cont1", "agentA"));
            Assert.True(c.TryAcquire("cont1", "agentA")); // renew, still held
        }

        [Fact]
        public void LocksArePerContainer()
        {
            var c = new InputLockCoordinator();
            Assert.True(c.TryAcquire("cont1", "agentA"));
            Assert.True(c.TryAcquire("cont2", "agentB")); // different desktop, no contention
        }
    }

    public class ContainerRegistryTests
    {
        [Fact]
        public void ResolveForAgent_PrefersOwnContainerThenShared()
        {
            var reg = new ContainerRegistry(_ => { });
            string pid = "test_" + Guid.NewGuid().ToString("N");
            reg.Add(new DesktopContainerRecord { ContainerID = "shared1", ProjectID = pid, AgentID = null, VncHostPort = 5901 });
            reg.Add(new DesktopContainerRecord { ContainerID = "ownA", ProjectID = pid, AgentID = "agentA", VncHostPort = 5902 });

            Assert.Equal("ownA", reg.ResolveForAgent(pid, "agentA")!.ContainerID);   // own wins
            Assert.Equal("shared1", reg.ResolveForAgent(pid, "agentB")!.ContainerID); // falls back to shared
        }

        [Fact]
        public void LostContainers_AreExcludedFromResolution()
        {
            var reg = new ContainerRegistry(_ => { });
            string pid = "test_" + Guid.NewGuid().ToString("N");
            var rec = new DesktopContainerRecord { ContainerID = "ownA", ProjectID = pid, AgentID = "agentA", VncHostPort = 5902, Lost = true };
            reg.Add(rec);
            Assert.Null(reg.ResolveForAgent(pid, "agentA"));
        }
    }

    public class VncTransportCaptureTests
    {
        [Fact]
        public async Task ReleaseAll_OnDisconnectedTransport_DoesNotAttemptAHandshake()
        {
            using var transport = new VncTransport("127.0.0.1", 1, _ => { });

            await transport.ReleaseAllAsync().WaitAsync(TimeSpan.FromSeconds(1));

            Assert.False(transport.Connected);
        }

        [Fact]
        public async Task ConcurrentCaptures_AreSerializedAndEachGetsItsOwnReply()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var firstRequestSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondCaptureStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var serverTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            var serverTask = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(serverTimeout.Token);
                await using var stream = client.GetStream();
                await CompleteHandshakeAsync(stream, serverTimeout.Token);

                AssertUpdateRequest(await ReadExactAsync(stream, 10, serverTimeout.Token), incremental: false);
                firstRequestSeen.TrySetResult(true);
                await secondCaptureStarted.Task.WaitAsync(serverTimeout.Token);
                await Task.Delay(100, serverTimeout.Token);

                // An RFB FramebufferUpdate has no request identifier.  Seeing request #2 before
                // replying to #1 is exactly the waiter-overwrite race this regression protects.
                bool secondWasInFlight = stream.DataAvailable;
                await SendFrameAsync(stream, 11, serverTimeout.Token);

                AssertUpdateRequest(await ReadExactAsync(stream, 10, serverTimeout.Token), incremental: true);
                await SendFrameAsync(stream, 22, serverTimeout.Token);
                return secondWasInFlight;
            }, serverTimeout.Token);

            using var transport = new VncTransport("127.0.0.1", port, _ => { });
            var first = transport.CaptureFrameAsync(serverTimeout.Token);
            await firstRequestSeen.Task.WaitAsync(serverTimeout.Token);
            var second = transport.CaptureFrameAsync(serverTimeout.Token);
            secondCaptureStarted.TrySetResult(true);

            var frames = await Task.WhenAll(first, second).WaitAsync(serverTimeout.Token);
            Assert.False(await serverTask);
            Assert.Equal(new byte[] { 11, 22 }, frames.Select(f => f.bgra[0]).OrderBy(v => v).ToArray());
            Assert.All(frames, frame =>
            {
                Assert.Equal(2, frame.width);
                Assert.Equal(1, frame.height);
                Assert.Equal(8, frame.bgra.Length);
            });
        }

        [Fact]
        public async Task MalformedRectangle_FailsConnectionInsteadOfCorruptingFramebufferRows()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            var server = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = client.GetStream();
                await CompleteHandshakeAsync(stream, timeout.Token);
                AssertUpdateRequest(await ReadExactAsync(stream, 10, timeout.Token), incremental: false);

                byte[] invalid = new byte[4 + 12 + 8];
                BinaryPrimitives.WriteUInt16BigEndian(invalid.AsSpan(2), 1); // one rectangle
                BinaryPrimitives.WriteUInt16BigEndian(invalid.AsSpan(4), 1); // x=1
                BinaryPrimitives.WriteUInt16BigEndian(invalid.AsSpan(8), 2); // width=2 => outside 2px frame
                BinaryPrimitives.WriteUInt16BigEndian(invalid.AsSpan(10), 1);
                await stream.WriteAsync(invalid, timeout.Token);
                await Task.Delay(50, timeout.Token);
            }, timeout.Token);

            using var transport = new VncTransport("127.0.0.1", port, _ => { });
            var error = await Assert.ThrowsAsync<IOException>(() => transport.CaptureFrameAsync(timeout.Token));
            Assert.Contains("connection lost", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("outside framebuffer", error.InnerException?.Message ?? error.ToString(), StringComparison.OrdinalIgnoreCase);
            await server;
            Assert.False(transport.Connected);
        }

        [Fact]
        public async Task StaticDesktop_UsesCachedCompleteFrameWithoutTenSecondStall()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            var server = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = client.GetStream();
                await CompleteHandshakeAsync(stream, timeout.Token);
                AssertUpdateRequest(await ReadExactAsync(stream, 10, timeout.Token), incremental: false);
                await SendFrameAsync(stream, 33, timeout.Token);
                AssertUpdateRequest(await ReadExactAsync(stream, 10, timeout.Token), incremental: true);
                // An RFB server may hold an incremental request until something changes.
                await Task.Delay(350, timeout.Token);
            }, timeout.Token);

            using var transport = new VncTransport("127.0.0.1", port, _ => { });
            var first = await transport.CaptureFrameWithVersionAsync(timeout.Token);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var second = await transport.CaptureFrameWithVersionAsync(timeout.Token);

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"Static capture took {sw.Elapsed}.");
            Assert.Equal(first.version, second.version);
            Assert.Equal((byte)33, second.bgra[0]);
            await server;
        }

        private static async Task CompleteHandshakeAsync(NetworkStream stream, CancellationToken ct)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("RFB 003.008\n"), ct);
            Assert.Equal("RFB 003.008\n", Encoding.ASCII.GetString(await ReadExactAsync(stream, 12, ct)));

            await stream.WriteAsync(new byte[] { 1, 1 }, ct); // one security type: None
            Assert.Equal(1, (await ReadExactAsync(stream, 1, ct))[0]);
            await stream.WriteAsync(new byte[4], ct);         // SecurityResult: OK
            Assert.Equal(1, (await ReadExactAsync(stream, 1, ct))[0]); // shared ClientInit

            byte[] init = new byte[24];
            BinaryPrimitives.WriteUInt16BigEndian(init.AsSpan(0), 2);
            BinaryPrimitives.WriteUInt16BigEndian(init.AsSpan(2), 1);
            init[4] = 32; init[5] = 24; init[7] = 1;
            BinaryPrimitives.WriteUInt16BigEndian(init.AsSpan(8), 255);
            BinaryPrimitives.WriteUInt16BigEndian(init.AsSpan(10), 255);
            BinaryPrimitives.WriteUInt16BigEndian(init.AsSpan(12), 255);
            init[14] = 16; init[15] = 8;
            byte[] name = Encoding.UTF8.GetBytes("test");
            BinaryPrimitives.WriteUInt32BigEndian(init.AsSpan(20), (uint)name.Length);
            await stream.WriteAsync(init, ct);
            await stream.WriteAsync(name, ct);

            Assert.Equal(0, (await ReadExactAsync(stream, 20, ct))[0]); // SetPixelFormat
            Assert.Equal(2, (await ReadExactAsync(stream, 8, ct))[0]);  // SetEncodings
        }

        private static void AssertUpdateRequest(byte[] request, bool incremental)
        {
            Assert.Equal(3, request[0]);
            Assert.Equal(incremental ? 1 : 0, request[1]);
            Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(6)));
            Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(8)));
        }

        private static async Task SendFrameAsync(NetworkStream stream, byte value, CancellationToken ct)
        {
            byte[] update = new byte[4 + 12 + 8];
            update[0] = 0; // FramebufferUpdate
            BinaryPrimitives.WriteUInt16BigEndian(update.AsSpan(2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(update.AsSpan(8), 2);
            BinaryPrimitives.WriteUInt16BigEndian(update.AsSpan(10), 1);
            // bytes 12..15 are Raw encoding (0); remaining bytes are two BGRA pixels.
            update.AsSpan(16).Fill(value);
            await stream.WriteAsync(update, ct);
        }

        private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken ct)
        {
            byte[] result = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = await stream.ReadAsync(result.AsMemory(read, count - read), ct);
                if (n == 0) throw new IOException("Test RFB client disconnected.");
                read += n;
            }
            return result;
        }
    }

    public class VncFrameEncoderTests
    {
        [Fact]
        public void EncodeJpeg_TreatsRfbPaddingByteAsOpaque()
        {
            const int width = 24, height = 16;
            byte[] bgrx = new byte[width * height * 4];
            for (int i = 0; i < bgrx.Length; i += 4)
            {
                bgrx[i] = 12;       // blue
                bgrx[i + 1] = 35;   // green
                bgrx[i + 2] = 225;  // red
                bgrx[i + 3] = 0;    // RFB padding, not alpha
            }

            byte[] jpeg = VncFrameEncoder.EncodeJpeg(bgrx, width, height, maxWidth: width, quality: 100);
            using var stream = new MemoryStream(jpeg);
            using var decoded = new Bitmap(stream);
            Color centre = decoded.GetPixel(width / 2, height / 2);

            Assert.True(centre.R > 180, $"Expected an opaque red pixel, got {centre}.");
            Assert.True(centre.G < 80 && centre.B < 80, $"Unexpected channel corruption: {centre}.");
        }

        [Fact]
        public void EncodeJpeg_RejectsShortFramebuffer()
        {
            Assert.Throws<ArgumentException>(() => VncFrameEncoder.EncodeJpeg(new byte[7], 2, 1));
        }
    }
}
