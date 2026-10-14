using Newtonsoft.Json.Linq;
using System.Globalization;

namespace Omnipotent.Services.OmniTrader.Data.Crypto
{
    public sealed record TimePoint(DateTime T, double V);

    public sealed class FundingSnapshot
    {
        public required string Symbol { get; init; }
        public double MarkPrice { get; init; }
        public double IndexPrice { get; init; }
        /// <summary>Last funding rate per 8h interval, as a fraction.</summary>
        public double FundingRate { get; init; }
        /// <summary>Funding annualised (3 intervals a day × 365), %.</summary>
        public double FundingAnnualPct => FundingRate * 3 * 365 * 100;
        public double BasisPct => IndexPrice > 0 ? (MarkPrice / IndexPrice - 1) * 100 : 0;
        public DateTime NextFundingUtc { get; init; }
        public double? QuoteVolume24h { get; set; }
        public double? PriceChangePct24h { get; set; }
    }

    public sealed record VenueFunding(string Venue, string Instrument, double? FundingRate, double? FundingAnnualPct, DateTime? NextFundingUtc);

    public sealed class DerivativesView
    {
        public required string Symbol { get; init; }
        public FundingSnapshot? Current { get; set; }
        public List<TimePoint> Funding { get; set; } = new();
        public List<TimePoint> OpenInterest { get; set; } = new();
        public List<TimePoint> OpenInterestValue { get; set; } = new();
        public List<TimePoint> LongShortAccounts { get; set; } = new();
        public List<TimePoint> TopTraderLongShort { get; set; } = new();
        public List<TimePoint> TakerBuySell { get; set; } = new();
        public List<VenueFunding> Venues { get; set; } = new();
    }

    public sealed class CryptoTicker
    {
        public required string Symbol { get; init; }
        public double Price { get; init; }
        public double ChangePct { get; init; }
        public double High { get; init; }
        public double Low { get; init; }
        public double Volume { get; init; }
        public double QuoteVolume { get; init; }
        public double Trades { get; init; }
    }

    public sealed class CoinRow
    {
        public required string Id { get; init; }
        public required string Symbol { get; init; }
        public required string Name { get; init; }
        public int? Rank { get; init; }
        public double? Price { get; init; }
        public double? MarketCap { get; init; }
        public double? Volume24h { get; init; }
        public double? Change1hPct { get; init; }
        public double? Change24hPct { get; init; }
        public double? Change7dPct { get; init; }
        public double? AthChangePct { get; init; }
        public double? CirculatingSupply { get; init; }
        public string? Image { get; init; }
        /// <summary>Binance USDT pair when one exists, for charting.</summary>
        public string? BinanceSymbol { get; set; }
    }

    public sealed class CryptoGlobal
    {
        public double? TotalMarketCapUsd { get; init; }
        public double? TotalVolumeUsd { get; init; }
        public double? MarketCapChange24hPct { get; init; }
        public double? BtcDominancePct { get; init; }
        public double? EthDominancePct { get; init; }
        public int? ActiveCoins { get; init; }
        public Dictionary<string, double> Dominance { get; init; } = new();
    }

    public sealed record FearGreedPoint(DateTime T, int Value, string Label);

    public sealed class OptionSurfacePoint
    {
        public required string Instrument { get; init; }
        public DateTime Expiry { get; init; }
        public double Strike { get; init; }
        public bool Call { get; init; }
        public double? MarkIv { get; init; }
        public double? OpenInterest { get; init; }
        public double? Volume { get; init; }
        public double Moneyness { get; init; }
    }

    public sealed class OptionSurface
    {
        public required string Currency { get; init; }
        public double? Underlying { get; set; }
        public List<TermPoint> Term { get; set; } = new();
        public List<OptionSurfacePoint> Points { get; set; } = new();
        public double? PutCallOi { get; set; }
    }

    public sealed record TermPoint(DateTime Expiry, double Days, double? AtmIv, double? CallOi, double? PutOi);

    /// <summary>
    /// Free crypto data beyond spot bars: perpetual funding, open interest, positioning and taker
    /// flow (Binance USD-M), cross-venue funding (OKX, Bybit), implied volatility (Deribit DVOL and
    /// the option surface), market structure (CoinGecko, DefiLlama) and sentiment (Fear &amp; Greed).
    /// </summary>
    public sealed class CryptoData
    {
        private const string Fapi = "https://fapi.binance.com";
        private const string Spot = "https://api.binance.com/api/v3";

        private static double D(JToken? t) => DataHttp.Num(t) ?? 0;
        private static DateTime Ms(JToken? t) => DateTimeOffset.FromUnixTimeMilliseconds((long)(DataHttp.Num(t) ?? 0)).UtcDateTime;

        public static string PerpSymbol(string symbol)
        {
            string s = symbol.ToUpperInvariant().Replace("/", "").Replace("-", "");
            if (s.EndsWith("USDT")) return s;
            foreach (var q in new[] { "USDC", "BUSD", "USD" })
                if (s.EndsWith(q) && s.Length > q.Length) return s[..^q.Length] + "USDT";
            return s + "USDT";
        }

        public static string BaseAsset(string symbol)
        {
            string s = PerpSymbol(symbol);
            return s[..^4];
        }

        // ── Binance spot tickers ──────────────────────────────────────────────────

        public async Task<List<CryptoTicker>> GetTickersAsync(IEnumerable<string>? symbols = null, CancellationToken ct = default)
        {
            string url = $"{Spot}/ticker/24hr";
            var list = symbols?.Select(s => s.ToUpperInvariant().Replace("/", "").Replace("-", "")).Distinct().ToList();
            if (list is { Count: > 0 }) url += "?symbols=" + Uri.EscapeDataString("[" + string.Join(",", list.Select(s => $"\"{s}\"")) + "]");
            var json = await DataHttp.GetJsonAsync("binance-spot", "crypto", url, ct: ct);
            var arr = json as JArray ?? new JArray(json);
            return arr.Select(t => new CryptoTicker
            {
                Symbol = t["symbol"]?.Value<string>() ?? "",
                Price = D(t["lastPrice"]),
                ChangePct = D(t["priceChangePercent"]),
                High = D(t["highPrice"]),
                Low = D(t["lowPrice"]),
                Volume = D(t["volume"]),
                QuoteVolume = D(t["quoteVolume"]),
                Trades = D(t["count"]),
            }).ToList();
        }

        public async Task<object> GetOrderBookAsync(string symbol, int depth = 50, CancellationToken ct = default)
        {
            string sym = symbol.ToUpperInvariant().Replace("/", "").Replace("-", "");
            int limit = depth <= 5 ? 5 : depth <= 10 ? 10 : depth <= 20 ? 20 : depth <= 50 ? 50 : depth <= 100 ? 100 : 500;
            var json = await DataHttp.GetJsonAsync("binance-spot", "crypto", $"{Spot}/depth?symbol={sym}&limit={limit}", ct: ct);
            var bids = (json["bids"] as JArray ?? new JArray()).Select(b => new[] { D(b[0]), D(b[1]) }).ToList();
            var asks = (json["asks"] as JArray ?? new JArray()).Select(a => new[] { D(a[0]), D(a[1]) }).ToList();
            double bestBid = bids.Count > 0 ? bids[0][0] : 0, bestAsk = asks.Count > 0 ? asks[0][0] : 0;
            double mid = bestBid > 0 && bestAsk > 0 ? (bestBid + bestAsk) / 2 : 0;
            double bidDepth = bids.Where(b => mid > 0 && b[0] >= mid * 0.99).Sum(b => b[0] * b[1]);
            double askDepth = asks.Where(a => mid > 0 && a[0] <= mid * 1.01).Sum(a => a[0] * a[1]);
            return new
            {
                Symbol = sym,
                Bids = bids,
                Asks = asks,
                Mid = mid,
                SpreadBps = mid > 0 ? (bestAsk - bestBid) / mid * 10000 : (double?)null,
                BidDepth1Pct = bidDepth,
                AskDepth1Pct = askDepth,
                Imbalance = bidDepth + askDepth > 0 ? (bidDepth - askDepth) / (bidDepth + askDepth) : 0
            };
        }

        // ── perpetuals ────────────────────────────────────────────────────────────

        public async Task<List<FundingSnapshot>> GetFundingBoardAsync(CancellationToken ct = default)
        {
            var premium = await DataHttp.GetJsonAsync("binance-futures", "crypto", $"{Fapi}/fapi/v1/premiumIndex", ct: ct) as JArray ?? new JArray();
            var tickers = await DataHttp.GetJsonAsync("binance-futures", "crypto", $"{Fapi}/fapi/v1/ticker/24hr", ct: ct) as JArray ?? new JArray();
            var vol = tickers.ToDictionary(t => t["symbol"]?.Value<string>() ?? "", t => (D(t["quoteVolume"]), D(t["priceChangePercent"])), StringComparer.OrdinalIgnoreCase);
            return premium.Select(p =>
            {
                string sym = p["symbol"]?.Value<string>() ?? "";
                var snap = new FundingSnapshot
                {
                    Symbol = sym,
                    MarkPrice = D(p["markPrice"]),
                    IndexPrice = D(p["indexPrice"]),
                    FundingRate = D(p["lastFundingRate"]),
                    NextFundingUtc = Ms(p["nextFundingTime"]),
                };
                if (vol.TryGetValue(sym, out var v)) { snap.QuoteVolume24h = v.Item1; snap.PriceChangePct24h = v.Item2; }
                return snap;
            }).Where(s => s.Symbol.EndsWith("USDT") && s.MarkPrice > 0).OrderByDescending(s => s.QuoteVolume24h ?? 0).ToList();
        }

        public async Task<DerivativesView> GetDerivativesAsync(string symbol, string period = "1h", int limit = 168, CancellationToken ct = default)
        {
            string perp = PerpSymbol(symbol);
            limit = Math.Clamp(limit, 10, 500);
            var view = new DerivativesView { Symbol = perp };

            async Task<JArray> Arr(string url)
            {
                try { return await DataHttp.GetJsonAsync("binance-futures", "crypto", url, ct: ct) as JArray ?? new JArray(); }
                catch { return new JArray(); }
            }

            var premiumTask = DataHttp.GetJsonAsync("binance-futures", "crypto", $"{Fapi}/fapi/v1/premiumIndex?symbol={perp}", ct: ct);
            var fundingTask = Arr($"{Fapi}/fapi/v1/fundingRate?symbol={perp}&limit={Math.Min(1000, limit * 3)}");
            var oiTask = Arr($"{Fapi}/futures/data/openInterestHist?symbol={perp}&period={period}&limit={limit}");
            var lsTask = Arr($"{Fapi}/futures/data/globalLongShortAccountRatio?symbol={perp}&period={period}&limit={limit}");
            var topTask = Arr($"{Fapi}/futures/data/topLongShortPositionRatio?symbol={perp}&period={period}&limit={limit}");
            var takerTask = Arr($"{Fapi}/futures/data/takerlongshortRatio?symbol={perp}&period={period}&limit={limit}");
            var venuesTask = GetCrossVenueFundingAsync(BaseAsset(perp), ct);

            try
            {
                var p = await premiumTask;
                view.Current = new FundingSnapshot
                {
                    Symbol = perp, MarkPrice = D(p["markPrice"]), IndexPrice = D(p["indexPrice"]),
                    FundingRate = D(p["lastFundingRate"]), NextFundingUtc = Ms(p["nextFundingTime"])
                };
            }
            catch { }

            view.Funding = (await fundingTask).Select(f => new TimePoint(Ms(f["fundingTime"]), D(f["fundingRate"]) * 100)).ToList();
            var oi = await oiTask;
            view.OpenInterest = oi.Select(o => new TimePoint(Ms(o["timestamp"]), D(o["sumOpenInterest"]))).ToList();
            view.OpenInterestValue = oi.Select(o => new TimePoint(Ms(o["timestamp"]), D(o["sumOpenInterestValue"]))).ToList();
            view.LongShortAccounts = (await lsTask).Select(o => new TimePoint(Ms(o["timestamp"]), D(o["longShortRatio"]))).ToList();
            view.TopTraderLongShort = (await topTask).Select(o => new TimePoint(Ms(o["timestamp"]), D(o["longShortRatio"]))).ToList();
            view.TakerBuySell = (await takerTask).Select(o => new TimePoint(Ms(o["timestamp"]), D(o["buySellRatio"]))).ToList();
            try { view.Venues = await venuesTask; } catch { }
            return view;
        }

        /// <summary>Current funding for one asset across Binance, OKX and Bybit.</summary>
        public async Task<List<VenueFunding>> GetCrossVenueFundingAsync(string baseAsset, CancellationToken ct = default)
        {
            string b = baseAsset.ToUpperInvariant();
            var results = new List<VenueFunding>();

            async Task Try(Func<Task<VenueFunding?>> f)
            {
                try { if (await f() is { } v) lock (results) results.Add(v); } catch { }
            }

            await Task.WhenAll(
                Try(async () =>
                {
                    var p = await DataHttp.GetJsonAsync("binance-futures", "crypto", $"{Fapi}/fapi/v1/premiumIndex?symbol={b}USDT", ct: ct);
                    double r = D(p["lastFundingRate"]);
                    return new VenueFunding("Binance", $"{b}USDT", r * 100, r * 3 * 365 * 100, Ms(p["nextFundingTime"]));
                }),
                Try(async () =>
                {
                    var j = await DataHttp.GetJsonAsync("okx", "crypto", $"https://www.okx.com/api/v5/public/funding-rate?instId={b}-USDT-SWAP", ct: ct);
                    var d = j["data"]?.FirstOrDefault();
                    if (d == null) return null;
                    double r = D(d["fundingRate"]);
                    return new VenueFunding("OKX", $"{b}-USDT-SWAP", r * 100, r * 3 * 365 * 100, Ms(d["nextFundingTime"]) is var nf && nf.Year > 2000 ? nf : Ms(d["fundingTime"]));
                }),
                Try(async () =>
                {
                    var j = await DataHttp.GetJsonAsync("bybit", "crypto", $"https://api.bybit.com/v5/market/tickers?category=linear&symbol={b}USDT", ct: ct);
                    var d = j["result"]?["list"]?.FirstOrDefault();
                    if (d == null) return null;
                    double r = D(d["fundingRate"]);
                    return new VenueFunding("Bybit", $"{b}USDT", r * 100, r * 3 * 365 * 100, Ms(d["nextFundingTime"]));
                }));
            return results.OrderBy(r => r.Venue).ToList();
        }

        // ── implied volatility (Deribit) ──────────────────────────────────────────

        public async Task<List<TimePoint>> GetDvolAsync(string currency = "BTC", int days = 180, string resolution = "1D", CancellationToken ct = default)
        {
            long end = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long start = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds();
            string res = resolution switch { "1h" or "60" => "3600", "1D" or "1d" => "1D", _ => resolution };
            var json = await DataHttp.GetJsonAsync("deribit", "crypto",
                $"https://www.deribit.com/api/v2/public/get_volatility_index_data?currency={currency.ToUpperInvariant()}&start_timestamp={start}&end_timestamp={end}&resolution={res}", ct: ct);
            return (json["result"]?["data"] as JArray ?? new JArray())
                .Select(r => new TimePoint(Ms(r[0]), D(r[4]))).OrderBy(p => p.T).ToList();
        }

        public async Task<OptionSurface> GetOptionSurfaceAsync(string currency = "BTC", CancellationToken ct = default)
        {
            var json = await DataHttp.GetJsonAsync("deribit", "crypto",
                $"https://www.deribit.com/api/v2/public/get_book_summary_by_currency?currency={currency.ToUpperInvariant()}&kind=option", ct: ct);
            var surface = new OptionSurface { Currency = currency.ToUpperInvariant() };
            var points = new List<OptionSurfacePoint>();
            foreach (var r in json["result"] as JArray ?? new JArray())
            {
                string name = r["instrument_name"]?.Value<string>() ?? "";
                var parts = name.Split('-');
                if (parts.Length != 4) continue;
                if (!DateTime.TryParseExact(parts[1], "dMMMyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expiry)) continue;
                if (!double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var strike)) continue;
                double underlying = D(r["underlying_price"]);
                surface.Underlying ??= underlying > 0 ? underlying : null;
                points.Add(new OptionSurfacePoint
                {
                    Instrument = name,
                    Expiry = expiry.AddHours(8),
                    Strike = strike,
                    Call = parts[3] == "C",
                    MarkIv = DataHttp.Num(r["mark_iv"]),
                    OpenInterest = DataHttp.Num(r["open_interest"]),
                    Volume = DataHttp.Num(r["volume"]),
                    Moneyness = underlying > 0 ? strike / underlying : 0
                });
            }
            surface.Points = points.OrderBy(p => p.Expiry).ThenBy(p => p.Strike).ToList();
            double callOi = points.Where(p => p.Call).Sum(p => p.OpenInterest ?? 0), putOi = points.Where(p => !p.Call).Sum(p => p.OpenInterest ?? 0);
            surface.PutCallOi = callOi > 0 ? putOi / callOi : null;
            surface.Term = points.GroupBy(p => p.Expiry).OrderBy(g => g.Key).Select(g =>
            {
                var atm = g.Where(p => p.MarkIv is > 0).OrderBy(p => Math.Abs(p.Moneyness - 1)).Take(4).ToList();
                return new TermPoint(g.Key, Math.Round((g.Key - DateTime.UtcNow).TotalDays, 2),
                    atm.Count > 0 ? atm.Average(p => p.MarkIv!.Value) : null,
                    g.Where(p => p.Call).Sum(p => p.OpenInterest ?? 0), g.Where(p => !p.Call).Sum(p => p.OpenInterest ?? 0));
            }).Where(t => t.Days > 0).ToList();
            return surface;
        }

        // ── market structure ──────────────────────────────────────────────────────

        public async Task<CryptoGlobal> GetGlobalAsync(CancellationToken ct = default)
        {
            var json = await DataHttp.GetJsonAsync("coingecko", "crypto", "https://api.coingecko.com/api/v3/global", ct: ct);
            var d = json["data"];
            var pct = d?["market_cap_percentage"] as JObject;
            var dominance = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (pct != null) foreach (var p in pct.Properties()) dominance[p.Name.ToUpperInvariant()] = D(p.Value);
            return new CryptoGlobal
            {
                TotalMarketCapUsd = DataHttp.Num(d?["total_market_cap"]?["usd"]),
                TotalVolumeUsd = DataHttp.Num(d?["total_volume"]?["usd"]),
                MarketCapChange24hPct = DataHttp.Num(d?["market_cap_change_percentage_24h_usd"]),
                BtcDominancePct = dominance.TryGetValue("BTC", out var btc) ? btc : null,
                EthDominancePct = dominance.TryGetValue("ETH", out var eth) ? eth : null,
                ActiveCoins = (int?)DataHttp.Num(d?["active_cryptocurrencies"]),
                Dominance = dominance
            };
        }

        public async Task<List<CoinRow>> GetTopCoinsAsync(int count = 100, CancellationToken ct = default)
        {
            count = Math.Clamp(count, 10, 250);
            var json = await DataHttp.GetJsonAsync("coingecko", "crypto",
                $"https://api.coingecko.com/api/v3/coins/markets?vs_currency=usd&order=market_cap_desc&per_page={count}&page=1&price_change_percentage=1h,24h,7d", ct: ct);
            return (json as JArray ?? new JArray()).Select(c => new CoinRow
            {
                Id = c["id"]?.Value<string>() ?? "",
                Symbol = (c["symbol"]?.Value<string>() ?? "").ToUpperInvariant(),
                Name = c["name"]?.Value<string>() ?? "",
                Rank = (int?)DataHttp.Num(c["market_cap_rank"]),
                Price = DataHttp.Num(c["current_price"]),
                MarketCap = DataHttp.Num(c["market_cap"]),
                Volume24h = DataHttp.Num(c["total_volume"]),
                Change1hPct = DataHttp.Num(c["price_change_percentage_1h_in_currency"]),
                Change24hPct = DataHttp.Num(c["price_change_percentage_24h_in_currency"] ?? c["price_change_percentage_24h"]),
                Change7dPct = DataHttp.Num(c["price_change_percentage_7d_in_currency"]),
                AthChangePct = DataHttp.Num(c["ath_change_percentage"]),
                CirculatingSupply = DataHttp.Num(c["circulating_supply"]),
                Image = c["image"]?.Value<string>(),
            }).ToList();
        }

        public async Task<List<object>> GetTrendingCoinsAsync(CancellationToken ct = default)
        {
            var json = await DataHttp.GetJsonAsync("coingecko", "crypto", "https://api.coingecko.com/api/v3/search/trending", ct: ct);
            return (json["coins"] as JArray ?? new JArray()).Select(c => c["item"]).Where(i => i != null).Select(i => (object)new
            {
                Id = i!["id"]?.Value<string>(),
                Symbol = i["symbol"]?.Value<string>()?.ToUpperInvariant(),
                Name = i["name"]?.Value<string>(),
                Rank = (int?)DataHttp.Num(i["market_cap_rank"]),
                Price = DataHttp.Num(i["data"]?["price"]),
                Change24hPct = DataHttp.Num(i["data"]?["price_change_percentage_24h"]?["usd"]),
                Image = i["thumb"]?.Value<string>()
            }).ToList();
        }

        public async Task<List<FearGreedPoint>> GetFearGreedAsync(int days = 365, CancellationToken ct = default)
        {
            var json = await DataHttp.GetJsonAsync("alternative.me", "sentiment", $"https://api.alternative.me/fng/?limit={Math.Clamp(days, 1, 2000)}", ct: ct);
            return (json["data"] as JArray ?? new JArray()).Select(d => new FearGreedPoint(
                DataHttp.Unix(d["timestamp"]) ?? DateTime.MinValue,
                (int)(DataHttp.Num(d["value"]) ?? 0),
                d["value_classification"]?.Value<string>() ?? "")).OrderBy(p => p.T).ToList();
        }

        public async Task<List<TimePoint>> GetTotalTvlAsync(CancellationToken ct = default)
        {
            var json = await DataHttp.GetJsonAsync("defillama", "crypto", "https://api.llama.fi/v2/historicalChainTvl", ct: ct);
            return (json as JArray ?? new JArray()).Select(d => new TimePoint(DataHttp.Unix(d["date"]) ?? DateTime.MinValue, D(d["tvl"])))
                .Where(p => p.V > 0).ToList();
        }

        public async Task<List<object>> GetChainsAsync(int top = 25, CancellationToken ct = default)
        {
            var json = await DataHttp.GetJsonAsync("defillama", "crypto", "https://api.llama.fi/v2/chains", ct: ct);
            return (json as JArray ?? new JArray())
                .Select(c => new { Name = c["name"]?.Value<string>(), Token = c["tokenSymbol"]?.Value<string>(), Tvl = D(c["tvl"]) })
                .OrderByDescending(c => c.Tvl).Take(top).Cast<object>().ToList();
        }

        public async Task<List<TimePoint>> GetStablecoinSupplyAsync(CancellationToken ct = default)
        {
            var json = await DataHttp.GetJsonAsync("defillama", "crypto", "https://stablecoins.llama.fi/stablecoincharts/all", ct: ct);
            return (json as JArray ?? new JArray()).Select(d => new TimePoint(DataHttp.Unix(d["date"]) ?? DateTime.MinValue,
                D(d["totalCirculatingUSD"]?["peggedUSD"]))).Where(p => p.V > 0).ToList();
        }
    }
}
