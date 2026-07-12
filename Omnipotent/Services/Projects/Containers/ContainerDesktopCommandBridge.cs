namespace Omnipotent.Services.Projects.Containers
{
    public enum ContainerDesktopControlCommand { LaunchBrowser, LaunchTerminal, FocusBrowser, FocusTerminal }

    /// <summary>
    /// Narrow application-control bridge for an isolated XFCE desktop.  It deliberately uses the
    /// desktop's launcher and VNC input. This bridge accepts only fixed browser/terminal window
    /// operations; bounded container-local shell execution is a separate adapter capability, and
    /// neither path can execute a command on the Omnipotent host.
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
            if (!Apps.TryGetValue(key, out var app))
                throw new InvalidOperationException($"'{key}' is not an allowed desktop application. Allowed: browser, firefox, terminal.");

            // Browser URLs are the one argument form allowed by this bridge. They are validated
            // as a URI and passed as one launcher argument; terminal arguments are intentionally
            // rejected so this cannot become a hidden shell API.
            string command = app;
            if (!string.IsNullOrWhiteSpace(args))
            {
                if (app != "chromium" || !Uri.TryCreate(args, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    throw new InvalidOperationException("Only an absolute http(s) URL may be passed to the browser launcher.");
                command += " " + uri.AbsoluteUri;
            }

            if (dockerControlAsync != null)
            {
                await dockerControlAsync(app == "chromium" ? ContainerDesktopControlCommand.LaunchBrowser : ContainerDesktopControlCommand.LaunchTerminal,
                    app == "chromium" ? args : null, ct);
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
            if (target.Contains("firefox", StringComparison.OrdinalIgnoreCase) || target.Contains("browser", StringComparison.OrdinalIgnoreCase))
                return dockerControlAsync != null ? dockerControlAsync(ContainerDesktopControlCommand.FocusBrowser, null, ct) : LaunchAsync("browser", null, ct);
            if (target.Contains("terminal", StringComparison.OrdinalIgnoreCase) || target.Contains("xfce", StringComparison.OrdinalIgnoreCase))
                return dockerControlAsync != null ? dockerControlAsync(ContainerDesktopControlCommand.FocusTerminal, null, ct) : LaunchAsync("terminal", null, ct);
            throw new InvalidOperationException("Container focus supports browser or terminal only; use computer_launch_app for an allowed application.");
        }
    }
}
