using Newtonsoft.Json;
using System.Collections.Concurrent;

namespace Omnipotent.Services.KliveAgent
{
    public class KliveAgentStats
    {
        private readonly object _lock = new();

        // lifetime counters
        public long TotalMessages { get; private set; }
        public long TotalIterations { get; private set; }
        public long TotalPromptTokens { get; private set; }
        public long TotalCompletionTokens { get; private set; }
        public long TotalScriptsRun { get; private set; }
        public long TotalScriptFailures { get; private set; }
        public long TotalCapabilityCalls { get; private set; }
        public long TotalCapabilityFailures { get; private set; }
        public long TotalCapabilityConfirmationBlocks { get; private set; }

        // rolling per-day buckets  key = "yyyy-MM-dd"
        private readonly ConcurrentDictionary<string, DayBucket> _days = new();
        private readonly ConcurrentDictionary<string, CapabilityBucket> _capabilities = new(StringComparer.OrdinalIgnoreCase);

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
        }

        public object GetSummary()
        {
            long totalTokens = TotalPromptTokens + TotalCompletionTokens;
            double avgPromptPerMsg = TotalMessages > 0 ? (double)TotalPromptTokens / TotalMessages : 0;
            double avgCompletionPerMsg = TotalMessages > 0 ? (double)TotalCompletionTokens / TotalMessages : 0;
            double avgIterationsPerMsg = TotalMessages > 0 ? (double)TotalIterations / TotalMessages : 0;
            double scriptSuccessRate = TotalScriptsRun > 0 ? (double)(TotalScriptsRun - TotalScriptFailures) / TotalScriptsRun * 100 : 100;
            double capabilitySuccessRate = TotalCapabilityCalls > 0 ? (double)(TotalCapabilityCalls - TotalCapabilityFailures) / TotalCapabilityCalls * 100 : 100;

            var recent30 = _days.Values
                .OrderByDescending(d => d.Date)
                .Take(30)
                .OrderBy(d => d.Date)
                .Select(d => new
                {
                    date = d.Date,
                    messages = d.Messages,
                    promptTokens = d.PromptTokens,
                    completionTokens = d.CompletionTokens,
                    totalTokens = d.PromptTokens + d.CompletionTokens,
                    iterations = d.Iterations,
                    scripts = d.Scripts,
                    scriptFailures = d.ScriptFailures,
                    capabilityCalls = d.CapabilityCalls,
                    capabilityFailures = d.CapabilityFailures,
                    capabilityConfirmationBlocks = d.CapabilityConfirmationBlocks
                })
                .ToList();

            var topCapabilities = _capabilities.Values
                .OrderByDescending(c => c.Executions)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .Select(c => new
                {
                    name = c.Name,
                    executions = c.Executions,
                    failures = c.Failures,
                    confirmationBlocks = c.ConfirmationBlocks,
                    lastExecutedAt = c.LastExecutedAt
                })
                .ToList();

            // compute today stats
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
                dailyHistory = recent30,
                topCapabilities
            };
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
