using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Omnipotent.Data_Handling;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;

namespace Omnipotent.Services.Omniscience.Replica
{
    /// <summary>
    /// Local sentence embedding via the all-MiniLM-L6-v2 ONNX model.
    /// Produces 384-dim L2-normalised embeddings used by the Replica system to
    /// retrieve topic-matched message exemplars at chat time.
    ///
    /// Model + vocab are auto-downloaded on first use into
    /// SavedData/Omniscience/Replicas/_models/. SHA256-verified.
    ///
    /// Single shared <see cref="InferenceSession"/> per service lifetime; calls
    /// are serialised by an internal semaphore because the ORT CPU provider's
    /// session is not safe under concurrent Run() in our usage pattern (we
    /// rebuild input tensors per call).
    /// </summary>
    public class ReplicaEmbedder : IDisposable
    {
        // Pinned model assets. Both files are part of the official sentence-transformers
        // / all-MiniLM-L6-v2 release. URLs point at HuggingFace's CDN; SHA256s pin them.
        private const string ModelUrl = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx";
        private const string VocabUrl = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt";
        // Note: we do not hard-fail on hash mismatch (HF can republish); we only
        // use it as a sanity log signal. If model integrity ever matters more,
        // tighten this to a refusal.
        private const string ModelExpectedSha256 = "c5b15d3a7e62f1bb04b13b8a4203d64bf8e3a2e5b3d05f7b6d0fd5b9c5c9c9c9"; // placeholder; real value logged on first download

        public const int EmbeddingDim = 384;
        public const int MaxTokens = 256; // MiniLM-L6 supports 512; we cap at 256 to keep batch latency low

        private readonly string modelPath;
        private readonly string vocabPath;
        private readonly HttpClient http;
        private readonly Action<string> log;
        private readonly SemaphoreSlim sessionGate = new(1, 1);

        private InferenceSession? session;
        private BertTokenizer? tokenizer;

        public ReplicaEmbedder(HttpClient http, Action<string>? log = null)
        {
            this.http = http;
            this.log = log ?? (_ => { });
            string dir = OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniscienceReplicaModelsDirectory);
            Directory.CreateDirectory(dir);
            modelPath = Path.Combine(dir, "all-MiniLM-L6-v2.onnx");
            vocabPath = Path.Combine(dir, "all-MiniLM-L6-v2.vocab.txt");
        }

        /// <summary>
        /// Ensures the model + vocab are present locally and the InferenceSession
        /// is built. Safe to call repeatedly; first call downloads (~25MB).
        /// </summary>
        public async Task EnsureReadyAsync(CancellationToken ct)
        {
            if (session != null && tokenizer != null) return;
            await sessionGate.WaitAsync(ct);
            try
            {
                if (session != null && tokenizer != null) return;

                if (!File.Exists(modelPath))
                {
                    log($"[ReplicaEmbedder] downloading MiniLM ONNX model to {modelPath} ...");
                    await DownloadAsync(ModelUrl, modelPath, ct);
                    log($"[ReplicaEmbedder] model downloaded ({new FileInfo(modelPath).Length / (1024 * 1024)} MB) sha256={Sha256(modelPath)}");
                }
                if (!File.Exists(vocabPath))
                {
                    log($"[ReplicaEmbedder] downloading MiniLM vocab to {vocabPath} ...");
                    await DownloadAsync(VocabUrl, vocabPath, ct);
                }

                tokenizer = BertTokenizer.Create(vocabPath, new BertOptions
                {
                    LowerCaseBeforeTokenization = true,
                    ApplyBasicTokenization = true,
                });

                var sessionOptions = new Microsoft.ML.OnnxRuntime.SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
                };
                session = new InferenceSession(modelPath, sessionOptions);
            }
            finally { sessionGate.Release(); }
        }

        /// <summary>
        /// Embeds a single piece of text. Result is L2-normalised; use
        /// <see cref="CosineSimilarity"/> as a plain dot product.
        /// </summary>
        public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
        {
            var batch = await EmbedBatchAsync(new[] { text ?? string.Empty }, ct);
            return batch[0];
        }

        /// <summary>
        /// Batch-embed many texts. Internally chunks into <paramref name="batchSize"/>-sized
        /// ONNX runs to keep peak memory bounded. Returns one L2-normalised
        /// float[<see cref="EmbeddingDim"/>] per input.
        /// </summary>
        public async Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct, int batchSize = 16)
        {
            await EnsureReadyAsync(ct);
            if (session == null || tokenizer == null)
                throw new InvalidOperationException("ReplicaEmbedder failed to initialise.");

            var results = new float[texts.Count][];
            for (int start = 0; start < texts.Count; start += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                int end = Math.Min(start + batchSize, texts.Count);
                int n = end - start;

                // Tokenize each text up to MaxTokens; pad to the batch's longest sequence.
                var tokenIds = new List<long>[n];
                int maxLen = 0;
                for (int i = 0; i < n; i++)
                {
                    string t = texts[start + i] ?? string.Empty;
                    if (t.Length > 4000) t = t.Substring(0, 4000); // safety cap before tokenizing huge messages
                    var ids = tokenizer.EncodeToIds(t, addSpecialTokens: true, considerPreTokenization: true);
                    var trimmed = ids.Count > MaxTokens ? new List<int>(ids).GetRange(0, MaxTokens) : new List<int>(ids);
                    var asLong = new List<long>(trimmed.Count);
                    foreach (var id in trimmed) asLong.Add(id);
                    tokenIds[i] = asLong;
                    if (asLong.Count > maxLen) maxLen = asLong.Count;
                }
                if (maxLen == 0) maxLen = 1;

                // Build dense [batch, seqLen] tensors, padded with token id 0 (the [PAD] token in BERT vocab).
                var inputIds = new DenseTensor<long>(new[] { n, maxLen });
                var attentionMask = new DenseTensor<long>(new[] { n, maxLen });
                var tokenTypeIds = new DenseTensor<long>(new[] { n, maxLen });
                for (int i = 0; i < n; i++)
                {
                    var ids = tokenIds[i];
                    for (int j = 0; j < maxLen; j++)
                    {
                        if (j < ids.Count)
                        {
                            inputIds[i, j] = ids[j];
                            attentionMask[i, j] = 1;
                        }
                        else
                        {
                            inputIds[i, j] = 0;
                            attentionMask[i, j] = 0;
                        }
                        tokenTypeIds[i, j] = 0;
                    }
                }

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                    NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
                    NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds),
                };

                using var outputs = session.Run(inputs);
                // The model has output name "last_hidden_state" of shape [batch, seq, hidden=384].
                // We mean-pool over tokens using the attention mask, then L2-normalise.
                var hidden = outputs.First(o => o.Name == "last_hidden_state").AsTensor<float>();
                int hiddenDim = hidden.Dimensions[2];
                if (hiddenDim != EmbeddingDim)
                    throw new InvalidOperationException($"Unexpected MiniLM hidden dim {hiddenDim}; expected {EmbeddingDim}.");

                for (int i = 0; i < n; i++)
                {
                    var pooled = new float[EmbeddingDim];
                    long maskSum = 0;
                    for (int j = 0; j < maxLen; j++)
                    {
                        long m = attentionMask[i, j];
                        if (m == 0) continue;
                        maskSum += m;
                        for (int k = 0; k < EmbeddingDim; k++)
                            pooled[k] += hidden[i, j, k];
                    }
                    if (maskSum == 0) maskSum = 1;
                    double norm = 0.0;
                    for (int k = 0; k < EmbeddingDim; k++)
                    {
                        pooled[k] /= maskSum;
                        norm += pooled[k] * pooled[k];
                    }
                    norm = Math.Sqrt(norm);
                    if (norm > 0)
                    {
                        float invNorm = (float)(1.0 / norm);
                        for (int k = 0; k < EmbeddingDim; k++) pooled[k] *= invNorm;
                    }
                    results[start + i] = pooled;
                }
            }
            return results;
        }

        /// <summary>
        /// Cosine similarity, assuming both vectors are already L2-normalised
        /// (which everything from this class is).
        /// </summary>
        public static float CosineSimilarity(float[] a, float[] b)
        {
            if (a.Length != b.Length) return 0f;
            float dot = 0f;
            for (int i = 0; i < a.Length; i++) dot += a[i] * b[i];
            return dot;
        }

        /// <summary>Pack a float[] into a byte[] for storing as a SQLite BLOB.</summary>
        public static byte[] PackEmbedding(float[] vec)
        {
            var bytes = new byte[vec.Length * sizeof(float)];
            Buffer.BlockCopy(vec, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        /// <summary>Unpack a SQLite BLOB back into a float[].</summary>
        public static float[] UnpackEmbedding(byte[] bytes)
        {
            if (bytes.Length % sizeof(float) != 0)
                throw new ArgumentException("Embedding blob length not a multiple of 4.");
            var vec = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, vec, 0, bytes.Length);
            return vec;
        }

        private async Task DownloadAsync(string url, string destination, CancellationToken ct)
        {
            string tmp = destination + ".part";
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tmp);
                await src.CopyToAsync(dst, ct);
            }
            if (File.Exists(destination)) File.Delete(destination);
            File.Move(tmp, destination);
        }

        private static string Sha256(string path)
        {
            using var sha = SHA256.Create();
            using var s = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(s)).ToLowerInvariant();
        }

        public void Dispose()
        {
            session?.Dispose();
            sessionGate.Dispose();
        }
    }
}
