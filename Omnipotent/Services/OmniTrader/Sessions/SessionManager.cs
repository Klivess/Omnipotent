using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Execution;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Strategy;
using System.Collections.Concurrent;

namespace Omnipotent.Services.OmniTrader.Sessions
{
    public sealed class SessionManager
    {
        private readonly MarketDataRouter marketData;
        private readonly StrategyRegistry registry;
        private readonly DeploymentRepository deploymentRepo;
        private readonly OrderRepository orderRepo;
        private readonly FillRepository fillRepo;
        private readonly EquityRepository equityRepo;
        private readonly Func<IOrderRouter?> krakenFactory;
        private readonly Action<string> log;
        private readonly Action<string, Exception?> err;

        private readonly ConcurrentDictionary<string, PaperSession> paperSessions = new();
        private readonly ConcurrentDictionary<string, LiveSession> liveSessions = new();

        public SessionManager(
            MarketDataRouter marketData,
            StrategyRegistry registry,
            DeploymentRepository deploymentRepo,
            OrderRepository orderRepo,
            FillRepository fillRepo,
            EquityRepository equityRepo,
            Func<IOrderRouter?> krakenFactory,
            Action<string> log,
            Action<string, Exception?> err)
        {
            this.marketData = marketData;
            this.registry = registry;
            this.deploymentRepo = deploymentRepo;
            this.orderRepo = orderRepo;
            this.fillRepo = fillRepo;
            this.equityRepo = equityRepo;
            this.krakenFactory = krakenFactory;
            this.log = log;
            this.err = err;
        }

        public IReadOnlyCollection<string> ActiveDeploymentIds =>
            paperSessions.Keys.Concat(liveSessions.Keys).ToList();

        public bool TryGetPaper(string id, out PaperSession? session)
        {
            bool ok = paperSessions.TryGetValue(id, out var s);
            session = s;
            return ok;
        }

        public bool TryGetLive(string id, out LiveSession? session)
        {
            bool ok = liveSessions.TryGetValue(id, out var s);
            session = s;
            return ok;
        }

        public async Task<string> CreateDeploymentAsync(DeploymentConfig config, CancellationToken ct = default)
        {
            var descriptor = registry.Resolve(config.StrategyClass)
                ?? throw new InvalidOperationException($"Unknown strategy {config.StrategyClass}");

            var strategy = registry.CreateInstance(descriptor.ClassName);
            string id = Guid.NewGuid().ToString("N");

            // Live deployments start paused; user must call ArmLive to activate.
            var status = config.Mode == SessionMode.Live ? DeploymentStatus.Paused : DeploymentStatus.Running;

            decimal initialEquity = config.InitialQuoteBalance;
            var row = new DeploymentRow
            {
                Id = id,
                StrategyClass = descriptor.ClassName,
                Config = config,
                Mode = config.Mode,
                Status = status,
                CreatedUtc = DateTime.UtcNow,
                EquityInitial = initialEquity,
                EquityCurrent = initialEquity
            };
            await deploymentRepo.InsertAsync(row, ct);

            if (config.Mode == SessionMode.Paper)
            {
                var session = new PaperSession(id, config, strategy, marketData,
                    orderRepo, fillRepo, equityRepo, deploymentRepo, log, err);
                if (paperSessions.TryAdd(id, session))
                    await session.StartAsync(ct);
            }
            else
            {
                var exchange = krakenFactory()
                    ?? throw new InvalidOperationException("Kraken credentials not configured");
                var session = new LiveSession(id, config, strategy, marketData, exchange,
                    orderRepo, fillRepo, equityRepo, deploymentRepo, log, err);
                if (liveSessions.TryAdd(id, session))
                    await session.StartAsync(ct);
            }

            return id;
        }

        public async Task<bool> ArmLiveAsync(string id, CancellationToken ct = default)
        {
            if (!liveSessions.TryGetValue(id, out var session)) return false;
            session.Arm();
            await deploymentRepo.SetArmedLiveAsync(id, DateTime.UtcNow, ct);
            return true;
        }

        public async Task<bool> PauseAsync(string id, CancellationToken ct = default)
        {
            if (liveSessions.TryGetValue(id, out var live))
            {
                live.Disarm();
                try { await live.FlattenAsync(ct); } catch { }
                await deploymentRepo.SetPausedAsync(id, DateTime.UtcNow, ct);
                return true;
            }
            if (paperSessions.TryRemove(id, out var paper))
            {
                await paper.StopAsync();
                await deploymentRepo.SetPausedAsync(id, DateTime.UtcNow, ct);
                return true;
            }
            return false;
        }

        public async Task<bool> ResumeAsync(string id, CancellationToken ct = default)
        {
            var row = await deploymentRepo.GetAsync(id, ct);
            if (row == null) return false;
            if (row.Mode != SessionMode.Paper) return false;
            if (paperSessions.ContainsKey(id)) return true;

            var strategy = registry.CreateInstance(row.StrategyClass);
            var session = new PaperSession(id, row.Config, strategy, marketData,
                orderRepo, fillRepo, equityRepo, deploymentRepo, log, err,
                startingQuote: row.EquityCurrent);
            if (paperSessions.TryAdd(id, session))
            {
                await session.StartAsync(ct);
                await deploymentRepo.UpdateStatusAsync(id, DeploymentStatus.Running, ct: ct);
            }
            return true;
        }

        public async Task<bool> KillAsync(string id, CancellationToken ct = default)
        {
            bool killed = false;
            if (liveSessions.TryRemove(id, out var live))
            {
                live.Disarm();
                try { await live.FlattenAsync(ct); } catch { }
                await live.StopAsync();
                await live.DisposeAsync();
                killed = true;
            }
            if (paperSessions.TryRemove(id, out var paper))
            {
                await paper.StopAsync();
                await paper.DisposeAsync();
                killed = true;
            }
            if (killed)
                await deploymentRepo.UpdateStatusAsync(id, DeploymentStatus.Stopped, ct: ct);
            return killed;
        }

        public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
        {
            await KillAsync(id, ct);
            await deploymentRepo.DeleteAsync(id, ct);
            return true;
        }

        public async Task RecoverAsync(CancellationToken ct = default)
        {
            var runnable = await deploymentRepo.ListRunnableAsync(ct);
            foreach (var row in runnable)
            {
                try
                {
                    if (row.Mode == SessionMode.Paper && row.Status == DeploymentStatus.Running)
                    {
                        var strategy = registry.CreateInstance(row.StrategyClass);
                        var session = new PaperSession(row.Id, row.Config, strategy, marketData,
                            orderRepo, fillRepo, equityRepo, deploymentRepo, log, err,
                            startingQuote: row.EquityCurrent);
                        if (paperSessions.TryAdd(row.Id, session))
                            await session.StartAsync(ct);
                    }
                    else if (row.Mode == SessionMode.Live)
                    {
                        // Always recover live deployments as paused — user must re-arm.
                        var exchange = krakenFactory();
                        if (exchange == null)
                        {
                            err($"Cannot recover live deployment {row.Id} — Kraken not configured", null);
                            continue;
                        }
                        var strategy = registry.CreateInstance(row.StrategyClass);
                        var session = new LiveSession(row.Id, row.Config, strategy, marketData, exchange,
                            orderRepo, fillRepo, equityRepo, deploymentRepo, log, err,
                            startingQuote: row.EquityCurrent);
                        if (liveSessions.TryAdd(row.Id, session))
                        {
                            await session.StartAsync(ct);
                            await deploymentRepo.SetPausedAsync(row.Id, DateTime.UtcNow, ct);
                            log($"Recovered live deployment {row.Id} as paused — requires re-arm.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    err($"Failed to recover deployment {row.Id}", ex);
                    try { await deploymentRepo.UpdateStatusAsync(row.Id, DeploymentStatus.Errored, ex.Message, ct); } catch { }
                }
            }
        }

        public async Task ShutdownAsync()
        {
            foreach (var s in paperSessions.Values) try { await s.StopAsync(); } catch { }
            foreach (var s in liveSessions.Values) try { await s.StopAsync(); } catch { }
        }
    }
}
