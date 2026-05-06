using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace Omnipotent.Services.Omniscience.Analytics
{
    /// <summary>Lightweight projection of a message used by analytic modules.</summary>
    public class AnalyticMessage
    {
        public string MessageId = "";
        public string ConversationId = "";
        public string AuthorIdentityId = "";
        public DateTime SentAt;
        public string Content = "";
    }

    public static class AnalyticHelpers
    {
        /// <summary>Resolves all identity_ids belonging to a person (including merged-in identities).</summary>
        public static List<string> GetPersonIdentityIds(SqliteConnection conn, string personId)
        {
            var ids = new List<string>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT identity_id FROM platform_identities
                WHERE person_id=$p
                   OR person_id IN (SELECT person_id FROM persons WHERE merged_into_person_id=$p)";
            cmd.Parameters.AddWithValue("$p", personId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetString(0));
            return ids;
        }

        /// <summary>Loads messages authored by any identity belonging to <paramref name="personId"/>.</summary>
        public static List<AnalyticMessage> LoadMessages(SqliteConnection conn, string personId, int? limit = null)
        {
            var ids = GetPersonIdentityIds(conn, personId);
            var list = new List<AnalyticMessage>();
            if (ids.Count == 0) return list;

            string inClause = string.Join(",", ids.ConvertAll(_ => "?"));
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"SELECT message_id, conversation_id, author_identity_id, sent_at, content
                FROM messages WHERE author_identity_id IN ({inClause})
                ORDER BY sent_at ASC" + (limit.HasValue ? $" LIMIT {limit.Value}" : "");
            foreach (var i in ids) cmd.Parameters.AddWithValue("?", i);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new AnalyticMessage
                {
                    MessageId = r.GetString(0),
                    ConversationId = r.GetString(1),
                    AuthorIdentityId = r.GetString(2),
                    SentAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)).UtcDateTime,
                    Content = r.IsDBNull(4) ? "" : r.GetString(4),
                });
            }
            return list;
        }
    }
}
