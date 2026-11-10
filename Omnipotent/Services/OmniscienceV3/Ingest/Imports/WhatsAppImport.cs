using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Omnipotent.Services.OmniscienceV3.Storage.Legacy;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Ingest.Imports
{
    /// <summary>Flags v3 itself sets in <c>messages.flags</c>, above the range platforms use.</summary>
    public static class IngestFlags
    {
        /// <summary>The source says the message was edited but not when.</summary>
        public const long EditedAtUnknownTime = 1L << 40;
    }

    /// <summary>
    /// WhatsApp "Export chat" text files, both dialects (plan I-Importers):
    /// <code>
    /// Android:  31/12/2020, 23:59 - Ada: happy new year
    /// iOS:      [31/12/2020, 23:59:41] Ada: happy new year
    /// </code>
    /// What the format leaves open, and how it is settled:
    /// <list type="bullet">
    /// <item><b>Zone.</b> Times are wall-clock readings of the exporting phone with no zone. The zone is chosen per
    /// import; every time is stored as <see cref="TimeQuality.InferredZone"/>. An hour that occurs twice (autumn) is
    /// resolved by message order, one that does not exist (spring) moves forward; both are counted.</item>
    /// <item><b>Date order.</b> 03/04/2021 is March or April depending on the phone's locale. If any date in the
    /// file has a component above 12 the order is certain; otherwise it must be chosen.</item>
    /// <item><b>Identity.</b> Senders are the names in Klives's address book, or phone numbers. A phone number is a
    /// stable id; a name identifies the same contact across chats.</item>
    /// <item><b>Ids.</b> None exist; see <see cref="ImportIds.Message"/>.</item>
    /// </list>
    /// Lines the app wrote rather than a person (encryption notice, joins, "media omitted") are kept as system
    /// messages or attachments, never as something someone said.
    /// </summary>
    public sealed class WhatsAppImport : IImportFormat
    {
        public const string Platform = "whatsapp";

        public string Id => "whatsapp";
        public string Title => "WhatsApp chat export";
        public bool NeedsZone => true;

        // [31/12/2020, 23:59:41]  |  31/12/2020, 23:59 -   (separators / . -, optional seconds, optional am/pm with any space before it)
        private static readonly Regex Header = new(
            @"^[\u200E\u200F\u202A-\u202E\uFEFF]*(?<open>\[)?(?<a>\d{1,4})[./-](?<b>\d{1,2})[./-](?<c>\d{1,4}),?[ \u00A0\u202F]+" +
            @"(?<h>\d{1,2})[:.](?<mi>\d{2})(?:[:.](?<s>\d{2}))?(?:[ \u00A0\u202F]*(?<ap>[AaPp])\.?[ \u00A0\u202F]?[Mm]\.?)?" +
            @"(?(open)\][ \u00A0]?|[ \u00A0]+[-–][ \u00A0]+)(?<rest>.*)$",
            RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex AttachedIos = new(@"^[\u200E\u200F]*<attached: (?<f>[^>]+)>$", RegexOptions.Compiled);
        private static readonly Regex AttachedAndroid = new(@"^[\u200E\u200F]*(?<f>[^\r\n<>:""/\\|?*]+\.[A-Za-z0-9]{2,5}) \(file attached\)$", RegexOptions.Compiled);
        private static readonly Regex OmittedIos = new(@"^[\u200E\u200F]*(?<k>image|video|audio|sticker|GIF|document|Contact card) omitted$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex PhoneNumber = new(@"^[\u202A-\u202E\u200E\u200F]*\+[\d \u00A0()\-]{6,}[\u202A-\u202E\u200E\u200F]*$", RegexOptions.Compiled);
        private static readonly Regex FileTitleAndroid = new(@"^WhatsApp Chat with (?<t>.+?)(?:\.txt)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex FileTitleIos = new(@"^WhatsApp Chat - (?<t>.+?)(?:\.zip)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private const string MediaOmitted = "<Media omitted>";
        private const string EditedMarker = "<This message was edited>";
        private static readonly string[] DeletedTexts = { "This message was deleted", "You deleted this message", "This message was deleted." };

        private sealed class RawLine
        {
            public required int A, B, C, Hour, Minute;
            public int? Second;
            public char? AmPm;
            public required string Stamp;
            public string? Sender;
            public required StringBuilder Text;
        }

        public double Detect(ImportInput input)
        {
            var chat = FindChatFile(input);
            if (chat == null) return 0;
            string head;
            using (var s = input.OpenEntry(chat))
            {
                byte[] buffer = new byte[8192];
                int read = s.Read(buffer, 0, buffer.Length);
                head = TextDecoding.Decode(buffer.AsSpan(0, read & ~1));
            }
            var lines = head.Split('\n').Take(40).ToList();
            int headers = lines.Count(l => Header.IsMatch(l.TrimEnd('\r')));
            if (headers == 0) return 0;
            bool named = FileTitleAndroid.IsMatch(chat.FileName) || chat.FileName.Equals("_chat.txt", StringComparison.OrdinalIgnoreCase);
            return headers >= Math.Max(1, lines.Count / 3) ? (named ? 0.95 : 0.7) : 0.3;
        }

        private static ImportEntry? FindChatFile(ImportInput input)
        {
            var texts = input.Entries.Where(e => e.Path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)).ToList();
            return texts.FirstOrDefault(e => e.FileName.Equals("_chat.txt", StringComparison.OrdinalIgnoreCase))
                   ?? texts.FirstOrDefault(e => FileTitleAndroid.IsMatch(e.FileName))
                   ?? (texts.Count == 1 ? texts[0] : null);
        }

        public IEnumerable<IngestMessage> Read(ImportInput input, ImportOptions options, ImportDiagnostics diagnostics)
        {
            var chat = FindChatFile(input) ?? throw new InvalidDataException("No WhatsApp chat text file was found in this export.");
            var zone = Zones.GetOrThrow(options.Zone ?? throw new ImportDecisionNeededException("WhatsApp exports carry no time zone: choose the zone the exporting phone was in."));
            diagnostics.ZoneUsed = options.Zone;

            var raw = Parse(input.ReadText(chat));
            if (raw.Count == 0) yield break;

            string order = DateOrder(raw);
            diagnostics.DateOrder = order;
            bool dayFirst = order switch
            {
                "day-first" => true,
                "month-first" or "year-first" => false,
                _ => options.DayFirst ?? throw new ImportDecisionNeededException(
                    "Every date in this export is ambiguous (no day above 12): choose whether the day or the month comes first."),
            };

            // Wall-clock readings in file order, then resolved together so an autumn hour stays in order.
            var wall = new List<DateTime>(raw.Count);
            var usable = new List<RawLine>(raw.Count);
            foreach (var line in raw)
            {
                if (ToWallClock(line, order == "year-first", dayFirst) is { } t) { wall.Add(t); usable.Add(line); }
                else { diagnostics.SkippedRecords++; diagnostics.Warn($"A line has a date that does not exist ({line.Stamp}); it was skipped."); }
            }
            var resolved = Zones.ResolveSequence(wall, zone);

            string title = options.ConversationTitle ?? TitleOf(input, chat)
                ?? throw new ImportDecisionNeededException(
                    "This export does not say which chat it is (an iPhone export's text file is always called _chat.txt): name the conversation.");
            string conversationKey = "chat:" + ImportIds.Key(title);
            var senders = usable.Where(l => !IsSystem(l)).Select(l => CleanName(l.Sender!)).Distinct(StringComparer.Ordinal).ToList();
            // A chat with one other person is titled with that person's name. More than two writers, or two writers
            // neither of whom the chat is named after, is a group. (A group in which only two people ever wrote and
            // which is named after one of them cannot be told apart from a direct chat by the export alone.)
            string cleanTitle = CleanName(title);
            bool group = senders.Count > 2 || (senders.Count == 2 && !senders.Contains(cleanTitle, StringComparer.OrdinalIgnoreCase));
            string conversationKind = options.ConversationKind switch
            {
                ConversationKinds.Dm => ConversationKinds.Dm,
                ConversationKinds.GroupDm => ConversationKinds.GroupDm,
                null => group ? ConversationKinds.GroupDm : ConversationKinds.Dm,
                _ => throw new ImportDecisionNeededException("A WhatsApp chat is either \"dm\" or \"group_dm\"."),
            };
            var conversation = new IngestConversation(Platform, conversationKey, conversationKind, title);
            string? owner = options.OwnerName == null ? null : CleanName(options.OwnerName);
            if (owner != null && !senders.Contains(owner, StringComparer.Ordinal))
                diagnostics.Warn($"Nobody in this chat is named \"{owner}\", so no message was marked as Klives's own.");
            diagnostics.OwnerDetected = owner != null && senders.Contains(owner, StringComparer.Ordinal) ? owner : null;
            var systemIdentity = new IngestIdentity(Platform, "system", DisplayName: "WhatsApp");

            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < usable.Count; i++)
            {
                var line = usable[i];
                var at = resolved[i];
                if (at.WasAmbiguous) diagnostics.AmbiguousTimes++;
                if (at.WasInvalid) diagnostics.InvalidTimes++;

                string text = line.Text.ToString().Trim('\u200E', '\u200F', '\uFEFF');
                bool system = IsSystem(line);
                string senderName = line.Sender == null ? "" : CleanName(line.Sender);

                long flags = 0;
                long? deleted = null;
                var attachments = new List<IngestAttachment>();
                string? content = text;

                if (content.EndsWith(EditedMarker, StringComparison.Ordinal))
                {
                    content = content[..^EditedMarker.Length].TrimEnd(' ', '\u200E');
                    flags |= IngestFlags.EditedAtUnknownTime;
                }
                if (DeletedTexts.Contains(content, StringComparer.Ordinal))
                {
                    deleted = at.UtcMs;
                    content = null;
                }
                else if (!system)
                {
                    // A media marker is the first line; a caption may follow on the next lines.
                    int newline = content.IndexOf('\n');
                    string first = newline < 0 ? content : content[..newline];
                    string? rest = NullIfEmpty(newline < 0 ? "" : content[(newline + 1)..]);
                    var attached = AttachedIos.Match(first);
                    if (!attached.Success) attached = AttachedAndroid.Match(first);
                    var omitted = OmittedIos.Match(first);
                    if (attached.Success)
                    {
                        attachments.Add(Attachment(input, attached.Groups["f"].Value.Trim(), diagnostics));
                        content = rest;
                    }
                    else if (first == MediaOmitted || omitted.Success)
                    {
                        // Exported without media: the message had an attachment that this export does not contain.
                        string? kind = !omitted.Success ? null : omitted.Groups["k"].Value.ToLowerInvariant() switch
                        {
                            "image" or "sticker" or "gif" => MediaKinds.Image,
                            "video" => MediaKinds.Video,
                            "audio" => MediaKinds.Voice,
                            _ => MediaKinds.File,
                        };
                        attachments.Add(new IngestAttachment(null, MediaKind: kind, Unavailable: true));
                        diagnostics.MediaMissing++;
                        content = rest;
                    }
                }
                if (system) diagnostics.SystemLines++;

                string idKey = $"{senderName}\u0001{line.Stamp}\u0001{text}";
                int occurrence = occurrences.GetValueOrDefault(idKey);
                occurrences[idKey] = occurrence + 1;

                var author = system
                    ? systemIdentity
                    : new IngestIdentity(Platform, UserIdOf(senderName, conversationKey, owner), DisplayName: IsPhone(senderName) ? null : senderName,
                        Username: IsPhone(senderName) ? NormalisePhone(senderName) : null, IsSelf: owner != null && senderName == owner);

                yield return new IngestMessage
                {
                    PlatformMessageId = ImportIds.Message(conversationKey, senderName, line.Stamp, text, occurrence),
                    Conversation = conversation,
                    Author = author,
                    SentAtMs = at.UtcMs,
                    Quality = TimeQuality.InferredZone,
                    OrderKey = i,
                    Content = content,
                    DeletedAtMs = deleted,
                    IsSystem = system,
                    Flags = flags,
                    Attachments = attachments,
                };
            }
        }

        /// <summary>
        /// Whether the app wrote the line rather than a person. Android writes its notices with no sender. iOS gives
        /// every line a sender (the group's name, or the person an event is about) and marks text it generated with
        /// U+200E; of those marked lines, media markers and deletion notices still stand for something the sender did,
        /// and everything else (joins, renames, calls, the encryption notice) is the app speaking.
        /// </summary>
        private static bool IsSystem(RawLine line)
        {
            if (line.Sender == null) return true;
            if (line.Text.Length == 0 || line.Text[0] is not ('\u200E' or '\u200F')) return false;
            string text = line.Text.ToString().Trim('\u200E', '\u200F', '\uFEFF');
            if (text.EndsWith(EditedMarker, StringComparison.Ordinal)) text = text[..^EditedMarker.Length].TrimEnd(' ', '\u200E');
            int newline = text.IndexOf('\n');
            string first = newline < 0 ? text : text[..newline];
            return !(first == MediaOmitted || OmittedIos.IsMatch(first) || AttachedIos.IsMatch(first) || AttachedAndroid.IsMatch(first)
                     || DeletedTexts.Contains(text, StringComparer.Ordinal));
        }

        private static List<RawLine> Parse(string text)
        {
            var result = new List<RawLine>();
            RawLine? current = null;
            DateTime? previous = null;
            foreach (var rawLine in text.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                var m = Header.Match(line);
                if (m.Success)
                {
                    var candidate = new RawLine
                    {
                        A = int.Parse(m.Groups["a"].Value, CultureInfo.InvariantCulture),
                        B = int.Parse(m.Groups["b"].Value, CultureInfo.InvariantCulture),
                        C = int.Parse(m.Groups["c"].Value, CultureInfo.InvariantCulture),
                        Hour = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture),
                        Minute = int.Parse(m.Groups["mi"].Value, CultureInfo.InvariantCulture),
                        Second = m.Groups["s"].Success ? int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture) : null,
                        AmPm = m.Groups["ap"].Success ? char.ToUpperInvariant(m.Groups["ap"].Value[0]) : null,
                        Stamp = line[..m.Groups["rest"].Index].Trim('\u200E', '\u200F', '\uFEFF', ' ', '\u00A0'),
                        Text = new StringBuilder(),
                    };
                    // A pasted chat log inside a message looks like headers too, but its dates run far behind the
                    // conversation's. Either reading order is tried; a line more than 36 hours before the previous
                    // message in both is text, not a new message.
                    var asDayFirst = ToWallClock(candidate, false, true);
                    var asMonthFirst = ToWallClock(candidate, candidate.A > 31, false);
                    bool behind = previous != null && current != null
                                  && (asDayFirst == null || asDayFirst < previous.Value.AddHours(-36))
                                  && (asMonthFirst == null || asMonthFirst < previous.Value.AddHours(-36));
                    if (!behind)
                    {
                        string rest = m.Groups["rest"].Value;
                        int colon = rest.IndexOf(": ", StringComparison.Ordinal);
                        // "Name: text". A line without a sender is an app notice (Android writes those bare).
                        if (colon > 0 && colon <= 80 && !rest[..colon].Contains('\n'))
                        {
                            candidate.Sender = rest[..colon];
                            candidate.Text.Append(rest[(colon + 2)..]);
                        }
                        else if (rest.EndsWith(':') && rest.Length <= 81)
                        {
                            candidate.Sender = rest[..^1];
                        }
                        else
                        {
                            candidate.Text.Append(rest);
                        }
                        result.Add(candidate);
                        current = candidate;
                        previous = Max(asDayFirst, asMonthFirst) ?? previous;
                        continue;
                    }
                }
                // Not a header: the previous message continues on this line.
                if (current != null) current.Text.Append('\n').Append(line);
            }
            foreach (var r in result)
            {
                // Trailing blank lines belong to the file, not the message.
                while (r.Text.Length > 0 && r.Text[^1] is '\n' or '\r') r.Text.Length--;
            }
            return result;
        }

        private static DateTime? Max(DateTime? a, DateTime? b) => a == null ? b : b == null ? a : a > b ? a : b;

        /// <summary>Which of day/month/year comes first, when the file itself proves it.</summary>
        private static string DateOrder(List<RawLine> lines)
        {
            if (lines.Any(l => l.A > 31)) return "year-first";
            bool dayFirst = lines.Any(l => l.A > 12), monthFirst = lines.Any(l => l.B > 12);
            if (dayFirst && !monthFirst) return "day-first";
            if (monthFirst && !dayFirst) return "month-first";
            return "ambiguous";
        }

        private static DateTime? ToWallClock(RawLine l, bool yearFirst, bool dayFirst)
        {
            int year, month, day;
            if (yearFirst) (year, month, day) = (l.A, l.B, l.C);
            else if (dayFirst) (day, month, year) = (l.A, l.B, l.C);
            else (month, day, year) = (l.A, l.B, l.C);
            // WhatsApp launched in 2009: a two-digit year is always in this century.
            if (year < 100) year += 2000;
            int hour = l.Hour;
            if (l.AmPm == 'P' && hour < 12) hour += 12;
            else if (l.AmPm == 'A' && hour == 12) hour = 0;
            if (year is < 2000 or > 2100 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || l.Minute > 59 || l.Second > 59) return null;
            return new DateTime(year, month, day, hour, l.Minute, l.Second ?? 0, DateTimeKind.Unspecified);
        }

        private static IngestAttachment Attachment(ImportInput input, string fileName, ImportDiagnostics diagnostics)
        {
            var entry = input.FindByName(fileName);
            if (entry == null)
            {
                diagnostics.MediaMissing++;
                return new IngestAttachment(fileName, Unavailable: true);
            }
            diagnostics.MediaFound++;
            return new IngestAttachment(fileName, SizeBytes: entry.Length, LocalFile: () => input.OpenEntry(entry));
        }

        /// <summary>
        /// The chat's name, from where the export carries it: Android names the text file after the chat, iOS names
        /// the zip. An iOS text file on its own is always "_chat.txt" and names nothing — null, so the caller asks,
        /// rather than filing every such export under one conversation.
        /// </summary>
        private static string? TitleOf(ImportInput input, ImportEntry chat)
        {
            var android = FileTitleAndroid.Match(chat.FileName);
            if (android.Success) return android.Groups["t"].Value.Trim();
            var ios = FileTitleIos.Match(input.Name);
            if (ios.Success) return ios.Groups["t"].Value.Trim();
            bool anonymous = chat.FileName.Equals("_chat.txt", StringComparison.OrdinalIgnoreCase);
            string name = Path.GetFileNameWithoutExtension(anonymous ? input.Name : chat.FileName);
            if (string.IsNullOrWhiteSpace(name) || name.Equals("_chat", StringComparison.OrdinalIgnoreCase)) return null;
            return name;
        }

        private static string CleanName(string name) => name.Trim('\u200E', '\u200F', '\u202A', '\u202B', '\u202C', '\u202D', '\u202E', '\uFEFF', ' ', '\u00A0', '~').Normalize(NormalizationForm.FormC);
        private static bool IsPhone(string name) => PhoneNumber.IsMatch(name);
        private static string NormalisePhone(string name) => "+" + new string(name.Where(char.IsAsciiDigit).ToArray());
        /// <summary>
        /// A phone number is the account itself. A name is only how Klives's phone labelled someone in this export,
        /// so it identifies a person within this chat; the same name in another chat is linked by Klives (or by the
        /// identity-link suggestions), never assumed. The owner is the same account in every chat.
        /// </summary>
        private static string UserIdOf(string name, string conversationKey, string? owner)
        {
            if (IsPhone(name)) return NormalisePhone(name);
            if (owner != null && name == owner) return "self:" + ImportIds.Key(name);
            return $"name:{conversationKey["chat:".Length..]}:{ImportIds.Key(name)}";
        }
        private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
    }
}
