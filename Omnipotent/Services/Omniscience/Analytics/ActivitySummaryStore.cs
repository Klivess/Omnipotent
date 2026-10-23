using Microsoft.Data.Sqlite;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Omnipotent.Services.Omniscience.Analytics
{
    /// <summary>
    /// Maintains small, incrementally updated activity summaries. Directory routes must
    /// never GROUP BY the multi-million-row messages table just to render one page.
    /// </summary>
    public sealed class ActivitySummaryStore
    {
        internal const string BackfillMetaKey = "activity_counts_v1_backfilled_at";
        private readonly OmniscienceDb db;
        private readonly Action<string>? log;
        private readonly SemaphoreSlim backfillGate = new(1, 1);
        private volatile bool backfillRunning;
        private string? lastBackfillError;

        public ActivitySummaryStore(OmniscienceDb db, Action<string>? log = null)
        {
            this.db = db;
            this.log = log;
        }

        public ActivitySummaryStatus GetStatus()
        {
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM omniscience_meta WHERE key=$k";
            cmd.Parameters.AddWithValue("$k", BackfillMetaKey);
            string? value = cmd.ExecuteScalar() as string;
            long.TryParse(value, out long completedAt);
            return new ActivitySummaryStatus(completedAt > 0, backfillRunning, lastBackfillError, completedAt);
        }

        /// <summary>
        /// One-time exact rebuild for existing corpora. New messages are counted by
        /// <see cref="RecordMessage"/> in the same transaction as the message insert.
        /// The service awaits this before enabling ingesters, so a rebuild cannot race
        /// a live increment and overwrite it with a stale count.
        /// </summary>
        public async Task EnsureBackfilledAsync(CancellationToken ct = default)
        {
            await backfillGate.WaitAsync(ct);
            try
            {
                if (GetStatus().Ready) return;
                backfillRunning = true;
                lastBackfillError = null;
                log?.Invoke("[Omniscience] Building incremental person/conversation activity summaries...");
                await db.WriteLock.WaitAsync(ct);
                try
                {
                    using var conn = db.Open();
                    using var tx = conn.BeginTransaction();
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandTimeout = 600;
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    cmd.Parameters.AddWithValue("$now", now);
                    cmd.Parameters.AddWithValue("$key", BackfillMetaKey);
                    cmd.CommandText = @"
DELETE FROM person_message_counts;
INSERT INTO person_message_counts(person_id, message_count, first_message_at, last_message_at, updated_at)
SELECT pi.person_id, COUNT(*), MIN(m.sent_at), MAX(m.sent_at), $now
FROM messages m
JOIN platform_identities pi ON pi.identity_id=m.author_identity_id
GROUP BY pi.person_id;

DELETE FROM conversation_message_counts;
INSERT INTO conversation_message_counts(conversation_id, message_count, first_message_at, last_message_at, updated_at)
SELECT conversation_id, COUNT(*), MIN(sent_at), MAX(sent_at), $now
FROM messages
GROUP BY conversation_id;

INSERT INTO omniscience_meta(key, value) VALUES($key, $now)
ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
                    using (ct.Register(() => { try { cmd.Cancel(); } catch { } }))
                    {
                        await cmd.ExecuteNonQueryAsync(ct);
                    }
                    tx.Commit();
                    log?.Invoke("[Omniscience] Activity summaries are ready.");
                }
                finally
                {
                    db.WriteLock.Release();
                }
            }
            catch (Exception ex)
            {
                lastBackfillError = ex.Message;
                log?.Invoke("[Omniscience] Activity summary backfill failed: " + ex.Message);
                throw;
            }
            finally
            {
                backfillRunning = false;
                backfillGate.Release();
            }
        }

        /// <summary>Called inside IngestPipeline's existing write transaction.</summary>
        internal static void RecordMessage(
            SqliteConnection conn,
            SqliteTransaction tx,
            string authorIdentityId,
            string conversationId,
            long sentAt,
            long capturedAt)
        {
            using (var person = conn.CreateCommand())
            {
                person.Transaction = tx;
                person.CommandText = @"
INSERT INTO person_message_counts(person_id, message_count, first_message_at, last_message_at, updated_at)
SELECT person_id, 1, $sent, $sent, $captured FROM platform_identities WHERE identity_id=$identity
ON CONFLICT(person_id) DO UPDATE SET
    message_count=person_message_counts.message_count+1,
    first_message_at=MIN(person_message_counts.first_message_at, excluded.first_message_at),
    last_message_at=MAX(person_message_counts.last_message_at, excluded.last_message_at),
    updated_at=excluded.updated_at;";
                person.Parameters.AddWithValue("$identity", authorIdentityId);
                person.Parameters.AddWithValue("$sent", sentAt);
                person.Parameters.AddWithValue("$captured", capturedAt);
                person.ExecuteNonQuery();
            }

            using var conversation = conn.CreateCommand();
            conversation.Transaction = tx;
            conversation.CommandText = @"
INSERT INTO conversation_message_counts(conversation_id, message_count, first_message_at, last_message_at, updated_at)
VALUES($conversation, 1, $sent, $sent, $captured)
ON CONFLICT(conversation_id) DO UPDATE SET
    message_count=conversation_message_counts.message_count+1,
    first_message_at=MIN(conversation_message_counts.first_message_at, excluded.first_message_at),
    last_message_at=MAX(conversation_message_counts.last_message_at, excluded.last_message_at),
    updated_at=excluded.updated_at;";
            conversation.Parameters.AddWithValue("$conversation", conversationId);
            conversation.Parameters.AddWithValue("$sent", sentAt);
            conversation.Parameters.AddWithValue("$captured", capturedAt);
            conversation.ExecuteNonQuery();
        }

        /// <summary>Repairs a person's count after identities are merged or reassigned.</summary>
        internal static void RebuildPerson(SqliteConnection conn, SqliteTransaction tx, string personId, long now)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
DELETE FROM person_message_counts WHERE person_id=$person;
INSERT INTO person_message_counts(person_id, message_count, first_message_at, last_message_at, updated_at)
SELECT $person, COUNT(*), MIN(m.sent_at), MAX(m.sent_at), $now
FROM messages m
JOIN platform_identities pi ON pi.identity_id=m.author_identity_id
WHERE pi.person_id=$person
HAVING COUNT(*) > 0;";
            cmd.Parameters.AddWithValue("$person", personId);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }
    }

    public sealed record ActivitySummaryStatus(bool Ready, bool Running, string? LastError, long CompletedAt);
}
