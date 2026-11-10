using System.Globalization;
using System.Text;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Storage.Legacy;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Ingest.Imports
{
    /// <summary>
    /// Telegram Desktop's JSON export (<c>result.json</c>): one chat, or the full account export with every chat
    /// under <c>chats.list</c>. Read as a stream, one message at a time, because a full export is routinely
    /// larger than memory.
    /// <para>
    /// Time: <c>date_unixtime</c> is an instant and is used when present (second precision). Older exports only
    /// have <c>date</c>, the exporting computer's wall clock, which needs a zone like any other local time.
    /// Identity: <c>from_id</c> ("user123") is stable across name changes; the name is recorded as it was on each message.
    /// </para>
    /// </summary>
    public sealed class TelegramImport : IImportFormat
    {
        public const string Platform = "telegram";
        private const string NotIncluded = "(File not included";

        public string Id => "telegram";
        public string Title => "Telegram export (JSON)";
        /// <summary>Only exports without <c>date_unixtime</c> need one; <see cref="Read"/> asks when it meets such a message.</summary>
        public bool NeedsZone => false;

        private sealed class Scope
        {
            public string? Property;
            public string? Name, Type, ChatId;
        }

        public double Detect(ImportInput input)
        {
            var file = FindResult(input);
            if (file == null) return 0;
            using var s = input.OpenEntry(file);
            byte[] buffer = new byte[4096];
            int read = s.Read(buffer, 0, buffer.Length);
            string head = Encoding.UTF8.GetString(buffer, 0, read);
            bool chat = head.Contains("\"messages\"", StringComparison.Ordinal) && head.Contains("\"type\"", StringComparison.Ordinal);
            bool full = head.Contains("\"personal_information\"", StringComparison.Ordinal) || head.Contains("\"chats\"", StringComparison.Ordinal);
            if (!chat && !full) return 0;
            return file.FileName.Equals("result.json", StringComparison.OrdinalIgnoreCase) ? 0.9 : head.Contains("date_unixtime", StringComparison.Ordinal) ? 0.8 : 0.4;
        }

        private static ImportEntry? FindResult(ImportInput input)
        {
            var json = input.Entries.Where(e => e.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToList();
            return json.Where(e => e.FileName.Equals("result.json", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Path.Length).FirstOrDefault()
                   ?? (json.Count == 1 ? json[0] : null);
        }

        public IEnumerable<IngestMessage> Read(ImportInput input, ImportOptions options, ImportDiagnostics diagnostics)
        {
            var file = FindResult(input) ?? throw new InvalidDataException("No Telegram result.json was found in this export.");
            string baseDir = file.Path.Contains('/') ? file.Path[..(file.Path.LastIndexOf('/') + 1)] : "";
            TimeZoneInfo? zone = options.Zone == null ? null : Zones.GetOrThrow(options.Zone);
            diagnostics.ZoneOffered = options.Zone;
            string? ownerId = null;
            string? ownerName = options.OwnerName?.Trim();
            long order = 0;

            using var reader = new JsonPullReader(input.OpenEntry(file));
            var scopes = new Stack<Scope>();
            string? pendingProperty = null;
            while (reader.Read())
            {
                switch (reader.Token)
                {
                    case JsonTokenType.StartObject:
                        scopes.Push(new Scope { Property = pendingProperty });
                        pendingProperty = null;
                        break;
                    case JsonTokenType.EndObject:
                        if (scopes.Count > 0) scopes.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        string property = reader.Text!;
                        var scope = scopes.Count > 0 ? scopes.Peek() : null;
                        if (scope == null) break;
                        if (property == "messages")
                        {
                            if (!reader.Read() || reader.Token != JsonTokenType.StartArray) break;
                            string chatId = scope.ChatId ?? ImportIds.Key(scope.Name ?? input.Name);
                            var conversation = new IngestConversation(Platform, chatId, KindOf(scope.Type), options.ConversationTitle ?? scope.Name);
                            while (reader.TryReadElement(out var element))
                            {
                                var message = Message(element, conversation, chatId, baseDir, input, zone, ownerId, ownerName, order++, diagnostics);
                                if (message != null) yield return message;
                            }
                        }
                        else if (property is "name" or "type" or "id" or "user_id")
                        {
                            var value = reader.ReadValue();
                            string? text = value.ValueKind switch
                            {
                                JsonValueKind.String => value.GetString(),
                                JsonValueKind.Number => value.GetRawText(),
                                _ => null,
                            };
                            if (property == "name") scope.Name = text;
                            else if (property == "type") scope.Type = text;
                            else if (property == "id") scope.ChatId = text;
                            else if (scope.Property == "personal_information" && text != null) ownerId = "user" + text;
                        }
                        else
                        {
                            pendingProperty = property;
                        }
                        break;
                    default:
                        pendingProperty = null;
                        break;
                }
            }
            if (ownerId != null) diagnostics.OwnerDetected = ownerId;
        }

        private static string KindOf(string? type) => type switch
        {
            "personal_chat" or "bot_chat" or "saved_messages" => ConversationKinds.Dm,
            "private_group" or "private_supergroup" or "public_supergroup" => ConversationKinds.GroupDm,
            "private_channel" or "public_channel" => ConversationKinds.Channel,
            _ => ConversationKinds.Unknown,
        };

        private static IngestMessage? Message(JsonElement e, IngestConversation conversation, string chatId, string baseDir, ImportInput input,
            TimeZoneInfo? zone, string? ownerId, string? ownerName, long order, ImportDiagnostics diagnostics)
        {
            if (e.ValueKind != JsonValueKind.Object) { diagnostics.SkippedRecords++; return null; }
            string? id = Scalar(e, "id");
            if (id == null) { diagnostics.SkippedRecords++; diagnostics.Warn("A Telegram message without an id was skipped."); return null; }

            var (sent, quality) = Time(e, "date_unixtime", "date", zone, diagnostics);
            if (sent == null) { diagnostics.SkippedRecords++; diagnostics.Warn("A Telegram message without a readable date was skipped."); return null; }
            var (edited, _) = e.TryGetProperty("edited", out _) || e.TryGetProperty("edited_unixtime", out _)
                ? Time(e, "edited_unixtime", "edited", zone, diagnostics)
                : (null, TimeQuality.Exact);

            bool service = Scalar(e, "type") == "service";
            string? fromName = Scalar(e, service ? "actor" : "from");
            string? fromId = Scalar(e, service ? "actor_id" : "from_id");
            // A deleted account has neither a name nor an id left in the export.
            string userId = fromId ?? (fromName != null ? "name:" + ImportIds.Key(fromName) : "deleted");
            bool self = (ownerId != null && fromId == ownerId) || (ownerId == null && ownerName != null && fromName == ownerName);
            var author = new IngestIdentity(Platform, userId, DisplayName: fromName, IsBot: conversation.Kind == ConversationKinds.Dm && fromId?.StartsWith("bot", StringComparison.Ordinal) == true, IsSelf: self);

            string? content = service ? ServiceText(e) : NullIfEmpty(Text(e));
            if (service) diagnostics.SystemLines++;

            var attachments = new List<IngestAttachment>();
            if (Scalar(e, "photo") is { } photo)
                attachments.Add(Attachment(photo, baseDir, input, MediaKinds.Image, null, IntOf(e, "width"), IntOf(e, "height"), null, diagnostics));
            if (Scalar(e, "file") is { } path)
            {
                string? mediaType = Scalar(e, "media_type");
                string? mime = Scalar(e, "mime_type");
                string kind = mediaType switch
                {
                    "voice_message" => MediaKinds.Voice,
                    "audio_file" => MediaKinds.Audio,
                    "video_file" or "video_message" or "animation" => MediaKinds.Video,
                    "sticker" => MediaKinds.Image,
                    _ => MediaKinds.Classify(mime, path),
                };
                long? duration = IntOf(e, "duration_seconds") is { } seconds ? seconds * 1000L : null;
                attachments.Add(Attachment(path, baseDir, input, kind, mime, IntOf(e, "width"), IntOf(e, "height"), duration, diagnostics));
            }

            var reactions = new List<IngestReaction>();
            if (e.TryGetProperty("reactions", out var rs) && rs.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in rs.EnumerateArray())
                {
                    string? emoji = Scalar(r, "emoji") ?? Scalar(r, "document_id");
                    if (emoji == null) continue;
                    if (IntOf(r, "count") is { } count) reactions.Add(new IngestReaction(emoji, emoji, Count: count));
                    if (!r.TryGetProperty("recent", out var recent) || recent.ValueKind != JsonValueKind.Array) continue;
                    foreach (var who in recent.EnumerateArray())
                    {
                        string? actorId = Scalar(who, "from_id");
                        if (actorId == null) continue;
                        var (reactedAt, _) = Time(who, "date_unixtime", "date", zone, null);
                        reactions.Add(new IngestReaction(emoji, emoji, new IngestIdentity(Platform, actorId, DisplayName: Scalar(who, "from")), reactedAt));
                    }
                }
            }

            string? forwarded = Scalar(e, "forwarded_from") is { } origin
                ? JsonSerializer.Serialize(new { from = origin, from_id = Scalar(e, "forwarded_from_id") })
                : null;

            return new IngestMessage
            {
                PlatformMessageId = chatId + "/" + id,
                Conversation = conversation,
                Author = author,
                SentAtMs = sent.Value,
                Quality = quality,
                OrderKey = long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out long numeric) ? numeric : order,
                Content = content,
                EditedAtMs = edited,
                IsSystem = service,
                ReplyToPlatformMessageId = Scalar(e, "reply_to_message_id") is { } reply ? chatId + "/" + reply : null,
                ForwardedFromJson = forwarded,
                Attachments = attachments,
                Reactions = reactions,
                RawJson = e.GetRawText(),
            };
        }

        /// <summary>An instant from the unix field when present, else the wall-clock field read in the chosen zone.</summary>
        private static (long? Ms, TimeQuality Quality) Time(JsonElement e, string unixField, string localField, TimeZoneInfo? zone, ImportDiagnostics? diagnostics)
        {
            if (Scalar(e, unixField) is { } unix && long.TryParse(unix, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds) && seconds > 0)
                return (seconds * 1000, TimeQuality.Second);
            string? local = Scalar(e, localField);
            if (local == null) return (null, TimeQuality.Exact);
            if (Instant.TryParseIsoWithOffset(local, out long withOffset)) return (withOffset, TimeQuality.Second);
            if (!DateTime.TryParseExact(local, new[] { "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall))
                return (null, TimeQuality.Exact);
            if (zone == null)
                throw new ImportDecisionNeededException("This Telegram export has no unix times, only the exporting computer's wall clock: choose the zone it was in.");
            var resolved = Zones.ResolveLocal(wall, zone);
            if (diagnostics != null)
            {
                diagnostics.ZoneUsed = diagnostics.ZoneOffered;
                if (resolved.WasAmbiguous) diagnostics.AmbiguousTimes++;
                if (resolved.WasInvalid) diagnostics.InvalidTimes++;
            }
            return (resolved.UtcMs, TimeQuality.InferredZone);
        }

        /// <summary><c>text</c> is a string, or a list mixing strings and formatted runs.</summary>
        private static string Text(JsonElement e)
        {
            if (!e.TryGetProperty("text", out var text)) return "";
            if (text.ValueKind == JsonValueKind.String) return text.GetString() ?? "";
            if (text.ValueKind != JsonValueKind.Array) return "";
            var sb = new StringBuilder();
            foreach (var part in text.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.String) sb.Append(part.GetString());
                else if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var run) && run.ValueKind == JsonValueKind.String) sb.Append(run.GetString());
            }
            return sb.ToString();
        }

        private static string ServiceText(JsonElement e)
        {
            string action = Scalar(e, "action") ?? "service";
            string? detail = Scalar(e, "title") ?? Scalar(e, "discard_reason");
            return detail == null ? $"[{action}]" : $"[{action}] {detail}";
        }

        private static IngestAttachment Attachment(string path, string baseDir, ImportInput input, string kind, string? mime, int? width, int? height, long? duration, ImportDiagnostics diagnostics)
        {
            string name = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
            if (path.StartsWith(NotIncluded, StringComparison.Ordinal))
            {
                diagnostics.MediaMissing++;
                return new IngestAttachment(null, mime, kind, Width: width, Height: height, DurationMs: duration, Unavailable: true);
            }
            var entry = input.Find(baseDir + path);
            if (entry == null)
            {
                diagnostics.MediaMissing++;
                return new IngestAttachment(name, mime, kind, Width: width, Height: height, DurationMs: duration, Unavailable: true);
            }
            diagnostics.MediaFound++;
            return new IngestAttachment(name, mime, kind, entry.Length, width, height, duration, LocalFile: () => input.OpenEntry(entry));
        }

        private static string? Scalar(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.String => NullIfEmpty(v.GetString() ?? ""),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            };
        }

        private static int? IntOf(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : null;

        private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
    }
}
