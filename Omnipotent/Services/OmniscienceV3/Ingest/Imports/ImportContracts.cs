using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Omnipotent.Services.OmniscienceV3.Ingest.Imports
{
    /// <summary>One file of an export, by its path inside it (forward slashes).</summary>
    public sealed record ImportEntry(string Path, long Length)
    {
        public string FileName => Path[(Path.LastIndexOf('/') + 1)..];
    }

    /// <summary>
    /// An export as Klives has it: a single file, a zip, or an unpacked folder. Importers read entries by path
    /// and never care which. Nothing is ever extracted to disk under a name taken from the archive (media goes
    /// into the content-addressed store), so a hostile archive cannot write outside it.
    /// </summary>
    public sealed class ImportInput : IDisposable
    {
        /// <summary>Text and JSON entries larger than this are refused rather than read (a decompression bomb, or not a chat export).</summary>
        public const long MaxTextEntryBytes = 2L * 1024 * 1024 * 1024;

        private readonly string path;
        private readonly ZipArchive? zip;
        private readonly Dictionary<string, ImportEntry> byPath;
        private readonly ILookup<string, ImportEntry> byName;

        private ImportInput(string path, string name, ZipArchive? zip, List<ImportEntry> entries)
        {
            this.path = path;
            this.zip = zip;
            Name = name;
            Entries = entries;
            byPath = new Dictionary<string, ImportEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries) byPath.TryAdd(e.Path, e);
            byName = entries.ToLookup(e => e.FileName, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>File or folder name, without a directory.</summary>
        public string Name { get; }
        public IReadOnlyList<ImportEntry> Entries { get; }
        public bool IsDirectory => zip == null && Directory.Exists(path);

        public static ImportInput Open(string path)
        {
            string full = Path.GetFullPath(path);
            if (Directory.Exists(full))
            {
                var entries = Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
                    .Select(f => new ImportEntry(Path.GetRelativePath(full, f).Replace('\\', '/'), new FileInfo(f).Length))
                    .OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
                return new ImportInput(full, Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), null, entries);
            }
            if (!File.Exists(full)) throw new FileNotFoundException("The export was not found.", full);

            if (IsZip(full))
            {
                var archive = ZipFile.OpenRead(full);
                var entries = archive.Entries.Where(e => !e.FullName.EndsWith('/'))
                    .Select(e => new ImportEntry(e.FullName.Replace('\\', '/'), e.Length))
                    .OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
                return new ImportInput(full, Path.GetFileName(full), archive, entries);
            }
            return new ImportInput(full, Path.GetFileName(full), null, new List<ImportEntry> { new(Path.GetFileName(full), new FileInfo(full).Length) });
        }

        private static bool IsZip(string file)
        {
            using var f = File.OpenRead(file);
            Span<byte> head = stackalloc byte[4];
            return f.Read(head) == 4 && head[0] == 0x50 && head[1] == 0x4B && head[2] == 0x03 && head[3] == 0x04;
        }

        public ImportEntry? Find(string relativePath) => byPath.GetValueOrDefault(relativePath.Replace('\\', '/'));

        /// <summary>An entry by file name alone, when exactly one has it (exports name media by file name, not path).</summary>
        public ImportEntry? FindByName(string fileName)
        {
            var matches = byName[fileName].Take(2).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        public Stream OpenEntry(ImportEntry entry)
        {
            if (zip != null)
                return (zip.GetEntry(entry.Path) ?? zip.Entries.First(e => string.Equals(e.FullName.Replace('\\', '/'), entry.Path, StringComparison.OrdinalIgnoreCase))).Open();
            string file = IsDirectory ? Path.Combine(path, entry.Path.Replace('/', Path.DirectorySeparatorChar)) : path;
            return new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
        }

        /// <summary>All of a text entry, decoded (BOMs honoured, UTF-16 detected).</summary>
        public string ReadText(ImportEntry entry)
        {
            if (entry.Length > MaxTextEntryBytes) throw new InvalidDataException($"{entry.Path} is {entry.Length / (1024 * 1024)} MB of text: too large to be a chat export.");
            using var s = OpenEntry(entry);
            using var buffer = new MemoryStream();
            s.CopyTo(buffer);
            return TextDecoding.Decode(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        }

        /// <summary>
        /// Identifies this export in the import ledger. A file is hashed whole. A folder is hashed by its listing
        /// (paths and sizes) plus the contents of its text files — the parts that carry messages — so a folder of
        /// gigabytes of media is not read twice.
        /// </summary>
        public string ContentSha256()
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if (!IsDirectory)
            {
                using var f = File.OpenRead(path);
                byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
                try
                {
                    int read;
                    while ((read = f.Read(buffer, 0, buffer.Length)) > 0) hash.AppendData(buffer, 0, read);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            else
            {
                foreach (var e in Entries)
                {
                    hash.AppendData(Encoding.UTF8.GetBytes($"{e.Path}|{e.Length}\n"));
                    string ext = Path.GetExtension(e.Path).ToLowerInvariant();
                    if (ext is not (".json" or ".txt" or ".csv" or ".tsv" or ".html")) continue;
                    using var s = OpenEntry(e);
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
                    try
                    {
                        int read;
                        while ((read = s.Read(buffer, 0, buffer.Length)) > 0) hash.AppendData(buffer, 0, read);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        public void Dispose() => zip?.Dispose();
    }

    /// <summary>What Klives decides before an import is committed.</summary>
    [Api.ApiContract]
    public sealed record ImportOptions
    {
        /// <summary>IANA zone for formats that give wall-clock times without one (WhatsApp, Telegram without unix times, tables).</summary>
        public string? Zone { get; init; }
        /// <summary>Day before month in numeric dates, when the file itself never settles it.</summary>
        public bool? DayFirst { get; init; }
        /// <summary>Which sender is Klives, for formats that only give names.</summary>
        public string? OwnerName { get; init; }
        /// <summary>Overrides the conversation title taken from the file name.</summary>
        public string? ConversationTitle { get; init; }
        /// <summary>"dm" or "group_dm", for formats that cannot tell (a WhatsApp export does not say which it is).</summary>
        public string? ConversationKind { get; init; }
        /// <summary>Generic tables: the platform the rows are filed under (default "import").</summary>
        public string? Platform { get; init; }
        /// <summary>Generic tables: field → column name, for columns the header does not name conventionally.</summary>
        public IReadOnlyDictionary<string, string>? Columns { get; init; }
    }

    /// <summary>What reading an export found besides its messages. Filled as the messages are enumerated.</summary>
    public sealed class ImportDiagnostics
    {
        private const int MaxWarnings = 200;
        private readonly List<string> warnings = new();
        private readonly HashSet<string> seen = new(StringComparer.Ordinal);

        public IReadOnlyList<string> Warnings => warnings;
        /// <summary>Files that could not be read, with the reason. The rest of the export is still imported.</summary>
        public List<(string File, string Reason)> UnreadableFiles { get; } = new();
        public int SystemLines { get; set; }
        /// <summary>Wall-clock times that occur twice (autumn clock change), resolved by message order.</summary>
        public int AmbiguousTimes { get; set; }
        /// <summary>Wall-clock times that do not exist (spring clock change), moved forward.</summary>
        public int InvalidTimes { get; set; }
        public int MediaFound { get; set; }
        public int MediaMissing { get; set; }
        /// <summary>"day-first", "month-first", "year-first" or "ambiguous" for numeric dates; null when the format has none.</summary>
        public string? DateOrder { get; set; }
        /// <summary>The zone wall-clock times were read in; null when the format carries instants.</summary>
        public string? ZoneUsed { get; set; }
        /// <summary>The zone on offer for this read (its IANA name), recorded as used the first time a wall-clock time needs it.</summary>
        public string? ZoneOffered { get; set; }
        public string? OwnerDetected { get; set; }
        public int RepairedText { get; set; }
        public int SkippedRecords { get; set; }

        /// <summary>Adds a warning once (the same problem on ten thousand lines is one warning).</summary>
        public void Warn(string message)
        {
            if (warnings.Count < MaxWarnings && seen.Add(message)) warnings.Add(message);
        }
    }

    /// <summary>A chat-export format.</summary>
    public interface IImportFormat
    {
        string Id { get; }
        string Title { get; }
        /// <summary>The format's times are wall-clock readings, so a zone must be chosen before commit.</summary>
        bool NeedsZone { get; }
        /// <summary>How sure this format is that the input is its own, 0 (no) to 1 (certain).</summary>
        double Detect(ImportInput input);
        /// <summary>Streams the export's messages in file order. Problems are reported, never thrown per record.</summary>
        IEnumerable<IngestMessage> Read(ImportInput input, ImportOptions options, ImportDiagnostics diagnostics);
    }

    /// <summary>A problem only Klives can settle before the import can be committed (which zone, which date order).</summary>
    public sealed class ImportDecisionNeededException : Exception
    {
        public ImportDecisionNeededException(string message) : base(message) { }
    }

    public static class ImportIds
    {
        /// <summary>
        /// A stable id for a message the source gives none for: a hash of where it was, who wrote it, the timestamp
        /// exactly as the file wrote it, the text, and its position among identical messages. Built from the raw
        /// text, not the interpreted time, so re-importing the same export with a corrected zone or date order lands
        /// on the same rows and corrects them instead of duplicating them.
        /// </summary>
        public static string Message(string conversationKey, string sender, string rawTimestamp, string? content, int occurrence)
        {
            byte[] bytes = Encoding.UTF8.GetBytes($"{conversationKey}\u0001{sender}\u0001{rawTimestamp}\u0001{content}\u0001{occurrence}");
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..32];
        }

        /// <summary>A short stable key for a name or title (case and surrounding space do not matter).</summary>
        public static string Key(string text) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant()))).ToLowerInvariant()[..16];
    }

    /// <summary>Decoding of export text: byte-order marks, UTF-16 without one, and the Meta exports' double-encoded UTF-8.</summary>
    public static class TextDecoding
    {
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
        private static readonly Encoding Latin1 = Encoding.Latin1;

        public static string Decode(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes[3..]);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes[2..]);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes[2..]);
            // UTF-16 without a BOM: ASCII-range text has a zero in every other byte.
            if (bytes.Length >= 64)
            {
                int evenZeros = 0, oddZeros = 0, sample = Math.Min(bytes.Length & ~1, 4096);
                for (int i = 0; i < sample; i += 2)
                {
                    if (bytes[i] == 0) evenZeros++;
                    if (bytes[i + 1] == 0) oddZeros++;
                }
                if (oddZeros > sample / 4 && evenZeros < sample / 64) return Encoding.Unicode.GetString(bytes);
                if (evenZeros > sample / 4 && oddZeros < sample / 64) return Encoding.BigEndianUnicode.GetString(bytes);
            }
            try
            {
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                // Not UTF-8: an old export in the system code page. Latin-1 maps every byte, so nothing is lost.
                return Latin1.GetString(bytes);
            }
        }

        /// <summary>
        /// Instagram and Messenger exports write UTF-8 bytes as if each were a Latin-1 character ("Ã©" for "é",
        /// "ð\u009f\u0098\u0082" for an emoji). This reverses it when — and only when — the text is entirely such
        /// characters and the bytes they stand for are valid UTF-8; correctly encoded text is returned untouched.
        /// </summary>
        public static string RepairMojibake(string text, out bool repaired)
        {
            repaired = false;
            bool suspicious = false;
            foreach (char ch in text)
            {
                if (ch > 0xFF) return text; // a real non-Latin-1 character: this text was not double-encoded
                if (ch >= 0x80) suspicious = true;
            }
            if (!suspicious) return text;
            try
            {
                string fixedText = StrictUtf8.GetString(Latin1.GetBytes(text));
                repaired = fixedText != text;
                return fixedText;
            }
            catch (DecoderFallbackException)
            {
                return text; // genuine Latin-1 text ("café" typed by hand in a correctly encoded file)
            }
        }
    }

    /// <summary>
    /// Reads JSON from a stream token by token, materialising one array element at a time, so a multi-gigabyte
    /// export (one object holding an array of every message) is never loaded whole.
    /// </summary>
    public sealed class JsonPullReader : IDisposable
    {
        private const int MaxElementBytes = 256 * 1024 * 1024;
        private readonly Stream stream;
        private byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 16);
        private int start, end;
        private bool finalBlock;
        private JsonReaderState state = new(new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 256 });

        public JsonPullReader(Stream stream)
        {
            this.stream = stream;
            Fill();
            // A UTF-8 byte-order mark is not JSON.
            if (end - start >= 3 && buffer[start] == 0xEF && buffer[start + 1] == 0xBB && buffer[start + 2] == 0xBF) start += 3;
        }

        public JsonTokenType Token { get; private set; }
        /// <summary>The text of the current property name or string token.</summary>
        public string? Text { get; private set; }
        public int Depth { get; private set; }

        /// <summary>Advances to the next token. False at the end of the document.</summary>
        public bool Read()
        {
            while (true)
            {
                var reader = new Utf8JsonReader(buffer.AsSpan(start, end - start), finalBlock, state);
                if (reader.Read())
                {
                    Token = reader.TokenType;
                    Depth = reader.CurrentDepth;
                    Text = Token is JsonTokenType.PropertyName or JsonTokenType.String ? reader.GetString() : null;
                    Commit(ref reader);
                    return true;
                }
                if (finalBlock) return false;
                Fill();
            }
        }

        /// <summary>
        /// Inside an array (the current token is its StartArray or the previous element): the next element as a
        /// self-contained <see cref="JsonElement"/>, or false when the array ends (its EndArray is consumed).
        /// </summary>
        public bool TryReadElement(out JsonElement element)
        {
            while (true)
            {
                var reader = new Utf8JsonReader(buffer.AsSpan(start, end - start), finalBlock, state);
                if (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray)
                    {
                        Token = JsonTokenType.EndArray;
                        Depth = reader.CurrentDepth;
                        Commit(ref reader);
                        element = default;
                        return false;
                    }
                    if (JsonDocument.TryParseValue(ref reader, out var doc))
                    {
                        using (doc) element = doc!.RootElement.Clone();
                        Token = reader.TokenType;
                        Depth = reader.CurrentDepth;
                        Commit(ref reader);
                        return true;
                    }
                }
                if (finalBlock) throw new JsonException("The JSON ends in the middle of a value.");
                if (end - start > MaxElementBytes) throw new JsonException("One JSON value is larger than 256 MB.");
                Fill();
            }
        }

        /// <summary>The value after a property name, as an element (small values only: scalars, short objects).</summary>
        public JsonElement ReadValue()
        {
            while (true)
            {
                var reader = new Utf8JsonReader(buffer.AsSpan(start, end - start), finalBlock, state);
                if (reader.Read() && JsonDocument.TryParseValue(ref reader, out var doc))
                {
                    JsonElement element;
                    using (doc) element = doc!.RootElement.Clone();
                    Token = reader.TokenType;
                    Depth = reader.CurrentDepth;
                    Commit(ref reader);
                    return element;
                }
                if (finalBlock) throw new JsonException("The JSON ends in the middle of a value.");
                if (end - start > MaxElementBytes) throw new JsonException("One JSON value is larger than 256 MB.");
                Fill();
            }
        }

        /// <summary>Skips the value after a property name without materialising it, however large.</summary>
        public void SkipValue()
        {
            if (!Read()) return;
            if (Token is not (JsonTokenType.StartObject or JsonTokenType.StartArray)) return;
            int target = Depth;
            while (Read())
                if (Token is JsonTokenType.EndObject or JsonTokenType.EndArray && Depth == target) return;
        }

        private void Commit(ref Utf8JsonReader reader)
        {
            start += (int)reader.BytesConsumed;
            state = reader.CurrentState;
        }

        private void Fill()
        {
            if (start > 0)
            {
                Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
                end -= start;
                start = 0;
            }
            if (end == buffer.Length)
            {
                byte[] bigger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                Buffer.BlockCopy(buffer, 0, bigger, 0, end);
                ArrayPool<byte>.Shared.Return(buffer);
                buffer = bigger;
            }
            int read = stream.Read(buffer, end, buffer.Length - end);
            if (read == 0) finalBlock = true;
            end += read;
        }

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(buffer);
            stream.Dispose();
        }
    }
}
