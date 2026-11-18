using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveAPI.Caching;

namespace Omnipotent.Services.Demiurge
{
    /// <summary>
    /// Durable, Steward-scoped memory for Klives' rules and instructions. This is deliberately
    /// separate from the LLM-authored digest: a digest is a useful summary, while a rule is an
    /// authoritative instruction that must survive compaction, model failures and restarts.
    ///
    /// Layout: Demiurge/Memory/&lt;stewardID&gt;.directives.json
    /// </summary>
    public sealed class StewardDirectiveStore
    {
        private const int OmissionNoticeReserveTokens = 64;

        /// <summary>
        /// How many recently-answered steers stay in the seed as still-in-force constraints. Bounded because
        /// casual chat should not accumulate into policy — but non-zero because an answered steer is not a
        /// discharged one, and dropping them is how a "don't do X yet" got re-proposed a wake later.
        /// </summary>
        public const int MaxAnsweredSteersInPrompt = 6;

        /// <summary>Token slice the answered-steer band may occupy, so it can never crowd out an explicit rule.</summary>
        private const int AnsweredSteerPromptTokens = 700;

        public const int MaxDirectivesPerSteward = 256;
        public const int MaxRuleLength = 1_000;
        public const int MaxTaskLength = 4_000;
        public const int MaxExpectedArtifacts = 16;

        private readonly string root;
        private readonly Action<string> log;
        private readonly ConcurrentDictionary<string, object> locks = new(StringComparer.Ordinal);
        // A corrupt memory document must fail closed. Returning an empty list then saving it on
        // the next mutation would turn one bad write into permanent loss of every rule/task.
        private readonly ConcurrentDictionary<string, byte> unavailableStewards = new(StringComparer.Ordinal);

        public StewardDirectiveStore(Action<string> log, string? rootOverride = null)
        {
            this.log = log ?? (_ => { });
            root = string.IsNullOrWhiteSpace(rootOverride)
                ? OmniPaths.GetPath(OmniPaths.GlobalPaths.DemiurgeMemoryDirectory)
                : Path.GetFullPath(rootOverride);
            Directory.CreateDirectory(root);
        }

        private static string CacheKey(string stewardID) => "demiurge:memory:" + stewardID;
        private object LockFor(string stewardID) => locks.GetOrAdd(stewardID, _ => new object());
        public string GetPath(string stewardID) => Path.Combine(root, stewardID + ".directives.json");

        public List<StewardDirective> List(string stewardID, bool includeResolved = true)
        {
            CacheDeps.NoteRead(CacheKey(stewardID));
            lock (LockFor(stewardID))
            {
                var all = LoadLocked(stewardID);
                return (includeResolved ? all : all.Where(x => x.IsOpen || x.Kind == StewardDirectiveKind.Rule && x.Status != StewardDirectiveStatus.Revoked))
                    .OrderByDescending(x => x.Priority).ThenBy(x => x.CreatedAt).Select(Clone).ToList();
            }
        }

        public StewardDirective? Get(string stewardID, string directiveID)
        {
            if (string.IsNullOrWhiteSpace(directiveID)) return null;
            CacheDeps.NoteRead(CacheKey(stewardID));
            lock (LockFor(stewardID))
            {
                var item = LoadLocked(stewardID).FirstOrDefault(x =>
                    string.Equals(x.DirectiveID, directiveID, StringComparison.Ordinal));
                return item == null ? null : Clone(item);
            }
        }

        public StewardDirective Create(string stewardID, string text, StewardDirectiveKind kind,
            StewardDirectiveScope scope = StewardDirectiveScope.Commander, IEnumerable<string>? targetAgentIDs = null,
            IEnumerable<string>? expectedArtifactPaths = null, int priority = 100, string? key = null,
            string? batchID = null)
        {
            text = NormalizeText(text, kind);
            key = NormalizeKey(key);
            batchID = NormalizeBatchID(batchID);
            var targets = NormalizeTargets(targetAgentIDs);
            if (scope == StewardDirectiveScope.SpecificAgents && targets.Count == 0)
                throw new InvalidOperationException("SpecificAgents scope requires at least one target agent ID.");

            lock (LockFor(stewardID))
            {
                var all = LoadLocked(stewardID);
                if (kind == StewardDirectiveKind.Rule && key != null)
                {
                    var existing = all.FirstOrDefault(x => x.Kind == StewardDirectiveKind.Rule &&
                        string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase) &&
                        x.Status != StewardDirectiveStatus.Revoked);
                    if (existing != null)
                    {
                        EnsureRulePromptCapacity(all.Where(x => !ReferenceEquals(x, existing)).Append(new StewardDirective
                        {
                            Kind = StewardDirectiveKind.Rule,
                            Status = StewardDirectiveStatus.Active,
                            Text = text,
                            ExpectedArtifactPaths = NormalizeArtifacts(expectedArtifactPaths),
                        }));
                        existing.Text = text;
                        existing.Scope = scope;
                        existing.TargetAgentIDs = targets;
                        existing.Priority = Math.Clamp(priority, -1_000, 1_000);
                        existing.ExpectedArtifactPaths = NormalizeArtifacts(expectedArtifactPaths);
                        if (batchID != null) existing.BatchID = batchID;
                        existing.Status = StewardDirectiveStatus.Active;
                        Touch(existing);
                        SaveLocked(stewardID, all);
                        return Clone(existing);
                    }
                }
                if (all.Count >= MaxDirectivesPerSteward)
                    throw new InvalidOperationException($"Steward memory cap reached ({MaxDirectivesPerSteward}). Revoke or complete old directives first.");

                var item = new StewardDirective
                {
                    DirectiveID = Guid.NewGuid().ToString("N"),
                    StewardID = stewardID,
                    BatchID = batchID,
                    Key = key,
                    Kind = kind,
                    Scope = scope,
                    TargetAgentIDs = targets,
                    Text = text,
                    Priority = Math.Clamp(priority, -1_000, 1_000),
                    Status = StewardDirectiveStatus.Active,
                    ExpectedArtifactPaths = NormalizeArtifacts(expectedArtifactPaths),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    Revision = 1,
                };
                if (kind == StewardDirectiveKind.Rule)
                    EnsureRulePromptCapacity(all.Append(item));
                all.Add(item);
                SaveLocked(stewardID, all);
                return Clone(item);
            }
        }

        public StewardDirective? SetSourceEvent(string stewardID, string directiveID, string eventID, long eventSequence)
            => Mutate(stewardID, directiveID, item =>
            {
                item.SourceEventID = string.IsNullOrWhiteSpace(eventID) ? item.SourceEventID : eventID;
                item.SourceEventSequence = eventSequence > 0 ? eventSequence : item.SourceEventSequence;
                return true;
            });

        public StewardDirective? MarkDelivered(string stewardID, string directiveID, string agentID, string? wakeID)
            => Mutate(stewardID, directiveID, item =>
            {
                if (!item.IsOpen) return false;
                bool changed = item.Status == StewardDirectiveStatus.Active ||
                    !string.Equals(item.DeliveredWakeID, wakeID, StringComparison.Ordinal) ||
                    !string.Equals(item.DeliveredToAgentID, agentID, StringComparison.Ordinal);
                if (!changed) return false;
                // An acknowledged task is still open work, but recovery must not erase who
                // accepted it by moving it backwards to Delivered.
                if (item.Kind != StewardDirectiveKind.Rule && item.Status == StewardDirectiveStatus.Active)
                    item.Status = StewardDirectiveStatus.Delivered;
                DateTime deliveredAt = DateTime.UtcNow;
                item.DeliveredAt = deliveredAt;
                item.DeliveredToAgentID = agentID;
                item.DeliveredWakeID = wakeID;
                var delivery = item.Deliveries.FirstOrDefault(x => string.Equals(x.AgentID, agentID, StringComparison.OrdinalIgnoreCase));
                if (delivery == null)
                    item.Deliveries.Add(new StewardDirectiveDelivery { AgentID = agentID, WakeID = wakeID, DeliveredAt = deliveredAt });
                else
                {
                    delivery.WakeID = wakeID;
                    delivery.DeliveredAt = deliveredAt;
                }
                return true;
            });

        /// <summary>
        /// Re-points an agent-scoped directive at the Commander because its only recipient was retired.
        ///
        /// Reassignment, not re-issue: the directive ID, deliverables, acknowledgement history and
        /// completion requirements are exactly the ones Klives created, so his instruction survives
        /// the loss of the agent that happened to be carrying it. Delivery is reset to Active so the
        /// Commander actually receives it — a directive still marked Delivered/Acknowledged by an agent
        /// that no longer exists would be filtered out of every future seed and quietly die.
        /// Returns null when there is no such directive, and leaves resolved directives untouched.
        /// </summary>
        public StewardDirective? ReassignToCommander(string stewardID, string directiveID)
            => Mutate(stewardID, directiveID, item =>
            {
                if (!item.IsOpen || item.Scope != StewardDirectiveScope.SpecificAgents) return false;
                item.Scope = StewardDirectiveScope.Commander;
                item.TargetAgentIDs = new List<string>();
                item.Status = StewardDirectiveStatus.Active;
                item.DeliveredAt = null;
                item.DeliveredToAgentID = null;
                item.DeliveredWakeID = null;
                item.Deliveries = new List<StewardDirectiveDelivery>();
                item.AcknowledgedAt = null;
                item.AcknowledgedBy = null;
                item.Acknowledgement = null;
                item.RetryCount = 0;
                item.LastRetryAt = null;
                return true;
            });

        /// <summary>Open directives addressed to exactly this agent (not Steward-wide rules, which
        /// every future agent inherits anyway and must not be narrowed to the Commander).</summary>
        public List<StewardDirective> ListAgentScoped(string stewardID, string agentID)
        {
            if (string.IsNullOrWhiteSpace(agentID)) return new();
            return List(stewardID, includeResolved: false)
                .Where(x => x.IsOpen
                    && x.Scope == StewardDirectiveScope.SpecificAgents
                    && x.TargetAgentIDs.Any(t => string.Equals(t, agentID.Trim(), StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        public StewardDirective? Acknowledge(string stewardID, string directiveID, string agentID, string? note = null)
            => Mutate(stewardID, directiveID, item =>
            {
                if (!item.IsOpen || item.Kind == StewardDirectiveKind.Rule) return false;
                if (item.Status == StewardDirectiveStatus.Acknowledged)
                    return string.Equals(item.AcknowledgedBy, agentID, StringComparison.OrdinalIgnoreCase);
                if (item.Status is not (StewardDirectiveStatus.Active or StewardDirectiveStatus.Delivered)) return false;
                item.Status = StewardDirectiveStatus.Acknowledged;
                item.AcknowledgedAt = DateTime.UtcNow;
                item.AcknowledgedBy = agentID;
                item.Acknowledgement = Trim(note, 1_000);
                return true;
            });

        /// <summary>Records a direct Commander reply. Steering messages become acknowledged;
        /// tasks remain open until an explicit completion is backed by their required artifacts.</summary>
        public StewardDirective? MarkResponded(string stewardID, string directiveID, string agentID,
            string response, long replyEventSequence)
            => Mutate(stewardID, directiveID, item =>
            {
                if (!item.IsOpen || item.Kind == StewardDirectiveKind.Rule) return false;
                bool changed = item.ReplyEventSequence != replyEventSequence;
                item.ReplyEventSequence = replyEventSequence;
                item.AcknowledgedAt ??= DateTime.UtcNow;
                item.AcknowledgedBy ??= agentID;
                item.Acknowledgement = Trim(response, 1_000);
                if (item.Kind == StewardDirectiveKind.Steering) item.Status = StewardDirectiveStatus.Acknowledged;
                else if (item.Status == StewardDirectiveStatus.Active || item.Status == StewardDirectiveStatus.Delivered)
                    item.Status = StewardDirectiveStatus.Acknowledged;
                return changed;
            });

        public StewardDirective? Complete(string stewardID, string directiveID, string agentID, string summary,
            IEnumerable<string>? artifactPaths = null)
            => Mutate(stewardID, directiveID, item =>
            {
                if (!item.IsOpen || item.Kind == StewardDirectiveKind.Rule) return false;
                if (item.Status != StewardDirectiveStatus.Acknowledged ||
                    !string.Equals(item.AcknowledgedBy, agentID, StringComparison.OrdinalIgnoreCase)) return false;
                item.Status = StewardDirectiveStatus.Completed;
                item.CompletedAt = DateTime.UtcNow;
                item.CompletedBy = agentID;
                item.CompletionSummary = NormalizeCompletion(summary);
                item.CompletionArtifactPaths = NormalizeArtifacts(artifactPaths);
                return true;
            });

        public StewardDirective? Revoke(string stewardID, string directiveID, string? reason = null)
            => Mutate(stewardID, directiveID, item =>
            {
                if (item.Status == StewardDirectiveStatus.Revoked) return false;
                item.Status = StewardDirectiveStatus.Revoked;
                item.CompletionSummary = NormalizeCompletion(reason);
                return true;
            });

        public StewardDirective? Fail(string stewardID, string directiveID, string summary)
            => Mutate(stewardID, directiveID, item =>
            {
                if (!item.IsOpen || item.Kind == StewardDirectiveKind.Rule) return false;
                item.Status = StewardDirectiveStatus.Failed;
                item.CompletionSummary = NormalizeCompletion(summary);
                return true;
            });

        /// <summary>
        /// Mark a directive as replaced by a later one, so only the winner is seeded. Used when Klives sends
        /// an instruction with the same <see cref="StewardDirective.Key"/> as an existing one.
        /// </summary>
        public StewardDirective? Supersede(string stewardID, string directiveID, string bySupersedingID,
            DateTime? nowUtc = null) =>
            Mutate(stewardID, directiveID, item =>
            {
                if (item.SupersededBy != null) return false;
                if (string.Equals(item.DirectiveID, bySupersedingID, StringComparison.Ordinal)) return false;
                item.SupersededBy = bySupersedingID;
                item.SupersededAt = nowUtc ?? DateTime.UtcNow;
                return true;
            });

        /// <summary>
        /// Always-injected, scope-filtered block for an agent's wake seed. This method owns the
        /// whole directives budget so callers never truncate through the middle of a rule/task.
        /// The currently-triggered directive is preferentially selected; every other queued task
        /// remains durable and discoverable with list_steward_directives.
        /// </summary>
        public string DescribeForPrompt(string stewardID, string agentID, string? preferredDirectiveID = null)
        {
            var open = List(stewardID, includeResolved: false)
                .Where(x => AppliesTo(x, agentID))
                .Where(x => x.SupersededBy == null)
                .ToList();

            // Acknowledged steers are dropped from the seed on the theory that an answered chat message is
            // history — which is right for a QUESTION ("what failed?"): the reply discharges it. It is wrong
            // for a CONSTRAINT. "Keep digging, don't complete yet" was acknowledged in prose and then absent
            // from every later seed, so the next wake re-proposed completion. An answer is not compliance.
            //
            // So the band keeps only the constraint-shaped ones. Most such messages are stored as Rules at
            // creation time anyway; this catches the ones that reached the store as ordinary steering, and
            // covers Stewards with rule auto-promotion turned off.
            //
            // Ordered by acknowledgement time, then by position in the store as a tiebreak: several steers
            // answered inside the same millisecond must not select an arbitrary subset differently on each
            // wake, or the seed would appear to change its mind about which of Klives' words still apply.
            var answeredSteers = open
                .Select((item, index) => (item, index))
                .Where(x => x.item.Kind == StewardDirectiveKind.Steering
                    && x.item.Status == StewardDirectiveStatus.Acknowledged
                    && StewardDirectiveClassifier.ClassifyStandingConstraint(x.item.Text) == StewardDirectiveKind.Rule)
                .OrderByDescending(x => x.item.AcknowledgedAt ?? x.item.UpdatedAt)
                .ThenByDescending(x => x.index)
                .Take(MaxAnsweredSteersInPrompt)
                .Select(x => x.item)
                .ToList();
            var visible = open
                .Where(x => x.Kind != StewardDirectiveKind.Steering || x.Status != StewardDirectiveStatus.Acknowledged)
                .ToList();
            if (visible.Count == 0 && answeredSteers.Count == 0) return "";

            var rules = visible.Where(x => x.Kind == StewardDirectiveKind.Rule)
                .OrderByDescending(x => x.Priority).ThenBy(x => x.CreatedAt).ToList();
            var work = visible.Where(x => x.Kind != StewardDirectiveKind.Rule)
                .OrderByDescending(x => string.Equals(x.DirectiveID, preferredDirectiveID, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x => x.Priority).ThenByDescending(x => x.CreatedAt).ToList();

            var renderedRules = new List<string>();
            foreach (var rule in rules)
                renderedRules.Add(RenderForPrompt(rule));

            // Answered steers sit with the rules — ahead of the work queue, because they constrain how the
            // work is done — but inside their own token slice, so a chatty day cannot crowd out an explicit
            // rule. Rules keep the guaranteed capacity EnsureRulePromptCapacity protects.
            int steerBudget = Math.Max(0, Math.Min(AnsweredSteerPromptTokens,
                DemiurgeContextBudget.DirectivesBudget - OmissionNoticeReserveTokens
                    - DemiurgeContextBudget.EstimateTokens(string.Join("\n", renderedRules))));
            int steerUsed = 0;
            foreach (var steer in answeredSteers)
            {
                string text = RenderForPrompt(steer);
                int cost = DemiurgeContextBudget.EstimateTokens(text);
                if (steerUsed + cost > steerBudget) break;
                renderedRules.Add(text);
                steerUsed += cost;
            }

            var selectedWork = new List<string>();
            int omitted = 0;
            foreach (var item in work)
            {
                string text = RenderForPrompt(item);
                string candidate = string.Join("\n", renderedRules.Concat(selectedWork).Append(text));
                if (DemiurgeContextBudget.EstimateTokens(candidate) >
                    DemiurgeContextBudget.DirectivesBudget - OmissionNoticeReserveTokens)
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
                    "Use steward_directive op:list before declaring the directive queue empty.]";
                string note = Notice();
                while (selectedWork.Count > 0 && DemiurgeContextBudget.EstimateTokens(
                    string.Join("\n", renderedRules.Concat(selectedWork).Append(note))) > DemiurgeContextBudget.DirectivesBudget)
                {
                    selectedWork.RemoveAt(selectedWork.Count - 1);
                    omitted++;
                    note = Notice();
                }
                rendered = renderedRules.Concat(selectedWork).Append(note).ToList();
            }
            return string.Join("\n", rendered);
        }

        private static string RenderForPrompt(StewardDirective item)
        {
            bool answeredSteer = item.Kind == StewardDirectiveKind.Steering
                && item.Status == StewardDirectiveStatus.Acknowledged;
            string label = item.Kind switch
            {
                StewardDirectiveKind.Rule => "RULE — NON-NEGOTIABLE",
                StewardDirectiveKind.Task => "OPEN TASK",
                _ => answeredSteer ? "ANSWERED STEER — STILL IN FORCE unless Klives supersedes it" : "UNANSWERED STEERING",
            };
            var sb = new StringBuilder();
            sb.Append($"[{label} id={item.DirectiveID}; status={item.Status}; set {Data_Handling.TemporalFormat.StampMinute(item.CreatedAt)}] ");
            sb.AppendLine(item.Text);
            if (item.ExpectedArtifactPaths.Count > 0)
                sb.AppendLine($"  Required deliverables: {string.Join(", ", item.ExpectedArtifactPaths)}. Do not mark complete until they exist in /project and are verified.");
            if (answeredSteer)
                sb.AppendLine("  You have already replied to this. Replying did not discharge it — keep obeying it until Klives says otherwise, and do not re-raise the question it answered.");
            else if (item.Kind != StewardDirectiveKind.Rule)
                sb.AppendLine($"  Use steward_directive op:acknowledge then op:complete for id={item.DirectiveID}; a status sentence alone does not complete this instruction.");
            return sb.ToString().TrimEnd();
        }

        public static bool AppliesTo(StewardDirective item, string agentID)
        {
            if (!item.IsOpen && !(item.Kind == StewardDirectiveKind.Rule && item.Status != StewardDirectiveStatus.Revoked)) return false;
            return ScopeAppliesTo(item, agentID);
        }

        /// <summary>Scope-only variant for authorization checks, including resolved directives.</summary>
        public static bool ScopeAppliesTo(StewardDirective item, string agentID)
        {
            return item.Scope switch
            {
                StewardDirectiveScope.AllAgents => true,
                StewardDirectiveScope.Commander => string.Equals(agentID, "commander", StringComparison.OrdinalIgnoreCase),
                StewardDirectiveScope.SpecificAgents => item.TargetAgentIDs.Any(x => string.Equals(x, agentID, StringComparison.OrdinalIgnoreCase)),
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

        private StewardDirective? Mutate(string stewardID, string directiveID, Func<StewardDirective, bool> mutation)
        {
            if (string.IsNullOrWhiteSpace(directiveID)) return null;
            lock (LockFor(stewardID))
            {
                var all = LoadLocked(stewardID);
                var item = all.FirstOrDefault(x => string.Equals(x.DirectiveID, directiveID, StringComparison.Ordinal));
                if (item == null || !mutation(item)) return item == null ? null : Clone(item);
                Touch(item);
                SaveLocked(stewardID, all);
                return Clone(item);
            }
        }

        private List<StewardDirective> LoadLocked(string stewardID)
        {
            if (unavailableStewards.ContainsKey(stewardID))
                throw new InvalidOperationException($"Durable Steward memory for '{stewardID}' is unavailable because its on-disk document is corrupt. Repair or restore it before changing directives.");
            string path = GetPath(stewardID);
            if (!File.Exists(path)) return new();
            try
            {
                var all = JsonConvert.DeserializeObject<List<StewardDirective>>(File.ReadAllText(path)) ?? new();
                foreach (var item in all) Normalize(item, stewardID);
                return all;
            }
            catch (Exception ex)
            {
                unavailableStewards[stewardID] = 0;
                log($"StewardDirectiveStore: failed to load {path} ({ex.Message}); preserving the file and refusing memory mutations until it is repaired.");
                throw new InvalidOperationException($"Durable Steward memory for '{stewardID}' could not be read. Its file was preserved and no directives were changed.", ex);
            }
        }

        private void SaveLocked(string stewardID, List<StewardDirective> all)
        {
            string path = GetPath(stewardID);
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
            CacheDeps.Bump(CacheKey(stewardID));
        }

        private static void Normalize(StewardDirective item, string stewardID)
        {
            item.DirectiveID = string.IsNullOrWhiteSpace(item.DirectiveID) ? Guid.NewGuid().ToString("N") : item.DirectiveID.Trim();
            item.StewardID = string.IsNullOrWhiteSpace(item.StewardID) ? stewardID : item.StewardID;
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

        private static void Touch(StewardDirective item)
        {
            item.UpdatedAt = DateTime.UtcNow;
            item.Revision++;
        }

        private static string NormalizeText(string? text, StewardDirectiveKind kind)
        {
            string value = (text ?? "").Trim();
            if (value.Length == 0) throw new InvalidOperationException("Directive text cannot be empty.");
            int max = kind == StewardDirectiveKind.Rule ? MaxRuleLength : MaxTaskLength;
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
        private static void EnsureRulePromptCapacity(IEnumerable<StewardDirective> directives)
        {
            int tokens = DemiurgeContextBudget.EstimateTokens(string.Join("\n", directives
                .Where(x => x.Kind == StewardDirectiveKind.Rule &&
                    x.Status != StewardDirectiveStatus.Revoked && x.Status != StewardDirectiveStatus.Failed)
                .Select(RenderForPrompt)));
            if (tokens > DemiurgeContextBudget.DirectivesBudget - OmissionNoticeReserveTokens)
                throw new InvalidOperationException(
                    $"Standing rules exceed the durable-directives prompt capacity " +
                    $"({DemiurgeContextBudget.DirectivesBudget - OmissionNoticeReserveTokens} tokens plus queue notice reserve). " +
                    "Replace or revoke an existing rule instead of adding one that agents could not all see.");
        }

        private static StewardDirective Clone(StewardDirective item) =>
            JsonConvert.DeserializeObject<StewardDirective>(JsonConvert.SerializeObject(item))!;
    }
}
