using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Replica
{
    /// <summary>
    /// Per-chat interaction memory only. Summaries never enter person_facts, training
    /// corpora or analytics, so generated statements cannot become evidence about anyone.
    /// </summary>
    public sealed class ReplicaChatMemory
    {
        private const int VerbatimTurns = 12;
        private const int SummariseAfterNewRows = 12;
        private readonly OmniscienceDb db;

        public ReplicaChatMemory(OmniscienceDb db) { this.db = db; }

        public sealed record MemoryState(string Summary, IReadOnlyList<(string Role, string Content)> History);

        public async Task<MemoryState> LoadAsync(long chatId, CancellationToken ct)
        {
            await Task.Yield();
            using var conn = db.Open();
            string summary = "";
            using (var chat = conn.CreateCommand())
            {
                chat.CommandText = "SELECT COALESCE(summary,'') FROM replica_chats WHERE chat_id=$c";
                chat.Parameters.AddWithValue("$c", chatId);
                summary = cmdString(chat.ExecuteScalar());
            }

            using var cmd = conn.CreateCommand();
            // message_id, not sent_at: immediate consecutive turns can share a millisecond.
            cmd.CommandText = @"SELECT role, content FROM replica_chat_messages
                WHERE chat_id=$c AND role IN ('user','assistant')
                ORDER BY message_id DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$c", chatId);
            cmd.Parameters.AddWithValue("$n", VerbatimTurns);
            var history = new List<(string Role, string Content)>();
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read()) history.Add((r.GetString(0), r.GetString(1)));
            }
            history.Reverse();
            return new MemoryState(summary, history);
        }

        public async Task MaybeSummariseAsync(long chatId, KliveLLM.KliveLLM llm, CancellationToken ct)
        {
            List<(long Id, string Role, string Content)> rows = new();
            string previousSummary = "";
            long watermark = 0;
            await Task.Yield();
            using (var conn = db.Open())
            {
                using (var chat = conn.CreateCommand())
                {
                    chat.CommandText = "SELECT COALESCE(summary,''), COALESCE(summary_through_message_id,0) FROM replica_chats WHERE chat_id=$c";
                    chat.Parameters.AddWithValue("$c", chatId);
                    using var chatReader = chat.ExecuteReader();
                    if (!chatReader.Read()) return;
                    previousSummary = chatReader.GetString(0);
                    watermark = chatReader.GetInt64(1);
                }
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT message_id, role, content FROM replica_chat_messages
                    WHERE chat_id=$c AND role IN ('user','assistant') ORDER BY message_id ASC";
                cmd.Parameters.AddWithValue("$c", chatId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
            }
            if (rows.Count < VerbatimTurns + SummariseAfterNewRows) return;
            long cutoffId = rows[^VerbatimTurns].Id; // first row retained verbatim
            var older = rows.Where(r => r.Id < cutoffId && r.Id > watermark).ToList();
            if (older.Count < SummariseAfterNewRows) return;

            string prompt = BuildSummaryPrompt(previousSummary, older);
            var response = await llm.QueryLLM(prompt, sessionId: null, maxTokensOverride: 360,
                systemPrompt: "You compress an AI-simulation chat into durable interaction memory. " +
                              "Retain unresolved topics, references, preferences expressed by the user and commitments made in the chat. " +
                              "Do not turn simulation statements into facts about any real person. Output at most 180 words.",
                useFreeModel: false, cancellationToken: ct);
            if (!response.Success || string.IsNullOrWhiteSpace(response.Response)) return;

            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"UPDATE replica_chats SET summary=$s, summary_through_message_id=$w
                    WHERE chat_id=$c AND COALESCE(summary_through_message_id,0)=$expected";
                cmd.Parameters.AddWithValue("$s", response.Response.Trim());
                cmd.Parameters.AddWithValue("$w", cutoffId - 1);
                cmd.Parameters.AddWithValue("$expected", watermark);
                cmd.Parameters.AddWithValue("$c", chatId);
                cmd.ExecuteNonQuery();
            }
            finally { db.WriteLock.Release(); }
        }

        internal static string BuildSummaryPrompt(string previousSummary, IReadOnlyList<(long Id, string Role, string Content)> older)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(previousSummary))
            {
                sb.AppendLine("Existing memory to preserve and refine:");
                sb.AppendLine(Truncate(previousSummary, 1800));
                sb.AppendLine();
            }
            sb.AppendLine("New older turns to fold into memory (simulation transcript, not real-world evidence):");
            int budget = 6500;
            foreach (var row in older)
            {
                string line = $"{(row.Role == "assistant" ? "Replica" : "User")}: {Truncate(row.Content, 500)}";
                if (budget - line.Length < 0) break;
                sb.AppendLine(line);
                budget -= line.Length;
            }
            sb.AppendLine("Return only the updated compact memory.");
            return sb.ToString();
        }

        private static string Truncate(string? value, int max) => string.IsNullOrEmpty(value) || value.Length <= max ? value ?? "" : value[..max] + "…";
        private static string cmdString(object? value) => value == null || value == DBNull.Value ? "" : value.ToString() ?? "";
    }
}
