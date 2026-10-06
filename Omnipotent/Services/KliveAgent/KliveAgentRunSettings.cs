namespace Omnipotent.Services.KliveAgent;

/// <summary>
/// Per-run settings, read once at the start of a run.
///
/// The long-task limits deliberately use NEW setting names rather than new defaults on the old ones.
/// OmniSettings persists a default the first time it is read, so every existing install already holds
/// KliveAgent_MaxRunTokens=600000 / MaxRunMinutes=30 / MaxLlmRetries=2 — values sized for a chat turn.
/// A computer-use task re-sends its whole context every step, so 600K cumulative prompt tokens is
/// about fifteen browser actions, and two quick retries cannot ride out a provider blip at step forty.
/// Changing only the C# default would have changed nothing on the machines that matter.
/// </summary>
internal sealed record KliveAgentRunSettings(
    bool StreamTokens, bool ComputerUseEnabled, int RetainedScreenshots, int ScriptTimeoutSeconds,
    string FastModel, string ReasoningModel, int MaxRunBillableTokens, int MaxTaskMinutes, int ModelRetryAttempts,
    int ClipMaxFramesPerTurn, int ModelStreamIdleSeconds, string ComputerTarget, bool VisionEnabled)
{
    internal const string ComputerTargetAuto = "auto";
    internal const string ComputerTargetContainer = "container";
    internal const string ComputerTargetHost = "host";
    internal static readonly string[] ComputerTargets = { ComputerTargetAuto, ComputerTargetContainer, ComputerTargetHost };

    internal static async Task<KliveAgentRunSettings> LoadAsync(KliveAgent agent)
    {
        var stream = agent.GetBoolOmniSetting("KliveAgent_StreamTokens", true);
        var computer = agent.GetBoolOmniSetting("KliveAgent_ComputerUseEnabled", true);
        var screenshots = agent.GetIntOmniSetting("KliveAgent_MaxRetainedScreenshots", 3);
        var scriptTimeout = agent.GetIntOmniSetting("KliveAgent_ScriptTimeoutSeconds", 30);
        var fast = agent.GetStringOmniSetting("KliveAgent_FastModel", "");
        var reasoning = agent.GetStringOmniSetting("KliveAgent_ReasoningModel", "");
        // Billable = uncached prompt + completion (+10% of cached reads). Not enforced at all on a
        // flat-fee provider, where tokens move no money. 0 disables.
        var tokens = agent.GetIntOmniSetting("KliveAgent_MaxRunBillableTokens", 4_000_000);
        // Wall clock for one run, sized for multi-step computer tasks. 0 disables.
        var minutes = agent.GetIntOmniSetting("KliveAgent_MaxTaskMinutes", 240);
        var retries = agent.GetIntOmniSetting("KliveAgent_ModelRetryAttempts", 6);
        var frames = agent.GetIntOmniSetting("KliveAgent_ClipMaxFramesPerTurn", 8);
        // A streamed reply that sends nothing at all for this long is abandoned and retried.
        var idle = agent.GetIntOmniSetting("KliveAgent_ModelStreamIdleSeconds", 240);
        var target = agent.GetDropdownOmniSetting("KliveAgent_ComputerTarget", ComputerTargetAuto, ComputerTargets);
        var vision = agent.GetBoolOmniSetting("KliveAgent_VisionEnabled", true);
        await Task.WhenAll(stream, computer, screenshots, scriptTimeout, fast, reasoning, tokens, minutes, retries,
            frames, idle, target, vision);
        return new(await stream, await computer, Math.Max(1, await screenshots), Math.Max(1, await scriptTimeout),
            await fast ?? "", await reasoning ?? "", Math.Max(0, await tokens), Math.Max(0, await minutes),
            Math.Clamp(await retries, 0, 20), Math.Max(1, await frames), Math.Clamp(await idle, 30, 1800),
            NormalizeTarget(await target), await vision);
    }

    internal static string NormalizeTarget(string? value)
    {
        string v = (value ?? "").Trim().ToLowerInvariant();
        return ComputerTargets.Contains(v) ? v : ComputerTargetAuto;
    }
}
