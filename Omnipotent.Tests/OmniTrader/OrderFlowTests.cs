using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.OrderFlow;
using Omnipotent.Services.OmniTrader.Risk;
using Omnipotent.Services.OmniTrader.Venues;

namespace Omnipotent.Tests.OmniTrader
{
    /// <summary>
    /// The order lifecycle invariants: only legal transitions, a stable idempotency key, and an
    /// unproven submission that stays unproven until reconciliation says otherwise.
    /// </summary>
    public class OrderFlowTests
    {
        private static FirmOrder Order(FirmOrderState state = FirmOrderState.Proposed) => new()
        {
            Id = "o1",
            ClientReference = "OTABC",
            ProposalId = "p1",
            RiskDecisionId = "d1",
            Venue = VenueId.Kraken,
            Environment = TradingEnvironment.Live,
            AccountId = "kraken-live",
            InstrumentId = "crypto:BTC/USD",
            VenueSymbol = "XBTUSD",
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = 1m,
            DecisionPrice = 100m,
            State = state
        };

        private static TradeProposal Proposal(decimal qty = 1m, string id = "p1") => new()
        {
            Id = id,
            InstrumentId = "crypto:BTC/USD",
            Venue = VenueId.Kraken,
            Environment = TradingEnvironment.Live,
            AccountId = "kraken-live",
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = qty,
            DecisionPrice = 100m,
            Authority = ExecutionAuthority.Automated
        };

        [Fact]
        public void HappyPathTransitionsAreLegal()
        {
            var order = Order();
            Assert.True(OrderStateMachine.Apply(order, FirmOrderState.RiskApproved, "risk"));
            Assert.True(OrderStateMachine.Apply(order, FirmOrderState.Submitting, "svc"));
            Assert.True(OrderStateMachine.Apply(order, FirmOrderState.Acknowledged, "kraken"));
            Assert.True(OrderStateMachine.Apply(order, FirmOrderState.Working, "kraken"));
            Assert.True(OrderStateMachine.Apply(order, FirmOrderState.PartiallyFilled, "kraken"));
            Assert.True(OrderStateMachine.Apply(order, FirmOrderState.Filled, "kraken"));
            Assert.Equal(FirmOrderState.Filled, order.State);
            Assert.NotNull(order.CompletedUtc);
            // Every hop is recorded — an order's history is the audit trail.
            Assert.Equal(6, order.History.Count);
        }

        [Fact]
        public void TerminalStatesAreTerminal()
        {
            var filled = Order(FirmOrderState.Filled);
            Assert.False(OrderStateMachine.Apply(filled, FirmOrderState.Working, "someone"));
            Assert.Equal(FirmOrderState.Filled, filled.State);

            var rejected = Order(FirmOrderState.Rejected);
            Assert.False(OrderStateMachine.Apply(rejected, FirmOrderState.Submitting, "retry"));
            Assert.Equal(FirmOrderState.Rejected, rejected.State);
        }

        [Fact]
        public void AProposedOrderCanNeverJumpStraightToSubmitting()
        {
            // This is the "no order bypasses risk" invariant expressed in the state machine.
            var order = Order();
            Assert.False(OrderStateMachine.CanTransition(FirmOrderState.Proposed, FirmOrderState.Submitting));
            Assert.False(OrderStateMachine.Apply(order, FirmOrderState.Submitting, "rogue"));
            Assert.Equal(FirmOrderState.Proposed, order.State);
            Assert.Empty(order.History);
        }

        [Fact]
        public void UnknownCannotBeResubmitted_OnlyResolved()
        {
            var order = Order(FirmOrderState.Unknown);
            Assert.False(OrderStateMachine.CanTransition(FirmOrderState.Unknown, FirmOrderState.Submitting));
            Assert.False(OrderStateMachine.CanTransition(FirmOrderState.Unknown, FirmOrderState.RiskApproved));

            // Reconciliation may prove any real outcome.
            foreach (var resolved in new[] { FirmOrderState.Filled, FirmOrderState.Cancelled,
                                             FirmOrderState.Rejected, FirmOrderState.Working })
                Assert.True(OrderStateMachine.CanTransition(FirmOrderState.Unknown, resolved));

            Assert.True(order.BlocksAutomation);
        }

        [Fact]
        public void IdempotencyKeyIsStableForTheSameProposal()
        {
            var proposal = Proposal();
            string first = OrderService.BuildClientReference(proposal);
            string second = OrderService.BuildClientReference(proposal);
            Assert.Equal(first, second);
        }

        [Fact]
        public void IdempotencyKeyChangesWhenTheOrderChanges()
        {
            string a = OrderService.BuildClientReference(Proposal(qty: 1m));
            string b = OrderService.BuildClientReference(Proposal(qty: 2m));
            string c = OrderService.BuildClientReference(Proposal(qty: 1m, id: "p2"));
            Assert.NotEqual(a, b);
            Assert.NotEqual(a, c);
        }

        [Fact]
        public void IdempotencyKeyFitsIgDealReferenceRules()
        {
            string reference = OrderService.BuildClientReference(Proposal());
            Assert.True(reference.Length <= 30, $"reference '{reference}' is {reference.Length} chars");
            Assert.All(reference, c => Assert.True(char.IsLetterOrDigit(c) || c == '-' || c == '_'));
            Assert.Equal(reference, IGVenueAdapter.SanitiseReference(reference));
        }

        [Fact]
        public void VenueStatusMapsOntoInternalState()
        {
            Assert.Equal(FirmOrderState.Working, OrderStateMachine.FromVenueStatus(OrderStatus.Open, 10m, 0m));
            Assert.Equal(FirmOrderState.PartiallyFilled, OrderStateMachine.FromVenueStatus(OrderStatus.Open, 10m, 4m));
            Assert.Equal(FirmOrderState.Filled, OrderStateMachine.FromVenueStatus(OrderStatus.Filled, 10m, 10m));
            Assert.Equal(FirmOrderState.Cancelled, OrderStateMachine.FromVenueStatus(OrderStatus.Cancelled, 10m, 0m));
            Assert.Equal(FirmOrderState.Rejected, OrderStateMachine.FromVenueStatus(OrderStatus.Rejected, 10m, 0m));
            // A partial fill that reaches full size is Filled — broker truth wins over the label.
            Assert.Equal(FirmOrderState.Filled, OrderStateMachine.FromVenueStatus(OrderStatus.PartiallyFilled, 10m, 10m));
        }

        [Fact]
        public void SlippageIsMeasuredAgainstTheDecisionPriceAndSignedBySide()
        {
            var buy = Order();
            buy.AverageFillPrice = 101m; // paid 1% more than the decision price
            Assert.Equal(100m, buy.SlippageBps);

            var sell = new FirmOrder
            {
                Id = "o2", ClientReference = "OTDEF", ProposalId = "p", RiskDecisionId = "d",
                Venue = VenueId.Kraken, Environment = TradingEnvironment.Live, AccountId = "a",
                InstrumentId = "crypto:BTC/USD", VenueSymbol = "XBTUSD",
                Side = OrderSide.Sell, Type = OrderType.Market, Quantity = 1m, DecisionPrice = 100m,
                AverageFillPrice = 99m // received 1% less — also adverse
            };
            Assert.Equal(100m, sell.SlippageBps);
        }

        [Fact]
        public void RemainingQuantityTracksPartialFills()
        {
            var order = Order(FirmOrderState.PartiallyFilled);
            order.FilledQuantity = 0.25m;
            Assert.Equal(0.75m, order.RemainingQuantity);
            Assert.True(order.IsLive);
            Assert.False(order.IsTerminal);
        }

        [Fact]
        public void IgReferenceSanitiserEnforcesTheVenueConstraint()
        {
            string sanitised = IGVenueAdapter.SanitiseReference("abc/def:ghi jkl-mno_pqr stu vwx yz0123456789");
            Assert.True(sanitised.Length <= 30);
            Assert.DoesNotContain('/', sanitised);
            Assert.DoesNotContain(':', sanitised);
            Assert.DoesNotContain(' ', sanitised);
        }
    }
}
