using Microsoft.ML.Tokenizers;

namespace Omnipotent.Services.OmniscienceV3.Ml
{
    /// <summary>Model-ready token ids for one text (with attention mask).</summary>
    public sealed record EncodedText(long[] InputIds, long[] AttentionMask, bool Truncated);

    /// <summary>Tokenizer contract every ONNX model adapter uses.</summary>
    public interface ITextTokenizer
    {
        EncodedText Encode(string text, int maxTokens);
        long PadId { get; }
    }

    /// <summary>
    /// Head+tail truncation (plan A5: long messages keep their beginning and end with a marker position),
    /// replacing v2's head-only truncation that dropped [SEP] and the tail (F329).
    /// </summary>
    public static class Truncation
    {
        public static IReadOnlyList<int> HeadTail(IReadOnlyList<int> ids, int budget, double headShare = 0.6)
        {
            if (ids.Count <= budget) return ids;
            int head = Math.Max(1, (int)Math.Round(budget * headShare));
            int tail = Math.Max(0, budget - head);
            var result = new List<int>(budget);
            for (int i = 0; i < head; i++) result.Add(ids[i]);
            for (int i = ids.Count - tail; i < ids.Count; i++) result.Add(ids[i]);
            return result;
        }
    }

    /// <summary>WordPiece (BERT family) tokenizer: [CLS] text [SEP] with head+tail truncation.</summary>
    public sealed class WordPieceTextTokenizer : ITextTokenizer
    {
        private readonly BertTokenizer tokenizer;

        public WordPieceTextTokenizer(string vocabPath, bool lowerCase)
        {
            tokenizer = BertTokenizer.Create(vocabPath, new BertOptions { LowerCaseBeforeTokenization = lowerCase });
        }

        public long PadId => tokenizer.PaddingTokenId;

        public EncodedText Encode(string text, int maxTokens)
        {
            var raw = tokenizer.EncodeToIds(text ?? "", addSpecialTokens: false);
            int budget = Math.Max(1, maxTokens - 2);
            var body = Truncation.HeadTail(raw, budget);
            var ids = new long[body.Count + 2];
            ids[0] = tokenizer.ClassificationTokenId;
            for (int i = 0; i < body.Count; i++) ids[i + 1] = body[i];
            ids[^1] = tokenizer.SeparatorTokenId;
            return new EncodedText(ids, Enumerable.Repeat(1L, ids.Length).ToArray(), raw.Count > budget);
        }
    }

    /// <summary>
    /// SentencePiece (XLM-R / multilingual-e5 family) tokenizer reproducing Hugging Face's fairseq id layout,
    /// which the exported ONNX models expect: &lt;s&gt;=0, &lt;pad&gt;=1, &lt;/s&gt;=2, &lt;unk&gt;=3, and every
    /// other SentencePiece id shifted by +1. Getting this offset wrong silently corrupts every embedding.
    /// </summary>
    public sealed class XlmRobertaTextTokenizer : ITextTokenizer
    {
        public const long BosId = 0, PadIdConst = 1, EosId = 2, UnkId = 3;
        private readonly SentencePieceTokenizer tokenizer;
        private readonly string prefix;

        /// <param name="prefix">Model-specific instruction prefix, e.g. "query: " or "passage: " for e5.</param>
        public XlmRobertaTextTokenizer(Stream sentencePieceModel, string prefix = "")
        {
            // Microsoft.ML.Tokenizers >= 2.0 handles SentencePiece Unigram models (XLM-R/e5); 1.0.x was BPE-only.
            tokenizer = SentencePieceTokenizer.Create(sentencePieceModel, addBeginningOfSentence: false, addEndOfSentence: false);
            this.prefix = prefix;
        }

        public long PadId => PadIdConst;

        /// <summary>Maps a raw SentencePiece id to the Hugging Face/fairseq id the model was trained with.</summary>
        public static long ToFairseqId(int spmId) => spmId == 0 ? UnkId : spmId + 1;

        public EncodedText Encode(string text, int maxTokens)
        {
            var raw = tokenizer.EncodeToIds(prefix + (text ?? ""), addBeginningOfSentence: false, addEndOfSentence: false);
            int budget = Math.Max(1, maxTokens - 2);
            var body = Truncation.HeadTail(raw, budget);
            var ids = new long[body.Count + 2];
            ids[0] = BosId;
            for (int i = 0; i < body.Count; i++) ids[i + 1] = ToFairseqId(body[i]);
            ids[^1] = EosId;
            return new EncodedText(ids, Enumerable.Repeat(1L, ids.Length).ToArray(), raw.Count > budget);
        }
    }
}
