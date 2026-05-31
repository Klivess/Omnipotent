using Omnipotent.Services.OmniTrader.Backtesting.Validation;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Strategy.Momentum;
using Omnipotent.Services.OmniTrader.Strategy.Strategies;

namespace Omnipotent.Services.OmniTrader.Backtesting
{
    /// <summary>
    /// Orchestrates a cross-sectional momentum (portfolio) backtest end-to-end: ensure point-in-time
    /// universe data is cached, assemble it into a <see cref="PortfolioInput"/>, run the primary
    /// portfolio backtest, then (optionally) the full Section 11 validation suite. Reuses the existing
    /// <see cref="BacktestSession"/> engine — no separate backtest engine.
    /// </summary>
    public sealed class MomentumBacktestRunner
    {
        private readonly UniverseRepository universeRepo;
        private readonly CoinGeckoUniverseProvider coingecko;
        private readonly Action<string> log;

        public MomentumBacktestRunner(UniverseRepository universeRepo, CoinGeckoUniverseProvider coingecko, Action<string> log)
        {
            this.universeRepo = universeRepo;
            this.coingecko = coingecko;
            this.log = log;
        }

        public async Task<BacktestResult> RunAsync(
            BacktestConfig config,
            Func<double, int, int, Task>? onProgress = null,
            Func<Task<bool>>? cancellationCheck = null,
            CancellationToken ct = default)
        {
            var s = config.Momentum ?? throw new InvalidOperationException("MomentumBacktestRunner requires config.Momentum.");

            // 1. Ensure the point-in-time universe data covers the window (cached after the first fetch).
            await coingecko.EnsureUniverseDataAsync(universeRepo, s.FromUtc.Date, s.ToUtc.Date, s.UniverseTopN, log, ct);

            // 2. Load it and assemble the portfolio inputs.
            var window = await universeRepo.LoadWindowAsync(s.FromUtc, s.ToUtc, ct);
            if (window.Count == 0)
                throw new InvalidOperationException($"No universe data for {s.FromUtc:yyyy-MM-dd}..{s.ToUtc:yyyy-MM-dd}. Check the CoinGecko API key and window.");

            var coins = await universeRepo.ListCoinsAsync(ct);
            var tickers = coins.ToDictionary(c => c.CoinId, c => c.Symbol, StringComparer.OrdinalIgnoreCase);
            var shortable = coins.Where(c => c.Shortable).Select(c => c.CoinId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var input = BuildPortfolioInput(window, s.RegimeCoinId);
            log($"Momentum backtest: {input.Candles.Count} coins over {s.FromUtc:yyyy-MM-dd}..{s.ToUtc:yyyy-MM-dd}.");

            var mcfg = MomentumConfig.FromSettings(s);
            // Charge short funding via the per-bar borrow/rollover rate (Section 10 mapping).
            var runCfg = WithMargin(config, CostModel.ToMarginSettings(mcfg, config.Margin));

            CrossSectionalMomentumStrategy MkStrat(MomentumConfig p) => new()
            {
                Config = p,
                RegimeSymbol = s.RegimeCoinId,
                Tickers = tickers,
                Shortable = shortable,
            };

            // 3. Primary run.
            var result = await new BacktestSession(MkStrat(mcfg), input, runCfg, onProgress, cancellationCheck, log)
                .RunPortfolioAsync(ct);

            // Gross vs net (Section 10).
            var grossNet = CostModel.Split(result.InitialEquity, result.FinalEquity, result.TotalFeesPaid);

            // 4. Validation suite (Section 11).
            if (s.RunValidation)
            {
                log("Momentum backtest: running validation suite (cost sensitivity, walk-forward, survivorship, turnover)…");
                var report = new MomentumValidationReport
                {
                    GrossPnLPercent = grossNet.GrossPnLPercent(result.InitialEquity),
                    NetPnLPercent = grossNet.NetPnLPercent(result.InitialEquity),
                };

                // 11.3 Cost sensitivity.
                report.CostSensitivity = await Validation.CostSensitivity.RunAsync(
                    c => new BacktestSession(MkStrat(mcfg), input, c), runCfg, new[] { 1.0, 2.0, 3.0 }, ct);

                // 11.4 Survivorship audit.
                var audit = SurvivorshipAudit.Audit(window);
                report.UniverseCoins = audit.TotalCoins;
                report.DelistedCoins = audit.DelistedCoins;
                report.PointInTimeUniverse = audit.PointInTime;
                report.DelistedExamples = audit.DelistedExamples;
                if (audit.DelistedCoins == 0)
                    report.Notes.Add("No delisted coins in window — dataset may be survivorship-biased.");

                // 11.5 Turnover & capacity.
                var avgVols = window.Values
                    .Where(p => p.Count > 0)
                    .Select(p => p.Average(x => x.VolumeUsd))
                    .ToList();
                var tc = TurnoverCapacity.Compute(result, mcfg, avgVols);
                report.WeeklyTurnover = tc.WeeklyTurnover;
                report.AnnualTurnover = tc.AnnualTurnover;
                report.EstimatedCapacityUsd = tc.EstimatedCapacityUsd;

                // 11.1 Walk-forward + 11.2 Deflated Sharpe.
                var grid = WalkForward.DefaultGrid(mcfg);
                var wf = await WalkForward.RunAsync(
                    input, grid,
                    (p, slice) => new BacktestSession(MkStrat(p), slice, runCfg),
                    runCfg.InitialQuoteBalance, s.InSampleDays, s.OosDays, s.WarmupDays, ct);
                report.WalkForwardOosPnLPercent = wf.OosPnLPercent;
                report.WalkForwardOosSharpe = wf.OosSharpe;
                report.WalkForwardOosMaxDrawdownPercent = wf.OosMaxDrawdownPercent;
                report.WalkForwardFolds = wf.Folds;
                report.TrialsTested = wf.TrialsPerFold;
                report.DeflatedSharpe = wf.DeflatedSharpe;
                report.ExpectedMaxSharpe = wf.ExpectedMaxSharpe;

                result.Validation = report;
                log($"Momentum validation done. OOS PnL {wf.OosPnLPercent:F1}%, DSR {wf.DeflatedSharpe:F2}, " +
                    $"delisted {audit.DelistedCoins}/{audit.TotalCoins} coins.");
            }

            return result;
        }

        private static PortfolioInput BuildPortfolioInput(Dictionary<string, List<UniverseDailyPoint>> window, string regime)
        {
            var candles = new Dictionary<string, IReadOnlyList<OHLCCandle>>(StringComparer.OrdinalIgnoreCase);
            var caps = new Dictionary<string, IReadOnlyList<MarketCapPoint>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, pts) in window)
            {
                var cs = new List<OHLCCandle>(pts.Count);
                var ms = new List<MarketCapPoint>(pts.Count);
                foreach (var p in pts)
                {
                    // CoinGecko gives daily close only → synthesise OHLC as close; Volume = USD quote volume.
                    cs.Add(new OHLCCandle(p.Date, p.Price, p.Price, p.Price, p.Price, p.VolumeUsd));
                    ms.Add(new MarketCapPoint(p.Date, p.MarketCap));
                }
                candles[id] = cs;
                caps[id] = ms;
            }
            return new PortfolioInput { Candles = candles, MarketCaps = caps, RegimeSymbol = regime };
        }

        private static BacktestConfig WithMargin(BacktestConfig c, MarginSettings margin) => new()
        {
            StrategyClass = c.StrategyClass,
            Coin = c.Coin,
            Currency = c.Currency,
            Interval = c.Interval,
            CandleCount = c.CandleCount,
            InitialQuoteBalance = c.InitialQuoteBalance,
            InitialBaseBalance = c.InitialBaseBalance,
            FeeFraction = c.FeeFraction,
            SlippageFraction = c.SlippageFraction,
            Margin = margin,
            Momentum = c.Momentum,
        };
    }
}
