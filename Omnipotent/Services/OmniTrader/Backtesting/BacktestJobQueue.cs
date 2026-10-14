using Omnipotent.Services.OmniTrader.Backtesting.Validation;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Strategy;
using Omnipotent.Services.OmniTrader.Strategy.Params;
using System.Threading.Channels;

namespace Omnipotent.Services.OmniTrader.Backtesting
{
    /// <summary>Bars fetched for a run, reusable across many runs of the same window (sweeps, batches).</summary>
    public sealed class PreparedData
    {
        public required IReadOnlyDictionary<string, IReadOnlyList<OHLCCandle>> Candles { get; init; }
        public string? RegimeSymbol { get; init; }
        public bool IsUniverse { get; init; }
        public string PrimarySymbol { get; init; } = "";
    }

    public sealed class BacktestJobQueue : IAsyncDisposable
    {
        private readonly Channel<string> queue = Channel.CreateUnbounded<string>();
        private readonly BacktestJobRepository jobRepo;
        private readonly MarketDataRouter marketData;
        private readonly StrategyRegistry registry;
        private readonly UniverseRepository universeRepo;
        private readonly Action<string> log;
        private readonly Action<string, Exception?> err;
        private readonly int workerCount;
        private CancellationTokenSource? cts;
        private readonly List<Task> workers = new();
        private int running;

        /// <summary>Raised after a job reaches a terminal state (succeeded, failed or cancelled).</summary>
        public event Func<BacktestJobRow, Task>? Completed;

        public BacktestJobQueue(BacktestJobRepository jobRepo, MarketDataRouter marketData, StrategyRegistry registry,
            UniverseRepository universeRepo,
            Action<string> log, Action<string, Exception?> err, int? workers = null)
        {
            this.jobRepo = jobRepo;
            this.marketData = marketData;
            this.registry = registry;
            this.universeRepo = universeRepo;
            this.log = log;
            this.err = err;
            workerCount = Math.Clamp(workers ?? Math.Max(1, Environment.ProcessorCount / 2), 1, 8);
        }

        public int Workers => workerCount;
        public int Running => Volatile.Read(ref running);

        public void Start(CancellationToken externalToken = default)
        {
            if (workers.Count > 0) return;
            cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            for (int i = 0; i < workerCount; i++)
                workers.Add(Task.Run(() => WorkerLoopAsync(cts.Token)));
        }

        public async Task<string> EnqueueAsync(BacktestConfig config, CancellationToken ct = default)
        {
            string id = Guid.NewGuid().ToString("N");
            var row = new BacktestJobRow
            {
                Id = id,
                StrategyClass = config.StrategyClass,
                Config = config,
                Status = BacktestJobStatus.Queued,
                QueuedUtc = DateTime.UtcNow
            };
            await jobRepo.InsertAsync(row, ct);
            queue.Writer.TryWrite(id);
            return id;
        }

        public async Task RestoreQueuedAsync(CancellationToken ct = default)
        {
            var queued = await jobRepo.ListQueuedAsync(ct);
            foreach (var row in queued) queue.Writer.TryWrite(row.Id);
        }

        private async Task WorkerLoopAsync(CancellationToken ct)
        {
            await foreach (var jobId in queue.Reader.ReadAllAsync(ct))
            {
                BacktestJobRow? row;
                try { row = await jobRepo.GetAsync(jobId, ct); }
                catch (Exception ex) { err($"Failed to load backtest job {jobId}", ex); continue; }
                if (row == null) continue;
                if (row.Status != BacktestJobStatus.Queued) continue;
                if (row.CancellationRequested)
                {
                    try { await jobRepo.CompleteAsync(row.Id, BacktestJobStatus.Cancelled, null, "cancelled before start", DateTime.UtcNow, ct); } catch { }
                    await RaiseCompletedAsync(row.Id);
                    continue;
                }

                Interlocked.Increment(ref running);
                try
                {
                    await RunSingleJobAsync(row, ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    try { await jobRepo.CompleteAsync(row.Id, BacktestJobStatus.Cancelled, null, "cancelled", DateTime.UtcNow, ct); } catch { }
                }
                catch (Exception ex)
                {
                    if (ex is not StrategyRuntimeException) err($"Backtest job {row.Id} failed", ex);
                    try { await jobRepo.CompleteAsync(row.Id, BacktestJobStatus.Failed, null, ex.Message, DateTime.UtcNow, ct); } catch { }
                }
                finally
                {
                    Interlocked.Decrement(ref running);
                }
                await RaiseCompletedAsync(row.Id);
            }
        }

        private async Task RaiseCompletedAsync(string jobId)
        {
            var handlers = Completed;
            if (handlers == null) return;
            BacktestJobRow? finished = null;
            try { finished = await jobRepo.GetAsync(jobId); } catch { }
            if (finished == null) return;
            foreach (Func<BacktestJobRow, Task> handler in handlers.GetInvocationList())
            {
                try { await handler(finished); }
                catch (Exception ex) { err("backtest completion handler failed", ex); }
            }
        }

        private async Task RunSingleJobAsync(BacktestJobRow row, CancellationToken ct)
        {
            await jobRepo.StartAsync(row.Id, DateTime.UtcNow, ct);

            Func<double, int, int, Task> onProgress = async (pct, done, total) =>
            {
                try { await jobRepo.UpdateProgressAsync(row.Id, pct, done, total, ct); } catch { }
            };
            Func<Task<bool>> cancellationCheck = () => jobRepo.IsCancellationRequestedAsync(row.Id, ct);

            var result = await RunAsync(row.Config, onProgress, cancellationCheck, ct);
            await jobRepo.CompleteAsync(row.Id, BacktestJobStatus.Succeeded, result, null, DateTime.UtcNow, ct);
        }

        // ── in-process runs (no job row) ─────────────────────────────────────────

        /// <summary>Create the strategy for a config, with parameters applied and the trade symbol folded in.</summary>
        public TradingStrategy CreateStrategy(BacktestConfig config)
        {
            var descriptor = registry.Resolve(config.StrategyClass)
                ?? throw new InvalidOperationException($"Unknown strategy {config.StrategyClass}");
            var strategy = registry.CreateInstance(descriptor.ClassName);
            StrategyParams.Apply(strategy, ParametersWithSymbol(config));
            return strategy;
        }

        private static Dictionary<string, object?> ParametersWithSymbol(BacktestConfig config)
        {
            // The symbol the strategy DECLARES drives the data fetch; Coin/Currency are a fallback for
            // requests that do not carry a TradeSymbol parameter.
            var pars = new Dictionary<string, object?>(config.Parameters ?? new(), StringComparer.OrdinalIgnoreCase);
            if (!pars.ContainsKey("TradeSymbol") && !string.IsNullOrWhiteSpace(config.Coin))
                pars["TradeSymbol"] = config.Coin + config.Currency;
            return pars;
        }

        /// <summary>Run one backtest end to end: fetch data, run, validate. Nothing is persisted.</summary>
        public async Task<BacktestResult> RunAsync(BacktestConfig config,
            Func<double, int, int, Task>? onProgress = null, Func<Task<bool>>? cancellationCheck = null,
            CancellationToken ct = default)
        {
            var strategy = CreateStrategy(config);
            var declaration = strategy.DeclareSymbols();

            // The point-in-time momentum research suite is an explicit opt-in; it does not drive dispatch.
            if (declaration.IsUniverse && config.Momentum != null)
                return await new MomentumBacktestRunner(universeRepo, log).RunAsync(config, onProgress, cancellationCheck, ct);

            var data = await PrepareAsync(config, strategy, ct);
            return await RunPreparedAsync(config, data, strategy, onProgress, cancellationCheck, ct);
        }

        /// <summary>Fetch the bars a config needs. Universe strategies resolve their universe here.</summary>
        public async Task<PreparedData> PrepareAsync(BacktestConfig config, TradingStrategy? strategy = null, CancellationToken ct = default)
        {
            strategy ??= CreateStrategy(config);
            var declaration = strategy.DeclareSymbols();
            var candles = new Dictionary<string, IReadOnlyList<OHLCCandle>>(StringComparer.OrdinalIgnoreCase);

            if (declaration.IsUniverse)
            {
                var spec = declaration.Universe!;
                var provider = new BinanceUniverseProvider(spec.QuoteAsset);
                var symbols = await provider.ResolveUniverseAsync(spec.TopN, ct);
                if (!symbols.Contains(spec.RegimeSymbol, StringComparer.OrdinalIgnoreCase)) symbols.Add(spec.RegimeSymbol);

                using var gate = new SemaphoreSlim(6, 6);
                var lockObj = new object();
                await Task.WhenAll(symbols.Select(async sym =>
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        var bars = await FetchCandlesAsync(sym, config, ct);
                        // USD quote volume so liquidity ranking sees USD (matches MultiAssetSession).
                        if (bars.Count > 0)
                            lock (lockObj)
                                candles[sym] = bars.Select(b => new OHLCCandle(b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume * b.Close)).ToList();
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { err($"universe backtest: fetch {sym} failed", ex); }
                    finally { gate.Release(); }
                }));
                if (candles.Count == 0)
                    throw new InvalidOperationException("No universe data could be fetched for the backtest.");
                return new PreparedData { Candles = candles, RegimeSymbol = spec.RegimeSymbol, IsUniverse = true, PrimarySymbol = spec.RegimeSymbol };
            }

            var fixedSymbols = (declaration.Fixed ?? new[] { declaration.Primary }).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
            if (!string.IsNullOrWhiteSpace(config.Benchmark) && !fixedSymbols.Contains(config.Benchmark!, StringComparer.OrdinalIgnoreCase))
                fixedSymbols.Add(config.Benchmark!);
            foreach (var sym in fixedSymbols)
            {
                var bars = await FetchCandlesAsync(sym, config, ct);
                if (bars.Count > 0) candles[sym] = bars;
            }
            string primary = declaration.Primary;
            if (!candles.ContainsKey(primary))
                throw new InvalidOperationException($"No price history for {primary} on {config.Interval}.");
            return new PreparedData { Candles = candles, RegimeSymbol = primary, IsUniverse = false, PrimarySymbol = primary };
        }

        /// <summary>Run against already-fetched bars. Safe to call concurrently with distinct strategy instances.</summary>
        public async Task<BacktestResult> RunPreparedAsync(BacktestConfig config, PreparedData data, TradingStrategy? strategy = null,
            Func<double, int, int, Task>? onProgress = null, Func<Task<bool>>? cancellationCheck = null,
            CancellationToken ct = default)
        {
            strategy ??= CreateStrategy(config);
            var declaration = strategy.DeclareSymbols();
            BacktestResult result;

            if (declaration.IsUniverse || data.IsUniverse || (declaration.Fixed?.Count ?? 1) > 1)
            {
                var input = new PortfolioInput { Candles = data.Candles, RegimeSymbol = data.RegimeSymbol };
                result = await new BacktestSession(strategy, input, config, onProgress, cancellationCheck, m => log(m)).RunPortfolioAsync(ct);
            }
            else
            {
                string symbol = declaration.Primary;
                var candles = data.Candles.TryGetValue(symbol, out var c) ? c : Array.Empty<OHLCCandle>();
                var runConfig = WithSymbol(config, symbol);
                var session = new BacktestSession(strategy, candles, runConfig, onProgress, cancellationCheck, m => log(m));
                result = await session.RunAsync(ct, data.Candles);
            }

            // Optional generic post-backtest validation — strategy-agnostic; no per-strategy code.
            if (config.Validation != null)
            {
                var stratType = strategy.GetType();
                var schema = registry.Resolve(config.StrategyClass)?.Parameters ?? StrategyParams.For(stratType);
                var baseParams = (IReadOnlyDictionary<string, object?>)ParametersWithSymbol(config);
                var input = new PortfolioInput { Candles = data.Candles, RegimeSymbol = data.RegimeSymbol };
                result.Validation = await GenericValidation.RunAsync(
                    input, config, baseParams, schema,
                    p => { var s = registry.CreateInstance(config.StrategyClass); StrategyParams.Apply(s, p); return s; },
                    result, config.Validation, ct);
            }
            return result;
        }

        /// <summary>Bars for a backtest: a [FromUtc, ToUtc] range when set, otherwise the most recent CandleCount.</summary>
        public Task<IReadOnlyList<OHLCCandle>> FetchCandlesAsync(string symbol, BacktestConfig config, CancellationToken ct)
            => config.FromUtc.HasValue && config.ToUtc.HasValue
                ? marketData.GetHistoricalCandlesRangeAsync(symbol, config.Interval, config.FromUtc.Value, config.ToUtc.Value, ct)
                : marketData.GetHistoricalCandlesAsync(symbol, config.Interval, config.CandleCount, ct);

        /// <summary>Copy a config with the engine symbol set to the strategy's declared pair.</summary>
        public static BacktestConfig WithSymbol(BacktestConfig c, string symbol) => new()
        {
            StrategyClass = c.StrategyClass,
            Coin = symbol,
            Currency = "",
            Interval = c.Interval,
            CandleCount = c.CandleCount,
            FromUtc = c.FromUtc,
            ToUtc = c.ToUtc,
            InitialQuoteBalance = c.InitialQuoteBalance,
            InitialBaseBalance = c.InitialBaseBalance,
            FeeFraction = c.FeeFraction,
            SlippageFraction = c.SlippageFraction,
            Margin = c.Margin,
            Momentum = c.Momentum,
            Validation = c.Validation,
            Parameters = c.Parameters,
            Execution = c.Execution,
            Benchmark = c.Benchmark,
            Label = c.Label,
            Origin = c.Origin,
            StudyId = c.StudyId,
        };

        public async ValueTask DisposeAsync()
        {
            cts?.Cancel();
            queue.Writer.TryComplete();
            foreach (var w in workers)
            {
                try { await w; } catch { }
            }
            cts?.Dispose();
        }
    }
}
