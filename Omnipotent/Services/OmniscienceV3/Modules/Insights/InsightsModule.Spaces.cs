using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Ml.Classification;
using Omnipotent.Services.OmniscienceV3.Modules.Classification;
using Omnipotent.Services.OmniscienceV3.Modules.Rollups;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Modules.Insights
{
    /// <summary>A member of a server or group chat in the window. <c>share</c>: their part of everything written there;
    /// <c>devotion</c>: the part of everything they wrote anywhere that they wrote there.</summary>
    [Api.ApiContract]
    public sealed record SpaceMemberDto(string PersonId, string? Name, long Messages, double Share, int ActiveDays, double? Devotion);

    /// <summary>Someone who writes much more there lately than before. <c>isNew</c>: they did not write there in the window
    /// before this one. <c>everywhere</c>: they became as much more active everywhere, so it is not about this place.</summary>
    [Api.ApiContract]
    public sealed record SpaceRisingDto(string PersonId, string? Name, long Recent, long Earlier, bool IsNew, bool Everywhere, string? FirstDay);

    [Api.ApiContract]
    public sealed record SpaceDayDto(string Day, int Messages, int People);

    /// <summary>How the messages read, as shares of the messages the ensemble answered, lately and in the window before.
    /// <c>conflict</c>: the share read as tense or hostile.</summary>
    [Api.ApiContract]
    public sealed record SpaceToneDto(Dictionary<string, double> Recent, Dictionary<string, double> Baseline, int RecentAnswered, int BaselineAnswered, double ConflictRecent, double ConflictBaseline);

    [Api.ApiContract]
    public sealed record SpaceTalkerDto(string PersonId, string? Name, int Messages);

    /// <summary>
    /// What is said there to or about Klives: how many messages, of which kind (addressed to him, naming him, about him
    /// without naming him), and the tone of exactly those messages (not the mood of the place).
    /// </summary>
    [Api.ApiContract]
    public sealed record SpaceAboutKlivesDto(int Messages, int PreviousMessages, Dictionary<string, int> Kinds, Dictionary<string, double> Sentiment, int SentimentAnswered, IReadOnlyList<SpaceTalkerDto> People);

    /// <summary><c>kind</c>: surge (far more messages than usual), conflict (many tense messages), joined or left (a wave of members).</summary>
    [Api.ApiContract]
    public sealed record SpaceEventDto(string Day, string Kind, double Value, double Baseline, string Text);

    [Api.ApiContract]
    public sealed record SpaceFactionMemberDto(string PersonId, string? Name, double Strength);

    /// <summary>A group inside the place whose members answer and mention each other more than the rest. Ids stay with the
    /// group that keeps most of its people from one night to the next.</summary>
    [Api.ApiContract]
    public sealed record SpaceFactionDto(string FactionId, int Size, double Weight, IReadOnlyList<SpaceFactionMemberDto> Members);

    [Api.ApiContract]
    public sealed record SpaceChannelDto(string ConversationId, string? Title, long Messages, int People);

    /// <summary>How much of the place Omniscience sees. Every figure on the page is about that part only.</summary>
    [Api.ApiContract]
    public sealed record SpaceCoverageDto(int ChannelsKnown, int ChannelsWithMessages, int ChannelsActive, int ChannelsFullyBackfilled, long? FirstMessageMs, long? LastMessageMs, string Note);

    /// <summary>
    /// A server or a group chat as a community (N13). <c>scopeKind</c>: guild or conversation. Days are Klives's days. Members
    /// who are or may be under 18, and members whose analysis is switched off, are not named (<c>peopleLeftOut</c> counts the former).
    /// </summary>
    [Api.ApiContract]
    public sealed record SpacePageDto(string ScopeKind, string ScopeId, string? Title, string? Platform, int WindowDays, string FromDay, string ToDay, long AsOfMs, long Messages,
        long PreviousMessages, int People, int PreviousPeople, IReadOnlyList<SpaceDayDto> Days, IReadOnlyList<SpaceMemberDto> Core, IReadOnlyList<SpaceRisingDto> Rising, SpaceToneDto Tone,
        SpaceAboutKlivesDto AboutKlives, IReadOnlyList<SpaceEventDto> Events, IReadOnlyList<SpaceFactionDto> Factions, double? FactionModularity, IReadOnlyList<SpaceChannelDto> Channels,
        SpaceCoverageDto Coverage, int PeopleLeftOut, string? LatestDigestId);

    [Api.ApiContract]
    public sealed record SpaceCardDto(string ScopeKind, string ScopeId, string? Title, string? Platform, long Messages, long PreviousMessages, int People, int AboutKlives, long? LastMessageMs,
        long ComputedAtMs);

    [Api.ApiContract]
    public sealed record SpaceListDto(IReadOnlyList<SpaceCardDto> Spaces, int WindowDays);

    public sealed partial class InsightsModule
    {
        public const string SpacesKind = "spaces";
        public const string SpaceKind = "space";

        private static readonly Migration SpacesMigration = new(ModuleId, 3, "community pages of servers and group chats", """
CREATE TABLE ins_spaces(
  scope_kind TEXT NOT NULL,
  scope_id TEXT NOT NULL,
  title TEXT,
  platform TEXT,
  messages INTEGER NOT NULL,
  previous_messages INTEGER NOT NULL,
  people INTEGER NOT NULL,
  about_klives INTEGER NOT NULL,
  last_message_ms INTEGER,
  window_days INTEGER NOT NULL,
  body_json TEXT NOT NULL,
  factions_json TEXT NOT NULL,
  computed_at_ms INTEGER NOT NULL,
  PRIMARY KEY(scope_kind, scope_id)
) WITHOUT ROWID;
INSERT INTO ins_state(name, value) VALUES('faction_seq', 0);
""");

        private static string Day(DateOnly d) => RollupModule.Day(d);

        /// <summary>Queues the page of one server or group chat now, whatever its size.</summary>
        public Task<long> RequestSpaceAsync(string scopeKind, string scopeId, CancellationToken ct)
        {
            scopeKind = (scopeKind ?? "").Trim().ToLowerInvariant();
            if (scopeKind is not ("guild" or "conversation")) throw new ArgumentException("'scopeKind' must be guild or conversation.");
            if (string.IsNullOrWhiteSpace(scopeId)) throw new ArgumentException("'scopeId' is required.");
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                bool exists = scopeKind == "guild"
                    ? corpus.Scalar<long>(null, "SELECT COUNT(*) FROM guilds WHERE guild_id=$i", ("$i", scopeId.Trim())) > 0
                    : corpus.Scalar<long>(null, "SELECT COUNT(*) FROM conversations WHERE conversation_id=$i AND kind<>'dm'", ("$i", scopeId.Trim())) > 0;
                if (!exists) throw new KeyNotFoundException(scopeKind == "guild" ? $"No server '{scopeId}'." : $"No group chat or channel '{scopeId}'.");
            }
            return Context.Jobs.EnqueueAsync(Job(SpaceKind, scopeKind, scopeId.Trim(), priority: 170), ct);
        }

        public SpaceListDto Spaces()
        {
            using var c = Context.Stores.Intel.OpenRead();
            var cards = c.Query(null, """
                SELECT scope_kind, scope_id, title, platform, messages, previous_messages, people, about_klives, last_message_ms, computed_at_ms FROM ins_spaces
                ORDER BY messages DESC, scope_kind, scope_id
                """, r => new SpaceCardDto(r.GetString(0), r.GetString(1), r.GetStringOrNull(2), r.GetStringOrNull(3), r.GetInt64(4), r.GetInt64(5), r.GetInt32(6), r.GetInt32(7), r.GetInt64OrNull(8),
                r.GetInt64(9)));
            return new SpaceListDto(cards, Context.Settings.Get("insights.space.window_days", 90));
        }

        /// <summary>The stored page, with anyone who has since become protected (a minor) or whose analysis was switched off
        /// taken out of its lists: the page is from last night, the rule is from now.</summary>
        public SpacePageDto? Space(string scopeKind, string scopeId)
        {
            string? body;
            using (var c = Context.Stores.Intel.OpenRead())
                body = c.Scalar<string>(null, "SELECT body_json FROM ins_spaces WHERE scope_kind=$k AND scope_id=$i", ("$k", (scopeKind ?? "").Trim().ToLowerInvariant()), ("$i", (scopeId ?? "").Trim()));
            if (body == null) return null;
            var page = JsonSerializer.Deserialize<SpacePageDto>(body, KernelJson.Options)!;
            long now = Context.Clock.NowMs;
            var off = Storage.AnalysisOff.Load(Context.Stores);
            var named = page.Core.Select(m => m.PersonId).Concat(page.Rising.Select(m => m.PersonId)).Concat(page.AboutKlives.People.Select(m => m.PersonId))
                .Concat(page.Factions.SelectMany(f => f.Members.Select(m => m.PersonId))).Distinct(StringComparer.Ordinal).ToList();
            var hidden = named.Where(p => off.Person(p) || Restricted(p, now)).ToHashSet(StringComparer.Ordinal);
            if (hidden.Count == 0) return page;
            return page with
            {
                Core = page.Core.Where(m => !hidden.Contains(m.PersonId)).ToList(),
                Rising = page.Rising.Where(m => !hidden.Contains(m.PersonId)).ToList(),
                AboutKlives = page.AboutKlives with { People = page.AboutKlives.People.Where(m => !hidden.Contains(m.PersonId)).ToList() },
                Factions = page.Factions.Select(f => f with { Members = f.Members.Where(m => !hidden.Contains(m.PersonId)).ToList() }).ToList(),
            };
        }

        // ── the nightly job ──────────────────────────────────────────────────────

        private async Task<string?> BuildSpacesAsync(JobContext job)
        {
            var ct = job.Cancellation;
            if (!HasTable("rollup_scope_day")) return Json(new { skipped = "this build has no rollups" });
            long now = Context.Clock.NowMs;
            int window = Context.Settings.Get("insights.space.window_days", 90), min = Context.Settings.Get("insights.space.min_messages", 200), max = Context.Settings.Get("insights.space.max_spaces", 150);
            var today = Zones.LocalDate(now, Context.Settings.KlivesZone);
            string to = Day(today), from = Day(today.AddDays(-(window - 1)));

            var targets = new List<(string Kind, string Id, long Messages)>();
            if (job.Job.Kind == SpaceKind)
            {
                targets.Add((job.Job.SubjectKind ?? "guild", job.Job.SubjectId ?? throw new JobFailureException(ErrorClass.Permanent, "Space job without a subject."), 0));
            }
            else
            {
                List<(string Id, long N)> conversations;
                using (var intel = Context.Stores.Intel.OpenRead())
                {
                    targets.AddRange(intel.Query(null, "SELECT key, SUM(msgs) FROM rollup_scope_day WHERE scope='guild' AND day >= $a AND day <= $b GROUP BY key HAVING SUM(msgs) >= $m",
                        r => ("guild", r.GetString(0), r.GetInt64(1)), ("$a", from), ("$b", to), ("$m", min)));
                    conversations = intel.Query(null, "SELECT key, SUM(msgs) FROM rollup_scope_day WHERE scope='conversation' AND day >= $a AND day <= $b GROUP BY key HAVING SUM(msgs) >= $m",
                        r => (r.GetString(0), r.GetInt64(1)), ("$a", from), ("$b", to), ("$m", min));
                }
                // Of the conversations, only group chats are places of their own (a server's channels belong to the server's page).
                using (var corpus = Context.Stores.Corpus.OpenRead())
                {
                    var groups = corpus.QueryIn(null, "SELECT conversation_id FROM conversations WHERE kind='group_dm' AND guild_id IS NULL AND conversation_id IN ({keys})",
                        conversations.Select(x => x.Id).ToList(), r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);
                    targets.AddRange(conversations.Where(x => groups.Contains(x.Id)).Select(x => ("conversation", x.Id, x.N)));
                }
                targets = targets.OrderByDescending(t => t.Messages).ThenBy(t => t.Id, StringComparer.Ordinal).Take(max).ToList();
            }

            // The rollups may still count someone who was just forgotten (they follow the same change, in their own time):
            // only people who exist are counted and named.
            HashSet<string> alive;
            using (var corpus = Context.Stores.Corpus.OpenRead())
                alive = corpus.Query(null, "SELECT person_id FROM persons", r => r.GetString(0)).ToHashSet(StringComparer.Ordinal);
            int built = 0;
            foreach (var (kind, id, _) in targets)
            {
                ct.ThrowIfCancellationRequested();
                await BuildSpaceAsync(kind, id, window, today, now, alive, ct).ConfigureAwait(false);
                built++;
                if (built % 10 == 0) await job.ReportProgressAsync(new { built, of = targets.Count }).ConfigureAwait(false);
            }
            if (job.Job.Kind == SpacesKind)
            {
                // A place that fell under the threshold, or is gone, loses its page.
                var keep = targets.Select(t => t.Kind + "|" + t.Id).ToHashSet(StringComparer.Ordinal);
                await Context.Stores.Intel.WriteAsync((c, tx) =>
                {
                    foreach (var (k, i) in c.Query(tx, "SELECT scope_kind, scope_id FROM ins_spaces", r => (r.GetString(0), r.GetString(1))).Where(x => !keep.Contains(x.Item1 + "|" + x.Item2)))
                        c.Exec(tx, "DELETE FROM ins_spaces WHERE scope_kind=$k AND scope_id=$i", ("$k", k), ("$i", i));
                }, ct).ConfigureAwait(false);
            }
            return Json(new { built });
        }

        private static double Median(List<double> sorted) => sorted.Count == 0 ? 0 : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;

        private static Dictionary<string, double> Shares(Dictionary<string, int>? counts)
        {
            int total = counts?.Values.Sum() ?? 0;
            return total == 0 ? new Dictionary<string, double>() : counts!.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value / (double)total, 4), StringComparer.Ordinal);
        }

        private async Task BuildSpaceAsync(string kind, string id, int window, DateOnly today, long now, HashSet<string> alive, CancellationToken ct)
        {
            string scope = kind == "guild" ? "guild" : "conversation";
            string to = Day(today), from = Day(today.AddDays(-(window - 1))), prevFrom = Day(today.AddDays(-(2 * window - 1)));
            int recentDays = Math.Max(7, Math.Min(30, window / 3)), earlierDays = Math.Max(1, window - recentDays);
            string recentFrom = Day(today.AddDays(-(recentDays - 1)));

            List<ScopeDayRow> rows;
            int? labelset;
            long? firstMs, lastMs;
            string? previousFactions;
            using (var intel = Context.Stores.Intel.OpenRead())
            {
                rows = RollupReader.ScopeDays(intel, scope, id, prevFrom, to);
                labelset = ClassificationReader.ActiveLabelset(intel);
                (firstMs, lastMs) = intel.Query(null, "SELECT MIN(first_ms), MAX(last_ms) FROM rollup_scope_day WHERE scope=$s AND key=$k", r => (r.GetInt64OrNull(0), r.GetInt64OrNull(1)), ("$s", scope), ("$k", id))
                    .FirstOrDefault();
                previousFactions = intel.Scalar<string>(null, "SELECT factions_json FROM ins_spaces WHERE scope_kind=$k AND scope_id=$i", ("$k", kind), ("$i", id));
            }
            var current = rows.Where(r => string.CompareOrdinal(r.Day, from) >= 0).ToList();
            var previous = rows.Where(r => string.CompareOrdinal(r.Day, from) < 0).ToList();

            // Who is not named: minors (MinorGuard). People whose analysis is switched off are not in the rollups at all.
            var off = Storage.AnalysisOff.Load(Context.Stores);
            var hiddenCache = new Dictionary<string, bool>(StringComparer.Ordinal);
            bool Hidden(string p) => hiddenCache.TryGetValue(p, out bool h) ? h : hiddenCache[p] = !alive.Contains(p) || off.Person(p) || Restricted(p, now);

            // ── activity and members ──
            long messages = current.Sum(r => (long)r.Msgs), previousMessages = previous.Sum(r => (long)r.Msgs);
            var total = new Dictionary<string, long>(StringComparer.Ordinal);
            var recent = new Dictionary<string, long>(StringComparer.Ordinal);
            var activeDays = new Dictionary<string, int>(StringComparer.Ordinal);
            var firstDay = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var r in current)
                foreach (var (p, n) in r.Persons.Where(x => alive.Contains(x.Key)))
                {
                    total[p] = total.GetValueOrDefault(p) + n;
                    activeDays[p] = activeDays.GetValueOrDefault(p) + 1;
                    if (string.CompareOrdinal(r.Day, recentFrom) >= 0) recent[p] = recent.GetValueOrDefault(p) + n;
                    if (!firstDay.ContainsKey(p)) firstDay[p] = r.Day;
                }
            var before = previous.SelectMany(r => r.Persons.Keys).Where(alive.Contains).ToHashSet(StringComparer.Ordinal);
            int leftOut = total.Keys.Count(Hidden);
            var byDay = current.ToDictionary(r => r.Day, StringComparer.Ordinal);
            var days = Enumerable.Range(0, window).Select(i => Day(today.AddDays(-(window - 1) + i)))
                .Select(d => byDay.TryGetValue(d, out var r) ? new SpaceDayDto(d, r.Msgs, r.People) : new SpaceDayDto(d, 0, 0)).ToList();

            var coreIds = total.Where(kv => !Hidden(kv.Key) && activeDays[kv.Key] >= Math.Min(3, window)).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(12)
                .Select(kv => kv.Key).ToList();
            double expectedFactor = recentDays / (double)earlierDays;
            var risingIds = total.Keys.Where(p => !Hidden(p)).Select(p => (Person: p, Recent: recent.GetValueOrDefault(p), Earlier: total[p] - recent.GetValueOrDefault(p)))
                .Where(x => x.Recent >= 15 && x.Recent >= 2 * Math.Max(1, x.Earlier * expectedFactor))
                .OrderByDescending(x => x.Recent - x.Earlier * expectedFactor).ThenBy(x => x.Person, StringComparer.Ordinal).Take(8).ToList();

            // Each person's own activity everywhere, for devotion and to tell "rising here" from "more active everywhere".
            var own = new Dictionary<string, (long All, long Recent)>(StringComparer.Ordinal);
            using (var intel = Context.Stores.Intel.OpenRead())
                foreach (var (p, all, rec) in intel.QueryInWith("""
                             SELECT person_id, SUM(msgs), SUM(CASE WHEN day >= $r THEN msgs ELSE 0 END) FROM rollup_person_day WHERE day >= $a AND day <= $b AND person_id IN ({keys}) GROUP BY person_id
                             """, coreIds.Concat(risingIds.Select(x => x.Person)).Distinct(StringComparer.Ordinal).ToList(), r => (r.GetString(0), r.GetInt64(1), r.GetInt64(2)),
                             ("$a", from), ("$b", to), ("$r", recentFrom)))
                    own[p] = (all, rec);
            var names = Names(coreIds.Concat(risingIds.Select(x => x.Person)).Concat(current.SelectMany(r => r.Klives?.Persons.Keys ?? Enumerable.Empty<string>())));
            var core = coreIds.Select(p => new SpaceMemberDto(p, names.GetValueOrDefault(p), total[p], messages == 0 ? 0 : Math.Round(total[p] / (double)messages, 4), activeDays[p],
                own.TryGetValue(p, out var o) && o.All > 0 ? Math.Round(Math.Min(1, total[p] / (double)o.All), 4) : null)).ToList();
            var rising = risingIds.Select(x =>
            {
                bool everywhere = false;
                if (own.TryGetValue(x.Person, out var o))
                {
                    double hereRatio = x.Recent / Math.Max(1.0, x.Earlier * expectedFactor), ownRatio = o.Recent / Math.Max(1.0, (o.All - o.Recent) * expectedFactor);
                    everywhere = before.Contains(x.Person) && ownRatio >= hereRatio / 1.5;
                }
                return new SpaceRisingDto(x.Person, names.GetValueOrDefault(x.Person), x.Recent, x.Earlier, !before.Contains(x.Person) && x.Earlier == 0, everywhere, firstDay.GetValueOrDefault(x.Person));
            }).ToList();

            // ── tone, and what is said about Klives ──
            var nowLabels = RollupReader.SumLabels(current.Select(r => (r.Labels, r.Labelset)), labelset);
            var thenLabels = RollupReader.SumLabels(previous.Select(r => (r.Labels, r.Labelset)), labelset);
            static double Conflict(Dictionary<string, Dictionary<string, int>> l) =>
                l.TryGetValue(ClassificationTasks.Conflict.Id, out var c) && c.Values.Sum() > 0 ? Math.Round((c.GetValueOrDefault("tension") + c.GetValueOrDefault("conflict")) / (double)c.Values.Sum(), 4) : 0;
            string sentiment = ClassificationTasks.Sentiment.Id;
            var tone = new SpaceToneDto(Shares(nowLabels.GetValueOrDefault(sentiment)), Shares(thenLabels.GetValueOrDefault(sentiment)), nowLabels.GetValueOrDefault(sentiment)?.Values.Sum() ?? 0,
                thenLabels.GetValueOrDefault(sentiment)?.Values.Sum() ?? 0, Conflict(nowLabels), Conflict(thenLabels));

            var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
            var aboutTone = new Dictionary<string, int>(StringComparer.Ordinal);
            var talkers = new Dictionary<string, int>(StringComparer.Ordinal);
            int about = 0;
            foreach (var k in current.Select(r => r.Klives).OfType<ScopeKlives>())
            {
                about += k.N;
                foreach (var (x, n) in k.Kinds) kinds[x] = kinds.GetValueOrDefault(x) + n;
                foreach (var (x, n) in k.Sentiment) aboutTone[x] = aboutTone.GetValueOrDefault(x) + n;
                foreach (var (x, n) in k.Persons) talkers[x] = talkers.GetValueOrDefault(x) + n;
            }
            var aboutKlives = new SpaceAboutKlivesDto(about, previous.Sum(r => r.Klives?.N ?? 0), kinds.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
                Shares(aboutTone), aboutTone.Values.Sum(), talkers.Where(kv => !Hidden(kv.Key)).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(8)
                    .Select(kv => new SpaceTalkerDto(kv.Key, names.GetValueOrDefault(kv.Key), kv.Value)).ToList());

            // ── notable days ──
            var events = new List<SpaceEventDto>();
            var series = Enumerable.Range(0, 2 * window).Select(i => today.AddDays(-(2 * window - 1) + i)).Select(d => (Day: Day(d), Row: rows.FirstOrDefault(r => r.Day == Day(d)))).ToList();
            double conflictBase = Conflict(thenLabels);
            for (int i = window; i < series.Count; i++)
            {
                var (day, row) = series[i];
                if (row == null) continue;
                var trailing = series.Skip(Math.Max(0, i - 28)).Take(Math.Min(28, i)).Select(s => (double)(s.Row?.Msgs ?? 0)).OrderBy(v => v).ToList();
                double median = Median(trailing);
                double mad = Median(trailing.Select(v => Math.Abs(v - median)).OrderBy(v => v).ToList());
                if (trailing.Count >= 14 && row.Msgs >= 30 && row.Msgs >= 3 * Math.Max(1, median) && (row.Msgs - median) / (1.4826 * Math.Max(mad, 1)) >= 4)
                    events.Add(new SpaceEventDto(day, "surge", row.Msgs, Math.Round(median, 1),
                        $"{row.Msgs} messages, about {Math.Round(row.Msgs / Math.Max(1, median)).ToString(CultureInfo.InvariantCulture)} times a usual day ({Math.Round(median).ToString(CultureInfo.InvariantCulture)})."));
                if (row.Labelset == labelset && row.Labels.TryGetValue(ClassificationTasks.Conflict.Id, out var c))
                {
                    int tense = c.GetValueOrDefault("tension") + c.GetValueOrDefault("conflict"), answered = c.Values.Sum();
                    double share = answered == 0 ? 0 : tense / (double)answered;
                    if (tense >= 10 && share >= 0.25 && share >= 3 * conflictBase)
                        events.Add(new SpaceEventDto(day, "conflict", tense, Math.Round(conflictBase, 4),
                            $"{tense} messages read as tense or hostile, {Math.Round(share * 100).ToString(CultureInfo.InvariantCulture)}% of what was read that day."));
                }
            }

            // ── channels, coverage, member waves, factions: from the archive ──
            string? title, platform;
            var channels = new List<SpaceChannelDto>();
            SpaceCoverageDto coverage;
            var edges = new Dictionary<(string A, string B), double>();
            long fromMs = Zones.DayStartUtcMs(RollupModule.ParseDay(from), Context.Settings.KlivesZone);
            using (var corpus = Context.Stores.Corpus.OpenRead())
            {
                List<(string Id, string? Title, bool HasMessages)> known;
                if (kind == "guild")
                {
                    (title, platform) = corpus.Query(null, "SELECT name, platform FROM guilds WHERE guild_id=$g", r => (r.GetStringOrNull(0), r.GetStringOrNull(1)), ("$g", id)).FirstOrDefault();
                    known = corpus.Query(null, "SELECT conversation_id, title, last_message_ms IS NOT NULL FROM conversations WHERE guild_id=$g", r => (r.GetString(0), r.GetStringOrNull(1), r.GetInt64(2) == 1), ("$g", id));
                    foreach (var (d, joined, left) in corpus.Query(null, """
                                 SELECT date(at_ms / 1000, 'unixepoch'), SUM(kind='join'), SUM(kind='leave') FROM member_events WHERE guild_id=$g AND at_ms >= $f GROUP BY 1
                                 """, r => (r.GetString(0), r.GetInt32(1), r.GetInt32(2)), ("$g", id), ("$f", fromMs)))
                    {
                        if (joined >= 5) events.Add(new SpaceEventDto(d, "joined", joined, 0, $"{joined} people joined."));
                        if (left >= 5) events.Add(new SpaceEventDto(d, "left", left, 0, $"{left} people left."));
                    }
                }
                else
                {
                    (title, platform) = corpus.Query(null, "SELECT title, platform FROM conversations WHERE conversation_id=$c", r => (r.GetStringOrNull(0), r.GetStringOrNull(1)), ("$c", id)).FirstOrDefault();
                    known = new List<(string, string?, bool)> { (id, title, lastMs != null) };
                }
                var knownIds = known.Select(k => k.Id).ToList();
                int backfilled = corpus.QueryIn(null, "SELECT DISTINCT conversation_id FROM ingest_cursors WHERE backward_complete=1 AND conversation_id IN ({keys})", knownIds, r => r.GetString(0)).Count;
                var active = new Dictionary<string, long>(StringComparer.Ordinal);
                if (kind == "guild")
                {
                    using var intel = Context.Stores.Intel.OpenRead();
                    foreach (var (c, n) in intel.QueryInWith("SELECT key, SUM(msgs) FROM rollup_scope_day WHERE scope='conversation' AND day >= $a AND day <= $b AND key IN ({keys}) GROUP BY key", knownIds,
                                 r => (r.GetString(0), r.GetInt64(1)), ("$a", from), ("$b", to)))
                        active[c] = n;
                    foreach (var (c, n) in active.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(10))
                        channels.Add(new SpaceChannelDto(c, known.First(k => k.Id == c).Title, n, RollupReader.ScopeDays(intel, "conversation", c, from, to).SelectMany(r => r.Persons.Keys).Distinct(StringComparer.Ordinal).Count()));
                }
                else if (messages > 0) active[id] = messages;
                int withMessages = known.Count(k => k.HasMessages);
                string note = kind == "guild"
                    ? $"Omniscience has messages from {withMessages} of the {known.Count} channels it knows of in this server; {backfilled} of them are read back to their first message. Everything here is about that part only."
                    : backfilled > 0 ? "This chat is read back to its first message." : "This chat is not read back to its first message yet; older messages may be missing.";
                coverage = new SpaceCoverageDto(known.Count, withMessages, active.Count, backfilled, firstMs, lastMs, note);

                // Who answers and mentions whom inside this place, in the window.
                string where = kind == "guild" ? "m.guild_id=$s" : "m.conversation_id=$s";
                const string Person = "COALESCE(pc{0}.canonical_id, i{0}.person_id)";
                string a = string.Format(CultureInfo.InvariantCulture, Person, 1), b = string.Format(CultureInfo.InvariantCulture, Person, 2);
                void Add(string? x, string? y, long n)
                {
                    if (x == null || y == null || x == y || Hidden(x) || Hidden(y)) return;
                    var key = Ordered(x, y);
                    edges[key] = edges.GetValueOrDefault(key) + n;
                }
                foreach (var (x, y, n) in corpus.Query(null, $"""
                             SELECT {a}, {b}, COUNT(*) FROM messages m JOIN messages t ON t.message_id=m.reply_to_message_id
                             JOIN identities i1 ON i1.identity_id=m.author_identity_id LEFT JOIN person_canonical pc1 ON pc1.person_id=i1.person_id
                             JOIN identities i2 ON i2.identity_id=t.author_identity_id LEFT JOIN person_canonical pc2 ON pc2.person_id=i2.person_id
                             WHERE {where} AND m.sent_at_ms >= $f AND m.is_bot=0 AND m.is_system=0 AND i1.kind<>'bot' AND i2.kind<>'bot' GROUP BY 1, 2
                             """, r => (r.GetStringOrNull(0), r.GetStringOrNull(1), r.GetInt64(2)), ("$s", id), ("$f", fromMs)))
                    Add(x, y, n);
                foreach (var (x, y, n) in corpus.Query(null, $"""
                             SELECT {a}, {b}, COUNT(*) FROM messages m JOIN message_mentions mm ON mm.message_id=m.message_id AND mm.target_kind='user'
                             JOIN identities i1 ON i1.identity_id=m.author_identity_id LEFT JOIN person_canonical pc1 ON pc1.person_id=i1.person_id
                             JOIN identities i2 ON i2.identity_id=mm.target_id LEFT JOIN person_canonical pc2 ON pc2.person_id=i2.person_id
                             WHERE {where} AND m.sent_at_ms >= $f AND m.is_bot=0 AND m.is_system=0 AND i1.kind<>'bot' AND i2.kind<>'bot' GROUP BY 1, 2
                             """, r => (r.GetStringOrNull(0), r.GetStringOrNull(1), r.GetInt64(2)), ("$s", id), ("$f", fromMs)))
                    Add(x, y, n);
            }

            int minEdge = Context.Settings.Get("insights.space.faction_min_edge", 2);
            var kept = edges.Where(e => e.Value >= minEdge).OrderBy(e => e.Key.A, StringComparer.Ordinal).ThenBy(e => e.Key.B, StringComparer.Ordinal).ToList();
            var people = kept.SelectMany(e => new[] { e.Key.A, e.Key.B }).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();
            var groups = new List<List<string>>();
            double? modularity = null;
            var strength = new Dictionary<string, double>(StringComparer.Ordinal);
            if (people.Count >= 4)
            {
                var index = people.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i, StringComparer.Ordinal);
                var list = kept.Select(e => (index[e.Key.A], index[e.Key.B], e.Value)).ToList();
                int[] membership = Leiden.Run(people.Count, list, Context.Settings.Get("insights.community.resolution", 1.0));
                modularity = Math.Round(Leiden.Modularity(people.Count, list, membership), 4);
                groups = membership.Select((c, i) => (c, Person: people[i])).GroupBy(x => x.c).OrderBy(g => g.Key).Select(g => g.Select(x => x.Person).ToList()).Where(g => g.Count >= 2).ToList();
                var groupOf = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int g = 0; g < groups.Count; g++)
                    foreach (string p in groups[g]) groupOf[p] = g;
                foreach (var e in kept)
                    if (groupOf.TryGetValue(e.Key.A, out int ga) && groupOf.TryGetValue(e.Key.B, out int gb) && ga == gb)
                    {
                        strength[e.Key.A] = strength.GetValueOrDefault(e.Key.A) + e.Value;
                        strength[e.Key.B] = strength.GetValueOrDefault(e.Key.B) + e.Value;
                    }
            }
            // One group that holds everyone is not a faction.
            if (groups.Count < 2) groups.Clear();
            var before2 = previousFactions == null ? new Dictionary<string, string>(StringComparer.Ordinal)
                : JsonSerializer.Deserialize<Dictionary<string, string>>(previousFactions, KernelJson.Options) ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var factionNames = Names(groups.SelectMany(g => g));
            string? digest = null;
            if (HasTable("brief_digests"))
            {
                using var intel = Context.Stores.Intel.OpenRead();
                digest = intel.Scalar<string>(null, "SELECT digest_id FROM brief_digests WHERE scope_kind=$k AND scope_id=$i AND state='done' ORDER BY to_ms DESC LIMIT 1", ("$k", kind), ("$i", id));
            }

            await Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                var ids = StableIds(groups, before2, () => "f" + c.Scalar<long>(tx, "UPDATE ins_state SET value = value + 1 WHERE name='faction_seq' RETURNING value").ToString(CultureInfo.InvariantCulture));
                var factions = groups.Select((g, i) => new SpaceFactionDto(ids[i], g.Count, g.Sum(p => strength.GetValueOrDefault(p)) / 2,
                    g.OrderByDescending(p => strength.GetValueOrDefault(p)).ThenBy(p => p, StringComparer.Ordinal).Take(8).Select(p => new SpaceFactionMemberDto(p, factionNames.GetValueOrDefault(p), strength.GetValueOrDefault(p))).ToList()))
                    .OrderByDescending(f => f.Size).ThenBy(f => f.FactionId, StringComparer.Ordinal).ToList();
                var page = new SpacePageDto(kind, id, title, platform, window, from, to, now, messages, previousMessages, total.Count, before.Count, days, core, rising, tone, aboutKlives,
                    events.OrderByDescending(e => e.Day, StringComparer.Ordinal).ThenBy(e => e.Kind, StringComparer.Ordinal).Take(15).ToList(), factions, groups.Count == 0 ? null : modularity, channels, coverage,
                    leftOut, digest);
                var memberOf = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int g = 0; g < groups.Count; g++)
                    foreach (string p in groups[g]) memberOf[p] = ids[g];
                c.Exec(tx, """
                    INSERT INTO ins_spaces(scope_kind, scope_id, title, platform, messages, previous_messages, people, about_klives, last_message_ms, window_days, body_json, factions_json, computed_at_ms)
                    VALUES($k,$i,$t,$p,$m,$pm,$pe,$ak,$l,$w,$b,$f,$n)
                    ON CONFLICT(scope_kind, scope_id) DO UPDATE SET title=excluded.title, platform=excluded.platform, messages=excluded.messages, previous_messages=excluded.previous_messages,
                        people=excluded.people, about_klives=excluded.about_klives, last_message_ms=excluded.last_message_ms, window_days=excluded.window_days, body_json=excluded.body_json,
                        factions_json=excluded.factions_json, computed_at_ms=excluded.computed_at_ms
                    """, ("$k", kind), ("$i", id), ("$t", title), ("$p", platform), ("$m", messages), ("$pm", previousMessages), ("$pe", total.Count), ("$ak", about), ("$l", lastMs), ("$w", window),
                    ("$b", JsonSerializer.Serialize(page, KernelJson.Options)), ("$f", JsonSerializer.Serialize(memberOf, KernelJson.Options)), ("$n", now));
            }, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Gives each group the id of the earlier group it shares most people with (Jaccard at least 0.3, each earlier id used
        /// once), and a new id otherwise: a group is the same one next week when it kept most of its people.
        /// </summary>
        internal static string[] StableIds(IReadOnlyList<List<string>> groups, IReadOnlyDictionary<string, string> previous, Func<string> newId)
        {
            var oldSizes = previous.Values.GroupBy(v => v, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var candidates = new List<(int Group, string Old, double Jaccard)>();
            for (int gi = 0; gi < groups.Count; gi++)
                foreach (var o in groups[gi].Where(previous.ContainsKey).GroupBy(p => previous[p], StringComparer.Ordinal))
                    candidates.Add((gi, o.Key, o.Count() / (double)(groups[gi].Count + oldSizes[o.Key] - o.Count())));
            var ids = new string?[groups.Count];
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var cand in candidates.Where(x => x.Jaccard >= 0.3).OrderByDescending(x => x.Jaccard).ThenBy(x => x.Old, StringComparer.Ordinal).ThenBy(x => x.Group))
            {
                if (ids[cand.Group] != null || !used.Add(cand.Old)) continue;
                ids[cand.Group] = cand.Old;
            }
            for (int gi = 0; gi < groups.Count; gi++) ids[gi] ??= newId();
            return ids!;
        }
    }
}
