using DSharpPlus;
using DSharpPlus.Entities;
using Microsoft.Extensions.AI;
using LLama;
using LLama.Abstractions;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Markdig.Extensions.TaskLists;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using System.Net;
using System.Reflection;
using System.Security.Permissions;
using System.IO;
using System.Linq;
using LangChain.Providers;
using System.Drawing;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Security.Policy;
using System.IO.Compression;
using System.Net.Http.Headers;
using HuggingFace;
using Omnipotent.Services.KliveLLM;
using System.ServiceModel;

namespace Omnipotent.Services.KliveLLM
{
    /// <summary>Stable, machine-readable classification for a remote inference failure.</summary>
    public enum RemoteLLMFailureKind
    {
        Unknown,
        Network,
        Timeout,
        RateLimited,
        InsufficientProviderCredit,
        Authentication,
        InvalidRequest,
        ModelUnavailable,
        ProviderUnavailable,
        EmptyResponse,
    }

    /// <summary>
    /// A remote inference failure with enough structure for callers to choose retry, fallback,
    /// defer, or human-attention behaviour without parsing the human-readable exception text.
    /// </summary>
    public sealed class RemoteLLMException : HttpRequestException
    {
        public RemoteLLMException(
            RemoteLLMFailureKind kind,
            string message,
            string provider,
            string model,
            HttpStatusCode? statusCode = null,
            int? requestedMaxTokens = null,
            int? affordableMaxTokens = null,
            TimeSpan? retryAfter = null,
            Exception? innerException = null)
            : base(message, innerException, statusCode)
        {
            Kind = kind;
            Provider = provider;
            Model = model;
            RequestedMaxTokens = requestedMaxTokens;
            AffordableMaxTokens = affordableMaxTokens;
            RetryAfter = retryAfter;
        }

        public RemoteLLMFailureKind Kind { get; }
        public string Provider { get; }
        public string Model { get; }
        public int? RequestedMaxTokens { get; }
        public int? AffordableMaxTokens { get; }
        public TimeSpan? RetryAfter { get; }

        public bool IsRetryable => Kind is RemoteLLMFailureKind.Network
            or RemoteLLMFailureKind.Timeout
            or RemoteLLMFailureKind.RateLimited
            or RemoteLLMFailureKind.ProviderUnavailable
            or RemoteLLMFailureKind.EmptyResponse;
    }

    /// <summary>
    /// What a given model+provider can accept/do, negotiated once so a caller never blind-sends a
    /// modality the endpoint would 400 on. <see cref="NativeToolCalling"/> preserves the historical
    /// "any remote provider supports tools" behaviour; the modality flags gate the newer
    /// image/audio/document content-parts. Sourced from the OpenRouter model catalog when reachable,
    /// otherwise a conservative static family table (see <c>KliveLLM.StaticModelCapabilities</c>).
    /// </summary>
    public sealed record ModelCapabilities(
        bool NativeToolCalling,
        bool ImageInput,
        bool AudioInput,
        bool DocumentInput,
        bool PromptCaching,
        bool Reasoning);

    /// <summary>
    /// Per-call sampling overrides. Every member is optional: a null is simply not sent, so the
    /// provider's own default applies and a caller that pins nothing produces the identical request it
    /// did before this type existed. Callers (Projects' per-route parameter configuration) are
    /// responsible for range validation — see <c>ModelParameterCatalog</c>.
    ///
    /// <see cref="TopK"/>, <see cref="RepetitionPenalty"/>, <see cref="MinP"/>, <see cref="TopA"/>,
    /// <see cref="ServiceTier"/> and <see cref="ProviderOnly"/> are OpenRouter extensions rather
    /// than vanilla OpenAI fields, and are dropped for any other provider so a strict
    /// OpenAI-compatible endpoint can never 400 on a parameter it doesn't know.
    /// </summary>
    public sealed record ModelSamplingParameters(
        double? Temperature = null,
        double? TopP = null,
        int? TopK = null,
        double? FrequencyPenalty = null,
        double? PresencePenalty = null,
        double? RepetitionPenalty = null,
        double? MinP = null,
        double? TopA = null,
        int? Seed = null,
        string? ServiceTier = null,
        IReadOnlyList<string>? ProviderOnly = null)
    {
        public bool IsEmpty => Temperature is null && TopP is null && TopK is null
            && FrequencyPenalty is null && PresencePenalty is null && RepetitionPenalty is null
            && MinP is null && TopA is null && Seed is null && ServiceTier is null
            && (ProviderOnly == null || ProviderOnly.Count == 0);
    }

    public class KliveLLM : OmniService
    {
        private const string DefaultHuggingFaceModel = "meta-llama/Llama-3.1-8B-Instruct:cerebras";
        private const string DefaultOpenRouterModel = "openai/gpt-4.1-mini";
        private const string DefaultFreeOpenRouterModel = "openrouter/free";

        // Any OpenAI-compatible endpoint Klives points at (agentrouter.org, LiteLLM, vLLM, a self-hosted
        // gateway…), driven entirely by two settings: CustomOpenAIEndpointURL + CustomOpenAILLMToken.
        // It is treated as a VANILLA OpenAI chat-completions endpoint — every OpenRouter-proprietary
        // extra (the `models` fallback array, service_tier, usage.include cost reporting, cache_control
        // prompt caching, the attribution headers, the 402 affordable-token retry) stays gated on
        // LLMProvider.OpenRouter so a strict endpoint can never 400 on a parameter it doesn't know.
        private const string CustomOpenAIProviderOption = "Custom OpenAI Compatible Endpoint";
        private const string DefaultCustomOpenAIEndpoint = "https://agentrouter.org/v1/";
        private const string DefaultCustomOpenAIModel = "openai/gpt-4.1-mini";

        // AIRouter (api.airouter.ch) — a Swiss OpenAI-compatible router sold as a FLAT FEE: unlimited
        // access, no per-token price. It gets its own provider rather than riding the generic custom
        // endpoint because two of its behaviours are not generic:
        //   • no pricing — every cost meter (the Projects budget ledger above all) must book ZERO for
        //     it, or a project would budget-pause against money that is never actually charged;
        //   • a hard fair-use envelope (3 parallel / 240 req-min / 10M token-min) that we stay under
        //     by client-side admission control (AIRouterFairUseLimiter) instead of by handling 429s.
        // The wire format itself is vanilla OpenAI chat-completions, so every OpenRouter-proprietary
        // extra stays gated on LLMProvider.OpenRouter exactly as it is for CustomOpenAI.
        private const string AIRouterProviderOption = "AIRouter";
        private const string AIRouterEndpoint = "https://api.airouter.ch/v1";
        private const string DefaultAIRouterModel = "Qwen3.8";

        private static readonly string[] ProviderOptions = new[] { "Local", "HuggingFace", "OpenRouter", CustomOpenAIProviderOption, AIRouterProviderOption };
        private static readonly string[] OpenRouterServiceTierOptions = new[] { "default", "flex", "priority" };
        private static readonly string[] ThinkingTypeOptions = new[] { "Off", "Low", "Medium", "High" };

        private string huggingFaceToken = "";
        private HttpClient client;
        private string ModelDownloadUrl = "";
        private static string LLamaBinariesDownloadPath;
        private static string LLamaBinariesFolder = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveLLamaBinariesDirectory);
        public static string LLamaDLLFile = Path.Combine(LLamaBinariesFolder, "llama.dll");
        public static string LLamaMTMDFile = Path.Combine(LLamaBinariesFolder, "mtmd.dll");
        private string modelPath;
        private ModelParams modelParams;
        private LLamaWeights modelWeights;
        private bool localModelReady = false;
        // How hard the model is asked to "think" (reasoning). "Off" appends /no_think to the system
        // prompt (Qwen-family + local) and disables OpenRouter reasoning; Low/Medium/High map to the
        // OpenRouter reasoning effort. Refreshed live on settings change. Read by sessions at creation,
        // so it also seeds the local model's directive.
        internal string thinkingType = "Medium";
        private Dictionary<string, KliveLLMSession> sessions = new Dictionary<string, KliveLLMSession>();

        // ── Model capability negotiation ──
        // Cached OpenRouter /models catalog (model id → capabilities), refreshed on a TTL. The catalog is the
        // authoritative source for which input modalities a model accepts; a static family table backs it up
        // for HuggingFace and catalog misses. See GetModelCapabilitiesAsync / GetOpenRouterCapabilityCatalogAsync.
        private static readonly HttpClient capabilitiesHttp = new() { Timeout = TimeSpan.FromSeconds(10) };
        private readonly object capabilitiesLock = new();
        private Dictionary<string, ModelCapabilities>? openRouterCapabilities;
        private DateTime openRouterCapabilitiesFetchedUtc = DateTime.MinValue;
        private static readonly TimeSpan CapabilitiesCacheTtl = TimeSpan.FromHours(6);

        internal enum LLMProvider
        {
            Local,
            HuggingFace,
            OpenRouter,
            CustomOpenAI,
            AIRouter,
        }

        /// <summary>
        /// True for providers billed as a flat subscription rather than per token. Callers that meter
        /// spend (the Projects budget ledger, KliveAgent's stats estimate) must book zero for these:
        /// their token counts are still real telemetry, but no money moves with them, so a budget
        /// must never pause work on account of them.
        /// </summary>
        internal static bool IsFlatFee(LLMProvider provider) => provider == LLMProvider.AIRouter;

        // One process-wide limiter: the fair-use envelope is per KEY, and every subsystem
        // (KliveAgent, Projects Commander + sub-agents + councils + utility routes, Omniscience,
        // Stratum) shares the one configured AIRouter key, so they must share one queue.
        private static readonly AIRouterFairUseLimiter sharedAIRouterFairUse = new();

        /// <summary>The fair-use queue this instance admits AIRouter requests through. Defaults to the
        /// process-wide one — the live service must share a queue with every other caller of the same
        /// key. A test-constructed instance gets its own so a deliberate 429 in one test cannot stall
        /// the shared queue for the rest of the run.</summary>
        internal AIRouterFairUseLimiter FairUse { get; set; } = sharedAIRouterFairUse;

        internal sealed class RemoteLLMProviderConfiguration
        {
            public RemoteLLMProviderConfiguration(LLMProvider provider, string displayName, string chatCompletionsEndpoint, string apiKey, string model, string? serviceTier = null)
            {
                Provider = provider;
                DisplayName = displayName;
                ChatCompletionsEndpoint = chatCompletionsEndpoint;
                ApiKey = apiKey;
                Model = model;
                ServiceTier = serviceTier;
            }

            public LLMProvider Provider { get; }
            public string DisplayName { get; }
            public string ChatCompletionsEndpoint { get; }
            public string ApiKey { get; }
            public string Model { get; }
            public string? ServiceTier { get; }
        }

        public KliveLLM()
        {
            name = "KliveLLM";
            threadAnteriority = ThreadAnteriority.High;
        }

        /// <summary>Test seam for deterministic provider-response policy tests.</summary>
        internal KliveLLM(HttpClient httpClient) : this()
        {
            client = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            // Never share the live fair-use queue with a test instance: a test that deliberately
            // provokes a 429 would otherwise penalise every other caller in the process.
            FairUse = new AIRouterFairUseLimiter();
        }

        protected override async void ServiceMain()
        {
            LLamaBinariesDownloadPath = await GetStringOmniSetting("CustomLLamaBinariesDownloadLink", "https://github.com/Klivess/Omnipotent/raw/refs/heads/master/OldCPULLamaBinaries.zip", false, true);
            ModelDownloadUrl = await GetOmniSetting("LocalLLMGGUFDownloadURL", OmniSettingType.String, false, true);
            huggingFaceToken = await GetOmniSetting("HuggingFaceLLMToken", OmniSettingType.String, true, false);
            await EnsureProviderSettingsExistAsync();
            thinkingType = await GetDropdownOmniSetting("ThinkingType", "Medium", ThinkingTypeOptions, false, false);
            client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

            await EnsureLlamaBinariesAvailableAsync();
            NativeLibraryConfig.All.WithLibrary(LLamaDLLFile, LLamaMTMDFile);

            if (await GetActiveProviderAsync() == LLMProvider.Local)
            {
                await SetupLocalLLM();
            }

            OnOmniSettingsChanged += KliveLLM_OnOmniSettingsChanged;
        }

        private async void KliveLLM_OnOmniSettingsChanged(object? sender, OmniSettingsChangedEventArgs e)
        {
            bool valueChanged = string.Equals(e.PreviousValue, e.Setting.Value, StringComparison.Ordinal) == false;

            if (e.Setting.Name == "ThinkingType" && valueChanged)
            {
                thinkingType = e.Setting.Value;
                ServiceLog($"Thinking type updated to '{thinkingType}'.");
            }

            if (e.Setting.Name == "RemoteLLMProvider" && valueChanged)
            {
                cachedActiveProvider = (int)ParseProvider(e.Setting.Value);
                if (ParseProvider(e.Setting.Value) == LLMProvider.Local)
                {
                    await SetupLocalLLM();
                    ServiceLog("Switched to local LLama provider. Model resources have been initialized.");
                }
                else
                {
                    modelWeights?.Dispose();
                    modelWeights = null;
                    localModelReady = false;
                    ServiceLog("Switched to remote LLM provider. Local model resources have been released.");
                }
            }
            if(e.Setting.Name == "LocalLLMGGUFDownloadURL" && valueChanged && await GetActiveProviderAsync() == LLMProvider.Local)
            {
                ServiceLog("Local LLM model URL updated. Setting up local model...");
                await SetupLocalLLM();
                ServiceLog("Local LLM model URL updated. Model resources have been re-initialized with the new model.");
            }
        }

        /// <summary>Maps the RemoteLLMProvider dropdown value onto the provider it selects. This is the
        /// single place the setting is interpreted — every remote call site resolves its endpoint, key and
        /// model through <see cref="GetRemoteProviderConfigurationAsync"/>, so the dropdown governs ALL
        /// traffic (KliveAgent, Omniscience, and the Projects Commander/sub-agent/utility/council routes)
        /// rather than any one subsystem. An unrecognised value falls back to HuggingFace.</summary>
        internal static LLMProvider ParseProvider(string? configuredProvider)
        {
            if (string.Equals(configuredProvider, "Local", StringComparison.OrdinalIgnoreCase))
            {
                return LLMProvider.Local;
            }

            if (string.Equals(configuredProvider, "OpenRouter", StringComparison.OrdinalIgnoreCase))
            {
                return LLMProvider.OpenRouter;
            }

            if (string.Equals(configuredProvider, CustomOpenAIProviderOption, StringComparison.OrdinalIgnoreCase))
            {
                return LLMProvider.CustomOpenAI;
            }

            // Accept the spaced spelling too — it is how the service brands itself, and a setting
            // typed by hand should not silently fall through to HuggingFace.
            if (string.Equals(configuredProvider, AIRouterProviderOption, StringComparison.OrdinalIgnoreCase)
                || string.Equals(configuredProvider, "AI Router", StringComparison.OrdinalIgnoreCase))
            {
                return LLMProvider.AIRouter;
            }

            return LLMProvider.HuggingFace;
        }

        /// <summary>
        /// Turns whatever Klives typed into CustomOpenAIEndpointURL into the chat-completions URL to POST.
        /// A base URL ("https://agentrouter.org/v1/") gets "chat/completions" appended; a URL that already
        /// names the endpoint is used verbatim. Trailing slashes either way are irrelevant, so the setting
        /// can be pasted from any provider's docs without a format gotcha.
        /// </summary>
        internal static string ResolveChatCompletionsEndpoint(string? configuredUrl)
        {
            string url = (configuredUrl ?? string.Empty).Trim();
            if (url.Length == 0) return string.Empty;
            string trimmed = url.TrimEnd('/');
            return trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
                ? trimmed
                : trimmed + "/chat/completions";
        }

        // Last provider the dropdown resolved to. GetActiveProviderAsync is async (it reads an
        // OmniSetting) but the cost meters that must know "is this router flat-fee?" run on
        // synchronous hot paths — a budget check per model turn. This cache is refreshed on every
        // resolution and on the settings-changed event, so it is never more than one turn stale.
        private volatile int cachedActiveProvider = (int)LLMProvider.HuggingFace;

        private async Task<LLMProvider> GetActiveProviderAsync()
        {
            string configuredProvider = await GetDropdownOmniSetting("RemoteLLMProvider", "HuggingFace", ProviderOptions, false, false);
            var provider = ParseProvider(configuredProvider);
            cachedActiveProvider = (int)provider;
            return provider;
        }

        /// <summary>
        /// Whether the active router charges a flat fee rather than per token — the question every
        /// cost meter in Omnipotent has to answer before it books spend. Synchronous by design: it
        /// serves the per-turn budget check in Projects, which cannot await a settings read.
        /// </summary>
        public bool IsFlatFeeProviderNow() => IsFlatFee((LLMProvider)cachedActiveProvider);

        /// <summary>Authoritative (settings-reading) form of <see cref="IsFlatFeeProviderNow"/>, for
        /// callers that are already on an async path and want to prime the cache.</summary>
        public async Task<bool> IsFlatFeeProviderAsync() => IsFlatFee(await GetActiveProviderAsync());

        private async Task EnsureProviderSettingsExistAsync()
        {
            var settingsManager = await GetOmniGlobalSettingsManager();
            var legacyProviderSetting = settingsManager.FindExistingSetting("UseHuggingFaceProvider", serviceID);
            var activeProviderSetting = settingsManager.FindExistingSetting("RemoteLLMProvider", serviceID);

            string? migratedProviderValue = null;
            if (legacyProviderSetting != null
                && bool.TryParse(legacyProviderSetting.Value, out bool useHuggingFaceProvider)
                && (activeProviderSetting == null || string.Equals(activeProviderSetting.Value, "HuggingFace", StringComparison.OrdinalIgnoreCase)))
            {
                migratedProviderValue = useHuggingFaceProvider ? "HuggingFace" : "Local";
            }

            await GetDropdownOmniSetting("RemoteLLMProvider", migratedProviderValue ?? "HuggingFace", ProviderOptions, false, false);

            if (!string.IsNullOrWhiteSpace(migratedProviderValue))
            {
                await settingsManager.SetDropdownOmniSetting("RemoteLLMProvider", migratedProviderValue, ProviderOptions, serviceID, name);
            }

            if (legacyProviderSetting != null)
            {
                await settingsManager.DeleteOmniSetting("UseHuggingFaceProvider", serviceID);
            }

            await GetStringOmniSetting("HuggingFaceModelID", DefaultHuggingFaceModel, false, false);
            await GetStringOmniSetting("OpenRouterLLMToken", defaultValue: null, sensitive: true, askKlivesForFulfillment: false);
            await GetStringOmniSetting("OpenRouterModelID", DefaultOpenRouterModel, false, false);
            await GetStringOmniSetting("CustomOpenAIEndpointURL", DefaultCustomOpenAIEndpoint, false, false);
            await GetStringOmniSetting("CustomOpenAILLMToken", defaultValue: null, sensitive: true, askKlivesForFulfillment: false);
            await GetStringOmniSetting("CustomOpenAIModelID", DefaultCustomOpenAIModel, false, false);
            // AIRouter needs no endpoint setting: it is one fixed service, not a generic gateway.
            await GetStringOmniSetting("AIRouterLLMToken", defaultValue: null, sensitive: true, askKlivesForFulfillment: false);
            await GetStringOmniSetting("AIRouterModelID", DefaultAIRouterModel, false, false);
            await GetStringOmniSetting("FreeOpenRouterModelID", DefaultFreeOpenRouterModel, false, false);
            await GetDropdownOmniSetting("OpenRouterServiceTier", "default", OpenRouterServiceTierOptions, false, false);
            await GetDropdownOmniSetting("ThinkingType", "Medium", ThinkingTypeOptions, false, false);
        }

        private async Task SetupLocalLLM()
        {
            try
            {
                await EnsureModelDownloadedAsync();
                await InitializeLocalModelAsync();
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, $"INIT FAILED: {ex.Message} | Inner: {ex.InnerException?.Message} | Stack: {ex.StackTrace}");
                return;
            }
        }

        private async Task EnsureLlamaBinariesAvailableAsync()
        {
            Directory.CreateDirectory(LLamaBinariesFolder);
            if (File.Exists(LLamaDLLFile) && File.Exists(LLamaMTMDFile))
            {
                return;
            }

            string tempZipPath = Path.Combine(Path.GetTempPath(), $"llama-binaries-{Guid.NewGuid():N}.zip");
            try
            {
                await ServiceLog($"LLama binaries missing. Downloading from {LLamaBinariesDownloadPath}");

                using var response = await client.GetAsync(LLamaBinariesDownloadPath, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                await using var remoteStream = await response.Content.ReadAsStreamAsync();
                await using (var fs = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await remoteStream.CopyToAsync(fs);
                }

                ZipFile.ExtractToDirectory(tempZipPath, LLamaBinariesFolder, overwriteFiles: true);

                // Some archives place binaries in a nested folder (e.g. Release). If so, flatten all DLLs
                // into the expected LLamaBinariesFolder so native dependencies resolve correctly.
                string? extractedLlamaDllPath = Directory
                    .GetFiles(LLamaBinariesFolder, "llama.dll", SearchOption.AllDirectories)
                    .OrderBy(p => p.Length)
                    .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(extractedLlamaDllPath))
                {
                    string? sourceDirectory = Path.GetDirectoryName(extractedLlamaDllPath);
                    if (!string.IsNullOrWhiteSpace(sourceDirectory)
                        && !Path.GetFullPath(sourceDirectory).Equals(Path.GetFullPath(LLamaBinariesFolder), StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (string dllPath in Directory.GetFiles(sourceDirectory, "*.dll", SearchOption.TopDirectoryOnly))
                        {
                            string destination = Path.Combine(LLamaBinariesFolder, Path.GetFileName(dllPath));
                            if (!Path.GetFullPath(dllPath).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                            {
                                File.Copy(dllPath, destination, overwrite: true);
                            }
                        }
                    }
                }

                if (!File.Exists(LLamaDLLFile) || !File.Exists(LLamaMTMDFile))
                {
                    string? extractedLlamaDll = Directory
                        .GetFiles(LLamaBinariesFolder, "llama.dll", SearchOption.AllDirectories)
                        .FirstOrDefault();
                    string? extractedMtmdDll = Directory
                        .GetFiles(LLamaBinariesFolder, "mtmd.dll", SearchOption.AllDirectories)
                        .FirstOrDefault();

                    if (!string.IsNullOrWhiteSpace(extractedLlamaDll) && !string.IsNullOrWhiteSpace(extractedMtmdDll))
                    {
                        if (!Path.GetFullPath(extractedLlamaDll).Equals(Path.GetFullPath(LLamaDLLFile), StringComparison.OrdinalIgnoreCase))
                        {
                            File.Copy(extractedLlamaDll, LLamaDLLFile, overwrite: true);
                        }

                        if (!Path.GetFullPath(extractedMtmdDll).Equals(Path.GetFullPath(LLamaMTMDFile), StringComparison.OrdinalIgnoreCase))
                        {
                            File.Copy(extractedMtmdDll, LLamaMTMDFile, overwrite: true);
                        }
                    }
                }

                if (!File.Exists(LLamaDLLFile) || !File.Exists(LLamaMTMDFile))
                {
                    throw new FileNotFoundException($"Downloaded binaries zip did not contain required files at expected paths: {LLamaDLLFile} and {LLamaMTMDFile}");
                }

                await ServiceLog($"LLama binaries downloaded and extracted to {LLamaBinariesFolder}");
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Failed to download/extract LLama binaries");
                throw;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempZipPath))
                    {
                        File.Delete(tempZipPath);
                    }
                }
                catch { }
            }
        }


        public async Task<KliveLLMResponse> QueryLLM(
            string prompt,
            string? sessionId = null,
            int? maxTokensOverride = null,
            string? systemPrompt = null,
            bool useFreeModel = false,
            CancellationToken cancellationToken = default,
            Action<string>? onToken = null,
            string? thinkingOverride = null)
        {
            if (useFreeModel)
            {
                return await QueryRemoteLLMAsync(
                    prompt,
                    sessionId,
                    maxTokensOverride,
                    systemPrompt,
                    forceFreeModel: true,
                    cancellationToken: cancellationToken,
                    onToken: onToken,
                    thinkingOverride: thinkingOverride);
            }

            if (await GetActiveProviderAsync() != LLMProvider.Local)
            {
                var hfResponse = await QueryRemoteLLMAsync(
                    prompt,
                    sessionId,
                    maxTokensOverride,
                    systemPrompt,
                    cancellationToken: cancellationToken,
                    onToken: onToken,
                    thinkingOverride: thinkingOverride);

                return hfResponse;
            }

            return await QueryLocalLLMAsync(
                prompt,
                sessionId,
                maxTokensOverride,
                cancellationToken: cancellationToken,
                onToken: onToken);
        }

        // ── Native structured tool calling ──
        // The caller (KliveAgentBrain) drives a tool loop:
        //   StartToolSession(id, systemPrompt) → AppendUserMessageToToolSession(id, msg)
        //   → QueryToolSessionAsync(id, tools)  ⟲  (if ToolCalls: AppendToolResult ×N, query again)
        // until QueryToolSessionAsync returns a response with no ToolCalls (final answer).

        /// <summary>True when the active provider can use native structured tool calling (any remote
        /// provider). The local LLama path has no tool channel and must use the {{{ }}} text protocol.</summary>
        public async Task<bool> SupportsNativeToolCallingAsync()
        {
            return await GetActiveProviderAsync() != LLMProvider.Local;
        }

        /// <summary>
        /// Negotiate what the active provider + a given model can accept, so a caller never attaches a
        /// modality the endpoint would reject. Pass a specific <paramref name="model"/> (e.g. a per-tier
        /// route) or null to probe the active provider's configured model. Local is always text-only; remote
        /// keeps NativeToolCalling=true (identical to <see cref="SupportsNativeToolCallingAsync"/>), while the
        /// image/audio/document flags come from the OpenRouter model catalog when reachable, else a
        /// conservative static family table. The result is safe to call on hot paths: the catalog is cached.
        /// </summary>
        public async Task<ModelCapabilities> GetModelCapabilitiesAsync(string? model = null, CancellationToken cancellationToken = default)
        {
            LLMProvider provider = await GetActiveProviderAsync();
            if (provider == LLMProvider.Local)
                return new ModelCapabilities(NativeToolCalling: false, ImageInput: false, AudioInput: false, DocumentInput: false, PromptCaching: false, Reasoning: false);

            string modelId = model?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(modelId))
            {
                modelId = provider switch
                {
                    LLMProvider.OpenRouter => await GetStringOmniSetting("OpenRouterModelID", DefaultOpenRouterModel, false, true),
                    LLMProvider.CustomOpenAI => await GetStringOmniSetting("CustomOpenAIModelID", DefaultCustomOpenAIModel, false, true),
                    LLMProvider.AIRouter => await GetStringOmniSetting("AIRouterModelID", DefaultAIRouterModel, false, true),
                    _ => await GetStringOmniSetting("HuggingFaceModelID", DefaultHuggingFaceModel, false, true),
                };
            }

            if (provider == LLMProvider.OpenRouter)
            {
                var catalog = await GetOpenRouterCapabilityCatalogAsync(cancellationToken);
                if (catalog != null && catalog.TryGetValue(modelId, out var caps))
                    return caps;
            }

            return StaticModelCapabilities(modelId);
        }

        /// <summary>Conservative capability guess from the model id's family, used for HuggingFace and when the
        /// OpenRouter catalog can't be reached. Asserts a modality only on positive family evidence (unknown →
        /// false) so audio/documents are never blind-sent; NativeToolCalling stays true for remote to preserve
        /// the pre-existing "all remote providers support tools" behaviour.</summary>
        internal static ModelCapabilities StaticModelCapabilities(string? modelId)
        {
            string id = (modelId ?? string.Empty).ToLowerInvariant();
            bool gemini = id.Contains("gemini");
            bool gptVision = id.Contains("gpt-4o") || id.Contains("gpt-4.1") || id.Contains("gpt-5")
                             || id.Contains("o1") || id.Contains("o3") || id.Contains("o4");
            bool claude = id.Contains("claude");
            bool ossVision = id.Contains("vision") || id.Contains("llava") || id.Contains("pixtral")
                             || id.Contains("-vl") || id.Contains("internvl");

            bool audio = gemini || id.Contains("audio") || id.Contains("-omni") || id.Contains("qwen2.5-omni");
            bool image = gemini || gptVision || claude || ossVision;
            bool document = gemini || claude; // native PDF/file input
            bool caching = claude;             // Anthropic prompt caching
            bool reasoning = gemini || id.Contains("o1") || id.Contains("o3") || id.Contains("o4")
                             || id.Contains("reason") || id.Contains("thinking") || id.Contains("-r1");

            return new ModelCapabilities(
                NativeToolCalling: true,
                ImageInput: image,
                AudioInput: audio,
                DocumentInput: document,
                PromptCaching: caching,
                Reasoning: reasoning);
        }

        /// <summary>Fetch + cache OpenRouter's /models catalog as id → capabilities (TTL-refreshed). Reads each
        /// model's architecture.input_modalities for image/audio/file support and supported_parameters for
        /// reasoning; NativeToolCalling is forced true (remote behaviour is unchanged and not every tool-capable
        /// model advertises it). Returns null on any failure so the caller falls back to the static family
        /// table — a capability probe must never break a request.</summary>
        private async Task<Dictionary<string, ModelCapabilities>?> GetOpenRouterCapabilityCatalogAsync(CancellationToken cancellationToken)
        {
            lock (capabilitiesLock)
            {
                if (openRouterCapabilities != null && DateTime.UtcNow - openRouterCapabilitiesFetchedUtc < CapabilitiesCacheTtl)
                    return openRouterCapabilities;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/models");
                string token = await GetOmniSetting("OpenRouterLLMToken", OmniSettingType.String, true, false);
                if (!string.IsNullOrWhiteSpace(token))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using var response = await capabilitiesHttp.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode) return null;

                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                var json = JObject.Parse(body);
                var map = new Dictionary<string, ModelCapabilities>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in (json["data"] as JArray) ?? new JArray())
                {
                    string entryId = (string?)entry["id"] ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(entryId)) continue;

                    var inputs = (entry["architecture"]?["input_modalities"] as JArray)?
                        .Select(x => (string?)x).Where(s => !string.IsNullOrEmpty(s))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var supported = (entry["supported_parameters"] as JArray)?
                        .Select(x => (string?)x).Where(s => !string.IsNullOrEmpty(s))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    map[entryId] = new ModelCapabilities(
                        NativeToolCalling: true,
                        ImageInput: inputs.Contains("image"),
                        AudioInput: inputs.Contains("audio"),
                        DocumentInput: inputs.Contains("file"),
                        PromptCaching: entryId.StartsWith("anthropic/", StringComparison.OrdinalIgnoreCase),
                        Reasoning: supported.Contains("reasoning") || supported.Contains("include_reasoning"));
                }

                lock (capabilitiesLock)
                {
                    openRouterCapabilities = map;
                    openRouterCapabilitiesFetchedUtc = DateTime.UtcNow;
                }
                return map;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ServiceLog($"GetModelCapabilities: OpenRouter catalog fetch failed ({ex.Message}); using static family fallback.");
                return null;
            }
        }

        /// <summary>Projects uses this to run OpenRouter-specific credit preflight only when its
        /// ordinary wake requests will actually use OpenRouter.</summary>
        public async Task<bool> IsOpenRouterActiveAsync()
        {
            return await GetActiveProviderAsync() == LLMProvider.OpenRouter;
        }

        /// <summary>Begin (or reset) a tool-calling session, seeding its structured message log with the
        /// system prompt. Subsequent turns are appended via AppendUserMessageToToolSession / AppendToolResult.</summary>
        public void StartToolSession(string sessionId, string? systemPrompt)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("sessionId required", nameof(sessionId));
            lock (sessions)
            {
                var s = new KliveLLMSession(this, false) { sessionId = sessionId };
                s.structuredMessages.Clear();
                if (!string.IsNullOrWhiteSpace(systemPrompt))
                    s.structuredMessages.Add(new HFWrapper.HFMessage { role = "system", content = systemPrompt + ThinkingDirective });
                sessions[sessionId] = s;
            }
        }

        /// <summary>
        /// Returns true when an existing structured tool session ends at a clean assistant turn and
        /// can therefore accept another user message without resetting its history. A session whose
        /// last assistant turn still has unanswered tool calls is deliberately rejected: continuing
        /// it would violate the provider's tool-call protocol.
        /// </summary>
        public bool CanContinueToolSession(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return false;
            lock (sessions)
            {
                if (!sessions.TryGetValue(sessionId, out var session) || session.structuredMessages.Count == 0)
                    return false;
                var last = session.structuredMessages[^1];
                return last.role == "assistant" && (last.tool_calls == null || last.tool_calls.Count == 0);
            }
        }

        /// <summary>The provider-reported size of the most recent request plus its completion.
        /// Returns zero before the session has completed its first model turn.</summary>
        public long GetToolSessionContextTokens(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return 0;
            lock (sessions)
            {
                return sessions.TryGetValue(sessionId, out var session)
                    ? (long)session.lastPromptTokens + session.lastCompletionTokens
                    : 0;
            }
        }

        // ── temporal grounding ──
        // Every inbound message (user turn / steering / tool result) is stamped with the wall-clock
        // at the moment it is appended, so the model can always read WHEN each thing it sees
        // happened — across long waits, queued deliveries and multi-hour agentic sessions the
        // stamps are what let it reason about elapsed time. Stamps are fixed at append time, so
        // provider prefix-caching of earlier turns is unaffected.
        internal static string StampIncoming(string? content) =>
            $"[{Data_Handling.TemporalFormat.NowStamp()}] {content ?? string.Empty}";

        /// <summary>Append a user turn to a tool-calling session, stamped with the receipt time.</summary>
        public void AppendUserMessageToToolSession(string sessionId, string content)
        {
            lock (sessions)
            {
                if (sessions.TryGetValue(sessionId, out var s))
                    s.structuredMessages.Add(new HFWrapper.HFMessage { role = "user", content = StampIncoming(content) });
            }
        }

        /// <summary>
        /// Append a user turn carrying text plus inline images (content-parts array). Used by the
        /// vision feedback loop: requires a vision-capable model on a remote provider. Images are
        /// sent as base64 data URIs.
        ///
        /// CONTEXT COMPACTION: a long computer-use task adds one screenshot per step; left unbounded the
        /// image history overflows the model's context window and it starts hallucinating stale on-screen
        /// state. So after adding the new screenshot, all but the most recent <paramref name="keepRecentImages"/>
        /// image messages are flattened to a one-line text placeholder — the model always keeps the latest
        /// view(s) for current state, at a fraction of the tokens.
        /// </summary>
        public void AppendUserContentToToolSession(string sessionId, string text, List<(byte[] data, string mimeType)> images, int keepRecentImages = 3)
        {
            var parts = new List<object>();
            // Stamp even image-only turns: the text part carries the capture-receipt time.
            parts.Add(new HFWrapper.HFTextPart { text = string.IsNullOrWhiteSpace(text) ? StampIncoming(null).TrimEnd() : StampIncoming(text) });
            foreach (var (data, mimeType) in images ?? new List<(byte[], string)>())
            {
                if (data == null || data.Length == 0) continue;
                parts.Add(new HFWrapper.HFImagePart
                {
                    image_url = new HFWrapper.HFImageUrl
                    {
                        url = $"data:{(string.IsNullOrWhiteSpace(mimeType) ? "image/png" : mimeType)};base64,{Convert.ToBase64String(data)}"
                    }
                });
            }
            lock (sessions)
            {
                if (sessions.TryGetValue(sessionId, out var s))
                {
                    s.structuredMessages.Add(new HFWrapper.HFMessage { role = "user", content = parts });
                    PruneOldToolImages(s, keepRecentImages);
                }
            }
        }

        /// <summary>Flatten all but the most recent <paramref name="keepRecent"/> image-bearing user messages
        /// to a tiny text placeholder, to keep the vision/computer-use context window from overflowing. Only
        /// touches standalone screenshot messages — assistant tool_calls and their tool results are untouched,
        /// so the tool-call protocol stays intact.</summary>
        internal static void PruneOldToolImages(KliveLLMSession s, int keepRecent)
        {
            if (keepRecent < 1) keepRecent = 1;
            var imageIdx = new List<int>();
            for (int i = 0; i < s.structuredMessages.Count; i++)
            {
                var m = s.structuredMessages[i];
                if (m.role == "user" && MessageHasImage(m)) imageIdx.Add(i);
            }
            // Batched for prefix-cache stability (see PromptPrefixStability): a flattened message is
            // one an earlier request already sent, so doing it one frame per turn re-prefills the
            // whole tail behind it every turn. An already-flattened message no longer carries an
            // image, so imageIdx counts exactly the frames still outstanding.
            int toPrune = imageIdx.Count - keepRecent;
            if (toPrune < PromptPrefixStability.MediaPruneBatch(keepRecent)) return;
            for (int k = 0; k < toPrune; k++)
            {
                s.structuredMessages[imageIdx[k]] = new HFWrapper.HFMessage
                {
                    role = "user",
                    content = "[Earlier screenshot omitted to conserve context — rely on the most recent screenshot for the current on-screen state.]"
                };
            }
        }

        private static bool MessageHasImage(HFWrapper.HFMessage m)
        {
            if (m.content is string) return false;
            if (m.content is System.Collections.IEnumerable parts)
                foreach (var p in parts)
                {
                    if (p is HFWrapper.HFImagePart) return true;
                    if (p is Newtonsoft.Json.Linq.JObject jo && (string?)jo["type"] == "image_url") return true;
                }
            return false;
        }

        /// <summary>
        /// Append a user turn carrying text plus inline audio clips (content-parts array). Sibling of
        /// <see cref="AppendUserContentToToolSession"/> for the audio modality: requires an audio-capable model
        /// on a remote provider — gate on <see cref="GetModelCapabilitiesAsync"/>(...).AudioInput BEFORE calling,
        /// or the provider will 400. Audio is sent as the RAW base64 payload with a container hint (wav/mp3/ogg…).
        ///
        /// CONTEXT COMPACTION: audio is even heavier than screenshots, so after adding the new clip all but the
        /// most recent <paramref name="keepRecentAudio"/> audio messages are flattened to a one-line text
        /// placeholder — the model keeps only the latest clip(s) for current state, at a fraction of the tokens.
        /// </summary>
        public void AppendAudioContentToToolSession(string sessionId, string text, List<(byte[] data, string format)> clips, int keepRecentAudio = 2)
        {
            var parts = new List<object>();
            // Stamp even audio-only turns: the text part carries the capture-receipt time (see StampIncoming).
            parts.Add(new HFWrapper.HFTextPart { text = string.IsNullOrWhiteSpace(text) ? StampIncoming(null).TrimEnd() : StampIncoming(text) });
            foreach (var (data, format) in clips ?? new List<(byte[], string)>())
            {
                if (data == null || data.Length == 0) continue;
                parts.Add(new HFWrapper.HFInputAudioPart
                {
                    input_audio = new HFWrapper.HFInputAudio
                    {
                        data = Convert.ToBase64String(data),
                        format = string.IsNullOrWhiteSpace(format) ? "wav" : format
                    }
                });
            }
            lock (sessions)
            {
                if (sessions.TryGetValue(sessionId, out var s))
                {
                    s.structuredMessages.Add(new HFWrapper.HFMessage { role = "user", content = parts });
                    PruneOldToolAudio(s, keepRecentAudio);
                }
            }
        }

        /// <summary>Flatten all but the most recent <paramref name="keepRecent"/> audio-bearing user messages to
        /// a tiny text placeholder, to keep the audio context window from overflowing. Only touches standalone
        /// audio messages — assistant tool_calls and their results are untouched, so the tool-call protocol
        /// stays intact. Audio analog of <see cref="PruneOldToolImages"/>.</summary>
        private static void PruneOldToolAudio(KliveLLMSession s, int keepRecent)
        {
            if (keepRecent < 1) keepRecent = 1;
            var audioIdx = new List<int>();
            for (int i = 0; i < s.structuredMessages.Count; i++)
            {
                var m = s.structuredMessages[i];
                if (m.role == "user" && MessageHasAudio(m)) audioIdx.Add(i);
            }
            // Batched for the same reason as PruneOldToolImages: rewriting one already-sent message
            // per turn pins the prefix-cache match rate low for the whole session.
            int toPrune = audioIdx.Count - keepRecent;
            if (toPrune < PromptPrefixStability.MediaPruneBatch(keepRecent)) return;
            for (int k = 0; k < toPrune; k++)
            {
                s.structuredMessages[audioIdx[k]] = new HFWrapper.HFMessage
                {
                    role = "user",
                    content = "[Earlier audio clip omitted to conserve context — rely on the most recent audio for the current state.]"
                };
            }
        }

        private static bool MessageHasAudio(HFWrapper.HFMessage m)
        {
            if (m.content is string) return false;
            if (m.content is System.Collections.IEnumerable parts)
                foreach (var p in parts)
                {
                    if (p is HFWrapper.HFInputAudioPart) return true;
                    if (p is Newtonsoft.Json.Linq.JObject jo && (string?)jo["type"] == "input_audio") return true;
                }
            return false;
        }

        /// <summary>Append a tool-result turn (role:"tool") answering a specific tool_call_id. After appending,
        /// OLD tool results are compacted (see <see cref="PruneOldToolResults"/>) so a long task's accumulated
        /// script/observation output doesn't bloat the window — the model keeps the most recent results in full.</summary>
        public void AppendToolResult(string sessionId, string toolCallId, string name, string content, int keepRecentFull = 16)
        {
            lock (sessions)
            {
                if (sessions.TryGetValue(sessionId, out var s))
                {
                    s.structuredMessages.Add(new HFWrapper.HFMessage
                    {
                        role = "tool",
                        tool_call_id = toolCallId,
                        name = name,
                        // Completion-time stamp: after a long-running tool (wait_for, a slow script)
                        // this is how the model knows how much wall-clock the step consumed.
                        content = StampIncoming(content)
                    });
                    PruneOldToolResults(s, keepRecentFull);
                }
            }
        }

        /// <summary>Shorten the CONTENT of tool-result messages older than the most recent <paramref name="keepRecent"/>,
        /// replacing a long output with a tiny stub. The message and its tool_call_id/name pairing are KEPT (so the
        /// tool-call protocol stays valid) — only the bulky, now-stale text is dropped. The model can always re-run a
        /// tool to fetch fresh data. This is the text analog of <see cref="PruneOldToolImages"/>.</summary>
        /// <remarks>
        /// BATCHED ON PURPOSE — see <see cref="PromptPrefixStability"/>. Stubbing one newly-old tool
        /// result per turn rewrites a message the previous request already sent, every turn, which
        /// moves the first differing byte permanently into the middle of the transcript and forces
        /// the provider to re-prefill everything behind it on every single turn. So we wait until a
        /// whole batch of results is due, then stub them together: most turns leave history
        /// byte-identical and extend a cached prefix, and the cost of the rewrite is amortised.
        /// </remarks>
        internal static void PruneOldToolResults(KliveLLMSession s, int keepRecent, int stubOverChars = 240)
        {
            if (keepRecent < 1) keepRecent = 1;
            var toolIdx = new List<int>();
            for (int i = 0; i < s.structuredMessages.Count; i++)
                if (s.structuredMessages[i].role == "tool") toolIdx.Add(i);

            // Only results that still hold their full text are actually rewritable; the ones stubbed
            // by an earlier batch are already at their final bytes and cost nothing to leave alone.
            // Counting those separately is what makes the hysteresis work: it measures OUTSTANDING
            // work, which resets after each batch, rather than transcript length, which never does.
            //
            // "Already stubbed" is decided by the notice, NOT by length: a stub is ~265 chars, which
            // is longer than stubOverChars, so a length test re-stubs its own output. That silently
            // re-trimmed every old result a second time (reporting a wrong trimmed-chars count) and,
            // worse here, meant the batch could never drain and history churned on every turn.
            int overLimit = toolIdx.Count - keepRecent;
            var due = new List<int>();
            for (int k = 0; k < overLimit; k++)
            {
                var candidate = s.structuredMessages[toolIdx[k]];
                if (candidate.content is string text
                    && text.Length > stubOverChars
                    && !text.Contains(ToolResultTrimNotice, StringComparison.Ordinal))
                {
                    due.Add(toolIdx[k]);
                }
            }
            if (due.Count < PromptPrefixStability.ToolResultPruneBatch(keepRecent)) return;

            foreach (int idx in due)
            {
                var m = s.structuredMessages[idx];
                if (m.content is not string str) continue;
                s.structuredMessages[idx] = new HFWrapper.HFMessage
                {
                    role = "tool",
                    tool_call_id = m.tool_call_id,
                    name = m.name,
                    content = str.Substring(0, 160).TrimEnd() +
                        $"\n[…{str.Length - 160} {ToolResultTrimNotice} — re-run the tool if you still need it.]"
                };
            }
        }

        /// <summary>Marks a tool result as already stubbed, so a later pass leaves it alone.</summary>
        private const string ToolResultTrimNotice = "chars of this earlier tool output trimmed to save context";

        // ── In-task context compaction ──
        // A long agentic task adds one assistant+tool exchange per step; left unbounded the request grows into
        // the hundreds-of-thousands of tokens, where the model loses the thread and MIS-PERCEIVES its own
        // earlier context (re-running discovery, repeating mistakes it already corrected). When the session
        // exceeds the budget we collapse the OLDER middle of the conversation into a compact deterministic
        // digest (the task + the tool actions taken and their key outcomes) and keep the system prompt + the
        // most recent exchanges verbatim — so the window stays sharp while the thread of work is preserved.
        private const int InTaskCompactAboveTokens = 70000;
        private const int InTaskKeepRecentMessages = 28;

        private static int EstimateMessageTokens(HFWrapper.HFMessage m)
        {
            int chars = HFWrapper.ContentToText(m.content)?.Length ?? 0;
            if (m.tool_calls != null)
                foreach (var tc in m.tool_calls)
                    chars += (tc.function?.name?.Length ?? 0) + (tc.function?.arguments?.Length ?? 0) + 8;
            // Three chars/token is intentionally more conservative than the common English prose
            // heuristic. Projects prompts contain source code, JSON and non-English text, all of
            // which tokenize more densely; OpenRouter's exact-token compression is the final guard.
            int tokens = (int)Math.Ceiling(chars / 3.0) + 8; // role/content envelope
            if (MessageHasImage(m)) tokens += 1100; // a downscaled screenshot the text estimate misses
            tokens += EstimateAudioTokens(m);       // audio parts the "[audio]" placeholder under-counts
            return tokens;
        }

        internal static int EstimateToolDefinitionTokens(IReadOnlyList<HFWrapper.HFTool>? tools)
        {
            if (tools == null || tools.Count == 0) return 0;
            string json = JsonConvert.SerializeObject(tools);
            return (int)Math.Ceiling(json.Length / 3.0) + tools.Count * 12;
        }

        /// <summary>
        /// Calculates how much of a model window can be occupied by messages after reserving the
        /// configured completion, serialized tool schemas, request envelope and 10% tokenizer margin.
        /// </summary>
        internal static int CalculateToolSessionMessageBudget(
            int contextWindowTokens,
            int maxOutputTokens,
            IReadOnlyList<HFWrapper.HFTool>? tools)
        {
            int context = Math.Max(1, contextWindowTokens);
            int safeTotal = Math.Max(1, (int)Math.Floor(context * 0.90));
            int output = Math.Clamp(maxOutputTokens, 0, safeTotal);
            int toolsAndEnvelope = EstimateToolDefinitionTokens(tools) + 256;
            return Math.Max(1, safeTotal - output - toolsAndEnvelope);
        }

        internal static int EstimateToolSessionTokens(IEnumerable<HFWrapper.HFMessage> messages)
        {
            int total = 0;
            foreach (var message in messages)
                total += EstimateMessageTokens(message);
            return total;
        }

        /// <summary>Deliberately-high token estimate for any audio content-parts in a message. The "[audio]"
        /// text placeholder contributes almost nothing to the char count, but providers bill audio by DURATION
        /// (tens of tokens/sec), so without this the compactor could under-count and overflow the window. Biased
        /// high, and the audio history is capped to a couple of clips by <see cref="PruneOldToolAudio"/>, so this
        /// only ever triggers compaction slightly early — never too late.</summary>
        private static int EstimateAudioTokens(HFWrapper.HFMessage m)
        {
            if (m.content is string || m.content is not System.Collections.IEnumerable parts) return 0;
            int tokens = 0;
            foreach (var p in parts)
            {
                if (p is HFWrapper.HFInputAudioPart ap)
                    tokens += 800 + (ap.input_audio?.data?.Length ?? 0) / 400;
                else if (p is Newtonsoft.Json.Linq.JObject jo && (string?)jo["type"] == "input_audio")
                    tokens += 800 + (((string?)jo["input_audio"]?["data"])?.Length ?? 0) / 400;
            }
            return tokens;
        }

        /// <param name="protectPrefixMessages">
        /// How many messages immediately AFTER the leading system block are protected from summarisation.
        ///
        /// A caller whose first user turn is a large rehydrated brief must pass a non-zero value. The
        /// Projects wake seed is one such message, carrying the agent's directives, grand plan, typed
        /// execution state, verified facts, dead ends, retrieval hits and recent-event window. Without
        /// this it is summarised like any other user turn — clipped to 240 characters by BuildCompacted —
        /// and the agent silently loses its entire rehydrated context mid-wake, which reliably produces
        /// repeated work and tool-call loops.
        /// </param>
        internal static bool CompactToolSessionIfNeeded(KliveLLMSession s, int aboveTokens, int keepRecent, int protectPrefixMessages = 0)
        {
            var msgs = s.structuredMessages;
            if (aboveTokens <= 0 || EstimateToolSessionTokens(msgs) <= aboveTokens) return false;

            int headStart = 0; // keep leading system message(s) verbatim
            while (headStart < msgs.Count && msgs[headStart].role == "system") headStart++;

            // Protected brief sits directly after the system block and is never summarised. Bounded so a
            // caller can never protect the whole conversation and defeat compaction entirely.
            int protectedEnd = Math.Clamp(headStart + Math.Max(0, protectPrefixMessages), headStart, Math.Max(headStart, msgs.Count - 1));

            // Compaction rewrites the middle of a transcript every request behind it has already
            // sent, so it is the most expensive thing we can do to a provider's prefix cache. Aim
            // BELOW the trigger rather than at it: landing just under the line means the next tool
            // result puts the session straight back over it and a long wake re-compacts — and so
            // re-prefills itself — on essentially every turn. See PromptPrefixStability.
            int target = PromptPrefixStability.CompactionTarget(aboveTokens);

            List<HFWrapper.HFMessage>? rebuilt = null;
            List<HFWrapper.HFMessage>? underTrigger = null;
            List<HFWrapper.HFMessage>? bestCompaction = null;
            int bestCompactionTokens = int.MaxValue;
            int availableTail = Math.Max(1, msgs.Count - protectedEnd);
            int tailToKeep = Math.Clamp(keepRecent, 1, availableTail);
            while (tailToKeep >= 1)
            {
                // Tail is advanced so it never begins on an orphan tool result whose assistant
                // tool_call would have been summarized away.
                int cut = Math.Max(protectedEnd, msgs.Count - tailToKeep);
                while (cut < msgs.Count && msgs[cut].role == "tool") cut++;
                if (cut > protectedEnd && cut < msgs.Count)
                {
                    var candidate = BuildCompacted(msgs, protectedEnd, cut);
                    int candidateTokens = EstimateToolSessionTokens(candidate);
                    if (candidateTokens < bestCompactionTokens)
                    {
                        bestCompaction = candidate;
                        bestCompactionTokens = candidateTokens;
                    }
                    // The loop walks tails largest-first, so the first candidate to fit each bar is
                    // the most history that bar allows. Remember the one that merely clears the
                    // trigger in case nothing reaches the headroom target.
                    if (underTrigger == null && candidateTokens <= aboveTokens) underTrigger = candidate;
                    if (candidateTokens <= target)
                    {
                        rebuilt = candidate;
                        break;
                    }
                }

                if (tailToKeep == 1) break;
                // Step down in quarters rather than halves. Halving overshoots the target and throws
                // away verbatim history the budget would have paid for; a finer walk keeps the
                // largest tail that still leaves the session room to grow before the next rewrite.
                int next = Math.Max(1, tailToKeep - Math.Max(1, tailToKeep / 4));
                if (next == tailToKeep) break;
                tailToKeep = next;
            }

            // Falling back to `underTrigger` preserves the old contract — get under the trigger by
            // whatever means — for the sessions whose protected prefix alone leaves no headroom.
            rebuilt ??= underTrigger ?? bestCompaction ?? new List<HFWrapper.HFMessage>(msgs);
            TrimStringMessagesToBudget(rebuilt, headStart, aboveTokens);
            s.structuredMessages = rebuilt;
            return true;

            // keepVerbatimBefore = the system block PLUS any protected brief; everything from there
            // up to `cut` is what gets summarised into the digest.
            static List<HFWrapper.HFMessage> BuildCompacted(
                List<HFWrapper.HFMessage> source,
                int keepVerbatimBefore,
                int cut)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("[Context compacted to keep the window sharp. Digest of EARLIER steps this task — the verbatim messages were summarised. Trust this plus the recent messages below; DON'T redo discovery already shown here, and re-run a tool only if you need fresh detail:]");
                for (int i = keepVerbatimBefore; i < cut; i++)
                {
                    var m = source[i];
                    var text = HFWrapper.ContentToText(m.content)?.Trim();
                    if (m.role == "user")
                    {
                        if (!string.IsNullOrEmpty(text)) sb.AppendLine("USER: " + Clip(text, 240));
                    }
                    else if (m.role == "assistant")
                    {
                        if (!string.IsNullOrEmpty(text)) sb.AppendLine("you: " + Clip(text, 200));
                        if (m.tool_calls != null)
                            foreach (var tc in m.tool_calls)
                                sb.AppendLine("  ⇒ called " + (tc.function?.name ?? "tool") + "(" + Clip(tc.function?.arguments ?? "", 120) + ")");
                    }
                    else if (m.role == "tool")
                    {
                        if (!string.IsNullOrEmpty(text)) sb.AppendLine("  ⤷ " + (m.name ?? "result") + ": " + Clip(FirstLine(text), 200));
                    }
                }
                string digest = sb.ToString();
                const int digestMaxChars = 6000;
                if (digest.Length > digestMaxChars)
                    digest = ClipMiddle(digest, digestMaxChars, "\n…(older steps elided)…\n");

                var output = new List<HFWrapper.HFMessage>(source.Count - (cut - keepVerbatimBefore) + 1);
                for (int i = 0; i < keepVerbatimBefore; i++) output.Add(source[i]);
                output.Add(new HFWrapper.HFMessage { role = "user", content = digest });
                for (int i = cut; i < source.Count; i++) output.Add(source[i]);
                return output;
            }

            static void TrimStringMessagesToBudget(
                List<HFWrapper.HFMessage> messages,
                int systemCount,
                int tokenBudget)
            {
                // If a single wake seed or recent tool result is itself too large, middle-truncate
                // non-system strings while retaining both the instructions at the front and the
                // trigger/latest evidence normally placed at the end. Tool IDs/calls stay untouched.
                for (int i = systemCount; i < messages.Count &&
                     EstimateToolSessionTokens(messages) > tokenBudget; i++)
                {
                    if (messages[i].content is not string text || text.Length <= 480) continue;
                    int excessTokens = EstimateToolSessionTokens(messages) - tokenBudget;
                    int targetChars = Math.Max(480, text.Length - excessTokens * 3 - 96);
                    if (targetChars >= text.Length) continue;
                    var m = messages[i];
                    messages[i] = new HFWrapper.HFMessage
                    {
                        role = m.role,
                        content = ClipMiddle(text, targetChars, "\n[…middle omitted to fit the active model context…]\n"),
                        tool_calls = m.tool_calls,
                        tool_call_id = m.tool_call_id,
                        name = m.name,
                        GemmaTextualToolTurn = m.GemmaTextualToolTurn,
                    };
                }
            }

            static string Clip(string x, int n) =>
                string.IsNullOrEmpty(x) ? "" : (x.Length <= n ? x : x.Substring(0, n) + "…");

            static string FirstLine(string x)
            {
                int nl = x.IndexOf('\n');
                return nl < 0 ? x : x.Substring(0, nl);
            }

            static string ClipMiddle(string text, int maxChars, string marker)
            {
                if (text.Length <= maxChars) return text;
                if (maxChars <= marker.Length) return text[..maxChars];
                int available = maxChars - marker.Length;
                int front = (int)Math.Ceiling(available * 0.58);
                int back = available - front;
                return text[..front] + marker + text[^back..];
            }
        }

        /// <summary>Send the session's current structured message log (plus the tool definitions) to the
        /// remote provider. Appends the assistant response — including any requested tool_calls — back to
        /// the log, and returns it. ToolCalls is populated when the model wants to invoke tools.</summary>
        public async Task<KliveLLMResponse> QueryToolSessionAsync(string sessionId, List<HFWrapper.HFTool> tools, int? maxTokensOverride = null, string? modelOverride = null, CancellationToken cancellationToken = default, Action<string>? onToken = null, string? thinkingOverride = null, Action<HFWrapper.HFToolCall>? onToolCallComplete = null, IReadOnlyList<string>? modelRoutes = null, int? compactAboveTokensOverride = null, int? compactKeepRecentMessagesOverride = null, int? contextWindowTokensOverride = null, bool enableOpenRouterContextCompression = false, ModelSamplingParameters? samplingParameters = null, int compactProtectPrefixMessages = 0)
        {
            KliveLLMSession session;
            List<HFWrapper.HFMessage> snapshot;
            bool contextWasCompacted = false;
            int? preflightMessageBudget = null;
            bool fixedRequestExceedsWindow = false;
            int irreducibleSystemTokens = 0;
            lock (sessions)
            {
                if (!sessions.TryGetValue(sessionId, out session))
                    return new KliveLLMResponse { Success = false, ErrorMessage = "Tool session not found. Call StartToolSession first.", SessionId = sessionId };
                // Most callers use the conservative global compactor. Long-running orchestrators can
                // supply their own context-window policy; zero disables this lossy compactor so their
                // measured provider usage and durable rollover mechanism remain authoritative.
                int compactAbove = compactAboveTokensOverride ?? InTaskCompactAboveTokens;
                int keepRecent = compactKeepRecentMessagesOverride ?? InTaskKeepRecentMessages;
                if (contextWindowTokensOverride is > 0)
                {
                    int safeTotal = Math.Max(1,
                        (int)Math.Floor(contextWindowTokensOverride.Value * 0.90));
                    int fixedRequestTokens = Math.Max(0, maxTokensOverride ?? 0)
                        + EstimateToolDefinitionTokens(tools)
                        + 256;
                    fixedRequestExceedsWindow = fixedRequestTokens >= safeTotal;
                    preflightMessageBudget = CalculateToolSessionMessageBudget(
                        contextWindowTokensOverride.Value,
                        Math.Max(0, maxTokensOverride ?? 0),
                        tools);
                    // A real provider window is a hard policy and therefore overrides an old
                    // compactAbove=0 "disable generic compaction" choice made by Projects.
                    compactAbove = compactAbove > 0
                        ? Math.Min(compactAbove, preflightMessageBudget.Value)
                        : preflightMessageBudget.Value;
                }
                if (compactAbove > 0)
                    contextWasCompacted = CompactToolSessionIfNeeded(session, compactAbove, keepRecent, compactProtectPrefixMessages);
                snapshot = new List<HFWrapper.HFMessage>(session.structuredMessages);
                irreducibleSystemTokens = snapshot
                    .TakeWhile(message => message.role == "system")
                    .Sum(EstimateMessageTokens);
            }

            if (fixedRequestExceedsWindow
                || (preflightMessageBudget.HasValue
                    && irreducibleSystemTokens > preflightMessageBudget.Value))
            {
                return new KliveLLMResponse
                {
                    Success = false,
                    ErrorMessage = $"The fixed system prompt, tool schemas and output reserve cannot fit the configured {contextWindowTokensOverride} token context window.",
                    SessionId = sessionId,
                    ContextWindowTokens = contextWindowTokensOverride,
                    ContextWasCompacted = contextWasCompacted,
                };
            }

            // If local deterministic compaction cannot fit an irreducible system prompt + tool
            // schema, only OpenRouter's exact-token compression may proceed. Without that backstop,
            // fail before dispatch instead of knowingly sending an over-window request.
            if (preflightMessageBudget.HasValue
                && EstimateToolSessionTokens(snapshot) > preflightMessageBudget.Value
                && !enableOpenRouterContextCompression)
            {
                return new KliveLLMResponse
                {
                    Success = false,
                    ErrorMessage = $"Tool session cannot fit the configured {contextWindowTokensOverride} token context window after safe compaction.",
                    SessionId = sessionId,
                    ContextWindowTokens = contextWindowTokensOverride,
                    ContextWasCompacted = contextWasCompacted,
                };
            }

            var response = await SendRemoteToolRequestAsync(snapshot, tools, maxTokensOverride, modelOverride: modelOverride, cancellationToken: cancellationToken, onToken: onToken, thinkingOverride: thinkingOverride, onToolCallComplete: onToolCallComplete, modelRoutes: modelRoutes, enableOpenRouterContextCompression: enableOpenRouterContextCompression, samplingParameters: samplingParameters);
            var msg = response.choices[0].message;
            var rawContent = HFWrapper.ContentToText(msg?.content);
            var nativeToolCalls = (msg?.tool_calls != null && msg.tool_calls.Count > 0) ? msg.tool_calls : null;
            var normalized = GemmaToolCallCompatibility.Normalize(rawContent, nativeToolCalls, tools);
            if (!normalized.Success)
            {
                // Do not append the raw envelope to the session: a later retry must start from the
                // last clean user/tool turn, and no caller should mistake provider protocol markup
                // for prose. Projects resets sessions that do not end at a clean assistant turn.
                return new KliveLLMResponse
                {
                    Success = false,
                    ErrorMessage = normalized.Error,
                    RawResponse = rawContent,
                    SessionId = sessionId,
                    PromptTokens = response.usage?.prompt_tokens ?? 0,
                    CachedPromptTokens = response.usage?.prompt_tokens_details?.cached_tokens ?? 0,
                    CompletionTokens = response.usage?.completion_tokens ?? 0,
                    GenerationId = response.id,
                    CostUsd = response.usage?.cost,
                    Model = response.model,
                    ContextWindowTokens = contextWindowTokensOverride,
                    ContextWasCompacted = contextWasCompacted,
                };
            }

            var content = normalized.Content;
            var toolCalls = normalized.ToolCalls;
            if (normalized.Adapted && onToolCallComplete != null && toolCalls != null)
                foreach (var call in toolCalls)
                    try { onToolCallComplete(call); } catch { }

            lock (sessions)
            {
                // Persist the assistant turn so the next round-trip carries the tool_calls it must answer.
                session.structuredMessages.Add(new HFWrapper.HFMessage
                {
                    role = "assistant",
                    content = content, // "" not null — strict providers reject null content
                    tool_calls = toolCalls,
                    GemmaTextualToolTurn = normalized.Adapted,
                });
                session.lastPromptTokens = response.usage?.prompt_tokens ?? 0;
                session.lastCompletionTokens = response.usage?.completion_tokens ?? 0;
                session.lastUpdated = DateTime.UtcNow;
            }

            return new KliveLLMResponse
            {
                Response = content,
                RawResponse = rawContent,
                SessionId = sessionId,
                Success = true,
                ToolCalls = toolCalls,
                PromptTokens = response.usage?.prompt_tokens ?? 0,
                    CachedPromptTokens = response.usage?.prompt_tokens_details?.cached_tokens ?? 0,
                CompletionTokens = response.usage?.completion_tokens ?? 0,
                GenerationId = response.id,
                CostUsd = response.usage?.cost,
                Model = response.model,
                ContextWindowTokens = contextWindowTokensOverride,
                ContextWasCompacted = contextWasCompacted,
            };
        }

        private async Task<KliveLLMResponse> QueryRemoteLLMAsync(string prompt, string? sessionId, int? maxTokensOverride, string? systemPrompt = null, bool forceFreeModel = false, CancellationToken cancellationToken = default, Action<string>? onToken = null, string? thinkingOverride = null)
        {
            // ensure session
            if (string.IsNullOrEmpty(sessionId))
            {
                sessionId = Guid.NewGuid().ToString();
                var s = new KliveLLMSession(this, false) { sessionId = sessionId };
                if (!string.IsNullOrWhiteSpace(systemPrompt))
                {
                    s.chatHistory.Messages.Clear();
                    s.chatHistory.AddMessage(AuthorRole.System, systemPrompt + ThinkingDirective);
                }
                lock (sessions)
                {
                    sessions[sessionId] = s;
                }
            }

            KliveLLMSession session;
            lock (sessions)
            {
                if (!sessions.TryGetValue(sessionId, out session))
                {
                    session = new KliveLLMSession(this, false) { sessionId = sessionId };
                    if (!string.IsNullOrWhiteSpace(systemPrompt))
                    {
                        session.chatHistory.Messages.Clear();
                        session.chatHistory.AddMessage(AuthorRole.System, systemPrompt + ThinkingDirective);
                    }
                    sessions[sessionId] = session;
                }
            }
            session.chatHistory.AddMessage(AuthorRole.User, StampIncoming(prompt));
            var response = await SendRemoteInferenceRequestAsync(session.chatHistory, maxTokensOverride, forceFreeModel, cancellationToken, onToken, thinkingOverride);
            {
                // Providers occasionally return an assistant turn with null content. Coalesce to an
                // empty string so it neither crashes downstream parsing nor poisons the session
                // history — a stored null re-serializes as "content": null on the next turn and is
                // rejected by strict providers (Alibaba/Qwen) with a 400.
                var content = HFWrapper.ContentToText(response.choices[0].message.content);
                session.chatHistory.AddMessage(AuthorRole.Assistant, content);
                session.lastUpdated = DateTime.UtcNow;

                return new KliveLLMResponse()
                {
                    Response = content,
                    RawResponse = content,
                    SessionId = sessionId,
                    Conversation = session.chatHistory,
                    Success = true,
                    PromptTokens = response.usage?.prompt_tokens ?? 0,
                    CachedPromptTokens = response.usage?.prompt_tokens_details?.cached_tokens ?? 0,
                    CompletionTokens = response.usage?.completion_tokens ?? 0,
                    GenerationId = response.id,
                    CostUsd = response.usage?.cost,
                };
            }
        }

        // Best-effort: open (and pool) the HTTPS connection to the active remote provider so the first
        // real inference call on a turn reuses a warm connection instead of paying the TLS/connect RTT.
        // Meant to be fired fire-and-forget at the START of an agent run so it overlaps with prompt
        // assembly. Errors are swallowed — a failed warm-up must never affect the actual request.
        public async Task WarmUpConnectionAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                if (client == null) return;
                if (await GetActiveProviderAsync() == LLMProvider.Local) return;
                RemoteLLMProviderConfiguration remoteProvider = await GetRemoteProviderConfigurationAsync();
                using var request = new HttpRequestMessage(HttpMethod.Get, remoteProvider.ChatCompletionsEndpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", remoteProvider.ApiKey);
                // We don't care about the status (a GET to the completions endpoint may 404/405) — the
                // point is to complete the TLS handshake and leave a pooled connection behind.
                using var _ = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch { /* warm-up is best-effort */ }
        }

        private async Task<RemoteLLMProviderConfiguration> GetRemoteProviderConfigurationAsync(bool forceFreeModel = false)
        {
            // Free-model fast path: always use OpenRouter with the FreeOpenRouterModelID setting,
            // regardless of the user's configured RemoteLLMProvider. Token still comes from
            // OpenRouterLLMToken because OpenRouter requires authentication for every request.
            if (forceFreeModel)
            {
                string freeToken = await GetOmniSetting("OpenRouterLLMToken", OmniSettingType.String, true, false);
                string freeModel = await GetStringOmniSetting("FreeOpenRouterModelID", DefaultFreeOpenRouterModel, false, true);
                if (string.IsNullOrWhiteSpace(freeToken))
                {
                    throw new InvalidOperationException("OpenRouterLLMToken is missing (required to use the free OpenRouter model).");
                }
                string freeServiceTier = await GetDropdownOmniSetting("OpenRouterServiceTier", "default", OpenRouterServiceTierOptions, false, false);
                return new RemoteLLMProviderConfiguration(
                    LLMProvider.OpenRouter,
                    "OpenRouter (free)",
                    "https://openrouter.ai/api/v1/chat/completions",
                    freeToken,
                    freeModel,
                    freeServiceTier);
            }

            string configuredProvider = await GetDropdownOmniSetting("RemoteLLMProvider", "HuggingFace", ProviderOptions, false, true);

            if (string.Equals(configuredProvider, "OpenRouter", StringComparison.OrdinalIgnoreCase))
            {
                string openRouterToken = await GetOmniSetting("OpenRouterLLMToken", OmniSettingType.String, true, false);
                string openRouterModel = await GetStringOmniSetting("OpenRouterModelID", DefaultOpenRouterModel, false, true);

                if (string.IsNullOrWhiteSpace(openRouterToken))
                {
                    throw new InvalidOperationException("OpenRouterLLMToken is missing.");
                }

                string openRouterServiceTier = await GetDropdownOmniSetting("OpenRouterServiceTier", "default", OpenRouterServiceTierOptions, false, false);
                return new RemoteLLMProviderConfiguration(
                    LLMProvider.OpenRouter,
                    "OpenRouter",
                    "https://openrouter.ai/api/v1/chat/completions",
                    openRouterToken,
                    openRouterModel,
                    openRouterServiceTier);
            }

            if (string.Equals(configuredProvider, CustomOpenAIProviderOption, StringComparison.OrdinalIgnoreCase))
            {
                string customToken = await GetOmniSetting("CustomOpenAILLMToken", OmniSettingType.String, true, false);
                string customEndpoint = ResolveChatCompletionsEndpoint(
                    await GetStringOmniSetting("CustomOpenAIEndpointURL", DefaultCustomOpenAIEndpoint, false, true));
                string customModel = await GetStringOmniSetting("CustomOpenAIModelID", DefaultCustomOpenAIModel, false, true);

                if (string.IsNullOrWhiteSpace(customEndpoint))
                {
                    throw new InvalidOperationException("CustomOpenAIEndpointURL is missing.");
                }

                if (string.IsNullOrWhiteSpace(customToken))
                {
                    throw new InvalidOperationException("CustomOpenAILLMToken is missing.");
                }

                return new RemoteLLMProviderConfiguration(
                    LLMProvider.CustomOpenAI,
                    CustomOpenAIProviderOption,
                    customEndpoint,
                    customToken,
                    customModel);
            }

            if (ParseProvider(configuredProvider) == LLMProvider.AIRouter)
            {
                string aiRouterToken = await GetOmniSetting("AIRouterLLMToken", OmniSettingType.String, true, false);
                string aiRouterModel = await GetStringOmniSetting("AIRouterModelID", DefaultAIRouterModel, false, true);

                if (string.IsNullOrWhiteSpace(aiRouterToken))
                {
                    throw new InvalidOperationException("AIRouterLLMToken is missing.");
                }

                return new RemoteLLMProviderConfiguration(
                    LLMProvider.AIRouter,
                    AIRouterProviderOption,
                    ResolveChatCompletionsEndpoint(AIRouterEndpoint),
                    aiRouterToken,
                    aiRouterModel);
            }

            if (string.Equals(configuredProvider, "Local", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Remote provider configuration was requested while RemoteLLMProvider is set to Local.");
            }

            string huggingFaceModel = await GetStringOmniSetting("HuggingFaceModelID", DefaultHuggingFaceModel, false, true);
            string huggingFaceApiKey = await GetOmniSetting("HuggingFaceLLMToken", OmniSettingType.String, true, false);

            if (string.IsNullOrWhiteSpace(huggingFaceApiKey))
            {
                throw new InvalidOperationException("HuggingFaceLLMToken is missing.");
            }

            return new RemoteLLMProviderConfiguration(
                LLMProvider.HuggingFace,
                "HuggingFace",
                "https://router.huggingface.co/v1/chat/completions",
                huggingFaceApiKey,
                huggingFaceModel);
        }

        // Bounded retry policy for the remote LLM call. Only transient failures (timeouts,
        // 408/429, 5xx, empty payloads, network resets) are retried; 4xx auth/validation errors
        // fail fast. Without this, a single blip from the provider aborts the whole agent loop.
        private const int RemoteInferenceMaxAttempts = 3;
        private const int RemoteInferenceBaseDelayMs = 800;

        // Upstream rate-limits / anti-abuse blocks (which OpenRouter frequently WRAPS as a top-level 400 —
        // see IsUpstreamRateLimitBody) clear after a short cool-off, so they warrant more attempts and a
        // longer wait than an ordinary blip. The per-try wait is capped so a single wake is never blocked
        // for minutes; if it still can't get through, the wake fails gracefully as before.
        private const int RateLimitMaxAttempts = 5;
        private const int RateLimitBaseDelayMs = 3000;
        private const int RateLimitMaxDelayMs = 20000;
        private const double AffordableTokenSafetyFraction = 0.90;

        // AIRouter's limits are all measured over a ROLLING MINUTE, which makes a 429 there a wait
        // rather than a wall: one cool-off inside the wake always clears it. So instead of the
        // ordinary policy — a few short retries, then surface a RateLimited failure that opens the
        // project circuit and defers the wake — we hold the whole shared queue off for the cool-off
        // and keep going. Bounded: at most this many attempts, each waiting at most one window.
        private const int AIRouterRateLimitMaxAttempts = 6;
        private static readonly TimeSpan AIRouterMaxCoolOff = TimeSpan.FromSeconds(75);
        private static readonly TimeSpan AIRouterDefaultCoolOff = TimeSpan.FromSeconds(10);

        // What a request with no explicit max_tokens is assumed to be allowed to generate, for
        // fair-use token accounting only. Uncapped requests routinely default to tens of thousands
        // of completion tokens, so reserving a small number would under-count the window.
        private const int FairUseDefaultCompletionReserve = 16_384;

        /// <summary>
        /// Pessimistic token size of one outbound request: everything being sent plus the entire
        /// completion being permitted. Over-counting is deliberate — the reservation is reconciled
        /// DOWN to the provider's own figure once the response lands, so the per-minute token window
        /// can never be over-committed during the gap between admission and reply.
        /// </summary>
        internal static long EstimateRequestTokens(HFWrapper.HFLLMInferenceRequest payload)
        {
            long total = 0;
            if (payload.messages != null)
                foreach (var message in payload.messages)
                    if (message != null)
                        total += EstimateMessageTokens(message);
            total += EstimateToolDefinitionTokens(payload.tools);
            total += payload.max_tokens is > 0 ? payload.max_tokens.Value : FairUseDefaultCompletionReserve;
            return total;
        }

        /// <summary>Queue behind the flat-fee router's fair-use envelope, or pass straight through for
        /// every other provider. Null means "no limiter applies", not "denied" — this never rejects.</summary>
        private async Task<AIRouterFairUseLease?> AcquireFairUseAsync(
            RemoteLLMProviderConfiguration remoteProvider,
            HFWrapper.HFLLMInferenceRequest payload,
            CancellationToken cancellationToken)
        {
            if (remoteProvider.Provider != LLMProvider.AIRouter) return null;
            return await FairUse.AcquireAsync(EstimateRequestTokens(payload), cancellationToken);
        }

        // ── AIRouter model resolution ──
        // AIRouter serves a small catalogue under its OWN names ("Qwen3.8", "DeepSeek-V4-Flash"),
        // not the "vendor/model" slugs OpenRouter uses. Every Projects role and KliveAgent route is
        // configured in slug form, so pointing the provider dropdown at AIRouter would send it a
        // model it has never heard of — a 404, classified ModelUnavailable, which opens the project
        // circuit and DEFERS the wake. That is the exact failure this integration exists to remove,
        // and it would hit on the very first turn after switching provider. So a request whose model
        // AIRouter does not serve is redirected to the configured AIRouterModelID instead.
        private static readonly HttpClient aiRouterCatalogHttp = new() { Timeout = TimeSpan.FromSeconds(10) };
        private readonly object aiRouterCatalogLock = new();
        private HashSet<string>? aiRouterModels;
        private DateTime aiRouterModelsFetchedUtc = DateTime.MinValue;
        private static readonly TimeSpan AIRouterCatalogTtl = TimeSpan.FromHours(6);

        /// <summary>
        /// A model id in "vendor/model" form is an OpenRouter/HuggingFace slug, which AIRouter's flat
        /// namespace never uses. Used as the offline verdict when the live catalogue is unreachable:
        /// better to serve the configured AIRouter model than to send a slug we can already tell is
        /// foreign and take a deferral for it.
        /// </summary>
        internal static bool LooksLikeForeignModelSlug(string? model) =>
            !string.IsNullOrWhiteSpace(model) && model.Contains('/', StringComparison.Ordinal);

        /// <summary>Chooses the model to actually send AIRouter: the caller's, when AIRouter serves it,
        /// otherwise the configured <c>AIRouterModelID</c>. Non-AIRouter providers are untouched.</summary>
        private async Task<string> ResolveAIRouterModelAsync(
            string? requested, RemoteLLMProviderConfiguration remoteProvider, CancellationToken cancellationToken)
        {
            string fallback = remoteProvider.Model;
            string model = (requested ?? string.Empty).Trim();
            if (model.Length == 0) return fallback;

            var catalog = await GetAIRouterCatalogAsync(remoteProvider, cancellationToken);
            bool served = catalog != null
                ? catalog.Contains(model)
                : !LooksLikeForeignModelSlug(model);
            if (served) return model;

            try
            {
                await ServiceLog($"AIRouter does not serve '{model}'; using the configured AIRouter model '{fallback}' " +
                    "instead of failing the turn on an unknown model.");
            }
            catch { }
            return fallback;
        }

        /// <summary>Fetches + caches AIRouter's advertised model ids. Returns null on any failure so the
        /// caller falls back to the slug heuristic — a catalogue probe must never break a request.</summary>
        private async Task<HashSet<string>?> GetAIRouterCatalogAsync(
            RemoteLLMProviderConfiguration remoteProvider, CancellationToken cancellationToken)
        {
            lock (aiRouterCatalogLock)
            {
                if (aiRouterModels != null && DateTime.UtcNow - aiRouterModelsFetchedUtc < AIRouterCatalogTtl)
                    return aiRouterModels;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, AIRouterEndpoint.TrimEnd('/') + "/models");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", remoteProvider.ApiKey);
                using var response = await aiRouterCatalogHttp.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode) return null;

                var json = JObject.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                var ids = ((json["data"] as JArray) ?? new JArray())
                    .Select(entry => (string?)entry["id"])
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
                if (ids.Count == 0) return null;

                lock (aiRouterCatalogLock)
                {
                    aiRouterModels = ids;
                    aiRouterModelsFetchedUtc = DateTime.UtcNow;
                }
                return ids;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                try { await ServiceLog($"AIRouter model catalogue unavailable ({ex.Message}); using the slug heuristic."); } catch { }
                return null;
            }
        }

        /// <summary>Reconcile a fair-use reservation against the provider's reported usage. A response
        /// without a usage block leaves the pessimistic reservation standing — under-counting the
        /// window is the one failure mode this whole mechanism exists to prevent.</summary>
        private void ReportFairUseUsage(AIRouterFairUseLease? lease, HFWrapper.HFLLMInferenceResponse.UsageDetails? usage)
        {
            if (lease == null || usage == null) return;
            long total = usage.total_tokens > 0
                ? usage.total_tokens
                : (long)usage.prompt_tokens + usage.completion_tokens;
            if (total > 0) lease.ReportActualTokens(total);

            // A lease only exists for AIRouter, so this meters exactly the traffic whose prefill we
            // are responsible for. The router already tells us how much of each prompt it served from
            // cache; until now nothing read it, which is why a prompt-assembly regression could run
            // for hours and reach us only as a suspension notice. See PrefixCacheMeter.
            string? warning = PrefixCacheMeter.Shared.Record(
                usage.prompt_tokens, usage.prompt_tokens_details?.cached_tokens ?? 0);
            if (warning != null)
            {
                try { _ = ServiceLog(warning); } catch { }
            }
        }

        /// <summary>The cool-off to hold the shared AIRouter queue for after a 429 that slipped through
        /// admission control (another client on the same key, or a window boundary landing badly).</summary>
        private static TimeSpan ResolveAIRouterCoolOff(TimeSpan? providerRetryAfter)
        {
            var coolOff = providerRetryAfter is { } requested && requested > TimeSpan.Zero
                ? requested
                : AIRouterDefaultCoolOff;
            return coolOff > AIRouterMaxCoolOff ? AIRouterMaxCoolOff : coolOff;
        }

        private async Task<HFWrapper.HFLLMInferenceResponse> SendRemoteInferenceRequestAsync(ChatHistory messages, int? maxTokensOverride, bool forceFreeModel = false, CancellationToken cancellationToken = default, Action<string>? onToken = null, string? thinkingOverride = null)
        {
            RemoteLLMProviderConfiguration remoteProvider = await GetRemoteProviderConfigurationAsync(forceFreeModel);

            HFWrapper.HFLLMInferenceRequest payload = new HFWrapper.HFLLMInferenceRequest()
            {
                model = remoteProvider.Model,
                stream = false,
                max_tokens = maxTokensOverride,
            };
            ApplyServiceTier(ref payload, remoteProvider);
            ApplyUsageAccounting(ref payload, remoteProvider);
            ApplyThinkingPreference(ref payload, remoteProvider, thinkingOverride);
            payload.BuildMessagesFromChatHistory(messages);
            ApplyPromptCaching(ref payload, remoteProvider);

            return await SendInferencePayloadAsync(remoteProvider, payload, cancellationToken, onToken);
        }

        /// <summary>
        /// Tool-calling variant: builds the payload from a structured message list (which can include
        /// assistant-with-tool_calls and role:"tool" turns) and attaches the tool definitions. Shares
        /// the same provider config + retry policy as the plain text path.
        /// </summary>
        private async Task<HFWrapper.HFLLMInferenceResponse> SendRemoteToolRequestAsync(
            List<HFWrapper.HFMessage> structuredMessages,
            List<HFWrapper.HFTool> tools,
            int? maxTokensOverride,
            bool forceFreeModel = false,
            string? modelOverride = null,
            CancellationToken cancellationToken = default,
            Action<string>? onToken = null,
            string? thinkingOverride = null,
            Action<HFWrapper.HFToolCall>? onToolCallComplete = null,
            IReadOnlyList<string>? modelRoutes = null,
            bool enableOpenRouterContextCompression = false,
            ModelSamplingParameters? samplingParameters = null)
        {
            RemoteLLMProviderConfiguration remoteProvider = await GetRemoteProviderConfigurationAsync(forceFreeModel);

            // An explicit override pins a route that already succeeded earlier in this wake. Otherwise
            // use the first configured route, then the provider default. Remaining routes become
            // OpenRouter's own fallback list (see ApplyModelFallback) rather than an app-side retry loop.
            string primaryModel = !string.IsNullOrWhiteSpace(modelOverride)
                ? modelOverride.Trim()
                : (modelRoutes != null && modelRoutes.Count > 0 && !string.IsNullOrWhiteSpace(modelRoutes[0])
                    ? modelRoutes[0].Trim()
                    : remoteProvider.Model);

            // AIRouter names its models in its own flat namespace, so a caller's OpenRouter-style
            // route would 404 there. Redirect to the configured AIRouter model rather than let an
            // unknown-model failure defer the wake. Every other provider keeps the caller's route.
            if (remoteProvider.Provider == LLMProvider.AIRouter)
                primaryModel = await ResolveAIRouterModelAsync(primaryModel, remoteProvider, cancellationToken);

            HFWrapper.HFLLMInferenceRequest payload = new HFWrapper.HFLLMInferenceRequest()
            {
                model = primaryModel,
                stream = false,
                max_tokens = maxTokensOverride,
                tools = tools,
            };
            ApplyModelFallback(ref payload, remoteProvider, modelRoutes);
            ApplyServiceTier(ref payload, remoteProvider);
            ApplyUsageAccounting(ref payload, remoteProvider);
            ApplyThinkingPreference(ref payload, remoteProvider, thinkingOverride);
            ApplyContextCompression(ref payload, remoteProvider, enableOpenRouterContextCompression);
            ApplySamplingParameters(ref payload, remoteProvider, samplingParameters);
            ApplyTextualToolProtocolStops(ref payload, remoteProvider, structuredMessages);
            // A raw Gemma call is only half of that model's control-token handshake. Once its tools
            // have run, fold the synthetic assistant/tool messages into a native tool-response turn
            // before calling the model again. Ordinary provider-native calls remain byte-for-byte on
            // the standard OpenAI path.
            var providerMessages = GemmaToolCallCompatibility.PrepareContinuationMessages(structuredMessages);
            payload.BuildMessagesFromList(providerMessages);
            ApplyPromptCaching(ref payload, remoteProvider);

            return await SendInferencePayloadAsync(remoteProvider, payload, cancellationToken, onToken, onToolCallComplete);
        }

        // Marker that KliveAgent's BuildSystemPrompt inserts between the STABLE system-prompt skeleton
        // (personality + rules + patterns + memory discipline — identical across tasks) and the VOLATILE
        // tail (task tool guide + repo map + memories). On OpenRouter the system message is split here and
        // the skeleton is tagged with cache_control so its prefill is served from cache on every turn after
        // the first. On any other provider the marker is simply stripped so the model never sees it.
        public const string CacheBreakpointMarker = "KLIVE_CACHE_BREAKPOINT";

        private static readonly object EphemeralCacheControl = new { type = "ephemeral" };

        /// <summary>Rewrites the leading system message to enable OpenRouter prompt caching. The stable
        /// skeleton (text before <see cref="CacheBreakpointMarker"/>, or the whole system message when the
        /// marker is absent) becomes a content-part tagged with cache_control; the volatile remainder
        /// follows as a plain part. For non-OpenRouter providers this only strips the marker.</summary>
        private static void ApplyPromptCaching(ref HFWrapper.HFLLMInferenceRequest payload, RemoteLLMProviderConfiguration remoteProvider)
        {
            if (payload.messages == null || payload.messages.Length == 0) return;

            var system = payload.messages.FirstOrDefault(m => m != null && m.role == "system");
            if (system == null) return;

            string text = HFWrapper.ContentToText(system.content);
            if (string.IsNullOrEmpty(text)) return;

            int markerIdx = text.IndexOf(CacheBreakpointMarker, StringComparison.Ordinal);
            string stable = markerIdx >= 0 ? text.Substring(0, markerIdx) : text;
            string volatileTail = markerIdx >= 0 ? text.Substring(markerIdx + CacheBreakpointMarker.Length) : string.Empty;

            if (remoteProvider.Provider != LLMProvider.OpenRouter)
            {
                // No native caching switch here — just guarantee the marker can never reach the model.
                if (markerIdx >= 0) system.content = stable + volatileTail;
                return;
            }

            var parts = new List<object>
            {
                new HFWrapper.HFTextPart { text = stable, cache_control = EphemeralCacheControl }
            };
            if (!string.IsNullOrEmpty(volatileTail))
                parts.Add(new HFWrapper.HFTextPart { text = volatileTail });

            system.content = parts;

            ApplyConversationCacheBreakpoint(payload);
        }

        /// <summary>
        /// Places a second cache breakpoint at the END of the settled conversation, so the growing
        /// tool transcript is served from cache instead of being re-billed in full on every turn.
        ///
        /// Caching the system message alone only ever covered the fixed prefix; in an agentic tool loop
        /// the conversation is the part that grows, and it was 100% uncached — which is why a Projects
        /// wake bills prompt tokens far in excess of what it generates.
        ///
        /// The breakpoint goes on the last USER-role message. Anthropic (via OpenRouter) caches the whole
        /// prefix up to and including a marked block, and a user turn is the shape that survives
        /// OpenRouter's OpenAI-compat translation most reliably — assistant/tool turns are reshaped more
        /// aggressively. Anthropic allows four breakpoints; this is the second.
        /// </summary>
        internal static void ApplyConversationCacheBreakpoint(HFWrapper.HFLLMInferenceRequest payload)
        {
            // The final user turn is the live one; marking it would write a new cache entry every turn
            // and never read one back. Mark the previous user turn instead — by the next request the
            // conversation has grown past it, so it is a stable prefix boundary.
            int lastUser = -1, previousUser = -1;
            for (int i = 0; i < payload.messages.Length; i++)
            {
                var m = payload.messages[i];
                if (m == null || m.role != "user") continue;
                previousUser = lastUser;
                lastUser = i;
            }
            if (previousUser < 0) return;

            var target = payload.messages[previousUser];

            // CRITICAL: BuildMessagesFromList copies HFMessage objects but SHARES their content
            // reference with the live session. Tagging in place would mutate the caller's stored
            // history — poisoning the session and changing earlier turns between requests. Always
            // build a fresh content object here.
            if (target.content is string s)
            {
                target.content = new List<object>
                {
                    new HFWrapper.HFTextPart { text = s, cache_control = EphemeralCacheControl },
                };
                return;
            }

            if (target.content is System.Collections.IEnumerable existing and not string)
            {
                var copy = new List<object>();
                foreach (var part in existing) copy.Add(part);
                if (copy.Count == 0) return;

                // Only a TEXT part can carry cache_control; an image/audio part cannot. If the last part
                // is not text, append a tiny marker part rather than corrupting a media part.
                if (copy[^1] is HFWrapper.HFTextPart lastText)
                {
                    copy[^1] = new HFWrapper.HFTextPart
                    {
                        text = lastText.text,
                        cache_control = EphemeralCacheControl,
                    };
                }
                else
                {
                    copy.Add(new HFWrapper.HFTextPart { text = " ", cache_control = EphemeralCacheControl });
                }
                target.content = copy;
            }
        }

        /// <summary>
        /// Turns an ordered route list into OpenRouter-native fallback routing. The request's
        /// <c>model</c> remains the primary and only routes after it are sent in the <c>models</c> array,
        /// which OpenRouter treats as its ordered fallback list. Repeating the primary in <c>models</c>
        /// makes OpenRouter retry the already-rate-limited route before it reaches a real backup.
        /// This replaces app-side "try model A, on failure re-query with model B" loops. Only applied for
        /// OpenRouter (the only provider that understands the parameter) and only when at least one
        /// distinct backup route exists.
        ///
        /// On every other provider — HuggingFace and a custom OpenAI-compatible endpoint — a caller's route
        /// list still selects the model (route 0 becomes the request's `model`), but the backup entries are
        /// simply not sent: a strict endpoint would reject the unknown parameter. So a Projects role
        /// configured with several routes runs on its PRIMARY route there, with no provider-side failover.
        /// </summary>
        internal static void ApplyModelFallback(ref HFWrapper.HFLLMInferenceRequest payload,
            RemoteLLMProviderConfiguration remoteProvider, IReadOnlyList<string>? modelRoutes)
        {
            if (remoteProvider.Provider != LLMProvider.OpenRouter || modelRoutes == null) return;
            var routes = modelRoutes
                .Select(m => (m ?? "").Trim())
                .Where(m => m.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            string? primary = payload.model;
            int primaryIndex = routes.FindIndex(m => string.Equals(m, primary, StringComparison.OrdinalIgnoreCase));
            var fallbacks = primaryIndex < 0
                ? routes.Take(3).ToList()
                : routes.Skip(primaryIndex + 1).Concat(routes.Take(primaryIndex)).Take(3).ToList();
            if (fallbacks.Count > 0) payload.models = fallbacks;
        }

        /// <summary>
        /// Attaches the caller's pinned sampling parameters. Only values that were actually set are
        /// written, so an unpinned parameter stays absent from the request and the provider's default
        /// applies — a caller passing null produces the identical payload it did before.
        ///
        /// Local has no HTTP payload at all, and the OpenRouter-only extensions (top_k,
        /// repetition_penalty, min_p, top_a) are withheld from HuggingFace and custom OpenAI-compatible
        /// endpoints, matching the rule every other proprietary field here follows: a strict endpoint
        /// must never see a parameter it does not know.
        /// </summary>
        internal static void ApplySamplingParameters(
            ref HFWrapper.HFLLMInferenceRequest payload,
            RemoteLLMProviderConfiguration remoteProvider,
            ModelSamplingParameters? parameters)
        {
            if (parameters == null || parameters.IsEmpty) return;

            payload.temperature = parameters.Temperature;
            payload.top_p = parameters.TopP;
            payload.frequency_penalty = parameters.FrequencyPenalty;
            payload.presence_penalty = parameters.PresencePenalty;
            payload.seed = parameters.Seed;

            if (remoteProvider.Provider != LLMProvider.OpenRouter) return;
            payload.top_k = parameters.TopK;
            payload.repetition_penalty = parameters.RepetitionPenalty;
            payload.min_p = parameters.MinP;
            payload.top_a = parameters.TopA;
            // Route-level values are applied after the global OpenRouter configuration. An explicit
            // `default` service tier therefore resets a globally pinned flex/priority tier for this route.
            if (!string.IsNullOrWhiteSpace(parameters.ServiceTier))
                payload.service_tier = parameters.ServiceTier;

            // OpenRouter's `provider.only` is deliberately restrictive: once Klives selects one or
            // more model-aware provider tags, every unselected endpoint is excluded for this request.
            if (parameters.ProviderOnly is { Count: > 0 })
            {
                payload.provider = new HFWrapper.HFProviderRouting
                {
                    only = parameters.ProviderOnly.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                };
            }
        }

        /// <summary>
        /// Gives a textual-tool-protocol model an end-of-turn the gateway can actually act on.
        ///
        /// A model that emits calls as Gemma control tokens never produces the finish signal an
        /// OpenAI-compatible gateway watches for, so generation does not stop when the call is complete —
        /// it continues straight into a fabricated tool result and the turns after it, all the way to
        /// max_tokens. That is a multi-minute generation whose useful part was the first few hundred
        /// tokens, and whose tail is invented evidence the agent then acts on. Stopping at the protocol's
        /// own terminators ends the turn where the call ends.
        ///
        /// Applied only when the route speaks that protocol, so a native tool-calling request — which has
        /// a real finish_reason and may legitimately batch several calls in one turn — is untouched. Local
        /// has no HTTP payload at all.
        /// </summary>
        internal static void ApplyTextualToolProtocolStops(
            ref HFWrapper.HFLLMInferenceRequest payload,
            RemoteLLMProviderConfiguration remoteProvider,
            IEnumerable<HFWrapper.HFMessage>? structuredMessages)
        {
            if (remoteProvider.Provider == LLMProvider.Local) return;
            if (!GemmaToolCallCompatibility.SpeaksTextualToolProtocol(
                    payload.model, payload.models, structuredMessages))
                return;

            payload.stop = GemmaToolCallCompatibility.StopSequences.ToList();
        }

        private static void ApplyServiceTier(ref HFWrapper.HFLLMInferenceRequest payload, RemoteLLMProviderConfiguration remoteProvider)
        {
            if (remoteProvider.Provider == LLMProvider.OpenRouter
                && !string.IsNullOrWhiteSpace(remoteProvider.ServiceTier)
                && !string.Equals(remoteProvider.ServiceTier, "default", StringComparison.OrdinalIgnoreCase))
            {
                payload.service_tier = remoteProvider.ServiceTier;
            }
        }

        // Ask OpenRouter to report the REAL per-request cost back in the response usage object
        // (usage.cost, in credits == USD). This is the authoritative figure the Projects budget ledger
        // meters against — accurate for whatever model is actually in use, instead of a flat per-million
        // estimate that is wildly wrong for cheap/free models. It arrives in the same response (and the
        // final streamed usage chunk), so there's no separate round-trip. No-op for other providers.
        private static void ApplyUsageAccounting(ref HFWrapper.HFLLMInferenceRequest payload, RemoteLLMProviderConfiguration remoteProvider)
        {
            if (remoteProvider.Provider == LLMProvider.OpenRouter)
                payload.usage = new { include = true };
        }

        /// <summary>
        /// Enables OpenRouter's exact-token context compression as a final safety net. Projects first
        /// performs deterministic local compaction so its durable digest/recent-tail semantics are
        /// preserved; this plugin handles tokenizer variance or an irreducible request envelope that
        /// local character estimates cannot measure exactly. It is never sent to other providers.
        /// </summary>
        internal static void ApplyContextCompression(
            ref HFWrapper.HFLLMInferenceRequest payload,
            RemoteLLMProviderConfiguration remoteProvider,
            bool enabled)
        {
            if (!enabled || remoteProvider.Provider != LLMProvider.OpenRouter) return;
            payload.plugins = new List<HFWrapper.HFPlugin>
            {
                new() { id = "context-compression", enabled = true }
            };
        }

        /// <summary>True when the configured thinking type means "no reasoning at all".</summary>
        private bool ThinkingDisabled => string.Equals(thinkingType, "Off", StringComparison.OrdinalIgnoreCase);

        /// <summary>Suffix folded into the system prompt so Qwen-family models (and the local model)
        /// honour the thinking preference even on providers without a native reasoning switch. Only
        /// "Off" needs a directive (/no_think); the effort levels rely on the provider/model default.</summary>
        internal string ThinkingDirective => ThinkingDisabled ? " /no_think" : string.Empty;

        // Canonical low→high ordering of the reasoning effort levels. Used to clamp a per-call
        // request against the user's configured ceiling (ThinkingType).
        private static readonly string[] ThinkingLevelOrder = { "off", "low", "medium", "high" };

        private static int ThinkingRank(string? level)
        {
            if (string.IsNullOrWhiteSpace(level)) return -1;
            for (int i = 0; i < ThinkingLevelOrder.Length; i++)
                if (string.Equals(ThinkingLevelOrder[i], level, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        /// <summary>Resolves the effective reasoning effort for a single call. The per-call
        /// <paramref name="requested"/> level is clamped so it never exceeds the user's configured
        /// ThinkingType (the ceiling); a null/unknown request falls back to the ceiling. This keeps the
        /// user's setting authoritative while letting the agent dial effort DOWN for cheap turns and UP
        /// (toward the ceiling) for hard ones. Returns a canonical lowercase level (off/low/medium/high).</summary>
        internal string ResolveThinkingLevel(string? requested)
        {
            int ceiling = ThinkingRank(thinkingType);
            if (ceiling < 0) ceiling = ThinkingRank("Medium"); // defensive: unknown setting → Medium
            int want = ThinkingRank(requested);
            int effective = want < 0 ? ceiling : Math.Min(want, ceiling);
            return ThinkingLevelOrder[effective];
        }

        /// <summary>Maps the (clamped) thinking level to OpenRouter's native reasoning control. Other
        /// providers rely on <see cref="ThinkingDirective"/> already present in the system prompt.
        /// <paramref name="thinkingOverride"/> is the agent's per-call request (null = use the ceiling).</summary>
        private void ApplyThinkingPreference(ref HFWrapper.HFLLMInferenceRequest payload, RemoteLLMProviderConfiguration remoteProvider, string? thinkingOverride = null)
        {
            if (remoteProvider.Provider != LLMProvider.OpenRouter) return;
            string effective = ResolveThinkingLevel(thinkingOverride);
            payload.reasoning = string.Equals(effective, "off", StringComparison.OrdinalIgnoreCase)
                ? new { enabled = false }
                : new { effort = effective };
        }

        // Chooses the transport: when a token sink is supplied we stream (stream=true + SSE) so the UI
        // can show tokens as they generate; otherwise we use the buffered request with its retry policy.
        // If a streaming attempt fails BEFORE any token is emitted (connect/headers/auth), we fall back
        // to the buffered path so streaming never reduces reliability.
        private async Task<HFWrapper.HFLLMInferenceResponse> SendInferencePayloadAsync(
            RemoteLLMProviderConfiguration remoteProvider,
            HFWrapper.HFLLMInferenceRequest payload,
            CancellationToken cancellationToken,
            Action<string>? onToken,
            Action<HFWrapper.HFToolCall>? onToolCallComplete = null)
        {
            if (onToken == null)
                return await SendPayloadWithRetryAsync(remoteProvider, payload, cancellationToken);

            try
            {
                return await SendStreamingPayloadAsync(remoteProvider, payload, onToken, cancellationToken, onToolCallComplete);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                try { await ServiceLog($"Streaming attempt failed before output ({ex.Message}); falling back to non-streaming."); } catch { }
                payload.stream = false;
                payload.stream_options = null;
                return await SendPayloadWithRetryAsync(remoteProvider, payload, cancellationToken);
            }
        }

        // Streams an OpenAI-compatible chat completion: posts stream=true, reads the SSE body as it
        // arrives, invokes onToken per content delta, merges any tool_call deltas by index, and
        // synthesizes a normal HFLLMInferenceResponse so the rest of the pipeline is unchanged. A
        // failure AFTER some content has streamed returns the partial result (no exception) rather than
        // discarding the user-visible progress; a failure before any content throws (caller falls back).
        private async Task<HFWrapper.HFLLMInferenceResponse> SendStreamingPayloadAsync(
            RemoteLLMProviderConfiguration remoteProvider,
            HFWrapper.HFLLMInferenceRequest payload,
            Action<string> onToken,
            CancellationToken cancellationToken,
            Action<HFWrapper.HFToolCall>? onToolCallComplete = null)
        {
            payload.stream = true;
            payload.stream_options = new { include_usage = true };
            string payloadJson = JsonConvert.SerializeObject(payload);

            // A streamed generation occupies a parallel slot for its WHOLE duration, not just the
            // request/response round-trip, so the lease is method-scoped: it is released when the SSE
            // body ends. Non-flat-fee providers get null and pass straight through.
            using AIRouterFairUseLease? fairUse = await AcquireFairUseAsync(remoteProvider, payload, cancellationToken);

            using var request = new HttpRequestMessage(HttpMethod.Post, remoteProvider.ChatCompletionsEndpoint)
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", remoteProvider.ApiKey);
            if (remoteProvider.Provider == LLMProvider.OpenRouter)
            {
                request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/Klivess/Omnipotent");
                request.Headers.TryAddWithoutValidation("X-Title", "Omnipotent");
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                // The caller falls back to the buffered path on any streaming failure. If this was a
                // fair-use rejection, hold the shared queue first so that fallback waits out the
                // cool-off instead of immediately reproducing the same 429.
                if (remoteProvider.Provider == LLMProvider.AIRouter
                    && ((int)response.StatusCode == 429 || IsUpstreamRateLimitBody(body)))
                    FairUse.Penalize(ResolveAIRouterCoolOff(GetProviderRetryAfter(response)));
                throw new HttpRequestException(
                    $"{remoteProvider.DisplayName} streaming request failed with status {(int)response.StatusCode} ({response.ReasonPhrase}). Body: {body}");
            }

            var contentSb = new StringBuilder();
            var toolCallsByIndex = new SortedDictionary<int, HFWrapper.HFToolCall>();
            var argsByIndex = new Dictionary<int, StringBuilder>();
            HFWrapper.HFLLMInferenceResponse.UsageDetails usage = null;
            string finishReason = null;
            // The generation id and the model that actually served the request (which may be an
            // OpenRouter fallback, not the one we asked for). Callers meter cost and pin routes on
            // these, so a streamed response must report them exactly as the buffered path does.
            string generationId = null;
            string servedModel = null;

            // #9 Speculative dispatch: notify the caller the moment a tool_call's arguments are fully
            // streamed so it can start executing that tool while the model is still generating later
            // tool_calls. An index is COMPLETE once a higher index begins streaming (OpenAI emits
            // tool_calls in index order); at stream end every remaining index is complete.
            var firedToolCalls = new HashSet<int>();
            void FireCompletedToolCalls(bool all)
            {
                if (onToolCallComplete == null || toolCallsByIndex.Count == 0) return;
                int maxIdx = toolCallsByIndex.Keys.Max();
                foreach (var kv in toolCallsByIndex)
                {
                    int idx = kv.Key;
                    if (!all && idx >= maxIdx) continue;          // the highest index may still be streaming
                    if (!firedToolCalls.Add(idx)) continue;       // already fired
                    var tc = kv.Value;
                    if (string.IsNullOrEmpty(tc.id) || string.IsNullOrEmpty(tc.function?.name)) { firedToolCalls.Remove(idx); continue; }
                    tc.function.arguments = argsByIndex[idx].ToString();
                    try { onToolCallComplete(tc); } catch { }
                }
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                string line;
                while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (line.Length == 0) continue;
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                    var data = line.Substring(5).Trim();
                    if (data == "[DONE]") break;

                    HFWrapper.HFLLMStreamChunk chunk;
                    try { chunk = JsonConvert.DeserializeObject<HFWrapper.HFLLMStreamChunk>(data); }
                    catch { continue; } // skip keep-alive/comment lines and malformed fragments

                    if (chunk == null) continue;
                    if (chunk.usage != null) usage = chunk.usage;
                    if (!string.IsNullOrEmpty(chunk.id)) generationId = chunk.id;
                    if (!string.IsNullOrEmpty(chunk.model)) servedModel = chunk.model;

                    var choice = chunk.choices != null && chunk.choices.Count > 0 ? chunk.choices[0] : null;
                    if (choice == null) continue;
                    if (!string.IsNullOrEmpty(choice.finish_reason)) finishReason = choice.finish_reason;

                    var delta = choice.delta;
                    if (delta == null) continue;

                    if (!string.IsNullOrEmpty(delta.content))
                    {
                        contentSb.Append(delta.content);
                        try { onToken(delta.content); } catch { }
                    }

                    if (delta.tool_calls != null)
                    {
                        foreach (var tcd in delta.tool_calls)
                        {
                            if (!toolCallsByIndex.TryGetValue(tcd.index, out var tc))
                            {
                                tc = new HFWrapper.HFToolCall { function = new HFWrapper.HFFunctionCall() };
                                toolCallsByIndex[tcd.index] = tc;
                                argsByIndex[tcd.index] = new StringBuilder();
                            }
                            if (!string.IsNullOrEmpty(tcd.id)) tc.id = tcd.id;
                            if (!string.IsNullOrEmpty(tcd.type)) tc.type = tcd.type;
                            if (tcd.function != null)
                            {
                                if (!string.IsNullOrEmpty(tcd.function.name)) tc.function.name = tcd.function.name;
                                if (tcd.function.arguments != null) argsByIndex[tcd.index].Append(tcd.function.arguments);
                            }
                        }
                    }

                    // A tool_call becomes dispatchable as soon as a later index starts streaming.
                    FireCompletedToolCalls(all: false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch when (contentSb.Length > 0 || toolCallsByIndex.Count > 0)
            {
                // Mid-stream drop after we already emitted output — return the partial answer rather
                // than failing the whole turn.
            }

            // Stream finished (or dropped mid-way) — every remaining tool_call is now complete.
            FireCompletedToolCalls(all: true);

            List<HFWrapper.HFToolCall> toolCalls = null;
            if (toolCallsByIndex.Count > 0)
            {
                toolCalls = new List<HFWrapper.HFToolCall>();
                foreach (var kv in toolCallsByIndex)
                {
                    kv.Value.function.arguments = argsByIndex[kv.Key].ToString();
                    toolCalls.Add(kv.Value);
                }
            }

            // The final SSE chunk carries usage; reconcile the reservation before returning.
            ReportFairUseUsage(fairUse, usage);

            return new HFWrapper.HFLLMInferenceResponse
            {
                id = generationId,
                model = servedModel,
                choices = new List<HFWrapper.HFLLMInferenceResponse.Choice>
                {
                    new HFWrapper.HFLLMInferenceResponse.Choice
                    {
                        index = 0,
                        finish_reason = finishReason,
                        message = new HFWrapper.HFMessage
                        {
                            role = "assistant",
                            content = contentSb.ToString(),
                            tool_calls = toolCalls
                        }
                    }
                },
                usage = usage
            };
        }

        // TEMP DIAGNOSTIC (harness-leak hunt, Jul 2026): distinctive phrases that only occur in Claude
        // Code / Agent-SDK harness scaffolding — never in legitimate KliveLLM traffic. Used to flag when
        // that scaffolding leaks into a request/response so the source can be traced. Remove once found.
        private static readonly string[] HarnessLeakMarkers =
        {
            "deferred tools are now available via ToolSearch",
            "Available agent types for the Agent tool",
            "available for use with the Skill tool",
            "require authentication before their tools can be used",
        };

        private static bool ContainsHarnessLeak(string? text) =>
            !string.IsNullOrEmpty(text) && HarnessLeakMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));

        // Writes the full outbound payload + inbound response to a timestamped file under the KliveLLM
        // data directory so a leaking exchange can be inspected offline. Best-effort; never throws.
        private async Task DumpHarnessLeakAsync(string model, string payloadJson, string responseContent)
        {
            try
            {
                string dir = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveLLMDirectory), "HarnessLeakDumps");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, $"leak_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.txt");
                var sb = new StringBuilder();
                sb.AppendLine($"# Harness-leak capture {DateTime.UtcNow:O}");
                sb.AppendLine($"# model: {model}");
                sb.AppendLine($"# outbound(payload) leak: {ContainsHarnessLeak(payloadJson)}   inbound(response) leak: {ContainsHarnessLeak(responseContent)}");
                sb.AppendLine();
                sb.AppendLine("=== OUTBOUND PAYLOAD (what we sent the provider) ===");
                sb.AppendLine(payloadJson);
                sb.AppendLine();
                sb.AppendLine("=== INBOUND RESPONSE (what the provider returned) ===");
                sb.AppendLine(responseContent);
                await File.WriteAllTextAsync(file, sb.ToString());
                try { await ServiceLog($"Harness-leak exchange written to {file}"); } catch { }
            }
            catch { /* diagnostic only — never affect the request */ }
        }

        internal async Task<HFWrapper.HFLLMInferenceResponse> SendPayloadWithRetryAsync(
            RemoteLLMProviderConfiguration remoteProvider,
            HFWrapper.HFLLMInferenceRequest payload,
            CancellationToken cancellationToken = default)
        {
            // Serialize once; HttpRequestMessage/StringContent can only be sent once, so build a
            // fresh request per attempt from this cached body.
            string payloadJson = JsonConvert.SerializeObject(payload);

            Exception lastError = null;
            bool affordableTokenRetryUsed = false;
            int maxAttempts = RemoteInferenceMaxAttempts; // raised to RateLimitMaxAttempts if we hit a rate-limit
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                HttpResponseMessage response;
                string responseContent;
                // Every ATTEMPT is another request against the per-minute windows, so admission is
                // re-acquired per attempt rather than once per call. The lease holds the parallel slot
                // only for the HTTP exchange; its window entry outlives disposal for the full minute.
                AIRouterFairUseLease? fairUse = null;
                try
                {
                    fairUse = await AcquireFairUseAsync(remoteProvider, payload, cancellationToken);
                    using var request = new HttpRequestMessage(HttpMethod.Post, remoteProvider.ChatCompletionsEndpoint)
                    {
                        Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
                    };
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", remoteProvider.ApiKey);

                    if (remoteProvider.Provider == LLMProvider.OpenRouter)
                    {
                        request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/Klivess/Omnipotent");
                        request.Headers.TryAddWithoutValidation("X-Title", "Omnipotent");
                    }

                    response = await client.SendAsync(request, cancellationToken);
                    responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                }
                // A deliberate cancellation (manual Stop / stall watchdog) surfaces as an
                // OperationCanceledException tied to our token — it is NOT a transient blip, so
                // propagate it immediately instead of burning retries on a run we're killing.
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (IsTransientNetworkError(ex))
                {
                    // Connection reset / timeout / DNS blip — retry with backoff.
                    var kind = ex is TaskCanceledException
                        ? RemoteLLMFailureKind.Timeout
                        : RemoteLLMFailureKind.Network;
                    var remoteEx = new RemoteLLMException(
                        kind,
                        $"{remoteProvider.DisplayName} network request failed: {ex.Message}",
                        remoteProvider.DisplayName,
                        payload.model ?? remoteProvider.Model,
                        requestedMaxTokens: payload.max_tokens,
                        innerException: ex);
                    lastError = remoteEx;
                    if (attempt >= RemoteInferenceMaxAttempts) throw remoteEx;
                    try { await ServiceLog($"Remote LLM network error (attempt {attempt}/{RemoteInferenceMaxAttempts}): {ex.Message}. Retrying."); } catch { }
                    await DelayBeforeRetryAsync(attempt, null, cancellationToken);
                    continue;
                }
                finally
                {
                    // The HTTP exchange is over (body fully read, or it failed) — hand the parallel
                    // slot back immediately so a queued caller can use it while we parse. The window
                    // entry survives disposal, so the per-minute limits still count this request.
                    fairUse?.Dispose();
                }

                // TEMP DIAGNOSTIC (harness-leak hunt, Jul 2026): detect Claude-Code / Agent-SDK harness
                // scaffolding in this exchange. In the OUTBOUND payload => it was echoed from a poisoned
                // context store (fix the store). Only in the INBOUND response => the model produced it
                // fresh (prompt/tool confusion). Either way, dump the full exchange for inspection. Both
                // calls are internally guarded so this can never affect the request. Remove once source found.
                bool payloadLeak = ContainsHarnessLeak(payloadJson);
                bool responseLeak = ContainsHarnessLeak(responseContent);
                if (payloadLeak || responseLeak)
                {
                    try
                    {
                        await ServiceLog($"⚠ HARNESS-LEAK detected (model={payload.model}): outbound={payloadLeak}, inbound={responseLeak}. "
                            + (payloadLeak
                                ? "Scaffolding is ALREADY in what we send — the source is a poisoned context store, not the model."
                                : "Model generated it fresh — prompt/tool confusion, not a poisoned store."));
                    }
                    catch { }
                    await DumpHarnessLeakAsync(payload.model, payloadJson, responseContent);
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        int status = (int)response.StatusCode;
                        // OpenRouter often surfaces an UPSTREAM rate-limit / anti-abuse block as a top-level
                        // 400 whose body carries the real code (a 429 "temporarily rate-limited upstream", or
                        // Xiaomi/MiMo's 441 "risk_control" for high-frequency requests). A naive status check
                        // treats that 400 as a permanent client error and fails the whole wake; in reality it's
                        // transient and clears after a cool-off, so we retry it with a longer, gentler backoff.
                        bool rateLimited = status == 429 || IsUpstreamRateLimitBody(responseContent);
                        bool transient = status == 408 || status >= 500 || rateLimited;
                        if (rateLimited) maxAttempts = Math.Max(maxAttempts, RateLimitMaxAttempts);

                        // A 429 from a flat-fee router means our client-side admission control drifted
                        // out of step with the router's own windows — another client on the same key, a
                        // window boundary, a restart that lost the in-memory window. It is not a reason
                        // to give up the wake: the windows are per-minute, so hold the WHOLE shared queue
                        // for the cool-off (every other agent would otherwise walk straight into the same
                        // wall) and keep retrying. The sleep happens inside the limiter on the next
                        // admission, so no extra backoff is added here.
                        if (rateLimited && remoteProvider.Provider == LLMProvider.AIRouter)
                        {
                            var coolOff = ResolveAIRouterCoolOff(GetProviderRetryAfter(response));
                            FairUse.Penalize(coolOff);
                            maxAttempts = Math.Max(maxAttempts, AIRouterRateLimitMaxAttempts);
                            if (attempt < maxAttempts)
                            {
                                try
                                {
                                    // The window state is the diagnostic that matters here: it says
                                    // whether our own admission control drifted, or whether something
                                    // outside this process is spending the same key.
                                    var window = FairUse.Describe();
                                    await ServiceLog($"AIRouter fair-use limit reached (attempt {attempt}/{maxAttempts}); " +
                                        $"queue held for {coolOff.TotalSeconds:0.#}s, then retrying in-wake. " +
                                        $"Our window: {window.RequestsInWindow}/{window.RequestCeiling} requests, " +
                                        $"{window.TokensInWindow:N0}/{window.TokenCeiling:N0} tokens, {window.InFlight} in flight.");
                                }
                                catch { }
                                continue;
                            }
                        }

                        // OpenRouter's 402 response includes the maximum completion length the current
                        // account can fund. A missing max_tokens lets a model default to a very large
                        // value (often 65,536), so retry exactly once with a 10% safety margin. This is
                        // an adaptation of the rejected request, not a generic retry: the same 402 can
                        // never loop, and other providers retain fail-fast 402 behaviour.
                        int? affordableMaxTokens = ParseAffordableMaxTokens(responseContent);
                        int? affordableRetryMax = remoteProvider.Provider == LLMProvider.OpenRouter && !affordableTokenRetryUsed
                            ? CalculateAffordableRetryMaxTokens(responseContent, payload.max_tokens)
                            : null;
                        if (affordableRetryMax.HasValue)
                        {
                            affordableTokenRetryUsed = true;
                            payload.max_tokens = affordableRetryMax.Value;
                            payloadJson = JsonConvert.SerializeObject(payload);
                            maxAttempts = Math.Max(maxAttempts, attempt + 1); // preserve the one adaptive retry even if ordinary attempts were consumed
                            try
                            {
                                await ServiceLog(
                                    $"OpenRouter could not fund the requested completion size; retrying once with max_tokens={affordableRetryMax.Value} " +
                                    $"(provider affordability {affordableMaxTokens}).");
                            }
                            catch { }
                            continue;
                        }

                        var retryAfter = GetProviderRetryAfter(response);
                        var httpEx = new RemoteLLMException(
                            ClassifyRemoteFailure(response.StatusCode, responseContent),
                            $"{remoteProvider.DisplayName} request failed with status {status} ({response.ReasonPhrase}). Body: {responseContent}",
                            remoteProvider.DisplayName,
                            payload.model ?? remoteProvider.Model,
                            response.StatusCode,
                            payload.max_tokens,
                            affordableMaxTokens,
                            retryAfter);

                        // A long Retry-After is the provider telling us this wake cannot recover yet.
                        // Do not send another full native-fallback chain before that window expires; the
                        // project circuit will defer the next wake using the unbounded provider hint.
                        bool retryFitsThisWake = !rateLimited || !retryAfter.HasValue
                            || retryAfter.Value <= TimeSpan.FromMilliseconds(RateLimitMaxDelayMs);
                        if (transient && retryFitsThisWake && attempt < maxAttempts)
                        {
                            lastError = httpEx;
                            try { await ServiceLog($"Remote LLM {(rateLimited ? "rate-limit" : "transient")} {status} (attempt {attempt}/{maxAttempts}). Retrying."); } catch { }
                            await DelayBeforeRetryAsync(attempt, response, cancellationToken, rateLimited);
                            continue;
                        }
                        throw httpEx; // non-transient (4xx) or out of attempts
                    }

                    var hfResponse = JsonConvert.DeserializeObject<HFWrapper.HFLLMInferenceResponse>(responseContent);

                    if (hfResponse?.choices == null || hfResponse.choices.Count == 0)
                    {
                        var emptyEx = new RemoteLLMException(
                            RemoteLLMFailureKind.EmptyResponse,
                            $"{remoteProvider.DisplayName} returned an empty completion payload.",
                            remoteProvider.DisplayName,
                            payload.model ?? remoteProvider.Model,
                            requestedMaxTokens: payload.max_tokens);
                        if (attempt < RemoteInferenceMaxAttempts)
                        {
                            lastError = emptyEx;
                            await DelayBeforeRetryAsync(attempt, null, cancellationToken);
                            continue;
                        }
                        throw emptyEx;
                    }

                    // Swap our pessimistic reservation for what the router actually counted, releasing
                    // the difference to the callers queued behind us.
                    ReportFairUseUsage(fairUse, hfResponse.usage);
                    return hfResponse;
                }
            }

            throw lastError ?? new InvalidOperationException("Remote inference failed for an unknown reason.");
        }

        /// <summary>
        /// Extracts OpenRouter's affordable completion-token limit. The top-level structured error
        /// message is authoritative; named numeric fields and nested messages support compatible
        /// provider shapes, and raw-text matching is the final fallback.
        /// </summary>
        internal static int? ParseAffordableMaxTokens(string? responseContent)
        {
            if (string.IsNullOrWhiteSpace(responseContent)) return null;

            try
            {
                var root = JToken.Parse(responseContent);
                var error = (root as JObject)?["error"] ?? root;

                // Prefer a direct structured field when a provider exposes one.
                if (error is JObject errorObject)
                {
                    foreach (var property in errorObject.Properties())
                    {
                        if (IsAffordableTokenProperty(property.Name) && TryPositiveInt(property.Value, out int structured))
                            return structured;
                    }

                    // OpenRouter's current contract carries the value in error.message.
                    if (TryParseAffordableTokenText(errorObject["message"]?.ToString(), out int fromMainMessage))
                        return fromMainMessage;
                }

                // Be liberal for routed-provider metadata without allowing a requested max_tokens
                // field to masquerade as an affordable limit.
                var descendants = root is JContainer container
                    ? container.Descendants().ToList()
                    : new List<JToken>();
                foreach (var property in descendants.OfType<JProperty>())
                {
                    if (IsAffordableTokenProperty(property.Name) && TryPositiveInt(property.Value, out int structured))
                        return structured;
                }
                foreach (var value in descendants.OfType<JValue>().Where(v => v.Type == JTokenType.String))
                {
                    if (TryParseAffordableTokenText(value.ToString(), out int nestedMessage))
                        return nestedMessage;
                }
            }
            catch (JsonException)
            {
                // Some OpenAI-compatible providers return plain text rather than JSON.
            }

            return TryParseAffordableTokenText(responseContent, out int raw) ? raw : null;
        }

        /// <summary>Returns the sole safe 402 retry size, or null when downshifting cannot help.</summary>
        internal static int? CalculateAffordableRetryMaxTokens(string? responseContent, int? requestedMaxTokens)
        {
            int? affordable = ParseAffordableMaxTokens(responseContent);
            if (!affordable.HasValue) return null;

            long withSafetyMargin = (long)Math.Floor(affordable.Value * AffordableTokenSafetyFraction);
            if (withSafetyMargin < 1) return null;

            if (requestedMaxTokens.HasValue)
            {
                withSafetyMargin = Math.Min(withSafetyMargin, requestedMaxTokens.Value);
                // A retry must actually reduce the rejected request. If the provider says it can
                // afford at least the caller's explicit cap, this 402 has a different root cause.
                if (withSafetyMargin >= requestedMaxTokens.Value) return null;
            }

            return (int)Math.Min(withSafetyMargin, int.MaxValue);
        }

        private static bool IsAffordableTokenProperty(string name)
        {
            string normalized = new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            return normalized.Contains("afford", StringComparison.Ordinal)
                && normalized.Contains("token", StringComparison.Ordinal);
        }

        private static bool TryPositiveInt(JToken token, out int value)
        {
            value = 0;
            if (token.Type == JTokenType.Integer)
            {
                long n = token.Value<long>();
                if (n is > 0 and <= int.MaxValue) { value = (int)n; return true; }
                return false;
            }
            return int.TryParse(token.ToString().Replace(",", "", StringComparison.Ordinal), out value) && value > 0;
        }

        private static bool TryParseAffordableTokenText(string? text, out int value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var match = System.Text.RegularExpressions.Regex.Match(
                text,
                @"can\s+only\s+afford(?:\s+up\s+to)?\s+([0-9][0-9,]*)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            return match.Success
                && int.TryParse(match.Groups[1].Value.Replace(",", "", StringComparison.Ordinal), out value)
                && value > 0;
        }

        internal static RemoteLLMFailureKind ClassifyRemoteFailure(HttpStatusCode status, string? responseContent)
        {
            int code = (int)status;
            if (status == HttpStatusCode.PaymentRequired) return RemoteLLMFailureKind.InsufficientProviderCredit;
            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return RemoteLLMFailureKind.Authentication;
            if (status == HttpStatusCode.NotFound) return RemoteLLMFailureKind.ModelUnavailable;
            if (status == HttpStatusCode.RequestTimeout) return RemoteLLMFailureKind.Timeout;
            if (code == 429 || IsUpstreamRateLimitBody(responseContent)) return RemoteLLMFailureKind.RateLimited;
            if (code >= 500) return RemoteLLMFailureKind.ProviderUnavailable;
            if (code >= 400) return RemoteLLMFailureKind.InvalidRequest;
            return RemoteLLMFailureKind.Unknown;
        }

        private static bool IsTransientNetworkError(Exception ex)
            => ex is HttpRequestException
            || ex is TaskCanceledException        // HttpClient timeout surfaces as this (no cancellation token in use)
            || ex is System.IO.IOException
            || ex is System.Net.Sockets.SocketException;

        private static async Task DelayBeforeRetryAsync(int attempt, HttpResponseMessage? response, CancellationToken cancellationToken = default, bool rateLimited = false)
        {
            TimeSpan delay;
            if (GetBoundedRetryAfter(response) is TimeSpan retryAfter && retryAfter > TimeSpan.Zero)
            {
                delay = retryAfter; // honour provider's Retry-After, bounded to the in-wake policy
            }
            else
            {
                double baseMs = rateLimited ? RateLimitBaseDelayMs : RemoteInferenceBaseDelayMs;
                double ms = baseMs * Math.Pow(2, attempt - 1);
                if (rateLimited) ms = Math.Min(ms, RateLimitMaxDelayMs); // cap so a wake isn't blocked for minutes
                delay = TimeSpan.FromMilliseconds(ms + Random.Shared.Next(0, 250)); // exp backoff + jitter
            }
            await Task.Delay(delay, cancellationToken);
        }

        private static TimeSpan? GetProviderRetryAfter(HttpResponseMessage? response)
        {
            if (response?.Headers?.RetryAfter == null) return null;
            TimeSpan? requested = response.Headers.RetryAfter.Delta;
            if (!requested.HasValue && response.Headers.RetryAfter.Date.HasValue)
                requested = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
            if (!requested.HasValue || requested.Value <= TimeSpan.Zero) return null;
            return requested;
        }

        private static TimeSpan? GetBoundedRetryAfter(HttpResponseMessage? response) =>
            GetProviderRetryAfter(response) is TimeSpan requested ? BoundProviderRetryAfter(requested) : null;

        internal static TimeSpan BoundProviderRetryAfter(TimeSpan requested)
        {
            if (requested <= TimeSpan.Zero) return TimeSpan.Zero;
            var maximum = TimeSpan.FromMilliseconds(RateLimitMaxDelayMs);
            return requested > maximum ? maximum : requested;
        }

        // OpenRouter can wrap an upstream 429/441 in a top-level 400. Inspect the FINAL structured error,
        // rather than its nested history: native fallback responses can contain an earlier route's 429 even
        // when the final route failed for a permanent validation/configuration reason.
        private static bool IsUpstreamRateLimitBody(string? body)
        {
            if (string.IsNullOrEmpty(body)) return false;
            try
            {
                var root = JToken.Parse(body);
                var error = (root as JObject)?["error"] ?? root;
                if (error is JObject errorObject)
                {
                    string? errorType = errorObject.SelectToken("metadata.error_type")?.ToString()
                        ?? errorObject["error_type"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(errorType))
                        return string.Equals(errorType, "rate_limit_exceeded", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(errorType, "rate_limited", StringComparison.OrdinalIgnoreCase)
                            || errorType.Contains("risk_control", StringComparison.OrdinalIgnoreCase);

                    if (IsRateLimitCode(errorObject["code"])) return true;
                    return HasRateLimitMarker(errorObject["message"]?.ToString());
                }
            }
            catch (JsonException)
            {
                // Plain-text compatible-provider errors have no structured final error to inspect.
            }

            return HasRateLimitMarker(body);
        }

        private static bool IsRateLimitCode(JToken? code) => int.TryParse(code?.ToString(), out int value)
            && value is 429 or 441;

        private static bool HasRateLimitMarker(string? text) => !string.IsNullOrEmpty(text)
            && (text.Contains("risk_control", StringComparison.OrdinalIgnoreCase)
                || text.Contains("rate-limited", StringComparison.OrdinalIgnoreCase)
                || text.Contains("rate limited", StringComparison.OrdinalIgnoreCase)
                || text.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                || text.Contains("too many requests", StringComparison.OrdinalIgnoreCase));
        private async Task EnsureModelDownloadedAsync()
        {
            try { await ServiceLog($"Ensuring model exists at startup: {ModelDownloadUrl}"); } catch { }
            // store models under configured OmniPaths LLM models directory
            string modelsDir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveLLMModelsDirectory);
            Directory.CreateDirectory(modelsDir);
            string fileName = Path.GetFileName(new Uri(ModelDownloadUrl).AbsolutePath);
            modelPath = Path.Combine(modelsDir, fileName);
            if (File.Exists(modelPath)) return;

            string tempModelPath = Path.Combine(Path.GetTempPath(), $"{fileName}.{Guid.NewGuid():N}.download");

            // Attempt to download the file. The provided HF link is adjusted to the raw 'resolve' path above.
            using var request = new HttpRequestMessage(HttpMethod.Get, ModelDownloadUrl);
            if (Uri.TryCreate(ModelDownloadUrl, UriKind.Absolute, out var modelUri)
                && modelUri.Host.Contains("huggingface.co", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(huggingFaceToken) == false)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", huggingFaceToken);
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            long totalBytes = response.Content.Headers.ContentLength ?? -1;
            DiscordMessage? progressMessage = null;

            try
            {
                var msgObj = await ExecuteServiceMethod<Omnipotent.Services.KliveBot_Discord.KliveBotDiscord>(
                    "SendMessageToKlives",
                    totalBytes > 0
                        ? $"Starting local LLM model download: 0% ({fileName})"
                        : $"Starting local LLM model download (size unknown): {fileName}");
                progressMessage = msgObj as DiscordMessage;
            }
            catch { }

            try
            {
                using (var remoteStream = await response.Content.ReadAsStreamAsync())
                using (var fs = new FileStream(tempModelPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[1024 * 1024];
                    long downloadedBytes = 0;
                    int lastReportedPercent = -1;
                    int bytesRead;
                    while ((bytesRead = await remoteStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fs.WriteAsync(buffer, 0, bytesRead);
                        downloadedBytes += bytesRead;

                        if (totalBytes > 0)
                        {
                            int percent = (int)((downloadedBytes * 100L) / totalBytes);
                            if (percent >= 100) percent = 100;

                            if (percent == 100 || percent >= lastReportedPercent + 1)
                            {
                                lastReportedPercent = percent;
                                try
                                {
                                    if (progressMessage != null)
                                    {
                                        await progressMessage.ModifyAsync($"Local LLM model download progress: {percent}% ({fileName})");
                                    }
                                    else
                                    {
                                        await ExecuteServiceMethod<Omnipotent.Services.KliveBot_Discord.KliveBotDiscord>(
                                            "SendMessageToKlives",
                                            $"Local LLM model download progress: {percent}% ({fileName})");
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }

                File.Move(tempModelPath, modelPath, true);

                try
                {
                    if (progressMessage != null)
                    {
                        await progressMessage.ModifyAsync($"Local LLM model downloaded: 100% ({fileName})");
                    }
                }
                catch { }
            }
            finally
            {
                try
                {
                    if (File.Exists(tempModelPath))
                    {
                        File.Delete(tempModelPath);
                    }
                }
                catch { }
            }
            ExecuteServiceMethod<Omnipotent.Services.KliveBot_Discord.KliveBotDiscord>("SendMessageToKlives", $"Local LLM model downloaded.");
            try { await ServiceLog($"Downloaded model to {modelPath}"); } catch { }
        }
        private async Task InitializeLocalModelAsync()
        {
            await Task.Yield();
            try
            {
                await ServiceLog("Initializing local LLamaSharp model");

                modelParams = new ModelParams(modelPath)
                {
                    ContextSize = Convert.ToUInt32(GetIntOmniSetting("ModelParameterContextSize", 26000).GetAwaiter().GetResult()),
                    //ContextSize=4096,
                    GpuLayerCount = 0,    // Explicitly force CPU
                    Threads = Math.Max(1, Environment.ProcessorCount - 1),
                };

                // Load weights once; contexts/executors are created per session
                modelWeights = LLamaWeights.LoadFromFile(modelParams);

                localModelReady = true;
                await ServiceLog("Local model initialized successfully");
            }
            catch (Exception e)
            {
                // ignore errors - leave localModelReady false
                try { await ServiceLogError(e, "Failed to initialize local model"); } catch { }
            }
        }
        public class KliveLLMResponse
        {
            public string Response { get; set; }
            public string RawResponse { get; set; }
            public string SessionId { get; set; }
            public ChatHistory Conversation { get; set; }
            public bool Success { get; set; }
            public string ErrorMessage { get; set; }
            public int PromptTokens { get; set; }
            public int CompletionTokens { get; set; }

            /// <summary>
            /// How many of <see cref="PromptTokens"/> were served from the provider's prompt cache.
            /// Deserialized from usage.prompt_tokens_details.cached_tokens. This is the ONLY way to tell
            /// whether the cache_control breakpoints are actually landing: explicit caching is honoured
            /// by Anthropic and Gemini, automatic on OpenAI/DeepSeek/Grok, and silently ignored by
            /// everything else — so a route change can quietly stop it working with no other symptom.
            /// </summary>
            public int CachedPromptTokens { get; set; }

            /// <summary>The provider's generation/response id (OpenRouter's `id`), used to fetch
            /// the authoritative per-request cost from /generation. Null when unavailable.</summary>
            public string? GenerationId { get; set; }

            /// <summary>The REAL USD cost of this turn, reported by OpenRouter in the response's usage
            /// object (usage.cost, credits == USD). This is authoritative and needs no /generation
            /// round-trip. Null when the provider doesn't report a cost (HuggingFace/local), in which
            /// case callers fall back to a provisional estimate + optional /generation reconciliation.</summary>
            public double? CostUsd { get; set; }

            /// <summary>The model that actually served this turn, as reported by the provider (OpenRouter's
            /// response `model`). With OpenRouter fallback routing this may be a later route than the primary
            /// one requested. Null when the provider doesn't echo it.</summary>
            public string? Model { get; set; }

            /// <summary>The live model-window limit applied to this request, when the caller supplied
            /// one (Projects/OpenRouter).</summary>
            public int? ContextWindowTokens { get; set; }

            /// <summary>Whether KliveLLM compacted the structured session before dispatch.</summary>
            public bool ContextWasCompacted { get; set; }

            // Native tool-calling path: populated when the model requested tool invocations
            // (finish_reason == "tool_calls"). Null/empty on an ordinary text completion.
            public List<HFWrapper.HFToolCall> ToolCalls { get; set; }
        }
        public void ResetSession(string? sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            lock (sessions)
            {
                sessions.Remove(sessionId);
            }
        }

        private async Task<KliveLLMResponse> QueryLocalLLMAsync(
            string prompt,
            string? sessionId = null,
            int? maxTokensOverride = null,
            bool resetSessionBeforeQuery = false,
            bool resetSessionAfterQuery = false,
            CancellationToken cancellationToken = default,
            Action<string>? onToken = null)
        {
            var resp = new KliveLLMResponse() { Response = string.Empty, Conversation = new ChatHistory(), Success = false, ErrorMessage = string.Empty };
            if (!localModelReady || modelWeights == null)
            {
                resp.ErrorMessage = "Local model not available";
                await ServiceLogError(resp.ErrorMessage);
                return resp;
            }

            if (resetSessionBeforeQuery && !string.IsNullOrWhiteSpace(sessionId))
            {
                ResetSession(sessionId);
            }

            try
            {
                // ensure session
                if (string.IsNullOrEmpty(sessionId))
                {
                    sessionId = Guid.NewGuid().ToString();
                    var s = new KliveLLMSession(this, true) { sessionId = sessionId };
                    lock (sessions)
                    {
                        sessions[sessionId] = s;
                    }
                }

                KliveLLMSession session;
                lock (sessions)
                {
                    if (!sessions.TryGetValue(sessionId, out session))
                    {
                        session = new KliveLLMSession(this, true) { sessionId = sessionId };
                        sessions[sessionId] = session;
                    }
                }

                await session.sessionLock.WaitAsync();
                try
                {
                    session.lastUpdated = DateTime.UtcNow;
                    try { await ServiceLog($"Querying local model for session {sessionId}"); } catch { }

                    // Use LLama ChatSession to generate response
                    if (session.executor == null || session.chatSession == null)
                    {
                        throw new InvalidOperationException("Local LLama executor or session not initialized");
                    }

                    var inferenceParams = new InferenceParams()
                    {
                        MaxTokens = Math.Clamp(
                            maxTokensOverride ?? GetIntOmniSetting("InferenceParameterMaxTokens", 1024).GetAwaiter().GetResult(),
                            32,
                            2048),
                        AntiPrompts = new List<string> { "User:", "\nUser:", "System:", "\nSystem:", "Assistant:", "\nAssistant:", "<|im_start|>", "<|im_end|>" },
                        SamplingPipeline = new DefaultSamplingPipeline
                        {
                            Temperature = (float)Convert.ToDouble(GetStringOmniSetting("InferenceParameterTemperature", "0.6").GetAwaiter().GetResult()),
                            TopK = GetIntOmniSetting("InferenceParameterTopK", 40).GetAwaiter().GetResult(),
                            TopP = (float)Convert.ToDouble(GetStringOmniSetting("InferenceParameterTopP", "0.95").GetAwaiter().GetResult()),
                            MinP = (float)Convert.ToDouble(GetStringOmniSetting("InferenceParameterMinimumP", "0.05").GetAwaiter().GetResult()),
                            RepeatPenalty = (float)Convert.ToDouble(GetStringOmniSetting("InferenceParameterRepeatPenalty", "1.1").GetAwaiter().GetResult()),
                            PresencePenalty = (float)Convert.ToDouble(GetStringOmniSetting("InferenceParameterPresencePenalty", "1.5").GetAwaiter().GetResult()),
                            FrequencyPenalty = (float)Convert.ToDouble(GetStringOmniSetting("InferenceParameterFrequencyPenalty", "0.5").GetAwaiter().GetResult()),
                        }
                    };

                    var chatMsg = new ChatHistory.Message(AuthorRole.User, StampIncoming(prompt));
                    StringBuilder sb = new StringBuilder();
                    await foreach (var chunk in session.chatSession.ChatAsync(chatMsg, inferenceParams, cancellationToken))
                    {
                        sb.Append(chunk);
                        try { onToken?.Invoke(chunk); } catch { }
                    }
                    string raw = sb.ToString();

                    string outStr = SanitizeLocalModelOutput(raw);
                    if (string.IsNullOrWhiteSpace(outStr))
                        outStr = "[No response. Error?]";

                    session.lastUpdated = DateTime.UtcNow;

                    resp.RawResponse = raw;
                    resp.Response = outStr;
                    resp.SessionId = sessionId;
                    resp.Conversation = session.chatHistory;
                    resp.Success = true;
                    if (resetSessionAfterQuery && !string.IsNullOrWhiteSpace(sessionId))
                    {
                        ResetSession(sessionId);
                    }
                    try { await ServiceLog($"Local model returned response for session {sessionId}"); } catch { }
                    return resp;
                }
                finally
                {
                    session.sessionLock.Release();
                }
            }
            catch (TargetInvocationException tie)
            {
                string fullError = $"{tie.GetType().Name}: {tie.Message} | Inner: {tie.InnerException?.GetType().Name}: {tie.InnerException?.Message} | Stack: {tie.StackTrace}";
                await ServiceLogError(tie, fullError);
                if (resetSessionAfterQuery && !string.IsNullOrWhiteSpace(sessionId))
                {
                    ResetSession(sessionId);
                }
                return new KliveLLMResponse() { Response = string.Empty, Conversation = new ChatHistory(), Success = false, ErrorMessage = fullError };
            }
            catch (Exception ex)
            {
                string fullError = $"{ex.GetType().Name}: {ex.Message} | Inner: {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message} | Stack: {ex.StackTrace}";
                await ServiceLogError(ex, fullError);
                if (resetSessionAfterQuery && !string.IsNullOrWhiteSpace(sessionId))
                {
                    ResetSession(sessionId);
                }
                return new KliveLLMResponse() { Response = string.Empty, Conversation = new ChatHistory(), Success = false, ErrorMessage = fullError };
            }
        }

        private static string SanitizeLocalModelOutput(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            string text = raw
                .Replace("\r\n", "\n")
                .Replace("<|im_start|>", "")
                .Replace("<|im_end|>", "")
                .Replace("<|endoftext|>", "")
                .Trim();

            int thinkEndIdx = text.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            if (thinkEndIdx >= 0)
            {
                text = text.Substring(thinkEndIdx + "</think>".Length).TrimStart();
            }

            // Clean up potentially unclosed or badly formatted XML-like thoughts common in some local agent models
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?s)<analysis>.*?(</analysis>|$)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?s)<decision>.*?(</decision>|</analysis>|$)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?s)Analysis:.*?(</analysis>|$)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // If the output is enveloped entirely in a response block or contains one, try to extract the inner content
            var responseMatch = System.Text.RegularExpressions.Regex.Match(text, @"<response>(.*?)</response>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            if (responseMatch.Success)
            {
                text = responseMatch.Groups[1].Value;
            }
            else
            {
                text = text.Replace("<response>", "").Replace("</response>", "");
            }

            // Remove common leading role prefixes emitted by chat-tuned models.
            string[] leadingPrefixes = ["Assistant:", "assistant:", "[Assistant]", "[assistant]", "System:", "system:"];
            bool removedPrefix;
            do
            {
                removedPrefix = false;
                foreach (var prefix in leadingPrefixes)
                {
                    if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        text = text.Substring(prefix.Length).TrimStart();
                        removedPrefix = true;
                    }
                }
            } while (removedPrefix);

            // Cut off when the model starts printing another role block.
            string[] stopMarkers = ["\nUser:", "\nSystem:", "\nAssistant:", "User:", "System:", "Assistant:"];
            int cutIndex = -1;
            foreach (var marker in stopMarkers)
            {
                int idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx > 0 && (cutIndex < 0 || idx < cutIndex))
                {
                    cutIndex = idx;
                }
            }

            if (cutIndex >= 0)
            {
                text = text.Substring(0, cutIndex).Trim();
            }

            var lines = text
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Where(line => !line.StartsWith("Processing Time:", StringComparison.OrdinalIgnoreCase))
                .ToList();

            return string.Join("\n", lines).Trim('`', '\n', '\r', ' ');
        }

        public string ProducePayloadString(string role, string content, string model)
        {
            string payload = $"{{\"messages\":[{{\"role\":\"{role}\",\"content\":\"{content}\"}}],\"model\":\"{model}\",\"stream\":false}}";
            return payload;
        }
        public class KliveLLMSession
        {
            private KliveLLM parentService;

            public string sessionId;
            public DateTime lastUpdated;
            public int lastPromptTokens;
            public int lastCompletionTokens;
            public ChatHistory chatHistory;

            // Tool-calling path only: a structured message log that can represent assistant turns
            // carrying tool_calls and role:"tool" result turns — neither of which LLama's ChatHistory
            // (role+content only) can hold. The remote tool path uses this instead of chatHistory.
            public List<HFWrapper.HFMessage> structuredMessages = new();


            //local
            public LLamaContext? context;
            public InteractiveExecutor? executor;
            public bool isLocalLLM;
            public readonly System.Threading.SemaphoreSlim sessionLock = new System.Threading.SemaphoreSlim(1, 1);
            public ChatSession? chatSession;

            public KliveLLMSession(KliveLLM parentService, bool isLocalLLM, LLamaWeights modelWeights=null, ModelParams modelParams=null)
            {
                this.isLocalLLM = isLocalLLM;
                this.parentService = parentService;
                this.lastUpdated = DateTime.UtcNow;
                chatHistory = new ChatHistory();
                if (isLocalLLM)
                {
                    modelWeights ??= parentService.modelWeights;
                    modelParams ??= parentService.modelParams;
                    context = modelWeights.CreateContext(modelParams);
                    executor = new InteractiveExecutor(context);
                    chatSession = new ChatSession(executor, chatHistory);
                }
                // Seed system prompt - can be customized. The thinking directive (/no_think when thinking
                // is Off) follows the configured ThinkingType.
                chatHistory.AddMessage(AuthorRole.System, "You are a helpful assistant." + parentService.ThinkingDirective);
            }
        }
    }
}
