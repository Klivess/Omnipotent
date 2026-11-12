using System.Text.Json;

namespace Omnipotent.Services.OmniscienceV3.Storage.Legacy
{
    public sealed record RawAttachment(string? Id, string? Filename, string? Url, string? ContentType, long? Size, int? Width, int? Height,
        long? DurationMs, string? AltText, bool Spoiler, string MediaKind);

    public sealed record RawEmbed(string? Type, string? Provider, string? Url, string? Title, string? Description, string Json);

    public sealed record RawSticker(string Id, string? Name);

    public sealed record RawPoll(string? Question, string AnswersJson, long? ExpiryMs);

    public sealed record RawReaction(string EmojiKey, string? EmojiName, int Count);

    public sealed record RawMention(string Kind, string Target);

    /// <summary>The parts of a Discord message payload v3 stores in structured form (the payload itself is kept too).</summary>
    public sealed record DiscordRawMessage(
        int Type,
        long Flags,
        bool AuthorBot,
        bool AuthorSystem,
        bool IsWebhook,
        string? GuildId,
        string? ReplyToPlatformMessageId,
        string? ForwardJson,
        IReadOnlyList<RawMention> Mentions,
        IReadOnlyList<RawAttachment> Attachments,
        IReadOnlyList<RawEmbed> Embeds,
        IReadOnlyList<RawSticker> Stickers,
        RawPoll? Poll,
        IReadOnlyList<RawReaction> Reactions);

    /// <summary>
    /// Reads Discord message JSON (gateway or REST shape). Fixes v2's reading of it (F361): forwards are not
    /// replies (a <c>message_reference</c> of type 1 is a forward, and pins/crossposts/follows also carry a
    /// reference), embeds, stickers, polls and forward snapshots are kept, and spoiler/voice flags are honoured.
    /// Tolerant of missing fields and malformed values: anything unreadable is simply absent.
    /// </summary>
    public static class DiscordRaw
    {
        private const int TypeReply = 19;
        private const int ReferenceForward = 1;
        private const long FlagVoiceMessage = 1 << 13;
        private const long AttachmentFlagSpoiler = 1 << 3;

        /// <summary>
        /// True for message types Discord writes itself (someone joined, a message was pinned, a call started, a
        /// boost, a poll's result). Ordinary messages, replies, thread starters and command responses are not.
        /// </summary>
        public static bool IsSystemType(int type) => type is not (0 or 19 or 20 or 21 or 23);

        public static DiscordRawMessage? TryParse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (JsonException) { return null; }
            using (doc)
            {
                var m = doc.RootElement;
                if (m.ValueKind != JsonValueKind.Object) return null;

                int type = Int(m, "type") ?? 0;
                long flags = Long(m, "flags") ?? 0;
                var author = Obj(m, "author");
                bool bot = author is { } a && Bool(a, "bot");
                bool system = author is { } a2 && Bool(a2, "system");
                bool webhook = Str(m, "webhook_id") != null;

                string? reply = null;
                string? forward = null;
                if (Obj(m, "message_reference") is { } reference)
                {
                    int refType = Int(reference, "type") ?? 0;
                    if (refType == ReferenceForward)
                    {
                        var fwd = new Dictionary<string, object?>
                        {
                            ["reference"] = JsonSerializer.Deserialize<JsonElement>(reference.GetRawText()),
                            ["snapshots"] = m.TryGetProperty("message_snapshots", out var snaps) ? JsonSerializer.Deserialize<JsonElement>(snaps.GetRawText()) : null,
                        };
                        forward = JsonSerializer.Serialize(fwd);
                    }
                    else if (type == TypeReply)
                    {
                        reply = Str(reference, "message_id") ?? (Obj(m, "referenced_message") is { } rm ? Str(rm, "id") : null);
                    }
                }

                var mentions = new List<RawMention>();
                foreach (var u in Array(m, "mentions"))
                    if (Str(u, "id") is { } uid) mentions.Add(new RawMention("user", CorpusIdsSafe("discord", uid)));
                foreach (var r in Array(m, "mention_roles"))
                    if (r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } rid) mentions.Add(new RawMention("role", "discord:" + rid));
                foreach (var c in Array(m, "mention_channels"))
                    if (Str(c, "id") is { } cid) mentions.Add(new RawMention("channel", "discord:" + cid));
                string? guildId = Str(m, "guild_id");
                if (Bool(m, "mention_everyone")) mentions.Add(new RawMention("everyone", guildId == null ? "*" : "discord:" + guildId));

                bool voiceMessage = (flags & FlagVoiceMessage) != 0;
                var attachments = new List<RawAttachment>();
                foreach (var at in Array(m, "attachments"))
                {
                    string? filename = Str(at, "filename");
                    string? contentType = Str(at, "content_type");
                    long attFlags = Long(at, "flags") ?? 0;
                    double? seconds = Double(at, "duration_secs");
                    attachments.Add(new RawAttachment(
                        Str(at, "id"), filename, Str(at, "url"), contentType, Long(at, "size"), Int(at, "width"), Int(at, "height"),
                        seconds is { } s ? (long)Math.Round(s * 1000) : null, Str(at, "description"),
                        (attFlags & AttachmentFlagSpoiler) != 0 || (filename?.StartsWith("SPOILER_", StringComparison.Ordinal) ?? false),
                        voiceMessage ? "voice" : MediaKinds.Classify(contentType, filename)));
                }

                var embeds = Array(m, "embeds").Select(e => new RawEmbed(
                    Str(e, "type"), Obj(e, "provider") is { } p ? Str(p, "name") : null, Str(e, "url"), Str(e, "title"), Str(e, "description"), e.GetRawText())).ToList();

                var stickers = Array(m, "sticker_items").Concat(Array(m, "stickers"))
                    .Select(s => Str(s, "id") is { } sid ? new RawSticker(sid, Str(s, "name")) : null)
                    .Where(s => s != null).Select(s => s!).DistinctBy(s => s.Id).ToList();

                RawPoll? poll = null;
                if (Obj(m, "poll") is { } pl)
                {
                    string? question = Obj(pl, "question") is { } q ? Str(q, "text") : null;
                    string answers = pl.TryGetProperty("answers", out var ans) ? ans.GetRawText() : "[]";
                    long? expiry = Str(pl, "expiry") is { } ex && Temporal.Instant.TryParseIsoWithOffset(ex, out long exMs) ? exMs : null;
                    poll = new RawPoll(question, answers, expiry);
                }

                var reactions = new List<RawReaction>();
                foreach (var r in Array(m, "reactions"))
                {
                    if (Obj(r, "emoji") is not { } emoji) continue;
                    string? name = Str(emoji, "name");
                    string? id = Str(emoji, "id");
                    string? key = id != null ? $"{name}:{id}" : name;
                    if (string.IsNullOrEmpty(key)) continue;
                    reactions.Add(new RawReaction(key, name, Int(r, "count") ?? 0));
                }

                return new DiscordRawMessage(type, flags, bot, system, webhook, guildId, reply, forward, mentions.Distinct().ToList(),
                    attachments, embeds, stickers, poll, reactions);
            }
        }

        private static string CorpusIdsSafe(string platform, string id) => platform + ":" + id;

        private static JsonElement? Obj(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

        private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : Enumerable.Empty<JsonElement>();

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            } : null;

        private static bool Bool(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        private static int? Int(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : null;

        private static long? Long(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l : null;

        private static double? Double(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) && double.IsFinite(d) ? d : null;
    }

    /// <summary>The media kinds attachments are classified into.</summary>
    public static class MediaKinds
    {
        public const string Image = "image";
        public const string Video = "video";
        public const string Audio = "audio";
        public const string Voice = "voice";
        public const string File = "file";

        public static string Classify(string? mime, string? filename)
        {
            if (!string.IsNullOrEmpty(mime))
            {
                if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return Image;
                if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return Video;
                if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return Audio;
            }
            return (Path.GetExtension(filename ?? "") ?? "").ToLowerInvariant() switch
            {
                ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".heic" or ".avif" => Image,
                ".mp4" or ".mov" or ".webm" or ".mkv" or ".avi" or ".m4v" => Video,
                ".mp3" or ".ogg" or ".wav" or ".m4a" or ".flac" or ".opus" or ".aac" => Audio,
                _ => File,
            };
        }

        /// <summary>Maps v2's attachment kinds ("image", "video", "voice", "file") onto v3's.</summary>
        public static string FromLegacy(string? legacyKind, string? mime, string? filename) => legacyKind switch
        {
            "image" => Image,
            "video" => Video,
            "voice" => Voice,
            _ => Classify(mime, filename),
        };
    }
}
