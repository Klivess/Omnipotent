using System.Net;
using System.Security.Cryptography;
using Omnipotent.Services.OmniscienceV3.Ml;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Tests.OmniscienceV3.Ml
{
    public class MlRuntimeTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_models_" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new(1_700_000_000_000);

        public void Dispose()
        {
            try { Directory.Delete(root, true); }
            catch (IOException) { /* unique temp dir */ }
        }

        /// <summary>Serves a byte payload, honours Range requests, can fail mid-stream once, and counts requests.</summary>
        private sealed class FakeModelServer : HttpMessageHandler
        {
            public byte[] Payload { get; set; } = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray();
            public int Requests;
            public int CutAfterBytes = -1;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Interlocked.Increment(ref Requests);
                await Task.Delay(20, ct);
                long from = request.Headers.Range?.Ranges.First().From ?? 0;
                var slice = Payload.Skip((int)from).ToArray();
                if (CutAfterBytes > 0) { slice = slice.Take(CutAfterBytes).ToArray(); CutAfterBytes = -1; }
                var resp = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(slice),
                };
                return resp;
            }
        }

        private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

        private ModelHub Hub(FakeModelServer server) => new(root, new HttpClient(server), clock, _ => { });

        private static ModelArtifact Artifact(string? sha = null, string licence = "MIT") =>
            new("org/model@rev", "model.onnx", new Uri("https://example.test/model.onnx"), sha, licence);

        [Fact]
        public async Task FirstDownload_RecordsTheHashAndLaterLoadsEnforceIt()
        {
            var server = new FakeModelServer();
            var hub = Hub(server);
            string path = await hub.EnsureAsync(Artifact());
            Assert.Equal(Sha(server.Payload), hub.ReadLock()["org/model@rev/model.onnx"].Sha256);

            // Tamper with the file on disk: the next load detects it and re-fetches the pinned bytes.
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            await hub.EnsureAsync(Artifact());
            Assert.Equal(server.Payload.Length, new FileInfo(path).Length);

            // The upstream changes underneath a pinned hash: refused.
            File.Delete(path);
            server.Payload = new byte[] { 9, 9, 9 };
            await Assert.ThrowsAsync<ModelIntegrityException>(() => hub.EnsureAsync(Artifact()));
            Assert.False(File.Exists(path));
        }

        [Fact]
        public async Task ExplicitHash_MustMatch()
        {
            var server = new FakeModelServer();
            await Assert.ThrowsAsync<ModelIntegrityException>(() => Hub(server).EnsureAsync(Artifact(sha: new string('A', 64))));
            string ok = await Hub(server).EnsureAsync(Artifact(sha: Sha(server.Payload)));
            Assert.True(File.Exists(ok));
        }

        [Fact]
        public async Task InterruptedDownload_Resumes()
        {
            var server = new FakeModelServer();
            var hub = Hub(server);
            var a = Artifact(sha: Sha(server.Payload));
            // First attempt is cut short: the truncated .part fails the hash and is discarded.
            server.CutAfterBytes = 50_000;
            await Assert.ThrowsAsync<ModelIntegrityException>(() => hub.EnsureAsync(a));
            string path = await hub.EnsureAsync(a);
            Assert.Equal(server.Payload, File.ReadAllBytes(path));
        }

        [Fact]
        public async Task ConcurrentCallers_ShareOneDownload()
        {
            var server = new FakeModelServer();
            var hub = Hub(server);
            var paths = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => hub.EnsureAsync(Artifact())));
            Assert.Single(paths.Distinct());
            Assert.Equal(1, server.Requests);
        }

        [Fact]
        public async Task ArtefactWithoutALicence_IsNeverActivated()
        {
            var hub = Hub(new FakeModelServer());
            await Assert.ThrowsAsync<InvalidOperationException>(() => hub.EnsureAsync(Artifact(licence: "")));
        }

        [Fact]
        public void HeadTailTruncation_KeepsBothEnds()
        {
            var ids = Enumerable.Range(0, 100).ToList();
            var cut = Truncation.HeadTail(ids, 10);
            Assert.Equal(10, cut.Count);
            Assert.Equal(0, cut[0]);
            Assert.Equal(99, cut[^1]);
            Assert.Same(ids, Truncation.HeadTail(ids, 200));
        }

        [Theory]
        [InlineData(0, 3)]     // SentencePiece <unk> → HF <unk>
        [InlineData(5, 6)]     // ordinary piece shifted by the fairseq offset
        [InlineData(250000, 250001)]
        public void XlmRoberta_UsesTheFairseqIdLayout(int spmId, long expected) =>
            Assert.Equal(expected, XlmRobertaTextTokenizer.ToFairseqId(spmId));

        [Fact]
        public void SignalFusion_AbstainsAndMeasuresDisagreement()
        {
            var positive = new Dictionary<string, double> { ["pos"] = 0.9, ["neg"] = 0.1 };
            var agree = SignalFusion.Fuse(new[] { ((IReadOnlyDictionary<string, double>)positive, 1.0), (positive, 2.0) });
            Assert.Equal("pos", agree.Label);
            Assert.Equal(0, agree.Disagreement);

            var leanPos = new Dictionary<string, double> { ["pos"] = 0.7, ["neg"] = 0.3 };
            var leanNeg = new Dictionary<string, double> { ["pos"] = 0.3, ["neg"] = 0.7 };
            var split = SignalFusion.Fuse(new[] { ((IReadOnlyDictionary<string, double>)leanPos, 1.0), (leanNeg, 1.0) });
            Assert.Null(split.Label); // 0.5 vs 0.5: below the floor and inside the margin → abstain
            Assert.Equal(0.5, split.Disagreement);

            Assert.Null(SignalFusion.Fuse(Array.Empty<(IReadOnlyDictionary<string, double>, double)>()).Label);
        }
    }
}
