namespace Omnipotent.Services.OmniBlox.Build.WorldGen
{
    /// <summary>
    /// An obby course built to the genre's rules: the first stages anyone can clear, one new mechanic at a time
    /// (hops, stairs, beams, lava strips, moving platforms, vanishing platforms, spinners, trusses, bounce pads),
    /// combinations only once each has been met alone, a breather after every hard stretch, a checkpoint at every
    /// stage, a new look every few stages, all over a lava basin with a kill floor, and a golden finish. Every jump
    /// stays inside what a default character can make (rises up to 3 studs, gaps up to 8), so the course is
    /// clearable by construction — and the geometry verifier proves it again on the compiled world.
    /// </summary>
    public static class ObbyCourse
    {
        private sealed record Theme(string Name, string Platform, string Accent, string Material, string Biome);

        private static readonly Dictionary<string, Theme> Themes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["meadow"] = new("Meadow", "#6CCB5F", "#FFD93D", "SmoothPlastic", "meadow"),
            ["candy"] = new("Candy", "#FF8AD8", "#8AE1FF", "SmoothPlastic", "candy"),
            ["lava"] = new("Lava", "#4A4A50", "#FF8A1F", "Slate", "volcanic"),
            ["ice"] = new("Ice", "#BFE9FF", "#FFFFFF", "SmoothPlastic", "snow"),
            ["sky"] = new("Sky", "#F4F7FF", "#FFD24A", "SmoothPlastic", "meadow"),
            ["jungle"] = new("Jungle", "#4E9F3D", "#C8873E", "WoodPlanks", "jungle"),
            ["space"] = new("Space", "#5B4BDB", "#3DF5FF", "SmoothPlastic", "crystal"),
            ["desert"] = new("Desert", "#E3C07A", "#C2683A", "Sandstone", "desert"),
            ["forest"] = new("Forest", "#8B5A2B", "#7BD35A", "WoodPlanks", "forest"),
        };

        private const string Kill = "#FF3B30";
        private const float RowLength = 250;

        private sealed class Cursor
        {
            public float X, Y, Z;
            public float Dx = 0, Dz = -1; // heading
            public float Rx => -Dz;       // right of the heading
            public float Rz => Dx;
            public int Row;
            public float RowStart;
            public float Along(float sx, float sz) => (X - sx) * Dx + (Z - sz) * Dz;
        }

        public static void Build(Generation g)
        {
            var spec = g.Spec.Course ?? new CourseSpec();
            g.Spec.Course = spec;
            int stages = Math.Clamp(spec.Stages, 5, 80);
            float hard = spec.Difficulty?.ToLowerInvariant() switch { "easy" => 0.75f, "hard" => 1.2f, _ => 1f };
            var themes = (spec.Themes.Count > 0 ? spec.Themes : new List<string> { "meadow", "candy", "ice", "lava", "sky" })
                .Select(t => Themes.TryGetValue(t, out var th) ? th : Themes["meadow"]).ToList();
            g.Scene.Layout = new LayoutPlan();

            // The course is built first (in the air), then the land is shaped under and round it.
            var c = new Cursor { X = 0, Y = 14, Z = 120 };
            c.RowStart = c.Z;
            var surfaces = new List<(float MinX, float MinZ, float MaxX, float MaxZ)>();

            // Start platform: the spawn, the shop and the board, and a START arch.
            float startTop = c.Y;
            var start = g.PropAt("Obby/Start", c.X, startTop - 1.2f, c.Z, 0);
            start.Part("StartPlatform", 0, 0.6f, 0, 38, 1.2f, 38, "#7A6A58", "Slate");
            g.AddParts(1);
            // The spawn looks straight down the course through the START arch; the shop and the board stand to the
            // sides, facing the path, so they are seen without blocking the view.
            KitProps.Spawn(g.Objects, g.NewId, "Obby/Start", c.X, startTop, c.Z + 9, 0, "#57C46A");
            g.AddParts(1);
            KitProps.Gate(g.PropAt("Obby/Start", c.X, startTop, c.Z - 13, 0), 12, "START", 0, null, null, KitProps.Stone, themes[0].Accent);
            g.AddParts(9);
            if (g.Manifest.Leaderboard) { KitProps.Leaderboard(g.PropAt("Obby/Start", c.X + 14, startTop, c.Z - 2, 90)); g.AddParts(4); }
            KitProps.Stand(g.PropAt("Obby/Start", c.X - 13, startTop, c.Z - 1, 270), "🛒 SHOP", "shop", "#FF5E8A", themes[0].Accent);
            g.AddParts(9);
            surfaces.Add((c.X - 19, c.Z - 19, c.X + 19, c.Z + 19));
            c.Z -= 19;

            var plan = Plan(stages, hard);
            for (int stage = 1; stage <= stages; stage++)
            {
                var theme = themes[Math.Min(themes.Count - 1, (stage - 1) * themes.Count / stages)];
                string folder = $"Obby/Stage{stage:00}";
                // Checkpoint pad (stage 1's is at the start of the course, right by the spawn).
                Advance(g, c, 4, 0);
                var cp = g.PropAt(folder, c.X + c.Dx * 5, c.Y - 1, c.Z + c.Dz * 5, Yaw(c));
                cp.Part("Checkpoint", 0, 0.5f, 0, 10, 1, 10, "#3CCB5A", "SmoothPlastic", tags: new[] { "Checkpoint", "Glow" }, config: new() { ["Stage"] = stage, ["Color"] = "#5BFF8A", ["Range"] = 10 });
                cp.Post("Flag", 4.2f, 4.2f, 7, 0.4f, "#FFFFFF", "SmoothPlastic", 1);
                cp.Board("StageSign", $"STAGE {stage}", 2.4f, 6.4f, 4.2f, 4.2f, 1.6f, theme.Accent);
                g.AddParts(3);
                surfaces.Add(Box(c.X + c.Dx * 5, c.Z + c.Dz * 5, 5));
                MoveTo(c, c.X + c.Dx * 10, c.Y, c.Z + c.Dz * 10);

                foreach (var step in plan[stage - 1])
                    BuildObstacle(g, c, step, theme, folder, surfaces);

                // Turn at the end of a row: a landing and a short side passage, then back the other way.
                if (stage < stages && c.Along(0, c.RowStart) > RowLength)
                    Turn(g, c, theme, folder, surfaces);
            }

            // The finish: a golden platform with a trophy.
            Advance(g, c, 5, 1);
            float fx = c.X + c.Dx * 11, fz = c.Z + c.Dz * 11;
            var fin = g.PropAt("Obby/Finish", fx, c.Y - 1.2f, fz, Yaw(c));
            fin.Part("FinishPlatform", 0, 0.6f, 0, 22, 1.2f, 22, "#E8B923", "SmoothPlastic");
            fin.Part("FinishPad", 0, 1.35f, 0, 12, 0.3f, 12, "#FFE45C", "Neon", tags: new[] { "Finish", "Glow", "Sparkle" }, config: new() { ["Color"] = "#FFE45C" });
            fin.Part("TrophyBase", 0, 2.2f, 7, 4, 1.4f, 4, "#B8860B");
            fin.Post("TrophyStem", 0, 7, 3, 1.2f, "#FFD700", "SmoothPlastic", 2.9f);
            fin.Ball("TrophyCup", 0, 7.6f, 7, 4.2f, "#FFD700", "Neon", tags: new[] { "Spin", "Sparkle" }, config: new() { ["Speed"] = 45 });
            fin.Board("FinishSign", "🏆 YOU WIN!", 0, 11.5f, 7, 12, 3, "#E0A21B");
            g.AddParts(6);
            surfaces.Add(Box(fx, fz, 11));

            // ── the land under the course ──
            float minX = surfaces.Min(s => s.MinX) - 40, maxX = surfaces.Max(s => s.MaxX) + 40;
            float minZ = surfaces.Min(s => s.MinZ) - 40, maxZ = surfaces.Max(s => s.MaxZ) + 40;
            minX = MathF.Floor(minX / 4) * 4; minZ = MathF.Floor(minZ / 4) * 4; maxX = MathF.Ceiling(maxX / 4) * 4; maxZ = MathF.Ceiling(maxZ / 4) * 4;
            g.Scene.Bounds.Area = new[] { minX, minZ, maxX, maxZ };
            g.Scene.Bounds.Walls = true;
            string floor = (spec.Floor ?? "lava").ToLowerInvariant();
            float cx = (minX + maxX) / 2, cz = (minZ + maxZ) / 2;
            float basin = MathF.Max(maxX - minX, maxZ - minZ) / 2 + 10;
            var recipe = new TerrainRecipe
            {
                Center = new[] { cx, cz }, Size = new[] { maxX - minX + 300, maxZ - minZ + 300 },
                Biome = floor == "lava" ? "volcanic" : "meadow", Material = floor == "lava" ? "Basalt" : "Grass",
                Hills = 26, HillScale = 70, Seed = g.Spec.Seed, FlatRadius = basin, BorderHeight = 110, BorderWidth = 130, Undulation = 2,
            };
            recipe.Ops.Add(new TerrainOp { Kind = "flatten", Center = new[] { cx, cz }, Radius = basin + 20, Height = -6, Falloff = 0.25f, Material = floor == "lava" ? "CrackedLava" : floor == "water" ? "Sand" : "Basalt" });
            if (floor == "water")
            {
                recipe.WaterLevel = -1;
                recipe.Ops.Add(new TerrainOp { Kind = "lake", Center = new[] { cx, cz }, Radius = basin + 10, Height = 6, Falloff = 0.3f });
            }
            g.SetGround(recipe);
            // The kill floor: invisible, just above the lava (or under the water), over the whole basin.
            var killer = g.PropAt("Obby/KillFloor", cx, floor == "water" ? -1.6f : -5f, cz, 0);
            killer.Part("KillFloor", 0, 0, 0, maxX - minX + 60, 1, maxZ - minZ + 60, Kill, "Neon", tags: new[] { "KillBrick" }, transparency: 1, collide: false);
            g.AddParts(1);
            g.Scene.Lighting = new LightingSpec { Preset = g.Spec.Lighting is { Length: > 0 } l ? l : themes[0].Name == "Lava" ? "golden" : "sunny" };

            // Scenery round the basin: trees on the slopes, so the course floats in a valley rather than a void.
            Scenery.TreeRing(g, themes[0].Biome, minX - 20, minZ - 20, maxX + 20, maxZ + 20, 110);

            g.Scene.Layout.Concept = $"An obby of {stages} stages over a {floor} basin, starting at the spawn platform (shop, leaderboard, START arch) and snaking in rows to a golden finish; themes {string.Join(" → ", themes.Select(t => t.Name).Distinct())}, one new mechanic at a time, a checkpoint at every stage.";
            g.Result.Notes.Add($"Course: {stages} stages, {plan.SelectMany(p => p).Select(s => s.Kind).Distinct().Count()} kinds of obstacle, climbing to y {c.Y:0}.");
        }

        // ─────────────── the plan: which obstacles, stage by stage ───────────────

        private sealed record Step(string Kind, float Level);

        private static List<List<Step>> Plan(int stages, float hard)
        {
            // Mechanics in the order they are introduced; each one first appears alone, at an easy level.
            var mechanics = new[] { "hops", "stairs", "zigzag", "beam", "strips", "mover", "vanish", "spinner", "truss", "bounce" };
            var plan = new List<List<Step>>();
            int introduced = 2;
            for (int s = 1; s <= stages; s++)
            {
                float t = (s - 1) / (float)Math.Max(1, stages - 1); // 0 → 1 over the course
                float level = Math.Clamp((0.15f + t * 0.85f) * hard, 0.1f, 1f);
                var steps = new List<Step>();
                if (s <= 3)
                {
                    steps.Add(new("hops", 0.1f + s * 0.05f));
                    if (s >= 2) steps.Add(new("stairs", 0.2f));
                }
                else if (s % 6 == 0)
                {
                    steps.Add(new("hops", 0.2f)); // a breather
                }
                else
                {
                    // A new mechanic every couple of stages, alone the first time.
                    if (introduced < mechanics.Length && s >= 4 + (introduced - 2) * 2)
                    {
                        steps.Add(new(mechanics[introduced], MathF.Max(0.2f, level * 0.6f)));
                        introduced++;
                    }
                    else
                    {
                        int count = level < 0.45f ? 2 : 3;
                        var known = mechanics.Take(introduced).ToArray();
                        for (int k = 0; k < count; k++) steps.Add(new(known[(s * 7 + k * 3) % known.Length], level));
                    }
                }
                plan.Add(steps);
            }
            return plan;
        }

        // ─────────────── building ───────────────

        private static void BuildObstacle(Generation g, Cursor c, Step step, Theme th, string folder, List<(float, float, float, float)> surfaces)
        {
            float lv = step.Level;
            switch (step.Kind)
            {
                case "hops":
                    {
                        int count = 3 + (int)(lv * 3);
                        float size = Lerp(7, 4, lv), gap = Lerp(4, 7.5f, lv);
                        for (int i = 0; i < count; i++)
                        {
                            float rise = i % 2 == 0 ? Lerp(0.5f, 2.5f, lv) : 0;
                            Platform(g, c, gap, size, size, rise, th.Platform, th.Material, folder, surfaces);
                        }
                        break;
                    }
                case "stairs":
                    {
                        int steps = 4 + (int)(lv * 3);
                        for (int i = 0; i < steps; i++) Platform(g, c, i == 0 ? 3 : 0.5f, 8, 4, 2.2f, i % 2 == 0 ? th.Platform : th.Accent, th.Material, folder, surfaces);
                        break;
                    }
                case "zigzag":
                    {
                        int count = 4 + (int)(lv * 2);
                        for (int i = 0; i < count; i++)
                        {
                            float side = (i % 2 == 0 ? 1 : -1) * Lerp(2.5f, 4, lv);
                            Platform(g, c, Lerp(3, 5, lv), Lerp(6, 4.5f, lv), Lerp(6, 4.5f, lv), i % 3 == 0 ? 1.5f : 0, th.Accent, th.Material, folder, surfaces, side);
                        }
                        break;
                    }
                case "beam":
                    {
                        Platform(g, c, Lerp(3, 5, lv), 6, 6, 0, th.Platform, th.Material, folder, surfaces);
                        float width = Lerp(3, 1.6f, lv), length = Lerp(14, 22, lv);
                        Platform(g, c, 0.2f, width, length, 0, th.Accent, th.Material, folder, surfaces);
                        Platform(g, c, 0.2f, 6, 6, 0, th.Platform, th.Material, folder, surfaces);
                        break;
                    }
                case "strips":
                    {
                        // A long floor with lava strips across it to hop over.
                        Platform(g, c, Lerp(3, 5, lv), 10, 26, 0, th.Platform, th.Material, folder, surfaces);
                        int strips = 2 + (int)(lv * 2);
                        for (int k = 0; k < strips; k++)
                        {
                            // Evenly over the floor's middle (the cursor is at its far edge).
                            float along = -21 + (k + 0.5f) * (16f / strips);
                            float x = c.X + c.Dx * along, z = c.Z + c.Dz * along;
                            var b = g.PropAt(folder, x, c.Y, z, Yaw(c));
                            b.Part("LavaStrip", 0, 0.15f, 0, 10, 0.3f, Lerp(2.5f, 3.5f, lv), Kill, "Neon", tags: new[] { "KillBrick" });
                            g.AddParts(1);
                        }
                        break;
                    }
                case "mover":
                    {
                        Platform(g, c, Lerp(3, 4, lv), 7, 7, 0, th.Platform, th.Material, folder, surfaces);
                        float span = Lerp(12, 18, lv);
                        // The mover starts against this platform and glides across the gap and back.
                        float sx = c.X + c.Dx * 3.5f, sz = c.Z + c.Dz * 3.5f;
                        var b = g.PropAt(folder, sx, c.Y - 1, sz, Yaw(c));
                        string offset = $"{R(c.Dx * span)},0,{R(c.Dz * span)}";
                        b.Part("Mover", 0, 0.5f, 0, 6, 1, 6, th.Accent, "SmoothPlastic", tags: new[] { "Mover" }, config: new() { ["Offset"] = offset, ["Period"] = Lerp(3.4f, 2.4f, lv) });
                        g.AddParts(1);
                        surfaces.Add(Box(sx + c.Dx * span / 2, sz + c.Dz * span / 2, span / 2 + 3));
                        MoveTo(c, c.X + c.Dx * (span + 3.5f), c.Y, c.Z + c.Dz * (span + 3.5f));
                        Platform(g, c, 3.5f, 7, 7, 0, th.Platform, th.Material, folder, surfaces);
                        break;
                    }
                case "vanish":
                    {
                        int count = 3 + (int)(lv * 2);
                        for (int i = 0; i < count; i++)
                        {
                            var part = Platform(g, c, Lerp(3.5f, 5.5f, lv), 6, 6, 0, "#7FD8FF", "Glass", folder, surfaces);
                            part.Tags.Add("Disappearing");
                            part.Config["Visible"] = Lerp(3, 2, lv);
                            part.Config["Hidden"] = 1.4f;
                            part.Transparency = 0.25f;
                        }
                        Platform(g, c, 4, 7, 7, 0, th.Platform, th.Material, folder, surfaces);
                        break;
                    }
                case "spinner":
                    {
                        var pad = Platform(g, c, Lerp(3, 4.5f, lv), 16, 16, 0, th.Platform, th.Material, folder, surfaces);
                        float mx = c.X - c.Dx * 8, mz = c.Z - c.Dz * 8;
                        var b = g.PropAt(folder, mx, c.Y, mz, Yaw(c));
                        b.Part("SpinnerHub", 0, 1.2f, 0, 1.6f, 2.4f, 1.6f, "#333333", "Metal");
                        b.Part("SpinnerBar", 0, 1.6f, 0, 14, 0.9f, 0.9f, Kill, "Neon", tags: new[] { "Spinner", "KillBrick" }, config: new() { ["Speed"] = Lerp(60, 110, lv) }, collide: false);
                        g.AddParts(2);
                        Platform(g, c, 3.5f, 7, 7, 0, th.Accent, th.Material, folder, surfaces);
                        break;
                    }
                case "truss":
                    {
                        Platform(g, c, 3, 7, 7, 0, th.Platform, th.Material, folder, surfaces);
                        float climb = MathF.Round(Lerp(9, 14, lv) / 2) * 2; // truss sizes come in 2-stud steps
                        float tx = c.X + c.Dx * 1, tz = c.Z + c.Dz * 1;
                        var b = g.PropAt(folder, tx, c.Y, tz, Yaw(c));
                        b.Part("Truss", 0, climb / 2, 0, 2, climb, 2, "#9AA0A6", "Metal", shape: "Truss");
                        g.AddParts(1);
                        surfaces.Add(Box(tx, tz, 1));
                        MoveTo(c, tx, c.Y + climb, tz);
                        Platform(g, c, 1.2f, 7, 7, 0, th.Accent, th.Material, folder, surfaces);
                        break;
                    }
                case "bounce":
                    {
                        var pad = Platform(g, c, 3.5f, 8, 8, 0, th.Platform, th.Material, folder, surfaces);
                        var b = g.PropAt(folder, c.X - c.Dx * 4, c.Y, c.Z - c.Dz * 4, Yaw(c));
                        b.Part("BouncePad", 0, 0.2f, 0, 5, 0.4f, 5, "#FF5FB8", "Neon", tags: new[] { "Bounce" }, config: new() { ["Power"] = 80 });
                        g.AddParts(1);
                        Platform(g, c, 5, 8, 8, Lerp(9, 12, lv), th.Accent, th.Material, folder, surfaces);
                        break;
                    }
            }
        }

        /// <summary>A platform <paramref name="gap"/> studs past the cursor (its top <paramref name="rise"/> higher), sideways by <paramref name="side"/>; the cursor moves to its far edge.</summary>
        private static SceneObject Platform(Generation g, Cursor c, float gap, float width, float length, float rise, string color, string material, string folder,
            List<(float, float, float, float)> surfaces, float side = 0)
        {
            float top = c.Y + rise;
            float along = gap + length / 2;
            float x = c.X + c.Dx * along + c.Rx * side, z = c.Z + c.Dz * along + c.Rz * side;
            var b = g.PropAt(folder, x, top - 1.2f, z, Yaw(c));
            var part = b.Part("Platform", 0, 0.6f, 0, width, 1.2f, length, color, material);
            g.AddParts(1);
            surfaces.Add(Box(x, z, MathF.Max(width, length) / 2));
            MoveTo(c, x + c.Dx * length / 2 - c.Rx * side * 0, top, z + c.Dz * length / 2);
            // Keep the course on its row's line after a sideways step.
            if (side != 0)
            {
                c.X -= c.Rx * side;
                c.Z -= c.Rz * side;
            }
            return part;
        }

        private static void Advance(Generation g, Cursor c, float gap, float rise) => MoveTo(c, c.X + c.Dx * gap, c.Y + rise, c.Z + c.Dz * gap);

        private static void MoveTo(Cursor c, float x, float y, float z) { c.X = x; c.Y = y; c.Z = z; }

        private static void Turn(Generation g, Cursor c, Theme th, string folder, List<(float, float, float, float)> surfaces)
        {
            // A landing, then hops sideways, then a landing facing back.
            Platform(g, c, 4, 12, 12, 0, th.Accent, th.Material, folder, surfaces);
            float sideways = c.Row % 2 == 0 ? 1 : -1;
            sideways = 1; // rows step to +X every time, so the course snakes and never crosses itself
            float dx = c.Dx, dz = c.Dz;
            c.Dx = 1; c.Dz = 0;
            MoveTo(c, c.X - dx * 6 + 6, c.Y, c.Z - dz * 6);
            for (int i = 0; i < 3; i++) Platform(g, c, 5, 7, 7, 0, th.Platform, th.Material, folder, surfaces);
            Platform(g, c, 5, 12, 12, 0, th.Accent, th.Material, folder, surfaces);
            // From the last landing's centre to its far edge in the new heading.
            c.Dx = -dx; c.Dz = -dz;
            MoveTo(c, c.X - 6 + c.Dx * 6, c.Y, c.Z + c.Dz * 6);
            c.Row++;
            c.RowStart = c.Z;
            _ = sideways;
        }

        private static (float, float, float, float) Box(float x, float z, float half) => (x - half, z - half, x + half, z + half);

        private static float Yaw(Cursor c) => WorldGenerator.YawToward(0, 0, c.Dx, c.Dz);

        private static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0, 1);

        private static string R(float v) => MathF.Round(v, 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Scenery shared by layouts that are not built round zones.</summary>
    public static class Scenery
    {
        /// <summary>Trees on the land round a rectangle (from its edge out to <paramref name="reach"/> studs), on slopes that can hold them.</summary>
        public static void TreeRing(Generation g, string biome, float minX, float minZ, float maxX, float maxZ, float reach)
        {
            var trees = g.Palette(Roles.Tree, biome, null, 3).Concat(g.Palette(Roles.Conifer, biome, null, 2)).ToList();
            var bushes = g.Palette(Roles.Bush, biome, null, 2);
            var rocks = g.Palette(Roles.Rock, biome, null, 2);
            if (trees.Count == 0 && bushes.Count == 0) return;
            foreach (var (x, z) in g.Darts(minX - reach, minZ - reach, maxX + reach, maxZ + reach, 11, 1400, (x, z) =>
            {
                bool outside = x < minX || x > maxX || z < minZ || z > maxZ;
                return outside && g.Free(x, z, 2.5f) && !g.Wet(x, z) && g.SlopeAt(x, z) < 1.3f && g.GroundAt(x, z) > -2;
            }, attemptsPerPoint: 5))
            {
                double roll = g.Rng.NextDouble();
                var palette = roll < 0.7 && trees.Count > 0 ? trees : roll < 0.9 && bushes.Count > 0 ? bushes : rocks.Count > 0 ? rocks : trees;
                var e = g.Pick(palette);
                if (e == null) continue;
                if (g.PlaceModel(e, x, z, (float)g.Rng.NextDouble() * 360, g.ScaleFor(e, Roles.TypicalHeight(e.Role)), "World/Scenery", occupyFactor: 0.3f) == null && g.OutOfBudget) break;
            }
        }
    }
}
