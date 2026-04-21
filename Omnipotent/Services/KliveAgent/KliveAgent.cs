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
        public KliveAgentStats Stats { get; private set; } = new();

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

        // ── Message Handling ──

        public async Task<AgentChatResponse> HandleIncomingMessage(
            string message,
            AgentSourceChannel channel,
            string conversationId = null,
            string senderName = null)
        {
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
