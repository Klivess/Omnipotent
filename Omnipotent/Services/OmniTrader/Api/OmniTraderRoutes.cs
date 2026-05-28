using Newtonsoft.Json;
using Omnipotent.Profiles;
using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Strategy.Strategies;
using System.Globalization;
using System.Net;

namespace Omnipotent.Services.OmniTrader.Api
{
    public sealed class OmniTraderRoutes
    {
        private readonly OmniTrader parent;

        public OmniTraderRoutes(OmniTrader parent)
        {
            this.parent = parent;
        }

        public async Task RegisterAsync()
        {
            await parent.CreateAPIRoute("/api/omnitrader/status", async req =>
            {
                var payload = new
                {
                    Service = "OmniTrader",
                    DbPath = parent.GetDbPath(),
                    DeployedCount = parent.SessionManager.ActiveDeploymentIds.Count,
                    ActiveDeploymentIds = parent.SessionManager.ActiveDeploymentIds,
                    Uptime = parent.GetServiceUptime().ToString(),
                    KrakenConfigured = parent.IsKrakenConfigured
                };
                await req.ReturnResponse(JsonConvert.SerializeObject(payload));
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Guest);

            await parent.CreateAPIRoute("/api/omnitrader/strategies", async req =>
            {
                var items = parent.StrategyRegistry.All
                    .Select(d => new { d.Name, d.ClassName, d.Description })
                    .OrderBy(x => x.Name)
                    .ToList();
                await req.ReturnResponse(JsonConvert.SerializeObject(items));
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Guest);

            await parent.CreateAPIRoute("/api/omnitrader/deployments", async req =>
            {
                var deployments = await parent.DeploymentRepo.ListAllAsync();
                var dtos = deployments.Select(d => new
                {
                    d.Id,
                    d.StrategyClass,
                    d.Config.Symbol,
                    Interval = d.Config.Interval.ToString(),
                    Mode = d.Mode.ToString(),
                    Status = d.Status.ToString(),
                    Armed = parent.SessionManager.TryGetLive(d.Id, out var live) && (live?.IsArmed ?? false),
                    d.EquityInitial,
                    d.EquityCurrent,
                    PnLPercent = d.EquityInitial == 0 ? 0 : (d.EquityCurrent - d.EquityInitial) / d.EquityInitial * 100m,
                    d.CreatedUtc,
                    d.ArmedLiveUtc,
                    d.PausedUtc,
                    d.Error
                }).ToList();
                await req.ReturnResponse(JsonConvert.SerializeObject(dtos));
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Guest);

            await parent.CreateAPIRoute("/api/omnitrader/deployment", async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await req.ReturnResponse("Missing id", code: HttpStatusCode.BadRequest); return; }
                var row = await parent.DeploymentRepo.GetAsync(id);
                if (row == null) { await req.ReturnResponse("Not found", code: HttpStatusCode.NotFound); return; }
                var orders = await parent.OrderRepo.ListByDeploymentAsync(id, 100);
                var fills = await parent.FillRepo.ListByDeploymentAsync(id, 200);
                await req.ReturnResponse(JsonConvert.SerializeObject(new
                {
                    Deployment = new
                    {
                        row.Id,
                        row.StrategyClass,
                        row.Config,
                        Mode = row.Mode.ToString(),
                        Status = row.Status.ToString(),
                        row.EquityInitial,
                        row.EquityCurrent,
                        row.CreatedUtc,
                        row.ArmedLiveUtc,
                        row.PausedUtc,
                        row.Error
                    },
                    Orders = orders,
                    Fills = fills
                }));
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Guest);

            await parent.CreateAPIRoute("/api/omnitrader/deployment/equity", async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await req.ReturnResponse("Missing id", code: HttpStatusCode.BadRequest); return; }
                DateTime? from = ParseUtc(req.userParameters.Get("from"));
                DateTime? to = ParseUtc(req.userParameters.Get("to"));
                var series = await parent.EquityRepo.GetSeriesAsync(id, from, to);
                await req.ReturnResponse(JsonConvert.SerializeObject(series));
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Guest);

            await parent.CreateAPIRoute("/api/omnitrader/deployment/create", async req =>
            {
                try
                {
                    var dto = JsonConvert.DeserializeObject<CreateDeploymentDto>(req.userMessageContent ?? "")
                        ?? throw new Exception("Invalid body");
                    var config = dto.ToDeploymentConfig();
                    string id = await parent.SessionManager.CreateDeploymentAsync(config);
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { Id = id, Mode = config.Mode.ToString() }));
                }
                catch (Exception ex)
                {
                    await parent.ServiceLogError(ex, "create-deployment failed");
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { Error = ex.Message }), code: HttpStatusCode.BadRequest);
                }
            }, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);

            await parent.CreateAPIRoute("/api/omnitrader/deployment/arm-live", async req =>
            {
                string? id = req.userParameters.Get("id");
                string? token = req.userParameters.Get("confirm");
                if (string.IsNullOrWhiteSpace(id) || token != id)
                {
                    await req.ReturnResponse("confirm token must equal id", code: HttpStatusCode.BadRequest);
                    return;
                }
                bool ok = await parent.SessionManager.ArmLiveAsync(id);
                await req.ReturnResponse(JsonConvert.SerializeObject(new { Armed = ok }), code: ok ? HttpStatusCode.OK : HttpStatusCode.NotFound);
            }, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);

            await parent.CreateAPIRoute("/api/omnitrader/deployment/pause", async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await req.ReturnResponse("Missing id", code: HttpStatusCode.BadRequest); return; }
                bool ok = await parent.SessionManager.PauseAsync(id);
                await req.ReturnResponse(JsonConvert.SerializeObject(new { Paused = ok }), code: ok ? HttpStatusCode.OK : HttpStatusCode.NotFound);
            }, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);

            await parent.CreateAPIRoute("/api/omnitrader/deployment/resume", async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await req.ReturnResponse("Missing id", code: HttpStatusCode.BadRequest); return; }
                bool ok = await parent.SessionManager.ResumeAsync(id);
                await req.ReturnResponse(JsonConvert.SerializeObject(new { Resumed = ok }), code: ok ? HttpStatusCode.OK : HttpStatusCode.NotFound);
            }, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);

            await parent.CreateAPIRoute("/api/omnitrader/deployment/kill", async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await req.ReturnResponse("Missing id", code: HttpStatusCode.BadRequest); return; }
                bool ok = await parent.SessionManager.KillAsync(id);
                await req.ReturnResponse(JsonConvert.SerializeObject(new { Killed = ok }), code: ok ? HttpStatusCode.OK : HttpStatusCode.NotFound);
            }, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);

            await parent.CreateAPIRoute("/api/omnitrader/deployment/delete", async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await req.ReturnResponse("Missing id", code: HttpStatusCode.BadRequest); return; }
                await parent.SessionManager.DeleteAsync(id);
                await req.ReturnResponse(JsonConvert.SerializeObject(new { Deleted = true }));
            }, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);

            await parent.CreateAPIRoute("/api/omnitrader/backtests", async req =>
            {
                var jobs = await parent.BacktestJobRepo.ListRecentAsync(50);
                var dtos = jobs.Select(j => new
                {
                    j.Id,
                    j.StrategyClass,
                    j.Config.Coin,
                    j.Config.Currency,
                    Interval = j.Config.Interval.ToString(),
                    j.Config.CandleCount,
                    Status = j.Status.ToString(),
                    j.ProgressPct,
                    j.CandlesTotal,
                    j.CandlesDone,
                    j.QueuedUtc,
                    j.StartedUtc,
                    j.FinishedUtc,
                    j.Error
                }).ToList();
                await req.ReturnResponse(JsonConvert.SerializeObject(dtos));
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Guest);

            await parent.CreateAPIRoute("/api/omnitrader/backtest", async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await req.ReturnResponse("Missing id", code: HttpStatusCode.BadRequest); return; }
                var job = await parent.BacktestJobRepo.GetAsync(id);
                if (job == null) { await req.ReturnResponse("Not found", code: HttpStatusCode.NotFound); return; }
                await req.ReturnResponse(JsonConvert.SerializeObject(new
                {
                    job.Id,
                    job.StrategyClass,
                    job.Config,
                    Status = job.Status.ToString(),
                    job.ProgressPct,
                    job.CandlesTotal,
                    job.CandlesDone,
                    job.QueuedUtc,
                    job.StartedUtc,
                    job.FinishedUtc,
                    job.CancellationRequested,
                    job.Error,
                    Result = job.Result
                }));
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Guest);

            await parent.CreateAPIRoute("/api/omnitrader/backtest/create", async req =>
            {
                try
                {
                    var dto = JsonConvert.DeserializeObject<CreateBacktestDto>(req.userMessageContent ?? "")
                        ?? throw new Exception("Invalid body");
                    string id = await parent.BacktestQueue.EnqueueAsync(dto.ToBacktestConfig());
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { JobId = id }));
                }
                catch (Exception ex)
                {
                    await parent.ServiceLogError(ex, "backtest enqueue failed");
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { Error = ex.Message }), code: HttpStatusCode.BadRequest);
                }
            }, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);

            await parent.CreateAPIRoute("/api/omnitrader/backtest/cancel", async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await req.ReturnResponse("Missing id", code: HttpStatusCode.BadRequest); return; }
                await parent.BacktestJobRepo.RequestCancelAsync(id);
                await req.ReturnResponse(JsonConvert.SerializeObject(new { Requested = true }));
            }, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);

            await parent.CreateAPIRoute("/api/omnitrader/signals/flowsignal", async req =>
            {
                try
                {
                    var payload = System.Text.Json.JsonSerializer.Deserialize<FlowSignalTraderStrategy.FlowSignalPayload>(req.userMessageContent ?? "");
                    if (payload == null || string.IsNullOrWhiteSpace(payload.Symbol))
                    {
                        await req.ReturnResponse("Bad payload", code: HttpStatusCode.BadRequest);
                        return;
                    }
                    var subscribers = FlowSignalTraderStrategy.Subscribers(payload.Symbol);
                    int delivered = 0;
                    foreach (var sub in subscribers)
                    {
                        await sub.HandleSignalAsync(payload);
                        delivered++;
                    }
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { Delivered = delivered }));
                }
                catch (Exception ex)
                {
                    await parent.ServiceLogError(ex, "flowsignal webhook failed");
                    await req.ReturnResponse(JsonConvert.SerializeObject(new { Error = ex.Message }), code: HttpStatusCode.InternalServerError);
                }
            }, HttpMethod.Post, KMProfileManager.KMPermissions.Klives);
        }

        private static DateTime? ParseUtc(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                return dt;
            return null;
        }
    }

    public sealed class CreateDeploymentDto
    {
        public string StrategyClass { get; set; } = "";
        public string Symbol { get; set; } = "";
        public string Interval { get; set; } = "OneHour";
        public string Mode { get; set; } = "Paper";
        public decimal InitialQuoteBalance { get; set; } = 10_000m;
        public decimal InitialBaseBalance { get; set; } = 0m;
        public decimal FeeFraction { get; set; } = 0.001m;
        public decimal SlippageFraction { get; set; } = 0.0005m;
        public decimal? MaxPositionQuoteUsd { get; set; }
        public decimal? MaxDailyLossUsd { get; set; }
        public int? MaxOrdersPerHour { get; set; }
        public List<string>? AllowedSymbols { get; set; }

        public DeploymentConfig ToDeploymentConfig()
        {
            var mode = Enum.Parse<SessionMode>(Mode, ignoreCase: true);
            var interval = Enum.Parse<TimeInterval>(Interval, ignoreCase: true);
            RiskCaps? caps = null;
            if (mode == SessionMode.Live)
            {
                caps = new RiskCaps
                {
                    MaxPositionQuoteUsd = MaxPositionQuoteUsd ?? 100m,
                    MaxDailyLossUsd = MaxDailyLossUsd ?? 50m,
                    MaxOrdersPerHour = MaxOrdersPerHour ?? 30,
                    AllowedSymbols = new HashSet<string>(AllowedSymbols ?? new List<string> { Symbol }, StringComparer.OrdinalIgnoreCase)
                };
            }
            return new DeploymentConfig
            {
                StrategyClass = StrategyClass,
                Symbol = Symbol,
                Interval = interval,
                Mode = mode,
                InitialQuoteBalance = InitialQuoteBalance,
                InitialBaseBalance = InitialBaseBalance,
                FeeFraction = FeeFraction,
                SlippageFraction = SlippageFraction,
                Caps = caps
            };
        }
    }

    public sealed class CreateBacktestDto
    {
        public string StrategyClass { get; set; } = "";
        public string Coin { get; set; } = "BTC";
        public string Currency { get; set; } = "USD";
        public string Interval { get; set; } = "OneHour";
        public int CandleCount { get; set; } = 500;
        public decimal InitialQuoteBalance { get; set; } = 10_000m;
        public decimal InitialBaseBalance { get; set; } = 0m;
        public decimal FeeFraction { get; set; } = 0.001m;
        public decimal SlippageFraction { get; set; } = 0.0005m;

        public BacktestConfig ToBacktestConfig() => new BacktestConfig
        {
            StrategyClass = StrategyClass,
            Coin = Coin,
            Currency = Currency,
            Interval = Enum.Parse<TimeInterval>(Interval, ignoreCase: true),
            CandleCount = CandleCount,
            InitialQuoteBalance = InitialQuoteBalance,
            InitialBaseBalance = InitialBaseBalance,
            FeeFraction = FeeFraction,
            SlippageFraction = SlippageFraction
        };
    }
}
