using Omnipotent.Services.OmniTrader.Journal;
using Omnipotent.Services.OmniTrader.Ledger;
using Omnipotent.Services.OmniTrader.OrderFlow;
using Omnipotent.Services.OmniTrader.Persistence;

namespace Omnipotent.Services.OmniTrader.Performance
{
    public sealed class PerformanceSlice
    {
        public required string Key { get; init; }
        public required string Label { get; init; }
        public decimal RealizedPnL { get; init; }
        public decimal Costs { get; init; }
        public int Trades { get; init; }
        public int Wins { get; init; }
        public decimal WinRate => Trades == 0 ? 0m : Wins * 100m / Trades;
        public decimal AverageWin { get; init; }
        public decimal AverageLoss { get; init; }
        public decimal Expectancy => Trades == 0 ? 0m
            : (WinRate / 100m * AverageWin) + ((1m - WinRate / 100m) * AverageLoss);
        public decimal PayoffRatio => AverageLoss == 0m ? 0m : Math.Abs(AverageWin / AverageLoss);
        public decimal NetPnL => RealizedPnL + Costs;
    }

    public sealed class ExecutionQuality
    {
        public int Submitted { get; init; }
        public int Filled { get; init; }
        public int PartiallyFilled { get; init; }
        public int Rejected { get; init; }
        public int Cancelled { get; init; }
        public int Unknown { get; init; }
        public decimal FillRatePercent => Submitted == 0 ? 0m : Filled * 100m / Submitted;
        public decimal RejectionRatePercent => Submitted == 0 ? 0m : Rejected * 100m / Submitted;
        public decimal? MedianSlippageBps { get; init; }
        public decimal? WorstSlippageBps { get; init; }
        public double? MedianLatencyMs { get; init; }
        public Dictionary<string, int> RejectionReasons { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class BehaviourAnalysis
    {
        public int Interventions { get; init; }
        public double? MedianApprovalDelaySeconds { get; init; }
        public decimal PnLWithIntervention { get; init; }
        public decimal PnLWithoutIntervention { get; init; }
        public string Verdict { get; init; } = "";
    }

    /// <summary>One day of the window, with the running total so a chart can show level and change
    /// without the client re-deriving either.</summary>
    public sealed class DailyPerformancePoint
    {
        public required DateTime Date { get; init; }
        public decimal NetPnL { get; init; }
        public decimal Cumulative { get; init; }
        public int Trades { get; init; }
    }

    /// <summary>A histogram bin. Distribution beats an average: two strategies with identical
    /// expectancy can have completely different shapes, and only the shape says which one survives.</summary>
    public sealed class DistributionBucket
    {
        public required string Label { get; init; }
        public decimal From { get; init; }
        public decimal To { get; init; }
        public int Count { get; init; }
    }

    public sealed class PerformanceReport
    {
        public required DateTime FromUtc { get; init; }
        public required DateTime ToUtc { get; init; }
        public required PerformanceSlice Firm { get; init; }
        public List<PerformanceSlice> ByVenue { get; init; } = new();
        public List<PerformanceSlice> ByStrategy { get; init; } = new();
        public List<PerformanceSlice> ByInstrument { get; init; } = new();
        public List<PerformanceSlice> ByRegimeTag { get; init; } = new();
        public required ExecutionQuality Execution { get; init; }
        public required BehaviourAnalysis Behaviour { get; init; }
        public List<(DateTime Ts, decimal Equity)> EquityCurve { get; init; } = new();
        public decimal MaxDrawdownPercent { get; init; }
        /// <summary>Costs the platform could only estimate, so a report never implies more precision
        /// than the data supports.</summary>
        public decimal EstimatedCosts { get; init; }
        public decimal ObservedCosts { get; init; }
        public decimal UnavailableCostInstruments { get; init; }

        // ── comparison ────────────────────────────────────────────────────────────
        // A number without a baseline is not a measurement. Every headline figure is reported
        // alongside the same figure over the immediately preceding window of equal length, so the
        // UI can state "+8.2% vs previous 30 days" rather than colouring an unexplained arrow.

        public DateTime PreviousFromUtc { get; init; }
        public DateTime PreviousToUtc { get; init; }
        public PerformanceSlice? Previous { get; init; }
        public ExecutionQuality? PreviousExecution { get; init; }
        /// <summary>False when the previous window has no closed trades at all — the UI must say
        /// "no baseline" rather than present a comparison against zero as a real result.</summary>
        public bool HasBaseline { get; init; }

        public List<DailyPerformancePoint> Daily { get; init; } = new();
        public List<DistributionBucket> PnLDistribution { get; init; } = new();
        public List<DistributionBucket> SlippageDistribution { get; init; } = new();
    }

    /// <summary>
    /// Post-trade measurement across the whole operation: firm, venue, strategy, instrument, execution
    /// quality and operator behaviour, all from the same ledger and journal the accounting uses — so a
    /// performance number and a balance can never disagree.
    /// </summary>
    public sealed class PerformanceService
    {
        private readonly LedgerRepository ledgerRepo;
        private readonly FirmOrderRepository orderRepo;
        private readonly JournalRepository journalRepo;
        private readonly AccountRepository accountRepo;

        public PerformanceService(LedgerRepository ledgerRepo, FirmOrderRepository orderRepo,
            JournalRepository journalRepo, AccountRepository accountRepo)
        {
            this.ledgerRepo = ledgerRepo;
            this.orderRepo = orderRepo;
            this.journalRepo = journalRepo;
            this.accountRepo = accountRepo;
        }

        public async Task<PerformanceReport> BuildAsync(DateTime? fromUtc = null, CancellationToken ct = default)
        {
            var from = fromUtc ?? DateTime.UtcNow.AddDays(-30);
            var to = DateTime.UtcNow;

            // The baseline is the immediately preceding window of the same length. Everything is read
            // once over both windows and split, so the comparison can never come from a different
            // snapshot of the ledger than the headline it is compared against.
            var span = to - from;
            var previousFrom = from - span;

            var allEntries = await ledgerRepo.ListAsync(previousFrom, limit: 40_000, ct: ct);
            var allOrders = (await orderRepo.ListRecentAsync(4000, ct)).Where(o => o.CreatedUtc >= previousFrom).ToList();
            var allJournal = (await journalRepo.ListAsync(limit: 4000, ct: ct)).Where(j => j.Ts >= previousFrom).ToList();

            var entries = allEntries.Where(e => e.Ts >= from).ToList();
            var orders = allOrders.Where(o => o.CreatedUtc >= from).ToList();
            var journal = allJournal.Where(j => j.Ts >= from).ToList();

            var previousEntries = allEntries.Where(e => e.Ts < from).ToList();
            var previousOrders = allOrders.Where(o => o.CreatedUtc < from).ToList();
            var previousClosed = allJournal.Where(j => j.Ts < from && j.RealizedPnL.HasValue).ToList();

            var closed = journal.Where(j => j.RealizedPnL.HasValue).ToList();

            var report = new PerformanceReport
            {
                FromUtc = from,
                ToUtc = to,
                Firm = BuildSlice("firm", "Firm", closed, entries),
                Execution = BuildExecutionQuality(orders),
                Behaviour = BuildBehaviour(journal),
                ObservedCosts = entries.Where(e => e.Kind == LedgerEntryKind.Cost && e.CostQuality == CostQuality.Observed).Sum(e => e.Amount),
                EstimatedCosts = entries.Where(e => e.Kind == LedgerEntryKind.Cost && e.CostQuality == CostQuality.Estimated).Sum(e => e.Amount),
                UnavailableCostInstruments = entries.Count(e => e.Kind == LedgerEntryKind.Cost && e.CostQuality == CostQuality.Unavailable),
                PreviousFromUtc = previousFrom,
                PreviousToUtc = from,
                Previous = BuildSlice("previous", "Previous window", previousClosed, previousEntries),
                PreviousExecution = BuildExecutionQuality(previousOrders),
                HasBaseline = previousClosed.Count > 0 || previousOrders.Count > 0
            };

            foreach (var group in closed.GroupBy(j => j.Venue.ToString()))
                report.ByVenue.Add(BuildSlice(group.Key, group.Key, group.ToList(),
                    entries.Where(e => e.Venue.ToString() == group.Key).ToList()));

            foreach (var group in closed.GroupBy(j => j.StrategyId ?? "manual"))
                report.ByStrategy.Add(BuildSlice(group.Key, group.Key, group.ToList(),
                    entries.Where(e => (e.StrategyId ?? "manual") == group.Key).ToList()));

            foreach (var group in closed.GroupBy(j => j.InstrumentId))
                report.ByInstrument.Add(BuildSlice(group.Key, group.Key, group.ToList(),
                    entries.Where(e => e.InstrumentId == group.Key).ToList()));

            // Context slices come from the tags an operator (or the journal writer) attached, so the
            // platform does not have to guess what a "setup" is.
            foreach (var group in closed.SelectMany(j => j.Tags.DefaultIfEmpty("untagged"), (j, tag) => (tag, j))
                                        .GroupBy(x => x.tag))
                report.ByRegimeTag.Add(BuildSlice(group.Key, group.Key, group.Select(x => x.j).ToList(), new List<LedgerEntry>()));

            var series = await accountRepo.SnapshotSeriesAsync(from, ct);
            var curve = series.GroupBy(s => s.Ts).Select(g => (g.Key, g.Sum(x => x.Value))).OrderBy(p => p.Key).ToList();
            report.EquityCurve.AddRange(curve);

            return new PerformanceReport
            {
                FromUtc = report.FromUtc,
                ToUtc = report.ToUtc,
                Firm = report.Firm,
                ByVenue = report.ByVenue.OrderByDescending(s => s.NetPnL).ToList(),
                ByStrategy = report.ByStrategy.OrderByDescending(s => s.NetPnL).ToList(),
                ByInstrument = report.ByInstrument.OrderByDescending(s => s.NetPnL).ToList(),
                ByRegimeTag = report.ByRegimeTag.OrderByDescending(s => s.NetPnL).ToList(),
                Execution = report.Execution,
                Behaviour = report.Behaviour,
                EquityCurve = report.EquityCurve,
                MaxDrawdownPercent = MaxDrawdown(curve),
                ObservedCosts = report.ObservedCosts,
                EstimatedCosts = report.EstimatedCosts,
                UnavailableCostInstruments = report.UnavailableCostInstruments,
                PreviousFromUtc = report.PreviousFromUtc,
                PreviousToUtc = report.PreviousToUtc,
                Previous = report.Previous,
                PreviousExecution = report.PreviousExecution,
                HasBaseline = report.HasBaseline,
                Daily = BuildDaily(closed, from, to),
                PnLDistribution = Distribute(closed.Select(j => j.RealizedPnL ?? 0m), 9),
                SlippageDistribution = Distribute(
                    orders.Where(o => o.SlippageBps.HasValue).Select(o => o.SlippageBps!.Value), 9, "bps")
            };
        }

        /// <summary>
        /// One point per calendar day of the window, including days with no trades — a gap in a P&amp;L
        /// series must read as "nothing happened", not as a missing measurement the chart quietly
        /// interpolates across.
        /// </summary>
        public static List<DailyPerformancePoint> BuildDaily(IEnumerable<JournalRecord> closed, DateTime from, DateTime to)
        {
            var byDay = closed
                .GroupBy(j => j.Ts.Date)
                .ToDictionary(g => g.Key, g => (Net: g.Sum(j => j.RealizedPnL ?? 0m), Trades: g.Count()));

            var points = new List<DailyPerformancePoint>();
            decimal running = 0m;
            // A very long window would otherwise produce thousands of points for a 1000px chart.
            var start = from.Date;
            var end = to.Date;
            if ((end - start).TotalDays > 400) start = end.AddDays(-400);

            for (var day = start; day <= end; day = day.AddDays(1))
            {
                byDay.TryGetValue(day, out var value);
                running += value.Net;
                points.Add(new DailyPerformancePoint
                {
                    Date = day,
                    NetPnL = value.Net,
                    Cumulative = running,
                    Trades = value.Trades
                });
            }
            return points;
        }

        /// <summary>
        /// Equal-width histogram over a set of values. Returns an empty list rather than a single
        /// degenerate bin when there is nothing to describe, so the UI shows an empty state instead of
        /// a chart implying a distribution it does not have.
        /// </summary>
        public static List<DistributionBucket> Distribute(IEnumerable<decimal> values, int buckets = 9, string unit = "")
        {
            var list = values.ToList();
            if (list.Count < 2 || buckets < 1) return new List<DistributionBucket>();

            decimal min = list.Min(), max = list.Max();
            if (max == min) return new List<DistributionBucket>();

            decimal width = (max - min) / buckets;
            var counts = new int[buckets];
            foreach (var value in list)
            {
                int index = (int)Math.Floor((double)((value - min) / width));
                counts[Math.Clamp(index, 0, buckets - 1)]++;
            }

            string suffix = string.IsNullOrEmpty(unit) ? "" : $" {unit}";
            return Enumerable.Range(0, buckets).Select(i =>
            {
                decimal lower = min + width * i;
                decimal upper = i == buckets - 1 ? max : lower + width;
                return new DistributionBucket
                {
                    Label = $"{Round(lower)}–{Round(upper)}{suffix}",
                    From = lower,
                    To = upper,
                    Count = counts[i]
                };
            }).ToList();

            // Bucket edges are labels, not measurements — showing six decimals of a bin edge is noise.
            static decimal Round(decimal value) => Math.Round(value, Math.Abs(value) >= 10m ? 0 : 2);
        }

        private static PerformanceSlice BuildSlice(string key, string label, List<JournalRecord> records, List<LedgerEntry> entries)
        {
            var pnls = records.Select(r => r.RealizedPnL ?? 0m).ToList();
            var wins = pnls.Where(p => p > 0m).ToList();
            var losses = pnls.Where(p => p <= 0m).ToList();

            return new PerformanceSlice
            {
                Key = key,
                Label = label,
                RealizedPnL = pnls.Sum(),
                Costs = entries.Where(e => e.Kind == LedgerEntryKind.Cost).Sum(e => e.Amount),
                Trades = pnls.Count,
                Wins = wins.Count,
                AverageWin = wins.Count == 0 ? 0m : wins.Average(),
                AverageLoss = losses.Count == 0 ? 0m : losses.Average()
            };
        }

        private static ExecutionQuality BuildExecutionQuality(List<FirmOrder> orders)
        {
            var submitted = orders.Where(o => o.SubmittedUtc.HasValue).ToList();
            var slippages = submitted.Select(o => o.SlippageBps).Where(s => s.HasValue).Select(s => s!.Value).OrderBy(s => s).ToList();
            var latencies = submitted.Select(o => o.SubmissionLatency).Where(l => l.HasValue)
                                     .Select(l => l!.Value.TotalMilliseconds).OrderBy(l => l).ToList();

            var reasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var order in orders.Where(o => o.State == FirmOrderState.Rejected))
            {
                string reason = string.IsNullOrWhiteSpace(order.Error) ? "unspecified" : order.Error!;
                reasons[reason] = reasons.TryGetValue(reason, out var c) ? c + 1 : 1;
            }

            return new ExecutionQuality
            {
                Submitted = submitted.Count,
                Filled = orders.Count(o => o.State == FirmOrderState.Filled),
                PartiallyFilled = orders.Count(o => o.State == FirmOrderState.PartiallyFilled),
                Rejected = orders.Count(o => o.State == FirmOrderState.Rejected),
                Cancelled = orders.Count(o => o.State == FirmOrderState.Cancelled),
                Unknown = orders.Count(o => o.State == FirmOrderState.Unknown),
                MedianSlippageBps = slippages.Count == 0 ? null : slippages[slippages.Count / 2],
                WorstSlippageBps = slippages.Count == 0 ? null : slippages[^1],
                MedianLatencyMs = latencies.Count == 0 ? null : latencies[latencies.Count / 2],
                RejectionReasons = reasons
            };
        }

        /// <summary>Did operator intervention help? Compare the P&amp;L of trades that were interfered
        /// with against those that were left alone.</summary>
        private static BehaviourAnalysis BuildBehaviour(List<JournalRecord> journal)
        {
            var intervened = journal.Where(j => j.Interventions.Count > 0 && j.RealizedPnL.HasValue).ToList();
            var untouched = journal.Where(j => j.Interventions.Count == 0 && j.RealizedPnL.HasValue).ToList();

            var delays = journal.Where(j => j.ApprovalDelay.HasValue)
                                .Select(j => j.ApprovalDelay!.Value.TotalSeconds).OrderBy(d => d).ToList();

            decimal withPnL = intervened.Sum(j => j.RealizedPnL ?? 0m);
            decimal withoutPnL = untouched.Sum(j => j.RealizedPnL ?? 0m);

            string verdict;
            if (intervened.Count == 0) verdict = "No manual interventions in this window.";
            else if (untouched.Count == 0) verdict = "Every trade was intervened on; no comparison group.";
            else
            {
                decimal withAvg = withPnL / intervened.Count;
                decimal withoutAvg = withoutPnL / untouched.Count;
                verdict = withAvg > withoutAvg
                    ? $"Intervened trades averaged {withAvg:F2} vs {withoutAvg:F2} left alone — intervention helped in this window."
                    : $"Intervened trades averaged {withAvg:F2} vs {withoutAvg:F2} left alone — intervention did not help in this window.";
            }

            return new BehaviourAnalysis
            {
                Interventions = intervened.Sum(j => j.Interventions.Count),
                MedianApprovalDelaySeconds = delays.Count == 0 ? null : delays[delays.Count / 2],
                PnLWithIntervention = withPnL,
                PnLWithoutIntervention = withoutPnL,
                Verdict = verdict
            };
        }

        private static decimal MaxDrawdown(List<(DateTime Ts, decimal Equity)> curve)
        {
            if (curve.Count == 0) return 0m;
            decimal peak = curve[0].Equity, worst = 0m;
            foreach (var (_, equity) in curve)
            {
                if (equity > peak) peak = equity;
                if (peak > 0m)
                {
                    decimal drawdown = (peak - equity) / peak * 100m;
                    if (drawdown > worst) worst = drawdown;
                }
            }
            return worst;
        }
    }
}
