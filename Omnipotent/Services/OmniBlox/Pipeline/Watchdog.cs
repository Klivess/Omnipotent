using System.Security.Cryptography;
using System.Text;
using Omnipotent.Services.OmniBlox.Core;

namespace Omnipotent.Services.OmniBlox.Pipeline
{
    /// <summary>
    /// Keeps a game moving without giving up on it. The first studio counted cycles and failures (8 test cycles,
    /// 6 failures) and abandoned the game on the second strike, so all three of its games died on a test-harness
    /// bug the agents could not fix. This watchdog measures progress instead: a cycle that changes the game's
    /// files, checklist, milestones or store page is progress; the same failure again with nothing changed is a
    /// stall. Stalls climb a ladder, and the top rung asks the owner. Nothing is ever abandoned automatically.
    ///
    /// 1. Rethink (3 stalls): the agents are told plainly that the approach is not working, shown the repeated
    ///    failure word for word, and reason harder (high effort).
    /// 2. Deep dive (5): the next cycle is a diagnosis: read the failing check's own source, question every
    ///    assumption, prove the cause before changing anything. A repeated failure inside the Game Kit's own
    ///    checks triggers a control run of the kit on its own to tell a kit bug from a game bug.
    /// 3. Ask the owner (8): the game pauses with an owner task explaining what keeps failing and what was
    ///    tried. Resuming (or steering) clears the ladder.
    /// </summary>
    public static class Watchdog
    {
        public const int RethinkAt = 3;
        public const int DeepDiveAt = 5;
        public const int AskOwnerAt = 8;
        public const int HistoryKept = 12;

        public static string LevelName(int level) => level switch { <= 0 => "moving", 1 => "rethink", 2 => "deep dive", _ => "waiting for the owner" };

        /// <summary>What progress looks like right now: any change to it means the game moved.</summary>
        public static string Mark(StudioContext ctx, GameDossier g)
        {
            var sb = new StringBuilder();
            sb.Append(g.Stage).Append('|');
            sb.Append(StageGates.Checklist(g, ctx).Count(c => c.Met)).Append('|');
            sb.Append(g.Milestones.Count(m => m.Done)).Append('|');
            sb.Append(g.Concepts.Count).Append('|').Append(g.Brief?.Title).Append('|');
            sb.Append(g.Store.Title).Append(g.Store.Description.Length).Append(g.Store.ThumbnailFiles.Count).Append(g.Store.IconFile).Append(g.Store.ComplianceOk).Append('|');
            sb.Append(g.Products.Count).Append('|');
            try
            {
                var ws = ctx.Workspace(g.GameId);
                if (ws.Exists) sb.Append(ws.ContentFingerprint());
            }
            catch (IOException) { }
            return Hash(sb.ToString());
        }

        /// <summary>A stable name for a set of failures, so "the same failure again" can be recognised.</summary>
        public static string? Signature(IEnumerable<string> failures)
        {
            var list = failures.Select(f => Normalize(f)).Where(f => f.Length > 0).Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();
            return list.Count == 0 ? null : Hash(string.Join("\n", list));
        }

        private static string Normalize(string failure)
        {
            // Revision numbers, times and counts change between runs of the same failure.
            var s = System.Text.RegularExpressions.Regex.Replace(failure.ToLowerInvariant(), @"\b(r|rev|revision )\d+\b", "r#");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\d{4}-\d\d-\d\d[ t]\d\d:\d\d(:\d\d)?", "time");
            return s.Trim();
        }

        /// <summary>
        /// Called after every cycle and every test run. <paramref name="failures"/> are this attempt's blocking
        /// problems (empty when it went well). Returns the ladder level the game is now on.
        /// </summary>
        public static int Observe(StudioContext ctx, string gameId, IEnumerable<string> failures, string what)
        {
            var game = ctx.Game(gameId);
            if (game == null) return 0;
            string mark = Mark(ctx, game);
            string? signature = Signature(failures);
            int level = 0;
            string? newDirective = null;
            bool askOwner = false;
            string summary = "";
            ctx.Update(gameId, g =>
            {
                var p = g.Progress;
                bool moved = p.LastMark != mark;
                p.LastMark = mark;
                if (signature == null)
                {
                    p.SameFailureCount = 0;
                    p.LastFailureSignature = null;
                }
                else if (signature == p.LastFailureSignature)
                {
                    p.SameFailureCount++;
                }
                else
                {
                    p.LastFailureSignature = signature;
                    p.SameFailureCount = 1;
                }
                p.StalledCycles = moved ? 0 : p.StalledCycles + 1;
                if (moved && signature == null)
                {
                    p.LastProgressUtc = ctx.Clock();
                    if (p.Level > 0) p.History.Add($"{ctx.Clock():MM-dd HH:mm} moving again after {what}; ladder reset");
                    p.Level = 0;
                    p.Directive = null;
                }
                int pressure = Math.Max(p.SameFailureCount, p.StalledCycles);
                int wanted = pressure >= AskOwnerAt ? 3 : pressure >= DeepDiveAt ? 2 : pressure >= RethinkAt ? 1 : 0;
                if (wanted > p.Level)
                {
                    p.Level = wanted;
                    var repeated = failures.Take(6).ToList();
                    summary = repeated.Count > 0 ? string.Join("; ", repeated) : "no visible progress (the game's files, checklist and milestones did not change)";
                    newDirective = wanted switch
                    {
                        1 => $"WATCHDOG (rethink): the last {pressure} attempts ended the same way — {Clip(summary, 700)}. The approach so far is not working. Re-read the exact failure, find the code that produces it (diagnose_failure shows the failing check's own source), and change approach instead of repeating it.",
                        2 => $"WATCHDOG (deep dive): {pressure} attempts in a row ended the same way — {Clip(summary, 700)}. Spend this cycle on diagnosis only until you have PROVED the cause: run diagnose_failure, read the check and the code it exercises, run run_tests after each single change, and write down what each experiment showed. Do not declare anything complete until the failure is gone.",
                        _ => null,
                    };
                    if (newDirective != null) p.Directive = newDirective;
                    p.History.Add($"{ctx.Clock():MM-dd HH:mm} level {wanted} after {what}: {Clip(summary, 300)}");
                    if (p.History.Count > HistoryKept) p.History.RemoveRange(0, p.History.Count - HistoryKept);
                    askOwner = wanted == 3;
                }
                level = p.Level;
            });

            if (newDirective != null)
                ctx.Event(EventLevel.Warning, "watchdog", $"\"{game.DisplayTitle}\" is stalling ({what}); the watchdog {(level == 1 ? "asked for a new approach" : "started a deep dive")}.", gameId);
            if (askOwner)
                AskOwner(ctx, gameId, summary);
            return level;
        }

        /// <summary>The top of the ladder: pause and ask, with everything the owner needs to decide.</summary>
        public static void AskOwner(StudioContext ctx, string gameId, string why)
        {
            var now = ctx.Clock();
            string title = "";
            ctx.Store.Mutate(s =>
            {
                var g = s.Game(gameId)!;
                title = g.DisplayTitle;
                g.Paused = true;
                g.WaitingReason = "paused by the watchdog: the same problem kept coming back — see the owner task";
                g.Escalations++;
                var tried = g.Cycles.TakeLast(4).Select(c => $"- {c.Utc:MM-dd HH:mm} {c.Role}: {Clip(c.Done, 260)}");
                s.FileOwnerTask(OwnerTaskKind.Escalation, "stuck:" + gameId, $"OmniBlox is stuck on \"{g.DisplayTitle}\" ({g.Stage})",
                    $"The same problem kept coming back and the agents could not get past it on their own:\n{Clip(why, 900)}\n\nWhat the last cycles tried:\n{string.Join("\n", tried)}\n\n" +
                    "Resume to let it try again (the watchdog starts over), steer it with a direction, or abandon the game. Nothing is lost while it waits.", gameId, now);
                s.Touch(g, now);
                s.AddEvent(EventLevel.Error, "watchdog.paused", $"Paused \"{g.DisplayTitle}\": the same problem kept coming back. It waits for you.", gameId, now);
            });
            _ = ctx.Alert($"OmniBlox paused \"{title}\": the same problem kept coming back ({Clip(why, 200)}). It is waiting for you on the dashboard.");
        }

        /// <summary>The owner resumed or steered: the ladder starts over.</summary>
        public static void Reset(GameDossier g, DateTime now, string reason)
        {
            g.Progress.Level = 0;
            g.Progress.StalledCycles = 0;
            g.Progress.SameFailureCount = 0;
            g.Progress.Directive = null;
            g.Progress.History.Add($"{now:MM-dd HH:mm} reset: {reason}");
            if (g.Progress.History.Count > HistoryKept) g.Progress.History.RemoveRange(0, g.Progress.History.Count - HistoryKept);
        }

        private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..20].ToLowerInvariant();

        private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    }
}
