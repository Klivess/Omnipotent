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

        public async Task<AgentMemoryEntry> SaveMemoryAsync(string content, string[] tags = null, string source = "agent", int importance = 1)
        {
            var entry = new AgentMemoryEntry
            {
                Content = content,
                Tags = tags?.ToList() ?? new List<string>(),
                Source = source,
                Importance = Math.Clamp(importance, 1, 5)
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

                var queryTerms = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);

                return cachedMemories
                    .Select(m => new
                    {
                        Memory = m,
                        Score = queryTerms.Sum(term =>
                            (m.Content?.ToLowerInvariant().Contains(term) == true ? 2 : 0) +
                            (m.Tags?.Any(t => t.ToLowerInvariant().Contains(term)) == true ? 3 : 0)) +
                            m.Importance
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

        public async Task<string> FormatMemoriesForPrompt(string conversationContext, int maxMemories = 8)
        {
            var relevant = await RecallMemoriesAsync(conversationContext, maxMemories);
            if (relevant.Count == 0) return "";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("\n[Your Persistent Memories]");
            foreach (var mem in relevant)
            {
                var tagStr = mem.Tags.Count > 0 ? $" [{string.Join(", ", mem.Tags)}]" : "";
                sb.AppendLine($"- {mem.Content}{tagStr} (saved {mem.CreatedAt:yyyy-MM-dd})");
            }
            return sb.ToString();
        }
    }
}
