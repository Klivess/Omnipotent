using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Strategy;
using Omnipotent.Services.OmniTrader.Strategy.Params;

namespace Omnipotent.Services.OmniTrader.Backtesting.Validation
{
    /// <summary>
    /// Strategy-agnostic post-backtest validation. Re-runs the (already-fetched, in-memory) universe data
    /// to produce: cost sensitivity, a walk-forward out-of-sample run that sweeps a grid auto-built from
    /// the strategy's [Param] ranges, the deflated Sharpe penalising that sweep, and turnover. Knows
    /// nothing about any specific strategy — it only needs a way to make a fresh strategy from a param set.
    /// </summary>
    public static class GenericValidation
    {
        public static async Task<ValidationReport> RunAsync(
            PortfolioInput input,
            BacktestConfig config,
            IReadOnlyDictionary<string, object?> baseParams,
            IReadOnlyList<ParamDescriptor> schema,
            Func<IReadOnlyDictionary<string, object?>, TradingStrategy> makeStrategy,
            BacktestResult primary,
            ValidationSettings settings,
            CancellationToken ct = default)
        {
            var report = new ValidationReport
            {
                NetPnLPercent = primary.TotalPnLPercent,
                GrossPnLPercent = primary.InitialEquity == 0m
                    ? 0m
                    : (primary.FinalEquity + primary.TotalFeesPaid - primary.InitialEquity) / primary.InitialEquity * 100m,
            };

            // Cost sensitivity — base params, scaled fee + slippage.
            report.CostSensitivity = await CostSensitivity.RunAsync(
                cfg => new BacktestSession(makeStrategy(baseParams), input, cfg),
                config, settings.CostMultipliers, ct);

            // Walk-forward over a sweep grid built from the strategy's [Param] ranges.
            var grid = ParamGrid.Build(schema, baseParams, settings.MaxGridCombos);
            var wf = await WalkForward.RunAsync(
                input, grid,
                (p, slice) => new BacktestSession(makeStrategy(p), slice, config),
                config.InitialQuoteBalance, settings.InSampleBars, settings.OosBars, settings.WarmupBars, ct);

            report.WalkForwardOosPnLPercent = wf.OosPnLPercent;
            report.WalkForwardOosSharpe = wf.OosSharpe;
            report.WalkForwardOosMaxDrawdownPercent = wf.OosMaxDrawdownPercent;
            report.WalkForwardFolds = wf.Folds;
            report.TrialsTested = wf.TrialsPerFold;
            report.DeflatedSharpe = wf.DeflatedSharpe;
            report.ExpectedMaxSharpe = wf.ExpectedMaxSharpe;

            var (weekly, annual) = Turnover(primary);
            report.WeeklyTurnover = weekly;
            report.AnnualTurnover = annual;

            if (grid.Count <= 1)
                report.Notes.Add("No sweepable [Param] ranges — walk-forward ran on fixed params (trials = 1).");
            if (wf.Folds == 0)
                report.Notes.Add("Not enough history for walk-forward folds — increase candles or reduce the in-sample/OOS window.");

            return report;
        }

        // Turnover as a multiple of equity traded per year (and per week). Generic: from the trade ledger.
        private static (decimal Weekly, decimal Annual) Turnover(BacktestResult r)
        {
            if (r.Trades.Count == 0 || r.Duration.TotalDays < 1) return (0m, 0m);
            decimal traded = 0m;
            foreach (var t in r.Trades)
                traded += Math.Abs(t.EntryPrice * t.Qty) + Math.Abs(t.ExitPrice * t.Qty);

            decimal avgEquity = (r.InitialEquity + r.FinalEquity) / 2m;
            double years = r.Duration.TotalDays / 365.0;
            if (avgEquity <= 0m || years <= 0) return (0m, 0m);

            decimal annual = traded / avgEquity / (decimal)years;
            return (annual / 52m, annual);
        }
    }
}
