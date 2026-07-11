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

        /// <summary>Append a user turn to a tool-calling session.</summary>
        public void AppendUserMessageToToolSession(string sessionId, string content)
        {
            lock (sessions)
            {
                if (sessions.TryGetValue(sessionId, out var s))
                    s.structuredMessages.Add(new HFWrapper.HFMessage { role = "user", content = content ?? string.Empty });
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
            if (!string.IsNullOrWhiteSpace(text))
                parts.Add(new HFWrapper.HFTextPart { text = text });
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
        private static void PruneOldToolImages(KliveLLMSession s, int keepRecent)
        {
            if (keepRecent < 1) keepRecent = 1;
            var imageIdx = new List<int>();
            for (int i = 0; i < s.structuredMessages.Count; i++)
            {
                var m = s.structuredMessages[i];
                if (m.role == "user" && MessageHasImage(m)) imageIdx.Add(i);
            }
            int toPrune = imageIdx.Count - keepRecent;
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
                        content = content ?? string.Empty
                    });
                    PruneOldToolResults(s, keepRecentFull);
                }
            }
        }

        /// <summary>Shorten the CONTENT of tool-result messages older than the most recent <paramref name="keepRecent"/>,
        /// replacing a long output with a tiny stub. The message and its tool_call_id/name pairing are KEPT (so the
        /// tool-call protocol stays valid) — only the bulky, now-stale text is dropped. The model can always re-run a
        /// tool to fetch fresh data. This is the text analog of <see cref="PruneOldToolImages"/>.</summary>
        private static void PruneOldToolResults(KliveLLMSession s, int keepRecent, int stubOverChars = 240)
        {
            if (keepRecent < 1) keepRecent = 1;
            var toolIdx = new List<int>();
            for (int i = 0; i < s.structuredMessages.Count; i++)
                if (s.structuredMessages[i].role == "tool") toolIdx.Add(i);

            int toPrune = toolIdx.Count - keepRecent;
            for (int k = 0; k < toPrune; k++)
            {
                var m = s.structuredMessages[toolIdx[k]];
                if (m.content is string str && str.Length > stubOverChars)
                {
                    s.structuredMessages[toolIdx[k]] = new HFWrapper.HFMessage
                    {
                        role = "tool",
                        tool_call_id = m.tool_call_id,
                        name = m.name,
                        content = str.Substring(0, 160).TrimEnd() +
                            $"\n[…{str.Length - 160} chars of this earlier tool output trimmed to save context — re-run the tool if you still need it.]"
                    };
                }
            }
        }

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
            int tokens = chars / 4;
            if (MessageHasImage(m)) tokens += 1100; // a downscaled screenshot the text estimate misses
            return tokens;
        }

        private static void CompactToolSessionIfNeeded(KliveLLMSession s, int aboveTokens, int keepRecent)
        {
            var msgs = s.structuredMessages;
            if (msgs.Count <= keepRecent + 2) return;
            int total = 0;
            foreach (var m in msgs) total += EstimateMessageTokens(m);
            if (total <= aboveTokens) return;

            int headStart = 0; // keep leading system message(s) verbatim
            while (headStart < msgs.Count && msgs[headStart].role == "system") headStart++;

            // Tail = last keepRecent messages, advanced so it does NOT begin on an ORPHAN tool result (one whose
            // assistant tool_call would be in the dropped head) — that keeps the tool-call protocol valid.
            int cut = Math.Max(headStart, msgs.Count - keepRecent);
            while (cut < msgs.Count && msgs[cut].role == "tool") cut++;
            if (cut <= headStart || cut >= msgs.Count) return; // nothing safe to drop / would empty the recent tail

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[Context compacted to keep the window sharp. Digest of EARLIER steps this task — the verbatim messages were summarised. Trust this plus the recent messages below; DON'T redo discovery already shown here, and re-run a tool only if you need fresh detail:]");
            for (int i = headStart; i < cut; i++)
            {
                var m = msgs[i];
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
            var digest = sb.ToString();
            const int digestMaxChars = 6000; // keep the TAIL (most recent progress) when the digest itself is long
            if (digest.Length > digestMaxChars)
                digest = digest.Substring(0, 420) + "\n…(older steps elided)…\n" + digest.Substring(digest.Length - (digestMaxChars - 440));

            var rebuilt = new List<HFWrapper.HFMessage>(msgs.Count - (cut - headStart) + 1);
            for (int i = 0; i < headStart; i++) rebuilt.Add(msgs[i]);
            rebuilt.Add(new HFWrapper.HFMessage { role = "user", content = digest });
            for (int i = cut; i < msgs.Count; i++) rebuilt.Add(msgs[i]);
            s.structuredMessages = rebuilt;

            static string Clip(string x, int n) => string.IsNullOrEmpty(x) ? "" : (x.Length <= n ? x : x.Substring(0, n) + "…");
            static string FirstLine(string x) { int nl = x.IndexOf('\n'); return nl < 0 ? x : x.Substring(0, nl); }
        }

        /// <summary>Send the session's current structured message log (plus the tool definitions) to the
        /// remote provider. Appends the assistant response — including any requested tool_calls — back to
        /// the log, and returns it. ToolCalls is populated when the model wants to invoke tools.</summary>
        public async Task<KliveLLMResponse> QueryToolSessionAsync(string sessionId, List<HFWrapper.HFTool> tools, int? maxTokensOverride = null, string? modelOverride = null, CancellationToken cancellationToken = default, Action<string>? onToken = null, string? thinkingOverride = null, Action<HFWrapper.HFToolCall>? onToolCallComplete = null)
        {
            KliveLLMSession session;
            List<HFWrapper.HFMessage> snapshot;
            lock (sessions)
            {
                if (!sessions.TryGetValue(sessionId, out session))
                    return new KliveLLMResponse { Success = false, ErrorMessage = "Tool session not found. Call StartToolSession first.", SessionId = sessionId };
                // Keep the request from spiralling into hundreds-of-thousands of tokens (where the model
                // mis-perceives its own context). Compacts the older middle in place when over budget.
                CompactToolSessionIfNeeded(session, InTaskCompactAboveTokens, InTaskKeepRecentMessages);
                snapshot = new List<HFWrapper.HFMessage>(session.structuredMessages);
            }

            var response = await SendRemoteToolRequestAsync(snapshot, tools, maxTokensOverride, modelOverride: modelOverride, cancellationToken: cancellationToken, onToken: onToken, thinkingOverride: thinkingOverride, onToolCallComplete: onToolCallComplete);
            var msg = response.choices[0].message;
            var content = HFWrapper.ContentToText(msg?.content);
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
                GenerationId = response.id,
                CostUsd = response.usage?.cost,
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
            session.chatHistory.AddMessage(AuthorRole.User, prompt);
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
            Action<HFWrapper.HFToolCall>? onToolCallComplete = null)
        {
            RemoteLLMProviderConfiguration remoteProvider = await GetRemoteProviderConfigurationAsync(forceFreeModel);

            HFWrapper.HFLLMInferenceRequest payload = new HFWrapper.HFLLMInferenceRequest()
            {
                model = string.IsNullOrWhiteSpace(modelOverride) ? remoteProvider.Model : modelOverride,
                stream = false,
                max_tokens = maxTokensOverride,
                tools = tools,
            };
            ApplyServiceTier(ref payload, remoteProvider);
            ApplyUsageAccounting(ref payload, remoteProvider);
            ApplyThinkingPreference(ref payload, remoteProvider, thinkingOverride);
            payload.BuildMessagesFromList(structuredMessages);
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
                throw new HttpRequestException(
                    $"{remoteProvider.DisplayName} streaming request failed with status {(int)response.StatusCode} ({response.ReasonPhrase}). Body: {body}");
            }

            var contentSb = new StringBuilder();
            var toolCallsByIndex = new SortedDictionary<int, HFWrapper.HFToolCall>();
            var argsByIndex = new Dictionary<int, StringBuilder>();
            HFWrapper.HFLLMInferenceResponse.UsageDetails usage = null;
            string finishReason = null;

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

            return new HFWrapper.HFLLMInferenceResponse
            {
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

        private async Task<HFWrapper.HFLLMInferenceResponse> SendPayloadWithRetryAsync(
            RemoteLLMProviderConfiguration remoteProvider,
            HFWrapper.HFLLMInferenceRequest payload,
            CancellationToken cancellationToken = default)
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
                    lastError = ex;
                    if (attempt >= RemoteInferenceMaxAttempts) throw;
                    try { await ServiceLog($"Remote LLM network error (attempt {attempt}/{RemoteInferenceMaxAttempts}): {ex.Message}. Retrying."); } catch { }
                    await DelayBeforeRetryAsync(attempt, null, cancellationToken);
                    continue;
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
                        bool transient = status == 408 || status == 429 || status >= 500;
                        var httpEx = new HttpRequestException(
                            $"{remoteProvider.DisplayName} request failed with status {status} ({response.ReasonPhrase}). Body: {responseContent}");

                        if (transient && attempt < RemoteInferenceMaxAttempts)
                        {
                            lastError = httpEx;
                            try { await ServiceLog($"Remote LLM transient {status} (attempt {attempt}/{RemoteInferenceMaxAttempts}). Retrying."); } catch { }
                            await DelayBeforeRetryAsync(attempt, response, cancellationToken);
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
                            await DelayBeforeRetryAsync(attempt, null, cancellationToken);
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

        private static async Task DelayBeforeRetryAsync(int attempt, HttpResponseMessage response, CancellationToken cancellationToken = default)
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
            await Task.Delay(delay, cancellationToken);
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

            /// <summary>The provider's generation/response id (OpenRouter's `id`), used to fetch
            /// the authoritative per-request cost from /generation. Null when unavailable.</summary>
            public string? GenerationId { get; set; }

            /// <summary>The REAL USD cost of this turn, reported by OpenRouter in the response's usage
            /// object (usage.cost, credits == USD). This is authoritative and needs no /generation
            /// round-trip. Null when the provider doesn't report a cost (HuggingFace/local), in which
            /// case callers fall back to a provisional estimate + optional /generation reconciliation.</summary>
            public double? CostUsd { get; set; }

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

                    var chatMsg = new ChatHistory.Message(AuthorRole.User, prompt);
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
