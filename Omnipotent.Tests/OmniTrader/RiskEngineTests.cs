using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Instruments;
using Omnipotent.Services.OmniTrader.Risk;
using Omnipotent.Services.OmniTrader.Venues;

namespace Omnipotent.Tests.OmniTrader
{
    /// <summary>
    /// The risk engine is the mandatory gate between every proposal and every broker submission, so
    /// these tests assert the *blocking* behaviour rule by rule rather than just the happy path.
    /// </summary>
    public class RiskEngineTests
    {
        private static RiskLimits Limits() => new()
        {
            MaxOrderNotional = 500m,
            SoftOrderNotional = 250m,
            MaxStrategyDailyLoss = 100m,
            MaxConcurrentPositionsPerStrategy = 5,
            MaxGrossExposure = 5000m,
            MaxNetExposure = 3000m,
            MaxSingleInstrumentExposure = 1000m,
            MaxVenueExposure = 4000m,
            MaxFirmDailyLoss = 250m,
            MaxDrawdownPercent = 15m,
            MaxPriceAge = TimeSpan.FromMinutes(15)
        };

        private static Instrument SpotInstrument() => new()
        {
            Id = "crypto:BTC/USD",
            DisplayName = "BTC/USD",
            AssetClass = AssetClass.Crypto,
            BaseAsset = "BTC",
            QuoteCurrency = "USD",
            Exposure = ExposureKind.Inventory,
            Venues =
            {
                new VenueMapping
                {
                    Venue = VenueId.Kraken, VenueSymbol = "XBTUSD",
                    TickSize = 0.1m, QuantityStep = 0.00001m, MinQuantity = 0.0001m, Tradeable = true
                }
            }
        };

        private static VenueCapabilities SpotCapabilities(bool supportsShort = false) => new()
        {
            Venue = VenueId.Kraken,
            DisplayName = "Kraken (Spot)",
            Exposure = ExposureKind.Inventory,
            AssetClasses = new[] { AssetClass.Crypto },
            SupportsShort = supportsShort,
            SupportsAttachedProtection = true,
            OrderTypes = new[] { OrderType.Market, OrderType.Limit, OrderType.StopLoss, OrderType.TakeProfit },
            Limitations = { ["SupportsShort"] = "spot cannot short" }
        };

        private static DataFreshness Fresh() => new()
        { InstrumentId = "crypto:BTC/USD", Age = TimeSpan.FromSeconds(5), Stale = false, LastUpdateUtc = DateTime.UtcNow };

        private static DataFreshness Stale() => new()
        { InstrumentId = "crypto:BTC/USD", Age = TimeSpan.FromHours(2), Stale = true, Issue = "feed stale" };

        private static RiskPortfolioState Portfolio(decimal freeBtc = 10m, decimal equity = 10_000m) => new()
        {
            Equity = equity,
            PeakEquity = equity,
            FreeInventory = { ["BTC"] = freeBtc },
            AvailableFunds = 10_000m
        };

        private static RiskOperationalState Clean() => new() { VenueOrderPathHealthy = true };

        private static TradeProposal Proposal(
            OrderSide side = OrderSide.Buy, decimal qty = 0.001m, decimal price = 100_000m,
            ExecutionAuthority authority = ExecutionAuthority.Automated,
            OrderType type = OrderType.Market, decimal? stopLoss = null, DateTime? dataTs = null) => new()
            {
                Id = Guid.NewGuid().ToString("N"),
                InstrumentId = "crypto:BTC/USD",
                Venue = VenueId.Kraken,
                Environment = TradingEnvironment.Live,
                AccountId = "kraken-live",
                Side = side,
                Type = type,
                Quantity = qty,
                DecisionPrice = price,
                StopLossPrice = stopLoss,
                Authority = authority,
                DataTimestampUtc = dataTs ?? DateTime.UtcNow,
                StrategyId = "strategy-a"
            };

        private static RiskDecision Evaluate(TradeProposal proposal, RiskPortfolioState? portfolio = null,
            RiskOperationalState? ops = null, DataFreshness? freshness = null,
            VenueCapabilities? capabilities = null, Instrument? instrument = null, RiskLimits? limits = null)
        {
            var engine = new RiskEngine(() => limits ?? Limits());
            return engine.Evaluate(proposal, instrument ?? SpotInstrument(), capabilities ?? SpotCapabilities(),
                freshness ?? Fresh(), portfolio ?? Portfolio(), ops ?? Clean());
        }

        private static bool Failed(RiskDecision decision, string rule)
            => decision.Rules.Any(r => r.Rule == rule && r.Severity != RiskSeverity.Pass);

        [Fact]
        public void CleanProposalIsApproved()
        {
            // 0.001 BTC at 100k = $100 notional, inside both the soft and hard caps, with a stop.
            var decision = Evaluate(Proposal(stopLoss: 95_000m));
            Assert.Equal(RiskVerdict.Approved, decision.Verdict);
            Assert.Empty(decision.Failures);
        }

        [Fact]
        public void StaleFeedBlocksTheOrder()
        {
            var decision = Evaluate(Proposal(stopLoss: 95_000m), freshness: Stale());
            Assert.Equal(RiskVerdict.Rejected, decision.Verdict);
            Assert.True(Failed(decision, "feed.stale"));
        }

        [Fact]
        public void StaleDecisionDataBlocksTheOrder()
        {
            var decision = Evaluate(Proposal(stopLoss: 95_000m, dataTs: DateTime.UtcNow.AddHours(-1)));
            Assert.Equal(RiskVerdict.Rejected, decision.Verdict);
            Assert.True(Failed(decision, "price.decision_stale"));
        }

        [Fact]
        public void ExpiredProposalIsNotExecuted()
        {
            var proposal = new TradeProposal
            {
                Id = "p1",
                InstrumentId = "crypto:BTC/USD",
                Venue = VenueId.Kraken,
                Environment = TradingEnvironment.Live,
                AccountId = "kraken-live",
                Side = OrderSide.Buy,
                Type = OrderType.Market,
                Quantity = 0.001m,
                DecisionPrice = 100_000m,
                Authority = ExecutionAuthority.Automated,
                ExpiresUtc = DateTime.UtcNow.AddSeconds(-1)
            };
            var decision = Evaluate(proposal);
            Assert.Equal(RiskVerdict.Rejected, decision.Verdict);
            Assert.True(Failed(decision, "proposal.expired"));
        }

        [Fact]
        public void HardNotionalCapBlocks_SoftCapEscalatesToApproval()
        {
            // $600 > the 500 hard cap.
            var hard = Evaluate(Proposal(qty: 0.006m, stopLoss: 95_000m));
            Assert.Equal(RiskVerdict.Rejected, hard.Verdict);
            Assert.True(Failed(hard, "order.notional"));

            // $300 is over the 250 soft cap but under the hard cap: a human decides.
            var soft = Evaluate(Proposal(qty: 0.003m, stopLoss: 95_000m));
            Assert.Equal(RiskVerdict.RequiresApproval, soft.Verdict);
            Assert.True(Failed(soft, "order.notional_soft"));
        }

        [Fact]
        public void SpotSellCannotExceedFreeInventory()
        {
            var decision = Evaluate(Proposal(side: OrderSide.Sell, qty: 5m, stopLoss: 105_000m),
                portfolio: Portfolio(freeBtc: 1m));
            Assert.Equal(RiskVerdict.Rejected, decision.Verdict);
            Assert.True(Failed(decision, "inventory.insufficient"));

            var rule = decision.Rules.First(r => r.Rule == "inventory.insufficient");
            // The message must carry the venue's own stated reason, not merely "not enough size".
            Assert.Contains("spot cannot short", rule.Detail);
        }

        [Fact]
        public void ObserveAuthorityCannotPlaceOrders()
        {
            var decision = Evaluate(Proposal(authority: ExecutionAuthority.Observe, stopLoss: 95_000m));
            Assert.Equal(RiskVerdict.Rejected, decision.Verdict);
            Assert.True(Failed(decision, "authority.observe_only"));
        }

        [Fact]
        public void ApprovalRequiredAuthorityNeedsAHumanEvenWhenEveryRulePasses()
        {
            var decision = Evaluate(Proposal(authority: ExecutionAuthority.ApprovalRequired, stopLoss: 95_000m));
            Assert.Equal(RiskVerdict.RequiresApproval, decision.Verdict);
            Assert.Contains(decision.Rules, r => r.Rule == "authority.approval_required");
        }

        [Fact]
        public void UnsupportedOrderTypeIsBlockedWithTheVenueReason()
        {
            var capabilities = new VenueCapabilities
            {
                Venue = VenueId.Kraken,
                DisplayName = "Kraken (Spot)",
                Exposure = ExposureKind.Inventory,
                AssetClasses = new[] { AssetClass.Crypto },
                SupportsAttachedProtection = false,
                OrderTypes = new[] { OrderType.Market },
                Limitations = { ["SupportsAttachedProtection"] = "no brackets here" }
            };
            var decision = Evaluate(Proposal(type: OrderType.Limit, stopLoss: 95_000m), capabilities: capabilities);
            Assert.Equal(RiskVerdict.Rejected, decision.Verdict);
            Assert.True(Failed(decision, "ordertype.unsupported"));
            Assert.True(Failed(decision, "protection.unsupported"));
            Assert.Contains(decision.Rules, r => r.Rule == "protection.unsupported" && r.Detail == "no brackets here");
        }

        [Fact]
        public void StopOnTheWrongSideOfEntryIsRejected()
        {
            // A buy with a stop *above* entry would trigger the moment it is placed.
            var decision = Evaluate(Proposal(side: OrderSide.Buy, stopLoss: 105_000m));
            Assert.Equal(RiskVerdict.Rejected, decision.Verdict);
            Assert.True(Failed(decision, "stoploss.wrong_side"));
        }

        [Fact]
        public void QuantityBelowVenueMinimumIsRejected()
        {
            var decision = Evaluate(Proposal(qty: 0.00001m, stopLoss: 95_000m));
            Assert.True(Failed(decision, "qty.below_minimum"));
            Assert.Equal(RiskVerdict.Rejected, decision.Verdict);
        }

        [Fact]
        public void PortfolioLimitsMeasureTheAfterPictureNotJustTheOrder()
        {
            var portfolio = new RiskPortfolioState
            {
                GrossExposure = 4_950m,
                Equity = 10_000m,
                PeakEquity = 10_000m,
                FreeInventory = { ["BTC"] = 10m },
                AvailableFunds = 10_000m
            };
            // $100 on its own is fine; it is the resulting 5,050 gross that breaches.
            var decision = Evaluate(Proposal(stopLoss: 95_000m), portfolio: portfolio);
            Assert.Equal(RiskVerdict.Rejected, decision.Verdict);
            Assert.True(Failed(decision, "portfolio.gross_exposure"));
            Assert.Equal(5_050m, decision.ProjectedGrossExposure);
        }

        [Fact]
        public void FirmDailyLossAndDrawdownBlockNewExposure()
        {
            var lossy = new RiskPortfolioState
            {
                Equity = 10_000m,
                PeakEquity = 10_000m,
                DailyRealizedPnL = -300m,
                FreeInventory = { ["BTC"] = 10m }
            };
            Assert.True(Failed(Evaluate(Proposal(stopLoss: 95_000m), portfolio: lossy), "firm.daily_loss"));

            var drawn = new RiskPortfolioState
            {
                Equity = 8_000m,
                PeakEquity = 10_000m,
                FreeInventory = { ["BTC"] = 10m }
            };
            Assert.True(Failed(Evaluate(Proposal(stopLoss: 95_000m), portfolio: drawn), "firm.drawdown"));
        }

        [Fact]
        public void AnUnknownOrderBlocksAllNewExposure()
        {
            var ops = new RiskOperationalState { UnknownOrders = 1, VenueOrderPathHealthy = true };
            var decision = Evaluate(Proposal(stopLoss: 95_000m), ops: ops);
            Assert.Equal(RiskVerdict.Rejected, decision.Verdict);
            Assert.True(Failed(decision, "orders.unknown_outstanding"));
        }

        [Fact]
        public void UnreconciledBreaksAndSafeModeBlock()
        {
            Assert.True(Failed(Evaluate(Proposal(stopLoss: 95_000m),
                ops: new RiskOperationalState { UnreconciledBreaks = 2, VenueOrderPathHealthy = true }),
                "reconciliation.breaks"));

            Assert.True(Failed(Evaluate(Proposal(stopLoss: 95_000m),
                ops: new RiskOperationalState { SafeModeActive = true, SafeModeReason = "tripped", VenueOrderPathHealthy = true }),
                "safemode.active"));
        }

        [Fact]
        public void DegradedOrderPathBlocksSubmission()
        {
            var decision = Evaluate(Proposal(stopLoss: 95_000m),
                ops: new RiskOperationalState { VenueOrderPathHealthy = false });
            Assert.Equal(RiskVerdict.Rejected, decision.Verdict);
            Assert.True(Failed(decision, "venue.order_path_down"));
        }

        [Fact]
        public void EveryLayerIsRepresentedInTheDecisionRecord()
        {
            // A rejection has to be explainable, so the record keeps passes as well as failures.
            var decision = Evaluate(Proposal(stopLoss: 95_000m));
            foreach (var layer in new[] { RiskLayer.DataIntegrity, RiskLayer.OrderValidity, RiskLayer.TradeRisk,
                                          RiskLayer.StrategyRisk, RiskLayer.VenueRisk, RiskLayer.PortfolioRisk,
                                          RiskLayer.OperationalRisk })
                Assert.Contains(decision.Rules, r => r.Layer == layer);
        }

        [Fact]
        public void MissingProtectionIsASoftControlNotAHardBlock()
        {
            var decision = Evaluate(Proposal());
            Assert.Equal(RiskVerdict.RequiresApproval, decision.Verdict);
            Assert.Contains(decision.Rules, r => r.Rule == "trade.no_protection" && r.Severity == RiskSeverity.Soft);
        }
    }
}
