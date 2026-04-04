using Newtonsoft.Json;
using Omnipotent.Profiles;
using Omnipotent.Service_Manager;
using Omnipotent.Services.OmniTrader.Backtesting;
using Omnipotent.Services.OmniTrader.Data;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.OmniTrader
{
    public class OmniTrader : OmniService
    {
        public OmniTraderFinanceData data;
        public OmniTraderSimulator simulator;

        private readonly Dictionary<Guid, string> deploymentStrategyNames = new();

        public OmniTrader()
        {
            name = "OmniTrader";
            threadAnteriority = ThreadAnteriority.Critical;
        }

        protected override void ServiceMain()
        {
            data = new OmniTraderFinanceData(this);
            simulator = new OmniTraderSimulator(this);

            _ = InitialiseRoutesAndAutoRedeploy();
        }

        private async Task InitialiseRoutesAndAutoRedeploy()
        {
            try
            {
                await CreateRoutes();
                await RestorePersistedDeployments();
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Failed to initialise OmniTrader routes and auto-redeploy.");
            }
        }

        public async Task<Guid> DeployStrategy(
            OmniTraderStrategy strategy,
            string symbol,
            OmniTraderFinanceData.TimeInterval interval,
            BacktestSettings? settings = null,
            CancellationToken cancellationToken = default)
        {
            Guid deploymentId = await simulator.Deploy(strategy, symbol, interval, settings, cancellationToken);
            lock (deploymentStrategyNames)
            {
                deploymentStrategyNames[deploymentId] = strategy.Name;
            }
            return deploymentId;
        }

        public async Task<bool> UndeployStrategy(Guid deploymentId)
        {
            bool removed = await simulator.Undeploy(deploymentId);
            if (removed)
            {
                lock (deploymentStrategyNames)
                {
                    deploymentStrategyNames.Remove(deploymentId);
                }
            }
            return removed;
        }

        public Task UndeployAllStrategies()
        {
            lock (deploymentStrategyNames)
            {
                deploymentStrategyNames.Clear();
            }
            return simulator.UndeployAll();
        }

        public IReadOnlyCollection<Guid> GetDeployedStrategyIds()
        {
            return simulator.GetDeploymentIds();
        }

        public OmniBacktestResult GetStrategyAnalytics(Guid deploymentId)
        {
            return simulator.GetSnapshot(deploymentId);
        }

        public Dictionary<Guid, OmniBacktestResult> GetAllStrategyAnalytics()
        {
            return simulator.GetAllSnapshots();
        }

        public Task<OmniBacktestResult> GetPersistedStrategyAnalytics(string strategyName)
        {
            return simulator.GetPersistedStrategySnapshot(strategyName);
        }

        public Task<Dictionary<string, OmniBacktestResult>> GetAllPersistedStrategyAnalytics()
        {
            return simulator.GetAllPersistedStrategySnapshots();
        }

        public async Task WriteBacktestResultToDesktop(OmniBacktestResult result)
        {
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string filePath = Path.Combine(desktopPath, $"BacktestResult_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            await File.WriteAllTextAsync(filePath, result.ToString());
        }

        private async Task RestorePersistedDeployments()
        {
            var deploymentsToRestore = await simulator.GetPersistedActiveDeployments();
            foreach (var registration in deploymentsToRestore)
            {
                try
                {
                    OmniTraderStrategy strategy = CreateStrategyInstance(registration.StrategyName);
                    await DeployStrategy(strategy, registration.Symbol, registration.Interval, registration.Settings);
                    await ServiceLog($"Auto-redeployed strategy '{registration.StrategyName}' on {registration.Symbol} {registration.Interval}.");
                }
                catch (Exception ex)
                {
                    await ServiceLogError(ex, $"Failed to auto-redeploy strategy '{registration.StrategyName}'.");
                }
            }
        }

        private static Dictionary<string, Type> GetStrategyTypeMap()
        {
            var map = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            var strategyTypes = Assembly.GetExecutingAssembly()
                .GetTypes()
                .Where(t => !t.IsAbstract
                            && typeof(OmniTraderStrategy).IsAssignableFrom(t)
                            && t.GetConstructor(Type.EmptyTypes) != null);

            foreach (var type in strategyTypes)
            {
                var strategy = (OmniTraderStrategy)Activator.CreateInstance(type)!;
                if (!string.IsNullOrWhiteSpace(strategy.Name))
                    map[strategy.Name] = type;

                map[type.Name] = type;
            }

            return map;
        }

        private static OmniTraderStrategy CreateStrategyInstance(string strategyName)
        {
            var strategyMap = GetStrategyTypeMap();
            if (!strategyMap.TryGetValue(strategyName, out var strategyType))
                throw new InvalidOperationException($"Strategy '{strategyName}' could not be found.");

            return (OmniTraderStrategy)Activator.CreateInstance(strategyType)!;
        }

        private async Task CreateRoutes()
        {

            await CreateAPIRoute("/omniTrader/status", async (req) =>
            {
                var summary = new
                {
                    Service = "OmniTrader",
                    DeployedCount = GetDeployedStrategyIds().Count,
                    ActiveDeploymentIds = GetDeployedStrategyIds(),
                    Uptime = GetServiceUptime().ToString(),
                    ManagerUptime = GetManagerUptime().ToString()
                };
                await req.ReturnResponse(JsonConvert.SerializeObject(summary));
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Guest);

            await CreateAPIRoute("/omniTrader/strategies/available", async (req) =>
            {
                var map = GetStrategyTypeMap();
                var available = map
                    .GroupBy(k => k.Value)
                    .Select(g =>
                    {
                        var strategy = (OmniTraderStrategy)Activator.CreateInstance(g.Key)!;
                        return new
                        {
                            StrategyName = strategy.Name,
                            ClassName = g.Key.Name,
                            strategy.Description
                        };
                    })
                    .OrderBy(x => x.StrategyName)
                    .ToList();

                await req.ReturnResponse(JsonConvert.SerializeObject(available));
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Guest);

            await CreateAPIRoute("/omniTrader/strategies/deployed", async (req) =>
            {
                var analytics = GetAllStrategyAnalytics();
                var deployed = analytics.Select(k => new
                {
                    DeploymentId = k.Key,
                    StrategyName = GetTrackedStrategyName(k.Key),
                    k.Value.FinalEquity,
                    k.Value.TotalTrades,
                    k.Value.TotalPnLPercent,
                    k.Value.WinRate,
                    k.Value.TotalFeesPaid
                }).ToList();

                await req.ReturnResponse(JsonConvert.SerializeObject(deployed));
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Guest);

            await CreateAPIRoute("/omniTrader/simulator/active-persistent", async (req) =>
            {
                var registrations = await simulator.GetPersistedActiveDeployments();
                await req.ReturnResponse(JsonConvert.SerializeObject(registrations));
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Guest);

            await CreateAPIRoute("/omniTrader/analytics/live/all", async (req) =>
            {
                var analytics = GetAllStrategyAnalytics();
                await req.ReturnResponse(JsonConvert.SerializeObject(analytics));
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Guest);

            await CreateAPIRoute("/omniTrader/analytics/live/byDeployment", async (req) =>
            {
                if (!Guid.TryParse(req.userParameters.Get("deploymentId"), out var deploymentId))
                {
                    await req.ReturnResponse("Invalid or missing deploymentId", code: HttpStatusCode.BadRequest);
                    return;
                }

                var result = GetStrategyAnalytics(deploymentId);
                await req.ReturnResponse(JsonConvert.SerializeObject(result));
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Guest);

            await CreateAPIRoute("/omniTrader/analytics/persisted/all", async (req) =>
            {
                var analytics = await GetAllPersistedStrategyAnalytics();
                await req.ReturnResponse(JsonConvert.SerializeObject(analytics));
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Guest);

            await CreateAPIRoute("/omniTrader/analytics/persisted/byStrategy", async (req) =>
            {
                string strategyName = req.userParameters.Get("strategyName") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(strategyName))
                {
                    await req.ReturnResponse("Missing strategyName", code: HttpStatusCode.BadRequest);
                    return;
                }

                var analytics = await GetPersistedStrategyAnalytics(strategyName);
                await req.ReturnResponse(JsonConvert.SerializeObject(analytics));
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Guest);

            await CreateAPIRoute("/omniTrader/analytics/strategyInsight", async (req) =>
            {
                string strategyName = req.userParameters.Get("strategyName") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(strategyName))
                {
                    await req.ReturnResponse("Missing strategyName", code: HttpStatusCode.BadRequest);
                    return;
                }

                var insight = await simulator.GetStrategyInsight(strategyName);
                await req.ReturnResponse(JsonConvert.SerializeObject(insight));
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Guest);

            await CreateAPIRoute("/omniTrader/backtest/run", async (req) =>
            {
                string strategyName = req.userParameters.Get("strategyName") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(strategyName))
                {
                    await req.ReturnResponse("Missing strategyName", code: HttpStatusCode.BadRequest);
                    return;
                }

                string coin = req.userParameters.Get("coin") ?? "BTC";
                string currency = req.userParameters.Get("currency") ?? "USD";
                int candleCount = ParseInt(req.userParameters.Get("candles"), 500);
                var interval = ParseInterval(req.userParameters.Get("interval"), OmniTraderFinanceData.TimeInterval.OneHour);

                var settings = new BacktestSettings
                {
                    InitialQuoteBalance = ParseDecimal(req.userParameters.Get("initialQuote"), 10_000m),
                    InitialBaseBalance = ParseDecimal(req.userParameters.Get("initialBase"), 0m),
                    FeeFraction = ParseDecimal(req.userParameters.Get("feeFraction"), 0.001m),
                    SlippageFraction = ParseDecimal(req.userParameters.Get("slippageFraction"), 0.0005m)
                };

                OmniTraderStrategy strategy = CreateStrategyInstance(strategyName);
                await strategy.PrepareForSession(this, TradeSessionType.Backtester);
                var result = await simulator.RunBacktestAndPersist(strategy, coin, currency, interval, candleCount, settings);

                await req.ReturnResponse(JsonConvert.SerializeObject(new
                {
                    strategyName,
                    coin,
                    currency,
                    interval,
                    candleCount,
                    result
                }));
            }, HttpMethod.Post, KMProfileManager.KMPermissions.Guest);

            await CreateAPIRoute("/omniTrader/simulator/deploy", async (req) =>
            {
                string strategyName = req.userParameters.Get("strategyName") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(strategyName))
                {
                    await req.ReturnResponse("Missing strategyName", code: HttpStatusCode.BadRequest);
                    return;
                }

                string symbol = req.userParameters.Get("symbol") ?? "BTCUSDT";
                var interval = ParseInterval(req.userParameters.Get("interval"), OmniTraderFinanceData.TimeInterval.OneMinute);

                var settings = new BacktestSettings
                {
                    InitialQuoteBalance = ParseDecimal(req.userParameters.Get("initialQuote"), 10_000m),
                    InitialBaseBalance = ParseDecimal(req.userParameters.Get("initialBase"), 0m),
                    FeeFraction = ParseDecimal(req.userParameters.Get("feeFraction"), 0.001m),
                    SlippageFraction = ParseDecimal(req.userParameters.Get("slippageFraction"), 0.0005m)
                };

                OmniTraderStrategy strategy = CreateStrategyInstance(strategyName);
                Guid deploymentId = await DeployStrategy(strategy, symbol, interval, settings);

                await req.ReturnResponse(JsonConvert.SerializeObject(new
                {
                    Message = "Strategy deployed",
                    DeploymentId = deploymentId,
                    StrategyName = strategyName,
                    Symbol = symbol,
                    Interval = interval
                }));
            }, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);

            await CreateAPIRoute("/omniTrader/simulator/undeploy", async (req) =>
            {
                if (!Guid.TryParse(req.userParameters.Get("deploymentId"), out var deploymentId))
                {
                    await req.ReturnResponse("Invalid or missing deploymentId", code: HttpStatusCode.BadRequest);
                    return;
                }

                bool result = await UndeployStrategy(deploymentId);
                await req.ReturnResponse(JsonConvert.SerializeObject(new
                {
                    Message = result ? "Strategy undeployed" : "Deployment not found",
                    DeploymentId = deploymentId,
                    Success = result
                }), code: result ? HttpStatusCode.OK : HttpStatusCode.NotFound);
            }, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);

            await CreateAPIRoute("/omniTrader/simulator/undeployAll", async (req) =>
            {
                await UndeployAllStrategies();
                await req.ReturnResponse(JsonConvert.SerializeObject(new
                {
                    Message = "All strategies undeployed"
                }));
            }, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);
        }

        private string GetTrackedStrategyName(Guid deploymentId)
        {
            lock (deploymentStrategyNames)
            {
                return deploymentStrategyNames.TryGetValue(deploymentId, out var name) ? name : "Unknown";
            }
        }

        private static int ParseInt(string? value, int defaultValue)
        {
            return int.TryParse(value, out var parsed) ? parsed : defaultValue;
        }

        private static decimal ParseDecimal(string? value, decimal defaultValue)
        {
            return decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue;
        }

        private static OmniTraderFinanceData.TimeInterval ParseInterval(string? raw, OmniTraderFinanceData.TimeInterval fallback)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return fallback;

            if (Enum.TryParse<OmniTraderFinanceData.TimeInterval>(raw, true, out var byName))
                return byName;

            if (int.TryParse(raw, out var byValue) && Enum.IsDefined(typeof(OmniTraderFinanceData.TimeInterval), byValue))
                return (OmniTraderFinanceData.TimeInterval)byValue;

            return fallback;
        }
    }
}
