using System.Buffers.Binary;
using System.Net.Sockets;

namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// Minimal in-house RFB 3.8 client — the container-native sibling of HostControl's Win32
    /// transport. Speaks exactly the subset the platform needs: security type None (the port is
    /// loopback-bound on the Docker host; isolation is the auth boundary), Raw encoding into an
    /// in-memory BGRA framebuffer, PointerEvent/KeyEvent for input. Existing .NET VNC libraries
    /// are viewer-oriented WinForms relics; ~400 focused lines beat adapting one.
    ///
    /// Concurrency: one receive loop owns the socket reads; senders serialise on
    /// <see cref="sendGate"/>. <see cref="CaptureFrameAsync"/> requests an update and awaits
    /// the next FramebufferUpdate applied by the receive loop.
    /// </summary>
    public sealed class VncTransport : IDisposable
    {
        private readonly string host;
        private readonly int port;
        private readonly Action<string> log;

        private TcpClient? tcp;
        private NetworkStream? stream;
        private Task? receiveLoop;
        private CancellationTokenSource? loopCts;
        private readonly SemaphoreSlim sendGate = new(1, 1);
        private readonly SemaphoreSlim connectGate = new(1, 1);

        private readonly object fbLock = new();
        private byte[] framebuffer = Array.Empty<byte>(); // BGRA, Width*Height*4
        private TaskCompletionSource<bool>? frameWaiter;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public string DesktopName { get; private set; } = "";
        public bool Connected => tcp?.Connected == true;

        // Pointer state: RFB PointerEvent always carries the full button mask.
        private byte buttonMask;
        private int pointerX, pointerY;

        public VncTransport(string host, int port, Action<string> log)
        {
            this.host = host;
            this.port = port;
            this.log = log ?? (_ => { });
        }

        // ── connection ──

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            await connectGate.WaitAsync(ct);
            try
            {
                if (Connected) return;
                DisposeConnection();

                tcp = new TcpClient { NoDelay = true };
                await tcp.ConnectAsync(host, port, ct);
                stream = tcp.GetStream();

                // ProtocolVersion handshake: server sends 12 bytes, we answer 3.8.
                byte[] version = await ReadExactAsync(12, ct);
                string serverVersion = System.Text.Encoding.ASCII.GetString(version);
                if (!serverVersion.StartsWith("RFB ")) throw new InvalidOperationException($"Not an RFB server: '{serverVersion}'");
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("RFB 003.008\n"), ct);

                // Security: server lists types; we require None (1).
                byte nTypes = (await ReadExactAsync(1, ct))[0];
                if (nTypes == 0)
                {
                    string reason = await ReadReasonStringAsync(ct);
                    throw new InvalidOperationException($"RFB handshake refused: {reason}");
                }
                byte[] types = await ReadExactAsync(nTypes, ct);
                if (!types.Contains((byte)1))
                    throw new InvalidOperationException("RFB server does not offer security type None — desktop containers must run x11vnc -nopw on a loopback-bound port.");
                await stream.WriteAsync(new byte[] { 1 }, ct);

                // SecurityResult (3.8 sends it for None too).
                byte[] secResult = await ReadExactAsync(4, ct);
                if (BinaryPrimitives.ReadUInt32BigEndian(secResult) != 0)
                {
                    string reason = await ReadReasonStringAsync(ct);
                    throw new InvalidOperationException($"RFB security failed: {reason}");
                }

                // ClientInit: shared = 1 (the website's live view coexists with the agent).
                await stream.WriteAsync(new byte[] { 1 }, ct);

                // ServerInit: geometry + pixel format + name.
                byte[] init = await ReadExactAsync(24, ct);
                Width = BinaryPrimitives.ReadUInt16BigEndian(init.AsSpan(0));
                Height = BinaryPrimitives.ReadUInt16BigEndian(init.AsSpan(2));
                uint nameLen = BinaryPrimitives.ReadUInt32BigEndian(init.AsSpan(20));
                DesktopName = System.Text.Encoding.UTF8.GetString(await ReadExactAsync((int)nameLen, ct));
                lock (fbLock) framebuffer = new byte[Width * Height * 4];

                // SetPixelFormat: 32bpp true-colour, little-endian, BGRA layout (blue shift 0).
                var spf = new byte[20];
                spf[0] = 0;                                  // message type
                spf[4] = 32; spf[5] = 24; spf[6] = 0;        // bits-per-pixel, depth, big-endian=0
                spf[7] = 1;                                  // true-colour
                BinaryPrimitives.WriteUInt16BigEndian(spf.AsSpan(8), 255);   // red max
                BinaryPrimitives.WriteUInt16BigEndian(spf.AsSpan(10), 255);  // green max
                BinaryPrimitives.WriteUInt16BigEndian(spf.AsSpan(12), 255);  // blue max
                spf[14] = 16; spf[15] = 8; spf[16] = 0;      // red/green/blue shifts → BGRA in memory
                await stream.WriteAsync(spf, ct);

                // SetEncodings: Raw only (loopback bandwidth is free; simplicity wins).
                var se = new byte[8];
                se[0] = 2;
                BinaryPrimitives.WriteUInt16BigEndian(se.AsSpan(2), 1);
                BinaryPrimitives.WriteInt32BigEndian(se.AsSpan(4), 0); // Raw
                await stream.WriteAsync(se, ct);

                loopCts = new CancellationTokenSource();
                receiveLoop = Task.Run(() => ReceiveLoopAsync(loopCts.Token));
                log($"VNC connected to {host}:{port} — '{DesktopName}' {Width}x{Height}.");
            }
            finally { connectGate.Release(); }
        }

        // ── capture ──

        /// <summary>
        /// Requests a (non-incremental) framebuffer update and returns a copy of the framebuffer
        /// once it lands: one settled full frame, the container analog of ScreenCapturer's
        /// settled-frame capture.
        /// </summary>
        public async Task<(byte[] bgra, int width, int height)> CaptureFrameAsync(CancellationToken ct = default)
        {
            if (!Connected) await ConnectAsync(ct);
            TaskCompletionSource<bool> tcs;
            lock (fbLock)
            {
                tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                frameWaiter = tcs;
            }
            await SendFramebufferUpdateRequestAsync(incremental: false, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await using (timeout.Token.Register(() => tcs.TrySetException(new TimeoutException("No framebuffer update within 10s."))))
                await tcs.Task;
            lock (fbLock)
                return ((byte[])framebuffer.Clone(), Width, Height);
        }

        // ── input ──

        public Task MoveMouseAsync(int x, int y, CancellationToken ct = default) => SendPointerAsync(x, y, buttonMask, ct);

        public async Task ClickAsync(int x, int y, int button = 1, int clicks = 1, CancellationToken ct = default)
        {
            byte bit = ButtonBit(button);
            for (int i = 0; i < Math.Max(1, clicks); i++)
            {
                await SendPointerAsync(x, y, (byte)(buttonMask | bit), ct);
                await Task.Delay(40, ct);
                await SendPointerAsync(x, y, (byte)(buttonMask & ~bit), ct);
                if (i + 1 < clicks) await Task.Delay(90, ct);
            }
        }

        public async Task DragAsync(int fromX, int fromY, int toX, int toY, int button = 1, CancellationToken ct = default)
        {
            byte bit = ButtonBit(button);
            await SendPointerAsync(fromX, fromY, buttonMask, ct);
            await SendPointerAsync(fromX, fromY, (byte)(buttonMask | bit), ct);
            // Interpolate so apps see genuine motion, not a teleport.
            const int steps = 12;
            for (int i = 1; i <= steps; i++)
            {
                int x = fromX + (toX - fromX) * i / steps;
                int y = fromY + (toY - fromY) * i / steps;
                await SendPointerAsync(x, y, (byte)(buttonMask | bit), ct);
                await Task.Delay(15, ct);
            }
            await SendPointerAsync(toX, toY, (byte)(buttonMask & ~bit), ct);
        }

        public async Task MouseDownAsync(int x, int y, int button = 1, CancellationToken ct = default)
            => await SendPointerAsync(x, y, (byte)(buttonMask | ButtonBit(button)), ct);

        public async Task MouseUpAsync(int x, int y, int button = 1, CancellationToken ct = default)
            => await SendPointerAsync(x, y, (byte)(buttonMask & ~ButtonBit(button)), ct);

        /// <summary>Wheel scroll: RFB buttons 4/5 = up/down, 6/7 = left/right, one press per notch.</summary>
        public async Task ScrollAsync(int x, int y, int dy, int dx, CancellationToken ct = default)
        {
            async Task Notches(byte bit, int count)
            {
                for (int i = 0; i < count; i++)
                {
                    await SendPointerAsync(x, y, (byte)(buttonMask | bit), ct);
                    await SendPointerAsync(x, y, buttonMask, ct);
                    await Task.Delay(25, ct);
                }
            }
            if (dy > 0) await Notches(1 << 3, dy);        // up
            else if (dy < 0) await Notches(1 << 4, -dy);  // down
            if (dx < 0) await Notches(1 << 5, -dx);       // left
            else if (dx > 0) await Notches(1 << 6, dx);   // right
        }

        public async Task TypeTextAsync(string text, CancellationToken ct = default)
        {
            foreach (char c in text)
            {
                bool shift = char.IsUpper(c) || "~!@#$%^&*()_+{}|:\"<>?".Contains(c);
                uint keysym = VncKeysyms.FromChar(c);
                if (shift) await SendKeyAsync(VncKeysyms.ShiftL, true, ct);
                await SendKeyAsync(keysym, true, ct);
                await SendKeyAsync(keysym, false, ct);
                if (shift) await SendKeyAsync(VncKeysyms.ShiftL, false, ct);
                await Task.Delay(18, ct);
            }
        }

        /// <summary>Presses a chord like "ctrl+l" or a single named key like "enter".</summary>
        public async Task KeyChordAsync(string chord, CancellationToken ct = default)
        {
            var parts = chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var keysyms = new List<uint>();
            foreach (var part in parts)
            {
                uint? ks = VncKeysyms.FromName(part);
                if (ks == null) throw new ArgumentException($"Unknown key '{part}' in chord '{chord}'.");
                keysyms.Add(ks.Value);
            }
            foreach (var ks in keysyms) await SendKeyAsync(ks, true, ct);
            for (int i = keysyms.Count - 1; i >= 0; i--) await SendKeyAsync(keysyms[i], false, ct);
        }

        public Task KeyDownAsync(string key, CancellationToken ct = default)
            => SendKeyAsync(VncKeysyms.FromName(key) ?? throw new ArgumentException($"Unknown key '{key}'."), true, ct);
        public Task KeyUpAsync(string key, CancellationToken ct = default)
            => SendKeyAsync(VncKeysyms.FromName(key) ?? throw new ArgumentException($"Unknown key '{key}'."), false, ct);

        /// <summary>Releases every held button/modifier — the container analog of computer_release_all.</summary>
        public async Task ReleaseAllAsync(CancellationToken ct = default)
        {
            await SendPointerAsync(pointerX, pointerY, 0, ct);
            foreach (var mod in new[] { VncKeysyms.ShiftL, VncKeysyms.ControlL, VncKeysyms.AltL, VncKeysyms.SuperL })
                await SendKeyAsync(mod, false, ct);
        }

        // ── wire helpers ──

        private static byte ButtonBit(int button) => button switch
        {
            2 => 1 << 1, // middle
            3 => 1 << 2, // right
            _ => 1 << 0, // left
        };

        private async Task SendPointerAsync(int x, int y, byte mask, CancellationToken ct)
        {
            if (!Connected) await ConnectAsync(ct);
            var msg = new byte[6];
            msg[0] = 5;
            msg[1] = mask;
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2), (ushort)Math.Clamp(x, 0, Math.Max(0, Width - 1)));
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(4), (ushort)Math.Clamp(y, 0, Math.Max(0, Height - 1)));
            await SendAsync(msg, ct);
            buttonMask = mask;
            pointerX = x; pointerY = y;
        }

        private async Task SendKeyAsync(uint keysym, bool down, CancellationToken ct)
        {
            if (!Connected) await ConnectAsync(ct);
            var msg = new byte[8];
            msg[0] = 4;
            msg[1] = (byte)(down ? 1 : 0);
            BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(4), keysym);
            await SendAsync(msg, ct);
        }

        private async Task SendFramebufferUpdateRequestAsync(bool incremental, CancellationToken ct)
        {
            var msg = new byte[10];
            msg[0] = 3;
            msg[1] = (byte)(incremental ? 1 : 0);
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(6), (ushort)Width);
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(8), (ushort)Height);
            await SendAsync(msg, ct);
        }

        private async Task SendAsync(byte[] payload, CancellationToken ct)
        {
            var s = stream ?? throw new InvalidOperationException("VNC not connected.");
            await sendGate.WaitAsync(ct);
            try { await s.WriteAsync(payload, ct); }
            finally { sendGate.Release(); }
        }

        // ── receive loop ──

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    byte msgType = (await ReadExactAsync(1, ct))[0];
                    switch (msgType)
                    {
                        case 0: // FramebufferUpdate
                        {
                            await ReadExactAsync(1, ct); // padding
                            int nRects = BinaryPrimitives.ReadUInt16BigEndian(await ReadExactAsync(2, ct));
                            for (int r = 0; r < nRects; r++)
                            {
                                byte[] head = await ReadExactAsync(12, ct);
                                int rx = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(0));
                                int ry = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(2));
                                int rw = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(4));
                                int rh = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(6));
                                int encoding = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(8));
                                if (encoding != 0)
                                    throw new InvalidOperationException($"Server sent unsupported encoding {encoding} despite Raw-only SetEncodings.");
                                byte[] pixels = await ReadExactAsync(rw * rh * 4, ct);
                                lock (fbLock)
                                {
                                    for (int row = 0; row < rh; row++)
                                    {
                                        int src = row * rw * 4;
                                        int dst = ((ry + row) * Width + rx) * 4;
                                        if (dst + rw * 4 <= framebuffer.Length)
                                            Buffer.BlockCopy(pixels, src, framebuffer, dst, rw * 4);
                                    }
                                }
                            }
                            lock (fbLock) { frameWaiter?.TrySetResult(true); frameWaiter = null; }
                            break;
                        }
                        case 1: // SetColourMapEntries — impossible in true-colour, but consume it
                        {
                            await ReadExactAsync(1, ct);
                            await ReadExactAsync(2, ct);
                            int nColours = BinaryPrimitives.ReadUInt16BigEndian(await ReadExactAsync(2, ct));
                            await ReadExactAsync(nColours * 6, ct);
                            break;
                        }
                        case 2: // Bell
                            break;
                        case 3: // ServerCutText
                        {
                            await ReadExactAsync(3, ct);
                            uint len = BinaryPrimitives.ReadUInt32BigEndian(await ReadExactAsync(4, ct));
                            await ReadExactAsync((int)len, ct);
                            break;
                        }
                        default:
                            throw new InvalidOperationException($"Unknown RFB server message type {msgType}.");
                    }
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log($"VNC receive loop ended: {ex.Message}");
                lock (fbLock) { frameWaiter?.TrySetException(new IOException("VNC connection lost.", ex)); frameWaiter = null; }
                DisposeConnection();
            }
        }

        private async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
        {
            var s = stream ?? throw new InvalidOperationException("VNC not connected.");
            var buf = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = await s.ReadAsync(buf.AsMemory(read, count - read), ct);
                if (n == 0) throw new IOException("VNC connection closed by server.");
                read += n;
            }
            return buf;
        }

        private async Task<string> ReadReasonStringAsync(CancellationToken ct)
        {
            uint len = BinaryPrimitives.ReadUInt32BigEndian(await ReadExactAsync(4, ct));
            return System.Text.Encoding.UTF8.GetString(await ReadExactAsync((int)Math.Min(len, 4096), ct));
        }

        private void DisposeConnection()
        {
            try { loopCts?.Cancel(); } catch { }
            try { stream?.Dispose(); } catch { }
            try { tcp?.Dispose(); } catch { }
            stream = null;
            tcp = null;
            buttonMask = 0;
        }

        public void Dispose()
        {
            DisposeConnection();
            sendGate.Dispose();
            connectGate.Dispose();
        }
    }
}
