using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Knowledge.Store;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Knowledge.Evidence
{
    /// <summary>What topping a person's queue up did: how many anchors wait now, how many were added, how many messages
    /// were looked at for it, and whether the person's history has been looked through to its end.</summary>
    public sealed record EvidenceTopUp(int Queued, int Added, int Looked, bool Complete);

    /// <summary>What one pass over the corpus change log put into the queues.</summary>
    public sealed record EvidenceRouted(long Cursor, long MaxSeq, int Changes, int Added, IReadOnlyCollection<string> Persons);

    /// <summary>
    /// What is waiting to be read about each tracked person, kept as a queue (intel.db <c>kn_evidence_queue</c>).
    /// <para>An extraction job used to work its reading list out from nothing every time: every message the person ever
    /// wrote (one row read each, scattered over a 15 GB file), every reply to each of them, every message they replied to,
    /// every mention, less what had been read; then it read the first hundred or so. For the three busiest people that
    /// list was never finished: twelve runs of three to fifteen hours each, not one model call, and the one worker of
    /// the model lane held by them for a day while fourteen other people's jobs and everything else in the lane waited
    /// (21 Nov 2026).</para>
    /// <para>Now the list is pushed, not pulled. A person's history is looked through once, a page at a time, oldest
    /// first, only as far as the next job needs (<see cref="TopUpAsync"/>): what they wrote, the replies to it and what it
    /// replied to, then the messages that @-mention them, then mentions by name. What arrives or changes afterwards is
    /// routed from the corpus change log to the people it is evidence about (<see cref="RouteAsync"/>). A job reads the
    /// head of the queue (<see cref="Head"/>); an anchor leaves the queue in the transaction that records it as read.
    /// The cost of a job no longer depends on how much the person has ever written.</para>
    /// </summary>
    public sealed class EvidenceQueue
    {
        private static readonly ConditionalWeakTable<KnowledgeKit, EvidenceQueue> queues = new();

        /// <summary>The queue of one engine (one per <see cref="KnowledgeKit"/>, so its passes over the change log never overlap).</summary>
        public static EvidenceQueue For(KnowledgeKit kit) => queues.GetValue(kit, k => new EvidenceQueue(k));

        /// <summary>Messages of one identity looked at per page of history.</summary>
        internal static int PageSize { get; set; } = 300;

        /// <summary>Change-log rows looked at per step of a routing pass.</summary>
        internal static int RouteWindow { get; set; } = 2_000;

        /// <summary>Names (and accepted aliases) per person that are looked for; the same limit the full scan had.</summary>
        private const int MaxNames = 12;

        private const string RouteCursorName = "changes";

        private readonly KnowledgeKit kit;
        private readonly EvidenceAssembler assembler;
        private readonly SemaphoreSlim routing = new(1, 1);

        private EvidenceQueue(KnowledgeKit kit)
        {
            this.kit = kit;
            assembler = new EvidenceAssembler(kit);
        }

        private OmniscienceStores Stores => kit.Context.Stores;

        private long Now => kit.Context.Clock.NowMs;

        // ── state ────────────────────────────────────────────────────────────────

        /// <summary>How far one identity's history has been paged: the sent time and row of the last message looked at.</summary>
        private sealed class Cursor
        {
            public long T { get; set; } = long.MinValue;
            public long R { get; set; }
            public bool Done { get; set; }
        }

        private sealed class QueueState
        {
            /// <summary>Per identity: what they wrote, paged oldest first.</summary>
            public Dictionary<string, Cursor> Authored { get; set; } = new(StringComparer.Ordinal);
            /// <summary>Per identity: the messages that @-mention it, paged in the order they were stored (<see cref="Cursor.R"/> only).</summary>
            public Dictionary<string, Cursor> Mentions { get; set; } = new(StringComparer.Ordinal);
            /// <summary>Names already looked for (lower case).</summary>
            public List<string> Names { get; set; } = new();
        }

        private (QueueState State, bool Complete, bool OtherPrompt) Load(string person, string promptVersion)
        {
            using var c = Stores.Intel.OpenRead();
            var row = c.Query(null, "SELECT prompt_version, complete, state_json FROM kn_evidence_state WHERE person_id=$p",
                r => (Prompt: r.GetString(0), Complete: r.GetInt32(1) == 1, Json: r.GetString(2)), ("$p", person)).FirstOrDefault();
            if (row.Prompt == null) return (new QueueState(), false, false);
            if (!string.Equals(row.Prompt, promptVersion, StringComparison.Ordinal)) return (new QueueState(), false, true);
            QueueState? state;
            try { state = JsonSerializer.Deserialize<QueueState>(row.Json, KernelJson.Options); }
            catch (JsonException) { state = null; } // unreadable: the history is looked through again; nothing read is read twice (coverage)
            return state == null ? (new QueueState(), false, false) : (state, row.Complete, false);
        }

        /// <summary>Forgets what is queued for a person and how far their history was looked through (their identities
        /// changed in a merge or an unmerge, their analysis was switched back on, or the prompt version changed). What was
        /// read stays read: coverage is not touched.</summary>
        public Task ResetAsync(string person, CancellationToken ct) => Stores.Intel.WriteAsync((c, tx) =>
        {
            c.Exec(tx, "DELETE FROM kn_evidence_queue WHERE person_id=$p", ("$p", person));
            c.Exec(tx, "DELETE FROM kn_evidence_convs WHERE person_id=$p", ("$p", person));
            c.Exec(tx, "DELETE FROM kn_evidence_state WHERE person_id=$p", ("$p", person));
        }, ct);

        /// <summary>How many anchors wait for a person.</summary>
        public int Count(string person)
        {
            using var c = Stores.Intel.OpenRead();
            return (int)c.Scalar<long>(null, "SELECT COUNT(*) FROM kn_evidence_queue WHERE person_id=$p", ("$p", person));
        }

        /// <summary>Whether a person's history has been looked through to its end (false when it has not been started).</summary>
        public bool Complete(string person, string promptVersion)
        {
            using var c = Stores.Intel.OpenRead();
            return c.Scalar<long?>(null, "SELECT complete FROM kn_evidence_state WHERE person_id=$p AND prompt_version=$v", ("$p", person), ("$v", promptVersion)) == 1;
        }

        /// <summary>The oldest anchors waiting for a person.</summary>
        public IReadOnlyList<EvidenceAnchor> Head(string person, int limit)
        {
            using var c = Stores.Intel.OpenRead();
            return c.Query(null, """
                SELECT message_id, version, sent_at_ms, order_key, conversation_id, ingest_seq, relation FROM kn_evidence_queue INDEXED BY ix_kn_evidence_queue_order
                WHERE person_id=$p ORDER BY sent_at_ms, order_key, ingest_seq, message_id LIMIT $n
                """, r => new EvidenceAnchor(r.GetString(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4), r.GetInt64(5), (EvidenceRelation)r.GetInt32(6)),
                ("$p", person), ("$n", Math.Max(1, limit)));
        }

        /// <summary>Takes anchors out of a person's queue that can no longer be read (the message is gone, or its author's
        /// analysis is switched off): left in, they would stand at the head of the queue for ever.</summary>
        public Task DropAsync(string person, IReadOnlyCollection<string> messageIds, CancellationToken ct)
        {
            if (messageIds.Count == 0) return Task.CompletedTask;
            return Stores.Intel.WriteAsync((c, tx) =>
            {
                foreach (var id in messageIds) c.Exec(tx, "DELETE FROM kn_evidence_queue WHERE person_id=$p AND message_id=$m", ("$p", person), ("$m", id));
            }, ct);
        }

        // ── history, a page at a time ────────────────────────────────────────────

        private sealed record Found(string MessageId, int Version, long SentAtMs, long OrderKey, string ConversationId, long IngestSeq, EvidenceRelation Relation);

        private static Found Map(SqliteDataReader r, EvidenceRelation relation) =>
            new(r.GetString(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4), r.GetInt64(5), relation);

        private const string AnchorColumns = "message_id, current_version, sent_at_ms, order_key, conversation_id, ingest_seq";

        /// <summary>
        /// Looks through more of a person's history until <paramref name="want"/> anchors wait in their queue, the history is
        /// at its end, or <paramref name="budget"/> is used. Each step reads one page (a few hundred rows) and says so.
        /// </summary>
        public async Task<EvidenceTopUp> TopUpAsync(string person, string promptVersion, int want, TimeSpan budget, CancellationToken ct, Func<object, Task>? progress = null)
        {
            // One at a time per person: the sweep and the person's own job would otherwise read the same page twice, and the
            // slower of the two would put the cursor back.
            var gate = toppingUp.GetOrAdd(person, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await TopUpCoreAsync(person, promptVersion, want, budget, ct, progress).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> toppingUp = new(StringComparer.Ordinal);

        private async Task<EvidenceTopUp> TopUpCoreAsync(string person, string promptVersion, int want, TimeSpan budget, CancellationToken ct, Func<object, Task>? progress)
        {
            var sw = Stopwatch.StartNew();
            long asOf = Now;
            var (state, complete, otherPrompt) = Load(person, promptVersion);
            // Another prompt version reads everything again: the queue is rebuilt against that version's coverage.
            if (otherPrompt) await ResetAsync(person, ct).ConfigureAwait(false);

            var identities = kit.Corpus.IdentitiesOf(kit.Corpus.PersonIdsOf(person)).Select(i => i.IdentityId).Distinct(StringComparer.Ordinal).ToList();
            foreach (var gone in state.Authored.Keys.Except(identities, StringComparer.Ordinal).ToList()) state.Authored.Remove(gone);
            foreach (var gone in state.Mentions.Keys.Except(identities, StringComparer.Ordinal).ToList()) state.Mentions.Remove(gone);
            // An identity that joined the person (a merge) has its own history to look through.
            if (identities.Any(i => !state.Authored.ContainsKey(i) || !state.Mentions.ContainsKey(i))) complete = false;
            foreach (var i in identities)
            {
                state.Authored.TryAdd(i, new Cursor());
                state.Mentions.TryAdd(i, new Cursor { T = 0 });
            }
            var names = Names(person, identities, asOf);
            if (names.Any(n => !state.Names.Contains(n, StringComparer.OrdinalIgnoreCase))) complete = false;

            int queued = Count(person), added = 0, looked = 0;
            // One page at least, however long the reads before it took: a pass that found its time gone would otherwise
            // never move, and neither would the one after it.
            for (int step = 0; !complete && queued < want && (step == 0 || sw.Elapsed < budget); step++)
            {
                ct.ThrowIfCancellationRequested();
                var found = new List<Found>();
                var conversations = new HashSet<string>(StringComparer.Ordinal);
                string phase;
                if (state.Authored.Where(kv => !kv.Value.Done).OrderBy(kv => kv.Value.T).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key).FirstOrDefault() is { } author)
                {
                    phase = "authored";
                    looked += AuthoredPage(author, state.Authored[author], asOf, found, conversations);
                }
                else if (state.Mentions.Where(kv => !kv.Value.Done).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault() is { } mentioned)
                {
                    phase = "mentions";
                    looked += MentionPage(mentioned, state.Mentions[mentioned], asOf, found);
                }
                else if (names.FirstOrDefault(n => !state.Names.Contains(n, StringComparer.OrdinalIgnoreCase)) is { } name)
                {
                    phase = "names";
                    var scope = Conversations(person, identities);
                    foreach (var a in assembler.NameMentions(new[] { name }, scope, identities, asOf))
                        found.Add(new Found(a.MessageId, a.Version, a.SentAtMs, a.OrderKey, a.ConversationId, a.IngestSeq, EvidenceRelation.Mention));
                    looked += found.Count;
                    state.Names.Add(name.ToLowerInvariant());
                }
                else
                {
                    complete = true;
                    await SaveAsync(person, promptVersion, state, true, Array.Empty<Found>(), conversations, ct).ConfigureAwait(false);
                    break;
                }
                var unread = Unread(person, promptVersion, found);
                bool done = state.Authored.Values.All(c => c.Done) && state.Mentions.Values.All(c => c.Done) && names.All(n => state.Names.Contains(n, StringComparer.OrdinalIgnoreCase));
                int inserted = await SaveAsync(person, promptVersion, state, done, unread, conversations, ct).ConfigureAwait(false);
                complete = done;
                added += inserted;
                queued += inserted;
                if (progress != null) await progress(new { person, stage = "evidence", phase, looked, queued }).ConfigureAwait(false);
            }
            return new EvidenceTopUp(queued, added, looked, complete);
        }

        /// <summary>The next page of what one identity wrote, oldest first, with the replies to those messages and the messages
        /// they reply to. Returns how many of the identity's messages were looked at.</summary>
        private int AuthoredPage(string identity, Cursor cursor, long asOf, List<Found> found, HashSet<string> conversations)
        {
            using var c = Stores.Corpus.OpenRead();
            var parents = new HashSet<string>(StringComparer.Ordinal);
            // Read along the (author, time) index, from where the last page ended: the rows of this page and no others.
            var page = c.Query(null, $"""
                SELECT {AnchorColumns}, rowid, reply_to_message_id FROM messages INDEXED BY ix_messages_author_time
                WHERE author_identity_id=$a AND (sent_at_ms, rowid) > ($t, $r) AND sent_at_ms <= $now
                ORDER BY sent_at_ms, rowid LIMIT $n
                """, r =>
                {
                    if (!r.IsDBNull(7)) parents.Add(r.GetString(7));
                    return (Anchor: Map(r, EvidenceRelation.Authored), Row: r.GetInt64(6));
                }, ("$a", identity), ("$t", cursor.T), ("$r", cursor.R), ("$now", asOf), ("$n", PageSize));
            if (page.Count == 0)
            {
                cursor.Done = true;
                return 0;
            }
            foreach (var (anchor, _) in page)
            {
                found.Add(anchor);
                conversations.Add(anchor.ConversationId);
            }
            var ids = page.Select(p => p.Anchor.MessageId).ToList();
            foreach (var a in c.QueryIn(null, $"SELECT {AnchorColumns} FROM messages WHERE reply_to_message_id IN ({{keys}})", ids, r => Map(r, EvidenceRelation.ReplyToSubject)))
                if (a.SentAtMs <= asOf) found.Add(a);
            foreach (var a in c.QueryIn(null, $"SELECT {AnchorColumns} FROM messages WHERE message_id IN ({{keys}})", parents.ToList(), r => Map(r, EvidenceRelation.RepliedBySubject)))
                if (a.SentAtMs <= asOf) found.Add(a);
            (cursor.T, cursor.R) = (page[^1].Anchor.SentAtMs, page[^1].Row);
            if (page.Count < PageSize) cursor.Done = true;
            return page.Count;
        }

        /// <summary>The next page of messages that @-mention one identity, in the order the mentions were stored.</summary>
        private int MentionPage(string identity, Cursor cursor, long asOf, List<Found> found)
        {
            using var c = Stores.Corpus.OpenRead();
            var page = c.Query(null, """
                SELECT rowid, message_id FROM message_mentions INDEXED BY ix_message_mentions_target
                WHERE target_kind='user' AND target_id=$i AND rowid > $r ORDER BY rowid LIMIT $n
                """, r => (Row: r.GetInt64(0), Message: r.GetString(1)), ("$i", identity), ("$r", cursor.R), ("$n", PageSize));
            if (page.Count == 0)
            {
                cursor.Done = true;
                return 0;
            }
            foreach (var a in c.QueryIn(null, $"SELECT {AnchorColumns} FROM messages WHERE message_id IN ({{keys}})", page.Select(p => p.Message).Distinct(StringComparer.Ordinal).ToList(),
                         r => Map(r, EvidenceRelation.Mention)))
                if (a.SentAtMs <= asOf) found.Add(a);
            cursor.R = page[^1].Row;
            if (page.Count < PageSize) cursor.Done = true;
            return page.Count;
        }

        /// <summary>The names a person is looked for by: what their identities were called (and the first word of each), and
        /// the aliases that have been accepted.</summary>
        private List<string> Names(string person, IReadOnlyCollection<string> identities, long asOf) =>
            kit.Corpus.NamesOf(identities).SelectMany(n => new[] { n.Value, n.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "" })
                .Concat(new Entities.AliasEngine(kit, new ReviewQueue(Stores, kit.Context.Clock)).Aliases(person, asOf).Where(a => a.Status is "accepted" or "confirmed").Select(a => a.Alias))
                .Select(n => n.Trim()).Where(n => n.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxNames).ToList();

        /// <summary>Where a person writes or is a member: names are community-scoped, so they are looked for there only.</summary>
        private List<string> Conversations(string person, IReadOnlyCollection<string> identities)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            using (var c = Stores.Intel.OpenRead())
                foreach (var id in c.Query(null, "SELECT conversation_id FROM kn_evidence_convs WHERE person_id=$p", r => r.GetString(0), ("$p", person))) result.Add(id);
            using (var c = Stores.Corpus.OpenRead())
                foreach (var id in c.QueryIn(null, "SELECT conversation_id FROM conversation_members WHERE identity_id IN ({keys})", identities, r => r.GetString(0))) result.Add(id);
            return result.ToList();
        }

        /// <summary>Of what was found, what this prompt version has not read at the message's current version (one row per message).</summary>
        private List<Found> Unread(string person, string promptVersion, IReadOnlyList<Found> found)
        {
            if (found.Count == 0) return new List<Found>();
            // The strongest relation wins when a message was found twice (what they wrote over a reply over a mention).
            var distinct = found.GroupBy(f => f.MessageId, StringComparer.Ordinal).Select(g => g.OrderBy(f => (int)f.Relation).First()).ToList();
            Dictionary<string, (int Version, string Prompt)> covered;
            using (var c = Stores.Intel.OpenRead())
                covered = c.QueryIn(null, "SELECT message_id, message_version, prompt_version FROM kn_coverage WHERE message_id IN ({keys}) AND person_id=$p",
                        distinct.Select(f => f.MessageId).ToList(), r => (Id: r.GetString(0), V: r.GetInt32(1), P: r.GetString(2)), 500, ("$p", person))
                    .ToDictionary(x => x.Id, x => (x.V, x.P), StringComparer.Ordinal);
            return distinct.Where(f => !covered.TryGetValue(f.MessageId, out var cv) || cv.Version < f.Version || cv.Prompt != promptVersion).ToList();
        }

        /// <summary>Queues what was found and records how far the history has been looked through, in one transaction: a
        /// crash between the two would otherwise skip a page for good. Returns how many anchors were new to the queue.</summary>
        private Task<int> SaveAsync(string person, string promptVersion, QueueState state, bool complete, IReadOnlyList<Found> unread, IReadOnlyCollection<string> conversations, CancellationToken ct)
        {
            long now = Now;
            string json = JsonSerializer.Serialize(state, KernelJson.Options);
            return Stores.Intel.WriteAsync((c, tx) =>
            {
                int inserted = Enqueue(c, tx, person, unread, now);
                foreach (var conversation in conversations)
                    c.Exec(tx, "INSERT OR IGNORE INTO kn_evidence_convs(person_id, conversation_id) VALUES($p,$c)", ("$p", person), ("$c", conversation));
                c.Exec(tx, """
                    INSERT INTO kn_evidence_state(person_id, prompt_version, complete, state_json, updated_at_ms) VALUES($p,$v,$d,$s,$t)
                    ON CONFLICT(person_id) DO UPDATE SET prompt_version=excluded.prompt_version, complete=excluded.complete, state_json=excluded.state_json, updated_at_ms=excluded.updated_at_ms
                    """, ("$p", person), ("$v", promptVersion), ("$d", complete ? 1 : 0), ("$s", json), ("$t", now));
                return inserted;
            }, ct);
        }

        private static int Enqueue(SqliteConnection c, SqliteTransaction tx, string person, IReadOnlyList<Found> anchors, long now)
        {
            int inserted = 0;
            foreach (var a in anchors)
            {
                bool queuedAlready = c.Scalar<long?>(tx, "SELECT 1 FROM kn_evidence_queue WHERE person_id=$p AND message_id=$m", ("$p", person), ("$m", a.MessageId)) != null;
                c.Exec(tx, """
                    INSERT INTO kn_evidence_queue(person_id, message_id, version, sent_at_ms, order_key, ingest_seq, conversation_id, relation, queued_at_ms)
                    VALUES($p,$m,$v,$s,$o,$i,$c,$r,$t)
                    ON CONFLICT(person_id, message_id) DO UPDATE SET version=MAX(version, excluded.version), sent_at_ms=excluded.sent_at_ms, order_key=excluded.order_key,
                        relation=MIN(relation, excluded.relation)
                    """, ("$p", person), ("$m", a.MessageId), ("$v", a.Version), ("$s", a.SentAtMs), ("$o", a.OrderKey), ("$i", a.IngestSeq), ("$c", a.ConversationId),
                    ("$r", (int)a.Relation), ("$t", now));
                if (!queuedAlready) inserted++;
            }
            return inserted;
        }

        // ── what arrives afterwards: the change log ──────────────────────────────

        private sealed record Tracked(Dictionary<string, string> PersonOfIdentity, Dictionary<string, List<string[]>> NamesOf, long LoadedAtMs, string Key);

        private Tracked? trackedCache;

        /// <summary>The tracked people's identities and the names they are looked for by. Read again when the set of tracked
        /// people changes, and every few minutes for a new alias or a renamed account.</summary>
        private Tracked LoadTracked(IReadOnlyCollection<string> tracked)
        {
            string key = string.Join("|", tracked.OrderBy(p => p, StringComparer.Ordinal));
            long now = Now;
            if (trackedCache is { } cached && cached.Key == key && now - cached.LoadedAtMs is >= 0 and < 300_000) return cached;
            var personOf = new Dictionary<string, string>(StringComparer.Ordinal);
            var namesOf = new Dictionary<string, List<string[]>>(StringComparer.Ordinal);
            foreach (var person in tracked)
            {
                var identities = kit.Corpus.IdentitiesOf(kit.Corpus.PersonIdsOf(person)).Select(i => i.IdentityId).Distinct(StringComparer.Ordinal).ToList();
                foreach (var identity in identities) personOf[identity] = person;
                var phrases = Names(person, identities, now).Select(NameWords).Where(w => w.Length > 0).ToList();
                if (phrases.Count > 0) namesOf[person] = phrases;
            }
            return trackedCache = new Tracked(personOf, namesOf, now, key);
        }

        /// <summary>
        /// Routes what the corpus change log has recorded since the last pass to the people it is evidence about: a new or
        /// edited message to its author, to whoever it replies to, to whoever replied to it, to everyone it @-mentions and to
        /// everyone it names in a conversation they belong to. Nothing here depends on how much anybody has written: a
        /// message costs a handful of index lookups. The first pass starts at the end of the log (what was there before is
        /// each person's history, looked through by <see cref="TopUpAsync"/>).
        /// <para>A merge, an unmerge or analysis switched back on resets the people concerned: their queues are rebuilt.</para>
        /// </summary>
        /// <returns>Null when another pass is running (it will have covered the same rows).</returns>
        public async Task<EvidenceRouted?> RouteAsync(IReadOnlyCollection<string> tracked, string promptVersion, int maxChanges, TimeSpan budget, CancellationToken ct)
        {
            if (!await routing.WaitAsync(0, ct).ConfigureAwait(false)) return null;
            try
            {
                var sw = Stopwatch.StartNew();
                long maxSeq = kit.Corpus.MaxSeq();
                long? stored;
                using (var c = Stores.Intel.OpenRead())
                    stored = c.Scalar<long?>(null, "SELECT seq FROM kn_evidence_cursor WHERE name=$n", ("$n", RouteCursorName));
                if (stored == null || tracked.Count == 0)
                {
                    // Nobody to route to (or the very first pass): the log is taken as read up to here.
                    if (stored != maxSeq) await SaveRouteAsync(Array.Empty<(string, Found)>(), Array.Empty<(string, string)>(), maxSeq, ct).ConfigureAwait(false);
                    return new EvidenceRouted(maxSeq, maxSeq, 0, 0, Array.Empty<string>());
                }
                long cursor = stored.Value;
                var context = LoadTracked(tracked);
                var off = kit.Corpus.AnalysisOff;
                long asOf = Now;
                int changes = 0, added = 0;
                var touched = new HashSet<string>(StringComparer.Ordinal);
                for (int step = 0; cursor < maxSeq && changes < maxChanges && (step == 0 || sw.Elapsed < budget); step++)
                {
                    ct.ThrowIfCancellationRequested();
                    long upTo = Math.Min(maxSeq, cursor + RouteWindow);
                    List<(string Kind, string Entity, string? Person)> window;
                    using (var c = Stores.Corpus.OpenRead())
                        window = c.Query(null, """
                            SELECT kind, entity_id, person_id FROM change_log WHERE seq > $a AND seq <= $b
                              AND kind IN ('message.insert','message.version','person.merge','person.unmerge','person.reanalyse')
                            ORDER BY seq
                            """, r => (r.GetString(0), r.GetString(1), r.GetStringOrNull(2)), ("$a", cursor), ("$b", upTo));
                    changes += window.Count;

                    // People whose identities or standing changed are rebuilt from their history.
                    foreach (var change in window.Where(w => w.Kind is ChangeKinds.PersonMerge or ChangeKinds.PersonUnmerge or ChangeKinds.PersonReanalyse))
                        foreach (var id in new[] { change.Entity, change.Person }.OfType<string>().SelectMany(p => new[] { p, kit.Corpus.Canonical(p) }).Distinct(StringComparer.Ordinal))
                        {
                            await ResetAsync(id, ct).ConfigureAwait(false);
                            if (tracked.Contains(id)) touched.Add(id);
                            trackedCache = null;
                        }
                    if (trackedCache == null) context = LoadTracked(tracked);

                    var messageIds = window.Where(w => w.Kind is ChangeKinds.MessageInsert or ChangeKinds.MessageVersion).Select(w => w.Entity).Distinct(StringComparer.Ordinal).ToList();
                    var (anchors, conversations) = messageIds.Count == 0 ? (new List<(string, Found)>(), new List<(string, string)>()) : Resolve(messageIds, context, off, asOf);
                    // Per person: only what this prompt version has not read at the message's current version.
                    var unread = new List<(string Person, Found Anchor)>();
                    foreach (var group in anchors.GroupBy(a => a.Item1, StringComparer.Ordinal))
                        foreach (var anchor in Unread(group.Key, promptVersion, group.Select(g => g.Item2).ToList()))
                            unread.Add((group.Key, anchor));
                    var inserted = await SaveRouteAsync(unread, conversations, upTo, ct).ConfigureAwait(false);
                    foreach (var person in inserted) touched.Add(person);
                    added += unread.Count;
                    cursor = upTo;
                }
                return new EvidenceRouted(cursor, maxSeq, changes, added, touched);
            }
            finally
            {
                routing.Release();
            }
        }

        /// <summary>For each changed message: who it is evidence about, and in what way.</summary>
        private (List<(string Person, Found Anchor)> Anchors, List<(string Person, string Conversation)> Conversations) Resolve(
            IReadOnlyCollection<string> messageIds, Tracked tracked, AnalysisOffSet off, long asOf)
        {
            var anchors = new List<(string, Found)>();
            var conversations = new List<(string, string)>();
            using var c = Stores.Corpus.OpenRead();
            var rows = c.QueryIn(null, $"SELECT {AnchorColumns}, author_identity_id, reply_to_message_id, content FROM messages WHERE message_id IN ({{keys}})", messageIds,
                r => (Anchor: Map(r, EvidenceRelation.Authored), Author: r.GetString(6), Parent: r.GetStringOrNull(7), Content: r.GetStringOrNull(8)));
            // What someone whose analysis is switched off wrote is never read, for anybody.
            rows = rows.Where(x => x.Anchor.SentAtMs <= asOf && !off.Author(x.Author)).ToList();
            if (rows.Count == 0) return (anchors, conversations);
            var ids = rows.Select(x => x.Anchor.MessageId).ToList();

            var parents = c.QueryIn(null, $"SELECT {AnchorColumns}, author_identity_id FROM messages WHERE message_id IN ({{keys}})",
                    rows.Where(x => x.Parent != null).Select(x => x.Parent!).Distinct(StringComparer.Ordinal).ToList(), r => (Anchor: Map(r, EvidenceRelation.RepliedBySubject), Author: r.GetString(6)))
                .ToDictionary(x => x.Anchor.MessageId, StringComparer.Ordinal);
            var children = c.QueryIn(null, "SELECT reply_to_message_id, author_identity_id FROM messages WHERE reply_to_message_id IN ({keys})", ids, r => (Parent: r.GetString(0), Author: r.GetString(1)))
                .ToLookup(x => x.Parent, x => x.Author, StringComparer.Ordinal);
            var mentions = c.QueryIn(null, "SELECT message_id, target_id FROM message_mentions WHERE target_kind='user' AND message_id IN ({keys})", ids, r => (Message: r.GetString(0), Target: r.GetString(1)))
                .ToLookup(x => x.Message, x => x.Target, StringComparer.Ordinal);

            // Who belongs to the conversations concerned (names are looked for only where the person writes or is a member).
            var scope = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            if (tracked.NamesOf.Count > 0)
            {
                var convs = rows.Select(x => x.Anchor.ConversationId).Distinct(StringComparer.Ordinal).ToList();
                void Belongs(string conversation, string person)
                {
                    if (!scope.TryGetValue(conversation, out var set)) scope[conversation] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(person);
                }
                foreach (var (conversation, identity) in c.QueryIn(null, "SELECT conversation_id, identity_id FROM conversation_members WHERE conversation_id IN ({keys})", convs, r => (r.GetString(0), r.GetString(1))))
                    if (tracked.PersonOfIdentity.TryGetValue(identity, out var p)) Belongs(conversation, p);
                using var intel = Stores.Intel.OpenRead();
                foreach (var (conversation, person) in intel.QueryIn(null, "SELECT conversation_id, person_id FROM kn_evidence_convs WHERE conversation_id IN ({keys})", convs, r => (r.GetString(0), r.GetString(1))))
                    Belongs(conversation, person);
            }

            foreach (var row in rows)
            {
                tracked.PersonOfIdentity.TryGetValue(row.Author, out var author);
                if (author != null && !off.Person(author))
                {
                    anchors.Add((author, row.Anchor));
                    conversations.Add((author, row.Anchor.ConversationId));
                    // What they replied to is evidence about them too.
                    if (row.Parent != null && parents.TryGetValue(row.Parent, out var parent) && parent.Anchor.SentAtMs <= asOf && !off.Author(parent.Author))
                        anchors.Add((author, parent.Anchor));
                }
                // A reply to something a tracked person wrote.
                if (row.Parent != null && parents.TryGetValue(row.Parent, out var replied) && tracked.PersonOfIdentity.TryGetValue(replied.Author, out var parentAuthor)
                    && parentAuthor != author && !off.Person(parentAuthor))
                    anchors.Add((parentAuthor, row.Anchor with { Relation = EvidenceRelation.ReplyToSubject }));
                // A message that arrived after the replies to it (history filled in backwards).
                foreach (var child in children[row.Anchor.MessageId])
                    if (tracked.PersonOfIdentity.TryGetValue(child, out var replier) && replier != author && !off.Person(replier))
                        anchors.Add((replier, row.Anchor with { Relation = EvidenceRelation.RepliedBySubject }));
                foreach (var target in mentions[row.Anchor.MessageId])
                    if (tracked.PersonOfIdentity.TryGetValue(target, out var mentioned) && mentioned != author && !off.Person(mentioned))
                        anchors.Add((mentioned, row.Anchor with { Relation = EvidenceRelation.Mention }));
                if (!string.IsNullOrEmpty(row.Content) && scope.TryGetValue(row.Anchor.ConversationId, out var members))
                {
                    string[]? words = null;
                    foreach (var member in members)
                    {
                        if (member == author || off.Person(member) || !tracked.NamesOf.TryGetValue(member, out var phrases)) continue;
                        words ??= NameWords(row.Content);
                        if (phrases.Any(p => ContainsPhrase(words, p))) anchors.Add((member, row.Anchor with { Relation = EvidenceRelation.Mention }));
                    }
                }
            }
            return (anchors, conversations);
        }

        /// <summary>Writes what a step of a routing pass found, with the cursor, in one transaction. Returns the people whose
        /// queue gained an anchor.</summary>
        private Task<List<string>> SaveRouteAsync(IReadOnlyCollection<(string Person, Found Anchor)> unread, IReadOnlyCollection<(string Person, string Conversation)> conversations,
            long cursor, CancellationToken ct)
        {
            long now = Now;
            return Stores.Intel.WriteAsync((c, tx) =>
            {
                var gained = new List<string>();
                foreach (var group in unread.GroupBy(u => u.Person, StringComparer.Ordinal))
                    if (Enqueue(c, tx, group.Key, group.Select(g => g.Anchor).ToList(), now) > 0) gained.Add(group.Key);
                foreach (var (person, conversation) in conversations.Distinct())
                    c.Exec(tx, "INSERT OR IGNORE INTO kn_evidence_convs(person_id, conversation_id) VALUES($p,$c)", ("$p", person), ("$c", conversation));
                c.Exec(tx, """
                    INSERT INTO kn_evidence_cursor(name, seq, updated_at_ms) VALUES($n,$s,$t)
                    ON CONFLICT(name) DO UPDATE SET seq=MAX(seq, excluded.seq), updated_at_ms=excluded.updated_at_ms
                    """, ("$n", RouteCursorName), ("$s", cursor), ("$t", now));
                return gained;
            }, ct);
        }

        // ── names in text ────────────────────────────────────────────────────────

        /// <summary>
        /// The words of a text as the full-text index holds them (unicode61, diacritics removed): runs of letters and digits,
        /// lower case. A name is found in a message when its words stand there in order, which is what the phrase search
        /// over history matches too.
        /// </summary>
        /// <remarks>
        /// It never throws, whatever the text holds. Normalising a whole string does throw on half a surrogate pair and on
        /// U+FFFE, which anybody can paste into a message; one such message would stop the routing of every message after
        /// it, for everyone, because the routing cursor cannot pass a window it cannot read. So the text is taken a
        /// character at a time, and what is not a character ends a word and nothing else.
        /// </remarks>
        internal static string[] NameWords(string text)
        {
            var words = new List<string>();
            var word = new StringBuilder();
            void End()
            {
                if (word.Length == 0) return;
                words.Add(word.ToString());
                word.Clear();
            }
            for (int i = 0; i < text.Length; i++)
            {
                char raw = text[i];
                if (raw < 128)
                {
                    if (char.IsAsciiLetterOrDigit(raw)) word.Append(char.ToLowerInvariant(raw));
                    else End();
                }
                else if (char.IsHighSurrogate(raw) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    // One character outside the basic plane: a letter of a rare script belongs to the word, an emoji ends it.
                    if (char.IsLetterOrDigit(text, i)) word.Append(raw).Append(text[i + 1]);
                    else End();
                    i++;
                }
                else if (char.IsSurrogate(raw)) End(); // half a pair: text cut in the middle of an emoji
                else if (Fold(raw) is { } letters) word.Append(letters);
                else End();
            }
            End();
            return words.ToArray();
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<char, string?> folds = new();

        /// <summary>One character of the basic plane as the index holds it: its letters and digits in lower case without
        /// their marks ("é" is "e"), "" for a mark that belongs to the letter before it, null for whatever ends a word.</summary>
        private static string? Fold(char c) => folds.GetOrAdd(c, static ch =>
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark) return "";
            if (!char.IsLetterOrDigit(ch)) return null;
            string decomposed;
            try
            {
                decomposed = ch.ToString().Normalize(NormalizationForm.FormD);
            }
            catch (ArgumentException)
            {
                // Not a character that can be normalised: it ends a word.
                return null;
            }
            var sb = new StringBuilder(decomposed.Length);
            foreach (char d in decomposed)
                if (char.IsLetterOrDigit(d)) sb.Append(char.ToLowerInvariant(d));
            return sb.Length == 0 ? null : sb.ToString();
        });

        internal static bool ContainsPhrase(string[] words, string[] phrase)
        {
            if (phrase.Length == 0 || phrase.Length > words.Length) return false;
            for (int i = 0; i + phrase.Length <= words.Length; i++)
            {
                int k = 0;
                while (k < phrase.Length && string.Equals(words[i + k], phrase[k], StringComparison.Ordinal)) k++;
                if (k == phrase.Length) return true;
            }
            return false;
        }
    }
}
