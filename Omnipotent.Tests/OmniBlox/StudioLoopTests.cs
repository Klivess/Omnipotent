using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox.Agents;
using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Pipeline;

namespace Omnipotent.Tests.OmniBlox
{
    /// <summary>The kept facts: keyed, de-duplicated, the owner's protected, old dossiers still readable.</summary>
    public class FactBookTests
    {
        private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

        private static KeyFact F(string text, string source = FactBook.Agent) => FactBook.Parse(text, Now, GameStage.Build, source);

        [Fact]
        public void AKeyedFact_ReplacesItsOlderSelf_AndMovesToTheEnd()
        {
            var book = new List<KeyFact>();
            FactBook.Merge(book, new[] { F("economy: axes cost 15/35/80"), F("spawn: s1 faces the shop") });
            FactBook.Merge(book, new[] { F("Economy: axes cost 20/50/120") });
            Assert.Equal(2, book.Count);
            Assert.Equal("economy", book[^1].Key);
            Assert.Equal("axes cost 20/50/120", book[^1].Text);
            Assert.Equal("spawn", book[0].Key);
        }

        [Fact]
        public void TheSameFactRestated_ReplacesInsteadOfPilingUp()
        {
            var book = new List<KeyFact>();
            FactBook.Merge(book, new[] { F("The hub sits at the centre of the map with the shop and the sell pad") });
            FactBook.Merge(book, new[] { F("The hub sits at the centre of the map with the shop and a sell pad") });
            Assert.Single(book);
            Assert.EndsWith("a sell pad", book[0].Text);
            // Saying nothing new leaves it where it is.
            Assert.Equal(0, FactBook.Merge(book, new[] { F("The hub sits at the centre of the map with the shop and a sell pad") }));
        }

        [Fact]
        public void TheOwnersFacts_AreNeverPushedOutByAnAgents()
        {
            var book = new List<KeyFact> { F("direction: keep the first stages easy", FactBook.Owner) };
            FactBook.Merge(book, new[] { F("direction: make stage 2 harder") });
            // The owner's words stay exactly as they were; the agent's note sits beside them rather than over them.
            Assert.Contains(book, f => f.Source == FactBook.Owner && f.Text == "keep the first stages easy");
            FactBook.Merge(book, new[] { F("direction: keep the first stages easy") });
            Assert.Equal(1, book.Count(f => f.Text == "keep the first stages easy"));

            // Full: the agent's oldest facts go first, the owner's stay.
            FactBook.Merge(book, Enumerable.Range(1, 45).Select(i => F($"note{i}: fact number {i}")), capacity: 40);
            Assert.Equal(40, book.Count);
            Assert.Contains(book, f => f.Source == FactBook.Owner);
            Assert.DoesNotContain(book, f => f.Key == "note1");
            Assert.Contains(book, f => f.Key == "note45");
        }

        [Fact]
        public void DroppedKeys_RemoveOnlyAnAgentsFacts()
        {
            var book = new List<KeyFact> { F("map: 400 studs"), F("map2: owner says big", FactBook.Owner) };
            FactBook.Merge(book, Array.Empty<KeyFact>(), new[] { "MAP", "map2" });
            Assert.Single(book);
            Assert.Equal(FactBook.Owner, book[0].Source);
        }

        [Fact]
        public void OldDossiers_WithPlainStringFacts_StillLoad_AndSaveAsObjects()
        {
            var game = JsonConvert.DeserializeObject<GameDossier>("{\"GameId\":\"g1\",\"KeyFacts\":[\"Experience 1001, place 2002\",\"economy: axes cost 15\",{\"Key\":\"map\",\"Text\":\"400 studs\",\"Source\":\"owner\"}," +
                "\"Owner's direction (2026-10-20): make the map bigger\"]}")!;
            Assert.Equal(4, game.KeyFacts.Count);
            Assert.Equal("economy", game.KeyFacts[1].Key);
            Assert.Equal(FactBook.Owner, game.KeyFacts[2].Source);
            Assert.Equal(FactBook.Owner, game.KeyFacts[3].Source); // the old steering format is recognised as the owner's
            Assert.Equal("make the map bigger", game.KeyFacts[3].Text);
            var saved = JObject.Parse(JsonConvert.SerializeObject(game));
            Assert.All((JArray)saved["KeyFacts"]!, f => Assert.Equal(JTokenType.Object, f.Type));
        }
    }

    /// <summary>List arguments: what models actually send.</summary>
    public class ArgsTests
    {
        [Fact]
        public void AJsonArrayWrittenAsAString_IsUnwrapped()
        {
            var a = JObject.Parse("{\"tips\": \"[\\\"Chop trees\\\", \\\"Sell logs\\\"]\"}");
            Assert.Equal(new[] { "Chop trees", "Sell logs" }, Args.List(a, "tips"));
        }

        [Fact]
        public void ShortItems_SplitOnCommas_ButSentencesNeverDo()
        {
            Assert.Equal(new[] { "red", "green", "blue" }, Args.List(JObject.Parse("{\"x\":\"red, green, blue\"}"), "x"));
            Assert.Single(Args.List(JObject.Parse("{\"x\":\"The hub, with the shop and the sell pad in it, faces north\"}"), "x"));
            Assert.Single(Args.List(JObject.Parse("{\"x\":\"Axe prices: [16, 35, 80]\"}"), "x")); // the first studio made three facts of this
            Assert.Single(Args.Prose(JObject.Parse("{\"x\":\"Chop 5 trees, then sell\"}"), "x"));
            Assert.Equal(new[] { "one", "two" }, Args.Prose(JObject.Parse("{\"x\":\"- one\\n- two\"}"), "x"));
        }
    }

    /// <summary>The watchdog: progress, not cycle counts.</summary>
    public class WatchdogTests
    {
        [Fact]
        public void TheSameFailure_IsRecognised_AcrossRevisionsAndTimes()
        {
            string? a = Watchdog.Signature(new[] { "engine game: Forest · chopping pays failed on r12 at 2026-10-21 10:00" });
            string? b = Watchdog.Signature(new[] { "engine game: Forest · chopping pays failed on r13 at 2026-10-21 11:42" });
            Assert.NotNull(a);
            Assert.Equal(a, b);
            Assert.NotEqual(a, Watchdog.Signature(new[] { "engine game: Shop · buying works failed on r13" }));
            Assert.Null(Watchdog.Signature(Array.Empty<string>()));
        }

        [Fact]
        public async Task Progress_ResetsTheLadder_AndRepeatsClimbIt()
        {
            using var h = new StudioHarness();
            string id = h.Orchestrator.StartGame();
            for (int i = 0; i < Watchdog.RethinkAt; i++) Watchdog.Observe(h.Ctx, id, new[] { "the same error" }, "a test");
            var game = h.Game(id);
            Assert.Equal(1, game.Progress.Level);
            Assert.Contains("rethink", game.Progress.Directive);
            Assert.Contains("the same error", game.Progress.Directive);

            // The game moves (its files change) and nothing fails: back to level 0, the directive goes.
            h.Ctx.Update(id, g => g.WorkingTitle = "moved");
            h.Ctx.Update(id, g => g.Concepts.Add(new GameConcept { ConceptId = "c1", Pitch = "x" }));
            Assert.Equal(0, Watchdog.Observe(h.Ctx, id, Array.Empty<string>(), "a cycle"));
            Assert.Null(h.Game(id).Progress.Directive);
            await Task.CompletedTask;
        }
    }

    /// <summary>The generated terrain: deterministic, encoded the way the kit decodes it, beaches only by water.</summary>
    public class TerrainTests
    {
        private static TerrainRecipe Recipe(params TerrainOp[] ops) => new() { Size = new[] { 320f, 320f }, Biome = "meadow", Hills = 12, Seed = 5, FlatRadius = 80, Ops = ops.ToList() };

        [Fact]
        public void TheSameRecipe_AlwaysMakesTheSameGround_AndTheCoreIsFlat()
        {
            var a = TerrainBuilder.Build(Recipe());
            var b = TerrainBuilder.Build(Recipe());
            Assert.Equal(a.Heights, b.Heights);
            Assert.Equal(0, a.HeightAt(0, 0), 2);
            Assert.True(a.HeightAt(150, 150) > 10, "the border mountains rise at the edge");
        }

        [Fact]
        public void TheHeightmap_DecodesTheWayTheKitReadsIt()
        {
            var map = TerrainBuilder.Build(Recipe(new TerrainOp { Kind = "raise", Center = new[] { 40f, 40f }, Radius = 30, Height = 18 }));
            string encoded = map.EncodeHeights();
            Assert.Equal(map.Heights.Length * 2, encoded.Length);
            const string b64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
            for (int i = 0; i < map.Heights.Length; i += 97)
            {
                // GameKit/Server/World.luau decodeHeights: floor + (a * 64 + b) * 0.5
                float decoded = map.Floor + (b64.IndexOf(encoded[i * 2]) * 64 + b64.IndexOf(encoded[i * 2 + 1])) * 0.5f;
                Assert.True(MathF.Abs(decoded - map.Heights[i]) <= 0.26f, $"cell {i}: {decoded} vs {map.Heights[i]}");
            }
            var config = map.ToConfig();
            Assert.Equal("heightmap", config["Kind"]);
            Assert.Equal(map.Floor, config["Floor"]);
        }

        [Fact]
        public void APond_GetsABeach_ButTheRestOfTheMeadowStaysGrass()
        {
            var map = TerrainBuilder.Build(Recipe(new TerrainOp { Kind = "lake", Center = new[] { 60f, 60f }, Radius = 24, Height = 6 }));
            Assert.True(map.InWater(60, 60));
            Assert.Equal("Grass", map.MaterialAt(-40, -40));
            Assert.Equal("Grass", map.MaterialAt(0, 0));
            string shore = map.MaterialAt(60 + 26, 60);
            Assert.True(shore is "Sand" or "Grass", shore);
        }
    }
}
