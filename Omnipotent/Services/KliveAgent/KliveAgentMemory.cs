using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Services.KliveAgent.Models;

namespace Omnipotent.Services.KliveAgent
{
    public class KliveAgentMemory
    {
        private readonly KliveAgent service;
        private List<AgentMemoryEntry> cachedMemories = new();
        private readonly SemaphoreSlim cacheLock = new(1, 1);
        private bool cacheLoaded = false;

        public KliveAgentMemory(KliveAgent service)
        {
            this.service = service;
        }

        public async Task InitializeAsync()
        {
            var dir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentMemoriesDirectory);
            if (!Directory.Exists(dir))
            {
                await service.GetDataHandler().CreateDirectory(dir);
            }
            await LoadCacheAsync();
        }

        private async Task LoadCacheAsync()
        {
            await cacheLock.WaitAsync();
            try
            {
                cachedMemories.Clear();
                var dir = OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentMemoriesDirectory);
                if (!Directory.Exists(dir)) return;

                foreach (var file in Directory.GetFiles(dir, "*.json"))
                {
                    try
                    {
                        var entry = await service.GetDataHandler().ReadAndDeserialiseDataFromFile<AgentMemoryEntry>(file);
                        if (entry != null) cachedMemories.Add(entry);
                    }
                    catch { }
                }
                cacheLoaded = true;
            }
            finally
            {
                cacheLock.Release();
            }
        }

        public async Task<AgentMemoryEntry> SaveMemoryAsync(string content, string[] tags = null, string source = "agent", int importance = 1, string memoryType = "general", string title = null)
        {
            var entry = new AgentMemoryEntry
            {
                Content = content,
                Tags = tags?.ToList() ?? new List<string>(),
                Source = source,
                Importance = Math.Clamp(importance, 1, 5),
                MemoryType = memoryType,
                Title = title
            };

            var path = Path.Combine(
                OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentMemoriesDirectory),
                $"{entry.Id}.json");

            await service.GetDataHandler().SerialiseObjectToFile(path, entry);

            await cacheLock.WaitAsync();
            try
            {
                cachedMemories.Add(entry);
            }
            finally
            {
                cacheLock.Release();
            }

            return entry;
        }

        public async Task<List<AgentMemoryEntry>> RecallMemoriesAsync(string query, int maxResults = 10)
        {
            if (!cacheLoaded) await LoadCacheAsync();

            await cacheLock.WaitAsync();
            try
            {
                if (string.IsNullOrWhiteSpace(query))
                {
                    return cachedMemories
                        .OrderByDescending(m => m.Importance)
                        .ThenByDescending(m => m.CreatedAt)
                        .Take(maxResults)
                        .ToList();
                }

                var queryTerms = query.ToLowerInvariant()
                    .Split(new[] { ' ', ',', '.', ';', ':', '?' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(t => t.Length >= 2).ToList();
                var corpus = cachedMemories.Select(m =>
                    ((m.Content ?? "") + " " + string.Join(" ", m.Tags ?? new List<string>())).ToLowerInvariant()).ToList();
                double avgLen = corpus.Count == 0 ? 1 : corpus.Average(c => c.Length);
                int N = corpus.Count;

                return cachedMemories
                    .Select((m, i) =>
                    {
                        var doc = corpus[i];
                        double bm25 = Bm25Score(queryTerms, doc, N, corpus, avgLen);
                        return new { Memory = m, Score = bm25 + m.Importance * 0.5 };
                    })
                    .Where(x => x.Score > 0)
                    .OrderByDescending(x => x.Score)
                    .ThenByDescending(x => x.Memory.CreatedAt)
                    .Take(maxResults)
                    .Select(x => x.Memory)
                    .ToList();
            }
            finally
            {
                cacheLock.Release();
            }
        }

        public async Task<List<AgentMemoryEntry>> GetAllMemoriesAsync()
        {
            if (!cacheLoaded) await LoadCacheAsync();

            await cacheLock.WaitAsync();
            try
            {
                return cachedMemories.OrderByDescending(m => m.CreatedAt).ToList();
            }
            finally
            {
                cacheLock.Release();
            }
        }

        public async Task<bool> DeleteMemoryAsync(string id)
        {
            var path = Path.Combine(
                OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentMemoriesDirectory),
                $"{id}.json");

            if (!File.Exists(path)) return false;

            await service.GetDataHandler().DeleteFile(path);

            await cacheLock.WaitAsync();
            try
            {
                cachedMemories.RemoveAll(m => m.Id == id);
            }
            finally
            {
                cacheLock.Release();
            }

            return true;
        }

        public async Task<List<AgentMemoryEntry>> GetShortcutsAsync(string? query = null, int maxResults = int.MaxValue)
        {
            if (!cacheLoaded) await LoadCacheAsync();

            await cacheLock.WaitAsync();
            try
            {
                var shortcuts = cachedMemories
                    .Where(m => m.MemoryType == "shortcut")
                    .ToList();

                if (string.IsNullOrWhiteSpace(query))
                {
                    return shortcuts
                        .OrderByDescending(m => m.Importance)
                        .ThenByDescending(m => m.CreatedAt)
                        .Take(maxResults)
                        .ToList();
                }

                var queryTerms = query.ToLowerInvariant()
                    .Split(new[] { ' ', ',', '.', ';', ':', '?' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(t => t.Length >= 2).ToList();
                var corpus = shortcuts.Select(m =>
                    ((m.Title ?? "") + " " + (m.Content ?? "") + " " +
                     string.Join(" ", m.Tags ?? new List<string>())).ToLowerInvariant()).ToList();
                double avgLen = corpus.Count == 0 ? 1 : corpus.Average(c => c.Length);
                int N = corpus.Count;

                return shortcuts
                    .Select((m, i) =>
                    {
                        var doc = corpus[i];
                        double bm25 = Bm25Score(queryTerms, doc, N, corpus, avgLen);
                        return new { Memory = m, Score = bm25 + m.Importance * 0.5 };
                    })
                    .Where(x => x.Score > 0)
                    .OrderByDescending(x => x.Score)
                    .ThenByDescending(x => x.Memory.CreatedAt)
                    .Take(maxResults)
                    .Select(x => x.Memory)
                    .ToList();
            }
            finally
            {
                cacheLock.Release();
            }
        }

        public async Task<string> FormatMemoriesForPrompt(
            string conversationContext,
            int maxMemories = 6,
            int maxShortcuts = 4,
            int maxTokens = KliveAgentContextBudget.MemoryBudget)
        {
            var shortcuts = await GetShortcutsAsync(conversationContext, maxShortcuts);
            var relevant = await RecallMemoriesAsync(conversationContext, maxMemories);
            var regularMemories = relevant.Where(m => m.MemoryType != "shortcut").ToList();

            // Budget-aware formatting: fit as many items as possible within the token limit
            var allItems = new List<(string text, double score)>();

            foreach (var sc in shortcuts)
            {
                var title = string.IsNullOrEmpty(sc.Title) ? string.Empty : $"{TruncateForPrompt(sc.Title, 80)}: ";
                allItems.Add(($"[Shortcut] {title}{TruncateForPrompt(sc.Content, 220)}", sc.Importance + 1.5));
            }
            foreach (var mem in regularMemories)
            {
                var tagStr = mem.Tags.Count > 0 ? $" [{string.Join(", ", mem.Tags)}]" : "";
                allItems.Add(($"[Memory] {TruncateForPrompt(mem.Content, 200)}{tagStr}", mem.Importance));
            }

            if (allItems.Count == 0) return string.Empty;

            var fitted = KliveAgentContextBudget.FitItemsInBudget(
                allItems,
                maxTokens,
                item => item.text,
                item => item.score);

            if (fitted.Count == 0) return string.Empty;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[Memories & Shortcuts]");
            foreach (var (text, _) in fitted)
                sb.AppendLine($"- {text}");

            return sb.ToString();
        }

        private static string TruncateForPrompt(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
                return value ?? string.Empty;

            return value[..Math.Max(0, maxLength - 3)] + "...";
        }

        // BM25 scorer (k1=1.5, b=0.75) — spec Chapter 5
        private static double Bm25Score(
            List<string> queryTerms,
            string document,
            int N,
            List<string> corpus,
            double avgDocLen)
        {
            const double k1 = 1.5, b = 0.75;
            double score = 0;
            foreach (var term in queryTerms)
            {
                int tf = CountOccurrences(document, term);
                if (tf == 0) continue;
                int df = corpus.Count(d => d.Contains(term, StringComparison.Ordinal));
                double idf = Math.Log((N - df + 0.5) / (df + 0.5) + 1);
                score += idf * (tf * (k1 + 1)) / (tf + k1 * (1 - b + b * document.Length / avgDocLen));
            }
            return score;
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(needle)) return 0;
            int count = 0, index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            { count++; index += needle.Length; }
            return count;
        }
    }
}
