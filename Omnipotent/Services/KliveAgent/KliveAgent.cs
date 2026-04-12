using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Logging;
using Omnipotent.Profiles;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAPI;
using Omnipotent.Services.KliveBot_Discord;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using static Omnipotent.Profiles.KMProfileManager;
using UserRequest = Omnipotent.Services.KliveAPI.KliveAPI.UserRequest;

namespace Omnipotent.Services.KliveAgent
{
    public class KliveAgent : OmniService
    {
        private readonly Channel<KliveAgentObservedEvent> _eventChannel = Channel.CreateUnbounded<KliveAgentObservedEvent>();
        private readonly ConcurrentQueue<KliveAgentObservedEvent> _recentEvents = new();
        private readonly ConcurrentQueue<KliveAgentBrainExecutionResult> _decisionHistory = new();
        private readonly ConcurrentDictionary<string, DateTime> _cooldownIndex = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Queue<DateTime>> _manualActionWindows = new(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<DateTime> _autonomyActionWindow = new();
        private readonly object _autonomyLock = new();
        private readonly object _manualActionLock = new();

        private CancellationTokenSource _shutdownToken = new();
        private OmniLogging? _logger;
        private KliveAgentMemory _memory = null!;
        private KliveAgentScripting _scripting = null!;
        private KliveAgentBrain _brain = null!;

        private const int MaxAutonomousActionsPerMinute = 6;
        private const int MaxManualActionsPerMinutePerProfile = 10;
        private const int MaxRecentEvents = 300;
        private const int MaxDecisionHistory = 150;
        private static readonly TimeSpan DefaultEventCooldown = TimeSpan.FromMinutes(2);

        public KliveAgent()
        {
            name = "KliveAgent";
            threadAnteriority = ThreadAnteriority.High;
        }

        protected override async void ServiceMain()
        {
            try
            {
                ServiceQuitRequest += OnServiceQuitRequested;

                var memoryDirectory = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentMemoryDirectory);
                _memory = new KliveAgentMemory(memoryDirectory);
                await _memory.InitializeAsync();

                _scripting = new KliveAgentScripting(this);
                _brain = new KliveAgentBrain(this);

                await EnsureDefaultRulesAsync();
                SubscribeToLogFeed();
                _ = Task.Run(() => ProcessEventsAsync(_shutdownToken.Token));

                await SetupRoutesAsync();
                await ServiceLog("KliveAgent online. Memory, LLM brain, scripting, and event autonomy are active.");
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "KliveAgent failed to start");
            }
        }

        internal async Task<T?> ResolveServiceAsync<T>() where T : OmniService
        {
            var services = await GetServicesByType<T>();
            if (services == null || services.Length == 0)
            {
                return null;
            }

            return services[0] as T;
        }

        public void LogFromScript(string message)
        {
            _ = ServiceLog($"[Script] {message}", false);
        }

        public async Task SaveMemoryFromScript(KliveAgentMemoryRecord record)
        {
            await _memory.SaveMemoryAsync(record);
        }

        public List<KliveAgentMemorySearchResult> SearchMemory(string query, int maxCount = 6)
        {
            return _memory.Search(query, maxCount);
        }

        public List<KliveAgentObservedEvent> GetRecentEventsSnapshot(int maxCount = 30)
        {
            return _recentEvents
                .ToArray()
                .TakeLast(Math.Max(1, maxCount))
                .ToList();
        }

        public List<KliveAgentBrainExecutionResult> GetRecentDecisions(int maxCount = 20)
        {
            return _decisionHistory
                .ToArray()
                .TakeLast(Math.Max(1, maxCount))
                .Reverse()
                .ToList();
        }

        internal async Task<KliveAgentScriptRunRecord> ExecuteScriptFromBrainAsync(string scriptCode, string trigger, KliveAgentObservedEvent? observed)
        {
            return await _scripting.ExecuteScriptAsync(scriptCode, trigger, observed, runInBackground: true);
        }

        internal async Task SendMessageToKlivesFromBrainAsync(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            var discord = await ResolveServiceAsync<KliveBotDiscord>();
            if (discord == null)
            {
                return;
            }

            await discord.SendMessageToKlives(message);
        }

        internal async Task<KliveAgentMemoryRecord> SaveMemoryFromBrainAsync(KliveAgentMemoryRecord record)
        {
            return await _memory.SaveMemoryAsync(record);
        }

        internal void RecordDecision(KliveAgentBrainExecutionResult result)
        {
            _decisionHistory.Enqueue(result);
            while (_decisionHistory.Count > MaxDecisionHistory && _decisionHistory.TryDequeue(out _)) { }
        }

        public async Task OnScriptRunUpdated(KliveAgentScriptRunRecord runRecord)
        {
            await _memory.SaveScriptRunAsync(runRecord);
        }

        public async Task OnScriptRunCompleted(KliveAgentScriptRunRecord runRecord)
        {
            await _memory.SaveScriptRunAsync(runRecord);

            if (runRecord.Status.Equals("error", StringComparison.OrdinalIgnoreCase))
            {
                await _memory.SaveMemoryAsync(new KliveAgentMemoryRecord
                {
                    Type = KliveAgentMemoryType.Event,
                    Title = "KliveAgent Script Failure",
                    Content = runRecord.Error,
                    Tags = new List<string> { "script", "error", "kliveagent" },
                    Source = "kliveagent",
                    Importance = 0.85
                });
            }
        }

        public async Task LogAutonomousError(string context, Exception ex)
        {
            await ServiceLogError(ex, context, false);
        }

        private void OnServiceQuitRequested()
        {
            try
            {
                _shutdownToken.Cancel();
            }
            catch { }

            try
            {
                if (_logger != null)
                {
                    _logger.OnLogMessage -= Logger_OnLogMessage;
                }
            }
            catch { }

            try
            {
                _memory?.PersistAllAsync().GetAwaiter().GetResult();
            }
            catch { }
        }

        private void SubscribeToLogFeed()
        {
            if (_logger != null)
            {
                return;
            }

            ref var loggerRef = ref GetLoggerService();
            _logger = loggerRef;
            _logger.OnLogMessage += Logger_OnLogMessage;
        }

        private void Logger_OnLogMessage(object? sender, OmniLogging.LoggedMessage message)
        {
            if (_shutdownToken.IsCancellationRequested)
            {
                return;
            }

            if (string.Equals(message.serviceName, name, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var observed = KliveAgentObservedEvent.FromLoggedMessage(message);
            _recentEvents.Enqueue(observed);
            while (_recentEvents.Count > MaxRecentEvents && _recentEvents.TryDequeue(out _)) { }
            _eventChannel.Writer.TryWrite(observed);
        }

        private async Task ProcessEventsAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                KliveAgentObservedEvent observed;
                try
                {
                    observed = await _eventChannel.Reader.ReadAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    continue;
                }

                try
                {
                    await HandleObservedEventAsync(observed);
                }
                catch (Exception ex)
                {
                    await ServiceLogError(ex, "Failed to process observed event", false);
                }
            }
        }

        private async Task HandleObservedEventAsync(KliveAgentObservedEvent observed)
        {
            if (observed.LogType >= OmniLogging.LogType.Error)
            {
                await _memory.SaveMemoryAsync(new KliveAgentMemoryRecord
                {
                    Type = KliveAgentMemoryType.Event,
                    Title = $"{observed.ServiceName} {observed.LogType}",
                    Content = BuildObservedEventSummary(observed),
                    Tags = new List<string> { "log", "event", observed.LogType.ToString().ToLowerInvariant(), observed.ServiceName.ToLowerInvariant() },
                    Source = "log-feed",
                    Importance = observed.LogType == OmniLogging.LogType.Error ? 0.9 : 0.6
                });
            }

            var matchingRules = _memory.GetRules()
                .Where(r => r.Matches(observed))
                .OrderByDescending(r => r.MinimumLogType)
                .ToList();

            var shouldAskBrain = matchingRules.Any() || ShouldEscalateByDefault(observed);
            if (!shouldAskBrain)
            {
                return;
            }

            var cooldownSeconds = matchingRules.Any()
                ? matchingRules.Min(r => Math.Max(5, r.CooldownSeconds))
                : (int)DefaultEventCooldown.TotalSeconds;
            var cooldownKey = $"brain:{observed.Fingerprint}";

            if (IsInCooldown(cooldownKey, TimeSpan.FromSeconds(cooldownSeconds)))
            {
                return;
            }

            if (!TryClaimAutonomousActionSlot())
            {
                await ServiceLog("KliveAgent skipped brain analysis due to autonomy rate limit.", false);
                return;
            }

            foreach (var rule in matchingRules)
            {
                rule.LastTriggeredAtUtc = DateTime.UtcNow;
                await _memory.UpsertRuleAsync(rule);
            }

            var recentEvents = GetRecentEventsSnapshot(25);
            var decisionResult = await _brain.DecideForEventAsync(observed, matchingRules, recentEvents, _shutdownToken.Token);
            RecordDecision(decisionResult);

            if (decisionResult.UsedFallback && decisionResult.Decision.Actions.Any())
            {
                foreach (var action in decisionResult.Decision.Actions)
                {
                    if (string.Equals(action.ActionType, "notify_klives", StringComparison.OrdinalIgnoreCase))
                    {
                        await SendMessageToKlivesFromBrainAsync(action.Message);
                    }
                }
            }
        }

        private static bool ShouldEscalateByDefault(KliveAgentObservedEvent observed)
        {
            if (observed.LogType != OmniLogging.LogType.Error)
            {
                return false;
            }

            var message = $"{observed.Message} {observed.ExceptionType} {observed.ExceptionMessage}".ToLowerInvariant();
            return message.Contains("unhandled")
                || message.Contains("fatal")
                || message.Contains("crash")
                || message.Contains("exception")
                || message.Contains("failed");
        }

        private bool IsInCooldown(string key, TimeSpan cooldown)
        {
            var now = DateTime.UtcNow;
            if (_cooldownIndex.TryGetValue(key, out var blockedUntil) && blockedUntil > now)
            {
                return true;
            }

            _cooldownIndex[key] = now.Add(cooldown);
            return false;
        }

        private bool TryClaimAutonomousActionSlot()
        {
            lock (_autonomyLock)
            {
                var now = DateTime.UtcNow;
                while (_autonomyActionWindow.Count > 0 && (now - _autonomyActionWindow.Peek()).TotalSeconds > 60)
                {
                    _autonomyActionWindow.Dequeue();
                }

                if (_autonomyActionWindow.Count >= MaxAutonomousActionsPerMinute)
                {
                    return false;
                }

                _autonomyActionWindow.Enqueue(now);
                return true;
            }
        }

        private bool TryClaimManualActionSlot(string profileScope)
        {
            var scope = string.IsNullOrWhiteSpace(profileScope) ? "anonymous" : profileScope;

            lock (_manualActionLock)
            {
                var window = _manualActionWindows.GetOrAdd(scope, _ => new Queue<DateTime>());
                var now = DateTime.UtcNow;

                while (window.Count > 0 && (now - window.Peek()).TotalSeconds > 60)
                {
                    window.Dequeue();
                }

                if (window.Count >= MaxManualActionsPerMinutePerProfile)
                {
                    return false;
                }

                window.Enqueue(now);
                return true;
            }
        }

        private async Task NotifyKlivesAsync(KliveAgentObservedEvent observed, string reason)
        {
            var discord = await ResolveServiceAsync<KliveBotDiscord>();
            if (discord == null)
            {
                return;
            }

            var summary = BuildObservedEventSummary(observed);
            summary = summary.Length > 1200 ? summary[..1200] + "..." : summary;
            await discord.SendMessageToKlives($"[KliveAgent] {reason}\n{summary}");
        }

        private static string BuildObservedEventSummary(KliveAgentObservedEvent observed)
        {
            var header = $"{observed.OccurredAtUtc:O} | {observed.ServiceName} | {observed.LogType}";
            var body = observed.Message;
            if (!string.IsNullOrWhiteSpace(observed.ExceptionType) || !string.IsNullOrWhiteSpace(observed.ExceptionMessage))
            {
                body += $"\nException: {observed.ExceptionType} - {observed.ExceptionMessage}";
            }

            return $"{header}\n{body}".Trim();
        }

        private async Task EnsureDefaultRulesAsync()
        {
            var rules = _memory.GetRules();
            if (rules.Any())
            {
                return;
            }

            await _memory.UpsertRuleAsync(new KliveAgentEventRule
            {
                Name = "Critical Error Escalation",
                Enabled = true,
                MinimumLogType = OmniLogging.LogType.Error,
                MessageContainsAny = new List<string> { "unhandled", "fatal", "crash", "exception", "failed" },
                NotifyKlives = true,
                CooldownSeconds = 120,
                ScriptCode = null
            });
        }

        private async Task SetupRoutesAsync()
        {
            await CreateAPIRoute("/kliveagent/memory/recent", HandleRecentMemories, HttpMethod.Get, KMPermissions.Admin);
            await CreateAPIRoute("/kliveagent/memory/search", HandleSearchMemories, HttpMethod.Get, KMPermissions.Admin);
            await CreateAPIRoute("/kliveagent/memory/save", HandleSaveMemory, HttpMethod.Post, KMPermissions.Admin);

            await CreateAPIRoute("/kliveagent/events/rules", HandleGetRules, HttpMethod.Get, KMPermissions.Admin);
            await CreateAPIRoute("/kliveagent/events/rules/upsert", HandleUpsertRule, HttpMethod.Post, KMPermissions.Admin);
            await CreateAPIRoute("/kliveagent/events/rules/delete", HandleDeleteRule, HttpMethod.Post, KMPermissions.Admin);

            await CreateAPIRoute("/kliveagent/scripts/run", HandleRunScript, HttpMethod.Post, KMPermissions.Admin);
            await CreateAPIRoute("/kliveagent/scripts/runs", HandleGetScriptRuns, HttpMethod.Get, KMPermissions.Admin);
            await CreateAPIRoute("/kliveagent/scripts/running", HandleGetRunningScripts, HttpMethod.Get, KMPermissions.Admin);
            await CreateAPIRoute("/kliveagent/scripts/cancel", HandleCancelScript, HttpMethod.Post, KMPermissions.Admin);

            await CreateAPIRoute("/kliveagent/brain/execute", HandleBrainExecute, HttpMethod.Post, KMPermissions.Admin);
            await CreateAPIRoute("/kliveagent/brain/execute-stream", HandleBrainExecuteStream, HttpMethod.Post, KMPermissions.Admin);
            await CreateAPIRoute("/kliveagent/brain/decisions", HandleBrainDecisions, HttpMethod.Get, KMPermissions.Admin);
        }

        private async Task HandleRecentMemories(UserRequest req)
        {
            int count = ParseInt(req.userParameters["count"], 30, 1, 200);
            var data = _memory.GetRecentMemories(count);
            await req.ReturnResponse(JsonConvert.SerializeObject(data), "application/json");
        }

        private async Task HandleSearchMemories(UserRequest req)
        {
            var query = req.userParameters["query"] ?? req.userParameters["q"] ?? string.Empty;
            int count = ParseInt(req.userParameters["count"], 8, 1, 50);
            var data = _memory.Search(query, count);
            await req.ReturnResponse(JsonConvert.SerializeObject(data), "application/json");
        }

        private async Task HandleSaveMemory(UserRequest req)
        {
            var model = TryParseJson<KliveAgentSaveMemoryRequest>(req.userMessageContent);
            if (model == null || string.IsNullOrWhiteSpace(model.Content))
            {
                await req.ReturnResponse(JsonConvert.SerializeObject(new { error = "Invalid payload. Expected content." }), code: System.Net.HttpStatusCode.BadRequest);
                return;
            }

            if (!Enum.TryParse<KliveAgentMemoryType>(model.Type, true, out var memoryType))
            {
                memoryType = KliveAgentMemoryType.Note;
            }

            var saved = await _memory.SaveMemoryAsync(new KliveAgentMemoryRecord
            {
                Type = memoryType,
                Title = model.Title,
                Content = model.Content,
                Tags = model.Tags ?? new List<string>(),
                Source = string.IsNullOrWhiteSpace(model.Source) ? "api" : model.Source,
                Importance = Math.Clamp(model.Importance, 0, 1)
            });

            await req.ReturnResponse(JsonConvert.SerializeObject(saved), "application/json");
        }

        private async Task HandleGetRules(UserRequest req)
        {
            await req.ReturnResponse(JsonConvert.SerializeObject(_memory.GetRules()), "application/json");
        }

        private async Task HandleUpsertRule(UserRequest req)
        {
            var model = TryParseJson<KliveAgentUpsertRuleRequest>(req.userMessageContent);
            if (model == null || string.IsNullOrWhiteSpace(model.Name))
            {
                await req.ReturnResponse(JsonConvert.SerializeObject(new { error = "Invalid payload. Expected rule name." }), code: System.Net.HttpStatusCode.BadRequest);
                return;
            }

            var rule = new KliveAgentEventRule
            {
                Id = string.IsNullOrWhiteSpace(model.Id) ? Guid.NewGuid().ToString("N") : model.Id,
                Name = model.Name,
                Enabled = model.Enabled,
                ServiceNameContains = model.ServiceNameContains ?? string.Empty,
                MessageContainsAny = model.MessageContainsAny ?? new List<string>(),
                MinimumLogType = model.MinimumLogType,
                NotifyKlives = model.NotifyKlives,
                ScriptCode = model.ScriptCode,
                CooldownSeconds = Math.Max(5, model.CooldownSeconds),
                CreatedAtUtc = DateTime.UtcNow
            };

            var saved = await _memory.UpsertRuleAsync(rule);
            await req.ReturnResponse(JsonConvert.SerializeObject(saved), "application/json");
        }

        private async Task HandleDeleteRule(UserRequest req)
        {
            var id = req.userParameters["id"];
            if (string.IsNullOrWhiteSpace(id))
            {
                await req.ReturnResponse(JsonConvert.SerializeObject(new { error = "Missing rule id." }), code: System.Net.HttpStatusCode.BadRequest);
                return;
            }

            var deleted = await _memory.DeleteRuleAsync(id);
            await req.ReturnResponse(JsonConvert.SerializeObject(new { deleted }), "application/json");
        }

        private async Task HandleRunScript(UserRequest req)
        {
            var model = TryParseJson<KliveAgentRunScriptRequest>(req.userMessageContent);
            if (model == null || string.IsNullOrWhiteSpace(model.ScriptCode))
            {
                await req.ReturnResponse(JsonConvert.SerializeObject(new { error = "Invalid payload. Expected scriptCode." }), code: System.Net.HttpStatusCode.BadRequest);
                return;
            }

            var run = await _scripting.ExecuteScriptAsync(model.ScriptCode, model.Trigger, triggerEvent: null, runInBackground: true);
            await req.ReturnResponse(JsonConvert.SerializeObject(run), "application/json");
        }

        private async Task HandleGetScriptRuns(UserRequest req)
        {
            int count = ParseInt(req.userParameters["count"], 20, 1, 200);
            await req.ReturnResponse(JsonConvert.SerializeObject(_memory.GetRecentScriptRuns(count)), "application/json");
        }

        private async Task HandleGetRunningScripts(UserRequest req)
        {
            var running = _scripting.GetRunningScriptIds();
            await req.ReturnResponse(JsonConvert.SerializeObject(new { count = running.Count, runs = running }), "application/json");
        }

        private async Task HandleCancelScript(UserRequest req)
        {
            var id = req.userParameters["id"];
            if (string.IsNullOrWhiteSpace(id))
            {
                await req.ReturnResponse(JsonConvert.SerializeObject(new { error = "Missing script run id." }), code: System.Net.HttpStatusCode.BadRequest);
                return;
            }

            var cancelled = _scripting.TryCancelScript(id);
            await req.ReturnResponse(JsonConvert.SerializeObject(new { cancelled }), "application/json");
        }

        private async Task HandleBrainExecute(UserRequest req)
        {
            var model = TryParseJson<KliveAgentBrainExecutionRequest>(req.userMessageContent);
            if (model == null || string.IsNullOrWhiteSpace(model.Goal))
            {
                await req.ReturnResponse(
                    JsonConvert.SerializeObject(new { error = "Invalid payload. Expected goal." }),
                    code: System.Net.HttpStatusCode.BadRequest);
                return;
            }

            model.RequestingProfileScope = BuildProfileScope(req);
            if (string.IsNullOrWhiteSpace(model.ConversationId))
            {
                model.ConversationId = "default";
            }

            if (!TryClaimManualActionSlot(model.RequestingProfileScope))
            {
                await req.ReturnResponse(
                    JsonConvert.SerializeObject(new { error = "KliveAgent request rate limit reached for this profile. Try again shortly." }),
                    code: System.Net.HttpStatusCode.TooManyRequests);
                return;
            }

            var result = await _brain.DecideForTaskAsync(model, _shutdownToken.Token);
            RecordDecision(result);

            if (model.NotifyKlivesOnCompletion && !string.IsNullOrWhiteSpace(result.FinalResponse))
            {
                await SendMessageToKlivesFromBrainAsync("[KliveAgent Task Result] " + result.FinalResponse);
            }

            await req.ReturnResponse(JsonConvert.SerializeObject(result), "application/json");
        }

        private async Task HandleBrainExecuteStream(UserRequest req)
        {
            var response = req.context.Response;
            response.ContentType = "text/event-stream";
            response.SendChunked = true;
            response.KeepAlive = true;
            response.Headers.Set("Cache-Control", "no-cache, no-transform");
            response.Headers.Set("X-Accel-Buffering", "no");
            response.Headers.Set("Access-Control-Allow-Origin", "*");
            response.Headers.Set("Access-Control-Expose-Headers", "*");

            await using var writer = new StreamWriter(response.OutputStream, Encoding.UTF8, 1024, leaveOpen: false);

            try
            {
                var model = TryParseJson<KliveAgentBrainExecutionRequest>(req.userMessageContent);
                if (model == null || string.IsNullOrWhiteSpace(model.Goal))
                {
                    await WriteSseEventAsync(writer, "error", "Invalid payload. Expected goal.");
                    await WriteSseEventAsync(writer, "done", "error");
                    return;
                }

                model.RequestingProfileScope = BuildProfileScope(req);
                if (string.IsNullOrWhiteSpace(model.ConversationId))
                {
                    model.ConversationId = "default";
                }

                if (!TryClaimManualActionSlot(model.RequestingProfileScope))
                {
                    await WriteSseEventAsync(writer, "error", "KliveAgent request rate limit reached for this profile. Try again shortly.");
                    await WriteSseEventAsync(writer, "done", "rate_limited");
                    return;
                }

                await WriteSseEventAsync(writer, "status", "Gathering context and consulting LLM brain...");

                var result = await _brain.DecideForTaskAsync(model, _shutdownToken.Token);
                RecordDecision(result);

                var finalText = string.IsNullOrWhiteSpace(result.FinalResponse)
                    ? (string.IsNullOrWhiteSpace(result.Summary) ? "No response generated." : result.Summary)
                    : result.FinalResponse;

                await WriteSseEventAsync(writer, "meta", JsonConvert.SerializeObject(new
                {
                    result.DecisionId,
                    result.MissionType,
                    result.LlmSessionId,
                    result.ApproxOutputTokens,
                    usedFallback = result.UsedFallback,
                    contextMemoryCount = result.ContextUsed?.MemoryEntries?.Count ?? 0,
                    contextEventCount = result.ContextUsed?.RecentEventEntries?.Count ?? 0,
                    contextRuleCount = result.ContextUsed?.MatchedRuleEntries?.Count ?? 0
                }));

                foreach (var token in SplitForStreaming(finalText))
                {
                    await WriteSseEventAsync(writer, "token", JsonConvert.SerializeObject(new { token }));
                }

                if (model.NotifyKlivesOnCompletion && !string.IsNullOrWhiteSpace(result.FinalResponse))
                {
                    await SendMessageToKlivesFromBrainAsync("[KliveAgent Task Result] " + result.FinalResponse);
                }

                await WriteSseEventAsync(writer, "result", JsonConvert.SerializeObject(result));
                await WriteSseEventAsync(writer, "done", "ok");
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "KliveAgent execute-stream failed", false);
                await WriteSseEventAsync(writer, "error", ex.Message);
                await WriteSseEventAsync(writer, "done", "error");
            }
        }

        private async Task HandleBrainDecisions(UserRequest req)
        {
            int count = ParseInt(req.userParameters["count"], 20, 1, 100);
            var decisions = GetRecentDecisions(count);
            await req.ReturnResponse(JsonConvert.SerializeObject(decisions), "application/json");
        }

        private static int ParseInt(string? value, int fallback, int min, int max)
        {
            if (!int.TryParse(value, out var parsed))
            {
                return fallback;
            }

            return Math.Clamp(parsed, min, max);
        }

        private static string BuildProfileScope(UserRequest req)
        {
            if (req.user == null)
            {
                return "anonymous";
            }

            var userId = SanitizeScopeSegment(req.user.UserID, "nouid");
            var name = SanitizeScopeSegment(req.user.Name, "unknown");
            return $"{name}-{userId}";
        }

        private static string SanitizeScopeSegment(string? value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            var chars = value
                .Trim()
                .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-')
                .ToArray();

            var normalized = new string(chars);
            while (normalized.Contains("--", StringComparison.Ordinal))
            {
                normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
            }

            normalized = normalized.Trim('-');
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return fallback;
            }

            return normalized.Length > 32 ? normalized[..32] : normalized;
        }

        private static IEnumerable<string> SplitForStreaming(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                yield break;
            }

            var current = new StringBuilder();
            foreach (var ch in text)
            {
                current.Append(ch);
                if (char.IsWhiteSpace(ch) || current.Length >= 18)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }

            if (current.Length > 0)
            {
                yield return current.ToString();
            }
        }

        private static async Task WriteSseEventAsync(StreamWriter writer, string eventName, string data)
        {
            await writer.WriteAsync("event: ");
            await writer.WriteLineAsync(eventName);

            var lines = (data ?? string.Empty).Replace("\r", string.Empty).Split('\n');
            foreach (var line in lines)
            {
                await writer.WriteAsync("data: ");
                await writer.WriteLineAsync(line);
            }

            await writer.WriteLineAsync();
            await writer.FlushAsync();
        }

        private static T? TryParseJson<T>(string? json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JsonConvert.DeserializeObject<T>(json);
            }
            catch
            {
                return null;
            }
        }
    }
}
