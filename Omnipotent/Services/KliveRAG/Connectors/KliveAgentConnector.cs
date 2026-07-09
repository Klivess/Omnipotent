using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveAgent.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.KliveRAG.Connectors
{
    /// <summary>
    /// Indexes KliveAgent's conversation archives and saved memories so both become semantically
    /// searchable across sessions (today memories are BM25-only and old conversations vanish once
    /// summarised out of the context window). Conversations chunk per user+assistant turn-pair with
    /// stable ids, so a growing conversation only re-embeds its new turns. Files are mtime-watermarked;
    /// deleted memories are tombstoned.
    /// </summary>
    public sealed class KliveAgentConnector : RagConnector
    {
        public override string Name => RagSource.AgentConversations;

        private const int MinChars = 16;
        private const string ConvCursor = "agent-conversations";
        private const string MemCursor = "agent-memories";

        public KliveAgentConnector(RagIndexWriter writer, Action<string> log) : base(writer, log) { }

        public override async Task RunIncrementalAsync(CancellationToken ct)
        {
            await ScanConversationsAsync(ct);
            await ScanMemoriesAsync(ct);
        }

        private async Task ScanConversationsAsync(CancellationToken ct)
        {
            string dir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentConversationsDirectory);
            if (!Directory.Exists(dir)) return;
            long watermark = long.TryParse(GetCursor(ConvCursor), out var w) ? w : 0;
            long newWatermark = watermark;

            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                ct.ThrowIfCancellationRequested();
                long mtime = FileMtimeMs(file);
                if (mtime <= watermark) continue;
                newWatermark = Math.Max(newWatermark, mtime);

                var conv = TryRead<AgentConversation>(file);
                if (conv?.Messages == null || conv.Messages.Count == 0) continue;

                var pairs = BuildTurnPairs(conv);
                if (pairs.Count == 0) continue;

                var doc = new RagDocument
                {
                    DocId = $"agentconv:{conv.ConversationId}",
                    Source = RagSource.AgentConversations,
                    Title = $"Conversation {Short(conv.ConversationId)}",
                    Uri = file,
                    Content = string.Join("\n\n", pairs),
                    CreatedAtUnixMs = RagTime.ToUnixMs(conv.LastUpdated),
                    PreChunks = pairs,
                };
                doc.ContentHash = RagChunker.Hash(doc.Content);
                await Writer.UpsertAsync(doc, ct);
            }

            if (newWatermark > watermark) await SetCursorAsync(ConvCursor, newWatermark.ToString(), ct);
        }

        private async Task ScanMemoriesAsync(CancellationToken ct)
        {
            string dir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentMemoriesDirectory);
            if (!Directory.Exists(dir)) return;
            long watermark = long.TryParse(GetCursor(MemCursor), out var w) ? w : 0;
            long newWatermark = watermark;

            var presentDocIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                ct.ThrowIfCancellationRequested();
                var mem = TryRead<AgentMemoryEntry>(file);
                if (mem == null || string.IsNullOrWhiteSpace(mem.Content)) continue;
                presentDocIds.Add($"agentmem:{mem.Id}");

                long mtime = FileMtimeMs(file);
                if (mtime > watermark)
                {
                    newWatermark = Math.Max(newWatermark, mtime);
                    string text = string.Join(" ", new[] { mem.Title, mem.Content, string.Join(" ", mem.Tags ?? new()) }
                        .Where(s => !string.IsNullOrWhiteSpace(s)));
                    if (text.Length < MinChars) continue;
                    var doc = new RagDocument
                    {
                        DocId = $"agentmem:{mem.Id}",
                        Source = RagSource.AgentMemories,
                        Title = string.IsNullOrWhiteSpace(mem.Title) ? "Memory" : mem.Title,
                        Content = text,
                        ContentHash = RagChunker.Hash(text),
                        CreatedAtUnixMs = RagTime.ToUnixMs(mem.CreatedAt),
                        SingleChunk = true,
                    };
                    await Writer.UpsertAsync(doc, ct);
                }
            }

            // Tombstone memories whose file has been deleted.
            foreach (var docId in Writer.GetDocIdsForSource(RagSource.AgentMemories))
                if (!presentDocIds.Contains(docId))
                    await Writer.DeleteAsync(docId, ct);

            if (newWatermark > watermark) await SetCursorAsync(MemCursor, newWatermark.ToString(), ct);
        }

        // Deterministic user+assistant turn-pairs: consecutive user prose folds into the next agent
        // reply. Script bodies/outputs and system messages are excluded — prose only.
        private static List<string> BuildTurnPairs(AgentConversation conv)
        {
            var pairs = new List<string>();
            var pendingUser = new List<string>();
            foreach (var m in conv.Messages)
            {
                if (m == null || string.IsNullOrWhiteSpace(m.Content)) continue;
                if (m.Role == AgentMessageRole.User)
                {
                    pendingUser.Add(m.Content.Trim());
                }
                else if (m.Role == AgentMessageRole.Agent)
                {
                    string user = pendingUser.Count > 0 ? string.Join(" ", pendingUser) : "";
                    pendingUser.Clear();
                    string pair = (user.Length > 0 ? $"User: {user}\n\n" : "") + $"Agent: {m.Content.Trim()}";
                    pairs.Add(pair);
                }
                // System / Script roles are skipped.
            }
            if (pendingUser.Count > 0) pairs.Add($"User: {string.Join(" ", pendingUser)}");
            return pairs.Where(p => p.Length >= MinChars).ToList();
        }

        private static long FileMtimeMs(string file)
        {
            try { return new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeMilliseconds(); }
            catch { return 0; }
        }

        private static T? TryRead<T>(string file) where T : class
        {
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                return JsonConvert.DeserializeObject<T>(sr.ReadToEnd());
            }
            catch { return null; } // locked / partial write — retried next scan
        }

        private static string Short(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length <= 8 ? s : s.Substring(0, 8));
    }
}
