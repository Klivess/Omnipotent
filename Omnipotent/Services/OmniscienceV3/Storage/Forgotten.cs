using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Storage
{
    /// <summary>
    /// What must never be ingested again: the identities, persons and direct chats of forgotten people.
    /// An ingest path loads this once per run and asks <see cref="Blocks"/> before it writes anything.
    /// </summary>
    public sealed record ForgottenSet(HashSet<string> Identities, HashSet<string> Persons, HashSet<string> Conversations)
    {
        public static readonly ForgottenSet None = new(new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal));

        public bool Any => Identities.Count + Persons.Count + Conversations.Count > 0;

        /// <summary>True when something written by this identity, or in this conversation, must not be stored.</summary>
        public bool Blocks(string? identityId, string? conversationId) =>
            (identityId != null && Identities.Contains(identityId)) || (conversationId != null && Conversations.Contains(conversationId));
    }

    /// <summary>
    /// Tombstones of forgotten people (N20), kept in operator.db so they survive a rebuild of everything derived: the
    /// identities (platform + platform user id), person ids and direct chats that must never be ingested again. Without
    /// them, the next import of an export or the next pass over the old database would bring a forgotten person straight back.
    /// <para>A tombstone necessarily holds the platform user id: it is the only way to recognise the person's data when it
    /// is offered again. It holds nothing else.</para>
    /// Every ingest path checks <see cref="Load"/> before writing an identity, a conversation or anything in or by them.
    /// </summary>
    public static class Forgotten
    {
        /// <summary>operator.db <c>dismissals.kind</c> of a forgotten identity (key: the v3 identity id).</summary>
        public const string IdentityKind = "forgotten_identity";
        /// <summary>operator.db <c>dismissals.kind</c> of a forgotten person (key: the person id).</summary>
        public const string PersonKind = "forgotten_person";
        /// <summary>operator.db <c>dismissals.kind</c> of a forgotten direct chat (key: the v3 conversation id).</summary>
        public const string ConversationKind = "forgotten_conversation";

        /// <summary>SQL condition on <c>dismissals</c> that leaves the tombstones alone.</summary>
        public const string NotTombstone = "kind NOT IN ('forgotten_identity','forgotten_person','forgotten_conversation')";

        public static HashSet<string> Identities(SqliteStore operatorStore) => Load(operatorStore).Identities;

        public static HashSet<string> Persons(SqliteStore operatorStore) => Load(operatorStore).Persons;

        /// <summary>Whether this person is forgotten. True from the moment the forget is confirmed, before its run has removed anything.</summary>
        public static bool IsPerson(SqliteStore operatorStore, string personId)
        {
            using var c = operatorStore.OpenRead();
            return c.Scalar<long>(null, "SELECT COUNT(*) FROM dismissals WHERE kind=$k AND key=$p", ("$k", PersonKind), ("$p", personId)) > 0;
        }

        public static ForgottenSet Load(SqliteStore operatorStore)
        {
            var set = new ForgottenSet(new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal));
            using var c = operatorStore.OpenRead();
            foreach (var (kind, key) in c.Query(null, "SELECT kind, key FROM dismissals WHERE kind IN ($i,$p,$c)", r => (r.GetString(0), r.GetString(1)),
                         ("$i", IdentityKind), ("$p", PersonKind), ("$c", ConversationKind)))
                (kind == IdentityKind ? set.Identities : kind == PersonKind ? set.Persons : set.Conversations).Add(key);
            return set;
        }

        public static void Add(SqliteConnection c, SqliteTransaction tx, IEnumerable<string> identities, IEnumerable<string> persons, IEnumerable<string> conversations, long nowMs)
        {
            foreach (var (kind, ids) in new[] { (IdentityKind, identities), (PersonKind, persons), (ConversationKind, conversations) })
                foreach (string id in ids)
                    c.Exec(tx, "INSERT OR IGNORE INTO dismissals(kind, key, until_ms, at_ms) VALUES($k,$i,NULL,$n)", ("$k", kind), ("$i", id), ("$n", nowMs));
        }
    }
}
