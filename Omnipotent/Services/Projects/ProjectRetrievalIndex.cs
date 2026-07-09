using System.Collections.Concurrent;

namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Minimal in-memory BM25 index over a project's event log, for the retrieval leg of
    /// rehydrate-on-wake (§7: "recent events + retrieval (BM25/semantic) into the full log").
    /// This is the per-project LEXICAL leg over the project's OWN log — deliberately cheap and
    /// rebuilt lazily from the JSONL. Cross-project + semantic recall (other projects, KliveAgent
    /// memory, Omniscience, docs, web) is provided separately by the KliveRAG service, injected into
    /// the wake seed as a RELEVANT KNOWLEDGE block and available as the search_knowledge tool.
    ///
    /// Freshness: the index tails the event log via <see cref="EnsureFresh"/> (pull), and the
    /// service also wires <see cref="ProjectEventLogStore.EventAppended"/> to <see cref="Ingest"/>
    /// (push) so searches between wakes stay current without a rescan.
    /// </summary>
    public class ProjectRetrievalIndex
    {
        private const double K1 = 1.2;
        private const double B = 0.75;
        /// <summary>Snippet stored per event for hydration into prompts; full text stays in the log.</summary>
        private const int SnippetChars = 400;

        private sealed class Doc
        {
            public long Sequence;
            public string EventID = "";
            public string Type = "";
            public DateTime Timestamp;
            public string Snippet = "";
            public Dictionary<string, int> TermFreqs = new(StringComparer.Ordinal);
            public int Length;
        }

        private sealed class ProjectIndex
        {
            public readonly object Gate = new();
            public long LastIngestedSequence;
            public List<Doc> Docs = new();
            public Dictionary<string, int> DocFreqs = new(StringComparer.Ordinal);
            public long TotalLength;
        }

        private readonly ProjectEventLogStore eventLog;
        private readonly ConcurrentDictionary<string, ProjectIndex> indexes = new(StringComparer.Ordinal);

        public ProjectRetrievalIndex(ProjectEventLogStore eventLog)
        {
            this.eventLog = eventLog;
        }

        /// <summary>A search hit: enough to render into a wake prompt without re-reading the log.</summary>
        public record RetrievalHit(long Sequence, string EventID, string Type, DateTime Timestamp, string Snippet, double Score);

        /// <summary>Pushes a single freshly-appended event into the index (wired to EventAppended).</summary>
        public void Ingest(ProjectEvent evt)
        {
            var idx = indexes.GetOrAdd(evt.ProjectID, _ => new ProjectIndex());
            lock (idx.Gate)
            {
                if (evt.Sequence <= idx.LastIngestedSequence) return;
                IngestLocked(idx, evt);
            }
        }

        /// <summary>Tails any events appended since the last ingest (used before each search).</summary>
        public void EnsureFresh(string projectID)
        {
            var idx = indexes.GetOrAdd(projectID, _ => new ProjectIndex());
            lock (idx.Gate)
            {
                // Page through the log so a very long backlog doesn't get silently capped.
                while (true)
                {
                    var newer = eventLog.ReadSince(projectID, idx.LastIngestedSequence, max: 2000);
                    if (newer.Count == 0) break;
                    foreach (var e in newer) IngestLocked(idx, e);
                }
            }
        }

        /// <summary>BM25 search over the project's ingested events, best-first.</summary>
        public List<RetrievalHit> Search(string projectID, string query, int topK = 12)
        {
            EnsureFresh(projectID);
            var idx = indexes.GetOrAdd(projectID, _ => new ProjectIndex());
            var queryTerms = Tokenize(query).Distinct().ToList();
            if (queryTerms.Count == 0) return new List<RetrievalHit>();

            lock (idx.Gate)
            {
                int n = idx.Docs.Count;
                if (n == 0) return new List<RetrievalHit>();
                double avgLen = (double)idx.TotalLength / n;

                var scored = new List<(Doc doc, double score)>();
                foreach (var doc in idx.Docs)
                {
                    double score = 0;
                    foreach (var term in queryTerms)
                    {
                        if (!doc.TermFreqs.TryGetValue(term, out int tf)) continue;
                        idx.DocFreqs.TryGetValue(term, out int df);
                        double idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
                        double denom = tf + K1 * (1 - B + B * doc.Length / Math.Max(1.0, avgLen));
                        score += idf * (tf * (K1 + 1)) / denom;
                    }
                    if (score > 0) scored.Add((doc, score));
                }

                return scored
                    .OrderByDescending(s => s.score)
                    .Take(topK)
                    .Select(s => new RetrievalHit(s.doc.Sequence, s.doc.EventID, s.doc.Type, s.doc.Timestamp, s.doc.Snippet, s.score))
                    .ToList();
            }
        }

        private static void IngestLocked(ProjectIndex idx, ProjectEvent evt)
        {
            string text = ComposeText(evt);
            var tokens = Tokenize(text);
            var doc = new Doc
            {
                Sequence = evt.Sequence,
                EventID = evt.EventID,
                Type = evt.Type,
                Timestamp = evt.Timestamp,
                Snippet = text.Length <= SnippetChars ? text : text[..SnippetChars] + "…",
                Length = tokens.Count,
            };
            foreach (var t in tokens)
                doc.TermFreqs[t] = doc.TermFreqs.TryGetValue(t, out int c) ? c + 1 : 1;
            foreach (var term in doc.TermFreqs.Keys)
                idx.DocFreqs[term] = idx.DocFreqs.TryGetValue(term, out int df) ? df + 1 : 1;
            idx.Docs.Add(doc);
            idx.TotalLength += doc.Length;
            idx.LastIngestedSequence = evt.Sequence;
        }

        private static string ComposeText(ProjectEvent evt)
        {
            // Index what a human would search for: type, author, prose, tool name and a slice of payload.
            var parts = new List<string> { evt.Type, evt.Author, evt.Text };
            if (!string.IsNullOrWhiteSpace(evt.ToolName)) parts.Add(evt.ToolName);
            if (!string.IsNullOrWhiteSpace(evt.PayloadJson))
                parts.Add(evt.PayloadJson.Length > 600 ? evt.PayloadJson[..600] : evt.PayloadJson);
            return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        private static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return tokens;
            var current = new System.Text.StringBuilder();
            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c)) current.Append(char.ToLowerInvariant(c));
                else if (current.Length > 0) { Flush(); }
            }
            if (current.Length > 0) Flush();
            return tokens;

            void Flush()
            {
                if (current.Length > 2) tokens.Add(current.ToString());
                current.Clear();
            }
        }
    }
}
