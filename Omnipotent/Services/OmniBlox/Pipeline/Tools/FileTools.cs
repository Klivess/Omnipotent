using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Omnipotent.Services.OmniBlox.Agents;
using Omnipotent.Services.OmniBlox.Build;
using static Omnipotent.Services.OmniBlox.Agents.Schema;

namespace Omnipotent.Services.OmniBlox.Pipeline.Tools
{
    /// <summary>
    /// The builder's file tools. Large files are written in parts (write_file, then append_file) and changed with
    /// exact replacements (edit_file), never by rewriting them whole: a reply that hits the output limit can no
    /// longer leave a file cut off half-way. Every write is syntax-checked at once and type-checked against the
    /// rest of the game and the kit, so a wrong API call is caught when it is written, not in a playtest.
    /// </summary>
    public static class FileTools
    {
        public const int MaxReadLines = 400;

        public static void Add(AgentToolbox box, StudioContext ctx, string gameId)
        {
            var ws = ctx.Workspace(gameId);

            box.Add("list_files", "List the game's source files with their sizes and line counts (game.json, scene.json, design.md, src/...).", Object(), _ =>
            {
                var files = ws.ListFiles();
                if (files.Count == 0) return ToolResult.Ok("(empty)");
                var sb = new StringBuilder();
                foreach (var f in files)
                {
                    string text = ws.ReadFile(f.Path) ?? "";
                    sb.AppendLine($"{f.Path} ({f.Bytes} bytes, {LuauSyntax.CountLines(text)} lines)");
                }
                return ToolResult.Ok(sb.ToString());
            });

            box.Add("read_file", $"Read a file with line numbers, optionally a line range (at most {MaxReadLines} lines at a time). Kit files can be read as GameKit/<path> (e.g. GameKit/Server/Currency.luau).",
                Object(Req("path", Str("e.g. src/server/systems/Forest.luau, or GameKit/Server/Shop.luau")), Opt("start_line", Int("First line (1-based)")), Opt("end_line", Int("Last line"))),
                args =>
                {
                    string path = Args.Required(args, "path");
                    string? text = path.StartsWith("GameKit/", StringComparison.OrdinalIgnoreCase)
                        ? GameKit.Files.FirstOrDefault(k => ("GameKit/" + k.Path).Equals(path, StringComparison.OrdinalIgnoreCase))?.Source
                        : ws.ReadFile(path);
                    if (text == null) return ToolResult.Error("no such file" + (path.StartsWith("GameKit/", StringComparison.OrdinalIgnoreCase) ? " (kit files: " + string.Join(", ", GameKit.Files.Select(k => "GameKit/" + k.Path)) + ")" : ""));
                    var lines = text.Split('\n');
                    int start = Math.Max(1, Args.Int(args, "start_line", 1));
                    int end = Math.Min(lines.Length, Args.Int(args, "end_line", Math.Min(lines.Length, start + MaxReadLines - 1)));
                    end = Math.Min(end, start + MaxReadLines - 1);
                    var sb = new StringBuilder();
                    for (int i = start; i <= end; i++) sb.Append(i.ToString(CultureInfo.InvariantCulture).PadLeft(4)).Append("  ").AppendLine(lines[i - 1].TrimEnd('\r'));
                    if (end < lines.Length) sb.AppendLine($"… {lines.Length - end} more lines (read on with start_line {end + 1})");
                    return ToolResult.Ok(sb.ToString());
                });

            box.Add("write_file", "Create or replace a file. Luau goes under src/: src/server/systems/*.luau (game systems: a module with Start(), started by the kit), src/server/*.server.luau (Scripts), src/client/controllers/*.luau (client modules with Start(hud)), src/client/*.client.luau (LocalScripts), src/shared/*.luau (modules for both), src/tests/*.luau (specs); design.md for the design. Keep each call under ~250 lines: write a long file as write_file (first part) then append_file. Change an existing file with edit_file instead of rewriting it. Luau is syntax- and type-checked at once.",
                Object(Req("path", Str("e.g. src/server/systems/Forest.luau")), Req("content", Str("The file's content (or its first part)"))),
                async (args, ct) =>
                {
                    string path = Args.Required(args, "path").Replace('\\', '/');
                    if (!IsWritable(path, out string? why)) return ToolResult.Error(why!);
                    string content = args["content"]?.ToString() ?? "";
                    ws.WriteFile(path, content);
                    return ToolResult.Ok($"Wrote {path} ({LuauSyntax.CountLines(content)} lines)." + await CheckAsync(ctx, gameId, path, content, ct));
                });

            box.Add("append_file", "Append text to the end of a file (to write a long file in parts). Checks the whole file afterwards.",
                Object(Req("path", Str("An existing file")), Req("content", Str("Text to add at the end"))),
                async (args, ct) =>
                {
                    string path = Args.Required(args, "path").Replace('\\', '/');
                    if (!IsWritable(path, out string? why)) return ToolResult.Error(why!);
                    string? current = ws.ReadFile(path);
                    if (current == null) return ToolResult.Error("no such file; create it with write_file first");
                    string addition = args["content"]?.ToString() ?? "";
                    string joined = current.Length == 0 || current.EndsWith('\n') ? current + addition : current + "\n" + addition;
                    ws.WriteFile(path, joined);
                    return ToolResult.Ok($"Appended {LuauSyntax.CountLines(addition)} lines to {path} (now {LuauSyntax.CountLines(joined)} lines)." + await CheckAsync(ctx, gameId, path, joined, ct));
                });

            box.Add("edit_file", "Change part of a file by exact replacement: each edit's old text must appear exactly once (copy it from read_file, without the line numbers), unless all is true. Edits apply in order; nothing is written if any edit fails. Prefer this to rewriting a file.",
                Object(Req("path", Str("The file")), Req("edits", Arr(Object(Req("old", Str("Exact text to find")), Req("new", Str("Replacement text")), Opt("all", Bool("Replace every occurrence"))), "Replacements"))),
                async (args, ct) =>
                {
                    string path = Args.Required(args, "path").Replace('\\', '/');
                    if (!IsWritable(path, out string? why)) return ToolResult.Error(why!);
                    string? text = ws.ReadFile(path);
                    if (text == null) return ToolResult.Error("no such file");
                    var edits = (args["edits"] as JArray ?? new JArray()).OfType<JObject>().ToList();
                    if (edits.Count == 0) return ToolResult.Error("give at least one edit");
                    string working = text.Replace("\r\n", "\n");
                    var changedLines = new List<int>();
                    for (int i = 0; i < edits.Count; i++)
                    {
                        string old = (edits[i]["old"]?.ToString() ?? "").Replace("\r\n", "\n");
                        string replacement = (edits[i]["new"]?.ToString() ?? "").Replace("\r\n", "\n");
                        bool all = Args.Bool(edits[i], "all");
                        if (old.Length == 0) return ToolResult.Error($"edit {i + 1}: 'old' is empty");
                        int count = Count(working, old);
                        if (count == 0)
                        {
                            string hint = Nearest(working, old);
                            return ToolResult.Error($"edit {i + 1}: the old text was not found in {path}; nothing was changed." + (hint.Length > 0 ? " The closest line is: " + hint : " Read the file again and copy the text exactly."));
                        }
                        if (count > 1 && !all) return ToolResult.Error($"edit {i + 1}: the old text appears {count} times in {path}; include more surrounding lines to make it unique, or set all: true. Nothing was changed.");
                        int at = working.IndexOf(old, StringComparison.Ordinal);
                        changedLines.Add(working[..at].Count(c => c == '\n') + 1);
                        working = all ? working.Replace(old, replacement, StringComparison.Ordinal) : working[..at] + replacement + working[(at + old.Length)..];
                    }
                    ws.WriteFile(path, working);
                    return ToolResult.Ok($"Edited {path}: {edits.Count} change(s) at line(s) {string.Join(", ", changedLines)} (now {LuauSyntax.CountLines(working)} lines)." + await CheckAsync(ctx, gameId, path, working, ct));
                });

            box.Add("delete_file", "Delete a file.", Object(Req("path", Str("File path"))), args =>
                ws.DeleteFile(Args.Required(args, "path")) ? ToolResult.Ok("Deleted.") : ToolResult.Error("no such file"));

            box.Add("search_code", "Search the game's files (and optionally the Game Kit) for text or a Lua pattern-like regex. Returns file:line: text.",
                Object(Req("query", Str("Text or a regular expression")), Opt("regex", Bool("Treat query as a regular expression")), Opt("include_kit", Bool("Search the Game Kit too")), Opt("limit", Int("Max matches (default 40)"))),
                args =>
                {
                    string query = Args.Required(args, "query");
                    Regex? regex = null;
                    if (Args.Bool(args, "regex"))
                    {
                        try { regex = new Regex(query, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)); }
                        catch (ArgumentException ex) { return ToolResult.Error("bad regex: " + ex.Message); }
                    }
                    int limit = Math.Clamp(Args.Int(args, "limit", 40), 1, 200);
                    var sources = ws.ListFiles().Select(f => (f.Path, Text: ws.ReadFile(f.Path) ?? "")).ToList();
                    if (Args.Bool(args, "include_kit")) sources.AddRange(GameKit.Files.Select(k => ("GameKit/" + k.Path, k.Source)));
                    var sb = new StringBuilder();
                    int found = 0;
                    foreach (var (path, text) in sources)
                    {
                        var lines = text.Split('\n');
                        for (int i = 0; i < lines.Length && found < limit; i++)
                        {
                            bool hit = regex != null ? regex.IsMatch(lines[i]) : lines[i].Contains(query, StringComparison.OrdinalIgnoreCase);
                            if (!hit) continue;
                            found++;
                            string line = lines[i].Trim();
                            sb.AppendLine($"{path}:{i + 1}: {(line.Length > 200 ? line[..200] + "…" : line)}");
                        }
                    }
                    return ToolResult.Ok(found == 0 ? "No matches." : sb.ToString() + (found >= limit ? $"(stopped at {limit} matches)" : ""));
                });
        }

        private static bool IsWritable(string path, out string? why)
        {
            why = null;
            if (path.Equals("game.json", StringComparison.OrdinalIgnoreCase)) { why = "edit game.json with update_manifest"; return false; }
            if (path.Equals("scene.json", StringComparison.OrdinalIgnoreCase)) { why = "change the world with the scene tools (add_objects, place_asset, terrain_*, plan_map…)"; return false; }
            if (path.StartsWith("src/", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return true;
            why = "scripts live under src/ (server/, server/systems/, client/, client/controllers/, shared/, character/, gui/ or tests/); notes go in design.md";
            return false;
        }

        private static int Count(string text, string part)
        {
            int count = 0, at = 0;
            while ((at = text.IndexOf(part, at, StringComparison.Ordinal)) >= 0) { count++; at += part.Length; }
            return count;
        }

        /// <summary>The file line most like the first line of the text that was not found (the usual cause: whitespace or a stale copy).</summary>
        private static string Nearest(string text, string old)
        {
            string first = old.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 3) ?? "";
            if (first.Length == 0) return "";
            var lines = text.Split('\n');
            int bestLine = -1, bestScore = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i].Trim();
                if (l.Length == 0) continue;
                int score = CommonPrefix(l, first) * 2 + (l.Contains(first[..Math.Min(first.Length, 12)], StringComparison.Ordinal) ? 10 : 0);
                if (score > bestScore) { bestScore = score; bestLine = i; }
            }
            return bestLine >= 0 && bestScore >= 8 ? $"line {bestLine + 1}: {lines[bestLine].Trim()}" : "";
        }

        private static int CommonPrefix(string a, string b)
        {
            int n = Math.Min(a.Length, b.Length), i = 0;
            while (i < n && a[i] == b[i]) i++;
            return i;
        }

        /// <summary>Syntax issues of the file, then its type-check findings in the context of the whole game.</summary>
        internal static async Task<string> CheckAsync(StudioContext ctx, string gameId, string path, string content, CancellationToken ct)
        {
            if (!path.EndsWith(".luau", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) return "";
            var issues = LuauSyntax.Check(content).Concat(LuauSyntax.Lint(content)).ToList();
            var sb = new StringBuilder();
            if (issues.Count > 0)
            {
                sb.Append('\n').Append(LuauSyntax.Format(issues.Take(10), path));
                if (issues.Any(i => i.IsError)) return sb.ToString(); // types are meaningless until it parses
            }
            else sb.Append(" Syntax OK.");
            if (ctx.Luau == null) return sb.ToString();
            var compiled = ctx.Compile(gameId);
            if (!compiled.Ok && compiled.Scripts.Count == 0) return sb.ToString();
            var analysis = await LuauAnalyzer.AnalyzeAsync(ctx.Luau, compiled.Scripts, ct);
            if (!analysis.Ran) return sb.Append($" (Not type-checked: {analysis.Problem})").ToString();
            var mine = analysis.Diagnostics.Where(d => d.File.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList();
            int elsewhere = analysis.Diagnostics.Count(d => d.IsError && !d.File.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (mine.Count == 0) sb.Append(" Types OK.");
            else
            {
                sb.Append($"\nType check of {path}: {mine.Count(d => d.IsError)} error(s), {mine.Count(d => !d.IsError)} warning(s):");
                foreach (var d in mine.Take(15)) sb.Append($"\n- {(d.IsError ? "ERROR" : "warning")} line {d.Line}:{d.Column}: {d.Message}");
            }
            if (elsewhere > 0) sb.Append($"\n({elsewhere} type error(s) in other files — build lists them all.)");
            return sb.ToString();
        }
    }
}
