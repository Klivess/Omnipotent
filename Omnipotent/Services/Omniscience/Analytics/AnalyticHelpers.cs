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
        // Conversational context, used for facet splits (people behave differently per room).
        public string ConversationKind = "";
        public string? GuildId;
        public string? GuildName;
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

        /// <summary>
        /// Resolves the person's raw platform user ids (e.g. Discord snowflakes). Event
        /// tables (presence, reactions, voice, typing) are keyed by these rather than
        /// identity_id because events can arrive before an identity row exists.
        /// </summary>
        public static List<string> GetPersonPlatformUserIds(SqliteConnection conn, string personId)
        {
            var ids = new List<string>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT platform_user_id FROM platform_identities
                WHERE person_id=$p
                   OR person_id IN (SELECT person_id FROM persons WHERE merged_into_person_id=$p)";
            cmd.Parameters.AddWithValue("$p", personId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetString(0));
            return ids;
        }

        /// <summary>Builds a named-parameter IN clause and binds the values.</summary>
        public static string BindInClause(Microsoft.Data.Sqlite.SqliteCommand cmd, string prefix, List<string> values)
        {
            var names = new List<string>(values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                string p = "$" + prefix + i;
                names.Add(p);
                cmd.Parameters.AddWithValue(p, values[i]);
            }
            return string.Join(",", names);
        }

        /// <summary>Loads messages authored by any identity belonging to <paramref name="personId"/>.</summary>
        public static List<AnalyticMessage> LoadMessages(SqliteConnection conn, string personId, int? limit = null)
        {
            var ids = GetPersonIdentityIds(conn, personId);
            var list = new List<AnalyticMessage>();
            if (ids.Count == 0) return list;

            // Microsoft.Data.Sqlite requires named parameters; positional '?' bindings
            // are not supported and raise "Must add values for the following parameters".
            var paramNames = new List<string>(ids.Count);
            for (int i = 0; i < ids.Count; i++) paramNames.Add("$i" + i);
            string inClause = string.Join(",", paramNames);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"SELECT m.message_id, m.conversation_id, m.author_identity_id, m.sent_at, m.content,
                       c.kind, c.guild_id, c.guild_name
                FROM messages m LEFT JOIN conversations c ON m.conversation_id = c.conversation_id
                WHERE m.author_identity_id IN ({inClause})
                ORDER BY m.sent_at ASC" + (limit.HasValue ? $" LIMIT {limit.Value}" : "");
            for (int i = 0; i < ids.Count; i++) cmd.Parameters.AddWithValue(paramNames[i], ids[i]);
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
                    ConversationKind = r.IsDBNull(5) ? "" : r.GetString(5),
                    GuildId = r.IsDBNull(6) ? null : r.GetString(6),
                    GuildName = r.IsDBNull(7) ? null : r.GetString(7),
                });
            }
            return list;
        }
    }
}
