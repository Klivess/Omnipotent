using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.Projects;

/// <summary>
/// Enforces the Projects product contract: an agent operates an external website through its
/// visible, persistent desktop. Shells remain first-class for installs, diagnostics, files and
/// software work, but cannot quietly replace the browser with Playwright/Selenium/headless/CDP
/// automation. The policy is intentionally semantic and narrow rather than banning scripts.
/// </summary>
public static class ProjectDesktopInteractionPolicy
{
    private static readonly HashSet<string> ScriptTools = new(StringComparer.Ordinal)
    {
        "run_script", "execute_csharp", "run_bash", "run_powershell", "computer_terminal",
        "create_stimulus_hook",
    };

    private static readonly string[] BrowserAutomationMarkers =
    {
        "from playwright", "import playwright", "require(\"playwright", "require('playwright",
        "@playwright/test", "from selenium", "import selenium", "org.openqa.selenium",
        "selenium.webdriver", "require(\"puppeteer", "require('puppeteer", "from puppeteer",
        "sync_playwright().start", "async with async_playwright", "await async_playwright().start",
        "playwright.chromium.launch", "playwright.firefox.launch", "p.chromium.launch", "p.firefox.launch",
        "puppeteer.launch", "webdriver.chrome", "webdriver.firefox", "new chromedriver", "new firefoxdriver",
        "input.dispatchmouseevent", "input.dispatchkeyevent", "--remote-debugging-port",
        "chrome-remote-interface", "chromeremoteinterface",
    };

    private static readonly string[] BrowserActionMarkers =
    {
        "page.goto(", "page.click(", "page.fill(", "page.type(", "locator(", "find_element(",
        "--headless",
    };

    private static readonly string[] HiddenNetworkMutationMarkers =
    {
        "requests.post(", "requests.put(", "requests.patch(", "requests.delete(",
        "httpx.post(", "httpx.put(", "httpx.patch(", "httpx.delete(",
        ".postasync(", ".putasync(", ".patchasync(", ".deleteasync(",
        "axios.post(", "axios.put(", "axios.patch(", "axios.delete(",
        "httpmethod.post", "httpmethod.put", "httpmethod.patch", "httpmethod.delete",
        "-method post", "-method put", "-method patch", "-method delete",
        "--request post", "--request put", "--request patch", "--request delete",
        "-x post", "-x put", "-x patch", "-x delete", "--data ", "--data=", " -d ",
        "--form ", "--post-data", "method: 'post'", "method: \"post\"",
        "method:'post'", "method:\"post\"", "method = 'post'", "method = \"post\"",
    };

    public static string? FindViolation(ProjectSettings settings, string toolName, string? argumentsJson,
        string? projectRoot = null)
    {
        string extracted = ExtractScript(toolName, argumentsJson);
        string raw = (argumentsJson ?? "") + "\n" + extracted;
        if (Regex.IsMatch(raw, @"(?i)(?:https?://)?(?:api\.)?mail\.tm(?:[/\s'""}]|$)"))
            return "DISPOSABLE_MAIL_PROHIBITED: mail.tm is not an account dependency for Projects. " +
                   "Create or reuse an @klive.dev mailbox with klivemail_create_mailbox, wait through the native KliveMail tools, and enter the code in the visible browser.";
        if (!settings.DesktopFirstWebsiteInteraction) return null;

        if (toolName == "http_request")
        {
            try
            {
                var request = JObject.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                string method = ((string?)request["method"] ?? "GET").Trim().ToUpperInvariant();
                string url = (string?)request["url"] ?? "";
                if (method is not ("GET" or "HEAD" or "OPTIONS") && IsPublicUrl(url))
                    return ViolationText("a direct mutating HTTP request to a public website");
            }
            catch { }
            return null;
        }

        if (!ScriptTools.Contains(toolName)) return null;

        string script = extracted;
        if (string.IsNullOrWhiteSpace(script)) return null;
        string material = script;
        if (!string.IsNullOrWhiteSpace(projectRoot))
            material += "\n" + ReadInvokedProjectScripts(script, projectRoot!);

        string lower = material.ToLowerInvariant();
        bool direct = BrowserAutomationMarkers.Any(lower.Contains)
            || BrowserActionMarkers.Any(lower.Contains)
               && (lower.Contains("browser", StringComparison.Ordinal)
                   || lower.Contains("chromium", StringComparison.Ordinal)
                   || lower.Contains("firefox", StringComparison.Ordinal)
                   || lower.Contains("webdriver", StringComparison.Ordinal)
                   || lower.Contains("playwright", StringComparison.Ordinal)
                   || lower.Contains("puppeteer", StringComparison.Ordinal)
                   || lower.Contains("page.", StringComparison.Ordinal));
        bool rawCdp = lower.Contains("9222", StringComparison.Ordinal) &&
            (lower.Contains("devtools", StringComparison.Ordinal) || lower.Contains("websocket", StringComparison.Ordinal)
             || lower.Contains("runtime.evaluate", StringComparison.Ordinal) || lower.Contains("page.navigate", StringComparison.Ordinal));
        bool syntheticDesktopInput = lower.Contains("xdotool", StringComparison.Ordinal) &&
            (lower.Contains(" click", StringComparison.Ordinal) || lower.Contains(" type", StringComparison.Ordinal)
             || lower.Contains(" key", StringComparison.Ordinal));
        bool scriptedBrowserLaunch = Regex.IsMatch(lower,
            @"(?im)(?:^|[\s;&|])(?:chromium(?:-browser)?|google-chrome|firefox(?:-esr)?)\s+[^\r\n]*(?:https?://|--remote-debugging)");
        bool hiddenNetworkMutation = HiddenNetworkMutationMarkers.Any(lower.Contains)
            && Regex.Matches(material, @"https?://[^\s'""<>]+", RegexOptions.IgnoreCase)
                .Cast<Match>().Any(m => IsPublicUrl(m.Value));
        if (!direct && !rawCdp && !syntheticDesktopInput && !scriptedBrowserLaunch && !hiddenNetworkMutation) return null;

        return ViolationText(hiddenNetworkMutation
            ? "hidden mutation of a public website over HTTP"
            : "hidden browser automation");
    }

    private static string ViolationText(string reason) =>
        $"DESKTOP_INTERACTION_REQUIRED: the browser-first policy blocked {reason}. " +
               "Operate the live account through ensure_desktop_ready and the visible computer_open_browser / " +
               "computer_navigate / computer_screenshot or computer_browser_inspect / computer_click(_text) / " +
               "computer_type / computer_wait tools. Shells are for installs, files, diagnostics and CLI work, " +
               "not Playwright, Selenium, headless Chromium, raw CDP, or scripted mouse/keyboard control of an " +
               "external website. Retrieve email codes with klivemail_wait_for_code, then type them into the GUI.";

    private static string ExtractScript(string toolName, string? argumentsJson)
    {
        try
        {
            var args = JObject.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (toolName == "create_stimulus_hook")
                return string.Equals((string?)args["sourceKind"], "script", StringComparison.OrdinalIgnoreCase)
                    ? (string?)args["sourceSpec"]?["script"] ?? ""
                    : "";
            string canonical = toolName == "computer_terminal" ? "command" : toolName is "run_script" or "execute_csharp" ? "code" : "script";
            return (string?)args[canonical] ?? (string?)args["command"] ?? (string?)args["code"] ?? "";
        }
        catch { return ""; }
    }

    private static string ReadInvokedProjectScripts(string command, string projectRoot)
    {
        var result = new List<string>();
        // Covers python file.py, python3 /project/file.py and a quoted equivalent. It intentionally
        // does not try to implement a shell parser; ambiguous paths simply receive normal policy.
        foreach (Match match in Regex.Matches(command,
                     @"(?ix)\b(?:python(?:3(?:\.\d+)?)?|/\S*/python(?:3(?:\.\d+)?)?|node|bash|sh|pwsh|powershell)\s+(?:-[A-Za-z]+\s+)*(?:['""])?(?<path>[^'""\s;|&]+\.(?:py|js|mjs|cjs|ps1|sh))"))
        {
            string relative = match.Groups["path"].Value.Replace('\\', '/');
            if (relative.StartsWith("/project/", StringComparison.Ordinal)) relative = relative[9..];
            else if (relative.StartsWith("/", StringComparison.Ordinal)) continue;
            try
            {
                string root = Path.GetFullPath(projectRoot);
                string full = Path.GetFullPath(Path.Combine(root, relative));
                string rel = Path.GetRelativePath(root, full);
                if (Path.IsPathRooted(rel) || rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    continue;
                if (File.Exists(full) && new FileInfo(full).Length <= 2 * 1024 * 1024)
                    result.Add(File.ReadAllText(full));
            }
            catch { }
        }
        return string.Join("\n", result);
    }

    private static bool IsPublicUrl(string value)
    {
        string candidate = value.TrimEnd('.', ',', ')', ']', '}');
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")) return false;
        if (uri.IsLoopback) return false;
        string host = uri.Host.TrimEnd('.');
        if (host.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return false;
        if (!System.Net.IPAddress.TryParse(host, out var ip)) return true;
        byte[] bytes = ip.GetAddressBytes();
        if (bytes.Length == 4)
            return !(bytes[0] == 10 || bytes[0] == 127 || bytes[0] == 169 && bytes[1] == 254
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && bytes[1] == 168);
        return !(ip.Equals(System.Net.IPAddress.IPv6Loopback) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal);
    }
}
