using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Ingest.Imports
{
    /// <summary>
    /// Discord's own "Request my data" package. It holds only what the account owner wrote, per channel:
    /// <c>messages/c&lt;channel&gt;/channel.json</c> and <c>messages.json</c> (packages before 2024: <c>messages.csv</c>).
    /// It is the one official way to recover the owner's side of closed DMs.
    /// <list type="bullet">
    /// <item><b>Author:</b> always the owner (<c>account/user.json</c>).</item>
    /// <item><b>Time:</b> derived from the message id (a snowflake), so a message imported here and the same message
    /// captured live or migrated from v2 agree to the millisecond.</item>
    /// <item><b>Attachments:</b> the package lists links, which have long expired. They are recorded as known but
    /// unavailable; nothing is fetched.</item>
    /// </list>
    /// </summary>
    public sealed class DiscordPackageImport : IImportFormat
    {
        public const string Platform = "discord";
        private static readonly Regex ChannelFolder = new(@"(^|/)messages/c?(?<id>\d{5,25})/(?<file>channel\.json|messages\.json|messages\.csv)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex DmTitle = new(@"^Direct Message with (?<n>.+)$", RegexOptions.Compiled);

        public string Id => "discord-package";
        public string Title => "Discord data package";
        public bool NeedsZone => false;

        public double Detect(ImportInput input)
        {
            int channels = input.Entries.Count(e => ChannelFolder.IsMatch(e.Path));
            if (channels == 0) return 0;
            bool account = input.Entries.Any(e => e.Path.EndsWith("account/user.json", StringComparison.OrdinalIgnoreCase));
            bool index = input.Entries.Any(e => e.Path.EndsWith("messages/index.json", StringComparison.OrdinalIgnoreCase));
            return account ? 0.95 : index ? 0.85 : 0.6;
        }

        public IEnumerable<IngestMessage> Read(ImportInput input, ImportOptions options, ImportDiagnostics diagnostics)
        {
            var owner = ReadOwner(input, diagnostics)
                        ?? throw new InvalidDataException("account/user.json is missing or unreadable, so the package's owner is unknown. Import the whole package, not only its messages folder.");
            diagnostics.OwnerDetected = owner.Username ?? owner.PlatformUserId;
            var titles = ReadIndex(input, diagnostics);

            var channels = input.Entries.Select(e => (Entry: e, Match: ChannelFolder.Match(e.Path))).Where(x => x.Match.Success)
                .GroupBy(x => x.Match.Groups["id"].Value, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal);
            foreach (var channel in channels)
            {
                ImportEntry? Named(string file) => channel.FirstOrDefault(x => x.Match.Groups["file"].Value.Equals(file, StringComparison.OrdinalIgnoreCase)).Entry;
                var conversation = ReadChannel(input, Named("channel.json"), channel.Key, titles.GetValueOrDefault(channel.Key), owner, diagnostics);

                var json = Named("messages.json");
                var csv = Named("messages.csv");
                if (json == null && csv == null) continue;
                IEnumerable<(string? Id, string? Contents, string? Attachments)> rows = json != null ? JsonRows(input, json, diagnostics) : CsvRows(input, csv!, diagnostics);
                foreach (var row in rows)
                {
                    if (row.Id == null || !ulong.TryParse(row.Id, NumberStyles.None, CultureInfo.InvariantCulture, out ulong snowflake) || snowflake > long.MaxValue)
                    {
                        diagnostics.SkippedRecords++;
                        diagnostics.Warn("A package message without a numeric id was skipped.");
                        continue;
                    }
                    var attachments = (row.Attachments ?? "").Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(u => u.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        .Select(u => new IngestAttachment(FileNameOf(u), SourceUrl: u, Unavailable: true)).ToList();
                    diagnostics.MediaMissing += attachments.Count;
                    yield return new IngestMessage
                    {
                        PlatformMessageId = row.Id,
                        Conversation = conversation,
                        Author = owner,
                        SentAtMs = Instant.FromDiscordSnowflake(snowflake),
                        Quality = TimeQuality.Exact,
                        OrderKey = (long)snowflake,
                        Content = string.IsNullOrEmpty(row.Contents) ? null : row.Contents,
                        Attachments = attachments,
                    };
                }
            }
        }

        private static IngestIdentity? ReadOwner(ImportInput input, ImportDiagnostics diagnostics)
        {
            var entry = input.Entries.Where(e => e.Path.EndsWith("account/user.json", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Path.Length).FirstOrDefault();
            if (entry == null) return null;
            try
            {
                using var doc = JsonDocument.Parse(input.ReadText(entry));
                string? id = Scalar(doc.RootElement, "id");
                if (id == null) return null;
                return new IngestIdentity(Platform, id, Scalar(doc.RootElement, "username"), Scalar(doc.RootElement, "global_name"), IsSelf: true);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                diagnostics.UnreadableFiles.Add((entry.Path, ex.Message));
                return null;
            }
        }

        /// <summary><c>messages/index.json</c>: channel id → "Direct Message with ada" / "general in Some Server".</summary>
        private static Dictionary<string, string> ReadIndex(ImportInput input, ImportDiagnostics diagnostics)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var entry = input.Entries.Where(e => e.Path.EndsWith("messages/index.json", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Path.Length).FirstOrDefault();
            if (entry == null) return result;
            try
            {
                using var doc = JsonDocument.Parse(input.ReadText(entry));
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString())) result[p.Name] = p.Value.GetString()!;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                diagnostics.UnreadableFiles.Add((entry.Path, ex.Message));
            }
            return result;
        }

        private static IngestConversation ReadChannel(ImportInput input, ImportEntry? entry, string channelId, string? indexTitle, IngestIdentity owner, ImportDiagnostics diagnostics)
        {
            string kind = ConversationKinds.Unknown;
            string? name = null, guildId = null, guildName = null;
            var members = new List<IngestIdentity>();
            if (entry != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(input.ReadText(entry));
                    var root = doc.RootElement;
                    // "type" is a number in older packages and a name in newer ones.
                    kind = Scalar(root, "type") switch
                    {
                        "1" or "DM" => ConversationKinds.Dm,
                        "3" or "GROUP_DM" => ConversationKinds.GroupDm,
                        "10" or "11" or "12" or "PUBLIC_THREAD" or "PRIVATE_THREAD" or "ANNOUNCEMENT_THREAD" => ConversationKinds.Thread,
                        null => ConversationKinds.Unknown,
                        _ => ConversationKinds.Text,
                    };
                    name = Scalar(root, "name");
                    if (root.TryGetProperty("guild", out var guild) && guild.ValueKind == JsonValueKind.Object)
                    {
                        guildId = Scalar(guild, "id");
                        guildName = Scalar(guild, "name");
                    }
                    if (root.TryGetProperty("recipients", out var recipients) && recipients.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var r in recipients.EnumerateArray())
                        {
                            string? id = r.ValueKind == JsonValueKind.String ? r.GetString() : r.ValueKind == JsonValueKind.Number ? r.GetRawText() : null;
                            if (!string.IsNullOrWhiteSpace(id) && id != owner.PlatformUserId) members.Add(new IngestIdentity(Platform, id));
                        }
                    }
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
                {
                    diagnostics.UnreadableFiles.Add((entry.Path, ex.Message));
                }
            }

            // A DM has no name of its own: the index names the other person.
            string? title = name;
            if (title == null && indexTitle != null)
            {
                var dm = DmTitle.Match(indexTitle);
                title = dm.Success ? dm.Groups["n"].Value : indexTitle;
                if (kind == ConversationKinds.Unknown && dm.Success) kind = ConversationKinds.Dm;
            }
            return new IngestConversation(Platform, channelId, kind, title, guildId, guildName) { Members = members };
        }

        private static IEnumerable<(string? Id, string? Contents, string? Attachments)> JsonRows(ImportInput input, ImportEntry entry, ImportDiagnostics diagnostics)
        {
            JsonPullReader reader;
            try
            {
                reader = new JsonPullReader(input.OpenEntry(entry));
            }
            catch (IOException ex)
            {
                diagnostics.UnreadableFiles.Add((entry.Path, ex.Message));
                yield break;
            }
            using (reader)
            {
                if (!Advance(reader, entry, diagnostics) || reader.Token != JsonTokenType.StartArray)
                {
                    diagnostics.UnreadableFiles.Add((entry.Path, "not a list of messages"));
                    yield break;
                }
                while (true)
                {
                    JsonElement e;
                    try
                    {
                        if (!reader.TryReadElement(out e)) break;
                    }
                    catch (JsonException ex)
                    {
                        // Everything before the damage has already been yielded; the rest of this file is lost, the package is not.
                        diagnostics.UnreadableFiles.Add((entry.Path, "damaged part-way through: " + ex.Message));
                        break;
                    }
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    yield return (Scalar(e, "ID") ?? Scalar(e, "id"), Scalar(e, "Contents") ?? Scalar(e, "contents"), Scalar(e, "Attachments") ?? Scalar(e, "attachments"));
                }
            }
        }

        private static bool Advance(JsonPullReader reader, ImportEntry entry, ImportDiagnostics diagnostics)
        {
            try
            {
                return reader.Read();
            }
            catch (JsonException ex)
            {
                diagnostics.UnreadableFiles.Add((entry.Path, ex.Message));
                return false;
            }
        }

        private static IEnumerable<(string? Id, string? Contents, string? Attachments)> CsvRows(ImportInput input, ImportEntry entry, ImportDiagnostics diagnostics)
        {
            using var text = new StreamReader(input.OpenEntry(entry), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            int id = -1, contents = -1, attachments = -1;
            bool header = true;
            foreach (var row in Csv.Read(text, ','))
            {
                if (header)
                {
                    header = false;
                    id = IndexOf(row, "ID");
                    contents = IndexOf(row, "Contents");
                    attachments = IndexOf(row, "Attachments");
                    if (id < 0)
                    {
                        diagnostics.UnreadableFiles.Add((entry.Path, "no ID column"));
                        yield break;
                    }
                    continue;
                }
                yield return (Cell(row, id), Cell(row, contents), Cell(row, attachments));
            }
        }

        private static int IndexOf(IReadOnlyList<string> row, string name)
        {
            for (int i = 0; i < row.Count; i++)
                if (row[i].Trim('﻿', ' ').Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static string? Cell(IReadOnlyList<string> row, int index) => index >= 0 && index < row.Count ? row[index] : null;

        private static string? FileNameOf(string url)
        {
            int query = url.IndexOf('?');
            string path = query < 0 ? url : url[..query];
            string name = path[(path.LastIndexOf('/') + 1)..];
            return name.Length == 0 ? null : name;
        }

        private static string? Scalar(JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.String => string.IsNullOrEmpty(v.GetString()) ? null : v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            };
        }
    }

    /// <summary>RFC 4180 reading: quoted fields, doubled quotes, and line breaks inside quotes.</summary>
    public static class Csv
    {
        public static IEnumerable<IReadOnlyList<string>> Read(TextReader reader, char separator)
        {
            var row = new List<string>();
            var field = new StringBuilder();
            bool quoted = false, any = false;
            int ch;
            while ((ch = reader.Read()) >= 0)
            {
                char c = (char)ch;
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (reader.Peek() == '"') { field.Append('"'); reader.Read(); }
                        else quoted = false;
                    }
                    else field.Append(c);
                    continue;
                }
                if (c == '"' && field.Length == 0) { quoted = true; any = true; }
                else if (c == separator) { row.Add(field.ToString()); field.Clear(); any = true; }
                else if (c == '\n' || c == '\r')
                {
                    if (c == '\r' && reader.Peek() == '\n') reader.Read();
                    if (any || field.Length > 0)
                    {
                        row.Add(field.ToString());
                        yield return row;
                        row = new List<string>();
                    }
                    field.Clear();
                    any = false;
                }
                else { field.Append(c); any = true; }
            }
            if (any || field.Length > 0)
            {
                row.Add(field.ToString());
                yield return row;
            }
        }
    }
}
