namespace Omnipotent.Services.KliveAgent;

internal sealed record KliveAgentRunSettings(
    bool StreamTokens, bool ComputerUseEnabled, int RetainedScreenshots, int ScriptTimeoutSeconds,
    string FastModel, string ReasoningModel, int MaxRunTokens, int MaxRunMinutes, int MaxLlmRetries,
    int ClipMaxFramesPerTurn)
{
    internal static async Task<KliveAgentRunSettings> LoadAsync(KliveAgent agent)
    {
        var stream = agent.GetBoolOmniSetting("KliveAgent_StreamTokens", true);
        var computer = agent.GetBoolOmniSetting("KliveAgent_ComputerUseEnabled", true);
        var screenshots = agent.GetIntOmniSetting("KliveAgent_MaxRetainedScreenshots", 3);
        var scriptTimeout = agent.GetIntOmniSetting("KliveAgent_ScriptTimeoutSeconds", 30);
        var fast = agent.GetStringOmniSetting("KliveAgent_FastModel", "");
        var reasoning = agent.GetStringOmniSetting("KliveAgent_ReasoningModel", "");
        var tokens = agent.GetIntOmniSetting("KliveAgent_MaxRunTokens", 600_000);
        var minutes = agent.GetIntOmniSetting("KliveAgent_MaxRunMinutes", 30);
        var retries = agent.GetIntOmniSetting("KliveAgent_MaxLlmRetries", 2);
        var frames = agent.GetIntOmniSetting("KliveAgent_ClipMaxFramesPerTurn", 8);
        await Task.WhenAll(stream, computer, screenshots, scriptTimeout, fast, reasoning, tokens, minutes, retries, frames);
        return new(await stream, await computer, Math.Max(1, await screenshots), Math.Max(1, await scriptTimeout),
            await fast ?? "", await reasoning ?? "", await tokens, await minutes, Math.Max(0, await retries), Math.Max(1, await frames));
    }
}
