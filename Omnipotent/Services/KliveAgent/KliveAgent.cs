using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Logging;
using Omnipotent.Profiles;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAPI;
using Omnipotent.Services.KliveBot_Discord;
using System.Collections.Concurrent;
using System.Threading.Channels;
using static Omnipotent.Profiles.KMProfileManager;
using UserRequest = Omnipotent.Services.KliveAPI.KliveAPI.UserRequest;

namespace Omnipotent.Services.KliveAgent
{
    public class KliveAgent : OmniService
    {
        private readonly Channel<KliveAgentObservedEvent> _eventChannel = Channel.CreateUnbounded<KliveAgentObservedEvent>();
        private readonly ConcurrentDictionary<string, DateTime> _cooldownIndex = new(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<DateTime> _autonomyActionWindow = new();
        private readonly object _autonomyLock = new();

        private CancellationTokenSource _shutdownToken = new();
        private OmniLogging? _logger;
        private KliveAgentMemory _memory = null!;
        private KliveAgentScripting _scripting = null!;

        private const int MaxAutonomousActionsPerMinute = 6;
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

                await EnsureDefaultRulesAsync();
                SubscribeToLogFeed();
                _ = Task.Run(() => ProcessEventsAsync(_shutdownToken.Token));

                await SetupRoutesAsync();
                await ServiceLog("KliveAgent online. Memory, scripting, and event autonomy are active.");
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

            _eventChannel.Writer.TryWrite(KliveAgentObservedEvent.FromLoggedMessage(message));
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

            var acted = false;
            foreach (var rule in matchingRules)
            {
                var cooldownKey = $"rule:{rule.Id}:{observed.Fingerprint}";
                var cooldown = TimeSpan.FromSeconds(Math.Max(5, rule.CooldownSeconds));
                if (IsInCooldown(cooldownKey, cooldown))
                {
                    continue;
                }

                if (!TryClaimAutonomousActionSlot())
                {
                    await ServiceLog("KliveAgent skipped rule action due to autonomy rate limit.", false);
                    break;
                }

                rule.LastTriggeredAtUtc = DateTime.UtcNow;
                await _memory.UpsertRuleAsync(rule);

                if (rule.NotifyKlives)
                {
                    await NotifyKlivesAsync(observed, rule.Name);
                }

                if (!string.IsNullOrWhiteSpace(rule.ScriptCode))
                {
                    await _scripting.ExecuteScriptAsync(rule.ScriptCode, $"event-rule:{rule.Name}", observed, runInBackground: true);
                }

                acted = true;
            }

            if (!acted && ShouldEscalateByDefault(observed))
            {
                var cooldownKey = $"default:{observed.Fingerprint}";
                if (!IsInCooldown(cooldownKey, DefaultEventCooldown) && TryClaimAutonomousActionSlot())
                {
                    await NotifyKlivesAsync(observed, "default-critical-escalation");
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

        private static int ParseInt(string? value, int fallback, int min, int max)
        {
            if (!int.TryParse(value, out var parsed))
            {
                return fallback;
            }

            return Math.Clamp(parsed, min, max);
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
