using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.Projects;

/// <summary>
/// Project agents may use the host shell for real work, but they must never "repair" the
/// infrastructure every project's computer runs on. On 2026-09-18 an agent's self-serve fix
/// (wsl --update, a standalone WSL MSI unsupported by the host's Windows build, and a Docker
/// Desktop reset) left Docker unable to start for every project for five days. Docker Desktop,
/// WSL, Hyper-V, Windows features and reboots belong to the supervised recovery in
/// <see cref="Containers.ContainerDependencyBootstrapper"/>; read-only diagnostics stay allowed.
/// </summary>
public static class ProjectHostInfrastructurePolicy
{
    private static readonly HashSet<string> HostScriptTools = new(StringComparer.Ordinal)
    {
        "run_script", "execute_csharp", "run_bash", "run_powershell", "create_stimulus_hook",
    };

    // Each pattern matches one mutating operation within a single command line. Reads such as
    // `docker ps`, `docker info`, `wsl --status` and `wsl -l -v` are deliberately not matched.
    private static readonly (Regex Pattern, string What)[] Rules =
    {
        (R(@"\bwsl(?:\.exe)?\b[^\r\n;|&]*?(?:--update|--shutdown|--unregister|--install|--uninstall|--import|--export|--set-version|--set-default-version|--terminate|--manage|--mount|--unmount|\s-t\s)"), "change WSL"),
        (R(@"\bmsiexec(?:\.exe)?\b"), "install or remove Windows packages"),
        (R(@"\bwinget(?:\.exe)?\b[^\r\n;|&]*\b(?:install|upgrade|uninstall|remove)\b[^\r\n;|&]*(?:docker|wsl|subsystem|hyper-?v)"), "install or remove Docker/WSL"),
        (R(@"(?:--reset|--factory-reset|--uninstall|\buninstall\b)[^\r\n]*docker|docker[^\r\n]*(?:--reset|--factory-reset|--uninstall)"), "reset or uninstall Docker Desktop"),
        (R(@"\b(?:Stop-Process|taskkill(?:\.exe)?|kill|pkill|Stop-Service|Restart-Service|Set-Service|Suspend-Service)\b[^\r\n;|&]*(?:docker|com\.docker|vmmem|vmcompute|LxssManager|WSLService|\bwsl\b|\bhns\b|vmms)"), "stop or restart Docker/WSL/Hyper-V processes or services"),
        (R(@"\bsc(?:\.exe)?\s+(?:stop|config|delete|failure)\b[^\r\n;|&]*(?:docker|LxssManager|WSLService|vmcompute|hns|vmms)"), "reconfigure Docker/WSL/Hyper-V services"),
        (R(@"\b(?:Enable|Disable)-WindowsOptionalFeature\b|\b(?:Add|Remove)-WindowsCapability\b|\bdism(?:\.exe)?\b[^\r\n]*/(?:enable|disable)-feature"), "change Windows features"),
        (R(@"\b(?:Restart|Stop)-Computer\b|\bshutdown(?:\.exe)?\s+[/-][rsgpf]\b|\bbcdedit\b"), "restart or shut down the host"),
        (R(@"\b(?:New|Remove|Stop|Start|Restart|Set|Suspend|Checkpoint)-VM(?:Switch|Network\w*|Memory|Processor|HardDiskDrive)?\b|\b(?:New|Remove)-NetNat\b"), "change Hyper-V machines or networks"),
        (R(@"\bdocker(?:\.exe)?\s+(?:system\s+prune|volume\s+(?:rm|prune)|image\s+prune|container\s+(?:rm|prune|kill|stop|restart|update)|network\s+(?:rm|prune)|rm|rmi|kill|stop|restart|update|builder\s+prune)\b"), "stop, remove or prune Docker objects shared by every project"),
        (R(@"(?:Set-Content|Add-Content|Out-File|Copy-Item|Move-Item|Remove-Item|>\s*)[^\r\n]*(?:\.wslconfig|Docker\\settings\.json|Docker/settings\.json)"), "edit WSL or Docker Desktop configuration"),
    };

    private static Regex R(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string? FindViolation(string toolName, string? argumentsJson)
    {
        if (!HostScriptTools.Contains(toolName)) return null;
        string script = ExtractScript(toolName, argumentsJson);
        if (string.IsNullOrWhiteSpace(script)) return null;
        foreach (var (pattern, what) in Rules)
        {
            var match = pattern.Match(script);
            if (!match.Success) continue;
            string snippet = match.Value.Length > 120 ? match.Value[..120] + "…" : match.Value;
            return "HOST_INFRASTRUCTURE_PROTECTED: project agents may not " + what + " on the Omnipotent host " +
                   $"(matched \"{snippet.Trim()}\"). Docker, WSL, Hyper-V, Windows features and reboots are shared by every " +
                   "project and are owned by Omnipotent's supervised desktop recovery. An agent-run repair on 2026-09-18 " +
                   "(wsl --update, a WSL package the host could not run, and a Docker Desktop reset) took every project's " +
                   "computer offline for five days. Read-only diagnostics (docker ps/info/logs, wsl --status, wsl -l -v, " +
                   "Get-Process) remain allowed. Use ensure_desktop_ready to run the supervised recovery, and report " +
                   "anything it cannot fix to Klives.";
        }
        return null;
    }

    private static string ExtractScript(string toolName, string? argumentsJson)
    {
        try
        {
            var args = JObject.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (toolName == "create_stimulus_hook")
                return string.Equals((string?)args["sourceKind"], "script", StringComparison.OrdinalIgnoreCase)
                    ? (string?)args["sourceSpec"]?["script"] ?? ""
                    : "";
            string canonical = toolName is "run_script" or "execute_csharp" ? "code" : "script";
            return (string?)args[canonical] ?? (string?)args["command"] ?? (string?)args["code"] ?? "";
        }
        catch { return argumentsJson ?? ""; }
    }
}
