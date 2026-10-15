using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.Projects.Containers
{
    /// <summary>
    /// Short control references. The browser helper identifies a control by a self-describing ref —
    /// base64 JSON of tab, frame, endpoint, DOM path and signature, ~200 characters each. Models copy
    /// those badly: a single wrong character reads as "the control ref is malformed", the agent
    /// re-inspects, and the loop guard eventually fires on an agent that was doing the right thing.
    ///
    /// Every ref the model sees is replaced by a short alias ("e12") remembered per desktop; every alias
    /// the model sends back is expanded before the helper sees it. An unchanged control keeps its alias
    /// across inspections, so re-inspecting a page does not renumber it.
    /// </summary>
    public static class BrowserRefAliases
    {
        private const int MaxAliasesPerDesktop = 4000;
        private static readonly Regex FullRef = new(@"kref1_[A-Za-z0-9_-]{16,}", RegexOptions.Compiled);
        private static readonly Regex Alias = new(@"^e\d{1,7}$", RegexOptions.Compiled);
        private static readonly ConcurrentDictionary<string, Table> Tables = new(StringComparer.Ordinal);

        private sealed class Table
        {
            public readonly object Gate = new();
            public readonly Dictionary<string, string> ByRef = new(StringComparer.Ordinal);
            public readonly Dictionary<string, string> ByAlias = new(StringComparer.Ordinal);
            public readonly Queue<string> Order = new();
            public int Next = 1;
        }

        internal static void ResetForTests() => Tables.Clear();

        private static Table For(string desktop) => Tables.GetOrAdd(desktop ?? "", _ => new Table());

        /// <summary>The alias for a full ref, minting one the first time it is seen.</summary>
        public static string AliasFor(string desktop, string fullRef)
        {
            var table = For(desktop);
            lock (table.Gate)
            {
                if (table.ByRef.TryGetValue(fullRef, out var existing)) return existing;
                string alias = "e" + table.Next++;
                table.ByRef[fullRef] = alias;
                table.ByAlias[alias] = fullRef;
                table.Order.Enqueue(alias);
                while (table.Order.Count > MaxAliasesPerDesktop)
                {
                    string old = table.Order.Dequeue();
                    if (table.ByAlias.Remove(old, out var oldRef)) table.ByRef.Remove(oldRef);
                }
                return alias;
            }
        }

        /// <summary>Full ref for an alias, or null when the alias is unknown on this desktop.</summary>
        public static string? Resolve(string desktop, string alias)
        {
            var table = For(desktop);
            lock (table.Gate) return table.ByAlias.TryGetValue(alias, out var full) ? full : null;
        }

        public static bool IsAlias(string? value) => value != null && Alias.IsMatch(value.Trim());

        /// <summary>Replaces every full ref in text the model is about to read with its alias.</summary>
        public static string Shorten(string desktop, string? text)
        {
            if (string.IsNullOrEmpty(text) || !text.Contains("kref1_", StringComparison.Ordinal)) return text ?? "";
            return FullRef.Replace(text, match => AliasFor(desktop, match.Value));
        }

        /// <summary>
        /// Expands aliases in a tool call's arguments: the top-level <c>ref</c> and a nested
        /// <c>trigger.ref</c>. Returns the rewritten JSON, or the original when nothing changed;
        /// <paramref name="unknown"/> names an alias that this desktop never issued.
        /// </summary>
        public static string ExpandArguments(string desktop, string? argsJson, out string? unknown)
        {
            unknown = null;
            if (string.IsNullOrWhiteSpace(argsJson) || !argsJson.Contains("\"ref\"", StringComparison.Ordinal)) return argsJson ?? "";
            JsonNode? root;
            try { root = JsonNode.Parse(argsJson); }
            catch (JsonException) { return argsJson; }
            if (root is not JsonObject obj) return argsJson;
            bool changed = false;
            string? missing = null;
            void Expand(JsonObject holder)
            {
                if (holder["ref"] is JsonValue value && value.TryGetValue<string>(out var text) && IsAlias(text))
                {
                    string? full = Resolve(desktop, text.Trim());
                    if (full == null) { missing ??= text.Trim(); return; }
                    holder["ref"] = full;
                    changed = true;
                }
            }
            Expand(obj);
            if (obj["trigger"] is JsonObject trigger) Expand(trigger);
            unknown = missing;
            return changed ? obj.ToJsonString() : argsJson;
        }
    }

    /// <summary>
    /// Renders the browser helper's control inventory as one line per control. The raw JSON spends
    /// 500–800 characters on each control (bounds, frame descriptor, a 200-character ref, a 1,400-character
    /// href) and was cut at 24,000 characters — so on a busy page the agent saw a third of it, often
    /// ending mid-object. The same information an agent acts on fits in a tenth of the space.
    /// </summary>
    public static class BrowserControlsRenderer
    {
        private static readonly HashSet<string> InformativeInputTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "password", "email", "file", "date", "datetime-local", "time", "month", "week", "tel", "number", "url", "search", "range", "color",
        };

        public static string Render(string json, string desktop)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("controls", out var controls)
                || controls.ValueKind != JsonValueKind.Array)
                return BrowserRefAliases.Shorten(desktop, json);

            var sb = new StringBuilder();
            string title = Str(root, "title");
            string url = Str(root, "url");
            sb.Append("PAGE \"").Append(Clip(title.Length > 0 ? title : "(untitled)", 120)).Append("\" — ").Append(Clip(url, 220));
            string ready = Str(root, "readyState");
            if (ready.Length > 0 && ready != "complete") sb.Append(" (still loading: ").Append(ready).Append(')');
            if (Int(root, "tabCount") is int tabCount && tabCount > 1 && Int(root, "tabIndex") is int tabIndex)
                sb.Append($" · tab {tabIndex + 1} of {tabCount}");
            sb.AppendLine();

            // Frames: the main document needs no label; every child frame gets a short one so a control
            // inside a payment or login iframe says where it lives.
            var frameLabels = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("frames", out var frames) && frames.ValueKind == JsonValueKind.Array)
            {
                int n = 0;
                var lines = new List<string>();
                foreach (var frame in frames.EnumerateArray())
                {
                    if ((Int(frame, "depth") ?? 0) == 0) continue;
                    string id = Str(frame, "id");
                    if (id.Length == 0) continue;
                    string label = "f" + ++n;
                    frameLabels[id] = label;
                    lines.Add($"{label} {Clip(Str(frame, "url"), 120)} ({Int(frame, "controlCount") ?? 0} controls)");
                }
                if (lines.Count > 0) sb.AppendLine("FRAMES: " + string.Join(" · ", lines));
            }

            int total = Int(root, "controlCount") ?? controls.GetArrayLength();
            int hidden = 0, shown = 0, offscreen = 0;
            var body = new StringBuilder();
            foreach (var control in controls.EnumerateArray())
            {
                if (control.TryGetProperty("visible", out var visible) && visible.ValueKind == JsonValueKind.False)
                {
                    hidden++;
                    continue;
                }
                bool inViewport = !(control.TryGetProperty("inViewport", out var iv) && iv.ValueKind == JsonValueKind.False);
                if (!inViewport) offscreen++;
                body.AppendLine(Line(control, desktop, frameLabels, inViewport));
                shown++;
            }

            sb.Append($"{shown} actionable control(s)");
            if (total > shown + hidden) sb.Append($" of {total} on the page (inventory capped — scroll or narrow with name/role/css)");
            if (hidden > 0) sb.Append($"; {hidden} hidden omitted");
            if (offscreen > 0) sb.Append($"; {offscreen} off-screen (scroll_into_view or op=click reaches them)");
            sb.AppendLine(". Act with ref:\"eN\" in browser op=click/fill/type/select/check/hover/focus/press or op=physical_click.");
            sb.Append(body);

            if (root.TryGetProperty("warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array && warnings.GetArrayLength() > 0)
                sb.AppendLine("WARNINGS: " + string.Join(" | ", warnings.EnumerateArray()
                    .Where(w => w.ValueKind == JsonValueKind.String).Take(5).Select(w => Clip(w.GetString() ?? "", 200))));
            return sb.ToString().TrimEnd();
        }

        private static string Line(JsonElement c, string desktop, Dictionary<string, string> frameLabels, bool inViewport)
        {
            var sb = new StringBuilder();
            string reference = Str(c, "ref");
            sb.Append('[').Append(reference.Length > 0 ? BrowserRefAliases.AliasFor(desktop, reference) : "-").Append("] ");
            string tag = Str(c, "tag");
            string role = Str(c, "role");
            string type = Str(c, "type");
            sb.Append(role.Length > 0 ? role : tag);
            if (tag == "input" && InformativeInputTypes.Contains(type) && !string.Equals(role, type, StringComparison.OrdinalIgnoreCase))
                sb.Append('[').Append(type).Append(']');

            string name = Str(c, "name");
            if (name.Length == 0) name = FirstNonEmpty(Str(c, "label"), Str(c, "placeholder"), Str(c, "testId"), Str(c, "htmlName"), Str(c, "id"));
            if (name.Length > 0) sb.Append(" \"").Append(Clip(name, 90)).Append('"');
            string text = Str(c, "text");
            if (text.Length > 0 && !Same(text, name)) sb.Append(" text=\"").Append(Clip(text, 70)).Append('"');
            string placeholder = Str(c, "placeholder");
            if (placeholder.Length > 0 && !Same(placeholder, name)) sb.Append(" placeholder=\"").Append(Clip(placeholder, 60)).Append('"');

            var flags = new List<string>();
            if (Bool(c, "disabled")) flags.Add("DISABLED");
            if (Bool(c, "focused")) flags.Add("focused");
            if (Bool(c, "required")) flags.Add("required");
            if (Bool(c, "readonly")) flags.Add("read-only");
            if (c.TryGetProperty("checked", out var check) && check.ValueKind is JsonValueKind.True or JsonValueKind.False)
                flags.Add(check.ValueKind == JsonValueKind.True ? "checked" : "unchecked");
            if (c.TryGetProperty("expanded", out var expanded) && expanded.ValueKind is JsonValueKind.True or JsonValueKind.False)
                flags.Add(expanded.ValueKind == JsonValueKind.True ? "expanded" : "collapsed");
            if (c.TryGetProperty("selected", out var selected) && selected.ValueKind == JsonValueKind.True) flags.Add("selected");
            if (Bool(c, "editable")) flags.Add(Bool(c, "hasValue") ? "filled" : "empty");
            if (type == "file")
            {
                string accept = Str(c, "accept");
                flags.Add((Int(c, "attached") ?? 0) > 0 ? $"{Int(c, "attached")} file(s) attached" : "no file");
                if (accept.Length > 0) flags.Add("accepts " + Clip(accept, 40));
            }
            if (c.TryGetProperty("frame", out var frame) && frame.ValueKind == JsonValueKind.Object
                && frameLabels.TryGetValue(Str(frame, "id"), out var frameLabel))
                flags.Add("in " + frameLabel);
            if (!inViewport) flags.Add("off-screen");
            if (flags.Count > 0) sb.Append(" (").Append(string.Join(", ", flags)).Append(')');

            string href = Str(c, "href");
            if (href.Length > 0) sb.Append(" → ").Append(Clip(href, 100));

            if (c.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array && options.GetArrayLength() > 0)
            {
                var items = options.EnumerateArray().ToList();
                var rendered = items.Take(8).Select(o => Clip(Str(o, "text"), 40) + (Bool(o, "selected") ? "*" : ""));
                sb.Append(" options: ").Append(string.Join(", ", rendered));
                if (items.Count > 8) sb.Append($", … (+{items.Count - 8})");
            }
            return sb.ToString();
        }

        private static bool Same(string a, string b) =>
            string.Equals(Norm(a), Norm(b), StringComparison.Ordinal) || Norm(b).Contains(Norm(a), StringComparison.Ordinal);

        private static string Norm(string value) => Regex.Replace(value ?? "", @"\s+", " ").Trim().ToLowerInvariant();

        private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => v.Length > 0) ?? "";

        private static string Str(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? (value.GetString() ?? "").Trim() : "";

        private static int? Int(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt32(out int number)
                ? number : null;

        private static bool Bool(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

        private static string Clip(string text, int max)
        {
            text = Regex.Replace(text ?? "", @"\s+", " ").Trim();
            return text.Length <= max ? text : text[..(max - 1)] + "…";
        }
    }
}
