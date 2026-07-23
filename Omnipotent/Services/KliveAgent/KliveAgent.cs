using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAgent.Models;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.KliveAgent
{
    public class KliveAgent : OmniService
    {
        private const int InitializationStateStarting = 0;
        private const int InitializationStateReady = 1;
        private const int InitializationStateFailed = 2;

        public KliveAgentMemory Memory { get; private set; }
        public KliveAgentBackgroundTasks BackgroundTasks { get; private set; }
        public KliveAgentScheduler Scheduler { get; private set; }
        public KliveAgentCapabilityRegistry Capabilities { get; private set; }
        public KliveAgentStats Stats { get; private set; } = null!;

        // Codebase intelligence subsystems (spec Ch. 3, 4, 7)
        public KliveAgentCodebaseIndex CodebaseIndex { get; private set; }
        public KliveAgentSymbolGraph SymbolGraph { get; private set; }
        public KliveAgentRepoMap RepoMap { get; private set; }

        private KliveAgentBrain brain;
        private KliveAgentScriptEngine scriptEngine;
        private readonly ConcurrentDictionary<string, AgentConversation> conversations = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, AgentPendingChatResponse> pendingApiResponses = new();
        private readonly ConcurrentDictionary<string, string> activeRunByConversation = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> runByClientMessage = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> conversationAdmissionGates = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> conversationExecutionGates = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> conversationPersistGates = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> runPersistGates = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, object> conversationSyncs = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> scheduledRunPersists = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, AgentLongTermJobLink> longTermJobs = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> jobByClientId = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, AgentNotification> notifications = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim longTermJobCreationGate = new(1, 1);
        private static readonly Regex SafeId = new(@"^[A-Za-z0-9_-]{1,128}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private int initializationState = InitializationStateStarting;
        private int durableStateReady;
        private long serviceGeneration;
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

        /// <summary>
        /// Fetches a budgeted block of accounts from the global shared registry for the system prompt,
        /// so the agent reuses existing accounts instead of creating duplicates. Resolves the service
        /// lazily and fails soft — returns "" if the registry is absent/down.
        /// </summary>
        public Task<string> DescribeAccountsForPromptAsync(int maxTokens)
        {
            try
            {
                var reg = GetActiveServices()
                    .OfType<Omnipotent.Services.AccountRegistry.AccountRegistry>()
                    .FirstOrDefault(s => s.IsServiceActive());
                return Task.FromResult(reg?.DescribeForPrompt("KliveAgent", maxTokens) ?? string.Empty);
            }
            catch { return Task.FromResult(string.Empty); }
        }

        protected override async void ServiceMain()
        {
            var serviceToken = cancellationToken.Token;
            long generation = Interlocked.Increment(ref serviceGeneration);
            bool Superseded() =>
                serviceToken.IsCancellationRequested
                || generation != Volatile.Read(ref serviceGeneration);

            Interlocked.Exchange(ref initializationState, InitializationStateStarting);
            Interlocked.Exchange(ref durableStateReady, 0);
            initializationProgress = 0;
            initializationMessage = "KliveAgent is initializing.";

            // Status must exist even when the feature is disabled, otherwise the website polls a
            // nonexistent endpoint forever instead of showing a truthful disabled state.
            var routes = new KliveAgentRoutes(this);
            await routes.RegisterRoutes();
            if (Superseded()) return;
            await ServiceLog("[KliveAgent] API routes registered.");

            try
            {
                // Durable history/results remain useful when the model is disabled or its heavier
                // initialization fails. Load them before the execution-readiness gate.
                await EnsureDirectories();
                if (Superseded()) return;
                await LoadConversationsAsync();
                if (Superseded()) return;
                await LoadChatRunsAsync();
                if (Superseded()) return;
                await LoadLongTermJobsAsync();
                if (Superseded()) return;
                await LoadNotificationsAsync();
                if (Superseded()) return;
                Interlocked.Exchange(ref durableStateReady, 1);
                _ = MonitorLongTermJobsAsync(serviceToken);
            }
            catch (Exception ex)
            {
                if (Superseded()) return;
                initializationMessage = $"KliveAgent durable state failed to load: {ex.Message}";
                Interlocked.Exchange(ref durableStateReady, -1);
                Interlocked.Exchange(ref initializationState, InitializationStateFailed);
                await ServiceLogError(ex, "[KliveAgent] Durable state initialization failed.");
                return;
            }

            var enabled = await GetBoolOmniSetting("KliveAgent_Enabled", defaultValue: true);
            if (Superseded()) return;
            if (!enabled)
            {
                initializationMessage = "KliveAgent is disabled via OmniSettings.";
                Interlocked.Exchange(ref initializationState, InitializationStateFailed);
                await ServiceLog("[KliveAgent] Service is disabled via OmniSettings. Exiting.");
                return;
            }

            await ServiceLog("[KliveAgent] Initializing...");

            try
            {
                SetInitProgress(3, "Warming up internal subsystems…");

                Stats = new KliveAgentStats(Path.Combine(
                    OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentDirectory),
                    "KliveAgentStats.json"))
                {
                    ErrorLogger = (ex, msg) => _ = ServiceLogError(ex, msg, false)
                };
                await Stats.InitializeAsync();
                if (Superseded()) return;

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
                if (Superseded()) return;

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
                if (Superseded()) return;

                // Prospective memory: durable future intentions that fire as full agent turns.
                Scheduler = new KliveAgentScheduler(this);
                await Scheduler.InitializeAsync();
                if (Superseded()) return;

                Capabilities = new KliveAgentCapabilityRegistry(this);

                // Initialize codebase intelligence (spec Ch. 3, 4, 7) — the slowest part of warmup.
                SetInitProgress(55, "Indexing the codebase…");
                var codebaseRoot = ResolveCodebaseRoot();
                var indexCacheDir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentIndexDirectory);
                CodebaseIndex = new KliveAgentCodebaseIndex(codebaseRoot, indexCacheDir);
                await CodebaseIndex.InitializeAsync();
                if (Superseded()) return;

                SetInitProgress(75, "Building the symbol graph…");
                SymbolGraph = new KliveAgentSymbolGraph(CodebaseIndex);
                await SymbolGraph.BuildAsync();
                if (Superseded()) return;

                SetInitProgress(88, "Preparing the agent brain…");
                RepoMap = new KliveAgentRepoMap(CodebaseIndex, SymbolGraph);

                brain = new KliveAgentBrain(this, scriptEngine, Memory);

                // Set up Discord DM handler
                DiscordDMHandler = HandleDiscordDM;

                SetInitProgress(100, "KliveAgent is ready.");
                Interlocked.Exchange(ref initializationState, InitializationStateReady);

                // Background watchdog: cancels hung (zero-progress) runs and evicts stale pending entries.
                _ = RunPendingRunWatchdogAsync(serviceToken);

                // Fire scheduled tasks only once the agent can actually take messages; tasks that
                // came due while offline fire on the first tick with an explicit lateness note.
                Scheduler.StartLoop(serviceToken);

                await ServiceLog("[KliveAgent] Initialized and ready. All systems nominal.");
            }
            catch (Exception ex)
            {
                if (Superseded()) return;
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
            CancellationToken cancellationToken = default,
            string requestId = null,
            bool messageAlreadyRecorded = false,
            AgentChatRunControl runControl = null,
            Action<AgentSteeringMessage> onSteeringApplied = null,
            long? expectedServiceGeneration = null)
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
            requestId ??= Guid.NewGuid().ToString("N");
            if (!IsSafeIdentifier(conversationId))
                return InvalidIdentifierResponse("conversationId", conversationId);

            var executionGate = conversationExecutionGates.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));
            await executionGate.WaitAsync(cancellationToken);
            try
            {

            var conversation = conversations.GetOrAdd(conversationId, _ => new AgentConversation
            {
                ConversationId = conversationId,
                SourceChannel = channel
            });

            if (!messageAlreadyRecorded)
                await RecordAcceptedUserMessageAsync(conversation, requestId, message, senderName);

            // Process through the brain
            var response = await brain.ProcessMessageAsync(message, conversation, senderName, onProgress,
                cancellationToken, runControl, onSteeringApplied);

            if (expectedServiceGeneration.HasValue
                && expectedServiceGeneration.Value != Volatile.Read(ref serviceGeneration))
                throw new OperationCanceledException(
                    "KliveAgent restarted before this run could commit its terminal state.");
            await CompleteConversationTurnAsync(conversation, requestId, response,
                cancellationToken.IsCancellationRequested ? "cancelled" : response.Success ? "completed" : "failed");

            // Persist after every turn so a crash never loses recent messages (the Discord path used
            // to persist only every 5th message, dropping up to ~5 turns on an unexpected restart).
            await PersistConversationAsync(conversation);

            return response;
            }
            finally
            {
                executionGate.Release();
            }
        }

        public bool TryGetDurableApiAvailability(out HttpStatusCode statusCode, out string message)
        {
            int state = Volatile.Read(ref durableStateReady);
            if (state == 1)
            {
                statusCode = HttpStatusCode.OK;
                message = string.Empty;
                return true;
            }
            statusCode = HttpStatusCode.ServiceUnavailable;
            message = state < 0
                ? "KliveAgent's durable state failed to load."
                : "KliveAgent is restoring durable state.";
            return false;
        }

        private async Task<AgentChatResponse> QueueIncomingApiMessageLegacyAsync(
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

        public async Task<AgentChatResponse> QueueIncomingApiMessageAsync(
            string message,
            string conversationId = null,
            string senderName = null,
            string clientMessageId = null)
        {
            if (!TryGetApiAvailability(out _, out var availabilityMessage))
                return new AgentChatResponse { Success = false, Response = availabilityMessage, ErrorMessage = availabilityMessage };

            if (!await GetBoolOmniSetting("KliveAgent_Enabled", defaultValue: true))
                return new AgentChatResponse
                {
                    Success = false,
                    Response = "KliveAgent is currently disabled.",
                    ErrorMessage = "KliveAgent is disabled via OmniSettings."
                };

            conversationId ??= Guid.NewGuid().ToString("N");
            if (!IsSafeIdentifier(conversationId))
                return InvalidIdentifierResponse("conversationId", conversationId);
            if (!string.IsNullOrWhiteSpace(clientMessageId) && !IsSafeIdentifier(clientMessageId))
                return InvalidIdentifierResponse("clientMessageId", clientMessageId);

            var admissionGate = conversationAdmissionGates.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));
            await admissionGate.WaitAsync();
            try
            {
                if (conversations.TryGetValue(conversationId, out var canonicalConversation))
                    conversationId = canonicalConversation.ConversationId;
                string idempotencyKey = ClientMessageKey(conversationId, clientMessageId);
                if (idempotencyKey != null && runByClientMessage.TryGetValue(idempotencyKey, out var knownRequestId)
                    && pendingApiResponses.TryGetValue(knownRequestId, out var known))
                {
                    var priorSteer = known.SteeringMessages.FirstOrDefault(x => x.ClientMessageId == clientMessageId);
                    string priorPayload = priorSteer?.Message ?? known.UserMessage;
                    if (!string.Equals(priorPayload?.Trim(), message.Trim(), StringComparison.Ordinal))
                        return new AgentChatResponse
                        {
                            Success = false,
                            ConversationId = conversationId,
                            ClientMessageId = clientMessageId,
                            PendingRequestId = knownRequestId,
                            Response = "clientMessageId was already used for a different message.",
                            ErrorMessage = "Idempotency keys cannot be reused with a different payload."
                        };
                    return PendingReceipt(known, wasSteering: priorSteer != null, acceptedMessageId: priorSteer?.MessageId);
                }
                if (clientMessageId != null
                    && conversations.TryGetValue(conversationId, out var existingConversation))
                {
                    lock (ConversationSync(conversationId))
                    {
                        var existingUser = existingConversation.Messages?.FirstOrDefault(
                            x => x.Role == AgentMessageRole.User && x.MessageId == clientMessageId);
                        if (existingUser != null)
                        {
                            if (!string.Equals(
                                existingUser.Content?.Trim(), message.Trim(), StringComparison.Ordinal))
                                return new AgentChatResponse
                                {
                                    Success = false,
                                    ConversationId = existingConversation.ConversationId,
                                    ClientMessageId = clientMessageId,
                                    PendingRequestId = existingUser.RequestId,
                                    Response = "clientMessageId was already used for a different message.",
                                    ErrorMessage = "Idempotency keys cannot be reused with a different payload."
                                };
                            var existingAgent = existingConversation.Messages.FirstOrDefault(
                                x => x.Role == AgentMessageRole.Agent
                                    && x.RequestId == existingUser.RequestId);
                            if (existingAgent != null)
                                return new AgentChatResponse
                                {
                                    Success = string.Equals(
                                        existingAgent.DeliveryStatus,
                                        "completed",
                                        StringComparison.OrdinalIgnoreCase),
                                    ConversationId = existingConversation.ConversationId,
                                    ClientMessageId = clientMessageId,
                                    PendingRequestId = existingUser.RequestId,
                                    AcceptedMessageId = existingUser.MessageId,
                                    Response = existingAgent.Content,
                                    ScriptsExecuted = existingAgent.ScriptResults
                                        ?? new List<AgentScriptResult>(),
                                    ErrorMessage = string.Equals(
                                        existingAgent.DeliveryStatus,
                                        "completed",
                                        StringComparison.OrdinalIgnoreCase)
                                            ? null
                                            : $"Run ended with status {existingAgent.DeliveryStatus}."
                                };
                            return new AgentChatResponse
                            {
                                Success = false,
                                ConversationId = existingConversation.ConversationId,
                                ClientMessageId = clientMessageId,
                                PendingRequestId = existingUser.RequestId,
                                AcceptedMessageId = existingUser.MessageId,
                                Response = "This message was already accepted, but its run receipt is unavailable.",
                                ErrorMessage = "Refusing to execute a duplicate idempotency key."
                            };
                        }
                    }
                }

                // Same-conversation input while work is active is ordered steering, not a second
                // concurrent brain that shares and resets the same provider session.
                if (activeRunByConversation.TryGetValue(conversationId, out var activeRequestId))
                {
                    var steered = await SteerPendingApiResponseCoreAsync(
                        activeRequestId, message, senderName, clientMessageId);
                    if (steered.Accepted && pendingApiResponses.TryGetValue(activeRequestId, out var active))
                        return PendingReceipt(active, wasSteering: true, acceptedMessageId: steered.MessageId);
                }

                var pending = new AgentPendingChatResponse
                {
                    ConversationId = conversationId,
                    ClientMessageId = clientMessageId,
                    UserMessage = message,
                    SenderName = senderName ?? "API",
                    Response = string.Empty,
                    CancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken.Token),
                    Control = new AgentChatRunControl()
                };

                pendingApiResponses[pending.RequestId] = pending;
                activeRunByConversation[conversationId] = pending.RequestId;
                if (idempotencyKey != null) runByClientMessage[idempotencyKey] = pending.RequestId;

                var conversation = conversations.GetOrAdd(conversationId, _ => new AgentConversation
                {
                    ConversationId = conversationId,
                    SourceChannel = AgentSourceChannel.API
                });
                string acceptedMessageId = null;
                try
                {
                    // Both the visible user turn and its run receipt are the acknowledgement
                    // boundary. Do not launch or return 202 until both writes have completed.
                    acceptedMessageId = await RecordAcceptedUserMessageAsync(
                        conversation, pending.RequestId, message, senderName,
                        messageId: clientMessageId);
                    await PersistRunAsync(pending);
                }
                catch (Exception ex)
                {
                    pending.Status = AgentTaskStatus.Failed;
                    pending.CompletedAt = DateTime.UtcNow;
                    pending.ErrorMessage = "Could not durably accept the run: " + ex.Message;
                    pending.FinalResponse = new AgentChatResponse
                    {
                        Success = false,
                        ConversationId = conversationId,
                        PendingRequestId = pending.RequestId,
                        Response = "I could not safely save this request, so I did not start it.",
                        ErrorMessage = pending.ErrorMessage
                    };
                    activeRunByConversation.TryRemove(conversationId, out _);
                    pendingApiResponses.TryRemove(pending.RequestId, out _);
                    if (idempotencyKey != null) runByClientMessage.TryRemove(idempotencyKey, out _);
                    try { pending.CancellationSource.Dispose(); } catch { }
                    await CompleteConversationTurnAsync(
                        conversation, pending.RequestId, pending.FinalResponse, "failed");
                    return pending.FinalResponse;
                }

                var runToken = pending.CancellationSource.Token;
                long runGeneration = Volatile.Read(ref serviceGeneration);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var response = await HandleIncomingMessage(
                            message,
                            AgentSourceChannel.API,
                            conversationId,
                            senderName,
                            onProgress: update =>
                            {
                                lock (pending)
                                {
                                    if (update.Text != null) pending.Response = update.Text;
                                    if (update.Scripts != null)
                                        pending.ScriptsExecuted = new List<AgentScriptResult>(update.Scripts);
                                    pending.Iteration = update.Iteration;
                                    pending.Phase = update.Phase;
                                    pending.StatusNote = update.StatusNote;
                                    pending.PromptTokens = update.PromptTokens;
                                    pending.CompletionTokens = update.CompletionTokens;
                                    if (update.NewActivity != null)
                                        pending.Activity = new List<AgentActivityEvent>(pending.Activity) { update.NewActivity };
                                    if (update.Frame != null)
                                        pending.LatestFrame = Convert.ToBase64String(update.Frame);
                                    if (update.Approval != null)
                                        pending.PendingApproval = update.Approval.Status == "pending" ? update.Approval : null;
                                    TouchRunLocked(pending);
                                }
                                ScheduleRunPersist(pending, 0, runGeneration);
                            },
                            cancellationToken: runToken,
                            requestId: pending.RequestId,
                            messageAlreadyRecorded: true,
                            runControl: pending.Control,
                            expectedServiceGeneration: runGeneration,
                            onSteeringApplied: steering =>
                            {
                                lock (pending)
                                {
                                    steering.Status = "applied";
                                    steering.AppliedAt = DateTime.UtcNow;
                                    TouchRunLocked(pending);
                                }
                                ScheduleRunPersist(pending, 0, runGeneration);
                            });

                        response.PendingRequestId = pending.RequestId;
                        response.ClientMessageId = pending.ClientMessageId;
                        lock (pending)
                        {
                            pending.FinalResponse = response;
                            pending.Phase = "final";
                            pending.Status = runToken.IsCancellationRequested
                                ? AgentTaskStatus.Cancelled
                                : response.Success ? AgentTaskStatus.Completed : AgentTaskStatus.Failed;
                            pending.ErrorMessage = response.Success ? null : response.ErrorMessage;
                            TouchRunLocked(pending);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        lock (pending)
                        {
                            pending.Status = AgentTaskStatus.Cancelled;
                            pending.ErrorMessage = "Run was stopped.";
                            pending.FinalResponse = new AgentChatResponse
                            {
                                Success = false,
                                ConversationId = conversationId,
                                PendingRequestId = pending.RequestId,
                                Response = string.IsNullOrWhiteSpace(pending.Response)
                                    ? "_(Run stopped before completion.)_"
                                    : pending.Response,
                                ErrorMessage = "Run was stopped."
                            };
                            TouchRunLocked(pending);
                        }
                        if (runGeneration == Volatile.Read(ref serviceGeneration))
                            await CompleteConversationTurnAsync(
                                conversation, pending.RequestId, pending.FinalResponse, "cancelled");
                    }
                    catch (Exception ex)
                    {
                        lock (pending)
                        {
                            pending.Status = AgentTaskStatus.Failed;
                            pending.ErrorMessage = ex.Message;
                            pending.FinalResponse = new AgentChatResponse
                            {
                                Success = false,
                                ConversationId = conversationId,
                                PendingRequestId = pending.RequestId,
                                Response = "KliveAgent failed while completing the request.",
                                ErrorMessage = ex.ToString()
                            };
                            TouchRunLocked(pending);
                        }
                        if (runGeneration == Volatile.Read(ref serviceGeneration))
                            await CompleteConversationTurnAsync(
                                conversation, pending.RequestId, pending.FinalResponse, "failed");
                    }
                    finally
                    {
                        pending.Control?.Seal();
                        lock (pending)
                        {
                            foreach (var steering in pending.SteeringMessages.Where(x => x.Status == "queued"))
                                steering.Status = "rejected";
                            pending.CompletedAt ??= DateTime.UtcNow;
                            pending.LastProgressAt = DateTime.UtcNow;
                            TouchRunLocked(pending);
                        }
                        ((ICollection<KeyValuePair<string, string>>)activeRunByConversation)
                            .Remove(new KeyValuePair<string, string>(
                                conversationId, pending.RequestId));
                        if (runGeneration == Volatile.Read(ref serviceGeneration))
                        {
                            try
                            {
                                await PersistRunAsync(pending);
                            }
                            catch (Exception ex)
                            {
                                ScheduleRunPersist(pending);
                                try
                                {
                                    await ServiceLogError(
                                        ex,
                                        $"[KliveAgent] Failed to persist terminal state for run {pending.RequestId}.",
                                        false);
                                }
                                catch { }
                            }
                            try
                            {
                                string notificationBody = pending.FinalResponse?.Response
                                    ?? pending.ErrorMessage
                                    ?? "Run ended without a response.";
                                if (notificationBody.Length > 4000)
                                    notificationBody = notificationBody.Substring(0, 4000) + "...";
                                await AddNotificationAsync(new AgentNotification
                                {
                                    NotificationId = "run" + pending.RequestId,
                                    Kind = "chat-completed",
                                    Title = pending.Status == AgentTaskStatus.Completed
                                        ? "KliveAgent finished a conversation"
                                        : $"KliveAgent run ended: {pending.Status}",
                                    Body = notificationBody,
                                    ConversationId = pending.ConversationId,
                                    RequestId = pending.RequestId
                                });
                            }
                            catch (Exception ex)
                            {
                                try { await ServiceLogError(ex, "[KliveAgent] Failed to persist completion notification.", false); } catch { }
                            }
                        }
                        try { pending.CancellationSource?.Dispose(); } catch { }
                    }
                });

                return PendingReceipt(pending, acceptedMessageId: acceptedMessageId);
            }
            finally
            {
                admissionGate.Release();
            }
        }

        public AgentPendingChatResponse GetPendingApiResponse(string requestId)
        {
            pendingApiResponses.TryGetValue(requestId, out var pendingResponse);
            return pendingResponse == null ? null : SnapshotRun(pendingResponse);
        }

        public List<AgentPendingChatResponse> GetPendingApiResponses(
            string conversationId = null, bool includeCompleted = true)
        {
            return pendingApiResponses.Values
                .Where(r => (string.IsNullOrEmpty(conversationId)
                        || string.Equals(r.ConversationId, conversationId, StringComparison.OrdinalIgnoreCase))
                    && (includeCompleted || r.CompletedAt == null))
                .OrderByDescending(r => r.CreatedAt)
                .Select(SnapshotRun)
                .ToList();
        }

        public AgentPendingChatResponse GetLatestConversationRun(string conversationId)
        {
            if (string.IsNullOrWhiteSpace(conversationId)) return null;
            return GetPendingApiResponses(conversationId).FirstOrDefault();
        }

        public async Task<AgentSteeringResult> SteerPendingApiResponseAsync(
            string requestId,
            string message,
            string senderName = null,
            string clientMessageId = null)
        {
            if (!pendingApiResponses.TryGetValue(requestId ?? string.Empty, out var pending))
                return await SteerPendingApiResponseCoreAsync(
                    requestId, message, senderName, clientMessageId);

            var gate = conversationAdmissionGates.GetOrAdd(
                pending.ConversationId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                return await SteerPendingApiResponseCoreAsync(
                    requestId, message, senderName, clientMessageId);
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task<AgentSteeringResult> SteerPendingApiResponseCoreAsync(
            string requestId,
            string message,
            string senderName,
            string clientMessageId)
        {
            if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(message))
                return new AgentSteeringResult
                {
                    Accepted = false,
                    Reason = "requestId and message are required."
                };
            if (!IsSafeIdentifier(requestId)
                || (!string.IsNullOrWhiteSpace(clientMessageId) && !IsSafeIdentifier(clientMessageId)))
                return new AgentSteeringResult
                {
                    Accepted = false,
                    RequestId = requestId,
                    Reason = "Invalid requestId or clientMessageId."
                };
            if (!pendingApiResponses.TryGetValue(requestId, out var pending) || pending.CompletedAt != null)
                return new AgentSteeringResult
                {
                    Accepted = false,
                    RequestId = requestId,
                    Reason = "Run is not active."
                };

            string key = ClientMessageKey(pending.ConversationId, clientMessageId);
            if (key != null && runByClientMessage.TryGetValue(key, out var known))
            {
                var existing = pending.SteeringMessages.FirstOrDefault(x => x.ClientMessageId == clientMessageId);
                bool sameSteering = known == requestId
                    && existing != null
                    && string.Equals(existing.Message, message.Trim(), StringComparison.Ordinal);
                return new AgentSteeringResult
                {
                    Accepted = sameSteering,
                    RequestId = known,
                    ConversationId = pending.ConversationId,
                    MessageId = existing?.MessageId,
                    Reason = sameSteering
                        ? "Already accepted."
                        : existing == null
                            ? "clientMessageId belongs to the original turn or another run."
                            : "clientMessageId was already used with different guidance."
                };
            }

            var steering = new AgentSteeringMessage
            {
                MessageId = clientMessageId ?? Guid.NewGuid().ToString("N"),
                Message = message.Trim(),
                SenderName = senderName ?? "API",
                ClientMessageId = clientMessageId
            };
            if (pending.Control == null || !pending.Control.TryReserve(steering))
                return new AgentSteeringResult
                {
                    Accepted = false,
                    RequestId = requestId,
                    ConversationId = pending.ConversationId,
                    Reason = "Run is finalizing; send this as the next turn."
                };

            AgentConversation conversation = null;
            try
            {
                lock (pending)
                {
                    pending.SteeringMessages = new List<AgentSteeringMessage>(pending.SteeringMessages)
                        { steering };
                    TouchRunLocked(pending);
                }

                conversations.TryGetValue(pending.ConversationId, out conversation);
                if (conversation != null)
                    await RecordAcceptedUserMessageAsync(
                        conversation, requestId, steering.Message, steering.SenderName, steering.MessageId);
                await PersistRunAsync(pending);

                if (!pending.Control.Commit(steering))
                {
                    await RejectPersistedSteeringAsync(pending, conversation, steering, "cancelled");
                    return new AgentSteeringResult
                    {
                        Accepted = false,
                        RequestId = requestId,
                        ConversationId = pending.ConversationId,
                        MessageId = steering.MessageId,
                        Reason = "Run stopped while the guidance was being saved."
                    };
                }
                if (key != null) runByClientMessage[key] = requestId;
            }
            catch (Exception ex)
            {
                pending.Control?.Reject(steering);
                await RejectPersistedSteeringAsync(pending, conversation, steering, "failed");
                return new AgentSteeringResult
                {
                    Accepted = false,
                    RequestId = requestId,
                    ConversationId = pending.ConversationId,
                    MessageId = steering.MessageId,
                    Reason = "Guidance was not accepted because it could not be saved: " + ex.Message
                };
            }

            return new AgentSteeringResult
            {
                Accepted = true,
                RequestId = requestId,
                ConversationId = pending.ConversationId,
                MessageId = steering.MessageId
            };
        }

        private async Task RejectPersistedSteeringAsync(
            AgentPendingChatResponse pending,
            AgentConversation conversation,
            AgentSteeringMessage steering,
            string deliveryStatus)
        {
            lock (pending)
            {
                steering.Status = "rejected";
                TouchRunLocked(pending);
            }
            if (conversation != null)
            {
                lock (ConversationSync(conversation.ConversationId))
                {
                    var message = conversation.Messages?.FirstOrDefault(
                        x => x.MessageId == steering.MessageId);
                    if (message != null) message.DeliveryStatus = deliveryStatus;
                    conversation.LastUpdated = DateTime.UtcNow;
                }
                await PersistConversationAsync(conversation);
            }
            try { await PersistRunAsync(pending); } catch { }
        }

        /// <summary>Manually stop a running message (the website's Stop button). Cancels the per-run
        /// token, which unwinds the LLM call, the agent loop, and any running script; the run then
        /// resolves to a truthful partial answer. Returns false if no such running request exists.</summary>
        public bool CancelPendingApiResponse(string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId)) return false;
            if (!pendingApiResponses.TryGetValue(requestId, out var pending)) return false;
            if (pending.CompletedAt != null) return false;
            pending.Control?.Seal();
            try { pending.CancellationSource?.Cancel(); } catch { }
            lock (pending)
            {
                pending.StatusNote = "Stop requested...";
                TouchRunLocked(pending);
            }
            ScheduleRunPersist(pending);
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
        private static readonly TimeSpan PendingRetentionWindow = TimeSpan.FromDays(7);

        private async Task RunPendingRunWatchdogAsync(CancellationToken serviceToken)
        {
            while (!serviceToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), serviceToken);

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
                            if (pendingApiResponses.TryRemove(kvp.Key, out var removed))
                            {
                                RemoveClientMessageIndex(
                                    ClientMessageKey(removed.ConversationId, removed.ClientMessageId),
                                    removed.RequestId);
                                foreach (var steering in removed.SteeringMessages)
                                    RemoveClientMessageIndex(
                                        ClientMessageKey(removed.ConversationId, steering.ClientMessageId),
                                        removed.RequestId);
                                runPersistGates.TryRemove(removed.RequestId, out _);
                                string runPath = Path.Combine(
                                    OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentChatRunsDirectory),
                                    removed.RequestId + ".json");
                                try { await GetDataHandler().DeleteFile(runPath); }
                                catch (Exception ex)
                                {
                                    try
                                    {
                                        await ServiceLogError(
                                            ex,
                                            $"[KliveAgent] Could not remove expired run {removed.RequestId}.",
                                            false);
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (serviceToken.IsCancellationRequested)
                {
                    break;
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

        public static bool IsSafeIdentifier(string value) =>
            !string.IsNullOrWhiteSpace(value) && SafeId.IsMatch(value);

        private static AgentChatResponse InvalidIdentifierResponse(string field, string value) => new()
        {
            Success = false,
            Response = $"{field} is invalid.",
            ErrorMessage = $"{field} must be 1-128 ASCII letters, numbers, underscores, or hyphens."
        };

        private static string ClientMessageKey(string conversationId, string clientMessageId) =>
            string.IsNullOrWhiteSpace(clientMessageId) ? null : conversationId + ":" + clientMessageId;

        private void RemoveClientMessageIndex(string key, string requestId)
        {
            if (key == null) return;
            ((ICollection<KeyValuePair<string, string>>)runByClientMessage)
                .Remove(new KeyValuePair<string, string>(key, requestId));
        }

        private static AgentChatResponse PendingReceipt(
            AgentPendingChatResponse pending,
            bool wasSteering = false,
            string acceptedMessageId = null)
        {
            if (pending.CompletedAt != null && pending.FinalResponse != null)
            {
                var final = pending.FinalResponse;
                return new AgentChatResponse
                {
                    Success = final.Success,
                    ConversationId = pending.ConversationId,
                    ClientMessageId = pending.ClientMessageId,
                    Response = final.Response,
                    ScriptsExecuted = final.ScriptsExecuted == null
                        ? new List<AgentScriptResult>()
                        : new List<AgentScriptResult>(final.ScriptsExecuted),
                    ErrorMessage = final.ErrorMessage,
                    PromptTokens = final.PromptTokens,
                    CompletionTokens = final.CompletionTokens,
                    Iterations = final.Iterations,
                    IsPending = false,
                    PendingRequestId = pending.RequestId,
                    WasSteering = wasSteering,
                    AcceptedMessageId = acceptedMessageId
                };
            }

            return new AgentChatResponse
            {
                Success = true,
                ConversationId = pending.ConversationId,
                ClientMessageId = pending.ClientMessageId,
                Response = pending.Response ?? string.Empty,
                IsPending = true,
                PendingRequestId = pending.RequestId,
                WasSteering = wasSteering,
                AcceptedMessageId = acceptedMessageId
            };
        }

        private object ConversationSync(string conversationId) =>
            conversations.TryGetValue(conversationId, out var conversation)
                ? conversation.SyncRoot
                : conversationSyncs.GetOrAdd(conversationId, _ => new object());

        private async Task<string> RecordAcceptedUserMessageAsync(
            AgentConversation conversation,
            string requestId,
            string message,
            string senderName,
            string messageId = null)
        {
            messageId ??= Guid.NewGuid().ToString("N");
            lock (ConversationSync(conversation.ConversationId))
            {
                conversation.Messages ??= new List<AgentMessage>();
                if (!conversation.Messages.Any(x => x.MessageId == messageId))
                {
                    conversation.Messages.Add(new AgentMessage
                    {
                        MessageId = messageId,
                        RequestId = requestId,
                        Role = AgentMessageRole.User,
                        Content = message,
                        SenderName = senderName,
                        Timestamp = DateTime.UtcNow,
                        DeliveryStatus = "running"
                    });
                    conversation.LastUpdated = DateTime.UtcNow;
                }
            }
            if (!await PersistConversationAsync(conversation))
                throw new IOException("The accepted chat message could not be persisted.");
            return messageId;
        }

        private async Task CompleteConversationTurnAsync(
            AgentConversation conversation,
            string requestId,
            AgentChatResponse response,
            string deliveryStatus)
        {
            lock (ConversationSync(conversation.ConversationId))
            {
                conversation.Messages ??= new List<AgentMessage>();
                foreach (var userMessage in conversation.Messages.Where(x =>
                    x.Role == AgentMessageRole.User && x.RequestId == requestId
                    && !string.Equals(x.DeliveryStatus, "rejected", StringComparison.OrdinalIgnoreCase)))
                    userMessage.DeliveryStatus = deliveryStatus;

                var agentMessage = conversation.Messages.FirstOrDefault(x =>
                    x.Role == AgentMessageRole.Agent && x.RequestId == requestId);
                if (agentMessage == null)
                {
                    conversation.Messages.Add(new AgentMessage
                    {
                        RequestId = requestId,
                        Role = AgentMessageRole.Agent,
                        Content = response?.Response ?? "KliveAgent ended without a response.",
                        ScriptResults = response?.ScriptsExecuted?.Count > 0
                            ? new List<AgentScriptResult>(response.ScriptsExecuted)
                            : null,
                        Timestamp = DateTime.UtcNow,
                        DeliveryStatus = deliveryStatus
                    });
                }
                else
                {
                    // The durable run is authoritative during restart reconciliation.
                    agentMessage.Content = response?.Response
                        ?? agentMessage.Content
                        ?? "KliveAgent ended without a response.";
                    agentMessage.ScriptResults = response?.ScriptsExecuted?.Count > 0
                        ? new List<AgentScriptResult>(response.ScriptsExecuted)
                        : agentMessage.ScriptResults;
                    agentMessage.DeliveryStatus = deliveryStatus;
                }
                conversation.LastUpdated = DateTime.UtcNow;
            }
            await PersistConversationAsync(conversation);
        }

        private static void TouchRunLocked(AgentPendingChatResponse pending)
        {
            pending.LastProgressAt = DateTime.UtcNow;
            pending.UpdatedAt = DateTime.UtcNow;
            pending.Sequence++;
        }

        private static AgentPendingChatResponse SnapshotRun(AgentPendingChatResponse source) =>
            SnapshotRun(source, includeFrame: true);

        private static AgentPendingChatResponse SnapshotRun(
            AgentPendingChatResponse source, bool includeFrame)
        {
            lock (source)
            {
                return new AgentPendingChatResponse
                {
                    RequestId = source.RequestId,
                    ConversationId = source.ConversationId,
                    ClientMessageId = source.ClientMessageId,
                    Status = source.Status,
                    UserMessage = source.UserMessage,
                    SenderName = source.SenderName,
                    Response = source.Response,
                    ScriptsExecuted = source.ScriptsExecuted == null
                        ? new List<AgentScriptResult>()
                        : new List<AgentScriptResult>(source.ScriptsExecuted),
                    Iteration = source.Iteration,
                    Phase = source.Phase,
                    StatusNote = source.StatusNote,
                    PromptTokens = source.PromptTokens,
                    CompletionTokens = source.CompletionTokens,
                    Activity = source.Activity == null
                        ? new List<AgentActivityEvent>()
                        : new List<AgentActivityEvent>(source.Activity),
                    SteeringMessages = source.SteeringMessages == null
                        ? new List<AgentSteeringMessage>()
                        : source.SteeringMessages.Select(CloneSteeringMessage).ToList(),
                    LatestFrame = includeFrame ? source.LatestFrame : null,
                    PendingApproval = source.PendingApproval,
                    FinalResponse = source.FinalResponse,
                    CreatedAt = source.CreatedAt,
                    CompletedAt = source.CompletedAt,
                    UpdatedAt = source.UpdatedAt,
                    Sequence = source.Sequence,
                    ErrorMessage = source.ErrorMessage,
                    LastProgressAt = source.LastProgressAt
                };
            }
        }

        private static AgentSteeringMessage CloneSteeringMessage(AgentSteeringMessage source) => new()
        {
            MessageId = source.MessageId,
            Message = source.Message,
            SenderName = source.SenderName,
            ClientMessageId = source.ClientMessageId,
            CreatedAt = source.CreatedAt,
            AppliedAt = source.AppliedAt,
            Status = source.Status
        };

        private void ScheduleRunPersist(
            AgentPendingChatResponse pending,
            int retryAttempt = 0,
            long? originatingGeneration = null)
        {
            if (!scheduledRunPersists.TryAdd(pending.RequestId, 0)) return;
            long generation = originatingGeneration ?? Volatile.Read(ref serviceGeneration);
            _ = Task.Run(async () =>
            {
                long persistedSequence = 0;
                bool persisted = false;
                try
                {
                    await Task.Delay(750);
                    if (generation != Volatile.Read(ref serviceGeneration)) return;
                    lock (pending) persistedSequence = pending.Sequence;
                    await PersistRunAsync(pending);
                    persisted = true;
                }
                catch (Exception ex)
                {
                    try { await ServiceLogError(ex, "[KliveAgent] Failed to persist live run progress.", false); } catch { }
                }
                finally
                {
                    scheduledRunPersists.TryRemove(pending.RequestId, out _);
                    if (generation == Volatile.Read(ref serviceGeneration))
                    {
                        if (persisted && pending.Sequence > persistedSequence)
                            ScheduleRunPersist(pending, 0, generation);
                        else if (!persisted && retryAttempt < 5)
                            ScheduleRunPersist(pending, retryAttempt + 1, generation);
                    }
                }
            });
        }

        private async Task PersistRunAsync(AgentPendingChatResponse pending)
        {
            var gate = runPersistGates.GetOrAdd(
                pending.RequestId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                // Snapshot only after entering the write gate. A delayed older caller therefore
                // serializes current state instead of overwriting a newer snapshot.
                var snapshot = SnapshotRun(pending, includeFrame: false);
                var path = Path.Combine(
                    OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentChatRunsDirectory),
                    snapshot.RequestId + ".json");
                await GetDataHandler().SerialiseObjectToFile(path, snapshot);
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task LoadChatRunsAsync()
        {
            var dir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentChatRunsDirectory);
            if (!Directory.Exists(dir)) return;

            int loaded = 0;
            int interrupted = 0;
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var run = await GetDataHandler()
                        .ReadAndDeserialiseDataFromFile<AgentPendingChatResponse>(file);
                    if (run == null || !IsSafeIdentifier(run.RequestId)
                        || !IsSafeIdentifier(run.ConversationId))
                        continue;

                    run.Activity ??= new List<AgentActivityEvent>();
                    run.ScriptsExecuted ??= new List<AgentScriptResult>();
                    run.SteeringMessages ??= new List<AgentSteeringMessage>();
                    run.LastProgressAt = run.UpdatedAt == default ? run.CreatedAt : run.UpdatedAt;

                    bool wasRunning = run.CompletedAt == null || run.Status == AgentTaskStatus.Running;
                    AgentMessage recoveredAgentMessage = null;
                    if (wasRunning && conversations.TryGetValue(run.ConversationId, out var recoveredConversation))
                    {
                        lock (ConversationSync(run.ConversationId))
                        {
                            recoveredAgentMessage = recoveredConversation.Messages?.FirstOrDefault(x =>
                                x.Role == AgentMessageRole.Agent && x.RequestId == run.RequestId
                                && !string.Equals(
                                    x.DeliveryStatus, "running", StringComparison.OrdinalIgnoreCase));
                        }
                    }
                    if (wasRunning && recoveredAgentMessage != null)
                    {
                        // The conversation commit won the crash race but the terminal run snapshot
                        // did not. Recover it instead of falsely reporting completed work as interrupted.
                        run.Status = recoveredAgentMessage.DeliveryStatus?.ToLowerInvariant() switch
                        {
                            "cancelled" => AgentTaskStatus.Cancelled,
                            "failed" => AgentTaskStatus.Failed,
                            "interrupted" => AgentTaskStatus.Interrupted,
                            _ => AgentTaskStatus.Completed
                        };
                        run.Phase = "final";
                        run.Response = recoveredAgentMessage.Content;
                        run.ScriptsExecuted = recoveredAgentMessage.ScriptResults
                            ?? new List<AgentScriptResult>();
                        run.CompletedAt = recoveredAgentMessage.Timestamp;
                        run.UpdatedAt = DateTime.UtcNow;
                        run.Sequence++;
                        run.FinalResponse = new AgentChatResponse
                        {
                            Success = run.Status == AgentTaskStatus.Completed,
                            ConversationId = run.ConversationId,
                            PendingRequestId = run.RequestId,
                            Response = recoveredAgentMessage.Content,
                            ScriptsExecuted = new List<AgentScriptResult>(run.ScriptsExecuted),
                            ErrorMessage = run.Status == AgentTaskStatus.Completed ? null : run.ErrorMessage
                        };
                        await PersistRunAsync(run);
                        wasRunning = false;
                    }
                    if (wasRunning)
                    {
                        run.Status = AgentTaskStatus.Interrupted;
                        run.Phase = "final";
                        run.StatusNote = "Interrupted by an Omnipotent restart.";
                        run.ErrorMessage = "The process restarted before this run completed; it was not replayed because prior side effects may already have happened.";
                        run.CompletedAt = DateTime.UtcNow;
                        run.UpdatedAt = DateTime.UtcNow;
                        run.Sequence++;
                        foreach (var steering in run.SteeringMessages.Where(x => x.Status == "queued"))
                            steering.Status = "rejected";
                        run.FinalResponse = new AgentChatResponse
                        {
                            Success = false,
                            ConversationId = run.ConversationId,
                            PendingRequestId = run.RequestId,
                            Response = string.IsNullOrWhiteSpace(run.Response)
                                ? "_(This run was interrupted by an Omnipotent restart.)_"
                                : run.Response + "\n\n_(Interrupted by an Omnipotent restart.)_",
                            ErrorMessage = run.ErrorMessage
                        };
                        await PersistRunAsync(run);
                        await AddNotificationAsync(new AgentNotification
                        {
                            NotificationId = "run" + run.RequestId,
                            Kind = "chat-interrupted",
                            Title = "KliveAgent run was interrupted by a restart",
                            Body = run.FinalResponse.Response,
                            ConversationId = run.ConversationId,
                            RequestId = run.RequestId
                        });
                        interrupted++;
                    }

                    run.FinalResponse ??= new AgentChatResponse
                    {
                        Success = run.Status == AgentTaskStatus.Completed,
                        ConversationId = run.ConversationId,
                        PendingRequestId = run.RequestId,
                        Response = string.IsNullOrWhiteSpace(run.Response)
                            ? run.ErrorMessage ?? "KliveAgent ended without a response."
                            : run.Response,
                        ErrorMessage = run.ErrorMessage
                    };
                    await ReconcileConversationFromRunAsync(run);

                    pendingApiResponses[run.RequestId] = run;
                    string originalKey = ClientMessageKey(run.ConversationId, run.ClientMessageId);
                    if (originalKey != null) runByClientMessage[originalKey] = run.RequestId;
                    foreach (var steering in run.SteeringMessages)
                    {
                        string steeringKey = ClientMessageKey(run.ConversationId, steering.ClientMessageId);
                        if (steeringKey != null) runByClientMessage[steeringKey] = run.RequestId;
                    }
                    loaded++;
                }
                catch (Exception ex)
                {
                    await ServiceLogError(ex,
                        $"[KliveAgent] Skipped unreadable chat run {Path.GetFileName(file)}.", false);
                }
            }

            if (loaded > 0)
                await ServiceLog($"[KliveAgent] Loaded {loaded} durable chat run(s); {interrupted} interrupted by the previous shutdown.");
        }

        private async Task ReconcileConversationFromRunAsync(AgentPendingChatResponse run)
        {
            string deliveryStatus = run.Status switch
            {
                AgentTaskStatus.Completed => "completed",
                AgentTaskStatus.Cancelled => "cancelled",
                AgentTaskStatus.Interrupted => "interrupted",
                AgentTaskStatus.Failed => "failed",
                _ => "running"
            };
            var conversation = conversations.GetOrAdd(run.ConversationId, _ => new AgentConversation
            {
                ConversationId = run.ConversationId,
                SourceChannel = AgentSourceChannel.API,
                LastUpdated = run.CreatedAt
            });
            lock (ConversationSync(run.ConversationId))
            {
                conversation.Messages ??= new List<AgentMessage>();
                var steeringIds = run.SteeringMessages
                    .Select(x => x.MessageId)
                    .ToHashSet(StringComparer.Ordinal);
                if (!conversation.Messages.Any(x =>
                    x.Role == AgentMessageRole.User && x.RequestId == run.RequestId
                    && !steeringIds.Contains(x.MessageId)))
                {
                    conversation.Messages.Add(new AgentMessage
                    {
                        MessageId = IsSafeIdentifier(run.ClientMessageId)
                            ? run.ClientMessageId
                            : Guid.NewGuid().ToString("N"),
                        RequestId = run.RequestId,
                        Role = AgentMessageRole.User,
                        Content = run.UserMessage ?? string.Empty,
                        SenderName = run.SenderName ?? "API",
                        Timestamp = run.CreatedAt,
                        DeliveryStatus = deliveryStatus
                    });
                }
                foreach (var steering in run.SteeringMessages.OrderBy(x => x.CreatedAt))
                {
                    if (conversation.Messages.Any(x => x.MessageId == steering.MessageId)) continue;
                    conversation.Messages.Add(new AgentMessage
                    {
                        MessageId = steering.MessageId,
                        RequestId = run.RequestId,
                        Role = AgentMessageRole.User,
                        Content = steering.Message,
                        SenderName = steering.SenderName,
                        Timestamp = steering.CreatedAt,
                        DeliveryStatus = steering.Status == "rejected"
                            ? "rejected"
                            : deliveryStatus
                    });
                }
                conversation.Messages = conversation.Messages
                    .OrderBy(x => x.Timestamp)
                    .ToList();
                if (conversation.LastUpdated < run.UpdatedAt)
                    conversation.LastUpdated = run.UpdatedAt;
            }
            await CompleteConversationTurnAsync(
                conversation, run.RequestId, run.FinalResponse, deliveryStatus);
        }

        private Omnipotent.Services.Projects.Projects GetProjectsService() =>
            GetActiveServices()
                .OfType<Omnipotent.Services.Projects.Projects>()
                .FirstOrDefault(x => x.IsServiceActive() && x.Store != null);

        public async Task<AgentLongTermJobView> CreateLongTermJobAsync(
            AgentLongTermJobRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Goal))
                throw new ArgumentException("A long-term job requires a goal.");
            if (request.Goal.Length > 200_000)
                throw new ArgumentException("Goal is too large.");
            if (!string.IsNullOrWhiteSpace(request.ConversationId)
                && !IsSafeIdentifier(request.ConversationId))
                throw new ArgumentException("Invalid conversationId.");
            if (!string.IsNullOrWhiteSpace(request.ClientJobId)
                && !IsSafeIdentifier(request.ClientJobId))
                throw new ArgumentException("Invalid clientJobId.");
            if (!double.IsFinite(request.TokenBudgetUsd) || request.TokenBudgetUsd <= 0
                || !double.IsFinite(request.MoneyBudgetUsd) || request.MoneyBudgetUsd < 0
                || !double.IsFinite(request.MoneyAutonomousThresholdUsd)
                || request.MoneyAutonomousThresholdUsd < 0)
                throw new ArgumentException("Budgets must be finite; token budget must be positive and money budgets non-negative.");

            string cleanGoal = request.Goal.Trim();
            string name = string.IsNullOrWhiteSpace(request.Name)
                ? (cleanGoal.Length <= 72 ? cleanGoal : cleanGoal.Substring(0, 72)).Trim()
                : request.Name.Trim();
            int agentCap = Math.Clamp(request.SubAgentCap, 1, 32);

            await longTermJobCreationGate.WaitAsync();
            try
            {
                if (!string.IsNullOrWhiteSpace(request.ClientJobId)
                    && jobByClientId.TryGetValue(request.ClientJobId, out var existingJobId)
                    && longTermJobs.TryGetValue(existingJobId, out var existingLink))
                {
                    var existing = BuildLongTermJobView(existingLink);
                    var existingProject = GetProjectsService()?.Store.GetProject(existingLink.ProjectId);
                    if (!string.Equals(existing.Goal, cleanGoal, StringComparison.Ordinal)
                        || !string.Equals(existing.Name, name, StringComparison.Ordinal)
                        || existingProject == null
                        || existingProject.TokenBudgetUsd != request.TokenBudgetUsd
                        || existingProject.MoneyBudgetUsd != request.MoneyBudgetUsd
                        || existingProject.MoneyAutonomousThresholdUsd
                            != request.MoneyAutonomousThresholdUsd
                        || existingProject.SubAgentCap != agentCap)
                        throw new ArgumentException(
                            "clientJobId is already associated with different job parameters.");
                    return existing;
                }

                var projects = GetProjectsService()
                    ?? throw new InvalidOperationException("The Projects service is not ready.");
                Omnipotent.Services.Projects.Project project = null;
                try
                {
                    project = await projects.CreateProjectAsync(
                        name,
                        cleanGoal,
                        request.TokenBudgetUsd,
                        request.MoneyBudgetUsd,
                        request.MoneyAutonomousThresholdUsd,
                        agentCap);
                    var receipt = projects.MessageProjectWithReceipt(
                        project.ProjectID,
                        cleanGoal,
                        Omnipotent.Services.Projects.ProjectDirectiveKind.Task,
                        expectedArtifactPaths: request.ExpectedArtifactPaths ?? new List<string>());
                    if (!receipt.Accepted)
                        throw new InvalidOperationException(
                            receipt.Reason ?? "The Project rejected its root job directive.");

                    var link = new AgentLongTermJobLink
                    {
                        ClientJobId = request.ClientJobId,
                        ProjectId = project.ProjectID,
                        DirectiveId = receipt.DirectiveID,
                        ConversationId = request.ConversationId,
                        OriginatingMessageId = request.OriginatingMessageId,
                        LastObservedStatus = project.Status.ToString()
                    };
                    // Persist before publishing the link or returning. A lost HTTP response can be
                    // retried with clientJobId and resolves to this same funded Project.
                    await PersistLongTermJobAsync(link);
                    longTermJobs[link.JobId] = link;
                    if (!string.IsNullOrWhiteSpace(link.ClientJobId))
                        jobByClientId[link.ClientJobId] = link.JobId;
                    return BuildLongTermJobView(link);
                }
                catch
                {
                    // Project creation wakes a Commander immediately. If linking fails, halt it so
                    // an invisible orphan cannot continue spending in the background.
                    if (project != null)
                    {
                        try { projects.HaltProject(project.ProjectID); } catch { }
                    }
                    throw;
                }
            }
            finally
            {
                longTermJobCreationGate.Release();
            }
        }

        public List<AgentLongTermJobView> GetLongTermJobs() =>
            longTermJobs.Values
                .OrderByDescending(x => x.CreatedAt)
                .Select(BuildLongTermJobView)
                .ToList();

        public AgentLongTermJobView GetLongTermJob(string jobId) =>
            !string.IsNullOrWhiteSpace(jobId) && longTermJobs.TryGetValue(jobId, out var link)
                ? BuildLongTermJobView(link)
                : null;

        public Omnipotent.Services.Projects.ProjectCommandReceipt SteerLongTermJob(
            string jobId, string message)
        {
            if (!longTermJobs.TryGetValue(jobId ?? string.Empty, out var link))
                return new Omnipotent.Services.Projects.ProjectCommandReceipt
                    { Status = "rejected", Reason = "Unknown jobId." };
            var projects = GetProjectsService();
            if (projects == null)
                return new Omnipotent.Services.Projects.ProjectCommandReceipt
                    { Status = "rejected", Reason = "The Projects service is unavailable." };
            return projects.MessageProjectWithReceipt(
                link.ProjectId,
                message,
                Omnipotent.Services.Projects.ProjectDirectiveKind.Steering);
        }

        public bool StopLongTermJob(string jobId)
        {
            if (!longTermJobs.TryGetValue(jobId ?? string.Empty, out var link)) return false;
            var projects = GetProjectsService();
            var project = projects?.Store.GetProject(link.ProjectId);
            if (project == null
                || project.Status is Omnipotent.Services.Projects.ProjectStatus.Completed
                    or Omnipotent.Services.Projects.ProjectStatus.Archived)
                return false;
            if (project.HaltedFromStatus.HasValue) return true;
            return projects.HaltProject(link.ProjectId);
        }

        public async Task<bool> ResumeLongTermJobAsync(string jobId)
        {
            if (!longTermJobs.TryGetValue(jobId ?? string.Empty, out var link)) return false;
            var projects = GetProjectsService();
            var project = projects?.Store.GetProject(link.ProjectId);
            if (project == null
                || project.Status is Omnipotent.Services.Projects.ProjectStatus.Completed
                    or Omnipotent.Services.Projects.ProjectStatus.Archived)
                return false;
            if (project.HaltedFromStatus.HasValue)
                return projects.UnhaltProject(link.ProjectId);
            if (project.Status is Omnipotent.Services.Projects.ProjectStatus.Active
                or Omnipotent.Services.Projects.ProjectStatus.Planning)
                return true;
            if (project.Status == Omnipotent.Services.Projects.ProjectStatus.BudgetPaused
                && !projects.Budget.IsWithinTokenBudget(project.ProjectID))
                return false;

            bool needsPlan = !projects.GrandPlans.HasApprovedPlan(project.ProjectID);
            bool wasBlocked = project.BlockedAt.HasValue
                || !string.IsNullOrWhiteSpace(project.BlockedReason);
            var fromStatus = project.Status;
            project.Status = needsPlan
                ? Omnipotent.Services.Projects.ProjectStatus.Planning
                : Omnipotent.Services.Projects.ProjectStatus.Active;
            project.BlockedAt = null;
            project.BlockedReason = null;
            project.HaltedFromStatus = null;
            projects.Store.SaveProject(project);
            projects.RuntimeState.SetDisposition(
                project.ProjectID,
                Omnipotent.Services.Projects.ProjectExecutionDisposition.Running);
            projects.RuntimeState.ClearBlocker(project.ProjectID);
            projects.RuntimeState.CloseCircuit(project.ProjectID);
            await projects.RefreshDesktopRegistryAsync();
            projects.EventLog.Append(new Omnipotent.Services.Projects.ProjectEvent
            {
                ProjectID = project.ProjectID,
                Type = wasBlocked
                    ? Omnipotent.Services.Projects.ProjectEventTypes.ProjectUnblocked
                    : Omnipotent.Services.Projects.ProjectEventTypes.Status,
                Author = "klives",
                Text = "Project resumed from the KliveAgent job dashboard.",
                PayloadJson = Omnipotent.Services.Projects.ProjectLifecycleEvents.Payload(
                    fromStatus, project.Status, wasBlocked ? "job-dashboard-unblock" : "job-dashboard-resume"),
            });
            projects.CommanderRunner.Wake(
                project,
                needsPlan
                    ? "Project resumed by Klives — continue planning and submit the Grand Plan for approval."
                    : "Project resumed by Klives. Rehydrate current state and continue with the next concrete step.");
            projects.DeliverPendingDirectives(project.ProjectID);
            return true;
        }

        private AgentLongTermJobView BuildLongTermJobView(AgentLongTermJobLink link)
        {
            var projects = GetProjectsService();
            var project = projects?.Store.GetProject(link.ProjectId);
            var directive = projects?.Directives.Get(link.ProjectId, link.DirectiveId);
            var lastEvent = projects?.EventLog.ReadTail(link.ProjectId, 1).LastOrDefault();
            var pendingPlan = projects?.GrandPlans.Get(link.ProjectId).Versions
                .Where(x => x.Status
                    == Omnipotent.Services.Projects.GrandPlanVersionStatus.PendingApproval)
                .OrderByDescending(x => x.Version)
                .FirstOrDefault();
            var blocker = projects?.RuntimeState.Get(link.ProjectId).Blocker;
            string status = directive?.Status is Omnipotent.Services.Projects.ProjectDirectiveStatus.Completed
                    or Omnipotent.Services.Projects.ProjectDirectiveStatus.Failed
                    or Omnipotent.Services.Projects.ProjectDirectiveStatus.Revoked
                ? directive.Status.ToString()
                : project?.Status.ToString() ?? "Unavailable";
            string attentionKey = null;
            string attentionMessage = null;
            if (pendingPlan != null)
            {
                attentionKey = $"plan-{pendingPlan.Version}";
                attentionMessage = string.IsNullOrWhiteSpace(pendingPlan.Summary)
                    ? $"Grand Plan v{pendingPlan.Version} is ready for approval."
                    : $"Grand Plan v{pendingPlan.Version} is ready for approval: {pendingPlan.Summary}";
            }
            else if (project?.Status is Omnipotent.Services.Projects.ProjectStatus.Blocked
                or Omnipotent.Services.Projects.ProjectStatus.BudgetPaused)
            {
                attentionKey = $"{project.Status}-{blocker?.BlockerID ?? "project"}";
                attentionMessage = blocker?.Summary
                    ?? project.BlockedReason
                    ?? $"The job needs attention because it is {project.Status}.";
            }
            DateTime lastUpdated = new[]
            {
                directive?.UpdatedAt ?? DateTime.MinValue,
                lastEvent?.Timestamp ?? DateTime.MinValue,
                blocker?.UpdatedAt ?? DateTime.MinValue,
                link.CreatedAt
            }.Max();
            return new AgentLongTermJobView
            {
                JobId = link.JobId,
                ClientJobId = link.ClientJobId,
                ProjectId = link.ProjectId,
                DirectiveId = link.DirectiveId,
                ConversationId = link.ConversationId,
                Name = project?.Name ?? "Unavailable job",
                Goal = project?.Goal ?? string.Empty,
                Status = status,
                RequiresPlanApproval = pendingPlan != null,
                AttentionRequired = attentionMessage != null,
                AttentionMessage = attentionMessage,
                AttentionKey = attentionKey,
                Result = directive?.CompletionSummary
                    ?? directive?.Acknowledgement
                    ?? lastEvent?.Text,
                ArtifactPaths = directive?.CompletionArtifactPaths == null
                    ? new List<string>()
                    : new List<string>(directive.CompletionArtifactPaths),
                CreatedAt = link.CreatedAt,
                CompletedAt = directive?.CompletedAt ?? project?.CompletedAt,
                LastUpdated = lastUpdated
            };
        }

        private async Task MonitorLongTermJobsAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    foreach (var link in longTermJobs.Values)
                    {
                        try
                        {
                            var view = BuildLongTermJobView(link);
                            bool linkChanged = false;
                            if (!string.Equals(link.LastObservedStatus, view.Status, StringComparison.Ordinal))
                            {
                                link.LastObservedStatus = view.Status;
                                linkChanged = true;
                            }

                            if (view.AttentionRequired
                                && !string.Equals(link.LastAttentionKey, view.AttentionKey, StringComparison.Ordinal))
                            {
                                string suffix = Regex.Replace(
                                    view.AttentionKey ?? "attention", @"[^A-Za-z0-9_-]", "");
                                if (suffix.Length > 48) suffix = suffix.Substring(0, 48);
                                await AddNotificationAsync(new AgentNotification
                                {
                                    NotificationId = "jobattention" + link.JobId + suffix,
                                    Kind = "job-attention",
                                    Title = $"Long-term job needs attention: {view.Name}",
                                    Body = view.AttentionMessage ?? "This job needs your attention.",
                                    ConversationId = link.ConversationId,
                                    JobId = link.JobId,
                                    ProjectId = link.ProjectId
                                });
                                link.LastAttentionKey = view.AttentionKey;
                                linkChanged = true;
                            }

                            bool terminal = view.Status is "Completed" or "Failed" or "Revoked" or "Archived";
                            if (terminal && link.CompletionNotifiedAt == null)
                            {
                                await AddNotificationAsync(new AgentNotification
                                {
                                    NotificationId = "job" + link.JobId,
                                    Kind = "job-completed",
                                    Title = view.Status == "Completed"
                                        ? $"Long-term job finished: {view.Name}"
                                        : $"Long-term job ended ({view.Status}): {view.Name}",
                                    Body = view.Result ?? $"Job ended with status {view.Status}.",
                                    ConversationId = link.ConversationId,
                                    JobId = link.JobId,
                                    ProjectId = link.ProjectId,
                                    ArtifactPaths = view.ArtifactPaths
                                });
                                link.CompletionNotifiedAt = DateTime.UtcNow;
                                linkChanged = true;
                            }
                            if (linkChanged) await PersistLongTermJobAsync(link);
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                await ServiceLogError(
                                    ex,
                                    $"[KliveAgent] Could not refresh long-term job {link.JobId}.",
                                    false);
                            }
                            catch { }
                        }
                    }
                    await Task.Delay(TimeSpan.FromSeconds(10), token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    try { await ServiceLogError(ex, "[KliveAgent] Long-term job monitor failed (non-fatal).", false); } catch { }
                    try { await Task.Delay(TimeSpan.FromSeconds(10), token); } catch { break; }
                }
            }
        }

        private async Task PersistLongTermJobAsync(AgentLongTermJobLink link)
        {
            var path = Path.Combine(
                OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentJobsDirectory),
                link.JobId + ".json");
            await GetDataHandler().SerialiseObjectToFile(path, link);
        }

        private async Task LoadLongTermJobsAsync()
        {
            string dir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentJobsDirectory);
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var link = await GetDataHandler()
                        .ReadAndDeserialiseDataFromFile<AgentLongTermJobLink>(file);
                    if (link != null && IsSafeIdentifier(link.JobId)
                        && IsSafeIdentifier(link.ProjectId))
                    {
                        longTermJobs[link.JobId] = link;
                        if (IsSafeIdentifier(link.ClientJobId))
                            jobByClientId[link.ClientJobId] = link.JobId;
                    }
                }
                catch (Exception ex)
                {
                    await ServiceLogError(ex,
                        $"[KliveAgent] Skipped unreadable long-term job {Path.GetFileName(file)}.", false);
                }
            }
        }

        public List<AgentNotification> GetNotifications(bool unreadOnly = false) =>
            notifications.Values
                .Where(x => !unreadOnly || x.ReadAt == null)
                .OrderByDescending(x => x.CreatedAt)
                .ToList();

        public async Task<bool> MarkNotificationReadAsync(string notificationId)
        {
            if (!notifications.TryGetValue(notificationId ?? string.Empty, out var notification))
                return false;
            notification.ReadAt ??= DateTime.UtcNow;
            await PersistNotificationAsync(notification);
            return true;
        }

        private async Task AddNotificationAsync(AgentNotification notification)
        {
            if (notifications.ContainsKey(notification.NotificationId)) return;
            // Persist first: a process exit can delay in-memory visibility, but can never leave a
            // notification that looked accepted and then vanished after restart.
            Exception lastError = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    await PersistNotificationAsync(notification);
                    lastError = null;
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (attempt < 2) await Task.Delay(250 * (attempt + 1));
                }
            }
            if (lastError != null) throw lastError;
            notifications.TryAdd(notification.NotificationId, notification);
        }

        private async Task PersistNotificationAsync(AgentNotification notification)
        {
            var path = Path.Combine(
                OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentNotificationsDirectory),
                notification.NotificationId + ".json");
            await GetDataHandler().SerialiseObjectToFile(path, notification);
        }

        private async Task LoadNotificationsAsync()
        {
            string dir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentNotificationsDirectory);
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var notification = await GetDataHandler()
                        .ReadAndDeserialiseDataFromFile<AgentNotification>(file);
                    if (notification != null && IsSafeIdentifier(notification.NotificationId))
                        notifications[notification.NotificationId] = notification;
                }
                catch (Exception ex)
                {
                    await ServiceLogError(ex,
                        $"[KliveAgent] Skipped unreadable notification {Path.GetFileName(file)}.", false);
                }
            }
        }

        public List<object> GetConversationSummaries()
        {
            return conversations.Keys
                .Select(GetConversation)
                .Where(c => c != null)
                .OrderByDescending(c => c.LastUpdated)
                .Select(c => (object)new
                {
                    conversationId = c.ConversationId,
                    sourceChannel = c.SourceChannel.ToString(),
                    lastUpdated = c.LastUpdated,
                    messageCount = c.Messages.Count,
                    lastMessage = TruncateSummary(c.Messages.LastOrDefault()?.Content),
                    activeRun = c.RecentRuns.FirstOrDefault(r => r.CompletedAt == null),
                    latestRunStatus = c.RecentRuns.FirstOrDefault()?.Status.ToString()
                })
                .ToList();
        }

        public AgentConversationView GetConversation(string conversationId)
        {
            if (!conversations.TryGetValue(conversationId, out var conversation)) return null;
            lock (ConversationSync(conversationId))
            {
                return new AgentConversationView
                {
                    ConversationId = conversation.ConversationId,
                    SourceChannel = conversation.SourceChannel,
                    LastUpdated = conversation.LastUpdated,
                    Messages = (conversation.Messages ?? new List<AgentMessage>())
                        .Select(CloneMessage)
                        .ToList(),
                    RecentRuns = GetPendingApiResponses(conversationId)
                        .Take(20)
                        .ToList()
                };
            }
        }

        private static string TruncateSummary(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return text.Length <= 100 ? text : text.Substring(0, 100);
        }

        private static AgentMessage CloneMessage(AgentMessage message) => new()
        {
            MessageId = message.MessageId,
            RequestId = message.RequestId,
            Role = message.Role,
            Content = message.Content,
            Timestamp = message.Timestamp,
            ScriptResult = message.ScriptResult,
            ScriptResults = message.ScriptResults == null
                ? null
                : new List<AgentScriptResult>(message.ScriptResults),
            SenderName = message.SenderName,
            DeliveryStatus = message.DeliveryStatus
        };

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
                OmniPaths.GlobalPaths.KliveAgentChatRunsDirectory,
                OmniPaths.GlobalPaths.KliveAgentJobsDirectory,
                OmniPaths.GlobalPaths.KliveAgentNotificationsDirectory,
                OmniPaths.GlobalPaths.KliveAgentBackgroundTasksDirectory,
                OmniPaths.GlobalPaths.KliveAgentScheduledTasksDirectory,
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
                        if (conv != null && IsSafeIdentifier(conv.ConversationId))
                        {
                            conv.Messages ??= new List<AgentMessage>();
                            foreach (var message in conv.Messages)
                            {
                                if (string.IsNullOrWhiteSpace(message.MessageId))
                                    message.MessageId = Guid.NewGuid().ToString("N");
                                message.DeliveryStatus ??= "completed";
                            }
                            if (!conversations.TryGetValue(conv.ConversationId, out var existing)
                                || existing.LastUpdated < conv.LastUpdated)
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

        private async Task<bool> PersistConversationAsync(AgentConversation conversation)
        {
            SemaphoreSlim gate = null;
            try
            {
                if (conversation == null || !IsSafeIdentifier(conversation.ConversationId))
                    throw new InvalidOperationException("Refusing to persist an invalid conversation ID.");

                gate = conversationPersistGates.GetOrAdd(
                    conversation.ConversationId, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync();
                AgentConversation snapshot;
                lock (ConversationSync(conversation.ConversationId))
                {
                    snapshot = new AgentConversation
                    {
                        ConversationId = conversation.ConversationId,
                        SourceChannel = conversation.SourceChannel,
                        LastUpdated = conversation.LastUpdated,
                        Messages = (conversation.Messages ?? new List<AgentMessage>())
                            .Select(CloneMessage)
                            .ToList()
                    };
                }
                var path = Path.Combine(
                    OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentConversationsDirectory),
                    $"{snapshot.ConversationId}.json");
                await GetDataHandler().SerialiseObjectToFile(path, snapshot);
                return true;
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "[KliveAgent] Failed to persist conversation.", false);
                return false;
            }
            finally
            {
                gate?.Release();
            }
        }
    }
}
