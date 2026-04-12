using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.KliveAgent
{
    public sealed class KliveAgentMemory
    {
        private readonly SemaphoreSlim _persistLock = new(1, 1);
        private readonly ConcurrentDictionary<string, KliveAgentMemoryRecord> _memoryIndex = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, KliveAgentEventRule> _ruleIndex = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, KliveAgentScriptRunRecord> _scriptRunIndex = new(StringComparer.OrdinalIgnoreCase);

        private readonly string _baseDirectory;
        private readonly string _memoryFilePath;
        private readonly string _rulesFilePath;
        private readonly string _scriptRunsFilePath;

        public KliveAgentMemory(string? baseDirectory = null)
        {
            _baseDirectory = baseDirectory ?? OmniPaths.GetPath("SavedData/KliveAgent");
            _memoryFilePath = Path.Combine(_baseDirectory, "memories.json");
            _rulesFilePath = Path.Combine(_baseDirectory, "event-rules.json");
            _scriptRunsFilePath = Path.Combine(_baseDirectory, "script-runs.json");
        }

        public async Task InitializeAsync()
        {
            Directory.CreateDirectory(_baseDirectory);
            await LoadMemoriesAsync();
            await LoadRulesAsync();
            await LoadScriptRunsAsync();
        }

        public List<KliveAgentMemoryRecord> GetRecentMemories(int maxCount = 20)
        {
            return _memoryIndex.Values
                .OrderByDescending(m => m.LastUpdatedAtUtc)
                .Take(Math.Max(1, maxCount))
                .ToList();
        }

        public List<KliveAgentMemorySearchResult> Search(string query, int maxCount = 8)
        {
            var normalizedQuery = Normalize(query);
            if (string.IsNullOrWhiteSpace(normalizedQuery))
            {
                return new List<KliveAgentMemorySearchResult>();
            }

            var queryTokens = Tokenize(normalizedQuery);
            var results = new List<KliveAgentMemorySearchResult>();

            foreach (var memory in _memoryIndex.Values)
            {
                var text = Normalize($"{memory.Title} {memory.Content} {string.Join(' ', memory.Tags)}");
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var score = ScoreMemory(text, queryTokens, memory.Importance);
                if (score <= 0)
                {
                    continue;
                }

                results.Add(new KliveAgentMemorySearchResult
                {
                    Memory = memory,
                    Score = score
                });
            }

            return results
                .OrderByDescending(r => r.Score)
                .ThenByDescending(r => r.Memory.LastUpdatedAtUtc)
                .Take(Math.Max(1, maxCount))
                .ToList();
        }

        public async Task<KliveAgentMemoryRecord> SaveMemoryAsync(KliveAgentMemoryRecord memory)
        {
            memory.Id = string.IsNullOrWhiteSpace(memory.Id) ? Guid.NewGuid().ToString("N") : memory.Id;
            memory.LastUpdatedAtUtc = DateTime.UtcNow;
            memory.CreatedAtUtc = memory.CreatedAtUtc == default ? DateTime.UtcNow : memory.CreatedAtUtc;
            memory.Title = memory.Title?.Trim() ?? string.Empty;
            memory.Content = memory.Content?.Trim() ?? string.Empty;
            memory.Tags ??= new List<string>();

            _memoryIndex[memory.Id] = memory;
            await PersistMemoriesAsync();
            return memory;
        }

        public async Task<KliveAgentScriptRunRecord> SaveScriptRunAsync(KliveAgentScriptRunRecord run)
        {
            run.RunId = string.IsNullOrWhiteSpace(run.RunId) ? Guid.NewGuid().ToString("N") : run.RunId;
            _scriptRunIndex[run.RunId] = run;
            await PersistScriptRunsAsync();
            return run;
        }

        public List<KliveAgentScriptRunRecord> GetRecentScriptRuns(int maxCount = 30)
        {
            return _scriptRunIndex.Values
                .OrderByDescending(r => r.StartedAtUtc)
                .Take(Math.Max(1, maxCount))
                .ToList();
        }

        public List<KliveAgentEventRule> GetRules()
        {
            return _ruleIndex.Values
                .OrderBy(r => r.Name)
                .ToList();
        }

        public async Task<KliveAgentEventRule> UpsertRuleAsync(KliveAgentEventRule rule)
        {
            rule.Id = string.IsNullOrWhiteSpace(rule.Id) ? Guid.NewGuid().ToString("N") : rule.Id;
            rule.CreatedAtUtc = rule.CreatedAtUtc == default ? DateTime.UtcNow : rule.CreatedAtUtc;
            rule.CooldownSeconds = Math.Max(5, rule.CooldownSeconds);
            rule.MessageContainsAny ??= new List<string>();
            _ruleIndex[rule.Id] = rule;
            await PersistRulesAsync();
            return rule;
        }

        public async Task<bool> DeleteRuleAsync(string id)
        {
            if (_ruleIndex.TryRemove(id, out _))
            {
                await PersistRulesAsync();
                return true;
            }

            return false;
        }

        public async Task PersistAllAsync()
        {
            await PersistMemoriesAsync();
            await PersistRulesAsync();
            await PersistScriptRunsAsync();
        }

        private async Task LoadMemoriesAsync()
        {
            var items = await LoadFromFileAsync<List<KliveAgentMemoryRecord>>(_memoryFilePath) ?? new List<KliveAgentMemoryRecord>();
            _memoryIndex.Clear();

            foreach (var item in items)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Id))
                {
                    continue;
                }

                _memoryIndex[item.Id] = item;
            }
        }

        private async Task LoadRulesAsync()
        {
            var items = await LoadFromFileAsync<List<KliveAgentEventRule>>(_rulesFilePath) ?? new List<KliveAgentEventRule>();
            _ruleIndex.Clear();

            foreach (var item in items)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Id))
                {
                    continue;
                }

                _ruleIndex[item.Id] = item;
            }
        }

        private async Task LoadScriptRunsAsync()
        {
            var items = await LoadFromFileAsync<List<KliveAgentScriptRunRecord>>(_scriptRunsFilePath) ?? new List<KliveAgentScriptRunRecord>();
            _scriptRunIndex.Clear();

            foreach (var item in items)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.RunId))
                {
                    continue;
                }

                _scriptRunIndex[item.RunId] = item;
            }
        }

        private async Task PersistMemoriesAsync()
        {
            var ordered = _memoryIndex.Values
                .OrderByDescending(m => m.LastUpdatedAtUtc)
                .Take(2000)
                .ToList();

            await SaveToFileAsync(_memoryFilePath, ordered);
        }

        private async Task PersistRulesAsync()
        {
            var ordered = _ruleIndex.Values
                .OrderBy(r => r.Name)
                .ToList();

            await SaveToFileAsync(_rulesFilePath, ordered);
        }

        private async Task PersistScriptRunsAsync()
        {
            var ordered = _scriptRunIndex.Values
                .OrderByDescending(r => r.StartedAtUtc)
                .Take(500)
                .ToList();

            await SaveToFileAsync(_scriptRunsFilePath, ordered);
        }

        private async Task SaveToFileAsync<T>(string path, T payload)
        {
            await _persistLock.WaitAsync();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var json = JsonConvert.SerializeObject(payload, Formatting.Indented);
                await File.WriteAllTextAsync(path, json, Encoding.UTF8);
            }
            finally
            {
                _persistLock.Release();
            }
        }

        private static async Task<T?> LoadFromFileAsync<T>(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return default;
                }

                var json = await File.ReadAllTextAsync(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return default;
                }

                return JsonConvert.DeserializeObject<T>(json);
            }
            catch
            {
                return default;
            }
        }

        private static HashSet<string> Tokenize(string text)
        {
            var tokenList = Regex.Split(text, "[^a-z0-9]+", RegexOptions.IgnoreCase)
                .Where(t => t.Length >= 2)
                .Select(t => t.ToLowerInvariant());
            return new HashSet<string>(tokenList);
        }

        private static string Normalize(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            return text.Trim().ToLowerInvariant();
        }

        private static double ScoreMemory(string memoryText, HashSet<string> queryTokens, double importance)
        {
            var memoryTokens = Tokenize(memoryText);
            if (memoryTokens.Count == 0 || queryTokens.Count == 0)
            {
                return 0;
            }

            var intersectionCount = queryTokens.Count(memoryTokens.Contains);
            if (intersectionCount == 0)
            {
                return 0;
            }

            var unionCount = memoryTokens.Count + queryTokens.Count - intersectionCount;
            var jaccard = unionCount == 0 ? 0 : (double)intersectionCount / unionCount;
            var clippedImportance = Math.Clamp(importance, 0, 1);
            return jaccard + (0.2 * clippedImportance);
        }
    }
}
