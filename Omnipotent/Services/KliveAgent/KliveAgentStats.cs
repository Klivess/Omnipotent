using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveAgent.Models;
using System.Collections.Concurrent;
using System.Globalization;

namespace Omnipotent.Services.KliveAgent
{
    public class KliveAgentStats
    {
        private readonly object _lock = new();
        private readonly object _saveScheduleLock = new();
        private readonly string persistencePath;
        private CancellationTokenSource? pendingSaveTokenSource;

        // Rough cost estimate (USD per 1,000,000 tokens). Real pricing is model/provider specific;
        // this is a deliberately approximate yardstick surfaced as "estimatedCostUsd".
        private const double PromptCostPerMillion = 3.0;
        private const double CompletionCostPerMillion = 15.0;

        public KliveAgentStats(string persistencePath)
        {
            this.persistencePath = persistencePath;
        }

        public long TotalMessages { get; private set; }
        public long TotalIterations { get; private set; }
        public long TotalPromptTokens { get; private set; }
        public long TotalCompletionTokens { get; private set; }
        public long TotalScriptsRun { get; private set; }
        public long TotalScriptFailures { get; private set; }
        public long TotalCapabilityCalls { get; private set; }
        public long TotalCapabilityFailures { get; private set; }
        public long TotalCapabilityConfirmationBlocks { get; private set; }
        public long TotalCapabilityDurationMs { get; private set; }
        public long TotalApiMessages { get; private set; }
        public long TotalDiscordMessages { get; private set; }
        public long TotalLatencyMs { get; private set; }
        public long MaxLatencyMs { get; private set; }
        public long TotalMemorySaves { get; private set; }
        public long TotalMemoryRecalls { get; private set; }

        private readonly ConcurrentDictionary<string, DayBucket> _days = new();
        private readonly ConcurrentDictionary<string, WeekBucket> _weeks = new();
        private readonly ConcurrentDictionary<string, MonthBucket> _months = new();
        private readonly ConcurrentDictionary<string, CapabilityBucket> _capabilities = new(StringComparer.OrdinalIgnoreCase);

        public async Task InitializeAsync()
        {
            try
            {
                if (!File.Exists(persistencePath))
                {
                    return;
                }

                string json = await File.ReadAllTextAsync(persistencePath);
                var snapshot = JsonConvert.DeserializeObject<StatsSnapshot>(json);
                if (snapshot == null)
                {
                    return;
                }

                lock (_lock)
                {
                    TotalMessages = snapshot.TotalMessages;
                    TotalIterations = snapshot.TotalIterations;
                    TotalPromptTokens = snapshot.TotalPromptTokens;
                    TotalCompletionTokens = snapshot.TotalCompletionTokens;
                    TotalScriptsRun = snapshot.TotalScriptsRun;
                    TotalScriptFailures = snapshot.TotalScriptFailures;
                    TotalCapabilityCalls = snapshot.TotalCapabilityCalls;
                    TotalCapabilityFailures = snapshot.TotalCapabilityFailures;
                    TotalCapabilityConfirmationBlocks = snapshot.TotalCapabilityConfirmationBlocks;
                    TotalCapabilityDurationMs = snapshot.TotalCapabilityDurationMs;
                    TotalApiMessages = snapshot.TotalApiMessages;
                    TotalDiscordMessages = snapshot.TotalDiscordMessages;
                    TotalLatencyMs = snapshot.TotalLatencyMs;
                    MaxLatencyMs = snapshot.MaxLatencyMs;
                    TotalMemorySaves = snapshot.TotalMemorySaves;
                    TotalMemoryRecalls = snapshot.TotalMemoryRecalls;
                }

                foreach (var bucket in snapshot.Days ?? new List<DayBucket>())
                {
                    if (!string.IsNullOrWhiteSpace(bucket.Date)) _days[bucket.Date] = bucket;
                }
                foreach (var bucket in snapshot.Weeks ?? new List<WeekBucket>())
                {
                    if (!string.IsNullOrWhiteSpace(bucket.Week)) _weeks[bucket.Week] = bucket;
                }
                foreach (var bucket in snapshot.Months ?? new List<MonthBucket>())
                {
                    if (!string.IsNullOrWhiteSpace(bucket.Month)) _months[bucket.Month] = bucket;
                }
                foreach (var capability in snapshot.Capabilities ?? new List<CapabilityBucket>())
                {
                    if (!string.IsNullOrWhiteSpace(capability.Name)) _capabilities[capability.Name] = capability;
                }
            }
            catch
            {
            }
        }

        // ── Period keys ──
        private static string DayKey(DateTime utc) => utc.ToString("yyyy-MM-dd");
        private static string MonthKey(DateTime utc) => utc.ToString("yyyy-MM");
        private static string WeekKey(DateTime utc) =>
            $"{ISOWeek.GetYear(utc):D4}-W{ISOWeek.GetWeekOfYear(utc):D2}";

        public void Record(int promptTokens, int completionTokens, int iterations, int scripts, int scriptFailures,
            long latencyMs = 0, AgentSourceChannel channel = AgentSourceChannel.API)
        {
            lock (_lock)
            {
                TotalMessages++;
                TotalIterations += iterations;
                TotalPromptTokens += promptTokens;
                TotalCompletionTokens += completionTokens;
                TotalScriptsRun += scripts;
                TotalScriptFailures += scriptFailures;
                TotalLatencyMs += latencyMs;
                if (latencyMs > MaxLatencyMs) MaxLatencyMs = latencyMs;
                if (channel == AgentSourceChannel.Discord) TotalDiscordMessages++; else TotalApiMessages++;
            }

            var now = DateTime.UtcNow;
            AccumulateMessage(_days.GetOrAdd(DayKey(now), k => new DayBucket { Date = k }),
                promptTokens, completionTokens, iterations, scripts, scriptFailures, latencyMs, channel);
            AccumulateMessage(_weeks.GetOrAdd(WeekKey(now), k => new WeekBucket { Week = k }),
                promptTokens, completionTokens, iterations, scripts, scriptFailures, latencyMs, channel);
            AccumulateMessage(_months.GetOrAdd(MonthKey(now), k => new MonthBucket { Month = k }),
                promptTokens, completionTokens, iterations, scripts, scriptFailures, latencyMs, channel);

            QueueSave();
        }

        private static void AccumulateMessage(MetricBucket b, int promptTokens, int completionTokens, int iterations,
            int scripts, int scriptFailures, long latencyMs, AgentSourceChannel channel)
        {
            lock (b)
            {
                b.Messages++;
                b.PromptTokens += promptTokens;
                b.CompletionTokens += completionTokens;
                b.Iterations += iterations;
                b.Scripts += scripts;
                b.ScriptFailures += scriptFailures;
                b.TotalLatencyMs += latencyMs;
                if (latencyMs > b.MaxLatencyMs) b.MaxLatencyMs = latencyMs;
                if (channel == AgentSourceChannel.Discord) b.DiscordMessages++; else b.ApiMessages++;
            }
        }

        public void RecordCapability(string capabilityName, bool success, bool confirmationBlocked = false, long durationMs = 0)
        {
            capabilityName = string.IsNullOrWhiteSpace(capabilityName) ? "unknown" : capabilityName.Trim();

            lock (_lock)
            {
                TotalCapabilityCalls++;
                if (!success) TotalCapabilityFailures++;
                if (confirmationBlocked) TotalCapabilityConfirmationBlocks++;
                TotalCapabilityDurationMs += durationMs;
            }

            var now = DateTime.UtcNow;
            AccumulateCapability(_days.GetOrAdd(DayKey(now), k => new DayBucket { Date = k }), success, confirmationBlocked);
            AccumulateCapability(_weeks.GetOrAdd(WeekKey(now), k => new WeekBucket { Week = k }), success, confirmationBlocked);
            AccumulateCapability(_months.GetOrAdd(MonthKey(now), k => new MonthBucket { Month = k }), success, confirmationBlocked);

            var capabilityBucket = _capabilities.GetOrAdd(capabilityName, name => new CapabilityBucket { Name = name });
            lock (capabilityBucket)
            {
                capabilityBucket.Executions++;
                if (!success) capabilityBucket.Failures++;
                if (confirmationBlocked) capabilityBucket.ConfirmationBlocks++;
                capabilityBucket.TotalDurationMs += durationMs;
                capabilityBucket.LastExecutedAt = DateTime.UtcNow;
            }

            QueueSave();
        }

        private static void AccumulateCapability(MetricBucket b, bool success, bool confirmationBlocked)
        {
            lock (b)
            {
                b.CapabilityCalls++;
                if (!success) b.CapabilityFailures++;
                if (confirmationBlocked) b.CapabilityConfirmationBlocks++;
            }
        }

        /// <summary>Records memory activity (saves / recalls) so it shows up in the time-series.
        /// Called from the memory tools the agent invokes inside scripts.</summary>
        public void RecordMemoryActivity(int saves, int recalls)
        {
            if (saves <= 0 && recalls <= 0) return;

            lock (_lock)
            {
                TotalMemorySaves += saves;
                TotalMemoryRecalls += recalls;
            }

            var now = DateTime.UtcNow;
            AccumulateMemory(_days.GetOrAdd(DayKey(now), k => new DayBucket { Date = k }), saves, recalls);
            AccumulateMemory(_weeks.GetOrAdd(WeekKey(now), k => new WeekBucket { Week = k }), saves, recalls);
            AccumulateMemory(_months.GetOrAdd(MonthKey(now), k => new MonthBucket { Month = k }), saves, recalls);

            QueueSave();
        }

        private static void AccumulateMemory(MetricBucket b, int saves, int recalls)
        {
            lock (b)
            {
                b.MemorySaves += saves;
                b.MemoryRecalls += recalls;
            }
        }

        private void QueueSave()
        {
            lock (_saveScheduleLock)
            {
                pendingSaveTokenSource?.Cancel();
                pendingSaveTokenSource?.Dispose();
                pendingSaveTokenSource = new CancellationTokenSource();
                _ = SaveAfterDelayAsync(pendingSaveTokenSource.Token);
            }
        }

        private async Task SaveAfterDelayAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(500, cancellationToken);
                await PersistAsync();
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task PersistAsync()
        {
            try
            {
                string? directory = Path.GetDirectoryName(persistencePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var snapshot = new StatsSnapshot
                {
                    TotalMessages = TotalMessages,
                    TotalIterations = TotalIterations,
                    TotalPromptTokens = TotalPromptTokens,
                    TotalCompletionTokens = TotalCompletionTokens,
                    TotalScriptsRun = TotalScriptsRun,
                    TotalScriptFailures = TotalScriptFailures,
                    TotalCapabilityCalls = TotalCapabilityCalls,
                    TotalCapabilityFailures = TotalCapabilityFailures,
                    TotalCapabilityConfirmationBlocks = TotalCapabilityConfirmationBlocks,
                    TotalCapabilityDurationMs = TotalCapabilityDurationMs,
                    TotalApiMessages = TotalApiMessages,
                    TotalDiscordMessages = TotalDiscordMessages,
                    TotalLatencyMs = TotalLatencyMs,
                    MaxLatencyMs = MaxLatencyMs,
                    TotalMemorySaves = TotalMemorySaves,
                    TotalMemoryRecalls = TotalMemoryRecalls,
                    Days = _days.Values.OrderBy(bucket => bucket.Date).ToList(),
                    Weeks = _weeks.Values.OrderBy(bucket => bucket.Week).ToList(),
                    Months = _months.Values.OrderBy(bucket => bucket.Month).ToList(),
                    Capabilities = _capabilities.Values
                        .OrderByDescending(capability => capability.Executions)
                        .ThenBy(capability => capability.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                };

                string tempPath = persistencePath + ".tmp";
                string json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);
                await File.WriteAllTextAsync(tempPath, json);

                if (File.Exists(persistencePath))
                {
                    File.Replace(tempPath, persistencePath, null);
                }
                else
                {
                    File.Move(tempPath, persistencePath);
                }
            }
            catch
            {
            }
        }

        private static double EstimateCost(long promptTokens, long completionTokens)
            => promptTokens / 1_000_000.0 * PromptCostPerMillion
             + completionTokens / 1_000_000.0 * CompletionCostPerMillion;

        /// <summary>Projects a time bucket into the JSON shape the UI charts consume. The period key
        /// name varies (date/week/month) so day/week/month series each carry their own label.</summary>
        private static Dictionary<string, object?> ProjectBucket(string keyName, string keyValue, MetricBucket b)
        {
            return new Dictionary<string, object?>
            {
                [keyName] = keyValue,
                ["messages"] = b.Messages,
                ["promptTokens"] = b.PromptTokens,
                ["completionTokens"] = b.CompletionTokens,
                ["totalTokens"] = b.PromptTokens + b.CompletionTokens,
                ["iterations"] = b.Iterations,
                ["scripts"] = b.Scripts,
                ["scriptFailures"] = b.ScriptFailures,
                ["scriptFailureRatePct"] = b.Scripts > 0 ? Math.Round((double)b.ScriptFailures / b.Scripts * 100.0, 1) : 0.0,
                ["capabilityCalls"] = b.CapabilityCalls,
                ["capabilityFailures"] = b.CapabilityFailures,
                ["capabilityConfirmationBlocks"] = b.CapabilityConfirmationBlocks,
                ["apiMessages"] = b.ApiMessages,
                ["discordMessages"] = b.DiscordMessages,
                ["avgLatencyMs"] = b.Messages > 0 ? Math.Round((double)b.TotalLatencyMs / b.Messages, 0) : 0.0,
                ["maxLatencyMs"] = b.MaxLatencyMs,
                ["memorySaves"] = b.MemorySaves,
                ["memoryRecalls"] = b.MemoryRecalls,
                ["estimatedCostUsd"] = Math.Round(EstimateCost(b.PromptTokens, b.CompletionTokens), 4)
            };
        }

        public object GetSummary()
        {
            long totalTokens = TotalPromptTokens + TotalCompletionTokens;
            double avgPromptPerMsg = TotalMessages > 0 ? (double)TotalPromptTokens / TotalMessages : 0;
            double avgCompletionPerMsg = TotalMessages > 0 ? (double)TotalCompletionTokens / TotalMessages : 0;
            double avgIterationsPerMsg = TotalMessages > 0 ? (double)TotalIterations / TotalMessages : 0;
            double avgLatencyMs = TotalMessages > 0 ? (double)TotalLatencyMs / TotalMessages : 0;
            double avgCapabilityDurationMs = TotalCapabilityCalls > 0 ? (double)TotalCapabilityDurationMs / TotalCapabilityCalls : 0;
            double scriptSuccessRate = TotalScriptsRun > 0 ? (double)(TotalScriptsRun - TotalScriptFailures) / TotalScriptsRun * 100 : 100;
            double capabilitySuccessRate = TotalCapabilityCalls > 0 ? (double)(TotalCapabilityCalls - TotalCapabilityFailures) / TotalCapabilityCalls * 100 : 100;

            var dailyHistory = _days.Values.OrderBy(d => d.Date)
                .Select(d => ProjectBucket("date", d.Date ?? "", d)).ToList();
            var weeklyHistory = _weeks.Values.OrderBy(w => w.Week)
                .Select(w => ProjectBucket("week", w.Week ?? "", w)).ToList();
            var monthlyHistory = _months.Values.OrderBy(m => m.Month)
                .Select(m => ProjectBucket("month", m.Month ?? "", m)).ToList();

            var topCapabilities = _capabilities.Values
                .OrderByDescending(capability => capability.Executions)
                .ThenBy(capability => capability.Name, StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .Select(capability => new
                {
                    name = capability.Name,
                    executions = capability.Executions,
                    failures = capability.Failures,
                    confirmationBlocks = capability.ConfirmationBlocks,
                    avgDurationMs = capability.Executions > 0 ? Math.Round((double)capability.TotalDurationMs / capability.Executions, 0) : 0.0,
                    lastExecutedAt = capability.LastExecutedAt
                })
                .ToList();

            var todayKey = DayKey(DateTime.UtcNow);
            _days.TryGetValue(todayKey, out var today);

            return new
            {
                lifetime = new
                {
                    messages = TotalMessages,
                    promptTokens = TotalPromptTokens,
                    completionTokens = TotalCompletionTokens,
                    totalTokens,
                    iterations = TotalIterations,
                    scripts = TotalScriptsRun,
                    scriptFailures = TotalScriptFailures,
                    capabilityCalls = TotalCapabilityCalls,
                    capabilityFailures = TotalCapabilityFailures,
                    capabilityConfirmationBlocks = TotalCapabilityConfirmationBlocks,
                    apiMessages = TotalApiMessages,
                    discordMessages = TotalDiscordMessages,
                    memorySaves = TotalMemorySaves,
                    memoryRecalls = TotalMemoryRecalls,
                    avgPromptTokensPerMessage = Math.Round(avgPromptPerMsg, 1),
                    avgCompletionTokensPerMessage = Math.Round(avgCompletionPerMsg, 1),
                    avgIterationsPerMessage = Math.Round(avgIterationsPerMsg, 2),
                    avgLatencyMs = Math.Round(avgLatencyMs, 0),
                    maxLatencyMs = MaxLatencyMs,
                    avgCapabilityDurationMs = Math.Round(avgCapabilityDurationMs, 0),
                    scriptSuccessRatePct = Math.Round(scriptSuccessRate, 1),
                    capabilitySuccessRatePct = Math.Round(capabilitySuccessRate, 1),
                    estimatedCostUsd = Math.Round(EstimateCost(TotalPromptTokens, TotalCompletionTokens), 4)
                },
                today = today == null ? null : ProjectBucket("date", today.Date ?? todayKey, today),
                historyWindow = new
                {
                    firstDay = dailyHistory.FirstOrDefault()?.GetValueOrDefault("date"),
                    lastDay = dailyHistory.LastOrDefault()?.GetValueOrDefault("date"),
                    totalDays = dailyHistory.Count,
                    totalWeeks = weeklyHistory.Count,
                    totalMonths = monthlyHistory.Count
                },
                dailyHistory,
                weeklyHistory,
                monthlyHistory,
                topCapabilities
            };
        }

        private sealed class StatsSnapshot
        {
            public long TotalMessages { get; set; }
            public long TotalIterations { get; set; }
            public long TotalPromptTokens { get; set; }
            public long TotalCompletionTokens { get; set; }
            public long TotalScriptsRun { get; set; }
            public long TotalScriptFailures { get; set; }
            public long TotalCapabilityCalls { get; set; }
            public long TotalCapabilityFailures { get; set; }
            public long TotalCapabilityConfirmationBlocks { get; set; }
            public long TotalCapabilityDurationMs { get; set; }
            public long TotalApiMessages { get; set; }
            public long TotalDiscordMessages { get; set; }
            public long TotalLatencyMs { get; set; }
            public long MaxLatencyMs { get; set; }
            public long TotalMemorySaves { get; set; }
            public long TotalMemoryRecalls { get; set; }
            public List<DayBucket> Days { get; set; } = new();
            public List<WeekBucket> Weeks { get; set; } = new();
            public List<MonthBucket> Months { get; set; } = new();
            public List<CapabilityBucket> Capabilities { get; set; } = new();
        }

        /// <summary>Shared metric fields for every time bucket (day/week/month). New fields default to
        /// 0 so older persisted snapshots deserialize cleanly.</summary>
        public abstract class MetricBucket
        {
            public long Messages { get; set; }
            public long PromptTokens { get; set; }
            public long CompletionTokens { get; set; }
            public long Iterations { get; set; }
            public long Scripts { get; set; }
            public long ScriptFailures { get; set; }
            public long CapabilityCalls { get; set; }
            public long CapabilityFailures { get; set; }
            public long CapabilityConfirmationBlocks { get; set; }
            public long ApiMessages { get; set; }
            public long DiscordMessages { get; set; }
            public long TotalLatencyMs { get; set; }
            public long MaxLatencyMs { get; set; }
            public long MemorySaves { get; set; }
            public long MemoryRecalls { get; set; }
        }

        public class DayBucket : MetricBucket
        {
            public string? Date { get; set; }
        }

        public class WeekBucket : MetricBucket
        {
            public string? Week { get; set; }
        }

        public class MonthBucket : MetricBucket
        {
            public string? Month { get; set; }
        }

        public class CapabilityBucket
        {
            public string Name { get; set; } = string.Empty;
            public long Executions { get; set; }
            public long Failures { get; set; }
            public long ConfirmationBlocks { get; set; }
            public long TotalDurationMs { get; set; }
            public DateTime? LastExecutedAt { get; set; }
        }
    }
}
