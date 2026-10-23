using System.Globalization;

namespace Omnipotent.Services.OmniBlox.Build.WorldGen
{
    /// <summary>
    /// The props every game needs that the free-model library cannot be trusted for: the spawn pad, the sell pad,
    /// stands that open the shop and upgrades, the rebirth altar, zone gates, signposts with real posts (the first
    /// studio's signs floated in the air), lamp posts, benches, fences, and fallbacks for trees and crystals when
    /// the library has nothing in the map's style. Each is built from parts in its own space — origin on the
    /// ground at its middle, front towards −Z — then turned and moved as one, so every piece of it stands on the
    /// ground and faces where it should.
    /// </summary>
    public sealed class PropBuilder
    {
        private readonly List<SceneObject> into;
        private readonly Func<string> nextId;
        private readonly string folder;
        private readonly float ox, oy, oz, yaw, cos, sin;
        private readonly string group;

        /// <param name="yaw">Degrees about Y; 0 faces −Z (the prop's front looks along its yaw).</param>
        public PropBuilder(List<SceneObject> into, Func<string> nextId, string folder, float x, float groundY, float z, float yaw)
        {
            this.into = into;
            this.nextId = nextId;
            this.folder = folder;
            (ox, oy, oz, this.yaw) = (x, groundY, z, yaw);
            float r = yaw * MathF.PI / 180f;
            cos = MathF.Cos(r);
            sin = MathF.Sin(r);
            group = "prop:" + nextId();
        }

        public string Folder => folder;

        /// <summary>World position of a point given in the prop's own space (Roblox's CFrame.Angles(0, yaw, 0)).</summary>
        public float[] World(float lx, float ly, float lz) => new[] { R(ox + cos * lx + sin * lz), R(oy + ly), R(oz - sin * lx + cos * lz) };

        private static float R(float v) => MathF.Round(v, 2);

        public SceneObject Part(string name, float lx, float ly, float lz, float sx, float sy, float sz, string color, string material = "SmoothPlastic",
            string shape = "Block", float localYaw = 0, IEnumerable<string>? tags = null, Dictionary<string, object>? config = null, float transparency = 0, bool collide = true)
        {
            var o = new SceneObject
            {
                Id = nextId(), Kind = SceneKinds.Part, Name = name, Folder = folder, Source = "gen", Group = group,
                Position = World(lx, ly, lz), Size = new[] { sx, sy, sz }, Yaw = Norm(yaw + localYaw), Shape = shape,
                Color = color, Material = material, Transparency = transparency > 0 ? transparency : null, CanCollide = collide,
                Tags = tags?.ToList() ?? new List<string>(), Config = config ?? new Dictionary<string, object>(),
            };
            into.Add(o);
            return o;
        }

        /// <summary>An upright cylinder (Roblox cylinders lie along X: this one is turned to stand on its end).</summary>
        public SceneObject Post(string name, float lx, float lz, float height, float diameter, string color, string material = "Wood", float baseY = 0)
        {
            var o = Part(name, lx, baseY + height / 2, lz, height, diameter, diameter, color, material, "Cylinder");
            o.Rotation = new[] { 0f, Norm(yaw), 90f };
            return o;
        }

        public SceneObject Ball(string name, float lx, float ly, float lz, float diameter, string color, string material = "SmoothPlastic", IEnumerable<string>? tags = null, Dictionary<string, object>? config = null) =>
            Part(name, lx, ly, lz, diameter, diameter, diameter, color, material, "Ball", tags: tags, config: config);

        /// <summary>A sign board (text on a SurfaceGui), readable from the front, and from the back when two-sided.</summary>
        public SceneObject Board(string name, string text, float lx, float ly, float lz, float width, float height, string color, bool twoSided = true, float depth = 0.5f, float localYaw = 0)
        {
            var o = new SceneObject
            {
                Id = nextId(), Kind = SceneKinds.Text, Name = name, Folder = folder, Source = "gen", Group = group, Text = text, TwoSided = twoSided,
                Position = World(lx, ly, lz), Size = new[] { width, height, depth }, Yaw = Norm(yaw + localYaw), Color = color, Material = "SmoothPlastic",
            };
            into.Add(o);
            return o;
        }

        public static float Norm(float degrees)
        {
            float d = degrees % 360;
            if (d < 0) d += 360;
            return MathF.Round(d, 1);
        }
    }

    /// <summary>The kit's prop recipes (see <see cref="PropBuilder"/>).</summary>
    public static class KitProps
    {
        public const string Wood = "#8A5A33";
        public const string DarkWood = "#5B3A22";
        public const string Stone = "#8C8A86";
        public const string Metal = "#3B3D46";
        public const string Warm = "#FFE08A";

        /// <summary>The spawn: a pad flush with the ground, facing the hub's centre.</summary>
        public static SceneObject Spawn(List<SceneObject> into, Func<string> id, string folder, float x, float y, float z, float yaw, string color)
        {
            var o = new SceneObject
            {
                Id = id(), Kind = SceneKinds.Spawn, Name = "SpawnPad", Folder = folder, Source = "gen",
                Position = new[] { x, MathF.Round(y + 0.5f, 2), z }, Size = new[] { 10f, 1f, 10f }, Yaw = PropBuilder.Norm(yaw), Color = color, Material = "SmoothPlastic",
            };
            into.Add(o);
            return o;
        }

        /// <summary>A glowing pad players step on to sell their bag, with a SELL sign behind it.</summary>
        public static void SellPad(PropBuilder b, string accent, string label = "💰 SELL")
        {
            b.Part("SellBase", 0, 0.3f, 0, 17, 0.6f, 17, "#D9A441");
            b.Part("SellPad", 0, 0.8f, 0, 13, 0.4f, 13, "#36E07A", "Neon", tags: new[] { "SellZone", "Glow" }, config: new() { ["Range"] = 16, ["Brightness"] = 1.2 });
            b.Post("SellPost", -5.6f, 8.6f, 9.5f, 0.9f, Wood);
            b.Post("SellPost", 5.6f, 8.6f, 9.5f, 0.9f, Wood);
            b.Board("SellSign", label, 0, 8.1f, 8.6f, 12.5f, 3.4f, accent);
        }

        /// <summary>
        /// A market stand: counter, posts, a striped canopy and a sign; stepping on the mat in front (or using its
        /// prompt) opens the HUD panel named by <paramref name="panel"/> (upgrades, shop, rebirth).
        /// </summary>
        public static void Stand(PropBuilder b, string label, string panel, string canopy, string accent)
        {
            b.Part("Counter", 0, 1.6f, 0.8f, 10, 3.2f, 3.4f, Wood, "WoodPlanks");
            b.Part("CounterTop", 0, 3.4f, 0.6f, 10.8f, 0.4f, 4.2f, accent);
            b.Post("StandPost", -5.1f, 2.6f, 8.6f, 0.7f, DarkWood);
            b.Post("StandPost", 5.1f, 2.6f, 8.6f, 0.7f, DarkWood);
            b.Part("Canopy", 0, 8.9f, 1.4f, 12.4f, 0.6f, 6.4f, canopy);
            b.Part("CanopyStripe", 0, 9.25f, 1.4f, 12.6f, 0.15f, 2.2f, "#FFFFFF");
            b.Board("StandSign", label, 0, 10.5f, 2.2f, 9.5f, 2.6f, accent);
            b.Part(MatName(panel), 0, 0.15f, -4.2f, 9, 0.3f, 5, accent, "Neon", tags: new[] { "Stand" }, config: new() { ["Panel"] = panel }, transparency: 0.35f);
        }

        /// <summary>The rebirth altar: a stone dais with a floating, glowing crystal.</summary>
        public static void RebirthAltar(PropBuilder b)
        {
            b.Part("AltarBase", 0, 0.6f, 0, 10, 1.2f, 10, Stone, "Slate");
            b.Part("AltarStep", 0, 1.6f, 0, 7, 0.8f, 7, "#B9B4AC", "Slate");
            b.Part("RebirthCrystal", 0, 6.2f, 0, 2.6f, 5.2f, 2.6f, "#B66DFF", "Neon", localYaw: 45, tags: new[] { "Bob", "Glow", "Sparkle", "Spin" }, config: new() { ["Color"] = "#C890FF", ["Speed"] = 40 }, collide: false);
            b.Post("AltarPost", -4.2f, 4.6f, 8.4f, 0.8f, Stone, "Slate");
            b.Post("AltarPost", 4.2f, 4.6f, 8.4f, 0.8f, Stone, "Slate");
            b.Board("RebirthSign", "🌟 REBIRTH", 0, 7.2f, 4.6f, 9.2f, 2.4f, "#6A3FB5");
            b.Part(MatName("rebirth"), 0, 0.15f, -6.5f, 8, 0.3f, 4, "#B66DFF", "Neon", tags: new[] { "Stand" }, config: new() { ["Panel"] = "rebirth" }, transparency: 0.35f);
        }

        /// <summary>A board the kit draws the global top-10 on (its front face).</summary>
        public static void Leaderboard(PropBuilder b)
        {
            b.Post("BoardPost", -7.4f, 0.6f, 13.8f, 1, DarkWood);
            b.Post("BoardPost", 7.4f, 0.6f, 13.8f, 1, DarkWood);
            b.Part("LeaderboardBoard", 0, 7.5f, 0, 14, 9, 0.6f, "#1E2430", tags: new[] { "Leaderboard" });
            b.Board("BoardTitle", "🏆 TOP PLAYERS", 0, 12.9f, 0, 14.6f, 1.8f, "#E0A21B", twoSided: false, depth: 0.7f);
        }

        /// <summary>
        /// A gate across a path: two pillars, a beam with the zone's name, and (when the zone costs something) a
        /// glowing barrier tagged ZoneGate that the kit turns into a lock with a price and an Unlock prompt.
        /// </summary>
        public static void Gate(PropBuilder b, float width, string zone, long price, string? requires, string? currency, string pillar, string accent)
        {
            float half = width / 2 + 1.6f;
            b.Part("GatePillar", -half, 9, 0, 3.2f, 18, 3.2f, pillar, "Slate");
            b.Part("GatePillar", half, 9, 0, 3.2f, 18, 3.2f, pillar, "Slate");
            b.Part("PillarCap", -half, 18.5f, 0, 4, 1, 4, accent);
            b.Part("PillarCap", half, 18.5f, 0, 4, 1, 4, accent);
            b.Part("GateBeam", 0, 20.3f, 0, width + 7, 2.6f, 3.2f, pillar, "Slate");
            b.Board("GateName", zone, 0, 20.3f, -1.75f, width + 2, 2.2f, accent, twoSided: false, depth: 0.3f);
            b.Board("GateNameBack", zone, 0, 20.3f, 1.75f, width + 2, 2.2f, accent, twoSided: false, depth: 0.3f, localYaw: 180);
            if (price > 0)
            {
                var config = new Dictionary<string, object> { ["Zone"] = zone, ["Price"] = price };
                if (!string.IsNullOrWhiteSpace(requires)) config["Requires"] = requires!;
                if (!string.IsNullOrWhiteSpace(currency)) config["Currency"] = currency!;
                b.Part(GateName(zone), 0, 8.5f, 0, width + 0.4f, 17, 1, "#59D8FF", "Neon", tags: new[] { "ZoneGate" }, config: config, transparency: 0.45f);
            }
        }

        /// <summary>The name of a stand's mat (what a quest's guide line points at: "UpgradesMat").</summary>
        public static string MatName(string panel) => (panel.Length == 0 ? "Stand" : char.ToUpperInvariant(panel[0]) + panel[1..]) + "Mat";

        /// <summary>The name of a zone's gate barrier (a quest's guide line target: "Gate_Pine_Forest").</summary>
        public static string GateName(string zone) => "Gate_" + new string(zone.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());

        /// <summary>A signpost: a real post with a board on top (never a board floating in the air).</summary>
        public static void Signpost(PropBuilder b, string text, string board)
        {
            b.Post("SignPost", 0, 0, 8.6f, 0.8f, DarkWood);
            float width = Math.Clamp(text.Length * 0.62f + 2.4f, 6, 13);
            b.Board("Signboard", text, 0, 7.2f, 0, width, 2.5f, board);
        }

        /// <summary>A lamp post with a warm glowing head (the kit adds the light: tag Glow).</summary>
        public static void Lamp(PropBuilder b)
        {
            b.Part("LampFoot", 0, 0.3f, 0, 1.8f, 0.6f, 1.8f, Metal, "Metal");
            b.Post("LampPost", 0, 0, 11, 0.7f, Metal, "Metal");
            b.Part("LampCap", 0, 11.2f, 0, 1.6f, 0.5f, 1.6f, Metal, "Metal");
            b.Ball("LampHead", 0, 12.4f, 0, 2.2f, Warm, "Neon", tags: new[] { "Glow" }, config: new() { ["Color"] = "#FFD27A", ["Range"] = 22, ["Brightness"] = 1.4 });
        }

        /// <summary>A wooden bench facing its front (players look −Z sitting on it).</summary>
        public static void Bench(PropBuilder b)
        {
            b.Part("BenchSeat", 0, 1.9f, 0, 6, 0.5f, 2.2f, Wood, "WoodPlanks");
            b.Part("BenchLeg", -2.5f, 0.85f, 0, 0.5f, 1.7f, 1.8f, DarkWood);
            b.Part("BenchLeg", 2.5f, 0.85f, 0, 0.5f, 1.7f, 1.8f, DarkWood);
            b.Part("BenchBack", 0, 3.25f, 0.95f, 6, 2.2f, 0.35f, Wood, "WoodPlanks");
        }

        /// <summary>One fence span from local (0,0,0) along +X for <paramref name="length"/> studs, with a post at each end.</summary>
        public static void FenceSpan(PropBuilder b, float length, string color)
        {
            b.Part("FencePost", 0, 2.1f, 0, 0.7f, 4.2f, 0.7f, color);
            b.Part("FencePost", length, 2.1f, 0, 0.7f, 4.2f, 0.7f, color);
            b.Part("FenceRail", length / 2, 1.5f, 0, length, 0.4f, 0.3f, color);
            b.Part("FenceRail", length / 2, 3.1f, 0, length, 0.4f, 0.3f, color);
        }

        /// <summary>A low-poly tree from parts: an upright trunk and a few leafy balls (the library's trees are preferred).</summary>
        public static void Tree(PropBuilder b, float height, string leaves, Random rng)
        {
            float trunk = height * 0.42f, d = MathF.Max(1.2f, height * 0.09f);
            b.Post("Trunk", 0, 0, trunk + 1, d, "#7A4E2D");
            float r = height * 0.26f;
            b.Ball("Leaves", 0, trunk + r * 0.9f, 0, r * 2, leaves, "Grass");
            for (int i = 0; i < 2; i++)
            {
                float a = (float)(rng.NextDouble() * Math.PI * 2);
                b.Ball("Leaves", MathF.Cos(a) * r * 0.55f, trunk + r * (0.5f + i * 0.5f), MathF.Sin(a) * r * 0.55f, r * 1.4f, Shade(leaves, 0.9f + i * 0.12f), "Grass");
            }
        }

        /// <summary>A cluster of glowing crystal shards (a resource node or a landmark).</summary>
        public static void Crystal(PropBuilder b, float height, string color, Random rng, IEnumerable<string>? tags = null, Dictionary<string, object>? config = null)
        {
            var main = b.Part("Crystal", 0, height * 0.45f, 0, height * 0.28f, height * 0.9f, height * 0.28f, color, "Neon", localYaw: 45, tags: tags, config: config);
            for (int i = 0; i < 3; i++)
            {
                float a = i * 2.1f + (float)rng.NextDouble();
                float h = height * (0.45f + (float)rng.NextDouble() * 0.2f);
                var shard = b.Part("Shard", MathF.Cos(a) * height * 0.22f, h * 0.4f, MathF.Sin(a) * height * 0.22f, h * 0.24f, h, h * 0.24f, Shade(color, 0.85f), "Glass");
                shard.Rotation = new[] { MathF.Cos(a) * 18, PropBuilder.Norm(main.Yaw + i * 40), MathF.Sin(a) * 18 };
            }
        }

        /// <summary>A colour scaled lighter (factor above 1) or darker.</summary>
        public static string Shade(string hex, float factor)
        {
            var c = PlaceCompiler.ParseColor(hex);
            int R(byte v) => Math.Clamp((int)MathF.Round(v * factor), 0, 255);
            return "#" + R(c.R).ToString("X2", CultureInfo.InvariantCulture) + R(c.G).ToString("X2", CultureInfo.InvariantCulture) + R(c.B).ToString("X2", CultureInfo.InvariantCulture);
        }
    }
}
