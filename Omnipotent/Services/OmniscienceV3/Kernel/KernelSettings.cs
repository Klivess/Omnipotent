using System.Collections.Concurrent;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Kernel
{
    /// <summary>The value types a <see cref="SettingDescriptor"/> may declare.</summary>
    public static class SettingTypes
    {
        public const string Bool = "bool";
        public const string Int = "int";
        public const string Number = "number";
        public const string String = "string";
        /// <summary>An IANA zone id, validated against the zone database.</summary>
        public const string Zone = "zone";
        /// <summary>Any JSON value; the module validates it when it reads it.</summary>
        public const string Json = "json";
    }

    /// <summary>Appends to operator.db's audit log: every action Klives (or an agent on his behalf) takes.</summary>
    public static class OperatorAudit
    {
        public static Task WriteAsync(SqliteStore op, IClock clock, string actor, string action, string? target, object? detail, CancellationToken ct = default)
        {
            string? json = detail == null ? null : JsonSerializer.Serialize(detail, KernelJson.Options);
            long now = clock.NowMs;
            return op.WriteAsync((c, tx) => c.Exec(tx, "INSERT INTO audit_log(at_ms, actor, action, target, detail_json) VALUES($t,$a,$x,$g,$d)",
                ("$t", now), ("$a", actor), ("$x", action), ("$g", target), ("$d", json)), ct);
        }
    }

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

        /// <summary>Settings owned by the kernel itself (modules declare theirs in their manifests).</summary>
        public static readonly IReadOnlyList<SettingDescriptor> KernelDescriptors = new[]
        {
            new SettingDescriptor(KlivesTimeZoneKey, SettingTypes.Zone, JsonSerializer.Serialize(Zones.DefaultKlivesZone),
                "Klives's IANA time zone. Defines \"today\", digest windows, reminders and the local time of daily schedules."),
        };

        /// <summary>Every stored (non-default) value.</summary>
        public IReadOnlyList<(string Key, string ValueJson, long UpdatedAtMs)> Stored()
        {
            using var c = op.OpenRead();
            return c.Query(null, "SELECT key, value_json, updated_at_ms FROM settings ORDER BY key",
                r => (r.GetString(0), r.GetString(1), r.GetInt64(2)));
        }

        /// <summary>
        /// Checks <paramref name="value"/> against <paramref name="descriptor"/>; returns the problem, or null when valid.
        /// Unknown keys are rejected by the caller, so a typo can never create a setting nothing reads.
        /// </summary>
        public static string? Validate(SettingDescriptor descriptor, JsonElement value)
        {
            switch (descriptor.Type)
            {
                case SettingTypes.Bool:
                    return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "must be true or false";
                case SettingTypes.Int:
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long i)) return "must be a whole number";
                    return InRange(descriptor, i);
                case SettingTypes.Number:
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double d) || !double.IsFinite(d)) return "must be a number";
                    return InRange(descriptor, d);
                case SettingTypes.String:
                    return value.ValueKind == JsonValueKind.String ? null : "must be a string";
                case SettingTypes.Zone:
                    return value.ValueKind == JsonValueKind.String && Zones.TryGet(value.GetString(), out _)
                        ? null : "must be an IANA time zone id such as Europe/London";
                case SettingTypes.Json:
                    return null;
                default:
                    return $"has an unknown declared type '{descriptor.Type}'";
            }
        }

        private static string? InRange(SettingDescriptor descriptor, double v)
        {
            if (descriptor.Min is double min && v < min) return $"must be at least {min}";
            if (descriptor.Max is double max && v > max) return $"must be at most {max}";
            return null;
        }

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
