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
                    catch (Exception ex) { _ = service.ServiceLogError(ex, $"[KliveAgent] Skipped unreadable memory file {Path.GetFileName(file)}.", false); }
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
            if (!cacheLoaded) await LoadCacheAsync();
            int clampedImportance = Math.Clamp(importance, 1, 5);
            var newTags = tags?.ToList() ?? new List<string>();

            // Dedup on save: if a memory with identical normalised content and the same type already
            // exists, merge into it (union tags, keep the higher importance) instead of writing a
            // near-duplicate file. Curation is no longer left entirely to the model deleting dupes.
            AgentMemoryEntry existing;
            await cacheLock.WaitAsync();
            try
            {
                string normalized = NormalizeContent(content);
                existing = cachedMemories.FirstOrDefault(m =>
                    m.MemoryType == memoryType && NormalizeContent(m.Content) == normalized);
                if (existing != null)
                {
                    existing.Importance = Math.Max(existing.Importance, clampedImportance);
                    foreach (var t in newTags)
                        if (!existing.Tags.Any(et => string.Equals(et, t, StringComparison.OrdinalIgnoreCase)))
                            existing.Tags.Add(t);
                    if (string.IsNullOrEmpty(existing.Title) && !string.IsNullOrEmpty(title))
                        existing.Title = title;
                }
            }
            finally { cacheLock.Release(); }

            if (existing != null)
            {
                var existingPath = Path.Combine(
                    OmniPaths.GetPath(OmniPaths.GlobalPaths.KliveAgentMemoriesDirectory),
                    $"{existing.Id}.json");
                await service.GetDataHandler().SerialiseObjectToFile(existingPath, existing);
                return existing;
            }

            var entry = new AgentMemoryEntry
            {
                Content = content,
                Tags = newTags,
                Source = source,
                Importance = clampedImportance,
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

        /// <summary>Normalises content for dedup: trim, lowercase, collapse internal whitespace.</summary>
        private static string NormalizeContent(string? content)
        {
            if (string.IsNullOrWhiteSpace(content)) return string.Empty;
            return System.Text.RegularExpressions.Regex.Replace(content.Trim().ToLowerInvariant(), @"\s+", " ");
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
                var df = BuildDocFrequencies(queryTerms, corpus);

                return cachedMemories
                    .Select((m, i) =>
                    {
                        var doc = corpus[i];
                        double bm25 = Bm25Score(queryTerms, doc, N, df, avgLen);
                        return new { Memory = m, Score = bm25 + m.Importance * 0.5 + RecencyBoost(m.CreatedAt) };
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
                var df = BuildDocFrequencies(queryTerms, corpus);

                return shortcuts
                    .Select((m, i) =>
                    {
                        var doc = corpus[i];
                        double bm25 = Bm25Score(queryTerms, doc, N, df, avgLen);
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
                var idTag = ShortId(sc.Id);
                allItems.Add(($"[Shortcut id={idTag}] {title}{TruncateForPrompt(sc.Content, 220)}", sc.Importance + 1.5));
            }
            foreach (var mem in regularMemories)
            {
                var tagStr = mem.Tags.Count > 0 ? $" [{string.Join(", ", mem.Tags)}]" : "";
                var idTag = ShortId(mem.Id);
                allItems.Add(($"[Memory id={idTag}] {TruncateForPrompt(mem.Content, 200)}{tagStr}", mem.Importance));
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

        /// <summary>
        /// Resolves a short id prefix (used in prompts) to a full memory id. Returns null if
        /// no match or multiple matches exist.
        /// </summary>
        public async Task<string?> ResolveIdAsync(string idOrPrefix)
        {
            if (string.IsNullOrWhiteSpace(idOrPrefix)) return null;
            if (!cacheLoaded) await LoadCacheAsync();
            await cacheLock.WaitAsync();
            try
            {
                var exact = cachedMemories.FirstOrDefault(m => m.Id == idOrPrefix);
                if (exact != null) return exact.Id;
                var matches = cachedMemories
                    .Where(m => m.Id.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                return matches.Count == 1 ? matches[0].Id : null;
            }
            finally { cacheLock.Release(); }
        }

        /// <summary>
        /// One-shot cleanup: removes legacy auto-saved "completed task" changelog memories.
        /// These were generated by an earlier brain that auto-recorded every successful turn,
        /// which polluted future prompts. Real memories (durable facts about reality) are preserved.
        /// </summary>
        public async Task<int> PruneAutoCompletedTaskMemoriesAsync()
        {
            if (!cacheLoaded) await LoadCacheAsync();

            List<AgentMemoryEntry> toRemove;
            await cacheLock.WaitAsync();
            try
            {
                toRemove = cachedMemories
                    .Where(m => m.MemoryType != "shortcut"
                        && m.Tags != null
                        && m.Tags.Contains("auto", StringComparer.OrdinalIgnoreCase)
                        && m.Tags.Contains("completed-task", StringComparer.OrdinalIgnoreCase))
                    .ToList();
            }
            finally { cacheLock.Release(); }

            int removed = 0;
            foreach (var m in toRemove)
            {
                if (await DeleteMemoryAsync(m.Id)) removed++;
            }
            return removed;
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return string.Empty;
            return id.Length <= 8 ? id : id[..8];
        }

        /// <summary>
        /// Precomputes document frequency for each distinct query term ONCE per recall. Previously df
        /// was recomputed inside the per-document scorer (a full corpus scan per term, per document →
        /// O(terms × N²)); precomputing collapses it to a single O(terms × N) pass.
        /// </summary>
        private static Dictionary<string, int> BuildDocFrequencies(List<string> queryTerms, List<string> corpus)
        {
            var df = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var term in queryTerms.Distinct(StringComparer.Ordinal))
                df[term] = corpus.Count(d => d.Contains(term, StringComparison.Ordinal));
            return df;
        }

        /// <summary>A small recency nudge so a fresh memory isn't permanently outranked by an old
        /// high-importance one. Decays smoothly (~45-day scale); always ≥0, at most ~0.5.</summary>
        private static double RecencyBoost(DateTime createdAtUtc)
        {
            double ageDays = Math.Max(0, (DateTime.UtcNow - createdAtUtc).TotalDays);
            return 0.5 * Math.Exp(-ageDays / 45.0);
        }

        // BM25 scorer (k1=1.5, b=0.75) — spec Chapter 5. df is precomputed (see BuildDocFrequencies).
        private static double Bm25Score(
            List<string> queryTerms,
            string document,
            int N,
            IReadOnlyDictionary<string, int> df,
            double avgDocLen)
        {
            const double k1 = 1.5, b = 0.75;
            double score = 0;
            foreach (var term in queryTerms)
            {
                int tf = CountOccurrences(document, term);
                if (tf == 0) continue;
                int dfi = df.TryGetValue(term, out var v) ? v : 0;
                double idf = Math.Log((N - dfi + 0.5) / (dfi + 0.5) + 1);
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
