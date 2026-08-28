using System.Collections.Concurrent;
using System.Text;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveAPI.Caching;

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
        /// <summary>
        /// How many times Klives has commented on this gate without approving or denying it. Discussion
        /// used to release the waiting agent and then leave nothing on the gate at all, so the comment lived
        /// only in the event log and the request looked untouched.
        /// </summary>
        public int DiscussionCount { get; set; }
        public DateTime? LastDiscussedAt { get; set; }
        public List<string> DiscussionComments { get; set; } = new();
        /// <summary>
        /// Identity of the REQUEST rather than of this gate, so a second identical ask can be recognised as
        /// the same question. Without it the Commander — which never saw its own pending approvals — could
        /// open a fresh "Complete the project?" card next to the one still waiting.
        /// </summary>
        public string DedupeHash { get; set; } = "";
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

        private readonly ConcurrentDictionary<string, int> pendingCounts = new(StringComparer.Ordinal);

        private object LockFor(string projectID) => locks.GetOrAdd(projectID, _ => new object());
        private string GatePath(string projectID) => Path.Combine(dir, projectID + ".gates.json");

        // Pending approvals are read by /projects/list and the detail page, so gate writes have to
        // invalidate those cached responses.
        private static string CacheKey(string projectID) => "projects:gates:" + projectID;

        /// <summary>
        /// Opens a gate and awaits its resolution. The calling agent's turn suspends here until
        /// Klives approves/denies/discusses (or <paramref name="ct"/> cancels the wake).
        /// </summary>
        public async Task<GateResolution> OpenGateAndWaitAsync(ProjectGate gate, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(gate.GateID)) gate.GateID = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(gate.DedupeHash)) gate.DedupeHash = ComputeDedupeHash(gate.Kind, gate.Title, gate.Description);
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
                var gates = LoadLocked(projectID);
                gate = gates.FirstOrDefault(g => g.GateID == gateID && !g.Resolved);
                if (gate == null) return false;
                // Persist the discussion. It used to be released to the waiting agent and then dropped, so
                // an unresolved-but-discussed gate was indistinguishable from an untouched one and Klives'
                // comment survived only as a log line that scrolled out of the seed.
                gate.DiscussionCount++;
                gate.LastDiscussedAt = DateTime.UtcNow;
                if (!string.IsNullOrWhiteSpace(comment))
                {
                    gate.DiscussionComments.Add(comment.Trim());
                    if (gate.DiscussionComments.Count > MaxDiscussionComments)
                        gate.DiscussionComments.RemoveAt(0);
                }
                SaveLocked(projectID, gates);
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

        /// <summary>How many of Klives' comments on one gate are kept.</summary>
        private const int MaxDiscussionComments = 6;

        /// <summary>How many recently resolved approvals the wake seed recalls.</summary>
        private const int SeededResolvedGates = 4;

        /// <summary>
        /// Identity of the request being asked, so the same question opens one gate rather than several.
        /// Normalised on kind + title + description, since those are what Klives actually reads on the card.
        /// </summary>
        public static string ComputeDedupeHash(string? kind, string? title, string? description)
        {
            string normalized = string.Join("", new[] { kind, title, description }
                .Select(x => System.Text.RegularExpressions.Regex.Replace(x ?? "", @"\s+", " ").Trim().ToLowerInvariant()));
            return Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16].ToLowerInvariant();
        }

        /// <summary>
        /// An unresolved gate asking the same thing, if one exists. Callers use this to answer "you already
        /// asked" instead of opening a duplicate card.
        /// </summary>
        public ProjectGate? TryFindOpenDuplicate(string projectID, string dedupeHash)
        {
            if (string.IsNullOrWhiteSpace(dedupeHash)) return null;
            lock (LockFor(projectID))
            {
                return LoadLocked(projectID).FirstOrDefault(g => !g.Resolved
                    && string.Equals(g.DedupeHash, dedupeHash, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>
        /// Pending and recently resolved approvals for the wake seed. The Commander previously had no view of
        /// its own approval state at all: a request it had already made, and a decision Klives had already
        /// given, were both only event-log lines that aged out of the recent window.
        /// </summary>
        public string DescribeForWake(string projectID, DateTime? nowUtc = null)
        {
            DateTime now = nowUtc ?? DateTime.UtcNow;
            List<ProjectGate> gates;
            lock (LockFor(projectID)) gates = LoadLocked(projectID);
            if (gates.Count == 0) return "";

            var sb = new StringBuilder();
            foreach (var gate in gates.Where(g => !g.Resolved).OrderBy(g => g.CreatedAt))
            {
                // Absolute stamps throughout this block: it is seeded high in every wake, so a
                // recomputed relative age would change its bytes on every wake and cost a re-prefill
                // of the whole seed behind it even when no approval had moved. The agent reads
                // staleness off the seed's 'Now:' line. See PromptPrefixStability.
                sb.AppendLine($"PENDING: \"{gate.Title}\" ({gate.Kind}) opened {TemporalFormat.StampMinute(gate.CreatedAt)}" +
                    (gate.DiscussionCount > 0
                        ? $"; Klives has commented {gate.DiscussionCount}× without deciding: \"{string.Join("\" / \"", gate.DiscussionComments.TakeLast(2))}\""
                        : "; no response from Klives yet"));
                sb.AppendLine("  → Do NOT re-open this request. Address his comment, or work another step and use reply_to_klives.");
            }
            foreach (var gate in gates.Where(g => g.Resolved)
                .OrderByDescending(g => g.ResolvedAt ?? g.CreatedAt).Take(SeededResolvedGates))
            {
                sb.AppendLine($"RESOLVED: \"{gate.Title}\" — {gate.Decision} {TemporalFormat.StampMinute(gate.ResolvedAt ?? gate.CreatedAt)}" +
                    (string.IsNullOrWhiteSpace(gate.Comment) ? "" : $": \"{gate.Comment}\""));
            }
            return sb.ToString().TrimEnd();
        }

        public List<ProjectGate> ListPending(string projectID)
        {
            CacheDeps.NoteRead(CacheKey(projectID));
            lock (LockFor(projectID))
            {
                var pending = LoadLocked(projectID).Where(g => !g.Resolved).ToList();
                pendingCounts[projectID] = pending.Count;
                return pending;
            }
        }

        /// <summary>How many approvals are waiting on Klives, served from memory. The fleet list
        /// shows only the number, and asking for it must not mean parsing every project's gate
        /// file on every refresh.</summary>
        public int CountPending(string projectID)
        {
            CacheDeps.NoteRead(CacheKey(projectID));
            if (pendingCounts.TryGetValue(projectID, out int cached)) return cached;
            return ListPending(projectID).Count;
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
            try
            {
                var gates = JsonConvert.DeserializeObject<List<ProjectGate>>(File.ReadAllText(path)) ?? new();
                foreach (var gate in gates)
                {
                    gate.DiscussionComments ??= new();
                    // Backfill for gates written before dedupe existed, so an already-pending request from
                    // an older build still suppresses a duplicate ask.
                    if (string.IsNullOrWhiteSpace(gate.DedupeHash))
                        gate.DedupeHash = ComputeDedupeHash(gate.Kind, gate.Title, gate.Description);
                }
                return gates;
            }
            catch { return new(); }
        }

        private void SaveLocked(string projectID, List<ProjectGate> gates)
        {
            string path = GatePath(projectID);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(gates, Formatting.Indented));
            File.Move(tmp, path, overwrite: true);
            // Single write chokepoint: opening and resolving both land here, so the count and the
            // cached responses that show it are refreshed together.
            pendingCounts[projectID] = gates.Count(g => !g.Resolved);
            CacheDeps.Bump(CacheKey(projectID));
        }
    }
}
