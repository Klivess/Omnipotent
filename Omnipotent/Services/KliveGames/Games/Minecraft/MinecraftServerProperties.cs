using System.Text;
using Omnipotent.Services.KliveGames.Models;

namespace Omnipotent.Services.KliveGames.Games.Minecraft
{
    /// <summary>
    /// Reads/writes Minecraft's <c>server.properties</c> (a flat key=value file) while preserving any
    /// keys we don't model. Also exposes a typed schema so the website can render a friendly editor.
    /// </summary>
    public static class MinecraftServerProperties
    {
        public const string FileName = "server.properties";

        /// <summary>Parses server.properties into an ordered key→value map. Returns empty if absent.</summary>
        public static Dictionary<string, string> Load(string serverDir)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string path = Path.Combine(serverDir, FileName);
            if (!File.Exists(path)) return result;

            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1);
                result[key] = value;
            }
            return result;
        }

        /// <summary>Writes the given properties back, merging over any existing on-disk values.</summary>
        public static void Save(string serverDir, IReadOnlyDictionary<string, string> values)
        {
            string path = Path.Combine(serverDir, FileName);
            var merged = Load(serverDir);
            foreach (var kv in values) merged[kv.Key] = kv.Value;

            var sb = new StringBuilder();
            sb.AppendLine("#Minecraft server properties");
            sb.AppendLine("#Managed by KliveGames");
            foreach (var kv in merged.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.AppendLine($"{kv.Key}={kv.Value}");

            Directory.CreateDirectory(serverDir);
            File.WriteAllText(path, sb.ToString());
        }

        /// <summary>Writes a sensible default server.properties (used at deploy) with the given port/motd.</summary>
        public static void WriteDefault(string serverDir, int port, string motd)
        {
            var defaults = new Dictionary<string, string>
            {
                ["server-port"] = port.ToString(),
                ["query.port"] = port.ToString(),
                ["motd"] = motd,
                ["gamemode"] = "survival",
                ["difficulty"] = "easy",
                ["max-players"] = "20",
                ["online-mode"] = "true",
                ["pvp"] = "true",
                ["white-list"] = "false",
                ["enforce-whitelist"] = "false",
                ["level-name"] = "world",
                ["level-seed"] = "",
                ["level-type"] = "minecraft:normal",
                ["view-distance"] = "10",
                ["simulation-distance"] = "10",
                ["spawn-protection"] = "16",
                ["allow-nether"] = "true",
                ["allow-flight"] = "false",
                ["enable-command-block"] = "false",
                ["hardcore"] = "false",
                ["spawn-monsters"] = "true",
                ["generate-structures"] = "true",
                ["enable-rcon"] = "false",
            };
            Save(serverDir, defaults);
        }

        /// <summary>Typed schema for well-known keys, with current values filled in from disk.</summary>
        public static IReadOnlyList<ConfigSchemaField> GetSchema(string serverDir)
        {
            var current = Load(serverDir);
            string V(string key, string fallback = "") => current.TryGetValue(key, out var v) ? v : fallback;

            ConfigSchemaField F(string key, string label, ConfigFieldType type, string category, string? desc = null, params string[] options) =>
                new()
                {
                    Key = key,
                    Label = label,
                    Type = type,
                    Category = category,
                    Description = desc,
                    Options = options.ToList(),
                    Value = V(key),
                };

            var schema = new List<ConfigSchemaField>
            {
                F("motd", "Server Message (MOTD)", ConfigFieldType.Text, "General", "Shown in the multiplayer server list."),
                F("gamemode", "Default Game Mode", ConfigFieldType.Dropdown, "General", null, "survival", "creative", "adventure", "spectator"),
                F("difficulty", "Difficulty", ConfigFieldType.Dropdown, "General", null, "peaceful", "easy", "normal", "hard"),
                F("hardcore", "Hardcore", ConfigFieldType.Boolean, "General", "Permanent death; bans on death."),
                F("max-players", "Max Players", ConfigFieldType.Number, "Players"),
                F("online-mode", "Online Mode", ConfigFieldType.Boolean, "Players", "Verify players against Mojang auth. Disable only for offline/cracked."),
                F("pvp", "PvP", ConfigFieldType.Boolean, "Players"),
                F("white-list", "Whitelist Enabled", ConfigFieldType.Boolean, "Players"),
                F("enforce-whitelist", "Enforce Whitelist", ConfigFieldType.Boolean, "Players", "Kick non-whitelisted players already online when enabled."),
                F("player-idle-timeout", "Idle Timeout (min)", ConfigFieldType.Number, "Players", "0 = never kick idle players."),
                F("level-name", "World Folder", ConfigFieldType.Text, "World"),
                F("level-seed", "World Seed", ConfigFieldType.Text, "World", "Leave blank for a random seed."),
                F("level-type", "World Type", ConfigFieldType.Text, "World", "e.g. minecraft:normal, minecraft:flat, minecraft:large_biomes"),
                F("view-distance", "View Distance", ConfigFieldType.Number, "World"),
                F("simulation-distance", "Simulation Distance", ConfigFieldType.Number, "World"),
                F("spawn-protection", "Spawn Protection (blocks)", ConfigFieldType.Number, "World"),
                F("allow-nether", "Allow Nether", ConfigFieldType.Boolean, "World"),
                F("allow-flight", "Allow Flight", ConfigFieldType.Boolean, "World", "Prevents anti-cheat kicks for legitimately flying players (mods/elytra)."),
                F("enable-command-block", "Enable Command Blocks", ConfigFieldType.Boolean, "World"),
                F("spawn-monsters", "Spawn Monsters", ConfigFieldType.Boolean, "World"),
                F("generate-structures", "Generate Structures", ConfigFieldType.Boolean, "World"),
                F("server-port", "Server Port", ConfigFieldType.Number, "Network", "Changing this also updates the instance's managed port."),
            };
            return schema;
        }
    }
}
