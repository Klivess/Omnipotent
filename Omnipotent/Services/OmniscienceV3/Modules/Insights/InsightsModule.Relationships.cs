using System.Globalization;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Modules;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Modules.Rollups;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;
using Omnipotent.Services.OmniscienceV3.Temporal;

namespace Omnipotent.Services.OmniscienceV3.Modules.Insights
{
    /// <summary>One week of one relationship. <c>closeness</c>: their interactions that week over the geometric mean of
    /// everything each of them did that week (1 = they only interacted with each other).</summary>
    [Api.ApiContract]
    public sealed record PairWeekDto(string Week, int FromA, int FromB, int Conflict, int Negative, int Positive, int Answered, double Closeness);

    /// <summary><c>kind</c>: fight, reconciled, drifting, closer, new or introduced. <c>data</c> holds the numbers behind it.</summary>
    [Api.ApiContract]
    public sealed record PairEventDto(string EventId, string A, string B, string Kind, string Week, long AtMs, JsonElement Data);

    /// <summary><c>trend</c>: new, closer, steady, drifting or quiet (one of them stopped writing altogether, which is not a change between them).</summary>
    [Api.ApiContract]
    public sealed record PairSummaryDto(string A, string? AName, string B, string? BName, int Interactions, int Recent, double Closeness, double PreviousCloseness, double Reciprocity,
        double Tension, string Trend, string FirstWeek, string LastWeek);

    /// <summary><c>medianReplySecondsFromA</c>: how quickly A typically answers B (null when there are too few replies to say).</summary>
    [Api.ApiContract]
    public sealed record PairDetailDto(PairSummaryDto Summary, IReadOnlyList<PairWeekDto> Weeks, IReadOnlyList<PairEventDto> Events, double? MedianReplySecondsFromA,
        double? MedianReplySecondsFromB, long AsOfMs);

    [Api.ApiContract]
    public sealed record CommunityMemberDto(string PersonId, string? Name, double Strength);

    [Api.ApiContract]
    public sealed record CommunityDto(string CommunityId, int Size, double Weight, IReadOnlyList<CommunityMemberDto> Top);

    /// <summary>Someone with a quarter or more of their interactions in another community than their own.</summary>
    [Api.ApiContract]
    public sealed record BridgeDto(string PersonId, string? Name, string CommunityId, string BridgeTo, double OutsideShare);

    [Api.ApiContract]
    public sealed record CommunitiesDto(IReadOnlyList<CommunityDto> Communities, IReadOnlyList<BridgeDto> Bridges, int People, double Modularity, long AsOfMs, string Note);

    [Api.ApiContract]
    public sealed record PersonRelationshipsDto(string PersonId, string? Name, string? CommunityId, string? BridgeTo, double? OutsideShare, IReadOnlyList<PairSummaryDto> Relationships,
        IReadOnlyList<PairEventDto> Events, long AsOfMs);

    public sealed partial class InsightsModule
    {
        private const int RecentWeeks = 8, PreviousWeeks = 26, IntroductionWeeks = 12, ReconcileWeeks = 8;
        public const string MinorRefusal = "Relationship analytics are not available for someone who is, or may be, under 18.";

        /// <summary>A week of a pair: interactions from a to b, from b to a, and what the ensemble read in them.</summary>
        private sealed class Cell
        {
            public int Out, In, Conflict, Negative, Positive, Answered;
            public int N => Out + In;
        }

        private sealed record NewEvent(string Id, string A, string B, string Kind, string Week, long AtMs, object Data);

        /// <summary>
        /// Rebuilds the relationship tables from the pair rollups: weekly series, a summary and a trend per pair, events
        /// (fights, reconciliations, drifting, getting closer, new, introductions), communities and bridges.
        /// </summary>
        private async Task<string?> BuildRelationshipsAsync(JobContext job)
        {
            var ct = job.Cancellation;
            if (!HasTable("rollup_pair_day")) return Json(new { skipped = "this build has no rollups" });
            long now = Context.Clock.NowMs;
            int weeks = Context.Settings.Get("insights.pair.weeks", 104), minInteractions = Context.Settings.Get("insights.pair.min_interactions", 10),
                minEdge = Context.Settings.Get("insights.community.min_edge", 3);
            double resolution = Context.Settings.Get("insights.community.resolution", 1.0);
            var current = DateOnly.ParseExact(WeekOf(now), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            string Week(int back) => current.AddDays(-7 * back).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string fromWeek = Week(weeks - 1);

            var restricted = new Dictionary<string, bool>(StringComparer.Ordinal);
            bool Hidden(string p) => restricted.TryGetValue(p, out bool r) ? r : restricted[p] = Restricted(p, now);
            var series = new Dictionary<(string A, string B), Dictionary<string, Cell>>();
            var personWeek = new Dictionary<(string Person, string Week), int>();
            using (var intel = Context.Stores.Intel.OpenRead())
            {
                // Summed per week in SQL (a week starts on Monday), so the job reads pairs × weeks rows, not pairs × days.
                foreach (var r in intel.Query(null, """
                    SELECT person_id, partner_id, date(day,'weekday 0','-6 days') AS week, SUM(replies+mentions+dm_msgs+reactions),
                           SUM(COALESCE(json_extract(labels,'$.conflict.tension'),0)+COALESCE(json_extract(labels,'$.conflict.conflict'),0)),
                           SUM(COALESCE(json_extract(labels,'$.sentiment.negative'),0)), SUM(COALESCE(json_extract(labels,'$.sentiment.positive'),0)),
                           SUM(COALESCE(json_extract(labels,'$.sentiment.negative'),0)+COALESCE(json_extract(labels,'$.sentiment.positive'),0)+COALESCE(json_extract(labels,'$.sentiment.neutral'),0))
                    FROM rollup_pair_day WHERE day >= $from GROUP BY person_id, partner_id, week
                    """, x => (Person: x.GetString(0), Partner: x.GetString(1), Week: x.GetString(2), N: x.GetInt32(3), Conflict: x.GetInt32(4), Negative: x.GetInt32(5), Positive: x.GetInt32(6),
                        Answered: x.GetInt32(7)), ("$from", fromWeek)))
                {
                    if (r.N <= 0 || r.Person == r.Partner || Hidden(r.Person) || Hidden(r.Partner)) continue;
                    var key = Ordered(r.Person, r.Partner);
                    if (!series.TryGetValue(key, out var byWeek)) series[key] = byWeek = new Dictionary<string, Cell>(StringComparer.Ordinal);
                    if (!byWeek.TryGetValue(r.Week, out var cell)) byWeek[r.Week] = cell = new Cell();
                    if (r.Person == key.A) cell.Out += r.N; else cell.In += r.N;
                    cell.Conflict += r.Conflict;
                    cell.Negative += r.Negative;
                    cell.Positive += r.Positive;
                    cell.Answered += r.Answered;
                    personWeek[(r.Person, r.Week)] = personWeek.GetValueOrDefault((r.Person, r.Week)) + r.N;
                    personWeek[(r.Partner, r.Week)] = personWeek.GetValueOrDefault((r.Partner, r.Week)) + r.N;
                }
            }
            ct.ThrowIfCancellationRequested();

            var recentSet = Enumerable.Range(0, RecentWeeks).Select(Week).ToHashSet(StringComparer.Ordinal);
            var previousSet = Enumerable.Range(RecentWeeks, PreviousWeeks).Select(Week).ToHashSet(StringComparer.Ordinal);
            var halfSet = Enumerable.Range(0, 26).Select(Week).ToHashSet(StringComparer.Ordinal);
            int Total(string person, HashSet<string> set) => set.Sum(w => personWeek.GetValueOrDefault((person, w)));
            var totals = new Dictionary<string, (int Recent, int Previous)>(StringComparer.Ordinal);
            (int Recent, int Previous) TotalsOf(string p) => totals.TryGetValue(p, out var t) ? t : totals[p] = (Total(p, recentSet), Total(p, previousSet));
            static double Share(int n, int ta, int tb) => n <= 0 || ta <= 0 || tb <= 0 ? 0 : Math.Min(1, n / Math.Sqrt((double)ta * tb));
            string halfYear = current.Year.ToString(CultureInfo.InvariantCulture) + (current.Month <= 6 ? "H1" : "H2");

            var kept = new List<(string A, string B, Dictionary<string, Cell> Weeks, int Interactions, int Recent, double Closeness, double Previous, double Reciprocity, double Tension, string Trend)>();
            var events = new List<NewEvent>();
            foreach (var ((a, b), byWeek) in series)
            {
                int interactions = byWeek.Values.Sum(c => c.N);
                if (interactions < minInteractions) continue;
                int recentN = byWeek.Where(w => recentSet.Contains(w.Key)).Sum(w => w.Value.N), previousN = byWeek.Where(w => previousSet.Contains(w.Key)).Sum(w => w.Value.N);
                var (aRecent, aPrevious) = TotalsOf(a);
                var (bRecent, bPrevious) = TotalsOf(b);
                double closeness = Share(recentN, aRecent, bRecent), previous = Share(previousN, aPrevious, bPrevious);
                // Someone who stopped writing to everybody has not grown apart from this one person.
                bool Inactive(int recentTotal, int previousTotal) => recentTotal == 0 || (previousTotal > 0 && recentTotal / (double)RecentWeeks < 0.2 * previousTotal / PreviousWeeks);
                bool anyInactive = Inactive(aRecent, aPrevious) || Inactive(bRecent, bPrevious);
                string trend = previousN == 0 && recentN >= 6 ? "new"
                    : previousN >= 20 && anyInactive && recentN * (double)PreviousWeeks < 0.4 * previousN * RecentWeeks ? "quiet"
                    : previousN >= 20 && closeness <= 0.4 * previous ? "drifting"
                    : recentN >= 6 && previousN > 0 && closeness >= 1.5 * previous ? "closer" : "steady";
                var half = byWeek.Where(w => halfSet.Contains(w.Key)).Select(w => w.Value).ToList();
                int outN = half.Sum(c => c.Out), inN = half.Sum(c => c.In), halfN = outN + inN;
                double reciprocity = Math.Max(outN, inN) == 0 ? 0 : (double)Math.Min(outN, inN) / Math.Max(outN, inN);
                double tension = halfN == 0 ? 0 : Math.Min(1, (double)half.Sum(c => c.Conflict) / halfN);
                kept.Add((a, b, byWeek, interactions, recentN, closeness, previous, reciprocity, tension, trend));

                if (trend is "new" or "closer" or "drifting")
                    events.Add(new NewEvent($"{trend}|{a}|{b}|{halfYear}", a, b, trend, Week(0), WeekStartMs(Week(0)),
                        new { recent = recentN, previous = previousN, closeness = Math.Round(closeness, 4), previousCloseness = Math.Round(previous, 4) }));
                var ordered = byWeek.OrderBy(w => w.Key, StringComparer.Ordinal).ToList();
                for (int i = 0; i < ordered.Count; i++)
                {
                    var (week, cell) = (ordered[i].Key, ordered[i].Value);
                    if (cell.Conflict < 3 || cell.Conflict < 0.3 * cell.N) continue;
                    events.Add(new NewEvent($"fight|{a}|{b}|{week}", a, b, "fight", week, WeekStartMs(week), new { conflict = cell.Conflict, interactions = cell.N }));
                    // Made up: within eight weeks, a week in which they talk again, calmly, and it does not flare up in between.
                    string limit = DateOnly.ParseExact(week, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(7 * ReconcileWeeks).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    for (int j = i + 1; j < ordered.Count && string.CompareOrdinal(ordered[j].Key, limit) <= 0; j++)
                    {
                        var later = ordered[j].Value;
                        if (later.Conflict >= 3 && later.Conflict >= 0.3 * later.N) break;
                        if (later.N >= 3 && later.Conflict <= 0.1 * later.N && later.Positive >= later.Negative)
                        {
                            events.Add(new NewEvent($"reconciled|{a}|{b}|{ordered[j].Key}", a, b, "reconciled", ordered[j].Key, WeekStartMs(ordered[j].Key), new { afterFightWeek = week, interactions = later.N }));
                            break;
                        }
                    }
                }
            }

            // Introductions: two people whose first interaction comes after both had been interacting with a third.
            var partnersOf = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var (a, b) in series.Keys)
            {
                (partnersOf.TryGetValue(a, out var pa) ? pa : partnersOf[a] = new HashSet<string>(StringComparer.Ordinal)).Add(b);
                (partnersOf.TryGetValue(b, out var pb) ? pb : partnersOf[b] = new HashSet<string>(StringComparer.Ordinal)).Add(a);
            }
            string earliestObservable = Week(weeks - 1 - IntroductionWeeks);
            foreach (var k in kept)
            {
                string first = k.Weeks.Keys.Min(StringComparer.Ordinal)!;
                if (string.CompareOrdinal(first, earliestObservable) <= 0) continue; // too close to the start of the window to know they had not met
                var firstDate = DateOnly.ParseExact(first, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var before = Enumerable.Range(1, IntroductionWeeks).Select(i => firstDate.AddDays(-7 * i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToHashSet(StringComparer.Ordinal);
                int With(string x, string y) => series.TryGetValue(Ordered(x, y), out var s) ? s.Where(w => before.Contains(w.Key)).Sum(w => w.Value.N) : 0;
                var by = partnersOf[k.A].Intersect(partnersOf[k.B]).Select(c => (Person: c, WithA: With(c, k.A), WithB: With(c, k.B))).Where(c => c.WithA >= 5 && c.WithB >= 5)
                    .OrderByDescending(c => Math.Min(c.WithA, c.WithB)).ThenBy(c => c.Person, StringComparer.Ordinal).FirstOrDefault();
                if (by.Person != null)
                    events.Add(new NewEvent($"introduced|{k.A}|{k.B}|{first}", k.A, k.B, "introduced", first, WeekStartMs(first), new { by = by.Person, withA = by.WithA, withB = by.WithB }));
            }
            ct.ThrowIfCancellationRequested();

            // Communities over the last half year.
            var edgeList = new List<(string A, string B, int W)>();
            foreach (var ((a, b), byWeek) in series)
            {
                int w = byWeek.Where(x => halfSet.Contains(x.Key)).Sum(x => x.Value.N);
                if (w >= minEdge) edgeList.Add((a, b, w));
            }
            var people = edgeList.SelectMany(e => new[] { e.A, e.B }).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();
            var index = people.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i, StringComparer.Ordinal);
            var graphEdges = edgeList.Select(e => (index[e.A], index[e.B], (double)e.W)).ToList();
            var membership = Leiden.Run(people.Count, graphEdges, resolution, seed: 1);
            double modularity = people.Count == 0 ? 0 : Leiden.Modularity(people.Count, graphEdges, membership, resolution);
            Dictionary<string, string> previousMembers;
            using (var intel = Context.Stores.Intel.OpenRead())
                previousMembers = intel.Query(null, "SELECT person_id, community_id FROM ins_members", r => (r.GetString(0), r.GetString(1))).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
            var groups = membership.Select((c, i) => (c, Person: people[i])).GroupBy(x => x.c).OrderBy(g => g.Key).Select(g => g.Select(x => x.Person).ToList()).ToList();

            var written = await Context.Stores.Intel.WriteAsync((c, tx) =>
            {
                // Ids stay with the community that keeps most of its people (so a community is the same one next week).
                var oldSizes = previousMembers.Values.GroupBy(v => v).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
                var candidates = new List<(int Group, string Old, double Jaccard)>();
                for (int gi = 0; gi < groups.Count; gi++)
                    foreach (var o in groups[gi].Where(previousMembers.ContainsKey).GroupBy(p => previousMembers[p]))
                        candidates.Add((gi, o.Key, o.Count() / (double)(groups[gi].Count + oldSizes[o.Key] - o.Count())));
                var idOf = new string?[groups.Count];
                var used = new HashSet<string>(StringComparer.Ordinal);
                foreach (var cand in candidates.Where(x => x.Jaccard >= 0.3).OrderByDescending(x => x.Jaccard).ThenBy(x => x.Old, StringComparer.Ordinal).ThenBy(x => x.Group))
                {
                    if (idOf[cand.Group] != null || !used.Add(cand.Old)) continue;
                    idOf[cand.Group] = cand.Old;
                }
                for (int gi = 0; gi < groups.Count; gi++)
                    idOf[gi] ??= "c" + c.Scalar<long>(tx, "UPDATE ins_state SET value = value + 1 WHERE name='community_seq' RETURNING value").ToString(CultureInfo.InvariantCulture);
                var communityOf = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int gi = 0; gi < groups.Count; gi++)
                    foreach (string p in groups[gi]) communityOf[p] = idOf[gi]!;
                var inside = new Dictionary<string, double>(StringComparer.Ordinal);
                var outside = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
                foreach (var (a, b, w) in edgeList)
                {
                    foreach (var (x, y) in new[] { (a, b), (b, a) })
                    {
                        if (communityOf[x] == communityOf[y]) inside[x] = inside.GetValueOrDefault(x) + w;
                        else
                        {
                            if (!outside.TryGetValue(x, out var o)) outside[x] = o = new Dictionary<string, double>(StringComparer.Ordinal);
                            o[communityOf[y]] = o.GetValueOrDefault(communityOf[y]) + w;
                        }
                    }
                }

                c.Exec(tx, "DELETE FROM ins_pair_week");
                using (var week = new PreparedStatement(c, tx, "INSERT INTO ins_pair_week(a, b, week, out_n, in_n, conflict, negative, positive, answered, closeness) VALUES($a,$b,$w,$o,$i,$c,$n,$p,$s,$cl)",
                           "$a", "$b", "$w", "$o", "$i", "$c", "$n", "$p", "$s", "$cl"))
                using (var pair = new PreparedStatement(c, tx, """
                           INSERT INTO ins_pairs(pair_id, a, b, interactions, recent, closeness, previous_closeness, reciprocity, tension, trend, first_week, last_week, updated_at_ms, change_seq)
                           VALUES($id,$a,$b,$n,$r,$c,$p,$rec,$t,$tr,$f,$l,$now,$ch)
                           ON CONFLICT(pair_id) DO UPDATE SET interactions=excluded.interactions, recent=excluded.recent, closeness=excluded.closeness, previous_closeness=excluded.previous_closeness,
                               reciprocity=excluded.reciprocity, tension=excluded.tension, trend=excluded.trend, first_week=excluded.first_week, last_week=excluded.last_week,
                               updated_at_ms=excluded.updated_at_ms, change_seq=excluded.change_seq
                           """, "$id", "$a", "$b", "$n", "$r", "$c", "$p", "$rec", "$t", "$tr", "$f", "$l", "$now", "$ch"))
                {
                    long change = NextChange(c, tx);
                    foreach (var k in kept)
                    {
                        foreach (var (w, cell) in k.Weeks)
                            week.Run(k.A, k.B, w, cell.Out, cell.In, cell.Conflict, cell.Negative, cell.Positive, cell.Answered,
                                Math.Round(Share(cell.N, personWeek.GetValueOrDefault((k.A, w)), personWeek.GetValueOrDefault((k.B, w))), 5));
                        pair.Run(PairId(k.A, k.B), k.A, k.B, k.Interactions, k.Recent, Math.Round(k.Closeness, 5), Math.Round(k.Previous, 5), Math.Round(k.Reciprocity, 4), Math.Round(k.Tension, 4),
                            k.Trend, k.Weeks.Keys.Min(StringComparer.Ordinal), k.Weeks.Keys.Max(StringComparer.Ordinal), now, change);
                    }
                }
                // Pairs that fell out (too few interactions in the window, or someone now protected) and their events go.
                c.Exec(tx, "DELETE FROM ins_pairs WHERE updated_at_ms < $n", ("$n", now));
                c.Exec(tx, "DELETE FROM ins_pair_events WHERE NOT EXISTS (SELECT 1 FROM ins_pairs p WHERE p.a = ins_pair_events.a AND p.b = ins_pair_events.b)");
                var fresh = new List<NewEvent>();
                foreach (var e in events)
                {
                    int n = c.Exec(tx, "INSERT OR IGNORE INTO ins_pair_events(event_id, a, b, kind, week, at_ms, data_json, created_at_ms, change_seq) VALUES($id,$a,$b,$k,$w,$t,$d,$n,$ch)",
                        ("$id", e.Id), ("$a", e.A), ("$b", e.B), ("$k", e.Kind), ("$w", e.Week), ("$t", e.AtMs), ("$d", Json(e.Data)), ("$n", now), ("$ch", NextChange(c, tx)));
                    if (n > 0) fresh.Add(e);
                }

                c.Exec(tx, "DELETE FROM ins_members");
                var keepIds = new HashSet<string>(idOf.OfType<string>(), StringComparer.Ordinal);
                foreach (string old in c.Query(tx, "SELECT community_id FROM ins_communities", r => r.GetString(0)).Where(o => !keepIds.Contains(o)))
                    c.Exec(tx, "DELETE FROM ins_communities WHERE community_id=$c", ("$c", old));
                for (int gi = 0; gi < groups.Count; gi++)
                {
                    var top = groups[gi].Select(p => new { personId = p, strength = inside.GetValueOrDefault(p) }).OrderByDescending(x => x.strength).ThenBy(x => x.personId, StringComparer.Ordinal).Take(10).ToList();
                    c.Exec(tx, """
                        INSERT INTO ins_communities(community_id, size, weight, top_json, created_at_ms, updated_at_ms) VALUES($c,$s,$w,$t,$n,$n)
                        ON CONFLICT(community_id) DO UPDATE SET size=excluded.size, weight=excluded.weight, top_json=excluded.top_json, updated_at_ms=excluded.updated_at_ms
                        """, ("$c", idOf[gi]), ("$s", groups[gi].Count), ("$w", groups[gi].Sum(p => inside.GetValueOrDefault(p)) / 2), ("$t", Json(top)), ("$n", now));
                    foreach (string p in groups[gi])
                    {
                        double own = inside.GetValueOrDefault(p), away = outside.TryGetValue(p, out var o) ? o.Values.Sum() : 0;
                        double share = own + away <= 0 ? 0 : away / (own + away);
                        var strongest = o == null ? default : o.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).First();
                        string? bridge = share >= 0.25 && strongest.Value >= 10 ? strongest.Key : null;
                        c.Exec(tx, "INSERT INTO ins_members(person_id, community_id, strength, outside_share, bridge_to, updated_at_ms) VALUES($p,$c,$s,$o,$b,$n)",
                            ("$p", p), ("$c", idOf[gi]), ("$s", own), ("$o", Math.Round(share, 4)), ("$b", bridge), ("$n", now));
                    }
                }
                return fresh;
            }, ct).ConfigureAwait(false);

            long seq;
            using (var corpus = Context.Stores.Corpus.OpenRead()) seq = ChangeLog.MaxSeq(corpus);
            await Context.ReadModels.PutAsync(CommunitiesModel, "all", 1, now, seq, Communities(modularity), ct).ConfigureAwait(false);
            int noticed = await NotifyAsync(written, now, ct).ConfigureAwait(false);
            return Json(new { pairs = kept.Count, events = written.Count, communities = groups.Count, people = people.Count, modularity = Math.Round(modularity, 4), noticed });
        }

        /// <summary>X45: a change in one of Klives's own relationships, or between two people he tracks, becomes a notice.</summary>
        private async Task<int> NotifyAsync(List<NewEvent> fresh, long now, CancellationToken ct)
        {
            if (notices == null || fresh.Count == 0 || !Context.Settings.Get("insights.notify.relationships", true)) return 0;
            string? self = SelfPerson();
            HashSet<string> tracked;
            using (var op = Context.Stores.Operator.OpenRead())
                tracked = op.Query(null, "SELECT person_id FROM person_settings WHERE tier='tracked' OR pinned=1", r => Context.Policy.Canonical(r.GetString(0))).ToHashSet(StringComparer.Ordinal);
            int count = 0;
            // Only what just happened: an event found in old history is in the tables, not on Klives's phone.
            foreach (var e in fresh.Where(e => now - e.AtMs <= 14 * 86_400_000L && e.Kind != "introduced"))
            {
                bool mine = e.A == self || e.B == self;
                if (!mine && !(tracked.Contains(e.A) && tracked.Contains(e.B))) continue;
                string person = e.A == self ? e.B : e.A;
                string? other = mine ? null : e.B;
                string severity = mine && e.Kind is "fight" or "drifting" ? NoticeSeverity.Attention : NoticeSeverity.Info;
                if (await notices.NoticeAsync(new Notice("rel|" + e.Id, "relationship", ModuleId, now, new { change = e.Kind, a = e.A, b = e.B, week = e.Week, detail = e.Data }, severity, person, other), ct)
                        .ConfigureAwait(false)) count++;
            }
            return count;
        }

        // ── reads ────────────────────────────────────────────────────────────────

        private const string PairColumns = "a, b, interactions, recent, closeness, previous_closeness, reciprocity, tension, trend, first_week, last_week";

        private static (string A, string B, int N, int Recent, double Closeness, double Previous, double Reciprocity, double Tension, string Trend, string First, string Last) ReadPair(
            Microsoft.Data.Sqlite.SqliteDataReader r) =>
            (r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6), r.GetDouble(7), r.GetString(8), r.GetString(9), r.GetString(10));

        private static PairEventDto ReadEvent(Microsoft.Data.Sqlite.SqliteDataReader r) =>
            new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5), JsonDocument.Parse(r.GetString(6)).RootElement.Clone());

        private long AsOf()
        {
            using var c = Context.Stores.Intel.OpenRead();
            return c.Scalar<long?>(null, "SELECT MAX(updated_at_ms) FROM ins_pairs") ?? 0;
        }

        public CommunitiesDto Communities(double? modularity = null)
        {
            long now = Context.Clock.NowMs;
            List<(string Id, int Size, double Weight, string Top, long Updated)> rows;
            List<(string Person, string Community, string? Bridge, double Share)> bridges;
            long people;
            using (var c = Context.Stores.Intel.OpenRead())
            {
                rows = c.Query(null, "SELECT community_id, size, weight, top_json, updated_at_ms FROM ins_communities ORDER BY size DESC, community_id",
                    r => (r.GetString(0), r.GetInt32(1), r.GetDouble(2), r.GetString(3), r.GetInt64(4)));
                bridges = c.Query(null, "SELECT person_id, community_id, bridge_to, outside_share FROM ins_members WHERE bridge_to IS NOT NULL ORDER BY outside_share DESC, person_id LIMIT 200",
                    r => (r.GetString(0), r.GetString(1), r.GetStringOrNull(2), r.GetDouble(3)));
                people = c.Scalar<long>(null, "SELECT COUNT(*) FROM ins_members");
            }
            var tops = rows.ToDictionary(r => r.Id, r => JsonSerializer.Deserialize<List<JsonElement>>(r.Top, KernelJson.Options) ?? new List<JsonElement>(), StringComparer.Ordinal);
            var visible = tops.Values.SelectMany(t => t.Select(x => x.GetProperty("personId").GetString()!)).Concat(bridges.Select(b => b.Person)).Where(p => !Restricted(p, now)).ToHashSet(StringComparer.Ordinal);
            var names = Names(visible);
            return new CommunitiesDto(
                rows.Select(r => new CommunityDto(r.Id, r.Size, r.Weight, tops[r.Id].Select(t => (Id: t.GetProperty("personId").GetString()!, Strength: t.GetProperty("strength").GetDouble()))
                    .Where(t => visible.Contains(t.Id)).Select(t => new CommunityMemberDto(t.Id, names.GetValueOrDefault(t.Id), t.Strength)).ToList())).ToList(),
                bridges.Where(b => visible.Contains(b.Person)).Select(b => new BridgeDto(b.Person, names.GetValueOrDefault(b.Person), b.Community, b.Bridge!, b.Share)).ToList(),
                (int)people, Math.Round(modularity ?? 0, 4), rows.Count == 0 ? 0 : rows.Max(r => r.Updated),
                "Communities are found only in the interactions Omniscience has seen over the last half year; people who talk where it cannot see are not connected here.");
        }

        public PersonRelationshipsDto PersonRelationships(string personId, int limit)
        {
            string person = Context.Policy.Canonical(personId);
            long now = Context.Clock.NowMs;
            if (Restricted(person, now)) throw new TacticRefusedException(MinorRefusal);
            limit = Math.Clamp(limit, 1, 100);
            using var c = Context.Stores.Intel.OpenRead();
            var pairs = c.Query(null, $"SELECT {PairColumns} FROM ins_pairs WHERE a=$p OR b=$p ORDER BY recent DESC, interactions DESC, pair_id LIMIT $l", ReadPair, ("$p", person), ("$l", limit * 2))
                .Where(p => !Restricted(p.A == person ? p.B : p.A, now)).Take(limit).ToList();
            var events = c.Query(null, "SELECT event_id, a, b, kind, week, at_ms, data_json FROM ins_pair_events WHERE a=$p OR b=$p ORDER BY at_ms DESC, event_id LIMIT 50", ReadEvent, ("$p", person))
                .Where(e => !Restricted(e.A == person ? e.B : e.A, now)).ToList();
            var member = c.Query(null, "SELECT community_id, bridge_to, outside_share FROM ins_members WHERE person_id=$p", r => (r.GetString(0), r.GetStringOrNull(1), r.GetDouble(2)), ("$p", person))
                .Select(m => ((string Community, string? Bridge, double Share)?)m).FirstOrDefault();
            var names = Names(pairs.SelectMany(p => new[] { p.A, p.B }).Append(person));
            return new PersonRelationshipsDto(person, names.GetValueOrDefault(person), member?.Community, member?.Bridge, member?.Share,
                pairs.Select(p => new PairSummaryDto(p.A, names.GetValueOrDefault(p.A), p.B, names.GetValueOrDefault(p.B), p.N, p.Recent, p.Closeness, p.Previous, p.Reciprocity, p.Tension, p.Trend, p.First, p.Last))
                    .ToList(), events, AsOf());
        }

        public PairDetailDto? Pair(string personA, string personB)
        {
            long now = Context.Clock.NowMs;
            var (a, b) = Ordered(Context.Policy.Canonical(personA), Context.Policy.Canonical(personB));
            if (a == b) throw new ArgumentException("A relationship needs two different people.");
            if (Restricted(a, now) || Restricted(b, now)) throw new TacticRefusedException(MinorRefusal);
            using var c = Context.Stores.Intel.OpenRead();
            var pair = c.Query(null, $"SELECT {PairColumns} FROM ins_pairs WHERE pair_id=$id", ReadPair, ("$id", PairId(a, b))).Select(p => ((string A, string B, int N, int Recent, double Closeness,
                double Previous, double Reciprocity, double Tension, string Trend, string First, string Last)?)p).FirstOrDefault();
            if (pair == null) return null;
            var p = pair.Value;
            var weeks = c.Query(null, "SELECT week, out_n, in_n, conflict, negative, positive, answered, closeness FROM ins_pair_week WHERE a=$a AND b=$b ORDER BY week",
                r => new PairWeekDto(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetDouble(7)), ("$a", a), ("$b", b));
            var events = c.Query(null, "SELECT event_id, a, b, kind, week, at_ms, data_json FROM ins_pair_events WHERE a=$a AND b=$b ORDER BY at_ms DESC, event_id", ReadEvent, ("$a", a), ("$b", b));
            // Reply times each way over the last half year, from the rollups' latency histograms (bucket b = about 2^b seconds).
            string from = Zones.LocalDate(now - 182 * 86_400_000L, Context.Settings.KlivesZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var totals = RollupReader.Pairs(c, a, from).FirstOrDefault(x => x.Partner == b);
            static double? Median(int[]? histogram)
            {
                if (histogram == null) return null;
                int total = histogram.Sum();
                if (total < 5) return null;
                int seen = 0;
                for (int i = 0; i < histogram.Length; i++)
                {
                    seen += histogram[i];
                    if (seen * 2 >= total) return Math.Pow(2, i);
                }
                return null;
            }
            var names = Names(new[] { a, b });
            return new PairDetailDto(new PairSummaryDto(a, names.GetValueOrDefault(a), b, names.GetValueOrDefault(b), p.N, p.Recent, p.Closeness, p.Previous, p.Reciprocity, p.Tension, p.Trend, p.First, p.Last),
                weeks, events, Median(totals?.LatencyOut), Median(totals?.LatencyIn), AsOf());
        }
    }
}
