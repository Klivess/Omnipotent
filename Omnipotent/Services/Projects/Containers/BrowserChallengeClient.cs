using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Omnipotent.Services.Projects.Containers;

/// <summary>One bounded solve across configured providers. Never repeats createTask against the
/// same provider in a call: an ambiguous network failure may already have created a paid task.</summary>
internal sealed class BrowserChallengeClient
{
    internal sealed record Credential(string Service, string ApiKey);
    internal sealed record Result(string? Token, string? Service, string? Error);
    private readonly HttpClient http;
    private readonly TimeSpan pollInterval;
    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, string Error)> unavailable = new();

    internal BrowserChallengeClient(HttpClient http, TimeSpan? pollInterval = null)
    {
        this.http = http;
        this.pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
    }

    internal async Task<Result> SolveAsync(IReadOnlyList<Credential> credentials,
        BrowserChallengeSolver.ChallengeWidget widget, string url, TimeSpan timeout, CancellationToken ct)
    {
        var candidates = credentials.Where(c => BrowserChallengeSolver.TaskTypeFor(c.Service, widget.Provider) != null)
            .DistinctBy(c => c.Service, StringComparer.OrdinalIgnoreCase).ToArray();
        var errors = new List<string>();
        var elapsed = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        // Keep only a hash of the credential in process-wide health state.
        foreach (var entry in unavailable.Where(x => x.Value.Until <= DateTimeOffset.UtcNow))
            unavailable.TryRemove(entry.Key, out _);

        for (int i = 0; i < candidates.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var credential = candidates[i];
            string healthKey = credential.Service + ":" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(credential.ApiKey)));
            if (unavailable.TryGetValue(healthKey, out var health) && health.Until > DateTimeOffset.UtcNow)
            {
                errors.Add(credential.Service + ": " + health.Error + " (temporarily cooling down)");
                continue;
            }
            var remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            // Reserve time for fallback instead of letting the first provider consume every second.
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            attempt.CancelAfter(remaining / (candidates.Length - i));
            try
            {
                string body = BrowserChallengeSolver.BuildCreateTaskRequest(
                    credential.Service, credential.ApiKey, widget, url);
                string created = await PostAsync(credential.Service, "createTask", body, attempt.Token);
                // Some providers finish within createTask and return the solution immediately.
                var immediate = BrowserChallengeSolver.ReadResult(created);
                if (immediate.Ready) return new(immediate.Token, credential.Service, null);
                string? id = BrowserChallengeSolver.ReadTaskId(created, out var error);
                if (id == null) throw new InvalidOperationException(error ?? "Task creation failed.");
                string query = BrowserChallengeSolver.BuildResultRequest(credential.ApiKey, id);
                int transientPollFailures = 0;
                while (true)
                {
                    await Task.Delay(pollInterval, attempt.Token);
                    string response;
                    try
                    {
                        response = await PostAsync(credential.Service, "getTaskResult", query, attempt.Token);
                        transientPollFailures = 0;
                    }
                    catch (HttpRequestException ex) when (++transientPollFailures <= 2
                        && (ex.StatusCode == null || (int)ex.StatusCode == 429 || (int)ex.StatusCode >= 500))
                    {
                        // Retry the existing task, never buy another token for a polling outage.
                        continue;
                    }
                    var outcome = BrowserChallengeSolver.ReadResult(response);
                    if (outcome.Error != null) throw new InvalidOperationException(outcome.Error);
                    if (outcome.Ready) return new(outcome.Token, credential.Service, null);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                errors.Add(credential.Service + ": solve timed out");
            }
            catch (HttpRequestException ex)
            {
                errors.Add(credential.Service + ": " + (ex.StatusCode is { } status
                    ? "HTTP " + (int)status : "network request failed"));
            }
            catch (InvalidOperationException ex)
            {
                string error = ex.Message;
                foreach (var key in credentials.Where(x => !string.IsNullOrEmpty(x.ApiKey)))
                    error = error.Replace(key.ApiKey, "[redacted]", StringComparison.Ordinal);
                errors.Add(credential.Service + ": " + error);
                if (error.Contains("ZERO_BALANCE", StringComparison.Ordinal)
                    || error.Contains("KEY_DOES_NOT_EXIST", StringComparison.Ordinal)
                    || error.Contains("KEY_DENIED_ACCESS", StringComparison.Ordinal)
                    || error.Contains("WRONG_USER_KEY", StringComparison.Ordinal))
                    unavailable[healthKey] = (DateTimeOffset.UtcNow.AddMinutes(2), error);
            }
        }
        ct.ThrowIfCancellationRequested();
        return new(null, null, (errors.Count == 0 ? "No compatible funded solver is available." : string.Join("; ", errors))
            + " Use the site's API or another supported route; if this exact page is essential, use request_human "
            + "with this diagnosis and resume the same desktop afterwards. Do not repeatedly buy tokens or register placeholder keys.");
    }

    private async Task<string> PostAsync(string service, string path, string body, CancellationToken ct)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(BrowserChallengeSolver.EndpointFor(service, path), content, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }
}
