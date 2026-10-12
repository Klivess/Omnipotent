using System.Net.WebSockets;
using System.Threading.Channels;

namespace Omnipotent.Services.KliveChat
{
    /// <summary>
    /// One socket's outbound side. Every frame goes through a bounded queue drained by a single writer, so a
    /// broadcast never waits on a slow participant: a client that can't keep up fills its own queue and is
    /// disconnected, instead of stalling everyone else in the room (the old per-client send lock did that).
    /// </summary>
    public sealed class KliveChatConnection
    {
        public const int OutboundCapacity = 512;
        public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

        private readonly Channel<byte[]> outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(OutboundCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        private readonly CancellationTokenSource cts = new();
        private Task writer = Task.CompletedTask;
        private int started;
        private int closing;

        public KliveChatConnection(WebSocket socket)
        {
            Socket = socket;
        }

        public WebSocket Socket { get; }

        /// <summary>Cancelled when the connection is torn down; the receive loop listens on it.</summary>
        public CancellationToken Token => cts.Token;

        /// <summary>Set when the server ended the connection: the reason the client is told.</summary>
        public string? ServerCloseReason { get; private set; }

        /// <summary>The server closed it while removing or replacing its participant, so no grace period follows.</summary>
        public bool ClosedForRemoval { get; private set; }

        public bool IsClosing => Volatile.Read(ref closing) == 1;

        public void Start()
        {
            if (Interlocked.Exchange(ref started, 1) == 0) writer = Task.Run(WriteLoopAsync);
        }

        /// <summary>Queues a frame. False when the client has fallen too far behind or the connection is closing.</summary>
        public bool TryEnqueue(byte[] frame) => !IsClosing && outbound.Writer.TryWrite(frame);

        private async Task WriteLoopAsync()
        {
            try
            {
                await foreach (byte[] frame in outbound.Reader.ReadAllAsync(cts.Token))
                {
                    using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    sendCts.CancelAfter(SendTimeout);
                    await Socket.SendAsync(frame, WebSocketMessageType.Text, true, sendCts.Token);
                }
            }
            catch
            {
                Abort();
            }
        }

        /// <summary>
        /// Sends what is already queued (a removal notice, say), then closes with a reason the client acts on.
        /// Never throws. A client that doesn't answer the close handshake is cut off a few seconds later.
        /// </summary>
        public async Task CloseAsync(WebSocketCloseStatus status, string reason, bool forRemoval)
        {
            if (Interlocked.Exchange(ref closing, 1) == 1) return;
            ServerCloseReason = reason;
            ClosedForRemoval = forRemoval;
            outbound.Writer.TryComplete();
            try { await writer.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            try
            {
                if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await Socket.CloseOutputAsync(status, reason, closeCts.Token);
                }
                try { cts.CancelAfter(TimeSpan.FromSeconds(3)); } catch (ObjectDisposedException) { }
            }
            catch
            {
                Abort();
            }
        }

        /// <summary>Drops the connection immediately (network fault, heartbeat timeout, slow consumer).</summary>
        public void Abort()
        {
            Interlocked.Exchange(ref closing, 1);
            outbound.Writer.TryComplete();
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            try { Socket.Abort(); } catch { }
        }
    }
}
