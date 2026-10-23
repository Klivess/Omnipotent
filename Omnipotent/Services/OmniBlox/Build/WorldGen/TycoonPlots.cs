namespace Omnipotent.Services.OmniBlox.Build.WorldGen
{
    /// <summary>
    /// A tycoon map: a plaza with the spawn, the shop and the leaderboard, and identical plots round it, each
    /// facing the plaza. A plot holds a production line that grows button by button — droppers over a conveyor,
    /// an upgrader, then walls, a garden, lamps, a statue, more droppers and a raised deck — with prices that rise
    /// as the income does, a collector by the entrance and an owner sign. Everything is the kit's tycoon template
    /// (TycoonPlot, TycoonButton, TycoonCollector, Dropper), so it plays the moment it is built.
    /// </summary>
    public static class TycoonPlots
    {
        public sealed record Item(string Name, double Income, string Kind);

        public const float PlotSize = 64;

        /// <summary>The production line, in buying order (names are unique in a plot: buttons unlock items by name).</summary>
        public static List<Item> Items(string theme)
        {
            string t = string.IsNullOrWhiteSpace(theme) ? "Factory" : char.ToUpperInvariant(theme.Trim()[0]) + theme.Trim()[1..];
            return new List<Item>
            {
                new($"{t} Dropper", 2, "dropper1"),
                new("Conveyor", 1, "conveyor"),
                new($"{t} Dropper 2", 4, "dropper2"),
                new("Walls", 3, "walls"),
                new($"{t} Dropper 3", 8, "dropper3"),
                new("Upgrader", 12, "upgrader"),
                new("Garden", 8, "garden"),
                new($"Big {t} Dropper", 20, "dropper4"),
                new("Lamps", 10, "lamps"),
                new("Statue", 25, "statue"),
                new($"Mega {t} Dropper", 45, "dropper5"),
                new("Sky Deck", 70, "deck"),
            };
        }

        /// <summary>Prices that keep each next button 30 s to a few minutes away at the income the plot has by then.</summary>
        public static List<long> Prices(List<Item> items, long first)
        {
            var prices = new List<long>();
            double income = 0;
            long last = 0;
            for (int i = 0; i < items.Count; i++)
            {
                double seconds = 25 + 14 * i;
                long price = i == 0 ? Math.Max(1, first) : (long)Math.Max(last * 1.35, income * seconds);
                price = Nice(price);
                prices.Add(price);
                last = price;
                income += items[i].Income;
            }
            return prices;
        }

        private static long Nice(long v)
        {
            if (v < 100) return (long)Math.Ceiling(v / 5.0) * 5;
            if (v < 1000) return (long)Math.Ceiling(v / 25.0) * 25;
            if (v < 10000) return (long)Math.Ceiling(v / 100.0) * 100;
            return (long)Math.Ceiling(v / 1000.0) * 1000;
        }

        public static void Build(Generation g)
        {
            var spec = g.Spec.Plots ?? new PlotsSpec();
            g.Spec.Plots = spec;
            int count = Math.Clamp(spec.Count, 2, 8);
            string biome = (g.Spec.Biome ?? "meadow").ToLowerInvariant();
            g.Scene.Layout = new LayoutPlan();
            float plazaR = 42;
            float ring = MathF.Max(plazaR + 26 + PlotSize * 0.72f, (PlotSize * 1.5f + 24) / (2 * MathF.Sin(MathF.PI / count)));
            var items = Items(spec.Theme);
            var prices = Prices(items, spec.FirstPrice);
            var (ground, plaza, path) = WorldGenerator.MaterialsFor(biome);

            var plots = Enumerable.Range(0, count).Select(i =>
            {
                float angle = 360f / count * i + 180f / count; // leave −Z (the spawn's view) between plots
                var (dx, dz) = WorldGenerator.Dir(angle);
                return (Angle: angle, X: dx * ring, Z: dz * ring, Dx: dx, Dz: dz);
            }).ToList();

            float extent = ring + PlotSize * 0.75f + 30;
            float minX = MathF.Floor(-extent / 4) * 4, maxX = -minX, minZ = minX, maxZ = maxX;
            g.Scene.Bounds.Area = new[] { minX, minZ, maxX, maxZ };
            g.Scene.Bounds.Walls = true;

            var recipe = new TerrainRecipe
            {
                Center = new[] { 0f, 0f }, Size = new[] { maxX - minX + 320, maxZ - minZ + 320 }, Biome = biome is "desert" ? "desert" : biome is "snow" ? "snow" : "meadow",
                Material = ground, Hills = 14, HillScale = 80, Seed = g.Spec.Seed, FlatRadius = extent - 10, BorderHeight = 85, BorderWidth = 145, Undulation = 2.5f,
            };
            recipe.Ops.Add(new TerrainOp { Kind = "flatten", Center = new[] { 0f, 0f }, Radius = plazaR + 12, Height = 0, Falloff = 0.3f });
            recipe.Ops.Add(new TerrainOp { Kind = "paint", Center = new[] { 0f, 0f }, Radius = plazaR * 0.9f, Material = plaza });
            foreach (var p in plots)
            {
                recipe.Ops.Add(new TerrainOp { Kind = "flatten", Center = new[] { p.X, p.Z }, Radius = PlotSize * 0.78f, Height = 0, Falloff = 0.2f });
                var road = new List<float[]> { new[] { p.Dx * (plazaR - 2), p.Dz * (plazaR - 2) }, new[] { p.X - p.Dx * (PlotSize / 2 - 2), p.Z - p.Dz * (PlotSize / 2 - 2) } };
                recipe.Ops.Add(new TerrainOp { Kind = "ramp", Points = road, Width = 12, Height = 0, EndHeight = 0, Material = path });
                g.KeepLine(road, 8);
                g.Scene.Layout.Paths.Add(new MapPath { From = "plaza", To = "plot" + (plots.IndexOf(p) + 1), Points = road, Width = 12 });
            }
            g.SetGround(recipe);

            // ── the plaza ──
            string accent = HubLayout.AccentFor(biome);
            float spawnZ = plazaR * 0.55f;
            KitProps.Spawn(g.Objects, g.NewId, "World/Plaza", 0, g.GroundAt(0, spawnZ), spawnZ, 0, "#57C46A");
            g.AddParts(1);
            g.KeepCircle(0, spawnZ, 10);
            KitProps.Stand(g.Prop("World/Plaza/Shop", -plazaR * 0.62f, plazaR * 0.1f, WorldGenerator.YawToward(-plazaR * 0.62f, plazaR * 0.1f, 0, 0)), "🛒 SHOP", "shop", "#FF5E8A", accent);
            g.AddParts(9);
            if (g.Manifest.Leaderboard) { KitProps.Leaderboard(g.Prop("World/Plaza/Leaderboard", plazaR * 0.62f, plazaR * 0.1f, WorldGenerator.YawToward(plazaR * 0.62f, plazaR * 0.1f, 0, 0))); g.AddParts(4); }
            g.KeepCircle(-plazaR * 0.62f, plazaR * 0.1f, 9);
            g.KeepCircle(plazaR * 0.62f, plazaR * 0.1f, 9);
            var centre = g.Palette(Roles.Fountain, biome, null, 1).Concat(g.Palette(Roles.Statue, biome, null, 1)).FirstOrDefault();
            if (centre != null) { g.PlaceModel(centre, 0, -6, 0, g.ScaleFor(centre, centre.Role == Roles.Fountain ? 10 : 16, 0.04f), "World/Plaza/Centerpiece", "Centerpiece", force: true); g.KeepCircle(0, -6, centre.Footprint / 2 + 5); }
            for (float a = 20; a < 360; a += 40)
            {
                var (dx, dz) = WorldGenerator.Dir(a);
                if (plots.Any(p => MathF.Abs(HubLayout.AngleDiff(a, p.Angle)) < 12)) continue;
                float x = dx * plazaR * 0.96f, z = dz * plazaR * 0.96f;
                if (!g.Free(x, z, 1.5f)) continue;
                KitProps.Lamp(g.Prop("World/Plaza/Lamps", x, z, 0));
                g.AddParts(4);
                g.Occupy(x, z, 1.5f);
            }

            // ── the plots ──
            for (int i = 0; i < plots.Count; i++)
            {
                var p = plots[i];
                float yaw = WorldGenerator.YawToward(p.X, p.Z, 0, 0); // the entrance (local −Z) faces the plaza
                Plot(g, i + 1, p.X, p.Z, yaw, items, prices, accent, biome);
                g.KeepCircle(p.X, p.Z, PlotSize * 0.72f);
                g.Scene.Layout.Zones.Add(new MapZone { Id = "plot" + (i + 1), Name = $"Plot {i + 1}", Order = 2, Area = new[] { p.X - PlotSize / 2, p.Z - PlotSize / 2, p.X + PlotSize / 2, p.Z + PlotSize / 2 }, Purpose = "a player's tycoon", Theme = spec.Theme, Circle = new[] { p.X, p.Z, PlotSize * 0.7f } });
            }
            g.Scene.Layout.Zones.Insert(0, new MapZone { Id = "plaza", Name = "Plaza", Order = 1, Area = new[] { -plazaR, -plazaR, plazaR, plazaR }, Purpose = "spawn, shop, leaderboard", Theme = biome, Circle = new[] { 0f, 0f, plazaR } });

            // ── scenery between and round the plots ──
            var trees = g.Palette(Roles.Tree, biome, null, 3).Concat(g.Palette(Roles.Conifer, biome, null, 2)).ToList();
            var bushes = g.Palette(Roles.Bush, biome, null, 2);
            foreach (var (x, z) in g.Darts(minX - 20, minZ - 20, maxX + 20, maxZ + 20, 12, 900, (x, z) =>
                MathF.Sqrt(x * x + z * z) > plazaR + 8 && g.Free(x, z, 2.5f) && !g.Wet(x, z) && g.SlopeAt(x, z) < 1.1f, attemptsPerPoint: 6))
            {
                var e = g.Pick(g.Rng.NextDouble() < 0.72 || bushes.Count == 0 ? trees : bushes);
                if (e == null) break;
                if (g.PlaceModel(e, x, z, (float)g.Rng.NextDouble() * 360, g.ScaleFor(e, Roles.TypicalHeight(e.Role)), "World/Scenery", occupyFactor: 0.3f) == null && g.OutOfBudget) break;
            }
            Scenery.TreeRing(g, biome, minX, minZ, maxX, maxZ, 80);
            g.Scene.Lighting = new LightingSpec { Preset = g.Spec.Lighting is { Length: > 0 } l ? l : WorldGenerator.LightingFor(biome) };
            g.Scene.Layout.Concept = $"A plaza (spawn, shop, leaderboard) with {count} identical {spec.Theme} plots round it, each facing the plaza; a plot's line grows button by button ({string.Join(", ", items.Select(it => it.Name))}).";
            g.Result.Notes.Add($"{count} plots of {items.Count} items each; prices {string.Join(", ", prices)}.");
        }

        private static void Plot(Generation g, int index, float cx, float cz, float yaw, List<Item> items, List<long> prices, string accent, string biome)
        {
            string root = $"Tycoon/Plot{index}";
            g.Scene.Folders[root] = new FolderSpec { Class = "Model", Tags = new List<string> { "TycoonPlot" } };
            g.Scene.Folders[root + "/Items"] = new FolderSpec { Class = "Folder" };
            float ground = g.GroundAt(cx, cz);
            var b = new PropBuilder(g.Objects, g.NewId, root, cx, ground, cz, yaw);
            float h = PlotSize / 2;
            b.Part("PlotFloor", 0, 0.5f, 0, PlotSize, 1, PlotSize, "#8FBF6A", "Grass");
            b.Part("PlotEdge", 0, 1.05f, -h + 0.5f, PlotSize, 0.1f, 1, accent, "Neon");
            b.Board("OwnerSign", "Free plot — walk in!", 0, 9, -h - 2, 16, 3, accent);
            b.Post("OwnerSignPost", -7, -h - 2.4f, 10.4f, 0.8f, KitProps.DarkWood);
            b.Post("OwnerSignPost", 7, -h - 2.4f, 10.4f, 0.8f, KitProps.DarkWood);
            b.Part("PlotSpawn", 0, 1.6f, -h + 6, 6, 1, 6, "#FFFFFF", transparency: 1, collide: false);
            b.Part("Collector", -h + 7, 1.2f, -h + 7, 7, 0.4f, 7, "#FFD34D", "Neon", tags: new[] { "TycoonCollector", "Glow" }, config: new() { ["Color"] = "#FFD34D" });
            b.Board("CollectorSign", "💰 COLLECT", -h + 7, 5.4f, -h + 11, 8, 2, "#B8860B");
            b.Post("CollectorPost", -h + 7, -h + 11.6f, 6.4f, 0.6f, KitProps.DarkWood);
            g.AddParts(9);

            // Buttons in rows along the left, each unlocking the next item.
            for (int i = 0; i < items.Count; i++)
            {
                float bx = -h + 6 + (i % 3) * 6, bz = -h + 18 + (i / 3) * 7;
                var config = new Dictionary<string, object> { ["Price"] = prices[i], ["Unlocks"] = items[i].Name, ["Label"] = items[i].Name };
                if (i > 0) config["Requires"] = items[i - 1].Name;
                b.Part("Button", bx, 1.15f, bz, 4, 0.3f, 4, i == 0 ? "#3CCB5A" : "#FFB13B", "Neon", tags: new[] { "TycoonButton" }, config: config);
                g.AddParts(1);
            }

            // The items (hidden until bought).
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                string folder = $"{root}/Items/{item.Name}";
                g.Scene.Folders[folder] = new FolderSpec { Class = "Model", Config = new Dictionary<string, object> { ["Income"] = item.Income } };
                var ib = new PropBuilder(g.Objects, g.NewId, folder, cx, ground + 1, cz, yaw);
                switch (item.Kind)
                {
                    case "conveyor":
                        ib.Part("Belt", 10, 1, -2, 6, 1, 44, "#2F2F35", "Metal", tags: new[] { "Conveyor" }, config: new() { ["Speed"] = 8 });
                        ib.Part("BeltRail", 6.6f, 1.6f, -2, 0.6f, 1.2f, 44, "#8C8C96", "Metal");
                        ib.Part("BeltRail", 13.4f, 1.6f, -2, 0.6f, 1.2f, 44, "#8C8C96", "Metal");
                        g.AddParts(3);
                        break;
                    case "dropper1": Dropper(ib, 10, 16, accent, 1); g.AddParts(5); break;
                    case "dropper2": Dropper(ib, 10, 8, accent, 1.1f); g.AddParts(5); break;
                    case "dropper3": Dropper(ib, 10, 0, accent, 1.2f); g.AddParts(5); break;
                    case "dropper4": Dropper(ib, 22, 12, "#FF6B3B", 1.5f); g.AddParts(5); break;
                    case "dropper5": Dropper(ib, 22, -2, "#B66DFF", 1.9f); g.AddParts(5); break;
                    case "upgrader":
                        ib.Part("UpgraderPost", 6.2f, 4, -10, 1.2f, 8, 1.2f, "#3A3A44", "Metal");
                        ib.Part("UpgraderPost", 13.8f, 4, -10, 1.2f, 8, 1.2f, "#3A3A44", "Metal");
                        ib.Part("UpgraderBeam", 10, 8.4f, -10, 9, 1, 1.4f, "#3A3A44", "Metal");
                        ib.Part("UpgraderGlow", 10, 4.4f, -10, 6.6f, 6.6f, 0.4f, "#36E07A", "Neon", tags: new[] { "Glow" }, config: new() { ["Color"] = "#36E07A" }, transparency: 0.4f, collide: false);
                        g.AddParts(4);
                        break;
                    case "walls":
                        foreach (var (wx, wz, sx, sz) in new[] { (0f, h - 0.6f, PlotSize, 1.2f), (-h + 0.6f, 4f, 1.2f, PlotSize - 8), (h - 0.6f, 4f, 1.2f, PlotSize - 8) })
                            ib.Part("Wall", wx, 3, wz, sx, 6, sz, "#E8DCC2", "Brick");
                        g.AddParts(3);
                        break;
                    case "garden":
                        {
                            var plants = g.Palette(Roles.Bush, biome, null, 2).Concat(g.Palette(Roles.Flower, biome, null, 2)).ToList();
                            if (plants.Count == 0)
                            {
                                for (int k = 0; k < 4; k++) ib.Ball("Hedge", -h + 4 + k * 6, 1.8f, h - 4, 3.6f, "#4FA043", "Grass");
                                g.AddParts(4);
                                break;
                            }
                            for (int k = 0; k < 5; k++)
                            {
                                // A bed along the back, clear of the wall.
                                var e = g.Pick(plants)!;
                                var w = ib.World(-h + 7 + k * 9, 0, h - 9.5f);
                                var o = g.PlaceModel(e, w[0], w[2], (float)g.Rng.NextDouble() * 360, g.ScaleFor(e, Roles.TypicalHeight(e.Role), 0.1f), folder, force: true);
                                if (o != null) o.Position[1] = ground + 1; // on the plot floor
                            }
                            break;
                        }
                    case "lamps":
                        foreach (var (lx, lz) in new[] { (-h + 3, h - 3), (h - 3, h - 3), (-h + 3, -h + 14), (h - 3, -h + 14) })
                        {
                            var w = ib.World(lx, 0, lz);
                            KitProps.Lamp(new PropBuilder(g.Objects, g.NewId, folder, w[0], ground + 1, w[2], yaw));
                            g.AddParts(4);
                        }
                        break;
                    case "statue":
                        {
                            var e = g.Palette(Roles.Statue, biome, null, 1).Concat(g.Palette(Roles.Fountain, biome, null, 1)).FirstOrDefault();
                            var w = ib.World(-14, 0, 6);
                            if (e != null)
                            {
                                var o = g.PlaceModel(e, w[0], w[2], yaw, g.ScaleFor(e, 12, 0.03f), folder, force: true);
                                if (o != null) o.Position[1] = ground + 1;
                            }
                            else
                            {
                                ib.Part("StatueBase", -14, 1, 6, 6, 2, 6, KitProps.Stone, "Slate");
                                KitProps.Crystal(new PropBuilder(g.Objects, g.NewId, folder, w[0], ground + 3, w[2], yaw), 9, accent, g.Rng, new[] { "Glow", "Sparkle" });
                                g.AddParts(5);
                            }
                            break;
                        }
                    case "deck":
                        ib.Part("DeckFloor", -12, 9, 14, 24, 1, 20, "#B07A45", "WoodPlanks");
                        foreach (var (px, pz) in new[] { (-23f, 5f), (-1f, 5f), (-23f, 23f), (-1f, 23f) }) ib.Part("DeckPost", px, 4.25f, pz, 1.2f, 8.5f, 1.2f, KitProps.DarkWood);
                        // Stairs up to the deck's front edge.
                        for (int s = 0; s < 6; s++) ib.Part("Step", -12, 0.75f + s * 1.5f, -8.5f + s * 2.2f, 5, 1.5f, 2.2f, "#B07A45", "WoodPlanks");
                        ib.Part("DeckRail", -12, 11, 24, 24, 3, 0.5f, KitProps.DarkWood);
                        g.AddParts(12);
                        break;
                }
            }
        }

        private static void Dropper(PropBuilder b, float x, float z, string color, float size)
        {
            b.Part("DropperLeg", x - 3.2f * size, 4, z, 0.8f, 8, 0.8f, "#3A3A44", "Metal");
            b.Part("DropperLeg", x + 3.2f * size, 4, z, 0.8f, 8, 0.8f, "#3A3A44", "Metal");
            b.Part("DropperBody", x, 9.5f, z, 5 * size, 3.5f * size, 5 * size, color);
            b.Part("DropperFunnel", x, 7.2f, z, 2.2f, 1.2f, 2.2f, "#2A2A30", "Metal", tags: new[] { "Dropper" });
            b.Part("DropperLight", x, 9.5f + 1.9f * size, z, 1.2f, 0.4f, 1.2f, "#FFE45C", "Neon");
        }
    }
}
