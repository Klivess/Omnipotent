using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveAPI.Caching;

namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Durable, project-scoped memory for Klives' rules and instructions. This is deliberately
    /// separate from the LLM-authored digest: a digest is a useful summary, while a rule is an
    /// authoritative instruction that must survive compaction, model failures and restarts.
    ///
    /// Layout: Projects/Memory/&lt;projectID&gt;.directives.json
    /// </summary>
    public sealed class ProjectDirectiveStore
    {
        private const int OmissionNoticeReserveTokens = 64;
        public const int MaxDirectivesPerProject = 256;
        public const int MaxRuleLength = 1_000;
        public const int MaxTaskLength = 4_000;
        public const int MaxExpectedArtifacts = 16;

        private readonly string root;
        private readonly Action<string> log;
        private readonly ConcurrentDictionary<string, object> locks = new(StringComparer.Ordinal);
        // A corrupt memory document must fail closed. Returning an empty list then saving it on
        // the next mutation would turn one bad write into permanent loss of every rule/task.
        private readonly ConcurrentDictionary<string, byte> unavailableProjects = new(StringComparer.Ordinal);

        public ProjectDirectiveStore(Action<string> log, string? rootOverride = null)
        {
            this.log = log ?? (_ => { });
            root = string.IsNullOrWhiteSpace(rootOverride)
                ? OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsMemoryDirectory)
                : Path.GetFullPath(rootOverride);
            Directory.CreateDirectory(root);
        }

        private static string CacheKey(string projectID) => "projects:memory:" + projectID;
        private object LockFor(string projectID) => locks.GetOrAdd(projectID, _ => new object());
        public string GetPath(string projectID) => Path.Combine(root, projectID + ".directives.json");

        public List<ProjectDirective> List(string projectID, bool includeResolved = true)
        {
            CacheDeps.NoteRead(CacheKey(projectID));
            lock (LockFor(projectID))
            {
                var all = LoadLocked(projectID);
                return (includeResolved ? all : all.Where(x => x.IsOpen || x.Kind == ProjectDirectiveKind.Rule && x.Status != ProjectDirectiveStatus.Revoked))
                    .OrderByDescending(x => x.Priority).ThenBy(x => x.CreatedAt).Select(Clone).ToList();
            }
        }

        public ProjectDirective? Get(string projectID, string directiveID)
        {
            if (string.IsNullOrWhiteSpace(directiveID)) return null;
            CacheDeps.NoteRead(CacheKey(projectID));
            lock (LockFor(projectID))
            {
                var item = LoadLocked(projectID).FirstOrDefault(x =>
                    string.Equals(x.DirectiveID, directiveID, StringComparison.Ordinal));
                return item == null ? null : Clone(item);
            }
        }

        public ProjectDirective Create(string projectID, string text, ProjectDirectiveKind kind,
            ProjectDirectiveScope scope = ProjectDirectiveScope.Commander, IEnumerable<string>? targetAgentIDs = null,
            IEnumerable<string>? expectedArtifactPaths = null, int priority = 100, string? key = null,
            string? batchID = null)
        {
            text = NormalizeText(text, kind);
            key = NormalizeKey(key);
            batchID = NormalizeBatchID(batchID);
            var targets = NormalizeTargets(targetAgentIDs);
            if (scope == ProjectDirectiveScope.SpecificAgents && targets.Count == 0)
                throw new InvalidOperationException("SpecificAgents scope requires at least one target agent ID.");

            lock (LockFor(projectID))
            {
                var all = LoadLocked(projectID);
                if (kind == ProjectDirectiveKind.Rule && key != null)
                {
                    var existing = all.FirstOrDefault(x => x.Kind == ProjectDirectiveKind.Rule &&
                        string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase) &&
                        x.Status != ProjectDirectiveStatus.Revoked);
                    if (existing != null)
                    {
                        EnsureRulePromptCapacity(all.Where(x => !ReferenceEquals(x, existing)).Append(new ProjectDirective
                        {
                            Kind = ProjectDirectiveKind.Rule,
                            Status = ProjectDirectiveStatus.Active,
                            Text = text,
                            ExpectedArtifactPaths = NormalizeArtifacts(expectedArtifactPaths),
                        }));
                        existing.Text = text;
                        existing.Scope = scope;
                        existing.TargetAgentIDs = targets;
                        existing.Priority = Math.Clamp(priority, -1_000, 1_000);
                        existing.ExpectedArtifactPaths = NormalizeArtifacts(expectedArtifactPaths);
                        if (batchID != null) existing.BatchID = batchID;
                        existing.Status = ProjectDirectiveStatus.Active;
                        Touch(existing);
                        SaveLocked(projectID, all);
                        return Clone(existing);
                    }
                }
                if (all.Count >= MaxDirectivesPerProject)
                    throw new InvalidOperationException($"Project memory cap reached ({MaxDirectivesPerProject}). Revoke or complete old directives first.");

                var item = new ProjectDirective
                {
                    DirectiveID = Guid.NewGuid().ToString("N"),
                    ProjectID = projectID,
                    BatchID = batchID,
                    Key = key,
                    Kind = kind,
                    Scope = scope,
                    TargetAgentIDs = targets,
                    Text = text,
                    Priority = Math.Clamp(priority, -1_000, 1_000),
                    Status = ProjectDirectiveStatus.Active,
                    ExpectedArtifactPaths = NormalizeArtifacts(expectedArtifactPaths),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    Revision = 1,
                };
                if (kind == ProjectDirectiveKind.Rule)
                    EnsureRulePromptCapacity(all.Append(item));
                all.Add(item);
                SaveLocked(projectID, all);
                return Clone(item);
            }
        }

        public ProjectDirective? SetSourceEvent(string projectID, string directiveID, string eventID, long eventSequence)
            => Mutate(projectID, directiveID, item =>
            {
                item.SourceEventID = string.IsNullOrWhiteSpace(eventID) ? item.SourceEventID : eventID;
                item.SourceEventSequence = eventSequence > 0 ? eventSequence : item.SourceEventSequence;
                return true;
            });

        public ProjectDirective? MarkDelivered(string projectID, string directiveID, string agentID, string? wakeID)
            => Mutate(projectID, directiveID, item =>
            {
                if (!item.IsOpen) return false;
                bool changed = item.Status == ProjectDirectiveStatus.Active ||
                    !string.Equals(item.DeliveredWakeID, wakeID, StringComparison.Ordinal) ||
                    !string.Equals(item.DeliveredToAgentID, agentID, StringComparison.Ordinal);
                if (!changed) return false;
                // An acknowledged task is still open work, but recovery must not erase who
                // accepted it by moving it backwards to Delivered.
                if (item.Kind != ProjectDirectiveKind.Rule && item.Status == ProjectDirectiveStatus.Active)
                    item.Status = ProjectDirectiveStatus.Delivered;
                DateTime deliveredAt = DateTime.UtcNow;
                item.DeliveredAt = deliveredAt;
                item.DeliveredToAgentID = agentID;
                item.DeliveredWakeID = wakeID;
                var delivery = item.Deliveries.FirstOrDefault(x => string.Equals(x.AgentID, agentID, StringComparison.OrdinalIgnoreCase));
                if (delivery == null)
                    item.Deliveries.Add(new ProjectDirectiveDelivery { AgentID = agentID, WakeID = wakeID, DeliveredAt = deliveredAt });
                else
                {
                    delivery.WakeID = wakeID;
                    delivery.DeliveredAt = deliveredAt;
                }
                return true;
            });

        public ProjectDirective? Acknowledge(string projectID, string directiveID, string agentID, string? note = null)
            => Mutate(projectID, directiveID, item =>
            {
                if (!item.IsOpen || item.Kind == ProjectDirectiveKind.Rule) return false;
                if (item.Status == ProjectDirectiveStatus.Acknowledged)
                    return string.Equals(item.AcknowledgedBy, agentID, StringComparison.OrdinalIgnoreCase);
                if (item.Status is not (ProjectDirectiveStatus.Active or ProjectDirectiveStatus.Delivered)) return false;
                item.Status = ProjectDirectiveStatus.Acknowledged;
                item.AcknowledgedAt = DateTime.UtcNow;
                item.AcknowledgedBy = agentID;
                item.Acknowledgement = Trim(note, 1_000);
                return true;
            });

        /// <summary>Records a direct Commander reply. Steering messages become acknowledged;
        /// tasks remain open until an explicit completion is backed by their required artifacts.</summary>
        public ProjectDirective? MarkResponded(string projectID, string directiveID, string agentID,
            string response, long replyEventSequence)
            => Mutate(projectID, directiveID, item =>
            {
                if (!item.IsOpen || item.Kind == ProjectDirectiveKind.Rule) return false;
                bool changed = item.ReplyEventSequence != replyEventSequence;
                item.ReplyEventSequence = replyEventSequence;
                item.AcknowledgedAt ??= DateTime.UtcNow;
                item.AcknowledgedBy ??= agentID;
                item.Acknowledgement = Trim(response, 1_000);
                if (item.Kind == ProjectDirectiveKind.Steering) item.Status = ProjectDirectiveStatus.Acknowledged;
                else if (item.Status == ProjectDirectiveStatus.Active || item.Status == ProjectDirectiveStatus.Delivered)
                    item.Status = ProjectDirectiveStatus.Acknowledged;
                return changed;
            });

        public ProjectDirective? Complete(string projectID, string directiveID, string agentID, string summary,
            IEnumerable<string>? artifactPaths = null)
            => Mutate(projectID, directiveID, item =>
            {
                if (!item.IsOpen || item.Kind == ProjectDirectiveKind.Rule) return false;
                if (item.Status != ProjectDirectiveStatus.Acknowledged ||
                    !string.Equals(item.AcknowledgedBy, agentID, StringComparison.OrdinalIgnoreCase)) return false;
                item.Status = ProjectDirectiveStatus.Completed;
                item.CompletedAt = DateTime.UtcNow;
                item.CompletedBy = agentID;
                item.CompletionSummary = NormalizeCompletion(summary);
                item.CompletionArtifactPaths = NormalizeArtifacts(artifactPaths);
                return true;
            });

        public ProjectDirective? Revoke(string projectID, string directiveID, string? reason = null)
            => Mutate(projectID, directiveID, item =>
            {
                if (item.Status == ProjectDirectiveStatus.Revoked) return false;
                item.Status = ProjectDirectiveStatus.Revoked;
                item.CompletionSummary = NormalizeCompletion(reason);
                return true;
            });

        public ProjectDirective? Fail(string projectID, string directiveID, string summary)
            => Mutate(projectID, directiveID, item =>
            {
                if (!item.IsOpen || item.Kind == ProjectDirectiveKind.Rule) return false;
                item.Status = ProjectDirectiveStatus.Failed;
                item.CompletionSummary = NormalizeCompletion(summary);
                return true;
            });

        /// <summary>
        /// Always-injected, scope-filtered block for an agent's wake seed. This method owns the
        /// whole directives budget so callers never truncate through the middle of a rule/task.
        /// The currently-triggered directive is preferentially selected; every other queued task
        /// remains durable and discoverable with list_project_directives.
        /// </summary>
        public string DescribeForPrompt(string projectID, string agentID, string? preferredDirectiveID = null)
        {
            var visible = List(projectID, includeResolved: false)
                .Where(x => AppliesTo(x, agentID))
                .Where(x => x.Kind != ProjectDirectiveKind.Steering || x.Status != ProjectDirectiveStatus.Acknowledged)
                .ToList();
            if (visible.Count == 0) return "";

            var rules = visible.Where(x => x.Kind == ProjectDirectiveKind.Rule)
                .OrderByDescending(x => x.Priority).ThenBy(x => x.CreatedAt).ToList();
            var work = visible.Where(x => x.Kind != ProjectDirectiveKind.Rule)
                .OrderByDescending(x => string.Equals(x.DirectiveID, preferredDirectiveID, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x => x.Priority).ThenByDescending(x => x.CreatedAt).ToList();

            var renderedRules = new List<string>();
            foreach (var rule in rules)
                renderedRules.Add(RenderForPrompt(rule));

            var selectedWork = new List<string>();
            int omitted = 0;
            foreach (var item in work)
            {
                string text = RenderForPrompt(item);
                string candidate = string.Join("\n", renderedRules.Concat(selectedWork).Append(text));
                if (ProjectsContextBudget.EstimateTokens(candidate) >
                    ProjectsContextBudget.DirectivesBudget - OmissionNoticeReserveTokens)
                {
                    omitted++;
                    continue;
                }
                selectedWork.Add(text);
            }

            var rendered = renderedRules.Concat(selectedWork).ToList();
            if (omitted > 0)
            {
                string Notice() => $"[{omitted} additional durable task(s) are queued but not expanded in this seed. " +
                    "Use list_project_directives before declaring the directive queue empty.]";
                string note = Notice();
                while (selectedWork.Count > 0 && ProjectsContextBudget.EstimateTokens(
                    string.Join("\n", renderedRules.Concat(selectedWork).Append(note))) > ProjectsContextBudget.DirectivesBudget)
                {
                    selectedWork.RemoveAt(selectedWork.Count - 1);
                    omitted++;
                    note = Notice();
                }
                rendered = renderedRules.Concat(selectedWork).Append(note).ToList();
            }
            return string.Join("\n", rendered);
        }

        private static string RenderForPrompt(ProjectDirective item)
        {
            string label = item.Kind switch
            {
                ProjectDirectiveKind.Rule => "RULE — NON-NEGOTIABLE",
                ProjectDirectiveKind.Task => "OPEN TASK",
                _ => "UNANSWERED STEERING",
            };
            var sb = new StringBuilder();
            sb.Append($"[{label} id={item.DirectiveID}; status={item.Status}; set {Data_Handling.TemporalFormat.StampMinute(item.CreatedAt)}] ");
            sb.AppendLine(item.Text);
            if (item.ExpectedArtifactPaths.Count > 0)
                sb.AppendLine($"  Required deliverables: {string.Join(", ", item.ExpectedArtifactPaths)}. Do not mark complete until they exist in /project and are verified.");
            if (item.Kind != ProjectDirectiveKind.Rule)
                sb.AppendLine($"  Use acknowledge_project_directive then complete_project_directive for id={item.DirectiveID}; a status sentence alone does not complete this instruction.");
            return sb.ToString().TrimEnd();
        }

        public static bool AppliesTo(ProjectDirective item, string agentID)
        {
            if (!item.IsOpen && !(item.Kind == ProjectDirectiveKind.Rule && item.Status != ProjectDirectiveStatus.Revoked)) return false;
            return ScopeAppliesTo(item, agentID);
        }

        /// <summary>Scope-only variant for authorization checks, including resolved directives.</summary>
        public static bool ScopeAppliesTo(ProjectDirective item, string agentID)
        {
            return item.Scope switch
            {
                ProjectDirectiveScope.AllAgents => true,
                ProjectDirectiveScope.Commander => string.Equals(agentID, "commander", StringComparison.OrdinalIgnoreCase),
                ProjectDirectiveScope.SpecificAgents => item.TargetAgentIDs.Any(x => string.Equals(x, agentID, StringComparison.OrdinalIgnoreCase)),
                _ => false,
            };
        }

        /// <summary>Correlates a wake trigger with a durable directive without trusting free-form prose.</summary>
        public static string? TryExtractDirectiveID(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var match = Regex.Match(text, @"\[directive:([a-f0-9]{16,64})\]", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : null;
        }

        private ProjectDirective? Mutate(string projectID, string directiveID, Func<ProjectDirective, bool> mutation)
        {
            if (string.IsNullOrWhiteSpace(directiveID)) return null;
            lock (LockFor(projectID))
            {
                var all = LoadLocked(projectID);
                var item = all.FirstOrDefault(x => string.Equals(x.DirectiveID, directiveID, StringComparison.Ordinal));
                if (item == null || !mutation(item)) return item == null ? null : Clone(item);
                Touch(item);
                SaveLocked(projectID, all);
                return Clone(item);
            }
        }

        private List<ProjectDirective> LoadLocked(string projectID)
        {
            if (unavailableProjects.ContainsKey(projectID))
                throw new InvalidOperationException($"Durable project memory for '{projectID}' is unavailable because its on-disk document is corrupt. Repair or restore it before changing directives.");
            string path = GetPath(projectID);
            if (!File.Exists(path)) return new();
            try
            {
                var all = JsonConvert.DeserializeObject<List<ProjectDirective>>(File.ReadAllText(path)) ?? new();
                foreach (var item in all) Normalize(item, projectID);
                return all;
            }
            catch (Exception ex)
            {
                unavailableProjects[projectID] = 0;
                log($"ProjectDirectiveStore: failed to load {path} ({ex.Message}); preserving the file and refusing memory mutations until it is repaired.");
                throw new InvalidOperationException($"Durable project memory for '{projectID}' could not be read. Its file was preserved and no directives were changed.", ex);
            }
        }

        private void SaveLocked(string projectID, List<ProjectDirective> all)
        {
            string path = GetPath(projectID);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(all, Formatting.Indented));
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(tmp, path, overwrite: true); break; }
                catch (Exception ex) when (attempt < 5 && (ex is IOException || ex is UnauthorizedAccessException))
                {
                    Thread.Sleep(15 * (attempt + 1));
                }
            }
            CacheDeps.Bump(CacheKey(projectID));
        }

        private static void Normalize(ProjectDirective item, string projectID)
        {
            item.DirectiveID = string.IsNullOrWhiteSpace(item.DirectiveID) ? Guid.NewGuid().ToString("N") : item.DirectiveID.Trim();
            item.ProjectID = string.IsNullOrWhiteSpace(item.ProjectID) ? projectID : item.ProjectID;
            item.Text ??= "";
            item.TargetAgentIDs ??= new();
            item.ExpectedArtifactPaths ??= new();
            item.Deliveries ??= new();
            item.CompletionArtifactPaths ??= new();
            item.CreatedBy ??= "klives";
            if (item.CreatedAt == default) item.CreatedAt = DateTime.UtcNow;
            if (item.UpdatedAt == default) item.UpdatedAt = item.CreatedAt;
            if (item.Revision <= 0) item.Revision = 1;
        }

        private static void Touch(ProjectDirective item)
        {
            item.UpdatedAt = DateTime.UtcNow;
            item.Revision++;
        }

        private static string NormalizeText(string? text, ProjectDirectiveKind kind)
        {
            string value = (text ?? "").Trim();
            if (value.Length == 0) throw new InvalidOperationException("Directive text cannot be empty.");
            int max = kind == ProjectDirectiveKind.Rule ? MaxRuleLength : MaxTaskLength;
            return value.Length <= max ? value : value[..max];
        }

        private static string? NormalizeKey(string? key)
        {
            string value = (key ?? "").Trim();
            return value.Length == 0 ? null : value[..Math.Min(value.Length, 120)];
        }

        private static string? NormalizeBatchID(string? batchID)
        {
            string value = (batchID ?? "").Trim();
            return value.Length == 0 ? null : value[..Math.Min(value.Length, 120)];
        }

        private static List<string> NormalizeTargets(IEnumerable<string>? values) => (values ?? Array.Empty<string>())
            .Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToList();

        private static List<string> NormalizeArtifacts(IEnumerable<string>? values) => (values ?? Array.Empty<string>())
            .Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxExpectedArtifacts).Select(x => x.Length <= 300 ? x : x[..300]).ToList();

        private static string? Trim(string? value, int max)
        {
            string text = (value ?? "").Trim();
            return text.Length == 0 ? null : text.Length <= max ? text : text[..max];
        }

        private static string? NormalizeCompletion(string? value) => Trim(value, 2_000);

        /// <summary>
        /// A standing rule is only trustworthy if it can fit in the always-injected prompt block.
        /// Refuse a new/expanded rule instead of silently truncating an older rule out of context.
        /// Tasks may queue behind each other, but explicit policy never becomes best-effort.
        /// </summary>
        private static void EnsureRulePromptCapacity(IEnumerable<ProjectDirective> directives)
        {
            int tokens = ProjectsContextBudget.EstimateTokens(string.Join("\n", directives
                .Where(x => x.Kind == ProjectDirectiveKind.Rule &&
                    x.Status != ProjectDirectiveStatus.Revoked && x.Status != ProjectDirectiveStatus.Failed)
                .Select(RenderForPrompt)));
            if (tokens > ProjectsContextBudget.DirectivesBudget - OmissionNoticeReserveTokens)
                throw new InvalidOperationException(
                    $"Standing rules exceed the durable-directives prompt capacity " +
                    $"({ProjectsContextBudget.DirectivesBudget - OmissionNoticeReserveTokens} tokens plus queue notice reserve). " +
                    "Replace or revoke an existing rule instead of adding one that agents could not all see.");
        }

        private static ProjectDirective Clone(ProjectDirective item) =>
            JsonConvert.DeserializeObject<ProjectDirective>(JsonConvert.SerializeObject(item))!;
    }
}
