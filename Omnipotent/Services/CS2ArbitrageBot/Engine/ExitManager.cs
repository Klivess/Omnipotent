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
        /// <summary>Lists the unit on the Steam market (into the buy order), or says what stopped it.</summary>
        Task<SteamSellResult> SellOnSteamAsync(PurchasedListing position, int pricePence);
        /// <summary>Lists the unit on CSFloat; null when it is not yet tradable in the CSFloat inventory.</summary>
        Task<string?> ListOnCSFloatAsync(PurchasedListing position, int priceCents, CancellationToken ct);
        Task<bool> RepriceOnCSFloatAsync(PurchasedListing position, int priceCents, CancellationToken ct);
        Task DelistFromCSFloatAsync(PurchasedListing position, CancellationToken ct);
        /// <summary>Reads what CSFloat says became of the position's relist and whether its unit is still in the inventory.</summary>
        Task<RelistFacts> InspectRelistAsync(PurchasedListing position, CancellationToken ct);
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
        /// <summary>
        /// After a re-price CSFloat refused, look again this much later. A refusal is usually the listing no longer
        /// being live (sold, or taken down), and the 10-minute review loop retried it 107 times a day with no change.
        /// </summary>
        public static readonly TimeSpan FailedChangeRetry = TimeSpan.FromHours(1);
        /// <summary>
        /// While the Steam login is down, a sale that needs Steam looks again this often (the service also wakes it as
        /// soon as the login works again).
        /// </summary>
        public static readonly TimeSpan SteamLoginRetry = TimeSpan.FromHours(6);
        /// <summary>
        /// How long a sale waits for the Steam login when Steam is the better exit. After that, holding the item costs
        /// more than the gap to the next-best exit, so an available CSFloat relist is taken instead.
        /// </summary>
        public static readonly TimeSpan SteamOutageRelistAfter = TimeSpan.FromDays(2);
        /// <summary>Steam's inventory could not be read (rate limit, server error): look again this much later.</summary>
        public static readonly TimeSpan SteamInventoryRetry = TimeSpan.FromHours(1);

        private readonly IExitHost host;
        private readonly Func<DateTime> utcNow;
        private (string? ListingId, DateTime AtUtc, RelistFacts? Facts) lastInspection;

        public ExitManager(IExitHost host, Func<DateTime>? utcNow = null)
        {
            this.host = host;
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>
        /// Decides and executes the exit for a position whose trade protection has lifted. Returns what happened
        /// ("steam", "relist", "deferred", "paused", "waiting for steam login", "steam unavailable", "not tradable",
        /// "unsellable" or "failed"). Only "unsellable" and "failed" count towards the position's failed-sale limit.
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

            if (environment.SteamUnavailableReason is string steamDown)
            {
                // Selling on Steam needs the bot's Steam login, which only Klives can approve. When Steam is the better
                // exit the sale waits for it (the service asks him, once) rather than relist for less — until the outage
                // has lasted SteamOutageRelistAfter, when an available relist is taken instead. Waiting never counts as
                // a failed sale: counting it is how a lapsed login turned every waiting item into a manual-look alert.
                var (withSteam, _, _) = PlanFor(p, signals, listedPrice: null, exposure: 0, environment with { SteamUnavailableReason = null });
                if (withSteam.Best?.Route == ExitRoute.SteamMarket)
                {
                    if (p.WaitingForSteamLoginSinceUtc == default) p.WaitingForSteamLoginSinceUtc = now;
                    TimeSpan waited = now - p.WaitingForSteamLoginSinceUtc;
                    if (plan.Best == null || waited < SteamOutageRelistAfter)
                        return await WaitForSteamLoginAsync(p, now, steamDown);
                    host.Log($"The Steam login has been down {waited.TotalHours:F0} h; {p.ItemMarketHashName} takes the best exit without Steam — {plan.Rationale}");
                }
            }

            var best = plan.Best;
            if (best == null)
            {
                // Logged, not alerted: if it keeps happening the position stops being sold automatically and the
                // service reports it once, with this reason.
                p.SaleAttempts++;
                p.LastSaleAttemptUtc = now;
                p.LastSaleFailure = "no exit could be priced: " + plan.Rationale;
                await host.SaveAsync(p);
                host.Log($"Can't price a sale of {p.ItemMarketHashName} ({p.SaleAttempts} of the allowed attempts): {plan.Rationale}. Retrying in 6 hours.");
                return "unsellable";
            }

            int depthBid = signals.Book?.PriceAtBuyDepth(3) ?? 0;
            if (best.Route == ExitRoute.SteamMarket)
            {
                // Counted before listing, so a sale that crashes mid-way still runs out of attempts; given back below
                // when what stopped it was an outage rather than the position.
                DateTime previousAttempt = p.LastSaleAttemptUtc;
                p.SaleAttempts++;
                p.LastSaleAttemptUtc = now;
                p.RecordExitDecision(ExitDecision.From(plan, "sale", "steam", depthBid, anchor));
                await host.SaveAsync(p);
                var result = await host.SellOnSteamAsync(p, best.Price);
                if (result.Listed)
                {
                    p.WaitingForSteamLoginSinceUtc = default;
                    p.LastSaleFailure = "";
                    await host.SaveAsync(p);
                    return "steam";
                }
                if (!result.CountsAsFailedAttempt)
                {
                    p.SaleAttempts--;
                    p.LastSaleAttemptUtc = previousAttempt;
                    if (result.Outcome == SteamSellOutcome.LoginUnavailable) return await WaitForSteamLoginAsync(p, now, result.Detail);
                    p.SaleDeferredUntilUtc = now + (result.Outcome == SteamSellOutcome.NotMarketableYet ? NotTradableRetry : SteamInventoryRetry);
                    await host.SaveAsync(p);
                    host.Log($"Sale of {p.ItemMarketHashName} on Steam postponed: {result.Detail}.");
                    return result.Outcome == SteamSellOutcome.NotMarketableYet ? "not tradable" : "steam unavailable";
                }
                p.LastSaleFailure = result.Detail;
                await host.SaveAsync(p);
                host.Log($"Selling {p.ItemMarketHashName} on Steam failed ({p.SaleAttempts} of the allowed attempts): {result.Detail}.");
                return "failed";
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
            p.WaitingForSteamLoginSinceUtc = default;
            p.LastSaleFailure = "";
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
            DateTime now = utcNow();
            // A sale the bot inferred from the listing (its trade never showed up) is checked again until the unit
            // leaves (completed) or the sale lapses (sell it again); a sale the trade monitor follows is left to it.
            if (p.LastTradeState == RelistReconciliation.InferredSoldState)
            {
                if (await SettleRelistAsync(p, now, ct) is string settled) return settled;
                if (p.LastTradeState == RelistReconciliation.InferredSoldState) return await PostponeAsync(p, now + RelistReconciliation.SoldRecheck, "sold");
                // Listed again: review it like any other live listing.
            }
            if (p.ResaleSoldAtUtc != default || p.LastTradeState is "resale:queued" or "resale:pending") return "skip"; // a buyer has it
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
                // The market keeps buying this item but not ours: usually the listing itself (sold with its trade out of
                // sight, taken down, hidden) rather than the price. Ask CSFloat first; only a listing it still shows as
                // live needs a person.
                if (await SettleRelistAsync(p, now, ct) is string settled) return settled;
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
                if (!await host.RepriceOnCSFloatAsync(p, best.Price, ct))
                {
                    // Refused: find out why (sold, taken down) instead of retrying the same change every hour for ever.
                    if (await SettleRelistAsync(p, now, ct) is string settled) return settled;
                    p.NextExitReviewUtc = now + FailedChangeRetry;
                    await host.SaveAsync(p);
                    return "failed";
                }
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
                var sold = await host.SellOnSteamAsync(p, best.Price);
                if (sold.Listed) return "steam";
                // Back with the sale scheduler, which handles it like any other sale (an outage waits and never counts).
                p.LastSaleFailure = sold.Detail;
                await host.SaveAsync(p);
                return "failed";
            }

            if (demand != null && p.CSFloatResalePriceCents > 0 && anchor > 0)
                p.RelistBaseDaysToSell = demand.ExpectedDaysToSell(p.CSFloatResalePriceCents / (double)anchor);
            p.NextExitReviewUtc = now + ReviewInterval(best?.ExpectedDaysToSell ?? 7);
            if (outcome is "reprice" or "capped" || p.ExitHistory.Count == 0 || p.ExitHistory[^1].AtUtc < now.AddHours(-24))
                p.RecordExitDecision(ExitDecision.From(plan, "review", outcome, depthBid, anchor));
            await host.SaveAsync(p);
            return outcome;
        }

        /// <summary>
        /// Asks CSFloat what became of the relist (and the inventory whether its unit is still there) and settles the
        /// position when the answer says it is no longer a live listing. Returns the outcome when the position moved on
        /// ("sold", "completed", "relist-gone", "left-inventory"), or null when it is still a live listing or nothing could be read.
        /// </summary>
        public async Task<string?> SettleRelistAsync(PurchasedListing p, DateTime now, CancellationToken ct)
        {
            RelistFacts facts;
            // One review can ask twice (overdue, then a refused re-price): the first answer serves both.
            var earlier = lastInspection;
            if (earlier.ListingId == p.CSFloatResaleListingID && earlier.AtUtc == now && earlier.Facts != null) facts = earlier.Facts;
            else
            {
                try { facts = await host.InspectRelistAsync(p, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    host.Log($"Couldn't read what became of the relist of {p.ItemMarketHashName}: {ex.Message}");
                    return null;
                }
                lastInspection = (p.CSFloatResaleListingID, now, facts);
            }
            var verdict = RelistReconciliation.Decide(facts, p, now);
            bool wasInferredSale = p.LastTradeState == RelistReconciliation.InferredSoldState;
            string listing = p.CSFloatResaleListingID;
            if (verdict is RelistVerdict.Unknown or RelistVerdict.StillListed)
            {
                if (verdict == RelistVerdict.StillListed && wasInferredSale)
                {
                    // Back on the market (the buyer's purchase was cancelled and CSFloat relisted it): the clock resumes.
                    p.ResaleSoldAtUtc = default;
                    p.LastTradeState = "resale:listed";
                    p.RelistExposureUpdatedUtc = now;
                    await host.SaveAsync(p);
                }
                return null;
            }
            if (verdict == RelistVerdict.SoldAwaitingTrade && wasInferredSale)
            {
                RelistReconciliation.Apply(verdict, facts, p, now, host.GbpPerUsd, host.Settings.Exit.SellerFee);
                await host.SaveAsync(p);
                return "sold";
            }

            string? note = RelistReconciliation.Apply(verdict, facts, p, now, host.GbpPerUsd, host.Settings.Exit.SellerFee);
            await host.SaveAsync(p);
            host.Log($"{p.ItemMarketHashName}: {note}");
            switch (verdict)
            {
                case RelistVerdict.SoldAwaitingTrade:
                    await host.AlertAsync("relist-sold-unseen:" + listing, "CS2 Arbitrage — relist sold, send the trade",
                        $"CSFloat shows the relist of **{p.ItemMarketHashName}** (${p.CSFloatResalePriceCents / 100.0:F2}) as sold, but its trade isn't among the recent trades. " +
                        $"If CSFloat's Trades page shows it waiting, send it (2-hour deadline): https://csfloat.com/item/{listing}");
                    return "sold";
                case RelistVerdict.SoldAndDelivered:
                    await host.NotifyAsync(p, "CS2 arbitrage completed ✓ (inferred)",
                        $"**{p.ItemMarketHashName}** sold on CSFloat for ${p.CSFloatResalePriceCents / 100.0:F2} and has left the inventory: " +
                        $"profit £{p.ActualAbsoluteProfitInPounds:F2} ({p.ActualProfitPercentage:F1}%). The trade itself was never seen, so this is worked out from the listing price.");
                    return "completed";
                case RelistVerdict.BackToSale:
                    await host.NotifyAsync(p, "CS2 relist is no longer up — selling again",
                        $"CSFloat shows the relist of **{p.ItemMarketHashName}** as {facts.ListingState} and the unit is still in the inventory, so the bot will decide its exit again.");
                    return "relist-gone";
                default:
                    await host.NotifyAsync(p, "CS2 position closed",
                        $"The relist of **{p.ItemMarketHashName}** is {facts.ListingState} on CSFloat and the unit has left the inventory (sold or moved outside the bot).");
                    return "left-inventory";
            }
        }

        private async Task<string> PostponeAsync(PurchasedListing p, DateTime nextReviewUtc, string outcome)
        {
            p.NextExitReviewUtc = nextReviewUtc;
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

        private async Task<string> WaitForSteamLoginAsync(PurchasedListing p, DateTime now, string why)
        {
            if (p.WaitingForSteamLoginSinceUtc == default) p.WaitingForSteamLoginSinceUtc = now;
            p.SaleDeferredUntilUtc = now + SteamLoginRetry;
            await host.SaveAsync(p);
            host.Log($"Sale of {p.ItemMarketHashName} waits for the Steam login ({why}); waiting since {p.WaitingForSteamLoginSinceUtc:u}.");
            return "waiting for steam login";
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
