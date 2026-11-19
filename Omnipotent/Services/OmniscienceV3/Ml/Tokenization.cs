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
    /// Text as a tokenizer can take it. Chat text is not always well-formed UTF-16: a message cut in the middle of an emoji,
    /// or stored broken by the app that sent it, carries half of a surrogate pair. The tokenizers throw on that
    /// (ArgumentException "Found a low surrogate char without a preceding high surrogate"), and one such message among
    /// 140,000 failed a whole replica build. A lone surrogate becomes U+FFFD, the replacement character; a NUL becomes a
    /// space. Well-formed text is returned as it is, without a copy.
    /// </summary>
    public static class UnicodeText
    {
        public static string WellFormed(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int bad = FirstBad(text, 0);
            if (bad < 0) return text;
            var sb = new System.Text.StringBuilder(text.Length);
            int from = 0;
            while (bad >= 0)
            {
                sb.Append(text, from, bad - from).Append(text[bad] == '\0' ? ' ' : '�');
                from = bad + 1;
                bad = FirstBad(text, from);
            }
            return sb.Append(text, from, text.Length - from).ToString();
        }

        /// <summary>
        /// The code point at <paramref name="index"/>, where <c>char.ConvertToUtf32</c> would throw: half of a surrogate
        /// pair reads as U+FFFD. Every place that looks at chat text by code point goes through this (the token estimate,
        /// the classifier's content gate, the replica's text features): the framework call throws an ArgumentException
        /// on such text, which the job queue takes for bad input and never retries.
        /// </summary>
        public static int CodePointAt(string text, int index)
        {
            char ch = text[index];
            if (!char.IsSurrogate(ch)) return ch;
            return char.IsHighSurrogate(ch) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])
                ? char.ConvertToUtf32(ch, text[index + 1])
                : 0xFFFD;
        }

        /// <summary>Index of the next NUL or unpaired surrogate at or after <paramref name="start"/>, or -1.</summary>
        private static int FirstBad(string text, int start)
        {
            for (int i = start; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch == '\0') return i;
                if (!char.IsSurrogate(ch)) continue;
                if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { i++; continue; }
                return i;
            }
            return -1;
        }
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
            var raw = tokenizer.EncodeToIds(UnicodeText.WellFormed(text), addSpecialTokens: false);
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
            var raw = tokenizer.EncodeToIds(prefix + UnicodeText.WellFormed(text), addBeginningOfSentence: false, addEndOfSentence: false);
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
