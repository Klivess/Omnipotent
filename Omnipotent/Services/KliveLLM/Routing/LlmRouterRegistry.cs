using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.KliveLLM
{
    /// <summary>What a router charges. This decides how cost is booked and how far fallback may go.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum LlmBilling
    {
        /// <summary>Every token is billed.</summary>
        PerToken,
        /// <summary>A subscription: tokens are metered but no money moves with them (AIRouter).</summary>
        Flat,
        /// <summary>No charge at all (OpenRouter's free models).</summary>
        Free,
    }

    public static class LlmRouterIds
    {
        public const string OpenRouter = "openrouter";
        public const string OpenRouterFree = "openrouter-free";
        public const string AIRouter = "airouter";
        public const string HuggingFace = "huggingface";
        public const string Custom = "custom";
        public const string Local = "local";

        public static readonly string[] BuiltIn = { OpenRouter, OpenRouterFree, AIRouter, HuggingFace, Custom, Local };

        public static bool IsBuiltIn(string? id) => id != null && BuiltIn.Contains(id, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One endpoint KliveLLM can send model calls to. The six built-in routers are the ones KliveLLM
    /// has always had (their keys and default models stay in the existing OmniSettings); any number of
    /// further OpenAI-compatible routers can be added from the KliveLLM page. Added routers are spoken
    /// to as vanilla OpenAI chat-completions, so no OpenRouter- or AIRouter-specific field ever reaches them.
    /// </summary>
    public sealed class LlmRouterDefinition
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>Wire protocol: openrouter, airouter, huggingface, openai or local.</summary>
        public string Kind { get; set; } = "openai";
        public bool BuiltIn { get; set; }
        /// <summary>Base URL of an added router ("https://example.com/v1"); built-ins have their own.</summary>
        public string? BaseUrl { get; set; }
        /// <summary>The model sent when nothing else chooses one. Built-ins read their OmniSetting instead.</summary>
        public string? DefaultModel { get; set; }
        public LlmBilling Billing { get; set; } = LlmBilling.PerToken;
        /// <summary>The subscription, for flat-fee economics on the Costs tab.</summary>
        public double? MonthlyFeeUsd { get; set; }
        /// <summary>Client-side limits for added routers. AIRouter keeps its own fair-use envelope.</summary>
        public int? MaxParallel { get; set; }
        public int? RequestsPerMinute { get; set; }
        public bool Enabled { get; set; } = true;
        public string? Notes { get; set; }
        public DateTime? CreatedUtc { get; set; }
        public DateTime? UpdatedUtc { get; set; }

        [JsonIgnore] public bool BillsPerToken => Billing == LlmBilling.PerToken;
        [JsonIgnore] public bool IsLocal => string.Equals(Id, LlmRouterIds.Local, StringComparison.OrdinalIgnoreCase);

        /// <summary>The OmniSetting that holds this router's key (empty for local).</summary>
        [JsonIgnore]
        public string KeySetting => Id.ToLowerInvariant() switch
        {
            LlmRouterIds.OpenRouter or LlmRouterIds.OpenRouterFree => "OpenRouterLLMToken",
            LlmRouterIds.AIRouter => "AIRouterLLMToken",
            LlmRouterIds.HuggingFace => "HuggingFaceLLMToken",
            LlmRouterIds.Custom => "CustomOpenAILLMToken",
            LlmRouterIds.Local => "",
            _ => LlmRouterRegistry.KeySettingFor(Id),
        };

        /// <summary>The OmniSetting that holds a built-in router's default model.</summary>
        [JsonIgnore]
        public string? ModelSetting => Id.ToLowerInvariant() switch
        {
            LlmRouterIds.OpenRouter => "OpenRouterModelID",
            LlmRouterIds.OpenRouterFree => "FreeOpenRouterModelID",
            LlmRouterIds.AIRouter => "AIRouterModelID",
            LlmRouterIds.HuggingFace => "HuggingFaceModelID",
            LlmRouterIds.Custom => "CustomOpenAIModelID",
            _ => null,
        };

        public LlmRouterDefinition Clone() => (LlmRouterDefinition)MemberwiseClone();
    }

    public static class LlmRouterRegistry
    {
        internal const string AddedKeyPrefix = "KliveLLM_RouterKey_";

        public static string KeySettingFor(string id) => AddedKeyPrefix + id;

        /// <summary>The routers KliveLLM ships with, as fresh objects.</summary>
        public static List<LlmRouterDefinition> BuiltIns() => new()
        {
            new() { Id = LlmRouterIds.AIRouter, Name = "AIRouter", Kind = "airouter", BuiltIn = true, Billing = LlmBilling.Flat,
                Notes = "Flat-fee router: 3 parallel requests, 240 a minute, 10M tokens a minute, shared by every service on the key." },
            new() { Id = LlmRouterIds.OpenRouter, Name = "OpenRouter", Kind = "openrouter", BuiltIn = true, Billing = LlmBilling.PerToken },
            new() { Id = LlmRouterIds.OpenRouterFree, Name = "OpenRouter (free)", Kind = "openrouter", BuiltIn = true, Billing = LlmBilling.Free,
                Notes = "OpenRouter with FreeOpenRouterModelID: what useFreeModel calls have always used." },
            new() { Id = LlmRouterIds.HuggingFace, Name = "HuggingFace", Kind = "huggingface", BuiltIn = true, Billing = LlmBilling.PerToken },
            new() { Id = LlmRouterIds.Custom, Name = "Custom endpoint", Kind = "openai", BuiltIn = true, Billing = LlmBilling.PerToken,
                Notes = "The OpenAI-compatible endpoint in CustomOpenAIEndpointURL." },
            new() { Id = LlmRouterIds.Local, Name = "Local model", Kind = "local", BuiltIn = true, Billing = LlmBilling.Free,
                Notes = "The GGUF model loaded in-process. Text only: no tool calling." },
        };

        /// <summary>
        /// Every router KliveLLM knows: the built-ins with whatever Klives changed on them (a monthly fee,
        /// notes, switched off, the custom endpoint's billing), then every added router.
        /// </summary>
        public static Dictionary<string, LlmRouterDefinition> Merge(IEnumerable<LlmRouterDefinition>? stored)
        {
            var result = new Dictionary<string, LlmRouterDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var builtIn in BuiltIns()) result[builtIn.Id] = builtIn;
            foreach (var entry in stored ?? Enumerable.Empty<LlmRouterDefinition>())
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Id)) continue;
                if (result.TryGetValue(entry.Id, out var builtIn) && builtIn.BuiltIn)
                {
                    builtIn.MonthlyFeeUsd = entry.MonthlyFeeUsd;
                    builtIn.Enabled = entry.Enabled;
                    if (!string.IsNullOrWhiteSpace(entry.Notes)) builtIn.Notes = entry.Notes;
                    // Only the generic endpoint's billing is unknown to KliveLLM.
                    if (string.Equals(builtIn.Id, LlmRouterIds.Custom, StringComparison.OrdinalIgnoreCase)) builtIn.Billing = entry.Billing;
                    builtIn.UpdatedUtc = entry.UpdatedUtc;
                    continue;
                }
                var added = entry.Clone();
                added.BuiltIn = false;
                added.Kind = "openai";
                result[added.Id] = added;
            }
            return result;
        }

        internal static string FromProvider(KliveLLM.LLMProvider provider) => provider switch
        {
            KliveLLM.LLMProvider.Local => LlmRouterIds.Local,
            KliveLLM.LLMProvider.OpenRouter => LlmRouterIds.OpenRouter,
            KliveLLM.LLMProvider.CustomOpenAI => LlmRouterIds.Custom,
            KliveLLM.LLMProvider.AIRouter => LlmRouterIds.AIRouter,
            _ => LlmRouterIds.HuggingFace,
        };

        /// <summary>A stable id for a router added from the page: its name as a slug, made unique.</summary>
        public static string NewId(string name, ICollection<string> taken)
        {
            string slug = Regex.Replace((name ?? "").Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            if (slug.Length == 0) slug = "router";
            if (slug.Length > 32) slug = slug[..32].Trim('-');
            string id = slug;
            for (int n = 2; LlmRouterIds.IsBuiltIn(id) || taken.Contains(id); n++) id = $"{slug}-{n}";
            return id;
        }

        /// <summary>Why an added router cannot be saved, or null when it can.</summary>
        public static string? Validate(LlmRouterDefinition router)
        {
            if (string.IsNullOrWhiteSpace(router.Name)) return "A router needs a name.";
            if (router.Name.Length > 60) return "That name is too long (60 characters at most).";
            if (router.BuiltIn) return null;
            if (string.IsNullOrWhiteSpace(router.BaseUrl)) return "An added router needs its base URL.";
            if (!Uri.TryCreate(router.BaseUrl.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
                return "The base URL must be an http(s) address.";
            if (string.IsNullOrWhiteSpace(router.DefaultModel)) return "An added router needs a default model.";
            if (router.MaxParallel is < 1 or > 64) return "Max parallel must be between 1 and 64.";
            if (router.RequestsPerMinute is < 1 or > 100_000) return "Requests a minute must be between 1 and 100000.";
            if (router.MonthlyFeeUsd is < 0 or > 1_000_000) return "The monthly fee is out of range.";
            return null;
        }
    }
}
