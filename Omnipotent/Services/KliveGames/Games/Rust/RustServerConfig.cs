using System.Globalization;
using System.Text;
using Omnipotent.Services.KliveGames.Models;

namespace Omnipotent.Services.KliveGames.Games.Rust
{
    /// <summary>Reads and writes the Rust server.cfg under the instance identity directory.</summary>
    public static class RustServerConfig
    {
        public static string GetIdentity(GameServerInstance inst) => $"klivegames-{inst.Id}";

        public static string GetPath(GameServerInstance inst)
            => Path.Combine(inst.ServerDirectory, "server", GetIdentity(inst), "cfg", "server.cfg");

        public static Dictionary<string, string> Load(GameServerInstance inst)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string path = GetPath(inst);
            if (!File.Exists(path)) return result;

            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;

                int split = 0;
                while (split < line.Length && !char.IsWhiteSpace(line[split])) split++;
                if (split == 0 || split >= line.Length) continue;

                string key = line[..split];
                string value = line[split..].Trim();
                char quote = (char)34;
                if (value.Length >= 2 && value[0] == quote && value[^1] == quote)
                    value = value[1..^1];
                result[key] = value;
            }
            return result;
        }

        public static void Save(GameServerInstance inst, IReadOnlyDictionary<string, string> values)
        {
            var merged = Load(inst);
            foreach (var pair in values) merged[pair.Key] = pair.Value;

            string path = GetPath(inst);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var text = new StringBuilder();
            text.AppendLine("# Rust dedicated server configuration");
            text.AppendLine("# Managed by KliveGames");
            foreach (var pair in merged.OrderBy(p => p.Key, StringComparer.Ordinal))
                text.Append(pair.Key).Append(' ').AppendLine(FormatValue(pair.Value));
            File.WriteAllText(path, text.ToString());
        }

        public static void WriteDefault(GameServerInstance inst, int worldSize, int seed, int maxPlayers, string description)
        {
            Save(inst, new Dictionary<string, string>
            {
                ["server.hostname"] = inst.Name,
                ["server.description"] = description,
                ["server.level"] = "Procedural Map",
                ["server.worldsize"] = Math.Clamp(worldSize, 1000, 6000).ToString(CultureInfo.InvariantCulture),
                ["server.seed"] = Math.Clamp(seed, 0, int.MaxValue).ToString(CultureInfo.InvariantCulture),
                ["server.maxplayers"] = Math.Clamp(maxPlayers, 1, 500).ToString(CultureInfo.InvariantCulture),
                ["server.saveinterval"] = "600",
                ["server.port"] = inst.Port.ToString(CultureInfo.InvariantCulture),
                ["server.queryport"] = (inst.Port + 1).ToString(CultureInfo.InvariantCulture),
            });
        }

        public static IReadOnlyList<ConfigSchemaField> GetSchema(GameServerInstance inst)
        {
            var current = Load(inst);
            string V(string key, string fallback = "") => current.TryGetValue(key, out var value) ? value : fallback;
            ConfigSchemaField F(string key, string label, ConfigFieldType type, string category, string? description = null, params string[] options)
                => new()
                {
                    Key = key,
                    Label = label,
                    Type = type,
                    Category = category,
                    Description = description,
                    Options = options.ToList(),
                    Value = V(key),
                };

            return new List<ConfigSchemaField>
            {
                F("server.hostname", "Server Name", ConfigFieldType.Text, "General"),
                F("server.description", "Description", ConfigFieldType.Text, "General"),
                F("server.maxplayers", "Max Players", ConfigFieldType.Number, "Players", "1–500."),
                F("server.level", "Map Type", ConfigFieldType.Dropdown, "World", null, "Procedural Map"),
                F("server.worldsize", "World Size", ConfigFieldType.Number, "World", "1000–6000. Changing this creates a different map on restart."),
                F("server.seed", "World Seed", ConfigFieldType.Number, "World", "0–2147483647. Changing this creates a different map on restart."),
                F("server.saveinterval", "Save Interval", ConfigFieldType.Number, "Performance", "Seconds between automatic saves."),
                F("server.port", "Game Port", ConfigFieldType.Number, "Network", "UDP. The server-browser query port is managed automatically as game port + 1."),
            };
        }

        public static void Apply(GameServerInstance inst, Dictionary<string, string> values)
        {
            if (values.TryGetValue("server.port", out string? portText) && int.TryParse(portText, out int port))
                values["server.queryport"] = (port + 1).ToString(CultureInfo.InvariantCulture);
            Save(inst, values);
        }

        private static string FormatValue(string value)
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                return value;
            string safe = (value ?? string.Empty)
                .Replace((char)34, (char)39)
                .Replace((char)13, ' ')
                .Replace((char)10, ' ');
            char quote = (char)34;
            return $"{quote}{safe}{quote}";
        }
    }
}
