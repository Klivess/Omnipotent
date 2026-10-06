using Omnipotent.Services.KliveAgent;
using Omnipotent.Services.KliveMail.Models;

namespace Omnipotent.Tests.KliveAgent;

/// <summary>
/// Signup verification usually arrives as a LINK, not a code (Tumblr's does). The wait tool has to
/// put the verification link in front of the agent — ahead of footer noise like unsubscribe and app
/// store badges — or the agent clicks the wrong thing and the signup never completes.
/// </summary>
public sealed class KliveAgentMailToolsTests
{
    private const string VerificationHtml = """
        <html><body>
          <a href="https://www.tumblr.com/privacy">Privacy</a>
          <p>Hi there! Confirm your email to finish creating your account.</p>
          <a href="https://www.tumblr.com/verify_email/abc123?token=xyz&amp;source=email">Verify your email</a>
          <a href="https://www.tumblr.com/settings/unsubscribe?u=1">Unsubscribe</a>
          <a href="https://apps.apple.com/app/tumblr">Get the app</a>
        </body></html>
        """;

    [Fact]
    public void ExtractLinks_PutsTheVerificationLinkFirst_AndDecodesEntities()
    {
        var links = KliveAgentMailTools.ExtractLinks(VerificationHtml, null);

        Assert.Equal("https://www.tumblr.com/verify_email/abc123?token=xyz&source=email", links[0]);
        Assert.Contains("https://www.tumblr.com/settings/unsubscribe?u=1", links);
        Assert.True(links.IndexOf("https://www.tumblr.com/settings/unsubscribe?u=1") > 0);
        Assert.Equal(links.Count, links.Distinct().Count());
    }

    [Fact]
    public void ExtractLinks_FindsBareUrlsInPlainText_AndIgnoresNonHttp()
    {
        var links = KliveAgentMailTools.ExtractLinks(null,
            "Activate here: https://example.com/activate?code=99. Or write to mailto:help@example.com");
        Assert.Equal(new[] { "https://example.com/activate?code=99" }, links);
    }

    [Fact]
    public void DescribeMessage_ShowsSenderCodeAndRankedLinks()
    {
        var message = new StoredMessage
        {
            Id = "m1",
            FromAddress = "no-reply@tumblr.com",
            FromName = "Tumblr",
            ToAddress = "tumblr.klives@klive.dev",
            Subject = "Your Tumblr verification code is 482913",
            BodyHtml = VerificationHtml,
            ReceivedUtc = DateTime.UtcNow,
        };

        string text = KliveAgentMailTools.DescribeMessage(message, maxBodyChars: 2000);

        Assert.Contains("From: Tumblr <no-reply@tumblr.com>", text);
        Assert.Contains("Verification code (best guess): 482913", text);
        Assert.Contains("verify_email/abc123", text);
        Assert.Contains("Confirm your email", text);
        Assert.True(text.IndexOf("verify_email", StringComparison.Ordinal) < text.IndexOf("unsubscribe", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("tumblr.klives", "tumblr.klives@klive.dev", true)]
    [InlineData("Tumblr.Klives@KLIVE.dev", "tumblr.klives@klive.dev", true)]
    [InlineData("someone@gmail.com", "someone@gmail.com", false)]
    public void Addresses_AreNormalisedToKliveMail(string input, string expected, bool valid)
    {
        string normalized = KliveAgentMailTools.NormalizeKliveMailAddress(input, out var error);
        Assert.Equal(expected, normalized);
        Assert.Equal(valid, error == null);
    }
}
