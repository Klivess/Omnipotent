using Omnipotent.Services.OmniTrader.Data;
using Omnipotent.Services.OmniTrader.Helpers;

namespace Omnipotent.Services.OmniTrader.Strategies
{
    /// <summary>
    /// Combined strategy that merges:
    ///   1. Meta-Labeling – IBS mean-reversion generates candidate signals, an XGBoost
    ///      classifier ("gate") decides whether the signal is worth taking based on
    ///      market context features (RSI, SMA distance, ATR%, volume ratio, IBS).
    ///   2. RL Position Management – a Q-learning agent chooses position size,
    ///      trailing-stop distance, and take-profit distance. It is rewarded with a
    ///      Sharpe-contribution metric that penalises drawdowns.
    /// </summary>
    public class MetaLabeledRLStrategy : OmniTraderStrategy
    {
        // ── IBS entry parameters (mirrors IBSMeanReversionStrategy) ───────
        private const int HighLookback = 10;
        private const int AvgRangeLookback = 25;
        private const decimal RangeMultiplier = 2.5m;
        private const decimal IBSThreshold = 0.3m;

        // ── Components ────────────────────────────────────────────────────
        private readonly MetaLabelGate _gate = new();
        private readonly RLPositionManager _rl;

        // ── Internal state ────────────────────────────────────────────────
        private readonly List<RequestKlineData.OHLCCandle> _history = [];
        private bool _inPosition;
        private decimal _entryPrice;
        private decimal _stopDistance;      // ATR × multiplier, fixed at entry
        private decimal _trailingStopPrice;
        private decimal _takeProfitPrice;
        private decimal _highestSinceEntry;

        // Minimum bars needed before the strategy can evaluate signals
        private int MinBars => Math.Max(MetaLabelGate.MinHistoryRequired, AvgRangeLookback);

        public MetaLabeledRLStrategy(int? rlSeed = null)
        {
            Name = "Meta-Labeled RL Strategy";
            Description =
                "IBS mean-reversion signals filtered by an XGBoost meta-label classifier, " +
                "with position sizing and trailing stop/take-profit managed by a Q-learning RL agent.";

            _rl = new RLPositionManager(seed: rlSeed);
        }

        // ── Lifecycle ─────────────────────────────────────────────────────
        protected override async Task OnLoad()
        {
            _history.Clear();
            _inPosition = false;

            // Fetch training data (separate pair/interval to avoid look-ahead bias)
            var trainingData = await parent.requestKlineData.GetCryptoCandlesDataAsync(
                "BTC", "USD",
                RequestKlineData.TimeInterval.FifteenMinute,
                2000);

            var candles = trainingData.candles;

            if (candles.Count < MetaLabelGate.MinHistoryRequired + 30)
            {
                StrategyLog("WARNING: Not enough training candles for meta-label gate. Gate will be untrained.");
                return;
            }

            // Generate labelled signal data and train the gate
            var (features, labels) = MetaLabelGate.GenerateTrainingData(candles, maxHoldBars: 20);

            if (features.Length < 10)
            {
                StrategyLog($"WARNING: Only {features.Length} training signals found. Gate may underperform.");
                if (features.Length == 0) return;
            }

            _gate.Train(features, labels);

            int wins = labels.Count(l => l > 0.5f);
            StrategyLog($"Meta-label gate trained on {features.Length} signals ({wins} wins, {features.Length - wins} losses).");

            // Pre-warm the RL agent by replaying training signals so Q-table isn't blank
            PreWarmRL(candles);
        }

        /// <summary>
        /// Replays historical signals through the RL agent to bootstrap Q-values.
        /// </summary>
        private void PreWarmRL(IList<RequestKlineData.OHLCCandle> candles)
        {
            int episodes = 0;
            int startIndex = Math.Max(MetaLabelGate.MinHistoryRequired, AvgRangeLookback);

            for (int i = startIndex; i < candles.Count - 25; i++)
            {
                if (!IsIBSEntrySignal(candles, i))
                    continue;

                int state = ComputeRLState(candles, i);
                var action = _rl.SelectAction(state);

                decimal entryPrice = candles[i].Close;
                decimal atr = TechnicalIndicators.ATR(candles, 14, i);
                decimal stopDist = atr * action.StopAtrMultiplier;
                decimal tpDist = atr * action.TakeProfitAtrMultiplier;
                decimal stopPrice = entryPrice - stopDist;
                decimal tpPrice = entryPrice + tpDist;
                decimal highest = entryPrice;

                // Simulate forward
                decimal exitPrice = entryPrice;
                for (int j = i + 1; j < candles.Count && j <= i + 25; j++)
                {
                    if (candles[j].High > highest)
                    {
                        highest = candles[j].High;
                        // Update trailing stop
                        decimal newStop = highest - stopDist;
                        if (newStop > stopPrice) stopPrice = newStop;
                    }

                    if (candles[j].Low <= stopPrice)
                    {
                        exitPrice = stopPrice;
                        break;
                    }
                    if (candles[j].High >= tpPrice)
                    {
                        exitPrice = tpPrice;
                        break;
                    }
                    // IBS exit: close > previous bar's high
                    if (candles[j].Close > candles[j - 1].High)
                    {
                        exitPrice = candles[j].Close;
                        break;
                    }
                    exitPrice = candles[j].Close; // still holding at end
                }

                decimal returnPct = entryPrice == 0 ? 0 : (exitPrice - entryPrice) / entryPrice * 100;
                int nextState = (i + 1 < candles.Count - 1)
                    ? ComputeRLState(candles, Math.Min(i + 25, candles.Count - 1))
                    : state;

                _rl.OnPositionClosed(returnPct, nextState);
                episodes++;
            }

            StrategyLog($"RL agent pre-warmed over {episodes} historical episodes.");
        }

        // ── Tick handler ──────────────────────────────────────────────────
        protected override Task OnTick(RequestKlineData.OHLCCandlesData candlesData)
        {
            var current = candlesData.candles.Last();

            // Guard against duplicate ticks (e.g. live feed reconnects)
            if (_history.Count > 0 && current.Timestamp == _history[^1].Timestamp)
                return Task.CompletedTask;

            _history.Add(current);

            if (_history.Count < MinBars)
                return Task.CompletedTask;

            int idx = _history.Count - 1;

            if (_inPosition)
            {
                HandleOpenPosition(idx);
            }
            else
            {
                HandleNoPosition(idx);
            }

            return Task.CompletedTask;
        }

        // ── Position entry logic ──────────────────────────────────────────
        private void HandleNoPosition(int idx)
        {
            // 1. Check IBS entry signal
            if (!IsIBSEntrySignal(_history, idx))
                return;

            // 2. Meta-label gate: should we take this trade?
            if (_gate.IsTrained)
            {
                var features = MetaLabelGate.ExtractFeatures(_history, idx);
                if (!_gate.ShouldTakeTrade(features))
                {
                    StrategyLog($"GATE BLOCKED signal at {_history[idx].Close:F2}");
                    return;
                }
            }

            // 3. RL agent decides sizing and stop/TP levels
            int state = ComputeRLState(_history, idx);
            var action = _rl.SelectAction(state);

            decimal atr = TechnicalIndicators.ATR(_history, 14, idx);
            decimal close = _history[idx].Close;
            _entryPrice = close;
            _highestSinceEntry = close;
            _stopDistance = atr * action.StopAtrMultiplier;
            _trailingStopPrice = close - _stopDistance;
            _takeProfitPrice = close + atr * action.TakeProfitAtrMultiplier;

            RaiseBuy(Backtesting.AmountType.Percentage, action.SizePercent);
            _inPosition = true;

            StrategyLog(
                $"ENTRY | Close {close:F2} | Size {action.SizePercent}% " +
                $"| Stop {_trailingStopPrice:F2} ({action.StopAtrMultiplier}×ATR) " +
                $"| TP {_takeProfitPrice:F2} ({action.TakeProfitAtrMultiplier}×ATR)");
        }

        // ── Position management logic ─────────────────────────────────────
        private void HandleOpenPosition(int idx)
        {
            var current = _history[idx];

            // Ratchet trailing stop up as price rises; distance stays fixed at _stopDistance
            if (current.High > _highestSinceEntry)
            {
                _highestSinceEntry = current.High;
                decimal newStop = _highestSinceEntry - _stopDistance;
                if (newStop > _trailingStopPrice)
                    _trailingStopPrice = newStop;
            }

            // Determine exit condition and the actual fill price for each case
            decimal exitPrice;
            string exitReason;

            if (current.Low <= _trailingStopPrice)
            {
                exitPrice = _trailingStopPrice;
                exitReason = $"TRAILING STOP | Low {current.Low:F2} <= Stop {_trailingStopPrice:F2}";
            }
            else if (current.High >= _takeProfitPrice)
            {
                exitPrice = _takeProfitPrice;
                exitReason = $"TAKE PROFIT | High {current.High:F2} >= TP {_takeProfitPrice:F2}";
            }
            else if (idx >= 1 && current.Close > _history[idx - 1].High)
            {
                exitPrice = current.Close;
                exitReason = $"IBS EXIT | Close {current.Close:F2} > Prev High {_history[idx - 1].High:F2}";
            }
            else
            {
                return; // Still holding
            }

            // Close position
            RaiseSell(Backtesting.AmountType.Percentage, 100);
            _inPosition = false;

            // Use the actual fill price so the RL agent receives an accurate reward signal
            decimal returnPct = _entryPrice == 0 ? 0 : (exitPrice - _entryPrice) / _entryPrice * 100;
            int nextState = ComputeRLState(_history, idx);
            _rl.OnPositionClosed(returnPct, nextState);

            StrategyLog($"{exitReason} | Return {returnPct:F2}%");
        }

        // ── IBS entry signal check ────────────────────────────────────────
        private static bool IsIBSEntrySignal(IList<RequestKlineData.OHLCCandle> candles, int idx)
        {
            if (idx < AvgRangeLookback - 1)
                return false;

            var current = candles[idx];

            decimal highestHigh = decimal.MinValue;
            for (int j = idx - HighLookback + 1; j <= idx; j++)
                if (candles[j].High > highestHigh) highestHigh = candles[j].High;

            decimal avgHigh = 0, avgLow = 0;
            for (int j = idx - AvgRangeLookback + 1; j <= idx; j++)
            {
                avgHigh += candles[j].High;
                avgLow += candles[j].Low;
            }
            avgHigh /= AvgRangeLookback;
            avgLow /= AvgRangeLookback;

            decimal entryThreshold = highestHigh - RangeMultiplier * (avgHigh - avgLow);
            decimal ibs = TechnicalIndicators.IBS(current);

            return current.Close < entryThreshold && ibs < IBSThreshold;
        }

        // ── RL state computation ──────────────────────────────────────────
        private static int ComputeRLState(IList<RequestKlineData.OHLCCandle> candles, int idx)
        {
            decimal close = candles[idx].Close;
            decimal atr = TechnicalIndicators.ATR(candles, 14, idx);
            decimal atrPct = close == 0 ? 0 : atr / close * 100;
            decimal rsi = TechnicalIndicators.RSI(candles, 14, idx);
            decimal sma = TechnicalIndicators.SMA(candles, 50, idx);
            bool aboveSma = close > sma;
            decimal volRatio = TechnicalIndicators.VolumeRatio(candles, 24, idx);

            return RLPositionManager.EncodeState(atrPct, rsi, aboveSma, volRatio);
        }
    }
}
