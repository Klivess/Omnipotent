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
    /// The Luau every generated game starts from (Services/OmniBlox/GameKit, embedded at build time):
    /// saving, currency, shop and purchases, analytics, daily rewards, leaderboard, feedback, world
    /// behaviours for tagged parts, genre templates, an in-engine test runner and the client HUD.
    /// </summary>
    public static class GameKit
    {
        public const string ResourcePrefix = "OmniBloxKit/";

        /// <summary>Template id → the module name in ServerScriptService.OmniKitServer.Templates.</summary>
        public static readonly IReadOnlyDictionary<string, string> Templates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["obby"] = "Obby",
            ["tycoon"] = "Tycoon",
            ["simulator"] = "Simulator",
            ["sandbox"] = "",
        };

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
            ["Checkpoint"] = "obby: stage marker with Stage = 1..N (1 is the start)",
            ["Finish"] = "obby: the end; touching it after the last stage completes the obby",
            ["TycoonPlot"] = "tycoon: one player's plot (Items folder + buttons + collector inside)",
            ["TycoonButton"] = "tycoon: Price, Unlocks (item name), optional Requires",
            ["TycoonCollector"] = "tycoon: touch to bank the plot's income",
            ["Dropper"] = "tycoon: cosmetic dropper inside an item",
            ["Collectible"] = "simulator: worth Value, respawns after Respawn seconds",
            ["SellZone"] = "simulator: touch to sell the bag",
        };

        private static readonly Lazy<IReadOnlyList<KitFile>> files = new(LoadFiles);

        public static IReadOnlyList<KitFile> Files => files.Value;

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
            template != null && Templates.TryGetValue(template.Trim(), out var module) ? module : "";
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
