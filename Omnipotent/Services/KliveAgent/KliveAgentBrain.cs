using LLama.Common;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAgent.Models;
using Omnipotent.Services.KliveLLM;
using System.Text;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.KliveAgent
{
    public class KliveAgentBrain
    {
        private readonly KliveAgent agentService;
        private readonly KliveAgentScriptEngine scriptEngine;
        private readonly KliveAgentMemory memory;

        public KliveAgentBrain(KliveAgent agentService, KliveAgentScriptEngine scriptEngine, KliveAgentMemory memory)
        {
            this.agentService = agentService;
            this.scriptEngine = scriptEngine;
            this.memory = memory;
        }

        // ── Prompt Assembly ──

        public async Task<string> BuildSystemPrompt(string conversationContext)
        {
            var personality = await agentService.GetStringOmniSetting(
                "KliveAgent_Personality",
                defaultValue: KliveAgentPersonality.Default);

            var memories = await memory.FormatMemoriesForPrompt(conversationContext);

            var sb = new StringBuilder();
            sb.AppendLine(personality);
            sb.AppendLine();
            sb.AppendLine(GetScriptInstructions());

            if (!string.IsNullOrEmpty(memories))
            {
                sb.AppendLine(memories);
            }

            return sb.ToString();
        }

        private string GetScriptInstructions()
        {
            return @"[Script Execution]
You can write and execute C# scripts by enclosing them in {{{ }}} delimiters.
Scripts run in a Roslyn sandbox with access to discovery and execution globals.

== DISCOVERY TOOLS (use these to find the right APIs before executing) ==

  ListServices() -> List<string>
      Returns all active OmniService names and types. Start here.

  SearchSymbols(string query, int maxResults = 25) -> string
      Search for types, methods, or properties by keyword across all Omnipotent code.
      Example: SearchSymbols(""Discord"") finds all Discord-related symbols.

  GetTypeInfo(string typeName) -> string
      Get full details (methods, properties, fields) for any type by name.
      Works with service type names or any loaded type.

  GetMethodSignature(string typeName, string methodName) -> string
      Get detailed parameter info for a specific method.

  BrowseNamespace(string namespaceName, int maxResults = 30) -> string
      List all types in a namespace. Example: BrowseNamespace(""Omnipotent.Services"")

  GetFullTypeHierarchy(string typeName) -> string
      See the inheritance chain and all public methods including inherited ones.

== EXECUTION TOOLS (use after you've discovered the right APIs) ==

  ExecuteServiceMethod(string serviceTypeName, string methodName, params object[] args) -> Task<object>
      Call any method on any running service.

  GetServiceObject(string serviceTypeName, string objectName) -> object
      Read any field or property from a running service.

  GetOmnipotentUptime() -> TimeSpan

  Log(string message)
      Output text back to you (you'll see it in the script result).

  SaveMemory(string content, string[] tags, int importance)
  RecallMemories(string query, int maxResults) -> List<AgentMemoryEntry>

  SpawnBackgroundTask(string description, string code) -> string taskId
      Spawn a long-running task. The code runs in its own ScriptGlobals context.

  Delay(int ms)

== WORKFLOW ==
When the user asks you to do something and you don't already know the exact API:
1. FIRST: Use a discovery script to find the right service/method
2. THEN: Use an execution script with the exact API you discovered
You can do both in a single response using multiple {{{ }}} blocks.

Example — user asks ""how many followers does my Instagram have?"":

Step 1 - Discover:
{{{
Log(SearchSymbols(""follower""));
}}}

Step 2 - After seeing the results, call the right method:
{{{
var result = await ExecuteServiceMethod(""OmniGram"", ""GetFollowerCount"");
Log($""Follower count: {result}"");
}}}

If you already know the exact API from a previous conversation or memory, skip discovery and execute directly.

Rules:
- Only use scripts when the user's request requires data retrieval or action execution.
- Always explain what you're doing in natural language alongside scripts.
- Script output is fed back to you so you can interpret results naturally.
- Use Log() to capture values you want to see and report on.
- If a method isn't found, use discovery tools to find the correct name — don't guess.";
        }

        // ── Response Parsing ──

        public static List<ResponseSegment> ParseLLMResponse(string response)
        {
            var segments = new List<ResponseSegment>();
            if (string.IsNullOrEmpty(response)) return segments;

            var pattern = @"\{\{\{(.*?)\}\}\}";
            var matches = Regex.Matches(response, pattern, RegexOptions.Singleline);

            int lastIndex = 0;
            foreach (Match match in matches)
            {
                // Text before the script
                if (match.Index > lastIndex)
                {
                    var text = response.Substring(lastIndex, match.Index - lastIndex).Trim();
                    if (!string.IsNullOrEmpty(text))
                        segments.Add(new ResponseSegment { IsScript = false, Content = text });
                }

                // The script itself
                var code = match.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(code))
                    segments.Add(new ResponseSegment { IsScript = true, Content = code });

                lastIndex = match.Index + match.Length;
            }

            // Text after last script
            if (lastIndex < response.Length)
            {
                var text = response.Substring(lastIndex).Trim();
                if (!string.IsNullOrEmpty(text))
                    segments.Add(new ResponseSegment { IsScript = false, Content = text });
            }

            return segments;
        }

        // ── Brain Orchestration ──

        public async Task<AgentChatResponse> ProcessMessageAsync(string userMessage, AgentConversation conversation)
        {
            try
            {
                // Build the full system prompt with conversation context
                var systemPrompt = await BuildSystemPrompt(userMessage);

                // Create a unique session ID for this conversation's LLM interaction
                var llmSessionId = $"kliveagent-{conversation.ConversationId}";

                // Get KliveLLM and query it
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

                // Reset session and inject the system prompt + history for a clean context
                llm.ResetSession(llmSessionId);

                // Build the combined prompt: system + conversation history + user message
                var fullPrompt = BuildConversationPrompt(systemPrompt, conversation, userMessage);

                var llmResponse = await llm.QueryLLM(fullPrompt, llmSessionId);
                if (!llmResponse.Success)
                {
                    return new AgentChatResponse
                    {
                        ConversationId = conversation.ConversationId,
                        Response = "Something went wrong with my brain. " + (llmResponse.ErrorMessage ?? "Unknown error."),
                        Success = false,
                        ErrorMessage = llmResponse.ErrorMessage
                    };
                }

                // Reset session after use to avoid context bloat
                llm.ResetSession(llmSessionId);

                // Parse the LLM response into text and script segments
                var segments = ParseLLMResponse(llmResponse.Response);
                var scriptsExecuted = new List<AgentScriptResult>();
                var finalResponse = new StringBuilder();

                foreach (var segment in segments)
                {
                    if (!segment.IsScript)
                    {
                        finalResponse.AppendLine(segment.Content);
                    }
                    else
                    {
                        // Execute the script
                        var globals = new ScriptGlobals(agentService);
                        var result = await scriptEngine.ExecuteScriptAsync(segment.Content, globals);
                        scriptsExecuted.Add(result);

                        if (result.Success && !string.IsNullOrEmpty(result.Output))
                        {
                            // Feed result back to LLM for interpretation
                            var interpretPrompt = $"Script executed successfully. Output:\n{result.Output}\n\nInterpret and present this result naturally to the user.";
                            var interpretResponse = await llm.QueryLLM(interpretPrompt);
                            llm.ResetSession(interpretResponse.SessionId);

                            if (interpretResponse.Success)
                            {
                                finalResponse.AppendLine(interpretResponse.Response);
                            }
                            else
                            {
                                finalResponse.AppendLine($"Script output: {result.Output}");
                            }
                        }
                        else if (!result.Success)
                        {
                            finalResponse.AppendLine($"[Script failed: {result.ErrorMessage}]");
                        }
                    }
                }

                var responseText = finalResponse.ToString().Trim();

                // Record messages in conversation
                conversation.Messages.Add(new AgentMessage
                {
                    Role = AgentMessageRole.User,
                    Content = userMessage
                });
                conversation.Messages.Add(new AgentMessage
                {
                    Role = AgentMessageRole.Agent,
                    Content = responseText,
                });
                conversation.LastUpdated = DateTime.UtcNow;

                return new AgentChatResponse
                {
                    ConversationId = conversation.ConversationId,
                    Response = responseText,
                    ScriptsExecuted = scriptsExecuted,
                    Success = true
                };
            }
            catch (Exception ex)
            {
                return new AgentChatResponse
                {
                    ConversationId = conversation.ConversationId,
                    Response = $"Something broke in my brain: {ex.Message}",
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        private string BuildConversationPrompt(string systemPrompt, AgentConversation conversation, string userMessage)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[System]");
            sb.AppendLine(systemPrompt);
            sb.AppendLine();

            // Include recent conversation history (last 20 messages to keep context manageable)
            var recentMessages = conversation.Messages.TakeLast(20).ToList();
            if (recentMessages.Count > 0)
            {
                sb.AppendLine("[Conversation History]");
                foreach (var msg in recentMessages)
                {
                    var role = msg.Role == AgentMessageRole.User ? "User" : "KliveAgent";
                    sb.AppendLine($"{role}: {msg.Content}");
                }
                sb.AppendLine();
            }

            sb.AppendLine("[New Message]");
            sb.AppendLine($"User: {userMessage}");

            return sb.ToString();
        }
    }

    public class ResponseSegment
    {
        public bool IsScript { get; set; }
        public string Content { get; set; }
    }
}
