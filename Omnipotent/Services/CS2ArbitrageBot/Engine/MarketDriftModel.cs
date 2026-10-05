using Newtonsoft.Json;

namespace Omnipotent.Services.CS2ArbitrageBot.Engine
{
    /// <summary>
    /// What the market as a whole is doing, learned continuously from evidence: the expected drift of each
    /// item category and how honest the forecaster's uncertainty is.
    ///
    /// Observations come from a random panel of items (<see cref="SnipeEngine"/> samples a few every couple
    /// of hours), never from the items the bot is considering buying: those are selected for looking cheap,
    /// often because they have been falling, and would bias the drift pessimistically.
    /// </summary>
    public sealed class MarketDriftModel
    {
        /// <summary>
        /// Mean realised log drift per day by category from the walk-forward backtest (62 items × 200 days, Oct
        /// 2026), halved: a single window is weak evidence. The live panel takes over as it accumulates.
        /// </summary>
        public static readonly IReadOnlyDictionary<ItemCategory, double> DefaultDriftPerDay = new Dictionary<ItemCategory, double>
        {
            [ItemCategory.Skin] = -0.0005,
            [ItemCategory.Sticker] = -0.0015,
            [ItemCategory.Container] = 0.0,
            [ItemCategory.Charm] = -0.0025,
            [ItemCategory.Patch] = -0.0017,
            [ItemCategory.Other] = -0.0013,
        };

        /// <summary>The defaults count as this many observations.</summary>
        public const double PriorWeight = 30;
        /// <summary>Older observations fade with this half-life (in observations), so a regime change is followed.</summary>
        public const double ForgettingHalfLife = 300;
        public const double MaxAbsDriftObservation = 0.03;
        public const double MaxAbsZ = 6;

        public sealed class DriftState
        {
            public double WeightedSum { get; set; }
            public double Weight { get; set; }
            public long Observations { get; set; }
            public DateTime? LastUpdatedUtc { get; set; }
        }

        public sealed class ErrorState
        {
            public double WeightedSquares { get; set; }
            public double WeightedWithinOneSigma { get; set; }
            public double Weight { get; set; }
            public long Observations { get; set; }
        }

        private readonly object gate = new();
        public Dictionary<ItemCategory, DriftState> Drift { get; set; } = new();
        public ErrorState ForecastErrors { get; set; } = new();
        /// <summary>Item → last UTC day it was observed (one observation per item per day).</summary>
        public Dictionary<string, DateTime> LastObservedDay { get; set; } = new(StringComparer.Ordinal);

        public double DriftPerDay(ItemCategory category)
        {
            double prior = DefaultDriftPerDay[category];
            lock (gate)
            {
                if (!Drift.TryGetValue(category, out var state)) return prior;
                return (PriorWeight * prior + state.WeightedSum) / (PriorWeight + state.Weight);
            }
        }

        /// <summary>RMS of the panel's standardised forecast errors, shrunk to 1: multiplies every forecast σ.</summary>
        [JsonIgnore]
        public double VolatilityScale
        {
            get
            {
                lock (gate)
                {
                    double meanSquare = (PriorWeight + ForecastErrors.WeightedSquares) / (PriorWeight + ForecastErrors.Weight);
                    return Math.Clamp(Math.Sqrt(meanSquare), 0.6, 2.0);
                }
            }
        }

        /// <summary>Share of panel outcomes within ±1σ (0.68 is perfectly calibrated); null before any evidence.</summary>
        [JsonIgnore]
        public double? CoverageWithinOneSigma
        {
            get { lock (gate) return ForecastErrors.Weight > 0 ? ForecastErrors.WeightedWithinOneSigma / ForecastErrors.Weight : null; }
        }

        /// <summary>
        /// Records one panel item: its realised drift over the last month and the standardised error of a
        /// walk-forward forecast. Returns false when the item was already observed today.
        /// </summary>
        public bool Observe(string marketHashName, ItemCategory category, double? trailingDriftPerDay, double? forecastZ, DateTime nowUtc)
        {
            DateTime day = nowUtc.Date;
            double decay = Math.Pow(0.5, 1 / ForgettingHalfLife);
            lock (gate)
            {
                if (LastObservedDay.TryGetValue(marketHashName, out var last) && last >= day) return false;
                LastObservedDay[marketHashName] = day;
                if (LastObservedDay.Count > 4000)
                    foreach (var stale in LastObservedDay.OrderBy(kv => kv.Value).Take(LastObservedDay.Count - 3000).Select(kv => kv.Key).ToList())
                        LastObservedDay.Remove(stale);

                if (trailingDriftPerDay is double drift && double.IsFinite(drift))
                {
                    if (!Drift.TryGetValue(category, out var state)) Drift[category] = state = new DriftState();
                    state.WeightedSum = state.WeightedSum * decay + Math.Clamp(drift, -MaxAbsDriftObservation, MaxAbsDriftObservation);
                    state.Weight = state.Weight * decay + 1;
                    state.Observations++;
                    state.LastUpdatedUtc = nowUtc;
                }
                if (forecastZ is double z && double.IsFinite(z))
                {
                    z = Math.Clamp(z, -MaxAbsZ, MaxAbsZ);
                    ForecastErrors.WeightedSquares = ForecastErrors.WeightedSquares * decay + z * z;
                    ForecastErrors.WeightedWithinOneSigma = ForecastErrors.WeightedWithinOneSigma * decay + (Math.Abs(z) <= 1 ? 1 : 0);
                    ForecastErrors.Weight = ForecastErrors.Weight * decay + 1;
                    ForecastErrors.Observations++;
                }
                return true;
            }
        }

        /// <summary>Serialises under the lock (the panel may be adding an observation on another thread).</summary>
        public string ToJson()
        {
            lock (gate) return JsonConvert.SerializeObject(this);
        }

        /// <summary>For the status route.</summary>
        public object Describe() => new
        {
            driftPerDay = Enum.GetValues<ItemCategory>().ToDictionary(c => c.ToString(), c => Math.Round(DriftPerDay(c), 5)),
            observations = Enum.GetValues<ItemCategory>().ToDictionary(c => c.ToString(), c => { lock (gate) return Drift.TryGetValue(c, out var s) ? s.Observations : 0; }),
            volatilityScale = Math.Round(VolatilityScale, 3),
            forecastChecks = ForecastErrors.Observations,
            coverageWithinOneSigma = CoverageWithinOneSigma is double c ? Math.Round(c, 3) : (double?)null,
        };
    }
}
