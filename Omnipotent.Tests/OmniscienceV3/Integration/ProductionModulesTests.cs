using System.Collections.Concurrent;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.OmniscienceV3;
using Omnipotent.Services.OmniscienceV3.Ingest.Media;
using Omnipotent.Services.OmniscienceV3.Kernel;
using Omnipotent.Services.OmniscienceV3.Knowledge;
using Omnipotent.Services.OmniscienceV3.Llm;
using Omnipotent.Services.OmniscienceV3.Ml.Encoders;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Temporal;
using Omnipotent.Tests.OmniscienceV3.Ingest;

namespace Omnipotent.Tests.OmniscienceV3.Integration
{
    /// <summary>
    /// The engine as the service builds it: every workstream's modules in one kernel, from the same list the service uses
    /// (<see cref="ProductionModules"/>). Each workstream tested its modules alone; this is where they meet.
    /// </summary>
    public class ProductionModulesTests : IAsyncLifetime
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "omniv3_production_" + Guid.NewGuid().ToString("N"));
        private readonly FixedClock clock = new(Instant.FromUtc(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc)));

        public Task InitializeAsync() => Task.CompletedTask;

        public Task DisposeAsync()
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* a reader handle may linger on Windows; the temp dir is unique per test */ }
            return Task.CompletedTask;
        }

        private sealed class NullLlm : ILlmClient
        {
            public Task<LlmResult> QueryAsync(LlmRequest request, CancellationToken ct) => throw new InvalidOperationException("No LLM in this test.");
            public Task<ModelContextInfo> GetContextAsync(string? model, CancellationToken ct) =>
                Task.FromResult(new ModelContextInfo("AIRouter", "test", 260_000, 16_384, "operator-stated", null, 0, null));
            public void EndSession(string sessionKey) { }
        }

        private IReadOnlyList<Omnipotent.Services.OmniscienceV3.Kernel.Modules.IIntelModule> Modules() => ProductionModules.Create(new ProductionModuleInputs
        {
            LegacyDatabase = () => Path.Combine(root, "no-legacy-database.db"),
            DiscordFeed = new FakeFeed { Connected = false },
            KliveBotToken = () => Task.FromResult<string?>(null),
            OwnerDiscordUserId = DiscordFixtures.Owner,
            Transcriber = () => Task.FromResult<ITranscriber?>(null),
        });

        [Fact]
        public async Task EveryProductionModule_StartsTogether_WithNothingClaimedTwice_AndTheKnowledgeLayerWired()
        {
            var errors = new ConcurrentQueue<string>();
            var modules = Modules();
            var rt = await V3Runtime.StartAsync(new V3RuntimeOptions
            {
                Paths = new StorePaths(Path.Combine(root, "v3")), Clock = clock, LlmClient = new NullLlm(), Log = _ => { },
                LogError = (m, e) => errors.Enqueue(m + " " + e?.Message),
                Kernel = new KernelOptions { StartWorkers = false, StartLoop = false },
                Modules = modules, LockWait = TimeSpan.FromSeconds(5), Encoder = new HashingTextEncoder(),
            });
            try
            {
                // Every module started (plus the kernel's own tactic runner), and nothing is claimed by two of them.
                Assert.Equal(modules.Count + 1, rt.Host.Modules.Count);
                static void Unique(string what, IEnumerable<string> ids)
                {
                    var twice = ids.GroupBy(x => x, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                    Assert.True(twice.Count == 0, $"{what} claimed twice: {string.Join(", ", twice)}");
                }
                Unique("module ids", modules.Select(m => m.Manifest.Id));
                Unique("setting keys", modules.SelectMany(m => m.Manifest.Settings).Select(s => s.Key));
                Unique("dataset ids", modules.SelectMany(m => m.Manifest.Datasets).Select(d => d.Id));
                Unique("read models", modules.SelectMany(m => m.Manifest.ReadModels).Select(r => r.Id));
                Unique("tactic keys", rt.Host.Modules.SelectMany(m => m.Tactics).Select(t => t.Descriptor.Key));
                Unique("tactic codes", rt.Host.Modules.SelectMany(m => m.Tactics).Select(t => t.Descriptor.Code));

                // One pass of the kernel loop: every module's change mapping and planner run on the empty engine.
                await rt.Host.PumpChangesOnceAsync();
                await rt.Host.PlanOnceAsync();

                // The plug-in points left open by the workstreams are filled.
                var kit = KnowledgeKit.For(rt.Host.Context);
                Assert.NotNull(kit.Explore);
                Assert.NotNull(kit.Translations);
                Assert.NotNull(kit.Encoder);
                Assert.IsType<ClassifierModalityOracle>(kit.Modality);
                Assert.Empty(errors);
            }
            finally
            {
                await rt.DisposeAsync();
            }
        }
    }
}
