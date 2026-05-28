using Microsoft.Data.Sqlite;

namespace Omnipotent.Services.OmniTrader.Persistence
{
    public sealed class KrakenNonceStore
    {
        private readonly OmniTraderDb db;
        private readonly object syncRoot = new();
        private long inMemory;

        public KrakenNonceStore(OmniTraderDb db) { this.db = db; }

        public async Task InitialiseAsync(CancellationToken ct = default)
        {
            await using var conn = await db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT last_nonce FROM kraken_nonce WHERE singleton=1";
            var result = await cmd.ExecuteScalarAsync(ct);
            inMemory = result is long l ? l : 0;
        }

        public async Task<long> NextAsync(CancellationToken ct = default)
        {
            long next;
            lock (syncRoot)
            {
                long now = DateTime.UtcNow.Ticks;
                next = now > inMemory ? now : inMemory + 1;
                inMemory = next;
            }

            await db.WithWriteLockAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO kraken_nonce(singleton, last_nonce) VALUES(1, $n)
                                    ON CONFLICT(singleton) DO UPDATE SET last_nonce=excluded.last_nonce";
                cmd.Parameters.AddWithValue("$n", next);
                await cmd.ExecuteNonQueryAsync(ct);
            }, ct);

            return next;
        }
    }
}
