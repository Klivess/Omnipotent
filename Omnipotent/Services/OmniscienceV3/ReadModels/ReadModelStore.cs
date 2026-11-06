using System.Text.Json;
using Omnipotent.Services.KliveAPI.Caching;
using Omnipotent.Services.OmniscienceV3.Kernel.Events;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.ReadModels
{
    /// <summary>A read model a module maintains: what it is, who may read it, and how fresh it should stay.</summary>
    public sealed record ReadModelDescriptor(
        string Id,
        string Title,
        string RequiredPermission,
        int SchemaVersion,
        long StalenessTargetMs,
        string Description = "");

    /// <summary>One stored read model row. <see cref="BodyJson"/> is served as-is.</summary>
    public sealed record ReadModelRow(string ModelId, string Key, long Version, int SchemaVersion, long AsOfMs, long SourceSeq, long ComputedAtMs, string BodyJson);

    /// <summary>The outcome of a write: whether the body changed (and so the version moved).</summary>
    public readonly record struct ReadModelWrite(long Version, bool Changed);

    /// <summary>
    /// Read models: the only thing v3 routes read (plan "Read models are the only thing routes read"). A projection
    /// (a module job) writes the finished view of one subject; a route reads one row by key. Rows carry the corpus
    /// watermark and as-of instant they reflect, so the UI can say how fresh a view is, and a version that only moves
    /// when the body actually changes, so caches and live clients are only disturbed by real changes.
    /// <para>
    /// Caching (F393): reads note <c>omniscience-v3:rm:{model}:{key}</c> and writes bump it, so the KliveAPI response
    /// cache can serve read-model routes without ever going stale. Anything a serving route also reads that can change
    /// the response (MinorGuard marks, tiers) bumps <see cref="PolicyDependencyKey"/>, which every read notes too.
    /// </para>
    /// </summary>
    public sealed class ReadModelStore
    {
        public const string PolicyDependencyKey = "omniscience-v3:policy";

        private readonly SqliteStore intel;
        private readonly IClock clock;
        private readonly EventHub? events;

        public ReadModelStore(SqliteStore intel, IClock clock, EventHub? events)
        {
            this.intel = intel;
            this.clock = clock;
            this.events = events;
        }

        public static string DependencyKey(string modelId, string key) => $"omniscience-v3:rm:{modelId}:{key}";

        /// <summary>
        /// Stores the view of (<paramref name="modelId"/>, <paramref name="key"/>). An identical body only refreshes
        /// the freshness stamps; a different one bumps the version, publishes <c>readmodel.updated</c> and invalidates
        /// cached responses.
        /// </summary>
        public async Task<ReadModelWrite> PutAsync(string modelId, string key, int schemaVersion, long asOfMs, long sourceSeq, object body, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            string json = body switch
            {
                JsonDocument d => d.RootElement.GetRawText(),
                JsonElement e => e.GetRawText(),
                _ => JsonSerializer.Serialize(body, KernelJson.Options),
            };
            long now = clock.NowMs;
            var result = await intel.WriteAsync((c, tx) =>
            {
                var current = c.Query(tx, "SELECT version, body, schema_version FROM read_models WHERE model_id=$m AND key=$k",
                    r => (Version: r.GetInt64(0), Body: r.GetString(1), Schema: r.GetInt32(2)), ("$m", modelId), ("$k", key)).FirstOrDefault();
                bool exists = current.Body != null;
                bool changed = !exists || current.Body != json || current.Schema != schemaVersion;
                long version = changed ? current.Version + 1 : current.Version;
                c.Exec(tx, """
                    INSERT INTO read_models(model_id, key, version, schema_version, as_of_ms, source_seq, computed_at_ms, body) VALUES($m,$k,$v,$s,$a,$q,$t,$b)
                    ON CONFLICT(model_id, key) DO UPDATE SET version=excluded.version, schema_version=excluded.schema_version, as_of_ms=excluded.as_of_ms,
                        source_seq=excluded.source_seq, computed_at_ms=excluded.computed_at_ms, body=excluded.body
                    """, ("$m", modelId), ("$k", key), ("$v", version), ("$s", schemaVersion), ("$a", asOfMs), ("$q", sourceSeq), ("$t", now), ("$b", json));
                return new ReadModelWrite(version, changed);
            }, ct).ConfigureAwait(false);
            if (result.Changed)
            {
                CacheDeps.Bump(DependencyKey(modelId, key));
                if (events != null)
                    await events.PublishAsync(Topics.ReadModelUpdated, new { model = modelId, key, version = result.Version, asOfMs }, ct).ConfigureAwait(false);
            }
            return result;
        }

        public ReadModelRow? Get(string modelId, string key)
        {
            CacheDeps.NoteRead(DependencyKey(modelId, key));
            CacheDeps.NoteRead(PolicyDependencyKey);
            using var c = intel.OpenRead();
            return c.Query(null, $"SELECT {Columns} FROM read_models WHERE model_id=$m AND key=$k", Map, ("$m", modelId), ("$k", key)).FirstOrDefault();
        }

        /// <summary>Rows of one model in key order (keyset: pass the last key seen as <paramref name="afterKey"/>).
        /// Listing is not cacheable (a new key appearing would not bump an existing key).</summary>
        public IReadOnlyList<ReadModelRow> List(string modelId, string? keyPrefix, string? afterKey, int limit)
        {
            CacheDeps.MarkUncacheable("omniscience-v3: read-model listing");
            using var c = intel.OpenRead();
            return c.Query(null, $"""
                SELECT {Columns} FROM read_models
                WHERE model_id=$m AND ($p IS NULL OR key >= $p AND key < $p || x'F48FBFBF') AND ($a IS NULL OR key > $a)
                ORDER BY key LIMIT $l
                """, Map, ("$m", modelId), ("$p", keyPrefix), ("$a", afterKey), ("$l", Math.Clamp(limit, 1, 1000)));
        }

        public async Task<bool> DeleteAsync(string modelId, string key, CancellationToken ct = default)
        {
            bool deleted = await intel.WriteAsync((c, tx) => c.Exec(tx, "DELETE FROM read_models WHERE model_id=$m AND key=$k", ("$m", modelId), ("$k", key)) == 1, ct).ConfigureAwait(false);
            if (deleted)
            {
                CacheDeps.Bump(DependencyKey(modelId, key));
                if (events != null) await events.PublishAsync(Topics.ReadModelUpdated, new { model = modelId, key, deleted = true }, ct).ConfigureAwait(false);
            }
            return deleted;
        }

        /// <summary>Call after any write that changes how read models are filtered at serving time (minor marks, tiers).</summary>
        public static void PolicyChanged() => CacheDeps.Bump(PolicyDependencyKey);

        private const string Columns = "model_id, key, version, schema_version, as_of_ms, source_seq, computed_at_ms, body";

        private static ReadModelRow Map(Microsoft.Data.Sqlite.SqliteDataReader r) =>
            new(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt32(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetString(7));
    }
}
