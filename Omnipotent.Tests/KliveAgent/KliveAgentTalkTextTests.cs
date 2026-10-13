using System.Buffers.Binary;
using Omnipotent.Services.KliveAgent.Talk;
using Omnipotent.Services.KliveLLM;

namespace Omnipotent.Tests.KliveAgent;

/// <summary>
/// The text side of talk mode: what gets said (and when it is safe to say it), how turn-taking is
/// judged from a provisional transcript, and the audio framing the browser and server share.
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

    // ── Turn-taking ──

    [Theory]
    [InlineData("Restart the trader.", "complete")]
    [InlineData("What's the status?", "complete")]
    [InlineData("yes", "complete")]
    [InlineData("Restart the trader and.", "incomplete")]
    [InlineData("Check the logs, um", "incomplete")]
    [InlineData("I want you to check the", "incomplete")]
    [InlineData("so...", "incomplete")]
    [InlineData("", "incomplete")]
    public void EndpointHint_ReadsWhetherTheSentenceSoundedFinished(string transcript, string hint)
    {
        Assert.Equal(hint, TalkText.EndpointHint(transcript));
    }

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

    [Fact]
    public void Phantoms_OnShortOrQuietClips_AreRecognised_ButRealAnswersAreNot()
    {
        var thanks = new AIRouterTranscript("Thank you.", "en", 0.8, 0, 0, false);
        Assert.True(TalkText.LooksLikePhantom(thanks, audioMs: 700, rms: 0.05));
        Assert.False(TalkText.LooksLikePhantom(thanks, audioMs: 2500, rms: 0.08));
        Assert.True(TalkText.LooksLikePhantom(new AIRouterTranscript(" ... ", null, 0, 0, 0, false), 1000, 0.1));
        Assert.True(TalkText.LooksLikePhantom(new AIRouterTranscript("Restart it", "en", 1, 0.9, -1.2, true), 1000, 0.1));
        Assert.False(TalkText.LooksLikePhantom(new AIRouterTranscript("Okay.", "en", 0.5, 0.1, -0.2, true), 500, 0.05));
        Assert.False(TalkText.LooksLikePhantom(new AIRouterTranscript("Restart the trader.", "en", 1.5, 0.05, -0.3, true), 1500, 0.06));
    }

    [Fact]
    public void WhisperPrompt_IsVocabularyThenTheLastThingSaid()
    {
        string prompt = TalkText.WhisperPrompt("Klives, OmniTrader", "Shall I restart OmniTrader?");
        Assert.Equal("Klives, OmniTrader. Shall I restart OmniTrader?", prompt);
        Assert.StartsWith("Klives, KliveAgent", TalkText.WhisperPrompt(null, null));
        Assert.Equal("Shall I?", TalkText.LastSentence("It is down. Shall I?"));
    }

    // ── Wire format ──

    [Fact]
    public void Wav_HeaderDescribesTheSamples_AndItsDurationReadsBack()
    {
        var pcm = new byte[TalkWire.CaptureBytesPerSecond / 2]; // 500 ms of 16 kHz mono PCM16
        byte[] wav = TalkWire.Wav(pcm);
        Assert.Equal(44 + pcm.Length, wav.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(16000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(pcm.Length, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
        Assert.Equal(500, TalkWire.WavDurationMs(wav));
        Assert.Null(TalkWire.WavDurationMs(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void Frames_CarryTheirKindAndBigEndianIds()
    {
        byte[] speech = TalkWire.SpeechFrame(turn: 258, seq: 3, new byte[] { 9, 9 });
        Assert.Equal(new byte[] { TalkWire.ServerSpeech, 1, 2, 0, 3, 9, 9 }, speech);

        byte[] client = { TalkWire.ClientAudio, 0, 7, 10, 20 };
        Assert.True(TalkWire.TryReadClientAudio(client, out int utt, out var samples));
        Assert.Equal(7, utt);
        Assert.Equal(new byte[] { 10, 20 }, samples.ToArray());
        Assert.False(TalkWire.TryReadClientAudio(new byte[] { TalkWire.ServerSpeech, 0, 1 }, out _, out _));
    }

    [Fact]
    public void Rms_SeparatesSilenceFromSpeech()
    {
        Assert.Equal(0, TalkWire.Rms(new byte[3200]));
        Assert.True(TalkWire.Rms(Tone(0.3, 3200)) > 0.15);
    }

    internal static byte[] Tone(double amplitude, int bytes)
    {
        var pcm = new byte[bytes];
        for (int i = 0; i < bytes / 2; i++)
        {
            short sample = (short)(Math.Sin(i * 2 * Math.PI * 220 / 16000) * amplitude * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), sample);
        }
        return pcm;
    }
}
