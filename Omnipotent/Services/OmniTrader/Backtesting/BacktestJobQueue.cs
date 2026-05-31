using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Strategy;
using System.Threading.Channels;

namespace Omnipotent.Services.OmniTrader.Backtesting
{
    public sealed class BacktestJobQueue : IAsyncDisposable
    {
        private readonly Channel<string> queue = Channel.CreateUnbounded<string>();
        private readonly BacktestJobRepository jobRepo;
        private readonly MarketDataRouter marketData;
        private readonly StrategyRegistry registry;
        private readonly UniverseRepository universeRepo;
        private readonly MarketData.CoinGeckoUniverseProvider coingecko;
        private readonly Action<string> log;
        private readonly Action<string, Exception?> err;
        private CancellationTokenSource? cts;
        private Task? worker;

        public BacktestJobQueue(BacktestJobRepository jobRepo, MarketDataRouter marketData, StrategyRegistry registry,
            UniverseRepository universeRepo, MarketData.CoinGeckoUniverseProvider coingecko,
            Action<string> log, Action<string, Exception?> err)
        {
            this.jobRepo = jobRepo;
            this.marketData = marketData;
            this.registry = registry;
            this.universeRepo = universeRepo;
            this.coingecko = coingecko;
            this.log = log;
            this.err = err;
        }

        public void Start(CancellationToken externalToken = default)
        {
            if (worker != null) return;
            cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            worker = Task.Run(() => WorkerLoopAsync(cts.Token));
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
                    err($"Backtest job {row.Id} failed", ex);
                    try { await jobRepo.CompleteAsync(row.Id, BacktestJobStatus.Failed, null, ex.Message, DateTime.UtcNow, ct); } catch { }
                }
            }
        }

        private async Task RunSingleJobAsync(BacktestJobRow row, CancellationToken ct)
        {
            await jobRepo.StartAsync(row.Id, DateTime.UtcNow, ct);

            // Cross-sectional momentum (portfolio) jobs run the multi-asset path end-to-end.
            if (row.Config.Momentum != null)
            {
                var runner = new MomentumBacktestRunner(universeRepo, coingecko, log);
                var momentumResult = await runner.RunAsync(
                    row.Config,
                    onProgress: async (pct, done, total) =>
                    {
                        try { await jobRepo.UpdateProgressAsync(row.Id, pct, done, total, ct); } catch { }
                    },
                    cancellationCheck: () => jobRepo.IsCancellationRequestedAsync(row.Id, ct),
                    ct: ct);
                await jobRepo.CompleteAsync(row.Id, BacktestJobStatus.Succeeded, momentumResult, null, DateTime.UtcNow, ct);
                return;
            }

            var descriptor = registry.Resolve(row.StrategyClass)
                ?? throw new InvalidOperationException($"Unknown strategy {row.StrategyClass}");
            var strategy = registry.CreateInstance(descriptor.ClassName);

            string symbol = row.Config.Coin + row.Config.Currency;
            var candles = await marketData.GetHistoricalCandlesAsync(symbol, row.Config.Interval, row.Config.CandleCount, ct);

            var session = new BacktestSession(
                strategy, candles, row.Config,
                onProgress: async (pct, done, total) =>
                {
                    try { await jobRepo.UpdateProgressAsync(row.Id, pct, done, total, ct); } catch { }
                },
                cancellationCheck: () => jobRepo.IsCancellationRequestedAsync(row.Id, ct),
                log: m => log(m));

            var result = await session.RunAsync(ct);
            await jobRepo.CompleteAsync(row.Id, BacktestJobStatus.Succeeded, result, null, DateTime.UtcNow, ct);
        }

        public async ValueTask DisposeAsync()
        {
            cts?.Cancel();
            queue.Writer.TryComplete();
            if (worker != null)
            {
                try { await worker; } catch { }
            }
            cts?.Dispose();
        }
    }
}
