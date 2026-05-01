using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAgent.Models;
using Omnipotent.Services.KliveLLM;
using System.Globalization;
using System.Reflection;
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
    ///   Chapter 7  -- Repo map injected in every prompt
    ///   Chapter 9  -- Agentic Loop Architecture
    ///   Chapter 10 -- Context Window Management &amp; Token Budgeting
    /// </summary>
    public class KliveAgentBrain
    {
        private readonly KliveAgent agentService;
        private readonly KliveAgentScriptEngine scriptEngine;
        private readonly KliveAgentMemory memory;

        private sealed record PromptToolDescriptor(string MethodName, string Description);

        private static readonly PromptToolDescriptor[] StarterTools =
        [
            new("ListProjectClasses", "Browse project types by name, namespace, or path."),
            new("FindProjectClass", "Jump straight to a likely project type when you already know its name."),
            new("ExploreClassCode", "Read the implementation around a target type declaration."),
            new("GetMethodDocumentation", "Inspect exact method signatures, defaults, and XML docs before calling unfamiliar APIs."),
            new("GetTypeSchema", "Inspect exact members and parameter types for any type, including ScriptGlobals itself."),
            new("ListServices", "See which OmniServices are live right now."),
            new("GetService", "Fetch a live service instance by type name or display name."),
            new("GetObjectTypeInfo", "Inspect a live object's members before calling or reading them."),
            new("ExecuteServiceMethod", "Call a known service method directly when you already know the exact service type and method name."),
            new("CallObjectMethod", "Invoke a method on a live object after inspecting it. Await Task-returning methods."),
            new("Log", "Write short structured observations back to the loop.")
        ];

        private static readonly PromptToolDescriptor[] DiscoveryTools =
        [
            new("SearchCodeHybrid", "Use BM25 search when you need a relevant code entry point fast."),
            new("FindDefinition", "Jump to the exact definition of a type or method."),
            new("FindReferences", "See where a type is used before changing behavior."),
            new("ReadFile", "Read the source directly with line pagination."),
            new("GetFileSymbols", "List the symbols declared in one file."),
            new("ListDirectory", "Browse a directory relative to the codebase root."),
            new("GetRepoMap", "Refresh the live repo map if you need broader structure."),
            new("SearchCode", "Use plain-text search only when a narrower type lookup did not find the target."),
            new("SearchCodeRegex", "Use regex search for signature-shaped lookups.")
        ];

        private static readonly PromptToolDescriptor[] AdvancedRuntimeTools =
        [
            new("GetServiceObject", "Read a field or property from a live service by type name."),
            new("GetObjectMember", "Inspect a specific field or property on a live object."),
            new("GetTypeInfo", "Get a human-readable API summary for a type."),
            new("SearchSymbols", "Search loaded Omnipotent assemblies for matching types, methods, or properties."),
            new("BrowseNamespace", "Browse public types inside a namespace."),
            new("GetFullTypeHierarchy", "Inspect inherited members before calling into framework or base-class behavior."),
            new("ListAgentCapabilities", "See if an explicit typed capability already exists for the task."),
            new("ExecuteAgentCapabilityAsync", "Run an explicit capability when one exists instead of stitching raw reflection together."),
            new("SpawnBackgroundTask", "Launch long-running work only when the task truly needs isolation."),
            new("Delay", "Pause inside long-running scripts while respecting cancellation.")
        ];

        private static readonly PromptToolDescriptor[] MemoryTools =
        [
            new("SaveMemory", "Persist a durable note. Tags must be a string array, for example new[] { \"service\", \"workflow\" }."),
            new("RecallMemories", "Search saved memories for prior discoveries or IDs."),
            new("SaveShortcut", "Store a reusable recipe immediately after solving a non-obvious task."),
            new("GetShortcuts", "Review saved shortcuts before rediscovering a workflow.")
        ];

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
            sb.AppendLine("- Multiple script blocks in the same reply share state — locals persist between blocks.");
            sb.AppendLine("- ALWAYS discover before acting: use GetTypeSchema / ExploreClassCode / GetMethodDocumentation before calling unfamiliar APIs.");
            sb.AppendLine("- Prefer one narrow lookup over broad searching. Find the target type/service first, then inspect it.");
            sb.AppendLine("- Scripts are disposable scratchpads: keep them compact, avoid boilerplate, and do not add comments unless a line would otherwise be ambiguous.");
            sb.AppendLine("- Do not narrate obvious steps in comments. Prefer concise, directly executable code.");
            sb.AppendLine("- Await methods that return Task or Task<T>. Sync methods should not be awaited.");
            sb.AppendLine("- If a script fails, read the error and change your approach — never retry the same failing code.");
            sb.AppendLine("- When you have completed the task, give a final text-only answer with no script blocks.");
            sb.AppendLine("- On your FIRST reply to a new task: write 1-3 sentences describing your plan before any script.");
            sb.AppendLine("- Code fences (```csharp or ```) are also recognised as script blocks — use {{{ }}} by default.");
            sb.AppendLine();
            sb.AppendLine("[Background Tasks]");
            sb.AppendLine("- SpawnBackgroundTask() runs in a SEPARATE isolated scope — it cannot read or set variables in your current session.");
            sb.AppendLine("- Background tasks communicate results ONLY via SaveMemory() / Log(). They cannot return values to your session directly.");
            sb.AppendLine("- Do NOT assume a variable set inside SpawnBackgroundTask() is accessible in your outer script — it will not be.");
            sb.AppendLine();
            sb.AppendLine("[CRITICAL - No Pretending]");
            sb.AppendLine("- If the task requires taking ANY action (sending a message, creating data, calling a service, modifying state), you MUST run a script that actually performs it.");
            sb.AppendLine("- NEVER describe an action as done, complete, or successful unless a script in THIS session already executed it and returned OK.");
            sb.AppendLine("- A text-only final answer is valid ONLY for: (1) purely conversational/informational replies that require zero system calls, OR (2) confirming completion after your scripts already ran.");
            sb.AppendLine("- Saying 'All set!' or 'Done!' without a preceding script is a hallucination. Do not do it.");
            sb.AppendLine();

            sb.Append(BuildToolGuide(userMessage));

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
        public static List<ResponseSegment> ParseLLMResponse(string response)
        {
            var segments = new List<ResponseSegment>();
            if (string.IsNullOrEmpty(response)) return segments;

            // Primary delimiter: {{{ ... }}}
            // Fallback delimiter: ```csharp ... ``` (or plain ``` ... ```)
            // Both are matched in a single pass by alternation so their relative order is preserved.
            var pattern = @"\{\{\{(.*?)\}\}\}|```(?:csharp|cs)?\s*\n(.*?)```";
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

                // Group 1 = {{{ }}} capture, Group 2 = ``` ``` capture
                var code = (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).Trim();
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
                                $"Completed: \"{TruncateForMemory(userMessage, 120)}\" -- {TruncateForMemory(finalText, 200)}",
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

                        var result = await scriptSession.ExecuteAsync(segment.Content ?? string.Empty);
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

        public static string BuildToolGuide(string userMessage)
        {
            var sb = new StringBuilder();

            sb.AppendLine("[Runtime Workflow]");
            sb.AppendLine("1. Tools are the public methods on ScriptGlobals.");
            sb.AppendLine("2. Narrow the task to one target type, service, or method before doing anything else.");
            sb.AppendLine("3. Inspect exact signatures before calling unfamiliar APIs.");
            sb.AppendLine("4. Act with the smallest script that can prove progress, then stop when you have the answer.");
            sb.AppendLine();

            AppendToolSection(sb, "Starter Tools", StarterTools);

            if (ShouldIncludeDiscoveryTools(userMessage))
            {
                sb.AppendLine();
                AppendToolSection(sb, "Codebase Tools", DiscoveryTools);
            }

            if (ShouldIncludeAdvancedRuntimeTools(userMessage))
            {
                sb.AppendLine();
                AppendToolSection(sb, "Advanced Runtime Tools", AdvancedRuntimeTools);
            }

            if (ShouldIncludeMemoryTools(userMessage))
            {
                sb.AppendLine();
                AppendToolSection(sb, "Memory Tools", MemoryTools);
            }

            sb.AppendLine();
            sb.AppendLine("[Tool Self-Discovery]");
            sb.AppendLine("If a needed tool is not listed above, inspect ScriptGlobals itself before using it:");
            sb.AppendLine("  GetTypeSchema(\"ScriptGlobals\")");
            sb.AppendLine("  GetMethodDocumentation(\"ScriptGlobals\", \"ToolName\")");
            sb.AppendLine("  ExploreClassCode(\"ScriptGlobals\")");

            return sb.ToString().TrimEnd();
        }

        private static void AppendToolSection(StringBuilder sb, string title, IEnumerable<PromptToolDescriptor> tools)
        {
            sb.AppendLine($"[{title}]");
            foreach (var tool in tools)
            {
                var signature = FormatToolSignature(tool.MethodName);
                if (string.IsNullOrWhiteSpace(signature))
                    continue;

                sb.AppendLine($"  {signature} -- {tool.Description}");
            }
        }

        private static string? FormatToolSignature(string methodName)
        {
            var method = typeof(ScriptGlobals)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .FirstOrDefault(m => m.Name.Equals(methodName, StringComparison.Ordinal));

            if (method == null)
                return null;

            var parameters = string.Join(", ", method.GetParameters().Select(FormatParameterSignature));
            return $"{method.Name}({parameters}) -> {FormatTypeName(method.ReturnType)}";
        }

        private static string FormatParameterSignature(ParameterInfo parameter)
        {
            var prefix = parameter.GetCustomAttribute<ParamArrayAttribute>() != null ? "params " : string.Empty;
            var signature = $"{prefix}{FormatTypeName(parameter.ParameterType)} {parameter.Name}";

            if (parameter.HasDefaultValue)
                signature += $" = {FormatDefaultValue(parameter.DefaultValue)}";

            return signature;
        }

        private static string FormatTypeName(Type type)
        {
            if (type == typeof(void))
                return "void";

            var nullable = Nullable.GetUnderlyingType(type);
            if (nullable != null)
                return $"{FormatTypeName(nullable)}?";

            if (type.IsArray)
                return $"{FormatTypeName(type.GetElementType()!)}[]";

            if (type.IsGenericType)
            {
                var genericName = type.Name;
                var tickIndex = genericName.IndexOf('`');
                if (tickIndex >= 0)
                    genericName = genericName[..tickIndex];

                var genericArguments = string.Join(", ", type.GetGenericArguments().Select(FormatTypeName));
                return $"{genericName}<{genericArguments}>";
            }

            return type.Name switch
            {
                nameof(String) => "string",
                nameof(Int32) => "int",
                nameof(Int64) => "long",
                nameof(UInt32) => "uint",
                nameof(UInt64) => "ulong",
                nameof(Int16) => "short",
                nameof(UInt16) => "ushort",
                nameof(Boolean) => "bool",
                nameof(Object) => "object",
                nameof(Double) => "double",
                nameof(Single) => "float",
                nameof(Decimal) => "decimal",
                nameof(Byte) => "byte",
                nameof(SByte) => "sbyte",
                nameof(Char) => "char",
                _ => type.Name,
            };
        }

        private static string FormatDefaultValue(object? value)
        {
            return value switch
            {
                null => "null",
                string text => $"\"{text}\"",
                char character => $"'{character}'",
                bool boolean => boolean ? "true" : "false",
                Enum enumValue => $"{enumValue.GetType().Name}.{enumValue}",
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null",
            };
        }

        private static bool ShouldIncludeDiscoveryTools(string userMessage)
        {
            return KliveAgentRepoMap.ExtractSeedsFromText(userMessage).Count > 0
                || ContainsAny(userMessage, "code", "class", "method", "file", "repo", "source", "implementation", "where", "why", "how");
        }

        private static bool ShouldIncludeAdvancedRuntimeTools(string userMessage)
        {
            return ContainsAny(userMessage, "service", "capability", "background", "task", "property", "field", "member", "invoke", "call", "execute");
        }

        private static bool ShouldIncludeMemoryTools(string userMessage)
        {
            return ContainsAny(userMessage, "memory", "memories", "remember", "recall", "shortcut");
        }

        private static bool ContainsAny(string userMessage, params string[] keywords)
        {
            if (string.IsNullOrWhiteSpace(userMessage))
                return false;

            return keywords.Any(keyword => userMessage.Contains(keyword, StringComparison.OrdinalIgnoreCase));
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
