using System.Net.Http.Headers;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.Projects;

public enum OpenRouterCreditStatus { Unknown, Available, Exhausted }

public sealed record OpenRouterCreditCheck(OpenRouterCreditStatus Status, double? RemainingUsd, string Detail);

/// <summary>Checks the authenticated OpenRouter key before a project launches a model turn. The
/// official /api/v1/key endpoint exposes a nullable limit_remaining value; null means the key has
/// no key-level spending limit, while a non-positive value predicts a payment-required response.</summary>
public sealed class OpenRouterCreditChecker
{
    private readonly Func<Task<string?>> tokenProvider;
    private readonly HttpClient http;
    private readonly Action<string> log;
    private readonly SemaphoreSlim gate = new(1, 1);
    private OpenRouterCreditCheck? cached;
    private DateTime cachedAtUtc;

    public OpenRouterCreditChecker(Func<Task<string?>> tokenProvider, Action<string> log, HttpClient? http = null)
    {
        this.tokenProvider = tokenProvider;
        this.log = log ?? (_ => { });
        this.http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<OpenRouterCreditCheck> CheckAsync(CancellationToken ct = default)
    {
        if (cached != null && DateTime.UtcNow - cachedAtUtc < TimeSpan.FromMinutes(1)) return cached;
        await gate.WaitAsync(ct);
        try
        {
            if (cached != null && DateTime.UtcNow - cachedAtUtc < TimeSpan.FromMinutes(1)) return cached;
            string? token = await tokenProvider();
            if (string.IsNullOrWhiteSpace(token))
                return Cache(new(OpenRouterCreditStatus.Unknown, null, "OpenRouter token is not configured."));

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/key");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                    return Cache(new(OpenRouterCreditStatus.Unknown, null,
                        $"OpenRouter credit preflight returned HTTP {(int)response.StatusCode}."));

                var json = JObject.Parse(await response.Content.ReadAsStringAsync(ct));
                JToken? remainingToken = json["data"]?["limit_remaining"];
                if (remainingToken == null || remainingToken.Type == JTokenType.Null)
                    return Cache(new(OpenRouterCreditStatus.Available, null,
                        "The OpenRouter key has no key-level spending limit."));

                double? remaining = remainingToken.Value<double?>();
                return Cache(remaining is <= 0
                    ? new(OpenRouterCreditStatus.Exhausted, remaining,
                        "The OpenRouter key has no remaining credits under its spending limit.")
                    : new(OpenRouterCreditStatus.Available, remaining,
                        $"The OpenRouter key reports ${remaining:0.####} remaining."));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log($"OpenRouterCreditChecker: preflight failed ({ex.Message}); allowing provider telemetry to decide.");
                return Cache(new(OpenRouterCreditStatus.Unknown, null, "OpenRouter credit preflight was unavailable."));
            }
        }
        finally { gate.Release(); }
    }

    private OpenRouterCreditCheck Cache(OpenRouterCreditCheck value)
    {
        cached = value;
        cachedAtUtc = DateTime.UtcNow;
        return value;
    }
}

internal sealed class OpenRouterCreditExhaustedException : Exception
{
    public OpenRouterCreditCheck Check { get; }
    public OpenRouterCreditExhaustedException(OpenRouterCreditCheck check) : base(check.Detail) => Check = check;
}
