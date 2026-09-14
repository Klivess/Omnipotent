using System.Collections.Concurrent;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveAPI.Caching;

namespace Omnipotent.Services.Projects
{
    /// <summary>Why an agent left the roster. Only <see cref="CommanderRetired"/> is the agent's own
    /// work finishing; the other two are Klives taking a slot away mid-assignment.</summary>
    public enum ProjectAgentRetirementReason
    {
        /// <summary>The Commander retired a worker that had delivered (the ordinary lifecycle).</summary>
        CommanderRetired,
        /// <summary>Klives lowered the agent cap below the roster, so the harness reclaimed slots.</summary>
        CapLowered,
        /// <summary>Klives removed this specific agent by hand.</summary>
        KlivesRemoved,
        /// <summary>A parent was retired, so its one-level-deep helpers went with it.</summary>
        ParentRetired,
    }

    public enum ProjectHandoverStatus { Open, Claimed, Dropped }

    /// <summary>
    /// Everything a retired agent was holding, frozen at the moment its slot was taken away.
    ///
    /// A slot can now be removed while its agent is mid-assignment, which makes this the difference
    /// between "the cap went down" and "work silently vanished". The agent record itself is kept
    /// (retirement is a flag, not a delete), but a retired record stops being seeded, stops being
    /// messageable, and drops off the roster block — so without a handover the only trace of what it
    /// was doing would be scattered across thousands of log lines nobody rereads.
    ///
    /// This is deliberately a FLAT SNAPSHOT rather than a set of pointers: the resume action, the
    /// directive texts and the deliverable paths are copied in, because the durable structures they
    /// came from are keyed by agent ID and get cleaned up behind a retired agent.
    /// </summary>
    public sealed class ProjectAgentHandover
    {
        public string HandoverID { get; set; } = "";
        public string ProjectID { get; set; } = "";
        public string AgentID { get; set; } = "";
        public string Role { get; set; } = "";
        public string Tier { get; set; } = "";
        public string? ParentAgentID { get; set; }
        public ProjectAgentMissionKind MissionKind { get; set; } = ProjectAgentMissionKind.Task;
        public ProjectAgentWorkStatus WorkStatus { get; set; } = ProjectAgentWorkStatus.Idle;
        public ProjectAgentRetirementReason Reason { get; set; } = ProjectAgentRetirementReason.CommanderRetired;
        /// <summary>Who took the slot away: "klives", "commander", or "system".</summary>
        public string RetiredBy { get; set; } = "system";

        /// <summary>What it was told to accomplish.</summary>
        public string Objective { get; set; } = "";
        /// <summary>Grand Plan milestones it owned. Ownership is released on retirement, so these are
        /// exactly the milestones that just became unowned.</summary>
        public List<string> ActiveMilestoneIDs { get; set; } = new();
        /// <summary>Project-relative outputs it was expected to produce (finished or not).</summary>
        public List<string> DeliverablePaths { get; set; } = new();
        /// <summary>Its last report to the Commander, if it ever filed one.</summary>
        public string? LastReport { get; set; }
        public DateTime? LastReportAt { get; set; }
        public DateTime? LastWakeAt { get; set; }
        /// <summary>The exact next action from its durable checkpoint — the single most valuable line
        /// here, because it is the one sentence written to survive a context reset.</summary>
        public string? ResumeAction { get; set; }
        /// <summary>Preconditions recorded alongside the resume action.</summary>
        public List<string> ResumePreconditions { get; set; } = new();
        /// <summary>Open Klives directives that were addressed to this agent alone. They are re-pointed
        /// at the Commander on retirement; these are kept for the narrative.</summary>
        public List<ProjectHandoverDirective> OpenDirectives { get; set; } = new();
        /// <summary>Desktop containers disposed with it. Recorded because the browser session, the
        /// half-filled form and any un-copied /agent-runtime state died with them — the successor
        /// needs to know that, not discover it.</summary>
        public List<string> DisposedContainerIDs { get; set; } = new();
        /// <summary>True when a wake was cancelled mid-flight to free the slot.</summary>
        public bool InterruptedMidWake { get; set; }

        public DateTime RetiredAt { get; set; } = DateTime.UtcNow;
        public ProjectHandoverStatus Status { get; set; } = ProjectHandoverStatus.Open;
        /// <summary>Agent ID that picked this work up ("commander" or a worker), once claimed.</summary>
        public string? ClaimedBy { get; set; }
        public DateTime? ClaimedAt { get; set; }
        /// <summary>How the claimant says the work continues — or, for a drop, why it does not.</summary>
        public string? ClaimNote { get; set; }
    }

    /// <summary>A directive snapshot inside a handover: enough to act on without loading the store.</summary>
    public sealed class ProjectHandoverDirective
    {
        public string DirectiveID { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Text { get; set; } = "";
        public List<string> ExpectedArtifactPaths { get; set; } = new();
    }

    /// <summary>
    /// Durable handover records, one file per project: Projects/Handovers/&lt;projectID&gt;.handovers.json
    ///
    /// Bounded like every other per-project store — a project that churns workers for a year must not
    /// grow an unbounded file — but bounded by CLAIMED records only. An open handover is unfinished
    /// work and is never evicted to make room.
    /// </summary>
    public sealed class ProjectAgentHandoverStore
    {
        private readonly string dir;
        private readonly Action<string> log;
        private readonly ConcurrentDictionary<string, object> locks = new(StringComparer.Ordinal);

        /// <summary>Resolved handovers kept for the record after the open ones.</summary>
        public const int MaxResolvedPerProject = 60;
        /// <summary>Open handovers rendered into a wake seed. More than this and the roster block stops
        /// being a roster; the rest stay readable through manage_agents op:handovers.</summary>
        public const int MaxOpenInPrompt = 8;

        public ProjectAgentHandoverStore(Action<string> log, string? rootOverride = null)
        {
            this.log = log ?? (_ => { });
            dir = rootOverride ?? Path.Combine(
                OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsDirectory), "Handovers");
            Directory.CreateDirectory(dir);
        }

        private object LockFor(string projectID) => locks.GetOrAdd(projectID, _ => new object());
        public string GetPath(string projectID) => Path.Combine(dir, projectID + ".handovers.json");

        /// <summary>Response-cache dependency key — /projects/* GETs are cacheable, so an
        /// uninstrumented store is served stale forever. See ProjectSubAgentManager.CacheKey.</summary>
        private static string CacheKey(string projectID) => "projects:handovers:" + projectID;

        public ProjectAgentHandover Record(ProjectAgentHandover handover)
        {
            ArgumentNullException.ThrowIfNull(handover);
            if (string.IsNullOrWhiteSpace(handover.ProjectID))
                throw new ArgumentException("ProjectID required", nameof(handover));
            if (string.IsNullOrWhiteSpace(handover.HandoverID))
                handover.HandoverID = Guid.NewGuid().ToString("N")[..16];
            if (handover.RetiredAt == default) handover.RetiredAt = DateTime.UtcNow;

            lock (LockFor(handover.ProjectID))
            {
                var all = LoadLocked(handover.ProjectID);
                all.Add(handover);
                SaveLocked(handover.ProjectID, all);
                return Clone(handover);
            }
        }

        public List<ProjectAgentHandover> List(string projectID, bool openOnly = false)
        {
            lock (LockFor(projectID))
                return LoadLocked(projectID)
                    .Where(h => !openOnly || h.Status == ProjectHandoverStatus.Open)
                    .OrderByDescending(h => h.RetiredAt)
                    .Select(Clone)
                    .ToList();
        }

        public ProjectAgentHandover? Get(string projectID, string handoverID)
        {
            lock (LockFor(projectID))
                return LoadLocked(projectID)
                    .Where(h => string.Equals(h.HandoverID, handoverID, StringComparison.OrdinalIgnoreCase))
                    .Select(Clone)
                    .FirstOrDefault();
        }

        /// <summary>
        /// Closes one handover. <paramref name="dropped"/> records a deliberate decision NOT to continue
        /// the work, which is a legitimate outcome and must be distinguishable from work that was picked
        /// up — otherwise "claimed" stops meaning anything and the block cannot be trusted.
        /// </summary>
        public ProjectAgentHandover? Claim(string projectID, string handoverID, string claimedBy,
            string? note = null, bool dropped = false, DateTime? nowUtc = null)
        {
            lock (LockFor(projectID))
            {
                var all = LoadLocked(projectID);
                var item = all.FirstOrDefault(h =>
                    string.Equals(h.HandoverID, handoverID, StringComparison.OrdinalIgnoreCase));
                if (item == null) return null;
                if (item.Status != ProjectHandoverStatus.Open) return Clone(item);
                item.Status = dropped ? ProjectHandoverStatus.Dropped : ProjectHandoverStatus.Claimed;
                item.ClaimedBy = string.IsNullOrWhiteSpace(claimedBy) ? "commander" : claimedBy.Trim();
                item.ClaimedAt = nowUtc ?? DateTime.UtcNow;
                item.ClaimNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
                TrimResolvedLocked(all);
                SaveLocked(projectID, all);
                return Clone(item);
            }
        }

        /// <summary>
        /// The open-handover appendix for a wake seed.
        ///
        /// PREFIX CACHE (see PromptPrefixStability): this is deliberately rendered as a tail section of
        /// the TASK FORCE block rather than as a section of its own. A handover only ever appears at the
        /// same moment the roster it belongs to changed, so folding it in here costs no cache
        /// invalidation that retiring the agent had not already caused. It also carries no relative ages
        /// or live clock — absolute stamps only — so an unchanged set of handovers renders byte-identical
        /// wake after wake and the whole block keeps being served from cache.
        /// </summary>
        public string DescribeForPrompt(string projectID)
        {
            var open = List(projectID, openOnly: true);
            if (open.Count == 0) return "";

            var sb = new System.Text.StringBuilder();
            // The full procedure lives HERE, not in the system prompt. Doctrine is re-sent on every
            // wake of every project forever; this text is only assembled when there is actually an
            // open handover, so the guidance costs nothing in the overwhelmingly common case where
            // the roster has not changed. (See PromptPrefixStability: conditional instructions belong
            // in the conditional block.)
            sb.AppendLine($"UNCLAIMED WORK FROM RETIRED AGENTS ({open.Count}) — their slots are gone, their work is not. "
                + "This is the highest-priority unowned work in the project: handle it before you pick anything else up. "
                + "For each one, read it, then either take the work onto your own step ledger, reassign it to a remaining "
                + "worker with manage_agents op:assign_work, or deliberately drop it with a stated reason — and close it "
                + "with manage_agents op:claim_handover either way. Files these agents wrote to /project survive them; "
                + "their desktops do not, so resume from the checkpointed action rather than assuming a half-finished "
                + "screen is still waiting. Do not spawn replacements above your current cap.");
            foreach (var h in open.OrderBy(h => h.RetiredAt).Take(MaxOpenInPrompt))
            {
                sb.AppendLine($"[handover {h.HandoverID}] {h.Role} ({h.AgentID}), "
                    + $"{h.MissionKind.ToString().ToLowerInvariant()} mission, retired {TemporalFormat.StampMinute(h.RetiredAt)} "
                    + $"by {h.RetiredBy} — {DescribeReason(h.Reason)}. Was {h.WorkStatus}"
                    + (h.InterruptedMidWake ? ", INTERRUPTED mid-wake." : "."));
                if (!string.IsNullOrWhiteSpace(h.Objective))
                    sb.AppendLine($"  objective: {Clip(h.Objective, 240)}");
                if (h.ActiveMilestoneIDs.Count > 0)
                    sb.AppendLine($"  milestones now UNOWNED: {string.Join(", ", h.ActiveMilestoneIDs)}");
                if (h.DeliverablePaths.Count > 0)
                    sb.AppendLine($"  expected deliverables: {string.Join(", ", h.DeliverablePaths.Take(8))}");
                if (!string.IsNullOrWhiteSpace(h.ResumeAction))
                    sb.AppendLine($"  EXACT NEXT ACTION it had checkpointed: {Clip(h.ResumeAction!, 400)}");
                if (h.ResumePreconditions.Count > 0)
                    sb.AppendLine($"  preconditions: {string.Join("; ", h.ResumePreconditions.Take(5))}");
                if (!string.IsNullOrWhiteSpace(h.LastReport))
                    sb.AppendLine($"  last report {TemporalFormat.StampMinute(h.LastReportAt ?? h.RetiredAt)}: {Clip(h.LastReport!, 300)}");
                else
                    sb.AppendLine("  last report: (none — it never reported, so its progress is only in the log)");
                foreach (var d in h.OpenDirectives.Take(4))
                    sb.AppendLine($"  open Klives {d.Kind.ToLowerInvariant()} directive {d.DirectiveID} (now yours): {Clip(d.Text, 240)}");
                if (h.DisposedContainerIDs.Count > 0)
                    sb.AppendLine("  its desktop was destroyed with it: any live browser session, unsaved window state "
                        + "and machine-local runtime are gone. Files it had written to /project survive.");
            }
            if (open.Count > MaxOpenInPrompt)
                sb.AppendLine($"(+{open.Count - MaxOpenInPrompt} more — manage_agents op:handovers lists them all.)");
            return sb.ToString().TrimEnd();
        }

        public static string DescribeReason(ProjectAgentRetirementReason reason) => reason switch
        {
            ProjectAgentRetirementReason.CapLowered => "Klives lowered the agent cap and this slot was reclaimed",
            ProjectAgentRetirementReason.KlivesRemoved => "Klives removed this agent directly",
            ProjectAgentRetirementReason.ParentRetired => "its parent agent was retired, so it went with it",
            _ => "retired after finishing",
        };

        /// <summary>Trims only RESOLVED records. Open handovers are unfinished work and never evicted.</summary>
        private static void TrimResolvedLocked(List<ProjectAgentHandover> all)
        {
            var resolved = all.Where(h => h.Status != ProjectHandoverStatus.Open)
                .OrderByDescending(h => h.ClaimedAt ?? h.RetiredAt)
                .ToList();
            if (resolved.Count <= MaxResolvedPerProject) return;
            foreach (var drop in resolved.Skip(MaxResolvedPerProject))
                all.Remove(drop);
        }

        private List<ProjectAgentHandover> LoadLocked(string projectID)
        {
            CacheDeps.NoteRead(CacheKey(projectID));
            string path = GetPath(projectID);
            if (!File.Exists(path)) return new();
            try
            {
                return JsonConvert.DeserializeObject<List<ProjectAgentHandover>>(File.ReadAllText(path)) ?? new();
            }
            catch (Exception ex)
            {
                log($"Projects: handover store for {projectID} unreadable ({ex.Message}); starting a fresh list.");
                return new();
            }
        }

        private void SaveLocked(string projectID, List<ProjectAgentHandover> all)
        {
            string path = GetPath(projectID);
            string tmp = path + ".tmp";
            // fsync before rename: a torn handover file would lose exactly the record that exists to
            // stop work being lost. Same discipline as the runtime-state store.
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(JsonConvert.SerializeObject(all, Formatting.Indented));
                writer.Flush();
                stream.Flush(true);
            }
            File.Move(tmp, path, overwrite: true);
            CacheDeps.Bump(CacheKey(projectID)); // after the move — the new handover is now visible
        }

        private static ProjectAgentHandover Clone(ProjectAgentHandover source) =>
            JsonConvert.DeserializeObject<ProjectAgentHandover>(JsonConvert.SerializeObject(source))!;

        private static string Clip(string text, int max)
        {
            text = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return text.Length <= max ? text : text[..max] + "…";
        }
    }
}
