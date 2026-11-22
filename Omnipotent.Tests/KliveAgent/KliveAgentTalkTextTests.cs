using Omnipotent.Services.KliveAgent.Talk;

namespace Omnipotent.Tests.KliveAgent;

/// <summary>
/// The text side of talk mode: what of KliveAgent's reply is handed to the voice (and when it is safe
/// to hand it over), and which short requests the voice session handles itself.
/// </summary>
public sealed class KliveAgentTalkTextTests
{
    // ── Segmenter: what is said, and how early ──

    [Fact]
    public void FirstSegment_IsCutAtTheFirstClauseWithEnoughWords_SoAudioStartsEarly()
    {
        var segmenter = new SpeechSegmenter();
        var said = new List<string>();
        said.AddRange(segmenter.Append("I checked the server logs, and "));
        Assert.Equal(new[] { "I checked the server logs," }, said);

        said.AddRange(segmenter.Append("everything looks healthy. Anything else"));
        said.AddRange(segmenter.Flush());
        Assert.Equal(new[] { "I checked the server logs,", "and everything looks healthy.", "Anything else" }, said);
    }

    [Fact]
    public void AShortLeadingClause_IsNotWorthItsOwnRequest()
    {
        var segmenter = new SpeechSegmenter();
        // "Sure," has one word: speaking it alone would cost a request for a fraction of a second of audio.
        Assert.Empty(segmenter.Append("Sure, the trader is running"));
        var said = segmenter.Append(" fine today. ");
        Assert.Equal(new[] { "Sure, the trader is running fine today." }, said);
    }

    [Fact]
    public void FirstSegment_WithNoBoundary_IsCutAfterADozenWords()
    {
        var segmenter = new SpeechSegmenter();
        var said = segmenter.Append("one two three four five six seven eight nine ten eleven twelve thirteen fourteen");
        Assert.Single(said);
        Assert.Equal(12, said[0].Split(' ').Length);
    }

    [Fact]
    public void LaterSegments_AreWholeSentences()
    {
        var segmenter = new SpeechSegmenter();
        segmenter.Append("Done. ");
        var said = segmenter.Append("The cache rebuilt, the index is fresh, and nothing failed. ");
        Assert.Equal(new[] { "The cache rebuilt, the index is fresh, and nothing failed." }, said);
    }

    [Fact]
    public void FencedBlocks_AreNeverSpoken_EvenWhenTheMarkersArriveInPieces()
    {
        var segmenter = new SpeechSegmenter();
        var said = new List<string>();
        foreach (string delta in new[] { "Here's the table you asked for.\n``", "`screen\n| a | b |\nsecret 42", "\n``", "`\nAll good.", " " })
            said.AddRange(segmenter.Append(delta));
        said.AddRange(segmenter.Flush());

        Assert.Equal(new[] { "Here's the table you asked for.", "All good." }, said);
        Assert.DoesNotContain(said, s => s.Contains("secret"));
    }

    [Fact]
    public void ScriptBlocks_AreNeverSpoken()
    {
        var segmenter = new SpeechSegmenter();
        var said = new List<string>(segmenter.Append("Pulling it now. {{{ var x = GetService(\"X\"); Log(x); }}} Got it. "));
        said.AddRange(segmenter.Flush());
        Assert.Equal(new[] { "Pulling it now.", "Got it." }, said);
    }

    [Fact]
    public void UnclosedFence_AtTheEndOfAReply_IsDropped()
    {
        var segmenter = new SpeechSegmenter();
        var said = new List<string>(segmenter.Append("Here: ```csharp\nvar never = \"spoken\";"));
        said.AddRange(segmenter.Flush());
        Assert.Equal(new[] { "Here:" }, said);
    }

    [Fact]
    public void NumberedListMarkers_AreNotSentences()
    {
        var segmenter = new SpeechSegmenter();
        var said = new List<string>(segmenter.Append("Two things.\n1. Restart the trader\n2. Clear the cache\n"));
        said.AddRange(segmenter.Flush());
        Assert.Equal(new[] { "Two things.", "Restart the trader", "Clear the cache" }, said);
    }

    [Fact]
    public void Abbreviations_DoNotEndASentence()
    {
        var segmenter = new SpeechSegmenter();
        var said = new List<string>(segmenter.Append("Use a short window, e.g. five minutes, for this. Then stop. "));
        Assert.Equal(new[] { "Use a short window,", "e.g. five minutes, for this.", "Then stop." }, said);
    }

    [Fact]
    public void Stalled_StreamSaysTheWordsSoFar()
    {
        var segmenter = new SpeechSegmenter();
        Assert.Empty(segmenter.Append("Give me a"));
        Assert.Empty(segmenter.TakeStalled()); // fewer than three whole words before the last space
        segmenter.Append(" moment while the slow");
        var said = segmenter.TakeStalled();
        Assert.Equal(new[] { "Give me a moment while the" }, said);
        Assert.Equal(new[] { "slow" }, segmenter.Flush());
    }

    [Theory]
    [InlineData("**Bold** and `code` with [the docs](https://x.y/z)", "Bold and code with the docs")]
    [InlineData("See https://klive.dev/kliveagent?x=1 now", "See the link now")]
    [InlineData("Saved to C:\\Projects\\Omni\\notes.txt", "Saved to notes.txt")]
    [InlineData("Edited Services/KliveAgent/Talk/TalkWire.cs", "Edited Talk Wire.cs")]
    [InlineData("Run 3f2a9c1e5b7d4e8f9a0b1c2d failed", "Run an ID failed")]
    [InlineData("Due 10/12/2026 at 5", "Due 10/12/2026 at 5")]
    [InlineData("MemeScraper and AIRouter are up 🎉", "Meme Scraper and AI Router are up")]
    [InlineData("## Status\n- trader -> fine", "Status trader to fine")]
    [InlineData("snake_case_name & more", "snake case name and more")]
    public void ToSpeech_TurnsMarkdownIntoSomethingSayable(string written, string spoken)
    {
        Assert.Equal(spoken, SpeechSegmenter.ToSpeech(written));
    }

    [Fact]
    public void Lexicon_RespellsWholeWords_BeforeCamelCaseSplitting()
    {
        var lexicon = SpeechSegmenter.ParseLexicon("Klives=Clives; KliveAgent=Clive Agent;  =ignored; bad");
        Assert.Equal(2, lexicon.Count);
        Assert.Equal("Clives asked Clive Agent", SpeechSegmenter.ToSpeech("Klives asked KliveAgent", lexicon));
        Assert.Equal("Klivesy stays", SpeechSegmenter.ToSpeech("Klivesy stays", lexicon));
    }

    // ── Local commands ──

    [Theory]
    [InlineData("Stop.", "Stop")]
    [InlineData("never mind", "Stop")]
    [InlineData("Be quiet!", "Stop")]
    [InlineData("Say that again?", "Repeat")]
    [InlineData("stop the trader", "None")]
    [InlineData("cancel the trade", "None")]
    [InlineData("okay", "None")]
    public void LocalCommands_MatchOnlyWholeUtterances(string transcript, string command)
    {
        Assert.Equal(command, TalkText.MatchCommand(transcript).ToString());
    }
}
