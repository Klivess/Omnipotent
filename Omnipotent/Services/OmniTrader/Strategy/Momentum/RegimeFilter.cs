using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Strategy.Momentum
{
    /// <summary>
    /// Section 6: market regime filter. The book is risk-on only when the regime asset (BTC, or a
    /// cap-weighted index) is above its trend MA. This is a switch on gross exposure, not a per-asset
    /// stop — it addresses the documented failure mode where momentum books blow up in sharp
    /// market-wide drawdowns.
    /// </summary>
    public static class RegimeFilter
    {
        /// <summary>
        /// True when price(regime) at t &gt; SMA(regime, ma_days, t). When there isn't yet enough
        /// history to form the MA, default to risk-on (don't block before the filter is meaningful).
        /// </summary>
        public static bool RegimeOn(IReadOnlyList<OHLCCandle> regimeHistory, int maDays)
        {
            if (regimeHistory == null || regimeHistory.Count == 0) return true;
            if (regimeHistory.Count < maDays) return true;

            decimal sum = 0m;
            for (int i = regimeHistory.Count - maDays; i < regimeHistory.Count; i++) sum += regimeHistory[i].Close;
            decimal sma = sum / maDays;
            return regimeHistory[^1].Close > sma;
        }
    }
}
