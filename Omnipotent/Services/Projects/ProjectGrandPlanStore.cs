using System.Collections.Concurrent;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Projects
{
    /// <summary>Status of a single Grand Plan version.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum GrandPlanVersionStatus { PendingApproval, Approved, Rejected, Superseded }

    /// <summary>
    /// One version of a project's Grand Plan — the strategic north star Klives approves before work
    /// begins. Versions are append-only and monotonically numbered; approving a new version
    /// supersedes the previously-approved one. Material versions pass through an approval gate;
    /// non-material amendments are applied immediately.
    /// </summary>
    public class GrandPlanVersion
    {
        public int Version { get; set; }
        /// <summary>The full plan: mission, workstreams, milestones, risks, budget plan, success criteria.</summary>
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
        /// Appends a new version (auto-numbered). A material version enters PendingApproval; a
        /// non-material amendment is stored Approved immediately and supersedes the current one.
        /// Returns a clone of the created version.
        /// </summary>
        public GrandPlanVersion SubmitVersion(string projectID, string markdown, string summary,
            string? changeNote, bool material, string? wakeID)
        {
            if (string.IsNullOrWhiteSpace(markdown))
                throw new InvalidOperationException("Grand Plan markdown cannot be empty.");
            lock (LockFor(projectID))
            {
                var doc = LoadLocked(projectID);
                int next = doc.Versions.Count == 0 ? 1 : doc.Versions.Max(v => v.Version) + 1;
                var version = new GrandPlanVersion
                {
                    Version = next,
                    Markdown = markdown,
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

        /// <summary>One-line summary for wake seeds: "GRAND PLAN v3 (approved 07-08): …" or "" when none.</summary>
        public string DescribeForSeed(string projectID)
        {
            var v = GetCurrentApproved(projectID);
            if (v == null) return "";
            string when = (v.ResolvedAt ?? v.SubmittedAt).ToString("MM-dd");
            return $"GRAND PLAN v{v.Version} (approved {when}): {v.Summary}";
        }

        // ── internals ──

        private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];

        private static GrandPlanVersion Clone(GrandPlanVersion v)
            => JsonConvert.DeserializeObject<GrandPlanVersion>(JsonConvert.SerializeObject(v))!;

        private GrandPlanDocument LoadLocked(string projectID)
        {
            string path = PathFor(projectID);
            if (!File.Exists(path)) return new GrandPlanDocument { ProjectID = projectID };
            try
            {
                return JsonConvert.DeserializeObject<GrandPlanDocument>(File.ReadAllText(path))
                       ?? new GrandPlanDocument { ProjectID = projectID };
            }
            catch (Exception ex)
            {
                log($"ProjectGrandPlanStore: failed to load {path} ({ex.Message}) — starting empty, file preserved.");
                return new GrandPlanDocument { ProjectID = projectID };
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
