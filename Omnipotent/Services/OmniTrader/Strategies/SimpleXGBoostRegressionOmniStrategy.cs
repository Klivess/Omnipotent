using Omnipotent.Services.OmniTrader.Data;
using XGBoostSharp;

namespace Omnipotent.Services.OmniTrader.Strategies
{
    public class SimpleXGBoostRegressionOmniStrategy : OmniTraderStrategy
    {
        private XGBRegressor regressor;

        public SimpleXGBoostRegressionOmniStrategy()
        {
            Name = "Simple XGBoost Regression Strategy";
            Description = "A simple regression strategy using XGBoost to predict the close price of the very next candle.";
        }
        private static (float[][] features, float[] labels) BuildFeatureMatrix(IList<RequestKlineData.OHLCCandle> candles)
        {
            int count = candles.Count - 1;
            float[][] features = new float[count][];
            float[] labels = new float[count];

            for (int i = 0; i < count; i++)
            {
                var c = candles[i];
                features[i] =
                [
                    (float)c.Open,
                    (float)c.High,
                    (float)c.Low,
                    (float)c.Close,
                    (float)c.VWAP,
                    (float)c.Volume,
                    (float)c.TradeCount
                ];
                labels[i] = (float)candles[i + 1].Close;
            }

            return (features, labels);
        }

        protected override async void OnLoad()
        {
            //Train model (ideally we'd have the model already loaded but this is lowkey just a test strategy lol)

            //Prepare data
            var data = await parent.requestKlineData.GetCryptoCandlesDataAsync("ETH", "USD", RequestKlineData.TimeInterval.OneHour, 700);

            var allCandles = data.candles;
            var trainCandles = allCandles.Take(500).ToList();
            var testCandles = allCandles.Skip(500).ToList();

            var (dataTrain, labelsTrain) = BuildFeatureMatrix(trainCandles);
            var (dataTest, labelsTest) = BuildFeatureMatrix(testCandles);

            //Prepare Regressor model
            regressor = new XGBRegressor(maxDepth: 3, learningRate: 0.1f, nEstimators: 100);
            regressor.Fit(dataTrain, labelsTrain);

            // Evaluate on test set
            var predictions = regressor.Predict(dataTest);
            float mae = 0f;
            for (int i = 0; i < predictions.Length; i++)
                mae += MathF.Abs(predictions[i] - labelsTest[i]);
            mae /= predictions.Length;

            StrategyLog("Model trained. Test MAE: {mae:F2} USD over {predictions.Length} samples.");
        }

        public override async void OnTick(RequestKlineData.OHLCCandlesData last200Candles)
        {
            if (IsLoaded == false)
            {
                throw new Exception("Strategy wasn't initialised!");
            }


            if (regressor == null)
                return;

            // Fetch the most recent candles (need at least 1 to predict the next close)
            var latest = last200Candles.candles.Last();

            float[] features =
            [
                (float)latest.Open,
                (float)latest.High,
                (float)latest.Low,
                (float)latest.Close,
                (float)latest.VWAP,
                (float)latest.Volume,
                (float)latest.TradeCount
            ];

            var prediction = regressor.Predict([features]);
            float predictedClose = prediction[0];
            float currentClose = (float)latest.Close;
            float delta = predictedClose - currentClose;

            string reason = $"Predicted: {predictedClose:F2} | Current: {currentClose:F2} | Delta: {delta:F2}";

            if (delta > 0)
                RaiseBuy(SimpleBacktestLib.AmountType.Percentage, 2);
            else
                RaiseBuy(SimpleBacktestLib.AmountType.Percentage, 2);
        }
    }
}
