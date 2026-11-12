using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Modules.Rollups;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Modules.Signalling
{
    [Api.ApiContract]
    public enum AlertState { New, Seen, Dismissed }

    [Api.ApiContract]
    public enum AlertVerdict { Correct, Wrong }

    [Api.ApiContract]
    public enum InboxAction { Dismiss, Snooze, Reopen }

    /// <summary>One message of an alert. The text is read from the corpus when the alert is shown, never stored with it (F387).</summary>
    [Api.ApiContract]
    public sealed record AlertHitDto(string MessageId, string ConversationId, long AtMs, string Reason, double? Probability, string? PersonId, string? Excerpt, bool Deleted);

    /// <summary>
    /// An alert: <c>kind</c> radar or watch; <c>reason</c> mention, alias, named, indirect or watch; <c>state</c> new, seen or
    /// dismissed; <c>delivery</c> pushed, quiet (held by quiet hours) or digest (held because the hour was full).
    /// </summary>
    [Api.ApiContract]
    public sealed record AlertDto(string AlertId, string Kind, string? RuleId, string? RuleName, string Reason, string MessageId, string ConversationId, string? ConversationTitle,
        string? GuildId, string Platform, string? PersonId, string? PersonName, long OccurredAtMs, long LastHitMs, int HitCount, IReadOnlyList<AlertHitDto> Hits, double? Probability,
        string State, string Delivery, long? SourceDeletedAtMs, long CreatedAtMs, string? Verdict);

    [Api.ApiContract]
    public sealed record AlertPageDto(IReadOnlyList<AlertDto> Items, string? NextCursor, long Unseen);

    [Api.ApiContract]
    public sealed record AlertFeedbackDto(string AlertId, string Kind, string Reason, string MessageId, AlertVerdict Verdict);

    /// <summary>
    /// Something waiting for Klives's answer: <c>kind</c> question, request, or unanswered (a direct chat that ends on their
    /// side); <c>reason</c> dm, reply, mention or addressed. <c>rank</c> orders the list: how much he talks with the person,
    /// times how long it has waited.
    /// </summary>
    [Api.ApiContract]
    public sealed record InboxItemDto(string ItemId, string Kind, string MessageId, string ConversationId, string? ConversationKind, string? ConversationTitle, string? GuildId,
        string Platform, string PersonId, string? PersonName, long SentAtMs, long WaitingMs, string State, string Reason, double? Probability, long? SnoozedUntilMs, bool Live,
        string? Excerpt, double Strength, double Rank);

    [Api.ApiContract]
    public sealed record InboxPageDto(IReadOnlyList<InboxItemDto> Items, long Open, long AsOfMs);

    public sealed partial class SignalsModule
    {
        private const int ExcerptLength = 280;

        // ── gold keys (Klives's verdicts on alerts, operator.db) ─────────────────

        /// <summary>The natural key of a verdict: the message and the rule it was judged under ("radar" for the radar). Plan R3:
        /// gold refers to natural keys, never to rows of the derived store.</summary>
        internal static string GoldKey(string? ruleId, string messageId) =>
            JsonSerializer.Serialize(new SortedDictionary<string, string>(StringComparer.Ordinal) { ["message"] = messageId, ["rule"] = string.IsNullOrEmpty(ruleId) ? "radar" : ruleId });

        private static string? GoldField(string naturalKey, string field)
        {
            try
            {
                using var doc = JsonDocument.Parse(naturalKey);
                return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            }
            catch (JsonException)
            {
                // A key written by something else under this kind is not ours to read.
                return null;
            }
        }

        internal static string? GoldMessage(string naturalKey) => GoldField(naturalKey, "message");

        internal static string? GoldRule(string naturalKey) => GoldField(naturalKey, "rule");

        // ── shared reads ─────────────────────────────────────────────────────────

        private static List<Msg> LoadMessages(SqliteConnection corpus, IReadOnlyCollection<string> ids, long notBeforeMs) =>
            corpus.QueryInWith("""
                SELECT m.message_id, m.conversation_id, m.guild_id, m.platform, m.author_identity_id, COALESCE(pc.canonical_id, i.person_id),
                       (m.is_bot=1 OR m.is_system=1 OR m.is_webhook=1 OR COALESCE(i.kind,'')='bot'), m.sent_at_ms, m.ingested_at_ms, m.first_source_kind, m.reply_to_message_id,
                       m.deleted_at_ms, cv.kind, m.content
                FROM messages m LEFT JOIN identities i ON i.identity_id=m.author_identity_id LEFT JOIN person_canonical pc ON pc.person_id=i.person_id
                LEFT JOIN conversations cv ON cv.conversation_id=m.conversation_id
                WHERE m.message_id IN ({keys}) AND m.sent_at_ms >= $cut
                """, ids, r => new Msg(r.GetString(0), r.GetString(1), r.GetStringOrNull(2), r.GetString(3), r.GetString(4), r.GetStringOrNull(5), r.GetInt64(6) == 1,
                    r.GetInt64(7), r.GetInt64(8), r.GetString(9), r.GetStringOrNull(10), r.GetInt64OrNull(11), r.GetStringOrNull(12), r.GetStringOrNull(13)),
                ("$cut", notBeforeMs));

        private static string? Excerpt(string? content)
        {
            if (string.IsNullOrWhiteSpace(content)) return null;
            string text = content.Trim();
            if (text.Length <= ExcerptLength) return text;
            int cut = ExcerptLength;
            if (char.IsHighSurrogate(text[cut - 1])) cut--;
            return text[..cut] + "…";
        }

        /// <summary>What a page needs from the corpus to show its rows: message texts, people's names, conversation titles.</summary>
        private (Dictionary<string, (string? Content, long? DeletedAt)> Messages, Dictionary<string, string?> People, Dictionary<string, string?> Conversations) Describe(
            IReadOnlyCollection<string> messageIds, IReadOnlyCollection<string> personIds, IReadOnlyCollection<string> conversationIds)
        {
            using var corpus = Context.Stores.Corpus.OpenRead();
            var messages = corpus.QueryIn(null, "SELECT message_id, content, deleted_at_ms FROM messages WHERE message_id IN ({keys})", messageIds,
                r => (r.GetString(0), r.GetStringOrNull(1), r.GetInt64OrNull(2))).ToDictionary(x => x.Item1, x => (x.Item2, x.Item3), StringComparer.Ordinal);
            var people = corpus.QueryIn(null, "SELECT person_id, display_name FROM persons WHERE person_id IN ({keys})", personIds, r => (r.GetString(0), r.GetStringOrNull(1)))
                .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
            var conversations = corpus.QueryIn(null, "SELECT conversation_id, title FROM conversations WHERE conversation_id IN ({keys})", conversationIds, r => (r.GetString(0), r.GetStringOrNull(1)))
                .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
            return (messages, people, conversations);
        }

        // ── alerts ───────────────────────────────────────────────────────────────

        private sealed record AlertRow(string Id, string Kind, string? RuleId, string? RuleName, string Reason, string MessageId, string ConversationId, string? GuildId, string Platform,
            string? PersonId, long OccurredAt, long LastHit, int HitCount, string HitsJson, double? Probability, string State, string Delivery, long? SourceDeletedAt, long CreatedAt);

        private sealed record StoredHit(string MessageId, string? ConversationId, long AtMs, string Reason, double? Probability, string? PersonId);

        public long UnseenAlerts()
        {
            using var c = Context.Stores.Intel.OpenRead();
            return c.Scalar<long>(null, "SELECT COUNT(*) FROM sig_alerts WHERE state='new'");
        }

        /// <summary>Alerts, newest first, keyset-paged. Texts, names and titles are read from the corpus as it is now.</summary>
        public AlertPageDto Alerts(AlertState? state, string? kind, string? ruleId, string? personId, string? cursor, int limit)
        {
            limit = Math.Clamp(limit, 1, 200);
            var where = new List<string>();
            var args = new List<(string, object?)>();
            if (state != null) { where.Add("state=$state"); args.Add(("$state", state.Value.ToString().ToLowerInvariant())); }
            if (kind != null) { where.Add("kind=$kind"); args.Add(("$kind", kind)); }
            if (ruleId != null) { where.Add("rule_id=$rule"); args.Add(("$rule", ruleId)); }
            if (personId != null) { where.Add("person_id=$person"); args.Add(("$person", Context.Policy.Canonical(personId))); }
            if (cursor != null)
            {
                int bar = cursor.IndexOf('|');
                if (bar <= 0 || !long.TryParse(cursor[..bar], NumberStyles.Integer, CultureInfo.InvariantCulture, out long at)) throw new ArgumentException("Malformed cursor.");
                where.Add("(created_at_ms < $cat OR (created_at_ms = $cat AND alert_id < $cid))");
                args.Add(("$cat", at));
                args.Add(("$cid", cursor[(bar + 1)..]));
            }
            args.Add(("$lim", limit + 1));
            List<AlertRow> rows;
            long unseen;
            using (var c = Context.Stores.Intel.OpenRead())
            {
                rows = c.Query(null, $"""
                    SELECT alert_id, kind, rule_id, rule_name, reason, message_id, conversation_id, guild_id, platform, person_id, occurred_at_ms, last_hit_ms, hit_count, hits_json,
                           probability, state, delivery, source_deleted_at_ms, created_at_ms
                    FROM sig_alerts {(where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where))} ORDER BY created_at_ms DESC, alert_id DESC LIMIT $lim
                    """, r => new AlertRow(r.GetString(0), r.GetString(1), r.GetStringOrNull(2), r.GetStringOrNull(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetStringOrNull(7),
                        r.GetString(8), r.GetStringOrNull(9), r.GetInt64(10), r.GetInt64(11), r.GetInt32(12), r.GetString(13), r.GetDoubleOrNull(14), r.GetString(15), r.GetString(16),
                        r.GetInt64OrNull(17), r.GetInt64(18)), args.ToArray());
                unseen = c.Scalar<long>(null, "SELECT COUNT(*) FROM sig_alerts WHERE state='new'");
            }
            var page = rows.Take(limit).ToList();
            var hits = page.ToDictionary(a => a.Id, a => JsonSerializer.Deserialize<List<StoredHit>>(a.HitsJson, KernelJson.Options) ?? new List<StoredHit>(), StringComparer.Ordinal);
            string? Person(string? id) => id == null ? null : Context.Policy.Canonical(id);
            var (messages, people, conversations) = Describe(
                hits.Values.SelectMany(h => h.Select(x => x.MessageId)).Concat(page.Select(a => a.MessageId)).Distinct(StringComparer.Ordinal).ToList(),
                page.Select(a => Person(a.PersonId)).OfType<string>().Distinct(StringComparer.Ordinal).ToList(),
                page.Select(a => a.ConversationId).Distinct(StringComparer.Ordinal).ToList());
            Dictionary<string, string> verdicts;
            using (var op = Context.Stores.Operator.OpenRead())
                verdicts = op.QueryInWith("SELECT natural_key, verdict FROM gold_labels WHERE kind=$k AND superseded_at_ms IS NULL AND natural_key IN ({keys})",
                        page.Select(a => GoldKey(a.RuleId, a.MessageId)).Distinct(StringComparer.Ordinal).ToList(), r => (r.GetString(0), r.GetString(1)), ("$k", GoldKind))
                    .GroupBy(x => x.Item1, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Item2, StringComparer.Ordinal);
            var items = page.Select(a =>
            {
                string? person = Person(a.PersonId);
                var hitDtos = hits[a.Id].Select(h =>
                {
                    messages.TryGetValue(h.MessageId, out var m);
                    return new AlertHitDto(h.MessageId, h.ConversationId ?? a.ConversationId, h.AtMs, h.Reason, h.Probability, Person(h.PersonId), Excerpt(m.Content), m.DeletedAt != null);
                }).ToList();
                return new AlertDto(a.Id, a.Kind, a.RuleId, a.RuleName, a.Reason, a.MessageId, a.ConversationId, conversations.GetValueOrDefault(a.ConversationId), a.GuildId, a.Platform,
                    person, person == null ? null : people.GetValueOrDefault(person), a.OccurredAt, a.LastHit, a.HitCount, hitDtos, a.Probability, a.State, a.Delivery, a.SourceDeletedAt,
                    a.CreatedAt, verdicts.GetValueOrDefault(GoldKey(a.RuleId, a.MessageId)));
            }).ToList();
            string? next = rows.Count > limit ? page[^1].CreatedAt.ToString(CultureInfo.InvariantCulture) + "|" + page[^1].Id : null;
            return new AlertPageDto(items, next, unseen);
        }

        /// <summary>Marks alerts seen, dismissed or new again: the given ones, or every unseen one (optionally of one kind) in a
        /// single call (F160: no request per alert).</summary>
        public async Task<int> SetAlertStateAsync(IReadOnlyList<string>? alertIds, bool allUnseen, string? kind, AlertState state, string actor, CancellationToken ct)
        {
            if (!allUnseen && alertIds is not { Count: > 0 }) throw new ArgumentException("Name the alerts to change, or ask for all unseen ones.");
            if (alertIds is { Count: > 1000 }) throw new ArgumentException("At most 1000 alerts per call.");
            long now = Context.Clock.NowMs;
            string value = state.ToString().ToLowerInvariant();
            int changed = await Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                int n = 0;
                if (allUnseen)
                {
                    n = c.Exec(tx, "UPDATE sig_alerts SET state=$s, updated_at_ms=$n, change_seq=(SELECT value + 1 FROM sig_state WHERE name='change_seq') WHERE state='new' AND state<>$s AND ($k IS NULL OR kind=$k)",
                        ("$s", value), ("$n", now), ("$k", kind));
                }
                else
                {
                    foreach (string id in alertIds!.Distinct(StringComparer.Ordinal))
                        n += c.Exec(tx, "UPDATE sig_alerts SET state=$s, updated_at_ms=$n, change_seq=(SELECT value + 1 FROM sig_state WHERE name='change_seq') WHERE alert_id=$a AND state<>$s",
                            ("$s", value), ("$n", now), ("$a", id));
                }
                if (n > 0) NextChange(c, tx);
                return n;
            }, ct).ConfigureAwait(false);
            if (changed > 0)
            {
                await Kernel.OperatorAudit.WriteAsync(Context.Stores.Operator, Context.Clock, actor, "alerts.state", null, new { state = value, changed, all = allUnseen, kind }, ct).ConfigureAwait(false);
                await RefreshSummaryAsync(ct).ConfigureAwait(false);
            }
            return changed;
        }

        /// <summary>
        /// Klives's verdict on an alert (or on one message of a grouped alert). It is kept as gold in operator.db; for a watch
        /// rule it becomes an example the model is shown from then on and a judgement the prefilter learns from (N11). A wrong
        /// alert is dismissed, a right one marked seen.
        /// </summary>
        public async Task<AlertFeedbackDto> FeedbackAsync(string alertId, string? messageId, AlertVerdict verdict, string? note, string actor, CancellationToken ct)
        {
            (string Kind, string? Rule, string Reason, string Message, string Hits) alert;
            using (var c = Context.Stores.Intel.OpenRead())
                alert = c.Query(null, "SELECT kind, rule_id, reason, message_id, hits_json FROM sig_alerts WHERE alert_id=$a",
                    r => (r.GetString(0), r.GetStringOrNull(1), r.GetString(2), r.GetString(3), r.GetString(4)), ("$a", alertId)).FirstOrDefault();
            if (alert.Kind == null) throw new KeyNotFoundException($"No alert '{alertId}'.");
            var hits = JsonSerializer.Deserialize<List<StoredHit>>(alert.Hits, KernelJson.Options) ?? new List<StoredHit>();
            string message = string.IsNullOrWhiteSpace(messageId) ? alert.Message : messageId.Trim();
            var hit = hits.FirstOrDefault(h => h.MessageId == message);
            if (hit == null && message != alert.Message) throw new ArgumentException($"Message '{message}' is not part of alert '{alertId}'.");
            long now = Context.Clock.NowMs;
            string key = GoldKey(alert.Rule, message), value = verdict == AlertVerdict.Correct ? "correct" : "wrong";
            if (note is { Length: > 1000 }) note = note[..1000];
            await Context.Stores.Operator.WriteAsync((c, tx) =>
            {
                c.Exec(tx, "UPDATE gold_labels SET superseded_at_ms=$n WHERE kind=$k AND natural_key=$key AND superseded_at_ms IS NULL", ("$n", now), ("$k", GoldKind), ("$key", key));
                c.Exec(tx, "INSERT INTO gold_labels(kind, natural_key, verdict, correction_json, note, decided_at_ms, actor) VALUES($k,$key,$v,$c,$note,$n,$a)",
                    ("$k", GoldKind), ("$key", key), ("$v", value), ("$c", JsonSerializer.Serialize(new { alertKind = alert.Kind, reason = hit?.Reason ?? alert.Reason }, KernelJson.Options)),
                    ("$note", string.IsNullOrWhiteSpace(note) ? null : note), ("$n", now), ("$a", actor));
            }, ct).ConfigureAwait(false);
            await Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                if (alert.Kind == "watch" && alert.Rule != null)
                {
                    double? similarity = c.Scalar<double?>(tx, "SELECT similarity FROM sig_watch_judgements WHERE watch_id=$w AND message_id=$m AND source='model'", ("$w", alert.Rule), ("$m", message));
                    c.Exec(tx, """
                        INSERT INTO sig_watch_judgements(watch_id, message_id, source, matched, confidence, similarity, at_ms) VALUES($w,$m,'klives',$y,1,$s,$n)
                        ON CONFLICT(watch_id, message_id, source) DO UPDATE SET matched=excluded.matched, similarity=excluded.similarity, at_ms=excluded.at_ms
                        """, ("$w", alert.Rule), ("$m", message), ("$y", verdict == AlertVerdict.Correct ? 1 : 0), ("$s", similarity), ("$n", now));
                }
                // The verdict on the alert's own message settles the alert; one on a later message of a burst only records the verdict.
                if (message == alert.Message)
                    c.Exec(tx, "UPDATE sig_alerts SET state=$s, updated_at_ms=$n, change_seq=$ch WHERE alert_id=$a AND state<>$s",
                        ("$s", verdict == AlertVerdict.Correct ? "seen" : "dismissed"), ("$n", now), ("$ch", NextChange(c, tx)), ("$a", alertId));
            }, ct).ConfigureAwait(false);
            await RefreshSummaryAsync(ct).ConfigureAwait(false);
            return new AlertFeedbackDto(alertId, alert.Kind, hit?.Reason ?? alert.Reason, message, verdict);
        }

        private async Task RefreshSummaryAsync(CancellationToken ct)
        {
            long seq;
            using (var c = Context.Stores.Intel.OpenRead()) seq = Cursor(c, "changes");
            await WriteSummaryAsync(Context.Clock.NowMs, seq, ct).ConfigureAwait(false);
        }

        // ── reply inbox ──────────────────────────────────────────────────────────

        private Dictionary<string, int>? strengths;
        private long strengthsAtMs = long.MinValue;

        /// <summary>How much Klives and each person interacted over the last half year (replies, mentions, direct messages,
        /// reactions, both ways), from the rollups; empty when this build has none. Refreshed every five minutes.</summary>
        private Dictionary<string, int> Strengths()
        {
            long now = Context.Clock.NowMs;
            lock (this)
            {
                // (MinValue marks "never read"; subtracting it would overflow into a "fresh" cache.)
                if (strengths != null && strengthsAtMs != long.MinValue && now - strengthsAtMs < 300_000) return strengths;
            }
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            string? self;
            using (var corpus = Context.Stores.Corpus.OpenRead())
                self = corpus.Scalar<string>(null, "SELECT COALESCE(pc.canonical_id, p.person_id) FROM persons p LEFT JOIN person_canonical pc ON pc.person_id=p.person_id WHERE p.is_self=1 ORDER BY p.person_id LIMIT 1");
            if (self != null)
            {
                using var intel = Context.Stores.Intel.OpenRead();
                if (intel.Scalar<long>(null, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='rollup_pair_day'") > 0)
                {
                    string from = Zones.LocalDate(now - 183 * 86_400_000L, Context.Settings.KlivesZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    foreach (var p in RollupReader.Pairs(intel, self, from)) result[p.Partner] = p.Interactions;
                }
            }
            lock (this)
            {
                strengths = result;
                strengthsAtMs = now;
            }
            return result;
        }

        /// <summary>
        /// The reply inbox. Open items are ranked by how much Klives talks with the person and how long the message has waited
        /// (a question or a request counts for more than a chat that merely ends on their side); other states are listed
        /// newest first.
        /// </summary>
        public InboxPageDto Inbox(string state, int limit)
        {
            limit = Math.Clamp(limit, 1, 500);
            state = string.IsNullOrWhiteSpace(state) ? "open" : state.Trim().ToLowerInvariant();
            if (state is not ("open" or "snoozed" or "answered" or "dismissed" or "stale" or "deleted" or "superseded"))
                throw new ArgumentException("'state' must be one of: open, snoozed, answered, dismissed, stale, deleted, superseded.");
            long now = Context.Clock.NowMs;
            List<(string Id, string Kind, string Message, string Conversation, string? ConversationKind, string? Guild, string Platform, string Person, long SentAt, string State, string Reason,
                double? Probability, long? Snoozed, bool Live)> rows;
            long open;
            using (var c = Context.Stores.Intel.OpenRead())
            {
                // Open items are ranked in memory, so the oldest 2,000 are read (the staleness cap keeps the open set small).
                rows = c.Query(null, $"""
                    SELECT item_id, kind, message_id, conversation_id, conversation_kind, guild_id, platform, person_id, sent_at_ms, state, reason, probability, snoozed_until_ms, live
                    FROM sig_inbox WHERE state=$s ORDER BY {(state == "open" ? "sent_at_ms ASC, item_id ASC" : "updated_at_ms DESC, item_id DESC")} LIMIT $l
                    """, r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetStringOrNull(4), r.GetStringOrNull(5), r.GetString(6), r.GetString(7), r.GetInt64(8),
                        r.GetString(9), r.GetString(10), r.GetDoubleOrNull(11), r.GetInt64OrNull(12), r.GetInt64(13) == 1), ("$s", state), ("$l", state == "open" ? 2_000 : limit));
                open = c.Scalar<long>(null, "SELECT COUNT(*) FROM sig_inbox WHERE state='open'");
            }
            var strength = Strengths();
            double top = strength.Count == 0 ? 0 : Math.Log(1 + strength.Values.Max());
            var ranked = rows.Select(r =>
            {
                string person = Context.Policy.Canonical(r.Person);
                double s = top <= 0 ? 0 : Math.Log(1 + strength.GetValueOrDefault(person)) / top;
                double hours = Math.Max(0, now - r.SentAt) / 3_600_000.0;
                double rank = (1 + 3 * s) * Math.Log(1 + hours) * (r.Kind == "unanswered" ? 1.0 : 1.5);
                return (Row: r, Person: person, Strength: s, Rank: rank);
            });
            var page = (state == "open" ? ranked.OrderByDescending(x => x.Rank).ThenBy(x => x.Row.SentAt).ThenBy(x => x.Row.Id, StringComparer.Ordinal) : ranked).Take(limit).ToList();
            var (messages, people, conversations) = Describe(page.Select(p => p.Row.Message).Distinct(StringComparer.Ordinal).ToList(),
                page.Select(p => p.Person).Distinct(StringComparer.Ordinal).ToList(), page.Select(p => p.Row.Conversation).Distinct(StringComparer.Ordinal).ToList());
            var items = page.Select(p =>
            {
                var r = p.Row;
                messages.TryGetValue(r.Message, out var m);
                return new InboxItemDto(r.Id, r.Kind, r.Message, r.Conversation, r.ConversationKind, conversations.GetValueOrDefault(r.Conversation), r.Guild, r.Platform, p.Person,
                    people.GetValueOrDefault(p.Person), r.SentAt, Math.Max(0, now - r.SentAt), r.State, r.Reason, r.Probability, r.Snoozed, r.Live, Excerpt(m.Content),
                    Math.Round(p.Strength, 3), Math.Round(p.Rank, 3));
            }).ToList();
            return new InboxPageDto(items, open, now);
        }

        /// <summary>
        /// Klives puts items away or takes them back. A dismissal or a snooze is his decision, so it is kept in operator.db
        /// and survives a rebuild of the derived store; a newer message from the same person opens the item again.
        /// </summary>
        public async Task<int> ResolveInboxAsync(IReadOnlyList<string> itemIds, InboxAction action, long? untilMs, string actor, CancellationToken ct)
        {
            if (itemIds is not { Count: > 0 }) throw new ArgumentException("Name the inbox items to change.");
            if (itemIds.Count > 1000) throw new ArgumentException("At most 1000 items per call.");
            long now = Context.Clock.NowMs;
            if (action == InboxAction.Snooze && (untilMs == null || untilMs <= now)) throw new ArgumentException("A snooze needs 'untilMs', a time in the future.");
            var ids = itemIds.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct(StringComparer.Ordinal).ToList();
            await Context.Stores.Operator.WriteAsync((c, tx) =>
            {
                foreach (string id in ids)
                {
                    if (action == InboxAction.Reopen) c.Exec(tx, "DELETE FROM dismissals WHERE kind=$k AND key=$i", ("$k", DismissalKind), ("$i", id));
                    else
                        c.Exec(tx, "INSERT INTO dismissals(kind, key, until_ms, at_ms) VALUES($k,$i,$u,$n) ON CONFLICT(kind, key) DO UPDATE SET until_ms=excluded.until_ms, at_ms=excluded.at_ms",
                            ("$k", DismissalKind), ("$i", id), ("$u", action == InboxAction.Snooze ? untilMs : null), ("$n", now));
                }
                c.Exec(tx, "INSERT INTO audit_log(at_ms, actor, action, target, detail_json) VALUES($t,$a,$x,NULL,$d)", ("$t", now), ("$a", actor),
                    ("$x", "inbox." + action.ToString().ToLowerInvariant()), ("$d", JsonSerializer.Serialize(new { items = ids.Count, untilMs }, KernelJson.Options)));
            }, ct).ConfigureAwait(false);
            int changed = await Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                int n = 0;
                foreach (string id in ids)
                {
                    n += action switch
                    {
                        InboxAction.Dismiss => c.Exec(tx, "UPDATE sig_inbox SET state='dismissed', snoozed_until_ms=NULL, resolved_at_ms=$n, updated_at_ms=$n, change_seq=$ch WHERE item_id=$i AND state IN ('open','snoozed')",
                            ("$n", now), ("$ch", NextChange(c, tx)), ("$i", id)),
                        InboxAction.Snooze => c.Exec(tx, "UPDATE sig_inbox SET state='snoozed', snoozed_until_ms=$u, updated_at_ms=$n, change_seq=$ch WHERE item_id=$i AND state IN ('open','snoozed')",
                            ("$u", untilMs), ("$n", now), ("$ch", NextChange(c, tx)), ("$i", id)),
                        _ => c.Exec(tx, "UPDATE sig_inbox SET state='open', snoozed_until_ms=NULL, resolved_at_ms=NULL, nudged_at_ms=NULL, updated_at_ms=$n, change_seq=$ch WHERE item_id=$i AND state IN ('dismissed','snoozed')",
                            ("$n", now), ("$ch", NextChange(c, tx)), ("$i", id)),
                    };
                }
                return n;
            }, ct).ConfigureAwait(false);
            if (changed > 0) await RefreshSummaryAsync(ct).ConfigureAwait(false);
            return changed;
        }
    }
}
