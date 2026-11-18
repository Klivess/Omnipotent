using Newtonsoft.Json;
using System.Collections.Concurrent;

namespace Omnipotent.Services.CS2ArbitrageBot.Engine
{
    /// <summary>
    /// When each Discord alert was last sent (and what it was called), kept on disk. The cooldowns used to live in
    /// memory, so every restart (18 in the week to 2026-11-13) sent each standing alert again: the same stuck relist
    /// produced a "relist overdue" and a "relist is no longer live" message after every start. The ledger also lets the
    /// status route say which alerts went out and when, which nothing showed before. Keys older than a month are
    /// dropped on save.
    /// </summary>
    public sealed class AlertLedger
    {
        private static readonly TimeSpan Retention = TimeSpan.FromDays(31);
        private readonly ConcurrentDictionary<string, Entry> sent = new(StringComparer.Ordinal);
        private readonly string? path;
        private int dirty;

        public sealed class Entry
        {
            public DateTime AtUtc { get; set; }
            public string? Title { get; set; }
        }

        public sealed record SentAlert(string Key, DateTime AtUtc, string? Title);

        public AlertLedger(string? path) { this.path = path; }

        public int Count => sent.Count;

        /// <summary>Loads the saved ledger (a missing or unreadable file starts empty).</summary>
        public void Load()
        {
            if (path == null || !File.Exists(path)) return;
            try
            {
                var saved = JsonConvert.DeserializeObject<Dictionary<string, Entry>>(File.ReadAllText(path));
                if (saved == null) return;
                foreach (var (key, entry) in saved)
                    if (entry != null) sent[key] = new Entry { AtUtc = DateTime.SpecifyKind(entry.AtUtc, DateTimeKind.Utc), Title = entry.Title };
            }
            catch { /* a corrupt ledger only means an alert may repeat once */ }
        }

        /// <summary>True (and recorded) when <paramref name="key"/> was not sent within <paramref name="cooldown"/>.</summary>
        public bool TryTake(string key, TimeSpan cooldown, DateTime nowUtc, string? title = null)
        {
            while (true)
            {
                var next = new Entry { AtUtc = nowUtc, Title = title };
                if (sent.TryGetValue(key, out var last))
                {
                    if (nowUtc - last.AtUtc < cooldown && last.AtUtc <= nowUtc.AddMinutes(5)) return false;
                    if (!sent.TryUpdate(key, next, last)) continue;
                }
                else if (!sent.TryAdd(key, next)) continue;
                Interlocked.Exchange(ref dirty, 1);
                return true;
            }
        }

        public DateTime? LastSent(string key) => sent.TryGetValue(key, out var entry) ? entry.AtUtc : null;

        /// <summary>The alerts sent most recently, newest first.</summary>
        public IReadOnlyList<SentAlert> Recent(int max) =>
            sent.Select(kv => new SentAlert(kv.Key, kv.Value.AtUtc, kv.Value.Title))
                .OrderByDescending(a => a.AtUtc).Take(Math.Max(0, max)).ToList();

        /// <summary>Writes the ledger when something changed. Returns whether it wrote.</summary>
        public async Task<bool> SaveIfChangedAsync(Func<string, string, Task> write, DateTime nowUtc)
        {
            if (path == null || Interlocked.Exchange(ref dirty, 0) == 0) return false;
            foreach (var (key, entry) in sent)
                if (nowUtc - entry.AtUtc > Retention) sent.TryRemove(key, out _);
            try
            {
                await write(path, JsonConvert.SerializeObject(sent.ToDictionary(kv => kv.Key, kv => kv.Value), Formatting.Indented));
                return true;
            }
            catch
            {
                Interlocked.Exchange(ref dirty, 1);
                throw;
            }
        }
    }
}
