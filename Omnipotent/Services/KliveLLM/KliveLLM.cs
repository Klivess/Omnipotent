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
using System.Security.Policy;
using System.IO.Compression;

namespace Omnipotent.Services.KliveLocalLLM
{
    public class KliveLLM : OmniService
    {
        private string huggingFaceToken = "";
        private HttpClient client;
        private string ModelDownloadUrl = "";
        private static string LLamaBinariesDownloadPath = @"https://github.com/Klivess/Omnipotent/raw/e98841bf168a3d9ed7dc2c722481ef274882389f/CustomLLamaBinaries.zip";
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
            catch
            {
                // Swallow to avoid crashing service at startup if local model cannot be initialized
            }
            ServiceLog("LocalLLM loaded and ready, heres what it has to say to Hello: "+(await QueryLocalLLMAsync("Hello!")).Response);
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
            string fileName = Path.GetFileName(new Uri(ModelDownloadUrl).AbsolutePath);
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
                    ContextSize = 512,
                    GpuLayerCount = 0,    // Explicitly force CPU
                    Threads = 4,
                    BatchSize = 128
                };

                // Load weights and create context/executor
                modelWeights = LLamaWeights.LoadFromFile(modelParams);
                llmContext = modelWeights.CreateContext(modelParams);
                executor = new InteractiveExecutor(llmContext);

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
