using Newtonsoft.Json;

namespace Omnipotent.Services.MemeScraper
{
    /// <summary>
    /// Every scrape's report, kept across restarts as JSON lines so scrape reliability can be read over
    /// any date range. Before this the health route only knew the last 50 runs since the server started,
    /// so "is the scraper getting worse?" had no answer after a restart.
    ///
    /// Bounded: the newest <see cref="DefaultMaxEntries"/> (by default) stay in memory, and the file is rewritten down to them
    /// once it holds twice that many lines. A torn last line (crash mid-append) is skipped on load.
    /// </summary>
    public sealed class MemeScraperScrapeLog
    {
        public const int DefaultMaxEntries = 5000;

        private readonly string path;
        private readonly int maxEntries;
        private readonly object gate = new();
        private readonly SemaphoreSlim fileLock = new(1, 1);
        private List<MemeScraper.ScrapeReport> entries = new();
        private int linesOnDisk;

        public MemeScraperScrapeLog(string path, int maxEntries = DefaultMaxEntries)
        {
            this.path = path;
            this.maxEntries = Math.Max(1, maxEntries);
        }

        /// <summary>Oldest first. A copy, safe to enumerate while scrapes append.</summary>
        public List<MemeScraper.ScrapeReport> Snapshot()
        {
            lock (gate) return new List<MemeScraper.ScrapeReport>(entries);
        }

        public int Count
        {
            get { lock (gate) return entries.Count; }
        }

        /// <summary>Newest first, at most <paramref name="count"/>.</summary>
        public List<MemeScraper.ScrapeReport> Latest(int count)
        {
            lock (gate) return entries.Skip(Math.Max(0, entries.Count - count)).Reverse().ToList();
        }

        /// <summary>Reads the file; returns how many lines could not be parsed.</summary>
        public async Task<int> LoadAsync(CancellationToken ct = default)
        {
            if (!File.Exists(path)) return 0;
            var loaded = new List<MemeScraper.ScrapeReport>();
            int lines = 0, unreadable = 0;
            foreach (var line in await File.ReadAllLinesAsync(path, ct))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                lines++;
                try
                {
                    var report = JsonConvert.DeserializeObject<MemeScraper.ScrapeReport>(line);
                    if (report != null) loaded.Add(report);
                    else unreadable++;
                }
                catch (JsonException)
                {
                    unreadable++;
                }
            }
            loaded.Sort((a, b) => a.StartedUtc.CompareTo(b.StartedUtc));
            if (loaded.Count > maxEntries) loaded.RemoveRange(0, loaded.Count - maxEntries);
            lock (gate)
            {
                // Anything appended while the file was being read is newer than all of it.
                loaded.AddRange(entries);
                entries = loaded;
            }
            linesOnDisk = lines;
            return unreadable;
        }

        public async Task AppendAsync(MemeScraper.ScrapeReport report)
        {
            lock (gate)
            {
                entries.Add(report);
                if (entries.Count > maxEntries) entries.RemoveRange(0, entries.Count - maxEntries);
            }
            string line = JsonConvert.SerializeObject(report, Formatting.None);
            await fileLock.WaitAsync();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.AppendAllTextAsync(path, line + "\n");
                if (++linesOnDisk > maxEntries * 2) await CompactLockedAsync();
            }
            finally
            {
                fileLock.Release();
            }
        }

        private async Task CompactLockedAsync()
        {
            var keep = Snapshot();
            string temp = path + ".tmp";
            await File.WriteAllLinesAsync(temp, keep.Select(e => JsonConvert.SerializeObject(e, Formatting.None)));
            File.Move(temp, path, overwrite: true);
            linesOnDisk = keep.Count;
        }
    }
}
