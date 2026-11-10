using System.Globalization;
using System.Text;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Ingest.Imports
{
    /// <summary>
    /// Any chat log that is a table: CSV or TSV with a header row, or a JSON list of objects. Columns are found by
    /// their usual names, or named explicitly in <see cref="ImportOptions.Columns"/>:
    /// <c>timestamp</c> and <c>sender</c> are required; <c>text</c>, <c>conversation</c>, <c>id</c>, <c>sender_id</c>
    /// and <c>reply_to</c> are optional.
    /// <para>
    /// A timestamp may be a unix time (seconds or milliseconds), ISO-8601 with an offset (both instants), or a
    /// wall-clock <c>yyyy-MM-dd HH:mm[:ss]</c>, which needs a zone and is stored as inferred.
    /// </para>
    /// </summary>
    public sealed class TableImport : IImportFormat
    {
        private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.Ordinal)
        {
            ["timestamp"] = new[] { "timestamp", "time", "date", "datetime", "sent_at", "sent", "created_at", "timestamp_ms", "date_unixtime" },
            ["sender"] = new[] { "sender", "from", "author", "sender_name", "name", "user", "username" },
            ["text"] = new[] { "text", "content", "message", "body", "contents" },
            ["conversation"] = new[] { "conversation", "chat", "channel", "thread", "room", "conversation_id", "chat_id" },
            ["id"] = new[] { "id", "message_id", "msg_id" },
            ["sender_id"] = new[] { "sender_id", "from_id", "author_id", "user_id" },
            ["reply_to"] = new[] { "reply_to", "reply_to_id", "reply_to_message_id", "in_reply_to" },
        };

        private static readonly string[] WallClockFormats =
        {
            "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        };

        public string Id => "table";
        public string Title => "Table (CSV, TSV or JSON list)";
        public bool NeedsZone => false;

        public double Detect(ImportInput input)
        {
            if (input.Entries.Count != 1) return 0;
            string ext = Path.GetExtension(input.Entries[0].Path).ToLowerInvariant();
            if (ext is not (".csv" or ".tsv" or ".json")) return 0;
            try
            {
                var first = Rows(input, input.Entries[0]).FirstOrDefault();
                if (first == null) return 0;
                var columns = Resolve(first.Keys, null);
                return columns.ContainsKey("timestamp") && columns.ContainsKey("sender") ? 0.5 : 0.05;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                return 0;
            }
        }

        public IEnumerable<IngestMessage> Read(ImportInput input, ImportOptions options, ImportDiagnostics diagnostics)
        {
            if (input.Entries.Count != 1) throw new InvalidDataException("A table import is a single CSV, TSV or JSON file.");
            var entry = input.Entries[0];
            string platform = string.IsNullOrWhiteSpace(options.Platform) ? "import" : options.Platform.Trim().ToLowerInvariant();
            TimeZoneInfo? zone = options.Zone == null ? null : Zones.GetOrThrow(options.Zone);
            diagnostics.ZoneOffered = options.Zone;
            string defaultConversation = options.ConversationTitle ?? Path.GetFileNameWithoutExtension(input.Name);
            string? owner = options.OwnerName?.Trim();

            Dictionary<string, string>? columns = null;
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            long order = 0;
            foreach (var row in Rows(input, entry))
            {
                columns ??= Require(Resolve(row.Keys, options.Columns), row.Keys);
                string? stamp = Get(row, columns, "timestamp"), sender = Get(row, columns, "sender");
                if (string.IsNullOrWhiteSpace(stamp) || string.IsNullOrWhiteSpace(sender))
                {
                    diagnostics.SkippedRecords++;
                    diagnostics.Warn("Rows without a timestamp or a sender were skipped.");
                    continue;
                }
                var (sent, quality) = Time(stamp.Trim(), zone, diagnostics);
                if (sent == null)
                {
                    diagnostics.SkippedRecords++;
                    diagnostics.Warn($"Rows with an unreadable timestamp (such as \"{stamp.Trim()}\") were skipped.");
                    continue;
                }

                sender = sender.Trim();
                string conversationName = NullIfBlank(Get(row, columns, "conversation")) ?? defaultConversation;
                string conversationKey = "chat:" + ImportIds.Key(conversationName);
                string? text = Get(row, columns, "text");
                string? id = NullIfBlank(Get(row, columns, "id"));
                if (id == null)
                {
                    string idKey = $"{conversationKey}\u0001{sender}\u0001{stamp}\u0001{text}";
                    int occurrence = occurrences.GetValueOrDefault(idKey);
                    occurrences[idKey] = occurrence + 1;
                    id = ImportIds.Message(conversationKey, sender, stamp.Trim(), text, occurrence);
                }
                else
                {
                    id = conversationKey + "/" + id;
                }
                string userId = NullIfBlank(Get(row, columns, "sender_id")) ?? "name:" + ImportIds.Key(sender);
                string? reply = NullIfBlank(Get(row, columns, "reply_to"));

                yield return new IngestMessage
                {
                    PlatformMessageId = id,
                    Conversation = new IngestConversation(platform, conversationKey, ConversationKinds.Unknown, conversationName),
                    Author = new IngestIdentity(platform, userId, DisplayName: sender, IsSelf: owner != null && sender == owner),
                    SentAtMs = sent.Value,
                    Quality = quality,
                    OrderKey = order++,
                    Content = string.IsNullOrEmpty(text) ? null : text,
                    ReplyToPlatformMessageId = reply == null ? null : conversationKey + "/" + reply,
                };
            }
        }

        private static (long? Ms, TimeQuality Quality) Time(string stamp, TimeZoneInfo? zone, ImportDiagnostics diagnostics)
        {
            if (long.TryParse(stamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number))
            {
                // Ten digits is seconds until the year 2286; thirteen is milliseconds.
                if (number is > 100_000_000 and < 100_000_000_000) return (number * 1000, TimeQuality.Second);
                if (number is >= 100_000_000_000 and < 100_000_000_000_000) return (number, TimeQuality.Exact);
                return (null, TimeQuality.Exact);
            }
            if (Instant.TryParseIsoWithOffset(stamp, out long instant)) return (instant, TimeQuality.Second);
            if (!DateTime.TryParseExact(stamp, WallClockFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall)) return (null, TimeQuality.Exact);
            if (zone == null) throw new ImportDecisionNeededException("This file's times have no zone or offset: choose the zone they were written in.");
            var resolved = Zones.ResolveLocal(wall, zone);
            diagnostics.ZoneUsed = diagnostics.ZoneOffered;
            if (resolved.WasAmbiguous) diagnostics.AmbiguousTimes++;
            if (resolved.WasInvalid) diagnostics.InvalidTimes++;
            return (resolved.UtcMs, TimeQuality.InferredZone);
        }

        /// <summary>field → the header that provides it. An explicit mapping wins over the usual names.</summary>
        private static Dictionary<string, string> Resolve(IEnumerable<string> headers, IReadOnlyDictionary<string, string>? chosen)
        {
            var byLower = headers.GroupBy(h => h.Trim().ToLowerInvariant(), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (field, names) in Aliases)
            {
                if (chosen != null && chosen.TryGetValue(field, out string? explicitName) && byLower.TryGetValue(explicitName.Trim().ToLowerInvariant(), out string? header))
                {
                    result[field] = header;
                    continue;
                }
                foreach (var name in names)
                    if (byLower.TryGetValue(name, out header)) { result[field] = header; break; }
            }
            return result;
        }

        private static Dictionary<string, string> Require(Dictionary<string, string> columns, IEnumerable<string> headers)
        {
            var missing = new[] { "timestamp", "sender" }.Where(f => !columns.ContainsKey(f)).ToList();
            if (missing.Count > 0)
                throw new ImportDecisionNeededException(
                    $"No column for {string.Join(" or ", missing)} could be found among: {string.Join(", ", headers)}. Say which column holds each.");
            return columns;
        }

        private static string? Get(IReadOnlyDictionary<string, string?> row, Dictionary<string, string> columns, string field) =>
            columns.TryGetValue(field, out string? header) && row.TryGetValue(header, out string? value) ? value : null;

        private static IEnumerable<IReadOnlyDictionary<string, string?>> Rows(ImportInput input, ImportEntry entry)
        {
            string ext = Path.GetExtension(entry.Path).ToLowerInvariant();
            return ext == ".json" ? JsonRows(input, entry) : DelimitedRows(input, entry, ext == ".tsv" ? '\t' : ',');
        }

        private static IEnumerable<IReadOnlyDictionary<string, string?>> DelimitedRows(ImportInput input, ImportEntry entry, char separator)
        {
            using var reader = new StringReader(input.ReadText(entry));
            IReadOnlyList<string>? header = null;
            foreach (var cells in Csv.Read(reader, separator))
            {
                if (header == null)
                {
                    header = cells.Select(c => c.Trim('﻿', ' ')).ToList();
                    continue;
                }
                var row = new Dictionary<string, string?>(StringComparer.Ordinal);
                for (int i = 0; i < header.Count && i < cells.Count; i++) row.TryAdd(header[i], cells[i]);
                yield return row;
            }
        }

        private static IEnumerable<IReadOnlyDictionary<string, string?>> JsonRows(ImportInput input, ImportEntry entry)
        {
            using var reader = new JsonPullReader(input.OpenEntry(entry));
            if (!reader.Read() || reader.Token != JsonTokenType.StartArray) throw new InvalidDataException("A JSON table must be a list of objects.");
            while (reader.TryReadElement(out var e))
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var row = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var p in e.EnumerateObject())
                {
                    row.TryAdd(p.Name, p.Value.ValueKind switch
                    {
                        JsonValueKind.String => p.Value.GetString(),
                        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => p.Value.GetRawText(),
                        JsonValueKind.Null or JsonValueKind.Undefined => null,
                        _ => p.Value.GetRawText(),
                    });
                }
                yield return row;
            }
        }

        private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
