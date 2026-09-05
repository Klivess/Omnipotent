using Newtonsoft.Json.Linq;
using Omnipotent.Services.HostControl;
using Omnipotent.Services.KliveLLM;
using Omnipotent.Services.ServiceTools;

namespace Omnipotent.Services.KliveAgent;

/// <summary>
/// KliveAgent's bridge to the Service Surface: it owns the live invoker, decides which dedicated
/// service tools this run offers, and routes a tool call to the right place.
///
/// Why a bridge rather than wiring straight into the brain: KliveAgentBrain's tool plumbing is static
/// (BuildToolDefinitions / IsNonScriptTool / DispatchNativeToolAsync), while invoking needs the live
/// service graph and this agent's settings. The catalogue itself is process-wide and immutable
/// (<see cref="OmniToolRegistry.Shared"/>), so the static side reads it directly and only execution
/// comes through here.
/// </summary>
public sealed class KliveAgentServiceTools
{
    /// <summary>Services whose dedicated tools are offered on every run unless Klives changes it.
    /// Omniscience is pinned as a whole (all of its groups) because it is the agent's memory of
    /// people and it needs granular reach, not one search box.</summary>
    public const string DefaultPinnedTools = "omniscience_*,omnitrader,klivemail,klivecloud,projects";

    private readonly KliveAgent agent;

    public KliveAgentServiceTools(KliveAgent agent)
    {
        this.agent = agent;
        Audit = new OmniToolAudit();
        Invoker = new OmniToolInvoker(OmniToolRegistry.Shared,
            () => agent.GetActiveServices(),
            Audit,
            message => agent.ServiceLog(message))
        {
            ApprovalGate = RequestApprovalAsync,
        };
        Api = new OmniApiClient(() => agent.GetActiveServices(), Audit,
            message => agent.ServiceLog(message));
    }

    public OmniToolRegistry Registry => OmniToolRegistry.Shared;
    public OmniToolAudit Audit { get; }
    public OmniToolInvoker Invoker { get; }
    public OmniApiClient Api { get; }

    // -- Routing (static: the brain's dispatch surface is static) --

    /// <summary>True for every tool this bridge handles. Deliberately claims a name even when service
    /// tools are switched off: the alternative is KliveAgentBrain's catch-all treating the name as
    /// execute_csharp and compiling the arguments JSON as C#, which fails in a way that teaches the
    /// model nothing.</summary>
    public static bool IsHandled(string? toolName)
        => !string.IsNullOrEmpty(toolName)
           && (toolName == OmniToolCatalog.UniversalServiceTool
               || toolName == OmniToolCatalog.UniversalApiTool
               || OmniToolRegistry.Shared.IsServiceTool(toolName));

    /// <summary>Whether a call can be started early and run alongside the turn's other reads. Unlike
    /// the fixed native tools, a service tool's safety depends on its 'op', so the arguments have to be
    /// read. Anything ambiguous is treated as unsafe.</summary>
    public static bool IsParallelSafe(string? toolName, string? argumentsJson)
    {
        if (string.IsNullOrEmpty(toolName)) return false;

        // A route call is only safe to pre-launch when it is a GET.
        if (toolName == OmniToolCatalog.UniversalApiTool)
            return string.Equals(ReadString(argumentsJson, "method"), "GET", StringComparison.OrdinalIgnoreCase);

        if (toolName == OmniToolCatalog.UniversalServiceTool)
        {
            var op = ReadString(argumentsJson, "op");
            // list/describe only read the catalogue. 'call' depends on the operation named inside it.
            if (string.Equals(op, "list", StringComparison.OrdinalIgnoreCase)
                || string.Equals(op, "describe", StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.Equals(op, "call", StringComparison.OrdinalIgnoreCase)) return false;

            var operation = OmniToolRegistry.Shared.FindOnService(
                ReadString(argumentsJson, "service") ?? "", ReadString(argumentsJson, "method") ?? "");
            return operation is { Mutating: false };
        }

        var tool = OmniToolRegistry.Shared.GetTool(toolName);
        if (tool == null) return false;

        var opName = ReadString(argumentsJson, "op");
        if (string.IsNullOrEmpty(opName)) return false;

        var resolved = tool.Operations.FirstOrDefault(o =>
            string.Equals(o.Op, opName, StringComparison.OrdinalIgnoreCase));
        return resolved is { Mutating: false };
    }

    private static string? ReadString(string? json, string property)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JObject.Parse(json).TryGetValue(property, StringComparison.OrdinalIgnoreCase, out var token)
                   && token.Type == JTokenType.String
                ? token.Value<string>()
                : null;
        }
        catch { return null; }
    }

    // -- Dispatch --

    public async Task<string> DispatchAsync(string toolName, string? argumentsJson, CancellationToken ct)
    {
        if (!await IsEnabledAsync())
            return "Service tools are switched off (KliveAgent_ServiceToolsEnabled). Ask Klives to turn them "
                 + "back on, or fall back to execute_csharp for this.";

        if (toolName == OmniToolCatalog.UniversalApiTool)
        {
            if (!await agent.GetBoolOmniSetting("KliveAgent_OmniApiEnabled", defaultValue: true))
                return "omni_api is switched off (KliveAgent_OmniApiEnabled). Use omniservice, or a dedicated tool.";
            return (await Api.ExecuteAsync(argumentsJson, ct)).Text;
        }

        Invoker.AllowUnverified = await agent.GetBoolOmniSetting("KliveAgent_ServiceToolsAllowUnverified", defaultValue: true);

        if (toolName == OmniToolCatalog.UniversalServiceTool)
            return (await DispatchUniversalAsync(argumentsJson, ct)).Text;

        return (await Invoker.ExecuteToolAsync(toolName, argumentsJson, ct)).Text;
    }

    private async Task<OmniToolInvocation> DispatchUniversalAsync(string? argumentsJson, CancellationToken ct)
    {
        JObject args;
        try
        {
            args = string.IsNullOrWhiteSpace(argumentsJson) ? new JObject() : JObject.Parse(argumentsJson);
        }
        catch (Exception ex)
        {
            return new OmniToolInvocation(false, new ToolArgumentError(ToolArgumentContract.InvalidJson, "$",
                $"Arguments were not valid JSON: {ex.Message}").ToToolResult());
        }

        var op = (args["op"]?.Value<string>() ?? "").Trim().ToLowerInvariant();
        switch (op)
        {
            case "list":
                return new OmniToolInvocation(true, OmniToolCatalog.RenderServiceList(Registry));

            case "describe":
            {
                var key = args["service"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(key))
                    return new OmniToolInvocation(false,
                        "'service' is required for op=describe. Call op=\"list\" to see the service keys.");

                var service = Registry.GetService(key!);
                return service == null
                    ? new OmniToolInvocation(false,
                        $"No service named '{key}'. Call op=\"list\" to see the service keys.")
                    : new OmniToolInvocation(true, OmniToolCatalog.RenderServiceDescription(service));
            }

            case "call":
                return await Invoker.ExecuteServiceCallAsync(
                    args["service"]?.Value<string>(), args["method"]?.Value<string>(), args["args"], ct);

            default:
                return new OmniToolInvocation(false, new ToolArgumentError(
                    ToolArgumentContract.EnumMismatch, "$.op",
                    string.IsNullOrEmpty(op) ? "'op' is required." : $"'{op}' is not an operation of omniservice.",
                    "Use one of: list | describe | call.").ToToolResult());
        }
    }

    // -- Offered surface --

    /// <summary>The dedicated service tools plus the two universals, for this run's settings.</summary>
    public async Task<List<HFWrapper.HFTool>> BuildToolDefinitionsAsync()
    {
        if (!await IsEnabledAsync()) return new List<HFWrapper.HFTool>();

        var (pinned, offerAll) = await ReadPinningAsync();
        var tools = OmniToolCatalog.BuildServiceTools(Registry, pinned, offerAll);
        tools.AddRange(OmniToolCatalog.BuildUniversalTools(
            await agent.GetBoolOmniSetting("KliveAgent_OmniApiEnabled", defaultValue: true)));
        return tools;
    }

    /// <summary>The [Service Surface] prompt block. Stable across runs, so it belongs above the cache
    /// breakpoint - the model should never have to spend a call discovering that a service exists.</summary>
    public async Task<string> BuildPromptBlockAsync()
    {
        if (!await IsEnabledAsync()) return "";
        var (pinned, offerAll) = await ReadPinningAsync();
        return OmniToolCatalog.BuildServiceSurfaceBlock(Registry, pinned, offerAll);
    }

    private async Task<(string[] pinned, bool offerAll)> ReadPinningAsync()
    {
        var raw = await agent.GetStringOmniSetting("KliveAgent_PinnedServiceTools", defaultValue: DefaultPinnedTools)
                  ?? DefaultPinnedTools;
        var pinned = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var offerAll = await agent.GetBoolOmniSetting("KliveAgent_ServiceToolsOfferAll", defaultValue: false);
        return (pinned, offerAll);
    }

    private Task<bool> IsEnabledAsync()
        => agent.GetBoolOmniSetting("KliveAgent_ServiceToolsEnabled", defaultValue: true);

    // -- Approval --

    /// <summary>Blocks an irreversible operation on Klives, reusing the exact approval path the
    /// computer-use tools use (website + Discord). Nothing new is built here on purpose: one approval
    /// channel means one place for Klives to look.</summary>
    private async Task<bool> RequestApprovalAsync(OmniOperation operation, string summary, CancellationToken ct)
    {
        var host = agent.GetActiveServices().OfType<HostControlManager>().FirstOrDefault(s => s.IsServiceActive());
        if (host == null)
        {
            await agent.ServiceLog($"[ServiceTools] refused {operation} - no approval channel available.");
            return false;
        }

        var payload = new JObject { ["summary"] = summary }.ToString(Newtonsoft.Json.Formatting.None);
        var result = await host.ExecuteToolAsync("computer_confirm_action", payload, ct);
        return result.Success;
    }
}
