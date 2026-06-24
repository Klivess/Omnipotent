using System.Text;
using Omnipotent.Services.KliveGames.Models;

namespace Omnipotent.Services.KliveGames.Games.Terraria
{
    /// <summary>
    /// Reads/writes Terraria's <c>serverconfig.txt</c> (flat key=value, no escaping). Shared by vanilla
    /// and tModLoader (tModLoader is a Terraria fork and honours the same options).
    /// </summary>
    public static class TerrariaServerConfig
    {
        public const string FileName = "serverconfig.txt";

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
                result[line.Substring(0, eq).Trim()] = line.Substring(eq + 1);
            }
            return result;
        }

        public static void Save(string serverDir, IReadOnlyDictionary<string, string> values)
        {
            string path = Path.Combine(serverDir, FileName);
            var merged = Load(serverDir);
            foreach (var kv in values) merged[kv.Key] = kv.Value;

            var sb = new StringBuilder();
            sb.AppendLine("#Terraria server config");
            sb.AppendLine("#Managed by KliveGames");
            foreach (var kv in merged.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.AppendLine($"{kv.Key}={kv.Value}");

            Directory.CreateDirectory(serverDir);
            File.WriteAllText(path, sb.ToString());
        }

        /// <summary>
        /// Writes a fully non-interactive default config (so the server never blocks on a world-creation
        /// prompt). The world is auto-created on first boot under the instance's worlds folder.
        /// </summary>
        public static void WriteDefault(string serverDir, int port, string worldName, int autocreateSize,
            int difficulty, int maxPlayers, string? password, string? seed)
        {
            string worldsDir = Path.Combine(serverDir, "worlds");
            Directory.CreateDirectory(worldsDir);
            string safeWorld = string.Concat((worldName ?? "World").Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '_' || c == '-')).Trim();
            if (safeWorld.Length == 0) safeWorld = "World";

            var cfg = new Dictionary<string, string>
            {
                ["world"] = Path.Combine(worldsDir, safeWorld + ".wld"),
                ["worldpath"] = worldsDir,
                ["autocreate"] = autocreateSize.ToString(),   // 1 small / 2 medium / 3 large
                ["worldname"] = safeWorld,
                ["difficulty"] = difficulty.ToString(),       // 0 classic / 1 expert / 2 master / 3 journey
                ["maxplayers"] = Math.Clamp(maxPlayers, 1, 255).ToString(),
                ["port"] = port.ToString(),
                ["motd"] = $"{safeWorld} — powered by KliveGames",
                ["secure"] = "1",
                ["upnp"] = "0",                               // KliveGames manages port-forwarding itself
                ["language"] = "en-US",
            };
            if (!string.IsNullOrWhiteSpace(password)) cfg["password"] = password!;
            if (!string.IsNullOrWhiteSpace(seed)) cfg["seed"] = seed!;

            Save(serverDir, cfg);
        }

        /// <summary>Typed schema for the post-deploy config editor (world size/difficulty are creation-only
        /// and intentionally excluded).</summary>
        public static IReadOnlyList<ConfigSchemaField> GetSchema(string serverDir)
        {
            var current = Load(serverDir);
            string V(string key, string fallback = "") => current.TryGetValue(key, out var v) ? v : fallback;

            ConfigSchemaField F(string key, string label, ConfigFieldType type, string category, string? desc = null, params string[] options) =>
                new() { Key = key, Label = label, Type = type, Category = category, Description = desc, Options = options.ToList(), Value = V(key) };

            return new List<ConfigSchemaField>
            {
                F("motd", "Message of the Day", ConfigFieldType.Text, "General"),
                F("maxplayers", "Max Players", ConfigFieldType.Number, "General", "1–255."),
                F("password", "Server Password", ConfigFieldType.Text, "General", "Leave blank for no password."),
                F("secure", "Cheat Protection", ConfigFieldType.Dropdown, "General", "1 = on, 0 = off.", "1", "0"),
                F("language", "Language", ConfigFieldType.Text, "General", "e.g. en-US, de-DE, fr-FR."),
                F("npcstream", "NPC Stream", ConfigFieldType.Number, "Performance", "Lower reduces enemy skipping but uses more bandwidth."),
                F("port", "Server Port", ConfigFieldType.Number, "Network", "Changing this also updates the instance's managed port."),
            };
        }
    }
}
