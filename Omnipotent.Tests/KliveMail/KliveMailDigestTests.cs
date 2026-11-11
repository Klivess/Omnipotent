using Omnipotent.Services.KliveMail;

namespace Omnipotent.Tests.KliveMail
{
    /// <summary>
    /// The preview and sign-in code a list row shows. The shapes below are the live inbox's, with every
    /// code, name and address replaced: about a quarter of the mail is a code from TikTok, Instagram,
    /// Reddit, Facebook, GitHub or Hubstaff, and a third of all mail has no text part at all.
    /// </summary>
    public class KliveMailDigestTests
    {
        [Theory]
        [InlineData("483920 is your verification code", "483920")]
        [InlineData("120456 is your Instagram code", "120456")]
        [InlineData("775310 is your Reddit verification code", "775310")]
        [InlineData("50231 is your confirmation code", "50231")]
        [InlineData("Your one-time passcode: 902 113", "902 113")]
        [InlineData("Your 2FA code is 4471", "4471")]
        public void FindsTheCodeInTheSubject(string subject, string expected)
            => Assert.Equal(expected, KliveMailDigest.FindCode(subject, null, null));

        [Fact]
        public void FindsTheCodeOnItsOwnLineUnderTheSentenceThatNamesIt()
        {
            // Hubstaff and GitHub put the code alone on a line below its cue.
            string hubstaff = "Hi there,\n\nEnter the following code to continue signing in:\n\n    654321\n\nThis code expires in 10 minutes.";
            string github = "You can confirm your device for GitHub by entering the code below:\n\n    83920145\n\nYou can enter it on the page you were on.";
            Assert.Equal("654321", KliveMailDigest.FindCode("Sign in to Hubstaff", hubstaff, null));
            Assert.Equal("83920145", KliveMailDigest.FindCode("[GitHub] Please verify your device", github, null));
        }

        [Fact]
        public void FindsTheCodeInAnHtmlOnlyMail()
        {
            string html = "<html><head><style>p{color:#333}</style><title>Code 0000</title></head><body>"
                + "<p>Hi,</p><p>Someone tried to sign up. If this was you, enter this confirmation code in the app:</p>"
                + "<p>&nbsp;</p><table><tr><td style=\"font-size:32px\">246810</td></tr></table></body></html>";
            Assert.Equal("246810", KliveMailDigest.FindCode("Confirm your account", null, html));
        }

        [Theory]
        // A street number in a footer, beside "security" (the live false positive that dropped that cue).
        [InlineData("Learn more about security at ngrok. ngrok Inc. 1234 Market St, San Francisco")]
        // A year beside "confirm".
        [InlineData("Confirm your 2026 subscription preferences")]
        // An order number and a price.
        [InlineData("Order #48213 confirmed: £1299 charged to your card")]
        // A phone number after a cue.
        [InlineData("Your verification is complete. Questions? Call 0800 1234 567")]
        // Numbers with no cue at all (the agents' loose fallback would take the first).
        [InlineData("Top 10 job matches for you: 52314 new roles posted this week")]
        [InlineData("You've made 25 posts! Keep going.")]
        public void IgnoresNumbersThatAreNotCodes(string text)
        {
            Assert.Null(KliveMailDigest.FindCode(text, null, null));
            Assert.Null(KliveMailDigest.FindCode("Hello", text, null));
            Assert.Null(KliveMailDigest.FindCode("Hello", null, "<p>" + text + "</p>"));
        }

        [Fact]
        public void PrefersTheSubjectOverTheBody()
        {
            // Facebook's body has the code again, then a footer number; the subject is unambiguous.
            Assert.Equal("50231", KliveMailDigest.FindCode("50231 is your confirmation code", "Thanks,\nThe team\n\n99999 Main Road", null));
        }

        [Fact]
        public void PreviewComesFromTheHtmlWhenThereIsNoTextPart()
        {
            string html = "<html><head><title>Ignore me</title><style>.x{a:b}</style></head><body>"
                + "<div style=\"display:none\">Your weekly recap is here ‌͏ ‌͏ ‌͏</div>"
                + "<h1>You've made 25 posts</h1><p>Congratulations &amp; keep going.</p></body></html>";
            var digest = KliveMailDigest.Build("Hooray!", null, html);
            Assert.Equal("Your weekly recap is here You've made 25 posts Congratulations & keep going.", digest.Snippet);
            Assert.Null(digest.Code);
        }

        [Fact]
        public void PreviewPrefersTheTextPart_AndIsCutAtAWord()
        {
            string text = string.Join(" ", Enumerable.Repeat("alpha beta gamma", 40));
            var digest = KliveMailDigest.Build("s", text, "<p>html version</p>");
            Assert.StartsWith("alpha beta gamma", digest.Snippet);
            Assert.EndsWith("…", digest.Snippet);
            Assert.True(digest.Snippet.Length <= KliveMailDigest.SnippetLength + 1, digest.Snippet.Length.ToString());
            Assert.False(digest.Snippet.TrimEnd('…').EndsWith(' '));
        }

        [Fact]
        public void APunctuationOnlyTextPartFallsBackToTheHtml()
            => Assert.Equal("Real words", KliveMailDigest.Build("s", " \r\n -- \r\n", "<p>Real words</p>").Snippet);

        [Fact]
        public void LinksRankVerificationFirst_AndFooterNoiseLast()
        {
            string html = "<a href=\"https://example.com/unsubscribe?u=1\">Unsubscribe</a>"
                + "<a href=\"https://example.com/blog\">Blog</a>"
                + "<a href=\"https://example.com/verify-email?token=abc&amp;x=1\">Verify</a>";
            var links = KliveMailDigest.RankLinks(html, null);
            Assert.Equal("https://example.com/verify-email?token=abc&x=1", links[0].Url);
            Assert.True(links[0].Score > 0);
            Assert.Equal("https://example.com/unsubscribe?u=1", links[^1].Url);
            Assert.True(links[^1].Score < 0);
        }
    }
}
