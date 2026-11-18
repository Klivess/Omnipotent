using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveAPI.Caching;
using System.Collections.Concurrent;
using System.Globalization;

namespace Omnipotent.Services.Demiurge
{
    /// <summary>
    /// Append-only on-disk store for the per-Steward event log — the single source of truth
    /// for a Steward (design doc §7). Cloned from Stratum's StratumTimelineStore JSONL pattern,
    /// with one structural difference: this log is multi-writer (Commander, N sub-agents and
    /// the stimulus bus append concurrently), so the per-Steward lock strictly guards only the
    /// sequence-assign + file-append critical section.
    ///
    /// Layout (under Demiurge/EventLog/):
    ///   &lt;stewardID&gt;.log.jsonl   (append-only event log)
    /// The mutable per-Steward meta (standing digest, active wake) lives in
    /// <see cref="StewardDigestStore"/>, not here.
    /// </summary>
    public class StewardEventLogStore
    {
        private readonly string root;
        private readonly Action<string> log;
        private readonly ConcurrentDictionary<string, object> locks = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, long> seqCache = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, List<(long sequence, long offset)>> sparseOffsets = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Dictionary<string, StewardEvent>> lastByType = new(StringComparer.Ordinal);
        /// <summary>Stewards whose sparse offset/last-of-type index has been built from a full read of
        /// the log. Tracked separately from <see cref="seqCache"/>, which the cheap tail read fills.</summary>
        private readonly ConcurrentDictionary<string, byte> fullyIndexed = new(StringComparer.Ordinal);
        private const int SparseStride = 128;
        /// <summary>Bytes read from the log's end when only the last sequence is wanted. One event's
        /// payload is capped at 32KB, so this always spans at least one complete record.</summary>
        private const int TailProbeBytes = 256 * 1024;
        private const int ReverseReadChunkBytes = 64 * 1024;

        /// <summary>Test-only counter: how many times a full log read has been performed.</summary>
        internal int FullIndexBuilds => fullIndexBuilds;
        private int fullIndexBuilds;

        /// <summary>Raised after an event is durably appended. Used by the retrieval index
        /// and (later) the website's live feed. Fired in sequence order.</summary>
        public event Action<StewardEvent>? EventAppended;

        private const int MaxPayloadBytes = 32 * 1024;

        public StewardEventLogStore(Action<string> log)
        {
            this.log = log ?? (_ => { });
            root = OmniPaths.GetPath(OmniPaths.GlobalPaths.DemiurgeEventLogDirectory);
            Directory.CreateDirectory(root);
        }

        private object LockFor(string stewardID) => locks.GetOrAdd(stewardID, _ => new object());
        private string LogPath(string stewardID) => Path.Combine(root, stewardID + ".log.jsonl");

        // Response-cache dependency keys: per-Steward log + the coarse set of logs.
        private static string CacheKey(string stewardID) => "demiurge:eventlog:" + stewardID;
        private const string CacheKeyAll = "demiurge:eventlog";

        /// <summary>Appends an event, assigning the next sequence number. Returns the stored event.</summary>
        public StewardEvent Append(StewardEvent evt)
        {
            if (evt == null || string.IsNullOrWhiteSpace(evt.StewardID))
                throw new ArgumentException("event requires StewardID");
            lock (LockFor(evt.StewardID))
            {
                long next = GetLastSequenceLocked(evt.StewardID) + 1;
                evt.Sequence = next;
                if (string.IsNullOrWhiteSpace(evt.EventID)) evt.EventID = Guid.NewGuid().ToString("N");
                if (evt.Timestamp == default) evt.Timestamp = DateTime.UtcNow;
                if (evt.PayloadJson != null && System.Text.Encoding.UTF8.GetByteCount(evt.PayloadJson) > MaxPayloadBytes)
                    evt.PayloadJson = TruncateUtf8(evt.PayloadJson, MaxPayloadBytes - 64) + "…(truncated)";

                string path = LogPath(evt.StewardID);
                // This is the Steward's source of truth, including side-effecting tool-call/result
                // pairs. Flush the complete JSONL record to disk before publishing it so a process
                // restart cannot acknowledge an event that existed only in an OS write buffer.
                byte[] record = System.Text.Encoding.UTF8.GetBytes(
                    JsonConvert.SerializeObject(evt) + Environment.NewLine);
                long offset;
                using (var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
                           bufferSize: 4096, FileOptions.WriteThrough))
                {
                    long end = fs.Seek(0, SeekOrigin.End);
                    if (end > 0)
                    {
                        fs.Seek(-1, SeekOrigin.End);
                        int last = fs.ReadByte();
                        fs.Seek(0, SeekOrigin.End);
                        if (last != '\n')
                        {
                            // A hard stop can leave one incomplete trailing JSON record. Isolate
                            // it before this append; otherwise both records become one unreadable
                            // line and the first post-restart event disappears with the tail.
                            fs.WriteByte((byte)'\n');
                        }
                    }
                    offset = fs.Position;
                    fs.Write(record, 0, record.Length);
                    fs.Flush(flushToDisk: true);
                }
                seqCache[evt.StewardID] = next;
                // Extend the sparse index only while it is complete. A partial index built from
                // appends alone would leave earlier offsets missing, and the early-return in
                // EnsureIndexLocked would then accept it as authoritative.
                if (fullyIndexed.ContainsKey(evt.StewardID) && (next - 1) % SparseStride == 0)
                    sparseOffsets.GetOrAdd(evt.StewardID, _ => new()).Add((next, offset));
                lastByType.GetOrAdd(evt.StewardID, _ => new(StringComparer.Ordinal))[evt.Type] = evt;
                // Bump under the append lock (write is durably visible above): invalidates
                // any cached read of this Steward's log, and the coarse Steward-set key.
                CacheDeps.Bump(CacheKey(evt.StewardID));
                CacheDeps.Bump(CacheKeyAll);
                // Publish while holding the per-Steward append lock so concurrent writers cannot
                // deliver sequence N+1 to retrieval/WebSocket subscribers before sequence N.
                try { EventAppended?.Invoke(evt); } catch { }
            }
            return evt;
        }

        /// <summary>Reads events with Sequence &gt; <paramref name="sinceExclusive"/>, ascending, capped at <paramref name="max"/>.</summary>
        public List<StewardEvent> ReadSince(string stewardID, long sinceExclusive, int max = 500)
        {
            CacheDeps.NoteRead(CacheKey(stewardID));
            if (max <= 0) return new List<StewardEvent>();

            // The website, WebSocket replay, and active wakes almost always read immediately after
            // the current cursor. When the whole missing range fits in this page, read it directly
            // from the file's end instead of cold-building an index over the Steward's unbounded
            // history. A genuinely old cursor still falls through to the sparse forward index so
            // paging keeps its "oldest max events after since" contract.
            long last = GetLastSequenceForRead(stewardID);
            if (sinceExclusive >= last) return new List<StewardEvent>();
            if (sinceExclusive >= Math.Max(0, last - max))
                return ReadNewestFromTail(stewardID, sinceExclusive, max);

            var results = new List<StewardEvent>();
            lock (LockFor(stewardID))
            {
                string path = LogPath(stewardID);
                if (!File.Exists(path)) return results;
                EnsureIndexLocked(stewardID);
                long offset = 0;
                if (sparseOffsets.TryGetValue(stewardID, out var offsets))
                    foreach (var item in offsets)
                    {
                        if (item.sequence > sinceExclusive + 1) break;
                        offset = item.offset;
                    }
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(offset, SeekOrigin.Begin);
                using var sr = new StreamReader(fs);
                string? line;
                while ((line = sr.ReadLine()) != null && results.Count < max)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    StewardEvent? e = null;
                    try { e = JsonConvert.DeserializeObject<StewardEvent>(line); } catch { /* skip partial line */ }
                    if (e != null && e.Sequence > sinceExclusive) results.Add(e);
                }
            }
            return results;
        }

        /// <summary>
        /// Reads the most recent <paramref name="count"/> events in ascending order directly from
        /// the log's end. This deliberately does not build the full sparse index or take the append
        /// lock: opening a Steward must remain cheap even when its history is hundreds of MB.
        /// </summary>
        public List<StewardEvent> ReadTail(string stewardID, int count)
        {
            CacheDeps.NoteRead(CacheKey(stewardID));
            return count <= 0
                ? new List<StewardEvent>()
                : ReadNewestFromTail(stewardID, long.MinValue, count);
        }

        /// <summary>
        /// Reads the newest bounded window after a watermark, ascending. Wake rehydration wants the
        /// latest relevant context rather than a forward-paging cursor, so it must never parse the
        /// complete historical log merely because a digest watermark is old.
        /// </summary>
        public List<StewardEvent> ReadRecentSince(string stewardID, long sinceExclusive, int count)
        {
            CacheDeps.NoteRead(CacheKey(stewardID));
            return count <= 0
                ? new List<StewardEvent>()
                : ReadNewestFromTail(stewardID, sinceExclusive, count);
        }

        /// <summary>
        /// Streams every event whose timestamp falls within [<paramref name="fromUtc"/>, <paramref name="toUtc"/>]
        /// (inclusive; null bounds are open-ended), ascending by sequence — the lossless export read.
        /// Unlike <see cref="ReadSince"/> this is uncapped and reads the append-only file directly
        /// (FileShare.ReadWrite, no append lock held) so a large export never stalls the fleet's
        /// concurrent event writes; a partially written trailing line is skipped rather than throwing.
        /// </summary>
        public IEnumerable<StewardEvent> EnumerateRange(string stewardID, DateTime? fromUtc, DateTime? toUtc)
        {
            CacheDeps.NoteRead(CacheKey(stewardID));
            string path = LogPath(stewardID);
            if (!File.Exists(path)) yield break;
            DateTime? from = fromUtc?.ToUniversalTime();
            DateTime? to = toUtc?.ToUniversalTime();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                StewardEvent? e = null;
                try { e = JsonConvert.DeserializeObject<StewardEvent>(line); } catch { /* skip partial/corrupt line */ }
                if (e == null) continue;
                DateTime ts = e.Timestamp.ToUniversalTime();
                if (from.HasValue && ts < from.Value) continue;
                if (to.HasValue && ts > to.Value) continue;
                yield return e;
            }
        }

        /// <summary>
        /// Analytics needs every event in the selected range plus the sparse lifecycle transitions
        /// before it. Read only Timestamp/Type from the front of each old JSON row first, so years of
        /// historical tool payloads are not fully deserialized merely to be discarded.
        /// </summary>
        public IEnumerable<StewardEvent> EnumerateForAnalytics(
            string stewardID,
            DateTime fromUtc,
            DateTime toUtc)
        {
            CacheDeps.NoteRead(CacheKey(stewardID));
            string path = LogPath(stewardID);
            if (!File.Exists(path)) yield break;
            DateTime from = fromUtc.ToUniversalTime();
            DateTime to = toUtc.ToUniversalTime();

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (TryReadAnalyticsEnvelope(line, out DateTime envelopeTimestamp, out string? envelopeType))
                {
                    if (envelopeTimestamp > to) continue;
                    if (envelopeTimestamp < from
                        && !StewardLifecycleEvents.IsLifecycleCapableType(envelopeType))
                        continue;
                }

                StewardEvent? evt = null;
                try { evt = JsonConvert.DeserializeObject<StewardEvent>(line); }
                catch { /* skip partial/corrupt line */ }
                if (evt == null) continue;
                DateTime timestamp = evt.Timestamp.ToUniversalTime();
                if (timestamp > to) continue;
                if (timestamp < from && !StewardLifecycleEvents.TryReadToStatus(evt, out _))
                    continue;
                yield return evt;
            }
        }

        private static bool TryReadAnalyticsEnvelope(
            string json,
            out DateTime timestamp,
            out string? type)
        {
            timestamp = default;
            type = null;
            bool hasTimestamp = false;
            bool hasType = false;
            try
            {
                using var reader = new JsonTextReader(new StringReader(json))
                {
                    DateParseHandling = DateParseHandling.DateTime,
                };
                while (reader.Read())
                {
                    if (reader.TokenType != JsonToken.PropertyName) continue;
                    string? name = reader.Value?.ToString();
                    if (!reader.Read()) break;
                    if (string.Equals(name, nameof(StewardEvent.Timestamp), StringComparison.OrdinalIgnoreCase))
                    {
                        if (reader.Value is DateTime value)
                        {
                            timestamp = value.ToUniversalTime();
                            hasTimestamp = true;
                        }
                        else if (DateTime.TryParse(
                            reader.Value?.ToString(), CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out value))
                        {
                            timestamp = value.ToUniversalTime();
                            hasTimestamp = true;
                        }
                    }
                    else if (string.Equals(name, nameof(StewardEvent.Type), StringComparison.OrdinalIgnoreCase))
                    {
                        type = DemiurgeLegacyNames.CurrentEventType(reader.Value?.ToString());
                        hasType = true;
                    }

                    // StewardEvent serializes Timestamp and Type before the potentially 32KB Text /
                    // PayloadJson fields, so the normal path stops before parsing those strings.
                    if (hasTimestamp && hasType) return true;
                }
            }
            catch { }
            return hasTimestamp && hasType;
        }

        public long GetLastSequence(string stewardID)
        {
            CacheDeps.NoteRead(CacheKey(stewardID));
            // Lock-free once the sequence is known. Append publishes it only after its durable
            // write, so an unlocked read returns a value that was true at some instant during the
            // call — exactly the guarantee taking the lock would give — while keeping the fleet
            // list off a lock that Append holds across an fsync.
            if (seqCache.TryGetValue(stewardID, out long cached)) return cached;
            lock (LockFor(stewardID)) return GetLastSequenceLocked(stewardID);
        }

        public StewardEvent? GetLastOfType(string stewardID, string type)
        {
            CacheDeps.NoteRead(CacheKey(stewardID));
            lock (LockFor(stewardID))
            {
                EnsureIndexLocked(stewardID);
                return lastByType.TryGetValue(stewardID, out var byType) && byType.TryGetValue(type, out var evt)
                    ? evt : null;
            }
        }

        /// <summary>All Steward IDs that have an event log on disk.</summary>
        public List<string> AllStewardIDsWithLogs()
        {
            CacheDeps.NoteRead(CacheKeyAll);
            if (!Directory.Exists(root)) return new List<string>();
            return Directory.EnumerateFiles(root, "*.log.jsonl")
                .Select(f => Path.GetFileName(f).Replace(".log.jsonl", ""))
                .ToList();
        }

        private long GetLastSequenceLocked(string stewardID)
        {
            if (seqCache.TryGetValue(stewardID, out long cached)) return cached;
            // Only the final sequence number is wanted here, and the log's tail answers that in a
            // few KB. Building the sparse index instead costs a full parse of a log that grows
            // without bound — and /demiurge/list asks this of every Steward on every fleet event,
            // while Append asks it for every event written. It must never be O(log size).
            long last = ReadLastSequenceFromTailLocked(stewardID);
            seqCache[stewardID] = last;
            return last;
        }

        private long GetLastSequenceForRead(string stewardID)
        {
            if (seqCache.TryGetValue(stewardID, out long cached)) return cached;
            lock (LockFor(stewardID)) return GetLastSequenceLocked(stewardID);
        }

        /// <summary>
        /// Reverse-scans complete JSONL records from a snapshot of the current file length. Records
        /// can cross chunk boundaries; a partial crash tail is ignored. Results are collected
        /// newest-first and reversed before return, preserving the public ascending-order contract.
        /// No Steward lock is taken, so a history viewer cannot stop an agent from appending.
        /// </summary>
        private List<StewardEvent> ReadNewestFromTail(string stewardID, long sinceExclusive, int count)
        {
            var newestFirst = new List<StewardEvent>(Math.Min(count, 2048));
            string path = LogPath(stewardID);
            if (!File.Exists(path)) return newestFirst;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long position = fs.Length;
            if (position == 0) return newestFirst;

            var reversedLine = new List<byte>(4096);
            var chunk = new byte[ReverseReadChunkBytes];
            bool reachedBoundary = false;

            while (position > 0 && newestFirst.Count < count && !reachedBoundary)
            {
                int bytesToRead = (int)Math.Min(chunk.Length, position);
                position -= bytesToRead;
                fs.Seek(position, SeekOrigin.Begin);
                fs.ReadExactly(chunk, 0, bytesToRead);

                for (int i = bytesToRead - 1; i >= 0; i--)
                {
                    if (chunk[i] != (byte)'\n')
                    {
                        reversedLine.Add(chunk[i]);
                        continue;
                    }

                    reachedBoundary = ConsumeReverseLine();
                    if (reachedBoundary || newestFirst.Count >= count) break;
                }
            }

            if (!reachedBoundary && newestFirst.Count < count && reversedLine.Count > 0)
                ConsumeReverseLine();

            newestFirst.Reverse();
            return newestFirst;

            bool ConsumeReverseLine()
            {
                if (reversedLine.Count == 0) return false; // normal trailing newline / blank record

                var bytes = reversedLine.ToArray();
                Array.Reverse(bytes);
                reversedLine.Clear();

                StewardEvent? evt = null;
                try
                {
                    string line = System.Text.Encoding.UTF8.GetString(bytes)
                        .TrimEnd('\r')
                        .TrimStart('\uFEFF');
                    if (!string.IsNullOrWhiteSpace(line))
                        evt = JsonConvert.DeserializeObject<StewardEvent>(line);
                }
                catch { /* partial/corrupt record: continue to the preceding complete line */ }

                if (evt == null) return false;
                if (evt.Sequence <= sinceExclusive) return true;
                newestFirst.Add(evt);
                return false;
            }
        }

        /// <summary>
        /// Highest sequence in the log, read from the last <see cref="TailProbeBytes"/> of the file.
        /// Only whole lines are considered (the first line of a mid-file window may be a fragment),
        /// and the window widens until something parses so a corrupt or oversized tail still resolves
        /// to the same answer a full read would give.
        /// </summary>
        private long ReadLastSequenceFromTailLocked(string stewardID)
        {
            string path = LogPath(stewardID);
            if (!File.Exists(path)) return 0;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long length = fs.Length;
            if (length == 0) return 0;

            for (long window = Math.Min(TailProbeBytes, length); ; window = Math.Min(window * 8, length))
            {
                fs.Seek(length - window, SeekOrigin.Begin);
                var buffer = new byte[window];
                fs.ReadExactly(buffer, 0, (int)window);

                string[] lines = System.Text.Encoding.UTF8.GetString(buffer).Split('\n');
                // A window that starts mid-file begins with the tail of an earlier record.
                int first = window < length ? 1 : 0;
                long last = 0;
                for (int i = first; i < lines.Length; i++)
                {
                    string line = lines[i].TrimEnd('\r').TrimStart('\uFEFF');
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var e = JsonConvert.DeserializeObject<StewardEvent>(line);
                        if (e != null && e.Sequence > last) last = e.Sequence;
                    }
                    catch { /* partial/corrupt trailing record */ }
                }
                if (last > 0 || window >= length) return last;
            }
        }

        private void EnsureIndexLocked(string stewardID)
        {
            if (fullyIndexed.ContainsKey(stewardID)) return;
            Interlocked.Increment(ref fullIndexBuilds);
            long last = 0;
            string path = LogPath(stewardID);
            var offsets = new List<(long sequence, long offset)>();
            var types = new Dictionary<string, StewardEvent>(StringComparer.Ordinal);
            if (File.Exists(path))
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long lineOffset = 0;
                long consumed = 0;
                var bytes = new List<byte>(4096);
                // Chunked rather than byte-at-a-time: this walks the whole log, and a long-lived
                // Steward's log is measured in hundreds of MB.
                var chunk = new byte[64 * 1024];
                int read;
                while ((read = fs.Read(chunk, 0, chunk.Length)) > 0)
                {
                    for (int i = 0; i < read; i++)
                    {
                        if (chunk[i] != (byte)'\n') { bytes.Add(chunk[i]); continue; }
                        IndexLine(bytes, lineOffset);
                        bytes.Clear();
                        lineOffset = consumed + i + 1;
                    }
                    consumed += read;
                }
                if (bytes.Count > 0) IndexLine(bytes, lineOffset);

                void IndexLine(List<byte> lineBytes, long offset)
                {
                    try
                    {
                        string line = System.Text.Encoding.UTF8.GetString(lineBytes.ToArray()).TrimEnd('\r').TrimStart('\uFEFF');
                        if (string.IsNullOrWhiteSpace(line)) return;
                        var e = JsonConvert.DeserializeObject<StewardEvent>(line);
                        if (e == null) return;
                        if ((e.Sequence - 1) % SparseStride == 0) offsets.Add((e.Sequence, offset));
                        if (e.Sequence > last) last = e.Sequence;
                        types[e.Type] = e;
                    }
                    catch { }
                }
            }
            seqCache[stewardID] = last;
            sparseOffsets[stewardID] = offsets;
            lastByType[stewardID] = types;
            fullyIndexed[stewardID] = 0;
        }

        private static string TruncateUtf8(string s, int maxBytes)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(s);
            if (bytes.Length <= maxBytes) return s;
            int len = maxBytes;
            while (len > 0 && (bytes[len] & 0xC0) == 0x80) len--; // don't split a code point
            return System.Text.Encoding.UTF8.GetString(bytes, 0, len);
        }
    }
}
