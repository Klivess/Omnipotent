using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Execution
{
    public enum RiskCheckOutcome { Allow, Block, Trip }

    public sealed class RiskGate
    {
        private readonly RiskCaps caps;
        private readonly Queue<DateTime> recentOrders = new();
        private readonly object syncRoot = new();
        private decimal dailyRealizedPnL;
        private DateTime currentDay = DateTime.UtcNow.Date;

        public bool Tripped { get; private set; }
        public string? TripReason { get; private set; }

        public RiskGate(RiskCaps caps)
        {
            this.caps = caps;
        }

        public RiskCheckOutcome Check(OrderRequest request, decimal markPrice, out string? reason)
        {
            reason = null;
            lock (syncRoot)
            {
                RollDayIfNeeded();
                if (Tripped) { reason = TripReason; return RiskCheckOutcome.Block; }

                if (caps.AllowedSymbols.Count > 0 && !caps.AllowedSymbols.Contains(request.Symbol))
                {
                    reason = $"Symbol {request.Symbol} not in allow-list";
                    return RiskCheckOutcome.Block;
                }

                if (request.Type == OrderType.Market || request.Type == OrderType.Limit)
                {
                    decimal notional = request.Qty * (request.LimitPrice ?? markPrice);
                    if (notional > caps.MaxPositionQuoteUsd)
                    {
                        reason = $"Notional {notional:F2} exceeds MaxPositionQuoteUsd {caps.MaxPositionQuoteUsd:F2}";
                        return RiskCheckOutcome.Block;
                    }
                }

                var cutoff = DateTime.UtcNow.AddHours(-1);
                while (recentOrders.Count > 0 && recentOrders.Peek() < cutoff) recentOrders.Dequeue();
                if (recentOrders.Count >= caps.MaxOrdersPerHour)
                {
                    reason = $"Order rate cap of {caps.MaxOrdersPerHour}/hr exceeded";
                    return RiskCheckOutcome.Block;
                }

                recentOrders.Enqueue(DateTime.UtcNow);
                return RiskCheckOutcome.Allow;
            }
        }

        public void RecordRealizedPnL(decimal pnl)
        {
            lock (syncRoot)
            {
                RollDayIfNeeded();
                dailyRealizedPnL += pnl;
                if (dailyRealizedPnL <= -Math.Abs(caps.MaxDailyLossUsd))
                {
                    Tripped = true;
                    TripReason = $"Daily loss {dailyRealizedPnL:F2} exceeded cap {-Math.Abs(caps.MaxDailyLossUsd):F2}";
                }
            }
        }

        public void Trip(string reason)
        {
            lock (syncRoot)
            {
                Tripped = true;
                TripReason = reason;
            }
        }

        public void Reset()
        {
            lock (syncRoot)
            {
                Tripped = false;
                TripReason = null;
                dailyRealizedPnL = 0;
                recentOrders.Clear();
            }
        }

        private void RollDayIfNeeded()
        {
            var today = DateTime.UtcNow.Date;
            if (today != currentDay)
            {
                currentDay = today;
                dailyRealizedPnL = 0;
            }
        }
    }
}
