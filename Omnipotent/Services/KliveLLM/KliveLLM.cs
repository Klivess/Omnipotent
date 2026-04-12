using DSharpPlus;
using DSharpPlus.Entities;
using Microsoft.Extensions.AI;
using LLama;
using LLama.Abstractions;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Markdig.Extensions.TaskLists;
using LangChain.Providers.HuggingFace;
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

namespace Omnipotent.Services.KliveLocalLLM
{
    public class KliveLLM : OmniService
    {
        private string huggingFaceToken = "";
        private HttpClient client;
        private string ModelDownloadUrl = "";
        private const string HuggingFaceNovitaModelId = "openai/gpt-oss-120b:novita";
        private static string LLamaBinariesDownloadPath;
        private static string LLamaBinariesFolder = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveLLamaBinariesDirectory);
        public static string LLamaDLLFile = Path.Combine(LLamaBinariesFolder, "llama.dll");
        public static string LLamaMTMDFile = Path.Combine(LLamaBinariesFolder, "mtmd.dll");
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
            client = new HttpClient();
            client.DefaultRequestHeaders.Add("Authorization", "Bearer " + huggingFaceToken);

            await EnsureLlamaBinariesAvailableAsync();
            NativeLibraryConfig.All.WithLibrary(LLamaDLLFile, LLamaMTMDFile);

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
            bool resetSessionBeforeQuery = false,
            bool resetSessionAfterQuery = false)
        {
            var useHuggingFaceProvider = await GetBoolOmniSetting("UseHuggingFaceProvider", false);
            if (useHuggingFaceProvider)
            {
                var hfResponse = await QueryLLMViaHuggingFaceAsync(
                    prompt,
                    sessionId,
                    maxTokensOverride,
                    resetSessionBeforeQuery,
                    resetSessionAfterQuery);

                if (!hfResponse.Success && ShouldFallbackToLocalForHuggingFaceError(hfResponse.ErrorMessage))
                {
                    await ServiceLog($"HuggingFace provider unavailable for model '{HuggingFaceNovitaModelId}'. Falling back to local model. Error={hfResponse.ErrorMessage}");
                    var localFallback = await QueryLocalLLMAsync(
                        prompt,
                        sessionId,
                        maxTokensOverride,
                        resetSessionBeforeQuery,
                        resetSessionAfterQuery);

                    if (localFallback.Success)
                    {
                        return localFallback;
                    }

                    await ServiceLogError($"Local fallback also failed after HuggingFace error. HuggingFace={hfResponse.ErrorMessage} | Local={localFallback.ErrorMessage}");
                }

                return hfResponse;
            }

            return await QueryLocalLLMAsync(
                prompt,
                sessionId,
                maxTokensOverride,
                resetSessionBeforeQuery,
                resetSessionAfterQuery);
        }

        private async Task<KliveLLMResponse> QueryLLMViaHuggingFaceAsync(
            string prompt,
            string? sessionId,
            int? maxTokensOverride,
            bool resetSessionBeforeQuery = false,
            bool resetSessionAfterQuery = false)
        {
            var resp = new KliveLLMResponse()
            {
                Response = string.Empty,
                Conversation = new List<KliveLLMMessage>(),
                Success = false,
                ErrorMessage = string.Empty,
                SessionId = string.IsNullOrWhiteSpace(sessionId) ? string.Empty : sessionId
            };

            try
            {
                if (resetSessionBeforeQuery && !string.IsNullOrWhiteSpace(sessionId))
                {
                    ResetSession(sessionId);
                }

                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    sessionId = Guid.NewGuid().ToString();
                }

                resp.SessionId = sessionId;

                KliveOnlineLLMSession session;
                lock (onlineSessions)
                {
                    if (!onlineSessions.TryGetValue(sessionId, out session))
                    {
                        session = new KliveOnlineLLMSession() { sessionId = sessionId };
                        onlineSessions[sessionId] = session;
                    }
                }

                await session.sessionLock.WaitAsync();
                try
                {
                    var userMessage = new KliveLLMMessage() { role = "user", content = prompt };
                    session.messages.Add(userMessage);
                    session.lastUpdated = DateTime.UtcNow;

                    int contextMessagesToInclude = Math.Clamp(
                        await GetIntOmniSetting("HuggingFaceChatContextMessageCount", 24),
                        2,
                        80);

                    int contextCharBudget = Math.Clamp(
                        await GetIntOmniSetting("HuggingFaceChatContextCharBudget", 18000),
                        2000,
                        120000);

                    var selectedMessages = SelectMessagesForOnlineContext(
                        session.messages,
                        contextMessagesToInclude,
                        contextCharBudget);

                    var primaryAttempt = await SendHuggingFaceChatCompletionAsync(selectedMessages, maxTokensOverride, sessionId);
                    if (!primaryAttempt.RequestSucceeded)
                    {
                        RemoveLastUserMessageIfUnanswered(session, userMessage);
                        resp.ErrorMessage = primaryAttempt.ErrorMessage;
                        await ServiceLogError($"{resp.ErrorMessage} | {primaryAttempt.Diagnostic}");
                        return resp;
                    }

                    var outStr = primaryAttempt.Output;
                    if (string.IsNullOrWhiteSpace(outStr))
                    {
                        // If context gets too large/noisy, retry once with only current prompt.
                        var retryAttempt = await SendHuggingFaceChatCompletionAsync(new List<KliveLLMMessage> { userMessage }, maxTokensOverride, sessionId);
                        if (!retryAttempt.RequestSucceeded)
                        {
                            RemoveLastUserMessageIfUnanswered(session, userMessage);
                            resp.ErrorMessage = retryAttempt.ErrorMessage;
                            await ServiceLogError($"{resp.ErrorMessage} | primary={primaryAttempt.Diagnostic} | retry={retryAttempt.Diagnostic}");
                            return resp;
                        }

                        outStr = retryAttempt.Output;
                        if (string.IsNullOrWhiteSpace(outStr))
                        {
                            RemoveLastUserMessageIfUnanswered(session, userMessage);
                            resp.ErrorMessage = "HuggingFace router returned empty content.";
                            await ServiceLogError($"{resp.ErrorMessage} | primary={primaryAttempt.Diagnostic} | retry={retryAttempt.Diagnostic}");
                            return resp;
                        }

                        await ServiceLog($"HuggingFace empty-content retry succeeded for session {sessionId}. primary={primaryAttempt.Diagnostic}; retry={retryAttempt.Diagnostic}");
                    }

                session.messages.Add(new KliveLLMMessage() { role = "assistant", content = outStr });
                session.lastUpdated = DateTime.UtcNow;

                resp.Response = outStr;
                resp.Conversation = session.messages
                    .Select(msg => new KliveLLMMessage() { role = msg.role, content = msg.content })
                    .ToList();
                resp.Success = true;

                if (resetSessionAfterQuery && !string.IsNullOrWhiteSpace(sessionId))
                {
                    ResetSession(sessionId);
                }

                return resp;
            }
                finally
                {
                    session.sessionLock.Release();
                }
            }
            catch (Exception ex)
            {
                string fullError = $"{ex.GetType().Name}: {ex.Message} | Inner: {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message} | Stack: {ex.StackTrace}";
                await ServiceLogError(ex, fullError);
                resp.ErrorMessage = fullError;

                if (resetSessionAfterQuery && !string.IsNullOrWhiteSpace(sessionId))
                {
                    ResetSession(sessionId);
                }

                return resp;
            }
        }

        private async Task<(bool RequestSucceeded, string Output, string ErrorMessage, string Diagnostic)> SendHuggingFaceChatCompletionAsync(
            IReadOnlyList<KliveLLMMessage> messages,
            int? maxTokensOverride,
            string? sessionId)
        {
            var modelCandidates = new List<string> { HuggingFaceNovitaModelId };
            var aliasSeparatorIndex = HuggingFaceNovitaModelId.IndexOf(':');
            if (aliasSeparatorIndex > 0)
            {
                modelCandidates.Add(HuggingFaceNovitaModelId[..aliasSeparatorIndex]);
            }

            Exception? lastException = null;
            string lastDiagnostic = string.Empty;

            foreach (var modelId in modelCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var configuration = new HuggingFaceConfiguration
                    {
                        ApiKey = huggingFaceToken,
                        ModelId = modelId,
                        MaxNewTokens = Math.Clamp(maxTokensOverride ?? 256, 32, 4096)
                    };

                    var provider = new HuggingFaceProvider(configuration, client);
                    var chatModel = new HuggingFaceChatModel(provider, modelId);

                    var providerMessages = BuildProviderMessages(messages);
                    if (providerMessages.Count == 0)
                    {
                        return (false, string.Empty, "HuggingFace provider request had no valid messages.", "no-messages");
                    }

                    var request = ChatRequest.ToChatRequest(providerMessages);
                    var settings = new ChatSettings()
                    {
                        UseStreaming = false,
                        User = string.IsNullOrWhiteSpace(sessionId) ? "kliveagent" : sessionId
                    };

                    var response = await chatModel.GenerateAsync(request, settings, CancellationToken.None);
                    var rawText = ExtractContentFromProviderResponse(response);
                    var outStr = SanitizeLocalModelOutput(rawText);
                    var finishReason = response?.FinishReason.ToString() ?? "unknown";
                    var diagnostic = $"model={modelId};finish_reason={finishReason};raw_chars={rawText.Length};sanitized_chars={outStr.Length};messages={providerMessages.Count}";

                    return (true, outStr, string.Empty, diagnostic);
                }
                catch (ApiException apiEx) when (apiEx.StatusCode == HttpStatusCode.Gone || apiEx.StatusCode == HttpStatusCode.NotFound)
                {
                    lastException = apiEx;
                    lastDiagnostic = $"model={modelId};status={(int)apiEx.StatusCode};body={TruncateForLog(apiEx.ResponseBody, 800)}";
                    continue;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    lastDiagnostic = $"model={modelId};{ex.GetType().Name}:{TruncateForLog(ex.Message, 400)}";
                    break;
                }
            }

            if (lastException is ApiException finalApiException)
            {
                return (
                    false,
                    string.Empty,
                    $"HuggingFace provider request failed: {(int)finalApiException.StatusCode} {finalApiException.StatusCode}",
                    lastDiagnostic);
            }

            if (lastException != null)
            {
                return (
                    false,
                    string.Empty,
                    $"HuggingFace provider request failed: {lastException.Message}",
                    lastDiagnostic);
            }

            return (false, string.Empty, "HuggingFace provider request failed: Unknown error", "unknown-error");
        }

        private static bool ShouldFallbackToLocalForHuggingFaceError(string? errorMessage)
        {
            if (string.IsNullOrWhiteSpace(errorMessage))
            {
                return false;
            }

            return errorMessage.Contains(" 410 ", StringComparison.OrdinalIgnoreCase)
                || errorMessage.Contains("gone", StringComparison.OrdinalIgnoreCase)
                || errorMessage.Contains("notfound", StringComparison.OrdinalIgnoreCase)
                || errorMessage.Contains("not found", StringComparison.OrdinalIgnoreCase);
        }

        private static IReadOnlyList<Message> BuildProviderMessages(IReadOnlyList<KliveLLMMessage> messages)
        {
            var providerMessages = new List<Message>
            {
                new Message("You are a helpful assistant. /no_think", MessageRole.System, string.Empty)
            };

            foreach (var message in messages)
            {
                if (string.IsNullOrWhiteSpace(message?.content))
                {
                    continue;
                }

                providerMessages.Add(new Message(
                    message.content,
                    ToProviderMessageRole(message.role),
                    string.Empty));
            }

            return providerMessages;
        }

        private static MessageRole ToProviderMessageRole(string? role)
        {
            if (string.IsNullOrWhiteSpace(role))
            {
                return MessageRole.Human;
            }

            if (role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
            {
                return MessageRole.Ai;
            }

            if (role.Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                return MessageRole.System;
            }

            return MessageRole.Human;
        }

        private static List<KliveLLMMessage> SelectMessagesForOnlineContext(
            IReadOnlyList<KliveLLMMessage> messages,
            int maxMessages,
            int maxCharacters)
        {
            var selected = new List<KliveLLMMessage>();
            var remaining = Math.Max(1000, maxCharacters);

            for (int i = messages.Count - 1; i >= 0; i--)
            {
                var message = messages[i];
                if (string.IsNullOrWhiteSpace(message?.content))
                {
                    continue;
                }

                var messageCost = message.content.Length + 16;
                if (selected.Count > 0 && remaining - messageCost < 0)
                {
                    break;
                }

                selected.Add(message);
                remaining -= messageCost;
                if (selected.Count >= maxMessages)
                {
                    break;
                }
            }

            selected.Reverse();
            return selected;
        }

        private static void RemoveLastUserMessageIfUnanswered(KliveOnlineLLMSession session, KliveLLMMessage userMessage)
        {
            if (session.messages.Count == 0)
            {
                return;
            }

            if (ReferenceEquals(session.messages[^1], userMessage))
            {
                session.messages.RemoveAt(session.messages.Count - 1);
            }
        }

        private static string TruncateForLog(string? value, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            if (value.Length <= maxChars)
            {
                return value;
            }

            return value.Substring(0, Math.Max(0, maxChars - 3)) + "...";
        }

        private static string ExtractContentFromProviderResponse(LangChain.Providers.ChatResponse? response)
        {
            if (response == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(response.LastMessageContent))
            {
                return response.LastMessageContent;
            }

            var lastMessageText = response.LastMessage.ToString();
            if (!string.IsNullOrWhiteSpace(lastMessageText))
            {
                return lastMessageText;
            }

            if (!string.IsNullOrWhiteSpace(response.Delta?.Content))
            {
                return response.Delta.Content;
            }

            if (response.Messages != null)
            {
                foreach (var msg in response.Messages.Reverse())
                {
                    var msgText = msg.ToString();
                    if (!string.IsNullOrWhiteSpace(msgText))
                    {
                        return msgText;
                    }
                }
            }

            if (response.ToolCalls != null)
            {
                var toolArgs = response.ToolCalls
                    .Select(call => call.ToolArguments ?? string.Empty)
                    .Where(arg => !string.IsNullOrWhiteSpace(arg))
                    .ToList();
                if (toolArgs.Count > 0)
                {
                    return string.Join("\n", toolArgs);
                }
            }

            return string.Empty;
        }

        // Local model support (download + reflective loader)
        private string modelPath;
        private ModelParams modelParams;
        private LLamaWeights modelWeights;
        private bool localModelReady = false;
        private Dictionary<string, KliveLLMSession> sessions = new Dictionary<string, KliveLLMSession>();
        private Dictionary<string, KliveOnlineLLMSession> onlineSessions = new Dictionary<string, KliveOnlineLLMSession>();

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
            using var response = await client.GetAsync(ModelDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
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

                            if (percent == 100 || percent >= lastReportedPercent + 5)
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
            ExecuteServiceMethod<Omnipotent.Services.KliveBot_Discord.KliveBotDiscord>("SendMessageToKlives",$"Local LLM model downloaded.");
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
            catch(Exception e)
            {
                // ignore errors - leave localModelReady false
                try { await ServiceLogError(e, "Failed to initialize local model"); } catch { }
            }
        }

        public class KliveLLMResponse
        {
            public string Response { get; set; }
            public string SessionId { get; set; }
            public List<KliveLLMMessage> Conversation { get; set; }
            public bool Success { get; set; }
            public string ErrorMessage { get; set; }
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

            lock (onlineSessions)
            {
                onlineSessions.Remove(sessionId);
            }
        }

        private async Task<KliveLLMResponse> QueryLocalLLMAsync(
            string prompt,
            string? sessionId = null,
            int? maxTokensOverride = null,
            bool resetSessionBeforeQuery = false,
            bool resetSessionAfterQuery = false)
        {
            var resp = new KliveLLMResponse() { Response = string.Empty, Conversation = new List<KliveLLMMessage>(), Success = false, ErrorMessage = string.Empty };
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
                    var s = new KliveLLMSession(this, modelWeights, modelParams) { sessionId = sessionId };
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
                        session = new KliveLLMSession(this, modelWeights, modelParams) { sessionId = sessionId };
                        sessions[sessionId] = session;
                    }
                }

                await session.sessionLock.WaitAsync();
                try
                {
                    var userMsg = new KliveLLMMessage() { role = "user", content = prompt };
                    session.messages.Add(userMsg);
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

                    var assistantMsg = new KliveLLMMessage() { role = "assistant", content = outStr };
                    session.messages.Add(assistantMsg);
                    session.lastUpdated = DateTime.UtcNow;

                    resp.Response = outStr;
                    resp.SessionId = sessionId;
                    resp.Conversation = session.messages;
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
                return new KliveLLMResponse() { Response = string.Empty, Conversation = new List<KliveLLMMessage>(), Success = false, ErrorMessage = fullError };
            }
            catch (Exception ex)
            {
                string fullError = $"{ex.GetType().Name}: {ex.Message} | Inner: {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message} | Stack: {ex.StackTrace}";
                await ServiceLogError(ex, fullError);
                if (resetSessionAfterQuery && !string.IsNullOrWhiteSpace(sessionId))
                {
                    ResetSession(sessionId);
                }
                return new KliveLLMResponse() { Response = string.Empty, Conversation = new List<KliveLLMMessage>(), Success = false, ErrorMessage = fullError };
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
            public List<KliveLLMMessage> messages = new List<KliveLLMMessage>();
            public DateTime lastUpdated;
            public ChatHistory chatHistory;
            public ChatSession chatSession;
            public LLamaContext context;
            public InteractiveExecutor executor;
            public readonly System.Threading.SemaphoreSlim sessionLock = new System.Threading.SemaphoreSlim(1, 1);

            public KliveLLMSession(KliveLLM parentService, LLamaWeights modelWeights, ModelParams modelParams)
            {
                this.parentService = parentService;
                this.lastUpdated = DateTime.UtcNow;
                context = modelWeights.CreateContext(modelParams);
                executor = new InteractiveExecutor(context);
                chatHistory = new ChatHistory();
                // Seed system prompt - can be customized
                chatHistory.AddMessage(AuthorRole.System, "You are a helpful assistant. /no_think");
                chatSession = new ChatSession(executor, chatHistory);
            }
        }

        public class KliveLLMMessage
        {
            public string content;
            public string role;
        }

        public class KliveOnlineLLMSession
        {
            public string sessionId = string.Empty;
            public List<KliveLLMMessage> messages = new List<KliveLLMMessage>();
            public DateTime lastUpdated = DateTime.UtcNow;
            public readonly System.Threading.SemaphoreSlim sessionLock = new System.Threading.SemaphoreSlim(1, 1);
        }
    }
}
