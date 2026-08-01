using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Risk;
using Omnipotent.Services.OmniTrader.Venues;

namespace Omnipotent.Services.OmniTrader.OrderFlow
{
    /// <summary>
    /// The order lifecycle. Every transition is audited, and the terminal set is deliberately narrow:
    /// an order is <see cref="Filled"/>, <see cref="Cancelled"/> or <see cref="Rejected"/>, or it sits
    /// in <see cref="Unknown"/> until reconciliation proves what happened.
    /// </summary>
    public enum FirmOrderState
    {
        /// <summary>Created by a strategy or manual ticket. No broker authority whatsoever.</summary>
        Proposed = 0,
        /// <summary>Held for a human decision under the active deployment mode.</summary>
        AwaitingApproval = 1,
        /// <summary>Pre-trade decision recorded as approved.</summary>
        RiskApproved = 2,
        /// <summary>Pre-trade decision recorded as rejected. Terminal.</summary>
        RiskRejected = 3,
        /// <summary>Idempotency key assigned and sent once to the adapter.</summary>
        Submitting = 4,
        /// <summary>Broker reference received; the order is externally addressable.</summary>
        Acknowledged = 5,
        Working = 6,
        PartiallyFilled = 7,
        Filled = 8,
        Cancelled = 9,
        /// <summary>The venue refused it. Terminal.</summary>
        Rejected = 10,
        /// <summary>The submission outcome cannot be proven. Automation must not retry.</summary>
        Unknown = 11
    }

    public sealed class OrderTransition
    {
        public required FirmOrderState From { get; init; }
        public required FirmOrderState To { get; init; }
        public required DateTime AtUtc { get; init; }
        public required string Actor { get; init; }
        public string? Reason { get; init; }
    }

    /// <summary>
    /// The platform's canonical order record. Broker truth determines external reality; this record
    /// provides attribution, history and the link back to the signal and risk decision that produced it.
    /// </summary>
    public sealed class FirmOrder
    {
        public required string Id { get; init; }
        /// <summary>The idempotency key sent to the venue. Stable for the life of the order — a resend
        /// with the same key is the same order, never a second one.</summary>
        public required string ClientReference { get; init; }
        public required string ProposalId { get; init; }
        public required string RiskDecisionId { get; init; }

        public required VenueId Venue { get; init; }
        public required TradingEnvironment Environment { get; init; }
        public required string AccountId { get; init; }
        public required string InstrumentId { get; init; }
        public required string VenueSymbol { get; init; }

        public required OrderSide Side { get; init; }
        public required OrderType Type { get; init; }
        public required decimal Quantity { get; init; }
        public decimal? LimitPrice { get; init; }
        public decimal? StopPrice { get; init; }
        public decimal? StopLossPrice { get; init; }
        public decimal? TakeProfitPrice { get; init; }

        public string? StrategyId { get; init; }
        public string? StrategyVersion { get; init; }
        public string? DeploymentId { get; init; }

        public FirmOrderState State { get; set; } = FirmOrderState.Proposed;
        public string? VenueOrderId { get; set; }
        public decimal FilledQuantity { get; set; }
        public decimal? AverageFillPrice { get; set; }
        public decimal Fees { get; set; }
        public string FeeCurrency { get; set; } = "";
        public string? Error { get; set; }

        /// <summary>The mark at the moment the decision was taken — slippage is measured against it.</summary>
        public decimal DecisionPrice { get; init; }
        public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
        public DateTime? SubmittedUtc { get; set; }
        public DateTime? AcknowledgedUtc { get; set; }
        public DateTime? CompletedUtc { get; set; }

        /// <summary>Set when a human approved or rejected this order (approval-required mode).</summary>
        public string? ApprovedBy { get; set; }
        public DateTime? ApprovedUtc { get; set; }

        /// <summary>The order this one modifies or cancels — every amendment is a new audited action.</summary>
        public string? AmendsOrderId { get; init; }

        public List<OrderTransition> History { get; init; } = new();

        public decimal RemainingQuantity => Math.Max(0m, Quantity - FilledQuantity);
        public bool IsTerminal => State is FirmOrderState.Filled or FirmOrderState.Cancelled
                                       or FirmOrderState.Rejected or FirmOrderState.RiskRejected;
        public bool IsLive => State is FirmOrderState.Acknowledged or FirmOrderState.Working or FirmOrderState.PartiallyFilled;
        public bool BlocksAutomation => State == FirmOrderState.Unknown;

        /// <summary>Slippage in basis points against the decision price, once there is a fill to measure.</summary>
        public decimal? SlippageBps
        {
            get
            {
                if (AverageFillPrice is not > 0m || DecisionPrice <= 0m) return null;
                decimal adverse = Side == OrderSide.Buy
                    ? AverageFillPrice.Value - DecisionPrice
                    : DecisionPrice - AverageFillPrice.Value;
                return adverse / DecisionPrice * 10_000m;
            }
        }

        public TimeSpan? SubmissionLatency => SubmittedUtc.HasValue && AcknowledgedUtc.HasValue
            ? AcknowledgedUtc.Value - SubmittedUtc.Value : null;
    }

    /// <summary>Which transitions are legal. Anything not listed here is a bug, not a state to handle.</summary>
    public static class OrderStateMachine
    {
        private static readonly Dictionary<FirmOrderState, FirmOrderState[]> Allowed = new()
        {
            [FirmOrderState.Proposed] = new[] { FirmOrderState.AwaitingApproval, FirmOrderState.RiskApproved, FirmOrderState.RiskRejected },
            [FirmOrderState.AwaitingApproval] = new[] { FirmOrderState.RiskApproved, FirmOrderState.RiskRejected, FirmOrderState.Cancelled },
            [FirmOrderState.RiskApproved] = new[] { FirmOrderState.Submitting, FirmOrderState.Cancelled },
            [FirmOrderState.Submitting] = new[] { FirmOrderState.Acknowledged, FirmOrderState.Rejected, FirmOrderState.Unknown, FirmOrderState.Filled },
            [FirmOrderState.Acknowledged] = new[] { FirmOrderState.Working, FirmOrderState.PartiallyFilled, FirmOrderState.Filled, FirmOrderState.Cancelled, FirmOrderState.Rejected, FirmOrderState.Unknown },
            [FirmOrderState.Working] = new[] { FirmOrderState.PartiallyFilled, FirmOrderState.Filled, FirmOrderState.Cancelled, FirmOrderState.Rejected, FirmOrderState.Unknown },
            [FirmOrderState.PartiallyFilled] = new[] { FirmOrderState.PartiallyFilled, FirmOrderState.Filled, FirmOrderState.Cancelled, FirmOrderState.Unknown },
            // Unknown is only ever left by reconciliation proving an outcome.
            [FirmOrderState.Unknown] = new[] { FirmOrderState.Acknowledged, FirmOrderState.Working, FirmOrderState.PartiallyFilled, FirmOrderState.Filled, FirmOrderState.Cancelled, FirmOrderState.Rejected },
            [FirmOrderState.Filled] = Array.Empty<FirmOrderState>(),
            [FirmOrderState.Cancelled] = Array.Empty<FirmOrderState>(),
            [FirmOrderState.Rejected] = Array.Empty<FirmOrderState>(),
            [FirmOrderState.RiskRejected] = Array.Empty<FirmOrderState>()
        };

        public static bool CanTransition(FirmOrderState from, FirmOrderState to)
            => Allowed.TryGetValue(from, out var next) && next.Contains(to);

        /// <summary>Apply a transition, recording it in the order's history. Returns false (and changes
        /// nothing) when the transition is not legal.</summary>
        public static bool Apply(FirmOrder order, FirmOrderState to, string actor, string? reason = null)
        {
            if (order.State == to && to != FirmOrderState.PartiallyFilled) return true;
            if (!CanTransition(order.State, to)) return false;

            order.History.Add(new OrderTransition
            {
                From = order.State,
                To = to,
                AtUtc = DateTime.UtcNow,
                Actor = actor,
                Reason = reason
            });
            order.State = to;
            if (to is FirmOrderState.Filled or FirmOrderState.Cancelled or FirmOrderState.Rejected or FirmOrderState.RiskRejected)
                order.CompletedUtc = DateTime.UtcNow;
            return true;
        }

        /// <summary>Map broker-reported status onto the internal state. Broker truth wins: a fill count
        /// that disagrees with the venue is a reconciliation break, not a state to preserve.</summary>
        public static FirmOrderState FromVenueStatus(OrderStatus status, decimal quantity, decimal filled) => status switch
        {
            OrderStatus.Pending => FirmOrderState.Acknowledged,
            OrderStatus.Open => filled > 0m ? FirmOrderState.PartiallyFilled : FirmOrderState.Working,
            OrderStatus.PartiallyFilled => filled >= quantity ? FirmOrderState.Filled : FirmOrderState.PartiallyFilled,
            OrderStatus.Filled => FirmOrderState.Filled,
            OrderStatus.Cancelled => FirmOrderState.Cancelled,
            OrderStatus.Rejected => FirmOrderState.Rejected,
            _ => FirmOrderState.Unknown
        };
    }
}
