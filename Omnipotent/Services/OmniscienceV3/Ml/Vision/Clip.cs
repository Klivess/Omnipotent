using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Omnipotent.Services.OmniscienceV3.Ml.Encoders;
using Omnipotent.Services.OmniscienceV3.Ml.Vectors;
using SkiaSharp;

namespace Omnipotent.Services.OmniscienceV3.Ml.Vision
{
    /// <summary>
    /// The pinned image–text embedding model for visual search (plan N2: local image embeddings). OpenAI's CLIP ViT-B/32 (MIT),
    /// in the ONNX export of <c>Xenova/clip-vit-base-patch32</c> at one immutable commit, dynamically quantised: the image tower
    /// (89 MB) and the text tower (65 MB), with the tokenizer's vocabulary and merges. Every file carries the SHA-256 and size of
    /// that commit, so ModelHub refuses anything else. Klives agreed to the download on 2026-11-13.
    /// </summary>
    public static class ClipCatalog
    {
        public const string ModelId = "Xenova/clip-vit-base-patch32";
        public const string Revision = "6b7c523703ae26e39be4b03774966f8571c7f523";
        public const string Licence = "MIT (openai/clip-vit-base-patch32; ONNX export by Xenova)";
        public const int Dimensions = 512;
        public const int ImageSize = 224;
        public const int ContextLength = 77;
        /// <summary>Stored with every image vector: vectors of another build are never compared with these.</summary>
        public const string Version = "clip-b32.6b7c5237.q8";

        private static readonly string Pin = $"{ModelId}@{Revision[..12]}";

        private static Uri File(string path) => new($"https://huggingface.co/{ModelId}/resolve/{Revision}/{path}");

        public static readonly ModelArtifact Vision = new(Pin, "vision_model_quantized.onnx", File("onnx/vision_model_quantized.onnx"),
            "583FD1110A514667812FEE7D684952AAF82A99B959760C8D7DCA7E0AB9839299", Licence, 89_117_001);

        public static readonly ModelArtifact Text = new(Pin, "text_model_quantized.onnx", File("onnx/text_model_quantized.onnx"),
            "73BAAB855D406190DA9FAA498CFEDF65F15CF309F4CC7385B7B032E6D08E5C3A", Licence, 64_504_507);

        public static readonly ModelArtifact Vocab = new(Pin, "vocab.json", File("vocab.json"),
            "5047B556CE86CCAF6AA22B3FFCCFC52D391EA4ACCDAB9C2F2407DA5B742D4363", Licence, 862_328);

        public static readonly ModelArtifact Merges = new(Pin, "merges.txt", File("merges.txt"),
            "9FD691F7C8039210E0FCED15865466C65820D09B63988B0174BFE25DE299051A", Licence, 524_619);

        public static IReadOnlyList<ModelArtifact> All { get; } = new[] { Vision, Text, Vocab, Merges };

        /// <summary>CLIP's own normalisation (preprocessor_config.json of that commit).</summary>
        public static readonly float[] Mean = { 0.48145466f, 0.4578275f, 0.40821073f };
        public static readonly float[] Std = { 0.26862954f, 0.26130258f, 0.27577711f };
    }

    /// <summary>
    /// CLIP's byte-level BPE tokenizer (OpenAI's <c>simple_tokenizer</c>): text cleaned (HTML entities, whitespace) and lower-cased,
    /// split by CLIP's pattern, each piece's UTF-8 bytes mapped to printable characters, merged by rank with an end-of-word
    /// marker, then wrapped in start and end tokens and cut to 77.
    /// </summary>
    public sealed class ClipTokenizer
    {
        private static readonly Regex Pattern = new(@"<\|startoftext\|>|<\|endoftext\|>|'s|'t|'re|'ve|'m|'ll|'d|[\p{L}]+|[\p{N}]|[^\s\p{L}\p{N}]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
        private static readonly string[] ByteChars = BuildByteChars();

        private readonly Dictionary<string, int> vocab;
        private readonly Dictionary<(string, string), int> ranks;
        private readonly Dictionary<string, string[]> cache = new(StringComparer.Ordinal);
        private readonly object cacheGate = new();

        public int StartId { get; }
        public int EndId { get; }

        public ClipTokenizer(Dictionary<string, int> vocab, IEnumerable<string> mergeLines)
        {
            this.vocab = vocab;
            ranks = new Dictionary<(string, string), int>();
            int rank = 0;
            foreach (var line in mergeLines)
            {
                if (line.Length == 0 || line.StartsWith("#version", StringComparison.Ordinal)) continue;
                int space = line.IndexOf(' ');
                if (space <= 0) continue;
                ranks.TryAdd((line[..space], line[(space + 1)..]), rank++);
            }
            StartId = vocab["<|startoftext|>"];
            EndId = vocab["<|endoftext|>"];
        }

        public static ClipTokenizer Load(string vocabPath, string mergesPath) =>
            new(JsonSerializer.Deserialize<Dictionary<string, int>>(System.IO.File.ReadAllText(vocabPath, Encoding.UTF8))!,
                System.IO.File.ReadAllLines(mergesPath, Encoding.UTF8));

        /// <summary>The ids of one text: start, at most 75 pieces, end (77 at most).</summary>
        public int[] Encode(string text)
        {
            var ids = new List<int> { StartId };
            string clean = Spaces.Replace(WebUtility.HtmlDecode(WebUtility.HtmlDecode(text ?? "")), " ").Trim().ToLowerInvariant();
            foreach (Match m in Pattern.Matches(clean))
            {
                var sb = new StringBuilder();
                foreach (byte b in Encoding.UTF8.GetBytes(m.Value)) sb.Append(ByteChars[b]);
                foreach (var piece in Bpe(sb.ToString()))
                {
                    if (ids.Count >= ClipCatalog.ContextLength - 1) break;
                    if (vocab.TryGetValue(piece, out int id)) ids.Add(id);
                }
                if (ids.Count >= ClipCatalog.ContextLength - 1) break;
            }
            ids.Add(EndId);
            return ids.ToArray();
        }

        private string[] Bpe(string token)
        {
            lock (cacheGate)
                if (cache.TryGetValue(token, out var hit)) return hit;
            // Every byte of the piece is one character of the byte map, so the piece splits character by character.
            var word = new List<string>(token.Length);
            for (int i = 0; i < token.Length; i++) word.Add(i == token.Length - 1 ? token[i] + "</w>" : token[i].ToString());
            while (word.Count > 1)
            {
                (string, string)? best = null;
                int bestRank = int.MaxValue;
                for (int i = 0; i < word.Count - 1; i++)
                    if (ranks.TryGetValue((word[i], word[i + 1]), out int r) && r < bestRank) { bestRank = r; best = (word[i], word[i + 1]); }
                if (best is not { } pair) break;
                var merged = new List<string>(word.Count);
                for (int i = 0; i < word.Count; i++)
                {
                    if (i < word.Count - 1 && word[i] == pair.Item1 && word[i + 1] == pair.Item2) { merged.Add(pair.Item1 + pair.Item2); i++; }
                    else merged.Add(word[i]);
                }
                word = merged;
            }
            var result = word.ToArray();
            lock (cacheGate)
                if (cache.Count < 50_000) cache[token] = result;
            return result;
        }

        /// <summary>GPT-2/CLIP's reversible byte → printable character map.</summary>
        private static string[] BuildByteChars()
        {
            var bs = new List<int>();
            for (int b = '!'; b <= '~'; b++) bs.Add(b);
            for (int b = '¡'; b <= '¬'; b++) bs.Add(b);
            for (int b = '®'; b <= 'ÿ'; b++) bs.Add(b);
            var cs = new List<int>(bs);
            int n = 0;
            for (int b = 0; b < 256; b++)
            {
                if (bs.Contains(b)) continue;
                bs.Add(b);
                cs.Add(256 + n++);
            }
            var map = new string[256];
            for (int i = 0; i < bs.Count; i++) map[bs[i]] = ((char)cs[i]).ToString();
            return map;
        }
    }

    /// <summary>CLIP's image input: the upright picture on white, shortest edge to 224, centre-cropped to 224×224, normalised, CHW.</summary>
    public static class ClipPreprocess
    {
        public static float[] Pixels(SKBitmap upright)
        {
            int size = ClipCatalog.ImageSize;
            double scale = (double)size / Math.Min(upright.Width, upright.Height);
            int w = Math.Max(size, (int)Math.Round(upright.Width * scale)), h = Math.Max(size, (int)Math.Round(upright.Height * scale));
            using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul))
                ?? throw new InvalidOperationException("Could not create a drawing surface.");
            surface.Canvas.Clear(SKColors.White);
            using (var image = SKImage.FromBitmap(upright))
            {
                // Mipmapped sampling smooths large reductions the way the reference resize does; the crop is the centre.
                var dest = new SKRect((size - w) / 2f, (size - h) / 2f, (size - w) / 2f + w, (size - h) / 2f + h);
                surface.Canvas.DrawImage(image, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            }
            using var snapshot = surface.Snapshot();
            using var pixels = snapshot.PeekPixels() ?? throw new InvalidOperationException("Could not read the prepared image.");
            var rgba = pixels.GetPixelSpan();
            var data = new float[3 * size * size];
            int plane = size * size;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int p = (y * size + x) * 4, o = y * size + x;
                    for (int c = 0; c < 3; c++) data[c * plane + o] = (rgba[p + c] / 255f - ClipCatalog.Mean[c]) / ClipCatalog.Std[c];
                }
            return data;
        }
    }

    /// <summary>What turns images and texts into one embedding space (CLIP in production, a stand-in in tests).</summary>
    public interface IImageTextEmbedder
    {
        /// <summary>Stored with every vector; vectors of different versions are never compared.</summary>
        string ModelVersion { get; }
        Task<float[][]> EmbedImagesAsync(IReadOnlyList<SKBitmap> images, CancellationToken ct);
        Task<float[][]> EmbedTextsAsync(IReadOnlyList<string> texts, CancellationToken ct);
    }

    /// <summary>
    /// CLIP's two towers, loaded on first use from the pinned files. Image and text vectors share one 512-dimensional space
    /// and come out unit length, so a text query ranks images by cosine similarity. Runs on the CPU, throttled like the shared encoder.
    /// </summary>
    public sealed class ClipEncoder : IImageTextEmbedder, IDisposable
    {
        private sealed record Towers(SharedOnnxSession Vision, string VisionInput, string VisionOutput, SharedOnnxSession Text, string[] TextInputs, string TextOutput,
            bool TextInt32, ClipTokenizer Tokenizer);

        private readonly ModelHub hub;
        private readonly OnnxRuntimeHost onnx;
        private readonly CpuThrottle? throttle;
        private readonly bool ownsHost;
        private readonly SemaphoreSlim loadGate = new(1, 1);
        private volatile Towers? towers;

        public ClipEncoder(ModelHub hub, OnnxRuntimeHost onnx, CpuThrottle? throttle = null, bool ownsHost = false)
        {
            this.hub = hub;
            this.onnx = onnx;
            this.throttle = throttle;
            this.ownsHost = ownsHost;
        }

        public string ModelVersion => ClipCatalog.Version;
        public bool Loaded => towers != null;

        public async Task<float[][]> EmbedImagesAsync(IReadOnlyList<SKBitmap> images, CancellationToken ct)
        {
            if (images.Count == 0) return Array.Empty<float[]>();
            var t = towers ?? await LoadAsync(ct).ConfigureAwait(false);
            int size = ClipCatalog.ImageSize, n = images.Count;
            var batch = new float[n * 3 * size * size];
            for (int i = 0; i < n; i++) ClipPreprocess.Pixels(images[i]).CopyTo(batch, i * 3 * size * size);
            return await RunAsync(t.Vision, new[] { t.VisionInput }, new[] { OrtValue.CreateTensorValueFromMemory(batch, new long[] { n, 3, size, size }) }, t.VisionOutput, n, ct)
                .ConfigureAwait(false);
        }

        public async Task<float[][]> EmbedTextsAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            if (texts.Count == 0) return Array.Empty<float[]>();
            var t = towers ?? await LoadAsync(ct).ConfigureAwait(false);
            var rows = texts.Select(t.Tokenizer.Encode).ToList();
            int n = rows.Count, seq = rows.Max(r => r.Length);
            var ids = new long[n * seq];
            var mask = new long[n * seq];
            for (int r = 0; r < n; r++)
                for (int i = 0; i < seq; i++)
                {
                    // Padded with the end token, as the reference does: the pooled position is the first end token either way.
                    ids[r * seq + i] = i < rows[r].Length ? rows[r][i] : t.Tokenizer.EndId;
                    mask[r * seq + i] = i < rows[r].Length ? 1 : 0;
                }
            long[] shape = { n, seq };
            var values = t.TextInputs.Select(name =>
            {
                long[] data = name == "attention_mask" ? mask : ids;
                return t.TextInt32 ? OrtValue.CreateTensorValueFromMemory(data.Select(x => (int)x).ToArray(), shape) : OrtValue.CreateTensorValueFromMemory(data, shape);
            }).ToArray();
            return await RunAsync(t.Text, t.TextInputs, values, t.TextOutput, n, ct).ConfigureAwait(false);
        }

        private async Task<float[][]> RunAsync(SharedOnnxSession session, string[] inputNames, OrtValue[] values, string outputName, int rows, CancellationToken ct)
        {
            try
            {
                if (throttle != null) await throttle.WaitAsync(ct).ConfigureAwait(false);
                await session.Gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    using var runOptions = new RunOptions();
                    using var outputs = session.Session.Run(runOptions, inputNames, values, new[] { outputName });
                    var output = outputs[0];
                    var shape = output.GetTensorTypeAndShape().Shape;
                    int dims = (int)shape[^1];
                    if (dims != ClipCatalog.Dimensions) throw new InvalidOperationException($"CLIP produced {dims} dimensions, expected {ClipCatalog.Dimensions}.");
                    var data = output.GetTensorDataAsSpan<float>();
                    var result = new float[rows][];
                    for (int r = 0; r < rows; r++) result[r] = VectorMath.Normalized(data.Slice(r * dims, dims).ToArray());
                    return result;
                }
                finally
                {
                    session.Gate.Release();
                }
            }
            finally
            {
                foreach (var v in values) v.Dispose();
            }
        }

        private async Task<Towers> LoadAsync(CancellationToken ct)
        {
            await loadGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (towers != null) return towers;
                string vision = await hub.EnsureAsync(ClipCatalog.Vision, ct).ConfigureAwait(false);
                string text = await hub.EnsureAsync(ClipCatalog.Text, ct).ConfigureAwait(false);
                string vocab = await hub.EnsureAsync(ClipCatalog.Vocab, ct).ConfigureAwait(false);
                string merges = await hub.EnsureAsync(ClipCatalog.Merges, ct).ConfigureAwait(false);
                var v = onnx.Get(vision);
                var t = onnx.Get(text);
                string visionInput = v.Session.InputMetadata.Keys.First();
                string visionOutput = v.Session.OutputMetadata.ContainsKey("image_embeds") ? "image_embeds" : v.Session.OutputMetadata.Keys.First();
                var textInputs = t.Session.InputMetadata.Keys.Where(k => k is "input_ids" or "attention_mask").ToArray();
                if (!textInputs.Contains("input_ids")) throw new InvalidOperationException("The CLIP text model takes no input_ids.");
                bool int32 = t.Session.InputMetadata["input_ids"].ElementType == typeof(int);
                string textOutput = t.Session.OutputMetadata.ContainsKey("text_embeds") ? "text_embeds" : t.Session.OutputMetadata.Keys.First();
                return towers = new Towers(v, visionInput, visionOutput, t, textInputs, textOutput, int32, ClipTokenizer.Load(vocab, merges));
            }
            finally
            {
                loadGate.Release();
            }
        }

        public void Dispose()
        {
            if (ownsHost) onnx.Dispose();
            loadGate.Dispose();
        }
    }
}
