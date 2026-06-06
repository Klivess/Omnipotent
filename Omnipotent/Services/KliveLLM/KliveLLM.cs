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
    public class KliveLLM : OmniService
    {
        private const string DefaultHuggingFaceModel = "meta-llama/Llama-3.1-8B-Instruct:cerebras";
        private const string DefaultOpenRouterModel = "openai/gpt-4.1-mini";
        private const string DefaultFreeOpenRouterModel = "openrouter/free";
        private static readonly string[] ProviderOptions = new[] { "Local", "HuggingFace", "OpenRouter" };
        private static readonly string[] OpenRouterServiceTierOptions = new[] { "default", "flex", "priority" };

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
        private Dictionary<string, KliveLLMSession> sessions = new Dictionary<string, KliveLLMSession>();

        private enum LLMProvider
        {
            Local,
            HuggingFace,
            OpenRouter,
        }

        private sealed class RemoteLLMProviderConfiguration
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

        protected override async void ServiceMain()
        {
            LLamaBinariesDownloadPath = await GetStringOmniSetting("CustomLLamaBinariesDownloadLink", "https://github.com/Klivess/Omnipotent/raw/refs/heads/master/OldCPULLamaBinaries.zip", false, true);
            ModelDownloadUrl = await GetOmniSetting("LocalLLMGGUFDownloadURL", OmniSettingType.String, false, true);
            huggingFaceToken = await GetOmniSetting("HuggingFaceLLMToken", OmniSettingType.String, true, false);
            await EnsureProviderSettingsExistAsync();
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

            if (e.Setting.Name == "RemoteLLMProvider" && valueChanged)
            {
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

        private static LLMProvider ParseProvider(string? configuredProvider)
        {
            if (string.Equals(configuredProvider, "Local", StringComparison.OrdinalIgnoreCase))
            {
                return LLMProvider.Local;
            }

            if (string.Equals(configuredProvider, "OpenRouter", StringComparison.OrdinalIgnoreCase))
            {
                return LLMProvider.OpenRouter;
            }

            return LLMProvider.HuggingFace;
        }

        private async Task<LLMProvider> GetActiveProviderAsync()
        {
            string configuredProvider = await GetDropdownOmniSetting("RemoteLLMProvider", "HuggingFace", ProviderOptions, false, false);
            return ParseProvider(configuredProvider);
        }

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
            await GetStringOmniSetting("FreeOpenRouterModelID", DefaultFreeOpenRouterModel, false, false);
            await GetDropdownOmniSetting("OpenRouterServiceTier", "default", OpenRouterServiceTierOptions, false, false);
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
            bool useFreeModel = false)
        {
            if (useFreeModel)
            {
                return await QueryRemoteLLMAsync(
                    prompt,
                    sessionId,
                    maxTokensOverride,
                    systemPrompt,
                    forceFreeModel: true);
            }

            if (await GetActiveProviderAsync() != LLMProvider.Local)
            {
                var hfResponse = await QueryRemoteLLMAsync(
                    prompt,
                    sessionId,
                    maxTokensOverride,
                    systemPrompt);

                return hfResponse;
            }

            return await QueryLocalLLMAsync(
                prompt,
                sessionId,
                maxTokensOverride);
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
                    s.structuredMessages.Add(new HFWrapper.HFMessage { role = "system", content = systemPrompt });
                sessions[sessionId] = s;
            }
        }

        /// <summary>Append a user turn to a tool-calling session.</summary>
        public void AppendUserMessageToToolSession(string sessionId, string content)
        {
            lock (sessions)
            {
                if (sessions.TryGetValue(sessionId, out var s))
                    s.structuredMessages.Add(new HFWrapper.HFMessage { role = "user", content = content ?? string.Empty });
            }
        }

        /// <summary>Append a tool-result turn (role:"tool") answering a specific tool_call_id.</summary>
        public void AppendToolResult(string sessionId, string toolCallId, string name, string content)
        {
            lock (sessions)
            {
                if (sessions.TryGetValue(sessionId, out var s))
                    s.structuredMessages.Add(new HFWrapper.HFMessage
                    {
                        role = "tool",
                        tool_call_id = toolCallId,
                        name = name,
                        content = content ?? string.Empty
                    });
            }
        }

        /// <summary>Send the session's current structured message log (plus the tool definitions) to the
        /// remote provider. Appends the assistant response — including any requested tool_calls — back to
        /// the log, and returns it. ToolCalls is populated when the model wants to invoke tools.</summary>
        public async Task<KliveLLMResponse> QueryToolSessionAsync(string sessionId, List<HFWrapper.HFTool> tools, int? maxTokensOverride = null)
        {
            KliveLLMSession session;
            List<HFWrapper.HFMessage> snapshot;
            lock (sessions)
            {
                if (!sessions.TryGetValue(sessionId, out session))
                    return new KliveLLMResponse { Success = false, ErrorMessage = "Tool session not found. Call StartToolSession first.", SessionId = sessionId };
                snapshot = new List<HFWrapper.HFMessage>(session.structuredMessages);
            }

            var response = await SendRemoteToolRequestAsync(snapshot, tools, maxTokensOverride);
            var msg = response.choices[0].message;
            var content = msg?.content ?? string.Empty;
            var toolCalls = (msg?.tool_calls != null && msg.tool_calls.Count > 0) ? msg.tool_calls : null;

            lock (sessions)
            {
                // Persist the assistant turn so the next round-trip carries the tool_calls it must answer.
                session.structuredMessages.Add(new HFWrapper.HFMessage
                {
                    role = "assistant",
                    content = content, // "" not null — strict providers reject null content
                    tool_calls = toolCalls,
                });
                session.lastUpdated = DateTime.UtcNow;
            }

            return new KliveLLMResponse
            {
                Response = content,
                RawResponse = content,
                SessionId = sessionId,
                Success = true,
                ToolCalls = toolCalls,
                PromptTokens = response.usage?.prompt_tokens ?? 0,
                CompletionTokens = response.usage?.completion_tokens ?? 0,
            };
        }

        private async Task<KliveLLMResponse> QueryRemoteLLMAsync(string prompt, string? sessionId, int? maxTokensOverride, string? systemPrompt = null, bool forceFreeModel = false)
        {
            // ensure session
            if (string.IsNullOrEmpty(sessionId))
            {
                sessionId = Guid.NewGuid().ToString();
                var s = new KliveLLMSession(this, false) { sessionId = sessionId };
                if (!string.IsNullOrWhiteSpace(systemPrompt))
                {
                    s.chatHistory.Messages.Clear();
                    s.chatHistory.AddMessage(AuthorRole.System, systemPrompt);
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
                        session.chatHistory.AddMessage(AuthorRole.System, systemPrompt);
                    }
                    sessions[sessionId] = session;
                }
            }
            session.chatHistory.AddMessage(AuthorRole.User, prompt);
            var response = await SendRemoteInferenceRequestAsync(session.chatHistory, maxTokensOverride, forceFreeModel);
            {
                // Providers occasionally return an assistant turn with null content. Coalesce to an
                // empty string so it neither crashes downstream parsing nor poisons the session
                // history — a stored null re-serializes as "content": null on the next turn and is
                // rejected by strict providers (Alibaba/Qwen) with a 400.
                var content = response.choices[0].message.content ?? string.Empty;
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
                    CompletionTokens = response.usage?.completion_tokens ?? 0,
                };
            }
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

        private async Task<HFWrapper.HFLLMInferenceResponse> SendRemoteInferenceRequestAsync(ChatHistory messages, int? maxTokensOverride, bool forceFreeModel = false)
        {
            RemoteLLMProviderConfiguration remoteProvider = await GetRemoteProviderConfigurationAsync(forceFreeModel);

            HFWrapper.HFLLMInferenceRequest payload = new HFWrapper.HFLLMInferenceRequest()
            {
                model = remoteProvider.Model,
                stream = false,
                max_tokens = maxTokensOverride,
            };
            ApplyServiceTier(ref payload, remoteProvider);
            payload.BuildMessagesFromChatHistory(messages);

            return await SendPayloadWithRetryAsync(remoteProvider, payload);
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
            bool forceFreeModel = false)
        {
            RemoteLLMProviderConfiguration remoteProvider = await GetRemoteProviderConfigurationAsync(forceFreeModel);

            HFWrapper.HFLLMInferenceRequest payload = new HFWrapper.HFLLMInferenceRequest()
            {
                model = remoteProvider.Model,
                stream = false,
                max_tokens = maxTokensOverride,
                tools = tools,
            };
            ApplyServiceTier(ref payload, remoteProvider);
            payload.BuildMessagesFromList(structuredMessages);

            return await SendPayloadWithRetryAsync(remoteProvider, payload);
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

        private async Task<HFWrapper.HFLLMInferenceResponse> SendPayloadWithRetryAsync(
            RemoteLLMProviderConfiguration remoteProvider,
            HFWrapper.HFLLMInferenceRequest payload)
        {
            // Serialize once; HttpRequestMessage/StringContent can only be sent once, so build a
            // fresh request per attempt from this cached body.
            string payloadJson = JsonConvert.SerializeObject(payload);

            Exception lastError = null;
            for (int attempt = 1; attempt <= RemoteInferenceMaxAttempts; attempt++)
            {
                HttpResponseMessage response;
                string responseContent;
                try
                {
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

                    response = await client.SendAsync(request);
                    responseContent = await response.Content.ReadAsStringAsync();
                }
                catch (Exception ex) when (IsTransientNetworkError(ex))
                {
                    // Connection reset / timeout / DNS blip — retry with backoff.
                    lastError = ex;
                    if (attempt >= RemoteInferenceMaxAttempts) throw;
                    try { await ServiceLog($"Remote LLM network error (attempt {attempt}/{RemoteInferenceMaxAttempts}): {ex.Message}. Retrying."); } catch { }
                    await DelayBeforeRetryAsync(attempt, null);
                    continue;
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        int status = (int)response.StatusCode;
                        bool transient = status == 408 || status == 429 || status >= 500;
                        var httpEx = new HttpRequestException(
                            $"{remoteProvider.DisplayName} request failed with status {status} ({response.ReasonPhrase}). Body: {responseContent}");

                        if (transient && attempt < RemoteInferenceMaxAttempts)
                        {
                            lastError = httpEx;
                            try { await ServiceLog($"Remote LLM transient {status} (attempt {attempt}/{RemoteInferenceMaxAttempts}). Retrying."); } catch { }
                            await DelayBeforeRetryAsync(attempt, response);
                            continue;
                        }
                        throw httpEx; // non-transient (4xx) or out of attempts
                    }

                    var hfResponse = JsonConvert.DeserializeObject<HFWrapper.HFLLMInferenceResponse>(responseContent);

                    if (hfResponse?.choices == null || hfResponse.choices.Count == 0)
                    {
                        var emptyEx = new InvalidOperationException($"{remoteProvider.DisplayName} returned an empty completion payload.");
                        if (attempt < RemoteInferenceMaxAttempts)
                        {
                            lastError = emptyEx;
                            await DelayBeforeRetryAsync(attempt, null);
                            continue;
                        }
                        throw emptyEx;
                    }

                    return hfResponse;
                }
            }

            throw lastError ?? new InvalidOperationException("Remote inference failed for an unknown reason.");
        }

        private static bool IsTransientNetworkError(Exception ex)
            => ex is HttpRequestException
            || ex is TaskCanceledException        // HttpClient timeout surfaces as this (no cancellation token in use)
            || ex is System.IO.IOException
            || ex is System.Net.Sockets.SocketException;

        private static async Task DelayBeforeRetryAsync(int attempt, HttpResponseMessage response)
        {
            TimeSpan delay;
            if (response?.Headers?.RetryAfter?.Delta is TimeSpan retryAfter && retryAfter > TimeSpan.Zero)
            {
                delay = retryAfter; // honour provider's Retry-After (common on 429)
            }
            else
            {
                double ms = RemoteInferenceBaseDelayMs * Math.Pow(2, attempt - 1);
                delay = TimeSpan.FromMilliseconds(ms + Random.Shared.Next(0, 250)); // exp backoff + jitter
            }
            await Task.Delay(delay);
        }
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
            bool resetSessionAfterQuery = false)
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

                    var chatMsg = new ChatHistory.Message(AuthorRole.User, prompt);
                    StringBuilder sb = new StringBuilder();
                    await foreach (var chunk in session.chatSession.ChatAsync(chatMsg, inferenceParams))
                    {
                        sb.Append(chunk);

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
                // Seed system prompt - can be customized
                chatHistory.AddMessage(AuthorRole.System, "You are a helpful assistant. /no_think");
            }
        }
    }
}
