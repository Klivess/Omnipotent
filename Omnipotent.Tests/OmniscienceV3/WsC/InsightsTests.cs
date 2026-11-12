using System.Globalization;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Jobs;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Modules.Insights;
using Omnipotent.Services.OmniscienceV3.Modules.Signalling;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Tests.OmniscienceV3.WsC
{
    /// <summary>Relationship dynamics and communities (N12, X36, X45) and the Leiden algorithm under them.</summary>
    public class InsightsTests
    {
        private static List<(int, int, double)> Clique(IEnumerable<int> nodes, double weight = 1)
        {
            var list = nodes.ToList();
            var edges = new List<(int, int, double)>();
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++) edges.Add((list[i], list[j], weight));
            return edges;
        }

        private static bool Connected(int n, List<(int A, int B, double W)> edges, int[] community, int id)
        {
            var members = Enumerable.Range(0, n).Where(i => community[i] == id).ToList();
            if (members.Count <= 1) return true;
            var inside = members.ToHashSet();
            var seen = new HashSet<int> { members[0] };
            var queue = new Queue<int>(seen);
            while (queue.Count > 0)
            {
                int v = queue.Dequeue();
                foreach (var (a, b, _) in edges)
                {
                    int other = a == v ? b : b == v ? a : -1;
                    if (other >= 0 && inside.Contains(other) && seen.Add(other)) queue.Enqueue(other);
                }
            }
            return seen.Count == members.Count;
        }

        [Fact]
        public void Leiden_FindsPlantedCommunities_IsDeterministic_AndEveryCommunityIsConnected()
        {
            // Two groups of five who all know each other, joined by one weak tie.
            var two = Clique(Enumerable.Range(0, 5)).Concat(Clique(Enumerable.Range(5, 5))).Append((4, 5, 0.2)).ToList();
            var found = Leiden.Run(10, two);
            Assert.Equal(2, found.Distinct().Count());
            Assert.Single(found.Take(5).Distinct());
            Assert.Single(found.Skip(5).Distinct());
            Assert.NotEqual(found[0], found[9]);
            Assert.Equal(found, Leiden.Run(10, two));
            Assert.True(Leiden.Modularity(10, two, found) > 0.45);

            // A ring of six groups of four, each tied to the next by a single edge.
            var ring = new List<(int, int, double)>();
            for (int g = 0; g < 6; g++)
            {
                ring.AddRange(Clique(Enumerable.Range(g * 4, 4)));
                ring.Add((g * 4 + 3, ((g + 1) % 6) * 4, 1));
            }
            var ringFound = Leiden.Run(24, ring);
            Assert.Equal(6, ringFound.Distinct().Count());
            for (int g = 0; g < 6; g++) Assert.Single(ringFound.Skip(g * 4).Take(4).Distinct());

            // A noisy planted partition: whatever it finds, every community holds together, and it is far better than chance.
            var rng = new Random(11);
            var noisy = new List<(int A, int B, double W)>();
            for (int i = 0; i < 60; i++)
                for (int j = i + 1; j < 60; j++)
                    if (rng.NextDouble() < (i / 15 == j / 15 ? 0.5 : 0.03)) noisy.Add((i, j, 1 + rng.Next(3)));
            var noisyFound = Leiden.Run(60, noisy, seed: 3);
            Assert.All(noisyFound.Distinct(), id => Assert.True(Connected(60, noisy, noisyFound, id), $"community {id} is not connected"));
            Assert.True(Leiden.Modularity(60, noisy, noisyFound) > 0.4);
            int agree = 0, pairs = 0;
            for (int i = 0; i < 60; i++)
                for (int j = i + 1; j < 60; j++) { pairs++; if ((i / 15 == j / 15) == (noisyFound[i] == noisyFound[j])) agree++; }
            Assert.True(agree / (double)pairs > 0.9, $"only {agree} of {pairs} pairs agree with the planted groups");
            // A higher resolution finds at least as many communities.
            Assert.True(Leiden.Run(60, noisy, resolution: 3, seed: 3).Distinct().Count() >= noisyFound.Distinct().Count());

            // Nothing to connect: everyone alone; nothing at all: nothing.
            Assert.Equal(new[] { 0, 1, 2 }, Leiden.Run(3, Array.Empty<(int, int, double)>()));
            Assert.Empty(Leiden.Run(0, Array.Empty<(int, int, double)>()));
            // Largest community first; an isolated node is its own community.
            var withIsolated = Leiden.Run(7, Clique(Enumerable.Range(0, 4)).Concat(Clique(new[] { 4, 5 })).ToList());
            Assert.Equal(new[] { 0, 0, 0, 0, 1, 1, 2 }, withIsolated);
        }

        private const string K = "p-klives", Ana = "p-ana", Ben = "p-ben", Cyd = "p-cyd";
        private static readonly DateOnly ThisWeek = new(2025, 6, 2);

        /// <summary>One week of interactions from one person to another, written straight into the pair rollup (as the rollups
        /// would have): <paramref name="weeksAgo"/> 0 is the current week.</summary>
        private static Task PairWeekAsync(SignalsHarness h, string from, string to, int weeksAgo, int n, int conflict = 0, int negative = 0, int positive = 0) =>
            h.Runtime.Stores.Intel.WriteAsync((c, tx) =>
            {
                string day = ThisWeek.AddDays(-7 * weeksAgo + 2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                string labels = JsonSerializer.Serialize(new
                {
                    conflict = new Dictionary<string, int> { ["tension"] = conflict / 2, ["conflict"] = conflict - conflict / 2, ["none"] = Math.Max(0, n - conflict) },
                    sentiment = new Dictionary<string, int> { ["negative"] = negative, ["positive"] = positive, ["neutral"] = Math.Max(0, n - negative - positive) },
                });
                c.Exec(tx, """
                    INSERT INTO rollup_pair_day(person_id, partner_id, day, replies, mentions, dm_msgs, reactions, latency_explicit, latency_implicit, labels, labelset, source_seq)
                    VALUES($p,$q,$d,$n,0,0,0,'[0,0,0,0,0,0,9,0,0,0,0,0,0,0,0,0]','[0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0]',$l,1,1)
                    ON CONFLICT(person_id, partner_id, day) DO UPDATE SET replies=excluded.replies, labels=excluded.labels
                    """, ("$p", from), ("$q", to), ("$d", day), ("$n", n), ("$l", labels));
            });

        private static async Task BothWaysAsync(SignalsHarness h, string a, string b, int fromWeeksAgo, int toWeeksAgo, int each)
        {
            for (int w = fromWeeksAgo; w >= toWeeksAgo; w--)
            {
                await PairWeekAsync(h, a, b, w, each);
                await PairWeekAsync(h, b, a, w, each);
            }
        }

        private static Task AddPersonAsync(SignalsHarness h, string person) => h.Runtime.Stores.Corpus.WriteAsync((c, tx) =>
        {
            c.Exec(tx, "INSERT INTO persons(person_id, created_at_ms, display_name, is_self) VALUES($p,$t,$n,0)", ("$p", person), ("$t", h.Clock.NowMs - 86_400_000L), ("$n", char.ToUpperInvariant(person[2]) + person[3..]));
            c.Exec(tx, "INSERT INTO person_canonical(person_id, canonical_id) VALUES($p,$p)", ("$p", person));
        });

        private static async Task<JsonElement> RebuildAsync(SignalsHarness h)
        {
            await h.Insights!.RefreshAsync(CancellationToken.None);
            await h.Driver.RunUntilIdleAsync(5_000);
            h.Driver.AssertNoFailures();
            return h.Driver.LastResult(InsightsModule.RelationshipsKind)!.Value;
        }

        [Fact]
        public async Task Relationships_AreFollowedWeekByWeek_WithTrendsEventsCommunitiesAndNotices()
        {
            await using var h = await SignalsHarness.StartAsync(insights: true);
            foreach (string p in new[] { "p-dan", "p-eve", "p-kid", "p-x0", "p-x1", "p-x2", "p-x3" }) await AddPersonAsync(h, p);
            await h.MarkMinorAsync("p-kid");
            await h.Driver.RunUntilIdleAsync(5_000);          // the modules' first jobs, on empty rollups
            h.Driver.AssertNoFailures();

            await BothWaysAsync(h, K, Ben, 40, 0, 10);        // steady
            await BothWaysAsync(h, K, Ana, 34, 9, 10);        // close for half a year, then nothing: drifting (both stay active elsewhere)
            await BothWaysAsync(h, Ana, Cyd, 30, 0, 6);
            await BothWaysAsync(h, K, "p-eve", 30, 9, 8);     // Eve stopped writing to anyone: quiet, not drifting
            await BothWaysAsync(h, Ben, Cyd, 20, 4, 4);
            await PairWeekAsync(h, Ben, Cyd, 3, 4, conflict: 3, negative: 3);      // a fight …
            await PairWeekAsync(h, Cyd, Ben, 3, 4, conflict: 2, negative: 3);
            await PairWeekAsync(h, Ben, Cyd, 2, 1);
            await PairWeekAsync(h, Ben, Cyd, 1, 3, positive: 2);                    // … and they make up
            await PairWeekAsync(h, Cyd, Ben, 1, 3, positive: 2);
            await BothWaysAsync(h, Cyd, "p-dan", 30, 0, 5);   // Cyd knows Dan and Ana; Ana and Dan first talk four weeks ago
            await BothWaysAsync(h, Ana, "p-dan", 4, 0, 3);
            await BothWaysAsync(h, K, "p-kid", 30, 0, 9);     // a minor: in none of it
            foreach (var (a, b) in new[] { ("p-x0", "p-x1"), ("p-x0", "p-x2"), ("p-x0", "p-x3"), ("p-x1", "p-x2"), ("p-x1", "p-x3"), ("p-x2", "p-x3") })
                await BothWaysAsync(h, a, b, 20, 0, 12);      // a second group that keeps to itself …
            await BothWaysAsync(h, Ben, "p-x0", 20, 0, 7);    // … except that Ben also talks with them a lot

            var result = await RebuildAsync(h);
            Assert.True(result.GetProperty("pairs").GetInt32() >= 12);
            Assert.Equal(0, h.Intel("SELECT COUNT(*) FROM ins_pairs WHERE a='p-kid' OR b='p-kid'"));
            Assert.Equal(0, h.Intel("SELECT COUNT(*) FROM ins_members WHERE person_id='p-kid'"));

            // Trends.
            string Trend(string a, string b) => h.IntelText("SELECT trend FROM ins_pairs WHERE pair_id=$i", ("$i", InsightsModule.PairId(a, b)))!;
            Assert.Equal("steady", Trend(K, Ben));
            Assert.Equal("drifting", Trend(K, Ana));
            Assert.Equal("quiet", Trend(K, "p-eve"));
            Assert.Equal("new", Trend(Ana, "p-dan"));

            // Events.
            List<(string Kind, string A, string B, string Week)> Events() { using var c = h.Runtime.Stores.Intel.OpenRead(); return c.Query(null, "SELECT kind, a, b, week FROM ins_pair_events ORDER BY kind, a, b",
                r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3))); }
            var events = Events();
            string Week(int ago) => ThisWeek.AddDays(-7 * ago).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            Assert.Contains(("fight", Ben, Cyd, Week(3)), events);
            Assert.Contains(("reconciled", Ben, Cyd, Week(1)), events);
            Assert.Contains(("drifting", Ana, K, Week(0)), events);
            Assert.Contains(("new", Ana, "p-dan", Week(0)), events);
            Assert.Contains(("introduced", Ana, "p-dan", Week(4)), events);
            Assert.DoesNotContain(events, e => e.Kind == "drifting" && (e.A == "p-eve" || e.B == "p-eve"));
            Assert.Single(events, e => e.Kind == "fight");
            Assert.Equal(Cyd, h.IntelText("SELECT json_extract(data_json,'$.by') FROM ins_pair_events WHERE kind='introduced'"));

            // Klives is told about his own relationship drifting; the fight between two people he does not track is only in the tables.
            var notices = h.Signals.Notices(null, null, null, null, 50).Items;
            var drift = Assert.Single(notices);
            Assert.Equal(("relationship", "attention", Ana, "drifting"), (drift.Kind, drift.Severity, drift.PersonId, drift.Data.GetProperty("change").GetString()));
            Assert.Equal("Ana", drift.PersonName);

            // Communities: Klives's circle and the second group; Ben bridges them. Ids survive a rebuild.
            var communities = h.Insights!.Communities();
            Assert.Equal(2, communities.Communities.Count);
            var first = communities.Communities.Single(c => c.Top.Any(t => t.PersonId == K));
            var second = communities.Communities.Single(c => c.Top.Any(t => t.PersonId == "p-x0"));
            Assert.Contains(first.Top, t => t.PersonId == Ben);
            Assert.Equal(4, second.Size);
            var bridge = Assert.Single(communities.Bridges);
            Assert.Equal((Ben, first.CommunityId, second.CommunityId), (bridge.PersonId, bridge.CommunityId, bridge.BridgeTo));
            Assert.True(bridge.OutsideShare >= 0.25);
            Assert.NotNull(h.Runtime.Host.ReadModels.Get(InsightsModule.CommunitiesModel, "all"));

            // A pair in detail: its weeks, the closeness share, reply times each way.
            var pair = h.Insights.Pair(Cyd, Ben)!;
            Assert.Equal((Ben, Cyd), (pair.Summary.A, pair.Summary.B));
            Assert.Equal(Week(20), pair.Weeks[0].Week);
            var fightWeek = pair.Weeks.Single(w => w.Week == Week(3));
            Assert.Equal((4, 4, 5, 6), (fightWeek.FromA, fightWeek.FromB, fightWeek.Conflict, fightWeek.Negative));
            Assert.All(pair.Weeks, w => Assert.InRange(w.Closeness, 0, 1));
            Assert.Equal(64, pair.MedianReplySecondsFromA);
            Assert.Equal(2, pair.Events.Count);
            Assert.Null(h.Insights.Pair(Ana, "p-x3"));
            Assert.Throws<TacticRefusedException>(() => h.Insights.Pair(K, "p-kid"));
            Assert.Throws<ArgumentException>(() => h.Insights.Pair(K, K));

            var mine = h.Insights.PersonRelationships(K, 10);
            Assert.Equal(first.CommunityId, mine.CommunityId);
            Assert.Equal(new[] { Ben, Ana, "p-eve" }.OrderBy(x => x), mine.Relationships.Select(r => r.A == K ? r.B : r.A).OrderBy(x => x));
            Assert.Equal(Ben, mine.Relationships[0].A == K ? mine.Relationships[0].B : mine.Relationships[0].A);
            Assert.Throws<TacticRefusedException>(() => h.Insights.PersonRelationships("p-kid", 10));

            // Running it again finds nothing new and keeps every id.
            var again = await RebuildAsync(h);
            Assert.Equal(0, again.GetProperty("events").GetInt32());
            Assert.Equal(events, Events());
            Assert.Equal(communities.Communities.Select(c => c.CommunityId).OrderBy(x => x), h.Insights.Communities().Communities.Select(c => c.CommunityId).OrderBy(x => x));
            Assert.Single(h.Signals.Notices(null, null, null, null, 50).Items);

            // Someone joins the second group: it is still the same community.
            await AddPersonAsync(h, "p-x4");
            foreach (string x in new[] { "p-x0", "p-x1", "p-x2", "p-x3" }) await BothWaysAsync(h, "p-x4", x, 10, 0, 12);
            await RebuildAsync(h);
            var grown = h.Insights.Communities().Communities.Single(c => c.Top.Any(t => t.PersonId == "p-x4"));
            Assert.Equal((second.CommunityId, 5), (grown.CommunityId, grown.Size));

            // Two people Klives tracks: a change between them is a notice too (for information).
            await h.Runtime.Stores.Operator.WriteAsync((c, tx) =>
            {
                foreach (string p in new[] { Ben, Cyd })
                    c.Exec(tx, "INSERT INTO person_settings(person_id, tier, updated_at_ms) VALUES($p,'tracked',$t) ON CONFLICT(person_id) DO UPDATE SET tier='tracked'", ("$p", p), ("$t", h.Clock.NowMs));
            });
            await PairWeekAsync(h, Ben, Cyd, 0, 6, conflict: 5, negative: 5);
            await RebuildAsync(h);
            var tracked = h.Signals.Notices(null, null, null, null, 50).Items.Single(n => n.Data.GetProperty("change").GetString() == "fight");
            Assert.Equal(("info", Ben, Cyd, "listed"), (tracked.Severity, tracked.PersonId, tracked.OtherPersonId, tracked.Delivery));
        }

        [Fact]
        public async Task Tactics_InfluenceAndRelationshipWatch()
        {
            await using var h = await SignalsHarness.StartAsync(insights: true);
            await AddPersonAsync(h, "p-dan");
            await AddPersonAsync(h, "p-fan");
            await h.Driver.RunUntilIdleAsync(5_000);
            await BothWaysAsync(h, Cyd, "p-dan", 30, 0, 5);
            await BothWaysAsync(h, Ana, Cyd, 30, 0, 6);
            await BothWaysAsync(h, Ana, "p-dan", 4, 0, 3);
            for (int w = 12; w >= 0; w--) { await PairWeekAsync(h, "p-fan", Cyd, w, 6); await PairWeekAsync(h, Cyd, "p-fan", w, 1); }      // Fan turns to Cyd far more than Cyd turns back
            await BothWaysAsync(h, K, Ana, 34, 9, 10);
            await BothWaysAsync(h, K, Ben, 40, 0, 10);
            await RebuildAsync(h);

            var mine = h.Runtime.Tactics.Catalog().Where(d => d.Code is "X36" or "X45").OrderBy(d => d.Code, StringComparer.Ordinal).ToList();
            Assert.Equal(new[] { InfluenceTactic.Key, RelationshipWatchTactic.Key }, mine.Select(d => d.Key));
            Assert.All(mine, d => Assert.True(Omnipotent.Profiles.Permissions.PermissionCatalog.Get(d.RequiredPermission) != null, d.Key));

            async Task<List<TacticOutput>> RunAsync(string key, object? parameters, params Selection[] selections)
            {
                string runId = await h.Runtime.Tactics.StartAsync(key, new TacticInput(selections, parameters == null ? null : JsonSerializer.SerializeToElement(parameters)), "tester");
                await h.Driver.RunUntilIdleAsync();
                h.Driver.AssertNoFailures();
                var run = h.Runtime.Tactics.Get(runId)!;
                Assert.True(run.State == TacticRunStates.Succeeded, $"{key}: {run.State} {run.Error}");
                return JsonSerializer.Deserialize<List<TacticOutput>>(run.OutputsJson!, KernelJson.Options)!;
            }

            var influence = await RunAsync(InfluenceTactic.Key, null, Selection.Of(SelectionKind.Person, new[] { Cyd }));
            var report = influence[0].Data!.Value;
            var introduced = Assert.Single(report.GetProperty("introduced").EnumerateArray());
            Assert.Equal((Ana, "p-dan"), (introduced.GetProperty("a").GetString(), introduced.GetProperty("b").GetString()));
            var fan = Assert.Single(report.GetProperty("turnToThem").EnumerateArray());
            Assert.Equal(("p-fan", 78, 13), (fan.GetProperty("personId").GetString(), fan.GetProperty("toThem").GetInt32(), fan.GetProperty("fromThem").GetInt32()));
            Assert.Equal(0, report.GetProperty("theyTurnTo").GetArrayLength());
            Assert.Equal("p-fan", Assert.Single(influence[1].Selection!.Items).Id);
            await h.MarkMinorAsync(Cyd);
            await Assert.ThrowsAsync<TacticRefusedException>(() => h.Runtime.Tactics.StartAsync(InfluenceTactic.Key, new TacticInput(new[] { Selection.Of(SelectionKind.Person, new[] { Cyd }) }), "tester"));

            // X45: what changed for Klives lately, and the switch.
            var watch = await RunAsync(RelationshipWatchTactic.Key, null);
            Assert.True(watch[0].Data!.Value.GetProperty("enabled").GetBoolean());
            var change = Assert.Single(watch[1].Data!.Value.GetProperty("events").EnumerateArray());
            Assert.Equal(("drifting", Ana), (change.GetProperty("kind").GetString(), change.GetProperty("withPersonId").GetString()));
            var off = await RunAsync(RelationshipWatchTactic.Key, new { enabled = false });
            Assert.False(off[0].Data!.Value.GetProperty("enabled").GetBoolean());
            Assert.False(h.Settings.Get("insights.notify.relationships", true));
        }
    }
}
