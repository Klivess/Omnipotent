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

        // rolling per-day buckets  key = "yyyy-MM-dd"
        private readonly ConcurrentDictionary<string, DayBucket> _days = new();

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

        public object GetSummary()
        {
            long totalTokens = TotalPromptTokens + TotalCompletionTokens;
            double avgPromptPerMsg = TotalMessages > 0 ? (double)TotalPromptTokens / TotalMessages : 0;
            double avgCompletionPerMsg = TotalMessages > 0 ? (double)TotalCompletionTokens / TotalMessages : 0;
            double avgIterationsPerMsg = TotalMessages > 0 ? (double)TotalIterations / TotalMessages : 0;
            double scriptSuccessRate = TotalScriptsRun > 0 ? (double)(TotalScriptsRun - TotalScriptFailures) / TotalScriptsRun * 100 : 100;

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
                    scriptFailures = d.ScriptFailures
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
                    avgPromptTokensPerMessage = Math.Round(avgPromptPerMsg, 1),
                    avgCompletionTokensPerMessage = Math.Round(avgCompletionPerMsg, 1),
                    avgIterationsPerMessage = Math.Round(avgIterationsPerMsg, 2),
                    scriptSuccessRatePct = Math.Round(scriptSuccessRate, 1)
                },
                today = today == null ? null : (object)new
                {
                    messages = today.Messages,
                    promptTokens = today.PromptTokens,
                    completionTokens = today.CompletionTokens,
                    totalTokens = today.PromptTokens + today.CompletionTokens,
                    scripts = today.Scripts
                },
                dailyHistory = recent30
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
        }
    }
}
