using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAgent.Models;
using System.Collections.Concurrent;

namespace Omnipotent.Services.KliveAgent
{
    public class KliveAgent : OmniService
    {
        public KliveAgentMemory Memory { get; private set; }
        public KliveAgentBackgroundTasks BackgroundTasks { get; private set; }
        public KliveAgentCapabilityRegistry Capabilities { get; private set; }
        public KliveAgentStats Stats { get; private set; } = new();

        // Codebase intelligence subsystems (spec Ch. 3, 4, 7)
        public KliveAgentCodebaseIndex CodebaseIndex { get; private set; }
        public KliveAgentSymbolGraph SymbolGraph { get; private set; }
        public KliveAgentRepoMap RepoMap { get; private set; }

        private KliveAgentBrain brain;
        private KliveAgentScriptEngine scriptEngine;
        private readonly ConcurrentDictionary<string, AgentConversation> conversations = new();

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

            // Ensure data directories exist
            await EnsureDirectories();

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

            // Register API routes
            var routes = new KliveAgentRoutes(this);
            await routes.RegisterRoutes();

            // Set up Discord DM handler
            DiscordDMHandler = HandleDiscordDM;

            // Load persisted conversations
            await LoadConversationsAsync();

            await ServiceLog("[KliveAgent] Initialized and ready. All systems nominal.");
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

        public Task<AgentCapabilityInvocationResult> ExecuteCapabilityAsync(AgentCapabilityInvocationRequest request, AgentCapabilityInvocationContext context)
        {
            return Capabilities.ExecuteAsync(request, context);
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
