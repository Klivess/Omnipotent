using Newtonsoft.Json;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Stratum
{
    /// <summary>
    /// On-disk persistence for agent runs, gates, and event streams.
    /// Separate from StratumStorage to keep run/event hot-path I/O isolated from the
    /// main projects index (which would otherwise grow unboundedly with event spam).
    ///
    /// Layout:
    ///   Stratum/Runs/&lt;projectID&gt;/&lt;runID&gt;.json           (run snapshot, rewritten on each status change)
    ///   Stratum/Runs/&lt;projectID&gt;/&lt;runID&gt;.events.jsonl    (append-only event log)
    ///   Stratum/Runs/&lt;projectID&gt;/gates/&lt;gateID&gt;.json     (gate state, rewritten on resolve)
    /// </summary>
    public class StratumRunStore
    {
        // One lock per run dir keyed by projectID|runID; coarse but sufficient at our event rate.
        private readonly Dictionary<string, object> runLocks = new(StringComparer.Ordinal);
        private readonly object dictLock = new();
        private readonly string runsRoot;
        private readonly Action<string> log;

        public StratumRunStore(Action<string> log)
        {
            this.log = log ?? (_ => { });
            runsRoot = OmniPaths.GetPath(OmniPaths.GlobalPaths.StratumRunsDirectory);
            Directory.CreateDirectory(runsRoot);
        }

        private object LockFor(string projectID, string runID)
        {
            string key = $"{projectID}|{runID}";
            lock (dictLock)
            {
                if (!runLocks.TryGetValue(key, out var l))
                {
                    l = new object();
                    runLocks[key] = l;
                }
                return l;
            }
        }

        private string ProjectRunDir(string projectID)
        {
            string dir = Path.Combine(runsRoot, projectID);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private string GatesDir(string projectID)
        {
            string dir = Path.Combine(ProjectRunDir(projectID), "gates");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private string RunSnapshotPath(string projectID, string runID) => Path.Combine(ProjectRunDir(projectID), runID + ".json");
        private string EventLogPath(string projectID, string runID) => Path.Combine(ProjectRunDir(projectID), runID + ".events.jsonl");
        private string GatePath(string projectID, string gateID) => Path.Combine(GatesDir(projectID), gateID + ".json");

        // ── Runs ──
        public void SaveRun(StratumAgentRun run)
        {
            if (run == null || string.IsNullOrWhiteSpace(run.RunID) || string.IsNullOrWhiteSpace(run.ProjectID))
                throw new ArgumentException("run requires RunID and ProjectID");

            lock (LockFor(run.ProjectID, run.RunID))
            {
                string path = RunSnapshotPath(run.ProjectID, run.RunID);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(run, Formatting.Indented));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
        }

        public StratumAgentRun? LoadRun(string projectID, string runID)
        {
            string path = RunSnapshotPath(projectID, runID);
            if (!File.Exists(path)) return null;
            try
            {
                lock (LockFor(projectID, runID))
                {
                    return JsonConvert.DeserializeObject<StratumAgentRun>(File.ReadAllText(path));
                }
            }
            catch (Exception ex)
            {
                log($"Failed to load run {projectID}/{runID}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Lists run summaries for a project (newest first).</summary>
        public List<StratumAgentRun> ListRunsForProject(string projectID)
        {
            string dir = ProjectRunDir(projectID);
            var results = new List<StratumAgentRun>();
            foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
            {
                if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var r = JsonConvert.DeserializeObject<StratumAgentRun>(File.ReadAllText(file));
                    if (r != null) results.Add(r);
                }
                catch { /* skip malformed */ }
            }
            return results.OrderByDescending(r => r.CreatedAt).ToList();
        }

        // ── Events ──
        public void AppendEvent(StratumAgentEvent evt, string projectID)
        {
            if (evt == null || string.IsNullOrWhiteSpace(evt.RunID))
                throw new ArgumentException("event requires RunID");

            lock (LockFor(projectID, evt.RunID))
            {
                string line = JsonConvert.SerializeObject(evt) + Environment.NewLine;
                File.AppendAllText(EventLogPath(projectID, evt.RunID), line);
            }
        }

        /// <summary>Reads events with sequence number &gt; <paramref name="sinceExclusive"/>. Returns ordered ascending.</summary>
        public List<StratumAgentEvent> ReadEventsSince(string projectID, string runID, long sinceExclusive)
        {
            string path = EventLogPath(projectID, runID);
            var results = new List<StratumAgentEvent>();
            if (!File.Exists(path)) return results;

            lock (LockFor(projectID, runID))
            {
                // We tolerate a partial trailing line (in-progress write) by skipping bad lines.
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    StratumAgentEvent? e = null;
                    try { e = JsonConvert.DeserializeObject<StratumAgentEvent>(line); } catch { /* skip */ }
                    if (e == null) continue;
                    if (e.Sequence > sinceExclusive) results.Add(e);
                }
            }
            return results;
        }

        public long GetLastEventSequence(string projectID, string runID)
        {
            // Cheaper than scanning if events get long: parse just the last non-empty line.
            string path = EventLogPath(projectID, runID);
            if (!File.Exists(path)) return 0;
            lock (LockFor(projectID, runID))
            {
                long last = 0;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var e = JsonConvert.DeserializeObject<StratumAgentEvent>(line);
                        if (e != null && e.Sequence > last) last = e.Sequence;
                    }
                    catch { }
                }
                return last;
            }
        }

        // ── Gates ──
        public void SaveGate(StratumApprovalGate gate, string projectID)
        {
            if (gate == null || string.IsNullOrWhiteSpace(gate.GateID))
                throw new ArgumentException("gate requires GateID");

            lock (LockFor(projectID, gate.RunID))
            {
                string path = GatePath(projectID, gate.GateID);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(gate, Formatting.Indented));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
        }

        public StratumApprovalGate? LoadGate(string projectID, string gateID)
        {
            string path = GatePath(projectID, gateID);
            if (!File.Exists(path)) return null;
            try { return JsonConvert.DeserializeObject<StratumApprovalGate>(File.ReadAllText(path)); }
            catch (Exception ex) { log($"Failed to load gate {gateID}: {ex.Message}"); return null; }
        }
    }
}
