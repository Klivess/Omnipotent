using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Omnipotent.Services.KliveAgent;

/// <summary>
/// The native read_data_file and list_data_directory tools: Omnipotent's runtime data (SavedData,
/// logs, state files) read and listed without a C# script.
///
/// read_file only reaches the source tree, so every look at a state file, a journal or a log meant an
/// execute_csharp script: a compile first (4.5 s for the first script of a run, 132 ms after), and one
/// more thing that can fail to compile. These two tools do the common case directly. Paths start at a
/// GlobalPaths key ("KliveAgentDirectory/ToolSpeed.json") or a path under the app folder, and nothing
/// outside the app folder is reachable.
///
/// Files that hold credentials (settings, the account registry, vaults, keys, tokens, certificates)
/// are listed but never read, and everything read is passed through the secret guard by the caller.
/// </summary>
internal static class KliveAgentDataFiles
{
    internal const int DefaultLines = 200;
    internal const int MaxLines = 1_000;
    internal const int MaxLineChars = 500;
    internal const int MaxOutputChars = 40_000;
    internal const int TailWindowBytes = 1 << 20;
    internal static readonly TimeSpan ListBudget = TimeSpan.FromSeconds(5);

    /// <summary>Folders whose files are credentials or protect them.</summary>
    private static readonly HashSet<string> ProtectedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "OmniGlobalSettings", "AccountRegistry", "Vaults", "ACMECerts", "Accounts",
    };

    private static readonly Regex ProtectedName = new(
        @"token|api[-_]?key|password|passwd|secret|credential|cookie|authstate|vault|encrypted|\.(key|pem|pfx|p12|crt|cer|jks)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static string AppDirectory => Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);

    /// <summary>
    /// A data path as an absolute path inside <paramref name="appDirectory"/>. The first segment may be a
    /// GlobalPaths key ("MemeScraperReelsDataDirectory/2026-11"); otherwise the path is taken relative to
    /// the app folder ("SavedData/KliveAgent"). An absolute path is accepted only inside the app folder.
    /// </summary>
    internal static (string? full, string? error) Resolve(string? pathOrKey, string? appDirectory = null,
        Func<string, string?>? keyLookup = null)
    {
        string app = Path.GetFullPath(appDirectory ?? AppDirectory);
        keyLookup ??= ScriptGlobals.ResolveGlobalPathKey;
        string input = (pathOrKey ?? "").Trim().Replace('\\', '/');
        if (input.Length == 0) input = "SavedData";

        string candidate;
        if (Path.IsPathRooted(input))
        {
            candidate = input;
        }
        else
        {
            int slash = input.IndexOf('/');
            string first = slash < 0 ? input : input[..slash];
            string rest = slash < 0 ? "" : input[(slash + 1)..];
            string? keyed = keyLookup(first);
            candidate = keyed != null
                ? Path.Combine(Path.IsPathRooted(keyed) ? keyed : Path.Combine(app, keyed), rest)
                : Path.Combine(app, input);
        }

        string full = Path.GetFullPath(candidate);
        string appWithSeparator = app.EndsWith(Path.DirectorySeparatorChar) ? app : app + Path.DirectorySeparatorChar;
        if (!full.Equals(app, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(appWithSeparator, StringComparison.OrdinalIgnoreCase))
            return (null, $"'{pathOrKey}' is outside Omnipotent's data. Paths start at a GlobalPaths key or at the app folder (e.g. \"SavedData/KliveAgent\").");
        return (full, null);
    }

    /// <summary>True for a file that holds credentials or sits in a folder that does.</summary>
    internal static bool IsProtected(string fullPath, string? appDirectory = null)
    {
        string app = Path.GetFullPath(appDirectory ?? AppDirectory);
        string relative = Path.GetRelativePath(app, fullPath).Replace('\\', '/');
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Take(Math.Max(0, segments.Length - 1)).Any(ProtectedFolders.Contains)) return true;
        return segments.Length > 0 && ProtectedName.IsMatch(segments[^1]);
    }

    internal static string Read(string? pathOrKey, int startLine, int maxLines, bool fromEnd, string? appDirectory = null,
        Func<string, string?>? keyLookup = null)
    {
        var (full, error) = Resolve(pathOrKey, appDirectory, keyLookup);
        if (full == null) return "Error: " + error;
        if (Directory.Exists(full)) return $"'{pathOrKey}' is a folder: list_data_directory shows what is in it.";
        if (!File.Exists(full)) return $"File not found: {pathOrKey}. list_data_directory shows what is there.";
        if (IsProtected(full, appDirectory))
            return $"'{pathOrKey}' holds credentials, so it is not readable here. Use the account registry ({{account:service/field}} references) or the vault instead.";

        maxLines = Math.Clamp(maxLines <= 0 ? DefaultLines : maxLines, 1, MaxLines);
        startLine = Math.Max(1, startLine);
        long size;
        try { size = new FileInfo(full).Length; }
        catch (Exception ex) { return $"Error: cannot read {pathOrKey}: {ex.Message}"; }

        try
        {
            if (LooksBinary(full))
                return $"{pathOrKey} is a binary file ({KliveAgentCodeSearch.FormatSize(size)}), so it is not shown as text.";

            var sb = new StringBuilder();
            if (fromEnd)
            {
                var (lines, whole) = ReadTail(full, size, maxLines);
                sb.AppendLine(whole
                    ? $"File: {pathOrKey} ({KliveAgentCodeSearch.FormatSize(size)}), last {lines.Count} line(s):"
                    : $"File: {pathOrKey} ({KliveAgentCodeSearch.FormatSize(size)}), last {lines.Count} line(s) of the final {KliveAgentCodeSearch.FormatSize(TailWindowBytes)}:");
                foreach (var line in lines) if (!AppendCapped(sb, Clip(line))) break;
                return sb.ToString().TrimEnd();
            }

            int lineNumber = 0, shown = 0;
            bool more = false;
            var body = new StringBuilder();
            foreach (var line in File.ReadLines(full))
            {
                lineNumber++;
                if (lineNumber < startLine) continue;
                if (shown >= maxLines) { more = true; break; }
                if (!AppendCapped(body, $"{lineNumber,6} | {Clip(line)}")) { more = true; break; }
                shown++;
            }
            if (shown == 0)
                return lineNumber == 0 ? $"{pathOrKey} is empty." : $"{pathOrKey} has {lineNumber} line(s); startLine {startLine} is past the end.";
            int last = startLine + shown - 1;
            sb.AppendLine($"File: {pathOrKey} ({KliveAgentCodeSearch.FormatSize(size)}), lines {startLine}-{last}:");
            sb.Append(body);
            if (more) sb.AppendLine($"… more below: startLine {last + 1} continues, or fromEnd:true for the newest lines.");
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"Error: cannot read {pathOrKey}: {ex.Message}";
        }
    }

    internal static string List(string? pathOrKey, string? pattern, bool recursive, int maxResults, string? appDirectory = null,
        Func<string, string?>? keyLookup = null, CancellationToken ct = default)
    {
        var (full, error) = Resolve(pathOrKey, appDirectory, keyLookup);
        if (full == null) return "Error: " + error;
        if (File.Exists(full)) return $"'{pathOrKey}' is a file: read_data_file reads it.";
        if (!Directory.Exists(full)) return $"Folder not found: {pathOrKey}.";

        maxResults = Math.Clamp(maxResults <= 0 ? 100 : maxResults, 1, 500);
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = recursive,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        var timer = Stopwatch.StartNew();
        var entries = new List<FileSystemInfo>();
        bool outOfTime = false;
        const int collectLimit = 20_000;
        try
        {
            foreach (var entry in new DirectoryInfo(full).EnumerateFileSystemInfos(string.IsNullOrWhiteSpace(pattern) ? "*" : pattern!, options))
            {
                ct.ThrowIfCancellationRequested();
                if (timer.Elapsed > ListBudget) { outOfTime = true; break; }
                entries.Add(entry);
                if (entries.Count >= collectLimit) break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return "list_data_directory was stopped.";
        }
        catch (Exception ex)
        {
            return $"Error: cannot list {pathOrKey}: {ex.Message}";
        }

        string label = string.IsNullOrWhiteSpace(pathOrKey) ? "SavedData" : pathOrKey!;
        if (entries.Count == 0) return $"{label} is empty{(string.IsNullOrWhiteSpace(pattern) ? "" : $" (nothing matches '{pattern}')")}.";

        int folders = entries.Count(e => e is DirectoryInfo);
        var sb = new StringBuilder($"{label}: {entries.Count}{(entries.Count >= collectLimit || outOfTime ? "+" : "")} entries ({folders} folders, {entries.Count - folders} files), newest first:\n");
        foreach (var entry in entries.OrderByDescending(e => SafeModified(e)).Take(maxResults))
        {
            string rel = Path.GetRelativePath(full, entry.FullName).Replace('\\', '/');
            string when = SafeModified(entry).ToString("yyyy-MM-dd HH:mm");
            if (entry is DirectoryInfo)
                sb.AppendLine($"[DIR] {rel}/ · {when}");
            else
                sb.AppendLine($"{rel} · {KliveAgentCodeSearch.FormatSize(((FileInfo)entry).Length)} · {when}"
                    + (IsProtected(entry.FullName, appDirectory) ? " · credentials, not readable" : ""));
        }
        if (entries.Count > maxResults) sb.AppendLine($"… and {entries.Count - maxResults} more (narrow with pattern, or raise maxResults)");
        if (outOfTime) sb.AppendLine($"[stopped counting after {ListBudget.TotalSeconds:0}s]");
        return sb.ToString().TrimEnd();
    }

    private static DateTime SafeModified(FileSystemInfo entry)
    {
        try { return entry.LastWriteTimeUtc; } catch { return DateTime.MinValue; }
    }

    private static bool LooksBinary(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[8192];
        int read = stream.Read(buffer, 0, buffer.Length);
        for (int i = 0; i < read; i++) if (buffer[i] == 0) return true;
        return false;
    }

    /// <summary>The last lines of a file, reading at most the final megabyte so a large journal costs the
    /// same as a small one.</summary>
    private static (List<string> lines, bool whole) ReadTail(string path, long size, int maxLines)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, size - TailWindowBytes);
        stream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[size - start];
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer, read, buffer.Length - read);
            if (n <= 0) break;
            read += n;
        }
        var text = Encoding.UTF8.GetString(buffer, 0, read);
        var all = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (start > 0 && all.Count > 0) all.RemoveAt(0);        // the first line was cut by the window
        if (all.Count > 0 && all[^1].Length == 0) all.RemoveAt(all.Count - 1);
        return (all.Skip(Math.Max(0, all.Count - maxLines)).ToList(), start == 0);
    }

    private static bool AppendCapped(StringBuilder sb, string line)
    {
        if (sb.Length + line.Length + 2 > MaxOutputChars) return false;
        sb.AppendLine(line);
        return true;
    }

    private static string Clip(string line) => line.Length <= MaxLineChars ? line : line[..MaxLineChars] + "…";
}
