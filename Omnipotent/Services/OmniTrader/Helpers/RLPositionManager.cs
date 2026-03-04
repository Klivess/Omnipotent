namespace Omnipotent.Services.OmniTrader.Helpers
{
    /// <summary>
    /// Tabular Q-learning agent that controls position sizing, trailing-stop distance,
    /// and take-profit distance. The agent's reward is a Sharpe-contribution metric
    /// that encourages cutting losers fast and letting winners run.
    /// </summary>
    public class RLPositionManager
    {
        // ── Action constants ──────────────────────────────────────────────
        /// <summary>Available position-size percentages.</summary>
        public static readonly decimal[] SizeOptions = [25m, 50m, 75m, 100m];

        /// <summary>Trailing-stop multipliers of ATR.</summary>
        public static readonly decimal[] StopMultipliers = [1.0m, 1.5m, 2.0m, 2.5m];

        /// <summary>Take-profit multipliers of ATR.</summary>
        public static readonly decimal[] TakeProfitMultipliers = [2.0m, 3.0m, 4.0m, 5.0m];

        public static int ActionCount => SizeOptions.Length * StopMultipliers.Length * TakeProfitMultipliers.Length;

        // ── State discretisation ──────────────────────────────────────────
        // ATR% bins:   Low (<1%), Medium (1-3%), High (>3%)  → 3
        // RSI zone:    Oversold (<30), Neutral (30-70), Overbought (>70) → 3
        // Trend:       Below SMA (0), Above SMA (1) → 2
        // Volume:      Below avg (0), Above avg (1) → 2
        // Total states: 3 × 3 × 2 × 2 = 36
        private const int AtrBins = 3;
        private const int RsiBins = 3;
        private const int TrendBins = 2;
        private const int VolBins = 2;
        public static int StateCount => AtrBins * RsiBins * TrendBins * VolBins;

        // ── Q-table & hyperparams ─────────────────────────────────────────
        private readonly float[,] _qTable;
        private readonly float _alpha;       // learning rate
        private readonly float _gamma;       // discount factor
        private float _epsilon;              // exploration rate
        private readonly float _epsilonDecay;
        private readonly float _epsilonMin;
        private readonly Random _rng;

        // ── Running Sharpe components for reward ──────────────────────────
        private readonly List<float> _recentReturns = [];
        private const int ReturnsWindow = 30;

        /// <summary>The action chosen for the current open position (or -1).</summary>
        public int CurrentAction { get; private set; } = -1;

        /// <summary>The state observed when the current position was opened.</summary>
        public int EntryState { get; private set; } = -1;

        public RLPositionManager(
            float alpha = 0.1f,
            float gamma = 0.95f,
            float epsilon = 1.0f,
            float epsilonDecay = 0.995f,
            float epsilonMin = 0.05f,
            int? seed = null)
        {
            _alpha = alpha;
            _gamma = gamma;
            _epsilon = epsilon;
            _epsilonDecay = epsilonDecay;
            _epsilonMin = epsilonMin;
            _rng = seed.HasValue ? new Random(seed.Value) : new Random();

            _qTable = new float[StateCount, ActionCount];
        }

        // ── State encoding ────────────────────────────────────────────────
        /// <summary>
        /// Encodes continuous market features into a discrete state index.
        /// </summary>
        public static int EncodeState(decimal atrPct, decimal rsi, bool aboveSma, decimal volumeRatio)
        {
            int atrBin = atrPct < 1m ? 0 : atrPct < 3m ? 1 : 2;
            int rsiBin = rsi < 30m ? 0 : rsi < 70m ? 1 : 2;
            int trendBin = aboveSma ? 1 : 0;
            int volBin = volumeRatio >= 1m ? 1 : 0;

            return atrBin * (RsiBins * TrendBins * VolBins)
                 + rsiBin * (TrendBins * VolBins)
                 + trendBin * VolBins
                 + volBin;
        }

        // ── Action decoding ───────────────────────────────────────────────
        public readonly struct DecodedAction
        {
            public decimal SizePercent { get; init; }
            public decimal StopAtrMultiplier { get; init; }
            public decimal TakeProfitAtrMultiplier { get; init; }
        }

        public static DecodedAction DecodeAction(int actionIndex)
        {
            int tpCount = TakeProfitMultipliers.Length;
            int stopCount = StopMultipliers.Length;

            int sizeIdx = actionIndex / (stopCount * tpCount);
            int remainder = actionIndex % (stopCount * tpCount);
            int stopIdx = remainder / tpCount;
            int tpIdx = remainder % tpCount;

            return new DecodedAction
            {
                SizePercent = SizeOptions[sizeIdx],
                StopAtrMultiplier = StopMultipliers[stopIdx],
                TakeProfitAtrMultiplier = TakeProfitMultipliers[tpIdx]
            };
        }

        // ── Policy ────────────────────────────────────────────────────────
        /// <summary>
        /// Selects an action via ε-greedy policy for the given state.
        /// Stores the state/action pair so it can be updated on position close.
        /// </summary>
        public DecodedAction SelectAction(int state)
        {
            int action;
            if (_rng.NextDouble() < _epsilon)
            {
                action = _rng.Next(ActionCount);
            }
            else
            {
                action = ArgMaxAction(state);
            }

            EntryState = state;
            CurrentAction = action;
            return DecodeAction(action);
        }

        /// <summary>
        /// Called when the position is closed. Computes a Sharpe-contribution reward
        /// and performs a Q-learning update.
        /// </summary>
        public void OnPositionClosed(decimal returnPct, int nextState)
        {
            if (CurrentAction < 0) return;

            float reward = ComputeReward((float)returnPct);

            // Q-learning: Q(s,a) ← Q(s,a) + α [r + γ max_a' Q(s',a') - Q(s,a)]
            float bestNextQ = MaxQ(nextState);
            float oldQ = _qTable[EntryState, CurrentAction];
            _qTable[EntryState, CurrentAction] = oldQ + _alpha * (reward + _gamma * bestNextQ - oldQ);

            // Decay exploration
            _epsilon = Math.Max(_epsilonMin, _epsilon * _epsilonDecay);

            CurrentAction = -1;
            EntryState = -1;
        }

        // ── Reward shaping ────────────────────────────────────────────────
        private float ComputeReward(float returnPct)
        {
            _recentReturns.Add(returnPct);
            if (_recentReturns.Count > ReturnsWindow)
                _recentReturns.RemoveAt(0);

            if (_recentReturns.Count < 2)
                return returnPct; // Not enough history for Sharpe

            float mean = _recentReturns.Average();
            float variance = _recentReturns.Sum(r => (r - mean) * (r - mean)) / _recentReturns.Count;
            float std = MathF.Sqrt(variance);

            if (std == 0) return returnPct;

            // Sharpe-contribution: mean / std, scaled so magnitude is comparable to raw return
            float sharpeContrib = mean / std;

            // Penalise large drawdowns: if return is very negative, add extra penalty
            float drawdownPenalty = returnPct < -2f ? returnPct * 0.5f : 0f;

            return sharpeContrib + drawdownPenalty;
        }

        // ── Helpers ───────────────────────────────────────────────────────
        private int ArgMaxAction(int state)
        {
            int best = 0;
            float bestVal = _qTable[state, 0];
            for (int a = 1; a < ActionCount; a++)
            {
                if (_qTable[state, a] > bestVal)
                {
                    bestVal = _qTable[state, a];
                    best = a;
                }
            }
            return best;
        }

        private float MaxQ(int state)
        {
            float max = _qTable[state, 0];
            for (int a = 1; a < ActionCount; a++)
                if (_qTable[state, a] > max) max = _qTable[state, a];
            return max;
        }
    }
}
