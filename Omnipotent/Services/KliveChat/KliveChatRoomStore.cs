using Newtonsoft.Json;
using System.Text;

namespace Omnipotent.Services.KliveChat
{
    /// <summary>
    /// Rooms on disk, so a room's link keeps working across restarts (rooms used to live only in memory and
    /// every restart broke every link). Written whole, atomically: a temp file flushed to disk, then renamed
    /// over the old one, so a crash mid-write leaves the previous version rather than a torn file.
    /// </summary>
    public sealed class KliveChatRoomStore
    {
        private static readonly JsonSerializerSettings Settings = new()
        {
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            Formatting = Formatting.Indented,
        };

        private readonly SemaphoreSlim writeLock = new(1, 1);

        public KliveChatRoomStore(string path)
        {
            FilePath = path;
        }

        public string FilePath { get; }

        /// <summary>
        /// The saved rooms. A missing file is no rooms; an unreadable one is moved aside (never overwritten
        /// with an empty list) and <paramref name="problem"/> says what happened.
        /// </summary>
        public List<KliveChatRoomRecord> Load(out string? problem)
        {
            problem = null;
            if (!File.Exists(FilePath)) return new List<KliveChatRoomRecord>();
            try
            {
                var records = JsonConvert.DeserializeObject<List<KliveChatRoomRecord>>(File.ReadAllText(FilePath), Settings) ?? new();
                return records
                    .Where(r => r != null && KliveChatText.IsValidRoomId(r.Id))
                    .GroupBy(r => r.Id, StringComparer.Ordinal)
                    .Select(g => Normalise(g.First()))
                    .ToList();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                string aside = $"{FilePath}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}";
                try { File.Move(FilePath, aside); } catch { }
                problem = $"{ex.Message} (moved aside to {Path.GetFileName(aside)})";
                return new List<KliveChatRoomRecord>();
            }
        }

        private static KliveChatRoomRecord Normalise(KliveChatRoomRecord record)
        {
            record.Name = KliveChatText.CleanRoomName(record.Name) ?? $"Room {record.Id[..Math.Min(4, record.Id.Length)]}";
            record.CreatedBy ??= string.Empty;
            record.BannedKeys ??= new List<string>();
            if (record.CreatedUtc == default) record.CreatedUtc = DateTime.UtcNow;
            if (record.LastActiveUtc == default) record.LastActiveUtc = record.CreatedUtc;
            return record;
        }

        public async Task SaveAsync(IReadOnlyCollection<KliveChatRoomRecord> records)
        {
            await writeLock.WaitAsync();
            try
            {
                string? directory = Path.GetDirectoryName(Path.GetFullPath(FilePath));
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                byte[] bytes = new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(records, Settings));
                string temp = FilePath + ".tmp";
                await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    await stream.WriteAsync(bytes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, FilePath, overwrite: true);
            }
            finally
            {
                writeLock.Release();
            }
        }
    }
}
