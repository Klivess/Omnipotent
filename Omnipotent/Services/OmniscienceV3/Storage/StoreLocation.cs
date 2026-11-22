using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Storage
{
    /// <summary>What a start found about where the databases are, and what it did about where they should be.</summary>
    /// <param name="Folder">The folder that holds the databases now: the one to open.</param>
    /// <param name="Moved">They were moved there by this start.</param>
    /// <param name="Problem">Why they are not where the setting asks for them (they were left where they were).</param>
    /// <param name="SetAsideAs">The ending the old files were given after a move ("…corpus.db.moved-20261122T021500Z").</param>
    public sealed record StoreLocationOutcome(string Folder, bool Moved, string? Problem, long BytesMoved, TimeSpan Took, string? SetAsideAs);

    /// <summary>The folder that is recorded as holding the databases does not hold them. Nothing is opened: a new, empty
    /// engine must never be started beside the real one's data.</summary>
    public sealed class StoresMissingException : IOException
    {
        public StoresMissingException(string message) : base(message) { }
    }

    /// <summary>A move that cannot be made (or finished) for a reason another try will not change by itself.</summary>
    public sealed class StoreMoveException : IOException
    {
        public StoreMoveException(string message) : base(message) { }
    }

    /// <summary>
    /// Where the engine's databases are, and moving them.
    /// <para>Everything of v3 lives in one folder, its home, beside the old database. The databases are what is read all
    /// day, a page at a time; on the server the home is on a drive that gives 25 to 150 ms a page with seven jobs reading,
    /// and copies a database at 0.85 MB a second (22 Nov 2026). They can be kept in another folder, on a faster drive,
    /// while media, models and backups stay at home.</para>
    /// <para>Which folder holds them is written in the home (<see cref="RecordFile"/>) and nowhere else; the setting only
    /// says where they should be. A start that finds the two different moves them before anything is opened: every file
    /// is copied in pieces whose hashes are kept, read back and compared, and only then is the record switched and the
    /// old files set aside under another name. Nothing is ever deleted. A move that is interrupted carries on from the
    /// last whole piece at the next start, and at every moment the record names a folder that holds complete
    /// databases.</para>
    /// </summary>
    public static class StoreLocation
    {
        /// <summary>In the home: the full path of the folder that holds the databases. Absent while they are at home.</summary>
        public const string RecordFile = "stores.location";

        /// <summary>In a folder that databases are being copied into: where from, and how far each file has got. It is
        /// there from the first byte until after the record has been switched, so a folder that has it is never opened
        /// as it stands.</summary>
        public const string ArrivingFile = "stores.arriving";

        /// <summary>The database a folder cannot hold the engine's data without.</summary>
        private const string Corpus = "corpus.db";

        /// <summary>Each database with the log that may lie beside it. The two are one state and are copied together;
        /// SQLite's shared-memory file is rebuilt by whoever opens the database next and is only set aside with the rest.
        /// Smallest first, so a move that cannot be made fails before the long copy.</summary>
        private static readonly (string Name, string Log, string? Shared)[] Databases =
        {
            ("operator.db", "operator.db-wal", "operator.db-shm"),
            ("vectors.db", "vectors.db-wal", "vectors.db-shm"),
            ("analytics.duckdb", "analytics.duckdb.wal", null),
            ("intel.db", "intel.db-wal", "intel.db-shm"),
            (Corpus, Corpus + "-wal", Corpus + "-shm"),
        };

        /// <summary>The size of the pieces a file is copied and checked in. A move that is interrupted loses one piece at most.</summary>
        internal static int PieceBytes { get; set; } = 64 << 20;

        /// <summary>Room that must be left free in the folder moved to, on top of what is copied (or a tenth of it, if that
        /// is more): the databases and their logs grow.</summary>
        internal static long MinHeadroomBytes { get; set; } = 2L << 30;

        /// <summary>How much room a folder's drive has (tests answer for it).</summary>
        internal static Func<string, long> FreeBytes { get; set; } = folder => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;

        /// <summary>Tests: called after each piece is written and recorded (file name, pieces so far).</summary>
        internal static Action<string, int>? AfterPiece { get; set; }

        /// <summary>Tests: called at "copied" (every file is in the new folder and has been read back) and at "switched"
        /// (the record names the new folder).</summary>
        internal static Action<string>? AfterStep { get; set; }

        private sealed class Arriving
        {
            public string From { get; set; } = "";
            public long StartedAtMs { get; set; }
            public int PieceBytes { get; set; }
            public Dictionary<string, FileState> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class FileState
        {
            public long Length { get; set; }
            public long ModifiedTicks { get; set; }
            /// <summary>Bytes that are in the new folder and whose pieces are recorded.</summary>
            public long Copied { get; set; }
            /// <summary>SHA-256 of each piece as it was read from the old folder.</summary>
            public List<string> Pieces { get; set; } = new();
            /// <summary>The copy was read back and every piece matched.</summary>
            public bool Verified { get; set; }
        }

        // ── the record ───────────────────────────────────────────────────────────

        /// <summary>The folder the home records as holding the databases, or null when they are at home.</summary>
        public static string? Recorded(string home)
        {
            string path = Path.Combine(home, RecordFile);
            if (!File.Exists(path)) return null;
            string text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }

        private static void Record(string home, string folder)
        {
            string path = Path.Combine(home, RecordFile), temp = path + ".new";
            using (var f = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                f.Write(System.Text.Encoding.UTF8.GetBytes(folder + Environment.NewLine));
                f.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }

        private static string Normalise(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Trim().Trim('"')));

        private static bool Same(string a, string b) => string.Equals(Normalise(a), Normalise(b), StringComparison.OrdinalIgnoreCase);

        private static bool Holds(string folder) => File.Exists(Path.Combine(folder, Corpus));

        private static bool HoldsAny(string folder) => Databases.Any(d => File.Exists(Path.Combine(folder, d.Name)) || File.Exists(Path.Combine(folder, d.Log)));

        /// <summary>The folder a setting names, as a full local path. Throws <see cref="ArgumentException"/> with the reason when it cannot be one.</summary>
        private static string Wanted(string setting)
        {
            string text = setting.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(text)) throw new ArgumentException($"'{text}' is not a full path (for example C:\\OmniscienceV3).");
            if (text.StartsWith(@"\\", StringComparison.Ordinal)) throw new ArgumentException($"'{text}' is a network folder; the databases need a drive of this computer.");
            try
            {
                return Normalise(text);
            }
            catch (Exception ex) when (ex is NotSupportedException or PathTooLongException or System.Security.SecurityException)
            {
                throw new ArgumentException($"'{text}' is not a folder path that can be used ({ex.Message}).");
            }
        }

        // ── deciding ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Finds the folder that holds the databases and, when <paramref name="setting"/> names another, moves them there
        /// first. Call it with the home's instance lock held and before any store is opened: nothing else may have the
        /// files open.
        /// </summary>
        /// <param name="home">v3's own folder (media, models and backups stay here whatever happens).</param>
        /// <param name="setting">Where the databases should be: a full local path, or empty for "where they are".</param>
        /// <param name="progress">Told how far a move has got, a few times a minute.</param>
        /// <exception cref="StoresMissingException">The databases are not where the record says, and not anywhere this start may adopt them from.</exception>
        public static async Task<StoreLocationOutcome> ResolveAsync(string home, string? setting, IClock clock, Action<string>? progress = null, Action<string>? log = null,
            CancellationToken ct = default)
        {
            home = Normalise(home);
            string? recorded = Recorded(home);
            string current = recorded == null ? home : Normalise(recorded);
            string? wanted = null, unusable = null;
            if (!string.IsNullOrWhiteSpace(setting))
            {
                try { wanted = Wanted(setting); }
                catch (ArgumentException ex) { unusable = ex.Message; }
            }

            // Somebody moved the files by hand, or the record was lost: when the folder the setting names holds complete
            // databases and the recorded one holds none, the setting is believed and written down.
            bool adoptable = wanted != null && !Same(wanted, current) && Holds(wanted) && !File.Exists(Path.Combine(wanted, ArrivingFile));
            if (!Holds(current))
            {
                if (adoptable)
                {
                    Record(home, wanted!);
                    log?.Invoke($"[OmniscienceV3] the databases were found in '{wanted}' and none in '{current}': that folder is recorded as theirs from now on.");
                    return new StoreLocationOutcome(wanted!, false, null, 0, TimeSpan.Zero, null);
                }
                if (recorded != null)
                    throw new StoresMissingException(
                        $"The databases were moved to '{current}' and are not there now (a drive that is not connected, a changed drive letter, an emptied folder). " +
                        "Nothing was opened or created. Put the folder back, or set OmniscienceV3_StoresFolder to the folder that holds them, and start again.");
                if (Directory.Exists(home) && Directory.EnumerateFiles(home, Corpus + ".moved-*").Any())
                    throw new StoresMissingException(
                        $"The databases were moved away from '{home}' (their old files are still there, set aside) and the record of where to ({RecordFile}) is gone. " +
                        "Nothing was opened or created. Set OmniscienceV3_StoresFolder to the folder that holds them and start again.");
                // Otherwise: nothing has ever been stored. The databases are created where the setting says, or at home.
            }

            FinishSwitch(current, clock, log);

            if (wanted == null)
                return new StoreLocationOutcome(current, false, unusable == null ? null : $"The databases stay in '{current}': {unusable}", 0, TimeSpan.Zero, null);
            if (Same(wanted, current)) return new StoreLocationOutcome(current, false, null, 0, TimeSpan.Zero, null);
            try
            {
                return await MoveAsync(home, current, wanted, clock, progress, log, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not the end of the start: the databases are whole where they were, and the engine runs from there. What
                // was copied so far is kept for the next try.
                string why = ex is StoreMoveException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
                return new StoreLocationOutcome(current, false, $"The databases stay in '{current}'; they could not be moved to '{wanted}': {why}", 0, TimeSpan.Zero, null);
            }
        }

        /// <summary>The record already names this folder and the folder still has its "arriving" note: the process ended
        /// after the switch and before it had tidied up. The record is only ever switched once every file was copied and
        /// read back, so the move was complete; what is left is to set the old files aside.</summary>
        private static void FinishSwitch(string current, IClock clock, Action<string>? log)
        {
            string note = Path.Combine(current, ArrivingFile);
            if (!File.Exists(note)) return;
            var arriving = Load(note);
            string? setAside = arriving != null && !Same(arriving.From, current) && Directory.Exists(arriving.From) ? SetAside(arriving.From, clock, log) : null;
            File.Delete(note);
            log?.Invoke($"[OmniscienceV3] finished a move of the databases to '{current}' that an earlier start had completed but not tidied up" +
                        (setAside == null ? "." : $"; the old files in '{arriving!.From}' now end in {setAside}."));
        }

        // ── moving ───────────────────────────────────────────────────────────────

        private static async Task<StoreLocationOutcome> MoveAsync(string home, string from, string to, IClock clock, Action<string>? progress, Action<string>? log, CancellationToken ct)
        {
            var took = Stopwatch.StartNew();
            Directory.CreateDirectory(to);
            string note = Path.Combine(to, ArrivingFile);
            bool begun = File.Exists(note);
            if (!begun && HoldsAny(to))
                throw new StoreMoveException($"'{to}' already holds Omniscience database files that no move from '{from}' put there. Empty that folder or choose another.");
            if (!Holds(from))
            {
                // Nothing has been stored yet: the databases are simply created in the new folder.
                Record(home, to);
                if (begun) File.Delete(note);
                return new StoreLocationOutcome(to, false, null, 0, took.Elapsed, null);
            }

            var arriving = begun ? Load(note) : null;
            if (arriving == null || !Same(arriving.From, from) || arriving.PieceBytes != PieceBytes)
            {
                // Left by a move from somewhere else, cut in other pieces, or unreadable: what it brought cannot be carried on from.
                foreach (var d in Databases)
                    foreach (string name in new[] { d.Name, d.Log })
                        File.Delete(Path.Combine(to, name));
                arriving = new Arriving { From = from, StartedAtMs = clock.NowMs, PieceBytes = PieceBytes };
            }

            var names = Databases.SelectMany(d => new[] { d.Name, d.Log }).Where(n => File.Exists(Path.Combine(from, n))).ToList();
            // A log that was there at the last try and is gone now (its database was checkpointed in between) leaves nothing to copy.
            foreach (string gone in arriving.Files.Keys.Where(k => !names.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
            {
                arriving.Files.Remove(gone);
                File.Delete(Path.Combine(to, gone));
            }
            foreach (string name in names)
            {
                var info = new FileInfo(Path.Combine(from, name));
                arriving.Files.TryGetValue(name, out var state);
                string copy = Path.Combine(to, name);
                bool carriesOn = state != null && state.Length == info.Length && state.ModifiedTicks == info.LastWriteTimeUtc.Ticks
                                 && File.Exists(copy) && new FileInfo(copy).Length >= state.Copied
                                 && state.Pieces.Count == (int)((state.Copied + PieceBytes - 1) / PieceBytes);
                // Changed since it was last looked at (the engine ran from the old folder in between), or never begun: from the start.
                if (!carriesOn) arriving.Files[name] = new FileState { Length = info.Length, ModifiedTicks = info.LastWriteTimeUtc.Ticks };
            }

            long total = arriving.Files.Values.Sum(f => f.Length), left = arriving.Files.Values.Sum(f => f.Verified ? 0 : f.Length - f.Copied);
            long headroom = Math.Max(MinHeadroomBytes, total / 10), free = FreeBytes(to);
            if (free < left + headroom)
                throw new StoreMoveException($"it needs {Size(left + headroom)} free there ({Size(left)} still to copy and {Size(headroom)} to grow into) and {Size(free)} are free.");
            Save(note, arriving);
            log?.Invoke($"[OmniscienceV3] moving the databases ({Size(total)}{(left < total ? ", " + Size(total - left) + " of it copied by an earlier start" : "")}) from '{from}' to '{to}'.");

            var report = new Report(progress, to, total, total - left);
            foreach (string name in names)
            {
                var state = arriving.Files[name];
                string source = Path.Combine(from, name), copy = Path.Combine(to, name);
                if (!state.Verified)
                {
                    await CopyAsync(source, copy, name, state, () => Save(note, arriving), report, ct).ConfigureAwait(false);
                    await VerifyAsync(copy, name, state, () => Save(note, arriving), ct).ConfigureAwait(false);
                }
                var now = new FileInfo(source);
                if (now.Length != state.Length || now.LastWriteTimeUtc.Ticks != state.ModifiedTicks)
                {
                    arriving.Files.Remove(name);
                    Save(note, arriving);
                    throw new StoreMoveException($"{name} changed while it was being copied; it is copied again at the next start.");
                }
            }
            AfterStep?.Invoke("copied");

            // The one step that decides: from here on the new folder is where the databases are.
            Record(home, to);
            AfterStep?.Invoke("switched");
            string setAside = SetAside(from, clock, log);
            File.Delete(note);
            log?.Invoke($"[OmniscienceV3] the databases ({Size(total)}) are in '{to}' now ({Describe(took.Elapsed)}). The old files stay in '{from}' with names ending in {setAside}; " +
                        "they are not used again and can be deleted once the engine has been seen working.");
            return new StoreLocationOutcome(to, true, null, total, took.Elapsed, setAside);
        }

        private static async Task CopyAsync(string source, string copy, string name, FileState state, Action save, Report report, CancellationToken ct)
        {
            await using var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
            await using var to = new FileStream(copy, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);
            // Whatever lies past the last recorded piece was written by a try that ended inside a piece.
            to.SetLength(state.Copied);
            from.Position = to.Position = state.Copied;
            byte[] buffer = new byte[Math.Min(PieceBytes, 4 << 20)];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (state.Copied < state.Length)
            {
                ct.ThrowIfCancellationRequested();
                long at = state.Copied, end = Math.Min(state.Length, at + PieceBytes);
                while (at < end)
                {
                    int read = await from.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, end - at)), ct).ConfigureAwait(false);
                    if (read == 0) throw new StoreMoveException($"{name} ended at {Size(at)} of {Size(state.Length)} while it was being copied.");
                    hash.AppendData(buffer, 0, read);
                    await to.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    at += read;
                    report.Copied(name, read);
                }
                to.Flush(flushToDisk: true);
                state.Pieces.Add(Convert.ToHexString(hash.GetHashAndReset()));
                state.Copied = end;
                save();
                AfterPiece?.Invoke(name, state.Pieces.Count);
            }
            to.Flush(flushToDisk: true);
        }

        /// <summary>Reads the copy back and compares every piece with what was read from the old folder. A copy that does not
        /// match is thrown away, so the next start copies that file again.</summary>
        private static async Task VerifyAsync(string copy, string name, FileState state, Action save, CancellationToken ct)
        {
            string? wrong = null;
            await using (var f = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous))
            {
                if (f.Length != state.Length) wrong = $"is {Size(f.Length)} long, not {Size(state.Length)}";
                byte[] buffer = new byte[Math.Min(PieceBytes, 4 << 20)];
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                for (int piece = 0; wrong == null && piece < state.Pieces.Count; piece++)
                {
                    ct.ThrowIfCancellationRequested();
                    long left = Math.Min(PieceBytes, state.Length - (long)piece * PieceBytes);
                    while (left > 0)
                    {
                        int read = await f.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct).ConfigureAwait(false);
                        if (read == 0) break;
                        hash.AppendData(buffer, 0, read);
                        left -= read;
                    }
                    if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), state.Pieces[piece], StringComparison.Ordinal))
                        wrong = $"did not read back as it was written (piece {piece + 1} of {state.Pieces.Count})";
                }
            }
            if (wrong != null)
            {
                File.Delete(copy);
                (state.Copied, state.Verified) = (0, false);
                state.Pieces.Clear();
                save();
                throw new StoreMoveException($"the copy of {name} {wrong}; it is copied again at the next start.");
            }
            state.Verified = true;
            save();
        }

        /// <summary>Gives every database file in <paramref name="folder"/> another name, so nothing can open it by mistake
        /// and nothing is lost. Returns the ending they were given.</summary>
        private static string SetAside(string folder, IClock clock, Action<string>? log)
        {
            string ending = ".moved-" + Instant.ToUtcDateTime(clock.NowMs).ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
            foreach (var d in Databases)
                foreach (string? name in new[] { d.Name, d.Log, d.Shared })
                {
                    if (name == null) continue;
                    string path = Path.Combine(folder, name);
                    if (!File.Exists(path)) continue;
                    try
                    {
                        File.Move(path, path + ending);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // It stays under its own name. The record no longer names this folder, so it is not opened.
                        log?.Invoke($"[OmniscienceV3] could not rename the old {name} in '{folder}' ({ex.Message}); it is no longer used and can be removed by hand.");
                    }
                }
            return ending;
        }

        // ── the note in the folder being moved to ────────────────────────────────

        private static Arriving? Load(string note)
        {
            try
            {
                return JsonSerializer.Deserialize<Arriving>(File.ReadAllText(note));
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // Cut off in the middle of being written: the move starts over.
                return null;
            }
        }

        private static void Save(string note, Arriving arriving)
        {
            string temp = note + ".new";
            using (var f = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                f.Write(JsonSerializer.SerializeToUtf8Bytes(arriving));
                f.Flush(flushToDisk: true);
            }
            File.Move(temp, note, overwrite: true);
        }

        // ── saying how far it is ─────────────────────────────────────────────────

        private sealed class Report
        {
            private readonly Action<string>? progress;
            private readonly string to;
            private readonly long total, before;
            private readonly Stopwatch running = Stopwatch.StartNew();
            private long copied, lastSaidAt = -10_000;

            public Report(Action<string>? progress, string to, long total, long before)
            {
                this.progress = progress;
                this.to = to;
                this.total = total;
                this.before = before;
            }

            public void Copied(string name, int bytes)
            {
                copied += bytes;
                long ms = running.ElapsedMilliseconds;
                if (progress == null || ms - lastSaidAt < 2_000) return;
                lastSaidAt = ms;
                long done = before + copied;
                double perSecond = ms < 1_000 ? 0 : copied / (ms / 1000.0);
                string left = perSecond <= 0 ? "" : ", about " + Describe(TimeSpan.FromSeconds((total - done) / perSecond)) + " left";
                progress($"Moving the databases to {to}: {Size(done)} of {Size(total)} ({(total == 0 ? 100 : done * 100 / total)}%), now {name}{left}");
            }
        }

        internal static string Size(long bytes) =>
            bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB"
            : bytes >= 1L << 20 ? (bytes / (double)(1L << 20)).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " MB"
            : bytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bytes";

        private static string Describe(TimeSpan span) =>
            span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} min" : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} min {span.Seconds} s" : $"{Math.Max(1, (int)span.TotalSeconds)} s";
    }
}
