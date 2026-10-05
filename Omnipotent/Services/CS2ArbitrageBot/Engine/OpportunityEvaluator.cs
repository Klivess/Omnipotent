using Omnipotent.Services.CS2ArbitrageBot.CSFloat;
using Omnipotent.Services.CS2ArbitrageBot.Steam;

namespace Omnipotent.Services.CS2ArbitrageBot.Engine
{
    /// <summary>How a purchased item is expected to be turned back into CSFloat cash.</summary>
    public enum ExitRoute
    {
        None,
        /// <summary>Sell into Steam buy orders, then convert the Steam wallet back via the conversion model.</summary>
        SteamMarket,
        /// <summary>Relist on CSFloat once trade protection ends (no Steam fee, no wallet conversion).</summary>
        CSFloatRelist,
    }

    /// <summary>Thresholds and market assumptions. Built from OmniSettings by the service.</summary>
    public sealed class EvaluationSettings
    {
        public double MinimumSteamRoiPercent { get; set; } = 10;
        public double MinimumRelistRoiPercent { get; set; } = 10;
        public int MinimumProfitPence { get; set; } = 40;
        public int MinimumPriceCents { get; set; } = 200;
        public int MaximumPriceCents { get; set; } = int.MaxValue;

        /// <summary>Units of buy-order depth the Steam sale price is taken at.</summary>
        public int SteamDepthUnits { get; set; } = 3;
        /// <summary>Minimum total buy orders on Steam for the item to count as sellable.</summary>
        public int MinimumSteamBuyOrders { get; set; } = 15;
        /// <summary>Haircut on the Steam price for drift over the 7-day trade protection.</summary>
        public double SteamPriceHaircutPercent { get; set; } = 3;

        public bool AllowRelistExit { get; set; } = true;
        public double RelistUndercutPercent { get; set; } = 3;
        public double RelistPriceHaircutPercent { get; set; } = 2;
        public int MinimumRelistReferenceQuantity { get; set; } = 40;
        public double CSFloatSellerFee { get; set; } = 0.02;

        public bool RejectAwaySellers { get; set; } = true;
        public double MaximumSellerFailureRate { get; set; } = 0.05;
        public int MaximumSellerMedianTradeSeconds { get; set; } = 12 * 3600;
    }

    public sealed class ExitEstimate
    {
        public ExitRoute Route { get; set; }
        public bool Available { get; set; }
        public string? UnavailableReason { get; set; }
        /// <summary>Price the item is expected to sell at, in the venue's currency (Steam: GBP pence buyer-pays; CSFloat: USD cents).</summary>
        public int ExpectedSalePrice { get; set; }
        /// <summary>CSFloat-cash value (GBP pence) after every fee and, for Steam, the wallet conversion.</summary>
        public int NetCashPence { get; set; }
        public int ProfitPence { get; set; }
        public double Roi { get; set; }
        public bool MeetsThreshold { get; set; }

        public static ExitEstimate Unavailable(ExitRoute route, string reason) =>
            new() { Route = route, Available = false, UnavailableReason = reason, Roi = -1 };
    }

    public sealed class OpportunityEvaluation
    {
        public string ListingId { get; set; } = "";
        public string MarketHashName { get; set; } = "";
        public int PriceCents { get; set; }
        public int CostPence { get; set; }
        public double? FloatValue { get; set; }
        public DateTime ListingCreatedUtc { get; set; }
        public DateTime EvaluatedAtUtc { get; set; }
        public string Source { get; set; } = "";

        public double ConversionCoefficient { get; set; }
        public int SteamHighestBuyOrderPence { get; set; }
        public int SteamLowestSellOrderPence { get; set; }
        public int SteamBuyOrderCount { get; set; }
        public DateTime? SteamBookFetchedUtc { get; set; }

        /// <summary>Set when the Steam lookup was skipped: an upper bound from bulk prices already ruled it out.</summary>
        public double? SteamPrefilterUpperRoi { get; set; }

        public ExitEstimate Steam { get; set; } = ExitEstimate.Unavailable(ExitRoute.SteamMarket, "not evaluated");
        public ExitEstimate Relist { get; set; } = ExitEstimate.Unavailable(ExitRoute.CSFloatRelist, "not evaluated");

        public ExitRoute BestRoute { get; set; }
        public double BestRoi { get; set; } = -1;
        public int BestProfitPence { get; set; }
        /// <summary>True when the listing should be bought.</summary>
        public bool ShouldBuy { get; set; }
        /// <summary>Why it should (or should not) be bought — shown in alerts and the status route.</summary>
        public string Reason { get; set; } = "";

        // ── Full exit model (run on listings the screen above passes, right before buying) ──
        /// <summary>True once <see cref="ExitPlanner"/> has valued this listing with live market evidence.</summary>
        public bool ExitModelChecked { get; set; }
        /// <summary>The screen's best ROI before the exit model replaced it (BestRoi then holds the model's).</summary>
        public double ScreenBestRoi { get; set; }
        public double ExpectedDaysToSell { get; set; }
        public double SellProbability { get; set; }
        public int PlannedPrice { get; set; }
        public double CSFloatSalesPerDay { get; set; }
        public string? ExitModelSummary { get; set; }
        public List<string>? MissingSignals { get; set; }
        [Newtonsoft.Json.JsonIgnore]
        public ExitPlan? ExitPlan { get; set; }
    }

    public static class OpportunityEvaluator
    {
        /// <summary>
        /// The screen: values a CSFloat listing against both exits from what is already in hand (the listing and
        /// a cached Steam book), cheaply enough to run on every new listing. Listings it passes are confirmed by
        /// the full exit model (<see cref="ExitPlanner"/>) with live market evidence before any money is spent.
        /// Pure: every input is passed in, so it is fully unit-testable.
        /// </summary>
        public static OpportunityEvaluation Evaluate(CSFloatListing listing, SteamOrderBook? book, double conversionCoefficient,
            double gbpPerUsd, EvaluationSettings settings, DateTime nowUtc, string source = "")
        {
            int cost = ArbitrageMath.UsdCentsToPenceCeil(listing.PriceCents, gbpPerUsd);
            var evaluation = new OpportunityEvaluation
            {
                ListingId = listing.Id,
                MarketHashName = listing.MarketHashName,
                PriceCents = listing.PriceCents,
                CostPence = cost,
                FloatValue = listing.FloatValue,
                ListingCreatedUtc = listing.CreatedAtUtc,
                EvaluatedAtUtc = nowUtc,
                Source = source,
                ConversionCoefficient = conversionCoefficient,
                SteamHighestBuyOrderPence = book?.HighestBuyOrderPence ?? 0,
                SteamLowestSellOrderPence = book?.LowestSellOrderPence ?? 0,
                SteamBuyOrderCount = book?.BuyOrderCount ?? 0,
                SteamBookFetchedUtc = book?.FetchedAtUtc,
            };
            if (cost <= 0 || gbpPerUsd <= 0)
            {
                evaluation.Reason = "invalid price or exchange rate";
                return evaluation;
            }

            evaluation.Steam = EvaluateSteamExit(book, cost, conversionCoefficient, settings);
            evaluation.Relist = EvaluateRelistExit(listing, book, cost, gbpPerUsd, settings);

            var passing = new[] { evaluation.Steam, evaluation.Relist }.Where(e => e.MeetsThreshold).ToList();
            var bestForStats = new[] { evaluation.Steam, evaluation.Relist }
                .Where(e => e.Available).OrderByDescending(e => e.Roi).FirstOrDefault();
            var chosen = passing.OrderByDescending(e => e.ProfitPence).FirstOrDefault() ?? bestForStats;
            if (chosen != null)
            {
                evaluation.BestRoute = chosen.Route;
                evaluation.BestRoi = chosen.Roi;
                evaluation.BestProfitPence = chosen.ProfitPence;
            }
            evaluation.ScreenBestRoi = evaluation.BestRoi;

            string? gate = ListingGate(listing, settings);
            if (passing.Count == 0)
            {
                evaluation.Reason = chosen == null
                    ? $"no exit available ({evaluation.Steam.UnavailableReason}; {evaluation.Relist.UnavailableReason})"
                    : $"best {chosen.Route} ROI {chosen.Roi:P1} (profit £{chosen.ProfitPence / 100.0:F2}) below the bar " +
                      $"(≥{(chosen.Route == ExitRoute.SteamMarket ? settings.MinimumSteamRoiPercent : settings.MinimumRelistRoiPercent):F0}% and ≥£{settings.MinimumProfitPence / 100.0:F2})";
            }
            else if (gate != null)
            {
                evaluation.Reason = "profitable but rejected: " + gate;
            }
            else
            {
                evaluation.ShouldBuy = true;
                evaluation.Reason = $"{chosen!.Route} ROI {chosen.Roi:P1}, profit £{chosen.ProfitPence / 100.0:F2}";
            }
            return evaluation;
        }

        /// <summary>
        /// Replaces the screen's estimate with the exit model's verdict: the listing is bought only if the model's
        /// best exit clears that route's bar on its risk-adjusted present value (after time, fees, conversion,
        /// the chance it never sells, and price risk), and the seller gates still pass.
        /// </summary>
        public static void ApplyExitPlan(OpportunityEvaluation evaluation, CSFloatListing listing, ExitPlan plan, EvaluationSettings settings, IReadOnlyList<string>? missingSignals = null)
        {
            evaluation.ExitModelChecked = true;
            evaluation.ExitPlan = plan;
            evaluation.ExitModelSummary = plan.Rationale;
            evaluation.MissingSignals = missingSignals is { Count: > 0 } ? missingSignals.ToList() : null;
            evaluation.CSFloatSalesPerDay = plan.SalesPerDay;
            var best = plan.Best;
            if (best == null)
            {
                evaluation.ShouldBuy = false;
                evaluation.BestRoute = ExitRoute.None;
                evaluation.BestRoi = -1;
                evaluation.BestProfitPence = 0;
                evaluation.Reason = "exit model: " + plan.Rationale;
                return;
            }
            evaluation.BestRoute = best.Route;
            evaluation.BestRoi = plan.Roi;
            evaluation.BestProfitPence = plan.ProfitPence;
            evaluation.ExpectedDaysToSell = best.ExpectedDaysToSell;
            evaluation.SellProbability = best.SellProbability;
            evaluation.PlannedPrice = best.Price;

            double bar = best.Route == ExitRoute.SteamMarket ? settings.MinimumSteamRoiPercent : settings.MinimumRelistRoiPercent;
            bool clears = plan.Roi * 100 >= bar && plan.ProfitPence >= settings.MinimumProfitPence;
            string? gate = ListingGate(listing, settings);
            evaluation.ShouldBuy = clears && gate == null;
            evaluation.Reason = !clears
                ? $"exit model: best exit worth {plan.Roi:P1} (£{plan.ProfitPence / 100.0:F2}) after time, risk and fees, below the bar (≥{bar:F0}% and ≥£{settings.MinimumProfitPence / 100.0:F2}) — {plan.Rationale}"
                : gate != null ? "profitable but rejected: " + gate
                : $"exit model: {plan.Rationale} (ROI {plan.Roi:P1}, profit £{plan.ProfitPence / 100.0:F2} after time, risk and fees)";
        }

        /// <summary>Reasons a listing must not be bought regardless of price. Null when it is acceptable.</summary>
        public static string? ListingGate(CSFloatListing listing, EvaluationSettings settings)
        {
            if (!string.IsNullOrEmpty(listing.Type) && !listing.Type.Equals("buy_now", StringComparison.OrdinalIgnoreCase))
                return "not a buy-now listing";
            if (!string.IsNullOrEmpty(listing.State) && !listing.State.Equals("listed", StringComparison.OrdinalIgnoreCase))
                return $"listing state is {listing.State}";
            if (listing.PriceCents < settings.MinimumPriceCents) return "below minimum price";
            if (listing.PriceCents > settings.MaximumPriceCents) return "above spend limit";
            if (settings.RejectAwaySellers && listing.SellerAway) return "seller is away (trade would likely stall)";
            if (listing.SellerTotalTrades >= 20 && listing.SellerFailureRate > settings.MaximumSellerFailureRate)
                return $"seller failure rate {listing.SellerFailureRate:P0}";
            if (settings.MaximumSellerMedianTradeSeconds > 0 && listing.SellerMedianTradeTimeSeconds > settings.MaximumSellerMedianTradeSeconds)
                return $"seller median trade time {TimeSpan.FromSeconds(listing.SellerMedianTradeTimeSeconds):g}";
            return null;
        }

        public static ExitEstimate EvaluateSteamExit(SteamOrderBook? book, int costPence, double conversionCoefficient, EvaluationSettings settings)
        {
            const ExitRoute route = ExitRoute.SteamMarket;
            if (book == null) return ExitEstimate.Unavailable(route, "no Steam order book");
            if (!book.HasBuyOrders) return ExitEstimate.Unavailable(route, "no Steam buy orders");
            if (book.BuyOrderCount < settings.MinimumSteamBuyOrders) return ExitEstimate.Unavailable(route, $"only {book.BuyOrderCount} Steam buy orders");
            if (conversionCoefficient <= 0) return ExitEstimate.Unavailable(route, "no conversion coefficient");
            int depthPrice = book.PriceAtBuyDepth(settings.SteamDepthUnits);
            if (depthPrice <= 0) return ExitEstimate.Unavailable(route, "Steam book too thin");
            if (book.HasSellOrders && depthPrice > book.LowestSellOrderPence * 1.05)
                return ExitEstimate.Unavailable(route, "crossed Steam book (stale data)");

            // The epsilon keeps binary rounding (e.g. 1400 × 0.95 = 1329.9999…) from shaving a penny.
            int salePrice = (int)Math.Floor(depthPrice * (1 - settings.SteamPriceHaircutPercent / 100.0) + 1e-6);
            int sellerReceives = ArbitrageMath.SteamSellerReceives(salePrice);
            int netCash = (int)Math.Floor(sellerReceives * conversionCoefficient + 1e-9);
            var estimate = new ExitEstimate
            {
                Route = route,
                Available = true,
                ExpectedSalePrice = salePrice,
                NetCashPence = netCash,
                ProfitPence = netCash - costPence,
                Roi = netCash / (double)costPence - 1,
            };
            estimate.MeetsThreshold = estimate.Roi * 100 >= settings.MinimumSteamRoiPercent && estimate.ProfitPence >= settings.MinimumProfitPence;
            return estimate;
        }

        public static ExitEstimate EvaluateRelistExit(CSFloatListing listing, SteamOrderBook? book, int costPence, double gbpPerUsd, EvaluationSettings settings)
        {
            const ExitRoute route = ExitRoute.CSFloatRelist;
            if (!settings.AllowRelistExit) return ExitEstimate.Unavailable(route, "relist exit disabled");
            if (listing.BasePriceCents is not > 0) return ExitEstimate.Unavailable(route, "no CSFloat reference price");
            if ((listing.ReferenceQuantity ?? 0) < settings.MinimumRelistReferenceQuantity)
                return ExitEstimate.Unavailable(route, $"illiquid on CSFloat (ref qty {listing.ReferenceQuantity ?? 0})");

            // Never count a float/pattern premium we might not get paid for: value at the lower of the
            // generic market price and CSFloat's float-adjusted price for this exact item.
            int value = listing.BasePriceCents.Value;
            if (listing.PredictedPriceCents is > 0) value = Math.Min(value, listing.PredictedPriceCents.Value);

            // An extreme discount is either a fat-finger (the best snipe there is) or a broken reference.
            // Only trust it when Steam's ask corroborates the CSFloat value.
            if (listing.PriceCents < value * 0.6)
            {
                if (book == null || !book.HasSellOrders)
                    return ExitEstimate.Unavailable(route, "extreme discount without Steam corroboration");
                int valuePence = ArbitrageMath.UsdCentsToPenceFloor(value, gbpPerUsd);
                if (book.LowestSellOrderPence < valuePence * 0.8)
                    return ExitEstimate.Unavailable(route, "CSFloat reference disagrees with Steam");
            }

            double keep = 1 - (settings.RelistUndercutPercent + settings.RelistPriceHaircutPercent) / 100.0;
            int salePriceCents = (int)Math.Floor(value * keep + 1e-6);
            int netCents = ArbitrageMath.CSFloatNetProceedsCents(salePriceCents, settings.CSFloatSellerFee);
            int netPence = ArbitrageMath.UsdCentsToPenceFloor(netCents, gbpPerUsd);
            var estimate = new ExitEstimate
            {
                Route = route,
                Available = true,
                ExpectedSalePrice = salePriceCents,
                NetCashPence = netPence,
                ProfitPence = netPence - costPence,
                Roi = netPence / (double)costPence - 1,
            };
            estimate.MeetsThreshold = estimate.Roi * 100 >= settings.MinimumRelistRoiPercent && estimate.ProfitPence >= settings.MinimumProfitPence;
            return estimate;
        }

        /// <summary>
        /// Upper bound on the Steam-exit ROI from a cheap reference price (no order book). Used to skip
        /// the vast majority of listings — priced at market — without spending a Steam request on them.
        /// </summary>
        public static double OptimisticSteamRoi(int costPence, int steamReferencePence, double maxConversionCoefficient)
        {
            if (costPence <= 0 || steamReferencePence <= 0) return double.PositiveInfinity; // unknown → don't skip
            int receives = ArbitrageMath.SteamSellerReceives(steamReferencePence);
            return receives * maxConversionCoefficient / costPence - 1;
        }
    }
}
