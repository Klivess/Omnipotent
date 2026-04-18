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

== DISCOVERY TOOLS ==

  ListServices() -> List<string>
      Returns all active OmniService names and types.

  SearchSymbols(string query, int maxResults = 25) -> string
      Search by keyword across all Omnipotent types, methods, properties.

  GetTypeInfo(string typeName) -> string
      Get methods, properties, and fields for a type by short or full name.

  GetMethodSignature(string typeName, string methodName) -> string
      Get parameter details for a method.

  BrowseNamespace(string namespaceName, int maxResults = 30) -> string
  GetFullTypeHierarchy(string typeName) -> string

== EXECUTION TOOLS ==

  ExecuteServiceMethod(string serviceTypeName, string methodName, params object[] args) -> Task<object>
      Call any method on any running service.

  GetServiceObject(string serviceTypeName, string objectName) -> object
      Read any field or property from a running service.

  Log(string message)          — Output text you'll see in the result.
  GetOmnipotentUptime() -> TimeSpan
  SaveMemory(string content, string[] tags, int importance)
  RecallMemories(string query, int maxResults) -> List<AgentMemoryEntry>
  SpawnBackgroundTask(string description, string code) -> string taskId
  Delay(int ms)

== CRITICAL RULES ==
1. DO NOT repeat the same discovery call. If you already have the info, use it.
2. After ONE discovery script, you MUST act on the results in the NEXT script.
   Do NOT just describe what you found — write the execution script.
3. You can put MULTIPLE {{{ }}} blocks in one response: first discovers, second executes.
4. When you have all the information needed, execute immediately. Do not ask the user for confirmation.
5. After executing an action, give a SHORT final answer (1-2 sentences, no scripts).
6. If a script fails, try a different approach — do not retry the same call.

== WORKFLOW EXAMPLE ==
User asks ""message me hello on discord"":

{{{
Log(GetTypeInfo(""KliveBotDiscord""));
}}}

After seeing SendMessageToKlives(string), immediately write:

{{{
var result = await ExecuteServiceMethod(""KliveBotDiscord"", ""SendMessageToKlives"", ""hello"");
Log($""Sent: {result}"");
}}}

Then give a short answer like: ""Done — sent 'hello' to your Discord.""

== IMPORTANT ==
- GetTypeInfo works with short names like ""KliveBotDiscord"", not just full namespaces.
- For Discord messages, the service is KliveBotDiscord with SendMessageToKlives(string).
- Only use scripts when you need to DO something or LOOK UP something. Casual conversation needs no scripts.";
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

        private const int MaxAgentIterations = 5;

        public async Task<AgentChatResponse> ProcessMessageAsync(string userMessage, AgentConversation conversation, string senderName = null)
        {
            try
            {
                var systemPrompt = await BuildSystemPrompt(userMessage);
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
                var currentPrompt = BuildConversationPrompt(systemPrompt, conversation, userMessage, senderName);

                // Agent loop: LLM responds → scripts execute → results fed back → repeat
                for (int iteration = 0; iteration < MaxAgentIterations; iteration++)
                {
                    KliveLLM.KliveLLM.KliveLLMResponse llmResponse;
                    try
                    {
                        llmResponse = await llm.QueryLLM(currentPrompt, llmSessionId);
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
                        return new AgentChatResponse
                        {
                            ConversationId = conversation.ConversationId,
                            Response = "Something went wrong with my brain. " + (llmResponse?.ErrorMessage ?? "LLM returned null."),
                            Success = false,
                            ErrorMessage = llmResponse?.ErrorMessage ?? "LLM returned null response."
                        };
                    }

                    var segments = ParseLLMResponse(llmResponse.Response ?? "");
                    var hasScripts = segments.Any(s => s.IsScript);

                    // No scripts → this is the final text response, return it
                    if (!hasScripts)
                    {
                        var finalText = string.Join("\n", segments.Where(s => !s.IsScript).Select(s => s.Content)).Trim();
                        llm.ResetSession(llmSessionId);

                        conversation.Messages.Add(new AgentMessage { Role = AgentMessageRole.User, Content = userMessage });
                        conversation.Messages.Add(new AgentMessage { Role = AgentMessageRole.Agent, Content = finalText });
                        conversation.LastUpdated = DateTime.UtcNow;

                        return new AgentChatResponse
                        {
                            ConversationId = conversation.ConversationId,
                            Response = finalText,
                            ScriptsExecuted = allScriptsExecuted,
                            Success = true
                        };
                    }

                    // Has scripts → execute them and build a follow-up prompt with results
                    var resultsSummary = new StringBuilder();
                    foreach (var segment in segments)
                    {
                        if (!segment.IsScript) continue;

                        var globals = new ScriptGlobals(agentService);
                        var result = await scriptEngine.ExecuteScriptAsync(segment.Content, globals);
                        allScriptsExecuted.Add(result);

                        if (result.Success)
                        {
                            resultsSummary.AppendLine($"[Script OK] {(string.IsNullOrEmpty(result.Output) ? "(no output)" : result.Output)}");
                        }
                        else
                        {
                            resultsSummary.AppendLine($"[Script FAILED] {result.ErrorMessage}");
                        }
                    }

                    // Feed results back as the next user turn so the LLM can act on them
                    currentPrompt = $"[Script Results]\n{resultsSummary}\n\nNow continue: if you have what you need, give the user a final answer (no scripts). If you need to take further action based on these results, write the next script.";
                }

                // If we hit the iteration cap, return whatever we have
                llm.ResetSession(llmSessionId);
                conversation.Messages.Add(new AgentMessage { Role = AgentMessageRole.User, Content = userMessage });
                conversation.Messages.Add(new AgentMessage { Role = AgentMessageRole.Agent, Content = "[Reached maximum reasoning steps]" });
                conversation.LastUpdated = DateTime.UtcNow;

                return new AgentChatResponse
                {
                    ConversationId = conversation.ConversationId,
                    Response = "I hit my maximum number of reasoning steps. Here's what I managed to do so far.",
                    ScriptsExecuted = allScriptsExecuted,
                    Success = true
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

        private string BuildConversationPrompt(string systemPrompt, AgentConversation conversation, string userMessage, string senderName = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[System]");
            sb.AppendLine(systemPrompt);
            sb.AppendLine();

            if (!string.IsNullOrEmpty(senderName))
            {
                sb.AppendLine($"[Current User: {senderName}]");
                sb.AppendLine($"Channel: {conversation.SourceChannel}");
                sb.AppendLine();
            }
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
