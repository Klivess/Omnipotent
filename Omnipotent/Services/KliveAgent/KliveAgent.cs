using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAgent.Models;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;

namespace Omnipotent.Services.KliveAgent
{
    public class KliveAgent : OmniService
    {
        private const int InitializationStateStarting = 0;
        private const int InitializationStateReady = 1;
        private const int InitializationStateFailed = 2;

        public KliveAgentMemory Memory { get; private set; }
        public KliveAgentBackgroundTasks BackgroundTasks { get; private set; }
        public KliveAgentCapabilityRegistry Capabilities { get; private set; }
        public KliveAgentStats Stats { get; private set; } = null!;

        // Codebase intelligence subsystems (spec Ch. 3, 4, 7)
        public KliveAgentCodebaseIndex CodebaseIndex { get; private set; }
        public KliveAgentSymbolGraph SymbolGraph { get; private set; }
        public KliveAgentRepoMap RepoMap { get; private set; }

        private KliveAgentBrain brain;
        private KliveAgentScriptEngine scriptEngine;
        private readonly ConcurrentDictionary<string, AgentConversation> conversations = new();
        private readonly ConcurrentDictionary<string, AgentPendingChatResponse> pendingApiResponses = new();
        private int initializationState = InitializationStateStarting;
        private string initializationMessage = "KliveAgent is initializing.";

        // Discord DM handler delegate — set by KliveBotDiscord when agent is active
        public Func<string, string, Task<string>> DiscordDMHandler { get; private set; }

        public KliveAgent()
        {
            name = "KliveAgent";
            threadAnteriority = ThreadAnteriority.Standard;
        }

        protected override async void ServiceMain()
        {
            var enabled = await GetBoolOmniSetting("KliveAgent_Enabled", defaultValue: true);
            if (!enabled)
            {
                await ServiceLog("[KliveAgent] Service is disabled via OmniSettings. Exiting.");
                return;
            }

            await ServiceLog("[KliveAgent] Initializing...");

            var routes = new KliveAgentRoutes(this);
            await routes.RegisterRoutes();
            await ServiceLog("[KliveAgent] API routes registered.");

            try
            {
                initializationMessage = "KliveAgent is warming up internal subsystems.";

                // Ensure data directories exist
                await EnsureDirectories();

                Stats = new KliveAgentStats(Path.Combine(
                    OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentDirectory),
                    "KliveAgentStats.json"));
                await Stats.InitializeAsync();

                // Initialize subsystems
                scriptEngine = new KliveAgentScriptEngine(this);
                scriptEngine.Initialize();

                Memory = new KliveAgentMemory(this);
                await Memory.InitializeAsync();

                BackgroundTasks = new KliveAgentBackgroundTasks(this, scriptEngine);
                await BackgroundTasks.InitializeAsync();

                Capabilities = new KliveAgentCapabilityRegistry(this);

                // Initialize codebase intelligence (spec Ch. 3, 4, 7)
                var codebaseRoot = ResolveCodebaseRoot();
                var indexCacheDir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentIndexDirectory);
                CodebaseIndex = new KliveAgentCodebaseIndex(codebaseRoot, indexCacheDir);
                await CodebaseIndex.InitializeAsync();

                SymbolGraph = new KliveAgentSymbolGraph(CodebaseIndex);
                await SymbolGraph.BuildAsync();

                RepoMap = new KliveAgentRepoMap(CodebaseIndex, SymbolGraph);

                brain = new KliveAgentBrain(this, scriptEngine, Memory);

                // Set up Discord DM handler
                DiscordDMHandler = HandleDiscordDM;

                // Load persisted conversations
                await LoadConversationsAsync();

                initializationMessage = "KliveAgent is ready.";
                Interlocked.Exchange(ref initializationState, InitializationStateReady);

                await ServiceLog("[KliveAgent] Initialized and ready. All systems nominal.");
            }
            catch (Exception ex)
            {
                initializationMessage = $"KliveAgent failed to initialize: {ex.Message}";
                Interlocked.Exchange(ref initializationState, InitializationStateFailed);
                await ServiceLogError(ex, "[KliveAgent] Initialization failed.");
            }
        }

        public bool TryGetApiAvailability(out HttpStatusCode statusCode, out string message)
        {
            var currentState = Volatile.Read(ref initializationState);
            if (currentState == InitializationStateReady)
            {
                statusCode = HttpStatusCode.OK;
                message = string.Empty;
                return true;
            }

            if (currentState == InitializationStateFailed)
            {
                statusCode = HttpStatusCode.ServiceUnavailable;
                message = initializationMessage;
                return false;
            }

            statusCode = HttpStatusCode.Accepted;
            message = initializationMessage;
            return false;
        }

        // ── Codebase Root Resolution ──

        internal static string ResolveCodebaseRoot()
        {
            var candidates = new[]
            {
                AppDomain.CurrentDomain.BaseDirectory,
                OmniPaths.CodebaseDirectory
            };
            foreach (var candidate in candidates)
            {
                var dir = candidate;
                for (int i = 0; i < 8 && dir != null; i++)
                {
                    if (File.Exists(Path.Combine(dir, "Omnipotent.sln")) ||
                        Directory.Exists(Path.Combine(dir, "Omnipotent")))
                        return dir;
                    dir = Path.GetDirectoryName(dir);
                }
            }
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        // ── Message Handling ──

        public async Task<AgentChatResponse> HandleIncomingMessage(
            string message,
            AgentSourceChannel channel,
            string conversationId = null,
            string senderName = null)
        {
            if (!TryGetApiAvailability(out _, out var availabilityMessage))
            {
                return new AgentChatResponse
                {
                    Success = false,
                    Response = availabilityMessage,
                    ErrorMessage = availabilityMessage
                };
            }

            var enabled = await GetBoolOmniSetting("KliveAgent_Enabled", defaultValue: true);
            if (!enabled)
            {
                return new AgentChatResponse
                {
                    Success = false,
                    Response = "KliveAgent is currently disabled.",
                    ErrorMessage = "KliveAgent is disabled via OmniSettings."
                };
            }

            // Get or create conversation
            if (string.IsNullOrEmpty(conversationId))
            {
                conversationId = Guid.NewGuid().ToString("N");
            }

            var conversation = conversations.GetOrAdd(conversationId, _ => new AgentConversation
            {
                ConversationId = conversationId,
                SourceChannel = channel
            });

            // Process through the brain
            var response = await brain.ProcessMessageAsync(message, conversation, senderName);

            // Persist conversation periodically (every 5 messages)
            if (conversation.Messages.Count % 5 == 0)
            {
                await PersistConversationAsync(conversation);
            }

            return response;
        }

        public async Task<AgentChatResponse> QueueIncomingApiMessageAsync(
            string message,
            string conversationId = null,
            string senderName = null)
        {
            if (!TryGetApiAvailability(out _, out var availabilityMessage))
            {
                return new AgentChatResponse
                {
                    Success = false,
                    Response = availabilityMessage,
                    ErrorMessage = availabilityMessage
                };
            }

            var enabled = await GetBoolOmniSetting("KliveAgent_Enabled", defaultValue: true);
            if (!enabled)
            {
                return new AgentChatResponse
                {
                    Success = false,
                    Response = "KliveAgent is currently disabled.",
                    ErrorMessage = "KliveAgent is disabled via OmniSettings."
                };
            }

            if (string.IsNullOrEmpty(conversationId))
            {
                conversationId = Guid.NewGuid().ToString("N");
            }

            var pendingResponse = new AgentPendingChatResponse
            {
                ConversationId = conversationId,
                Response = BuildPendingApiResponseText()
            };

            pendingApiResponses[pendingResponse.RequestId] = pendingResponse;

            _ = Task.Run(async () =>
            {
                try
                {
                    var response = await HandleIncomingMessage(message, AgentSourceChannel.API, conversationId, senderName);
                    pendingResponse.FinalResponse = response;
                    pendingResponse.Status = response.Success ? AgentTaskStatus.Completed : AgentTaskStatus.Failed;
                    pendingResponse.ErrorMessage = response.Success ? null : response.ErrorMessage;

                    if (conversations.TryGetValue(conversationId, out var conversation))
                    {
                        await PersistConversationAsync(conversation);
                    }
                }
                catch (Exception ex)
                {
                    pendingResponse.Status = AgentTaskStatus.Failed;
                    pendingResponse.ErrorMessage = ex.Message;
                    pendingResponse.FinalResponse = new AgentChatResponse
                    {
                        Success = false,
                        ConversationId = conversationId,
                        Response = "KliveAgent failed while completing the request.",
                        ErrorMessage = ex.ToString()
                    };
                }
                finally
                {
                    pendingResponse.CompletedAt = DateTime.UtcNow;
                }
            });

            return new AgentChatResponse
            {
                Success = true,
                ConversationId = conversationId,
                Response = pendingResponse.Response,
                IsPending = true,
                PendingRequestId = pendingResponse.RequestId
            };
        }

        public AgentPendingChatResponse GetPendingApiResponse(string requestId)
        {
            pendingApiResponses.TryGetValue(requestId, out var pendingResponse);
            return pendingResponse;
        }

        private static string BuildPendingApiResponseText()
        {
            return "I’m on it. I’m inspecting the request and running any needed scripts now. I’ll send the finished answer as soon as execution completes.";
        }

        private async Task<string> HandleDiscordDM(string message, string channelId)
        {
            var conversationId = $"discord-{channelId}";
            var response = await HandleIncomingMessage(message, AgentSourceChannel.Discord, conversationId, "Klives");
            return response.Response;
        }

        // ── Conversation Management ──

        public List<object> GetConversationSummaries()
        {
            return conversations.Values
                .OrderByDescending(c => c.LastUpdated)
                .Select(c => (object)new
                {
                    conversationId = c.ConversationId,
                    sourceChannel = c.SourceChannel.ToString(),
                    lastUpdated = c.LastUpdated,
                    messageCount = c.Messages.Count,
                    lastMessage = c.Messages.LastOrDefault()?.Content?.Substring(0, Math.Min(c.Messages.LastOrDefault()?.Content?.Length ?? 0, 100))
                })
                .ToList();
        }

        public AgentConversation GetConversation(string conversationId)
        {
            conversations.TryGetValue(conversationId, out var conversation);
            return conversation;
        }

        public List<AgentCapabilityDefinition> GetCapabilities(string? category = null)
        {
            return Capabilities.GetCapabilities(category);
        }

        public async Task<AgentCapabilityInvocationResult> ExecuteCapabilityAsync(AgentCapabilityInvocationRequest request, AgentCapabilityInvocationContext context)
        {
            var result = await Capabilities.ExecuteAsync(request, context);
            Stats.RecordCapability(
                request.Capability,
                result.Success,
                result.RequiresConfirmation && !result.Success);
            return result;
        }

        // ── Persistence ──

        private async Task EnsureDirectories()
        {
            var dirs = new[]
            {
                OmniPaths.GlobalPaths.KliveAgentDirectory,
                OmniPaths.GlobalPaths.KliveAgentMemoriesDirectory,
                OmniPaths.GlobalPaths.KliveAgentConversationsDirectory,
                OmniPaths.GlobalPaths.KliveAgentBackgroundTasksDirectory,
                OmniPaths.GlobalPaths.KliveAgentScriptLogsDirectory,
            };

            foreach (var dir in dirs)
            {
                var path = OmniPaths.GetPath(dir);
                if (!Directory.Exists(path))
                {
                    await GetDataHandler().CreateDirectory(path);
                }
            }
        }

        private async Task LoadConversationsAsync()
        {
            try
            {
                var dir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentConversationsDirectory);
                if (!Directory.Exists(dir)) return;

                foreach (var file in Directory.GetFiles(dir, "*.json"))
                {
                    try
                    {
                        var conv = await GetDataHandler().ReadAndDeserialiseDataFromFile<AgentConversation>(file);
                        if (conv != null)
                        {
                            conversations[conv.ConversationId] = conv;
                        }
                    }
                    catch { }
                }

                if (conversations.Count > 0)
                {
                    await ServiceLog($"[KliveAgent] Loaded {conversations.Count} persisted conversations.");
                }
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "[KliveAgent] Error loading conversations.");
            }
        }

        private async Task PersistConversationAsync(AgentConversation conversation)
        {
            try
            {
                var path = Path.Combine(
                    OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentConversationsDirectory),
                    $"{conversation.ConversationId}.json");
                await GetDataHandler().SerialiseObjectToFile(path, conversation);
            }
            catch { }
        }
    }
}
