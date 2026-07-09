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
        // 0..100 setup progress for the website's loading bar; 100 == ready to talk. Advanced step-by-step
        // through ServiceMain so the bar reflects the (codebase-index-dominated) warmup actually happening.
        private volatile int initializationProgress = 0;

        // Discord DM handler delegate — set by KliveBotDiscord when agent is active.
        // Args: (messageContent, channelId, authorDiscordId). KliveAgent only serves Klives over Discord.
        public Func<string, string, ulong, Task<string>> DiscordDMHandler { get; private set; }

        public KliveAgent()
        {
            name = "KliveAgent";
            threadAnteriority = ThreadAnteriority.Standard;
        }

        /// <summary>
        /// Fetches a budgeted cross-system knowledge block (KliveRAG) for the system prompt. Resolves the
        /// service lazily and fails soft — returns "" if KliveRAG is absent, cold, slow or errors, so the
        /// prompt build never blocks or breaks when the index isn't there. Races a short timeout.
        /// </summary>
        public async Task<string> SearchKnowledgeForPromptAsync(string userMessage, int maxTokens)
        {
            try
            {
                var rag = GetActiveServices()
                    .OfType<Omnipotent.Services.KliveRAG.KliveRAG>()
                    .FirstOrDefault(s => s.IsServiceActive());
                if (rag == null) return string.Empty;
                return await rag.SearchForPromptAsync(userMessage, maxTokens, TimeSpan.FromMilliseconds(400));
            }
            catch { return string.Empty; }
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
                SetInitProgress(3, "Warming up internal subsystems…");

                // Ensure data directories exist
                await EnsureDirectories();

                Stats = new KliveAgentStats(Path.Combine(
                    OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentDirectory),
                    "KliveAgentStats.json"))
                {
                    ErrorLogger = (ex, msg) => _ = ServiceLogError(ex, msg, false)
                };
                await Stats.InitializeAsync();

                // Initialize subsystems
                SetInitProgress(15, "Starting the script engine…");
                scriptEngine = new KliveAgentScriptEngine(this);
                scriptEngine.Initialize();
                // Pay Roslyn's cold-start now (overlapped with the rest of init below) so the user's first
                // script doesn't eat the ~1s+ first-compile penalty. Fire-and-forget; safe if it fails.
                _ = scriptEngine.WarmupAsync();

                SetInitProgress(30, "Loading memory…");
                Memory = new KliveAgentMemory(this);
                await Memory.InitializeAsync();

                // One-shot cleanup of legacy auto-saved "completed task" changelog memories.
                // The previous brain auto-recorded every successful turn, which polluted prompts.
                // Real memories (durable facts about reality) are preserved.
                try
                {
                    var pruned = await Memory.PruneAutoCompletedTaskMemoriesAsync();
                    if (pruned > 0)
                        await ServiceLog($"[KliveAgent] Pruned {pruned} legacy auto-completed-task memor{(pruned == 1 ? "y" : "ies")}.");
                }
                catch (Exception pruneEx)
                {
                    await ServiceLogError(pruneEx, "[KliveAgent] Memory prune failed (non-fatal).");
                }

                SetInitProgress(45, "Restoring background tasks…");
                BackgroundTasks = new KliveAgentBackgroundTasks(this, scriptEngine);
                await BackgroundTasks.InitializeAsync();

                Capabilities = new KliveAgentCapabilityRegistry(this);

                // Initialize codebase intelligence (spec Ch. 3, 4, 7) — the slowest part of warmup.
                SetInitProgress(55, "Indexing the codebase…");
                var codebaseRoot = ResolveCodebaseRoot();
                var indexCacheDir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentIndexDirectory);
                CodebaseIndex = new KliveAgentCodebaseIndex(codebaseRoot, indexCacheDir);
                await CodebaseIndex.InitializeAsync();

                SetInitProgress(75, "Building the symbol graph…");
                SymbolGraph = new KliveAgentSymbolGraph(CodebaseIndex);
                await SymbolGraph.BuildAsync();

                SetInitProgress(88, "Preparing the agent brain…");
                RepoMap = new KliveAgentRepoMap(CodebaseIndex, SymbolGraph);

                brain = new KliveAgentBrain(this, scriptEngine, Memory);

                // Set up Discord DM handler
                DiscordDMHandler = HandleDiscordDM;

                // Load persisted conversations
                SetInitProgress(95, "Loading conversations…");
                await LoadConversationsAsync();

                SetInitProgress(100, "KliveAgent is ready.");
                Interlocked.Exchange(ref initializationState, InitializationStateReady);

                // Background watchdog: cancels hung (zero-progress) runs and evicts stale pending entries.
                _ = RunPendingRunWatchdogAsync();

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

        /// <summary>Advance the setup progress (0..100) + the human-readable step message the website's
        /// loading bar shows while KliveAgent warms up.</summary>
        private void SetInitProgress(int percent, string message)
        {
            initializationProgress = Math.Clamp(percent, 0, 100);
            initializationMessage = message;
        }

        /// <summary>Setup status for the website's loading bar. ready == true (progress 100) means the agent
        /// can be talked to. Safe to call at any time, including before initialization finishes.</summary>
        public (bool ready, string state, int progress, string message) GetInitializationStatus()
        {
            var currentState = Volatile.Read(ref initializationState);
            string state = currentState switch
            {
                InitializationStateReady => "ready",
                InitializationStateFailed => "failed",
                _ => "starting",
            };
            int progress = currentState == InitializationStateReady ? 100 : Math.Clamp(initializationProgress, 0, 100);
            return (currentState == InitializationStateReady, state, progress, initializationMessage);
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
            string senderName = null,
            Action<AgentProgressUpdate> onProgress = null,
            CancellationToken cancellationToken = default)
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
            var response = await brain.ProcessMessageAsync(message, conversation, senderName, onProgress, cancellationToken);

            // Persist after every turn so a crash never loses recent messages (the Discord path used
            // to persist only every 5th message, dropping up to ~5 turns on an unexpected restart).
            await PersistConversationAsync(conversation);

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
                // Carry the user's prompt so a page that reloads mid-run can re-render the turn before
                // it has been persisted to the conversation file.
                UserMessage = message,
                // No canned placeholder — the bubble fills with the agent's real prose + code as it
                // streams in (or its final answer for a no-script reply).
                Response = string.Empty,
                CancellationSource = new CancellationTokenSource()
            };

            pendingApiResponses[pendingResponse.RequestId] = pendingResponse;
            var runToken = pendingResponse.CancellationSource.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    // Stream the agent's prose + the scripts it runs into the pending response so the
                    // poller shows it talking while it works, with code appearing as it executes. Every
                    // update also bumps LastProgressAt — the heartbeat the stall watchdog watches.
                    var response = await HandleIncomingMessage(message, AgentSourceChannel.API, conversationId, senderName,
                        onProgress: update =>
                        {
                            if (update.Text != null) pendingResponse.Response = update.Text;
                            // COPY-ON-WRITE the live lists: the poll route serializes this object from another
                            // thread, and Newtonsoft throws "Collection was modified" (→ HTTP 500 on the poll)
                            // if it enumerates a List while we mutate it. Always hand the reader a fresh,
                            // immutable snapshot instead of mutating/sharing the run's live list.
                            if (update.Scripts != null) pendingResponse.ScriptsExecuted = new List<AgentScriptResult>(update.Scripts);
                            pendingResponse.Iteration = update.Iteration;
                            pendingResponse.Phase = update.Phase;
                            pendingResponse.StatusNote = update.StatusNote;
                            pendingResponse.PromptTokens = update.PromptTokens;
                            pendingResponse.CompletionTokens = update.CompletionTokens;
                            if (update.NewActivity != null)
                                pendingResponse.Activity = new List<AgentActivityEvent>(pendingResponse.Activity) { update.NewActivity };
                            // Computer-use: stream the latest annotated frame (video) + any approval card.
                            if (update.Frame != null) pendingResponse.LatestFrame = Convert.ToBase64String(update.Frame);
                            if (update.Approval != null)
                                pendingResponse.PendingApproval = update.Approval.Status == "pending" ? update.Approval : null;
                            pendingResponse.LastProgressAt = DateTime.UtcNow;
                        },
                        cancellationToken: runToken);
                    pendingResponse.FinalResponse = response;
                    pendingResponse.Phase = "final";
                    pendingResponse.Status = runToken.IsCancellationRequested
                        ? AgentTaskStatus.Cancelled
                        : response.Success ? AgentTaskStatus.Completed : AgentTaskStatus.Failed;
                    pendingResponse.ErrorMessage = response.Success ? null : response.ErrorMessage;

                    if (conversations.TryGetValue(conversationId, out var conversation))
                    {
                        await PersistConversationAsync(conversation);
                    }
                }
                catch (OperationCanceledException)
                {
                    pendingResponse.Status = AgentTaskStatus.Cancelled;
                    pendingResponse.ErrorMessage = "Run was stopped.";
                    pendingResponse.FinalResponse = new AgentChatResponse
                    {
                        Success = false,
                        ConversationId = conversationId,
                        Response = string.IsNullOrWhiteSpace(pendingResponse.Response)
                            ? "_(Run stopped before completion.)_"
                            : pendingResponse.Response,
                        ErrorMessage = "Run was stopped."
                    };
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
                    pendingResponse.LastProgressAt = DateTime.UtcNow;
                    try { pendingResponse.CancellationSource?.Dispose(); } catch { }
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

        /// <summary>Manually stop a running message (the website's Stop button). Cancels the per-run
        /// token, which unwinds the LLM call, the agent loop, and any running script; the run then
        /// resolves to a truthful partial answer. Returns false if no such running request exists.</summary>
        public bool CancelPendingApiResponse(string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId)) return false;
            if (!pendingApiResponses.TryGetValue(requestId, out var pending)) return false;
            if (pending.CompletedAt != null) return false;
            try { pending.CancellationSource?.Cancel(); } catch { }
            return true;
        }

        /// <summary>Resolve a pending computer-use approval (the website's Approve/Deny buttons). Forwards to
        /// HostControlManager's ApprovalBroker, which unblocks the waiting action. Returns false if no such
        /// approval is pending.</summary>
        public async Task<bool> SubmitApprovalAsync(string approvalId, bool approved)
        {
            if (string.IsNullOrWhiteSpace(approvalId)) return false;
            var hcms = await GetServicesByType<Omnipotent.Services.HostControl.HostControlManager>();
            if (hcms == null || hcms.Length == 0) return false;
            return ((Omnipotent.Services.HostControl.HostControlManager)hcms[0]).Approvals.SubmitDecision(approvalId, approved);
        }

        // ── Stall watchdog + pending-response eviction ──
        // A run is "hung" only when it produces ZERO progress (no LLM reply, token, iteration, or script
        // result) for a long, configurable window — NOT merely because it is slow. This lets genuinely
        // long tasks run while still guaranteeing nothing blocks forever. The same loop evicts completed
        // pending entries after a generous window so the in-memory dict can't grow unbounded.
        private static readonly TimeSpan PendingRetentionWindow = TimeSpan.FromHours(6);

        private async Task RunPendingRunWatchdogAsync()
        {
            while (true)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30));

                    double stallMinutes = await GetIntOmniSetting("KliveAgent_StallTimeoutMinutes", 5);
                    if (stallMinutes < 1) stallMinutes = 1;
                    var stallWindow = TimeSpan.FromMinutes(stallMinutes);
                    var now = DateTime.UtcNow;

                    foreach (var kvp in pendingApiResponses)
                    {
                        var pending = kvp.Value;

                        // Running but silent for too long → treat as hung, cancel it.
                        if (pending.CompletedAt == null
                            && pending.CancellationSource != null
                            && !pending.CancellationSource.IsCancellationRequested
                            && now - pending.LastProgressAt > stallWindow)
                        {
                            try { pending.CancellationSource.Cancel(); } catch { }
                            try { await ServiceLog($"[KliveAgent] Stall watchdog cancelled run {kvp.Key} after {stallMinutes:0}min of no progress."); } catch { }
                        }

                        // Completed long ago → evict (durable record lives in the persisted conversation).
                        if (pending.CompletedAt != null && now - pending.CompletedAt.Value > PendingRetentionWindow)
                        {
                            pendingApiResponses.TryRemove(kvp.Key, out _);
                        }
                    }
                }
                catch (Exception ex)
                {
                    try { await ServiceLogError(ex, "[KliveAgent] Pending-run watchdog iteration failed (non-fatal)."); } catch { }
                }
            }
        }

        private async Task<string> HandleDiscordDM(string message, string channelId, ulong authorDiscordId)
        {
            // Defense-in-depth: KliveAgent is gated to Klives over Discord at the KliveBotDiscord
            // boundary too, but never trust a single gate for a service this powerful.
            if (authorDiscordId != OmniPaths.KlivesDiscordAccountID)
            {
                return "Nice try. KliveAgent only takes orders from Klives.";
            }

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
                result.RequiresConfirmation && !result.Success,
                result.DurationMs);
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
                    catch (Exception ex) { await ServiceLogError(ex, $"[KliveAgent] Skipped unreadable conversation file {Path.GetFileName(file)}.", false); }
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
            catch (Exception ex) { await ServiceLogError(ex, "[KliveAgent] Failed to persist conversation.", false); }
        }
    }
}
