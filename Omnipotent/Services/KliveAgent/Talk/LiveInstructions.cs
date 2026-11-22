using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.KliveLLM;

namespace Omnipotent.Services.KliveAgent.Talk;

/// <summary>
/// What GPT-Live is told when a voice session starts: its instructions (the "frontend" prompt, in the
/// shape OpenAI's GPT-Live prompting guide recommends — role, style, backchannel, interruption and
/// delegation policies), the recent conversation, and the session configuration around them.
///
/// The split of work: GPT-Live hears, speaks and takes turns; every answer that depends on Klives's
/// systems, data, memory or an action is delegated to KliveAgent, which runs its usual tool loop and
/// hands back what to say.
/// </summary>
internal static class LiveInstructions
{
    /// <summary>GPT-Live's history limit is 8,192 tokens; a character budget well under it.</summary>
    internal const int HistoryCharBudget = 20_000;
    internal const int HistoryMessageChars = 700;

    /// <summary>The browser may mute, unmute and hang up over its data channel; nothing else. Instructions,
    /// thinking and commentary only come from the server's sideband.</summary>
    internal static readonly string[] BrowserClientEvents = { "session.input_audio.mute", "session.input_audio.unmute", "session.close" };

    internal static string Build(string model, string? vocabulary, string? extra)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Role");
        sb.AppendLine("You are the voice of KliveAgent, Klives's personal agent. KliveAgent runs Omnipotent, his software platform, and every service on it: trading, scraping, games, mail, cloud storage, his autonomous agents and more. You are talking with Klives himself, live. KliveAgent is your backend: it does the thinking and the work, and you are how Klives hears it.");
        sb.AppendLine();
        sb.AppendLine("# Speaking style");
        sb.AppendLine("- British English. Natural, warm and brief: one to three short sentences unless he asks for more.");
        sb.AppendLine("- Say results plainly. Never read out code, file paths, URLs, long IDs or tables; say they are on screen.");
        sb.AppendLine("- Round numbers sensibly and say times the way a person would.");
        sb.AppendLine();
        sb.AppendLine("# Backchannel policy");
        sb.AppendLine("- While he is speaking, stay quiet. Do not fill short pauses; he may still be thinking.");
        sb.AppendLine("- When he has finished and the answer needs the backend, a very short acknowledgement is fine, then wait.");
        sb.AppendLine();
        sb.AppendLine("# Interruption policy");
        sb.AppendLine("- If he talks over you, stop and listen. Do not start the interrupted sentence again unless he asks.");
        sb.AppendLine("- If he changes or corrects the request, follow the new one.");
        sb.AppendLine();
        sb.AppendLine("# Delegation policy");
        sb.AppendLine("Backend tools: KliveAgent can see and change everything on his platform: service health, logs, settings, his data and files, memory of past conversations, the web, code, his computer, schedules and messages.");
        sb.AppendLine("Delegate to the backend when:");
        sb.AppendLine("- the answer depends on his systems, his data, anything he told KliveAgent before, the date or time, the web, or anything that changes;");
        sb.AppendLine("- he asks for something to be done, checked, changed, started, stopped, sent or scheduled;");
        sb.AppendLine("- he asks to stop or cancel work that is under way (only the backend can cancel it);");
        sb.AppendLine("- he asks you to repeat what the backend last said;");
        sb.AppendLine("- you are not certain of the answer.");
        sb.AppendLine("Do not delegate when:");
        sb.AppendLine("- you can answer from this conversation or from a backend result that is still current;");
        sb.AppendLine("- it is small talk or a greeting, or you need to ask what he means.");
        sb.AppendLine("Delegate to the backend before giving an answer that depends on backend work. Do not guess the result while waiting. A spoken promise such as \"I'm checking that\" does not mean the work has started; only the backend's result says what happened.");
        sb.AppendLine("When the backend's words arrive, say them in your own voice, briefly, and add no facts they did not give. If they say something failed, say so. If he has moved on, do not read out a stale result. Backend notes in brackets are for you, not to be read out.");

        string vocab = (vocabulary ?? string.Empty).Trim();
        if (vocab.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# Names you will hear and say");
            sb.AppendLine(vocab.Length > 1500 ? vocab[..1500] : vocab);
        }
        string more = (extra ?? string.Empty).Trim();
        if (more.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# Klives's own rules");
            sb.AppendLine(more.Length > 6000 ? more[..6000] : more);
        }
        return ForModel(sb.ToString().TrimEnd(), model);
    }

    /// <summary>
    /// GPT-Live-1 (the first model) predates the "delegate" wording: its guide says to ask it to
    /// generate &lt;|SpawnThinking|&gt; instead. Every later model takes the instructions as written.
    /// </summary>
    internal static string ForModel(string instructions, string? model)
    {
        string id = (model ?? string.Empty).Trim().ToLowerInvariant();
        bool firstGeneration = id == "gpt-live-1" || (id.StartsWith("gpt-live-1-", StringComparison.Ordinal) && !id.StartsWith("gpt-live-1.", StringComparison.Ordinal));
        if (!firstGeneration) return instructions;
        return instructions
            .Replace("Do not delegate", "Do not generate <|SpawnThinking|>", StringComparison.Ordinal)
            .Replace("Delegate to the backend", "Generate <|SpawnThinking|>", StringComparison.Ordinal);
    }

    private static readonly Regex Fenced = new(@"```[\s\S]*?(```|$)", RegexOptions.Compiled);
    private static readonly Regex Scripts = new(@"\{\{\{[\s\S]*?(\}\}\}|$)", RegexOptions.Compiled);
    private static readonly Regex Urls = new(@"https?://\S+", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>A message as the voice should know it: no code, scripts or links, and not too long.</summary>
    internal static string CleanForHistory(string? text)
    {
        string t = text ?? string.Empty;
        t = Fenced.Replace(t, " (shown on screen) ");
        t = Scripts.Replace(t, " ");
        t = Urls.Replace(t, "(a link)");
        t = t.Replace("**", "").Replace("__", "").Replace("`", "");
        t = Whitespace.Replace(t, " ").Trim();
        return t.Length > HistoryMessageChars ? t[..HistoryMessageChars].TrimEnd() + "…" : t;
    }

    /// <summary>The newest messages of the conversation that fit, oldest first, as GPT-Live input items.</summary>
    internal static JArray History(IReadOnlyList<(bool User, string Text)> messages, int maxMessages)
    {
        var picked = new List<(bool User, string Text)>();
        int budget = HistoryCharBudget;
        for (int i = messages.Count - 1; i >= 0 && picked.Count < maxMessages; i--)
        {
            string text = CleanForHistory(messages[i].Text);
            if (text.Length == 0) continue;
            if (text.Length > budget) break;
            budget -= text.Length;
            picked.Add((messages[i].User, text));
        }
        picked.Reverse();
        var items = new JArray();
        foreach (var (user, text) in picked)
        {
            items.Add(new JObject
            {
                ["type"] = "message",
                ["role"] = user ? "user" : "assistant",
                ["content"] = new JArray { new JObject { ["type"] = user ? "input_text" : "output_text", ["text"] = text } },
            });
        }
        return items;
    }

    /// <summary>The <c>session</c> object for <c>POST /v1/live/sessions</c> (WebRTC: no audio format).</summary>
    internal static JObject Session(KliveAgentTalkSettings settings, string voice, JArray history)
    {
        var session = new JObject
        {
            ["model"] = settings.LiveModel,
            ["instructions"] = Build(settings.LiveModel, settings.Vocabulary, settings.ExtraInstructions),
            ["audio"] = new JObject { ["output"] = new JObject { ["voice"] = OpenAILiveClient.VoiceToken(voice) } },
            ["delegation"] = new JObject { ["type"] = "client", ["acknowledgement_delay_ms"] = settings.AckDelayMs },
            ["store"] = false,
            ["client"] = new JObject
            {
                ["data_channel"] = new JObject { ["allowed_client_events"] = new JArray(BrowserClientEvents) },
            },
        };
        if (history.Count > 0) session["input"] = history;
        if (settings.IdleSeconds > 0) session["idle_timeout_s"] = settings.IdleSeconds;
        return session;
    }

    private static readonly Regex CustomVoiceId = new(@"^[A-Za-z0-9_\-]{1,128}$", RegexOptions.Compiled);

    /// <summary>The voice the browser asked for when it is one GPT-Live could take, else the configured one.</summary>
    internal static string ChooseVoice(string? requested, string configured)
    {
        string v = (requested ?? string.Empty).Trim();
        if (v.Length == 0) return configured;
        if (OpenAILiveClient.BuiltInVoices.Contains(v, StringComparer.OrdinalIgnoreCase)) return v.ToLowerInvariant();
        return CustomVoiceId.IsMatch(v) ? v : configured;
    }
}
