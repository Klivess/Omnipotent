using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Ml.Classification;
using Omnipotent.Services.OmniscienceV3.Modules.Classification;
using Omnipotent.Services.OmniscienceV3.Modules.Rollups;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Modules.Explore
{
    public sealed partial class ExploreModule
    {
        private const int Batch = 5_000;
        private static readonly TimeSpan Budget = TimeSpan.FromMinutes(15);
        private static readonly string[] LabelColumns =
        {
            "language", "sentiment", "emotion", "toxicity", "humour", "dialogue_act", "modality", "politeness", "formality", "self_disclosure", "info_density",
            "conflict", "support", "about_klives",
        };

        /// <summary>Mirrors corpus changes, label changes, zone changes and the rollup outbox (relations) into DuckDB.</summary>
        private async Task<string?> MirrorAsync(JobContext job)
        {
            var ct = job.Cancellation;
            var sw = Stopwatch.StartNew();
            int messages = 0, events = 0, relations = 0;
            await writeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                long cursor = ReadState("changes") ?? 0, maxSeq;
                using (var cr = Context.Stores.Corpus.OpenRead()) maxSeq = ChangeLog.MaxSeq(cr);
                bool mapDirty = ReadState("person_map_seq") == null, erased = false;
                while (cursor < maxSeq && sw.Elapsed < Budget)
                {
                    ct.ThrowIfCancellationRequested();
                    List<(long Seq, string Kind, string Entity)> changes;
                    using (var cr = Context.Stores.Corpus.OpenRead())
                        changes = cr.Query(null, "SELECT seq, kind, entity_id FROM change_log WHERE seq > $s AND seq <= $m ORDER BY seq LIMIT $l",
                            r => (r.GetInt64(0), r.GetString(1), r.GetString(2)), ("$s", cursor), ("$m", maxSeq), ("$l", Batch));
                    if (changes.Count == 0) { cursor = maxSeq; break; }
                    var messageIds = changes.Where(c => c.Kind is ChangeKinds.MessageInsert or ChangeKinds.MessageVersion or ChangeKinds.MessageDelete or ChangeKinds.AttachmentAdd)
                        .Select(c => c.Kind == ChangeKinds.AttachmentAdd ? c.Entity.Split('#')[0] : c.Entity).Distinct().ToList();
                    messages += MirrorMessages(messageIds);
                    var reacted = changes.Where(c => c.Kind is ChangeKinds.ReactionChange or ChangeKinds.MessageInsert).Select(c => c.Entity).Distinct().ToList();
                    events += MirrorReactions(reacted);
                    if (changes.Any(c => c.Kind is ChangeKinds.PersonMerge or ChangeKinds.PersonUnmerge or ChangeKinds.PersonErase or ChangeKinds.PersonCreate or ChangeKinds.IdentityUpsert))
                        mapDirty = true;
                    if (changes.Any(c => c.Kind == ChangeKinds.PersonErase))
                    {
                        EraseMissingPeople();
                        erased = true;
                    }
                    cursor = changes[^1].Seq;
                    WriteState("changes", cursor);
                    await job.ReportProgressAsync(new { cursor, maxSeq, messages }).ConfigureAwait(false);
                }
                WriteState("changes", cursor);
                if (mapDirty) { RefreshPersonMap(); WriteState("person_map_seq", cursor); }
                events += MirrorEventTables();
                messages += MirrorLabelOutbox();
                messages += MirrorZoneChanges();
                relations += MirrorRelations();
                if (erased)
                {
                    // The datasets copied from other modules are re-copied whole now, so what their modules purged leaves the
                    // mirror with this pass instead of at the next reconciliation.
                    foreach (var b in bound.Values.Where(b => b.Source != null)) ClearState(SourceState(b.Dataset.Id) + ".whole_at");
                    MirrorSources(ct);
                }
                bool policyChanged = RefreshRestricted();
                WriteState("as_of", Context.Clock.NowMs);
                if (messages + events + relations > 0 || mapDirty || policyChanged || erased) Bump();
            }
            finally
            {
                writeGate.Release();
            }
            return JsonSerializer.Serialize(new { messages, events, relations, version = Version });
        }

        // ── messages ─────────────────────────────────────────────────────────────

        private sealed record MirrorRow(string Id, long Occurred, long Ingested, long? Edited, long? Deleted, int TimeQuality, string? Author, string? Partner,
            string Conversation, string? Guild, string? Kind, string Platform, string Source, int Length, string? LinkDomain, string? Media, bool IsReply,
            bool IsEdited, bool IsDeleted, bool IsForwarded, bool MentionsKlives, string?[] Labels, bool NonAuthored, int? Labelset, string Zone, int ZoneOffsetMs);

        /// <summary>(Re)mirrors the given messages; ids that no longer exist in the corpus (erased) are removed.</summary>
        private int MirrorMessages(IReadOnlyCollection<string> ids)
        {
            if (ids.Count == 0) return 0;
            var rows = new List<MirrorRow>(ids.Count);
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                var self = ClassificationModule.SelfIdentities(corpus);
                var raw = corpus.QueryIn(null, """
                    SELECT m.message_id, m.sent_at_ms, m.ingested_at_ms, m.edited_at_ms, m.deleted_at_ms, m.time_quality, i.person_id, m.conversation_id, m.guild_id,
                           cv.kind, m.platform, m.best_source_id, length(COALESCE(m.content,'')), m.content, m.reply_to_message_id, m.current_version,
                           m.forwarded_from_json IS NOT NULL, (m.is_bot=1 OR COALESCE(i.kind,'')='bot' OR m.is_system=1)
                    FROM messages m LEFT JOIN identities i ON i.identity_id=m.author_identity_id LEFT JOIN conversations cv ON cv.conversation_id=m.conversation_id
                    WHERE m.message_id IN ({keys})
                    """, ids, r => (Id: r.GetString(0), At: r.GetInt64(1), Ing: r.GetInt64(2), Ed: r.GetInt64OrNull(3), Del: r.GetInt64OrNull(4), Q: r.GetInt32(5),
                        Person: r.GetStringOrNull(6), Conv: r.GetString(7), Guild: r.GetStringOrNull(8), Kind: r.GetStringOrNull(9), Platform: r.GetString(10),
                        Source: r.GetString(11), Len: r.GetInt32(12), Content: r.GetStringOrNull(13), Reply: r.GetStringOrNull(14), Version: r.GetInt32(15),
                        Fwd: r.GetInt64(16) == 1, Bot: r.GetInt64(17) == 1)).Where(x => !x.Bot).ToList();
                var replyPeople = corpus.QueryIn(null, "SELECT m.message_id, i.person_id FROM messages m JOIN identities i ON i.identity_id=m.author_identity_id WHERE m.message_id IN ({keys})",
                    raw.Where(x => x.Reply != null).Select(x => x.Reply!).Distinct().ToList(), r => (r.GetString(0), r.GetString(1))).ToDictionary(x => x.Item1, x => x.Item2);
                var dmPartner = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var conv in raw.Where(x => x.Kind == "dm").Select(x => x.Conv).Distinct())
                    dmPartner[conv] = null;
                foreach (var conv in dmPartner.Keys.ToList())
                {
                    var members = corpus.Query(null, "SELECT DISTINCT i.person_id FROM conversation_members cm JOIN identities i ON i.identity_id=cm.identity_id WHERE cm.conversation_id=$c",
                        r => r.GetString(0), ("$c", conv));
                    dmPartner[conv] = members.Count == 2 ? string.Join("|", members) : null;
                }
                var mentionsSelf = self.Count == 0 ? new HashSet<string>() : corpus.QueryIn(null,
                        "SELECT message_id, target_id FROM message_mentions WHERE target_kind='user' AND message_id IN ({keys})", raw.Select(x => x.Id).ToList(), r => (r.GetString(0), r.GetString(1)))
                    .Where(x => self.Contains(x.Item2)).Select(x => x.Item1).ToHashSet(StringComparer.Ordinal);
                var media = corpus.QueryIn(null, "SELECT message_id, media_kind FROM attachments WHERE message_id IN ({keys}) ORDER BY idx", raw.Select(x => x.Id).ToList(),
                    r => (r.GetString(0), r.GetString(1))).GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => g.First().Item2);
                var labels = ClassificationReader.ForMessages(Context.Stores, raw.Select(x => x.Id).ToList());
                var zones = new ZoneLookup(Context);
                foreach (var x in raw)
                {
                    labels.TryGetValue(x.Id, out var l);
                    string? partner = x.Reply != null && replyPeople.TryGetValue(x.Reply, out var rp) && rp != x.Person ? rp
                        : x.Kind == "dm" && dmPartner.GetValueOrDefault(x.Conv) is { } pair ? pair.Split('|').FirstOrDefault(p => p != x.Person) : null;
                    var labelValues = LabelColumns.Select(c => l?.Answer(c)).ToArray();
                    bool aboutKlives = labelValues[^1] is { } k && k != "none";
                    var (zone, offsetMs) = x.Person == null ? (Zones.Utc, 0) : zones.At(x.Person, x.At);
                    rows.Add(new MirrorRow(x.Id, x.At, x.Ing, x.Ed, x.Del, x.Q, x.Person, partner, x.Conv, x.Guild, x.Kind, x.Platform, x.Source, x.Len,
                        LinkDomain(x.Content), media.GetValueOrDefault(x.Id), x.Reply != null, x.Ed != null || x.Version > 1, x.Del != null, x.Fwd,
                        mentionsSelf.Contains(x.Id) || aboutKlives, labelValues, l?.NonAuthored ?? false, l?.Labelset, zone, offsetMs));
                }
            }
            using var tx = Db.BeginTransaction();
            DeleteIn("messages", "message_id", ids);
            using (var appender = Db.CreateAppender("messages"))
            {
                foreach (var r in rows)
                {
                    var row = appender.CreateRow()
                        .AppendValue(r.Id).AppendValue(r.Occurred).AppendValue(r.Ingested).AppendValue(r.Edited).AppendValue(r.Deleted).AppendValue((sbyte)r.TimeQuality)
                        .AppendValue(r.Author).AppendValue(r.Partner).AppendValue(r.Conversation).AppendValue(r.Guild).AppendValue(r.Kind).AppendValue(r.Platform)
                        .AppendValue(r.Source).AppendValue(r.Length).AppendValue(r.LinkDomain).AppendValue(r.Media).AppendValue(r.IsReply).AppendValue(r.IsEdited)
                        .AppendValue(r.IsDeleted).AppendValue(r.IsForwarded).AppendValue(r.MentionsKlives);
                    foreach (var v in r.Labels) row = row.AppendValue(v);
                    row.AppendValue(r.NonAuthored).AppendValue(r.Labelset).AppendValue(r.Zone).AppendValue(r.ZoneOffsetMs).EndRow();
                }
            }
            tx.Commit();
            return rows.Count;
        }

        /// <summary>
        /// A person's zone for the month of an instant, and that zone's UTC offset at the instant (so the subject lens is plain
        /// arithmetic at query time). One lookup per person and month for a whole batch; UTC when their zone is unknown (R4:
        /// the UI falls back to the UTC lens rather than guessing).
        /// </summary>
        private sealed class ZoneLookup
        {
            private readonly ModuleContext context;
            private readonly Dictionary<string, string> canonical = new(StringComparer.Ordinal);
            private readonly Dictionary<(string Person, string Month), TimeZoneInfo?> zones = new();
            private readonly bool hasZones;

            public ZoneLookup(ModuleContext context)
            {
                this.context = context;
                using var c = context.Stores.Intel.OpenRead();
                hasZones = c.Scalar<long>(null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='rollup_zones'") > 0;
            }

            public (string Zone, int OffsetMs) At(string person, long at)
            {
                if (!hasZones) return (Zones.Utc, 0);
                if (!canonical.TryGetValue(person, out var id)) canonical[person] = id = context.Policy.Canonical(person);
                string month = RollupModule.Month(DateOnly.FromDateTime(Instant.ToUtcDateTime(at)));
                if (!zones.TryGetValue((id, month), out var tz))
                {
                    using var c = context.Stores.Intel.OpenRead();
                    string? name = c.Scalar<string>(null, "SELECT zone FROM rollup_zones WHERE person_id=$p AND month=$m AND zone IS NOT NULL", ("$p", id), ("$m", month));
                    zones[(id, month)] = tz = name != null && Zones.TryGet(name, out var found) ? found : null;
                }
                return tz == null ? (Zones.Utc, 0) : (tz.Id, (int)Zones.OffsetAt(at, tz).TotalMilliseconds);
            }
        }

        /// <summary>The registrable domain-ish host of the first link (structural URL parsing; tracking parameters are irrelevant to a host).</summary>
        internal static string? LinkDomain(string? content)
        {
            if (string.IsNullOrEmpty(content)) return null;
            int i = content.IndexOf("http", StringComparison.OrdinalIgnoreCase);
            while (i >= 0)
            {
                int end = i;
                while (end < content.Length && !char.IsWhiteSpace(content[end]) && content[end] is not ('>' or ')' or ']' or '"')) end++;
                if (Uri.TryCreate(content[i..end], UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    string host = uri.Host.ToLowerInvariant();
                    return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
                }
                i = content.IndexOf("http", end, StringComparison.OrdinalIgnoreCase);
            }
            return null;
        }

        private void DeleteIn(string table, string column, IReadOnlyCollection<string> ids)
        {
            foreach (var chunk in ids.Distinct(StringComparer.Ordinal).Chunk(500))
            {
                using var cmd = Db.CreateCommand();
                var names = chunk.Select((_, i) => "$p" + i.ToString(CultureInfo.InvariantCulture)).ToArray();
                cmd.CommandText = $"DELETE FROM {table} WHERE {column} IN ({string.Join(",", names)})";
                for (int i = 0; i < chunk.Length; i++) cmd.Parameters.Add(new DuckDBParameter("p" + i.ToString(CultureInfo.InvariantCulture), chunk[i]));
                cmd.ExecuteNonQuery();
            }
        }

        private int MirrorLabelOutbox()
        {
            long cursor = ReadState("labels") ?? 0;
            int done = 0;
            while (true)
            {
                List<(long Seq, string Id)> rows;
                using (var c = Context.Stores.Intel.OpenRead())
                {
                    if (c.Scalar<long>(null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='classify_outbox'") == 0) return done;
                    rows = c.Query(null, "SELECT seq, message_id FROM classify_outbox WHERE seq > $s ORDER BY seq LIMIT $l", r => (r.GetInt64(0), r.GetString(1)), ("$s", cursor), ("$l", Batch));
                }
                if (rows.Count == 0) return done;
                done += MirrorMessages(rows.Select(r => r.Id).Distinct().ToList());
                cursor = rows[^1].Seq;
                WriteState("labels", cursor);
            }
        }

        /// <summary>People whose zone readings changed get their messages' subject-lens zone rewritten (re-mirrored).</summary>
        private int MirrorZoneChanges()
        {
            long since = ReadState("zones") ?? 0;
            List<(string Person, long At)> changed;
            using (var c = Context.Stores.Intel.OpenRead())
            {
                if (c.Scalar<long>(null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='rollup_zones'") == 0) return 0;
                changed = c.Query(null, "SELECT person_id, MAX(computed_at_ms) FROM rollup_zones WHERE computed_at_ms > $s GROUP BY person_id", r => (r.GetString(0), r.GetInt64(1)), ("$s", since));
            }
            int done = 0;
            foreach (var (person, _) in changed)
            {
                List<string> ids;
                using (var corpus = Context.Stores.Corpus.OpenRead())
                    ids = RollupModule.IdentitiesOf(corpus, person).SelectMany(i => corpus.Query(null, "SELECT message_id FROM messages WHERE author_identity_id=$i", r => r.GetString(0), ("$i", i))).ToList();
                foreach (var chunk in ids.Chunk(Batch)) done += MirrorMessages(chunk);
            }
            if (changed.Count > 0) WriteState("zones", changed.Max(c => c.At));
            return done;
        }

        /// <summary>
        /// An erased person's rows are gone from the corpus: their mirror rows go too (the purge reaches the mirror, N20).
        /// "Gone" is read from the mirror's own tables, whose person columns only ever hold person ids. The datasets copied
        /// from other modules are not touched here (their person columns may hold other entities): the caller re-copies them
        /// whole, which removes whatever their module purged.
        /// </summary>
        private void EraseMissingPeople()
        {
            var columns = new[] { ("messages", "author"), ("events", "person"), ("relations", "person"), ("relations", "partner"), ("concepts", "author"), ("assertions", "subject") };
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (table, column) in columns.Append(("messages", "partner")))
            {
                using var cmd = Db.CreateCommand();
                cmd.CommandText = $"SELECT DISTINCT {column} FROM {table} WHERE {column} IS NOT NULL";
                using var r = cmd.ExecuteReader();
                while (r.Read()) ids.Add(r.GetString(0));
            }
            HashSet<string> alive;
            using (var corpus = Context.Stores.Corpus.OpenRead())
                alive = corpus.QueryIn(null, "SELECT person_id FROM persons WHERE person_id IN ({keys})", ids, r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);
            var gone = ids.Where(i => !alive.Contains(i)).ToList();
            if (gone.Count == 0) return;
            foreach (var (table, column) in columns) DeleteIn(table, column, gone);
            // A message someone else wrote to the erased person stays; it just no longer names them.
            foreach (var chunk in gone.Chunk(500))
            {
                using var cmd = Db.CreateCommand();
                var names = chunk.Select((_, i) => "$p" + i.ToString(CultureInfo.InvariantCulture)).ToArray();
                cmd.CommandText = $"UPDATE messages SET partner = NULL WHERE partner IN ({string.Join(",", names)})";
                for (int i = 0; i < chunk.Length; i++) cmd.Parameters.Add(new DuckDBParameter("p" + i.ToString(CultureInfo.InvariantCulture), chunk[i]));
                cmd.ExecuteNonQuery();
            }
        }

        private void RefreshPersonMap()
        {
            List<(string Person, string Canonical)> map;
            using (var corpus = Context.Stores.Corpus.OpenRead())
                map = corpus.Query(null, "SELECT p.person_id, COALESCE(pc.canonical_id, p.person_id) FROM persons p LEFT JOIN person_canonical pc ON pc.person_id=p.person_id",
                    r => (r.GetString(0), r.GetString(1)));
            using var tx = Db.BeginTransaction();
            Exec("DELETE FROM person_map");
            using (var appender = Db.CreateAppender("person_map"))
                foreach (var (p, c) in map) appender.CreateRow().AppendValue(p).AppendValue(c).EndRow();
            tx.Commit();
        }

        /// <summary>Minors and likely minors (MinorGuard) as of now: Klives's marks plus every band the projections computed.</summary>
        private bool RefreshRestricted()
        {
            var restricted = new HashSet<string>(StringComparer.Ordinal);
            using (var op = Context.Stores.Operator.OpenRead())
                foreach (var p in op.Query(null, "SELECT person_id FROM person_settings WHERE minor_marked=1", r => r.GetString(0))) restricted.Add(Context.Policy.Canonical(p));
            using (var c = Context.Stores.Intel.OpenRead())
                if (c.Scalar<long>(null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='projection_people'") > 0)
                    foreach (var p in c.Query(null, "SELECT person_id FROM projection_people WHERE band IN ('Minor','LikelyMinor')", r => r.GetString(0))) restricted.Add(p);
            var current = new HashSet<string>(StringComparer.Ordinal);
            using (var cmd = Db.CreateCommand())
            {
                cmd.CommandText = "SELECT person_id FROM restricted_persons";
                using var r = cmd.ExecuteReader();
                while (r.Read()) current.Add(r.GetString(0));
            }
            if (current.SetEquals(restricted)) return false;
            using var tx = Db.BeginTransaction();
            Exec("DELETE FROM restricted_persons");
            using (var appender = Db.CreateAppender("restricted_persons"))
                foreach (var p in restricted) appender.CreateRow().AppendValue(p).EndRow();
            tx.Commit();
            return true;
        }

        // ── events ───────────────────────────────────────────────────────────────

        private int MirrorReactions(IReadOnlyCollection<string> messageIds)
        {
            if (messageIds.Count == 0) return 0;
            List<(string Message, string Emoji, string Person, long At, string? Guild, string Channel, string Platform)> rows;
            using (var corpus = Context.Stores.Corpus.OpenRead())
                rows = corpus.QueryIn(null, """
                    SELECT r.message_id, r.emoji_key, i.person_id, r.added_at_ms, m.guild_id, m.conversation_id, m.platform FROM reactions r
                    JOIN identities i ON i.identity_id=r.identity_id JOIN messages m ON m.message_id=r.message_id
                    WHERE r.message_id IN ({keys}) AND r.removed_at_ms IS NULL AND r.added_at_ms IS NOT NULL
                    """, messageIds, r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetStringOrNull(4), r.GetString(5), r.GetString(6)));
            using var tx = Db.BeginTransaction();
            foreach (var chunk in messageIds.Chunk(500))
            {
                using var cmd = Db.CreateCommand();
                var names = chunk.Select((_, i) => "$p" + i.ToString(CultureInfo.InvariantCulture)).ToArray();
                cmd.CommandText = $"DELETE FROM events WHERE kind='reaction' AND split_part(event_id, '|', 1) IN ({string.Join(",", names)})";
                for (int i = 0; i < chunk.Length; i++) cmd.Parameters.Add(new DuckDBParameter("p" + i.ToString(CultureInfo.InvariantCulture), chunk[i]));
                cmd.ExecuteNonQuery();
            }
            using (var appender = Db.CreateAppender("events"))
                foreach (var r in rows)
                    appender.CreateRow().AppendValue(r.Message + "|" + r.Emoji + "|" + r.Person).AppendValue("reaction").AppendValue(r.At).AppendValue(r.Person)
                        .AppendValue(r.Guild).AppendValue(r.Channel).AppendValue(r.Platform).AppendValue((string?)null).AppendValue(r.Emoji)
                        .AppendValue((string?)null).AppendValue((string?)null).AppendValue((long?)null).EndRow();
            tx.Commit();
            return rows.Count;
        }

        /// <summary>Presence, voice, member and typing events, by each table's own cursor (open voice sessions are refreshed).</summary>
        private int MirrorEventTables()
        {
            int n = 0;
            n += MirrorEventTable("presence", "SELECT p.event_id, p.at_ms, i.person_id, p.guild_id, NULL, i.platform, p.source_id, NULL, p.activities_json, p.status, NULL FROM presence_events p JOIN identities i ON i.identity_id=p.identity_id WHERE p.event_id > $c ORDER BY p.event_id LIMIT 5000");
            n += MirrorEventTable("member", "SELECT e.event_id, e.at_ms, i.person_id, e.guild_id, NULL, i.platform, e.source_id, NULL, NULL, e.kind, NULL FROM member_events e JOIN identities i ON i.identity_id=e.identity_id WHERE e.event_id > $c ORDER BY e.event_id LIMIT 5000");
            n += MirrorEventTable("typing", "SELECT t.rowid, t.at_ms, i.person_id, c.guild_id, t.conversation_id, i.platform, t.source_id, NULL, NULL, NULL, NULL FROM typing_events t JOIN identities i ON i.identity_id=t.identity_id LEFT JOIN conversations c ON c.conversation_id=t.conversation_id WHERE t.rowid > $c ORDER BY t.rowid LIMIT 5000");
            n += MirrorEventTable("voice", "SELECT v.rowid, v.joined_at_ms, i.person_id, v.guild_id, v.channel_id, i.platform, v.source_id, NULL, NULL, v.left_reason, v.left_at_ms - v.joined_at_ms FROM voice_sessions v JOIN identities i ON i.identity_id=v.identity_id WHERE v.rowid > $c OR v.left_at_ms IS NULL ORDER BY v.rowid LIMIT 5000");
            return n;
        }

        private int MirrorEventTable(string kind, string sql)
        {
            long cursor = ReadState("events." + kind) ?? 0;
            var rows = new List<(long Id, long At, string Person, string? Guild, string? Channel, string Platform, string Source, string? Activity, string? Status, long? Duration)>();
            using (var corpus = Context.Stores.Corpus.OpenRead())
                rows = corpus.Query(null, sql, r => (r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetStringOrNull(3), r.GetStringOrNull(4), r.GetString(5), r.GetString(6),
                    r.GetStringOrNull(8) is { } act ? ActivityName(act) : null, r.GetStringOrNull(9), r.IsDBNull(10) ? (long?)null : r.GetInt64(10)), ("$c", cursor));
            if (rows.Count == 0) return 0;
            var ids = rows.Select(r => kind + ":" + r.Id.ToString(CultureInfo.InvariantCulture)).ToList();
            using var tx = Db.BeginTransaction();
            DeleteIn("events", "event_id", ids);
            using (var appender = Db.CreateAppender("events"))
                foreach (var r in rows)
                    appender.CreateRow().AppendValue(kind + ":" + r.Id.ToString(CultureInfo.InvariantCulture)).AppendValue(kind).AppendValue(r.At).AppendValue(r.Person)
                        .AppendValue(r.Guild).AppendValue(r.Channel).AppendValue(r.Platform).AppendValue(r.Source).AppendValue((string?)null).AppendValue(r.Activity)
                        .AppendValue(r.Status).AppendValue(r.Duration).EndRow();
            tx.Commit();
            WriteState("events." + kind, rows.Max(r => r.Id));
            return rows.Count;
        }

        /// <summary>The first activity's name (game or track) from a presence payload; structural JSON reading.</summary>
        private static string? ActivityName(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0
                    && doc.RootElement[0].TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    return name.GetString();
                return null;
            }
            catch (JsonException)
            {
                // An unreadable payload carries no activity name; the event itself is still mirrored.
                return null;
            }
        }

        // ── relations (rollup pair-days) ─────────────────────────────────────────

        private int MirrorRelations()
        {
            long cursor = ReadState("rollup_changes") ?? 0;
            int done = 0;
            while (true)
            {
                List<(long Seq, string Person, string Day)> changes;
                using (var c = Context.Stores.Intel.OpenRead())
                {
                    if (c.Scalar<long>(null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='rollup_changes'") == 0) return done;
                    long min = c.Scalar<long?>(null, "SELECT MIN(seq) FROM rollup_changes") ?? 0;
                    if (cursor > 0 && min > cursor + 1) cursor = 0; // fell behind the outbox's retention: rebuild every pair below
                    changes = c.Query(null, "SELECT seq, subject_id, day FROM rollup_changes WHERE seq > $s AND subject_kind='person' ORDER BY seq LIMIT $l",
                        r => (r.GetInt64(0), r.GetString(1), r.GetString(2)), ("$s", cursor), ("$l", Batch));
                }
                if (changes.Count == 0) return done;
                var keys = changes.Select(c => (c.Person, c.Day)).Distinct().ToList();
                var rows = new List<(string Person, string Partner, string Day, int Replies, int Mentions, int Dm, int Reactions)>();
                using (var c = Context.Stores.Intel.OpenRead())
                    foreach (var (p, d) in keys)
                        rows.AddRange(d == "*"
                            ? c.Query(null, "SELECT person_id, partner_id, day, replies, mentions, dm_msgs, reactions FROM rollup_pair_day WHERE person_id=$p",
                                r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6)), ("$p", p))
                            : c.Query(null, "SELECT person_id, partner_id, day, replies, mentions, dm_msgs, reactions FROM rollup_pair_day WHERE person_id=$p AND day=$d",
                                r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6)), ("$p", p), ("$d", d)));
                using (var tx = Db.BeginTransaction())
                {
                    foreach (var (p, d) in keys)
                    {
                        using var cmd = Db.CreateCommand();
                        cmd.CommandText = d == "*" ? "DELETE FROM relations WHERE person=$p" : "DELETE FROM relations WHERE person=$p AND day_text=$d";
                        cmd.Parameters.Add(new DuckDBParameter("p", p));
                        if (d != "*") cmd.Parameters.Add(new DuckDBParameter("d", d));
                        cmd.ExecuteNonQuery();
                    }
                    using (var appender = Db.CreateAppender("relations"))
                        foreach (var r in rows)
                            appender.CreateRow().AppendValue(r.Person + "|" + r.Partner + "|" + r.Day).AppendValue(Zones.DayStartUtcMs(RollupModule.ParseDay(r.Day), TimeZoneInfo.Utc))
                                .AppendValue(r.Day).AppendValue(r.Person).AppendValue(r.Partner).AppendValue(r.Replies).AppendValue(r.Mentions).AppendValue(r.Dm)
                                .AppendValue(r.Reactions).AppendValue(r.Replies + r.Mentions + r.Dm + r.Reactions).EndRow();
                    tx.Commit();
                }
                done += rows.Count;
                cursor = changes[^1].Seq;
                WriteState("rollup_changes", cursor);
            }
        }

        // ── engine datasets (every few minutes) ──────────────────────────────────

        /// <summary>Concept hits, facts, tactic runs and jobs are bounded tables, rebuilt whole; LLM calls are appended by id;
        /// every dataset another module offers is copied from its table (<see cref="MirrorSources"/>).</summary>
        private async Task<string?> MirrorEngineAsync(JobContext job)
        {
            var ct = job.Cancellation;
            await writeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                int concepts = RebuildConcepts(), assertions = RebuildAssertions(), runs = RebuildTacticRuns(), jobs = RebuildJobs(), calls = AppendLlmCalls();
                var modules = MirrorSources(ct);
                WriteState("as_of", Context.Clock.NowMs);
                Bump();
                return JsonSerializer.Serialize(new { concepts, assertions, runs, jobs, calls, modules });
            }
            finally
            {
                writeGate.Release();
            }
        }

        private int Rebuild<T>(string table, IReadOnlyList<T> rows, Action<DuckDBAppender, T> append)
        {
            using var tx = Db.BeginTransaction();
            Exec($"DELETE FROM {table}");
            using (var appender = Db.CreateAppender(table))
                foreach (var r in rows) append(appender, r);
            tx.Commit();
            return rows.Count;
        }

        private bool IntelHas(SqliteConnection c, string table) => c.Scalar<long>(null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$t", ("$t", table)) > 0;

        private int RebuildConcepts()
        {
            List<(string Item, string Task, double P)> hits;
            Dictionary<string, string> names;
            using (var c = Context.Stores.Intel.OpenRead())
            {
                if (!IntelHas(c, "classify_concepts")) return 0;
                hits = c.Query(null, "SELECT item_id, task, probability FROM labels WHERE item_kind='message' AND task LIKE 'concept:%'", r => (r.GetString(0), r.GetString(1), r.GetDouble(2)));
                names = c.Query(null, "SELECT concept_id, name FROM classify_concepts WHERE state IN ('active','applying')", r => (r.GetString(0), r.GetString(1))).ToDictionary(x => x.Item1, x => x.Item2);
            }
            Dictionary<string, (long At, string? Person, string Conv, string? Guild)> meta;
            using (var corpus = Context.Stores.Corpus.OpenRead())
                meta = corpus.QueryIn(null, "SELECT m.message_id, m.sent_at_ms, i.person_id, m.conversation_id, m.guild_id FROM messages m LEFT JOIN identities i ON i.identity_id=m.author_identity_id WHERE m.message_id IN ({keys})",
                    hits.Select(h => h.Item).Distinct().ToList(), r => (r.GetString(0), (r.GetInt64(1), r.GetStringOrNull(2), r.GetString(3), r.GetStringOrNull(4)))).ToDictionary(x => x.Item1, x => x.Item2);
            var rows = hits.Where(h => meta.ContainsKey(h.Item) && names.ContainsKey(h.Task["concept:".Length..])).ToList();
            return Rebuild("concepts", rows, (a, h) =>
            {
                var m = meta[h.Item];
                string id = h.Task["concept:".Length..];
                a.CreateRow().AppendValue(h.Item + "|" + id).AppendValue(h.Item).AppendValue(m.At).AppendValue(m.Person).AppendValue(m.Conv).AppendValue(m.Guild)
                    .AppendValue(names[id]).AppendValue(id).AppendValue(h.P).EndRow();
            });
        }

        private int RebuildAssertions()
        {
            List<(string Id, long Rec, long? From, long? To, long? Asserted, string Subject, string Attr, string? Value, string Status, string Module, string? Model, string? Pv, double? P, string? Modality, long? Sup, long? Ret)> rows;
            using (var c = Context.Stores.Intel.OpenRead())
                rows = c.Query(null, """
                    SELECT assertion_id, recorded_at_ms, COALESCE(valid_from_earliest_ms, valid_from_latest_ms), COALESCE(valid_to_latest_ms, valid_to_earliest_ms), asserted_at_ms,
                           subject_id, attribute, value_text, status, module_id, model, prompt_version, probability, modality, superseded_at_ms, retracted_at_ms
                    FROM assertions WHERE subject_kind='person'
                    """, r => (r.GetString(0), r.GetInt64(1), r.GetInt64OrNull(2), r.GetInt64OrNull(3), r.GetInt64OrNull(4), r.GetString(5), r.GetString(6), r.GetStringOrNull(7),
                        r.GetString(8), r.GetString(9), r.GetStringOrNull(10), r.GetStringOrNull(11), r.GetDoubleOrNull(12), r.GetStringOrNull(13), r.GetInt64OrNull(14), r.GetInt64OrNull(15)));
            return Rebuild("assertions", rows, (a, x) => a.CreateRow().AppendValue(x.Id).AppendValue(x.Rec).AppendValue(x.From).AppendValue(x.To).AppendValue(x.Asserted)
                .AppendValue(x.Subject).AppendValue(x.Attr).AppendValue(x.Value).AppendValue(x.Status).AppendValue(x.Module).AppendValue(x.Model).AppendValue(x.Pv)
                .AppendValue(x.P).AppendValue(x.Modality).AppendValue(x.Sup).AppendValue(x.Ret).EndRow());
        }

        private int RebuildTacticRuns()
        {
            List<(string Id, long Created, string Tactic, string State, string Actor, long? Duration, string? Error)> rows;
            using (var c = Context.Stores.Intel.OpenRead())
            {
                if (!IntelHas(c, "tactic_runs")) return 0;
                rows = c.Query(null, "SELECT run_id, created_at_ms, tactic_key, state, actor, finished_at_ms - started_at_ms, error FROM tactic_runs",
                    r => (r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64OrNull(5), r.GetStringOrNull(6)));
            }
            return Rebuild("tactic_runs", rows, (a, x) => a.CreateRow().AppendValue(x.Id).AppendValue(x.Created).AppendValue(x.Tactic).AppendValue(x.State).AppendValue(x.Actor)
                .AppendValue(x.Duration).AppendValue(x.Error).EndRow());
        }

        private int RebuildJobs()
        {
            List<(long Id, long Created, string Module, string Kind, string State, int Attempts, long? Duration, string? Error)> rows;
            using (var c = Context.Stores.Intel.OpenRead())
                rows = c.Query(null, "SELECT job_id, created_at_ms, module_id, kind, state, attempts, finished_at_ms - started_at_ms, error_class FROM jobs",
                    r => (r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetInt64OrNull(6), r.GetStringOrNull(7)));
            return Rebuild("jobs", rows, (a, x) => a.CreateRow().AppendValue(x.Id.ToString(CultureInfo.InvariantCulture)).AppendValue(x.Created).AppendValue(x.Module)
                .AppendValue(x.Kind).AppendValue(x.State).AppendValue(x.Attempts).AppendValue(x.Duration).AppendValue(x.Error).EndRow());
        }

        private int AppendLlmCalls()
        {
            long cursor = ReadState("llm_calls") ?? 0;
            List<(long Id, long At, string Module, string Purpose, string? Model, string? Wc, string Outcome, string? Pv, long? Pt, long? Ct, long? Cpt, long? Lat)> rows;
            using (var c = Context.Stores.Intel.OpenRead())
                rows = c.Query(null, """
                    SELECT call_id, at_ms, module_id, purpose, model, work_class, outcome, prompt_version, prompt_tokens, cached_tokens, completion_tokens, latency_ms
                    FROM llm_calls WHERE call_id > $c ORDER BY call_id LIMIT 50000
                    """, r => (r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetStringOrNull(4), r.GetStringOrNull(5), r.GetString(6), r.GetStringOrNull(7),
                        r.GetInt64OrNull(8), r.GetInt64OrNull(9), r.GetInt64OrNull(10), r.GetInt64OrNull(11)), ("$c", cursor));
            if (rows.Count == 0) return 0;
            using (var appender = Db.CreateAppender("llm_calls"))
                foreach (var x in rows)
                    appender.CreateRow().AppendValue(x.Id.ToString(CultureInfo.InvariantCulture)).AppendValue(x.At).AppendValue(x.Module).AppendValue(x.Purpose)
                        .AppendValue(x.Model).AppendValue(x.Wc).AppendValue(x.Outcome).AppendValue(x.Pv).AppendValue(x.Pt).AppendValue(x.Ct).AppendValue(x.Cpt).AppendValue(x.Lat).EndRow();
            WriteState("llm_calls", rows[^1].Id);
            return rows.Count;
        }
    }
}
