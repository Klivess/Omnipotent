using System.Text;
using Newtonsoft.Json;
using Omnipotent.Services.OmniBlox.Agents;
using Omnipotent.Services.OmniBlox.Core;

namespace Omnipotent.Services.OmniBlox.Pipeline
{
    /// <summary>One thing that happened in a cycle: the agent's words, a tool call, its result, or a note from the runner.</summary>
    public sealed class LiveEntry
    {
        public DateTime Utc { get; set; }
        /// <summary>text, call, result, note.</summary>
        public string Kind { get; set; } = "";
        public string? Tool { get; set; }
        public string Text { get; set; } = "";
        public bool Error { get; set; }
        /// <summary>Picture file names in the cycle's folder (a render the agent looked at, an icon it made).</summary>
        public List<string> Images { get; set; } = new();
    }

    /// <summary>The cycle a game is running now (or ran last): who, how far, and its most recent steps.</summary>
    public sealed class LiveCycle
    {
        public const int MaxEntries = 120;

        public string CycleId { get; set; } = "";
        public string GameId { get; set; } = "";
        public string Role { get; set; } = "";
        public GameStage Stage { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime LastUtc { get; set; }
        public bool Running { get; set; }
        public string? Outcome { get; set; }
        /// <summary>What it is doing right now, e.g. "running build" or "thinking".</summary>
        public string Status { get; set; } = "";
        public int Turns { get; set; }
        public int ToolCalls { get; set; }
        public int ToolErrors { get; set; }
        public long PromptTokens { get; set; }
        public long CachedTokens { get; set; }
        public long CompletionTokens { get; set; }
        /// <summary>The size of the last request: how full the cycle's context is.</summary>
        public long ContextTokens { get; set; }
        public List<LiveEntry> Entries { get; set; } = new();
    }

    /// <summary>The cycles running now, one per game, kept in memory for the dashboard's live view.</summary>
    public sealed class LiveBoard
    {
        private readonly object gate = new();
        private readonly Dictionary<string, LiveCycle> byGame = new(StringComparer.Ordinal);

        public LiveCycle? Get(string gameId)
        {
            lock (gate) return byGame.TryGetValue(gameId, out var c) ? OmniBloxStore.Clone(c) : null;
        }

        public List<LiveCycle> Running()
        {
            lock (gate) return byGame.Values.Where(c => c.Running).Select(OmniBloxStore.Clone).ToList();
        }

        /// <summary>The game's cycle without its entries: cheap enough for every row of a list.</summary>
        public LiveCycle? Peek(string gameId)
        {
            lock (gate)
            {
                if (!byGame.TryGetValue(gameId, out var c)) return null;
                return new LiveCycle
                {
                    CycleId = c.CycleId, GameId = c.GameId, Role = c.Role, Stage = c.Stage, StartedUtc = c.StartedUtc, LastUtc = c.LastUtc, Running = c.Running,
                    Outcome = c.Outcome, Status = c.Status, Turns = c.Turns, ToolCalls = c.ToolCalls, ToolErrors = c.ToolErrors,
                    PromptTokens = c.PromptTokens, CachedTokens = c.CachedTokens, CompletionTokens = c.CompletionTokens, ContextTokens = c.ContextTokens,
                };
            }
        }

        internal void Update(string gameId, Action<LiveCycle> change)
        {
            lock (gate)
            {
                if (!byGame.TryGetValue(gameId, out var c)) byGame[gameId] = c = new LiveCycle { GameId = gameId };
                change(c);
                if (c.Entries.Count > LiveCycle.MaxEntries) c.Entries.RemoveRange(0, c.Entries.Count - LiveCycle.MaxEntries);
            }
        }
    }

    /// <summary>
    /// Records a cycle for the owner: the live view while it runs, and a full log on disk
    /// (games/&lt;id&gt;/cycles/&lt;cycle&gt;.jsonl, pictures alongside) that the dashboard opens later.
    /// The newest <see cref="KeepLogs"/> cycles of each game are kept.
    /// </summary>
    public sealed class CycleLog : ICycleObserver
    {
        public const int KeepLogs = 60;
        private const int LiveClip = 700;
        private const int LogClip = 24_000;

        private readonly StudioContext ctx;
        private readonly string gameId;
        private readonly object gate = new();
        private string cycleId = "";
        private string? logPath;
        private string? imageDir;
        private int imageCount;

        public CycleLog(StudioContext ctx, string gameId)
        {
            this.ctx = ctx;
            this.gameId = gameId;
        }

        public static string LogsDirectory(StudioContext ctx, string gameId) => Path.Combine(ctx.Store.GameDirectory(gameId), "cycles");

        public void Started(string id, string role, GameStage stage)
        {
            var now = ctx.Clock();
            lock (gate)
            {
                cycleId = id;
                string dir = LogsDirectory(ctx, gameId);
                Directory.CreateDirectory(dir);
                logPath = Path.Combine(dir, id + ".jsonl");
                imageDir = Path.Combine(dir, id);
                imageCount = 0;
            }
            ctx.Live.Update(gameId, c =>
            {
                c.CycleId = id;
                c.Role = role;
                c.Stage = stage;
                c.StartedUtc = now;
                c.LastUtc = now;
                c.Running = true;
                c.Outcome = null;
                c.Status = "reading the briefing";
                c.Turns = c.ToolCalls = c.ToolErrors = 0;
                c.PromptTokens = c.CachedTokens = c.CompletionTokens = c.ContextTokens = 0;
                c.Entries = new List<LiveEntry>();
            });
            Append(new LiveEntry { Utc = now, Kind = "note", Text = $"{role} cycle started ({stage})." });
            Prune();
        }

        public void AssistantText(string text) => Append(new LiveEntry { Utc = ctx.Clock(), Kind = "text", Text = text.Trim() });

        public void ToolCall(string callId, string name, string argumentsJson)
        {
            ctx.Live.Update(gameId, c => { c.ToolCalls++; c.Status = "running " + name; });
            Append(new LiveEntry { Utc = ctx.Clock(), Kind = "call", Tool = name, Text = argumentsJson });
        }

        public void ToolResult(string callId, string name, string text, bool isError, IReadOnlyList<byte[]> images)
        {
            var files = new List<string>();
            foreach (var png in images.Take(8)) if (SaveImage(png) is string f) files.Add(f);
            if (isError) ctx.Live.Update(gameId, c => c.ToolErrors++);
            ctx.Live.Update(gameId, c => c.Status = "thinking");
            Append(new LiveEntry { Utc = ctx.Clock(), Kind = "result", Tool = name, Text = text, Error = isError, Images = files });
        }

        public void Note(string text) => Append(new LiveEntry { Utc = ctx.Clock(), Kind = "note", Text = text });

        public void Usage(long promptTokens, long cachedTokens, long completionTokens) => ctx.Live.Update(gameId, c =>
        {
            c.Turns++;
            c.PromptTokens += promptTokens;
            c.CachedTokens += cachedTokens;
            c.CompletionTokens += completionTokens;
            c.ContextTokens = promptTokens;
            c.LastUtc = ctx.Clock();
        });

        public void Finished(CycleReport report)
        {
            Append(new LiveEntry { Utc = ctx.Clock(), Kind = "note", Text = $"Cycle {report.Outcome}: {report.Done}" });
            ctx.Live.Update(gameId, c =>
            {
                c.Running = false;
                c.Outcome = report.Outcome;
                c.Status = report.Outcome;
                c.LastUtc = ctx.Clock();
            });
        }

        private void Append(LiveEntry entry)
        {
            var live = new LiveEntry { Utc = entry.Utc, Kind = entry.Kind, Tool = entry.Tool, Text = Clip(entry.Text, LiveClip), Error = entry.Error, Images = entry.Images };
            ctx.Live.Update(gameId, c => { c.Entries.Add(live); c.LastUtc = entry.Utc; });
            lock (gate)
            {
                if (logPath == null) return;
                try
                {
                    var line = new LiveEntry { Utc = entry.Utc, Kind = entry.Kind, Tool = entry.Tool, Text = Clip(entry.Text, LogClip), Error = entry.Error, Images = entry.Images };
                    File.AppendAllText(logPath, JsonConvert.SerializeObject(line, Formatting.None, OmniBloxStore.JsonSettings) + "\n", Encoding.UTF8);
                }
                catch (IOException ex)
                {
                    ctx.Log("[OmniBlox] cycle log write failed: " + ex.Message);
                }
            }
        }

        private string? SaveImage(byte[] png)
        {
            lock (gate)
            {
                if (imageDir == null) return null;
                try
                {
                    Directory.CreateDirectory(imageDir);
                    string name = $"{++imageCount:D3}.png";
                    File.WriteAllBytes(Path.Combine(imageDir, name), png);
                    return name;
                }
                catch (IOException)
                {
                    return null;
                }
            }
        }

        private void Prune()
        {
            try
            {
                string dir = LogsDirectory(ctx, gameId);
                var logs = Directory.EnumerateFiles(dir, "c*.jsonl").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(KeepLogs).ToList();
                foreach (var old in logs)
                {
                    File.Delete(old);
                    string pictures = Path.Combine(dir, Path.GetFileNameWithoutExtension(old));
                    if (Directory.Exists(pictures)) Directory.Delete(pictures, recursive: true);
                }
            }
            catch (IOException) { /* a log in use is pruned next time */ }
        }

        /// <summary>A cycle's full log, oldest first. Null when it is gone (pruned) or never existed.</summary>
        public static List<LiveEntry>? Read(StudioContext ctx, string gameId, string cycleId)
        {
            if (!IsCycleId(cycleId)) return null;
            string path = Path.Combine(LogsDirectory(ctx, gameId), cycleId + ".jsonl");
            if (!File.Exists(path)) return null;
            var list = new List<LiveEntry>();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                try
                {
                    if (JsonConvert.DeserializeObject<LiveEntry>(line, OmniBloxStore.JsonSettings) is { } e) list.Add(e);
                }
                catch (JsonException) { /* a half-written last line */ }
            }
            return list;
        }

        /// <summary>The cycles whose full log is still on disk, newest first.</summary>
        public static List<string> Available(StudioContext ctx, string gameId)
        {
            string dir = LogsDirectory(ctx, gameId);
            if (!Directory.Exists(dir)) return new List<string>();
            try
            {
                return Directory.EnumerateFiles(dir, "c*.jsonl").Select(f => Path.GetFileNameWithoutExtension(f)!).Where(IsCycleId)
                    .OrderByDescending(id => id, StringComparer.Ordinal).ToList();
            }
            catch (IOException)
            {
                return new List<string>();
            }
        }

        public static string? ImagePath(StudioContext ctx, string gameId, string cycleId, string file)
        {
            if (!IsCycleId(cycleId) || file.Length > 12 || !file.EndsWith(".png", StringComparison.Ordinal) || !file[..^4].All(char.IsDigit)) return null;
            string path = Path.Combine(LogsDirectory(ctx, gameId), cycleId, file);
            return File.Exists(path) ? path : null;
        }

        public static bool IsCycleId(string id) => id.Length is > 5 and < 40 && id[0] == 'c' && id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-');

        private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + $"… [{s.Length - max} more characters]";
    }
}
