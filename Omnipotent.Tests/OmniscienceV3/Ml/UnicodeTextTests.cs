using Omnipotent.Services.OmniscienceV3.Ml;

namespace Omnipotent.Tests.OmniscienceV3.Ml
{
    /// <summary>
    /// Text as a tokenizer can take it. On 19 Nov 2026 the first replica build failed after reading 140,000 messages:
    /// "ArgumentException: Found a low surrogate char without a preceding high surrogate at index: 0". One message in the
    /// corpus held half of an emoji, and the tokenizer throws on that.
    /// </summary>
    public class UnicodeTextTests
    {
        private const string Grin = "😀"; // one emoji, two UTF-16 units

        [Fact]
        public void WellFormedText_IsReturnedAsItIs()
        {
            foreach (string text in new[] { "plain", "café naïve", "ok " + Grin + " then", Grin + Grin, "line\nbreak\ttab" })
                Assert.Same(text, UnicodeText.WellFormed(text));
            Assert.Equal("", UnicodeText.WellFormed(null));
            Assert.Equal("", UnicodeText.WellFormed(""));
        }

        // Built at run time: half a surrogate pair cannot be written into an attribute (it is stored as UTF-8).
        private static readonly string High = ((char)0xD83D).ToString(), Low = ((char)0xDE00).ToString(), Replaced = ((char)0xFFFD).ToString();

        [Fact]
        public void ALoneSurrogate_BecomesTheReplacementCharacter_AndTheRestIsKept()
        {
            var cases = new (string Broken, string Expected)[]
            {
                (Low + " cut at the start", Replaced + " cut at the start"),                // the low half first: the failure seen live
                ("cut at the end " + High, "cut at the end " + Replaced),                    // a high half with nothing after it
                ("two " + High + High + Low + " highs", "two " + Replaced + High + Low + " highs"), // a stray high before a whole pair
                ("a" + Low + Low + "b", "a" + Replaced + Replaced + "b"),
                ("nul" + (char)0 + "inside", "nul inside"),
            };
            foreach (var (broken, expected) in cases)
            {
                string clean = UnicodeText.WellFormed(broken);
                Assert.Equal(expected, clean);
                Assert.Equal(broken.Length, clean.Length);
                // What comes out can be walked code point by code point, which is what the tokenizers do.
                for (int i = 0; i < clean.Length; i += char.IsSurrogatePair(clean, i) ? 2 : 1)
                    _ = char.ConvertToUtf32(clean, i);
            }
        }

        [Fact]
        public void EverythingThatReadsChatTextByCodePoint_TakesBrokenText()
        {
            string broken = Low + " half an emoji at each end " + High;
            Assert.Equal(0xFFFD, UnicodeText.CodePointAt(broken, 0));
            Assert.Equal(0xFFFD, UnicodeText.CodePointAt(broken, broken.Length - 1));
            Assert.Equal(0x1F600, UnicodeText.CodePointAt(Grin, 0));
            Assert.Equal('h', UnicodeText.CodePointAt(broken, 2));

            // The token estimate is where the replica build died (it sizes every prompt, extraction slices included).
            var estimator = new Omnipotent.Services.OmniscienceV3.Llm.TokenEstimator();
            Assert.InRange(estimator.Estimate(broken), 5, 40);
            Assert.Equal(estimator.Estimate(Replaced + " half an emoji at each end " + Replaced), estimator.Estimate(broken));

            // The classifier's content gate sees every message; the replica's features every message of its person.
            var shape = Omnipotent.Services.OmniscienceV3.Ml.Classification.ContentGate.Measure(broken);
            Assert.True(shape.LetterGraphemes > 0);
            Assert.False(Omnipotent.Services.OmniscienceV3.Ml.Classification.ContentGate.IsEmoji(Low));
            Assert.False(Omnipotent.Services.OmniscienceV3.Replica.TextFeatures.IsEmojiCluster(Low));
            Assert.NotEmpty(Omnipotent.Services.OmniscienceV3.Replica.TextFeatures.Tokens(broken));
            Assert.True(Omnipotent.Services.OmniscienceV3.Replica.TextFeatures.IsEmojiCluster(Grin));
        }

        [Fact]
        public void TheBrokenText_IsWhatTheTokenizersChokedOn()
        {
            // The exact exception of the failed build, from the call the tokenizers make.
            var ex = Assert.Throws<ArgumentException>(() => char.ConvertToUtf32(Low + " half an emoji", 0));
            Assert.Contains("low surrogate", ex.Message);
        }
    }
}
