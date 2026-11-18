using System.Collections.Concurrent;
using System.Text;

namespace Omnipotent.Services.Demiurge
{
    /// <summary>What an agent is doing RIGHT NOW, between two committed events.</summary>
    public static class StewardActivityPhases
    {
        /// <summary>The request is in flight but no visible token has arrived (queueing/reasoning).</summary>
        public const string Thinking = "thinking";
        /// <summary>Visible prose is streaming — <see cref="StewardAgentActivity.Preview"/> is live.</summary>
        public const string Writing = "writing";
        /// <summary>A dispatched tool is executing; the model turn is already over.</summary>
        public const string Tool = "tool";
    }

    /// <summary>
    /// One agent's in-flight work, as a snapshot safe to serialize to the website. Nothing here is
    /// durable: the event log records what an agent DID, this records what it is doing before any
    /// event exists to show. A snapshot is replaced wholesale on every update.
    /// </summary>
    public sealed class StewardAgentActivity
    {
        public string StewardID { get; init; } = "";
        public string AgentID { get; init; } = "";
        /// <summary>Free-text role ("commander", "market-researcher") for the UI label.</summary>
        public string Role { get; init; } = "";
        public string Phase { get; init; } = StewardActivityPhases.Thinking;
        /// <summary>The tool being executed in <see cref="StewardActivityPhases.Tool"/>, else null.</summary>
        public string? ToolName { get; init; }
        /// <summary>Human-readable detail: the tool call summary, or null while writing.</summary>
        public string? Detail { get; init; }
        /// <summary>Live tail of the prose the model has generated this turn (bounded).</summary>
        public string? Preview { get; init; }
        /// <summary>Characters generated so far this turn — lets the UI show progress without the text.</summary>
        public int GeneratedChars { get; init; }
        public string? Model { get; init; }
        public DateTime StartedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
    }

    /// <summary>
    /// In-memory registry of live agent activity, so the Conversation panel can show who is about to
    /// speak and what they are producing — the gap the append-only event log cannot fill, because an
    /// event only exists once the turn is over (a Commander turn can take minutes).
    ///
    /// Deliberately ephemeral: nothing is persisted, a restart simply shows nobody working until the
    /// next model turn. Updates fan out through <see cref="Changed"/>/<see cref="Ended"/> to the
    /// WebSocket broadcaster. Token updates are throttled per agent so a fast stream cannot flood the
    /// socket; phase changes always publish immediately.
    /// </summary>
    public sealed class StewardAgentActivityTracker
    {
        /// <summary>Tail of the live turn kept for the UI. Enough for a readable line, never a transcript.</summary>
        public const int PreviewChars = 280;
        private const int PreviewThrottleMs = 250;
        /// <summary>An entry this stale is assumed orphaned by a crashed runner and is swept.</summary>
        private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

        private sealed class Entry
        {
            public string StewardID = "";
            public string AgentID = "";
            public string Role = "";
            public string Phase = StewardActivityPhases.Thinking;
            public string? ToolName;
            public string? Detail;
            public string? Model;
            public DateTime StartedAt = DateTime.UtcNow;
            public DateTime UpdatedAt = DateTime.UtcNow;
            public DateTime LastPublishedAt = DateTime.MinValue;
            /// <summary>Everything generated this turn, counted even after the buffer below is trimmed.</summary>
            public int TotalChars;
            public readonly StringBuilder Text = new();
        }

        private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);

        /// <summary>Fires when an agent's live state changes (start, phase change, throttled token tick).</summary>
        public event Action<StewardAgentActivity>? Changed;
        /// <summary>Fires (stewardID, agentID) when an agent stops working and its indicator must clear.</summary>
        public event Action<string, string>? Ended;

        private static string Key(string stewardID, string agentID) => $"{stewardID}/{agentID}";

        /// <summary>A model request is in flight; no visible output yet.</summary>
        public void BeginThinking(string stewardID, string agentID, string role, string? model)
        {
            var entry = entries.AddOrUpdate(Key(stewardID, agentID),
                _ => new Entry { StewardID = stewardID, AgentID = agentID, Role = role, Model = model },
                (_, existing) => existing);
            lock (entry)
            {
                entry.Role = role;
                entry.Model = model;
                entry.Phase = StewardActivityPhases.Thinking;
                entry.ToolName = null;
                entry.Detail = null;
                entry.Text.Clear();
                entry.TotalChars = 0;
                entry.StartedAt = DateTime.UtcNow;
                entry.UpdatedAt = entry.StartedAt;
                entry.LastPublishedAt = entry.StartedAt;
                Publish(Snapshot(entry));
            }
            Sweep();
        }

        /// <summary>
        /// A content delta arrived from the provider. The first token flips the phase to
        /// <see cref="StewardActivityPhases.Writing"/> and publishes at once; later tokens publish at
        /// most every <see cref="PreviewThrottleMs"/> so a fast model cannot saturate the socket.
        /// </summary>
        public void AppendToken(string stewardID, string agentID, string? chunk)
        {
            if (string.IsNullOrEmpty(chunk)) return;
            if (!entries.TryGetValue(Key(stewardID, agentID), out var entry)) return;
            StewardAgentActivity? publish = null;
            lock (entry)
            {
                bool firstToken = entry.Phase != StewardActivityPhases.Writing;
                entry.Text.Append(chunk);
                entry.TotalChars += chunk.Length;
                // Keep only what the UI can show plus a little slack, so a long turn cannot grow
                // this buffer without bound.
                if (entry.Text.Length > PreviewChars * 4)
                    entry.Text.Remove(0, entry.Text.Length - PreviewChars * 2);
                entry.Phase = StewardActivityPhases.Writing;
                entry.ToolName = null;
                entry.Detail = null;
                entry.UpdatedAt = DateTime.UtcNow;
                if (firstToken || (entry.UpdatedAt - entry.LastPublishedAt).TotalMilliseconds >= PreviewThrottleMs)
                {
                    entry.LastPublishedAt = entry.UpdatedAt;
                    publish = Snapshot(entry);
                }
            }
            if (publish != null) Publish(publish);
        }

        /// <summary>A tool the model asked for is now executing (the model turn itself is finished).</summary>
        public void BeginTool(string stewardID, string agentID, string toolName, string? detail)
        {
            if (!entries.TryGetValue(Key(stewardID, agentID), out var entry)) return;
            lock (entry)
            {
                entry.Phase = StewardActivityPhases.Tool;
                entry.ToolName = toolName;
                entry.Detail = Trim(detail, 160);
                entry.Text.Clear();
                entry.TotalChars = 0;
                entry.UpdatedAt = DateTime.UtcNow;
                entry.LastPublishedAt = entry.UpdatedAt;
                Publish(Snapshot(entry));
            }
        }

        /// <summary>The agent is no longer working — clear its indicator.</summary>
        public void End(string stewardID, string agentID)
        {
            if (!entries.TryRemove(Key(stewardID, agentID), out _)) return;
            try { Ended?.Invoke(stewardID, agentID); } catch { }
        }

        /// <summary>Every agent currently working on one Steward (stale entries swept first).</summary>
        public IReadOnlyList<StewardAgentActivity> ListForSteward(string stewardID)
        {
            // Purely wall-clock live state: the response cache's version model cannot track it, and a
            // cached indicator would freeze on a turn that finished minutes ago.
            KliveAPI.Caching.CacheDeps.MarkUncacheable("live agent activity");
            Sweep();
            var live = new List<StewardAgentActivity>();
            foreach (var entry in entries.Values)
            {
                if (!string.Equals(entry.StewardID, stewardID, StringComparison.Ordinal)) continue;
                lock (entry) live.Add(Snapshot(entry));
            }
            return live;
        }

        /// <summary>Drops entries whose runner died without clearing them, so a crash cannot leave a
        /// permanent "thinking…" indicator on the panel.</summary>
        private void Sweep()
        {
            DateTime cutoff = DateTime.UtcNow - StaleAfter;
            foreach (var kv in entries)
            {
                DateTime updated;
                lock (kv.Value) updated = kv.Value.UpdatedAt;
                if (updated > cutoff) continue;
                if (entries.TryRemove(kv.Key, out var dead))
                {
                    try { Ended?.Invoke(dead.StewardID, dead.AgentID); } catch { }
                }
            }
        }

        // Callers hold the entry lock; the snapshot is immutable so subscribers can keep it.
        private static StewardAgentActivity Snapshot(Entry entry)
        {
            string text = entry.Text.ToString();
            string preview = text.Length > PreviewChars ? "…" + text[^PreviewChars..] : text;
            return new StewardAgentActivity
            {
                StewardID = entry.StewardID,
                AgentID = entry.AgentID,
                Role = entry.Role,
                Phase = entry.Phase,
                ToolName = entry.ToolName,
                Detail = entry.Detail,
                Preview = preview.Length == 0 ? null : preview,
                GeneratedChars = entry.TotalChars,
                Model = entry.Model,
                StartedAt = entry.StartedAt,
                UpdatedAt = entry.UpdatedAt,
            };
        }

        private void Publish(StewardAgentActivity snapshot)
        {
            try { Changed?.Invoke(snapshot); } catch { /* a UI signal must never break a wake */ }
        }

        private static string? Trim(string? text, int max)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            return text.Length <= max ? text : text[..max] + "…";
        }
    }
}
