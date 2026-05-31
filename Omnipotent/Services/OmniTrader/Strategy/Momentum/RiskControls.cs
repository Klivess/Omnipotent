namespace Omnipotent.Services.OmniTrader.Strategy.Momentum
{
    /// <summary>
    /// Section 9: portfolio-level risk controls. The drawdown killswitch tracks the equity peak and,
    /// once drawdown exceeds <see cref="MomentumConfig.DdKillswitch"/>, flags the book to be flattened
    /// and new entries paused until equity recovers above the reset threshold (peak·(1 − dd/2)).
    /// <para>Deliberately NO per-asset hard stops — the exit mechanism for this book is the weekly
    /// re-rank, not intraday stops (per the spec).</para>
    /// </summary>
    public sealed class KillswitchState
    {
        public decimal Peak { get; private set; }
        public bool Paused { get; private set; }

        /// <summary>Feed the current equity each bar. Returns true while trading is paused.</summary>
        public bool Update(decimal equity, MomentumConfig cfg)
        {
            if (equity > Peak) Peak = equity;
            if (Peak <= 0m) return Paused;

            decimal drawdown = (Peak - equity) / Peak;
            if (!Paused)
            {
                if (drawdown >= (decimal)cfg.DdKillswitch) Paused = true;
            }
            else
            {
                decimal resetLevel = Peak * (1m - (decimal)cfg.DdKillswitch / 2m);
                if (equity >= resetLevel) Paused = false;
            }
            return Paused;
        }
    }
}
