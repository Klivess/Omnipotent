using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Modules.Classification;
using Omnipotent.Services.OmniscienceV3.Modules.Rollups;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Services.OmniscienceV3.Modules.Monitors
{
    public sealed partial class MonitorsModule
    {
        private const int ScanBatch = 2_000;

        private sealed record LiveMessage(string Id, string Author, string? Person, string Conversation, string? ConversationKind, string? Guild, long SentAt, long IngestedAt, bool Bot, long? DeletedAt);

        /// <summary>
        /// Looks at every message that arrived live since the last pass: person-activity watches (X42) and someone new writing
        /// to Klives directly (X46). Messages that are already old by the time they arrive raise nothing.
        /// </summary>
        private async Task<string?> ScanAsync(JobContext job)
        {
            var ct = job.Cancellation;
            long now = Context.Clock.NowMs, fresh = FreshMs;
            long cursor = State("changes"), maxSeq;
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                maxSeq = ChangeLog.MaxSeq(corpus);
                // The first pass starts at the freshness horizon: what came before it is history.
                if (cursor == 0) cursor = Math.Max(0, FirstChangeAtOrAfter(corpus, now - fresh, maxSeq) - 1);
            }
            var personRules = LoadRules<PersonRuleDefinition>(PersonRuleKind).Where(r => r.Enabled).ToList();
            bool newPeople = Context.Settings.Get("monitors.new_people.enabled", true);
            var rulesByPerson = personRules.SelectMany(r => r.Definition.Persons.Select(p => (Person: p, Rule: r))).GroupBy(x => x.Person, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Rule).ToList(), StringComparer.Ordinal);
            int looked = 0, fired = 0;
            HashSet<string> self;
            List<string> klivesDms;
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                self = ClassificationModule.SelfIdentities(corpus);
                klivesDms = self.Count == 0 ? new List<string>() : corpus.QueryIn(null, """
                    SELECT DISTINCT cm.conversation_id FROM conversation_members cm JOIN conversations cv ON cv.conversation_id=cm.conversation_id WHERE cv.kind='dm' AND cm.identity_id IN ({keys})
                    """, self.ToList(), r => r.GetString(0));
            }
            var dmSet = klivesDms.ToHashSet(StringComparer.Ordinal);
            var off = AnalysisOff.Load(Context.Stores);
            var identities = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            List<string> IdentitiesOf(SqliteConnection corpus, string person) =>
                identities.TryGetValue(person, out var known) ? known : identities[person] = RollupModule.IdentitiesOf(corpus, person);

            while (cursor < maxSeq)
            {
                ct.ThrowIfCancellationRequested();
                List<(long Seq, string Message)> changes;
                using (var corpus = Context.Stores.Corpus.OpenRead())
                    changes = corpus.Query(null, "SELECT seq, entity_id FROM change_log WHERE seq > $s AND seq <= $m AND kind='message.insert' AND source_kind='live' ORDER BY seq LIMIT $l",
                        r => (r.GetInt64(0), r.GetString(1)), ("$s", cursor), ("$m", maxSeq), ("$l", ScanBatch));
                long to = changes.Count < ScanBatch ? maxSeq : changes[^1].Seq;
                if (changes.Count > 0 && (rulesByPerson.Count > 0 || newPeople))
                {
                    List<LiveMessage> messages;
                    using (var corpus = Context.Stores.Corpus.OpenRead())
                        messages = corpus.QueryIn(null, """
                            SELECT m.message_id, m.author_identity_id, COALESCE(pc.canonical_id, i.person_id), m.conversation_id, cv.kind, m.guild_id, m.sent_at_ms, m.ingested_at_ms,
                                   (m.is_bot=1 OR m.is_system=1 OR m.is_webhook=1 OR COALESCE(i.kind,'')='bot'), m.deleted_at_ms
                            FROM messages m LEFT JOIN identities i ON i.identity_id=m.author_identity_id LEFT JOIN person_canonical pc ON pc.person_id=i.person_id
                            LEFT JOIN conversations cv ON cv.conversation_id=m.conversation_id WHERE m.message_id IN ({keys})
                            """, changes.Select(c => c.Message).Distinct(StringComparer.Ordinal).ToList(), r => new LiveMessage(r.GetString(0), r.GetString(1), r.GetStringOrNull(2), r.GetString(3),
                            r.GetStringOrNull(4), r.GetStringOrNull(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8) == 1, r.GetInt64OrNull(9)));
                    foreach (var m in messages.OrderBy(m => m.SentAt).ThenBy(m => m.Id, StringComparer.Ordinal))
                    {
                        looked++;
                        if (m.Bot || m.DeletedAt != null || m.Person == null || self.Contains(m.Author) || off.Author(m.Author)) continue;
                        if (now - m.SentAt > fresh || now - m.IngestedAt > fresh) continue;
                        if (rulesByPerson.TryGetValue(m.Person, out var rules))
                            foreach (var rule in rules) fired += await PersonRuleAsync(rule, m, IdentitiesOf, ct).ConfigureAwait(false);
                        if (newPeople && m.ConversationKind == "dm" && dmSet.Contains(m.Conversation)) fired += await NewDirectMessageAsync(m, self, klivesDms, IdentitiesOf, ct).ConfigureAwait(false);
                    }
                }
                await Context.Stores.Intel.WriteAsync((c, tx) => SetState(c, tx, "changes", to), ct).ConfigureAwait(false);
                cursor = to;
            }
            // Where the first pass started is remembered even when there was nothing new to read.
            if (cursor > 0 && State("changes") != cursor)
            {
                long at = cursor;
                await Context.Stores.Intel.WriteAsync((c, tx) => SetState(c, tx, "changes", at), ct).ConfigureAwait(false);
            }
            return Json(new { looked, fired, cursor });
        }

        /// <summary>X42: back after a silence of the watch's length, or posting in one of the watch's servers (once a day each).</summary>
        private async Task<int> PersonRuleAsync(Rule<PersonRuleDefinition> rule, LiveMessage m, Func<SqliteConnection, string, List<string>> identitiesOf, CancellationToken ct)
        {
            int fired = 0;
            var d = rule.Definition;
            if (d.AbsenceDays is { } days)
            {
                long? previous;
                using (var corpus = Context.Stores.Corpus.OpenRead())
                {
                    var ids = identitiesOf(corpus, m.Person!);
                    previous = ids.Count == 0 ? null : corpus.QueryInWith("SELECT MAX(sent_at_ms) FROM messages WHERE author_identity_id IN ({keys}) AND (sent_at_ms < $t OR (sent_at_ms = $t AND message_id < $m))",
                        ids, r => r.GetInt64OrNull(0), ("$t", m.SentAt), ("$m", m.Id)).Max();
                }
                if (previous == null || m.SentAt - previous.Value >= days * Day)
                {
                    double? gap = previous == null ? null : Math.Round((m.SentAt - previous.Value) / (double)Day, 1);
                    if (await FireAsync($"x42|{rule.Id}|returned|{m.Id}", "person_activity", rule.Id, m.Person, m.Guild, m.Conversation, m.SentAt,
                            new { trigger = "returned", ruleId = rule.Id, ruleName = rule.Name, messageId = m.Id, gapDays = gap, lastSeenMs = previous, absenceDays = days }, NoticeSeverity.Attention, true, ct)
                            .ConfigureAwait(false))
                        fired++;
                }
            }
            if (m.Guild != null && d.Guilds.Contains(m.Guild))
            {
                string day = KlivesDay(m.SentAt);
                if (await FireAsync($"x42|{rule.Id}|posted|{m.Person}|{m.Guild}|{day}", "person_activity", rule.Id, m.Person, m.Guild, m.Conversation, m.SentAt,
                        new { trigger = "posted", ruleId = rule.Id, ruleName = rule.Name, messageId = m.Id, day }, NoticeSeverity.Attention, true, ct).ConfigureAwait(false))
                    fired++;
            }
            return fired;
        }

        /// <summary>X46: someone writes to Klives directly for the first time (and he did not write to them first).</summary>
        private async Task<int> NewDirectMessageAsync(LiveMessage m, HashSet<string> self, List<string> klivesDms, Func<SqliteConnection, string, List<string>> identitiesOf, CancellationToken ct)
        {
            bool before, klivesFirst, knownElsewhere;
            long? firstSeen;
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                var ids = identitiesOf(corpus, m.Person!);
                if (ids.Count == 0) return 0;
                var (authors, authorArgs) = SqlHelpers.InList(ids, "$a");
                var (dms, dmArgs) = SqlHelpers.InList(klivesDms, "$d");
                var args = authorArgs.Concat(dmArgs).Append(("$t", (object?)m.SentAt)).Append(("$m", (object?)m.Id)).ToArray();
                before = corpus.Scalar<long>(null, $"SELECT COUNT(*) FROM messages WHERE author_identity_id IN ({authors}) AND conversation_id IN ({dms}) AND (sent_at_ms < $t OR (sent_at_ms = $t AND message_id < $m))", args) > 0;
                var (selfList, selfArgs) = SqlHelpers.InList(self.ToList(), "$s");
                klivesFirst = corpus.Scalar<long>(null, $"SELECT COUNT(*) FROM messages WHERE conversation_id=$c AND author_identity_id IN ({selfList}) AND sent_at_ms <= $t",
                    selfArgs.Append(("$c", (object?)m.Conversation)).Append(("$t", (object?)m.SentAt)).ToArray()) > 0;
                knownElsewhere = corpus.Scalar<long>(null, $"SELECT COUNT(*) FROM messages WHERE author_identity_id IN ({authors}) AND (sent_at_ms < $t OR (sent_at_ms = $t AND message_id < $m))",
                    authorArgs.Append(("$t", (object?)m.SentAt)).Append(("$m", (object?)m.Id)).ToArray()) > 0;
                firstSeen = corpus.QueryIn(null, "SELECT MIN(first_seen_ms) FROM identities WHERE identity_id IN ({keys})", ids, r => r.GetInt64OrNull(0)).Min();
            }
            if (before || klivesFirst) return 0;
            return await FireAsync($"x46|dm|{m.Person}", "new_person", null, m.Person, null, m.Conversation, m.SentAt,
                new { trigger = "first_direct_message", messageId = m.Id, knownElsewhere, firstSeenMs = firstSeen }, NoticeSeverity.Attention, true, ct).ConfigureAwait(false) ? 1 : 0;
        }
    }
}
