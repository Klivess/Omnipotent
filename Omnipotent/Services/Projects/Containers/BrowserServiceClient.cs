using System.Collections.Concurrent;
using System.Net.Http.Headers;
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

        /// <summary>
        /// How long a (service, mode) pair that rejected a well-formed request is skipped in favour of
        /// <c>docker exec</c>. Long enough that a broken service is not re-asked on every action, short
        /// enough that a recreated container is picked up again within one task.
        /// </summary>
        internal static readonly TimeSpan RejectionBackoff = TimeSpan.FromMinutes(10);

        /// <summary>Endpoint+mode pairs whose service answered 4xx, and until when to skip them.</summary>
        private static readonly ConcurrentDictionary<string, DateTime> Rejected = new(StringComparer.Ordinal);

        /// <summary>
        /// Where a rejection is reported. A 4xx means the harness and the in-container service
        /// disagree about the protocol — a bug worth a log line, never a reason to fail the agent.
        /// </summary>
        internal static Action<string>? Diagnostics { get; set; }

        private readonly int hostPort;
        private readonly string host;

        internal BrowserServiceClient(string host, int hostPort)
        {
            this.host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host;
            this.hostPort = hostPort;
        }

        internal bool Available => hostPort > 0;

        private string Endpoint => $"{host}:{hostPort}";

        /// <summary>
        /// The request body, serialised up front so it carries a Content-Length.
        ///
        /// <c>PostAsJsonAsync</c> streams its body with chunked transfer encoding and no length, and
        /// browser-service.py — like any <c>BaseHTTPRequestHandler</c> — reads exactly Content-Length
        /// bytes. It therefore answered every request with 400 "request body must be 1..4MiB", so from
        /// the day the service shipped every structured browser action on a desktop that published it
        /// failed outright. That is the "browser-service ops returning 400 while CDP worked" stall in
        /// KliveAgent's October 2026 computer-use report.
        /// </summary>
        internal static ByteArrayContent BuildRunContent(string mode, string? payload, IReadOnlyList<string>? args)
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(
                new { mode, payload = payload ?? "", args = args ?? Array.Empty<string>() });
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            content.Headers.ContentLength = body.Length;
            return content;
        }

        internal static void ResetRejectionsForTests() => Rejected.Clear();

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
            string rejectionKey = Endpoint + "|" + mode;
            if (Rejected.TryGetValue(rejectionKey, out var skipUntil))
            {
                if (DateTime.UtcNow < skipUntil) return null;
                Rejected.TryRemove(rejectionKey, out _);
            }
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 900)));
                using var response = await Http.PostAsync($"http://{host}:{hostPort}/run",
                    BuildRunContent(mode, payload, args), timeout.Token);

                // A 4xx is the service disagreeing with the harness about the protocol — an older
                // service that predates a mode, or a request it cannot parse. Treating that as the
                // action failing is what turned one framing bug into a fleet-wide outage of every
                // structured browser action. Report it, stop asking that service for that mode for a
                // while, and let the caller take the exec route, which runs the same helper.
                if ((int)response.StatusCode is >= 400 and < 500)
                {
                    string detail = "";
                    try { detail = (await response.Content.ReadAsStringAsync(timeout.Token)).Trim(); } catch { }
                    Rejected[rejectionKey] = DateTime.UtcNow + RejectionBackoff;
                    try
                    {
                        Diagnostics?.Invoke($"Container browser service {Endpoint} rejected mode '{mode}' "
                            + $"({(int)response.StatusCode} {(detail.Length > 200 ? detail[..200] : detail)}); "
                            + $"using docker exec for that mode for {RejectionBackoff.TotalMinutes:0} minutes.");
                    }
                    catch { }
                    return null;
                }
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
