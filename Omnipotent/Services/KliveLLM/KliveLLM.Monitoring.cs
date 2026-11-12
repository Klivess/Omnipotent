using Newtonsoft.Json.Linq;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveLLM.Telemetry;
using System.Net.Http.Headers;

namespace Omnipotent.Services.KliveLLM;

/// <summary>
/// Start-up of the routing store, the price book and the ledger, and the background loop that keeps
/// router health current: a light probe per router every minute, gauges every ten seconds, and
/// OpenRouter's catalogue prices every six hours.
/// </summary>
public partial class KliveLLM
{
    private static readonly HttpClient probeHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private CancellationTokenSource? monitorStop;
    private DateTime lastCataloguePricesUtc = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> lastProbeUtc = new(StringComparer.OrdinalIgnoreCase);

    internal bool LedgerStarted => Ledger?.Running == true;
    internal string? LedgerError { get; private set; }

    /// <summary>
    /// Loads routing, prices and the ledger, then registers the /klivellm routes. Runs beside the rest
    /// of start-up (it does not wait for the local model or llama binaries), and a failure in any part
    /// leaves model calls routing exactly as RemoteLLMProvider says.
    /// </summary>
    private async Task StartKliveLlmConsoleAsync()
    {
        string dir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveLLMDirectory);
        try
        {
            var routing = LlmRoutingStore.Load(dir, message => { try { _ = ServiceLog(message); } catch { } });
            routing.Changed += OnRoutingChanged;
            Routing = routing;
            Prices = LlmPriceBook.Open(dir, message => { try { _ = ServiceLog(message); } catch { } });
        }
        catch (Exception ex)
        {
            try { await ServiceLogError(ex, "KliveLLM: routing could not be loaded; every call follows RemoteLLMProvider."); } catch { }
        }

        try
        {
            string telemetryDir = Path.Combine(dir, "Telemetry");
            int callDays = Math.Clamp(await GetIntOmniSetting("KliveLLM_LedgerCallRetentionDays", 90), 7, 730);
            int contentDays = Math.Clamp(await GetIntOmniSetting("KliveLLM_KeptTextRetentionDays", 3), 1, 14);
            var content = new LlmContentStore(Path.Combine(telemetryDir, "klivellm-content.db")) { RetentionDays = contentDays };
            var ledger = new LlmTelemetry(new LlmTelemetryDb(Path.Combine(telemetryDir, "klivellm.db")),
                new LlmTelemetryOptions { CallRetentionDays = callDays }, () => Prices, content,
                message => { try { _ = ServiceLog(message); } catch { } })
            {
                BillingOf = id => RouterById(id)?.Billing,
            };
            ledger.Start();
            Ledger = ledger;
            Queries = new LlmQueryEngine(ledger.Db, () => Prices, BillingOf, RouterNameOf);
            await ServiceLog($"KliveLLM ledger started ({ledger.Db.FileBytes / 1_048_576.0:0.0} MB, routing revision {Routing.Revision}).");
        }
        catch (Exception ex)
        {
            LedgerError = ex.Message;
            try { await ServiceLogError(ex, "KliveLLM: the call ledger could not start; calls still run, unrecorded."); } catch { }
        }

        try { await KliveLLMRoutes.RegisterAsync(this); }
        catch (Exception ex) { try { await ServiceLogError(ex, "KliveLLM: page routes could not be registered."); } catch { } }

        monitorStop = new CancellationTokenSource();
        _ = Task.Run(() => MonitorLoopAsync(monitorStop.Token));
        if (Ledger != null)
            _ = Task.Run(async () =>
            {
                // After start-up has settled: the import reads every Projects journal once.
                try { await Task.Delay(TimeSpan.FromMinutes(2), monitorStop.Token); } catch { return; }
                try
                {
                    string journals = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.ProjectsDirectory), "AnalyticsUsage");
                    int imported = await LlmProjectsImport.RunAsync(Ledger, journals, message => { try { _ = ServiceLog(message); } catch { } }, monitorStop.Token);
                    if (imported > 0) await ServiceLog($"KliveLLM ledger: imported {imported:N0} Projects turns from before it started.");
                }
                catch (Exception ex) { try { await ServiceLogError(ex, "KliveLLM: Projects history import failed (it will be retried at the next start)."); } catch { } }
            });
    }

    private void OnRoutingChanged(LlmRoutingDocument doc, LlmRoutingHistoryEntry entry)
    {
        routerCache = null;
        Ledger?.AddMark("routing", $"Routing revision {entry.Revision}{(entry.By != null ? " by " + entry.By : "")}",
            entry.Note ?? string.Join("; ", entry.Changes.Take(6)));
        // A changed route deserves a fresh try of every router it names.
        Circuits.Reset();
        // A lane routed to the local model needs it loaded.
        bool wantsLocal = string.Equals(doc.Default.Router, LlmRouterIds.Local, StringComparison.OrdinalIgnoreCase)
            || doc.Services.Values.Concat(doc.Lanes.Values).Any(p => string.Equals(p.Router, LlmRouterIds.Local, StringComparison.OrdinalIgnoreCase));
        if (wantsLocal && !localModelReady)
            _ = Task.Run(async () =>
            {
                try { await SetupLocalLLM(); }
                catch (Exception ex) { try { await ServiceLogError(ex, "KliveLLM: the local model could not be loaded for the lanes routed to it."); } catch { } }
            });
    }

    // ── router health ──

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        int tick = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch { return; }
            tick++;
            try { SampleGauges(); } catch { }
            if (tick % 6 == 0)
            {
                try { await ProbeRoutersAsync(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { try { _ = ServiceLog($"KliveLLM router probes failed: {ex.Message}"); } catch { } }
            }
            if (DateTime.UtcNow - lastCataloguePricesUtc > TimeSpan.FromHours(6))
            {
                lastCataloguePricesUtc = DateTime.UtcNow;
                _ = Task.Run(() => RefreshCataloguePricesAsync(ct));
            }
        }
    }

    /// <summary>What each router is doing right now, written as minute maxima into router health.</summary>
    private void SampleGauges()
    {
        var ledger = Ledger;
        if (ledger == null) return;
        var health = ledger.Health;
        var flights = ledger.InFlight();
        foreach (var group in flights.Where(t => !string.IsNullOrEmpty(t.Router)).GroupBy(t => t.Router, StringComparer.OrdinalIgnoreCase))
        {
            health.NoteGauge(group.Key, "inFlight", group.Count());
            health.NoteGauge(group.Key, "queued", group.Count(t => t.State == LlmCallState.Queued));
        }
        var air = FairUse.Describe();
        health.NoteGauge(LlmRouterIds.AIRouter, "slotsBusy", air.InFlight);
        health.NoteGauge(LlmRouterIds.AIRouter, "waiting", air.Waiting);
        health.NoteGauge(LlmRouterIds.AIRouter, "requestsInWindow", air.RequestsInWindow);
        health.NoteGauge(LlmRouterIds.AIRouter, "tokensInWindow", air.TokensInWindow);
        health.NoteGauge(LlmRouterIds.AIRouter, "coolOffSeconds", air.PenaltyRemaining.TotalSeconds);
        health.NoteGauge(LlmRouterIds.AIRouter, "warmCohort", air.WarmCohortSize);
        health.NoteGauge(LlmRouterIds.AIRouter, "residents", air.Residents);
        if (AIRouterMeasuredPrefixLifetime() is { } lifetime) health.NoteGauge(LlmRouterIds.AIRouter, "prefixLifetimeSeconds", lifetime.TotalSeconds);
        var meter = PrefixCacheMeter.Shared.Describe();
        if (meter.Requests > 0) health.NoteGauge(LlmRouterIds.AIRouter, "cacheHitPct1h", Math.Round(meter.HitRate * 100, 2));
    }

    /// <summary>
    /// One cheap request per keyed router: OpenRouter's key endpoint (which also reports credit),
    /// AIRouter's and added routers' model list. HuggingFace every five minutes (its list is large).
    /// Costs no tokens anywhere.
    /// </summary>
    private async Task ProbeRoutersAsync(CancellationToken ct)
    {
        var ledger = Ledger;
        if (ledger == null) return;
        if (!await GetBoolOmniSetting("KliveLLM_RouterProbes", true)) return;
        foreach (var router in Routers().Values)
        {
            if (!router.Enabled || router.IsLocal || router.Id == LlmRouterIds.OpenRouterFree) continue;
            if (router.Id == LlmRouterIds.HuggingFace && lastProbeUtc.TryGetValue(router.Id, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(5)) continue;
            lastProbeUtc[router.Id] = DateTime.UtcNow;
            RemoteLLMProviderConfiguration? config;
            try { config = await BuildRouterConfigurationAsync(router); }
            catch { continue; } // no key: nothing to probe
            if (config == null) continue;

            string url = router.Kind switch
            {
                "openrouter" => "https://openrouter.ai/api/v1/key",
                _ => config.ChatCompletionsEndpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
                    ? config.ChatCompletionsEndpoint[..^"/chat/completions".Length] + "/models"
                    : config.ChatCompletionsEndpoint.TrimEnd('/') + "/models",
            };
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
                using var response = await probeHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                // Never read an unbounded body: the model lists of some routers are large.
                string body = await ReadCappedAsync(response, 2 * 1024 * 1024, ct);
                timer.Stop();
                if (!response.IsSuccessStatusCode)
                {
                    ledger.Health.NoteProbe(router.Id, false, timer.Elapsed.TotalMilliseconds, $"HTTP {(int)response.StatusCode}");
                    continue;
                }
                List<string>? models = null;
                try
                {
                    var json = JObject.Parse(body);
                    if (router.Kind == "openrouter")
                    {
                        var data = json["data"] as JObject;
                        if (data?["limit_remaining"] is JValue remaining && remaining.Type is JTokenType.Float or JTokenType.Integer)
                            ledger.Health.NoteGauge(router.Id, "creditRemainingUsd", remaining.Value<double>());
                        if (data?["usage"] is JValue used && used.Type is JTokenType.Float or JTokenType.Integer)
                            ledger.Health.NoteGauge(router.Id, "creditUsedUsd", used.Value<double>());
                        if (data?["limit"] is JValue limit && limit.Type is JTokenType.Float or JTokenType.Integer)
                            ledger.Health.NoteGauge(router.Id, "creditLimitUsd", limit.Value<double>());
                    }
                    else
                    {
                        models = ((json["data"] as JArray) ?? new JArray()).Select(m => (string?)m["id"])
                            .Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).ToList();
                    }
                }
                catch { /* a probe that answered is up even if its body is not the shape we know */ }
                ledger.Health.NoteProbe(router.Id, true, timer.Elapsed.TotalMilliseconds, null, models);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                ledger.Health.NoteProbe(router.Id, false, timer.Elapsed.TotalMilliseconds, ex.Message);
            }
        }
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > maxBytes) break;
        }
        return System.Text.Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>OpenRouter's catalogue prices into the price book (manual prices always win).</summary>
    internal async Task<int> RefreshCataloguePricesAsync(CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/models");
            string token = await GetOmniSetting("OpenRouterLLMToken", OmniSettingType.String, true, false);
            if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await capabilitiesHttp.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return 0;
            var json = JObject.Parse(await response.Content.ReadAsStringAsync(ct));
            return Prices.ImportOpenRouterCatalogue(json["data"] as JArray);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try { _ = ServiceLog($"KliveLLM: OpenRouter catalogue prices could not be read ({ex.Message})."); } catch { }
            return 0;
        }
    }
}
