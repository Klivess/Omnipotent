using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.OmniBlox.Agents
{
    /// <summary>What a tool hands back: text for the transcript, optional pictures, and (for finish_cycle) the report.</summary>
    public sealed class ToolResult
    {
        public string Text { get; init; } = "";
        public List<byte[]> Images { get; init; } = new();
        public JObject? Finish { get; init; }
        public bool IsError { get; init; }

        public static ToolResult Ok(string text) => new() { Text = text };
        public static ToolResult Error(string text) => new() { Text = "ERROR: " + text, IsError = true };
        public static ToolResult WithImages(string text, IEnumerable<byte[]> images) => new() { Text = text, Images = images.ToList() };
    }

    public sealed class AgentTool
    {
        public required string Name { get; init; }
        public required string Description { get; init; }
        public required JObject Parameters { get; init; }
        public required Func<JObject, CancellationToken, Task<ToolResult>> Handler { get; init; }

        public ToolDefinition Definition => new() { Name = Name, Description = Description, Parameters = Parameters };
    }

    /// <summary>A role's tools. The list (and its order) never changes between cycles, so it stays in the cached prefix.</summary>
    public sealed class AgentToolbox
    {
        private readonly List<AgentTool> tools = new();
        private readonly Dictionary<string, AgentTool> byName = new(StringComparer.Ordinal);

        public IReadOnlyList<AgentTool> Tools => tools;
        public IReadOnlyList<ToolDefinition> Definitions => tools.Select(t => t.Definition).ToList();

        public AgentToolbox Add(AgentTool tool)
        {
            if (byName.ContainsKey(tool.Name)) throw new InvalidOperationException("Duplicate tool " + tool.Name);
            tools.Add(tool);
            byName[tool.Name] = tool;
            return this;
        }

        public AgentToolbox Add(string name, string description, JObject parameters, Func<JObject, CancellationToken, Task<ToolResult>> handler) =>
            Add(new AgentTool { Name = name, Description = description, Parameters = parameters, Handler = handler });

        public AgentToolbox Add(string name, string description, JObject parameters, Func<JObject, ToolResult> handler) =>
            Add(name, description, parameters, (args, _) => Task.FromResult(handler(args)));

        public bool Has(string name) => byName.ContainsKey(name);

        /// <summary>A toolbox holding only the named tools (in this toolbox's order).</summary>
        public AgentToolbox Subset(params string[] names)
        {
            var box = new AgentToolbox();
            foreach (var t in tools.Where(t => names.Contains(t.Name))) box.Add(t);
            return box;
        }

        /// <summary>Adds every tool of another toolbox that this one does not already have.</summary>
        public AgentToolbox Merge(AgentToolbox other)
        {
            foreach (var t in other.Tools.Where(t => !Has(t.Name))) Add(t);
            return this;
        }

        public async Task<ToolResult> ExecuteAsync(string name, string argumentsJson, CancellationToken ct)
        {
            if (!byName.TryGetValue(name, out var tool))
                return ToolResult.Error($"there is no tool called '{name}'. Available: {string.Join(", ", tools.Select(t => t.Name))}.");
            JObject args;
            try
            {
                args = string.IsNullOrWhiteSpace(argumentsJson) ? new JObject() : JObject.Parse(argumentsJson);
            }
            catch (JsonException ex)
            {
                return ToolResult.Error($"the arguments are not valid JSON ({ex.Message}). Send one JSON object.");
            }
            foreach (var required in tool.Parameters["required"] as JArray ?? new JArray())
            {
                string key = required.ToString();
                if (args[key] == null || args[key]!.Type == JTokenType.Null)
                    return ToolResult.Error($"'{key}' is required for {name}.");
            }
            try
            {
                return await tool.Handler(args, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or KeyNotFoundException
                                           or JsonException or IOException or InvalidCastException or Roblox.RobloxApiException or Roblox.RobloxKeyMissingException)
            {
                return ToolResult.Error(ex.Message);
            }
        }
    }

    /// <summary>Tiny JSON-schema builder so tool definitions stay readable.</summary>
    public static class Schema
    {
        public static JObject Object(params (string Name, JObject Schema, bool Required)[] properties)
        {
            var props = new JObject();
            var required = new JArray();
            foreach (var (name, schema, isRequired) in properties)
            {
                props[name] = schema;
                if (isRequired) required.Add(name);
            }
            var obj = new JObject { ["type"] = "object", ["properties"] = props };
            if (required.Count > 0) obj["required"] = required;
            return obj;
        }

        public static JObject Str(string description, params string[] enumValues)
        {
            var s = new JObject { ["type"] = "string", ["description"] = description };
            if (enumValues.Length > 0) s["enum"] = new JArray(enumValues);
            return s;
        }

        public static JObject Num(string description) => new() { ["type"] = "number", ["description"] = description };
        public static JObject Int(string description) => new() { ["type"] = "integer", ["description"] = description };
        public static JObject Bool(string description) => new() { ["type"] = "boolean", ["description"] = description };
        public static JObject Arr(JObject items, string description) => new() { ["type"] = "array", ["items"] = items, ["description"] = description };
        public static JObject StrList(string description) => Arr(new JObject { ["type"] = "string" }, description);
        public static JObject Vec3(string description) => Arr(new JObject { ["type"] = "number" }, description + " — [x, y, z] in studs");
        public static JObject Free(string description) => new() { ["type"] = "object", ["description"] = description };

        public static (string, JObject, bool) Req(string name, JObject schema) => (name, schema, true);
        public static (string, JObject, bool) Opt(string name, JObject schema) => (name, schema, false);
    }

    /// <summary>Lenient readers for tool arguments (models send "3" for 3, 1 for true, and so on).</summary>
    public static class Args
    {
        public static string Str(JObject a, string key, string fallback = "") =>
            a[key] is { Type: not JTokenType.Null } t ? (t.Type == JTokenType.String ? (string)t! : t.ToString(Formatting.None)).Trim() : fallback;

        public static string Required(JObject a, string key) =>
            Str(a, key) is { Length: > 0 } s ? s : throw new ArgumentException($"'{key}' is required.");

        public static double? Num(JObject a, string key)
        {
            var t = a[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type is JTokenType.Integer or JTokenType.Float) return (double)t;
            return double.TryParse(t.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null;
        }

        public static int Int(JObject a, string key, int fallback) => Num(a, key) is double d ? (int)Math.Round(d) : fallback;

        public static bool Bool(JObject a, string key, bool fallback = false)
        {
            var t = a[key];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            if (t.Type == JTokenType.Boolean) return (bool)t;
            string s = t.ToString().Trim().ToLowerInvariant();
            return s is "true" or "1" or "yes";
        }

        public static List<string> List(JObject a, string key)
        {
            var t = a[key];
            if (t is JArray arr) return arr.Select(x => x.Type == JTokenType.String ? (string)x! : x.ToString(Formatting.None)).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (t != null && t.Type == JTokenType.String) return ((string)t!).Split(new[] { ',', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
            return new List<string>();
        }

        public static float[]? Vec3(JObject a, string key)
        {
            var t = a[key];
            if (t is JArray arr && arr.Count == 3) return arr.Select(x => (float)x).ToArray();
            if (t is JObject o && o["x"] != null) return new[] { (float)o["x"]!, (float)(o["y"] ?? 0), (float)(o["z"] ?? 0) };
            if (t != null && t.Type == JTokenType.String)
            {
                var parts = ((string)t!).Trim('[', ']', '(', ')', ' ').Split(',');
                if (parts.Length == 3 && parts.All(p => float.TryParse(p.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                    return parts.Select(p => float.Parse(p.Trim(), CultureInfo.InvariantCulture)).ToArray();
            }
            return null;
        }
    }
}
