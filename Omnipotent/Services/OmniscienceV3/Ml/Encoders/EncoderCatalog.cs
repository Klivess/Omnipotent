namespace Omnipotent.Services.OmniscienceV3.Ml.Encoders
{
    /// <summary>One pinned build of the shared encoder: graph + tokenizer, both SHA-256 pinned with their licence.</summary>
    public sealed record EncoderVariant(
        string Key,
        string ModelId,
        string Revision,
        ModelArtifact Graph,
        ModelArtifact SentencePiece,
        int Dimensions,
        int MaxModelTokens,
        string Description)
    {
        /// <summary>
        /// The id stored with every vector and label computed from this variant (vectors.db <c>model</c>,
        /// labels' signal versions). It changes whenever anything that changes the numbers changes: the
        /// revision, the graph file, the instruction prefix or the token budget, so vectors from different
        /// builds are never compared or mixed.
        /// </summary>
        public string VersionFor(EncoderOptions options) =>
            $"e5s.{Revision[..8]}.{Key}.{(options.Prefix.Length == 0 ? "np" : options.Prefix.Trim().TrimEnd(':'))}.t{options.MaxTokens}";
    }

    /// <summary>How texts are cut and batched. Defaults suit chat messages (short, many).</summary>
    public sealed record EncoderOptions
    {
        /// <summary>
        /// The e5 instruction prefix. multilingual-e5 documents "query: " for embeddings used as features (linear-probe
        /// classification, clustering) and for symmetric similarity; the shared message embedding serves exactly those
        /// plus message-to-message search, so every message is encoded once with "query: " (plan R2: one pass per message).
        /// </summary>
        public string Prefix { get; init; } = "query: ";
        /// <summary>Tokens per encoder window including &lt;s&gt; and &lt;/s&gt;. Longer texts are split into windows that are pooled (F329).</summary>
        public int MaxTokens { get; init; } = 256;
        /// <summary>At most this many windows per text; beyond it the head and tail windows are kept (plan A5 head+tail).</summary>
        public int MaxChunks { get; init; } = 8;
        public int MaxBatchItems { get; init; } = 64;
        /// <summary>Padded tokens per ONNX run (batch rows × longest row): bounds peak memory.</summary>
        public int MaxBatchTokens { get; init; } = 8192;
    }

    /// <summary>
    /// The pinned encoder builds (plan R2: multilingual-e5-small, INT8-quantised ONNX). All files come from the official
    /// <c>intfloat/multilingual-e5-small</c> repository (MIT) at one immutable commit, with the SHA-256 and size Hugging Face
    /// reports for that commit, so ModelHub refuses anything else.
    /// <list type="bullet">
    /// <item><b>int8</b> (default): <c>onnx/model_qint8_avx512_vnni.onnx</c>, 118 MB, dynamically quantised. Its agreement with the
    /// fp32 graph is measured by the opt-in real-model test (ws-c.md records the numbers on the development CPU).</item>
    /// <item><b>fp32</b>: <c>onnx/model.onnx</c>, 470 MB: the reference the int8 build is measured against, and the fallback if a
    /// host's int8 kernels lose too much accuracy.</item>
    /// </list>
    /// </summary>
    public static class EncoderCatalog
    {
        public const string ModelId = "intfloat/multilingual-e5-small";
        public const string Revision = "614241f622f53c4eeff9890bdc4f31cfecc418b3";
        public const string Licence = "MIT (intfloat/multilingual-e5-small model card)";

        private static Uri File(string path) => new($"https://huggingface.co/{ModelId}/resolve/{Revision}/{path}");

        private static readonly ModelArtifact SentencePiece = new(
            $"{ModelId}@{Revision[..12]}", "sentencepiece.bpe.model", File("onnx/sentencepiece.bpe.model"),
            "CFC8146ABE2A0488E9E2A0C56DE7952F7C11AB059ECA145A0A727AFCE0DB2865", Licence, 5_069_051);

        public static readonly EncoderVariant Int8 = new(
            "int8", ModelId, Revision,
            new ModelArtifact($"{ModelId}@{Revision[..12]}", "model_qint8_avx512_vnni.onnx", File("onnx/model_qint8_avx512_vnni.onnx"),
                "DD476DD0C2514E9B9BE83AEB3853FAC0763E0BDF4A71645407587D77C48A2D88", Licence, 118_346_824),
            SentencePiece, 384, 512,
            "multilingual-e5-small, dynamically quantised to int8 (official export).");

        public static readonly EncoderVariant Fp32 = new(
            "fp32", ModelId, Revision,
            new ModelArtifact($"{ModelId}@{Revision[..12]}", "model.onnx", File("onnx/model.onnx"),
                "CA456C06B3A9505DDFD9131408916DD79290368331E7D76BB621F1CBA6BC8665", Licence, 470_268_510),
            SentencePiece, 384, 512,
            "multilingual-e5-small, fp32 (official export): the accuracy reference.");

        public static IReadOnlyList<EncoderVariant> All { get; } = new[] { Int8, Fp32 };

        public static EncoderVariant Get(string key) =>
            All.FirstOrDefault(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"No encoder variant '{key}' (known: {string.Join(", ", All.Select(v => v.Key))}).");
    }
}
