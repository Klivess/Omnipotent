using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Logging;
using Omnipotent.Services.KliveLocalLLM;
using System.Text;

namespace Omnipotent.Services.KliveAgent
{
    public sealed class KliveAgentBrain
    {
        private readonly KliveAgent _agent;
        private const int MaxActionsPerDecision = 3;

        public KliveAgentBrain(KliveAgent agent)
        {
            _agent = agent;
        }

        public async Task<KliveAgentBrainExecutionResult> DecideForTaskAsync(
            KliveAgentBrainExecutionRequest request,
            CancellationToken cancellationToken)
        {
            var goal = request.Goal?.Trim() ?? string.Empty;
            var missionType = "manual-task";

            if (string.IsNullOrWhiteSpace(goal))
            {
                return new KliveAgentBrainExecutionResult
                {
                    MissionType = missionType,
                    RequestedAtUtc = DateTime.UtcNow,
                    CompletedAtUtc = DateTime.UtcNow,
                    UsedFallback = true,
                    Summary = "Goal was empty.",
                    FinalResponse = "No goal was provided for KliveAgent to execute."
                };
            }

            var memoryHits = _agent.SearchMemory(goal, 8);
            var recentEvents = _agent.GetRecentEventsSnapshot(20);
            var memoryEntries = BuildMemoryEntries(memoryHits, 6);
            var eventEntries = BuildEventEntries(recentEvents, 10);
            var prompt = BuildTaskPrompt(goal, request.Context, memoryHits, recentEvents);
            var contextSnapshot = new KliveAgentBrainContextSnapshot
            {
                Goal = goal,
                UserContext = request.Context ?? string.Empty,
                PromptUsed = prompt,
                MemoryEntries = memoryEntries,
                RecentEventEntries = eventEntries,
                MatchedRuleEntries = new List<string>()
            };

            return await QueryAndExecuteAsync(missionType, prompt, request.AllowScriptExecution, contextSnapshot, null, cancellationToken);
        }

        public async Task<KliveAgentBrainExecutionResult> DecideForEventAsync(
            KliveAgentObservedEvent observed,
            IReadOnlyList<KliveAgentEventRule> matchingRules,
            IReadOnlyList<KliveAgentObservedEvent> recentEvents,
            CancellationToken cancellationToken)
        {
            var missionType = "live-event";
            var memoryQuery = $"{observed.ServiceName} {observed.Message} {observed.ExceptionType} {observed.ExceptionMessage}";
            var memoryHits = _agent.SearchMemory(memoryQuery, 8);
            var memoryEntries = BuildMemoryEntries(memoryHits, 6);
            var eventEntries = BuildEventEntries(recentEvents, 12);
            var matchedRuleEntries = matchingRules.Take(8)
                .Select(rule => $"{rule.Name} | Min={rule.MinimumLogType} | Notify={rule.NotifyKlives} | Cooldown={rule.CooldownSeconds}s")
                .ToList();
            var prompt = BuildEventPrompt(observed, matchingRules, memoryHits, recentEvents);
            var contextSnapshot = new KliveAgentBrainContextSnapshot
            {
                Goal = $"Handle live event from {observed.ServiceName}",
                UserContext = BuildObservedEventFallbackMessage(observed, "live-event-context"),
                PromptUsed = prompt,
                MemoryEntries = memoryEntries,
                RecentEventEntries = eventEntries,
                MatchedRuleEntries = matchedRuleEntries
            };

            return await QueryAndExecuteAsync(missionType, prompt, allowScriptExecution: true, contextSnapshot, observed, cancellationToken);
        }

        private async Task<KliveAgentBrainExecutionResult> QueryAndExecuteAsync(
            string missionType,
            string prompt,
            bool allowScriptExecution,
            KliveAgentBrainContextSnapshot contextSnapshot,
            KliveAgentObservedEvent? observed,
            CancellationToken cancellationToken)
        {
            var result = new KliveAgentBrainExecutionResult
            {
                DecisionId = Guid.NewGuid().ToString("N"),
                MissionType = missionType,
                RequestedAtUtc = DateTime.UtcNow,
                CompletedAtUtc = DateTime.UtcNow,
                ContextUsed = contextSnapshot
            };

            try
            {
                var llm = await _agent.ResolveServiceAsync<KliveLLM>();
                if (llm == null || !llm.IsServiceActive())
                {
                    return BuildFallbackResult(result, "KliveLLM service is unavailable.", observed);
                }

                var llmResponse = await llm.QueryLocalLLMAsync(prompt, $"kliveagent-brain-{missionType}");
                if (!llmResponse.Success || string.IsNullOrWhiteSpace(llmResponse.Response))
                {
                    var error = string.IsNullOrWhiteSpace(llmResponse.ErrorMessage)
                        ? "KliveLLM returned no content."
                        : llmResponse.ErrorMessage;
                    return BuildFallbackResult(result, error, observed);
                }

                result.RawModelOutput = llmResponse.Response;
                result.LlmSessionId = llmResponse.SessionId ?? string.Empty;
                result.ApproxOutputTokens = EstimateTokenCount(llmResponse.Response);
                var decision = ParseDecisionEnvelope(llmResponse.Response);
                if (decision == null)
                {
                    return BuildFallbackResult(result, "Failed to parse LLM decision JSON.", observed, llmResponse.Response);
                }

                result.Decision = decision;
                result.Summary = decision.Summary;
                result.FinalResponse = decision.FinalResponse;

                var actions = (decision.Actions ?? new List<KliveAgentBrainAction>())
                    .Take(MaxActionsPerDecision)
                    .ToList();

                foreach (var action in actions)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    var actionResult = await ExecuteActionAsync(action, allowScriptExecution, missionType, observed);
                    result.ActionResults.Add(actionResult);
                }

                if (result.ActionResults.Count == 0 && observed != null && observed.LogType == OmniLogging.LogType.Error)
                {
                    var fallbackMessage = BuildObservedEventFallbackMessage(observed, "error-no-action-selected");
                    await _agent.SendMessageToKlivesFromBrainAsync(fallbackMessage);
                    result.ActionResults.Add(new KliveAgentBrainActionResult
                    {
                        ActionType = "notify_klives",
                        Status = "completed",
                        Details = "Fallback notification sent due to empty action list for error event."
                    });
                }

                result.CompletedAtUtc = DateTime.UtcNow;
                return result;
            }
            catch (Exception ex)
            {
                await _agent.LogAutonomousError("KliveAgent brain execution failed", ex);
                return BuildFallbackResult(result, ex.Message, observed);
            }
        }

        private async Task<KliveAgentBrainActionResult> ExecuteActionAsync(
            KliveAgentBrainAction action,
            bool allowScriptExecution,
            string missionType,
            KliveAgentObservedEvent? observed)
        {
            var normalized = NormalizeActionType(action.ActionType);
            var actionResult = new KliveAgentBrainActionResult
            {
                ActionType = normalized,
                Status = "skipped"
            };

            try
            {
                if (normalized == "notify_klives")
                {
                    var message = string.IsNullOrWhiteSpace(action.Message)
                        ? $"KliveAgent action requested with no message. Mission={missionType}."
                        : action.Message;
                    await _agent.SendMessageToKlivesFromBrainAsync(message);
                    actionResult.Status = "completed";
                    actionResult.Details = "Message sent to Klives.";
                    return actionResult;
                }

                if (normalized == "save_memory")
                {
                    if (!Enum.TryParse<KliveAgentMemoryType>(action.MemoryType, true, out var memoryType))
                    {
                        memoryType = KliveAgentMemoryType.Note;
                    }

                    var memory = new KliveAgentMemoryRecord
                    {
                        Type = memoryType,
                        Title = string.IsNullOrWhiteSpace(action.MemoryTitle) ? "KliveAgent Memory" : action.MemoryTitle,
                        Content = action.MemoryContent ?? string.Empty,
                        Tags = action.MemoryTags ?? new List<string>(),
                        Source = "kliveagent-brain",
                        Importance = Math.Clamp(action.MemoryImportance, 0, 1),
                        CreatedAtUtc = DateTime.UtcNow,
                        LastUpdatedAtUtc = DateTime.UtcNow
                    };

                    await _agent.SaveMemoryFromBrainAsync(memory);
                    actionResult.Status = "completed";
                    actionResult.Details = "Memory saved.";
                    return actionResult;
                }

                if (normalized == "run_script")
                {
                    if (!allowScriptExecution)
                    {
                        actionResult.Status = "blocked";
                        actionResult.Details = "Script execution was disabled for this request.";
                        return actionResult;
                    }

                    if (string.IsNullOrWhiteSpace(action.ScriptCode))
                    {
                        actionResult.Status = "failed";
                        actionResult.Details = "No script code provided.";
                        return actionResult;
                    }

                    var scriptCode = action.ScriptCode;
                    if (scriptCode.Length > 12000)
                    {
                        actionResult.Status = "blocked";
                        actionResult.Details = "Script exceeds maximum allowed size.";
                        return actionResult;
                    }

                    var run = await _agent.ExecuteScriptFromBrainAsync(scriptCode, $"brain:{missionType}", observed);
                    actionResult.Status = run.Status;
                    actionResult.ScriptRunId = run.RunId;
                    actionResult.Details = string.IsNullOrWhiteSpace(run.Error) ? "Script dispatched." : run.Error;
                    return actionResult;
                }

                actionResult.Status = "skipped";
                actionResult.Details = $"Unsupported or no-op action type '{action.ActionType}'.";
                return actionResult;
            }
            catch (Exception ex)
            {
                actionResult.Status = "failed";
                actionResult.Details = ex.Message;
                await _agent.LogAutonomousError($"Action execution failed: {normalized}", ex);
                return actionResult;
            }
        }

        private static string BuildTaskPrompt(
            string goal,
            string context,
            IReadOnlyList<KliveAgentMemorySearchResult> memoryHits,
            IReadOnlyList<KliveAgentObservedEvent> recentEvents)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are KliveAgent, a Jarvis-like autonomous operator for Omnipotent.");
            sb.AppendLine("Decide the best next actions to achieve the goal. Scripts are OPTIONAL and should only be used when direct actions are insufficient.");
            sb.AppendLine();
            sb.AppendLine("Allowed action types:");
            sb.AppendLine("- notify_klives: send a message to Klives.");
            sb.AppendLine("- save_memory: persist long-term memory.");
            sb.AppendLine("- run_script: execute C# script asynchronously (only if necessary).\n");
            sb.AppendLine("Return ONLY valid JSON matching this schema:");
            sb.AppendLine("{");
            sb.AppendLine("  \"summary\": \"...\",");
            sb.AppendLine("  \"should_act\": true,");
            sb.AppendLine("  \"confidence\": 0.0,");
            sb.AppendLine("  \"final_response\": \"...\",");
            sb.AppendLine("  \"actions\": [");
            sb.AppendLine("    { \"action_type\": \"notify_klives|save_memory|run_script\", \"reason\": \"...\", \"message\": \"...\", \"memory_type\": \"Note\", \"memory_title\": \"...\", \"memory_content\": \"...\", \"memory_tags\": [\"...\"], \"memory_importance\": 0.7, \"script_code\": \"...\" }");
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine($"Goal:\n{goal}");
            if (!string.IsNullOrWhiteSpace(context))
            {
                sb.AppendLine($"Context:\n{context}");
            }

            sb.AppendLine("Relevant memories:");
            foreach (var hit in memoryHits.Take(6))
            {
                sb.AppendLine($"- [{hit.Memory.Type}] {hit.Memory.Title} :: {hit.Memory.Content}");
            }

            sb.AppendLine("Recent system events:");
            foreach (var ev in recentEvents.TakeLast(10))
            {
                sb.AppendLine($"- {ev.OccurredAtUtc:O} | {ev.ServiceName} | {ev.LogType} | {ev.Message}");
            }

            return sb.ToString();
        }

        private static string BuildEventPrompt(
            KliveAgentObservedEvent observed,
            IReadOnlyList<KliveAgentEventRule> matchingRules,
            IReadOnlyList<KliveAgentMemorySearchResult> memoryHits,
            IReadOnlyList<KliveAgentObservedEvent> recentEvents)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are KliveAgent, a Jarvis-like autonomous operator watching live Omnipotent events.");
            sb.AppendLine("Decide if this event requires action. Scripts are OPTIONAL; use them only when needed to complete a concrete task.");
            sb.AppendLine();
            sb.AppendLine("Allowed action types:");
            sb.AppendLine("- notify_klives");
            sb.AppendLine("- save_memory");
            sb.AppendLine("- run_script");
            sb.AppendLine();
            sb.AppendLine("Return ONLY valid JSON with the same schema as requested before.");
            sb.AppendLine();
            sb.AppendLine("Observed event:");
            sb.AppendLine($"- Time: {observed.OccurredAtUtc:O}");
            sb.AppendLine($"- Service: {observed.ServiceName}");
            sb.AppendLine($"- Type: {observed.LogType}");
            sb.AppendLine($"- Message: {observed.Message}");
            if (!string.IsNullOrWhiteSpace(observed.ExceptionType) || !string.IsNullOrWhiteSpace(observed.ExceptionMessage))
            {
                sb.AppendLine($"- Exception: {observed.ExceptionType} :: {observed.ExceptionMessage}");
            }

            sb.AppendLine("Matching policy rules:");
            foreach (var rule in matchingRules.Take(6))
            {
                sb.AppendLine($"- {rule.Name} | Notify={rule.NotifyKlives} | Min={rule.MinimumLogType} | CooldownSec={rule.CooldownSeconds}");
            }

            sb.AppendLine("Relevant memories:");
            foreach (var hit in memoryHits.Take(6))
            {
                sb.AppendLine($"- [{hit.Memory.Type}] {hit.Memory.Title} :: {hit.Memory.Content}");
            }

            sb.AppendLine("Recent events around this incident:");
            foreach (var ev in recentEvents.TakeLast(12))
            {
                sb.AppendLine($"- {ev.OccurredAtUtc:O} | {ev.ServiceName} | {ev.LogType} | {ev.Message}");
            }

            return sb.ToString();
        }

        private static KliveAgentBrainExecutionResult BuildFallbackResult(
            KliveAgentBrainExecutionResult seed,
            string reason,
            KliveAgentObservedEvent? observed,
            string rawModelOutput = "")
        {
            seed.UsedFallback = true;
            seed.RawModelOutput = rawModelOutput;
            seed.ApproxOutputTokens = EstimateTokenCount(rawModelOutput);
            seed.Summary = $"Fallback path used: {reason}";
            seed.FinalResponse = reason;
            seed.Decision = new KliveAgentBrainDecisionEnvelope
            {
                Summary = seed.Summary,
                ShouldAct = observed != null && observed.LogType == OmniLogging.LogType.Error,
                Confidence = 0.0,
                FinalResponse = seed.FinalResponse,
                Actions = new List<KliveAgentBrainAction>()
            };

            if (observed != null && observed.LogType == OmniLogging.LogType.Error)
            {
                seed.Decision.Actions.Add(new KliveAgentBrainAction
                {
                    ActionType = "notify_klives",
                    Reason = "Fallback escalation for error event",
                    Message = BuildObservedEventFallbackMessage(observed, reason)
                });
            }

            seed.CompletedAtUtc = DateTime.UtcNow;
            return seed;
        }

        private static List<string> BuildMemoryEntries(IReadOnlyList<KliveAgentMemorySearchResult> memoryHits, int maxItems)
        {
            return memoryHits
                .Take(Math.Max(1, maxItems))
                .Select(hit => $"[{hit.Memory.Type}] {hit.Memory.Title} :: {hit.Memory.Content}")
                .ToList();
        }

        private static List<string> BuildEventEntries(IReadOnlyList<KliveAgentObservedEvent> recentEvents, int maxItems)
        {
            return recentEvents
                .TakeLast(Math.Max(1, maxItems))
                .Select(ev => $"{ev.OccurredAtUtc:O} | {ev.ServiceName} | {ev.LogType} | {ev.Message}")
                .ToList();
        }

        private static int EstimateTokenCount(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            var roughWords = text
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Length;
            return Math.Max(1, roughWords);
        }

        private static KliveAgentBrainDecisionEnvelope? ParseDecisionEnvelope(string raw)
        {
            var json = ExtractJson(raw);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                var root = JObject.Parse(json);
                var envelope = new KliveAgentBrainDecisionEnvelope
                {
                    Summary = root.Value<string>("summary")
                        ?? root.Value<string>("reasoning")
                        ?? string.Empty,
                    ShouldAct = root.Value<bool?>("should_act")
                        ?? root.Value<bool?>("shouldAct")
                        ?? false,
                    Confidence = root.Value<double?>("confidence") ?? 0.0,
                    FinalResponse = root.Value<string>("final_response")
                        ?? root.Value<string>("finalResponse")
                        ?? string.Empty,
                    Actions = new List<KliveAgentBrainAction>()
                };

                var actions = root["actions"] as JArray;
                if (actions != null)
                {
                    foreach (var actionToken in actions)
                    {
                        if (actionToken is not JObject actionObj)
                        {
                            continue;
                        }

                        var action = new KliveAgentBrainAction
                        {
                            ActionType = actionObj.Value<string>("action_type")
                                ?? actionObj.Value<string>("actionType")
                                ?? actionObj.Value<string>("type")
                                ?? "none",
                            Reason = actionObj.Value<string>("reason") ?? string.Empty,
                            Message = actionObj.Value<string>("message") ?? string.Empty,
                            ScriptCode = actionObj.Value<string>("script_code")
                                ?? actionObj.Value<string>("scriptCode")
                                ?? actionObj.Value<string>("script")
                                ?? string.Empty,
                            MemoryType = actionObj.Value<string>("memory_type")
                                ?? actionObj.Value<string>("memoryType")
                                ?? "Note",
                            MemoryTitle = actionObj.Value<string>("memory_title")
                                ?? actionObj.Value<string>("memoryTitle")
                                ?? string.Empty,
                            MemoryContent = actionObj.Value<string>("memory_content")
                                ?? actionObj.Value<string>("memoryContent")
                                ?? string.Empty,
                            MemoryImportance = actionObj.Value<double?>("memory_importance")
                                ?? actionObj.Value<double?>("memoryImportance")
                                ?? 0.7
                        };

                        var tagsToken = actionObj["memory_tags"] ?? actionObj["memoryTags"] ?? actionObj["tags"];
                        if (tagsToken is JArray tagsArray)
                        {
                            action.MemoryTags = tagsArray
                                .Select(t => t?.ToString() ?? string.Empty)
                                .Where(t => !string.IsNullOrWhiteSpace(t))
                                .ToList();
                        }

                        envelope.Actions.Add(action);
                    }
                }

                return envelope;
            }
            catch
            {
                return null;
            }
        }

        private static string ExtractJson(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var trimmed = raw.Trim();
            if (trimmed.StartsWith("{") && trimmed.EndsWith("}"))
            {
                return trimmed;
            }

            var fenceStart = trimmed.IndexOf("```", StringComparison.Ordinal);
            if (fenceStart >= 0)
            {
                var nextFence = trimmed.IndexOf("```", fenceStart + 3, StringComparison.Ordinal);
                if (nextFence > fenceStart)
                {
                    var fencedBody = trimmed.Substring(fenceStart + 3, nextFence - (fenceStart + 3)).Trim();
                    var jsonStartInFence = fencedBody.IndexOf('{');
                    var jsonEndInFence = fencedBody.LastIndexOf('}');
                    if (jsonStartInFence >= 0 && jsonEndInFence > jsonStartInFence)
                    {
                        return fencedBody.Substring(jsonStartInFence, jsonEndInFence - jsonStartInFence + 1);
                    }
                }
            }

            var first = trimmed.IndexOf('{');
            var last = trimmed.LastIndexOf('}');
            if (first >= 0 && last > first)
            {
                return trimmed.Substring(first, last - first + 1);
            }

            return string.Empty;
        }

        private static string NormalizeActionType(string? actionType)
        {
            return (actionType ?? string.Empty)
                .Trim()
                .ToLowerInvariant()
                .Replace("-", "_")
                .Replace(" ", "_");
        }

        private static string BuildObservedEventFallbackMessage(KliveAgentObservedEvent observed, string reason)
        {
            var summary = $"{observed.OccurredAtUtc:O} | {observed.ServiceName} | {observed.LogType} | {observed.Message}";
            if (!string.IsNullOrWhiteSpace(observed.ExceptionMessage))
            {
                summary += $" | Exception={observed.ExceptionMessage}";
            }

            return $"[KliveAgent Fallback] {reason}\n{summary}";
        }
    }
}
