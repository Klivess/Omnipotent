using System.Globalization;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Events;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Knowledge.Policy;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Modules.Signalling
{
    /// <summary>
    /// A finding another module handed in: <c>kind</c> says what (relationship, wellbeing, sentiment, new_person, health,
    /// question …), <c>data</c> holds its ids and numbers. <c>severity</c> info, attention or urgent; <c>delivery</c> listed,
    /// pushed, quiet or digest.
    /// </summary>
    [Api.ApiContract]
    public sealed record NoticeDto(string NoticeId, string Kind, string ModuleId, string Severity, string? PersonId, string? PersonName, string? OtherPersonId, string? OtherPersonName,
        string? GuildId, string? ConversationId, long OccurredAtMs, JsonElement Data, string State, string Delivery, long CreatedAtMs);

    [Api.ApiContract]
    public sealed record NoticePageDto(IReadOnlyList<NoticeDto> Items, string? NextCursor, long Unseen);

    public sealed partial class SignalsModule : INoticeSink
    {
        public const string NoticesDataset = "signals.notices";
        /// <summary>A finding older than this when it is handed in is listed, not pushed (T5: nothing stale pages Klives).</summary>
        private const long NoticeFreshMs = 48 * 3_600_000L;

        private static readonly Migration NoticesMigration = new(ModuleId, 2, "notices from other modules", """
CREATE TABLE sig_notices(
  notice_id TEXT PRIMARY KEY,
  kind TEXT NOT NULL,
  module_id TEXT NOT NULL,
  severity TEXT NOT NULL,
  person_id TEXT,
  other_person_id TEXT,
  guild_id TEXT,
  conversation_id TEXT,
  occurred_at_ms INTEGER NOT NULL,
  data_json TEXT NOT NULL,
  state TEXT NOT NULL,
  delivery TEXT NOT NULL,
  created_at_ms INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL,
  change_seq INTEGER NOT NULL
);
CREATE INDEX ix_sig_notices_time ON sig_notices(created_at_ms DESC, notice_id DESC);
CREATE INDEX ix_sig_notices_state ON sig_notices(state, created_at_ms DESC);
CREATE INDEX ix_sig_notices_person ON sig_notices(person_id);
""");

        /// <summary>
        /// Records a notice once (the same id again changes nothing) and pushes it when its severity and the hour allow.
        /// A person-targeted notice about someone who is, or may be, a minor is not recorded at all.
        /// </summary>
        public async Task<bool> NoticeAsync(Notice notice, CancellationToken ct)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(notice.NoticeId);
            ArgumentException.ThrowIfNullOrWhiteSpace(notice.Kind);
            if (!NoticeSeverity.IsKnown(notice.Severity)) throw new ArgumentException($"Unknown notice severity '{notice.Severity}'.");
            long now = Context.Clock.NowMs;
            string? person = notice.PersonId == null ? null : Context.Policy.Canonical(notice.PersonId), other = notice.OtherPersonId == null ? null : Context.Policy.Canonical(notice.OtherPersonId);
            if (notice.PersonTargeted && new[] { person, other }.OfType<string>().Any(p => MinorGuard.IsRestricted(Context.Policy.BandOf(p, now)))) return false;
            string data = notice.Data switch
            {
                JsonElement e => e.GetRawText(),
                JsonDocument d => d.RootElement.GetRawText(),
                string s => JsonSerializer.Serialize(new { text = s }, KernelJson.Options),
                _ => JsonSerializer.Serialize(notice.Data, KernelJson.Options),
            };
            var (inserted, delivery) = await Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                if (c.Scalar<long>(tx, "SELECT COUNT(*) FROM sig_notices WHERE notice_id=$i", ("$i", notice.NoticeId)) > 0) return (false, "");
                string how = notice.Severity == NoticeSeverity.Info || now - notice.OccurredAtMs > NoticeFreshMs ? "listed"
                    : notice.Severity == NoticeSeverity.Urgent ? "pushed" : NoticeDelivery(c, tx, now);
                c.Exec(tx, """
                    INSERT INTO sig_notices(notice_id, kind, module_id, severity, person_id, other_person_id, guild_id, conversation_id, occurred_at_ms, data_json, state, delivery,
                        created_at_ms, updated_at_ms, change_seq)
                    VALUES($i,$k,$m,$s,$p,$o,$g,$c,$t,$d,'new',$how,$n,$n,$ch)
                    """, ("$i", notice.NoticeId), ("$k", notice.Kind), ("$m", notice.ModuleId), ("$s", notice.Severity), ("$p", person), ("$o", other), ("$g", notice.GuildId),
                    ("$c", notice.ConversationId), ("$t", notice.OccurredAtMs), ("$d", data), ("$how", how), ("$n", now), ("$ch", NextChange(c, tx)));
                return (true, how);
            }, ct).ConfigureAwait(false);
            if (!inserted) return false;
            if (delivery == "pushed")
                await Context.Events.PublishAsync(Topics.Alert, new
                {
                    kind = "notice", noticeId = notice.NoticeId, noticeKind = notice.Kind, severity = notice.Severity, personId = person, otherPersonId = other, guildId = notice.GuildId,
                    conversationId = notice.ConversationId, data = JsonDocument.Parse(data).RootElement,
                }, ct).ConfigureAwait(false);
            await RefreshSummaryAsync(ct).ConfigureAwait(false);
            return true;
        }

        /// <summary>Quiet hours and the hourly cap, counted over alerts and notices together.</summary>
        private string NoticeDelivery(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, long now)
        {
            if (Quiet(now)) return "quiet";
            long pushed = c.Scalar<long>(tx, "SELECT COUNT(*) FROM sig_alerts WHERE delivery='pushed' AND created_at_ms > $t", ("$t", now - 3_600_000L))
                          + c.Scalar<long>(tx, "SELECT COUNT(*) FROM sig_notices WHERE delivery='pushed' AND severity<>'urgent' AND created_at_ms > $t", ("$t", now - 3_600_000L));
            return pushed >= Context.Settings.Get("signals.max_pushed_per_hour", 6) ? "digest" : "pushed";
        }

        /// <summary>Notices, newest first, keyset-paged.</summary>
        public NoticePageDto Notices(AlertState? state, string? kind, string? personId, string? cursor, int limit)
        {
            limit = Math.Clamp(limit, 1, 200);
            var where = new List<string>();
            var args = new List<(string, object?)>();
            if (state != null) { where.Add("state=$state"); args.Add(("$state", state.Value.ToString().ToLowerInvariant())); }
            if (kind != null) { where.Add("kind=$kind"); args.Add(("$kind", kind)); }
            if (personId != null) { where.Add("(person_id=$person OR other_person_id=$person)"); args.Add(("$person", Context.Policy.Canonical(personId))); }
            if (cursor != null)
            {
                int bar = cursor.IndexOf('|');
                if (bar <= 0 || !long.TryParse(cursor[..bar], NumberStyles.Integer, CultureInfo.InvariantCulture, out long at)) throw new ArgumentException("Malformed cursor.");
                where.Add("(created_at_ms < $cat OR (created_at_ms = $cat AND notice_id < $cid))");
                args.Add(("$cat", at));
                args.Add(("$cid", cursor[(bar + 1)..]));
            }
            args.Add(("$lim", limit + 1));
            List<(string Id, string Kind, string Module, string Severity, string? Person, string? Other, string? Guild, string? Conversation, long At, string Data, string State, string Delivery, long Created)> rows;
            long unseen;
            using (var c = Context.Stores.Intel.OpenRead())
            {
                rows = c.Query(null, $"""
                    SELECT notice_id, kind, module_id, severity, person_id, other_person_id, guild_id, conversation_id, occurred_at_ms, data_json, state, delivery, created_at_ms
                    FROM sig_notices {(where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where))} ORDER BY created_at_ms DESC, notice_id DESC LIMIT $lim
                    """, r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetStringOrNull(4), r.GetStringOrNull(5), r.GetStringOrNull(6), r.GetStringOrNull(7), r.GetInt64(8),
                        r.GetString(9), r.GetString(10), r.GetString(11), r.GetInt64(12)), args.ToArray());
                unseen = c.Scalar<long>(null, "SELECT COUNT(*) FROM sig_notices WHERE state='new'");
            }
            var page = rows.Take(limit).ToList();
            long now = Context.Clock.NowMs;
            // A notice about someone who has since been marked as a minor is no longer shown.
            page = page.Where(n => !new[] { n.Person, n.Other }.OfType<string>().Any(p => MinorGuard.IsRestricted(Context.Policy.BandOf(p, now)))).ToList();
            var (_, people, _) = Describe(Array.Empty<string>(), page.SelectMany(n => new[] { n.Person, n.Other }).OfType<string>().Select(Context.Policy.Canonical).Distinct(StringComparer.Ordinal).ToList(),
                Array.Empty<string>());
            string? Name(string? p) => p == null ? null : people.GetValueOrDefault(Context.Policy.Canonical(p));
            var items = page.Select(n => new NoticeDto(n.Id, n.Kind, n.Module, n.Severity, n.Person, Name(n.Person), n.Other, Name(n.Other), n.Guild, n.Conversation, n.At,
                JsonDocument.Parse(n.Data).RootElement.Clone(), n.State, n.Delivery, n.Created)).ToList();
            string? next = rows.Count > limit ? rows[limit - 1].Created.ToString(CultureInfo.InvariantCulture) + "|" + rows[limit - 1].Id : null;
            return new NoticePageDto(items, next, unseen);
        }

        public async Task<int> SetNoticeStateAsync(IReadOnlyList<string>? noticeIds, bool allUnseen, string? kind, AlertState state, string actor, CancellationToken ct)
        {
            if (!allUnseen && noticeIds is not { Count: > 0 }) throw new ArgumentException("Name the notices to change, or ask for all unseen ones.");
            if (noticeIds is { Count: > 1000 }) throw new ArgumentException("At most 1000 notices per call.");
            long now = Context.Clock.NowMs;
            string value = state.ToString().ToLowerInvariant();
            int changed = await Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                int n = 0;
                if (allUnseen)
                    n = c.Exec(tx, "UPDATE sig_notices SET state=$s, updated_at_ms=$n, change_seq=(SELECT value + 1 FROM sig_state WHERE name='change_seq') WHERE state='new' AND state<>$s AND ($k IS NULL OR kind=$k)",
                        ("$s", value), ("$n", now), ("$k", kind));
                else
                    foreach (string id in noticeIds!.Distinct(StringComparer.Ordinal))
                        n += c.Exec(tx, "UPDATE sig_notices SET state=$s, updated_at_ms=$n, change_seq=(SELECT value + 1 FROM sig_state WHERE name='change_seq') WHERE notice_id=$i AND state<>$s",
                            ("$s", value), ("$n", now), ("$i", id));
                if (n > 0) NextChange(c, tx);
                return n;
            }, ct).ConfigureAwait(false);
            if (changed > 0)
            {
                await Kernel.OperatorAudit.WriteAsync(Context.Stores.Operator, Context.Clock, actor, "notices.state", null, new { state = value, changed, all = allUnseen, kind }, ct).ConfigureAwait(false);
                await RefreshSummaryAsync(ct).ConfigureAwait(false);
            }
            return changed;
        }

        public long UnseenNotices()
        {
            using var c = Context.Stores.Intel.OpenRead();
            return c.Scalar<long>(null, "SELECT COUNT(*) FROM sig_notices WHERE state='new'");
        }
    }
}
