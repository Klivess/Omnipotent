using Omnipotent.Services.OmniTrader.Contracts;
using Omnipotent.Services.OmniTrader.Instruments;
using Omnipotent.Services.OmniTrader.Ledger;
using Omnipotent.Services.OmniTrader.Ops;
using Omnipotent.Services.OmniTrader.Persistence;
using Omnipotent.Services.OmniTrader.Risk;
using Omnipotent.Services.OmniTrader.Venues;
using System.Security.Cryptography;
using System.Text;

namespace Omnipotent.Services.OmniTrader.OrderFlow
{
    public sealed class OrderSubmissionResult
    {
        public required FirmOrder? Order { get; init; }
        public required RiskDecision Decision { get; init; }
        public required bool Submitted { get; init; }
        public string? Message { get; init; }
        public bool AwaitingApproval => Order?.State == FirmOrderState.AwaitingApproval;
        public bool Blocked => Order == null || Order.State == FirmOrderState.RiskRejected;
    }

    /// <summary>
    /// Owns order state, broker submission, confirmation, cancellation and reconciliation. It is the
    /// **only** path to a venue, and it cannot bypass risk: a proposal is evaluated before anything is
    /// persisted as submittable, and submission requires an approved decision id.
    ///
    /// Three invariants hold everything together:
    /// <list type="number">
    /// <item>Every submission carries a stable idempotency key, and the key is UNIQUE in the store, so
    /// the same intent cannot become two broker orders.</item>
    /// <item>A submission whose outcome cannot be proven becomes <see cref="FirmOrderState.Unknown"/>
    /// and is never retried — only reconciliation may move it out.</item>
    /// <item>Every state change is recorded as an audited transition on the order itself.</item>
    /// </list>
    /// </summary>
    public sealed class OrderService
    {
        private readonly VenueRegistry venues;
        private readonly InstrumentMaster instruments;
        private readonly RiskEngine risk;
        private readonly EmergencyControls emergency;
        private readonly FirmOrderRepository orderRepo;
        private readonly RiskDecisionRepository riskRepo;
        private readonly FirmLedger ledger;
        private readonly AlertService alerts;
        private readonly AuditRepository audit;
        private readonly Func<Task<RiskPortfolioState>> portfolioProvider;
        private readonly Action<string>? log;
        private readonly SemaphoreSlim submitLock = new(1, 1);

        public OrderService(
            VenueRegistry venues,
            InstrumentMaster instruments,
            RiskEngine risk,
            EmergencyControls emergency,
            FirmOrderRepository orderRepo,
            RiskDecisionRepository riskRepo,
            FirmLedger ledger,
            AlertService alerts,
            AuditRepository audit,
            Func<Task<RiskPortfolioState>> portfolioProvider,
            Action<string>? log = null)
        {
            this.venues = venues;
            this.instruments = instruments;
            this.risk = risk;
            this.emergency = emergency;
            this.orderRepo = orderRepo;
            this.riskRepo = riskRepo;
            this.ledger = ledger;
            this.alerts = alerts;
            this.audit = audit;
            this.portfolioProvider = portfolioProvider;
            this.log = log;
        }

        /// <summary>
        /// Take a proposal all the way to a decision, and — when authority and risk allow — to the
        /// venue. Returns the recorded decision either way, so a rejection is as auditable as a fill.
        /// </summary>
        public async Task<OrderSubmissionResult> SubmitProposalAsync(TradeProposal proposal, string actor, CancellationToken ct = default)
        {
            var adapter = venues.Resolve(proposal.Venue, proposal.Environment);
            var instrument = instruments.Resolve(proposal.InstrumentId, proposal.Venue);
            var freshness = instruments.GetFreshness(instrument?.Id ?? proposal.InstrumentId);
            var portfolio = await portfolioProvider();
            var operations = await BuildOperationalStateAsync(adapter, ct);

            var capabilities = adapter?.Capabilities ?? new VenueCapabilities
            {
                Venue = proposal.Venue,
                DisplayName = proposal.Venue.ToString(),
                Exposure = ExposureKind.Inventory,
                AssetClasses = Array.Empty<AssetClass>(),
                Limitations = { ["Configured"] = "No adapter is registered for this venue and environment." }
            };

            var decision = risk.Evaluate(proposal, instrument, capabilities, freshness, portfolio, operations);

            // An engaged kill switch is a hard block layered on top of the rule engine so an operator's
            // explicit decision cannot be out-voted by a clean rule sweep.
            if (emergency.IsBlocked(proposal.Venue, proposal.AccountId, proposal.StrategyId, out var blockReason))
            {
                decision.Rules.Add(RiskRuleResult.Hard(RiskLayer.OperationalRisk, "killswitch.engaged",
                    blockReason ?? "An emergency control is engaged for this scope."));
                decision = Reject(decision);
            }

            if (adapter == null)
                decision = Reject(WithRule(decision, RiskRuleResult.Hard(RiskLayer.OperationalRisk, "venue.not_registered",
                    $"No adapter registered for {proposal.Venue}/{proposal.Environment}.")));

            await riskRepo.RecordAsync(proposal, decision, ct);

            if (decision.Verdict == RiskVerdict.Rejected)
            {
                await audit.AppendAsync(actor, "order.risk_rejected", proposal.InstrumentId, decision.Summary, decision, ct);
                return new OrderSubmissionResult { Order = null, Decision = decision, Submitted = false, Message = decision.Summary };
            }

            var order = BuildOrder(proposal, decision, adapter!);
            // Idempotency at the boundary: if this reference already produced an order, that IS the
            // order. Callers retrying a proposal get the original back rather than a second submission.
            var existing = await orderRepo.GetByClientReferenceAsync(order.ClientReference, ct);
            if (existing != null)
                return new OrderSubmissionResult
                {
                    Order = existing,
                    Decision = decision,
                    Submitted = false,
                    Message = "Idempotent replay — this client reference already has an order."
                };

            if (decision.Verdict == RiskVerdict.RequiresApproval)
            {
                OrderStateMachine.Apply(order, FirmOrderState.AwaitingApproval, actor, decision.Summary);
                await orderRepo.UpsertAsync(order, ct);
                await audit.AppendAsync(actor, "order.awaiting_approval", order.InstrumentId, decision.Summary, null, ct);
                await alerts.RaiseAsync(AlertSeverity.Medium, "orders", "Order awaiting approval",
                    $"{order.Side} {order.Quantity} {order.InstrumentId} on {order.Venue} needs a decision: {decision.Summary}",
                    dedupeKey: $"approval:{order.Id}", venue: order.Venue.ToString(),
                    environment: order.Environment.ToString(), ct: ct);
                return new OrderSubmissionResult { Order = order, Decision = decision, Submitted = false, Message = "Awaiting approval" };
            }

            OrderStateMachine.Apply(order, FirmOrderState.RiskApproved, "risk-engine");
            await orderRepo.UpsertAsync(order, ct);
            await SubmitApprovedAsync(order, adapter!, actor, ct);
            return new OrderSubmissionResult
            {
                Order = order,
                Decision = decision,
                Submitted = order.State != FirmOrderState.RiskApproved,
                Message = order.Error
            };
        }

        /// <summary>Human approval of a held order. The approval itself is audited and stamped onto the
        /// order — a live fill can always be traced back to who authorised it.</summary>
        public async Task<FirmOrder?> ApproveAsync(string orderId, string approvedBy, CancellationToken ct = default)
        {
            var order = await orderRepo.GetAsync(orderId, ct);
            if (order == null || order.State != FirmOrderState.AwaitingApproval) return null;

            var adapter = venues.Resolve(order.Venue, order.Environment);
            if (adapter == null)
            {
                order.Error = $"No adapter for {order.Venue}/{order.Environment}";
                OrderStateMachine.Apply(order, FirmOrderState.RiskRejected, approvedBy, order.Error);
                await orderRepo.UpsertAsync(order, ct);
                return order;
            }

            order.ApprovedBy = approvedBy;
            order.ApprovedUtc = DateTime.UtcNow;
            OrderStateMachine.Apply(order, FirmOrderState.RiskApproved, approvedBy, "approved by operator");
            await orderRepo.UpsertAsync(order, ct);
            await audit.AppendAsync(approvedBy, "order.approved", order.InstrumentId,
                $"{order.Side} {order.Quantity} {order.InstrumentId}", null, ct);
            await alerts.ResolveByDedupeAsync($"approval:{order.Id}", ct);

            await SubmitApprovedAsync(order, adapter, approvedBy, ct);
            return order;
        }

        public async Task<FirmOrder?> RejectAsync(string orderId, string rejectedBy, string reason, CancellationToken ct = default)
        {
            var order = await orderRepo.GetAsync(orderId, ct);
            if (order == null || order.State != FirmOrderState.AwaitingApproval) return null;
            order.Error = reason;
            OrderStateMachine.Apply(order, FirmOrderState.RiskRejected, rejectedBy, reason);
            await orderRepo.UpsertAsync(order, ct);
            await audit.AppendAsync(rejectedBy, "order.rejected_by_operator", order.InstrumentId, reason, null, ct);
            await alerts.ResolveByDedupeAsync($"approval:{order.Id}", ct);
            return order;
        }

        /// <summary>
        /// The single place a venue is written to. Serialised so two callers cannot race the same
        /// order onto the wire, and the outcome is always resolved into a definite state — including
        /// the definite state of "we do not know".
        /// </summary>
        private async Task SubmitApprovedAsync(FirmOrder order, IVenueAdapter adapter, string actor, CancellationToken ct)
        {
            await submitLock.WaitAsync(ct);
            try
            {
                if (order.State != FirmOrderState.RiskApproved) return;

                OrderStateMachine.Apply(order, FirmOrderState.Submitting, actor);
                order.SubmittedUtc = DateTime.UtcNow;
                await orderRepo.UpsertAsync(order, ct);

                var request = new OrderRequest
                {
                    IntentId = order.ClientReference,
                    Side = order.Side,
                    Type = order.Type,
                    Symbol = order.VenueSymbol,
                    Qty = order.Quantity,
                    LimitPrice = order.LimitPrice,
                    StopPrice = order.StopPrice,
                    StopLossPrice = order.StopLossPrice,
                    TakeProfitPrice = order.TakeProfitPrice
                };

                VenueSubmissionResult result;
                try { result = await adapter.SubmitOrderAsync(request, order.ClientReference, ct); }
                catch (Exception ex) { result = VenueSubmissionResult.Unknown(ex.Message, order.ClientReference); }

                order.AcknowledgedUtc = DateTime.UtcNow;

                switch (result.Outcome)
                {
                    case SubmissionOutcome.Accepted:
                        order.VenueOrderId = result.VenueOrderId;
                        OrderStateMachine.Apply(order, FirmOrderState.Acknowledged, adapter.Venue.ToString());
                        await orderRepo.UpsertAsync(order, ct);
                        await audit.AppendAsync(actor, "order.submitted", order.InstrumentId,
                            $"{order.Side} {order.Quantity} {order.VenueSymbol} → {order.VenueOrderId}", null, ct);
                        // The internal simulator fills synchronously; pull its state straight back so
                        // paper P&L is booked on the same code path live uses.
                        await ReconcileOrderAsync(order, adapter, ct);
                        break;

                    case SubmissionOutcome.Rejected:
                        order.Error = result.Reason;
                        OrderStateMachine.Apply(order, FirmOrderState.Rejected, adapter.Venue.ToString(), result.Reason);
                        await orderRepo.UpsertAsync(order, ct);
                        await audit.AppendAsync(actor, "order.venue_rejected", order.InstrumentId, result.Reason, null, ct);
                        await alerts.RaiseAsync(AlertSeverity.Medium, "orders", "Broker rejected an order",
                            $"{order.Venue} rejected {order.Side} {order.Quantity} {order.VenueSymbol}: {result.Reason}",
                            dedupeKey: $"reject:{order.Venue}:{order.VenueSymbol}",
                            venue: order.Venue.ToString(), environment: order.Environment.ToString(), ct: ct);
                        break;

                    default:
                        // We cannot prove what happened. Park the order, block automation, and shout.
                        order.Error = result.Reason;
                        order.VenueOrderId = result.VenueOrderId;
                        OrderStateMachine.Apply(order, FirmOrderState.Unknown, adapter.Venue.ToString(), result.Reason);
                        await orderRepo.UpsertAsync(order, ct);
                        emergency.EnterSafeMode(
                            $"Order {order.ClientReference} has an unproven outcome at {order.Venue}",
                            "order-service", automatic: true);
                        await alerts.CriticalAsync("orders", "Unknown live order outcome",
                            $"{order.Side} {order.Quantity} {order.VenueSymbol} at {order.Venue} could not be resolved: {result.Reason}. "
                            + "Automation is blocked and this order will NOT be retried.",
                            dedupeKey: $"unknown:{order.Id}",
                            recoveryHint: "Reconcile against the broker, then resolve the order on the Execution page.",
                            ct: ct);
                        await audit.AppendAsync(actor, "order.unknown_outcome", order.InstrumentId, result.Reason, null, ct);
                        break;
                }
            }
            finally { submitLock.Release(); }
        }

        /// <summary>Cancel a working order. A cancellation is a new audited action linked to the
        /// original; it never rewrites the original's history.</summary>
        public async Task<bool> CancelAsync(string orderId, string actor, CancellationToken ct = default)
        {
            var order = await orderRepo.GetAsync(orderId, ct);
            if (order == null) return false;

            if (order.State == FirmOrderState.AwaitingApproval)
            {
                OrderStateMachine.Apply(order, FirmOrderState.Cancelled, actor, "cancelled before approval");
                await orderRepo.UpsertAsync(order, ct);
                await audit.AppendAsync(actor, "order.cancelled", order.InstrumentId, "cancelled before approval", null, ct);
                return true;
            }

            if (!order.IsLive || string.IsNullOrWhiteSpace(order.VenueOrderId)) return false;
            var adapter = venues.Resolve(order.Venue, order.Environment);
            if (adapter == null) return false;

            bool ok = await adapter.CancelOrderAsync(order.VenueOrderId!, ct);
            await audit.AppendAsync(actor, "order.cancel_requested", order.InstrumentId,
                $"{order.VenueOrderId} → {(ok ? "accepted" : "refused")}", null, ct);
            // Broker truth decides the resulting state, not our request.
            await ReconcileOrderAsync(order, adapter, ct);
            return ok;
        }

        /// <summary>
        /// Pull broker truth for one order and fold it in. This is the only way an order leaves
        /// <see cref="FirmOrderState.Unknown"/>, and the only place fills are booked to the ledger.
        /// </summary>
        public async Task<FirmOrder> ReconcileOrderAsync(FirmOrder order, IVenueAdapter adapter, CancellationToken ct = default)
        {
            // For an unknown order we may only hold the client reference; both venues accept it as a
            // lookup key, which is exactly why the reference is the idempotency key.
            string lookup = order.VenueOrderId ?? order.ClientReference;
            IReadOnlyList<VenueOrderSnapshot> snapshots;
            try { snapshots = await adapter.QueryOrdersAsync(new[] { lookup }, ct); }
            catch (Exception ex) { log?.Invoke($"reconcile {order.Id} failed: {ex.Message}"); return order; }

            var snapshot = snapshots.FirstOrDefault();
            if (snapshot == null)
            {
                // Absence is not proof. An unknown order stays unknown.
                if (order.State == FirmOrderState.Unknown)
                    log?.Invoke($"order {order.ClientReference} still unresolved at {adapter.Venue}");
                return order;
            }

            decimal previouslyFilled = order.FilledQuantity;
            decimal newlyFilled = snapshot.FilledQuantity - previouslyFilled;

            order.VenueOrderId ??= snapshot.VenueOrderId;
            order.FilledQuantity = snapshot.FilledQuantity;
            order.AverageFillPrice = snapshot.AverageFillPrice ?? order.AverageFillPrice;
            order.Fees = snapshot.Fee;
            order.FeeCurrency = string.IsNullOrWhiteSpace(snapshot.FeeCurrency) ? order.FeeCurrency : snapshot.FeeCurrency;
            if (!string.IsNullOrWhiteSpace(snapshot.Reason)) order.Error = snapshot.Reason;

            var target = OrderStateMachine.FromVenueStatus(snapshot.Status, order.Quantity, snapshot.FilledQuantity);
            bool wasUnknown = order.State == FirmOrderState.Unknown;
            if (!OrderStateMachine.Apply(order, target, $"reconcile:{adapter.Venue}"))
                log?.Invoke($"order {order.Id}: refused illegal transition {order.State} → {target}");

            await orderRepo.UpsertAsync(order, ct);

            // Book only the increment, so a repeated reconciliation cannot double-count a fill.
            if (newlyFilled > 0m && order.AverageFillPrice is > 0m)
            {
                await ledger.BookFillAsync(order, newlyFilled, order.AverageFillPrice.Value,
                    snapshot.Fee - (previouslyFilled > 0m ? order.Fees - snapshot.Fee : 0m), ct);
            }

            if (wasUnknown && order.State != FirmOrderState.Unknown)
            {
                await alerts.ResolveByDedupeAsync($"unknown:{order.Id}", ct);
                await audit.AppendAsync("reconciliation", "order.unknown_resolved", order.InstrumentId,
                    $"{order.ClientReference} resolved to {order.State}", null, ct);
                // Automation stays blocked until *every* unknown clears, not just this one.
                var remaining = await orderRepo.ListUnknownAsync(ct);
                if (remaining.Count == 0 && emergency.SafeModeActive
                    && (emergency.SafeModeReason?.Contains("unproven outcome", StringComparison.OrdinalIgnoreCase) ?? false))
                    emergency.ExitSafeMode("reconciliation");
            }

            return order;
        }

        /// <summary>Reconcile everything that is open or unknown. Called at startup, after reconnects,
        /// after ambiguous outcomes and on a schedule.</summary>
        public async Task<int> ReconcileOutstandingAsync(CancellationToken ct = default)
        {
            var open = await orderRepo.ListOpenAsync(ct);
            var unknown = await orderRepo.ListUnknownAsync(ct);
            int count = 0;
            foreach (var order in open.Concat(unknown).DistinctBy(o => o.Id))
            {
                var adapter = venues.Resolve(order.Venue, order.Environment);
                if (adapter == null) continue;
                await ReconcileOrderAsync(order, adapter, ct);
                count++;
            }
            return count;
        }

        public async Task<RiskOperationalState> BuildOperationalStateAsync(IVenueAdapter? adapter, CancellationToken ct = default)
        {
            var unknown = await orderRepo.ListUnknownAsync(ct);
            int rejections = await orderRepo.CountRejectionsSinceAsync(DateTime.UtcNow.AddHours(-1), ct);
            int breaks = await ledger.CountMaterialBreaksAsync(ct);
            return new RiskOperationalState
            {
                UnknownOrders = unknown.Count,
                UnreconciledBreaks = breaks,
                RecentRejections = rejections,
                VenueOrderPathHealthy = adapter?.Health.OrderPathHealthy ?? false,
                SafeModeActive = emergency.SafeModeActive,
                SafeModeReason = emergency.SafeModeReason
            };
        }

        // ── construction helpers ──────────────────────────────────────────────────

        private FirmOrder BuildOrder(TradeProposal proposal, RiskDecision decision, IVenueAdapter adapter)
        {
            var instrument = instruments.Resolve(proposal.InstrumentId, proposal.Venue);
            string venueSymbol = instrument != null
                ? instruments.VenueSymbolFor(instrument.Id, proposal.Venue)
                : proposal.InstrumentId;

            return new FirmOrder
            {
                Id = Guid.NewGuid().ToString("N"),
                ClientReference = BuildClientReference(proposal),
                ProposalId = proposal.Id,
                RiskDecisionId = decision.Id,
                Venue = proposal.Venue,
                Environment = proposal.Environment,
                AccountId = proposal.AccountId,
                InstrumentId = instrument?.Id ?? proposal.InstrumentId,
                VenueSymbol = venueSymbol,
                Side = proposal.Side,
                Type = proposal.Type,
                Quantity = proposal.Quantity,
                LimitPrice = proposal.LimitPrice,
                StopPrice = proposal.StopPrice,
                StopLossPrice = proposal.StopLossPrice,
                TakeProfitPrice = proposal.TakeProfitPrice,
                StrategyId = proposal.StrategyId,
                StrategyVersion = proposal.StrategyVersion,
                DeploymentId = proposal.DeploymentId,
                DecisionPrice = proposal.DecisionPrice
            };
        }

        /// <summary>
        /// Derive the idempotency key from the proposal's identity, not from a clock or a random value.
        /// Re-submitting the same proposal therefore produces the same key — and the UNIQUE constraint
        /// on it turns a duplicate submission into a no-op rather than a second live order.
        /// </summary>
        public static string BuildClientReference(TradeProposal proposal)
        {
            string material = string.Join("|", proposal.Id, proposal.InstrumentId, proposal.Venue,
                proposal.Environment, proposal.Side, proposal.Type, proposal.Quantity.ToString("G29"),
                proposal.LimitPrice?.ToString("G29") ?? "-", proposal.AccountId);
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
            // 24 hex chars keeps it inside IG's 30-character deal-reference limit with room for a prefix.
            return "OT" + Convert.ToHexString(hash)[..22];
        }

        private static RiskDecision Reject(RiskDecision decision) => new()
        {
            Id = decision.Id,
            ProposalId = decision.ProposalId,
            Verdict = RiskVerdict.Rejected,
            DecidedUtc = decision.DecidedUtc,
            Rules = decision.Rules,
            ProjectedGrossExposure = decision.ProjectedGrossExposure,
            ProjectedNetExposure = decision.ProjectedNetExposure,
            ProjectedVenueExposure = decision.ProjectedVenueExposure
        };

        private static RiskDecision WithRule(RiskDecision decision, RiskRuleResult rule)
        {
            decision.Rules.Add(rule);
            return decision;
        }
    }
}
