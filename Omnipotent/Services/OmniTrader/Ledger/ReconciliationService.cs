using Omnipotent.Services.OmniTrader.Instruments;
using Omnipotent.Services.OmniTrader.Ops;
using Omnipotent.Services.OmniTrader.OrderFlow;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Risk;
using Omnipotent.Services.OmniTrader.Venues;

namespace Omnipotent.Services.OmniTrader.Ledger
{
    /// <summary>
    /// Compares internal state against broker truth and classifies every difference. It runs at
    /// startup, after reconnects, after ambiguous order outcomes, after fills, and on a schedule.
    ///
    /// It never silently "fixes" the ledger to match the broker: a difference becomes a classified
    /// break, corrections are posted as new entries, and material breaks block new automated exposure
    /// until a human clears them.
    /// </summary>
    public sealed class ReconciliationService
    {
        private readonly VenueRegistry venues;
        private readonly InstrumentMaster instruments;
        private readonly FirmLedger ledger;
        private readonly FirmOrderRepository orderRepo;
        private readonly ReconciliationRepository repo;
        private readonly AccountRepository accountRepo;
        private readonly OrderService orders;
        private readonly AlertService alerts;
        private readonly EmergencyControls emergency;
        private readonly Action<string>? log;

        public DateTime? LastRunUtc { get; private set; }
        public ReconciliationRun? LastRun { get; private set; }

        public ReconciliationService(
            VenueRegistry venues, InstrumentMaster instruments, FirmLedger ledger,
            FirmOrderRepository orderRepo, ReconciliationRepository repo, AccountRepository accountRepo,
            OrderService orders, AlertService alerts, EmergencyControls emergency, Action<string>? log = null)
        {
            this.venues = venues;
            this.instruments = instruments;
            this.ledger = ledger;
            this.orderRepo = orderRepo;
            this.repo = repo;
            this.accountRepo = accountRepo;
            this.orders = orders;
            this.alerts = alerts;
            this.emergency = emergency;
            this.log = log;
        }

        /// <summary>Reconcile every registered venue. Returns the runs, one per venue.</summary>
        public async Task<List<ReconciliationRun>> ReconcileAllAsync(string trigger, CancellationToken ct = default)
        {
            var runs = new List<ReconciliationRun>();
            foreach (var adapter in venues.All)
            {
                try { runs.Add(await ReconcileVenueAsync(adapter, trigger, ct)); }
                catch (Exception ex)
                {
                    log?.Invoke($"reconciliation of {adapter.Venue} failed: {ex.Message}");
                    await alerts.HighAsync("reconciliation", "Reconciliation failed",
                        $"Could not reconcile {adapter.Venue}/{adapter.Environment}: {ex.Message}",
                        dedupeKey: $"recon-fail:{adapter.Venue}:{adapter.Environment}", ct: ct);
                }
            }
            LastRunUtc = DateTime.UtcNow;
            return runs;
        }

        public async Task<ReconciliationRun> ReconcileVenueAsync(IVenueAdapter adapter, string trigger, CancellationToken ct = default)
        {
            var run = new ReconciliationRun
            {
                Id = Guid.NewGuid().ToString("N"),
                Venue = adapter.Venue,
                Environment = adapter.Environment,
                Trigger = trigger
            };

            // 1. Orders first — a fill we have not booked explains most position differences, so
            //    resolving orders before positions stops us raising breaks that are pure timing.
            await ReconcileOrdersAsync(adapter, run, ct);

            // 2. Positions / inventory.
            await ReconcilePositionsAsync(adapter, run, ct);

            // 3. Account balances.
            await ReconcileBalancesAsync(adapter, run, ct);

            // A break describes a *condition*, not an occurrence. Re-detecting the same unresolved
            // condition on the next sweep must not create a second row — otherwise a single
            // unexplained difference becomes fifty identical rows by lunchtime and the operator
            // stops reading any of them.
            var alreadyOpen = (await repo.ListOpenBreaksAsync(ct))
                .Select(BreakIdentity)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            int duplicates = run.Breaks.RemoveAll(b => !alreadyOpen.Add(BreakIdentity(b)));
            run.SuppressedDuplicates = duplicates;

            run.FinishedUtc = DateTime.UtcNow;
            await repo.SaveRunAsync(run, ct);
            LastRun = run;
            LastRunUtc = run.FinishedUtc;

            if (run.MaterialBreaks > 0)
            {
                await alerts.CriticalAsync("reconciliation", "Material reconciliation mismatch",
                    $"{run.MaterialBreaks} unexplained difference(s) between the internal ledger and {adapter.Venue}. "
                    + "New automated exposure is blocked until they are resolved.",
                    dedupeKey: $"recon-break:{adapter.Venue}:{adapter.Environment}",
                    recoveryHint: "Review the breaks on the Portfolio page and resolve or explain each one.",
                    ct: ct);
                emergency.EnterSafeMode($"{run.MaterialBreaks} unresolved reconciliation break(s) at {adapter.Venue}",
                    "reconciliation", automatic: true);
            }
            else
            {
                await alerts.ResolveByDedupeAsync($"recon-break:{adapter.Venue}:{adapter.Environment}", ct);
                await alerts.ResolveByDedupeAsync($"recon-fail:{adapter.Venue}:{adapter.Environment}", ct);
            }

            return run;
        }

        private async Task ReconcileOrdersAsync(IVenueAdapter adapter, ReconciliationRun run, CancellationToken ct)
        {
            var tracked = (await orderRepo.ListOpenAsync(ct))
                .Concat(await orderRepo.ListUnknownAsync(ct))
                .Where(o => o.Venue == adapter.Venue && o.Environment == adapter.Environment)
                .DistinctBy(o => o.Id)
                .ToList();

            foreach (var order in tracked)
            {
                var before = order.State;
                await orders.ReconcileOrderAsync(order, adapter, ct);
                run.Checked++;

                if (order.State == FirmOrderState.Unknown)
                {
                    run.Breaks.Add(new ReconciliationBreak
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        RunId = run.Id,
                        Venue = adapter.Venue,
                        Environment = adapter.Environment,
                        Kind = BreakKind.Order,
                        // We asked the venue and it could not tell us. That is not timing.
                        Classification = BreakClassification.Unexplained,
                        Subject = order.ClientReference,
                        Detail = $"Order {order.ClientReference} ({order.Side} {order.Quantity} {order.VenueSymbol}) "
                               + $"still has an unproven outcome. Last error: {order.Error ?? "none"}."
                    });
                }
                else if (before == FirmOrderState.Unknown)
                {
                    log?.Invoke($"reconciliation resolved {order.ClientReference}: {before} → {order.State}");
                }
            }

            // Working orders the venue knows about that we have no record of: someone placed them
            // outside the platform, or a submission we treated as failed actually landed.
            IReadOnlyList<VenueOrderSnapshot> working;
            try { working = await adapter.GetWorkingOrdersAsync(ct); }
            catch { return; }

            foreach (var snapshot in working)
            {
                if (string.IsNullOrWhiteSpace(snapshot.VenueOrderId)) continue;
                var known = await orderRepo.GetByVenueOrderIdAsync(snapshot.VenueOrderId, ct);
                if (known != null) continue;
                if (!string.IsNullOrWhiteSpace(snapshot.ClientReference)
                    && await orderRepo.GetByClientReferenceAsync(snapshot.ClientReference!, ct) != null) continue;

                run.Breaks.Add(new ReconciliationBreak
                {
                    Id = Guid.NewGuid().ToString("N"),
                    RunId = run.Id,
                    Venue = adapter.Venue,
                    Environment = adapter.Environment,
                    Kind = BreakKind.Order,
                    Classification = BreakClassification.ExternalManualActivity,
                    Subject = snapshot.VenueOrderId,
                    VenueValue = snapshot.Quantity,
                    Detail = $"{adapter.Venue} reports a working order ({snapshot.Side} {snapshot.Quantity} "
                           + $"{snapshot.VenueSymbol}) that the platform did not place."
                });
            }
        }

        private async Task ReconcilePositionsAsync(IVenueAdapter adapter, ReconciliationRun run, CancellationToken ct)
        {
            IReadOnlyList<VenuePositionSnapshot> venuePositions;
            try { venuePositions = await adapter.GetPositionsAsync(ct); }
            catch (Exception ex) { run.Error = ex.Message; return; }

            var venueByInstrument = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in venuePositions)
            {
                var instrument = instruments.Resolve(p.VenueSymbol, adapter.Venue);
                string id = instrument?.Id ?? p.VenueSymbol;
                venueByInstrument[id] = venueByInstrument.TryGetValue(id, out var q) ? q + p.Quantity : p.Quantity;
            }

            var internalPositions = ledger.Positions
                .Where(p => p.Venue == adapter.Venue && p.Environment == adapter.Environment)
                .ToList();

            foreach (var position in internalPositions)
            {
                run.Checked++;
                decimal venueQty = venueByInstrument.TryGetValue(position.InstrumentId, out var v) ? v : 0m;
                position.VenueQuantity = venueQty;
                decimal difference = position.Quantity - venueQty;
                if (Math.Abs(difference) <= Tolerance(position.Quantity)) continue;

                run.Breaks.Add(new ReconciliationBreak
                {
                    Id = Guid.NewGuid().ToString("N"),
                    RunId = run.Id,
                    Venue = adapter.Venue,
                    Environment = adapter.Environment,
                    Kind = BreakKind.Position,
                    Classification = await ClassifyPositionBreakAsync(adapter, position.InstrumentId, ct),
                    Subject = position.InstrumentId,
                    InternalValue = position.Quantity,
                    VenueValue = venueQty,
                    Detail = $"Internal position {position.Quantity} vs {adapter.Venue} {venueQty} "
                           + $"(difference {difference})."
                });
            }

            // Anything the venue holds that we do not track at all is *adopted*, not accused.
            //
            // An account that existed before this platform did — or that its owner tops up by hand —
            // is the normal case, not a discrepancy. Raising a break for every pre-existing coin
            // produced a screen full of red that said nothing except "you own things", and it
            // tripped safe mode for the crime of holding assets. What the platform actually needs to
            // know is that the holding exists, which is what adoption records.
            string accountId = $"{adapter.Venue}-{adapter.Environment}".ToLowerInvariant();
            foreach (var (instrumentId, qty) in venueByInstrument)
            {
                if (qty == 0m) continue;
                if (internalPositions.Any(p => string.Equals(p.InstrumentId, instrumentId, StringComparison.OrdinalIgnoreCase))) continue;

                decimal mark = 0m;
                try { mark = await adapter.GetLatestPriceAsync(instruments.VenueSymbolFor(instrumentId, adapter.Venue), ct); }
                catch { /* an unpriceable holding is still a holding */ }

                // Exchange dust — a fraction of a coin worth less than a coffee — is left alone.
                // Tracking it costs an operator more attention than the position is worth.
                if (mark > 0m && Math.Abs(qty * mark) < DustThreshold)
                {
                    run.DustIgnored++;
                    continue;
                }

                var instrument = instruments.Get(instrumentId);
                bool changed = await ledger.AdoptExternalHoldingAsync(accountId, adapter.Venue, adapter.Environment,
                    instrumentId, instrument?.Exposure ?? ExposureKind.Inventory, qty, mark > 0m ? mark : null, ct);
                if (changed)
                {
                    run.Adopted++;
                    log?.Invoke($"adopted {qty} {instrumentId} already held at {adapter.Venue}");
                }
            }
        }

        /// <summary>Holdings worth less than this in their quote currency are ignored rather than
        /// tracked. Exchanges leave dust behind constantly and it is never a decision.</summary>
        public const decimal DustThreshold = 1.0m;

        private async Task ReconcileBalancesAsync(IVenueAdapter adapter, ReconciliationRun run, CancellationToken ct)
        {
            VenueAccountSnapshot account;
            try { account = await adapter.GetAccountAsync(ct); }
            catch (Exception ex) { run.Error = ex.Message; return; }

            await accountRepo.RecordSnapshotAsync(account, ct);
            run.Checked++;

            // The internal cash figure is only meaningful once the platform has actually traded the
            // account; before that a difference is expected, not a break.
            string cashKey = $"{account.AccountId}|{account.BaseCurrency}";
            if (!ledger.CashBalances.TryGetValue(cashKey, out var internalCash)) return;
            if (account.Balance is not { } venueCash) return;

            decimal difference = internalCash - venueCash;
            if (Math.Abs(difference) <= Math.Max(0.01m, Math.Abs(venueCash) * 0.0001m)) return;

            run.Breaks.Add(new ReconciliationBreak
            {
                Id = Guid.NewGuid().ToString("N"),
                RunId = run.Id,
                Venue = adapter.Venue,
                Environment = adapter.Environment,
                Kind = BreakKind.Balance,
                Classification = BreakClassification.Unexplained,
                Subject = account.BaseCurrency,
                InternalValue = internalCash,
                VenueValue = venueCash,
                Detail = $"Internal cash {internalCash:F2} vs {adapter.Venue} {venueCash:F2} (difference {difference:F2})."
            });
        }

        /// <summary>A position difference with a fill booked in the last few minutes is almost always
        /// settlement timing; anything older needs a human's attention.</summary>
        private async Task<BreakClassification> ClassifyPositionBreakAsync(IVenueAdapter adapter, string instrumentId, CancellationToken ct)
        {
            var recent = await orderRepo.ListFilledSinceAsync(DateTime.UtcNow.AddMinutes(-5), ct);
            bool recentlyTraded = recent.Any(o => string.Equals(o.InstrumentId, instrumentId, StringComparison.OrdinalIgnoreCase)
                                               && o.Venue == adapter.Venue);
            return recentlyTraded ? BreakClassification.Timing : BreakClassification.Unexplained;
        }

        /// <summary>What makes two breaks "the same condition" for deduplication.</summary>
        private static string BreakIdentity(ReconciliationBreak b)
            => $"{b.Venue}:{b.Environment}:{b.Kind}:{b.Subject}";

        /// <summary>
        /// Collapse historical duplicates left by earlier runs: for each condition, keep the oldest
        /// open break and close the rest as duplicates. Runs once at startup so an operator inheriting
        /// a screen full of repeats gets one row per real problem.
        /// </summary>
        public async Task<int> CollapseDuplicateBreaksAsync(CancellationToken ct = default)
        {
            var open = await repo.ListOpenBreaksAsync(ct);
            int collapsed = 0;
            foreach (var group in open.GroupBy(BreakIdentity, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var duplicate in group.OrderBy(b => b.DetectedUtc).Skip(1))
                {
                    await repo.ResolveBreakAsync(duplicate.Id, "duplicate of an existing open break (auto)", ct);
                    collapsed++;
                }
            }
            if (collapsed > 0) log?.Invoke($"collapsed {collapsed} duplicate reconciliation break(s)");
            return collapsed;
        }

        /// <summary>Reclassify or resolve a break. Explaining a break is a recorded human judgement,
        /// not an edit to the ledger.</summary>
        public async Task<bool> ResolveBreakAsync(string breakId, string resolution, string who, CancellationToken ct = default)
        {
            await repo.ResolveBreakAsync(breakId, $"{resolution} (by {who})", ct);
            int remaining = await ledger.CountMaterialBreaksAsync(ct);
            if (remaining == 0 && emergency.SafeModeActive
                && (emergency.SafeModeReason?.Contains("reconciliation break", StringComparison.OrdinalIgnoreCase) ?? false))
                emergency.ExitSafeMode(who);
            return true;
        }

        public Task<List<ReconciliationBreak>> ListOpenBreaksAsync(CancellationToken ct = default)
            => repo.ListOpenBreaksAsync(ct);

        public Task<List<ReconciliationRun>> ListRunsAsync(int limit = 50, CancellationToken ct = default)
            => repo.ListRunsAsync(limit, ct);

        /// <summary>Quantity tolerance scaled to the position — crypto sizes are small, index CFDs are not.</summary>
        private static decimal Tolerance(decimal quantity) => Math.Max(0.00000001m, Math.Abs(quantity) * 0.0001m);
    }
}
