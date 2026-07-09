using Omnipotent.Data_Handling;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.KliveRAG.Connectors
{
    /// <summary>
    /// Indexes the repo's markdown docs (READMEs, architecture notes, service guides) so agents can
    /// answer questions about how Omnipotent itself works. Incremental by file mtime+hash; removed
    /// files are tombstoned. Source-code indexing is intentionally out of scope — KliveAgent's own
    /// codebase index owns that.
    /// </summary>
    public sealed class RepoDocsConnector : RagConnector
    {
        public override string Name => RagSource.RepoDocs;

        private static readonly string[] SkipDirs = { "bin", "obj", ".git", "node_modules", ".vs", "TempDownloads", "SavedData" };

        public RepoDocsConnector(RagIndexWriter writer, Action<string> log) : base(writer, log) { }

        public override async Task RunIncrementalAsync(CancellationToken ct)
        {
            string root = Path.GetFullPath(OmniPaths.CodebaseDirectory);
            if (!Directory.Exists(root)) { Log("[KliveRAG] repo-docs: codebase root not found, skipping."); return; }

            var seenDocIds = new HashSet<string>(StringComparer.Ordinal);
            int changed = 0;

            foreach (var file in EnumerateMarkdown(root))
            {
                ct.ThrowIfCancellationRequested();
                string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                string docId = $"repodoc:{rel}";
                seenDocIds.Add(docId);

                string text;
                try { text = await File.ReadAllTextAsync(file, ct); }
                catch { continue; }
                if (string.IsNullOrWhiteSpace(text)) continue;

                long createdMs;
                try { createdMs = new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeMilliseconds(); }
                catch { createdMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }

                var doc = new RagDocument
                {
                    DocId = docId,
                    Source = RagSource.RepoDocs,
                    Title = rel,
                    Uri = file,
                    Content = text,
                    ContentHash = RagChunker.Hash(text),
                    CreatedAtUnixMs = createdMs,
                    IsMarkdown = true,
                };
                if (await Writer.UpsertAsync(doc, ct)) changed++;
            }

            // Tombstone docs whose file no longer exists.
            foreach (var docId in Writer.GetDocIdsForSource(RagSource.RepoDocs))
                if (!seenDocIds.Contains(docId))
                    await Writer.DeleteAsync(docId, ct);

            await SetCursorAsync(Name, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(), ct);
            if (changed > 0) Log($"[KliveRAG] repo-docs: indexed {changed} changed doc(s).");
        }

        private IEnumerable<string> EnumerateMarkdown(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                string[] subdirs;
                try { subdirs = Directory.GetDirectories(dir); }
                catch { continue; }
                foreach (var sub in subdirs)
                {
                    string name = Path.GetFileName(sub);
                    if (SkipDirs.Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase))) continue;
                    stack.Push(sub);
                }
                string[] files;
                try { files = Directory.GetFiles(dir, "*.md"); }
                catch { continue; }
                foreach (var f in files) yield return f;
            }
        }
    }
}
