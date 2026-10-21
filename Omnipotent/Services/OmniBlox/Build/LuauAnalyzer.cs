using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.OmniBlox.Build
{
    public sealed record LuauDiagnostic(string File, int Line, int Column, string Severity, string Code, string Message)
    {
        public bool IsError => Severity == "error";
        public override string ToString() => $"{File}:{Line}:{Column}: {Message}";
    }

    public sealed class LuauAnalysis
    {
        public bool Ran { get; init; }
        /// <summary>Why it did not run (no type checker installed, a timeout…).</summary>
        public string? Problem { get; init; }
        public List<LuauDiagnostic> Diagnostics { get; init; } = new();
        public TimeSpan Took { get; init; }
        public IEnumerable<LuauDiagnostic> Errors => Diagnostics.Where(d => d.IsError);
        public IEnumerable<LuauDiagnostic> Warnings => Diagnostics.Where(d => !d.IsError);
    }

    /// <summary>
    /// Type-checks a game's Luau the way Roblox's own tools would, so the mistakes that crashed the first studio's
    /// games in playtests are caught at build time: calling a method a class does not have (game:GetPlayers(),
    /// Part:GetBoundingBox()), a module function that does not exist (Currency.Give), an unknown variable, the
    /// wrong number of arguments, a property of the wrong type.
    ///
    /// Two passes over the compiled place's scripts, all in strict mode:
    ///   A. with a sourcemap of the place, so requires resolve to the real kit modules and module members are
    ///      checked;
    ///   B. without one, where Roblox's own classes keep their exact types, for method calls on them (the
    ///      sourcemap pass types the data model loosely).
    /// Only the game's own files are reported; known false positives are dropped; each finding is an error
    /// (fails the build: it would crash at runtime) or a warning (worth a look).
    /// </summary>
    public static class LuauAnalyzer
    {
        public const int MaxReported = 60;
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

        private static readonly Regex Line = new(@"^(?<path>.*?\.luau)(?: \[(?<dm>[^\]]*)\])?:(?<line>\d+):(?<col>\d+)(?:-(?<end>\d+))?: \((?<code>\w+)\) (?<kind>\w+): (?<msg>.*)$", RegexOptions.Compiled);

        private static readonly HashSet<string> IgnoredLints = new(StringComparer.Ordinal)
        {
            "LocalUnused", "FunctionUnused", "ImportUnused", "LocalShadow", "SameLineStatement", "MultiLineStatement", "PlaceholderRead", "PlaceholderReadUnchecked",
        };

        private static readonly HashSet<string> Services = new(StringComparer.Ordinal)
        {
            "Workspace", "ReplicatedStorage", "ServerScriptService", "ServerStorage", "StarterPlayer", "StarterGui", "StarterPack", "Lighting", "SoundService", "Players", "Teams",
            "StarterPlayerScripts", "StarterCharacterScripts", "ReplicatedFirst",
        };

        public static async Task<LuauAnalysis> AnalyzeAsync(LuauToolchain tools, IReadOnlyList<ScriptPlacement> scripts, CancellationToken ct)
        {
            var started = Stopwatch.StartNew();
            if (!await tools.EnsureInstalledAsync(ct))
                return new LuauAnalysis { Ran = false, Problem = tools.Problem ?? "the Luau type checker is not installed" };

            string root = Path.Combine(Path.GetTempPath(), "omniblox-luau-" + Guid.NewGuid().ToString("N")[..10]);
            Directory.CreateDirectory(Path.Combine(root, "s"));
            try
            {
                // Lay every script out as a file and describe the place's tree in a sourcemap.
                var byFile = new Dictionary<string, ScriptPlacement>(StringComparer.OrdinalIgnoreCase);
                var tree = new Node("Game", "DataModel");
                int index = 0;
                foreach (var s in scripts)
                {
                    string safe = Regex.Replace(s.FullName, @"[^A-Za-z0-9_.-]", "_");
                    string rel = $"s/{index++:D3}_{(safe.Length > 80 ? safe[^80..] : safe)}.luau";
                    File.WriteAllText(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)), s.Source, new UTF8Encoding(false));
                    byFile[rel] = s;
                    var at = tree;
                    for (int i = 0; i < s.DataModelPath.Count; i++)
                    {
                        string name = s.DataModelPath[i];
                        bool last = i == s.DataModelPath.Count - 1;
                        string cls = last ? s.ClassName : Services.Contains(name) && (i == 0 || name.StartsWith("Starter", StringComparison.Ordinal)) ? name : "Folder";
                        at = at.Child(name, cls);
                        if (last) at.File = rel;
                    }
                }
                File.WriteAllText(Path.Combine(root, "sourcemap.json"), tree.ToJson().ToString(Newtonsoft.Json.Formatting.None));
                File.WriteAllText(Path.Combine(root, "strict.luaurc"), "{\"languageMode\": \"strict\"}");

                var gameFiles = byFile.Where(kv => kv.Value.WorkspaceFile != null).Select(kv => kv.Key).ToList();
                if (gameFiles.Count == 0) return new LuauAnalysis { Ran = true, Took = started.Elapsed };

                string a = await RunAsync(tools, root, byFile.Keys.ToList(), withSourcemap: true, ct);
                string b = await RunAsync(tools, root, gameFiles, withSourcemap: false, ct);

                var found = new List<LuauDiagnostic>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                Collect(a, byFile, root, passB: false, found, seen);
                Collect(b, byFile, root, passB: true, found, seen);
                var ordered = found.OrderBy(d => d.IsError ? 0 : 1).ThenBy(d => d.File, StringComparer.Ordinal).ThenBy(d => d.Line).Take(MaxReported).ToList();
                return new LuauAnalysis { Ran = true, Diagnostics = ordered, Took = started.Elapsed };
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
            {
                return new LuauAnalysis { Ran = false, Problem = "the type checker failed: " + ex.Message, Took = started.Elapsed };
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { /* a temp folder; cleaned by the OS */ }
            }
        }

        private static void Collect(string output, Dictionary<string, ScriptPlacement> byFile, string root, bool passB, List<LuauDiagnostic> found, HashSet<string> seen)
        {
            LuauDiagnostic? last = null;
            foreach (string raw in output.Split('\n'))
            {
                string text = raw.TrimEnd('\r');
                var m = Line.Match(text.Trim());
                if (!m.Success)
                {
                    // Continuation lines ("caused by: …") belong to the diagnostic before them.
                    if (last != null && text.Trim().Length > 0 && found.Count > 0 && found[^1] == last && last.Message.Length < 600)
                        found[^1] = last = last with { Message = last.Message + " " + text.Trim() };
                    continue;
                }
                last = null;
                string path = m.Groups["path"].Value.Replace('\\', '/');
                int at = path.IndexOf("/s/", StringComparison.Ordinal);
                string rel = at >= 0 ? path[(at + 1)..] : path.StartsWith("s/", StringComparison.Ordinal) ? path : "";
                if (!byFile.TryGetValue(rel, out var script) || script.WorkspaceFile == null) continue;
                int line = int.Parse(m.Groups["line"].Value), col = int.Parse(m.Groups["col"].Value);
                string kind = m.Groups["kind"].Value, msg = m.Groups["msg"].Value.Trim();
                string sourceLine = SourceLine(script.Source, line);
                var severity = Classify(kind, msg, sourceLine, col, passB);
                if (severity == null) continue;
                string key = $"{script.WorkspaceFile}|{line}|{Normalize(msg)}";
                if (!seen.Add(key)) continue;
                last = new LuauDiagnostic(script.WorkspaceFile, line, col, severity, kind, Explain(kind, msg));
                found.Add(last);
            }
        }

        private static string Normalize(string message) => Regex.Replace(message, @"\s+", " ");

        private static string SourceLine(string source, int line)
        {
            var lines = source.Split('\n');
            return line >= 1 && line <= lines.Length ? lines[line - 1] : "";
        }

        /// <summary>error, warning, or null to drop it.</summary>
        internal static string? Classify(string kind, string msg, string sourceLine, int column, bool passB)
        {
            if (kind == "SyntaxError") return passB ? null : "error";
            if (kind != "TypeError")
            {
                if (IgnoredLints.Contains(kind)) return null;
                if (passB) return null;
                return kind is "UnknownGlobal" or "FormatString" ? "error" : "warning";
            }
            // The type checker does not know a failed pcall returns its error message as well.
            if (msg.StartsWith("Function only returns", StringComparison.Ordinal) && Regex.IsMatch(sourceLine, @"\bx?pcall\s*\(")) return null;

            var notFound = Regex.Match(msg, @"^Key '(?<key>[^']+)' not found in (?<what>external type|class|table) '(?<type>[^']+)'");
            if (passB)
            {
                // Without the sourcemap only Roblox's own classes are exact: keep method calls on them.
                if (!notFound.Success || notFound.Groups["what"].Value == "table") return msg.StartsWith("Unknown global", StringComparison.Ordinal) ? "error" : null;
                string key = notFound.Groups["key"].Value, type = notFound.Groups["type"].Value;
                bool method = sourceLine.Contains(":" + key, StringComparison.Ordinal);
                if (!method) return null;
                return type == "Instance" ? "warning" : "error";
            }
            if (msg.StartsWith("Unknown global", StringComparison.Ordinal)) return "error";
            if (msg.StartsWith("Unknown require", StringComparison.Ordinal)) return msg.Contains("unsupported path", StringComparison.Ordinal) ? "warning" : "error";
            if (msg.Contains("Argument count mismatch", StringComparison.Ordinal) || msg.Contains("arguments, but", StringComparison.Ordinal)) return "error";
            if (notFound.Success)
            {
                string type = notFound.Groups["type"].Value;
                return notFound.Groups["what"].Value switch
                {
                    "table" => "error",
                    _ when type == "Instance" => "warning",
                    _ => "error",
                };
            }
            if (msg.Contains("could be nil", StringComparison.Ordinal)) return "warning";
            return "warning";
        }

        /// <summary>The checker's words plus what usually fixes it.</summary>
        private static string Explain(string kind, string msg)
        {
            var notFound = Regex.Match(msg, @"^Key '(?<key>[^']+)' not found in (?<what>external type|class|table) '(?<type>[^']+)'");
            if (notFound.Success)
            {
                string key = notFound.Groups["key"].Value, type = notFound.Groups["type"].Value;
                if (notFound.Groups["what"].Value == "table")
                    return $"'{type}' has no '{key}' — check the name (kit_reference modules lists the kit's APIs), or give the table a type such as {{ [string]: any }} if its keys are added at runtime.";
                if (type == "Instance")
                    return $"'{key}' is not on every Instance — check what kind of object it is first (if x:IsA(\"Model\") then …) or cast it (x :: Model).";
                return $"{type} has no '{key}' — this would error at runtime. Check the Roblox API (for example Players:GetPlayers(), not game:GetPlayers(); Model:GetBoundingBox(), not Part).";
            }
            if (msg.StartsWith("Unknown global", StringComparison.Ordinal)) return msg + " (a typo, or a local declared later or in another scope?)";
            return kind == "TypeError" || kind == "SyntaxError" ? msg : $"{kind}: {msg}";
        }

        private static async Task<string> RunAsync(LuauToolchain tools, string root, List<string> files, bool withSourcemap, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(tools.ExecutablePath)
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("analyze");
            psi.ArgumentList.Add("--platform=roblox");
            psi.ArgumentList.Add("--definitions=@roblox=" + tools.DefinitionsPath);
            psi.ArgumentList.Add("--formatter=plain");
            psi.ArgumentList.Add("--base-luaurc=strict.luaurc");
            if (withSourcemap) psi.ArgumentList.Add("--sourcemap=sourcemap.json");
            foreach (var f in files) psi.ArgumentList.Add(f);

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start luau-lsp");
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException($"the type checker took longer than {Timeout.TotalSeconds:0} s");
            }
            _ = await stderr;
            return await stdout;
        }

        private sealed class Node
        {
            public Node(string name, string className) { Name = name; ClassName = className; }
            public string Name { get; }
            public string ClassName { get; set; }
            public string? File { get; set; }
            public List<Node> Children { get; } = new();

            public Node Child(string name, string className)
            {
                var existing = Children.FirstOrDefault(c => c.Name == name);
                if (existing != null)
                {
                    // A folder that turns out to be a script (init.luau) takes the script's class.
                    if (className != "Folder" && existing.ClassName == "Folder") existing.ClassName = className;
                    return existing;
                }
                var node = new Node(name, className);
                Children.Add(node);
                return node;
            }

            public JObject ToJson()
            {
                var o = new JObject { ["name"] = Name, ["className"] = ClassName };
                if (File != null) o["filePaths"] = new JArray(File);
                o["children"] = new JArray(Children.Select(c => c.ToJson()));
                return o;
            }
        }
    }
}
