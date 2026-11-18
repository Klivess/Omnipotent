using Omnipotent.Services.KliveAgent.Models;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.KliveAgent
{
    /// <summary>
    /// Script health over time: what failed and why, what the engine fixed itself, and how long compiling
    /// took. Until 2026-11-11 the only figures were scripts and failures per day, and a lifetime count
    /// per error code that no route served. That is how a 44% failure rate, a 4.5 s first compile and
    /// nine multi-minute compiles went unseen until someone read the stored conversations.
    /// </summary>
    public partial class KliveAgentStats
    {
        /// <summary>Upper bounds (ms) of the compile-time histogram; the last bucket is everything above.</summary>
        internal static readonly int[] CompileBucketsMs = { 50, 100, 250, 500, 1000, 2000, 5000, 10000, 30000 };

        private const int ScriptHealthDaysKept = 180;
        private const int RecentFailuresKept = 40;

        private readonly ConcurrentDictionary<string, ScriptHealthDay> _scriptHealth = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, long> _repairKinds = new(StringComparer.Ordinal);
        private readonly List<ScriptFailureExample> _recentScriptFailures = new();
        private readonly object _scriptHealthLock = new();

        /// <summary>One day of script outcomes. Plain counters so it round-trips through the stats file.</summary>
        public sealed class ScriptHealthDay
        {
            public string Date { get; set; } = "";
            public long Total { get; set; }
            public long Compile { get; set; }
            public long Runtime { get; set; }
            public long Timeout { get; set; }
            public long CompileTimeout { get; set; }
            public long Stopped { get; set; }
            /// <summary>Scripts that ran only because the engine fixed them first.</summary>
            public long Repaired { get; set; }
            /// <summary>Scripts the engine changed that still failed.</summary>
            public long RepairedButFailed { get; set; }
            public long ColdCompiles { get; set; }
            public long ColdCompileTotalMs { get; set; }
            public long ColdCompileMaxMs { get; set; }
            public long WarmCompiles { get; set; }
            public long WarmCompileTotalMs { get; set; }
            public long WarmCompileMaxMs { get; set; }
            public long RunTotalMs { get; set; }
            public int LongestFailureStreak { get; set; }
            public long[] ColdHistogram { get; set; } = new long[CompileBucketsMs.Length + 1];
            public long[] WarmHistogram { get; set; } = new long[CompileBucketsMs.Length + 1];

            public long Failed => Compile + Runtime + Timeout + CompileTimeout;
        }

        /// <summary>A recent failure, reduced to what names it. Never the script or its output: scripts carry
        /// whatever the task handled, and this is served on a route.</summary>
        public sealed class ScriptFailureExample
        {
            public DateTime AtUtc { get; set; }
            public string Kind { get; set; } = "";
            public string Code { get; set; } = "";
            public string Summary { get; set; } = "";
        }

        private const int SlowToolsKept = 40;
        private readonly List<SlowToolCall> _slowTools = new();

        /// <summary>A tool call that took a minute or more, or ran into its deadline.</summary>
        public sealed class SlowToolCall
        {
            public DateTime AtUtc { get; set; }
            public string Tool { get; set; } = "";
            public string? Operation { get; set; }
            public long DurationMs { get; set; }
            public bool TimedOut { get; set; }
        }

        /// <summary>Keeps the last slow tool calls on disk, so a hung operation can be named after a restart.</summary>
        public void RecordSlowTool(string tool, string? operation, long durationMs, bool timedOut)
        {
            lock (_scriptHealthLock)
            {
                _slowTools.Add(new SlowToolCall { AtUtc = DateTime.UtcNow, Tool = tool, Operation = operation, DurationMs = durationMs, TimedOut = timedOut });
                if (_slowTools.Count > SlowToolsKept) _slowTools.RemoveRange(0, _slowTools.Count - SlowToolsKept);
            }
            QueueSave();
        }

        /// <summary>Records one finished script: its outcome class, whether a repair saved it, its compile
        /// and run time, and the failure streak it ended on.</summary>
        public void RecordScriptOutcome(AgentScriptResult result, int consecutiveFailed = 0)
        {
            if (result == null) return;
            var day = _scriptHealth.GetOrAdd(DayKey(DateTime.UtcNow), key => new ScriptHealthDay { Date = key });
            bool repaired = result.Repairs is { Count: > 0 };
            lock (day)
            {
                day.Total++;
                switch (result.Success ? null : result.FailureKind ?? "runtime")
                {
                    case null: if (repaired) day.Repaired++; break;
                    case "compile": day.Compile++; break;
                    case "timeout": day.Timeout++; break;
                    case "compile-timeout": day.CompileTimeout++; break;
                    case "stopped": day.Stopped++; break;
                    default: day.Runtime++; break;
                }
                if (repaired && !result.Success) day.RepairedButFailed++;
                if (result.CompileMs is long compileMs)
                {
                    int bucket = BucketOf(compileMs);
                    if (result.ColdStart)
                    {
                        day.ColdCompiles++;
                        day.ColdCompileTotalMs += compileMs;
                        day.ColdCompileMaxMs = Math.Max(day.ColdCompileMaxMs, compileMs);
                        day.ColdHistogram[bucket]++;
                    }
                    else
                    {
                        day.WarmCompiles++;
                        day.WarmCompileTotalMs += compileMs;
                        day.WarmCompileMaxMs = Math.Max(day.WarmCompileMaxMs, compileMs);
                        day.WarmHistogram[bucket]++;
                    }
                }
                if (result.RunMs is long runMs) day.RunTotalMs += runMs;
                day.LongestFailureStreak = Math.Max(day.LongestFailureStreak, consecutiveFailed);
            }

            if (repaired)
                foreach (var kind in result.Repairs!.Select(RepairKindOf).Distinct())
                    _repairKinds.AddOrUpdate(kind, 1, (_, n) => n + 1);

            if (!result.Success && result.FailureKind != "stopped")
            {
                var example = new ScriptFailureExample
                {
                    AtUtc = DateTime.UtcNow,
                    Kind = result.FailureKind ?? "runtime",
                    Code = KliveAgentBrain.ClassifyScriptError(result.ErrorMessage),
                    Summary = SummariseFailure(result.ErrorMessage),
                };
                lock (_scriptHealthLock)
                {
                    _recentScriptFailures.Add(example);
                    if (_recentScriptFailures.Count > RecentFailuresKept)
                        _recentScriptFailures.RemoveRange(0, _recentScriptFailures.Count - RecentFailuresKept);
                }
            }

            foreach (var old in _scriptHealth.Keys.OrderByDescending(k => k, StringComparer.Ordinal).Skip(ScriptHealthDaysKept).ToList())
                _scriptHealth.TryRemove(old, out _);
            QueueSave();
            WatchScript(result);
        }

        private static int BucketOf(long ms)
        {
            for (int i = 0; i < CompileBucketsMs.Length; i++)
                if (ms < CompileBucketsMs[i]) return i;
            return CompileBucketsMs.Length;
        }

        /// <summary>Which rule a repair note came from, by the words every note of that rule carries.</summary>
        internal static string RepairKindOf(string note)
        {
            if (note.Contains("`using var`", StringComparison.Ordinal)) return "top-level using declaration";
            if (note.Contains("await Aw(", StringComparison.Ordinal)) return "await of a cast, parenthesis or object";
            if (note.Contains("added `using ", StringComparison.Ordinal)) return "missing namespace";
            if (note.Contains("moved to the top", StringComparison.Ordinal)) return "using directive below a statement";
            if (note.Contains("did not exist", StringComparison.Ordinal)) return "assignment to an undeclared name";
            if (note.Contains("`await` was added", StringComparison.Ordinal)) return "missing await";
            if (note.Contains("internal to Omnipotent", StringComparison.Ordinal) || note.Contains("is not public", StringComparison.Ordinal))
                return "internal type or non-public member";
            if (note.Contains("lambda passed to a dynamic call", StringComparison.Ordinal)) return "untyped lambda in a dynamic call";
            if (note.Contains("typed `object`", StringComparison.Ordinal)) return "member of an object-typed value";
            return "other";
        }

        private static readonly Regex DiagnosticLine = new(@"^\[CS\d{3,5}\][^\n]*", RegexOptions.Multiline | RegexOptions.Compiled);

        /// <summary>The compiler's own sentence, or the exception type and message. Never source lines.</summary>
        internal static string SummariseFailure(string? error)
        {
            if (string.IsNullOrWhiteSpace(error)) return "";
            var diagnostic = DiagnosticLine.Match(error);
            string line = diagnostic.Success ? diagnostic.Value : error.Replace("\r", "").Split('\n')[0];
            line = line.Trim();
            return line.Length <= 180 ? line : line[..179] + "…";
        }

        /// <summary>The script-health view behind GET /kliveagent/stats/scripts.</summary>
        public object BuildScriptHealth(int days = 30)
        {
            days = Math.Clamp(days, 1, ScriptHealthDaysKept);
            string from = DayKey(DateTime.UtcNow.Date.AddDays(-(days - 1)));
            var window = _scriptHealth.Values.Where(d => string.CompareOrdinal(d.Date, from) >= 0)
                .OrderBy(d => d.Date, StringComparer.Ordinal).Select(Copy).ToList();

            var weeks = window.GroupBy(d => WeekKey(DateTime.ParseExact(d.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture)))
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => Project("week", g.Key, Sum(g))).ToList();

            List<ScriptFailureExample> recent;
            List<SlowToolCall> slow;
            lock (_scriptHealthLock)
            {
                recent = _recentScriptFailures.AsEnumerable().Reverse().ToList();
                slow = _slowTools.AsEnumerable().Reverse().ToList();
            }

            return new
            {
                windowDays = days,
                trackingSince = _scriptHealth.Keys.OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault(),
                totals = Project("window", $"{days}d", Sum(window)),
                days = window.Select(d => Project("date", d.Date, d)).ToList(),
                weeks,
                repairKinds = _repairKinds.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => new { kind = kv.Key, count = kv.Value }).ToList(),
                topErrorCodes = _scriptErrorCodes.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .Take(12).Select(kv => new { code = kv.Key, count = kv.Value }).ToList(),
                recentFailures = recent.Select(f => new { at = f.AtUtc, kind = f.Kind, code = f.Code, summary = f.Summary }).ToList(),
                slowTools = slow.Select(t => new { at = t.AtUtc, tool = t.Tool, operation = t.Operation, durationMs = t.DurationMs, timedOut = t.TimedOut }).ToList(),
                compileBucketsMs = CompileBucketsMs,
                watch = BuildWatch(),
            };
        }

        private static ScriptHealthDay Copy(ScriptHealthDay day)
        {
            lock (day) return Sum(new[] { day }, day.Date);
        }

        private static ScriptHealthDay Sum(IEnumerable<ScriptHealthDay> days, string date = "")
        {
            var total = new ScriptHealthDay { Date = date };
            foreach (var d in days)
            {
                total.Total += d.Total;
                total.Compile += d.Compile;
                total.Runtime += d.Runtime;
                total.Timeout += d.Timeout;
                total.CompileTimeout += d.CompileTimeout;
                total.Stopped += d.Stopped;
                total.Repaired += d.Repaired;
                total.RepairedButFailed += d.RepairedButFailed;
                total.ColdCompiles += d.ColdCompiles;
                total.ColdCompileTotalMs += d.ColdCompileTotalMs;
                total.ColdCompileMaxMs = Math.Max(total.ColdCompileMaxMs, d.ColdCompileMaxMs);
                total.WarmCompiles += d.WarmCompiles;
                total.WarmCompileTotalMs += d.WarmCompileTotalMs;
                total.WarmCompileMaxMs = Math.Max(total.WarmCompileMaxMs, d.WarmCompileMaxMs);
                total.RunTotalMs += d.RunTotalMs;
                total.LongestFailureStreak = Math.Max(total.LongestFailureStreak, d.LongestFailureStreak);
                for (int i = 0; i < total.ColdHistogram.Length; i++)
                {
                    if (i < (d.ColdHistogram?.Length ?? 0)) total.ColdHistogram[i] += d.ColdHistogram![i];
                    if (i < (d.WarmHistogram?.Length ?? 0)) total.WarmHistogram[i] += d.WarmHistogram![i];
                }
            }
            return total;
        }

        private static Dictionary<string, object?> Project(string keyName, string key, ScriptHealthDay d)
        {
            long counted = d.Total - d.Stopped;
            return new Dictionary<string, object?>
            {
                [keyName] = key,
                ["total"] = d.Total,
                ["failed"] = d.Failed,
                ["compile"] = d.Compile,
                ["runtime"] = d.Runtime,
                ["timeout"] = d.Timeout,
                ["compileTimeout"] = d.CompileTimeout,
                ["stopped"] = d.Stopped,
                ["repaired"] = d.Repaired,
                ["repairedButFailed"] = d.RepairedButFailed,
                // A stopped script is nobody's failure, so it is left out of the rate.
                ["firstPassPct"] = counted > 0 ? Math.Round((double)(counted - d.Failed) / counted * 100.0, 1) : (double?)null,
                ["failurePct"] = counted > 0 ? Math.Round((double)d.Failed / counted * 100.0, 1) : (double?)null,
                ["coldCompiles"] = d.ColdCompiles,
                ["coldCompileAvgMs"] = d.ColdCompiles > 0 ? d.ColdCompileTotalMs / d.ColdCompiles : (long?)null,
                ["coldCompileP50Ms"] = Percentile(d.ColdHistogram, 0.50),
                ["coldCompileP95Ms"] = Percentile(d.ColdHistogram, 0.95),
                ["coldCompileMaxMs"] = d.ColdCompiles > 0 ? d.ColdCompileMaxMs : (long?)null,
                ["warmCompiles"] = d.WarmCompiles,
                ["warmCompileAvgMs"] = d.WarmCompiles > 0 ? d.WarmCompileTotalMs / d.WarmCompiles : (long?)null,
                ["warmCompileP50Ms"] = Percentile(d.WarmHistogram, 0.50),
                ["warmCompileP95Ms"] = Percentile(d.WarmHistogram, 0.95),
                ["warmCompileMaxMs"] = d.WarmCompiles > 0 ? d.WarmCompileMaxMs : (long?)null,
                ["runAvgMs"] = d.Total > 0 ? d.RunTotalMs / d.Total : (long?)null,
                ["longestFailureStreak"] = d.LongestFailureStreak,
            };
        }

        /// <summary>The upper bound of the bucket a percentile falls in (so "under 250 ms"), or null with no samples.</summary>
        internal static int? Percentile(long[]? histogram, double p)
        {
            if (histogram == null) return null;
            long total = histogram.Sum();
            if (total == 0) return null;
            long target = (long)Math.Ceiling(total * p);
            long seen = 0;
            for (int i = 0; i < histogram.Length; i++)
            {
                seen += histogram[i];
                if (seen >= target) return i < CompileBucketsMs.Length ? CompileBucketsMs[i] : CompileBucketsMs[^1] * 2;
            }
            return CompileBucketsMs[^1] * 2;
        }
    }
}
