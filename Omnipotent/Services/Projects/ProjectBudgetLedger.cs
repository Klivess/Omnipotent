using System.Collections.Concurrent;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveAPI.Caching;

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
        private readonly ProjectTokenUsageStore? tokenUsage;
        private readonly Action<string> log;
        private readonly ConcurrentDictionary<string, object> locks = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> llmTurnGates = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, double> llmTurnReservations = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim reconcileSweepGate = new(1, 1);

        // Provisional per-million-token USD estimate, used until the real cost reconciles.
        // Same yardstick style as KliveAgentStats; the real OpenRouter figure supersedes it.
        private const double ProvisionalPromptPerMillion = 3.0;
        private const double ProvisionalCompletionPerMillion = 15.0;

        private const double WarnFraction = 0.80;
        private const double DefaultTurnReservationUsd = 0.05;

        /// <summary>Raised (projectID) when a project crosses 100% and is auto-paused, so a surface can alert Klives.</summary>
        public event Action<string>? BudgetPausedRaised;

        /// <summary>
        /// True while the active router bills a FLAT FEE (AIRouter) rather than per token. Everything
        /// this ledger exists to police — turn reservations, the 80% warning, the 100% auto-pause —
        /// is arithmetic on money that, under a flat fee, is never actually charged. Metering it
        /// anyway would stop a project on a bill that does not exist, and a stopped project is
        /// exactly the wake deferral we are trying to make impossible.
        ///
        /// Token COUNTS are still recorded in full: they are real telemetry the analytics and usage
        /// views need. Only the USD attribution is zeroed.
        ///
        /// Wired by <c>Projects</c> to KliveLLM's provider setting; unset (null) means per-token
        /// billing, so behaviour is unchanged for every existing provider.
        /// </summary>
        public Func<bool>? IsFlatFeeProvider { get; set; }

        private bool FlatFee
        {
            get
            {
                try { return IsFlatFeeProvider?.Invoke() ?? false; }
                catch { return false; }
            }
        }

        public ProjectBudgetLedger(ProjectStore projectStore, ProjectEventLogStore eventLog,
            OpenRouterCostFetcher costFetcher, Action<string> log,
            ProjectTokenUsageStore? tokenUsage = null)
        {
            this.projectStore = projectStore;
            this.eventLog = eventLog;
            this.costFetcher = costFetcher;
            this.tokenUsage = tokenUsage;
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

        /// <summary>Spend as the fleet list and budget bars render it — the only two numbers those
        /// views need, cached so the list never re-reads N ledger files per request.</summary>
        public readonly record struct Spend(double TokenSpendUsd, double MoneySpendUsd);

        private readonly ConcurrentDictionary<string, Spend> spendCache = new(StringComparer.Ordinal);

        private object LockFor(string projectID) => locks.GetOrAdd(projectID, _ => new object());
        private string LedgerPath(string projectID) => Path.Combine(dir, projectID + ".ledger.json");

        // Spend is read by /projects/list and /projects/state, so it must participate in the
        // response cache's version model or those views serve the spend they were first filled with.
        private static string CacheKey(string projectID) => "projects:budget:" + projectID;

        public Ledger GetLedger(string projectID)
        {
            CacheDeps.NoteRead(CacheKey(projectID));
            lock (LockFor(projectID)) return LoadLocked(projectID);
        }

        /// <summary>Cumulative token and money spend, served from memory. Every write goes through
        /// <see cref="SaveLocked"/>, which refreshes the entry, so this never lags the ledger.</summary>
        public Spend GetSpend(string projectID)
        {
            CacheDeps.NoteRead(CacheKey(projectID));
            if (spendCache.TryGetValue(projectID, out var cached)) return cached;
            lock (LockFor(projectID))
            {
                if (spendCache.TryGetValue(projectID, out cached)) return cached;
                var ledger = LoadLocked(projectID);
                var spend = new Spend(ledger.TokenSpendUsd, ledger.MoneySpendUsd);
                spendCache[projectID] = spend;
                return spend;
            }
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
                // Under a flat fee there is nothing to reserve against: the turn is admitted on the
                // project being runnable alone. Returning null here is what makes a wake report
                // WakeDeferred, so a flat-fee router must never reach that path.
                if (FlatFee)
                {
                    bool flatFeeRunnable = project?.Status is ProjectStatus.Active or ProjectStatus.Planning;
                    return flatFeeRunnable ? new LlmTurnLease(this, projectID, 0) : null;
                }
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
            if (project == null) return false;
            if (FlatFee) return true; // no per-token bill to exceed
            if (project.TokenBudgetUsd <= 0) return false;
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
        public async Task RecordTokenSpendAsync(
            string projectID,
            long promptTokens,
            long completionTokens,
            string? generationId = null,
            double? actualCostUsd = null,
            ProjectTokenUsageContext? usageContext = null,
            long cachedPromptTokens = 0)
        {
            // The completion already carries the real cost — book it and skip the estimate/reconcile
            // path entirely. A provider that doesn't report cost (HuggingFace/local) falls back to the
            // flat provisional, which the /generation fetch later reconciles when a generation ID exists.
            promptTokens = Math.Max(0, promptTokens);
            completionTokens = Math.Max(0, completionTokens);
            // A flat-fee router charges nothing per token, so there is no real figure to book and no
            // estimate worth making: the cost IS zero, and it is recorded as authoritative ("actual")
            // rather than provisional so nothing later tries to reconcile it against a per-token
            // pricing endpoint that knows nothing about this generation.
            bool flatFee = FlatFee;
            bool haveActual = flatFee
                || (actualCostUsd.HasValue
                    && double.IsFinite(actualCostUsd.Value)
                    && actualCostUsd.Value >= 0);
            double amount = flatFee
                ? 0
                : haveActual
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

            ProjectTokenUsageRecord? usageRecord = tokenUsage?.TryAppend(new ProjectTokenUsageRecord
            {
                RecordKind = "usage",
                ProjectID = projectID,
                OccurredAt = usageContext?.OccurredAt ?? DateTime.UtcNow,
                WakeID = usageContext?.WakeID,
                AgentID = usageContext?.AgentID ?? "system",
                Source = usageContext?.Source ?? "unknown",
                Operation = usageContext?.Operation,
                Model = usageContext?.Model ?? "unknown",
                SourceReference = usageContext?.SourceReference,
                Label = usageContext?.Label,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                CachedPromptTokens = Math.Clamp(cachedPromptTokens, 0, promptTokens),
                CostUsd = amount,
                // "flat-fee" rather than "actual" so the usage view can say WHY a turn cost nothing,
                // instead of leaving a reader to wonder whether the figure simply failed to arrive.
                CostBasis = flatFee ? "flat-fee" : haveActual ? "actual" : "provisional",
                GenerationID = generationId,
            });

            CheckTokenThresholds(projectID);

            if (!haveActual && !string.IsNullOrWhiteSpace(generationId))
                _ = Task.Run(() => ReconcileAsync(projectID, generationId!, usageRecord));
        }

        /// <summary>
        /// Resumes provider-cost reconciliation for provisional generations that survived a
        /// process restart. The ledger's pending map is authoritative; the usage journal is
        /// consulted only to preserve the original time/model/wake attribution.
        /// </summary>
        public async Task ReconcilePendingAsync(CancellationToken cancellationToken = default)
        {
            if (!await reconcileSweepGate.WaitAsync(0, cancellationToken)) return;
            try
            {
                foreach (var project in projectStore.ListProjects())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string[] generations;
                    lock (LockFor(project.ProjectID))
                        generations = LoadLocked(project.ProjectID).PendingReconcile.Keys.ToArray();
                    if (generations.Length == 0) continue;

                    var generationSet = generations.ToHashSet(StringComparer.Ordinal);
                    var provisionalByGeneration = tokenUsage?
                        .EnumerateRange(project.ProjectID, null, null)
                        .Where(record =>
                            !string.IsNullOrWhiteSpace(record.GenerationID)
                            && generationSet.Contains(record.GenerationID)
                            && string.Equals(record.CostBasis, "provisional", StringComparison.OrdinalIgnoreCase))
                        .GroupBy(record => record.GenerationID!, StringComparer.Ordinal)
                        .ToDictionary(
                            group => group.Key,
                            group => group.OrderByDescending(record => record.Sequence).First(),
                            StringComparer.Ordinal)
                        ?? new Dictionary<string, ProjectTokenUsageRecord>(StringComparer.Ordinal);

                    foreach (string generationID in generations)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        provisionalByGeneration.TryGetValue(generationID, out var provisionalUsage);
                        await ReconcileAsync(
                            project.ProjectID, generationID, provisionalUsage, cancellationToken);
                    }
                }
            }
            finally
            {
                reconcileSweepGate.Release();
            }
        }

        private async Task ReconcileAsync(
            string projectID,
            string generationId,
            ProjectTokenUsageRecord? provisionalUsage,
            CancellationToken cancellationToken = default)
        {
            try
            {
                double? real = await costFetcher.TryGetCostAsync(
                    generationId, ct: cancellationToken);
                if (real == null || !double.IsFinite(real.Value) || real.Value < 0)
                    return; // keep the provisional figure
                double adjustment = 0;
                bool reconciled = false;
                lock (LockFor(projectID))
                {
                    var ledger = LoadLocked(projectID);
                    if (ledger.PendingReconcile.TryGetValue(generationId, out double prov))
                    {
                        adjustment = real.Value - prov;
                        ledger.TokenSpendUsd += adjustment; // swap estimate for truth
                        ledger.PendingReconcile.Remove(generationId);
                        SaveLocked(ledger);
                        reconciled = true;
                    }
                }
                if (reconciled && provisionalUsage != null)
                {
                    tokenUsage?.TryAppend(new ProjectTokenUsageRecord
                    {
                        RecordKind = "cost-adjustment",
                        ProjectID = projectID,
                        OccurredAt = provisionalUsage.OccurredAt,
                        WakeID = provisionalUsage.WakeID,
                        AgentID = provisionalUsage.AgentID,
                        Source = provisionalUsage.Source,
                        Operation = provisionalUsage.Operation,
                        Model = provisionalUsage.Model,
                        SourceReference = provisionalUsage.SourceReference,
                        Label = provisionalUsage.Label,
                        CostUsd = adjustment,
                        CostBasis = "reconciliation",
                        GenerationID = generationId,
                        ReconcilesUsageID = provisionalUsage.UsageID,
                    });
                }
                CheckTokenThresholds(projectID);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
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
                PayloadJson = JsonConvert.SerializeObject(new { amountUsd, description }),
            });
        }

        private void CheckTokenThresholds(string projectID)
        {
            // The 80% warning and the 100% auto-pause both measure spend that a flat fee never
            // incurs. Skipping them is what guarantees a flat-fee project can never be paused —
            // and therefore never deferred — by a budget it is not actually consuming.
            if (FlatFee) return;
            var project = projectStore.GetProject(projectID);
            if (project == null || project.TokenBudgetUsd <= 0) return;

            Ledger ledger;
            lock (LockFor(projectID)) ledger = LoadLocked(projectID);
            double fraction = ledger.TokenSpendUsd / project.TokenBudgetUsd;

            if (fraction >= 1.0 && project.Status is ProjectStatus.Active or ProjectStatus.Planning)
            {
                ProjectStatus fromStatus = project.Status;
                project.Status = ProjectStatus.BudgetPaused;
                projectStore.SaveProject(project);
                eventLog.Append(new ProjectEvent
                {
                    ProjectID = projectID,
                    Type = ProjectEventTypes.BudgetPaused,
                    Author = "system",
                    PayloadJson = ProjectLifecycleEvents.Payload(
                        fromStatus, ProjectStatus.BudgetPaused, "token-budget-exhausted"),
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
        /// per-wake cost attribution in the timeline. The reconciled OpenRouter figure supersedes it
        /// cumulatively. Zero under a flat fee — the tokens are real, the charge is not.</summary>
        public double EstimateCost(long promptTokens, long completionTokens)
            => FlatFee
             ? 0
             : promptTokens / 1_000_000.0 * ProvisionalPromptPerMillion
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
            // Under a flat fee the token budget is not a constraint the agent should reason about —
            // saying "$0.00/$50 (0%)" every wake would invite it to economise for no reason.
            if (FlatFee)
                return $"tokens unmetered (flat-fee router; {ledger.PromptTokens + ledger.CompletionTokens:N0} tokens used, no per-token charge), " +
                       $"money ${ledger.MoneySpendUsd:0.##}/${project.MoneyBudgetUsd:0.##}";
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
            // Single write chokepoint: refresh the projection and invalidate cached responses
            // together, so no future mutator can update spend without both following.
            spendCache[ledger.ProjectID] = new Spend(ledger.TokenSpendUsd, ledger.MoneySpendUsd);
            CacheDeps.Bump(CacheKey(ledger.ProjectID));
        }
    }
}
