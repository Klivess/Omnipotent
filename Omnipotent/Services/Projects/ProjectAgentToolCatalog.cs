using Omnipotent.Services.KliveLLM;

namespace Omnipotent.Services.Projects;

/// <summary>
/// Builds the canonical tool set from the same effective perception profile used by the runners.
/// Keeping this in one place prevents the project setting, prompt and offered surface from drifting:
/// disabling vision removes only raw-pixel observations, never OCR/DOM/terminal or input capability.
/// </summary>
public static class ProjectAgentToolCatalog
{
    public static List<HFWrapper.HFTool> BuildCommanderCanonical(bool visionEnabled)
    {
        var tools = ProjectCommanderAgent.BuildCoreToolDefinitions();
        tools.AddRange(ProjectCommanderAgent.BuildComputerToolDefinitions(visionEnabled));
        return tools;
    }

    public static List<HFWrapper.HFTool> BuildWorkerCanonical(
        ProjectTierRouter router, ProjectAgentTier tier, bool visionEnabled)
    {
        ArgumentNullException.ThrowIfNull(router);
        var tools = ProjectCommanderAgent.BuildCoreToolDefinitions()
            .Where(t => router.IsToolAllowed(tier, t.function.name, visionEnabled)
                     && !ProjectTierRouter.IsCommanderOnly(t.function.name))
            .ToList();
        tools.AddRange(ProjectCommanderAgent.BuildComputerToolDefinitions(visionEnabled)
            .Where(t => router.IsToolAllowed(tier, t.function.name, visionEnabled)));
        return tools;
    }

    /// <summary>
    /// Images may be attached only when the project permits them, the tier can consume them and
    /// every configured route accepts image input. Requiring fallback compatibility prevents a
    /// primary-provider failure from handing an image-bearing transcript to a text-only fallback.
    /// </summary>
    public static async Task<bool> ResolveEffectiveVisionAsync(
        KliveLLM.KliveLLM llm,
        bool projectVisionEnabled,
        ProjectAgentTier tier,
        IReadOnlyList<string> modelRoutes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(llm);
        var routes = modelRoutes
            .Where(route => !string.IsNullOrWhiteSpace(route))
            .Select(route => route.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!projectVisionEnabled || tier == ProjectAgentTier.Text || routes.Count == 0)
            return false;

        return await AllRoutesSupportImageInputAsync(
            routes, (route, token) => llm.GetModelCapabilitiesAsync(route, token), ct);
    }

    /// <summary>
    /// Conservative route-set check kept as a seam for the fallback matrix tests. A capability
    /// lookup outage disables the optional image channel for this wake instead of taking down an
    /// otherwise fully capable text/DOM/CLI agent.
    /// </summary>
    internal static async Task<bool> AllRoutesSupportImageInputAsync(
        IReadOnlyList<string> modelRoutes,
        Func<string, CancellationToken, Task<ModelCapabilities>> getCapabilitiesAsync,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(getCapabilitiesAsync);
        if (modelRoutes.Count == 0) return false;
        foreach (string route in modelRoutes)
        {
            try
            {
                var capabilities = await getCapabilitiesAsync(route, ct);
                if (!capabilities.ImageInput) return false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch
            {
                return false;
            }
        }
        return true;
    }
}
