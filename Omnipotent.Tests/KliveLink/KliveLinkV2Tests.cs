using KliveLink.Contracts;
using Omnipotent.Services.KliveLink;

namespace Omnipotent.Tests.KliveLink;

public sealed class KliveLinkV2Tests
{
    [Fact]
    public void Frame_RoundTrips_AndDetectsTamper()
    {
        var frame = new V2Frame
        {
            Type = V2FrameType.Command,
            StreamId = 7,
            Sequence = 42,
            Payload = "{\"hello\":\"world\"}",
        };
        byte[] bytes = frame.ToBytes();
        Assert.True(V2Frame.TryParse(bytes, out V2Frame? parsed, out int consumed));
        Assert.Equal(bytes.Length, consumed);
        Assert.NotNull(parsed);
        Assert.Equal(V2FrameType.Command, parsed!.Type);
        Assert.Equal(7u, parsed.StreamId);
        Assert.Equal(42ul, parsed.Sequence);
        Assert.Equal(frame.Payload, parsed.Payload);

        bytes[10] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => V2Frame.TryParse(bytes, out _, out _));
    }

    [Fact]
    public void Proof_IsDeterministic()
    {
        string a = V2Proof.Compute("dev-token", "agent-1", "nonce-a", "nonce-b");
        string b = V2Proof.Compute("dev-token", "agent-1", "nonce-a", "nonce-b");
        string c = V2Proof.Compute("dev-token", "agent-1", "nonce-a", "nonce-c");
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Store_UpsertsAgent_AndPurgesAllData()
    {
        string root = Path.Combine(Path.GetTempPath(), "klive-v2-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new KliveLinkV2Store(
                Path.Combine(root, "v2.db"),
                Path.Combine(root, "v2.key"),
                Path.Combine(root, "collections"));

            string agentId = "agent-test";
            store.UpsertAgent(new KliveLinkV2AgentRecord
            {
                AgentId = agentId,
                MachineName = "TEST-PC",
                OsVersion = "Windows 11",
                AgentVersion = "2.0.0",
                FirstSeen = DateTimeOffset.UtcNow,
                LastSeen = DateTimeOffset.UtcNow,
                ConnectedAt = DateTimeOffset.UtcNow,
                IsConnected = true,
                TokenHash = "abc",
            });
            Assert.Equal("TEST-PC", store.GetAgent(agentId)!.MachineName);
            Assert.Single(store.ListAgents());

            string snapshotId = "snap-1";
            store.CreateSnapshot(new KliveLinkV2SnapshotRecord
            {
                SnapshotId = snapshotId,
                AgentId = agentId,
                Tier = "T0",
                Profile = "t0-fast",
                Status = "complete",
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                Bytes = 128,
                Sha256 = "sha",
                ManifestJson = "{}",
                BundlePath = "",
            }, new[]
            {
                new V2CollectionRecord
                {
                    Collector = "chromium",
                    Category = "browser.password",
                    SourcePath = "https://example.test",
                    Name = "user",
                    Username = "user",
                    Secret = "hunter2",
                    Url = "https://example.test",
                },
            }, "");

            Assert.Single(store.ListSnapshots(agentId));
            var records = store.SearchRecords(agentId, null, null);
            Assert.Single(records);
            var revealed = store.GetRecord(records[0].RecordId, revealSecret: true);
            Assert.Equal("hunter2", revealed!.Secret);
            store.RecordCommand("cmd-1", agentId, "test", "GetSystemInfo", "{}", "ok", DateTimeOffset.UtcNow);

            var summary = store.GetDataSummary(agentId);
            Assert.Equal(1, summary.Commands);
            Assert.Equal(1, summary.Snapshots);
            Assert.Equal(1, summary.Records);

            var (purged, _) = store.PurgeAgent(agentId, keepAudit: false);
            Assert.Equal(1, purged.Records);
            Assert.Null(store.GetAgent(agentId));
            Assert.Empty(store.ListSnapshots(agentId));
            Assert.Empty(store.SearchRecords(agentId, null, null));

            byte[] bundle = { 1, 2, 3, 4, 5 };
            string bundlePath = Path.Combine(root, "bundle.zip");
            File.WriteAllBytes(bundlePath, bundle);
            string encryptedPath = store.EncryptBundle(bundlePath);
            Assert.EndsWith(".enc", encryptedPath);
            Assert.Equal(bundle, store.ReadBundle(encryptedPath));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Options_HashesPlainTokens()
    {
        var options = new KliveLinkV2Options
        {
            TokenHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { V2Proof.Sha256Hex("secret-token") },
            AllowDevToken = false,
        };
        Assert.True(options.IsTokenValid("secret-token"));
        Assert.False(options.IsTokenValid("wrong"));
        Assert.False(options.IsTokenValid(""));
    }
}
