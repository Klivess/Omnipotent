using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Data;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.OmniTrader
{
    public class OmniTraderSimulator
    {
        private enum PositionSide
        {
            None,
            Long,
            Short
        }

        private sealed class DeploymentState
        {
            public Guid DeploymentId { get; init; }
            public required OmniTraderStrategy Strategy { get; init; }
            public required string Symbol { get; init; }
            public required OmniTraderFinanceData.TimeInterval Interval { get; init; }
            public required BacktestSettings Settings { get; init; }
            public required CancellationTokenSource Cts { get; init; }

            public object SyncRoot { get; } = new();

            public DateTime StartTimeUtc { get; set; } = DateTime.UtcNow;
            public DateTime? EndTimeUtc { get; set; }
            public int CandlesProcessed { get; set; }

            public decimal QuoteBalance { get; set; }
            public decimal BaseBalance { get; set; }
            public decimal TotalFees { get; set; }

            public PositionSide PositionSide { get; set; }
            public decimal PositionQuantity { get; set; }
            public decimal PositionEntryPrice { get; set; }
            public decimal PositionCost { get; set; }
            public decimal PositionEntryFee { get; set; }
            public DateTime PositionEntryTime { get; set; }
            public decimal? StopLossPrice { get; set; }
            public decimal? TakeProfitPrice { get; set; }

            public OmniTraderFinanceData.OHLCCandle? LatestClosedCandle { get; set; }
            public List<TradeRecord> Trades { get; } = [];

            public Task? StreamTask { get; set; }

            public required string StrategyKey { get; init; }
            public required PersistedStrategyHistory History { get; init; }
            public required PersistedSessionRecord SessionRecord { get; init; }

            public EventHandler<TradeSignalEventArgs>? LongHandler { get; set; }
            public EventHandler<TradeSignalEventArgs>? ShortHandler { get; set; }
            public EventHandler<TradeSignalEventArgs>? SellHandler { get; set; }
            public EventHandler<TradePosition>? CloseHandler { get; set; }
        }

        public sealed class PersistedSessionRecord
        {
            public Guid DeploymentId { get; set; }
            public string Symbol { get; set; } = string.Empty;
            public OmniTraderFinanceData.TimeInterval Interval { get; set; }
            public DateTime StartTimeUtc { get; set; }
            public DateTime EndTimeUtc { get; set; }
            public int CandlesProcessed { get; set; }
            public int NewTrades { get; set; }
            public decimal FinalQuoteBalance { get; set; }
            public decimal FinalBaseBalance { get; set; }
            public decimal FinalEquity { get; set; }
        }

        public sealed class PersistedBacktestRecord
        {
            public DateTime RunAtUtc { get; set; }
            public string Symbol { get; set; } = string.Empty;
            public string Currency { get; set; } = string.Empty;
            public OmniTraderFinanceData.TimeInterval Interval { get; set; }
            public int CandleCount { get; set; }
            public BacktestSettings Settings { get; set; } = new();
            public OmniBacktestResult Result { get; set; } = new();
        }

        public sealed class PersistedActiveDeployment
        {
            public string StrategyName { get; set; } = string.Empty;
            public string StrategyKey { get; set; } = string.Empty;
            public string Symbol { get; set; } = string.Empty;
            public OmniTraderFinanceData.TimeInterval Interval { get; set; }
            public BacktestSettings Settings { get; set; } = new();
            public DateTime LastUpdatedUtc { get; set; }
        }

        public sealed class StrategyInsightView
        {
            public string StrategyName { get; set; } = string.Empty;
            public string StrategyKey { get; set; } = string.Empty;
            public bool IsCurrentlyDeployed { get; set; }
            public Guid? ActiveDeploymentId { get; set; }
            public OmniBacktestResult? LiveSnapshot { get; set; }
            public OmniBacktestResult PersistedSnapshot { get; set; } = new();
            public int TotalSessions { get; set; }
            public int TotalBacktests { get; set; }
            public List<PersistedSessionRecord> RecentSessions { get; set; } = [];
            public List<PersistedBacktestRecord> RecentBacktests { get; set; } = [];
        }

        private sealed class PersistedStrategyHistory
        {
            public string StrategyName { get; set; } = string.Empty;
            public string StrategyKey { get; set; } = string.Empty;
            public decimal InitialQuoteBalance { get; set; }
            public decimal InitialBaseBalance { get; set; }
            public decimal LastQuoteBalance { get; set; }
            public decimal LastBaseBalance { get; set; }
            public decimal TotalFeesPaid { get; set; }
            public int TotalCandlesProcessed { get; set; }
            public DateTime LastUpdatedUtc { get; set; }
            public List<TradeRecord> AllTrades { get; set; } = [];
            public List<PersistedSessionRecord> Sessions { get; set; } = [];
            public List<PersistedBacktestRecord> Backtests { get; set; } = [];
        }

        private readonly OmniTrader parent;
        private readonly ConcurrentDictionary<Guid, DeploymentState> deployments = new();
        private readonly ConcurrentDictionary<string, PersistedStrategyHistory> historyByStrategyKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim activeDeploymentRegistryLoadLock = new(1, 1);
        private readonly SemaphoreSlim activeDeploymentRegistryWriteLock = new(1, 1);
        private readonly ConcurrentDictionary<string, PersistedActiveDeployment> activeDeploymentRegistry = new(StringComparer.OrdinalIgnoreCase);
        private volatile bool activeRegistryLoaded;

        public OmniTraderSimulator(OmniTrader parent)
        {
            this.parent = parent;
        }

        public async Task<Guid> Deploy(
            OmniTraderStrategy strategy,
            string symbol,
            OmniTraderFinanceData.TimeInterval interval,
            BacktestSettings? settings = null,
            CancellationToken cancellationToken = default)
        {
            await EnsureActiveDeploymentRegistryLoaded();

            var effectiveSettings = settings ?? new BacktestSettings();
            string strategyKey = GetStrategyKey(strategy.Name);
            var history = await GetOrLoadStrategyHistory(strategy.Name, strategyKey);

            if (deployments.Values.Any(d => ReferenceEquals(d.Strategy, strategy)))
                throw new InvalidOperationException("This strategy instance is already deployed. Create a new strategy instance for another deployment.");

            if (deployments.Values.Any(d => string.Equals(d.StrategyKey, strategyKey, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A deployment for strategy '{strategy.Name}' is already active.");

            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var sessionRecord = new PersistedSessionRecord
            {
                DeploymentId = Guid.NewGuid(),
                Symbol = symbol.ToUpperInvariant(),
                Interval = interval,
                StartTimeUtc = DateTime.UtcNow
            };

            if (history.Sessions.Count == 0)
            {
                history.InitialQuoteBalance = effectiveSettings.InitialQuoteBalance;
                history.InitialBaseBalance = effectiveSettings.InitialBaseBalance;
                history.LastQuoteBalance = effectiveSettings.InitialQuoteBalance;
                history.LastBaseBalance = effectiveSettings.InitialBaseBalance;
            }

            var deployment = new DeploymentState
            {
                DeploymentId = sessionRecord.DeploymentId,
                Strategy = strategy,
                Symbol = sessionRecord.Symbol,
                Interval = interval,
                Settings = effectiveSettings,
                Cts = cts,
                QuoteBalance = history.Sessions.Count > 0 ? history.LastQuoteBalance : effectiveSettings.InitialQuoteBalance,
                BaseBalance = history.Sessions.Count > 0 ? history.LastBaseBalance : effectiveSettings.InitialBaseBalance,
                TotalFees = history.TotalFeesPaid,
                CandlesProcessed = history.TotalCandlesProcessed,
                PositionSide = PositionSide.None,
                StrategyKey = strategyKey,
                History = history,
                SessionRecord = sessionRecord
            };

            deployment.Trades.AddRange(history.AllTrades.Select(CloneTrade));

            await strategy.PrepareForSession(parent, TradeSessionType.Simulator);

            deployment.LongHandler = (_, args) => HandleLongSignal(deployment, args);
            deployment.ShortHandler = (_, args) => HandleShortSignal(deployment, args);
            deployment.SellHandler = (_, args) => HandleSellSignal(deployment, args);
            deployment.CloseHandler = (_, position) => HandleClosePosition(deployment, position);

            strategy.OnLong += deployment.LongHandler;
            strategy.OnShort += deployment.ShortHandler;
            strategy.OnSell += deployment.SellHandler;
            strategy.ClosePosition += deployment.CloseHandler;

            if (!deployments.TryAdd(deployment.DeploymentId, deployment))
            {
                UnwireStrategyHandlers(deployment);
                cts.Dispose();
                throw new InvalidOperationException("Failed to add deployment.");
            }

            deployment.StreamTask = Task.Run(() => RunMarketStreamLoopAsync(deployment, deployment.Cts.Token), CancellationToken.None);

            await UpsertActiveDeployment(new PersistedActiveDeployment
            {
                StrategyName = strategy.Name,
                StrategyKey = strategyKey,
                Symbol = deployment.Symbol,
                Interval = deployment.Interval,
                Settings = deployment.Settings,
                LastUpdatedUtc = DateTime.UtcNow
            });

            await parent.ServiceLog($"Simulator deployed strategy '{strategy.Name}' with id {deployment.DeploymentId} on {deployment.Symbol} {interval}.");
            return deployment.DeploymentId;
        }

        public async Task<bool> Undeploy(Guid deploymentId)
        {
            await EnsureActiveDeploymentRegistryLoaded();

            if (!deployments.TryRemove(deploymentId, out var deployment))
                return false;

            deployment.EndTimeUtc = DateTime.UtcNow;

            lock (deployment.SyncRoot)
            {
                if (HasOpenPosition(deployment) && deployment.LatestClosedCandle.HasValue)
                {
                    if (deployment.PositionSide == PositionSide.Short)
                        CloseShort(deployment, deployment.LatestClosedCandle.Value, deployment.PositionQuantity, deployment.LatestClosedCandle.Value.Close);
                    else
                        CloseLong(deployment, deployment.LatestClosedCandle.Value, deployment.PositionQuantity, deployment.LatestClosedCandle.Value.Close);
                }
            }

            try
            {
                deployment.Cts.Cancel();
                if (deployment.StreamTask != null)
                    await deployment.StreamTask;
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                lock (deployment.SyncRoot)
                {
                    FinaliseAndMergeHistory(deployment);
                }

                UnwireStrategyHandlers(deployment);
                deployment.Cts.Dispose();
                await PersistHistory(deployment.History);
                await RemoveActiveDeployment(deployment.StrategyKey);
                await parent.ServiceLog($"Simulator undeployed strategy id {deploymentId}.");
            }

            return true;
        }

        public async Task UndeployAll()
        {
            foreach (var deploymentId in deployments.Keys.ToList())
                await Undeploy(deploymentId);
        }

        public IReadOnlyCollection<Guid> GetDeploymentIds()
        {
            return deployments.Keys.ToList();
        }

        public OmniBacktestResult GetSnapshot(Guid deploymentId)
        {
            if (!deployments.TryGetValue(deploymentId, out var deployment))
                throw new KeyNotFoundException($"No deployment found for id {deploymentId}.");

            return BuildSnapshot(deployment);
        }

        public Dictionary<Guid, OmniBacktestResult> GetAllSnapshots()
        {
            var output = new Dictionary<Guid, OmniBacktestResult>();
            foreach (var kvp in deployments)
                output[kvp.Key] = BuildSnapshot(kvp.Value);
            return output;
        }

        public Dictionary<Guid, OmniBacktestResult> GetAllSnapshotSummaries()
        {
            var output = new Dictionary<Guid, OmniBacktestResult>();
            foreach (var kvp in deployments)
                output[kvp.Key] = BuildSnapshot(kvp.Value, includeTrades: false);
            return output;
        }

        public async Task<IReadOnlyCollection<PersistedActiveDeployment>> GetPersistedActiveDeployments()
        {
            await EnsureActiveDeploymentRegistryLoaded();
            return activeDeploymentRegistry.Values.OrderBy(v => v.StrategyName).ToList();
        }

        public async Task<IReadOnlyCollection<PersistedActiveDeployment>> GetPersistedActiveDeployments(int timeoutMs)
        {
            try
            {
                await EnsureActiveDeploymentRegistryLoaded().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
            }
            catch (TimeoutException)
            {
            }

            return activeDeploymentRegistry.Values.OrderBy(v => v.StrategyName).ToList();
        }

        public async Task<OmniBacktestResult> RunBacktestAndPersist(
            OmniTraderStrategy strategy,
            string coin,
            string currency,
            OmniTraderFinanceData.TimeInterval interval,
            int candleCount,
            BacktestSettings? settings = null)
        {
            string strategyKey = GetStrategyKey(strategy.Name);
            var history = await GetOrLoadStrategyHistory(strategy.Name, strategyKey);

            var testSet = await parent.data.GetCryptoCandlesDataAsync(coin, currency, interval, candleCount);
            var result = await strategy.BacktestStrategy(testSet, settings);

            history.Backtests.Add(new PersistedBacktestRecord
            {
                RunAtUtc = DateTime.UtcNow,
                Symbol = coin,
                Currency = currency,
                Interval = interval,
                CandleCount = candleCount,
                Settings = settings ?? new BacktestSettings(),
                Result = result
            });

            await PersistHistory(history);
            return result;
        }

        public async Task<StrategyInsightView> GetStrategyInsight(string strategyName, bool includeTrades = false)
        {
            string strategyKey = GetStrategyKey(strategyName);
            var history = await GetOrLoadStrategyHistory(strategyName, strategyKey);
            var persisted = BuildSnapshotFromHistory(history, includeTrades);

            var active = deployments.Values.FirstOrDefault(d => string.Equals(d.StrategyKey, strategyKey, StringComparison.OrdinalIgnoreCase));

            var insight = new StrategyInsightView
            {
                StrategyName = history.StrategyName,
                StrategyKey = strategyKey,
                IsCurrentlyDeployed = active != null,
                ActiveDeploymentId = active?.DeploymentId,
                LiveSnapshot = active == null ? null : BuildSnapshot(active, includeTrades),
                PersistedSnapshot = persisted,
                TotalSessions = history.Sessions.Count,
                TotalBacktests = history.Backtests.Count,
                RecentSessions = history.Sessions.TakeLast(25).ToList(),
                RecentBacktests = history.Backtests.TakeLast(25).ToList()
            };

            return insight;
        }

        public async Task<OmniBacktestResult> GetPersistedStrategySnapshot(string strategyName, bool includeTrades = false)
        {
            string strategyKey = GetStrategyKey(strategyName);
            var history = await GetOrLoadStrategyHistory(strategyName, strategyKey);
            return BuildSnapshotFromHistory(history, includeTrades);
        }

        public async Task<Dictionary<string, OmniBacktestResult>> GetAllPersistedStrategySnapshots()
        {
            EnsureHistoryDirectoryExists();

            var output = new Dictionary<string, OmniBacktestResult>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in historyByStrategyKey.Keys)
            {
                if (historyByStrategyKey.TryGetValue(key, out var cached))
                    output[cached.StrategyName] = BuildSnapshotFromHistory(cached, includeTrades: false);
            }

            return output;
        }

        private async Task RunMarketStreamLoopAsync(DeploymentState deployment, CancellationToken ct)
        {
            string streamInterval = ToBinanceInterval(deployment.Interval);
            string endpoint = $"wss://stream.binance.com:9443/ws/{deployment.Symbol.ToLowerInvariant()}@kline_{streamInterval}";

            while (!ct.IsCancellationRequested)
            {
                using var socket = new ClientWebSocket();
                try
                {
                    await socket.ConnectAsync(new Uri(endpoint), ct);
                    await parent.ServiceLog($"Simulator stream connected for deployment {deployment.DeploymentId}: {deployment.Symbol} {streamInterval}");

                    byte[] buffer = new byte[32 * 1024];
                    while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        string message = await ReceiveFullMessageAsync(socket, buffer, ct);
                        var candle = TryParseClosedCandle(message);
                        if (!candle.HasValue)
                            continue;

                        lock (deployment.SyncRoot)
                        {
                            deployment.LatestClosedCandle = candle.Value;
                            deployment.CandlesProcessed++;
                            if (HasOpenPosition(deployment))
                                CheckStopLossTakeProfit(deployment, candle.Value);
                        }

                        await deployment.Strategy.CandleClose(candle.Value);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    await parent.ServiceLogError(ex, $"Simulator stream disconnected for deployment {deployment.DeploymentId}, retrying in 3s.");
                    await Task.Delay(3000, ct);
                }
            }
        }

        private static async Task<string> ReceiveFullMessageAsync(ClientWebSocket socket, byte[] buffer, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    return string.Empty;
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private static OmniTraderFinanceData.OHLCCandle? TryParseClosedCandle(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("k", out JsonElement kline))
                return null;

            if (!kline.TryGetProperty("x", out JsonElement isClosedElement) || !isClosedElement.GetBoolean())
                return null;

            long closeTimestampMs = kline.GetProperty("T").GetInt64();
            DateTime timestamp = DateTimeOffset.FromUnixTimeMilliseconds(closeTimestampMs).UtcDateTime;

            decimal ParseDecimal(string propertyName) => decimal.Parse(kline.GetProperty(propertyName).GetString()!, CultureInfo.InvariantCulture);

            return new OmniTraderFinanceData.OHLCCandle
            {
                Timestamp = timestamp,
                Open = ParseDecimal("o"),
                High = ParseDecimal("h"),
                Low = ParseDecimal("l"),
                Close = ParseDecimal("c"),
                Volume = ParseDecimal("v"),
                VWAP = ParseDecimal("c"),
                TradeCount = kline.GetProperty("n").GetDecimal()
            };
        }

        private static string ToBinanceInterval(OmniTraderFinanceData.TimeInterval interval)
        {
            return interval switch
            {
                OmniTraderFinanceData.TimeInterval.OneMinute => "1m",
                OmniTraderFinanceData.TimeInterval.FiveMinute => "5m",
                OmniTraderFinanceData.TimeInterval.FifteenMinute => "15m",
                OmniTraderFinanceData.TimeInterval.ThirtyMinute => "30m",
                OmniTraderFinanceData.TimeInterval.OneHour => "1h",
                OmniTraderFinanceData.TimeInterval.FourHour => "4h",
                OmniTraderFinanceData.TimeInterval.OneDay => "1d",
                OmniTraderFinanceData.TimeInterval.OneWeek => "1w",
                _ => "1m"
            };
        }

        private static bool HasOpenPosition(DeploymentState deployment)
            => deployment.PositionSide != PositionSide.None && deployment.PositionQuantity > 0;

        private static void HandleLongSignal(DeploymentState deployment, TradeSignalEventArgs args)
        {
            lock (deployment.SyncRoot)
            {
                if (!deployment.LatestClosedCandle.HasValue || HasOpenPosition(deployment))
                    return;

                decimal executionPrice = deployment.LatestClosedCandle.Value.Close * (1 + deployment.Settings.SlippageFraction);
                decimal quoteToSpend = args.amountType == AmountType.Percentage
                    ? deployment.QuoteBalance * (args.inputAmount / 100m)
                    : args.inputAmount;

                if (quoteToSpend <= 0 || quoteToSpend > deployment.QuoteBalance)
                    return;

                decimal fee = quoteToSpend * deployment.Settings.FeeFraction;
                decimal netQuote = quoteToSpend - fee;
                decimal quantity = netQuote / executionPrice;

                deployment.QuoteBalance -= quoteToSpend;
                deployment.BaseBalance += quantity;
                deployment.TotalFees += fee;

                deployment.PositionSide = PositionSide.Long;
                deployment.PositionQuantity = quantity;
                deployment.PositionEntryPrice = executionPrice;
                deployment.PositionCost = quoteToSpend;
                deployment.PositionEntryFee = fee;
                deployment.PositionEntryTime = deployment.LatestClosedCandle.Value.Timestamp;
                deployment.StopLossPrice = args.StopLossPrice;
                deployment.TakeProfitPrice = args.TakeProfitPrice;
            }
        }

        private static void HandleShortSignal(DeploymentState deployment, TradeSignalEventArgs args)
        {
            lock (deployment.SyncRoot)
            {
                if (!deployment.LatestClosedCandle.HasValue || HasOpenPosition(deployment))
                    return;

                decimal executionPrice = deployment.LatestClosedCandle.Value.Close * (1 - deployment.Settings.SlippageFraction);
                decimal quoteNotional = args.amountType == AmountType.Percentage
                    ? deployment.QuoteBalance * (args.inputAmount / 100m)
                    : args.inputAmount;

                if (quoteNotional <= 0)
                    return;

                decimal quantityToShort = quoteNotional / executionPrice;
                decimal fee = quoteNotional * deployment.Settings.FeeFraction;
                decimal netProceeds = quoteNotional - fee;

                deployment.BaseBalance -= quantityToShort;
                deployment.QuoteBalance += netProceeds;
                deployment.TotalFees += fee;

                deployment.PositionSide = PositionSide.Short;
                deployment.PositionQuantity = quantityToShort;
                deployment.PositionEntryPrice = executionPrice;
                deployment.PositionCost = netProceeds;
                deployment.PositionEntryFee = fee;
                deployment.PositionEntryTime = deployment.LatestClosedCandle.Value.Timestamp;
                deployment.StopLossPrice = args.StopLossPrice;
                deployment.TakeProfitPrice = args.TakeProfitPrice;
            }
        }

        private static void HandleSellSignal(DeploymentState deployment, TradeSignalEventArgs args)
        {
            lock (deployment.SyncRoot)
            {
                if (!deployment.LatestClosedCandle.HasValue || !HasOpenPosition(deployment))
                    return;

                decimal qty = args.amountType == AmountType.Percentage
                    ? deployment.PositionQuantity * (args.inputAmount / 100m)
                    : Math.Min(args.inputAmount, deployment.PositionQuantity);

                if (qty <= 0)
                    return;

                if (deployment.PositionSide == PositionSide.Short)
                    CloseShort(deployment, deployment.LatestClosedCandle.Value, qty, deployment.LatestClosedCandle.Value.Close);
                else
                    CloseLong(deployment, deployment.LatestClosedCandle.Value, qty, deployment.LatestClosedCandle.Value.Close);
            }
        }

        private static void HandleClosePosition(DeploymentState deployment, TradePosition _)
        {
            lock (deployment.SyncRoot)
            {
                if (!deployment.LatestClosedCandle.HasValue || !HasOpenPosition(deployment))
                    return;

                if (deployment.PositionSide == PositionSide.Short)
                    CloseShort(deployment, deployment.LatestClosedCandle.Value, deployment.PositionQuantity, deployment.LatestClosedCandle.Value.Close);
                else
                    CloseLong(deployment, deployment.LatestClosedCandle.Value, deployment.PositionQuantity, deployment.LatestClosedCandle.Value.Close);
            }
        }

        private static void CheckStopLossTakeProfit(DeploymentState deployment, OmniTraderFinanceData.OHLCCandle candle)
        {
            bool slHit;
            bool tpHit;

            if (deployment.PositionSide == PositionSide.Short)
            {
                slHit = deployment.StopLossPrice.HasValue && candle.High >= deployment.StopLossPrice.Value;
                tpHit = deployment.TakeProfitPrice.HasValue && candle.Low <= deployment.TakeProfitPrice.Value;
            }
            else
            {
                slHit = deployment.StopLossPrice.HasValue && candle.Low <= deployment.StopLossPrice.Value;
                tpHit = deployment.TakeProfitPrice.HasValue && candle.High >= deployment.TakeProfitPrice.Value;
            }

            if (!slHit && !tpHit)
                return;

            if (slHit)
            {
                decimal fillPrice = deployment.PositionSide == PositionSide.Short
                    ? Math.Max(deployment.StopLossPrice!.Value, candle.Open)
                    : Math.Min(deployment.StopLossPrice!.Value, candle.Open);

                if (deployment.PositionSide == PositionSide.Short)
                    CloseShort(deployment, candle, deployment.PositionQuantity, fillPrice);
                else
                    CloseLong(deployment, candle, deployment.PositionQuantity, fillPrice);

                deployment.Strategy.NotifyStopLossTriggered(fillPrice);
            }
            else
            {
                decimal fillPrice = deployment.PositionSide == PositionSide.Short
                    ? Math.Min(deployment.TakeProfitPrice!.Value, candle.Open)
                    : Math.Max(deployment.TakeProfitPrice!.Value, candle.Open);

                if (deployment.PositionSide == PositionSide.Short)
                    CloseShort(deployment, candle, deployment.PositionQuantity, fillPrice);
                else
                    CloseLong(deployment, candle, deployment.PositionQuantity, fillPrice);

                deployment.Strategy.NotifyTakeProfitTriggered(fillPrice);
            }
        }

        private static void CloseLong(DeploymentState deployment, OmniTraderFinanceData.OHLCCandle candle, decimal qty, decimal fillPrice)
        {
            decimal executionPrice = fillPrice * (1 - deployment.Settings.SlippageFraction);
            decimal ratio = deployment.PositionQuantity == 0 ? 1 : Math.Clamp(qty / deployment.PositionQuantity, 0, 1);

            decimal entryCostShare = deployment.PositionCost * ratio;
            decimal entryFeeShare = deployment.PositionEntryFee * ratio;

            decimal grossProceeds = qty * executionPrice;
            decimal fee = grossProceeds * deployment.Settings.FeeFraction;
            decimal netProceeds = grossProceeds - fee;

            deployment.Trades.Add(new TradeRecord
            {
                IsShort = false,
                EntryTime = deployment.PositionEntryTime,
                EntryPrice = deployment.PositionEntryPrice,
                EntryQuantity = qty,
                EntryCost = entryCostShare,
                EntryFee = entryFeeShare,
                ExitTime = candle.Timestamp,
                ExitPrice = executionPrice,
                ExitProceeds = netProceeds,
                ExitFee = fee
            });

            deployment.BaseBalance -= qty;
            deployment.QuoteBalance += netProceeds;
            deployment.TotalFees += fee;

            deployment.PositionQuantity -= qty;
            deployment.PositionCost -= entryCostShare;
            deployment.PositionEntryFee -= entryFeeShare;

            if (deployment.PositionQuantity <= 0)
                ResetPosition(deployment);
        }

        private static void CloseShort(DeploymentState deployment, OmniTraderFinanceData.OHLCCandle candle, decimal qty, decimal fillPrice)
        {
            decimal executionPrice = fillPrice * (1 + deployment.Settings.SlippageFraction);
            decimal ratio = deployment.PositionQuantity == 0 ? 1 : Math.Clamp(qty / deployment.PositionQuantity, 0, 1);

            decimal entryCostShare = deployment.PositionCost * ratio;
            decimal entryFeeShare = deployment.PositionEntryFee * ratio;

            decimal grossCost = qty * executionPrice;
            decimal fee = grossCost * deployment.Settings.FeeFraction;
            decimal totalCoverCost = grossCost + fee;

            deployment.Trades.Add(new TradeRecord
            {
                IsShort = true,
                EntryTime = deployment.PositionEntryTime,
                EntryPrice = deployment.PositionEntryPrice,
                EntryQuantity = qty,
                EntryCost = entryCostShare,
                EntryFee = entryFeeShare,
                ExitTime = candle.Timestamp,
                ExitPrice = executionPrice,
                ExitProceeds = totalCoverCost,
                ExitFee = fee
            });

            deployment.BaseBalance += qty;
            deployment.QuoteBalance -= totalCoverCost;
            deployment.TotalFees += fee;

            deployment.PositionQuantity -= qty;
            deployment.PositionCost -= entryCostShare;
            deployment.PositionEntryFee -= entryFeeShare;

            if (deployment.PositionQuantity <= 0)
                ResetPosition(deployment);
        }

        private static void ResetPosition(DeploymentState deployment)
        {
            deployment.PositionSide = PositionSide.None;
            deployment.PositionQuantity = 0;
            deployment.PositionEntryPrice = 0;
            deployment.PositionCost = 0;
            deployment.PositionEntryFee = 0;
            deployment.PositionEntryTime = default;
            deployment.StopLossPrice = null;
            deployment.TakeProfitPrice = null;
        }

        private async Task<PersistedStrategyHistory> GetOrLoadStrategyHistory(string strategyName, string strategyKey)
        {
            if (historyByStrategyKey.TryGetValue(strategyKey, out var cached))
                return cached;

            await EnsureHistoryDirectoryExists();

            PersistedStrategyHistory history;
            string filePath = GetHistoryFilePath(strategyKey);
            try
            {
                var content = await parent.ExecuteServiceMethod<DataUtil>("ReadDataFromFile", filePath, false) as string;
                history = string.IsNullOrWhiteSpace(content)
                    ? new PersistedStrategyHistory()
                    : JsonConvert.DeserializeObject<PersistedStrategyHistory>(content) ?? new PersistedStrategyHistory();
            }
            catch
            {
                history = new PersistedStrategyHistory();
            }

            if (string.IsNullOrWhiteSpace(history.StrategyName))
                history.StrategyName = strategyName;
            if (string.IsNullOrWhiteSpace(history.StrategyKey))
                history.StrategyKey = strategyKey;

            history.AllTrades ??= [];
            history.Sessions ??= [];
            history.Backtests ??= [];

            historyByStrategyKey[strategyKey] = history;
            return history;
        }

        private async Task PersistHistory(PersistedStrategyHistory history)
        {
            await EnsureHistoryDirectoryExists();
            history.LastUpdatedUtc = DateTime.UtcNow;
            string filePath = GetHistoryFilePath(history.StrategyKey);
            string json = JsonConvert.SerializeObject(history, Formatting.Indented);
            await parent.ExecuteServiceMethod<DataUtil>("WriteToFile", filePath, json, true);
        }

        private async Task EnsureHistoryDirectoryExists()
        {
            string historyDirectory = GetHistoryDirectoryPath();
            await parent.ExecuteServiceMethod<DataUtil>("CreateDirectory", historyDirectory, true);
        }

        private async Task EnsureActiveDeploymentRegistryLoaded()
        {
            if (activeRegistryLoaded)
                return;

            await activeDeploymentRegistryLoadLock.WaitAsync();
            try
            {
                if (activeRegistryLoaded)
                    return;

                await EnsureHistoryDirectoryExists();
                string filePath = GetActiveDeploymentsFilePath();

                List<PersistedActiveDeployment> items = [];
                try
                {
                    var content = await parent.ExecuteServiceMethod<DataUtil>("ReadDataFromFile", filePath, false) as string;
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        items = JsonConvert.DeserializeObject<List<PersistedActiveDeployment>>(content) ?? [];
                    }
                }
                catch
                {
                    items = [];
                }

                activeDeploymentRegistry.Clear();
                foreach (var item in items)
                {
                    if (string.IsNullOrWhiteSpace(item.StrategyKey))
                        item.StrategyKey = GetStrategyKey(item.StrategyName);

                    activeDeploymentRegistry[item.StrategyKey] = item;
                }

                activeRegistryLoaded = true;
            }
            finally
            {
                activeDeploymentRegistryLoadLock.Release();
            }
        }

        private async Task UpsertActiveDeployment(PersistedActiveDeployment deployment)
        {
            await EnsureActiveDeploymentRegistryLoaded();
            activeDeploymentRegistry[deployment.StrategyKey] = deployment;
            await PersistActiveDeploymentRegistry();
        }

        private async Task RemoveActiveDeployment(string strategyKey)
        {
            await EnsureActiveDeploymentRegistryLoaded();
            activeDeploymentRegistry.TryRemove(strategyKey, out _);
            await PersistActiveDeploymentRegistry();
        }

        private async Task PersistActiveDeploymentRegistry()
        {
            await activeDeploymentRegistryWriteLock.WaitAsync();
            try
            {
                await EnsureHistoryDirectoryExists();
                string filePath = GetActiveDeploymentsFilePath();
                var items = activeDeploymentRegistry.Values.OrderBy(v => v.StrategyName).ToList();
                string json = JsonConvert.SerializeObject(items, Formatting.Indented);
                await parent.ExecuteServiceMethod<DataUtil>("WriteToFile", filePath, json, true);
            }
            finally
            {
                activeDeploymentRegistryWriteLock.Release();
            }
        }

        private static string GetStrategyKey(string strategyName)
        {
            string key = string.IsNullOrWhiteSpace(strategyName) ? "unnamed_strategy" : strategyName.Trim();
            foreach (char c in Path.GetInvalidFileNameChars())
                key = key.Replace(c, '_');
            return key.ToLowerInvariant();
        }

        private static TradeRecord CloneTrade(TradeRecord source)
        {
            return new TradeRecord
            {
                EntryTime = source.EntryTime,
                IsShort = source.IsShort,
                EntryPrice = source.EntryPrice,
                EntryQuantity = source.EntryQuantity,
                EntryCost = source.EntryCost,
                EntryFee = source.EntryFee,
                ExitTime = source.ExitTime,
                ExitPrice = source.ExitPrice,
                ExitProceeds = source.ExitProceeds,
                ExitFee = source.ExitFee
            };
        }

        private static void FinaliseAndMergeHistory(DeploymentState deployment)
        {
            var history = deployment.History;
            var session = deployment.SessionRecord;

            decimal markPrice = deployment.LatestClosedCandle?.Close ?? 0;
            session.EndTimeUtc = DateTime.UtcNow;
            session.CandlesProcessed = Math.Max(0, deployment.CandlesProcessed - history.TotalCandlesProcessed);
            session.NewTrades = Math.Max(0, deployment.Trades.Count - history.AllTrades.Count);
            session.FinalQuoteBalance = deployment.QuoteBalance;
            session.FinalBaseBalance = deployment.BaseBalance;
            session.FinalEquity = deployment.QuoteBalance + deployment.BaseBalance * markPrice;

            history.LastQuoteBalance = deployment.QuoteBalance;
            history.LastBaseBalance = deployment.BaseBalance;
            history.TotalFeesPaid = deployment.TotalFees;
            history.TotalCandlesProcessed = deployment.CandlesProcessed;
            history.AllTrades = deployment.Trades.Select(CloneTrade).ToList();
            history.Sessions.Add(session);
            history.LastUpdatedUtc = DateTime.UtcNow;
        }

        private static string GetHistoryDirectoryPath()
        {
            return OmniPaths.GetPath(Path.Combine(OmniPaths.GlobalPaths.OmniTraderDirectory, "SimulatorHistory"));
        }

        private static string GetHistoryFilePath(string strategyKey)
        {
            return Path.Combine(GetHistoryDirectoryPath(), $"{strategyKey}.json");
        }

        private static string GetActiveDeploymentsFilePath()
        {
            return Path.Combine(GetHistoryDirectoryPath(), "active_deployments.json");
        }

        private OmniBacktestResult BuildSnapshot(DeploymentState deployment, bool includeTrades = true)
        {
            lock (deployment.SyncRoot)
            {
                decimal markPrice = deployment.LatestClosedCandle?.Close ?? 0;
                decimal initialEquity = deployment.Settings.InitialQuoteBalance + deployment.Settings.InitialBaseBalance * markPrice;
                decimal finalEquity = deployment.QuoteBalance + deployment.BaseBalance * markPrice;

                var wins = deployment.Trades.Where(t => t.IsWin).ToList();
                var losses = deployment.Trades.Where(t => !t.IsWin).ToList();
                decimal totalWinAmount = wins.Sum(t => t.RealizedPnL);
                decimal totalLossAmount = losses.Sum(t => Math.Abs(t.RealizedPnL));

                return new OmniBacktestResult
                {
                    InitialEquity = initialEquity,
                    FinalEquity = finalEquity,
                    FinalQuoteBalance = deployment.QuoteBalance,
                    FinalBaseBalance = deployment.BaseBalance,
                    TotalTrades = deployment.Trades.Count,
                    WinningTrades = wins.Count,
                    LosingTrades = losses.Count,
                    TotalFeesPaid = deployment.TotalFees,
                    AverageWin = wins.Count > 0 ? totalWinAmount / wins.Count : 0,
                    AverageLoss = losses.Count > 0 ? totalLossAmount / losses.Count : 0,
                    LargestWin = wins.Count > 0 ? wins.Max(t => t.RealizedPnL) : 0,
                    LargestLoss = losses.Count > 0 ? losses.Min(t => t.RealizedPnL) : 0,
                    ProfitFactor = totalLossAmount == 0 ? (totalWinAmount > 0 ? decimal.MaxValue : 0) : totalWinAmount / totalLossAmount,
                    TotalCandles = deployment.CandlesProcessed,
                    StartTime = deployment.StartTimeUtc,
                    EndTime = deployment.EndTimeUtc ?? DateTime.UtcNow,
                    BacktestDuration = (deployment.EndTimeUtc ?? DateTime.UtcNow) - deployment.StartTimeUtc,
                    Trades = includeTrades ? [.. deployment.Trades] : []
                };
            }
        }

        private static OmniBacktestResult BuildSnapshotFromHistory(PersistedStrategyHistory history, bool includeTrades)
        {
            history.AllTrades ??= [];
            history.Sessions ??= [];

            decimal markPrice = history.AllTrades.Count > 0 ? history.AllTrades[^1].ExitPrice : 0;
            decimal initialEquity = history.InitialQuoteBalance + history.InitialBaseBalance * markPrice;
            decimal finalEquity = history.LastQuoteBalance + history.LastBaseBalance * markPrice;

            var wins = history.AllTrades.Where(t => t.IsWin).ToList();
            var losses = history.AllTrades.Where(t => !t.IsWin).ToList();
            decimal totalWinAmount = wins.Sum(t => t.RealizedPnL);
            decimal totalLossAmount = losses.Sum(t => Math.Abs(t.RealizedPnL));

            DateTime start = history.Sessions.Count > 0 ? history.Sessions.Min(s => s.StartTimeUtc) : default;
            DateTime end = history.Sessions.Count > 0 ? history.Sessions.Max(s => s.EndTimeUtc) : DateTime.UtcNow;

            return new OmniBacktestResult
            {
                InitialEquity = initialEquity,
                FinalEquity = finalEquity,
                FinalQuoteBalance = history.LastQuoteBalance,
                FinalBaseBalance = history.LastBaseBalance,
                TotalTrades = history.AllTrades.Count,
                WinningTrades = wins.Count,
                LosingTrades = losses.Count,
                TotalFeesPaid = history.TotalFeesPaid,
                AverageWin = wins.Count > 0 ? totalWinAmount / wins.Count : 0,
                AverageLoss = losses.Count > 0 ? totalLossAmount / losses.Count : 0,
                LargestWin = wins.Count > 0 ? wins.Max(t => t.RealizedPnL) : 0,
                LargestLoss = losses.Count > 0 ? losses.Min(t => t.RealizedPnL) : 0,
                ProfitFactor = totalLossAmount == 0 ? (totalWinAmount > 0 ? decimal.MaxValue : 0) : totalWinAmount / totalLossAmount,
                TotalCandles = history.TotalCandlesProcessed,
                StartTime = start,
                EndTime = end,
                BacktestDuration = end - start,
                Trades = includeTrades ? history.AllTrades.Select(CloneTrade).ToList() : []
            };
        }

        private static void UnwireStrategyHandlers(DeploymentState deployment)
        {
            if (deployment.LongHandler != null)
                deployment.Strategy.OnLong -= deployment.LongHandler;
            if (deployment.ShortHandler != null)
                deployment.Strategy.OnShort -= deployment.ShortHandler;
            if (deployment.SellHandler != null)
                deployment.Strategy.OnSell -= deployment.SellHandler;
            if (deployment.CloseHandler != null)
                deployment.Strategy.ClosePosition -= deployment.CloseHandler;
        }
    }
}
