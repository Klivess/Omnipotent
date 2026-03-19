using DSharpPlus;
using Microsoft.Extensions.AI;
using LLama;
using LLama.Abstractions;
using LLama.Common;
using LLama.Native;
using Markdig.Extensions.TaskLists;
using LangChain.Providers.HuggingFace;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using System.Net;
using System.Reflection;
using System.Security.Permissions;
using System.IO;
using System.Linq;
using LangChain.Providers.HuggingFace.Predefined;
using LangChain.Providers;
using System.Drawing;
using System.Text;
using Newtonsoft.Json;

namespace Omnipotent.Services.KliveLocalLLM
{
    public class KliveLLM : OmniService
    {
        private string huggingFaceToken = "";
        private HttpClient client;
        public KliveLLM()
        {
            name = "KliveLLM";
            threadAnteriority = ThreadAnteriority.High;
        }

        protected override async void ServiceMain()
        {
            huggingFaceToken = await GetDataHandler().ReadDataFromFile(OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveLLMTokenText));
            if (string.IsNullOrEmpty(huggingFaceToken))
            {
                string apparentToken = (string)await ExecuteServiceMethod<Omnipotent.Services.Notifications.NotificationsService>("SendTextPromptToKlivesDiscord", "KliveLLM requires a hugging face token to function.", "Produce one and set it.", TimeSpan.FromDays(7), "Token", "right here please");
                huggingFaceToken = apparentToken.Trim();
            }
            client = new HttpClient();
            client.DefaultRequestHeaders.Add("Authorization", "Bearer " + huggingFaceToken);
            // Ensure local model is present and initialize reflective LLamaSharp wrapper if available
            // WAITING FOR QWEN3.5 SUPPORT https://github.com/SciSharp/LLamaSharp/issues/1340
            try
            {
                await EnsureModelDownloadedAsync();
                await InitializeLocalModelAsync();
            }
            catch
            {
                // Swallow to avoid crashing service at startup if local model cannot be initialized
            }


            var response = await QueryLocalLLMAsync("testing, say something");
            ServiceLog($"LLM Response: {response.Response}");

        }

        public async Task<string> QueryLLM(string content)
        {
            string payload = ProducePayloadString("user", content, "openai/gpt-oss-120b:cerebras");
            HttpContent httpcontent = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var response = await client.PostAsync("https://router.huggingface.co/v1/chat/completions", httpcontent);
            string llmResp = await response.Content.ReadAsStringAsync();
            dynamic json = JsonConvert.DeserializeObject(llmResp);
            return json.choices[0].message.content;
        }

        // Local model support (download + reflective loader)
        private const string ModelDownloadUrl = "https://huggingface.co/unsloth/Qwen3.5-4B-GGUF/resolve/main/Qwen3.5-4B-Q4_K_M.gguf";
        private string modelPath;
        private ModelParams modelParams;
        private LLamaWeights modelWeights;
        private LLamaContext? llmContext = null;
        private InteractiveExecutor? executor = null;
        private bool localModelReady = false;
        private Dictionary<string, KliveLLMSession> sessions = new Dictionary<string, KliveLLMSession>();

        private async Task EnsureModelDownloadedAsync()
        {
            try { await ServiceLog($"Ensuring model exists at startup: {ModelDownloadUrl}"); } catch { }
            // store models under configured OmniPaths LLM models directory
            string modelsDir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveLLMModelsDirectory);
            Directory.CreateDirectory(modelsDir);
            string fileName = "Qwen3.5-4B-Q4_K_M.gguf";
            modelPath = Path.Combine(modelsDir, fileName);
            if (File.Exists(modelPath)) return;

            // Attempt to download the file. The provided HF link is adjusted to the raw 'resolve' path above.
            using var response = await client.GetAsync(ModelDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            using var remoteStream = await response.Content.ReadAsStreamAsync();
            using var fs = new FileStream(modelPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await remoteStream.CopyToAsync(fs);
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
                    ContextSize = 1024,
                    GpuLayerCount = 5
                };

                // Load weights and create context/executor
                modelWeights = LLamaWeights.LoadFromFile(modelParams);
                llmContext = modelWeights.CreateContext(modelParams);
                executor = new InteractiveExecutor(llmContext);

                localModelReady = true;
                await ServiceLog("Local model initialized successfully");
            }
            catch
            {
                // ignore errors - leave localModelReady false
                try { await ServiceLogError("Failed to initialize local model"); } catch { }
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

        public async Task<KliveLLMResponse> QueryLocalLLMAsync(string prompt, string? sessionId = null)
        {
            var resp = new KliveLLMResponse() { Response = string.Empty, Conversation = new List<KliveLLMMessage>(), Success = false, ErrorMessage = string.Empty };
            if (!localModelReady || executor == null)
            {
                resp.ErrorMessage = "Local model not available";
                await ServiceLogError(resp.ErrorMessage);
                return resp;
            }

            try
            {
                // ensure session
                if (string.IsNullOrEmpty(sessionId))
                {
                    sessionId = Guid.NewGuid().ToString();
                    var s = new KliveLLMSession(this, executor) { sessionId = sessionId };
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
                        session = new KliveLLMSession(this, executor) { sessionId = sessionId };
                        sessions[sessionId] = session;
                    }
                }

                var userMsg = new KliveLLMMessage() { role = "user", content = prompt };
                session.messages.Add(userMsg);
                session.lastUpdated = DateTime.UtcNow;
                try { await ServiceLog($"Querying local model for session {sessionId}"); } catch { }

                // Use LLama ChatSession to generate response
                if (executor == null || session.chatSession == null)
                {
                    throw new InvalidOperationException("Local LLama executor or session not initialized");
                }

                var inferenceParams = new InferenceParams()
                {
                    MaxTokens = 256,
                    AntiPrompts = new List<string> { "User:" }
                };

                var chatMsg = new ChatHistory.Message(AuthorRole.User, prompt);
                StringBuilder sb = new StringBuilder();
                await foreach (var chunk in session.chatSession.ChatAsync(chatMsg, inferenceParams))
                {
                    sb.Append(chunk);
                }
                string outStr = sb.ToString();

                var assistantMsg = new KliveLLMMessage() { role = "assistant", content = outStr };
                session.messages.Add(assistantMsg);
                session.lastUpdated = DateTime.UtcNow;

                resp.Response = outStr;
                resp.SessionId = sessionId;
                resp.Conversation = session.messages;
                resp.Success = true;
                try { await ServiceLog($"Local model returned response for session {sessionId}"); } catch { }
                return resp;
            }
            catch (TargetInvocationException tie)
            {
                var err = tie.InnerException?.Message ?? tie.Message;
                await ServiceLogError(tie, "Error invoking local model");
                return new KliveLLMResponse() { Response = string.Empty, Conversation = new List<KliveLLMMessage>(), Success = false, ErrorMessage = err };
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Local model query failed");
                return new KliveLLMResponse() { Response = string.Empty, Conversation = new List<KliveLLMMessage>(), Success = false, ErrorMessage = ex.Message };
            }
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

            public KliveLLMSession(KliveLLM parentService, InteractiveExecutor executor)
            {
                this.parentService = parentService;
                this.lastUpdated = DateTime.UtcNow;
                chatHistory = new ChatHistory();
                // Seed system prompt - can be customized
                chatHistory.AddMessage(AuthorRole.System, "You are a helpful assistant.");
                chatSession = new ChatSession(executor, chatHistory);
            }
        }

        public class KliveLLMMessage
        {
            public string content;
            public string role;
        }
    }
}
