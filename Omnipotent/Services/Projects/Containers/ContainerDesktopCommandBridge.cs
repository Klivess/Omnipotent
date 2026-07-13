namespace Omnipotent.Services.Projects.Containers
{
    public enum ContainerDesktopControlCommand
    {
        LaunchBrowser,
        LaunchTerminal,
        LaunchApplication,
        FocusBrowser,
        FocusTerminal,
        FocusWindow,
    }

    /// <summary>
    /// Narrow application-control bridge for an isolated XFCE desktop.  It deliberately uses the
    /// desktop's launcher and VNC input. Arbitrary installed GUI applications are allowed because
    /// this is the agent's isolated computer; executable and arguments are passed as Docker argv,
    /// never interpolated into a host shell.
    /// </summary>
    public sealed class ContainerDesktopCommandBridge
    {
        private readonly VncTransport transport;
        private readonly Func<ContainerDesktopControlCommand, string?, CancellationToken, Task>? dockerControlAsync;

        private static readonly Dictionary<string, string> Apps = new(StringComparer.OrdinalIgnoreCase)
        {
            ["browser"] = "chromium",
            ["chromium"] = "chromium",
            ["firefox"] = "chromium",
            ["firefox-esr"] = "chromium",
            ["terminal"] = "xfce4-terminal",
            ["xfce4-terminal"] = "xfce4-terminal",
        };

        public ContainerDesktopCommandBridge(VncTransport transport,
            Func<ContainerDesktopControlCommand, string?, CancellationToken, Task>? dockerControlAsync = null)
        {
            this.transport = transport;
            this.dockerControlAsync = dockerControlAsync;
        }

        public async Task LaunchAsync(string? requestedApp, string? args, CancellationToken ct)
        {
            string key = (requestedApp ?? "browser").Trim();
            string app = Apps.TryGetValue(key, out var alias) ? alias : key;
            if (!IsSafeExecutable(app))
                throw new InvalidOperationException("Application must be an installed executable name or absolute container path without shell operators.");

            // Browser URLs are the one argument form allowed by this bridge. They are validated
            // as a URI and passed as one launcher argument; terminal arguments are intentionally
            // rejected so this cannot become a hidden shell API.
            string command = app;
            if (!string.IsNullOrWhiteSpace(args))
            {
                if (app == "chromium")
                {
                    if (!Uri.TryCreate(args, UriKind.Absolute, out var uri) ||
                        (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                        throw new InvalidOperationException("Only an absolute http(s) URL may be passed to the browser launcher.");
                    command += " " + uri.AbsoluteUri;
                }
            }

            if (dockerControlAsync != null)
            {
                var control = app == "chromium" ? ContainerDesktopControlCommand.LaunchBrowser
                    : app == "xfce4-terminal" ? ContainerDesktopControlCommand.LaunchTerminal
                    : ContainerDesktopControlCommand.LaunchApplication;
                string? payload = control == ContainerDesktopControlCommand.LaunchApplication
                    ? System.Text.Json.JsonSerializer.Serialize(new { executable = app, arguments = SplitArguments(args) })
                    : app == "chromium" ? args : null;
                await dockerControlAsync(control, payload, ct);
                await Task.Delay(500, ct);
                return;
            }

            await transport.KeyChordAsync("alt+f2", ct: ct);
            await Task.Delay(100, ct);
            await transport.TypeTextAsync(command, ct: ct);
            await transport.KeyChordAsync("enter", ct: ct);
            await Task.Delay(700, ct);
        }

        public async Task NavigateAsync(string url, CancellationToken ct)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException("computer_navigate requires an absolute http(s) URL.");
            // Pass the validated URL as one Docker-exec argv value. This focuses/opens Firefox in
            // one operation and avoids a fragile launch -> focus -> address-bar typing sequence.
            await LaunchAsync("browser", uri.AbsoluteUri, ct);
        }

        public Task FocusAsync(string? titleContains, string? processName, CancellationToken ct)
        {
            string target = (processName ?? titleContains ?? "").Trim();
            if (target.Contains("firefox", StringComparison.OrdinalIgnoreCase)
                || target.Contains("browser", StringComparison.OrdinalIgnoreCase)
                || target.Contains("chromium", StringComparison.OrdinalIgnoreCase)
                || target.Contains("chrome", StringComparison.OrdinalIgnoreCase))
                return dockerControlAsync != null ? dockerControlAsync(ContainerDesktopControlCommand.FocusBrowser, null, ct) : LaunchAsync("browser", null, ct);
            if (target.Contains("terminal", StringComparison.OrdinalIgnoreCase) || target.Contains("xfce", StringComparison.OrdinalIgnoreCase))
                return dockerControlAsync != null ? dockerControlAsync(ContainerDesktopControlCommand.FocusTerminal, null, ct) : LaunchAsync("terminal", null, ct);
            if (target.Length == 0) throw new InvalidOperationException("Provide titleContains or processName.");
            if (target.Length > 256 || target.Any(char.IsControl)) throw new InvalidOperationException("Window focus target is invalid.");
            if (dockerControlAsync == null)
                throw new InvalidOperationException("General window focus requires the isolated desktop-control bridge.");
            return dockerControlAsync(ContainerDesktopControlCommand.FocusWindow,
                System.Text.Json.JsonSerializer.Serialize(new { titleContains, processName }), ct);
        }

        internal static bool IsSafeExecutable(string value) => value.Length is > 0 and <= 512
            && !value.Any(char.IsControl)
            && value.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '+' or '.' or '/')
            && (!value.Contains('/') || value.StartsWith('/'))
            && value is not "/" && !value.EndsWith('/') && !value.Contains("//")
            && !value.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or "..");

        internal static string[] SplitArguments(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();
            var result = new List<string>();
            var current = new System.Text.StringBuilder();
            char quote = '\0';
            bool escape = false;
            foreach (char c in value)
            {
                if (escape) { current.Append(c); escape = false; continue; }
                if (c == '\\' && quote != '\'') { escape = true; continue; }
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0'; else current.Append(c);
                    continue;
                }
                if (c is '\'' or '"') { quote = c; continue; }
                if (char.IsWhiteSpace(c))
                {
                    if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                    continue;
                }
                current.Append(c);
            }
            if (escape || quote != '\0') throw new InvalidOperationException("Application arguments contain an unmatched quote or escape.");
            if (current.Length > 0) result.Add(current.ToString());
            if (result.Count > 128 || result.Any(x => x.Length > 4096))
                throw new InvalidOperationException("Application argument list is too large.");
            return result.ToArray();
        }
    }
}
