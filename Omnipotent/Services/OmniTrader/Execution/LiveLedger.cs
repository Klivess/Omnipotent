using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Execution
{
    /// <summary>
    /// Tracks a live deployment's cash, position and realized PnL by booking fills as they come back
    /// from the exchange. Pure and self-contained (no I/O) so the accounting is unit-tested directly.
    ///
    /// Cash convention matches the simulator: a buy spends quote (may go negative = borrowed on
    /// margin) and adds base; a sell adds quote and reduces base (may go negative = short). Realized
    /// PnL is booked on the portion of a fill that closes against the existing average entry, net of
    /// that fill's fee — this is what feeds the RiskGate daily-loss cap.
    /// </summary>
    public sealed class LiveLedger
    {
        public Position? Position { get; private set; }
        public decimal QuoteBalance { get; private set; }
        public decimal BaseBalance { get; private set; }
        public decimal RealizedPnL { get; private set; }
        public decimal Fees { get; private set; }

        public LiveLedger(decimal quoteBalance, decimal baseBalance)
        {
            QuoteBalance = quoteBalance;
            BaseBalance = baseBalance;
        }

        /// <summary>Book a fill. Returns the realized PnL it produced (negative = loss).</summary>
        public decimal ApplyFill(OrderSide side, decimal qty, decimal price, decimal fee, string symbol, DateTime ts)
        {
            if (qty <= 0m) return 0m;
            decimal notional = qty * price;

            if (side == OrderSide.Buy) { QuoteBalance -= notional + fee; BaseBalance += qty; }
            else                       { QuoteBalance += notional - fee; BaseBalance -= qty; }
            Fees += fee;

            decimal realized = UpdatePosition(side, qty, price, ts, symbol) - fee;
            RealizedPnL += realized;
            return realized;
        }

        // Mirrors the simulator's averaging/sign-flip logic, and additionally returns the gross
        // realized PnL (price difference on the closed quantity).
        private decimal UpdatePosition(OrderSide side, decimal qty, decimal price, DateTime ts, string symbol)
        {
            decimal signed = side == OrderSide.Buy ? qty : -qty;

            if (Position == null || Position.Qty == 0m)
            {
                Position = new Position { Symbol = symbol, OpenedUtc = ts, AveragePrice = price, Qty = signed };
                return 0m;
            }

            decimal pos = Position.Qty;
            if (Math.Sign(signed) == Math.Sign(pos))
            {
                // Same direction: weighted-average the entry.
                decimal totalCost = Position.AveragePrice * Math.Abs(pos) + price * qty;
                Position.AveragePrice = totalCost / (Math.Abs(pos) + qty);
                Position.Qty = pos + signed;
                return 0m;
            }

            // Opposing: realize on the closed portion against the average entry.
            decimal closeQty = Math.Min(qty, Math.Abs(pos));
            decimal dir = Math.Sign(pos); // +1 long, -1 short
            decimal realized = closeQty * (price - Position.AveragePrice) * dir;

            decimal newQty = pos + signed;
            if (newQty == 0m) { Position = null; return realized; }
            if (Math.Sign(newQty) != Math.Sign(pos))
            {
                // Flipped through zero: the remainder opens a fresh position at this price.
                Position.AveragePrice = price;
                Position.OpenedUtc = ts;
            }
            Position.Qty = newQty;
            return realized;
        }
    }
}
