using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniDefence;

namespace Omnipotent.Tests.OmniDefence;

public sealed class IpDetailIndexTests
{
    [Theory]
    [InlineData("requests", "ix_requests_ip_ts")]
    [InlineData("auth_events", "ix_auth_ip_ts")]
    [InlineData("ip_events", "ix_ipe_ip_ts")]
    public async Task RecentIpDetailQueryUsesOrderedCompositeIndex(string table, string index)
    {
        string path = Path.Combine(Path.GetTempPath(), "omnidefence-index-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new OmniDefenceStore(path);
        try
        {
            await store.InitializeAsync();
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"EXPLAIN QUERY PLAN SELECT * FROM {table} WHERE ip=$ip ORDER BY utc_ts DESC LIMIT 200";
            cmd.Parameters.AddWithValue("$ip", "203.0.113.1");
            using var reader = await cmd.ExecuteReaderAsync();
            var details = new List<string>();
            while (await reader.ReadAsync()) details.Add(reader.GetString(3));
            Assert.Contains(details, detail => detail.Contains(index, StringComparison.Ordinal));
            Assert.DoesNotContain(details, detail => detail.Contains("TEMP B-TREE", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await store.ShutdownAsync();
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                try { if (File.Exists(path + suffix)) File.Delete(path + suffix); } catch { }
            }
        }
    }
}
