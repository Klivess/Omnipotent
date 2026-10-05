using Omnipotent.Services.CS2ArbitrageBot.CSFloat;
using static Omnipotent.Services.CS2ArbitrageBot.CS2ArbitrageBotLabs.Scanalytics;

namespace Omnipotent.Services.CS2ArbitrageBot.Engine
{
    /// <summary>What the exit manager needs from its owning service. Implemented by CS2ArbitrageBot (and fakes in tests).</summary>
    public interface IExitHost
    {
        ExitSignalsProvider ExitSignals { get; }
        EngineSettings Settings { get; }
        double GbpPerUsd { get; }
        double ConversionCoefficient { get; }
        MarketDriftModel MarketModel { get; }
        ExitCalibrationSnapshot ExitCalibration { get; }
        /// <summary>Trade locks and holds (measured), the conversion route's hold risk, account limits and away mode.</summary>
        ExitEnvironment ExitEnvironment { get; }
        string? OwnSteamId { get; }
        /// <summary>Lists the unit on the Steam market (into the buy order); false when it failed.</summary>
        Task<bool> SellOnSteamAsync(PurchasedListing position, int pricePence);
        /// <summary>Lists the unit on CSFloat; null when it is not yet tradable in the CSFloat inventory.</summary>
        Task<string?> ListOnCSFloatAsync(PurchasedListing position, int priceCents, CancellationToken ct);
        Task<bool> RepriceOnCSFloatAsync(PurchasedListing position, int priceCents, CancellationToken ct);
        Task DelistFromCSFloatAsync(PurchasedListing position, CancellationToken ct);
        Task SaveAsync(PurchasedListing position);
        Task NotifyAsync(PurchasedListing position, string title, string message);
        Task AlertAsync(string key, string title, string message);
        void Log(string message);
    }

    /// <summary>
    /// Turns positions back into CSFloat cash with the exit model: picks the exit when trade protection lifts,
    /// then keeps re-checking a CSFloat relist (re-pricing it, or switching to Steam) as evidence arrives.
    /// </summary>
    public sealed class ExitManager
    {
        public const int MaxSaleDeferrals = 8;
        public static readonly TimeSpan DeferralDelay = TimeSpan.FromHours(1);
        /// <summary>CSFloat's inventory can lag the unlock; look again this soon rather than after the 6-hour retry.</summary>
        public static readonly TimeSpan NotTradableRetry = TimeSpan.FromMinutes(30);
        /// <summary>While the CSFloat account is away, check back this often.</summary>
        public static readonly TimeSpan PausedRetry = TimeSpan.FromHours(6);
        public const int MaxRelistChanges = 10;
        /// <summary>A relist that sat through this many expected sales without one gets a person's attention.</summary>
        public const double OverdueExpectedSales = 8;
        public static readonly TimeSpan MinTimeBetweenChanges = TimeSpan.FromHours(6);

        private readonly IExitHost host;
        private readonly Func<DateTime> utcNow;

        public ExitManager(IExitHost host, Func<DateTime>? utcNow = null)
        {
            this.host = host;
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>
        /// Decides and executes the exit for a position whose trade protection has lifted. Returns what happened
        /// ("steam", "relist", "deferred", "not tradable", "unsellable" or "failed").
        /// </summary>
        public async Task<string> RunSaleAsync(PurchasedListing p, CancellationToken ct)
        {
            var settings = host.Settings;
            var signals = await host.ExitSignals.GetAsync(p.ItemMarketHashName, SignalUse.Sale, ct);

            // The old scheduler sold on Steam whenever the relist could not be priced, e.g. because the
            // listing search budget was spent. Without CSFloat evidence the decision waits instead.
            bool relistPossible = settings.Evaluation.AllowRelistExit && host.GbpPerUsd > 0;
            if (relistPossible && (!signals.HasCSFloatDemand || signals.Competitors == null) && p.SaleDeferrals < MaxSaleDeferrals)
            {
                p.SaleDeferrals++;
                p.SaleDeferredUntilUtc = utcNow() + DeferralDelay;
                await host.SaveAsync(p);
                host.Log($"Sale of {p.ItemMarketHashName} postponed ({p.SaleDeferrals}/{MaxSaleDeferrals}): {string.Join("; ", signals.Missing)}");
                return "deferred";
            }

            DateTime now = utcNow();
            var environment = host.ExitEnvironment;
            var (plan, demand, anchor) = PlanFor(p, signals, listedPrice: null, exposure: 0, environment);
            if (environment.CSFloatSellingPaused && relistPossible)
            {
                // The CSFloat account is away, so a relist cannot sell now. Sell on Steam only if it would win anyway;
                // otherwise wait for the account to come back rather than dump the item on Steam.
                var (unpaused, _, _) = PlanFor(p, signals, listedPrice: null, exposure: 0, environment with { CSFloatSellingPaused = false });
                if (unpaused.Best?.Route == ExitRoute.CSFloatRelist)
                {
                    p.SaleDeferredUntilUtc = now + PausedRetry;
                    await host.SaveAsync(p);
                    host.Log($"Sale of {p.ItemMarketHashName} waits: CSFloat selling is paused (away) and the relist is the better exit — {unpaused.Rationale}");
                    return "paused";
                }
            }

            var best = plan.Best;
            if (best == null)
            {
                p.SaleAttempts++;
                p.LastSaleAttemptUtc = now;
                await host.SaveAsync(p);
                await host.AlertAsync("unsellable:" + p.CSFloatListingID, "CS2 Arbitrage — can't price a sale",
                    $"**{p.ItemMarketHashName}**: {plan.Rationale}. Retrying in 6 hours.");
                return "unsellable";
            }

            int depthBid = signals.Book?.PriceAtBuyDepth(3) ?? 0;
            if (best.Route == ExitRoute.SteamMarket)
            {
                p.SaleAttempts++;
                p.LastSaleAttemptUtc = now;
                p.RecordExitDecision(ExitDecision.From(plan, "sale", "steam", depthBid, anchor));
                await host.SaveAsync(p);
                return await host.SellOnSteamAsync(p, best.Price) ? "steam" : "failed";
            }

            string? listingId = await host.ListOnCSFloatAsync(p, best.Price, ct);
            if (listingId == null)
            {
                // CSFloat still sees the item inside Valve's trade protection (its inventory lags the unlock, or a new
                // lock appeared). Look again shortly, and don't count it as a failed sale.
                p.SaleDeferredUntilUtc = now + NotTradableRetry;
                await host.SaveAsync(p);
                if (p.PredictedTimeToBeResoldOnSteam != default && now - p.PredictedTimeToBeResoldOnSteam.ToUniversalTime() > TimeSpan.FromDays(1))
                    await host.AlertAsync("still-locked:" + p.CSFloatListingID, "CS2 Arbitrage — item still trade-locked",
                        $"**{p.ItemMarketHashName}** should have unlocked at {p.PredictedTimeToBeResoldOnSteam.ToUniversalTime():dd/MM HH:mm} UTC, but CSFloat still shows it as not tradable. " +
                        "A new lock (a trade reversal, or a Steam trade/Market restriction on the account)?");
                return "not tradable";
            }
            p.SaleAttempts++;
            p.LastSaleAttemptUtc = now;
            p.RecordExitDecision(ExitDecision.From(plan, "sale", "relist", depthBid, anchor));
            StartRelistEpisode(p, listingId, best.Price, demand!, anchor);
            await host.SaveAsync(p);
            await host.NotifyAsync(p, "CS2 item relisted on CSFloat",
                $"**{p.ItemMarketHashName}** listed at **${best.Price / 100.0:F2}**.\n{Explain(plan)}\nYou'll be told when it sells.");
            return "relist";
        }

        /// <summary>
        /// Re-checks a live relist: keep it, re-price it, or take it down and sell on Steam. Every unsold hour is
        /// evidence that demand is weaker than modelled, so a listing that does not sell drifts towards a price
        /// (or exit) that does. Returns "keep", "reprice", "steam", "cooldown", "capped", "deferred" or "skip".
        /// </summary>
        public async Task<string> ReviewListingAsync(PurchasedListing p, CancellationToken ct)
        {
            if (p.CurrentStrategicStage != StrategicStages.WaitingForCSFloatResale || string.IsNullOrEmpty(p.CSFloatResaleListingID)) return "skip";
            if (p.ResaleSoldAtUtc != default || p.LastTradeState is "resale:queued" or "resale:pending") return "skip"; // a buyer has it
            DateTime now = utcNow();
            var environment = host.ExitEnvironment;
            if (environment.CSFloatSellingPaused)
            {
                // Away: the listing is hidden, so unsold time is no evidence against its price, and nothing should change.
                p.RelistExposureUpdatedUtc = now;
                p.NextExitReviewUtc = now + PausedRetry;
                await host.SaveAsync(p);
                return "paused";
            }
            AccrueExposure(p, now);
            if (p.RelistTotalExposure >= OverdueExpectedSales)
            {
                // The market keeps buying this item but not ours: usually the listing itself (hidden, wrong item,
                // trade hold) rather than the price. That needs a person, not another re-price.
                await host.AlertAsync("relist-overdue:" + p.CSFloatListingID, "CS2 Arbitrage — relist overdue",
                    $"**{p.ItemMarketHashName}** has been listed {(now - p.ListedOnCSFloatAtUtc).TotalDays:F1} days (now ${p.CSFloatResalePriceCents / 100.0:F2}) " +
                    $"and should have sold ~{p.RelistTotalExposure:F0} times by now at the rates CSFloat shows. Check the listing is live and visible: https://csfloat.com/item/{p.CSFloatResaleListingID}");
            }

            var signals = await host.ExitSignals.GetAsync(p.ItemMarketHashName, SignalUse.Review, ct);
            if (!signals.HasCSFloatDemand || signals.Competitors == null)
            {
                // Never touch a live listing on missing data (the proxies are pessimistic); look again soon.
                p.NextExitReviewUtc = now.AddHours(1);
                await host.SaveAsync(p);
                host.Log($"Relist review of {p.ItemMarketHashName} postponed: {string.Join("; ", signals.Missing)}");
                return "deferred";
            }
            var (plan, demand, anchor) = PlanFor(p, signals, p.CSFloatResalePriceCents, p.RelistModelExposure, environment);
            var best = plan.Best;
            int depthBid = signals.Book?.PriceAtBuyDepth(3) ?? 0;
            string outcome;

            if (best == null || best.IsCurrentListing || demand == null)
            {
                outcome = "keep";
            }
            else if (now - p.LastRelistChangeUtc < MinTimeBetweenChanges)
            {
                outcome = "cooldown";
            }
            else if (p.RelistRepriceCount >= MaxRelistChanges)
            {
                outcome = "capped";
                await host.AlertAsync("relist-capped:" + p.CSFloatListingID, "CS2 Arbitrage — relist needs a look",
                    $"**{p.ItemMarketHashName}** has been re-priced {p.RelistRepriceCount} times without selling. {plan.Rationale}");
            }
            else if (!host.Settings.AutoManageRelists)
            {
                outcome = "keep";
                await host.AlertAsync("relist-advice:" + p.CSFloatListingID, "CS2 Arbitrage — relist advice",
                    $"**{p.ItemMarketHashName}** (listed at ${p.CSFloatResalePriceCents / 100.0:F2}): the model would act — {plan.Rationale}. Automatic re-pricing is off.");
            }
            else if (best.Route == ExitRoute.CSFloatRelist)
            {
                int from = p.CSFloatResalePriceCents;
                if (!await host.RepriceOnCSFloatAsync(p, best.Price, ct)) return "failed";
                p.RelistRepriceCount++;
                // Only the part of the no-sale evidence that bears on the new price carries over.
                if (anchor > 0) p.RelistModelExposure *= ExitPlanner.EvidenceWeight(demand, from / (double)anchor, best.Price / (double)anchor);
                p.CSFloatResalePriceCents = best.Price;
                p.LastRelistChangeUtc = now;
                host.ExitSignals.InvalidateCompetition(p.ItemMarketHashName);
                outcome = "reprice";
                await host.NotifyAsync(p, "CS2 relist re-priced",
                    $"**{p.ItemMarketHashName}**: ${from / 100.0:F2} → **${best.Price / 100.0:F2}** after {(now - p.ListedOnCSFloatAtUtc).TotalDays:F1} days unsold.\n{Explain(plan)}");
            }
            else
            {
                await host.DelistFromCSFloatAsync(p, ct);
                p.CSFloatResaleListingID = "";
                p.CurrentStrategicStage = StrategicStages.JustRetrieved;
                p.LastRelistChangeUtc = now;
                host.ExitSignals.InvalidateCompetition(p.ItemMarketHashName);
                p.RecordExitDecision(ExitDecision.From(plan, "review", "delist+steam", depthBid, anchor));
                await host.SaveAsync(p);
                await host.NotifyAsync(p, "CS2 relist withdrawn — selling on Steam",
                    $"**{p.ItemMarketHashName}** didn't sell on CSFloat in {(now - p.ListedOnCSFloatAtUtc).TotalDays:F1} days.\n{Explain(plan)}");
                return await host.SellOnSteamAsync(p, best.Price) ? "steam" : "failed";
            }

            if (demand != null && p.CSFloatResalePriceCents > 0 && anchor > 0)
                p.RelistBaseDaysToSell = demand.ExpectedDaysToSell(p.CSFloatResalePriceCents / (double)anchor);
            p.NextExitReviewUtc = now + ReviewInterval(best?.ExpectedDaysToSell ?? 7);
            if (outcome is "reprice" or "capped" || p.ExitHistory.Count == 0 || p.ExitHistory[^1].AtUtc < now.AddHours(-24))
                p.RecordExitDecision(ExitDecision.From(plan, "review", outcome, depthBid, anchor));
            await host.SaveAsync(p);
            return outcome;
        }

        /// <summary>Plans an exit for a held (tradable) unit from fresh signals.</summary>
        public (ExitPlan Plan, CSFloatDemand? Demand, int AnchorCents) PlanFor(PurchasedListing p, ExitSignals signals, int? listedPrice, double exposure, ExitEnvironment? environment = null)
        {
            DateTime now = utcNow();
            int anchor = AnchorCents(p, signals, host.GbpPerUsd);
            var demand = anchor > 0
                ? CSFloatDemand.Build(p.ItemMarketHashName, anchor, signals.SalesGraph, signals.RecentSales, signals.Competitors,
                    signals.ReferenceQuantity, host.OwnSteamId, p.CSFloatResaleListingID, now)
                : null;
            var context = new ExitContext
            {
                MarketHashName = p.ItemMarketHashName,
                CostPence = p.PurchaseCostPence > 0 ? p.PurchaseCostPence : (int)Math.Round((p.comparison?.CSFloatListing.PriceInPounds ?? 0) * 100),
                DaysUntilTradable = 0,
                AnchorCents = anchor,
                GbpPerUsd = host.GbpPerUsd,
                ConversionCoefficient = host.ConversionCoefficient,
                Book = signals.Book,
                Forecast = PriceForecaster.Forecast(signals.SteamHistory, p.ItemMarketHashName, now, host.MarketModel),
                Demand = demand,
                AllowRelist = host.Settings.Evaluation.AllowRelistExit,
                ListedPriceCents = listedPrice,
                ListedHazardExposure = exposure,
                Calibration = host.ExitCalibration,
                Environment = environment ?? host.ExitEnvironment,
            };
            return (ExitPlanner.Plan(context, host.Settings.Exit, now), demand, anchor);
        }

        /// <summary>CSFloat's value for this unit now: today's base value × the unit's float factor at purchase.</summary>
        public static int AnchorCents(PurchasedListing p, ExitSignals signals, double gbpPerUsd)
        {
            double factor = p.AnchorFloatFactor > 0 ? Math.Min(p.AnchorFloatFactor, 1.0) : 1.0; // never count a float premium
            if (signals.ReferenceBaseCents is int baseNow && baseNow > 0) return (int)Math.Round(baseNow * factor);
            if (p.AnchorCentsAtPurchase > 0) return p.AnchorCentsAtPurchase;
            int legacyPence = p.comparison?.CSFloatListing.AppraisalBasePriceInPence ?? 0;
            return legacyPence > 0 && gbpPerUsd > 0 ? (int)Math.Floor(legacyPence / gbpPerUsd) : 0;
        }

        private void StartRelistEpisode(PurchasedListing p, string listingId, int priceCents, CSFloatDemand demand, int anchor)
        {
            DateTime now = utcNow();
            p.CSFloatResaleListingID = listingId;
            p.CSFloatResalePriceCents = priceCents;
            p.CurrentStrategicStage = StrategicStages.WaitingForCSFloatResale;
            if (p.ListedOnCSFloatAtUtc == default) p.ListedOnCSFloatAtUtc = now;
            p.LastRelistChangeUtc = now;
            p.RelistExposureUpdatedUtc = now;
            p.RelistBaseDaysToSell = anchor > 0 ? demand.ExpectedDaysToSell(priceCents / (double)anchor) : 0;
            p.ResaleSoldAtUtc = default;
            p.NextExitReviewUtc = now + ReviewInterval(p.RelistBaseDaysToSell);
        }

        /// <summary>Adds the expected sales (base model) the listing sat through since the last update.</summary>
        public static void AccrueExposure(PurchasedListing p, DateTime nowUtc)
        {
            if (p.RelistBaseDaysToSell > 0 && p.RelistExposureUpdatedUtc != default && nowUtc > p.RelistExposureUpdatedUtc)
            {
                double expectedSales = (nowUtc - p.RelistExposureUpdatedUtc).TotalDays / p.RelistBaseDaysToSell;
                p.RelistModelExposure += expectedSales;
                p.RelistTotalExposure += expectedSales;
            }
            p.RelistExposureUpdatedUtc = nowUtc;
        }

        /// <summary>Review a third of the way through the expected wait, between 2 and 12 hours.</summary>
        public static TimeSpan ReviewInterval(double expectedDaysToSell) =>
            TimeSpan.FromHours(Math.Clamp(expectedDaysToSell * 24 / 3, 2, 12));

        public static string Explain(ExitPlan plan)
        {
            string evidence = plan.SalesPerDay > 0
                ? $"CSFloat demand: {plan.SalesPerDay:0.##} sales/day ({plan.SalesRateBasis})" +
                  (double.IsFinite(plan.DaysSinceLastCSFloatSale) ? $", last sale {ExitOption.FormatDays(plan.DaysSinceLastCSFloatSale)} ago" : "") +
                  (plan.MedianValueRatio > 0 ? $", buyers pay ~{plan.MedianValueRatio:P0} of CSFloat's value" : "") +
                  (plan.DemandMultiplier < 0.95 ? $"; demand marked down to {plan.DemandMultiplier:P0} on the evidence so far" : "")
                : "";
            return plan.Rationale + (evidence.Length > 0 ? "\n" + evidence : "");
        }
    }
}
