using Omnipotent.Services.CS2ArbitrageBot.Steam;

namespace Omnipotent.Services.CS2ArbitrageBot.Engine
{
    /// <summary>Economic assumptions of the exit model (refreshed from OmniSettings by the service).</summary>
    public sealed class ExitModelSettings
    {
        /// <summary>Opportunity cost of capital per day: money tied up in an item cannot buy the next deal.</summary>
        public double CapitalCostPerDay { get; set; } = 0.002;
        /// <summary>γ in certainty-equivalent = E − (γ/2)·Var/E. 0 is risk-neutral.</summary>
        public double RiskAversion { get; set; } = 2.0;
        /// <summary>A relist still unsold after this many days falls back to the better quick exit.</summary>
        public double RelistHorizonDays { get; set; } = 21;
        /// <summary>Uncertainty of the conversion coefficient k across converters (their price risk over the hold is added separately).</summary>
        public double ConversionSigma { get; set; } = 0.04;
        /// <summary>When a purchase falls through (seller never sends, or reverses the trade), the money is back in about this long.</summary>
        public double FailedPurchaseRefundDays { get; set; } = 1;
        public double SellerFee { get; set; } = 0.02;
        /// <summary>A live listing is changed only when the alternative beats it by this fraction…</summary>
        public double MinimumImprovementFraction { get; set; } = 0.015;
        /// <summary>…and by at least this many pence (each change is an API call and resets the listing).</summary>
        public int MinimumImprovementPence { get; set; } = 5;
        /// <summary>At purchase the Steam exit is valued this deep into the buy book (a lone high order rarely survives the hold).</summary>
        public int SteamDepthUnitsAtPurchase { get; set; } = 3;
        public int MinimumSteamBuyOrders { get; set; } = 15;
    }

    /// <summary>Everything the planner needs about one unit and its markets.</summary>
    public sealed class ExitContext
    {
        public string MarketHashName { get; set; } = "";
        public int CostPence { get; set; }
        /// <summary>Days until the unit can be sold: ~8 at purchase (trade + protection), 0 once it is tradable.</summary>
        public double DaysUntilTradable { get; set; }
        /// <summary>CSFloat's value for this exact unit (float-adjusted when known), USD cents.</summary>
        public int AnchorCents { get; set; }
        public double GbpPerUsd { get; set; }
        public double ConversionCoefficient { get; set; }
        public SteamOrderBook? Book { get; set; }
        public PriceForecast Forecast { get; set; } = new();
        public CSFloatDemand? Demand { get; set; }
        public bool AllowRelist { get; set; } = true;
        /// <summary>Set when the unit is already listed on CSFloat at this price.</summary>
        public int? ListedPriceCents { get; set; }
        /// <summary>Sales the live listing was expected to have had by now (Σ hazard × time) without selling.</summary>
        public double ListedHazardExposure { get; set; }
        public ExitCalibrationSnapshot? Calibration { get; set; }
        /// <summary>Locks and holds (measured), the conversion route's risk over its hold, and account limits.</summary>
        public ExitEnvironment Environment { get; set; } = ExitEnvironment.Default;
        /// <summary>At purchase: the chance the trade never completes (the seller does not send, or reverses it inside protection).</summary>
        public double TradeFailureProbability { get; set; }
    }

    public sealed class ExitOption
    {
        public ExitRoute Route { get; set; }
        /// <summary>Steam: the buy-order price sold into, GBP pence. CSFloat: the listing price, USD cents.</summary>
        public int Price { get; set; }
        public bool IsCurrentListing { get; set; }
        public double ExpectedDaysToSell { get; set; }
        /// <summary>Probability of selling within the relist horizon (1 for the Steam exit).</summary>
        public double SellProbability { get; set; } = 1;
        /// <summary>Cash the sale itself brings, GBP pence, before time value (Steam: after conversion).</summary>
        public double NetIfSoldPence { get; set; }
        public double PresentValuePence { get; set; }
        public double StdDevPence { get; set; }
        public double CertaintyEquivalentPence { get; set; }
        public double ExpectedDaysToCash { get; set; }

        public string Describe(double gbpPerUsd) => Route == ExitRoute.SteamMarket
            ? $"Steam at £{Price / 100.0:F2} → £{NetIfSoldPence / 100.0:F2} after fees, conversion and the converters' 7-day hold, cash in ~{ExpectedDaysToCash:F0}d, worth £{CertaintyEquivalentPence / 100.0:F2} now"
            : $"CSFloat at ${Price / 100.0:F2}{(IsCurrentListing ? " (current)" : "")} → ~{FormatDays(ExpectedDaysToSell)} to sell, {SellProbability:P0} within the horizon, cash in ~{ExpectedDaysToCash:F0}d after the buyer's protection, worth £{CertaintyEquivalentPence / 100.0:F2} now";

        internal static string FormatDays(double days) => days < 1 ? $"{days * 24:F0}h" : days >= 365 ? "never" : $"{days:F1}d";
    }

    public sealed class ExitPlan
    {
        public DateTime ComputedAtUtc { get; set; }
        public int CostPence { get; set; }
        public List<ExitOption> Options { get; set; } = new();
        /// <summary>The option to act on (for a live listing: the current one unless an alternative clears the hysteresis).</summary>
        public ExitOption? Best { get; set; }
        public string Rationale { get; set; } = "";

        // Diagnostics recorded with every decision (the evidence behind it).
        /// <summary>Days until the unit can be sold (0 once tradable): handover + Valve's protection to its daily boundary.</summary>
        public double DaysUntilTradable { get; set; }
        public double SalesPerDay { get; set; }
        public string SalesRateBasis { get; set; } = "";
        public double DaysSinceLastCSFloatSale { get; set; } = double.PositiveInfinity;
        public double MedianValueRatio { get; set; }
        public double AnchorAtTradableCents { get; set; }
        public double SteamBidAtTradablePence { get; set; }
        public double SteamSigmaAtTradable { get; set; }
        public double DemandMultiplier { get; set; } = 1;

        public ExitOption? BestSteam => Options.Where(o => o.Route == ExitRoute.SteamMarket).MaxBy(o => o.CertaintyEquivalentPence);
        public ExitOption? BestRelist => Options.Where(o => o.Route == ExitRoute.CSFloatRelist).MaxBy(o => o.CertaintyEquivalentPence);
        public ExitOption? CurrentListing => Options.FirstOrDefault(o => o.IsCurrentListing);
        public double Roi => CostPence > 0 && Best != null ? Best.CertaintyEquivalentPence / CostPence - 1 : -1;
        public int ProfitPence => Best == null ? 0 : (int)Math.Floor(Best.CertaintyEquivalentPence - CostPence);
    }

    /// <summary>
    /// Chooses how to turn an item back into CSFloat cash, valuing every exit on one footing: the certainty
    /// equivalent (risk-adjusted present value, GBP pence) of the spendable CSFloat cash it produces.
    ///
    /// <list type="bullet">
    /// <item><b>Steam now</b>: sell into the buy book, then carry the wallet back through converters (×k, ~16 days).</item>
    /// <item><b>CSFloat at price p</b>, for a grid of prices around the item's value, recent sale prices and the
    /// competing listings: sells after an expected T(p) days (<see cref="CSFloatDemand"/>), with the market
    /// drifting while it waits; if it has not sold within the horizon it falls back to the better quick exit then.</item>
    /// </list>
    /// Price risk comes from <see cref="PriceForecast"/>, timing from the demand model, and both are corrected by
    /// the bot's own track record (<see cref="ExitCalibrationSnapshot"/>). Pure and deterministic, so the
    /// decisions that spend and recover money are unit-tested.
    /// </summary>
    public static class ExitPlanner
    {
        private static readonly double[] GridRatios = { 0.80, 0.84, 0.87, 0.90, 0.92, 0.94, 0.95, 0.96, 0.97, 0.98, 0.99, 1.00, 1.01, 1.02, 1.03, 1.05, 1.08, 1.10 };
        private static readonly (double Z, double Weight)[] Quadrature = { (-Math.Sqrt(3), 1 / 6.0), (0, 2 / 3.0), (Math.Sqrt(3), 1 / 6.0) };
        public const double MaxListingRatio = 1.15;

        public static ExitPlan Plan(ExitContext ctx, ExitModelSettings s, DateTime nowUtc)
        {
            var plan = new ExitPlan { ComputedAtUtc = nowUtc, CostPence = ctx.CostPence };
            // Nothing can be sold before the unit is tradable: every exit starts after the handover and Valve's
            // trade protection (which ends on a daily boundary), and is valued at the prices expected by then.
            double d = Math.Max(0, ctx.DaysUntilTradable);
            plan.DaysUntilTradable = d;
            var calibration = ctx.Calibration ?? ExitCalibrationSnapshot.Neutral;
            bool atPurchase = d > 0;

            // Steam: the bid when the unit becomes sellable.
            double steamGrowth = ctx.Forecast.Growth(d) * (atPurchase ? Math.Exp(calibration.SteamForecastBiasLog) : 1);
            int bidNow = SteamBidNow(ctx, s, atPurchase, out string? steamProblem);
            plan.SteamSigmaAtTradable = ctx.Forecast.Sigma(d);
            if (bidNow > 0)
            {
                plan.SteamBidAtTradablePence = bidNow * steamGrowth;
                plan.Options.Add(SteamOption(ctx, s, d, bidNow * steamGrowth, ctx.Forecast.Sigma(d)));
            }

            var demand = ctx.Demand;
            bool relistBlocked = ctx.Environment.CSFloatSellingPaused;
            if (ctx.AllowRelist && !relistBlocked && demand != null && ctx.AnchorCents > 0 && ctx.GbpPerUsd > 0)
            {
                double anchorGrowth = ctx.Forecast.Growth(d) * (atPurchase ? Math.Exp(calibration.CSFloatForecastBiasLog) : 1);
                double anchorT = ctx.AnchorCents * anchorGrowth;
                double prior = PriorDemand(calibration);
                plan.AnchorAtTradableCents = anchorT;
                plan.DemandMultiplier = DemandMultiplier(prior, ctx.ListedHazardExposure); // at the listed price
                plan.SalesPerDay = demand.SalesPerDay;
                plan.SalesRateBasis = demand.SalesRateBasis;
                plan.DaysSinceLastCSFloatSale = demand.DaysSinceLastSale;
                plan.MedianValueRatio = demand.MedianValueRatio;

                double fallback = FallbackValue(ctx, s, d, bidNow, anchorT, demand, prior);
                foreach (int price in CandidatePrices(ctx, demand, anchorT))
                    plan.Options.Add(RelistOption(ctx, s, d, price, anchorT, demand, prior, fallback, price == ctx.ListedPriceCents));
            }

            if (atPurchase && ctx.TradeFailureProbability > 0 && ctx.CostPence > 0)
                foreach (var option in plan.Options) ApplyTradeFailure(option, ctx, s);

            plan.Options = plan.Options.OrderByDescending(o => o.CertaintyEquivalentPence).ToList();
            string? relistProblem = !ctx.AllowRelist ? "relisting disabled"
                : relistBlocked ? "CSFloat selling paused (account away)"
                : demand == null || ctx.AnchorCents <= 0 ? "no CSFloat value for this unit" : null;
            Choose(plan, ctx, s, steamProblem, relistProblem);
            return plan;
        }

        /// <summary>
        /// A purchase can fall through before any exit: the seller never sends, or reverses the trade inside the
        /// 7-day protection. CSFloat refunds (it holds the seller's money until protection ends), so that branch
        /// returns the cost a little later and no profit.
        /// </summary>
        private static void ApplyTradeFailure(ExitOption option, ExitContext ctx, ExitModelSettings s)
        {
            double p = Math.Clamp(ctx.TradeFailureProbability, 0, 1);
            double refund = ctx.CostPence * Math.Exp(-s.CapitalCostPerDay * s.FailedPurchaseRefundDays);
            double pv = (1 - p) * option.PresentValuePence + p * refund;
            double variance = (1 - p) * option.StdDevPence * option.StdDevPence + p * (1 - p) * Math.Pow(option.PresentValuePence - refund, 2);
            option.PresentValuePence = pv;
            option.StdDevPence = Math.Sqrt(Math.Max(0, variance));
            option.CertaintyEquivalentPence = pv > 0 ? pv - s.RiskAversion / 2 * variance / pv : pv;
        }

        private static int SteamBidNow(ExitContext ctx, ExitModelSettings s, bool atPurchase, out string? problem)
        {
            problem = null;
            var book = ctx.Book;
            if (book == null) { problem = "no Steam order book"; return 0; }
            if (!book.HasBuyOrders) { problem = "no Steam buy orders"; return 0; }
            if (atPurchase && book.BuyOrderCount < s.MinimumSteamBuyOrders) { problem = $"only {book.BuyOrderCount} Steam buy orders"; return 0; }
            if (ctx.ConversionCoefficient <= 0) { problem = "no conversion coefficient"; return 0; }
            int bid = atPurchase ? book.PriceAtBuyDepth(s.SteamDepthUnitsAtPurchase) : book.HighestBuyOrderPence;
            if (bid <= 0) { problem = "Steam book too thin"; return 0; }
            if (book.HasSellOrders && bid > book.LowestSellOrderPence * 1.05) { problem = "crossed Steam book (stale data)"; return 0; }
            // Steam refuses a Market listing that would take the wallet past its cap, or above its price limit.
            var env = ctx.Environment;
            if (env.SteamWalletHeadroomPence is double headroom && bid > headroom) { problem = $"the Steam wallet is near its cap (room for £{Math.Max(0, headroom) / 100:F2})"; return 0; }
            if (env.SteamMaxListingPence is double max && bid > max) { problem = "above Steam's Market listing limit"; return 0; }
            return bid;
        }

        private static ExitOption SteamOption(ExitContext ctx, ExitModelSettings s, double d, double bidAtTradable, double sigma)
        {
            var env = ctx.Environment;
            int bid = (int)Math.Floor(bidAtTradable + 1e-6);
            // The wallet comes back through converter items, which Steam holds for 7 days before they can go to a
            // CSFloat buyer: their expected drift and price risk over that hold are part of this exit.
            double cash = ArbitrageMath.SteamSellerReceives(bid) * ctx.ConversionCoefficient * env.ConverterHoldGrowth;
            double days = d + env.Timeline.SteamSaleToCashDays;
            double pv = cash * Math.Exp(-s.CapitalCostPerDay * days);
            double k = Math.Max(0.05, ctx.ConversionCoefficient);
            double relativeVariance = sigma * sigma + Math.Pow(s.ConversionSigma / k, 2) + env.ConverterHoldSigma * env.ConverterHoldSigma;
            return new ExitOption
            {
                Route = ExitRoute.SteamMarket,
                Price = bid,
                NetIfSoldPence = cash,
                PresentValuePence = pv,
                StdDevPence = pv * Math.Sqrt(relativeVariance),
                CertaintyEquivalentPence = pv * (1 - s.RiskAversion / 2 * relativeVariance),
                ExpectedDaysToCash = days,
            };
        }

        /// <summary>Shape of the Gamma prior on demand relative to the model: CV 1/√2, i.e. the model could easily be 2× off.</summary>
        public const double DemandShape = 2;

        /// <summary>The prior mean of demand relative to the model, from the bot's own track record (1 without one).</summary>
        public static double PriorDemand(ExitCalibrationSnapshot calibration) => 1 / Math.Clamp(calibration.RelistTimeMultiplier, 0.25, 6);

        /// <summary>
        /// Posterior mean demand (relative to the model) at a price, after a live listing sat through
        /// <paramref name="exposure"/> expected sales without one: Gamma(α, α/prior) prior on a Poisson rate →
        /// α / (α/prior + exposure).
        /// </summary>
        public static double DemandMultiplier(double prior, double exposure) => DemandShape / (DemandShape / prior + Math.Max(0, exposure));

        public static double DemandMultiplier(ExitContext ctx, ExitCalibrationSnapshot calibration) =>
            DemandMultiplier(PriorDemand(calibration), ctx.ListedHazardExposure);

        /// <summary>
        /// How much of a listing's no-sale evidence bears on another price. Not selling at the listed price says
        /// buyers will not pay that much: it applies fully at or above it and fades to nothing at the clearing
        /// price recent buyers demonstrably paid (the lower quartile of recent sales, at most 0.97 of value).
        /// </summary>
        public static double EvidenceWeight(CSFloatDemand demand, double listedRatio, double ratio)
        {
            double clear = Math.Min(demand.LowerQuartileValueRatio, 0.97);
            if (ratio >= listedRatio) return 1;
            if (listedRatio <= clear) return 0;
            return Math.Clamp((ratio - clear) / (listedRatio - clear), 0, 1);
        }

        /// <summary>
        /// E[e^{−rτ}; τ &lt; H] for the Gamma–Poisson (Lomax) wait. Integrated over the CDF, u = F(τ), where the
        /// integrand is smooth even when nearly all the mass sits in the first minutes (cases).
        /// </summary>
        internal static double DiscountedSaleProbability(double hazard, double beta, double horizon, double r)
        {
            double sell = 1 - Math.Pow(beta / (beta + hazard * horizon), DemandShape);
            if (sell <= 0) return 0;
            if (r <= 0) return sell;
            const int n = 32;
            double step = sell / n, sum = 0;
            for (int i = 0; i <= n; i++)
            {
                double u = Math.Min(i * step, sell);
                double t = beta / hazard * (Math.Pow(1 - u, -1 / DemandShape) - 1);
                sum += (i == 0 || i == n ? 1 : i % 2 == 1 ? 4 : 2) * Math.Exp(-r * t);
            }
            return sum * step / 3;
        }

        private static ExitOption RelistOption(ExitContext ctx, ExitModelSettings s, double d, int priceCents, double anchorT,
            CSFloatDemand demand, double priorDemand, double fallbackPv, bool isCurrent)
        {
            var f = ctx.Forecast;
            double horizon = s.RelistHorizonDays;
            double r = s.CapitalCostPerDay;
            double ratio = priceCents / anchorT;
            double net = ArbitrageMath.CSFloatNetProceedsCents(priceCents, s.SellerFee) * ctx.GbpPerUsd;
            // A sale only becomes cash after the buyer's 7-day protection runs to its daily boundary and CSFloat
            // verifies the trade (measured); a sale the account fails to send in time is cancelled, so it doesn't count.
            var timeline = ctx.Environment.Timeline;
            double settle = Math.Exp(-r * (d + timeline.CSFloatSaleToCashDays));
            double completes = 1 - Math.Clamp(timeline.SaleCancelRate, 0, 0.5);

            // Demand is uncertain (and a live listing's unsold time is evidence against it at its price), so the
            // wait is a Gamma–Poisson mixture: P(τ > t) = (β / (β + h·t))^α, heavier-tailed than an exponential.
            double exposure = ctx.ListedPriceCents is int listed && ctx.ListedHazardExposure > 0
                ? ctx.ListedHazardExposure * EvidenceWeight(demand, listed / anchorT, ratio) * priorDemand
                : 0;
            double beta = DemandShape + exposure;

            // The price is fixed while the market moves. Value the listing in three market scenarios (3-point
            // Gauss–Hermite over where the level may be halfway through the wait) and mix the *outcomes*: mixing
            // the sale rates instead lets the rare "market jumped" scenario make an overpriced listing look quick.
            double mid = Math.Min(demand.ExpectedDaysToSell(ratio, priorDemand), horizon) / 2;
            var hazards = new double[Quadrature.Length];
            var values = new double[Quadrature.Length];
            double sell = 0, pv = 0, within = 0;
            for (int q = 0; q < Quadrature.Length; q++)
            {
                var (z, w) = Quadrature[q];
                double movedRatio = ratio * Math.Exp(-f.ExpectedLogChange(mid) - f.Sigma(mid) * z);
                double hazard = Math.Max(completes / demand.ExpectedDaysToSell(movedRatio, priorDemand), 1 / CSFloatDemand.MaxDaysToSell);
                double sellQ = 1 - Math.Pow(beta / (beta + hazard * horizon), DemandShape);
                double pvSoldQ = net * DiscountedSaleProbability(hazard, beta, horizon, r) * settle;
                double valueQ = pvSoldQ + (1 - sellQ) * fallbackPv;
                double soldValueQ = sellQ > 1e-9 ? pvSoldQ / sellQ : 0;
                within += w * (sellQ * (1 - sellQ) * Math.Pow(soldValueQ - fallbackPv, 2) + (1 - sellQ) * Math.Pow(fallbackPv * f.Sigma(d + horizon), 2));
                hazards[q] = hazard;
                values[q] = valueQ;
                sell += w * sellQ;
                pv += w * valueQ;
            }
            double between = 0;
            for (int q = 0; q < Quadrature.Length; q++) between += Quadrature[q].Weight * Math.Pow(values[q] - pv, 2);
            // At purchase the whole relist also scales with where the level is once the unit becomes tradable.
            double variance = within + between + Math.Pow(pv * f.Sigma(d), 2);
            double ce = pv > 0 ? pv - s.RiskAversion / 2 * variance / pv : pv;
            double medianDays = MedianWait(hazards, beta);
            return new ExitOption
            {
                Route = ExitRoute.CSFloatRelist,
                Price = priceCents,
                IsCurrentListing = isCurrent,
                ExpectedDaysToSell = medianDays,
                SellProbability = sell,
                NetIfSoldPence = net,
                PresentValuePence = pv,
                StdDevPence = Math.Sqrt(Math.Max(0, variance)),
                CertaintyEquivalentPence = ce,
                ExpectedDaysToCash = d + Math.Min(medianDays, horizon) + timeline.CSFloatSaleToCashDays,
            };
        }

        /// <summary>The median wait of the scenario mixture: P(sold by t) = ½, by bisection on log t.</summary>
        private static double MedianWait(double[] hazards, double beta)
        {
            double Unsold(double t)
            {
                double u = 0;
                for (int q = 0; q < hazards.Length; q++) u += Quadrature[q].Weight * Math.Pow(beta / (beta + hazards[q] * t), DemandShape);
                return u;
            }
            double lo = Math.Log(1e-4), hi = Math.Log(1e4);
            if (Unsold(Math.Exp(hi)) > 0.5) return Math.Exp(hi);
            for (int i = 0; i < 50; i++)
            {
                double m = (lo + hi) / 2;
                if (Unsold(Math.Exp(m)) > 0.5) lo = m; else hi = m;
            }
            return Math.Exp((lo + hi) / 2);
        }

        /// <summary>
        /// Value (now) of what happens if a relist is still unsold at the horizon: the better of selling into
        /// Steam's buy book then, or listing at a clearing price against demand that has proven weaker.
        /// </summary>
        private static double FallbackValue(ExitContext ctx, ExitModelSettings s, double d, int bidNow, double anchorT, CSFloatDemand demand, double priorDemand)
        {
            var f = ctx.Forecast;
            double horizon = s.RelistHorizonDays;
            double r = s.CapitalCostPerDay;
            double steam = 0;
            if (bidNow > 0)
            {
                int bid = (int)Math.Floor(bidNow * f.Growth(d + horizon));
                steam = ArbitrageMath.SteamSellerReceives(bid) * ctx.ConversionCoefficient * ctx.Environment.ConverterHoldGrowth
                        * Math.Exp(-r * (d + horizon + ctx.Environment.Timeline.SteamSaleToCashDays));
            }
            double clearRatio = Math.Min(demand.LowerQuartileValueRatio, 0.97);
            double clearDays = demand.ExpectedDaysToSell(clearRatio, priorDemand * 0.5); // it has just failed to sell for the whole horizon
            int clearPrice = (int)Math.Floor(anchorT * f.Growth(horizon) * clearRatio);
            double clear = ArbitrageMath.CSFloatNetProceedsCents(clearPrice, s.SellerFee) * ctx.GbpPerUsd
                           * Math.Exp(-r * (d + horizon + clearDays + ctx.Environment.Timeline.CSFloatSaleToCashDays))
                           * (1 - Math.Exp(-2 * horizon / clearDays)); // whatever has not sold after that is written off
            return Math.Max(steam, 0.9 * clear);
        }

        private static IEnumerable<int> CandidatePrices(ExitContext ctx, CSFloatDemand demand, double anchorT)
        {
            var ratios = new List<double>(GridRatios);
            if (demand.ValueRatios.Count > 0)
                foreach (double q in new[] { 0.1, 0.25, 0.5, 0.75, 0.9 }) ratios.Add(CSFloatDemand.Quantile(demand.ValueRatios, q));
            var prices = ratios.Select(x => (int)Math.Floor(anchorT * x)).ToList();
            // One cent under each of the cheapest competitors (at their value ratio, for skins).
            foreach (double c in demand.CompetitorRatios.OrderBy(c => c).Take(6)) prices.Add((int)Math.Floor(anchorT * c) - 1);
            if (ctx.ListedPriceCents is int listed) prices.Add(listed);
            int cap = (int)Math.Floor(anchorT * MaxListingRatio);
            return prices.Where(p => p >= 3 && (p <= cap || p == ctx.ListedPriceCents)).Distinct().OrderBy(p => p);
        }

        private static void Choose(ExitPlan plan, ExitContext ctx, ExitModelSettings s, string? steamProblem, string? relistProblem)
        {
            var best = plan.Options.FirstOrDefault();
            if (best == null)
            {
                plan.Rationale = "no exit can be valued (" + (steamProblem ?? "Steam unavailable") + "; " + (relistProblem ?? "no CSFloat value") + ")";
                return;
            }
            var current = plan.CurrentListing;
            if (current != null && best != current)
            {
                double gain = best.CertaintyEquivalentPence - current.CertaintyEquivalentPence;
                double needed = Math.Max(s.MinimumImprovementPence, s.MinimumImprovementFraction * Math.Max(0, current.CertaintyEquivalentPence));
                if (gain < needed)
                {
                    plan.Best = current;
                    plan.Rationale = $"keep the listing: the best alternative ({best.Describe(ctx.GbpPerUsd)}) gains only £{gain / 100.0:F2}";
                    return;
                }
            }
            plan.Best = best;
            var steam = plan.BestSteam;
            var relist = plan.BestRelist;
            string versus = best.Route == ExitRoute.SteamMarket
                ? relist == null ? $"no relist ({relistProblem ?? "can't be valued"})" : $"best relist: {relist.Describe(ctx.GbpPerUsd)}"
                : steam == null ? $"Steam unavailable ({steamProblem})" : $"Steam: {steam.Describe(ctx.GbpPerUsd)}";
            plan.Rationale = $"{best.Describe(ctx.GbpPerUsd)}; {versus}";
        }
    }
}
