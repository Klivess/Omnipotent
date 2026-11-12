using System.Globalization;
using System.Text.Json;
using Omnipotent.Services.OmniscienceV3.Kernel.Tactics;
using Omnipotent.Services.OmniscienceV3.Modules.Insights;
using Omnipotent.Services.OmniscienceV3.Storage;
using Omnipotent.Services.OmniscienceV3.Storage.Sqlite;

namespace Omnipotent.Tests.OmniscienceV3.WsC
{
    /// <summary>Similar people and their map (N16), and the layout mathematics under it. Synthetic data only.</summary>
    public class PeopleTests
    {
        // ── layout ───────────────────────────────────────────────────────────────

        private static List<float[]> Planted(int groups, int each, int dims, int seed)
        {
            var rng = new Random(seed);
            var centres = Enumerable.Range(0, groups).Select(_ => Enumerable.Range(0, dims).Select(_ => rng.NextDouble() * 2 - 1).ToArray()).ToList();
            var points = new List<float[]>();
            for (int g = 0; g < groups; g++)
                for (int i = 0; i < each; i++)
                {
                    var v = centres[g].Select(c => c + (rng.NextDouble() - 0.5) * 0.25).ToArray();
                    double norm = Math.Sqrt(v.Sum(x => x * x));
                    points.Add(v.Select(x => (float)(x / norm)).ToArray());
                }
            return points;
        }

        [Fact]
        public void Tsne_KeepsPlantedGroupsTogether_AndGivesTheSameMapEveryTime()
        {
            var points = Planted(groups: 4, each: 15, dims: 24, seed: 5);
            var map = Layout2D.Tsne(points);
            Assert.Equal(60, map.Length);
            Assert.All(map, p => Assert.True(double.IsFinite(p[0]) && double.IsFinite(p[1])));
            // Everyone's nearest neighbour on the map is from their own group.
            int right = 0;
            for (int i = 0; i < 60; i++)
            {
                int nearest = Enumerable.Range(0, 60).Where(j => j != i).OrderBy(j => Math.Pow(map[i][0] - map[j][0], 2) + Math.Pow(map[i][1] - map[j][1], 2)).First();
                if (nearest / 15 == i / 15) right++;
            }
            Assert.True(right >= 58, $"only {right} of 60 points have a neighbour from their own group");
            // Groups are apart: the distance between two group centres is larger than a group's own spread.
            var centres = Enumerable.Range(0, 4).Select(g => (X: map.Skip(g * 15).Take(15).Average(p => p[0]), Y: map.Skip(g * 15).Take(15).Average(p => p[1]))).ToList();
            double spread = Enumerable.Range(0, 60).Average(i => Math.Sqrt(Math.Pow(map[i][0] - centres[i / 15].X, 2) + Math.Pow(map[i][1] - centres[i / 15].Y, 2)));
            double apart = (from a in Enumerable.Range(0, 4) from b in Enumerable.Range(0, 4) where a < b select Math.Sqrt(Math.Pow(centres[a].X - centres[b].X, 2) + Math.Pow(centres[a].Y - centres[b].Y, 2))).Min();
            Assert.True(apart > 3 * spread, $"groups {apart:F2} apart with a spread of {spread:F2}");

            // The same input gives the same map, however many threads work on it.
            var again = Layout2D.Tsne(points);
            var single = Layout2D.Tsne(points, maxThreads: 1);
            for (int i = 0; i < 60; i++)
            {
                Assert.Equal(map[i], again[i]);
                Assert.Equal(map[i], single[i]);
            }

            // Too few points for a neighbourhood: the principal components, no failure. Nothing: nothing.
            Assert.Equal(3, Layout2D.Tsne(points.Take(3).ToList()).Length);
            Assert.Empty(Layout2D.Tsne(new List<float[]>()));
            // Identical points do not break it.
            var same = Enumerable.Repeat(points[0], 8).ToList();
            Assert.All(Layout2D.Tsne(same), p => Assert.True(double.IsFinite(p[0]) && double.IsFinite(p[1])));
        }

        [Fact]
        public void Pca_FindsTheDirectionsOfMostSpread()
        {
            // Points along a line in 5 dimensions, with a little spread across it.
            var rng = new Random(2);
            var rows = Enumerable.Range(0, 200).Select(_ =>
            {
                double t = rng.NextDouble() * 10 - 5, u = rng.NextDouble() - 0.5;
                return new[] { (float)(t * 0.6), (float)(t * 0.8), (float)u, 0f, 0f };
            }).ToList();
            var projected = Layout2D.Pca(rows);
            double var1 = projected.Average(p => p[0] * p[0]) - Math.Pow(projected.Average(p => p[0]), 2), var2 = projected.Average(p => p[1] * p[1]) - Math.Pow(projected.Average(p => p[1]), 2);
            Assert.InRange(var1, 7.5, 9.5);     // the variance of a uniform spread over 10 is 100/12
            Assert.InRange(var2, 0.06, 0.11);   // and over 1 is 1/12
        }

        [Fact]
        public void Procrustes_RecoversATurnAMirrorAScaleAndAShift()
        {
            var rng = new Random(9);
            var from = Enumerable.Range(0, 20).Select(_ => (X: rng.NextDouble() * 4 - 2, Y: rng.NextDouble() * 4 - 2)).ToList();
            foreach (bool mirror in new[] { false, true })
            {
                double angle = 40 * Math.PI / 180, scale = 2.5;
                (double X, double Y) Move((double X, double Y) p)
                {
                    double y = mirror ? -p.Y : p.Y;
                    return (scale * (Math.Cos(angle) * p.X - Math.Sin(angle) * y) + 7, scale * (Math.Sin(angle) * p.X + Math.Cos(angle) * y) - 3);
                }
                var to = from.Select(Move).ToList();
                var align = Layout2D.Procrustes(from, to);
                foreach (var (p, q) in from.Zip(to))
                {
                    var moved = align(p.X, p.Y);
                    Assert.Equal(q.X, moved.X, 9);
                    Assert.Equal(q.Y, moved.Y, 9);
                }
                // A point that was not used for the fit goes where the same movement puts it.
                var extra = align(0.5, -1.25);
                Assert.Equal(Move((0.5, -1.25)).X, extra.X, 9);
            }
            // Noise: the fit is the best one, not an exact one, and it does not mirror a map that was only nudged.
            var nudged = from.Select(p => (p.X + (rng.NextDouble() - 0.5) * 0.05, p.Y + (rng.NextDouble() - 0.5) * 0.05)).ToList();
            var gentle = Layout2D.Procrustes(nudged, from);
            Assert.All(nudged.Zip(from), pair => Assert.True(Math.Abs(gentle(pair.First.Item1, pair.First.Item2).X - pair.Second.X) < 0.06));
            // With fewer than three points in common there is nothing to align to.
            Assert.Equal((1.5, 2.5), Layout2D.Procrustes(from.Take(2).ToList(), from.Take(2).ToList())(1.5, 2.5));
        }

        // ── similar people and the map ───────────────────────────────────────────

        private const long Day = 86_400_000L;

        private sealed record Kind(string Name, int Words, int[] Hours, double Replies, double Links, string Guild, string Tone, int PerDay);

        private static readonly Kind Owls = new("owl", 40, new[] { 0, 1, 2, 3, 23 }, 0.7, 0.02, "discord:g1", "negative", 12);
        private static readonly Kind Larks = new("lark", 6, new[] { 6, 7, 8, 9 }, 0.1, 0.4, "discord:g2", "positive", 3);
        private static readonly Kind Middles = new("mid", 15, new[] { 12, 13, 14, 19, 20 }, 0.35, 0.1, "discord:g3", "neutral", 6);

        /// <summary>Forty days of one person's rollups, written as the rollups would have, in the manner of their kind.</summary>
        private static Task PlantAsync(SignalsHarness h, string person, Kind kind, Random rng) => h.Runtime.Stores.Intel.WriteAsync((c, tx) =>
        {
            var today = DateOnly.FromDateTime(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(h.Clock.NowMs));
            for (int d = 1; d <= 40; d++)
            {
                int msgs = kind.PerDay + rng.Next(2);
                var hours = new int[24];
                for (int i = 0; i < msgs; i++) hours[kind.Hours[rng.Next(kind.Hours.Length)]]++;
                int words = msgs * (kind.Words + rng.Next(-1, 2));
                int answered = msgs;
                var sentiment = new Dictionary<string, int> { ["negative"] = 0, ["neutral"] = 0, ["positive"] = 0 };
                sentiment[kind.Tone] = answered - 1;
                sentiment["neutral"] += 1;
                c.Exec(tx, """
                    INSERT INTO rollup_person_day(person_id, day, zone, zone_known, msgs, authored, deleted, edited, replies, forwarded, non_authored, with_attachments, with_links, chars, words,
                        dm_msgs, group_msgs, server_msgs, day_only, reactions_given, mentions_made, hours, labels, conversations, guilds, labelset, source_seq, computed_at_ms)
                    VALUES($p,$d,'UTC',0,$m,$m,0,0,$r,0,0,0,$l,$ch,$w,0,0,$m,0,0,0,$h,$lab,'{}',$g,1,1,$n)
                    """, ("$p", person), ("$d", today.AddDays(-d).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("$m", msgs), ("$r", (int)Math.Round(msgs * kind.Replies)),
                    ("$l", (int)Math.Round(msgs * kind.Links)), ("$ch", words * 5), ("$w", words), ("$h", JsonSerializer.Serialize(hours)),
                    ("$lab", JsonSerializer.Serialize(new { sentiment })), ("$g", JsonSerializer.Serialize(new Dictionary<string, int> { [kind.Guild] = msgs })), ("$n", h.Clock.NowMs));
            }
        });

        private static async Task RebuildAsync(SignalsHarness h)
        {
            await h.Insights!.RefreshAsync(CancellationToken.None);
            await h.Driver.RunUntilIdleAsync(5_000);
            h.Driver.AssertNoFailures();
        }

        private static async Task<List<(string Person, Kind Kind)>> SeedAsync(SignalsHarness h, int each)
        {
            var rng = new Random(3);
            var people = new List<(string, Kind)>();
            int n = 300;
            await h.Runtime.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "INSERT INTO classify_labelsets(labelset, state, encoder, heads_json, fusion_json, created_at_ms) VALUES(1,'active','test',$h,$f,$n)",
                ("$h", """{"single":{},"context":{}}"""), ("$f", """{"tasks":{},"llmReliability":{}}"""), ("$n", h.Clock.NowMs)));
            foreach (var kind in new[] { Owls, Larks, Middles })
                for (int i = 1; i <= each; i++)
                {
                    string person = $"p-{kind.Name}{i}";
                    await h.AddPersonAsync("discord:" + (++n), person, char.ToUpperInvariant(kind.Name[0]) + kind.Name[1..] + " " + i);
                    await PlantAsync(h, person, kind, rng);
                    people.Add((person, kind));
                }
            return people;
        }

        [Fact]
        public async Task SimilarPeople_AreTheOnesWhoWriteAlike_AndSayWhy()
        {
            await using var h = await SignalsHarness.StartAsync(insights: true);
            var people = await SeedAsync(h, each: 6);
            var rng = new Random(11);
            // Like the owls in every way, but not to be compared: a minor, someone whose analysis is off, someone with too few messages.
            await h.AddPersonAsync("discord:401", "p-kid", "Kid");
            await PlantAsync(h, "p-kid", Owls, rng);
            await h.MarkMinorAsync("p-kid");
            await h.AddPersonAsync("discord:402", "p-off", "Off");
            await PlantAsync(h, "p-off", Owls, rng);
            await h.Runtime.Stores.Operator.WriteAsync((c, tx) => AnalysisOff.Set(c, tx, new[] { "p-off" }, h.Clock.NowMs));
            await h.AddPersonAsync("discord:403", "p-few", "Few");
            await h.Runtime.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, """
                INSERT INTO rollup_person_day(person_id, day, zone, zone_known, msgs, authored, deleted, edited, replies, forwarded, non_authored, with_attachments, with_links, chars, words,
                    dm_msgs, group_msgs, server_msgs, day_only, reactions_given, mentions_made, hours, labels, conversations, guilds, labelset, source_seq, computed_at_ms)
                VALUES('p-few','2025-05-20','UTC',0,10,10,0,0,0,0,0,0,0,400,80,0,0,10,0,0,0,'[10,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0]','{}','{}','{}',1,1,$n)
                """, ("$n", h.Clock.NowMs)));
            // Two owls also write about the same thing, and one lark about something else.
            foreach (string text in new[] { "the telescope mirror needs collimating again", "new eyepiece for the telescope arrived", "clear sky tonight, telescope out" })
            {
                await h.PostAsync(SignalsHarness.General, "discord:301", text, embed: true);
                await h.PostAsync(SignalsHarness.General, "discord:302", text + " finally", embed: true);
            }
            foreach (string text in new[] { "fed the sourdough starter", "this flour makes a better sourdough loaf", "sourdough crumb came out open" })
                await h.PostAsync(SignalsHarness.General, "discord:307", text, embed: true);

            await RebuildAsync(h);

            var result = h.Insights!.Similar("p-owl1", limit: 5);
            Assert.Equal(("p-owl1", "Owl 1", null), (result.PersonId, result.Name, result.Note));
            Assert.Equal(5, result.People.Count);
            // The five most similar are the five other owls: never the minor, the switched-off one, or the one with too few messages.
            Assert.Equal(new[] { "p-owl2", "p-owl3", "p-owl4", "p-owl5", "p-owl6" }, result.People.Select(p => p.PersonId).OrderBy(p => p, StringComparer.Ordinal));
            Assert.Equal(result.People.Select(p => p.Similarity).OrderByDescending(s => s), result.People.Select(p => p.Similarity));
            // The one who also writes about the same thing comes first, and the reason is visible: the content part.
            Assert.Equal("p-owl2", result.People[0].PersonId);
            Assert.True(result.People[0].Content > 0.6 && result.People[0].Content > result.People[1].Content + 0.3);
            Assert.All(result.People, p =>
            {
                Assert.InRange(p.Similarity, 0.3, 1.0001);
                Assert.True(p.Style > 0.6 && p.Rhythm > 0.8 && p.Tone > 0.8, $"{p.PersonId}: style {p.Style}, rhythm {p.Rhythm}, tone {p.Tone}");
                // They explain themselves: what both have in common that most people do not.
                Assert.Contains(p.Shared, t => t.Trait == "words_per_message" && t.Text == "long messages" && t.Mine > 1 && t.Theirs > 1);
                Assert.Contains(p.Shared, t => t.Trait == "night_share" && t.Text == "up at night");
                Assert.Equal(new[] { "Server 1" }, p.SharedPlaces);
            });
            Assert.Contains(result.Traits, t => t.Trait == "words_per_message" && t.Text == "long messages" && t.Z > 1);
            Assert.Contains(result.Traits, t => t.Trait == "tone:sentiment:negative" && t.Text == "often negative (sentiment)");

            // A lark's matches are larks, and what they share reads the other way round.
            var lark = h.Insights.Similar("p-lark2", limit: 3);
            Assert.All(lark.People, p => Assert.StartsWith("p-lark", p.PersonId));
            Assert.Contains(lark.People[0].Shared, t => t.Text is "short messages" or "many links" or "mornings");
            // Across kinds the similarity is low.
            var all = h.Insights.Similar("p-owl1", limit: 50);
            Assert.Equal(17, all.People.Count);
            Assert.True(all.People.Where(p => p.PersonId.StartsWith("p-lark", StringComparison.Ordinal)).Max(p => p.Similarity) < all.People.Where(p => p.PersonId.StartsWith("p-owl", StringComparison.Ordinal)).Min(p => p.Similarity));

            // Who cannot be compared, and why.
            Assert.Contains("under 18", Assert.Throws<TacticRefusedException>(() => h.Insights.Similar("p-kid")).Message);
            Assert.Contains("switched off", Assert.Throws<TacticRefusedException>(() => h.Insights.Similar("p-off")).Message);
            var few = h.Insights.Similar("p-few");
            Assert.Empty(few.People);
            Assert.Contains("at least 50 messages", few.Note);
            Assert.Throws<KeyNotFoundException>(() => h.Insights.Similar("p-nobody"));

            // Someone marked a minor after the build is no longer offered as a match.
            await h.MarkMinorAsync("p-owl3");
            Assert.DoesNotContain(h.Insights.Similar("p-owl1", limit: 5).People, p => p.PersonId == "p-owl3");
        }

        [Fact]
        public async Task TheMap_PutsAlikePeopleTogether_KeepsItsShapeFromNightToNight_AndPlacesTheRestNearTheirKind()
        {
            await using var h = await SignalsHarness.StartAsync(insights: true);
            await h.Settings.SetAsync("insights.map.max_people", 50, "test");
            var people = await SeedAsync(h, each: 20);
            await RebuildAsync(h);

            var map = h.Insights!.PeopleMap();
            Assert.Equal((60, 60), (map.People, map.Points.Count));
            Assert.Equal(50, h.Intel("SELECT COUNT(*) FROM ins_people WHERE mapped=1"));
            var at = map.Points.ToDictionary(p => p.PersonId, p => (p.X, p.Y));
            Assert.All(map.Points, p => Assert.InRange(Math.Sqrt(p.X * p.X + p.Y * p.Y), 0, 1.0001));
            var kindOf = people.ToDictionary(p => p.Person, p => p.Kind.Name);
            (double X, double Y) Centre(string kind, Dictionary<string, (double X, double Y)> positions) =>
                (positions.Where(p => kindOf[p.Key] == kind).Average(p => p.Value.X), positions.Where(p => kindOf[p.Key] == kind).Average(p => p.Value.Y));
            static double Distance((double X, double Y) a, (double X, double Y) b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
            // Everyone, including the ten the map did not lay out itself, is nearest to the centre of their own kind.
            var centres = new[] { "owl", "lark", "mid" }.ToDictionary(k => k, k => Centre(k, at));
            Assert.All(at, p => Assert.Equal(kindOf[p.Key], centres.OrderBy(c => Distance(c.Value, p.Value)).First().Key));
            Assert.Equal("Owl 1", map.Points.Single(p => p.PersonId == "p-owl1").Name);

            // The next night, with one more day written by everyone: the map is the same map (not turned, not mirrored).
            h.Clock.Advance(TimeSpan.FromDays(1));
            var rng = new Random(21);
            foreach (var (person, kind) in people)
                await h.Runtime.Stores.Intel.WriteAsync((c, tx) => c.Exec(tx, "UPDATE rollup_person_day SET words = words + $w WHERE person_id=$p AND day=(SELECT MAX(day) FROM rollup_person_day WHERE person_id=$p)",
                    ("$w", rng.Next(0, 6)), ("$p", person)));
            await RebuildAsync(h);
            var later = h.Insights.PeopleMap().Points.ToDictionary(p => p.PersonId, p => (p.X, p.Y));
            double moved = at.Average(p => Distance(p.Value, later[p.Key]));
            Assert.True(moved < 0.15, $"people moved {moved:F3} on average on a map two wide");
            foreach (string kind in new[] { "owl", "lark", "mid" }) Assert.True(Distance(centres[kind], Centre(kind, later)) < 0.15, kind);

            // Marked a minor since the map was built: off the map at once.
            await h.MarkMinorAsync("p-lark5");
            Assert.DoesNotContain(h.Insights.PeopleMap().Points, p => p.PersonId == "p-lark5");
        }
    }
}
