using Omnipotent.Services.KliveLLM;

namespace Omnipotent.Services.KliveAgent.Talk;

/// <summary>
/// Talk-mode settings, read when a voice socket opens. OmniSettings persists a default the first time it
/// is read, so changing one of these defaults later needs a new setting name.
///
/// Since Nov 2026 the voice is OpenAI's GPT-Live (the KliveAgent_Live* settings); KliveAgent is the
/// backend it delegates to (the KliveAgent_Talk* settings that shape a spoken run are unchanged). The
/// old AIRouter Whisper/Kokoro settings (voice bm_george, speed, language, audio format, fillers) are no
/// longer read.
/// </summary>
internal sealed record KliveAgentTalkSettings(
    bool Enabled,
    string LiveModel,
    string LiveVoice,
    int IdleSeconds,
    int AckDelayMs,
    string ExtraInstructions,
    int HistoryMessages,
    int ProgressSeconds,
    double ActivePerMinuteUsd,
    double StandbyPerMinuteUsd,
    string Thinking,
    string Model,
    string Vocabulary,
    string Pronunciations,
    bool ReserveSlot,
    int ReserveIdleMinutes,
    int SessionMaxTokens)
{
    internal static readonly string[] ThinkingLevels = { "off", "low", "medium" };

    /// <summary>The default only: any GPT-Live model id can be set in KliveAgent_LiveModel.</summary>
    internal const string DefaultLiveModel = "gpt-live-1.1";

    internal static async Task<KliveAgentTalkSettings> LoadAsync(KliveAgent agent)
    {
        var enabled = agent.GetBoolOmniSetting("KliveAgent_TalkEnabled", true);
        // Which GPT-Live model answers. A new model id is a settings change, not a code change.
        var liveModel = agent.GetStringOmniSetting("KliveAgent_LiveModel", DefaultLiveModel);
        // A built-in GPT-Live voice (marin, cedar, …) or a custom voice id. Fixed for a session: a change
        // applies from the next one.
        var liveVoice = agent.GetStringOmniSetting("KliveAgent_LiveVoice", OpenAILiveClient.DefaultVoice);
        // Seconds of silence before the session drops to standby (billed at the lower rate, woken by
        // speech). 0 keeps it active the whole time. GPT-Live accepts 5–60.
        var idle = agent.GetIntOmniSetting("KliveAgent_LiveIdleSeconds", 20);
        // How long GPT-Live waits for KliveAgent's first words before saying a short acknowledgement
        // itself ("On it."). 0–800 ms; KliveAgent's own first clause cancels it when it is quicker.
        var ackDelay = agent.GetIntOmniSetting("KliveAgent_LiveAckDelayMs", 500);
        // Added to the voice's built-in instructions (LiveInstructions), for Klives's own rules.
        var extra = agent.GetStringOmniSetting("KliveAgent_LiveExtraInstructions", "");
        // How many earlier messages of the conversation the voice is given when it starts.
        var history = agent.GetIntOmniSetting("KliveAgent_LiveHistoryMessages", 20);
        // With KliveAgent still working and nothing said for this long, the voice says what it is doing.
        // 0 turns it off.
        var progress = agent.GetIntOmniSetting("KliveAgent_LiveProgressSeconds", 10);
        // GPT-Live's prices, per minute, for the KliveLLM ledger (OpenAI's Nov 2026 list prices).
        var activePrice = agent.GetStringOmniSetting("KliveAgent_LiveActivePerMinuteUsd", "0.05");
        var standbyPrice = agent.GetStringOmniSetting("KliveAgent_LiveStandbyPerMinuteUsd", "0.02");
        // Spoken turns think at a fixed, low level: reasoning tokens are the largest latency cost of a
        // turn and nobody waits well in silence.
        var thinking = agent.GetDropdownOmniSetting("KliveAgent_TalkThinking", "low", ThinkingLevels);
        // Empty = the provider's default model. One model for the whole conversation: switching models
        // mid-conversation throws the provider's prompt cache away.
        var model = agent.GetStringOmniSetting("KliveAgent_TalkModel", "");
        // Names the voice should expect to hear and say (it is told them in its instructions).
        var vocabulary = agent.GetStringOmniSetting("KliveAgent_TalkVocabulary", TalkText.DefaultVocabulary);
        // Written=Spoken pairs, ';'-separated, applied to what KliveAgent hands the voice to say.
        var pronunciations = agent.GetStringOmniSetting("KliveAgent_TalkPronunciations", "");
        var reserve = agent.GetBoolOmniSetting("KliveAgent_TalkReserveSlot", true);
        var reserveIdle = agent.GetIntOmniSetting("KliveAgent_TalkReserveIdleMinutes", 3);
        var maxTokens = agent.GetIntOmniSetting("KliveAgent_TalkSessionMaxTokens", 48_000);
        await Task.WhenAll(enabled, liveModel, liveVoice, idle, ackDelay, extra, history, progress, activePrice, standbyPrice,
            thinking, model, vocabulary, pronunciations, reserve, reserveIdle, maxTokens);

        string thinkingLevel = (await thinking ?? "low").Trim().ToLowerInvariant();
        if (!ThinkingLevels.Contains(thinkingLevel)) thinkingLevel = "low";
        int idleSeconds = await idle;
        idleSeconds = idleSeconds <= 0 ? 0 : Math.Clamp(idleSeconds, 5, 60);

        return new KliveAgentTalkSettings(
            await enabled,
            string.IsNullOrWhiteSpace(await liveModel) ? DefaultLiveModel : (await liveModel).Trim(),
            string.IsNullOrWhiteSpace(await liveVoice) ? OpenAILiveClient.DefaultVoice : (await liveVoice).Trim(),
            idleSeconds,
            Math.Clamp(await ackDelay, 0, 800),
            (await extra ?? "").Trim(),
            Math.Clamp(await history, 0, 128),
            Math.Clamp(await progress, 0, 120),
            Price(await activePrice, 0.05),
            Price(await standbyPrice, 0.02),
            thinkingLevel,
            (await model ?? "").Trim(),
            await vocabulary ?? TalkText.DefaultVocabulary,
            await pronunciations ?? "",
            await reserve,
            Math.Clamp(await reserveIdle, 1, 60),
            Math.Clamp(await maxTokens, 8_000, 200_000));
    }

    private static double Price(string? text, double fallback) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) && v >= 0 && v < 100
            ? v
            : fallback;
}
