using System.Text.RegularExpressions;

namespace Omnipotent.Services.KliveAgent.Talk;

/// <summary>What a short spoken request asks the voice session itself to do, without a model call.</summary>
internal enum TalkCommand
{
    None,
    /// <summary>"stop", "never mind", "be quiet": stop speaking and stop the run.</summary>
    Stop,
    /// <summary>"say that again": say the last reply again.</summary>
    Repeat,
}

/// <summary>Pure text rules for talk mode: local commands and transcript matching.</summary>
internal static class TalkText
{
    /// <summary>Default vocabulary: names the voice would otherwise hear and spell as ordinary English.</summary>
    internal const string DefaultVocabulary =
        "Klives, KliveAgent, Omnipotent, KliveAPI, KliveLLM, KliveRAG, KliveMail, KliveChat, KliveCloud, " +
        "OmniTrader, Omniscience, OmniDefence, OmniTumblr, OmniGram, OmniBlox, MemeScraper, Stratum, Demiurge, Steward, " +
        "Commander, AIRouter, Qwen, DeepSeek, Discord, Tumblr, Trading212";

    private static readonly Regex NonWord = new(@"[^\p{L}\p{N}' ]+", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    private static readonly HashSet<string> StopPhrases = new(StringComparer.Ordinal)
    {
        "stop", "stop it", "stop that", "stop talking", "cancel", "cancel that", "never mind", "nevermind",
        "shut up", "be quiet", "quiet", "enough", "thats enough", "that's enough", "hush", "okay stop", "ok stop",
    };

    private static readonly HashSet<string> RepeatPhrases = new(StringComparer.Ordinal)
    {
        "repeat that", "repeat", "say that again", "say again", "what did you say", "come again",
        "pardon", "sorry what", "can you repeat that", "could you repeat that",
    };

    /// <summary>Only an utterance that is ENTIRELY a command counts, so "stop the trader" still reaches
    /// the agent as a request.</summary>
    internal static TalkCommand MatchCommand(string? transcript)
    {
        string normal = NormalizeForMatch(transcript);
        if (normal.Length == 0) return TalkCommand.None;
        if (StopPhrases.Contains(normal)) return TalkCommand.Stop;
        if (RepeatPhrases.Contains(normal)) return TalkCommand.Repeat;
        return TalkCommand.None;
    }

    internal static string NormalizeForMatch(string? text)
    {
        string lower = (text ?? string.Empty).ToLowerInvariant().Replace('’', '\'');
        lower = NonWord.Replace(lower, " ");
        return Spaces.Replace(lower, " ").Trim();
    }

    /// <summary>Same words, ignoring case and punctuation — whether a speculative transcript can stand in
    /// for the final one when deciding which prepared context to use.</summary>
    internal static bool SameWords(string? a, string? b) =>
        string.Equals(NormalizeForMatch(a), NormalizeForMatch(b), StringComparison.Ordinal);
}
