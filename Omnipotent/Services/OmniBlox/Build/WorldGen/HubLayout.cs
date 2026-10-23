namespace Omnipotent.Services.OmniBlox.Build.WorldGen
{
    /// <summary>
    /// The hub-and-spokes map (simulators, and any game whose players move through zones): a spawn hub in the
    /// middle, the zones round it in the order they open, roads out to each, forested ridges between them and
    /// mountains past the edge. See <see cref="WorldGenerator"/> for the layers.
    /// </summary>
    public static class HubLayout
    {
        public sealed class ZonePlan
        {
            public required ZoneSpec Spec { get; init; }
            public float Angle { get; set; }
            public float Cx { get; set; }
            public float Cz { get; set; }
            public float Radius { get; set; }
            public float Elevation { get; set; }
            public float DirX { get; set; }
            public float DirZ { get; set; }
            public List<float[]> Road { get; set; } = new();
            public float GateX { get; set; }
            public float GateZ { get; set; }
            public float Distance(float x, float z) => MathF.Sqrt((x - Cx) * (x - Cx) + (z - Cz) * (z - Cz));
        }

        public const float RoadWidth = 14;

        public static void Build(Generation g)
        {
            var spec = g.Spec;
            string hubBiome = (spec.Biome ?? "meadow").ToLowerInvariant();
            bool simulator = string.Equals(g.Manifest.Template, "simulator", StringComparison.OrdinalIgnoreCase);
            var zones = Prepare(spec, g.Manifest);
            int n = zones.Count;
            g.Scene.Layout = new LayoutPlan();
            float hubR = spec.Hub.Radius > 0 ? Math.Clamp(spec.Hub.Radius, 36, 90) : n >= 5 ? 60 : 52;

            // ── layout ──
            var plans = new List<ZonePlan>();
            for (int i = 0; i < n; i++)
            {
                var z = zones[i];
                float r = z.Radius > 0 ? Math.Clamp(z.Radius, 40, 110) : Math.Min(84, 60 + 4 * i);
                float jitter = n > 1 ? ((float)g.Rng.NextDouble() * 2 - 1) * 6 : 0;
                plans.Add(new ZonePlan { Spec = z, Radius = r, Angle = i * 360f / Math.Max(1, n) + (i == 0 ? 0 : jitter), Elevation = MathF.Min(14, 3 * i) });
            }
            float ring = plans.Count == 0 ? 0 : plans.Max(p => hubR + 46 + p.Radius);
            for (int i = 0; i < plans.Count && plans.Count > 1; i++)
            {
                var a = plans[i];
                var b = plans[(i + 1) % plans.Count];
                float delta = ((b.Angle - a.Angle) % 360 + 360) % 360;
                if (delta < 1) continue;
                float need = (a.Radius + b.Radius + 38) / (2 * MathF.Sin(delta * MathF.PI / 360));
                ring = MathF.Max(ring, need);
            }
            foreach (var p in plans)
            {
                float d = ring + ((float)g.Rng.NextDouble() * 2 - 1) * 8;
                var (dx, dz) = WorldGenerator.Dir(p.Angle);
                (p.DirX, p.DirZ) = (dx, dz);
                (p.Cx, p.Cz) = (dx * d, dz * d);
                // The road: from the hub's edge to just inside the zone, bowed a little so it does not look ruled.
                float sx = dx * (hubR - 3), sz = dz * (hubR - 3);
                float ex = p.Cx - dx * (p.Radius - 3), ez = p.Cz - dz * (p.Radius - 3);
                float bow = ((float)g.Rng.NextDouble() * 2 - 1) * 9;
                float mx = (sx + ex) / 2 + -dz * bow, mz = (sz + ez) / 2 + dx * bow;
                p.Road = new List<float[]> { new[] { R(sx), R(sz) }, new[] { R(mx), R(mz) }, new[] { R(ex), R(ez) } };
                (p.GateX, p.GateZ) = (p.Cx - dx * (p.Radius - 1), p.Cz - dz * (p.Radius - 1));
            }

            // The spawn sits in the gap between roads that faces the first zone across the hub.
            float spawnAngle = SpawnAngle(plans.Select(p => p.Angle).ToList());
            var (sdx, sdz) = WorldGenerator.Dir(spawnAngle);
            float spawnX = sdx * hubR * 0.55f, spawnZ = sdz * hubR * 0.55f;
            float lookX = plans.Count > 0 ? plans[0].GateX * 0.3f : 0, lookZ = plans.Count > 0 ? plans[0].GateZ * 0.3f : -10;
            float spawnYaw = WorldGenerator.YawToward(spawnX, spawnZ, lookX, lookZ);

            // The play area: the hub and every zone with a margin of scenery.
            float minX = -hubR - 24, maxX = hubR + 24, minZ = -hubR - 24, maxZ = hubR + 24;
            foreach (var p in plans)
            {
                minX = MathF.Min(minX, p.Cx - p.Radius - 22); maxX = MathF.Max(maxX, p.Cx + p.Radius + 22);
                minZ = MathF.Min(minZ, p.Cz - p.Radius - 22); maxZ = MathF.Max(maxZ, p.Cz + p.Radius + 22);
            }
            (minX, minZ, maxX, maxZ) = (Snap(minX, -1), Snap(minZ, -1), Snap(maxX, 1), Snap(maxZ, 1));
            g.Scene.Bounds.Area = new[] { minX, minZ, maxX, maxZ };
            g.Scene.Bounds.Walls = true;

            // ── land ──
            var (groundMat, plazaMat, pathMat) = WorldGenerator.MaterialsFor(hubBiome);
            if (spec.Hub.Plaza is { Length: > 0 } plaza && TerrainBuilder.Materials.Contains(plaza, StringComparer.OrdinalIgnoreCase)) plazaMat = plaza;
            const float margin = 170;
            var recipe = new TerrainRecipe
            {
                Center = new[] { R((minX + maxX) / 2), R((minZ + maxZ) / 2) },
                Size = new[] { maxX - minX + 2 * margin, maxZ - minZ + 2 * margin },
                Biome = TerrainBiome(hubBiome),
                Material = groundMat,
                Hills = 16,
                HillScale = 85,
                Seed = spec.Seed,
                FlatRadius = hubR + 20,
                BorderHeight = 90,
                BorderWidth = margin - 12,
                Undulation = 3.2f,
                UndulationScale = 64,
            };
            var ops = recipe.Ops;
            ops.Add(new TerrainOp { Kind = "flatten", Center = new[] { 0f, 0f }, Radius = hubR + 14, Height = 0, Falloff = 0.3f });

            // Ridges between neighbouring zones, or a lake in the widest gap.
            int lakeGap = -1;
            if (plans.Count >= 2 && !string.Equals(spec.Water, "none", StringComparison.OrdinalIgnoreCase))
            {
                bool want = string.Equals(spec.Water, "lake", StringComparison.OrdinalIgnoreCase) || g.Rng.NextDouble() < 0.65;
                if (want) lakeGap = Enumerable.Range(0, plans.Count).OrderByDescending(i => Gap(plans, i)).First();
            }
            for (int i = 0; i < plans.Count && plans.Count >= 2; i++)
            {
                var a = plans[i];
                var b = plans[(i + 1) % plans.Count];
                float mid = MidAngle(a.Angle, b.Angle);
                if (MathF.Abs(AngleDiff(mid, spawnAngle)) < 20) continue; // the spawn's view stays open
                var (mdx, mdz) = WorldGenerator.Dir(mid);
                float dist = (a.Distance(0, 0) + b.Distance(0, 0)) / 2 * 0.93f;
                float span = MathF.Sqrt((a.Cx - b.Cx) * (a.Cx - b.Cx) + (a.Cz - b.Cz) * (a.Cz - b.Cz)) - a.Radius - b.Radius;
                if (i == lakeGap && span > 24)
                {
                    recipe.WaterLevel = -1;
                    ops.Add(new TerrainOp { Kind = "lake", Center = new[] { R(mdx * dist), R(mdz * dist) }, Radius = Math.Clamp(span / 2 + 10, 20, 44), Height = 7, Falloff = 0.5f });
                    g.KeepCircle(mdx * dist, mdz * dist, Math.Clamp(span / 2 + 10, 20, 44) + 2);
                    g.Result.Notes.Add($"A lake between {a.Spec.Name} and {b.Spec.Name}.");
                }
                else
                {
                    ops.Add(new TerrainOp { Kind = "raise", Center = new[] { R(mdx * dist), R(mdz * dist) }, Radius = Math.Clamp(span / 2 + 22, 26, 70), Height = 13 + (float)g.Rng.NextDouble() * 9, Falloff = 0.85f });
                }
            }
            foreach (var p in plans)
            {
                var (zg, _, _) = WorldGenerator.MaterialsFor(p.Spec.Biome);
                ops.Add(new TerrainOp { Kind = "plateau", Center = new[] { R(p.Cx), R(p.Cz) }, Radius = p.Radius + 10, Height = p.Elevation, Falloff = 0.35f });
                // The zone's ground, with a ragged edge (a hard-edged disc of snow on grass looks pasted on).
                if (!zg.Equals(groundMat, StringComparison.OrdinalIgnoreCase))
                    ops.Add(new TerrainOp { Kind = "paint", Points = Blob(p.Cx, p.Cz, p.Radius + 9, g.Spec.Seed + p.Spec.Order * 13, 0.16f), Material = zg });
            }
            // Rocky mounds scattered over the land between things.
            string mound = hubBiome switch { "desert" => "Sandstone", "volcanic" => "Basalt", "snow" => "Slate", _ => "Rock" };
            int mounds = 10 + 3 * plans.Count;
            for (int k = 0, tries = 0; k < mounds && tries < 400; tries++)
            {
                float x = minX + (float)g.Rng.NextDouble() * (maxX - minX), z = minZ + (float)g.Rng.NextDouble() * (maxZ - minZ);
                if (MathF.Sqrt(x * x + z * z) < hubR + 22) continue;
                if (plans.Any(p => p.Distance(x, z) < p.Radius + 6)) continue;
                if (plans.Any(p => TerrainBuilder.DistanceToPolyline(x, z, p.Road, out _) < RoadWidth / 2 + 12)) continue;
                if (MathF.Sqrt((x - spawnX) * (x - spawnX) + (z - spawnZ) * (z - spawnZ)) < 30) continue;
                ops.Add(new TerrainOp { Kind = "raise", Center = new[] { R(x), R(z) }, Radius = 5 + (float)g.Rng.NextDouble() * 4, Height = 3 + (float)g.Rng.NextDouble() * 3.5f, Falloff = 0.9f, Material = mound });
                k++;
            }
            foreach (var p in plans)
                ops.Add(new TerrainOp { Kind = "ramp", Points = p.Road, Width = RoadWidth, Height = 0, EndHeight = p.Elevation, Material = pathMat, Falloff = 0.4f });
            foreach (var p in plans)
                ops.Add(new TerrainOp { Kind = "flatten", Center = new[] { R(p.GateX), R(p.GateZ) }, Radius = 10, Height = p.Elevation, Falloff = 0.6f });
            ops.Add(new TerrainOp { Kind = "paint", Center = new[] { 0f, 0f }, Radius = hubR * 0.86f, Material = plazaMat });
            g.SetGround(recipe);

            foreach (var p in plans) g.KeepLine(p.Road, RoadWidth / 2 + 2.5f);
            g.KeepCircle(spawnX, spawnZ, 11);

            // ── gameplay ──
            string accent = AccentFor(hubBiome);
            KitProps.Spawn(g.Objects, g.NewId, "World/Hub", R(spawnX), g.GroundAt(spawnX, spawnZ), R(spawnZ), spawnYaw, "#57C46A");
            g.AddParts(1);

            var standAngles = StandAngles(plans.Select(p => p.Angle).ToList(), spawnAngle, spawnX, spawnZ, spawnYaw, hubR);
            var standOrder = new List<(string Kind, string Label)> { ("sell", "💰 SELL"), ("upgrades", "⬆️ UPGRADES") };
            if (spec.Hub.Shop) standOrder.Add(("shop", "🛒 SHOP"));
            bool rebirth = spec.Hub.Rebirth && (g.Manifest.Rebirth?.Enabled ?? simulator);
            if (rebirth) standOrder.Add(("rebirth", "🌟 REBIRTH"));
            if (spec.Hub.Leaderboard && g.Manifest.Leaderboard) standOrder.Add(("leaderboard", ""));
            if (!simulator) standOrder.RemoveAll(s => s.Kind is "sell" or "upgrades");
            var placedStands = new List<float>();
            for (int i = 0; i < standOrder.Count && i < standAngles.Count; i++)
            {
                float angle = standAngles[i];
                var (dx, dz) = WorldGenerator.Dir(angle);
                float sr = standOrder[i].Kind == "leaderboard" ? hubR * 0.8f : hubR * 0.7f;
                float x = dx * sr, z = dz * sr;
                float yaw = WorldGenerator.YawToward(x, z, 0, 0);
                var b = g.Prop("World/Hub/" + Title(standOrder[i].Kind), x, z, yaw);
                switch (standOrder[i].Kind)
                {
                    case "sell": KitProps.SellPad(b, "#1F7A3E"); g.AddParts(8); break;
                    case "upgrades": KitProps.Stand(b, standOrder[i].Label, "upgrades", "#3D8BFF", accent); g.AddParts(9); break;
                    case "shop": KitProps.Stand(b, standOrder[i].Label, "shop", "#FF5E8A", accent); g.AddParts(9); break;
                    case "rebirth": KitProps.RebirthAltar(b); g.AddParts(7); break;
                    case "leaderboard": KitProps.Leaderboard(b); g.AddParts(4); break;
                }
                g.KeepCircle(x, z, standOrder[i].Kind == "sell" ? 12 : 10);
                g.Occupy(x, z, 9);
                placedStands.Add(angle);
            }
            if (standOrder.Count > standAngles.Count) g.Result.Warnings.Add($"only {standAngles.Count} of {standOrder.Count} hub stands fitted between the roads; make the hub bigger (hub radius)");

            // The centrepiece the spawn looks across.
            float centreFoot = Centerpiece(g, hubBiome, spec.Hub.Centerpiece);
            // Benches round it, facing it.
            var benches = g.Palette(Roles.Bench, hubBiome, null, 2);
            for (int k = 0; k < 3; k++)
            {
                float angle = spawnAngle + 180 + (k - 1) * 70;
                var (dx, dz) = WorldGenerator.Dir(angle);
                float br = centreFoot / 2 + 9;
                float x = dx * br, z = dz * br;
                if (!g.Free(x, z, 3)) continue;
                float yaw = WorldGenerator.YawToward(x, z, 0, 0);
                if (benches.Count > 0 && g.Pick(benches) is { } bench) g.PlaceModel(bench, x, z, yaw, g.ScaleFor(bench, Roles.TypicalHeight(Roles.Bench), 0.05f), "World/Hub/Benches", force: true);
                else { KitProps.Bench(g.Prop("World/Hub/Benches", x, z, yaw)); g.AddParts(4); }
                g.Occupy(x, z, 3.5f);
            }

            // Lamps round the plaza, clear of roads, stands and the spawn.
            for (float angle = 15; angle < 360; angle += 30)
            {
                if (plans.Any(p => MathF.Abs(AngleDiff(angle, p.Angle)) < 14) || placedStands.Any(s => MathF.Abs(AngleDiff(angle, s)) < 13) || MathF.Abs(AngleDiff(angle, spawnAngle)) < 14) continue;
                var (dx, dz) = WorldGenerator.Dir(angle);
                float x = dx * hubR * 0.95f, z = dz * hubR * 0.95f;
                if (!g.Free(x, z, 1.5f)) continue;
                KitProps.Lamp(g.Prop("World/Hub/Lamps", x, z, 0));
                g.AddParts(4);
                g.Occupy(x, z, 1.5f);
            }

            // ── zones ──
            string pillar = hubBiome is "snow" ? "#DDE7F0" : hubBiome is "desert" ? "#D6B27A" : KitProps.Stone;
            for (int i = 0; i < plans.Count; i++)
            {
                var p = plans[i];
                var z = p.Spec;
                float gateYaw = WorldGenerator.YawToward(p.GateX, p.GateZ, 0, 0);
                KitProps.Gate(g.Prop($"World/{Folder(z.Name)}/Gate", p.GateX, p.GateZ, gateYaw), RoadWidth + 1, z.Name, z.Price, z.Price > 0 ? Requires(plans, i) : null, z.Currency, pillar, accent);
                g.AddParts(z.Price > 0 ? 10 : 9);
                g.KeepCircle(p.GateX, p.GateZ, RoadWidth / 2 + 6);
                g.KeepCircle(p.Cx - p.DirX * (p.Radius - 14), p.Cz - p.DirZ * (p.Radius - 14), 12); // a clearing inside the gate

                // A signpost where the road leaves the hub.
                float sx = p.DirX * (hubR + 3) + -p.DirZ * (RoadWidth / 2 + 3.5f), sz = p.DirZ * (hubR + 3) + p.DirX * (RoadWidth / 2 + 3.5f);
                KitProps.Signpost(g.Prop("World/Hub/Signs", sx, sz, WorldGenerator.YawToward(sx, sz, 0, 0)), $"{Emoji(z.Biome)} {z.Name}", accent);
                g.AddParts(2);
                g.Occupy(sx, sz, 1.5f);

                Landmark(g, p);
                if (z.Resource != null) Nodes(g, p);
            }

            // ── dressing: each layer gets its share of the parts left, so none starves the others ──
            int dressing = g.DressingBudget;
            g.WithBudget(0.2f, dressing, () => Rims(g, plans));
            g.WithBudget(0.06f, dressing, () => HubBorder(g, plans, hubR, hubBiome, spawnAngle));
            g.WithBudget(0.07f, dressing, () => Roadsides(g, plans, hubBiome));
            g.WithBudget(0.09f, dressing, () => Accents(g, plans));
            g.WithBudget(0.42f, dressing, () => Belts(g, plans, hubR, hubBiome, minX, minZ, maxX, maxZ));
            g.WithBudget(0.16f, dressing, () => Framing(g, plans, hubBiome, minX, minZ, maxX, maxZ));

            // ── the plan, for the map view and the report ──
            var layout = g.Scene.Layout;
            layout.Concept = $"A spawn hub ({spec.Hub.Name}) with the sell pad and stands in sight of the spawn; {plans.Count} zone(s) round it that open in order ({string.Join(" → ", plans.Select(p => p.Spec.Name))}), each on its own plateau behind a gate, reached by a road from the hub, separated by forested ridges{(lakeGap >= 0 ? " and a lake" : "")}, with mountains past the edge.";
            layout.Zones = new List<MapZone> { new() { Id = "hub", Name = spec.Hub.Name, Area = new[] { -hubR, -hubR, hubR, hubR }, Order = 1, Purpose = simulator ? "spawn hub: sell pad, upgrades, shop" : "spawn hub", Theme = hubBiome, Circle = new[] { 0f, 0f, hubR } } };
            foreach (var p in plans)
                layout.Zones.Add(new MapZone
                {
                    Id = p.Spec.Id, Name = p.Spec.Name, Order = p.Spec.Order + 1, Theme = p.Spec.Biome + (p.Spec.Theme.Length > 0 ? ", " + p.Spec.Theme : ""),
                    Area = new[] { R(p.Cx - p.Radius), R(p.Cz - p.Radius), R(p.Cx + p.Radius), R(p.Cz + p.Radius) },
                    Purpose = p.Spec.Resource != null ? $"{p.Spec.Resource.Count} {p.Spec.Resource.Kind} nodes" + (p.Spec.Price > 0 ? $", unlocks for {p.Spec.Price}" : ", open from the start") : "",
                    Circle = new[] { R(p.Cx), R(p.Cz), p.Radius }, Entrance = new[] { R(p.GateX), R(p.GateZ) }, Elevation = p.Elevation,
                    Gated = p.Spec.Price > 0,
                });
            layout.Paths = plans.Select(p => new MapPath { From = "hub", To = p.Spec.Id, Points = p.Road, Width = RoadWidth }).ToList();
            g.Scene.Lighting = new LightingSpec { Preset = spec.Lighting is { Length: > 0 } l ? l : WorldGenerator.LightingFor(hubBiome) };
            g.Result.Notes.Add($"Play area {maxX - minX:0}×{maxZ - minZ:0} studs: hub r{hubR:0} with {placedStands.Count} stand(s); zones {string.Join(", ", plans.Select(p => $"{p.Spec.Name} (r{p.Radius:0}, +{p.Elevation:0} high{(p.Spec.Price > 0 ? ", behind a gate" : ", open")})"))}.");
        }

        // ─────────────── preparing the zones ───────────────

        public static List<ZoneSpec> Prepare(WorldSpec spec, GameManifest manifest)
        {
            bool simulator = string.Equals(manifest.Template, "simulator", StringComparison.OrdinalIgnoreCase);
            var zones = spec.Zones.Count > 0 ? spec.Zones : WorldGenerator.DefaultZones(spec.Biome, "tree");
            if (spec.Zones.Count == 0) spec.Zones = zones;
            int order = 0;
            foreach (var z in zones.OrderBy(z => z.Order <= 0 ? int.MaxValue : z.Order).ToList())
            {
                z.Order = ++order;
                if (string.IsNullOrWhiteSpace(z.Id)) z.Id = "z" + z.Order;
                if (string.IsNullOrWhiteSpace(z.Name)) z.Name = "Zone " + z.Order;
                z.Biome = string.IsNullOrWhiteSpace(z.Biome) ? spec.Biome : z.Biome.Trim().ToLowerInvariant();
                if (z.Order == 1) z.Price = 0;
                else if (simulator && z.Price <= 0) z.Price = StartingPrice(z.Order);
                if (simulator && z.Resource == null) z.Resource = new ResourceSpec { Kind = "tree", Hp = (int)Math.Round(4 * Math.Pow(2.4, z.Order - 1)), Value = (int)Math.Round(2 * Math.Pow(2.7, z.Order - 1)) };
                if (z.Resource != null)
                {
                    z.Resource.Count = Math.Clamp(z.Resource.Count <= 0 ? 12 : z.Resource.Count, 3, 30);
                    z.Resource.Hp = Math.Max(1, z.Resource.Hp);
                    z.Resource.Value = Math.Max(1, z.Resource.Value);
                    z.Resource.Respawn = Math.Clamp(z.Resource.Respawn <= 0 ? 8 : z.Resource.Respawn, 2, 120);
                }
            }
            return zones.OrderBy(z => z.Order).ToList();
        }

        /// <summary>A zone's price before the economy is balanced: the second zone 250, each later one four times the last.</summary>
        public static long StartingPrice(int order) => order <= 1 ? 0 : (long)(250 * Math.Pow(4, order - 2));

        private static string? Requires(List<ZonePlan> plans, int i)
        {
            for (int k = i - 1; k >= 0; k--)
                if (plans[k].Spec.Price > 0) return plans[k].Spec.Name;
            return null;
        }

        // ─────────────── layers ───────────────

        private static float Centerpiece(Generation g, string biome, long? chosen)
        {
            CatalogEntry? e = chosen is long id ? g.Catalog.Get(id) : null;
            e ??= g.Palette(Roles.Fountain, biome, null, 1).FirstOrDefault()
                ?? g.Palette(Roles.Statue, biome, null, 1).FirstOrDefault()
                ?? g.Palette(Roles.Landmark, biome, null, 2).FirstOrDefault(l => l.Height <= 60);
            if (e != null)
            {
                float target = e.Role == Roles.Fountain ? 11 : e.Role == Roles.Statue ? 18 : 24;
                float scale = g.ScaleFor(e, target, 0.04f);
                g.PlaceModel(e, 0, 0, 0, scale, "World/Hub/Centerpiece", "Centerpiece", force: true);
                g.KeepCircle(0, 0, e.Footprint * scale / 2 + 5);
                return e.Footprint * scale;
            }
            // Else the hub's own tallest tree, made great (a pine in a forest, a palm on a beach): it always fits the theme.
            CatalogEntry? tree = null;
            foreach (string role in new[] { Roles.Tree, Roles.Conifer, Roles.Palm })
                if ((tree = g.Palette(role, biome, null, 3).OrderByDescending(t => t.Height).FirstOrDefault()) != null) break;
            if (tree != null)
            {
                float scale = g.ScaleFor(tree, 34, 0.03f);
                g.PlaceModel(tree, 0, 0, 0, scale, "World/Hub/Centerpiece", "Great Tree", force: true);
                g.KeepCircle(0, 0, tree.Footprint * scale / 2 + 4);
                return tree.Footprint * scale;
            }
            var b = g.Prop("World/Hub/Centerpiece", 0, 0, 0);
            b.Part("Pedestal", 0, 1, 0, 8, 2, 8, KitProps.Stone, "Slate");
            KitProps.Crystal(new PropBuilder(g.Objects, g.NewId, "World/Hub/Centerpiece", 0, g.GroundAt(0, 0) + 2, 0, 0), 14, AccentFor(biome), g.Rng, new[] { "Glow", "Sparkle" });
            g.AddParts(6);
            g.KeepCircle(0, 0, 9);
            return 8;
        }

        private static void Landmark(Generation g, ZonePlan p)
        {
            float x = p.Cx + p.DirX * p.Radius * 0.45f, z = p.Cz + p.DirZ * p.Radius * 0.45f;
            float yaw = WorldGenerator.YawToward(x, z, 0, 0);
            string folder = $"World/{Folder(p.Spec.Name)}/Landmark";
            CatalogEntry? e = p.Spec.Landmark is long id ? g.Catalog.Get(id) : null;
            e ??= g.Palette(Roles.Landmark, p.Spec.Biome, p.Spec.Palette, 2).FirstOrDefault()
                ?? g.Palette(Roles.Statue, p.Spec.Biome, p.Spec.Palette, 1).FirstOrDefault();
            if (e == null)
            {
                // The zone's own tallest tree, made giant, is a landmark that always fits its theme.
                foreach (string role in new[] { Roles.Conifer, Roles.Tree, Roles.Palm, Roles.Crystal, Roles.Mushroom })
                {
                    e = g.Palette(role, p.Spec.Biome, p.Spec.Palette, 3).OrderByDescending(t => t.Height).FirstOrDefault();
                    if (e != null) break;
                }
            }
            if (e != null)
            {
                float scale = g.ScaleFor(e, e.Role is Roles.Landmark or Roles.Statue ? 40 : 50, 0.05f);
                if (e.Role is not (Roles.Landmark or Roles.Statue)) scale = Math.Clamp(50 / MathF.Max(1, e.Height), 1.2f, 3.2f);
                g.PlaceModel(e, x, z, e.Role is Roles.Landmark or Roles.Statue ? yaw : (float)g.Rng.NextDouble() * 360, scale, folder, p.Spec.Name + " Landmark", force: true);
                g.KeepCircle(x, z, e.Footprint * scale / 2 + 4);
                g.Scene.Layout.Landmarks.Add(new MapLandmark { Name = p.Spec.Name + " landmark", Position = new[] { R(x), R(z) }, Note = Gen(e) });
                return;
            }
            KitProps.Crystal(g.Prop(folder, x, z, yaw), 26, AccentFor(p.Spec.Biome), g.Rng, new[] { "Glow", "Sparkle" });
            g.AddParts(4);
            g.KeepCircle(x, z, 9);
            g.Scene.Layout.Landmarks.Add(new MapLandmark { Name = p.Spec.Name + " crystal", Position = new[] { R(x), R(z) }, Note = "kit crystal (no library landmark fits)" });
        }

        /// <summary>The zone's resource nodes: tagged Resource with the zone, hits, value and respawn the kit reads.</summary>
        private static void Nodes(Generation g, ZonePlan p)
        {
            var res = p.Spec.Resource!;
            string folder = $"World/{Folder(p.Spec.Name)}/Nodes";
            var palette = res.Assets.Select(id => g.Catalog.Get(id)).OfType<CatalogEntry>().ToList();
            if (palette.Count == 0)
                foreach (string role in WorldGenerator.RolesForResource(res.Kind, p.Spec.Biome))
                {
                    palette = g.Palette(role, p.Spec.Biome, p.Spec.Palette, 3);
                    if (palette.Count > 0) break;
                }
            float targetHeight = (WorldGenerator.RolesForResource(res.Kind, p.Spec.Biome)[0] switch
            {
                Roles.Tree or Roles.Conifer or Roles.Palm or Roles.DeadTree => 16 + 4 * p.Spec.Order,
                Roles.Rock or Roles.Boulder => 4 + 1.4f * p.Spec.Order,
                Roles.Crystal => 6 + 2 * p.Spec.Order,
                Roles.Cactus => 8 + 2 * p.Spec.Order,
                _ => 4 + 1.5f * p.Spec.Order,
            }) * (res.Scale > 0 ? res.Scale : 1);
            float foot = palette.Count > 0 ? palette.Max(e => e.Footprint * Math.Clamp(targetHeight / MathF.Max(1, e.Height), 0.4f, 3.2f)) : targetHeight * 0.5f;
            float spacing = MathF.Max(13, MathF.Min(foot * 1.25f, 28));
            var config = new Dictionary<string, object> { ["Zone"] = p.Spec.Name, ["Hp"] = res.Hp, ["Value"] = res.Value, ["Respawn"] = res.Respawn, ["Kind"] = res.Kind };
            var spots = g.Darts(p.Cx - p.Radius, p.Cz - p.Radius, p.Cx + p.Radius, p.Cz + p.Radius, spacing, res.Count, (x, z) =>
                p.Distance(x, z) < p.Radius - 9 && g.Free(x, z, spacing * 0.35f) && !g.Wet(x, z) && g.SlopeAt(x, z) < 0.55f);
            int made = 0;
            foreach (var (x, z) in spots)
            {
                if (palette.Count > 0 && g.Pick(palette) is { } e)
                {
                    float scale = Math.Clamp(targetHeight / MathF.Max(0.5f, e.Height), 0.4f, 3.2f) * (0.92f + (float)g.Rng.NextDouble() * 0.16f);
                    g.PlaceModel(e, x, z, (float)g.Rng.NextDouble() * 360, scale, folder, $"{p.Spec.Name} {Title(res.Kind)}", new[] { "Resource" }, new Dictionary<string, object>(config), force: true, occupyFactor: 0.5f);
                }
                else
                {
                    // No model: a kit-built node, its parts grouped in a Model the kit treats as one node.
                    string nodeFolder = $"{folder}/Node{made + 1}";
                    g.Scene.Folders[nodeFolder] = new FolderSpec { Class = "Model", Tags = new List<string> { "Resource" }, Config = new Dictionary<string, object>(config) };
                    var b = g.Prop(nodeFolder, x, z, (float)g.Rng.NextDouble() * 360);
                    if (res.Kind is "tree" or "wood" or "log" or "pine" or "palm") KitProps.Tree(b, targetHeight, LeafColor(p.Spec.Biome), g.Rng);
                    else KitProps.Crystal(b, MathF.Max(5, targetHeight), AccentFor(p.Spec.Biome), g.Rng);
                    g.AddParts(4);
                    g.Occupy(x, z, foot * 0.4f);
                }
                made++;
            }
            if (made < res.Count) g.Result.Warnings.Add($"{p.Spec.Name}: only {made} of {res.Count} nodes fitted (make the zone bigger or the nodes fewer)");
            if (palette.Count == 0) g.Result.Warnings.Add($"{p.Spec.Name}: no library model for '{res.Kind}' in this style; kit-built nodes used (the harvester has been told what to find)");
        }

        /// <summary>A ring of trees and bushes round each zone, open at its gate: what gives a zone its shape.</summary>
        private static void Rims(Generation g, List<ZonePlan> plans)
        {
            foreach (var p in plans)
            {
                var trees = TreesFor(g, p.Spec.Biome, p.Spec.Palette);
                var bushes = g.Palette(Roles.Bush, p.Spec.Biome, p.Spec.Palette, 3);
                float open = 21 * MathF.PI / 180 + (RoadWidth / 2 + 7) / p.Radius;
                float gateAngle = MathF.Atan2(-p.DirZ, -p.DirX);
                float spacing = 10.5f / MathF.Sqrt(Math.Clamp(g.Spec.Density, 0.5f, 1.6f));
                var spots = g.Darts(p.Cx - p.Radius - 18, p.Cz - p.Radius - 18, p.Cx + p.Radius + 18, p.Cz + p.Radius + 18, spacing, 400, (x, z) =>
                {
                    float d = p.Distance(x, z);
                    if (d < p.Radius - 3 || d > p.Radius + 15) return false;
                    float a = MathF.Atan2(z - p.Cz, x - p.Cx);
                    if (MathF.Abs(WrapRad(a - gateAngle)) < open) return false;
                    return g.Free(x, z, 2.5f) && !g.Wet(x, z) && g.SlopeAt(x, z) < 1.1f;
                });
                foreach (var (x, z) in spots)
                {
                    bool bush = bushes.Count > 0 && g.Rng.NextDouble() < 0.28;
                    var e = g.Pick(bush ? bushes : trees.Count > 0 ? trees : bushes);
                    if (e == null) continue;
                    float scale = g.ScaleFor(e, Roles.TypicalHeight(e.Role) * (bush ? 1 : 1.05f));
                    if (g.PlaceModel(e, x, z, (float)g.Rng.NextDouble() * 360, scale, $"World/{Folder(p.Spec.Name)}/Rim", occupyFactor: 0.3f) == null && g.OutOfBudget) return;
                }
            }
        }

        /// <summary>A hedge round the hub between its roads (a park's edge rather than an open field).</summary>
        private static void HubBorder(Generation g, List<ZonePlan> plans, float hubR, string biome, float spawnAngle)
        {
            var bushes = g.Palette(Roles.Bush, biome, null, 2);
            var flowers = g.Palette(Roles.Flower, biome, null, 3);
            if (bushes.Count == 0 && flowers.Count == 0) return;
            float r = hubR + 4;
            float step = 360 / (MathF.PI * 2 * r / 7.5f);
            for (float angle = 0; angle < 360; angle += step)
            {
                if (plans.Any(p => MathF.Abs(AngleDiff(angle, p.Angle)) < (RoadWidth / 2 + 6) / r * 180 / MathF.PI)) continue;
                var (dx, dz) = WorldGenerator.Dir(angle);
                float x = dx * r, z = dz * r;
                if (!g.Free(x, z, 2)) continue;
                var e = bushes.Count > 0 && (flowers.Count == 0 || g.Rng.NextDouble() < 0.7) ? g.Pick(bushes) : g.Pick(flowers);
                if (e == null) continue;
                if (g.PlaceModel(e, x, z, (float)g.Rng.NextDouble() * 360, g.ScaleFor(e, Roles.TypicalHeight(e.Role) * 0.9f, 0.12f), "World/Hub/Hedge", occupyFactor: 0.3f) == null && g.OutOfBudget) return;
            }
            // Flower beds by the benches and lamps.
            foreach (var (x, z) in g.Darts(-hubR, -hubR, hubR, hubR, 7, 10, (x, z) => { float d = MathF.Sqrt(x * x + z * z); return d > 12 && d < hubR * 0.9f && g.Free(x, z, 2.5f); }))
                if (g.Pick(flowers) is { } f) g.PlaceModel(f, x, z, (float)g.Rng.NextDouble() * 360, g.ScaleFor(f, Roles.TypicalHeight(Roles.Flower), 0.15f), "World/Hub/Flowers", occupyFactor: 0.35f);
        }

        /// <summary>Lamps along each road (every ~40 studs, alternating sides) and plants and rocks at its edges.</summary>
        private static void Roadsides(Generation g, List<ZonePlan> plans, string biome)
        {
            foreach (var p in plans)
            {
                var edge = g.Palette(Roles.Flower, p.Spec.Biome, p.Spec.Palette, 3).Concat(g.Palette(Roles.Rock, p.Spec.Biome, p.Spec.Palette, 2)).Concat(g.Palette(Roles.Bush, p.Spec.Biome, p.Spec.Palette, 2)).ToList();
                float length = PathLength(p.Road);
                int side = 1;
                for (float t = 18; t < length - 12; t += 40)
                {
                    var (x, z, ux, uz) = PointAlong(p.Road, t);
                    float ox = -uz * side * (RoadWidth / 2 + 2.6f), oz = ux * side * (RoadWidth / 2 + 2.6f);
                    if (g.Unoccupied(x + ox, z + oz, 1.2f) && !g.Wet(x + ox, z + oz))
                    {
                        KitProps.Lamp(g.Prop($"World/{Folder(p.Spec.Name)}/Road", x + ox, z + oz, 0));
                        g.AddParts(4);
                        g.Occupy(x + ox, z + oz, 1.4f);
                    }
                    side = -side;
                }
                if (edge.Count == 0) continue;
                for (float t = 8; t < length - 6; t += 9)
                {
                    if (g.Rng.NextDouble() < 0.45) continue;
                    var (x, z, ux, uz) = PointAlong(p.Road, t);
                    int s = g.Rng.NextDouble() < 0.5 ? 1 : -1;
                    float off = RoadWidth / 2 + 3.5f + (float)g.Rng.NextDouble() * 4;
                    float px = x - uz * s * off, pz = z + ux * s * off;
                    if (!g.Free(px, pz, 1.8f) || g.Wet(px, pz)) continue;
                    var e = g.Pick(edge);
                    if (e == null) continue;
                    if (g.PlaceModel(e, px, pz, (float)g.Rng.NextDouble() * 360, g.ScaleFor(e, Roles.TypicalHeight(e.Role), 0.18f), $"World/{Folder(p.Spec.Name)}/Road", occupyFactor: 0.35f) == null && g.OutOfBudget) return;
                }
            }
        }

        /// <summary>A few small things inside each zone (flowers, rocks, the biome's own touches), kept off the nodes.</summary>
        private static void Accents(Generation g, List<ZonePlan> plans)
        {
            foreach (var p in plans)
            {
                var roles = new List<string> { Roles.Flower, Roles.Rock, Roles.Bush };
                roles.AddRange(p.Spec.Biome switch
                {
                    "forest" => new[] { Roles.Mushroom, Roles.Stump, Roles.Log },
                    "autumn" => new[] { Roles.Pumpkin, Roles.Stump, Roles.Log },
                    "spooky" => new[] { Roles.Pumpkin, Roles.DeadTree },
                    "desert" => new[] { Roles.Cactus },
                    "beach" or "jungle" => new[] { Roles.Palm },
                    "candy" => new[] { Roles.Candy },
                    "crystal" or "volcanic" => new[] { Roles.Crystal },
                    "snow" => new[] { Roles.Conifer },
                    _ => new[] { Roles.Stump },
                });
                var palette = roles.SelectMany(r => g.Palette(r, p.Spec.Biome, p.Spec.Palette, 2)).ToList();
                if (palette.Count == 0) continue;
                int count = (int)(10 * Math.Clamp(g.Spec.Density, 0.5f, 1.6f));
                foreach (var (cx, cz) in g.Darts(p.Cx - p.Radius, p.Cz - p.Radius, p.Cx + p.Radius, p.Cz + p.Radius, 14, count, (x, z) =>
                    p.Distance(x, z) < p.Radius - 4 && g.Free(x, z, 3) && !g.Wet(x, z)))
                {
                    var e = g.Pick(palette);
                    if (e == null) continue;
                    // Flowers come in patches; everything else alone.
                    int patch = e.Role == Roles.Flower ? 3 + g.Rng.Next(3) : 1;
                    for (int k = 0; k < patch; k++)
                    {
                        float x = cx + (k == 0 ? 0 : ((float)g.Rng.NextDouble() * 2 - 1) * 4), z = cz + (k == 0 ? 0 : ((float)g.Rng.NextDouble() * 2 - 1) * 4);
                        if (k > 0 && !g.Free(x, z, 1)) continue;
                        if (g.PlaceModel(e, x, z, (float)g.Rng.NextDouble() * 360, g.ScaleFor(e, Roles.TypicalHeight(e.Role)), $"World/{Folder(p.Spec.Name)}/Accents", occupyFactor: 0.4f) == null && g.OutOfBudget) return;
                    }
                }
            }
        }

        /// <summary>
        /// The groves on the land between zones: dense where the noise says so, open in small clearings, mixed
        /// trees with bushes and rocks under them, filling the budget left after everything else.
        /// </summary>
        private static void Belts(Generation g, List<ZonePlan> plans, float hubR, string hubBiome, float minX, float minZ, float maxX, float maxZ)
        {
            float pad = 34;
            bool InBelt(float x, float z)
            {
                if (MathF.Sqrt(x * x + z * z) < hubR + 9) return false;
                foreach (var p in plans) if (p.Distance(x, z) < p.Radius + 14) return false;
                return x > minX - pad && x < maxX + pad && z > minZ - pad && z < maxZ + pad;
            }
            float area = (maxX - minX + 2 * pad) * (maxZ - minZ + 2 * pad) - MathF.PI * (hubR + 9) * (hubR + 9) - plans.Sum(p => MathF.PI * (p.Radius + 14) * (p.Radius + 14));
            var sample = TreesFor(g, hubBiome, null).Concat(g.Palette(Roles.Bush, hubBiome, null, 2)).ToList();
            float avgParts = sample.Count > 0 ? (float)sample.Average(e => Math.Max(1, e.Parts)) : 6;
            int remaining = Math.Max(0, g.Budget - g.Parts - 180);
            float maxObjects = remaining * 0.82f / avgParts;
            // Groves and meadows: about half the land (where the noise is high) is wooded, densely; the rest stays open
            // grass with the odd tree, so the land reads as woods and clearings rather than trees sprinkled evenly.
            float spacing = Math.Clamp(MathF.Sqrt(MathF.Max(1, area * 0.5f) / MathF.Max(1, maxObjects * 0.8f)), 8, 22) / MathF.Sqrt(Math.Clamp(g.Spec.Density, 0.5f, 1.6f));
            var spots = g.Darts(minX - pad, minZ - pad, maxX + pad, maxZ + pad, spacing, 5000, (x, z) =>
            {
                if (!InBelt(x, z) || !g.Free(x, z, 2.2f) || g.Wet(x, z) || g.SlopeAt(x, z) > 1.2f) return false;
                float n = TerrainBuilder.Fbm(x / 52f, z / 52f, g.Spec.Seed + 5, 3);
                return n > -0.02f || g.Rng.NextDouble() < 0.06;
            }, attemptsPerPoint: 6);
            int placed = 0;
            foreach (var (x, z) in spots.OrderBy(s => MathF.Sqrt(s.X * s.X + s.Z * s.Z)))
            {
                string biome = NearestBiome(plans, hubBiome, x, z);
                double roll = g.Rng.NextDouble();
                List<CatalogEntry> palette = roll < 0.66 ? TreesFor(g, biome, null) : roll < 0.88 ? g.Palette(Roles.Bush, biome, null, 3) : g.Palette(Roles.Rock, biome, null, 2);
                if (palette.Count == 0) palette = TreesFor(g, biome, null);
                var e = g.Pick(palette);
                if (e == null) continue;
                var o = g.PlaceModel(e, x, z, (float)g.Rng.NextDouble() * 360, g.ScaleFor(e, Roles.TypicalHeight(e.Role)), "World/Scenery/Groves", occupyFactor: 0.28f);
                if (o == null && g.OutOfBudget) break;
                if (o != null) placed++;
            }
            g.Result.Notes.Add($"Groves: {placed} trees, bushes and rocks between the zones ({spacing:0.#} studs apart where dense).");
        }

        /// <summary>A tree line just past the play area, so its edge is a forest rather than an invisible wall in a field.</summary>
        private static void Framing(Generation g, List<ZonePlan> plans, string hubBiome, float minX, float minZ, float maxX, float maxZ)
        {
            var trees = TreesFor(g, hubBiome, null);
            if (trees.Count == 0) return;
            foreach (var (x, z) in g.Darts(minX - 70, minZ - 70, maxX + 70, maxZ + 70, 13, 900, (x, z) =>
            {
                bool outside = x < minX || x > maxX || z < minZ || z > maxZ;
                bool near = x > minX - 70 && x < maxX + 70 && z > minZ - 70 && z < maxZ + 70;
                return outside && near && g.Free(x, z, 2.5f) && !g.Wet(x, z) && g.SlopeAt(x, z) < 1.4f;
            }, attemptsPerPoint: 5))
            {
                var e = g.Pick(TreesFor(g, NearestBiome(plans, hubBiome, x, z), null));
                if (e == null) continue;
                if (g.PlaceModel(e, x, z, (float)g.Rng.NextDouble() * 360, g.ScaleFor(e, Roles.TypicalHeight(e.Role) * 1.1f), "World/Scenery/Treeline", occupyFactor: 0.3f) == null && g.OutOfBudget) break;
            }
        }

        // ─────────────── helpers ───────────────

        private static List<CatalogEntry> TreesFor(Generation g, string biome, IDictionary<string, List<long>>? palette)
        {
            string main = biome switch { "snow" => Roles.Conifer, "beach" or "jungle" => Roles.Palm, "spooky" => Roles.DeadTree, "desert" => Roles.Cactus, "crystal" => Roles.Crystal, "candy" => Roles.Candy, _ => Roles.Tree };
            var list = g.Palette(main, biome, palette, 3).ToList();
            if (biome is "forest" or "snow" or "autumn" or "meadow") list.AddRange(g.Palette(biome == "snow" ? Roles.Tree : Roles.Conifer, biome, palette, 2).Where(e => list.All(l => l.AssetId != e.AssetId)));
            if (list.Count == 0) list = g.Palette(Roles.Tree, biome, palette, 3);
            if (list.Count == 0) list = g.Palette(Roles.Conifer, biome, palette, 3);
            return list;
        }

        private static string NearestBiome(List<ZonePlan> plans, string hub, float x, float z)
        {
            var near = plans.OrderBy(p => p.Distance(x, z)).FirstOrDefault();
            if (near == null) return hub;
            float hubDistance = MathF.Sqrt(x * x + z * z);
            return near.Distance(x, z) < hubDistance ? near.Spec.Biome : hub;
        }

        /// <summary>The hub's stand positions, in the order stands are placed: the sell pad nearest the spawn and in its view, then outward.</summary>
        private static List<float> StandAngles(List<float> roads, float spawnAngle, float spawnX, float spawnZ, float spawnYaw, float hubR)
        {
            // Where the spawn looks: yaw 0 faces −Z.
            float fx = -MathF.Sin(spawnYaw * MathF.PI / 180), fz = -MathF.Cos(spawnYaw * MathF.PI / 180);
            var candidates = new List<(float Angle, float Score)>();
            for (float a = 0; a < 360; a += 5)
            {
                if (roads.Any(r => MathF.Abs(AngleDiff(a, r)) < 21) || MathF.Abs(AngleDiff(a, spawnAngle)) < 26) continue;
                var (dx, dz) = WorldGenerator.Dir(a);
                float x = dx * hubR * 0.7f - spawnX, z = dz * hubR * 0.7f - spawnZ;
                float dist = MathF.Sqrt(x * x + z * z);
                float ahead = (x * fx + z * fz) / MathF.Max(1, dist);
                candidates.Add((a, dist - (ahead > 0.15f ? 30 : 0) - ahead * 10));
            }
            var chosen = new List<float>();
            foreach (var (a, _) in candidates.OrderBy(c => c.Score))
                if (chosen.All(c => MathF.Abs(AngleDiff(a, c)) >= 31)) chosen.Add(a);
            return chosen;
        }

        public static float SpawnAngle(List<float> zoneAngles)
        {
            if (zoneAngles.Count == 0) return 180;
            var sorted = zoneAngles.Select(a => (a % 360 + 360) % 360).OrderBy(a => a).ToList();
            (float From, float To)? best = null;
            for (int i = 0; i < sorted.Count; i++)
            {
                float from = sorted[i], to = i + 1 < sorted.Count ? sorted[i + 1] : sorted[0] + 360;
                bool holds180 = (from <= 180 && to >= 180) || (from <= 540 && to >= 540);
                if (holds180) return ((from + to) / 2) % 360;
                if (best == null || to - from > best.Value.To - best.Value.From) best = (from, to);
            }
            return ((best!.Value.From + best.Value.To) / 2) % 360;
        }

        private static float Gap(List<ZonePlan> plans, int i)
        {
            var a = plans[i];
            var b = plans[(i + 1) % plans.Count];
            return MathF.Sqrt((a.Cx - b.Cx) * (a.Cx - b.Cx) + (a.Cz - b.Cz) * (a.Cz - b.Cz)) - a.Radius - b.Radius;
        }

        /// <summary>A rough circle: <paramref name="points"/> points round (x, z) at a radius that wanders by up to <paramref name="rough"/> of it.</summary>
        public static List<float[]> Blob(float x, float z, float radius, int seed, float rough, int points = 28)
        {
            var list = new List<float[]>();
            for (int i = 0; i < points; i++)
            {
                float a = i * MathF.PI * 2 / points;
                float r = radius * (1 + rough * TerrainBuilder.Fbm(MathF.Cos(a) * 1.7f + seed * 0.37f, MathF.Sin(a) * 1.7f, seed, 2));
                list.Add(new[] { R(x + MathF.Cos(a) * r), R(z + MathF.Sin(a) * r) });
            }
            return list;
        }

        public static float AngleDiff(float a, float b)
        {
            float d = ((a - b) % 360 + 540) % 360 - 180;
            return d;
        }

        private static float MidAngle(float a, float b)
        {
            float d = ((b - a) % 360 + 360) % 360;
            return (a + d / 2) % 360;
        }

        private static float WrapRad(float a)
        {
            while (a > MathF.PI) a -= 2 * MathF.PI;
            while (a < -MathF.PI) a += 2 * MathF.PI;
            return a;
        }

        public static float PathLength(List<float[]> pts)
        {
            float len = 0;
            for (int i = 0; i + 1 < pts.Count; i++) len += MathF.Sqrt((pts[i + 1][0] - pts[i][0]) * (pts[i + 1][0] - pts[i][0]) + (pts[i + 1][1] - pts[i][1]) * (pts[i + 1][1] - pts[i][1]));
            return len;
        }

        /// <summary>The point <paramref name="t"/> studs along a polyline, with the unit direction of travel there.</summary>
        public static (float X, float Z, float Ux, float Uz) PointAlong(List<float[]> pts, float t)
        {
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                float dx = pts[i + 1][0] - pts[i][0], dz = pts[i + 1][1] - pts[i][1];
                float len = MathF.Sqrt(dx * dx + dz * dz);
                if (len < 1e-4f) continue;
                if (t <= len || i + 2 == pts.Count)
                {
                    float k = Math.Clamp(t / len, 0, 1);
                    return (pts[i][0] + dx * k, pts[i][1] + dz * k, dx / len, dz / len);
                }
                t -= len;
            }
            return (pts[0][0], pts[0][1], 0, -1);
        }

        private static string TerrainBiome(string biome) => biome switch
        {
            "forest" or "jungle" or "spooky" => "forest",
            "desert" => "desert",
            "snow" or "crystal" => "snow",
            "beach" => "beach",
            "volcanic" => "volcanic",
            "candy" => "candy",
            _ => "meadow",
        };

        public static string AccentFor(string biome) => biome switch
        {
            "forest" => "#2E8B57",
            "autumn" => "#E07A1F",
            "snow" => "#3FA9F5",
            "desert" => "#E0A21B",
            "beach" => "#13B5C8",
            "jungle" => "#2FA84F",
            "volcanic" => "#E8442E",
            "candy" => "#FF5FB8",
            "spooky" => "#8E44FF",
            "crystal" => "#7B6CFF",
            _ => "#3D8BFF",
        };

        private static string LeafColor(string biome) => biome switch
        {
            "autumn" => "#E0782A",
            "snow" => "#2F6B4F",
            "candy" => "#FF8BD0",
            "spooky" => "#4B3A5A",
            _ => "#4FA043",
        };

        private static string Emoji(string biome) => biome switch
        {
            "forest" => "🌲", "autumn" => "🍂", "snow" => "❄️", "desert" => "🌵", "beach" => "🏝️", "jungle" => "🌴",
            "volcanic" => "🌋", "candy" => "🍭", "spooky" => "🎃", "crystal" => "💎", _ => "🌼",
        };

        public static string Folder(string name)
        {
            string s = new string(name.Where(ch => char.IsLetterOrDigit(ch) || ch == ' ').ToArray()).Trim();
            return s.Length == 0 ? "Zone" : s.Replace(' ', '_');
        }

        private static string Title(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

        private static string Gen(CatalogEntry e) => $"#{e.AssetId} {e.Name}";

        private static float Snap(float v, int dir) => dir < 0 ? MathF.Floor(v / 4) * 4 : MathF.Ceiling(v / 4) * 4;

        private static float R(float v) => MathF.Round(v, 1);
    }
}
