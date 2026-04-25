using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAgent.Models;
using Omnipotent.Services.KliveLLM;
using System.Text;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.KliveAgent
{
    /// <summary>
    /// The central orchestration brain for KliveAgent.
    ///
    /// Every user message goes through a Think â†’ Script â†’ Observe loop:
    ///   1. Build a rich system prompt: personality + repo map (task-personalised) + BM25 memories + tool catalogue.
    ///   2. LLM responds with a planning statement and optional {{{ C# script }}} blocks.
    ///   3. Scripts are executed; structured observations are fed back.
    ///   4. Loop continues until the LLM produces a text-only response (the final answer).
    ///
    /// Spec references:
    ///   Chapter 7  â€” Repo map injected in every prompt
    ///   Chapter 9  â€” Agentic Loop Architecture
    ///   Chapter 10 â€” Context Window Management &amp; Token Budgeting
    /// </summary>
    public class KliveAgentBrain
    {
        private readonly KliveAgent agentService;
        private readonly KliveAgentScriptEngine scriptEngine;
        private readonly KliveAgentMemory memory;

        // Tool catalogue injected into every system prompt -- tells the LLM exactly what is callable.
        private const string ToolCatalogue = @"
[Discovery Tools -- call inside {{{ }}} to explore the codebase before acting]
  SearchCode(text, subfolder?, maxResults?)          -- text search across .cs files
  SearchCodeRegex(pattern, subfolder?, maxResults?)  -- regex search across .cs files
  SearchCodeHybrid(query, maxResults?)               -- BM25-ranked search (best relevance)
  FindDefinition(symbolName)                         -- file + line where a type/method is defined
  FindReferences(typeName)                           -- all files that reference a given type
  GetFileSymbols(relativePath)                       -- symbols declared in a specific file
  GetRankedFiles(max?, seed?)                        -- PageRank-ranked files by structural importance
  GetRepoMap(maxTokens?)                             -- full live repo map (already in prompt; refresh if needed)
  ReadFile(relativePath, startLine?, maxLines?)      -- read source file with pagination
  ListDirectory(relativePath?)                       -- list files/folders at a path
  FindFiles(pattern, subfolder?)                     -- glob-style file search
  ListProjectClasses(query?, maxResults?)            -- all classes/interfaces/enums in source
  FindProjectClass(typeName)                         -- locate a type's source file + line
  ExploreClassCode(typeName, maxLines?)              -- read source around a type declaration
  GetTypeSchema(typeName)                            -- reflection-based type structure
  GetTypeInfo(typeName)                              -- human-readable type API
  GetMethodDocumentation(typeName, methodName)       -- source-level docs + signature
  SearchSymbols(query, maxResults?)                  -- symbol search across loaded assemblies
  BrowseNamespace(namespaceName)                     -- list types in a namespace
  GetFullTypeHierarchy(typeName)                     -- full inheritance chain with all members

[Action Tools]
  ListServices()                                     -- list active OmniServices; returns List<ServiceInfo> with .Name, .TypeName, .Uptime
  GetService(serviceName)                            -- get the LIVE service instance by TypeName or Name (use this + CallObjectMethod for action tasks)
  ExecuteServiceMethod(serviceType, method, args...) -- invoke a method directly on a service (simplest path; no GetService step needed)
  GetServiceMember(serviceType, memberName)          -- read a field/property VALUE from a service (NOT the service itself -- use GetService() for that)
  GetObjectMember(obj, memberName)                   -- read any field/property (incl. private) on any object; walks full inheritance chain
  CallObjectMethod(obj, methodName, args...)         -- invoke any method (incl. private/async) on any object; awaits Task<T> automatically
  GetObjectTypeInfo(obj)                             -- list ALL fields, properties, and methods (public + private) of any object's type
  ListAgentCapabilities(category?)                   -- list typed capabilities
  ExecuteAgentCapabilityAsync(name, args?, confirmed?) -- invoke a typed capability
  SpawnBackgroundTask(description, code)             -- launch a long-running background script
  SaveMemory(content, tags?, importance?)            -- persist information across sessions
  SaveShortcut(title, content, tags?)                -- save a reusable procedure you discovered
  RecallMemories(query, maxResults?)                 -- search your persistent memory
  Log(message)                                       -- append to script output buffer

[Pattern: Calling a method on a service -- ALWAYS start here for action tasks]
  // Preferred: one-step direct call
  var result = await ExecuteServiceMethod(""KliveBotDiscord"", ""SendMessageToUserAsync"", ""Klives"", ""Hello!"");
  Log(result?.ToString() ?? ""done"");

  // Alternative two-step: get instance first, then call (use when you need the object for multiple calls)
  var svc = GetService(""KliveBotDiscord"");           // returns the live service instance
  Log(GetObjectTypeInfo(svc));                         // inspect available methods before calling
  await CallObjectMethod(svc, ""SendMessageToUserAsync"", ""Klives"", ""Hello!"");

  // Find available services and their TypeNames
  var services = ListServices();                       // returns List<ServiceInfo>
  var discord = services.FirstOrDefault(s => s.TypeName == ""KliveBotDiscord"");
  Log(discord?.Name ?? ""not found"");
";

        public KliveAgentBrain(KliveAgent agentService, KliveAgentScriptEngine scriptEngine, KliveAgentMemory memory)
        {
            this.agentService = agentService;
            this.scriptEngine = scriptEngine;
            this.memory = memory;
        }

        // â”€â”€ Prompt Assembly â”€â”€

        public async Task<string> BuildSystemPrompt(string userMessage, AgentConversation conversation)
        {
            var personality = await agentService.GetStringOmniSetting(
                "KliveAgent_Personality",
                defaultValue: KliveAgentPersonality.Default);

            // Extract PascalCase seed words from the user message for repo map personalisation
            var seeds = KliveAgentRepoMap.ExtractSeedsFromText(userMessage);

            // Build token-budgeted, task-personalised repo map
            var repoMap = string.Empty;
            try
            {
                // Only inject the repo map when the task has code-exploration signals (PascalCase
                // type/service names found in the message). For plain action requests like
                // "send me a Discord message" there are no seeds and the map adds pure token waste.
                if (agentService.RepoMap != null && seeds.Count > 0)
                    repoMap = agentService.RepoMap.GetRepoMap(KliveAgentContextBudget.RepoMapBudget, seeds);
            }
            catch { /* best-effort */ }

            // BM25-ranked memories, budget-capped
            var memoriesSection = string.Empty;
            try
            {
                memoriesSection = await memory.FormatMemoriesForPrompt(
                    userMessage,
                    maxMemories: 6,
                    maxShortcuts: 4,
                    maxTokens: KliveAgentContextBudget.MemoryBudget);
            }
            catch { }

            var sb = new StringBuilder();

            sb.AppendLine(personality);
            sb.AppendLine();

            sb.AppendLine("[Script Execution Rules]");
            sb.AppendLine("- Write C# inside {{{ }}} to inspect the codebase or take action.");
            sb.AppendLine("- Multiple script blocks in the same reply share state â€” locals persist between blocks.");
            sb.AppendLine("- ALWAYS discover before acting: use GetTypeSchema / ExploreClassCode / GetMethodDocumentation before calling unfamiliar APIs.");
            sb.AppendLine("- If a script fails, read the error and change your approach â€” never retry the same failing code.");
            sb.AppendLine("- When you have completed the task, give a final text-only answer with no script blocks.");
            sb.AppendLine("- On your FIRST reply to a new task: write 1-3 sentences describing your plan before any script.");
            sb.AppendLine();
            sb.AppendLine("[CRITICAL - No Pretending]");
            sb.AppendLine("- If the task requires taking ANY action (sending a message, creating data, calling a service, modifying state), you MUST run a script that actually performs it.");
            sb.AppendLine("- NEVER describe an action as done, complete, or successful unless a script in THIS session already executed it and returned OK.");
            sb.AppendLine("- A text-only final answer is valid ONLY for: (1) purely conversational/informational replies that require zero system calls, OR (2) confirming completion after your scripts already ran.");
            sb.AppendLine("- Saying 'All set!' or 'Done!' without a preceding script is a hallucination. Do not do it.");
            sb.AppendLine();

            sb.Append(ToolCatalogue);

            if (!string.IsNullOrWhiteSpace(repoMap))
            {
                sb.AppendLine();
                sb.Append(repoMap);
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine("[Repo Map] Not pre-loaded (no specific type/service names found in this task). Call GetRepoMap() or FindDefinition() inside a script if you need codebase context.");
            }

            if (!string.IsNullOrWhiteSpace(memoriesSection))
            {
                sb.AppendLine();
                sb.Append(memoriesSection);
            }

            return KliveAgentContextBudget.TruncateToTokens(
                sb.ToString(),
                KliveAgentContextBudget.TotalSystemPromptBudget);
        }

        // â”€â”€ Response Parsing â”€â”€

        public static List<ResponseSegment> ParseLLMResponse(string response)
        {
            var segments = new List<ResponseSegment>();
            if (string.IsNullOrEmpty(response)) return segments;

            var pattern = @"\{\{\{(.*?)\}\}\}";
            var matches = Regex.Matches(response, pattern, RegexOptions.Singleline);

            int lastIndex = 0;
            foreach (Match match in matches)
            {
                if (match.Index > lastIndex)
                {
                    var text = response.Substring(lastIndex, match.Index - lastIndex).Trim();
                    if (!string.IsNullOrEmpty(text))
                        segments.Add(new ResponseSegment { IsScript = false, Content = text });
                }

                var code = match.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(code))
                    segments.Add(new ResponseSegment { IsScript = true, Content = code });

                lastIndex = match.Index + match.Length;
            }

            if (lastIndex < response.Length)
            {
                var text = response.Substring(lastIndex).Trim();
                if (!string.IsNullOrEmpty(text))
                    segments.Add(new ResponseSegment { IsScript = false, Content = text });
            }

            return segments;
        }

        // â”€â”€ Brain Orchestration â”€â”€

        private const int MaxAgentIterations = 25;

        public async Task<AgentChatResponse> ProcessMessageAsync(
            string userMessage,
            AgentConversation conversation,
            string? senderName = null)
        {
            try
            {
                var systemPrompt = await BuildSystemPrompt(userMessage, conversation);
                var llmSessionId = $"kliveagent-{conversation.ConversationId}";

                var llmServices = await agentService.GetServicesByType<KliveLLM.KliveLLM>();
                if (llmServices == null || llmServices.Length == 0)
                {
                    return new AgentChatResponse
                    {
                        ConversationId = conversation.ConversationId,
                        Response = "My brain (KliveLLM) isn't running right now. I'm essentially lobotomized. Try again later.",
                        Success = false,
                        ErrorMessage = "KliveLLM service not available."
                    };
                }

                var llm = (KliveLLM.KliveLLM)llmServices[0];
                llm.ResetSession(llmSessionId);

                var allScriptsExecuted = new List<AgentScriptResult>();
                var sharedGlobals = new ScriptGlobals(agentService);
                var scriptSession = scriptEngine.CreateSession(sharedGlobals);
                int totalPromptTokens = 0;
                int totalCompletionTokens = 0;
                int iterationsDone = 0;

                // First iteration: pass system prompt so KliveLLM sets it as the system role (not stuffed into the user turn).
                // Subsequent iterations only send observations — the session retains the system message.
                var currentPrompt = BuildUserPrompt(conversation, userMessage, senderName);
                string? firstIterationSystemPrompt = systemPrompt;

                // â”€â”€ Agentic Loop: Think â†’ Script â†’ Observe â†’ repeat â”€â”€
                for (int iteration = 0; iteration < MaxAgentIterations; iteration++)
                {
                    iterationsDone = iteration + 1;

                    KliveLLM.KliveLLM.KliveLLMResponse llmResponse;
                    try
                    {
                        // Pass system prompt only on iteration 0 so it is set as the LLM session's
                        // system role message once — not re-injected into every user turn.
                        llmResponse = await llm.QueryLLM(currentPrompt, llmSessionId,
                            systemPrompt: firstIterationSystemPrompt);
                        firstIterationSystemPrompt = null; // don't resend
                    }
                    catch (Exception llmEx)
                    {
                        return new AgentChatResponse
                        {
                            ConversationId = conversation.ConversationId,
                            Response = $"LLM query failed: {llmEx.Message}",
                            Success = false,
                            ErrorMessage = llmEx.ToString()
                        };
                    }

                    if (llmResponse == null || !llmResponse.Success)
                    {
                        agentService.Stats.Record(totalPromptTokens, totalCompletionTokens, iterationsDone,
                            allScriptsExecuted.Count, allScriptsExecuted.Count(s => !s.Success));
                        return new AgentChatResponse
                        {
                            ConversationId = conversation.ConversationId,
                            Response = "Something went wrong with my brain. " + (llmResponse?.ErrorMessage ?? "LLM returned null."),
                            Success = false,
                            ErrorMessage = llmResponse?.ErrorMessage ?? "LLM returned null response."
                        };
                    }

                    totalPromptTokens += llmResponse.PromptTokens;
                    totalCompletionTokens += llmResponse.CompletionTokens;

                    var segments = ParseLLMResponse(llmResponse.Response ?? "");
                    var hasScripts = segments.Any(s => s.IsScript);

                    // No scripts â†’ this is the final answer
                    if (!hasScripts)
                    {
                        var finalText = string.Join("\n", segments
                            .Where(s => !s.IsScript)
                            .Select(s => s.Content)).Trim();

                        llm.ResetSession(llmSessionId);

                        conversation.Messages.Add(new AgentMessage { Role = AgentMessageRole.User, Content = userMessage });
                        conversation.Messages.Add(new AgentMessage { Role = AgentMessageRole.Agent, Content = finalText });
                        conversation.LastUpdated = DateTime.UtcNow;

                        agentService.Stats.Record(totalPromptTokens, totalCompletionTokens, iterationsDone,
                            allScriptsExecuted.Count, allScriptsExecuted.Count(s => !s.Success));

                        // Auto-save a codebase-pattern memory when non-trivial scripts succeeded
                        if (allScriptsExecuted.Count > 0 && allScriptsExecuted.Any(s => s.Success))
                        {
                            _ = memory.SaveMemoryAsync(
                                $"Completed: \"{TruncateForMemory(userMessage, 120)}\" â€” {TruncateForMemory(finalText, 200)}",
                                tags: new[] { "auto", "completed-task" },
                                source: "agent",
                                importance: 2);
                        }

                        return new AgentChatResponse
                        {
                            ConversationId = conversation.ConversationId,
                            Response = finalText,
                            ScriptsExecuted = allScriptsExecuted,
                            Success = true,
                            PromptTokens = totalPromptTokens,
                            CompletionTokens = totalCompletionTokens,
                            Iterations = iterationsDone
                        };
                    }

                    // Execute scripts â†’ collect structured observations
                    var observationSb = new StringBuilder();
                    observationSb.AppendLine("[Script Observations]");

                    foreach (var segment in segments)
                    {
                        if (!segment.IsScript)
                        {
                            if (!string.IsNullOrWhiteSpace(segment.Content))
                                observationSb.AppendLine($"[Agent thought] {segment.Content.Trim()}");
                            continue;
                        }

                        var result = await scriptSession.ExecuteAsync(segment.Content);
                        allScriptsExecuted.Add(result);

                        if (result.Success)
                        {
                            var output = KliveAgentContextBudget.TruncateToTokens(
                                result.Output ?? string.Empty,
                                KliveAgentContextBudget.ScriptOutputBudget);

                            observationSb.AppendLine($"[OK | {result.ExecutionTimeMs}ms]");
                            if (!string.IsNullOrWhiteSpace(output))
                                observationSb.AppendLine(output);
                        }
                        else
                        {
                            observationSb.AppendLine($"[ERROR | {result.ExecutionTimeMs}ms]");
                            observationSb.AppendLine(result.ErrorMessage ?? "Unknown error.");
                        }

                        observationSb.AppendLine();
                    }

                    // Feed observations back as the next prompt turn
                    currentPrompt = observationSb.ToString().TrimEnd()
                        + "\n\nEarlier script state is preserved. If you have what you need, give the final answer now (no scripts). "
                        + "Otherwise write your next script(s).";
                }

                // Hit iteration cap
                llm.ResetSession(llmSessionId);
                conversation.Messages.Add(new AgentMessage { Role = AgentMessageRole.User, Content = userMessage });
                conversation.Messages.Add(new AgentMessage { Role = AgentMessageRole.Agent, Content = "[Reached maximum reasoning steps]" });
                conversation.LastUpdated = DateTime.UtcNow;

                agentService.Stats.Record(totalPromptTokens, totalCompletionTokens, iterationsDone,
                    allScriptsExecuted.Count, allScriptsExecuted.Count(s => !s.Success));

                return new AgentChatResponse
                {
                    ConversationId = conversation.ConversationId,
                    Response = "I hit my maximum number of reasoning steps. Here is what I managed to complete so far.",
                    ScriptsExecuted = allScriptsExecuted,
                    Success = true,
                    PromptTokens = totalPromptTokens,
                    CompletionTokens = totalCompletionTokens,
                    Iterations = iterationsDone
                };
            }
            catch (Exception ex)
            {
                await agentService.ServiceLogError(ex, "KliveAgentBrain.ProcessMessageAsync");
                return new AgentChatResponse
                {
                    ConversationId = conversation.ConversationId,
                    Response = $"Something broke in my brain: {ex.Message}",
                    Success = false,
                    ErrorMessage = ex.ToString()
                };
            }
        }

        // â”€â”€ Conversation Prompt Builder â”€â”€

        /// <summary>
        /// Builds the user-turn portion of the prompt (history + new message).
        /// The system prompt is no longer embedded here — it is passed separately to KliveLLM
        /// so it lands in the 'system' role of the chat, not the 'user' role.
        /// </summary>
        private string BuildUserPrompt(
            AgentConversation conversation,
            string userMessage,
            string? senderName = null)
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrEmpty(senderName))
            {
                sb.AppendLine($"[Current User: {senderName}]");
                sb.AppendLine($"Channel: {conversation.SourceChannel}");
                sb.AppendLine();
            }

            // Budget-aware conversation history selection
            if (conversation.Messages.Count > 0)
            {
                var historyMessages = SelectHistoryMessages(
                    conversation.Messages, userMessage, KliveAgentContextBudget.HistoryBudget);

                if (historyMessages.Count > 0)
                {
                    sb.AppendLine("[Conversation History]");
                    foreach (var msg in historyMessages)
                    {
                        var role = msg.Role == AgentMessageRole.User ? "User" : "KliveAgent";
                        sb.AppendLine($"{role}: {msg.Content}");
                    }
                    sb.AppendLine();
                }
            }

            sb.AppendLine("[New Message]");
            sb.AppendLine(string.IsNullOrEmpty(senderName) ? $"User: {userMessage}" : $"{senderName}: {userMessage}");

            return sb.ToString();
        }

        private static List<AgentMessage> SelectHistoryMessages(
            List<AgentMessage> messages,
            string currentQuery,
            int maxTokens)
        {
            if (messages.Count == 0) return new List<AgentMessage>();

            var pairs = new List<(int idx, AgentMessage user, AgentMessage? agent, double score)>();
            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i].Role != AgentMessageRole.User) continue;
                var agent = (i + 1 < messages.Count && messages[i + 1].Role == AgentMessageRole.Agent)
                    ? messages[i + 1] : null;

                var indexFromEnd = messages.Count - i;
                var score = KliveAgentContextBudget.ScoreMessage(
                    messages[i].Content + " " + (agent?.Content ?? ""),
                    currentQuery,
                    indexFromEnd);

                pairs.Add((i, messages[i], agent, score));
            }

            var usedTokens = 0;
            var selected = new List<(int idx, AgentMessage user, AgentMessage? agent)>();

            foreach (var (idx, user, agent, _) in pairs.OrderByDescending(p => p.score))
            {
                var cost = KliveAgentContextBudget.EstimateTokens(
                    user.Content + " " + (agent?.Content ?? ""));
                if (usedTokens + cost > maxTokens) continue;
                usedTokens += cost;
                selected.Add((idx, user, agent));
            }

            return selected
                .OrderBy(x => x.idx)
                .SelectMany(x => x.agent != null
                    ? new[] { x.user, x.agent }
                    : new[] { x.user })
                .ToList();
        }

        private static string TruncateForMemory(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('\n', ' ').Replace('\r', ' ');
            return s.Length <= max ? s : s[..max] + "…";
        }
    }

    public class ResponseSegment
    {
        public bool IsScript { get; set; }
        public string? Content { get; set; }
    }
}
