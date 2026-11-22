using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.KliveAgent;

/// <summary>
/// The native grep and find_files tools: content and file-name search over the codebase with no compile
/// step, bounded so neither can hold a run.
///
/// The grep tool used to call the script helper SearchCodeRegex, which walked every directory under the
/// root (bin, obj, node_modules and the .claude worktrees included, each a whole copy of the repository),
/// matched with no regex timeout, and had no time limit. A file list came back only as matching lines, so
/// "where is X used" cost a page of lines, and other file types needed a script. This keeps the same
/// "path:line: text" output and adds what an agent searching code needs: a files-only and a count mode,
/// context lines, a file glob, case folding, a 250 ms limit per regex match and a 15 s limit per search.
/// The script helpers are unchanged.
/// </summary>
internal static class KliveAgentCodeSearch
{
    internal static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan RegexMatchLimit = TimeSpan.FromMilliseconds(250);
    internal const long MaxFileBytes = 2_000_000;
    internal const int MaxLineChars = 300;

    internal sealed record GrepOptions(
        string Pattern,
        string? Path = null,
        string? Glob = null,
        string OutputMode = "content",
        int Context = 0,
        bool IgnoreCase = false,
        bool FixedString = false,
        int MaxResults = 30);

    internal static string Grep(string root, GrepOptions o, TimeSpan budget, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(o.Pattern)) return "Error: 'pattern' is required.";
        var (scope, scopeError) = ResolveScope(root, o.Path);
        if (scope == null) return scopeError!;

        Regex regex;
        try
        {
            regex = new Regex(o.FixedString ? Regex.Escape(o.Pattern) : o.Pattern,
                (o.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant, RegexMatchLimit);
        }
        catch (ArgumentException ex)
        {
            return $"Error: '{o.Pattern}' is not a valid regex ({ex.Message}). Set fixedString:true to search for it literally.";
        }

        string mode = (o.OutputMode ?? "content").Trim().ToLowerInvariant() switch
        {
            "files" or "files_with_matches" or "filesonly" or "paths" => "files",
            "count" or "counts" => "count",
            _ => "content",
        };
        int context = Math.Clamp(o.Context, 0, 5);
        int maxResults = Math.Clamp(o.MaxResults <= 0 ? 30 : o.MaxResults, 1, 500);
        var fileFilter = BuildFileFilter(o.Glob);

        var timer = Stopwatch.StartNew();
        var content = new StringBuilder();
        var fileCounts = new List<(string rel, int count)>();
        int matches = 0, filesSearched = 0;
        bool capped = false, outOfTime = false;

        try
        {
            foreach (var file in WalkFiles(scope, ct))
            {
                if (timer.Elapsed > budget) { outOfTime = true; break; }
                string rel = Relative(root, file);
                string relFromScope = Directory.Exists(scope) ? Relative(scope, file) : System.IO.Path.GetFileName(file);
                if (!fileFilter(relFromScope)) continue;
                if (!ScriptGlobals.IsAllowedSourceFile(file)) continue;
                try { if (new FileInfo(file).Length > MaxFileBytes) continue; } catch { continue; }

                string[] lines;
                try { lines = File.ReadAllLines(file); } catch { continue; }
                filesSearched++;

                var hits = new List<int>();
                for (int i = 0; i < lines.Length; i++)
                {
                    try
                    {
                        if (regex.IsMatch(lines[i])) hits.Add(i);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        return $"Error: the pattern took over {RegexMatchLimit.TotalMilliseconds:0} ms on one line ({rel}:{i + 1}), which means it backtracks badly. "
                            + "Simplify it, anchor it, or set fixedString:true.";
                    }
                }
                if (hits.Count == 0) continue;

                if (mode == "content")
                {
                    int remaining = maxResults - matches;
                    if (remaining <= 0) { capped = true; break; }
                    if (hits.Count > remaining) { hits = hits.Take(remaining).ToList(); capped = true; }
                    AppendContent(content, rel, lines, hits, context);
                    matches += hits.Count;
                    if (capped) break;
                }
                else
                {
                    matches += hits.Count;
                    fileCounts.Add((rel, hits.Count));
                    if (mode == "files" && fileCounts.Count >= maxResults) { capped = true; break; }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return "grep was stopped.";
        }

        string where = string.IsNullOrWhiteSpace(o.Path) ? "the codebase" : o.Path!;
        string filter = string.IsNullOrWhiteSpace(o.Glob) ? " (*.cs files; set glob for others)" : $" ({o.Glob})";
        string tail = outOfTime
            ? $"\n[stopped after {budget.TotalSeconds:0}s having searched {filesSearched} files: narrow it with path or glob]"
            : "";

        if (matches == 0)
            return $"No matches for '{o.Pattern}' in {where}{filter}.{tail}";

        var sb = new StringBuilder();
        switch (mode)
        {
            case "files":
                sb.AppendLine($"{fileCounts.Count} file(s) match '{o.Pattern}' in {where}:");
                foreach (var (rel, count) in fileCounts) sb.AppendLine($"{rel} ({count})");
                if (capped) sb.AppendLine($"[stopped at {maxResults} files]");
                break;
            case "count":
                sb.AppendLine($"{matches} match(es) in {fileCounts.Count} file(s) for '{o.Pattern}' in {where}.");
                foreach (var (rel, count) in fileCounts.OrderByDescending(f => f.count).Take(25)) sb.AppendLine($"{rel}: {count}");
                if (fileCounts.Count > 25) sb.AppendLine($"… and {fileCounts.Count - 25} more files");
                break;
            default:
                sb.Append(content);
                if (capped) sb.AppendLine($"[truncated at {maxResults} results]");
                break;
        }
        return (sb.ToString().TrimEnd() + tail).TrimEnd();
    }

    /// <summary>Matching lines as path:line: text, context lines as path-line- text, groups apart by "--".</summary>
    private static void AppendContent(StringBuilder sb, string rel, string[] lines, List<int> hits, int context)
    {
        if (context == 0)
        {
            foreach (int i in hits) sb.Append(rel).Append(':').Append(i + 1).Append(": ").AppendLine(Clip(lines[i].Trim()));
            return;
        }

        var hitSet = new HashSet<int>(hits);
        int groupEnd = -2;
        foreach (int hit in hits)
        {
            int from = Math.Max(0, hit - context), to = Math.Min(lines.Length - 1, hit + context);
            if (from > groupEnd + 1 && sb.Length > 0) sb.AppendLine("--");
            for (int i = Math.Max(from, groupEnd + 1); i <= to; i++)
            {
                bool isHit = hitSet.Contains(i);
                sb.Append(rel).Append(isHit ? ':' : '-').Append(i + 1).Append(isHit ? ": " : "- ").AppendLine(Clip(lines[i].TrimEnd()));
            }
            groupEnd = Math.Max(groupEnd, to);
        }
    }

    private static string Clip(string line) => line.Length <= MaxLineChars ? line : line[..MaxLineChars] + "…";

    /// <summary>Files whose name (or, for a glob with a slash, whose path below the search folder) matches,
    /// newest first.</summary>
    internal static string FindFiles(string root, string pattern, string? path, int maxResults, TimeSpan budget, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return "Error: 'pattern' is required (e.g. \"*Routes*.cs\" or \"**/Talk/*.cs\").";
        var (scope, scopeError) = ResolveScope(root, path);
        if (scope == null) return scopeError!;
        if (!Directory.Exists(scope)) return $"'{path}' is a file, not a folder.";

        var matcher = BuildFileFilter(pattern);
        maxResults = Math.Clamp(maxResults <= 0 ? 50 : maxResults, 1, 200);
        var timer = Stopwatch.StartNew();
        var found = new List<(string rel, long size, DateTime modified)>();
        bool outOfTime = false;
        const int collectLimit = 5_000;
        try
        {
            foreach (var file in WalkFiles(scope, ct))
            {
                if (timer.Elapsed > budget) { outOfTime = true; break; }
                if (!matcher(Relative(scope, file))) continue;
                try
                {
                    var info = new FileInfo(file);
                    found.Add((Relative(root, file), info.Length, info.LastWriteTimeUtc));
                }
                catch { }
                if (found.Count >= collectLimit) break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return "find_files was stopped.";
        }

        string where = string.IsNullOrWhiteSpace(path) ? "the codebase" : path!;
        if (found.Count == 0)
            return $"No files match '{pattern}' in {where}." + (outOfTime ? $" [stopped after {budget.TotalSeconds:0}s: narrow it with path]" : "");

        var sb = new StringBuilder($"{found.Count}{(found.Count >= collectLimit ? "+" : "")} file(s) match '{pattern}' in {where}, newest first:\n");
        foreach (var f in found.OrderByDescending(f => f.modified).Take(maxResults))
            sb.AppendLine($"{f.rel} · {FormatSize(f.size)} · {f.modified:yyyy-MM-dd}");
        if (found.Count > maxResults) sb.AppendLine($"… and {found.Count - maxResults} more (raise maxResults or narrow the pattern)");
        if (outOfTime) sb.AppendLine($"[stopped after {budget.TotalSeconds:0}s: there may be more; narrow it with path]");
        return sb.ToString().TrimEnd();
    }

    internal static string FormatSize(long bytes) =>
        bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / (1024.0 * 1024):0.#} MB";

    // ── Shared ──

    /// <summary>A file glob as a predicate over a '/'-separated path. A glob without a slash matches the
    /// file name only ("*.cs" finds .cs files at any depth); with one, it matches the path below the
    /// search folder ("Talk/*.cs", "**/Routes/*.cs"). No glob means "*.cs".</summary>
    internal static Func<string, bool> BuildFileFilter(string? glob)
    {
        string g = string.IsNullOrWhiteSpace(glob) ? "*.cs" : glob.Trim().Replace('\\', '/');
        if (g.StartsWith("./", StringComparison.Ordinal)) g = g[2..];
        var regex = GlobToRegex(g);
        bool byName = !g.Contains('/');
        return relative =>
        {
            string p = relative.Replace('\\', '/');
            return regex.IsMatch(byName ? p[(p.LastIndexOf('/') + 1)..] : p);
        };
    }

    internal static Regex GlobToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        i++;
                        if (i + 1 < glob.Length && glob[i + 1] == '/') { i++; sb.Append("(?:.*/)?"); }
                        else sb.Append(".*");
                    }
                    else sb.Append("[^/]*");
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                case '{':
                    int close = glob.IndexOf('}', i);
                    if (close > i)
                    {
                        var alternatives = glob[(i + 1)..close].Split(',').Select(a => Regex.Escape(a.Trim()));
                        sb.Append("(?:").Append(string.Join("|", alternatives)).Append(')');
                        i = close;
                    }
                    else sb.Append("\\{");
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>The folder or file a search covers, inside the root (a path that climbs out is refused).</summary>
    internal static (string? scope, string? error) ResolveScope(string root, string? path)
    {
        string fullRoot = System.IO.Path.GetFullPath(root);
        if (string.IsNullOrWhiteSpace(path)) return (fullRoot, null);
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(fullRoot, path.Trim().TrimStart('/', '\\')));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) return (null, $"'{path}' is outside the codebase.");
        if (!Directory.Exists(full) && !File.Exists(full)) return (null, $"Path not found: {path}. find_files or list_directory shows what is there.");
        return (full, null);
    }

    /// <summary>Every file below a folder (or the file itself), skipping build output, dependencies,
    /// version control and the .claude worktrees: the directories KliveAgentCodebaseIndex skips.</summary>
    internal static IEnumerable<string> WalkFiles(string scope, CancellationToken ct)
    {
        if (File.Exists(scope)) { yield return scope; yield break; }
        var pending = new Stack<string>();
        pending.Push(scope);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            string[] files, children;
            try
            {
                files = Directory.GetFiles(directory, "*", options);
                children = Directory.GetDirectories(directory, "*", options);
            }
            catch { continue; }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (var file in files) yield return file;
            Array.Sort(children, StringComparer.OrdinalIgnoreCase);
            for (int i = children.Length - 1; i >= 0; i--)
                if (!KliveAgentCodebaseIndex.IgnoredSourceDirectories.Contains(System.IO.Path.GetFileName(children[i])))
                    pending.Push(children[i]);
        }
    }

    private static string Relative(string root, string file) => System.IO.Path.GetRelativePath(root, file).Replace('\\', '/');
}
