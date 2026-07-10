using System.Buffers.Binary;
using System.Net.Sockets;

namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// Minimal in-house RFB 3.8 client — the container-native sibling of HostControl's Win32
    /// transport. Speaks exactly the subset the platform needs: security type None (the port is
    /// loopback-bound on the Docker host; isolation is the auth boundary), Raw encoding into an
    /// in-memory BGRA framebuffer, PointerEvent/KeyEvent for input. Existing .NET VNC libraries
    /// are viewer-oriented WinForms relics; this focused transport keeps the protocol surface small.
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
        private readonly object connectionLock = new();
        private bool connected;
        private int connectionGeneration;
        // RFB update requests have no request ID.  More than one in flight means the first
        // FramebufferUpdate can satisfy the wrong caller, leaving the displaced caller to time
        // out.  The website live view and screen-diff hook share this transport with tool calls,
        // so this is not merely defensive: without a capture gate they routinely overwrite one
        // another's waiter.
        private readonly SemaphoreSlim captureGate = new(1, 1);

        private readonly object fbLock = new();
        private byte[] framebuffer = Array.Empty<byte>(); // BGRA, Width*Height*4
        private int framebufferGeneration = -1;
        private TaskCompletionSource<bool>? frameWaiter;
        private bool frameRequestIsFull;
        private bool hasCompleteFrame;
        private long frameVersion;
        private static readonly TimeSpan IncrementalFreshnessWait = TimeSpan.FromMilliseconds(120);
        private static readonly TimeSpan InitialFramebufferTimeout = TimeSpan.FromSeconds(5);

        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Port => port;
        public string DesktopName { get; private set; } = "";
        public bool Connected { get { lock (connectionLock) return connected; } }

        // Pointer state: RFB PointerEvent always carries the full button mask.
        private byte buttonMask;
        private int pointerX, pointerY;
        private string clipboardText = string.Empty;

        public VncTransport(string host, int port, Action<string> log)
        {
            this.host = host;
            this.port = port;
            this.log = log ?? (_ => { });
        }

        // ── connection ──

        /// <summary>Overall budget for TCP connect + the RFB handshake. Without this a container
        /// whose x11vnc hasn't come up (docker-proxy accepts the TCP connection but no RFB bytes
        /// ever arrive) would hang a computer_* tool indefinitely — the "all computer_* tools
        /// timeout" symptom. A bounded failure surfaces a clear error the agent can act on.</summary>
        private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(12);

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            await connectGate.WaitAsync(ct);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(HandshakeTimeout);
            var hct = linked.Token;
            TcpClient? newTcp = null;
            NetworkStream? newStream = null;
            try
            {
                if (Connected) return;
                DisposeConnection();

                // Keep the candidate socket local until the complete handshake succeeds. The old
                // receive loop can therefore never switch mid-read to a replacement NetworkStream.
                newTcp = new TcpClient { NoDelay = true };
                await newTcp.ConnectAsync(host, port, hct);
                newStream = newTcp.GetStream();

                // ProtocolVersion handshake: server sends 12 bytes, we answer 3.8.
                byte[] version = await ReadExactAsync(newStream, 12, hct);
                string serverVersion = System.Text.Encoding.ASCII.GetString(version);
                if (!serverVersion.StartsWith("RFB ")) throw new InvalidOperationException($"Not an RFB server: '{serverVersion}'");
                await newStream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("RFB 003.008\n"), hct);

                // Security: server lists types; we require None (1).
                byte nTypes = (await ReadExactAsync(newStream, 1, hct))[0];
                if (nTypes == 0)
                {
                    string reason = await ReadReasonStringAsync(newStream, hct);
                    throw new InvalidOperationException($"RFB handshake refused: {reason}");
                }
                byte[] types = await ReadExactAsync(newStream, nTypes, hct);
                if (!types.Contains((byte)1))
                    throw new InvalidOperationException("RFB server does not offer security type None — desktop containers must run x11vnc -nopw on a loopback-bound port.");
                await newStream.WriteAsync(new byte[] { 1 }, hct);

                // SecurityResult (3.8 sends it for None too).
                byte[] secResult = await ReadExactAsync(newStream, 4, hct);
                if (BinaryPrimitives.ReadUInt32BigEndian(secResult) != 0)
                {
                    string reason = await ReadReasonStringAsync(newStream, hct);
                    throw new InvalidOperationException($"RFB security failed: {reason}");
                }

                // ClientInit: shared = 1 (the website's live view coexists with the agent).
                await newStream.WriteAsync(new byte[] { 1 }, hct);

                // ServerInit: geometry + pixel format + name.
                byte[] init = await ReadExactAsync(newStream, 24, hct);
                int width = BinaryPrimitives.ReadUInt16BigEndian(init.AsSpan(0));
                int height = BinaryPrimitives.ReadUInt16BigEndian(init.AsSpan(2));
                if (width <= 0 || height <= 0 || (long)width * height * 4 > int.MaxValue)
                    throw new InvalidOperationException($"RFB server announced invalid geometry {width}x{height}.");
                uint nameLen = BinaryPrimitives.ReadUInt32BigEndian(init.AsSpan(20));
                if (nameLen > 1024 * 1024)
                    throw new InvalidOperationException($"RFB server announced an invalid desktop-name length ({nameLen} bytes).");
                string desktopName = System.Text.Encoding.UTF8.GetString(await ReadExactAsync(newStream, (int)nameLen, hct));

                // SetPixelFormat: 32bpp true-colour, little-endian, BGRA layout (blue shift 0).
                var spf = new byte[20];
                spf[0] = 0;                                  // message type
                spf[4] = 32; spf[5] = 24; spf[6] = 0;        // bits-per-pixel, depth, big-endian=0
                spf[7] = 1;                                  // true-colour
                BinaryPrimitives.WriteUInt16BigEndian(spf.AsSpan(8), 255);   // red max
                BinaryPrimitives.WriteUInt16BigEndian(spf.AsSpan(10), 255);  // green max
                BinaryPrimitives.WriteUInt16BigEndian(spf.AsSpan(12), 255);  // blue max
                spf[14] = 16; spf[15] = 8; spf[16] = 0;      // red/green/blue shifts → BGRA in memory
                await newStream.WriteAsync(spf, hct);

                // SetEncodings: Raw only (loopback bandwidth is free; simplicity wins).
                var se = new byte[8];
                se[0] = 2;
                BinaryPrimitives.WriteUInt16BigEndian(se.AsSpan(2), 1);
                BinaryPrimitives.WriteInt32BigEndian(se.AsSpan(4), 0); // Raw
                await newStream.WriteAsync(se, hct);

                var establishedTcp = newTcp;
                var establishedStream = newStream;
                var newLoopCts = new CancellationTokenSource();
                int generation;
                Width = width;
                Height = height;
                DesktopName = desktopName;
                lock (fbLock)
                {
                    framebuffer = new byte[width * height * 4];
                    framebufferGeneration = -1;
                    frameWaiter = null;
                    frameRequestIsFull = false;
                    hasCompleteFrame = false;
                    frameVersion = 0;
                }
                lock (connectionLock)
                {
                    tcp = establishedTcp;
                    stream = establishedStream;
                    loopCts = newLoopCts;
                    generation = ++connectionGeneration;
                    connected = false;
                }
                lock (fbLock) framebufferGeneration = generation;
                lock (connectionLock)
                {
                    if (connectionGeneration != generation || !ReferenceEquals(stream, establishedStream))
                        throw new IOException("VNC connection was disposed while its handshake completed.");
                    connected = true;
                }
                receiveLoop = Task.Run(() => ReceiveLoopAsync(
                    establishedStream, generation, width, height, newLoopCts.Token));
                // The published connection now owns these resources.
                newTcp = null;
                newStream = null;
                log($"VNC connected to {host}:{port} — '{DesktopName}' {Width}x{Height}.");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The timeout fired (not the caller's token) — the desktop never completed the RFB
                // handshake. Surface it as a clear, actionable failure, not a silent hang.
                DisposeConnection();
                throw new TimeoutException($"VNC handshake to {host}:{port} did not complete within {HandshakeTimeout.TotalSeconds:0}s — the container's desktop server (x11vnc) is not responding yet or has failed to start.");
            }
            finally
            {
                try { newStream?.Dispose(); } catch { }
                try { newTcp?.Dispose(); } catch { }
                connectGate.Release();
            }
        }

        // ── capture ──

        /// <summary>
        /// Gets a current framebuffer without allowing overlapping RFB requests. The first capture
        /// requests a complete frame. Later captures use one outstanding incremental request and
        /// briefly wait for changed rectangles; a static desktop returns the known-complete cached
        /// frame instead of transferring four megabytes on every live-view tick.
        /// </summary>
        public async Task<(byte[] bgra, int width, int height)> CaptureFrameAsync(CancellationToken ct = default)
        {
            var frame = await CaptureFrameWithVersionAsync(ct);
            return (frame.bgra, frame.width, frame.height);
        }

        /// <summary>Capture variant used by the website stream so unchanged frames can reuse the
        /// last JPEG instead of encoding the same desktop several times per second.</summary>
        public async Task<(byte[] bgra, int width, int height, long version)> CaptureFrameWithVersionAsync(
            CancellationToken ct = default)
        {
            await captureGate.WaitAsync(ct);
            TaskCompletionSource<bool>? tcs = null;
            bool createdRequest = false;
            bool requestIsFull = false;
            int requestGeneration = -1;
            try
            {
                if (!Connected) await ConnectAsync(ct);
                lock (connectionLock)
                {
                    if (!connected) throw new IOException("VNC disconnected before the framebuffer request.");
                    requestGeneration = connectionGeneration;
                }
                lock (fbLock)
                {
                    if (frameWaiter == null)
                    {
                        tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        frameWaiter = tcs;
                        frameRequestIsFull = !hasCompleteFrame;
                        createdRequest = true;
                    }
                    else
                    {
                        tcs = frameWaiter;
                    }
                    requestIsFull = frameRequestIsFull;
                }

                if (createdRequest)
                {
                    try { await SendFramebufferUpdateRequestAsync(incremental: !requestIsFull, ct); }
                    catch
                    {
                        // If the request was not completely written its eventual response ordering
                        // is unknowable. Reconnect rather than poisoning the next capture.
                        AbandonFrameRequest(tcs, requestGeneration);
                        throw;
                    }
                }

                bool completeFrame;
                lock (fbLock) completeFrame = hasCompleteFrame;
                if (!completeFrame || requestIsFull)
                {
                    try { await tcs.Task.WaitAsync(InitialFramebufferTimeout, ct); }
                    catch (TimeoutException)
                    {
                        AbandonFrameRequest(tcs, requestGeneration);
                        throw new TimeoutException("No initial framebuffer update within 5s; the VNC connection was reset before retrying.");
                    }
                }
                else
                {
                    // An incremental request may legitimately remain unanswered while nothing on
                    // screen changes. Keep it pending so the next change is captured, but do not
                    // freeze screenshots or the live viewer waiting for a static desktop.
                    Task freshnessDelay = Task.Delay(IncrementalFreshnessWait, ct);
                    Task completed = await Task.WhenAny(tcs.Task, freshnessDelay);
                    if (completed == tcs.Task) await tcs.Task;
                    else ct.ThrowIfCancellationRequested();
                }

                lock (fbLock)
                {
                    if (!hasCompleteFrame)
                        throw new IOException("VNC update did not produce a complete framebuffer.");
                    return ((byte[])framebuffer.Clone(), Width, Height, frameVersion);
                }
            }
            finally
            {
                captureGate.Release();
            }
        }

        private void AbandonFrameRequest(TaskCompletionSource<bool> waiter, int requestGeneration)
        {
            lock (fbLock)
            {
                if (ReferenceEquals(frameWaiter, waiter)) frameWaiter = null;
                frameRequestIsFull = false;
            }
            // Keep an abandoned waiter from becoming an unobserved fault when the connection is
            // torn down.  The public caller receives the original cancellation/timeout instead.
            waiter.TrySetCanceled();
            DisposeConnection(requestGeneration);
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

        public async Task TypeTextAsync(string text, int charDelayMs = 18, CancellationToken ct = default)
        {
            foreach (char c in text)
            {
                bool shift = char.IsUpper(c) || "~!@#$%^&*()_+{}|:\"<>?".Contains(c);
                uint keysym = VncKeysyms.FromChar(c);
                if (shift) await SendKeyAsync(VncKeysyms.ShiftL, true, ct);
                await SendKeyAsync(keysym, true, ct);
                await SendKeyAsync(keysym, false, ct);
                if (shift) await SendKeyAsync(VncKeysyms.ShiftL, false, ct);
                if (charDelayMs > 0) await Task.Delay(Math.Clamp(charDelayMs, 0, 500), ct);
            }
        }

        /// <summary>Presses a chord like "ctrl+l" or a single named key like "enter".</summary>
        public async Task KeyChordAsync(string chord, int holdMs = 55, int repeats = 1, CancellationToken ct = default)
        {
            var parts = chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var keysyms = new List<uint>();
            foreach (var part in parts)
            {
                uint? ks = VncKeysyms.FromName(part);
                if (ks == null) throw new ArgumentException($"Unknown key '{part}' in chord '{chord}'.");
                keysyms.Add(ks.Value);
            }
            for (int repeat = 0; repeat < Math.Clamp(repeats, 1, 50); repeat++)
            {
                foreach (var ks in keysyms) await SendKeyAsync(ks, true, ct);
                await Task.Delay(Math.Clamp(holdMs, 1, 2000), ct);
                for (int i = keysyms.Count - 1; i >= 0; i--) await SendKeyAsync(keysyms[i], false, ct);
                if (repeat + 1 < repeats) await Task.Delay(70, ct);
            }
        }

        public Task KeyDownAsync(string key, CancellationToken ct = default)
            => SendKeyAsync(VncKeysyms.FromName(key) ?? throw new ArgumentException($"Unknown key '{key}'."), true, ct);
        public Task KeyUpAsync(string key, CancellationToken ct = default)
            => SendKeyAsync(VncKeysyms.FromName(key) ?? throw new ArgumentException($"Unknown key '{key}'."), false, ct);

        /// <summary>Releases every held button/modifier — the container analog of computer_release_all.</summary>
        public async Task ReleaseAllAsync(CancellationToken ct = default)
        {
            // A disconnected RFB session cannot retain input, and reconnecting solely to release
            // keys would make terminal-only wakes depend on a healthy framebuffer server.
            if (!Connected) { buttonMask = 0; return; }
            await SendPointerAsync(pointerX, pointerY, 0, ct);
            foreach (var mod in new[] { VncKeysyms.ShiftL, VncKeysyms.ControlL, VncKeysyms.AltL, VncKeysyms.SuperL })
                await SendKeyAsync(mod, false, ct);
        }

        /// <summary>RFB ClientCutText. The VNC server mirrors it into the container X selection;
        /// no shell command is involved.</summary>
        public async Task SetClipboardTextAsync(string text, CancellationToken ct = default)
        {
            if (!Connected) await ConnectAsync(ct);
            text ??= string.Empty;
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
            if (bytes.Length > 1024 * 1024)
                throw new ArgumentException("Clipboard text is too large (maximum 1 MiB UTF-8).", nameof(text));
            var msg = new byte[8 + bytes.Length];
            msg[0] = 6;
            BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(4), (uint)bytes.Length);
            Buffer.BlockCopy(bytes, 0, msg, 8, bytes.Length);
            await SendAsync(msg, ct);
            clipboardText = text;
        }

        /// <summary>The most recent server clipboard announcement. A VNC server is not required
        /// to proactively publish selections, so callers receive an explicit unavailable state
        /// until one has been observed.</summary>
        public string? GetClipboardText() => string.IsNullOrEmpty(clipboardText) ? null : clipboardText;

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
            await sendGate.WaitAsync(ct);
            try
            {
                NetworkStream s;
                int generation;
                lock (connectionLock)
                {
                    if (!connected || stream == null)
                        throw new InvalidOperationException("VNC not connected.");
                    s = stream;
                    generation = connectionGeneration;
                }

                try { await s.WriteAsync(payload, ct); }
                catch
                {
                    // A cancelled or failed write can leave a partial RFB message on the wire.
                    // Never reuse that byte stream for a later action.
                    DisposeConnection(generation);
                    throw;
                }
            }
            finally { sendGate.Release(); }
        }

        // ── receive loop ──

        private async Task ReceiveLoopAsync(NetworkStream ownedStream, int generation,
            int ownedWidth, int ownedHeight, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    byte msgType = (await ReadExactAsync(ownedStream, 1, ct))[0];
                    if (!IsCurrentConnection(ownedStream, generation)) return;
                    switch (msgType)
                    {
                        case 0: // FramebufferUpdate
                        {
                            await ReadExactAsync(ownedStream, 1, ct); // padding
                            int nRects = BinaryPrimitives.ReadUInt16BigEndian(await ReadExactAsync(ownedStream, 2, ct));
                            bool completingFullRequest;
                            lock (fbLock) completingFullRequest = frameRequestIsFull;
                            if (completingFullRequest && nRects == 0)
                                throw new InvalidDataException("RFB server returned an empty response to a full-frame request.");
                            for (int r = 0; r < nRects; r++)
                            {
                                byte[] head = await ReadExactAsync(ownedStream, 12, ct);
                                int rx = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(0));
                                int ry = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(2));
                                int rw = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(4));
                                int rh = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(6));
                                int encoding = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(8));
                                if (encoding != 0)
                                    throw new InvalidOperationException($"Server sent unsupported encoding {encoding} despite Raw-only SetEncodings.");
                                // A malformed/out-of-date rectangle must never wrap into the next
                                // scanline. The previous flattened-length check allowed exactly that,
                                // leaving black/white blocks while keeping the connection apparently up.
                                if (rw <= 0 || rh <= 0 || rx + rw > ownedWidth || ry + rh > ownedHeight)
                                    throw new InvalidDataException($"RFB rectangle ({rx},{ry}) {rw}x{rh} is outside framebuffer {ownedWidth}x{ownedHeight}.");
                                long pixelBytes = (long)rw * rh * 4;
                                if (pixelBytes > int.MaxValue)
                                    throw new InvalidDataException($"RFB rectangle {rw}x{rh} is too large.");
                                byte[] pixels = await ReadExactAsync(ownedStream, (int)pixelBytes, ct);
                                lock (fbLock)
                                {
                                    if (framebufferGeneration != generation) return;
                                    for (int row = 0; row < rh; row++)
                                    {
                                        int src = row * rw * 4;
                                        int dst = ((ry + row) * ownedWidth + rx) * 4;
                                        Buffer.BlockCopy(pixels, src, framebuffer, dst, rw * 4);
                                    }
                                }
                            }
                            TaskCompletionSource<bool>? completedWaiter;
                            lock (fbLock)
                            {
                                if (framebufferGeneration != generation) return;
                                if (frameRequestIsFull) hasCompleteFrame = true;
                                if (nRects > 0) frameVersion++;
                                completedWaiter = frameWaiter;
                                frameWaiter = null;
                                frameRequestIsFull = false;
                            }
                            completedWaiter?.TrySetResult(true);
                            break;
                        }
                        case 1: // SetColourMapEntries — impossible in true-colour, but consume it
                        {
                            await ReadExactAsync(ownedStream, 1, ct);
                            await ReadExactAsync(ownedStream, 2, ct);
                            int nColours = BinaryPrimitives.ReadUInt16BigEndian(await ReadExactAsync(ownedStream, 2, ct));
                            await ReadExactAsync(ownedStream, nColours * 6, ct);
                            break;
                        }
                        case 2: // Bell
                            break;
                        case 3: // ServerCutText
                        {
                            await ReadExactAsync(ownedStream, 3, ct);
                            uint len = BinaryPrimitives.ReadUInt32BigEndian(await ReadExactAsync(ownedStream, 4, ct));
                            if (len > 16 * 1024 * 1024)
                                throw new InvalidDataException($"RFB clipboard announcement is too large ({len} bytes).");
                            string announced = System.Text.Encoding.UTF8.GetString(await ReadExactAsync(ownedStream, (int)len, ct));
                            if (IsCurrentConnection(ownedStream, generation)) clipboardText = announced;
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
                DisposeConnection(generation, new IOException("VNC connection lost.", ex));
            }
        }

        private bool IsCurrentConnection(NetworkStream candidate, int generation)
        {
            lock (connectionLock)
                return connected && connectionGeneration == generation && ReferenceEquals(stream, candidate);
        }

        private static async Task<byte[]> ReadExactAsync(NetworkStream source, int count, CancellationToken ct)
        {
            var buf = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = await source.ReadAsync(buf.AsMemory(read, count - read), ct);
                if (n == 0) throw new IOException("VNC connection closed by server.");
                read += n;
            }
            return buf;
        }

        private static async Task<string> ReadReasonStringAsync(NetworkStream source, CancellationToken ct)
        {
            uint len = BinaryPrimitives.ReadUInt32BigEndian(await ReadExactAsync(source, 4, ct));
            return System.Text.Encoding.UTF8.GetString(await ReadExactAsync(source, (int)Math.Min(len, 4096), ct));
        }

        private void DisposeConnection(int? expectedGeneration = null, Exception? pendingError = null)
        {
            CancellationTokenSource? oldCts;
            NetworkStream? oldStream;
            TcpClient? oldTcp;
            lock (connectionLock)
            {
                // A receive loop owns exactly the stream/generation it was started with. A late
                // failure from an abandoned loop must not tear down a successful reconnect.
                if (expectedGeneration.HasValue && expectedGeneration.Value != connectionGeneration)
                    return;
                connected = false;
                connectionGeneration++;
                oldCts = loopCts;
                oldStream = stream;
                oldTcp = tcp;
                loopCts = null;
                stream = null;
                tcp = null;
                receiveLoop = null;
            }

            TaskCompletionSource<bool>? waiter;
            lock (fbLock)
            {
                waiter = frameWaiter;
                frameWaiter = null;
                frameRequestIsFull = false;
            }
            if (pendingError != null) waiter?.TrySetException(pendingError);
            else waiter?.TrySetCanceled();

            try { oldCts?.Cancel(); } catch { }
            try { oldStream?.Dispose(); } catch { }
            try { oldTcp?.Dispose(); } catch { }
            try { oldCts?.Dispose(); } catch { }
            buttonMask = 0;
        }

        public void Dispose()
        {
            DisposeConnection();
            sendGate.Dispose();
            connectGate.Dispose();
            captureGate.Dispose();
        }
    }
}
