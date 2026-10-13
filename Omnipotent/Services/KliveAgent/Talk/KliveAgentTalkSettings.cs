namespace Omnipotent.Services.KliveAgent.Talk;

/// <summary>
/// Talk-mode settings, read when a talk socket opens. OmniSettings persists a default the first time it
/// is read, so changing one of these defaults later needs a new setting name.
/// </summary>
internal sealed record KliveAgentTalkSettings(
    bool Enabled,
    string Voice,
    double Speed,
    string Thinking,
    string Model,
    string Language,
    string Vocabulary,
    string Pronunciations,
    string AudioFormat,
    bool ReserveSlot,
    int ReserveIdleMinutes,
    int SessionMaxTokens,
    bool Fillers)
{
    internal static readonly string[] ThinkingLevels = { "off", "low", "medium" };
    internal static readonly string[] AudioFormats = { "wav", "mp3" };

    internal const string DefaultVoice = "bm_george";

    internal static async Task<KliveAgentTalkSettings> LoadAsync(KliveAgent agent)
    {
        var enabled = agent.GetBoolOmniSetting("KliveAgent_TalkEnabled", true);
        var voice = agent.GetStringOmniSetting("KliveAgent_TalkVoice", DefaultVoice);
        // Percent, so the settings page shows a whole number: 100 = normal speed.
        var speed = agent.GetIntOmniSetting("KliveAgent_TalkSpeed", 100);
        // Spoken turns think at a fixed, low level: reasoning tokens are the largest latency cost of a
        // turn and nobody waits well in silence.
        var thinking = agent.GetDropdownOmniSetting("KliveAgent_TalkThinking", "low", ThinkingLevels);
        // Empty = the provider's default model. One model for the whole conversation: switching models
        // mid-conversation throws the provider's prompt cache away.
        var model = agent.GetStringOmniSetting("KliveAgent_TalkModel", "");
        // Telling Whisper the language skips its detection pass and stops a two-word command being
        // heard as another language. Empty = detect.
        var language = agent.GetStringOmniSetting("KliveAgent_TalkLanguage", "en");
        var vocabulary = agent.GetStringOmniSetting("KliveAgent_TalkVocabulary", TalkText.DefaultVocabulary);
        // Written=Spoken pairs, ';'-separated, for names the synthesiser says wrong.
        var pronunciations = agent.GetStringOmniSetting("KliveAgent_TalkPronunciations", "");
        // WAV plays back without gaps between segments; MP3 is a fifth of the size.
        var format = agent.GetDropdownOmniSetting("KliveAgent_TalkAudioFormat", "wav", AudioFormats);
        var reserve = agent.GetBoolOmniSetting("KliveAgent_TalkReserveSlot", true);
        var reserveIdle = agent.GetIntOmniSetting("KliveAgent_TalkReserveIdleMinutes", 3);
        var maxTokens = agent.GetIntOmniSetting("KliveAgent_TalkSessionMaxTokens", 48_000);
        var fillers = agent.GetBoolOmniSetting("KliveAgent_TalkFillers", true);
        await Task.WhenAll(enabled, voice, speed, thinking, model, language, vocabulary, pronunciations, format,
            reserve, reserveIdle, maxTokens, fillers);

        string thinkingLevel = (await thinking ?? "low").Trim().ToLowerInvariant();
        if (!ThinkingLevels.Contains(thinkingLevel)) thinkingLevel = "low";
        string audioFormat = (await format ?? "wav").Trim().ToLowerInvariant();
        if (!AudioFormats.Contains(audioFormat)) audioFormat = "wav";

        return new KliveAgentTalkSettings(
            await enabled,
            string.IsNullOrWhiteSpace(await voice) ? DefaultVoice : (await voice).Trim(),
            Math.Clamp(await speed, 50, 200) / 100.0,
            thinkingLevel,
            (await model ?? "").Trim(),
            (await language ?? "").Trim(),
            await vocabulary ?? TalkText.DefaultVocabulary,
            await pronunciations ?? "",
            audioFormat,
            await reserve,
            Math.Clamp(await reserveIdle, 1, 60),
            Math.Clamp(await maxTokens, 8_000, 200_000),
            await fillers);
    }
}
