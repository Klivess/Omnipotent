using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.Omniscience.Ingest;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Scheduling
{
    /// <summary>
    /// Background maintenance: one-time reaction backfill out of stored raw_json (free
    /// historical data — message JSON carries reaction emoji+counts), nightly OCR
    /// backfill over historical image attachments, and retention pruning of high-volume
    /// event streams. Runs shortly after startup and then daily.
    /// </summary>
    public class MaintenanceJobs
    {
        private readonly Omniscience service;
        private readonly OmniscienceDb db;
        private readonly OcrService ocr;
        private readonly CancellationTokenSource cts = new();
        private readonly SemaphoreSlim runLock = new(1, 1);

        private const string ReactionCursorKey = "reaction_backfill_cursor";
        private const string ReactionDoneKey = "reaction_backfill_done";

        public MaintenanceJobs(Omniscience service, OmniscienceDb db, OcrService ocr)
        {
            this.service = service;
            this.db = db;
            this.ocr = ocr;
        }

        public void Start()
        {
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(TimeSpan.FromMinutes(5), cts.Token); } catch { return; }
                while (!cts.IsCancellationRequested)
                {
                    try { await RunOnceAsync(cts.Token); }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { _ = service.ServiceLogError(ex, "[Omniscience] Maintenance pass failed"); }
                    try { await Task.Delay(TimeSpan.FromHours(24), cts.Token); } catch { break; }
                }
            });
        }

        public void Stop() => cts.Cancel();

        public async Task RunOnceAsync(CancellationToken ct)
        {
            if (!await runLock.WaitAsync(0, ct)) return;
            try
            {
                await BackfillReactionsFromRawJsonAsync(ct);
                int ocrDone = await ocr.BackfillBatchAsync(300, ct);
                if (ocrDone > 0) await service.ServiceLog($"[Omniscience] OCR backfill: processed {ocrDone} images.");
                await PruneEventsAsync(ct);
            }
            finally { runLock.Release(); }
        }

        // ── Reaction backfill: mine reactions already sitting in messages.raw_json ──
        private async Task BackfillReactionsFromRawJsonAsync(CancellationToken ct)
        {
            if (GetMeta(ReactionDoneKey) == "1") return;
            string cursor = GetMeta(ReactionCursorKey) ?? "";
            int scanned = 0, inserted = 0;

            while (!ct.IsCancellationRequested)
            {
                var rows = new List<(string MessageId, string PlatformMessageId, string ConversationId, long SentAt, string RawJson)>();
                using (var conn = db.Open())
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"SELECT message_id, platform_message_id, conversation_id, sent_at, raw_json
                        FROM messages
                        WHERE message_id > $cursor AND raw_json LIKE '%""reactions""%'
                        ORDER BY message_id ASC LIMIT 500";
                    cmd.Parameters.AddWithValue("$cursor", cursor);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetString(4)));
                }
                if (rows.Count == 0)
                {
                    SetMeta(ReactionDoneKey, "1");
                    if (scanned > 0)
                        await service.ServiceLog($"[Omniscience] Reaction backfill complete: {inserted} snapshot rows from {scanned} messages.");
                    return;
                }

                await db.WriteLock.WaitAsync(ct);
                try
                {
                    using var conn = db.Open();
                    using var tx = conn.BeginTransaction();
                    foreach (var row in rows)
                    {
                        scanned++;
                        cursor = row.MessageId;
                        try
                        {
                            var json = JObject.Parse(row.RawJson);
                            if (json["reactions"] is not JArray reactions) continue;
                            foreach (var reaction in reactions)
                            {
                                var emojiObj = reaction["emoji"] as JObject;
                                string emoji = emojiObj?.Value<string>("name") ?? "?";
                                if (!string.IsNullOrEmpty(emojiObj?.Value<string>("id"))) emoji = ":" + emoji + ":";
                                int count = reaction.Value<int?>("count") ?? 1;

                                // Idempotence: snapshot rows are unique per (message, emoji).
                                using var check = conn.CreateCommand();
                                check.Transaction = tx;
                                check.CommandText = @"SELECT 1 FROM reaction_events
                                    WHERE platform_message_id=$m AND emoji=$e AND action='snapshot' LIMIT 1";
                                check.Parameters.AddWithValue("$m", row.PlatformMessageId);
                                check.Parameters.AddWithValue("$e", emoji);
                                if (check.ExecuteScalar() != null) continue;

                                using var ins = conn.CreateCommand();
                                ins.Transaction = tx;
                                ins.CommandText = @"INSERT INTO reaction_events
                                    (platform_message_id, channel_id, reactor_platform_user_id, emoji, action, count, occurred_at)
                                    VALUES($m,$c,'',$e,'snapshot',$n,$t)";
                                ins.Parameters.AddWithValue("$m", row.PlatformMessageId);
                                ins.Parameters.AddWithValue("$c", row.ConversationId);
                                ins.Parameters.AddWithValue("$e", emoji);
                                ins.Parameters.AddWithValue("$n", count);
                                ins.Parameters.AddWithValue("$t", row.SentAt);
                                ins.ExecuteNonQuery();
                                inserted++;
                            }
                        }
                        catch { /* malformed raw_json: skip */ }
                    }
                    tx.Commit();
                }
                finally { db.WriteLock.Release(); }

                SetMeta(ReactionCursorKey, cursor);
                await Task.Delay(100, ct); // yield between batches
            }
        }

        // ── Retention: presence/typing are high-volume; aggregates persist in modules ──
        private async Task PruneEventsAsync(CancellationToken ct)
        {
            int retentionDays = Math.Clamp(await service.GetIntOmniSetting("OmniscienceEventRetentionDays", 365), 30, 3650);
            long cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToUnixTimeMilliseconds();
            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                foreach (var (table, column) in new[] { ("presence_events", "captured_at"), ("typing_events", "started_at") })
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"DELETE FROM {table} WHERE {column} < $cut";
                    cmd.Parameters.AddWithValue("$cut", cutoff);
                    int n = cmd.ExecuteNonQuery();
                    if (n > 0) _ = service.ServiceLog($"[Omniscience] Pruned {n} rows from {table} (retention {retentionDays}d).");
                }
            }
            finally { db.WriteLock.Release(); }
        }

        // ── omniscience_meta helpers ──
        private string? GetMeta(string key)
        {
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT value FROM omniscience_meta WHERE key=$k";
                cmd.Parameters.AddWithValue("$k", key);
                return cmd.ExecuteScalar() as string;
            }
            catch { return null; }
        }

        private void SetMeta(string key, string value)
        {
            db.WriteLock.Wait();
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO omniscience_meta(key, value) VALUES($k,$v)
                    ON CONFLICT(key) DO UPDATE SET value=excluded.value";
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$v", value);
                cmd.ExecuteNonQuery();
            }
            finally { db.WriteLock.Release(); }
        }
    }
}
