using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using System.Collections.Concurrent;

namespace Omnipotent.Services.KliveAgent
{
    public class KliveAgentStats
    {
        private readonly object _lock = new();
        private readonly object _saveScheduleLock = new();
        private readonly string persistencePath;
        private CancellationTokenSource? pendingSaveTokenSource;

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

        private readonly ConcurrentDictionary<string, DayBucket> _days = new();
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
                }

                foreach (var bucket in snapshot.Days ?? new List<DayBucket>())
                {
                    if (string.IsNullOrWhiteSpace(bucket.Date))
                    {
                        continue;
                    }

                    _days[bucket.Date] = bucket;
                }

                foreach (var capability in snapshot.Capabilities ?? new List<CapabilityBucket>())
                {
                    if (string.IsNullOrWhiteSpace(capability.Name))
                    {
                        continue;
                    }

                    _capabilities[capability.Name] = capability;
                }
            }
            catch
            {
            }
        }

        public void Record(int promptTokens, int completionTokens, int iterations, int scripts, int scriptFailures)
        {
            lock (_lock)
            {
                TotalMessages++;
                TotalIterations += iterations;
                TotalPromptTokens += promptTokens;
                TotalCompletionTokens += completionTokens;
                TotalScriptsRun += scripts;
                TotalScriptFailures += scriptFailures;
            }

            var key = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var bucket = _days.GetOrAdd(key, _ => new DayBucket { Date = key });
            lock (bucket)
            {
                bucket.Messages++;
                bucket.PromptTokens += promptTokens;
                bucket.CompletionTokens += completionTokens;
                bucket.Iterations += iterations;
                bucket.Scripts += scripts;
                bucket.ScriptFailures += scriptFailures;
            }

            QueueSave();
        }

        public void RecordCapability(string capabilityName, bool success, bool confirmationBlocked = false)
        {
            capabilityName = string.IsNullOrWhiteSpace(capabilityName) ? "unknown" : capabilityName.Trim();

            lock (_lock)
            {
                TotalCapabilityCalls++;
                if (!success)
                {
                    TotalCapabilityFailures++;
                }

                if (confirmationBlocked)
                {
                    TotalCapabilityConfirmationBlocks++;
                }
            }

            var key = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var dayBucket = _days.GetOrAdd(key, _ => new DayBucket { Date = key });
            lock (dayBucket)
            {
                dayBucket.CapabilityCalls++;
                if (!success)
                {
                    dayBucket.CapabilityFailures++;
                }

                if (confirmationBlocked)
                {
                    dayBucket.CapabilityConfirmationBlocks++;
                }
            }

            var capabilityBucket = _capabilities.GetOrAdd(capabilityName, name => new CapabilityBucket { Name = name });
            lock (capabilityBucket)
            {
                capabilityBucket.Executions++;
                if (!success)
                {
                    capabilityBucket.Failures++;
                }

                if (confirmationBlocked)
                {
                    capabilityBucket.ConfirmationBlocks++;
                }

                capabilityBucket.LastExecutedAt = DateTime.UtcNow;
            }

            QueueSave();
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
                    Days = _days.Values.OrderBy(bucket => bucket.Date).ToList(),
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

        public object GetSummary()
        {
            long totalTokens = TotalPromptTokens + TotalCompletionTokens;
            double avgPromptPerMsg = TotalMessages > 0 ? (double)TotalPromptTokens / TotalMessages : 0;
            double avgCompletionPerMsg = TotalMessages > 0 ? (double)TotalCompletionTokens / TotalMessages : 0;
            double avgIterationsPerMsg = TotalMessages > 0 ? (double)TotalIterations / TotalMessages : 0;
            double scriptSuccessRate = TotalScriptsRun > 0 ? (double)(TotalScriptsRun - TotalScriptFailures) / TotalScriptsRun * 100 : 100;
            double capabilitySuccessRate = TotalCapabilityCalls > 0 ? (double)(TotalCapabilityCalls - TotalCapabilityFailures) / TotalCapabilityCalls * 100 : 100;

            var fullHistory = _days.Values
                .OrderBy(day => day.Date)
                .Select(day => new
                {
                    date = day.Date,
                    messages = day.Messages,
                    promptTokens = day.PromptTokens,
                    completionTokens = day.CompletionTokens,
                    totalTokens = day.PromptTokens + day.CompletionTokens,
                    iterations = day.Iterations,
                    scripts = day.Scripts,
                    scriptFailures = day.ScriptFailures,
                    capabilityCalls = day.CapabilityCalls,
                    capabilityFailures = day.CapabilityFailures,
                    capabilityConfirmationBlocks = day.CapabilityConfirmationBlocks
                })
                .ToList();

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
                    lastExecutedAt = capability.LastExecutedAt
                })
                .ToList();

            var todayKey = DateTime.UtcNow.ToString("yyyy-MM-dd");
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
                    avgPromptTokensPerMessage = Math.Round(avgPromptPerMsg, 1),
                    avgCompletionTokensPerMessage = Math.Round(avgCompletionPerMsg, 1),
                    avgIterationsPerMessage = Math.Round(avgIterationsPerMsg, 2),
                    scriptSuccessRatePct = Math.Round(scriptSuccessRate, 1),
                    capabilitySuccessRatePct = Math.Round(capabilitySuccessRate, 1)
                },
                today = today == null ? null : (object)new
                {
                    messages = today.Messages,
                    promptTokens = today.PromptTokens,
                    completionTokens = today.CompletionTokens,
                    totalTokens = today.PromptTokens + today.CompletionTokens,
                    scripts = today.Scripts,
                    capabilityCalls = today.CapabilityCalls,
                    capabilityFailures = today.CapabilityFailures,
                    capabilityConfirmationBlocks = today.CapabilityConfirmationBlocks
                },
                historyWindow = new
                {
                    firstDay = fullHistory.FirstOrDefault()?.date,
                    lastDay = fullHistory.LastOrDefault()?.date,
                    totalDays = fullHistory.Count
                },
                dailyHistory = fullHistory,
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
            public List<DayBucket> Days { get; set; } = new();
            public List<CapabilityBucket> Capabilities { get; set; } = new();
        }

        public class DayBucket
        {
            public string? Date { get; set; }
            public long Messages { get; set; }
            public long PromptTokens { get; set; }
            public long CompletionTokens { get; set; }
            public long Iterations { get; set; }
            public long Scripts { get; set; }
            public long ScriptFailures { get; set; }
            public long CapabilityCalls { get; set; }
            public long CapabilityFailures { get; set; }
            public long CapabilityConfirmationBlocks { get; set; }
        }

        public class CapabilityBucket
        {
            public string Name { get; set; } = string.Empty;
            public long Executions { get; set; }
            public long Failures { get; set; }
            public long ConfirmationBlocks { get; set; }
            public DateTime? LastExecutedAt { get; set; }
        }
    }
}
