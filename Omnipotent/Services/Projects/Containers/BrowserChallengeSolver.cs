using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// Protocol layer for turning a detected browser challenge into a token through a solving
    /// service. A CAPTCHA used to be a hard stop that ended the run and parked the project on a
    /// human; with a funded solver account an agent clears it the same way any other automated
    /// operator does, and only genuinely human-bound walls (SMS, ID checks) escalate.
    ///
    /// All three supported services speak the same createTask/getTaskResult JSON dialect, so the
    /// only per-service differences are the host and the task-type spelling.
    /// </summary>
    public static class BrowserChallengeSolver
    {
        public sealed record ChallengeWidget(string Provider, string SiteKey, bool Invisible, string? Action)
        {
            public string? Id { get; init; }
            public string? CData { get; init; }
            public string? PageData { get; init; }
            public string? DataS { get; init; }
            public bool Visible { get; init; } = true;
            public bool ResponsePresent { get; init; }
        }

        public sealed record ChallengeProbe(
            bool Detected, bool Interstitial, string Url, IReadOnlyList<ChallengeWidget> Widgets)
        {
            public string? TabId { get; init; }
            public string? DocumentId { get; init; }
            public string? Error { get; init; }
            public ChallengeWidget? Primary => Widgets.Where(w => !w.ResponsePresent
                && !string.IsNullOrWhiteSpace(w.SiteKey)).OrderByDescending(w => w.Visible).FirstOrDefault();
        }

        public sealed record SolveOutcome(bool Ready, string? Token, string? Error);

        /// <summary>Preference order; the client falls through configured providers on failure.</summary>
        public static readonly IReadOnlyList<string> SupportedServices = new[] { "capsolver", "2captcha", "anticaptcha" };

        /// <summary>Field names an agent might plausibly have used when registering the key.</summary>
        public static readonly IReadOnlyList<string> KeyFieldNames = new[] { "apiKey", "api_key", "key", "clientKey", "token", "password" };

        public static string EndpointFor(string service, string path) => service.ToLowerInvariant() switch
        {
            "2captcha" => "https://api.2captcha.com/" + path,
            "anticaptcha" => "https://api.anti-captcha.com/" + path,
            "capsolver" => "https://api.capsolver.com/" + path,
            _ => throw new ArgumentException("Unknown challenge service.", nameof(service)),
        };

        /// <summary>CapSolver capitalises the proxyless suffix differently from the anti-captcha dialect.</summary>
        public static string? TaskTypeFor(string service, string provider)
        {
            if (!SupportedServices.Contains(service, StringComparer.OrdinalIgnoreCase)) return null;
            bool capsolver = string.Equals(service, "capsolver", StringComparison.OrdinalIgnoreCase);
            string suffix = capsolver ? "ProxyLess" : "Proxyless";
            switch (provider.ToLowerInvariant())
            {
                case "recaptcha_v2":
                    return (capsolver ? "ReCaptchaV2Task" : "RecaptchaV2Task") + suffix;
                case "recaptcha_enterprise":
                    return (capsolver ? "ReCaptchaV2EnterpriseTask" : "RecaptchaV2EnterpriseTask") + suffix;
                case "hcaptcha":
                    return "HCaptchaTask" + suffix;
                case "turnstile":
                    return capsolver ? "AntiTurnstileTaskProxyLess"
                        : "TurnstileTaskProxyless";
                default:
                    return null;
            }
        }

        public static ChallengeProbe ParseProbe(string helperJson)
        {
            var widgets = new List<ChallengeWidget>();
            bool detected = false, interstitial = false;
            string url = "";
            try
            {
                using var document = JsonDocument.Parse(helperJson);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return new ChallengeProbe(false, false, "", widgets) { Error = "Challenge probe returned a non-object." };
                if (!root.TryGetProperty("detected", out _))
                    return new ChallengeProbe(false, false, "", widgets) { Error = "Challenge probe returned no detection state; update the desktop helper." };
                detected = root.TryGetProperty("detected", out var d) && d.ValueKind == JsonValueKind.True;
                interstitial = root.TryGetProperty("interstitial", out var i) && i.ValueKind == JsonValueKind.True;
                url = root.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() ?? "" : "";
                if (root.TryGetProperty("widgets", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var widget in list.EnumerateArray().Take(5))
                    {
                        if (widget.ValueKind != JsonValueKind.Object) continue;
                        string provider = widget.TryGetProperty("provider", out var p) && p.ValueKind == JsonValueKind.String
                            ? p.GetString() ?? "" : "";
                        string siteKey = widget.TryGetProperty("sitekey", out var k) && k.ValueKind == JsonValueKind.String
                            ? k.GetString() ?? "" : "";
                        bool invisible = widget.TryGetProperty("invisible", out var v) && v.ValueKind == JsonValueKind.True;
                        string? action = widget.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String
                            ? a.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(provider))
                            widgets.Add(new ChallengeWidget(provider, siteKey, invisible, action)
                            {
                                Id = StringField(widget, "id"), CData = StringField(widget, "cData"),
                                PageData = StringField(widget, "pageData"), DataS = StringField(widget, "dataS"),
                                Visible = !widget.TryGetProperty("visible", out var vis) || vis.ValueKind != JsonValueKind.False,
                                ResponsePresent = widget.TryGetProperty("responsePresent", out var response) && response.ValueKind == JsonValueKind.True,
                            });
                    }
                }
                return new ChallengeProbe(detected, interstitial, url, widgets)
                {
                    TabId = root.TryGetProperty("tab", out var tab) && tab.ValueKind == JsonValueKind.Object
                        ? StringField(tab, "id") : null,
                    DocumentId = StringField(root, "documentId"),
                };
            }
            catch (JsonException)
            {
                return new ChallengeProbe(false, false, "", widgets) { Error = "Challenge probe returned malformed JSON." };
            }
        }

        public static string BuildCreateTaskRequest(string service, string apiKey, ChallengeWidget widget, string pageUrl)
        {
            string? taskType = TaskTypeFor(service, widget.Provider)
                ?? throw new InvalidOperationException(
                    $"{service} cannot solve a {widget.Provider} challenge.");
            var task = new Dictionary<string, object?>
            {
                ["type"] = taskType,
                ["websiteURL"] = pageUrl,
                ["websiteKey"] = widget.SiteKey,
            };
            if (widget.Provider.Equals("turnstile", StringComparison.OrdinalIgnoreCase))
            {
                if (service.Equals("capsolver", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(widget.PageData))
                        throw new InvalidOperationException("CapSolver's standalone Turnstile task does not accept managed page data; use another configured provider.");
                    var metadata = new Dictionary<string, object?>();
                    Add(metadata, "action", widget.Action);
                    Add(metadata, "cdata", widget.CData);
                    if (metadata.Count > 0) task["metadata"] = metadata;
                }
                else
                {
                    bool twoCaptcha = service.Equals("2captcha", StringComparison.OrdinalIgnoreCase);
                    Add(task, "action", widget.Action);
                    Add(task, twoCaptcha ? "data" : "cData", widget.CData);
                    Add(task, twoCaptcha ? "pagedata" : "chlPageData", widget.PageData);
                }
            }
            else
            {
                if (widget.Invisible) task["isInvisible"] = true;
                Add(task, "pageAction", widget.Action);
                if (!string.IsNullOrWhiteSpace(widget.DataS))
                {
                    if (widget.Provider.Equals("recaptcha_enterprise", StringComparison.OrdinalIgnoreCase))
                        task["enterprisePayload"] = new Dictionary<string, object?> { ["s"] = widget.DataS };
                    else Add(task, "recaptchaDataSValue", widget.DataS);
                }
            }
            return JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["clientKey"] = apiKey,
                ["task"] = task,
            });
        }

        public static string BuildResultRequest(string apiKey, string taskId) =>
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["clientKey"] = apiKey,
                ["taskId"] = taskId,
            });

        /// <summary>Reads a createTask response. Returns the task id, or null with an error.</summary>
        public static string? ReadTaskId(string json, out string? error)
        {
            error = null;
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    error = "The solver returned a non-object response.";
                    return null;
                }
                string? failure = ReadError(root);
                if (failure != null)
                {
                    error = failure;
                    return null;
                }
                if (root.TryGetProperty("taskId", out var id))
                {
                    string? value = id.ValueKind == JsonValueKind.String ? id.GetString()
                        : id.ValueKind == JsonValueKind.Number ? id.ToString() : null;
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
                error = "The solver accepted the request but returned no taskId.";
                return null;
            }
            catch (JsonException)
            {
                error = "The solver returned malformed JSON.";
                return null;
            }
        }

        /// <summary>Reads a getTaskResult response into ready/pending/failed.</summary>
        public static SolveOutcome ReadResult(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return new SolveOutcome(false, null, "The solver returned a non-object response.");
                string? failure = ReadError(root);
                if (failure != null) return new SolveOutcome(false, null, failure);
                string status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString() ?? "" : "";
                if (status is "processing" or "idle")
                    return new SolveOutcome(false, null, null);
                if (!status.Equals("ready", StringComparison.OrdinalIgnoreCase))
                    return new SolveOutcome(false, null, "The solver returned a failed or unknown task status.");
                if (!root.TryGetProperty("solution", out var solution) || solution.ValueKind != JsonValueKind.Object)
                    return new SolveOutcome(false, null, "The solver reported ready with no solution.");
                foreach (string field in new[] { "gRecaptchaResponse", "token", "text" })
                {
                    if (solution.TryGetProperty(field, out var value)
                        && value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(value.GetString()))
                        return new SolveOutcome(true, value.GetString(), null);
                }
                return new SolveOutcome(false, null, "The solver reported ready but returned no token field.");
            }
            catch (JsonException)
            {
                return new SolveOutcome(false, null, "The solver returned malformed JSON.");
            }
        }

        private static string? ReadError(JsonElement root)
        {
            bool failed = root.TryGetProperty("errorId", out var errorId)
                && errorId.ValueKind is JsonValueKind.Number or JsonValueKind.String
                && int.TryParse(errorId.ToString(), out int code) && code != 0;
            failed |= !string.IsNullOrWhiteSpace(StringField(root, "errorCode"));
            if (!failed) return null;
            string errorCode = root.TryGetProperty("errorCode", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? "" : "";
            // Never echo provider descriptions: they can contain the submitted credential or token.
            return System.Text.RegularExpressions.Regex.IsMatch(errorCode, "^[A-Z][A-Z0-9_]{1,99}$")
                ? errorCode : "The solver rejected the task.";
        }

        private static string? StringField(JsonElement value, string field) =>
            value.TryGetProperty(field, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;

        private static void Add(Dictionary<string, object?> target, string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) target[key] = value;
        }

        /// <summary>The message an agent gets when no solver account exists yet. It has to be
        /// enough to fix the situation without a human, because that is the whole point.</summary>
        public static string NoSolverConfiguredMessage(string pageUrl, string provider) =>
            $"A {provider} challenge is blocking {Shorten(pageUrl)} and no solving service is registered. " +
            "This is self-serve, not a reason to stop: register one in the SHARED account registry as service " +
            "'capsolver' (or '2captcha' / 'anticaptcha') with field 'apiKey' — account op:register — and this tool " +
            "will use it automatically for every project. Use an existing funded account and register its actual key; " +
            "an account-registry entry does not create a provider account or add credits. " +
            "If you cannot fund an account, treat the challenge as a human-bound wall and use request_human, " +
            "which lets Klives drive this desktop directly.";

        private static string Shorten(string url) =>
            string.IsNullOrWhiteSpace(url) ? "this page"
                : url.Length <= 120 ? url : url[..117] + "...";
    }
}
