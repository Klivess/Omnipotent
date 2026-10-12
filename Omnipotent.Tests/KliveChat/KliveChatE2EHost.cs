using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Omnipotent.Profiles;
using Omnipotent.Services.KliveChat;

namespace Omnipotent.Tests.KliveChat;

/// <summary>
/// Serves the real <see cref="KliveChatRoom"/> over a plain local WebSocket so the website's Playwright call
/// test (tests/e2e/klivechat.spec.ts) can run two real browsers against the real room engine. Does nothing
/// unless KLIVECHAT_E2E_PORT is set; then it serves until KLIVECHAT_E2E_MINUTES (default 10) run out:
///
///   KLIVECHAT_E2E_PORT=5291 DOTNET_ROLL_FORWARD=Major dotnet test Omnipotent.Tests --filter FullyQualifiedName~KliveChatE2EHost
///
/// The socket's ?authorization=kms_e2e signs in as Klives (the room's creator); anyone else is a guest.
/// </summary>
public class KliveChatE2EHost
{
    public const string RoomId = "e2eroom234";

    private sealed class ConsoleHost : IKliveChatHost
    {
        public DateTime UtcNow => DateTime.UtcNow;
        public void Log(string message) => Console.WriteLine($"[klivechat] {message}");
        public void LogError(Exception error, string context) => Console.WriteLine($"[klivechat] ERROR {context}: {error}");
        public void RoomChanged(KliveChatRoom room, bool persist) { }
    }

    [Fact]
    public async Task Serve()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("KLIVECHAT_E2E_PORT"), out int port)) return;
        int minutes = int.TryParse(Environment.GetEnvironmentVariable("KLIVECHAT_E2E_MINUTES"), out int m) ? m : 10;

        var host = new ConsoleHost();
        var rooms = new ConcurrentDictionary<string, KliveChatRoom>(StringComparer.Ordinal);
        DateTime now = DateTime.UtcNow;
        rooms[RoomId] = new KliveChatRoom(new KliveChatRoomRecord
        {
            Id = RoomId, Name = "Standup", CreatedBy = "Klives", CreatedByUserId = "klives", CreatedUtc = now, LastActiveUtc = now,
        }, host);

        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(minutes));
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine($"[klivechat] e2e host listening on ws://127.0.0.1:{port}/klivechat/ws");

        _ = Task.Run(async () =>
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(1000);
                foreach (var room in rooms.Values) room.Sweep();
            }
        });

        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(lifetime.Token);
                _ = Task.Run(() => ServeClientAsync(client, rooms, host));
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task ServeClientAsync(TcpClient client, ConcurrentDictionary<string, KliveChatRoom> rooms, IKliveChatHost host)
    {
        using var _ = client;
        try
        {
            NetworkStream stream = client.GetStream();
            string head = await ReadHeadAsync(stream);
            string[] lines = head.Split("\r\n");
            string[] requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2) return;
            var uri = new Uri("http://localhost" + requestLine[1]);
            string? key = lines.Skip(1).Select(l => l.Split(':', 2)).Where(p => p.Length == 2 && p[0].Trim().Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
                .Select(p => p[1].Trim()).FirstOrDefault();
            if (uri.AbsolutePath != "/klivechat/ws" || key == null)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n"));
                return;
            }
            string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"));
            using WebSocket socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(30) });

            var query = HttpUtility.ParseQueryString(uri.Query);
            string? roomId = query["roomId"];
            // Each test run asks for its own e2e… room, so one long-lived host never leaks chat between runs.
            if (roomId != null && roomId.StartsWith("e2e", StringComparison.Ordinal) && KliveChatText.IsValidRoomId(roomId))
            {
                rooms.GetOrAdd(roomId, id => new KliveChatRoom(new KliveChatRoomRecord
                {
                    Id = id, Name = "Standup", CreatedBy = "Klives", CreatedByUserId = "klives", CreatedUtc = DateTime.UtcNow, LastActiveUtc = DateTime.UtcNow,
                }, host));
            }
            if (string.IsNullOrEmpty(roomId) || !rooms.TryGetValue(roomId, out var room) || room.IsClosed)
            {
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Invalid Room ID", CancellationToken.None);
                return;
            }

            bool klives = query["authorization"] == "kms_e2e";
            string? secret = query["secret"];
            bool resumable = KliveChatText.IsValidSecret(secret);
            var join = new KliveChatJoinRequest
            {
                ParticipantId = resumable ? KliveChatText.ParticipantIdFromSecret(roomId, secret!) : KliveChatText.RandomParticipantId(),
                Resumable = resumable,
                Name = klives ? "Klives" : KliveChatText.CleanDisplayName(query["name"]) ?? "Guest",
                UserId = klives ? "klives" : null,
                Rank = klives ? ProfileRank.Klives : ProfileRank.None,
                HoldsModeratePermission = klives,
                HoldsDeleteAnyPermission = klives,
                IdentityKey = klives ? "profile:klives" : $"guest:{query["guestIdentity"] ?? Guid.NewGuid().ToString("N")}",
                IpAddress = "127.0.0.1",
                Protocol = int.TryParse(query["protocol"], out int protocol) ? protocol : 1,
                IceServers = new List<KliveChatIceServer>(), // loopback only: no STUN round trips in the test
            };
            await room.RunConnectionAsync(join, socket);
        }
        catch (Exception ex) when (ex is IOException or WebSocketException or SocketException or ObjectDisposedException)
        {
        }
    }

    private static async Task<string> ReadHeadAsync(NetworkStream stream)
    {
        var buffer = new List<byte>(1024);
        var one = new byte[1];
        while (buffer.Count < 16 * 1024)
        {
            int read = await stream.ReadAsync(one);
            if (read == 0) break;
            buffer.Add(one[0]);
            int n = buffer.Count;
            if (n >= 4 && buffer[n - 4] == '\r' && buffer[n - 3] == '\n' && buffer[n - 2] == '\r' && buffer[n - 1] == '\n') break;
        }
        return Encoding.ASCII.GetString(buffer.ToArray());
    }
}
