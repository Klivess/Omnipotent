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
        private readonly ConcurrentDictionary<string, SemaphoreSlim> llmTurnGates = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, double> llmTurnReservations = new(StringComparer.Ordinal);

        // Provisional per-million-token USD estimate, used until the real cost reconciles.
        // Same yardstick style as KliveAgentStats; the real OpenRouter figure supersedes it.
        private const double ProvisionalPromptPerMillion = 3.0;
        private const double ProvisionalCompletionPerMillion = 15.0;

        private const double WarnFraction = 0.80;
        private const double DefaultTurnReservationUsd = 0.05;

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

        private sealed class LlmTurnLease : IAsyncDisposable
        {
            private ProjectBudgetLedger? owner;
            private readonly string projectID;
            private readonly double reservedUsd;

            public LlmTurnLease(ProjectBudgetLedger owner, string projectID, double reservedUsd)
            {
                this.owner = owner;
                this.projectID = projectID;
                this.reservedUsd = reservedUsd;
            }

            public ValueTask DisposeAsync()
            {
                Interlocked.Exchange(ref owner, null)?.ReleaseReservation(projectID, reservedUsd);
                return ValueTask.CompletedTask;
            }
        }

        /// <summary>
        /// Reserves a conservative slice of the remaining project budget for one provider turn.
        /// The per-project gate is held only while checking and reserving, never across the HTTP
        /// call. This preserves multi-agent concurrency while preventing a burst of callers from
        /// all observing the same uncommitted final cents. Reservations are process-local because
        /// provider calls cannot survive a process restart.
        /// </summary>
        public async Task<IAsyncDisposable?> TryAcquireLlmTurnAsync(string projectID, CancellationToken ct = default)
        {
            var gate = llmTurnGates.GetOrAdd(projectID, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                var project = projectStore.GetProject(projectID);
                double reservation = 0;
                lock (LockFor(projectID))
                {
                    var ledger = LoadLocked(projectID);
                    llmTurnReservations.TryGetValue(projectID, out double alreadyReserved);
                    bool runnable = project?.Status is ProjectStatus.Active or ProjectStatus.Planning;
                    double remaining = project == null ? 0 : project.TokenBudgetUsd - ledger.TokenSpendUsd - alreadyReserved;
                    if (runnable && project!.TokenBudgetUsd > 0 && remaining > 0)
                    {
                        reservation = Math.Min(DefaultTurnReservationUsd, remaining);
                        llmTurnReservations[projectID] = alreadyReserved + reservation;
                    }
                }
                if (reservation > 0)
                    return new LlmTurnLease(this, projectID, reservation);
            }
            finally
            {
                gate.Release();
            }
            CheckTokenThresholds(projectID);
            return null;
        }

        private void ReleaseReservation(string projectID, double amountUsd)
        {
            lock (LockFor(projectID))
            {
                llmTurnReservations.TryGetValue(projectID, out double reserved);
                double remaining = Math.Max(0, reserved - amountUsd);
                if (remaining <= 0.0000001) llmTurnReservations.TryRemove(projectID, out _);
                else llmTurnReservations[projectID] = remaining;
            }
        }

        public bool IsWithinTokenBudget(string projectID)
        {
            var project = projectStore.GetProject(projectID);
            if (project == null || project.TokenBudgetUsd <= 0) return false;
            lock (LockFor(projectID)) return LoadLocked(projectID).TokenSpendUsd < project.TokenBudgetUsd;
        }

        /// <summary>
        /// Records an LLM turn's spend. When <paramref name="actualCostUsd"/> is supplied (OpenRouter
        /// reports the real per-request cost in the completion's usage object), that authoritative figure
        /// is booked directly — accurate for whatever model is in use, and immediate, with no /generation
        /// round-trip. Otherwise a flat per-model provisional is applied and (if a generation ID is given)
        /// reconciled against the real OpenRouter cost in the background. Emits budget warning/pause
        /// events as thresholds are crossed.
        /// </summary>
        public async Task RecordTokenSpendAsync(string projectID, long promptTokens, long completionTokens, string? generationId = null, double? actualCostUsd = null)
        {
            // The completion already carries the real cost — book it and skip the estimate/reconcile
            // path entirely. A provider that doesn't report cost (HuggingFace/local) falls back to the
            // flat provisional, which the /generation fetch later reconciles when a generation ID exists.
            bool haveActual = actualCostUsd.HasValue && actualCostUsd.Value >= 0;
            double amount = haveActual
                ? actualCostUsd!.Value
                : promptTokens / 1_000_000.0 * ProvisionalPromptPerMillion
                + completionTokens / 1_000_000.0 * ProvisionalCompletionPerMillion;

            lock (LockFor(projectID))
            {
                var ledger = LoadLocked(projectID);
                ledger.PromptTokens += promptTokens;
                ledger.CompletionTokens += completionTokens;
                ledger.TokenSpendUsd += amount;
                if (!haveActual && !string.IsNullOrWhiteSpace(generationId))
                    ledger.PendingReconcile[generationId] = amount;
                SaveLocked(ledger);
            }

            CheckTokenThresholds(projectID);

            if (!haveActual && !string.IsNullOrWhiteSpace(generationId))
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
            if (!double.IsFinite(amountUsd) || amountUsd <= 0) return false;
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
            if (!double.IsFinite(amountUsd) || amountUsd <= 0)
                throw new ArgumentOutOfRangeException(nameof(amountUsd), "Money spend must be positive and finite.");
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

            if (fraction >= 1.0 && project.Status is ProjectStatus.Active or ProjectStatus.Planning)
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

        /// <summary>
        /// Called after Klives edits a project's budgets from the UI. Re-arms the once-only 80%
        /// warning if the new budget puts spend back under the warn line (otherwise a raised budget
        /// could never warn again), and reports whether spend is now within the token budget (the
        /// caller uses that to un-pause a BudgetPaused project).
        /// </summary>
        public bool NotifyBudgetChanged(string projectID)
        {
            var project = projectStore.GetProject(projectID);
            if (project == null) return false;
            lock (LockFor(projectID))
            {
                var ledger = LoadLocked(projectID);
                double fraction = project.TokenBudgetUsd > 0 ? ledger.TokenSpendUsd / project.TokenBudgetUsd : 0;
                if (ledger.TokenWarned && fraction < WarnFraction)
                {
                    ledger.TokenWarned = false;
                    SaveLocked(ledger);
                }
                return fraction < 1.0;
            }
        }

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
