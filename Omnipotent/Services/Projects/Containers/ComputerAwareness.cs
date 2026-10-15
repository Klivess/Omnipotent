using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// What an agent should know about its own computer without having to ask: whether it is awake,
    /// what the browser has open, and what happened to it while the agent was not looking (a crash, an
    /// OOM kill, a wedged browser the daemon restarted).
    ///
    /// Two consumers. The wake seed gets a compact "YOUR COMPUTER" block, so a new wake resumes on the
    /// tab it left instead of re-navigating and re-inspecting to rediscover it. Tool results get a one-line
    /// notice of new events, so "the page I was on is gone" arrives with its cause attached instead of
    /// as a mysterious failure the agent retries.
    /// </summary>
    public static class ComputerAwareness
    {
        /// <summary>Per desktop, the newest daemon event already shown to an agent.</summary>
        private static readonly ConcurrentDictionary<string, long> SeenEventSeq = new(StringComparer.Ordinal);

        /// <summary>Event kinds worth interrupting an agent for.</summary>
        private static readonly HashSet<string> NotableKinds = new(StringComparer.Ordinal)
        {
            "oom-kill", "browser-exited", "browser-hung-killed",
        };

        internal static void ResetForTests() => SeenEventSeq.Clear();

        /// <summary>
        /// New notable events since the last time any agent was told about this desktop, as one line, or
        /// "" when there is nothing new. Marks them seen. A daemon that restarted (its sequence went back
        /// down) resets the cursor rather than silencing everything until it catches up.
        /// </summary>
        public static string TakeNewEvents(string containerID, ComputerDaemonHealth? health)
        {
            if (health == null || health.Events.Count == 0) return "";
            long newest = health.Events.Max(e => e.Seq);
            bool firstLook = !SeenEventSeq.TryGetValue(containerID, out long seen);
            if (!firstLook && newest < seen) seen = 0;   // daemon restarted: its counter started over
            // The first look after a harness restart has no cursor: report only what is recent enough to
            // still matter, rather than replaying the daemon's whole journal as news.
            var fresh = health.Events.Where(e => NotableKinds.Contains(e.Kind)
                && (firstLook ? IsRecent(e.At, TimeSpan.FromMinutes(15)) : e.Seq > seen)).ToList();
            SeenEventSeq[containerID] = newest;
            if (fresh.Count == 0) return "";
            return "COMPUTER EVENTS since your last look: " + string.Join(" ", fresh.Select(Describe));
        }

        private static bool IsRecent(string at, TimeSpan window) =>
            DateTime.TryParse(at, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var when)
            && DateTime.UtcNow - when <= window;

        private static string Describe(ComputerDaemonHealth.DaemonEvent e)
        {
            string at = e.At.Length >= 16 ? e.At[11..16] + "Z" : e.At;
            string tabs = KnownTabs(e.Details);
            return e.Kind switch
            {
                "oom-kill" => $"[{at}] the kernel killed a process for memory (this desktop is at its RAM ceiling — close tabs you do not need).",
                "browser-exited" => $"[{at}] Chromium exited (crash or OOM); the next browser action relaunches it.{tabs}",
                "browser-hung-killed" => $"[{at}] Chromium stopped responding and was restarted by the desktop's watchdog.{tabs}",
                _ => $"[{at}] {e.Message}",
            };
        }

        private static string KnownTabs(JsonElement details)
        {
            if (details.ValueKind != JsonValueKind.Object || !details.TryGetProperty("lastKnownTabs", out var tabs)
                || tabs.ValueKind != JsonValueKind.Array || tabs.GetArrayLength() == 0)
                return "";
            var urls = tabs.EnumerateArray()
                .Select(t => t.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null)
                .Where(u => !string.IsNullOrWhiteSpace(u) && !u!.StartsWith("chrome", StringComparison.Ordinal))
                .Take(4)
                .Select(u => Truncate(u!, 120))
                .ToList();
            return urls.Count == 0 ? "" : " Tabs it had open: " + string.Join(" | ", urls) + ".";
        }

        /// <summary>
        /// The seed block for one agent's desktop. Never provisions or wakes anything: an absent desktop is
        /// described as absent, a sleeping one as sleeping. Bounded by the caller's token.
        /// </summary>
        public static async Task<string> DescribeForSeedAsync(DesktopContainerRecord? record, ComputerDaemonClient? daemon,
            CancellationToken ct)
        {
            if (record == null) return "";
            var sb = new StringBuilder();
            string id = record.ContainerID[..Math.Min(12, record.ContainerID.Length)];
            if (record.Lost)
                return $"Desktop {id} is missing (its container is gone). Your next computer tool provisions a fresh one; files in /project are intact.";
            if (record.Suspended)
                return $"Desktop {id} is asleep (stopped while idle to save memory). Your next computer tool wakes it with its apps, files and browser sign-ins intact.";
            if (daemon == null || !daemon.Configured)
                return $"Desktop {id} is running ({record.Width}x{record.Height}).";

            var health = await daemon.HealthAsync(ct);
            if (health == null)
                return $"Desktop {id} is running, but its control daemon is not answering; computer tools fall back to slower paths.";

            var memory = health.Resources?.Memory;
            sb.Append($"Desktop {id}: {record.Width}x{record.Height}");
            if (memory?.UsedMB is { } used && memory.LimitMB is { } limit)
                sb.Append($" · memory {used:0}/{limit:0} MB");
            if (memory?.OomKills is > 0)
                sb.Append($" · {memory.OomKills} OOM kill(s) so far");
            sb.AppendLine(".");

            if (health.Browser is { Process: true, Cdp: true })
            {
                string? state = await daemon.GetJsonAsync("/browser/state", "browser-state", ct, timeoutSeconds: 5);
                var tabs = ParseTabs(state, out string? activeID);
                if (tabs.Count == 0)
                    sb.AppendLine("Browser: running, no page tabs open.");
                else
                {
                    sb.AppendLine($"Browser: running with {tabs.Count} tab(s) — your session and sign-ins are live, so continue from them instead of re-navigating:");
                    foreach (var tab in tabs.Take(6))
                        sb.AppendLine($"  {(tab.ID == activeID ? "▶" : "·")} {Truncate(tab.Title, 70)} — {Truncate(tab.Url, 140)}");
                    if (tabs.Count > 6) sb.AppendLine($"  … {tabs.Count - 6} more");
                }
            }
            else if (health.Browser is { Process: true })
                sb.AppendLine("Browser: process running but not answering yet (starting, or busy).");
            else
                sb.AppendLine("Browser: not running — browser op:navigate or op:open starts it with your saved profile.");

            string events = TakeNewEvents(record.ContainerID, health);
            if (events.Length > 0) sb.AppendLine(events);
            return sb.ToString().TrimEnd();
        }

        private sealed record Tab(string ID, string Title, string Url);

        private static List<Tab> ParseTabs(string? json, out string? activeID)
        {
            activeID = null;
            var tabs = new List<Tab>();
            if (string.IsNullOrWhiteSpace(json)) return tabs;
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.TryGetProperty("activeTabId", out var active) && active.ValueKind == JsonValueKind.String)
                    activeID = active.GetString();
                if (root.TryGetProperty("tabs", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var tab in list.EnumerateArray())
                    {
                        string Str(string name) => tab.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                        string url = Str("url");
                        if (url.Length == 0 || url.StartsWith("chrome-extension://", StringComparison.Ordinal)) continue;
                        tabs.Add(new Tab(Str("id"), Str("title").Length > 0 ? Str("title") : "(untitled)", url));
                    }
            }
            catch (JsonException) { }
            return tabs;
        }

        private static string Truncate(string text, int max) =>
            string.IsNullOrEmpty(text) || text.Length <= max ? text ?? "" : text[..(max - 1)] + "…";
    }
}
