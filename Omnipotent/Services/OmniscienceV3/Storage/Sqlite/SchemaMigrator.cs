using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Storage.Sqlite
{
    /// <summary>One schema step owned by one module. Versions start at 1 and are contiguous per module.</summary>
    public sealed record Migration(string ModuleId, int Version, string Description, string Sql)
    {
        public string Checksum => SchemaMigrator.ChecksumOf(Sql);
    }

    /// <summary>
    /// Per-module, checksum-verified migrations (plan: "per-module migrations"; legacy defects F015/F374:
    /// ALTER-based, non-idempotent, unverified). Each module owns its tables and its version line, so
    /// parallel workstreams never edit a shared migration list. An already-applied migration whose SQL
    /// changed is a hard error: shipped schema steps are immutable, changes go in a new version.
    /// </summary>
    public static class SchemaMigrator
    {
        public const string LedgerDdl =
            "CREATE TABLE IF NOT EXISTS schema_migrations(" +
            " module_id TEXT NOT NULL, version INTEGER NOT NULL, description TEXT, checksum TEXT NOT NULL," +
            " applied_at_ms INTEGER NOT NULL, PRIMARY KEY(module_id, version));";

        public static string ChecksumOf(string sql)
        {
            string normalised = sql.Replace("\r\n", "\n").Trim();
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalised)));
        }

        /// <summary>Validates ordering and applies every missing migration inside the caller's transaction.
        /// Returns the number applied.</summary>
        public static int Apply(SqliteConnection conn, SqliteTransaction tx, IEnumerable<Migration> migrations, long nowMs)
        {
            conn.Exec(tx, LedgerDdl);
            int applied = 0;
            foreach (var group in migrations.GroupBy(m => m.ModuleId))
            {
                var ordered = group.OrderBy(m => m.Version).ToList();
                for (int i = 0; i < ordered.Count; i++)
                {
                    if (ordered[i].Version != i + 1)
                        throw new InvalidOperationException($"Module '{group.Key}' migrations must be contiguous from 1; found version {ordered[i].Version} at position {i + 1}.");
                }

                var existing = conn.Query(tx,
                    "SELECT version, checksum FROM schema_migrations WHERE module_id=$m",
                    r => (Version: r.GetInt32(0), Checksum: r.GetString(1)),
                    ("$m", group.Key)).ToDictionary(x => x.Version, x => x.Checksum);

                foreach (var m in ordered)
                {
                    if (existing.TryGetValue(m.Version, out var checksum))
                    {
                        if (!string.Equals(checksum, m.Checksum, StringComparison.Ordinal))
                            throw new InvalidOperationException($"Migration {m.ModuleId} v{m.Version} ('{m.Description}') changed after it was applied. Add a new version instead of editing a shipped one.");
                        continue;
                    }
                    conn.Exec(tx, m.Sql);
                    conn.Exec(tx,
                        "INSERT INTO schema_migrations(module_id, version, description, checksum, applied_at_ms) VALUES($m,$v,$d,$c,$t)",
                        ("$m", m.ModuleId), ("$v", m.Version), ("$d", m.Description), ("$c", m.Checksum), ("$t", nowMs));
                    applied++;
                }
            }
            return applied;
        }

        public static int AppliedVersion(SqliteConnection conn, string moduleId)
        {
            bool hasLedger = conn.Scalar<long>(null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_migrations'") > 0;
            if (!hasLedger) return 0;
            return (int)(conn.Scalar<long?>(null, "SELECT MAX(version) FROM schema_migrations WHERE module_id=$m", ("$m", moduleId)) ?? 0);
        }
    }
}
