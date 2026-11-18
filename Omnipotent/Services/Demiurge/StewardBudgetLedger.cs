using System.Collections.Concurrent;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveAPI.Caching;

namespace Omnipotent.Services.Demiurge
{
    /// <summary>
    /// Per-Steward budget ledger (§8): tracks LLM token spend (in USD) and real-money spend,
    /// cumulative, against the Steward's budgets. Token cost is captured from the actual
    /// OpenRouter generation endpoint when a generation ID is available, with a per-model
    /// provisional estimate applied immediately so the UI is never blank and the ledger never
    /// under-counts if the fetch fails.
    ///
    /// Budgets themselves live on the Steward record (they are NOT OmniSettings) — this ledger
    /// only accrues spend and answers "how much is left / is this money spend autonomous".
    ///
    /// Layout: Demiurge/&lt;stewardID&gt;.ledger.json (one small doc, atomic rewrite).
    /// </summary>
    public class StewardBudgetLedger
    {
        private readonly string dir;
        private readonly StewardStore stewardStore;
        private readonly StewardEventLogStore eventLog;
        private readonly OpenRouterCostFetcher costFetcher;
        private readonly StewardTokenUsageStore? tokenUsage;
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

        /// <summary>Raised (stewardID) when a Steward crosses 100% and is auto-paused, so a surface can alert Klives.</summary>
        public event Action<string>? BudgetPausedRaised;

        /// <summary>
        /// Raised for every LLM turn booked here, with the full journal record. This is the one
        /// place every Demiurge model call — Commander, sub-agent, council and utility — already
        /// converges on, which is what lets <see cref="StewardCacheHealthMonitor"/> measure a rate
        /// across the whole fleet rather than per runner.
        /// </summary>
        public event Action<StewardTokenUsageRecord>? TokenUsageRecorded;

        /// <summary>
        /// A fleet-wide reason no agent may be admitted to a model call, or null when the fleet may
        /// run. Checked ahead of the per-Steward budget because it outranks it: a halt means no
        /// request is sent regardless of how much budget a Steward still has. Wired by
        /// <see cref="Demiurge"/> to the cache-health kill switch.
        /// </summary>
        public Func<string?>? FleetAdmissionBlock { get; set; }

        private string? FleetBlockReason()
        {
            try { return FleetAdmissionBlock?.Invoke(); }
            catch { return null; }
        }

        /// <summary>
        /// Why <see cref="TryAcquireLlmTurnAsync"/> would refuse this Steward right now, phrased for
        /// a wake's outcome text. Runners used to hard-code "no token budget remained", which became
        /// a lie the moment admission could be refused for any other reason.
        /// </summary>
        public string DescribeAdmissionRefusal(string stewardID)
        {
            string? fleet = FleetBlockReason();
            if (fleet != null) return fleet;
            var steward = stewardStore.GetSteward(stewardID);
            if (steward == null) return "the Steward no longer exists";
            if (steward.Status is not (StewardStatus.Active or StewardStatus.Planning))
                return $"the Steward status is {steward.Status}";
            return "no token budget remained";
        }

        /// <summary>
        /// True while the active router bills a FLAT FEE (AIRouter) rather than per token. Everything
        /// this ledger exists to police — turn reservations, the 80% warning, the 100% auto-pause —
        /// is arithmetic on money that, under a flat fee, is never actually charged. Metering it
        /// anyway would stop a Steward on a bill that does not exist, and a stopped Steward is
        /// exactly the wake deferral we are trying to make impossible.
        ///
        /// Token COUNTS are still recorded in full: they are real telemetry the analytics and usage
        /// views need. Only the USD attribution is zeroed.
        ///
        /// Wired by <c>Demiurge</c> to KliveLLM's provider setting; unset (null) means per-token
        /// billing, so behaviour is unchanged for every existing provider.
        /// </summary>
        public Func<bool>? IsFlatFeeProvider { get; set; }

        /// <summary>
        /// Whether the router one booked turn ran on charges nothing per token, from the source and
        /// operation it is booked under (each Demiurge lane can have its own router). Null when unknown:
        /// the ledger then uses <see cref="IsFlatFeeProvider"/>.
        /// </summary>
        public Func<string?, string?, bool?>? IsFlatFeeForTurn { get; set; }

        private bool FlatFee
        {
            get
            {
                try { return IsFlatFeeProvider?.Invoke() ?? false; }
                catch { return false; }
            }
        }

        private bool? SafeTurnFlatFee(StewardTokenUsageContext? context)
        {
            if (context == null || IsFlatFeeForTurn == null) return null;
            try { return IsFlatFeeForTurn(context.Source, context.Operation); }
            catch { return null; }
        }

        public StewardBudgetLedger(StewardStore stewardStore, StewardEventLogStore eventLog,
            OpenRouterCostFetcher costFetcher, Action<string> log,
            StewardTokenUsageStore? tokenUsage = null)
        {
            this.stewardStore = stewardStore;
            this.eventLog = eventLog;
            this.costFetcher = costFetcher;
            this.tokenUsage = tokenUsage;
            this.log = log ?? (_ => { });
            dir = OmniPaths.GetPath(OmniPaths.GlobalPaths.DemiurgeDirectory);
            Directory.CreateDirectory(dir);
        }

        public class Ledger
        {
            public string StewardID { get; set; } = "";
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

        private object LockFor(string stewardID) => locks.GetOrAdd(stewardID, _ => new object());
        private string LedgerPath(string stewardID) => Path.Combine(dir, stewardID + ".ledger.json");

        // Spend is read by /demiurge/list and /demiurge/state, so it must participate in the
        // response cache's version model or those views serve the spend they were first filled with.
        private static string CacheKey(string stewardID) => "demiurge:budget:" + stewardID;

        public Ledger GetLedger(string stewardID)
        {
            CacheDeps.NoteRead(CacheKey(stewardID));
            lock (LockFor(stewardID)) return LoadLocked(stewardID);
        }

        /// <summary>Cumulative token and money spend, served from memory. Every write goes through
        /// <see cref="SaveLocked"/>, which refreshes the entry, so this never lags the ledger.</summary>
        public Spend GetSpend(string stewardID)
        {
            CacheDeps.NoteRead(CacheKey(stewardID));
            if (spendCache.TryGetValue(stewardID, out var cached)) return cached;
            lock (LockFor(stewardID))
            {
                if (spendCache.TryGetValue(stewardID, out cached)) return cached;
                var ledger = LoadLocked(stewardID);
                var spend = new Spend(ledger.TokenSpendUsd, ledger.MoneySpendUsd);
                spendCache[stewardID] = spend;
                return spend;
            }
        }

        private sealed class LlmTurnLease : IAsyncDisposable
        {
            private StewardBudgetLedger? owner;
            private readonly string stewardID;
            private readonly double reservedUsd;

            public LlmTurnLease(StewardBudgetLedger owner, string stewardID, double reservedUsd)
            {
                this.owner = owner;
                this.stewardID = stewardID;
                this.reservedUsd = reservedUsd;
            }

            public ValueTask DisposeAsync()
            {
                Interlocked.Exchange(ref owner, null)?.ReleaseReservation(stewardID, reservedUsd);
                return ValueTask.CompletedTask;
            }
        }

        /// <summary>
        /// Reserves a conservative slice of the remaining Steward budget for one provider turn.
        /// The per-Steward gate is held only while checking and reserving, never across the HTTP
        /// call. This preserves multi-agent concurrency while preventing a burst of callers from
        /// all observing the same uncommitted final cents. Reservations are process-local because
        /// provider calls cannot survive a process restart.
        /// </summary>
        public async Task<IAsyncDisposable?> TryAcquireLlmTurnAsync(string stewardID, CancellationToken ct = default)
        {
            // Ahead of the per-Steward gate: a fleet halt is not a budget question, and refusing here
            // means a halted fleet never even contends for the reservation semaphore.
            if (FleetBlockReason() != null) return null;
            var gate = llmTurnGates.GetOrAdd(stewardID, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                var steward = stewardStore.GetSteward(stewardID);
                // Under a flat fee there is nothing to reserve against: the turn is admitted on the
                // Steward being runnable alone. Returning null here is what makes a wake report
                // WakeDeferred, so a flat-fee router must never reach that path.
                if (FlatFee)
                {
                    bool flatFeeRunnable = steward?.Status is StewardStatus.Active or StewardStatus.Planning;
                    return flatFeeRunnable ? new LlmTurnLease(this, stewardID, 0) : null;
                }
                double reservation = 0;
                lock (LockFor(stewardID))
                {
                    var ledger = LoadLocked(stewardID);
                    llmTurnReservations.TryGetValue(stewardID, out double alreadyReserved);
                    bool runnable = steward?.Status is StewardStatus.Active or StewardStatus.Planning;
                    double remaining = steward == null ? 0 : steward.TokenBudgetUsd - ledger.TokenSpendUsd - alreadyReserved;
                    if (runnable && steward!.TokenBudgetUsd > 0 && remaining > 0)
                    {
                        reservation = Math.Min(DefaultTurnReservationUsd, remaining);
                        llmTurnReservations[stewardID] = alreadyReserved + reservation;
                    }
                }
                if (reservation > 0)
                    return new LlmTurnLease(this, stewardID, reservation);
            }
            finally
            {
                gate.Release();
            }
            CheckTokenThresholds(stewardID);
            return null;
        }

        private void ReleaseReservation(string stewardID, double amountUsd)
        {
            lock (LockFor(stewardID))
            {
                llmTurnReservations.TryGetValue(stewardID, out double reserved);
                double remaining = Math.Max(0, reserved - amountUsd);
                if (remaining <= 0.0000001) llmTurnReservations.TryRemove(stewardID, out _);
                else llmTurnReservations[stewardID] = remaining;
            }
        }

        public bool IsWithinTokenBudget(string stewardID)
        {
            var steward = stewardStore.GetSteward(stewardID);
            if (steward == null) return false;
            if (FlatFee) return true; // no per-token bill to exceed
            if (steward.TokenBudgetUsd <= 0) return false;
            lock (LockFor(stewardID)) return LoadLocked(stewardID).TokenSpendUsd < steward.TokenBudgetUsd;
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
            string stewardID,
            long promptTokens,
            long completionTokens,
            string? generationId = null,
            double? actualCostUsd = null,
            StewardTokenUsageContext? usageContext = null,
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
            // The turn's own router decides: its response said so, or its lane is asked.
            bool flatFee = usageContext?.FlatFee
                ?? SafeTurnFlatFee(usageContext)
                ?? FlatFee;
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

            lock (LockFor(stewardID))
            {
                var ledger = LoadLocked(stewardID);
                ledger.PromptTokens += promptTokens;
                ledger.CompletionTokens += completionTokens;
                ledger.TokenSpendUsd += amount;
                if (!haveActual && !string.IsNullOrWhiteSpace(generationId))
                    ledger.PendingReconcile[generationId] = amount;
                SaveLocked(ledger);
            }

            var pendingUsage = new StewardTokenUsageRecord
            {
                RecordKind = "usage",
                StewardID = stewardID,
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
                CacheMetricsAvailable = usageContext?.CacheMetricsAvailable ?? false,
                CacheWritePromptTokens = usageContext?.CacheWritePromptTokens ?? 0,
                Provider = usageContext?.Provider,
                RoutedProvider = usageContext?.RoutedProvider,
                UpstreamInferenceCostUsd = usageContext?.UpstreamInferenceCostUsd,
                TurnIndex = usageContext?.TurnIndex ?? 0,
                RequestDurationMs = usageContext?.RequestDurationMs ?? 0,
                QueueDurationMs = usageContext?.QueueDurationMs ?? 0,
                ProviderDurationMs = usageContext?.ProviderDurationMs ?? 0,
                LatencyBreakdownAvailable = usageContext?.LatencyBreakdownAvailable ?? false,
                RouterStrategy = usageContext?.RouterStrategy,
                RouterAttempt = usageContext?.RouterAttempt,
                ContextWasCompacted = usageContext?.ContextWasCompacted ?? false,
                CacheEpochID = usageContext?.CacheEpochID,
                CacheEpochTurnIndex = usageContext?.CacheEpochTurnIndex ?? 0,
                PromptAssemblyStatus = usageContext?.PromptAssemblyStatus,
                AppendedBriefTokens = usageContext?.AppendedBriefTokens ?? 0,
                FullBriefTokens = usageContext?.FullBriefTokens ?? 0,
                CacheSessionID = usageContext?.CacheSessionID,
                ResponseCacheStatus = usageContext?.ResponseCacheStatus,
                PromptCacheTelemetryVersion = usageContext?.PromptCacheTelemetryVersion,
                CostUsd = amount,
                // "flat-fee" rather than "actual" so the usage view can say WHY a turn cost nothing,
                // instead of leaving a reader to wonder whether the figure simply failed to arrive.
                CostBasis = flatFee ? "flat-fee" : haveActual ? "actual" : "provisional",
                GenerationID = generationId,
            };
            StewardTokenUsageRecord? usageRecord = tokenUsage?.TryAppend(pendingUsage);

            // Announce the turn even when journaling failed. A cache collapse is exactly the kind of
            // incident that arrives alongside disk trouble, and the kill switch must not be the thing
            // that goes quiet first. TryAppend normalises in place, so the fallback carries the same
            // values minus the assigned sequence.
            try { TokenUsageRecorded?.Invoke(usageRecord ?? pendingUsage); }
            catch (Exception ex) { log($"Token-usage observer failed for {stewardID}: {ex.Message}"); }

            CheckTokenThresholds(stewardID);

            if (!haveActual && !string.IsNullOrWhiteSpace(generationId))
                _ = Task.Run(() => ReconcileAsync(stewardID, generationId!, usageRecord));
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
                foreach (var steward in stewardStore.ListStewards())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string[] generations;
                    lock (LockFor(steward.StewardID))
                        generations = LoadLocked(steward.StewardID).PendingReconcile.Keys.ToArray();
                    if (generations.Length == 0) continue;

                    var generationSet = generations.ToHashSet(StringComparer.Ordinal);
                    var provisionalByGeneration = tokenUsage?
                        .EnumerateRange(steward.StewardID, null, null)
                        .Where(record =>
                            !string.IsNullOrWhiteSpace(record.GenerationID)
                            && generationSet.Contains(record.GenerationID)
                            && string.Equals(record.CostBasis, "provisional", StringComparison.OrdinalIgnoreCase))
                        .GroupBy(record => record.GenerationID!, StringComparer.Ordinal)
                        .ToDictionary(
                            group => group.Key,
                            group => group.OrderByDescending(record => record.Sequence).First(),
                            StringComparer.Ordinal)
                        ?? new Dictionary<string, StewardTokenUsageRecord>(StringComparer.Ordinal);

                    foreach (string generationID in generations)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        provisionalByGeneration.TryGetValue(generationID, out var provisionalUsage);
                        await ReconcileAsync(
                            steward.StewardID, generationID, provisionalUsage, cancellationToken);
                    }
                }
            }
            finally
            {
                reconcileSweepGate.Release();
            }
        }

        private async Task ReconcileAsync(
            string stewardID,
            string generationId,
            StewardTokenUsageRecord? provisionalUsage,
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
                lock (LockFor(stewardID))
                {
                    var ledger = LoadLocked(stewardID);
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
                    tokenUsage?.TryAppend(new StewardTokenUsageRecord
                    {
                        RecordKind = "cost-adjustment",
                        StewardID = stewardID,
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
                CheckTokenThresholds(stewardID);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) { log($"Budget reconcile failed for {stewardID}/{generationId}: {ex.Message}"); }
        }

        /// <summary>
        /// Whether a single real-money spend is autonomous: at/below the Steward's per-action
        /// threshold AND within the remaining money budget. Above the threshold → Discord
        /// approval (P5). Stricter than, and separate from, the token budget.
        /// </summary>
        public bool IsMoneySpendAutonomous(string stewardID, double amountUsd)
        {
            if (!double.IsFinite(amountUsd) || amountUsd <= 0) return false;
            var steward = stewardStore.GetSteward(stewardID);
            if (steward == null) return false;
            if (amountUsd > steward.MoneyAutonomousThresholdUsd) return false;
            lock (LockFor(stewardID))
            {
                var ledger = LoadLocked(stewardID);
                return ledger.MoneySpendUsd + amountUsd <= steward.MoneyBudgetUsd;
            }
        }

        /// <summary>Records a real-money spend against the ledger (after it happened / was approved).</summary>
        public void RecordMoneySpend(string stewardID, double amountUsd, string description)
        {
            if (!double.IsFinite(amountUsd) || amountUsd <= 0)
                throw new ArgumentOutOfRangeException(nameof(amountUsd), "Money spend must be positive and finite.");
            lock (LockFor(stewardID))
            {
                var ledger = LoadLocked(stewardID);
                ledger.MoneySpendUsd += amountUsd;
                SaveLocked(ledger);
            }
            eventLog.Append(new StewardEvent
            {
                StewardID = stewardID,
                Type = StewardEventTypes.MoneySpent,
                Author = "system",
                Text = $"Real-money spend ${amountUsd:0.##}: {description}",
                PayloadJson = JsonConvert.SerializeObject(new { amountUsd, description }),
            });
        }

        private void CheckTokenThresholds(string stewardID)
        {
            // The 80% warning and the 100% auto-pause both measure spend that a flat fee never
            // incurs. Skipping them is what guarantees a flat-fee Steward can never be paused —
            // and therefore never deferred — by a budget it is not actually consuming.
            if (FlatFee) return;
            var steward = stewardStore.GetSteward(stewardID);
            if (steward == null || steward.TokenBudgetUsd <= 0) return;

            Ledger ledger;
            lock (LockFor(stewardID)) ledger = LoadLocked(stewardID);
            double fraction = ledger.TokenSpendUsd / steward.TokenBudgetUsd;

            if (fraction >= 1.0 && steward.Status is StewardStatus.Active or StewardStatus.Planning)
            {
                StewardStatus fromStatus = steward.Status;
                steward.Status = StewardStatus.BudgetPaused;
                stewardStore.SaveSteward(steward);
                eventLog.Append(new StewardEvent
                {
                    StewardID = stewardID,
                    Type = StewardEventTypes.BudgetPaused,
                    Author = "system",
                    PayloadJson = StewardLifecycleEvents.Payload(
                        fromStatus, StewardStatus.BudgetPaused, "token-budget-exhausted"),
                    Text = $"Token budget exhausted (${ledger.TokenSpendUsd:0.##} of ${steward.TokenBudgetUsd:0.##}). Steward paused — a budget conversation with Klives is required to continue.",
                });
                try { BudgetPausedRaised?.Invoke(stewardID); } catch { }
            }
            else if (fraction >= WarnFraction && !ledger.TokenWarned)
            {
                lock (LockFor(stewardID))
                {
                    var l = LoadLocked(stewardID);
                    l.TokenWarned = true;
                    SaveLocked(l);
                }
                eventLog.Append(new StewardEvent
                {
                    StewardID = stewardID,
                    Type = StewardEventTypes.BudgetWarning,
                    Author = "system",
                    Text = $"Token budget at {fraction:P0} (${ledger.TokenSpendUsd:0.##} of ${steward.TokenBudgetUsd:0.##}).",
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
        /// Called after Klives edits a Steward's budgets from the UI. Re-arms the once-only 80%
        /// warning if the new budget puts spend back under the warn line (otherwise a raised budget
        /// could never warn again), and reports whether spend is now within the token budget (the
        /// caller uses that to un-pause a BudgetPaused Steward).
        /// </summary>
        public bool NotifyBudgetChanged(string stewardID)
        {
            var steward = stewardStore.GetSteward(stewardID);
            if (steward == null) return false;
            lock (LockFor(stewardID))
            {
                var ledger = LoadLocked(stewardID);
                double fraction = steward.TokenBudgetUsd > 0 ? ledger.TokenSpendUsd / steward.TokenBudgetUsd : 0;
                if (ledger.TokenWarned && fraction < WarnFraction)
                {
                    ledger.TokenWarned = false;
                    SaveLocked(ledger);
                }
                return fraction < 1.0;
            }
        }

        /// <summary>Compact human-readable budget state for the standing digest / wake seed.</summary>
        public string DescribeState(string stewardID)
        {
            var steward = stewardStore.GetSteward(stewardID);
            var ledger = GetLedger(stewardID);
            if (steward == null) return "unknown Steward";
            // Under a flat fee the token budget is not a constraint the agent should reason about —
            // saying "$0.00/$50 (0%)" every wake would invite it to economise for no reason.
            if (FlatFee)
                return $"tokens unmetered (flat-fee router; {ledger.PromptTokens + ledger.CompletionTokens:N0} tokens used, no per-token charge), " +
                       $"money ${ledger.MoneySpendUsd:0.##}/${steward.MoneyBudgetUsd:0.##}";
            return $"tokens ${ledger.TokenSpendUsd:0.##}/${steward.TokenBudgetUsd:0.##} " +
                   $"({(steward.TokenBudgetUsd > 0 ? ledger.TokenSpendUsd / steward.TokenBudgetUsd : 0):P0}), " +
                   $"money ${ledger.MoneySpendUsd:0.##}/${steward.MoneyBudgetUsd:0.##}";
        }

        private Ledger LoadLocked(string stewardID)
        {
            string path = LedgerPath(stewardID);
            if (!File.Exists(path)) return new Ledger { StewardID = stewardID };
            try { return JsonConvert.DeserializeObject<Ledger>(File.ReadAllText(path)) ?? new Ledger { StewardID = stewardID }; }
            catch { return new Ledger { StewardID = stewardID }; }
        }

        private void SaveLocked(Ledger ledger)
        {
            ledger.UpdatedAt = DateTime.UtcNow;
            string path = LedgerPath(ledger.StewardID);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(ledger, Formatting.Indented));
            File.Move(tmp, path, overwrite: true);
            // Single write chokepoint: refresh the projection and invalidate cached responses
            // together, so no future mutator can update spend without both following.
            spendCache[ledger.StewardID] = new Spend(ledger.TokenSpendUsd, ledger.MoneySpendUsd);
            CacheDeps.Bump(CacheKey(ledger.StewardID));
        }
    }
}
