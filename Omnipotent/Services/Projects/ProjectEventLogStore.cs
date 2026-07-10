using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using System.Collections.Concurrent;

namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Append-only on-disk store for the per-project event log — the single source of truth
    /// for a Project (design doc §7). Cloned from Stratum's StratumTimelineStore JSONL pattern,
    /// with one structural difference: this log is multi-writer (Commander, N sub-agents and
    /// the stimulus bus append concurrently), so the per-project lock strictly guards only the
    /// sequence-assign + file-append critical section.
    ///
    /// Layout (under Projects/EventLog/):
    ///   &lt;projectID&gt;.log.jsonl   (append-only event log)
    /// The mutable per-project meta (standing digest, active wake) lives in
    /// <see cref="ProjectDigestStore"/>, not here.
    /// </summary>
    public class ProjectEventLogStore
    {
        private readonly string root;
        private readonly Action<string> log;
        private readonly ConcurrentDictionary<string, object> locks = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, long> seqCache = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, List<(long sequence, long offset)>> sparseOffsets = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Dictionary<string, ProjectEvent>> lastByType = new(StringComparer.Ordinal);
        private const int SparseStride = 128;

        /// <summary>Raised after an event is durably appended. Used by the retrieval index
        /// and (later) the website's live feed. Fired in sequence order.</summary>
        public event Action<ProjectEvent>? EventAppended;

        private const int MaxPayloadBytes = 32 * 1024;

        public ProjectEventLogStore(Action<string> log)
        {
            this.log = log ?? (_ => { });
            root = OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsEventLogDirectory);
            Directory.CreateDirectory(root);
        }

        private object LockFor(string projectID) => locks.GetOrAdd(projectID, _ => new object());
        private string LogPath(string projectID) => Path.Combine(root, projectID + ".log.jsonl");

        /// <summary>Appends an event, assigning the next sequence number. Returns the stored event.</summary>
        public ProjectEvent Append(ProjectEvent evt)
        {
            if (evt == null || string.IsNullOrWhiteSpace(evt.ProjectID))
                throw new ArgumentException("event requires ProjectID");
            lock (LockFor(evt.ProjectID))
            {
                long next = GetLastSequenceLocked(evt.ProjectID) + 1;
                evt.Sequence = next;
                if (string.IsNullOrWhiteSpace(evt.EventID)) evt.EventID = Guid.NewGuid().ToString("N");
                if (evt.Timestamp == default) evt.Timestamp = DateTime.UtcNow;
                if (evt.PayloadJson != null && System.Text.Encoding.UTF8.GetByteCount(evt.PayloadJson) > MaxPayloadBytes)
                    evt.PayloadJson = TruncateUtf8(evt.PayloadJson, MaxPayloadBytes - 64) + "…(truncated)";

                string path = LogPath(evt.ProjectID);
                long offset = File.Exists(path) ? new FileInfo(path).Length : 0;
                File.AppendAllText(path, JsonConvert.SerializeObject(evt) + Environment.NewLine);
                seqCache[evt.ProjectID] = next;
                var offsets = sparseOffsets.GetOrAdd(evt.ProjectID, _ => new());
                if ((next - 1) % SparseStride == 0) offsets.Add((next, offset));
                lastByType.GetOrAdd(evt.ProjectID, _ => new(StringComparer.Ordinal))[evt.Type] = evt;
                // Publish while holding the per-project append lock so concurrent writers cannot
                // deliver sequence N+1 to retrieval/WebSocket subscribers before sequence N.
                try { EventAppended?.Invoke(evt); } catch { }
            }
            return evt;
        }

        /// <summary>Reads events with Sequence &gt; <paramref name="sinceExclusive"/>, ascending, capped at <paramref name="max"/>.</summary>
        public List<ProjectEvent> ReadSince(string projectID, long sinceExclusive, int max = 500)
        {
            var results = new List<ProjectEvent>();
            lock (LockFor(projectID))
            {
                string path = LogPath(projectID);
                if (!File.Exists(path)) return results;
                EnsureIndexLocked(projectID);
                long offset = 0;
                if (sparseOffsets.TryGetValue(projectID, out var offsets))
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
                    ProjectEvent? e = null;
                    try { e = JsonConvert.DeserializeObject<ProjectEvent>(line); } catch { /* skip partial line */ }
                    if (e != null && e.Sequence > sinceExclusive) results.Add(e);
                }
            }
            return results;
        }

        /// <summary>Reads the most recent <paramref name="count"/> events, ascending order.</summary>
        public List<ProjectEvent> ReadTail(string projectID, int count)
        {
            long last = GetLastSequence(projectID);
            return ReadSince(projectID, Math.Max(0, last - count), max: count);
        }

        public long GetLastSequence(string projectID)
        {
            lock (LockFor(projectID)) return GetLastSequenceLocked(projectID);
        }

        public ProjectEvent? GetLastOfType(string projectID, string type)
        {
            lock (LockFor(projectID))
            {
                EnsureIndexLocked(projectID);
                return lastByType.TryGetValue(projectID, out var byType) && byType.TryGetValue(type, out var evt)
                    ? evt : null;
            }
        }

        /// <summary>All project IDs that have an event log on disk.</summary>
        public List<string> AllProjectIDsWithLogs()
        {
            if (!Directory.Exists(root)) return new List<string>();
            return Directory.EnumerateFiles(root, "*.log.jsonl")
                .Select(f => Path.GetFileName(f).Replace(".log.jsonl", ""))
                .ToList();
        }

        private long GetLastSequenceLocked(string projectID)
        {
            EnsureIndexLocked(projectID);
            return seqCache.TryGetValue(projectID, out var cached) ? cached : 0;
        }

        private void EnsureIndexLocked(string projectID)
        {
            if (sparseOffsets.ContainsKey(projectID) && seqCache.ContainsKey(projectID)) return;
            long last = 0;
            string path = LogPath(projectID);
            var offsets = new List<(long sequence, long offset)>();
            var types = new Dictionary<string, ProjectEvent>(StringComparer.Ordinal);
            if (File.Exists(path))
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long lineOffset = 0;
                var bytes = new List<byte>(4096);
                int value;
                while ((value = fs.ReadByte()) >= 0)
                {
                    if (value != '\n') { bytes.Add((byte)value); continue; }
                    IndexLine(bytes, lineOffset);
                    bytes.Clear();
                    lineOffset = fs.Position;
                }
                if (bytes.Count > 0) IndexLine(bytes, lineOffset);

                void IndexLine(List<byte> lineBytes, long offset)
                {
                    try
                    {
                        string line = System.Text.Encoding.UTF8.GetString(lineBytes.ToArray()).TrimEnd('\r').TrimStart('\uFEFF');
                        if (string.IsNullOrWhiteSpace(line)) return;
                        var e = JsonConvert.DeserializeObject<ProjectEvent>(line);
                        if (e == null) return;
                        if ((e.Sequence - 1) % SparseStride == 0) offsets.Add((e.Sequence, offset));
                        if (e.Sequence > last) last = e.Sequence;
                        types[e.Type] = e;
                    }
                    catch { }
                }
            }
            seqCache[projectID] = last;
            sparseOffsets[projectID] = offsets;
            lastByType[projectID] = types;
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
