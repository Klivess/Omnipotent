using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox.Assets;
using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Build.WorldGen;
using Omnipotent.Services.OmniBlox.Core;
using Omnipotent.Services.OmniBlox.Pipeline;

namespace Omnipotent.Tests.OmniBlox
{
    /// <summary>
    /// The world generator, the map report, the walking check and the economy bot, on worlds made from a small
    /// part-built library (<see cref="TestModels"/>): what the generator makes stands on the ground, faces the right
    /// way, can be walked and played through in order; what the first studio shipped is blocked.
    /// </summary>
    public class WorldGenTests
    {
        private sealed class World : IDisposable
        {
            public required TempDir Dir { get; init; }
            public required AssetLibrary Library { get; init; }
            public required GameManifest Manifest { get; init; }
            public required GenerationResult Result { get; init; }
            public required List<GeometryIssue> Geometry { get; init; }
            public required MapAnalysis Map { get; init; }
            public SceneDocument Scene => Result.Scene;

            public void Dispose()
            {
                Library.Dispose();
                Dir.Dispose();
            }
        }

        private static World Make(string template, WorldSpec spec)
        {
            var dir = new TempDir();
            var library = TestModels.Library(Path.Combine(dir.Path, "assets"));
            var catalog = TestModels.Catalog(library);
            var manifest = new GameManifest { Title = "Test", Template = template, Rebirth = template == "simulator" ? new RebirthSpec() : null };
            var result = WorldGenerator.Generate(spec, manifest, catalog);
            var ws = new GameWorkspace(Path.Combine(dir.Path, "ws"), Path.Combine(dir.Path, "snap"));
            ws.EnsureCreated(manifest);
            ws.SaveScene(result.Scene);
            var compiled = TestModels.Compile(library, ws, template);
            Assert.True(compiled.Ok, string.Join("\n", compiled.Errors));
            var geometry = GeometryVerifier.Verify(compiled.Parts, result.Scene, manifest, compiled.Terrain, catalog.Get);
            var map = MapReport.Analyze(result.Scene, compiled.Parts, compiled.Terrain, manifest, geometry, catalog.Get);
            return new World { Dir = dir, Library = library, Manifest = manifest, Result = result, Geometry = geometry, Map = map };
        }

        private static WorldSpec Hub(int seed = 11) => new()
        {
            Layout = "hub", Biome = "forest", Seed = seed, Hub = new HubSpec { Name = "Lumber Camp" }, Zones = WorldGenerator.DefaultZones("forest", "tree"),
        };

        private static string Cfg(IDictionary<string, object> config, string key) =>
            config.TryGetValue(key, out var v) ? Convert.ToString(v is JValue jv ? jv.Value : v, CultureInfo.InvariantCulture) ?? "" : "";

        private static double Num(IDictionary<string, object> config, string key) =>
            double.TryParse(Cfg(config, key), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : double.NaN;

        private static float AngleDiff(float a, float b) => MathF.Abs(((a - b) % 360 + 540) % 360 - 180);

        private static void AssertGroundedAndAboveTheBar(World w)
        {
            Assert.DoesNotContain(w.Geometry, g => g.IsError);
            var misplaced = w.Geometry.Where(g => g.Message.Contains("floats") || g.Message.Contains("is sunk")).ToList();
            Assert.True(misplaced.Count == 0, string.Join("\n", misplaced));
            Assert.False(w.Map.Blocked, w.Map.Describe());
            Assert.True(w.Map.Score >= MapReport.Bar, w.Map.Describe());
        }

        [Fact]
        public void TheSameSpec_MakesTheSameWorld_AndAnotherSeedAnotherLayout()
        {
            using var dir = new TempDir();
            using var library = TestModels.Library(Path.Combine(dir.Path, "assets"));
            var catalog = TestModels.Catalog(library);
            var manifest = new GameManifest { Title = "T", Template = "simulator", Rebirth = new RebirthSpec() };
            string Json(int seed) => JsonConvert.SerializeObject(WorldGenerator.Generate(Hub(seed), manifest, catalog).Scene);
            Assert.Equal(Json(7), Json(7));
            Assert.NotEqual(Json(7), Json(8));
        }

        [Fact]
        public void AGeneratedSimulator_StandsOnTheGround_FacesTheHub_AndEveryZoneIsGatedStockedAndReachable()
        {
            using var w = Make("simulator", Hub());
            AssertGroundedAndAboveTheBar(w);
            Assert.True(w.Result.Models.Count >= 6, "it dresses with many kinds of model: " + string.Join(", ", w.Result.Models.Keys));

            var zones = w.Scene.Spec!.Zones.OrderBy(z => z.Order).ToList();
            Assert.Equal(4, zones.Count);
            Assert.Equal(0, zones[0].Price);
            var gates = w.Scene.Objects.Where(o => o.Tags.Contains("ZoneGate")).ToDictionary(o => Cfg(o.Config, "Zone"));
            Assert.DoesNotContain(zones[0].Name, gates.Keys);
            double last = 0;
            foreach (var zone in zones.Skip(1))
            {
                Assert.True(gates.TryGetValue(zone.Name, out var gate), $"{zone.Name} has no gate");
                double price = Num(gate!.Config, "Price");
                Assert.True(price > last, $"{zone.Name} costs {price}, not more than the zone before it ({last})");
                last = price;
                Assert.True(AngleDiff(gate.Yaw, WorldGenerator.YawToward(gate.Position[0], gate.Position[2], 0, 0)) < 2, $"the gate of {zone.Name} does not face the hub");
            }

            // Stands face the hub's middle, where players come from.
            var stands = w.Scene.Objects.Where(o => o.Tags.Contains("Stand")).ToList();
            Assert.True(stands.Count >= 2);
            Assert.All(stands, s => Assert.True(AngleDiff(s.Yaw, WorldGenerator.YawToward(s.Position[0], s.Position[2], 0, 0)) < 2, s.Name + " faces away"));

            // Every zone has its nodes, each a real zone's; all of them can be walked to; the sell pad is by the spawn.
            var nodeZones = w.Scene.Objects.Where(o => o.Tags.Contains("Resource")).Select(o => Cfg(o.Config, "Zone"))
                .Concat(w.Scene.Folders.Where(f => f.Value.Tags.Contains("Resource")).Select(f => Cfg(f.Value.Config, "Zone"))).ToList();
            foreach (var zone in zones) Assert.True(nodeZones.Count(z => z == zone.Name) >= 8, $"{zone.Name} has {nodeZones.Count(z => z == zone.Name)} nodes");
            Assert.All(nodeZones, z => Assert.Contains(z, zones.Select(x => x.Name)));
            var play = w.Map.Play!;
            Assert.True(play.Ok, play.Describe());
            Assert.Equal(play.Nodes, play.NodesReachable);
            var spawn = Assert.Single(w.Scene.Objects, o => o.Kind == SceneKinds.Spawn);
            var sell = Assert.Single(w.Scene.Objects, o => o.Tags.Contains("SellZone"));
            Assert.True(MathF.Sqrt(MathF.Pow(spawn.Position[0] - sell.Position[0], 2) + MathF.Pow(spawn.Position[2] - sell.Position[2], 2)) < 50, "the sell pad is far from the spawn");
        }

        [Fact]
        public void AGeneratedObby_HasEveryStageOnceAndInOrder_AFinish_AKillFloor_AndASpawnLookingDownTheCourse()
        {
            using var w = Make("obby", new WorldSpec { Layout = "course", Biome = "meadow", Seed = 3, Course = new CourseSpec { Stages = 20, Themes = { "meadow", "candy", "ice", "lava" } } });
            AssertGroundedAndAboveTheBar(w);
            var checkpoints = w.Scene.Objects.Where(o => o.Tags.Contains("Checkpoint")).OrderBy(o => Num(o.Config, "Stage")).ToList();
            Assert.Equal(Enumerable.Range(1, 20).Select(i => (double)i), checkpoints.Select(c => Num(c.Config, "Stage")));
            Assert.Single(w.Scene.Objects, o => o.Tags.Contains("Finish"));
            Assert.Contains(w.Scene.Objects, o => o.Tags.Contains("KillBrick"));
            var spawn = Assert.Single(w.Scene.Objects, o => o.Kind == SceneKinds.Spawn);
            var first = checkpoints[0];
            Assert.True(AngleDiff(spawn.Yaw, WorldGenerator.YawToward(spawn.Position[0], spawn.Position[2], first.Position[0], first.Position[2])) < 30, "the spawn does not look at stage 1");
        }

        [Fact]
        public void AGeneratedTycoon_GivesEveryPlayerAPlot_WithButtonsThatUnlockRealItemsInPriceOrder()
        {
            using var w = Make("tycoon", new WorldSpec { Layout = "plots", Biome = "meadow", Seed = 5, Plots = new PlotsSpec { Count = 4, Theme = "lumber" } });
            AssertGroundedAndAboveTheBar(w);
            var plots = w.Scene.Folders.Where(f => f.Value.Tags.Contains("TycoonPlot")).Select(f => f.Key).ToList();
            Assert.Equal(4, plots.Count);
            foreach (string plot in plots)
            {
                var inside = w.Scene.Objects.Where(o => o.Folder == plot || o.Folder.StartsWith(plot + "/", StringComparison.Ordinal)).ToList();
                Assert.Contains(inside, o => o.Name == "OwnerSign");
                Assert.Contains(inside, o => o.Name == "PlotSpawn");
                Assert.Contains(inside, o => o.Tags.Contains("TycoonCollector"));
                var items = w.Scene.Folders.Keys.Where(k => k.StartsWith(plot + "/Items/", StringComparison.Ordinal)).Select(k => k[(plot.Length + 7)..]).ToHashSet();
                var buttons = inside.Where(o => o.Tags.Contains("TycoonButton")).OrderBy(o => Num(o.Config, "Price")).ToList();
                Assert.True(buttons.Count >= 10, $"{plot} has {buttons.Count} buttons");
                for (int i = 0; i < buttons.Count; i++)
                {
                    Assert.Contains(Cfg(buttons[i].Config, "Unlocks"), items);
                    if (i > 0) Assert.Equal(Cfg(buttons[i - 1].Config, "Unlocks"), Cfg(buttons[i].Config, "Requires"));
                }
            }
        }

        [Fact]
        public void AFlatStripOfGroundFloatingInTheSky_IsBlocked()
        {
            // The first studio's "Chop the Forest": a 140 × 400 slab of grass, a few bushes, the spawn at one end.
            using var dir = new TempDir();
            var ws = new GameWorkspace(Path.Combine(dir.Path, "ws"), Path.Combine(dir.Path, "snap"));
            var manifest = new GameManifest { Title = "Chop", Template = "simulator" };
            ws.EnsureCreated(manifest);
            var scene = new SceneDocument();
            scene.Bounds.Area = new[] { -70f, -200, 70, 200 };
            scene.Terrain.Add(new TerrainFill { Shape = "block", Material = "Grass", Center = new[] { 0f, -4, 0 }, Size = new[] { 140f, 8, 400 } });
            scene.Objects.Add(new SceneObject { Id = "s", Kind = SceneKinds.Spawn, Name = "Start", Position = new[] { 0f, 0.5f, 180 }, Size = new[] { 6f, 1, 6 } });
            scene.Objects.Add(new SceneObject { Id = "p", Name = "SellPad", Position = new[] { 12f, 0.5f, 170 }, Size = new[] { 8f, 1, 8 }, Tags = { "SellZone" } });
            for (int i = 0; i < 6; i++)
                scene.Objects.Add(new SceneObject { Id = "b" + i, Name = "Bush", Position = new[] { -40f + i * 16, 2, i * 50 - 100 }, Size = new[] { 4f, 4, 4 }, Shape = "ball", Color = "#3C8C3C" });
            ws.SaveScene(scene);
            var compiled = PlaceCompiler.Compile(new CompileInput { GameId = "chop", Workspace = ws });
            Assert.True(compiled.Ok, string.Join("\n", compiled.Errors));
            var map = MapReport.Analyze(scene, compiled.Parts, compiled.Terrain, manifest);
            Assert.True(map.Blocked, map.Describe());
            Assert.True(map.Score <= MapReport.BlockedCap, "scored " + map.Score);
            Assert.Contains(map.Findings, f => f.IsError && f.Area == "ground");
            Assert.Contains(map.Findings, f => f.IsError && f.Area == "shape");
        }

        [Fact]
        public void AZoneWalledOffFromTheSpawn_IsReportedByTheWalkingCheck()
        {
            using var dir = new TempDir();
            var ws = new GameWorkspace(Path.Combine(dir.Path, "ws"), Path.Combine(dir.Path, "snap"));
            var manifest = new GameManifest { Title = "Walls", Template = "simulator" };
            ws.EnsureCreated(manifest);
            var scene = new SceneDocument { Land = new TerrainRecipe { Size = new[] { 320f, 320 }, Hills = 0, BorderHeight = 0, FlatRadius = 1000, Undulation = 0 } };
            scene.Bounds.Area = new[] { -100f, -100, 100, 100 };
            scene.Objects.Add(new SceneObject { Id = "s", Kind = SceneKinds.Spawn, Name = "Start", Position = new[] { 0f, 0.5f, 0 }, Size = new[] { 6f, 1, 6 } });
            scene.Objects.Add(new SceneObject { Id = "p", Name = "SellPad", Position = new[] { 14f, 0.5f, 0 }, Size = new[] { 8f, 1, 8 }, Tags = { "SellZone" } });
            scene.Layout.Zones.Add(new MapZone { Id = "z1", Name = "Locked Woods", Order = 2, Area = new[] { 40f, -20, 80, 20 }, Circle = new[] { 60f, 0, 20 }, Entrance = new[] { 60f, 0 }, Gated = true });
            foreach (var (x, z, sx, sz) in new[] { (44f, 0f, 2f, 34f), (76f, 0f, 2f, 34f), (60f, -16f, 34f, 2f), (60f, 16f, 34f, 2f) })
                scene.Objects.Add(new SceneObject { Id = $"w{x}{z}", Name = "Wall", Position = new[] { x, 7, z }, Size = new[] { sx, 14, sz } });
            ws.SaveScene(scene);
            var compiled = PlaceCompiler.Compile(new CompileInput { GameId = "walls", Workspace = ws });
            var play = Playability.Check(scene, compiled.Parts, compiled.Terrain, manifest);
            Assert.True(play.Checked);
            Assert.Contains(play.Findings, f => f.IsError && f.Message.Contains("Locked Woods"));
            Assert.True(play.Distances.ContainsKey("sell pad"), play.Describe());

            // A gap in the wall lets players in.
            scene.Objects.RemoveAll(o => o.Id == "w44" + "0");
            ws.SaveScene(scene);
            compiled = PlaceCompiler.Compile(new CompileInput { GameId = "walls", Workspace = ws });
            play = Playability.Check(scene, compiled.Parts, compiled.Terrain, manifest);
            Assert.DoesNotContain(play.Findings, f => f.IsError);
            Assert.True(play.Distances["zone Locked Woods"] > 40, play.Describe());
        }

        private static SimulatorEconomy FourZones() => new()
        {
            Zones =
            {
                new EconZone { Name = "Meadow", Hp = 4, Value = 2, Distance = 70, Spacing = 14 },
                new EconZone { Name = "Forest", Hp = 9, Value = 5, Distance = 130, Spacing = 14 },
                new EconZone { Name = "Autumn", Hp = 20, Value = 13, Distance = 190, Spacing = 15 },
                new EconZone { Name = "Snow", Hp = 45, Value = 34, Distance = 250, Spacing = 16 },
            },
            Tools = SimulatorEconomy.DefaultTools(),
            Capacity = new EconUpgrade { Key = "capacity", Costs = new() { 50, 150, 400, 1000, 2500 }, Step = 25 },
            Speed = new EconUpgrade { Key = "speed", Costs = new() { 75, 250, 700, 1800 }, Step = 3 },
            RebirthCost = 10_000,
        };

        [Fact]
        public void TheBalancedEconomy_MeetsTheSchedule_InOrder_AndAWallIsCaught()
        {
            var e = FourZones();
            Economy.Balance(e);
            var report = Economy.Simulate(e);
            Assert.True(report.Ok, report.Describe());
            Assert.True(report.FirstSellSeconds <= 90, report.Describe());
            Assert.True(report.FirstPurchaseMinutes <= 2, report.Describe());
            Assert.Equal(new[] { "unlock Forest", "unlock Autumn", "unlock Snow" }, report.Timeline.Where(t => t.What.StartsWith("unlock ", StringComparison.Ordinal)).Select(t => t.What));
            Assert.InRange(report.RebirthMinutes ?? 0, 30, 90);
            Assert.Equal(0, e.Zones[0].Price);
            Assert.True(e.Zones[1].Price < e.Zones[2].Price && e.Zones[2].Price < e.Zones[3].Price, string.Join(", ", e.Zones.Select(z => z.Price)));

            e.Zones[3].Price = 50_000_000;
            var walled = Economy.Simulate(e);
            Assert.False(walled.Ok);
            Assert.Contains(walled.Findings, f => f.IsError && f.Message.Contains("out of reach"));
        }

        [Fact]
        public void TheEconomy_PlaysZonesInTheOrderTheDesignOpensThem_NotByNameOrPrice()
        {
            var scene = new SceneDocument { Spec = new WorldSpec { Zones = { new ZoneSpec { Name = "Zeta", Order = 1 }, new ZoneSpec { Name = "Alpha", Order = 2 }, new ZoneSpec { Name = "Mid", Order = 3 } } } };
            int n = 0;
            foreach (string zone in new[] { "Mid", "Alpha", "Zeta" })
                for (int i = 0; i < 6; i++)
                {
                    n++;
                    scene.Objects.Add(new SceneObject { Id = "n" + n, Name = "Node", Position = new[] { n * 10f, 1, 0 }, Size = new[] { 2f, 2, 2 }, Tags = { "Resource" }, Config = { ["Zone"] = zone, ["Hp"] = 4, ["Value"] = 2 } });
                }
            var e = SimulatorEconomy.FromGame(new GameManifest { Template = "simulator" }, scene);
            Assert.Equal(new[] { "Zeta", "Alpha", "Mid" }, e.Zones.Select(z => z.Name));
        }

        [Fact]
        public void AfterATerrainEdit_WhatStoodOnTheGroundStandsOnTheNewGround_AndWhatWasUpInTheAirStays()
        {
            var recipe = new TerrainRecipe { Size = new[] { 200f, 200 }, Hills = 0, BorderHeight = 0, FlatRadius = 1000, Undulation = 0 };
            var before = TerrainBuilder.Build(recipe);
            recipe.Ops.Add(new TerrainOp { Kind = "raise", Center = new[] { 0f, 0 }, Radius = 40, Height = 4, Falloff = 0 });
            var after = TerrainBuilder.Build(recipe);
            float lift = after.HeightAt(5, 5) - before.HeightAt(5, 5);
            Assert.True(lift > 3, "the raise did not raise: " + lift);

            var pine = new CatalogEntry(TestModels.Pine, "Pine", Roles.Conifer, "stylized", new[] { "forest" }, 8, 15, 8, 4, 7, 0, false, 0);
            var scene = new SceneDocument();
            float seat = Generation.SeatOn(before.HeightAt, pine, 0, 0, 0, 1).Y;
            scene.Objects.Add(new SceneObject { Id = "tree", Kind = SceneKinds.Asset, AssetId = pine.AssetId, Name = "Pine", Position = new[] { 0f, seat, 0 } });
            scene.Objects.Add(new SceneObject { Id = "rock", Name = "Rock", Position = new[] { 5f, before.HeightAt(5, 5) + 1, 5 }, Size = new[] { 4f, 2, 4 } });
            scene.Objects.Add(new SceneObject { Id = "base", Name = "Base", Group = "prop:sign", Position = new[] { -6f, before.HeightAt(-6, 0) + 0.5f, 0 }, Size = new[] { 2f, 1, 2 } });
            scene.Objects.Add(new SceneObject { Id = "board", Name = "Board", Group = "prop:sign", Position = new[] { -6f, before.HeightAt(-6, 0) + 5, 0 }, Size = new[] { 6f, 3, 0.5f } });
            scene.Objects.Add(new SceneObject { Id = "balloon", Name = "Balloon", Position = new[] { 8f, before.HeightAt(8, 0) + 20, 0 }, Size = new[] { 3f, 3, 3 } });
            float boardY = scene.Objects.Single(o => o.Id == "board").Position[1], balloonY = scene.Objects.Single(o => o.Id == "balloon").Position[1];

            int moved = WorldOps.Reground(scene, before, after, id => id == pine.AssetId ? pine : null);
            Assert.Equal(4, moved);
            float Y(string id) => scene.Objects.Single(o => o.Id == id).Position[1];
            Assert.Equal(Generation.SeatOn(after.HeightAt, pine, 0, 0, 0, 1).Y, Y("tree"), 2);
            Assert.Equal(after.HeightAt(5, 5) + 1, Y("rock"), 1);
            Assert.Equal(boardY + (after.HeightAt(-6, 0) - before.HeightAt(-6, 0)), Y("board"), 1); // the sign moved with its base
            Assert.Equal(balloonY, Y("balloon"));
        }

        [Fact]
        public void TheScaffold_TurnsTheBriefIntoAPlayableBaseline_Once_AndKeepsTheOldWorld()
        {
            using var h = new StudioHarness();
            TestModels.Seed(h.Ctx.Library);
            string id = h.Orchestrator.StartGame();
            h.Ctx.Update(id, g => g.Brief = new GameBrief
            {
                Title = "Timber Island", Genre = "simulator", Template = "sandbox", Pitch = "Chop trees, sell logs, unlock new forests.",
                CoreLoop = "Chop trees for logs, sell them, buy better axes, unlock the next forest.",
                MustHave = { "Four zones: Maple Grove, Pine Hills, Frost Woods, Ember Wilds", "Axe upgrades", "A rebirth" },
            });
            var ws = h.Ctx.Workspace(id);
            ws.EnsureCreated(new GameManifest { Title = "Timber Island", Template = "sandbox" });
            ws.SaveScene(new SceneDocument { Objects = { new SceneObject { Id = "old1", Name = "OldSlab", Position = new[] { 0f, -2, 0 }, Size = new[] { 140f, 4, 400 } } } });

            h.Orchestrator.Enter(id, GameStage.Build);
            var manifest = ws.LoadManifest();
            var scene = ws.LoadScene();
            Assert.Equal("simulator", manifest.Template); // a "sandbox" about chopping is a simulator
            Assert.Equal("forest", scene.Spec!.Biome);
            Assert.Equal(new[] { "Maple Grove", "Pine Hills", "Frost Woods", "Ember Wilds" }, scene.Spec.Zones.OrderBy(z => z.Order).Select(z => z.Name));
            Assert.Equal("snow", scene.Spec.Zones.Single(z => z.Name == "Frost Woods").Biome);
            Assert.DoesNotContain(scene.Objects, o => o.Name == "OldSlab");
            Assert.True(File.Exists(Path.Combine(ws.SnapshotsDirectory, "scene.before-worldgen.json")));
            Assert.Contains("Maple Grove", ws.ReadFile("design.md"));

            // The tutorial points at real things.
            Assert.True(manifest.Quests.Count >= 6);
            foreach (var q in manifest.Quests.Where(q => q.Target is { Length: > 0 }))
                Assert.True(scene.Objects.Any(o => o.Name == q.Target || o.Tags.Contains(q.Target!)) || scene.Folders.Values.Any(f => f.Tags.Contains(q.Target!)), $"quest {q.Id} points at {q.Target}, which is not in the world");

            // An economy a bot plays through, zone after zone in the design's order.
            var report = Economy.Simulate(SimulatorEconomy.FromGame(manifest, scene));
            Assert.True(report.Ok, report.Describe());
            Assert.True(report.FirstPurchaseMinutes <= 2, report.Describe());
            Assert.Equal(new[] { "unlock Pine Hills", "unlock Frost Woods", "unlock Ember Wilds" }, report.Timeline.Where(t => t.What.StartsWith("unlock ", StringComparison.Ordinal)).Select(t => t.What));

            // The baseline clears the bar as it is.
            var compiled = h.Ctx.Compile(id);
            var map = MapReport.Analyze(scene, compiled.Parts, compiled.Terrain, manifest, catalog: h.Ctx.Catalog().Get);
            Assert.False(map.Blocked, map.Describe());
            Assert.True(map.Score >= MapReport.Bar, map.Describe());

            // Once.
            Assert.Null(GameScaffold.Ensure(h.Ctx, id));
        }

        [Fact]
        public void ASpecChange_MergesInAnyCase_AndKeepsWhatItDoesNotName()
        {
            var spec = JObject.FromObject(new WorldSpec { Layout = "course", Course = new CourseSpec { Stages = 30, Themes = { "lava", "ice" }, Difficulty = "hard" } });
            Services.OmniBlox.Pipeline.Tools.WorldTools.MergeInto(spec, JObject.Parse("{\"course\": {\"stages\": 16}, \"density\": 1.3}"));
            var merged = spec.ToObject<WorldSpec>()!;
            Assert.Equal(16, merged.Course!.Stages);
            Assert.Equal(new[] { "lava", "ice" }, merged.Course.Themes);
            Assert.Equal("hard", merged.Course.Difficulty);
            Assert.Equal(1.3f, merged.Density);
        }

        [Theory]
        [InlineData("Timber Island: chop trees, sell logs, unlock new forests", "forest")]
        [InlineData("Lava Rush: leap over glowing lava", "volcanic")]
        [InlineData("Snowy peaks and frozen lakes", "snow")]
        [InlineData("Start the race and get to the finish", "meadow")] // "start" is not "star"
        public void TheBiome_IsWhatTheBriefTalksAboutMost(string text, string biome) => Assert.Equal(biome, GameScaffold.BiomeOf(text, "meadow"));

        [Fact]
        public void TheLaunchApproval_CoversTheStorePageAndTheProducts_AndNothingElse()
        {
            var g = new GameDossier
            {
                Store = new StorePage { Title = "Timber Island", Description = "Chop, sell, unlock!" },
                Products = { new ProductRecord { Key = "coins", Name = "500 Coins", PriceRobux = 25, Grant = "currency:500", Description = "A bag of coins" } },
            };
            string approved = Approvals.Fingerprint(g);
            g.Milestones.Add(new Milestone { MilestoneId = "m9", Title = "unrelated" });
            Assert.Equal(approved, Approvals.Fingerprint(g));
            g.Products[0].PriceRobux = 49;
            Assert.NotEqual(approved, Approvals.Fingerprint(g));
            g.Products[0].PriceRobux = 25;
            g.Store.Description = "Chop, sell, unlock — now with pets!";
            Assert.NotEqual(approved, Approvals.Fingerprint(g));
        }
    }
}
