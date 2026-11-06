using System.Collections.Concurrent;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Kernel
{
    /// <summary>
    /// v3 settings, stored in operator.db with defaults that live in code. Unlike OmniSettings, an unset
    /// key is never persisted, so changing a default in code takes effect immediately
    /// (the "OmniSettings defaults persist" trap). Every change is audited.
    /// </summary>
    public sealed class KernelSettings
    {
        public const string KlivesTimeZoneKey = "klives.time_zone";

        private readonly SqliteStore op;
        private readonly IClock clock;
        private readonly ConcurrentDictionary<string, (string? Json, long ReadAtMs)> cache = new();
        private static readonly long CacheMs = 5_000;

        public KernelSettings(SqliteStore operatorStore, IClock clock)
        {
            op = operatorStore;
            this.clock = clock;
        }

        public T Get<T>(string key, T defaultValue)
        {
            string? json = ReadRaw(key);
            if (json == null) return defaultValue;
            try { return JsonSerializer.Deserialize<T>(json, KernelJson.Options) ?? defaultValue; }
            catch (JsonException) { return defaultValue; }
        }

        public async Task SetAsync<T>(string key, T value, string actor, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            string json = JsonSerializer.Serialize(value, KernelJson.Options);
            long now = clock.NowMs;
            await op.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "INSERT INTO settings(key, value_json, updated_at_ms) VALUES($k,$v,$t) ON CONFLICT(key) DO UPDATE SET value_json=excluded.value_json, updated_at_ms=excluded.updated_at_ms",
                    ("$k", key), ("$v", json), ("$t", now));
                c.Exec(tx, "INSERT INTO audit_log(at_ms, actor, action, target, detail_json) VALUES($t,$a,'setting.set',$k,$v)",
                    ("$t", now), ("$a", actor), ("$k", key), ("$v", json));
            }, ct).ConfigureAwait(false);
            cache[key] = (json, now);
        }

        /// <summary>Klives's IANA zone: defines "today", digest windows, reminders and daily schedules (plan T4/T7).</summary>
        public TimeZoneInfo KlivesZone =>
            Zones.TryGet(Get(KlivesTimeZoneKey, Zones.DefaultKlivesZone), out var z) ? z : Zones.GetOrThrow(Zones.DefaultKlivesZone);

        private string? ReadRaw(string key)
        {
            long now = clock.NowMs;
            if (cache.TryGetValue(key, out var hit) && now - hit.ReadAtMs < CacheMs) return hit.Json;
            using var c = op.OpenRead();
            string? json = c.Scalar<string>(null, "SELECT value_json FROM settings WHERE key=$k", ("$k", key));
            cache[key] = (json, now);
            return json;
        }
    }
}
