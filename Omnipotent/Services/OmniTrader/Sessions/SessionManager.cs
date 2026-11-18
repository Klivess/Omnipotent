using Omnipotent.Services.KliveAPI.Caching;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Execution;
using Omnipotent.Services.OmniTrader.MarketData;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Strategy;
using System.Collections.Concurrent;

namespace Omnipotent.Services.OmniTrader.Sessions
{
    /// <summary>Which session runs a strategy, decided by what it declares it trades.</summary>
    public enum SessionKind
    {
        /// <summary>One symbol: <see cref="PaperSession"/> / <see cref="LiveSession"/> (OnCandleClose).</summary>
        Single,
        /// <summary>A fixed list of symbols: <see cref="MultiAssetSession"/> over exactly that list (OnUniverseBar).</summary>
        Basket,
        /// <summary>A dynamic top-N universe: <see cref="MultiAssetSession"/> resolving it (OnUniverseBar).</summary>
        Universe,
    }

    public sealed class SessionManager
    {
        // In-memory live/armed state that deployment routes read alongside the tracked
        // repos. Bumped on every create/arm/pause/resume/kill so a cached deployment
        // list can never serve a stale Armed flag or active-set.
        private const string CacheKey = "omnitrader:sessions";

        private readonly MarketDataRouter marketData;
        private readonly StrategyRegistry registry;
        private readonly DeploymentRepository deploymentRepo;
        private readonly OrderRepository orderRepo;
        private readonly FillRepository fillRepo;
        private readonly EquityRepository equityRepo;
        private readonly Func<IOrderRouter?> krakenFactory;
        private readonly Action<string> log;
        private readonly Action<string, Exception?> err;
        private readonly PaperBookStore? books;

        private readonly ConcurrentDictionary<string, PaperSession> paperSessions = new();
        private readonly ConcurrentDictionary<string, LiveSession> liveSessions = new();
        private readonly ConcurrentDictionary<string, MultiAssetSession> multiSessions = new();
        private readonly MarketData.BinanceUniverseProvider universeProvider = new();

        public SessionManager(
            MarketDataRouter marketData,
            StrategyRegistry registry,
            DeploymentRepository deploymentRepo,
            OrderRepository orderRepo,
            FillRepository fillRepo,
            EquityRepository equityRepo,
            Func<IOrderRouter?> krakenFactory,
            Action<string> log,
            Action<string, Exception?> err,
            PaperBookStore? books = null)
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
            this.books = books;
        }

        public PaperBookStore? Books => books;

        public IReadOnlyCollection<string> ActiveDeploymentIds
        {
            get
            {
                CacheDeps.NoteRead(CacheKey);
                return paperSessions.Keys.Concat(liveSessions.Keys).Concat(multiSessions.Keys).ToList();
            }
        }

        /// <summary>
        /// The session a declaration needs. The backtester already ran any strategy declaring more than one
        /// fixed symbol on the portfolio engine (BacktestJobQueue.RunPreparedAsync); deployments only looked
        /// at <see cref="StrategySymbols.IsUniverse"/>, so a fixed basket was given a single-symbol session
        /// whose OnCandleClose a basket strategy never implements, and it could not place a single order.
        /// </summary>
        public static SessionKind KindOf(StrategySymbols declaration)
            => declaration.IsUniverse ? SessionKind.Universe
             : (declaration.Fixed?.Count ?? 1) > 1 ? SessionKind.Basket
             : SessionKind.Single;

        public static string ShapeName(SessionKind kind) => kind switch
        {
            SessionKind.Basket => "basket",
            SessionKind.Universe => "universe",
            _ => "single",
        };

        private static DeploymentConfig WithSymbol(DeploymentConfig c, string symbol) => new()
        {
            StrategyClass = c.StrategyClass,
            Symbol = symbol,
            Interval = c.Interval,
            Mode = c.Mode,
            InitialQuoteBalance = c.InitialQuoteBalance,
            InitialBaseBalance = c.InitialBaseBalance,
            FeeFraction = c.FeeFraction,
            SlippageFraction = c.SlippageFraction,
            Margin = c.Margin,
            Caps = c.Caps,
            Parameters = c.Parameters,
            Execution = c.Execution,
            HistoryBars = c.HistoryBars,
            Origin = c.Origin,
        };

        public bool TryGetPaper(string id, out PaperSession? session)
        {
            bool ok = paperSessions.TryGetValue(id, out var s);
            session = s;
            return ok;
        }

        public bool TryGetMulti(string id, out MultiAssetSession? session)
        {
            bool ok = multiSessions.TryGetValue(id, out var s);
            session = s;
            return ok;
        }

        public bool TryGetLive(string id, out LiveSession? session)
        {
            bool ok = liveSessions.TryGetValue(id, out var s);
            session = s;
            return ok;
        }

        /// <summary>What the running session for a deployment reports; null when none runs in this process.</summary>
        public SessionPulse? PulseOf(string id)
        {
            if (paperSessions.TryGetValue(id, out var p)) return p.Pulse();
            if (multiSessions.TryGetValue(id, out var m)) return m.Pulse();
            if (liveSessions.TryGetValue(id, out var l)) return l.Pulse();
            return null;
        }

        public async Task<string> CreateDeploymentAsync(DeploymentConfig config, CancellationToken ct = default)
        {
            var descriptor = registry.Resolve(config.StrategyClass)
                ?? throw new InvalidOperationException($"Unknown strategy {config.StrategyClass}");

            var strategy = registry.CreateInstance(descriptor.ClassName);
            // Apply params (form's symbol is a fallback for the declared TradeSymbol), then key the
            // deployment off the symbol(s) the strategy declares.
            var pars = new Dictionary<string, object?>(config.Parameters ?? new(), StringComparer.OrdinalIgnoreCase);
            if (!pars.ContainsKey("TradeSymbol") && !string.IsNullOrWhiteSpace(config.Symbol))
                pars["TradeSymbol"] = config.Symbol;
            Strategy.Params.StrategyParams.Apply(strategy, pars);
            var declaration = strategy.DeclareSymbols();
            var kind = KindOf(declaration);
            if (kind == SessionKind.Single && !string.IsNullOrWhiteSpace(declaration.Primary) && declaration.Primary != config.Symbol)
                config = WithSymbol(config, declaration.Primary);
            else if (kind == SessionKind.Universe)
                config = WithSymbol(config, declaration.Universe!.RegimeSymbol);
            else if (kind == SessionKind.Basket && declaration.Primary != config.Symbol)
                config = WithSymbol(config, declaration.Primary);
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

            if (kind != SessionKind.Single)
            {
                IOrderRouter? exchange = config.Mode == SessionMode.Live
                    ? krakenFactory() ?? throw new InvalidOperationException("Kraken credentials not configured")
                    : null;
                var session = new MultiAssetSession(id, config, strategy, config.Mode, marketData, universeProvider,
                    exchange, orderRepo, fillRepo, equityRepo, deploymentRepo, log, err,
                    basket: kind == SessionKind.Basket ? declaration.Fixed : null, books: books);
                if (multiSessions.TryAdd(id, session))
                    await session.StartAsync(ct);
            }
            else if (config.Mode == SessionMode.Paper)
            {
                var session = new PaperSession(id, config, strategy, marketData,
                    orderRepo, fillRepo, equityRepo, deploymentRepo, log, err, books: books);
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

            CacheDeps.Bump(CacheKey);
            return id;
        }

        public async Task<bool> ArmLiveAsync(string id, CancellationToken ct = default)
        {
            if (liveSessions.TryGetValue(id, out var session)) session.Arm();
            else if (multiSessions.TryGetValue(id, out var multi) && multi.Mode == SessionMode.Live) multi.Arm();
            else return false;
            await deploymentRepo.SetArmedLiveAsync(id, DateTime.UtcNow, ct);
            CacheDeps.Bump(CacheKey);
            return true;
        }

        public async Task<bool> PauseAsync(string id, CancellationToken ct = default)
        {
            if (liveSessions.TryGetValue(id, out var live))
            {
                live.Disarm();
                try { await live.FlattenAsync(ct); } catch { }
                await deploymentRepo.SetPausedAsync(id, DateTime.UtcNow, ct);
                CacheDeps.Bump(CacheKey);
                return true;
            }
            if (multiSessions.TryGetValue(id, out var multi))
            {
                if (multi.Mode == SessionMode.Live) multi.Disarm();
                else { multiSessions.TryRemove(id, out _); await multi.StopAsync(); }
                await deploymentRepo.SetPausedAsync(id, DateTime.UtcNow, ct);
                CacheDeps.Bump(CacheKey);
                return true;
            }
            if (paperSessions.TryRemove(id, out var paper))
            {
                await paper.StopAsync();
                await deploymentRepo.SetPausedAsync(id, DateTime.UtcNow, ct);
                CacheDeps.Bump(CacheKey);
                return true;
            }
            return false;
        }

        public async Task<bool> ResumeAsync(string id, CancellationToken ct = default)
        {
            var row = await deploymentRepo.GetAsync(id, ct);
            if (row == null) return false;
            if (row.Mode != SessionMode.Paper) return false;
            if (paperSessions.ContainsKey(id) || multiSessions.ContainsKey(id)) return true;

            var strategy = registry.CreateInstance(row.StrategyClass);
            Strategy.Params.StrategyParams.Apply(strategy, row.Config.Parameters);
            var declaration = strategy.DeclareSymbols();
            var kind = KindOf(declaration);
            var book = await LoadBookAsync(id, ct);
            if (kind != SessionKind.Single)
            {
                var session = new MultiAssetSession(id, row.Config, strategy, SessionMode.Paper, marketData,
                    universeProvider, null, orderRepo, fillRepo, equityRepo, deploymentRepo, log, err,
                    startingQuote: row.EquityCurrent,
                    basket: kind == SessionKind.Basket ? declaration.Fixed : null, books: books, restore: book);
                if (multiSessions.TryAdd(id, session)) { await session.StartAsync(ct); await deploymentRepo.UpdateStatusAsync(id, DeploymentStatus.Running, ct: ct); }
                CacheDeps.Bump(CacheKey);
                return true;
            }
            var paper = new PaperSession(id, row.Config, strategy, marketData,
                orderRepo, fillRepo, equityRepo, deploymentRepo, log, err,
                startingQuote: row.EquityCurrent, books: books, restore: book);
            if (paperSessions.TryAdd(id, paper))
            {
                await paper.StartAsync(ct);
                await deploymentRepo.UpdateStatusAsync(id, DeploymentStatus.Running, ct: ct);
            }
            CacheDeps.Bump(CacheKey);
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
            if (multiSessions.TryRemove(id, out var multi))
            {
                multi.Disarm();
                await multi.StopAsync();
                await multi.DisposeAsync();
                killed = true;
            }
            if (paperSessions.TryRemove(id, out var paper))
            {
                await paper.StopAsync();
                await paper.DisposeAsync();
                killed = true;
            }
            if (!killed)
            {
                // A paused paper deployment, or one that errored or failed to recover, has no session in
                // memory. Stopping it used to answer 404 "Action failed", so it could only be resumed or
                // deleted. Stopping is a status change on the row.
                var row = await deploymentRepo.GetAsync(id, ct);
                if (row == null) return false;
                if (row.Status == DeploymentStatus.Stopped) return true;
            }
            await deploymentRepo.UpdateStatusAsync(id, DeploymentStatus.Stopped, ct: ct);
            CacheDeps.Bump(CacheKey);
            return true;
        }

        public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
        {
            await KillAsync(id, ct);
            await deploymentRepo.DeleteAsync(id, ct);
            if (books != null) { try { await books.DeleteAsync(id, ct); } catch { } }
            return true;
        }

        private async Task<PaperBook?> LoadBookAsync(string id, CancellationToken ct)
        {
            if (books == null) return null;
            try { return await books.GetAsync(id, ct); }
            catch (Exception ex) { err($"Paper book for {id} could not be read; continuing from its equity alone", ex); return null; }
        }

        public async Task RecoverAsync(CancellationToken ct = default)
        {
            var runnable = await deploymentRepo.ListRunnableAsync(ct);
            foreach (var row in runnable)
            {
                try
                {
                    var strategy = registry.CreateInstance(row.StrategyClass);
                    Strategy.Params.StrategyParams.Apply(strategy, row.Config.Parameters);
                    var declaration = strategy.DeclareSymbols();
                    var kind = KindOf(declaration);
                    var basket = kind == SessionKind.Basket ? declaration.Fixed : null;

                    if (row.Mode == SessionMode.Paper && row.Status == DeploymentStatus.Running)
                    {
                        // The saved book carries positions across the restart; without one (older rows) the
                        // deployment restarts from its equity as cash, as before.
                        var book = await LoadBookAsync(row.Id, ct);
                        if (kind != SessionKind.Single)
                        {
                            var session = new MultiAssetSession(row.Id, row.Config, strategy, SessionMode.Paper, marketData,
                                universeProvider, null, orderRepo, fillRepo, equityRepo, deploymentRepo, log, err,
                                startingQuote: row.EquityCurrent, basket: basket, books: books, restore: book);
                            if (multiSessions.TryAdd(row.Id, session)) await session.StartAsync(ct);
                        }
                        else
                        {
                            var session = new PaperSession(row.Id, row.Config, strategy, marketData,
                                orderRepo, fillRepo, equityRepo, deploymentRepo, log, err,
                                startingQuote: row.EquityCurrent, books: books, restore: book);
                            if (paperSessions.TryAdd(row.Id, session))
                                await session.StartAsync(ct);
                        }
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
                        if (kind != SessionKind.Single)
                        {
                            var session = new MultiAssetSession(row.Id, row.Config, strategy, SessionMode.Live, marketData,
                                universeProvider, exchange, orderRepo, fillRepo, equityRepo, deploymentRepo, log, err,
                                startingQuote: row.EquityCurrent, basket: basket);
                            if (multiSessions.TryAdd(row.Id, session))
                            {
                                await session.StartAsync(ct);
                                await deploymentRepo.SetPausedAsync(row.Id, DateTime.UtcNow, ct);
                                log($"Recovered live multi-asset deployment {row.Id} as paused — requires re-arm.");
                            }
                        }
                        else
                        {
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
                }
                catch (Exception ex)
                {
                    err($"Failed to recover deployment {row.Id}", ex);
                    try { await deploymentRepo.UpdateStatusAsync(row.Id, DeploymentStatus.Errored, ex.Message, ct); } catch { }
                }
            }
            CacheDeps.Bump(CacheKey);
        }

        /// <summary>Whether a live deployment (single- or multi-asset) is currently armed for real orders.</summary>
        public bool IsDeploymentArmed(string id)
        {
            CacheDeps.NoteRead(CacheKey);
            if (liveSessions.TryGetValue(id, out var live)) return live.IsArmed;
            if (multiSessions.TryGetValue(id, out var multi)) return multi.Mode == SessionMode.Live && multi.IsArmed;
            return false;
        }

        public async Task ShutdownAsync()
        {
            foreach (var s in paperSessions.Values) try { await s.StopAsync(); } catch { }
            foreach (var s in liveSessions.Values) try { await s.StopAsync(); } catch { }
            foreach (var s in multiSessions.Values) try { await s.StopAsync(); } catch { }
        }
    }
}
