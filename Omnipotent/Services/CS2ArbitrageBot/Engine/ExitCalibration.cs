using static Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs.Scanalytics;

namespace Omnipotent.Services.CS2ArbitrageBot.Engine
{
    /// <summary>One exit decision as recorded on a position: what was chosen, the alternative, and the evidence.</summary>
    public sealed class ExitDecision
    {
        public DateTime AtUtc { get; set; }
        /// <summary>"purchase", "sale" or "review".</summary>
        public string Stage { get; set; } = "";
        /// <summary>"buy", "steam", "relist", "reprice", "keep", "delist+steam", "defer" or "none".</summary>
        public string Action { get; set; } = "";
        public string Route { get; set; } = "";
        /// <summary>Steam: buy-order price, GBP pence. CSFloat: listing price, USD cents.</summary>
        public int Price { get; set; }
        public double ExpectedDaysToSell { get; set; }
        public double SellProbability { get; set; }
        public double CertaintyEquivalentPence { get; set; }
        public double SteamCertaintyEquivalentPence { get; set; }
        public double RelistCertaintyEquivalentPence { get; set; }
        public double SalesPerDay { get; set; }
        public string SalesRateBasis { get; set; } = "";
        public double MedianValueRatio { get; set; }
        public double DemandMultiplier { get; set; } = 1;
        /// <summary>Days until the unit could be sold when the decision was made (Valve's protection to its daily boundary).</summary>
        public double DaysUntilTradable { get; set; }
        /// <summary>Forecasts for when the unit becomes tradable (checked against what is observed then).</summary>
        public double SteamBidAtTradablePence { get; set; }
        public double AnchorAtTradableCents { get; set; }
        /// <summary>What was observed at decision time (depth-3 Steam bid; CSFloat value of this unit).</summary>
        public int ObservedSteamDepthBidPence { get; set; }
        public int ObservedAnchorCents { get; set; }
        public string Rationale { get; set; } = "";

        public static ExitDecision From(ExitPlan plan, string stage, string action, int observedDepthBidPence, int observedAnchorCents)
        {
            var best = plan.Best;
            return new ExitDecision
            {
                AtUtc = plan.ComputedAtUtc,
                Stage = stage,
                Action = action,
                Route = best?.Route.ToString() ?? nameof(ExitRoute.None),
                Price = best?.Price ?? 0,
                ExpectedDaysToSell = best == null ? 0 : Math.Round(best.ExpectedDaysToSell, 3),
                SellProbability = best == null ? 0 : Math.Round(best.SellProbability, 4),
                CertaintyEquivalentPence = Math.Round(best?.CertaintyEquivalentPence ?? 0, 1),
                SteamCertaintyEquivalentPence = Math.Round(plan.BestSteam?.CertaintyEquivalentPence ?? 0, 1),
                RelistCertaintyEquivalentPence = Math.Round(plan.BestRelist?.CertaintyEquivalentPence ?? 0, 1),
                SalesPerDay = Math.Round(plan.SalesPerDay, 4),
                SalesRateBasis = plan.SalesRateBasis,
                MedianValueRatio = Math.Round(plan.MedianValueRatio, 4),
                DemandMultiplier = Math.Round(plan.DemandMultiplier, 4),
                DaysUntilTradable = Math.Round(plan.DaysUntilTradable, 3),
                SteamBidAtTradablePence = Math.Round(plan.SteamBidAtTradablePence, 1),
                AnchorAtTradableCents = Math.Round(plan.AnchorAtTradableCents, 1),
                ObservedSteamDepthBidPence = observedDepthBidPence,
                ObservedAnchorCents = observedAnchorCents,
                Rationale = plan.Rationale,
            };
        }
    }

    /// <summary>What the bot's own track record says about the exit model.</summary>
    public sealed class ExitCalibrationSnapshot
    {
        public static readonly ExitCalibrationSnapshot Neutral = new();

        /// <summary>Realised ÷ predicted time to sell of the bot's relists (unsold listings count as censored), shrunk to 1.</summary>
        public double RelistTimeMultiplier { get; set; } = 1;
        public int RelistEpisodes { get; set; }
        public int RelistSales { get; set; }
        /// <summary>Mean log error of purchase-time forecasts (observed − forecast at tradability), shrunk to 0.</summary>
        public double SteamForecastBiasLog { get; set; }
        public double CSFloatForecastBiasLog { get; set; }
        public int ForecastChecks { get; set; }
        /// <summary>Σ realised net cash ÷ Σ net cash expected at purchase, over finished positions (reported, not applied).</summary>
        public double? RealisedVsExpected { get; set; }
        public int CompletedPositions { get; set; }
    }

    /// <summary>
    /// Learns from the bot's own exits. Every estimate is shrunk towards "the model was right" by a prior
    /// worth <see cref="PriorObservations"/> observations, so a couple of trades nudge it rather than swing it.
    /// </summary>
    public static class ExitCalibration
    {
        public const double PriorObservations = 3;
        public const double MaxBiasLog = 0.2;

        public static ExitCalibrationSnapshot Compute(IEnumerable<PurchasedListing> purchases, DateTime nowUtc)
        {
            var snapshot = new ExitCalibrationSnapshot();
            double exposure = 0, steamError = 0, csfloatError = 0, realised = 0, expected = 0;
            int sales = 0, episodes = 0, checks = 0, steamChecks = 0, csfloatChecks = 0, completed = 0;

            foreach (var p in purchases)
            {
                // Relist timing: the exposure is in model units (expected sales so far), so for an exponential
                // model the maximum-likelihood multiplier is Σ exposure ÷ sales, with unsold listings censored.
                if (p.RelistTotalExposure > 0)
                {
                    episodes++;
                    exposure += p.RelistTotalExposure;
                    if (p.ResaleSoldAtUtc != default) sales++;
                }

                // Forecast accuracy: what the purchase-time plan expected at tradability vs what the sale saw.
                var plan = p.PurchasePlan;
                var sale = p.ExitHistory?.FirstOrDefault(d => d.Stage == "sale");
                if (plan != null && sale != null)
                {
                    bool counted = false;
                    if (plan.SteamBidAtTradablePence > 0 && sale.ObservedSteamDepthBidPence > 0)
                    {
                        steamError += Math.Log(sale.ObservedSteamDepthBidPence / plan.SteamBidAtTradablePence);
                        steamChecks++;
                        counted = true;
                    }
                    if (plan.AnchorAtTradableCents > 0 && sale.ObservedAnchorCents > 0)
                    {
                        csfloatError += Math.Log(sale.ObservedAnchorCents / plan.AnchorAtTradableCents);
                        csfloatChecks++;
                        counted = true;
                    }
                    if (counted) checks++;
                }

                if (p.CurrentStrategicStage == StrategicStages.StrategyCompleted && p.ExpectedNetCashPence > 0 && p.PurchaseCostPence > 0)
                {
                    completed++;
                    realised += p.PurchaseCostPence + p.ActualAbsoluteProfitInPence;
                    expected += p.ExpectedNetCashPence;
                }
            }

            snapshot.RelistEpisodes = episodes;
            snapshot.RelistSales = sales;
            snapshot.RelistTimeMultiplier = Math.Clamp((PriorObservations + exposure) / (PriorObservations + sales), 0.25, 6);
            snapshot.ForecastChecks = checks;
            snapshot.SteamForecastBiasLog = Math.Clamp(steamError / (PriorObservations + steamChecks), -MaxBiasLog, MaxBiasLog);
            snapshot.CSFloatForecastBiasLog = Math.Clamp(csfloatError / (PriorObservations + csfloatChecks), -MaxBiasLog, MaxBiasLog);
            snapshot.CompletedPositions = completed;
            snapshot.RealisedVsExpected = expected > 0 ? realised / expected : null;
            return snapshot;
        }
    }
}
