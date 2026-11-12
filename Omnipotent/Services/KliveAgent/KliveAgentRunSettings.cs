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

    /// <summary>Longest a script may ask for with its own timeoutSeconds.</summary>
    internal const int MaxScriptTimeoutSeconds = 300;

    /// <summary>How long a compile may take before the script is reported as not run. Its own budget:
    /// nine live "compile took longer than 30 s" failures were really 42 to 377 s of a busy server.</summary>
    public int ScriptCompileBudgetSeconds { get; init; } = 120;

    /// <summary>Fix the mechanical compile errors before the model is told (KliveAgentScriptRepair).</summary>
    public bool ScriptAutoRepair { get; init; } = true;

    /// <summary>How long a conversation's script variables outlive a run, so a follow-up message can use
    /// them and has no cold first compile. 0 gives every run a new session, as before.</summary>
    public int KeepScriptSessionMinutes { get; init; } = 20;

    /// <summary>Longest one native or service tool call may run before the model is told it timed out.
    /// Two live calls ran 2 h and 3 h 16 min with nothing bounding them. 0 turns the limit off.</summary>
    public int NativeToolDeadlineSeconds { get; init; } = 600;

    internal const string ThinkingBySteps = "steps";
    internal const string ThinkingByErrors = "errors";
    internal static readonly string[] ThinkingEscalationModes = { ThinkingBySteps, ThinkingByErrors };

    /// <summary>
    /// What raises the reasoning effort during a run. "steps" (as it has always been): errors, and also
    /// simply reaching step 6 (medium) and step 12 (high), so a long run thinks at "high" for nearly all
    /// of its steps. "errors": only a failing step raises it. The stages model.think.low/medium/high
    /// record what each level costs per call; switch once those say the step rule is buying nothing.
    /// </summary>
    public string ThinkingEscalation { get; init; } = ThinkingBySteps;

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
        var compileBudget = agent.GetIntOmniSetting("KliveAgent_ScriptCompileBudgetSeconds", 120);
        var autoRepair = agent.GetBoolOmniSetting("KliveAgent_ScriptAutoRepair", true);
        var keepSession = agent.GetIntOmniSetting("KliveAgent_KeepScriptSessionMinutes", 20);
        var toolDeadline = agent.GetIntOmniSetting("KliveAgent_NativeToolDeadlineSeconds", 600);
        var thinking = agent.GetDropdownOmniSetting("KliveAgent_ThinkingEscalationMode", ThinkingBySteps, ThinkingEscalationModes);
        await Task.WhenAll(stream, computer, screenshots, scriptTimeout, fast, reasoning, tokens, minutes, retries,
            frames, idle, target, vision, compileBudget, autoRepair, keepSession, toolDeadline, thinking);
        return new(await stream, await computer, Math.Max(1, await screenshots), Math.Max(1, await scriptTimeout),
            await fast ?? "", await reasoning ?? "", Math.Max(0, await tokens), Math.Max(0, await minutes),
            Math.Clamp(await retries, 0, 20), Math.Max(1, await frames), Math.Clamp(await idle, 30, 1800),
            NormalizeTarget(await target), await vision)
        {
            ScriptCompileBudgetSeconds = Math.Clamp(await compileBudget, 10, 900),
            ScriptAutoRepair = await autoRepair,
            KeepScriptSessionMinutes = Math.Clamp(await keepSession, 0, 240),
            NativeToolDeadlineSeconds = Math.Clamp(await toolDeadline, 0, 86_400),
            ThinkingEscalation = string.Equals((await thinking)?.Trim(), ThinkingByErrors, StringComparison.OrdinalIgnoreCase)
                ? ThinkingByErrors : ThinkingBySteps,
        };
    }

    internal static string NormalizeTarget(string? value)
    {
        string v = (value ?? "").Trim().ToLowerInvariant();
        return ComputerTargets.Contains(v) ? v : ComputerTargetAuto;
    }
}
