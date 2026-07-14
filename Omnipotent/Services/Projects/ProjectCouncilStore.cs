using System.Collections.Concurrent;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Projects
{
    /// <summary>Lifecycle of a convened council.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum CouncilStatus { Running, Completed, Failed, Cancelled }

        /// <summary>Decision semantics are advisory: councils surface evidence and recommendations, never veto a project.</summary>
        [JsonConverter(typeof(StringEnumConverter))]
        public enum CouncilRecommendationClass
        {
            Unclassified,
            Proceed,
            ProceedWithConditions,
            NeedsUserDecision,
            // Retained solely so historical council transcripts deserialize. New verdicts are
            // normalized to NeedsUserDecision because a council has no veto authority.
            [Obsolete("Historical persistence value only; councils cannot block projects.")]
            HardPolicyBlock,
        }

    /// <summary>One panelist turn (or the Chair's synthesis) in a council deliberation.</summary>
    public class CouncilStatement
    {
        /// <summary>Panelist role ("Strategist", "Skeptic", …) or "Chair".</summary>
        public string Role { get; set; } = "";
        /// <summary>1 = opening, 2 = rebuttal, 3 = chair synthesis.</summary>
        public int Round { get; set; }
        public string Text { get; set; } = "";
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public long PromptTokens { get; set; }
        public long CompletionTokens { get; set; }
        public double CostUsd { get; set; }
    }

    /// <summary>
    /// One adversarial council: a panel of role-played LLM seats the Commander convenes for a
    /// high-stakes decision (grand-plan drafting, strategy pivots, big spends, surprising events).
    /// Panelists deliberate over the Commander-supplied briefing ONLY; a Chair synthesizes a
    /// verdict that becomes the tool result. Full transcript lives here (event payloads are
    /// truncated to 32 KB, so they carry only excerpts).
    /// </summary>
    public class CouncilSession
    {
        public string CouncilID { get; set; } = "";
        public string ProjectID { get; set; } = "";
        /// <summary>The Commander wake that raised this council; null if raised outside a wake.</summary>
        public string? WakeID { get; set; }
        /// <summary>"commander" (future: "klives").</summary>
        public string ConvenedBy { get; set; } = "commander";
        /// <summary>Free-text intent: "planning" | "decision" | "event".</summary>
        public string Purpose { get; set; } = "decision";
        public string Topic { get; set; } = "";
        public string Briefing { get; set; } = "";
        /// <summary>Stable hash of the normalized topic + briefing. It lets the store reuse an
        /// identical deliberation before another seven model calls are spent.</summary>
        public string InputFingerprint { get; set; } = "";
        /// <summary>"routine" | "elevated" | "critical".</summary>
        public string Urgency { get; set; } = "routine";
        /// <summary>The panelist roles (excludes the always-present Chair).</summary>
        public List<string> Roles { get; set; } = new();
        public string Model { get; set; } = "";
        public CouncilStatus Status { get; set; } = CouncilStatus.Running;
        public List<CouncilStatement> Statements { get; set; } = new();
        /// <summary>The Chair's synthesis, verbatim — the product returned to the Commander.</summary>
        public string? VerdictText { get; set; }
        public CouncilRecommendationClass RecommendationClass { get; set; } = CouncilRecommendationClass.Unclassified;
        public double TotalCostUsd { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// Storage for a project's councils — one file per project, per-project lock, atomic tmp+move
    /// writes, fail-soft load (same shape as ProjectObservableStore). Sessions are trimmed to a cap
    /// so a council-happy project can't grow the file unbounded.
    ///
    /// Layout: Projects/Councils/&lt;projectID&gt;.councils.json
    /// </summary>
    public class ProjectCouncilStore
    {
        public const int MaxSessionsPerProject = 200;

        private readonly Action<string> log;
        private readonly string dir;
        private readonly ConcurrentDictionary<string, object> locks = new(StringComparer.Ordinal);

        public ProjectCouncilStore(Action<string> log)
        {
            this.log = log ?? (_ => { });
            dir = OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsCouncilsDirectory);
            Directory.CreateDirectory(dir);
        }

        private object LockFor(string projectID) => locks.GetOrAdd(projectID, _ => new object());
        private string PathFor(string projectID) => Path.Combine(dir, projectID + ".councils.json");

        /// <summary>Persists a new session (assigns a CouncilID if unset). Returns a clone.</summary>
        public CouncilSession Create(CouncilSession session)
        {
            NormalizeLegacyRecommendation(session);
            if (string.IsNullOrWhiteSpace(session.CouncilID)) session.CouncilID = Guid.NewGuid().ToString("N");
            lock (LockFor(session.ProjectID))
            {
                var all = LoadLocked(session.ProjectID);
                all.RemoveAll(s => s.CouncilID == session.CouncilID);
                all.Add(session);
                TrimLocked(all);
                SaveLocked(session.ProjectID, all);
                return Clone(session);
            }
        }

        /// <summary>
        /// Atomically applies duplicate/wake/day guardrails and persists a new running session.
        /// Keeping the checks and append under one project lock prevents overlapping wakes from
        /// both observing spare capacity and overspending it.
        /// </summary>
        public CouncilSession? TryCreateWithGuards(CouncilSession session, int maxPerWake, int maxPerDay,
            out string? reason)
        {
            NormalizeLegacyRecommendation(session);
            reason = null;
            lock (LockFor(session.ProjectID))
            {
                var all = LoadLocked(session.ProjectID);
                DateTime now = DateTime.UtcNow;
                if (!string.IsNullOrWhiteSpace(session.InputFingerprint))
                {
                    var duplicate = all.OrderByDescending(s => s.CreatedAt).FirstOrDefault(s =>
                        string.Equals(s.InputFingerprint, session.InputFingerprint, StringComparison.Ordinal)
                        && s.CreatedAt >= now.AddHours(-24)
                        && s.Status is CouncilStatus.Running or CouncilStatus.Completed);
                    if (duplicate != null)
                    {
                        reason = $"A matching council already considered this topic and briefing (council {duplicate.CouncilID}). " +
                            "Use its advisory verdict, or supply a materially updated briefing containing new evidence or a changed decision.";
                        return null;
                    }
                }
                if (maxPerWake > 0 && !string.IsNullOrEmpty(session.WakeID)
                    && all.Count(s => s.WakeID == session.WakeID) >= maxPerWake)
                {
                    reason = $"Council limit for this wake reached ({maxPerWake}). Continue with the advisory verdicts already available, or try again next wake.";
                    return null;
                }
                if (maxPerDay > 0 && all.Count(s => s.CreatedAt >= now.Date) >= maxPerDay)
                {
                    reason = $"Daily council limit reached ({maxPerDay}). Councils cost real tokens; continue with the evidence already available, or try again tomorrow.";
                    return null;
                }

                if (string.IsNullOrWhiteSpace(session.CouncilID)) session.CouncilID = Guid.NewGuid().ToString("N");
                all.RemoveAll(s => s.CouncilID == session.CouncilID);
                all.Add(session);
                TrimLocked(all);
                SaveLocked(session.ProjectID, all);
                return Clone(session);
            }
        }

        /// <summary>Overwrites the stored session with the same CouncilID (progress/verdict updates).</summary>
        public void Update(CouncilSession session)
        {
            NormalizeLegacyRecommendation(session);
            lock (LockFor(session.ProjectID))
            {
                var all = LoadLocked(session.ProjectID);
                all.RemoveAll(s => s.CouncilID == session.CouncilID);
                all.Add(session);
                TrimLocked(all);
                SaveLocked(session.ProjectID, all);
            }
        }

        public List<CouncilSession> List(string projectID)
        {
            lock (LockFor(projectID))
                return LoadLocked(projectID).OrderByDescending(s => s.CreatedAt).Select(Clone).ToList();
        }

        public CouncilSession? Get(string projectID, string councilID)
        {
            lock (LockFor(projectID))
            {
                var s = LoadLocked(projectID).FirstOrDefault(x => x.CouncilID == councilID);
                return s == null ? null : Clone(s);
            }
        }

        /// <summary>Councils created since UTC midnight — the per-day cap check.</summary>
        public int CountToday(string projectID)
        {
            var since = DateTime.UtcNow.Date;
            lock (LockFor(projectID))
                return LoadLocked(projectID).Count(s => s.CreatedAt >= since);
        }

        /// <summary>Councils raised within one Commander wake — the per-wake cap check.</summary>
        public int CountForWake(string projectID, string? wakeID)
        {
            if (string.IsNullOrEmpty(wakeID)) return 0;
            lock (LockFor(projectID))
                return LoadLocked(projectID).Count(s => s.WakeID == wakeID);
        }

        // ── internals ──

        private static void TrimLocked(List<CouncilSession> all)
        {
            if (all.Count <= MaxSessionsPerProject) return;
            var keep = all.OrderByDescending(s => s.CreatedAt).Take(MaxSessionsPerProject).ToList();
            all.Clear();
            all.AddRange(keep);
        }

        private static CouncilSession Clone(CouncilSession s)
        {
            var clone = JsonConvert.DeserializeObject<CouncilSession>(JsonConvert.SerializeObject(s))!;
            NormalizeLegacyRecommendation(clone);
            return clone;
        }

        private List<CouncilSession> LoadLocked(string projectID)
        {
            string path = PathFor(projectID);
            if (!File.Exists(path)) return new();
            try
            {
                var sessions = JsonConvert.DeserializeObject<List<CouncilSession>>(File.ReadAllText(path)) ?? new();
                sessions.ForEach(NormalizeLegacyRecommendation);
                return sessions;
            }
            catch (Exception ex)
            {
                log($"ProjectCouncilStore: failed to load {path} ({ex.Message}) — starting empty, file preserved.");
                return new();
            }
        }

        private static void NormalizeLegacyRecommendation(CouncilSession session)
        {
#pragma warning disable CS0618 // Historical persisted value is deliberately normalized on read.
            if (session.RecommendationClass == CouncilRecommendationClass.HardPolicyBlock)
                session.RecommendationClass = CouncilRecommendationClass.NeedsUserDecision;
#pragma warning restore CS0618
        }

        private void SaveLocked(string projectID, List<CouncilSession> all)
        {
            string path = PathFor(projectID);
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(all, Formatting.Indented));
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
