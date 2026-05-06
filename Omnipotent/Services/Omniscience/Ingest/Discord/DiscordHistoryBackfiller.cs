using Newtonsoft.Json.Linq;
using Omnipotent.Data_Handling;
using Omnipotent.Services.Omniscience.Domain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Omnipotent.Services.Omniscience.Ingest.Discord
{
    /// <summary>
    /// Walks every visible DM and guild text channel for a given source, paginating
    /// backwards via /channels/{id}/messages?before=. Persists ingest_cursors so a
    /// restart can pick up where it left off.
    /// </summary>
    public class DiscordHistoryBackfiller
    {
        private readonly Omniscience service;
        private readonly OmniscienceDb db;
        private readonly DiscordRestClient rest;
        private readonly IngestPipeline pipeline;
        private readonly string sourceId;

        public DiscordHistoryBackfiller(Omniscience service, OmniscienceDb db, DiscordRestClient rest, IngestPipeline pipeline, string sourceId)
        {
            this.service = service;
            this.db = db;
            this.rest = rest;
            this.pipeline = pipeline;
            this.sourceId = sourceId;
        }

        public async Task RunAsync(CancellationToken ct)
        {
            // ── DMs ──
            try
            {
                var dms = await rest.GetDmChannelsAsync(ct) as JArray;
                if (dms != null)
                {
                    foreach (var ch in dms.OfType<JObject>())
                    {
                        ct.ThrowIfCancellationRequested();
                        await BackfillChannelAsync(ch, null, null, ct);
                    }
                }
            }
            catch (Exception ex) { _ = service.ServiceLogError(ex, "Backfill DM enumeration failed"); }

            // ── Guilds ──
            try
            {
                var guilds = await rest.GetGuildsAsync(ct) as JArray;
                if (guilds != null)
                {
                    foreach (var g in guilds.OfType<JObject>())
                    {
                        ct.ThrowIfCancellationRequested();
                        string gid = g.Value<string>("id") ?? "";
                        string gname = g.Value<string>("name") ?? "";
                        try
                        {
                            var channels = await rest.GetGuildChannelsAsync(gid, ct) as JArray;
                            if (channels == null) continue;
                            foreach (var ch in channels.OfType<JObject>())
                            {
                                int type = ch.Value<int?>("type") ?? -1;
                                // 0 = GUILD_TEXT, 5 = ANNOUNCEMENT, 11 = PUBLIC_THREAD, 12 = PRIVATE_THREAD, 15 = FORUM
                                if (type != 0 && type != 5) continue;
                                ct.ThrowIfCancellationRequested();
                                await BackfillChannelAsync(ch, gid, gname, ct);
                            }
                        }
                        catch (Exception ex) { _ = service.ServiceLogError(ex, $"Backfill failed for guild {gname}"); }
                    }
                }
            }
            catch (Exception ex) { _ = service.ServiceLogError(ex, "Backfill guild enumeration failed"); }

            // Mark source synced.
            await db.WriteLock.WaitAsync(ct);
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE harvest_sources SET last_full_sync_at=$ts WHERE source_id=$id";
                cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$id", sourceId);
                cmd.ExecuteNonQuery();
            }
            finally { db.WriteLock.Release(); }
        }

        private async Task BackfillChannelAsync(JObject ch, string? guildId, string? guildName, CancellationToken ct)
        {
            string channelId = ch.Value<string>("id") ?? "";
            if (string.IsNullOrEmpty(channelId)) return;

            int chType = ch.Value<int?>("type") ?? 0;
            string convKind = chType switch
            {
                1 => "dm",
                3 => "group_dm",
                _ => "guild_channel",
            };
            string? title = ch.Value<string>("name");
            if (convKind == "dm")
            {
                var rec = ch["recipients"] as JArray;
                title = rec?.OfType<JObject>().Select(r => r.Value<string>("global_name") ?? r.Value<string>("username")).FirstOrDefault();
            }

            string conversationId = DiscordNormaliser.Platform + ":" + channelId;
            (string? earliest, bool fully) = ReadCursor(conversationId);

            string? cursor = earliest;
            int batches = 0;
            while (!fully)
            {
                ct.ThrowIfCancellationRequested();
                JArray? page;
                try
                {
                    page = await rest.GetMessagesBeforeAsync(channelId, cursor, 100, ct) as JArray;
                }
                catch (System.Net.Http.HttpRequestException ex) when ((int?)(ex.StatusCode) is 403 or 404 or 401)
                {
                    // No access; mark fully done so we don't keep retrying.
                    WriteCursor(conversationId, cursor, null, fully: true);
                    return;
                }

                if (page == null || page.Count == 0)
                {
                    WriteCursor(conversationId, cursor, null, fully: true);
                    return;
                }

                foreach (var m in page.OfType<JObject>())
                {
                    var hm = DiscordNormaliser.MessageFromJson(m, guildId, guildName, convKind, title);

                    // Group DM recipients are part of the channel object: stamp them as participants
                    // so social graph captures them even from messages that never mention them.
                    if (convKind != "guild_channel")
                    {
                        var recArr = ch["recipients"] as JArray;
                        if (recArr != null)
                        {
                            foreach (var r in recArr.OfType<JObject>())
                                hm.Participants.Add(DiscordNormaliser.IdentityFromJson(r));
                        }
                    }

                    await pipeline.IngestAsync(hm, ct);
                }

                // Oldest message in page becomes the next "before" cursor.
                cursor = page.OfType<JObject>().Last().Value<string>("id");
                WriteCursor(conversationId, cursor, null, fully: false);

                batches++;
                if (batches % 5 == 0)
                    await service.ServiceLog($"Backfilled {batches * 100}+ msgs from {convKind}/{title ?? channelId}");

                // Pace ourselves to look human and avoid 429 storms.
                await Task.Delay(550, ct);
            }
        }

        private (string? earliest, bool fully) ReadCursor(string conversationId)
        {
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT earliest_message_id, fully_backfilled FROM ingest_cursors WHERE source_id=$s AND conversation_id=$c";
            cmd.Parameters.AddWithValue("$s", sourceId);
            cmd.Parameters.AddWithValue("$c", conversationId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return (null, false);
            string? earliest = r.IsDBNull(0) ? null : r.GetString(0);
            bool fully = r.GetInt64(1) != 0;
            return (earliest, fully);
        }

        private void WriteCursor(string conversationId, string? earliest, string? latest, bool fully)
        {
            db.WriteLock.Wait();
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO ingest_cursors(source_id, conversation_id, earliest_message_id, latest_message_id, fully_backfilled, last_synced_at)
                    VALUES($s,$c,$e,$l,$f,$ts)
                    ON CONFLICT(source_id, conversation_id) DO UPDATE SET
                        earliest_message_id=COALESCE(excluded.earliest_message_id, earliest_message_id),
                        latest_message_id=COALESCE(excluded.latest_message_id, latest_message_id),
                        fully_backfilled=excluded.fully_backfilled,
                        last_synced_at=excluded.last_synced_at";
                cmd.Parameters.AddWithValue("$s", sourceId);
                cmd.Parameters.AddWithValue("$c", conversationId);
                cmd.Parameters.AddWithValue("$e", (object?)earliest ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$l", (object?)latest ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$f", fully ? 1 : 0);
                cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                cmd.ExecuteNonQuery();
            }
            finally { db.WriteLock.Release(); }
        }
    }
}
