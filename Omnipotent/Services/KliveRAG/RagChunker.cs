using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Omnipotent.Services.KliveRAG
{
    /// <summary>
    /// Pure, deterministic chunking. A document becomes an ordered list of chunks; the same
    /// document text always yields the same chunk ids and per-chunk hashes, so re-ingesting an
    /// unchanged (or append-only-grown) document only re-embeds the chunks that actually changed.
    ///
    /// Target ~<see cref="TargetTokens"/> tokens with ~<see cref="OverlapTokens"/> overlap. The
    /// target stays under the embedder's 256-token window so the whole chunk is embedded, not
    /// silently truncated. Token counts use the ~4-chars/token heuristic used elsewhere in the repo.
    /// </summary>
    public static class RagChunker
    {
        public const int TargetTokens = 250;
        public const int OverlapTokens = 40;
        private const double CharsPerToken = 4.0;

        private static int TargetChars => (int)(TargetTokens * CharsPerToken);   // ~1000
        private static int OverlapChars => (int)(OverlapTokens * CharsPerToken); // ~160

        public static int EstimateTokens(string text) =>
            string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / CharsPerToken);

        /// <summary>SHA-256 (hex) of a string — used for both document and chunk change detection.</summary>
        public static string Hash(string text)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""))).ToLowerInvariant();
        }

        /// <summary>Short (16-hex) hash, for compact natural ids like web:&lt;sha16(url)&gt;.</summary>
        public static string ShortHash(string text) => Hash(text).Substring(0, 16);

        /// <summary>
        /// Splits a document into chunks. Single-chunk docs (events, memories, facts) return one
        /// chunk. Markdown docs split on headings first, then size-bound. Everything else splits on
        /// paragraph/sentence boundaries into overlapping windows.
        /// </summary>
        public static List<RagChunk> Chunk(RagDocument doc)
        {
            var chunks = new List<RagChunk>();
            string text = (doc.Content ?? "").Trim();
            if (text.Length == 0) return chunks;

            List<string> pieces;
            if (doc.PreChunks != null)
                pieces = doc.PreChunks;
            else if (doc.SingleChunk || text.Length <= TargetChars)
                pieces = new List<string> { text };
            else if (doc.IsMarkdown)
                pieces = ChunkMarkdown(text);
            else
                pieces = ChunkProse(text);

            int seq = 0;
            foreach (var piece in pieces)
            {
                string t = piece.Trim();
                if (t.Length == 0) continue;
                chunks.Add(new RagChunk
                {
                    ChunkId = $"{doc.DocId}#{seq}",
                    DocId = doc.DocId,
                    Seq = seq,
                    Source = doc.Source,
                    CreatedAtUnixMs = doc.CreatedAtUnixMs,
                    Text = t,
                    ContentHash = Hash(t),
                    TokenEstimate = EstimateTokens(t),
                });
                seq++;
            }
            return chunks;
        }

        // Split on markdown headings, then size-bound each section via the prose splitter.
        private static List<string> ChunkMarkdown(string text)
        {
            var sections = new List<string>();
            var current = new StringBuilder();
            foreach (var line in text.Split('\n'))
            {
                bool isHeading = line.StartsWith("#") && line.TrimStart('#').StartsWith(" ");
                if (isHeading && current.Length > 0)
                {
                    sections.Add(current.ToString());
                    current.Clear();
                }
                current.AppendLine(line);
            }
            if (current.Length > 0) sections.Add(current.ToString());

            var pieces = new List<string>();
            foreach (var section in sections)
            {
                if (section.Length <= TargetChars) pieces.Add(section);
                else pieces.AddRange(ChunkProse(section));
            }
            return pieces;
        }

        // Overlapping windows over paragraph/sentence boundaries. Greedy: accumulate boundary
        // units until adding the next would exceed TargetChars, emit, then re-seed with an
        // OverlapChars tail so context isn't severed mid-thought.
        private static List<string> ChunkProse(string text)
        {
            var units = SplitBoundaries(text);
            var pieces = new List<string>();
            var buf = new StringBuilder();

            foreach (var unit in units)
            {
                if (buf.Length > 0 && buf.Length + unit.Length > TargetChars)
                {
                    pieces.Add(buf.ToString().Trim());
                    string prev = buf.ToString();
                    buf.Clear();
                    if (OverlapChars > 0 && prev.Length > OverlapChars)
                        buf.Append(prev.Substring(prev.Length - OverlapChars));
                }

                // A single unit larger than the target is hard-split so no chunk blows the window.
                if (unit.Length > TargetChars)
                {
                    if (buf.Length > 0) { pieces.Add(buf.ToString().Trim()); buf.Clear(); }
                    for (int i = 0; i < unit.Length; i += TargetChars)
                        pieces.Add(unit.Substring(i, Math.Min(TargetChars, unit.Length - i)).Trim());
                    continue;
                }
                buf.Append(unit);
            }
            if (buf.Length > 0) pieces.Add(buf.ToString().Trim());
            return pieces.Where(p => p.Length > 0).ToList();
        }

        // Break into paragraph then sentence units, preserving trailing whitespace so re-joins read naturally.
        private static List<string> SplitBoundaries(string text)
        {
            var units = new List<string>();
            foreach (var para in text.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.None))
            {
                string p = para;
                if (p.Length == 0) continue;
                if (p.Length <= TargetChars) { units.Add(p + "\n\n"); continue; }

                var sb = new StringBuilder();
                foreach (char c in p)
                {
                    sb.Append(c);
                    if ((c == '.' || c == '!' || c == '?' || c == '\n') && sb.Length >= 40)
                    {
                        units.Add(sb.ToString());
                        sb.Clear();
                    }
                }
                if (sb.Length > 0) units.Add(sb.ToString());
            }
            return units;
        }
    }
}
