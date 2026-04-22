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
- Use C# inside {{{ }}} only when you need runtime data, codebase inspection, or to take an action.
- Multiple script blocks in the same reply share locals and execution state.
- Prefer structured discovery over guessing. Inspect types, classes, methods, files, and documentation before using unfamiliar APIs.
- Generic discovery helpers: ListProjectClasses(...), FindProjectClass(...), ExploreClassCode(...), GetTypeSchema(...), GetTypeInfo(...), GetMethodDocumentationEntries(...), GetMethodDocumentation(...), SearchSymbols(...), SearchCode(...), ReadFile(...), ListDirectory(...), FindFiles(...).
- Runtime/action helpers remain available too, but do not hardcode assumptions when the codebase can tell you the answer.
- If a script fails, change approach instead of repeating the same failing call.
- When you already have enough information, stop scripting and answer plainly.";
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

        private const int MaxAgentIterations = 25;

        public async Task<AgentChatResponse> ProcessMessageAsync(string userMessage, AgentConversation conversation, string? senderName = null)
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
                var sharedGlobals = new ScriptGlobals(agentService);
                var scriptSession = scriptEngine.CreateSession(sharedGlobals);
                int totalPromptTokens = 0;
                int totalCompletionTokens = 0;
                int iterationsDone = 0;

                // Agent loop: LLM responds → scripts execute → results fed back → repeat
                for (int iteration = 0; iteration < MaxAgentIterations; iteration++)
                {
                    iterationsDone = iteration + 1;
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
                        agentService.Stats.Record(totalPromptTokens, totalCompletionTokens, iterationsDone, allScriptsExecuted.Count, allScriptsExecuted.Count(s => !s.Success));
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

                    // No scripts → this is the final text response, return it
                    if (!hasScripts)
                    {
                        var finalText = string.Join("\n", segments.Where(s => !s.IsScript).Select(s => s.Content)).Trim();
                        llm.ResetSession(llmSessionId);

                        conversation.Messages.Add(new AgentMessage { Role = AgentMessageRole.User, Content = userMessage });
                        conversation.Messages.Add(new AgentMessage { Role = AgentMessageRole.Agent, Content = finalText });
                        conversation.LastUpdated = DateTime.UtcNow;

                        agentService.Stats.Record(totalPromptTokens, totalCompletionTokens, iterationsDone, allScriptsExecuted.Count, allScriptsExecuted.Count(s => !s.Success));

                        // Auto-record a memory when scripts were successfully run (non-trivial action completed)
                        if (allScriptsExecuted.Count > 0 && allScriptsExecuted.Any(s => s.Success))
                        {
                            _ = memory.SaveMemoryAsync(
                                $"Completed task: \"{TruncateForMemory(userMessage, 120)}\" — {TruncateForMemory(finalText, 200)}",
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

                    // Has scripts → execute them and build a follow-up prompt with results
                    var resultsSummary = new StringBuilder();
                    foreach (var segment in segments)
                    {
                        if (!segment.IsScript) continue;

                        var result = await scriptSession.ExecuteAsync(segment.Content ?? "");
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
                    currentPrompt = $"[Script Results]\n{resultsSummary}\n\nEarlier successful script locals are still available. Now continue: if you have what you need, give the user a final answer (no scripts). If you need to take further action based on these results, write the next script.";
                }

                // If we hit the iteration cap, return whatever we have
                llm.ResetSession(llmSessionId);
                conversation.Messages.Add(new AgentMessage { Role = AgentMessageRole.User, Content = userMessage });
                conversation.Messages.Add(new AgentMessage { Role = AgentMessageRole.Agent, Content = "[Reached maximum reasoning steps]" });
                conversation.LastUpdated = DateTime.UtcNow;
                agentService.Stats.Record(totalPromptTokens, totalCompletionTokens, iterationsDone, allScriptsExecuted.Count, allScriptsExecuted.Count(s => !s.Success));
                return new AgentChatResponse
                {
                    ConversationId = conversation.ConversationId,
                    Response = "I hit my maximum number of reasoning steps. Here's what I managed to do so far.",
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

        private string BuildConversationPrompt(string systemPrompt, AgentConversation conversation, string userMessage, string? senderName = null)
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
            var recentMessages = conversation.Messages.TakeLast(6).ToList();
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
