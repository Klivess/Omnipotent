namespace Omnipotent.Services.CS2ArbitrageBot
{
    public class GraphMath
    {
        public static float CalculateLatestEMA(List<float> values, int period)
        {
            if (values == null || values.Count == 0 || period <= 0)
                throw new ArgumentException("Invalid input");

            float multiplier = 2f / (period + 1);
            float ema = values[0]; // start with the first value

            for (int i = 1; i < values.Count; i++)
            {
                ema = (values[i] - ema) * multiplier + ema;
            }

            return ema;
        }
    }
}
