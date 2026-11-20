using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Kernel
{
    /// <summary>One standing problem a part of the engine reports about itself.</summary>
    public sealed record ModuleProblem(string ModuleId, string Problem, string Severity, string DetailJson, long SinceMs, long LastMs);

    /// <summary>
    /// Standing problems the engine's own parts report about themselves, for the health check to show (table
    /// <c>module_problems</c> in intel.db). A job that cannot do its work for a reason retrying will not change (no room
    /// on the disk for a backup) says so here and ends, rather than failing every night as a dead job nobody can tell from
    /// the others; the problem stays listed until whoever reported it clears it.
    /// </summary>
    public static class ModuleProblems
    {
        /// <summary>The engine itself (the job kernel), as opposed to a module.</summary>
        public const string Kernel = "kernel";

        public static Task ReportAsync(SqliteStore intel, IClock clock, string moduleId, string problem, string severity, object detail, CancellationToken ct = default)
        {
            string json = JsonSerializer.Serialize(detail, KernelJson.Options);
            long now = clock.NowMs;
            return intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO module_problems(module_id, problem, severity, detail_json, since_ms, last_ms) VALUES($m,$p,$s,$d,$n,$n)
                ON CONFLICT(module_id, problem) DO UPDATE SET severity=excluded.severity, detail_json=excluded.detail_json, last_ms=excluded.last_ms
                """, ("$m", moduleId), ("$p", problem), ("$s", severity), ("$d", json), ("$n", now)), ct);
        }

        public static Task ClearAsync(SqliteStore intel, string moduleId, string problem, CancellationToken ct = default) =>
            intel.WriteAsync((c, tx) => c.Exec(tx, "DELETE FROM module_problems WHERE module_id=$m AND problem=$p", ("$m", moduleId), ("$p", problem)), ct);

        public static IReadOnlyList<ModuleProblem> Open(SqliteStore intel)
        {
            using var c = intel.OpenRead();
            return c.Query(null, "SELECT module_id, problem, severity, detail_json, since_ms, last_ms FROM module_problems ORDER BY module_id, problem",
                r => new ModuleProblem(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5)));
        }
    }
}
