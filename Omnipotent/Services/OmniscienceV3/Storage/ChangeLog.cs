using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Storage
{
    /// <summary>Where a corpus change came from (plan T5: alerts only ever fire for <see cref="Live"/>).</summary>
    public static class SourceKinds
    {
        public const string Live = "live";
        public const string Backfill = "backfill";
        public const string Import = "import";
        public const string Legacy = "legacy";
        public const string Operator = "operator";

        public static bool IsKnown(string? kind) =>
            kind is Live or Backfill or Import or Legacy or Operator;
    }

    /// <summary>Change kinds recorded in corpus.db's change log. Consumers switch on these.</summary>
    public static class ChangeKinds
    {
        public const string MessageInsert = "message.insert";
        public const string MessageVersion = "message.version";
        public const string MessageDelete = "message.delete";
        public const string ReactionChange = "reaction.change";
        public const string AttachmentAdd = "attachment.add";
        public const string AttachmentStored = "attachment.stored";
        public const string IdentityUpsert = "identity.upsert";
        public const string IdentityName = "identity.name";
        public const string PersonCreate = "person.create";
        public const string PersonMerge = "person.merge";
        public const string PersonUnmerge = "person.unmerge";
        public const string PersonErase = "person.erase";
        public const string ConversationUpsert = "conversation.upsert";
        public const string GuildUpsert = "guild.upsert";
        public const string PresenceEvent = "event.presence";
        public const string VoiceEvent = "event.voice";
        public const string MemberEvent = "event.member";
        public const string PollVote = "poll.vote";
    }

    public readonly record struct ChangeRecord(long Seq, long AtMs, string Kind, string EntityId, string? PersonId, string? ConversationId, string SourceKind);

    /// <summary>
    /// The durable outbox of v3 (plan R3). Every corpus mutation appends one row in the same transaction
    /// as the mutation; derived modules consume rows in <c>seq</c> order from their own watermark, so
    /// late-arriving backfill is processed exactly like live data (plan T5, defects F178/F182) and no event
    /// ever exists only in memory (defects F442/F528).
    /// </summary>
    public static class ChangeLog
    {
        public static long Append(SqliteConnection conn, SqliteTransaction tx, long atMs, string kind, string entityId,
            string? personId, string? conversationId, string sourceKind)
        {
            if (!SourceKinds.IsKnown(sourceKind)) throw new ArgumentException($"Unknown source kind '{sourceKind}'.", nameof(sourceKind));
            conn.Exec(tx,
                "INSERT INTO change_log(at_ms, kind, entity_id, person_id, conversation_id, source_kind) VALUES($a,$k,$e,$p,$c,$s)",
                ("$a", atMs), ("$k", kind), ("$e", entityId), ("$p", personId), ("$c", conversationId), ("$s", sourceKind));
            return conn.Scalar<long>(tx, "SELECT last_insert_rowid()");
        }

        public static IReadOnlyList<ChangeRecord> ReadAfter(SqliteConnection conn, long afterSeq, int limit) =>
            conn.Query(null,
                "SELECT seq, at_ms, kind, entity_id, person_id, conversation_id, source_kind FROM change_log WHERE seq > $s ORDER BY seq LIMIT $l",
                r => new ChangeRecord(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetStringOrNull(4), r.GetStringOrNull(5), r.GetString(6)),
                ("$s", afterSeq), ("$l", Math.Clamp(limit, 1, 100_000)));

        public static long MaxSeq(SqliteConnection conn) => conn.Scalar<long?>(null, "SELECT MAX(seq) FROM change_log") ?? 0;
    }
}
