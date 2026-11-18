using Newtonsoft.Json;
using System.Diagnostics;
using System.IO.Compression;
using Reel = Omnipotent.Services.MemeScraper.InstagramScrapeUtilities.InstagramReel;

namespace Omnipotent.Services.MemeScraper
{
    /// <summary>
    /// Every reel record in one compressed file, so MemeScraper starts by reading one file instead of one per reel.
    ///
    /// Why: the reel store is one JSON file per reel (20,234 on 13 Nov 2026) on the server's D: volume. Reading them
    /// one by one took 1,171 s at the 13 Nov start, while every other service was starting too. For those 19.5 minutes
    /// every MemeScraper data route held its request 20 s and then answered 503 (the KM dashboard card, the MemeScraper
    /// pages), and OmniGram/OmniTumblr/OmniTube saw no reel catalogue. Listing the 20k video files' metadata alone took
    /// 61 s on an idle afternoon, so the cost is per file on that disk, not JSON parsing.
    ///
    /// The per-reel files stay the source of truth: scrapes write them and deleting a source removes them. The index
    /// caches them, and stamps each one with the file's length and last-write time from the directory listing.
    /// Start-up publishes the index's reels at once. <see cref="ReconcileAsync"/> then lists the directory, which opens
    /// no file. It reads only the files that are new or changed since the index was written, and reports files that
    /// have gone. A missing, unreadable or older-format index falls back to reading every file, once, after which the
    /// caller writes a new index.
    /// </summary>
    public sealed class MemeScraperReelIndex
    {
        public const int FormatVersion = 1;
        public readonly string ReelsDirectory;
        public readonly string IndexPath;
        private readonly int parallelism;

        // Live progress of a full read, for the health route while MemeScraper is still loading.
        private int progressTotal;
        private int progressDone;
        public (int Done, int Total) Progress => (Volatile.Read(ref progressDone), Volatile.Read(ref progressTotal));

        public MemeScraperReelIndex(string reelsDirectory, string indexPath, int? parallelism = null)
        {
            ReelsDirectory = reelsDirectory;
            IndexPath = indexPath;
            this.parallelism = parallelism ?? Math.Clamp(Environment.ProcessorCount * 2, 4, 32);
        }

        /// <summary>A reel file as the directory listing describes it.</summary>
        public readonly record struct Stamp(string File, long Length, long WriteTicks)
        {
            public bool Matches(FileInfo info) => info.Length == Length && info.LastWriteTimeUtc.Ticks == WriteTicks;
            public static Stamp Of(FileInfo info) => new(info.Name, info.Length, info.LastWriteTimeUtc.Ticks);
        }

        public sealed class Entry
        {
            public string File = "";
            public long Length;
            public long WriteTicks;
            public Reel? Reel;
        }

        private sealed class Document
        {
            public int Version;
            public DateTime WrittenUtc;
            public string? Directory;
            public List<Entry>? Entries;
        }

        public sealed class LoadReport
        {
            /// <summary>"index" (one file read) or "files" (every reel file read).</summary>
            public string Via = "files";
            public long Ms;
            public long IndexMs;
            public long IndexBytes;
            public DateTime? IndexWrittenUtc;
            /// <summary>Why the index was not used, when it was not.</summary>
            public string? IndexProblem;
            public long ListMs;
            /// <summary>Reel files opened. The whole point: 0 when the index was current.</summary>
            public int FilesRead;
            public int Failures;
            public string? FirstError;
            public int Reels;
        }

        public sealed class ReconcileReport
        {
            public long Ms;
            public long ListMs;
            public int Listed;
            public int Added;
            public int Changed;
            public int Removed;
            public int FilesRead;
            public int Failures;
            public string? FirstError;
            public DateTime FinishedUtc;
        }

        public sealed class WriteReport
        {
            public DateTime WrittenUtc;
            public int Reels;
            public long Bytes;
            public long Ms;
        }

        public sealed class ReconcileResult
        {
            public List<(Reel Reel, Stamp Stamp)> Added = new();
            public List<(string File, Reel Reel, Stamp Stamp)> Changed = new();
            public List<string> Removed = new();
            public ReconcileReport Report = new();
        }

        /// <summary>
        /// The reels and the stamp of the file each one came from: from the index when there is a usable one, else from
        /// every file in the directory.
        /// </summary>
        public async Task<(List<(Reel Reel, Stamp Stamp)> Items, LoadReport Report)> LoadAsync(CancellationToken ct = default)
        {
            var clock = Stopwatch.StartNew();
            var report = new LoadReport();
            var fromIndex = await TryReadIndexAsync(report, ct);
            if (fromIndex != null)
            {
                report.Via = "index";
                report.Reels = fromIndex.Count;
                report.Ms = clock.ElapsedMilliseconds;
                return (fromIndex, report);
            }

            var listClock = Stopwatch.StartNew();
            var files = ListFiles();
            report.ListMs = listClock.ElapsedMilliseconds;
            var (read, failures, firstError) = await ReadFilesAsync(files, ct);
            report.FilesRead = files.Count;
            report.Failures = failures;
            report.FirstError = firstError;
            report.Reels = read.Count;
            report.Ms = clock.ElapsedMilliseconds;
            return (read, report);
        }

        /// <summary>
        /// Compares <paramref name="known"/> (file → stamp, as the index described it) with the directory: reads new and
        /// changed files, and names files that are gone. Opens nothing for a file whose length and write time still match.
        /// </summary>
        public async Task<ReconcileResult> ReconcileAsync(IReadOnlyDictionary<string, Stamp> known, CancellationToken ct = default)
        {
            var clock = Stopwatch.StartNew();
            var result = new ReconcileResult();
            var listClock = Stopwatch.StartNew();
            var files = ListFiles();
            result.Report.ListMs = listClock.ElapsedMilliseconds;
            result.Report.Listed = files.Count;

            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var toRead = new List<FileInfo>();
            foreach (var file in files)
            {
                listed.Add(file.Name);
                if (known.TryGetValue(file.Name, out var stamp) && stamp.Matches(file)) continue;
                toRead.Add(file);
            }
            foreach (var name in known.Keys)
            {
                if (!listed.Contains(name)) result.Removed.Add(name);
            }

            var (read, failures, firstError) = await ReadFilesAsync(toRead, ct);
            foreach (var (reel, stamp) in read)
            {
                if (known.ContainsKey(stamp.File)) result.Changed.Add((stamp.File, reel, stamp));
                else result.Added.Add((reel, stamp));
            }
            result.Report.FilesRead = toRead.Count;
            result.Report.Failures = failures;
            result.Report.FirstError = firstError;
            result.Report.Added = result.Added.Count;
            result.Report.Changed = result.Changed.Count;
            result.Report.Removed = result.Removed.Count;
            result.Report.Ms = clock.ElapsedMilliseconds;
            result.Report.FinishedUtc = DateTime.UtcNow;
            return result;
        }

        /// <summary>Writes the index (temp file, then a rename, so a crash mid-write leaves the old one).</summary>
        public Task<WriteReport> WriteAsync(IReadOnlyList<Entry> entries, CancellationToken ct = default) => Task.Run(() =>
        {
            var clock = Stopwatch.StartNew();
            string? folder = Path.GetDirectoryName(IndexPath);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            string temp = IndexPath + ".tmp";
            var document = new Document { Version = FormatVersion, WrittenUtc = DateTime.UtcNow, Directory = ReelsDirectory, Entries = entries.ToList() };
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            using (var writer = new StreamWriter(gzip))
            using (var json = new JsonTextWriter(writer))
            {
                JsonSerializer.CreateDefault().Serialize(json, document);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temp, IndexPath, overwrite: true);
            return new WriteReport
            {
                WrittenUtc = document.WrittenUtc,
                Reels = entries.Count,
                Bytes = new FileInfo(IndexPath).Length,
                Ms = clock.ElapsedMilliseconds,
            };
        }, ct);

        private async Task<List<(Reel Reel, Stamp Stamp)>?> TryReadIndexAsync(LoadReport report, CancellationToken ct)
        {
            if (!File.Exists(IndexPath))
            {
                report.IndexProblem = "no index yet";
                return null;
            }
            var clock = Stopwatch.StartNew();
            try
            {
                var document = await Task.Run(() =>
                {
                    using var file = new FileStream(IndexPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                    report.IndexBytes = file.Length;
                    using var gzip = new GZipStream(file, CompressionMode.Decompress);
                    using var reader = new StreamReader(gzip);
                    using var json = new JsonTextReader(reader);
                    return JsonSerializer.CreateDefault().Deserialize<Document>(json);
                }, ct);
                report.IndexMs = clock.ElapsedMilliseconds;
                if (document == null || document.Entries == null)
                {
                    report.IndexProblem = "the index is empty";
                    return null;
                }
                if (document.Version != FormatVersion)
                {
                    report.IndexProblem = $"the index is format {document.Version}, this build reads {FormatVersion}";
                    return null;
                }
                report.IndexWrittenUtc = document.WrittenUtc;
                return document.Entries
                    .Where(e => e.Reel != null && !string.IsNullOrEmpty(e.File))
                    .Select(e => (e.Reel!, new Stamp(e.File, e.Length, e.WriteTicks)))
                    .ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                report.IndexMs = clock.ElapsedMilliseconds;
                report.IndexProblem = "the index could not be read: " + ex.Message;
                return null;
            }
        }

        /// <summary>The reel files, in name order (the order the old one-file-at-a-time loader used).</summary>
        private List<FileInfo> ListFiles()
        {
            Directory.CreateDirectory(ReelsDirectory);
            var files = new DirectoryInfo(ReelsDirectory).EnumerateFiles("*.json").ToList();
            files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            return files;
        }

        private async Task<(List<(Reel Reel, Stamp Stamp)> Items, int Failures, string? FirstError)> ReadFilesAsync(IReadOnlyList<FileInfo> files, CancellationToken ct)
        {
            var slots = new (Reel Reel, Stamp Stamp)?[files.Count];
            int failures = 0;
            string? firstError = null;
            Volatile.Write(ref progressDone, 0);
            Volatile.Write(ref progressTotal, files.Count);
            var options = new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct };
            await Parallel.ForEachAsync(Enumerable.Range(0, files.Count), options, async (i, token) =>
            {
                try
                {
                    string json = await File.ReadAllTextAsync(files[i].FullName, token);
                    var reel = JsonConvert.DeserializeObject<Reel>(json);
                    // The listing's stamp: if the file changed after it was listed, the next check reads it again.
                    if (reel != null) slots[i] = (reel, Stamp.Of(files[i]));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One summary line from the caller beats thousands of per-file error logs at boot.
                    Interlocked.Increment(ref failures);
                    Interlocked.CompareExchange(ref firstError, $"{files[i].Name}: {ex.Message}", null);
                }
                finally
                {
                    Interlocked.Increment(ref progressDone);
                }
            });
            return (slots.Where(s => s.HasValue).Select(s => s!.Value).ToList(), failures, firstError);
        }
    }
}
