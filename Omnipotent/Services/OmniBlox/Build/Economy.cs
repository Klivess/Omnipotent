using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.OmniBlox.Build
{
    public sealed class EconZone
    {
        public string Name { get; set; } = "";
        public long Price { get; set; }
        public int Hp { get; set; } = 4;
        public int Value { get; set; } = 2;
        public int Nodes { get; set; } = 12;
        public float Respawn { get; set; } = 8;
        /// <summary>Walking distance (studs) from the sell pad to the zone's middle.</summary>
        public float Distance { get; set; } = 150;
        /// <summary>Studs walked between one node and the next.</summary>
        public float Spacing { get; set; } = 16;
    }

    public sealed class EconTool
    {
        public string Name { get; set; } = "";
        public double Power { get; set; } = 1;
        public double Cooldown { get; set; } = 0.45;
        public long Price { get; set; }
    }

    public sealed class EconUpgrade
    {
        public string Key { get; set; } = "";
        public List<long> Costs { get; set; } = new();
        public double Step { get; set; }
    }

    /// <summary>A simulator's economy as numbers: what the map holds, what the tools do, what everything costs.</summary>
    public sealed class SimulatorEconomy
    {
        public List<EconZone> Zones { get; set; } = new();
        public List<EconTool> Tools { get; set; } = new();
        public int BaseCapacity { get; set; } = 25;
        public EconUpgrade? Capacity { get; set; }
        public EconUpgrade? Speed { get; set; }
        public float WalkSpeed { get; set; } = 16;
        public long StartingCurrency { get; set; }
        public long? RebirthCost { get; set; }

        /// <summary>The economy of a game as it stands: zones and nodes from the scene, tools and upgrades from game.json.</summary>
        public static SimulatorEconomy FromGame(GameManifest manifest, SceneDocument scene)
        {
            var e = new SimulatorEconomy
            {
                WalkSpeed = manifest.WalkSpeed > 0 ? manifest.WalkSpeed : 16,
                StartingCurrency = manifest.StartingCurrency,
                RebirthCost = manifest.Rebirth is { Enabled: true } r ? r.Cost : null,
            };
            var t = manifest.TemplateSettings;
            if (t.TryGetValue("BaseCapacity", out var bc) && double.TryParse(Convert.ToString(Value(bc), CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out double cap)) e.BaseCapacity = (int)cap;
            if (t.TryGetValue("Tools", out var tools) && JToken.FromObject(tools) is JArray ta)
                e.Tools = ta.OfType<JObject>().Select(x => new EconTool { Name = (string?)x["Name"] ?? "", Power = (double?)x["Power"] ?? 1, Cooldown = (double?)x["Cooldown"] ?? 0.45, Price = (long?)x["Price"] ?? 0 }).ToList();
            if (e.Tools.Count == 0) e.Tools = DefaultTools();
            if (t.TryGetValue("Upgrades", out var ups) && JToken.FromObject(ups) is JArray ua)
                foreach (var u in ua.OfType<JObject>())
                {
                    var up = new EconUpgrade { Key = (string?)u["Key"] ?? "", Costs = (u["Costs"] as JArray)?.Select(c => (long)c).ToList() ?? new(), Step = (double?)u["Step"] ?? 0 };
                    if (up.Key == "capacity") e.Capacity = up;
                    else if (up.Key == "speed") e.Speed = up;
                }
            e.Capacity ??= new EconUpgrade { Key = "capacity", Costs = new() { 50, 150, 400, 1000, 2500 }, Step = 25 };
            e.Speed ??= new EconUpgrade { Key = "speed", Costs = new() { 75, 250, 700, 1800 }, Step = 3 };

            // The map: nodes by zone, prices from the gates, distances from the sell pad.
            var sell = scene.Objects.FirstOrDefault(o => o.Tags.Contains("SellZone", StringComparer.OrdinalIgnoreCase));
            float sx = sell?.Position[0] ?? 0, sz = sell?.Position[2] ?? 0;
            var gates = scene.Objects.Where(o => o.Tags.Contains("ZoneGate", StringComparer.OrdinalIgnoreCase))
                .ToDictionary(o => Str(o.Config, "Zone"), o => (long)Num(o.Config, "Price", 0), StringComparer.OrdinalIgnoreCase);
            var nodes = scene.Objects.Where(o => o.Tags.Contains("Resource", StringComparer.OrdinalIgnoreCase)).Select(o => (Config: o.Config, X: o.Position[0], Z: o.Position[2]))
                .Concat(scene.Folders.Where(kv => kv.Value.Tags.Contains("Resource", StringComparer.OrdinalIgnoreCase))
                    .Select(kv => (Config: kv.Value.Config, X: scene.Objects.Where(o => o.Folder.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)).Select(o => o.Position[0]).DefaultIfEmpty(0).Average(),
                        Z: scene.Objects.Where(o => o.Folder.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)).Select(o => o.Position[2]).DefaultIfEmpty(0).Average())))
                .ToList();
            foreach (var group in nodes.GroupBy(n => Str(n.Config, "Zone")))
            {
                var list = group.ToList();
                float cx = list.Average(n => n.X), cz = list.Average(n => n.Z);
                float spread = (float)list.Average(n => Math.Sqrt((n.X - cx) * (n.X - cx) + (n.Z - cz) * (n.Z - cz)));
                e.Zones.Add(new EconZone
                {
                    Name = group.Key,
                    Price = gates.GetValueOrDefault(group.Key),
                    Hp = (int)list.Average(n => Num(n.Config, "Hp", 4)),
                    Value = (int)list.Average(n => Num(n.Config, "Value", 1)),
                    Nodes = list.Count,
                    Respawn = (float)list.Average(n => Num(n.Config, "Respawn", 8)),
                    Distance = MathF.Sqrt((cx - sx) * (cx - sx) + (cz - sz) * (cz - sz)) * 1.2f,
                    Spacing = MathF.Max(8, spread * 0.9f),
                });
            }
            var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var z in scene.Spec?.Zones ?? new List<WorldGen.ZoneSpec>()) order.TryAdd(z.Name, z.Order);
            foreach (var z in scene.Layout.Zones) order.TryAdd(z.Name, z.Order);
            e.Zones = e.Zones.OrderBy(z => order.TryGetValue(z.Name, out int o) ? o : int.MaxValue).ThenBy(z => z.Price).ThenBy(z => z.Name, StringComparer.Ordinal).ToList();
            return e;
        }

        public static List<EconTool> DefaultTools() => new()
        {
            new() { Name = "Wooden", Power = 1, Cooldown = 0.45, Price = 0 },
            new() { Name = "Stone", Power = 2, Cooldown = 0.42, Price = 60 },
            new() { Name = "Iron", Power = 4, Cooldown = 0.4, Price = 350 },
            new() { Name = "Golden", Power = 9, Cooldown = 0.38, Price = 2000 },
            new() { Name = "Diamond", Power = 20, Cooldown = 0.35, Price = 11000 },
            new() { Name = "Mythic", Power = 45, Cooldown = 0.32, Price = 60000 },
        };

        private static object? Value(object? v) => v is JValue j ? j.Value : v;
        private static string Str(Dictionary<string, object> config, string key) => config.TryGetValue(key, out var v) ? Convert.ToString(Value(v), CultureInfo.InvariantCulture) ?? "" : "";
        private static double Num(Dictionary<string, object> config, string key, double fallback) =>
            config.TryGetValue(key, out var v) && double.TryParse(Convert.ToString(Value(v), CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : fallback;
    }

    public sealed record EconEvent(double Minute, string What, long Cost);

    public sealed class EconomyReport
    {
        public List<EconEvent> Timeline { get; } = new();
        public List<MapFinding> Findings { get; } = new();
        public double FirstHarvestSeconds { get; set; }
        public double FirstSellSeconds { get; set; }
        public double? FirstPurchaseMinutes { get; set; }
        public double? AllZonesMinutes { get; set; }
        public double? RebirthMinutes { get; set; }
        public double CoinsPerMinuteAtStart { get; set; }
        public bool Ok => !Findings.Any(f => f.IsError);

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"A player who plays well: first harvest {FirstHarvestSeconds:0} s, first sale {FirstSellSeconds:0} s ({CoinsPerMinuteAtStart:0} coins a minute at the start), first purchase {(FirstPurchaseMinutes is double p ? p.ToString("0.0", CultureInfo.InvariantCulture) + " min" : "never")}, every zone {(AllZonesMinutes is double a ? a.ToString("0", CultureInfo.InvariantCulture) + " min" : "never")}{(RebirthMinutes is double r ? $", first rebirth {r:0} min" : "")}.");
            foreach (var e in Timeline.Take(40)) sb.AppendLine($"  {e.Minute,6:0.0} min  {e.What}{(e.Cost > 0 ? $" ({e.Cost.ToString("N0", CultureInfo.InvariantCulture)})" : "")}");
            foreach (var f in Findings) sb.AppendLine(f.ToString());
            return sb.ToString();
        }
    }

    /// <summary>
    /// A simulator's economy, played: a bot that harvests in the best zone it owns, fills its bag, walks to the
    /// sell pad, and buys the next zone, tool or bag as soon as it can afford it — using the game's own numbers
    /// (node hits and values, the tools' power and speed, the bag, walking distances on the real map). Its
    /// timeline is checked against what keeps young players playing (a reward in seconds, a purchase in a
    /// minute or two, a new zone every several minutes, no wall). <see cref="Balance"/> sets the prices that
    /// make that timeline happen.
    /// </summary>
    public static class Economy
    {
        private sealed class State
        {
            public double T;
            public double Coins;
            public int Tool;
            public int Cap;
            public int Speed;
            public int Zones = 1;
        }

        private static double Power(SimulatorEconomy e, State s) => e.Tools.Count == 0 ? 1 : e.Tools[Math.Min(s.Tool, e.Tools.Count - 1)].Power;
        private static double Cooldown(SimulatorEconomy e, State s) => e.Tools.Count == 0 ? 0.45 : Math.Max(0.15, e.Tools[Math.Min(s.Tool, e.Tools.Count - 1)].Cooldown);
        private static double Capacity(SimulatorEconomy e, State s) => e.BaseCapacity + s.Cap * (e.Capacity?.Step ?? 25);
        private static double Walk(SimulatorEconomy e, State s) => e.WalkSpeed + s.Speed * (e.Speed?.Step ?? 3);

        /// <summary>One bag's worth in a zone: seconds it takes and coins it earns.</summary>
        private static (double Seconds, double Coins) Trip(SimulatorEconomy e, State s, EconZone z)
        {
            double cap = Capacity(e, s);
            double nodes = Math.Ceiling(cap / Math.Max(1, z.Value));
            double perNode = Math.Ceiling(z.Hp / Power(e, s)) * Cooldown(e, s) + z.Spacing / Walk(e, s);
            // A zone's nodes regrow on a timer: past one pass over them, the regrowth sets the pace.
            perNode = Math.Max(perNode, z.Respawn / Math.Max(1, z.Nodes));
            double seconds = nodes * perNode + 2 * z.Distance / Walk(e, s);
            return (seconds, cap);
        }

        private static (int Zone, double Rate) Best(SimulatorEconomy e, State s)
        {
            int best = 0;
            double rate = 0;
            for (int i = 0; i < Math.Min(s.Zones, e.Zones.Count); i++)
            {
                var (sec, coins) = Trip(e, s, e.Zones[i]);
                double r = coins / Math.Max(1, sec);
                if (r > rate) { rate = r; best = i; }
            }
            return (best, rate);
        }

        public static EconomyReport Simulate(SimulatorEconomy e, double maxMinutes = 360)
        {
            var report = new EconomyReport();
            if (e.Zones.Count == 0)
            {
                report.Findings.Add(new MapFinding("error", "economy", "There is nothing to harvest: no resource nodes in the map."));
                return report;
            }
            var s = new State { Coins = e.StartingCurrency };
            s.Zones = 1 + e.Zones.Skip(1).TakeWhile(z => z.Price <= 0).Count();
            {
                var z0 = e.Zones[0];
                report.FirstHarvestSeconds = z0.Distance / Walk(e, s) + Math.Ceiling(z0.Hp / Power(e, s)) * Cooldown(e, s);
                var (sec, coins) = Trip(e, s, z0);
                report.FirstSellSeconds = sec;
                report.CoinsPerMinuteAtStart = coins / Math.Max(1, sec) * 60;
            }
            bool rebirthDone = false;
            while (s.T < maxMinutes * 60)
            {
                var (zone, _) = Best(e, s);
                var (seconds, earned) = Trip(e, s, e.Zones[zone]);
                s.T += seconds;
                s.Coins += earned;
                // Spend: the next zone first, then a better tool, then a bigger bag or faster shoes when cheap
                // next to what comes next.
                bool bought;
                do
                {
                    bought = false;
                    double nextBig = s.Zones < e.Zones.Count ? e.Zones[s.Zones].Price
                        : s.Tool + 1 < e.Tools.Count ? e.Tools[s.Tool + 1].Price
                        : e.RebirthCost is long saveFor ? saveFor * 0.4 : double.MaxValue;
                    if (s.Zones < e.Zones.Count && s.Coins >= e.Zones[s.Zones].Price)
                    {
                        s.Coins -= e.Zones[s.Zones].Price;
                        report.Timeline.Add(new EconEvent(s.T / 60, "unlock " + e.Zones[s.Zones].Name, e.Zones[s.Zones].Price));
                        s.Zones++;
                        bought = true;
                    }
                    else if (s.Tool + 1 < e.Tools.Count && s.Coins >= e.Tools[s.Tool + 1].Price)
                    {
                        s.Coins -= e.Tools[s.Tool + 1].Price;
                        s.Tool++;
                        report.Timeline.Add(new EconEvent(s.T / 60, "tool: " + e.Tools[s.Tool].Name, e.Tools[s.Tool].Price));
                        bought = true;
                    }
                    else if (e.Capacity is { } cu && s.Cap < cu.Costs.Count && s.Coins >= cu.Costs[s.Cap] && cu.Costs[s.Cap] <= nextBig * 0.6)
                    {
                        s.Coins -= cu.Costs[s.Cap];
                        s.Cap++;
                        report.Timeline.Add(new EconEvent(s.T / 60, $"bag level {s.Cap}", cu.Costs[s.Cap - 1]));
                        bought = true;
                    }
                    else if (e.Speed is { } su && s.Speed < su.Costs.Count && s.Coins >= su.Costs[s.Speed] && su.Costs[s.Speed] <= nextBig * 0.35)
                    {
                        s.Coins -= su.Costs[s.Speed];
                        s.Speed++;
                        report.Timeline.Add(new EconEvent(s.T / 60, $"speed level {s.Speed}", su.Costs[s.Speed - 1]));
                        bought = true;
                    }
                    if (bought && report.FirstPurchaseMinutes == null) report.FirstPurchaseMinutes = s.T / 60;
                } while (bought);
                if (s.Zones >= e.Zones.Count && report.AllZonesMinutes == null) report.AllZonesMinutes = s.T / 60;
                if (e.RebirthCost is long rc && s.Zones >= e.Zones.Count && s.Coins >= rc)
                {
                    report.RebirthMinutes = s.T / 60;
                    report.Timeline.Add(new EconEvent(s.T / 60, "rebirth", rc));
                    rebirthDone = true;
                }
                if (s.Zones >= e.Zones.Count && (e.RebirthCost == null || rebirthDone)) break;
            }
            Judge(e, report);
            return report;
        }

        private static void Judge(SimulatorEconomy e, EconomyReport r)
        {
            void Warn(string m) => r.Findings.Add(new MapFinding("warning", "economy", m));
            void Error(string m) => r.Findings.Add(new MapFinding("error", "economy", m));
            if (r.FirstHarvestSeconds > 45) Warn($"The first harvest takes {r.FirstHarvestSeconds:0} s (walking there and breaking a node): the first zone's nodes should be close to the spawn and break in a few hits.");
            if (r.FirstSellSeconds > 240) Error($"The first bag takes {r.FirstSellSeconds / 60:0.0} minutes to fill and sell: a new player needs a reward in about a minute (smaller bag, bigger node values, sell pad closer).");
            else if (r.FirstSellSeconds > 120) Warn($"The first bag takes {r.FirstSellSeconds:0} s to fill and sell; about 60 s keeps a new player hooked.");
            if (r.FirstPurchaseMinutes is null) Error("A player can never afford anything: the prices are out of reach.");
            else if (r.FirstPurchaseMinutes > 4) Warn($"The first purchase comes after {r.FirstPurchaseMinutes:0.0} minutes; make something cost about a minute's earnings.");
            double last = 0, lastGap = 0;
            foreach (var unlock in r.Timeline.Where(t => t.What.StartsWith("unlock ", StringComparison.Ordinal)))
            {
                double gap = unlock.Minute - last;
                if (last == 0 && gap > 15) Warn($"The second zone takes {gap:0} minutes to reach: 4-12 minutes keeps players moving.");
                if (lastGap > 0 && gap > lastGap * 3 && gap > 20) Warn($"{unlock.What} takes {gap:0} minutes after the previous zone ({lastGap:0} before it): a wall rather than a curve.");
                last = unlock.Minute;
                lastGap = gap;
            }
            int unlocked = 1 + r.Timeline.Count(t => t.What.StartsWith("unlock ", StringComparison.Ordinal));
            if (unlocked < e.Zones.Count) Error($"{e.Zones.Count - unlocked} zone(s) are out of reach within six hours of play: an economy wall. Lower their prices or raise what the zones before them pay.");
            if (r.AllZonesMinutes is double all && all < 8 && e.Zones.Count > 2) Warn($"Every zone is unlocked within {all:0} minutes: the game runs out of goals too fast. Raise later prices.");
            if (e.RebirthCost != null && r.RebirthMinutes is double rb && (rb < 15 || rb > 150)) Warn($"The first rebirth comes at {rb:0} minutes; 30-90 is the usual target.");
            if (e.RebirthCost != null && r.RebirthMinutes == null && unlocked >= e.Zones.Count) Warn("The rebirth is never affordable in six hours of play.");
            int toolsBought = r.Timeline.Count(t => t.What.StartsWith("tool:", StringComparison.Ordinal));
            if (e.Tools.Count > 1 && toolsBought < Math.Min(2, e.Tools.Count - 1)) Warn("Players hardly ever buy a better tool: tool prices are too high for what the zones pay.");
        }

        /// <summary>The schedule a good player should meet (minutes from joining): zones, tools, bag and speed levels, the rebirth.</summary>
        public sealed record Schedule(List<double> Zones, List<double> Tools, List<double> Capacity, List<double> Speed, double Rebirth);

        public static Schedule Targets(SimulatorEconomy e)
        {
            var zones = new List<double>();
            double zt = 0;
            for (int k = 1; k < e.Zones.Count; k++)
            {
                zt += 5 * Math.Pow(1.55, k - 1);
                zones.Add(zt);
            }
            // Tools: two cheap early ones, then each a little after the zone whose nodes it is made for.
            var tools = new List<double>();
            for (int k = 1; k < e.Tools.Count; k++)
                tools.Add(k switch
                {
                    1 => 1.1,
                    2 => 3.5,
                    _ => k - 2 < zones.Count ? zones[k - 2] + 2.5 : zt + 8 * (k - 1 - zones.Count),
                });
            var capacity = Enumerable.Range(0, e.Capacity?.Costs.Count ?? 0).Select(k => 2 + k * k * 4.5 + k * 3).ToList();
            var speed = Enumerable.Range(0, e.Speed?.Costs.Count ?? 0).Select(k => 3.5 + k * k * 6 + k * 4).ToList();
            return new Schedule(zones, tools, capacity, speed, zt + 25);
        }

        /// <summary>
        /// Sets the starting bag and the zone, tool, bag, speed and rebirth prices so a good player meets the
        /// schedule (<see cref="Targets"/>): a first sale in about a minute, a tool in the first minute or so, the
        /// second zone at ~5 minutes, each later zone a little further apart, a better tool soon after each new
        /// zone, a rebirth ~25 minutes after the last zone. First each price is what the player earns between the
        /// previous purchase and its time; then the bot plays and every price is nudged until the bot's own
        /// timeline matches the schedule.
        /// </summary>
        public static void Balance(SimulatorEconomy e)
        {
            if (e.Zones.Count == 0) return;
            var targets = Targets(e);

            // The starting bag: full in about 40 seconds of harvesting in the first zone, so the first sale comes in about a minute.
            {
                var s0 = new State();
                var z0 = e.Zones[0];
                double perNode = Math.Ceiling(z0.Hp / Power(e, s0)) * Cooldown(e, s0) + z0.Spacing / Walk(e, s0);
                double nodes = Math.Clamp(40 / Math.Max(0.5, perNode), 5, 20);
                e.BaseCapacity = (int)Math.Max(10, Math.Round(nodes * Math.Max(1, z0.Value) / 5) * 5);
                if (e.Capacity != null) e.Capacity.Step = Math.Max(5, Math.Round(e.BaseCapacity * 0.8 / 5) * 5);
            }

            var plan = new List<(double Minute, string Kind, int Index)>();
            for (int k = 0; k < targets.Zones.Count; k++) plan.Add((targets.Zones[k], "zone", k + 1));
            for (int k = 0; k < targets.Tools.Count; k++) plan.Add((targets.Tools[k], "tool", k + 1));
            for (int k = 0; k < targets.Capacity.Count; k++) plan.Add((targets.Capacity[k], "cap", k));
            for (int k = 0; k < targets.Speed.Count; k++) plan.Add((targets.Speed[k], "speed", k));
            plan = plan.OrderBy(p => p.Minute).ToList();
            var s = new State { Coins = e.StartingCurrency };
            double clock = 0;
            foreach (var (minute, kind, index) in plan)
            {
                var (_, rate) = Best(e, s);
                long price = Nice(Math.Max(10, rate * Math.Max(0.2, minute - clock) * 60));
                switch (kind)
                {
                    case "zone": e.Zones[index].Price = price; s.Zones = index + 1; break;
                    case "tool": e.Tools[index].Price = price; s.Tool = index; break;
                    case "cap": e.Capacity!.Costs[index] = price; s.Cap = index + 1; break;
                    case "speed": e.Speed!.Costs[index] = price; s.Speed = index + 1; break;
                }
                clock = minute;
            }
            if (e.RebirthCost != null) e.RebirthCost = Nice(Best(e, s).Rate * 60 * 18);
            Rising(e);

            // Play it and nudge: each zone, tool and the rebirth towards its time on the schedule.
            for (int pass = 0; pass < 10; pass++)
            {
                var report = Simulate(e);
                bool changed = false;
                double Ratio(double target, double? actual) => actual is double a ? Math.Clamp(target / Math.Max(0.1, a), 0.45, 2.2) : 0.5;
                long Nudge(long price, double ratio)
                {
                    if (Math.Abs(ratio - 1) < 0.12) return price;
                    changed = true;
                    return Nice(Math.Max(10, price * Math.Pow(ratio, 0.85)));
                }
                for (int k = 1; k < e.Zones.Count; k++)
                    e.Zones[k].Price = Nudge(e.Zones[k].Price, Ratio(targets.Zones[k - 1], report.Timeline.FirstOrDefault(t => t.What == "unlock " + e.Zones[k].Name)?.Minute));
                for (int k = 1; k < e.Tools.Count; k++)
                    e.Tools[k].Price = Nudge(e.Tools[k].Price, Ratio(targets.Tools[k - 1], report.Timeline.FirstOrDefault(t => t.What == "tool: " + e.Tools[k].Name)?.Minute));
                if (e.RebirthCost is long rc) e.RebirthCost = Nudge(rc, Ratio(targets.Rebirth, report.RebirthMinutes));
                Rising(e);
                if (!changed) break;
            }
            e.Zones[0].Price = 0;
            if (e.Tools.Count > 0) e.Tools[0].Price = 0;
        }

        /// <summary>Keeps each list rising: a later zone, tool or level never costs less than the one before.</summary>
        private static void Rising(SimulatorEconomy e)
        {
            for (int k = 2; k < e.Tools.Count; k++) e.Tools[k].Price = Math.Max(e.Tools[k].Price, Nice(e.Tools[k - 1].Price * 1.3));
            for (int k = 2; k < e.Zones.Count; k++) e.Zones[k].Price = Math.Max(e.Zones[k].Price, Nice(e.Zones[k - 1].Price * 1.3));
            if (e.Capacity != null) for (int k = 1; k < e.Capacity.Costs.Count; k++) e.Capacity.Costs[k] = Math.Max(e.Capacity.Costs[k], Nice(e.Capacity.Costs[k - 1] * 1.3));
            if (e.Speed != null) for (int k = 1; k < e.Speed.Costs.Count; k++) e.Speed.Costs[k] = Math.Max(e.Speed.Costs[k], Nice(e.Speed.Costs[k - 1] * 1.3));
        }

        /// <summary>A round price players read at a glance (5s, 25s, 100s, 1000s).</summary>
        public static long Nice(double v)
        {
            if (v < 100) return (long)Math.Ceiling(v / 5) * 5;
            if (v < 1000) return (long)Math.Ceiling(v / 25) * 25;
            if (v < 10000) return (long)Math.Ceiling(v / 100) * 100;
            if (v < 100000) return (long)Math.Ceiling(v / 1000) * 1000;
            return (long)Math.Ceiling(v / 10000) * 10000;
        }
    }
}
