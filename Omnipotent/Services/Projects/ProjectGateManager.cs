using System.Collections.Concurrent;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Projects
{
    public enum GateDecision { Approve, Deny, Discuss }

    public record GateResolution(GateDecision Decision, string Comment, string ResolvedBy);

    /// <summary>A pending approval gate: an agent action suspended awaiting Klives' decision (§8).</summary>
    public class ProjectGate
    {
        public string GateID { get; set; } = "";
        public string ProjectID { get; set; } = "";
        public string? WakeID { get; set; }
        public string? AgentID { get; set; }
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string Rationale { get; set; } = "";
        /// <summary>Kind: "action" | "budget" | "money" — lets the UI/Discord style the card.</summary>
        public string Kind { get; set; } = "action";
        public string? ProposalJson { get; set; }
        public bool Resolved { get; set; }
        public string? Decision { get; set; }
        public string? Comment { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ResolvedAt { get; set; }
    }

    /// <summary>
    /// Approval-gate machinery for Projects, cloned in spirit from Stratum's
    /// RegisterGateWaiter/ResolveGate: an agent opens a gate and awaits a
    /// TaskCompletionSource that either the website or Discord (P5) resolves — first responder
    /// wins. Gates are persisted so the UI can render pending ones and so an unresolved gate
    /// survives to the log; the in-memory waiter is re-established on the next wake if needed.
    /// </summary>
    public class ProjectGateManager
    {
        private readonly ProjectEventLogStore eventLog;
        private readonly string dir;
        private readonly Action<string> log;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<GateResolution>> waiters = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, object> locks = new(StringComparer.Ordinal);

        /// <summary>Raised when a gate opens, so a surface (Discord P5) can present it alongside the website.</summary>
        public event Action<ProjectGate>? GateOpened;
        /// <summary>Raised after a persisted first-wins resolution, including resolutions of gates
        /// whose original in-memory waiter was lost in a restart.</summary>
        public event Action<ProjectGate, GateResolution>? GateResolved;

        public ProjectGateManager(ProjectEventLogStore eventLog, Action<string> log)
        {
            this.eventLog = eventLog;
            this.log = log ?? (_ => { });
            dir = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsDirectory), "Gates");
            Directory.CreateDirectory(dir);
        }

        private object LockFor(string projectID) => locks.GetOrAdd(projectID, _ => new object());
        private string GatePath(string projectID) => Path.Combine(dir, projectID + ".gates.json");

        /// <summary>
        /// Opens a gate and awaits its resolution. The calling agent's turn suspends here until
        /// Klives approves/denies/discusses (or <paramref name="ct"/> cancels the wake).
        /// </summary>
        public async Task<GateResolution> OpenGateAndWaitAsync(ProjectGate gate, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(gate.GateID)) gate.GateID = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<GateResolution>(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters[gate.GateID] = tcs;
            Persist(gate);

            eventLog.Append(new ProjectEvent
            {
                ProjectID = gate.ProjectID,
                WakeID = gate.WakeID,
                AgentID = gate.AgentID,
                Type = ProjectEventTypes.ApprovalRequested,
                Author = "commander",
                Text = $"{gate.Title}: {gate.Description}",
                GateID = gate.GateID,
                PayloadJson = gate.ProposalJson,
            });

            using var reg = ct.Register(() =>
            {
                waiters.TryRemove(gate.GateID, out _);
                tcs.TrySetCanceled(ct);
            });
            try { GateOpened?.Invoke(gate); } catch { /* surfaces must not break the gate */ }
            return await tcs.Task;
        }

        /// <summary>Resolves a pending gate (called from the website route or Discord bridge). First wins.</summary>
        public bool ResolveGate(string projectID, string gateID, GateResolution resolution)
        {
            if (resolution.Decision == GateDecision.Discuss) return false;

            ProjectGate? gate;
            lock (LockFor(projectID))
            {
                var gates = LoadLocked(projectID);
                gate = gates.FirstOrDefault(g => g.GateID == gateID);
                if (gate == null || gate.Resolved) return false;
                gate.Resolved = true;
                gate.Decision = resolution.Decision.ToString();
                gate.Comment = resolution.Comment;
                gate.ResolvedAt = DateTime.UtcNow;
                SaveLocked(projectID, gates);
            }

            eventLog.Append(new ProjectEvent
            {
                ProjectID = gate.ProjectID,
                WakeID = gate.WakeID,
                AgentID = gate.AgentID,
                Type = ProjectEventTypes.ApprovalResolved,
                Author = "klives",
                Text = $"{resolution.Decision}: {resolution.Comment}",
                GateID = gate.GateID,
            });

            if (waiters.TryRemove(gateID, out var tcs)) tcs.TrySetResult(resolution);
            try { GateResolved?.Invoke(gate, resolution); } catch { }
            return true;
        }

        /// <summary>Releases a live agent to discuss without resolving the persisted approval.
        /// The consequential action remains blocked; a later Approve/Deny is still first-wins.</summary>
        public bool BeginDiscussion(string projectID, string gateID, string comment)
        {
            ProjectGate? gate;
            lock (LockFor(projectID))
            {
                gate = LoadLocked(projectID).FirstOrDefault(g => g.GateID == gateID && !g.Resolved);
                if (gate == null) return false;
            }
            var resolution = new GateResolution(GateDecision.Discuss, comment, "klives");
            if (waiters.TryRemove(gateID, out var tcs)) tcs.TrySetResult(resolution);
            eventLog.Append(new ProjectEvent
            {
                ProjectID = projectID,
                WakeID = gate.WakeID,
                AgentID = gate.AgentID,
                Type = ProjectEventTypes.KlivesMessage,
                Author = "klives",
                Text = $"Discussion requested for approval '{gate.Title}': {comment}",
                GateID = gateID,
            });
            return true;
        }

        public List<ProjectGate> ListPending(string projectID)
        {
            lock (LockFor(projectID))
                return LoadLocked(projectID).Where(g => !g.Resolved).ToList();
        }

        private void Persist(ProjectGate gate)
        {
            lock (LockFor(gate.ProjectID))
            {
                var gates = LoadLocked(gate.ProjectID);
                gates.RemoveAll(g => g.GateID == gate.GateID);
                gates.Add(gate);
                SaveLocked(gate.ProjectID, gates);
            }
        }

        private List<ProjectGate> LoadLocked(string projectID)
        {
            string path = GatePath(projectID);
            if (!File.Exists(path)) return new();
            try { return JsonConvert.DeserializeObject<List<ProjectGate>>(File.ReadAllText(path)) ?? new(); }
            catch { return new(); }
        }

        private void SaveLocked(string projectID, List<ProjectGate> gates)
        {
            string path = GatePath(projectID);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(gates, Formatting.Indented));
            File.Move(tmp, path, overwrite: true);
        }
    }
}
