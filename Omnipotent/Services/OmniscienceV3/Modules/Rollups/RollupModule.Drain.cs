using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Ml.Classification;
using Omnipotent.Services.OmniscienceV3.Modules.Classification;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Modules.Rollups
{
    public sealed partial class RollupModule
    {
        private const int ChangeBatch = 1_000;
        private const int CellBatch = 250;
        private static readonly TimeSpan Budget = TimeSpan.FromMinutes(20);

        /// <summary>
        /// Turns corpus changes (and label changes from classification) into dirty cells, then rebuilds dirty cells, until
        /// both are drained or the time budget is used. When classification has an active label set, rollups trail its drain
        /// cursor so a cell is not built twice (once without labels, once with).
        /// </summary>
        private async Task<string?> DrainAsync(JobContext job)
        {
            var ct = job.Cancellation;
            var sw = Stopwatch.StartNew();
            long cursor = Cursor("changes");
            long maxSeq;
            using (var cr = Context.Stores.Corpus.OpenRead()) maxSeq = ChangeLog.MaxSeq(cr);
            long upper = maxSeq;
            if (ActiveLabelset() != null)
            {
                using var c = Context.Stores.Intel.OpenRead();
                long classify = c.Scalar<long?>(null, "SELECT seq FROM classify_cursors WHERE name='drain'") ?? 0;
                upper = Math.Min(maxSeq, classify);
            }
            int marked = 0, built = 0;
            while (cursor < upper && sw.Elapsed < Budget)
            {
                ct.ThrowIfCancellationRequested();
                List<ChangeRecord> changes;
                using (var cr = Context.Stores.Corpus.OpenRead())
                    changes = cr.Query(null, """
                        SELECT seq, at_ms, kind, entity_id, person_id, conversation_id, source_kind FROM change_log
                        WHERE seq > $s AND seq <= $u AND kind IN ('message.insert','message.version','message.delete','reaction.change','attachment.add',
                                                                   'person.merge','person.unmerge','person.erase','identity.upsert')
                        ORDER BY seq LIMIT $l
                        """, r => new ChangeRecord(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetStringOrNull(4), r.GetStringOrNull(5), r.GetString(6)),
                        ("$s", cursor), ("$u", upper), ("$l", ChangeBatch));
                long to = changes.Count < ChangeBatch ? upper : changes[^1].Seq;
                marked += await MarkFromChangesAsync(changes, to, ct).ConfigureAwait(false);
                await SetCursorAsync("changes", to, ct).ConfigureAwait(false);
                cursor = to;
                built += await BuildDirtyAsync(job, ct, Budget - sw.Elapsed).ConfigureAwait(false);
            }
            // Label changes after the fact (teacher signals, gold corrections, relabelled successors).
            marked += await MarkFromLabelOutboxAsync(ct).ConfigureAwait(false);
            built += await BuildDirtyAsync(job, ct, Budget - sw.Elapsed).ConfigureAwait(false);
            await job.ReportProgressAsync(new { cursor, upper, marked, built }).ConfigureAwait(false);
            if (built > 0) await KickProjectionsAsync(ct).ConfigureAwait(false);
            // The outbox is a queue with several consumers (projections, the Explore mirror): rows live a week; a consumer
            // that falls further behind rebuilds from the cells.
            await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "DELETE FROM rollup_changes WHERE at_ms < $cut",
                ("$cut", Context.Clock.NowMs - 7 * 86_400_000L)), ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { cursor, upper, marked, built });
        }

        /// <summary>
        /// Rebuilt cells should reach the pages at once, not at the projections' next 30-second poll: when the projections module
        /// is installed (it has a module_state row), its outbox job is queued (single-flight).
        /// </summary>
        private async Task KickProjectionsAsync(CancellationToken ct)
        {
            bool installed;
            using (var c = Context.Stores.Intel.OpenRead())
                installed = c.Scalar<long>(null, "SELECT COUNT(*) FROM module_state WHERE module_id='projections' AND enabled=1") > 0;
            if (!installed) return;
            await Context.Jobs.EnqueueAsync(new JobRequest
            {
                ModuleId = "projections", Kind = "outbox", SubjectKind = "stream", SubjectId = "rollups",
                DedupeKey = JobRequest.KeyFor("projections", "outbox", "stream", "rollups"), Priority = 125, Lane = Lane.Cpu,
            }, ct).ConfigureAwait(false);
        }

        // ── marking ──────────────────────────────────────────────────────────────

        private sealed record MessageFacts(string MessageId, string Author, string? Person, string ConversationId, string? GuildId, long SentAtMs, int TimeQuality);

        private static List<MessageFacts> LoadFacts(SqliteConnection corpus, IReadOnlyCollection<string> messageIds) =>
            corpus.QueryIn(null, """
                SELECT m.message_id, m.author_identity_id, COALESCE(pc.canonical_id, i.person_id), m.conversation_id, m.guild_id, m.sent_at_ms, m.time_quality
                FROM messages m LEFT JOIN identities i ON i.identity_id=m.author_identity_id LEFT JOIN person_canonical pc ON pc.person_id=i.person_id
                WHERE m.message_id IN ({keys})
                """, messageIds, r => new MessageFacts(r.GetString(0), r.GetString(1), r.GetStringOrNull(2), r.GetString(3), r.GetStringOrNull(4), r.GetInt64(5), r.GetInt32(6)));

        private async Task<int> MarkFromChangesAsync(IReadOnlyList<ChangeRecord> changes, long seq, CancellationToken ct)
        {
            if (changes.Count == 0) return 0;
            var cells = new HashSet<(string Kind, string Key, string Day)>();
            var tz = Context.Settings.KlivesZone;
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                var messageIds = changes.Where(c => c.Kind is ChangeKinds.MessageInsert or ChangeKinds.MessageVersion or ChangeKinds.MessageDelete or ChangeKinds.AttachmentAdd)
                    .Select(c => c.Kind == ChangeKinds.AttachmentAdd ? c.EntityId.Split('#')[0] : c.EntityId).Distinct().ToList();
                foreach (var m in LoadFacts(corpus, messageIds)) AddMessageCells(cells, m, tz);

                // A reaction change names the message: every reactor's day of that message is rebuilt (who changed is not recorded).
                // Reactions that arrive with their message (backfill, imports) are covered by the message's own change.
                var reacted = changes.Where(c => c.Kind == ChangeKinds.ReactionChange).Select(c => c.EntityId)
                    .Concat(changes.Where(c => c.Kind is ChangeKinds.MessageInsert or ChangeKinds.MessageVersion).Select(c => c.EntityId)).Distinct().ToList();
                foreach (var (identity, person, at) in corpus.QueryIn(null, """
                             SELECT r.identity_id, COALESCE(pc.canonical_id, i.person_id), COALESCE(r.removed_at_ms, r.added_at_ms) FROM reactions r
                             LEFT JOIN identities i ON i.identity_id=r.identity_id LEFT JOIN person_canonical pc ON pc.person_id=i.person_id
                             WHERE r.message_id IN ({keys})
                             """, reacted, r => (r.GetString(0), r.GetStringOrNull(1), r.GetInt64OrNull(2))))
                {
                    if (person == null || at == null) continue;
                    cells.Add((Cells.PersonDay, person, Day(LocalDayOf(person, at.Value))));
                }

                foreach (var c in changes.Where(c => c.Kind is ChangeKinds.PersonMerge or ChangeKinds.PersonUnmerge or ChangeKinds.PersonErase))
                {
                    cells.Add((Cells.PersonAll, c.EntityId, "*"));
                    if (c.PersonId != null && c.PersonId != c.EntityId) cells.Add((Cells.PersonAll, c.PersonId, "*"));
                }
                foreach (var c in changes.Where(c => c.Kind == ChangeKinds.IdentityUpsert))
                {
                    // An identity can move between persons: rebuild the person it belongs to now and the one it belonged to before.
                    string? now = corpus.Scalar<string>(null, "SELECT COALESCE(pc.canonical_id, i.person_id) FROM identities i LEFT JOIN person_canonical pc ON pc.person_id=i.person_id WHERE i.identity_id=$i",
                        ("$i", c.EntityId));
                    string? before;
                    using (var intel = Context.Stores.Intel.OpenRead())
                        before = intel.Scalar<string>(null, "SELECT value_json FROM rollup_state WHERE key=$k", ("$k", "identity:" + c.EntityId));
                    before = before == null ? null : JsonSerializer.Deserialize<string>(before);
                    if (now != null && now != before)
                    {
                        // A first sighting needs nothing rebuilt (its messages mark their own cells); a real move rebuilds both people.
                        if (before != null)
                        {
                            cells.Add((Cells.PersonAll, now, "*"));
                            cells.Add((Cells.PersonAll, before, "*"));
                        }
                        await SetStateAsync("identity:" + c.EntityId, now, ct).ConfigureAwait(false);
                    }
                }
            }
            return await AddDirtyAsync(cells, seq, ct).ConfigureAwait(false);
        }

        private void AddMessageCells(HashSet<(string, string, string)> cells, MessageFacts m, TimeZoneInfo klivesZone)
        {
            string klivesDay = Day(Zones.LocalDate(m.SentAtMs, klivesZone));
            cells.Add((Cells.Conversation, m.ConversationId, klivesDay));
            if (m.GuildId != null) cells.Add((Cells.Guild, m.GuildId, klivesDay));
            cells.Add((Cells.Global, "all", klivesDay));
            if (m.Person == null) return;
            cells.Add((Cells.PersonDay, m.Person, Day(LocalDayOf(m.Person, m.SentAtMs))));
            cells.Add((Cells.Slots, m.Person, Month(DateOnly.FromDateTime(Instant.ToUtcDateTime(m.SentAtMs)))));
        }

        private Task<int> AddDirtyAsync(IReadOnlyCollection<(string Kind, string Key, string Day)> cells, long seq, CancellationToken ct)
        {
            if (cells.Count == 0) return Task.FromResult(0);
            return Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                using var put = new PreparedStatement(c, tx, """
                    INSERT INTO rollup_dirty(kind, key, day, since_seq) VALUES($k,$key,$d,$s)
                    ON CONFLICT(kind, key, day) DO UPDATE SET since_seq=MAX(since_seq, excluded.since_seq)
                    """, "$k", "$key", "$d", "$s");
                foreach (var (kind, key, day) in cells) put.Run(kind, key, day, seq);
                return cells.Count;
            }, ct);
        }

        /// <summary>Label changes from classification's outbox mark the labelled messages' cells.</summary>
        private async Task<int> MarkFromLabelOutboxAsync(CancellationToken ct)
        {
            int? active = ActiveLabelset();
            if (active == null) return 0;
            long cursor = Cursor("labels");
            List<(long Seq, string Message)> rows;
            using (var c = Context.Stores.Intel.OpenRead())
            {
                bool hasOutbox = c.Scalar<long>(null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='classify_outbox'") > 0;
                if (!hasOutbox) return 0;
                rows = c.Query(null, "SELECT seq, message_id FROM classify_outbox WHERE seq > $s AND labelset=$l ORDER BY seq LIMIT 5000",
                    r => (r.GetInt64(0), r.GetString(1)), ("$s", cursor), ("$l", active.Value));
            }
            if (rows.Count == 0) return 0;
            var cells = new HashSet<(string, string, string)>();
            var tz = Context.Settings.KlivesZone;
            using (var corpus = Context.Stores.Corpus.OpenRead())
                foreach (var m in LoadFacts(corpus, rows.Select(r => r.Message).Distinct().ToList())) AddMessageCells(cells, m, tz);
            int n = await AddDirtyAsync(cells, rows[^1].Seq, ct).ConfigureAwait(false);
            await SetCursorAsync("labels", rows[^1].Seq, ct).ConfigureAwait(false);
            return n;
        }

        // ── zones ────────────────────────────────────────────────────────────────

        private readonly Dictionary<(string Person, string Month), TimeZoneInfo?> zoneCache = new();

        /// <summary>The person's zone for a UTC instant's month (inferred and confident), or null when unknown (R4: then UTC).</summary>
        internal TimeZoneInfo? ZoneOf(string person, long utcMs)
        {
            string month = Month(DateOnly.FromDateTime(Instant.ToUtcDateTime(utcMs)));
            lock (zoneCache)
                if (zoneCache.TryGetValue((person, month), out var z)) return z;
            TimeZoneInfo? zone = null;
            using (var c = Context.Stores.Intel.OpenRead())
            {
                string? id = c.Scalar<string>(null, "SELECT zone FROM rollup_zones WHERE person_id=$p AND month=$m AND zone IS NOT NULL", ("$p", person), ("$m", month));
                if (id != null && Zones.TryGet(id, out var found)) zone = found;
            }
            lock (zoneCache)
            {
                if (zoneCache.Count > 50_000) zoneCache.Clear();
                zoneCache[(person, month)] = zone;
            }
            return zone;
        }

        internal void ForgetZones(string person)
        {
            lock (zoneCache)
                foreach (var k in zoneCache.Keys.Where(k => k.Person == person).ToList()) zoneCache.Remove(k);
        }

        internal DateOnly LocalDayOf(string person, long utcMs) => Zones.LocalDate(utcMs, ZoneOf(person, utcMs) ?? TimeZoneInfo.Utc);

        // ── building ─────────────────────────────────────────────────────────────

        private async Task<int> BuildDirtyAsync(JobContext job, CancellationToken ct, TimeSpan budget)
        {
            var sw = Stopwatch.StartNew();
            int built = 0;
            while (sw.Elapsed < budget)
            {
                ct.ThrowIfCancellationRequested();
                List<(string Kind, string Key, string Day, long Seq)> batch;
                using (var c = Context.Stores.Intel.OpenRead())
                    batch = c.Query(null, "SELECT kind, key, day, since_seq FROM rollup_dirty ORDER BY CASE kind WHEN 'person-all' THEN 0 ELSE 1 END, kind, key, day LIMIT $l",
                        r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3)), ("$l", CellBatch));
                if (batch.Count == 0) break;
                foreach (var cell in batch)
                {
                    switch (cell.Kind)
                    {
                        case Cells.PersonAll: await ExpandPersonAsync(cell.Key, cell.Day, cell.Seq, ct).ConfigureAwait(false); break;
                        case Cells.PersonDay: await BuildPersonDayAsync(cell.Key, ParseDay(cell.Day), cell.Seq, ct).ConfigureAwait(false); break;
                        case Cells.Slots: await BuildSlotsAsync(cell.Key, cell.Day, cell.Seq, ct).ConfigureAwait(false); break;
                        default: await BuildScopeDayAsync(cell.Kind, cell.Key, ParseDay(cell.Day), cell.Seq, ct).ConfigureAwait(false); break;
                    }
                    await Context.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "DELETE FROM rollup_dirty WHERE kind=$k AND key=$key AND day=$d AND since_seq <= $s",
                        ("$k", cell.Kind), ("$key", cell.Key), ("$d", cell.Day), ("$s", cell.Seq)), ct).ConfigureAwait(false);
                    built++;
                }
                await job.ReportProgressAsync(new { built }).ConfigureAwait(false);
            }
            return built;
        }

        /// <summary>
        /// A person's every cell must be rebuilt (merge, unmerge, erase, identity move, zone change). Days come from the cells
        /// the person (or anyone whose pair cells name them) has now plus the days of their current messages; a month
        /// ("yyyy-MM") limits it to one month. Cells of a person who no longer exists are deleted.
        /// </summary>
        private async Task ExpandPersonAsync(string person, string scope, long seq, CancellationToken ct)
        {
            bool monthOnly = scope != "*";
            // A month scope is a UTC month (zones are per UTC month); its messages can fall on the neighbouring months' edge days locally.
            string? edgeFrom = null, edgeTo = null;
            if (monthOnly)
            {
                var first = DateOnly.ParseExact(scope + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
                edgeFrom = Day(first.AddDays(-1));
                edgeTo = Day(first.AddMonths(1));
            }
            bool InScope(string d) => !monthOnly || (string.CompareOrdinal(d, edgeFrom) >= 0 && string.CompareOrdinal(d, edgeTo) <= 0);
            var days = new HashSet<string>(StringComparer.Ordinal);
            var actorDays = new HashSet<(string, string)>();
            using (var c = Context.Stores.Intel.OpenRead())
            {
                foreach (var d in c.Query(null, "SELECT day FROM rollup_person_day WHERE person_id=$p", r => r.GetString(0), ("$p", person)))
                    if (InScope(d)) days.Add(d);
                foreach (var (actor, d) in c.Query(null, "SELECT person_id, day FROM rollup_pair_day WHERE partner_id=$p", r => (r.GetString(0), r.GetString(1)), ("$p", person)))
                    actorDays.Add((actor, d));
            }
            bool exists;
            List<string> identities;
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                exists = corpus.Scalar<long>(null, "SELECT COUNT(*) FROM persons WHERE person_id=$p", ("$p", person)) > 0
                         && (corpus.Scalar<string>(null, "SELECT canonical_id FROM person_canonical WHERE person_id=$p", ("$p", person)) ?? person) == person;
                identities = exists ? IdentitiesOf(corpus, person) : new List<string>();
                foreach (var identity in identities)
                {
                    foreach (long at in corpus.Query(null, "SELECT sent_at_ms FROM messages WHERE author_identity_id=$i", r => r.GetInt64(0), ("$i", identity)))
                    {
                        if (monthOnly && Month(DateOnly.FromDateTime(Instant.ToUtcDateTime(at))) != scope) continue;
                        days.Add(Day(LocalDayOf(person, at)));
                    }
                }
            }
            if (!exists)
            {
                // Merged away or erased: its own cells go; the partners' cells that named it are rebuilt below.
                await Context.Stores.Intel.WriteAsync((c, tx) =>
                {
                    c.Exec(tx, "DELETE FROM rollup_person_day WHERE person_id=$p", ("$p", person));
                    c.Exec(tx, "DELETE FROM rollup_pair_day WHERE person_id=$p", ("$p", person));
                    c.Exec(tx, "DELETE FROM rollup_slots WHERE person_id=$p", ("$p", person));
                    c.Exec(tx, "DELETE FROM rollup_zones WHERE person_id=$p", ("$p", person));
                    c.Exec(tx, "INSERT INTO rollup_changes(subject_kind, subject_id, day, at_ms) VALUES('person',$p,'*',$t)", ("$p", person), ("$t", Context.Clock.NowMs));
                }, ct).ConfigureAwait(false);
                days.Clear();
            }
            var cells = new HashSet<(string, string, string)>();
            foreach (var d in days) cells.Add((Cells.PersonDay, person, d));
            foreach (var (actor, d) in actorDays) cells.Add((Cells.PersonDay, actor, d));
            if (exists)
            {
                using var corpus = Context.Stores.Corpus.OpenRead();
                foreach (var month in identities.SelectMany(i => corpus.Query(null,
                             "SELECT DISTINCT strftime('%Y-%m', sent_at_ms / 1000, 'unixepoch') FROM messages WHERE author_identity_id=$i", r => r.GetString(0), ("$i", i))).Distinct())
                    if (!monthOnly || month == scope) cells.Add((Cells.Slots, person, month));
            }
            ForgetZones(person);
            await AddDirtyAsync(cells, seq, ct).ConfigureAwait(false);
        }

        internal static List<string> IdentitiesOf(SqliteConnection corpus, string person) =>
            corpus.Query(null, """
                SELECT i.identity_id FROM identities i LEFT JOIN person_canonical pc ON pc.person_id=i.person_id
                WHERE COALESCE(pc.canonical_id, i.person_id) = $p
                """, r => r.GetString(0), ("$p", person));

        /// <summary>A message as the rollups read it (with everything needed for person, pair and scope cells).</summary>
        private sealed record RollupMessage(string MessageId, string Author, string ConversationId, string? GuildId, string? ConversationKind, string Platform,
            long SentAtMs, long OrderKey, int TimeQuality, int Version, string? Content, bool Bot, bool System, bool Webhook, bool Forwarded, string? ReplyTo,
            long? DeletedAtMs, long? EditedAtMs, int Attachments);

        private const string RollupColumns = """
            m.message_id, m.author_identity_id, m.conversation_id, m.guild_id, cv.kind, m.platform, m.sent_at_ms, m.order_key, m.time_quality, m.current_version,
            m.content, (m.is_bot=1 OR COALESCE(i.kind,'')='bot'), m.is_system, m.is_webhook, m.forwarded_from_json IS NOT NULL, m.reply_to_message_id,
            m.deleted_at_ms, m.edited_at_ms, (SELECT COUNT(*) FROM attachments a WHERE a.message_id=m.message_id)
            """;

        private const string RollupFrom = """
            FROM messages m LEFT JOIN identities i ON i.identity_id=m.author_identity_id LEFT JOIN conversations cv ON cv.conversation_id=m.conversation_id
            """;

        private static RollupMessage MapRollup(SqliteDataReader r) => new(
            r.GetString(0), r.GetString(1), r.GetString(2), r.GetStringOrNull(3), r.GetStringOrNull(4), r.GetString(5), r.GetInt64(6), r.GetInt64(7), r.GetInt32(8),
            r.GetInt32(9), r.GetStringOrNull(10), r.GetInt64(11) == 1, r.GetInt64(12) == 1, r.GetInt64(13) == 1, r.GetInt64(14) == 1, r.GetStringOrNull(15),
            r.GetInt64OrNull(16), r.GetInt64OrNull(17), r.GetInt32(18));

        private static readonly IReadOnlyList<string> CountedTasks = ClassificationTasks.All.Where(t => t.Id != ClassificationTasks.Distress.Id).Select(t => t.Id).ToList();

        private (Dictionary<string, MessageLabels> Labels, int? Labelset) LabelsFor(IReadOnlyCollection<string> ids)
        {
            int? ls = ActiveLabelset();
            return (ls == null ? new Dictionary<string, MessageLabels>() : ClassificationReader.ForMessages(Context.Stores, ids, ls), ls);
        }

        /// <summary>Adds a message's answered labels to per-task counts (only authored, live messages: A5, analytics edge cases).</summary>
        private static void CountLabels(Dictionary<string, Dictionary<string, int>> counts, MessageLabels? labels)
        {
            if (labels == null) return;
            foreach (var task in CountedTasks)
            {
                string? answer = labels.Answer(task);
                if (answer == null) continue;
                if (!counts.TryGetValue(task, out var byLabel)) counts[task] = byLabel = new Dictionary<string, int>(StringComparer.Ordinal);
                byLabel[answer] = byLabel.GetValueOrDefault(answer) + 1;
            }
        }

        /// <summary>Log2 latency bucket: 0 = under 2 s … 15 = 9 h and more (gaps over 24 h are not replies).</summary>
        internal static int LatencyBucket(long ms) => Math.Clamp((int)Math.Floor(Math.Log2(Math.Max(1, ms / 1000.0) + 1)), 0, 15);

        private static string Json(object v) => JsonSerializer.Serialize(v);

        /// <summary>Rebuilds one person-day cell and all of that person's outgoing pair cells for the day.</summary>
        private async Task BuildPersonDayAsync(string person, DateOnly day, long seq, CancellationToken ct)
        {
            // Each message belongs to exactly one person-day: its local date in the zone of its own (UTC) month. A zone change
            // between months can make consecutive local days overlap or leave a gap, so the cell reads a widened UTC window
            // (offsets never exceed ±14 h) and keeps only the messages whose own local day is this day.
            var zone = ZoneOf(person, Zones.DayStartUtcMs(day, TimeZoneInfo.Utc) + 12 * 3_600_000L);
            var tz = zone ?? TimeZoneInfo.Utc;
            long start = Zones.DayStartUtcMs(day, TimeZoneInfo.Utc) - 14 * 3_600_000L, end = Zones.DayStartUtcMs(day.AddDays(1), TimeZoneInfo.Utc) + 14 * 3_600_000L;
            bool Mine(long at) => LocalDayOf(person, at) == day;
            List<RollupMessage> msgs;
            var replyAuthors = new Dictionary<string, (string Person, long SentAt)>(StringComparer.Ordinal);
            var mentionPersons = new List<(string Message, string Person)>();
            var dmCounterparts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var implicitLatency = new Dictionary<string, (string Person, long Ms)>(StringComparer.Ordinal);
            var reactionsGiven = new List<(string Partner, long At)>();
            var self = new HashSet<string>(StringComparer.Ordinal);
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                var identities = IdentitiesOf(corpus, person);
                foreach (var i in identities) self.Add(i);
                msgs = identities.SelectMany(i => corpus.Query(null, $"SELECT {RollupColumns} {RollupFrom} WHERE m.author_identity_id=$i AND m.sent_at_ms >= $a AND m.sent_at_ms < $b",
                    MapRollup, ("$i", i), ("$a", start), ("$b", end))).Where(m => !m.Bot && !m.System && Mine(m.SentAtMs)).OrderBy(m => m.SentAtMs).ThenBy(m => m.OrderKey).ToList();
                var targets = msgs.Where(m => m.ReplyTo != null).Select(m => m.ReplyTo!).Distinct().ToList();
                foreach (var (id, p, at) in corpus.QueryIn(null, """
                             SELECT m.message_id, COALESCE(pc.canonical_id, i.person_id), m.sent_at_ms FROM messages m
                             LEFT JOIN identities i ON i.identity_id=m.author_identity_id LEFT JOIN person_canonical pc ON pc.person_id=i.person_id WHERE m.message_id IN ({keys})
                             """, targets, r => (r.GetString(0), r.GetStringOrNull(1), r.GetInt64(2))))
                    if (p != null) replyAuthors[id] = (p, at);
                mentionPersons = corpus.QueryIn(null, """
                    SELECT mm.message_id, COALESCE(pc.canonical_id, i.person_id) FROM message_mentions mm
                    JOIN identities i ON i.identity_id=mm.target_id LEFT JOIN person_canonical pc ON pc.person_id=i.person_id
                    WHERE mm.target_kind='user' AND mm.message_id IN ({keys})
                    """, msgs.Select(m => m.MessageId).ToList(), r => (r.GetString(0), r.GetString(1)));
                foreach (var conv in msgs.Where(m => m.ConversationKind == "dm").Select(m => m.ConversationId).Distinct())
                {
                    dmCounterparts[conv] = corpus.Query(null, """
                        SELECT DISTINCT COALESCE(pc.canonical_id, i.person_id) FROM conversation_members cm JOIN identities i ON i.identity_id=cm.identity_id
                        LEFT JOIN person_canonical pc ON pc.person_id=i.person_id WHERE cm.conversation_id=$c
                        """, r => r.GetString(0), ("$c", conv)).Where(p => p != person).ToList();
                }
                // Implicit DM reply latency: the first message of a burst answering the other side's last message.
                foreach (var m in msgs.Where(m => m.ConversationKind == "dm" && m.ReplyTo == null))
                {
                    var prev = corpus.Query(null, """
                        SELECT m2.author_identity_id, m2.sent_at_ms, COALESCE(pc.canonical_id, i.person_id) FROM messages m2
                        LEFT JOIN identities i ON i.identity_id=m2.author_identity_id LEFT JOIN person_canonical pc ON pc.person_id=i.person_id
                        WHERE m2.conversation_id=$c AND (m2.sent_at_ms, m2.order_key) < ($t, $o) ORDER BY m2.sent_at_ms DESC, m2.order_key DESC LIMIT 1
                        """, r => (r.GetString(0), r.GetInt64(1), r.GetStringOrNull(2)), ("$c", m.ConversationId), ("$t", m.SentAtMs), ("$o", m.OrderKey)).FirstOrDefault();
                    if (prev != default && !self.Contains(prev.Item1) && prev.Item3 != null && m.SentAtMs - prev.Item2 is >= 0 and <= 86_400_000)
                        implicitLatency[m.MessageId] = (prev.Item3, m.SentAtMs - prev.Item2);
                }
                foreach (var identity in identities)
                {
                    reactionsGiven.AddRange(corpus.Query(null, """
                        SELECT COALESCE(pc.canonical_id, i.person_id), r.added_at_ms FROM reactions r JOIN messages m ON m.message_id=r.message_id
                        JOIN identities i ON i.identity_id=m.author_identity_id LEFT JOIN person_canonical pc ON pc.person_id=i.person_id
                        WHERE r.identity_id=$i AND r.added_at_ms >= $a AND r.added_at_ms < $b AND r.removed_at_ms IS NULL
                        """, r => (r.GetString(0), r.GetInt64(1)), ("$i", identity), ("$a", start), ("$b", end)).Where(x => Mine(x.Item2)));
                }
            }
            var (labels, labelset) = LabelsFor(msgs.Select(m => m.MessageId).ToList());

            // Person cell.
            var hours = new int[24];
            var labelCounts = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            var conversations = new Dictionary<string, int>(StringComparer.Ordinal);
            var guilds = new Dictionary<string, int>(StringComparer.Ordinal);
            int authored = 0, deleted = 0, edited = 0, replies = 0, forwarded = 0, nonAuthored = 0, withAttachments = 0, withLinks = 0, dm = 0, group = 0, server = 0,
                dayOnly = 0, mentionsMade = 0;
            long chars = 0, words = 0;
            foreach (var m in msgs)
            {
                labels.TryGetValue(m.MessageId, out var l);
                var shape = ContentGate.Measure(m.Content ?? "");
                bool nonAuth = m.Forwarded || l?.NonAuthored == true || shape.CodeShare >= 0.5 || shape.UrlOnly || shape.QuotedOnly;
                bool isDeleted = m.DeletedAtMs != null;
                if (isDeleted) deleted++;
                if (m.EditedAtMs != null || m.Version > 1) edited++;
                if (m.ReplyTo != null) replies++;
                if (m.Forwarded) forwarded++;
                if (nonAuth) nonAuthored++;
                if (m.Attachments > 0) withAttachments++;
                if (shape.Urls > 0) withLinks++;
                switch (m.ConversationKind) { case "dm": dm++; break; case "group_dm": group++; break; default: server++; break; }
                // R4: day-precision times have no hour; legacy times of unknown zone have no trustworthy hour. Zone-inferred
                // imports (WhatsApp) keep their recorded wall-clock hour.
                if (m.TimeQuality is (int)TimeQuality.Day or (int)TimeQuality.LegacyUnknownZone) dayOnly++;
                else hours[Zones.ToLocal(m.SentAtMs, ZoneOf(person, m.SentAtMs) ?? TimeZoneInfo.Utc).Hour]++;
                conversations[m.ConversationId] = conversations.GetValueOrDefault(m.ConversationId) + 1;
                if (m.GuildId != null) guilds[m.GuildId] = guilds.GetValueOrDefault(m.GuildId) + 1;
                mentionsMade += mentionPersons.Count(x => x.Message == m.MessageId && x.Person != person);
                if (!isDeleted && !nonAuth)
                {
                    authored++;
                    chars += (m.Content ?? "").Length;
                    words += TextStats.Words(m.Content);
                    CountLabels(labelCounts, l);
                }
            }

            // Pair cells: replies, mentions, DM messages, reactions and latencies towards each partner.
            var pairs = new Dictionary<string, PairAcc>(StringComparer.Ordinal);
            PairAcc Pair(string partner) => pairs.TryGetValue(partner, out var p) ? p : pairs[partner] = new PairAcc();
            foreach (var m in msgs)
            {
                labels.TryGetValue(m.MessageId, out var l);
                bool countable = m.DeletedAtMs == null && l?.NonAuthored != true && !m.Forwarded;
                var targetsOf = new HashSet<string>(StringComparer.Ordinal);
                if (m.ReplyTo != null && replyAuthors.TryGetValue(m.ReplyTo, out var ra) && ra.Person != person)
                {
                    var p = Pair(ra.Person);
                    p.Replies++;
                    long lat = m.SentAtMs - ra.SentAt;
                    if (lat is >= 0 and <= 86_400_000) p.Explicit[LatencyBucket(lat)]++;
                    targetsOf.Add(ra.Person);
                }
                foreach (var (_, target) in mentionPersons.Where(x => x.Message == m.MessageId && x.Person != person))
                {
                    Pair(target).Mentions++;
                    targetsOf.Add(target);
                }
                if (m.ConversationKind == "dm" && dmCounterparts.TryGetValue(m.ConversationId, out var counterparts))
                {
                    foreach (var cp in counterparts)
                    {
                        Pair(cp).DmMsgs++;
                        targetsOf.Add(cp);
                    }
                }
                if (implicitLatency.TryGetValue(m.MessageId, out var il) && il.Person != person) Pair(il.Person).Implicit[LatencyBucket(il.Ms)]++;
                if (countable)
                    foreach (var t in targetsOf) CountLabels(Pair(t).Labels, l);
            }
            foreach (var (partner, _) in reactionsGiven.Where(r => r.Partner != person)) Pair(partner).Reactions++;

            long now = Context.Clock.NowMs;
            string dayText = Day(day);
            await Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "DELETE FROM rollup_pair_day WHERE person_id=$p AND day=$d", ("$p", person), ("$d", dayText));
                if (msgs.Count == 0 && reactionsGiven.Count == 0)
                {
                    c.Exec(tx, "DELETE FROM rollup_person_day WHERE person_id=$p AND day=$d", ("$p", person), ("$d", dayText));
                }
                else
                {
                    c.Exec(tx, """
                        INSERT INTO rollup_person_day(person_id, day, zone, zone_known, msgs, authored, deleted, edited, replies, forwarded, non_authored, with_attachments,
                            with_links, chars, words, dm_msgs, group_msgs, server_msgs, day_only, reactions_given, mentions_made, first_ms, last_ms, hours, labels,
                            conversations, guilds, labelset, source_seq, computed_at_ms)
                        VALUES($p,$d,$z,$zk,$msgs,$au,$del,$ed,$rep,$fw,$na,$att,$lnk,$ch,$w,$dm,$gr,$sv,$do,$rg,$mm,$f,$l,$h,$lab,$conv,$g,$ls,$s,$t)
                        ON CONFLICT(person_id, day) DO UPDATE SET zone=excluded.zone, zone_known=excluded.zone_known, msgs=excluded.msgs, authored=excluded.authored,
                            deleted=excluded.deleted, edited=excluded.edited, replies=excluded.replies, forwarded=excluded.forwarded, non_authored=excluded.non_authored,
                            with_attachments=excluded.with_attachments, with_links=excluded.with_links, chars=excluded.chars, words=excluded.words, dm_msgs=excluded.dm_msgs,
                            group_msgs=excluded.group_msgs, server_msgs=excluded.server_msgs, day_only=excluded.day_only, reactions_given=excluded.reactions_given,
                            mentions_made=excluded.mentions_made, first_ms=excluded.first_ms, last_ms=excluded.last_ms, hours=excluded.hours, labels=excluded.labels,
                            conversations=excluded.conversations, guilds=excluded.guilds, labelset=excluded.labelset, source_seq=excluded.source_seq, computed_at_ms=excluded.computed_at_ms
                        """, ("$p", person), ("$d", dayText), ("$z", tz.Id == TimeZoneInfo.Utc.Id ? Zones.Utc : tz.Id), ("$zk", zone == null ? 0 : 1), ("$msgs", msgs.Count),
                        ("$au", authored), ("$del", deleted), ("$ed", edited), ("$rep", replies), ("$fw", forwarded), ("$na", nonAuthored), ("$att", withAttachments),
                        ("$lnk", withLinks), ("$ch", chars), ("$w", words), ("$dm", dm), ("$gr", group), ("$sv", server), ("$do", dayOnly), ("$rg", reactionsGiven.Count),
                        ("$mm", mentionsMade), ("$f", msgs.Count == 0 ? null : msgs[0].SentAtMs), ("$l", msgs.Count == 0 ? null : msgs[^1].SentAtMs), ("$h", Json(hours)),
                        ("$lab", Json(labelCounts)), ("$conv", Json(conversations)), ("$g", Json(guilds)), ("$ls", labelset), ("$s", seq), ("$t", now));
                    using var put = new PreparedStatement(c, tx, """
                        INSERT INTO rollup_pair_day(person_id, partner_id, day, replies, mentions, dm_msgs, reactions, latency_explicit, latency_implicit, labels, labelset, source_seq)
                        VALUES($p,$q,$d,$r,$m,$dm,$re,$le,$li,$lab,$ls,$s)
                        """, "$p", "$q", "$d", "$r", "$m", "$dm", "$re", "$le", "$li", "$lab", "$ls", "$s");
                    foreach (var (partner, a) in pairs)
                        put.Run(person, partner, dayText, a.Replies, a.Mentions, a.DmMsgs, a.Reactions, Json(a.Explicit), Json(a.Implicit), Json(a.Labels), labelset, seq);
                }
                c.Exec(tx, "INSERT INTO rollup_changes(subject_kind, subject_id, day, at_ms) VALUES('person',$p,$d,$t)", ("$p", person), ("$d", dayText), ("$t", now));
            }, ct).ConfigureAwait(false);
        }

        private sealed class PairAcc
        {
            public int Replies, Mentions, DmMsgs, Reactions;
            public int[] Explicit = new int[16];
            public int[] Implicit = new int[16];
            public Dictionary<string, Dictionary<string, int>> Labels = new(StringComparer.Ordinal);
        }

        /// <summary>Rebuilds a conversation, guild or global day (Klives's zone, plan T4: his zone defines the UI's days).</summary>
        private async Task BuildScopeDayAsync(string scope, string key, DateOnly day, long seq, CancellationToken ct)
        {
            var (start, end) = Zones.LocalDayRange(day, Context.Settings.KlivesZone);
            string where = scope switch
            {
                Cells.Conversation => "m.conversation_id=$k AND m.sent_at_ms >= $a AND m.sent_at_ms < $b",
                Cells.Guild => "m.guild_id=$k AND m.sent_at_ms >= $a AND m.sent_at_ms < $b",
                Cells.Global => "m.sent_at_ms >= $a AND m.sent_at_ms < $b AND $k = 'all'",
                _ => throw new JobFailureException(ErrorClass.Permanent, $"Unknown rollup scope '{scope}'."),
            };
            List<(RollupMessage M, string? Person)> msgs;
            using (var corpus = Context.Stores.Corpus.OpenRead())
                msgs = corpus.Query(null, $"""
                    SELECT {RollupColumns}, COALESCE(pc.canonical_id, i.person_id) {RollupFrom} LEFT JOIN person_canonical pc ON pc.person_id=i.person_id WHERE {where}
                    """, r => (MapRollup(r), r.GetStringOrNull(19)), ("$k", key), ("$a", start), ("$b", end)).Where(x => !x.Item1.Bot && !x.Item1.System).ToList();
            var (labels, labelset) = LabelsFor(msgs.Select(m => m.M.MessageId).ToList());
            var persons = new Dictionary<string, int>(StringComparer.Ordinal);
            var platforms = new Dictionary<string, int>(StringComparer.Ordinal);
            var labelCounts = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            int authored = 0, deleted = 0;
            foreach (var (m, p) in msgs)
            {
                if (p != null) persons[p] = persons.GetValueOrDefault(p) + 1;
                platforms[m.Platform] = platforms.GetValueOrDefault(m.Platform) + 1;
                labels.TryGetValue(m.MessageId, out var l);
                if (m.DeletedAtMs != null) { deleted++; continue; }
                if (m.Forwarded || l?.NonAuthored == true) continue;
                authored++;
                CountLabels(labelCounts, l);
            }
            long now = Context.Clock.NowMs;
            string dayText = Day(day);
            await Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                if (msgs.Count == 0)
                    c.Exec(tx, "DELETE FROM rollup_scope_day WHERE scope=$sc AND key=$k AND day=$d", ("$sc", scope), ("$k", key), ("$d", dayText));
                else
                    c.Exec(tx, """
                        INSERT INTO rollup_scope_day(scope, key, day, msgs, authored, deleted, people, persons, labels, platforms, first_ms, last_ms, labelset, source_seq, computed_at_ms)
                        VALUES($sc,$k,$d,$n,$au,$del,$pp,$ps,$lab,$pl,$f,$l,$ls,$s,$t)
                        ON CONFLICT(scope, key, day) DO UPDATE SET msgs=excluded.msgs, authored=excluded.authored, deleted=excluded.deleted, people=excluded.people,
                            persons=excluded.persons, labels=excluded.labels, platforms=excluded.platforms, first_ms=excluded.first_ms, last_ms=excluded.last_ms,
                            labelset=excluded.labelset, source_seq=excluded.source_seq, computed_at_ms=excluded.computed_at_ms
                        """, ("$sc", scope), ("$k", key), ("$d", dayText), ("$n", msgs.Count), ("$au", authored), ("$del", deleted), ("$pp", persons.Count),
                        ("$ps", Json(persons)), ("$lab", Json(labelCounts)), ("$pl", Json(platforms)), ("$f", msgs.Min(m => m.M.SentAtMs)), ("$l", msgs.Max(m => m.M.SentAtMs)),
                        ("$ls", labelset), ("$s", seq), ("$t", now));
                c.Exec(tx, "INSERT INTO rollup_changes(subject_kind, subject_id, day, at_ms) VALUES($sc,$k,$d,$t)", ("$sc", scope), ("$k", key), ("$d", dayText), ("$t", now));
            }, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Rebuilds a person's UTC activity profile for one month: 96 quarter-hour slots and 168 weekday-hours. Only instants
        /// whose clock is trustworthy count: day-precision times have no time of day, and zone-inferred times (imports
        /// without a zone) would make zone inference circular.
        /// </summary>
        private async Task BuildSlotsAsync(string person, string month, long seq, CancellationToken ct)
        {
            var first = DateOnly.ParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
            long start = Zones.DayStartUtcMs(first, TimeZoneInfo.Utc), end = Zones.DayStartUtcMs(first.AddMonths(1), TimeZoneInfo.Utc);
            var slots = new int[96];
            var weekHours = new int[168];
            var days = new HashSet<DateOnly>();
            int n = 0;
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                foreach (var identity in IdentitiesOf(corpus, person))
                {
                    foreach (var (at, quality) in corpus.Query(null, """
                                 SELECT m.sent_at_ms, m.time_quality FROM messages m LEFT JOIN identities i ON i.identity_id=m.author_identity_id
                                 WHERE m.author_identity_id=$i AND m.sent_at_ms >= $a AND m.sent_at_ms < $b AND m.is_bot=0 AND m.is_system=0 AND COALESCE(i.kind,'') <> 'bot'
                                 """, r => (r.GetInt64(0), r.GetInt32(1)), ("$i", identity), ("$a", start), ("$b", end)))
                    {
                        if (quality is (int)TimeQuality.Day or (int)TimeQuality.InferredZone or (int)TimeQuality.LegacyUnknownZone) continue;
                        var utc = Instant.ToUtcDateTime(at);
                        slots[(utc.Hour * 60 + utc.Minute) / 15]++;
                        weekHours[(int)utc.DayOfWeek * 24 + utc.Hour]++;
                        days.Add(DateOnly.FromDateTime(utc));
                        n++;
                    }
                }
            }
            await Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                if (n == 0) c.Exec(tx, "DELETE FROM rollup_slots WHERE person_id=$p AND month=$m", ("$p", person), ("$m", month));
                else c.Exec(tx, """
                    INSERT INTO rollup_slots(person_id, month, msgs, days, slots, week_hours, source_seq) VALUES($p,$m,$n,$d,$s,$w,$q)
                    ON CONFLICT(person_id, month) DO UPDATE SET msgs=excluded.msgs, days=excluded.days, slots=excluded.slots, week_hours=excluded.week_hours, source_seq=excluded.source_seq
                    """, ("$p", person), ("$m", month), ("$n", n), ("$d", days.Count), ("$s", Json(slots)), ("$w", Json(weekHours)), ("$q", seq));
            }, ct).ConfigureAwait(false);
        }

        /// <summary>A new label set became active: every cell's label columns are rebuilt in the background.</summary>
        private async Task<string?> RelabelAsync(JobContext job)
        {
            var ct = job.Cancellation;
            int? active = ActiveLabelset();
            if (active == null) return null;
            int marked = await Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                int a = c.Exec(tx, "INSERT OR IGNORE INTO rollup_dirty(kind, key, day, since_seq) SELECT 'person', person_id, day, 0 FROM rollup_person_day WHERE COALESCE(labelset,-1) <> $l", ("$l", active.Value));
                int b = c.Exec(tx, "INSERT OR IGNORE INTO rollup_dirty(kind, key, day, since_seq) SELECT scope, key, day, 0 FROM rollup_scope_day WHERE COALESCE(labelset,-1) <> $l", ("$l", active.Value));
                return a + b;
            }, ct).ConfigureAwait(false);
            await SetStateAsync<int?>("labelset", active, ct).ConfigureAwait(false);
            await Context.Jobs.EnqueueAsync(Job("drain", "stream", "changes", priority: 115), ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { labelset = active, marked });
        }
    }

    /// <summary>Exact text measurements (plan A1: counts stay arithmetic). Words are Unicode letter/digit runs (F038/F045).</summary>
    public static class TextStats
    {
        public static int Words(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int words = 0;
            bool inWord = false;
            var e = StringInfo.GetTextElementEnumerator(text);
            while (e.MoveNext())
            {
                string el = e.GetTextElement();
                bool letter = char.IsLetterOrDigit(el, 0) || (inWord && (el == "'" || el == "’" || el == "-"));
                if (letter && !inWord) words++;
                inWord = letter;
            }
            return words;
        }
    }
}
