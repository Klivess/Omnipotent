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
                    .Select(d => new { d.Name, d.ClassName, d.Description, d.RequiresUniverse, d.Parameters })
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
                    Armed = parent.SessionManager.IsDeploymentArmed(d.Id),
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

            // Total account value over time = the summed equity of all deployments of a given mode
            // (default Live = the Kraken account), forward-filled so each deployment contributes its
            // last-known equity from when it first came online.
            await parent.CreateAPIRoute("/api/omnitrader/portfolio/equity", async req =>
            {
                string mode = req.userParameters.Get("mode") ?? "Live";
                var all = await parent.DeploymentRepo.ListAllAsync();
                var selected = all.Where(d => string.Equals(d.Mode.ToString(), mode, StringComparison.OrdinalIgnoreCase)).ToList();

                var serieses = new List<List<EquityPoint>>();
                foreach (var d in selected)
                {
                    var s = await parent.EquityRepo.GetSeriesAsync(d.Id);
                    if (s.Count > 0) serieses.Add(s);
                }

                var total = MergeEquity(serieses);
                await req.ReturnResponse(JsonConvert.SerializeObject(new
                {
                    Mode = mode,
                    Deployments = serieses.Count,
                    Series = total.Select(p => new { Ts = p.Ts, Equity = p.Equity })
                }));
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Guest);

            await parent.CreateAPIRoute("/api/omnitrader/deployment/chart", async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await req.ReturnResponse("Missing id", code: HttpStatusCode.BadRequest); return; }
                var row = await parent.DeploymentRepo.GetAsync(id);
                if (row == null) { await req.ReturnResponse("Not found", code: HttpStatusCode.NotFound); return; }

                int limit = 300;
                if (int.TryParse(req.userParameters.Get("limit"), out var l)) limit = Math.Clamp(l, 50, 1000);

                string symbol = row.Config.Symbol;
                var interval = row.Config.Interval;

                // Candles are cache-first (the live/paper stream upserts each candle into candle_cache),
                // so repeated polling is a fast DB read once the window is warm.
                IReadOnlyList<OHLCCandle> candles;
                try { candles = await parent.MarketData.GetHistoricalCandlesAsync(symbol, interval, limit); }
                catch { candles = Array.Empty<OHLCCandle>(); }

                // Markers from this deployment's fills, joined to their order for side. Walk the fills
                // chronologically tracking net position so each is classified as an entry or an exit
                // (opposing an open position reduces/closes it -> exit; otherwise it opens/adds -> entry).
                var orders = await parent.OrderRepo.ListByDeploymentAsync(id, 1000);
                var sideById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var o in orders) sideById[o.Id] = o.Side;

                var fills = await parent.FillRepo.ListByDeploymentAsync(id, 1000);
                fills.Sort((a, b) => a.FilledUtc.CompareTo(b.FilledUtc));

                var markers = new List<object>();
                decimal pos = 0m;
                foreach (var f in fills)
                {
                    if (!sideById.TryGetValue(f.OrderId, out var side)) continue;
                    bool isBuy = string.Equals(side, "buy", StringComparison.OrdinalIgnoreCase);
                    decimal prev = pos;
                    pos += isBuy ? f.Qty : -f.Qty;
                    bool opposing = prev != 0m && (isBuy ? prev < 0m : prev > 0m);
                    markers.Add(new
                    {
                        Time = f.FilledUtc.ToString("o"),
                        f.Price,
                        Side = isBuy ? "buy" : "sell",
                        Kind = opposing ? "exit" : "entry"
                    });
                }

                await req.ReturnResponse(JsonConvert.SerializeObject(new
                {
                    Symbol = symbol,
                    Interval = interval.ToString(),
                    Candles = candles.Select(c => new { c.Timestamp, c.Open, c.High, c.Low, c.Close }),
                    Markers = markers
                }));
            }, HttpMethod.Get, KMProfileManager.KMPermissions.Guest);

            await parent.CreateAPIRoute("/api/omnitrader/deployment/ticks", async req =>
            {
                string? id = req.userParameters.Get("id");
                if (string.IsNullOrWhiteSpace(id)) { await req.ReturnResponse("Missing id", code: HttpStatusCode.BadRequest); return; }
                var row = await parent.DeploymentRepo.GetAsync(id);
                if (row == null) { await req.ReturnResponse(JsonConvert.SerializeObject(new { Price = 0m, Forming = (object?)null })); return; }

                // The forming candle is pure market data — the live price of the deployment's chart
                // symbol, bucketed at its interval. This works identically for paper, live and
                // multi-asset sessions; no per-strategy session plumbing is involved.
                string symbol = row.Config.Symbol;
                var interval = row.Config.Interval;

                decimal price = 0m;
                try { price = await parent.MarketData.GetLatestPriceAsync(symbol); } catch { }

                object? forming = null;
                if (price > 0m)
                {
                    var bucket = BucketStart(DateTime.UtcNow, interval);
                    decimal open = price, high = price, low = price;
                    try
                    {
                        // Merge with the current bucket's cached candle so the bar keeps its open/high/low.
                        var recent = await parent.MarketData.GetHistoricalCandlesAsync(symbol, interval, 1);
                        if (recent.Count > 0 && recent[^1].Timestamp == bucket)
                        {
                            open = recent[^1].Open;
                            high = Math.Max(recent[^1].High, price);
                            low = Math.Min(recent[^1].Low, price);
                        }
                    }
                    catch { }
                    forming = new { Timestamp = bucket, Open = open, High = high, Low = low, Close = price };
                }

                await req.ReturnResponse(JsonConvert.SerializeObject(new { Price = price, Forming = forming }));
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
                    j.Error,
                    // At-a-glance summary metrics (null until the job has a result).
                    TotalPnLPercent = j.Result?.TotalPnLPercent,
                    WinRate = j.Result?.WinRate,
                    SharpeRatio = j.Result?.SharpeRatio,
                    MaxDrawdownPercent = j.Result?.MaxDrawdownPercent,
                    TotalTrades = j.Result?.TotalTrades
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

        // Forward-fill merge of several equity series into a single total-value series. At each
        // distinct timestamp, every series contributes its last-known equity (0 before it starts).
        // Downsamples to keep the payload light.
        private static List<(DateTime Ts, decimal Equity)> MergeEquity(List<List<EquityPoint>> serieses)
        {
            var result = new List<(DateTime, decimal)>();
            if (serieses.Count == 0) return result;

            var times = serieses.SelectMany(s => s.Select(p => p.Ts)).Distinct().OrderBy(t => t).ToList();
            var idx = new int[serieses.Count];
            var last = new decimal[serieses.Count];
            var started = new bool[serieses.Count];

            foreach (var t in times)
            {
                decimal sum = 0m;
                for (int i = 0; i < serieses.Count; i++)
                {
                    var s = serieses[i];
                    while (idx[i] < s.Count && s[idx[i]].Ts <= t) { last[i] = s[idx[i]].Equity; started[i] = true; idx[i]++; }
                    if (started[i]) sum += last[i];
                }
                result.Add((t, sum));
            }

            const int maxPoints = 1500;
            if (result.Count > maxPoints)
            {
                int stride = (int)Math.Ceiling(result.Count / (double)maxPoints);
                var sampled = new List<(DateTime, decimal)>();
                for (int i = 0; i < result.Count; i += stride) sampled.Add(result[i]);
                if (sampled[^1].Item1 != result[^1].Item1) sampled.Add(result[^1]); // keep the latest point
                result = sampled;
            }
            return result;
        }

        // Floor a UTC time to the start of its interval bucket (matches Binance bar-open times).
        private static DateTime BucketStart(DateTime utc, TimeInterval interval)
        {
            long sec = (int)interval * 60;
            long unix = ((DateTimeOffset)DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
            return DateTimeOffset.FromUnixTimeSeconds(unix - unix % sec).UtcDateTime;
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
        public decimal Leverage { get; set; } = 1m;
        public decimal? MaxPositionQuoteUsd { get; set; }
        public decimal? MaxDailyLossUsd { get; set; }
        public int? MaxOrdersPerHour { get; set; }
        public List<string>? AllowedSymbols { get; set; }
        public Dictionary<string, object?>? Parameters { get; set; }

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
                Margin = new MarginSettings { Leverage = Math.Clamp(Leverage, 1m, 10m) },
                Caps = caps,
                Parameters = Parameters
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
        public decimal Leverage { get; set; } = 1m;
        public Dictionary<string, object?>? Parameters { get; set; }
        /// <summary>When present, this is a cross-sectional momentum (portfolio) backtest. The universe
        /// window/params live here; Coin/Currency/Interval/CandleCount are derived from them.</summary>
        public MomentumBacktestSettings? Momentum { get; set; }
        /// <summary>When present, run the generic post-backtest validation (universe strategies only).</summary>
        public ValidationSettings? Validation { get; set; }

        public BacktestConfig ToBacktestConfig()
        {
            if (Momentum != null)
            {
                int days = Math.Max(1, (int)(Momentum.ToUtc - Momentum.FromUtc).TotalDays);
                return new BacktestConfig
                {
                    StrategyClass = string.IsNullOrWhiteSpace(StrategyClass) ? "CrossSectionalMomentumStrategy" : StrategyClass,
                    Coin = Momentum.RegimeSymbol,
                    Currency = "USD",
                    Interval = TimeInterval.OneDay,
                    CandleCount = days,
                    InitialQuoteBalance = InitialQuoteBalance,
                    FeeFraction = FeeFraction,
                    SlippageFraction = SlippageFraction,
                    Margin = new MarginSettings { Leverage = Math.Clamp(Leverage, 1m, 10m) },
                    Momentum = Momentum,
                    Parameters = Parameters,
                };
            }
            return new BacktestConfig
            {
                StrategyClass = StrategyClass,
                Coin = Coin,
                Currency = Currency,
                Interval = Enum.Parse<TimeInterval>(Interval, ignoreCase: true),
                CandleCount = CandleCount,
                InitialQuoteBalance = InitialQuoteBalance,
                InitialBaseBalance = InitialBaseBalance,
                FeeFraction = FeeFraction,
                SlippageFraction = SlippageFraction,
                Margin = new MarginSettings { Leverage = Math.Clamp(Leverage, 1m, 10m) },
                Validation = Validation,
                Parameters = Parameters,
            };
        }
    }
}
