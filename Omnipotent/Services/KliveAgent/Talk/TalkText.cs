using System.Text;
using System.Text.RegularExpressions;
using Omnipotent.Services.KliveLLM;

namespace Omnipotent.Services.KliveAgent.Talk;

/// <summary>What a short utterance asks the talk session itself to do, without a model call.</summary>
internal enum TalkCommand
{
    None,
    /// <summary>"stop", "never mind", "be quiet": stop speaking and stop the run.</summary>
    Stop,
    /// <summary>"say that again": replay the last reply's audio.</summary>
    Repeat,
}

/// <summary>Pure text rules for talk mode: turn-taking hints, local commands, and transcript hygiene.</summary>
internal static class TalkText
{
    /// <summary>Default Whisper vocabulary: names it would otherwise spell as ordinary English.</summary>
    internal const string DefaultVocabulary =
        "Klives, KliveAgent, Omnipotent, KliveAPI, KliveLLM, KliveRAG, KliveMail, KliveChat, KliveCloud, " +
        "OmniTrader, Omniscience, OmniDefence, OmniTumblr, OmniGram, MemeScraper, Stratum, Projects, Commander, " +
        "AIRouter, Qwen, DeepSeek, Kokoro, Discord, Tumblr, Trading212";

    // Words a sentence does not end on. When a pause follows one of these, Klives is mid-thought even
    // if Whisper put a full stop after it.
    private static readonly HashSet<string> Continuations = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "but", "or", "so", "because", "cause", "then", "if", "that", "which", "who", "where", "when",
        "um", "uh", "erm", "er", "hmm", "like", "the", "a", "an", "to", "of", "with", "for", "in", "on", "at",
        "my", "your", "is", "are", "was", "it's", "its", "can", "could", "should", "would", "will", "please",
        "also", "plus", "about", "from", "into", "via",
    };

    private static readonly Regex Words = new(@"[\p{L}\p{N}']+", RegexOptions.Compiled);
    private static readonly Regex NonWord = new(@"[^\p{L}\p{N}' ]+", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// "complete" when the provisional transcript reads like a finished turn, "incomplete" when it
    /// trails off. The browser commits sooner on the first and waits longer on the second, which is
    /// where most of the silence-detection delay comes back from.
    /// </summary>
    internal static string EndpointHint(string? transcript)
    {
        string text = (transcript ?? string.Empty).Trim();
        if (text.Length == 0) return "incomplete";
        if (text.EndsWith("...", StringComparison.Ordinal) || text.EndsWith('…') || text.EndsWith(',')
            || text.EndsWith('-') || text.EndsWith('—'))
            return "incomplete";

        var words = Words.Matches(text);
        if (words.Count == 0) return "incomplete";
        string last = words[^1].Value;
        if (Continuations.Contains(last)) return "incomplete";

        char end = text[^1];
        if (end is '.' or '?' or '!') return "complete";
        // Whisper usually punctuates. A bare short answer ("yes", "no thanks") is still a whole turn.
        return words.Count <= 3 ? "complete" : "incomplete";
    }

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

    // What Whisper produces from silence, breath, a cough or room noise. Common enough on short or quiet
    // clips that a hands-free conversation would otherwise keep answering phantom "Thank you."s.
    private static readonly HashSet<string> PhantomPhrases = new(StringComparer.Ordinal)
    {
        "thank you", "thanks", "thank you very much", "thanks for watching", "thank you for watching",
        // Deliberately not "okay"/"yes"/"no": those are real answers to a question the agent asked.
        "thank you so much for watching", "please subscribe", "subscribe", "you", "bye", "bye bye",
        "oh", "uh", "um", "hmm", "so", "music", "silence", "applause", "laughter",
        "subtitles by the amara org community", "transcribed by", "i'm sorry", "the end",
    };

    /// <summary>
    /// True when the transcript is almost certainly not something Klives said: empty, punctuation only,
    /// a classic Whisper phantom on a short or quiet clip, or a segment Whisper itself marks as no speech.
    /// </summary>
    internal static bool LooksLikePhantom(AIRouterTranscript transcript, int audioMs, double rms)
    {
        string normal = NormalizeForMatch(transcript.Text);
        if (normal.Length == 0) return true;
        if (transcript.Detailed && transcript.NoSpeechProbability > 0.6 && transcript.AverageLogProbability < -0.8)
            return true;
        if (PhantomPhrases.Contains(normal) && (audioMs < 1500 || rms < 0.012 || transcript.NoSpeechProbability > 0.4))
            return true;
        return false;
    }

    /// <summary>
    /// The Whisper prompt: the vocabulary (so names come out spelled right) followed by the last thing
    /// the agent said (so an answer to its question is decoded in context). Whisper reads the prompt as
    /// preceding transcript and conditions on its end.
    /// </summary>
    internal static string WhisperPrompt(string? vocabulary, string? lastSpoken)
    {
        var sb = new StringBuilder();
        string vocab = string.IsNullOrWhiteSpace(vocabulary) ? DefaultVocabulary : vocabulary.Trim();
        sb.Append(vocab);
        if (!sb.ToString().EndsWith('.')) sb.Append('.');
        string tail = (lastSpoken ?? string.Empty).Trim();
        if (tail.Length > 0)
        {
            if (tail.Length > 300) tail = tail[^300..];
            sb.Append(' ').Append(tail);
        }
        return sb.ToString();
    }

    /// <summary>The last sentence of a reply, for the Whisper prompt.</summary>
    internal static string LastSentence(string? text)
    {
        string t = (text ?? string.Empty).Trim();
        if (t.Length == 0) return t;
        int cut = t.LastIndexOfAny(new[] { '.', '?', '!' }, Math.Max(0, t.Length - 2));
        return cut > 0 && cut < t.Length - 1 ? t[(cut + 1)..].Trim() : t;
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
