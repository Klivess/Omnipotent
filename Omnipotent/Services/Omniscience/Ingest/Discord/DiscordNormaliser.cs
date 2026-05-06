using Newtonsoft.Json.Linq;
using Omnipotent.Services.Omniscience.Domain;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Omnipotent.Services.Omniscience.Ingest.Discord
{
    /// <summary>Pure functions that translate Discord JSON payloads to <see cref="HarvestedMessage"/>.</summary>
    public static class DiscordNormaliser
    {
        public const string Platform = "discord";

        public static HarvestedMessage MessageFromJson(JObject m, string? guildId, string? guildName, string conversationKind, string? channelTitle)
        {
            var author = m["author"] as JObject ?? new JObject();
            var msg = new HarvestedMessage
            {
                Platform = Platform,
                PlatformMessageId = m.Value<string>("id") ?? "",
                SentAt = ParseTimestamp(m.Value<string>("timestamp")) ?? DateTime.UtcNow,
                EditedAt = ParseTimestamp(m.Value<string>("edited_timestamp")),
                Content = m.Value<string>("content"),
                ReplyToPlatformMessageId = (m["referenced_message"] as JObject)?.Value<string>("id")
                    ?? (m["message_reference"] as JObject)?.Value<string>("message_id"),
                ConversationKind = conversationKind,
                GuildId = guildId,
                GuildName = guildName,
                ChannelId = m.Value<string>("channel_id") ?? "",
                ChannelTitle = channelTitle,
                Author = IdentityFromJson(author),
                RawJson = m.ToString(Newtonsoft.Json.Formatting.None),
            };

            // Mentions \u2192 participants
            if (m["mentions"] is JArray mentions)
            {
                foreach (var u in mentions.OfType<JObject>())
                    msg.Participants.Add(IdentityFromJson(u));
            }

            if (m["attachments"] is JArray atts)
            {
                foreach (var a in atts.OfType<JObject>())
                {
                    string url = a.Value<string>("url") ?? "";
                    string? mime = a.Value<string>("content_type");
                    msg.Attachments.Add(new HarvestedAttachment
                    {
                        OriginalUrl = url,
                        Mime = mime,
                        Filename = a.Value<string>("filename"),
                        SizeBytes = a.Value<long?>("size"),
                        Kind = ClassifyKind(mime, a.Value<string>("filename")),
                    });
                }
            }

            return msg;
        }

        public static HarvestedIdentity IdentityFromJson(JObject u)
        {
            string id = u.Value<string>("id") ?? "";
            string? username = u.Value<string>("username");
            string? globalName = u.Value<string>("global_name");
            string? avatar = u.Value<string>("avatar");
            string? avatarUrl = (id != "" && !string.IsNullOrEmpty(avatar))
                ? $"https://cdn.discordapp.com/avatars/{id}/{avatar}.png?size=256"
                : null;
            return new HarvestedIdentity
            {
                Platform = Platform,
                PlatformUserId = id,
                Username = username,
                DisplayName = globalName ?? username,
                AvatarUrl = avatarUrl,
                Bio = u.Value<string>("bio"),
            };
        }

        public static DateTime? ParseTimestamp(string? iso)
        {
            if (string.IsNullOrEmpty(iso)) return null;
            if (DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                return dt.ToUniversalTime();
            return null;
        }

        private static string ClassifyKind(string? mime, string? filename)
        {
            if (!string.IsNullOrEmpty(mime))
            {
                if (mime.StartsWith("image/")) return "image";
                if (mime.StartsWith("video/")) return "video";
                if (mime.StartsWith("audio/")) return "voice";
            }
            string ext = (System.IO.Path.GetExtension(filename ?? "") ?? "").ToLowerInvariant();
            return ext switch
            {
                ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" => "image",
                ".mp4" or ".mov" or ".webm" or ".mkv" => "video",
                ".mp3" or ".ogg" or ".wav" or ".m4a" => "voice",
                _ => "file",
            };
        }
    }
}
