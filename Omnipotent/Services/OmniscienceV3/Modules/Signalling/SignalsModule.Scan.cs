using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel.Events;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Modules.Classification;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Modules.Signalling
{
    public sealed partial class SignalsModule
    {
        private const int ScanBatch = 2_000;
        private const int MaxHitsKept = 50;

        /// <summary>Code, links and custom emoji: a name inside them is not someone talking about Klives.</summary>
        private static readonly Regex NotProse = new(@"```.*?```|`[^`\n]*`|https?://\S+|<a?:\w+:\d+>", RegexOptions.Singleline | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

        private sealed record Msg(string Id, string Conversation, string? Guild, string Platform, string AuthorIdentity, string? Person, bool Bot, long SentAt, long IngestedAt,
            string FirstSource, string? ReplyTo, long? DeletedAt, string? ConversationKind, string? Content)
        {
            /// <summary>What a burst is grouped by: the server when there is one (a burst can span its channels), otherwise the chat.</summary>
            public string GroupKey => Guild ?? Conversation;
        }

        private sealed record RadarHit(Msg Message, string Reason, double? Probability, int? Labelset);

        private sealed record InboxOpen(string ItemId, string Kind, Msg Message, string Reason, double? Probability, bool Live, string State, long? SnoozedUntil);

        private sealed record InboxAnswer(string Conversation, bool Chat, long At, string ByMessage, string? RepliesTo);

        private sealed record WatchCandidate(string WatchId, string MessageId, double? Similarity);

        /// <summary>How a message came to be looked at in this pass.</summary>
        private readonly record struct Trigger(bool LiveInsert, bool LiveEdit, bool GraceOver);

        /// <summary>Everything one batch decided, applied in one intel transaction together with the cursors it read to.</summary>
        private sealed class Decisions
        {
            public readonly List<string> Looked = new();
            public readonly List<RadarHit> Radar = new();
            public readonly List<InboxOpen> Open = new();
            public readonly List<InboxAnswer> Answers = new();
            public readonly List<WatchCandidate> Candidates = new();
            public readonly List<(string MessageId, long At)> Deleted = new();
            public readonly List<(string MessageId, long DueMs)> Pending = new();
            public readonly HashSet<string> Erased = new(StringComparer.Ordinal);
        }

        /// <summary>What a whole pass shares between its batches (read once, off the ingest path: F050).</summary>
        private sealed class ScanState
        {
            public required HashSet<string> Self { get; init; }
            public required Regex? Aliases { get; init; }
            public required bool ReadingsExpected { get; init; }
            public required IReadOnlyList<ActiveWatch> Watches { get; init; }
            public required long Now { get; init; }
            public required long FreshnessMs { get; init; }
            public required long StaleMs { get; init; }
            public required long GraceMs { get; init; }
            public required long ChannelAnswerMs { get; init; }
            public required double Named { get; init; }
            public required double Indirect { get; init; }
            public required double Act { get; init; }
            public Dictionary<string, bool> KlivesDm { get; } = new(StringComparer.Ordinal);
        }

        /// <summary>
        /// One pass: the corpus change log (new, edited and deleted messages) and the classification outbox (messages whose
        /// labels just arrived) from where the last pass stopped. A message is looked at when it arrives, for what is exact,
        /// and again when the ensemble has read it, for what needs meaning. Every write is keyed by the message, so looking
        /// twice never alerts twice.
        /// </summary>
        private async Task<string?> ScanAsync(JobContext job)
        {
            var ct = job.Cancellation;
            long now = Context.Clock.NowMs;
            long changeCursor, labelCursor;
            bool first, hasOutbox;
            List<string> due;
            using (var c = Context.Stores.Intel.OpenRead())
            {
                first = c.Scalar<long?>(null, "SELECT value FROM sig_state WHERE name='changes'") == null;
                changeCursor = Cursor(c, "changes");
                labelCursor = Cursor(c, "labels");
                hasOutbox = c.Scalar<long>(null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='classify_outbox'") > 0;
                due = c.Query(null, "SELECT message_id FROM sig_pending WHERE due_ms <= $n LIMIT 500", r => r.GetString(0), ("$n", now));
            }
            ScanState state;
            long maxSeq;
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                maxSeq = ChangeLog.MaxSeq(corpus);
                long staleMs = StaleMs;
                // The first pass starts where the reply inbox's window starts: nothing older can raise anything.
                if (first) changeCursor = FirstChangeAtOrAfter(corpus, now - staleMs, maxSeq) - 1;
                var self = ClassificationModule.SelfIdentities(corpus);
                state = new ScanState
                {
                    Self = self, Aliases = AliasMatcher(corpus, self), ReadingsExpected = readings.Expected, Watches = ActiveWatches(), Now = now, FreshnessMs = FreshnessMs,
                    StaleMs = staleMs, GraceMs = Context.Settings.Get("signals.radar.reading_grace_seconds", 90) * 1000L,
                    ChannelAnswerMs = Context.Settings.Get("signals.inbox.channel_answer_minutes", 120) * 60_000L,
                    Named = Context.Settings.Get("signals.radar.named_probability", 0.6), Indirect = Context.Settings.Get("signals.radar.indirect_probability", 0.75),
                    Act = Context.Settings.Get("signals.inbox.act_probability", 0.5),
                };
            }

            int alerts = 0, items = 0, answered = 0, queued = 0, looked = 0;
            bool erased = false;
            var events = new List<object>();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var triggers = new Dictionary<string, Trigger>(StringComparer.Ordinal);
                var decisions = new Decisions();
                long nextChange = changeCursor, nextLabel = labelCursor;
                int changeRows = 0, labelRows = 0;
                using (var corpus = Context.Stores.Corpus.OpenRead())
                {
                    foreach (var ch in corpus.Query(null, "SELECT seq, at_ms, kind, entity_id, person_id, source_kind FROM change_log WHERE seq > $s AND seq <= $m AND kind IN ($a,$b,$c,$e) ORDER BY seq LIMIT $l",
                                 r => (Seq: r.GetInt64(0), At: r.GetInt64(1), Kind: r.GetString(2), Entity: r.GetString(3), Person: r.GetStringOrNull(4), Source: r.GetString(5)),
                                 ("$s", changeCursor), ("$m", maxSeq), ("$a", ChangeKinds.MessageInsert), ("$b", ChangeKinds.MessageVersion), ("$c", ChangeKinds.MessageDelete),
                                 ("$e", ChangeKinds.PersonErase), ("$l", ScanBatch)))
                    {
                        changeRows++;
                        nextChange = ch.Seq;
                        if (ch.Kind == ChangeKinds.PersonErase)
                        {
                            decisions.Erased.Add(ch.Entity);
                            if (ch.Person != null) decisions.Erased.Add(ch.Person);
                            continue;
                        }
                        if (ch.Kind == ChangeKinds.MessageDelete) decisions.Deleted.Add((ch.Entity, ch.At));
                        bool live = ch.Source == SourceKinds.Live && now - ch.At <= state.FreshnessMs;
                        var t = triggers.GetValueOrDefault(ch.Entity);
                        triggers[ch.Entity] = t with { LiveInsert = t.LiveInsert || (live && ch.Kind == ChangeKinds.MessageInsert), LiveEdit = t.LiveEdit || (live && ch.Kind == ChangeKinds.MessageVersion) };
                    }
                }
                // Fewer rows than asked for: nothing else of ours up to the watermark.
                if (changeRows < ScanBatch) nextChange = maxSeq;
                if (hasOutbox)
                {
                    using var intel = Context.Stores.Intel.OpenRead();
                    foreach (var (seq, id) in intel.Query(null, "SELECT seq, message_id FROM classify_outbox WHERE seq > $s ORDER BY seq LIMIT $l", r => (r.GetInt64(0), r.GetString(1)),
                                 ("$s", labelCursor), ("$l", ScanBatch)))
                    {
                        labelRows++;
                        nextLabel = seq;
                        triggers.TryAdd(id, default);
                    }
                }
                // Name matches that waited for a reading long enough are decided now, with or without one.
                foreach (var id in due) triggers[id] = triggers.GetValueOrDefault(id) with { GraceOver = true };
                due.Clear();

                if (triggers.Count > 0)
                {
                    looked += triggers.Count;
                    Decide(triggers, decisions, state);
                }
                erased |= decisions.Erased.Count > 0;
                long c1 = nextChange, c2 = nextLabel;
                var (a, i, r2, q) = await Context.Stores.Intel.WriteAsync((c, tx) =>
                {
                    var applied = Apply(c, tx, decisions, now, c1, events);
                    SetCursor(c, tx, "changes", c1);
                    SetCursor(c, tx, "labels", c2);
                    return applied;
                }, ct).ConfigureAwait(false);
                alerts += a; items += i; answered += r2; queued += q;
                changeCursor = nextChange;
                labelCursor = nextLabel;
                if (changeRows < ScanBatch && labelRows < ScanBatch) break;
                await job.ReportProgressAsync(new { changeCursor, maxSeq, looked }).ConfigureAwait(false);
            }

            if (erased) await PurgeGoneAsync(ct).ConfigureAwait(false);
            int housekept = await Context.Stores.Intel.WriteAsync((c, tx) => Housekeep(c, tx, now, state, events), ct).ConfigureAwait(false);
            foreach (var e in events) await Context.Events.PublishAsync(Topics.Alert, e, ct).ConfigureAwait(false);
            await EnsureWatchesActiveAsync(ct).ConfigureAwait(false);
            if (ConfirmableCandidates() > 0)
                await Context.Jobs.EnqueueAsync(Job(ConfirmKind, "stream", "confirm", priority: 250, lane: Lane.LlmBackground), ct).ConfigureAwait(false);
            if (alerts + items + answered + housekept > 0 || first || erased) await WriteSummaryAsync(now, maxSeq, ct).ConfigureAwait(false);
            return Json(new { looked, alerts, inbox = items, answered, housekept, watchQueued = queued });
        }

        /// <summary>The first change at or after an instant. The log's times rise with its sequence, so a binary search finds it
        /// without reading the log (it has no index by time).</summary>
        private static long FirstChangeAtOrAfter(SqliteConnection corpus, long atMs, long maxSeq)
        {
            long lo = 1, hi = maxSeq + 1;
            while (lo < hi)
            {
                long mid = lo + (hi - lo) / 2;
                var row = corpus.Query(null, "SELECT seq, at_ms FROM change_log WHERE seq >= $s ORDER BY seq LIMIT 1", r => (Seq: r.GetInt64(0), At: r.GetInt64(1)), ("$s", mid)).FirstOrDefault();
                if (row.Seq == 0 || row.At >= atMs) hi = mid;
                else lo = row.Seq + 1;
            }
            return lo;
        }

        // ── names ────────────────────────────────────────────────────────────────

        /// <summary>
        /// One pattern for every name Klives answers to: the names in the settings, the ones he confirmed elsewhere, and the
        /// current names of his own accounts. Whole words only, in any script and any letter case (F048). A name nobody
        /// confirmed is never in here.
        /// </summary>
        private Regex? AliasMatcher(SqliteConnection corpus, HashSet<string> self)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string? n)
            {
                n = n?.Trim();
                if (n is { Length: >= 3 and <= 64 } && n.Any(char.IsLetter)) names.Add(n);
            }
            foreach (var a in Context.Settings.Get("signals.radar.aliases", Array.Empty<string>())) Add(a);
            if (confirmedAliases != null)
                foreach (var a in confirmedAliases()) Add(a);
            if (Context.Settings.Get("signals.radar.account_names", true))
                foreach (var n in corpus.QueryIn(null, "SELECT value FROM identity_names WHERE valid_to_ms IS NULL AND identity_id IN ({keys})", self, r => r.GetString(0))) Add(n);
            if (names.Count == 0) return null;
            string pattern = @"(?<![\p{L}\p{N}_])(?:" + string.Join("|", names.OrderByDescending(n => n.Length).ThenBy(n => n, StringComparer.Ordinal).Select(Regex.Escape)) + @")(?![\p{L}\p{N}_])";
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        }

        private bool NamesKlives(string? content, Regex? aliases)
        {
            if (aliases == null || string.IsNullOrWhiteSpace(content)) return false;
            try
            {
                return aliases.IsMatch(NotProse.Replace(content, " "));
            }
            catch (RegexMatchTimeoutException ex)
            {
                // A pathological text costs its name match, not the pass; the ensemble still reads it.
                Context.LogError("[Signals] name match timed out on one message; it was treated as no match.", ex);
                return false;
            }
        }

        // ── deciding (reads only) ────────────────────────────────────────────────

        private void Decide(Dictionary<string, Trigger> triggers, Decisions d, ScanState s)
        {
            var ids = triggers.Keys.ToList();
            d.Looked.AddRange(ids);
            var self = s.Self;
            using var corpus = Context.Stores.Corpus.OpenRead();
            var messages = LoadMessages(corpus, ids, s.Now - s.StaleMs);
            if (messages.Count == 0) return;
            var found = messages.Select(m => m.Id).ToList();
            var mentionsSelf = self.Count == 0 ? new HashSet<string>() : corpus.QueryIn(null,
                    "SELECT message_id, target_id FROM message_mentions WHERE target_kind='user' AND message_id IN ({keys})", found, r => (r.GetString(0), r.GetString(1)))
                .Where(x => self.Contains(x.Item2)).Select(x => x.Item1).ToHashSet(StringComparer.Ordinal);
            var parents = messages.Where(m => m.ReplyTo != null).Select(m => m.ReplyTo!).Distinct(StringComparer.Ordinal).ToList();
            var parentBySelf = corpus.QueryIn(null, "SELECT message_id, author_identity_id FROM messages WHERE message_id IN ({keys})", parents, r => (r.GetString(0), r.GetString(1)))
                .Where(x => self.Contains(x.Item2)).Select(x => x.Item1).ToHashSet(StringComparer.Ordinal);

            // A direct chat is Klives's own when one of his accounts is in it or wrote in it (a bot's chats with other people are not).
            bool KlivesDm(Msg m)
            {
                if (m.ConversationKind != "dm" || self.Count == 0) return false;
                if (s.KlivesDm.TryGetValue(m.Conversation, out bool known)) return known;
                bool mine = corpus.QueryInWith("SELECT 1 FROM conversation_members WHERE conversation_id=$c AND identity_id IN ({keys}) LIMIT 1", self, r => 1, ("$c", m.Conversation)).Count > 0
                            || corpus.QueryInWith("SELECT 1 FROM messages WHERE conversation_id=$c AND author_identity_id IN ({keys}) LIMIT 1", self, r => 1, ("$c", m.Conversation)).Count > 0;
                return s.KlivesDm[m.Conversation] = mine;
            }

            // Whether Klives already answered: in his own direct chat anything he wrote later; elsewhere a reply to the
            // message, or something he wrote there soon after (later than that he may be talking to someone else).
            bool Answered(Msg m, bool chat) => self.Count > 0 && corpus.QueryInWith("""
                SELECT 1 FROM messages WHERE conversation_id=$c AND sent_at_ms > $t AND deleted_at_ms IS NULL AND author_identity_id IN ({keys})
                  AND ($chat = 1 OR reply_to_message_id = $m OR sent_at_ms <= $t + $w) LIMIT 1
                """, self, r => 1, ("$c", m.Conversation), ("$t", m.SentAt), ("$chat", chat ? 1 : 0), ("$m", m.Id), ("$w", s.ChannelAnswerMs)).Count > 0;

            var read = readings.For(found);
            Dictionary<string, (long? Until, long At)> dismissed;
            using (var op = Context.Stores.Operator.OpenRead())
                dismissed = op.Query(null, "SELECT key, until_ms, at_ms FROM dismissals WHERE kind=$k", r => (r.GetString(0), r.GetInt64OrNull(1), r.GetInt64(2)), ("$k", DismissalKind))
                    .ToDictionary(x => x.Item1, x => (x.Item2, x.Item3), StringComparer.Ordinal);

            var questions = new List<InboxOpen>();
            var tails = new Dictionary<string, (Msg Message, bool Live)>(StringComparer.Ordinal);
            var forWatch = new List<Msg>();
            foreach (var m in messages)
            {
                bool klivesDm = KlivesDm(m);
                if (self.Contains(m.AuthorIdentity))
                {
                    if (m.DeletedAt == null) d.Answers.Add(new InboxAnswer(m.Conversation, klivesDm, m.SentAt, m.Id, m.ReplyTo));
                    continue;
                }
                if (m.Bot || m.DeletedAt != null) continue;
                var t = triggers[m.Id];
                read.TryGetValue(m.Id, out var reading);
                bool mention = mentionsSelf.Contains(m.Id), replyToSelf = m.ReplyTo != null && parentBySelf.Contains(m.ReplyTo);
                // Live means it arrived live and recently (F047): by the change that brought it, or, when it is looked at again
                // for its labels, by how it first arrived. An edit only counts for a message that is itself recent.
                bool live = t.LiveInsert || (t.LiveEdit && s.Now - m.SentAt <= s.FreshnessMs) || (m.FirstSource == SourceKinds.Live && s.Now - m.IngestedAt <= s.FreshnessMs);
                bool authored = !(reading?.NonAuthored ?? false);

                // Radar: someone talking about Klives where he is not the one being spoken to in private.
                if (!klivesDm && live)
                {
                    string? reason = null;
                    double? probability = null;
                    if (mention) reason = "mention";
                    else if (authored && reading is { AboutKlives: "mentioned" } named && named.AboutKlivesProbability >= s.Named) (reason, probability) = ("named", named.AboutKlivesProbability);
                    else if (authored && reading is { AboutKlives: "indirect" } ind && ind.AboutKlivesProbability >= s.Indirect) (reason, probability) = ("indirect", ind.AboutKlivesProbability);
                    else if (authored && NamesKlives(m.Content, s.Aliases))
                    {
                        // A name match is exact about the word, not about who is meant. With a reading, the reading decides:
                        // "not about him" drops it, "spoken to him" is the inbox's business. Without one it waits a little.
                        if (reading == null && s.ReadingsExpected && !t.GraceOver && s.GraceMs > 0) d.Pending.Add((m.Id, s.Now + s.GraceMs));
                        else if (!(reading is { AboutKlives: "none" or "addressed" } other && other.AboutKlivesProbability >= s.Named)) reason = "alias";
                    }
                    if (reason != null) d.Radar.Add(new RadarHit(m, reason, probability, reading?.Labelset));
                }

                // Reply inbox: said to Klives, and a question or a request (the ensemble's reading), or simply his turn in a
                // direct chat. In a group or a server it has to be addressed to him.
                bool toKlives = klivesDm || replyToSelf || mention || (reading is { AboutKlives: "addressed" } to && to.AboutKlivesProbability >= s.Named);
                if (toKlives)
                {
                    string? act = authored && reading is { DialogueAct: "question" or "request" } da && da.DialogueActProbability >= s.Act ? da.DialogueAct : null;
                    string why = klivesDm ? "dm" : replyToSelf ? "reply" : mention ? "mention" : "addressed";
                    if (act != null) questions.Add(Opened("q|" + m.Id, act, m, why, reading!.DialogueActProbability, live, dismissed, s.Now));
                    else if (klivesDm && (!tails.TryGetValue(m.Conversation, out var last) || m.SentAt >= last.Message.SentAt)) tails[m.Conversation] = (m, live);
                }
                if (live && authored) forWatch.Add(m);
            }
            foreach (var q in questions)
                if (!Answered(q.Message, KlivesDm(q.Message))) d.Open.Add(q);
            foreach (var (conversation, (m, live)) in tails)
                if (!Answered(m, true)) d.Open.Add(Opened("tail|" + conversation, "unanswered", m, "dm", null, live, dismissed, s.Now));
            if (forWatch.Count > 0) d.Candidates.AddRange(Prefilter(forWatch, s.Watches));
        }

        private static InboxOpen Opened(string itemId, string kind, Msg m, string reason, double? probability, bool live, Dictionary<string, (long? Until, long At)> dismissed, long now)
        {
            // Klives's own decisions live in operator.db and survive a rebuild of the derived store: a dismissal made after
            // this message stands; a snooze stands until its time.
            string state = "open";
            long? until = null;
            if (dismissed.TryGetValue(itemId, out var dis) && dis.At >= m.SentAt)
            {
                if (dis.Until == null) state = "dismissed";
                else if (dis.Until > now) { state = "snoozed"; until = dis.Until; }
            }
            return new InboxOpen(itemId, kind, m, reason, probability, live, state, until);
        }

        // ── applying (one transaction) ───────────────────────────────────────────

        private (int Alerts, int Items, int Answered, int Queued) Apply(SqliteConnection c, SqliteTransaction tx, Decisions d, long now, long sourceSeq, List<object> events)
        {
            int alerts = 0, items = 0, answered = 0, queued = 0;
            long burst = BurstMs;
            var pushedMessages = new HashSet<string>(StringComparer.Ordinal);

            foreach (string person in d.Erased)
            {
                // Forgetting a person takes what was kept about them here with it; what refers to messages that no longer
                // exist is swept after the pass (PurgeGoneAsync).
                c.Exec(tx, "DELETE FROM sig_alert_hits WHERE alert_id IN (SELECT alert_id FROM sig_alerts WHERE person_id=$p)", ("$p", person));
                c.Exec(tx, "DELETE FROM sig_alerts WHERE person_id=$p", ("$p", person));
                c.Exec(tx, "DELETE FROM sig_inbox WHERE person_id=$p", ("$p", person));
            }

            foreach (var hit in d.Radar.OrderBy(h => h.Message.SentAt))
            {
                var m = hit.Message;
                if (c.Scalar<long>(tx, "SELECT COUNT(*) FROM sig_alert_hits WHERE kind='radar' AND rule_id='' AND message_id=$m", ("$m", m.Id)) > 0) continue;
                var (alertId, created, pushed, count) = AddHit(c, tx, "radar", "", null, hit.Reason, m, hit.Probability, hit.Labelset, burst, sourceSeq, now);
                if (created) alerts++;
                if (pushed)
                {
                    pushedMessages.Add(m.Id);
                    events.Add(new { kind = "radar", alertId, reason = hit.Reason, messageId = m.Id, conversationId = m.Conversation, guildId = m.Guild, personId = m.Person, count, grouped = !created });
                }
            }

            foreach (var o in d.Open.OrderBy(o => o.Message.SentAt))
            {
                var m = o.Message;
                var existing = c.Query(tx, "SELECT state, sent_at_ms FROM sig_inbox WHERE item_id=$i", r => (State: r.GetString(0), SentAt: r.GetInt64(1)), ("$i", o.ItemId)).FirstOrDefault();
                // The same message looked at again never reopens what was answered or put away; a newer message from them does.
                if (existing.State != null && m.SentAt <= existing.SentAt) continue;
                string state = o.State;
                // A question from them stands for the conversation: the plain "your turn" item steps aside while one is open.
                if (o.Kind == "unanswered" && state == "open"
                    && c.Scalar<long>(tx, "SELECT COUNT(*) FROM sig_inbox WHERE conversation_id=$c AND kind<>'unanswered' AND state IN ('open','snoozed')", ("$c", m.Conversation)) > 0)
                    state = "superseded";
                c.Exec(tx, """
                    INSERT INTO sig_inbox(item_id, kind, message_id, conversation_id, conversation_kind, guild_id, platform, person_id, sent_at_ms, state, reason, probability,
                        snoozed_until_ms, live, source_seq, created_at_ms, updated_at_ms, change_seq)
                    VALUES($i,$k,$m,$c,$ck,$g,$pl,$p,$t,$st,$r,$pr,$su,$live,$seq,$n,$n,$ch)
                    ON CONFLICT(item_id) DO UPDATE SET kind=excluded.kind, message_id=excluded.message_id, person_id=excluded.person_id, sent_at_ms=excluded.sent_at_ms,
                        state=excluded.state, reason=excluded.reason, probability=excluded.probability, snoozed_until_ms=excluded.snoozed_until_ms, live=excluded.live,
                        resolved_at_ms=NULL, resolved_by=NULL, nudged_at_ms=NULL, source_seq=excluded.source_seq, updated_at_ms=excluded.updated_at_ms, change_seq=excluded.change_seq
                    """, ("$i", o.ItemId), ("$k", o.Kind), ("$m", m.Id), ("$c", m.Conversation), ("$ck", m.ConversationKind), ("$g", m.Guild), ("$pl", m.Platform),
                    ("$p", m.Person ?? m.AuthorIdentity), ("$t", m.SentAt), ("$st", state), ("$r", o.Reason), ("$pr", o.Probability), ("$su", o.SnoozedUntil), ("$live", o.Live ? 1 : 0),
                    ("$seq", sourceSeq), ("$n", now), ("$ch", NextChange(c, tx)));
                if (o.Kind != "unanswered" && state is "open" or "snoozed")
                    c.Exec(tx, "UPDATE sig_inbox SET state='superseded', updated_at_ms=$n, change_seq=$ch WHERE item_id=$i AND state IN ('open','snoozed')",
                        ("$n", now), ("$ch", NextChange(c, tx)), ("$i", "tail|" + m.Conversation));
                // One event when something starts waiting, not one per further message of the same wait; none when the
                // radar already pushed this very message.
                if (state == "open" && o.Live && existing.State != "open" && !pushedMessages.Contains(m.Id))
                    events.Add(new { kind = "inbox", itemId = o.ItemId, itemKind = o.Kind, reason = o.Reason, messageId = m.Id, conversationId = m.Conversation, guildId = m.Guild, personId = m.Person });
                items++;
            }

            long window = Context.Settings.Get("signals.inbox.channel_answer_minutes", 120) * 60_000L;
            foreach (var a in d.Answers.OrderBy(a => a.At))
            {
                int n = c.Exec(tx, """
                    UPDATE sig_inbox SET state='answered', resolved_at_ms=$t, resolved_by=$m, updated_at_ms=$n, change_seq=(SELECT value + 1 FROM sig_state WHERE name='change_seq')
                    WHERE conversation_id=$c AND state IN ('open','snoozed','superseded') AND sent_at_ms < $t AND ($chat = 1 OR message_id = $reply OR sent_at_ms >= $t - $w)
                    """, ("$t", a.At), ("$m", a.ByMessage), ("$n", now), ("$c", a.Conversation), ("$chat", a.Chat ? 1 : 0), ("$reply", a.RepliesTo ?? ""), ("$w", window));
                if (n > 0) NextChange(c, tx);
                answered += n;
            }

            foreach (var (messageId, at) in d.Deleted)
            {
                // The alert stays (it happened), marked as deleted at the source; nothing still waits for an answer to a message that is gone.
                int n = c.Exec(tx, "UPDATE sig_alerts SET source_deleted_at_ms=$t, updated_at_ms=$n, change_seq=(SELECT value + 1 FROM sig_state WHERE name='change_seq') WHERE message_id=$m AND source_deleted_at_ms IS NULL",
                    ("$t", at), ("$n", now), ("$m", messageId));
                if (n > 0) NextChange(c, tx);
                int gone = c.Exec(tx, "UPDATE sig_inbox SET state='deleted', updated_at_ms=$n, change_seq=(SELECT value + 1 FROM sig_state WHERE name='change_seq') WHERE message_id=$m AND state IN ('open','snoozed','superseded')",
                    ("$n", now), ("$m", messageId));
                if (gone > 0) NextChange(c, tx);
                c.Exec(tx, "DELETE FROM sig_watch_candidates WHERE message_id=$m", ("$m", messageId));
                answered += gone;
            }

            // A name match that was decided (either way) no longer waits; one that still has no reading waits until its time.
            var waiting = d.Pending.Select(p => p.MessageId).ToHashSet(StringComparer.Ordinal);
            foreach (string id in d.Looked.Where(id => !waiting.Contains(id))) c.Exec(tx, "DELETE FROM sig_pending WHERE message_id=$m", ("$m", id));
            foreach (var (messageId, dueMs) in d.Pending)
                c.Exec(tx, "INSERT OR IGNORE INTO sig_pending(message_id, due_ms) VALUES($m,$d)", ("$m", messageId), ("$d", dueMs));

            foreach (var cand in d.Candidates)
            {
                if (c.Scalar<long>(tx, "SELECT COUNT(*) FROM sig_watch_judgements WHERE watch_id=$w AND message_id=$m AND source='model'", ("$w", cand.WatchId), ("$m", cand.MessageId)) > 0) continue;
                queued += c.Exec(tx, "INSERT OR IGNORE INTO sig_watch_candidates(watch_id, message_id, similarity, queued_at_ms) VALUES($w,$m,$s,$n)",
                    ("$w", cand.WatchId), ("$m", cand.MessageId), ("$s", cand.Similarity), ("$n", now));
            }
            return (alerts, items, answered, queued);
        }

        /// <summary>
        /// Records one hit of a rule (the radar is the rule with the empty id): it joins the open alert of the same burst when
        /// there is one, otherwise it starts a new alert. Returns whether an alert was created and whether to push.
        /// </summary>
        private (string AlertId, bool Created, bool Pushed, int Count) AddHit(SqliteConnection c, SqliteTransaction tx, string kind, string ruleId, string? ruleName, string reason, Msg m,
            double? probability, int? labelset, long burst, long sourceSeq, long now)
        {
            var entry = new { messageId = m.Id, conversationId = m.Conversation, atMs = m.SentAt, reason, probability, personId = m.Person };
            var open = c.Query(tx, """
                SELECT alert_id, hits_json, hit_count, delivery FROM sig_alerts
                WHERE kind=$k AND COALESCE(rule_id,'')=$rule AND group_key=$g AND state='new' AND last_hit_ms >= $a AND last_hit_ms <= $b ORDER BY last_hit_ms DESC LIMIT 1
                """, r => (Id: r.GetString(0), Hits: r.GetString(1), Count: r.GetInt32(2), Delivery: r.GetString(3)),
                ("$k", kind), ("$rule", ruleId), ("$g", m.GroupKey), ("$a", m.SentAt - burst), ("$b", m.SentAt + burst)).FirstOrDefault();
            string alertId;
            bool created = open.Id == null, pushed;
            int count;
            if (!created)
            {
                alertId = open.Id!;
                count = open.Count + 1;
                var hits = JsonSerializer.Deserialize<List<JsonElement>>(open.Hits, KernelJson.Options) ?? new List<JsonElement>();
                if (hits.Count < MaxHitsKept) hits.Add(JsonSerializer.SerializeToElement(entry, KernelJson.Options));
                c.Exec(tx, "UPDATE sig_alerts SET hit_count=hit_count+1, last_hit_ms=MAX(last_hit_ms,$t), hits_json=$h, updated_at_ms=$n, change_seq=$s WHERE alert_id=$id",
                    ("$t", m.SentAt), ("$h", JsonSerializer.Serialize(hits, KernelJson.Options)), ("$n", now), ("$s", NextChange(c, tx)), ("$id", alertId));
                pushed = open.Delivery == "pushed";
            }
            else
            {
                alertId = kind + "|" + (ruleId.Length == 0 ? "" : ruleId + "|") + m.Id;
                count = 1;
                string delivery = DeliveryFor(c, tx, now);
                c.Exec(tx, """
                    INSERT OR IGNORE INTO sig_alerts(alert_id, kind, rule_id, rule_name, reason, message_id, conversation_id, group_key, guild_id, platform, person_id, occurred_at_ms,
                        last_hit_ms, hit_count, hits_json, probability, state, delivery, labelset, source_seq, created_at_ms, updated_at_ms, change_seq)
                    VALUES($id,$k,$rule,$name,$r,$m,$c,$gk,$g,$pl,$p,$t,$t,1,$h,$pr,'new',$d,$ls,$seq,$n,$n,$ch)
                    """, ("$id", alertId), ("$k", kind), ("$rule", ruleId.Length == 0 ? null : ruleId), ("$name", ruleName), ("$r", reason), ("$m", m.Id), ("$c", m.Conversation),
                    ("$gk", m.GroupKey), ("$g", m.Guild), ("$pl", m.Platform), ("$p", m.Person), ("$t", m.SentAt), ("$h", JsonSerializer.Serialize(new[] { entry }, KernelJson.Options)),
                    ("$pr", probability), ("$d", delivery), ("$ls", labelset), ("$seq", sourceSeq), ("$n", now), ("$ch", NextChange(c, tx)));
                pushed = delivery == "pushed";
            }
            c.Exec(tx, "INSERT OR IGNORE INTO sig_alert_hits(kind, rule_id, message_id, alert_id) VALUES($k,$rule,$m,$a)", ("$k", kind), ("$rule", ruleId), ("$m", m.Id), ("$a", alertId));
            return (alertId, created, pushed, count);
        }

        /// <summary>Time passing: unanswered messages past the window go stale, snoozes run out, long waits are nudged once (X49),
        /// and what waited too long for the model is dropped.</summary>
        private int Housekeep(SqliteConnection c, SqliteTransaction tx, long now, ScanState s, List<object> events)
        {
            int n = c.Exec(tx, "UPDATE sig_inbox SET state='stale', updated_at_ms=$n, change_seq=(SELECT value + 1 FROM sig_state WHERE name='change_seq') WHERE state IN ('open','snoozed','superseded') AND sent_at_ms < $t",
                ("$n", now), ("$t", now - s.StaleMs));
            if (n > 0) NextChange(c, tx);
            int woke = c.Exec(tx, "UPDATE sig_inbox SET state='open', snoozed_until_ms=NULL, updated_at_ms=$n, change_seq=(SELECT value + 1 FROM sig_state WHERE name='change_seq') WHERE state='snoozed' AND snoozed_until_ms <= $n",
                ("$n", now));
            if (woke > 0) NextChange(c, tx);
            n += woke;

            double nudgeHours = Context.Settings.Get("signals.inbox.nudge_hours", 24.0);
            if (nudgeHours > 0 && !Quiet(now))
            {
                var waiting = c.Query(tx, """
                    SELECT item_id, kind, message_id, conversation_id, guild_id, person_id, sent_at_ms FROM sig_inbox
                    WHERE state='open' AND live=1 AND nudged_at_ms IS NULL AND sent_at_ms <= $t ORDER BY sent_at_ms LIMIT 20
                    """, r => (Id: r.GetString(0), Kind: r.GetString(1), Message: r.GetString(2), Conversation: r.GetString(3), Guild: r.GetStringOrNull(4), Person: r.GetString(5), SentAt: r.GetInt64(6)),
                    ("$t", now - (long)(nudgeHours * 3_600_000)));
                foreach (var w in waiting)
                {
                    c.Exec(tx, "UPDATE sig_inbox SET nudged_at_ms=$n, updated_at_ms=$n, change_seq=$ch WHERE item_id=$i", ("$n", now), ("$ch", NextChange(c, tx)), ("$i", w.Id));
                    events.Add(new { kind = "nudge", itemId = w.Id, itemKind = w.Kind, messageId = w.Message, conversationId = w.Conversation, guildId = w.Guild, personId = w.Person, waitingMs = now - w.SentAt });
                }
                n += waiting.Count;
            }
            // A message that waited for the model past the freshness window is no longer worth an alert.
            c.Exec(tx, "DELETE FROM sig_watch_candidates WHERE queued_at_ms < $t", ("$t", now - s.FreshnessMs));
            c.Exec(tx, "DELETE FROM sig_pending WHERE due_ms < $t", ("$t", now - s.FreshnessMs));
            return n;
        }

        /// <summary>
        /// After a person was forgotten: removes everything here that refers to a message the corpus no longer has (alerts,
        /// hits inside grouped alerts, inbox items, watch judgements and candidates, and Klives's verdicts on them).
        /// </summary>
        private async Task PurgeGoneAsync(CancellationToken ct)
        {
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            List<(string Id, string Message, string Hits)> alerts;
            using (var c = Context.Stores.Intel.OpenRead())
            {
                alerts = c.Query(null, "SELECT alert_id, message_id, hits_json FROM sig_alerts", r => (r.GetString(0), r.GetString(1), r.GetString(2)));
                foreach (string table in new[] { "sig_alert_hits", "sig_inbox", "sig_watch_judgements", "sig_watch_candidates", "sig_pending" })
                    referenced.UnionWith(c.Query(null, $"SELECT DISTINCT message_id FROM {table}", r => r.GetString(0)));
            }
            referenced.UnionWith(alerts.Select(a => a.Message));
            List<(long Id, string? Message)> gold;
            using (var op = Context.Stores.Operator.OpenRead())
                gold = op.Query(null, "SELECT label_id, natural_key FROM gold_labels WHERE kind=$k", r => (r.GetInt64(0), GoldMessage(r.GetString(1))), ("$k", GoldKind));
            referenced.UnionWith(gold.Where(g => g.Message != null).Select(g => g.Message!));
            if (referenced.Count == 0) return;
            HashSet<string> exists;
            using (var corpus = Context.Stores.Corpus.OpenRead())
                exists = corpus.QueryIn(null, "SELECT message_id FROM messages WHERE message_id IN ({keys})", referenced, r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);
            var gone = referenced.Where(id => !exists.Contains(id)).ToList();
            if (gone.Count == 0) return;
            var goneSet = gone.ToHashSet(StringComparer.Ordinal);
            long now = Context.Clock.NowMs;
            await Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                foreach (var a in alerts)
                {
                    if (goneSet.Contains(a.Message))
                    {
                        c.Exec(tx, "DELETE FROM sig_alerts WHERE alert_id=$a", ("$a", a.Id));
                        c.Exec(tx, "DELETE FROM sig_alert_hits WHERE alert_id=$a", ("$a", a.Id));
                        continue;
                    }
                    var hits = JsonSerializer.Deserialize<List<JsonElement>>(a.Hits, KernelJson.Options) ?? new List<JsonElement>();
                    var kept = hits.Where(h => !(h.TryGetProperty("messageId", out var id) && id.GetString() is { } mid && goneSet.Contains(mid))).ToList();
                    if (kept.Count == hits.Count) continue;
                    c.Exec(tx, "UPDATE sig_alerts SET hits_json=$h, hit_count=MAX(1, hit_count - $d), updated_at_ms=$n, change_seq=$ch WHERE alert_id=$a",
                        ("$h", JsonSerializer.Serialize(kept, KernelJson.Options)), ("$d", hits.Count - kept.Count), ("$n", now), ("$ch", NextChange(c, tx)), ("$a", a.Id));
                }
                foreach (string id in gone)
                    foreach (string table in new[] { "sig_alert_hits", "sig_inbox", "sig_watch_judgements", "sig_watch_candidates", "sig_pending" })
                        c.Exec(tx, $"DELETE FROM {table} WHERE message_id=$m", ("$m", id));
            }, ct).ConfigureAwait(false);
            var goldGone = gold.Where(g => g.Message != null && goneSet.Contains(g.Message)).Select(g => g.Id).ToList();
            if (goldGone.Count > 0)
                await Context.Stores.Operator.WriteAsync((c, tx) =>
                {
                    foreach (long id in goldGone) c.Exec(tx, "DELETE FROM gold_labels WHERE label_id=$i", ("$i", id));
                }, ct).ConfigureAwait(false);
        }

        private async Task WriteSummaryAsync(long now, long sourceSeq, CancellationToken ct)
        {
            object body;
            using (var c = Context.Stores.Intel.OpenRead())
            {
                var unseen = c.Query(null, "SELECT kind, COUNT(*), SUM(hit_count), MAX(created_at_ms) FROM sig_alerts WHERE state='new' GROUP BY kind ORDER BY kind",
                    r => new { kind = r.GetString(0), alerts = r.GetInt64(1), messages = r.GetInt64(2), newestMs = r.GetInt64OrNull(3) });
                var waiting = c.Query(null, "SELECT kind, COUNT(*), MIN(sent_at_ms) FROM sig_inbox WHERE state='open' GROUP BY kind ORDER BY kind",
                    r => new { kind = r.GetString(0), items = r.GetInt64(1), oldestMs = r.GetInt64OrNull(2) });
                body = new
                {
                    unseenAlerts = unseen, unseenTotal = unseen.Sum(u => u.alerts), waiting, waitingTotal = waiting.Sum(w => w.items),
                    heldForDigest = c.Scalar<long>(null, "SELECT COUNT(*) FROM sig_alerts WHERE state='new' AND delivery IN ('quiet','digest')"),
                };
            }
            await Context.ReadModels.PutAsync(SummaryModel, "klives", 1, now, sourceSeq, body, ct).ConfigureAwait(false);
        }
    }
}
