using System.Globalization;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Storage.Legacy;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Ingest.Discord
{
    /// <summary>A Discord channel as the bot knows it: enough to file its messages under the right conversation.</summary>
    public sealed record DiscordChannelInfo(string ChannelId, string Kind, string? Title = null, string? GuildId = null, string? GuildName = null,
        string? ParentChannelId = null)
    {
        /// <summary>The people in a DM or group DM (the bot's own account included, as Discord lists it).</summary>
        public IReadOnlyList<IngestIdentity>? Recipients { get; init; }

        /// <summary>The conversation record, its title stamped as current at <paramref name="observedAtMs"/>.</summary>
        public IngestConversation ToConversation(long? observedAtMs = null) =>
            new(DiscordPayload.Platform, ChannelId, Kind, Title, GuildId, GuildName, ParentChannelId) { Members = Recipients, ObservedAtMs = observedAtMs };
    }

    /// <summary>
    /// Who is reading Discord. <see cref="ContentIntent"/> says whether Discord hands this bot the text of server
    /// messages at all: without the message-content intent it sends them with empty text, embeds and attachments,
    /// except in DMs, for the bot's own messages and for messages that mention it.
    /// </summary>
    public sealed record DiscordReader(string? BotUserId, string? OwnerUserId, bool ContentIntent);

    /// <summary>
    /// Turns Discord's own JSON (the same shape from the gateway and from the bot API) into the records the corpus
    /// writer takes. It reads the whole payload — type, flags, forwards, polls, stickers, voice notes, every name an
    /// account has — and keeps the payload itself, so nothing v2's thin rebuild dropped is lost (F419–F422, F452,
    /// F453). Pure: no clock, no I/O; the caller says when a profile was observed.
    /// </summary>
    public static class DiscordPayload
    {
        public const string Platform = "discord";

        /// <summary>The conversation kind of a Discord channel type; null for types that hold no messages (categories, forums themselves).</summary>
        public static string? ConversationKind(int channelType) => channelType switch
        {
            1 => ConversationKinds.Dm,
            3 => ConversationKinds.GroupDm,
            0 or 2 or 13 => ConversationKinds.Text,          // text, voice-channel chat, stage chat
            5 => ConversationKinds.Channel,                  // announcements
            10 or 11 or 12 => ConversationKinds.Thread,      // announcement, public and private threads (forum posts are public threads)
            _ => null,
        };

        /// <summary>A channel object (from the bot API or a thread list) as a channel record; null when it holds no messages.</summary>
        public static DiscordChannelInfo? Channel(JsonElement channel, DiscordReader reader, string? guildName = null, long? observedAtMs = null)
        {
            if (channel.ValueKind != JsonValueKind.Object || !IsSnowflake(Str(channel, "id"))) return null;
            string? kind = ConversationKind(Int(channel, "type") ?? -1);
            if (kind == null) return null;
            string? guildId = Str(channel, "guild_id");
            // A thread's parent is the channel it hangs off; a channel's parent is only its category, which is not a conversation.
            string? parent = kind == ConversationKinds.Thread ? Str(channel, "parent_id") : null;
            List<IngestIdentity>? recipients = null;
            if (channel.TryGetProperty("recipients", out var rs) && rs.ValueKind == JsonValueKind.Array)
                recipients = rs.EnumerateArray().Select(u => User(u, reader, null, null, observedAtMs)).OfType<IngestIdentity>().ToList();
            return new DiscordChannelInfo(Str(channel, "id")!, kind, NullIfBlank(Str(channel, "name")), guildId, guildName, parent) { Recipients = recipients };
        }

        /// <summary>
        /// A user object as an identity: username, global display name, bot flag, picture, and — with the server's
        /// <paramref name="member"/> object — the nickname it carries in that server.
        /// </summary>
        public static IngestIdentity? User(JsonElement user, DiscordReader reader, JsonElement? member = null, string? guildId = null, long? observedAtMs = null)
        {
            if (user.ValueKind != JsonValueKind.Object) return null;
            string? id = Str(user, "id");
            if (!IsSnowflake(id)) return null;
            string? nick = member is { ValueKind: JsonValueKind.Object } mb ? NullIfBlank(Str(mb, "nick")) : null;
            return new IngestIdentity(Platform, id!, NullIfBlank(Str(user, "username")), NullIfBlank(Str(user, "global_name")), Bool(user, "bot"),
                reader.OwnerUserId != null && id == reader.OwnerUserId)
            {
                Nickname = nick,
                // Only a member object says anything about the nickname; without one the server's timeline is left alone.
                NicknameScope = member != null && guildId != null ? guildId : null,
                AvatarHash = NullIfBlank(Str(user, "avatar")),
                ObservedAtMs = observedAtMs,
            };
        }

        public static IngestMessage? Message(string json, DiscordChannelInfo channel, DiscordReader reader, long? observedAtMs = null)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (JsonException) { return null; }
            using (doc) return Message(doc.RootElement, channel, reader, observedAtMs);
        }

        /// <summary>
        /// One message object. Null when it is not a message Discord could have sent (no id, no author).
        /// <paramref name="observedAtMs"/> is when the payload was fetched: Discord puts the author's profile as it is
        /// now on every message, however old.
        /// </summary>
        public static IngestMessage? Message(JsonElement m, DiscordChannelInfo channel, DiscordReader reader, long? observedAtMs = null)
        {
            if (m.ValueKind != JsonValueKind.Object) return null;
            string? id = Str(m, "id");
            if (!ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out ulong snowflake) || snowflake > long.MaxValue) return null;
            if (!m.TryGetProperty("author", out var authorJson)) return null;
            string raw = m.GetRawText();
            var parsed = DiscordRaw.TryParse(raw);
            if (parsed == null) return null;

            string? guildId = channel.GuildId ?? parsed.GuildId;
            var author = User(authorJson, reader, m.TryGetProperty("member", out var member) ? member : null, guildId, observedAtMs);
            if (author == null) return null;
            // A webhook posts under whatever name each message gives it: one automated account, not a person.
            if (parsed.IsWebhook) author = author with { IsBot = true };

            string? content = m.TryGetProperty("content", out var contentJson) && contentJson.ValueKind == JsonValueKind.String ? contentJson.GetString() : null;
            if (string.IsNullOrEmpty(content) && !ContentDelivered(channel, reader, author, parsed)) content = null;

            long? edited = Str(m, "edited_timestamp") is { } e && Instant.TryParseIsoWithOffset(e, out long editedMs) ? editedMs : null;
            return new IngestMessage
            {
                PlatformMessageId = id!,
                Conversation = channel.ToConversation(observedAtMs) with { GuildId = guildId },
                Author = author,
                // The id carries the send time, so live, history and data-package copies of a message always agree (plan T1).
                SentAtMs = Instant.FromDiscordSnowflake(snowflake),
                Quality = TimeQuality.Exact,
                OrderKey = (long)snowflake,
                Content = content,
                EditedAtMs = edited,
                IsSystem = parsed.AuthorSystem || DiscordRaw.IsSystemType(parsed.Type),
                IsWebhook = parsed.IsWebhook,
                Type = parsed.Type,
                Flags = parsed.Flags,
                ReplyToPlatformMessageId = parsed.ReplyToPlatformMessageId,
                ForwardedFromJson = parsed.ForwardJson,
                Attachments = parsed.Attachments.Select(a => new IngestAttachment(a.Filename, a.ContentType, a.MediaKind, a.Size, a.Width, a.Height, a.DurationMs,
                    a.AltText, a.Spoiler, a.Url, a.Id)).ToList(),
                Reactions = parsed.Reactions.Select(r => new IngestReaction(r.EmojiKey, r.EmojiName, Count: r.Count)).ToList(),
                Mentions = parsed.Mentions,
                Embeds = parsed.Embeds,
                Stickers = parsed.Stickers,
                Poll = parsed.Poll,
                RawJson = raw,
            };
        }

        /// <summary>
        /// Whether an empty text really means "no text". Without the content intent Discord blanks server messages,
        /// so empty there means "not told" and is stored as unknown, which can never replace real text (F458).
        /// </summary>
        private static bool ContentDelivered(DiscordChannelInfo channel, DiscordReader reader, IngestIdentity author, DiscordRawMessage parsed)
        {
            if (reader.ContentIntent) return true;
            if (channel.Kind is ConversationKinds.Dm or ConversationKinds.GroupDm) return true;
            if (reader.BotUserId == null) return false;
            return author.PlatformUserId == reader.BotUserId || parsed.Mentions.Any(x => x.Kind == "user" && x.Target == Platform + ":" + reader.BotUserId);
        }

        /// <summary>The key reactions are stored under: <c>name:id</c> for a server's own emoji, the character itself otherwise.</summary>
        public static string? EmojiKey(string? name, string? id) =>
            !string.IsNullOrEmpty(id) && id != "0" ? $"{name}:{id}" : string.IsNullOrEmpty(name) ? null : name;

        public static bool IsSnowflake(string? id) =>
            id is { Length: >= 15 and <= 20 } && ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out ulong v) && v <= long.MaxValue;

        /// <summary>Orders two snowflakes (text comparison would put a 17-digit id after an 18-digit one).</summary>
        public static int Compare(string a, string b) => ulong.Parse(a, CultureInfo.InvariantCulture).CompareTo(ulong.Parse(b, CultureInfo.InvariantCulture));

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            } : null;

        private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        private static int? Int(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : null;

        private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
