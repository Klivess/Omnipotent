using Omnipotent.Data_Handling;
using Omnipotent.Services.OmniTrader.Contracts;

namespace Omnipotent.Services.OmniTrader.Strategy.Strategies
{
    /// <summary>
    /// Deadbanded, volatility-scaled TCN signal strategy — fully self-contained in C#.
    ///
    /// The Temporal Convolutional Network (see <see cref="TcnNetwork"/>) is trained in-process:
    /// no Python, no ONNX, no pre-trained file required. On first run the strategy trains the
    /// model on the candle history available to it, then trades. The trained model is cached so
    /// later runs are instant.
    ///
    /// Per-bar contract (once a model is ready):
    ///   p_hat  = net.PredictUpProbability(window_64x12)   // calibrated, in (0,1)
    ///   raw    = 2 * p_hat - 1                             // in (-1, +1)
    ///   sig    = deadband(raw, tau)                        // continuous rescaled variant
    ///   w      = (sigma_star / sigma_hat) * sig
    ///   w      = clip(w, w_min, w_max)
    ///   skip rebalance if |w - w_current| < rebalance_band
    ///
    /// Mode-aware training (important for honest results):
    ///   • Backtest : never loads/saves the on-disk cache. Trains fresh on the first
    ///                <see cref="TrainMinBars"/> bars only, then trades the remainder.
    ///                This guarantees the model never sees future data (no look-ahead leak).
    ///   • Paper/Live: loads the cached model if present; otherwise trains in the background
    ///                on available history and saves it. Stays flat until the model is ready.
    /// </summary>
    [TradingStrategy(
        "TCN Volatility Signal",
        "Self-training Temporal Convolutional Network (pure C#, no external model file). " +
        "Predicts next-bar direction, then applies deadband + EWMA volatility scaling to size a " +
        "long/short target weight. Auto-trains on first run and caches the model. " +
        "For spot-only exchanges set WMin = 0.")]
    public sealed class TCNSignalStrategy : TradingStrategy
    {
        // ── Spec: fixed by design ───────────────────────────────────────────────
        private const int SequenceLen   = 64;
        private const int FeatureCount  = 12;
        private const int FeatureWarmup = 34;   // deepest feature lookback (MACD signal)

        // ── [DEFAULT] tuneable — calibrate on validation, never on test ─────────
        private const double Tau           = 0.30;   // deadband threshold
        private const double SigmaStarAnn  = 0.12;   // 12% target annualised vol
        private const double WMin          = -1.0;   // max short weight (set 0 for spot-only)
        private const double WMax          = +1.0;   // max long weight
        private const double RebalanceBand = 0.05;   // minimum |delta weight| to reorder
        private const double LambdaEwma    = 0.94;   // RiskMetrics EWMA decay
        private const double VolFloor      = 0.02;   // 2% ann. vol floor (avoids ÷0 blow-up)

        // ── Training ────────────────────────────────────────────────────────────
        private const int TrainMinBars    = 300;   // need at least this many bars to train
        private const int MaxBuildWindows = 6000;  // cap training windows (memory/time bound)

        // ── Model state machine ─────────────────────────────────────────────────
        private const int Cold = 0, Training = 1, Ready = 2, Failed = 3;
        private int _state = Cold;
        private volatile TcnNetwork? _net;

        private SessionMode _mode;
        private string _cachePath = "";

        // ── Running state ───────────────────────────────────────────────────────
        private double _ewmaVar;
        private bool   _ewmaSeeded;
        private int    _periodsPerYear;

        // ════════════════════════════════════════════════════════════════════════

        public override Task OnStart(CancellationToken ct)
        {
            _mode = Ctx.Host.Mode;

            _periodsPerYear = Ctx.Host.Interval switch
            {
                TimeInterval.OneMinute     => 525_600,
                TimeInterval.FiveMinute    => 105_120,
                TimeInterval.FifteenMinute =>  35_040,
                TimeInterval.ThirtyMinute  =>  17_520,
                TimeInterval.OneHour       =>   8_760,
                TimeInterval.FourHour      =>   2_190,
                TimeInterval.OneDay        =>     365,
                TimeInterval.OneWeek       =>      52,
                _                          =>   8_760
            };

            _cachePath = BuildCachePath();

            // Live/Paper: try to resume from a cached model. Backtests always train fresh
            // (loading a cache trained on overlapping data would leak the future).
            if (_mode != SessionMode.Backtest && File.Exists(_cachePath))
            {
                var loaded = TcnNetwork.Load(_cachePath);
                if (loaded != null && loaded.SeqLen == SequenceLen && loaded.FeatureCount == FeatureCount)
                {
                    _net = loaded;
                    _state = Ready;
                    Log($"TCN: loaded cached model from {_cachePath} (T={loaded.Temperature:F3})");
                }
            }

            if (_state != Ready)
                Log($"TCN: no model yet (mode={_mode}). Will train after {TrainMinBars} bars; flat until ready.");
            return Task.CompletedTask;
        }

        public override Task OnCandleClose(OHLCCandle candle, CancellationToken ct)
        {
            var history = History;

            // Keep the EWMA volatility estimate warm at every bar, regardless of model state.
            UpdateEwma(history, candle);

            int state = Volatile.Read(ref _state);

            if (state == Ready && _net != null)
                return RunSignal(candle, history, ct);

            if (state == Cold && history.Count >= TrainMinBars)
                TriggerTraining(history);

            return Task.CompletedTask; // dormant while training / cold / failed
        }

        // ── Volatility estimate ───────────────────────────────────────────────────

        private void UpdateEwma(IReadOnlyList<OHLCCandle> history, OHLCCandle candle)
        {
            if (history.Count < 2) return;
            decimal prevClose = history[^2].Close;
            if (prevClose <= 0 || candle.Close <= 0) return;

            double r = Math.Log((double)(candle.Close / prevClose));
            if (!_ewmaSeeded) { _ewmaVar = r * r; _ewmaSeeded = true; }
            else _ewmaVar = LambdaEwma * _ewmaVar + (1.0 - LambdaEwma) * r * r;
        }

        private double CurrentAnnualisedVol()
        {
            double sigma = Math.Sqrt(Math.Max(_ewmaVar, 0.0) * _periodsPerYear);
            return Math.Max(sigma, VolFloor);
        }

        // ── Signal pipeline ───────────────────────────────────────────────────────

        private Task RunSignal(OHLCCandle candle, IReadOnlyList<OHLCCandle> history, CancellationToken ct)
        {
            if (history.Count < SequenceLen + FeatureWarmup + 1) return Task.CompletedTask;

            var h = history as IList<OHLCCandle> ?? history.ToList();
            int last = h.Count - 1;

            // 1. Calibrated probability from the network
            float[] window = BuildRawWindow(h, last);
            double pHat = _net!.PredictUpProbability(window);

            // 2. Signal transform + deadband
            double raw = 2.0 * pHat - 1.0;
            double sig = ApplyDeadband(raw, Tau);

            // 3. Volatility scaling + clip
            double sigmaHat = CurrentAnnualisedVol();
            double wTarget = Math.Clamp((SigmaStarAnn / sigmaHat) * sig, WMin, WMax);

            // 4. Rebalance band (turnover control)
            decimal totalEquity = QuoteBalance + BaseBalance * candle.Close;
            double wCurrent = totalEquity > 0
                ? (double)((Position?.Qty ?? 0m) * candle.Close / totalEquity)
                : 0.0;

            if (Math.Abs(wTarget - wCurrent) < RebalanceBand)
                return Task.CompletedTask;

            return AdjustPosition(wTarget, wCurrent, totalEquity, sigmaHat, candle, ct);
        }

        private async Task AdjustPosition(
            double wTarget, double wCurrent, decimal totalEquity,
            double sigmaHat, OHLCCandle candle, CancellationToken ct)
        {
            if (candle.Close <= 0 || totalEquity <= 0) return;

            decimal targetQty  = (decimal)wTarget * totalEquity / candle.Close;
            decimal currentQty = Position?.Qty ?? 0m;
            decimal delta      = targetQty - currentQty;

            if (Math.Abs(delta) < 1e-10m) return;

            Log($"TCN w={wTarget:F3} (curr={wCurrent:F3}) sigHat={sigmaHat:F3} delta={delta:F6}");

            await SubmitOrder(new OrderRequest
            {
                IntentId = Guid.NewGuid().ToString("N"),
                Side     = delta > 0 ? OrderSide.Buy : OrderSide.Sell,
                Type     = OrderType.Market,
                Symbol   = Symbol,
                Qty      = Math.Abs(delta)
            }, ct);
        }

        // ── Training orchestration ─────────────────────────────────────────────────

        private void TriggerTraining(IReadOnlyList<OHLCCandle> history)
        {
            // Claim the Cold -> Training transition exactly once.
            if (Interlocked.CompareExchange(ref _state, Training, Cold) != Cold)
                return;

            // Snapshot the candles now (engine thread) so background training reads a
            // stable copy. In backtest mode we train synchronously instead.
            OHLCCandle[] snapshot = history.ToArray();

            if (_mode == SessionMode.Backtest)
            {
                TrainAndPublish(snapshot, persist: false);
            }
            else
            {
                Log($"TCN: training in background on {snapshot.Length} bars…");
                _ = Task.Run(() => TrainAndPublish(snapshot, persist: true));
            }
        }

        private void TrainAndPublish(OHLCCandle[] candles, bool persist)
        {
            try
            {
                var (windows, labels) = BuildTrainingSet(candles);
                if (windows.Count < 50)
                {
                    Log($"TCN: only {windows.Count} training windows — staying dormant.");
                    Volatile.Write(ref _state, Failed);
                    return;
                }

                var opt = new TcnNetwork.TrainOptions();
                var net = TcnNetwork.Train(windows, labels, SequenceLen, FeatureCount, opt, Log);

                _net = net;
                Volatile.Write(ref _state, Ready);

                if (persist)
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
                        net.Save(_cachePath);
                        Log($"TCN: model cached to {_cachePath}");
                    }
                    catch (Exception ex) { LogError("TCN: failed to cache model", ex); }
                }
            }
            catch (Exception ex)
            {
                LogError("TCN: training failed", ex);
                Volatile.Write(ref _state, Failed);
            }
        }

        // Build all (window, label) training pairs from a candle snapshot, strided so the
        // count stays within MaxBuildWindows. Per-bar features are computed once and reused
        // across overlapping windows.
        private (List<float[]> windows, List<float> labels) BuildTrainingSet(OHLCCandle[] candles)
        {
            int count = candles.Length;
            var feats = new float[count][];
            for (int i = 0; i < count; i++) feats[i] = ComputeFeatureVector(candles, i);

            var windows = new List<float[]>();
            var labels  = new List<float>();

            int firstEnd = SequenceLen - 1 + FeatureWarmup; // oldest window bar has mature features
            int lastEnd  = count - 2;                        // need candle i+1 for the label
            int possible = lastEnd - firstEnd + 1;
            if (possible <= 0) return (windows, labels);

            int stride = possible > MaxBuildWindows
                ? (int)Math.Ceiling((double)possible / MaxBuildWindows) : 1;

            for (int i = firstEnd; i <= lastEnd; i += stride)
            {
                var w = new float[SequenceLen * FeatureCount];
                for (int t = 0; t < SequenceLen; t++)
                    Array.Copy(feats[i - (SequenceLen - 1) + t], 0, w, t * FeatureCount, FeatureCount);
                windows.Add(w);
                labels.Add(candles[i + 1].Close > candles[i].Close ? 1f : 0f);
            }
            return (windows, labels);
        }

        private string BuildCachePath()
        {
            string dir = Path.Combine(
                OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniTraderDirectory), "Models");
            string safeSymbol = string.Concat(
                Ctx.Host.Symbol.Where(c => char.IsLetterOrDigit(c)));
            return Path.Combine(dir, $"tcn_{safeSymbol}_{Ctx.Host.Interval}.bin");
        }

        // ── Feature window (inference) ──────────────────────────────────────────────

        private static float[] BuildRawWindow(IList<OHLCCandle> c, int lastIndex)
        {
            var window = new float[SequenceLen * FeatureCount];
            for (int t = 0; t < SequenceLen; t++)
            {
                float[] f = ComputeFeatureVector(c, lastIndex - (SequenceLen - 1) + t);
                Array.Copy(f, 0, window, t * FeatureCount, FeatureCount);
            }
            return window;
        }

        // Returns the 12 raw (unnormalised) features for candle at index i.
        // Features with insufficient lookback fall back to neutral values (0, or 0.5 for %B).
        // The TcnNetwork normalises these internally using stats fit during training.
        private static float[] ComputeFeatureVector(IList<OHLCCandle> c, int i)
        {
            // F1: 1-bar log return
            float ret1 = i > 0
                ? (float)Math.Log((double)(c[i].Close / c[i - 1].Close)) : 0f;

            // F2: 5-bar cumulative log return
            float ret5 = i >= 5
                ? (float)Math.Log((double)(c[i].Close / c[i - 5].Close)) : 0f;

            // F3: 20-bar cumulative log return
            float ret20 = i >= 20
                ? (float)Math.Log((double)(c[i].Close / c[i - 20].Close)) : 0f;

            // F4: RSI(14) rescaled to [-1, +1]
            float rsi14 = i >= 14
                ? (float)(Indicators.RSI(c, 14, i) / 50.0m - 1.0m) : 0f;

            // F5: 20-bar realised volatility (std of log returns)
            float realVol = 0f;
            if (i >= 20)
            {
                double sum = 0, sumSq = 0;
                for (int k = i - 19; k <= i; k++)
                {
                    double lr = k > 0 ? Math.Log((double)(c[k].Close / c[k - 1].Close)) : 0.0;
                    sum += lr; sumSq += lr * lr;
                }
                double mean = sum / 20;
                realVol = (float)Math.Sqrt(Math.Max(sumSq / 20 - mean * mean, 0.0));
            }

            // F6: relative volume — log(vol / 20-bar mean vol)
            float relVol = 0f;
            if (i >= 20 && c[i].Volume > 0)
            {
                double avg = 0;
                for (int k = i - 19; k <= i; k++) avg += (double)c[k].Volume;
                avg /= 20;
                relVol = avg > 0 ? (float)Math.Log((double)c[i].Volume / avg) : 0f;
            }

            // F7: ATR(14) / close
            float atrNorm = i >= 14 && c[i].Close > 0
                ? (float)(Indicators.ATR(c, 14, i) / c[i].Close) : 0f;

            // F8: (close / SMA20) - 1
            float priceVsSma = i >= 19 && c[i].Close > 0
                ? (float)(c[i].Close / Indicators.SMA(c, 20, i) - 1m) : 0f;

            // F9: MACD histogram / (close / 100) — windowed EMA
            float macdNorm = 0f;
            if (i >= 34 && c[i].Close > 0)
            {
                decimal ema12    = Indicators.EMA(c, 12, i);
                decimal ema26    = Indicators.EMA(c, 26, i);
                decimal macdLine = ema12 - ema26;
                decimal macdSig  = ComputeMacdSignal(c, i, signalPeriod: 9);
                decimal hist     = macdLine - macdSig;
                decimal scale    = c[i].Close / 100m;
                macdNorm = scale != 0m ? (float)(hist / scale) : 0f;
            }

            // F10: Bollinger %B = (close - lower) / (upper - lower), bands = SMA20 ± 2σ
            float bPct = 0.5f;
            if (i >= 19)
            {
                decimal sma20 = Indicators.SMA(c, 20, i);
                double  ssq   = 0;
                for (int k = i - 19; k <= i; k++)
                    ssq += Math.Pow((double)(c[k].Close - sma20), 2);
                decimal std20 = (decimal)Math.Sqrt(ssq / 20);
                decimal band  = 4m * std20;
                bPct = band > 0m
                    ? (float)((c[i].Close - (sma20 - 2m * std20)) / band)
                    : 0.5f;
            }

            // F11: bar range (high - low) / close
            float barRange = c[i].Close > 0
                ? (float)((c[i].High - c[i].Low) / c[i].Close) : 0f;

            // F12: time-of-day encoding: sin(2π·hour / 24)
            float timeOfDay = (float)Math.Sin(2.0 * Math.PI * c[i].Timestamp.Hour / 24.0);

            return new[] { ret1, ret5, ret20, rsi14, realVol, relVol,
                           atrNorm, priceVsSma, macdNorm, bPct, barRange, timeOfDay };
        }

        // Windowed EMA(9) of the MACD line, seeded at the start of the 9-bar window —
        // same windowed-EMA convention as Indicators.EMA.
        private static decimal ComputeMacdSignal(IList<OHLCCandle> c, int i, int signalPeriod)
        {
            int needed = signalPeriod + 26;
            if (i < needed - 1) return 0m;

            decimal mult  = 2m / (signalPeriod + 1);
            int     start = i - signalPeriod + 1;

            decimal signal = Indicators.EMA(c, 12, start) - Indicators.EMA(c, 26, start);
            for (int k = start + 1; k <= i; k++)
            {
                decimal macdK = Indicators.EMA(c, 12, k) - Indicators.EMA(c, 26, k);
                signal = (macdK - signal) * mult + signal;
            }
            return signal;
        }

        // ── Signal transform helpers ────────────────────────────────────────────────

        // Continuous rescaled deadband: zeroes |raw| < tau, rescales survivors back toward ±1.
        private static double ApplyDeadband(double raw, double tau)
        {
            if (Math.Abs(raw) <= tau) return 0.0;
            return Math.Sign(raw) * (Math.Abs(raw) - tau) / (1.0 - tau);
        }
    }
}
