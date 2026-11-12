using System.Globalization;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Replica
{
    public static class Stages
    {
        public const string Index = "index";
        public const string Vectors = "vectors";
        public const string Reflex = "reflex";
        public const string Persona = "persona";
        public const string Eval = "eval";
        public const string Finalize = "finalize";

        public static readonly IReadOnlyList<string> All = new[] { Index, Vectors, Reflex, Persona, Eval, Finalize };

        /// <summary>The middle stages run in parallel after the index; evaluation waits for all of them.</summary>
        public static readonly IReadOnlyList<string> Parallel = new[] { Vectors, Reflex, Persona };

        /// <summary>Required stages fail the version; the others degrade it (plan: "Persona synthesis failing doesn't block").</summary>
        public static bool Required(string stage) => stage is Index or Finalize;
    }

    public static class StageStates
    {
        public const string Pending = "pending";
        public const string Queued = "queued";
        public const string Running = "running";
        public const string Done = "done";
        public const string Degraded = "degraded";
        public const string Failed = "failed";
        public const string Skipped = "skipped";

        public static bool IsTerminal(string s) => s is Done or Degraded or Failed or Skipped;
    }

    public static class VersionStates
    {
        public const string Building = "building";
        public const string Ready = "ready";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";
        public const string Retired = "retired";
        public const string Blocked = "blocked";
    }

    public static class PersonStatus
    {
        public const string None = "none";
        public const string Building = "building";
        public const string Ready = "ready";
        public const string Failed = "failed";
        public const string Blocked = "blocked";
    }

    public sealed record StageState(string State, int Attempts = 0, long? StartedAtMs = null, long? FinishedAtMs = null, string? Detail = null, string? Error = null,
        int Total = 0, int Completed = 0, int Failed = 0);

    public sealed record VersionRecord(
        string VersionId, string PersonId, int Number, string State, string Reason, string RequestedBy, int ModuleVersion, long CreatedAtMs, long? StartedAtMs,
        long? FinishedAtMs, long SourceSeq, long? DataFromMs, long? DataToMs, long? AdultFromMs, string? EncoderModel, int? WindowTokens,
        IReadOnlyDictionary<string, StageState> Stages, string? CountsJson, string? ConfigJson, string? MetricsJson, IReadOnlyList<string> Degraded, string? StyleJson, string? Error)
    {
        public StageState Stage(string name) => Stages.TryGetValue(name, out var s) ? s : new StageState(StageStates.Pending);
    }

    public sealed record PersonRecord(
        string PersonId, string? DisplayName, string? ServingVersionId, string? BuildingVersionId, string Status, string? StatusDetail, bool Stale, long? StaleSinceMs,
        string? StaleReason, long ServedSourceSeq, int NewAuthored, string? ReadinessJson, long? ReadinessAtMs, bool AutoRetrain, long CreatedAtMs, long UpdatedAtMs);

    /// <summary>
    /// Replica persons and versions in intel.db. Every transition is one transaction through intel's single writer, so a
    /// crash leaves either the old state or the new one, never half (F266–F269): a build is a <see cref="VersionStates.Building"/>
    /// row beside the serving one, and only <see cref="SwapAsync"/> — after every stage reached a terminal state — moves the
    /// person's serving pointer. A failed or cancelled build never touches the serving version.
    /// </summary>
    public sealed class VersionStore
    {
        private readonly SqliteStore intel;
        private readonly IClock clock;

        public VersionStore(SqliteStore intel, IClock clock)
        {
            this.intel = intel;
            this.clock = clock;
        }

        private const string VersionColumns =
            "version_id, person_id, number, state, reason, requested_by, module_version, created_at_ms, started_at_ms, finished_at_ms, source_seq, data_from_ms, data_to_ms, " +
            "adult_from_ms, encoder_model, window_tokens, stages_json, counts_json, config_json, metrics_json, degraded_json, style_json, error";

        private static VersionRecord MapVersion(SqliteDataReader r) => new(
            r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt32(6), r.GetInt64(7), r.GetInt64OrNull(8), r.GetInt64OrNull(9),
            r.GetInt64(10), r.GetInt64OrNull(11), r.GetInt64OrNull(12), r.GetInt64OrNull(13), r.GetStringOrNull(14), r.IsDBNull(15) ? null : r.GetInt32(15),
            RJson.Read<Dictionary<string, StageState>>(r.GetString(16)) ?? new Dictionary<string, StageState>(), r.GetStringOrNull(17), r.GetStringOrNull(18),
            r.GetStringOrNull(19), RJson.Read<List<string>>(r.GetStringOrNull(20)) ?? new List<string>(), r.GetStringOrNull(21), r.GetStringOrNull(22));

        private const string PersonColumns =
            "person_id, display_name, serving_version_id, building_version_id, status, status_detail, stale, stale_since_ms, stale_reason, served_source_seq, new_authored, " +
            "readiness_json, readiness_at_ms, auto_retrain, created_at_ms, updated_at_ms";

        private static PersonRecord MapPerson(SqliteDataReader r) => new(
            r.GetString(0), r.GetStringOrNull(1), r.GetStringOrNull(2), r.GetStringOrNull(3), r.GetString(4), r.GetStringOrNull(5), r.GetInt32(6) == 1, r.GetInt64OrNull(7),
            r.GetStringOrNull(8), r.GetInt64(9), r.GetInt32(10), r.GetStringOrNull(11), r.GetInt64OrNull(12), r.GetInt32(13) == 1, r.GetInt64(14), r.GetInt64(15));

        public VersionRecord? Version(string versionId)
        {
            using var c = intel.OpenRead();
            return Version(c, null, versionId);
        }

        internal static VersionRecord? Version(SqliteConnection c, SqliteTransaction? tx, string versionId) =>
            c.Query(tx, $"SELECT {VersionColumns} FROM replica_versions WHERE version_id=$v", MapVersion, ("$v", versionId)).FirstOrDefault();

        public IReadOnlyList<VersionRecord> Versions(string personId, int limit = 50)
        {
            using var c = intel.OpenRead();
            return c.Query(null, $"SELECT {VersionColumns} FROM replica_versions WHERE person_id=$p ORDER BY number DESC LIMIT $l", MapVersion, ("$p", personId), ("$l", limit));
        }

        public IReadOnlyList<VersionRecord> Building()
        {
            using var c = intel.OpenRead();
            return c.Query(null, $"SELECT {VersionColumns} FROM replica_versions WHERE state='building'", MapVersion);
        }

        public PersonRecord? Person(string personId)
        {
            using var c = intel.OpenRead();
            return Person(c, null, personId);
        }

        internal static PersonRecord? Person(SqliteConnection c, SqliteTransaction? tx, string personId) =>
            c.Query(tx, $"SELECT {PersonColumns} FROM replica_persons WHERE person_id=$p", MapPerson, ("$p", personId)).FirstOrDefault();

        public IReadOnlyList<PersonRecord> Persons(string? status = null)
        {
            using var c = intel.OpenRead();
            return c.Query(null, $"SELECT {PersonColumns} FROM replica_persons WHERE ($s IS NULL OR status=$s) ORDER BY person_id", MapPerson, ("$s", status));
        }

        /// <summary>Records (or refreshes) a person's readiness, creating their row on first sight.</summary>
        public Task SaveReadinessAsync(string personId, string displayName, ReplicaReadinessDto readiness, CancellationToken ct) =>
            intel.WriteAsync((c, tx) =>
            {
                long now = clock.NowMs;
                c.Exec(tx, """
                    INSERT INTO replica_persons(person_id, display_name, status, readiness_json, readiness_at_ms, created_at_ms, updated_at_ms)
                    VALUES($p,$n,'none',$r,$t,$t,$t)
                    ON CONFLICT(person_id) DO UPDATE SET display_name=excluded.display_name, readiness_json=excluded.readiness_json,
                        readiness_at_ms=excluded.readiness_at_ms, updated_at_ms=excluded.updated_at_ms
                    """, ("$p", personId), ("$n", displayName), ("$r", RJson.Write(readiness)), ("$t", now));
            }, ct);

        /// <summary>
        /// Starts a build: the version row (state building, every stage pending) and the person's building pointer, in one
        /// transaction. Single-flight per person: when a build is already running, that build is returned (Created=false).
        /// </summary>
        public Task<(VersionRecord Version, bool Created)> CreateAsync(string personId, string displayName, string reason, string actor, int moduleVersion, CancellationToken ct) =>
            intel.WriteAsync((c, tx) =>
            {
                long now = clock.NowMs;
                var person = Person(c, tx, personId);
                if (person?.BuildingVersionId is { } building && Version(c, tx, building) is { State: VersionStates.Building } live) return (live, false);
                int number = (int)(c.Scalar<long?>(tx, "SELECT MAX(number) FROM replica_versions WHERE person_id=$p", ("$p", personId)) ?? 0) + 1;
                string versionId = "rv_" + TextFeatures.Hash(personId, 10) + "_" + number.ToString(CultureInfo.InvariantCulture) + "_" + now.ToString(CultureInfo.InvariantCulture);
                var stages = Stages.All.ToDictionary(s => s, _ => new StageState(StageStates.Pending), StringComparer.Ordinal);
                stages[Stages.Index] = new StageState(StageStates.Queued);
                c.Exec(tx, $"""
                    INSERT INTO replica_versions(version_id, person_id, number, state, reason, requested_by, module_version, created_at_ms, stages_json)
                    VALUES($v,$p,$n,'building',$r,$a,$m,$t,$s)
                    """, ("$v", versionId), ("$p", personId), ("$n", number), ("$r", reason), ("$a", actor), ("$m", moduleVersion), ("$t", now), ("$s", RJson.Write(stages)));
                c.Exec(tx, """
                    INSERT INTO replica_persons(person_id, display_name, building_version_id, status, created_at_ms, updated_at_ms) VALUES($p,$n,$v,'building',$t,$t)
                    ON CONFLICT(person_id) DO UPDATE SET display_name=excluded.display_name, building_version_id=excluded.building_version_id,
                        status=CASE WHEN replica_persons.serving_version_id IS NULL THEN 'building' ELSE 'ready' END, status_detail=NULL, updated_at_ms=excluded.updated_at_ms
                    """, ("$p", personId), ("$n", displayName), ("$v", versionId), ("$t", now));
                return (Version(c, tx, versionId)!, true);
            }, ct);

        /// <summary>Updates one stage (read-modify-write in a single transaction). Returns the new stage map, or null when the
        /// version is no longer building (cancelled, failed or already finished): callers then stop.</summary>
        public Task<IReadOnlyDictionary<string, StageState>?> UpdateStageAsync(string versionId, string stage, Func<StageState, StageState> update, CancellationToken ct) =>
            intel.WriteAsync<IReadOnlyDictionary<string, StageState>?>((c, tx) =>
            {
                var v = Version(c, tx, versionId);
                if (v == null || v.State != VersionStates.Building) return null;
                var stages = new Dictionary<string, StageState>(v.Stages, StringComparer.Ordinal);
                stages[stage] = update(v.Stage(stage));
                c.Exec(tx, "UPDATE replica_versions SET stages_json=$s, started_at_ms=COALESCE(started_at_ms,$t) WHERE version_id=$v",
                    ("$s", RJson.Write(stages)), ("$t", clock.NowMs), ("$v", versionId));
                return stages;
            }, ct);

        /// <summary>
        /// Marks a stage terminal and decides, in the same transaction, which stages may start now (the fan-out after the
        /// index, the join before evaluation, finalisation after it). A stage is handed out once: it moves to queued here.
        /// </summary>
        public Task<IReadOnlyList<string>> CompleteStageAsync(string versionId, string stage, StageState final, CancellationToken ct) =>
            intel.WriteAsync((c, tx) => CompleteStageCore(c, tx, versionId, stage, final, clock.NowMs), ct);

        /// <summary><see cref="CompleteStageAsync"/> inside an existing transaction (persona slices join through it).</summary>
        internal static IReadOnlyList<string> CompleteStageCore(SqliteConnection c, SqliteTransaction tx, string versionId, string stage, StageState final, long nowMs)
        {
            var v = Version(c, tx, versionId);
            if (v == null || v.State != VersionStates.Building) return Array.Empty<string>();
            var stages = new Dictionary<string, StageState>(v.Stages, StringComparer.Ordinal);
            stages[stage] = final with { FinishedAtMs = final.FinishedAtMs ?? nowMs };
            var next = new List<string>();
            void Start(string s)
            {
                var st = stages.TryGetValue(s, out var x) ? x : new StageState(StageStates.Pending);
                if (st.State != StageStates.Pending) return;
                stages[s] = st with { State = StageStates.Queued };
                next.Add(s);
            }
            bool indexOk = stages.TryGetValue(Stages.Index, out var idx) && idx.State is StageStates.Done or StageStates.Degraded;
            if (stage == Stages.Index && indexOk) foreach (var s in Stages.Parallel) Start(s);
            if (indexOk && Stages.Parallel.All(s => stages.TryGetValue(s, out var p) && StageStates.IsTerminal(p.State))) Start(Stages.Eval);
            if (stages.TryGetValue(Stages.Eval, out var ev) && StageStates.IsTerminal(ev.State)) Start(Stages.Finalize);
            c.Exec(tx, "UPDATE replica_versions SET stages_json=$s WHERE version_id=$v", ("$s", RJson.Write(stages)), ("$v", versionId));
            return next;
        }

        public Task UpdateFieldsAsync(string versionId, long? sourceSeq = null, long? dataFrom = null, long? dataTo = null, long? adultFrom = null, string? encoder = null,
            int? windowTokens = null, string? countsJson = null, string? configJson = null, string? metricsJson = null, string? styleJson = null, string? addDegraded = null,
            CancellationToken ct = default) =>
            intel.WriteAsync((c, tx) =>
            {
                var v = Version(c, tx, versionId);
                if (v == null) return;
                var degraded = v.Degraded.ToList();
                if (addDegraded != null && !degraded.Contains(addDegraded)) degraded.Add(addDegraded);
                c.Exec(tx, """
                    UPDATE replica_versions SET source_seq=COALESCE($seq, source_seq), data_from_ms=COALESCE($df, data_from_ms), data_to_ms=COALESCE($dt, data_to_ms),
                        adult_from_ms=COALESCE($af, adult_from_ms), encoder_model=COALESCE($enc, encoder_model), window_tokens=COALESCE($w, window_tokens),
                        counts_json=COALESCE($cn, counts_json), config_json=COALESCE($cf, config_json), metrics_json=COALESCE($mt, metrics_json),
                        style_json=COALESCE($st, style_json), degraded_json=$dg
                    WHERE version_id=$v
                    """, ("$seq", sourceSeq), ("$df", dataFrom), ("$dt", dataTo), ("$af", adultFrom), ("$enc", encoder), ("$w", windowTokens), ("$cn", countsJson),
                    ("$cf", configJson), ("$mt", metricsJson), ("$st", styleJson), ("$dg", RJson.Write(degraded)), ("$v", versionId));
            }, ct);

        /// <summary>
        /// The atomic swap (F268/F269): the finished build becomes the serving version, the previous serving version is
        /// retired (kept for rollback until pruned), the person's staleness is cleared and their serving watermark moves.
        /// False when the version was cancelled or failed meanwhile.
        /// </summary>
        public Task<(bool Swapped, string? Previous)> SwapAsync(string versionId, CancellationToken ct) =>
            intel.WriteAsync((c, tx) =>
            {
                var v = Version(c, tx, versionId);
                if (v == null || v.State != VersionStates.Building) return (false, (string?)null);
                long now = clock.NowMs;
                var person = Person(c, tx, v.PersonId);
                string? previous = person?.ServingVersionId;
                c.Exec(tx, "UPDATE replica_versions SET state='ready', finished_at_ms=$t WHERE version_id=$v", ("$t", now), ("$v", versionId));
                if (previous != null && previous != versionId)
                    c.Exec(tx, "UPDATE replica_versions SET state='retired' WHERE version_id=$p AND state='ready'", ("$p", previous));
                c.Exec(tx, """
                    UPDATE replica_persons SET serving_version_id=$v, building_version_id=NULL, status='ready', status_detail=NULL, stale=0, stale_since_ms=NULL,
                        stale_reason=NULL, served_source_seq=$seq, new_authored=0, updated_at_ms=$t
                    WHERE person_id=$p
                    """, ("$v", versionId), ("$seq", v.SourceSeq), ("$t", now), ("$p", v.PersonId));
                return (true, previous);
            }, ct);

        /// <summary>Ends a build without touching the serving version (failed, cancelled or blocked by MinorGuard).</summary>
        public Task<bool> EndAsync(string versionId, string state, string error, CancellationToken ct) =>
            intel.WriteAsync((c, tx) =>
            {
                var v = Version(c, tx, versionId);
                if (v == null || v.State != VersionStates.Building) return false;
                long now = clock.NowMs;
                c.Exec(tx, "UPDATE replica_versions SET state=$s, finished_at_ms=$t, error=$e WHERE version_id=$v", ("$s", state), ("$t", now), ("$e", error), ("$v", versionId));
                var person = Person(c, tx, v.PersonId);
                string status = state == VersionStates.Blocked ? PersonStatus.Blocked : person?.ServingVersionId != null ? PersonStatus.Ready : PersonStatus.Failed;
                c.Exec(tx, """
                    UPDATE replica_persons SET building_version_id=NULL, status=$st, status_detail=$d, updated_at_ms=$t WHERE person_id=$p AND building_version_id=$v
                    """, ("$st", status), ("$d", (state == VersionStates.Cancelled ? "Last build cancelled: " : "Last build failed: ") + error), ("$t", now),
                    ("$p", v.PersonId), ("$v", versionId));
                return true;
            }, ct);

        public Task MarkStaleAsync(string personId, string reason, int newAuthored, CancellationToken ct) =>
            intel.WriteAsync((c, tx) => c.Exec(tx, """
                UPDATE replica_persons SET stale=1, stale_since_ms=COALESCE(stale_since_ms,$t), stale_reason=$r, new_authored=$n, updated_at_ms=$t WHERE person_id=$p
                """, ("$t", clock.NowMs), ("$r", reason), ("$n", newAuthored), ("$p", personId)), ct);

        public Task SetNewAuthoredAsync(string personId, int newAuthored, CancellationToken ct) =>
            intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE replica_persons SET new_authored=$n WHERE person_id=$p AND new_authored<>$n", ("$n", newAuthored), ("$p", personId)), ct);

        public Task SetAutoRetrainAsync(string personId, bool enabled, CancellationToken ct) =>
            intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE replica_persons SET auto_retrain=$e, updated_at_ms=$t WHERE person_id=$p",
                ("$e", enabled ? 1 : 0), ("$t", clock.NowMs), ("$p", personId)), ct);

        /// <summary>MinorGuard: no replica of a (likely) minor. The serving pointer is cleared at once; derived data is purged by the caller.</summary>
        public Task BlockAsync(string personId, string reason, CancellationToken ct) =>
            intel.WriteAsync((c, tx) =>
            {
                long now = clock.NowMs;
                c.Exec(tx, "UPDATE replica_versions SET state='blocked', error=COALESCE(error,$r), finished_at_ms=COALESCE(finished_at_ms,$t) WHERE person_id=$p AND state IN ('building','ready','retired')",
                    ("$r", reason), ("$t", now), ("$p", personId));
                c.Exec(tx, """
                    INSERT INTO replica_persons(person_id, status, status_detail, created_at_ms, updated_at_ms) VALUES($p,'blocked',$r,$t,$t)
                    ON CONFLICT(person_id) DO UPDATE SET serving_version_id=NULL, building_version_id=NULL, status='blocked', status_detail=$r, updated_at_ms=$t
                    """, ("$p", personId), ("$r", reason), ("$t", now));
            }, ct);

        /// <summary>Removes the person's replica rows themselves (an erased person, after their derived rows were purged).</summary>
        public Task ForgetAsync(string personId, CancellationToken ct) =>
            intel.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "DELETE FROM replica_versions WHERE person_id=$p", ("$p", personId));
                c.Exec(tx, "DELETE FROM replica_persons WHERE person_id=$p", ("$p", personId));
            }, ct);

        /// <summary>Lifts a block that was set for <paramref name="reason"/> (analysis switched back on): the person can be trained again.</summary>
        public Task<bool> UnblockAsync(string personId, string reason, CancellationToken ct) =>
            intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE replica_persons SET status='none', status_detail=NULL, updated_at_ms=$t WHERE person_id=$p AND status='blocked' AND status_detail=$r",
                ("$t", clock.NowMs), ("$p", personId), ("$r", reason)) > 0, ct);

        /// <summary>Moves a replica row to a person's new canonical id after a merge (their versions keep their history).</summary>
        public Task RekeyAsync(string fromPersonId, string toPersonId, CancellationToken ct) =>
            intel.WriteAsync((c, tx) =>
            {
                if (Person(c, tx, fromPersonId) == null) return;
                long now = clock.NowMs;
                if (Person(c, tx, toPersonId) == null)
                    c.Exec(tx, "UPDATE replica_persons SET person_id=$to, stale=1, stale_since_ms=$t, stale_reason='merged with another person', updated_at_ms=$t WHERE person_id=$from",
                        ("$to", toPersonId), ("$t", now), ("$from", fromPersonId));
                else
                {
                    c.Exec(tx, "UPDATE replica_persons SET stale=1, stale_since_ms=COALESCE(stale_since_ms,$t), stale_reason='merged with another person', updated_at_ms=$t WHERE person_id=$to",
                        ("$t", now), ("$to", toPersonId));
                    c.Exec(tx, "DELETE FROM replica_persons WHERE person_id=$from", ("$from", fromPersonId));
                }
                c.Exec(tx, "UPDATE replica_versions SET person_id=$to WHERE person_id=$from AND state IN ('ready','retired','building')", ("$to", toPersonId), ("$from", fromPersonId));
            }, ct);

        /// <summary>
        /// Deletes the derived rows of versions beyond retention (serving, previous and building are always kept). The version
        /// rows themselves stay, with their metrics, so the fidelity trend is kept per version. Returns versions pruned.
        /// </summary>
        public Task<int> PruneAsync(string personId, int keep, CancellationToken ct) =>
            intel.WriteAsync((c, tx) =>
            {
                var person = Person(c, tx, personId);
                var versions = c.Query(tx, "SELECT version_id, state FROM replica_versions WHERE person_id=$p ORDER BY number DESC", r => (Id: r.GetString(0), State: r.GetString(1)), ("$p", personId));
                var protectedIds = new HashSet<string>(new[] { person?.ServingVersionId, person?.BuildingVersionId }.OfType<string>(), StringComparer.Ordinal);
                var kept = versions.Where(v => v.State is VersionStates.Ready or VersionStates.Retired or VersionStates.Building).Take(keep).Select(v => v.Id).ToHashSet(StringComparer.Ordinal);
                kept.UnionWith(protectedIds);
                int pruned = 0;
                foreach (var v in versions.Where(v => !kept.Contains(v.Id)))
                {
                    int n = c.Exec(tx, "DELETE FROM replica_pairs WHERE version_id=$v", ("$v", v.Id))
                            + c.Exec(tx, "DELETE FROM replica_persona_items WHERE version_id=$v", ("$v", v.Id))
                            + c.Exec(tx, "DELETE FROM replica_eras WHERE version_id=$v", ("$v", v.Id))
                            + c.Exec(tx, "DELETE FROM replica_slices WHERE version_id=$v", ("$v", v.Id));
                    if (n > 0) pruned++;
                }
                return pruned;
            }, ct);
    }
}
