using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Ml;
using Omnipotent.Services.OmniscienceV3.Ml.Encoders;
using Omnipotent.Services.OmniscienceV3.Ml.Vectors;
using Omnipotent.Services.OmniscienceV3.Ml.Vision;
using Omnipotent.Services.OmniscienceV3.Temporal;
using SkiaSharp;
using Xunit.Abstractions;

namespace Omnipotent.Tests.OmniscienceV3.Ml
{
    /// <summary>CLIP for visual search: the tokenizer and the image input on their own, and (opt-in) the pinned model itself.</summary>
    public class ClipTests
    {
        private readonly ITestOutputHelper output;

        public ClipTests(ITestOutputHelper output) => this.output = output;

        /// <summary>A tiny vocabulary in CLIP's format: byte characters, end-of-word forms, a few merges and the two specials.</summary>
        private static ClipTokenizer TinyTokenizer()
        {
            var vocab = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in new[] { "c", "a", "t", "a</w>", "t</w>", "ca", "cat</w>", "s</w>", "!</w>", "Ã", "©</w>", "<|startoftext|>", "<|endoftext|>" }) vocab[t] = vocab.Count;
            return new ClipTokenizer(vocab, new[] { "#version: 0.2", "c a", "ca t</w>" });
        }

        [Fact]
        public void Tokenizer_MergesByRank_MarksWordEnds_CleansTheText_AndCutsAt77()
        {
            var tok = TinyTokenizer();
            int cat = 6, a = 3, bang = 8;
            Assert.Equal(new[] { tok.StartId, cat, tok.EndId }, tok.Encode("cat"));
            // Lower-cased, entities decoded, whitespace collapsed, punctuation its own piece.
            Assert.Equal(new[] { tok.StartId, cat, a, bang, tok.EndId }, tok.Encode("  CAT \n  a !"));
            Assert.Equal(new[] { tok.StartId, cat, bang, tok.EndId }, tok.Encode("cat&#33;")); // an HTML entity is decoded first
            // Non-ASCII goes through the byte map: "é" is bytes C3 A9, shown as "Ã" and "©".
            Assert.Equal(new[] { tok.StartId, 9, 10, tok.EndId }, tok.Encode("é"));
            // Never more than 77 ids, start and end included.
            var many = tok.Encode(string.Join(" ", Enumerable.Repeat("cat", 200)));
            Assert.Equal(77, many.Length);
            Assert.Equal((tok.StartId, tok.EndId), (many[0], many[^1]));
        }

        [Fact]
        public void ImageInput_IsTheCentreCrop_At224_NormalisedPerChannel()
        {
            // A wide picture: red on the left third, blue in the middle, red on the right. The centre crop is blue.
            using var bitmap = new SKBitmap(672, 224, SKColorType.Rgba8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Red);
                using var paint = new SKPaint { Color = SKColors.Blue };
                canvas.DrawRect(new SKRect(224, 0, 448, 224), paint);
            }
            var pixels = ClipPreprocess.Pixels(bitmap);
            Assert.Equal(3 * 224 * 224, pixels.Length);
            int plane = 224 * 224, centre = 112 * 224 + 112;
            float Expected(int channel, float value) => (value - ClipCatalog.Mean[channel]) / ClipCatalog.Std[channel];
            Assert.Equal(Expected(0, 0), pixels[centre], 2);              // red channel
            Assert.Equal(Expected(2, 1), pixels[2 * plane + centre], 2);  // blue channel
        }

        [ModelFact]
        public async Task RealModel_TokenisesLikeTheReference_AndMatchesColoursToTheirWords()
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            var hub = new ModelHub(ModelFactAttribute.CacheDir, http, SystemClock.Instance, s => output.WriteLine(s));
            using var clip = new ClipEncoder(hub, new OnnxRuntimeHost(2, preferDirectML: false, s => output.WriteLine(s)), ownsHost: true);

            // The reference ids for this sentence (OpenAI's tokenizer, as Hugging Face's CLIPTokenizer gives them).
            var tok = ClipTokenizer.Load(await hub.EnsureAsync(ClipCatalog.Vocab), await hub.EnsureAsync(ClipCatalog.Merges));
            Assert.Equal(new[] { 49406, 320, 1125, 539, 320, 2368, 49407 }, tok.Encode("a photo of a cat"));

            SKBitmap Square(SKColor colour)
            {
                var b = new SKBitmap(320, 240, SKColorType.Rgba8888, SKAlphaType.Premul);
                using var canvas = new SKCanvas(b);
                canvas.Clear(colour);
                return b;
            }
            using var red = Square(SKColors.Red);
            using var blue = Square(SKColors.Blue);
            var images = await clip.EmbedImagesAsync(new[] { red, blue }, CancellationToken.None);
            var texts = await clip.EmbedTextsAsync(new[] { "a red square", "a blue square" }, CancellationToken.None);
            Assert.All(images.Concat(texts), v => Assert.Equal(ClipCatalog.Dimensions, v.Length));
            Assert.All(images.Concat(texts), v => Assert.InRange(VectorMath.Dot(v, v), 0.99f, 1.01f));
            float rr = VectorMath.Dot(images[0], texts[0]), rb = VectorMath.Dot(images[0], texts[1]);
            float br = VectorMath.Dot(images[1], texts[0]), bb = VectorMath.Dot(images[1], texts[1]);
            output.WriteLine($"red image: red {rr:0.000} blue {rb:0.000}; blue image: red {br:0.000} blue {bb:0.000}");
            Assert.True(rr > rb, "the red picture is closer to 'a red square'");
            Assert.True(bb > br, "the blue picture is closer to 'a blue square'");
        }
    }
}
