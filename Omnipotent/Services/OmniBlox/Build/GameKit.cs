using System.Globalization;
using System.Reflection;
using System.Text;

namespace Omnipotent.Services.OmniBlox.Build
{
    /// <summary>One Luau file of the Game Kit, as embedded in the assembly (path relative to GameKit/).</summary>
    public sealed record KitFile(string Path, string Source)
    {
        public string FileName => Path.Split('/').Last();
    }

    /// <summary>
    /// The Luau every game is built on (Services/OmniBlox/GameKit, embedded at build time): saving, currency, shop
    /// and purchases, analytics, daily rewards, leaderboard, feedback, zones and gates, rebirth, quests, world
    /// behaviours for tagged parts, an in-engine test runner and the client HUD. A game's own loop is its own code.
    ///
    /// The kit used to carry three genre templates (obby, tycoon, simulator) that every game was made from, and the
    /// studio's games all came out alike. No game is made from one any more; the modules stay only for the games
    /// that were (<see cref="LegacyTemplates"/>), and only those games get them.
    /// </summary>
    public static class GameKit
    {
        public const string ResourcePrefix = "OmniBloxKit/";

        /// <summary>
        /// The templates games were made from before they were abolished (id → the module in
        /// ServerScriptService.OmniKitServer.Templates). A game whose game.json names one keeps it so it goes on working;
        /// nothing new is ever made from one.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> LegacyTemplates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["obby"] = "Obby",
            ["tycoon"] = "Tycoon",
            ["simulator"] = "Simulator",
        };

        /// <summary>Whether a game was made from one of the old templates (its game.json names one).</summary>
        public static bool IsLegacy(string? template) => template != null && LegacyTemplates.ContainsKey(template.Trim());

        /// <summary>A kit file that belongs to an old template (only games made from one get it).</summary>
        public static bool IsTemplateFile(KitFile file) => file.Path.StartsWith("Server/Templates/", StringComparison.Ordinal);

        /// <summary>The kit files a game gets: all of them for a game made from an old template, the rest without the templates.</summary>
        public static IEnumerable<KitFile> FilesFor(string? template) => IsLegacy(template) ? Files : Files.Where(f => !IsTemplateFile(f));

        /// <summary>The CollectionService tags the kit gives behaviour to, with what each does — told to the builder.</summary>
        public static readonly IReadOnlyDictionary<string, string> Tags = new Dictionary<string, string>
        {
            ["KillBrick"] = "kills on touch",
            ["Coin"] = "pickup worth Value (default 1), respawns after Respawn seconds",
            ["Bounce"] = "launches the player up with Power (default 90)",
            ["SpeedPad"] = "sets walk speed to Speed for Seconds",
            ["Conveyor"] = "moves players along its front (LookVector) at Speed",
            ["Teleporter"] = "teleports onto the part named Target",
            ["Spinner"] = "spins about Y at Speed degrees/second",
            ["Mover"] = "slides back and forth by Offset (\"x,y,z\") over Period seconds",
            ["Disappearing"] = "vanishes for Hidden seconds every Visible seconds",
            ["Leaderboard"] = "shows the global top-10 on its front face",
            ["Checkpoint"] = "games made from the old obby template: stage marker with Stage = 1..N (an obby of your own keeps its stages in its own system)",
            ["Finish"] = "games made from the old obby template: the end of the course",
            ["TycoonPlot"] = "games made from the old tycoon template: one player's plot",
            ["TycoonButton"] = "games made from the old tycoon template: Price, Unlocks, optional Requires",
            ["TycoonCollector"] = "games made from the old tycoon template: touch to bank the plot's income",
            ["Dropper"] = "games made from the old tycoon template: cosmetic dropper inside an item",
            ["Collectible"] = "games made from the old simulator template: worth Value, respawns after Respawn seconds (use Coin, or your own tag and system)",
            ["SellZone"] = "games made from the old simulator template: touch to sell the bag",
            ["ZoneGate"] = "blocks a zone until bought: Zone (name), Price, optional Currency (an extra currency) and Requires (another zone); shows a lock sign and an Unlock prompt, then turns see-through for that player",
            ["Spin"] = "turns in place about Y at Speed degrees/second (default 90): coins, gems, rewards",
            ["Bob"] = "floats gently up and down by Height (default 1) every Period seconds (1.6): pickups, signs",
            ["Glow"] = "adds a point light: Color (default the part's), Range (14), Brightness (1.6)",
            ["Sparkle"] = "adds sparkle particles: Color (default gold), Rate (6) — rewards, goals, rare things",
        };

        private static readonly Lazy<IReadOnlyList<KitFile>> files = new(LoadFiles);
        private static readonly Lazy<string> fingerprint = new(() =>
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            foreach (var f in Files)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(f.Path + "\n" + f.Source + "\n");
                sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash!)[..16].ToLowerInvariant();
        });

        public static IReadOnlyList<KitFile> Files => files.Value;

        /// <summary>Identifies this version of the kit: a build of unchanged files on a different kit is a different build.</summary>
        public static string Fingerprint => fingerprint.Value;

        private static IReadOnlyList<KitFile> LoadFiles()
        {
            var assembly = typeof(GameKit).Assembly;
            var list = new List<KitFile>();
            foreach (string name in assembly.GetManifestResourceNames())
            {
                if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream, Encoding.UTF8);
                list.Add(new KitFile(name[ResourcePrefix.Length..].Replace('\\', '/'), reader.ReadToEnd()));
            }
            if (list.Count == 0) throw new InvalidOperationException("The OmniBlox Game Kit is not embedded in this build.");
            return list.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        }

        public static string TemplateModule(string? template) =>
            template != null && LegacyTemplates.TryGetValue(template.Trim(), out var module) ? module : "";
    }

    /// <summary>Writes C# values as Luau literals (for the generated Config modules).</summary>
    public static class LuauLiteral
    {
        public static string Write(object? value, int indent = 0)
        {
            var sb = new StringBuilder();
            Append(sb, value, indent);
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, object? value, int indent)
        {
            switch (value)
            {
                case null:
                    sb.Append("nil");
                    return;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    return;
                case string s:
                    sb.Append(Quote(s));
                    return;
                case float f:
                    sb.Append(Num(f));
                    return;
                case double d:
                    sb.Append(Num(d));
                    return;
                case decimal m:
                    sb.Append(m.ToString(CultureInfo.InvariantCulture));
                    return;
                case int or long or short or byte or uint or ulong:
                    sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    return;
                case Newtonsoft.Json.Linq.JValue jv:
                    Append(sb, jv.Value, indent);
                    return;
                case Newtonsoft.Json.Linq.JObject jo:
                    Append(sb, jo.Properties().ToDictionary(p => p.Name, p => (object?)p.Value), indent);
                    return;
                case Newtonsoft.Json.Linq.JArray ja:
                    Append(sb, ja.Cast<object?>().ToList(), indent);
                    return;
                case System.Collections.IDictionary dict:
                    {
                        if (dict.Count == 0) { sb.Append("{}"); return; }
                        string pad = new('\t', indent + 1);
                        sb.Append("{\n");
                        foreach (System.Collections.DictionaryEntry e in dict)
                        {
                            sb.Append(pad).Append(Key(Convert.ToString(e.Key, CultureInfo.InvariantCulture) ?? "")).Append(" = ");
                            Append(sb, e.Value, indent + 1);
                            sb.Append(",\n");
                        }
                        sb.Append(new string('\t', indent)).Append('}');
                        return;
                    }
                case System.Collections.IEnumerable list:
                    {
                        var items = list.Cast<object?>().ToList();
                        if (items.Count == 0) { sb.Append("{}"); return; }
                        bool simple = items.All(i => i is null or bool or string or int or long or float or double or decimal);
                        if (simple && items.Count <= 8)
                        {
                            sb.Append("{ ");
                            for (int k = 0; k < items.Count; k++)
                            {
                                if (k > 0) sb.Append(", ");
                                Append(sb, items[k], indent);
                            }
                            sb.Append(" }");
                            return;
                        }
                        string pad = new('\t', indent + 1);
                        sb.Append("{\n");
                        foreach (var item in items)
                        {
                            sb.Append(pad);
                            Append(sb, item, indent + 1);
                            sb.Append(",\n");
                        }
                        sb.Append(new string('\t', indent)).Append('}');
                        return;
                    }
                default:
                    {
                        // Plain objects: public readable properties as a table.
                        var props = value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                            .ToDictionary(p => p.Name, p => p.GetValue(value));
                        Append(sb, props, indent);
                        return;
                    }
            }
        }

        public static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\").Append(((int)ch).ToString("D3", CultureInfo.InvariantCulture));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        private static readonly HashSet<string> LuauKeywords = new(StringComparer.Ordinal)
        {
            "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "if", "in", "local", "nil",
            "not", "or", "repeat", "return", "then", "true", "until", "while",
        };

        private static string Key(string key)
        {
            bool identifier = key.Length > 0 && (char.IsLetter(key[0]) || key[0] == '_') && key.All(c => char.IsLetterOrDigit(c) || c == '_') && !LuauKeywords.Contains(key);
            return identifier ? key : "[" + Quote(key) + "]";
        }

        private static string Num(double d)
        {
            if (double.IsNaN(d)) return "0/0";
            if (double.IsPositiveInfinity(d)) return "math.huge";
            if (double.IsNegativeInfinity(d)) return "-math.huge";
            return d.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
