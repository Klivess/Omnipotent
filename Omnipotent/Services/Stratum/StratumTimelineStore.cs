using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using System.Collections.Concurrent;

namespace Omnipotent.Services.Stratum
{
    /// <summary>
    /// Append-only on-disk store for the unified per-project conversation timeline.
    /// Modeled on <see cref="StratumRunStore"/>'s JSONL pattern — explicitly NOT the
    /// rewrite-whole-file pattern of legacy chat storage, because a single Engineer turn
    /// can append hundreds of events.
    ///
    /// Layout (under Stratum/Conversations/):
    ///   &lt;projectID&gt;.timeline.jsonl   (append-only event log)
    ///   &lt;projectID&gt;.meta.json        (rolling summary + active turn, rewritten on change)
    /// </summary>
    public class StratumTimelineStore
    {
        private readonly string root;
        private readonly Action<string> log;
        private readonly ConcurrentDictionary<string, object> locks = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, long> seqCache = new(StringComparer.Ordinal);

        public StratumTimelineStore(Action<string> log)
        {
            this.log = log ?? (_ => { });
            root = OmniPaths.GetPath(OmniPaths.GlobalPaths.StratumConversationsDirectory);
            Directory.CreateDirectory(root);
        }

        private object LockFor(string projectID) => locks.GetOrAdd(projectID, _ => new object());
        private string TimelinePath(string projectID) => Path.Combine(root, projectID + ".timeline.jsonl");
        private string MetaPath(string projectID) => Path.Combine(root, projectID + ".meta.json");
        private string LegacyChatPath(string projectID) => Path.Combine(root, $"{projectID}_{StratumAgentRoles.MechanicalEngineer}.json");

        private const int MaxPayloadBytes = 32 * 1024;

        /// <summary>Appends an event, assigning the next sequence number. Returns the stored event.</summary>
        public StratumTimelineEvent Append(StratumTimelineEvent evt)
        {
            if (evt == null || string.IsNullOrWhiteSpace(evt.ProjectID))
                throw new ArgumentException("event requires ProjectID");
            lock (LockFor(evt.ProjectID))
            {
                ImportLegacyChatIfNeededLocked(evt.ProjectID);
                long next = GetLastSequenceLocked(evt.ProjectID) + 1;
                evt.Sequence = next;
                if (string.IsNullOrWhiteSpace(evt.EventID)) evt.EventID = Guid.NewGuid().ToString("N");
                if (evt.Timestamp == default) evt.Timestamp = DateTime.UtcNow;
                if (evt.PayloadJson != null && System.Text.Encoding.UTF8.GetByteCount(evt.PayloadJson) > MaxPayloadBytes)
                    evt.PayloadJson = TruncateUtf8(evt.PayloadJson, MaxPayloadBytes - 64) + "…(truncated)";

                File.AppendAllText(TimelinePath(evt.ProjectID), JsonConvert.SerializeObject(evt) + Environment.NewLine);
                seqCache[evt.ProjectID] = next;
                return evt;
            }
        }

        /// <summary>Reads events with Sequence &gt; <paramref name="sinceExclusive"/>, ascending, capped at <paramref name="max"/>.</summary>
        public List<StratumTimelineEvent> ReadSince(string projectID, long sinceExclusive, int max = 500)
        {
            var results = new List<StratumTimelineEvent>();
            lock (LockFor(projectID))
            {
                ImportLegacyChatIfNeededLocked(projectID);
                string path = TimelinePath(projectID);
                if (!File.Exists(path)) return results;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                string? line;
                while ((line = sr.ReadLine()) != null && results.Count < max)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    StratumTimelineEvent? e = null;
                    try { e = JsonConvert.DeserializeObject<StratumTimelineEvent>(line); } catch { /* skip partial line */ }
                    if (e != null && e.Sequence > sinceExclusive) results.Add(e);
                }
            }
            return results;
        }

        public long GetLastSequence(string projectID)
        {
            lock (LockFor(projectID)) return GetLastSequenceLocked(projectID);
        }

        private long GetLastSequenceLocked(string projectID)
        {
            if (seqCache.TryGetValue(projectID, out var cached)) return cached;
            long last = 0;
            string path = TimelinePath(projectID);
            if (File.Exists(path))
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var e = JsonConvert.DeserializeObject<StratumTimelineEvent>(line);
                        if (e != null && e.Sequence > last) last = e.Sequence;
                    }
                    catch { }
                }
            }
            seqCache[projectID] = last;
            return last;
        }

        // ── meta ──

        public StratumConversationMeta GetMeta(string projectID)
        {
            lock (LockFor(projectID))
            {
                string path = MetaPath(projectID);
                if (File.Exists(path))
                {
                    try
                    {
                        var m = JsonConvert.DeserializeObject<StratumConversationMeta>(File.ReadAllText(path));
                        if (m != null) return m;
                    }
                    catch (Exception ex) { log($"Timeline meta load failed for {projectID}: {ex.Message}"); }
                }
                return new StratumConversationMeta { ProjectID = projectID };
            }
        }

        public void SaveMeta(StratumConversationMeta meta)
        {
            lock (LockFor(meta.ProjectID))
            {
                meta.UpdatedAt = DateTime.UtcNow;
                string path = MetaPath(meta.ProjectID);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(meta, Formatting.Indented));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
        }

        /// <summary>All project IDs that have a conversation meta with an active turn (crash recovery).</summary>
        public List<StratumConversationMeta> AllMetasWithActiveTurns()
        {
            var list = new List<StratumConversationMeta>();
            if (!Directory.Exists(root)) return list;
            foreach (var f in Directory.EnumerateFiles(root, "*.meta.json"))
            {
                try
                {
                    var m = JsonConvert.DeserializeObject<StratumConversationMeta>(File.ReadAllText(f));
                    if (m != null && !string.IsNullOrWhiteSpace(m.ActiveTurnID)) list.Add(m);
                }
                catch { }
            }
            return list;
        }

        // ── legacy import ──

        /// <summary>
        /// One-time lazy import: when a project has no timeline yet but a legacy
        /// MechanicalEngineer chat file exists, convert its messages into timeline events so
        /// the unified conversation keeps the old history. Caller must hold the project lock.
        /// </summary>
        private void ImportLegacyChatIfNeededLocked(string projectID)
        {
            string timelinePath = TimelinePath(projectID);
            if (File.Exists(timelinePath)) return;
            string legacyPath = LegacyChatPath(projectID);
            if (!File.Exists(legacyPath)) return;

            try
            {
                var raw = JsonConvert.DeserializeObject<LegacyConversationFile>(File.ReadAllText(legacyPath));
                if (raw?.Messages == null || raw.Messages.Count == 0) return;
                long seq = 0;
                var lines = new System.Text.StringBuilder();
                foreach (var m in raw.Messages.OrderBy(m => m.Sequence))
                {
                    var evt = new StratumTimelineEvent
                    {
                        Sequence = ++seq,
                        EventID = m.MessageID ?? Guid.NewGuid().ToString("N"),
                        ProjectID = projectID,
                        Timestamp = m.CreatedAt,
                        Type = string.Equals(m.Author, "user", StringComparison.OrdinalIgnoreCase)
                            ? StratumTimelineEventTypes.UserMessage
                            : StratumTimelineEventTypes.AgentMessage,
                        Author = string.Equals(m.Author, "user", StringComparison.OrdinalIgnoreCase) ? "user" : "agent",
                        Text = m.Text ?? "",
                        PayloadJson = m.ProposalJson,
                        ArtifactIDs = m.ReferencedArtifactIDs ?? new List<string>(),
                    };
                    lines.Append(JsonConvert.SerializeObject(evt)).Append(Environment.NewLine);
                }
                File.WriteAllText(timelinePath, lines.ToString());
                seqCache[projectID] = seq;
                log($"Imported {seq} legacy chat message(s) into the {projectID} timeline.");
            }
            catch (Exception ex)
            {
                log($"Legacy chat import failed for {projectID}: {ex.Message}");
            }
        }

        private sealed class LegacyConversationFile
        {
            public StratumConversation? Conversation { get; set; }
            public List<StratumChatMessage>? Messages { get; set; }
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
