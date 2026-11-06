using System.Globalization;

namespace Omnipotent.Services.OmniscienceV3.Llm
{
    /// <summary>
    /// Token estimate for packing prompts to a model's real window (plan "TokenEstimator"). Base ratios per
    /// script class (Latin text ~3.6 chars/token, CJK ~1.1, other scripts/emoji ~2), scaled by a factor that
    /// is calibrated continuously from the provider's own <c>usage.prompt_tokens</c> (EWMA, clamped). Starts
    /// conservative (overestimates) so the first calls never overflow.
    /// </summary>
    public sealed class TokenEstimator
    {
        private const double LatinCharsPerToken = 3.6;
        private const double CjkCharsPerToken = 1.1;
        private const double OtherCharsPerToken = 2.0;
        private double factor = 1.15;
        private readonly object sync = new();

        public double Factor { get { lock (sync) return factor; } }

        public int Estimate(string? text) => (int)Math.Ceiling(Raw(text) * Factor) + 4;

        /// <summary>Feeds back what the provider actually counted for a prompt we estimated.</summary>
        public void Calibrate(string promptText, long actualPromptTokens)
        {
            if (actualPromptTokens <= 0) return;
            double raw = Raw(promptText);
            if (raw < 200) return; // tiny prompts are dominated by template overhead
            double observed = Math.Clamp(actualPromptTokens / raw, 0.5, 2.5);
            lock (sync)
            {
                // Lean conservative: rise quickly, fall slowly.
                double alpha = observed > factor ? 0.5 : 0.1;
                factor = Math.Clamp(factor + alpha * (observed - factor), 0.6, 2.5);
            }
        }

        internal static double Raw(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            double latin = 0, cjk = 0, other = 0;
            var e = StringInfo.GetTextElementEnumerator(text);
            while (e.MoveNext())
            {
                string el = e.GetTextElement();
                int cp = char.ConvertToUtf32(el, 0);
                if (cp < 0x0250 || (cp >= 0x1E00 && cp < 0x2000)) latin++;
                else if ((cp >= 0x3040 && cp < 0x30FF) || (cp >= 0x3400 && cp < 0x9FFF) || (cp >= 0xAC00 && cp < 0xD7AF)) cjk++;
                else other += el.Length > 2 ? 2 : 1; // emoji sequences cost more than one token
            }
            return latin / LatinCharsPerToken + cjk / CjkCharsPerToken + other / OtherCharsPerToken;
        }
    }
}
