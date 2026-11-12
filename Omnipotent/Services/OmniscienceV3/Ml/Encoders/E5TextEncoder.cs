using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.Tokenizers;

namespace Omnipotent.Services.OmniscienceV3.Ml.Encoders
{
    /// <summary>Counters for the Modules page.</summary>
    public sealed record EncoderStats(string ModelVersion, string Provider, long Texts, long Windows, long Batches, long OomSplits, double MsPerText, long ThrottlePauses, long ThrottledMs);

    /// <summary>Something that can report how the shared encoder is doing.</summary>
    public interface IEncoderDiagnostics
    {
        EncoderStats Stats();
    }

    /// <summary>
    /// The shared text encoder of the local-first classification layer (plan R2): multilingual-e5-small on ONNX Runtime.
    /// One pass per message yields an L2-normalised 384-dimensional embedding that serves search, clustering and every
    /// light task head.
    /// <list type="bullet">
    /// <item><b>Pinned:</b> the graph and SentencePiece files come through <see cref="ModelHub"/> with SHA-256 and licence.</item>
    /// <item><b>One session:</b> through <see cref="OnnxRuntimeHost"/>, shared process-wide with capped intra-op threads (F330).</item>
    /// <item><b>Long texts:</b> split into windows of <see cref="EncoderOptions.MaxTokens"/> that are pooled by length, never
    /// silently cut (F329); past <see cref="EncoderOptions.MaxChunks"/> windows the head and tail are kept (plan A5).</item>
    /// <item><b>Batched with dynamic padding:</b> windows are sorted by length and packed under a padded-token budget; an
    /// allocation failure halves the batch and retries (plan F-ML "Batch OOM: halve the batch").</item>
    /// <item><b>CPU-aware:</b> every batch first passes the <see cref="CpuThrottle"/> (plan R1).</item>
    /// </list>
    /// Pooling is the model's own: attention-masked mean over the last hidden state (multilingual-e5's pooling config),
    /// or the graph's pooled <c>sentence_embedding</c> output when an export provides one.
    /// </summary>
    public sealed class E5TextEncoder : IEmbeddingEncoder, IEncoderDiagnostics
    {
        private readonly SharedOnnxSession onnx;
        private readonly E5Tokenizer tokenizer;
        private readonly EncoderOptions options;
        private readonly CpuThrottle throttle;
        private readonly string[] inputNames;
        private readonly bool[] inputIsInt32;
        private readonly string outputName;
        private readonly bool outputIsPooled;
        private long texts, windows, batches, oomSplits, encodeTicks;

        private E5TextEncoder(EncoderVariant variant, SharedOnnxSession onnx, E5Tokenizer tokenizer, EncoderOptions options, CpuThrottle throttle)
        {
            Variant = variant;
            this.onnx = onnx;
            this.tokenizer = tokenizer;
            this.options = options;
            this.throttle = throttle;
            var inputs = onnx.Session.InputMetadata;
            foreach (var required in new[] { "input_ids", "attention_mask" })
                if (!inputs.ContainsKey(required))
                    throw new InvalidOperationException($"Encoder graph {variant.Graph.FileName} has no '{required}' input (inputs: {string.Join(", ", inputs.Keys)}).");
            inputNames = inputs.Keys.ToArray();
            foreach (string name in inputNames)
                if (name is not ("input_ids" or "attention_mask" or "token_type_ids"))
                    throw new InvalidOperationException($"Encoder graph {variant.Graph.FileName} needs an unsupported input '{name}'.");
            inputIsInt32 = inputNames.Select(n => inputs[n].ElementType == typeof(int)).ToArray();
            var outputs = onnx.Session.OutputMetadata;
            if (outputs.ContainsKey("sentence_embedding")) { outputName = "sentence_embedding"; outputIsPooled = true; }
            else if (outputs.ContainsKey("last_hidden_state")) outputName = "last_hidden_state";
            else outputName = outputs.First(o => o.Value.Dimensions.Length == 3).Key;
            ModelVersion = variant.VersionFor(options);
        }

        public EncoderVariant Variant { get; }
        public string ModelId => Variant.ModelId;
        public string ModelVersion { get; }
        public int Dimensions => Variant.Dimensions;

        /// <summary>Fetches (or verifies) the pinned files and opens the shared session.</summary>
        public static async Task<E5TextEncoder> CreateAsync(ModelHub hub, OnnxRuntimeHost host, EncoderVariant variant, EncoderOptions? options = null,
            CpuThrottle? throttle = null, CancellationToken ct = default)
        {
            var o = options ?? new EncoderOptions();
            if (o.MaxTokens < 8 || o.MaxTokens > variant.MaxModelTokens)
                throw new ArgumentOutOfRangeException(nameof(options), $"MaxTokens must be between 8 and {variant.MaxModelTokens}.");
            string graph = await hub.EnsureAsync(variant.Graph, ct).ConfigureAwait(false);
            string spm = await hub.EnsureAsync(variant.SentencePiece, ct).ConfigureAwait(false);
            E5Tokenizer tok;
            await using (var stream = File.OpenRead(spm)) tok = new E5Tokenizer(stream, o.Prefix);
            var session = host.Get(graph);
            return new E5TextEncoder(variant, session, tok, o, throttle ?? CpuThrottle.None);
        }

        public EncoderStats Stats()
        {
            long t = Interlocked.Read(ref texts);
            double ms = t == 0 ? 0 : Interlocked.Read(ref encodeTicks) * 1000.0 / Stopwatch.Frequency / t;
            return new EncoderStats(ModelVersion, onnx.Provider.ToString(), t, Interlocked.Read(ref windows), Interlocked.Read(ref batches),
                Interlocked.Read(ref oomSplits), ms, throttle.Pauses, throttle.PausedMs);
        }

        public async Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(inputs);
            var result = new float[inputs.Count][];
            if (inputs.Count == 0) return result;
            var sw = Stopwatch.StartNew();

            // 1. Windows: every text becomes one or more token windows, each remembered with its owner and weight.
            var all = new List<Window>(inputs.Count);
            for (int i = 0; i < inputs.Count; i++)
                foreach (var (ids, weight) in tokenizer.Windows(inputs[i] ?? "", options.MaxTokens, options.MaxChunks))
                    all.Add(new Window(i, ids, weight));

            // 2. Dynamic padding: similar lengths share a batch (stable order for reproducibility).
            var order = Enumerable.Range(0, all.Count).OrderBy(k => all[k].Ids.Length).ThenBy(k => k).ToArray();
            var pooled = new float[all.Count][];
            int start = 0;
            while (start < order.Length)
            {
                int count = 0, longest = 0;
                while (start + count < order.Length && count < options.MaxBatchItems)
                {
                    int len = all[order[start + count]].Ids.Length;
                    int widest = Math.Max(longest, len);
                    if (count > 0 && (long)widest * (count + 1) > options.MaxBatchTokens) break;
                    longest = widest;
                    count++;
                }
                await RunWithSplitsAsync(all, order, start, count, pooled, ct).ConfigureAwait(false);
                start += count;
            }

            // 3. Pool each text's windows by their share of the text, then L2-normalise.
            var sums = new float[inputs.Count][];
            for (int k = 0; k < all.Count; k++)
            {
                var w = all[k];
                var acc = sums[w.Owner] ??= new float[Dimensions];
                var v = pooled[k];
                for (int d = 0; d < v.Length; d++) acc[d] += (float)w.Weight * v[d];
            }
            for (int i = 0; i < inputs.Count; i++) result[i] = VectorMath.Normalized(sums[i]);

            Interlocked.Add(ref texts, inputs.Count);
            Interlocked.Add(ref windows, all.Count);
            Interlocked.Add(ref encodeTicks, sw.ElapsedTicks);
            return result;
        }

        private readonly record struct Window(int Owner, long[] Ids, double Weight);

        /// <summary>Runs order[start..start+count) as one batch, halving it on allocation failure down to single rows.</summary>
        private async Task RunWithSplitsAsync(List<Window> all, int[] order, int start, int count, float[][] pooled, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            await throttle.WaitAsync(ct).ConfigureAwait(false);
            await onnx.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                RunBatch(all, order, start, count, pooled);
                Interlocked.Increment(ref batches);
                return;
            }
            catch (Exception ex) when (count > 1 && IsAllocationFailure(ex))
            {
                Interlocked.Increment(ref oomSplits);
            }
            finally
            {
                onnx.Gate.Release();
            }
            int half = count / 2;
            await RunWithSplitsAsync(all, order, start, half, pooled, ct).ConfigureAwait(false);
            await RunWithSplitsAsync(all, order, start + half, count - half, pooled, ct).ConfigureAwait(false);
        }

        internal static bool IsAllocationFailure(Exception ex) =>
            ex is OutOfMemoryException
            || (ex is OnnxRuntimeException ort && (ort.Message.Contains("alloc", StringComparison.OrdinalIgnoreCase)
                                                  || ort.Message.Contains("out of memory", StringComparison.OrdinalIgnoreCase)));

        private void RunBatch(List<Window> all, int[] order, int start, int count, float[][] pooled)
        {
            int seq = 0;
            for (int r = 0; r < count; r++) seq = Math.Max(seq, all[order[start + r]].Ids.Length);
            var ids = new long[count * seq];
            var mask = new long[count * seq];
            for (int r = 0; r < count; r++)
            {
                var row = all[order[start + r]].Ids;
                int off = r * seq;
                for (int t = 0; t < seq; t++)
                {
                    if (t < row.Length) { ids[off + t] = row[t]; mask[off + t] = 1; }
                    else ids[off + t] = E5Tokenizer.PadId;
                }
            }
            long[] shape = { count, seq };
            var values = new List<OrtValue>(inputNames.Length);
            try
            {
                for (int i = 0; i < inputNames.Length; i++)
                {
                    long[] data = inputNames[i] switch
                    {
                        "input_ids" => ids,
                        "attention_mask" => mask,
                        _ => new long[count * seq], // token_type_ids: XLM-R has a single segment type
                    };
                    values.Add(inputIsInt32[i]
                        ? OrtValue.CreateTensorValueFromMemory(data.Select(x => (int)x).ToArray(), shape)
                        : OrtValue.CreateTensorValueFromMemory(data, shape));
                }
                using var runOptions = new RunOptions();
                using var outputs = onnx.Session.Run(runOptions, inputNames, values, new[] { outputName });
                var output = outputs[0];
                var outShape = output.GetTensorTypeAndShape().Shape;
                var data2 = output.GetTensorDataAsSpan<float>();
                int hidden = (int)outShape[^1];
                if (hidden != Dimensions) throw new InvalidOperationException($"Encoder produced {hidden} dimensions, expected {Dimensions}.");
                for (int r = 0; r < count; r++)
                {
                    var vec = new float[hidden];
                    if (outputIsPooled)
                    {
                        data2.Slice(r * hidden, hidden).CopyTo(vec);
                    }
                    else
                    {
                        int tokens = 0;
                        for (int t = 0; t < seq; t++)
                        {
                            if (mask[r * seq + t] == 0) continue;
                            tokens++;
                            var h = data2.Slice((r * seq + t) * hidden, hidden);
                            for (int d = 0; d < hidden; d++) vec[d] += h[d];
                        }
                        if (tokens > 0) for (int d = 0; d < hidden; d++) vec[d] /= tokens;
                    }
                    pooled[order[start + r]] = VectorMath.Normalized(vec);
                }
            }
            finally
            {
                foreach (var v in values) v.Dispose();
            }
        }
    }

    /// <summary>
    /// SentencePiece tokenisation for multilingual-e5 (XLM-R vocabulary in the fairseq id layout the ONNX export expects,
    /// see <see cref="XlmRobertaTextTokenizer.ToFairseqId"/>). The instruction prefix is tokenised together with the text,
    /// exactly as the model was trained ("query: " + text in one string); long texts are split into windows that each
    /// carry the prefix.
    /// </summary>
    internal sealed class E5Tokenizer
    {
        public const long BosId = XlmRobertaTextTokenizer.BosId;
        public const long EosId = XlmRobertaTextTokenizer.EosId;
        public const long PadId = XlmRobertaTextTokenizer.PadIdConst;

        private readonly SentencePieceTokenizer spm;
        private readonly string prefix;
        private readonly long[] prefixIds;

        public E5Tokenizer(Stream model, string prefix)
        {
            spm = SentencePieceTokenizer.Create(model, addBeginningOfSentence: false, addEndOfSentence: false);
            this.prefix = prefix ?? "";
            prefixIds = this.prefix.Length == 0 ? Array.Empty<long>() : Fairseq(spm.EncodeToIds(this.prefix.TrimEnd(), addBeginningOfSentence: false, addEndOfSentence: false));
        }

        private static long[] Fairseq(IReadOnlyList<int> spmIds)
        {
            var ids = new long[spmIds.Count];
            for (int i = 0; i < ids.Length; i++) ids[i] = XlmRobertaTextTokenizer.ToFairseqId(spmIds[i]);
            return ids;
        }

        /// <summary>Model-ready windows (with &lt;s&gt; … &lt;/s&gt;) and each window's share of the text.</summary>
        public IEnumerable<(long[] Ids, double Weight)> Windows(string text, int maxTokens, int maxChunks)
        {
            string clean = text.Replace('\0', ' ');
            long[] full = Fairseq(spm.EncodeToIds(prefix + clean, addBeginningOfSentence: false, addEndOfSentence: false));
            // The prefix normally tokenises to the same leading ids alone and inside the full string. If it does not
            // (an unusual first character fused with it), treat the whole string as body: correct, just not windowed per prefix.
            bool prefixed = prefixIds.Length > 0 && full.Length >= prefixIds.Length && full.AsSpan(0, prefixIds.Length).SequenceEqual(prefixIds);
            long[] head = prefixed ? prefixIds : Array.Empty<long>();
            var body = prefixed ? full.AsSpan(prefixIds.Length).ToArray() : full;
            int room = Math.Max(1, maxTokens - 2 - head.Length);
            if (body.Length <= room)
            {
                yield return (Frame(head, body, 0, body.Length), 1.0);
                yield break;
            }
            int chunks = (body.Length + room - 1) / room;
            var starts = Enumerable.Range(0, chunks).Select(c => c * room).ToList();
            if (chunks > maxChunks)
            {
                // Head and tail windows (plan A5): the beginning and the end of a long text carry its gist.
                int headCount = (maxChunks + 1) / 2, tailCount = maxChunks - headCount;
                starts = starts.Take(headCount).Concat(starts.Skip(chunks - tailCount)).ToList();
            }
            int covered = starts.Sum(s => Math.Min(room, body.Length - s));
            foreach (int s in starts)
            {
                int len = Math.Min(room, body.Length - s);
                yield return (Frame(head, body, s, len), (double)len / covered);
            }
        }

        private static long[] Frame(long[] head, long[] body, int start, int length)
        {
            var ids = new long[length + head.Length + 2];
            ids[0] = BosId;
            head.CopyTo(ids, 1);
            Array.Copy(body, start, ids, 1 + head.Length, length);
            ids[^1] = EosId;
            return ids;
        }
    }
}
