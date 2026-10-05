using Omnipotent.Services.CS2ArbitrageBot.Steam;

namespace Omnipotent.Services.CS2ArbitrageBot.Engine
{
    /// <summary>
    /// A distributional forecast of an item's price level on a log scale: where it is now, where it is
    /// expected to be in h days, and how uncertain that is.
    /// </summary>
    public sealed class PriceForecast
    {
        public ItemCategory Category { get; set; }
        public bool HasHistory { get; set; }
        /// <summary>ln(GBP) of the current price level.</summary>
        public double LevelLog { get; set; }
        /// <summary>Expected log change per day (the category drift, never the item's own trend; see <see cref="PriceForecaster"/>).</summary>
        public double DriftPerDay { get; set; }
        /// <summary>Standard deviation of the 8-day log change.</summary>
        public double Sigma8 { get; set; }
        /// <summary>σ(h) = Sigma8 × (h/8)^HorizonExponent. Below 0.5: daily noise partly reverts instead of compounding.</summary>
        public double HorizonExponent { get; set; } = PriceForecaster.DefaultHorizonExponent;
        /// <summary>Live calibration multiplier from the walk-forward panel (1 = forecasts were as accurate as claimed).</summary>
        public double VolatilityScale { get; set; } = 1;
        public double SteamSalesPerDay { get; set; }
        public double DaysSinceLastSale { get; set; } = double.PositiveInfinity;
        public int DaysWithData { get; set; }
        public string Basis { get; set; } = "";

        public double LevelPence => HasHistory ? Math.Exp(LevelLog) * 100 : 0;
        public double ExpectedLogChange(double days) => DriftPerDay * Math.Max(0, days);
        /// <summary>Median multiplicative change over <paramref name="days"/> (the lognormal's convexity bonus is deliberately not counted).</summary>
        public double Growth(double days) => Math.Exp(ExpectedLogChange(days));
        public double Sigma(double days) => VolatilityScale * RawSigma(days);

        /// <summary>
        /// σ of the log change over <paramref name="days"/> before live calibration. The power law is fitted on
        /// 4–16 day changes; below 4 days it scales diffusively (√h) from σ(4), since the daily-median noise that
        /// flattens the fitted exponent is not movement of the level itself.
        /// </summary>
        public double RawSigma(double days)
        {
            if (days <= 0) return 0;
            if (days >= PriceForecaster.ShortestFittedHorizonDays) return Sigma8 * Math.Pow(days / 8.0, HorizonExponent);
            double sigma4 = Sigma8 * Math.Pow(PriceForecaster.ShortestFittedHorizonDays / 8.0, HorizonExponent);
            return sigma4 * Math.Sqrt(days / PriceForecaster.ShortestFittedHorizonDays);
        }
    }

    /// <summary>
    /// Forecasts Steam price levels from Steam's price history (hourly for the last month, daily before).
    ///
    /// Every choice here comes from a walk-forward backtest over 62 items × 200 days (4,000 forecasts per
    /// horizon, Oct 2026):
    /// <list type="bullet">
    /// <item>Extrapolating an item's own trend loses to "no change" at 8 and 15 days, shrunk or not. The previous
    /// guard (last 3 days vs days 4–14, declines only) was the worst model tested: 1.15–1.29× the error of no
    /// change, biased towards predicting falls that did not happen. So the item's trend is not used.</item>
    /// <item>The best current level is an exponentially weighted average (4-day half-life, √volume weights):
    /// 7–8% less error than a 3-day average.</item>
    /// <item>Prices drift down on average, and by category (charms and stickers most, cases not at all), so
    /// the expected change is a category drift that the live panel keeps re-estimating.</item>
    /// <item>Daily volatility × √h overstates the uncertainty ~1.5× (daily medians are noisy and revert). Measuring
    /// the spread of the item's own h-day changes directly is calibrated: 70% of outcomes within ±1σ, 94% within ±2σ.</item>
    /// </list>
    /// </summary>
    public static class PriceForecaster
    {
        public const double LevelHalfLifeDays = 4;
        public const int LevelWindowDays = 60;
        public const int VolatilityWindowDays = 180;
        public const double DefaultHorizonExponent = 0.3;
        /// <summary>Horizons the spread is measured at; shorter ones would overlap the 3-day level windows.</summary>
        private static readonly int[] AnchorHorizons = { 4, 8, 16 };
        public const double ShortestFittedHorizonDays = 4;

        /// <summary>σ of the 8-day log change by category (same backtest); used when an item has too little history.</summary>
        public static readonly IReadOnlyDictionary<ItemCategory, double> DefaultSigma8 = new Dictionary<ItemCategory, double>
        {
            [ItemCategory.Skin] = 0.21,
            [ItemCategory.Sticker] = 0.12,
            [ItemCategory.Container] = 0.09,
            [ItemCategory.Charm] = 0.10,
            [ItemCategory.Patch] = 0.09,
            [ItemCategory.Other] = 0.11,
        };

        public readonly record struct DailyPoint(DateTime DayUtc, double LogPrice, int Volume);

        /// <summary>One point per UTC day: the volume-weighted mean of ln(median price), and the units sold.</summary>
        public static List<DailyPoint> Daily(IEnumerable<SteamPricePoint>? history)
        {
            var buckets = new SortedDictionary<DateTime, (double WeightedLog, double Weight, int Volume)>();
            if (history == null) return new List<DailyPoint>();
            foreach (var p in history)
            {
                if (p.MedianPriceGbp <= 0) continue;
                DateTime day = DateTime.SpecifyKind(p.TimeUtc.Date, DateTimeKind.Utc);
                double w = Math.Max(1, p.Purchases);
                buckets.TryGetValue(day, out var b);
                buckets[day] = (b.WeightedLog + w * Math.Log(p.MedianPriceGbp), b.Weight + w, b.Volume + Math.Max(0, p.Purchases));
            }
            return buckets.Select(kv => new DailyPoint(kv.Key, kv.Value.WeightedLog / kv.Value.Weight, kv.Value.Volume)).ToList();
        }

        /// <summary>The exponentially weighted level as of <paramref name="asOfDay"/>, from days on or before it.</summary>
        public static double? Level(IReadOnlyList<DailyPoint> daily, DateTime asOfDay)
        {
            double sum = 0, weight = 0;
            foreach (var p in daily)
            {
                if (p.DayUtc > asOfDay || p.DayUtc <= asOfDay.AddDays(-LevelWindowDays)) continue;
                double age = (asOfDay - p.DayUtc).TotalDays;
                double w = Math.Pow(0.5, age / LevelHalfLifeDays) * Math.Sqrt(Math.Max(1, p.Volume));
                sum += w * p.LogPrice;
                weight += w;
            }
            return weight > 0 ? sum / weight : null;
        }

        /// <summary>Volume-weighted mean log price over the <paramref name="days"/> days ending at <paramref name="asOfDay"/>.</summary>
        public static double? WindowLevel(IReadOnlyList<DailyPoint> daily, DateTime asOfDay, int days = 3)
        {
            double sum = 0, weight = 0;
            foreach (var p in daily)
            {
                if (p.DayUtc > asOfDay || p.DayUtc <= asOfDay.AddDays(-days)) continue;
                double w = Math.Max(1, p.Volume);
                sum += w * p.LogPrice;
                weight += w;
            }
            return weight > 0 ? sum / weight : null;
        }

        /// <summary>
        /// Fits σ(h) = σ₈ (h/8)^H to the spread of the item's own h-day level changes over the last half year.
        /// Null when there are too few observations.
        /// </summary>
        public static (double Sigma8, double Exponent)? FitHorizonVolatility(IReadOnlyList<DailyPoint> daily, DateTime asOfDay)
        {
            var window = daily.Where(p => p.DayUtc > asOfDay.AddDays(-VolatilityWindowDays) && p.DayUtc <= asOfDay).ToList();
            if (window.Count < 20) return null;
            var points = new List<(double LogH, double LogSigma)>();
            foreach (int h in AnchorHorizons)
            {
                var changes = new List<double>();
                for (int i = 0; i < window.Count; i += 2)
                {
                    DateTime start = window[i].DayUtc, end = start.AddDays(h);
                    if (end > asOfDay) break;
                    if (WindowLevel(window, start) is double a && WindowLevel(window, end) is double b) changes.Add(b - a);
                }
                if (changes.Count < 8) continue;
                double mean = changes.Average();
                double sd = Math.Sqrt(changes.Sum(c => (c - mean) * (c - mean)) / (changes.Count - 1));
                if (sd > 0) points.Add((Math.Log(h), Math.Log(sd)));
            }
            if (points.Count == 0) return null;

            double exponent, logSigma8;
            if (points.Count == 1)
            {
                exponent = DefaultHorizonExponent;
                logSigma8 = points[0].LogSigma + exponent * (Math.Log(8) - points[0].LogH);
            }
            else
            {
                double mx = points.Average(p => p.LogH), my = points.Average(p => p.LogSigma);
                double sxx = points.Sum(p => (p.LogH - mx) * (p.LogH - mx));
                exponent = sxx > 0 ? points.Sum(p => (p.LogH - mx) * (p.LogSigma - my)) / sxx : DefaultHorizonExponent;
                exponent = Math.Clamp(exponent, 0.05, 0.6);
                logSigma8 = my + exponent * (Math.Log(8) - mx);
            }
            return (Math.Clamp(Math.Exp(logSigma8), 0.015, 1.0), exponent);
        }

        /// <summary>Steam units sold per day over the last <paramref name="days"/> full days (days without sales count as zero).</summary>
        public static double SalesPerDay(IReadOnlyList<DailyPoint> daily, DateTime nowUtc, int days = 14)
        {
            DateTime today = nowUtc.Date;
            return daily.Where(p => p.DayUtc < today && p.DayUtc >= today.AddDays(-days)).Sum(p => p.Volume) / (double)days;
        }

        /// <summary>Realised drift of the level per day over the last <paramref name="days"/> days.</summary>
        public static double? TrailingDrift(IReadOnlyList<DailyPoint> daily, DateTime nowUtc, int days = 30)
        {
            DateTime today = nowUtc.Date;
            double? now = Level(daily, today), then = Level(daily, today.AddDays(-days));
            return now is double a && then is double b ? (a - b) / days : null;
        }

        public static PriceForecast Forecast(IReadOnlyList<SteamPricePoint>? history, string marketHashName, DateTime nowUtc, MarketDriftModel? market = null)
        {
            var category = ItemCategories.Of(marketHashName);
            var forecast = new PriceForecast
            {
                Category = category,
                DriftPerDay = market?.DriftPerDay(category) ?? MarketDriftModel.DefaultDriftPerDay[category],
                VolatilityScale = market?.VolatilityScale ?? 1,
            };
            DateTime today = DateTime.SpecifyKind(nowUtc.Date, DateTimeKind.Utc);
            var daily = Daily(history).Where(p => p.DayUtc <= today).ToList();
            forecast.DaysWithData = daily.Count(p => p.DayUtc > today.AddDays(-LevelWindowDays));
            if (history is { Count: > 0 })
            {
                DateTime last = history.Where(p => p.TimeUtc <= nowUtc).Select(p => p.TimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
                if (last != DateTime.MinValue) forecast.DaysSinceLastSale = Math.Max(0, (nowUtc - last).TotalDays);
            }
            forecast.SteamSalesPerDay = SalesPerDay(daily, nowUtc);

            if (Level(daily, today) is double level)
            {
                forecast.HasHistory = true;
                forecast.LevelLog = level;
            }
            var fit = forecast.DaysWithData >= 5 ? FitHorizonVolatility(daily, today) : null;
            if (fit is { } fitted)
            {
                forecast.Sigma8 = fitted.Sigma8;
                forecast.HorizonExponent = fitted.Exponent;
                forecast.Basis = $"item history ({daily.Count(p => p.DayUtc > today.AddDays(-VolatilityWindowDays))} days)";
            }
            else
            {
                // Unknown items are riskier than the average of their category, not equal to it.
                forecast.Sigma8 = DefaultSigma8[category] * 1.25;
                forecast.Basis = forecast.HasHistory ? "thin Steam history: category volatility +25%" : "no Steam history: category volatility +25%";
            }
            return forecast;
        }

        /// <summary>
        /// Walk-forward check of the forecaster on one item: forecast from <paramref name="horizonDays"/>+1 days ago
        /// using only data up to then, compare with the level since, and return the standardised error
        /// (realised − forecast) / σ. Null when the history is too short. Feeds <see cref="MarketDriftModel"/>.
        /// </summary>
        public static double? WalkForwardZ(IReadOnlyList<SteamPricePoint>? history, string marketHashName, DateTime nowUtc, MarketDriftModel? market = null, int horizonDays = 8)
        {
            if (history == null || history.Count == 0) return null;
            DateTime today = DateTime.SpecifyKind(nowUtc.Date, DateTimeKind.Utc);
            DateTime origin = today.AddDays(-(horizonDays + 1));
            var past = history.Where(p => p.TimeUtc < origin.AddDays(1)).ToList();
            var forecast = Forecast(past, marketHashName, origin.AddDays(1).AddTicks(-1), market);
            if (!forecast.HasHistory || forecast.DaysWithData < 10) return null;
            double? realised = WindowLevel(Daily(history), today);
            if (realised == null) return null;
            double sigma = forecast.RawSigma(horizonDays);
            if (sigma <= 0) return null;
            return (realised.Value - (forecast.LevelLog + forecast.ExpectedLogChange(horizonDays))) / sigma;
        }
    }
}
