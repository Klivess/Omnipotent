using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Omnipotent.Services.OmniscienceV3.Storage.Legacy;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Ingest.Imports
{
    /// <summary>
    /// Instagram and Messenger "Download your information" exports (JSON). Both use one layout:
    /// <c>…/messages/&lt;folder&gt;/&lt;thread&gt;/message_N.json</c>, newest message first, <c>message_1</c> the newest file.
    /// <list type="bullet">
    /// <item><b>Text</b> is UTF-8 written as Latin-1 characters; every string is repaired (see <see cref="TextDecoding.RepairMojibake"/>).</item>
    /// <item><b>Time</b> is <c>timestamp_ms</c>: an instant, no zone needed.</item>
    /// <item><b>Identity</b> is a display name only. The same name in two threads may be two people, so a sender is
    /// scoped to its thread until Klives links them; the account owner is one identity across all threads.</item>
    /// <item><b>Ids</b> do not exist; see <see cref="ImportIds.Message"/>.</item>
    /// </list>
    /// </summary>
    public sealed class MetaImport : IImportFormat
    {
        private static readonly Regex MessageFile = new(@"(^|/)messages/(?<box>[^/]+)/(?<thread>[^/]+)/message_(?<n>\d+)\.json$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly string platform;

        /// <param name="platform">"instagram" or "messenger".</param>
        public MetaImport(string platform) => this.platform = platform;

        public string Id => platform;
        public string Title => platform == "instagram" ? "Instagram data download (JSON)" : "Messenger / Facebook data download (JSON)";
        public bool NeedsZone => false;

        public double Detect(ImportInput input)
        {
            var files = ThreadFiles(input);
            if (files.Count == 0) return 0;
            bool instagramPath = input.Entries.Any(e => e.Path.Contains("instagram", StringComparison.OrdinalIgnoreCase)) || input.Name.Contains("instagram", StringComparison.OrdinalIgnoreCase);
            bool facebookPath = input.Entries.Any(e => e.Path.Contains("facebook", StringComparison.OrdinalIgnoreCase)) || input.Name.Contains("facebook", StringComparison.OrdinalIgnoreCase)
                                || input.Name.Contains("messenger", StringComparison.OrdinalIgnoreCase);
            if (instagramPath != facebookPath) return (platform == "instagram") == instagramPath ? 0.9 : 0.1;
            // The path does not say. Messenger threads carry a thread_type; Instagram's do not.
            bool hasThreadType;
            using (var doc = JsonDocument.Parse(input.ReadText(files[0].Entry)))
                hasThreadType = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("thread_type", out _);
            return (platform == "messenger") == hasThreadType ? 0.75 : 0.35;
        }

        private static List<(ImportEntry Entry, string Thread, int Number)> ThreadFiles(ImportInput input) =>
            input.Entries.Select(e => (Entry: e, Match: MessageFile.Match(e.Path))).Where(x => x.Match.Success)
                .Select(x => (x.Entry, Thread: x.Match.Groups["box"].Value + "/" + x.Match.Groups["thread"].Value,
                    Number: int.Parse(x.Match.Groups["n"].Value, CultureInfo.InvariantCulture)))
                .ToList();

        public IEnumerable<IngestMessage> Read(ImportInput input, ImportOptions options, ImportDiagnostics diagnostics)
        {
            var files = ThreadFiles(input);
            if (files.Count == 0) throw new InvalidDataException("No message_N.json files were found under a messages folder.");
            string? owner = options.OwnerName?.Trim() ?? DetectOwner(input, files, diagnostics);
            diagnostics.OwnerDetected = owner;
            if (owner == null) diagnostics.Warn("The account owner could not be told from the export, so no message was marked as Klives's own. Name the owner and import again.");

            foreach (var thread in files.GroupBy(f => f.Thread, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                // message_1 is the newest file and each file is newest-first: read backwards for chronological order.
                var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
                long order = 0;
                foreach (var file in thread.OrderByDescending(f => f.Number))
                {
                    JsonDocument doc;
                    try
                    {
                        doc = JsonDocument.Parse(input.ReadText(file.Entry));
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
                    {
                        diagnostics.UnreadableFiles.Add((file.Entry.Path, ex.Message));
                        continue;
                    }
                    using (doc)
                    {
                        var root = doc.RootElement;
                        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
                        {
                            diagnostics.UnreadableFiles.Add((file.Entry.Path, "no messages array"));
                            continue;
                        }
                        string threadPath = Str(root, "thread_path", diagnostics) ?? thread.Key;
                        string threadKey = ImportIds.Key(threadPath);
                        string? title = options.ConversationTitle ?? Str(root, "title", diagnostics);
                        var participants = root.TryGetProperty("participants", out var ps) && ps.ValueKind == JsonValueKind.Array
                            ? ps.EnumerateArray().Select(p => Str(p, "name", diagnostics)).OfType<string>().ToList()
                            : new List<string>();
                        string kind = participants.Count > 2 || Str(root, "thread_type", null) == "RegularGroup" ? ConversationKinds.GroupDm : ConversationKinds.Dm;
                        var conversation = new IngestConversation(platform, "thread:" + threadKey, kind, title)
                        {
                            Members = participants.Select(p => Identity(p, threadKey, owner)).ToList(),
                        };

                        foreach (var e in messages.EnumerateArray().Reverse())
                        {
                            var message = Message(e, conversation, threadKey, owner, input, file.Entry.Path, occurrences, order++, diagnostics);
                            if (message != null) yield return message;
                        }
                    }
                }
            }
        }

        /// <summary>The owner is the one name present in every thread (needs at least two threads to tell).</summary>
        private static string? DetectOwner(ImportInput input, List<(ImportEntry Entry, string Thread, int Number)> files, ImportDiagnostics diagnostics)
        {
            var firstFiles = files.GroupBy(f => f.Thread, StringComparer.Ordinal).Select(g => g.OrderBy(f => f.Number).First()).Take(200).ToList();
            if (firstFiles.Count < 2) return null;
            Dictionary<string, int> counts = new(StringComparer.Ordinal);
            int threads = 0;
            foreach (var file in firstFiles)
            {
                try
                {
                    using var doc = JsonDocument.Parse(input.ReadText(file.Entry));
                    if (!doc.RootElement.TryGetProperty("participants", out var ps) || ps.ValueKind != JsonValueKind.Array) continue;
                    threads++;
                    foreach (var name in ps.EnumerateArray().Select(p => Str(p, "name", null)).OfType<string>().Distinct(StringComparer.Ordinal))
                        counts[name] = counts.GetValueOrDefault(name) + 1;
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
                {
                    diagnostics.Warn($"{file.Entry.Path} could not be read while looking for the account owner: {ex.Message}");
                }
            }
            var everywhere = counts.Where(c => c.Value == threads).Select(c => c.Key).ToList();
            return threads >= 2 && everywhere.Count == 1 ? everywhere[0] : null;
        }

        private IngestIdentity Identity(string name, string threadKey, string? owner) =>
            name == owner
                ? new IngestIdentity(platform, "self:" + ImportIds.Key(name), DisplayName: name, IsSelf: true)
                : new IngestIdentity(platform, $"thread:{threadKey}:{ImportIds.Key(name)}", DisplayName: name);

        private IngestMessage? Message(JsonElement e, IngestConversation conversation, string threadKey, string? owner, ImportInput input, string filePath,
            Dictionary<string, int> occurrences, long order, ImportDiagnostics diagnostics)
        {
            if (e.ValueKind != JsonValueKind.Object) { diagnostics.SkippedRecords++; return null; }
            string? sender = Str(e, "sender_name", diagnostics);
            if (!e.TryGetProperty("timestamp_ms", out var ts) || !ts.TryGetInt64(out long sent) || sender == null)
            {
                diagnostics.SkippedRecords++;
                diagnostics.Warn($"A message in {filePath} has no sender or timestamp and was skipped.");
                return null;
            }

            string? content = Str(e, "content", diagnostics);
            bool unsent = e.TryGetProperty("is_unsent", out var u) && u.ValueKind == JsonValueKind.True;
            // The app writes this line itself when someone taps like on a message.
            bool system = content == "Liked a message";
            if (system) diagnostics.SystemLines++;

            var attachments = new List<IngestAttachment>();
            foreach (var (property, kind) in new[] { ("photos", MediaKinds.Image), ("videos", MediaKinds.Video), ("audio_files", MediaKinds.Audio), ("gifs", MediaKinds.Image), ("files", MediaKinds.File) })
            {
                if (!e.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in list.EnumerateArray())
                    if (Str(item, "uri", null) is { } uri) attachments.Add(Attachment(uri, kind, input, diagnostics));
            }
            if (e.TryGetProperty("sticker", out var sticker) && Str(sticker, "uri", null) is { } stickerUri)
                attachments.Add(Attachment(stickerUri, MediaKinds.Image, input, diagnostics));

            var embeds = new List<RawEmbed>();
            if (e.TryGetProperty("share", out var share) && share.ValueKind == JsonValueKind.Object)
                embeds.Add(new RawEmbed("share", Str(share, "original_content_owner", diagnostics), Str(share, "link", null), null, Str(share, "share_text", diagnostics), share.GetRawText()));

            var reactions = new List<IngestReaction>();
            if (e.TryGetProperty("reactions", out var rs) && rs.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in rs.EnumerateArray())
                {
                    string? emoji = Str(r, "reaction", diagnostics);
                    string? actor = Str(r, "actor", diagnostics);
                    if (emoji == null || actor == null) continue;
                    long? at = r.TryGetProperty("timestamp", out var rt) && rt.TryGetInt64(out long seconds) && seconds > 0 ? seconds * 1000 : null;
                    reactions.Add(new IngestReaction(emoji, emoji, Identity(actor, threadKey, owner), at));
                }
            }

            string stamp = sent.ToString(CultureInfo.InvariantCulture);
            string idKey = $"{sender}\u0001{stamp}\u0001{content}";
            int occurrence = occurrences.GetValueOrDefault(idKey);
            occurrences[idKey] = occurrence + 1;

            return new IngestMessage
            {
                PlatformMessageId = ImportIds.Message(threadKey, sender, stamp, content, occurrence),
                Conversation = conversation,
                Author = Identity(sender, threadKey, owner),
                SentAtMs = sent,
                Quality = TimeQuality.Exact,
                OrderKey = order,
                Content = unsent ? null : content,
                DeletedAtMs = unsent ? sent : null,
                IsSystem = system,
                Attachments = attachments,
                Embeds = embeds,
                Reactions = reactions,
                RawJson = e.GetRawText(),
            };
        }

        /// <summary>Media URIs are paths from the export root; links to the web are kept as links.</summary>
        private static IngestAttachment Attachment(string uri, string kind, ImportInput input, ImportDiagnostics diagnostics)
        {
            string name = uri[(uri.LastIndexOf('/') + 1)..];
            if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return new IngestAttachment(name, MediaKind: kind, SourceUrl: uri);
            // The export may be rooted one level above the path the URI assumes.
            var entry = input.Find(uri) ?? input.Entries.FirstOrDefault(x => x.Path.EndsWith("/" + uri, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                diagnostics.MediaMissing++;
                return new IngestAttachment(name, MediaKind: kind, Unavailable: true);
            }
            diagnostics.MediaFound++;
            return new IngestAttachment(name, MediaKind: kind, SizeBytes: entry.Length, LocalFile: () => input.OpenEntry(entry));
        }

        private static string? Str(JsonElement e, string name, ImportDiagnostics? diagnostics)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
            string text = TextDecoding.RepairMojibake(v.GetString() ?? "", out bool repaired);
            if (repaired && diagnostics != null) diagnostics.RepairedText++;
            return text.Length == 0 ? null : text;
        }
    }
}
