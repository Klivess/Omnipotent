using Omnipotent.Services.OmniTrader.Data;
using XGBoostSharp;

namespace Omnipotent.Services.OmniTrader.Helpers
{
    /// <summary>
    /// Meta-labeling gate that uses an XGBoost classifier to filter rule-based trade signals.
    /// For each candidate signal the gate receives a feature vector describing market context
    /// and predicts whether the trade is likely to be a Win (1) or Loss (0).
    /// </summary>
    public class MetaLabelGate
    {
        // Minimum candle history required to compute all features
        public const int MinHistoryRequired = 52; // 50-SMA + 2 buffer

        private XGBClassifier? _classifier;
        private bool _isTrained;

        public bool IsTrained => _isTrained;

        /// <summary>
        /// Builds a feature vector describing market context at the given candle index.
        /// Features: RSI(14), SMA(50) distance %, ATR(14) / price %, volume ratio (24-bar), IBS.
        /// </summary>
        public static float[] ExtractFeatures(IList<RequestKlineData.OHLCCandle> candles, int index)
        {
            decimal close = candles[index].Close;

            decimal rsi = TechnicalIndicators.RSI(candles, 14, index);
            decimal sma50 = TechnicalIndicators.SMA(candles, 50, index);
            decimal smaDistPct = close == 0 ? 0 : (close - sma50) / close * 100m;
            decimal atr14 = TechnicalIndicators.ATR(candles, 14, index);
            decimal atrPct = close == 0 ? 0 : atr14 / close * 100m;
            decimal volRatio = TechnicalIndicators.VolumeRatio(candles, 24, index);
            decimal ibs = TechnicalIndicators.IBS(candles[index]);

            return
            [
                (float)rsi,
                (float)smaDistPct,
                (float)atrPct,
                (float)volRatio,
                (float)ibs
            ];
        }

        /// <summary>
        /// Trains the classifier on labelled signal data.
        /// <paramref name="features"/> – one row per signal, columns from <see cref="ExtractFeatures"/>.
        /// <paramref name="labels"/> – 1 = Win, 0 = Loss.
        /// </summary>
        public void Train(float[][] features, float[] labels)
        {
            _classifier = new XGBClassifier(
                maxDepth: 4,
                learningRate: 0.05f,
                nEstimators: 200,
                objective: "binary:logistic"
            );
            _classifier.Fit(features, labels);
            _isTrained = true;
        }

        /// <summary>
        /// Returns the predicted probability that the signal is a Win (0‑1).
        /// </summary>
        public float PredictWinProbability(float[] features)
        {
            if (!_isTrained || _classifier is null)
                throw new InvalidOperationException("MetaLabelGate has not been trained.");

            var probs = _classifier.PredictProbability([features]);
            // PredictProbability returns shape [nSamples][nClasses]; class 1 = Win
            return probs[0].Length > 1 ? probs[0][1] : probs[0][0];
        }

        /// <summary>
        /// Returns true when the model is confident enough that the trade is a Win.
        /// </summary>
        public bool ShouldTakeTrade(float[] features, float threshold = 0.55f)
        {
            return PredictWinProbability(features) >= threshold;
        }

        /// <summary>
        /// Generates training data by replaying IBS mean-reversion signals over historical candles.
        /// A signal is labelled Win if the close rises above the entry bar's high within
        /// <paramref name="maxHoldBars"/> bars; otherwise Loss.
        /// </summary>
        public static (float[][] features, float[] labels) GenerateTrainingData(
            IList<RequestKlineData.OHLCCandle> candles,
            int maxHoldBars = 20)
        {
            const int highLookback = 10;
            const int avgRangeLookback = 25;
            const decimal rangeMultiplier = 2.5m;
            const decimal ibsThreshold = 0.3m;

            var featureList = new List<float[]>();
            var labelList = new List<float>();

            // We need at least MinHistoryRequired bars before we can compute features,
            // and avgRangeLookback bars for the IBS entry condition.
            int startIndex = Math.Max(MinHistoryRequired, avgRangeLookback);

            for (int i = startIndex; i < candles.Count - maxHoldBars; i++)
            {
                var current = candles[i];

                // --- IBS entry condition (same logic as IBSMeanReversionStrategy) ---
                decimal highestHigh = decimal.MinValue;
                for (int j = i - highLookback + 1; j <= i; j++)
                    if (candles[j].High > highestHigh) highestHigh = candles[j].High;

                decimal avgHigh = 0, avgLow = 0;
                for (int j = i - avgRangeLookback + 1; j <= i; j++)
                {
                    avgHigh += candles[j].High;
                    avgLow += candles[j].Low;
                }
                avgHigh /= avgRangeLookback;
                avgLow /= avgRangeLookback;

                decimal entryThreshold = highestHigh - rangeMultiplier * (avgHigh - avgLow);
                decimal ibs = TechnicalIndicators.IBS(current);

                if (current.Close >= entryThreshold || ibs >= ibsThreshold)
                    continue; // No signal at this bar

                // Signal triggered – determine outcome
                bool win = false;
                for (int k = 1; k <= maxHoldBars && i + k < candles.Count; k++)
                {
                    if (candles[i + k].Close > current.High)
                    {
                        win = true;
                        break;
                    }
                }

                featureList.Add(ExtractFeatures(candles, i));
                labelList.Add(win ? 1f : 0f);
            }

            return (featureList.ToArray(), labelList.ToArray());
        }
    }
}
