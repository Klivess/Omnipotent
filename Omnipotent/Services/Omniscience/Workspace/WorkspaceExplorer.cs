using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.Omniscience.Analytics;
using System.Text;

namespace Omnipotent.Services.Omniscience.Workspace
{
    /// <summary>Read-side queries for the Omniscience investigation workspace.</summary>
    public sealed class WorkspaceExplorer
    {
        private readonly OmniscienceDb db;
        private readonly Func<ActivitySummaryStatus>? getActivityStatus;
        public WorkspaceExplorer(Omniscience service) : this(service.Db, () => service.ActivitySummaries.GetStatus()) { }
        public WorkspaceExplorer(OmniscienceDb db, Func<ActivitySummaryStatus>? getActivityStatus = null)
        {
            this.db = db;
            this.getActivityStatus = getActivityStatus;
        }

        public JObject Capabilities() => new()
        {
            ["workspace_version"] = "2.0",
            ["replica_version"] = "temporal-v2",
            ["features"] = new JArray(
                "fast_directory", "unified_command_search", "advanced_message_search", "saved_investigations",
                "recent_queries", "query_export", "pinned_people", "operator_notes", "evidence_timeline",
                "activity_heatmap", "person_comparison", "relationship_graph", "dossier_export", "data_health",
                "temporal_replicas", "replica_contexts", "replica_era_comparison", "replica_evidence",
                "replica_feedback", "replica_memory", "replica_readiness"),
        };

        public JObject Directory(string? search, string? tier, string? platform, bool pinnedOnly,
            string? sort, int limit, int offset)
        {
            limit = Math.Clamp(limit, 1, 200);
            offset = Math.Clamp(offset, 0, 10000);
            using var conn = db.Open();
            var where = new List<string> { "p.merged_into_person_id IS NULL" };
            var parameters = new List<(string Name, object Value)>();
            if (!string.IsNullOrWhiteSpace(search))
            {
                where.Add(@"(p.display_name LIKE $search OR EXISTS(SELECT 1 FROM platform_identities i WHERE i.person_id=p.person_id AND (i.display_name LIKE $search OR i.platform_username LIKE $search))
                    OR EXISTS(SELECT 1 FROM identity_alt_names a JOIN platform_identities i ON i.identity_id=a.identity_id WHERE i.person_id=p.person_id AND a.alt_name LIKE $search))");
                parameters.Add(("$search", "%" + search.Trim() + "%"));
            }
            if (!string.IsNullOrWhiteSpace(tier) && tier != "all") { where.Add("COALESCE(p.tier,'archive')=$tier"); parameters.Add(("$tier", tier)); }
            if (!string.IsNullOrWhiteSpace(platform) && platform != "all")
            {
                where.Add("EXISTS(SELECT 1 FROM platform_identities i WHERE i.person_id=p.person_id AND i.platform=$platform)");
                parameters.Add(("$platform", platform));
            }
            if (pinnedOnly) where.Add("EXISTS(SELECT 1 FROM workspace_pins w WHERE w.person_id=p.person_id)");
            string whereSql = string.Join(" AND ", where);
            long total;
            using (var count = conn.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM persons p WHERE " + whereSql;
                Bind(count, parameters);
                total = Convert.ToInt64(count.ExecuteScalar() ?? 0);
            }
            string order = sort switch
            {
                "name" => "p.display_name COLLATE NOCASE ASC",
                "recent" => "COALESCE(c.last_message_at,0) DESC, p.display_name COLLATE NOCASE",
                "completeness" => "COALESCE(json_extract(ps.payload_json,'$.completeness_score'),0) DESC, COALESCE(c.message_count,0) DESC",
                "pinned" => "COALESCE(w.sort_order,999999), COALESCE(c.message_count,0) DESC",
                _ => "COALESCE(c.message_count,0) DESC, COALESCE(c.last_message_at,0) DESC",
            };
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"SELECT p.person_id,p.display_name,COALESCE(p.tier,'archive'),p.created_at,p.updated_at,
                COALESCE(c.message_count,0),c.first_message_at,c.last_message_at,
                (SELECT GROUP_CONCAT(platform||':'||COALESCE(platform_username,''),'|') FROM platform_identities WHERE person_id=p.person_id),
                (SELECT json_group_array(json_object('platform',platform,'username',platform_username,'display_name',display_name)) FROM platform_identities WHERE person_id=p.person_id),
                EXISTS(SELECT 1 FROM person_profile_targets t WHERE t.person_id=p.person_id AND t.enabled=1),
                EXISTS(SELECT 1 FROM workspace_pins w WHERE w.person_id=p.person_id),ps.payload_json
                FROM persons p LEFT JOIN person_message_counts c ON c.person_id=p.person_id
                LEFT JOIN workspace_pins w ON w.person_id=p.person_id
                LEFT JOIN person_statistics ps ON ps.person_id=p.person_id AND ps.module_name='deduction_summary'
                WHERE {whereSql} ORDER BY {order} LIMIT $limit OFFSET $offset";
            Bind(cmd, parameters);
            cmd.Parameters.AddWithValue("$limit", limit);
            cmd.Parameters.AddWithValue("$offset", offset);
            var items = new JArray();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                JArray identities;
                try { identities = JArray.Parse(r.IsDBNull(9) ? "[]" : r.GetString(9)); } catch { identities = new(); }
                double? completeness = null;
                if (!r.IsDBNull(12)) { try { completeness = JObject.Parse(r.GetString(12)).Value<double?>("completeness_score"); } catch { } }
                items.Add(new JObject
                {
                    ["person_id"] = r.GetString(0), ["display_name"] = r.GetString(1), ["tier"] = r.GetString(2),
                    ["created_at"] = r.GetInt64(3), ["updated_at"] = r.GetInt64(4), ["message_count"] = r.GetInt64(5),
                    ["first_message_at"] = r.IsDBNull(6) ? null : (long?)r.GetInt64(6),
                    ["last_message_at"] = r.IsDBNull(7) ? null : (long?)r.GetInt64(7),
                    ["handles"] = r.IsDBNull(8) ? "" : r.GetString(8), ["identities"] = identities,
                    ["profile_targeted"] = r.GetInt32(10) == 1, ["pinned"] = r.GetInt32(11) == 1,
                    ["completeness"] = completeness,
                });
            }
            return new JObject
            {
                ["items"] = items, ["total"] = total, ["limit"] = limit, ["offset"] = offset,
                ["has_more"] = offset + items.Count < total, ["activity_counts"] = ActivityPayload(),
            };
        }

        public JObject CommandSearch(string query, int limit)
        {
            query = (query ?? "").Trim();
            if (query.Length < 2) return new JObject { ["results"] = new JArray(), ["query"] = query };
            limit = Math.Clamp(limit, 5, 60);
            string like = "%" + query + "%";
            var results = new JArray();
            using var conn = db.Open();
            Read(conn, @"SELECT p.person_id,p.display_name,COALESCE(p.tier,'archive'),COALESCE(c.message_count,0)
                FROM persons p LEFT JOIN person_message_counts c ON c.person_id=p.person_id
                WHERE p.merged_into_person_id IS NULL AND (p.display_name LIKE $q OR EXISTS(SELECT 1 FROM platform_identities i WHERE i.person_id=p.person_id AND (i.platform_username LIKE $q OR i.display_name LIKE $q)))
                ORDER BY COALESCE(c.message_count,0) DESC LIMIT $n", cmd =>
            {
                cmd.Parameters.AddWithValue("$q", like); cmd.Parameters.AddWithValue("$n", limit);
            }, r => results.Add(new JObject
            {
                ["kind"] = "person", ["item_id"] = r.GetString(0), ["person_id"] = r.GetString(0),
                ["title"] = r.GetString(1), ["subtitle"] = $"{r.GetString(2)} · {r.GetInt64(3):N0} messages",
                ["url"] = "/omniscience-person?personId=" + Uri.EscapeDataString(r.GetString(0)),
            }));
            Read(conn, @"SELECT f.fact_id,f.person_id,p.display_name,f.category,f.fact_text,f.updated_at,f.evidence_message_ids_json
                FROM person_facts f JOIN persons p ON p.person_id=f.person_id
                WHERE f.status='active' AND (f.fact_text LIKE $q OR f.category LIKE $q)
                ORDER BY f.confidence DESC,f.updated_at DESC LIMIT $n", cmd =>
            {
                cmd.Parameters.AddWithValue("$q", like); cmd.Parameters.AddWithValue("$n", limit);
            }, r => results.Add(new JObject
            {
                ["kind"] = "fact", ["item_id"] = r.GetString(0), ["person_id"] = r.GetString(1),
                ["title"] = r.GetString(4), ["subtitle"] = $"{r.GetString(2)} · {r.GetString(3)}",
                ["at"] = r.GetInt64(5), ["evidence"] = ParseArray(r.IsDBNull(6) ? "[]" : r.GetString(6)),
                ["url"] = "/omniscience-person?personId=" + Uri.EscapeDataString(r.GetString(1)) + "&tab=Knowledge",
            }));
            Read(conn, @"SELECT n.note_id,n.person_id,p.display_name,n.content,n.tags,n.updated_at
                FROM workspace_notes n JOIN persons p ON p.person_id=n.person_id
                WHERE n.content LIKE $q OR n.tags LIKE $q ORDER BY n.updated_at DESC LIMIT $n", cmd =>
            {
                cmd.Parameters.AddWithValue("$q", like); cmd.Parameters.AddWithValue("$n", limit);
            }, r => results.Add(new JObject
            {
                ["kind"] = "note", ["item_id"] = r.GetString(0), ["person_id"] = r.GetString(1),
                ["title"] = Truncate(r.GetString(3), 140), ["subtitle"] = $"{r.GetString(2)} · {r.GetString(4)}",
                ["at"] = r.GetInt64(5), ["url"] = "/omniscience-person?personId=" + Uri.EscapeDataString(r.GetString(1)) + "&tab=Notes",
            }));
            Read(conn, "SELECT query_id,name,query_json,updated_at FROM workspace_saved_queries WHERE name LIKE $q OR query_json LIKE $q ORDER BY updated_at DESC LIMIT 10", cmd =>
                cmd.Parameters.AddWithValue("$q", like), r => results.Add(new JObject
            {
                ["kind"] = "saved_query", ["item_id"] = r.GetString(0), ["title"] = r.GetString(1),
                ["subtitle"] = "Saved investigation", ["query"] = ParseObject(r.GetString(2)), ["at"] = r.GetInt64(3),
                ["url"] = "/omniscience-explore?saved=" + Uri.EscapeDataString(r.GetString(0)),
            }));
            return new JObject { ["query"] = query, ["results"] = results, ["count"] = results.Count };
        }

        public JObject SearchMessages(JObject filters, int limit, int offset)
        {
            limit = Math.Clamp(limit, 1, 200);
            offset = Math.Clamp(offset, 0, 10000);
            string? q = Clean(filters.Value<string>("q"), 200);
            string? personId = Clean(filters.Value<string>("personId"), 100);
            string? conversationId = Clean(filters.Value<string>("conversationId"), 200);
            long? from = filters.Value<long?>("from");
            long? to = filters.Value<long?>("to");
            bool globalUnbounded = string.IsNullOrEmpty(personId) && string.IsNullOrEmpty(conversationId) && from == null && to == null;
            if (globalUnbounded)
                throw new ArgumentException("Scope message search by person, conversation or date; use semantic search for unbounded meaning queries.");
            using var conn = db.Open();
            using var cmd = conn.CreateCommand();
            var where = new List<string> { "1=1" };
            if (!string.IsNullOrEmpty(q)) { where.Add("m.content LIKE $q"); cmd.Parameters.AddWithValue("$q", "%" + q + "%"); }
            if (!string.IsNullOrEmpty(personId))
            {
                where.Add("(pi.person_id=$person OR pi.person_id IN(SELECT person_id FROM persons WHERE merged_into_person_id=$person))");
                cmd.Parameters.AddWithValue("$person", personId);
            }
            if (!string.IsNullOrEmpty(conversationId)) { where.Add("m.conversation_id=$conversation"); cmd.Parameters.AddWithValue("$conversation", conversationId); }
            if (from != null) { where.Add("m.sent_at >= $from"); cmd.Parameters.AddWithValue("$from", from.Value); }
            if (to != null) { where.Add("m.sent_at < $to"); cmd.Parameters.AddWithValue("$to", to.Value); }
            string? context = Clean(filters.Value<string>("contextKind"), 40);
            if (!string.IsNullOrEmpty(context) && context != "all") { where.Add("c.kind=$kind"); cmd.Parameters.AddWithValue("$kind", context); }
            string? platform = Clean(filters.Value<string>("platform"), 40);
            if (!string.IsNullOrEmpty(platform) && platform != "all") { where.Add("m.platform=$platform"); cmd.Parameters.AddWithValue("$platform", platform); }
            cmd.CommandText = $@"SELECT m.message_id,m.sent_at,m.content,m.conversation_id,c.title,c.kind,c.guild_name,
                pi.person_id,p.display_name,m.platform FROM messages m
                JOIN platform_identities pi ON pi.identity_id=m.author_identity_id
                LEFT JOIN persons p ON p.person_id=pi.person_id
                LEFT JOIN conversations c ON c.conversation_id=m.conversation_id
                WHERE {string.Join(" AND ", where)} ORDER BY m.sent_at DESC LIMIT $limit OFFSET $offset";
            cmd.Parameters.AddWithValue("$limit", limit + 1);
            cmd.Parameters.AddWithValue("$offset", offset);
            var items = new JArray();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                items.Add(new JObject
                {
                    ["message_id"] = r.GetString(0), ["sent_at"] = r.GetInt64(1), ["content"] = r.IsDBNull(2) ? "" : r.GetString(2),
                    ["conversation_id"] = r.GetString(3), ["conversation_title"] = r.IsDBNull(4) ? null : r.GetString(4),
                    ["context_kind"] = r.IsDBNull(5) ? null : r.GetString(5), ["guild_name"] = r.IsDBNull(6) ? null : r.GetString(6),
                    ["person_id"] = r.GetString(7), ["author_display"] = r.IsDBNull(8) ? "Unknown" : r.GetString(8), ["platform"] = r.GetString(9),
                });
            }
            bool hasMore = items.Count > limit;
            if (hasMore) items.RemoveAt(items.Count - 1);
            return new JObject { ["items"] = items, ["has_more"] = hasMore, ["limit"] = limit, ["offset"] = offset, ["filters"] = filters };
        }

        public JObject Timeline(string personId, long? from, long? to, int limit, string? types)
        {
            limit = Math.Clamp(limit, 10, 200);
            var enabled = new HashSet<string>((types ?? "message,fact,identity,profile,change,note").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            var events = new List<JObject>();
            using var conn = db.Open();
            var idents = AnalyticHelpers.GetPersonIdentityIds(conn, personId);
            if (enabled.Contains("message") && idents.Count > 0)
            {
                using var cmd = conn.CreateCommand();
                string inC = AnalyticHelpers.BindInClause(cmd, "i", idents);
                cmd.CommandText = $@"SELECT m.message_id,m.sent_at,m.content,m.conversation_id,c.title,c.kind FROM messages m
                    LEFT JOIN conversations c ON c.conversation_id=m.conversation_id WHERE m.author_identity_id IN ({inC}) {TimeWhere("m.sent_at", from, to)}
                    ORDER BY m.sent_at DESC LIMIT $limit";
                TimeParams(cmd, from, to, limit);
                using var r = cmd.ExecuteReader();
                while (r.Read()) events.Add(Event("message", r.GetString(0), r.GetInt64(1), "Message", r.IsDBNull(2) ? "" : r.GetString(2),
                    r.IsDBNull(4) ? r.GetString(3) : r.GetString(4), personId, null, new JObject { ["conversation_id"] = r.GetString(3), ["context_kind"] = r.IsDBNull(5) ? null : r.GetString(5) }));
            }
            if (enabled.Contains("fact"))
                Read(conn, $@"SELECT fact_id,updated_at,category,fact_text,source_context,evidence_message_ids_json FROM person_facts
                    WHERE person_id=$p {TimeWhere("updated_at", from, to)} ORDER BY updated_at DESC LIMIT $limit", cmd =>
                    { cmd.Parameters.AddWithValue("$p", personId); TimeParams(cmd, from, to, limit); }, r =>
                    events.Add(Event("fact", r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), personId,
                        ParseArray(r.IsDBNull(5) ? "[]" : r.GetString(5)), null)));
            if (enabled.Contains("identity"))
                Read(conn, $@"SELECT h.history_id,h.changed_at,h.field,h.old_value,h.new_value,i.platform FROM identity_history h
                    JOIN platform_identities i ON i.identity_id=h.identity_id
                    WHERE (i.person_id=$p OR i.person_id IN(SELECT person_id FROM persons WHERE merged_into_person_id=$p)) {TimeWhere("h.changed_at", from, to)}
                    ORDER BY h.changed_at DESC LIMIT $limit", cmd => { cmd.Parameters.AddWithValue("$p", personId); TimeParams(cmd, from, to, limit); }, r =>
                    events.Add(Event("identity", r.GetInt64(0).ToString(), r.GetInt64(1), "Identity · " + r.GetString(2),
                        $"{(r.IsDBNull(3) ? "∅" : r.GetString(3))} → {(r.IsDBNull(4) ? "∅" : r.GetString(4))}", r.GetString(5), personId, null, null)));
            if (enabled.Contains("profile"))
                Read(conn, $@"SELECT profile_id,generated_at,profile_markdown,era FROM personality_profiles
                    WHERE person_id=$p {TimeWhere("generated_at", from, to)} ORDER BY generated_at DESC LIMIT $limit", cmd =>
                    { cmd.Parameters.AddWithValue("$p", personId); TimeParams(cmd, from, to, limit); }, r =>
                    events.Add(Event("profile", r.GetString(0), r.GetInt64(1), r.IsDBNull(3) ? "Current personality profile" : "Era profile · " + r.GetString(3),
                        Truncate(r.IsDBNull(2) ? "" : r.GetString(2), 1000), "personality_profiler", personId, null, null)));
            if (enabled.Contains("change"))
                Read(conn, $@"SELECT changelog_id,generated_at,changes_markdown FROM profile_changelogs
                    WHERE person_id=$p {TimeWhere("generated_at", from, to)} ORDER BY generated_at DESC LIMIT $limit", cmd =>
                    { cmd.Parameters.AddWithValue("$p", personId); TimeParams(cmd, from, to, limit); }, r =>
                    events.Add(Event("change", r.GetString(0), r.GetInt64(1), "Profile changes", r.GetString(2), "personality_profiler", personId, null, null)));
            if (enabled.Contains("note"))
                Read(conn, $@"SELECT note_id,updated_at,content,tags FROM workspace_notes
                    WHERE person_id=$p {TimeWhere("updated_at", from, to)} ORDER BY updated_at DESC LIMIT $limit", cmd =>
                    { cmd.Parameters.AddWithValue("$p", personId); TimeParams(cmd, from, to, limit); }, r =>
                    events.Add(Event("note", r.GetString(0), r.GetInt64(1), "Operator note", r.GetString(2), r.IsDBNull(3) ? "operator" : r.GetString(3), personId, null, null)));
            var ordered = new JArray(events.OrderByDescending(e => e.Value<long>("at")).Take(limit));
            return new JObject { ["person_id"] = personId, ["events"] = ordered, ["count"] = ordered.Count, ["from"] = from, ["to"] = to };
        }

        public JObject Activity(string personId, int days)
        {
            days = Math.Clamp(days, 7, 1095);
            long to = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long from = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds();
            long previousFrom = DateTimeOffset.UtcNow.AddDays(-days * 2).ToUnixTimeMilliseconds();
            using var conn = db.Open();
            var idents = AnalyticHelpers.GetPersonIdentityIds(conn, personId);
            if (idents.Count == 0) return new JObject { ["daily"] = new JArray(), ["hourly"] = new JArray(), ["top_conversations"] = new JArray() };
            string inC;
            var daily = new JArray();
            using (var cmd = conn.CreateCommand())
            {
                inC = AnalyticHelpers.BindInClause(cmd, "i", idents);
                cmd.CommandText = $@"SELECT strftime('%Y-%m-%d',m.sent_at/1000,'unixepoch'),COUNT(*) FROM messages m
                    WHERE m.author_identity_id IN ({inC}) AND m.sent_at >= $from AND m.sent_at < $to GROUP BY 1 ORDER BY 1";
                cmd.Parameters.AddWithValue("$from", from); cmd.Parameters.AddWithValue("$to", to);
                using var r = cmd.ExecuteReader();
                while (r.Read()) daily.Add(new JObject { ["day"] = r.GetString(0), ["count"] = r.GetInt64(1) });
            }
            var hourlyRaw = new int[24];
            long current = 0, previous = 0;
            using (var cmd = conn.CreateCommand())
            {
                inC = AnalyticHelpers.BindInClause(cmd, "i", idents);
                cmd.CommandText = $@"SELECT CAST(strftime('%H',m.sent_at/1000,'unixepoch') AS INTEGER),COUNT(*),
                    SUM(CASE WHEN m.sent_at >= $from THEN 1 ELSE 0 END),SUM(CASE WHEN m.sent_at < $from THEN 1 ELSE 0 END)
                    FROM messages m WHERE m.author_identity_id IN ({inC}) AND m.sent_at >= $previousFrom AND m.sent_at < $to
                    GROUP BY 1";
                cmd.Parameters.AddWithValue("$from", from); cmd.Parameters.AddWithValue("$to", to); cmd.Parameters.AddWithValue("$previousFrom", previousFrom);
                using var r = cmd.ExecuteReader();
                while (r.Read()) { hourlyRaw[r.GetInt32(0)] = r.GetInt32(1); current += r.GetInt64(2); previous += r.GetInt64(3); }
            }
            var hourly = new JArray(Enumerable.Range(0, 24).Select(h => new JObject { ["hour"] = h, ["count"] = hourlyRaw[h] }));
            var top = new JArray();
            using (var cmd = conn.CreateCommand())
            {
                inC = AnalyticHelpers.BindInClause(cmd, "i", idents);
                cmd.CommandText = $@"SELECT m.conversation_id,c.title,c.kind,COUNT(*) FROM messages m
                    LEFT JOIN conversations c ON c.conversation_id=m.conversation_id
                    WHERE m.author_identity_id IN ({inC}) AND m.sent_at >= $from AND m.sent_at < $to
                    GROUP BY m.conversation_id ORDER BY 4 DESC LIMIT 8";
                cmd.Parameters.AddWithValue("$from", from); cmd.Parameters.AddWithValue("$to", to);
                using var r = cmd.ExecuteReader();
                while (r.Read()) top.Add(new JObject { ["conversation_id"] = r.GetString(0), ["title"] = r.IsDBNull(1) ? r.GetString(0) : r.GetString(1),
                    ["context_kind"] = r.IsDBNull(2) ? null : r.GetString(2), ["count"] = r.GetInt64(3) });
            }
            double trend = previous == 0 ? (current > 0 ? 100 : 0) : (current - previous) * 100.0 / previous;
            return new JObject
            {
                ["person_id"] = personId, ["days"] = days, ["daily"] = daily, ["hourly"] = hourly,
                ["top_conversations"] = top, ["current_messages"] = current, ["previous_messages"] = previous,
                ["trend_pct"] = Math.Round(trend, 1), ["active_days"] = daily.Count,
                ["average_per_active_day"] = daily.Count == 0 ? 0 : Math.Round(current / (double)daily.Count, 1),
            };
        }

        public JObject Compare(string leftId, string rightId)
        {
            using var conn = db.Open();
            var left = PersonSummary(conn, leftId);
            var right = PersonSummary(conn, rightId);
            var leftIdentities = AnalyticHelpers.GetPersonIdentityIds(conn, leftId);
            var rightIdentities = AnalyticHelpers.GetPersonIdentityIds(conn, rightId);
            var shared = new JArray();
            if (leftIdentities.Count > 0 && rightIdentities.Count > 0)
            {
                using var cmd = conn.CreateCommand();
                string leftIn = AnalyticHelpers.BindInClause(cmd, "l", leftIdentities);
                string rightIn = AnalyticHelpers.BindInClause(cmd, "r", rightIdentities);
                cmd.CommandText = $@"SELECT cp1.conversation_id,c.title,c.kind FROM conversation_participants cp1
                    JOIN conversation_participants cp2 ON cp2.conversation_id=cp1.conversation_id
                    LEFT JOIN conversations c ON c.conversation_id=cp1.conversation_id
                    WHERE cp1.identity_id IN ({leftIn}) AND cp2.identity_id IN ({rightIn})
                    GROUP BY cp1.conversation_id ORDER BY c.last_seen DESC LIMIT 20";
                using var r = cmd.ExecuteReader();
                while (r.Read()) shared.Add(new JObject { ["conversation_id"] = r.GetString(0), ["title"] = r.IsDBNull(1) ? r.GetString(0) : r.GetString(1),
                    ["context_kind"] = r.IsDBNull(2) ? null : r.GetString(2) });
            }
            var links = new JArray();
            Read(conn, @"SELECT rel_id,owner_person_id,related_person_id,rel_type,confidence,valid_from,valid_to FROM entity_relationships
                WHERE (owner_person_id=$left AND related_person_id=$right) OR (owner_person_id=$right AND related_person_id=$left)", cmd =>
            {
                cmd.Parameters.AddWithValue("$left", leftId); cmd.Parameters.AddWithValue("$right", rightId);
            }, r => links.Add(new JObject
            {
                ["rel_id"] = r.GetInt64(0), ["owner_person_id"] = r.GetString(1), ["related_person_id"] = r.GetString(2),
                ["rel_type"] = r.GetString(3), ["confidence"] = r.GetDouble(4),
                ["valid_from"] = r.IsDBNull(5) ? null : (long?)r.GetInt64(5), ["valid_to"] = r.IsDBNull(6) ? null : (long?)r.GetInt64(6),
            }));
            return new JObject
            {
                ["left"] = left, ["right"] = right, ["shared_conversations"] = shared,
                ["shared_conversation_count"] = shared.Count, ["relationship_links"] = links,
                ["message_ratio"] = Ratio(left.Value<long>("message_count"), right.Value<long>("message_count")),
            };
        }

        public JObject Graph(string personId, int depth, int limit)
        {
            depth = Math.Clamp(depth, 1, 2);
            limit = Math.Clamp(limit, 10, 150);
            using var conn = db.Open();
            var edges = new List<JObject>();
            LoadEdges(conn, new[] { personId }, depth == 1 ? personId : null, limit, edges);
            if (depth == 2)
            {
                var neighbours = edges.Select(e => e.Value<string>("source")).Concat(edges.Select(e => e.Value<string>("target")))
                    .Where(id => id != null && !id.StartsWith("entity:") && id != personId).Distinct().Take(30).ToArray();
                if (neighbours.Length > 0) LoadEdges(conn, neighbours, null, limit * 2, edges);
            }
            var distinct = edges.GroupBy(e => e.Value<long>("rel_id")).Select(g => g.First()).Take(limit).ToList();
            var personIds = distinct.Select(e => e.Value<string>("source")).Concat(distinct.Select(e => e.Value<string>("target")))
                .Where(id => id != null && !id.StartsWith("entity:")).Distinct().ToList();
            personIds.Add(personId);
            var nodes = new JArray();
            var added = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in personIds.Distinct())
            {
                if (!added.Add(id)) continue;
                var summary = PersonSummary(conn, id);
                nodes.Add(new JObject { ["id"] = id, ["kind"] = "person", ["label"] = summary.Value<string>("display_name"),
                    ["root"] = id == personId, ["message_count"] = summary.Value<long>("message_count"), ["tier"] = summary.Value<string>("tier") });
            }
            foreach (var edge in distinct)
            {
                foreach (string endpoint in new[] { edge.Value<string>("source")!, edge.Value<string>("target")! })
                {
                    if (!endpoint.StartsWith("entity:") || !added.Add(endpoint)) continue;
                    nodes.Add(new JObject { ["id"] = endpoint, ["kind"] = "entity", ["label"] = edge.Value<string>("entity_name") ?? endpoint,
                        ["root"] = false, ["message_count"] = 0 });
                }
            }
            return new JObject { ["person_id"] = personId, ["nodes"] = nodes, ["edges"] = new JArray(distinct), ["depth"] = depth };
        }

        public JObject ExportPerson(string personId, string format)
        {
            using var conn = db.Open();
            var data = PersonSummary(conn, personId);
            var identities = new JArray();
            Read(conn, "SELECT platform,platform_username,display_name,bio,first_seen,last_seen FROM platform_identities WHERE person_id=$p", cmd =>
                cmd.Parameters.AddWithValue("$p", personId), r => identities.Add(new JObject
            {
                ["platform"] = r.GetString(0), ["username"] = r.IsDBNull(1) ? null : r.GetString(1),
                ["display_name"] = r.IsDBNull(2) ? null : r.GetString(2), ["bio"] = r.IsDBNull(3) ? null : r.GetString(3),
                ["first_seen"] = r.IsDBNull(4) ? null : (long?)r.GetInt64(4), ["last_seen"] = r.IsDBNull(5) ? null : (long?)r.GetInt64(5),
            }));
            data["identities"] = identities;
            var facts = new JArray();
            Read(conn, @"SELECT fact_id,category,fact_text,confidence,first_evidence_at,last_evidence_at,evidence_message_ids_json,extracted_by
                FROM person_facts WHERE person_id=$p AND status='active' ORDER BY category,confidence DESC", cmd =>
                cmd.Parameters.AddWithValue("$p", personId), r => facts.Add(new JObject
            {
                ["fact_id"] = r.GetString(0), ["category"] = r.GetString(1), ["fact"] = r.GetString(2), ["confidence"] = r.GetDouble(3),
                ["first_evidence_at"] = r.IsDBNull(4) ? null : (long?)r.GetInt64(4), ["last_evidence_at"] = r.IsDBNull(5) ? null : (long?)r.GetInt64(5),
                ["evidence_message_ids"] = ParseArray(r.IsDBNull(6) ? "[]" : r.GetString(6)), ["extracted_by"] = r.GetString(7),
            }));
            data["facts"] = facts;
            var notes = new WorkspaceStore(db).ListNotes(personId);
            data["operator_notes"] = notes;
            data["exported_at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            data["export_notice"] = "Generated export: deductions are confidence-scored, not guaranteed facts; simulation chats are excluded.";
            string name = data.Value<string>("display_name") ?? personId;
            string content;
            string mime;
            string extension;
            if (string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase) || string.Equals(format, "md", StringComparison.OrdinalIgnoreCase))
            {
                content = PersonMarkdown(data);
                mime = "text/markdown";
                extension = "md";
            }
            else
            {
                content = data.ToString(Formatting.Indented);
                mime = "application/json";
                extension = "json";
            }
            return new JObject { ["filename"] = $"{SafeFileName(name)}-dossier.{extension}", ["mime"] = mime, ["content"] = content };
        }

        public JObject Health()
        {
            using var conn = db.Open();
            long Scalar(string sql)
            {
                try { using var cmd = conn.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar() ?? 0); }
                catch { return 0; }
            }
            long messages = Scalar("SELECT COUNT(*) FROM messages");
            long trackedMessages = Scalar("SELECT COALESCE(SUM(c.message_count),0) FROM person_message_counts c JOIN persons p ON p.person_id=c.person_id WHERE p.tier='tracked' AND p.merged_into_person_id IS NULL");
            long trackedEmbedded = Scalar(@"SELECT COUNT(*) FROM message_embeddings e JOIN messages m ON m.message_id=e.message_id
                JOIN platform_identities i ON i.identity_id=m.author_identity_id JOIN persons p ON p.person_id=i.person_id WHERE p.tier='tracked'");
            long failedJobs = Scalar("SELECT COUNT(*) FROM replica_training_jobs WHERE status='failed' AND started_at >= " + DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeMilliseconds());
            var sources = new JArray();
            Read(conn, "SELECT source_id,platform,label,status,last_status_message,last_event_at,last_full_sync_at FROM harvest_sources ORDER BY platform,label", _ => { }, r =>
                sources.Add(new JObject
            {
                ["source_id"] = r.GetString(0), ["platform"] = r.GetString(1), ["label"] = r.IsDBNull(2) ? null : r.GetString(2),
                ["status"] = r.GetString(3), ["status_message"] = r.IsDBNull(4) ? null : r.GetString(4),
                ["last_event_at"] = r.IsDBNull(5) ? null : (long?)r.GetInt64(5), ["last_full_sync_at"] = r.IsDBNull(6) ? null : (long?)r.GetInt64(6),
            }));
            var activity = ActivityPayload();
            var warnings = new JArray();
            if (!activity.Value<bool>("ready")) warnings.Add(activity.Value<bool>("running") ? "Activity summaries are warming" : "Activity summaries need a rebuild");
            if (sources.Count == 0) warnings.Add("No dedicated harvest sources configured; linked live feeds may still be active");
            if (trackedMessages > 0 && trackedEmbedded < trackedMessages / 2) warnings.Add("Tracked-message embeddings are below 50%");
            if (failedJobs > 0) warnings.Add($"{failedJobs} replica training jobs failed in the last 7 days");
            var metrics = new JObject
            {
                ["persons"] = Scalar("SELECT COUNT(*) FROM persons WHERE merged_into_person_id IS NULL"),
                ["messages"] = messages, ["tracked_messages"] = trackedMessages, ["tracked_embedded"] = trackedEmbedded,
                ["tracked_embedding_coverage_pct"] = trackedMessages == 0 ? 0 : Math.Round(trackedEmbedded * 100.0 / trackedMessages, 1),
                ["stimulus_reply_pairs"] = Scalar("SELECT COUNT(*) FROM stimulus_reply_pairs"),
                ["replica_eras"] = Scalar("SELECT COUNT(*) FROM replica_era_profiles"),
                ["replicas_ready"] = Scalar("SELECT COUNT(*) FROM replicas WHERE status IN ('ready','stale')"),
                ["failed_replica_jobs_7d"] = failedJobs, ["saved_queries"] = Scalar("SELECT COUNT(*) FROM workspace_saved_queries"),
                ["operator_notes"] = Scalar("SELECT COUNT(*) FROM workspace_notes"),
                ["latest_message_at"] = Scalar("SELECT COALESCE(MAX(last_message_at),0) FROM person_message_counts"),
            };
            return new JObject
            {
                ["status"] = warnings.Count == 0 ? "healthy" : "attention", ["warnings"] = warnings,
                ["metrics"] = metrics, ["sources"] = sources, ["activity_counts"] = activity,
                ["checked_at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
        }

        public string MessagesCsv(JObject filters)
        {
            filters = (JObject)filters.DeepClone();
            var result = SearchMessages(filters, 200, 0);
            var sb = new StringBuilder("message_id,sent_at,platform,author,conversation,context,content\n");
            foreach (var item in (JArray)result["items"]!)
            {
                string[] fields = { item.Value<string>("message_id") ?? "", item.Value<long>("sent_at").ToString(),
                    item.Value<string>("platform") ?? "", item.Value<string>("author_display") ?? "",
                    item.Value<string>("conversation_title") ?? item.Value<string>("conversation_id") ?? "",
                    item.Value<string>("context_kind") ?? "", item.Value<string>("content") ?? "" };
                sb.AppendLine(string.Join(',', fields.Select(Csv)));
            }
            return sb.ToString();
        }

        private JObject ActivityPayload()
        {
            var status = getActivityStatus?.Invoke() ?? new ActivitySummaryStatus(true, false, null, 1);
            return new JObject { ["ready"] = status.Ready, ["running"] = status.Running, ["last_error"] = status.LastError, ["completed_at"] = status.CompletedAt == 0 ? null : status.CompletedAt };
        }

        private static JObject PersonSummary(SqliteConnection conn, string personId)
        {
            string displayName = personId, tier = "archive";
            Read(conn, "SELECT display_name,COALESCE(tier,'archive') FROM persons WHERE person_id=$p", cmd => cmd.Parameters.AddWithValue("$p", personId), r =>
            { displayName = r.GetString(0); tier = r.GetString(1); });
            long messages = 0, first = 0, last = 0;
            Read(conn, "SELECT message_count,COALESCE(first_message_at,0),COALESCE(last_message_at,0) FROM person_message_counts WHERE person_id=$p", cmd =>
                cmd.Parameters.AddWithValue("$p", personId), r => { messages = r.GetInt64(0); first = r.GetInt64(1); last = r.GetInt64(2); });
            long Scalar(string sql)
            {
                try { using var cmd = conn.CreateCommand(); cmd.CommandText = sql; cmd.Parameters.AddWithValue("$p", personId); return Convert.ToInt64(cmd.ExecuteScalar() ?? 0); }
                catch { return 0; }
            }
            return new JObject
            {
                ["person_id"] = personId, ["display_name"] = displayName, ["tier"] = tier,
                ["message_count"] = messages, ["first_message_at"] = first == 0 ? null : first, ["last_message_at"] = last == 0 ? null : last,
                ["facts"] = Scalar("SELECT COUNT(*) FROM person_facts WHERE person_id=$p AND status='active'"),
                ["relationships"] = Scalar("SELECT COUNT(*) FROM entity_relationships WHERE owner_person_id=$p AND status='active'"),
                ["eras"] = Scalar("SELECT COUNT(*) FROM replica_era_profiles WHERE person_id=$p"),
                ["profiles"] = Scalar("SELECT COUNT(*) FROM personality_profiles WHERE person_id=$p AND era IS NULL"),
            };
        }

        private static void LoadEdges(SqliteConnection conn, IReadOnlyList<string> personIds, string? requireRoot, int limit, List<JObject> output)
        {
            if (personIds.Count == 0) return;
            using var cmd = conn.CreateCommand();
            string inC = string.Join(',', personIds.Select((_, i) => "$p" + i));
            for (int i = 0; i < personIds.Count; i++) cmd.Parameters.AddWithValue("$p" + i, personIds[i]);
            string root = requireRoot == null ? "" : " AND (er.owner_person_id=$root OR er.related_person_id=$root)";
            if (requireRoot != null) cmd.Parameters.AddWithValue("$root", requireRoot);
            cmd.CommandText = $@"SELECT er.rel_id,er.owner_person_id,COALESCE(er.related_person_id,'entity:'||er.entity_id),er.rel_type,
                er.confidence,er.evidence_count,er.valid_from,er.valid_to,er.status,e.canonical_name
                FROM entity_relationships er LEFT JOIN entities e ON e.entity_id=er.entity_id
                WHERE (er.owner_person_id IN ({inC}) OR er.related_person_id IN ({inC}))
                  AND (er.related_person_id IS NOT NULL OR er.entity_id IS NOT NULL) {root}
                ORDER BY er.confidence DESC,er.evidence_count DESC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) output.Add(new JObject
            {
                ["rel_id"] = r.GetInt64(0), ["source"] = r.GetString(1), ["target"] = r.GetString(2), ["rel_type"] = r.GetString(3),
                ["confidence"] = r.GetDouble(4), ["evidence_count"] = r.GetInt32(5),
                ["valid_from"] = r.IsDBNull(6) ? null : (long?)r.GetInt64(6), ["valid_to"] = r.IsDBNull(7) ? null : (long?)r.GetInt64(7),
                ["status"] = r.GetString(8), ["entity_name"] = r.IsDBNull(9) ? null : r.GetString(9),
            });
        }

        private static string PersonMarkdown(JObject data)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# " + data.Value<string>("display_name"));
            sb.AppendLine();
            sb.AppendLine($"Exported {DateTimeOffset.FromUnixTimeMilliseconds(data.Value<long>("exported_at")).UtcDateTime:yyyy-MM-dd HH:mm} UTC. Deductions are confidence-scored, not guaranteed facts.");
            sb.AppendLine();
            sb.AppendLine($"Tier: **{data.Value<string>("tier")}** · Messages: **{data.Value<long>("message_count"):N0}** · Facts: **{data.Value<long>("facts")}** · Relationships: **{data.Value<long>("relationships")}**");
            sb.AppendLine();
            sb.AppendLine("## Linked identities");
            foreach (var identity in (JArray)data["identities"]!)
                sb.AppendLine($"- {identity.Value<string>("platform")}: {identity.Value<string>("username") ?? identity.Value<string>("display_name") ?? "unknown"}");
            sb.AppendLine();
            sb.AppendLine("## Evidence-backed knowledge");
            foreach (var fact in (JArray)data["facts"]!)
                sb.AppendLine($"- **{fact.Value<string>("category")}** ({fact.Value<double>("confidence"):P0}) {fact.Value<string>("fact")} — evidence: {string.Join(", ", ((JArray)fact["evidence_message_ids"]!).Select(x => x.ToString()))}");
            sb.AppendLine();
            sb.AppendLine("## Operator notes");
            foreach (var note in (JArray)data["operator_notes"]!) sb.AppendLine($"- {note.Value<string>("content")}");
            return sb.ToString();
        }

        private static JObject Event(string type, string id, long at, string title, string content, string? source, string personId, JArray? evidence, JObject? metadata) => new()
        {
            ["type"] = type, ["event_id"] = id, ["at"] = at, ["title"] = title, ["content"] = content,
            ["source"] = source, ["person_id"] = personId, ["evidence"] = evidence ?? new JArray(), ["metadata"] = metadata ?? new JObject(),
        };
        private static string TimeWhere(string column, long? from, long? to) => (from != null ? $" AND {column} >= $from" : "") + (to != null ? $" AND {column} < $to" : "");
        private static void TimeParams(SqliteCommand cmd, long? from, long? to, int limit)
        {
            if (from != null) cmd.Parameters.AddWithValue("$from", from.Value);
            if (to != null) cmd.Parameters.AddWithValue("$to", to.Value);
            cmd.Parameters.AddWithValue("$limit", limit);
        }
        private static void Read(SqliteConnection conn, string sql, Action<SqliteCommand> bind, Action<SqliteDataReader> read)
        {
            using var cmd = conn.CreateCommand(); cmd.CommandText = sql; bind(cmd); using var r = cmd.ExecuteReader(); while (r.Read()) read(r);
        }
        private static void Bind(SqliteCommand cmd, IEnumerable<(string Name, object Value)> parameters)
        { foreach (var parameter in parameters) cmd.Parameters.AddWithValue(parameter.Name, parameter.Value); }
        private static JArray ParseArray(string json) { try { return JArray.Parse(json); } catch { return new(); } }
        private static JObject ParseObject(string json) { try { return JObject.Parse(json); } catch { return new(); } }
        private static string Truncate(string? value, int max) => string.IsNullOrEmpty(value) || value.Length <= max ? value ?? "" : value[..max] + "…";
        private static string? Clean(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];
        private static double Ratio(long left, long right) => right == 0 ? (left == 0 ? 1 : 0) : Math.Round(left / (double)right, 3);
        private static string SafeFileName(string name) => string.Join("-", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Replace(' ', '-');
        private static string Csv(string value) => '"' + value.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + '"';
    }
}
