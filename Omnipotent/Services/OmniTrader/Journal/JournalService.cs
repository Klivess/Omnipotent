using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Instruments;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.OrderFlow;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Risk;
using Omnipotent.Services.OmniTrader.Venues;

namespace Omnipotent.Services.OmniTrader.Journal
{
    public enum ReviewState { Unreviewed = 0, Reviewed = 1, Flagged = 2 }

    /// <summary>
    /// The complete decision record for one trade: what was seen, what was proposed, what risk said,
    /// who approved it, what actually happened, and what it cost. Written automatically — the point is
    /// that the record exists without anyone remembering to create it.
    /// </summary>
    public sealed class JournalRecord
    {
        public required string Id { get; init; }
        public required DateTime Ts { get; init; }
        public required string InstrumentId { get; init; }
        public required VenueId Venue { get; init; }
        public required TradingEnvironment Environment { get; init; }
        public string? StrategyId { get; init; }
        public string? StrategyVersion { get; init; }
        public string? DeploymentId { get; init; }
        public ExecutionAuthority Authority { get; init; }

        // ── the decision ──────────────────────────────────────────────────────────
        public string? ProposalId { get; init; }
        public DateTime? SignalTimeUtc { get; init; }
        public decimal DecisionPrice { get; init; }
        public string? Rationale { get; init; }
        /// <summary>The analytical snapshot at signal time, so a review can see what the strategy saw.</summary>
        public Dictionary<string, string> DataSnapshot { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        // ── risk and approval ─────────────────────────────────────────────────────
        public string? RiskDecisionId { get; init; }
        public RiskVerdict RiskVerdict { get; init; }
        public string? RiskSummary { get; init; }
        public string? ApprovedBy { get; init; }
        public DateTime? ApprovedUtc { get; init; }
        public TimeSpan? ApprovalDelay { get; init; }

        // ── intended vs actual ────────────────────────────────────────────────────
        public OrderSide Side { get; init; }
        public decimal IntendedQuantity { get; init; }
        public decimal FilledQuantity { get; init; }
        public decimal? IntendedPrice { get; init; }
        public decimal? ActualPrice { get; init; }
        public decimal? StopLossPrice { get; init; }
        public decimal? TakeProfitPrice { get; init; }
        public decimal? SlippageBps { get; init; }
        public decimal Fees { get; init; }
        public string? OrderId { get; init; }
        public FirmOrderState FinalState { get; init; }

        // ── outcome ───────────────────────────────────────────────────────────────
        public decimal? RealizedPnL { get; set; }
        public DateTime? ExitTimeUtc { get; set; }
        public decimal? ExitPrice { get; set; }
        public string? ExitReason { get; set; }
        /// <summary>Best unrealized gain reached while the position was open.</summary>
        public decimal? MaxFavourableExcursion { get; set; }
        /// <summary>Worst unrealized loss reached while the position was open.</summary>
        public decimal? MaxAdverseExcursion { get; set; }
        public TimeSpan? HoldingPeriod { get; set; }

        // ── review ────────────────────────────────────────────────────────────────
        public ReviewState ReviewState { get; set; } = ReviewState.Unreviewed;
        public List<string> Tags { get; init; } = new();
        public string? Notes { get; set; }
        /// <summary>Manual interventions taken on this trade, each with the state before and after.</summary>
        public List<InterventionRecord> Interventions { get; init; } = new();
    }

    public sealed class InterventionRecord
    {
        public required DateTime AtUtc { get; init; }
        public required string Actor { get; init; }
        public required string Action { get; init; }
        public string? StateBefore { get; init; }
        public string? StateAfter { get; init; }
        public string? Reason { get; init; }
    }

    /// <summary>
    /// Writes and maintains the trade journal. Records are created when an order reaches a terminal
    /// state, then enriched with excursion and exit data as the position plays out.
    /// </summary>
    public sealed class JournalService
    {
        private readonly JournalRepository repo;
        private readonly RiskDecisionRepository riskRepo;
        private readonly InstrumentMaster instruments;
        private readonly MarketDataRouter marketData;

        public JournalService(JournalRepository repo, RiskDecisionRepository riskRepo,
            InstrumentMaster instruments, MarketDataRouter marketData)
        {
            this.repo = repo;
            this.riskRepo = riskRepo;
            this.instruments = instruments;
            this.marketData = marketData;
        }

        /// <summary>Create the journal record for a completed order. Idempotent on the order id.</summary>
        public async Task<JournalRecord> RecordOrderAsync(FirmOrder order, CancellationToken ct = default)
        {
            var existing = (await repo.ListAsync(limit: 500, ct: ct)).FirstOrDefault(r => r.OrderId == order.Id);
            if (existing != null) return existing;

            var decision = await riskRepo.GetAsync(order.RiskDecisionId, ct);
            var proposal = await riskRepo.GetProposalAsync(order.ProposalId, ct);

            var record = new JournalRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Ts = order.CreatedUtc,
                InstrumentId = order.InstrumentId,
                Venue = order.Venue,
                Environment = order.Environment,
                StrategyId = order.StrategyId,
                StrategyVersion = order.StrategyVersion,
                DeploymentId = order.DeploymentId,
                Authority = proposal?.Authority ?? ExecutionAuthority.Observe,
                ProposalId = order.ProposalId,
                SignalTimeUtc = proposal?.CreatedUtc,
                DecisionPrice = order.DecisionPrice,
                Rationale = proposal?.Rationale,
                RiskDecisionId = order.RiskDecisionId,
                RiskVerdict = decision?.Verdict ?? RiskVerdict.Approved,
                RiskSummary = decision?.Summary,
                ApprovedBy = order.ApprovedBy,
                ApprovedUtc = order.ApprovedUtc,
                ApprovalDelay = order.ApprovedUtc.HasValue ? order.ApprovedUtc.Value - order.CreatedUtc : null,
                Side = order.Side,
                IntendedQuantity = order.Quantity,
                FilledQuantity = order.FilledQuantity,
                IntendedPrice = order.LimitPrice ?? order.DecisionPrice,
                ActualPrice = order.AverageFillPrice,
                StopLossPrice = order.StopLossPrice,
                TakeProfitPrice = order.TakeProfitPrice,
                SlippageBps = order.SlippageBps,
                Fees = order.Fees,
                OrderId = order.Id,
                FinalState = order.State
            };

            if (proposal != null)
            {
                record.DataSnapshot["decision_price"] = proposal.DecisionPrice.ToString("G29");
                record.DataSnapshot["data_timestamp"] = proposal.DataTimestampUtc.ToString("o");
                record.DataSnapshot["authority"] = proposal.Authority.ToString();
            }
            if (decision != null)
                foreach (var failure in decision.Failures)
                    record.DataSnapshot[$"risk:{failure.Rule}"] = $"{failure.Severity}: {failure.Detail}";

            await repo.UpsertAsync(record, ct);
            return record;
        }

        /// <summary>Close out a record once the position is flat: exit, holding period and the
        /// excursions that show how much heat the trade took before it worked (or did not).</summary>
        public async Task<JournalRecord?> CloseAsync(string recordId, decimal exitPrice, decimal realizedPnL,
            string exitReason, CancellationToken ct = default)
        {
            var record = await repo.GetAsync(recordId, ct);
            if (record == null) return null;

            record.ExitTimeUtc = DateTime.UtcNow;
            record.ExitPrice = exitPrice;
            record.RealizedPnL = realizedPnL;
            record.ExitReason = exitReason;
            record.HoldingPeriod = record.ExitTimeUtc - record.Ts;

            var (favourable, adverse) = await ComputeExcursionsAsync(record, ct);
            record.MaxFavourableExcursion = favourable;
            record.MaxAdverseExcursion = adverse;

            await repo.UpsertAsync(record, ct);
            return record;
        }

        /// <summary>Walk the bars the position was open for and measure how far it went for and
        /// against. Computed from candles rather than sampled live, so a restart cannot lose it.</summary>
        private async Task<(decimal? Favourable, decimal? Adverse)> ComputeExcursionsAsync(JournalRecord record, CancellationToken ct)
        {
            if (record.ActualPrice is not > 0m || record.ExitTimeUtc == null) return (null, null);
            try
            {
                string engineSymbol = instruments.EngineSymbolFor(record.InstrumentId);
                var candles = await marketData.GetHistoricalCandlesAsync(engineSymbol, TimeInterval.OneHour, 500);
                var window = candles.Where(c => c.Timestamp >= record.Ts && c.Timestamp <= record.ExitTimeUtc.Value).ToList();
                if (window.Count == 0) return (null, null);

                decimal entry = record.ActualPrice.Value;
                decimal direction = record.Side == OrderSide.Buy ? 1m : -1m;
                decimal best = window.Max(c => (direction > 0 ? c.High : entry - (c.Low - entry)) - entry) * direction;
                decimal worst = window.Min(c => (direction > 0 ? c.Low : entry + (entry - c.High)) - entry) * direction;

                decimal qty = record.FilledQuantity;
                return (Math.Max(0m, best) * qty, Math.Min(0m, worst) * qty);
            }
            catch { return (null, null); }
        }

        public async Task<JournalRecord?> AnnotateAsync(string id, string? notes, IEnumerable<string>? tags,
            ReviewState? reviewState, CancellationToken ct = default)
        {
            var record = await repo.GetAsync(id, ct);
            if (record == null) return null;
            if (notes != null) record.Notes = notes;
            if (tags != null) { record.Tags.Clear(); record.Tags.AddRange(tags.Where(t => !string.IsNullOrWhiteSpace(t))); }
            if (reviewState.HasValue) record.ReviewState = reviewState.Value;
            await repo.UpsertAsync(record, ct);
            return record;
        }

        /// <summary>Record a manual intervention against a trade, with the account state either side
        /// of it — this is what makes "did overriding help?" an answerable question.</summary>
        public async Task<JournalRecord?> RecordInterventionAsync(string id, string actor, string action,
            string? stateBefore, string? stateAfter, string? reason, CancellationToken ct = default)
        {
            var record = await repo.GetAsync(id, ct);
            if (record == null) return null;
            record.Interventions.Add(new InterventionRecord
            {
                AtUtc = DateTime.UtcNow,
                Actor = actor,
                Action = action,
                StateBefore = stateBefore,
                StateAfter = stateAfter,
                Reason = reason
            });
            await repo.UpsertAsync(record, ct);
            return record;
        }

        public Task<List<JournalRecord>> ListAsync(string? reviewState = null, string? strategyId = null,
            int limit = 200, CancellationToken ct = default)
            => repo.ListAsync(reviewState, strategyId, limit, ct);

        public Task<JournalRecord?> GetAsync(string id, CancellationToken ct = default) => repo.GetAsync(id, ct);
    }
}
