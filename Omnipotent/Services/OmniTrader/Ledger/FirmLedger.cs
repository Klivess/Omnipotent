using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Instruments;
using Omnipotent.Services.OmniTrader.OrderFlow;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Venues;
using System.Collections.Concurrent;

namespace Omnipotent.Services.OmniTrader.Ledger
{
    /// <summary>
    /// The firm's internal book: balances, inventory, positions, costs and realized P&amp;L, built by
    /// appending immutable entries. It does not replace broker reconciliation — broker-reported orders
    /// and fills determine external reality, and this ledger provides attribution, history and the
    /// thing reconciliation compares against.
    ///
    /// Positions are kept per (account, instrument) with the exposure kind carried through, so CFD
    /// notional is never summed into owned spot inventory.
    /// </summary>
    public sealed class FirmLedger
    {
        private readonly LedgerRepository repo;
        private readonly ReconciliationRepository reconRepo;
        private readonly InstrumentMaster instruments;
        private readonly ConcurrentDictionary<string, FirmPosition> positions = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, decimal> cash = new(StringComparer.OrdinalIgnoreCase);
        private readonly object bookLock = new();

        public FirmLedger(LedgerRepository repo, ReconciliationRepository reconRepo, InstrumentMaster instruments)
        {
            this.repo = repo;
            this.reconRepo = reconRepo;
            this.instruments = instruments;
        }

        private static string PositionKey(string accountId, string instrumentId) => $"{accountId}|{instrumentId}";
        private static string CashKey(string accountId, string currency) => $"{accountId}|{currency}";

        public IReadOnlyList<FirmPosition> Positions => positions.Values.Where(p => p.Quantity != 0m).ToList();
        public IReadOnlyDictionary<string, decimal> CashBalances => cash;

        public FirmPosition? GetPosition(string accountId, string instrumentId)
            => positions.TryGetValue(PositionKey(accountId, instrumentId), out var p) ? p : null;

        /// <summary>Rebuild the in-memory book from the durable entry log. Called at startup, so a
        /// restart never invents or loses a position.</summary>
        public async Task RehydrateAsync(CancellationToken ct = default)
        {
            var entries = await repo.ListAsync(limit: 20_000, ct: ct);
            entries.Reverse(); // repository returns newest-first; replay chronologically
            lock (bookLock)
            {
                positions.Clear();
                cash.Clear();
                foreach (var e in entries) ApplyToBook(e);
            }
        }

        /// <summary>
        /// Book an increment of a fill. Called only from reconciliation with the *newly* filled
        /// quantity, so replaying a reconciliation pass cannot double-count.
        /// </summary>
        public async Task BookFillAsync(FirmOrder order, decimal quantity, decimal price, decimal fee, CancellationToken ct = default)
        {
            if (quantity <= 0m) return;

            var instrument = instruments.Get(order.InstrumentId);
            var exposure = instrument?.Exposure
                ?? (order.Venue == VenueId.IG ? ExposureKind.Derivative : ExposureKind.Inventory);
            string quoteCurrency = instrument?.QuoteCurrency ?? "USD";
            string baseAsset = instrument?.BaseAsset ?? order.InstrumentId;
            decimal signed = order.Side == OrderSide.Buy ? quantity : -quantity;
            decimal notional = quantity * price;
            var ts = DateTime.UtcNow;

            var entries = new List<LedgerEntry>();

            // 1. Cash moves against the trade (spot buys spend cash; CFD deals post margin, which is
            //    tracked as the venue's own available-funds figure rather than as cash leaving).
            if (exposure == ExposureKind.Inventory)
            {
                entries.Add(new LedgerEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Ts = ts,
                    AccountId = order.AccountId,
                    Venue = order.Venue,
                    Environment = order.Environment,
                    InstrumentId = order.InstrumentId,
                    Kind = LedgerEntryKind.Cash,
                    Asset = quoteCurrency,
                    Amount = order.Side == OrderSide.Buy ? -notional : notional,
                    Price = price,
                    SourceType = "fill",
                    SourceId = order.Id,
                    StrategyId = order.StrategyId
                });
            }

            // 2. Quantity change — inventory for spot, exposure for derivatives.
            entries.Add(new LedgerEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Ts = ts,
                AccountId = order.AccountId,
                Venue = order.Venue,
                Environment = order.Environment,
                InstrumentId = order.InstrumentId,
                Kind = exposure == ExposureKind.Inventory ? LedgerEntryKind.Inventory : LedgerEntryKind.Exposure,
                Asset = baseAsset,
                Amount = signed * price,
                Quantity = signed,
                Price = price,
                SourceType = "fill",
                SourceId = order.Id,
                StrategyId = order.StrategyId
            });

            // 3. The explicit cost of trading, with its provenance recorded.
            if (fee != 0m)
            {
                entries.Add(new LedgerEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Ts = ts,
                    AccountId = order.AccountId,
                    Venue = order.Venue,
                    Environment = order.Environment,
                    InstrumentId = order.InstrumentId,
                    Kind = LedgerEntryKind.Cost,
                    Asset = string.IsNullOrWhiteSpace(order.FeeCurrency) ? quoteCurrency : order.FeeCurrency,
                    Amount = -Math.Abs(fee),
                    CostKind = order.Venue == VenueId.IG ? CostKind.Commission : CostKind.MakerTakerFee,
                    CostQuality = order.Venue == VenueId.Internal ? CostQuality.Estimated : CostQuality.Observed,
                    SourceType = "fill",
                    SourceId = order.Id,
                    StrategyId = order.StrategyId
                });
            }

            // 4. Realized P&L on whatever portion of the fill closed against the existing position.
            decimal realized = ComputeRealized(order.AccountId, order.InstrumentId, signed, price);
            if (realized != 0m)
            {
                entries.Add(new LedgerEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Ts = ts,
                    AccountId = order.AccountId,
                    Venue = order.Venue,
                    Environment = order.Environment,
                    InstrumentId = order.InstrumentId,
                    Kind = LedgerEntryKind.RealizedPnL,
                    Asset = quoteCurrency,
                    Amount = realized,
                    Price = price,
                    SourceType = "fill",
                    SourceId = order.Id,
                    StrategyId = order.StrategyId
                });
            }

            lock (bookLock)
            {
                foreach (var e in entries) ApplyToBook(e);
                UpdatePosition(order, signed, price, fee, exposure);
            }
            await repo.AppendAsync(entries, ct);
        }

        /// <summary>
        /// Adopt a holding the broker reports that the platform never traded.
        ///
        /// This is the normal case for an account that existed before OmniTrader did, or that its
        /// owner tops up by hand: it is not a discrepancy to be explained, it is an asset the firm
        /// owns and the book has simply never been told about. The broker's quantity is taken as
        /// truth, the entry is marked <see cref="EntryOrigin.ExternalManual"/> so it can never be
        /// mistaken for a platform decision, and the cost basis is recorded as the current mark
        /// because the real one is unknowable from here — which keeps unrealized P&amp;L at zero
        /// rather than inventing a profit the firm never made.
        ///
        /// Returns true when the book changed.
        /// </summary>
        public async Task<bool> AdoptExternalHoldingAsync(string accountId, VenueId venue, TradingEnvironment environment,
            string instrumentId, ExposureKind exposure, decimal venueQuantity, decimal? markPrice, CancellationToken ct = default)
        {
            decimal existing = GetPosition(accountId, instrumentId)?.Quantity ?? 0m;
            decimal delta = venueQuantity - existing;
            if (Math.Abs(delta) < 0.00000001m) return false;

            var instrument = instruments.Get(instrumentId);
            decimal price = markPrice ?? 0m;

            var entry = new LedgerEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Ts = DateTime.UtcNow,
                AccountId = accountId,
                Venue = venue,
                Environment = environment,
                InstrumentId = instrumentId,
                Kind = exposure == ExposureKind.Inventory ? LedgerEntryKind.Inventory : LedgerEntryKind.Exposure,
                Asset = instrument?.BaseAsset ?? instrumentId,
                Amount = delta * price,
                Quantity = delta,
                Price = price > 0m ? price : null,
                SourceType = "reconciliation",
                Origin = EntryOrigin.ExternalManual,
                ReconciliationState = ReconciliationState.Matched,
                Note = existing == 0m
                    ? $"Adopted {venueQuantity} {instrumentId} already held at {venue}. Cost basis unknown; marked at {price}."
                    : $"Aligned {instrumentId} to the {venue} quantity ({existing} → {venueQuantity}) from an external change."
            };

            lock (bookLock)
            {
                ApplyToBook(entry);
                var position = positions.GetOrAdd(PositionKey(accountId, instrumentId), _ => new FirmPosition
                {
                    InstrumentId = instrumentId,
                    Venue = venue,
                    Environment = environment,
                    AccountId = accountId,
                    Exposure = exposure,
                    OpenedUtc = DateTime.UtcNow
                });
                position.Quantity = venueQuantity;
                position.VenueQuantity = venueQuantity;
                // With no traded cost basis, the mark is the only defensible average price.
                if (existing == 0m && price > 0m) position.AveragePrice = price;
                if (price > 0m) position.MarkPrice = price;
            }

            await repo.AppendAsync(new[] { entry }, ct);
            return true;
        }

        /// <summary>Import broker-originated activity we did not initiate. It lands in the ledger with
        /// its origin permanently marked so it can never be mistaken for a platform decision.</summary>
        public async Task ImportExternalActivityAsync(string accountId, VenueId venue, TradingEnvironment environment,
            DateTime ts, string asset, decimal amount, string description, CancellationToken ct = default)
        {
            var entry = new LedgerEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Ts = ts,
                AccountId = accountId,
                Venue = venue,
                Environment = environment,
                Kind = LedgerEntryKind.Cash,
                Asset = asset,
                Amount = amount,
                SourceType = "activity",
                Origin = EntryOrigin.ExternalManual,
                Note = description
            };
            lock (bookLock) ApplyToBook(entry);
            await repo.AppendAsync(new[] { entry }, ct);
        }

        /// <summary>Post a correction. The original entry is never touched; the adjustment references
        /// it, so the audit trail shows both what we believed and what we corrected it to.</summary>
        public async Task PostAdjustmentAsync(LedgerEntry original, decimal correctedAmount, string reason,
            CancellationToken ct = default)
        {
            var adjustment = new LedgerEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Ts = DateTime.UtcNow,
                AccountId = original.AccountId,
                Venue = original.Venue,
                Environment = original.Environment,
                InstrumentId = original.InstrumentId,
                Kind = LedgerEntryKind.Adjustment,
                Asset = original.Asset,
                Amount = correctedAmount - original.Amount,
                SourceType = "reconciliation",
                SourceId = original.Id,
                Origin = EntryOrigin.Correction,
                CorrectsEntryId = original.Id,
                Note = reason
            };
            lock (bookLock) ApplyToBook(adjustment);
            await repo.AppendAsync(new[] { adjustment }, ct);
        }

        public Task<(decimal Realized, decimal Costs)> PnLSinceAsync(DateTime sinceUtc, string? strategyId = null, CancellationToken ct = default)
            => repo.SumSinceAsync(sinceUtc, strategyId, null, ct);

        public Task<Dictionary<string, decimal>> DailyPnLByStrategyAsync(CancellationToken ct = default)
            => repo.DailyPnLByStrategyAsync(DateTime.UtcNow.Date, ct);

        public async Task<int> CountMaterialBreaksAsync(CancellationToken ct = default)
            => (await reconRepo.ListOpenBreaksAsync(ct)).Count(b => b.Material);

        /// <summary>Apply the latest marks so unrealized P&amp;L and exposure are current.</summary>
        public void ApplyMarks(IReadOnlyDictionary<string, decimal> marksByInstrument)
        {
            foreach (var p in positions.Values)
                if (marksByInstrument.TryGetValue(p.InstrumentId, out var mark) && mark > 0m)
                    p.MarkPrice = mark;
        }

        // ── internals ─────────────────────────────────────────────────────────────

        private void ApplyToBook(LedgerEntry e)
        {
            switch (e.Kind)
            {
                case LedgerEntryKind.Cash:
                case LedgerEntryKind.Cost:
                case LedgerEntryKind.RealizedPnL:
                case LedgerEntryKind.Adjustment:
                    string key = CashKey(e.AccountId, e.Asset);
                    // Realized P&L is an accounting attribution of cash that already moved on the
                    // trade legs — counting it again would double the balance.
                    if (e.Kind != LedgerEntryKind.RealizedPnL)
                        cash[key] = cash.TryGetValue(key, out var c) ? c + e.Amount : e.Amount;
                    break;
            }
        }

        private decimal ComputeRealized(string accountId, string instrumentId, decimal signedQty, decimal price)
        {
            var position = GetPosition(accountId, instrumentId);
            if (position == null || position.Quantity == 0m) return 0m;
            if (Math.Sign(signedQty) == Math.Sign(position.Quantity)) return 0m;

            decimal closeQty = Math.Min(Math.Abs(signedQty), Math.Abs(position.Quantity));
            decimal direction = Math.Sign(position.Quantity);
            return closeQty * (price - position.AveragePrice) * direction;
        }

        private void UpdatePosition(FirmOrder order, decimal signedQty, decimal price, decimal fee, ExposureKind exposure)
        {
            string key = PositionKey(order.AccountId, order.InstrumentId);
            var position = positions.GetOrAdd(key, _ => new FirmPosition
            {
                InstrumentId = order.InstrumentId,
                Venue = order.Venue,
                Environment = order.Environment,
                AccountId = order.AccountId,
                Exposure = exposure,
                StrategyId = order.StrategyId,
                OpenedUtc = DateTime.UtcNow
            });

            decimal existing = position.Quantity;
            position.Fees += Math.Abs(fee);
            position.MarkPrice = price;

            if (existing == 0m)
            {
                position.Quantity = signedQty;
                position.AveragePrice = price;
                position.OpenedUtc = DateTime.UtcNow;
                position.StrategyId ??= order.StrategyId;
                return;
            }

            if (Math.Sign(signedQty) == Math.Sign(existing))
            {
                decimal totalCost = position.AveragePrice * Math.Abs(existing) + price * Math.Abs(signedQty);
                position.Quantity = existing + signedQty;
                position.AveragePrice = totalCost / Math.Abs(position.Quantity);
                return;
            }

            decimal closeQty = Math.Min(Math.Abs(signedQty), Math.Abs(existing));
            position.RealizedPnL += closeQty * (price - position.AveragePrice) * Math.Sign(existing);
            decimal next = existing + signedQty;
            if (next == 0m)
            {
                position.Quantity = 0m;
                position.OpenedUtc = null;
                return;
            }
            if (Math.Sign(next) != Math.Sign(existing))
            {
                // Flipped through flat — the remainder is a fresh position at this price.
                position.AveragePrice = price;
                position.OpenedUtc = DateTime.UtcNow;
            }
            position.Quantity = next;
        }
    }
}
