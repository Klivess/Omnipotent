using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveChat;

namespace Omnipotent.Tests.KliveChat;

/// <summary>
/// An in-memory socket: the test plays the browser (<see cref="ClientSend"/>, <see cref="ClientClose"/>,
/// <see cref="Drop"/>) and reads what the server sent with <see cref="ExpectAsync"/>. A server-initiated close
/// is answered like a browser would answer it, so the room's receive loop sees the handshake complete.
/// </summary>
internal sealed class FakeWebSocket : WebSocket
{
    private sealed record Frame(byte[] Data, WebSocketMessageType Type, bool Drop);

    private readonly Channel<Frame> inbound = Channel.CreateUnbounded<Frame>();
    private readonly object sync = new();
    private readonly List<JObject> sent = new();
    private readonly HashSet<int> consumed = new();
    private Frame? partial;
    private int partialOffset;
    private volatile WebSocketState state = WebSocketState.Open;

    public WebSocketCloseStatus? ServerCloseStatus { get; private set; }
    public string? ServerCloseReason { get; private set; }

    public override WebSocketCloseStatus? CloseStatus => ServerCloseStatus;
    public override string? CloseStatusDescription => ServerCloseReason;
    public override WebSocketState State => state;
    public override string? SubProtocol => null;

    // ── the browser's side ──

    public void ClientSend(object message) => ClientSendRaw(JsonConvert.SerializeObject(message));

    public void ClientSendRaw(string json) =>
        inbound.Writer.TryWrite(new Frame(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, false));

    /// <summary>The browser hangs up cleanly (leave button, tab closed).</summary>
    public void ClientClose() => inbound.Writer.TryWrite(new Frame(Array.Empty<byte>(), WebSocketMessageType.Close, false));

    /// <summary>The network goes away mid-call.</summary>
    public void Drop() => inbound.Writer.TryWrite(new Frame(Array.Empty<byte>(), WebSocketMessageType.Text, true));

    public IReadOnlyList<JObject> All(string type)
    {
        lock (sync) return sent.Where(m => (string?)m["type"] == type).ToList();
    }

    /// <summary>The first not-yet-consumed message of <paramref name="type"/> (matching <paramref name="where"/>), waiting up to two seconds.</summary>
    public async Task<JObject> ExpectAsync(string type, Func<JObject, bool>? where = null, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            lock (sync)
            {
                for (int i = 0; i < sent.Count; i++)
                {
                    if (consumed.Contains(i) || (string?)sent[i]["type"] != type || (where != null && !where(sent[i]))) continue;
                    consumed.Add(i);
                    return sent[i];
                }
            }
            if (DateTime.UtcNow > deadline)
            {
                string seen;
                lock (sync) seen = string.Join(", ", sent.Select(m => (string?)m["type"]));
                throw new Xunit.Sdk.XunitException($"Expected a '{type}' message; received: [{seen}]");
            }
            await Task.Delay(10);
        }
    }

    public async Task AssertNothingAsync(string type, int withinMs = 150)
    {
        await Task.Delay(withinMs);
        lock (sync)
        {
            for (int i = 0; i < sent.Count; i++)
            {
                if (!consumed.Contains(i) && (string?)sent[i]["type"] == type)
                    throw new Xunit.Sdk.XunitException($"Did not expect a '{type}' message: {sent[i].ToString(Formatting.None)}");
            }
        }
    }

    public async Task WaitClosedAsync(int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (ServerCloseReason == null && state is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            if (DateTime.UtcNow > deadline) throw new Xunit.Sdk.XunitException("The server never closed the socket.");
            await Task.Delay(10);
        }
    }

    // ── the server's side ──

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        Frame frame = partial ?? await inbound.Reader.ReadAsync(cancellationToken);
        if (frame.Drop)
        {
            state = WebSocketState.Aborted;
            throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
        }
        if (frame.Type == WebSocketMessageType.Close)
        {
            state = state == WebSocketState.CloseSent ? WebSocketState.Closed : WebSocketState.CloseReceived;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "bye");
        }
        int count = Math.Min(buffer.Count, frame.Data.Length - partialOffset);
        Array.Copy(frame.Data, partialOffset, buffer.Array!, buffer.Offset, count);
        partialOffset += count;
        bool end = partialOffset >= frame.Data.Length;
        if (end)
        {
            partial = null;
            partialOffset = 0;
        }
        else
        {
            partial = frame;
        }
        return new WebSocketReceiveResult(count, frame.Type, end);
    }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        if (state is not (WebSocketState.Open or WebSocketState.CloseReceived)) throw new WebSocketException(WebSocketError.InvalidState);
        var message = JObject.Parse(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
        lock (sync) sent.Add(message);
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        ServerCloseStatus = closeStatus;
        ServerCloseReason = statusDescription;
        state = state == WebSocketState.CloseReceived ? WebSocketState.Closed : WebSocketState.CloseSent;
        // A browser answers the close handshake.
        inbound.Writer.TryWrite(new Frame(Array.Empty<byte>(), WebSocketMessageType.Close, false));
        return Task.CompletedTask;
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        ServerCloseStatus = closeStatus;
        ServerCloseReason = statusDescription;
        state = WebSocketState.Closed;
        inbound.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public override void Abort()
    {
        if (state != WebSocketState.Closed) state = WebSocketState.Aborted;
        inbound.Writer.TryComplete(new WebSocketException(WebSocketError.ConnectionClosedPrematurely));
    }

    public override void Dispose() { }
}

internal sealed class FakeChatHost : IKliveChatHost
{
    private readonly object sync = new();
    public DateTime Now { get; set; } = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
    public DateTime UtcNow => Now;
    public List<string> Logs { get; } = new();
    public int Changes;
    public int Persists;

    public void Log(string message)
    {
        lock (sync) Logs.Add(message);
    }

    public void LogError(Exception error, string context)
    {
        lock (sync) Logs.Add($"ERROR {context}: {error.Message}");
    }

    public void RoomChanged(KliveChatRoom room, bool persist)
    {
        Interlocked.Increment(ref Changes);
        if (persist) Interlocked.Increment(ref Persists);
    }

    public void Advance(TimeSpan by) => Now += by;
}
