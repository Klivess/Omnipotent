using DSharpPlus;
using DSharpPlus.Entities;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveBot_Discord;
using Omnipotent.Services.Omniscience.Ingest.Discord;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Ingest.KliveBot
{
    /// <summary>
    /// Second Discord sensor: bridges the KliveBot DSharpPlus client into the ingest
    /// pipeline. Everything the bot can see — every message in every server it's in,
    /// DMs sent to it, reactions, typing, voice states — flows through the same
    /// normaliser and dedupe path as the user-token gateway, so overlapping coverage
    /// is free. Also runs a polite REST backfill through the bot token.
    /// Guild message *content* requires the privileged message-content intent.
    /// </summary>
    public class KliveBotIngester
    {
        private const string CursorSourceId = "klivebot";

        private readonly Omniscience service;
        private readonly OmniscienceDb db;
        private readonly IngestPipeline pipeline;
        private readonly DiscordEventRecorder recorder;
        private DiscordClient? client;

        public KliveBotIngester(Omniscience service, OmniscienceDb db, IngestPipeline pipeline, DiscordEventRecorder recorder)
        {
            this.service = service;
            this.db = db;
            this.pipeline = pipeline;
            this.recorder = recorder;
        }

        public async Task StartAsync(CancellationToken ct)
        {
            var bots = await service.GetServicesByType<KliveBotDiscord>();
            var bot = bots is { Length: > 0 } ? bots[0] as KliveBotDiscord : null;
            if (bot == null)
            {
                await service.ServiceLog("[Omniscience] KliveBot service not found; bot feed disabled.");
                return;
            }
            // The bot service connects on its own schedule.
            int waited = 0;
            while (bot.Client == null && waited < 120_000 && !ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                waited += 1000;
            }
            client = bot.Client;
            if (client == null)
            {
                await service.ServiceLog("[Omniscience] KliveBot client never initialised; bot feed disabled.");
                return;
            }

            client.MessageCreated += async (s, e) =>
            {
                try { await IngestBotMessage(e.Message, e.Guild); }
                catch (Exception ex) { _ = service.ServiceLogError(ex, "[Omniscience] KliveBot message ingest failed"); }
            };
            client.MessageUpdated += async (s, e) =>
            {
                try
                {
                    if (e.Message == null) return;
                    string compositeId = DiscordNormaliser.Platform + ":" + e.Message.Id;
                    recorder.RecordEdit(compositeId, e.Message.Content, e.Message.EditedTimestamp?.UtcDateTime);
                    await IngestBotMessage(e.Message, e.Guild); // no-op when already stored
                }
                catch (Exception ex) { _ = service.ServiceLogError(ex, "[Omniscience] KliveBot edit capture failed"); }
            };
            client.MessageDeleted += (s, e) =>
            {
                recorder.OnGatewayEvent("MESSAGE_DELETE", new JObject(
                    new JProperty("id", e.Message?.Id.ToString() ?? ""),
                    new JProperty("channel_id", e.Channel?.Id.ToString())));
                return Task.CompletedTask;
            };
            client.MessageReactionAdded += (s, e) =>
            {
                recorder.OnGatewayEvent("MESSAGE_REACTION_ADD", ReactionJson(e.User, e.Channel, e.Message, e.Emoji));
                return Task.CompletedTask;
            };
            client.MessageReactionRemoved += (s, e) =>
            {
                recorder.OnGatewayEvent("MESSAGE_REACTION_REMOVE", ReactionJson(e.User, e.Channel, e.Message, e.Emoji));
                return Task.CompletedTask;
            };
            client.TypingStarted += (s, e) =>
            {
                recorder.OnGatewayEvent("TYPING_START", new JObject(
                    new JProperty("user_id", e.User?.Id.ToString()),
                    new JProperty("channel_id", e.Channel?.Id.ToString()),
                    new JProperty("timestamp", e.StartedAt.ToUnixTimeSeconds())));
                return Task.CompletedTask;
            };
            client.VoiceStateUpdated += (s, e) =>
            {
                recorder.OnGatewayEvent("VOICE_STATE_UPDATE", new JObject(
                    new JProperty("user_id", e.User?.Id.ToString()),
                    new JProperty("channel_id", e.After?.Channel?.Id.ToString()),
                    new JProperty("guild_id", e.Guild?.Id.ToString())));
                return Task.CompletedTask;
            };

            await service.ServiceLog("[Omniscience] KliveBot feed online: live events bridged into the pipeline.");

            bool backfill = await service.GetBoolOmniSetting("OmniscienceKliveBotBackfill", true);
            if (backfill)
            {
                _ = Task.Run(async () =>
                {
                    try { await BackfillAsync(ct); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { _ = service.ServiceLogError(ex, "[Omniscience] KliveBot backfill crashed"); }
                }, ct);
            }
        }

        private static JObject ReactionJson(DiscordUser? user, DiscordChannel? channel, DiscordMessage? message, DiscordEmoji? emoji)
        {
            var e = new JObject(new JProperty("name", emoji?.Name ?? "?"));
            if (emoji != null && emoji.Id != 0) e["id"] = emoji.Id.ToString();
            return new JObject(
                new JProperty("user_id", user?.Id.ToString()),
                new JProperty("channel_id", channel?.Id.ToString()),
                new JProperty("message_id", message?.Id.ToString()),
                new JProperty("emoji", e));
        }

        private async Task IngestBotMessage(DiscordMessage msg, DiscordGuild? guild)
        {
            if (msg == null || msg.Author == null) return;
            string kind = msg.Channel?.IsPrivate == true
                ? (msg.Channel.Type == ChannelType.Group ? "group_dm" : "dm")
                : "guild_channel";
            var json = ToDiscordJson(msg);
            var hm = DiscordNormaliser.MessageFromJson(json, guild?.Id.ToString(), guild?.Name, kind, msg.Channel?.Name);
            await pipeline.IngestAsync(hm, CancellationToken.None);
        }

        // Rebuilds the Discord REST/gateway JSON shape from DSharpPlus entities so the
        // existing normaliser stays the single translation point.
        private static JObject ToDiscordJson(DiscordMessage m)
        {
            // DSharpPlus 4.x doesn't expose global_name; the normaliser falls back to username.
            var author = new JObject(
                new JProperty("id", m.Author.Id.ToString()),
                new JProperty("username", m.Author.Username),
                new JProperty("avatar", m.Author.AvatarHash));
            var attachments = new JArray(m.Attachments.Select(a => new JObject(
                new JProperty("url", a.Url),
                new JProperty("content_type", a.MediaType),
                new JProperty("filename", a.FileName),
                new JProperty("size", a.FileSize))));
            var mentions = new JArray(m.MentionedUsers.Where(u => u != null).Select(u => new JObject(
                new JProperty("id", u.Id.ToString()),
                new JProperty("username", u.Username),
                new JProperty("avatar", u.AvatarHash))));
            var json = new JObject(
                new JProperty("id", m.Id.ToString()),
                new JProperty("channel_id", m.ChannelId.ToString()),
                new JProperty("content", m.Content),
                new JProperty("timestamp", m.Timestamp.UtcDateTime.ToString("o")),
                new JProperty("edited_timestamp", m.EditedTimestamp?.UtcDateTime.ToString("o")),
                new JProperty("author", author),
                new JProperty("attachments", attachments),
                new JProperty("mentions", mentions));
            if (m.ReferencedMessage != null)
                json["referenced_message"] = new JObject(new JProperty("id", m.ReferencedMessage.Id.ToString()));
            return json;
        }

        // ── Backfill: page every readable channel backwards through history ──

        private async Task BackfillAsync(CancellationToken ct)
        {
            if (client == null) return;
            await Task.Delay(TimeSpan.FromSeconds(30), ct); // let live traffic settle first

            var channels = new List<DiscordChannel>();
            foreach (var guild in client.Guilds.Values)
            {
                try
                {
                    channels.AddRange(guild.Channels.Values.Where(c =>
                        c.Type is ChannelType.Text or ChannelType.News));
                }
                catch { }
            }

            foreach (var channel in channels)
            {
                ct.ThrowIfCancellationRequested();
                try { await BackfillChannelAsync(channel, ct); }
                catch (OperationCanceledException) { throw; }
                catch (DSharpPlus.Exceptions.UnauthorizedException) { /* hidden channel */ }
                catch (Exception ex) { _ = service.ServiceLogError(ex, $"[Omniscience] KliveBot backfill failed for #{channel.Name}"); }
            }
            await service.ServiceLog("[Omniscience] KliveBot backfill pass complete.");
        }

        private async Task BackfillChannelAsync(DiscordChannel channel, CancellationToken ct)
        {
            string conversationId = DiscordNormaliser.Platform + ":" + channel.Id;
            var (earliest, fullyBackfilled) = LoadCursor(conversationId);
            if (fullyBackfilled) return;

            ulong? before = earliest != null && ulong.TryParse(earliest, out var e) ? e : null;
            string? guildId = channel.Guild?.Id.ToString();
            string? guildName = channel.Guild?.Name;
            string kind = channel.IsPrivate ? (channel.Type == ChannelType.Group ? "group_dm" : "dm") : "guild_channel";

            while (!ct.IsCancellationRequested)
            {
                IReadOnlyList<DiscordMessage> page;
                try
                {
                    page = before.HasValue
                        ? await channel.GetMessagesBeforeAsync(before.Value, 100)
                        : await channel.GetMessagesAsync(100);
                }
                catch (DSharpPlus.Exceptions.UnauthorizedException) { return; }

                if (page.Count == 0)
                {
                    await SaveCursor(conversationId, before?.ToString(), fully: true);
                    return;
                }

                foreach (var m in page)
                {
                    try
                    {
                        var json = ToDiscordJson(m);
                        var hm = DiscordNormaliser.MessageFromJson(json, guildId, guildName, kind, channel.Name);
                        await pipeline.IngestAsync(hm, ct);
                    }
                    catch (Exception ex) { _ = service.ServiceLogError(ex, "[Omniscience] KliveBot backfill ingest failed"); }
                }

                before = page.Min(m => m.Id);
                await SaveCursor(conversationId, before.ToString(), fully: page.Count < 100);
                if (page.Count < 100) return;
                await Task.Delay(1200, ct); // stay polite with the REST API
            }
        }

        private (string? earliest, bool fullyBackfilled) LoadCursor(string conversationId)
        {
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT earliest_message_id, fully_backfilled FROM ingest_cursors WHERE source_id=$s AND conversation_id=$c";
            cmd.Parameters.AddWithValue("$s", CursorSourceId);
            cmd.Parameters.AddWithValue("$c", conversationId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return (null, false);
            return (r.IsDBNull(0) ? null : r.GetString(0), !r.IsDBNull(1) && r.GetInt32(1) == 1);
        }

        private async Task SaveCursor(string conversationId, string? earliest, bool fully)
        {
            await db.WriteLock.WaitAsync();
            try
            {
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO ingest_cursors(source_id, conversation_id, earliest_message_id, fully_backfilled, last_synced_at)
                    VALUES($s,$c,$e,$f,$t)
                    ON CONFLICT(source_id, conversation_id) DO UPDATE SET
                        earliest_message_id=excluded.earliest_message_id,
                        fully_backfilled=excluded.fully_backfilled,
                        last_synced_at=excluded.last_synced_at";
                cmd.Parameters.AddWithValue("$s", CursorSourceId);
                cmd.Parameters.AddWithValue("$c", conversationId);
                cmd.Parameters.AddWithValue("$e", (object?)earliest ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$f", fully ? 1 : 0);
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                cmd.ExecuteNonQuery();
            }
            finally { db.WriteLock.Release(); }
        }
    }
}
