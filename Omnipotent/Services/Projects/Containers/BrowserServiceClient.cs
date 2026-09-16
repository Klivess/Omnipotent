using System.Net.Http.Json;
using System.Text.Json;

namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// Talks to a desktop container's in-container browser helper over its published loopback port
    /// (see browser-service.py), instead of reaching the helper through <c>docker exec</c>.
    ///
    /// The point is not speed, though it is faster by a whole exec round trip. It is that the Docker
    /// daemon stops being on the path of an ordinary browser action. An exec attach is a hijacked
    /// stream that ignores its cancellation token, so when the host got busy on 2026-09-16 a browser
    /// action sat for 26 minutes — on three projects at once, because they share the one daemon.
    /// An HTTP request to a running container has none of that: the container serves it whatever the
    /// daemon is doing, and <see cref="HttpClient"/>'s timeout is real.
    ///
    /// Every method answers null rather than throwing when the service cannot be used — no port on
    /// a pre-existing container, the service still starting, a connection refused. Null means "fall
    /// back to exec", so a desktop built before this existed keeps working unchanged.
    /// </summary>
    internal sealed class BrowserServiceClient
    {
        // One pooled client for the whole fleet. Per-request timeouts come from the CancellationToken;
        // the handler-level timeout is the outer backstop for a connection that hangs mid-body.
        private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

        /// <summary>How long to wait on a service that should answer instantly before deciding it is
        /// not there. Short: the fallback costs an exec, and hesitating here is the cost we are
        /// trying to remove.</summary>
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

        private readonly int hostPort;
        private readonly string host;

        internal BrowserServiceClient(string host, int hostPort)
        {
            this.host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host;
            this.hostPort = hostPort;
        }

        internal bool Available => hostPort > 0;

        /// <summary>
        /// Whether Chromium's debugger is answering inside the container.
        ///
        /// This replaces the <c>docker exec</c> that ran before EVERY structured browser action to
        /// make sure the browser was up. In steady state the answer is always yes, so that check was
        /// the single most wasteful call on the path — and, being an exec, one of the two that could
        /// hang. Null when the service itself cannot be reached, which is not the same as "the
        /// browser is down" and must not be treated as it.
        /// </summary>
        internal async Task<bool?> BrowserUpAsync(CancellationToken ct)
        {
            if (!Available) return null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ProbeTimeout);
                using var response = await Http.GetAsync($"http://{host}:{hostPort}/health", timeout.Token);
                if (!response.IsSuccessStatusCode) return null;
                var body = await response.Content.ReadFromJsonAsync<HealthBody>(cancellationToken: timeout.Token);
                return body?.BrowserUp;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return null; }
        }

        /// <summary>
        /// Runs one helper mode, or returns null if the service could not be reached and the caller
        /// should fall back to <c>docker exec</c>.
        /// </summary>
        internal async Task<(bool Ok, string Stdout, string Error)?> RunAsync(
            string mode, string? payload, IReadOnlyList<string>? args, int timeoutSeconds, CancellationToken ct)
        {
            if (!Available) return null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 900)));
                using var response = await Http.PostAsJsonAsync($"http://{host}:{hostPort}/run",
                    new { mode, payload = payload ?? "", args = args ?? Array.Empty<string>() }, timeout.Token);

                // A 4xx is the service rejecting the request, which is a harness bug rather than a
                // transport problem: repeating it through exec would only hide it.
                if ((int)response.StatusCode is >= 400 and < 500)
                    return (false, "", $"The container browser service rejected mode '{mode}' ({(int)response.StatusCode}).");
                if (!response.IsSuccessStatusCode) return null;

                var body = await response.Content.ReadFromJsonAsync<RunBody>(cancellationToken: timeout.Token);
                if (body == null) return null;
                string stdout = (body.Stdout ?? "").Trim();
                if (body.ExitCode == 0 && stdout.Length > 0) return (true, stdout, "");
                return (false, stdout, string.Join(" ", new[] { body.Stderr, body.Stdout }
                    .Where(x => !string.IsNullOrWhiteSpace(x))).Trim());
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch
            {
                // Includes the deliberate case: the per-call deadline elapsed. Falling back to exec
                // would hand the same slow work to the slower path, but the caller cannot tell a
                // slow helper from an absent service, so exec stays the single recovery route.
                return null;
            }
        }

        private sealed class HealthBody
        {
            public bool Ok { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("browserUp")]
            public bool BrowserUp { get; set; }
        }

        private sealed class RunBody
        {
            [System.Text.Json.Serialization.JsonPropertyName("exitCode")]
            public int ExitCode { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("stdout")]
            public string? Stdout { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("stderr")]
            public string? Stderr { get; set; }
        }
    }
}
