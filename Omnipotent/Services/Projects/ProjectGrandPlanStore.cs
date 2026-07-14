using System.Collections.Concurrent;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Projects
{
    /// <summary>Status of a single Grand Plan version.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum GrandPlanVersionStatus { PendingApproval, Approved, Rejected, Superseded }

    /// <summary>Live status of a plan milestone. Advanced in place via update_plan_progress (non-material).</summary>
    [JsonConverter(typeof(StringEnumConverter))]
        // Blocked is retained for old persisted plans; it is normalized to InProgress so agents
        // cannot use milestone state to stop project execution.
        public enum MilestoneStatus { Pending, InProgress, Done, Blocked }

    /// <summary>Assessed severity of a plan risk.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum RiskSeverity { Low, Medium, High }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum PlanRiskStatus { Open, Mitigated, Accepted }

    /// <summary>Validation state for an assumption that must hold before execution can advance.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum PlanPreconditionStatus { Unverified, Verified, Failed }

    /// <summary>Evidence supporting a milestone/criterion transition; references remain stable in the event/file/artifact stores.</summary>
    public class PlanEvidence
    {
        public string Summary { get; set; } = "";
        public long? EventSequence { get; set; }
        public List<string> ArtifactIDs { get; set; } = new();
        /// <summary>An approval-gate reference. Used when Klives explicitly accepts a risk in a
        /// material plan; agents cannot manufacture this reference through progress updates.</summary>
        public string? GateID { get; set; }
        public string RecordedBy { get; set; } = "";
        public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>A parallel track of work within the plan.</summary>
    public class PlanWorkstream
    {
        /// <summary>Store-assigned stable id (w1, w2, …) for the lifetime of this version.</summary>
        public string ID { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
    }

    /// <summary>A concrete, trackable milestone. Its <see cref="Status"/> is the living part of the plan.</summary>
    public class PlanMilestone
    {
        /// <summary>Store-assigned stable id (m1, m2, …), referenced by update_plan_progress.</summary>
        public string ID { get; set; } = "";
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        public MilestoneStatus Status { get; set; } = MilestoneStatus.Pending;
        /// <summary>Optional target date or condition, free text (e.g. "by end of week", "after data feed").</summary>
        public string? Target { get; set; }
        /// <summary>Display order within the plan.</summary>
        public int Order { get; set; }
        /// <summary>When the status was last changed.</summary>
        public DateTime? UpdatedAt { get; set; }
        public List<string> DependsOn { get; set; } = new();
        public string? OwnerAgentID { get; set; }
        public string? BlockReason { get; set; }
        public List<PlanEvidence> Evidence { get; set; } = new();
    }

    /// <summary>A definition-of-done criterion. <see cref="Met"/> is ticked in place as it is achieved.</summary>
    public class PlanCriterion
    {
        /// <summary>Store-assigned stable id (c1, c2, …), referenced by update_plan_progress.</summary>
        public string ID { get; set; } = "";
        public string Text { get; set; } = "";
        public bool Met { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public List<PlanEvidence> Evidence { get; set; } = new();
    }

    /// <summary>A known risk and its mitigation.</summary>
    public class PlanRisk
    {
        /// <summary>Store-assigned stable id (r1, r2, …).</summary>
        public string ID { get; set; } = "";
        public string Description { get; set; } = "";
        public RiskSeverity Severity { get; set; } = RiskSeverity.Medium;
        public string Mitigation { get; set; } = "";
        public PlanRiskStatus Status { get; set; } = PlanRiskStatus.Open;
        /// <summary>Legacy persisted field. Risks remain visible and auditable, but never gate execution.</summary>
        public bool BlocksExecution { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public List<PlanEvidence> Evidence { get; set; } = new();
    }

    public class PlanPrecondition
    {
        public string ID { get; set; } = "";
        public string Description { get; set; } = "";
        public string Verification { get; set; } = "";
        public PlanPreconditionStatus Status { get; set; } = PlanPreconditionStatus.Unverified;
        public DateTime? UpdatedAt { get; set; }
        public List<PlanEvidence> Evidence { get; set; } = new();
    }

    /// <summary>
    /// The structured content of a Grand Plan version — the analytical, trackable form of the plan.
    /// Authored whole on submit/amend; milestone statuses and criterion ticks are then updated in
    /// place on the current approved version (non-material progress, not a new version). The store
    /// also renders this to canonical markdown so text consumers (wake seeds, Discord report,
    /// get_grand_plan) keep working unchanged.
    /// </summary>
    public class GrandPlanContent
    {
        public string Mission { get; set; } = "";
        public List<PlanWorkstream> Workstreams { get; set; } = new();
        public List<PlanMilestone> Milestones { get; set; } = new();
        public List<PlanRisk> Risks { get; set; } = new();
        /// <summary>Go/no-go assumptions proven against reality before milestones may advance.</summary>
        public List<PlanPrecondition> Preconditions { get; set; } = new();
        public List<PlanCriterion> SuccessCriteria { get; set; } = new();
        /// <summary>Prose budget plan; live actuals come from the ProjectBudgetLedger, not here.</summary>
        public string BudgetPlan { get; set; } = "";
    }

    /// <summary>
    /// One version of a project's Grand Plan — the strategic north star Klives approves before work
    /// begins. Versions are append-only and monotonically numbered; approving a new version
    /// supersedes the previously-approved one. Material versions pass through an approval gate;
    /// non-material amendments are applied immediately.
    /// </summary>
    public class GrandPlanVersion
    {
        public int Version { get; set; }
        /// <summary>Structured, analytical plan content. Null on legacy versions authored before the structured model.</summary>
        public GrandPlanContent? Content { get; set; }
        /// <summary>Canonical markdown, rendered from <see cref="Content"/> on submit. Kept for text consumers and legacy versions.</summary>
        public string Markdown { get; set; } = "";
        /// <summary>Commander-authored ≤150-word summary used in wake seeds and the gate card.</summary>
        public string Summary { get; set; } = "";
        /// <summary>For amendments: what changed and why.</summary>
        public string? ChangeNote { get; set; }
        /// <summary>Whether this version required Klives' approval (opened a gate).</summary>
        public bool Material { get; set; } = true;
        public GrandPlanVersionStatus Status { get; set; } = GrandPlanVersionStatus.PendingApproval;
        public string? GateID { get; set; }
        public string? KlivesComment { get; set; }
        public string? SubmittedByWakeID { get; set; }
        public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ResolvedAt { get; set; }
    }

    /// <summary>The version history for one project's Grand Plan.</summary>
    public class GrandPlanDocument
    {
        public string ProjectID { get; set; } = "";
        public List<GrandPlanVersion> Versions { get; set; } = new();
    }

    /// <summary>
    /// Storage for a project's Grand Plan — one file per project, per-project lock, atomic tmp+move
    /// writes, fail-soft load (same shape as ProjectObservableStore).
    ///
    /// Layout: Projects/GrandPlans/&lt;projectID&gt;.grandplan.json
    /// </summary>
    public class ProjectGrandPlanStore
    {
        public const int MaxSummaryLength = 1200;

        private readonly Action<string> log;
        private readonly string dir;
        private readonly ConcurrentDictionary<string, object> locks = new(StringComparer.Ordinal);

        public ProjectGrandPlanStore(Action<string> log)
        {
            this.log = log ?? (_ => { });
            dir = OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsGrandPlansDirectory);
            Directory.CreateDirectory(dir);
        }

        private object LockFor(string projectID) => locks.GetOrAdd(projectID, _ => new object());
        private string PathFor(string projectID) => Path.Combine(dir, projectID + ".grandplan.json");

        /// <summary>
        /// Appends a new version (auto-numbered) from structured content. A material version enters
        /// PendingApproval; a non-material amendment is stored Approved immediately and supersedes the
        /// current one. Stable ids are assigned to milestones/criteria/risks/workstreams, and a
        /// canonical markdown mirror is rendered for text consumers. Returns a clone of the created version.
        /// </summary>
        public GrandPlanVersion SubmitVersion(string projectID, GrandPlanContent content, string summary,
            string? changeNote, bool material, string? wakeID)
        {
            if (content == null)
                throw new InvalidOperationException("Grand Plan must have a mission.");
            NormalizeContent(content);
            if (string.IsNullOrWhiteSpace(content.Mission))
                throw new InvalidOperationException("Grand Plan must have a mission.");
            lock (LockFor(projectID))
            {
                var doc = LoadLocked(projectID);
                var previous = CurrentApprovedLocked(doc)?.Content;
                AssignIds(content, previous);
                ValidateDependencyGraph(content);
                ValidateSubmittedState(content, previous, material);
                int next = doc.Versions.Count == 0 ? 1 : doc.Versions.Max(v => v.Version) + 1;
                var version = new GrandPlanVersion
                {
                    Version = next,
                    Content = content,
                    Markdown = RenderMarkdown(content),
                    Summary = Trim(summary ?? "", MaxSummaryLength),
                    ChangeNote = string.IsNullOrWhiteSpace(changeNote) ? null : changeNote.Trim(),
                    Material = material,
                    SubmittedByWakeID = wakeID,
                    SubmittedAt = DateTime.UtcNow,
                };
                if (!material)
                {
                    // Immediate amendment: supersede the current approved version, become approved.
                    foreach (var v in doc.Versions.Where(v => v.Status == GrandPlanVersionStatus.Approved))
                        v.Status = GrandPlanVersionStatus.Superseded;
                    version.Status = GrandPlanVersionStatus.Approved;
                    version.ResolvedAt = DateTime.UtcNow;
                }
                doc.Versions.Add(version);
                SaveLocked(projectID, doc);
                return Clone(version);
            }
        }

        public void MarkApproved(string projectID, int version, string? gateID, string? comment)
        {
            lock (LockFor(projectID))
            {
                var doc = LoadLocked(projectID);
                var v = doc.Versions.FirstOrDefault(x => x.Version == version)
                    ?? throw new InvalidOperationException($"No Grand Plan version {version}.");
                foreach (var other in doc.Versions.Where(x => x.Status == GrandPlanVersionStatus.Approved))
                    other.Status = GrandPlanVersionStatus.Superseded;
                v.Status = GrandPlanVersionStatus.Approved;
                v.GateID = gateID;
                v.KlivesComment = comment;
                v.ResolvedAt = DateTime.UtcNow;
                if (v.Content != null)
                {
                    foreach (var risk in v.Content.Risks.Where(r => r.Status == PlanRiskStatus.Accepted
                        && !HasEvidenceReference(r.Evidence)))
                    {
                        if (string.IsNullOrWhiteSpace(gateID))
                            throw new InvalidOperationException("An accepted risk requires a durable approval gate reference.");
                        risk.Evidence.Add(new PlanEvidence
                        {
                            Summary = "Risk explicitly accepted by Klives as part of this material Grand Plan approval.",
                            GateID = gateID,
                            RecordedBy = "klives",
                        });
                        risk.UpdatedAt = DateTime.UtcNow;
                    }
                    v.Markdown = RenderMarkdown(v.Content);
                }
                SaveLocked(projectID, doc);
            }
        }

        public void MarkRejected(string projectID, int version, string? gateID, string? comment)
        {
            lock (LockFor(projectID))
            {
                var doc = LoadLocked(projectID);
                var v = doc.Versions.FirstOrDefault(x => x.Version == version);
                if (v == null) return;
                v.Status = GrandPlanVersionStatus.Rejected;
                v.GateID = gateID;
                v.KlivesComment = comment;
                v.ResolvedAt = DateTime.UtcNow;
                SaveLocked(projectID, doc);
            }
        }

        /// <summary>
        /// Sets a milestone's status on the current approved version, in place (non-material progress —
        /// no new version, no gate). Matches by id first, then case-insensitively by title. Returns the
        /// updated milestone, or null if there is no approved plan / no match.
        /// </summary>
        public PlanMilestone? UpdateMilestoneStatus(string projectID, string milestoneRef, MilestoneStatus status,
            PlanEvidence? evidence = null, string? blockReason = null, string? ownerAgentID = null)
        {
            lock (LockFor(projectID))
            {
                var doc = LoadLocked(projectID);
                var v = CurrentApprovedLocked(doc);
                var m = v?.Content?.Milestones.FirstOrDefault(x =>
                    string.Equals(x.ID, milestoneRef, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x.Title, milestoneRef, StringComparison.OrdinalIgnoreCase));
                if (m == null) return null;
                if (status == MilestoneStatus.Blocked) status = MilestoneStatus.InProgress;
                if (status is MilestoneStatus.InProgress or MilestoneStatus.Done)
                {
                    var unmet = m.DependsOn.Where(dep => v!.Content!.Milestones
                        .FirstOrDefault(x => string.Equals(x.ID, dep, StringComparison.OrdinalIgnoreCase))?.Status != MilestoneStatus.Done).ToList();
                    if (unmet.Count > 0)
                        throw new InvalidOperationException($"Milestone '{m.Title}' cannot advance until dependencies are done: {string.Join(", ", unmet)}.");
                }
                if (status == MilestoneStatus.Done && !HasEvidenceReference(evidence)
                    && !HasEvidenceReference(m.Evidence))
                    throw new InvalidOperationException("A completed milestone requires evidence with a durable event, artifact, or approval-gate reference.");
                m.Status = status;
                if (!string.IsNullOrWhiteSpace(blockReason)) m.BlockReason = blockReason.Trim();
                else if (status is MilestoneStatus.Pending or MilestoneStatus.Done) m.BlockReason = null;
                if (!string.IsNullOrWhiteSpace(ownerAgentID)) m.OwnerAgentID = ownerAgentID.Trim();
                if (evidence != null) m.Evidence.Add(CloneJson(evidence));
                m.UpdatedAt = DateTime.UtcNow;
                SaveLocked(projectID, doc);
                return CloneJson(m);
            }
        }

        /// <summary>
        /// Marks a success criterion met/unmet on the current approved version, in place. Matches by id
        /// first, then case-insensitively by text. Returns the updated criterion, or null if no match.
        /// </summary>
        public PlanCriterion? SetCriterionMet(string projectID, string criterionRef, bool met, PlanEvidence? evidence = null)
        {
            lock (LockFor(projectID))
            {
                var doc = LoadLocked(projectID);
                var v = CurrentApprovedLocked(doc);
                var c = v?.Content?.SuccessCriteria.FirstOrDefault(x =>
                    string.Equals(x.ID, criterionRef, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x.Text, criterionRef, StringComparison.OrdinalIgnoreCase));
                if (c == null) return null;
                if (met && !HasEvidenceReference(evidence) && !HasEvidenceReference(c.Evidence))
                    throw new InvalidOperationException("A met success criterion requires evidence with a durable event, artifact, or approval-gate reference.");
                c.Met = met;
                if (evidence != null) c.Evidence.Add(CloneJson(evidence));
                c.UpdatedAt = DateTime.UtcNow;
                SaveLocked(projectID, doc);
                return CloneJson(c);
            }
        }

        public PlanPrecondition? SetPreconditionStatus(string projectID, string preconditionRef,
            PlanPreconditionStatus status, PlanEvidence evidence)
        {
            ArgumentNullException.ThrowIfNull(evidence);
            EnsureEvidenceReference(evidence, "A precondition decision");
            lock (LockFor(projectID))
            {
                var doc = LoadLocked(projectID);
                var v = CurrentApprovedLocked(doc);
                var p = v?.Content?.Preconditions.FirstOrDefault(x =>
                    string.Equals(x.ID, preconditionRef, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x.Description, preconditionRef, StringComparison.OrdinalIgnoreCase));
                if (p == null) return null;
                p.Status = status;
                p.Evidence.Add(CloneJson(evidence));
                p.UpdatedAt = DateTime.UtcNow;
                SaveLocked(projectID, doc);
                return CloneJson(p);
            }
        }

        public PlanRisk? SetRiskStatus(string projectID, string riskRef, PlanRiskStatus status, PlanEvidence evidence)
        {
            ArgumentNullException.ThrowIfNull(evidence);
            if (status == PlanRiskStatus.Open)
                throw new InvalidOperationException("Use Mitigated or Accepted when resolving a risk; reopening belongs in a plan amendment.");
            if (status == PlanRiskStatus.Accepted)
                throw new InvalidOperationException("Agents cannot accept a risk through a progress update. Put the acceptance in a material Grand Plan amendment for Klives to approve.");
            EnsureEvidenceReference(evidence, "A mitigated risk");
            lock (LockFor(projectID))
            {
                var doc = LoadLocked(projectID);
                var v = CurrentApprovedLocked(doc);
                var risk = v?.Content?.Risks.FirstOrDefault(x =>
                    string.Equals(x.ID, riskRef, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x.Description, riskRef, StringComparison.OrdinalIgnoreCase));
                if (risk == null) return null;
                risk.Status = status;
                risk.Evidence.Add(CloneJson(evidence));
                risk.UpdatedAt = DateTime.UtcNow;
                SaveLocked(projectID, doc);
                return CloneJson(risk);
            }
        }

        public GrandPlanVersion? GetCurrentApproved(string projectID)
        {
            lock (LockFor(projectID))
                return LoadLocked(projectID).Versions
                    .Where(v => v.Status == GrandPlanVersionStatus.Approved)
                    .OrderByDescending(v => v.Version)
                    .Select(Clone)
                    .FirstOrDefault();
        }

        public bool HasApprovedPlan(string projectID) => GetCurrentApproved(projectID) != null;

        public GrandPlanDocument Get(string projectID)
        {
            lock (LockFor(projectID))
            {
                var doc = LoadLocked(projectID);
                return JsonConvert.DeserializeObject<GrandPlanDocument>(JsonConvert.SerializeObject(doc))!;
            }
        }

        /// <summary>One-line summary for wake seeds: "GRAND PLAN v3 (approved 07-08): … (progress: 3/6 milestones, 2/4 criteria)" or "" when none.</summary>
        public string DescribeForSeed(string projectID)
        {
            var v = GetCurrentApproved(projectID);
            if (v == null) return "";
            string when = Data_Handling.TemporalFormat.StampWithAge(v.ResolvedAt ?? v.SubmittedAt);
            var ready = GetReadyMilestones(projectID);
            string readyText = ready.Count == 0 ? "" : $" Ready now: {string.Join(", ", ready.Take(5).Select(m => $"{m.ID} {m.Title}"))}.";
            return $"GRAND PLAN v{v.Version} (approved {when}): {v.Summary}{DescribeProgress(v.Content)}{readyText}";
        }

        /// <summary>Dependency-aware runnable frontier. Pending work is ready only when every
        /// predecessor is done; in-progress work remains ready for its current owner.</summary>
        public List<PlanMilestone> GetReadyMilestones(string projectID)
        {
            var v = GetCurrentApproved(projectID);
            if (v?.Content == null) return new();
            var byId = v.Content.Milestones.ToDictionary(m => m.ID, StringComparer.OrdinalIgnoreCase);
            return v.Content.Milestones
                .Where(m => m.Status is MilestoneStatus.Pending or MilestoneStatus.InProgress or MilestoneStatus.Blocked)
                .Where(m => m.DependsOn.All(dep => byId.TryGetValue(dep, out var prerequisite)
                    && prerequisite.Status == MilestoneStatus.Done && HasEvidenceReference(prerequisite.Evidence)))
                .OrderBy(m => m.Order).Select(CloneJson).ToList();
        }

        /// <summary>" (progress: 3/6 milestones, 2/4 criteria)" or "" when there's nothing to count.</summary>
        public static string DescribeProgress(GrandPlanContent? c)
        {
            if (c == null) return "";
            var parts = new List<string>();
            if (c.Milestones.Count > 0)
                parts.Add($"{c.Milestones.Count(m => m.Status == MilestoneStatus.Done)}/{c.Milestones.Count} milestones");
            if (c.Preconditions.Count > 0)
                parts.Add($"{c.Preconditions.Count(p => p.Status == PlanPreconditionStatus.Verified)}/{c.Preconditions.Count} preconditions");
            if (c.Risks.Count > 0)
                parts.Add($"{c.Risks.Count(r => r.Status != PlanRiskStatus.Open)}/{c.Risks.Count} risks resolved");
            if (c.SuccessCriteria.Count > 0)
                parts.Add($"{c.SuccessCriteria.Count(x => x.Met)}/{c.SuccessCriteria.Count} criteria");
            return parts.Count == 0 ? "" : $" (progress: {string.Join(", ", parts)})";
        }

        /// <summary>Machine-checkable completion readiness. Legacy plans without structured content
        /// remain user-reviewable, while structured plans must have every milestone and criterion done.</summary>
        public List<string> GetCompletionReadinessIssues(string projectID)
        {
            var v = GetCurrentApproved(projectID);
            if (v?.Content == null) return new List<string>();
            var issues = new List<string>();
            issues.AddRange(v.Content.Preconditions.Where(p => p.Status != PlanPreconditionStatus.Verified)
                .Select(p => $"precondition {p.ID} '{p.Description}' is {p.Status}"));
            issues.AddRange(v.Content.Preconditions.Where(p => p.Status == PlanPreconditionStatus.Verified && !HasEvidenceReference(p.Evidence))
                .Select(p => $"precondition {p.ID} '{p.Description}' has no verification evidence"));
            issues.AddRange(v.Content.Risks.Where(r => r.Status == PlanRiskStatus.Open && (r.Severity == RiskSeverity.High || r.BlocksExecution))
                .Select(r => $"risk {r.ID} '{r.Description}' remains open ({r.Severity}{(r.BlocksExecution ? ", blocking" : "")})"));
            issues.AddRange(v.Content.Risks.Where(r => r.Status != PlanRiskStatus.Open && !HasEvidenceReference(r.Evidence))
                .Select(r => $"risk {r.ID} '{r.Description}' is {r.Status} without evidence"));
            issues.AddRange(v.Content.Milestones.Where(m => m.Status != MilestoneStatus.Done)
                .Select(m => $"milestone {m.ID} '{m.Title}' is {m.Status}"));
            issues.AddRange(v.Content.SuccessCriteria.Where(c => !c.Met)
                .Select(c => $"criterion {c.ID} '{c.Text}' is unmet"));
            issues.AddRange(v.Content.Milestones.Where(m => m.Status == MilestoneStatus.Done && !HasEvidenceReference(m.Evidence))
                .Select(m => $"milestone {m.ID} '{m.Title}' has no completion evidence"));
            issues.AddRange(v.Content.SuccessCriteria.Where(c => c.Met && !HasEvidenceReference(c.Evidence))
                .Select(c => $"criterion {c.ID} '{c.Text}' has no evidence"));
            return issues;
        }

        // ── internals ──

        private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];

        private static GrandPlanVersion? CurrentApprovedLocked(GrandPlanDocument doc)
            => doc.Versions.Where(v => v.Status == GrandPlanVersionStatus.Approved)
                           .OrderByDescending(v => v.Version).FirstOrDefault();

        /// <summary>Assigns stable per-version ids (m1.., c1.., r1.., w1.., p1..) and milestone display order.</summary>
        private static void AssignIds(GrandPlanContent c, GrandPlanContent? previous)
        {
            int nextW = NextId(previous?.Workstreams.Select(x => x.ID), "w");
            int nextM = NextId(previous?.Milestones.Select(x => x.ID), "m");
            int nextR = NextId(previous?.Risks.Select(x => x.ID), "r");
            int nextC = NextId(previous?.SuccessCriteria.Select(x => x.ID), "c");
            int nextP = NextId(previous?.Preconditions.Select(x => x.ID), "p");
            foreach (var w in c.Workstreams)
                w.ID = previous?.Workstreams.FirstOrDefault(x => string.Equals(x.Name, w.Name, StringComparison.OrdinalIgnoreCase))?.ID ?? "w" + nextW++;
            for (int i = 0; i < c.Milestones.Count; i++)
            {
                var old = previous?.Milestones.FirstOrDefault(x => string.Equals(x.Title, c.Milestones[i].Title, StringComparison.OrdinalIgnoreCase));
                c.Milestones[i].ID = old?.ID ?? "m" + nextM++;
                c.Milestones[i].Order = i;
                if (old != null)
                {
                    if (c.Milestones[i].Status == MilestoneStatus.Pending) c.Milestones[i].Status = old.Status;
                    if (c.Milestones[i].Evidence.Count == 0) c.Milestones[i].Evidence = CloneJson(old.Evidence);
                    c.Milestones[i].OwnerAgentID ??= old.OwnerAgentID;
                    c.Milestones[i].BlockReason ??= old.BlockReason;
                }
            }
            foreach (var milestone in c.Milestones)
                milestone.DependsOn = milestone.DependsOn.Select(dep =>
                    c.Milestones.FirstOrDefault(x => string.Equals(x.ID, dep, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(x.Title, dep, StringComparison.OrdinalIgnoreCase))?.ID
                    ?? previous?.Milestones.FirstOrDefault(x => string.Equals(x.ID, dep, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(x.Title, dep, StringComparison.OrdinalIgnoreCase))?.ID
                    ?? dep).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var r in c.Risks)
            {
                var old = previous?.Risks.FirstOrDefault(x => string.Equals(x.Description, r.Description, StringComparison.OrdinalIgnoreCase));
                r.ID = old?.ID ?? "r" + nextR++;
                if (old != null)
                {
                    if (r.Status == PlanRiskStatus.Open) r.Status = old.Status;
                    if (r.Evidence.Count == 0) r.Evidence = CloneJson(old.Evidence);
                    r.BlocksExecution = false;
                }
            }
            foreach (var precondition in c.Preconditions)
            {
                var old = previous?.Preconditions.FirstOrDefault(x =>
                    string.Equals(x.Description, precondition.Description, StringComparison.OrdinalIgnoreCase));
                precondition.ID = old?.ID ?? "p" + nextP++;
                if (old != null)
                {
                    if (precondition.Status == PlanPreconditionStatus.Unverified) precondition.Status = old.Status;
                    if (precondition.Evidence.Count == 0) precondition.Evidence = CloneJson(old.Evidence);
                }
            }
            foreach (var criterion in c.SuccessCriteria)
            {
                var old = previous?.SuccessCriteria.FirstOrDefault(x => string.Equals(x.Text, criterion.Text, StringComparison.OrdinalIgnoreCase));
                criterion.ID = old?.ID ?? "c" + nextC++;
                if (old != null)
                {
                    criterion.Met |= old.Met;
                    if (criterion.Evidence.Count == 0) criterion.Evidence = CloneJson(old.Evidence);
                }
            }
        }

        private static int NextId(IEnumerable<string>? ids, string prefix)
        {
            int max = 0;
            foreach (var id in ids ?? Array.Empty<string>())
                if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(id[prefix.Length..], out int n)) max = Math.Max(max, n);
            return max + 1;
        }

        private static void ValidateDependencyGraph(GrandPlanContent content)
        {
            var byId = content.Milestones.ToDictionary(m => m.ID, StringComparer.OrdinalIgnoreCase);
            foreach (var milestone in content.Milestones)
            {
                foreach (string dependency in milestone.DependsOn)
                {
                    if (!byId.ContainsKey(dependency))
                        throw new InvalidOperationException($"Milestone '{milestone.Title}' depends on unknown milestone '{dependency}'.");
                    if (string.Equals(milestone.ID, dependency, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Milestone '{milestone.Title}' cannot depend on itself.");
                }
            }

            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool Visit(string id)
            {
                if (visited.Contains(id)) return false;
                if (!visiting.Add(id)) return true;
                foreach (string dependency in byId[id].DependsOn)
                    if (Visit(dependency)) return true;
                visiting.Remove(id);
                visited.Add(id);
                return false;
            }
            if (byId.Keys.Any(Visit))
                throw new InvalidOperationException("Grand Plan milestone dependencies contain a cycle.");
        }

        private static void ValidateSubmittedState(GrandPlanContent content, GrandPlanContent? previous, bool material)
        {
            foreach (var milestone in content.Milestones)
            {
                if (milestone.Status == MilestoneStatus.Done && !HasEvidenceReference(milestone.Evidence))
                    throw new InvalidOperationException($"Milestone '{milestone.Title}' is marked done without durable evidence. Carry forward evidence-backed state or submit it pending.");
            }
            foreach (var criterion in content.SuccessCriteria.Where(c => c.Met && !HasEvidenceReference(c.Evidence)))
                throw new InvalidOperationException($"Success criterion '{criterion.Text}' is marked met without durable evidence.");
            foreach (var precondition in content.Preconditions.Where(p => p.Status != PlanPreconditionStatus.Unverified
                && !HasEvidenceReference(p.Evidence)))
                throw new InvalidOperationException($"Precondition '{precondition.Description}' has a terminal status without durable evidence.");
            foreach (var risk in content.Risks.Where(r => r.Status == PlanRiskStatus.Mitigated
                && !HasEvidenceReference(r.Evidence)))
                throw new InvalidOperationException($"Risk '{risk.Description}' is marked mitigated without durable evidence.");

            foreach (var accepted in content.Risks.Where(r => r.Status == PlanRiskStatus.Accepted))
            {
                bool wasAlreadyAccepted = previous?.Risks.Any(old =>
                    string.Equals(old.Description, accepted.Description, StringComparison.OrdinalIgnoreCase)
                    && old.Status == PlanRiskStatus.Accepted && HasEvidenceReference(old.Evidence)) == true;
                if (!wasAlreadyAccepted && !material)
                    throw new InvalidOperationException($"Accepting risk '{accepted.Description}' is material and requires Klives' approval.");
            }
        }

        private static bool HasEvidenceReference(PlanEvidence? evidence) => evidence != null
            && !string.IsNullOrWhiteSpace(evidence.Summary)
            && (evidence.EventSequence is > 0 || evidence.ArtifactIDs.Any(x => !string.IsNullOrWhiteSpace(x))
                || !string.IsNullOrWhiteSpace(evidence.GateID));

        private static bool HasEvidenceReference(IEnumerable<PlanEvidence>? evidence) =>
            evidence?.Any(HasEvidenceReference) == true;

        private static void EnsureEvidenceReference(PlanEvidence evidence, string subject)
        {
            if (!HasEvidenceReference(evidence))
                throw new InvalidOperationException($"{subject} requires a concise summary and a durable event, artifact, or approval-gate reference.");
        }

        /// <summary>Renders structured content to canonical markdown for text consumers (wake seeds, Discord, get_grand_plan).</summary>
        public static string RenderMarkdown(GrandPlanContent c)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("## Mission").AppendLine(c.Mission.Trim()).AppendLine();
            if (c.Workstreams.Count > 0)
            {
                sb.AppendLine("## Workstreams");
                foreach (var w in c.Workstreams)
                    sb.AppendLine($"- **{w.Name}** — {w.Description}".TrimEnd(' ', '—'));
                sb.AppendLine();
            }
            if (c.Milestones.Count > 0)
            {
                sb.AppendLine("## Milestones");
                foreach (var m in c.Milestones)
                {
                    string box = m.Status switch
                    {
                        MilestoneStatus.Done => "[x]",
                        MilestoneStatus.InProgress => "[~]",
                        MilestoneStatus.Blocked => "[~]", // legacy value: treated as active work
                        _ => "[ ]",
                    };
                    string tail = string.IsNullOrWhiteSpace(m.Detail) ? "" : $" — {m.Detail}";
                    string target = string.IsNullOrWhiteSpace(m.Target) ? "" : $" (target: {m.Target})";
                    string owner = string.IsNullOrWhiteSpace(m.OwnerAgentID) ? "" : $" (owner: {m.OwnerAgentID})";
                    string blocked = string.IsNullOrWhiteSpace(m.BlockReason) ? "" : $" — OBSERVATION: {m.BlockReason}";
                    string deps = m.DependsOn.Count == 0 ? "" : $" (depends on: {string.Join(", ", m.DependsOn)})";
                    sb.AppendLine($"- {box} **{m.Title}**{tail}{target}{owner}{deps}{blocked}" +
                        (m.Evidence.Count > 0 ? $" [evidence: {m.Evidence.Count}]" : ""));
                }
                sb.AppendLine();
            }
            if (c.Preconditions.Count > 0)
            {
                sb.AppendLine("## Preconditions");
                foreach (var p in c.Preconditions)
                {
                    string box = p.Status == PlanPreconditionStatus.Verified ? "[x]"
                        : p.Status == PlanPreconditionStatus.Failed ? "[!]" : "[ ]";
                    sb.AppendLine($"- {box} **{p.Description}**" +
                        (string.IsNullOrWhiteSpace(p.Verification) ? "" : $" — verify: {p.Verification}") +
                        (p.Evidence.Count > 0 ? $" [evidence: {p.Evidence.Count}]" : ""));
                }
                sb.AppendLine();
            }
            if (c.Risks.Count > 0)
            {
                sb.AppendLine("## Risks");
                foreach (var r in c.Risks)
                    sb.AppendLine($"- **[{r.Severity}/{r.Status}]** {r.Description}" +
                        (string.IsNullOrWhiteSpace(r.Mitigation) ? "" : $" → {r.Mitigation}") +
                        (r.Evidence.Count > 0 ? $" [evidence: {r.Evidence.Count}]" : ""));
                sb.AppendLine();
            }
            if (c.SuccessCriteria.Count > 0)
            {
                sb.AppendLine("## Success criteria");
                foreach (var x in c.SuccessCriteria)
                    sb.AppendLine($"- {(x.Met ? "[x]" : "[ ]")} {x.Text}");
                sb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(c.BudgetPlan))
                sb.AppendLine("## Budget plan").AppendLine(c.BudgetPlan.Trim());
            return sb.ToString().TrimEnd();
        }

        private static T CloneJson<T>(T v) => JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(v))!;

        private static GrandPlanVersion Clone(GrandPlanVersion v)
            => JsonConvert.DeserializeObject<GrandPlanVersion>(JsonConvert.SerializeObject(v))!;

        private GrandPlanDocument LoadLocked(string projectID)
        {
            string path = PathFor(projectID);
            if (!File.Exists(path)) return new GrandPlanDocument { ProjectID = projectID };
            try
            {
                var document = JsonConvert.DeserializeObject<GrandPlanDocument>(File.ReadAllText(path))
                               ?? new GrandPlanDocument { ProjectID = projectID };
                NormalizeDocument(document, projectID);
                return document;
            }
            catch (Exception ex)
            {
                log($"ProjectGrandPlanStore: failed to load {path} ({ex.Message}) — starting empty, file preserved.");
                return new GrandPlanDocument { ProjectID = projectID };
            }
        }

        /// <summary>Old plan JSON predates several structured collections. Newtonsoft can also
        /// assign an explicit JSON null over a property initializer, so normalize every loaded or
        /// submitted graph before planning logic enumerates it.</summary>
        private static void NormalizeDocument(GrandPlanDocument document, string projectID)
        {
            document.ProjectID = string.IsNullOrWhiteSpace(document.ProjectID) ? projectID : document.ProjectID;
            document.Versions ??= new();
            document.Versions = document.Versions.Where(v => v != null).ToList();
            foreach (var version in document.Versions)
            {
                version.Markdown ??= "";
                version.Summary ??= "";
                if (version.Content != null) NormalizeContent(version.Content);
            }
        }

        private static void NormalizeContent(GrandPlanContent content)
        {
            content.Mission ??= "";
            content.BudgetPlan ??= "";
            content.Workstreams ??= new();
            content.Milestones ??= new();
            content.Risks ??= new();
            content.Preconditions ??= new();
            content.SuccessCriteria ??= new();
            content.Workstreams = content.Workstreams.Where(x => x != null).ToList();
            content.Milestones = content.Milestones.Where(x => x != null).ToList();
            content.Risks = content.Risks.Where(x => x != null).ToList();
            content.Preconditions = content.Preconditions.Where(x => x != null).ToList();
            content.SuccessCriteria = content.SuccessCriteria.Where(x => x != null).ToList();
            foreach (var milestone in content.Milestones)
            {
                // Historical plans could mark a milestone Blocked. Preserve its narrative
                // observation, but keep it runnable so an agent cannot stop the project.
                if (milestone.Status == MilestoneStatus.Blocked)
                    milestone.Status = MilestoneStatus.InProgress;
                milestone.DependsOn ??= new();
                milestone.Evidence ??= new();
                NormalizeEvidence(milestone.Evidence);
            }
            foreach (var precondition in content.Preconditions)
            {
                precondition.Evidence ??= new();
                NormalizeEvidence(precondition.Evidence);
            }
            foreach (var risk in content.Risks)
            {
                // Historical risk metadata remains useful for reporting, but it has no
                // authority to gate a project's execution.
                risk.BlocksExecution = false;
                risk.Evidence ??= new();
                NormalizeEvidence(risk.Evidence);
            }
            foreach (var criterion in content.SuccessCriteria)
            {
                criterion.Evidence ??= new();
                NormalizeEvidence(criterion.Evidence);
            }
        }

        private static void NormalizeEvidence(List<PlanEvidence> evidence)
        {
            evidence.RemoveAll(x => x == null);
            foreach (var item in evidence)
            {
                item.Summary ??= "";
                item.RecordedBy ??= "";
                item.ArtifactIDs ??= new();
            }
        }

        private void SaveLocked(string projectID, GrandPlanDocument doc)
        {
            doc.ProjectID = projectID;
            string path = PathFor(projectID);
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(doc, Formatting.Indented));
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(tmp, path, overwrite: true); break; }
                catch (Exception ex) when (attempt < 5 &&
                    (ex is IOException || ex is UnauthorizedAccessException))
                {
                    Thread.Sleep(15 * (attempt + 1));
                }
            }
        }
    }
}
