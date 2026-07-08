using System.Collections.Concurrent;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;

namespace Omnipotent.Services.Projects
{
    /// <summary>
    /// Per-project budget ledger (§8): tracks LLM token spend (in USD) and real-money spend,
    /// cumulative, against the project's budgets. Token cost is captured from the actual
    /// OpenRouter generation endpoint when a generation ID is available, with a per-model
    /// provisional estimate applied immediately so the UI is never blank and the ledger never
    /// under-counts if the fetch fails.
    ///
    /// Budgets themselves live on the Project record (they are NOT OmniSettings) — this ledger
    /// only accrues spend and answers "how much is left / is this money spend autonomous".
    ///
    /// Layout: Projects/&lt;projectID&gt;.ledger.json (one small doc, atomic rewrite).
    /// </summary>
    public class ProjectBudgetLedger
    {
        private readonly string dir;
        private readonly ProjectStore projectStore;
        private readonly ProjectEventLogStore eventLog;
        private readonly OpenRouterCostFetcher costFetcher;
        private readonly Action<string> log;
        private readonly ConcurrentDictionary<string, object> locks = new(StringComparer.Ordinal);

        // Provisional per-million-token USD estimate, used until the real cost reconciles.
        // Same yardstick style as KliveAgentStats; the real OpenRouter figure supersedes it.
        private const double ProvisionalPromptPerMillion = 3.0;
        private const double ProvisionalCompletionPerMillion = 15.0;

        private const double WarnFraction = 0.80;

        /// <summary>Raised (projectID) when a project crosses 100% and is auto-paused, so a surface can alert Klives.</summary>
        public event Action<string>? BudgetPausedRaised;

        public ProjectBudgetLedger(ProjectStore projectStore, ProjectEventLogStore eventLog,
            OpenRouterCostFetcher costFetcher, Action<string> log)
        {
            this.projectStore = projectStore;
            this.eventLog = eventLog;
            this.costFetcher = costFetcher;
            this.log = log ?? (_ => { });
            dir = OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsDirectory);
            Directory.CreateDirectory(dir);
        }

        public class Ledger
        {
            public string ProjectID { get; set; } = "";
            public double TokenSpendUsd { get; set; }
            public double MoneySpendUsd { get; set; }
            public long PromptTokens { get; set; }
            public long CompletionTokens { get; set; }
            /// <summary>Set true once an 80% warning has been emitted, so it fires only once per budget.</summary>
            public bool TokenWarned { get; set; }
            /// <summary>Generation IDs still awaiting real-cost reconciliation and their provisional cost.</summary>
            public Dictionary<string, double> PendingReconcile { get; set; } = new();
            public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        }

        private object LockFor(string projectID) => locks.GetOrAdd(projectID, _ => new object());
        private string LedgerPath(string projectID) => Path.Combine(dir, projectID + ".ledger.json");

        public Ledger GetLedger(string projectID)
        {
            lock (LockFor(projectID)) return LoadLocked(projectID);
        }

        /// <summary>
        /// Records an LLM turn's spend. Applies the provisional estimate immediately, then (if a
        /// generation ID is given) reconciles against the real OpenRouter cost in the background.
        /// Emits budget warning/pause events as thresholds are crossed.
        /// </summary>
        public async Task RecordTokenSpendAsync(string projectID, long promptTokens, long completionTokens, string? generationId = null)
        {
            double provisional = promptTokens / 1_000_000.0 * ProvisionalPromptPerMillion
                               + completionTokens / 1_000_000.0 * ProvisionalCompletionPerMillion;

            lock (LockFor(projectID))
            {
                var ledger = LoadLocked(projectID);
                ledger.PromptTokens += promptTokens;
                ledger.CompletionTokens += completionTokens;
                ledger.TokenSpendUsd += provisional;
                if (!string.IsNullOrWhiteSpace(generationId))
                    ledger.PendingReconcile[generationId] = provisional;
                SaveLocked(ledger);
            }

            CheckTokenThresholds(projectID);

            if (!string.IsNullOrWhiteSpace(generationId))
                _ = Task.Run(() => ReconcileAsync(projectID, generationId!));
        }

        private async Task ReconcileAsync(string projectID, string generationId)
        {
            try
            {
                double? real = await costFetcher.TryGetCostAsync(generationId);
                if (real == null) return; // keep the provisional figure
                lock (LockFor(projectID))
                {
                    var ledger = LoadLocked(projectID);
                    if (ledger.PendingReconcile.TryGetValue(generationId, out double prov))
                    {
                        ledger.TokenSpendUsd += real.Value - prov; // swap estimate for truth
                        ledger.PendingReconcile.Remove(generationId);
                        SaveLocked(ledger);
                    }
                }
                CheckTokenThresholds(projectID);
            }
            catch (Exception ex) { log($"Budget reconcile failed for {projectID}/{generationId}: {ex.Message}"); }
        }

        /// <summary>
        /// Whether a single real-money spend is autonomous: at/below the project's per-action
        /// threshold AND within the remaining money budget. Above the threshold → Discord
        /// approval (P5). Stricter than, and separate from, the token budget.
        /// </summary>
        public bool IsMoneySpendAutonomous(string projectID, double amountUsd)
        {
            var project = projectStore.GetProject(projectID);
            if (project == null) return false;
            if (amountUsd > project.MoneyAutonomousThresholdUsd) return false;
            lock (LockFor(projectID))
            {
                var ledger = LoadLocked(projectID);
                return ledger.MoneySpendUsd + amountUsd <= project.MoneyBudgetUsd;
            }
        }

        /// <summary>Records a real-money spend against the ledger (after it happened / was approved).</summary>
        public void RecordMoneySpend(string projectID, double amountUsd, string description)
        {
            lock (LockFor(projectID))
            {
                var ledger = LoadLocked(projectID);
                ledger.MoneySpendUsd += amountUsd;
                SaveLocked(ledger);
            }
            eventLog.Append(new ProjectEvent
            {
                ProjectID = projectID,
                Type = ProjectEventTypes.MoneySpent,
                Author = "system",
                Text = $"Real-money spend ${amountUsd:0.##}: {description}",
            });
        }

        private void CheckTokenThresholds(string projectID)
        {
            var project = projectStore.GetProject(projectID);
            if (project == null || project.TokenBudgetUsd <= 0) return;

            Ledger ledger;
            lock (LockFor(projectID)) ledger = LoadLocked(projectID);
            double fraction = ledger.TokenSpendUsd / project.TokenBudgetUsd;

            if (fraction >= 1.0 && project.Status == ProjectStatus.Active)
            {
                project.Status = ProjectStatus.BudgetPaused;
                projectStore.SaveProject(project);
                eventLog.Append(new ProjectEvent
                {
                    ProjectID = projectID,
                    Type = ProjectEventTypes.BudgetPaused,
                    Author = "system",
                    Text = $"Token budget exhausted (${ledger.TokenSpendUsd:0.##} of ${project.TokenBudgetUsd:0.##}). Project paused — a budget conversation with Klives is required to continue.",
                });
                try { BudgetPausedRaised?.Invoke(projectID); } catch { }
            }
            else if (fraction >= WarnFraction && !ledger.TokenWarned)
            {
                lock (LockFor(projectID))
                {
                    var l = LoadLocked(projectID);
                    l.TokenWarned = true;
                    SaveLocked(l);
                }
                eventLog.Append(new ProjectEvent
                {
                    ProjectID = projectID,
                    Type = ProjectEventTypes.BudgetWarning,
                    Author = "system",
                    Text = $"Token budget at {fraction:P0} (${ledger.TokenSpendUsd:0.##} of ${project.TokenBudgetUsd:0.##}).",
                });
            }
        }

        /// <summary>Provisional USD cost for a token count (the same yardstick applied per turn), for
        /// per-wake cost attribution in the timeline. The reconciled OpenRouter figure supersedes it cumulatively.</summary>
        public double EstimateCost(long promptTokens, long completionTokens)
            => promptTokens / 1_000_000.0 * ProvisionalPromptPerMillion
             + completionTokens / 1_000_000.0 * ProvisionalCompletionPerMillion;

        /// <summary>Compact human-readable budget state for the standing digest / wake seed.</summary>
        public string DescribeState(string projectID)
        {
            var project = projectStore.GetProject(projectID);
            var ledger = GetLedger(projectID);
            if (project == null) return "unknown project";
            return $"tokens ${ledger.TokenSpendUsd:0.##}/${project.TokenBudgetUsd:0.##} " +
                   $"({(project.TokenBudgetUsd > 0 ? ledger.TokenSpendUsd / project.TokenBudgetUsd : 0):P0}), " +
                   $"money ${ledger.MoneySpendUsd:0.##}/${project.MoneyBudgetUsd:0.##}";
        }

        private Ledger LoadLocked(string projectID)
        {
            string path = LedgerPath(projectID);
            if (!File.Exists(path)) return new Ledger { ProjectID = projectID };
            try { return JsonConvert.DeserializeObject<Ledger>(File.ReadAllText(path)) ?? new Ledger { ProjectID = projectID }; }
            catch { return new Ledger { ProjectID = projectID }; }
        }

        private void SaveLocked(Ledger ledger)
        {
            ledger.UpdatedAt = DateTime.UtcNow;
            string path = LedgerPath(ledger.ProjectID);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(ledger, Formatting.Indented));
            File.Move(tmp, path, overwrite: true);
        }
    }
}
