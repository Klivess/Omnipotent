using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Omnipotent.Services.OmniscienceV3.Ml;
using Omnipotent.Services.OmniscienceV3.Ml.Encoders;
using Omnipotent.Services.OmniscienceV3.Temporal;
using Xunit.Abstractions;

namespace Omnipotent.Tests.OmniscienceV3.Ml
{
    /// <summary>Runs only with OMNISCIENCE_MODELS=1: downloads the pinned encoder (≈600 MB incl. the fp32 reference) once
    /// into OMNISCIENCE_MODEL_CACHE (default %LOCALAPPDATA%/omniscience-v3-test-models).</summary>
    public sealed class ModelFactAttribute : FactAttribute
    {
        public ModelFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("OMNISCIENCE_MODELS") != "1")
                Skip = "Set OMNISCIENCE_MODELS=1 to run the real-model encoder tests (downloads the pinned multilingual-e5-small builds).";
        }

        public static string CacheDir => Environment.GetEnvironmentVariable("OMNISCIENCE_MODEL_CACHE")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "omniscience-v3-test-models");
    }

    public class EncoderTests
    {
        private readonly ITestOutputHelper output;

        public EncoderTests(ITestOutputHelper output) => this.output = output;

        [Fact]
        public async Task HashingEncoder_IsDeterministicUnitLengthAndSimilarityAware()
        {
            var enc = new HashingTextEncoder(128);
            var v = await enc.EmbedAsync(new[] { "the harbor at night", "the harbour at night!", "quartz velvet zephyr", "" }, CancellationToken.None);
            Assert.All(v.Take(3), x => Assert.InRange(VectorMath.Dot(x, x), 0.999f, 1.001f));
            Assert.Equal(0f, VectorMath.Dot(v[3], v[3])); // nothing to embed: the zero vector, never NaN
            Assert.True(VectorMath.Dot(v[0], v[1]) > VectorMath.Dot(v[0], v[2]) + 0.3f);
            var again = await new HashingTextEncoder(128).EmbedAsync(new[] { "the harbor at night" }, CancellationToken.None);
            Assert.Equal(v[0], again[0]);
        }

        [Fact]
        public void HashingEncoder_TokenisesUnicodeAndEmojiAsWords()
        {
            var words = HashingTextEncoder.Words("Café NAÏVE 日本語 привет 👍🏽 don't").ToList();
            Assert.Contains("café", words);
            Assert.Contains("naïve", words);
            Assert.Contains("日本語", words);
            Assert.Contains("привет", words);
            Assert.Contains("👍🏽", words); // one grapheme: the skin tone stays with its emoji
        }

        [Fact]
        public async Task CpuThrottle_PausesWhileBusyButNeverForever()
        {
            var load = new FixedHostLoad(0.99);
            var throttle = new CpuThrottle(load, 0.85, maxPause: TimeSpan.FromMilliseconds(300), poll: TimeSpan.FromMilliseconds(20));
            var sw = Stopwatch.StartNew();
            await throttle.WaitAsync(CancellationToken.None);
            Assert.InRange(sw.ElapsedMilliseconds, 250, 3_000);
            Assert.Equal(1, throttle.Pauses);

            load.Load = 0.2;
            sw.Restart();
            await throttle.WaitAsync(CancellationToken.None);
            Assert.True(sw.ElapsedMilliseconds < 200);

            load.Load = null; // unknown load: never throttle on a guess
            await throttle.WaitAsync(CancellationToken.None);
            Assert.Equal(1, throttle.Pauses);
        }

        [Fact]
        public async Task CpuThrottle_ResumesAsSoonAsTheLoadDrops()
        {
            var load = new FixedHostLoad(0.95);
            var throttle = new CpuThrottle(load, 0.85, maxPause: TimeSpan.FromSeconds(10), poll: TimeSpan.FromMilliseconds(20));
            var wait = throttle.WaitAsync(CancellationToken.None);
            await Task.Delay(100);
            load.Load = 0.5;
            await wait.WaitAsync(TimeSpan.FromSeconds(3));
        }

        [Fact]
        public void HostCpuMonitor_ReportsAFractionOrUnknown()
        {
            var monitor = new HostCpuMonitor { MinInterval = TimeSpan.Zero };
            monitor.CpuLoad();
            Thread.Sleep(50);
            var load = monitor.CpuLoad();
            if (load is double l) Assert.InRange(l, 0, 1);
        }

        [Fact]
        public void AllocationFailures_AreRecognisedForBatchHalving()
        {
            Assert.True(E5TextEncoder.IsAllocationFailure(new OutOfMemoryException()));
            Assert.False(E5TextEncoder.IsAllocationFailure(new InvalidOperationException("shape mismatch")));
        }

        [Fact]
        public void Catalog_PinsEveryArtefactWithHashSizeAndLicence()
        {
            foreach (var v in EncoderCatalog.All)
            {
                foreach (var a in new[] { v.Graph, v.SentencePiece })
                {
                    Assert.Matches("^[0-9A-F]{64}$", a.ExpectedSha256!);
                    Assert.NotNull(a.ExpectedBytes);
                    Assert.False(string.IsNullOrWhiteSpace(a.Licence));
                    Assert.Contains(EncoderCatalog.Revision, a.Source.ToString());
                }
            }
            var o = new EncoderOptions();
            Assert.NotEqual(EncoderCatalog.Int8.VersionFor(o), EncoderCatalog.Fp32.VersionFor(o));
            Assert.NotEqual(EncoderCatalog.Int8.VersionFor(o), EncoderCatalog.Int8.VersionFor(o with { MaxTokens = 128 }));
        }

        // ── real model (opt-in) ──────────────────────────────────────────────────

        private static ModelHub Hub() => new(ModelFactAttribute.CacheDir, new HttpClient { Timeout = TimeSpan.FromMinutes(30) }, SystemClock.Instance, _ => { });

        /// <summary>Synthetic multilingual paraphrase pairs and an unrelated sentence for each.</summary>
        private static readonly (string A, string B, string Unrelated)[] Pairs =
        {
            ("The train to the coast leaves at seven tomorrow morning.", "Der Zug an die Küste fährt morgen früh um sieben ab.", "My cat refuses to eat the new food."),
            ("I lost my keys somewhere in the park.", "J'ai perdu mes clés quelque part dans le parc.", "The concert was cancelled because of rain."),
            ("Can you send me the report before Friday?", "¿Puedes enviarme el informe antes del viernes?", "Penguins live mostly in the southern hemisphere."),
            ("This soup is far too salty.", "Questa zuppa è decisamente troppo salata.", "We should repaint the garage next spring."),
            ("happy birthday!! hope you have the best day 🎉", "alles gute zum geburtstag!! 🎉", "the server crashed again during the update"),
        };

        [ModelFact]
        public async Task RealEncoder_Int8AgreesWithFp32AndRanksParaphrasesFirst()
        {
            using var onnx = new OnnxRuntimeHost(2, false, _ => { });
            var hub = Hub();
            var int8 = await E5TextEncoder.CreateAsync(hub, onnx, EncoderCatalog.Int8);
            var fp32 = await E5TextEncoder.CreateAsync(hub, onnx, EncoderCatalog.Fp32);
            var texts = Pairs.SelectMany(p => new[] { p.A, p.B, p.Unrelated }).ToList();

            var sw = Stopwatch.StartNew();
            var a = await int8.EmbedAsync(texts, CancellationToken.None);
            output.WriteLine($"int8: {texts.Count} texts in {sw.ElapsedMilliseconds} ms");
            sw.Restart();
            var b = await fp32.EmbedAsync(texts, CancellationToken.None);
            output.WriteLine($"fp32: {texts.Count} texts in {sw.ElapsedMilliseconds} ms");

            var agreement = a.Zip(b, (x, y) => VectorMath.Dot(x, y)).ToList();
            output.WriteLine($"int8 vs fp32 cosine: mean {agreement.Average():0.0000}, min {agreement.Min():0.0000}");
            Assert.True(agreement.Min() > 0.95f, $"int8 drifted from fp32 (min cosine {agreement.Min():0.000})");

            foreach (var e in new[] { a, b })
            {
                for (int i = 0; i < Pairs.Length; i++)
                {
                    float para = VectorMath.Dot(e[i * 3], e[i * 3 + 1]);
                    float unrelated = VectorMath.Dot(e[i * 3], e[i * 3 + 2]);
                    output.WriteLine($"pair {i}: paraphrase {para:0.000} vs unrelated {unrelated:0.000}");
                    Assert.True(para > unrelated + 0.05f, $"pair {i}: paraphrase {para:0.000} should beat unrelated {unrelated:0.000}");
                }
            }
        }

        [ModelFact]
        public async Task RealEncoder_BatchingPaddingAndChunkingDoNotChangeResults()
        {
            using var onnx = new OnnxRuntimeHost(2, false, _ => { });
            var enc = await E5TextEncoder.CreateAsync(Hub(), onnx, EncoderCatalog.Int8, new EncoderOptions { MaxTokens = 64, MaxBatchItems = 3 });
            string longText = string.Join(" ", Enumerable.Repeat("The quarterly planning meeting moved to Thursday afternoon because the venue flooded.", 30));
            var texts = new[] { "short one", Pairs[0].A, longText, "", "🎉🎉🎉" };
            var together = await enc.EmbedAsync(texts, CancellationToken.None);
            for (int i = 0; i < texts.Length; i++)
            {
                // Dynamic int8 quantisation scales activations per batch tensor, so neighbours move a vector very slightly;
                // the same batch always gives the same numbers, and the drift is far below ranking noise.
                var alone = await enc.EmbedAsync(new[] { texts[i] }, CancellationToken.None);
                float same = VectorMath.Dot(alone[0], together[i]);
                output.WriteLine($"text {i}: alone vs batched cosine {same:0.00000}");
                Assert.True(same > 0.99f, $"text {i} changed with its batch neighbours ({same:0.0000})");
                Assert.InRange(VectorMath.Dot(together[i], together[i]), 0.999f, 1.001f);
            }
            var repeat = await enc.EmbedAsync(texts, CancellationToken.None);
            for (int i = 0; i < texts.Length; i++) Assert.Equal(together[i], repeat[i]); // reproducible for the same batch
            var stats = enc.Stats();
            Assert.True(stats.Windows > texts.Length, "the long text must be split into several windows");
            // The pooled long text still sits near its own sentence.
            var sentence = await enc.EmbedAsync(new[] { "The quarterly planning meeting moved to Thursday afternoon because the venue flooded." }, CancellationToken.None);
            Assert.True(VectorMath.Dot(sentence[0], together[2]) > 0.9f);
        }
    }
}
