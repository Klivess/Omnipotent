using System.Net.WebSockets;
using KliveLink.Contracts;
using Omnipotent.Services.KliveLink;

namespace Omnipotent.Tests.KliveLink;

public sealed class KliveLinkV2HandshakeTests
{
    [Fact]
    public async Task Server_AcceptsHandshake_AndCorrelatesACommand()
    {
        string root = Path.Combine(Path.GetTempPath(), "klive-v2-handshake", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int port = 5217;
        var options = new KliveLinkV2Options
        {
            ListenPrefix = $"http://127.0.0.1:{port}/",
            DataDirectory = root,
            DbPath = Path.Combine(root, "v2.db"),
            KeyPath = Path.Combine(root, "v2.key"),
            CollectionDirectory = Path.Combine(root, "collections"),
            TokenHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { V2Proof.Sha256Hex("test-token") },
            AllowDevToken = false,
        };
        var store = new KliveLinkV2Store(options.DbPath, options.KeyPath, options.CollectionDirectory);
        var service = new KliveLinkV2Service(store, options);
        service.Server.Start();
        try
        {
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("X-Agent-Id", "handshake-agent");
            socket.Options.SetRequestHeader("X-Enrollment-Token", "test-token");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), cts.Token);

            string clientNonce = "client-nonce";
            var hello = new V2Hello
            {
                AgentId = "handshake-agent",
                MachineName = "TEST",
                OsVersion = "Windows",
                AgentVersion = "2.0.0",
                Nonce = clientNonce,
                Capabilities = new[] { "sysinfo" },
            };
            await V2FrameCodec.SendAsync(socket, new V2Frame
            {
                Type = V2FrameType.Hello,
                Payload = V2Json.Serialize(hello),
            }, cts.Token);
            V2Frame challengeFrame = await V2FrameCodec.ReadFrameAsync(socket, cts.Token);
            Assert.Equal(V2FrameType.Challenge, challengeFrame.Type);
            V2Challenge challenge = V2Json.Deserialize<V2Challenge>(challengeFrame.Payload)!;
            await V2FrameCodec.SendAsync(socket, new V2Frame
            {
                Type = V2FrameType.Auth,
                Payload = V2Json.Serialize(new V2Auth
                {
                    Proof = V2Proof.Compute("test-token", "handshake-agent", clientNonce, challenge.ServerNonce),
                }),
            }, cts.Token);
            V2Frame welcome = await V2FrameCodec.ReadFrameAsync(socket, cts.Token);
            Assert.Equal(V2FrameType.Welcome, welcome.Type);
            await Task.Delay(250);
            if (service.Server.LastError != null) throw new Exception(service.Server.LastError);

            Task<V2Result> commandTask = service.Server.SendCommandAsync(
                "handshake-agent", V2CommandOperation.GetSystemInfo, new { }, 5000, "test");
            V2Frame commandFrame = await V2FrameCodec.ReadFrameAsync(socket, cts.Token);
            Assert.Equal(V2FrameType.Command, commandFrame.Type);
            V2Command command = V2Json.Deserialize<V2Command>(commandFrame.Payload)!;
            await V2FrameCodec.SendAsync(socket, new V2Frame
            {
                Type = V2FrameType.Result,
                Payload = V2Json.Serialize(new V2Result
                {
                    CommandId = command.CommandId,
                    Status = V2CommandStatus.Ok,
                    Payload = V2Json.Serialize(new { machineName = "TEST" }),
                }),
            }, cts.Token);
            V2Result result = await commandTask;
            Assert.Equal(V2CommandStatus.Ok, result.Status);
            Assert.Contains("TEST", result.Payload);
            Assert.NotNull(store.GetAgent("handshake-agent"));
        }
        finally
        {
            service.Server.Stop();
            store.Dispose();
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
