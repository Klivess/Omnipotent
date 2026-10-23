using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox.Build;
using Omnipotent.Services.OmniBlox.Build.WorldGen;
using Omnipotent.Services.OmniBlox.Core;

namespace Omnipotent.Services.OmniBlox.Pipeline
{
    /// <summary>
    /// A playable game before the builder writes a line: when a game reaches Build without a generated world,
    /// the studio makes its baseline from the brief —
    /// <list type="bullet">
    /// <item>the template (obby, tycoon or simulator; a brief that asked for "sandbox" but harvests things is a simulator);</item>
    /// <item>the world spec: the biome from the brief's words, its zones (names it lists, else a themed set),
    /// what players harvest and with what tool — then the generated world;</item>
    /// <item>the template's settings: the tool, the verb, the tool tiers, upgrades, rebirth; a tutorial quest
    /// chain whose guide lines point at real things; welcome tips;</item>
    /// <item>for a simulator, an economy balanced by a bot playing it on the generated map;</item>
    /// <item>design.md with all of it, for the builder to read and improve.</item>
    /// </list>
    /// The first studio's builder started from an empty plate and spent its cycles on scaffolding, then shipped a
    /// strip of grass with a game hidden in client scripts. Now the builder starts from a game that already works
    /// and spends its cycles making it special.
    /// </summary>
    public static class GameScaffold
    {
        /// <summary>Makes the baseline when the world has never been generated. Returns what it did, or null when there was nothing to do.</summary>
        public static string? Ensure(StudioContext ctx, string gameId)
        {
            var game = ctx.Game(gameId);
            if (game?.Brief == null) return null;
            var ws = ctx.Workspace(gameId);
            ws.EnsureCreated(new GameManifest { Title = game.Brief.Title, Template = game.Brief.Template });
            var scene = ws.LoadScene();
            if (scene.Spec != null) return null;
            var manifest = ws.LoadManifest();

            // An older hand-built world is kept beside the new one (nothing is lost), then replaced.
            if (scene.Objects.Count > 0 || scene.Terrain.Count > 0 || scene.Land != null)
            {
                Directory.CreateDirectory(ws.SnapshotsDirectory);
                OmniBloxStore.WriteAtomic(Path.Combine(ws.SnapshotsDirectory, "scene.before-worldgen.json"), Newtonsoft.Json.JsonConvert.SerializeObject(scene, Newtonsoft.Json.Formatting.Indented));
            }
            var fresh = new SceneDocument();

            string template = TemplateFor(game.Brief, manifest);
            manifest.Template = template;
            if (string.IsNullOrWhiteSpace(manifest.Title)) manifest.Title = Clip(Regex.Replace(game.Brief.Title, @"\s*\(working title\)", "", RegexOptions.IgnoreCase), 60);
            var spec = SpecFor(game.Brief, template, Seed(gameId));
            Defaults(manifest, template, spec, game.Brief);

            var result = WorldGenerator.Generate(spec, manifest, ctx.Catalog(), fresh);
            string economyNote = "";
            if (template == "simulator")
                economyNote = TuneEconomy(manifest, result.Scene);
            manifest.Quests = QuestsFor(template, manifest, result.Scene);
            manifest.Save(ws.ManifestPath);
            ws.SaveScene(result.Scene);

            // What the library could not supply in this style: the harvester goes and finds it.
            var missing = result.Warnings.Where(w => w.Contains("no library model", StringComparison.Ordinal)).ToList();
            if (missing.Count > 0) ctx.Harvester.RequestAssets(spec.Zones.Select(z => z.Resource?.Kind ?? "").Where(k => k.Length > 0).Distinct().Select(k => "low poly " + k));

            string design = Design(game.Brief, template, spec, manifest, result, economyNote);
            string existing = ws.ReadFile("design.md") ?? "";
            ws.WriteFile("design.md", existing.Length == 0 ? design : existing.TrimEnd() + "\n\n" + design);
            string summary = $"The studio generated the baseline game ({template}): {result.Generated} objects, " +
                (template == "simulator" ? $"{spec.Zones.Count} zones of {spec.Zones.FirstOrDefault()?.Resource?.Kind ?? "nodes"}, an economy a bot plays through" : template == "obby" ? $"{spec.Course?.Stages} stages" : $"{spec.Plots?.Count} plots") +
                ". design.md describes it.";
            ctx.Event(EventLevel.Info, "build.scaffold", summary, gameId);
            ctx.Update(gameId, g => FactBook.Merge(g.KeyFacts, new[]
            {
                new KeyFact { Key = "world", Text = "The world is generated from its world spec (design_world shows and edits it; regenerating keeps objects placed by hand). The template's kit code runs the core loop; game.json configures it.", Utc = ctx.Clock(), Stage = g.Stage, Source = FactBook.Studio },
            }));
            return summary;
        }

        // ─────────────── the template and the spec ───────────────

        public static string TemplateFor(GameBrief brief, GameManifest manifest)
        {
            string t = (brief.Template ?? manifest.Template ?? "").Trim().ToLowerInvariant();
            if (t is "obby" or "tycoon" or "simulator") return t;
            string text = Text(brief);
            if (Regex.IsMatch(text, @"\b(obby|obstacle|parkour|stage|checkpoint)\b")) return "obby";
            if (Regex.IsMatch(text, @"\b(tycoon|dropper|factory|conveyor)\b")) return "tycoon";
            return "simulator";
        }

        private static readonly (string Biome, string[] Words)[] BiomeWords =
        {
            ("snow", new[] { "snow", "ice", "frost", "frozen", "winter", "arctic", "glacier", "christmas", "polar" }),
            ("desert", new[] { "desert", "sand", "dune", "cactus", "egypt", "pyramid", "oasis" }),
            ("beach", new[] { "beach", "ocean", "island", "tropical", "palm", "pirate", "shell", "coral" }),
            ("jungle", new[] { "jungle", "rainforest", "vine", "temple", "bamboo" }),
            ("volcanic", new[] { "lava", "volcano", "magma", "fire", "inferno", "obsidian" }),
            ("candy", new[] { "candy", "sweet", "sugar", "lollipop", "cake", "dessert", "gummy", "chocolate" }),
            ("spooky", new[] { "spooky", "halloween", "haunted", "ghost", "graveyard", "witch", "pumpkin" }),
            ("crystal", new[] { "crystal", "gem", "diamond", "magic", "space", "galaxy", "star", "cosmic", "mythic" }),
            ("autumn", new[] { "autumn", "fall", "golden", "maple", "harvest", "amber" }),
            ("forest", new[] { "forest", "pine", "wood", "oak", "redwood", "grove", "lumber", "timber", "camp" }),
            ("meadow", new[] { "meadow", "farm", "garden", "flower", "field", "spring", "park", "village" }),
        };

        /// <summary>
        /// The biome the text talks about most, as whole words (a plural or -y ending counts): "Timber Island", about
        /// chopping trees in forests, is a forest, not a beach; ties go to the more specific biome listed first.
        /// </summary>
        public static string BiomeOf(string text, string fallback)
        {
            text = text.ToLowerInvariant();
            string best = fallback;
            int most = 0;
            foreach (var (biome, words) in BiomeWords)
            {
                int n = words.Sum(w => Mentions(text, w));
                if (n > most) (best, most) = (biome, n);
            }
            return best;
        }

        /// <summary>How often a word appears in lower-case text as a whole word ("forests" and "snowy" count, "start" is not "star").</summary>
        private static int Mentions(string text, string word) => Regex.Matches(text, @"\b" + Regex.Escape(word) + @"(?:s|es|y)?\b").Count;

        public static WorldSpec SpecFor(GameBrief brief, string template, int seed)
        {
            string text = Text(brief);
            string biome = BiomeOf(brief.Title + " " + brief.Pitch + " " + brief.ArtStyle + " " + brief.CoreLoop, "meadow");
            var spec = new WorldSpec { Seed = seed, Biome = biome, Style = StyleOf(brief.ArtStyle) };
            switch (template)
            {
                case "obby":
                    {
                        var stages = Regex.Match(text, @"(\d{2,3})\s*(?:stages|levels)");
                        var themes = BiomeWords.Where(b => b.Words.Any(w => Mentions(text, w) > 0)).Select(b => b.Biome switch
                        {
                            "snow" => "ice", "volcanic" => "lava", "crystal" => "space", "forest" => "forest", "jungle" => "jungle", "desert" => "desert", "candy" => "candy", _ => "meadow",
                        }).Distinct().ToList();
                        if (themes.Count < 3) themes.AddRange(new[] { "meadow", "candy", "ice", "lava", "sky" }.Where(t => !themes.Contains(t)).Take(5 - themes.Count));
                        spec.Layout = "course";
                        spec.Course = new CourseSpec { Stages = stages.Success ? Math.Clamp(int.Parse(stages.Groups[1].Value, CultureInfo.InvariantCulture), 10, 80) : 30, Themes = themes.Take(5).ToList(), Floor = biome == "beach" ? "water" : "lava" };
                        break;
                    }
                case "tycoon":
                    spec.Layout = "plots";
                    spec.Plots = new PlotsSpec { Count = 4, Theme = ThemeWord(brief), FirstPrice = 25 };
                    break;
                default:
                    {
                        spec.Layout = "hub";
                        string kind = ResourceOf(text);
                        var names = ZoneNames(brief);
                        int count = names.Count >= 3 ? Math.Min(6, names.Count) : 4;
                        var defaults = WorldGenerator.DefaultZones(biome, kind);
                        spec.Zones = Enumerable.Range(0, count).Select(i =>
                        {
                            var d = i < defaults.Count ? defaults[i] : defaults[^1];
                            string name = i < names.Count ? names[i] : d.Name;
                            return new ZoneSpec
                            {
                                Id = "z" + (i + 1), Name = name, Order = i + 1, Biome = BiomeOf(name, d.Biome), Theme = "", Price = HubLayout.StartingPrice(i + 1),
                                Resource = new ResourceSpec { Kind = kind, Count = 12, Hp = (int)Math.Round(4 * Math.Pow(2.2, i)), Value = (int)Math.Round(2 * Math.Pow(2.4, i)), Respawn = 8 },
                            };
                        }).ToList();
                        spec.Hub = new HubSpec { Name = HubName(brief, kind) };
                        break;
                    }
            }
            return spec;
        }

        private static string StyleOf(string art)
        {
            string a = (art ?? "").ToLowerInvariant();
            if (a.Contains("voxel") || a.Contains("blocky")) return "voxel";
            if (a.Contains("realistic") && !a.Contains("cartoon") && !a.Contains("low")) return "realistic";
            return "stylized";
        }

        /// <summary>What players harvest, from the brief's words.</summary>
        public static string ResourceOf(string text)
        {
            text = text.ToLowerInvariant();
            if (Regex.IsMatch(text, @"\b(chop|tree|wood|log|lumber|timber|axe|forest)")) return "tree";
            if (Regex.IsMatch(text, @"\b(crystal|gem|diamond|jewel)")) return "crystal";
            if (Regex.IsMatch(text, @"\b(mine|mining|rock|stone|ore|pickaxe|boulder|dig)")) return "rock";
            if (Regex.IsMatch(text, @"\b(candy|sweet|lollipop)")) return "candy";
            if (Regex.IsMatch(text, @"\b(pumpkin)")) return "pumpkin";
            if (Regex.IsMatch(text, @"\b(mushroom)")) return "mushroom";
            if (Regex.IsMatch(text, @"\b(cactus|cacti)")) return "cactus";
            if (Regex.IsMatch(text, @"\b(flower|garden|bloom)")) return "flower";
            return "tree";
        }

        /// <summary>Zone names the brief lists ("5 zones: Meadow, Pine Grove, Old Growth…" or "zones (A, B, C)").</summary>
        public static List<string> ZoneNames(GameBrief brief)
        {
            foreach (string line in brief.MustHave.Append(brief.CoreLoop).Append(brief.Pitch))
            {
                var m = Regex.Match(line, @"zones?[^:(]{0,40}[:(]\s*([^).;]+)", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                var names = Regex.Split(m.Groups[1].Value, @",|\band\b|→|->|/")
                    .Select(n => Regex.Replace(n, @"[^\p{L}\p{N}' -]", "").Trim())
                    .Where(n => n.Length is > 1 and < 32 && !Regex.IsMatch(n, @"^\d+$"))
                    .ToList();
                if (names.Count >= 3) return names.Select(n => char.ToUpperInvariant(n[0]) + n[1..]).Distinct().Take(6).ToList();
            }
            return new List<string>();
        }

        private static string HubName(GameBrief brief, string kind) => kind switch
        {
            "tree" => "Lumber Camp", "rock" or "crystal" => "Mining Camp", "candy" => "Candy Shop Square", "pumpkin" => "Pumpkin Patch", "flower" => "Garden Square",
            _ => "Town Square",
        };

        private static string ThemeWord(GameBrief brief)
        {
            var m = Regex.Match(brief.Title + " " + brief.Pitch, @"\b([A-Z][a-z]+)\s+(?:Tycoon|Factory|Empire)\b");
            return m.Success ? m.Groups[1].Value : "Factory";
        }

        // ─────────────── the template's settings ───────────────

        private static void Defaults(GameManifest manifest, string template, WorldSpec spec, GameBrief brief)
        {
            if (string.IsNullOrWhiteSpace(manifest.CurrencyName)) manifest.CurrencyName = "Coins";
            if (string.IsNullOrWhiteSpace(manifest.CurrencyIcon)) manifest.CurrencyIcon = "🪙";
            var t = manifest.TemplateSettings;
            switch (template)
            {
                case "simulator":
                    {
                        string kind = spec.Zones.FirstOrDefault()?.Resource?.Kind ?? "tree";
                        var (tool, style, verb, item, icon) = ToolFor(kind);
                        t["ToolName"] = tool;
                        t["ToolStyle"] = style;
                        t["Verb"] = verb;
                        t["ItemIcon"] = item;
                        t["Tools"] = new JArray(new[] { ("Wooden", 1.0, 0.45), ("Stone", 2.0, 0.42), ("Iron", 4.0, 0.4), ("Golden", 9.0, 0.38), ("Diamond", 20.0, 0.35), ("Mythic", 45.0, 0.32) }
                            .Select((x, i) => new JObject { ["Name"] = $"{x.Item1} {tool}", ["Icon"] = i < 4 ? icon : i == 4 ? "💎" : "✨", ["Power"] = x.Item2, ["Cooldown"] = x.Item3, ["Price"] = 0 }));
                        t["Upgrades"] = new JArray(
                            new JObject { ["Key"] = "capacity", ["Name"] = "Bigger Bag", ["Icon"] = "🎒", ["Costs"] = new JArray(50, 150, 400, 1000, 2500), ["Step"] = 20 },
                            new JObject { ["Key"] = "speed", ["Name"] = "Faster Shoes", ["Icon"] = "👟", ["Costs"] = new JArray(75, 250, 700, 1800), ["Step"] = 3 });
                        manifest.Rebirth ??= new RebirthSpec { Enabled = true, Cost = 10000, Growth = 2.5, Bonus = 0.5 };
                        if (manifest.Tips.Count == 0)
                            manifest.Tips = new List<string> { $"Walk up to a {kind} and tap {icon} {verb.ToUpperInvariant()} (or click) to harvest it.", "Full bag? Step on the glowing SELL pad for coins.", "Buy a better tool and unlock new zones at their gates!" };
                        break;
                    }
                case "obby":
                    if (manifest.Tips.Count == 0)
                        manifest.Tips = new List<string> { "Jump from platform to platform — red is lava!", "Every green pad is a checkpoint: fall and you start there.", "Reach the golden finish for a big reward!" };
                    t["StageReward"] = 5;
                    t["FinishReward"] = 250;
                    break;
                case "tycoon":
                    if (manifest.Tips.Count == 0)
                        manifest.Tips = new List<string> { "Walk into your plot and step on the green button.", "Your droppers earn cash: step on the gold COLLECT pad to bank it.", "Each button unlocks the next: build your empire!" };
                    manifest.StartingCurrency = Math.Max(manifest.StartingCurrency, (int)(spec.Plots?.FirstPrice ?? 25));
                    break;
            }
        }

        public static (string Tool, string Style, string Verb, string Item, string Icon) ToolFor(string kind) => kind switch
        {
            "rock" => ("Pickaxe", "pickaxe", "Mine", "🪨", "⛏️"),
            "crystal" => ("Pickaxe", "pickaxe", "Mine", "💎", "⛏️"),
            "candy" => ("Hammer", "hammer", "Smash", "🍬", "🔨"),
            "pumpkin" => ("Hammer", "hammer", "Smash", "🎃", "🔨"),
            "mushroom" => ("Wand", "wand", "Pick", "🍄", "🪄"),
            "flower" => ("Wand", "wand", "Pick", "🌸", "🪄"),
            "cactus" => ("Axe", "axe", "Chop", "🌵", "🪓"),
            _ => ("Axe", "axe", "Chop", "🪵", "🪓"),
        };

        /// <summary>Balances a simulator's prices with a bot playing it on its map, and writes them into the gates and game.json.</summary>
        public static string TuneEconomy(GameManifest manifest, SceneDocument scene)
        {
            var econ = SimulatorEconomy.FromGame(manifest, scene);
            if (econ.Zones.Count == 0) return "";
            Economy.Balance(econ);
            var prices = econ.Zones.ToDictionary(z => z.Name, z => z.Price, StringComparer.OrdinalIgnoreCase);
            foreach (var gate in scene.Objects.Where(o => o.Tags.Contains("ZoneGate", StringComparer.OrdinalIgnoreCase)))
                if (gate.Config.TryGetValue("Zone", out var z) && prices.TryGetValue(Convert.ToString(z is JValue jv ? jv.Value : z, CultureInfo.InvariantCulture) ?? "", out long price))
                    gate.Config["Price"] = price;
            if (scene.Spec != null)
                foreach (var zone in scene.Spec.Zones)
                    if (prices.TryGetValue(zone.Name, out long price)) zone.Price = price;
            var t = manifest.TemplateSettings;
            t["BaseCapacity"] = econ.BaseCapacity;
            if (t.TryGetValue("Tools", out var tools) && JToken.FromObject(tools) is JArray ta)
            {
                for (int i = 0; i < ta.Count && i < econ.Tools.Count; i++) ta[i]["Price"] = econ.Tools[i].Price;
                t["Tools"] = ta;
            }
            t["Upgrades"] = new JArray(
                new JObject { ["Key"] = "capacity", ["Name"] = "Bigger Bag", ["Icon"] = "🎒", ["Costs"] = new JArray(econ.Capacity!.Costs), ["Step"] = econ.Capacity.Step },
                new JObject { ["Key"] = "speed", ["Name"] = "Faster Shoes", ["Icon"] = "👟", ["Costs"] = new JArray(econ.Speed!.Costs), ["Step"] = econ.Speed.Step });
            if (manifest.Rebirth != null && econ.RebirthCost is long rc) manifest.Rebirth.Cost = rc;
            var report = Economy.Simulate(econ);
            return report.Describe();
        }

        /// <summary>A tutorial that teaches the loop one step at a time, each with a guide line to where to do it.</summary>
        public static List<QuestSpec> QuestsFor(string template, GameManifest manifest, SceneDocument scene)
        {
            string Verb() => manifest.TemplateSettings.TryGetValue("Verb", out var v) ? Convert.ToString(v is JValue jv ? jv.Value : v, CultureInfo.InvariantCulture) ?? "Chop" : "Chop";
            string Tool() => manifest.TemplateSettings.TryGetValue("ToolName", out var v) ? Convert.ToString(v is JValue jv ? jv.Value : v, CultureInfo.InvariantCulture) ?? "Axe" : "Axe";
            switch (template)
            {
                case "simulator":
                    {
                        var zones = scene.Layout.Zones.Where(z => z.Gated).ToList();
                        string noun = Plural(scene.Spec?.Zones.FirstOrDefault()?.Resource?.Kind ?? "tree");
                        var list = new List<QuestSpec>
                        {
                            new() { Id = "q1", Text = $"{Verb()} 3 {noun}", Event = "harvest", Count = 3, Reward = 10, Target = "Resource" },
                            new() { Id = "q2", Text = "Sell your bag at the SELL pad", Event = "sell", Count = 1, Reward = 15, Target = "SellZone" },
                            new() { Id = "q3", Text = $"Buy a better {Tool()}", Event = "tool", Count = 1, Reward = 20, Target = KitProps.MatName("upgrades") },
                        };
                        if (zones.Count > 0) list.Add(new() { Id = "q4", Text = $"Unlock {zones[0].Name}", Event = "zone", Count = 1, Reward = 50, Target = KitProps.GateName(zones[0].Name) });
                        list.Add(new() { Id = "q5", Text = $"{Verb()} 40 {noun}", Event = "harvest", Count = 40, Reward = 80 });
                        if (zones.Count > 1) list.Add(new() { Id = "q6", Text = $"Unlock {zones[1].Name}", Event = "zone", Count = 1, Reward = 150, Target = KitProps.GateName(zones[1].Name) });
                        list.Add(new() { Id = "q7", Text = "Upgrade 5 times", Event = "upgrade", Count = 5, Reward = 200 });
                        if (manifest.Rebirth is { Enabled: true }) list.Add(new() { Id = "q8", Text = "Rebirth for a permanent boost", Event = "rebirth", Count = 1, Reward = 0, Target = KitProps.MatName("rebirth") });
                        return list;
                    }
                case "obby":
                    return new List<QuestSpec>
                    {
                        new() { Id = "q1", Text = "Reach stage 3", Event = "stage", Count = 2, Reward = 10 },
                        new() { Id = "q2", Text = "Reach stage 10", Event = "stage", Count = 7, Reward = 40 },
                        new() { Id = "q3", Text = "Reach stage 20", Event = "stage", Count = 10, Reward = 80 },
                        new() { Id = "q4", Text = "Finish the obby!", Event = "finish", Count = 1, Reward = 200, Target = "Finish" },
                    };
                case "tycoon":
                    return new List<QuestSpec>
                    {
                        new() { Id = "q1", Text = "Buy your first dropper", Event = "unlock", Count = 1, Reward = 10, Target = "TycoonButton" },
                        new() { Id = "q2", Text = "Collect your cash", Event = "collect", Count = 1, Reward = 15, Target = "TycoonCollector" },
                        new() { Id = "q3", Text = "Buy 4 more upgrades", Event = "unlock", Count = 4, Reward = 60, Target = "TycoonButton" },
                        new() { Id = "q4", Text = "Build the whole tycoon", Event = "unlock", Count = 7, Reward = 300, Target = "TycoonButton" },
                    };
                default:
                    return manifest.Quests;
            }
        }

        // ─────────────── design.md ───────────────

        private static string Design(GameBrief brief, string template, WorldSpec spec, GameManifest manifest, GenerationResult result, string economy)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Generated baseline (the studio made this; improve it)");
            sb.AppendLine();
            sb.AppendLine($"Template: **{template}**. The kit runs the whole core loop; game.json and the world spec configure it. Write code only for what the template does not do (as systems with specs), never a second copy of the loop or the HUD.");
            sb.AppendLine();
            switch (template)
            {
                case "simulator":
                    {
                        var t = manifest.TemplateSettings;
                        sb.AppendLine($"Core loop: walk to a node, tap {Val(t, "Verb")} with the {Val(t, "ToolName")} (every player holds one from the start), fill the bag ({Val(t, "BaseCapacity")} to begin), sell at the SELL pad in the hub, buy a better {Val(t, "ToolName")} and a bigger bag at the upgrade stand, unlock the next zone at its gate, rebirth at the altar.");
                        sb.AppendLine();
                        sb.AppendLine("Zones (in order):");
                        foreach (var z in spec.Zones) sb.AppendLine($"- {z.Name} ({z.Biome}): {z.Resource?.Count} {z.Resource?.Kind} nodes, {z.Resource?.Hp} hits at power 1, {z.Resource?.Value} each, {(z.Price > 0 ? $"unlocks for {z.Price}" : "open")}");
                        sb.AppendLine();
                        if (economy.Length > 0)
                        {
                            sb.AppendLine("Economy (a bot playing these numbers on this map):");
                            sb.AppendLine("```");
                            sb.AppendLine(economy.TrimEnd());
                            sb.AppendLine("```");
                        }
                        break;
                    }
                case "obby":
                    sb.AppendLine($"Course: {spec.Course?.Stages} stages over a {spec.Course?.Floor} basin, themes {string.Join(" → ", spec.Course?.Themes ?? new())}; one new obstacle at a time, a checkpoint every stage, the finish pays {Val(manifest.TemplateSettings, "FinishReward")}.");
                    break;
                case "tycoon":
                    sb.AppendLine($"Plots: {spec.Plots?.Count} {spec.Plots?.Theme} plots round the plaza; each has a line of buttons that unlock droppers, a conveyor, an upgrader and decor, prices rising with the income. Players start with {manifest.StartingCurrency} to buy the first button.");
                    break;
            }
            sb.AppendLine();
            sb.AppendLine("Tutorial quests: " + string.Join(" → ", manifest.Quests.Select(q => q.Text)) + ".");
            sb.AppendLine();
            sb.AppendLine("World: " + result.Describe().Replace("\n", "\n  "));
            sb.AppendLine("Brief must-haves to check against: " + string.Join("; ", brief.MustHave));
            return sb.ToString();
        }

        public static string Plural(string kind) => kind switch
        {
            "candy" => "candies", "cactus" => "cacti", "tree" or "rock" or "crystal" or "pumpkin" or "mushroom" or "flower" => kind + "s", _ => kind,
        };

        private static string Val(Dictionary<string, object> t, string key) => t.TryGetValue(key, out var v) ? Convert.ToString(v is JValue jv ? jv.Value : v, CultureInfo.InvariantCulture) ?? "" : "";

        private static string Text(GameBrief b) => string.Join(" ", new[] { b.Title, b.Genre, b.Pitch, b.CoreLoop, b.ArtStyle }.Concat(b.MustHave).Concat(b.NiceToHave)).ToLowerInvariant();

        private static int Seed(string gameId) => Math.Abs(gameId.Aggregate(17, (h, c) => unchecked(h * 31 + c))) % 100000 + 1;

        private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
    }
}
