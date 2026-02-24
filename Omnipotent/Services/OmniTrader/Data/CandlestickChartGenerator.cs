using ScottPlot;
using static Omnipotent.Services.OmniTrader.Data.RequestKlineData;

namespace Omnipotent.Services.OmniTrader.Data
{
    public class CandlestickChartGenerator
    {
        public int Width { get; set; } = 1200;
        public int Height { get; set; } = 600;
        public string Title { get; set; } = "";
        public bool ShowVolume { get; set; } = false;
        public ScottPlot.Color BullishColor { get; set; } = ScottPlot.Colors.Green;
        public ScottPlot.Color BearishColor { get; set; } = ScottPlot.Colors.Red;

        public CandlestickChartGenerator() { }

        public CandlestickChartGenerator(int width, int height)
        {
            Width = width;
            Height = height;
        }

        private static List<ScottPlot.OHLC> ConvertCandles(List<OHLCCandle> candles, TimeInterval interval)
        {
            TimeSpan candleWidth = TimeSpan.FromMinutes((int)interval);
            List<ScottPlot.OHLC> ohlcList = new(candles.Count);
            foreach (var c in candles)
            {
                ohlcList.Add(new ScottPlot.OHLC(
                    (double)c.Open,
                    (double)c.High,
                    (double)c.Low,
                    (double)c.Close,
                    c.Timestamp,
                    candleWidth));
            }
            return ohlcList;
        }

        public Plot BuildCandlestickPlot(OHLCCandlesData candlesData, TimeInterval interval)
        {
            return BuildCandlestickPlot(candlesData.candles, interval);
        }

        public Plot BuildCandlestickPlot(List<OHLCCandle> candles, TimeInterval interval)
        {
            var plot = new Plot();
            var ohlcData = ConvertCandles(candles, interval);

            var candlestick = plot.Add.Candlestick(ohlcData);
            candlestick.Axes.XAxis = plot.Axes.Bottom;
            candlestick.Axes.YAxis = plot.Axes.Left;

            plot.Axes.DateTimeTicksBottom();

            if (!string.IsNullOrEmpty(Title))
            {
                plot.Title(Title);
            }

            plot.Axes.Left.Label.Text = "Price";
            plot.Axes.Bottom.Label.Text = "Time";

            return plot;
        }

        public string SaveChartPng(OHLCCandlesData candlesData, TimeInterval interval, string outputPath)
        {
            return SaveChartPng(candlesData.candles, interval, outputPath);
        }

        public string SaveChartPng(List<OHLCCandle> candles, TimeInterval interval, string outputPath)
        {
            string fullPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            var plot = BuildCandlestickPlot(candles, interval);
            plot.SavePng(fullPath, Width, Height);
            return fullPath;
        }

        public byte[] GenerateChartPngBytes(OHLCCandlesData candlesData, TimeInterval interval)
        {
            return GenerateChartPngBytes(candlesData.candles, interval);
        }

        public byte[] GenerateChartPngBytes(List<OHLCCandle> candles, TimeInterval interval)
        {
            var plot = BuildCandlestickPlot(candles, interval);
            return plot.GetImageBytes(Width, Height, ImageFormat.Png);
        }

        public MemoryStream GenerateChartPngStream(OHLCCandlesData candlesData, TimeInterval interval)
        {
            return GenerateChartPngStream(candlesData.candles, interval);
        }

        public MemoryStream GenerateChartPngStream(List<OHLCCandle> candles, TimeInterval interval)
        {
            byte[] bytes = GenerateChartPngBytes(candles, interval);
            return new MemoryStream(bytes);
        }

        public static string QuickSave(OHLCCandlesData candlesData, TimeInterval interval, string outputPath, string title = "")
        {
            var generator = new CandlestickChartGenerator { Title = title };
            return generator.SaveChartPng(candlesData, interval, outputPath);
        }
    }
}
