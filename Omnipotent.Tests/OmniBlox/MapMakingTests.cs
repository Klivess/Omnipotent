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
    /// The map-making layer the builder designs every world with, now that games are no longer made from
    /// templates: landforms with their edges, the sculpting steps that make places, the landscaper's layers over
    /// any area (on a small part-built library, <see cref="TestModels"/>), kit props, the walking check and the
    /// map report — what the first studio shipped is still blocked.
    /// </summary>
    public class MapMakingTests
    {
        private static TerrainRecipe Flat(int size = 400) => new() { Size = new[] { (float)size, size }, Hills = 0, BorderHeight = 0, FlatRadius = 2000, Undulation = 0 };

        private static TerrainRecipe Shape(string shape, Action<TerrainRecipe>? tweak = null)
        {
            var r = new TerrainRecipe { Shape = shape, Size = new[] { 640f, 640 }, Seed = 7 };
            tweak?.Invoke(r);
            return r;
        }

        [Theory]
        [InlineData("basin")]
        [InlineData("plains")]
        [InlineData("island")]
        [InlineData("archipelago")]
        [InlineData("coast")]
        [InlineData("valley")]
        [InlineData("canyon")]
        [InlineData("mountain")]
        [InlineData("highlands")]
        [InlineData("crater")]
        public void EveryLandform_IsTheSameFromTheSameRecipe_AndAnotherSeedChangesIt(string shape)
        {
            var a = TerrainBuilder.Build(Shape(shape));
            var b = TerrainBuilder.Build(Shape(shape));
            Assert.Equal(a.Heights, b.Heights);
            Assert.Equal(a.Materials, b.Materials);
            var c = TerrainBuilder.Build(Shape(shape, r => r.Seed = 8));
            Assert.NotEqual(a.Heights, c.Heights);
        }

        [Fact]
        public void TheLandforms_HaveTheirOwnCharacter()
        {
            // A basin: a flat core to build on, mountains at the edge (what every map used to be).
            var basin = TerrainBuilder.Build(Shape("basin"));
            Assert.InRange(basin.HeightAt(0, 0), -0.5f, 0.5f);
            Assert.True(basin.HeightAt(310, 0) > 35, "basin edge " + basin.HeightAt(310, 0));
            Assert.Null(basin.WaterLevel);

            // An island: dry in the middle, the sea all round (the sea is the edge).
            var island = TerrainBuilder.Build(Shape("island"));
            Assert.Equal(TerrainBuilder.SeaLevel, island.WaterLevel);
            Assert.False(island.InWater(0, 0));
            foreach (var (x, z) in new[] { (300f, 0f), (-300f, 0f), (0f, 300f), (0f, -300f), (290f, 290f) }) Assert.True(island.InWater(x, z), $"({x}, {z}) is dry");
            Assert.Contains("Sand", island.MaterialNames);

            // An archipelago: every island it lists is dry land, the sea between and round them.
            var archRecipe = Shape("archipelago", r => r.Islands = 5);
            var arch = TerrainBuilder.Build(archRecipe);
            var islands = TerrainBuilder.Features(archRecipe);
            Assert.Equal(5, islands.Count);
            Assert.All(islands, f => Assert.False(arch.InWater(f.X, f.Z), f.Name + " is under water"));
            double wet = Enumerable.Range(0, 64 * 64).Count(i => arch.InWater(-310 + i % 64 * 9.8f, -310 + i / 64 * 9.8f)) / (64.0 * 64);
            Assert.InRange(wet, 0.35, 0.92);

            // A coast: the sea where its direction points, land behind.
            var coast = TerrainBuilder.Build(Shape("coast", r => r.Direction = 90));
            Assert.True(coast.InWater(-260, 0) || coast.InWater(260, 0));
            bool seaEast = coast.InWater(-260, 0) == false;
            Assert.True(seaEast ? coast.InWater(260, 0) && !coast.InWater(-150, 0) : !coast.InWater(150, 0), "the sea is not on one side");

            // A valley along Z: high walls either side of the floor.
            var valley = TerrainBuilder.Build(Shape("valley"));
            Assert.True(valley.HeightAt(230, 0) - valley.HeightAt(0, 0) > 35 && valley.HeightAt(-230, 0) - valley.HeightAt(0, 0) > 35, $"walls {valley.HeightAt(-230, 0):0}/{valley.HeightAt(230, 0):0}, floor {valley.HeightAt(0, 0):0}");

            // A canyon: a high plateau with the floor cut deep into it.
            var canyon = TerrainBuilder.Build(Shape("canyon"));
            float floor = Enumerable.Range(-6, 13).Min(i => canyon.HeightAt(i * 10, 0));
            Assert.True(floor < 6, "canyon floor " + floor);
            Assert.True(canyon.HeightAt(220, 0) > 30 && canyon.HeightAt(-220, 0) > 30, "the plateau is not high");

            // A mountain: a peak in the middle, its foot low.
            var mountain = TerrainBuilder.Build(Shape("mountain"));
            Assert.True(mountain.HeightAt(0, 0) > 80, "peak " + mountain.HeightAt(0, 0));
            Assert.True(mountain.HeightAt(0, 230) < 25, "foot " + mountain.HeightAt(0, 230));
            Assert.Contains("Snow", mountain.MaterialNames);

            // A crater: a flat bowl inside a high rim.
            var crater = TerrainBuilder.Build(Shape("crater"));
            Assert.InRange(crater.HeightAt(0, 0), -1f, 3f);
            Assert.True(crater.HeightAt(160, 0) > 30, "rim " + crater.HeightAt(160, 0));

            // Highlands: terraces — most of the land sits on a few levels with steep steps between.
            var high = TerrainBuilder.Build(Shape("highlands", r => r.BorderHeight = 0));
            var heights = Enumerable.Range(0, 40 * 40).Select(i => high.HeightAt(-240 + i % 40 * 12, -240 + i / 40 * 12)).ToList();
            double onStep = heights.Count(h => { float m = h % 10; return m < 0.6f || m > 9.4f; }) / (double)heights.Count;
            Assert.True(onStep > 0.55, $"only {onStep:P0} of the highlands sit on a terrace");
            Assert.True(heights.Max() > 15);
        }

        [Fact]
        public void TheSculptingSteps_MakePlaces()
        {
            var r = Flat();
            r.Ops.Add(new TerrainOp { Kind = "mesa", Center = new[] { -100f, -100 }, Radius = 40, Height = 30 });
            r.Ops.Add(new TerrainOp { Kind = "ridge", Points = new() { new[] { -150f, 60 }, new[] { 150f, 60 } }, Width = 60, Height = 30 });
            r.Ops.Add(new TerrainOp { Kind = "canyon", Points = new() { new[] { -150f, 130 }, new[] { 150f, 130 } }, Width = 20, Height = 16, Falloff = 0.2f });
            r.Ops.Add(new TerrainOp { Kind = "ramp", Points = new() { new[] { 100f, -160 }, new[] { 100f, -60 } }, Width = 14, Height = 0, EndHeight = 20 });
            r.Ops.Add(new TerrainOp { Kind = "mountain", Center = new[] { 160f, -10 }, Radius = 40, Height = 60 });
            r.Ops.Add(new TerrainOp { Kind = "flatten", Points = new() { new[] { -40f, -40 }, new[] { 40f, -40 }, new[] { 40f, -10 }, new[] { -40f, -10 } }, Height = 6, Falloff = 0.2f });
            r.Ops.Add(new TerrainOp { Kind = "paint", Points = new() { new[] { -190f, 160 }, new[] { -120f, 160 }, new[] { -120f, 190 }, new[] { -190f, 190 } }, Biome = "snow" });
            var map = TerrainBuilder.Build(r);

            Assert.InRange(map.HeightAt(-100, -100), 29.5f, 30.5f); // the mesa's flat top
            Assert.True(map.HeightAt(-100, -30) < 2, "the mesa spread out");
            float cliff = Enumerable.Range(30, 20).Max(d => map.SlopeAt(-100 + d, -100));
            Assert.True(cliff > 1, "the mesa's sides are not steep: " + cliff);
            Assert.True(map.HeightAt(0, 60) > 20 && map.HeightAt(0, 0) < 2, "the ridge");
            Assert.True(map.HeightAt(0, 130) < -12, "the canyon " + map.HeightAt(0, 130));
            Assert.InRange(map.HeightAt(100, -110), 6f, 14f); // half way up the ramp
            Assert.InRange(map.HeightAt(100, -62), 17f, 21f);
            Assert.True(map.HeightAt(160, -10) > 50, "the mountain");
            Assert.InRange(map.HeightAt(0, -25), 5.5f, 6.5f); // the polygon plaza
            Assert.Equal("Snow", map.MaterialAt(-150, 175));
            Assert.Equal("Grass", map.MaterialAt(150, 175));

            // An island rises out of the sea; terraces step a slope; smoothing softens; a rough outline is no circle.
            var sea = Flat();
            sea.WaterLevel = -2.5f;
            sea.Ops.Add(new TerrainOp { Kind = "lower", Center = new[] { 0f, 0 }, Radius = 400, Height = 20, Falloff = 0 });
            sea.Ops.Add(new TerrainOp { Kind = "island", Center = new[] { 0f, 0 }, Radius = 70, Height = 2 });
            var islandMap = TerrainBuilder.Build(sea);
            Assert.False(islandMap.InWater(0, 0));
            Assert.True(islandMap.InWater(150, 0));

            var bumpy = Flat();
            bumpy.Ops.Add(new TerrainOp { Kind = "roughen", Center = new[] { 0f, 0 }, Radius = 120, Height = 8, Width = 20 });
            var rough = TerrainBuilder.Build(bumpy);
            bumpy.Ops.Add(new TerrainOp { Kind = "smooth", Center = new[] { 0f, 0 }, Radius = 120, Width = 24 });
            var smooth = TerrainBuilder.Build(bumpy);
            double Spread(TerrainMap m) => Enumerable.Range(0, 400).Select(i => (double)m.HeightAt(-60 + i % 20 * 6, -60 + i / 20 * 6)).Aggregate((Sum: 0.0, Sq: 0.0, N: 0), (a, h) => (a.Sum + h, a.Sq + h * h, a.N + 1), a => Math.Sqrt(a.Sq / a.N - Math.Pow(a.Sum / a.N, 2)));
            Assert.True(Spread(rough) > 1.5, "roughen did nothing");
            Assert.True(Spread(smooth) < Spread(rough) * 0.8, "smooth did nothing");

            var round = Flat();
            round.Ops.Add(new TerrainOp { Kind = "plateau", Center = new[] { 0f, 0 }, Radius = 60, Height = 10, Falloff = 0.05f });
            var ragged = Flat();
            ragged.Ops.Add(new TerrainOp { Kind = "plateau", Center = new[] { 0f, 0 }, Radius = 60, Height = 10, Falloff = 0.05f, Rough = 0.35f });
            var a = TerrainBuilder.Build(round);
            var b = TerrainBuilder.Build(ragged);
            int differ = Enumerable.Range(0, 72).Count(i => MathF.Abs(a.HeightAt(MathF.Cos(i * 0.0873f) * 60, MathF.Sin(i * 0.0873f) * 60) - b.HeightAt(MathF.Cos(i * 0.0873f) * 60, MathF.Sin(i * 0.0873f) * 60)) > 2);
            Assert.True(differ > 10, "a rough outline is still a circle");
        }

        private sealed class Land : IDisposable
        {
            public required TempDir Dir { get; init; }
            public required AssetLibrary Library { get; init; }
            public required AssetCatalog Catalog { get; init; }
            public required SceneDocument Scene { get; init; }
            public TerrainMap Terrain => TerrainBuilder.Build(Scene.Land!);

            public Landscaper Landscaper(int seed = 3) => new(Scene, Terrain, Catalog, Array.Empty<Footprint>(), seed);

            public void Dispose()
            {
                Library.Dispose();
                Dir.Dispose();
            }
        }

        /// <summary>Rolling meadow with a pond, a play area, two planned places and a path between them.</summary>
        private static Land Meadow()
        {
            var dir = new TempDir();
            var library = TestModels.Library(Path.Combine(dir.Path, "assets"));
            var land = new TerrainRecipe { Size = new[] { 640f, 640 }, Hills = 8, FlatRadius = 140, Undulation = 2, Seed = 4, WaterLevel = -1 };
            land.Ops.Add(new TerrainOp { Kind = "lake", Center = new[] { 90f, 90 }, Radius = 34, Height = 6 });
            var scene = new SceneDocument { Land = land };
            scene.Bounds.Area = new[] { -200f, -200, 200, 200 };
            scene.Layout.Zones.Add(new MapZone { Id = "camp", Name = "Camp", Order = 1, Circle = new[] { -90f, -90, 45 }, Area = new[] { -135f, -135, -45, -45 } });
            scene.Layout.Zones.Add(new MapZone { Id = "grove", Name = "Old Grove", Order = 2, Circle = new[] { 90f, -90, 50 }, Area = new[] { 40f, -140, 140, -40 }, Theme = "misty forest" });
            scene.Layout.Paths.Add(new MapPath { From = "camp", To = "grove", Points = new() { new[] { -50f, -90 }, new[] { 0f, -80 }, new[] { 45f, -90 } }, Width = 12 });
            return new Land { Dir = dir, Library = library, Catalog = TestModels.Catalog(library), Scene = scene };
        }

        [Fact]
        public void AForest_StaysInItsArea_OffPathsAndWater_OnTheGround_AndIsTheSameForTheSameSeed()
        {
            using var w = Meadow();
            var area = new List<float[]> { new[] { -200f, -40 }, new[] { 200f, -40 }, new[] { 200f, 200 }, new[] { -200f, 200 } };
            var result = Dressing.Dress(w.Landscaper(), new DressRequest { Kind = "forest", Polygon = area, Biome = "forest" });
            var placed = w.Scene.Objects.Where(o => o.Folder == "Scenery/Forest").ToList();
            Assert.True(placed.Count > 60, result.Describe());
            Assert.Contains("tree", result.Roles.Keys);
            Assert.Contains("bush", result.Roles.Keys);
            var terrain = w.Terrain;
            Assert.All(placed, o =>
            {
                Assert.True(TerrainBuilder.InPolygon(o.Position[0], o.Position[2], area), $"{o.Id} outside the area");
                Assert.False(terrain.InWater(o.Position[0], o.Position[2]), $"{o.Id} in the pond");
                Assert.True(o.Position[1] <= terrain.HeightAt(o.Position[0], o.Position[2]) + 0.05f, $"{o.Id} floats");
                Assert.True(o.Position[1] > terrain.HeightAt(o.Position[0], o.Position[2]) - 3, $"{o.Id} is buried");
            });
            // Clearings: the wood is not a carpet.
            var clearing = Enumerable.Range(0, 30 * 30).Select(i => (X: -195 + i % 30 * 13f, Z: -35 + i / 30 * 8f))
                .Count(p => !placed.Any(o => MathF.Abs(o.Position[0] - p.X) < 10 && MathF.Abs(o.Position[2] - p.Z) < 10));
            Assert.True(clearing > 40, "no clearings");

            using var again = Meadow();
            Dressing.Dress(again.Landscaper(), new DressRequest { Kind = "forest", Polygon = area, Biome = "forest" });
            Assert.Equal(JsonConvert.SerializeObject(w.Scene.Objects), JsonConvert.SerializeObject(again.Scene.Objects));
        }

        [Fact]
        public void ARim_RingsAPlace_AndStaysOpenWherePathsComeIn_ATreeLineStaysOutsideThePlayArea()
        {
            using var w = Meadow();
            var grove = w.Scene.Layout.Zones[1];
            Dressing.Dress(w.Landscaper(), new DressRequest { Kind = "rim", Circle = (grove.Circle![0], grove.Circle[1], grove.Circle[2]), Biome = "forest" });
            var rim = w.Scene.Objects.Where(o => o.Folder == "Scenery/Rim").ToList();
            Assert.True(rim.Count > 12, "rim of " + rim.Count);
            var entry = w.Scene.Layout.Paths[0].Points[^1];
            Assert.DoesNotContain(rim, o => MathF.Sqrt(MathF.Pow(o.Position[0] - entry[0], 2) + MathF.Pow(o.Position[2] - entry[1], 2)) < 10);
            Assert.All(rim, o =>
            {
                float d = MathF.Sqrt(MathF.Pow(o.Position[0] - grove.Circle[0], 2) + MathF.Pow(o.Position[2] - grove.Circle[1], 2));
                Assert.InRange(d, grove.Circle[2] - 4, grove.Circle[2] + 15);
            });

            Dressing.Dress(w.Landscaper(), new DressRequest { Kind = "treeline", Biome = "forest" });
            var line = w.Scene.Objects.Where(o => o.Folder == "Scenery/Treeline").ToList();
            Assert.True(line.Count > 40, "tree line of " + line.Count);
            Assert.All(line, o => Assert.False(w.Scene.Bounds.Contains(o.Position[0], o.Position[2]), o.Id + " is inside the play area"));

            Dressing.Dress(w.Landscaper(), new DressRequest { Kind = "roadside", Biome = "meadow" });
            Assert.Contains(w.Scene.Objects, o => o.Folder == "Scenery/Roadside" && o.Name == "LampHead");
        }

        [Fact]
        public void TheLandscaper_KeepsToTheBudget_AndBuildsKitTreesWhereTheLibraryHasNone()
        {
            using var w = Meadow();
            var l = w.Landscaper();
            l.Budget = Landscaper.PartsOf(w.Scene, w.Catalog) + 120;
            var result = Dressing.Dress(l, new DressRequest { Kind = "forest", Biome = "forest" });
            Assert.True(result.MapParts <= l.Budget, result.Describe());

            using var empty = Meadow();
            var bare = new Landscaper(empty.Scene, empty.Terrain, AssetCatalog.Empty, Array.Empty<Footprint>(), 1);
            var kit = Dressing.Dress(bare, new DressRequest { Kind = "forest", Polygon = new() { new[] { -200f, 0 }, new[] { 0f, 0 }, new[] { 0f, 200 }, new[] { -200f, 200 } }, Biome = "forest" });
            Assert.Contains("kit tree", kit.Roles.Keys);
            Assert.Contains("tree", kit.Missing);
            Assert.Contains(empty.Scene.Objects, o => o.Name == "Trunk");
        }

        [Fact]
        public void ABridge_SpansTheWater_WalkablyFromBankToBank()
        {
            var recipe = Flat();
            recipe.WaterLevel = -1;
            recipe.Ops.Add(new TerrainOp { Kind = "river", Points = new() { new[] { 0f, -200 }, new[] { 0f, 200 } }, Width = 30, Height = 5 });
            var map = TerrainBuilder.Build(recipe);
            Assert.True(map.InWater(0, 0));
            var objects = new List<SceneObject>();
            int n = 0;
            float length = 60, y0 = map.HeightAt(-30, 0);
            var b = new PropBuilder(objects, () => "p" + n++, "World/Bridge", -30, y0, 0, WorldOps.YawToward(-30, 0, 30, 0));
            KitProps.Bridge(b, length, 8, map.HeightAt(30, 0) - y0, 4, localZ => map.HeightAt(-30 - localZ, 0) - y0);
            var deck = objects.Where(o => o.Name == "BridgeDeck").OrderBy(o => o.Position[0]).ToList();
            Assert.True(deck.Count >= 15);
            Assert.InRange(deck[0].Position[0], -30f, -25f);
            Assert.InRange(deck[^1].Position[0], 25f, 30f);
            Assert.All(deck, d => Assert.InRange(d.Position[2], -0.1f, 0.1f));
            // Each plank meets the next (no step a character trips on) and the middle arches over the water.
            for (int i = 1; i < deck.Count; i++) Assert.True(MathF.Abs(deck[i].Position[1] - deck[i - 1].Position[1]) < 1.2f);
            Assert.True(deck[deck.Count / 2].Position[1] > deck[0].Position[1] + 2);
            Assert.Contains(objects, o => o.Name == "Rail");
            Assert.Contains(objects, o => o.Name == "BridgePillar");
        }

        [Fact]
        public void AFlatStripOfGroundFloatingInTheSky_IsBlocked()
        {
            // The first studio's "Chop the Forest": a 140 × 400 slab of grass, a few bushes, the spawn at one end.
            foreach (string template in new[] { "", "simulator" })
            {
                using var dir = new TempDir();
                var ws = new GameWorkspace(Path.Combine(dir.Path, "ws"), Path.Combine(dir.Path, "snap"));
                var manifest = new GameManifest { Title = "Chop", Template = template };
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
                Assert.Contains(map.Findings, f => f.Area == "shape");
            }
        }

        [Fact]
        public void APlaceMeantToBeUnlocked_WithoutAGate_IsBlocked_AndAPlaceWithNothingToDoIsCalledOut()
        {
            using var w = Meadow();
            w.Scene.Layout.Zones[1].Gated = true;
            w.Scene.Objects.Add(new SceneObject { Id = "s", Kind = SceneKinds.Spawn, Name = "Start", Position = new[] { -90f, 0.5f, -60 }, Size = new[] { 8f, 1, 8 }, Yaw = 180 });
            var ws = new GameWorkspace(Path.Combine(w.Dir.Path, "ws"), Path.Combine(w.Dir.Path, "snap"));
            var manifest = new GameManifest { Title = "Grove" };
            ws.EnsureCreated(manifest);
            ws.SaveScene(w.Scene);
            var compiled = TestModels.Compile(w.Library, ws, "grove");
            var map = MapReport.Analyze(w.Scene, compiled.Parts, compiled.Terrain, manifest, catalog: w.Catalog.Get);
            Assert.Equal("open", map.Kind);
            Assert.Contains(map.Findings, f => f.IsError && f.Message.Contains("Old Grove is meant to be unlocked"));
            Assert.Contains(map.Findings, f => f.Message.Contains("Nothing to do in Camp"));
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
        public void AnOldSimulatorsEconomy_StillMeetsTheSchedule_InOrder_AndAWallIsCaught()
        {
            var e = FourZones();
            Economy.Balance(e);
            var report = Economy.Simulate(e);
            Assert.True(report.Ok, report.Describe());
            Assert.True(report.FirstSellSeconds <= 90, report.Describe());
            Assert.True(report.FirstPurchaseMinutes <= 2, report.Describe());
            Assert.Equal(new[] { "unlock Forest", "unlock Autumn", "unlock Snow" }, report.Timeline.Where(t => t.What.StartsWith("unlock ", StringComparison.Ordinal)).Select(t => t.What));
            Assert.InRange(report.RebirthMinutes ?? 0, 30, 90);

            e.Zones[3].Price = 50_000_000;
            var walled = Economy.Simulate(e);
            Assert.False(walled.Ok);
            Assert.Contains(walled.Findings, f => f.IsError && f.Message.Contains("out of reach"));
        }

        [Fact]
        public void AnOldSimulatorsEconomy_PlaysZonesInTheOrderThePlanOpensThem_NotByNameOrPrice()
        {
            var scene = new SceneDocument();
            scene.Layout.Zones.AddRange(new[] { new MapZone { Name = "Zeta", Order = 1 }, new MapZone { Name = "Alpha", Order = 2 }, new MapZone { Name = "Mid", Order = 3 } });
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
            float seat = Landscaper.SeatOn(before.HeightAt, pine, 0, 0, 0, 1).Y;
            scene.Objects.Add(new SceneObject { Id = "tree", Kind = SceneKinds.Asset, AssetId = pine.AssetId, Name = "Pine", Position = new[] { 0f, seat, 0 } });
            scene.Objects.Add(new SceneObject { Id = "rock", Name = "Rock", Position = new[] { 5f, before.HeightAt(5, 5) + 1, 5 }, Size = new[] { 4f, 2, 4 } });
            scene.Objects.Add(new SceneObject { Id = "base", Name = "Base", Group = "prop:sign", Position = new[] { -6f, before.HeightAt(-6, 0) + 0.5f, 0 }, Size = new[] { 2f, 1, 2 } });
            scene.Objects.Add(new SceneObject { Id = "board", Name = "Board", Group = "prop:sign", Position = new[] { -6f, before.HeightAt(-6, 0) + 5, 0 }, Size = new[] { 6f, 3, 0.5f } });
            scene.Objects.Add(new SceneObject { Id = "balloon", Name = "Balloon", Position = new[] { 8f, before.HeightAt(8, 0) + 20, 0 }, Size = new[] { 3f, 3, 3 } });
            float boardY = scene.Objects.Single(o => o.Id == "board").Position[1], balloonY = scene.Objects.Single(o => o.Id == "balloon").Position[1];

            int moved = WorldOps.Reground(scene, before, after, id => id == pine.AssetId ? pine : null);
            Assert.Equal(4, moved);
            float Y(string id) => scene.Objects.Single(o => o.Id == id).Position[1];
            Assert.Equal(Landscaper.SeatOn(after.HeightAt, pine, 0, 0, 0, 1).Y, Y("tree"), 2);
            Assert.Equal(after.HeightAt(5, 5) + 1, Y("rock"), 1);
            Assert.Equal(boardY + (after.HeightAt(-6, 0) - before.HeightAt(-6, 0)), Y("board"), 1); // the sign moved with its base
            Assert.Equal(balloonY, Y("balloon"));
        }

        [Fact]
        public void ANewGame_StartsFromTheKitAlone_AndAnOldOneKeepsItsTemplate()
        {
            using var h = new StudioHarness();
            string id = h.Orchestrator.StartGame();
            h.Ctx.Update(id, g => g.Brief = new GameBrief
            {
                Title = "Crab Cove", Genre = "collecting adventure", Pitch = "Catch crabs on five islands.", CoreLoop = "Catch crabs, sell them at the docks, buy a better net, sail to the next island.",
                World = "Five islands round a lagoon, linked by rope bridges, a lighthouse on the highest cliff.", MustHave = { "Five islands", "Nets", "A lighthouse" },
            });
            h.Orchestrator.Enter(id, GameStage.Build);
            var ws = h.Ctx.Workspace(id);
            Assert.Equal("", ws.LoadManifest().Template);
            var scene = ws.LoadScene();
            Assert.Null(scene.Land);
            Assert.Empty(scene.Objects);
            var compiled = h.Ctx.Compile(id);
            Assert.DoesNotContain(compiled.Scripts, s => s.KitFile != null && s.KitFile.StartsWith("Server/Templates/", StringComparison.Ordinal));
            Assert.True(h.Game(id).Milestones.Count >= 5);
            Assert.Contains(h.Game(id).Milestones, m => m.Title.Contains("landform", StringComparison.Ordinal));

            string old = h.Orchestrator.StartGame();
            h.Ctx.Update(old, g => g.Brief = new GameBrief { Title = "Timber Island", Genre = "simulator", Template = "simulator", CoreLoop = "Chop trees and sell logs." });
            h.Orchestrator.Enter(old, GameStage.Build);
            Assert.Equal("simulator", h.Ctx.Workspace(old).LoadManifest().Template);
            Assert.Contains(h.Ctx.Compile(old).Scripts, s => s.KitFile == "Server/Templates/Simulator.luau");
        }

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
