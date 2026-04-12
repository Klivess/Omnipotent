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

namespace Omnipotent.Services.KliveLocalLLM
{
    public class KliveLLM : OmniService
    {
        private string huggingFaceToken = "";
        private HttpClient client;
        private string ModelDownloadUrl = "";
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
                return await QueryLLMViaHuggingFaceAsync(
                    prompt,
                    sessionId,
                    maxTokensOverride,
                    resetSessionBeforeQuery,
                    resetSessionAfterQuery);
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

                    var payloadMessages = new JArray
                    {
                        new JObject
                        {
                            ["role"] = "system",
                            ["content"] = "You are a helpful assistant. /no_think"
                        }
                    };

                    foreach (var message in session.messages.TakeLast(contextMessagesToInclude))
                    {
                        if (string.IsNullOrWhiteSpace(message?.content))
                        {
                            continue;
                        }

                        payloadMessages.Add(new JObject
                        {
                            ["role"] = NormalizeChatRole(message.role),
                            ["content"] = message.content
                        });
                    }

                var body = new JObject
                {
                    ["messages"] = payloadMessages,
                    ["model"] = "openai/gpt-oss-120b:novita",
                    ["stream"] = false
                };

                if (maxTokensOverride.HasValue)
                {
                    body["max_tokens"] = Math.Clamp(maxTokensOverride.Value, 32, 4096);
                }

                using var httpcontent = new StringContent(body.ToString(Formatting.None), Encoding.UTF8);
                httpcontent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                var response = await client.PostAsync("https://router.huggingface.co/v1/chat/completions", httpcontent);
                var llmResp = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    resp.ErrorMessage = $"HuggingFace router request failed ({(int)response.StatusCode}): {llmResp}";
                    await ServiceLogError(resp.ErrorMessage);
                    return resp;
                }

                var json = JObject.Parse(llmResp);
                var rawText = ExtractChatCompletionContent(json["choices"]?[0]?["message"]?["content"]);
                var outStr = SanitizeLocalModelOutput(rawText);
                if (string.IsNullOrWhiteSpace(outStr))
                {
                    resp.ErrorMessage = "HuggingFace router returned empty content.";
                    return resp;
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

        private static string NormalizeChatRole(string? role)
        {
            if (string.IsNullOrWhiteSpace(role))
            {
                return "user";
            }

            if (role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
            {
                return "assistant";
            }

            if (role.Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                return "system";
            }

            return "user";
        }

        private static string ExtractChatCompletionContent(JToken? contentToken)
        {
            if (contentToken == null)
            {
                return string.Empty;
            }

            if (contentToken.Type == JTokenType.String)
            {
                return contentToken.ToString();
            }

            if (contentToken.Type == JTokenType.Array)
            {
                var parts = contentToken
                    .Children<JObject>()
                    .Select(token => token["text"]?.ToString() ?? token["content"]?.ToString() ?? string.Empty)
                    .Where(text => !string.IsNullOrWhiteSpace(text));

                return string.Join("\n", parts);
            }

            if (contentToken.Type == JTokenType.Object)
            {
                return contentToken["text"]?.ToString()
                    ?? contentToken["content"]?.ToString()
                    ?? string.Empty;
            }

            return contentToken.ToString();
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
