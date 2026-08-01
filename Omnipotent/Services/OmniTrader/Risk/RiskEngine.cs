using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Instruments;
using Omnipotent.Services.OmniTrader.Venues;

namespace Omnipotent.Services.OmniTrader.Risk
{
    /// <summary>
    /// The mandatory pre-trade decision. Every proposed order — strategy or manual, paper or live —
    /// passes through <see cref="Evaluate"/>, and the order service refuses to submit anything
    /// without an approved decision id.
    ///
    /// The engine is deliberately **pure**: it takes the proposal, the instrument, the venue
    /// capabilities and snapshots of portfolio/operational state, and returns a decision. That makes
    /// every rule directly unit-testable and keeps the engine unable to mutate strategy logic.
    /// </summary>
    public sealed class RiskEngine
    {
        private readonly Func<RiskLimits> limitsProvider;

        public RiskEngine(Func<RiskLimits> limitsProvider) => this.limitsProvider = limitsProvider;

        public RiskLimits Limits => limitsProvider();

        public RiskDecision Evaluate(
            TradeProposal proposal,
            Instrument? instrument,
            VenueCapabilities capabilities,
            DataFreshness freshness,
            RiskPortfolioState portfolio,
            RiskOperationalState operations)
        {
            var limits = limitsProvider();
            var rules = new List<RiskRuleResult>();

            decimal price = proposal.LimitPrice ?? proposal.DecisionPrice;
            decimal notional = Math.Abs(proposal.Quantity * price);
            decimal signedNotional = proposal.Side == OrderSide.Buy ? notional : -notional;

            EvaluateDataIntegrity(rules, proposal, instrument, freshness, limits);
            EvaluateOrderValidity(rules, proposal, instrument, capabilities, price);
            EvaluateTradeRisk(rules, proposal, notional, limits);
            EvaluateStrategyRisk(rules, proposal, portfolio, limits);
            EvaluateVenueRisk(rules, proposal, instrument, capabilities, portfolio, notional);
            var projection = EvaluatePortfolioRisk(rules, proposal, portfolio, limits, signedNotional, notional);
            EvaluateOperationalRisk(rules, operations, limits);

            bool hardFail = rules.Any(r => r.Severity == RiskSeverity.Hard);
            bool softFail = rules.Any(r => r.Severity == RiskSeverity.Soft);
            // Authority is itself a control: below Automated, a clean order still needs a human.
            bool authorityNeedsHuman = proposal.Authority == ExecutionAuthority.ApprovalRequired;

            var verdict = hardFail ? RiskVerdict.Rejected
                        : (softFail || authorityNeedsHuman) ? RiskVerdict.RequiresApproval
                        : RiskVerdict.Approved;

            if (authorityNeedsHuman && !hardFail && !softFail)
                rules.Add(RiskRuleResult.Soft(RiskLayer.OperationalRisk, "authority.approval_required",
                    "Deployment authority is approval-required; a human must confirm before submission."));

            return new RiskDecision
            {
                Id = Guid.NewGuid().ToString("N"),
                ProposalId = proposal.Id,
                Verdict = verdict,
                DecidedUtc = DateTime.UtcNow,
                Rules = rules,
                ProjectedGrossExposure = projection.Gross,
                ProjectedNetExposure = projection.Net,
                ProjectedVenueExposure = projection.Venue
            };
        }

        // ── layer 1: data integrity ───────────────────────────────────────────────
        private static void EvaluateDataIntegrity(List<RiskRuleResult> rules, TradeProposal proposal,
            Instrument? instrument, DataFreshness freshness, RiskLimits limits)
        {
            if (instrument == null)
            {
                rules.Add(RiskRuleResult.Hard(RiskLayer.DataIntegrity, "instrument.unknown",
                    $"No canonical instrument for '{proposal.InstrumentId}'."));
                return;
            }
            rules.Add(RiskRuleResult.Pass(RiskLayer.DataIntegrity, "instrument.known"));

            var age = DateTime.UtcNow - proposal.DataTimestampUtc;
            if (age > limits.MaxPriceAge)
                rules.Add(RiskRuleResult.Hard(RiskLayer.DataIntegrity, "price.decision_stale",
                    $"Decision data is {age.TotalMinutes:F1} min old (limit {limits.MaxPriceAge.TotalMinutes:F0} min).",
                    (decimal)age.TotalMinutes, (decimal)limits.MaxPriceAge.TotalMinutes));
            else
                rules.Add(RiskRuleResult.Pass(RiskLayer.DataIntegrity, "price.decision_fresh",
                    (decimal)age.TotalMinutes, (decimal)limits.MaxPriceAge.TotalMinutes));

            if (freshness.Stale)
                rules.Add(RiskRuleResult.Hard(RiskLayer.DataIntegrity, "feed.stale",
                    freshness.Issue ?? "Instrument feed is stale."));
            else
                rules.Add(RiskRuleResult.Pass(RiskLayer.DataIntegrity, "feed.fresh"));

            if (proposal.DecisionPrice <= 0m && proposal.LimitPrice is null or <= 0m)
                rules.Add(RiskRuleResult.Hard(RiskLayer.DataIntegrity, "price.unusable",
                    "No usable decision or limit price — exposure cannot be measured."));

            if (proposal.Expired)
                rules.Add(RiskRuleResult.Hard(RiskLayer.DataIntegrity, "proposal.expired",
                    $"Proposal expired at {proposal.ExpiresUtc:o}."));
        }

        // ── layer 2: order validity ───────────────────────────────────────────────
        private static void EvaluateOrderValidity(List<RiskRuleResult> rules, TradeProposal proposal,
            Instrument? instrument, VenueCapabilities capabilities, decimal price)
        {
            if (proposal.Quantity <= 0m)
            {
                rules.Add(RiskRuleResult.Hard(RiskLayer.OrderValidity, "qty.positive",
                    "Quantity must be greater than zero."));
                return;
            }
            rules.Add(RiskRuleResult.Pass(RiskLayer.OrderValidity, "qty.positive"));

            if (!capabilities.Supports(proposal.Type))
                rules.Add(RiskRuleResult.Hard(RiskLayer.OrderValidity, "ordertype.unsupported",
                    $"{capabilities.DisplayName} does not support {proposal.Type} orders."));
            else
                rules.Add(RiskRuleResult.Pass(RiskLayer.OrderValidity, "ordertype.supported"));

            if (proposal.Type == OrderType.Limit && proposal.LimitPrice is null or <= 0m)
                rules.Add(RiskRuleResult.Hard(RiskLayer.OrderValidity, "limit.price_required",
                    "A limit order requires a positive limit price."));

            if (proposal.Type == OrderType.StopLoss && proposal.StopPrice is null or <= 0m)
                rules.Add(RiskRuleResult.Hard(RiskLayer.OrderValidity, "stop.price_required",
                    "A stop order requires a positive stop price."));

            if ((proposal.StopLossPrice.HasValue || proposal.TakeProfitPrice.HasValue)
                && !capabilities.SupportsAttachedProtection)
                rules.Add(RiskRuleResult.Hard(RiskLayer.OrderValidity, "protection.unsupported",
                    capabilities.WhyNot("SupportsAttachedProtection")
                    ?? $"{capabilities.DisplayName} cannot attach protective orders."));

            // A protective level on the wrong side of the entry would arm instantly.
            if (proposal.StopLossPrice is > 0m && price > 0m)
            {
                bool wrongSide = proposal.Side == OrderSide.Buy
                    ? proposal.StopLossPrice.Value >= price
                    : proposal.StopLossPrice.Value <= price;
                if (wrongSide)
                    rules.Add(RiskRuleResult.Hard(RiskLayer.OrderValidity, "stoploss.wrong_side",
                        $"Stop {proposal.StopLossPrice:F4} is on the wrong side of entry {price:F4}."));
            }
            if (proposal.TakeProfitPrice is > 0m && price > 0m)
            {
                bool wrongSide = proposal.Side == OrderSide.Buy
                    ? proposal.TakeProfitPrice.Value <= price
                    : proposal.TakeProfitPrice.Value >= price;
                if (wrongSide)
                    rules.Add(RiskRuleResult.Hard(RiskLayer.OrderValidity, "takeprofit.wrong_side",
                        $"Target {proposal.TakeProfitPrice:F4} is on the wrong side of entry {price:F4}."));
            }

            var mapping = instrument?.MappingFor(proposal.Venue);
            if (mapping == null)
            {
                rules.Add(RiskRuleResult.Hard(RiskLayer.OrderValidity, "venue.no_mapping",
                    $"{instrument?.DisplayName ?? proposal.InstrumentId} is not mapped to {proposal.Venue}."));
                return;
            }
            if (!mapping.Tradeable)
                rules.Add(RiskRuleResult.Hard(RiskLayer.OrderValidity, "market.not_tradeable",
                    $"Market status is {mapping.TradingStatus ?? "not tradeable"}."));
            else
                rules.Add(RiskRuleResult.Pass(RiskLayer.OrderValidity, "market.tradeable"));

            if (mapping.MinQuantity > 0m && proposal.Quantity < mapping.MinQuantity)
                rules.Add(RiskRuleResult.Hard(RiskLayer.OrderValidity, "qty.below_minimum",
                    $"Quantity {proposal.Quantity} is below the venue minimum {mapping.MinQuantity}.",
                    proposal.Quantity, mapping.MinQuantity));
            if (mapping.MaxQuantity is > 0m && proposal.Quantity > mapping.MaxQuantity.Value)
                rules.Add(RiskRuleResult.Hard(RiskLayer.OrderValidity, "qty.above_maximum",
                    $"Quantity {proposal.Quantity} exceeds the venue maximum {mapping.MaxQuantity}.",
                    proposal.Quantity, mapping.MaxQuantity));

            if (mapping.QuantityStep > 0m)
            {
                decimal remainder = Math.Abs(proposal.Quantity) % mapping.QuantityStep;
                // Tolerate a step-sized epsilon so decimal representation noise is not a rejection.
                if (remainder > mapping.QuantityStep / 1000m && Math.Abs(remainder - mapping.QuantityStep) > mapping.QuantityStep / 1000m)
                    rules.Add(RiskRuleResult.Hard(RiskLayer.OrderValidity, "qty.precision",
                        $"Quantity {proposal.Quantity} is not a multiple of the venue step {mapping.QuantityStep}."));
                else
                    rules.Add(RiskRuleResult.Pass(RiskLayer.OrderValidity, "qty.precision"));
            }
        }

        // ── layer 3: trade risk ───────────────────────────────────────────────────
        private static void EvaluateTradeRisk(List<RiskRuleResult> rules, TradeProposal proposal,
            decimal notional, RiskLimits limits)
        {
            if (notional > limits.MaxOrderNotional)
                rules.Add(RiskRuleResult.Hard(RiskLayer.TradeRisk, "order.notional",
                    $"Order notional {notional:F2} exceeds the hard cap {limits.MaxOrderNotional:F2}.",
                    notional, limits.MaxOrderNotional));
            else if (notional > limits.SoftOrderNotional)
                rules.Add(RiskRuleResult.Soft(RiskLayer.TradeRisk, "order.notional_soft",
                    $"Order notional {notional:F2} exceeds the soft cap {limits.SoftOrderNotional:F2} and needs approval.",
                    notional, limits.SoftOrderNotional));
            else
                rules.Add(RiskRuleResult.Pass(RiskLayer.TradeRisk, "order.notional", notional, limits.MaxOrderNotional));

            // Requested loss: what the attached stop would actually cost if hit.
            if (proposal.StopLossPrice is > 0m && proposal.DecisionPrice > 0m)
            {
                decimal perUnit = Math.Abs(proposal.DecisionPrice - proposal.StopLossPrice.Value);
                decimal requestedLoss = perUnit * proposal.Quantity;
                rules.Add(requestedLoss > limits.MaxStrategyDailyLoss
                    ? RiskRuleResult.Soft(RiskLayer.TradeRisk, "trade.requested_loss",
                        $"Stop implies a {requestedLoss:F2} loss, above the {limits.MaxStrategyDailyLoss:F2} daily strategy budget.",
                        requestedLoss, limits.MaxStrategyDailyLoss)
                    : RiskRuleResult.Pass(RiskLayer.TradeRisk, "trade.requested_loss", requestedLoss, limits.MaxStrategyDailyLoss));
            }
            else
            {
                rules.Add(RiskRuleResult.Soft(RiskLayer.TradeRisk, "trade.no_protection",
                    "No stop attached — the loss on this trade is unbounded by the order itself."));
            }

            if (proposal.Type == OrderType.Limit && proposal.LimitPrice is > 0m && proposal.DecisionPrice > 0m)
            {
                decimal distanceBps = Math.Abs(proposal.LimitPrice.Value - proposal.DecisionPrice) / proposal.DecisionPrice * 10_000m;
                rules.Add(distanceBps > limits.MaxSlippageToleranceBps * 10m
                    ? RiskRuleResult.Soft(RiskLayer.TradeRisk, "limit.far_from_mark",
                        $"Limit is {distanceBps:F0} bps from the mark; it may rest unfilled.", distanceBps, limits.MaxSlippageToleranceBps * 10m)
                    : RiskRuleResult.Pass(RiskLayer.TradeRisk, "limit.distance", distanceBps));
            }
        }

        // ── layer 4: strategy risk ────────────────────────────────────────────────
        private static void EvaluateStrategyRisk(List<RiskRuleResult> rules, TradeProposal proposal,
            RiskPortfolioState portfolio, RiskLimits limits)
        {
            if (proposal.Authority == ExecutionAuthority.Observe)
                rules.Add(RiskRuleResult.Hard(RiskLayer.StrategyRisk, "authority.observe_only",
                    "Deployment is in observe mode; it may record signals but not place orders."));
            else
                rules.Add(RiskRuleResult.Pass(RiskLayer.StrategyRisk, "authority.permits_execution"));

            string key = proposal.StrategyId ?? proposal.DeploymentId ?? "manual";
            if (portfolio.DailyPnLByStrategy.TryGetValue(key, out var dailyPnL) && dailyPnL <= -Math.Abs(limits.MaxStrategyDailyLoss))
                rules.Add(RiskRuleResult.Hard(RiskLayer.StrategyRisk, "strategy.daily_loss",
                    $"Strategy has lost {dailyPnL:F2} today, at or past its {limits.MaxStrategyDailyLoss:F2} budget.",
                    dailyPnL, -Math.Abs(limits.MaxStrategyDailyLoss)));
            else
                rules.Add(RiskRuleResult.Pass(RiskLayer.StrategyRisk, "strategy.daily_loss",
                    portfolio.DailyPnLByStrategy.TryGetValue(key, out var p) ? p : 0m, -Math.Abs(limits.MaxStrategyDailyLoss)));

            if (portfolio.OpenPositionsByStrategy.TryGetValue(key, out var open) && open >= limits.MaxConcurrentPositionsPerStrategy)
                rules.Add(RiskRuleResult.Hard(RiskLayer.StrategyRisk, "strategy.position_count",
                    $"Strategy already holds {open} positions (limit {limits.MaxConcurrentPositionsPerStrategy}).",
                    open, limits.MaxConcurrentPositionsPerStrategy));
        }

        // ── layer 5: venue risk ───────────────────────────────────────────────────
        private static void EvaluateVenueRisk(List<RiskRuleResult> rules, TradeProposal proposal,
            Instrument? instrument, VenueCapabilities capabilities, RiskPortfolioState portfolio, decimal notional)
        {
            // Spot: a sell can never exceed free inventory, and a short is simply not available.
            if (capabilities.Exposure == ExposureKind.Inventory && proposal.Side == OrderSide.Sell)
            {
                string asset = instrument?.BaseAsset ?? "";
                decimal free = portfolio.FreeInventory.TryGetValue(asset, out var f) ? f : 0m;
                if (proposal.Quantity > free)
                    rules.Add(RiskRuleResult.Hard(RiskLayer.VenueRisk, "inventory.insufficient",
                        capabilities.SupportsShort
                            ? $"Sell of {proposal.Quantity} {asset} exceeds free inventory {free}."
                            : $"Sell of {proposal.Quantity} {asset} exceeds free inventory {free}; "
                              + (capabilities.WhyNot("SupportsShort") ?? "short exposure is unavailable on this venue."),
                        proposal.Quantity, free));
                else
                    rules.Add(RiskRuleResult.Pass(RiskLayer.VenueRisk, "inventory.sufficient", proposal.Quantity, free));
            }

            // A spot buy spends cash rather than inventory. Where the venue reports available funds we
            // check against them; either way the layer records a verdict so the decision shows it ran.
            if (capabilities.Exposure == ExposureKind.Inventory && proposal.Side == OrderSide.Buy)
            {
                if (portfolio.AvailableFunds is { } cash && cash > 0m)
                    rules.Add(notional > cash
                        ? RiskRuleResult.Hard(RiskLayer.VenueRisk, "cash.insufficient",
                            $"Buy of {notional:F2} exceeds available cash {cash:F2}.", notional, cash)
                        : RiskRuleResult.Pass(RiskLayer.VenueRisk, "cash.sufficient", notional, cash));
                else
                    rules.Add(RiskRuleResult.Pass(RiskLayer.VenueRisk, "cash.not_reported"));
            }

            if (capabilities.Exposure == ExposureKind.Derivative && proposal.Side == OrderSide.Buy)
            {
                if (portfolio.AvailableFunds is { } available && available > 0m)
                {
                    decimal marginFactor = instrument?.MappingFor(proposal.Venue)?.MarginFactor ?? 100m;
                    decimal marginRequired = notional * marginFactor / 100m;
                    if (marginRequired > available)
                        rules.Add(RiskRuleResult.Hard(RiskLayer.VenueRisk, "margin.insufficient",
                            $"Margin of {marginRequired:F2} exceeds available funds {available:F2}.",
                            marginRequired, available));
                    else
                        rules.Add(RiskRuleResult.Pass(RiskLayer.VenueRisk, "margin.sufficient", marginRequired, available));
                }
            }

            if (!capabilities.SupportsShort && proposal.Side == OrderSide.Sell
                && instrument?.Exposure == ExposureKind.Derivative)
                rules.Add(RiskRuleResult.Hard(RiskLayer.VenueRisk, "short.unsupported",
                    capabilities.WhyNot("SupportsShort") ?? "Short exposure is not available on this venue."));
        }

        // ── layer 6: portfolio risk (the after-picture) ───────────────────────────
        private static (decimal Gross, decimal Net, decimal Venue) EvaluatePortfolioRisk(
            List<RiskRuleResult> rules, TradeProposal proposal, RiskPortfolioState portfolio,
            RiskLimits limits, decimal signedNotional, decimal notional)
        {
            decimal projectedGross = portfolio.GrossExposure + notional;
            decimal projectedNet = portfolio.NetExposure + signedNotional;

            decimal instrumentBefore = portfolio.ExposureByInstrument.TryGetValue(proposal.InstrumentId, out var ie) ? ie : 0m;
            decimal projectedInstrument = Math.Abs(instrumentBefore + signedNotional);

            decimal venueBefore = portfolio.ExposureByVenue.TryGetValue(proposal.Venue, out var ve) ? ve : 0m;
            decimal projectedVenue = Math.Abs(venueBefore) + notional;

            Add(rules, projectedGross > limits.MaxGrossExposure, RiskLayer.PortfolioRisk, "portfolio.gross_exposure",
                $"Gross exposure would reach {projectedGross:F2} (limit {limits.MaxGrossExposure:F2}).",
                projectedGross, limits.MaxGrossExposure);

            Add(rules, Math.Abs(projectedNet) > limits.MaxNetExposure, RiskLayer.PortfolioRisk, "portfolio.net_exposure",
                $"Net exposure would reach {projectedNet:F2} (limit ±{limits.MaxNetExposure:F2}).",
                Math.Abs(projectedNet), limits.MaxNetExposure);

            Add(rules, projectedInstrument > limits.MaxSingleInstrumentExposure, RiskLayer.PortfolioRisk, "portfolio.concentration",
                $"Exposure to {proposal.InstrumentId} would reach {projectedInstrument:F2} (limit {limits.MaxSingleInstrumentExposure:F2}).",
                projectedInstrument, limits.MaxSingleInstrumentExposure);

            Add(rules, projectedVenue > limits.MaxVenueExposure, RiskLayer.PortfolioRisk, "portfolio.venue_exposure",
                $"Exposure at {proposal.Venue} would reach {projectedVenue:F2} (limit {limits.MaxVenueExposure:F2}).",
                projectedVenue, limits.MaxVenueExposure);

            Add(rules, portfolio.DailyRealizedPnL <= -Math.Abs(limits.MaxFirmDailyLoss), RiskLayer.PortfolioRisk, "firm.daily_loss",
                $"Firm has realized {portfolio.DailyRealizedPnL:F2} today, at or past the {limits.MaxFirmDailyLoss:F2} limit.",
                portfolio.DailyRealizedPnL, -Math.Abs(limits.MaxFirmDailyLoss));

            Add(rules, portfolio.DrawdownPercent > limits.MaxDrawdownPercent, RiskLayer.PortfolioRisk, "firm.drawdown",
                $"Drawdown is {portfolio.DrawdownPercent:F2}% (limit {limits.MaxDrawdownPercent:F2}%).",
                portfolio.DrawdownPercent, limits.MaxDrawdownPercent);

            return (projectedGross, projectedNet, projectedVenue);
        }

        // ── layer 7: operational risk ─────────────────────────────────────────────
        private static void EvaluateOperationalRisk(List<RiskRuleResult> rules, RiskOperationalState ops, RiskLimits limits)
        {
            if (ops.SafeModeActive)
                rules.Add(RiskRuleResult.Hard(RiskLayer.OperationalRisk, "safemode.active",
                    ops.SafeModeReason ?? "Safe mode is active; new automated exposure is blocked."));
            else
                rules.Add(RiskRuleResult.Pass(RiskLayer.OperationalRisk, "safemode.clear"));

            Add(rules, ops.UnknownOrders > limits.MaxUnresolvedUnknownOrders, RiskLayer.OperationalRisk, "orders.unknown_outstanding",
                $"{ops.UnknownOrders} order(s) have an unproven outcome; new exposure is blocked until they reconcile.",
                ops.UnknownOrders, limits.MaxUnresolvedUnknownOrders);

            Add(rules, ops.UnreconciledBreaks > limits.MaxUnreconciledBreaks, RiskLayer.OperationalRisk, "reconciliation.breaks",
                $"{ops.UnreconciledBreaks} unresolved reconciliation break(s).",
                ops.UnreconciledBreaks, limits.MaxUnreconciledBreaks);

            Add(rules, ops.RecentRejections >= limits.RepeatedRejectionThreshold, RiskLayer.OperationalRisk, "orders.repeated_rejection",
                $"{ops.RecentRejections} recent broker rejections at or past the {limits.RepeatedRejectionThreshold} threshold.",
                ops.RecentRejections, limits.RepeatedRejectionThreshold);

            if (!ops.VenueOrderPathHealthy)
                rules.Add(RiskRuleResult.Hard(RiskLayer.OperationalRisk, "venue.order_path_down",
                    "The venue's order path is not healthy; submission would have an unprovable outcome."));
            else
                rules.Add(RiskRuleResult.Pass(RiskLayer.OperationalRisk, "venue.order_path_healthy"));
        }

        private static void Add(List<RiskRuleResult> rules, bool breached, RiskLayer layer, string rule,
            string detail, decimal observed, decimal limit)
            => rules.Add(breached
                ? RiskRuleResult.Hard(layer, rule, detail, observed, limit)
                : RiskRuleResult.Pass(layer, rule, observed, limit));
    }
}
