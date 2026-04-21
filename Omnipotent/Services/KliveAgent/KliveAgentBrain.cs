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

== DISCORD RUNTIME TOOLS (use these first for any Discord task — do NOT search code) ==

  GetDiscordGuilds() -> Task<string>
      Returns all guilds (servers) the bot is currently in, with their IDs and names.
      ALWAYS call this first when you need a guild ID — never guess or search code for IDs.

  GetDiscordChannels(ulong guildId) -> Task<string>
      Returns all text channels in a guild with their IDs.
      Call after GetDiscordGuilds() to find the channel ID.

  SendDiscordMessage(ulong guildId, ulong channelId, string message) -> Task<string>
      Send a message to a channel. Requires IDs from the above two tools.

Example — user asks ""send 'hello' to #general in Hypixel server"":
{{{
Log(await GetDiscordGuilds());
}}}
Then (using the ID from output):
{{{
Log(await GetDiscordChannels(12345678901234567));
}}}
Then:
{{{
Log(await SendDiscordMessage(12345678901234567, 98765432109876543, ""hello""));
}}}

== EXECUTION TOOLS ==

  ExecuteServiceMethod(string serviceTypeName, string methodName, params object[] args) -> Task<object>
      Call any method on any running service.

  GetServiceObject(string serviceTypeName, string objectName) -> object
      Read any field or property from a running service.

  GetOmnipotentUptime() -> TimeSpan
  Log(string message)          — Output text you'll see in the result.
  SaveMemory(string content, string[] tags, int importance)
      Save a fact or observation for future conversations. Call this when you learn something useful.
  SaveShortcut(string title, string content, string[] tags)
      Save a reusable procedure you just figured out. Call this immediately after solving a non-obvious
      task so you can skip re-discovery next time. Example: after finding a guild ID and sending a message,
      save a shortcut titled "Send Discord message to <GuildName>" with the exact steps.
  GetShortcuts() -> string       — List all saved shortcuts.
  RecallMemories(string query, int maxResults) -> List<AgentMemoryEntry>
  SpawnBackgroundTask(string description, string code) -> string taskId
  Delay(int ms)

== MEMORY / SHORTCUT RULES ==
- After completing any non-trivial action (Discord message, service call, etc.) ALWAYS save a memory noting what you did.
- After figuring out a multi-step process (e.g. finding a guild ID then sending a message), ALWAYS save a shortcut so you can skip those steps next time.
- Shortcuts are shown at the top of every prompt — check them before running discovery scripts.

== DISCOVERY TOOLS (for understanding the codebase, not for finding live runtime IDs) ==

  ListServices() -> List<string>
  SearchSymbols(string query) -> string
  GetTypeInfo(string typeName) -> string
  GetMethodSignature(string typeName, string methodName) -> string
  SearchCode(string text, string subfolder = """") -> string
  ReadFile(string relativePath, int startLine = 1, int maxLines = 200) -> string
  ListDirectory(string relativePath = """") -> string
  FindFiles(string pattern) -> string

== CRITICAL RULES ==
1. For Discord tasks: use GetDiscordGuilds() / GetDiscordChannels() to find IDs at RUNTIME. Never search source code to find guild or channel IDs.
2. DO NOT repeat the same discovery call. If you already have the info, act on it.
3. After ONE discovery script, write the execution script immediately — do not describe the results.
4. You can put MULTIPLE {{{ }}} blocks in one response.
5. If a script fails, try a different approach — do not retry the same call.
6. If stuck after 2-3 attempts, stop and explain to the user in plain text.

== DELAYED / SCHEDULED ACTIONS ==
- NEVER use Delay() in the same script as the action.
- Use SpawnBackgroundTask for ""do X in Y seconds"":
  SpawnBackgroundTask(""Send Discord message in 10s"", @""
      await Delay(10000);
      await ExecuteServiceMethod(""""KliveBotDiscord"""", """"SendMessageToKlives"""", """"hello"""");
  "");
  Then reply immediately: ""Got it, I'll send that in 10 seconds.""

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

        private const int MaxAgentIterations = 25;

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
        public string Content { get; set; }
    }
}
